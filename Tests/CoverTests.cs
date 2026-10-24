using System;
using Gdpyr.Sim;
using Godot;
using Xunit;

namespace Gdpyr.Tests;

/// <summary>
/// The cover board (docs/COVER.md §2): which boxes count, where a body has to stand
/// for a box to hide it from a threat, whether it can still shoot from there, and
/// that the answer is the same every time and costs nothing to ask.
/// </summary>
public class CoverTests
{
	/// <summary>A low wall like the map's: four metres across, 0.8 deep, 1.15 high, its front facing +Z.</summary>
	private static readonly Vector3 LowWall = new(4f, 1.15f, 0.8f);

	/// <summary>A block nothing standing fires over.</summary>
	private static readonly Vector3 Block = new(3f, 2.8f, 3f);

	/// <summary>A rifleman behind cover, a rifleman in front of it, twenty metres off.</summary>
	private static CoverQuery Rifleman(Vector3 from, Vector3 threat, bool mustFire = true) => new()
	{
		From = from,
		Threat = threat,
		ThreatEyeMeters = 1.5f,
		ThreatAimMeters = 1.0f,
		SearchMeters = 12f,
		BodyRadiusMeters = 0.4f,
		ProtectMeters = 0.9f,
		EyeMeters = 1.5f,
		MustFire = mustFire,
		MinThreatMeters = 6f,
		MaxThreatMeters = 60f,
	};

	/// <summary>A wall standing on the ground at the origin, as the map would mark it (by its centre).</summary>
	private static CoverBoard WallAtOrigin(Vector3 size, float yaw = 0f)
	{
		var board = new CoverBoard();
		Assert.Equal(0, board.AddLevel(new Vector3(0f, size.Y * 0.5f, 0f), yaw, size));
		return board;
	}

	// ---- the first box along a line (docs/TACTICS.md §5.4) ----------------------

	[Fact]
	public void TheFirstBoxAlongALine_IsTheNearerOfTwo_AtWhereItEntersIt()
	{
		CoverBoard board = WallAtOrigin(LowWall);
		Assert.Equal(1, board.AddLevel(new Vector3(0f, 0.6f, -10f), 0f, LowWall));

		// From z = 20 to z = -20 at knee height: the wall at the origin first, entered at its near face.
		int first = board.FirstHit(new Vector3(0f, 0.5f, 20f), new Vector3(0f, 0.5f, -20f), out float t);
		Assert.Equal(0, first);
		Assert.Equal((20f - (LowWall.Z * 0.5f)) / 40f, t, 4);

		// The other way, the other wall.
		Assert.Equal(1, board.FirstHit(new Vector3(0f, 0.5f, -20f), new Vector3(0f, 0.5f, 20f), out _));
	}

	[Fact]
	public void ALineOverOrPastEveryBox_EntersNone()
	{
		CoverBoard board = WallAtOrigin(LowWall);

		Assert.Equal(-1, board.FirstHit(new Vector3(0f, 1.5f, 20f), new Vector3(0f, 1.5f, -20f), out float t));
		Assert.Equal(1f, t);
		Assert.Equal(-1, board.FirstHit(new Vector3(3f, 0.5f, 20f), new Vector3(3f, 0.5f, -20f), out _));
		Assert.Equal(-1, new CoverBoard().FirstHit(Vector3.Zero, new Vector3(0f, 0f, 5f), out _));
	}

	[Theory]
	[InlineData(0f)]
	[InlineData(0.7854f)]
	[InlineData(1.5708f)]
	public void ATurnedWall_IsFoundWhereverTheLineCrossesIt(float yaw)
	{
		// The broad phase is a circle round the box's middle, half its diagonal across:
		// the wall's ends are inside it however it is turned.
		CoverBoard board = WallAtOrigin(LowWall, yaw);
		Vector3 end = new Vector3(1.9f, 0f, 0f).Rotated(Vector3.Up, yaw);
		Vector3 across = new Vector3(0f, 0f, 10f).Rotated(Vector3.Up, yaw);

		Assert.Equal(0, board.FirstHit(end + across + (Vector3.Up * 0.5f), end - across + (Vector3.Up * 0.5f), out _));
	}

