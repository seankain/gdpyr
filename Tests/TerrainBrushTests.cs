using System;
using Gdpyr.Sim.Level;
using Xunit;

namespace Gdpyr.Tests;

/// <summary>
/// The terrain brushes (docs/LEVEL_EDITOR.md §6.2): what a dab adds, the grade a
/// sculpt brush's settings come to, the grade a grade-locked brush holds whatever
/// it is drawn over, and what smooth, level and a ramp do. §10's tables are here as
/// values, and what §6.2 claims of a grade is claimed over seeded strokes.
/// </summary>
public class TerrainBrushTests
{
	private const float Sixtieth = 1f / 60f;

	// ---- the falloff, and a sculpt dab ------------------------------------------------------

	[Theory]
	[InlineData(0f)]
	[InlineData(0.5f)]
	[InlineData(0.8f)]
	[InlineData(0.9f)]
	public void TheFalloff_IsOneInsideTheCore_ASmoothstepDownToNothingAtTheRadius(float core)
	{
		const float radius = 16f;

		Assert.Equal(1f, TerrainBrush.Weight(0f, radius, core));
		Assert.Equal(1f, TerrainBrush.Weight(core * radius, radius, core));
		Assert.Equal(0f, TerrainBrush.Weight(radius, radius, core));
		Assert.Equal(0f, TerrainBrush.Weight(radius * 3f, radius, core));
		Assert.Equal(0f, TerrainBrush.Weight(1f, 0f, core));
		LevelGround.Near(0.5, TerrainBrush.Weight((core + ((1f - core) * 0.5f)) * radius, radius, core), 1e-5);

		// It only falls, and at its steepest by 1.5 over the falloff's width.
		double steepest = 0d;
		const double step = 1e-3;
		for (double r = 0d; r < radius; r += step)
		{
			double fall = TerrainBrush.Weight((float)r, radius, core) - TerrainBrush.Weight((float)(r + step), radius, core);
			LevelGround.True(fall >= -1e-6, $"the falloff rises at {r} m");
			steepest = Math.Max(steepest, fall / step);
		}

		LevelGround.Near(1.5 / ((1d - core) * radius), steepest, 0.02 * 1.5 / ((1d - core) * radius));
	}

	[Fact]
	public void ASculptDab_AddsItsAmountUnderTheFalloff_AndNothingPastTheRadius()
	{
		const float radius = 8f;
		const float core = 0.25f;
		HeightField field = LevelGround.Noise(65, 3f, seed: 61);
		HeightField before = field.Clone();

		SampleRect touched = TerrainBrush.SculptDab(field, 0.3f, -0.2f, radius, core, 0.5f);

		for (int z = 0; z < field.Depth; z++)
		{
			for (int x = 0; x < field.Width; x++)
			{
				double dx = field.WorldX(x) - 0.3;
				double dz = field.WorldZ(z) + 0.2;
				double t = Math.Sqrt((dx * dx) + (dz * dz)) / radius;
				double s = Math.Clamp((t - core) / (1d - core), 0d, 1d);
				double weight = t >= 1d ? 0d : 1d - (s * s * (3d - (2d * s)));
				LevelGround.Near(before[x, z] + (0.5 * weight), field[x, z], 1e-5);
				if (t >= 1d)
				{
					Assert.Equal(before[x, z], field[x, z]);
				}
			}
		}

		Assert.Equal(LevelGround.Changed(before, field), touched);

		// The right button is the left one turned over.
		HeightField lowered = before.Clone();
		TerrainBrush.SculptDab(lowered, 0.3f, -0.2f, radius, core, -0.5f);
		for (int i = 0; i < field.Heights.Length; i++)
		{
			LevelGround.Near(before.Heights[i] - (field.Heights[i] - before.Heights[i]), lowered.Heights[i], 1e-5);
		}
	}

	/// <summary>§10, "The sculpt brush": the steepest grade once the centre has risen H, on the grid and by the formula.</summary>
	[Theory]
	[InlineData(4f, 0f, 2f, 35.60, 36.87)]
	[InlineData(8f, 0f, 2f, 20.16, 20.56)]
	[InlineData(16f, 0f, 2f, 10.58, 10.62)]
	[InlineData(32f, 0f, 2f, 5.35, 5.36)]
	[InlineData(16f, 0.5f, 2f, 20.36, 20.56)]
	[InlineData(16f, 0.8f, 2f, 42.09, 43.15)]
	[InlineData(16f, 0.5f, 6f, 48.07, 48.37)]
	public void ASculptBrushesSteepestGrade_IsSection10s(float radius, float core, float rise, double onTheGrid, double byTheFormula)
	{
		HeightField field = LevelGround.Flat(129);
		for (int dab = 0; dab < 200; dab++)
		{
			TerrainBrush.SculptDab(field, 0f, 0f, radius, core, rise / 200f);
		}

		LevelGround.Near(rise, field.Sample(0f, 0f), 1e-4);
		LevelGround.Near(onTheGrid, LevelGround.Degrees(LevelGround.SteepestEdge(field)), 0.006);
		LevelGround.Near(byTheFormula, LevelGround.Degrees(TerrainBrush.SculptGrade(rise, radius, core)), 0.006);
	}

	[Fact]
	public void ASculptBrushesGrade_IsNeverOverOnePointFiveHOverItsFalloff_WhereverItIsHeld()
	{
		var random = new Random(67);
		for (int trial = 0; trial < 60; trial++)
		{
			float radius = LevelGround.Uniform(random, 2f, 48f);
			float core = LevelGround.Uniform(random, 0f, 0.9f);
			float rise = LevelGround.Uniform(random, 0.5f, 6f);
			float x = LevelGround.Uniform(random, -2f, 2f);
			float z = LevelGround.Uniform(random, -2f, 2f);
			bool lower = trial % 3 == 0;

			HeightField field = LevelGround.Flat(129);
			for (int dab = 0; dab < 50; dab++)
			{
				TerrainBrush.SculptDab(field, x, z, radius, core, (lower ? -rise : rise) / 50f);
			}

			double formula = TerrainBrush.SculptGrade(rise, radius, core);
			double grid = LevelGround.SteepestEdge(field);
			LevelGround.True(grid <= formula * (1d + 1e-4), $"R {radius} core {core} H {rise}: {grid} on the grid, over the formula's {formula}");

			// And the formula is the grid's, once the falloff is some samples wide.
			if ((1f - core) * radius >= 8f)
			{
				LevelGround.True(grid >= formula * 0.97, $"R {radius} core {core} H {rise}: {grid} on the grid, well under the formula's {formula}");
			}
		}
	}

