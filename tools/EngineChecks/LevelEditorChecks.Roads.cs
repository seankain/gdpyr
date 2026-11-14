using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Gdpyr.Core;
using Gdpyr.Level;
using Gdpyr.MapEditor;
using Gdpyr.Rts;
using Gdpyr.Sim;
using Gdpyr.Sim.Level;
using Godot;

namespace Gdpyr.EngineChecks;

/// <summary>
/// LE6, roads and bridges (docs/LEVEL_EDITOR.md §11), by the editor's own commands and in the world
/// a round builds from the file:
/// <list type="bullet">
/// <item><c>roads</c>: a process started with <c>--edit</c> and no name. A road drawn across a ridge with
/// the road tool is no steeper than its limit at any centreline sample, as the collider has the ground,
/// and is drawn where it is; one with two corners too; a point moved grades the new line; undone, every
/// sample is as it was. Then a river, three spans over it with the bridge tool, roads to both of its ends,
/// a span floating over dry ground on two piers, and a round's pieces: saved as <c>LE6_BRIDGE</c>; the
/// three spans taken up, saved as <c>LE6_GONE</c>. The deep check finds the walks across the bridge, and
/// none once it is gone.</item>
/// <item><c>bridge-round</c>: <c>--listen --map LE6_BRIDGE --no-bots</c>: the bridge and its piers are
/// where the file says, every pier's top at its deck and its foot under the ground; a span that floats
/// stays where it was put; a rifleman ordered across the river crosses it on the deck; and a dropship
/// ordered onto the floating span's deck lands beside it.</item>
/// <item><c>bridge-gone</c>: the same on <c>LE6_GONE</c>: the rifleman does not arrive.</item>
/// </list>
/// The 4x4 driven across the bridge by a scenario's script is <c>tools/level_editor_checks/le6_4x4_bridge.json</c>,
/// which the script plays on <c>LE6_BRIDGE</c> through <c>tools/Gdpyr.Playtest</c>.
/// </summary>
public partial class LevelEditorChecks
{
	// ---- the map LE6 makes, which its round parts and its scenario know by these numbers -----------------

	/// <summary>The river's middle is at x = 0, along z, from bank top to bank top 36 m: three spans.</summary>
	private const float RiverHalfMeters = 18f;

	/// <summary>The river's bed under level ground at 0 m, and the water a new map has at −5 m: 3 m deep.</summary>
	private const float RiverBedMeters = -8f;

	/// <summary>Where the rifleman starts and where it is ordered to, along z = 0, the bridge's line.</summary>
	private static readonly Vector3 LaneWest = new(-40f, 0f, 0f);
	private static readonly Vector3 LaneEast = new(40f, 0f, 0f);

	/// <summary>The span that floats over dry ground, on two piers: its point and its deck's top.</summary>
	private static readonly Vector3 FloatingSpan = new(60f, 6f, -80f);

	/// <summary>Where the dropship starts, on the east bank.</summary>
	private static readonly Vector3 DropshipStart = new(30f, 0f, -80f);

	private const float East = MathF.PI / 2f;

	/// <summary>The ground of a map with a river: level at 0 m, the bed at −8 m for |x| ≤ 10, the banks at 45°.</summary>
	private static float RiverGround(float x) => -Math.Clamp(RiverHalfMeters - MathF.Abs(x), 0f, -RiverBedMeters);

	/// <summary>A ridge along z, 12 m high at x = 0 and falling at 40 % either side to level ground: RoadPathTests' own.</summary>
	private static float RidgeGround(float x) => MathF.Max(0f, 12f - (0.4f * MathF.Abs(x)));

