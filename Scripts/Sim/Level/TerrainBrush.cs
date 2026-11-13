using System;
using System.Buffers;

namespace Gdpyr.Sim.Level;

public enum BrushKind : byte
{
	/// <summary>Adds or removes height under a falloff. Its grade follows from its settings.</summary>
	Sculpt,

	/// <summary>Raises or lowers a cone of a set grade: the one brush that can promise a grade.</summary>
	GradeLocked,

	/// <summary>Moves each sample towards the mean of its neighbours.</summary>
	Smooth,

	/// <summary>Moves each sample towards the height under the cursor when the stroke began.</summary>
	Level,

	/// <summary>Two clicks and a width: a strip on the straight line between their heights.</summary>
	Ramp,
}

/// <summary>
/// What a brush is set to (docs/LEVEL_EDITOR.md §6.2). Every kind reads the radius;
/// sculpt reads the core and the rate, grade-locked the grade and the rate, smooth
/// and level the core and the strength, and a ramp the radius alone, as half its
/// width.
/// </summary>
public struct BrushSettings
{
	public const float MinRadiusMeters = 2f;
	public const float MaxRadiusMeters = 64f;
	public const float MinRateMetersPerSecond = 0.25f;
	public const float MaxRateMetersPerSecond = 8f;
	public const float MaxCore = 0.9f;
	public const float MinGradeDegrees = 5f;
	public const float MaxGradeDegrees = 70f;

	public BrushKind Kind;

	/// <summary>How far from the cursor the brush reaches [m]: the edited area, bigger or smaller.</summary>
	public float RadiusMeters;

	/// <summary>The share of the radius that takes the brush whole; the falloff is the rest.</summary>
	public float Core;

	/// <summary>How fast the ground under the cursor rises or falls while the button is down [m/s].</summary>
	public float RateMetersPerSecond;

	/// <summary>The slope a grade-locked brush makes [degrees].</summary>
	public float GradeDegrees;

	/// <summary>How far a dab of smooth or level moves a sample towards where it is going, of 1.</summary>
	public float Strength;

	/// <summary>The same settings with each pulled into its range: what a slider or a key can leave them at.</summary>
	public readonly BrushSettings Clamped() => new()
	{
		Kind = Kind,
		RadiusMeters = Within(RadiusMeters, MinRadiusMeters, MaxRadiusMeters),
		Core = Within(Core, 0f, MaxCore),
		RateMetersPerSecond = Within(RateMetersPerSecond, MinRateMetersPerSecond, MaxRateMetersPerSecond),
		GradeDegrees = Within(GradeDegrees, MinGradeDegrees, MaxGradeDegrees),
		Strength = Within(Strength, 0f, 1f),
	};

	/// <summary>A clamp that takes a NaN to the low end and not through.</summary>
	private static float Within(float value, float min, float max) => value > min ? MathF.Min(value, max) : min;
}

/// <summary>A named starting point for a brush: a button in the terrain tool (§8.3).</summary>
public readonly struct BrushPreset
{
	public readonly string Name;
	public readonly BrushSettings Settings;

	public BrushPreset(string name, BrushSettings settings)
	{
		Name = name;
		Settings = settings;
	}
}

/// <summary>
/// One press of the button: the brush as it was set when the press began, which way
/// it works, and what the level brush levels to.
/// </summary>
public readonly struct BrushStroke
{
	public readonly BrushSettings Brush;

	/// <summary>The right button: lower where the left raises. Smooth and level work the same either way.</summary>
	public readonly bool Lower;

	/// <summary>The ground's height under the cursor when the stroke began [m].</summary>
	public readonly float LevelMeters;

	public BrushStroke(BrushSettings brush, bool lower, float levelMeters)
	{
		Brush = brush;
		Lower = lower;
		LevelMeters = levelMeters;
	}
}

public enum RampResult : byte
{
	Ok,

	/// <summary>The two clicks are one point, or the strip between them holds no sample.</summary>
	TooSmall,

	/// <summary>The line between the clicks is steeper than the skirt its sides are given (§6.3).</summary>
	TooSteep,
}

