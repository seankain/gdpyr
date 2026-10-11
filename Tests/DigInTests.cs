using System;
using Gdpyr.Sim;
using Gdpyr.Sim.Htn;
using Godot;
using Xunit;

namespace Gdpyr.Tests;

/// <summary>
/// The computer strategist's sandbags for a squad holding ground (docs/COVER.md §7):
/// how many walls a ring wants, where they go, and that a wall laid there gives the
/// ring the spots the rule counts on.
/// </summary>
public class DigInTests
{
	/// <summary>sandbag_wall.tres: four metres across, 1.1 high, 0.8 deep.</summary>
	private static readonly Vector3 Sandbags = new(4f, 1.1f, 0.8f);

	private static readonly Vector3 Anchor = new(100f, 0.5f, -86f);
	private static readonly Vector3 Spawns = new(4f, 0.5f, 7.5f);

	/// <summary>Four riflemen on a ring: 4 m spacing makes the 4 m minimum radius.</summary>
	private static readonly float Ring = DefendPosts.Radius(4, 4f, 4f, 10f);

	[Theory]
	[InlineData(4, 0, 0, 2)]
	[InlineData(4, 3, 0, 1)]
	[InlineData(4, 4, 0, 0)]
	[InlineData(2, 0, 0, 1)]
	[InlineData(6, 0, 1, 1)]
	[InlineData(10, 0, 2, 0)]
	[InlineData(0, 0, 0, 0)]
	[InlineData(-1, 5, -3, 0)]
	public void AWallForEveryThreeDefendersWithoutASpot_AtMostTwo(int defenders, int spots, int walls, int wanted)
	{
		Assert.Equal(wanted, DigIn.WallsWanted(defenders, spots, walls));
	}

	[Fact]
	public void TheFirstWallStandsJustOutsideTheRingOnTheBearingItsPostsFace()
	{
		DigIn.Layout(Anchor, Spawns, Ring, 0, Sandbags.X, out Vector3 at, out float yaw);

		float bearing = DefendPosts.Bearing(Anchor, Spawns);
		var forward = new Vector3(MathF.Sin(bearing), 0f, MathF.Cos(bearing));
		Vector3 expected = Anchor + (forward * (Ring + DigIn.OutsideRingMeters));

		Assert.True(at.IsEqualApprox(expected));
		Assert.Equal(StructurePlacement.YawFacing(Anchor, Anchor + forward), yaw, 4);

		// Post 0 of the ring, the one facing the threat, is right behind it.
		Vector3 post = DefendPosts.Post(Anchor, 0, 4, bearing, Ring);
		Assert.Equal(DigIn.OutsideRingMeters, post.DistanceTo(at), 3);
	}

	[Fact]
	public void TheSecondAndThirdWallsLieAlongsideTheFirst()
	{
		DigIn.Layout(Anchor, Spawns, Ring, 0, Sandbags.X, out Vector3 middle, out float yaw);
		DigIn.Layout(Anchor, Spawns, Ring, 1, Sandbags.X, out Vector3 one, out float yawOne);
		DigIn.Layout(Anchor, Spawns, Ring, 2, Sandbags.X, out Vector3 two, out _);

		float apart = Sandbags.X + DigIn.GapMeters;
		Assert.Equal(apart, middle.DistanceTo(one), 3);
		Assert.Equal(apart, middle.DistanceTo(two), 3);
		Assert.Equal(2f * apart, one.DistanceTo(two), 3);
		Assert.Equal(yaw, yawOne);

		// Side by side, not overlapping: the placement rule would refuse an overlap.
		var a = new Footprint(middle, yaw, Sandbags.X * 0.5f, Sandbags.Z * 0.5f);
		var b = new Footprint(one, yaw, Sandbags.X * 0.5f, Sandbags.Z * 0.5f);
		Assert.False(a.Overlaps(b, StructurePlacement.OverlapAllowanceMeters));
	}

	[Fact]
	public void TwoLaidWallsHideAFourManRingFromItsThreat()
	{
		var board = new CoverBoard();
		var query = new CoverQuery
		{
			From = Anchor,
			Threat = Spawns,
			ThreatEyeMeters = 1.5f,
			ThreatAimMeters = 1.0f,
			SearchMeters = Ring + DigIn.OutsideRingMeters + 5f,
			BodyRadiusMeters = 0.4f,
			ProtectMeters = 0.9f,
			EyeMeters = 1.5f,
			MustFire = true,
		};

		Span<CoverSpot> spots = stackalloc CoverSpot[4];
		Assert.Equal(0, board.FindSpots(query, default, spots));
		Assert.Equal(2, DigIn.WallsWanted(4, 0, 0));

		DigIn.Layout(Anchor, Spawns, Ring, 0, Sandbags.X, out Vector3 at, out float yaw);
		board.SetStructure(0, at, yaw, Sandbags);
		int one = board.FindSpots(query, default, spots);
		Assert.Equal(DigIn.SpotsPerWall, one);
		Assert.Equal(1, DigIn.WallsWanted(4, one, 1));

		DigIn.Layout(Anchor, Spawns, Ring, 1, Sandbags.X, out at, out yaw);
		board.SetStructure(1, at, yaw, Sandbags);
		int two = board.FindSpots(query, default, spots);
		Assert.Equal(4, two);
		Assert.Equal(0, DigIn.WallsWanted(4, two, 2));

		for (int i = 0; i < two; i++)
		{
			Assert.True(board.Protects(spots[i].Position, query));
			Assert.True(spots[i].Position.DistanceTo(Anchor) <= 10f);
		}
	}
}
