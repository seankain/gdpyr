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

	/// <summary>
	/// Intersects the segment <paramref name="from"/> → <paramref name="to"/> with
	/// <paramref name="box"/>, its half extents grown by <paramref name="extraRadius"/> —
	/// a round's sweep radius, or a unit's capsule radius when the segment is that
	/// capsule's axis (docs/GROUND_VEHICLES.md §5.9). The slab test: the segment's entry
	/// into each pair of opposite faces, the latest of those entries, and a hit when it
	/// comes before the earliest exit.
	///
	/// <paramref name="t"/> is the fraction along the segment at entry, as for a capsule,
	/// so the nearest of a capsule, a unit and a hull is the one struck first. A segment
	/// that starts inside the grown box hits at 0, and a zero-length one hits only there.
	///
	/// Growing the faces rather than rounding them overstates the grown box at its edges
	/// and corners — by (√2 − 1) of the growth at an edge — which a bullet's zero sweep
	/// never sees and a grenade's 6 cm barely does.
	/// </summary>
	public static bool SegmentIntersects(in HitObb box, Vector3 from, Vector3 to, float extraRadius, out float t)
	{
		t = 0f;
		float grow = MathF.Max(extraRadius, 0f);
		Vector3 half = box.HalfExtents + new Vector3(grow, grow, grow);
		Vector3 p = box.ToLocal(from);
		Vector3 d = new(
			(to - from).Dot(box.AxisX),
			(to - from).Dot(box.AxisY),
			(to - from).Dot(box.AxisZ));

		float enter = 0f;
		float exit = 1f;
		if (!Slab(p.X, d.X, half.X, ref enter, ref exit)
			|| !Slab(p.Y, d.Y, half.Y, ref enter, ref exit)
			|| !Slab(p.Z, d.Z, half.Z, ref enter, ref exit))
		{
			return false;
		}

		t = enter;
		return true;
	}

	/// <summary>
	/// The distance from <paramref name="point"/> to <paramref name="box"/>'s surface; 0
	/// inside it. What a blast is measured by (docs/GROUND_VEHICLES.md §6.5): the face of
	/// the hull, not its centre, so a shell that lands beside a truck hurts it as much as
	/// one beside a man.
	/// </summary>
	public static float DistanceTo(in HitObb box, Vector3 point)
	{
		Vector3 local = box.ToLocal(point);
		float x = MathF.Max(MathF.Abs(local.X) - box.HalfExtents.X, 0f);
		float y = MathF.Max(MathF.Abs(local.Y) - box.HalfExtents.Y, 0f);
		float z = MathF.Max(MathF.Abs(local.Z) - box.HalfExtents.Z, 0f);
		return MathF.Sqrt((x * x) + (y * y) + (z * z));
	}

	/// <summary>The point of <paramref name="box"/> nearest <paramref name="point"/>: itself when inside.</summary>
	public static Vector3 ClosestPoint(in HitObb box, Vector3 point)
	{
		Vector3 local = box.ToLocal(point);
		float x = Math.Clamp(local.X, -box.HalfExtents.X, box.HalfExtents.X);
		float y = Math.Clamp(local.Y, -box.HalfExtents.Y, box.HalfExtents.Y);
		float z = Math.Clamp(local.Z, -box.HalfExtents.Z, box.HalfExtents.Z);
		return box.Center + (box.AxisX * x) + (box.AxisY * y) + (box.AxisZ * z);
	}

	/// <summary>
	/// Narrows [<paramref name="enter"/>, <paramref name="exit"/>] to where the segment is
	/// between one pair of faces. False when it never is: parallel to them and outside,
	/// or entering after it has already left another pair.
	/// </summary>
	private static bool Slab(float start, float delta, float half, ref float enter, ref float exit)
	{
		if (MathF.Abs(delta) < 1e-9f)
		{
			return MathF.Abs(start) <= half;
		}

		float a = (-half - start) / delta;
		float b = (half - start) / delta;
		if (a > b)
		{
			(a, b) = (b, a);
		}

		enter = MathF.Max(enter, a);
		exit = MathF.Min(exit, b);
		return enter <= exit;
	}
}

/// <summary>
/// An oriented box: a crew vehicle's hull is up to four of these (docs/GROUND_VEHICLES.md
/// §5.4) — the 4x4's cab, bonnet, bed and gun shield, the helicopter's cabin and tail
/// boom. A centre, three orthonormal axes and the half extents along each.
///
/// A box rather than a capsule because a truck is one, and a capsule round a cab
/// would either leave the corners out or take rounds a metre off the paint. Analytic,
/// as a <see cref="HitCapsule"/> is, so a hit on a hull is decided by the same code on
/// every peer and by <c>dotnet test</c>.
/// </summary>
public readonly struct HitObb
{
	public readonly Vector3 Center;

	/// <summary>The box's own X axis, a unit vector.</summary>
	public readonly Vector3 AxisX;

	public readonly Vector3 AxisY;

	public readonly Vector3 AxisZ;

	/// <summary>Half the box's size along each of its axes; never negative.</summary>
	public readonly Vector3 HalfExtents;

	public HitObb(Vector3 center, Vector3 axisX, Vector3 axisY, Vector3 axisZ, Vector3 halfExtents)
	{
		Center = center;
		AxisX = axisX;
		AxisY = axisY;
		AxisZ = axisZ;
		HalfExtents = new Vector3(MathF.Max(halfExtents.X, 0f), MathF.Max(halfExtents.Y, 0f),
			MathF.Max(halfExtents.Z, 0f));
	}

	/// <summary>
	/// A box authored in a body's own frame — <paramref name="localCenter"/> and
	/// <paramref name="halfExtents"/> along the body's axes — placed by the body's
	/// rotation <paramref name="basis"/> (orthonormal) and <paramref name="origin"/>.
	/// </summary>
	public static HitObb Place(in Basis basis, Vector3 origin, Vector3 localCenter, Vector3 halfExtents) =>
		new(origin + (basis * localCenter), basis.X, basis.Y, basis.Z, halfExtents);

	/// <summary>An axis-aligned box: what a hull is with its body at rest and facing −Z.</summary>
	public static HitObb Aligned(Vector3 center, Vector3 halfExtents) =>
		new(center, Vector3.Right, Vector3.Up, Vector3.Back, halfExtents);

	/// <summary><paramref name="point"/> in the box's own frame, measured from its centre.</summary>
	public Vector3 ToLocal(Vector3 point)
	{
		Vector3 p = point - Center;
		return new Vector3(p.Dot(AxisX), p.Dot(AxisY), p.Dot(AxisZ));
	}

	/// <summary>The radius of the sphere round the centre that holds the whole box.</summary>
	public float BoundingRadius => HalfExtents.Length();

	public bool IsValid => HalfExtents.X > 0f && HalfExtents.Y > 0f && HalfExtents.Z > 0f;
}