	[Fact]
	public void AWiderOrSofterBrush_IsAShallowerGrade_ANarrowerOrHarderOneSteeper()
	{
		static double Steepest(float radius, float core)
		{
			HeightField field = LevelGround.Flat(161);
			for (int dab = 0; dab < 40; dab++)
			{
				TerrainBrush.SculptDab(field, 0f, 0f, radius, core, 0.05f);
			}

			return LevelGround.SteepestEdge(field);
		}

		float[] radii = { 4f, 8f, 16f, 32f, 64f };
		for (int i = 1; i < radii.Length; i++)
		{
			LevelGround.True(Steepest(radii[i], 0.3f) < Steepest(radii[i - 1], 0.3f), $"{radii[i]} m is not shallower than {radii[i - 1]} m");
		}

		float[] cores = { 0f, 0.2f, 0.4f, 0.6f, 0.8f, 0.9f };
		for (int i = 1; i < cores.Length; i++)
		{
			LevelGround.True(Steepest(16f, cores[i]) > Steepest(16f, cores[i - 1]), $"a core of {cores[i]} is not steeper than {cores[i - 1]}");
		}
	}

	[Fact]
	public void ASlowBrush_Accumulates_WhileTheStrokeIsDown_AndIsWholeStepsWhenItEnds()
	{
		// 0.25 m/s at 240 dabs a second is 1 mm a dab: under half a step. Rounded dab by dab it would never rise.
		var brush = new BrushSettings { Kind = BrushKind.Sculpt, RadiusMeters = 8f, RateMetersPerSecond = 0.25f };
		HeightField field = LevelGround.Flat(65);
		HeightField rounded = LevelGround.Flat(65);
		BrushStroke stroke = TerrainBrush.Begin(field, brush, 0f, 0f, lower: false);
		SampleRect touched = SampleRect.Empty;

		for (int dab = 0; dab < 240; dab++)
		{
			touched = touched.Union(TerrainBrush.Dab(field, stroke, 0f, 0f, 1f / 240f));
			rounded.Round(TerrainBrush.Dab(rounded, stroke, 0f, 0f, 1f / 240f));
		}

		LevelGround.Near(0.25, field.Sample(0f, 0f), 1e-4);
		Assert.Equal(0f, rounded.Sample(0f, 0f));

		field.Round(touched);
		Assert.Equal(64, HeightField.ToSample(field.Sample(0f, 0f)));
		Assert.Equal(field.Heights, HeightField.FromSamples(field.ToSamples(), 65, 65).Heights);
	}

	[Theory]
	[InlineData("Hill")]
	[InlineData("Mound 20°")]
	public void TheRightButton_LowersWhereTheLeftRaises_AtTheBrushesRate(string name)
	{
		BrushSettings brush = Array.Find(TerrainBrush.Presets, preset => preset.Name == name).Settings;
		HeightField raised = LevelGround.Flat(65, 3f);
		HeightField lowered = LevelGround.Flat(65, 3f);
		BrushStroke up = TerrainBrush.Begin(raised, brush, 0f, 0f, lower: false);
		BrushStroke down = TerrainBrush.Begin(lowered, brush, 0f, 0f, lower: true);
		Assert.False(up.Lower);
		Assert.True(down.Lower);

		// A second of it, in dabs of two lengths: 2 m/s is 2 m either way.
		for (int dab = 0; dab < 40; dab++)
		{
			float dt = dab < 20 ? 1f / 60f : 1f / 30f;
			TerrainBrush.Dab(raised, up, 0f, 0f, dt);
			TerrainBrush.Dab(lowered, down, 0f, 0f, dt);
		}

		LevelGround.Near(5d, raised.Sample(0f, 0f), 1e-3);
		LevelGround.Near(1d, lowered.Sample(0f, 0f), 1e-3);
		for (int i = 0; i < raised.Heights.Length; i++)
		{
			LevelGround.Near(raised.Heights[i] - 3d, 3d - lowered.Heights[i], 1e-4);
		}
	}

	// ---- the presets and the ranges ---------------------------------------------------------

	[Fact]
	public void ThePresets_AreSection6Point2sTable()
	{
		(string Name, BrushKind Kind, float Radius, float Core, float RateOrGrade)[] table =
		{
			("Plain", BrushKind.Sculpt, 32f, 0f, 1f),
			("Hill", BrushKind.Sculpt, 16f, 0f, 2f),
			("Bank", BrushKind.Sculpt, 16f, 0.5f, 2f),
			("Bluff", BrushKind.Sculpt, 16f, 0.8f, 2f),
			("Mound 10°", BrushKind.GradeLocked, 16f, 0f, 10f),
			("Mound 20°", BrushKind.GradeLocked, 16f, 0f, 20f),
			("Mound 30°", BrushKind.GradeLocked, 16f, 0f, 30f),
			("Cliff 60°", BrushKind.GradeLocked, 8f, 0f, 60f),
			("Smooth", BrushKind.Smooth, 8f, 0f, 0.5f),
			("Level", BrushKind.Level, 8f, 0f, 0.5f),
			("Ramp", BrushKind.Ramp, 8f, 0f, 0.5f),
		};

		Assert.Equal(table.Length, TerrainBrush.Presets.Length);
		for (int i = 0; i < table.Length; i++)
		{
			BrushPreset preset = TerrainBrush.Presets[i];
			BrushSettings settings = preset.Settings;
			Assert.Equal(table[i].Name, preset.Name);
			Assert.Equal(table[i].Kind, settings.Kind);
			Assert.Equal(table[i].Radius, settings.RadiusMeters);
			Assert.Equal(table[i].Core, settings.Core);
			Assert.Equal(table[i].RateOrGrade, settings.Kind switch
			{
				BrushKind.Sculpt => settings.RateMetersPerSecond,
				BrushKind.GradeLocked => settings.GradeDegrees,
				_ => settings.Strength,
			});

			// A preset is a place the sliders can be: clamping one changes nothing.
			Assert.Equal(settings, settings.Clamped());
		}
	}

