using System;
using System.Collections.Generic;
using System.Linq;
using Gdpyr.Sim;
using Gdpyr.Sim.Level;
using Godot;
using Xunit;

namespace Gdpyr.Tests;

/// <summary>
/// Bridges (docs/LEVEL_EDITOR.md §6.7, LE6): a section put on a free connector takes the pose
/// that puts one of its own on it, the same point and the opposite heading, by either end; one
/// with no connector near floats where it is put; supports stand a pier under every end not
/// joined to a section with one there, from the deck's underside to half a metre under the
/// lowest ground beneath it; an end near the ground is landed; a road's end is a connector like
/// any other, and a road joined to a section is pinned to its deck.
/// </summary>
public sealed class BridgeRulesTests
{
	/// <summary>Level ground at 0 m and a river across x = 0: its bed at −8 m for |x| ≤ 10, its banks at 45° to |x| = 18.</summary>
	private static MapDocument River()
	{
		MapDocument document = LevelMaps.Empty();
		HeightField field = document.Terrain;
		for (int z = 0; z < field.Depth; z++)
		{
			for (int x = 0; x < field.Width; x++)
			{
				field[x, z] = -Math.Clamp(18f - MathF.Abs(field.WorldX(x)), 0f, 8f);
			}
		}

		field.Round(field.Bounds);
		return document;
	}

	private const float East = MathF.PI / 2f;

	private static PlacementRequest Span(float x, float z, float yaw, float height, bool supports) =>
		BridgeRules.Float(PrefabSpecs.BridgeSpan, x, z, yaw, height, supports);

	private static void Near(Vector3 expected, Vector3 actual, float within = 0.01f) =>
		Assert.True((expected - actual).Length() <= within, $"{actual} is not within {within} m of {expected}");

	// ---- connectors -----------------------------------------------------------------------------------

	[Fact]
	public void ASection_HasAConnectorAtEachEnd_AtTheDecksTop_HeadingOut()
	{
		MapDocument document = LevelMaps.Empty();
		document.Objects.Add(MapObject.At(PrefabSpecs.BridgeRamp, new Vector3(10f, 2f, -5f), East));
		List<Connector> connectors = BridgeRules.Connectors(document);
		Assert.Equal(2, connectors.Count);
		Near(new Vector3(4f, 2f, -5f), connectors[0].Point);
		Near(new Vector3(16f, 3.5f, -5f), connectors[1].Point);
		Assert.Equal(-1f, connectors[0].Direction.X, 3);
		Assert.Equal(1f, connectors[1].Direction.X, 3);
		Assert.Equal(2, BridgeRules.Free(document).Count);
	}

	[Theory]
	[InlineData(PrefabSpecs.BridgeSpan, 0, 0f)]
	[InlineData(PrefabSpecs.BridgeSpan, 1, 0f)]
	[InlineData(PrefabSpecs.BridgeRamp, 0, 1.5f)]
	[InlineData(PrefabSpecs.BridgeRamp, 1, -1.5f)]
	public void ASectionPutOnAConnector_SharesItsPoint_HeadsTheOtherWay_AndRisesAsItsEndSays(ushort id, int which,
		float rise)
	{
		MapDocument document = LevelMaps.Empty();
		document.Objects.Add(MapObject.At(PrefabSpecs.BridgeSpan, new Vector3(0f, 3f, 0f), 0.4f));
		Connector target = BridgeRules.Connectors(document)[1];
		PlacementRequest request = BridgeRules.Attach(id, which, target, false);
		MapObject placed = MapObject.At(id, new Vector3(request.X, request.Height, request.Z), request.Yaw);
		Vector3 joined = BridgeRules.EndPoint(placed, which);
		Vector3 far = BridgeRules.EndPoint(placed, 1 - which);
		Near(target.Point, joined);
		Assert.Equal(rise, far.Y - joined.Y, 2);

		// Its connector at the joint heads back into the section it joins: the two are one joint, and neither is free.
		document.Objects.Add(placed);
		List<Connector> all = BridgeRules.Connectors(document);
		Connector mine = all.Single(c => c.Object == 1 && c.End == which);
		Assert.Equal(-target.Direction.X, mine.Direction.X, 3);
		Assert.Equal(-target.Direction.Y, mine.Direction.Y, 3);
		Assert.True(BridgeRules.IsJoined(mine, all));
		Assert.Equal(2, BridgeRules.Free(document).Count);
		Assert.DoesNotContain(MapValidator.Validate(document).Issues,
			i => i.Code == MapIssueCode.OpenBridgeEnd && (i.Where - joined).Length() < 0.5f);
	}

