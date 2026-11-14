using System;
using System.Collections.Generic;
using System.Linq;
using Gdpyr.Sim;
using Gdpyr.Sim.Level;
using Godot;
using Xunit;
using Xunit.Abstractions;

namespace Gdpyr.Tests;

/// <summary>
/// A road (docs/LEVEL_EDITOR.md §6.6, LE6): its centreline, rounded at its corners and sampled
/// evenly; its profile, which is no steeper than its limit and cuts or fills E / 2 at its worst,
/// pinned where an end is joined; its ribbon, level across with square ends; and a road graded
/// on a map, which keeps its grade along its centreline until the ground under it is edited.
/// The profile's definition is written out here a second time, as a sum over every pair of
/// samples, so that a test does not ask the code under test what the answer is.
/// </summary>
public sealed class RoadPathTests
{
	private readonly ITestOutputHelper _output;

	public RoadPathTests(ITestOutputHelper output)
	{
		_output = output;
	}

	// ---- the profile ---------------------------------------------------------------------------------

	/// <summary>m and M by their definition: every pair of samples.</summary>
	private static void Definition(float[] f, float step, float g, out float[] m, out float[] big)
	{
		int n = f.Length;
		m = new float[n];
		big = new float[n];
		for (int s = 0; s < n; s++)
		{
			float low = float.MaxValue;
			float high = float.MinValue;
			for (int t = 0; t < n; t++)
			{
				low = MathF.Min(low, f[t] + (g * step * Math.Abs(s - t)));
				high = MathF.Max(high, f[t] - (g * step * Math.Abs(s - t)));
			}

			m[s] = low;
			big[s] = high;
		}
	}

	[Fact]
	public void TheEnvelope_IsItsDefinition_AndTheProfileIsNoSteeperThanG_AndCutsOrFillsEOverTwo()
	{
		var random = new Random(3);
		float worstGrade = 0f;
		float worstGap = 0f;
		float worstDefinition = 0f;
		for (int trial = 0; trial < 200; trial++)
		{
			int n = random.Next(40, 400);
			float step = (float)(0.25 + (random.NextDouble() * 0.25));
			float spread = (float)(0.05 + (random.NextDouble() * 0.55));
			float wave = (float)(random.NextDouble() * 6.0);
			float period = (float)(8.0 + (random.NextDouble() * 32.0));
			float g = new[] { 0.06f, 0.08f, 0.10f, 0.15f }[random.Next(4)];
			var f = new float[n];
			float walk = 0f;
			for (int i = 0; i < n; i++)
			{
				walk += (float)(((random.NextDouble() * 2.0) - 1.0) * spread);
				f[i] = walk + (wave * MathF.Sin(i * step / period));
			}

			var lower = new float[n];
			var upper = new float[n];
			RoadPath.Envelope(f, step, g, lower, upper);
			Definition(f, step, g, out float[] m, out float[] big);
			float excess = 0f;
			for (int s = 0; s < n; s++)
			{
				worstDefinition = MathF.Max(worstDefinition, MathF.Max(MathF.Abs(lower[s] - m[s]), MathF.Abs(upper[s] - big[s])));
				for (int t = 0; t < n; t++)
				{
					excess = MathF.Max(excess, MathF.Abs(f[s] - f[t]) - (g * step * Math.Abs(s - t)));
				}
			}

			float deepest = 0f;
			for (int s = 0; s < n; s++)
			{
				float z = (lower[s] + upper[s]) * 0.5f;
				deepest = MathF.Max(deepest, MathF.Abs(z - f[s]));
				if (s > 0)
				{
					float before = (lower[s - 1] + upper[s - 1]) * 0.5f;
					worstGrade = MathF.Max(worstGrade, (MathF.Abs(z - before) / step) - g);
				}
			}

			worstGap = MathF.Max(worstGap, MathF.Abs(deepest - (excess * 0.5f)));
		}

		_output.WriteLine($"envelope off its definition {worstDefinition:E1} m; grade over g {worstGrade:E1}; |deepest - E/2| {worstGap:E1} m");
		Assert.True(worstDefinition < 1e-4f, $"the two passes are {worstDefinition} m off the definition");
		Assert.True(worstGrade < 1e-4f, $"the profile is {worstGrade} steeper than g somewhere");
		Assert.True(worstGap < 1e-3f, $"the deepest cut or fill is {worstGap} m off E / 2");
	}

