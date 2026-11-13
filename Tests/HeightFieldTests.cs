using System;
using Gdpyr.Sim.Level;
using Godot;
using Xunit;

namespace Gdpyr.Tests;

/// <summary>
/// The grid a map's ground is (docs/LEVEL_EDITOR.md §6, §6.1): where its samples
/// are, which triangles a cell is, what a stored sample is, the two measures of a
/// grade, and a ray that the ground stops.
/// </summary>
public class HeightFieldTests
{
	// ---- the layout ---------------------------------------------------------------------

	[Fact]
	public void AMapsGround_Is241SamplesASide_AMetreApart_CentredOnTheOrigin()
	{
		var field = new HeightField();

		Assert.Equal(241, field.Width);
		Assert.Equal(241, field.Depth);
		Assert.Equal(58_081, field.Heights.Length);
		Assert.Equal(57_600, (field.Width - 1) * (field.Depth - 1));
		Assert.Equal(115_200, (field.Width - 1) * (field.Depth - 1) * 2);
		Assert.Equal(116_162, field.Heights.Length * sizeof(short));

		Assert.Equal(-120f, field.WorldX(0));
		Assert.Equal(0f, field.WorldX(120));
		Assert.Equal(120f, field.WorldX(240));
		Assert.Equal(-120f, field.WorldZ(0));
		Assert.Equal(120f, field.WorldZ(240));
		Assert.Equal(new SampleRect(0, 0, 240, 240), field.Bounds);
	}

	[Fact]
	public void SampleXZ_IsAtIndexZTimesWidthPlusX_AndAtXLess120ZLess120()
	{
		var field = new HeightField();
		field[7, 3] = 5f;

		Assert.Equal((3 * 241) + 7, field.Index(7, 3));
		Assert.Equal(5f, field.Heights[(3 * 241) + 7]);
		Assert.Equal(new Vector3(-113f, 5f, -117f), field.Position(7, 3));
		Assert.Equal(5f, field.Sample(-113f, -117f));
		Assert.Equal(0f, field.Sample(-117f, -113f));
	}

	[Fact]
	public void AFieldSmallerThanACell_IsRefused()
	{
		Assert.Throws<ArgumentOutOfRangeException>(() => new HeightField(1, 5));
		Assert.Throws<ArgumentOutOfRangeException>(() => new HeightField(5, 1));
	}

	// ---- the collider's triangles ---------------------------------------------------------

	[Fact]
	public void Sample_AtASample_IsItsHeight()
	{
		HeightField field = LevelGround.Noise(49, 8f, seed: 21);

		for (int z = 0; z < field.Depth; z++)
		{
			for (int x = 0; x < field.Width; x++)
			{
				Assert.Equal(field[x, z], field.Sample(field.WorldX(x), field.WorldZ(z)));
			}
		}
	}

	[Fact]
	public void ACell_IsSplitAlongTheDiagonalFromXPlusOneToZPlusOne()
	{
		// A saddle: 0 at (x, z) and (x+1, z+1), 1 at the other two. The diagonal the collider's
		// triangles share joins the two 1s, so the cell's middle is at 1 and not at 0.
		var field = new HeightField(2, 2);
		field[1, 0] = 1f;
		field[0, 1] = 1f;

		Assert.Equal(1f, field.Sample(0f, 0f));
		Assert.Equal(0.5f, field.Sample(-0.25f, -0.25f));
		Assert.Equal(0.5f, field.Sample(0.25f, 0.25f));
		Assert.Equal(1f, field.Sample(0.25f, -0.25f));
		Assert.Equal(1f, field.Sample(-0.25f, 0.25f));
	}