	[Fact]
	public void TheBoxesNearALine_AnswerEveryChordOnIt_AsTheWholeBoardDoes()
	{
		// What a grenade's flight relies on: gather once, then ask each chord of the arc.
		var board = new CoverBoard();
		var random = new Random(7);
		for (int i = 0; i < CoverBoard.LevelCapacity; i++)
		{
			board.AddLevel(new Vector3(random.Next(-30, 30), 0.6f, random.Next(-30, 30)), (float)random.NextDouble() * 3f,
				random.Next(2) == 0 ? LowWall : Block);
		}

		Span<int> near = stackalloc int[CoverBoard.Capacity];
		for (int line = 0; line < 200; line++)
		{
			var from = new Vector3(random.Next(-35, 35), 1.6f, random.Next(-35, 35));
			var to = new Vector3(random.Next(-35, 35), 0f, random.Next(-35, 35));
			int count = board.Near(from, to, 0.1f, near);

			for (int chord = 0; chord < 20; chord++)
			{
				Vector3 a = from.Lerp(to, chord / 20f) + (Vector3.Up * (float)random.NextDouble());
				Vector3 b = from.Lerp(to, (chord + 1) / 20f);
				int all = board.FirstHit(a, b, out float tAll);
				int some = board.FirstHit(near[..count], a, b, out float tSome);

				Assert.Equal(all, some);
				Assert.Equal(tAll, tSome);
			}
		}
	}

	// ---- on the way (docs/TACTICS.md §5.1) -----------------------------------

	/// <summary>A unit at the origin walking to +X, as a move order would send it.</summary>
	private static CoverQuery OnItsWay(CoverQuery query, Vector3 toward, float back = 3f, float offLine = 6f)
	{
		query.HasToward = true;
		query.Toward = toward;
		query.MaxBackMeters = back;
		query.MaxOffLineMeters = offLine;
		return query;
	}

	[Fact]
	public void AQueryGoingNowhere_TakesASpotAnywhere()
	{
		CoverQuery query = Rifleman(Vector3.Zero, new Vector3(0f, 0f, 30f));
		Assert.True(CoverBoard.OnTheWay(new Vector3(-50f, 0f, -50f), query));
	}

	[Theory]
	[InlineData(10f, 0f, true)]   // ahead on the line
	[InlineData(-2.9f, 0f, true)] // a little behind: within the 3 m back
	[InlineData(-3.5f, 0f, false)] // further behind than that
	[InlineData(10f, 5.9f, true)]  // off the line by less than 6 m
	[InlineData(10f, 6.5f, false)] // by more
	[InlineData(48f, 0f, true)]    // short of the end
	[InlineData(52.5f, 0f, true)]  // past the end, but no further from it than the walker
	[InlineData(60f, 0f, false)]   // past the end by more than the line reaches
	public void ASpotOnTheWay_IsNoFurtherFromTheDestination_AndNearTheLine(float x, float z, bool onTheWay)
	{
		CoverQuery query = OnItsWay(Rifleman(Vector3.Zero, new Vector3(25f, 0f, 40f)), new Vector3(50f, 0f, 0f));
		Assert.Equal(onTheWay, CoverBoard.OnTheWay(new Vector3(x, 0f, z), query));
	}

	[Fact]
	public void TheSearch_LeavesASpotThatIsNotOnTheWay()
	{
		// A wall at the origin, a threat 20 m to +Z: the spot behind it is on the -Z face.
		CoverBoard board = WallAtOrigin(LowWall);
		CoverQuery query = Rifleman(new Vector3(0f, 0f, -4f), new Vector3(0f, 0f, 20f));
		Assert.True(board.TryFindSpot(query, ReadOnlySpan<Vector3>.Empty, out CoverSpot free));

		// Walking towards the wall's spot and beyond: it is on the way.
		Assert.True(board.TryFindSpot(OnItsWay(query, new Vector3(0f, 0f, -40f)), ReadOnlySpan<Vector3>.Empty,
			out CoverSpot ahead));
		Assert.Equal(free.Position, ahead.Position);

		// Walking 40 m to +X from 10 m out: the wall is off the line, and refused.
		CoverQuery away = Rifleman(new Vector3(-10f, 0f, -12f), new Vector3(0f, 0f, 20f));
		away.SearchMeters = 15f;
		Assert.True(board.TryFindSpot(away, ReadOnlySpan<Vector3>.Empty, out _));
		Assert.False(board.TryFindSpot(OnItsWay(away, new Vector3(30f, 0f, -12f), offLine: 6f),
			ReadOnlySpan<Vector3>.Empty, out _));
	}

