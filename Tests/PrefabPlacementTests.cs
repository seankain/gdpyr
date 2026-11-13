using System;
using System.Collections.Generic;
using System.Linq;
using Gdpyr.Sim;
using Gdpyr.Sim.Level;
using Godot;
using Xunit;

namespace Gdpyr.Tests;

/// <summary>
/// May a prefab go here, and the pad it makes (docs/LEVEL_EDITOR.md §6.5): each result
/// from a placement built to produce it, in its order; the average grade as a least-
/// squares plane; the pad level at the mean, exactly, out to 1.5 m; and LE2's "done
/// when": placing, then undoing, restores every sample.
/// </summary>
public class PrefabPlacementTests
{
	private static MapDocument Flat(float at = 0f)
	{
		MapDocument document = LevelMaps.Empty();
		Array.Fill(document.Terrain.Heights, at);
		return document;
	}

	/// <summary>A plane rising <paramref name="degrees"/> along +x through 0 m at x = 0, rounded to steps.</summary>
	private static MapDocument Slope(float degrees, float alongZ = 0f)
	{
		MapDocument document = LevelMaps.Empty();
		HeightField field = document.Terrain;
		float grade = HeightField.Grade(degrees);
		for (int z = 0; z < field.Depth; z++)
		{
			for (int x = 0; x < field.Width; x++)
			{
				field[x, z] = HeightField.ToMeters(HeightField.ToSample((grade * field.WorldX(x) * 0.3f) + (alongZ * field.WorldZ(z) * 0.3f)));
			}
		}

		// Steeper near the middle only, where the tests place: 0.3 of the grade elsewhere keeps the ground inside LE-D4.
		for (int z = 60; z < 181; z++)
		{
			for (int x = 60; x < 181; x++)
			{
				field[x, z] = HeightField.ToMeters(HeightField.ToSample((grade * field.WorldX(x)) + (alongZ * field.WorldZ(z))));
			}
		}

		return document;
	}

	private static PlacementCheck Place(MapDocument document, ushort prefab, float x, float z, float yaw = 0f,
		float height = 0f, MapParameter[] parameters = null)
	{
		PlacementCheck check = PrefabPlacement.Check(document, new PlacementRequest(prefab, x, z, yaw, height, parameters));
		if (check.IsOk)
		{
			check.Edit.Apply(document);
		}

		return check;
	}

	// ---- each result -----------------------------------------------------------------------

	[Fact]
	public void UnknownPrefab_AndOk()
	{
		MapDocument document = Flat();
		PlacementCheck unknown = PrefabPlacement.Check(document, new PlacementRequest(PrefabSpecs.Count, 0f, 0f, 0f));
		Assert.Equal(PrefabPlacementResult.UnknownPrefab, unknown.Result);
		Assert.Null(unknown.Object);
		Assert.Null(unknown.Edit);

		PlacementCheck ok = Place(document, PrefabSpecs.CoverBlock, 10f, -20f, 0.5f);
		Assert.Equal(PrefabPlacementResult.Ok, ok.Result);
		Assert.Equal("ok", ok.ToString());
		Assert.Equal(ok.Object, document.Objects.Single());
		Assert.Equal(1000, ok.Object.X);
		Assert.Equal(-2000, ok.Object.Z);
		Assert.Equal(Quantize.YawToU16(0.5f), ok.Object.Yaw);
	}

	[Fact]
	public void LimitReached_WhenTheMapHoldsAsManyOfTheKindAsItMay_OrAsManyObjects()
	{
		MapDocument document = Flat();
		Assert.True(Place(document, PrefabSpecs.VehicleKiosk, 0f, 0f).IsOk);
		Assert.Equal(PrefabPlacementResult.LimitReached, Place(document, PrefabSpecs.VehicleKiosk, 50f, 50f).Result);

		for (int i = 0; i < 4; i++)
		{
			Assert.True(Place(document, PrefabSpecs.Barracks, -90f + (i * 30f), -90f).IsOk);
		}

		Assert.Equal(PrefabPlacementResult.LimitReached, Place(document, PrefabSpecs.Barracks, 60f, 60f).Result);

		for (int i = 0; i < 8; i++)
		{
			Assert.True(Place(document, i % 2 == 0 ? PrefabSpecs.HeavyGun : PrefabSpecs.MortarLauncher, -100f + (i * 5f), 100f).IsOk);
		}

		Assert.Equal(PrefabPlacementResult.LimitReached, Place(document, PrefabSpecs.MortarLauncher, 0f, 100f).Result);

		MapDocument full = Flat();
		for (int i = 0; i < MapLimits.MaxObjects; i++)
		{
			full.Objects.Add(new MapObject(PrefabSpecs.Bunker, 0, 0, 0, 0));
		}

		Assert.Equal(PrefabPlacementResult.LimitReached, Place(full, PrefabSpecs.Bunker, 50f, 50f).Result);
	}

