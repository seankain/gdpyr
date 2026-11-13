using System;
using System.Linq;
using Gdpyr.Sim;
using Gdpyr.Sim.Level;
using Godot;
using Xunit;

namespace Gdpyr.Tests;

/// <summary>
/// What a round needs, asked of a document (docs/LEVEL_EDITOR.md §6.9): the generated
/// maps pass, and a map is refused for each of §6.9's errors — a generated one changed
/// to have that error and nothing else — and warned of each of its warnings that LE2
/// can see.
/// </summary>
public class MapValidatorTests
{
	/// <summary>The generated map, changed; asserts the change is refused for <paramref name="code"/> and for nothing else.</summary>
	private static MapIssue Refused(Action<MapDocument> change, MapIssueCode code, string because = null)
	{
		MapDocument document = LevelMaps.Generate(1);
		change(document);
		MapReport report = MapValidator.Validate(document);
		Assert.False(report.IsPlayable);
		Assert.All(report.Issues.Where(i => i.Severity == MapIssueSeverity.Error), issue => Assert.Equal(code, issue.Code));
		Assert.True(report.TryGet(code, out MapIssue found));
		Assert.Equal(MapIssueSeverity.Error, found.Severity);
		if (because != null)
		{
			Assert.Contains(because, found.Message);
		}

		return found;
	}

	private static MapIssue Warned(Action<MapDocument> change, MapIssueCode code, string because = null)
	{
		MapDocument document = LevelMaps.Generate(1);
		change(document);
		MapReport report = MapValidator.Validate(document);
		Assert.True(report.TryGet(code, out MapIssue found), string.Join("; ", report.Issues));
		Assert.Equal(MapIssueSeverity.Warning, found.Severity);
		if (because != null)
		{
			Assert.Contains(because, found.Message);
		}

		return found;
	}

	private static int First(MapDocument document, ushort prefab) => document.Objects.FindIndex(o => o.PrefabId == prefab);

	private static void Add(MapDocument document, ushort prefab, float x, float z, float yaw = 0f, MapParameter[] parameters = null,
		float height = 0f)
	{
		PlacementCheck check = PrefabPlacement.Check(document, new PlacementRequest(prefab, x, z, yaw, height, parameters));
		Assert.True(check.IsOk, $"{PrefabSpecs.Get(prefab).Name} at ({x}, {z}): {check}");
		check.Edit.Apply(document);
	}

	// ---- the generated maps ----------------------------------------------------------------

	[Theory]
	[InlineData(1)]
	[InlineData(2)]
	[InlineData(3)]
	[InlineData(4)]
	[InlineData(5)]
	[InlineData(6)]
	[InlineData(7)]
	[InlineData(8)]
	public void AGeneratedMap_Passes_WithNoWarning_AndSoDoesItsFile(int seed)
	{
		MapDocument document = LevelMaps.Generate(seed);
		MapReport report = MapValidator.Validate(document);
		Assert.True(report.IsPlayable, string.Join("; ", report.Issues));
		Assert.Empty(report.Issues);

		// What a round needs, and more.
		Assert.Equal(1, document.CountOf(PrefabGroup.SpawnLine));
		Assert.Equal(1, document.CountOf(PrefabGroup.WeaponLocker));
		Assert.Equal(1, document.CountOf(PrefabGroup.Barracks));
		Assert.Equal(3, document.CountOf(PrefabGroup.ResourceNode));
		Assert.Equal(1, document.CountOf(PrefabGroup.VehicleKiosk));
		Assert.Equal(1, document.CountOf(PrefabGroup.StrategistAnchor));
		Assert.True(document.CountOf(PrefabGroup.Cover) >= 8);
		Assert.Equal(2, document.CountOf(PrefabGroup.Structure));

		// Read back from its file, it says the same.
		Assert.True(MapFile.TryRead(MapFile.Write(document), out MapDocument read, out string error), error);
		Assert.Empty(MapValidator.Validate(read).Issues);
	}