	[Fact]
	public void Sample_IsThePlaneOfTheTriangleThePointIsIn_TheFirstWhereTheFractionsSumToAtMostOne()
	{
		HeightField field = LevelGround.Noise(33, 6f, seed: 11);
		var random = new Random(3);

		for (int i = 0; i < 20_000; i++)
		{
			double gx = random.NextDouble() * (field.Width - 1);
			double gz = random.NextDouble() * (field.Depth - 1);
			int x = Math.Min((int)gx, field.Width - 2);
			int z = Math.Min((int)gz, field.Depth - 2);
			bool first = (gx - x) + (gz - z) <= 1d;

			// §3's pair: (x,z) (x+1,z) (x,z+1), and (x+1,z) (x+1,z+1) (x,z+1).
			(int X, int Z) a = first ? (x, z) : (x + 1, z);
			(int X, int Z) b = first ? (x + 1, z) : (x + 1, z + 1);
			(int X, int Z) c = (x, z + 1);

			// The point's weights on the triangle's corners: all three from 0 to 1 is inside it.
			double area = ((b.X - a.X) * (c.Z - a.Z)) - ((c.X - a.X) * (b.Z - a.Z));
			double wb = (((gx - a.X) * (c.Z - a.Z)) - ((c.X - a.X) * (gz - a.Z))) / area;
			double wc = (((b.X - a.X) * (gz - a.Z)) - ((gx - a.X) * (b.Z - a.Z))) / area;
			double wa = 1d - wb - wc;
			LevelGround.True(wa >= -1e-9 && wb >= -1e-9 && wc >= -1e-9, $"({gx}, {gz}) is not in the triangle it was given");

			double expected = (wa * field[a.X, a.Z]) + (wb * field[b.X, b.Z]) + (wc * field[c.X, c.Z]);
			float sampled = field.Sample((float)(gx + field.OriginX), (float)(gz + field.OriginZ));
			LevelGround.True(Math.Abs(expected - sampled) < 1e-4, $"({gx}, {gz}): {sampled}, and the triangle is at {expected}");
		}
	}

	[Fact]
	public void TheGround_HasNoStepInIt_AcrossALaneOrADiagonal()
	{
		// Two points a hair apart are never further apart in height than the steepest face allows.
		HeightField field = LevelGround.Noise(33, 6f, seed: 12);
		double steepest = LevelGround.SteepestFace(field);
		var random = new Random(4);

		for (int i = 0; i < 20_000; i++)
		{
			float x = LevelGround.Uniform(random, -16f, 16f);
			float z = LevelGround.Uniform(random, -16f, 16f);

			// Half of them straddle a lane, and half of those a corner.
			if (i % 2 == 0)
			{
				x = MathF.Round(x);
			}

			if (i % 4 == 0)
			{
				z = MathF.Round(z);
			}

			float dx = LevelGround.Uniform(random, -0.01f, 0.01f);
			float dz = LevelGround.Uniform(random, -0.01f, 0.01f);
			double run = Math.Sqrt((dx * dx) + (dz * dz));
			double rise = Math.Abs(field.Sample(x + dx, z + dz) - field.Sample(x - dx, z - dz));
			LevelGround.True(rise <= (steepest * 2d * run) + 1e-5, $"a step of {rise} m at ({x}, {z})");
		}
	}

	[Fact]
	public void APointOffTheField_TakesTheFieldsEdge()
	{
		HeightField field = LevelGround.Noise(17, 4f, seed: 5);

		Assert.Equal(field[0, 5], field.Sample(-500f, field.WorldZ(5)));
		Assert.Equal(field[16, 5], field.Sample(500f, field.WorldZ(5)));
		Assert.Equal(field[3, 0], field.Sample(field.WorldX(3), -500f));
		Assert.Equal(field[3, 16], field.Sample(field.WorldX(3), 500f));
		Assert.Equal(field[0, 0], field.Sample(float.NaN, float.NaN));
	}

	// ---- stored samples -------------------------------------------------------------------

