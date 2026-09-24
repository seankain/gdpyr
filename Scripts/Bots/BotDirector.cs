using System;
using System.Collections.Generic;
using Gdpyr.Agent;
using Gdpyr.Core;
using Gdpyr.Fps;
using Gdpyr.Match;
using Gdpyr.Net;
using Gdpyr.Sim;
using Godot;

namespace Gdpyr.Bots;

/// <summary>
/// Keeps both sides filled with computer players
/// (docs/IMPLEMENTATION_PLAN.md §M3.5).
///
/// It is the only thing in the game that knows bots exist as a category. It
/// decides how many each side should have, asks <see cref="PlayerManager"/> to
/// spawn or despawn them, and hands out the per-tick <see cref="InputFrame"/> for
/// each one; everything downstream sees players.
///
/// The policy it executes is <see cref="BotFillPolicy"/> and is engine-free, so
/// the interesting question — what happens to the bots as people arrive and leave
/// — is answered by a test. What is left here is the doing of it.
///
/// Server-only, and never ticked by itself: <see cref="PlayerManager"/> drives it
/// at the top of the server tick, before the roster loop that reads its frames,
/// like every other manager in this codebase.
/// </summary>
public sealed class BotDirector
{
	/// <summary>
	/// Ticks between roster decisions. Half a second: fast enough that a bot has
	/// given up its seat before the human who wants it has finished loading, slow
	/// enough that it is not a per-tick scan of the roster.
	/// </summary>
	private const int ReconcileIntervalTicks = SimConfig.TickRate / 2;

	private readonly Dictionary<int, BotPilot> _pilots = new();
	private readonly Dictionary<int, BotStrategist> _commanders = new();

	private uint _nextReconcileTick;

	public BotDirector(GameModeDefinition gameMode, LaunchOptions options)
	{
		int ground = options?.GroundBots ?? gameMode?.BotGroundForce ?? 0;
		int strategists = options?.StrategistBots ?? gameMode?.BotStrategists ?? 0;

		GroundTarget = Math.Clamp(ground, 0, BotRoster.MaxBots);
		StrategistTarget = Math.Clamp(strategists, 0, TeamService.MaxStrategists);

		GD.Print(Enabled
			? $"[bots] backfilling to {GroundTarget} ground force and {StrategistTarget} strategist(s)"
				+ " while anyone is connected"
			: "[bots] disabled");
	}

	/// <summary>Ground-force players wanted in total, humans included.</summary>
	public int GroundTarget { get; }

	/// <summary>Strategists wanted in total, humans included.</summary>
	public int StrategistTarget { get; }

	public bool Enabled => GroundTarget > 0 || StrategistTarget > 0;

	public int GroundBots => _pilots.Count;

	public int StrategistBots => _commanders.Count;

	public int Count => _pilots.Count + _commanders.Count;

	/// <summary>
	/// One server tick: keep the roster at its target, then let each computer
	/// strategist think. Ground bots think when they are asked for a frame, which is
	/// what keeps their decision on the same tick as the character that acts on it.
	/// </summary>
	public void ServerTick(uint tick)
	{
		if (Enabled && tick >= _nextReconcileTick)
		{
			_nextReconcileTick = tick + (uint)ReconcileIntervalTicks;
			Reconcile();
		}

		AgentServer agents = AgentServer.Instance;
		foreach (BotStrategist commander in _commanders.Values)
		{
			// The strategist half of the one branch the agent API adds to the game
			// (docs/AGENT_API.md §2, §7.4): an attached seat's commands run instead of
			// this bot's decision, and a seat whose policy has gone quiet falls back to
			// the bot on this line.
			if (agents != null && agents.TryCommand(commander.PeerId, tick))
			{
				continue;
			}

			commander.ServerTick(tick);
		}
	}

	/// <summary>
	/// This bot's intent for the tick. A peer that is not a bot — or a strategist
	/// bot, which has no body to drive — gets a neutral frame, which is what the
	/// server would have simulated for it anyway.
	/// </summary>
	public InputFrame Sample(int peerId, uint tick)
	{
		// The one branch the agent API adds to the game (docs/AGENT_API.md §2). A
		// seat whose policy has gone quiet falls back to its pilot on this line, and
		// nothing downstream — PlayerManager, CombatManager, any client — learns a
		// new concept.
		if (AgentServer.Instance is { } agents && agents.TrySample(peerId, tick, out InputFrame frame))
		{
			return frame;
		}

		return _pilots.TryGetValue(peerId, out BotPilot pilot) ? pilot.Sample(tick) : InputFrame.Neutral(tick);
	}

	/// <summary>
	/// Puts one more bot on <paramref name="team"/> now and hands back its peer id,
	/// or 0 when the roster has no room. Called by the agent API when a policy asks
	/// for a seat and the side has none free (docs/AGENT_API.md §2.2).
	/// </summary>
	public int SpawnSeat(Team team)
	{
		PlayerManager players = PlayerManager.Instance;
		if (players == null || players.PlayerCount >= SnapshotCodec.MaxPlayers)
		{
			return 0;
		}

		return TryAdd(team);
	}

