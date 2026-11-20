using System;
using Godot;

namespace Gdpyr.Sim;

/// <summary>
/// The scout drone's strike (docs/OUTPOST.md §3.3), as numbers and pure functions: how
/// fast it dives, how near its aim it goes off, and how long it may fly at one before it
/// goes off where it is. Its charge is a round in the weapon catalog like any other
/// (<c>Weapons/projectiles/scout_charge.tres</c>), because what a blast does is
/// <see cref="Armour"/>'s and <c>CombatManager.Explode</c>'s to say.
///
/// Nothing here starts a strike. A drone goes off only on the strategist's
/// <see cref="OrderKind.Strike"/>: no planner, no reflex and no other order reaches the
/// charge.
/// </summary>
public static class StrikeRules
{
	/// <summary>How fast it flies at its aim [m/s]: twice its cruise, under an FPV drone's 24.</summary>
	public const float SpeedMetersPerSecond = 18f;

	/// <summary>Within this of its aim it goes off [m].</summary>
	public const float FuseMeters = 1.5f;

	/// <summary>
	/// Within this of an enemy body, measured from its middle to theirs, it goes off on the
	/// way [m]: somebody who steps into its path is what it was sent for.
	/// </summary>
	public const float ProximityMeters = 2f;

	/// <summary>The longest it flies at one aim before it goes off where it is: 8 s, 144 m at its speed.</summary>
	public const int MaxTicks = SimConfig.TickRate * 8;

	/// <summary>How far above a point on the ground it aims [m]: chest height, so the blast is not half in the floor.</summary>
	public const float GroundAimLiftMeters = 1f;
}

/// <summary>Why a strike ended, for the log and the agent stream.</summary>
public enum StrikeEnd : byte
{
	/// <summary>Still flying.</summary>
	None = 0,

	/// <summary>It reached its aim.</summary>
	Arrived = 1,

	/// <summary>An enemy came within <see cref="StrikeRules.ProximityMeters"/>.</summary>
	Proximity = 2,

	/// <summary>It flew into the world on the way.</summary>
	Impact = 3,

	/// <summary><see cref="StrikeRules.MaxTicks"/> ran out.</summary>
	Expired = 4,
}

/// <summary>
/// One tick of a strike, engine-free: where the drone is headed and whether it goes off.
/// The engine half asks the world whether the move ran into anything, and who is near.
/// </summary>
public static class StrikeSim
{
	/// <summary>
	/// The velocity that flies a drone at <paramref name="position"/> straight at
	/// <paramref name="aim"/>, no faster than <see cref="StrikeRules.SpeedMetersPerSecond"/>
	/// and no further than the aim in one tick; and whether it goes off this tick instead —
	/// it is within <see cref="StrikeRules.FuseMeters"/> of its aim, or it has been flying
	/// at it for <see cref="StrikeRules.MaxTicks"/>.
	/// </summary>
	public static StrikeEnd Step(Vector3 position, Vector3 aim, uint startTick, uint tick, float dt, out Vector3 velocity)
	{
		velocity = Vector3.Zero;
		if (!position.IsFinite() || !aim.IsFinite())
		{
			return StrikeEnd.Expired;
		}

		Vector3 to = aim - position;
		float distance = to.Length();
		if (distance <= StrikeRules.FuseMeters)
		{
			return StrikeEnd.Arrived;
		}

		if (tick >= startTick && tick - startTick >= StrikeRules.MaxTicks)
		{
			return StrikeEnd.Expired;
		}

		float step = dt > 0f && float.IsFinite(dt) ? dt : SimConfig.TickDelta;
		float speed = MathF.Min(StrikeRules.SpeedMetersPerSecond, distance / step);
		velocity = to / distance * speed;
		return StrikeEnd.None;
	}

	/// <summary>Whether a body whose middle is at <paramref name="body"/> is close enough to set the drone off on the way.</summary>
	public static bool Proximate(Vector3 drone, Vector3 body) =>
		drone.IsFinite() && body.IsFinite()
		&& drone.DistanceSquaredTo(body) <= StrikeRules.ProximityMeters * StrikeRules.ProximityMeters;

	/// <summary>Where a strike at a point on the ground aims: that far over it.</summary>
	public static Vector3 GroundAim(Vector3 ground) => ground + (Vector3.Up * StrikeRules.GroundAimLiftMeters);
}
