using System;
using Gdpyr.Sim.Level;
using Xunit;

namespace Gdpyr.Tests;

/// <summary>
/// The terrain's texture bands (docs/LEVEL_EDITOR.md §6.4): water, sand, mud, grass
/// and stone from height above the water and slope, in that order and never out of
/// it. §10's table is here as values.
/// </summary>
public class TerrainBandsTests
{
	/// <summary>The order asked for (§1): the texture only moves forward through it as the ground gets higher or steeper.</summary>
	private static readonly TerrainBand[] Asked =
	{
		TerrainBand.Water, TerrainBand.Sand, TerrainBand.Mud, TerrainBand.Grass, TerrainBand.Stone,
	};

	private static int Rank(TerrainBand band) => Array.IndexOf(Asked, band);

	private static readonly int[] Slopes = { 0, 10, 15, 20, 25, 30, 35, 40, 45, 50, 60 };

	/// <summary>§10, "The bands", with the default numbers: a row a height above the water, a column a slope.</summary>
	[Theory]
	[InlineData(-2f, "water water water water water water water water water water water")]
	[InlineData(0.5f, "sand sand sand mud grass grass grass grass grass grass stone")]
	[InlineData(1f, "sand sand sand grass grass grass grass grass grass stone stone")]
	[InlineData(2f, "mud mud mud grass grass grass grass grass grass stone stone")]
	[InlineData(5f, "grass grass grass grass grass grass grass grass stone stone stone")]
	[InlineData(8f, "grass grass grass grass grass grass grass stone stone stone stone")]
	[InlineData(12f, "grass grass grass grass grass grass stone stone stone stone stone")]
	[InlineData(16f, "grass grass grass grass stone stone stone stone stone stone stone")]
	[InlineData(20f, "grass grass grass stone stone stone stone stone stone stone stone")]
	[InlineData(25f, "stone stone stone stone stone stone stone stone stone stone stone")]
	public void TheBands_AreSection10sTable(float aboveTheWater, string row)
	{
		BandSettings settings = BandSettings.Default;
		string[] expected = row.Split(' ');
		Assert.Equal(Slopes.Length, expected.Length);

		for (int i = 0; i < Slopes.Length; i++)
		{
			TerrainBand band = TerrainBands.BandOf(settings, aboveTheWater, Slopes[i]);
			Assert.True(expected[i] == band.ToString().ToLowerInvariant(),
				$"{aboveTheWater} m above the water at {Slopes[i]}°: {band}, and §10 has {expected[i]}");
			Assert.Equal(band, TerrainBands.Classify(settings, aboveTheWater, Slopes[i]).Band);
		}
	}

	[Fact]
	public void TheFormula_IsHeightAboveTheWater_AndSixTenthsOfAMetreForEachDegreePastFifteen()
	{
		BandSettings settings = BandSettings.Default;

		Assert.Equal(3f, TerrainBands.U(settings, 3f, 0f));
		Assert.Equal(3f, TerrainBands.U(settings, 3f, 15f));
		LevelGround.Near(3d + (0.6 * 10d), TerrainBands.U(settings, 3f, 25f), 1e-5);
		LevelGround.Near(0.6 * 75d, TerrainBands.U(settings, 0f, 90f), 1e-4);

		// sand below u = 1.5, mud below 4, grass below 22, stone above; under the water, water.
		Assert.Equal(TerrainBand.Water, TerrainBands.BandOf(settings, -0.01f, 80f));
		Assert.Equal(TerrainBand.Sand, TerrainBands.BandOf(settings, 0f, 0f));
		Assert.Equal(TerrainBand.Sand, TerrainBands.BandOf(settings, 1.49f, 0f));
		Assert.Equal(TerrainBand.Mud, TerrainBands.BandOf(settings, 1.5f, 0f));
		Assert.Equal(TerrainBand.Mud, TerrainBands.BandOf(settings, 3.99f, 0f));
		Assert.Equal(TerrainBand.Grass, TerrainBands.BandOf(settings, 4f, 0f));
		Assert.Equal(TerrainBand.Grass, TerrainBands.BandOf(settings, 21.99f, 0f));
		Assert.Equal(TerrainBand.Stone, TerrainBands.BandOf(settings, 22f, 0f));

		// §6.4: stone is anything high or steep — level ground at 22 m, ground 4 m up at 45°.
		Assert.Equal(TerrainBand.Stone, TerrainBands.BandOf(settings, 4f, 45f));
		Assert.Equal(TerrainBand.Grass, TerrainBands.BandOf(settings, 4f, 44f));
		Assert.Equal(TerrainBand.Grass, TerrainBands.BandOf(settings, 0f, 51f));
		Assert.Equal(TerrainBand.Stone, TerrainBands.BandOf(settings, 0f, 52f));
	}

