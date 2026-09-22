using System;
using Godot;

namespace Gdpyr.Sim;

/// <summary>What a barracks defence fires, which decides what it needs in order to fire it.</summary>
public enum DefenseKind : byte
{
	/// <summary>Direct fire. Has to see what it shoots at, and has to turn to face it.</summary>
	Gun = 0,

	/// <summary>
	/// Indirect fire. Needs nothing but range: the shell goes over whatever is in the
	/// way, which is the whole reason to have one — cover next to the door stops the
	/// gun and does not stop this.
	/// </summary>
	Mortar = 1,
}

/// <summary>
/// One defence's rules, in the units the simulation runs in. The mount's authored
/// numbers (metres, seconds, degrees) are converted once, where the node is built,
/// exactly as a unit's are (<c>UnitDefinition.ToTraits</c>).
/// </summary>
public readonly struct DefenseTraits
{
	public readonly DefenseKind Kind;

	/// <summary>Furthest it engages from, muzzle to target.</summary>
	public readonly float RangeMeters;

	/// <summary>Nearest. A mortar cannot drop a shell at its own feet; a gun has no floor.</summary>
	public readonly float MinRangeMeters;

	/// <summary>Ticks between picking a target and firing at it: the gunner's reaction, the mortar's laying time.</summary>
	public readonly int ReactionTicks;

	/// <summary>How far the gun turns in one tick, on each axis [rad].</summary>
	public readonly float TraverseRadiansPerTick;

	/// <summary>How far off its aim a gun may still be and fire [rad].</summary>
	public readonly float OnTargetRadians;

	public DefenseTraits(DefenseKind kind, float rangeMeters, float minRangeMeters, int reactionTicks,
		float traverseRadiansPerTick, float onTargetRadians)
	{
		Kind = kind;
		RangeMeters = MathF.Max(rangeMeters, 0f);
		MinRangeMeters = Math.Clamp(minRangeMeters, 0f, RangeMeters);
		ReactionTicks = Math.Max(reactionTicks, 0);
		TraverseRadiansPerTick = MathF.Max(traverseRadiansPerTick, 0f);
		OnTargetRadians = MathF.Max(onTargetRadians, 0f);
	}

	public bool NeedsLineOfSight => Kind == DefenseKind.Gun;
}

/// <summary>
/// The rules for a barracks' own guns and mortars, as pure functions
/// (docs/NETCODE.md §10.4).
///
/// A defence exists to make the ground around a barracks somewhere the ground
/// force cannot stand, so that a round's default is not six players parked on the
/// door shooting every unit the strategist pays for as it walks out. It is the
/// building's and not the strategist's: it costs nothing, runs out of nothing,
/// cannot be ordered about and cannot be killed. What it can be is avoided — by
/// staying out of its range, and, for the gun, out of its sight.
///
/// Engine-free for the usual reason: what the gun decides to shoot at, when it may
/// fire, and where a shell comes down are all questions with an answer, and
/// answering them by standing a server up is how they stop being asked.
/// </summary>
public static class DefenseSim
{
	/// <summary>
	/// How far past its range a defence holds a target it already has. Hysteresis, as
	/// units have: a player walking the edge of the ring must not make the gun
	/// flicker between firing and turning away every scan.
	/// </summary>
	public const float KeepRangeScale = 1.1f;

	/// <summary>Whether a candidate is worth picking up.</summary>
	public static bool CanEngage(in DefenseTraits traits, float distance, bool lineOfSight) =>
		distance <= traits.RangeMeters && distance >= traits.MinRangeMeters
		&& (lineOfSight || !traits.NeedsLineOfSight);

	/// <summary>
	/// Whether a target already held is still worth holding. Wider than
	/// <see cref="CanEngage"/> on range, the same on everything else: a gun whose
	/// target has ducked behind a wall looks for somebody it can see.
	/// </summary>
	public static bool ShouldKeep(in DefenseTraits traits, float distance, bool lineOfSight) =>
		distance <= traits.RangeMeters * KeepRangeScale && distance >= traits.MinRangeMeters
		&& (lineOfSight || !traits.NeedsLineOfSight);

