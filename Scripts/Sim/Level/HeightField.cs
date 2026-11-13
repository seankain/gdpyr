using System;
using Godot;

namespace Gdpyr.Sim.Level;

/// <summary>
/// A rectangle of samples, both corners included: what an edit touched. What undo
/// keeps before and after, what is rounded when a stroke ends and what is given
/// new triangles (docs/LEVEL_EDITOR.md §6.2, §8.3, §8.6).
/// </summary>
public readonly struct SampleRect : IEquatable<SampleRect>
{
	public readonly int MinX;
	public readonly int MinZ;
	public readonly int MaxX;
	public readonly int MaxZ;

	/// <summary>No sample at all: what an edit that changed nothing returns.</summary>
	public static readonly SampleRect Empty = new(0, 0, -1, -1);

	public SampleRect(int minX, int minZ, int maxX, int maxZ)
	{
		MinX = minX;
		MinZ = minZ;
		MaxX = maxX;
		MaxZ = maxZ;
	}

	public bool IsEmpty => MaxX < MinX || MaxZ < MinZ;

	/// <summary>Samples across, along x.</summary>
	public int Width => IsEmpty ? 0 : MaxX - MinX + 1;

	/// <summary>Samples deep, along z.</summary>
	public int Depth => IsEmpty ? 0 : MaxZ - MinZ + 1;

	public int Count => Width * Depth;

	public bool Contains(int x, int z) => x >= MinX && x <= MaxX && z >= MinZ && z <= MaxZ;

	/// <summary>The smallest rectangle holding this one and the sample (x, z).</summary>
	public SampleRect With(int x, int z) =>
		IsEmpty
			? new SampleRect(x, z, x, z)
			: new SampleRect(Math.Min(MinX, x), Math.Min(MinZ, z), Math.Max(MaxX, x), Math.Max(MaxZ, z));

	/// <summary>The smallest rectangle holding both: what a stroke of dabs touched.</summary>
	public SampleRect Union(in SampleRect other)
	{
		if (IsEmpty)
		{
			return other;
		}

		return other.IsEmpty
			? this
			: new SampleRect(Math.Min(MinX, other.MinX), Math.Min(MinZ, other.MinZ),
				Math.Max(MaxX, other.MaxX), Math.Max(MaxZ, other.MaxZ));
	}

	/// <summary>What both hold; <see cref="Empty"/> when they are apart.</summary>
	public SampleRect Intersect(in SampleRect other)
	{
		var both = new SampleRect(Math.Max(MinX, other.MinX), Math.Max(MinZ, other.MinZ),
			Math.Min(MaxX, other.MaxX), Math.Min(MaxZ, other.MaxZ));
		return IsEmpty || other.IsEmpty || both.IsEmpty ? Empty : both;
	}

	/// <summary>This rectangle with <paramref name="samples"/> more on every side.</summary>
	public SampleRect Grown(int samples) =>
		IsEmpty ? Empty : new SampleRect(MinX - samples, MinZ - samples, MaxX + samples, MaxZ + samples);

	public bool Equals(SampleRect other) =>
		(IsEmpty && other.IsEmpty)
		|| (MinX == other.MinX && MinZ == other.MinZ && MaxX == other.MaxX && MaxZ == other.MaxZ);

	public override bool Equals(object obj) => obj is SampleRect other && Equals(other);

	public override int GetHashCode() => IsEmpty ? 0 : HashCode.Combine(MinX, MinZ, MaxX, MaxZ);

	public static bool operator ==(SampleRect a, SampleRect b) => a.Equals(b);

	public static bool operator !=(SampleRect a, SampleRect b) => !a.Equals(b);

	public override string ToString() => IsEmpty ? "(empty)" : $"({MinX}, {MinZ})-({MaxX}, {MaxZ})";
}

/// <summary>The three edges of the collider's triangles that leave a sample (docs/LEVEL_EDITOR.md §6).</summary>
public enum HeightEdge : byte
{
	/// <summary>From (x, z) to (x + 1, z): 1 m.</summary>
	AlongX,