	[Fact]
	public void OutOfBounds_ABoxPast120m_ARoundPiecePast115m_OrAHeightOutsideLeD4()
	{
		MapDocument document = Flat();
		Assert.Equal(PrefabPlacementResult.OutOfBounds, Place(document, PrefabSpecs.CoverBlock, 118.6f, 0f).Result);
		Assert.True(Place(document, PrefabSpecs.CoverBlock, 118.4f, 0f).IsOk);

		// A block turned 45° reaches further.
		Assert.Equal(PrefabPlacementResult.OutOfBounds, Place(document, PrefabSpecs.CoverBlock, 0f, -118.4f, MathF.PI / 4f).Result);

		// A round piece is held to ±115 m whatever its box.
		Assert.Equal(PrefabPlacementResult.OutOfBounds, Place(document, PrefabSpecs.AmmoCan, -115.1f, 0f).Result);
		Assert.True(Place(document, PrefabSpecs.AmmoCan, -114.9f, 0f).IsOk);
		Assert.Equal(PrefabPlacementResult.OutOfBounds, Place(document, PrefabSpecs.StrategistAnchor, 0f, 115.5f).Result);

		// A node's disc stays on the field.
		Assert.Equal(PrefabPlacementResult.OutOfBounds, Place(document, PrefabSpecs.ResourceNode, 0f, 110.1f).Result);
		Assert.True(Place(document, PrefabSpecs.ResourceNode, 0f, 109.9f).IsOk);

		// A bridge section's height is where it is put, inside LE-D4.
		Assert.Equal(PrefabPlacementResult.OutOfBounds, Place(document, PrefabSpecs.BridgeSpan, -50f, -50f, 0f, 64.5f).Result);
		Assert.True(Place(document, PrefabSpecs.BridgeSpan, -50f, -50f, 0f, 63.5f).IsOk);
	}

	[Fact]
	public void InTheWay_ABoxOverAnothersInPlanAndInHeight_OrAPier()
	{
		MapDocument document = Flat();
		Assert.True(Place(document, PrefabSpecs.CoverBlock, 0f, 0f).IsOk);
		PlacementCheck over = Place(document, PrefabSpecs.CoverBlock, 2.9f, 0f);
		Assert.Equal(PrefabPlacementResult.InTheWay, over.Result);
		Assert.Equal(0, over.Other);

		// Touching is not in the way, and on level ground the pads agree.
		Assert.True(Place(document, PrefabSpecs.CoverBlock, 3f, 0f).IsOk);

		// A deck may pass over a block; a pier may not stand in one.
		Assert.True(Place(document, PrefabSpecs.BridgeSpan, 1.5f, 0f, 0f, 4f).IsOk);
		var piers = new[] { new MapParameter(PrefabSpecs.SupportsKey, 1), new MapParameter(PrefabSpecs.PierBackKey, 400) };
		PlacementCheck pier = Place(document, PrefabSpecs.BridgeSpan, 30f, 5.4f, 0f, 4f, piers);
		Assert.True(pier.IsOk);
		PlacementCheck under = Place(document, PrefabSpecs.CoverBlock, 30f, 0f);
		Assert.Equal(PrefabPlacementResult.InTheWay, under.Result);
		Assert.Equal(document.Objects.Count - 1, under.Other);
	}

