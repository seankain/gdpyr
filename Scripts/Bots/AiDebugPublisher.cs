using System;
using System.Collections.Generic;
using Gdpyr.Fps;
using Gdpyr.Match;
using Gdpyr.Rts;
using Gdpyr.Sim;
using Gdpyr.Sim.AiDebug;
using Gdpyr.Sim.Htn;
using Godot;

namespace Gdpyr.Bots;

/// <summary>
/// Reads the authority's planners into what a spectator is sent (docs/AI_DEBUG.md §6):
/// a label per planning agent, each computer strategist's commander squad by squad,
/// and the full plan of whatever the spectator has selected.
///
/// Read-only over the live contexts, and outside the planners' tick: nothing here
/// can change what a bot decides. It is called at most ten times a second, and only
/// while a spectator is watching, so it builds fresh objects rather than pooling
/// them — the planner's own path stays allocation-free (docs/HTN_BOTS.md §4.2 rule 3),
/// and this is not on it.
/// </summary>
public sealed class AiDebugPublisher
{
	private readonly BotDirector _bots;
	private readonly List<BotPilot> _pilots = new();
	private readonly List<BotStrategist> _strategists = new();
	private readonly ushort[] _members = new ushort[SimConfig.MaxUnits];

	public AiDebugPublisher(BotDirector bots) => _bots = bots ?? throw new ArgumentNullException(nameof(bots));

	/// <summary>One frame for one spectator's <paramref name="watch"/>.</summary>
	public AiDebugFrame Build(uint tick, AiWatch watch)
	{
		var frame = new AiDebugFrame
		{
			Tick = tick,
			BotAi = _bots.Ai,
			GroundSignature = _bots.GroundPlanning?.Map.Signature ?? 0u,
			UnitSignature = UnitManager.Instance?.Planning?.Map.Signature ?? 0u,
			SquadSignature = _bots.CommanderPlanning?.Map.Signature ?? 0u,
		};

		if (watch == null)
		{
			return frame;
		}

		if (watch.Wants(AiWatchFlags.Labels))
		{
			AddGroundLabels(frame);
			AddUnitLabels(frame);
		}

		if (watch.Wants(AiWatchFlags.Commander))
		{
			AddCommanders(frame, watch.CommanderPeerId);
		}

		foreach (AiEntityRef target in watch.Selected)
		{
			if (Inspect(target) is { } inspect)
			{
				frame.Inspected.Add(inspect);
			}
		}

		return frame;
	}

	/// <summary>Everything about one agent, or null when it is not on the field.</summary>
	public AiInspect Inspect(AiEntityRef target) => target.Kind switch
	{
		AiEntityKind.Player => InspectPlayer(target.Id),
		AiEntityKind.Unit => InspectUnit((ushort)Math.Clamp(target.Id, 0, ushort.MaxValue)),
		AiEntityKind.Squad => InspectSquad(target.Id, target.Slot),
		_ => null,
	};

	/// <summary>The computer strategists, for the console to name and the panel to list.</summary>
	public void CollectStrategists(List<int> peerIds)
	{
		peerIds.Clear();
		_bots.CollectStrategists(_strategists);
		foreach (BotStrategist strategist in _strategists)
		{
			peerIds.Add(strategist.PeerId);
		}
	}

	// ---- labels ------------------------------------------------------------

	private void AddGroundLabels(AiDebugFrame frame)
	{
		_bots.CollectPilots(_pilots);
		foreach (BotPilot pilot in _pilots)
		{
			GroundContext plan = pilot.Plan;
			if (plan == null)
			{
				continue;
			}

			GroundIntent intent = plan.Intent;
			frame.Labels.Add(new AiLabel
			{
				Ref = AiEntityRef.Player(pilot.PeerId),
				Goal = (byte)intent.Goal,
				Detail = (byte)plan.Role,
				Flags = (intent.HasPoint ? AiLabelFlags.HasPoint : AiLabelFlags.None)
					| (plan.Is(GroundFact.Sweep) ? AiLabelFlags.Sweeping : AiLabelFlags.None)
					| (pilot.TargetOwnerId != OwnerId.None ? AiLabelFlags.Engaging : AiLabelFlags.None),
				Point = intent.Point,
				TargetOwnerId = pilot.TargetOwnerId,
			});
		}
	}