	/// <summary>A map of a ground written out here, saved under a name and opened in the editor.</summary>
	private bool OpenMade(MapEditorRoot editor, string name, Func<float, float, float> ground, out string error)
	{
		MapDocument document = MapDocument.Create(Guid.NewGuid(), name, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
		HeightField field = document.Terrain;
		for (int z = 0; z < field.Depth; z++)
		{
			for (int x = 0; x < field.Width; x++)
			{
				field[x, z] = ground(field.WorldX(x), field.WorldZ(z));
			}
		}

		field.Round(field.Bounds);
		error = null;
		return MapStore.TrySave(document, MapEditorRoot.Directory, name, 0, out _, out error) && editor.Open(name, out error);
	}

	/// <summary>The ground under (x, z) as the physics server has it: the first thing on layer 1 a ray down from 100 m meets.</summary>
	private float RayDown(float x, float z, out Node collider)
	{
		collider = null;
		PhysicsDirectSpaceState3D space = GetViewport().World3D.DirectSpaceState;
		var query = PhysicsRayQueryParameters3D.Create(new Vector3(x, 100f, z), new Vector3(x, -100f, z), CollisionLayers.World);
		Godot.Collections.Dictionary hit = space.IntersectRay(query);
		if (hit.Count == 0)
		{
			return float.NaN;
		}

		collider = hit["collider"].As<Node>();
		return hit["position"].AsVector3().Y;
	}

	/// <summary>
	/// The steepest grade between neighbouring centreline samples of a road, the ground at each by a ray
	/// down at the collider; and the most a ray's ground is off the document's (§6.1's Sample).
	/// </summary>
	private float SteepestAlong(RoadPath path, HeightField field, out float offDocument, out int samples)
	{
		float steepest = 0f;
		offDocument = 0f;
		samples = path.Count;
		float before = float.NaN;
		for (int i = 0; i < path.Count; i++)
		{
			Vector2 at = path.Points[i];
			float ground = RayDown(at.X, at.Y, out _);
			offDocument = MathF.Max(offDocument, MathF.Abs(ground - field.Sample(at.X, at.Y)));
			if (i > 0)
			{
				steepest = MathF.Max(steepest, MathF.Abs(ground - before) / path.Step);
			}

			before = ground;
		}

		return steepest;
	}

	/// <summary>Whether the drawn coverage of every sample is the document's roads', exactly.</summary>
	private static bool CoverageIsRoads(MapEditorRoot editor, out int samples)
	{
		float[] expected = RoadPath.Coverage(editor.Editing.Document);
		ReadOnlySpan<float> drawn = editor.Terrain.RoadCoverage;
		samples = editor.Terrain.RoadSamples;
		if (drawn.Length != expected.Length)
		{
			return false;
		}

		for (int i = 0; i < expected.Length; i++)
		{
			if (drawn[i] != expected[i])
			{
				return false;
			}
		}

		return true;
	}

	/// <summary>A road across the ridge, drawn and graded with the road tool, and held to its limit by the collider.</summary>
	private IEnumerable<bool> RidgeRoad(MapEditorRoot editor, string what, Vector2[] points, float width, float grade)
	{
		HeightField field = editor.Editing.Document.Terrain;
		editor.DropRoad();
		editor.SetRoadWidth(width);
		editor.SetRoadGrade(grade);
		foreach (Vector2 point in points)
		{
			editor.AddRoadPoint(point.X, point.Y);
		}

		RoadCheck preview = editor.RoadPreview;
		Fact(preview != null && preview.IsOk, $"{what}: the preview is §6.6's answer, {MapEditorControls.RoadPreview(preview, points.Length)}");
		int roads = editor.Editing.Document.Roads.Count;
		long started = Stopwatch.GetTimestamp();
		RoadCheck check = editor.ApplyRoad();
		double took = Ms(started);
		Fact(check != null && check.IsOk && editor.Editing.Document.Roads.Count == roads + 1 && editor.RoadDraft.Count == 0,
			$"{what}: Enter grades it as one step, in {F(took, "0")} ms: {check?.Result}");
		yield return true;
		yield return true;

		RoadPath path = RoadPath.Of(editor.Editing.Document.Roads[^1]);
		float steepest = SteepestAlong(path, field, out float off, out int samples);
		float allowed = grade + (2f * MapLimits.HeightStepMeters / path.Step);
		Fact(steepest <= allowed && off <= 0.001f,
			$"{what}: at each of its {samples} centreline samples, {F(path.Length, "0.0")} m, the collider's ground climbs at most"
			+ $" {F(steepest * 100f, "0.00")} % to the next, its limit {F(grade * 100f, "0")} % and a stored step either end"
			+ $" ({F(allowed * 100f, "0.00")} %); the rays meet the document's ground to {F(off * 1000f, "0.000")} mm");
		Measured($"{what}: deepest cut {F(check?.Path.DeepestCut ?? 0f)} m, fill {F(check?.Path.DeepestFill ?? 0f)} m, E after"
			+ $" {F(path.ExcessOf(field) * 100f, "0.00")} cm");
		Fact(CoverageIsRoads(editor, out int covered) && covered > 0,
			$"{what}: the ground is drawn with the roads' coverage, {covered} samples of it, and nowhere else");
		Fact(!editor.Editing.Report.Has(MapIssueCode.RoadOffItsGrade), $"{what}: the validator finds it at its grade");
	}

	private IEnumerable<bool> Roads()
	{
		long waiting = Stopwatch.GetTimestamp();
		while (MapEditorRoot.Current == null && Ms(waiting) < 30_000)
		{
			yield return true;
		}

		MapEditorRoot editor = MapEditorRoot.Current;
		Fact(editor != null, "the editor is up");
		if (editor == null)
		{
			yield break;
		}

		// ---- a road across a ridge -------------------------------------------------------------------
		string error;
		Fact(OpenMade(editor, Env("LE6_RIDGE", "le6-ridge"), (x, _) => RidgeGround(x), out error),
			$"a map that is a 12 m ridge with 40 % sides opened: {error ?? editor.Message}");
		if (editor.Editing == null)
		{
			yield break;
		}

		HeightField field = editor.Editing.Document.Terrain;
		short[] bare = field.ToSamples();
		editor.SelectTool(EditorTool.Roads);
		Fact(editor.Tool == EditorTool.Roads, "the road tool is selected: 3, and on the rail");

		foreach (bool step in RidgeRoad(editor, "a road across the ridge, 6 m at 10 %",
			new[] { new Vector2(-60f, 0f), new Vector2(60f, 0f) }, 6f, 0.10f))
		{
			yield return step;
		}

		foreach (bool step in RidgeRoad(editor, "a road with two corners across it, 4 m at 15 %",
			new[] { new Vector2(-70f, -70f), new Vector2(-20f, -45f), new Vector2(25f, -35f), new Vector2(70f, -15f) }, 4f, 0.15f))
		{
			yield return step;
		}

		// Its points moved: a click on the road takes them up, a point is moved, Enter grades the new line.
		float oldTop = field.Sample(0f, 0f);
		Fact(editor.SelectRoad(0) && editor.RoadDraft.Count == 2, "a click on the first road takes its two points up to move");
		editor.MoveRoadPoint(1, 60f, 30f);
		RoadCheck moved = editor.ApplyRoad();
		Fact(moved != null && moved.IsOk && editor.Editing.Document.Roads.Count == 2
			&& editor.Editing.Document.Roads[0].Points[1].Equals(RoadPoint.At(60f, 30f)) && field.Sample(0f, 0f) == oldTop,
			$"its end moved and graded again: the road keeps its place in the file, and the old line's ground stays ({F(oldTop)} m at its old middle)");
		yield return true;
		yield return true;
		float steepestMoved = SteepestAlong(RoadPath.Of(editor.Editing.Document.Roads[0]), field, out _, out _);
		Fact(steepestMoved <= 0.10f + (2f * MapLimits.HeightStepMeters / RoadPath.SampleMeters),
			$"the new line climbs at most {F(steepestMoved * 100f, "0.00")} % along it");

		// The roads and their ground undone.
		while (editor.Editing.History.CanUndo)
		{
			editor.Undo();
		}

		yield return true;
		bool colliderIsGround = ColliderIsGround(editor, out string where);
		Fact(editor.Editing.Document.Roads.Count == 0 && SameSamples(bare, field.ToSamples()) && editor.Terrain.RoadSamples == 0
			&& colliderIsGround,
			$"every road and its grading undone: no road, every sample the ridge's, none drawn as road, the collider the ground: {where}");

		// ---- a river, a bridge over it, and roads to its ends ------------------------------------------
		string bridgeName = Env("LE6_BRIDGE", "le6-bridge");
		Fact(OpenMade(editor, bridgeName, (x, _) => RiverGround(x), out error),
			$"a map with a river 3 m deep, under its water at -5 m, from bank top to bank top 36 m, opened: {error ?? editor.Message}");
		if (editor.Editing == null)
		{
			yield break;
		}

		MapDocument document = editor.Editing.Document;
		field = document.Terrain;
		editor.SelectTool(EditorTool.Prefabs);
		var pieces = new (ushort Id, float X, float Z, float YawDegrees)[]
		{
			(PrefabSpecs.SpawnLine, -95f, 60f, 90f), (PrefabSpecs.WeaponLocker, -95f, 80f, 0f),
			(PrefabSpecs.Barracks, 95f, 95f, 90f), (PrefabSpecs.Barracks, 95f, -40f, 90f),
			(PrefabSpecs.ResourceNode, -60f, 50f, 0f), (PrefabSpecs.ResourceNode, 60f, 50f, 0f), (PrefabSpecs.ResourceNode, -60f, -50f, 0f),
		};
		bool allPlaced = true;
		foreach ((ushort id, float x, float z, float yawDegrees) in pieces)
		{
			editor.SelectPrefab(id);
			editor.SetGhostYaw(yawDegrees * (MathF.PI / 180f));
			PlacementCheck check = editor.PlaceAt(x, z);
			allPlaced &= check.IsOk;
			if (!check.IsOk)
			{
				Fact(false, $"{PrefabSpecs.Get(id).Name} at ({x}, {z}): {check.Result}");
			}
		}

		Fact(allPlaced, $"a round's {pieces.Length} pieces placed with the prefab tool, either side of the river");

		// Three spans with the bridge tool: the first floating at the west bank's top, the others on its free end.
		editor.SelectTool(EditorTool.Bridges);
		editor.SelectSection(PrefabSpecs.BridgeSpan);
		editor.SetSupports(true);
		editor.SetFloatHeight(0f);
		editor.SetGhostYaw(East);
		int sections = document.Objects.Count;
		SectionCheck first = editor.PlaceSectionAt(-RiverHalfMeters + 6f, 0f);
		Fact(first != null && first.IsOk && first.Target == null && first.Piers == 2 && first.Landed == 1,
			$"the first span floats at the west bank's top, on 2 piers, its west end landed: {MapEditorControls.SectionGhost(PrefabSpecs.Get(PrefabSpecs.BridgeSpan), first, 0f)}");
		for (int k = 1; k < 3; k++)
		{
			float x = -RiverHalfMeters + (12f * k);
			SectionCheck ghost = editor.GhostSection(x - 0.6f, 0.7f);
			SectionCheck next = editor.PlaceSectionAt(x - 0.6f, 0.7f);
			Fact(next != null && next.IsOk && next.Target is { } joined && joined.Object == sections + k - 1 && next.Piers == 1
				&& ghost?.Result == next.Result && document.Objects[^1].Position.DistanceTo(new Vector3(x + 6f, 0f, 0f)) < 0.01f,
				$"span {k + 1}: a click within 3 m of the last span's free end joins it there, on 1 pier: the ghost's answer and the placing's,"
				+ $" {MapEditorControls.SectionGhost(PrefabSpecs.Get(PrefabSpecs.BridgeSpan), next, 0f)}");
		}

		// Roads from both of the bridge's ends, each joined to its connector and pinned to its deck.
		editor.SelectTool(EditorTool.Roads);
		editor.SetRoadWidth(6f);
		editor.SetRoadGrade(0.10f);
		editor.AddRoadPoint(-RiverHalfMeters - 1f, 0.8f);
		editor.AddRoadPoint(-90f, 0f);
		RoadCheck west = editor.ApplyRoad();
		editor.AddRoadPoint(RiverHalfMeters + 1f, -0.8f);
		editor.AddRoadPoint(90f, 0f);
		RoadCheck east = editor.ApplyRoad();
		Fact(west != null && west.IsOk && west.StartJoin?.Object == sections && east != null && east.IsOk
			&& east.StartJoin?.Object == sections + 2 && document.Roads.Count == 2,
			$"a road from each end of the bridge, joined to the end's connector: {west?.Result}, {east?.Result}");
		yield return true;
		yield return true;
		float westDeck = RayDown(-RiverHalfMeters - 0.25f, 0f, out _);
		float eastDeck = RayDown(RiverHalfMeters + 0.25f, 0f, out _);
		Fact(MathF.Abs(westDeck) <= 0.02f && MathF.Abs(eastDeck) <= 0.02f,
			$"the roads meet the decks flush: the ground a quarter metre off each end at {F(westDeck, "0.000")} and {F(eastDeck, "0.000")} m, the decks' top at 0 m");

		// A span that floats over dry ground, 6 m up, on two piers.
		editor.SelectTool(EditorTool.Bridges);
		editor.SetFloatHeight(FloatingSpan.Y);
		editor.SetGhostYaw(0f);
		SectionCheck floating = editor.PlaceSectionAt(FloatingSpan.X, FloatingSpan.Z);
		Fact(floating != null && floating.IsOk && floating.Target == null && floating.Piers == 2 && floating.Landed == 0,
			$"a span floating 6 m over dry ground, on 2 piers, no end landed: {MapEditorControls.SectionGhost(PrefabSpecs.Get(PrefabSpecs.BridgeSpan), floating, FloatingSpan.Y)}");

		MapReport report = editor.Editing.Report;
		Fact(report.IsPlayable && !report.Has(MapIssueCode.RoadOffItsGrade) && Count(report, MapIssueCode.OpenBridgeEnd) == 2,
			$"it passes the validator; its roads are at their grade, and its open ends are the floating span's two: {MapStore.Describe(report)}");

		// Its piers in the editor's world: the bridge's nodes and the document's piers alike.
		int piers = 0;
		bool piersHold = true;
		for (int i = 0; i < document.Objects.Count; i++)
		{
			MapObject section = document.Objects[i];
			if (PrefabSpecs.Get(section.PrefabId).Kind != PrefabKind.Bridge)
			{
				continue;
			}

			for (int end = 0; end < 2; end++)
			{
				int length = section.Parameter(end == 0 ? PrefabSpecs.PierBackKey : PrefabSpecs.PierFrontKey, 0);
				if (length > 0)
				{
					piers++;
					float lowest = BridgeRules.LowestUnder(field, BridgeRules.PierFootprint(section, end));
					float foot = BridgeRules.EndPoint(section, end).Y - PrefabSpecs.DeckThicknessMeters - (length / 100f);
					piersHold &= lowest - foot >= BridgeRules.PierFootMeters && lowest - foot <= BridgeRules.PierFootMeters + 0.0101f;
				}
			}
		}

		Fact(piers == 6 && piersHold, $"{piers} piers, each one's foot 0.50-0.51 m under the lowest ground beneath it, as the ground is after every landing and road");
		Fact(editor.Built.BridgeSections == 4 && editor.Built.Piers == 6,
			$"the editor's world has the 4 sections and 6 piers: {editor.Built.BridgeSections} and {editor.Built.Piers}");

		foreach (bool step in WaitForDeep(editor))
		{
			yield return step;
		}

		Fact(editor.DeepIsCurrent && editor.DeepWalks == 9 && Count(editor.DeepReport, MapIssueCode.Unreachable) == 0,
			$"the deep check: {editor.DeepWalks} walks from the spawn line and two barracks' doors to three nodes either side of the river,"
			+ $" none unreached: the bake goes over the bridge ({editor.DeepVertices} vertices)");
		Fact(editor.Save(out error), $"saved as {bridgeName}: {error ?? editor.Message}");
		File.WriteAllText(Path.Combine(_dir, "bridge-hash"), editor.SavedHash ?? string.Empty);

		// ---- the bridge taken up -----------------------------------------------------------------------------
		for (int k = 2; k >= 0; k--)
		{
			editor.Select(sections + k);
			Fact(editor.TakeUpSelected(), $"span {k + 1} selected and taken up");
		}

		Fact(document.Objects.Count == sections + 1 && document.Roads.Count == 2,
			"the three spans are gone; the floating span and the roads stay");
		foreach (bool step in WaitForDeep(editor))
		{
			yield return step;
		}

		int unreached = Count(editor.DeepReport, MapIssueCode.Unreachable);
		Fact(editor.DeepIsCurrent && editor.DeepWalks == 9 && unreached == 5,
			$"the deep check with the bridge gone: {editor.DeepWalks} walks, {unreached} unreached — every one across the river");
		string gone = Env("LE6_GONE", "le6-nobridge");
		Fact(editor.SaveAs(gone, out error), $"saved as {gone}: {error ?? editor.Message}");
		File.WriteAllText(Path.Combine(_dir, "gone-hash"), editor.SavedHash ?? string.Empty);
	}

	// ---- LE6: the bridge in a round --------------------------------------------------------------------

	/// <summary>The world's bridge sections by the file's order: <c>Geometry/Objects/BridgeNN</c>.</summary>
	private static List<(MapObject Section, StaticBody3D Node)> Sections(LevelRoot level)
	{
		var found = new List<(MapObject, StaticBody3D)>();
		int ordinal = 0;
		foreach (MapObject placed in level.Document.Objects)
		{
			if (PrefabSpecs.TryGet(placed.PrefabId, out PrefabSpec spec) && spec.Kind == PrefabKind.Bridge)
			{
				ordinal++;
				found.Add((placed, level.GetNodeOrNull<StaticBody3D>($"Geometry/Objects/Bridge{ordinal:00}")));
			}
		}

		return found;
	}

	/// <summary>The rifleman ordered from the west bank to the east along the bridge's line: whether it arrived, when, and how low it went over the river.</summary>
	private IEnumerable<bool> Crossing(UnitManager units, int seconds, Action<bool, double, float, float, Vector3> done)
	{
		float groundWest = LevelRoot.Current.Terrain.HeightAt(LaneWest.X, LaneWest.Z);
		ushort id = units.ServerPlaceUnit(UnitCatalog.Infantry, LaneWest + (Vector3.Up * (groundWest + 0.5f)), Team.Strategist, OrderKind.None);
		Unit unit = units.Find(id);
		if (unit == null)
		{
			done(false, 0, float.NaN, float.NaN, default);
			yield break;
		}

		for (int i = 0; i < 30; i++)
		{
			yield return true;
		}

		Vector3 goal = LaneEast + (Vector3.Up * LevelRoot.Current.Terrain.HeightAt(LaneEast.X, LaneEast.Z));
		unit.Order = new UnitOrder { Kind = OrderKind.Move, Target = goal, Anchor = goal, TargetOwnerId = OwnerId.None };
		long started = Stopwatch.GetTimestamp();
		int ticks = 0;
		float lowestOver = float.MaxValue;
		float furthest = float.MinValue;
		bool arrived = false;
		while (ticks < seconds * SimConfig.TickRate)
		{
			yield return true;
			ticks++;
			Vector3 at = unit.GlobalPosition;
			furthest = MathF.Max(furthest, at.X);
			if (MathF.Abs(at.X) < RiverHalfMeters - 8f)
			{
				lowestOver = MathF.Min(lowestOver, at.Y);
			}

			if (new Vector2(at.X - goal.X, at.Z - goal.Z).Length() < 1.5f)
			{
				arrived = true;
				break;
			}
		}

		done(arrived, ticks / (double)SimConfig.TickRate, lowestOver, furthest, unit.GlobalPosition);
		units.ClearUnits();
	}

	private IEnumerable<bool> BridgeRound()
	{
		long waiting = Stopwatch.GetTimestamp();
		UnitManager units = UnitManager.Instance;
		while ((LevelRoot.Current?.Terrain == null || units == null || !units.NavigationReady) && Ms(waiting) < 120_000)
		{
			units ??= UnitManager.Instance;
			yield return true;
		}

		LevelRoot level = LevelRoot.Current;
		Fact(level != null && units != null && units.NavigationReady && Session.Map.IsData
			&& Session.Map.Hash == ReadMark("bridge-hash"),
			$"the map with the bridge is the world, and its navigation mesh is baked: {Session.Map}");
		if (level == null || units == null)
		{
			yield break;
		}

		HeightField field = level.Document.Terrain;
		List<(MapObject Section, StaticBody3D Node)> sections = Sections(level);
		Fact(sections.Count == 4 && level.Built.BridgeSections == 4 && level.Built.Piers == 6,
			$"the round built the file's 4 sections and 6 piers: {level.Built}");

		// Each section where the file put it, and each pier's top at its deck and its foot under the ground.
		bool posed = true;
		int piers = 0;
		bool topsAtDecks = true;
		float shallowest = float.MaxValue;
		foreach ((MapObject section, StaticBody3D node) in sections)
		{
			PrefabSpec spec = PrefabSpecs.Get(section.PrefabId);
			posed &= node != null && node.GlobalTransform.Origin.DistanceTo(section.Position) < 0.001f
				&& MathF.Abs(Mathf.Wrap(node.GlobalRotation.Y - section.YawRadians, -MathF.PI, MathF.PI)) < 0.001f;
			if (node == null)
			{
				continue;
			}

			foreach (Node child in node.GetChildren())
			{
				if (child is not CollisionShape3D { Shape: BoxShape3D box } shape || MathF.Abs(box.Size.X - PrefabSpecs.PierSideMeters) > 1e-4f
					|| MathF.Abs(box.Size.Z - PrefabSpecs.PierSideMeters) > 1e-4f)
				{
					continue;
				}

				piers++;
				Vector3 centre = shape.GlobalTransform.Origin;
				float top = centre.Y + (box.Size.Y * 0.5f);
				float foot = centre.Y - (box.Size.Y * 0.5f);
				var plan = new Footprint(centre, section.YawRadians, PrefabSpecs.PierSideMeters * 0.5f, PrefabSpecs.PierSideMeters * 0.5f);
				int end = new StructurePose(section.Position, section.YawRadians).ToLocal(centre).Z < 0f ? 0 : 1;
				float deck = BridgeRules.EndPoint(section, end).Y - PrefabSpecs.DeckThicknessMeters;
				topsAtDecks &= MathF.Abs(top - deck) < 0.001f;
				shallowest = MathF.Min(shallowest, BridgeRules.LowestUnder(field, plan) - foot);
			}
		}

		Fact(posed, "every section stands where the file put it, turned by its yaw");
		Fact(piers == 6 && topsAtDecks && shallowest >= BridgeRules.PierFootMeters - 0.001f,
			$"{piers} pier colliders: each one's top at its deck's underside to the millimetre, and its foot under the ground,"
			+ $" {F(shallowest)} m under the lowest of it beneath it at the least");

		// The span that floats: where it was put, a ray down meets its deck, and it stays.
		(MapObject floatingSection, StaticBody3D floatingNode) = sections.Find(s => s.Section.Position.DistanceTo(FloatingSpan) < 0.01f);
		float deckHit = RayDown(FloatingSpan.X, FloatingSpan.Z, out Node hitNode);
		Fact(floatingNode != null && MathF.Abs(deckHit - FloatingSpan.Y) < 0.01f && hitNode == floatingNode,
			$"the floating span's deck is met by a ray down at {F(deckHit, "0.000")} m, where the file put it");

		// A rifleman across the river.
		bool arrived = false;
		double took = 0;
		float lowest = float.NaN;
		foreach (bool step in Crossing(units, 90, (a, t, low, _, _) => (arrived, took, lowest) = (a, t, low)))
		{
			yield return step;
		}

		Fact(arrived && lowest >= -0.6f,
			$"a rifleman ordered from x {F(LaneWest.X, "0")} to {F(LaneEast.X, "0")} across the river crosses it in {F(took, "0.0")} s,"
			+ $" no lower over the river than {F(lowest)} m: on the deck, whose top is 0 m");

		// A dropship ordered onto the floating span's deck lands beside it.
		ushort shipId = units.ServerPlaceUnit(UnitCatalog.Dropship, DropshipStart + (Vector3.Up * 1f), Team.Strategist, OrderKind.None);
		Unit ship = units.Find(shipId);
		bool flew = false;
		bool landed = false;
		if (ship != null)
		{
			for (int i = 0; i < 30; i++)
			{
				yield return true;
			}

			ship.Order = new UnitOrder { Kind = OrderKind.Move, Target = FloatingSpan, Anchor = FloatingSpan, TargetOwnerId = OwnerId.None };
			for (int tick = 0; tick < 90 * SimConfig.TickRate && !landed; tick++)
			{
				yield return true;
				flew |= ship.Phase != FlightPhase.Grounded;
				landed = flew && ship.Phase == FlightPhase.Grounded;
			}
		}

		Vector3 down = ship?.GlobalPosition ?? default;
		var deckPlan = new Footprint(FloatingSpan, floatingSection?.YawRadians ?? 0f, PrefabSpecs.DeckWidthMeters * 0.5f,
			PrefabSpecs.DeckLengthMeters * 0.5f);
		float ground = level.Terrain.HeightAt(down.X, down.Z);
		float apart = new Vector2(down.X - FloatingSpan.X, down.Z - FloatingSpan.Z).Length();
		Fact(landed && !deckPlan.Contains(down) && MathF.Abs(down.Y - ground) < 0.5f && apart < 30f,
			$"a dropship ordered onto the floating span's deck flies, and lands beside it on the ground: at ({F(down.X, "0.0")}, {F(down.Y)},"
			+ $" {F(down.Z, "0.0")}), {F(apart, "0.0")} m from the deck's middle, the ground there at {F(ground)} m");

		deckHit = RayDown(FloatingSpan.X, FloatingSpan.Z, out hitNode);
		Fact(floatingNode != null && floatingNode.GlobalTransform.Origin.DistanceTo(floatingSection.Position) < 0.001f
			&& MathF.Abs(deckHit - FloatingSpan.Y) < 0.01f && hitNode == floatingNode,
			$"and after all that the floating span stays where it was put: its deck met at {F(deckHit, "0.000")} m");
		WriteDone("bridge-round");
	}

	private IEnumerable<bool> BridgeGone()
	{
		long waiting = Stopwatch.GetTimestamp();
		UnitManager units = UnitManager.Instance;
		while ((LevelRoot.Current?.Terrain == null || units == null || !units.NavigationReady) && Ms(waiting) < 120_000)
		{
			units ??= UnitManager.Instance;
			yield return true;
		}

		LevelRoot level = LevelRoot.Current;
		Fact(level != null && units != null && units.NavigationReady && Session.Map.Hash == ReadMark("gone-hash")
			&& level.Built.BridgeSections == 1,
			$"the map with the bridge taken up is the world, one section left, and its navigation mesh is baked: {Session.Map}");
		if (level == null || units == null)
		{
			yield break;
		}

		bool arrived = true;
		float furthest = float.NaN;
		Vector3 ended = default;
		foreach (bool step in Crossing(units, 90, (a, _, _, far, at) => (arrived, furthest, ended) = (a, far, at)))
		{
			yield return step;
		}

		Fact(!arrived && furthest < 0f,
			$"the rifleman ordered across does not arrive in 90 s: it got no further east than x {F(furthest, "0.0")},"
			+ $" and stood at ({F(ended.X, "0.0")}, {F(ended.Y)}, {F(ended.Z, "0.0")}), the river's west side");
		WriteDone("bridge-gone");
	}

	/// <summary>What a mark another part wrote says; empty when there is none.</summary>
	private string ReadMark(string name) =>
		!string.IsNullOrEmpty(_dir) && File.Exists(Path.Combine(_dir, name)) ? File.ReadAllText(Path.Combine(_dir, name)).Trim() : string.Empty;

	/// <summary>A part's facts, for the script to read: a process can crash as it exits, after every fact is in (§13).</summary>
	private void WriteDone(string part)
	{
		if (!string.IsNullOrEmpty(_dir))
		{
			File.WriteAllText(Path.Combine(_dir, $"{part}-done"), $"{_facts - _failures} of {_facts}");
		}
	}
}
