using System;
using System.Collections.Generic;
using System.Linq;
using Gdpyr.MapEditor;
using Gdpyr.Sim.Level;
using Godot;
using Xunit;

namespace Gdpyr.Tests;

/// <summary>
/// The map editor's screen without its widgets (docs/LEVEL_EDITOR.md §8.2–8.3, §8.6–8.7,
/// LE4): which key does what, which settings a brush shows and what a key steps them by,
/// what the readout and the status line say, when an autosave is due, and what the test
/// process is started with.
/// </summary>
public class MapEditorControlsTests
{
	[Theory]
	[InlineData(Key.Key1, false, false, EditorAction.TerrainTool)]
	[InlineData(Key.Key2, false, false, EditorAction.PrefabsTool)]
	[InlineData(Key.Key3, false, false, EditorAction.RoadsTool)]
	[InlineData(Key.Key4, false, false, EditorAction.BridgesTool)]
	[InlineData(Key.Bracketleft, false, false, EditorAction.RadiusDown)]
	[InlineData(Key.Bracketright, false, false, EditorAction.RadiusUp)]
	[InlineData(Key.Minus, false, false, EditorAction.AmountDown)]
	[InlineData(Key.Equal, false, false, EditorAction.AmountUp)]
	[InlineData(Key.Comma, false, false, EditorAction.ShapeDown)]
	[InlineData(Key.Period, false, false, EditorAction.ShapeUp)]
	[InlineData(Key.R, false, false, EditorAction.TurnLeft)]
	[InlineData(Key.R, false, true, EditorAction.TurnRight)]
	[InlineData(Key.G, false, false, EditorAction.SnapToGrid)]
	[InlineData(Key.Tab, false, false, EditorAction.TurnEndForEnd)]
	[InlineData(Key.Pageup, false, false, EditorAction.RaiseSection)]
	[InlineData(Key.Pagedown, false, false, EditorAction.LowerSection)]
	[InlineData(Key.Delete, false, false, EditorAction.TakeUp)]
	[InlineData(Key.O, false, false, EditorAction.NextOverlay)]
	[InlineData(Key.Z, true, false, EditorAction.Undo)]
	[InlineData(Key.Y, true, false, EditorAction.Redo)]
	[InlineData(Key.S, true, false, EditorAction.Save)]
	[InlineData(Key.F5, false, false, EditorAction.Test)]
	[InlineData(Key.T, false, false, EditorAction.StraightDown)]
	[InlineData(Key.Escape, false, false, EditorAction.Cancel)]
	public void TheKeys_AreSection8_2s(Key key, bool ctrl, bool shift, EditorAction action) =>
		Assert.Equal(action, MapEditorControls.ActionOf(key, ctrl, shift));

	[Theory]
	[InlineData(Key.Z, false, false)]
	[InlineData(Key.S, false, false)]
	[InlineData(Key.W, false, false)]
	[InlineData(Key.Y, false, false)]
	[InlineData(Key.Key1, true, false)]
	[InlineData(Key.R, true, false)]
	public void AKeyNotInTheTable_DoesNothing(Key key, bool ctrl, bool shift) =>
		Assert.Equal(EditorAction.None, MapEditorControls.ActionOf(key, ctrl, shift));

	[Fact]
	public void Shift_IsTheCamerasSprint_AndLeavesAKeyWithoutAShiftOfItsOwnAsItIs()
	{
		Assert.Equal(EditorAction.RadiusUp, MapEditorControls.ActionOf(Key.Bracketright, false, true));
		Assert.Equal(EditorAction.TerrainTool, MapEditorControls.ActionOf(Key.Key1, false, true));
		Assert.Equal(EditorAction.Undo, MapEditorControls.ActionOf(Key.Z, true, true));
	}

