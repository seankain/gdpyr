using System;
using Godot;

namespace Gdpyr.Sim;

/// <summary>
/// A damageable entity's capsule at one instant, as the character's collision
/// shape defines it: two sphere centres and a radius.
///
/// Stored as the two hemisphere centres rather than as (origin, height) because
/// that is what the intersection test wants and because the character's capsule
/// is animated — crouching and sliding move both ends — so there is no fixed
/// relationship to recompute it from.
/// </summary>
public readonly struct HitCapsule
{
	/// <summary>Centre of the lower hemisphere.</summary>
	public readonly Vector3 Bottom;

	/// <summary>Centre of the upper hemisphere.</summary>
	public readonly Vector3 Top;

	public readonly float Radius;

	public HitCapsule(Vector3 bottom, Vector3 top, float radius)
	{
		Bottom = bottom;
		Top = top;
		Radius = MathF.Max(radius, 0f);
	}

	/// <summary>
	/// From the position a <c>CharacterBody3D</c> reports (the floor point) and the
	/// collision capsule's total height and radius. A Godot capsule's height
	/// includes both hemispheres, so the axis is shorter than the height by a
	/// diameter.
	/// </summary>
	public static HitCapsule FromFeet(Vector3 feet, float height, float radius)
	{
		float axis = MathF.Max(height - 2f * radius, 0f);
		Vector3 bottom = feet + new Vector3(0f, radius, 0f);
		return new HitCapsule(bottom, bottom + new Vector3(0f, axis, 0f), radius);
	}

	public Vector3 Center => (Bottom + Top) * 0.5f;

	public bool IsValid => Radius > 0f;
}

/// <summary>
/// Segment-against-capsule intersection: the one geometric primitive combat needs
/// that the engine will not do for us.
///
/// It is here, in engine-free code, rather than as a Godot shapecast because
/// player hits are resolved against *recorded* hitbox transforms
/// (docs/NETCODE.md §5) — positions the physics server does not have any more —
/// and because a hit test that decides whether someone died should be testable
/// without booting an engine. World geometry is still the engine's job.
/// </summary>
public static class Hitbox
{
	/// <summary>
	/// Intersects the segment <paramref name="from"/> → <paramref name="to"/> with
	/// <paramref name="capsule"/>, optionally fattened by
	/// <paramref name="extraRadius"/> (a melee swing or a shell with a body is a
	/// swept sphere, not a ray).
	///
	/// <paramref name="t"/> comes back as the fraction along the segment at the
	/// *entry* point, not at closest approach: whichever of several candidate hits
	/// happens first has to win, and closest approach does not order correctly when
	/// a bullet passes a shoulder before hitting a chest behind it.
	/// </summary>
	public static bool SegmentIntersects(in HitCapsule capsule, Vector3 from, Vector3 to, float extraRadius, out float t)
	{
		t = 0f;
		float radius = capsule.Radius + MathF.Max(extraRadius, 0f);
		if (radius <= 0f)
		{
			return false;
		}

		// Start already overlapping: a projectile spawned inside a hitbox hits at
		// once, and a zero-length segment has no direction to solve along. Answering
		// "no hit" here would let a point-blank shot pass through.
		if (Inside(capsule, from, radius))
		{
			t = 0f;
			return true;
		}

		Vector3 d = to - from;
		float dd = d.Dot(d);
		if (dd <= 0f)
		{
			return false;
		}

		Vector3 ba = capsule.Top - capsule.Bottom;
		Vector3 oa = from - capsule.Bottom;

		float baba = ba.Dot(ba);
		float bard = ba.Dot(d);
		float baoa = ba.Dot(oa);
		float rdoa = d.Dot(oa);
		float oaoa = oa.Dot(oa);

		float hit = float.PositiveInfinity;

		// The cylindrical body. `a` degenerates to zero when the segment runs
		// parallel to the capsule's axis, in which case only the caps can be hit.
		float a = baba * dd - bard * bard;
		float b = baba * rdoa - baoa * bard;
		float c = baba * oaoa - baoa * baoa - radius * radius * baba;
		float h = b * b - a * c;

		if (h >= 0f && MathF.Abs(a) > 1e-9f)
		{
			float root = (-b - MathF.Sqrt(h)) / a;
			float y = baoa + root * bard;
			if (y > 0f && y < baba && root >= 0f && root <= 1f)
			{
				hit = root;
			}
		}

		// The two hemispherical caps.
		hit = MathF.Min(hit, SphereHit(from - capsule.Bottom, d, dd, radius));
		hit = MathF.Min(hit, SphereHit(from - capsule.Top, d, dd, radius));

		if (float.IsPositiveInfinity(hit))
		{
			return false;
		}

		t = hit;
		return true;
	}

	/// <summary>Nearest forward intersection with a sphere at the origin, or +inf.</summary>
	private static float SphereHit(Vector3 oc, Vector3 d, float dd, float radius)
	{
		float b = d.Dot(oc);
		float c = oc.Dot(oc) - radius * radius;
		float h = b * b - dd * c;
		if (h < 0f)
		{
			return float.PositiveInfinity;
		}

		float root = (-b - MathF.Sqrt(h)) / dd;
		return root >= 0f && root <= 1f ? root : float.PositiveInfinity;
	}

	/// <summary>True when <paramref name="point"/> is within <paramref name="radius"/> of the capsule's axis.</summary>
	public static bool Inside(in HitCapsule capsule, Vector3 point, float radius) =>
		DistanceToAxisSquared(capsule, point) <= radius * radius;

	/// <summary>Squared distance from a point to the capsule's axis segment.</summary>
	public static float DistanceToAxisSquared(in HitCapsule capsule, Vector3 point)
	{
		Vector3 ba = capsule.Top - capsule.Bottom;
		Vector3 pa = point - capsule.Bottom;
		float baba = ba.Dot(ba);
		float projection = baba > 0f ? Math.Clamp(pa.Dot(ba) / baba, 0f, 1f) : 0f;
		return (pa - ba * projection).LengthSquared();
	}
}
