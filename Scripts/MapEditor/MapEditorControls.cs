using System;
using System.Collections.Generic;
using System.Globalization;
using Gdpyr.Sim.Level;
using Godot;

namespace Gdpyr.MapEditor;

/// <summary>The editor's four tools, on the rail down the left (docs/LEVEL_EDITOR.md §8.2), in the order of their keys.</summary>
public enum EditorTool : byte
{
	Terrain,
	Prefabs,
	Roads,
	Bridges,
}

/// <summary>What a key does in the editor (§8.2). <see cref="None"/> for a key that does nothing there.</summary>
public enum EditorAction : byte
{
	None,
	TerrainTool,
	PrefabsTool,
	RoadsTool,
	BridgesTool,

	/// <summary><c>[</c> and <c>]</c>: the brush's radius.</summary>
	RadiusDown,
	RadiusUp,

	/// <summary><c>-</c> and <c>=</c>: its rate, or its strength.</summary>
	AmountDown,
	AmountUp,

	/// <summary><c>,</c> and <c>.</c>: its core, or its grade.</summary>
	ShapeDown,
	ShapeUp,

	/// <summary><c>R</c> and <c>Shift+R</c>: a prefab turned 15° (LE5).</summary>
	TurnLeft,
	TurnRight,

	/// <summary><c>G</c>: a prefab snapped to the metre grid (LE5).</summary>
	SnapToGrid,

	/// <summary><c>Tab</c>: a bridge section turned end for end (LE6).</summary>
	TurnEndForEnd,

	/// <summary><c>PgUp</c> and <c>PgDn</c>: a free-floating section raised or lowered (LE6).</summary>
	RaiseSection,
	LowerSection,

	/// <summary><c>Del</c>: take up what is selected (LE5).</summary>
	TakeUp,

	/// <summary><c>O</c>: the next overlay.</summary>
	NextOverlay,

	Undo,
	Redo,
	Save,

	/// <summary><c>F5</c>: save, and play it in a second process (§8.7).</summary>
	Test,

	/// <summary><c>T</c>: the camera looks straight down.</summary>
	StraightDown,

	/// <summary><c>Esc</c>: drop a ramp's first click, or a stroke under way.</summary>
	Cancel,
}

/// <summary>A setting of a brush: a slider in the terrain tool's panel (§8.3).</summary>
public enum BrushField : byte
{
	Radius,
	Rate,
	Strength,
	Core,
	Grade,
}

/// <summary>
/// What the terrain is drawn with over its bands (§8.3): the terrain shader's
/// <c>overlay</c>, by number. <i>Buildable</i> is LE5's, and is where LE-D21 takes a
/// structure (<see cref="Gdpyr.Sim.Level.BuildableGround"/>).
/// </summary>
public enum TerrainOverlay : byte
{
	None = 0,
	Bands = 1,
	Slope = 2,
	Contours = 3,
	Buildable = 4,
}

/// <summary>How the slope overlay and the brush's readout colour a face (LE-D23).</summary>
public enum SlopeClass : byte
{
	/// <summary>Under 35°: green.</summary>
	Walkable,

	/// <summary>35° to 40°: yellow. Still walkable, and the bake keeps it.</summary>
	Steep,

	/// <summary>Past 40°: red. Not walkable on a data map.</summary>
	TooSteep,
}

/// <summary>The water and the six band numbers (§6.4): a slider each in the terrain tool's panel (§8.3).</summary>
public enum BandNumber : byte
{
	WaterLevel,
	SlopeFree,
	SlopeGain,
	SandTop,
	MudTop,
	GrassTop,
	Blend,
}

/// <summary>One key of §8.2's table.</summary>
public readonly struct EditorKey
{
	public readonly Key Key;
	public readonly bool Ctrl;
	public readonly bool Shift;
	public readonly EditorAction Action;

	public EditorKey(Key key, bool ctrl, bool shift, EditorAction action)
	{
		Key = key;
		Ctrl = ctrl;
		Shift = shift;
		Action = action;
	}

	/// <summary>The key as it is printed: <c>Ctrl+Z</c>, <c>Shift+R</c>, <c>]</c>.</summary>
	public string Label => (Ctrl ? "Ctrl+" : string.Empty) + (Shift ? "Shift+" : string.Empty) + MapEditorControls.KeyName(Key);
}

