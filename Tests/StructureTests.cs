using System;
using Gdpyr.Sim;
using Godot;
using Xunit;

namespace Gdpyr.Tests;

/// <summary>
/// The rules a builder's structures go up, stand and come down by
/// (docs/NETCODE.md §10.5): the boxes a round is tested against, the rectangle
/// placement is checked with, the arithmetic of a site's work and health, and
/// the computer strategist's plan for where to put them.
/// </summary>
public class StructureTests
{
	/// <summary>The sandbag wall's body: four metres across, 0.8 deep, 1.1 high.</summary>
	private static readonly StructureShape Wall = new(StructureBox.Standing(4f, 0.8f, 1.1f));

	/// <summary>A tower: a 2 m column seven metres tall and a 3.2 m cabin on top of it.</summary>
	private static readonly StructureShape Tower = new(StructureBox.Standing(2f, 2f, 7f),
		StructureBox.Standing(3.2f, 3.2f, 1.1f, elevation: 7f));

	// ---- owner ids -----------------------------------------------------------

	[Fact]
	public void AStructureIsOnTheStrategistsSideOfTheOwnerIdSpace()
	{
		for (int slot = 0; slot < SimConfig.MaxStructures; slot++)
		{
			int id = OwnerId.ForStructure(slot);
			Assert.True(id < 0);
			Assert.True(OwnerId.IsStructure(id));
			Assert.False(OwnerId.IsUnit(id));
			Assert.False(OwnerId.IsDefense(id));
			Assert.False(OwnerId.IsPeer(id));
			Assert.Equal(slot, OwnerId.StructureOf(id));
		}
	}

	[Fact]
	public void NoOtherShooterIsTakenForAStructure()
	{
		Assert.False(OwnerId.IsStructure(OwnerId.ForUnit(ushort.MaxValue)));
		Assert.False(OwnerId.IsStructure(OwnerId.ForDefense(SimConfig.MaxDefenses - 1)));
		Assert.False(OwnerId.IsStructure(OwnerId.ForStructure(SimConfig.MaxStructures)));
		Assert.False(OwnerId.IsStructure(OwnerId.ForPeer(7)));
		Assert.False(OwnerId.IsStructure(OwnerId.None));
		Assert.Equal(-1, OwnerId.StructureOf(OwnerId.ForUnit(3)));

		// The two ranges sit back to back: the last defence and the first structure
		// are neighbours, and neither is taken for the other.
		Assert.Equal(OwnerId.ForDefense(SimConfig.MaxDefenses - 1) - 1, OwnerId.ForStructure(0));
	}

	// ---- boxes -----------------------------------------------------------------

	[Fact]
	public void ARoundAtChestHeightStopsOnTheFrontOfAWall()
	{
		var pose = new StructurePose(Vector3.Zero, 0f);
		var from = new Vector3(0f, 0.9f, 10f);
		var to = new Vector3(0f, 0.9f, -10f);

		Assert.True(pose.SegmentIntersects(Wall, from, to, 0f, out float t));

		// Enters the front face, 0.4 m in front of the centre line: 9.6 m of 20.
		Assert.Equal(9.6f / 20f, t, 4);
	}

	[Fact]
	public void ARoundOverTheTopOfAWallIsNotStopped()
	{
		var pose = new StructurePose(Vector3.Zero, 0f);
		Assert.False(pose.SegmentIntersects(Wall, new Vector3(0f, 1.3f, 10f), new Vector3(0f, 1.3f, -10f), 0f,
			out _));
	}

	[Fact]
	public void ARoundPastTheEndOfAWallIsNotStopped()
	{
		var pose = new StructurePose(Vector3.Zero, 0f);
		Assert.False(pose.SegmentIntersects(Wall, new Vector3(2.2f, 0.5f, 10f), new Vector3(2.2f, 0.5f, -10f), 0f,
			out _));
	}

	[Fact]
	public void ASweptRoundClipsTheEdgeItWouldOtherwiseMiss()
	{
		var pose = new StructurePose(Vector3.Zero, 0f);
		var from = new Vector3(2.1f, 0.5f, 10f);
		var to = new Vector3(2.1f, 0.5f, -10f);

		Assert.False(pose.SegmentIntersects(Wall, from, to, 0f, out _));
		Assert.True(pose.SegmentIntersects(Wall, from, to, 0.15f, out _));
	}

