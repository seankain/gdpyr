using System;
using Godot;

namespace Gdpyr.Sim;

/// <summary>
/// One body's hand grenades: how many are left, and when the next may go
/// (docs/NETCODE.md §10.9). A player's is stepped from the recorded frames on the
/// server and on the owning client alike, as a weapon is; a unit's lives on the
/// server only.
/// </summary>
public struct GrenadeState
{
	public byte Count;

	/// <summary>The earliest tick the next one may be thrown on.</summary>
	public uint NextThrowTick;

	/// <summary>The tick the last one was thrown on. Meaningful once <see cref="Thrown"/> is not zero.</summary>
	public uint LastThrowTick;

	/// <summary>
	/// Grenades thrown this life. Half the seed of a unit's throwing error, for the
	/// reason <see cref="WeaponState.ShotIndex"/> is half the seed of a cone.
	/// </summary>
	public uint Thrown;

	public readonly bool HasAny => Count > 0;

	/// <summary>A fresh life's grenades, clamped to what the snapshot can count (<see cref="SimConfig.MaxGrenades"/>).</summary>
	public static GrenadeState Full(int count) => new()
	{
		Count = (byte)Math.Clamp(count, 0, SimConfig.MaxGrenades),
	};
}

/// <summary>
/// What a computer player or a unit is allowed to know before it throws
/// (<see cref="GrenadeSim.ShouldThrow"/>).
/// </summary>
public readonly struct ThrowSituation
{
	public readonly bool HasGrenade;

	/// <summary>Its own pacing allows a throw: the hand is free and the last one was long enough ago.</summary>
	public readonly bool Ready;

	/// <summary>From the thrower's eye to where the grenade is meant to land [m].</summary>
	public readonly float DistanceMeters;

	/// <summary>The target's ground speed [m/s].</summary>
	public readonly float TargetSpeed;

	/// <summary>
	/// The target's middle is hidden from the thrower — behind a wall with only its
	/// head over it, or out of sight where it was last seen. What a grenade is for.
	/// </summary>
	public readonly bool TargetInCover;

	/// <summary>The blast would reach the thrower or somebody on its own side that it does not spare.</summary>
	public readonly bool EndangersFriends;

	public ThrowSituation(bool hasGrenade, bool ready, float distanceMeters, float targetSpeed, bool targetInCover,
		bool endangersFriends)
	{
		HasGrenade = hasGrenade;
		Ready = ready;
		DistanceMeters = distanceMeters;
		TargetSpeed = targetSpeed;
		TargetInCover = targetInCover;
		EndangersFriends = endangersFriends;
	}
}

/// <summary>
/// Hand grenades (docs/NETCODE.md §10.9), as pure functions: the throw a press of
/// the grenade key makes, the arc it leaves on, and when a bot or a unit decides one
/// is worth throwing.
///
/// A grenade is a round like any other — one byte naming it in a spawn message, one
/// pool, one integrator (<see cref="ProjectileSim"/>) — with two differences: it is
/// thrown from the off hand, whatever is in the other, and it is on a fuse
/// (<see cref="ProjectileStats.Fused"/>). Its damage is the projectile's to say
/// (<c>Weapons/projectiles/hand_grenade.tres</c>): a wide blast, and a quarter of it
/// against vehicles and structures.
/// </summary>
public static class GrenadeSim
{
	/// <summary>What a body carries when its definition says nothing: a player every life, and a unit on foot.</summary>
	public const int Carried = 3;

	/// <summary>
	/// Ticks between two throws. A second's wind-up, so the key held down or mashed is
	/// one grenade and not three.
	/// </summary>
	public const int ThrowIntervalTicks = SimConfig.TickRate;

	/// <summary>
	/// Ticks a bot or a unit waits after a throw before it considers another: three
	/// grenades are a fight's worth, not a second's (<see cref="AiReady"/>).
	/// </summary>
	public const int AiThrowIntervalTicks = SimConfig.TickRate * 6;