	[Fact]
	public void AnEndPinnedOverTheGround_MeetsItExactly_StaysWithinG_AndRejoinsTheFreeProfile()
	{
		// §10's: 200 m of rolling ground at 10 %, the first end pinned 4 m over it.
		var field = new HeightField();
		for (int z = 0; z < field.Depth; z++)
		{
			for (int x = 0; x < field.Width; x++)
			{
				field[x, z] = 6f * MathF.Sin(field.WorldX(x) / 25f);
			}
		}

		RoadPath free = RoadPath.Of(new[] { new Vector2(-100f, 0f), new Vector2(100f, 0f) }, 6f, 0.10f);
		Assert.Equal(RoadResult.Ok, free.Fit(field));
		float deck = field.Sample(-100f, 0f) + 4f;
		RoadPath pinned = RoadPath.Of(new[] { new Vector2(-100f, 0f), new Vector2(100f, 0f) }, 6f, 0.10f);
		Assert.Equal(RoadResult.Ok, pinned.Fit(field, deck));

		Assert.Equal(deck, pinned.Profile[0]);
		float steepest = 0f;
		int rejoins = -1;
		for (int i = 1; i < pinned.Count; i++)
		{
			steepest = MathF.Max(steepest, MathF.Abs(pinned.Profile[i] - pinned.Profile[i - 1]) / pinned.Step);
			if (rejoins < 0 && MathF.Abs(pinned.Profile[i] - free.Profile[i]) < 1e-4f)
			{
				rejoins = i;
			}
		}

		_output.WriteLine($"pinned 4 m over the ground: steepest {100f * steepest:0.00} %, back on the free profile in {pinned.DistanceAlong(rejoins):0.0} m");
		Assert.True(steepest <= 0.10f + 1e-4f, $"the pinned profile is {steepest} steep");
		Assert.InRange(rejoins, 1, pinned.Count - 1);
		for (int i = rejoins; i < pinned.Count; i++)
		{
			Assert.Equal(free.Profile[i], pinned.Profile[i], 4);
		}
	}

	[Fact]
	public void TwoEndsTooFarApartInHeight_AreTooSteepToJoin_WithTheGradeItWouldTake()
	{
		var field = new HeightField();
		RoadPath path = RoadPath.Of(new[] { new Vector2(-50f, 0f), new Vector2(50f, 0f) }, 6f, 0.10f);
		Assert.Equal(RoadResult.TooSteepToJoin, path.Fit(field, 0f, 12f));
		Assert.Equal(0.12f, path.JoinGrade, 3);

		Assert.Equal(RoadResult.Ok, path.Fit(field, 0f, 9.9f));
		Assert.Equal(0f, path.Profile[0]);
		Assert.Equal(9.9f, path.Profile[^1]);
		for (int i = 1; i < path.Count; i++)
		{
			Assert.True(MathF.Abs(path.Profile[i] - path.Profile[i - 1]) <= (0.10f * path.Step) + 1e-4f);
		}
	}

	// ---- the line --------------------------------------------------------------------------------------

	[Fact]
	public void AStraight_IsSampledEvenly_EveryHalfMetreOrLess_EndToEnd()
	{
		RoadPath path = RoadPath.Of(new[] { new Vector2(-10f, 3f), new Vector2(20.3f, 3f) }, 6f, 0.10f);
		Assert.Equal(RoadResult.Ok, path.Shape);
		Assert.Equal(30.3f, path.Length, 3);
		Assert.Equal(62, path.Count);
		Assert.True(path.Step <= RoadPath.SampleMeters);
		Assert.Equal(new Vector2(-10f, 3f), path.Points[0]);
		Assert.Equal(new Vector2(20.3f, 3f), path.Points[^1]);
		for (int i = 1; i < path.Count; i++)
		{
			Assert.Equal(path.Step, (path.Points[i] - path.Points[i - 1]).Length(), 3);
			Assert.Equal(new Vector2(1f, 0f), path.Tangents[i]);
		}
	}

