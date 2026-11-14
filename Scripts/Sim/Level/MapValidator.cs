using System;
using System.Collections.Generic;
using Godot;

namespace Gdpyr.Sim.Level;

public enum MapIssueSeverity : byte
{
	/// <summary>The map is not listed, hosted or loaded.</summary>
	Error,

	/// <summary>Shown, and the map still plays.</summary>
	Warning,
}

/// <summary>What the validator found (docs/LEVEL_EDITOR.md §6.9), by the row of its table it is in.</summary>
public enum MapIssueCode : byte
{
	// ---- errors: counts
	NoSpawnLine,
	NoWeaponLocker,
	BarracksCount,
	NodeCount,

	/// <summary>More than 8 guns and launchers, 12 cans, 96 cover boxes, 16 structures, one kiosk or one anchor.</summary>
	TooMany,

	// ---- errors: ids
	/// <summary>A prefab id at or past this build's catalog: "needs a newer build".</summary>
	UnknownPrefab,

	// ---- errors: place
	/// <summary>An object outside ±120 m, or a round's piece outside ±115 m.</summary>
	OutOfBounds,

	/// <summary>Two objects in the way of each other.</summary>
	InTheWay,

	// ---- errors: ground
	/// <summary>A padded object not on its pad: the samples under it and within 1.5 m not level to 2 cm, or its height off theirs.</summary>
	OffItsPad,

	/// <summary>A round's piece under the water.</summary>
	UnderWater,

	// ---- errors: limits
	/// <summary>A height outside −16 … +64 m.</summary>
	HeightOutOfRange,

	/// <summary>A number outside its range: the terrain's size, the water or bands, an object's height, flags or parameters, a road's, a text's length.</summary>
	NumberOutOfRange,

	// ---- warnings
	/// <summary>A node or the spawn line inside a barracks' defended ring (MAPS.md §2.3).</summary>
	InsideDefendedRing,

	/// <summary>More than 4 m of relief inside a node's capture radius.</summary>
	NodeRelief,

	/// <summary>A bridge end neither joined nor within 0.5 m of the ground.</summary>
	OpenBridgeEnd,

	/// <summary>No path from a barracks door or the spawn line to a node: the editor's deep check, which bakes (LE5).</summary>
	Unreachable,
}

/// <summary>One thing the validator found, and where: the editor's list flies to it (§8.7).</summary>
public readonly struct MapIssue
{
	public readonly MapIssueSeverity Severity;
	public readonly MapIssueCode Code;
	public readonly string Message;

	/// <summary>The object it is about, by its index in the document; −1 for none.</summary>
	public readonly int Object;

	/// <summary>A second object it is about — the one in the way, the barracks — or −1.</summary>
	public readonly int Other;

	/// <summary>Where it is [m].</summary>
	public readonly Vector3 Where;

	public MapIssue(MapIssueSeverity severity, MapIssueCode code, string message, int placed = -1, int other = -1,
		Vector3 where = default)
	{
		Severity = severity;
		Code = code;
		Message = message;
		Object = placed;
		Other = other;
		Where = where;
	}

	public override string ToString() => $"{(Severity == MapIssueSeverity.Error ? "error" : "warning")}: {Message}";
}

/// <summary>Everything the validator found in one document.</summary>
public sealed class MapReport
{
	private readonly List<MapIssue> _issues = new();

	public IReadOnlyList<MapIssue> Issues => _issues;

	public int Errors { get; private set; }

	public int Warnings { get; private set; }

	/// <summary>No errors: the map may be listed, hosted, sent and loaded.</summary>
	public bool IsPlayable => Errors == 0;

	public bool Has(MapIssueCode code)
	{
		foreach (MapIssue issue in _issues)
		{
			if (issue.Code == code)
			{
				return true;
			}
		}

		return false;
	}

	/// <summary>The first issue of a kind, or false.</summary>
	public bool TryGet(MapIssueCode code, out MapIssue found)
	{
		foreach (MapIssue issue in _issues)
		{
			if (issue.Code == code)
			{
				found = issue;
				return true;
			}
		}

		found = default;
		return false;
	}

