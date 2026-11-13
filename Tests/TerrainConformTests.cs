using System;
using System.Collections.Generic;
using Gdpyr.Sim;
using Gdpyr.Sim.Level;
using Godot;
using Xunit;

namespace Gdpyr.Tests;

/// <summary>
/// Levelling ground to a shape (docs/LEVEL_EDITOR.md §6.3): h' = clamp(h, L, U).
/// Its five properties are asserted as properties, over seeded terrains, against L
/// and U worked out here from their definition — every anchor asked for every
/// sample — and §10's "Levelling" is here as values.
/// </summary>
public class TerrainConformTests
{
	private static readonly double[] Skirts = { 20, 30, 35, 45 };

	// ---- the five properties, for anchors that are samples ------------------------------------

	[Fact]
	public void LevelledToAnchors_TheGroundIsClampedBetweenTheirCones_AndSection6Point3sFivePropertiesHold()
	{
		// §10: 40 random terrains, pads and roads in turn.
		const int n = 96;
		var random = new Random(11);

		for (int trial = 0; trial < 40; trial++)
		{
			HeightField before = LevelGround.Noise(n, LevelGround.Uniform(random, 2f, 8f), seed: 100 + trial, new[] { 3, 6, 12, 24 });
			double grade = LevelGround.Grade(Skirts[random.Next(Skirts.Length)]);
			List<ConformAnchor> anchors = trial % 2 == 0
				? PadCells(before, RandomFootprint(random, before, 18f, 1f, 15f), out _)
				: Ribbon(before);

			HeightField field = before.Clone();
			Assert.Equal(ConformResult.Ok, TerrainConform.Apply(field, anchors.ToArray(), (float)grade, out SampleRect touched));
			Assert.Equal(LevelGround.Changed(before, field), touched);
			Assert.False(touched.IsEmpty);

			Envelope(before, anchors, grade, out double[] lower, out double[] upper);

			// 1. On its target at every anchor.
			foreach (ConformAnchor anchor in anchors)
			{
				Assert.Equal(anchor.Target, field[anchor.X, anchor.Z]);
			}

			for (int i = 0; i < field.Heights.Length; i++)
			{
				// The operator itself.
				LevelGround.Near(Math.Clamp(before.Heights[i], lower[i], upper[i]), field.Heights[i], 1e-4);

				// 2. Ground already between L and U is not touched.
				if (before.Heights[i] >= lower[i] + 1e-4 && before.Heights[i] <= upper[i] - 1e-4)
				{
					LevelGround.Same(before.Heights[i], field.Heights[i]);
					continue;
				}

				// 5. A sample that moved is further from some target in height than k times its distance from it.
				if (before.Heights[i] != field.Heights[i])
				{
					LevelGround.True(Reached(before, anchors, grade, i % n, i / n), $"trial {trial}: sample ({i % n}, {i / n}) moved, and no anchor reaches it");
				}
			}

			// 3. No pair of samples ends steeper than the steeper of the skirt and what the pair was.
			double worst = LevelGround.WorstPair(before, field, grade);
			LevelGround.True(worst <= 1e-4, $"trial {trial}: a pair of samples ended {worst} m over the skirt");

			// 4. Twice changes nothing.
			HeightField once = field.Clone();
			Assert.Equal(ConformResult.Ok, TerrainConform.Apply(field, anchors.ToArray(), (float)grade, out SampleRect again));
			Assert.True(again.IsEmpty);
			Assert.Equal(once.Heights, field.Heights);
		}
	}