	[Fact]
	public void ASegmentThatStartsInsideEntersAtZero()
	{
		var pose = new StructurePose(Vector3.Zero, 0f);
		Assert.True(pose.SegmentIntersects(Wall, new Vector3(0f, 0.5f, 0f), new Vector3(0f, 0.5f, -10f), 0f,
			out float t));
		Assert.Equal(0f, t);
	}

	[Fact]
	public void TurningAWallTurnsWhatItStops()
	{
		// A quarter turn lays the wall along Z, so a round travelling along Z runs
		// down its length rather than into its face, and one along X is stopped.
		var pose = new StructurePose(new Vector3(5f, 0f, 5f), Mathf.Pi * 0.5f);

		Assert.True(pose.SegmentIntersects(Wall, new Vector3(-5f, 0.5f, 5f), new Vector3(15f, 0.5f, 5f), 0f,
			out float t));
		Assert.Equal(9.6f / 20f, t, 4);

		Assert.False(pose.SegmentIntersects(Wall, new Vector3(5f, 0.5f, 7.5f), new Vector3(5f, 0.5f, 20f), 0f,
			out _));
	}

	[Fact]
	public void ATowerIsHitOnItsCabinAndOnItsColumnButNotBesideTheColumn()
	{
		var pose = new StructurePose(Vector3.Zero, 0f);

		// Through the cabin, which overhangs the column.
		Assert.True(pose.SegmentIntersects(Tower, new Vector3(1.4f, 7.5f, 10f), new Vector3(1.4f, 7.5f, -10f), 0f,
			out float cabin));
		Assert.Equal((10f - 1.6f) / 20f, cabin, 4);

		// Through the column.
		Assert.True(pose.SegmentIntersects(Tower, new Vector3(0f, 3f, 10f), new Vector3(0f, 3f, -10f), 0f, out _));

		// Under the overhang, beside the column.
		Assert.False(pose.SegmentIntersects(Tower, new Vector3(1.4f, 3f, 10f), new Vector3(1.4f, 3f, -10f), 0f,
			out _));
	}

	[Fact]
	public void TheNearerBoxDecides()
	{
		// A shot rising steeply through the column and then the cabin: the column
		// comes first along the segment.
		var pose = new StructurePose(Vector3.Zero, 0f);
		Assert.True(pose.SegmentIntersects(Tower, new Vector3(0f, 1f, 5f), new Vector3(0f, 12f, -5f), 0f,
			out float t));
		float z = 5f - (10f * t);
		Assert.Equal(1f, z, 3);
	}

	[Fact]
	public void AHalfBuiltWallIsOnlyAsTallAsItHasBeenBuilt()
	{
		var pose = new StructurePose(Vector3.Zero, 0f);
		StructureShape half = Wall.Raised(0.5f);

		Assert.Equal(0.55f, half.Height, 4);
		Assert.False(pose.SegmentIntersects(half, new Vector3(0f, 0.9f, 10f), new Vector3(0f, 0.9f, -10f), 0f, out _));
		Assert.True(pose.SegmentIntersects(half, new Vector3(0f, 0.4f, 10f), new Vector3(0f, 0.4f, -10f), 0f, out _));

		// Raising squashes, it does not narrow.
		Assert.Equal(Wall.HalfWidth, half.HalfWidth);
		Assert.Equal(Wall.HalfDepth, half.HalfDepth);
	}

	[Fact]
	public void ATowersCabinComesDownWithTheRestOfIt()
	{
		StructureShape half = Tower.Raised(0.5f);
		Assert.Equal(4.05f, half.Height, 3);
		Assert.Equal(3.5f + 0.275f, half.Top.Center.Y, 3);
	}

	[Fact]
	public void DistanceToABoxIsZeroInsideAndStraightLineOutside()
	{
		var pose = new StructurePose(Vector3.Zero, 0f);

		Assert.Equal(0f, pose.DistanceTo(Wall, new Vector3(1f, 0.5f, 0f)));
		Assert.Equal(1f, pose.DistanceTo(Wall, new Vector3(0f, 0.5f, 1.4f)), 4);
		Assert.Equal(2f, pose.DistanceTo(Wall, new Vector3(0f, 3.1f, 0f)), 4);
	}

	[Fact]
	public void AShapesFootprintCoversEveryBox()
	{
		Assert.Equal(2f, Wall.HalfWidth);
		Assert.Equal(0.4f, Wall.HalfDepth);
		Assert.Equal(1.1f, Wall.Height, 4);

		Assert.Equal(1.6f, Tower.HalfWidth, 4);
		Assert.Equal(1.6f, Tower.HalfDepth, 4);
		Assert.Equal(8.1f, Tower.Height, 4);
	}