	/// <summary>From (x, z) to (x, z + 1): 1 m.</summary>
	AlongZ,

	/// <summary>From (x + 1, z) to (x, z + 1), the cell's diagonal: √2 m.</summary>
	Diagonal,
}

/// <summary>
/// The ground of a map made in the game (docs/LEVEL_EDITOR.md §6.1): a grid of
/// heights 1 m apart, sample (x, z) at index <c>z · Width + x</c> and at
/// (x − (Width − 1) / 2, z − (Depth − 1) / 2) m, which for the 241 × 241 a map has
/// is (x − 120, z − 120). That is a heightmap's layout, and a cell is the two
/// triangles a heightmap makes of it — (x,z) (x+1,z) (x,z+1) and
/// (x+1,z) (x+1,z+1) (x,z+1) — which are the ones the collider is built from
/// (LE-D5). Everything here that asks where the ground is asks those triangles.
///
/// Heights are <c>float</c> metres. In a document each is a whole number of steps
/// of 1/256 m, every one of which a <c>float</c> holds exactly; while a stroke is
/// down they are whatever the brush left, so that a slow brush accumulates, and
/// <see cref="Round"/> makes the rectangle a stroke touched a document's again.
///
/// A size other than a map's is taken, for the tests' sake: a rule about a grid is
/// the same rule on a smaller one. The file's reader is what refuses another size.
/// </summary>
public sealed class HeightField
{
	private const float RootTwo = 1.41421356f;

	/// <summary>Samples along x.</summary>
	public int Width { get; }

	/// <summary>Samples along z.</summary>
	public int Depth { get; }

	/// <summary>Every height [m], row by row: sample (x, z) at <c>z · Width + x</c>.</summary>
	public float[] Heights { get; }

	/// <summary>A map's ground: 241 × 241 samples, level at 0 m.</summary>
	public HeightField()
		: this(MapLimits.TerrainSamples, MapLimits.TerrainSamples)
	{
	}

	public HeightField(int width, int depth)
	{
		if (width < 2 || depth < 2)
		{
			throw new ArgumentOutOfRangeException(nameof(width), "a height field is at least one cell: 2 x 2 samples");
		}

		Width = width;
		Depth = depth;
		Heights = new float[width * depth];
	}

	/// <summary>The x of sample 0 [m]: the field is centred on the origin, as a heightmap is.</summary>
	public float OriginX => -(Width - 1) * 0.5f;

	/// <summary>The z of sample 0 [m].</summary>
	public float OriginZ => -(Depth - 1) * 0.5f;

	/// <summary>Every sample.</summary>
	public SampleRect Bounds => new(0, 0, Width - 1, Depth - 1);

	public int Index(int x, int z) => (z * Width) + x;

	public float this[int x, int z]
	{
		get => Heights[(z * Width) + x];
		set => Heights[(z * Width) + x] = value;
	}

	/// <summary>Where sample column <paramref name="x"/> is in the world [m].</summary>
	public float WorldX(int x) => x + OriginX;

	/// <summary>Where sample row <paramref name="z"/> is in the world [m].</summary>
	public float WorldZ(int z) => z + OriginZ;

	/// <summary>Sample (x, z) as a point of the ground.</summary>
	public Vector3 Position(int x, int z) => new(WorldX(x), this[x, z], WorldZ(z));

	public HeightField Clone()
	{
		var copy = new HeightField(Width, Depth);
		Array.Copy(Heights, copy.Heights, Heights.Length);
		return copy;
	}

	// ---- stored samples ---------------------------------------------------------------

	/// <summary>A height as it is stored: the nearest step, held to LE-D4's limits.</summary>
	public static short ToSample(float meters)
	{
		float steps = MathF.Round(meters * MapLimits.HeightStepsPerMeter);
		return steps >= MapLimits.MaxHeightSample
			? MapLimits.MaxHeightSample
			: steps > MapLimits.MinHeightSample ? (short)steps : MapLimits.MinHeightSample;
	}

	/// <summary>A stored sample's height [m]. Exact: |q| ≤ 2¹⁵ is 16 bits of a 24-bit significand.</summary>
	public static float ToMeters(short sample) => sample * MapLimits.HeightStepMeters;