	[Fact]
	public void ItReachesNoFurtherThanTheMostTheGroundIsOffATarget_OverTheSkirt()
	{
		// One anchor at 0 m on ground at 5 m: a pit, out to 5 / k and no further.
		foreach (double degrees in Skirts)
		{
			float grade = (float)LevelGround.Grade(degrees);
			HeightField field = LevelGround.Flat(65, 5f);
			Assert.Equal(ConformResult.Ok, TerrainConform.Apply(field, new[] { new ConformAnchor(32, 32, 0f) }, grade, out SampleRect touched));

			int reach = (int)Math.Ceiling(5d / grade);
			Assert.True(new SampleRect(32 - reach, 32 - reach, 32 + reach, 32 + reach).Intersect(field.Bounds).Contains(touched.MinX, touched.MinZ));
			Assert.True(touched.MaxX <= 32 + reach && touched.MaxZ <= 32 + reach);
			for (int z = 0; z < 65; z++)
			{
				for (int x = 0; x < 65; x++)
				{
					double away = Math.Sqrt(((x - 32) * (x - 32)) + ((z - 32) * (z - 32)));
					LevelGround.Near(Math.Min(5d, grade * away), field[x, z], 1e-5);
				}
			}
		}
	}

	// ---- when it applies --------------------------------------------------------------------

	[Fact]
	public void TargetsSteeperThanTheSkirt_AreRefused_AndNothingIsChanged()
	{
		HeightField before = LevelGround.Noise(49, 3f, seed: 131);
		HeightField field = before.Clone();
		float grade = TerrainConform.SkirtGrade;

		// Two anchors a metre apart: 0.5 m between them is 26.6° and taken, 0.6 m is 31° and not.
		Assert.Equal(ConformResult.Ok, TerrainConform.Apply(field.Clone(),
			new[] { new ConformAnchor(20, 20, 1f), new ConformAnchor(21, 20, 1.5f) }, grade, out _));
		Assert.Equal(ConformResult.TooSteep, TerrainConform.Apply(field,
			new[] { new ConformAnchor(20, 20, 1f), new ConformAnchor(21, 20, 1.6f) }, grade, out SampleRect touched));
		Assert.True(touched.IsEmpty);
		Assert.Equal(before.Heights, field.Heights);

		// Far apart, the same: 30 m apart and 18 m between them.
		Assert.Equal(ConformResult.TooSteep, TerrainConform.Apply(field,
			new[] { new ConformAnchor(5, 24, -4f), new ConformAnchor(35, 24, 14f) }, grade, out _));
		Assert.Equal(ConformResult.Ok, TerrainConform.Apply(field.Clone(),
			new[] { new ConformAnchor(5, 24, -4f), new ConformAnchor(35, 24, 13f) }, grade, out _));

		// One sample asked to be at two heights.
		Assert.Equal(ConformResult.TooSteep, TerrainConform.Apply(field,
			new[] { new ConformAnchor(20, 20, 1f), new ConformAnchor(20, 20, 1.01f) }, grade, out _));
		Assert.Equal(ConformResult.Ok, TerrainConform.Apply(field.Clone(),
			new[] { new ConformAnchor(20, 20, 1f), new ConformAnchor(20, 20, 1f) }, grade, out _));
		Assert.Equal(before.Heights, field.Heights);

		// A steeper skirt takes what the 30° one refuses.
		Assert.Equal(ConformResult.Ok, TerrainConform.Apply(field,
			new[] { new ConformAnchor(20, 20, 1f), new ConformAnchor(21, 20, 1.6f) }, (float)LevelGround.Grade(45d), out _));
		Assert.Equal(1f, field[20, 20]);
		Assert.Equal(1.6f, field[21, 20]);
	}

