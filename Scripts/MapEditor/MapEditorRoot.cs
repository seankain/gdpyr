using System;
using System.Collections.Generic;
using System.IO;
using Gdpyr.Core;
using Gdpyr.Level;
using Gdpyr.Sim.Level;
using Gdpyr.Ui.Spectator;
using Godot;

namespace Gdpyr.MapEditor;

/// <summary>
/// The map editor (docs/LEVEL_EDITOR.md §8): the root of <c>Scenes/MapEditor/MapEditor.tscn</c>.
/// Not a <see cref="WorldRoot"/>: the session stays in its menu phase, so nothing is simulated
/// while a map is edited (§8.1). It holds a <see cref="MapEditSession"/> — the document, its
/// undo and the brush — and builds the world from it with the round's own
/// <see cref="TerrainNode"/> and <see cref="LevelBuilder"/>, so what is edited is what will be
/// played.
///
/// Every command the panels and keys give is a public method here, and an engine check drives
/// the editor with no window through the same ones (§11, LE4): a stroke, undo, save, open, a
/// test. The widgets are in <c>MapEditorRoot.Ui.cs</c>; which key does what, and what the
/// status line says, are <see cref="MapEditorControls"/>', which <c>dotnet test</c> answers.
///
/// One's own maps are <c>user://maps/&lt;name&gt;.map</c> (<see cref="MapStore"/>): an unfinished
/// map is a file, saved and opened; what may not be hosted is a map the validator finds an
/// error in (§8.7).
/// </summary>
public partial class MapEditorRoot : Node3D
{
	public const string ScenePath = "res://Scenes/MapEditor/MapEditor.tscn";

	/// <summary>How high over the ground the ring is drawn [m].</summary>
	private const float RingLift = 0.15f;

	/// <summary>The editor in the tree, or null.</summary>
	public static MapEditorRoot Current { get; private set; }

	/// <summary>
	/// One's own map to open when the editor next opens: made new under that name when there is
	/// none. Null for the editor's list. The menu sets it, from its button or from <c>--edit</c>.
	/// </summary>
	public static string OpenOnEnter { get; set; }

	/// <summary>The map being edited, its undo and the brush; null while the list is up.</summary>
	public MapEditSession Editing { get; private set; }

	/// <summary>Its library name: <c>user://maps/&lt;FileName&gt;.map</c>.</summary>
	public string FileName { get; private set; }

	/// <summary>Whether it has a file: it was opened, cloned, or saved since it was made.</summary>
	public bool EverSaved { get; private set; }

	/// <summary>The SHA-256 of the file as it was last opened or saved, hex; null when it has none.</summary>
	public string SavedHash { get; private set; }

	public TerrainNode Terrain { get; private set; }

	/// <summary>What <see cref="LevelBuilder"/> last built of the map's objects.</summary>
	public LevelBuilder.Summary Built { get; private set; }

	/// <summary>The node built for each object, in the file's order; null for a structure, which a round puts up.</summary>
	public IReadOnlyList<Node3D> BuiltNodes => _builtNodes;

	private readonly List<Node3D> _builtNodes = new();

	/// <summary>How many times the objects and the perimeter have been built.</summary>
	public int ObjectBuilds { get; private set; }

	/// <summary>An autosave newer than the file, waiting to be taken back or turned down (§8.6); null when none is.</summary>
	public MapDocument AutosaveOffered { get; private set; }

	public EditorTool Tool { get; private set; } = EditorTool.Terrain;

	public TerrainOverlay Overlay { get; private set; } = TerrainOverlay.None;

	/// <summary>Where the cursor meets the ground, by §6.1's ray; null when it does not.</summary>
	public Vector3? Cursor { get; private set; }

	/// <summary>The last thing the editor said: under the view, and in the log.</summary>
	public string Message { get; private set; } = string.Empty;

	/// <summary>The last test process's id, or -1 (LE-D19).</summary>
	public int TestProcessId { get; private set; } = -1;

	/// <summary>The arguments the last test process was started with.</summary>
	public IReadOnlyList<string> TestArgumentsUsed { get; private set; } = Array.Empty<string>();