/// <summary>A slider's range and step, and what it is called.</summary>
public readonly struct SliderRange
{
	public readonly string Label;
	public readonly string Unit;
	public readonly float Min;
	public readonly float Max;
	public readonly float Step;

	public SliderRange(string label, string unit, float min, float max, float step)
	{
		Label = label;
		Unit = unit;
		Min = min;
		Max = max;
		Step = step;
	}
}

/// <summary>
/// The engine-free half of the map editor's screen (docs/LEVEL_EDITOR.md §8.2–8.3): which
/// key does what, which settings a brush shows and what a key steps them by, the slider
/// ranges, what the status line and the readout say, and what the test process is started
/// with. The widgets are <c>MapEditorRoot</c>'s; these are answered by <c>dotnet test</c>, as
/// <c>StrategistCards</c> is for the strategist's card.
/// </summary>
public static class MapEditorControls
{
	/// <summary>How often an unsaved map is autosaved [s] (§8.6).</summary>
	public const float AutosaveSeconds = 60f;

	/// <summary>Where the slope overlay turns from green to yellow [degrees]: the shader's.</summary>
	public const float SteepDegrees = 35f;

	/// <summary>What a key press multiplies a radius or a rate by, or divides it by.</summary>
	public const float ScaleStep = 1.25f;

	/// <summary>What a key press moves a core or a strength by.</summary>
	public const float ShareStep = 0.05f;

	/// <summary>What a key press moves a grade by [degrees].</summary>
	public const float GradeStep = 5f;

	/// <summary>§8.2's keys, as the editor reads them. A key with Ctrl is a command, and moves no camera.</summary>
	public static readonly EditorKey[] Keys =
	{
		new(Key.Key1, false, false, EditorAction.TerrainTool),
		new(Key.Key2, false, false, EditorAction.PrefabsTool),
		new(Key.Key3, false, false, EditorAction.RoadsTool),
		new(Key.Key4, false, false, EditorAction.BridgesTool),
		new(Key.Bracketleft, false, false, EditorAction.RadiusDown),
		new(Key.Bracketright, false, false, EditorAction.RadiusUp),
		new(Key.Minus, false, false, EditorAction.AmountDown),
		new(Key.Equal, false, false, EditorAction.AmountUp),
		new(Key.Comma, false, false, EditorAction.ShapeDown),
		new(Key.Period, false, false, EditorAction.ShapeUp),
		new(Key.R, false, false, EditorAction.TurnLeft),
		new(Key.R, false, true, EditorAction.TurnRight),
		new(Key.G, false, false, EditorAction.SnapToGrid),
		new(Key.Tab, false, false, EditorAction.TurnEndForEnd),
		new(Key.Pageup, false, false, EditorAction.RaiseSection),
		new(Key.Pagedown, false, false, EditorAction.LowerSection),
		new(Key.Delete, false, false, EditorAction.TakeUp),
		new(Key.O, false, false, EditorAction.NextOverlay),
		new(Key.Z, true, false, EditorAction.Undo),
		new(Key.Y, true, false, EditorAction.Redo),
		new(Key.S, true, false, EditorAction.Save),
		new(Key.F5, false, false, EditorAction.Test),
		new(Key.T, false, false, EditorAction.StraightDown),
		new(Key.Escape, false, false, EditorAction.Cancel),
	};

	/// <summary>What a key does, with the modifiers held. Shift is read only where a key has a Shift of its own.</summary>
	public static EditorAction ActionOf(Key key, bool ctrl, bool shift)
	{
		EditorAction unshifted = EditorAction.None;
		foreach (EditorKey bound in Keys)
		{
			if (bound.Key != key || bound.Ctrl != ctrl)
			{
				continue;
			}

			if (bound.Shift == shift)
			{
				return bound.Action;
			}

			if (!bound.Shift)
			{
				unshifted = bound.Action;
			}
		}

		// Shift is sprint for the camera: a key without a Shift of its own still works with it held.
		return shift ? unshifted : EditorAction.None;
	}

