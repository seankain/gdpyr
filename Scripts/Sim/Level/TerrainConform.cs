using System;
using System.Buffers;

namespace Gdpyr.Sim.Level;

/// <summary>One sample of a shape the ground is levelled to, and the height it is to have.</summary>
public readonly struct ConformAnchor
{
	public readonly int X;
	public readonly int Z;

	/// <summary>The height the sample ends at [m].</summary>
	public readonly float Target;

	public ConformAnchor(int x, int z, float target)
	{
		X = x;
		Z = z;
		Target = target;
	}
}

public enum ConformResult : byte
{
	/// <summary>The ground was levelled to the shape; it may not have needed to move.</summary>
	Ok,

	/// <summary>
	/// The targets are steeper than the skirt between two of the anchors, so no ground
	/// meets them all inside it. Nothing was changed.
	/// </summary>
	TooSteep,
}

/// <summary>
/// Levelling ground to a shape (docs/LEVEL_EDITOR.md §6.3): one operator for a
/// prefab's pad, a road's ribbon, a ramp's strip and a bridgehead. Given anchors q,
/// each with a target S(q), and a skirt grade k:
///
/// <code>
///   L(p) = max over q of  S(q) − k · d(p, q)
///   U(p) = min over q of  S(q) + k · d(p, q)
///   h'(p) = clamp(h(p), L(p), U(p))
/// </code>
///
/// with d flat distance. It applies when the targets are themselves no steeper than
/// k between any two anchors, and then: the anchors end on their targets; ground
/// already between L and U is not touched; between any two samples the ground ends
/// no steeper than the steeper of what it was and k; doing it twice changes
/// nothing; and it reaches no further than max|h − S| / k from the anchors. Each is
/// a test (<c>TerrainConformTests</c>), over the definition written out again there.
///
/// Heights stay <c>float</c> here: the caller rounds what was touched when its edit
/// ends (<see cref="HeightField.Round"/>). Rounded, levelled and rounded again, the
/// ground is what it was after the first rounding — a rounded height that has left
/// [L, U] is put back on the bound it passed, which rounds to the step it came
/// from — and that is what lets a pad be levelled again after every edit near it
/// (§6.5).
/// </summary>
public static class TerrainConform
{
	/// <summary>
	/// The skirt [degrees]. Not a setting (§6.3): a face is at most √2 times as steep
	/// as its steepest edge, so a 30° skirt leaves no face over 39.2°, which is under
	/// what is walkable (<see cref="MapLimits.WalkableSlopeDegrees"/>). A unit and a
	/// body walk onto every pad and every road from any side, with no further check.
	/// </summary>
	public const float SkirtDegrees = 30f;

	/// <summary><see cref="SkirtDegrees"/> as rise over run.</summary>
	public static readonly float SkirtGrade = HeightField.Grade(SkirtDegrees);

	/// <summary>How far past a footprint its pad is level [m] (LE-D10).</summary>
	public const float PadMarginMeters = 1.5f;

	/// <summary>How far past <c>k · d</c> two targets may differ and still be taken: a float's worth, and no more.</summary>
	private const float Slack = 1e-5f;

	/// <summary>Levels the ground to the anchors with the 30° skirt.</summary>
	public static ConformResult Apply(HeightField field, ReadOnlySpan<ConformAnchor> anchors, out SampleRect touched) =>
		Apply(field, anchors, SkirtGrade, out touched);

