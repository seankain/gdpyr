using System;
using Gdpyr.Sim.Level;

namespace Gdpyr.Tests;

/// <summary>
/// Ground for the level editor's tests (docs/LEVEL_EDITOR.md §11, LE1): seeded
/// terrains to assert §6's properties over, and the measures those properties are
/// stated in, written out here a second time so that a test does not ask the code
/// under test what the answer is.
/// </summary>
internal static class LevelGround
{
	public static readonly int[] Rough = { 4, 8, 16, 32 };

	/// <summary>
	/// Value-noise fBm on an n × n grid, as <c>tools/level_editor_checks/design_checks.py</c>
	/// makes it: <paramref name="amplitude"/> at the coarsest lattice, falling by
	/// <paramref name="persistence"/> a lattice. The same seed is the same ground.
	/// </summary>
	public static HeightField Noise(int n, float amplitude, int seed, int[] lattices = null, float persistence = 0.5f)
	{
		var random = new Random(seed);
		var field = new HeightField(n, n);
		double amp = amplitude;
		foreach (int cells in lattices ?? Rough)
		{
			var lattice = new double[(cells + 2) * (cells + 2)];
			for (int i = 0; i < lattice.Length; i++)
			{
				lattice[i] = (random.NextDouble() * 2d) - 1d;
			}

			// Along either axis a sample is between the same two lattice lines, by the same share.
			var at = new int[n];
			var share = new double[n];
			for (int i = 0; i < n; i++)
			{
				double f = i * (double)cells / n;
				at[i] = (int)f;
				share[i] = Smooth(f - at[i]);
			}

			for (int z = 0; z < n; z++)
			{
				int row = at[z] * (cells + 2);
				for (int x = 0; x < n; x++)
				{
					int i = row + at[x];
					double near = Lerp(lattice[i], lattice[i + 1], share[x]);
					double far = Lerp(lattice[i + cells + 2], lattice[i + cells + 3], share[x]);
					field.Heights[(z * n) + x] += (float)(amp * Lerp(near, far, share[z]));
				}
			}

			amp *= persistence;
		}

		return field;
	}

	/// <summary>Rolling hills over a whole map, after §10's: −6 to 18 m or so, most of it walkable.</summary>
	public static HeightField Hills(int seed)
	{
		HeightField field = Noise(MapLimits.TerrainSamples, 12.4f, seed, new[] { 4, 8, 16, 32, 64, 128 });
		for (int i = 0; i < field.Heights.Length; i++)
		{
			field.Heights[i] += 6f;
		}

		return field;
	}

	/// <summary>A plane: <c>h = at + alongX · x + alongZ · z</c>, in samples from sample (0, 0).</summary>
	public static HeightField Plane(int n, float at, float alongX, float alongZ)
	{
		var field = new HeightField(n, n);
		for (int z = 0; z < n; z++)
		{
			for (int x = 0; x < n; x++)
			{
				field[x, z] = at + (alongX * x) + (alongZ * z);
			}
		}

		return field;
	}

	public static HeightField Flat(int n, float at = 0f) => Plane(n, at, 0f, 0f);

	/// <summary>The steepest edge of the collider's triangles — along x, along z, a cell's diagonal — as a grade.</summary>
	public static double SteepestEdge(HeightField field)
	{
		float[] h = field.Heights;
		int width = field.Width;
		int depth = field.Depth;
		double steepest = 0d;
		for (int z = 0; z < depth; z++)
		{
			for (int x = 0; x < width; x++)
			{
				int i = (z * width) + x;
				if (x + 1 < width)
				{
					steepest = Math.Max(steepest, Math.Abs((double)h[i + 1] - h[i]));
				}

				if (z + 1 < depth)
				{
					steepest = Math.Max(steepest, Math.Abs((double)h[i + width] - h[i]));
				}

				if (x + 1 < width && z + 1 < depth)
				{
					steepest = Math.Max(steepest, Math.Abs((double)h[i + 1] - h[i + width]) / Math.Sqrt(2d));
				}
			}
		}

		return steepest;
	}

	/// <summary>The steepest face — a triangle's gradient — as a grade.</summary>
	public static double SteepestFace(HeightField field)
	{
		double steepest = 0d;
		for (int z = 0; z + 1 < field.Depth; z++)
		{
			for (int x = 0; x + 1 < field.Width; x++)
			{
				steepest = Math.Max(steepest, Math.Max(Face(field, x, z, false), Face(field, x, z, true)));
			}
		}

		return steepest;
	}

	/// <summary>One triangle's gradient: the first of cell (x, z)'s pair, or the second.</summary>
	public static double Face(HeightField field, int x, int z, bool second)
	{
		float[] h = field.Heights;
		int i = (z * field.Width) + x;
		double a = h[i];
		double b = h[i + 1];
		double c = h[i + field.Width];
		double d = h[i + field.Width + 1];
		return second
			? Math.Sqrt(((d - c) * (d - c)) + ((d - b) * (d - b)))
			: Math.Sqrt(((b - a) * (b - a)) + ((c - a) * (c - a)));
	}

