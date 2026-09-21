using System;
using System.Collections.Generic;
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

		foreach (BotStrategist commander in _commanders.Values)
		{
			commander.ServerTick(tick);
		}
	}

	/// <summary>
	/// This bot's intent for the tick. A peer that is not a bot — or a strategist
	/// bot, which has no body to drive — gets a neutral frame, which is what the
	/// server would have simulated for it anyway.
	/// </summary>
	public InputFrame Sample(int peerId, uint tick) =>
		_pilots.TryGetValue(peerId, out BotPilot pilot) ? pilot.Sample(tick) : InputFrame.Neutral(tick);

	public bool IsBot(int peerId) => _pilots.ContainsKey(peerId) || _commanders.ContainsKey(peerId);

	/// <summary>
	/// Gives up one bot's seat on <paramref name="team"/> now, rather than at the
	/// next reconcile. Called when somebody has asked for a side that is full, which
	/// with two computer strategists is the one moment where the backfill would
	/// otherwise make a person ask twice.
	/// </summary>
	public bool YieldSeat(Team team) =>
		team == Team.Strategist ? RemoveOne(_commanders.Keys) : RemoveOne(_pilots.Keys);

	/// <summary>
	/// Forgets a bot. Called by <see cref="PlayerManager"/> whenever a player is
	/// despawned, bot or not, so that the two rosters cannot drift apart.
	/// </summary>
	public void Release(int peerId)
	{
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

		while (_commanders.Count > want.Strategist && RemoveOne(_commanders.Keys))
		{
		}

		while (_pilots.Count > want.Ground && RemoveOne(_pilots.Keys))
		{
		}

		if (_commanders.Count < want.Strategist)
		{
			TryAdd(Team.Strategist);
		}
		else if (_pilots.Count < want.Ground)
		{
			TryAdd(Team.GroundForce);
		}
	}

	/// <summary>
	/// Counts the people on each side, and the roster slots that are neither theirs
	/// nor this director's.
	///
	/// A peer is a person unless this director is driving it. That is a stronger
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

			if (IsBot(player.PeerId))
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

	private bool TryAdd(Team team)
	{
		CombatManager combat = CombatManager.Instance;
		PlayerManager players = PlayerManager.Instance;
		if (combat == null || players == null)
		{
			return false;
		}

		int peerId = FreePeerId(players);
		if (peerId == 0)
		{
			return false;
		}

		fps_controller character = players.SpawnBot(peerId);
		if (character == null)
		{
			return false;
		}

		Team granted = combat.ServerAssignTeam(peerId, team);
		if (granted != team)
		{
			// The cap refused it. The policy already accounts for the cap, so this is
			// a disagreement worth hearing about rather than one to paper over.
			GD.PushWarning($"[bots] {BotRoster.NameOf(peerId)} asked for {team} and got {granted}");
			players.DespawnBot(peerId);
			return false;
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
		return true;
	}

	/// <summary>Takes the highest-numbered bot off the given roster. Last in, first out.</summary>
	private bool RemoveOne(IEnumerable<int> peerIds)
	{
		int last = 0;
		foreach (int peerId in peerIds)
		{
			if (peerId > last)
			{
				last = peerId;
			}
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
	/// Arms a fresh bot. Rifles and DMRs only: the launcher's blast damages players
	/// on both sides (<c>CombatManager.Explode</c>, which spares friendly *units*
	/// and nobody else), and a bot that occasionally kills the person it spawned to
	/// help is worse than no bot.
	/// </summary>
	private static void Equip(CombatManager combat, int peerId)
	{
		PlayerCombat player = combat.Find(peerId);
		if (player == null)
		{
			return;
		}

		byte large = (BotRoster.SlotOf(peerId) & 1) == 0 ? WeaponCatalog.Rifle : WeaponCatalog.Dmr;
		player.PendingLoadout = new LoadoutSelection
		{
			Melee = WeaponCatalog.Hammer,
			Sidearm = WeaponCatalog.Pistol,
			Large = large,
		};
		player.Equip(player.PendingLoadout);
	}
}