	[Fact]
	public void EveryStoredSample_IsExactAsAFloat_AndAStepIs3Point90625Millimetres()
	{
		Assert.Equal(0.00390625f, MapLimits.HeightStepMeters);

		for (int q = short.MinValue; q <= short.MaxValue; q++)
		{
			float meters = HeightField.ToMeters((short)q);
			Assert.Equal(q, (double)meters * 256d);
			if (q >= MapLimits.MinHeightSample && q <= MapLimits.MaxHeightSample)
			{
				Assert.Equal(q, HeightField.ToSample(meters));
			}
		}
	}

	[Fact]
	public void AHeight_IsStoredAsTheNearestStep_InsideTheLimits()
	{
		Assert.Equal(256, HeightField.ToSample(1f));
		Assert.Equal(257, HeightField.ToSample(1f + 0.0039f));
		Assert.Equal(256, HeightField.ToSample(1f + 0.0019f));
		Assert.Equal(-256, HeightField.ToSample(-1f));
		Assert.Equal(MapLimits.MaxHeightSample, HeightField.ToSample(500f));
		Assert.Equal(MapLimits.MinHeightSample, HeightField.ToSample(-500f));
		Assert.Equal(MapLimits.MinHeightSample, HeightField.ToSample(float.NaN));
	}

	[Fact]
	public void Round_MakesTheRectangleWholeSteps_AndTouchesNothingElse()
	{
		HeightField field = LevelGround.Noise(33, 6f, seed: 8);
		field[10, 10] = 90f;
		field[11, 10] = -90f;
		HeightField before = field.Clone();
		var rectangle = new SampleRect(6, 7, 20, 18);

		field.Round(rectangle);

		for (int z = 0; z < field.Depth; z++)
		{
			for (int x = 0; x < field.Width; x++)
			{
				if (!rectangle.Contains(x, z))
				{
					Assert.Equal(before[x, z], field[x, z]);
					continue;
				}

				double steps = (double)field[x, z] * 256d;
				Assert.Equal(Math.Round(steps), steps);
				Assert.InRange(field[x, z], MapLimits.MinHeightMeters, MapLimits.MaxHeightMeters);
				Assert.True(Math.Abs(field[x, z] - Math.Clamp(before[x, z], -16f, 64f)) <= (MapLimits.HeightStepMeters / 2f) + 1e-6f);
			}
		}

		Assert.Equal(64f, field[10, 10]);
		Assert.Equal(-16f, field[11, 10]);

		// Rounded once, it is rounded: a second time changes nothing.
		HeightField once = field.Clone();
		field.Round(rectangle);
		Assert.Equal(once.Heights, field.Heights);

		// A rectangle that runs off the field rounds what there is of it.
		field.Round(new SampleRect(-40, -40, 400, 400));
		Assert.Equal(field.ToSamples(), HeightField.FromSamples(field.ToSamples(), 33, 33).ToSamples());
		Assert.Equal(HeightField.FromSamples(field.ToSamples(), 33, 33).Heights, field.Heights);
	}

	[Fact]
	public void Samples_ReadBackAsTheGroundTheyWereWrittenFrom()
	{
		HeightField field = LevelGround.Noise(33, 6f, seed: 9);
		field.Round(field.Bounds);

		short[] samples = field.ToSamples();
		HeightField read = HeightField.FromSamples(samples, 33, 33);

		Assert.Equal(field.Heights, read.Heights);
		Assert.Equal(samples, read.ToSamples());
		Assert.Null(HeightField.FromSamples(samples, 33, 32));
		Assert.Null(HeightField.FromSamples(samples.AsSpan(0, 1), 1, 1));
	}

	// ---- grades ---------------------------------------------------------------------------