	[Fact]
	public void TooSteep_WhenTheAverageGradeUnderTheFootprintIsOverThePrefabs()
	{
		// A barracks takes 12°.
		PlacementCheck steep = Place(Slope(15f), PrefabSpecs.Barracks, 0f, 0f);
		Assert.Equal(PrefabPlacementResult.TooSteep, steep.Result);
		Assert.Equal(15f, steep.GradeDegrees, 1);
		Assert.True(Place(Slope(10f), PrefabSpecs.Barracks, 0f, 0f).IsOk);

		// The grade is a vector: 9° along x and 9° along z are 12.7° together.
		PlacementCheck both = Place(Slope(9f, HeightField.Grade(9f)), PrefabSpecs.Barracks, 0f, 0f);
		Assert.Equal(PrefabPlacementResult.TooSteep, both.Result);
		Assert.Equal(HeightField.Degrees(HeightField.Grade(9f) * MathF.Sqrt(2f)), both.GradeDegrees, 1);

		// Cover takes 25°; a bridge section has no limit.
		Assert.True(Place(Slope(20f), PrefabSpecs.CoverLowWall, 0f, 0f).IsOk);
		Assert.Equal(PrefabPlacementResult.TooSteep, Place(Slope(30f), PrefabSpecs.CoverLowWall, 0f, 0f).Result);
		Assert.True(Place(Slope(40f), PrefabSpecs.BridgeSpan, 0f, 0f, 0f, 12f).IsOk);
	}

	[Fact]
	public void InWater_APadsLevelOrARoundPieceUnderTheWater()
	{
		MapDocument document = Flat();
		document.Bands = new MapBands { WaterLevel = 256, SlopeGain = 600, SlopeFree = 15000, SandTop = 1500, MudTop = 4000, GrassTop = 22000, Blend = 1000 };
		Assert.Equal(PrefabPlacementResult.InWater, Place(document, PrefabSpecs.CoverBlock, 0f, 0f).Result);
		Assert.Equal(PrefabPlacementResult.InWater, Place(document, PrefabSpecs.AmmoCan, 0f, 0f).Result);
		Assert.Equal(PrefabPlacementResult.InWater, Place(document, PrefabSpecs.StrategistAnchor, 0f, 0f).Result);

		// A bridge is what goes over water.
		Assert.True(Place(document, PrefabSpecs.BridgeSpan, 0f, 0f, 0f, 2f).IsOk);

		// Level with the water is not under it.
		Assert.True(Place(Flat(-5f), PrefabSpecs.CoverBlock, 0f, 0f).IsOk);
		Assert.Equal(PrefabPlacementResult.InWater, Place(Flat(-5.01f), PrefabSpecs.CoverBlock, 0f, 0f).Result);
	}

	[Fact]
	public void DisturbsNeighbour_WhenLevellingWouldMoveASampleOfAnotherObjectsPad()
	{
		MapDocument document = Slope(6f);
		Assert.True(Place(document, PrefabSpecs.CoverBlock, 0f, 0f).IsOk);
		short[] before = document.Terrain.ToSamples();

		// A block beside it stands 0.4 m higher; its pad would cut into the first's.
		PlacementCheck beside = Place(document, PrefabSpecs.CoverBlock, 3.5f, 0f);
		Assert.Equal(PrefabPlacementResult.DisturbsNeighbour, beside.Result);
		Assert.Equal(0, beside.Other);
		Assert.Null(beside.Edit);
		Assert.Equal(before, document.Terrain.ToSamples());

		// Far enough off, it does not.
		Assert.True(Place(document, PrefabSpecs.CoverBlock, 12f, 0f).IsOk);

		// On level ground two pads side by side are at one level and disturb nothing.
		MapDocument flat = Flat(2f);
		Assert.True(Place(flat, PrefabSpecs.CoverBlock, 0f, 0f).IsOk);
		Assert.True(Place(flat, PrefabSpecs.CoverBlock, 3.2f, 0f).IsOk);
	}

	[Fact]
	public void BlocksDoor_AStructureWithin10mOfABarracksDoor_EitherWayRound()
	{
		// Yaw 0: the door is 6 m along −Z.
		MapDocument document = Flat();
		Assert.True(Place(document, PrefabSpecs.Barracks, 0f, 0f).IsOk);
		Assert.Equal(new Vector3(0f, 0f, -6f), PrefabSpecs.TryGetDoor(document.Objects[0], out Vector3 door) ? door : default);

		PlacementCheck blocked = Place(document, PrefabSpecs.StructurePillbox, 0f, -12f);
		Assert.Equal(PrefabPlacementResult.BlocksDoor, blocked.Result);
		Assert.Equal(0, blocked.Other);
		Assert.Equal(PrefabPlacementResult.BlocksDoor, Place(document, PrefabSpecs.StructureSandbagWall, 5f, -14f).Result);

		// 10.2 m from the door it stands; behind the barracks, nothing to block.
		Assert.True(Place(document, PrefabSpecs.StructurePillbox, 0f, -18f).IsOk);
		Assert.True(Place(document, PrefabSpecs.StructurePillbox, 0f, 10f).IsOk);

		// Cover is not a structure.
		Assert.True(Place(document, PrefabSpecs.CoverBlock, -6f, -12f).IsOk);

		// A barracks put down with its door at a structure.
		MapDocument other = Flat();
		Assert.True(Place(other, PrefabSpecs.StructureSniperTower, 50f, 38f).IsOk);
		PlacementCheck barracks = Place(other, PrefabSpecs.Barracks, 50f, 50f);
		Assert.Equal(PrefabPlacementResult.BlocksDoor, barracks.Result);
		Assert.Equal(0, barracks.Other);
		Assert.True(Place(other, PrefabSpecs.Barracks, 50f, 50f, MathF.PI).IsOk);
	}