	[Fact]
	public void TwoSectionsEndsAtOnePointInPlan_ButAMetreApartInHeight_AreNotJoined()
	{
		MapDocument document = LevelMaps.Empty();
		document.Objects.Add(MapObject.At(PrefabSpecs.BridgeSpan, new Vector3(0f, 3f, 0f), East));
		document.Objects.Add(MapObject.At(PrefabSpecs.BridgeSpan, new Vector3(12f, 4f, 0f), East));
		Assert.Equal(4, BridgeRules.Free(document).Count);
		document.Objects[1] = MapObject.At(PrefabSpecs.BridgeSpan, new Vector3(12f, 3.05f, 0f), East);
		Assert.Equal(2, BridgeRules.Free(document).Count);
	}

	[Fact]
	public void ASectionsJoinedEnd_IsNotLanded_ThoughItIsNearTheGround()
	{
		// A span a metre over level ground lands both its ends; one put on its free end lands only its own other end.
		MapDocument document = LevelMaps.Empty();
		var session = new MapEditSession(document);
		SectionCheck first = session.PlaceSection(Span(0f, 0f, East, 1f, false), -1, out _);
		Assert.Equal(2, first.Landed);
		Connector end = BridgeRules.Free(document).Single(c => c.End == 1);
		SectionCheck second = session.PlaceSection(BridgeRules.Attach(PrefabSpecs.BridgeSpan, 0, end, false), -1, out _, end);
		Assert.True(second.IsOk);
		Assert.Equal(1, second.Landed);
	}

	[Fact]
	public void ACursorWithin3MetresOfAFreeConnector_SnapsToIt_AndOneFurtherFloats()
	{
		MapDocument document = LevelMaps.Empty();
		document.Objects.Add(MapObject.At(PrefabSpecs.BridgeSpan, new Vector3(0f, 3f, 0f), East));
		PlacementRequest snapped = BridgeRules.Snap(document, PrefabSpecs.BridgeSpan, false, 8.5f, 1.5f, 1f, 9f, false, -1,
			out Connector? target);
		Assert.NotNull(target);
		Assert.Equal(12f, snapped.X, 2);
		Assert.Equal(0f, snapped.Z, 2);
		Assert.Equal(3f, snapped.Height, 2);
		Assert.Equal(East, snapped.Yaw, 3);

		PlacementRequest floating = BridgeRules.Snap(document, PrefabSpecs.BridgeSpan, false, 10f, 1.5f, 1f, 9f, false, -1,
			out target);
		Assert.Null(target);
		Assert.Equal(10f, floating.X);
		Assert.Equal(9f, floating.Height);
		Assert.Equal(1f, floating.Yaw);
	}

	// ---- three spans over a river -------------------------------------------------------------------------

	/// <summary>Three spans with supports, each put on the last's free end as the bridge tool puts them: 36 m from bank to bank.</summary>
	private static (MapEditSession Session, List<SectionCheck> Checks) ThreeSpans(MapDocument document)
	{
		var session = new MapEditSession(document);
		var checks = new List<SectionCheck>
		{
			session.PlaceSection(Span(-12f, 0f, East, 0f, true), -1, out _),
		};
		for (int k = 1; k < 3; k++)
		{
			PlacementRequest next = BridgeRules.Snap(document, PrefabSpecs.BridgeSpan, false, -18f + (12f * k), 0.5f, 0f, 0f, true, -1,
				out Connector? target);
			checks.Add(session.PlaceSection(next, -1, out _, target));
		}

		return (session, checks);
	}

