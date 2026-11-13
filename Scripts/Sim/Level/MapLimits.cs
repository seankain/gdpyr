namespace Gdpyr.Sim.Level;

/// <summary>
/// The bounds a map made in the game is held to (docs/LEVEL_EDITOR.md LE-D3, LE-D4,
/// LE-D23). One place, because the brushes clamp to them, the validator refuses what
/// is outside them and the reader refuses a file that says otherwise: three answers
/// that have to be the same answer.
/// </summary>
public static class MapLimits
{
	/// <summary>Samples along a side of the terrain: 240 cells over the 240 m field (LE-D3).</summary>
	public const int TerrainSamples = 241;

	/// <summary>How far apart two samples are [mm]. The one spacing a heightfield takes without scaling.</summary>
	public const int TerrainSpacingMillimeters = 1000;

	/// <summary>The field's edge from its middle [m]: where the walls stand.</summary>
	public const float FieldHalfMeters = 120f;

	/// <summary>A stored height is an <c>int16</c> of these to the metre (LE-D3).</summary>
	public const int HeightStepsPerMeter = 256;

	/// <summary>One step of a stored height [m]: 3.90625 mm, and exact as a <c>float</c>.</summary>
	public const float HeightStepMeters = 1f / HeightStepsPerMeter;

	/// <summary>
	/// The lowest the ground goes [m] (LE-D4): over the drone's floor at −20 m and the
	/// kill plane at −50 m, so neither is met by standing on the ground.
	/// </summary>
	public const float MinHeightMeters = -16f;

	/// <summary>
	/// The highest the ground goes [m] (LE-D4): under the agent's rays, which start at
	/// 80 m, and the drone's ceiling at 120 m.
	/// </summary>
	public const float MaxHeightMeters = 64f;

	/// <summary><see cref="MinHeightMeters"/> as a stored sample.</summary>
	public const short MinHeightSample = (short)(MinHeightMeters * HeightStepsPerMeter);

	/// <summary><see cref="MaxHeightMeters"/> as a stored sample.</summary>
	public const short MaxHeightSample = (short)(MaxHeightMeters * HeightStepsPerMeter);

	/// <summary>
	/// The steepest face that counts as walkable [degrees] (LE-D23). A body and a unit
	/// were measured up a 42.1° face of the collider's triangles and stalled on a 43.0°
	/// one; this is under the first with two degrees to spare.
	/// </summary>
	public const float WalkableSlopeDegrees = 40f;

	/// <summary>
	/// Half the square a round's pieces stand in [m]: spawns, the locker, the kiosk,
	/// barracks, nodes, guns, cans and the anchor (MAPS.md §3.1, item 7). It is
	/// <c>Cargo.FieldHalfExtentMeters</c>, and a test holds the two equal.
	/// </summary>
	public const float PlayHalfMeters = 115f;

	// ---- the file (docs/LEVEL_EDITOR.md §6.8) ---------------------------------------------

	/// <summary>The largest file a reader takes or a receiver accepts: 1 MiB.</summary>
	public const int MaxFileBytes = 1 << 20;

	/// <summary>Objects one map may hold.</summary>
	public const int MaxObjects = 512;

	/// <summary>Parameters one object may carry.</summary>
	public const int MaxObjectParameters = 8;

	/// <summary>Roads one map may hold.</summary>
	public const int MaxRoads = 32;

	/// <summary>Control points one road may have.</summary>
	public const int MaxRoadPoints = 64;

	/// <summary>The fewest control points a road has: a straight.</summary>
	public const int MinRoadPoints = 2;
}