	/// <summary>§6.2's "Makes" column for the four sculpt presets, each held until its centre has risen 2 m.</summary>
	[Theory]
	[InlineData("Plain", 5.4)]
	[InlineData("Hill", 10.6)]
	[InlineData("Bank", 20.4)]
	[InlineData("Bluff", 42.1)]
	public void ASculptPreset_MakesTheGradeItsRowSays_OnceTwoMetresIsRaised(string name, double degrees)
	{
		BrushSettings brush = Array.Find(TerrainBrush.Presets, preset => preset.Name == name).Settings;
		HeightField field = LevelGround.Flat(129);
		BrushStroke stroke = TerrainBrush.Begin(field, brush, 0f, 0f, lower: false);

		int dabs = (int)MathF.Round(2f / (brush.RateMetersPerSecond * Sixtieth));
		for (int dab = 0; dab < dabs; dab++)
		{
			TerrainBrush.Dab(field, stroke, 0f, 0f, Sixtieth);
		}

		LevelGround.Near(2d, field.Sample(0f, 0f), 1e-3);
		LevelGround.Near(degrees, LevelGround.Degrees(LevelGround.SteepestEdge(field)), 0.05);
	}

	[Fact]
	public void ABrushesSettings_ArePulledIntoTheirRanges()
	{
		BrushSettings low = new BrushSettings
		{
			Kind = BrushKind.GradeLocked, RadiusMeters = 0.1f, Core = -1f, RateMetersPerSecond = 0f, GradeDegrees = 1f, Strength = -3f,
		}.Clamped();
		Assert.Equal(2f, low.RadiusMeters);
		Assert.Equal(0f, low.Core);
		Assert.Equal(0.25f, low.RateMetersPerSecond);
		Assert.Equal(5f, low.GradeDegrees);
		Assert.Equal(0f, low.Strength);
		Assert.Equal(BrushKind.GradeLocked, low.Kind);

		BrushSettings high = new BrushSettings
		{
			RadiusMeters = 500f, Core = 1f, RateMetersPerSecond = 100f, GradeDegrees = 89f, Strength = 7f,
		}.Clamped();
		Assert.Equal(64f, high.RadiusMeters);
		Assert.Equal(0.9f, high.Core);
		Assert.Equal(8f, high.RateMetersPerSecond);
		Assert.Equal(70f, high.GradeDegrees);
		Assert.Equal(1f, high.Strength);

		BrushSettings nothing = new BrushSettings
		{
			RadiusMeters = float.NaN, Core = float.NaN, RateMetersPerSecond = float.NaN, GradeDegrees = float.NaN, Strength = float.NaN,
		}.Clamped();
		Assert.Equal(low.RadiusMeters, nothing.RadiusMeters);
		Assert.Equal(low.GradeDegrees, nothing.GradeDegrees);

		// A stroke begins with them clamped, whatever it was handed.
		Assert.Equal(64f, TerrainBrush.Begin(LevelGround.Flat(9), new BrushSettings { RadiusMeters = 500f }, 0f, 0f, false).Brush.RadiusMeters);
	}

	// ---- grade-locked -----------------------------------------------------------------------

	[Fact]
	public void AGradeLockedStroke_MovesNothingOnOrOutsideItsRim_AndLeavesNoEdgeSteeperThanItsGradeOrWhatTheEdgeWas()
	{
		// §10, "The grade-locked brush": 200 strokes of 80 dabs over rough ground, raising and
		// lowering, 5–60°, radii 4–24 m.
		const int n = 129;
		var random = new Random(5);
		double[] grades = { 5, 10, 20, 30, 45, 60 };
		float[] radii = { 4f, 8f, 16f, 24f };
		double steepestFace = 0d;

		for (int stroke = 0; stroke < 200; stroke++)
		{
			HeightField ground = LevelGround.Noise(n, LevelGround.Uniform(random, 0f, 4f), seed: 1000 + stroke);
			HeightField field = ground.Clone();
			float grade = (float)LevelGround.Grade(grades[random.Next(grades.Length)]);
			float radius = radii[random.Next(radii.Length)];
			bool lower = random.Next(2) == 0;
			float x = LevelGround.Uniform(random, -24f, 24f);
			float z = LevelGround.Uniform(random, -24f, 24f);
			double heading = random.NextDouble() * Math.PI * 2d;

			HeightField was = field.Clone();
			for (int dab = 0; dab < 80; dab++)
			{
				LevelGround.Copy(field, was);
				SampleRect touched = TerrainBrush.GradeLockedDab(field, x, z, radius, grade, 0.25f, lower);

				Assert.Equal(LevelGround.Changed(was, field), touched);
				for (int sz = touched.MinZ; sz <= touched.MaxZ; sz++)
				{
					for (int sx = touched.MinX; sx <= touched.MaxX; sx++)
					{
						double dx = field.WorldX(sx) - x;
						double dz = field.WorldZ(sz) - z;
						if (was[sx, sz] != field[sx, sz])
						{
							LevelGround.True(Math.Sqrt((dx * dx) + (dz * dz)) <= radius - TerrainBrush.RimMeters + 1e-4,
								$"a sample {Math.Sqrt((dx * dx) + (dz * dz))} m from a dab of {radius} m moved");
							Assert.True(lower ? field[sx, sz] < was[sx, sz] : field[sx, sz] > was[sx, sz]);
						}
					}
				}

				heading += (random.NextDouble() - 0.5) * 1.2;
				x = Math.Clamp(x + (0.5f * (float)Math.Cos(heading)), -34f, 34f);
				z = Math.Clamp(z + (0.5f * (float)Math.Sin(heading)), -34f, 34f);
			}

			double worst = LevelGround.WorstEdge(ground, field, grade);
			LevelGround.True(worst <= 1e-4, $"stroke {stroke}: an edge ended {worst} steeper than the steeper of {grade} and what it was");

			double limit = Math.Max(grade, LevelGround.SteepestEdge(ground));
			double face = LevelGround.SteepestFace(field);
			LevelGround.True(face <= (limit * Math.Sqrt(2d)) + 1e-4, $"stroke {stroke}: a face of {face}, over √2 times {limit}");
			steepestFace = Math.Max(steepestFace, face / Math.Max(grade, LevelGround.SteepestFace(ground)));
		}

		// Faces do get the √2, or some of it: the guarantee is for edges.
		Assert.InRange(steepestFace, 1.05, Math.Sqrt(2d) + 1e-4);
	}