	[Fact]
	public void EveryKey_IsBoundOnce_AndEveryActionButNone_HasOne()
	{
		var seen = new HashSet<(Key, bool, bool)>();
		foreach (EditorKey bound in MapEditorControls.Keys)
		{
			Assert.True(seen.Add((bound.Key, bound.Ctrl, bound.Shift)), $"{bound.Label} is bound twice");
		}

		foreach (EditorAction action in Enum.GetValues<EditorAction>())
		{
			Assert.Equal(action == EditorAction.None, MapEditorControls.KeyOf(action) == string.Empty);
		}

		Assert.Equal("Ctrl+Z", MapEditorControls.KeyOf(EditorAction.Undo));
		Assert.Equal("Shift+R", MapEditorControls.KeyOf(EditorAction.TurnRight));
		Assert.Equal("]", MapEditorControls.KeyOf(EditorAction.RadiusUp));
		Assert.Equal("1", MapEditorControls.KeyOf(EditorAction.TerrainTool));
		Assert.Equal("PgDn", MapEditorControls.KeyOf(EditorAction.LowerSection));
	}

	[Fact]
	public void TheToolKeys_SelectTheirTools_AndTheTerrainAndPrefabsAreBuilt()
	{
		Assert.Equal(EditorTool.Terrain, MapEditorControls.ToolOf(EditorAction.TerrainTool));
		Assert.Equal(EditorTool.Bridges, MapEditorControls.ToolOf(EditorAction.BridgesTool));
		Assert.Null(MapEditorControls.ToolOf(EditorAction.Undo));
		Assert.True(MapEditorControls.IsBuilt(EditorTool.Terrain));
		Assert.True(MapEditorControls.IsBuilt(EditorTool.Prefabs));
		foreach (EditorTool tool in new[] { EditorTool.Roads, EditorTool.Bridges })
		{
			Assert.False(MapEditorControls.IsBuilt(tool));
			Assert.Matches("LE6", MapEditorControls.NotBuilt(tool));
		}

		Assert.StartsWith("1", MapEditorControls.ToolLabel(EditorTool.Terrain));
		Assert.StartsWith("4", MapEditorControls.ToolLabel(EditorTool.Bridges));
	}

	[Theory]
	[InlineData(BrushKind.Sculpt, new[] { BrushField.Radius, BrushField.Core, BrushField.Rate })]
	[InlineData(BrushKind.GradeLocked, new[] { BrushField.Radius, BrushField.Grade, BrushField.Rate })]
	[InlineData(BrushKind.Smooth, new[] { BrushField.Radius, BrushField.Core, BrushField.Strength })]
	[InlineData(BrushKind.Level, new[] { BrushField.Radius, BrushField.Core, BrushField.Strength })]
	[InlineData(BrushKind.Ramp, new[] { BrushField.Radius })]
	public void ABrush_ShowsWhatItReads(BrushKind kind, BrushField[] fields)
	{
		Assert.Equal(fields, MapEditorControls.FieldsOf(kind));
		BrushField? amount = MapEditorControls.AmountOf(kind);
		BrushField? shape = MapEditorControls.ShapeOf(kind);
		Assert.True(amount == null || fields.Contains(amount.Value));
		Assert.True(shape == null || fields.Contains(shape.Value));
	}

	[Fact]
	public void TheSliders_AreTheBrushsOwnRanges()
	{
		foreach (BrushField field in Enum.GetValues<BrushField>())
		{
			SliderRange range = MapEditorControls.RangeOf(field);
			BrushSettings low = MapEditorControls.Set(TerrainBrush.Presets[0].Settings, field, -1000f);
			BrushSettings high = MapEditorControls.Set(TerrainBrush.Presets[0].Settings, field, 1000f);
			Assert.Equal(range.Min, MapEditorControls.Get(low, field));
			Assert.Equal(range.Max, MapEditorControls.Get(high, field));
			Assert.True(range.Step > 0f && range.Step <= (range.Max - range.Min) / 10f);
		}

		Assert.Equal("Half width", MapEditorControls.RangeOf(BrushField.Radius, BrushKind.Ramp).Label);
		Assert.Equal("Radius", MapEditorControls.RangeOf(BrushField.Radius, BrushKind.Sculpt).Label);
	}