	[Fact]
	public void HigherOrSteeper_NeverGivesAnEarlierBand()
	{
		// §10's sweep: 261 heights from 5 m under the water to 60 m over it, 179 slopes to 89°.
		AssertInOrder(BandSettings.Default);
	}

	[Fact]
	public void HigherOrSteeper_NeverGivesAnEarlierBand_WhateverAMapsNumbersAre()
	{
		var random = new Random(163);
		for (int map = 0; map < 40; map++)
		{
			float sand = LevelGround.Uniform(random, 0f, 10f);
			float mud = sand + LevelGround.Uniform(random, 0f, 10f);
			var settings = new BandSettings
			{
				WaterLevelMeters = LevelGround.Uniform(random, -16f, 30f),
				SlopeFreeDegrees = LevelGround.Uniform(random, 0f, 60f),
				SlopeGainMetersPerDegree = LevelGround.Uniform(random, 0f, 3f),
				SandTopMeters = sand,
				MudTopMeters = mud,
				GrassTopMeters = mud + LevelGround.Uniform(random, 0f, 40f),
				BlendMeters = LevelGround.Uniform(random, 0f, 4f),
			};
			Assert.True(settings.IsValid);
			AssertInOrder(settings);
		}
	}

	private static void AssertInOrder(in BandSettings settings)
	{
		const int heights = 261;
		const int slopes = 179;
		var ranks = new int[heights, slopes];
		for (int h = 0; h < heights; h++)
		{
			for (int s = 0; s < slopes; s++)
			{
				float height = settings.WaterLevelMeters - 5f + (65f * h / (heights - 1));
				ranks[h, s] = Rank(TerrainBands.BandOf(settings, height, 89f * s / (slopes - 1)));
				Assert.InRange(ranks[h, s], 0, Asked.Length - 1);
			}
		}

		for (int h = 0; h < heights; h++)
		{
			for (int s = 0; s < slopes; s++)
			{
				if (h > 0 && ranks[h, s] < ranks[h - 1, s])
				{
					Assert.Fail($"higher gave an earlier band: {Asked[ranks[h - 1, s]]} then {Asked[ranks[h, s]]}, row {h} at slope {s}");
				}

				if (s > 0 && ranks[h, s] < ranks[h, s - 1])
				{
					Assert.Fail($"steeper gave an earlier band: {Asked[ranks[h, s - 1]]} then {Asked[ranks[h, s]]}, row {h} at slope {s}");
				}
			}
		}

		// And every band is there to be had: the sweep is not all one of them.
		Assert.Equal(Rank(TerrainBand.Water), ranks[0, 0]);
	}

	[Fact]
	public void AllFiveBands_AreReached_InTheirOrder_GoingUpLevelGround()
	{
		BandSettings settings = BandSettings.Default;
		int reached = 0;
		for (float height = -1f; height < 30f; height += 0.05f)
		{
			int rank = Rank(TerrainBands.BandOf(settings, height, 0f));
			Assert.InRange(rank, reached, reached + 1);
			reached = rank;
		}

		Assert.Equal(Asked.Length - 1, reached);
		Assert.Equal(Asked.Length, TerrainBands.Count);
	}

	// ---- a map's own numbers --------------------------------------------------------------------

	[Fact]
	public void TheWaterLevelAndTheSixNumbers_AreAMapsOwn()
	{
		BandSettings settings = BandSettings.Default;
		Assert.Equal(0f, settings.WaterLevelMeters);
		Assert.Equal(15f, settings.SlopeFreeDegrees);
		Assert.Equal(0.6f, settings.SlopeGainMetersPerDegree);
		Assert.Equal(1.5f, settings.SandTopMeters);
		Assert.Equal(4f, settings.MudTopMeters);
		Assert.Equal(22f, settings.GrassTopMeters);
		Assert.Equal(1f, settings.BlendMeters);
		Assert.True(settings.IsValid);

		// The water 6 m up: what was grass at 5 m is under it, and the bands start from there.
		settings.WaterLevelMeters = 6f;
		Assert.Equal(TerrainBand.Water, TerrainBands.BandOf(settings, 5f, 0f));
		Assert.Equal(TerrainBand.Sand, TerrainBands.BandOf(settings, 6.5f, 0f));
		Assert.Equal(TerrainBand.Mud, TerrainBands.BandOf(settings, 8f, 0f));
		Assert.Equal(TerrainBand.Stone, TerrainBands.BandOf(settings, 28f, 0f));

		// A beach 3 m deep, and slope that counts from the first degree.
		settings = BandSettings.Default;
		settings.SandTopMeters = 3f;
		Assert.Equal(TerrainBand.Sand, TerrainBands.BandOf(settings, 2f, 0f));
		settings.SlopeFreeDegrees = 0f;
		settings.SlopeGainMetersPerDegree = 1f;
		Assert.Equal(TerrainBand.Grass, TerrainBands.BandOf(settings, 2f, 5f));
		Assert.Equal(TerrainBand.Stone, TerrainBands.BandOf(settings, 2f, 20f));
	}