	/// <summary>
	/// §10, "The grade-locked brush", held still on level ground until it stops: the
	/// height it reaches, and the steepest face it leaves with its tip sharp and
	/// rounded over 1, 2 and 3 m.
	/// </summary>
	[Theory]
	[InlineData(10.0, 8f, 0.88, 14.00, 10.48, 10.02, 9.46)]
	[InlineData(20.0, 8f, 1.82, 27.24, 20.89, 20.03, 18.97)]
	[InlineData(30.0, 16f, 7.33, 39.23, 31.20, 30.36, 30.04)]
	[InlineData(45.0, 16f, 12.70, 54.74, 46.37, 45.42, 45.04)]
	[InlineData(60.0, 8f, 8.66, 67.79, 61.17, 60.04, 58.57)]
	public void AGradeLockedBrushHeldStill_StopsWhenItsFootReachesTheRim_AndItsFacesAreSection10s(
		double degrees, float radius, double height, double sharp, double overOne, double overTwo, double overThree)
	{
		float grade = (float)LevelGround.Grade(degrees);
		double[] expected = { sharp, overOne, overTwo, overThree };

		for (int tip = 0; tip <= 3; tip++)
		{
			HeightField field = LevelGround.Flat(129);
			int dabs = 0;
			while (!TerrainBrush.GradeLockedDab(field, 0f, 0f, radius, grade, 0.25f, lower: false, tipRadius: tip).IsEmpty)
			{
				Assert.True(++dabs < 6000, "the cone never stopped growing");
			}

			LevelGround.Near(expected[tip], LevelGround.Degrees(LevelGround.SteepestFace(field)), 0.006);
			if (tip != (int)TerrainBrush.TipRadiusMeters)
			{
				continue;
			}

			// k · φ(R − 1.5) high, or as much higher as the first sample past R − 1.5 is further out.
			LevelGround.Near(height, field.Sample(0f, 0f), 0.006);
			double foot = radius - TerrainBrush.RimMeters;
			Assert.InRange(field.Sample(0f, 0f), (grade * TerrainBrush.Cone((float)foot, tip)) - 1e-4,
				(grade * TerrainBrush.Cone((float)(foot + 1d), tip)) + 1e-4);

			// And it is the default tip that a dab without one is rounded over.
			HeightField byDefault = LevelGround.Flat(129);
			while (!TerrainBrush.GradeLockedDab(byDefault, 0f, 0f, radius, grade, 0.25f, lower: false).IsEmpty)
			{
			}

			Assert.Equal(field.Heights, byDefault.Heights);
		}
	}

	[Theory]
	[InlineData(0.5f, 0.5f)]
	[InlineData(0.3f, 0.8f)]
	[InlineData(-0.45f, 0.1f)]
	[InlineData(0f, 0f)]
	public void AGradeLockedBrush_RisesAtItsRate_WhereverBetweenSamplesItIsHeld(float x, float z)
	{
		// The ground under a cursor between samples is under the cone's tip, by more than a dab's
		// rise: a brush that rose from the height under the cursor would stand still there.
		const float radius = 8f;
		float grade = (float)LevelGround.Grade(30d);
		BrushSettings brush = new() { Kind = BrushKind.GradeLocked, RadiusMeters = radius, GradeDegrees = 30f, RateMetersPerSecond = 0.25f };
		HeightField field = LevelGround.Flat(65);
		BrushStroke stroke = TerrainBrush.Begin(field, brush, x, z, lower: false);

		int nearX = (int)MathF.Round(x) + 32;
		int nearZ = (int)MathF.Round(z) + 32;
		for (int dab = 0; dab < 120; dab++)
		{
			TerrainBrush.Dab(field, stroke, x, z, Sixtieth);
		}

		// Two seconds at 0.25 m/s: half a metre, at the sample nearest the cursor.
		LevelGround.Near(0.5, field[nearX, nearZ], 2e-3);

		int dabs = 120;
		while (!TerrainBrush.Dab(field, stroke, x, z, Sixtieth).IsEmpty)
		{
			Assert.True(++dabs < 20_000, "the cone never stopped growing");
		}

		float offset = MathF.Sqrt(((x - MathF.Round(x)) * (x - MathF.Round(x))) + ((z - MathF.Round(z)) * (z - MathF.Round(z))));
		double least = grade * (TerrainBrush.Cone(radius - TerrainBrush.RimMeters, 2f) - TerrainBrush.Cone(offset, 2f));
		LevelGround.True(field[nearX, nearZ] >= least - 1e-3, $"stopped at {field[nearX, nearZ]} m, short of {least} m");
		LevelGround.Near(30d, LevelGround.Degrees(LevelGround.SteepestFace(field)), 0.6);
	}

