using System;
using Godot;

namespace Gdpyr.Sim;

/// <summary>
/// Whether a round came close to a body without hitting it (docs/TACTICS.md §4.1),
/// as pure geometry: no ray, no engine. A round's chord for the tick, up to what it
/// hit, against a body's capsule — the same test a hit is, with the capsule
/// fattened to <see cref="Meters"/> round its axis — behind a box test that turns
/// away almost every pair first.
/// </summary>
public static class NearMiss
{
	/// <summary>How near a body's axis a round has to pass to be noticed [m]: a shoulder's width and a half.</summary>
	public const float Meters = 1.5f;

	/// <summary>
	/// Whether the chord <paramref name="from"/> → <paramref name="to"/> passes within
	/// <paramref name="meters"/> of the capsule's axis. A chord that hits the capsule
	/// passes within it too: the caller leaves out the round's victim.
	/// </summary>
	public static bool Passes(in HitCapsule capsule, Vector3 from, Vector3 to, float meters = Meters)
	{
		if (meters <= 0f || !Near(capsule, from, to, meters))
		{
			return false;
		}

		// SegmentIntersects fattens the capsule's own radius; what is wanted is a
		// distance from the axis, so the radius is taken back out.
		return Hitbox.SegmentIntersects(capsule, from, to, MathF.Max(meters - capsule.Radius, 0f), out _);
	}

	/// <summary>
	/// Whether a blast at <paramref name="point"/> reaching <paramref name="radius"/>
	/// reaches the capsule, measured to its surface as the blast's damage is
	/// (<c>CombatManager.Explode</c>).
	/// </summary>
	public static bool InReach(in HitCapsule capsule, Vector3 point, float radius) =>
		radius > 0f && Hitbox.Inside(capsule, point, radius + capsule.Radius);

	/// <summary>
	/// The chord's bounding box grown by <paramref name="meters"/>, made once for the
	/// chord and asked of every body (<see cref="Chord.MayPass"/>), so a pass over the
	/// field builds a capsule only for the bodies it may have gone near.
	/// </summary>
	public readonly struct Chord
	{
		private readonly float _minX, _maxX, _minY, _maxY, _minZ, _maxZ;

		public Chord(Vector3 from, Vector3 to, float meters = Meters)
		{
			float grow = MathF.Max(meters, 0f);
			_minX = MathF.Min(from.X, to.X) - grow;
			_maxX = MathF.Max(from.X, to.X) + grow;
			_minY = MathF.Min(from.Y, to.Y) - grow;
			_maxY = MathF.Max(from.Y, to.Y) + grow;
			_minZ = MathF.Min(from.Z, to.Z) - grow;
			_maxZ = MathF.Max(from.Z, to.Z) + grow;
		}

		/// <summary>
		/// Whether a standing body whose feet are at <paramref name="feet"/> and which is
		/// <paramref name="height"/> tall may be within reach: its axis is straight up
		/// from its feet, so this never turns away one <see cref="Passes"/> would keep.
		/// </summary>
		public bool MayPass(Vector3 feet, float height) =>
			feet.X >= _minX && feet.X <= _maxX && feet.Z >= _minZ && feet.Z <= _maxZ
			&& feet.Y <= _maxY && feet.Y + height >= _minY;
	}

	/// <summary>
	/// The broad phase: the chord's bounding box, grown by <paramref name="meters"/>,
	/// against the capsule axis's. A round on the other side of the map is two
	/// comparisons.
	/// </summary>
	private static bool Near(in HitCapsule capsule, Vector3 from, Vector3 to, float meters)
	{
		return Overlaps(MathF.Min(from.X, to.X), MathF.Max(from.X, to.X), capsule.Bottom.X, capsule.Top.X, meters)
			&& Overlaps(MathF.Min(from.Z, to.Z), MathF.Max(from.Z, to.Z), capsule.Bottom.Z, capsule.Top.Z, meters)
			&& Overlaps(MathF.Min(from.Y, to.Y), MathF.Max(from.Y, to.Y), capsule.Bottom.Y, capsule.Top.Y, meters);
	}

	private static bool Overlaps(float min, float max, float a, float b, float grow) =>
		min - grow <= MathF.Max(a, b) && max + grow >= MathF.Min(a, b);
}