	internal void Add(in MapIssue issue)
	{
		_issues.Add(issue);
		if (issue.Severity == MapIssueSeverity.Error)
		{
			Errors++;
		}
		else
		{
			Warnings++;
		}
	}

	internal void Error(MapIssueCode code, string message, int placed = -1, int other = -1, Vector3 where = default) =>
		Add(new MapIssue(MapIssueSeverity.Error, code, message, placed, other, where));

	internal void Warn(MapIssueCode code, string message, int placed = -1, int other = -1, Vector3 where = default) =>
		Add(new MapIssue(MapIssueSeverity.Warning, code, message, placed, other, where));
}

/// <summary>
/// What <c>MapSceneTests</c> asks of a scene, asked of a document (docs/LEVEL_EDITOR.md
/// §6.9): one function for the editor's panel, a file about to be listed, a file just
/// received and a server at boot. Errors keep a map from being listed, hosted, sent or
/// loaded, and do not keep it from being saved: an unfinished map is a file (§8.7).
///
/// | | Errors |
/// |---|---|
/// | counts | no spawn line; no locker; barracks not 1–4; nodes not 1–8; more than 8 guns and launchers, 12 cans, 96 cover boxes, 16 structures, one kiosk or one anchor |
/// | ids | a prefab id at or past this build's catalog |
/// | place | an object outside ±120 m, or a round's piece outside ±115 m; two objects in the way of each other |
/// | ground | a padded object not on its pad; a round's piece under the water |
/// | limits | a height outside −16 … +64 m; a number outside its range |
///
/// Warnings: a node or the spawn line inside a barracks' defended ring; more than 4 m
/// of relief inside a node's capture radius; an open bridge end. Two of §6.9's are
/// not here: a road steeper than its limit after later edits needs LE6's profile
/// (<c>RoadPath</c>), and reach is the editor's deep check, which bakes (LE5).
/// </summary>
public static class MapValidator
{
	/// <summary>How far a pad's samples may be from level, and from the object's height [m].</summary>
	public const float PadToleranceMeters = 0.02f;

	/// <summary>Relief inside a node's capture radius past which a body on the pad may not count as on it (<c>CapturePad</c>'s 4 m).</summary>
	public const float NodeReliefMeters = 4f;

	/// <summary>An end within this of the ground in height is not open: the bake's climb (§6.7).</summary>
	public const float LandedEndMeters = 0.5f;

	/// <summary>Two connectors this close, in plan and in height, are one joint [m].</summary>
	public const float JointMeters = 0.1f;

	/// <summary>A road's end this close to a connector, in plan, joins it [m].</summary>
	public const float RoadJointMeters = 0.5f;

	public static MapReport Validate(MapDocument document)
	{
		var report = new MapReport();
		Limits(document, report);
		Counts(document, report);
		var boxes = new List<PlacedBox>[document.Objects.Count];
		for (int i = 0; i < document.Objects.Count; i++)
		{
			MapObject placed = document.Objects[i];
			if (!PrefabSpecs.TryGet(placed.PrefabId, out PrefabSpec spec))
			{
				report.Error(MapIssueCode.UnknownPrefab,
					$"object {i} is prefab {placed.PrefabId}, which this build has not got: the map needs a newer build",
					i, -1, placed.Position);
				continue;
			}

			boxes[i] = PrefabSpecs.BoxesOf(placed);
			Object(document, report, i, placed, spec);
		}

		InTheWay(document, report, boxes);
		Layout(document, report);
		Bridges(document, report);
		return report;
	}

	// ---- limits ----------------------------------------------------------------------------