	[Fact]
	public void ThreeSpansOverARiver_StandOnFourPiers_TheirEndsLandedOnTheBanks_WithNoEndOpen()
	{
		MapDocument document = River();
		(MapEditSession session, List<SectionCheck> checks) = ThreeSpans(document);
		Assert.All(checks, c => Assert.True(c.IsOk, c.Result.ToString()));
		Assert.Equal(new[] { 2, 1, 1 }, checks.Select(c => c.Piers));
		Assert.Equal(new[] { 1, 0, 1 }, checks.Select(c => c.Landed));
		Assert.Null(checks[0].Target);
		Assert.NotNull(checks[1].Target);

		HeightField field = document.Terrain;
		for (int i = 0; i < 3; i++)
		{
			MapObject section = document.Objects[i];
			Near(new Vector3(-12f + (12f * i), 0f, 0f), section.Position);
			for (int end = 0; end < 2; end++)
			{
				int length = section.Parameter(end == 0 ? PrefabSpecs.PierBackKey : PrefabSpecs.PierFrontKey, 0);
				if (length == 0)
				{
					continue;
				}

				// Its top at the deck's underside; its foot at least half a metre under the lowest ground beneath it, by no more than a centimetre.
				float top = BridgeRules.EndPoint(section, end).Y - PrefabSpecs.DeckThicknessMeters;
				float foot = top - (length / 100f);
				float lowest = BridgeRules.LowestUnder(field, BridgeRules.PierFootprint(section, end));
				Assert.InRange(lowest - foot, BridgeRules.PierFootMeters, BridgeRules.PierFootMeters + 0.0101f);
			}
		}

		// No pier at the two joints the first and second spans stood theirs on.
		Assert.Equal(new[] { true, true, false, true, false, true },
			document.Objects.SelectMany(o => new[] { o.Parameter(PrefabSpecs.PierBackKey, 0) > 0, o.Parameter(PrefabSpecs.PierFrontKey, 0) > 0 }));

		// The banks meet the decks flush; a bank steeper than the skirt was cut back to it by the landing,
		// and the river's middle is as deep as it was.
		Assert.Equal(0f, field.Sample(-18f, 0f), 2);
		Assert.Equal(0f, field.Sample(18f, 0f), 2);
		Assert.Equal(-8f, field.Sample(0f, 0f));
		Assert.True(field.Sample(-12f, 0f) > -8f + 0.5f, "the bank under the first span is the landing's skirt");
		MapReport report = MapValidator.Validate(document);
		Assert.False(report.Has(MapIssueCode.OpenBridgeEnd));
		Assert.False(report.Has(MapIssueCode.InTheWay));
		Assert.Equal(3, session.History.UndoCount);
	}

	[Fact]
	public void SupportsOff_StandsNoPier_AndAnOpenEndIsWarnedOf()
	{
		MapDocument document = River();
		var session = new MapEditSession(document);
		SectionCheck check = session.PlaceSection(Span(-12f, 0f, East, 0f, false), -1, out _);
		Assert.True(check.IsOk);
		Assert.Equal(0, check.Piers);
		Assert.Empty(document.Objects[0].Parameters.ToArray());
		Assert.True(MapValidator.Validate(document).TryGet(MapIssueCode.OpenBridgeEnd, out MapIssue issue));
		Near(new Vector3(-6f, 0f, 0f), issue.Where);
	}

	[Fact]
	public void APierInAnotherObjectsBox_IsInTheWay_AndSupportsOffIsTheAnswer()
	{
		// A span 4 m over level ground, a cover block under its +Z end; the block is 3 m tall and the deck's underside at 3.4 m.
		MapDocument document = LevelMaps.Empty();
		var session = new MapEditSession(document);
		Assert.True(session.Place(new PlacementRequest(PrefabSpecs.CoverBlock, 0f, 5.4f, 0f), out _).IsOk);
		SectionCheck with = session.CheckSection(Span(0f, 0f, 0f, 4f, true));
		Assert.Equal(PrefabPlacementResult.InTheWay, with.Result);
		Assert.Equal(0, with.Placement.Other);
		Assert.Equal(2, with.Piers);
		SectionCheck without = session.CheckSection(Span(0f, 0f, 0f, 4f, false));
		Assert.True(without.IsOk, without.Result.ToString());
		Assert.Equal(0, without.Landed);
	}