	[Fact]
	public void TheChecks_AreInSection6Point5sOrder_TheFirstThatFailsIsTheAnswer()
	{
		// Limit before bounds.
		MapDocument document = Flat();
		Assert.True(Place(document, PrefabSpecs.StrategistAnchor, 0f, 0f).IsOk);
		Assert.Equal(PrefabPlacementResult.LimitReached, Place(document, PrefabSpecs.StrategistAnchor, 200f, 0f).Result);

		// Bounds before the way.
		Assert.True(Place(document, PrefabSpecs.CoverBlock, 117f, 0f).IsOk);
		Assert.Equal(PrefabPlacementResult.OutOfBounds, Place(document, PrefabSpecs.CoverBlock, 119f, 0f).Result);

		// The way before the grade, the grade before the water, the water before a neighbour.
		MapDocument steep = Slope(30f);
		Assert.True(Place(steep, PrefabSpecs.BridgeSpan, 0f, 0f, 0f, 1f).IsOk);
		Assert.Equal(PrefabPlacementResult.InTheWay, Place(steep, PrefabSpecs.CoverBlock, 0f, 0f).Result);
		steep.Bands = new MapBands { WaterLevel = MapLimits.MaxHeightSample, SlopeGain = 600, SlopeFree = 15000, SandTop = 1500, MudTop = 4000, GrassTop = 22000, Blend = 1000 };
		Assert.Equal(PrefabPlacementResult.TooSteep, Place(steep, PrefabSpecs.CoverBlock, 20f, 0f).Result);

		MapDocument wet = Slope(6f);
		Assert.True(Place(wet, PrefabSpecs.CoverBlock, 0f, 0f).IsOk);
		wet.Bands = new MapBands { WaterLevel = 512, SlopeGain = 600, SlopeFree = 15000, SandTop = 1500, MudTop = 4000, GrassTop = 22000, Blend = 1000 };
		Assert.Equal(PrefabPlacementResult.InWater, Place(wet, PrefabSpecs.CoverBlock, 3.5f, 0f).Result);

		// A neighbour before a door.
		// A pillbox 2.2 m off the barracks' front, 0.35 m higher: its pad would cut into the barracks', and it is at the door.
		MapDocument door = Slope(4f);
		Assert.True(Place(door, PrefabSpecs.Barracks, 0f, 0f).IsOk);
		Assert.Equal(PrefabPlacementResult.DisturbsNeighbour, Place(door, PrefabSpecs.StructurePillbox, 5f, -10.5f).Result);
		Assert.Equal(PrefabPlacementResult.BlocksDoor, Place(door, PrefabSpecs.StructurePillbox, 0f, -10.5f).Result);
	}

	// ---- the average grade and the pad -----------------------------------------------------

	[Fact]
	public void TheAverageGrade_OnAPlane_IsThePlanes_AndTheLevelIsTheMean()
	{
		var random = new Random(21);
		for (int trial = 0; trial < 30; trial++)
		{
			float b = (float)((random.NextDouble() * 0.4) - 0.2);
			float c = (float)((random.NextDouble() * 0.4) - 0.2);
			HeightField field = LevelGround.Plane(MapLimits.TerrainSamples, 7f, b, c);
			var area = PadArea.Of(new Footprint(new Vector3((float)(random.NextDouble() * 40d) - 20f, 0f, (float)(random.NextDouble() * 40d) - 20f),
				(float)(random.NextDouble() * 6d), 1f + (float)(random.NextDouble() * 8d), 1f + (float)(random.NextDouble() * 8d)));
			PrefabPlacement.Fit(field, area, out float gradeX, out float gradeZ, out float mean, out int samples);
			Assert.True(samples >= 3);
			Assert.Equal(b, gradeX, 4);
			Assert.Equal(c, gradeZ, 4);

			// The mean of the samples under it, written out.
			double sum = 0d;
			int count = 0;
			for (int z = 0; z < field.Depth; z++)
			{
				for (int x = 0; x < field.Width; x++)
				{
					if (area.Rect.Contains(new Vector3(field.WorldX(x), 0f, field.WorldZ(z))))
					{
						sum += field[x, z];
						count++;
					}
				}
			}

			Assert.Equal(count, samples);
			Assert.Equal(sum / count, mean, 1e-4 * Math.Max(1d, Math.Abs(mean)));
		}
	}