	[Fact]
	public void NumbersAFileCannotHold_OrEdgesOutOfOrder_AreNotValid()
	{
		Assert.True(BandSettings.Default.IsValid);

		BandSettings settings = BandSettings.Default;
		settings.MudTopMeters = 1f;
		Assert.False(settings.IsValid);

		settings = BandSettings.Default;
		settings.GrassTopMeters = 3f;
		Assert.False(settings.IsValid);

		settings = BandSettings.Default;
		settings.SlopeGainMetersPerDegree = -0.1f;
		Assert.False(settings.IsValid);

		settings = BandSettings.Default;
		settings.GrassTopMeters = 65.6f;
		Assert.False(settings.IsValid);
		settings.GrassTopMeters = 65.535f;
		Assert.True(settings.IsValid);

		settings = BandSettings.Default;
		settings.BlendMeters = float.NaN;
		Assert.False(settings.IsValid);

		settings = BandSettings.Default;
		settings.WaterLevelMeters = MapLimits.MaxHeightMeters + 0.5f;
		Assert.False(settings.IsValid);
		settings.WaterLevelMeters = MapLimits.MinHeightMeters;
		Assert.True(settings.IsValid);

		// Not valid is still answered: a band, and never an index off the end.
		settings = new BandSettings { SandTopMeters = 9f, MudTopMeters = 2f, GrassTopMeters = 5f, BlendMeters = 50f };
		for (float height = -2f; height < 12f; height += 0.25f)
		{
			BandSample sample = TerrainBands.Classify(settings, height, 30f);
			Assert.InRange(Rank(sample.Band), 0, 4);
			Assert.InRange(sample.UpperWeight, 0f, 1f);
		}
	}

	// ---- for drawing ----------------------------------------------------------------------------

	[Fact]
	public void Classify_GivesTheTwoNeighbouringBands_AndAWeightForEach_AcrossAMetreOfU()
	{
		BandSettings settings = BandSettings.Default;

		// Well inside a band: all of it.
		BandSample grass = TerrainBands.Classify(settings, 10f, 0f);
		Assert.Equal(TerrainBand.Grass, grass.Band);
		Assert.Equal(TerrainBand.Grass, grass.Lower);
		Assert.Equal(TerrainBand.Grass, grass.Upper);
		Assert.Equal(1f, grass.WeightOf(TerrainBand.Grass));
		Assert.Equal(0f, grass.WeightOf(TerrainBand.Mud));
		Assert.Equal(0f, grass.WeightOf(TerrainBand.Stone));

		// On an edge: half of each.
		BandSample edge = TerrainBands.Classify(settings, 4f, 0f);
		Assert.Equal(TerrainBand.Grass, edge.Band);
		Assert.Equal(TerrainBand.Mud, edge.Lower);
		Assert.Equal(TerrainBand.Grass, edge.Upper);
		Assert.Equal(0.5f, edge.UpperWeight);
		Assert.Equal(0.5f, edge.LowerWeight);

		// A quarter of a metre short of it: three quarters mud. And the blend is a metre wide: 0.5 m either side.
		BandSample below = TerrainBands.Classify(settings, 3.75f, 0f);
		Assert.Equal(TerrainBand.Mud, below.Band);
		Assert.Equal(0.25f, below.WeightOf(TerrainBand.Grass));
		Assert.Equal(0.75f, below.WeightOf(TerrainBand.Mud));
		Assert.Equal(1f, TerrainBands.Classify(settings, 3.49f, 0f).WeightOf(TerrainBand.Mud));
		Assert.Equal(1f, TerrainBands.Classify(settings, 4.51f, 0f).WeightOf(TerrainBand.Grass));
		Assert.Equal(TerrainBand.Mud, TerrainBands.Classify(settings, 3.49f, 0f).Upper);
		Assert.Equal(TerrainBand.Grass, TerrainBands.Classify(settings, 4.51f, 0f).Lower);
		Assert.Equal(TerrainBand.Grass, TerrainBands.Classify(settings, 3.51f, 0f).Upper);
		Assert.Equal(TerrainBand.Mud, TerrainBands.Classify(settings, 4.49f, 0f).Lower);

		// It is u that is blended across, so slope moves the edge as height does.
		BandSample steep = TerrainBands.Classify(settings, 1f, 20f);
		Assert.Equal(TerrainBand.Mud, steep.Lower);
		Assert.Equal(TerrainBand.Grass, steep.Upper);
		LevelGround.Near(0.5, steep.UpperWeight, 1e-5);

		// Under the water there is nothing to blend, to its very edge.
		BandSample water = TerrainBands.Classify(settings, -0.001f, 0f);
		Assert.Equal(TerrainBand.Water, water.Band);
		Assert.Equal(1f, water.WeightOf(TerrainBand.Water));
		Assert.Equal(1f, TerrainBands.Classify(settings, 0f, 0f).WeightOf(TerrainBand.Sand));
	}