	// ---- footprints ------------------------------------------------------------

	[Fact]
	public void TheFootprintsAxesAreTheNodesAxes()
	{
		// Whatever a node turned by this yaw calls its X and Z, so must this.
		float yaw = 0.7f;
		var footprint = new Footprint(Vector3.Zero, yaw, 1f, 1f);
		Vector3 x = Vector3.Right.Rotated(Vector3.Up, yaw);
		Vector3 z = Vector3.Back.Rotated(Vector3.Up, yaw);

		Assert.True(footprint.AxisX.IsEqualApprox(x));
		Assert.True(footprint.AxisZ.IsEqualApprox(z));
	}

	[Fact]
	public void DistanceToAFootprintIsFlat()
	{
		var footprint = new Footprint(new Vector3(10f, 0f, 10f), 0f, 2f, 0.4f);

		Assert.Equal(0f, footprint.DistanceTo(new Vector3(11f, 50f, 10.2f)));
		Assert.Equal(1f, footprint.DistanceTo(new Vector3(10f, 0f, 11.4f)), 4);
		Assert.Equal(MathF.Sqrt(2f), footprint.DistanceTo(new Vector3(13f, 0f, 11.4f)), 4);
	}

	[Fact]
	public void WallsLaidEndToEndDoNotOverlap()
	{
		var a = new Footprint(new Vector3(0f, 0f, 0f), 0f, 2f, 0.4f);
		var b = new Footprint(new Vector3(4f, 0f, 0f), 0f, 2f, 0.4f);

		Assert.False(a.Overlaps(b, StructurePlacement.OverlapAllowanceMeters));
		Assert.False(b.Overlaps(a, StructurePlacement.OverlapAllowanceMeters));
	}

	[Fact]
	public void WallsLaidOverEachOtherDo()
	{
		var a = new Footprint(new Vector3(0f, 0f, 0f), 0f, 2f, 0.4f);
		var b = new Footprint(new Vector3(3.5f, 0f, 0f), 0f, 2f, 0.4f);

		Assert.True(a.Overlaps(b, StructurePlacement.OverlapAllowanceMeters));
	}

	[Fact]
	public void TurnedRectanglesAreTestedOnTheirOwnAxes()
	{
		// A wall turned 45 degrees beside a square: their bounding circles overlap,
		// the rectangles do not.
		var square = new Footprint(Vector3.Zero, 0f, 1f, 1f);
		var turned = new Footprint(new Vector3(2.3f, 0f, 2.3f), Mathf.Pi * 0.25f, 2f, 0.4f);
		Assert.False(square.Overlaps(turned));

		// Moved in, it cuts the square's corner: its near face is 1.16 m out along
		// the diagonal, and the corner is 1.41 m out.
		var closer = new Footprint(new Vector3(1.1f, 0f, 1.1f), Mathf.Pi * 0.25f, 2f, 0.4f);
		Assert.True(square.Overlaps(closer));
	}

	[Fact]
	public void ABuilderIsSentToTheNearestEdgeAndStandsClearOfIt()
	{
		var footprint = new Footprint(new Vector3(0f, 1f, 0f), 0f, 2f, 0.4f);

		Vector3 approach = footprint.ApproachPoint(new Vector3(0.5f, 0f, 20f), Construction.StandoffMeters);

		Assert.Equal(0.5f, approach.X, 4);
		Assert.Equal(0.4f + Construction.StandoffMeters, approach.Z, 4);
		Assert.Equal(1f, approach.Y);
		Assert.True(Construction.InReach(footprint, approach));
		Assert.Equal(Construction.StandoffMeters, footprint.DistanceTo(approach), 4);
	}

	[Fact]
	public void ABuilderStandingOnTheSiteIsWalkedOutThroughTheNearestSide()
	{
		var footprint = new Footprint(Vector3.Zero, 0f, 2f, 0.4f);

		Vector3 approach = footprint.ApproachPoint(new Vector3(1.5f, 0f, -0.1f), 1f);

		Assert.Equal(1.5f, approach.X, 4);
		Assert.Equal(-1.4f, approach.Z, 4);
	}

