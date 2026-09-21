namespace Gdpyr.Sim;

/// <summary>
/// When a round is over, as pure predicates (docs/IMPLEMENTATION_PLAN.md §M5).
///
/// The ground force's condition is one number and lives on the round itself: at
/// zero tickets they are out. The strategist's is three numbers at once, which is
/// exactly the sort of thing that is wrong for twenty minutes before anybody
/// notices — so it is here, engine-free, and tested.
/// </summary>
public static class WinConditions
{
	/// <summary>
	/// Whether the strategist has nothing left to play with: not enough points for
	/// the cheapest unit in the catalog, nothing alive on the field, nothing on a
	/// barracks queue, and no resource node held.
	///
	/// All four, because each one on its own is a normal moment in a round. Broke is
	/// normal — income is a rate. An empty field is normal for as long as something
	/// is building. A queue is paid for, so a unit in the oven is a unit. And a node
	/// held is income coming, which is points, which is units: a strategist standing
	/// on a node is not out of the game however empty their pockets are.
	///
	/// <paramref name="queuedUnits"/> is what makes this safe to test every tick
	/// rather than on a timer: the moment the last queue empties is the moment the
	/// answer can change, and it changes on a tick rather than over a second.
	/// </summary>
	public static bool IsStrategistEliminated(int points, int cheapestUnitCost, int liveUnits, int queuedUnits,
		int heldNodes)
	{
		if (liveUnits > 0 || queuedUnits > 0 || heldNodes > 0)
		{
			return false;
		}

		// A catalog with nothing buildable in it reports its cheapest unit as
		// int.MaxValue, which lands here as "cannot afford anything" — which is what
		// a strategist with nothing to build is.
		return points < cheapestUnitCost;
	}
}