	private static void Limits(MapDocument document, MapReport report)
	{
		HeightField field = document.Terrain;
		if (field.Width != MapLimits.TerrainSamples || field.Depth != MapLimits.TerrainSamples)
		{
			report.Error(MapIssueCode.NumberOutOfRange,
				$"the terrain is {field.Width} × {field.Depth} samples, not {MapLimits.TerrainSamples} × {MapLimits.TerrainSamples}");
		}

		int outside = 0;
		int first = -1;
		for (int i = 0; i < field.Heights.Length; i++)
		{
			float height = field.Heights[i];
			if (!(height >= MapLimits.MinHeightMeters && height <= MapLimits.MaxHeightMeters))
			{
				outside++;
				first = first < 0 ? i : first;
			}
		}

		if (outside > 0)
		{
			int x = first % field.Width;
			int z = first / field.Width;
			report.Error(MapIssueCode.HeightOutOfRange,
				$"{outside} samples outside {MapLimits.MinHeightMeters:0} … +{MapLimits.MaxHeightMeters:0} m, the first at ({x}, {z})",
				-1, -1, field.Position(x, z));
		}

		BandSettings bands = document.Bands.Settings;
		if (!bands.IsValid)
		{
			report.Error(MapIssueCode.NumberOutOfRange,
				"the water is outside the ground's limits, or the band edges are out of their order");
		}

		if (MapDocument.Utf8Bytes(document.Name) > MapDocument.MaxNameBytes
			|| MapDocument.Utf8Bytes(document.Author) > MapDocument.MaxAuthorBytes
			|| MapDocument.Utf8Bytes(document.Description) > MapDocument.MaxDescriptionBytes)
		{
			report.Error(MapIssueCode.NumberOutOfRange, "the name, author or description is longer than a file holds");
		}

		if (document.Objects.Count > MapLimits.MaxObjects)
		{
			report.Error(MapIssueCode.NumberOutOfRange, $"{document.Objects.Count} objects, over {MapLimits.MaxObjects}");
		}

		if (document.Roads.Count > MapLimits.MaxRoads)
		{
			report.Error(MapIssueCode.NumberOutOfRange, $"{document.Roads.Count} roads, over {MapLimits.MaxRoads}");
		}

		for (int i = 0; i < document.Roads.Count; i++)
		{
			Road(report, i, document.Roads[i]);
		}
	}

	private static void Road(MapReport report, int index, MapRoad road)
	{
		string wrong = null;
		if (road.Style != MapRoad.StyleDirt)
		{
			wrong = $"style {road.Style}";
		}
		else if (MapRoad.Widths.IndexOf(road.WidthDecimeters) < 0)
		{
			wrong = $"a width of {road.WidthDecimeters / 10f:0.#} m, not 4, 6 or 8";
		}
		else if (road.GradeLimitPermille < MapRoad.MinGradeLimitPermille
			|| road.GradeLimitPermille > MapRoad.MaxGradeLimitPermille)
		{
			wrong = $"a grade limit of {road.GradeLimitPermille / 10f:0.#} %, outside 6–15 %";
		}
		else if (road.Points.Length < MapLimits.MinRoadPoints)
		{
			wrong = $"{road.Points.Length} points";
		}
		else
		{
			foreach (RoadPoint point in road.Points)
			{
				if (MathF.Abs(point.XMeters) > MapLimits.FieldHalfMeters || MathF.Abs(point.ZMeters) > MapLimits.FieldHalfMeters)
				{
					wrong = "a point off the field";
					break;
				}
			}
		}

		if (wrong != null)
		{
			report.Error(MapIssueCode.NumberOutOfRange, $"road {index} has {wrong}");
		}
	}

	// ---- counts ----------------------------------------------------------------------------

