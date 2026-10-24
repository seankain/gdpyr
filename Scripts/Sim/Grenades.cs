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

	/// <summary>
	/// How far either side of a flight's line [m] a box is still gathered as one it could
	/// strike: float error, and nothing else — the flight does not leave its line.
	/// </summary>
	private const float NearMarginMeters = 0.1f;

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
	public static float Carry(Vector3 from, Vector3 direction, in ProjectileStats round, float floorY) =>
		Fly(from, direction, round, floorY, null, out Vector3 rest)
			? new Vector2(rest.X - from.X, rest.Z - from.Z).Length()
			: -1f;

	/// <summary>
	/// The low arc to <paramref name="to"/> (<see cref="TrySolve"/>) flown against the
	/// cover on <paramref name="boxes"/> (docs/TACTICS.md §5.4): where it comes to rest —
	/// against the first box it strikes, as a grenade that hits a wall lies at its foot,
	/// else where it comes down — and whether that is within
	/// <paramref name="reachMeters"/> of <paramref name="to"/>, measured flat. A blast
	/// is not stopped by a wall (<c>CombatManager.Explode</c> measures distance and
	/// nothing else), so a grenade that stops against the near face of the wall somebody
	/// crouches behind still reaches them, and one that stops against a block between
	/// them does not.
	///
	/// No high arc: thrown at <see cref="ProjectileStats.MuzzleVelocity"/>, every high
	/// root inside <see cref="MaxThrowMeters"/> is still in the air when the fuse runs
	/// out (<c>GrenadeTests</c> holds this), and none comes down steeper than a low arc
	/// that stops at the wall's foot.
	/// </summary>
	public static bool TrySolveOver(Vector3 from, Vector3 to, in ProjectileStats round, CoverBoard boxes,
		float reachMeters, out Vector3 direction, out Vector3 rest)
	{
		rest = to;
		if (!TrySolve(from, to, round, out direction) || !Fly(from, direction, round, to.Y, boxes, out rest))
		{
			return false;
		}

		var miss = new Vector2(rest.X - to.X, rest.Z - to.Z);
		return miss.LengthSquared() <= reachMeters * reachMeters;
	}

	/// <summary>
	/// How far from a body's feet, measured flat, a grenade lying on the ground still
	/// kills it from <paramref name="health"/>: the blast's lethal distance from the
	/// body's surface — <see cref="ProjectileStats.ExplosionRadiusMeters"/> × (1 −
	/// health ÷ <see cref="ProjectileStats.ExplosionDamage"/>), 2 m for the hand grenade
	/// against a full 100 — reckoned against a capsule of <paramref name="bodyRadius"/>
	/// whose lowest point is at the feet, as <c>CombatManager.Explode</c> measures it.
	/// 0 for a round whose blast never kills from there.
	/// </summary>
	public static float KillReach(in ProjectileStats round, float health, float bodyRadius)
	{
		if (!round.IsExplosive || round.ExplosionDamage <= health)
		{
			return 0f;
		}

		float lethal = round.ExplosionRadiusMeters * (1f - (health / round.ExplosionDamage));
		float radius = MathF.Max(bodyRadius, 0f);
		return MathF.Sqrt(((lethal + radius) * (lethal + radius)) - (radius * radius));
	}

	/// <summary>
	/// Flies a round thrown along <paramref name="direction"/> tick by tick, through the
	/// same integrator <see cref="ProjectileSim"/> uses, until it comes down through
	/// <paramref name="floorY"/> or strikes a box on <paramref name="boxes"/>, if any:
	/// where it comes to rest. False when the fuse runs out first.
	///
	/// Drag acts along the velocity, so seen from above the flight is a straight line
	/// along <paramref name="direction"/>: the boxes near that line, as far as the round
	/// could carry, are gathered once and each chord asks only those.
	/// </summary>
	private static bool Fly(Vector3 from, Vector3 direction, in ProjectileStats round, float floorY, CoverBoard boxes,
		out Vector3 rest)
	{
		Vector3 position = from;
		Vector3 velocity = direction.Normalized() * round.MuzzleVelocity;
		int ticks = (int)MathF.Ceiling(round.LifetimeSeconds * SimConfig.TickRate);

		Span<int> near = stackalloc int[boxes != null ? CoverBoard.Capacity : 0];
		int nearCount = 0;
		if (boxes != null)
		{
			var flat = new Vector3(velocity.X, 0f, velocity.Z);
			Vector3 farthest = from + (flat * round.LifetimeSeconds);
			nearCount = boxes.Near(from, farthest, NearMarginMeters, near);
		}

		for (int t = 0; t < ticks; t++)
		{
			Vector3 previous = position;
			Ballistics.Integrate(SimConfig.TickDelta, SimConfig.ProjectileSubSteps, ref position, ref velocity,
				round.Physics);

			// Down through the floor on this chord, how far along it; a box counts only
			// if it is struck before that.
			float end = 1f;
			bool down = velocity.Y < 0f && position.Y <= floorY;
			if (down)
			{
				float drop = previous.Y - position.Y;
				end = drop > 1e-6f ? Math.Clamp((previous.Y - floorY) / drop, 0f, 1f) : 1f;
			}

			if (nearCount > 0 && boxes.FirstHit(near[..nearCount], previous, position, out float hit) >= 0 && hit <= end)
			{
				rest = previous + ((position - previous) * hit);
				return true;
			}

			if (down)
			{
				rest = previous + ((position - previous) * end);
				return true;
			}
		}

		rest = position;
		return false;
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

/// <summary>
/// One place a unit could throw a grenade at (docs/TACTICS.md §5.4): where the side
/// last saw an enemy, or where a round that came near the unit was fired from.
/// </summary>
public readonly struct GrenadeCandidate
{
	/// <summary>Where the enemy's feet were, as far as anybody knows.</summary>
	public readonly Vector3 Feet;

	/// <summary>The tick it was seen there, or fired from there.</summary>
	public readonly uint Tick;

	/// <summary>What one grenade at it claims (<see cref="GrenadeClaims"/>): the contact, or a cell of the ground.</summary>
	public readonly long Key;

	/// <summary>A fired-from point rather than a contact: nobody saw who fired.</summary>
	public readonly bool FiredFrom;

	private GrenadeCandidate(Vector3 feet, uint tick, long key, bool firedFrom)
	{
		Feet = feet;
		Tick = tick;
		Key = key;
		FiredFrom = firedFrom;
	}

	/// <summary>A contact the side remembers, by its <see cref="OwnerId"/>.</summary>
	public static GrenadeCandidate Contact(int ownerId, Vector3 feet, uint seenTick) =>
		new(feet, seenTick, (uint)ownerId, firedFrom: false);

	/// <summary>
	/// Where a round came from. Claimed by the <see cref="GrenadePlan.CellMeters"/>
	/// cell it falls in: two rounds from one rifle land a metre apart in a unit's
	/// reckoning, and are one place to throw at.
	/// </summary>
	public static GrenadeCandidate Fired(Vector3 feet, uint tick)
	{
		long x = (long)MathF.Floor(feet.X / GrenadePlan.CellMeters) & 0xFFFFFFF;
		long z = (long)MathF.Floor(feet.Z / GrenadePlan.CellMeters) & 0xFFFFFFF;
		return new GrenadeCandidate(feet, tick, (1L << 62) | (x << 28) | z, firedFrom: true);
	}
}

/// <summary>
/// Which hidden enemy a unit throws at (docs/TACTICS.md §5.4), as pure functions
/// over what the side remembers and the cover on the field: no ray.
/// </summary>
public static class GrenadePlan
{
	/// <summary>
	/// How old a sighting or a round may be [ticks], 3 s: the fuse is 3 s, and a place
	/// an enemy was longer ago than that is where they were, not where the blast will
	/// find them.
	/// </summary>
	public const int FreshTicks = SimConfig.TickRate * 3;

	/// <summary>How long one grenade claims its enemy for the rest of the side [ticks]: its flight, its fuse, and a second.</summary>
	public const int ClaimTicks = SimConfig.TickRate * 4;

	/// <summary>The size of the cell a fired-from point is claimed by [m].</summary>
	public const float CellMeters = 3f;

	/// <summary>
	/// How far above where the side recorded an enemy the hidden test aims [m]: low on
	/// the body, as a crouched one's middle is. A player's recorded position is its
	/// origin, which crouching lifts 0.3 m off the ground. One whose head shows over the
	/// box is a target the unit has, and never a candidate.
	/// </summary>
	public const float HiddenAimMeters = 0.5f;

	/// <summary>Within this of the line to where the unit's order goes [m], a candidate is on its way and goes first.</summary>
	public const float OnLineMeters = 6f;

	/// <summary>
	/// Whether <paramref name="candidate"/> is one to throw at from <paramref name="eye"/>:
	/// seen or fired from within <see cref="FreshTicks"/>, between
	/// <see cref="GrenadeSim.MinThrowMeters"/> and <see cref="GrenadeSim.MaxThrowMeters"/>
	/// away, and behind a box — the line from the eye to <see cref="HiddenAimMeters"/> above
	/// it crosses one. One the unit can see it shoots.
	/// </summary>
	public static bool IsCandidate(in GrenadeCandidate candidate, Vector3 eye, uint tick, CoverBoard boxes)
	{
		if (candidate.Tick > tick || tick - candidate.Tick > FreshTicks)
		{
			return false;
		}

		float distance = eye.DistanceTo(candidate.Feet);
		return distance >= GrenadeSim.MinThrowMeters && distance <= GrenadeSim.MaxThrowMeters
			&& boxes.FirstHit(eye, candidate.Feet + (Vector3.Up * HiddenAimMeters), out _) >= 0;
	}

	/// <summary>
	/// The candidate to throw at, or -1: of those that are candidates and not claimed by
	/// another unit, the ones within <see cref="OnLineMeters"/> of the line from
	/// <paramref name="lineFrom"/> to <paramref name="lineTo"/> first when
	/// <paramref name="hasLine"/> (where its order goes), then the nearest; ties to the
	/// earlier. A fired-from point within <see cref="CellMeters"/> of a contact in the
	/// list is that contact, seen, and is not a second enemy.
	/// </summary>
	public static int Choose(ReadOnlySpan<GrenadeCandidate> candidates, Vector3 eye, uint tick, CoverBoard boxes,
		GrenadeClaims claims, int unitId, bool hasLine, Vector3 lineFrom, Vector3 lineTo)
	{
		int best = -1;
		bool bestOnLine = false;
		float bestMeters = float.MaxValue;
		for (int i = 0; i < candidates.Length; i++)
		{
			GrenadeCandidate candidate = candidates[i];
			if (!IsCandidate(candidate, eye, tick, boxes) || (claims != null && claims.IsClaimedByOther(candidate.Key, tick,
				unitId)) || (candidate.FiredFrom && NearContact(candidates, candidate.Feet)))
			{
				continue;
			}

			bool onLine = hasLine && OffLineSquared(candidate.Feet, lineFrom, lineTo) <= OnLineMeters * OnLineMeters;
			float meters = eye.DistanceTo(candidate.Feet);
			if (best < 0 || (onLine && !bestOnLine) || (onLine == bestOnLine && meters < bestMeters))
			{
				best = i;
				bestOnLine = onLine;
				bestMeters = meters;
			}
		}

		return best;
	}

	private static bool NearContact(ReadOnlySpan<GrenadeCandidate> candidates, Vector3 feet)
	{
		for (int i = 0; i < candidates.Length; i++)
		{
			if (!candidates[i].FiredFrom
				&& new Vector2(candidates[i].Feet.X - feet.X, candidates[i].Feet.Z - feet.Z).LengthSquared()
				<= CellMeters * CellMeters)
			{
				return true;
			}
		}

		return false;
	}

	/// <summary>The squared flat distance from <paramref name="point"/> to the segment from <paramref name="a"/> to <paramref name="b"/>.</summary>
	private static float OffLineSquared(Vector3 point, Vector3 a, Vector3 b)
	{
		float dx = b.X - a.X;
		float dz = b.Z - a.Z;
		float length = (dx * dx) + (dz * dz);
		float s = length > 1e-8f ? Math.Clamp((((point.X - a.X) * dx) + ((point.Z - a.Z) * dz)) / length, 0f, 1f) : 0f;
		float ox = point.X - (a.X + (dx * s));
		float oz = point.Z - (a.Z + (dz * s));
		return (ox * ox) + (oz * oz);
	}
}

/// <summary>
/// One side's grenades in the air at hidden enemies (docs/TACTICS.md §5.4): one per
/// enemy at a time, for <see cref="GrenadePlan.ClaimTicks"/> after it is thrown.
/// Fixed-size and allocation-free; a full table gives up its oldest claim.
/// </summary>
public sealed class GrenadeClaims
{
	private readonly long[] _keys;
	private readonly uint[] _until;
	private readonly int[] _units;

	public GrenadeClaims(int capacity)
	{
		_keys = new long[Math.Max(capacity, 1)];
		_until = new uint[_keys.Length];
		_units = new int[_keys.Length];
	}

	public int Capacity => _keys.Length;

	/// <summary>Whether a unit other than <paramref name="unitId"/> has a grenade on its way to <paramref name="key"/>.</summary>
	public bool IsClaimedByOther(long key, uint tick, int unitId)
	{
		for (int i = 0; i < _keys.Length; i++)
		{
			if (_until[i] > tick && _keys[i] == key && _units[i] != unitId)
			{
				return true;
			}
		}

		return false;
	}

	/// <summary>Records that <paramref name="unitId"/> threw at <paramref name="key"/> on <paramref name="tick"/>.</summary>
	public void Claim(long key, uint tick, int unitId)
	{
		int slot = 0;
		for (int i = 0; i < _keys.Length; i++)
		{
			if (_until[i] <= tick || _keys[i] == key)
			{
				slot = i;
				break;
			}

			if (_until[i] < _until[slot])
			{
				slot = i;
			}
		}

		_keys[slot] = key;
		_until[slot] = tick + GrenadePlan.ClaimTicks;
		_units[slot] = unitId;
	}

	/// <summary>Forgets every claim, between rounds.</summary>
	public void Clear()
	{
		Array.Clear(_keys);
		Array.Clear(_until);
		Array.Clear(_units);
	}
}