	[Fact]
	public void NoAnchors_OrOnesOffTheField_LevelNothing_AndASkirtOfNoGradeIsNotOne()
	{
		HeightField before = LevelGround.Noise(33, 3f, seed: 137);
		HeightField field = before.Clone();

		Assert.Equal(ConformResult.Ok, TerrainConform.Apply(field, ReadOnlySpan<ConformAnchor>.Empty, out SampleRect touched));
		Assert.True(touched.IsEmpty);
		Assert.Equal(ConformResult.Ok, TerrainConform.Apply(field,
			new[] { new ConformAnchor(-3, 5, 9f), new ConformAnchor(5, 33, 9f), new ConformAnchor(400, 400, -9f) }, out touched));
		Assert.True(touched.IsEmpty);
		Assert.Equal(before.Heights, field.Heights);

		Assert.Throws<ArgumentOutOfRangeException>(() => TerrainConform.Apply(field, new[] { new ConformAnchor(5, 5, 1f) }, 0f, out _));
		Assert.Throws<ArgumentOutOfRangeException>(() => TerrainConform.Apply(field, new[] { new ConformAnchor(5, 5, 1f) }, float.NaN, out _));
		Assert.Throws<ArgumentOutOfRangeException>(() => TerrainConform.Pad(field, new Footprint(Vector3.Zero, 0f, 2f, 2f), 1f, 1.5f, -1f));
	}

	[Fact]
	public void ATargetOrALevelOutsideTheLimits_IsHeldToThem()
	{
		HeightField field = LevelGround.Flat(65, 60f);
		Assert.Equal(ConformResult.Ok, TerrainConform.Apply(field, new[] { new ConformAnchor(32, 32, 900f) }, out _));
		Assert.Equal(64f, field[32, 32]);

		TerrainConform.Pad(field, new Footprint(new Vector3(-10f, 0f, -10f), 0.4f, 3f, 2f), 900f);
		Assert.Equal(64f, field.Sample(-10f, -10f));
		TerrainConform.Disc(field, 15f, 15f, 4f, -900f);
		Assert.Equal(-16f, field.Sample(15f, 15f));

		foreach (float height in field.Heights)
		{
			Assert.InRange(height, MapLimits.MinHeightMeters, MapLimits.MaxHeightMeters);
		}
	}

	// ---- a pad ------------------------------------------------------------------------------

	[Theory]
	[InlineData(1f, 8f, "rough", 1.19)]
	[InlineData(0.2f, 1.5f, "gentle", 1.16)]
	public void APad_IsLevelToItsMargin_AndSkirtedFromItsRectangle(float least, float most, string ground, double bound)
	{
		// §10: 300 pads on rough ground and 300 on gentle, the skirt measured from the footprint.
		const int n = 129;
		var random = new Random(33);
		double worstFace = 0d;
		int over = 0;
		int changed = 0;

		for (int trial = 0; trial < 300; trial++)
		{
			HeightField before = LevelGround.Noise(n, LevelGround.Uniform(random, least, most), seed: 5000 + trial);
			double grade = LevelGround.Grade(Skirts[random.Next(Skirts.Length)]);
			Footprint footprint = RandomFootprint(random, before, 24f, 0.4f, 15f);
			PadCells(before, footprint, out float level);

			HeightField field = before.Clone();
			SampleRect touched = TerrainConform.Pad(field, footprint, level, TerrainConform.PadMarginMeters, (float)grade);
			Assert.Equal(LevelGround.Changed(before, field), touched);

			// Sample by sample on every third pad; the faces, below, on all of them.
			var rectangle = new Rectangle(footprint);
			for (int z = 0; z < n && trial % 3 == 0; z++)
			{
				for (int x = 0; x < n; x++)
				{
					// The rectangle is the footprint's own: the one placement is checked against (§6.5).
					double away = rectangle.DistanceTo(field.WorldX(x), field.WorldZ(z));
					LevelGround.Near(footprint.DistanceTo(new Vector3(field.WorldX(x), 0f, field.WorldZ(z))), away, 1e-4);
					double skirt = grade * Math.Max(away - 1.5, 0d);

					// Every sample within 1.5 m of the footprint is at the level, exactly.
					if (away <= 1.5 - 1e-4)
					{
						LevelGround.Same(level, field[x, z]);
						continue;
					}

					// L and U are level ∓ k · max(0, distance − 1.5 m).
					LevelGround.Near(Math.Clamp(before[x, z], level - skirt, level + skirt), field[x, z], 1e-4);
					if (Math.Abs(before[x, z] - level) <= skirt - 1e-4)
					{
						LevelGround.Same(before[x, z], field[x, z]);
					}
				}
			}

			if (trial % 9 == 0)
			{
				double pair = LevelGround.WorstPair(before, field, grade);
				LevelGround.True(pair <= 1e-4, $"pad {trial} on {ground} ground: a pair of samples ended {pair} m over the skirt");
			}

			HeightField once = field.Clone();
			Assert.True(TerrainConform.Pad(field, footprint, level, TerrainConform.PadMarginMeters, (float)grade).IsEmpty);
			Assert.Equal(once.Heights, field.Heights);

			double limit = Math.Max(grade, LevelGround.SteepestFace(before));
			worstFace = Math.Max(worstFace, LevelGround.SteepestFace(field) / limit);
			Count(before, field, limit, ref over, ref changed);
		}

		// §10: at worst 1.151 and 1.147 times max(k, before), on 0.44 % and 0.27 % of the cells changed,
		// on that script's terrains. On these, 1.181 and 1.154, on 0.48 % and 0.34 %. What is proven
		// is the √2; on level ground it is the corner's cone, 1.156 (the test after next).
		LevelGround.True(worstFace <= bound, $"{ground} ground: a face {worstFace} times the steeper of the skirt and the steepest before");
		LevelGround.True(worstFace > 1.05, $"{ground} ground: the worst face is {worstFace} times the limit; the corners' cones were expected to show");
		LevelGround.True(over < 0.01 * changed, $"{ground} ground: {over} of {changed} changed cells have a face over the limit");
	}