	private static void Counts(MapDocument document, MapReport report)
	{
		if (document.CountOf(PrefabGroup.SpawnLine) == 0)
		{
			report.Error(MapIssueCode.NoSpawnLine, "no spawn line");
		}

		if (document.CountOf(PrefabGroup.WeaponLocker) == 0)
		{
			report.Error(MapIssueCode.NoWeaponLocker, "no weapon locker");
		}

		int barracks = document.CountOf(PrefabGroup.Barracks);
		if (barracks < PrefabSpecs.MinOf(PrefabGroup.Barracks) || barracks > PrefabSpecs.MaxOf(PrefabGroup.Barracks))
		{
			report.Error(MapIssueCode.BarracksCount, $"{barracks} barracks; a map has 1 to {SimConfig.MaxBarracks}");
		}

		int nodes = document.CountOf(PrefabGroup.ResourceNode);
		if (nodes < PrefabSpecs.MinOf(PrefabGroup.ResourceNode) || nodes > PrefabSpecs.MaxOf(PrefabGroup.ResourceNode))
		{
			report.Error(MapIssueCode.NodeCount, $"{nodes} resource nodes; a map has 1 to {SimConfig.MaxResourceNodes}");
		}

		foreach ((PrefabGroup group, string what) in new[]
		{
			(PrefabGroup.Emplacement, "guns and launchers"), (PrefabGroup.AmmoCan, "ammunition cans"),
			(PrefabGroup.Cover, "cover boxes"), (PrefabGroup.Structure, "structures"),
			(PrefabGroup.VehicleKiosk, "vehicle kiosks"), (PrefabGroup.StrategistAnchor, "strategist anchors"),
		})
		{
			int count = document.CountOf(group);
			if (count > PrefabSpecs.MaxOf(group))
			{
				report.Error(MapIssueCode.TooMany, $"{count} {what}; a map holds at most {PrefabSpecs.MaxOf(group)}");
			}
		}
	}

	// ---- one object ------------------------------------------------------------------------

	private static void Object(MapDocument document, MapReport report, int index, MapObject placed, PrefabSpec spec)
	{
		Vector3 at = placed.Position;
		if (placed.Flags != 0)
		{
			report.Error(MapIssueCode.NumberOutOfRange, $"{Name(index, spec)} has flags {placed.Flags:x2}, none of which this build defines",
				index, -1, at);
		}

		foreach (MapParameter parameter in placed.Parameters)
		{
			if (!spec.TryGetParameter(parameter.Key, out PrefabParameter defined))
			{
				report.Error(MapIssueCode.NumberOutOfRange, $"{Name(index, spec)} has parameter {parameter.Key}, which a {spec.Name} has not",
					index, -1, at);
			}
			else if (parameter.Value < defined.Min || parameter.Value > defined.Max)
			{
				report.Error(MapIssueCode.NumberOutOfRange,
					$"{Name(index, spec)}'s {defined.Name} is {parameter.Value}, outside {defined.Min}–{defined.Max}", index, -1, at);
			}
		}

		if (PrefabPlacement.IsOutOfBounds(placed))
		{
			report.Error(MapIssueCode.OutOfBounds,
				spec.IsRoundPiece
					? $"{Name(index, spec)} is off the map: a round's piece stands inside ±{MapLimits.PlayHalfMeters:0} m and its body inside ±{MapLimits.FieldHalfMeters:0} m"
					: $"{Name(index, spec)} is off the map: it stands inside ±{MapLimits.FieldHalfMeters:0} m",
				index, -1, at);
		}

		if (spec.IsRoundPiece && PrefabPlacement.StandingHeight(placed) < document.Bands.WaterLevelMeters)
		{
			report.Error(MapIssueCode.UnderWater, $"{Name(index, spec)} is under the water", index, -1, at);
		}

		if (spec.MakesPad && PrefabSpecs.TryGetPadArea(placed, out PadArea area))
		{
			Pad(document, report, index, placed, spec, area);
		}
	}