	private static void AddUnitLabels(AiDebugFrame frame)
	{
		UnitManager units = UnitManager.Instance;
		if (units == null)
		{
			return;
		}

		for (int i = 0; i < units.SlotCount && frame.Labels.Count < AiDebugCodec.MaxLabels; i++)
		{
			Unit unit = units.UnitAt(i);
			if (unit is not { IsAlive: true, Plan: { } plan })
			{
				continue;
			}

			bool hasPoint = TryIntentPoint(unit, plan, out Vector3 point);
			frame.Labels.Add(new AiLabel
			{
				Ref = AiEntityRef.Unit(unit.UnitId),
				Goal = (byte)plan.Intent.Goal,
				Detail = (byte)unit.Order.Kind,
				Flags = (hasPoint ? AiLabelFlags.HasPoint : AiLabelFlags.None)
					| (plan.Waiting && plan.Intent.Goal == UnitGoal.Advance ? AiLabelFlags.Waiting : AiLabelFlags.None)
					| (unit.TargetOwnerId != OwnerId.None ? AiLabelFlags.Engaging : AiLabelFlags.None),
				Point = point,
				TargetOwnerId = unit.TargetOwnerId,
			});
		}
	}

	/// <summary>
	/// Where the unit's running task is taking it: the intent's point, or for a task that
	/// follows the order, where the order points. Nothing for one that holds.
	/// </summary>
	private static bool TryIntentPoint(Unit unit, UnitContext plan, out Vector3 point)
	{
		switch (plan.Intent.Move)
		{
			case UnitMove.Point:
			case UnitMove.Target:
				point = plan.Intent.Point;
				return true;

			case UnitMove.Order when unit.Order.IsStanding:
				point = unit.Order.Kind == OrderKind.Patrol && unit.Order.Returning ? unit.Order.Anchor : unit.Order.Target;
				return true;

			default:
				point = default;
				return false;
		}
	}

	// ---- the commander ------------------------------------------------------

	private void AddCommanders(AiDebugFrame frame, int onlyPeerId)
	{
		HtnDomainMap map = _bots.CommanderPlanning?.Map;
		_bots.CollectStrategists(_strategists);
		foreach (BotStrategist strategist in _strategists)
		{
			Commander commander = strategist.Commander;
			if (commander == null || (onlyPeerId != 0 && strategist.PeerId != onlyPeerId)
				|| frame.Commanders.Count >= AiDebugCodec.MaxCommanders)
			{
				continue;
			}

			var summary = new AiCommander
			{
				PeerId = strategist.PeerId,
				BuyTimeZone = commander.BuyTimeZone,
				ReconWanted = commander.ReconWanted,
			};

			for (int s = 0; s < commander.Capacity; s++)
			{
				if (commander.SquadAt(s).Active)
				{
					summary.Squads.Add(Squad(commander, s, map));
				}
			}

			frame.Commanders.Add(summary);
		}
	}

	private AiSquad Squad(Commander commander, int slot, HtnDomainMap map)
	{
		CommandSquad squad = commander.SquadAt(slot);
		CommandIntent intent = commander.IntentOf(slot);
		CommandContext context = commander.ContextOf(slot);

		var row = new AiSquad
		{
			Slot = (byte)slot,
			Role = (byte)squad.Role,
			Task = (byte)squad.Task,
			Target = (byte)squad.Target,
			Goal = (byte)intent.Goal,
			Order = (byte)intent.Order,
			Phase = (byte)intent.Phase,
			Flags = (squad.Ready ? AiSquadFlags.Ready : AiSquadFlags.None)
				| (squad.Depleted ? AiSquadFlags.Depleted : AiSquadFlags.None)
				| (squad.Healable ? AiSquadFlags.Healable : AiSquadFlags.None)
				| (squad.FallingBack ? AiSquadFlags.FallingBack : AiSquadFlags.None)
				| (squad.Short ? AiSquadFlags.Short : AiSquadFlags.None)
				| (squad.Engaging > 0 ? AiSquadFlags.Engaged : AiSquadFlags.None),
			Zone = squad.Zone,
			TargetOwnerId = squad.TargetOwnerId,
			Strength = squad.Strength,
			FullStrength = squad.FullStrength,
			StrengthAtFormation = squad.StrengthAtFormation,
			Centroid = squad.Centroid,
			TaskPoint = squad.TaskPoint,
			StagingPoint = squad.StagingPoint,
			FallbackPoint = squad.FallbackPoint,
			OrderPoint = intent.Point,
		};

		int members = commander.MembersOf(slot, _members);
		for (int i = 0; i < members; i++)
		{
			row.Members.Add(_members[i]);
		}

		HtnTracer.Capture(map, context, context?.History, row.Trace);
		return row;
	}

