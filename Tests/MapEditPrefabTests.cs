using System;
using System.Collections.Generic;
using Gdpyr.Sim;
using Gdpyr.Sim.Level;
using Godot;
using Xunit;

namespace Gdpyr.Tests;

/// <summary>
/// The editor's prefab commands (docs/LEVEL_EDITOR.md §8.4, LE5): a placement is one step
/// and a refusal none; an object turned or given other parameters is taken up and placed
/// again where it is in the file, not counted, in the way or a neighbour to itself; taking
/// one up leaves its ground; a click picks the object under it; and a map made by placing
/// alone plays.
/// </summary>
public class MapEditPrefabTests
{
	/// <summary>A new map tilted 3° along X, so that a pad has ground to level.</summary>
	private static MapDocument Tilted()
	{
		MapDocument document = LevelMaps.Empty();
		HeightField field = document.Terrain;
		float grade = HeightField.Grade(3f);
		for (int z = 0; z < field.Depth; z++)
		{
			for (int x = 0; x < field.Width; x++)
			{
				field[x, z] = field.WorldX(x) * grade;
			}
		}

		field.Round(field.Bounds);
		return document;
	}

	private static PlacementRequest At(ushort id, float x, float z, float yawDegrees = 0f, MapParameter[] parameters = null) =>
		new(id, x, z, yawDegrees * (MathF.PI / 180f), 0f, parameters);

	[Fact]
	public void APlacement_IsOneStep_AndUndoTakesItAndItsPadAway()
	{
		var session = new MapEditSession(Tilted());
		short[] before = session.Document.Terrain.ToSamples();
		PlacementCheck check = session.Place(At(PrefabSpecs.Barracks, 10f, 20f, 20f), out EditOutcome outcome);
		Assert.True(check.IsOk, check.ToString());
		Assert.True(outcome.Step && outcome.ObjectsChanged && !outcome.Changed.IsEmpty);
		Assert.Single(session.Document.Objects);
		Assert.Equal(check.Object, session.Document.Objects[0]);
		Assert.Equal(1, session.History.UndoCount);
		Assert.True(session.Dirty);
		short[] after = session.Document.Terrain.ToSamples();
		Assert.NotEqual(before, after);
		Assert.True(MapValidator.Validate(session.Document).Issues.Count > 0);
		Assert.False(MapValidator.Validate(session.Document).Has(MapIssueCode.OffItsPad));

		Assert.True(session.Undo(out EditOutcome undone));
		Assert.True(undone.ObjectsChanged);
		Assert.Empty(session.Document.Objects);
		Assert.Equal(before, session.Document.Terrain.ToSamples());
		Assert.True(session.Redo(out _));
		Assert.Equal(check.Object, session.Document.Objects[0]);
		Assert.Equal(after, session.Document.Terrain.ToSamples());
	}

	[Fact]
	public void ARefusedPlacement_ChangesNothing_AndSaysWhy()
	{
		var session = new MapEditSession(Tilted());
		Assert.True(session.Place(At(PrefabSpecs.VehicleKiosk, 0f, 0f), out _).IsOk);
		short[] samples = session.Document.Terrain.ToSamples();
		int changes = session.Changes;

		PlacementCheck second = session.Place(At(PrefabSpecs.VehicleKiosk, 60f, 60f), out EditOutcome none);
		Assert.Equal(PrefabPlacementResult.LimitReached, second.Result);
		Assert.False(none.Step);
		PlacementCheck onTop = session.Place(At(PrefabSpecs.WeaponLocker, 2f, 0f), out _);
		Assert.Equal(PrefabPlacementResult.InTheWay, onTop.Result);
		Assert.Equal(0, onTop.Other);
		Assert.Equal(PrefabPlacementResult.UnknownPrefab, session.Place(At(999, 0f, 0f), out _).Result);

		Assert.Single(session.Document.Objects);
		Assert.Equal(samples, session.Document.Terrain.ToSamples());
		Assert.Equal(1, session.History.UndoCount);
		Assert.Equal(changes, session.Changes);
	}