	/// <summary>
	/// Turns a gun towards where it wants to point, by at most
	/// <see cref="DefenseTraits.TraverseRadiansPerTick"/> on each axis, and returns
	/// how far off it still is.
	///
	/// A gun that snaps onto whoever it picks is a gun nobody can outrun; one that
	/// has to swing round is one a player sprinting across its front can beat to
	/// cover, which is the decision the ring is meant to create.
	/// </summary>
	public static float Slew(ref float yaw, ref float pitch, float wantYaw, float wantPitch, in DefenseTraits traits)
	{
		float step = traits.TraverseRadiansPerTick;

		yaw = Mathf.Wrap(yaw + Math.Clamp(Mathf.AngleDifference(yaw, wantYaw), -step, step), -Mathf.Pi, Mathf.Pi);
		pitch += Math.Clamp(wantPitch - pitch, -step, step);

		return Aim.Direction(yaw, pitch).AngleTo(Aim.Direction(wantYaw, wantPitch));
	}

	/// <summary>
	/// Whether a defence may put a round in the air this tick: it has had its
	/// reaction time on this target, and it is pointed close enough at it. A mortar
	/// passes an aim error of zero — its tube is laid for each shell, not slewed.
	/// </summary>
	public static bool MayFire(in DefenseTraits traits, uint tick, uint targetSinceTick, float aimError) =>
		tick - targetSinceTick >= (uint)traits.ReactionTicks && aimError <= traits.OnTargetRadians;

	/// <summary>
	/// A point on the ring of <paramref name="radius"/> about
	/// <paramref name="center"/>, on the side <paramref name="from"/> is on, swung
	/// round by <paramref name="bearingOffsetRadians"/>.
	///
	/// Where a ground bot waits for the strategist's units instead of on the door:
	/// the edge of the defended ground, facing the way it came.
	/// </summary>
	public static Vector3 StandoffPoint(Vector3 center, Vector3 from, float radius, float bearingOffsetRadians)
	{
		Vector3 away = from - center;
		away.Y = 0f;

		// Standing on the centre has no side; any bearing will do, so long as it is
		// always the same one.
		Vector3 bearing = away.LengthSquared() > 0.0001f ? away.Normalized() : Vector3.Back;
		bearing = bearing.Rotated(Vector3.Up, bearingOffsetRadians);

		return new Vector3(center.X, from.Y, center.Z) + (bearing * MathF.Max(radius, 0f));
	}

	/// <summary>True when <paramref name="point"/> is inside the ring, measured flat.</summary>
	public static bool IsInside(Vector3 center, Vector3 point, float radius)
	{
		float dx = point.X - center.X;
		float dz = point.Z - center.Z;
		return radius > 0f && ((dx * dx) + (dz * dz)) < radius * radius;
	}
}

/// <summary>
/// Where to point a mortar so that its shell comes down on a point, through the
/// same integrator the shell will actually fly with (docs/NETCODE.md §4.2).
///
/// High arc only. The low solution is a rifle shot with a slow round, stopped by
/// the same cover a gun is, and cover is exactly what a mortar is for.
///
/// Solved numerically rather than in closed form, because the round has drag and
/// the closed form is for a vacuum. Above the elevation of greatest range, range
/// falls monotonically as the tube rises, so a bracketed search cannot pick the
/// wrong branch; the vacuum's greatest-range elevation is the bracket's floor, and
/// drag only ever pulls the real one lower. The search is false position with the
/// Illinois correction rather than bisection: range is smooth in elevation, so it
/// converges in a handful of flights where bisection takes fourteen, and every
/// flight is a few hundred integration steps on the tick the shell is fired.
/// </summary>
public static class MortarSolver
{
	/// <summary>The steepest a tube is laid. Vertical is a shell that comes down on the crew.</summary>
	public static readonly float MaxPitchRadians = Mathf.DegToRad(88f);

	/// <summary>How close to the aim point is close enough [m]. Far inside any scatter a map would author.</summary>
	private const float ToleranceMeters = 0.02f;

	/// <summary>A ceiling on flights per solve, which a smooth range curve never reaches.</summary>
	private const int MaxIterations = 24;

	/// <summary>
	/// Integration sub-steps per tick while solving. One, not the eight a shell in
	/// flight gets (<see cref="SimConfig.ProjectileSubSteps"/>): Heun's method is
	/// exact under constant acceleration and a mortar shell's drag is a few percent
	/// of gravity. Tested against the real integrator to land inside half a metre.
	/// </summary>
	private const int SubSteps = 1;