/// <summary>
/// The terrain brushes (docs/LEVEL_EDITOR.md §6.2). A dab is a function of the
/// heights, a centre, the brush and a time step, and returns the rectangle of the
/// samples it moved; a stroke is dabs at the cursor. Nothing here keeps anything
/// between dabs, so a stroke replayed is the same ground, and what a brush promises
/// about a grade is a test over random strokes (<c>TerrainBrushTests</c>) and not a
/// thing to find out by sculpting.
///
/// Every dab holds what it writes to LE-D4's limits. None rounds: the editor rounds
/// the rectangle a stroke touched when the stroke ends (<see cref="HeightField.Round"/>),
/// so a slow brush accumulates in between.
/// </summary>
public static class TerrainBrush
{
	/// <summary>
	/// The outer part of a grade-locked brush's disc that it holds its cone under and
	/// does not move [m]. More than a cell's diagonal, so that an edge of the
	/// collider's triangles has both its ends in the disc or neither end moved.
	/// </summary>
	public const float RimMeters = 1.5f;

	/// <summary>
	/// What a grade-locked brush's cone is rounded over at its tip [m]. Sharp, a 45°
	/// cone's four faces at its tip are 54.7°, the whole √2; rounded over 2 m they
	/// are 45.4° (§10).
	/// </summary>
	public const float TipRadiusMeters = 2f;

	/// <summary>§6.2's table. A ramp is as wide as its ring: twice its radius.</summary>
	public static readonly BrushPreset[] Presets =
	{
		new("Plain", Sculpt(32f, 0f, 1f)),
		new("Hill", Sculpt(16f, 0f, 2f)),
		new("Bank", Sculpt(16f, 0.5f, 2f)),
		new("Bluff", Sculpt(16f, 0.8f, 2f)),
		new("Mound 10°", Locked(16f, 10f)),
		new("Mound 20°", Locked(16f, 20f)),
		new("Mound 30°", Locked(16f, 30f)),
		new("Cliff 60°", Locked(8f, 60f)),
		new("Smooth", Soft(BrushKind.Smooth)),
		new("Level", Soft(BrushKind.Level)),
		new("Ramp", Soft(BrushKind.Ramp)),
	};

	private static BrushSettings Sculpt(float radius, float core, float rate) => new()
	{
		Kind = BrushKind.Sculpt, RadiusMeters = radius, Core = core, RateMetersPerSecond = rate, GradeDegrees = 30f, Strength = 0.5f,
	};

	private static BrushSettings Locked(float radius, float degrees) => new()
	{
		Kind = BrushKind.GradeLocked, RadiusMeters = radius, Core = 0f, RateMetersPerSecond = 2f, GradeDegrees = degrees, Strength = 0.5f,
	};

	private static BrushSettings Soft(BrushKind kind) => new()
	{
		Kind = kind, RadiusMeters = 8f, Core = 0f, RateMetersPerSecond = 2f, GradeDegrees = 30f, Strength = 0.5f,
	};

	// ---- a stroke -----------------------------------------------------------------------

	/// <summary>Starts a stroke at the cursor: the brush pulled into its ranges, and the height a level brush will level to.</summary>
	public static BrushStroke Begin(HeightField field, in BrushSettings brush, float x, float z, bool lower) =>
		new(brush.Clamped(), lower, field.Sample(x, z));

	/// <summary>
	/// One dab of a stroke at (x, z), <paramref name="dt"/> seconds after the last.
	/// A ramp is not dabbed: it is <see cref="Ramp"/>, once, between two clicks.
	/// </summary>
	public static SampleRect Dab(HeightField field, in BrushStroke stroke, float x, float z, float dt)
	{
		BrushSettings brush = stroke.Brush;
		float rise = brush.RateMetersPerSecond * (dt > 0f ? dt : 0f);
		return brush.Kind switch
		{
			BrushKind.Sculpt => SculptDab(field, x, z, brush.RadiusMeters, brush.Core, stroke.Lower ? -rise : rise),
			BrushKind.GradeLocked => GradeLockedDab(field, x, z, brush.RadiusMeters, HeightField.Grade(brush.GradeDegrees),
				rise, stroke.Lower),
			BrushKind.Smooth => SmoothDab(field, x, z, brush.RadiusMeters, brush.Core, brush.Strength),
			BrushKind.Level => LevelDab(field, x, z, brush.RadiusMeters, brush.Core, brush.Strength, stroke.LevelMeters),
			_ => SampleRect.Empty,
		};
	}

	// ---- sculpt -------------------------------------------------------------------------

	/// <summary>
	/// A brush's falloff: 1 inside <c>core · radius</c>, a smoothstep down to 0 at the
	/// radius. A smoothstep's steepest slope is 1.5 over its width.
	/// </summary>
	public static float Weight(float distance, float radius, float core)
	{
		if (!(radius > 0f) || !(distance < radius))
		{
			return 0f;
		}

		core = core > 0f ? MathF.Min(core, 0.999f) : 0f;
		float t = distance / radius;
		if (t <= core)
		{
			return 1f;
		}

		float s = (t - core) / (1f - core);
		return 1f - (s * s * (3f - (2f * s)));
	}