	/// <summary>The key an action is on, as printed; empty for one on none.</summary>
	public static string KeyOf(EditorAction action)
	{
		foreach (EditorKey bound in Keys)
		{
			if (bound.Action == action)
			{
				return bound.Label;
			}
		}

		return string.Empty;
	}

	public static string KeyName(Key key) => key switch
	{
		>= Key.Key0 and <= Key.Key9 => ((char)('0' + (key - Key.Key0))).ToString(),
		Key.Bracketleft => "[",
		Key.Bracketright => "]",
		Key.Minus => "-",
		Key.Equal => "=",
		Key.Comma => ",",
		Key.Period => ".",
		Key.Pageup => "PgUp",
		Key.Pagedown => "PgDn",
		Key.Delete => "Del",
		Key.Escape => "Esc",
		_ => key.ToString(),
	};

	// ---- tools -----------------------------------------------------------------------------------

	/// <summary>The tool a key selects, or null for a key that is not a tool's.</summary>
	public static EditorTool? ToolOf(EditorAction action) => action switch
	{
		EditorAction.TerrainTool => EditorTool.Terrain,
		EditorAction.PrefabsTool => EditorTool.Prefabs,
		EditorAction.RoadsTool => EditorTool.Roads,
		EditorAction.BridgesTool => EditorTool.Bridges,
		_ => null,
	};

	/// <summary>Whether a tool is in this build: the terrain is LE4's, prefabs LE5's; roads and bridges are LE6's.</summary>
	public static bool IsBuilt(EditorTool tool) => tool is EditorTool.Terrain or EditorTool.Prefabs;

	/// <summary>What the rail calls a tool, with its key.</summary>
	public static string ToolLabel(EditorTool tool)
	{
		string name = tool switch
		{
			EditorTool.Terrain => "Terrain",
			EditorTool.Prefabs => "Prefabs",
			EditorTool.Roads => "Roads",
			_ => "Bridges",
		};
		return $"{(int)tool + 1}  {name}";
	}

	/// <summary>Why a tool's button is off: which phase builds it.</summary>
	public static string NotBuilt(EditorTool tool) => tool switch
	{
		EditorTool.Prefabs => "prefabs are LE5's",
		EditorTool.Roads => "roads are LE6's",
		EditorTool.Bridges => "bridges are LE6's",
		_ => string.Empty,
	};

	// ---- a brush's settings ------------------------------------------------------------------------

	private static readonly BrushField[] SculptFields = { BrushField.Radius, BrushField.Core, BrushField.Rate };
	private static readonly BrushField[] LockedFields = { BrushField.Radius, BrushField.Grade, BrushField.Rate };
	private static readonly BrushField[] SoftFields = { BrushField.Radius, BrushField.Core, BrushField.Strength };
	private static readonly BrushField[] RampFields = { BrushField.Radius };

	/// <summary>
	/// The settings a brush shows (§6.2): what it reads, and nothing it does not. Sculpt
	/// reads its core and rate, grade-locked its grade and rate, smooth and level their core
	/// and strength, and a ramp its radius alone, as half its width.
	/// </summary>
	public static IReadOnlyList<BrushField> FieldsOf(BrushKind kind) => kind switch
	{
		BrushKind.Sculpt => SculptFields,
		BrushKind.GradeLocked => LockedFields,
		BrushKind.Smooth or BrushKind.Level => SoftFields,
		_ => RampFields,
	};

	/// <summary>The setting <c>-</c> and <c>=</c> step for a brush: its rate, or its strength; none for a ramp.</summary>
	public static BrushField? AmountOf(BrushKind kind) => kind switch
	{
		BrushKind.Sculpt or BrushKind.GradeLocked => BrushField.Rate,
		BrushKind.Smooth or BrushKind.Level => BrushField.Strength,
		_ => null,
	};

