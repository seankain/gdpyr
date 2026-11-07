using System;
using Godot;

namespace Gdpyr.Sim;

/// <summary>
/// Where a mounted mortar launcher points its tube (Scripts/Fps/MortarLauncher.cs), as
/// pure functions.
///
/// The gunner lays it by looking at the ground: the round comes down where their look
/// meets the ground the launcher stands on, inside the traverse arc it was put down
/// facing (<see cref="SimConfig.EmplacementTraverseRadians"/>, as the heavy gun's) and
/// between its nearest and furthest reach. The tube's elevation for that distance is the
/// barracks mortar's high-arc solve (<see cref="MortarSolver"/>).
///
/// "The ground the launcher stands on" is a level plane through its feet, not a ray into
/// the world: the server and the owning client lay the same tube from the same recorded
/// look, so the predicted shell and the authoritative one are the same arc, and a look
/// at a tank's hull — which a plane does not stop — is a look past it. Aim at the ground
/// under the hull; the HUD's marker shows where that is.
/// </summary>
public static class MortarLay
{
	/// <summary>Looking down by less than this is looking at the horizon: the furthest reach.</summary>
	private const float HorizonEpsilon = 1e-4f;

	/// <summary>
	/// The nearest and furthest along level ground a tube <paramref name="muzzleHeight"/>
	/// above it lays <paramref name="round"/>: the furthest at the high arc's flattest
	/// elevation (45°, which the solver brackets from), the nearest at its steepest or
	/// <see cref="SimConfig.MortarMinRangeMeters"/>, whichever is further. False for a round
	/// that cannot be laid at all.
	/// </summary>
	public static bool TryRangeLimits(in ProjectileStats round, float muzzleHeight, out float min, out float max)
	{
		min = 0f;
		max = 0f;
		float height = -MathF.Max(muzzleHeight, 0f);

		if (!MortarSolver.TryRange(MathF.PI * 0.25f, height, round, out max, out _)
			|| !MortarSolver.TryRange(MortarSolver.MaxPitchRadians, height, round, out float steepest, out _))
		{
			max = 0f;
			return false;
		}

		min = MathF.Max(steepest, SimConfig.MortarMinRangeMeters);
		return min < max;
	}

	/// <summary>
	/// The point on the level ground through <paramref name="ground"/> (the launcher's feet)
	/// that a gunner whose eye is at <paramref name="eye"/>, looking at
	/// <paramref name="yaw"/>, <paramref name="pitch"/>, lays the launcher on.
	///
	/// Measured from the launcher, which the gunner stands beside rather than at. A look
	/// at or above the horizon is the furthest reach along the look; a look that comes
	/// down nearer the launcher than its nearest reach is the nearest along the look. The
	/// bearing is then held inside the traverse arc about <paramref name="deployYaw"/>, and
	/// the distance between <paramref name="min"/> and <paramref name="max"/>.
	/// </summary>
	public static Vector3 Target(Vector3 eye, float yaw, float pitch, Vector3 ground, float deployYaw, float min,
		float max)
	{
		float bearing = yaw;
		float distance = max;

		Vector3 look = Aim.Direction(yaw, pitch);
		float drop = eye.Y - ground.Y;

		if (look.Y < -HorizonEpsilon && drop > 0f)
		{
			Vector3 point = eye + (look * (drop / -look.Y));
			var flat = new Vector3(point.X - ground.X, 0f, point.Z - ground.Z);
			float length = flat.Length();

			if (length >= min)
			{
				Aim.Angles(flat, out bearing, out _);
				distance = length;
			}
			else
			{
				distance = min;
			}
		}

		bearing = deployYaw + Mathf.Clamp(Mathf.AngleDifference(deployYaw, bearing),
			-SimConfig.EmplacementTraverseRadians, SimConfig.EmplacementTraverseRadians);
		distance = Mathf.Clamp(distance, min, MathF.Max(min, max));

		return ground + (Aim.Direction(bearing, 0f) * distance);
	}

	/// <summary>
	/// The direction a round leaves <paramref name="muzzle"/> in to come down on
	/// <paramref name="target"/> (<see cref="MortarSolver.TrySolve"/>, the high arc). A
	/// target <see cref="Target"/> chose is always one it solves; anything else is laid at
	/// the flattest elevation of the high arc, along its bearing, rather than not at all.
	/// </summary>
	public static Vector3 Direction(Vector3 muzzle, Vector3 target, in ProjectileStats round)
	{
		if (MortarSolver.TrySolve(muzzle, target, round, out Vector3 direction, out _))
		{
			return direction;
		}

		Aim.Angles(new Vector3(target.X - muzzle.X, 0f, target.Z - muzzle.Z), out float yaw, out _);
		return Aim.Direction(yaw, MathF.PI * 0.25f);
	}

	/// <summary>How far along the ground from the launcher's feet a target is [m].</summary>
	public static float RangeOf(Vector3 ground, Vector3 target) =>
		new Vector3(target.X - ground.X, 0f, target.Z - ground.Z).Length();
}