	[Theory]
	[InlineData(4f, 90f)]
	[InlineData(6f, 45f)]
	[InlineData(8f, 120f)]
	public void ACorner_IsAnArcOfTheRoadsWidth_TangentToBothStraights(float width, float turnDegrees)
	{
		float turn = turnDegrees * MathF.PI / 180f;
		var corner = new Vector2(0f, 0f);
		var start = new Vector2(-40f, 0f);
		var end = corner + (new Vector2(MathF.Cos(turn), MathF.Sin(turn)) * 40f);
		RoadPath path = RoadPath.Of(new[] { start, corner, end }, width, 0.10f);
		Assert.Equal(RoadResult.Ok, path.Shape);

		float tangent = width * MathF.Tan(turn * 0.5f);
		float expected = (2f * (40f - tangent)) + (width * turn);
		Assert.Equal(expected, path.Length, 2);

		// Every sample is the step from the last along the line, and those on the arc are its radius from its centre.
		Vector2 centre = new Vector2(-tangent, 0f) + new Vector2(0f, width);
		int onArc = 0;
		for (int i = 0; i < path.Count; i++)
		{
			if (i > 0)
			{
				Assert.True((path.Points[i] - path.Points[i - 1]).Length() <= path.Step + 1e-3f);
			}

			float s = path.DistanceAlong(i);
			if (s > 40f - tangent + 0.01f && s < 40f - tangent + (width * turn) - 0.01f)
			{
				Assert.Equal(width, (path.Points[i] - centre).Length(), 2);
				onArc++;
			}
		}

		Assert.True(onArc > 0);
		Assert.Equal(new Vector2(MathF.Cos(turn), MathF.Sin(turn)).X, path.Tangents[^1].X, 3);
		Assert.Equal(new Vector2(MathF.Cos(turn), MathF.Sin(turn)).Y, path.Tangents[^1].Y, 3);
	}

	[Fact]
	public void PointsTooCloseForTheirTurn_AreACornerTooTight()
	{
		// A 6 m road turning 90° needs 6 m of straight either side of the corner.
		Assert.Equal(RoadResult.CornerTooTight,
			RoadPath.Of(new[] { new Vector2(0f, 0f), new Vector2(5f, 0f), new Vector2(5f, 20f) }, 6f, 0.10f).Shape);
		Assert.Equal(RoadResult.Ok,
			RoadPath.Of(new[] { new Vector2(0f, 0f), new Vector2(6f, 0f), new Vector2(6f, 20f) }, 6f, 0.10f).Shape);

		// Two corners share the straight between them.
		Assert.Equal(RoadResult.CornerTooTight, RoadPath.Of(new[]
		{
			new Vector2(0f, 0f), new Vector2(20f, 0f), new Vector2(20f, 11f), new Vector2(0f, 11f),
		}, 6f, 0.10f).Shape);
		Assert.Equal(RoadResult.Ok, RoadPath.Of(new[]
		{
			new Vector2(0f, 0f), new Vector2(20f, 0f), new Vector2(20f, 12f), new Vector2(0f, 12f),
		}, 6f, 0.10f).Shape);

		// Two points in one place, and a road turned straight back on itself.
		Assert.Equal(RoadResult.CornerTooTight,
			RoadPath.Of(new[] { new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(10f, 0f) }, 6f, 0.10f).Shape);
		Assert.Equal(RoadResult.CornerTooTight,
			RoadPath.Of(new[] { new Vector2(0f, 0f), new Vector2(50f, 0f), new Vector2(0f, 0.5f) }, 6f, 0.10f).Shape);
	}

	[Theory]
	[InlineData(1, 6f, 0.10f)]
	[InlineData(65, 6f, 0.10f)]
	[InlineData(2, 5f, 0.10f)]
	[InlineData(2, 6f, 0.05f)]
	[InlineData(2, 6f, 0.20f)]
	public void ARoadOfNumbersItMayNotHave_IsMalformed(int points, float width, float grade)
	{
		Vector2[] line = Enumerable.Range(0, points).Select(i => new Vector2(i * 3f - 100f, (i % 2) * 0.01f)).ToArray();
		Assert.Equal(RoadResult.Malformed, RoadPath.Of(line, width, grade).Shape);
	}

	[Fact]
	public void APointOffTheField_IsMalformed_AndARoadOfAnotherStyleIsToo()
	{
		Assert.Equal(RoadResult.Malformed, RoadPath.Of(new[] { new Vector2(0f, 0f), new Vector2(121f, 0f) }, 6f, 0.10f).Shape);
		Assert.Equal(RoadResult.Malformed,
			RoadPath.Of(new MapRoad(1, 60, 100, new[] { new RoadPoint(0, 0), new RoadPoint(1000, 0) })).Shape);
		Assert.Equal(RoadResult.Ok, RoadPath.Of(new MapRoad(0, 60, 100, new[] { new RoadPoint(0, 0), new RoadPoint(1000, 0) })).Shape);
	}

