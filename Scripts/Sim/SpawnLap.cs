using Godot;

namespace Gdpyr.Sim;

/// <summary>
/// Where a player stands when a map has fewer spawn points than players.
///
/// Spawn index <c>i</c> is spawn point <c>i % points</c>, so with four points and
/// six ground players indices 0 and 4, and 1 and 5, name the same point. At a round
/// reset every player is put back at once, and two character capsules put in the
/// same place push each other upward and out of the world, where nothing kills them
/// (docs/HTN_BOTS.md §6, found in H2, fixed in H6). So each lap of indices round the
/// points stands in a slot of its own beside the point: the first on it, the second
/// <see cref="StepMeters"/> to its side, the third that far behind it, the fourth
/// both — four slots a point, which is <see cref="SnapshotCodec.MaxPlayers"/> on a
/// map with four points. A respawn walks the index on with each death, and the
/// slots repeat rather than walking the player off across the map.
/// </summary>
public static class SpawnLap
{
	/// <summary>
	/// How far a slot stands from its point: half the 4 m the test map's points are
	/// apart, so a side-step lands midway between two of them.
	/// </summary>
	public const float StepMeters = 2f;

	/// <summary>Slots a point has before they repeat.</summary>
	public const int Slots = 4;

	/// <summary>
	/// The offset of spawn index <paramref name="index"/>'s slot, in the spawn
	/// point's own frame: x to its side, z behind it (Godot's +Z, the point faces −Z).
	/// </summary>
	public static Vector3 Offset(int index, int points)
	{
		if (points <= 0 || index < points)
		{
			return Vector3.Zero;
		}

		int lap = (index / points) % Slots;
		return new Vector3((lap & 1) * StepMeters, 0f, (lap >> 1) * StepMeters);
	}
}