	[Fact]
	public void OnAPlane_EveryEdgeFaceAndNormalIsThePlanes()
	{
		const float b = 0.3f;
		const float c = -0.2f;
		HeightField field = LevelGround.Plane(9, 2f, b, c);
		float face = MathF.Sqrt((b * b) + (c * c));

		for (int z = 0; z < 8; z++)
		{
			for (int x = 0; x < 8; x++)
			{
				LevelGround.Near(MathF.Abs(b), field.EdgeGrade(x, z, HeightEdge.AlongX), 1e-5);
				LevelGround.Near(MathF.Abs(c), field.EdgeGrade(x, z, HeightEdge.AlongZ), 1e-5);
				LevelGround.Near(MathF.Abs(b - c) / MathF.Sqrt(2f), field.EdgeGrade(x, z, HeightEdge.Diagonal), 1e-5);
				LevelGround.Near(face, field.FaceGrade(x, z, false), 1e-5);
				LevelGround.Near(face, field.FaceGrade(x, z, true), 1e-5);
			}
		}

		// Central differences inside, the one difference there is at an edge: the same on a plane.
		Vector3 normal = new Vector3(-b, 1f, -c).Normalized();
		foreach ((int x, int z) in new[] { (4, 4), (0, 0), (8, 8), (0, 5), (8, 2), (3, 0), (6, 8) })
		{
			field.Gradient(x, z, out float alongX, out float alongZ);
			LevelGround.Near(b, alongX, 1e-5);
			LevelGround.Near(c, alongZ, 1e-5);
			Assert.True(field.Normal(x, z).DistanceTo(normal) < 1e-5f);
			LevelGround.Near(LevelGround.Degrees(face), field.SlopeDegrees(x, z), 1e-3);
		}

		// The diagonal runs against both, so it is the steepest edge here: 0.354 to the axes' 0.3 and 0.2.
		LevelGround.Near(MathF.Abs(b - c) / Math.Sqrt(2d), field.SteepestEdge(), 1e-5);
		LevelGround.Near(face, field.SteepestFace(), 1e-5);
	}

	[Fact]
	public void ANormal_IsByCentralDifferences()
	{
		HeightField field = LevelGround.Flat(5);
		field[1, 2] = 1f;
		field[3, 2] = 4f;
		field[2, 1] = -2f;
		field[2, 3] = 2f;
		field[2, 2] = 100f;

		// Half the difference of the samples either side: the sample itself has no say.
		field.Gradient(2, 2, out float alongX, out float alongZ);
		Assert.Equal(1.5f, alongX);
		Assert.Equal(2f, alongZ);
		Assert.True(field.Normal(2, 2).DistanceTo(new Vector3(-1.5f, 1f, -2f).Normalized()) < 1e-6f);
		LevelGround.Near(LevelGround.Degrees(2.5), field.SlopeDegrees(2, 2), 1e-3);
	}

	[Fact]
	public void AFace_IsAtMostRootTwoTimesItsSteepestEdge()
	{
		HeightField field = LevelGround.Noise(65, 12f, seed: 31, persistence: 0.7f);
		double worst = 0d;

		for (int z = 0; z < field.Depth - 1; z++)
		{
			for (int x = 0; x < field.Width - 1; x++)
			{
				float diagonal = field.EdgeGrade(x, z, HeightEdge.Diagonal);
				float first = MathF.Max(diagonal,
					MathF.Max(field.EdgeGrade(x, z, HeightEdge.AlongX), field.EdgeGrade(x, z, HeightEdge.AlongZ)));
				float second = MathF.Max(diagonal,
					MathF.Max(field.EdgeGrade(x + 1, z, HeightEdge.AlongZ), field.EdgeGrade(x, z + 1, HeightEdge.AlongX)));

				Assert.True(field.FaceGrade(x, z, false) <= (first * Math.Sqrt(2d)) + 1e-5);
				Assert.True(field.FaceGrade(x, z, true) <= (second * Math.Sqrt(2d)) + 1e-5);
				worst = Math.Max(worst, Math.Max(field.FaceGrade(x, z, false) / first, field.FaceGrade(x, z, true) / second));
			}
		}

		// And rough ground comes close to it: the bound is not slack.
		Assert.InRange(worst, 1.35, Math.Sqrt(2d) + 1e-5);
		LevelGround.Near(LevelGround.SteepestFace(field), field.SteepestFace(), 1e-4);
		LevelGround.Near(LevelGround.SteepestEdge(field), field.SteepestEdge(), 1e-4);
	}