	[Fact]
	public void TheGenerator_IsSeeded()
	{
		Assert.Equal(MapFile.Write(Gdpyr.MapTool.MapGenerator.Generate(5)), MapFile.Write(Gdpyr.MapTool.MapGenerator.Generate(5)));
		Assert.NotEqual(MapFile.HashHex(MapFile.Write(LevelMaps.Generate(5))), MapFile.HashHex(MapFile.Write(LevelMaps.Generate(6))));
		Assert.NotEqual(LevelMaps.Generate(5).Id, LevelMaps.Generate(6).Id);
	}

	[Fact]
	public void ANewMap_IsRefusedForWhatItLacks()
	{
		MapReport report = MapValidator.Validate(LevelMaps.Empty());
		Assert.Equal(
			new[] { MapIssueCode.NoSpawnLine, MapIssueCode.NoWeaponLocker, MapIssueCode.BarracksCount, MapIssueCode.NodeCount },
			report.Issues.Select(i => i.Code));
		Assert.Equal(4, report.Errors);
		Assert.Equal(0, report.Warnings);
	}

	// ---- errors: counts --------------------------------------------------------------------

	[Fact]
	public void NoSpawnLine_OrNoLocker_IsRefused()
	{
		Refused(d => d.Objects.RemoveAt(First(d, PrefabSpecs.SpawnLine)), MapIssueCode.NoSpawnLine, "no spawn line");
		Refused(d => d.Objects.RemoveAt(First(d, PrefabSpecs.WeaponLocker)), MapIssueCode.NoWeaponLocker, "no weapon locker");
	}

	[Fact]
	public void BarracksNot1To4_IsRefused()
	{
		Refused(d => d.Objects.RemoveAt(First(d, PrefabSpecs.Barracks)), MapIssueCode.BarracksCount, "0 barracks; a map has 1 to 4");

		// Five, as a file another tool wrote could hold them: the count is refused, whatever else is.
		MapDocument document = LevelMaps.Generate(1);
		MapObject barracks = document.Objects[First(document, PrefabSpecs.Barracks)];
		for (int i = 0; i < 4; i++)
		{
			document.Objects.Add(barracks);
		}

		Assert.True(MapValidator.Validate(document).TryGet(MapIssueCode.BarracksCount, out MapIssue five));
		Assert.Contains("5 barracks; a map has 1 to 4", five.Message);
	}

	[Fact]
	public void NodesNot1To8_AreRefused()
	{
		Refused(d => d.Objects.RemoveAll(o => o.PrefabId == PrefabSpecs.ResourceNode), MapIssueCode.NodeCount, "0 resource nodes; a map has 1 to 8");

		MapDocument document = LevelMaps.Generate(1);
		MapObject node = document.Objects[First(document, PrefabSpecs.ResourceNode)];
		while (document.CountOf(PrefabGroup.ResourceNode) < 9)
		{
			document.Objects.Add(node);
		}

		Assert.True(MapValidator.Validate(document).TryGet(MapIssueCode.NodeCount, out MapIssue nine));
		Assert.Contains("9 resource nodes", nine.Message);
	}