	/// <summary>Where one's own maps are, on this machine.</summary>
	public static string Directory => MapLibrary.UserRoot;

	/// <summary>The file the map is saved as.</summary>
	public string SavedPath => FileName == null ? null : MapStore.PathOf(Directory, FileName);

	/// <summary>The editor draws unless this is a dedicated server; a headless one builds its meshes all the same, for a check to read.</summary>
	private static bool Draws => !Bootstrap.IsDedicatedServer;

	private static long Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

	private Node3D _world;
	private Node3D _built;
	private SpectatorCamera _camera;
	private MeshInstance3D _ring;
	private ImmediateMesh _ringMesh;
	private StandardMaterial3D _ringMaterial;
	private AutosaveClock _autosave = new();
	private Vector2? _rampFrom;
	private bool _strokeFromMouse;
	private float _steepest;

	public override void _Ready()
	{
		Current = this;
		Session.EnterMenu();
		Input.MouseMode = Input.MouseModeEnum.Visible;

		_camera = new SpectatorCamera { Name = "Camera", LookButton = MouseButton.Middle, CtrlIsCommand = true };
		AddChild(_camera);
		// In group navmesh, so that the deep check's bake reads the ground and everything on it (LE5).
		_world = new Node3D { Name = "World" };
		_world.AddToGroup("navmesh");
		AddChild(_world);
		BuildRing();
		BuildGhost();
		_navigation = new NavigationRegion3D { Name = "Navigation" };
		AddChild(_navigation);
		BuildUi();

		string open = OpenOnEnter;
		OpenOnEnter = null;
		string error = null;
		bool opened = open != null && (MapStore.Exists(Directory, open) ? Open(open, out error) : NewMap(open, out error));
		if (!opened)
		{
			ShowList();
			if (error != null)
			{
				Say(error);
			}
		}

		GD.Print($"[editor] map editor: {Directory}" + (opened ? $", editing {FileName}.map" : ", the list of maps"));
	}

	public override void _ExitTree()
	{
		if (Current == this)
		{
			Current = null;
		}
	}

	// ---- opening ------------------------------------------------------------------------------

	/// <summary>A new map, level ground and nothing on it, to be saved as one's own <paramref name="name"/>.</summary>
	public bool NewMap(string name, out string error)
	{
		if (!FreeName(name, out name, out error))
		{
			return false;
		}

		MapDocument document = MapDocument.Create(Guid.NewGuid(), name, Now);
		Load(document, name, false, null);
		Say($"a new map, {name}: not saved yet");
		return true;
	}

	/// <summary>Opens one's own map: read as a file is (§6.8), not validated — an unfinished map is opened to be finished.</summary>
	public bool Open(string name, out string error)
	{
		if (!MapStore.TryOpen(Directory, name, out MapDocument document, out byte[] file, out error))
		{
			Say(error);
			return false;
		}

		MapNames.TryParseName(name, out name, out _);
		Load(document, name, true, MapFile.HashHex(file));
		Say($"opened {name}.map, revision {document.Revision}: {MapStore.Describe(Editing.Report)}");
		if (MapStore.TryReadAutosave(Directory, name, out MapDocument autosave, out _))
		{
			AutosaveOffered = autosave;
			OfferAutosave();
		}

		return true;
	}

	/// <summary>
	/// A copy of any map to be one's own, under a new name (§9.2): a new GUID, the source's
	/// GUID and hash as its parent. Saved at once, and opened. <paramref name="path"/> is a
	/// bundled map's <c>res://</c> path, one's own, or a received one's.
	/// </summary>
	public bool Clone(string path, string name, out string error)
	{
		if (!FreeName(name, out name, out error))
		{
			return false;
		}

		if (!MapLibrary.TryReadBytes(path, out byte[] bytes, out error) || !MapFile.TryRead(bytes, out MapDocument source, out error))
		{
			Say(error);
			return false;
		}

		MapDocument copy = MapStore.Fork(source, MapFile.Hash(bytes), Guid.NewGuid(), null, Now);
		if (!MapStore.TrySave(copy, Directory, name, Now, out byte[] file, out error))
		{
			Say(error);
			return false;
		}

		Load(copy, name, true, MapFile.HashHex(file));
		Say($"cloned {Path.GetFileName(path)} as {name}.map: {MapStore.Describe(Editing.Report)}");
		GD.Print($"[editor] cloned {path} (sha-256 {MapFile.HashHex(bytes)[..8]}…) as {SavedPath}");
		return true;
	}