	[Theory]
	[InlineData(0.25f)]
	[InlineData(1f)]
	[InlineData(-3f)]
	public void ACreaseAcrossAFace_IsTheWholeRootTwo(float g)
	{
		// Heights 0, g, g at (x,z), (x+1,z), (x,z+1): both axis edges at g, the diagonal level.
		var field = new HeightField(2, 2);
		field[1, 0] = g;
		field[0, 1] = g;
		field[1, 1] = g;

		Assert.Equal(MathF.Abs(g), field.EdgeGrade(0, 0, HeightEdge.AlongX));
		Assert.Equal(MathF.Abs(g), field.EdgeGrade(0, 0, HeightEdge.AlongZ));
		Assert.Equal(0f, field.EdgeGrade(0, 0, HeightEdge.Diagonal));
		LevelGround.Near(MathF.Abs(g) * Math.Sqrt(2d), field.FaceGrade(0, 0, false), 1e-5);
		Assert.Equal(0f, field.FaceGrade(0, 0, true));
	}

	[Fact]
	public void TheSteepestOfARectangle_IsOfTheEdgesAndFacesInsideIt()
	{
		HeightField field = LevelGround.Flat(12);
		field[6, 6] = 3f;

		// The spike's edges all have an end at (6, 6); a rectangle that stops short of it has none of them.
		Assert.Equal(0f, field.SteepestEdge(new SampleRect(0, 0, 5, 11)));
		Assert.Equal(0f, field.SteepestFace(new SampleRect(0, 0, 5, 11)));
		Assert.Equal(3f, field.SteepestEdge(new SampleRect(5, 6, 6, 6)));
		Assert.Equal(0f, field.SteepestFace(new SampleRect(5, 6, 6, 6)));
		Assert.Equal(3f, field.SteepestEdge(new SampleRect(4, 4, 8, 8)));
		LevelGround.Near(3d * Math.Sqrt(2d), field.SteepestFace(new SampleRect(4, 4, 8, 8)), 1e-5);
		Assert.Equal(0f, field.SteepestEdge(SampleRect.Empty));
		Assert.Equal(3f, field.SteepestEdge(new SampleRect(-50, -50, 50, 50)));
	}

	[Theory]
	[InlineData(0f, 0f)]
	[InlineData(30f, 0.57735f)]
	[InlineData(45f, 1f)]
	[InlineData(60f, 1.73205f)]
	public void AGradeOfK_IsAtanKOfSlope(float degrees, float grade)
	{
		Assert.Equal(grade, HeightField.Grade(degrees), 3);
		LevelGround.Near(degrees, HeightField.Degrees(grade), 1e-3);
	}

	// ---- a ray ----------------------------------------------------------------------------