	[Theory]
	[InlineData(PrefabSpecs.HeavyGun, 9, "9 guns and launchers; a map holds at most 8")]
	[InlineData(PrefabSpecs.AmmoCan, 13, "13 ammunition cans; a map holds at most 12")]
	[InlineData(PrefabSpecs.CoverBlock, 97, "97 cover boxes; a map holds at most 96")]
	[InlineData(PrefabSpecs.StructureSandbagWall, 17, "17 structures; a map holds at most 16")]
	[InlineData(PrefabSpecs.VehicleKiosk, 2, "2 vehicle kiosks; a map holds at most 1")]
	[InlineData(PrefabSpecs.StrategistAnchor, 2, "2 strategist anchors; a map holds at most 1")]
	public void MoreThanAGroupMayHold_IsRefused(int prefab, int count, string because)
	{
		// In memory, as a file another tool wrote could hold them: one on top of another is the count's error and the way's.
		MapDocument document = LevelMaps.Generate(1);
		int have = document.CountOf(PrefabSpecs.Get((ushort)prefab).Group);
		for (int i = have; i < count; i++)
		{
			document.Objects.Add(new MapObject((ushort)prefab, (short)(-10000 + (i * 50)), 0, (short)(-10500 + (i % 5 * 20)), 0));
		}

		MapReport report = MapValidator.Validate(document);
		Assert.True(report.TryGet(MapIssueCode.TooMany, out MapIssue found));
		Assert.Contains(because, found.Message);
	}

	// ---- errors: ids -----------------------------------------------------------------------

	[Fact]
	public void APrefabPastThisBuildsCatalog_NeedsANewerBuild()
	{
		MapIssue issue = Refused(d => d.Objects.Add(new MapObject(PrefabSpecs.Count, 0, 0, 0, 0)), MapIssueCode.UnknownPrefab,
			"needs a newer build");
		Assert.Equal(LevelMaps.Generate(1).Objects.Count, issue.Object);
	}

	// ---- errors: place ---------------------------------------------------------------------

	[Fact]
	public void AnObjectOffTheMap_IsRefused()
	{
		// A bridge section whose deck leaves ±120 m; written by something other than the editor.
		Refused(d => d.Objects.Add(new MapObject(PrefabSpecs.BridgeSpan, 11800, 0, 0, 0)), MapIssueCode.OutOfBounds,
			"off the map: it stands inside ±120 m");

		// A round's piece outside ±115 m, though its box is inside ±120 m.
		Refused(d => d.Objects.Add(new MapObject(PrefabSpecs.AmmoCan, 0, 0, -11600, 0)), MapIssueCode.OutOfBounds, "±115 m");
	}

	[Fact]
	public void TwoObjectsInEachOthersWay_AreRefused()
	{
		MapIssue issue = Refused(d =>
		{
			int locker = First(d, PrefabSpecs.WeaponLocker);
			MapObject copy = d.Objects[locker];
			d.Objects.Add(new MapObject(copy.PrefabId, (short)(copy.X + 100), copy.Y, copy.Z, copy.Yaw));
		}, MapIssueCode.InTheWay, "are in each other's way");
		Assert.Equal(First(LevelMaps.Generate(1), PrefabSpecs.WeaponLocker), issue.Object);
		Assert.Equal(LevelMaps.Generate(1).Objects.Count, issue.Other);
	}

	// ---- errors: ground --------------------------------------------------------------------