	/// <summary>
	/// The launch direction that brings a round fired from <paramref name="origin"/>
	/// down on <paramref name="target"/>, and how long it takes to get there. False
	/// when the target is out of reach, inside the minimum range the tube's
	/// steepest elevation allows, or directly overhead.
	/// </summary>
	public static bool TrySolve(Vector3 origin, Vector3 target, in ProjectileStats stats, out Vector3 direction,
		out float flightSeconds)
	{
		direction = Vector3.Zero;
		flightSeconds = 0f;

		Vector3 flat = target - origin;
		float height = flat.Y;
		flat.Y = 0f;
		float distance = flat.Length();

		if (stats.MuzzleVelocity <= 0f || distance < 0.01f)
		{
			return false;
		}

		float low = (MathF.PI * 0.25f) + (0.5f * MathF.Atan2(height, distance));
		float high = MaxPitchRadians;
		if (low >= high)
		{
			return false;
		}

		// Out of reach at the flattest elevation the high arc has: nothing steeper
		// goes further.
		if (!TryRange(low, height, stats, out float furthest, out float lowSeconds) || furthest < distance)
		{
			return false;
		}

		// Still beyond the target at the steepest: it is inside the minimum range. A
		// steepest flight that never comes down inside the round's lifetime is no
		// bracket at all.
		if (!TryRange(high, height, stats, out float nearest, out float highSeconds) || nearest > distance)
		{
			return false;
		}

		// Range minus distance: positive at the floor of the bracket, negative at the top.
		float errorLow = furthest - distance;
		float errorHigh = nearest - distance;
		int side = 0;

		float pitch = low;
		float seconds = lowSeconds;
		float error = errorLow;

		if (MathF.Abs(errorHigh) < MathF.Abs(errorLow))
		{
			pitch = high;
			seconds = highSeconds;
			error = errorHigh;
		}

		for (int i = 0; i < MaxIterations && MathF.Abs(error) > ToleranceMeters; i++)
		{
			float span = errorLow - errorHigh;
			pitch = span > 1e-6f ? low + ((high - low) * (errorLow / span)) : 0.5f * (low + high);

			if (!TryRange(pitch, height, stats, out float range, out seconds))
			{
				return false;
			}

			error = range - distance;

			if (error > 0f)
			{
				low = pitch;
				errorLow = error;

				// Illinois: when the same end moves twice running, halve the other's
				// weight, or false position creeps up on the root from one side for ever.
				if (side == 1)
				{
					errorHigh *= 0.5f;
				}
				side = 1;
			}
			else
			{
				high = pitch;
				errorHigh = error;
				if (side == -1)
				{
					errorLow *= 0.5f;
				}
				side = -1;
			}
		}

		Aim.Angles(flat, out float yaw, out _);
		direction = Aim.Direction(yaw, pitch);
		flightSeconds = seconds;
		return true;
	}

	/// <summary>
	/// How far along the ground a round fired at <paramref name="pitch"/> is when it
	/// comes back down through <paramref name="height"/> (relative to the muzzle),
	/// and after how long. False when it never does inside its lifetime — a target
	/// above the top of the arc, or a flight that outlives the round.
	/// </summary>
	public static bool TryRange(float pitch, float height, in ProjectileStats stats, out float range,
		out float seconds)
	{
		range = 0f;
		seconds = 0f;

		// A vertical plane with -Z forward, which is the frame Aim.Direction uses for
		// a yaw of zero: range is the distance travelled along -Z.
		Vector3 position = Vector3.Zero;
		Vector3 velocity = Aim.Direction(0f, pitch) * stats.MuzzleVelocity;

		float dt = SimConfig.TickDelta;
		int ticks = (int)MathF.Ceiling(stats.LifetimeSeconds / dt);

		for (int t = 1; t <= ticks; t++)
		{
			Vector3 previous = position;
			Ballistics.Integrate(dt, SubSteps, ref position, ref velocity, stats.Physics);

			// Downwards through the target's height, and only downwards: a round still
			// climbing past it has not arrived, and one whose whole arc is below it
			// never will.
			if (previous.Y > height && position.Y <= height)
			{
				float f = (previous.Y - height) / (previous.Y - position.Y);
				range = -(previous.Z + ((position.Z - previous.Z) * f));
				seconds = (t - 1 + f) * dt;
				return true;
			}
		}

		return false;
	}
}