	[Fact]
	public void APierLongerThanAFileHolds_IsOffTheMap()
	{
		MapDocument document = LevelMaps.Empty();
		HeightField field = document.Terrain;
		Array.Fill(field.Heights, MapLimits.MinHeightMeters);
		var session = new MapEditSession(document);
		SectionCheck ramp = session.CheckSection(BridgeRules.Float(PrefabSpecs.BridgeRamp, 0f, 0f, 0f, MapLimits.MaxHeightMeters, true));
		Assert.Equal(PrefabPlacementResult.OutOfBounds, ramp.Result);
		SectionCheck span = session.CheckSection(Span(0f, 0f, 0f, MapLimits.MaxHeightMeters, true));
		Assert.True(span.IsOk, span.Result.ToString());
		Assert.Equal(7990, span.Placement.Object.Parameter(PrefabSpecs.PierFrontKey, 0));
	}

	[Fact]
	public void AnEndNearTheGround_IsLandedFlush_AndOneFarFromItIsNot()
	{
		MapDocument document = River();
		var session = new MapEditSession(document);

		// Its −Z end 2 m over the bank's top, its +Z end 10 m over the bed.
		SectionCheck check = session.PlaceSection(Span(-12f, 0f, East, 2f, false), -1, out EditOutcome outcome);
		Assert.True(check.IsOk);
		Assert.Equal(1, check.Landed);
		Assert.False(outcome.Changed.IsEmpty);

		HeightField field = document.Terrain;
		Footprint landing = BridgeRules.LandingFootprint(new Connector(new Vector3(-18f, 2f, 0f), 3f * MathF.PI / 2f, 0, -1, 0));
		for (int z = 0; z < field.Depth; z++)
		{
			for (int x = 0; x < field.Width; x++)
			{
				if (landing.Contains(new Vector3(field.WorldX(x), 0f, field.WorldZ(z))))
				{
					Assert.Equal(2f, field[x, z]);
				}
			}
		}

		Assert.Equal(2f, field.Sample(-18f, 0f), 2);
		Assert.Equal(2f, field.Sample(-22f, 0f), 2);
		Assert.True(field.Sample(-6f, 0f) < 2f - BridgeRules.LandingReachMeters, "the far end was not landed");
		Assert.True(HeightField.Degrees(field.SteepestFace(new SampleRect(80, 100, 101, 140))) < MapLimits.WalkableSlopeDegrees);
		Assert.True(MapValidator.Validate(document).Issues.Count(i => i.Code == MapIssueCode.OpenBridgeEnd) == 1);
	}

	[Fact]
	public void ALandingThatWouldMoveAPad_DisturbsItsNeighbour()
	{
		MapDocument document = River();
		var session = new MapEditSession(document);
		Assert.True(session.Place(new PlacementRequest(PrefabSpecs.WeaponLocker, -24f, 0f, 0f), out _).IsOk);
		SectionCheck check = session.CheckSection(Span(-12f, 0f, East, 2f, false));
		Assert.Equal(PrefabPlacementResult.DisturbsNeighbour, check.Result);
		Assert.Equal(0, check.Placement.Other);
	}

	[Fact]
	public void TakingUpTheSectionThatHeldAJointsPier_GivesItsNeighbourOne_InTheSameStep()
	{
		MapDocument document = River();
		(MapEditSession session, _) = ThreeSpans(document);
		List<MapObject> before = document.Objects.ToList();
		Assert.Equal(0, document.Objects[1].Parameter(PrefabSpecs.PierBackKey, 0));

		Assert.True(session.TakeUp(0, out EditOutcome outcome));
		Assert.True(outcome.ObjectsChanged);
		Assert.Equal(2, document.Objects.Count);
		MapObject neighbour = document.Objects[0];
		Assert.Equal(before[1].Position, neighbour.Position);
		Assert.Equal(BridgeRules.PierCentimeters(document.Terrain, neighbour, 0), neighbour.Parameter(PrefabSpecs.PierBackKey, 0));
		Assert.True(neighbour.Parameter(PrefabSpecs.PierBackKey, 0) > 700);
		Assert.Equal(before[1].Parameter(PrefabSpecs.PierFrontKey, 0), neighbour.Parameter(PrefabSpecs.PierFrontKey, 0));

		Assert.True(session.Undo(out _));
		Assert.Equal(before, document.Objects);
	}