	/// <summary>
	/// The steepest grade a sculpt brush has made once its centre has risen
	/// <paramref name="rise"/>: <c>1.5 · H / ((1 − core) · R)</c>. A wider or softer
	/// brush is a shallower grade; a narrower or harder one is steeper.
	/// </summary>
	public static float SculptGrade(float rise, float radius, float core) =>
		1.5f * MathF.Abs(rise) / ((1f - core) * radius);

	/// <summary>Sculpt: <c>h += amount · w(r / R)</c>, <paramref name="amount"/> being ±rate · dt.</summary>
	public static SampleRect SculptDab(HeightField field, float x, float z, float radius, float core, float amount)
	{
		SampleRect disc = float.IsNaN(amount) ? SampleRect.Empty : Disc(field, x, z, radius);
		SampleRect touched = SampleRect.Empty;
		for (int sz = disc.MinZ; sz <= disc.MaxZ; sz++)
		{
			for (int sx = disc.MinX; sx <= disc.MaxX; sx++)
			{
				float weight = Weight(Distance(field, sx, sz, x, z), radius, core);
				if (weight <= 0f)
				{
					continue;
				}

				float before = field[sx, sz];
				float after = Math.Clamp(before + (amount * weight), MapLimits.MinHeightMeters, MapLimits.MaxHeightMeters);
				if (after != before)
				{
					field[sx, sz] = after;
					touched = touched.With(sx, sz);
				}
			}
		}

		return touched;
	}

	// ---- grade-locked -------------------------------------------------------------------

	/// <summary>A cone of grade 1, rounded over <paramref name="tip"/> at its apex: how far under the apex it is at a distance. Nowhere steeper than 1.</summary>
	public static float Cone(float distance, float tip) =>
		MathF.Sqrt((distance * distance) + (tip * tip)) - tip;

	/// <summary>
	/// Grade-locked. With k the grade, r the distance from the dab's centre and φ the
	/// rounded cone:
	///
	/// <code>
	///   raise:  apex = min(held + rise,  min over rim samples q of  h(q) + k · φ(r_q))
	///           h' = max(h, apex − k · φ(r))    inside the rim
	///   lower:  the mirror image
	/// </code>
	///
	/// The rim is the disc's outer <see cref="RimMeters"/>. Holding the apex by every
	/// rim sample puts the cone at or under the ground on the whole rim, so only
	/// samples inside it move, and no edge ends steeper than the steeper of k and what
	/// it was: |max(f, g)(a) − max(f, g)(b)| ≤ max(|f(a) − f(b)|, |g(a) − g(b)|), and
	/// the cone is no steeper than k between any two points.
	///
	/// <c>held</c> is the apex the ground about the centre already holds a cone of
	/// this grade up to: the least of h(q) + k · φ(r_q) over the corners of the cell
	/// the centre is in. Over a sample of level ground that is the ground's height.
	/// It is not simply the height under the cursor because a cursor is seldom over a
	/// sample: between four of them the ground under it is their triangles, which lie
	/// under the cone's tip by up to k · φ(0.71), and a brush that rose from there by
	/// less than that a dab would never rise at all.
	/// </summary>
	public static SampleRect GradeLockedDab(HeightField field, float x, float z, float radius, float grade, float rise,
		bool lower, float tipRadius = TipRadiusMeters)
	{
		// Lowering is raising the ground turned over: every height below is times this.
		float sign = lower ? -1f : 1f;
		float limit = lower ? -MapLimits.MinHeightMeters : MapLimits.MaxHeightMeters;
		tipRadius = MathF.Max(tipRadius, 0f);

		int cellX = Math.Clamp((int)MathF.Floor(x - field.OriginX), 0, field.Width - 2);
		int cellZ = Math.Clamp((int)MathF.Floor(z - field.OriginZ), 0, field.Depth - 2);
		float apex = float.MaxValue;
		for (int corner = 0; corner < 4; corner++)
		{
			int sx = cellX + (corner & 1);
			int sz = cellZ + (corner >> 1);
			apex = MathF.Min(apex, (sign * field[sx, sz]) + (grade * Cone(Distance(field, sx, sz, x, z), tipRadius)));
		}

		apex += rise > 0f ? rise : 0f;

		SampleRect disc = Disc(field, x, z, radius);
		float inner = radius - RimMeters;
		for (int sz = disc.MinZ; sz <= disc.MaxZ; sz++)
		{
			for (int sx = disc.MinX; sx <= disc.MaxX; sx++)
			{
				float distance = Distance(field, sx, sz, x, z);
				if (distance > inner && distance <= radius)
				{
					apex = MathF.Min(apex, (sign * field[sx, sz]) + (grade * Cone(distance, tipRadius)));
				}
			}
		}

		apex = MathF.Min(apex, limit);

		SampleRect touched = SampleRect.Empty;
		for (int sz = disc.MinZ; sz <= disc.MaxZ; sz++)
		{
			for (int sx = disc.MinX; sx <= disc.MaxX; sx++)
			{
				float distance = Distance(field, sx, sz, x, z);
				if (distance > inner)
				{
					continue;
				}

				float cone = apex - (grade * Cone(distance, tipRadius));
				if (sign * field[sx, sz] < cone)
				{
					field[sx, sz] = sign * cone;
					touched = touched.With(sx, sz);
				}
			}
		}

		return touched;
	}

