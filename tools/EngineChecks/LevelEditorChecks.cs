using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using Gdpyr.Core;
using Gdpyr.Level;
using Gdpyr.MapEditor;
using Gdpyr.Match;
using Gdpyr.Rts;
using Gdpyr.Sim;
using Gdpyr.Sim.Level;
using Godot;

namespace Gdpyr.EngineChecks;

/// <summary>
/// LE4, the editor: shell and terrain (docs/LEVEL_EDITOR.md §11). The editor driven with no
/// window through its own commands — the methods its panels and keys call — and held to its
/// document: a stroke changes the samples under it and no others, and the drawn ground and
/// the collider are the document's after it; undo restores every sample, and redo the stroke;
/// save then open is the same document; an autosave is offered back; a clone of the bundled
/// map keeps its pads under a stroke; and Test starts a second process on the saved file,
/// which reports the round live. Run by <c>scripts/level-editor-checks.sh editor</c> on a copy
/// of the tree with this file added as an autoload; the game never compiles or loads it.
///
/// <c>LE4_CHECK</c> names this process's part:
/// <list type="bullet">
/// <item><c>editor</c>: a process started with <c>--edit &lt;LE4_NEW&gt;</c>, a map that is not
/// there yet, under a <c>HOME</c> of its own so that nobody's maps are touched.</item>
/// <item><c>shot</c>: a drawn editor, under a virtual display, started with <c>--edit</c> and
/// no name: its list, then a clone of the bundled map with the ring under the cursor, a
/// stroke, and the slope overlay, each saved as a picture in <c>LE4_DIR</c>. For a person to
/// look at; nothing here is a fact about how it looks.</item>
/// <item><c>prefabs</c> (LE5): a process started with <c>--edit</c> and no name. Every prefab of
/// the catalog placed on a 6° slope stands on a level pad with its colliders inside its boxes;
/// each of §6.5's answers comes of a placement built to get it, the ghost giving the same; an
/// object is selected, turned, given a parameter and taken up; the deep check finds every walk
/// on the slope and none to a node on an island; and then a map made only in the editor — two
/// barracks, three nodes, cover and a pillbox standing at the start — passes the validator and
/// the deep check, and is tested.</item>
/// <item><c>round</c> (LE5): that map's test process, playing it with bots for <c>LE5_SECONDS</c>
/// (600 unless given): the pillbox is put up at the round's start, as destructible as one a
/// builder made and inside its boxes, fires, and is destroyed.</item>
/// <item><c>test-child</c>: the process the editor's Test starts. It is told by its
/// environment, which it inherits, what file it must be playing and what ground it must
/// stand on, and leaves a mark in <c>LE4_DIR</c> once the round is live.</item>
/// </list>
/// </summary>
public partial class LevelEditorChecks : Node
{
	/// <summary>How far a ray's hit may be from §6.1's Sample [m].</summary>
	private const float RayTolerance = 0.001f;

	private string _role;
	private string _dir;
	private IEnumerator<bool> _run;
	private int _failures;
	private int _facts;
	private EditOutcome _outcome;
	private readonly List<double> _strokeFrameMs = new();
	private readonly List<double> _strokeEndMs = new();
	private readonly List<double> _colliderMs = new();

	public override void _Ready()
	{
		_role = System.Environment.GetEnvironmentVariable("LE4_CHECK");
		_dir = System.Environment.GetEnvironmentVariable("LE4_DIR");
		if (string.IsNullOrEmpty(_role))
		{
			SetPhysicsProcess(false);
			return;
		}

		ProcessMode = ProcessModeEnum.Always;
		_run = _role switch
		{
			"editor" => Editor().GetEnumerator(),
			"test-child" => TestChild().GetEnumerator(),
			"shot" => Shot().GetEnumerator(),
			"prefabs" => Prefabs().GetEnumerator(),
			"round" => Round().GetEnumerator(),
			_ => null,
		};

		if (_run == null)
		{
			GD.PrintErr($"[checks] no such part: {_role}");
			SetPhysicsProcess(false);
			GetTree().Quit(99);
			return;
		}

		GD.Print($"[checks] level editor check: {_role}");
	}

	public override void _PhysicsProcess(double delta)
	{
		bool more;
		try
		{
			more = _run.MoveNext();
		}
		catch (Exception e)
		{
			GD.PrintErr($"[checks] EXCEPTION {e}");
			_failures++;
			more = false;
		}

		if (!more)
		{
			GD.Print($"[checks] {_role}: {_facts - _failures} of {_facts} facts hold");
			Mark($"finished-{_role}");
			SetPhysicsProcess(false);
			GetTree().Quit(_failures);
		}
	}

	// ---- plumbing ----------------------------------------------------------------------------

	private void Fact(bool ok, string what)
	{
		_facts++;
		if (!ok)
		{
			_failures++;
		}

		GD.Print($"[checks] {(ok ? "PASS" : "FAIL")} {_role}: {what}");
	}

	private void Measured(string what) => GD.Print($"[checks] measured {_role}: {what}");

	private static string F(double v, string format = "0.00") => v.ToString(format, CultureInfo.InvariantCulture);

	private static double Ms(long from) => (Stopwatch.GetTimestamp() - from) * 1000.0 / Stopwatch.Frequency;

	private void Mark(string name)
	{
		if (!string.IsNullOrEmpty(_dir))
		{
			File.WriteAllText(Path.Combine(_dir, name), string.Empty);
		}
	}

	private bool Has(string name) => !string.IsNullOrEmpty(_dir) && File.Exists(Path.Combine(_dir, name));

	private static string Env(string name, string otherwise) =>
		System.Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : otherwise;

	private static string Stats(List<double> samples)
	{
		if (samples.Count == 0)
		{
			return "no samples";
		}

		var sorted = new List<double>(samples);
		sorted.Sort();
		double sum = 0;
		foreach (double s in sorted)
		{
			sum += s;
		}

		return $"n {sorted.Count}, mean {F(sum / sorted.Count, "0.000")} ms, p95 {F(sorted[Math.Min((int)(0.95 * sorted.Count), sorted.Count - 1)], "0.000")},"
			+ $" max {F(sorted[^1], "0.000")}";
	}

	// ---- what the world is held to --------------------------------------------------------------

	/// <summary>Every drawn chunk's vertices are the document's samples, bit for bit.</summary>
	private static bool GroundDrawn(MapEditorRoot editor, out string where)
	{
		TerrainNode terrain = editor.Terrain;
		HeightField field = editor.Editing.Document.Terrain;
		for (int cz = 0; cz < terrain.ChunksDown; cz++)
		{
			for (int cx = 0; cx < terrain.ChunksAcross; cx++)
			{
				Vector3[] vertices = terrain.ChunkVertices(cx, cz);
				if (vertices == null)
				{
					where = $"chunk ({cx}, {cz}) is not drawn";
					return false;
				}

				const int Side = TerrainNode.ChunkCells + 1;
				for (int i = 0; i < vertices.Length; i++)
				{
					Vector3 expected = field.Position((cx * TerrainNode.ChunkCells) + (i % Side), (cz * TerrainNode.ChunkCells) + (i / Side));
					if (vertices[i] != expected)
					{
						where = $"chunk ({cx}, {cz}) vertex {i}: {vertices[i]} where the document has {expected}";
						return false;
					}
				}
			}
		}

		where = "every vertex of 225 chunks";
		return true;
	}

	/// <summary>Every collider chunk's triangles are the document's, as <see cref="TerrainNode.ChunkFaces"/> makes them.</summary>
	private static bool ColliderIsGround(MapEditorRoot editor, out string where)
	{
		TerrainNode terrain = editor.Terrain;
		HeightField field = editor.Editing.Document.Terrain;
		for (int cz = 0; cz < terrain.ChunksDown; cz++)
		{
			for (int cx = 0; cx < terrain.ChunksAcross; cx++)
			{
				Vector3[] faces = terrain.ColliderFaces(cx, cz);
				Vector3[] expected = TerrainNode.ChunkFaces(field, cx, cz);
				if (faces.Length != expected.Length)
				{
					where = $"chunk ({cx}, {cz}) has {faces.Length} vertices of faces, not {expected.Length}";
					return false;
				}

				for (int i = 0; i < faces.Length; i++)
				{
					if (faces[i] != expected[i])
					{
						where = $"chunk ({cx}, {cz}) face vertex {i}: {faces[i]} where the document has {expected[i]}";
						return false;
					}
				}
			}
		}

		where = "every triangle of 225 chunks";
		return true;
	}