	/// <summary>The setting <c>,</c> and <c>.</c> step for a brush: its core, or its grade; none for a ramp.</summary>
	public static BrushField? ShapeOf(BrushKind kind) => kind switch
	{
		BrushKind.GradeLocked => BrushField.Grade,
		BrushKind.Sculpt or BrushKind.Smooth or BrushKind.Level => BrushField.Core,
		_ => null,
	};

	/// <summary>A setting's slider: <see cref="BrushSettings"/>' own ranges (§6.2).</summary>
	public static SliderRange RangeOf(BrushField field, BrushKind kind = BrushKind.Sculpt) => field switch
	{
		BrushField.Radius => new(kind == BrushKind.Ramp ? "Half width" : "Radius", "m",
			BrushSettings.MinRadiusMeters, BrushSettings.MaxRadiusMeters, 0.5f),
		BrushField.Rate => new("Rate", "m/s", BrushSettings.MinRateMetersPerSecond, BrushSettings.MaxRateMetersPerSecond, 0.25f),
		BrushField.Strength => new("Strength", string.Empty, 0f, 1f, ShareStep),
		BrushField.Core => new("Core", string.Empty, 0f, BrushSettings.MaxCore, ShareStep),
		_ => new("Grade", "°", BrushSettings.MinGradeDegrees, BrushSettings.MaxGradeDegrees, 1f),
	};

	public static float Get(in BrushSettings brush, BrushField field) => field switch
	{
		BrushField.Radius => brush.RadiusMeters,
		BrushField.Rate => brush.RateMetersPerSecond,
		BrushField.Strength => brush.Strength,
		BrushField.Core => brush.Core,
		_ => brush.GradeDegrees,
	};

	/// <summary>The brush with one setting changed, pulled into its range.</summary>
	public static BrushSettings Set(BrushSettings brush, BrushField field, float value)
	{
		switch (field)
		{
			case BrushField.Radius:
				brush.RadiusMeters = value;
				break;
			case BrushField.Rate:
				brush.RateMetersPerSecond = value;
				break;
			case BrushField.Strength:
				brush.Strength = value;
				break;
			case BrushField.Core:
				brush.Core = value;
				break;
			default:
				brush.GradeDegrees = value;
				break;
		}

		return brush.Clamped();
	}

	/// <summary>
	/// What a key does to the brush (§8.2): <c>[</c> <c>]</c> the radius, <c>-</c> <c>=</c> the
	/// rate or strength, <c>,</c> <c>.</c> the core or grade. A radius and a rate are scaled by
	/// <see cref="ScaleStep"/>, a share moved by <see cref="ShareStep"/>, a grade by
	/// <see cref="GradeStep"/>; each held to its range. Any other action leaves it.
	/// </summary>
	public static BrushSettings Step(in BrushSettings brush, EditorAction action)
	{
		(BrushField? field, int direction) = action switch
		{
			EditorAction.RadiusDown => ((BrushField?)BrushField.Radius, -1),
			EditorAction.RadiusUp => (BrushField.Radius, 1),
			EditorAction.AmountDown => (AmountOf(brush.Kind), -1),
			EditorAction.AmountUp => (AmountOf(brush.Kind), 1),
			EditorAction.ShapeDown => (ShapeOf(brush.Kind), -1),
			EditorAction.ShapeUp => (ShapeOf(brush.Kind), 1),
			_ => (null, 0),
		};

		if (field is not { } which)
		{
			return brush;
		}

		float value = Get(brush, which);
		value = which switch
		{
			BrushField.Radius or BrushField.Rate => direction > 0 ? value * ScaleStep : value / ScaleStep,
			BrushField.Grade => value + (direction * GradeStep),
			_ => value + (direction * ShareStep),
		};

		// A share stepped in twentieths stays on them, not 0.15000001.
		if (which is BrushField.Core or BrushField.Strength)
		{
			value = MathF.Round(value / ShareStep) * ShareStep;
		}

		return Set(brush, which, value);
	}