	/// <summary>
	/// The operator, for anchors that are samples with a target each: a road's ribbon,
	/// a ramp's strip. Anchors off the field are left out; targets are held to LE-D4's
	/// limits. <paramref name="touched"/> is the rectangle of the samples that moved.
	/// </summary>
	public static ConformResult Apply(HeightField field, ReadOnlySpan<ConformAnchor> anchors, float grade,
		out SampleRect touched)
	{
		touched = SampleRect.Empty;
		if (!(grade > 0f))
		{
			throw new ArgumentOutOfRangeException(nameof(grade), "a skirt has a grade over 0");
		}

		int width = field.Width;
		float[] heights = field.Heights;
		float[] targets = ArrayPool<float>.Shared.Rent(heights.Length);
		try
		{
			Array.Fill(targets, float.NaN, 0, heights.Length);
			SampleRect shape = SampleRect.Empty;
			float lowest = float.MaxValue;
			float highest = float.MinValue;
			foreach (ConformAnchor anchor in anchors)
			{
				if (!field.Bounds.Contains(anchor.X, anchor.Z))
				{
					continue;
				}

				float target = Math.Clamp(anchor.Target, MapLimits.MinHeightMeters, MapLimits.MaxHeightMeters);
				int i = (anchor.Z * width) + anchor.X;
				if (!float.IsNaN(targets[i]) && targets[i] != target)
				{
					return ConformResult.TooSteep;
				}

				targets[i] = target;
				shape = shape.With(anchor.X, anchor.Z);
				lowest = MathF.Min(lowest, target);
				highest = MathF.Max(highest, target);
			}

			if (shape.IsEmpty)
			{
				return ConformResult.Ok;
			}

			// The condition: L ≤ U at every anchor, which is S no steeper than k between any two.
			for (int z = shape.MinZ; z <= shape.MaxZ; z++)
			{
				for (int x = shape.MinX; x <= shape.MaxX; x++)
				{
					float target = targets[(z * width) + x];
					if (float.IsNaN(target))
					{
						continue;
					}

					float most = MathF.Max(target - lowest, highest - target);
					Bounds(targets, width, shape, x, z, most / grade, grade, out float lower, out float upper);
					if (lower > target + Allowed(target) || upper < target - Allowed(target))
					{
						return ConformResult.TooSteep;
					}
				}
			}

			// Nothing further than max|h − S| / k from the shape can move.
			Range(heights, out float floor, out float ceiling);
			int reach = (int)MathF.Ceiling(MathF.Max(ceiling - lowest, highest - floor) / grade) + 1;
			SampleRect region = shape.Grown(reach).Intersect(field.Bounds);
			for (int z = region.MinZ; z <= region.MaxZ; z++)
			{
				for (int x = region.MinX; x <= region.MaxX; x++)
				{
					int i = (z * width) + x;
					float before = heights[i];
					float after = targets[i];
					if (float.IsNaN(after))
					{
						// A sample that neither the highest nor the lowest target could move from here is left.
						float most = MathF.Max(before - lowest, highest - before);
						if (most <= grade * Distance(shape, x, z))
						{
							continue;
						}

						Bounds(targets, width, shape, x, z, most / grade, grade, out float lower, out float upper);
						after = MathF.Max(MathF.Min(before, upper), lower);
					}

					if (after != before)
					{
						heights[i] = after;
						touched = touched.With(x, z);
					}
				}
			}

			return ConformResult.Ok;
		}
		finally
		{
			ArrayPool<float>.Shared.Return(targets);
		}
	}

	/// <summary>A pad under a rectangle, level at <paramref name="level"/>, with the 30° skirt and LE-D10's margin.</summary>
	public static SampleRect Pad(HeightField field, in Footprint footprint, float level) =>
		Pad(field, footprint, level, PadMarginMeters, SkirtGrade);

	/// <summary>
	/// The operator for a pad: one target, and d the distance to the footprint less
	/// the margin, so that L and U are <c>level ∓ k · max(0, distance − margin)</c>.
	/// Every sample within the margin of the footprint ends at the level, exactly.
	///
	/// The distance is to the rectangle itself and not to the nearest levelled sample.
	/// Measured from samples, every stair of a turned edge is a crease in the skirt
	/// and a face on it can be the whole √2 over the skirt's grade; measured from the
	/// rectangle the skirt is a plane along a side and a cone round a corner, and its
	/// steepest face is 1.16 times it (§10).
	/// </summary>
	public static SampleRect Pad(HeightField field, in Footprint footprint, float level, float margin, float grade) =>
		LevelUnder(field, new PadShape(footprint), level, margin, grade);

	/// <summary>A pad under a disc: a resource node's, which levels its whole capture radius (§6.5).</summary>
	public static SampleRect Disc(HeightField field, float x, float z, float radius, float level) =>
		Disc(field, x, z, radius, level, PadMarginMeters, SkirtGrade);

	/// <summary>The same operator, with d the distance to a disc less the margin.</summary>
	public static SampleRect Disc(HeightField field, float x, float z, float radius, float level, float margin,
		float grade) =>
		LevelUnder(field, new PadShape(x, z, radius), level, margin, grade);

	private static SampleRect LevelUnder(HeightField field, in PadShape shape, float level, float margin, float grade)
	{
		if (!(grade > 0f))
		{
			throw new ArgumentOutOfRangeException(nameof(grade), "a skirt has a grade over 0");
		}

		level = Math.Clamp(level, MapLimits.MinHeightMeters, MapLimits.MaxHeightMeters);
		margin = MathF.Max(margin, 0f);
		float[] heights = field.Heights;
		Range(heights, out float floor, out float ceiling);
		float reach = shape.Radius + margin + (MathF.Max(ceiling - level, level - floor) / grade) + 1f;
		SampleRect region = new SampleRect(
			(int)MathF.Floor(shape.X - reach - field.OriginX), (int)MathF.Floor(shape.Z - reach - field.OriginZ),
			(int)MathF.Ceiling(shape.X + reach - field.OriginX), (int)MathF.Ceiling(shape.Z + reach - field.OriginZ))
			.Intersect(field.Bounds);

		SampleRect touched = SampleRect.Empty;
		for (int z = region.MinZ; z <= region.MaxZ; z++)
		{
			for (int x = region.MinX; x <= region.MaxX; x++)
			{
				int i = (z * field.Width) + x;
				float before = heights[i];
				float skirt = grade * MathF.Max(shape.DistanceTo(field.WorldX(x), field.WorldZ(z)) - margin, 0f);
				float after = Math.Clamp(before, level - skirt, level + skirt);
				if (after != before)
				{
					heights[i] = after;
					touched = touched.With(x, z);
				}
			}
		}

		return touched;
	}