	[Fact]
	public void TheSmallestGradeLockedBrush_MovesOnlyASampleItIsWithinHalfAMetreOf()
	{
		// 2 m, less the rim's 1.5 m: what is left to move is a disc of 0.5 m, and the middle of a
		// cell is 0.71 m from every sample. From 2.21 m up there is always a sample inside.
		float grade = (float)LevelGround.Grade(30d);

		HeightField field = LevelGround.Flat(33);
		Assert.True(TerrainBrush.GradeLockedDab(field, 0.5f, 0.5f, 2f, grade, 0.25f, lower: false).IsEmpty);
		Assert.Equal(new SampleRect(16, 16, 16, 16), TerrainBrush.GradeLockedDab(field, 0.3f, 0.3f, 2f, grade, 0.25f, lower: false));
		Assert.False(TerrainBrush.GradeLockedDab(LevelGround.Flat(33), 0.5f, 0.5f, 2.21f, grade, 0.25f, lower: false).IsEmpty);
	}

	[Fact]
	public void LoweringIsRaising_TurnedOver()
	{
		HeightField up = LevelGround.Noise(65, 2f, seed: 71);
		HeightField down = up.Clone();
		for (int i = 0; i < down.Heights.Length; i++)
		{
			down.Heights[i] = -down.Heights[i];
		}

		var random = new Random(73);
		float grade = (float)LevelGround.Grade(20d);
		for (int dab = 0; dab < 60; dab++)
		{
			float x = LevelGround.Uniform(random, -10f, 10f);
			float z = LevelGround.Uniform(random, -10f, 10f);
			SampleRect raised = TerrainBrush.GradeLockedDab(up, x, z, 12f, grade, 0.3f, lower: false);
			SampleRect lowered = TerrainBrush.GradeLockedDab(down, x, z, 12f, grade, 0.3f, lower: true);
			Assert.Equal(raised, lowered);
		}

		for (int i = 0; i < up.Heights.Length; i++)
		{
			Assert.Equal(up.Heights[i], -down.Heights[i]);
		}
	}

	[Theory]
	[InlineData(10f)]
	[InlineData(20f)]
	[InlineData(30f)]
	public void AMound_OnGroundNoSteeperThanItsGrade_LeavesEveryFaceWalkable(float degrees)
	{
		// By the √2: atan(√2 · tan 30°) = 39.2°, under the 40° that is walkable.
		BrushSettings brush = Array.Find(TerrainBrush.Presets, preset => preset.Name == $"Mound {degrees:0}°").Settings;
		double grade = LevelGround.Grade(degrees);
		var random = new Random(79 + (int)degrees);

		for (int trial = 0; trial < 12; trial++)
		{
			HeightField field = LevelGround.Noise(129, 0.6f, seed: 300 + trial, new[] { 2, 4, 8 });
			Assert.True(LevelGround.SteepestEdge(field) <= grade, "the ground this is drawn over is to be no steeper than the brush");

			float x = LevelGround.Uniform(random, -20f, 20f);
			float z = LevelGround.Uniform(random, -20f, 20f);
			double heading = random.NextDouble() * Math.PI * 2d;
			BrushStroke stroke = TerrainBrush.Begin(field, brush, x, z, lower: trial % 2 == 1);
			for (int dab = 0; dab < 600; dab++)
			{
				TerrainBrush.Dab(field, stroke, x, z, Sixtieth);
				heading += (random.NextDouble() - 0.5) * 0.4;
				x = Math.Clamp(x + (0.1f * (float)Math.Cos(heading)), -40f, 40f);
				z = Math.Clamp(z + (0.1f * (float)Math.Sin(heading)), -40f, 40f);
			}

			double face = LevelGround.Degrees(LevelGround.SteepestFace(field));
			LevelGround.True(face <= LevelGround.Degrees(Math.Sqrt(2d) * grade) + 1e-3, $"a face of {face}°");
			LevelGround.True(face < MapLimits.WalkableSlopeDegrees, $"a {degrees}° mound left a face of {face}°, which is not walked");
		}
	}

	[Fact]
	public void UpToThirtyDegrees_TheRootTwoStaysUnderWalkable_AndAtThirtyFiveItDoesNot()
	{
		static double Bound(double degrees) => LevelGround.Degrees(Math.Sqrt(2d) * LevelGround.Grade(degrees));

		LevelGround.Near(39.23, Bound(30d), 0.006);
		LevelGround.Near(44.72, Bound(35d), 0.006);
		Assert.True(Bound(30d) < MapLimits.WalkableSlopeDegrees);
		Assert.True(Bound(35d) > MapLimits.WalkableSlopeDegrees);
	}

	// ---- smooth, level ------------------------------------------------------------------------

	[Fact]
	public void ASmoothDab_MovesEachSampleTowardsTheMeanOfItsFourNeighbours_ByItsWeightTimesTheStrength()
	{
		const float radius = 9f;
		const float core = 0.3f;
		const float strength = 0.6f;
		HeightField before = LevelGround.Noise(65, 5f, seed: 83, persistence: 0.7f);
		HeightField field = before.Clone();

		SampleRect touched = TerrainBrush.SmoothDab(field, 1.4f, -2.6f, radius, core, strength);

		for (int z = 1; z < 64; z++)
		{
			for (int x = 1; x < 64; x++)
			{
				double dx = field.WorldX(x) - 1.4;
				double dz = field.WorldZ(z) + 2.6;
				double weight = TerrainBrush.Weight((float)Math.Sqrt((dx * dx) + (dz * dz)), radius, core) * strength;
				double mean = ((double)before[x - 1, z] + before[x + 1, z] + before[x, z - 1] + before[x, z + 1]) / 4d;
				LevelGround.Near(before[x, z] + ((mean - before[x, z]) * weight), field[x, z], 1e-5);
			}
		}

		Assert.Equal(LevelGround.Changed(before, field), touched);
		Assert.False(touched.IsEmpty);
	}

