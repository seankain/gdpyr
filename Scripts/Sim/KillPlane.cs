using System;

namespace Gdpyr.Sim;

/// <summary>
/// A height a map can set, below which a body dies (docs/MAPS.md §3.2): the first node in
/// group <see cref="Group"/>, an invisible marker whose Y is the plane. Gully puts it at its
/// river's surface, so a player who drops into the gully, whose walls are too steep to climb
/// back up, can walk into the water and come back at the spawn line for a ticket.
///
/// Engine-free, so what is below the plane and what a death through any vest is are tests.
/// </summary>
public static class KillPlane
{
	/// <summary>The group the plane's marker is in.</summary>
	public const string Group = "kill_plane";

	/// <summary>No plane: nothing is below it.</summary>
	public const float None = float.NegativeInfinity;

	/// <summary>
	/// Whether a body standing at <paramref name="y"/> — a player's feet, a unit's origin —
	/// is below <paramref name="planeY"/>. Never for no plane, and never for a height that
	/// is not a number.
	/// </summary>
	public static bool IsBelow(float y, float planeY) => float.IsFinite(planeY) && y < planeY;

	/// <summary>
	/// What a player at <paramref name="health"/> in a vest of <paramref name="vestPoints"/>
	/// is dealt by the plane: more than both together, which no vest can leave standing.
	/// </summary>
	public static float LethalDamage(int health, int vestPoints) =>
		Math.Max(health, 0) + Math.Max(vestPoints, 0) + 1f;
}
