using Godot;

namespace Gdpyr.Sim;

/// <summary>
/// Whether a round went past the listener on a tick, and where (docs/AUDIO.md §2.2),
/// as pure geometry: the round's chord for the tick against one point, the ear. The
/// sound is played where the round came closest, on the one tick it did, so a round
/// is heard going by once however many chords it takes to pass.
///
/// Only a round that was coming towards the ear when the chord began and was level
/// with it or going away by the end of it passes: <c>0 &lt; t ≤ 1</c> along the chord.
/// A chord that ends still on its way in leaves it to the next; one that starts going
/// away has passed already. Chords meet end to end, so exactly one of them owns the
/// moment of closest approach.
/// </summary>
public static class Flyby
{
	/// <summary>How near the ear a round of <paramref name="kind"/> has to pass to be heard going by [m].</summary>
	public static float RadiusOf(FlybyKind kind) => kind switch
	{
		FlybyKind.Bullet => 4f,
		FlybyKind.HeavyBullet => 6f,
		FlybyKind.Shell => 10f,
		_ => 0f,
	};

	/// <summary>
	/// How far a round of <paramref name="kind"/> has to have come from its muzzle before
	/// it is heard going by [m]. Nearer than that, the shot itself is what is heard: a
	/// teammate firing past a shoulder is a shot, not a near miss.
	/// </summary>
	public static float MinTravelOf(FlybyKind kind) => kind switch
	{
		FlybyKind.Bullet => 10f,
		FlybyKind.HeavyBullet => 12f,
		FlybyKind.Shell => 15f,
		_ => float.PositiveInfinity,
	};

	/// <summary>
	/// Whether the chord <paramref name="from"/> → <paramref name="to"/> of a round fired
	/// from <paramref name="origin"/> comes closest to <paramref name="ear"/> on this chord,
	/// within <paramref name="radius"/> of it, at least <paramref name="minTravel"/> from
	/// the muzzle. <paramref name="point"/> is where it came closest.
	/// </summary>
	public static bool Passes(Vector3 from, Vector3 to, Vector3 origin, Vector3 ear, float radius, float minTravel,
		out Vector3 point)
	{
		point = to;
		if (!(radius > 0f))
		{
			return false;
		}

		Vector3 chord = to - from;
		float lengthSquared = chord.LengthSquared();
		if (!(lengthSquared > 1e-8f))
		{
			// A round lying where it landed flies no chord.
			return false;
		}

		float t = (ear - from).Dot(chord) / lengthSquared;
		if (!(t > 0f) || t > 1f)
		{
			return false;
		}

		point = from + (chord * t);
		if (point.DistanceSquaredTo(ear) > radius * radius)
		{
			return false;
		}

		return minTravel <= 0f || point.DistanceSquaredTo(origin) >= minTravel * minTravel;
	}

	/// <summary>The same, with <paramref name="kind"/>'s own reach and travel; never for <see cref="FlybyKind.None"/>.</summary>
	public static bool Passes(FlybyKind kind, Vector3 from, Vector3 to, Vector3 origin, Vector3 ear, out Vector3 point) =>
		Passes(from, to, origin, ear, RadiusOf(kind), MinTravelOf(kind), out point);
}