	[Fact]
	public void Smoothing_NeverRaisesTheHighestOrLowersTheLowestSampleItTouches()
	{
		var random = new Random(89);
		HeightField field = LevelGround.Noise(65, 6f, seed: 97, persistence: 0.8f);
		float highest = float.MinValue;
		float lowest = float.MaxValue;
		foreach (float height in field.Heights)
		{
			highest = MathF.Max(highest, height);
			lowest = MathF.Min(lowest, height);
		}

		double roughBefore = LevelGround.SteepestEdge(field);
		for (int dab = 0; dab < 200; dab++)
		{
			HeightField was = field.Clone();
			TerrainBrush.SmoothDab(field, LevelGround.Uniform(random, -36f, 36f), LevelGround.Uniform(random, -36f, 36f),
				LevelGround.Uniform(random, 2f, 20f), LevelGround.Uniform(random, 0f, 0.9f), LevelGround.Uniform(random, 0f, 1f));

			for (int z = 0; z < 65; z++)
			{
				for (int x = 0; x < 65; x++)
				{
					if (field[x, z] == was[x, z])
					{
						continue;
					}

					// Between itself and its neighbours as they were: a convex combination of them.
					float low = was[x, z];
					float high = was[x, z];
					foreach ((int nx, int nz) in new[] { (x - 1, z), (x + 1, z), (x, z - 1), (x, z + 1) })
					{
						if (nx >= 0 && nx < 65 && nz >= 0 && nz < 65)
						{
							low = MathF.Min(low, was[nx, nz]);
							high = MathF.Max(high, was[nx, nz]);
						}
					}

					Assert.InRange(field[x, z], low, high);
					Assert.InRange(field[x, z], lowest, highest);
				}
			}
		}

		Assert.True(LevelGround.SteepestEdge(field) < roughBefore);
	}

	[Fact]
	public void Smoothing_LeavesAnEvenSlopeAlone_AtTheFieldsEdgeAsAnywhere()
	{
		HeightField plane = LevelGround.Plane(33, 3f, 0.4f, -0.25f);
		foreach ((float x, float z) in new[] { (0f, 0f), (-16f, 0f), (16f, 16f), (-16f, -16f), (3.3f, 16f) })
		{
			HeightField field = plane.Clone();
			TerrainBrush.SmoothDab(field, x, z, 10f, 0.5f, 1f);
			LevelGround.True(LevelGround.Change(plane, field) < 1e-5, $"smoothing an even slope at ({x}, {z}) moved it {LevelGround.Change(plane, field)} m");
		}
	}

	[Fact]
	public void ASmoothDab_ReadsTheGroundAsItWasBeforeTheDab()
	{
		// Turned end for end, the same dab is the same ground turned end for end: it does not
		// matter which sample is written first.
		HeightField field = LevelGround.Noise(41, 5f, seed: 101);
		var mirror = new HeightField(41, 41);
		for (int z = 0; z < 41; z++)
		{
			for (int x = 0; x < 41; x++)
			{
				mirror[40 - x, 40 - z] = field[x, z];
			}
		}

		TerrainBrush.SmoothDab(field, 2f, -3f, 12f, 0.2f, 0.9f);
		TerrainBrush.SmoothDab(mirror, -2f, 3f, 12f, 0.2f, 0.9f);

		for (int z = 0; z < 41; z++)
		{
			for (int x = 0; x < 41; x++)
			{
				LevelGround.Near(field[x, z], mirror[40 - x, 40 - z], 1e-5);
			}
		}
	}

	[Fact]
	public void ALevelStroke_MovesTheGroundTowardsTheHeightUnderTheCursorWhenItBegan_AndNeverPastIt()
	{
		HeightField before = LevelGround.Noise(65, 5f, seed: 103);
		HeightField field = before.Clone();
		BrushSettings brush = Array.Find(TerrainBrush.Presets, preset => preset.Name == "Level").Settings;
		float target = before.Sample(4.5f, -3.25f);
		BrushStroke stroke = TerrainBrush.Begin(field, brush, 4.5f, -3.25f, lower: false);
		Assert.Equal(target, stroke.LevelMeters);

		// One dab: by the weight times the strength, towards the height it began at.
		SampleRect touched = TerrainBrush.Dab(field, stroke, 4.5f, -3.25f, Sixtieth);
		Assert.Equal(LevelGround.Changed(before, field), touched);
		for (int z = 0; z < 65; z++)
		{
			for (int x = 0; x < 65; x++)
			{
				double dx = field.WorldX(x) - 4.5;
				double dz = field.WorldZ(z) + 3.25;
				double weight = TerrainBrush.Weight((float)Math.Sqrt((dx * dx) + (dz * dz)), 8f, 0f) * 0.5;
				LevelGround.Near(before[x, z] + ((target - before[x, z]) * weight), field[x, z], 1e-5);
			}
		}

		// Dragged away, it still levels to where it began, and nothing crosses that height.
		for (int dab = 0; dab < 300; dab++)
		{
			HeightField was = field.Clone();
			float x = 4.5f + (dab * 0.02f);
			TerrainBrush.Dab(field, stroke, x, -3.25f, Sixtieth);
			for (int i = 0; i < field.Heights.Length; i++)
			{
				Assert.InRange(field.Heights[i], MathF.Min(was.Heights[i], target), MathF.Max(was.Heights[i], target));
			}
		}

		LevelGround.Near(target, field.Sample(7f, -3.25f), 1e-3);
		Assert.Equal(before.Sample(-20f, 20f), field.Sample(-20f, 20f));
	}

	// ---- ramp ---------------------------------------------------------------------------------