	private bool FreeName(string name, out string parsed, out string error)
	{
		if (!MapNames.TryParseName(name, out parsed, out error))
		{
			Say(error);
			return false;
		}

		if (MapStore.Exists(Directory, parsed))
		{
			error = $"there is already a map called {parsed}";
			Say(error);
			return false;
		}

		return true;
	}

	private void Load(MapDocument document, string name, bool everSaved, string hash)
	{
		Editing = new MapEditSession(document);
		FileName = name;
		EverSaved = everSaved;
		SavedHash = hash;
		AutosaveOffered = null;
		_autosave = new AutosaveClock();
		_rampFrom = null;
		_strokeFromMouse = false;
		Selected = -1;
		DeepReport = null;
		_ghostChanges = -1;

		BuildWorld();
		float highest = Terrain.MaxHeight;
		_camera.Place(new Vector3(0f, highest + 45f, 55f), -0.75f);
		HideList();
		RefreshUi(true);
		GD.Print($"[editor] editing '{document.Name}' ({name}.map), revision {document.Revision},"
			+ $" {document.Objects.Count} objects, {MapStore.Describe(Editing.Report)}");
	}

	/// <summary>The ground, then the map's objects and its walls, from the document.</summary>
	private void BuildWorld()
	{
		foreach (Node child in _world.GetChildren())
		{
			_world.RemoveChild(child);
			child.QueueFree();
		}

		_built = null;
		Terrain = new TerrainNode { Name = "Terrain", Buildable = Overlay == TerrainOverlay.Buildable };
		Terrain.Build(Editing.Document, Draws);
		_world.AddChild(Terrain);
		Terrain.SetOverlay((int)Overlay);
		RebuildObjects();
	}

	/// <summary>
	/// The map's objects and the perimeter, built again: after an edit that put one in or took
	/// one out, or that moved the ground along the field's edge, which the walls span (§7.1).
	/// Nothing built here ticks: the session is in its menu phase, and the node they are under
	/// is not processed.
	/// </summary>
	private void RebuildObjects()
	{
		if (_built != null)
		{
			_world.RemoveChild(_built);
			_built.QueueFree();
		}

		_built = new Node3D { Name = "Built", ProcessMode = ProcessModeEnum.Disabled };
		_world.AddChild(_built);
		Built = LevelBuilder.Build(_built, _built, Editing.Document, Terrain, Draws, _builtNodes);
		BuildStructureStandIns();
		ObjectBuilds++;
		DrawSelection();
	}

	// ---- the terrain tool ---------------------------------------------------------------------

	/// <summary>Presses the button at (x, z): raising, or lowering on the right button (§8.3).</summary>
	public bool BeginStroke(float x, float z, bool lower)
	{
		if (Editing == null || Tool != EditorTool.Terrain || !Editing.BeginStroke(x, z, lower))
		{
			return false;
		}

		Terrain.RefreshMeshes(Editing.StrokeTouched);
		return true;
	}

	/// <summary>The button held <paramref name="seconds"/> more with the cursor at (x, z): the meshes drawn again where it dabbed.</summary>
	public void StrokeTo(float x, float z, float seconds)
	{
		if (Editing?.Stroking == true)
		{
			Terrain.RefreshMeshes(Editing.StrokeTo(x, z, seconds));
		}
	}

	/// <summary>Lets the button go: the stroke rounded, pads kept, one step to undo, and the collider given the ground it changed.</summary>
	public EditOutcome EndStroke()
	{
		_strokeFromMouse = false;
		if (Editing?.Stroking != true)
		{
			return default;
		}

		int dabs = Editing.StrokeDabs;
		EditOutcome outcome = Editing.EndStroke();
		Apply(outcome);
		Say(outcome.Step ? $"stroke: {dabs} dabs, {outcome}" : "stroke: no change");
		return outcome;
	}