	// ---- roads and bridges ---------------------------------------------------------------------------------

	[Fact]
	public void ARoadDrawnFromADecksEnd_IsPinnedToIt_AndASectionPutOnARoadsEndTakesItsHeightAndHeading()
	{
		MapDocument document = River();
		var session = new MapEditSession(document);
		Assert.True(session.PlaceSection(Span(-12f, 0f, East, 1.5f, true), -1, out _).IsOk);

		// From the section's −Z end, west and down the bank's level ground at 10 %.
		MapRoad road = RoadPath.Record(new[] { new Vector2(-18f, 0f), new Vector2(-70f, 0f) }, 6f, 0.10f);
		RoadCheck check = session.ApplyRoad(road, -1, out _);
		Assert.True(check.IsOk, check.Result.ToString());
		Assert.Equal(0, check.StartJoin?.Object);
		Assert.Null(check.EndJoin);
		Assert.Equal(1.5f, check.Path.Profile[0]);
		HeightField field = document.Terrain;
		Assert.Equal(1.5f, field.Sample(-18.25f, 0f), 1);
		Assert.True(MathF.Abs(field.Sample(-18.25f, 0f) - 1.5f) <= 0.03f);

		// The road's far end is a connector: a section put there joins it at the ground's height, heading on west.
		Assert.Contains(BridgeRules.Free(document), c => c.Road == 0 && c.End == 1);
		PlacementRequest onRoad = BridgeRules.Snap(document, PrefabSpecs.BridgeSpan, false, -69f, 1f, 0f, 0f, false, -1,
			out Connector? target);
		Assert.Equal(0, target?.Road);
		var section = MapObject.At(PrefabSpecs.BridgeSpan, new Vector3(onRoad.X, onRoad.Height, onRoad.Z), onRoad.Yaw);
		Near(new Vector3(-70f, field.Sample(-70f, 0f), 0f), BridgeRules.EndPoint(section, 0), 0.02f);
		Assert.Equal(-1f, BridgeRules.EndPoint(section, 1).X - BridgeRules.EndPoint(section, 0).X > 0f ? 1f : -1f);

		// The section's end the road starts at is joined, not open.
		Assert.DoesNotContain(MapValidator.Validate(document).Issues,
			i => i.Code == MapIssueCode.OpenBridgeEnd && (i.Where - new Vector3(-18f, 1.5f, 0f)).Length() < 0.5f);
	}

