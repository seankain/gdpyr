using System;
using Godot;

namespace Gdpyr.Sim;

/// <summary>
/// The accuracy cone: a shot's direction perturbed inside a cone about where the
/// shooter aimed (docs/IMPLEMENTATION_PLAN.md §M3 — "units fire the same
/// projectiles as players, with an accuracy cone"; §7 asks for the per-weapon
/// version at the same time).
///
/// It is seeded and not random. Three processes have to derive the same direction
/// from the same shot — the server that decides what it hit, the shooter's client
/// that draws a tracer before hearing back, and everyone else replaying the spawn
/// record — so a call to any RNG whose sequence depends on how many other shots
/// happened first would be a desync. The seed is the shot's own identity: who
/// fired, with what, and how many rounds that weapon has put out
/// (<see cref="WeaponState.ShotIndex"/>, which both ends derive from the same
/// inputs).
///
/// Directions are uniform over the spherical cap, not over the angle: sampling the
/// angle uniformly piles shots into the centre and makes a wide cone behave like a
/// narrow one.
/// </summary>
public static class Spread
{
	/// <summary>Reciprocal of 2^24, for turning 24 random bits into [0,1).</summary>
	private const float UnitScale = 1f / 16777216f;

	/// <summary>
	/// The seed for one shot. Distinct for every (shooter, weapon, round) triple,
	/// and derived only from values the client and the server both compute from the
	/// same recorded input.
	/// </summary>
	public static uint Seed(int ownerId, byte definitionId, uint shotIndex) =>
		Mix((uint)ownerId ^ 0x9E37_79B9u) ^ Mix(definitionId + 1u) ^ Mix(shotIndex * 2_654_435_761u);

	/// <summary>
	/// <paramref name="direction"/> pushed somewhere inside a cone of half-angle
	/// <paramref name="coneRadians"/>. A cone of zero returns the input unchanged,
	/// which is what keeps a weapon nobody has tuned yet exactly as accurate as it
	/// was before this existed.
	/// </summary>
	public static Vector3 Apply(Vector3 direction, float coneRadians, uint seed)
	{
		if (coneRadians <= 0f || direction == Vector3.Zero)
		{
			return direction;
		}

		Vector3 forward = direction.Normalized();

		uint a = Mix(seed);
		uint b = Mix(a ^ 0x85EB_CA6Bu);
		float u = (a >> 8) * UnitScale;
		float v = (b >> 8) * UnitScale;

		// Uniform over the cap: cos θ is what has to be uniform, not θ.
		float cosCone = MathF.Cos(Math.Clamp(coneRadians, 0f, MathF.PI));
		float cosTheta = 1f - (u * (1f - cosCone));
		float sinTheta = MathF.Sqrt(MathF.Max(0f, 1f - (cosTheta * cosTheta)));
		float phi = v * Quantize.TwoPi;

		Basis(forward, out Vector3 right, out Vector3 up);

		return ((forward * cosTheta)
			+ (right * (sinTheta * MathF.Cos(phi)))
			+ (up * (sinTheta * MathF.Sin(phi)))).Normalized();
	}

	/// <summary>
	/// An offset uniformly inside a disc of <paramref name="radius"/> on the ground
	/// plane. For a round whose error is where it comes down rather than which way it
	/// left — a mortar's — and seeded for the same reason the cone is.
	/// </summary>
	public static Vector3 Disc(float radius, uint seed)
	{
		if (radius <= 0f)
		{
			return Vector3.Zero;
		}

		uint a = Mix(seed);
		uint b = Mix(a ^ 0x85EB_CA6Bu);

		// Uniform over the area, so the radius goes as the root: a uniform radius
		// piles shells into the middle exactly as a uniform angle piles shots.
		float r = radius * MathF.Sqrt((a >> 8) * UnitScale);
		float phi = (b >> 8) * UnitScale * Quantize.TwoPi;

		return new Vector3(r * MathF.Cos(phi), 0f, r * MathF.Sin(phi));
	}

	/// <summary>Degrees as a designer authors them to the half-angle the cone is expressed in.</summary>
	public static float ConeFromDegrees(float degrees) =>
		degrees <= 0f ? 0f : Math.Clamp(degrees, 0f, 90f) * (MathF.PI / 180f);

	/// <summary>
	/// Any two unit vectors perpendicular to <paramref name="forward"/> and to each
	/// other. Which two does not matter — the azimuth is uniform — but the choice
	/// has to be a function of <paramref name="forward"/> alone, so that two peers
	/// with the same shot build the same frame.
	/// </summary>
	private static void Basis(Vector3 forward, out Vector3 right, out Vector3 up)
	{
		// Up is degenerate as a reference when the shot is (nearly) vertical.
		Vector3 reference = MathF.Abs(forward.Y) < 0.99f ? Vector3.Up : Vector3.Right;
		right = forward.Cross(reference).Normalized();
		up = right.Cross(forward);
	}

	/// <summary>splitmix32's finalizer: cheap, allocation-free and well distributed.</summary>
	private static uint Mix(uint x)
	{
		x ^= x >> 16;
		x *= 0x7FEB_352Du;
		x ^= x >> 15;
		x *= 0x846C_A68Bu;
		x ^= x >> 16;
		return x;
	}
}