	/// <summary>Drops the stroke under way, or a ramp's first click.</summary>
	public void Cancel()
	{
		_strokeFromMouse = false;
		if (Editing?.Stroking == true)
		{
			Terrain.RefreshMeshes(Editing.CancelStroke());
			Say("stroke dropped");
		}
		else if (_rampFrom != null)
		{
			_rampFrom = null;
			Say("ramp dropped");
		}
	}

	/// <summary>A ramp between two points, as wide as the ring (§6.2).</summary>
	public RampResult Ramp(Vector2 from, Vector2 to)
	{
		if (Editing == null)
		{
			return RampResult.TooSmall;
		}

		RampResult result = Editing.Ramp(from.X, from.Y, to.X, to.Y, out EditOutcome outcome);
		if (result == RampResult.Ok)
		{
			Apply(outcome);
		}

		Say(MapEditorControls.Describe(result) + (result == RampResult.Ok ? $": {outcome}" : string.Empty));
		return result;
	}

	public bool Undo()
	{
		if (Editing == null || !Editing.Undo(out EditOutcome outcome))
		{
			Say("nothing to undo");
			return false;
		}

		Apply(outcome);
		Say($"undone: {outcome}; {Editing.History.UndoCount} steps left to undo");
		return true;
	}

	public bool Redo()
	{
		if (Editing == null || !Editing.Redo(out EditOutcome outcome))
		{
			Say("nothing to redo");
			return false;
		}

		Apply(outcome);
		Say($"redone: {outcome}");
		return true;
	}

	/// <summary>
	/// What an edit did, drawn: the meshes over everything it may have moved, the collider over
	/// what it changed, the water counted again, and the objects and walls built again when it
	/// changed them or the ground along the edge.
	/// </summary>
	private void Apply(in EditOutcome outcome)
	{
		Terrain.RefreshMeshes(outcome.Area);
		Terrain.RefreshColliders(outcome.Changed);
		Terrain.RefreshWater();
		SampleRect bounds = Editing.Document.Terrain.Bounds;
		SampleRect changed = outcome.Changed;
		bool edge = !changed.IsEmpty && (changed.MinX == 0 || changed.MinZ == 0 || changed.MaxX == bounds.MaxX
			|| changed.MaxZ == bounds.MaxZ);
		if (outcome.ObjectsChanged)
		{
			// An index may name another object now; whoever replaced one selects it again.
			Selected = -1;
		}

		if (outcome.ObjectsChanged || edge)
		{
			RebuildObjects();
		}

		RefreshUi(false);
	}

	/// <summary>Sets the brush; a stroke under way keeps the one it began with.</summary>
	public void SetBrush(BrushSettings brush)
	{
		if (Editing == null)
		{
			return;
		}

		BrushKind was = Editing.Brush.Kind;
		Editing.Brush = brush;
		if (Editing.Brush.Kind != BrushKind.Ramp)
		{
			_rampFrom = null;
		}

		RefreshBrush(was != Editing.Brush.Kind);
	}

	/// <summary>One of §6.2's presets.</summary>
	public void SelectPreset(int index)
	{
		if (index >= 0 && index < TerrainBrush.Presets.Length)
		{
			SetBrush(TerrainBrush.Presets[index].Settings);
			Say($"{TerrainBrush.Presets[index].Name}: {MapEditorControls.Describe(Editing?.Brush ?? default)}");
		}
	}

	/// <summary>The water or a band number, as its slider sets it: drawn again as it moves (§8.3).</summary>
	public void SetBand(BandNumber number, float value)
	{
		if (Editing == null)
		{
			return;
		}

		MapBands bands = MapEditorControls.SetBand(Editing.Document.Bands, number, value);
		if (Editing.SetBands(bands))
		{
			Terrain.SetBands(bands.Settings);
			RefreshUi(false);
		}
	}

	public void SetOverlay(TerrainOverlay overlay)
	{
		Overlay = overlay;
		if (Terrain != null)
		{
			Terrain.Buildable = overlay == TerrainOverlay.Buildable;
			Terrain.SetOverlay((int)overlay);
		}

		RefreshOverlayPicker();
		Say($"overlay: {MapEditorControls.Name(overlay)}");
	}