	[Fact]
	public void ARayDown_MeetsTheGroundWhereSampleSaysItIs()
	{
		HeightField field = LevelGround.Hills(seed: 1);
		var random = new Random(17);

		for (int i = 0; i < 1_000; i++)
		{
			float x = LevelGround.Uniform(random, -120f, 120f);
			float z = LevelGround.Uniform(random, -120f, 120f);

			Assert.True(field.Raycast(new Vector3(x, 80f, z), Vector3.Down, 200f, out Vector3 hit));
			Assert.Equal(x, hit.X);
			Assert.Equal(z, hit.Z);
			LevelGround.True(MathF.Abs(hit.Y - field.Sample(x, z)) < 1e-4f, $"({x}, {z}): met at {hit.Y}, the ground is at {field.Sample(x, z)}");
		}
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void ARayFromOverTheGroundToUnderIt_NeverPassesThrough(bool hills)
	{
		// What LE0 cast at the engine's heightmap, 65 of 20,000 of which went through it (§3): long
		// slanted rays. Here none may, and each meets the ground where the ground is, and no later.
		HeightField field = hills ? LevelGround.Hills(seed: 1) : LevelGround.Flat(MapLimits.TerrainSamples, 0.5f);
		var random = new Random(hills ? 29 : 23);
		int cast = 0;

		while (cast < 10_000)
		{
			var from = new Vector3(LevelGround.Uniform(random, -119f, 119f), 0f, LevelGround.Uniform(random, -119f, 119f));
			var to = new Vector3(LevelGround.Uniform(random, -119f, 119f), 0f, LevelGround.Uniform(random, -119f, 119f));
			float plan = from.DistanceTo(to);
			if (plan < 16f || plan > 200f)
			{
				continue;
			}

			from.Y = field.Sample(from.X, from.Z) + LevelGround.Uniform(random, 0.5f, 30f);
			to.Y = field.Sample(to.X, to.Z) - LevelGround.Uniform(random, 0.5f, 30f);
			float length = from.DistanceTo(to);
			cast++;

			LevelGround.True(field.Raycast(from, to - from, length, out Vector3 hit), $"a ray from {from} to {to} went through the ground");
			LevelGround.True(MathF.Abs(hit.Y - field.Sample(hit.X, hit.Z)) < 1e-3f, $"met at {hit}, where the ground is at {field.Sample(hit.X, hit.Z)}");

			// On the ray, and the ray over the ground all the way to it.
			float along = from.DistanceTo(hit);
			Assert.True((from + ((to - from) / length * along)).DistanceTo(hit) < 1e-3f);
			for (float t = 0f; t < along - 0.05f; t += 0.37f)
			{
				Vector3 at = from + ((to - from) / length * t);
				LevelGround.True(at.Y > field.Sample(at.X, at.Z) - 1e-3f, $"the ray from {from} to {to} was under the ground {t} m along, before it met it at {along} m");
			}
		}
	}

	[Fact]
	public void ARay_MeetsTheGroundWhereItFirstMeetsATriangle()
	{
		// Every triangle asked in turn, with no walk to get wrong.
		HeightField field = LevelGround.Noise(25, 5f, seed: 41);
		var random = new Random(43);
		int met = 0;

		for (int i = 0; i < 4_000; i++)
		{
			var from = new Vector3(LevelGround.Uniform(random, -16f, 16f), LevelGround.Uniform(random, -8f, 12f), LevelGround.Uniform(random, -16f, 16f));
			var direction = new Vector3(LevelGround.Uniform(random, -1f, 1f), LevelGround.Uniform(random, -1f, 1f), LevelGround.Uniform(random, -1f, 1f));
			if (direction.Length() < 0.1f)
			{
				continue;
			}

			float reach = LevelGround.Uniform(random, 1f, 60f);
			double expected = FirstTriangle(field, from, direction, reach);
			bool hit = field.Raycast(from, direction, reach, out Vector3 at);

			// A ray that ends within a hair of the ground may be called either way; no other may.
			if (double.IsNaN(expected) || expected > reach - 1e-3)
			{
				LevelGround.True(!hit || from.DistanceTo(at) > reach - 2e-3f, $"met the ground {from.DistanceTo(at)} m along a ray from {from} that no triangle is on before {reach} m");
				continue;
			}

			LevelGround.True(hit, $"a ray from {from} along {direction} meets a triangle at {expected} m and was let through");
			LevelGround.True(Math.Abs(from.DistanceTo(at) - expected) < 2e-3, $"met at {from.DistanceTo(at)} m; the first triangle is at {expected} m");
			met++;
		}

		Assert.InRange(met, 500, 4_000);
	}

	[Fact]
	public void ARayAlongALane_ThroughCorners_OrAFloatShortOfOne_IsStoppedAsAnyOtherIs()
	{
		// The rays a heightmap's walk goes wrong on (§3): on a lane between cells, through the
		// corners where four meet, and starting one float short of a lane.
		HeightField field = LevelGround.Noise(25, 5f, seed: 47);
		float shy = MathF.BitDecrement(3f);
		(Vector3 From, Vector3 Direction)[] rays =
		{
			(new Vector3(-11f, 9f, 3f), new Vector3(1f, -1f, 0f)),
			(new Vector3(3f, 9f, 11f), new Vector3(0f, -1f, -1f)),
			(new Vector3(-10f, 9f, -10f), new Vector3(1f, -1.25f, 1f)),
			(new Vector3(10f, 9f, -10f), new Vector3(-1f, -1.25f, 1f)),
			(new Vector3(-10f, 9f, 4f), new Vector3(1f, -1.5f, -1f)),
			(new Vector3(shy, 9f, -11f), new Vector3(0f, -1f, 1f)),
			(new Vector3(shy, 9f, shy), new Vector3(1f, -3f, 1f)),
			(new Vector3(-11f, 9f, shy), new Vector3(1f, -1f, 1e-7f)),
			(new Vector3(-12f, 9f, -12f), new Vector3(1f, -1.125f, 1f)),
			(new Vector3(12f, 9f, 12f), new Vector3(-1f, -1.125f, -1f)),
		};

		foreach ((Vector3 from, Vector3 direction) in rays)
		{
			double expected = FirstTriangle(field, from, direction, 100f);
			Assert.False(double.IsNaN(expected), $"the ray from {from} is not one that meets the ground");

			LevelGround.True(field.Raycast(from, direction, 100f, out Vector3 hit), $"a ray from {from} along {direction} went through the ground");
			LevelGround.True(Math.Abs(from.DistanceTo(hit) - expected) < 2e-3, $"from {from}: met at {from.DistanceTo(hit)} m; the first triangle is at {expected} m");
			Assert.True(MathF.Abs(hit.Y - field.Sample(hit.X, hit.Z)) < 1e-3f);
		}
	}

	[Fact]
	public void ARay_IsStoppedFromEitherSide()
	{
		HeightField field = LevelGround.Plane(17, 2f, 0.25f, 0f);

		// h = 2 + 0.25 · (x + 8). Down onto it, and up out of it, at x = 0: both at 4 m.
		Assert.True(field.Raycast(new Vector3(0f, 10f, 0f), Vector3.Down, 50f, out Vector3 down));
		Assert.Equal(4f, down.Y, 5);
		Assert.True(field.Raycast(new Vector3(0f, -10f, 0f), Vector3.Up, 50f, out Vector3 up));
		Assert.Equal(4f, up.Y, 5);

		// A ray that starts on the ground has met it.
		Assert.True(field.Raycast(new Vector3(0f, 4f, 0f), new Vector3(1f, 1f, 0f), 50f, out Vector3 on));
		Assert.Equal(new Vector3(0f, 4f, 0f), on);
	}

	[Fact]
	public void ARayThatStaysOverTheGround_OrOffTheField_OrStopsShort_MeetsNothing()
	{
		HeightField field = LevelGround.Noise(25, 5f, seed: 53);

		Assert.False(field.Raycast(new Vector3(-12f, 20f, 0f), new Vector3(1f, 0f, 0.3f), 100f, out _));
		Assert.False(field.Raycast(new Vector3(0f, 20f, 0f), Vector3.Up, 100f, out _));
		Assert.False(field.Raycast(new Vector3(0f, 20f, 0f), Vector3.Down, 5f, out _));

		// There is no ground past the field's edge: a ray that comes down outside it meets none.
		Assert.False(field.Raycast(new Vector3(30f, 20f, 0f), Vector3.Down, 100f, out _));
		Assert.False(field.Raycast(new Vector3(30f, 20f, 0f), new Vector3(1f, -1f, 0f), 100f, out _));
		Assert.True(field.Raycast(new Vector3(30f, 20f, 0f), new Vector3(-1f, -1f, 0f), 100f, out Vector3 inside));
		Assert.InRange(inside.X, -12f, 12f);

		// And a ray that is not one meets nothing, rather than throwing.
		Assert.False(field.Raycast(Vector3.Zero, Vector3.Zero, 100f, out _));
		Assert.False(field.Raycast(Vector3.Zero, Vector3.Down, 0f, out _));
		Assert.False(field.Raycast(new Vector3(float.NaN, 5f, 0f), Vector3.Down, 100f, out _));
		Assert.False(field.Raycast(Vector3.Zero, new Vector3(float.NaN, -1f, 0f), 100f, out _));
	}

	[Fact]
	public void ARaysDirection_NeedNotBeOfLengthOne()
	{
		HeightField field = LevelGround.Noise(25, 5f, seed: 59);
		var from = new Vector3(-6f, 14f, 3.3f);
		var direction = new Vector3(0.7f, -1f, -0.2f);

		Assert.True(field.Raycast(from, direction, 80f, out Vector3 unit));
		Assert.True(field.Raycast(from, direction * 250f, 80f, out Vector3 scaled));
		Assert.True(unit.DistanceTo(scaled) < 1e-4f);
	}

	/// <summary>
	/// How far along a ray the first of the ground's triangles is, each of them asked
	/// in turn (Möller–Trumbore, from either side, an edge counted as in). NaN for none
	/// within <paramref name="reach"/>.
	/// </summary>
	private static double FirstTriangle(HeightField field, Vector3 from, Vector3 direction, double reach)
	{
		double length = Math.Sqrt(((double)direction.X * direction.X) + ((double)direction.Y * direction.Y) + ((double)direction.Z * direction.Z));
		double[] o = { from.X, from.Y, from.Z };
		double[] d = { direction.X / length, direction.Y / length, direction.Z / length };
		double first = double.NaN;

		for (int z = 0; z < field.Depth - 1; z++)
		{
			for (int x = 0; x < field.Width - 1; x++)
			{
				double[] a = Corner(field, x, z);
				double[] b = Corner(field, x + 1, z);
				double[] c = Corner(field, x, z + 1);
				double[] e = Corner(field, x + 1, z + 1);
				foreach (double t in new[] { Triangle(o, d, a, b, c), Triangle(o, d, b, e, c) })
				{
					if (!double.IsNaN(t) && t <= reach && !(t >= first))
					{
						first = t;
					}
				}
			}
		}

		return first;
	}

	private static double[] Corner(HeightField field, int x, int z) => new double[] { field.WorldX(x), field[x, z], field.WorldZ(z) };

	private static double Triangle(double[] o, double[] d, double[] a, double[] b, double[] c)
	{
		double[] ab = { b[0] - a[0], b[1] - a[1], b[2] - a[2] };
		double[] ac = { c[0] - a[0], c[1] - a[1], c[2] - a[2] };
		double[] p = Cross(d, ac);
		double det = Dot(ab, p);
		if (Math.Abs(det) < 1e-12)
		{
			return double.NaN;
		}

		double[] s = { o[0] - a[0], o[1] - a[1], o[2] - a[2] };
		double u = Dot(s, p) / det;
		double[] q = Cross(s, ab);
		double v = Dot(d, q) / det;
		double t = Dot(ac, q) / det;
		return u >= -1e-9 && v >= -1e-9 && u + v <= 1d + 1e-9 && t >= 0d ? t : double.NaN;
	}

	private static double[] Cross(double[] a, double[] b) =>
		new[] { (a[1] * b[2]) - (a[2] * b[1]), (a[2] * b[0]) - (a[0] * b[2]), (a[0] * b[1]) - (a[1] * b[0]) };

	private static double Dot(double[] a, double[] b) => (a[0] * b[0]) + (a[1] * b[1]) + (a[2] * b[2]);
}