	/// <summary>A preset's button: its name, and the key-free description under it.</summary>
	public static string Describe(in BrushSettings brush) => brush.Kind switch
	{
		BrushKind.Sculpt => $"sculpt, {F(brush.RadiusMeters, "0.#")} m, core {F(brush.Core)}, {F(brush.RateMetersPerSecond)} m/s",
		BrushKind.GradeLocked => $"grade-locked, {F(brush.RadiusMeters, "0.#")} m, {F(brush.GradeDegrees, "0")}°, {F(brush.RateMetersPerSecond)} m/s",
		BrushKind.Smooth => $"smooth, {F(brush.RadiusMeters, "0.#")} m, strength {F(brush.Strength)}",
		BrushKind.Level => $"level, {F(brush.RadiusMeters, "0.#")} m, strength {F(brush.Strength)}",
		_ => $"ramp, {F(2f * brush.RadiusMeters, "0.#")} m wide: two clicks",
	};

	/// <summary>The preset whose settings these are, or -1 once a setting has been moved off every one.</summary>
	public static int PresetOf(in BrushSettings brush)
	{
		for (int i = 0; i < TerrainBrush.Presets.Length; i++)
		{
			BrushSettings preset = TerrainBrush.Presets[i].Settings;
			bool same = preset.Kind == brush.Kind;
			foreach (BrushField field in FieldsOf(brush.Kind))
			{
				same &= Get(preset, field) == Get(brush, field);
			}

			if (same)
			{
				return i;
			}
		}

		return -1;
	}

	// ---- the water and the bands --------------------------------------------------------------------

	/// <summary>A band number's slider. The edges' are held between their neighbours by <see cref="SetBand"/>.</summary>
	public static SliderRange RangeOf(BandNumber number) => number switch
	{
		BandNumber.WaterLevel => new("Water level", "m", MapLimits.MinHeightMeters, MapLimits.MaxHeightMeters, 0.25f),
		BandNumber.SlopeFree => new("Slope that costs nothing", "°", 0f, 90f, 1f),
		BandNumber.SlopeGain => new("A degree past it is worth", "m", 0f, 2f, 0.05f),
		BandNumber.SandTop => new("Sand's top", "m", 0f, 20f, 0.25f),
		BandNumber.MudTop => new("Mud's top", "m", 0f, 30f, 0.25f),
		BandNumber.GrassTop => new("Grass's top", "m", 0f, BandSettings.MaxNumber, 0.5f),
		_ => new("Blend", "m", 0f, 5f, 0.1f),
	};

	public static float GetBand(in MapBands bands, BandNumber number)
	{
		BandSettings settings = bands.Settings;
		return number switch
		{
			BandNumber.WaterLevel => settings.WaterLevelMeters,
			BandNumber.SlopeFree => settings.SlopeFreeDegrees,
			BandNumber.SlopeGain => settings.SlopeGainMetersPerDegree,
			BandNumber.SandTop => settings.SandTopMeters,
			BandNumber.MudTop => settings.MudTopMeters,
			BandNumber.GrassTop => settings.GrassTopMeters,
			_ => settings.BlendMeters,
		};
	}

	/// <summary>
	/// The numbers with one changed, as the file holds them, and always numbers a map can
	/// mean (§6.4): the water inside LE-D4's limits, every number from 0 to 65.535, and an edge
	/// held between its neighbours — sand's top no higher than mud's, mud's between sand's and
	/// grass's — so a slider never puts the bands out of their order. A number not a number
	/// changes nothing.
	/// </summary>
	public static MapBands SetBand(in MapBands bands, BandNumber number, float value)
	{
		if (float.IsNaN(value))
		{
			return bands;
		}

		BandSettings s = bands.Settings;
		float n = Math.Clamp(value, 0f, BandSettings.MaxNumber);
		switch (number)
		{
			case BandNumber.WaterLevel:
				// Held to LE-D4's limits by the step it is stored as (MapBands.From).
				s.WaterLevelMeters = value;
				break;
			case BandNumber.SlopeFree:
				s.SlopeFreeDegrees = n;
				break;
			case BandNumber.SlopeGain:
				s.SlopeGainMetersPerDegree = n;
				break;
			case BandNumber.SandTop:
				s.SandTopMeters = MathF.Min(n, s.MudTopMeters);
				break;
			case BandNumber.MudTop:
				s.MudTopMeters = Math.Clamp(n, s.SandTopMeters, s.GrassTopMeters);
				break;
			case BandNumber.GrassTop:
				s.GrassTopMeters = MathF.Max(n, s.MudTopMeters);
				break;
			default:
				s.BlendMeters = n;
				break;
		}

		return MapBands.From(s);
	}