	public void SelectTool(EditorTool tool)
	{
		if (!MapEditorControls.IsBuilt(tool))
		{
			Say(MapEditorControls.NotBuilt(tool));
			return;
		}

		Tool = tool;
		if (tool != EditorTool.Prefabs)
		{
			Select(-1);
		}

		RefreshUi(false);
	}

	/// <summary>The map's name, author and description (§9.2).</summary>
	public bool SetText(string name, string author, string description, out string error)
	{
		error = "no map is open";
		if (Editing == null || !Editing.TrySetText(name, author, description, out error))
		{
			Say(error);
			return false;
		}

		RefreshUi(false);
		return true;
	}

	// ---- saving --------------------------------------------------------------------------------

	/// <summary>
	/// Saves the map as one's own (§8.6): the next revision, through a file beside it and a
	/// rename, the last kept as <c>.bak</c>, the autosave dropped. Errors in the map do not stop
	/// it (§8.7).
	/// </summary>
	public bool Save(out string error)
	{
		error = "no map is open";
		if (Editing == null)
		{
			return false;
		}

		if (Editing.Stroking)
		{
			EndStroke();
		}

		MapDocument document = Editing.Document;
		if (!MapStore.TrySave(document, Directory, FileName, Now, out byte[] file, out error))
		{
			Say($"not saved: {error}");
			return false;
		}

		Editing.MarkSaved();
		EverSaved = true;
		SavedHash = MapFile.HashHex(file);
		AutosaveOffered = null;
		_autosave.Written(Editing.Changes);
		MapReport report = Editing.Report;
		Say($"saved {FileName}.map, revision {document.Revision}, {file.Length:N0} bytes, sha-256 {SavedHash[..8]}…"
			+ (report.IsPlayable ? string.Empty : $"; it does not play yet: {MapStore.Describe(report)}"));
		GD.Print($"[editor] saved {SavedPath}: revision {document.Revision}, {file.Length} bytes, sha-256 {SavedHash},"
			+ $" {MapStore.Describe(report)}");
		RefreshUi(false);
		return true;
	}

	/// <summary>
	/// Saves the map under another name. A map that has a file becomes a fork of it — a new
	/// GUID, that file as its parent — so two files never share a lineage; one that has none
	/// is simply saved under the new name. Decided by LE4.
	/// </summary>
	public bool SaveAs(string name, out string error)
	{
		if (Editing == null)
		{
			error = "no map is open";
			return false;
		}

		if (!FreeName(name, out name, out error))
		{
			return false;
		}

		MapDocument document = Editing.Document;
		Guid id = document.Id;
		Guid parent = document.ParentId;
		byte[] parentHash = document.ParentHash;
		uint revision = document.Revision;
		long created = document.CreatedUnixSeconds;
		long modified = document.ModifiedUnixSeconds;
		string file = FileName;
		if (EverSaved && SavedHash != null)
		{
			MapStore.BecomeFork(document, Convert.FromHexString(SavedHash), Guid.NewGuid(), Now);
		}

		FileName = name;
		if (!Save(out error))
		{
			document.Id = id;
			document.ParentId = parent;
			document.ParentHash = parentHash;
			document.Revision = revision;
			document.CreatedUnixSeconds = created;
			document.ModifiedUnixSeconds = modified;
			FileName = file;
			return false;
		}

		return true;
	}

	/// <summary>Writes the autosave now (§8.6): what the clock does a minute after a change not yet written.</summary>
	public bool Autosave(out string error)
	{
		error = "no map is open";
		if (Editing == null || !MapStore.TryAutosave(Editing.Document, Directory, FileName, out error))
		{
			Say($"not autosaved: {error}");
			return false;
		}

		_autosave.Written(Editing.Changes);
		GD.Print($"[editor] autosaved {MapStore.AutosavePathOf(Directory, FileName)}");
		Message = $"autosaved {FileName}{MapStore.AutosaveSuffix}{MapFile.Extension}";
		RefreshStatus();
		return true;
	}

