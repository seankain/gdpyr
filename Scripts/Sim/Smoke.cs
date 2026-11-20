using System;
using Godot;

namespace Gdpyr.Sim;

/// <summary>
/// A smoke grenade's cloud, as numbers every peer and the tests share
/// (docs/OUTPOST.md §4.3). The drawing (<see cref="ExplosionKind.Smoke"/>) is made to the
/// same numbers, so what a person sees and what the server's sight lines meet are one
/// cloud.
/// </summary>
public static class SmokeRules
{
	/// <summary>The cloud's radius once it has bloomed [m]: a screen nine metres across.</summary>
	public const float RadiusMeters = 4.5f;

	/// <summary>Its radius the moment it goes off [m].</summary>
	public const float StartRadiusMeters = 1f;

	/// <summary>How long it takes to grow to <see cref="RadiusMeters"/> [s].</summary>
	public const float BloomSeconds = 1.5f;

	/// <summary>How long it stands, from going off to gone [s].</summary>
	public const float Seconds = 18f;

	/// <summary>Over the last this-many seconds it thins to nothing, shrinking as it goes [s].</summary>
	public const float ThinSeconds = 3f;

	/// <summary>
	/// How far above where the grenade lay the cloud's middle is [m]: a cloud on the floor
	/// hides a standing body's chest and head, and a drone 12 m up looking down sees into
	/// its top before it sees through it.
	/// </summary>
	public const float LiftMeters = 1.2f;

	/// <summary>
	/// How much of a sight line has to pass through the cloud for it to be blocked [m]. A
	/// line that only grazes the edge sees through: smoke is thinnest there.
	/// </summary>
	public const float MinChordMeters = 1f;

	/// <summary>Clouds standing at once, the whole field's. A pool size: more than the units can throw in one cloud's life.</summary>
	public const int Capacity = 24;

	public const int BloomTicks = (int)(BloomSeconds * SimConfig.TickRate);

	public const int LifeTicks = (int)(Seconds * SimConfig.TickRate);

	public const int ThinTicks = (int)(ThinSeconds * SimConfig.TickRate);

	/// <summary>
	/// The cloud's radius <paramref name="ageTicks"/> after it went off: from
	/// <see cref="StartRadiusMeters"/> to <see cref="RadiusMeters"/> over the bloom, held, and
	/// down to nothing over the last <see cref="ThinTicks"/>. Zero before it goes off and
	/// once it is gone.
	/// </summary>
	public static float RadiusAt(long ageTicks)
	{
		if (ageTicks < 0 || ageTicks >= LifeTicks)
		{
			return 0f;
		}

		if (ageTicks < BloomTicks)
		{
			float grown = ageTicks / (float)BloomTicks;
			return StartRadiusMeters + ((RadiusMeters - StartRadiusMeters) * grown);
		}

		long left = LifeTicks - ageTicks;
		return left < ThinTicks ? RadiusMeters * (left / (float)ThinTicks) : RadiusMeters;
	}

	/// <summary>
	/// How much of the segment from <paramref name="from"/> to <paramref name="to"/> lies
	/// inside a sphere [m]: zero for a line that misses it.
	/// </summary>
	public static float ChordInside(Vector3 from, Vector3 to, Vector3 centre, float radius)
	{
		if (radius <= 0f)
		{
			return 0f;
		}

		Vector3 d = to - from;
		float length = d.Length();
		if (length < 1e-5f)
		{
			return 0f;
		}

		Vector3 dir = d / length;
		Vector3 m = from - centre;
		float b = m.Dot(dir);
		float c = m.Dot(m) - (radius * radius);
		float discriminant = (b * b) - c;
		if (discriminant <= 0f)
		{
			return 0f;
		}

		float root = MathF.Sqrt(discriminant);
		float enter = Math.Clamp(-b - root, 0f, length);
		float exit = Math.Clamp(-b + root, 0f, length);
		return MathF.Max(exit - enter, 0f);
	}
}

/// <summary>One cloud on the field.</summary>
public struct SmokeCloud
{
	/// <summary>Where the grenade went off, on the ground.</summary>
	public Vector3 Ground;