	[Fact]
	public void SkirtedFromItsLevelledSamplesInstead_APadsFacesAreTheWholeRootTwo()
	{
		// §10: "Measured from the nearest levelled sample instead, 58 of 120 pads left a face over
		// the limit, at worst 1.414 times it." The general operator with the pad's samples as its
		// anchors is that measure: every stair of a turned edge is a crease in its skirt.
		const int n = 129;
		var random = new Random(33);
		double worstFromSamples = 0d;
		double worstFromRectangle = 0d;
		int overFromSamples = 0;
		int overFromRectangle = 0;

		for (int trial = 0; trial < 120; trial++)
		{
			HeightField before = LevelGround.Noise(n, LevelGround.Uniform(random, 1f, 8f), seed: 5000 + trial);
			double grade = LevelGround.Grade(Skirts[random.Next(Skirts.Length)]);
			Footprint footprint = RandomFootprint(random, before, 24f, 0.4f, 15f);
			List<ConformAnchor> anchors = PadCells(before, footprint, out float level);
			double limit = Math.Max(grade, LevelGround.SteepestFace(before));

			HeightField fromSamples = before.Clone();
			Assert.Equal(ConformResult.Ok, TerrainConform.Apply(fromSamples, anchors.ToArray(), (float)grade, out _));
			double ratio = LevelGround.SteepestFace(fromSamples) / limit;
			worstFromSamples = Math.Max(worstFromSamples, ratio);
			overFromSamples += ratio > 1.16 ? 1 : 0;

			HeightField fromRectangle = before.Clone();
			TerrainConform.Pad(fromRectangle, footprint, level, TerrainConform.PadMarginMeters, (float)grade);
			ratio = LevelGround.SteepestFace(fromRectangle) / limit;
			worstFromRectangle = Math.Max(worstFromRectangle, ratio);
			overFromRectangle += ratio > 1.16 ? 1 : 0;

			// Both level the same samples to the same height: it is only the skirt that differs.
			foreach (ConformAnchor anchor in anchors)
			{
				Assert.Equal(level, fromSamples[anchor.X, anchor.Z]);
				Assert.Equal(level, fromRectangle[anchor.X, anchor.Z]);
			}
		}

		Assert.InRange(worstFromSamples, 1.35, Math.Sqrt(2d) + 1e-4);
		Assert.InRange(overFromSamples, 20, 120);
		LevelGround.True(worstFromRectangle <= 1.16, $"from the rectangle: {worstFromRectangle}");
		Assert.Equal(0, overFromRectangle);
	}