	[Fact]
	public void TurningAnObject_KeepsItsPlaceInTheFile_AsOneStep()
	{
		var session = new MapEditSession(Tilted());
		Assert.True(session.Place(At(PrefabSpecs.WeaponLocker, -40f, 0f), out _).IsOk);
		Assert.True(session.Place(At(PrefabSpecs.Barracks, 0f, 40f), out _).IsOk);
		Assert.True(session.Place(At(PrefabSpecs.WeaponLocker, 40f, 0f), out _).IsOk);
		MapObject first = session.Document.Objects[0];
		short[] before = session.Document.Terrain.ToSamples();

		PlacementRequest turned = MapEditSession.RequestFor(first);
		turned.Yaw = MathF.PI / 12f;
		PlacementCheck check = session.Replace(0, turned, out EditOutcome outcome);
		Assert.True(check.IsOk, check.ToString());
		Assert.True(outcome.Step && outcome.ObjectsChanged);
		Assert.Equal(3, session.Document.Objects.Count);
		Assert.Equal(PrefabSpecs.WeaponLocker, session.Document.Objects[0].PrefabId);
		Assert.Equal(Quantize.YawToU16(MathF.PI / 12f), session.Document.Objects[0].Yaw);
		Assert.Equal(PrefabSpecs.Barracks, session.Document.Objects[1].PrefabId);
		Assert.Equal(4, session.History.UndoCount);
		Assert.False(MapValidator.Validate(session.Document).Has(MapIssueCode.OffItsPad));

		Assert.True(session.Undo(out _));
		Assert.Equal(first, session.Document.Objects[0]);
		Assert.Equal(before, session.Document.Terrain.ToSamples());
	}

	[Fact]
	public void AnObjectReplaced_IsNotCounted_InTheWay_OrANeighbourToItself()
	{
		var session = new MapEditSession(Tilted());
		Assert.True(session.Place(At(PrefabSpecs.VehicleKiosk, 0f, 0f), out _).IsOk);
		Assert.True(session.Place(At(PrefabSpecs.Barracks, 50f, 0f), out _).IsOk);

		// The one kiosk a map may hold, turned where it stands: its own boxes and pad are where the new ones go.
		PlacementRequest kiosk = MapEditSession.RequestFor(session.Document.Objects[0]);
		kiosk.Yaw = MathF.PI / 12f;
		Assert.True(session.Replace(0, kiosk, out _).IsOk);

		// Moved onto the barracks, it is refused and stays.
		MapObject stays = session.Document.Objects[0];
		kiosk.X = 50f;
		PlacementCheck refused = session.Replace(0, kiosk, out EditOutcome none);
		Assert.Equal(PrefabPlacementResult.InTheWay, refused.Result);
		Assert.Equal(1, refused.Other);
		Assert.False(none.Step);
		Assert.Equal(stays, session.Document.Objects[0]);
		Assert.Equal(PrefabPlacementResult.UnknownPrefab, session.Replace(7, kiosk, out _).Result);
	}

	/// <summary>
	/// Placed again as it stands, an object is where it was, on a pad at the same level — or it
	/// is refused, as §6.5 would refuse it placed afresh, by a neighbour placed after it whose
	/// level ground is inside its skirt. Never by one placed before it: that one's ground was
	/// inside its skirt's band when it was placed, and has not moved since.
	/// </summary>
	[Theory]
	[InlineData(1)]
	[InlineData(2)]
	[InlineData(3)]
	[InlineData(4)]
	public void AnObjectPlacedAgainAsItStands_IsWhereItWas_OrRefusedByALaterNeighbour(int seed)
	{
		MapDocument document = LevelMaps.Generate(seed);
		int taken = 0;
		for (int i = 0; i < document.Objects.Count; i++)
		{
			MapObject placed = document.Objects[i];
			PlacementCheck check = PrefabPlacement.Check(document, MapEditSession.RequestFor(placed), i);
			if (!check.IsOk)
			{
				Assert.True(check.Result == PrefabPlacementResult.DisturbsNeighbour && check.Other > i,
					$"seed {seed}, object {i} ({placed}): {check}, object {check.Other}");
				continue;
			}

			taken++;
			Assert.Equal(placed.PrefabId, check.Object.PrefabId);
			Assert.Equal(placed.X, check.Object.X);
			Assert.Equal(placed.Z, check.Object.Z);
			Assert.Equal(placed.Yaw, check.Object.Yaw);
			Assert.Equal(PrefabPlacement.StandingHeight(placed), PrefabPlacement.StandingHeight(check.Object));
		}

		Assert.True(taken >= document.Objects.Count - 2, $"seed {seed}: {taken} of {document.Objects.Count}");
	}

	[Fact]
	public void AMapAtItsLimitOfObjects_TakesNoMore_AndStillTurnsOne()
	{
		// 512 lockers, 4 m apart: none in another's way, each on its own level pad of flat ground.
		MapDocument document = LevelMaps.Empty();
		for (int i = 0; i < MapLimits.MaxObjects; i++)
		{
			document.Objects.Add(MapObject.At(PrefabSpecs.WeaponLocker, new Vector3(-44f + (4f * (i % 23)), 0f, -44f + (4f * (i / 23))), 0f));
		}

		var session = new MapEditSession(document);
		Assert.Equal(PrefabPlacementResult.LimitReached, session.Place(At(PrefabSpecs.WeaponLocker, 80f, 80f), out _).Result);
		PlacementRequest turned = MapEditSession.RequestFor(document.Objects[0]);
		turned.Yaw = MathF.PI / 2f;
		Assert.True(session.Replace(0, turned, out _).IsOk);
		Assert.Equal(MapLimits.MaxObjects, document.Objects.Count);
	}