	// ---- smooth, level --------------------------------------------------------------------

	/// <summary>
	/// Smooth: each sample moves towards the mean of its four neighbours by
	/// <c>w · strength</c> ≤ 1 — a convex combination, so it never raises the highest
	/// or lowers the lowest sample it touches. Every sample reads the ground as it was
	/// before the dab. At the field's edge a sample has its neighbours along the edge
	/// only, and a corner none: an even slope is left alone there as it is anywhere.
	/// A dab is that share whatever time it stood for: how often a stroke dabs is the
	/// editor's to set, here and for <see cref="LevelDab"/>.
	/// </summary>
	public static SampleRect SmoothDab(HeightField field, float x, float z, float radius, float core, float strength)
	{
		SampleRect disc = Disc(field, x, z, radius);
		if (disc.IsEmpty)
		{
			return SampleRect.Empty;
		}

		strength = strength > 0f ? MathF.Min(strength, 1f) : 0f;
		SampleRect read = disc.Grown(1).Intersect(field.Bounds);
		float[] before = ArrayPool<float>.Shared.Rent(read.Count);
		try
		{
			for (int sz = read.MinZ; sz <= read.MaxZ; sz++)
			{
				Array.Copy(field.Heights, field.Index(read.MinX, sz), before, (sz - read.MinZ) * read.Width, read.Width);
			}

			SampleRect touched = SampleRect.Empty;
			for (int sz = disc.MinZ; sz <= disc.MaxZ; sz++)
			{
				for (int sx = disc.MinX; sx <= disc.MaxX; sx++)
				{
					float weight = Weight(Distance(field, sx, sz, x, z), radius, core) * strength;
					if (weight <= 0f)
					{
						continue;
					}

					int i = ((sz - read.MinZ) * read.Width) + (sx - read.MinX);
					double sum = 0d;
					int neighbours = 0;
					if (sx > 0 && sx < field.Width - 1)
					{
						sum += (double)before[i - 1] + before[i + 1];
						neighbours += 2;
					}

					if (sz > 0 && sz < field.Depth - 1)
					{
						sum += (double)before[i - read.Width] + before[i + read.Width];
						neighbours += 2;
					}

					if (neighbours == 0)
					{
						continue;
					}

					float was = before[i];
					float after = Towards(was, (float)(sum / neighbours), weight);
					if (after != was)
					{
						field[sx, sz] = after;
						touched = touched.With(sx, sz);
					}
				}
			}

			return touched;
		}
		finally
		{
			ArrayPool<float>.Shared.Return(before);
		}
	}

	/// <summary>Level: each sample moves towards <paramref name="target"/> by <c>w · strength</c> ≤ 1, and never past it.</summary>
	public static SampleRect LevelDab(HeightField field, float x, float z, float radius, float core, float strength,
		float target)
	{
		SampleRect disc = float.IsNaN(target) ? SampleRect.Empty : Disc(field, x, z, radius);
		strength = strength > 0f ? MathF.Min(strength, 1f) : 0f;
		target = Math.Clamp(target, MapLimits.MinHeightMeters, MapLimits.MaxHeightMeters);
		SampleRect touched = SampleRect.Empty;
		for (int sz = disc.MinZ; sz <= disc.MaxZ; sz++)
		{
			for (int sx = disc.MinX; sx <= disc.MaxX; sx++)
			{
				float weight = Weight(Distance(field, sx, sz, x, z), radius, core) * strength;
				if (weight <= 0f)
				{
					continue;
				}

				float before = field[sx, sz];
				float after = Towards(before, target, weight);
				if (after != before)
				{
					field[sx, sz] = after;
					touched = touched.With(sx, sz);
				}
			}
		}

		return touched;
	}