	public uint StartTick;

	/// <summary>The middle of the sphere sight lines are tested against.</summary>
	public readonly Vector3 Centre => Ground + (Vector3.Up * SmokeRules.LiftMeters);

	public readonly float RadiusAt(uint tick) => SmokeRules.RadiusAt((long)tick - StartTick);
}

/// <summary>
/// Every smoke cloud standing, and whether one stands between two points
/// (docs/OUTPOST.md §4.3). Server-side: the units' sight, the strategist's fog and the
/// ground bots' sensors ask it after the world ray says the line is clear, so a cloud is
/// one more thing a line of sight can be stopped by. A client draws the clouds and asks
/// nothing: what it is not told about is the fog's business, as ever.
///
/// Fixed storage and allocation-free: it is asked from inside every sight line, which is
/// the hottest question the server has.
/// </summary>
public sealed class SmokeField
{
	private readonly SmokeCloud[] _clouds = new SmokeCloud[SmokeRules.Capacity];

	private uint _tick;

	/// <summary>Clouds standing at the last <see cref="Step"/>.</summary>
	public int Count { get; private set; }

	/// <summary>Clouds let out since the round started, for the debug HUD.</summary>
	public int Released { get; private set; }

	/// <summary>The tick sight lines are asked about: the last one stepped to.</summary>
	public uint Tick => _tick;

	public SmokeCloud At(int index) => _clouds[index];

	/// <summary>A cloud going off at <paramref name="ground"/> on <paramref name="tick"/>. With the pool full, the oldest gives way.</summary>
	public void Add(Vector3 ground, uint tick)
	{
		if (!ground.IsFinite())
		{
			return;
		}

		if (Count >= _clouds.Length)
		{
			int oldest = 0;
			for (int i = 1; i < Count; i++)
			{
				if (_clouds[i].StartTick < _clouds[oldest].StartTick)
				{
					oldest = i;
				}
			}

			RemoveAt(oldest);
		}

		_clouds[Count++] = new SmokeCloud { Ground = ground, StartTick = tick };
		Released++;
		_tick = Math.Max(_tick, tick);
	}

	/// <summary>Moves the field's clock on, and takes away every cloud that has thinned to nothing.</summary>
	public void Step(uint tick)
	{
		_tick = tick;
		for (int i = Count - 1; i >= 0; i--)
		{
			if ((long)tick - _clouds[i].StartTick >= SmokeRules.LifeTicks)
			{
				RemoveAt(i);
			}
		}
	}

	/// <summary>Whether a cloud stops the sight line from <paramref name="from"/> to <paramref name="to"/> now.</summary>
	public bool Blocks(Vector3 from, Vector3 to) => Blocks(from, to, _tick);

	/// <summary>
	/// Whether a cloud standing at <paramref name="tick"/> has at least
	/// <see cref="SmokeRules.MinChordMeters"/> of the line inside it — so a body inside a
	/// cloud is hidden from outside it, and sees nothing out of it either.
	/// </summary>
	public bool Blocks(Vector3 from, Vector3 to, uint tick)
	{
		for (int i = 0; i < Count; i++)
		{
			SmokeCloud cloud = _clouds[i];
			if (SmokeRules.ChordInside(from, to, cloud.Centre, cloud.RadiusAt(tick)) >= SmokeRules.MinChordMeters)
			{
				return true;
			}
		}

		return false;
	}

	/// <summary>Whether <paramref name="point"/> is inside a cloud now.</summary>
	public bool Covers(Vector3 point)
	{
		for (int i = 0; i < Count; i++)
		{
			SmokeCloud cloud = _clouds[i];
			float radius = cloud.RadiusAt(_tick);
			if (radius > 0f && point.DistanceSquaredTo(cloud.Centre) <= radius * radius)
			{
				return true;
			}
		}

		return false;
	}

	public void Clear()
	{
		Array.Clear(_clouds);
		Count = 0;
		Released = 0;
		_tick = 0;
	}

	private void RemoveAt(int index)
	{
		_clouds[index] = _clouds[Count - 1];
		_clouds[Count - 1] = default;
		Count--;
	}
}