	[Fact]
	public void ApproachingATurnedSiteLandsOnItsRing()
	{
		var footprint = new Footprint(new Vector3(3f, 0f, -4f), 1.1f, 1.8f, 1.8f);
		for (int i = 0; i < 16; i++)
		{
			float angle = i * Mathf.Tau / 16f;
			var from = new Vector3(3f + (20f * MathF.Cos(angle)), 0f, -4f + (20f * MathF.Sin(angle)));
			Vector3 approach = footprint.ApproachPoint(from, Construction.StandoffMeters);

			Assert.Equal(Construction.StandoffMeters, footprint.DistanceTo(approach), 3);
		}
	}

	[Fact]
	public void TheCornersGoRoundTheRectangle()
	{
		var footprint = new Footprint(new Vector3(1f, 0f, 1f), 0f, 2f, 1f);
		Assert.True(footprint.Corner(0).IsEqualApprox(new Vector3(-1f, 0f, 0f)));
		Assert.True(footprint.Corner(1).IsEqualApprox(new Vector3(3f, 0f, 0f)));
		Assert.True(footprint.Corner(2).IsEqualApprox(new Vector3(3f, 0f, 2f)));
		Assert.True(footprint.Corner(3).IsEqualApprox(new Vector3(-1f, 0f, 2f)));
	}

	// ---- construction ------------------------------------------------------------

	[Fact]
	public void OneBuilderTakesTheBuildTimeAndFinishesAtFullHealth()
	{
		const int buildTicks = 600;
		const float max = 600f;
		int work = 0;
		float health = Construction.InitialHealth(max);
		int finished = 0;

		for (int tick = 0; tick < buildTicks + 60; tick++)
		{
			if (Construction.Work(ref work, ref health, 1, buildTicks, max))
			{
				finished++;
				Assert.Equal(buildTicks - 1, tick);
			}
		}

		Assert.Equal(1, finished);
		Assert.Equal(buildTicks, work);
		Assert.Equal(max, health, 2);
		Assert.True(Construction.IsBuilt(work, buildTicks));
	}

	[Fact]
	public void TwoBuildersTakeHalfAsLongAndAFifthAddsNothing()
	{
		Assert.Equal(300, TicksToBuild(2, 600));
		Assert.Equal(150, TicksToBuild(4, 600));
		Assert.Equal(150, TicksToBuild(5, 600));
		Assert.Equal(int.MaxValue, TicksToBuild(0, 600));
	}

	[Fact]
	public void ASiteShotAtWhileItGoesUpFinishesAsHurtAsItWasMade()
	{
		const int buildTicks = 100;
		const float max = 500f;
		int work = 0;
		float health = Construction.InitialHealth(max);

		for (int i = 0; i < 50; i++)
		{
			Construction.Work(ref work, ref health, 1, buildTicks, max);
		}

		health -= 120f;

		while (!Construction.Work(ref work, ref health, 1, buildTicks, max))
		{
		}

		Assert.Equal(max - 120f, health, 2);
	}

	[Fact]
	public void TheLastTickOfWorkIsNotOverpaidWhenBuildersOutnumberIt()
	{
		const float max = 100f;
		int work = 98;
		float health = 50f;
		Assert.True(Construction.Work(ref work, ref health, 4, 100, max));
		Assert.Equal(100, work);
		Assert.Equal(50f + (2f * max * 0.9f / 100f), health, 3);
	}

	[Fact]
	public void RepairRunsAtHalfTheBuildRateAndStopsAtFull()
	{
		const float max = 600f;
		float health = 300f;

		Assert.False(Construction.Repair(ref health, 1, 600, max));
		Assert.Equal(300.5f, health, 3);

		int ticks = 1;
		while (!Construction.Repair(ref health, 2, 600, max))
		{
			ticks++;
		}

		Assert.Equal(max, health);
		Assert.InRange(ticks, 299, 301);
	}

	[Fact]
	public void NothingMendsWhatIsAlreadyDown()
	{
		float health = 0f;
		Assert.False(Construction.Repair(ref health, 4, 600, 600f));
		Assert.Equal(0f, health);
	}

	[Fact]
	public void ASiteShowsItsFoundationsFromTheStart()
	{
		Assert.Equal(Construction.MinRaisedFraction, Construction.RaisedFraction(0f));
		Assert.Equal(0.6f, Construction.RaisedFraction(0.6f));
		Assert.Equal(1f, Construction.RaisedFraction(1.4f));
	}

	// ---- placement ---------------------------------------------------------------