	// ---- inspection ---------------------------------------------------------

	private AiInspect InspectPlayer(int peerId)
	{
		PlayerCombat combat = CombatManager.Instance?.Find(peerId);
		if (combat == null)
		{
			return null;
		}

		var inspect = new AiInspect { Ref = AiEntityRef.Player(peerId) };
		inspect.Add(AiValueKey.Health, combat.Health);
		inspect.Add(AiValueKey.Team, (int)combat.Team);

		BotPilot pilot = _bots.PilotOf(peerId);
		GroundContext plan = pilot?.Plan;
		if (plan == null || (Agent.AgentServer.Instance?.IsAttached(peerId) ?? false))
		{
			// A person, an external policy's seat, a strategist, or a legacy bot: there
			// is no plan here to show, and the panel says which.
			inspect.Add(AiValueKey.Person, BotRoster.IsBot(peerId) ? 0 : 1);
			return inspect;
		}

		inspect.Planning = true;
		inspect.Goal = (byte)plan.Intent.Goal;
		HtnTracer.Capture(_bots.GroundPlanning.Map, plan, plan.History, inspect.Trace);

		GroundIntent intent = plan.Intent;
		if (intent.HasPoint)
		{
			inspect.Add(AiMarkerKey.Intent, intent.Point);
		}

		if (plan.Is(GroundFact.InsideDefences))
		{
			inspect.Add(AiMarkerKey.Exit, plan.ExitPoint);
		}

		inspect.Add(AiMarkerKey.Fallback, plan.FallbackPoint);

		if (plan.HasLocker && plan.Role == GroundRole.LockerRunner)
		{
			inspect.Add(AiMarkerKey.Locker, plan.LockerPoint);
		}

		if (plan.Get(GroundFact.Contact) == (byte)ContactLevel.Ghost)
		{
			inspect.Add(AiMarkerKey.Ghost, plan.GhostPoint);
		}

		if (plan.HasArmourPoint)
		{
			inspect.Add(AiMarkerKey.Armour, plan.ArmourPoint);
		}

		if (plan.HasZone)
		{
			inspect.Add(AiMarkerKey.Zone, plan.ZonePoint);
		}

		if (plan.Is(GroundFact.Sweep))
		{
			inspect.Add(AiMarkerKey.Sweep, plan.SweepPoint);
		}

		if (plan.HasBuddy)
		{
			inspect.Add(AiMarkerKey.Buddy, plan.BuddyPoint);
		}

		inspect.Add(AiValueKey.Role, (int)plan.Role);
		inspect.Add(AiValueKey.Move, (int)intent.Move);
		inspect.Add(AiValueKey.Target, pilot.TargetOwnerId);
		inspect.Add(AiValueKey.Focus, plan.FocusOwnerId);
		inspect.Add(AiValueKey.FailedTrips, plan.FailedLockerTrips);
		return inspect;
	}