	// ---- overlays, slopes, the readout ----------------------------------------------------------------

	/// <summary><c>O</c>: none, bands, slope, buildable, contours, and round again: §8.3's order.</summary>
	public static TerrainOverlay Next(TerrainOverlay overlay) => overlay switch
	{
		TerrainOverlay.None => TerrainOverlay.Bands,
		TerrainOverlay.Bands => TerrainOverlay.Slope,
		TerrainOverlay.Slope => TerrainOverlay.Buildable,
		TerrainOverlay.Buildable => TerrainOverlay.Contours,
		_ => TerrainOverlay.None,
	};

	public static string Name(TerrainOverlay overlay) => overlay switch
	{
		TerrainOverlay.Bands => "bands",
		TerrainOverlay.Slope => "slope",
		TerrainOverlay.Buildable => "buildable: where a structure is taken",
		TerrainOverlay.Contours => "contours every 2 m",
		_ => "none",
	};

	/// <summary>A face's class, as the slope overlay colours it: green under 35°, yellow to 40°, red past it (LE-D23).</summary>
	public static SlopeClass ClassOf(float degrees) =>
		degrees < SteepDegrees ? SlopeClass.Walkable
		: degrees <= MapLimits.WalkableSlopeDegrees ? SlopeClass.Steep
		: SlopeClass.TooSteep;

	/// <summary>The class's colour: the shader's.</summary>
	public static Color ColourOf(SlopeClass slope) => slope switch
	{
		SlopeClass.Walkable => new Color(0.2f, 0.75f, 0.2f),
		SlopeClass.Steep => new Color(0.9f, 0.85f, 0.15f),
		_ => new Color(0.9f, 0.15f, 0.1f),
	};

	/// <summary>
	/// The steepest face under a brush [degrees] (§8.3): of the cells whose middle is within
	/// <paramref name="radius"/> of (x, z), the steeper of each one's two triangles. Zero when
	/// no cell is.
	/// </summary>
	public static float SteepestFaceDegrees(HeightField field, float x, float z, float radius)
	{
		if (!(radius > 0f) || !float.IsFinite(x) || !float.IsFinite(z))
		{
			return 0f;
		}

		int minX = Math.Max((int)MathF.Floor(x - radius - field.OriginX - 0.5f), 0);
		int minZ = Math.Max((int)MathF.Floor(z - radius - field.OriginZ - 0.5f), 0);
		int maxX = Math.Min((int)MathF.Ceiling(x + radius - field.OriginX - 0.5f), field.Width - 2);
		int maxZ = Math.Min((int)MathF.Ceiling(z + radius - field.OriginZ - 0.5f), field.Depth - 2);
		float steepest = 0f;
		for (int cz = minZ; cz <= maxZ; cz++)
		{
			for (int cx = minX; cx <= maxX; cx++)
			{
				float dx = field.WorldX(cx) + 0.5f - x;
				float dz = field.WorldZ(cz) + 0.5f - z;
				if ((dx * dx) + (dz * dz) <= radius * radius)
				{
					steepest = MathF.Max(steepest, MathF.Max(field.FaceGrade(cx, cz, false), field.FaceGrade(cx, cz, true)));
				}
			}
		}

		return HeightField.Degrees(steepest);
	}