	// ---- the ribbon -------------------------------------------------------------------------------------

	[Fact]
	public void TheRibbon_IsTheSamplesWithinHalfTheWidth_SquareAtItsEnds_LevelAcross()
	{
		var field = LevelGround.Plane(MapLimits.TerrainSamples, 0f, 0.05f, 0.02f);
		RoadPath path = RoadPath.Of(new[] { new Vector2(-20f, 5f), new Vector2(20f, 5f) }, 6f, 0.10f);
		Assert.Equal(RoadResult.Ok, path.Fit(field));
		List<ConformAnchor> ribbon = path.Ribbon(field);

		// 41 samples along, x from -20 to 20, and 7 across, z from 2 to 8.
		Assert.Equal(41 * 7, ribbon.Count);
		foreach (ConformAnchor anchor in ribbon)
		{
			float x = field.WorldX(anchor.X);
			float z = field.WorldZ(anchor.Z);
			Assert.InRange(x, -20f, 20f);
			Assert.InRange(z, 2f, 8f);
			Assert.Equal(path.ProfileAt(x + 20f), anchor.Target, 4);
		}

		// Level across: every sample of a column has the target of the one on the centreline.
		foreach (IGrouping<int, ConformAnchor> column in ribbon.GroupBy(a => a.X))
		{
			Assert.Single(column.Select(a => a.Target).Distinct());
		}

		Assert.True(path.OnRibbon(0f, 7.9f));
		Assert.False(path.OnRibbon(0f, 8.1f));
		Assert.False(path.OnRibbon(-20.5f, 5f));
		Assert.True(path.DistanceTo(-20.5f, 5f) < 1f);
	}

	[Fact]
	public void Coverage_IsWholeOnTheRibbon_AndNoneAMetrePastItsEdge()
	{
		var document = LevelMaps.Empty();
		document.Roads.Add(RoadPath.Record(new[] { new Vector2(-30f, 0f), new Vector2(30f, 0f) }, 6f, 0.10f));
		float[] coverage = RoadPath.Coverage(document);
		HeightField field = document.Terrain;
		float At(float x, float z) => coverage[field.Index((int)(x - field.OriginX), (int)(z - field.OriginZ))];
		Assert.Equal(1f, At(0f, 0f));
		Assert.Equal(1f, At(0f, 2f));
		Assert.Equal(0.5f, At(0f, 3f));
		Assert.Equal(0f, At(0f, 4f));
		Assert.Equal(0f, At(0f, -10f));
		Assert.Equal(0f, At(-31f, 0f));
		Assert.Equal(0f, coverage.Count(c => c < 0f || c > 1f));
	}

	// ---- bends and crossings -----------------------------------------------------------------------------

	/// <summary>
	/// A loop on the tightest radius a road has, its width: <paramref name="corners"/> corners of
	/// equal turn, each straight between two of them exactly as long as their arcs need, so that
	/// the arcs meet as one; a straight in and a straight out.
	/// </summary>
	private static Vector2[] Loop(float width, float turnDegrees, int corners)
	{
		// No straight either end: the road is the arc, its first point where the arc starts and its last where it ends.
		float each = turnDegrees / corners * MathF.PI / 180f;
		float tangent = width * MathF.Tan(each * 0.5f);
		var at = new Vector2(-20f, -40f);
		var points = new List<Vector2> { at - new Vector2(tangent, 0f), at };
		float heading = 0f;
		for (int k = 1; k < corners; k++)
		{
			heading += each;
			at += new Vector2(MathF.Cos(heading), MathF.Sin(heading)) * (2f * tangent);
			points.Add(at);
		}

		heading += each;
		points.Add(at + (new Vector2(MathF.Cos(heading), MathF.Sin(heading)) * tangent));
		return points.ToArray();
	}

	/// <summary>A loop climbing at its limit end to end, which its ends pinned make it, and §6.3's condition on its ribbon.</summary>
	private static ConformResult Climbing(float width, float grade, float turnDegrees, int corners)
	{
		var field = new HeightField();
		RoadPath path = RoadPath.Of(Loop(width, turnDegrees, corners), width, grade);
		Assert.Equal(RoadResult.Ok, path.Shape);
		Assert.Equal(RoadResult.Ok, path.Fit(field, 0f, grade * path.Length));
		return TerrainConform.Apply(field.Clone(), path.Ribbon(field).ToArray(), out _);
	}