	[Fact]
	public void TakingUp_LeavesTheGround_AndUndoPutsItBackWhereItWas()
	{
		var session = new MapEditSession(Tilted());
		Assert.True(session.Place(At(PrefabSpecs.WeaponLocker, -30f, 0f), out _).IsOk);
		Assert.True(session.Place(At(PrefabSpecs.CoverBlock, 30f, 0f), out _).IsOk);
		MapObject locker = session.Document.Objects[0];
		short[] ground = session.Document.Terrain.ToSamples();

		Assert.True(session.TakeUp(0, out EditOutcome outcome));
		Assert.True(outcome.ObjectsChanged);
		Assert.Single(session.Document.Objects);
		Assert.Equal(PrefabSpecs.CoverBlock, session.Document.Objects[0].PrefabId);
		Assert.Equal(ground, session.Document.Terrain.ToSamples());
		Assert.False(session.TakeUp(5, out _));

		Assert.True(session.Undo(out _));
		Assert.Equal(locker, session.Document.Objects[0]);
		Assert.Equal(2, session.Document.Objects.Count);
	}

	[Fact]
	public void AClick_PicksTheSmallestThingUnderIt()
	{
		var session = new MapEditSession(Tilted());
		Assert.True(session.Place(At(PrefabSpecs.ResourceNode, 0f, 0f), out _).IsOk);
		Assert.True(session.Place(At(PrefabSpecs.CoverBlock, 5f, 0f), out _).IsOk);
		Assert.True(session.Place(At(PrefabSpecs.StrategistAnchor, -60f, 60f), out _).IsOk);
		Assert.True(session.Place(At(PrefabSpecs.Barracks, 50f, -50f, 30f), out _).IsOk);
		MapDocument document = session.Document;

		Assert.Equal(0, MapEditSession.Pick(document, 0.3f, 0.2f));
		Assert.Equal(0, MapEditSession.Pick(document, -7f, 3f));
		Assert.Equal(1, MapEditSession.Pick(document, 5.5f, -1f));
		Assert.Equal(2, MapEditSession.Pick(document, -61f, 61f));
		Assert.Equal(-1, MapEditSession.Pick(document, -63f, 60f));
		Assert.Equal(3, MapEditSession.Pick(document, 54f, -50f));
		Assert.Equal(-1, MapEditSession.Pick(document, -80f, -80f));
	}

	[Fact]
	public void AMapMadeByPlacingAlone_Plays()
	{
		var session = new MapEditSession(Tilted());
		var made = new (ushort Id, float X, float Z, float Yaw)[]
		{
			(PrefabSpecs.SpawnLine, -90f, 0f, 90f),
			(PrefabSpecs.WeaponLocker, -90f, 12f, 0f),
			(PrefabSpecs.Barracks, 80f, 60f, 0f),
			(PrefabSpecs.Barracks, 80f, -60f, 180f),
			(PrefabSpecs.ResourceNode, -30f, 50f, 0f),
			(PrefabSpecs.ResourceNode, 0f, 0f, 0f),
			(PrefabSpecs.ResourceNode, -30f, -50f, 0f),
			(PrefabSpecs.CoverBlock, -20f, 20f, 0f),
			(PrefabSpecs.CoverLowWall, -20f, -20f, 45f),
			(PrefabSpecs.StructurePillbox, -50f, 0f, 90f),
			(PrefabSpecs.StrategistAnchor, 90f, 0f, 0f),
		};

		foreach ((ushort id, float x, float z, float yaw) in made)
		{
			PlacementCheck check = session.Place(At(id, x, z, yaw), out _);
			Assert.True(check.IsOk, $"{PrefabSpecs.Get(id).Name} at ({x}, {z}): {check}");
		}

		MapReport report = MapValidator.Validate(session.Document);
		Assert.True(report.IsPlayable, string.Join("; ", report.Issues));
		Assert.Equal(made.Length, session.History.UndoCount);
		Assert.True(MapFile.TryWrite(session.Document, out byte[] file, out _));
		Assert.True(MapFile.TryRead(file, out MapDocument read, out _));
		Assert.True(MapValidator.Validate(read).IsPlayable);
	}