	/// <summary>
	/// The most any pair of samples up to <paramref name="window"/> apart ends steeper
	/// than the steeper of what the pair was and <paramref name="grade"/>:
	/// max of |h'(a) − h'(b)| − max(|h(a) − h(b)|, k · d(a, b)), in metres.
	/// </summary>
	public static double WorstPair(HeightField before, HeightField after, double grade, int window = 6)
	{
		float[] was = before.Heights;
		float[] now = after.Heights;
		int width = before.Width;
		double worst = double.MinValue;
		for (int dz = 0; dz <= window; dz++)
		{
			for (int dx = -window; dx <= window; dx++)
			{
				if (dz == 0 && dx <= 0)
				{
					continue;
				}

				double skirt = grade * Math.Sqrt((dx * dx) + (dz * dz));
				int other = (dz * width) + dx;
				for (int z = 0; z + dz < before.Depth; z++)
				{
					int last = (z * width) + width - Math.Max(0, dx);
					for (int i = (z * width) + Math.Max(0, -dx); i < last; i++)
					{
						double rose = Math.Abs((double)now[i] - now[i + other]);
						double allowed = Math.Max(Math.Abs((double)was[i] - was[i + other]), skirt);
						if (rose - allowed > worst)
						{
							worst = rose - allowed;
						}
					}
				}
			}
		}

		return worst;
	}

	/// <summary>
	/// The most any edge of the collider's triangles ends steeper than the steeper of
	/// what that edge was and <paramref name="grade"/>, as a grade: §6.2's claim, edge
	/// by edge.
	/// </summary>
	public static double WorstEdge(HeightField before, HeightField after, double grade)
	{
		float[] was = before.Heights;
		float[] now = after.Heights;
		int width = before.Width;
		double worst = double.MinValue;
		(int X, int Z, int ToX, int ToZ, double Run)[] edges =
		{
			(0, 0, 1, 0, 1d), (0, 0, 0, 1, 1d), (1, 0, 0, 1, Math.Sqrt(2d)),
		};
		foreach ((int fromX, int fromZ, int toX, int toZ, double run) in edges)
		{
			int from = (fromZ * width) + fromX;
			int to = (toZ * width) + toX;
			for (int z = 0; z + Math.Max(fromZ, toZ) < before.Depth; z++)
			{
				for (int x = 0; x + Math.Max(fromX, toX) < width; x++)
				{
					int i = (z * width) + x;
					double steep = Math.Abs((double)was[i + from] - was[i + to]) / run;
					double ended = Math.Abs((double)now[i + from] - now[i + to]) / run;
					worst = Math.Max(worst, ended - Math.Max(steep, grade));
				}
			}
		}

		return worst;
	}

	/// <summary>The most any sample differs between two grounds [m].</summary>
	public static double Change(HeightField a, HeightField b)
	{
		double most = 0d;
		for (int i = 0; i < a.Heights.Length; i++)
		{
			most = Math.Max(most, Math.Abs((double)a.Heights[i] - b.Heights[i]));
		}

		return most;
	}

	/// <summary>The rectangle of the samples that differ between two grounds.</summary>
	public static SampleRect Changed(HeightField a, HeightField b)
	{
		float[] one = a.Heights;
		float[] other = b.Heights;
		int width = a.Width;
		int minX = int.MaxValue;
		int minZ = int.MaxValue;
		int maxX = int.MinValue;
		int maxZ = int.MinValue;
		for (int i = 0; i < one.Length; i++)
		{
			if (one[i] != other[i])
			{
				int x = i % width;
				int z = i / width;
				minX = Math.Min(minX, x);
				maxX = Math.Max(maxX, x);
				minZ = Math.Min(minZ, z);
				maxZ = Math.Max(maxZ, z);
			}
		}

		return maxX < minX ? SampleRect.Empty : new SampleRect(minX, minZ, maxX, maxZ);
	}

	/// <summary>One ground's heights written over another's, of the same size: a copy without a new field.</summary>
	public static void Copy(HeightField from, HeightField to) => Array.Copy(from.Heights, to.Heights, from.Heights.Length);

	/// <summary>Within <paramref name="tolerance"/> of what was expected, or the test fails saying what it got.</summary>
	public static void Near(double expected, double actual, double tolerance)
	{
		if (!(Math.Abs(expected - actual) <= tolerance))
		{
			Xunit.Assert.Fail($"expected {expected} within {tolerance}, got {actual}");
		}
	}

	/// <summary>The same float, to the bit: for a loop over every sample of a field, where <c>Assert.Equal</c> is slow.</summary>
	public static void Same(float expected, float actual)
	{
		if (expected != actual)
		{
			Xunit.Assert.Fail($"expected {expected:R} exactly, got {actual:R}");
		}
	}

	/// <summary>
	/// <c>Assert.True</c> with a message that is only put together when it is needed:
	/// these tests assert inside loops over every sample of a field.
	/// </summary>
	public static void True(bool condition, FormattableString message)
	{
		if (!condition)
		{
			Xunit.Assert.Fail(message.ToString());
		}
	}

	public static double Degrees(double grade) => Math.Atan(grade) * 180d / Math.PI;

	public static double Grade(double degrees) => Math.Tan(degrees * Math.PI / 180d);

	public static float Uniform(Random random, float from, float to) => from + ((float)random.NextDouble() * (to - from));

	private static double Smooth(double t) => t * t * (3d - (2d * t));

	private static double Lerp(double a, double b, double t) => a + ((b - a) * t);
}