	[Fact]
	public void TheWeights_SumToOne_AreOfNeighbours_FavourTheBand_AndHaveNoStepInThem()
	{
		BandSettings settings = BandSettings.Default;
		var random = new Random(167);
		foreach (float slope in new[] { 0f, 12f, 20f, 33f, 47f })
		{
			BandSample last = default;
			const float step = 0.002f;
			for (float height = 0f; height < 30f; height += step)
			{
				BandSample sample = TerrainBands.Classify(settings, height, slope);
				Assert.Equal(TerrainBands.BandOf(settings, height, slope), sample.Band);
				Assert.InRange(sample.UpperWeight, 0f, 1f);
				LevelGround.Near(1d, sample.LowerWeight + sample.UpperWeight, 1e-6);
				Assert.InRange(Rank(sample.Upper) - Rank(sample.Lower), 0, 1);
				Assert.True(sample.Band == sample.Lower || sample.Band == sample.Upper);
				Assert.True(sample.WeightOf(sample.Band) >= 0.5f);

				// Each band's weight moves by no more than the step over the blend's width.
				if (height > 0f)
				{
					foreach (TerrainBand band in Asked)
					{
						LevelGround.True(MathF.Abs(sample.WeightOf(band) - last.WeightOf(band)) <= (step / settings.BlendMeters) + 1e-4f,
							$"{band}'s weight steps at {height} m, {slope}°");
					}
				}

				last = sample;
			}
		}

		// No blend at all is a hard edge, and still a band.
		BandSettings hard = BandSettings.Default;
		hard.BlendMeters = 0f;
		for (int i = 0; i < 200; i++)
		{
			BandSample sample = TerrainBands.Classify(hard, LevelGround.Uniform(random, -2f, 30f), LevelGround.Uniform(random, 0f, 80f));
			Assert.Equal(1f, sample.WeightOf(sample.Band));
		}
	}

	[Fact]
	public void ASamplesBand_IsFromItsHeight_AndItsSlopeByCentralDifferences()
	{
		BandSettings settings = BandSettings.Default;

		// An even slope of 30° climbing east from 0 m: steeper than 15°, so u runs ahead of the height.
		float grade = (float)LevelGround.Grade(30d);
		HeightField field = LevelGround.Plane(41, 0f, grade, 0f);
		for (int x = 0; x < 41; x++)
		{
			BandSample sample = TerrainBands.Classify(settings, field, x, 20);
			LevelGround.Near(30d, field.SlopeDegrees(x, 20), 1e-3);
			Assert.Equal(TerrainBands.Classify(settings, field[x, 20], field.SlopeDegrees(x, 20)).Band, sample.Band);
			Assert.Equal(TerrainBands.BandOf(settings, x * grade, 30f), sample.Band);
		}

		// u = h + 9 on it: grass from the first sample, stone from 13 m up.
		Assert.Equal(TerrainBand.Grass, TerrainBands.Classify(settings, field, 0, 20).Band);
		Assert.Equal(TerrainBand.Grass, TerrainBands.Classify(settings, field, 22, 20).Band);
		Assert.Equal(TerrainBand.Stone, TerrainBands.Classify(settings, field, 23, 20).Band);

		// The same heights on level ground are sand, mud and grass.
		HeightField level = LevelGround.Flat(9, 1f);
		Assert.Equal(TerrainBand.Sand, TerrainBands.Classify(settings, level, 4, 4).Band);
		level = LevelGround.Flat(9, 12.7f);
		Assert.Equal(TerrainBand.Grass, TerrainBands.Classify(settings, level, 4, 4).Band);
	}
}