	[Fact]
	public void APaddedObjectNotOnItsPad_IsRefused()
	{
		// Its ground moved under it: a sample under it raised 5 cm.
		Refused(d =>
		{
			MapObject barracks = d.Objects[First(d, PrefabSpecs.Barracks)];
			Vector3 at = barracks.Position;
			d.Terrain[(int)(at.X - d.Terrain.OriginX), (int)(at.Z - d.Terrain.OriginZ)] += 0.05f;
		}, MapIssueCode.OffItsPad, "the ground under it runs");

		// A sample 1.2–1.5 m past the barracks' footprint, under no other, lowered six steps, 23 mm: the pad
		// reaches 1.5 m past the footprint, and is held level out there as under it.
		MapIssue margin = Refused(d =>
		{
			int barracks = First(d, PrefabSpecs.Barracks);
			PrefabSpecs.TryGetPadArea(d.Objects[barracks], out PadArea area);
			HeightField field = d.Terrain;
			for (int z = 0; z < field.Depth; z++)
			{
				for (int x = 0; x < field.Width; x++)
				{
					float wx = field.WorldX(x);
					float wz = field.WorldZ(z);
					float distance = area.DistanceTo(wx, wz);
					bool underAnother = d.Objects.Exists(o => o != d.Objects[barracks]
						&& PrefabSpecs.TryGetPadArea(o, out PadArea other) && other.DistanceTo(wx, wz) <= 1.5f);
					if (distance > 1.2f && distance <= 1.5f && !underAnother)
					{
						field[x, z] -= 6f / 256f;
						return;
					}
				}
			}
		}, MapIssueCode.OffItsPad);
		Assert.Equal(First(LevelMaps.Generate(1), PrefabSpecs.Barracks), margin.Object);

		// It stands 3 cm off its level pad.
		Refused(d =>
		{
			int spawn = First(d, PrefabSpecs.SpawnLine);
			d.Objects[spawn] = d.Objects[spawn].WithY((short)(d.Objects[spawn].Y + 3));
		}, MapIssueCode.OffItsPad, "it stands at");

		// 1 cm is within what is stored: a pad's step against a centimetre.
		MapDocument document = LevelMaps.Generate(1);
		int node = First(document, PrefabSpecs.ResourceNode);
		document.Objects[node] = document.Objects[node].WithY((short)(document.Objects[node].Y + 1));
		Assert.False(MapValidator.Validate(document).Has(MapIssueCode.OffItsPad));
	}

	[Fact]
	public void ARoundsPieceUnderTheWater_IsRefused()
	{
		// The water raised over a can: it, and every round piece as low, is under it; cover and scenery are no error.
		Refused(d =>
		{
			float can = d.Objects[First(d, PrefabSpecs.AmmoCan)].Position.Y;
			d.Bands = d.Bands with { WaterLevel = HeightField.ToSample(can + 0.05f) };
		}, MapIssueCode.UnderWater, "is under the water");
	}

	[Fact]
	public void APieceLevelWithTheWater_IsNotUnderIt_ToPlacementOrToTheValidator()
	{
		// Ground and water at step −1277, −4.98828 m. A spawn line's height to the centimetre is
		// −4.99 m, 1.7 mm under the water; its pad's level is the water's step. It is not under.
		MapDocument document = LevelMaps.Empty();
		document.Bands = document.Bands with { WaterLevel = -1277 };
		Array.Fill(document.Terrain.Heights, HeightField.ToMeters(-1277));
		Add(document, PrefabSpecs.SpawnLine, 0f, 0f);
		Assert.Equal(-499, document.Objects[0].Y);
		Assert.Equal(HeightField.ToMeters(-1277), PrefabPlacement.StandingHeight(document.Objects[0]));
		Assert.False(MapValidator.Validate(document).Has(MapIssueCode.UnderWater));

		// A can stands on the ground to the centimetre, which is under: both say so.
		PlacementCheck can = PrefabPlacement.Check(document, new PlacementRequest(PrefabSpecs.AmmoCan, 20f, 0f, 0f));
		Assert.Equal(PrefabPlacementResult.InWater, can.Result);
		document.Objects.Add(can.Object);
		Assert.True(MapValidator.Validate(document).Has(MapIssueCode.UnderWater));
	}

	// ---- errors: limits --------------------------------------------------------------------

	[Fact]
	public void AHeightOutsideLeD4_IsRefused()
	{
		MapIssue issue = Refused(d => d.Terrain[3, 200] = 64.5f, MapIssueCode.HeightOutOfRange, "1 samples outside -16 … +64 m, the first at (3, 200)");
		Assert.Equal(new Vector3(-117f, 64.5f, 80f), issue.Where);
		Refused(d => d.Terrain[3, 200] = float.NaN, MapIssueCode.HeightOutOfRange);
	}

