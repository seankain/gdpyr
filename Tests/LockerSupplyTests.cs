using System;
using Gdpyr.Sim;
using Godot;
using Xunit;

namespace Gdpyr.Tests;

/// <summary>
/// Where a weapon locker reaches (docs/LOADOUT.md §4.4, L-D7, L-D8): a metre, flat,
/// from the cabinet's turned 1.2 × 0.5 m footprint.
/// </summary>
public class LockerSupplyTests
{
	/// <summary>The Test map's locker: at (8, 0.5, 11), turned 180° so its racks face −Z, towards the spawn.</summary>
	private static readonly Vector3 TestLocker = new(8f, 0.5f, 11f);

	private const float Precision = 1e-4f;

	[Fact]
	public void TheFootprintIsTheCabinetUnderIt()
	{
		Assert.Equal(1.2f, LockerSupply.CabinetWidthMeters);
		Assert.Equal(0.5f, LockerSupply.CabinetDepthMeters);
	}

	[Theory]
	[InlineData(0f)]
	[InlineData(MathF.PI)]
	public void InsideTheCabinetIsNoDistanceAtAll(float yaw)
	{
		Assert.Equal(0f, LockerSupply.FootprintDistance(TestLocker, TestLocker, yaw));
		Assert.Equal(0f, LockerSupply.FootprintDistance(TestLocker + new Vector3(0.59f, 0f, 0.24f), TestLocker, yaw));
		Assert.Equal(0f, LockerSupply.FootprintDistance(TestLocker + new Vector3(-0.6f, 0f, -0.25f), TestLocker, yaw),
			Precision);
	}

	[Theory]
	[InlineData(0f)]
	[InlineData(MathF.PI)]
	public void TheTestMapsLockerIsAMetreFromItsFaceAndFromItsEnds(float yaw)
	{
		// Turned 180° the rectangle is the same rectangle: both turns measure alike.
		Assert.Equal(1f, LockerSupply.FootprintDistance(new Vector3(8f, 1f, 9.75f), TestLocker, yaw), Precision);
		Assert.Equal(1f, LockerSupply.FootprintDistance(new Vector3(8f, 1f, 12.25f), TestLocker, yaw), Precision);
		Assert.Equal(1f, LockerSupply.FootprintDistance(new Vector3(6.4f, 1f, 11f), TestLocker, yaw), Precision);
		Assert.Equal(1f, LockerSupply.FootprintDistance(new Vector3(9.6f, 1f, 11f), TestLocker, yaw), Precision);
	}

	[Fact]
	public void PointNineNineIsInAndOnePointOhOneIsOut()
	{
		float yaw = MathF.PI;
		float justIn = LockerSupply.FootprintDistance(new Vector3(8f, 1f, 10.75f - 0.99f), TestLocker, yaw);
		float justOut = LockerSupply.FootprintDistance(new Vector3(8f, 1f, 10.75f - 1.01f), TestLocker, yaw);

		Assert.True(LockerSupply.InReach(justIn));
		Assert.False(LockerSupply.InReach(justOut));

		float endIn = LockerSupply.FootprintDistance(new Vector3(8.6f + 0.99f, 1f, 11f), TestLocker, yaw);
		float endOut = LockerSupply.FootprintDistance(new Vector3(8.6f + 1.01f, 1f, 11f), TestLocker, yaw);
		Assert.True(LockerSupply.InReach(endIn));
		Assert.False(LockerSupply.InReach(endOut));
	}

	[Fact]
	public void TheScenariosSeatsStandWhereThePlanSaysTheyDo()
	{
		// locker_launcher_vs_tank's seat, 0.75 m from the face; where it stood before
		// L2, 1.25 m — outside the reach, inside the request's slack; and
		// locker_kit_refused's, 3 m out.
		float yaw = MathF.PI;
		Assert.Equal(0.75f, LockerSupply.FootprintDistance(new Vector3(8f, 1f, 10f), TestLocker, yaw), Precision);
		Assert.Equal(1.25f, LockerSupply.FootprintDistance(new Vector3(8f, 1f, 9.5f), TestLocker, yaw), Precision);
		Assert.Equal(3f, LockerSupply.FootprintDistance(new Vector3(8f, 1f, 7.75f), TestLocker, yaw), Precision);
	}

	[Fact]
	public void AMetreFromTheOriginIsNotAMetreFromTheCabinet()
	{
		// What measuring from the footprint is for: from the origin a metre reached
		// 0.75 m past the face and only 0.4 m past an end.
		float yaw = 0f;
		Assert.Equal(0.4f, LockerSupply.FootprintDistance(TestLocker + new Vector3(1f, 0f, 0f), TestLocker, yaw),
			Precision);
		Assert.Equal(0.75f, LockerSupply.FootprintDistance(TestLocker + new Vector3(0f, 0f, 1f), TestLocker, yaw),
			Precision);
	}

	[Fact]
	public void TheCornerIsMeasuredToTheCorner()
	{
		Vector3 feet = TestLocker + new Vector3(0.6f + 0.3f, 0f, 0.25f + 0.4f);
		Assert.Equal(0.5f, LockerSupply.FootprintDistance(feet, TestLocker, 0f), Precision);
	}

	[Fact]
	public void AQuarterTurnTurnsTheFootprint()
	{
		// Turned 90° the long side runs along world Z: a metre off the end in X is now
		// only 0.75 m off its face.
		float quarter = MathF.PI * 0.5f;
		Assert.Equal(0.75f, LockerSupply.FootprintDistance(TestLocker + new Vector3(1f, 0f, 0f), TestLocker, quarter),
			Precision);
		Assert.Equal(0.4f, LockerSupply.FootprintDistance(TestLocker + new Vector3(0f, 0f, 1f), TestLocker, quarter),
			Precision);
	}

	[Fact]
	public void HeightIsIgnored()
	{
		Assert.Equal(
			LockerSupply.FootprintDistance(new Vector3(8f, 0f, 9f), TestLocker, MathF.PI),
			LockerSupply.FootprintDistance(new Vector3(8f, 5f, 9f), TestLocker, MathF.PI));
	}

	[Fact]
	public void TheStandPointIsInFrontOfTheRacksAndInReach()
	{
		// The Test map's locker faces −Z, towards the spawn.
		Vector3 stand = LockerSupply.StandPoint(TestLocker, MathF.PI);
		Assert.Equal(8f, stand.X, Precision);
		Assert.Equal(11f - 0.25f - LockerSupply.StandOffMeters, stand.Z, Precision);
		Assert.Equal(TestLocker.Y, stand.Y);

		float distance = LockerSupply.FootprintDistance(stand, TestLocker, MathF.PI);
		Assert.Equal(LockerSupply.StandOffMeters, distance, Precision);
		Assert.True(LockerSupply.InReach(distance));

		// Unturned, it is on +Z.
		Assert.True(LockerSupply.StandPoint(TestLocker, 0f).Z > TestLocker.Z);
	}
}