	/// <summary>The readout beside the ring: the steepest face under the brush, and its class.</summary>
	public static string Readout(float steepestDegrees) =>
		$"steepest face {F(steepestDegrees, "0.0")}°" + ClassOf(steepestDegrees) switch
		{
			SlopeClass.Walkable => string.Empty,
			SlopeClass.Steep => ", steep",
			_ => ", past what is walkable",
		};

	/// <summary>
	/// The status line under the view (§8.2): where the cursor is on the ground, its height,
	/// the slope there and its band.
	/// </summary>
	public static string CursorLine(HeightField field, in BandSettings bands, Vector3? ground)
	{
		if (ground is not { } at)
		{
			return "the cursor is not over the ground";
		}

		int x = Math.Clamp((int)MathF.Round(at.X - field.OriginX), 0, field.Width - 1);
		int z = Math.Clamp((int)MathF.Round(at.Z - field.OriginZ), 0, field.Depth - 1);
		float slope = field.SlopeDegrees(x, z);
		TerrainBand band = TerrainBands.BandOf(bands, at.Y, slope);
		return $"x {F(at.X, "0.0")}  z {F(at.Z, "0.0")}   height {F(at.Y)} m   slope {F(slope, "0.0")}°   {Name(band)}";
	}

	public static string Name(TerrainBand band) => band switch
	{
		TerrainBand.Water => "water",
		TerrainBand.Sand => "sand",
		TerrainBand.Mud => "mud",
		TerrainBand.Grass => "grass",
		_ => "stone",
	};

	/// <summary>The bar's title (§8.2): the map's name, its file, and whether it is saved.</summary>
	public static string Title(string mapName, string fileName, bool dirty, bool everSaved) =>
		$"{mapName} ({fileName}{MapFile.Extension}) — "
		+ (!everSaved ? "not saved yet" : dirty ? "unsaved changes" : "saved");

	/// <summary>The count of the validator's issues, under the view (§8.2).</summary>
	public static string Issues(MapReport report) => MapStore.Describe(report);

	/// <summary>What a ramp's answer reads as.</summary>
	public static string Describe(RampResult result) => result switch
	{
		RampResult.Ok => "ramp made",
		RampResult.TooSmall => "a ramp needs two clicks apart, with ground between them",
		_ => "too steep for a ramp: the line between the clicks is steeper than 30°",
	};

	// ---- the prefab tool (§8.4, LE5) ------------------------------------------------------------------

	/// <summary>How far <c>R</c> and <c>Shift+R</c> turn a prefab [degrees].</summary>
	public const float TurnStepDegrees = 15f;

	/// <summary>The palette's kinds, in its order. Bridge sections are LE6's tool's, not this palette's.</summary>
	public static readonly PrefabKind[] PaletteKinds = { PrefabKind.Round, PrefabKind.Cover, PrefabKind.Structure, PrefabKind.Scenery };

	public static string Name(PrefabKind kind) => kind switch
	{
		PrefabKind.Round => "a round's pieces",
		PrefabKind.Cover => "cover",
		PrefabKind.Structure => "structures standing at the start",
		PrefabKind.Scenery => "scenery",
		_ => "bridges",
	};

	/// <summary>The prefabs of a kind on the palette, by id.</summary>
	public static List<PrefabSpec> Palette(PrefabKind kind)
	{
		var specs = new List<PrefabSpec>();
		foreach (PrefabSpec spec in PrefabSpecs.All)
		{
			if (spec.Kind == kind && kind != PrefabKind.Bridge)
			{
				specs.Add(spec);
			}
		}

		return specs;
	}

	/// <summary>
	/// A palette entry's count against its limit (§8.4): <c>barracks 1/4</c>; a group a map
	/// holds any number of is its count alone, and one it must hold says so while it holds none.
	/// </summary>
	public static string CountLabel(MapDocument document, PrefabSpec spec)
	{
		int count = document.CountOf(spec.Group);
		int max = PrefabSpecs.MaxOf(spec.Group);
		string label = max == int.MaxValue ? $"{spec.Name} {count}" : $"{spec.Name} {count}/{max}";
		return count < PrefabSpecs.MinOf(spec.Group) ? label + ", needs one" : label;
	}