	/// <summary>
	/// How far above the line of sight a grenade leaves [rad], 10°: an arm throws up and
	/// out. From a standing eye on flat ground, looking level, a throw lands 17 m off a
	/// second later; looking 35° up it lands 33 m off with 0.3 s of fuse to spare, and
	/// looking much higher it goes off in the air.
	/// </summary>
	public const float LoftRadians = 0.17453292f;

	/// <summary>Nearer than this [m], a bot or a unit fights with its gun.</summary>
	public const float MinThrowMeters = 6f;

	/// <summary>Further than this [m], a bot or a unit does not throw: the fuse would leave nobody standing there.</summary>
	public const float MaxThrowMeters = 28f;

	/// <summary>
	/// A target moving slower than this [m/s] — standing, crouched, or a unit holding a
	/// line — will still be near where the grenade lands. Walking pace is five.
	/// </summary>
	public const float SlowTargetMetersPerSecond = 2.5f;

	/// <summary>Close enough [m] for the solver to stop correcting for drag.</summary>
	private const float SolveToleranceMeters = 0.2f;

	/// <summary>Further out than this [m] after every correction, there is no throw that lands there.</summary>
	private const float SolveGiveUpMeters = 1f;

	private const int SolvePasses = 4;

	/// <summary>
	/// Advances one body's grenades by a tick of a player's input and reports whether
	/// it threw one. Edge-triggered off the recorded frame, as a semi-automatic trigger
	/// is, so the server and the owning client throw on the same tick.
	/// </summary>
	public static bool Step(ref GrenadeState state, in InputContext input, uint tick) =>
		input.JustPressed(InputButtons.Grenade) && TrySpend(ref state, tick, ThrowIntervalTicks);

	/// <summary>
	/// Takes one grenade if there is one and the hand is free, and starts the wait for
	/// the next. What a unit, which has no input frame, throws through.
	/// </summary>
	public static bool TrySpend(ref GrenadeState state, uint tick, int intervalTicks)
	{
		if (state.Count == 0 || tick < state.NextThrowTick)
		{
			return false;
		}

		state.Count--;
		state.Thrown++;
		state.LastThrowTick = tick;
		state.NextThrowTick = tick + (uint)Math.Max(intervalTicks, 1);
		return true;
	}

	/// <summary>
	/// Whether a bot or a unit may consider a throw: it has one, its hand is free, and
	/// its last throw this life was <see cref="AiThrowIntervalTicks"/> ago. A person is
	/// held only to <see cref="ThrowIntervalTicks"/>.
	/// </summary>
	public static bool AiReady(in GrenadeState state, uint tick) =>
		state.Count > 0 && tick >= state.NextThrowTick
		&& (state.Thrown == 0 || tick >= state.LastThrowTick + (uint)AiThrowIntervalTicks);

	/// <summary>The direction a grenade leaves a player's hand in, looking along these angles.</summary>
	public static Vector3 ThrowDirection(float yaw, float pitch) =>
		Aim.Direction(yaw, MathF.Min(pitch + LoftRadians, Quantize.HalfPi));

	/// <summary>
	/// Where to look so that a throw leaves along <paramref name="throwDirection"/>: the
	/// inverse of <see cref="ThrowDirection"/>, for a bot that has worked out the arc.
	/// </summary>
	public static Vector3 LookFor(Vector3 throwDirection)
	{
		Aim.Angles(throwDirection, out float yaw, out float pitch);
		return Aim.Direction(yaw, MathF.Max(pitch - LoftRadians, -Quantize.HalfPi));
	}

	/// <summary>
	/// Whether a bot or a unit should throw at its target now: it has one and is ready,
	/// the target is in throwing range, the blast would spare its own side, and the
	/// target is either behind something or slow enough to still be there when the
	/// fuse runs out. Whether there is an arc that reaches is asked separately, and
	/// only once this says yes (<see cref="TrySolve"/>).
	/// </summary>
	public static bool ShouldThrow(in ThrowSituation situation) =>
		situation.HasGrenade && situation.Ready && !situation.EndangersFriends
		&& situation.DistanceMeters >= MinThrowMeters && situation.DistanceMeters <= MaxThrowMeters
		&& (situation.TargetInCover || situation.TargetSpeed <= SlowTargetMetersPerSecond);