	[Theory]
	[InlineData(0.10f, 258f)]
	[InlineData(0.15f, 212f)]
	public void RoundTheTightestBend_TheRibbonPassesTheSkirt_Past258DegreesAt10Percent_And212At15(float grade, float theory)
	{
		// §10: the inside edge, at half the width from the arc's centre, climbs as the centreline does
		// over half the distance; its targets pass tan 30° between two points on it an angle θ apart when
		// g · θ / sin(θ / 2) passes tan 30°: at 258° of turn for 10 %, 212° for 15 %. On the grid the
		// inside edge is the samples within half the width, most of them further out than the edge,
		// which draws the targets apart: the turn a road is refused at is found, every 2°, and is a
		// little past the edge's — never short of it.
		int first = -1;
		for (int turn = 120; turn <= 340 && first < 0; turn += 2)
		{
			if (Climbing(6f, grade, turn, (int)MathF.Ceiling(turn / 60f)) == ConformResult.TooSteep)
			{
				first = turn;
			}
		}

		_output.WriteLine($"{100f * grade:0} %: first refused at {first}° of turn, where the inside edge passes the skirt at {theory}°");
		Assert.InRange(first, (int)theory, (int)theory + 30);
		Assert.Equal(ConformResult.Ok, Climbing(6f, grade, 180f, 3));
	}

	[Fact]
	public void ALoopOnLevelGround_IsTaken_ForItsTargetsAreLevel()
	{
		var field = new HeightField();
		RoadPath path = RoadPath.Of(Loop(6f, 300f, 5), 6f, 0.10f);
		Assert.Equal(RoadResult.Ok, path.Fit(field));
		Assert.Equal(ConformResult.Ok, TerrainConform.Apply(field.Clone(), path.Ribbon(field).ToArray(), out _));
	}

	[Fact]
	public void ACentrelineThatCrossesItself_IsSeen_AndOneThatDoesNotIsNot()
	{
		Vector2[] crossing = { new(-30f, -30f), new(30f, 30f), new(30f, -30f), new(-30f, 30f) };
		Assert.True(RoadPath.Of(crossing, 6f, 0.10f).CrossesItself());
		Vector2[] zigzag = { new(-30f, -30f), new(30f, 30f), new(30f, -30f), new(60f, 30f) };
		Assert.False(RoadPath.Of(zigzag, 6f, 0.10f).CrossesItself());
		Assert.False(RoadPath.Of(Loop(6f, 300f, 5), 6f, 0.10f).CrossesItself());
	}

	// ---- a road on a map -------------------------------------------------------------------------------

	/// <summary>A map whose ground is a ridge along z, 12 m high at x = 0 and falling at 40 % either side to level ground.</summary>
	private static MapDocument Ridge()
	{
		MapDocument document = LevelMaps.Empty();
		HeightField field = document.Terrain;
		for (int z = 0; z < field.Depth; z++)
		{
			for (int x = 0; x < field.Width; x++)
			{
				field[x, z] = MathF.Max(0f, 12f - (0.4f * MathF.Abs(field.WorldX(x))));
			}
		}

		field.Round(field.Bounds);
		return document;
	}

	/// <summary>The steepest the ground is between two neighbouring centreline samples, as a grade.</summary>
	private static float Steepest(HeightField field, RoadPath path)
	{
		float steepest = 0f;
		for (int i = 1; i < path.Count; i++)
		{
			float rise = field.Sample(path.Points[i].X, path.Points[i].Y) - field.Sample(path.Points[i - 1].X, path.Points[i - 1].Y);
			steepest = MathF.Max(steepest, MathF.Abs(rise) / path.Step);
		}

		return steepest;
	}

	public static IEnumerable<object[]> Lines() => new[]
	{
		new object[] { "straight along x", new[] { new Vector2(-60f, 0f), new Vector2(60f, 0f) }, 6f, 0.10f },
		new object[] { "straight at an angle", new[] { new Vector2(-60f, -20f), new Vector2(60f, 25f) }, 6f, 0.10f },
		new object[] { "8 m at 6 %", new[] { new Vector2(-80f, 10f), new Vector2(80f, -10f) }, 8f, 0.06f },
		new object[] { "4 m at 15 %", new[] { new Vector2(-50f, 30f), new Vector2(50f, 31f) }, 4f, 0.15f },
		new object[] { "two corners", new[] { new Vector2(-70f, -40f), new Vector2(-20f, -10f), new Vector2(25f, 10f), new Vector2(70f, 45f) }, 6f, 0.10f },
	};