	[Fact]
	public void OnLevelGround_APadsSkirtIsAPlaneAlongASide_AndAConeRoundACorner_NoFaceOverOnePointOneSixTimesIt()
	{
		// The cone round a corner starts 1.5 m out. Its steepest face is the one with a corner on
		// that circle at 45°: √2 · (√(1.5² + 1.5·√2 + 1) − 1.5) = 1.1562 times the skirt.
		double bound = Math.Sqrt(2d) * (Math.Sqrt((1.5 * 1.5) + (1.5 * Math.Sqrt(2d)) + 1d) - 1.5);
		LevelGround.Near(1.1562, bound, 1e-4);

		var random = new Random(139);
		double worst = 0d;
		for (int trial = 0; trial < 200; trial++)
		{
			HeightField field = LevelGround.Flat(97);
			double grade = LevelGround.Grade(Skirts[random.Next(Skirts.Length)]);
			float level = trial % 2 == 0 ? 6f : -6f;
			if (trial % 4 < 2)
			{
				TerrainConform.Pad(field, RandomFootprint(random, field, 12f, 0.4f, 10f), level, TerrainConform.PadMarginMeters, (float)grade);
			}
			else
			{
				TerrainConform.Disc(field, LevelGround.Uniform(random, -12f, 12f), LevelGround.Uniform(random, -12f, 12f),
					LevelGround.Uniform(random, 0f, 10f), level, TerrainConform.PadMarginMeters, (float)grade);
			}

			worst = Math.Max(worst, LevelGround.SteepestFace(field) / grade);
			Assert.True(LevelGround.SteepestEdge(field) <= grade + 1e-5);
		}

		Assert.InRange(worst, 1.10, bound + 1e-4);
	}

	[Fact]
	public void APadUnderADisc_IsLevelToItsMarginPastTheRadius()
	{
		// A resource node's: its whole capture radius (§6.5).
		HeightField before = LevelGround.Noise(97, 4f, seed: 149);
		HeightField field = before.Clone();
		const float level = 1.25f;

		SampleRect touched = TerrainConform.Disc(field, 3.5f, -2.25f, 10f, level);

		Assert.Equal(LevelGround.Changed(before, field), touched);
		for (int z = 0; z < 97; z++)
		{
			for (int x = 0; x < 97; x++)
			{
				double dx = field.WorldX(x) - 3.5;
				double dz = field.WorldZ(z) + 2.25;
				double skirt = TerrainConform.SkirtGrade * Math.Max(Math.Sqrt((dx * dx) + (dz * dz)) - 11.5, 0d);
				LevelGround.Near(Math.Clamp(before[x, z], level - skirt, level + skirt), field[x, z], 1e-4);
				if (Math.Sqrt((dx * dx) + (dz * dz)) <= 11.5 - 1e-4)
				{
					LevelGround.Same(level, field[x, z]);
				}
			}
		}

		Assert.True(LevelGround.WorstPair(before, field, TerrainConform.SkirtGrade) <= 1e-4);
		Assert.True(TerrainConform.Disc(field, 3.5f, -2.25f, 10f, level).IsEmpty);
	}