	/// <summary>
	/// The direction to throw <paramref name="round"/> in, from <paramref name="from"/>,
	/// so that it comes down at <paramref name="to"/>: the low arc, which spends the
	/// least of the fuse in the air. Solved in a vacuum and then corrected against the
	/// integrator the round actually flies with, so drag does not leave it short. False
	/// when no arc reaches before the fuse runs out.
	/// </summary>
	public static bool TrySolve(Vector3 from, Vector3 to, in ProjectileStats round, out Vector3 direction)
	{
		direction = Vector3.Zero;

		float speed = round.MuzzleVelocity;
		var flat = new Vector3(to.X - from.X, 0f, to.Z - from.Z);
		float distance = flat.Length();
		if (speed <= 0f || distance < 0.5f)
		{
			return false;
		}

		flat /= distance;
		float height = to.Y - from.Y;
		float aimed = distance;
		float error = float.MaxValue;

		for (int pass = 0; pass < SolvePasses; pass++)
		{
			if (!LowArc(aimed, height, speed, out float elevation))
			{
				return false;
			}

			Vector3 candidate = (flat * MathF.Cos(elevation)) + (Vector3.Up * MathF.Sin(elevation));
			float carried = Carry(from, candidate, round, to.Y);
			if (carried < 0f)
			{
				return false;
			}

			direction = candidate;
			error = distance - carried;
			if (MathF.Abs(error) <= SolveToleranceMeters)
			{
				return true;
			}

			aimed += error;
		}

		return MathF.Abs(error) <= SolveGiveUpMeters;
	}

	/// <summary>
	/// How far, measured flat, a round thrown along <paramref name="direction"/> carries
	/// before it comes down through <paramref name="floorY"/>, flown tick by tick through
	/// the same integrator <see cref="ProjectileSim"/> uses. -1 when the fuse runs out
	/// first.
	/// </summary>
	public static float Carry(Vector3 from, Vector3 direction, in ProjectileStats round, float floorY)
	{
		Vector3 position = from;
		Vector3 velocity = direction.Normalized() * round.MuzzleVelocity;
		int ticks = (int)MathF.Ceiling(round.LifetimeSeconds * SimConfig.TickRate);

		for (int t = 0; t < ticks; t++)
		{
			Vector3 previous = position;
			Ballistics.Integrate(SimConfig.TickDelta, SimConfig.ProjectileSubSteps, ref position, ref velocity,
				round.Physics);

			if (velocity.Y < 0f && position.Y <= floorY)
			{
				float drop = previous.Y - position.Y;
				float t01 = drop > 1e-6f ? Math.Clamp((previous.Y - floorY) / drop, 0f, 1f) : 1f;
				Vector3 crossing = previous + ((position - previous) * t01);
				return new Vector2(crossing.X - from.X, crossing.Z - from.Z).Length();
			}
		}

		return -1f;
	}

	/// <summary>
	/// The lower of the two elevations that put a round of this speed
	/// <paramref name="distance"/> away and <paramref name="height"/> up, in a vacuum.
	/// False past its reach.
	/// </summary>
	private static bool LowArc(float distance, float height, float speed, out float elevation)
	{
		elevation = 0f;
		if (distance <= 0f)
		{
			return false;
		}

		float g = Ballistics.Gravity;
		float v2 = speed * speed;
		float discriminant = (v2 * v2) - (g * ((g * distance * distance) + (2f * height * v2)));
		if (discriminant < 0f)
		{
			return false;
		}

		elevation = MathF.Atan((v2 - MathF.Sqrt(discriminant)) / (g * distance));
		return true;
	}
}
