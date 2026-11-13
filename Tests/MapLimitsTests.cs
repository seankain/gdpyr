using System;
using Gdpyr.Sim;
using Gdpyr.Sim.Level;
using Xunit;

namespace Gdpyr.Tests;

/// <summary>
/// The bounds a map made in the game is held to (docs/LEVEL_EDITOR.md LE-D3, LE-D4,
/// LE-D23), against the constants elsewhere in the game they were chosen to stay
/// inside: a change to either side that brings them together fails here.
/// </summary>
public class MapLimitsTests
{
	[Fact]
	public void TheGrid_Is241SamplesAMetreApart_OverTheFieldTheGameAssumes()
	{
		Assert.Equal(241, MapLimits.TerrainSamples);
		Assert.Equal(1000, MapLimits.TerrainSpacingMillimeters);
		Assert.Equal(120f, MapLimits.FieldHalfMeters);
		Assert.Equal(2f * MapLimits.FieldHalfMeters, (MapLimits.TerrainSamples - 1) * MapLimits.TerrainSpacingMillimeters / 1000f);

		// §10, "The grid".
		Assert.Equal(58_081, MapLimits.TerrainSamples * MapLimits.TerrainSamples);
		Assert.Equal(57_600, (MapLimits.TerrainSamples - 1) * (MapLimits.TerrainSamples - 1));
		Assert.Equal(116_162, MapLimits.TerrainSamples * MapLimits.TerrainSamples * sizeof(short));

		// Play is kept inside the walls, and the walls inside what a position on the wire can say.
		Assert.True(Cargo.FieldHalfExtentMeters < MapLimits.FieldHalfMeters);
		Assert.True(MapLimits.FieldHalfMeters < SimConfig.MaxPositionMeters);
	}

	[Fact]
	public void AHeight_IsAnInt16In256thsOfAMetre()
	{
		Assert.Equal(256, MapLimits.HeightStepsPerMeter);
		Assert.Equal(1f / 256f, MapLimits.HeightStepMeters);
		Assert.Equal(3.90625, MapLimits.HeightStepMeters * 1000d);

		Assert.Equal(-4096, MapLimits.MinHeightSample);
		Assert.Equal(16384, MapLimits.MaxHeightSample);
		Assert.Equal(MapLimits.MinHeightMeters, HeightField.ToMeters(MapLimits.MinHeightSample));
		Assert.Equal(MapLimits.MaxHeightMeters, HeightField.ToMeters(MapLimits.MaxHeightSample));
	}

	[Fact]
	public void TheGround_IsHeldInsideEveryAbsoluteHeightTheGameHas()
	{
		// LE-D4: −16 m … +64 m.
		Assert.Equal(-16f, MapLimits.MinHeightMeters);
		Assert.Equal(64f, MapLimits.MaxHeightMeters);

		// A projectile below the kill plane is dropped; a drone under its floor or over its ceiling is lost.
		Assert.True(MapLimits.MinHeightMeters > SimConfig.ProjectileKillPlaneY);
		Assert.True(MapLimits.MinHeightMeters > -DroneRules.FieldMarginMeters);
		Assert.True(MapLimits.MaxHeightMeters < DroneRules.CeilingMeters);

		// The agent's passability rays start at 80 m and reach 120 m down, to −40 m
		// (Scripts/Agent/AgentPlaneView.cs, which this project does not compile).
		Assert.True(MapLimits.MaxHeightMeters < 80f);
		Assert.True(MapLimits.MinHeightMeters > 80f - 120f);

		// An aircraft cruising 100 m over the highest ground is still a height the wire can say.
		Assert.True(MapLimits.MaxHeightMeters + 100f < SimConfig.MaxPositionMeters);
		Assert.True(MapLimits.MinHeightMeters > -SimConfig.MaxPositionMeters);
	}

	[Fact]
	public void Walkable_IsUnderWhatABodyWasMeasuredToWalk_AndOverWhatASkirtCanLeave()
	{
		// LE-D23: up a 42.1° face and not a 43.0° one; a 30° skirt can leave 39.2°.
		Assert.Equal(40f, MapLimits.WalkableSlopeDegrees);
		Assert.True(MapLimits.WalkableSlopeDegrees <= 42.1f - 2f);

		double skirt = Math.Atan(Math.Sqrt(2d) * Math.Tan(TerrainConform.SkirtDegrees * Math.PI / 180d)) * 180d / Math.PI;
		Assert.True(skirt < MapLimits.WalkableSlopeDegrees);
		Assert.Equal(39.23, skirt, 2);
	}
}