	// ---- ramp -----------------------------------------------------------------------------

	/// <summary>
	/// Ramp: the strip <paramref name="width"/> wide between two clicks is anchored to
	/// the straight line between the ground's heights at them and applied with §6.3,
	/// so its sides and its ends are skirted at 30°. Refused, with nothing changed,
	/// when that line is steeper than the skirt.
	/// </summary>
	public static RampResult Ramp(HeightField field, float fromX, float fromZ, float toX, float toZ, float width,
		out SampleRect touched)
	{
		touched = SampleRect.Empty;
		float alongX = toX - fromX;
		float alongZ = toZ - fromZ;
		float length = MathF.Sqrt((alongX * alongX) + (alongZ * alongZ));
		if (!(length > 1e-3f) || !(width >= 0f))
		{
			return RampResult.TooSmall;
		}

		alongX /= length;
		alongZ /= length;
		float from = field.Sample(fromX, fromZ);
		float to = field.Sample(toX, toZ);
		if (MathF.Abs(to - from) > TerrainConform.SkirtGrade * length)
		{
			return RampResult.TooSteep;
		}

		float half = width * 0.5f;
		SampleRect strip = new SampleRect(
			(int)MathF.Floor(MathF.Min(fromX, toX) - half - field.OriginX),
			(int)MathF.Floor(MathF.Min(fromZ, toZ) - half - field.OriginZ),
			(int)MathF.Ceiling(MathF.Max(fromX, toX) + half - field.OriginX),
			(int)MathF.Ceiling(MathF.Max(fromZ, toZ) + half - field.OriginZ)).Intersect(field.Bounds);
		if (strip.IsEmpty)
		{
			return RampResult.TooSmall;
		}

		ConformAnchor[] anchors = ArrayPool<ConformAnchor>.Shared.Rent(strip.Count);
		try
		{
			int count = 0;
			for (int sz = strip.MinZ; sz <= strip.MaxZ; sz++)
			{
				for (int sx = strip.MinX; sx <= strip.MaxX; sx++)
				{
					float dx = field.WorldX(sx) - fromX;
					float dz = field.WorldZ(sz) - fromZ;
					float along = (dx * alongX) + (dz * alongZ);
					float across = (dx * alongZ) - (dz * alongX);
					if (along >= 0f && along <= length && MathF.Abs(across) <= half)
					{
						anchors[count++] = new ConformAnchor(sx, sz, from + ((to - from) * (along / length)));
					}
				}
			}

			if (count == 0)
			{
				return RampResult.TooSmall;
			}

			return TerrainConform.Apply(field, anchors.AsSpan(0, count), out touched) == ConformResult.Ok
				? RampResult.Ok
				: RampResult.TooSteep;
		}
		finally
		{
			ArrayPool<ConformAnchor>.Shared.Return(anchors);
		}
	}

	// ---- shared ---------------------------------------------------------------------------

	/// <summary>From <paramref name="from"/> towards <paramref name="to"/> by <paramref name="share"/> of the way, and never past either.</summary>
	private static float Towards(float from, float to, float share)
	{
		float moved = from + ((to - from) * share);
		return from <= to ? Math.Clamp(moved, from, to) : Math.Clamp(moved, to, from);
	}

	private static float Distance(HeightField field, int sampleX, int sampleZ, float x, float z)
	{
		float dx = field.WorldX(sampleX) - x;
		float dz = field.WorldZ(sampleZ) - z;
		return MathF.Sqrt((dx * dx) + (dz * dz));
	}

	/// <summary>The samples a disc could reach: its square, on the field.</summary>
	private static SampleRect Disc(HeightField field, float x, float z, float radius)
	{
		if (!(radius > 0f) || float.IsNaN(x) || float.IsNaN(z))
		{
			return SampleRect.Empty;
		}

		return new SampleRect(
			(int)MathF.Floor(x - radius - field.OriginX), (int)MathF.Floor(z - radius - field.OriginZ),
			(int)MathF.Ceiling(x + radius - field.OriginX), (int)MathF.Ceiling(z + radius - field.OriginZ))
			.Intersect(field.Bounds);
	}
}