	[Fact]
	public void TheSkirtAndTheMargin_ArePlansNumbers_AndThePadWithoutOneUsesThem()
	{
		Assert.Equal(30f, TerrainConform.SkirtDegrees);
		LevelGround.Near(Math.Tan(Math.PI / 6d), TerrainConform.SkirtGrade, 1e-6);
		Assert.Equal(1.5f, TerrainConform.PadMarginMeters);

		HeightField before = LevelGround.Noise(65, 4f, seed: 151);
		var footprint = new Footprint(new Vector3(2f, 0f, -3f), 0.6f, 4f, 2.5f);
		HeightField byDefault = before.Clone();
		HeightField spelled = before.Clone();
		TerrainConform.Pad(byDefault, footprint, 0.75f);
		TerrainConform.Pad(spelled, footprint, 0.75f, 1.5f, (float)Math.Tan(Math.PI / 6d));
		Assert.True(LevelGround.Change(spelled, byDefault) < 1e-5);
		Assert.True(LevelGround.Change(before, byDefault) > 0.5);

		var anchors = new[] { new ConformAnchor(30, 30, 2f), new ConformAnchor(34, 31, 3f) };
		byDefault = before.Clone();
		spelled = before.Clone();
		TerrainConform.Apply(byDefault, anchors, out _);
		TerrainConform.Apply(spelled, anchors, (float)Math.Tan(Math.PI / 6d), out _);
		Assert.True(LevelGround.Change(spelled, byDefault) < 1e-5);
		Assert.True(LevelGround.Change(before, byDefault) > 0.5);
	}

	// ---- the √2, and why the skirt is 30° ----------------------------------------------------

	/// <summary>§10: the steepest face a skirt can leave, atan(√2 · tan), and a shape that leaves it.</summary>
	[Theory]
	[InlineData(25.0, 33.40)]
	[InlineData(30.0, 39.23)]
	[InlineData(35.0, 44.72)]
	[InlineData(40.0, 49.88)]
	[InlineData(45.0, 54.74)]
	public void TheSteepestFaceASkirtCanLeave_IsRootTwoTimesIt_AndACornerOfAnchorsLeavesIt(double skirt, double face)
	{
		// Anchors along two sides of a square of high ground, level at 0: the ground between is
		// k times the distance to the nearer side, and creased where the two are as near.
		float grade = (float)LevelGround.Grade(skirt);
		HeightField field = LevelGround.Flat(17, 60f);
		var anchors = new List<ConformAnchor>();
		for (int i = 0; i < 17; i++)
		{
			anchors.Add(new ConformAnchor(i, 0, 0f));
			if (i > 0)
			{
				anchors.Add(new ConformAnchor(0, i, 0f));
			}
		}

		Assert.Equal(ConformResult.Ok, TerrainConform.Apply(field, anchors.ToArray(), grade, out _));

		LevelGround.Near(grade, LevelGround.SteepestEdge(field), 1e-5);
		LevelGround.Near(face, LevelGround.Degrees(LevelGround.SteepestFace(field)), 0.006);
		LevelGround.Near(face, LevelGround.Degrees(Math.Sqrt(2d) * LevelGround.Grade(skirt)), 0.006);
	}

	[Fact]
	public void AThirtyDegreeSkirt_LeavesNoFaceThatIsNotWalkable_AndAThirtyFiveDegreeOneCould()
	{
		static double Worst(double skirt) => LevelGround.Degrees(Math.Sqrt(2d) * LevelGround.Grade(skirt));

		Assert.True(Worst(TerrainConform.SkirtDegrees) < MapLimits.WalkableSlopeDegrees);
		Assert.True(Worst(35d) > MapLimits.WalkableSlopeDegrees);
		LevelGround.Near(39.2, Worst(TerrainConform.SkirtDegrees), 0.05);
	}

	// ---- a pad is kept --------------------------------------------------------------------------