	[Fact]
	public void StayingOnItsSide_ItTakesNoSpotBehindABoxItStandsInFrontOf()
	{
		// docs/TACTICS.md §5.1: in front of the wall, towards the threat, the spot behind it
		// is a walk round its end, down the line the fire is coming along.
		CoverBoard board = WallAtOrigin(LowWall);
		CoverQuery inFront = Rifleman(new Vector3(0f, 0f, 2f), new Vector3(0f, 0f, 20f));
		Assert.True(board.TryFindSpot(inFront, ReadOnlySpan<Vector3>.Empty, out _));
		inFront.StaySameSide = true;
		Assert.False(board.TryFindSpot(inFront, ReadOnlySpan<Vector3>.Empty, out _));

		// Behind it, or beside its end, the spot is its own side's.
		CoverQuery behind = Rifleman(new Vector3(0f, 0f, -3f), new Vector3(0f, 0f, 20f));
		behind.StaySameSide = true;
		Assert.True(board.TryFindSpot(behind, ReadOnlySpan<Vector3>.Empty, out _));
		CoverQuery beside = Rifleman(new Vector3(3f, 0f, 0f), new Vector3(0f, 0f, 20f));
		beside.StaySameSide = true;
		Assert.True(board.TryFindSpot(beside, ReadOnlySpan<Vector3>.Empty, out _));
	}

	// ---- the board ------------------------------------------------------------

	[Fact]
	public void ABoxTooLowToHideAChestIsNotCover()
	{
		var board = new CoverBoard();
		Assert.Equal(-1, board.AddLevel(Vector3.Zero, 0f, new Vector3(4f, 0.5f, 1f)));
		Assert.Equal(-1, board.AddLevel(Vector3.Zero, 0f, new Vector3(0f, 2f, 1f)));
		Assert.Equal(-1, board.AddLevel(Vector3.Zero, 0f, new Vector3(float.NaN, 2f, 1f)));
		Assert.Equal(0, board.Count);
		Assert.Equal(0, board.AddLevel(Vector3.Zero, 0f, new Vector3(4f, CoverBoard.MinHeightMeters, 1f)));
		Assert.Equal(1, board.Count);
	}

	[Fact]
	public void AMapBoxStandsOnTheBottomOfItsCsgBox()
	{
		var board = new CoverBoard();
		int index = board.AddLevel(new Vector3(10f, 1.075f, -5f), 0.3f, LowWall);
		Assert.True(board.BaseOf(index).IsEqualApprox(new Vector3(10f, 0.5f, -5f)));
		Assert.Equal(1.15f, board.HeightOf(index), 4);
		Assert.Equal(0.3f, board.YawOf(index));
		Assert.Equal(2f, board.FootprintOf(index).HalfWidth);
		Assert.Equal(0.4f, board.FootprintOf(index).HalfDepth);
	}

	[Fact]
	public void AFullBoardRefusesAndCountsTheRest()
	{
		var board = new CoverBoard();
		for (int i = 0; i < CoverBoard.LevelCapacity; i++)
		{
			Assert.Equal(i, board.AddLevel(new Vector3(i * 10f, 0.6f, 0f), 0f, LowWall));
		}

		Assert.Equal(-1, board.AddLevel(Vector3.Zero, 0f, LowWall));
		Assert.Equal(1, board.Overflow);
		Assert.Equal(CoverBoard.LevelCapacity, board.LevelCount);

		board.ClearLevel();
		Assert.Equal(0, board.Count);
		Assert.Equal(0, board.Overflow);
		Assert.Equal(0, board.AddLevel(Vector3.Zero, 0f, LowWall));
	}