	[Fact]
	public void TheAverageGrade_OnRoughGround_IsTheLeastSquaresPlane()
	{
		// No plane through the samples' centroid fits them better than the one found.
		var random = new Random(8);
		for (int trial = 0; trial < 30; trial++)
		{
			HeightField field = LevelGround.Noise(MapLimits.TerrainSamples, 6f, 300 + trial);
			var area = PadArea.Of(new Footprint(new Vector3((float)(random.NextDouble() * 160d) - 80f, 0f, (float)(random.NextDouble() * 160d) - 80f),
				(float)(random.NextDouble() * 6d), 2f + (float)(random.NextDouble() * 10d), 2f + (float)(random.NextDouble() * 10d)));
			PrefabPlacement.Fit(field, area, out float gradeX, out float gradeZ, out float mean, out _);

			var points = new List<(double X, double Z, double H)>();
			for (int z = 0; z < field.Depth; z++)
			{
				for (int x = 0; x < field.Width; x++)
				{
					if (area.Rect.Contains(new Vector3(field.WorldX(x), 0f, field.WorldZ(z))))
					{
						points.Add((field.WorldX(x), field.WorldZ(z), field[x, z]));
					}
				}
			}

			double mx = points.Average(p => p.X);
			double mz = points.Average(p => p.Z);
			double Residual(double bx, double bz) =>
				points.Sum(p => Math.Pow(p.H - mean - (bx * (p.X - mx)) - (bz * (p.Z - mz)), 2d));

			double best = Residual(gradeX, gradeZ);
			foreach ((double dx, double dz) in new[] { (1e-3, 0d), (-1e-3, 0d), (0d, 1e-3), (0d, -1e-3), (1e-3, 1e-3), (-1e-3, 1e-3) })
			{
				Assert.True(Residual(gradeX + dx, gradeZ + dz) >= best - 1e-6, $"trial {trial}: a better plane at ({dx}, {dz})");
			}
		}
	}

	[Fact]
	public void AFootprintOverFewerThanThreeSamples_TakesTheTriangleUnderItsCentre()
	{
		// One sample raised 1 m: the first triangle of its cell is 45° along x, the second level.
		MapDocument document = Flat();
		document.Terrain[101, 100] = 1f;
		float x = document.Terrain.WorldX(100);
		float z = document.Terrain.WorldZ(100);

		PrecheckCan(document, x + 0.2f, z + 0.2f, 45f);
		PrecheckCan(document, x + 0.8f, z + 0.8f, 45f);
		PrecheckCan(document, x - 0.8f, z - 0.2f, 0f);

		static void PrecheckCan(MapDocument document, float x, float z, float degrees)
		{
			PlacementCheck check = PrefabPlacement.Check(document, new PlacementRequest(PrefabSpecs.AmmoCan, x, z, 0f));
			Assert.Equal(degrees, check.GradeDegrees, 3);
			Assert.Equal(degrees > 20f ? PrefabPlacementResult.TooSteep : PrefabPlacementResult.Ok, check.Result);

			// It stands on the ground under its origin, to the centimetre.
			Assert.Equal(Quantize.MetersToI16(document.Terrain.Sample(Quantize.I16ToMeters(Quantize.MetersToI16(x)),
				Quantize.I16ToMeters(Quantize.MetersToI16(z)))), check.Object.Y);
		}
	}