	[Fact]
	public void ANumberOutsideItsRange_IsRefused()
	{
		Refused(d => d.Bands = d.Bands with { WaterLevel = (-16 * 256) - 1 }, MapIssueCode.NumberOutOfRange, "the water is outside");
		Refused(d => d.Bands = d.Bands with { SandTop = 5000 }, MapIssueCode.NumberOutOfRange, "the band edges are out of their order");
		Refused(d => d.Name = new string('x', 49), MapIssueCode.NumberOutOfRange, "the name, author or description");

		Refused(d =>
		{
			int i = First(d, PrefabSpecs.CoverBlock);
			MapObject o = d.Objects[i];
			d.Objects[i] = new MapObject(o.PrefabId, o.X, o.Y, o.Z, o.Yaw, 4);
		}, MapIssueCode.NumberOutOfRange, "has flags 04");

		Refused(d =>
		{
			int i = First(d, PrefabSpecs.CoverBlock);
			MapObject o = d.Objects[i];
			d.Objects[i] = new MapObject(o.PrefabId, o.X, o.Y, o.Z, o.Yaw, 0, new[] { new MapParameter(1, 5) });
		}, MapIssueCode.NumberOutOfRange, "has parameter 1, which a cover_block has not");

		Refused(d =>
		{
			int i = First(d, PrefabSpecs.ResourceNode);
			MapObject o = d.Objects[i];
			d.Objects[i] = new MapObject(o.PrefabId, o.X, o.Y, o.Z, o.Yaw, 0, new[] { new MapParameter(PrefabSpecs.NodePerkKey, 2) });
		}, MapIssueCode.NumberOutOfRange, "perk is 2, outside 0–1");

	}

	[Theory]
	[InlineData(0, 50, 100, 2, "a width of 5 m, not 4, 6 or 8")]
	[InlineData(0, 60, 200, 2, "a grade limit of 20 %, outside 6–15 %")]
	[InlineData(0, 60, 50, 2, "a grade limit of 5 %")]
	[InlineData(1, 60, 100, 2, "style 1")]
	[InlineData(0, 80, 150, 1, "1 points")]
	public void ARoadsNumbersOutsideTheirRange_AreRefused(int style, int width, int grade, int points, string because)
	{
		Refused(d => d.Roads.Add(new MapRoad((byte)style, (byte)width, (byte)grade,
			Enumerable.Range(0, points).Select(p => new RoadPoint((short)(p * 1000), 0)).ToArray())), MapIssueCode.NumberOutOfRange, because);
	}

	[Fact]
	public void ARoadOffTheField_IsRefused_AndOneInRange_IsNot()
	{
		Refused(d => d.Roads.Add(new MapRoad(0, 60, 100, new[] { new RoadPoint(0, 0), new RoadPoint(12100, 0) })),
			MapIssueCode.NumberOutOfRange, "road 0 has a point off the field");

		MapDocument document = LevelMaps.Generate(1);
		document.Roads.Add(new MapRoad(0, 40, 60, new[] { new RoadPoint(-5000, 2000), new RoadPoint(5000, 2000), new RoadPoint(5000, -2000) }));
		Assert.Empty(MapValidator.Validate(document).Issues);
	}

	[Fact]
	public void ATerrainOfAnotherSize_IsRefused()
	{
		MapDocument document = LevelMaps.Generate(1);
		var small = new MapDocument(new HeightField(61, 61));
		small.Objects.AddRange(document.Objects);
		MapReport report = MapValidator.Validate(small);
		Assert.True(report.TryGet(MapIssueCode.NumberOutOfRange, out MapIssue issue));
		Assert.Contains("the terrain is 61 × 61 samples, not 241 × 241", issue.Message);
	}

	// ---- warnings --------------------------------------------------------------------------