	[Fact]
	public void AStructureSlotHasABoxOfItsOwnUntilItIsCleared()
	{
		var board = new CoverBoard();
		board.SetStructure(3, new Vector3(5f, 0.5f, 5f), 0f, new Vector3(4f, 1.1f, 0.8f));
		int index = CoverBoard.StructureIndex(3);
		Assert.True(board.IsActive(index));
		Assert.Equal(1, board.Count);

		// Set again, it moves rather than counting twice.
		board.SetStructure(3, new Vector3(6f, 0.5f, 5f), 0f, new Vector3(4f, 1.1f, 0.8f));
		Assert.Equal(1, board.Count);
		Assert.Equal(6f, board.BaseOf(index).X);

		board.ClearStructure(3);
		Assert.False(board.IsActive(index));
		Assert.Equal(0, board.Count);

		// Out of range is ignored rather than written past the end.
		board.SetStructure(SimConfig.MaxStructures, Vector3.Zero, 0f, LowWall);
		board.ClearStructure(-1);
		Assert.Equal(0, board.Count);
	}

	[Fact]
	public void ClearingTheMapLeavesTheStructures()
	{
		var board = WallAtOrigin(LowWall);
		board.SetStructure(0, new Vector3(20f, 0f, 0f), 0f, LowWall);
		board.ClearLevel();
		Assert.Equal(1, board.Count);
		board.Clear();
		Assert.Equal(0, board.Count);
	}

	// ---- occlusion -------------------------------------------------------------

	[Fact]
	public void AWallOccludesALineThroughItAndNotOneOverIt()
	{
		var board = WallAtOrigin(LowWall);
		Assert.True(board.Occludes(0, new Vector3(0f, 1f, 10f), new Vector3(0f, 1f, -10f)));
		Assert.False(board.Occludes(0, new Vector3(0f, 1.5f, 10f), new Vector3(0f, 1.5f, -10f)));
		Assert.False(board.Occludes(0, new Vector3(5f, 1f, 10f), new Vector3(5f, 1f, -10f)));
	}

	[Fact]
	public void ATurnedWallOccludesAlongItsOwnFront()
	{
		// Turned a quarter: its front now faces +X, so a line along X goes through it.
		var board = WallAtOrigin(LowWall, MathF.PI * 0.5f);
		Assert.True(board.Occludes(0, new Vector3(10f, 1f, 0f), new Vector3(-10f, 1f, 0f)));
		Assert.True(board.Occludes(0, new Vector3(10f, 1f, 1.5f), new Vector3(-10f, 1f, 1.5f)));
		Assert.False(board.Occludes(0, new Vector3(1f, 1f, 10f), new Vector3(1f, 1f, -10f)));
	}

	// ---- spots -----------------------------------------------------------------

	[Fact]
	public void ASpotIsOnTheFarSideOfTheWallFromTheThreat()
	{
		var board = WallAtOrigin(LowWall);
		CoverQuery query = Rifleman(new Vector3(0f, 0f, -6f), new Vector3(0f, 0f, 20f));

		Assert.True(board.TryFindSpot(query, default, out CoverSpot spot));
		Assert.Equal(0, spot.Object);
		Assert.True(spot.Low);

		// Behind the back face, a body's radius and a gap off it.
		Assert.Equal(-(0.4f + 0.4f + CoverBoard.GapMeters), spot.Position.Z, 3);
		Assert.InRange(MathF.Abs(spot.Position.X), 0f, 2f);
		Assert.True(board.Protects(spot.Position, query));
	}

	[Fact]
	public void ABodyIsMeasuredFromTheGroundTheWallStandsOn()
	{
		// A spot snapped to the navigation mesh comes back a cell above the floor; the
		// chest it hides is still the chest of somebody standing on that floor.
		var board = WallAtOrigin(LowWall);
		CoverQuery query = Rifleman(new Vector3(0f, 0f, -6f), new Vector3(0f, 0f, 20f));
		Assert.True(board.TryFindSpot(query, default, out CoverSpot spot));

		Assert.True(board.Protects(spot.Position + new Vector3(0f, 0.2f, 0f), query));
		Assert.True(board.Protects(spot.Position + new Vector3(0f, 1.5f, 0f), query));
	}