	private AiInspect InspectUnit(ushort unitId)
	{
		Unit unit = UnitManager.Instance?.Find(unitId);
		if (unit == null)
		{
			return null;
		}

		var inspect = new AiInspect { Ref = AiEntityRef.Unit(unitId) };

		// In percent: the snapshot's HealthPercent is a byte scaled to 255, not to 100.
		float maxHealth = unit.Definition?.MaxHealth ?? 100f;
		inspect.Add(AiValueKey.Health, maxHealth > 0f ? Mathf.RoundToInt(100f * unit.Health / maxHealth) : 0);
		inspect.Add(AiValueKey.Order, (int)unit.Order.Kind);
		inspect.Add(AiValueKey.Issuer, unit.OrderIssuer);
		inspect.Add(AiValueKey.Target, unit.TargetOwnerId);

		if (unit.Order.IsStanding)
		{
			inspect.Add(AiMarkerKey.Order, unit.Order.Target);
			if (unit.Order.Kind is OrderKind.Patrol or OrderKind.Defend)
			{
				inspect.Add(AiMarkerKey.Anchor, unit.Order.Anchor);
			}
		}

		_bots.CollectStrategists(_strategists);
		foreach (BotStrategist strategist in _strategists)
		{
			int slot = strategist.Commander?.SquadOf(unitId) ?? -1;
			if (slot >= 0)
			{
				inspect.Add(AiValueKey.Commander, strategist.PeerId);
				inspect.Add(AiValueKey.CommanderSquad, slot);
				break;
			}
		}

		UnitContext plan = unit.Plan;
		HtnDomainMap map = UnitManager.Instance.Planning?.Map;
		if (plan == null || map == null)
		{
			return inspect;
		}

		inspect.Planning = true;
		inspect.Goal = (byte)plan.Intent.Goal;
		HtnTracer.Capture(map, plan, plan.History, inspect.Trace);

		if (TryIntentPoint(unit, plan, out Vector3 point))
		{
			inspect.Add(AiMarkerKey.Intent, point);
		}

		if (plan.HasNamedTargetPoint)
		{
			inspect.Add(AiMarkerKey.NamedTarget, plan.NamedTargetPoint);
		}

		if (plan.HasSquad)
		{
			inspect.Add(AiMarkerKey.Squad, plan.SquadCentroid);
		}

		if (plan.HasSupport)
		{
			inspect.Add(AiMarkerKey.Support, plan.SupportPoint);
		}

		if (plan.HasPost)
		{
			inspect.Add(AiMarkerKey.Post, plan.PostPoint);
		}

		if (plan.HasStaging)
		{
			inspect.Add(AiMarkerKey.Staging, plan.StagingPoint);
		}

		if (plan.Get(UnitFact.Stance) == (byte)UnitStance.Autonomous)
		{
			inspect.Add(AiMarkerKey.Fallback, plan.FallbackPoint);
		}

		if (plan.Is(UnitFact.UnderFire) && plan.Is(UnitFact.FriendNear))
		{
			inspect.Add(AiMarkerKey.Friend, plan.FriendPoint);
		}

		if (plan.Is(UnitFact.Supply))
		{
			inspect.Add(AiMarkerKey.Supply, plan.SupplyPoint);
		}

		inspect.Add(AiValueKey.Move, (int)plan.Intent.Move);
		inspect.Add(AiValueKey.Focus, plan.FocusOwnerId);
		inspect.Add(AiValueKey.SquadAlive, plan.HasSquad ? plan.SquadAlive : 0);
		inspect.Add(AiValueKey.Waiting, plan.Waiting ? 1 : 0);
		inspect.Add(AiValueKey.StopToFight, plan.Intent.StopToFight ? 1 : 0);
		inspect.Add(AiValueKey.FailedResupplies, plan.FailedResupplies);
		return inspect;
	}

	private AiInspect InspectSquad(int strategistPeerId, int slot)
	{
		Commander commander = _bots.StrategistOf(strategistPeerId)?.Commander;
		if (commander == null || !commander.SquadAt(slot).Active)
		{
			return null;
		}

		CommandContext context = commander.ContextOf(slot);
		CommandIntent intent = commander.IntentOf(slot);
		CommandSquad squad = commander.SquadAt(slot);

		var inspect = new AiInspect
		{
			Ref = AiEntityRef.Squad(strategistPeerId, slot),
			Planning = true,
			Goal = (byte)intent.Goal,
		};

		HtnTracer.Capture(_bots.CommanderPlanning.Map, context, context.History, inspect.Trace);
		inspect.Add(AiMarkerKey.Squad, squad.Centroid);
		inspect.Add(AiMarkerKey.Intent, intent.Point);
		if (intent.Phase == SquadPhase.Gathering)
		{
			inspect.Add(AiMarkerKey.Staging, intent.StagingPoint);
		}

		if (squad.FallingBack)
		{
			inspect.Add(AiMarkerKey.Fallback, squad.FallbackPoint);
		}

		if (intent.Goal == CommandGoal.Refill && squad.Healable)
		{
			inspect.Add(AiMarkerKey.Supply, context.SupplyPoint);
		}

		inspect.Add(AiValueKey.Order, (int)intent.Order);
		inspect.Add(AiValueKey.Target, intent.TargetOwnerId);
		return inspect;
	}
}