	/// <summary>The samples under the footprint and within 1.5 m of it: level to 2 cm, and at the object's height to 2 cm.</summary>
	private static void Pad(MapDocument document, MapReport report, int index, MapObject placed, PrefabSpec spec,
		in PadArea area)
	{
		HeightField field = document.Terrain;
		SampleRect bounds = area.Bounds(field, TerrainConform.PadMarginMeters);
		float lowest = float.MaxValue;
		float highest = float.MinValue;
		double sum = 0d;
		int count = 0;
		for (int z = bounds.MinZ; z <= bounds.MaxZ; z++)
		{
			for (int x = bounds.MinX; x <= bounds.MaxX; x++)
			{
				if (area.DistanceTo(field.WorldX(x), field.WorldZ(z)) > TerrainConform.PadMarginMeters)
				{
					continue;
				}

				float height = field[x, z];
				lowest = MathF.Min(lowest, height);
				highest = MathF.Max(highest, height);
				sum += height;
				count++;
			}
		}

		if (count == 0)
		{
			return;
		}

		float mean = (float)(sum / count);
		Vector3 at = placed.Position;
		if (highest - lowest > PadToleranceMeters)
		{
			report.Error(MapIssueCode.OffItsPad,
				$"{Name(index, spec)} is not on its pad: the ground under it runs {lowest:0.00} … {highest:0.00} m", index, -1, at);
		}
		else if (MathF.Abs(mean - at.Y) > PadToleranceMeters)
		{
			report.Error(MapIssueCode.OffItsPad,
				$"{Name(index, spec)} is not on its pad: it stands at {at.Y:0.00} m and its pad is at {mean:0.00} m", index, -1, at);
		}
	}

	// ---- two objects -----------------------------------------------------------------------

	private static void InTheWay(MapDocument document, MapReport report, List<PlacedBox>[] boxes)
	{
		for (int i = 0; i < boxes.Length; i++)
		{
			if (boxes[i] == null)
			{
				continue;
			}

			for (int j = i + 1; j < boxes.Length; j++)
			{
				if (boxes[j] != null && PrefabSpecs.InTheWay(boxes[i], boxes[j]))
				{
					MapObject a = document.Objects[i];
					MapObject b = document.Objects[j];
					report.Error(MapIssueCode.InTheWay,
						$"{Name(i, PrefabSpecs.Get(a.PrefabId))} and {Name(j, PrefabSpecs.Get(b.PrefabId))} are in each other's way",
						i, j, a.Position);
				}
			}
		}
	}

	// ---- warnings --------------------------------------------------------------------------

	private static void Layout(MapDocument document, MapReport report)
	{
		HeightField field = document.Terrain;
		for (int i = 0; i < document.Objects.Count; i++)
		{
			MapObject placed = document.Objects[i];
			if (!PrefabSpecs.TryGet(placed.PrefabId, out PrefabSpec spec)
				|| (spec.Group != PrefabGroup.ResourceNode && spec.Group != PrefabGroup.SpawnLine))
			{
				continue;
			}

			Vector3 at = placed.Position;
			for (int j = 0; j < document.Objects.Count; j++)
			{
				MapObject barracks = document.Objects[j];
				if (barracks.PrefabId != PrefabSpecs.Barracks)
				{
					continue;
				}

				Vector3 apart = barracks.Position - at;
				float distance = MathF.Sqrt((apart.X * apart.X) + (apart.Z * apart.Z));
				if (distance < PrefabSpecs.BarracksDefendedRadiusMeters)
				{
					report.Warn(MapIssueCode.InsideDefendedRing,
						$"{Name(i, spec)} is {distance:0} m from barracks {j}, inside its defended ring of {PrefabSpecs.BarracksDefendedRadiusMeters:0} m",
						i, j, at);
				}
			}

			if (spec.Group == PrefabGroup.ResourceNode && PrefabSpecs.TryGetPadArea(placed, out PadArea disc))
			{
				SampleRect bounds = disc.Bounds(field, 0f);
				float lowest = float.MaxValue;
				float highest = float.MinValue;
				for (int z = bounds.MinZ; z <= bounds.MaxZ; z++)
				{
					for (int x = bounds.MinX; x <= bounds.MaxX; x++)
					{
						if (disc.Covers(field.WorldX(x), field.WorldZ(z)))
						{
							lowest = MathF.Min(lowest, field[x, z]);
							highest = MathF.Max(highest, field[x, z]);
						}
					}
				}

				if (highest - lowest > NodeReliefMeters)
				{
					report.Warn(MapIssueCode.NodeRelief,
						$"{Name(i, spec)} has {highest - lowest:0.0} m of relief inside its capture radius: a body may be on the pad and not count",
						i, -1, at);
				}
			}
		}
	}