	[Fact]
	public void ARamp_IsTheStraightLineBetweenTheGroundAtItsTwoClicks_AndItsSidesAreSkirted()
	{
		var random = new Random(107);
		int made = 0;
		for (int trial = 0; trial < 40; trial++)
		{
			HeightField before = LevelGround.Noise(97, LevelGround.Uniform(random, 1f, 5f), seed: 500 + trial);
			HeightField field = before.Clone();
			float fromX = LevelGround.Uniform(random, -30f, 30f);
			float fromZ = LevelGround.Uniform(random, -30f, 30f);
			float toX = LevelGround.Uniform(random, -30f, 30f);
			float toZ = LevelGround.Uniform(random, -30f, 30f);
			float width = LevelGround.Uniform(random, 3f, 16f);
			double length = Math.Sqrt(((toX - fromX) * (toX - fromX)) + ((toZ - fromZ) * (toZ - fromZ)));
			double from = before.Sample(fromX, fromZ);
			double to = before.Sample(toX, toZ);

			RampResult result = TerrainBrush.Ramp(field, fromX, fromZ, toX, toZ, width, out SampleRect touched);
			if (length < 4d || Math.Abs(to - from) > TerrainConform.SkirtGrade * length)
			{
				continue;
			}

			Assert.Equal(RampResult.Ok, result);
			Assert.Equal(LevelGround.Changed(before, field), touched);
			made++;

			// Every sample of the strip is on the line, level across.
			for (int z = 0; z < 97; z++)
			{
				for (int x = 0; x < 97; x++)
				{
					double dx = field.WorldX(x) - fromX;
					double dz = field.WorldZ(z) - fromZ;
					double along = ((dx * (toX - fromX)) + (dz * (toZ - fromZ))) / length;
					double across = ((dx * (toZ - fromZ)) - (dz * (toX - fromX))) / length;
					if (along > 1e-3 && along < length - 1e-3 && Math.Abs(across) < (width / 2d) - 1e-3)
					{
						LevelGround.Near(from + ((to - from) * along / length), field[x, z], 1e-4);
					}
				}
			}

			// And nothing ends steeper than the skirt or what it was, between any two samples.
			double worst = LevelGround.WorstPair(before, field, TerrainConform.SkirtGrade);
			LevelGround.True(worst <= 1e-4, $"ramp {trial}: a pair of samples ended {worst} m over the skirt");
		}

		Assert.InRange(made, 20, 40);
	}

	[Fact]
	public void ARampsStrip_StopsAtItsClicks_AndItsEndsAreSkirtedAsItsSidesAre()
	{
		// Level ground, and a wall 5 m high whose foot is two metres past the second click.
		HeightField field = LevelGround.Flat(65);
		for (int z = 0; z < 65; z++)
		{
			for (int x = 44; x < 65; x++)
			{
				field[x, z] = 5f;
			}
		}

		Assert.Equal(RampResult.Ok, TerrainBrush.Ramp(field, -10f, 0f, 10f, 0f, 6f, out _));

		// The strip is level to the click. Past it the wall is cut to the skirt and no further:
		// tan 30° a metre from the strip's last sample, straight on and round its corner.
		double skirt = TerrainConform.SkirtGrade;
		LevelGround.Near(0d, field.Sample(10f, 0f), 1e-5);
		LevelGround.Near(0d, field.Sample(11f, 0f), 1e-5);
		LevelGround.Near(2d * skirt, field.Sample(12f, 0f), 1e-4);
		LevelGround.Near(3d * skirt, field.Sample(13f, 0f), 1e-4);
		LevelGround.Near(2d * skirt, field.Sample(12f, 3f), 1e-4);
		LevelGround.Near(Math.Sqrt(8d) * skirt, field.Sample(12f, 5f), 1e-4);
		LevelGround.Near(5d, field.Sample(12f, 20f), 1e-5);
		LevelGround.Near(5d, field.Sample(20f, 0f), 1e-5);
	}

	[Fact]
	public void ARampSteeperThanItsSkirt_OrBetweenNoSamples_IsRefused_AndChangesNothing()
	{
		// A cliff of 20 m: the line down it is steeper than 30° for any two clicks under 34.6 m apart.
		HeightField before = LevelGround.Flat(65);
		for (int z = 0; z < 65; z++)
		{
			for (int x = 33; x < 65; x++)
			{
				before[x, z] = 20f;
			}
		}

		HeightField field = before.Clone();
		Assert.Equal(RampResult.TooSteep, TerrainBrush.Ramp(field, -10f, 0f, 10f, 0f, 8f, out SampleRect touched));
		Assert.True(touched.IsEmpty);
		Assert.Equal(before.Heights, field.Heights);

		// 40 m apart it is 26.6°, and is made.
		Assert.Equal(RampResult.Ok, TerrainBrush.Ramp(field, -20f, 0f, 20f, 0f, 8f, out touched));
		Assert.False(touched.IsEmpty);
		LevelGround.Near(10d, field.Sample(0f, 0f), 1e-4);

		field = before.Clone();
		Assert.Equal(RampResult.TooSmall, TerrainBrush.Ramp(field, 3f, 3f, 3f, 3f, 8f, out _));
		Assert.Equal(RampResult.TooSmall, TerrainBrush.Ramp(field, -20.5f, 0.5f, -10.5f, 0.5f, 0.5f, out _));
		Assert.Equal(RampResult.TooSmall, TerrainBrush.Ramp(field, 500f, 500f, 520f, 500f, 8f, out _));
		Assert.Equal(RampResult.TooSmall, TerrainBrush.Ramp(field, 0f, 0f, 10f, 0f, float.NaN, out _));
		Assert.Equal(before.Heights, field.Heights);
	}

	// ---- every brush --------------------------------------------------------------------------