	[Fact]
	public void APadsLevel_IsTheNearestStepToTheObjectsHeight_AndAFixedPoint()
	{
		for (int y = -1700; y <= 6500; y++)
		{
			short level = PrefabPlacement.PadLevelSample((short)y);
			decimal exact = y * 256m / 100m;
			short nearest = (short)Math.Clamp(Math.Round(exact, MidpointRounding.AwayFromZero), MapLimits.MinHeightSample, MapLimits.MaxHeightSample);
			Assert.Equal(nearest, level);
			Assert.NotEqual(0.5m, Math.Abs(exact - Math.Truncate(exact)));

			// Stood at its own level to the centimetre, an object's pad is the same step again.
			short again = Quantize.MetersToI16(HeightField.ToMeters(level));
			Assert.Equal(level, PrefabPlacement.PadLevelSample(again));
		}
	}

	[Fact]
	public void APad_IsLevelAtTheMean_ExactlyOutTo1Point5m_AndItsSkirtIsNoSteeperThan30Degrees()
	{
		ushort[] padded = { 0, 1, 2, 3, 4, 9, 10, 11, 12, 13, 14 };
		var random = new Random(31);
		int placed = 0;
		for (int trial = 0; trial < 60; trial++)
		{
			MapDocument document = LevelMaps.Empty();
			HeightField ground = LevelGround.Noise(MapLimits.TerrainSamples, 3f, 500 + trial, new[] { 4, 8, 16 });
			ground.Round(ground.Bounds);
			Array.Copy(ground.Heights, document.Terrain.Heights, ground.Heights.Length);
			HeightField before = document.Terrain.Clone();

			ushort prefab = padded[random.Next(padded.Length)];
			var request = new PlacementRequest(prefab, (float)(random.NextDouble() * 160d) - 80f,
				(float)(random.NextDouble() * 160d) - 80f, (float)(random.NextDouble() * Math.PI * 2d));
			PlacementCheck check = PrefabPlacement.Check(document, request);
			if (!check.IsOk)
			{
				Assert.Equal(PrefabPlacementResult.TooSteep, check.Result);
				continue;
			}

			placed++;
			check.Edit.Apply(document);
			HeightField after = document.Terrain;
			Assert.True(PrefabSpecs.TryGetPadArea(check.Object, out PadArea area));
			PrefabPlacement.Fit(before, area, out _, out _, out float mean, out _);
			Assert.True(MathF.Abs(check.Object.Position.Y - mean) <= 0.005f);
			float level = PrefabPlacement.PadLevel(check.Object);
			Assert.Equal(check.Level, level);
			Assert.True(MathF.Abs(level - mean) <= 0.005f + (0.5f / 256f));

			for (int z = 0; z < after.Depth; z++)
			{
				for (int x = 0; x < after.Width; x++)
				{
					// Every height a whole step.
					Assert.Equal(MathF.Round(after[x, z] * 256f), after[x, z] * 256f);
					if (area.DistanceTo(after.WorldX(x), after.WorldZ(z)) <= TerrainConform.PadMarginMeters)
					{
						Assert.Equal(level, after[x, z]);
					}
				}
			}

			// §6.3's third property, to a step of rounding.
			double skirt = HeightField.Grade(TerrainConform.SkirtDegrees);
			Assert.True(LevelGround.SteepestEdge(after) <= Math.Max(LevelGround.SteepestEdge(before), skirt) + (2d / 256d));
		}

		Assert.True(placed > 35, $"{placed} of 60 placed");
	}

	// ---- placing, then undoing -------------------------------------------------------------

	[Theory]
	[InlineData(1)]
	[InlineData(2)]
	[InlineData(3)]
	[InlineData(4)]
	[InlineData(5)]
	[InlineData(6)]
	public void PlacingThenUndoing_RestoresEverySample_AndRedoingRestoresThePlacements(int seed)
	{
		var random = new Random(seed);
		MapDocument document = LevelMaps.Empty();
		HeightField ground = LevelGround.Hills(seed);
		ground.Round(ground.Bounds);
		Array.Copy(ground.Heights, document.Terrain.Heights, ground.Heights.Length);
		MapDocument start = document.Clone();

		var history = new EditHistory();
		int placed = 0;
		int changedSamples = 0;
		for (int step = 0; step < 60; step++)
		{
			if (step % 7 == 6 && document.Objects.Count > 0)
			{
				history.Do(document, PrefabPlacement.TakeUp(document, random.Next(document.Objects.Count)));
				continue;
			}

			ushort prefab = (ushort)random.Next(PrefabSpecs.Count);
			var request = new PlacementRequest(prefab, (float)(random.NextDouble() * 220d) - 110f,
				(float)(random.NextDouble() * 220d) - 110f, random.Next(24) * MathF.PI / 12f,
				(float)(random.NextDouble() * 20d));
			PlacementCheck check = PrefabPlacement.Check(document, request);
			if (check.IsOk)
			{
				changedSamples += check.Edit.Rect.Count;
				history.Do(document, check.Edit);
				placed++;
			}
		}

		Assert.True(placed >= 15, $"{placed} placed");
		Assert.True(changedSamples > 1000, $"{changedSamples} samples in the edits");
		Assert.False(start.SameAs(document, out _));
		MapDocument end = document.Clone();

		while (history.Undo(document))
		{
		}

		Assert.True(start.SameAs(document, out string difference), difference);

		while (history.Redo(document))
		{
		}

		Assert.True(end.SameAs(document, out difference), difference);
	}