	[Fact]
	public void AClearSiteIsAccepted()
	{
		var site = new Footprint(new Vector3(0f, 0.5f, -30f), 0f, 1.8f, 1.8f);
		Assert.Equal(PlacementResult.Ok, StructurePlacement.Check(site, ReadOnlySpan<Footprint>.Empty,
			new[] { new Vector3(100f, 0.5f, -106f) }));
	}

	[Fact]
	public void TheDoorIsKeptClear()
	{
		var door = new Vector3(100f, 0.5f, -106f);
		var near = new Footprint(door + new Vector3(0f, 0f, 9f), 0f, 2f, 0.4f);
		var clear = new Footprint(door + new Vector3(0f, 0f, 11f), 0f, 2f, 0.4f);

		Assert.Equal(PlacementResult.TooCloseToBarracks,
			StructurePlacement.Check(near, ReadOnlySpan<Footprint>.Empty, new[] { door }));
		Assert.Equal(PlacementResult.Ok,
			StructurePlacement.Check(clear, ReadOnlySpan<Footprint>.Empty, new[] { door }));
	}

	[Fact]
	public void ASiteOnTopOfAnotherIsRefused()
	{
		var existing = new[] { new Footprint(new Vector3(0f, 0f, 0f), 0f, 1.8f, 1.8f) };
		var site = new Footprint(new Vector3(2f, 0f, 0f), 0f, 2f, 0.4f);

		Assert.Equal(PlacementResult.Overlaps, StructurePlacement.Check(site, existing, ReadOnlySpan<Vector3>.Empty));
	}

	[Theory]
	[InlineData(400f, 0f)]
	[InlineData(0f, -330f)]
	[InlineData(float.NaN, 0f)]
	[InlineData(0f, float.PositiveInfinity)]
	public void ASiteOffTheMapIsRefused(float x, float z)
	{
		var site = new Footprint(new Vector3(x, 0f, z), 0f, 2f, 0.4f);
		Assert.Equal(PlacementResult.OutOfBounds,
			StructurePlacement.Check(site, ReadOnlySpan<Footprint>.Empty, ReadOnlySpan<Vector3>.Empty));
	}

	[Fact]
	public void AWallFacingAThreatLiesAcrossTheLineToIt()
	{
		var at = new Vector3(10f, 0f, 10f);
		var threat = new Vector3(40f, 0f, -20f);
		float yaw = StructurePlacement.YawFacing(at, threat);
		var footprint = new Footprint(at, yaw, 2f, 0.4f);

		Vector3 towards = (threat - at).Normalized();
		Assert.Equal(1f, footprint.AxisZ.Dot(towards), 4);
		Assert.Equal(0f, footprint.AxisX.Dot(towards), 4);
		Assert.Equal(0f, StructurePlacement.YawFacing(at, at));
	}

	// ---- orders ------------------------------------------------------------------

	[Fact]
	public void ABuildOrderCannotArriveThroughTheGenericOrderMessage()
	{
		Assert.False(UnitOrder.IsIssuable((byte)OrderKind.Build));
	}

	[Fact]
	public void ABuilderWalksToWhereItWasToldToStandAndKeepsWalkingUnderFire()
	{
		var order = new UnitOrder
		{
			Kind = OrderKind.Build,
			Target = new Vector3(4f, 0f, 1.6f),
			Anchor = Vector3.Zero,
			TargetOwnerId = OwnerId.ForStructure(2),
		};
		var traits = new UnitTraits(35f, 20f, 1f, 15f);

		Vector3 destination = UnitBrain.Destination(order, new Vector3(20f, 0f, 20f), hasTarget: true,
			new Vector3(30f, 0f, 30f), traits, out bool hasDestination);

		Assert.True(hasDestination);
		Assert.Equal(order.Target, destination);
		Assert.False(UnitBrain.HoldsWhileEngaging(OrderKind.Build));
	}

	// ---- the computer strategist ---------------------------------------------------

	[Fact]
	public void ABuilderIsBoughtOnlyOnceThereIsAnArmyAndOnlyUpToTheCount()
	{
		StrategistTraits traits = StrategistTraits.Default;

		Assert.False(StrategistBrain.ShouldQueueBuilder(1000, 60, 0, 0, traits.UnitsBeforeBuilders - 1, traits));
		Assert.True(StrategistBrain.ShouldQueueBuilder(1000, 60, 0, 0, traits.UnitsBeforeBuilders, traits));
		Assert.False(StrategistBrain.ShouldQueueBuilder(1000, 60, traits.Builders, 0, 10, traits));
		Assert.False(StrategistBrain.ShouldQueueBuilder(1000, 60, 0, traits.Builders, 10, traits));
		Assert.False(StrategistBrain.ShouldQueueBuilder(59, 60, 0, 0, 10, traits));
	}