	[Theory]
	[MemberData(nameof(Lines))]
	public void ARoadAcrossARidge_KeepsItsGradeAlongItsCentreline_AndTheValidatorFindsItSo(string what, Vector2[] line,
		float width, float grade)
	{
		MapDocument document = Ridge();
		MapRoad road = RoadPath.Record(line, width, grade);
		var session = new MapEditSession(document);
		short[] before = document.Terrain.ToSamples();
		RoadCheck check = session.ApplyRoad(road, -1, out EditOutcome outcome);
		Assert.True(check.IsOk, $"{what}: {check.Result}");
		Assert.True(outcome.Step && outcome.RoadsChanged && !outcome.ObjectsChanged);
		Assert.Equal(road, Assert.Single(document.Roads));

		HeightField field = document.Terrain;
		RoadPath path = RoadPath.Of(road);
		float steepest = Steepest(field, path);
		float excess = path.ExcessOf(field);
		float faces = HeightField.Degrees(field.SteepestFace());
		_output.WriteLine($"{what}: steepest along {100f * steepest:0.000} % of {100f * grade:0} %, E {100f * excess:0.00} cm,"
			+ $" cut {check.Path.DeepestCut:0.00} m, fill {check.Path.DeepestFill:0.00} m, steepest face anywhere {faces:0.0}°");

		// A step of 1/256 m either end of half a metre is 0.78 %: what rounding the ground can add.
		Assert.True(steepest <= grade + (2f * MapLimits.HeightStepMeters / path.Step), $"{what}: {steepest} along it");
		Assert.True(excess <= MapValidator.RoadToleranceMeters, $"{what}: E is {excess} m");
		Assert.True(faces < MapLimits.WalkableSlopeDegrees, $"{what}: a face of {faces}° left");
		Assert.False(MapValidator.Validate(document).Has(MapIssueCode.RoadOffItsGrade));

		// Undo puts every sample back and the road out; redo the road and its ground again.
		Assert.True(session.Undo(out EditOutcome undone) && undone.RoadsChanged);
		Assert.Empty(document.Roads);
		Assert.Equal(before, field.ToSamples());
		Assert.True(session.Redo(out _));
		Assert.Equal(road, Assert.Single(document.Roads));
		Assert.Equal(steepest, Steepest(field, path));
	}

	[Fact]
	public void ARoadEditedOverAfterwards_IsWarnedOf_AtItsWorst()
	{
		MapDocument document = Ridge();
		var session = new MapEditSession(document);
		Assert.True(session.ApplyRoad(RoadPath.Record(new[] { new Vector2(-60f, 0f), new Vector2(60f, 0f) }, 6f, 0.10f), -1, out _).IsOk);
		Assert.False(MapValidator.Validate(document).Has(MapIssueCode.RoadOffItsGrade));

		// A mound raised on the road where it crosses x = 30, two seconds of a Hill.
		session.Brush = TerrainBrush.Presets[1].Settings;
		Assert.True(session.BeginStroke(30f, 0f, false));
		for (int frame = 0; frame < 60; frame++)
		{
			session.StrokeTo(30f, 0f, MapEditSession.DabSeconds);
		}

		EditOutcome stroke = session.EndStroke();
		float excess = RoadPath.Of(document.Roads[0]).ExcessOf(document.Terrain);
		_output.WriteLine($"stroke: {stroke}; E now {excess:0.000} m");
		Assert.True(excess > MapValidator.RoadToleranceMeters);
		MapReport report = MapValidator.Validate(document);
		Assert.True(report.TryGet(MapIssueCode.RoadOffItsGrade, out MapIssue issue));
		Assert.Equal(MapIssueSeverity.Warning, issue.Severity);
		Assert.Contains("road 0 is steeper than its 10 %", issue.Message);
		Assert.InRange(issue.Where.X, 14f, 46f);
	}