	[Fact]
	public void TwoEndsOfARoadJoinedToDecksTooFarApartInHeight_AreTooSteepToJoin()
	{
		MapDocument document = LevelMaps.Empty();
		document.Objects.Add(MapObject.At(PrefabSpecs.BridgeSpan, new Vector3(-30f, 0f, 0f), East));
		document.Objects.Add(MapObject.At(PrefabSpecs.BridgeSpan, new Vector3(30f, 5f, 0f), East));
		RoadCheck check = RoadPath.Check(document, RoadPath.Record(new[] { new Vector2(-24f, 0f), new Vector2(24f, 0f) }, 6f, 0.10f));
		Assert.Equal(RoadResult.TooSteepToJoin, check.Result);
		Assert.Equal(5f / 48f, check.Path.JoinGrade, 3);
	}
}
public sealed class BridgePierKeepingTests
{
	[Fact]
	public void APierStoodOnGroundAStrokeMovedAfterwards_IsGivenItsLengthAgain_InTheStrokesStep()
	{
		// A span 6 m over level ground, on two piers; a mound raised under its +Z end.
		MapDocument document = LevelMaps.Empty();
		var session = new MapEditSession(document);
		Assert.True(session.PlaceSection(BridgeRules.Float(PrefabSpecs.BridgeSpan, 0f, 0f, 0f, 6f, true), -1, out _).IsOk);
		MapObject before = document.Objects[0];
		Assert.Equal(590, before.Parameter(PrefabSpecs.PierFrontKey, 0));

		session.Brush = TerrainBrush.Presets[1].Settings;
		Assert.True(session.BeginStroke(0f, 5.4f, false));
		for (int frame = 0; frame < 30; frame++)
		{
			session.StrokeTo(0f, 5.4f, MapEditSession.DabSeconds);
		}

		EditOutcome stroke = session.EndStroke();
		Assert.True(stroke.Step && stroke.ObjectsChanged);
		MapObject after = document.Objects[0];
		int front = after.Parameter(PrefabSpecs.PierFrontKey, 0);
		Assert.True(front < 590, $"{front}");
		Assert.Equal(BridgeRules.PierCentimeters(document.Terrain, after, 1), front);
		Assert.Equal(BridgeRules.PierCentimeters(document.Terrain, after, 0), after.Parameter(PrefabSpecs.PierBackKey, 0));

		// One step: undone, the ground and the pier both as they were.
		Assert.True(session.Undo(out EditOutcome undone) && undone.ObjectsChanged);
		Assert.Equal(before, document.Objects[0]);
		Assert.Equal(0f, document.Terrain.Sample(0f, 5.4f));
	}

	[Fact]
	public void APierUnderAPadPlacedBesideIt_IsGivenItsLengthAgain_InThePlacementsStep()
	{
		// A span 6 m over a slope; a weapon locker levelled beside its +Z pier cuts and fills under it.
		MapDocument document = LevelMaps.Empty();
		HeightField field = document.Terrain;
		for (int z = 0; z < field.Depth; z++)
		{
			for (int x = 0; x < field.Width; x++)
			{
				field[x, z] = 0.1f * field.WorldX(x);
			}
		}

		field.Round(field.Bounds);
		var session = new MapEditSession(document);
		Assert.True(session.PlaceSection(BridgeRules.Float(PrefabSpecs.BridgeSpan, 0f, 0f, 0f, 6f, true), -1, out _).IsOk);
		PlacementCheck locker = session.Place(new PlacementRequest(PrefabSpecs.WeaponLocker, 2.6f, 5.4f, 0f), out EditOutcome outcome);
		Assert.True(locker.IsOk, locker.Result.ToString());
		Assert.True(outcome.ObjectsChanged);
		MapObject section = document.Objects[0];
		Assert.Equal(BridgeRules.PierCentimeters(document.Terrain, section, 1), section.Parameter(PrefabSpecs.PierFrontKey, 0));
}

	[Fact]
	public void ASectionTurnedWhereItIs_HasItsPiersWorkedOutAgain()
	{
		// A span on piers over a slope, turned a quarter where it is: its piers stand on other ground now.
		MapDocument document = LevelMaps.Empty();
		HeightField field = document.Terrain;
		for (int z = 0; z < field.Depth; z++)
		{
			for (int x = 0; x < field.Width; x++)
			{
				field[x, z] = 0.2f * field.WorldX(x);
			}
		}

		field.Round(field.Bounds);
		var session = new MapEditSession(document);
		Assert.True(session.PlaceSection(BridgeRules.Float(PrefabSpecs.BridgeSpan, 0f, 0f, 0f, 6f, true), -1, out _).IsOk);
		MapObject before = document.Objects[0];
		PlacementRequest turned = MapEditSession.RequestFor(before);
		turned.Yaw = MathF.PI / 2f;
		Assert.True(session.Replace(0, turned, out _).IsOk);
		MapObject after = document.Objects[0];
		for (int end = 0; end < 2; end++)
		{
			byte key = end == 0 ? PrefabSpecs.PierBackKey : PrefabSpecs.PierFrontKey;
			Assert.Equal(BridgeRules.PierCentimeters(field, after, end), after.Parameter(key, 0));
		}

		Assert.NotEqual(before.Parameter(PrefabSpecs.PierBackKey, 0), after.Parameter(PrefabSpecs.PierBackKey, 0));
	}
}