	[Fact]
	public void TheKeys_StepTheBrush_AndHoldItToItsRanges()
	{
		BrushSettings hill = TerrainBrush.Presets[1].Settings;
		Assert.Equal(16f * MapEditorControls.ScaleStep, MapEditorControls.Step(hill, EditorAction.RadiusUp).RadiusMeters, 4);
		Assert.Equal(16f / MapEditorControls.ScaleStep, MapEditorControls.Step(hill, EditorAction.RadiusDown).RadiusMeters, 4);
		Assert.Equal(2f * MapEditorControls.ScaleStep, MapEditorControls.Step(hill, EditorAction.AmountUp).RateMetersPerSecond, 4);
		Assert.Equal(0.05f, MapEditorControls.Step(hill, EditorAction.ShapeUp).Core, 5);
		Assert.Equal(hill.Core, MapEditorControls.Step(hill, EditorAction.ShapeDown).Core);

		BrushSettings mound = TerrainBrush.Presets[6].Settings;
		Assert.Equal(35f, MapEditorControls.Step(mound, EditorAction.ShapeUp).GradeDegrees, 4);
		Assert.Equal(25f, MapEditorControls.Step(mound, EditorAction.ShapeDown).GradeDegrees, 4);
		Assert.Equal(mound.Core, MapEditorControls.Step(mound, EditorAction.ShapeUp).Core);

		BrushSettings smooth = TerrainBrush.Presets[8].Settings;
		Assert.Equal(0.55f, MapEditorControls.Step(smooth, EditorAction.AmountUp).Strength, 5);
		Assert.Equal(smooth.RateMetersPerSecond, MapEditorControls.Step(smooth, EditorAction.AmountUp).RateMetersPerSecond);

		BrushSettings ramp = TerrainBrush.Presets[10].Settings;
		Assert.Equal(ramp, MapEditorControls.Step(ramp, EditorAction.AmountUp));
		Assert.Equal(ramp, MapEditorControls.Step(ramp, EditorAction.ShapeUp));
		Assert.Equal(hill, MapEditorControls.Step(hill, EditorAction.Undo));

		BrushSettings big = hill;
		for (int i = 0; i < 40; i++)
		{
			big = MapEditorControls.Step(big, EditorAction.RadiusUp);
		}

		Assert.Equal(BrushSettings.MaxRadiusMeters, big.RadiusMeters);
		for (int i = 0; i < 80; i++)
		{
			big = MapEditorControls.Step(big, EditorAction.RadiusDown);
			big = MapEditorControls.Step(big, EditorAction.ShapeUp);
		}

		Assert.Equal(BrushSettings.MinRadiusMeters, big.RadiusMeters);
		Assert.Equal(BrushSettings.MaxCore, big.Core, 5);
	}

	[Fact]
	public void ASharesSteps_StayOnTwentieths()
	{
		BrushSettings smooth = TerrainBrush.Presets[8].Settings;
		for (int i = 0; i < 7; i++)
		{
			smooth = MapEditorControls.Step(smooth, EditorAction.AmountDown);
		}

		Assert.Equal(0.15f, smooth.Strength);
	}

	[Fact]
	public void EveryPreset_IsFoundAgain_AndAMovedSettingIsNone()
	{
		for (int i = 0; i < TerrainBrush.Presets.Length; i++)
		{
			Assert.Equal(i, MapEditorControls.PresetOf(TerrainBrush.Presets[i].Settings));
			Assert.False(string.IsNullOrEmpty(MapEditorControls.Describe(TerrainBrush.Presets[i].Settings)));
		}

		Assert.Equal(-1, MapEditorControls.PresetOf(MapEditorControls.Step(TerrainBrush.Presets[1].Settings, EditorAction.RadiusUp)));
		Assert.Contains("16 m wide", MapEditorControls.Describe(TerrainBrush.Presets[10].Settings));
	}

