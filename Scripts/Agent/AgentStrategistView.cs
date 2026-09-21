using System;
using Gdpyr.Fps;
using Gdpyr.Match;
using Gdpyr.Rts;
using Gdpyr.Sim;
using Gdpyr.Sim.Agent;
using Godot;

namespace Gdpyr.Agent;

/// <summary>
/// Builds the strategist observation out of server state
/// (docs/AGENT_API.md §6.2).
///
/// The engine half of the encoder: it looks the world up and fills the view
/// structs <see cref="AgentStrategistObservation"/> turns into floats.
///
/// **The fog is the game's own.** Where a ground policy is behind
/// <see cref="Gdpyr.Bots.GroundSensor"/> — the scan a ground bot acquires
/// through — a strategist policy is behind
/// <see cref="VisibilityService"/>, which is the same call that decides what a
/// human strategist's snapshot may carry (<c>IsVisibleTo</c>) and what its HUD
/// may draw a ghost from (<c>TryContact</c>). A change to what a strategist may
/// know changes what the policy may know on the same commit, because there is one
/// filter and not two.
///
/// <c>--agent-omniscient</c> is the one exception, and it is stamped into every
/// observation frame produced under it.
/// </summary>
public sealed class AgentStrategistView
{
	private readonly AgentUnitView[] _units = new AgentUnitView[AgentStrategistObservation.MaxUnits];
	private readonly AgentBarracksView[] _barracks =
		new AgentBarracksView[AgentStrategistObservation.MaxBarracks];
	private readonly AgentNodeView[] _nodes = new AgentNodeView[AgentStrategistObservation.MaxNodes];

	private readonly AgentStrategistContactView[] _candidates =
		new AgentStrategistContactView[AgentStrategistObservation.MaxContacts];

	private readonly AgentStrategistContactView[] _contacts =
		new AgentStrategistContactView[AgentStrategistObservation.MaxContacts];

	/// <summary>
	/// Fills <paramref name="into"/> with one observation for
	/// <paramref name="peerId"/>. Returns false when the seat is not a strategist
	/// this tick, which is what a policy sees if the roster moved it.
	/// </summary>
	public bool Encode(int peerId, uint tick, int stepMul, in AgentLimits limits, Span<float> into)
	{
		CombatManager combat = CombatManager.Instance;
		PlayerCombat player = combat?.Find(peerId);
		UnitManager units = UnitManager.Instance;
		if (combat == null || player == null || units == null || player.Team != Team.Strategist)
		{
			return false;
		}

		AgentStrategistRoundView round = Round(combat, units, tick, stepMul);
		int liveUnits = Units(units, player.Team);
		int barracks = Barracks(units, player.Team, tick);
		int nodes = Nodes(combat);
		int contacts = Contacts(combat, peerId, tick, limits);

		return AgentStrategistObservation.Encode(round, _units.AsSpan(0, liveUnits),
			_barracks.AsSpan(0, barracks), _nodes.AsSpan(0, nodes), _contacts.AsSpan(0, contacts), into)
			== AgentStrategistObservation.Floats;
	}

	// ---- blocks ------------------------------------------------------------

	private static AgentStrategistRoundView Round(CombatManager combat, UnitManager units, uint tick,
		int stepMul)
	{
		MatchState match = combat.Match;
		float total = (combat.GameMode?.RoundDurationMinutes ?? 20) * 60f;

		return new AgentStrategistRoundView
		{
			Phase = (byte)match.Phase,
			SecondsRemaining = match.SecondsRemaining(tick),
			SecondsTotal = total,
			Points = match.StrategistPoints,
			IncomePaid = combat.Economy.IncomePaid,
			GroundTickets = match.GroundTickets,
			StartingGroundTickets = match.StartingGroundTickets,
			LiveUnits = units.LiveUnitCount,
			QueuedUnits = units.QueuedUnits,
			UnitsLost = units.UnitsLost,
			Tick = tick,
			StepMul = stepMul,
		};
	}

	/// <summary>
	/// The army, in registry order — which is the order <c>UnitManager</c> walks and
	/// therefore the order a command's unit ids were read out of. A corpse is
	/// carried until the reaper takes it, flagged dead, because a policy that saw a
	/// unit vanish a tick before the <c>unit_lost</c> event would have to infer the
	/// loss from a hole in a tensor.
	/// </summary>
	private int Units(UnitManager units, Team team)
	{
		int count = 0;
		for (int i = 0; i < units.SlotCount && count < _units.Length; i++)
		{
			Unit unit = units.UnitAt(i);
			if (unit == null || unit.Team != team)
			{
				continue;
			}

			float maxHealth = unit.Definition?.MaxHealth ?? 100f;
			bool hasTarget = unit.TargetOwnerId != OwnerId.None;

			_units[count++] = new AgentUnitView
			{
				Alive = unit.IsAlive,
				Tier = unit.DefinitionId,
				X = unit.GlobalPosition.X,
				Z = unit.GlobalPosition.Z,
				HealthFraction = maxHealth > 0f ? unit.Health / maxHealth : 0f,
				Order = (byte)unit.Order.Kind,
				HasTarget = hasTarget,
				TargetDistanceMeters = unit.Order.IsStanding
					? unit.GlobalPosition.DistanceTo(unit.Order.Target)
					: 0f,
			};
		}

		return count;
	}