	/// <summary>Rays straight down at the points meet the ground where §6.1's Sample says. Returns the worst miss [m]; infinity when one met nothing.</summary>
	private float RaysDown(MapEditorRoot editor, List<Vector2> points)
	{
		PhysicsDirectSpaceState3D space = GetTree().Root.World3D.DirectSpaceState;
		HeightField field = editor.Editing.Document.Terrain;
		float worst = 0f;
		foreach (Vector2 p in points)
		{
			// Through anything standing on the ground — a cover box — to the ground's own body.
			var query = PhysicsRayQueryParameters3D.Create(new Vector3(p.X, 100f, p.Y), new Vector3(p.X, -50f, p.Y),
				CollisionLayers.World);
			var past = new Godot.Collections.Array<Rid>();
			Godot.Collections.Dictionary hit = null;
			for (int tries = 0; tries < 8; tries++)
			{
				hit = space.IntersectRay(query);
				if (hit.Count == 0 || hit["collider"].AsGodotObject() == editor.Terrain)
				{
					break;
				}

				past.Add(hit["rid"].AsRid());
				query.Exclude = past;
			}

			if (hit == null || hit.Count == 0 || hit["collider"].AsGodotObject() != editor.Terrain)
			{
				return float.PositiveInfinity;
			}

			worst = MathF.Max(worst, MathF.Abs(hit["position"].AsVector3().Y - field.Sample(p.X, p.Y)));
		}

		return worst;
	}

	private static List<Vector2> Points(Random random, int count, Vector2 centre, float radius)
	{
		var points = new List<Vector2>(count);
		for (int i = 0; i < count; i++)
		{
			float angle = (float)(random.NextDouble() * Math.PI * 2d);
			float r = radius * MathF.Sqrt((float)random.NextDouble());
			Vector2 p = centre + new Vector2(MathF.Cos(angle) * r, MathF.Sin(angle) * r);
			points.Add(new Vector2(Math.Clamp(p.X, -119.5f, 119.5f), Math.Clamp(p.Y, -119.5f, 119.5f)));
		}

		return points;
	}

	/// <summary>
	/// Holds the world to the document: drawn, collider, and rays a physics frame later. Each a fact.
	/// </summary>
	private IEnumerable<bool> WorldIsDocument(MapEditorRoot editor, string when, List<Vector2> rays)
	{
		Fact(GroundDrawn(editor, out string drawn), $"{when}: the drawn ground is the document's ({drawn})");
		Fact(ColliderIsGround(editor, out string collider), $"{when}: the collider is the document's ({collider})");
		yield return true;
		yield return true;
		float worst = RaysDown(editor, rays);
		Fact(worst <= RayTolerance, $"{when}: {rays.Count} rays straight down meet the ground where the document says, the worst {F(worst * 1000f, "0.000")} mm off");
	}

	// ---- a stroke --------------------------------------------------------------------------------

	/// <summary>
	/// A stroke, as the mouse makes one: pressed at <paramref name="from"/>, dragged to
	/// <paramref name="to"/> over <paramref name="frames"/> frames of 1/60 s, let go. While it is
	/// down the drawn ground follows the document every tenth frame. Its outcome is <see cref="_outcome"/>.
	/// </summary>
	private IEnumerable<bool> Stroke(MapEditorRoot editor, string name, BrushSettings brush, Vector2 from, Vector2 to,
		bool lower, int frames, List<Vector2> dabbed)
	{
		editor.SetBrush(brush);
		Fact(editor.BeginStroke(from.X, from.Y, lower), $"{name}: the stroke begins");
		dabbed.Add(from);
		bool drawnDuring = true;
		string where = string.Empty;
		for (int f = 1; f <= frames; f++)
		{
			Vector2 p = from.Lerp(to, f / (float)frames);
			int before = editor.Editing.StrokeDabs;
			long started = Stopwatch.GetTimestamp();
			editor.StrokeTo(p.X, p.Y, 1f / 60f);
			_strokeFrameMs.Add(Ms(started));
			if (editor.Editing.StrokeDabs > before)
			{
				dabbed.Add(p);
			}

			if (f % 10 == 0 && drawnDuring)
			{
				drawnDuring = GroundDrawn(editor, out where);
			}

			yield return true;
		}

		Fact(drawnDuring, $"{name}: while the button is down, the drawn ground follows the document" + (drawnDuring ? string.Empty : $": {where}"));
		long ending = Stopwatch.GetTimestamp();
		_outcome = editor.EndStroke();
		_strokeEndMs.Add(Ms(ending));
		_colliderMs.Add(editor.Terrain.LastColliderMilliseconds);
	}

	/// <summary>
	/// A stroke on a map, held to "the samples under it and no others": every stored sample that
	/// changed is within the brush's radius of a point it dabbed at, some did, it is one step to
	/// undo, the meshes outside its reach were not drawn again, and the world is the document's.
	/// </summary>
	private IEnumerable<bool> HeldStroke(MapEditorRoot editor, string name, BrushSettings brush, Vector2 from, Vector2 to,
		bool lower, int frames, Random random)
	{
		HeightField field = editor.Editing.Document.Terrain;
		short[] before = field.ToSamples();
		int steps = editor.Editing.History.UndoCount;
		TerrainNode terrain = editor.Terrain;
		var drawn = new Vector3[terrain.ChunksAcross * terrain.ChunksDown][];
		for (int i = 0; i < drawn.Length; i++)
		{
			drawn[i] = terrain.ChunkVertices(i % terrain.ChunksAcross, i / terrain.ChunksAcross);
		}

		var dabbed = new List<Vector2>();
		foreach (bool step in Stroke(editor, name, brush, from, to, lower, frames, dabbed))
		{
			yield return step;
		}

		short[] after = field.ToSamples();
		int changed = 0;
		int outside = 0;
		float radius = editor.Editing.Brush.RadiusMeters;
		for (int i = 0; i < after.Length; i++)
		{
			if (after[i] == before[i])
			{
				continue;
			}

			changed++;
			float x = field.WorldX(i % field.Width);
			float z = field.WorldZ(i / field.Width);
			bool near = false;
			foreach (Vector2 p in dabbed)
			{
				near |= ((x - p.X) * (x - p.X)) + ((z - p.Y) * (z - p.Y)) <= (radius * radius) + 1e-3f;
			}

			outside += near ? 0 : 1;
		}

		Fact(changed > 0 && outside == 0, $"{name}: {changed} samples changed, every one within the brush's {F(radius, "0.#")} m of"
			+ $" one of its {dabbed.Count} dab points; {outside} beyond");
		Fact(_outcome.Step && editor.Editing.History.UndoCount == steps + 1,
			$"{name}: one step to undo ({_outcome})");

		terrain.ChunksOf(_outcome.Area.Grown(1), out int minX, out int minZ, out int maxX, out int maxZ);
		int kept = 0;
		int redrawnOutside = 0;
		for (int i = 0; i < drawn.Length; i++)
		{
			int cx = i % terrain.ChunksAcross;
			int cz = i / terrain.ChunksAcross;
			bool reached = cx >= minX && cx <= maxX && cz >= minZ && cz <= maxZ;
			bool same = ReferenceEquals(drawn[i], terrain.ChunkVertices(cx, cz));
			kept += same ? 1 : 0;
			redrawnOutside += !reached && !same ? 1 : 0;
		}

		Fact(redrawnOutside == 0, $"{name}: no mesh chunk out of its reach was drawn again; {drawn.Length - kept} of {drawn.Length} were");

		var rays = Points(random, 150, from.Lerp(to, 0.5f), radius + from.DistanceTo(to));
		rays.AddRange(Points(random, 50, Vector2.Zero, 110f));
		foreach (bool step in WorldIsDocument(editor, name, rays))
		{
			yield return step;
		}
	}

	// ---- the editor -----------------------------------------------------------------------------