	[Fact]
	public void TheBandSliders_KeepNumbersAMapCanMean()
	{
		MapBands bands = MapBands.NewMap;
		var random = new Random(5);
		BandNumber[] numbers = Enum.GetValues<BandNumber>();
		for (int i = 0; i < 2000; i++)
		{
			BandNumber number = numbers[random.Next(numbers.Length)];
			float value = (float)((random.NextDouble() * 200d) - 60d);
			bands = MapEditorControls.SetBand(bands, number, value);
			Assert.True(bands.Settings.IsValid, $"after {number} = {value}");
		}

		MapBands start = MapBands.NewMap;
		Assert.Equal(-2f, MapEditorControls.GetBand(MapEditorControls.SetBand(start, BandNumber.WaterLevel, -2f), BandNumber.WaterLevel));
		Assert.Equal(MapLimits.MinHeightMeters,
			MapEditorControls.GetBand(MapEditorControls.SetBand(start, BandNumber.WaterLevel, -100f), BandNumber.WaterLevel));
		Assert.Equal(4f, MapEditorControls.GetBand(MapEditorControls.SetBand(start, BandNumber.SandTop, 9f), BandNumber.SandTop));
		Assert.Equal(22f, MapEditorControls.GetBand(MapEditorControls.SetBand(start, BandNumber.MudTop, 30f), BandNumber.MudTop));
		Assert.Equal(1.5f, MapEditorControls.GetBand(MapEditorControls.SetBand(start, BandNumber.MudTop, 0f), BandNumber.MudTop));
		Assert.Equal(4f, MapEditorControls.GetBand(MapEditorControls.SetBand(start, BandNumber.GrassTop, 1f), BandNumber.GrassTop));
		Assert.Equal(0.25f, MapEditorControls.GetBand(MapEditorControls.SetBand(start, BandNumber.Blend, 0.25f), BandNumber.Blend));
		Assert.Equal(start, MapEditorControls.SetBand(start, BandNumber.Blend, float.NaN));
		foreach (BandNumber number in numbers)
		{
			SliderRange range = MapEditorControls.RangeOf(number);
			Assert.True(range.Min < range.Max && range.Step > 0f);
			float now = MapEditorControls.GetBand(start, number);
			Assert.InRange(now, range.Min, range.Max);
		}
	}

	[Fact]
	public void TheOverlays_GoRound_InSection8_3sOrder()
	{
		TerrainOverlay overlay = TerrainOverlay.None;
		var seen = new List<TerrainOverlay>();
		for (int i = 0; i < 5; i++)
		{
			seen.Add(overlay);
			overlay = MapEditorControls.Next(overlay);
		}

		Assert.Equal(TerrainOverlay.None, overlay);
		Assert.Equal(new[] { TerrainOverlay.None, TerrainOverlay.Bands, TerrainOverlay.Slope, TerrainOverlay.Buildable,
			TerrainOverlay.Contours }, seen);
		Assert.Equal(2, (int)TerrainOverlay.Slope);
		Assert.Equal(4, (int)TerrainOverlay.Buildable);
		Assert.Contains("2 m", MapEditorControls.Name(TerrainOverlay.Contours));
	}

	[Theory]
	[InlineData(0f, SlopeClass.Walkable)]
	[InlineData(34.99f, SlopeClass.Walkable)]
	[InlineData(35f, SlopeClass.Steep)]
	[InlineData(40f, SlopeClass.Steep)]
	[InlineData(40.01f, SlopeClass.TooSteep)]
	[InlineData(70f, SlopeClass.TooSteep)]
	public void ASlope_IsColouredAsTheOverlayColoursIt(float degrees, SlopeClass expected)
	{
		Assert.Equal(expected, MapEditorControls.ClassOf(degrees));
		Assert.Equal(MapLimits.WalkableSlopeDegrees, 40f);
	}

	[Fact]
	public void TheReadout_IsTheSteepestFaceUnderTheBrush()
	{
		var field = new HeightField();
		Assert.Equal(0f, MapEditorControls.SteepestFaceDegrees(field, 0f, 0f, 8f));

		// A sample 1 m up, 10 m east of the middle: its edges are 45°, and the face across the
		// crease of the cell north-west of it the whole √2 steeper, 54.7°. Under a brush that
		// reaches it, and not under one that does not.
		field[130, 120] = 1f;
		float crease = HeightField.Degrees(MathF.Sqrt(2f));
		Assert.Equal(crease, MapEditorControls.SteepestFaceDegrees(field, 0f, 0f, 12f), 3);
		Assert.Equal(0f, MapEditorControls.SteepestFaceDegrees(field, 0f, 0f, 8f));
		Assert.Equal(crease, MapEditorControls.SteepestFaceDegrees(field, 10f, 0f, 2f), 3);
		Assert.Equal(45f, MapEditorControls.SteepestFaceDegrees(field, 9.5f, 0.5f, 0.75f), 3);

		// The one cell under a brush whose crease is in its second triangle, its first being level.
		Assert.Equal(crease, MapEditorControls.SteepestFaceDegrees(field, 9.5f, -0.5f, 0.6f), 3);

		// A disc, not its square: the crease's cell is in the square of 9.2 m about (0, 6), and 11.5 m off.
		Assert.Equal(0f, MapEditorControls.SteepestFaceDegrees(field, 0f, 6f, 9.2f));
		Assert.Equal(0f, MapEditorControls.SteepestFaceDegrees(field, float.NaN, 0f, 2f));

		Assert.Equal("steepest face 12.0°", MapEditorControls.Readout(12f));
		Assert.EndsWith(", steep", MapEditorControls.Readout(38f));
		Assert.EndsWith("past what is walkable", MapEditorControls.Readout(41f));
	}