	[Fact]
	public void TheThreatsSideOfTheWallIsNoCover()
	{
		var board = WallAtOrigin(LowWall);
		CoverQuery query = Rifleman(new Vector3(0f, 0f, -3f), new Vector3(0f, 0f, 20f));
		Assert.False(board.Protects(new Vector3(0f, 0f, 1f), query));
		Assert.False(board.Protects(new Vector3(0f, 0f, -8f), query));
	}

	[Fact]
	public void TheThreatOnTheOtherSideMovesTheSpotRound()
	{
		var board = WallAtOrigin(LowWall);
		CoverQuery query = Rifleman(new Vector3(0f, 0f, 6f), new Vector3(0f, 0f, -20f));
		Assert.True(board.TryFindSpot(query, default, out CoverSpot spot));
		Assert.True(spot.Position.Z > 0f);
	}

	[Fact]
	public void TheNearestSpotIsTakenAndTheWalkDecidesWhichSlot()
	{
		var board = WallAtOrigin(LowWall);
		CoverQuery left = Rifleman(new Vector3(-3f, 0f, -4f), new Vector3(0f, 0f, 20f));
		CoverQuery right = Rifleman(new Vector3(3f, 0f, -4f), new Vector3(0f, 0f, 20f));
		Assert.True(board.TryFindSpot(left, default, out CoverSpot a));
		Assert.True(board.TryFindSpot(right, default, out CoverSpot b));
		Assert.True(a.Position.X < 0f);
		Assert.True(b.Position.X > 0f);
	}

	[Fact]
	public void AClaimedSpotIsLeftForWhoeverClaimedIt()
	{
		var board = WallAtOrigin(LowWall);
		CoverQuery query = Rifleman(new Vector3(0f, 0f, -4f), new Vector3(0f, 0f, 20f));
		Assert.True(board.TryFindSpot(query, default, out CoverSpot first));

		ReadOnlySpan<Vector3> claimed = stackalloc Vector3[] { first.Position };
		Assert.True(board.TryFindSpot(query, claimed, out CoverSpot second));
		Assert.True(Flat(first.Position, second.Position) >= CoverBoard.ClaimMeters);
	}

	[Fact]
	public void AFourMetreWallHidesThreeBodiesFromOneThreat()
	{
		var board = WallAtOrigin(LowWall);
		CoverQuery query = Rifleman(new Vector3(0f, 0f, -4f), new Vector3(0f, 0f, 20f));
		Span<CoverSpot> spots = stackalloc CoverSpot[8];

		int found = board.FindSpots(query, default, spots);
		Assert.Equal(3, found);
		for (int i = 0; i < found; i++)
		{
			Assert.True(board.Protects(spots[i].Position, query));
			for (int j = 0; j < i; j++)
			{
				Assert.True(Flat(spots[i].Position, spots[j].Position) >= CoverBoard.ClaimMeters);
			}
		}
	}

	[Fact]
	public void NothingIsFoundBeyondTheSearch()
	{
		var board = WallAtOrigin(LowWall);
		CoverQuery query = Rifleman(new Vector3(0f, 0f, -20f), new Vector3(0f, 0f, 20f));
		query.SearchMeters = 10f;
		Assert.False(board.TryFindSpot(query, default, out _));
		query.SearchMeters = 20f;
		Assert.True(board.TryFindSpot(query, default, out _));
	}

	[Fact]
	public void ASpotOutsideTheFightsRangeIsNotTaken()
	{
		var board = WallAtOrigin(LowWall);
		CoverQuery query = Rifleman(new Vector3(0f, 0f, -4f), new Vector3(0f, 0f, 70f));
		Assert.False(board.TryFindSpot(query, default, out _));

		query.Threat = new Vector3(0f, 0f, 4f);
		Assert.False(board.TryFindSpot(query, default, out _));

		query.MaxThreatMeters = 0f;
		query.Threat = new Vector3(0f, 0f, 70f);
		Assert.True(board.TryFindSpot(query, default, out _));
	}