	[Fact]
	public void AStrategistThatWantsNoBuildersNeverBuysOne()
	{
		var traits = new StrategistTraits(30, 2, 4, 12f, builders: 0);
		Assert.False(StrategistBrain.ShouldQueueBuilder(100000, 60, 0, 0, 50, traits));
	}

	[Fact]
	public void TheArmyStopsGrowingOnlyWhileABuilderWaitsAndTheArmyIsBigEnough()
	{
		StrategistTraits traits = StrategistTraits.Default;

		Assert.Equal(150, StrategistBrain.StructureSavings(150, builderWaiting: true, traits.UnitsBeforeBuilders,
			traits));
		Assert.Equal(0, StrategistBrain.StructureSavings(150, builderWaiting: false, 20, traits));
		Assert.Equal(0, StrategistBrain.StructureSavings(150, builderWaiting: true, traits.UnitsBeforeBuilders - 1,
			traits));
		Assert.Equal(0, StrategistBrain.StructureSavings(0, builderWaiting: true, 20, traits));
	}

	[Fact]
	public void EverySiteGetsAPillboxBeforeAnySiteGetsAWall()
	{
		var sites = new[]
		{
			new FortificationSite { HasPillbox = true },
			new FortificationSite(),
		};

		Assert.True(StrategistBrain.TryPlanStructure(sites, out int site, out byte kind));
		Assert.Equal(1, site);
		Assert.Equal(StructureKinds.Pillbox, kind);

		sites[1].HasPillbox = true;
		Assert.True(StrategistBrain.TryPlanStructure(sites, out site, out kind));
		Assert.Equal(0, site);
		Assert.Equal(StructureKinds.SandbagWall, kind);

		sites[0].HasWall = true;
		sites[1].HasWall = true;
		Assert.True(StrategistBrain.TryPlanStructure(sites, out site, out kind));
		Assert.Equal(0, site);
		Assert.Equal(StructureKinds.SniperTower, kind);

		sites[0].HasTower = true;
		sites[1].HasTower = true;
		Assert.False(StrategistBrain.TryPlanStructure(sites, out _, out _));
		Assert.False(StrategistBrain.TryPlanStructure(ReadOnlySpan<FortificationSite>.Empty, out _, out _));
	}

	[Fact]
	public void TheWallScreensThePillboxAndTheTowerStandsBehind()
	{
		var anchor = new Vector3(0f, 0.5f, -35f);
		var threat = new Vector3(0f, 0.5f, 7.5f);

		StrategistBrain.Layout(anchor, threat, StructureKinds.Pillbox, out Vector3 pillbox, out _);
		StrategistBrain.Layout(anchor, threat, StructureKinds.SandbagWall, out Vector3 wall, out float wallYaw);
		StrategistBrain.Layout(anchor, threat, StructureKinds.SniperTower, out Vector3 tower, out _);

		float Along(Vector3 p) => p.Z - anchor.Z;
		Assert.True(Along(wall) > Along(pillbox));
		Assert.True(Along(pillbox) > 0f);
		Assert.True(Along(tower) < 0f);

		// The wall lies across the line to the threat.
		var footprint = new Footprint(wall, wallYaw, 2f, 0.4f);
		Assert.Equal(1f, footprint.AxisZ.Dot((threat - anchor).Normalized()), 4);

		// And the three do not stand on each other.
		var pillboxFootprint = new Footprint(pillbox, wallYaw, 1.8f, 1.8f);
		var towerFootprint = new Footprint(tower, wallYaw, 1.6f, 1.6f);
		Assert.False(footprint.Overlaps(pillboxFootprint, StructurePlacement.OverlapAllowanceMeters));
		Assert.False(towerFootprint.Overlaps(pillboxFootprint, StructurePlacement.OverlapAllowanceMeters));
	}

	private static int TicksToBuild(int builders, int buildTicks)
	{
		int work = 0;
		float health = 10f;
		for (int tick = 1; tick <= buildTicks * 2; tick++)
		{
			if (Construction.Work(ref work, ref health, builders, buildTicks, 100f))
			{
				return tick;
			}
		}

		return int.MaxValue;
	}
}