	[Fact]
	public void APlacedObject_IsOneTheValidatorFindsNothingWrongWithWhereItStands()
	{
		var random = new Random(12);
		MapDocument document = LevelMaps.Empty();
		HeightField ground = LevelGround.Hills(12);
		ground.Round(ground.Bounds);
		Array.Copy(ground.Heights, document.Terrain.Heights, ground.Heights.Length);
		for (int step = 0; step < 150; step++)
		{
			var request = new PlacementRequest((ushort)random.Next(PrefabSpecs.Count), (float)(random.NextDouble() * 230d) - 115f,
				(float)(random.NextDouble() * 230d) - 115f, (float)(random.NextDouble() * Math.PI * 2d), (float)(random.NextDouble() * 30d));
			PlacementCheck check = PrefabPlacement.Check(document, request);
			if (check.IsOk)
			{
				check.Edit.Apply(document);
			}
		}

		Assert.True(document.Objects.Count >= 40, $"{document.Objects.Count} placed");
		MapReport report = MapValidator.Validate(document);
		MapIssueCode[] where = { MapIssueCode.OutOfBounds, MapIssueCode.InTheWay, MapIssueCode.OffItsPad, MapIssueCode.UnderWater,
			MapIssueCode.NumberOutOfRange, MapIssueCode.HeightOutOfRange, MapIssueCode.UnknownPrefab, MapIssueCode.TooMany };
		Assert.Empty(report.Issues.Where(issue => where.Contains(issue.Code)));
	}

	[Fact]
	public void TakingUp_RemovesTheObject_AndLeavesItsPad()
	{
		MapDocument document = Slope(6f);
		Assert.True(Place(document, PrefabSpecs.CoverBlock, 0f, 0f).IsOk);
		short[] levelled = document.Terrain.ToSamples();
		new EditHistory().Do(document, PrefabPlacement.TakeUp(document, 0));
		Assert.Empty(document.Objects);
		Assert.Equal(levelled, document.Terrain.ToSamples());
	}

	// ---- a pad is kept ---------------------------------------------------------------------

	[Fact]
	public void APad_IsKept_LevelledAgainWhenAnEditReachesIt_AndOtherwiseLeftAlone()
	{
		MapDocument document = LevelMaps.Generate(1);
		MapDocument untouched = document.Clone();

		// Nothing has moved: keeping every pad changes nothing.
		Assert.True(PrefabPlacement.KeepPads(document, document.Terrain.Bounds).IsEmpty);
		Assert.True(untouched.SameAs(document, out string difference), difference);

		// A sculpt over the middle node's pad.
		int node = document.Objects.FindIndex(o => o.PrefabId == PrefabSpecs.ResourceNode);
		Vector3 at = document.Objects[node].Position;
		SampleRect stroke = TerrainBrush.SculptDab(document.Terrain, at.X + 3f, at.Z, 8f, 0f, 0.6f);
		document.Terrain.Round(stroke);
		Assert.Contains(MapValidator.Validate(document).Issues, issue => issue.Code == MapIssueCode.OffItsPad && issue.Object == node);

		SampleRect kept = PrefabPlacement.KeepPads(document, stroke);
		Assert.False(kept.IsEmpty);
		MapReport report = MapValidator.Validate(document);
		Assert.True(report.IsPlayable, string.Join("; ", report.Issues));

		// Kept twice is kept once (§6.3's fourth property, to the step).
		short[] once = document.Terrain.ToSamples();
		Assert.True(PrefabPlacement.KeepPads(document, document.Terrain.Bounds).IsEmpty);
		Assert.Equal(once, document.Terrain.ToSamples());
	}
}