	[Fact]
	public void RequestFor_CarriesAnObjectsParameters()
	{
		var radius = new[] { new MapParameter(PrefabSpecs.NodeCaptureRadiusKey, 800) };
		var session = new MapEditSession(Tilted());
		Assert.True(session.Place(At(PrefabSpecs.ResourceNode, 0f, 0f, 0f, radius), out _).IsOk);
		PlacementRequest request = MapEditSession.RequestFor(session.Document.Objects[0]);
		Assert.Equal(radius, request.Parameters);

		request.Parameters = new[] { new MapParameter(PrefabSpecs.NodeCaptureRadiusKey, 1200) };
		Assert.True(session.Replace(0, request, out _).IsOk);
		Assert.Equal(1200, session.Document.Objects[0].Parameter(PrefabSpecs.NodeCaptureRadiusKey, 0));
		Assert.True(PrefabSpecs.TryGetPadArea(session.Document.Objects[0], out PadArea pad));
		Assert.Equal(12f, pad.Radius);
		Assert.False(MapValidator.Validate(session.Document).Has(MapIssueCode.OffItsPad));
		Assert.Empty(MapEditSession.RequestFor(new MapObject(PrefabSpecs.AmmoCan, 0, 0, 0, 0)).Parameters ?? Array.Empty<MapParameter>());
	}
}

/// <summary>
/// Reach (§6.9), the editor's deep check (§8.7, LE5): every barracks door and spawn line
/// is asked about every node, and each pair with no path is a warning naming both.
/// </summary>
public class MapReachTests
{
	[Fact]
	public void EveryStart_IsAskedAboutEveryNode()
	{
		MapDocument document = LevelMaps.Generate(1);
		int starts = document.CountOf(PrefabGroup.Barracks) + document.CountOf(PrefabGroup.SpawnLine);
		int nodes = document.CountOf(PrefabGroup.ResourceNode);
		var asked = new List<(Vector3, Vector3)>();
		var report = new MapReport();
		int pairs = MapValidator.Reach(document, (from, to) =>
		{
			asked.Add((from, to));
			return true;
		}, report);
		Assert.Equal(starts * nodes, pairs);
		Assert.Equal(pairs, asked.Count);
		Assert.Empty(report.Issues);

		foreach (MapObject placed in document.Objects)
		{
			if (placed.PrefabId == PrefabSpecs.ResourceNode)
			{
				Assert.Equal(starts, asked.FindAll(p => p.Item2.X == placed.Position.X && p.Item2.Z == placed.Position.Z
					&& p.Item2.Y == PrefabPlacement.StandingHeight(placed)).Count);
			}
			else if (PrefabSpecs.TryGetDoor(placed, out Vector3 door))
			{
				Assert.Equal(nodes, asked.FindAll(p => MathF.Abs(p.Item1.X - door.X) < 1e-4f && MathF.Abs(p.Item1.Z - door.Z) < 1e-4f).Count);
			}
		}
	}

	[Fact]
	public void APairWithNoPath_IsAWarningNamingBoth()
	{
		MapDocument document = LevelMaps.Generate(2);
		int node = document.Objects.FindIndex(o => o.PrefabId == PrefabSpecs.ResourceNode);
		Vector3 cut = document.Objects[node].Position;
		var report = MapValidator.Validate(document);
		int before = report.Warnings;
		MapValidator.Reach(document, (from, to) => to.X != cut.X || to.Z != cut.Z, report);
		int starts = document.CountOf(PrefabGroup.Barracks) + document.CountOf(PrefabGroup.SpawnLine);
		Assert.Equal(before + starts, report.Warnings);
		Assert.True(report.IsPlayable);
		Assert.True(report.TryGet(MapIssueCode.Unreachable, out MapIssue issue));
		Assert.Equal(MapIssueSeverity.Warning, issue.Severity);
		Assert.Equal(node, issue.Other);
		Assert.Contains($"resource_node {node}", issue.Message);
		Assert.StartsWith("no path from the ", issue.Message);
	}
}

/// <summary>
/// The buildable overlay (§7.5, §8.3, LE5): where LE-D21 takes a pillbox square to the field
/// and turned 45° — 0.5 m of relief under its centre and corners at the most.
/// </summary>
public class BuildableGroundTests
{
	private static HeightField Plane(float degrees)
	{
		var field = new HeightField(41, 41);
		float grade = HeightField.Grade(degrees);
		for (int z = 0; z < field.Depth; z++)
		{
			for (int x = 0; x < field.Width; x++)
			{
				field[x, z] = field.WorldX(x) * grade;
			}
		}

		return field;
	}

	[Theory]
	[InlineData(0f, true)]
	[InlineData(5.5f, true)]
	[InlineData(5.75f, false)]
	[InlineData(7f, false)]
	public void APlane_IsBuildableUpToWhatAPillboxTurned45DegreesTakes(float degrees, bool buildable) =>
		Assert.Equal(buildable, BuildableGround.At(Plane(degrees), 0f, 0f));

	[Fact]
	public void ABump_UnderACorner_IsNotBuildable()
	{
		HeightField field = Plane(0f);
		Assert.True(BuildableGround.At(field, 0f, 0f));
		field[22, 22] = 0.6f;
		Assert.False(BuildableGround.At(field, 0.2f, 0.2f));
		Assert.True(BuildableGround.At(field, -8f, -8f));
	}
}