	[Fact]
	public void ANodeOrTheSpawnLine_InsideABarracksRing_IsWarnedOf()
	{
		// A node 60 m from the barracks: §2.3's layout keeps them over 107 m apart.
		MapIssue node = Warned(d => Add(d, PrefabSpecs.ResourceNode, 90f, -30f), MapIssueCode.InsideDefendedRing,
			"inside its defended ring of 107 m");
		Assert.Equal(First(LevelMaps.Generate(1), PrefabSpecs.Barracks), node.Other);

		// The barracks moved 58 m from the spawn line, as a file another tool wrote could have it.
		Warned(d =>
		{
			int at = First(d, PrefabSpecs.Barracks);
			MapObject barracks = d.Objects[at];
			d.Objects[at] = new MapObject(barracks.PrefabId, -6000, barracks.Y, 4000, barracks.Yaw);
		}, MapIssueCode.InsideDefendedRing, "spawn_line 1 is 58 m from barracks 0");

		// And the map still plays: a warning is not an error.
		MapDocument document = LevelMaps.Generate(1);
		Add(document, PrefabSpecs.ResourceNode, 90f, -30f);
		Assert.True(MapValidator.Validate(document).IsPlayable);
	}

	[Fact]
	public void MoreThan4mOfReliefInsideANodesRadius_IsWarnedOf()
	{
		Warned(d =>
		{
			MapObject node = d.Objects[First(d, PrefabSpecs.ResourceNode)];
			Vector3 at = node.Position;
			d.Terrain[(int)(at.X - d.Terrain.OriginX) + 5, (int)(at.Z - d.Terrain.OriginZ)] += 4.5f;
		}, MapIssueCode.NodeRelief, "4.5 m of relief");
	}

	[Fact]
	public void AnOpenBridgeEnd_IsWarnedOf_AndAJoinedOrLandedOneIsNot()
	{
		// One span 6 m over the ground: both ends open.
		MapDocument document = LevelMaps.Generate(1);
		float ground = document.Terrain.Sample(-40f, -60f);
		Add(document, PrefabSpecs.BridgeSpan, -40f, -60f, 0f, null, ground + 6f);
		MapReport report = MapValidator.Validate(document);
		Assert.True(report.IsPlayable);
		Assert.Equal(2, report.Issues.Count(i => i.Code == MapIssueCode.OpenBridgeEnd));
		Assert.Contains("has an open end", report.Issues.First(i => i.Code == MapIssueCode.OpenBridgeEnd).Message);

		// A second joined to its +Z end: two ends open, not four.
		Add(document, PrefabSpecs.BridgeSpan, -40f, -48f, 0f, null, ground + 6f);
		Assert.Equal(2, MapValidator.Validate(document).Issues.Count(i => i.Code == MapIssueCode.OpenBridgeEnd));

		// A road's end at the open −Z end joins it.
		document.Roads.Add(new MapRoad(0, 60, 100, new[] { new RoadPoint(-4000, -6600), new RoadPoint(-4000, -9000) }));
		Assert.Single(MapValidator.Validate(document).Issues, i => i.Code == MapIssueCode.OpenBridgeEnd);

		// A span laid on level ground: its ends are landed.
		MapDocument flat = LevelMaps.Empty();
		Add(flat, PrefabSpecs.BridgeSpan, 0f, 0f, 0.3f, null, 0.4f);
		Assert.Empty(MapValidator.Validate(flat).Issues.Where(i => i.Code == MapIssueCode.OpenBridgeEnd));
	}

	[Fact]
	public void TheReport_CountsErrorsAndWarnings_AndAnIssueSaysWhichItIs()
	{
		MapDocument document = LevelMaps.Generate(1);
		document.Objects.RemoveAt(First(document, PrefabSpecs.WeaponLocker));
		Add(document, PrefabSpecs.ResourceNode, 90f, -30f);
		MapReport report = MapValidator.Validate(document);
		Assert.Equal(1, report.Errors);
		Assert.Equal(1, report.Warnings);
		Assert.False(report.IsPlayable);
		Assert.StartsWith("error: no weapon locker", report.Issues.First(i => i.Severity == MapIssueSeverity.Error).ToString());
		Assert.StartsWith("warning: ", report.Issues.First(i => i.Severity == MapIssueSeverity.Warning).ToString());
	}
}