	/// <summary>
	/// L and U at one sample, over the anchors within <paramref name="reach"/> of it:
	/// one further off cannot be the one that binds, by the caller's bound on how far
	/// a target is from the sample in height.
	/// </summary>
	private static void Bounds(float[] targets, int width, in SampleRect shape, int x, int z, float reach, float grade,
		out float lower, out float upper)
	{
		lower = float.MinValue;
		upper = float.MaxValue;
		int span = (int)MathF.Ceiling(reach) + 1;
		SampleRect near = new SampleRect(x - span, z - span, x + span, z + span).Intersect(shape);
		for (int az = near.MinZ; az <= near.MaxZ; az++)
		{
			float dz = az - z;
			for (int ax = near.MinX; ax <= near.MaxX; ax++)
			{
				float target = targets[(az * width) + ax];
				if (float.IsNaN(target) || (ax == x && az == z))
				{
					continue;
				}

				float dx = ax - x;
				float skirt = grade * MathF.Sqrt((dx * dx) + (dz * dz));
				lower = MathF.Max(lower, target - skirt);
				upper = MathF.Min(upper, target + skirt);
			}
		}
	}

	/// <summary>How far a sample is from a rectangle of samples, flat. Zero inside it.</summary>
	private static float Distance(in SampleRect shape, int x, int z)
	{
		float dx = Math.Max(Math.Max(shape.MinX - x, x - shape.MaxX), 0);
		float dz = Math.Max(Math.Max(shape.MinZ - z, z - shape.MaxZ), 0);
		return MathF.Sqrt((dx * dx) + (dz * dz));
	}

	private static float Allowed(float target) => Slack * (1f + MathF.Abs(target));

	private static void Range(float[] heights, out float floor, out float ceiling)
	{
		floor = float.MaxValue;
		ceiling = float.MinValue;
		foreach (float height in heights)
		{
			floor = MathF.Min(floor, height);
			ceiling = MathF.Max(ceiling, height);
		}
	}

	/// <summary>What a pad is measured from: a rectangle, or a disc.</summary>
	private readonly struct PadShape
	{
		private readonly bool _disc;
		private readonly float _halfWidth;
		private readonly float _halfDepth;
		private readonly float _cos;
		private readonly float _sin;

		public readonly float X;
		public readonly float Z;

		/// <summary>The furthest the shape reaches from (<see cref="X"/>, <see cref="Z"/>).</summary>
		public readonly float Radius;

		public PadShape(in Footprint footprint)
		{
			_disc = false;
			_halfWidth = footprint.HalfWidth;
			_halfDepth = footprint.HalfDepth;
			_cos = MathF.Cos(footprint.Yaw);
			_sin = MathF.Sin(footprint.Yaw);
			X = footprint.Center.X;
			Z = footprint.Center.Z;
			Radius = MathF.Sqrt((_halfWidth * _halfWidth) + (_halfDepth * _halfDepth));
		}

		public PadShape(float x, float z, float radius)
		{
			_disc = true;
			_halfWidth = 0f;
			_halfDepth = 0f;
			_cos = 1f;
			_sin = 0f;
			X = x;
			Z = z;
			Radius = MathF.Max(radius, 0f);
		}

		/// <summary>
		/// How far a point is from the shape, flat; zero on or inside it. For a rectangle,
		/// <see cref="Footprint.DistanceTo"/> with its two axes worked out once: a pad asks
		/// this of every sample its skirt could reach.
		/// </summary>
		public float DistanceTo(float x, float z)
		{
			float dx = x - X;
			float dz = z - Z;
			if (_disc)
			{
				return MathF.Max(MathF.Sqrt((dx * dx) + (dz * dz)) - Radius, 0f);
			}

			float outX = MathF.Max(MathF.Abs((dx * _cos) - (dz * _sin)) - _halfWidth, 0f);
			float outZ = MathF.Max(MathF.Abs((dx * _sin) + (dz * _cos)) - _halfDepth, 0f);
			return MathF.Sqrt((outX * outX) + (outZ * outZ));
		}
	}
}