	[Fact]
	public void ARoadThatCannotBeDrawn_IsWarnedOf()
	{
		MapDocument document = LevelMaps.Empty();
		document.Roads.Add(new MapRoad(0, 60, 100, new[] { new RoadPoint(0, 0), new RoadPoint(500, 0), new RoadPoint(500, 2000) }));
		Assert.True(MapValidator.Validate(document).TryGet(MapIssueCode.RoadOffItsGrade, out MapIssue issue));
		Assert.Contains("road 0 cannot be drawn: a corner is too tight", issue.Message);
	}

	[Fact]
	public void ARoadCrossingItselfAtTwoHeights_IsRefused_AndChangesNothing()
	{
		MapDocument document = LevelMaps.Empty();
		HeightField field = document.Terrain;
		for (int z = 0; z < field.Depth; z++)
		{
			for (int x = 0; x < field.Width; x++)
			{
				field[x, z] = 0.3f * field.WorldZ(z) + 40f;
			}
		}

		field.Round(field.Bounds);
		short[] before = field.ToSamples();
		var session = new MapEditSession(document);
		MapRoad road = RoadPath.Record(new[] { new Vector2(-30f, -30f), new Vector2(30f, 30f), new Vector2(30f, -30f), new Vector2(-30f, 30f) },
			6f, 0.10f);
		RoadCheck check = session.ApplyRoad(road, -1, out EditOutcome outcome);
		Assert.Equal(RoadResult.CrossesItself, check.Result);
		Assert.False(outcome.Step);
		Assert.Empty(document.Roads);
		Assert.Equal(before, field.ToSamples());
		Assert.Equal(0, session.History.UndoCount);
	}

	[Fact]
	public void ARoadThatWouldCutAPad_DisturbsItsNeighbour_AndAPadOnARoadIsInItsWay()
	{
		MapDocument document = Ridge();
		var session = new MapEditSession(document);
		PlacementCheck locker = session.Place(new PlacementRequest(PrefabSpecs.WeaponLocker, -40f, 0f, 0f), out _);
		Assert.True(locker.IsOk);

		RoadCheck through = session.CheckRoad(RoadPath.Record(new[] { new Vector2(-60f, 0f), new Vector2(60f, 0f) }, 6f, 0.10f));
		Assert.Equal(RoadResult.DisturbsNeighbour, through.Result);
		Assert.Equal(0, through.Other);

		RoadCheck beside = session.ApplyRoad(RoadPath.Record(new[] { new Vector2(-60f, 20f), new Vector2(60f, 20f) }, 6f, 0.10f), -1,
			out _);
		Assert.True(beside.IsOk, beside.Result.ToString());

		PlacementCheck onIt = session.Check(new PlacementRequest(PrefabSpecs.CoverBlock, -50f, 22f, 0f));
		Assert.Equal(PrefabPlacementResult.InTheWay, onIt.Result);
		Assert.Equal(0, onIt.OtherRoad);
		Assert.Equal(-1, onIt.Other);

		// An ammunition can makes no pad: it may stand on a road.
		Assert.True(session.Check(new PlacementRequest(PrefabSpecs.AmmoCan, -50f, 20f, 0f)).IsOk);

		// A file that has one anyway is refused by the validator, as two objects in each other's way are.
		document.Objects.Add(onIt.Object);
		Assert.True(MapValidator.Validate(document).TryGet(MapIssueCode.InTheWay, out MapIssue issue));
		Assert.Contains("stands on road 0", issue.Message);
	}

	[Fact]
	public void ARoadWhoseRibbonAPadsSkirtWouldMove_DisturbsIt_ThoughItsOwnSkirtMovesNoLevelGround()
	{
		// Level ground, and from z = 4 on a shelf 0.45 m up. A locker's pad at (0, 0.4), level at 0 m: its
		// level ground's edge 3.4 m out, and its skirt pulling the shelf's first row to 0.35 m. A road along
		// z = 7 at the shelf's height: its ribbon's first row is 1 m from the pad's level samples, which its
		// own skirt leaves alone, and 0.6 m from where the pad's skirt starts, which keeping the pad would
		// pull the ribbon down to.
		MapDocument document = LevelMaps.Empty();
		HeightField field = document.Terrain;
		for (int z = 0; z < field.Depth; z++)
		{
			for (int x = 0; x < field.Width; x++)
			{
				field[x, z] = field.WorldZ(z) >= 4f ? 0.45f : 0f;
			}
		}

		field.Round(field.Bounds);
		var session = new MapEditSession(document);
		Assert.True(session.Place(new PlacementRequest(PrefabSpecs.WeaponLocker, 0f, 0.4f, 0f), out _).IsOk);
		RoadCheck check = session.CheckRoad(RoadPath.Record(new[] { new Vector2(-40f, 7f), new Vector2(40f, 7f) }, 6f, 0.10f));
		Assert.Equal(RoadResult.DisturbsNeighbour, check.Result);
		Assert.Equal(0, check.Other);
	}