	public bool IsBot(int peerId) => _pilots.ContainsKey(peerId) || _commanders.ContainsKey(peerId);

	/// <summary>
	/// Gives up one bot's seat on <paramref name="team"/> now, rather than at the
	/// next reconcile. Called when somebody has asked for a side that is full, which
	/// with two computer strategists is the one moment where the backfill would
	/// otherwise make a person ask twice.
	/// </summary>
	public bool YieldSeat(Team team) =>
		team == Team.Strategist
			? RemoveOne(_commanders.Keys, evictAttached: true)
			: RemoveOne(_pilots.Keys, evictAttached: true);

	/// <summary>
	/// Forgets a bot. Called by <see cref="PlayerManager"/> whenever a player is
	/// despawned, bot or not, so that the two rosters cannot drift apart.
	/// </summary>
	public void Release(int peerId)
	{
		// A lease on a seat that no longer exists would leave a session holding a
		// body nobody is simulating (docs/AGENT_API.md §2.1).
		AgentServer.Instance?.ReleaseSeat(peerId, "despawned");

		if (_pilots.Remove(peerId, out BotPilot pilot))
		{
			pilot.Dispose();
		}

		_commanders.Remove(peerId);
	}

	// ---- roster ------------------------------------------------------------

	/// <summary>
	/// Brings the bot roster to what <see cref="BotFillPolicy"/> asks for.
	///
	/// Removals happen all at once and additions one at a time. A seat a person
	/// wants has to be free now; a seat nobody wants can fill over the next few
	/// seconds, which also spreads the cost of building six characters over six
	/// ticks instead of one.
	/// </summary>
	private void Reconcile()
	{
		CombatManager combat = CombatManager.Instance;
		PlayerManager players = PlayerManager.Instance;
		if (combat == null || players == null)
		{
			return;
		}

		Census(combat, out int groundHumans, out int strategistHumans, out int reserved);

		BotFill want = BotFillPolicy.Plan(new BotDemand(GroundTarget, StrategistTarget, groundHumans,
			strategistHumans, SnapshotCodec.MaxPlayers - reserved, TeamService.MaxStrategists));

		// Seats an external policy is sitting in are counted as participants in the
		// census, not as backfill, so they come off both sides of the comparison:
		// the fill policy is being asked how many *bots* are wanted beside them.
		// RemoveOne never takes an attached seat, so these two are constant across
		// the loops below (docs/AGENT_API.md §2.1).
		int attachedGround = CountAttached(_pilots.Keys);
		int attachedStrategists = CountAttached(_commanders.Keys);

		while (_commanders.Count - attachedStrategists > want.Strategist && RemoveOne(_commanders.Keys))
		{
		}

		while (_pilots.Count - attachedGround > want.Ground && RemoveOne(_pilots.Keys))
		{
		}

		if (_commanders.Count - attachedStrategists < want.Strategist)
		{
			TryAdd(Team.Strategist);
		}
		else if (_pilots.Count - attachedGround < want.Ground)
		{
			TryAdd(Team.GroundForce);
		}
	}

	/// <summary>How many of these seats an external policy holds.</summary>
	private static int CountAttached(IEnumerable<int> peerIds)
	{
		AgentServer agents = AgentServer.Instance;
		if (agents == null)
		{
			return 0;
		}

		int count = 0;
		foreach (int peerId in peerIds)
		{
			if (agents.IsAttached(peerId))
			{
				count++;
			}
		}

		return count;
	}

	/// <summary>
	/// Counts the people on each side, and the roster slots that are neither theirs
	/// nor this director's.
	///
	/// A peer is a person unless this director is driving it, and a seat an external
	/// policy has attached to counts as a person too. That is a stronger
	/// test than <see cref="BotRoster.IsBot"/> alone, and it is the one that makes
	/// the bot id band safe: Godot draws client ids from the whole positive range,
	/// so one could in principle land inside the band, and when it does it is
	/// counted as the person it is.
	///
	/// <paramref name="reserved"/> comes off the seat budget rather than the head
	/// count, because a slot that is neither a bot nor a person is still a slot the
	/// snapshot has to carry.
	/// </summary>
	private void Census(CombatManager combat, out int ground, out int strategists, out int reserved)
	{
		ground = 0;
		strategists = 0;
		reserved = 0;

		NetworkManager net = NetworkManager.Instance;
		bool hostPlays = net?.HasLocalPlayer ?? true;
		int hostPeerId = net?.LocalPeerId ?? 1;

		for (int i = 0; i < combat.PlayerCount; i++)
		{
			PlayerCombat player = combat.PlayerAt(i);
			if (player == null)
			{
				continue;
			}

			// A seat an external policy is driving is somebody playing, not backfill.
			// Without this an agent-only server — which is exactly what a training run
			// is — would see nobody connected, fill neither side, and hand the policy
			// an empty map to learn on (docs/AGENT_API.md §2).
			if (IsBot(player.PeerId) && !(AgentServer.Instance?.IsAttached(player.PeerId) ?? false))
			{
				continue;
			}

			// A dedicated server no longer spawns a character for itself, but if one
			// ever appears again it is a body nobody is behind and must not be counted
			// as somebody who wants to play.
			if (!hostPlays && player.PeerId == hostPeerId)
			{
				reserved++;
				continue;
			}

			if (player.Team == Team.Strategist)
			{
				strategists++;
			}
			else
			{
				ground++;
			}
		}
	}