	/// <summary>
	/// Makes the rectangle a document's: every height in it the nearest step inside
	/// LE-D4's limits. What ends a stroke (§6.1).
	/// </summary>
	public void Round(in SampleRect samples)
	{
		SampleRect inside = samples.Intersect(Bounds);
		for (int z = inside.MinZ; z <= inside.MaxZ; z++)
		{
			for (int x = inside.MinX; x <= inside.MaxX; x++)
			{
				int i = (z * Width) + x;
				Heights[i] = ToMeters(ToSample(Heights[i]));
			}
		}
	}

	/// <summary>Every height as it is stored, row by row.</summary>
	public short[] ToSamples()
	{
		var samples = new short[Heights.Length];
		for (int i = 0; i < samples.Length; i++)
		{
			samples[i] = ToSample(Heights[i]);
		}

		return samples;
	}

	/// <summary>The ground a file's samples are. Null when there are not width × depth of them.</summary>
	public static HeightField FromSamples(ReadOnlySpan<short> samples, int width, int depth)
	{
		if (width < 2 || depth < 2 || samples.Length != width * depth)
		{
			return null;
		}

		var field = new HeightField(width, depth);
		for (int i = 0; i < samples.Length; i++)
		{
			field.Heights[i] = ToMeters(samples[i]);
		}

		return field;
	}

	// ---- where the ground is ----------------------------------------------------------

	/// <summary>
	/// The ground's height under (x, z) [m]: the collider's triangle there, which is
	/// the first of a cell's pair where the fractions sum to at most 1 and the second
	/// where they do not. A point off the field takes the field's edge.
	/// </summary>
	public float Sample(float x, float z) => (float)SampleGrid((double)x - OriginX, (double)z - OriginZ);

	/// <summary>The same, in the grid's own frame: sample (0, 0) at the origin.</summary>
	private double SampleGrid(double gx, double gz)
	{
		// Written so that a NaN comes out as the first sample and not as an index.
		gx = gx > 0d ? Math.Min(gx, Width - 1) : 0d;
		gz = gz > 0d ? Math.Min(gz, Depth - 1) : 0d;
		int ix = Math.Min((int)gx, Width - 2);
		int iz = Math.Min((int)gz, Depth - 2);
		double u = gx - ix;
		double v = gz - iz;
		int i = (iz * Width) + ix;
		double h00 = Heights[i];
		double h10 = Heights[i + 1];
		double h01 = Heights[i + Width];
		double h11 = Heights[i + Width + 1];
		return u + v <= 1d
			? h00 + (u * (h10 - h00)) + (v * (h01 - h00))
			: h11 + ((1d - u) * (h01 - h11)) + ((1d - v) * (h10 - h11));
	}

