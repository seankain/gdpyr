using System;
using Gdpyr.Sim;
using Gdpyr.Sim.Level;
using Godot;
using Xunit;

namespace Gdpyr.Tests;

/// <summary>
/// What a map is in memory (docs/LEVEL_EDITOR.md §6.8): a new one, the numbers it is
/// held as — the file's, so that nothing is lost between them — and the one choice
/// LE1 left to LE2, a new map's water level.
/// </summary>
public class MapDocumentTests
{
	[Fact]
	public void ANewMap_IsLevelGroundAt0m_EmptyAndUnsaved_WithThisBuildsCatalogCount()
	{
		var id = Guid.NewGuid();
		MapDocument document = MapDocument.Create(id, "ridge", 1_700_000_123);
		Assert.Equal(id, document.Id);
		Assert.Equal("ridge", document.Name);
		Assert.Equal(0u, document.Revision);
		Assert.Equal(1_700_000_123, document.CreatedUnixSeconds);
		Assert.Equal(1_700_000_123, document.ModifiedUnixSeconds);
		Assert.Equal(Guid.Empty, document.ParentId);
		Assert.Equal(new byte[32], document.ParentHash);
		Assert.Equal(PrefabSpecs.Count, document.PrefabCatalogCount);
		Assert.Equal(MapLimits.TerrainSamples, document.Terrain.Width);
		Assert.Equal(MapLimits.TerrainSamples, document.Terrain.Depth);
		Assert.All(document.Terrain.Heights, h => Assert.Equal(0f, h));
		Assert.Empty(document.Objects);
		Assert.Empty(document.Roads);
	}

	[Fact]
	public void ANewMapsWater_Is5mUnderItsGround_SoTheGroundIsGrass_NotSand()
	{
		// LE1 left this to LE2: at 0 m, level ground at 0 m is sand (§6.4).
		MapDocument document = LevelMaps.Empty();
		BandSettings bands = document.Bands.Settings;
		Assert.Equal(-5f, bands.WaterLevelMeters);
		Assert.Equal(MapDocument.NewMapWaterLevelMeters, bands.WaterLevelMeters);
		Assert.True(bands.IsValid);

		BandSample flat = TerrainBands.Classify(bands, 0f, 0f);
		Assert.Equal(TerrainBand.Grass, flat.Band);
		Assert.Equal(1f, flat.WeightOf(TerrainBand.Grass));

		Assert.Equal(TerrainBand.Sand, TerrainBands.Classify(BandSettings.Default, 0f, 0f).Band);

		// Dug 5 m, it is water; 3.6 m, sand.
		Assert.Equal(TerrainBand.Water, TerrainBands.Classify(bands, -5.01f, 0f).Band);
		Assert.Equal(TerrainBand.Sand, TerrainBands.Classify(bands, -3.6f, 0f).Band);

		// Everything else is §6.4's.
		BandSettings plan = BandSettings.Default;
		Assert.Equal(plan.SlopeGainMetersPerDegree, bands.SlopeGainMetersPerDegree);
		Assert.Equal(plan.SlopeFreeDegrees, bands.SlopeFreeDegrees);
		Assert.Equal(plan.SandTopMeters, bands.SandTopMeters);
		Assert.Equal(plan.MudTopMeters, bands.MudTopMeters);
		Assert.Equal(plan.GrassTopMeters, bands.GrassTopMeters);
		Assert.Equal(plan.BlendMeters, bands.BlendMeters);
	}

	[Fact]
	public void TheBandNumbers_AsTheFileHoldsThem_AreExactlyTheSettings()
	{
		// n / 1000 is the float nearest n thousandths, which is what a literal is.
		for (int n = 0; n <= ushort.MaxValue; n++)
		{
			float value = n / 1000f;
			var bands = new BandSettings { SlopeGainMetersPerDegree = value, BlendMeters = value, WaterLevelMeters = 0f };
			MapBands stored = MapBands.From(bands);
			Assert.Equal(n, stored.SlopeGain);
			Assert.Equal(value, stored.Settings.BlendMeters);
		}

		Assert.Equal(0.6f, MapBands.NewMap.Settings.SlopeGainMetersPerDegree);
		Assert.Equal(22f, MapBands.NewMap.Settings.GrassTopMeters);

		// Out of a field's range, a number is clamped to it.
		MapBands clamped = MapBands.From(new BandSettings { SandTopMeters = 70f, MudTopMeters = -1f, WaterLevelMeters = 99f });
		Assert.Equal(ushort.MaxValue, clamped.SandTop);
		Assert.Equal(0, clamped.MudTop);
		Assert.Equal(MapLimits.MaxHeightSample, clamped.WaterLevel);
	}