	[Fact]
	public void ABlockNobodyFiresOverIsCoverOnlyForSomebodyWhoNeedNotFire()
	{
		var board = WallAtOrigin(Block);
		CoverQuery fighter = Rifleman(new Vector3(0f, 0f, -5f), new Vector3(0f, 0f, 20f));
		Assert.False(board.TryFindSpot(fighter, default, out _));

		CoverQuery builder = Rifleman(new Vector3(0f, 0f, -5f), new Vector3(0f, 0f, 20f), mustFire: false);
		Assert.True(board.TryFindSpot(builder, default, out CoverSpot spot));
		Assert.False(spot.Low);
		Assert.True(board.Protects(spot.Position, builder));
	}

	[Fact]
	public void AWallThatHidesAStandingTankIsNotOneATankFiresOver()
	{
		// A hull 2.4 m tall with its gun at 2.2 is hidden by nothing this low: the
		// middle of it is above the wall.
		var board = WallAtOrigin(LowWall);
		CoverQuery tank = Rifleman(new Vector3(0f, 0f, -5f), new Vector3(0f, 0f, 20f));
		tank.ProtectMeters = 1.2f;
		tank.EyeMeters = 2.2f;
		tank.BodyRadiusMeters = 1.6f;
		Assert.False(board.TryFindSpot(tank, default, out _));
	}

	[Fact]
	public void TheSeekerFiresOverItsWallAtAThreatOnTheGround()
	{
		var board = WallAtOrigin(LowWall);
		CoverQuery query = Rifleman(new Vector3(0f, 0f, -4f), new Vector3(0f, 0f, 30f));
		Assert.True(board.TryFindSpot(query, default, out CoverSpot spot));

		// The eye clears the top; the chest does not.
		Vector3 threatChest = query.Threat + (Vector3.Up * query.ThreatAimMeters);
		Assert.False(board.Occludes(0, spot.Position + (Vector3.Up * query.EyeMeters), threatChest));
		Assert.True(board.Occludes(0, spot.Position + (Vector3.Up * query.ProtectMeters), threatChest));
	}

	[Fact]
	public void ASpotInsideAnotherBoxIsNotOffered()
	{
		var board = WallAtOrigin(LowWall);

		// A block right up against the back of the wall: the only face turned away
		// from the threat is buried in it.
		board.AddLevel(new Vector3(0f, 1.4f, -1.9f), 0f, new Vector3(6f, 2.8f, 2.2f));
		CoverQuery query = Rifleman(new Vector3(0f, 0f, -6f), new Vector3(0f, 0f, 20f));
		if (board.TryFindSpot(query, default, out CoverSpot spot))
		{
			Assert.NotEqual(0, spot.Object);
			Assert.False(board.FootprintOf(0).Contains(spot.Position, query.BodyRadiusMeters));
			Assert.False(board.FootprintOf(1).Contains(spot.Position, query.BodyRadiusMeters));
		}
	}

	[Fact]
	public void AnAngledThreatStillGetsTheFaceTurnedAwayFromIt()
	{
		var board = WallAtOrigin(LowWall);
		CoverQuery query = Rifleman(new Vector3(-2f, 0f, -4f), new Vector3(15f, 0f, 15f));
		Assert.True(board.TryFindSpot(query, default, out CoverSpot spot));
		Assert.True(board.Protects(spot.Position, query));
		Assert.True(spot.Position.Z < 0f || spot.Position.X < -2f);
	}

	[Fact]
	public void ASandbagWallIsCoverLikeAnyOther()
	{
		var board = new CoverBoard();
		board.SetStructure(2, new Vector3(0f, 0f, 0f), 0f, new Vector3(4f, 1.1f, 0.8f));
		CoverQuery query = Rifleman(new Vector3(0f, 0f, -4f), new Vector3(0f, 0f, 25f));
		Assert.True(board.TryFindSpot(query, default, out CoverSpot spot));
		Assert.Equal(CoverBoard.StructureIndex(2), spot.Object);
		Assert.True(board.Protects(spot.Position, query));
	}