	/// <summary>
	/// Where a ray first meets the ground, from either side. The editor's cursor,
	/// which must not depend on when the physics server last saw the ground (§6.1).
	///
	/// Along a ray the ground's height is a broken line whose corners are where the
	/// ray crosses a lane of samples or a cell's diagonal. This walks those corners in
	/// order and asks <see cref="Sample"/>'s arithmetic for the ray's height over the
	/// ground at each: where that changes sign, the ray has met the ground, and
	/// between two corners it is one triangle's plane, so the meeting point is exact.
	/// No cell is picked by flooring a point that lies on a lane — which is how a
	/// heightmap's own walk lets a ray through (§3) — so a corner found a float early
	/// or late moves nothing: the height over the ground is continuous, and a ray
	/// that starts over the ground and ends under it has a sign change to find.
	/// </summary>
	public bool Raycast(Vector3 from, Vector3 direction, float maxDistance, out Vector3 hit)
	{
		hit = default;
		double dx = direction.X;
		double dy = direction.Y;
		double dz = direction.Z;
		double length = Math.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
		if (!(length > 1e-12) || !(maxDistance > 0f))
		{
			return false;
		}

		dx /= length;
		dy /= length;
		dz /= length;
		double ox = (double)from.X - OriginX;
		double oy = from.Y;
		double oz = (double)from.Z - OriginZ;

		// The piece of the ray that is over the field.
		double start = 0d;
		double end = maxDistance;
		if (!Clip(ox, dx, Width - 1, ref start, ref end) || !Clip(oz, dz, Depth - 1, ref start, ref end))
		{
			return false;
		}

		double over = oy + (dy * start) - SampleGrid(ox + (dx * start), oz + (dz * start));
		if (over == 0d)
		{
			hit = At(from, dx, dy, dz, start);
			return true;
		}

		var laneX = new Lane(ox, dx, start);
		var laneZ = new Lane(oz, dz, start);
		var diagonal = new Lane(ox + oz, dx + dz, start);
		double t = start;
		int corners = (4 * (Width + Depth)) + 16;
		for (int i = 0; i < corners; i++)
		{
			double next = Math.Max(Math.Min(Math.Min(laneX.T, laneZ.T), Math.Min(diagonal.T, end)), t);
			laneX.Pass(next);
			laneZ.Pass(next);
			diagonal.Pass(next);

			double overNext = oy + (dy * next) - SampleGrid(ox + (dx * next), oz + (dz * next));
			if (overNext == 0d || (over > 0d) != (overNext > 0d))
			{
				double at = overNext == 0d ? next : t + ((next - t) * (over / (over - overNext)));
				hit = At(from, dx, dy, dz, at);
				return true;
			}

			if (next >= end)
			{
				break;
			}

			t = next;
			over = overNext;
		}

		return false;
	}

	private static Vector3 At(Vector3 from, double dx, double dy, double dz, double t) =>
		new((float)(from.X + (dx * t)), (float)(from.Y + (dy * t)), (float)(from.Z + (dz * t)));

	/// <summary>Narrows [start, end] to where <c>origin + rate · t</c> is inside [0, max]. False when it never is.</summary>
	private static bool Clip(double origin, double rate, double max, ref double start, ref double end)
	{
		if (Math.Abs(rate) < 1e-12)
		{
			return origin >= 0d && origin <= max;
		}

		double a = -origin / rate;
		double b = (max - origin) / rate;
		if (a > b)
		{
			(a, b) = (b, a);
		}

		start = Math.Max(start, a);
		end = Math.Min(end, b);
		return start <= end;
	}

	/// <summary>
	/// One family of the lines a ray's ground can bend at — x a whole number, z a
	/// whole number, or x + z one — as the next distance along the ray at which the
	/// ray crosses one.
	/// </summary>
	private struct Lane
	{
		private readonly double _origin;
		private readonly double _rate;
		private double _line;

		/// <summary>How far along the ray the next line is. Infinite for a ray that runs along the family.</summary>
		public double T;

		public Lane(double origin, double rate, double from)
		{
			_origin = origin;
			_rate = rate;
			if (Math.Abs(rate) < 1e-12)
			{
				_line = 0d;
				T = double.PositiveInfinity;
				return;
			}

			double at = origin + (rate * from);
			_line = rate > 0d ? Math.Floor(at) + 1d : Math.Ceiling(at) - 1d;
			T = (_line - origin) / rate;
		}

		/// <summary>Moves on to the line after, once the walk has reached this one.</summary>
		public void Pass(double reached)
		{
			while (T <= reached)
			{
				_line += _rate > 0d ? 1d : -1d;
				T = (_line - _origin) / _rate;
			}
		}
	}

	// ---- grades -------------------------------------------------------------------------

	/// <summary>
	/// Rise over run along one edge of the collider's triangles, from sample (x, z):
	/// what §6's guarantees are stated for, and exact.
	/// </summary>
	public float EdgeGrade(int x, int z, HeightEdge edge)
	{
		int i = (z * Width) + x;
		return edge switch
		{
			HeightEdge.AlongX => MathF.Abs(Heights[i + 1] - Heights[i]),
			HeightEdge.AlongZ => MathF.Abs(Heights[i + Width] - Heights[i]),
			_ => MathF.Abs(Heights[i + 1] - Heights[i + Width]) / RootTwo,
		};
	}