	/// <summary>
	/// The doors, by map index — the same index a <c>build</c> command names, so a
	/// policy reading slot 1 and writing <c>barracks: 1</c> is addressing one
	/// building (docs/AGENT_API.md §7.4).
	///
	/// A barracks the seat does not own keeps its slot and is left zeroed: the index
	/// has to stay aligned, and a queue the seat is not allowed to spend from is not
	/// a queue it is allowed to read.
	/// </summary>
	private int Barracks(UnitManager units, Team team, uint tick)
	{
		int carried = Math.Min(units.BarracksCount, _barracks.Length);
		for (int i = 0; i < carried; i++)
		{
			Barracks door = units.BarracksAt(i);
			if (door == null || door.Team != team)
			{
				_barracks[i] = default;
				continue;
			}

			_barracks[i] = new AgentBarracksView
			{
				X = door.GlobalPosition.X,
				Z = door.GlobalPosition.Z,
				QueueDepth = door.Queue.Count,
				HeadTier = door.Queue.Head,
				HeadProgress = door.Queue.Progress(tick),
				RallyX = door.RallyPoint.X,
				RallyZ = door.RallyPoint.Z,
			};
		}

		return carried;
	}

	/// <summary>
	/// The nodes. Not fogged, and deliberately: a resource node is map geometry on
	/// a screen both sides are looking at, and who holds it is the thing the capture
	/// meter is for (docs/IMPLEMENTATION_PLAN.md §M5).
	/// </summary>
	private int Nodes(CombatManager combat)
	{
		EconomyService economy = combat.Economy;
		int carried = Math.Min(economy.NodeCount, _nodes.Length);

		for (int i = 0; i < carried; i++)
		{
			ResourceNode node = economy.NodeAt(i);
			if (node == null)
			{
				_nodes[i] = default;
				continue;
			}

			_nodes[i] = new AgentNodeView
			{
				X = node.GlobalPosition.X,
				Z = node.GlobalPosition.Z,
				Owner = (byte)node.Capture.Owner,
				Contested = node.Capture.Contested,
				CaptureProgress = node.Capture.Progress(node.Rules),
				IncomePaid = node.Capture.Paid,
			};
		}

		return carried;
	}

	/// <summary>
	/// Everybody the seat may know about: its own side, and the ground-force players
	/// the fog admits — live where a sensor has one now, a ghost where one was seen
	/// and lost.
	///
	/// This is exactly what a human strategist's client can draw. The live records
	/// are the ones <see cref="VisibilityService.IsVisibleTo"/> writes into their
	/// snapshot; the ghosts are what <see cref="VisibilityService.TryContact"/>
	/// hands their HUD (docs/NETCODE.md §6.2). A peer nobody has ever laid eyes on
	/// is in neither, and is therefore not in the vector.
	/// </summary>
	private int Contacts(CombatManager combat, int viewerPeerId, uint tick, in AgentLimits limits)
	{
		VisibilityService fog = combat.Visibility;
		int found = 0;

		for (int i = 0; i < combat.PlayerCount && found < _candidates.Length; i++)
		{
			PlayerCombat player = combat.PlayerAt(i);
			if (player?.Character == null)
			{
				continue;
			}

			var contact = new AgentStrategistContactView
			{
				IsPlayer = true,
				Team = (byte)player.Team,
			};

			if (player.Team == Team.Strategist || player.PeerId == viewerPeerId)
			{
				// Its own side, which a strategist's packet always carries.
				contact.Known = true;
				contact.Visible = true;
				contact.X = player.Character.SimPosition.X;
				contact.Z = player.Character.SimPosition.Z;
			}
			else if (limits.Omniscient)
			{
				// The whole field, and stamped as cheating wherever it appears
				// (docs/AGENT_API.md §6.4).
				contact.Known = true;
				contact.Visible = true;
				contact.X = player.Character.SimPosition.X;
				contact.Z = player.Character.SimPosition.Z;
			}
			else if (fog != null && fog.TryContact(player.PeerId, tick, out Vector3 at, out float age))
			{
				contact.Known = true;
				contact.Visible = fog.IsVisible(player.PeerId);
				contact.X = at.X;
				contact.Z = at.Z;
				contact.TicksSinceSeen = (int)MathF.Min(age, int.MaxValue);
			}
			else
			{
				continue;
			}

			_candidates[found++] = contact;
		}

		return AgentStrategistObservation.SelectContacts(_candidates.AsSpan(0, found), _contacts);
	}
}