	[Fact]
	public void TheSameQueryFindsTheSameSpot()
	{
		var board = new CoverBoard();
		for (int i = 0; i < 12; i++)
		{
			board.AddLevel(new Vector3((i % 4) * 6f, 0.6f, (i / 4) * 6f), i * 0.4f, LowWall);
		}

		CoverQuery query = Rifleman(new Vector3(9f, 0f, -3f), new Vector3(9f, 0f, 40f));
		Assert.True(board.TryFindSpot(query, default, out CoverSpot a));
		Assert.True(board.TryFindSpot(query, default, out CoverSpot b));
		Assert.Equal(a.Position, b.Position);
		Assert.Equal(a.Object, b.Object);
	}

	[Fact]
	public void AskingCostsNothing()
	{
		var board = new CoverBoard();
		for (int i = 0; i < 40; i++)
		{
			board.AddLevel(new Vector3((i % 8) * 7f, 0.6f, (i / 8) * 7f), i * 0.3f, i % 3 == 0 ? Block : LowWall);
		}

		for (int s = 0; s < SimConfig.MaxStructures; s++)
		{
			board.SetStructure(s, new Vector3(s * 3f, 0f, -20f), 0f, new Vector3(4f, 1.1f, 0.8f));
		}

		Span<Vector3> claimed = stackalloc Vector3[16];
		Span<CoverSpot> spots = stackalloc CoverSpot[4];
		Run(claimed, spots);
		long before = GC.GetAllocatedBytesForCurrentThread();
		Run(claimed, spots);
		Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);

		void Run(Span<Vector3> claims, Span<CoverSpot> found)
		{
			for (int i = 0; i < 200; i++)
			{
				CoverQuery query = Rifleman(new Vector3(i % 50, 0f, (i % 30) - 10f), new Vector3(20f, 0f, 60f));
				board.TryFindSpot(query, claims, out _);
				board.FindSpots(query, claims, found);
				board.Protects(query.From, query);
			}
		}
	}

	// ---- exposure ----------------------------------------------------------------

	[Fact]
	public void AStandingHeadShowsOverASandbagWallAndACrouchedOneDoesNot()
	{
		// sandbag_cover.json's numbers: a 1.1 m wall 1.4 m in front of the player, a
		// rifleman's eye at 1.5 m 28 m off. A crouched eye sits at about 1.05 m: its
		// crown may clear the wall, the head under it does not.
		var board = new CoverBoard();
		board.SetStructure(0, new Vector3(0f, 0f, 1.4f), 0f, new Vector3(4f, 1.1f, 0.8f));
		var shooter = new Vector3(0f, 1.5f, 30f);

		Vector3 standing = new(0f, 1.5f, 0f);
		Assert.False(board.Occludes(CoverBoard.StructureIndex(0), shooter, Exposure.Below(standing)));

		Vector3 crouched = new(0f, 1.05f, 0f);
		Assert.True(board.Occludes(CoverBoard.StructureIndex(0), shooter, Exposure.Below(crouched)));
	}

	// ---- bands -------------------------------------------------------------------

	[Fact]
	public void InCoverIsEnteredAtTheSpotAndHeldUntilShovedClearOfIt()
	{
		Assert.Equal(CoverState.None, CoverBands.Band(false, 0f, true, CoverState.In));
		Assert.Equal(CoverState.Near, CoverBands.Band(true, 5f, false, CoverState.None));
		Assert.Equal(CoverState.Near, CoverBands.Band(true, 1.3f, true, CoverState.Near));
		Assert.Equal(CoverState.In, CoverBands.Band(true, 0.5f, true, CoverState.Near));
		Assert.Equal(CoverState.In, CoverBands.Band(true, 1.3f, true, CoverState.In));
		Assert.Equal(CoverState.Near, CoverBands.Band(true, 2f, true, CoverState.In));

		// A box that no longer hides it — the threat walked round — is not cover.
		Assert.Equal(CoverState.Near, CoverBands.Band(true, 0.2f, false, CoverState.In));
	}

	private static float Flat(Vector3 a, Vector3 b) => new Vector2(a.X - b.X, a.Z - b.Z).Length();
}
