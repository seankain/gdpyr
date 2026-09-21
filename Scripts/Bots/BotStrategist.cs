using System;
using Gdpyr.Core;
using Gdpyr.Fps;
using Gdpyr.Match;
using Gdpyr.Net;
using Gdpyr.Rts;
using Gdpyr.Sim;
using Godot;

namespace Gdpyr.Bots;

/// <summary>
/// One computer strategist (docs/IMPLEMENTATION_PLAN.md §M3.5).
///
/// It holds a strategist slot like a person does — the same peer id, the same
/// entry in <see cref="TeamService"/>, the same cap of two — and plays through the
/// same two server-side requests a human client's RPCs land in: queue a unit, and
/// order units about (docs/NETCODE.md §6.3). Nothing here is privileged; an order
/// it gives goes through the identical ownership checks, so a bug that would let a
/// bot command somebody else's units is a bug that would let a client do it too.
///
/// Its intelligence is its units' intelligence: the only way it learns where the
/// ground force is, is that one of its own riflemen can see somebody. Until M4
/// that was read off <see cref="Unit.TargetOwnerId"/>, which is what a unit has
/// *acquired* and therefore a little narrower than what the side can see; it now
/// reads the same <see cref="VisibilityService"/> the human strategist's snapshot
/// is filtered through, so the two are behind one fog and not two
/// (docs/NETCODE.md §6.2, §9).
/// </summary>
public sealed class BotStrategist
{
	/// <summary>Ticks between sweeping the next spawn area when nothing has been seen.</summary>
	private const int SweepIntervalTicks = SimConfig.TickRate * 25;

	private readonly int _peerId;
	private readonly StrategistTraits _traits;

	/// <summary>Order batches, pre-sized: the plan forbids per-tick allocation on the unit path.</summary>
	private readonly int[] _assault = new int[SimConfig.MaxUnits];
	private readonly int[] _garrison = new int[SimConfig.MaxUnits];

	private uint _nextDecisionTick;

	public BotStrategist(int peerId, in StrategistTraits traits)
	{
		_peerId = peerId;
		_traits = traits;
	}

	public int PeerId => _peerId;

	/// <summary>Units it has ordered forward at the last decision. For the debug HUD.</summary>
	public int AssaultCount { get; private set; }

	/// <summary>Units it is keeping at home. For the debug HUD.</summary>
	public int GarrisonCount { get; private set; }

	/// <summary>
	/// One decision, at most every <see cref="StrategistTraits.DecisionIntervalTicks"/>.
	/// Called every server tick by <see cref="BotDirector"/>, which does not itself
	/// know how often a strategist should think.
	/// </summary>
	public void ServerTick(uint tick)
	{
		if (tick < _nextDecisionTick)
		{
			return;
		}

		// Staggered off the peer id so two strategists do not decide on the same tick.
		_nextDecisionTick = tick + (uint)_traits.DecisionIntervalTicks
			+ (uint)(_peerId % _traits.DecisionIntervalTicks);

		CombatManager combat = CombatManager.Instance;
		UnitManager units = UnitManager.Instance;
		if (combat == null || units == null || combat.TeamOf(_peerId) != Team.Strategist)
		{
			return;
		}

		// Nothing is worth spending or ordering between rounds: the field is cleared
		// and the queues emptied when the next one starts (UnitManager.ClearUnits).
		if (combat.Match.Phase == RoundPhase.Ended)
		{
			return;
		}

		Build(combat, units);
		Command(tick, combat, units);
	}

	/// <summary>
	/// Puts units on every barracks queue it owns, while the points last.
	///
	/// Every barracks, not only the first: the strategist HUD can address one today
	/// (docs/IMPLEMENTATION_PLAN.md §7) but the RPC has always taken an index, and a
	/// bot that only ever used one door would make a two-door map read as a broken
	/// map.
	/// </summary>
	private void Build(CombatManager combat, UnitManager units)
	{
		UnitDefinition infantry = UnitCatalog.Definition(UnitCatalog.Infantry);
		if (infantry == null)
		{
			return;
		}

		int live = units.LiveUnitCount;

		for (int i = 0; i < units.BarracksCount; i++)
		{
			Barracks barracks = units.BarracksAt(i);
			if (barracks == null || barracks.Team != Team.Strategist)
			{
				continue;
			}

			if (!StrategistBrain.ShouldQueue(combat.Match.StrategistPoints, infantry.Cost,
				barracks.Queue.Count, live, _traits, SimConfig.MaxUnits))
			{
				continue;
			}

			units.ServerQueueUnit(_peerId, i, UnitCatalog.Infantry);
		}
	}