	[Fact]
	public void TheStatusLine_SaysWhereTheCursorIs()
	{
		var field = new HeightField();
		BandSettings bands = MapBands.NewMap.Settings;
		Assert.Equal("x 1.5  z -2.0   height 0.00 m   slope 0.0°   grass",
			MapEditorControls.CursorLine(field, bands, new Vector3(1.5f, 0f, -2f)));
		Assert.Contains("not over the ground", MapEditorControls.CursorLine(field, bands, null));
		Assert.EndsWith("water", MapEditorControls.CursorLine(field, bands, new Vector3(0f, -6f, 0f)));

		Assert.Equal("Ridge (ridge.map) — unsaved changes", MapEditorControls.Title("Ridge", "ridge", true, true));
		Assert.Equal("Ridge (ridge.map) — saved", MapEditorControls.Title("Ridge", "ridge", false, true));
		Assert.Equal("Ridge (ridge.map) — not saved yet", MapEditorControls.Title("Ridge", "ridge", true, false));
		Assert.Contains("too steep", MapEditorControls.Describe(RampResult.TooSteep));
		Assert.Contains("two clicks", MapEditorControls.Describe(RampResult.TooSmall));
	}

	[Fact]
	public void ThePalette_HoldsEveryPrefabButTheBridgeSections_Once_ByKind()
	{
		var seen = new HashSet<ushort>();
		foreach (PrefabKind kind in MapEditorControls.PaletteKinds)
		{
			foreach (PrefabSpec spec in MapEditorControls.Palette(kind))
			{
				Assert.Equal(kind, spec.Kind);
				Assert.True(seen.Add(spec.Id), $"{spec.Name} twice");
			}

			Assert.False(string.IsNullOrEmpty(MapEditorControls.Name(kind)));
		}

		foreach (PrefabSpec spec in PrefabSpecs.All)
		{
			Assert.Equal(spec.Kind != PrefabKind.Bridge, seen.Contains(spec.Id));
		}

		Assert.Empty(MapEditorControls.Palette(PrefabKind.Bridge));
	}

	[Fact]
	public void APaletteEntry_CountsAgainstItsLimit()
	{
		MapDocument document = LevelMaps.Generate(1);
		Assert.Equal($"barracks {document.CountOf(PrefabGroup.Barracks)}/4",
			MapEditorControls.CountLabel(document, PrefabSpecs.Get(PrefabSpecs.Barracks)));
		Assert.Equal($"spawn_line {document.CountOf(PrefabGroup.SpawnLine)}",
			MapEditorControls.CountLabel(document, PrefabSpecs.Get(PrefabSpecs.SpawnLine)));
		Assert.Equal("heavy_gun 2/8", MapEditorControls.CountLabel(document, PrefabSpecs.Get(PrefabSpecs.HeavyGun))
			.Replace($"{document.CountOf(PrefabGroup.Emplacement)}/8", "2/8"));
		MapDocument empty = LevelMaps.Empty();
		Assert.Equal("barracks 0/4, needs one", MapEditorControls.CountLabel(empty, PrefabSpecs.Get(PrefabSpecs.Barracks)));
		Assert.Equal("weapon_locker 0, needs one", MapEditorControls.CountLabel(empty, PrefabSpecs.Get(PrefabSpecs.WeaponLocker)));
		Assert.Equal("bunker 0", MapEditorControls.CountLabel(empty, PrefabSpecs.Get(PrefabSpecs.Bunker)));
	}

