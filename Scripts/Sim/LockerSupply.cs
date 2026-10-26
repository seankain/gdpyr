using System;
using Godot;

namespace Gdpyr.Sim;

/// <summary>
/// Where a weapon locker reaches (docs/LOADOUT.md §4.4, L-D7, L-D8): a metre, flat,
/// from the cabinet's footprint rather than from its origin.
///
/// The cabinet has no collider and a player can stand in it, and from the origin a
/// metre would reach 0.75 m past its front face but only 0.4 m past either end. The
/// footprint is the 1.2 × 0.5 m rectangle under it, turned with its node; the front —
/// the face with the racks — is the node's +Z.
///
/// Engine-free because the server's kit validator, the use key's resolution on both
/// ends of the prediction and a ground bot's walk to the locker all measure the same
/// distance, and from L5 the resupply does too.
/// </summary>
public static class LockerSupply
{
	/// <summary>The cabinet along the node's X: its width.</summary>
	public const float CabinetWidthMeters = 1.2f;

	/// <summary>The cabinet along the node's Z: its depth, racks to back.</summary>
	public const float CabinetDepthMeters = 0.5f;

	/// <summary>
	/// How far in front of the racks a bot sent to the locker stands: half its reach,
	/// so a bot that stops a little short is still at it.
	/// </summary>
	public const float StandOffMeters = SimConfig.LockerReachMeters * 0.5f;

	/// <summary>
	/// The flat distance from <paramref name="feet"/> to the footprint of a locker at
	/// <paramref name="origin"/> turned <paramref name="yaw"/> radians about +Y; 0 inside it.
	/// Height is ignored: a locker reaches whoever stands on the floor in front of it.
	/// </summary>
	public static float FootprintDistance(Vector3 feet, Vector3 origin, float yaw)
	{
		float dx = feet.X - origin.X;
		float dz = feet.Z - origin.Z;

		// Into the node's frame: its X axis is (cos, -sin) in world X/Z and its Z axis
		// (sin, cos), which is Godot's rotation about +Y.
		float cos = MathF.Cos(yaw);
		float sin = MathF.Sin(yaw);
		float localX = (dx * cos) - (dz * sin);
		float localZ = (dx * sin) + (dz * cos);

		float outX = MathF.Max(MathF.Abs(localX) - (CabinetWidthMeters * 0.5f), 0f);
		float outZ = MathF.Max(MathF.Abs(localZ) - (CabinetDepthMeters * 0.5f), 0f);
		return MathF.Sqrt((outX * outX) + (outZ * outZ));
	}

	/// <summary>Whether a footprint distance is within the locker's reach (L-D8).</summary>
	public static bool InReach(float distance) => distance <= SimConfig.LockerReachMeters;

	/// <summary>
	/// Where to stand to use a locker: <see cref="StandOffMeters"/> in front of its
	/// racks, on its centre line, at the origin's height.
	/// </summary>
	public static Vector3 StandPoint(Vector3 origin, float yaw)
	{
		float ahead = (CabinetDepthMeters * 0.5f) + StandOffMeters;
		return new Vector3(origin.X + (MathF.Sin(yaw) * ahead), origin.Y, origin.Z + (MathF.Cos(yaw) * ahead));
	}
}