	/// <summary>
	/// Sorts the units into a garrison and an assault, and gives each group the one
	/// order it needs. Two messages at most per decision, however many units there
	/// are, because every unit in a group is being sent to the same place.
	/// </summary>
	private void Command(uint tick, CombatManager combat, UnitManager units)
	{
		bool hasObjective = TryObjective(tick, combat, units, out Vector3 objective, out int targetOwnerId);
		Vector3 home = Home(units, objective, hasObjective);

		int assault = 0;
		int garrison = 0;
		int assigned = 0;
		int assaultOrders = 0;
		int garrisonOrders = 0;

		for (int i = 0; i < units.SlotCount; i++)
		{
			Unit unit = units.UnitAt(i);
			if (unit == null || !unit.IsAlive || unit.Team != Team.Strategist)
			{
				continue;
			}

			// Roles are assigned in registry order, so a garrison that loses somebody
			// is refilled by the next unit to need an order rather than by a bespoke
			// bookkeeping pass.
			bool defends = StrategistBrain.ShouldGarrison(assigned, _traits);
			assigned++;

			if (defends)
			{
				garrison++;
			}
			else
			{
				assault++;
				if (!hasObjective)
				{
					// Nothing seen and nowhere to sweep: it keeps whatever it was doing.
					continue;
				}
			}

			Vector3 destination = defends ? home : objective;
			OrderKind wanted = StrategistBrain.OrderFor(defends);
			float drift = unit.Order.Target.DistanceTo(destination);
			if (!StrategistBrain.NeedsOrder(unit.State, unit.Order.Kind, wanted, drift, _traits))
			{
				continue;
			}

			if (defends)
			{
				_garrison[garrisonOrders++] = unit.UnitId;
			}
			else
			{
				_assault[assaultOrders++] = unit.UnitId;
			}
		}

		AssaultCount = assault;
		GarrisonCount = garrison;

		if (garrisonOrders > 0)
		{
			units.ServerIssueOrder(_peerId, _garrison.AsSpan(0, garrisonOrders),
				StrategistBrain.OrderFor(garrison: true), home, OwnerId.None);
		}

		if (assaultOrders > 0)
		{
			// Attack-move rather than move: a bot has no plan past "go where they
			// are", so stopping to fight whatever it meets on the way *is* the plan
			// (StrategistBrain.OrderFor).
			units.ServerIssueOrder(_peerId, _assault.AsSpan(0, assaultOrders),
				StrategistBrain.OrderFor(garrison: false), objective, targetOwnerId);
		}
	}

	/// <summary>
	/// Where to send the assault: the nearest player its units can see, or — when
	/// nothing has been seen — the ground force's spawn areas, walked one at a time.
	///
	/// "Can see" is <see cref="VisibilityService"/>, the same answer the human
	/// strategist's packet is built from, so neither of them knows anything the
	/// other would not (docs/IMPLEMENTATION_PLAN.md §M4). It deliberately reads the
	/// live contact rather than the last known position: a bot that chased ghosts
	/// would be a different opponent from the one this exists to be, and the
	/// position it orders an attack on is the one the fog is currently offering.
	///
	/// Sweeping known spawns is not cheating: they are static map geometry, which a
	/// human strategist can see on the same screen. What the bot does not get is a
	/// position for a player nobody is looking at.
	/// </summary>
	private bool TryObjective(uint tick, CombatManager combat, UnitManager units, out Vector3 objective,
		out int targetOwnerId)
	{
		objective = Vector3.Zero;
		targetOwnerId = OwnerId.None;

		PlayerCombat best = null;
		float bestDistance = float.MaxValue;

		for (int i = 0; i < combat.PlayerCount; i++)
		{
			PlayerCombat seen = combat.PlayerAt(i);
			if (seen?.Character == null || !seen.IsAlive || seen.Team != Team.GroundForce
				|| combat.Visibility?.IsVisible(seen.PeerId) != true)
			{
				continue;
			}

			float distance = NearestUnitDistance(units, seen.Character.SimPosition);
			if (distance < bestDistance)
			{
				bestDistance = distance;
				best = seen;
			}
		}

		if (best != null)
		{
			objective = best.Character.SimPosition;
			targetOwnerId = OwnerId.ForPeer(best.PeerId);
			return true;
		}

		PlayerManager players = PlayerManager.Instance;
		if (players == null || players.SpawnPointCount == 0)
		{
			return false;
		}

		uint sweep = tick / (uint)SweepIntervalTicks;
		objective = players.SpawnPositionAt((int)(sweep % (uint)players.SpawnPointCount));
		return true;
	}

	/// <summary>
	/// How far the nearest unit is from a contact. It decides which of several
	/// contacts the assault is sent at, so it is measured from the army and not from
	/// the bot, which has no position of its own.
	/// </summary>
	private static float NearestUnitDistance(UnitManager units, Vector3 point)
	{
		float nearest = float.MaxValue;

		for (int i = 0; i < units.SlotCount; i++)
		{
			Unit unit = units.UnitAt(i);
			if (unit == null || !unit.IsAlive || unit.Team != Team.Strategist)
			{
				continue;
			}

			float distance = unit.GlobalPosition.DistanceTo(point);
			if (distance < nearest)
			{
				nearest = distance;
			}
		}

		return nearest;
	}

	/// <summary>
	/// What the garrison defends: the first barracks it owns, or the objective when
	/// the map has none — a defend order with no anchor would park every unit on the
	/// origin.
	/// </summary>
	private static Vector3 Home(UnitManager units, Vector3 objective, bool hasObjective)
	{
		for (int i = 0; i < units.BarracksCount; i++)
		{
			Barracks barracks = units.BarracksAt(i);
			if (barracks != null && barracks.Team == Team.Strategist)
			{
				return barracks.RallyPoint;
			}
		}

		return hasObjective ? objective : Vector3.Zero;
	}
}