	[Fact]
	public void R_TurnsAPrefabToTheNext15DegreeStep()
	{
		float step = MathF.PI / 12f;
		Assert.Equal(step, MapEditorControls.Turn(0f, 1), 5);
		Assert.Equal(23 * step, MapEditorControls.Turn(0f, -1), 5);
		Assert.Equal(2 * step, MapEditorControls.Turn(step * 1.2f, 1), 5);
		Assert.Equal(0f, MapEditorControls.Turn(23 * step, 1), 5);
		float yaw = 0f;
		for (int i = 0; i < 24; i++)
		{
			yaw = MapEditorControls.Turn(yaw, 1);
			Assert.InRange(yaw, 0f, 2f * MathF.PI);
		}

		Assert.Equal(0f, yaw, 4);
		Assert.Equal(3f, MapEditorControls.Snap(2.6f));
		Assert.Equal(-12f, MapEditorControls.Snap(-12.4f));
	}

	[Fact]
	public void TheGhost_SaysWhatPlacingWouldDo()
	{
		var check = new PlacementCheck();
		PrefabSpec barracks = PrefabSpecs.Get(PrefabSpecs.Barracks);
		Assert.Equal("barracks: ok, 0.0° under it", MapEditorControls.Ghost(barracks, check));
		MapDocument document = LevelMaps.Empty();
		PlacementCheck refused = PrefabPlacement.Check(document, new PlacementRequest(PrefabSpecs.Barracks, 119f, 0f, 0f));
		Assert.Equal("barracks: off the map, 0.0° under it", MapEditorControls.Ghost(barracks, refused));
		PrefabSpec anchor = PrefabSpecs.Get(PrefabSpecs.StrategistAnchor);
		Assert.Equal("strategist_anchor: ok", MapEditorControls.Ghost(anchor, new PlacementCheck()));
	}

	[Fact]
	public void AParameter_HasItsOwnRange()
	{
		PrefabSpec node = PrefabSpecs.Get(PrefabSpecs.ResourceNode);
		Assert.True(node.TryGetParameter(PrefabSpecs.NodeCaptureRadiusKey, out PrefabParameter radius));
		SliderRange range = MapEditorControls.RangeOf(radius);
		Assert.Equal(500f, range.Min);
		Assert.Equal(1500f, range.Max);
		Assert.Equal(50f, range.Step);
		Assert.True(node.TryGetParameter(PrefabSpecs.NodePerkKey, out PrefabParameter perk));
		Assert.Equal(1f, MapEditorControls.RangeOf(perk).Step);
	}

	[Fact]
	public void TheTestProcess_PlaysTheSavedFile_OnItsOwn()
	{
		Assert.Equal(new[] { "--path", "/home/me/gdpyr", "--", "--practice", "--map", "/data/maps/ridge.map" },
			MapEditorControls.TestArguments("/data/maps/ridge.map", "/home/me/gdpyr", false));
		Assert.Equal(new[] { "--", "--practice", "--map", "/data/maps/ridge.map" },
			MapEditorControls.TestArguments("/data/maps/ridge.map", null, false));
		Assert.Equal(new[] { "--headless", "--", "--practice", "--map", "/a b/ridge.map" },
			MapEditorControls.TestArguments("/a b/ridge.map", string.Empty, true));

		// What the second process makes of what is after the bare `--`.
		List<string> arguments = MapEditorControls.TestArguments("/data/maps/ridge.map", "/p", true);
		var options = Gdpyr.Core.LaunchOptions.Parse(arguments.Skip(arguments.IndexOf("--") + 1).ToList());
		Assert.Null(options.Error);
		Assert.True(options.Practice);
		Assert.Equal("/data/maps/ridge.map", options.MapReference);
	}

	[Fact]
	public void AnAutosave_IsDueAMinuteAfterTheFirstChangeNotWritten()
	{
		var clock = new AutosaveClock();
		Assert.False(clock.Tick(120f, 0));
		Assert.Equal(0f, clock.Waiting);

		Assert.False(clock.Tick(30f, 1));
		Assert.False(clock.Tick(29f, 3));
		Assert.True(clock.Tick(1f, 3));
		clock.Written(3);
		Assert.False(clock.Tick(59f, 3));
		Assert.Equal(0f, clock.Waiting);
		Assert.False(clock.Tick(59f, 4));
		Assert.True(clock.Tick(1f, 4));

		// A save is a write too.
		clock.Written(4);
		Assert.False(clock.Tick(600f, 4));
		Assert.False(clock.Tick(-5f, 5));
		Assert.Equal(0f, clock.Waiting);
	}
}