	[Fact]
	public void ARoadsPointsMoved_GradeTheNewLine_KeepItsPlace_AndLeaveTheOldLinesGround()
	{
		MapDocument document = Ridge();
		var session = new MapEditSession(document);
		Assert.True(session.ApplyRoad(RoadPath.Record(new[] { new Vector2(-60f, -30f), new Vector2(60f, -30f) }, 6f, 0.10f), -1, out _).IsOk);
		Assert.True(session.ApplyRoad(RoadPath.Record(new[] { new Vector2(-60f, 0f), new Vector2(60f, 0f) }, 6f, 0.10f), -1, out _).IsOk);
		HeightField field = document.Terrain;
		short[] graded = field.ToSamples();
		float oldTop = field.Sample(0f, 0f);

		MapRoad moved = RoadPath.Record(new[] { new Vector2(-60f, 30f), new Vector2(60f, 30f) }, 6f, 0.10f);
		RoadCheck check = session.ApplyRoad(moved, 1, out EditOutcome outcome);
		Assert.True(check.IsOk && outcome.RoadsChanged);
		Assert.Equal(2, document.Roads.Count);
		Assert.Equal(moved, document.Roads[1]);
		Assert.Equal(oldTop, field.Sample(0f, 0f));
		Assert.True(Steepest(field, RoadPath.Of(moved)) <= 0.10f + (2f * MapLimits.HeightStepMeters / 0.5f));

		Assert.True(session.TakeUpRoad(0, out EditOutcome taken) && taken.RoadsChanged);
		Assert.Equal(moved, Assert.Single(document.Roads));
		Assert.Equal(oldTop, field.Sample(0f, 0f));
		Assert.True(session.Undo(out _));
		Assert.Equal(2, document.Roads.Count);
		Assert.True(session.Undo(out _));
		Assert.Equal(graded, field.ToSamples());
	}

	[Fact]
	public void ThirtyThreeRoads_AreOneTooMany()
	{
		MapDocument document = LevelMaps.Empty();
		for (int i = 0; i < MapLimits.MaxRoads; i++)
		{
			document.Roads.Add(RoadPath.Record(new[] { new Vector2(-100f, -96f + (6f * i)), new Vector2(100f, -96f + (6f * i)) }, 4f, 0.10f));
		}

		Assert.Equal(RoadResult.LimitReached,
			RoadPath.Check(document, RoadPath.Record(new[] { new Vector2(0f, 0f), new Vector2(10f, 0f) }, 4f, 0.10f)).Result);
		Assert.Equal(RoadResult.Ok,
			RoadPath.Check(document, RoadPath.Record(new[] { new Vector2(0f, 0f), new Vector2(10f, 0f) }, 4f, 0.10f), 3).Result);
	}

	[Fact]
	public void AFootprintOnTheRibbon_IsUnderTheRoad_AndOneBesideItIsNot()
	{
		MapDocument document = LevelMaps.Empty();
		document.Roads.Add(RoadPath.Record(new[] { new Vector2(-30f, 0f), new Vector2(30f, 0f) }, 6f, 0.10f));
		Assert.Equal(0, RoadPath.RoadUnder(document, PadArea.Of(new Footprint(new Vector3(0f, 0f, 4f), 0f, 1.5f, 1.5f))));
		Assert.Equal(-1, RoadPath.RoadUnder(document, PadArea.Of(new Footprint(new Vector3(0f, 0f, 5f), 0f, 1.5f, 1.5f))));
		Assert.Equal(0, RoadPath.RoadUnder(document, PadArea.Disc(0f, 14f, 11.5f)));
		Assert.Equal(-1, RoadPath.RoadUnder(document, PadArea.Disc(0f, 15f, 11.5f)));
		Assert.Equal(0, MapEditSession.PickRoad(document, 10f, 2.9f));
		Assert.Equal(-1, MapEditSession.PickRoad(document, 10f, 3.1f));
	}
}