	[Fact]
	public void AnObject_IsStoredQuantizedAsTheWireIs_WithItsParametersSortedAndOnceEach()
	{
		var placed = MapObject.At(PrefabSpecs.ResourceNode, new Vector3(12.345f, -1.006f, -99.994f), 1f,
			new[] { new MapParameter(1, 900), new MapParameter(0, 1) });
		Assert.Equal(Quantize.MetersToI16(12.345f), placed.X);
		Assert.Equal(-101, placed.Y);
		Assert.Equal(-9999, placed.Z);
		Assert.Equal(Quantize.YawToU16(1f), placed.Yaw);
		Assert.Equal(new[] { new MapParameter(0, 1), new MapParameter(1, 900) }, placed.Parameters.ToArray());
		Assert.Equal(900, placed.Parameter(1, 5));
		Assert.Equal(5, placed.Parameter(2, 5));
		Assert.Equal(-1.01f, placed.Position.Y, 5);

		Assert.Throws<ArgumentException>(() => new MapObject(0, 0, 0, 0, 0, 0, new[] { new MapParameter(3, 1), new MapParameter(3, 2) }));
		Assert.Throws<ArgumentException>(() => new MapObject(0, 0, 0, 0, 0, 0, new MapParameter[9]));
		Assert.Equal(placed, placed.WithY(placed.Y));
		Assert.NotEqual(placed, placed.WithY(0));
	}

	[Fact]
	public void AClone_SharesNothingThatCanChange()
	{
		MapDocument original = LevelMaps.Generate(1);
		MapDocument copy = original.Clone();
		Assert.True(original.SameAs(copy, out string difference), difference);

		copy.Terrain[5, 5] += 1f;
		copy.Objects.RemoveAt(0);
		copy.Roads.Add(new MapRoad(0, 60, 100, new[] { new RoadPoint(0, 0), new RoadPoint(100, 0) }));
		copy.ParentHash[0] = 9;
		copy.Name = "other";

		MapDocument again = LevelMaps.Generate(1);
		Assert.True(original.SameAs(again, out difference), difference);
		Assert.False(original.SameAs(copy, out difference));
		Assert.Equal("the parent", difference);
	}

	[Fact]
	public void SameAs_NamesTheFirstDifference()
	{
		MapDocument a = LevelMaps.Empty();
		MapDocument b = a.Clone();
		Assert.True(a.SameAs(b, out _));

		b.Terrain[7, 9] = 1f;
		Assert.False(a.SameAs(b, out string difference));
		Assert.Equal("sample (7, 9)", difference);

		b = a.Clone();
		b.Objects.Add(new MapObject(8, 0, 0, 0, 0));
		Assert.False(a.SameAs(b, out difference));
		Assert.Equal("the object count", difference);

		b = a.Clone();
		b.Bands = new MapBands { WaterLevel = 3 };
		Assert.False(a.SameAs(b, out difference));
		Assert.Equal("the water or bands", difference);
		Assert.False(a.SameAs(null, out _));
	}

	[Fact]
	public void AParentHash_IsAHashOrNone()
	{
		MapDocument document = LevelMaps.Empty();
		Assert.Throws<ArgumentException>(() => document.ParentHash = new byte[31]);
		document.ParentHash = null;
		Assert.Equal(new byte[32], document.ParentHash);
	}

	[Fact]
	public void CountOf_CountsAGroupTogether()
	{
		MapDocument document = LevelMaps.Empty();
		document.Objects.Add(new MapObject(PrefabSpecs.HeavyGun, 0, 0, 0, 0));
		document.Objects.Add(new MapObject(PrefabSpecs.MortarLauncher, 0, 0, 0, 0));
		document.Objects.Add(new MapObject(PrefabSpecs.AmmoCan, 0, 0, 0, 0));
		document.Objects.Add(new MapObject(999, 0, 0, 0, 0));
		Assert.Equal(2, document.CountOf(PrefabGroup.Emplacement));
		Assert.Equal(1, document.CountOf(PrefabGroup.AmmoCan));
		Assert.Equal(0, document.CountOf(PrefabGroup.Barracks));
	}
}
