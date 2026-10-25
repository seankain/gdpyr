using System;
using Gdpyr.Match;
using Gdpyr.Sim;
using Gdpyr.Sim.Htn;
using Godot;

namespace Gdpyr.Rts;

/// <summary>
/// A heavy gunner's base of fire (docs/TACTICS.md §5.2): on its scan,
/// a gunner under micro-autonomy with nothing of its own to shoot gathers the places the
/// side knows of an enemy — where one is seen now by any friend, was last seen, or fired
/// at it from — and keeps those in its gun's reach with a line to them over the cover
/// board; its planner fires bursts at them. As its squad drill's base it fires where the
/// drill says (§5.5). No ray: what the side remembers, the unit's own fire memory, the
/// drill's list, and the cover board.
/// </summary>
public partial class UnitManager
{
	/// <summary>One scan's places, reused by every gunner: the contacts first, then where its last round came from.</summary>
	private readonly Vector3[] _suppressPlaces = new Vector3[GunnerPlan.Capacity];

	/// <summary>
	/// The places a gunner's planner may suppress, on its scan, into its
	/// <see cref="UnitFact.Suppress"/>: none unless it may (micro-autonomy, the gunner's
	/// doctrine, an order it suppresses under, no target of its own). Not a player who has
	/// died since the side saw them: a burst is not spent on a body, as the reflex's is not
	/// (<see cref="Suppress(Unit, uint, ref Vector3, ref Vector3, ref float)"/>).
	/// </summary>
	private void SenseGunner(Unit unit, UnitContext plan, in UnitOrder order, uint tick, ContactMemory memory,
		bool objective, int squad)
	{
		bool may = unit.Autonomy == MicroAutonomy.On && unit.Traits.Doctrine == UnitDoctrine.Gunner
			&& unit.Team == Team.Strategist && unit.TargetOwnerId == OwnerId.None
			&& UnitContext.SuppressesUnder(order.Kind, objective);
		if (!may)
		{
			plan.SenseSuppress(ReadOnlySpan<Vector3>.Empty);
			return;
		}

		// The base of its squad's drill fires where the drill says, squad-mates' fired-from
		// points among them (docs/TACTICS.md §4.2, §5.2).
		int drilled = DrillPlaces(unit, plan, squad);
		if (drilled >= 0)
		{
			plan.SenseSuppress(_drillPlaces.AsSpan(0, drilled));
			return;
		}

		int count = GunnerPlan.Gather(memory, tick, IsAlivePlayer, FiredUpon.IsRecent(unit.Fire, tick,
			GunnerPlan.FiredFromTicks), FiredFromFeet(unit), _suppressPlaces, out int contacts);

		// From the eye it fires from, which is its standing one wherever it waits.
		Vector3 eye = unit.GlobalPosition + (Vector3.Up * unit.Body.EyeHeight(UnitPosture.Standing));
		int places = GunnerPlan.Entries(_suppressPlaces.AsSpan(0, count), contacts, eye, unit.Traits.EngageRangeMeters,
			Cover);
		plan.SenseSuppress(_suppressPlaces.AsSpan(0, places));
	}

	/// <summary>Whether the player an owner id names is alive: made once, so asking it allocates nothing.</summary>
	private static readonly Func<int, bool> IsAlivePlayer =
		ownerId => CombatManager.Instance?.Find(PeerId(ownerId)) is { IsAlive: true };

	/// <summary>Gunners firing at a place they cannot see, this tick. Server-side, for the debug HUD's <c>fire</c> row.</summary>
	private int GunnersSuppressing()
	{
		int count = 0;
		for (int i = 0; i < _ordered.Count; i++)
		{
			Unit unit = _ordered[i];
			count += unit.IsOnField && unit.Plan is { Intent.Goal: UnitGoal.SuppressPoint, Intent.FireAt: true } ? 1 : 0;
		}

		return count;
	}
}