	/// <summary>
	/// The length of a triangle's gradient: the grade the navigation bake's slope limit
	/// and a body's floor test read (LE-D23). The first of cell (x, z)'s pair, or the
	/// <paramref name="second"/>. At most √2 times its steepest edge.
	/// </summary>
	public float FaceGrade(int x, int z, bool second)
	{
		int i = (z * Width) + x;
		float a = Heights[i];
		float b = Heights[i + 1];
		float c = Heights[i + Width];
		float d = Heights[i + Width + 1];
		float alongX = second ? d - c : b - a;
		float alongZ = second ? d - b : c - a;
		return MathF.Sqrt((alongX * alongX) + (alongZ * alongZ));
	}

	/// <summary>The steepest edge with both its ends in the rectangle, as a grade.</summary>
	public float SteepestEdge(in SampleRect samples)
	{
		SampleRect inside = samples.Intersect(Bounds);
		float steepest = 0f;
		for (int z = inside.MinZ; z <= inside.MaxZ; z++)
		{
			for (int x = inside.MinX; x <= inside.MaxX; x++)
			{
				bool east = x < inside.MaxX;
				bool south = z < inside.MaxZ;
				if (east)
				{
					steepest = MathF.Max(steepest, EdgeGrade(x, z, HeightEdge.AlongX));
				}

				if (south)
				{
					steepest = MathF.Max(steepest, EdgeGrade(x, z, HeightEdge.AlongZ));
				}

				if (east && south)
				{
					steepest = MathF.Max(steepest, EdgeGrade(x, z, HeightEdge.Diagonal));
				}
			}
		}

		return steepest;
	}

	/// <summary>The steepest edge of the whole field.</summary>
	public float SteepestEdge() => SteepestEdge(Bounds);

	/// <summary>The steepest face of the cells whose corners are all in the rectangle, as a grade.</summary>
	public float SteepestFace(in SampleRect samples)
	{
		SampleRect inside = samples.Intersect(Bounds);
		float steepest = 0f;
		for (int z = inside.MinZ; z < inside.MaxZ; z++)
		{
			for (int x = inside.MinX; x < inside.MaxX; x++)
			{
				steepest = MathF.Max(steepest, MathF.Max(FaceGrade(x, z, false), FaceGrade(x, z, true)));
			}
		}

		return steepest;
	}

	/// <summary>The steepest face of the whole field.</summary>
	public float SteepestFace() => SteepestFace(Bounds);

	/// <summary>
	/// The ground's gradient at a sample, by central differences: half the difference
	/// of the samples either side, or the one difference there is at the field's edge.
	/// </summary>
	public void Gradient(int x, int z, out float alongX, out float alongZ)
	{
		int west = Math.Max(x - 1, 0);
		int east = Math.Min(x + 1, Width - 1);
		int north = Math.Max(z - 1, 0);
		int south = Math.Min(z + 1, Depth - 1);
		alongX = (this[east, z] - this[west, z]) / (east - west);
		alongZ = (this[x, south] - this[x, north]) / (south - north);
	}

	/// <summary>The ground's normal at a sample, from <see cref="Gradient"/>: what the mesh is lit by.</summary>
	public Vector3 Normal(int x, int z)
	{
		Gradient(x, z, out float alongX, out float alongZ);
		return new Vector3(-alongX, 1f, -alongZ).Normalized();
	}

	/// <summary>The ground's slope at a sample [degrees], from <see cref="Gradient"/>: what the texture bands read (§6.4).</summary>
	public float SlopeDegrees(int x, int z)
	{
		Gradient(x, z, out float alongX, out float alongZ);
		return Degrees(MathF.Sqrt((alongX * alongX) + (alongZ * alongZ)));
	}

	/// <summary>A grade — rise over run — as a slope [degrees].</summary>
	public static float Degrees(float grade) => MathF.Atan(grade) * (180f / MathF.PI);

	/// <summary>A slope [degrees] as a grade.</summary>
	public static float Grade(float degrees) => MathF.Tan(degrees * (MathF.PI / 180f));
}