	private IEnumerable<bool> Editor()
	{
		string name = Env("LE4_NEW", "le4-new");
		string hills = Env("LE4_HILLS", "le4-hills");
		var random = new Random(4);

		long waiting = Stopwatch.GetTimestamp();
		while ((MapEditorRoot.Current?.Editing == null) && Ms(waiting) < 30_000)
		{
			yield return true;
		}

		MapEditorRoot editor = MapEditorRoot.Current;
		Fact(editor?.Editing != null, $"--edit {name} opens the editor on a map");
		if (editor?.Editing == null)
		{
			yield break;
		}

		Fact(!Session.InGame, "the session stays in its menu phase: nothing is simulated");
		Fact(editor.FileName == name && !editor.EverSaved && editor.Editing.Document.Name == name,
			$"a map that is not there is a new one by that name, not saved yet ({editor.FileName}, '{editor.Editing.Document.Name}')");
		Fact(!editor.Editing.Report.IsPlayable,
			$"a new map does not play, and is still a map to edit: {MapStore.Describe(editor.Editing.Report)}");
		Fact(editor.Terrain.ShapeCount == 225 && editor.Terrain.MeshChunks == 225,
			$"the ground is the round's: {editor.Terrain.ShapeCount} collider chunks, {editor.Terrain.MeshChunks} drawn");
		foreach (bool step in WorldIsDocument(editor, "opened", Points(random, 200, Vector2.Zero, 110f)))
		{
			yield return step;
		}

		HeightField field = editor.Editing.Document.Terrain;
		short[] initial = field.ToSamples();
		var states = new List<short[]> { initial };

		// ---- strokes, every family of brush --------------------------------------------------------
		var strokes = new (string Name, int Preset, Vector2 From, Vector2 To, bool Lower, int Frames)[]
		{
			("sculpt (Hill), raised along a line", 1, new Vector2(-60f, -60f), new Vector2(-40f, -50f), false, 60),
			("grade-locked (Mound 30°), held still", 6, new Vector2(40f, 40f), new Vector2(40f, 40f), false, 120),
			("grade-locked (Cliff 60°), lowered", 7, new Vector2(40f, -50f), new Vector2(44f, -50f), true, 90),
			("smooth, over the first", 8, new Vector2(-55f, -58f), new Vector2(-45f, -52f), false, 40),
			("level, over the first", 9, new Vector2(-48f, -50f), new Vector2(-48f, -50f), false, 30),
			("sculpt (Bluff), at the field's edge", 3, new Vector2(-116f, 10f), new Vector2(-116f, 20f), false, 45),
		};

		foreach (var stroke in strokes)
		{
			int builds = editor.ObjectBuilds;
			foreach (bool step in HeldStroke(editor, stroke.Name, TerrainBrush.Presets[stroke.Preset].Settings, stroke.From,
				stroke.To, stroke.Lower, stroke.Frames, random))
			{
				yield return step;
			}

			states.Add(field.ToSamples());
			if (stroke.Name.Contains("edge", StringComparison.Ordinal))
			{
				Fact(editor.ObjectBuilds == builds + 1, "a stroke at the field's edge builds the walls again, which span its ground");
			}
		}

		// A ramp: two clicks, from the level ground up the mound's side.
		BrushSettings ramp = TerrainBrush.Presets[10].Settings;
		ramp.RadiusMeters = 3f;
		editor.SetBrush(ramp);
		RampResult made = editor.Ramp(new Vector2(22f, 40f), new Vector2(38f, 40f));
		Fact(made == RampResult.Ok && editor.Editing.History.UndoCount == states.Count,
			$"a ramp up the mound is made and is one step: {made}, {editor.Editing.History.UndoCount} steps");
		states.Add(field.ToSamples());
		foreach (bool step in WorldIsDocument(editor, "the ramp", Points(random, 100, new Vector2(30f, 40f), 12f)))
		{
			yield return step;
		}

		Measured($"a frame of a stroke, its dabs and the meshes drawn again: {Stats(_strokeFrameMs)}");
		Measured($"a stroke's end, rounded, pads kept, collider given its triangles: {Stats(_strokeEndMs)}");
		Measured($"the collider chunks at a stroke's end: {Stats(_colliderMs)}");
		Measured($"mesh chunks drawn again, all told: {editor.Terrain.MeshesRedrawn}; collider chunks reset: {editor.Terrain.CollidersReset}");

		// ---- undo, redo ----------------------------------------------------------------------------
		bool undone = true;
		for (int i = states.Count - 1; i > 0; i--)
		{
			undone &= editor.Undo() && SameSamples(states[i - 1], field.ToSamples());
		}

		Fact(undone && !editor.Undo() && SameSamples(initial, field.ToSamples()),
			$"undoing {states.Count - 1} steps restores every sample, step by step, to the new map's");
		var rays = Points(random, 300, Vector2.Zero, 118f);
		foreach (bool step in WorldIsDocument(editor, "all undone", rays))
		{
			yield return step;
		}

		bool redone = true;
		for (int i = 1; i < states.Count; i++)
		{
			redone &= editor.Redo() && SameSamples(states[i], field.ToSamples());
		}

		Fact(redone && !editor.Redo(), $"redoing them makes each again, sample for sample");
		foreach (bool step in WorldIsDocument(editor, "all redone", rays))
		{
			yield return step;
		}

		// ---- the keys ------------------------------------------------------------------------------
		editor.SelectPreset(1);
		float radius = editor.Editing.Brush.RadiusMeters;
		Press(Key.Bracketright, false);
		yield return true;
		Fact(MathF.Abs(editor.Editing.Brush.RadiusMeters - (radius * MapEditorControls.ScaleStep)) < 1e-4f,
			$"] widens the brush: {F(radius, "0.##")} m to {F(editor.Editing.Brush.RadiusMeters, "0.##")} m");
		Press(Key.O, false);
		yield return true;
		Fact(editor.Overlay == TerrainOverlay.Bands && OverlayOnChunk(editor) == (int)TerrainOverlay.Bands,
			$"O is the next overlay, in the shader: {editor.Overlay}, the chunk's is {OverlayOnChunk(editor)}");
		int stepsNow = editor.Editing.History.UndoCount;
		Press(Key.Z, true);
		yield return true;
		Fact(editor.Editing.History.UndoCount == stepsNow - 1 && SameSamples(states[^2], field.ToSamples()),
			"Ctrl+Z undoes the last step");
		Press(Key.Y, true);
		yield return true;
		Fact(editor.Editing.History.UndoCount == stepsNow && SameSamples(states[^1], field.ToSamples()),
			"Ctrl+Y redoes it");

		// ---- the water and the bands -----------------------------------------------------------
		editor.SetBand(BandNumber.WaterLevel, 2f);
		int under = 0;
		foreach (float h in field.Heights)
		{
			under += h < 2f ? 1 : 0;
		}

		Fact(editor.Editing.Document.Bands.WaterLevelMeters == 2f && editor.Terrain.SubmergedSamples == under
			&& editor.Terrain.WaterDrawn && editor.Editing.Dirty,
			$"the water's slider: at 2 m, {editor.Terrain.SubmergedSamples} samples under it and its quad drawn");
		editor.SetBand(BandNumber.WaterLevel, -5f);
		under = 0;
		foreach (float h in field.Heights)
		{
			under += h < -5f ? 1 : 0;
		}

		Fact(editor.Terrain.SubmergedSamples == under && editor.Terrain.WaterDrawn == (under > 0),
			$"and back at -5 m, {under} under it, the quad drawn: {editor.Terrain.WaterDrawn}");

		// ---- save, open ----------------------------------------------------------------------------
		Fact(editor.Save(out string error), $"save: {error ?? editor.Message}");
		string path = editor.SavedPath;
		byte[] first = File.ReadAllBytes(path);
		Fact(MapFile.TryRead(first, out MapDocument saved, out error) && saved.SameAs(editor.Editing.Document, out error),
			$"the saved file reads as the document being edited ({error ?? "the same"})");
		Fact(saved != null && saved.Revision == 1 && !editor.Editing.Dirty && editor.EverSaved
			&& editor.SavedHash == MapFile.HashHex(first) && !File.Exists(path + MapStore.TempSuffix),
			$"it is revision {saved?.Revision}, the editor's hash is the file's, no .tmp is left");

		foreach (bool step in HeldStroke(editor, "sculpt (Hill), after a save", TerrainBrush.Presets[1].Settings,
			new Vector2(70f, 70f), new Vector2(80f, 70f), false, 30, random))
		{
			yield return step;
		}

		Fact(editor.Editing.Dirty, "a stroke after a save leaves the map unsaved");
		Fact(editor.Save(out error), $"saved again: {error ?? editor.Message}");
		byte[] second = File.ReadAllBytes(path);
		Fact(File.Exists(path + MapStore.BackupSuffix) && SameBytes(first, File.ReadAllBytes(path + MapStore.BackupSuffix))
			&& !SameBytes(first, second), "the second save keeps the first file as .bak");

		MapDocument before = editor.Editing.Document;
		Fact(editor.Open(name, out error), $"open {name}: {error ?? editor.Message}");
		Fact(editor.Editing.Document != before && editor.Editing.Document.SameAs(before, out error)
			&& editor.Editing.Document.Revision == 2 && editor.Editing.History.UndoCount == 0 && editor.AutosaveOffered == null,
			$"save then open is the same document, at revision 2, with nothing to undo ({error ?? "the same"})");
		field = editor.Editing.Document.Terrain;
		foreach (bool step in WorldIsDocument(editor, "opened again", rays))
		{
			yield return step;
		}

		// ---- autosave ------------------------------------------------------------------------------
		foreach (bool step in HeldStroke(editor, "sculpt (Plain), to autosave", TerrainBrush.Presets[0].Settings,
			new Vector2(0f, 80f), new Vector2(10f, 80f), false, 30, random))
		{
			yield return step;
		}

		MapDocument unsaved = editor.Editing.Document.Clone();
		Fact(editor.Autosave(out error), $"autosave: {error ?? editor.Message}");
		string autosave = MapStore.AutosavePathOf(MapEditorRoot.Directory, name);
		Fact(MapStore.TryReadFile(autosave, out MapDocument written, out _, out error) && written.SameAs(unsaved, out error)
			&& SameBytes(second, File.ReadAllBytes(path)),
			$"the autosave is the map as it is, beside the saved file, which is untouched ({error ?? "the same"})");
		Fact(editor.Open(name, out error) && editor.AutosaveOffered != null && editor.AutosaveOffered.SameAs(unsaved, out _),
			"opening it again offers the autosave back");
		Fact(editor.RestoreAutosave() && editor.Editing.Document.SameAs(unsaved, out error) && editor.Editing.Dirty,
			$"taken back, it is the map as autosaved, not saved ({error ?? "the same"})");
		field = editor.Editing.Document.Terrain;
		foreach (bool step in WorldIsDocument(editor, "the autosave taken back", rays))
		{
			yield return step;
		}

		Fact(editor.Save(out _) && !File.Exists(autosave), "saving drops the autosave");
		Fact(editor.Open(name, out _) && editor.AutosaveOffered == null, "and opening it again offers none");

		// ---- save as -------------------------------------------------------------------------------
		Guid lineage = editor.Editing.Document.Id;
		string savedHash = editor.SavedHash;
		Fact(editor.SaveAs(name + "-fork", out error), $"save as {name}-fork: {error ?? editor.Message}");
		MapDocument fork = editor.Editing.Document;
		Fact(editor.FileName == name + "-fork" && fork.Id != lineage && fork.ParentId == lineage
			&& Convert.ToHexString(fork.ParentHash).ToLowerInvariant() == savedHash && fork.Revision == 1
			&& File.Exists(MapStore.PathOf(MapEditorRoot.Directory, name)),
			"save as is a fork: a new GUID, the file it was saved as for its parent, revision 1, and the first file kept");

		// ---- a clone of the bundled map --------------------------------------------------------------
		Fact(MapCatalog.TryFind("hills", out MapInfo bundled) && editor.Clone(bundled.Path, hills, out error),
			$"clone the bundled map as {hills}: {error ?? editor.Message}");
		MapDocument document = editor.Editing.Document;
		Fact(editor.EverSaved && File.Exists(editor.SavedPath) && document.ParentHash != null
			&& Convert.ToHexString(document.ParentHash).ToLowerInvariant() == bundled.Hash && document.Revision == 1,
			$"the clone is saved as {hills}.map, the bundled file's hash its parent's");
		Fact(editor.Editing.Report.IsPlayable, $"it plays: {MapStore.Describe(editor.Editing.Report)}");
		int Count(ushort id) => document.Objects.FindAll(o => o.PrefabId == id).Count;
		LevelBuilder.Summary built = editor.Built;
		Fact(built.Barracks == Count(PrefabSpecs.Barracks) && built.Nodes == Count(PrefabSpecs.ResourceNode)
			&& built.SpawnLines == Count(PrefabSpecs.SpawnLine) && built.Lockers == Count(PrefabSpecs.WeaponLocker)
			&& built.Unknown == 0 && built.Barracks > 0,
			$"what is edited is what will be played: the round's builder put up its pieces ({built})");

		field = document.Terrain;
		MapObject node = document.Objects.Find(o => o.PrefabId == PrefabSpecs.ResourceNode);
		PrefabSpecs.TryGetPadArea(node, out PadArea pad);
		var padSamples = new List<int>();
		for (int i = 0; i < field.Heights.Length; i++)
		{
			float dx = field.WorldX(i % field.Width) - pad.X;
			float dz = field.WorldZ(i / field.Width) - pad.Z;
			if (MathF.Sqrt((dx * dx) + (dz * dz)) <= pad.Radius + TerrainConform.PadMarginMeters)
			{
				padSamples.Add(i);
			}
		}

		float level = PrefabPlacement.PadLevel(node);
		short[] hillsBefore = field.ToSamples();
		BrushSettings hill = TerrainBrush.Presets[1].Settings;
		hill.RadiusMeters = 20f;
		foreach (bool step in HeldStroke(editor, "sculpt across a node's pad", hill, new Vector2(pad.X - 6f, pad.Z),
			new Vector2(pad.X + 6f, pad.Z), false, 60, random))
		{
			yield return step;
		}

		bool padKept = true;
		foreach (int i in padSamples)
		{
			padKept &= field.Heights[i] == level;
		}

		MapReport report = editor.Editing.Report;
		Fact(padKept && !report.Has(MapIssueCode.OffItsPad) && report.IsPlayable,
			$"the node's {padSamples.Count} pad samples are at its level still, and the map plays: {MapStore.Describe(report)}");

		// The highest the stroke raised: what the test process must stand on.
		short[] hillsAfter = field.ToSamples();
		int raised = 0;
		for (int i = 1; i < hillsAfter.Length; i++)
		{
			raised = hillsAfter[i] - hillsBefore[i] > hillsAfter[raised] - hillsBefore[raised] ? i : raised;
		}

		float gx = field.WorldX(raised % field.Width);
		float gz = field.WorldZ(raised / field.Width);
		float gh = field.Heights[raised];
		Measured($"the stroke raised ({F(gx, "0")}, {F(gz, "0")}) by {F(HeightField.ToMeters((short)(hillsAfter[raised] - hillsBefore[raised])))} m");

		// ---- test ----------------------------------------------------------------------------------
		Fact(editor.Save(out error), $"saved: {error ?? editor.Message}");
		OS.SetEnvironment("LE4_CHECK", "test-child");
		OS.SetEnvironment("LE4_EXPECT_HASH", editor.SavedHash);
		OS.SetEnvironment("LE4_EXPECT_PATH", editor.SavedPath);
		OS.SetEnvironment("LE4_EXPECT_GROUND", string.Join(",", F(gx, "R"), F(gz, "R"), F(gh, "R")));
		string revisionHash = editor.SavedHash;
		Fact(editor.Test(out error) && editor.TestProcessId > 0, $"Test: {error ?? editor.Message}");
		Fact(editor.SavedHash == revisionHash, "a map saved and unchanged is not saved again to be tested");
		IReadOnlyList<string> arguments = editor.TestArgumentsUsed;
		int bare = IndexOf(arguments, "--");
		int project = IndexOf(arguments, "--path");
		int headless = IndexOf(arguments, "--headless");
		Fact(bare >= 0 && IndexOf(arguments, "--practice") > bare && IndexOf(arguments, "--map") > bare
			&& arguments[^1] == editor.SavedPath && project >= 0 && project < bare && headless >= 0 && headless < bare,
			$"the test process is started on the saved file: {string.Join(' ', arguments)}");
		Measured($"the test process: {editor.TestProcessId}");

		int pid = editor.TestProcessId;
		long started = Stopwatch.GetTimestamp();
		bool stayed = true;
		while (pid > 0 && OS.IsProcessRunning(pid) && Ms(started) < 400_000)
		{
			stayed &= MapEditorRoot.Current == editor && editor.Editing.Document == document && !Session.InGame;
			yield return true;
		}

		int exit = OS.GetProcessExitCode(pid);
		Fact(pid > 0 && !OS.IsProcessRunning(pid) && exit == 0 && Has("test-child-live"),
			$"the test process played the saved file, its round went live, and it ended with {exit} after {F(Ms(started) / 1000.0, "0.0")} s");
		Fact(stayed && MapEditorRoot.Current == editor && editor.Editing.Document == document,
			"and the editor never left: the same map open in it, and nothing simulated, while the test ran");
	}

	private void Press(Key key, bool ctrl)
	{
		var press = new InputEventKey { Keycode = key, PhysicalKeycode = key, CtrlPressed = ctrl, Pressed = true };
		GetTree().Root.PushInput(press);
		var release = new InputEventKey { Keycode = key, PhysicalKeycode = key, CtrlPressed = ctrl, Pressed = false };
		GetTree().Root.PushInput(release);
	}

	private static int OverlayOnChunk(MapEditorRoot editor)
	{
		var chunk = editor.Terrain.GetNodeOrNull<MeshInstance3D>("Chunk_00_00");
		return chunk?.Mesh is ArrayMesh mesh && mesh.SurfaceGetMaterial(0) is ShaderMaterial material
			? material.GetShaderParameter("overlay").AsInt32()
			: -1;
	}

	private static int IndexOf(IReadOnlyList<string> list, string item)
	{
		for (int i = 0; i < list.Count; i++)
		{
			if (list[i] == item)
			{
				return i;
			}
		}

		return -1;
	}

	private static bool SameSamples(short[] a, short[] b) => a.AsSpan().SequenceEqual(b);

	private static bool SameBytes(byte[] a, byte[] b) => a.AsSpan().SequenceEqual(b);

	// ---- LE5: prefabs ------------------------------------------------------------------------------

	/// <summary>How far a collider's corner may be outside its prefab's boxes [m].</summary>
	private const float BoxTolerance = 0.02f;

	/// <summary>A held stroke at one point, through the editor's commands, for <paramref name="frames"/> frames of 1/60 s.</summary>
	private static IEnumerable<bool> Hold(MapEditorRoot editor, BrushSettings brush, Vector2 at, int frames, bool lower)
	{
		editor.SelectTool(EditorTool.Terrain);
		editor.SetBrush(brush);
		editor.BeginStroke(at.X, at.Y, lower);
		for (int f = 0; f < frames; f++)
		{
			editor.StrokeTo(at.X, at.Y, 1f / 60f);
			yield return true;
		}

		editor.EndStroke();
		editor.SelectTool(EditorTool.Prefabs);
	}

	/// <summary>Every corner of every collider under a node, in the world: box shapes, concave faces, CSG boxes.</summary>
	private static List<Vector3> ColliderPoints(Node node)
	{
		var points = new List<Vector3>();
		var stack = new Stack<Node>();
		stack.Push(node);
		while (stack.Count > 0)
		{
			Node current = stack.Pop();
			foreach (Node child in current.GetChildren())
			{
				stack.Push(child);
			}

			if (current is CollisionShape3D { Shape: BoxShape3D box } boxNode)
			{
				AddCorners(points, boxNode.GlobalTransform, box.Size * 0.5f);
			}
			else if (current is CollisionShape3D { Shape: ConcavePolygonShape3D concave } concaveNode)
			{
				foreach (Vector3 v in concave.GetFaces())
				{
					points.Add(concaveNode.GlobalTransform * v);
				}
			}
			else if (current is CollisionShape3D { Shape: ConvexPolygonShape3D convex } convexNode)
			{
				foreach (Vector3 v in convex.Points)
				{
					points.Add(convexNode.GlobalTransform * v);
				}
			}
			else if (current is CsgBox3D { UseCollision: true } csg)
			{
				AddCorners(points, csg.GlobalTransform, csg.Size * 0.5f);
			}
		}

		return points;
	}

	private static void AddCorners(List<Vector3> points, Transform3D at, Vector3 h)
	{
		for (int c = 0; c < 8; c++)
		{
			points.Add(at * new Vector3((c & 1) == 0 ? -h.X : h.X, (c & 2) == 0 ? -h.Y : h.Y, (c & 4) == 0 ? -h.Z : h.Z));
		}
	}

	/// <summary>
	/// How far the furthest point is outside the object's boxes, in its own frame [m]: 0 when all
	/// are inside. With <paramref name="plinth"/>, a structure's: LE-D21's plinth, the body's plan
	/// from 0.5 m under its base to the base (§7.5), is allowed too — under its level pad.
	/// </summary>
	private static float OutsideBoxes(MapObject placed, PrefabSpec spec, List<Vector3> world, bool plinth = false)
	{
		Transform3D inverse = PrefabCatalog.PoseOf(placed, spec).AffineInverse();
		List<PlacedBox> placedBoxes = PrefabSpecs.BoxesOf(placed);
		var boxes = new List<StructureBox>(spec.Boxes);
		if (plinth)
		{
			StructureBox body = spec.Boxes[0];
			float depth = StructurePlacement.MaxGroundReliefMeters;
			boxes.Add(new StructureBox(new Vector3(body.Center.X, -depth * 0.5f, body.Center.Z),
				new Vector3(body.HalfExtents.X, depth * 0.5f, body.HalfExtents.Z)));
		}

		for (int i = spec.Boxes.Count; i < placedBoxes.Count; i++)
		{
			// A pier, back into the section's frame.
			PlacedBox pier = placedBoxes[i];
			Vector3 centre = inverse * new Vector3(pier.Plan.Center.X, (pier.Bottom + pier.Top) * 0.5f, pier.Plan.Center.Z);
			boxes.Add(new StructureBox(centre, new Vector3(pier.Plan.HalfWidth, (pier.Top - pier.Bottom) * 0.5f, pier.Plan.HalfDepth)));
		}

		float worst = 0f;
		foreach (Vector3 point in world)
		{
			Vector3 local = inverse * point;
			float best = float.MaxValue;
			foreach (StructureBox box in boxes)
			{
				Vector3 d = (local - box.Center).Abs() - box.HalfExtents;
				best = MathF.Min(best, MathF.Max(0f, MathF.Max(d.X, MathF.Max(d.Y, d.Z))));
			}

			worst = MathF.Max(worst, best);
		}

		return worst;
	}

	/// <summary>Whether every sample under an object's footprint and within 1.5 m of it is at its pad's level, to the bit.</summary>
	private static bool PadLevel(HeightField field, MapObject placed, out int samples)
	{
		samples = 0;
		if (!PrefabSpecs.TryGetPadArea(placed, out PadArea area))
		{
			return true;
		}

		float level = PrefabPlacement.PadLevel(placed);
		SampleRect rect = area.Bounds(field, TerrainConform.PadMarginMeters);
		for (int z = rect.MinZ; z <= rect.MaxZ; z++)
		{
			for (int x = rect.MinX; x <= rect.MaxX; x++)
			{
				if (area.DistanceTo(field.WorldX(x), field.WorldZ(z)) <= TerrainConform.PadMarginMeters)
				{
					samples++;
					if (field[x, z] != level)
					{
						return false;
					}
				}
			}
		}

		return true;
	}

	private IEnumerable<bool> WaitForDeep(MapEditorRoot editor)
	{
		editor.Deep();
		long started = Stopwatch.GetTimestamp();
		while ((editor.DeepRunning || !editor.DeepIsCurrent) && Ms(started) < 120_000)
		{
			yield return true;
		}
	}

	private static int Count(MapReport report, MapIssueCode code)
	{
		int n = 0;
		foreach (MapIssue issue in report?.Issues ?? Array.Empty<MapIssue>())
		{
			n += issue.Code == code ? 1 : 0;
		}

		return n;
	}

	private IEnumerable<bool> Prefabs()
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

		// ---- a map on a 6° slope, from a file, opened ------------------------------------------------
		string slope = Env("LE5_SLOPE", "le5-slope");
		MapDocument plane = MapDocument.Create(Guid.NewGuid(), slope, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
		float grade = HeightField.Grade(6f);
		for (int z = 0; z < plane.Terrain.Depth; z++)
		{
			for (int x = 0; x < plane.Terrain.Width; x++)
			{
				plane.Terrain[x, z] = plane.Terrain.WorldX(x) * grade;
			}
		}

		plane.Terrain.Round(plane.Terrain.Bounds);
		string error = null;
		Fact(MapStore.TrySave(plane, MapEditorRoot.Directory, slope, 0, out _, out error) && editor.Open(slope, out error),
			$"a map that is a 6° slope along X opened: {error ?? editor.Message}");
		if (editor.Editing == null)
		{
			yield break;
		}

		HeightField field = editor.Editing.Document.Terrain;
		short[] bare = field.ToSamples();
		editor.SelectTool(EditorTool.Prefabs);
		Fact(editor.Tool == EditorTool.Prefabs, "the prefab tool is selected");

		// ---- every prefab of the catalog -----------------------------------------------------------
		var layout = new (ushort Id, float X, float Z)[]
		{
			(PrefabSpecs.SpawnLine, -20f, -80f), (PrefabSpecs.WeaponLocker, -20f, -50f), (PrefabSpecs.VehicleKiosk, 20f, -80f),
			(PrefabSpecs.Barracks, 60f, -80f), (PrefabSpecs.ResourceNode, -15f, 20f), (PrefabSpecs.StrategistAnchor, 90f, 90f),
			(PrefabSpecs.HeavyGun, 20f, -40f), (PrefabSpecs.MortarLauncher, 30f, -40f), (PrefabSpecs.AmmoCan, 40f, -40f),
			(PrefabSpecs.CoverLowWall, 20f, 0f), (PrefabSpecs.CoverBlock, 35f, 0f), (PrefabSpecs.Bunker, 80f, 20f),
			(PrefabSpecs.StructurePillbox, 20f, 40f), (PrefabSpecs.StructureSandbagWall, 30f, 40f),
			(PrefabSpecs.StructureSniperTower, 40f, 40f), (PrefabSpecs.BridgeSpan, 20f, 80f), (PrefabSpecs.BridgeRamp, 45f, 80f),
		};
		Fact(layout.Length == PrefabSpecs.Count, $"{layout.Length} prefabs of the catalog's {PrefabSpecs.Count} to place");
		float yaw = MathF.PI / 6f;
		int placedCount = 0;
		foreach ((ushort id, float x, float z) in layout)
		{
			PrefabSpec spec = PrefabSpecs.Get(id);
			PlacementCheck check;
			if (spec.FreeHeight)
			{
				check = editor.Place(new PlacementRequest(id, x, z, yaw, field.Sample(x, z) + 3f));
			}
			else
			{
				editor.SelectPrefab(id);
				editor.SetGhostYaw(yaw);
				PlacementCheck ghost = editor.Ghost(x, z);
				check = editor.PlaceAt(x, z);
				Fact(ghost != null && ghost.Result == check.Result && ghost.GradeDegrees == check.GradeDegrees,
					$"{spec.Name}: the ghost's answer is the placement's, {ghost?.Result}, {ghost?.GradeDegrees:0.00}°");
			}

			Fact(check.IsOk, $"{spec.Name}: placed on the 6° slope, {check.GradeDegrees:0.00}° under it: {check.Result}");
			if (!check.IsOk)
			{
				continue;
			}

			placedCount++;
			int index = editor.Editing.Document.Objects.Count - 1;
			MapObject placed = editor.Editing.Document.Objects[index];
			if (spec.MakesPad)
			{
				Fact(PadLevel(field, placed, out int samples), $"{spec.Name}: stands on a level pad, its {samples} samples at"
					+ $" {PrefabPlacement.PadLevel(placed):0.000} m to the bit");
			}

			Node3D node = index < editor.BuiltNodes.Count ? editor.BuiltNodes[index] : null;
			if (spec.Kind == PrefabKind.Structure)
			{
				Fact(node == null, $"{spec.Name}: left for the round to put up; its colliders are the round part's to hold");
				continue;
			}

			if (node != null && spec.MakesPad)
			{
				// A cover box's node is the box, whose origin is its middle (PrefabCatalog).
				float lift = spec.Kind == PrefabKind.Cover ? spec.Boxes[0].Center.Y : 0f;
				Fact(node.GlobalPosition.Y == PrefabPlacement.PadLevel(placed) + lift,
					$"{spec.Name}: its node stands at the pad's level, {node.GlobalPosition.Y - lift:0.000} m");
			}

			List<Vector3> points = node == null ? new List<Vector3>() : ColliderPoints(node);
			float outside = OutsideBoxes(placed, spec, points);
			Fact(node != null && outside <= BoxTolerance, points.Count == 0
				? $"{spec.Name}: built, with no collider of its own"
				: $"{spec.Name}: {points.Count} collider corners, the furthest {outside * 100f:0.0} cm outside its boxes");
		}

		MapReport laid = MapValidator.Validate(editor.Editing.Document);
		Fact(!laid.Has(MapIssueCode.OffItsPad) && !laid.Has(MapIssueCode.InTheWay),
			$"the validator finds every padded object on its pad and nothing in the way: {MapStore.Describe(laid)}");

		// ---- each of §6.5's answers ---------------------------------------------------------------
		MapDocument document = editor.Editing.Document;
		int Find(ushort id) => document.Objects.FindIndex(o => o.PrefabId == id);
		MapObject kiosk = document.Objects[Find(PrefabSpecs.VehicleKiosk)];
		MapObject barracks = document.Objects[Find(PrefabSpecs.Barracks)];
		var kioskPose = new StructurePose(kiosk.Position, kiosk.YawRadians);
		var barracksPose = new StructurePose(barracks.Position, barracks.YawRadians);
		Vector3 besideKiosk = kioskPose.ToWorld(new Vector3(0f, 0f, 7.5f));
		Vector3 beforeDoor = barracksPose.ToWorld(new Vector3(0f, 0f, -13f));

		// A mound of 30° to be too steep on.
		foreach (bool step in Hold(editor, TerrainBrush.Presets[6].Settings, new Vector2(-5f, 70f), 120, false))
		{
			yield return step;
		}

		var answers = new (string What, ushort Id, float X, float Z, PrefabPlacementResult Expected)[]
		{
			("a second kiosk", PrefabSpecs.VehicleKiosk, -20f, 60f, PrefabPlacementResult.LimitReached),
			("a locker at the field's edge", PrefabSpecs.WeaponLocker, 119.5f, 0f, PrefabPlacementResult.OutOfBounds),
			("a locker on the kiosk", PrefabSpecs.WeaponLocker, kiosk.Position.X, kiosk.Position.Z, PrefabPlacementResult.InTheWay),
			("a locker on the mound's side", PrefabSpecs.WeaponLocker, -1f, 70f, PrefabPlacementResult.TooSteep),
			("a locker in the water", PrefabSpecs.WeaponLocker, -80f, -30f, PrefabPlacementResult.InWater),
			("a cover block whose pad would cut the kiosk's", PrefabSpecs.CoverBlock, besideKiosk.X, besideKiosk.Z,
				PrefabPlacementResult.DisturbsNeighbour),
			("a pillbox before the barracks' door", PrefabSpecs.StructurePillbox, beforeDoor.X, beforeDoor.Z,
				PrefabPlacementResult.BlocksDoor),
		};
		foreach ((string what, ushort id, float x, float z, PrefabPlacementResult expected) in answers)
		{
			short[] before = field.ToSamples();
			int objects = document.Objects.Count;
			int steps = editor.Editing.History.UndoCount;
			editor.SelectPrefab(id);
			editor.SetGhostYaw(id == PrefabSpecs.StructurePillbox ? barracks.YawRadians : kiosk.YawRadians);
			PlacementCheck ghost = editor.Ghost(x, z);
			PlacementCheck check = editor.PlaceAt(x, z);
			Fact(check.Result == expected && ghost.Result == expected && document.Objects.Count == objects
				&& editor.Editing.History.UndoCount == steps && SameSamples(before, field.ToSamples()),
				$"{what}: {PrefabPlacement.Describe(check.Result)}, the ghost the same, and nothing changed"
				+ (check.Result == expected ? string.Empty : $" (expected {expected})"));
		}

		PlacementCheck unknown = editor.Place(new PlacementRequest(999, 0f, 0f, 0f));
		Fact(unknown.Result == PrefabPlacementResult.UnknownPrefab, $"prefab 999: {PrefabPlacement.Describe(unknown.Result)}");

		// ---- select, turn, parameters, take up ------------------------------------------------------
		int kioskIndex = Find(PrefabSpecs.VehicleKiosk);
		Fact(editor.SelectAt(kiosk.Position.X + 1f, kiosk.Position.Z) == kioskIndex, $"a click on the kiosk selects it: {editor.Selected}");
		int stepsBefore = editor.Editing.History.UndoCount;
		ushort yawBefore = kiosk.Yaw;
		Press(Key.R, false);
		yield return true;
		MapObject turned = document.Objects[kioskIndex];
		Fact(turned.PrefabId == PrefabSpecs.VehicleKiosk && turned.Yaw != yawBefore
			&& MathF.Abs(Mathf.Wrap(turned.YawRadians - kiosk.YawRadians, -MathF.PI, MathF.PI) - (MathF.PI / 12f)) < 0.001f
			&& editor.Editing.History.UndoCount == stepsBefore + 1 && editor.Selected == kioskIndex
			&& PadLevel(field, turned, out _),
			$"R turns the selected kiosk 15° where it is in the file, as one step, on a level pad: {Mathf.RadToDeg(turned.YawRadians):0.0}°");

		int nodeIndex = Find(PrefabSpecs.ResourceNode);
		MapObject node0 = document.Objects[nodeIndex];
		Fact(editor.SelectAt(node0.Position.X + 6f, node0.Position.Z) == nodeIndex, "a click on the node's disc selects it");
		PlacementCheck radius = editor.SetSelectedParameter(PrefabSpecs.NodeCaptureRadiusKey, 1200);
		MapObject wider = document.Objects[nodeIndex];
		int discSamples = 0;
		Fact(radius != null && radius.IsOk && wider.Parameter(PrefabSpecs.NodeCaptureRadiusKey, 0) == 1200
			&& PrefabSpecs.TryGetPadArea(wider, out PadArea disc) && disc.Radius == 12f && PadLevel(field, wider, out discSamples),
			$"its capture radius to 12 m: placed again, its disc of {discSamples} samples level");

		int canIndex = Find(PrefabSpecs.AmmoCan);
		MapObject can = document.Objects[canIndex];
		editor.Select(canIndex);
		int objectsBefore = document.Objects.Count;
		Press(Key.Delete, false);
		yield return true;
		Fact(document.Objects.Count == objectsBefore - 1 && !document.Objects.Contains(can) && editor.Selected == -1,
			"Del takes the selected can up");
		Fact(editor.Undo() && document.Objects[canIndex].Equals(can), "undo puts it back where it was in the file");

		// ---- the deep check: every walk on the slope, and none to a node on an island ------------------
		foreach (bool step in WaitForDeep(editor))
		{
			yield return step;
		}

		Fact(editor.DeepIsCurrent && editor.DeepVertices > 0 && editor.DeepWalks == 2
			&& Count(editor.DeepReport, MapIssueCode.Unreachable) == 0,
			$"deep: {editor.DeepVertices} vertices baked in {editor.DeepBakeMilliseconds:0} ms; {editor.DeepWalks} walks, from the spawn line"
			+ $" and the barracks' door to the node, {Count(editor.DeepReport, MapIssueCode.Unreachable)} unreached"
			+ $" (current: {editor.DeepIsCurrent}): {MapStore.Describe(editor.DeepReport)}");

		foreach (bool step in Hold(editor, TerrainBrush.Presets[0].Settings, new Vector2(-100f, 60f), 660, false))
		{
			yield return step;
		}

		editor.SelectPrefab(PrefabSpecs.ResourceNode);
		PlacementCheck island = editor.PlaceAt(-100f, 60f);
		Fact(island.IsOk, $"a node on an island raised out of the water: {island.Result}, at {island.Level:0.00} m");
		foreach (bool step in WaitForDeep(editor))
		{
			yield return step;
		}

		int unreached = Count(editor.DeepReport, MapIssueCode.Unreachable);
		bool namesIsland = false;
		foreach (MapIssue issue in editor.DeepReport?.Issues ?? Array.Empty<MapIssue>())
		{
			namesIsland |= issue.Code == MapIssueCode.Unreachable && issue.Other == document.Objects.Count - 1;
		}

		Fact(editor.DeepWalks == 4 && unreached == 2 && namesIsland && (editor.DeepReport?.IsPlayable ?? false),
			$"deep again: {editor.DeepWalks} walks, the island's two unreached and named, and no error of it:"
			+ $" {MapStore.Describe(editor.DeepReport)}");

		// ---- undone, to the slope as it was ---------------------------------------------------------
		while (editor.Editing.History.CanUndo)
		{
			editor.Undo();
		}

		Fact(document.Objects.Count == 0 && SameSamples(bare, field.ToSamples()),
			$"every placement, turn, parameter and stroke undone: no object, and every sample the slope's ({placedCount} placed)");

		// ---- a map made only in the editor, tested ----------------------------------------------------
		string made = Env("LE5_MADE", "le5-made");
		Fact(editor.NewMap(made, out error), $"a new map, {made}: {error ?? editor.Message}");
		document = editor.Editing.Document;
		foreach (bool step in Hold(editor, TerrainBrush.Presets[1].Settings, new Vector2(0f, 75f), 90, false))
		{
			yield return step;
		}

		foreach (bool step in Hold(editor, TerrainBrush.Presets[2].Settings, new Vector2(10f, -80f), 90, false))
		{
			yield return step;
		}

		var pieces = new (ushort Id, float X, float Z, float YawDegrees)[]
		{
			(PrefabSpecs.SpawnLine, -95f, 0f, 90f), (PrefabSpecs.WeaponLocker, -95f, 14f, 0f),
			(PrefabSpecs.VehicleKiosk, -85f, -40f, 0f), (PrefabSpecs.Barracks, 90f, 50f, 90f), (PrefabSpecs.Barracks, 90f, -50f, 90f),
			(PrefabSpecs.ResourceNode, -30f, 0f, 0f), (PrefabSpecs.ResourceNode, 25f, 45f, 0f), (PrefabSpecs.ResourceNode, 25f, -45f, 0f),
			(PrefabSpecs.CoverBlock, -15f, 18f, 0f), (PrefabSpecs.CoverBlock, -15f, -18f, 0f), (PrefabSpecs.CoverLowWall, -60f, 10f, 0f),
			(PrefabSpecs.CoverLowWall, -60f, -10f, 0f), (PrefabSpecs.CoverLowWall, 5f, 25f, 45f), (PrefabSpecs.CoverLowWall, 5f, -25f, -45f),
			(PrefabSpecs.StructurePillbox, -48f, 6f, 90f), (PrefabSpecs.StrategistAnchor, 70f, 0f, 0f),
			(PrefabSpecs.HeavyGun, -70f, 20f, 90f), (PrefabSpecs.AmmoCan, -92f, 20f, 0f),
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

		MapReport report = editor.Editing.Report;
		Fact(allPlaced && report.IsPlayable, $"{pieces.Length} pieces placed with the prefab tool; it passes the validator:"
			+ $" {MapStore.Describe(report)}");
		foreach (bool step in WaitForDeep(editor))
		{
			yield return step;
		}

		Fact(editor.DeepIsCurrent && Count(editor.DeepReport, MapIssueCode.Unreachable) == 0 && editor.DeepReport.IsPlayable,
			$"and the deep check: {editor.DeepWalks} walks, none unreached: {MapStore.Describe(editor.DeepReport)}");
		Fact(editor.Save(out error), $"saved: {error ?? editor.Message}");

		MapObject pillbox = document.Objects[document.Objects.FindIndex(o => o.PrefabId == PrefabSpecs.StructurePillbox)];
		OS.SetEnvironment("LE4_CHECK", "round");
		OS.SetEnvironment("LE5_PILLBOX", string.Join(",", F(pillbox.Position.X, "R"), F(pillbox.Position.Z, "R"),
			F(PrefabPlacement.PadLevel(pillbox), "R"), F(pillbox.YawRadians, "R")));
		OS.SetEnvironment("LE4_EXPECT_HASH", editor.SavedHash);
		Fact(editor.Test(out error), $"Test: {error ?? editor.Message}");
		int pid = editor.TestProcessId;
		int seconds = int.TryParse(Env("LE5_SECONDS", "600"), out int s) ? s : 600;
		long started = Stopwatch.GetTimestamp();
		while (pid > 0 && OS.IsProcessRunning(pid) && Ms(started) < (seconds + 300) * 1000.0)
		{
			yield return true;
		}

		string held = Has("round-done") ? File.ReadAllText(Path.Combine(_dir, "round-done")) : "none";
		string[] counts = held.Split(" of ");
		Fact(pid > 0 && !OS.IsProcessRunning(pid) && counts.Length == 2 && counts[0] == counts[1],
			$"the test process played the map made in the editor, {F(Ms(started) / 1000.0, "0")} s, and its facts held: {held}");
		Measured($"the test process's exit code: {OS.GetProcessExitCode(pid)}"
			+ (OS.GetProcessExitCode(pid) == 0 ? string.Empty : " — the crash on exit of §13, after its facts were in"));
	}

	// ---- LE5: the round of the map made in the editor -------------------------------------------------

	private IEnumerable<bool> Round()
	{
		string[] at = Env("LE5_PILLBOX", "0,0,0,0").Split(',');
		float px = float.Parse(at[0], CultureInfo.InvariantCulture);
		float pz = float.Parse(at[1], CultureInfo.InvariantCulture);
		float level = float.Parse(at[2], CultureInfo.InvariantCulture);
		float facing = float.Parse(at[3], CultureInfo.InvariantCulture);
		int seconds = int.TryParse(Env("LE5_SECONDS", "600"), out int s) ? s : 600;

		long waiting = Stopwatch.GetTimestamp();
		CombatManager combat = CombatManager.Instance;
		UnitManager units = UnitManager.Instance;
		while (!(combat?.Match.IsLive ?? false) && Ms(waiting) < 240_000)
		{
			yield return true;
		}

		Fact(combat?.Match.IsLive ?? false, "the round is live");
		Fact(Session.Map.IsData && Session.Map.Hash == Env("LE4_EXPECT_HASH", string.Empty),
			$"on the map made in the editor: {Session.Map}");
		Fact(units != null && units.MapStructuresPlaced == 1 && units.MapStructuresRefused == 0,
			$"the round put the map's structure up: {units?.MapStructuresPlaced} put up, {units?.MapStructuresRefused} refused");

		Structure Pillbox()
		{
			for (int i = 0; units != null && i < SimConfig.MaxStructures; i++)
			{
				Structure structure = units.StructureAt(i);
				if (structure != null && structure.Kind == StructureKinds.Pillbox
					&& new Vector2(structure.Pose.Base.X - px, structure.Pose.Base.Z - pz).Length() < 0.05f)
				{
					return structure;
				}
			}

			return null;
		}

		Structure first = Pillbox();
		Fact(first != null && first.IsBuilt && !first.IsDestroyed && first.Team == Team.Strategist
			&& MathF.Abs(first.Pose.Base.Y - level) < 0.01f && MathF.Abs(Mathf.Wrap(first.Facing - facing, -MathF.PI, MathF.PI)) < 0.001f,
			$"a finished pillbox of the strategist's stands where the file put it, at its pad's level: {first?.Pose.Base}");
		if (first != null)
		{
			var placed = MapObject.At(PrefabSpecs.StructurePillbox, new Vector3(px, level, pz), facing);
			List<Vector3> points = ColliderPoints(first);
			float outside = OutsideBoxes(placed, PrefabSpecs.Get(PrefabSpecs.StructurePillbox), points);
			float underPlinth = OutsideBoxes(placed, PrefabSpecs.Get(PrefabSpecs.StructurePillbox), points, plinth: true);
			Fact(points.Count > 0 && underPlinth <= BoxTolerance,
				$"its {points.Count} collider corners are inside the prefab's boxes and LE-D21's plinth under them, the furthest"
				+ $" {underPlinth * 100f:0.0} cm out; without the plinth, {outside * 100f:0.0} cm, its depth");
		}

		bool fired = false;
		bool destroyed = false;
		int rounds = 0;
		bool live = true;
		long started = Stopwatch.GetTimestamp();
		while (!(fired && destroyed) && Ms(started) < seconds * 1000.0)
		{
			Structure pillbox = Pillbox();
			fired |= pillbox != null && pillbox.Weapon.ShotIndex > 0;
			destroyed |= pillbox != null && pillbox.IsDestroyed;
			if (combat.Match.IsLive != live)
			{
				live = combat.Match.IsLive;
				rounds += live ? 1 : 0;
			}

			yield return true;
		}

		Measured($"{F(Ms(started) / 1000.0, "0")} s of the round; {rounds} more rounds began; units trained {units.UnitsProduced}"
			+ $" this round; structures built {units.StructuresBuilt}, lost {units.StructuresLost}");
		Fact(fired, "the pillbox fired");
		Fact(destroyed, "the pillbox was destroyed");

		// The facts, for the editor's part to read: this process's exit code is its failures' count,
		// but a practice round can crash as it exits, after every fact is in (§13).
		if (!string.IsNullOrEmpty(_dir))
		{
			File.WriteAllText(Path.Combine(_dir, "round-done"), $"{_facts - _failures} of {_facts}");
		}
	}

	// ---- pictures ---------------------------------------------------------------------------------

	private IEnumerable<bool> Shot()
	{
		long waiting = Stopwatch.GetTimestamp();
		while (MapEditorRoot.Current == null && Ms(waiting) < 30_000)
		{
			yield return true;
		}

		MapEditorRoot editor = MapEditorRoot.Current;
		Fact(editor != null && editor.Editing == null && editor.Modal, "--edit with no name opens the editor's list of maps");
		if (editor == null)
		{
			yield break;
		}

		foreach (bool step in Picture("le4-list"))
		{
			yield return step;
		}

		string error = null;
		Fact(MapCatalog.TryFind("hills", out MapInfo bundled) && editor.Clone(bundled.Path, "le4-shot", out error),
			$"clone the bundled map: {error ?? editor.Message}");
		Vector2 size = GetViewport().GetVisibleRect().Size;
		Input.WarpMouse(new Vector2(size.X * 0.45f, size.Y * 0.55f));
		for (int i = 0; i < 10; i++)
		{
			yield return true;
		}

		Fact(editor.Cursor != null, $"the cursor is on the ground: {editor.Cursor}");
		foreach (bool step in Picture("le4-editor"))
		{
			yield return step;
		}

		if (editor.Cursor is { } at)
		{
			editor.SelectPreset(2);
			editor.BeginStroke(at.X, at.Z, false);
			for (int i = 0; i < 60; i++)
			{
				editor.StrokeTo(at.X, at.Z, 1f / 60f);
				yield return true;
			}

			editor.EndStroke();
		}

		editor.SetOverlay(TerrainOverlay.Slope);
		editor.ShowIssues();
		foreach (bool step in Picture("le4-editor-slope"))
		{
			yield return step;
		}

		// LE5: the prefab tool's ghost under the cursor, over the buildable overlay.
		editor.SetOverlay(TerrainOverlay.Buildable);
		editor.SelectTool(EditorTool.Prefabs);
		editor.SelectPrefab(PrefabSpecs.StructurePillbox);
		for (int i = 0; i < 10; i++)
		{
			yield return true;
		}

		Fact(editor.GhostCheck != null, $"the ghost stands under the cursor: {editor.GhostCheck?.Result}");
		foreach (bool step in Picture("le5-prefabs-buildable"))
		{
			yield return step;
		}

		if (editor.Cursor is { } there)
		{
			editor.SelectPrefab(PrefabSpecs.Barracks);
			editor.PlaceAt(there.X + 25f, there.Z);
		}

		editor.SetOverlay(TerrainOverlay.None);
		for (int i = 0; i < 10; i++)
		{
			yield return true;
		}

		foreach (bool step in Picture("le5-prefabs"))
		{
			yield return step;
		}
	}

	private IEnumerable<bool> Picture(string name)
	{
		for (int i = 0; i < 20; i++)
		{
			yield return true;
		}

		Image image = GetViewport().GetTexture().GetImage();
		string file = Path.Combine(_dir ?? ".", name + ".png");
		Error saved = image.SavePng(file);
		Fact(saved == Error.Ok && image.GetWidth() > 0, $"{Path.GetFileName(file)}: {image.GetWidth()} x {image.GetHeight()}");
	}

	// ---- the test process -------------------------------------------------------------------------

	private IEnumerable<bool> TestChild()
	{
		string hash = Env("LE4_EXPECT_HASH", string.Empty);
		string path = Env("LE4_EXPECT_PATH", string.Empty);
		string[] ground = Env("LE4_EXPECT_GROUND", "0,0,0").Split(',');
		float gx = float.Parse(ground[0], CultureInfo.InvariantCulture);
		float gz = float.Parse(ground[1], CultureInfo.InvariantCulture);
		float gh = float.Parse(ground[2], CultureInfo.InvariantCulture);

		long waiting = Stopwatch.GetTimestamp();
		while (LevelRoot.Current?.Terrain == null && Ms(waiting) < 60_000)
		{
			yield return true;
		}

		Fact(Bootstrap.Options.Practice && Session.Map.IsData && Session.Map.Path == path && Session.Map.Hash == hash,
			$"the test process plays the practice round on the saved file: {Session.Map}, {Session.Map.Path}");
		LevelRoot level = LevelRoot.Current;
		if (level?.Terrain == null)
		{
			yield break;
		}

		Fact(level.Terrain.HeightAt(gx, gz) == gh,
			$"its ground is the edited ground: ({F(gx, "0")}, {F(gz, "0")}) at {F(level.Terrain.HeightAt(gx, gz))} m, as the editor left it");

		CombatManager combat = CombatManager.Instance;
		while (!(combat?.Match.IsLive ?? false) && Ms(waiting) < 240_000)
		{
			yield return true;
		}

		bool live = combat?.Match.IsLive ?? false;
		Fact(live, "the round is live");
		if (live)
		{
			Mark("test-child-live");
		}
	}
}