	[Fact]
	public void ADab_ReturnsTheRectangleOfTheSamplesItMoved_WhateverTheBrush()
	{
		var random = new Random(109);
		BrushKind[] kinds = { BrushKind.Sculpt, BrushKind.GradeLocked, BrushKind.Smooth, BrushKind.Level, BrushKind.Ramp };

		for (int trial = 0; trial < 400; trial++)
		{
			HeightField field = LevelGround.Noise(49, LevelGround.Uniform(random, 0f, 4f), seed: 700 + (trial % 7));
			var brush = new BrushSettings
			{
				Kind = kinds[trial % kinds.Length],
				RadiusMeters = LevelGround.Uniform(random, 2f, 30f),
				Core = LevelGround.Uniform(random, 0f, 0.9f),
				RateMetersPerSecond = LevelGround.Uniform(random, 0.25f, 8f),
				GradeDegrees = LevelGround.Uniform(random, 5f, 70f),
				Strength = LevelGround.Uniform(random, 0f, 1f),
			};

			// Off the field's edge as often as not: a dab there moves what there is of it.
			float x = LevelGround.Uniform(random, -40f, 40f);
			float z = LevelGround.Uniform(random, -40f, 40f);
			BrushStroke stroke = TerrainBrush.Begin(field, brush, x, z, lower: trial % 2 == 0);
			for (int dab = 0; dab < 5; dab++)
			{
				HeightField was = field.Clone();
				SampleRect touched = TerrainBrush.Dab(field, stroke, x + dab, z - (dab * 0.5f), Sixtieth);
				Assert.Equal(LevelGround.Changed(was, field), touched);
				if (brush.Kind == BrushKind.Ramp)
				{
					Assert.True(touched.IsEmpty);
				}
			}
		}
	}

	[Fact]
	public void ADab_OfNoTimeOrNoSize_OrNowhere_MovesNothing()
	{
		HeightField before = LevelGround.Noise(33, 3f, seed: 113);
		HeightField field = before.Clone();
		foreach (BrushPreset preset in TerrainBrush.Presets)
		{
			BrushStroke stroke = TerrainBrush.Begin(field, preset.Settings, 0f, 0f, lower: false);
			Assert.True(TerrainBrush.Dab(field, stroke, float.NaN, 0f, Sixtieth).IsEmpty);
			Assert.True(TerrainBrush.Dab(field, stroke, 900f, 900f, Sixtieth).IsEmpty);
			if (preset.Settings.Kind == BrushKind.Sculpt)
			{
				Assert.True(TerrainBrush.Dab(field, stroke, 0f, 0f, 0f).IsEmpty);
				Assert.True(TerrainBrush.Dab(field, stroke, 0f, 0f, -1f).IsEmpty);
				Assert.True(TerrainBrush.Dab(field, stroke, 0f, 0f, float.NaN).IsEmpty);
			}
		}

		Assert.True(TerrainBrush.SculptDab(field, 0f, 0f, 0f, 0f, 1f).IsEmpty);
		Assert.True(TerrainBrush.SculptDab(field, 0f, 0f, 8f, 0f, 0f).IsEmpty);
		Assert.True(TerrainBrush.SculptDab(field, 0f, 0f, 8f, 0f, float.NaN).IsEmpty);
		Assert.True(TerrainBrush.SmoothDab(field, 0f, 0f, 8f, 0f, 0f).IsEmpty);
		Assert.True(TerrainBrush.LevelDab(field, 0f, 0f, 8f, 0f, 0f, 5f).IsEmpty);
		Assert.True(TerrainBrush.LevelDab(field, 0f, 0f, 8f, 0f, 1f, float.NaN).IsEmpty);
		Assert.True(TerrainBrush.GradeLockedDab(field, 0f, 0f, 8f, float.NaN, 0.25f, lower: false).IsEmpty);
		Assert.Equal(before.Heights, field.Heights);

		// A grade-locked dab of no time raises nothing over level ground; over rough ground it
		// fills what lies under the cone the ground about its centre already holds.
		HeightField level = LevelGround.Flat(33, 2f);
		Assert.True(TerrainBrush.GradeLockedDab(level, 0.3f, 0.2f, 8f, 0.5f, 0f, lower: false).IsEmpty);
	}

	[Fact]
	public void EveryBrush_HoldsTheGroundToTheLimits()
	{
		// LE-D4: −16 m … +64 m.
		HeightField high = LevelGround.Flat(65, 63.5f);
		HeightField low = LevelGround.Flat(65, -15.5f);
		float cliff = (float)LevelGround.Grade(60d);

		for (int dab = 0; dab < 200; dab++)
		{
			TerrainBrush.SculptDab(high, -12f, -12f, 8f, 0.5f, 0.2f);
			TerrainBrush.SculptDab(low, -12f, -12f, 8f, 0.5f, -0.2f);
			TerrainBrush.GradeLockedDab(high, 12f, 12f, 8f, cliff, 0.2f, lower: false);
			TerrainBrush.GradeLockedDab(low, 12f, 12f, 8f, cliff, 0.2f, lower: true);
			TerrainBrush.LevelDab(high, 12f, -12f, 8f, 0f, 1f, 900f);
			TerrainBrush.LevelDab(low, 12f, -12f, 8f, 0f, 1f, -900f);
		}

		Assert.Equal(64f, high.Sample(-12f, -12f));
		Assert.Equal(64f, high.Sample(12f, 12f));
		Assert.Equal(64f, high.Sample(12f, -12f));
		Assert.Equal(-16f, low.Sample(-12f, -12f));
		Assert.Equal(-16f, low.Sample(12f, 12f));
		Assert.Equal(-16f, low.Sample(12f, -12f));
		foreach (float height in high.Heights)
		{
			Assert.InRange(height, 63.5f, MapLimits.MaxHeightMeters);
		}

		foreach (float height in low.Heights)
		{
			Assert.InRange(height, MapLimits.MinHeightMeters, -15.5f);
		}

		// A ramp's line is the ground's, which is inside them; a ramp to a height outside is §6.3's to clamp.
		Assert.Equal(RampResult.Ok, TerrainBrush.Ramp(high, -20f, 20f, 20f, 20f, 6f, out _));
		foreach (float height in high.Heights)
		{
			Assert.InRange(height, 63.5f, MapLimits.MaxHeightMeters);
		}
	}
}