	/// <summary>A bridge section's end that is neither joined to another's or to a road's end nor within 0.5 m of the ground.</summary>
	private static void Bridges(MapDocument document, MapReport report)
	{
		var points = new List<Vector3>();
		var owners = new List<int>();
		Span<Vector3> ends = stackalloc Vector3[4];
		Span<float> headings = stackalloc float[4];
		for (int i = 0; i < document.Objects.Count; i++)
		{
			int count = PrefabSpecs.Connectors(document.Objects[i], ends, headings);
			for (int c = 0; c < count; c++)
			{
				points.Add(ends[c]);
				owners.Add(i);
			}
		}

		for (int k = 0; k < points.Count; k++)
		{
			Vector3 end = points[k];
			if (MathF.Abs(end.Y - document.Terrain.Sample(end.X, end.Z)) <= LandedEndMeters)
			{
				continue;
			}

			bool joined = false;
			for (int m = 0; m < points.Count && !joined; m++)
			{
				Vector3 apart = points[m] - end;
				joined = owners[m] != owners[k] && (apart.X * apart.X) + (apart.Z * apart.Z) <= JointMeters * JointMeters
					&& MathF.Abs(apart.Y) <= JointMeters;
			}

			foreach (MapRoad road in document.Roads)
			{
				if (joined || road.Points.Length == 0)
				{
					break;
				}

				joined = Near(road.Points[0], end) || Near(road.Points[^1], end);
			}

			if (!joined)
			{
				int i = owners[k];
				report.Warn(MapIssueCode.OpenBridgeEnd,
					$"{Name(i, PrefabSpecs.Get(document.Objects[i].PrefabId))} has an open end, {end.Y - document.Terrain.Sample(end.X, end.Z):0.0} m over the ground",
					i, -1, end);
			}
		}
	}

	private static bool Near(RoadPoint point, Vector3 end)
	{
		float dx = point.XMeters - end.X;
		float dz = point.ZMeters - end.Z;
		return (dx * dx) + (dz * dz) <= RoadJointMeters * RoadJointMeters;
	}

	private static string Name(int index, PrefabSpec spec) => $"{spec.Name} {index}";

	// ---- reach (the deep check) ---------------------------------------------------------------

	/// <summary>
	/// §6.9's reach, the editor's deep check (§8.7, LE5): from every barracks door and every
	/// spawn line to every node, <paramref name="reaches"/> asked whether a unit gets from one
	/// to the other — which takes a navigation mesh, so the editor bakes and asks it, and a file
	/// being listed is never asked. Each pair it says no to is a warning, naming both and
	/// placed where the walk starts. Adds them to <paramref name="report"/>, and returns how
	/// many pairs were asked.
	/// </summary>
	public static int Reach(MapDocument document, Func<Vector3, Vector3, bool> reaches, MapReport report)
	{
		var starts = new List<(int Index, Vector3 At, string What)>();
		var nodes = new List<(int Index, Vector3 At)>();
		for (int i = 0; i < document.Objects.Count; i++)
		{
			MapObject placed = document.Objects[i];
			if (!PrefabSpecs.TryGet(placed.PrefabId, out PrefabSpec spec))
			{
				continue;
			}

			Vector3 at = placed.Position;
			at.Y = PrefabPlacement.StandingHeight(placed);
			if (spec.Group == PrefabGroup.ResourceNode)
			{
				nodes.Add((i, at));
			}
			else if (spec.Group == PrefabGroup.SpawnLine)
			{
				starts.Add((i, at, $"the spawn line {i}"));
			}
			else if (PrefabSpecs.TryGetDoor(placed, out Vector3 door))
			{
				door.Y = at.Y;
				starts.Add((i, door, $"the door of barracks {i}"));
			}
		}

		int asked = 0;
		foreach ((int from, Vector3 start, string what) in starts)
		{
			foreach ((int to, Vector3 node) in nodes)
			{
				asked++;
				if (!reaches(start, node))
				{
					report.Warn(MapIssueCode.Unreachable, $"no path from {what} to resource_node {to}", from, to, start);
				}
			}
		}

		return asked;
	}
}