	/// <summary>
	/// <c>R</c> (+1) and <c>Shift+R</c> (−1): the next 15° step that way, from a yaw that may be
	/// between steps; in [0, 2π).
	/// </summary>
	public static float Turn(float yaw, int direction)
	{
		float step = TurnStepDegrees * (MathF.PI / 180f);
		float steps = MathF.Round(yaw / step);
		float turned = (steps + Math.Sign(direction)) * step;
		float tau = 2f * MathF.PI;
		turned %= tau;
		return turned < 0f ? turned + tau : turned;
	}

	/// <summary><c>G</c>'s grid: a point on the nearest whole metre.</summary>
	public static float Snap(float meters) => MathF.Round(meters);

	/// <summary>What the ghost says (§8.4): the prefab, the answer, and the average grade under it.</summary>
	public static string Ghost(PrefabSpec spec, PlacementCheck check)
	{
		string grade = spec.HasFootprint ? $", {F(check.GradeDegrees, "0.0")}° under it" : string.Empty;
		string limit = check.Result == PrefabPlacementResult.TooSteep ? $" where it takes {F(spec.SteepestDegrees, "0")}°" : string.Empty;
		return $"{spec.Name}: {PrefabPlacement.Describe(check.Result)}{grade}{limit}";
	}

	/// <summary>A prefab parameter's slider: its own range, in the file's whole numbers.</summary>
	public static SliderRange RangeOf(PrefabParameter parameter) =>
		new(parameter.Name, string.Empty, parameter.Min, parameter.Max, parameter.Max - parameter.Min > 100 ? 50f : 1f);

	// ---- the test process --------------------------------------------------------------------------

	/// <summary>
	/// What <b>Test</b> starts a second process with (LE-D19, §8.7): <c>-- --practice --map
	/// &lt;path&gt;</c>, and before that <c>--path &lt;project&gt;</c> when this is not an export
	/// (<paramref name="projectPath"/> null when it is), and <c>--headless</c> when this
	/// process draws nothing, since a test of a map from a headless editor has no window to
	/// open either.
	/// </summary>
	public static List<string> TestArguments(string mapPath, string projectPath, bool headless)
	{
		var arguments = new List<string>();
		if (!string.IsNullOrEmpty(projectPath))
		{
			arguments.Add("--path");
			arguments.Add(projectPath);
		}

		if (headless)
		{
			arguments.Add("--headless");
		}

		arguments.Add("--");
		arguments.Add("--practice");
		arguments.Add("--map");
		arguments.Add(mapPath);
		return arguments;
	}

	private static string F(float value, string format = "0.00") => value.ToString(format, CultureInfo.InvariantCulture);
}

/// <summary>
/// When an unsaved map is autosaved (§8.6): a minute after the first change not yet
/// written, saved or autosaved, and every minute while changes keep coming. Nothing is
/// written for a map with no change since the last write.
/// </summary>
public sealed class AutosaveClock
{
	private int _written;
	private float _waiting;

	public AutosaveClock(int changes = 0)
	{
		_written = changes;
	}

	/// <summary>Seconds since the first change not yet written.</summary>
	public float Waiting => _waiting;

	/// <summary>
	/// <paramref name="seconds"/> more, with <paramref name="changes"/> made in the session so
	/// far. True when an autosave is due; the caller writes it and calls <see cref="Written"/>.
	/// </summary>
	public bool Tick(float seconds, int changes)
	{
		if (changes == _written)
		{
			_waiting = 0f;
			return false;
		}

		_waiting += seconds > 0f ? seconds : 0f;
		return _waiting >= MapEditorControls.AutosaveSeconds;
	}

	/// <summary>The map as it is, <paramref name="changes"/> into the session, has been written: saved or autosaved.</summary>
	public void Written(int changes)
	{
		_written = changes;
		_waiting = 0f;
	}
}