	[Fact]
	public void RoundedLevelledAndRoundedAgain_TheGroundIsWhatItWasAfterTheFirstRounding()
	{
		// §6.5, "A pad is kept": levelling a pad again after an edit near it changes nothing
		// unless the edit moved it, in a document, whose heights are whole steps.
		const int n = 97;
		var random = new Random(157);
		for (int trial = 0; trial < 60; trial++)
		{
			HeightField field = LevelGround.Noise(n, LevelGround.Uniform(random, 1f, 8f), seed: 900 + trial);
			field.Round(field.Bounds);
			Footprint footprint = RandomFootprint(random, field, 16f, 0.4f, 12f);
			List<ConformAnchor> anchors = PadCells(field, footprint, out float level);
			bool pad = trial % 2 == 0;

			for (int pass = 0; pass < 2; pass++)
			{
				short[] was = field.ToSamples();
				SampleRect touched;
				if (pad)
				{
					touched = TerrainConform.Pad(field, footprint, level);
				}
				else
				{
					Assert.Equal(ConformResult.Ok, TerrainConform.Apply(field, anchors.ToArray(), out touched));
				}

				field.Round(touched);
				if (pass == 1)
				{
					Assert.Equal(was, field.ToSamples());
				}
			}

			// And it stands on its pad: level to the step.
			foreach (ConformAnchor anchor in anchors)
			{
				Assert.Equal(level, field[anchor.X, anchor.Z]);
			}
		}
	}

	// ---- shapes, and the definition written out again ------------------------------------------

	/// <summary>A rectangle somewhere in the middle of a field, at any turn.</summary>
	private static Footprint RandomFootprint(Random random, HeightField field, float spread, float least, float most) =>
		new(new Vector3(LevelGround.Uniform(random, -spread, spread), 0f, LevelGround.Uniform(random, -spread, spread)),
			LevelGround.Uniform(random, 0f, MathF.PI), LevelGround.Uniform(random, least, most), LevelGround.Uniform(random, least, most));

	/// <summary>
	/// A footprint's rectangle, its turn worked out once: how far a point is from it,
	/// flat, in doubles from its centre, turn and half sizes.
	/// </summary>
	private readonly struct Rectangle
	{
		private readonly double _x;
		private readonly double _z;
		private readonly double _cos;
		private readonly double _sin;
		private readonly double _halfWidth;
		private readonly double _halfDepth;

		public Rectangle(in Footprint footprint)
		{
			_x = footprint.Center.X;
			_z = footprint.Center.Z;
			_cos = Math.Cos(footprint.Yaw);
			_sin = Math.Sin(footprint.Yaw);
			_halfWidth = footprint.HalfWidth;
			_halfDepth = footprint.HalfDepth;
		}

		public double DistanceTo(double x, double z)
		{
			double dx = x - _x;
			double dz = z - _z;
			double outX = Math.Max(Math.Abs((dx * _cos) - (dz * _sin)) - _halfWidth, 0d);
			double outZ = Math.Max(Math.Abs((dx * _sin) + (dz * _cos)) - _halfDepth, 0d);
			return Math.Sqrt((outX * outX) + (outZ * outZ));
		}
	}

	/// <summary>
	/// A pad as its samples: every one within 1.5 m of the footprint, each anchored to
	/// the mean of the ground under the footprint, rounded to a step (§6.5).
	/// </summary>
	private static List<ConformAnchor> PadCells(HeightField field, in Footprint footprint, out float level)
	{
		double sum = 0d;
		int under = 0;
		var cells = new List<(int X, int Z)>();
		double nearest = double.MaxValue;
		double nearestHeight = 0d;
		var rectangle = new Rectangle(footprint);
		for (int z = 0; z < field.Depth; z++)
		{
			for (int x = 0; x < field.Width; x++)
			{
				double away = rectangle.DistanceTo(field.WorldX(x), field.WorldZ(z));
				if (away <= 1.5)
				{
					cells.Add((x, z));
				}

				if (away <= 0d)
				{
					sum += field[x, z];
					under++;
				}

				if (away < nearest)
				{
					nearest = away;
					nearestHeight = field[x, z];
				}
			}
		}

		level = HeightField.ToMeters(HeightField.ToSample((float)(under > 0 ? sum / under : nearestHeight)));
		var anchors = new List<ConformAnchor>(cells.Count);
		foreach ((int x, int z) in cells)
		{
			anchors.Add(new ConformAnchor(x, z, level));
		}

		return anchors;
	}