	/// <summary>Adds one bot on <paramref name="team"/>. Returns its peer id, or 0.</summary>
	private int TryAdd(Team team)
	{
		CombatManager combat = CombatManager.Instance;
		PlayerManager players = PlayerManager.Instance;
		if (combat == null || players == null)
		{
			return 0;
		}

		int peerId = FreePeerId(players);
		if (peerId == 0)
		{
			return 0;
		}

		fps_controller character = players.SpawnBot(peerId);
		if (character == null)
		{
			return 0;
		}

		Team granted = combat.ServerAssignTeam(peerId, team);
		if (granted != team)
		{
			// The cap refused it. The policy already accounts for the cap, so this is
			// a disagreement worth hearing about rather than one to paper over.
			GD.PushWarning($"[bots] {BotRoster.NameOf(peerId)} asked for {team} and got {granted}");
			players.DespawnBot(peerId);
			return 0;
		}

		if (team == Team.Strategist)
		{
			_commanders[peerId] = new BotStrategist(peerId, StrategistTraits.Default);
		}
		else
		{
			Equip(combat, peerId);
			_pilots[peerId] = new BotPilot(peerId, character, BotTraits.Default);
		}

		GD.Print($"[bots] {BotRoster.NameOf(peerId)} joined as {team}");
		return peerId;
	}

	/// <summary>
	/// Takes the highest-numbered bot off the given roster. Last in, first out.
	///
	/// A seat an external policy is sitting in is not a candidate unless
	/// <paramref name="evictAttached"/> says so, which only <see cref="YieldSeat"/>
	/// does. The backfill's rule is that a bot holds a slot while nobody else wants
	/// it, and a policy is somebody who wants it — otherwise an agent-only server,
	/// which is exactly what a training run is, would have its seat taken away
	/// every half second because the fill policy sees no people (docs/AGENT_API.md
	/// §2.1). A person still outranks a policy: that is what the flag is for.
	/// </summary>
	private bool RemoveOne(IEnumerable<int> peerIds, bool evictAttached = false)
	{
		int last = 0;
		int lastAttached = 0;
		AgentServer agents = AgentServer.Instance;

		foreach (int peerId in peerIds)
		{
			if (agents != null && agents.IsAttached(peerId))
			{
				lastAttached = Math.Max(lastAttached, peerId);
				continue;
			}

			last = Math.Max(last, peerId);
		}

		if (last == 0 && evictAttached)
		{
			last = lastAttached;
		}

		if (last == 0)
		{
			return false;
		}

		GD.Print($"[bots] {BotRoster.NameOf(last)} left");

		// Despawning calls back into Release, which is what actually drops the pilot.
		PlayerManager.Instance?.DespawnBot(last);
		Release(last);
		return true;
	}

	/// <summary>
	/// The lowest bot id nobody is using. Checks the whole roster rather than only
	/// this director's, so a client that happened to be given an id in the band
	/// cannot be collided with.
	/// </summary>
	private static int FreePeerId(PlayerManager players)
	{
		for (int slot = 0; slot < BotRoster.MaxBots; slot++)
		{
			int peerId = BotRoster.PeerIdFor(slot);
			if (peerId != 0 && !players.HasPlayer(peerId))
			{
				return peerId;
			}
		}

		return 0;
	}

	/// <summary>
	/// Arms a fresh bot: a rifle, a DMR or a launcher, by roster slot, so six bots
	/// carry two of each.
	///
	/// Launchers were left out until armour arrived: the blast damages players on
	/// both sides (<c>CombatManager.Explode</c>, which spares friendly *units* and
	/// nobody else), and a bot that occasionally kills the person it spawned to help
	/// is worse than no bot. Now a tank, a pillbox and a sniper tower take nothing
	/// from a bullet (docs/NETCODE.md §10.6), and a ground force of bots with no
	/// launcher could not touch any of them. What makes one safe to carry is the
	/// pilot: it holds a launcher's fire while a teammate, or the bot itself, is
	/// inside the blast (<c>BotPilot.BlastEndangersFriends</c>).
	/// </summary>
	private static void Equip(CombatManager combat, int peerId)
	{
		PlayerCombat player = combat.Find(peerId);
		if (player == null)
		{
			return;
		}

		int slot = BotRoster.SlotOf(peerId);
		byte large = slot % 3 == 2 ? WeaponCatalog.Launcher
			: (slot & 1) == 0 ? WeaponCatalog.Rifle
			: WeaponCatalog.Dmr;
		player.PendingLoadout = new LoadoutSelection
		{
			Melee = WeaponCatalog.Hammer,
			Sidearm = WeaponCatalog.Pistol,
			Large = large,
		};
		player.Equip(player.PendingLoadout);
	}
}