	/// <summary>Takes the offered autosave back: the map as it was when it was autosaved, not saved.</summary>
	public bool RestoreAutosave()
	{
		MapDocument offered = AutosaveOffered;
		if (offered == null || Editing == null)
		{
			return false;
		}

		Load(offered, FileName, EverSaved, SavedHash);
		Editing.MarkUnsaved();
		Say("the autosave is taken back: save to keep it");
		return true;
	}

	/// <summary>Turns the offered autosave down, and drops it.</summary>
	public void DiscardAutosave()
	{
		if (AutosaveOffered != null)
		{
			MapStore.DiscardAutosave(Directory, FileName);
			AutosaveOffered = null;
			Say("the autosave is dropped");
		}
	}

	/// <summary>Copies the saved file to <paramref name="path"/> (§9.2), saving first what is not saved.</summary>
	public bool Export(string path, out string error)
	{
		error = null;
		if (Editing == null || ((!EverSaved || Editing.Dirty) && !Save(out error)))
		{
			error ??= "no map is open";
			return false;
		}

		try
		{
			File.Copy(SavedPath, path, true);
		}
		catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException
			or ArgumentException)
		{
			error = e.Message;
			Say($"not exported: {error}");
			return false;
		}

		Say($"exported {FileName}.map to {path}");
		GD.Print($"[editor] exported {SavedPath} to {path}");
		return true;
	}

	/// <summary>
	/// <b>Test</b> (LE-D19, §8.7): saves what is not saved, and plays the saved file in a second
	/// process — the practice round, on this map — while this one stays where it is. A map saved
	/// and unchanged since is not saved again, which would only make another revision of it. A
	/// map the validator finds an error in is saved and not played: the second process would
	/// refuse it.
	/// </summary>
	public bool Test(out string error)
	{
		error = "no map is open";
		if (Editing == null || ((!EverSaved || Editing.Dirty || Editing.Stroking) && !Save(out error)))
		{
			return false;
		}

		error = null;

		MapReport report = Editing.Report;
		if (!report.IsPlayable)
		{
			foreach (MapIssue issue in report.Issues)
			{
				if (issue.Severity == MapIssueSeverity.Error)
				{
					error = $"it does not play yet: {issue.Message}" + (report.Errors > 1 ? $", and {report.Errors - 1} more" : string.Empty);
					break;
				}
			}

			Say(error);
			ShowIssues();
			return false;
		}

		string project = OS.HasFeature("template") ? null : ProjectSettings.GlobalizePath("res://");
		bool headless = DisplayServer.GetName() == "headless";
		List<string> arguments = MapEditorControls.TestArguments(SavedPath, project, headless);
		int pid = OS.CreateInstance(arguments.ToArray());
		if (pid <= 0)
		{
			error = $"the test process did not start ({pid})";
			Say(error);
			return false;
		}

		TestProcessId = pid;
		TestArgumentsUsed = arguments;
		Say($"testing {FileName}.map in process {pid}: close it to come back here");
		GD.Print($"[editor] testing {SavedPath} (sha-256 {SavedHash}) in process {pid}: {string.Join(' ', arguments)}");
		return true;
	}

	/// <summary>Back to the main menu: unsaved changes are asked about first, unless <paramref name="discard"/>.</summary>
	public void Exit(bool discard = false)
	{
		if (Editing?.Dirty == true && !discard)
		{
			ConfirmExit();
			return;
		}

		if (Editing != null && discard)
		{
			MapStore.DiscardAutosave(Directory, FileName);
		}

		GD.Print("[editor] back to the menu");
		GetTree().ChangeSceneToFile(Session.MenuScenePath);
	}

	/// <summary>Closes the map, back to the editor's list: unsaved changes are asked about first, unless <paramref name="discard"/>.</summary>
	public void CloseMap(bool discard = false)
	{
		if (Editing?.Dirty == true && !discard)
		{
			_exitToList = true;
			_exitDialog.PopupCentered();
			return;
		}

		if (Editing != null && discard)
		{
			MapStore.DiscardAutosave(Directory, FileName);
		}

		Editing = null;
		FileName = null;
		Terrain = null;
		_built = null;
		foreach (Node child in _world.GetChildren())
		{
			_world.RemoveChild(child);
			child.QueueFree();
		}

		ShowList();
	}

	// ---- the frame -----------------------------------------------------------------------------

	public override void _Process(double delta)
	{
		if (Editing == null)
		{
			return;
		}

		Cursor = UnderMouse();

		// A button let go outside the window sends no release here: neither held is the end of the stroke.
		if (_strokeFromMouse && Editing.Stroking && !Input.IsMouseButtonPressed(MouseButton.Left)
			&& !Input.IsMouseButtonPressed(MouseButton.Right))
		{
			EndStroke();
		}

		if (_strokeFromMouse && Editing.Stroking && Cursor is { } at)
		{
			StrokeTo(at.X, at.Z, (float)delta);
		}

		if (!Editing.Stroking && _autosave.Tick((float)delta, Editing.Changes))
		{
			Autosave(out _);
		}

		_steepest = Cursor is { } c
			? MapEditorControls.SteepestFaceDegrees(Editing.Document.Terrain, c.X, c.Z, Editing.Brush.RadiusMeters)
			: 0f;
		DrawRing();
		UpdateGhost();
		RefreshStatus();
	}

	public override void _UnhandledInput(InputEvent @event)
	{
		if (Editing == null || Modal)
		{
			return;
		}

		if (@event is InputEventKey key && key.Pressed)
		{
			if (InputFocus.TextEntry)
			{
				return;
			}

			Key code = key.Keycode != Key.None ? key.Keycode : key.PhysicalKeycode;
			EditorAction action = MapEditorControls.ActionOf(code, key.CtrlPressed || key.MetaPressed, key.ShiftPressed);
			if (action == EditorAction.None || (key.Echo && !Repeats(action)))
			{
				return;
			}

			Do(action);
			GetViewport().SetInputAsHandled();
		}
		else if (@event is InputEventMouseButton button && button.ButtonIndex is MouseButton.Left or MouseButton.Right)
		{
			if (button.Pressed)
			{
				Press(button.ButtonIndex == MouseButton.Right);
			}
			else if (_strokeFromMouse)
			{
				EndStroke();
			}

			GetViewport().SetInputAsHandled();
		}
	}

	private static bool Repeats(EditorAction action) => action is EditorAction.RadiusDown or EditorAction.RadiusUp
		or EditorAction.AmountDown or EditorAction.AmountUp or EditorAction.ShapeDown or EditorAction.ShapeUp
		or EditorAction.Undo or EditorAction.Redo;

	/// <summary>What a key does (§8.2).</summary>
	public void Do(EditorAction action)
	{
		if (MapEditorControls.ToolOf(action) is { } tool)
		{
			SelectTool(tool);
			return;
		}

		switch (action)
		{
			case EditorAction.RadiusDown:
			case EditorAction.RadiusUp:
			case EditorAction.AmountDown:
			case EditorAction.AmountUp:
			case EditorAction.ShapeDown:
			case EditorAction.ShapeUp:
				SetBrush(MapEditorControls.Step(Editing.Brush, action));
				Say(MapEditorControls.Describe(Editing.Brush));
				break;
			case EditorAction.NextOverlay:
				SetOverlay(MapEditorControls.Next(Overlay));
				break;
			case EditorAction.Undo:
				Undo();
				break;
			case EditorAction.Redo:
				Redo();
				break;
			case EditorAction.Save:
				Save(out _);
				break;
			case EditorAction.Test:
				Test(out _);
				break;
			case EditorAction.StraightDown:
				_camera.LookStraightDown();
				break;
			case EditorAction.Cancel:
				Cancel();
				Select(-1);
				break;
			case EditorAction.TurnLeft:
			case EditorAction.TurnRight:
				if (Tool == EditorTool.Prefabs)
				{
					Turn(action == EditorAction.TurnLeft ? 1 : -1);
				}

				break;
			case EditorAction.SnapToGrid:
				if (Tool == EditorTool.Prefabs)
				{
					ToggleSnap();
				}

				break;
			case EditorAction.TakeUp:
				TakeUpSelected();
				break;
			default:
				Say("bridges are LE6's");
				break;
		}
	}

	/// <summary>A button pressed over the view: a stroke, a ramp's click, or the prefab tool's click.</summary>
	private void Press(bool lower)
	{
		if (Tool == EditorTool.Prefabs)
		{
			PressPrefab(lower);
			return;
		}

		if (Tool != EditorTool.Terrain || Cursor is not { } at)
		{
			return;
		}

		if (Editing.Brush.Kind == BrushKind.Ramp)
		{
			if (lower)
			{
				Cancel();
			}
			else if (_rampFrom is not { } from)
			{
				_rampFrom = new Vector2(at.X, at.Z);
				Say("ramp: click where it ends; Esc or the right button drops it");
			}
			else
			{
				_rampFrom = null;
				Ramp(from, new Vector2(at.X, at.Z));
			}

			return;
		}

		_strokeFromMouse = BeginStroke(at.X, at.Z, lower);
	}

	/// <summary>Where the ray through the mouse meets the ground, by §6.1's walk: not the physics server's, which a stroke has not told yet.</summary>
	private Vector3? UnderMouse()
	{
		Viewport viewport = GetViewport();
		Camera3D camera = viewport.GetCamera3D();
		if (camera == null || OverUi())
		{
			return null;
		}

		Vector2 mouse = viewport.GetMousePosition();
		Vector3 from = camera.ProjectRayOrigin(mouse);
		Vector3 direction = camera.ProjectRayNormal(mouse);
		return Editing.Document.Terrain.Raycast(from, direction, 4000f, out Vector3 hit) ? hit : null;
	}

	// ---- the ring ------------------------------------------------------------------------------

	private void BuildRing()
	{
		_ringMesh = new ImmediateMesh();
		_ringMaterial = new StandardMaterial3D
		{
			ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
			VertexColorUseAsAlbedo = true,
			NoDepthTest = true,
		};
		_ring = new MeshInstance3D
		{
			Name = "Ring",
			Mesh = _ringMesh,
			MaterialOverride = _ringMaterial,
			CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
		};
		AddChild(_ring);
	}

	/// <summary>
	/// The brush's ring on the ground (§8.3), coloured as the slope overlay colours the steepest
	/// face under it; a ramp's line from its first click.
	/// </summary>
	private void DrawRing()
	{
		_ringMesh.ClearSurfaces();
		if (Cursor is not { } at || Tool != EditorTool.Terrain)
		{
			return;
		}

		HeightField field = Editing.Document.Terrain;
		float radius = Editing.Brush.RadiusMeters;
		Color colour = MapEditorControls.ColourOf(MapEditorControls.ClassOf(_steepest));
		const int Segments = 72;
		_ringMesh.SurfaceBegin(Mesh.PrimitiveType.LineStrip);
		for (int i = 0; i <= Segments; i++)
		{
			float angle = i * Mathf.Tau / Segments;
			float x = at.X + (radius * Mathf.Cos(angle));
			float z = at.Z + (radius * Mathf.Sin(angle));
			_ringMesh.SurfaceSetColor(colour);
			_ringMesh.SurfaceAddVertex(new Vector3(x, field.Sample(x, z) + RingLift, z));
		}

		_ringMesh.SurfaceEnd();

		if (_rampFrom is { } from)
		{
			_ringMesh.SurfaceBegin(Mesh.PrimitiveType.Lines);
			_ringMesh.SurfaceSetColor(Colors.White);
			_ringMesh.SurfaceAddVertex(new Vector3(from.X, field.Sample(from.X, from.Y) + RingLift, from.Y));
			_ringMesh.SurfaceSetColor(Colors.White);
			_ringMesh.SurfaceAddVertex(at + (Vector3.Up * RingLift));
			_ringMesh.SurfaceEnd();
		}
	}

	/// <summary>Says something under the view and in the log.</summary>
	private void Say(string message)
	{
		Message = message ?? string.Empty;
		if (!string.IsNullOrEmpty(message))
		{
			GD.Print($"[editor] {message}");
		}

		RefreshStatus();
	}
}