	/// <summary>A road's ribbon: the samples within 3.5 m of a sine across the field, each anchored to a profile climbing at 8 %.</summary>
	private static List<ConformAnchor> Ribbon(HeightField field)
	{
		int points = 76;
		var lineX = new double[points];
		var lineZ = new double[points];
		var profile = new double[points];
		for (int i = 0; i < points; i++)
		{
			lineX[i] = 10 + i;
			lineZ[i] = 48d + (14d * Math.Sin(lineX[i] / 12d));
			profile[i] = i == 0
				? field[10, (int)lineZ[0]]
				: profile[i - 1] + (0.08 * Math.Sqrt(1d + ((lineZ[i] - lineZ[i - 1]) * (lineZ[i] - lineZ[i - 1]))));
		}

		var anchors = new List<ConformAnchor>();
		for (int z = 0; z < field.Depth; z++)
		{
			for (int x = 0; x < field.Width; x++)
			{
				double nearest = double.MaxValue;
				int at = 0;
				for (int i = 0; i < points; i++)
				{
					double away = ((x - lineX[i]) * (x - lineX[i])) + ((z - lineZ[i]) * (z - lineZ[i]));
					if (away < nearest)
					{
						nearest = away;
						at = i;
					}
				}

				if (Math.Sqrt(nearest) <= 3.5)
				{
					anchors.Add(new ConformAnchor(x, z, (float)profile[at]));
				}
			}
		}

		return anchors;
	}

	/// <summary>§6.3's L and U at every sample, from every anchor.</summary>
	private static void Envelope(HeightField field, List<ConformAnchor> anchors, double grade, out double[] lower, out double[] upper)
	{
		lower = new double[field.Heights.Length];
		upper = new double[field.Heights.Length];
		ConformAnchor[] all = anchors.ToArray();
		for (int z = 0; z < field.Depth; z++)
		{
			for (int x = 0; x < field.Width; x++)
			{
				double low = double.MinValue;
				double high = double.MaxValue;
				for (int i = 0; i < all.Length; i++)
				{
					int dx = x - all[i].X;
					int dz = z - all[i].Z;
					double skirt = grade * Math.Sqrt((dx * dx) + (dz * dz));
					double target = all[i].Target;
					if (target - skirt > low)
					{
						low = target - skirt;
					}

					if (target + skirt < high)
					{
						high = target + skirt;
					}
				}

				lower[field.Index(x, z)] = low;
				upper[field.Index(x, z)] = high;
			}
		}
	}

	/// <summary>Whether some anchor's target is further from the ground at a sample, in height, than k times its distance.</summary>
	private static bool Reached(HeightField before, List<ConformAnchor> anchors, double grade, int x, int z)
	{
		foreach (ConformAnchor anchor in anchors)
		{
			double away = Math.Sqrt(((x - anchor.X) * (x - anchor.X)) + ((z - anchor.Z) * (z - anchor.Z)));
			if (Math.Abs(before[x, z] - anchor.Target) > (grade * away) - 1e-4)
			{
				return true;
			}
		}

		return false;
	}

	/// <summary>The cells an edit changed, and those of them with a face over a limit.</summary>
	private static void Count(HeightField before, HeightField after, double limit, ref int over, ref int changed)
	{
		for (int z = 0; z + 1 < before.Depth; z++)
		{
			for (int x = 0; x + 1 < before.Width; x++)
			{
				if (before[x, z] == after[x, z])
				{
					continue;
				}

				changed++;
				if (Math.Max(LevelGround.Face(after, x, z, false), LevelGround.Face(after, x, z, true)) > limit * 1.0001)
				{
					over++;
				}
			}
		}
	}
}
