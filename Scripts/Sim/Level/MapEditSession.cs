using System;
using System.Collections.Generic;
using Godot;

namespace Gdpyr.Sim.Level;

/// <summary>
/// What an edit that ended, or one undone or redone, did to a document: what the editor
/// draws again and gives the collider again (docs/LEVEL_EDITOR.md §8.3, §8.6).
/// </summary>
public readonly struct EditOutcome
{
	/// <summary>
	/// Every sample the edit may have moved, as the ground is drawn: a stroke's dabs, the
	/// rounding that ended it and the pads kept after it. The meshes are drawn again over
	/// it, and the collider's chunks given their triangles again.
	/// </summary>
	public readonly SampleRect Area;

	/// <summary>The stored samples that are not what they were: the rectangle the step to undo holds. Empty when none is.</summary>
	public readonly SampleRect Changed;

	/// <summary>Whether it was kept as a step to undo: an edit that changes no stored sample is not one (§8.6).</summary>
	public readonly bool Step;

	/// <summary>Whether objects were put in, taken out or replaced: the built world is built again.</summary>
	public readonly bool ObjectsChanged;

	public EditOutcome(SampleRect area, SampleRect changed, bool step, bool objectsChanged)
	{
		Area = area;
		Changed = changed;
		Step = step;
		ObjectsChanged = objectsChanged;
	}

	public override string ToString() =>
		Step ? $"a step over {Changed}, {Changed.Count} samples" + (ObjectsChanged ? ", and objects" : string.Empty) : "no change";
}

/// <summary>
/// The map editor's commands (docs/LEVEL_EDITOR.md §5, §8): a document, its undo, the brush
/// the terrain tool is set to, and the stroke under way. The editor's panels and keys call
/// these, and so does an engine check that drives the editor with no window (§11, LE4);
/// nothing here knows there is a screen.
///
/// <b>A stroke</b> is a press of the button: <see cref="BeginStroke"/> dabs once where it
/// was pressed, <see cref="StrokeTo"/> dabs at the cursor <see cref="DabsPerSecond"/> times
/// a second of the press, whatever the frame rate, and <see cref="EndStroke"/> rounds the
/// rectangle the dabs touched to stored steps (§6.1), keeps every pad (§6.5), and keeps
/// what changed as one step to undo (§8.6). Between dabs the ground is <c>float</c>, so a
/// slow brush accumulates; the document is whole steps again once the stroke ends.
///
/// How often a stroke dabs was §6.2's to leave to the editor: smooth and level move a
/// sample a share of the way each dab, however long it stood for. 30 a second, every one
/// of them <see cref="DabSeconds"/> long, makes a held brush the same ground on a slow
/// machine as on a fast one. Decided by LE4.
/// </summary>
public sealed class MapEditSession
{
	/// <summary>How often a held brush dabs (LE4).</summary>
	public const int DabsPerSecond = 30;

	/// <summary>The time step every dab is given [s].</summary>
	public const float DabSeconds = 1f / DabsPerSecond;

	/// <summary>
	/// The most dabs a frame catches up on: a frame that stalled does not lay a quarter
	/// second's backlog down at the one point the cursor is at when it ends.
	/// </summary>
	public const int MaxDabsAtOnce = 8;

	/// <summary>The preset the terrain tool starts on: Hill (§6.2).</summary>
	public const int DefaultPreset = 1;

	private BrushSettings _brush;
	private HeightField _before;
	private BrushStroke _stroke;
	private double _owed;
	private SampleRect _touched = SampleRect.Empty;
	private int _reportAt = -1;
	private MapReport _report;

	public MapEditSession(MapDocument document)
	{
		Document = document ?? throw new ArgumentNullException(nameof(document));
		_brush = TerrainBrush.Presets[DefaultPreset].Settings.Clamped();
	}

	public MapDocument Document { get; }

	public EditHistory History { get; } = new();

	/// <summary>What the terrain tool is set to, always inside its ranges. A stroke under way keeps what it began with.</summary>
	public BrushSettings Brush
	{
		get => _brush;
		set => _brush = value.Clamped();
	}

	/// <summary>Whether a stroke is under way: the button is down.</summary>
	public bool Stroking { get; private set; }

	/// <summary>Every sample the stroke under way has moved so far.</summary>
	public SampleRect StrokeTouched => _touched;

	/// <summary>Dabs the stroke under way has made.</summary>
	public int StrokeDabs { get; private set; }

	/// <summary>Whether the document has changed since it was last saved or opened.</summary>
	public bool Dirty { get; private set; }

	/// <summary>Changes made in this session, saved or not: what the autosave and the validator's report are kept by.</summary>
	public int Changes { get; private set; }

	/// <summary>§6.9 asked of the document as it is now; asked again only after a change.</summary>
	public MapReport Report
	{
		get
		{
			if (_report == null || _reportAt != Changes)
			{
				_report = MapValidator.Validate(Document);
				_reportAt = Changes;
			}

			return _report;
		}
	}

	// ---- a stroke ---------------------------------------------------------------------------

	/// <summary>
	/// Presses the button at (x, z): the brush as it is set now, raising, or lowering on the
	/// right button. One dab, there. False when a stroke is under way, the brush is a ramp
	/// (which is two clicks, <see cref="Ramp"/>), or the point is not a number.
	/// </summary>
	public bool BeginStroke(float x, float z, bool lower)
	{
		if (Stroking || _brush.Kind == BrushKind.Ramp || !float.IsFinite(x) || !float.IsFinite(z))
		{
			return false;
		}

		_before = Document.Terrain.Clone();
		_stroke = TerrainBrush.Begin(Document.Terrain, _brush, x, z, lower);
		_touched = SampleRect.Empty;
		_owed = 0d;
		StrokeDabs = 0;
		Stroking = true;
		DabAt(x, z);
		return true;
	}

	/// <summary>
	/// The button held for <paramref name="seconds"/> more, the cursor at (x, z): a dab there
	/// for every <see cref="DabSeconds"/> of it, and at most <see cref="MaxDabsAtOnce"/>.
	/// Returns what they moved, for the meshes to be drawn again over.
	/// </summary>
	public SampleRect StrokeTo(float x, float z, float seconds)
	{
		if (!Stroking || !(seconds > 0f) || !float.IsFinite(seconds))
		{
			return SampleRect.Empty;
		}

		// Counted in dabs, not seconds, so that 1/60 s sixty times is thirty dabs and not 29.
		_owed = Math.Min(_owed + (seconds * DabsPerSecond), MaxDabsAtOnce);
		SampleRect touched = SampleRect.Empty;
		while (_owed >= 1d - 1e-4)
		{
			_owed -= 1d;
			if (float.IsFinite(x) && float.IsFinite(z))
			{
				touched = touched.Union(DabAt(x, z));
			}
		}

		return touched;
	}

	/// <summary>
	/// Lets the button go: the stroke's rectangle rounded to stored steps, every pad kept,
	/// and what changed kept as one step to undo.
	/// </summary>
	public EditOutcome EndStroke()
	{
		if (!Stroking)
		{
			return default;
		}

		Stroking = false;
		Document.Terrain.Round(_touched);
		EditOutcome outcome = Keep(_touched);
		_touched = SampleRect.Empty;
		return outcome;
	}

	/// <summary>Drops the stroke under way: the ground as it was when it began. Returns what to draw again.</summary>
	public SampleRect CancelStroke()
	{
		if (!Stroking)
		{
			return SampleRect.Empty;
		}

		Stroking = false;
		SampleRect touched = _touched;
		HeightField field = Document.Terrain;
		for (int z = touched.MinZ; z <= touched.MaxZ; z++)
		{
			for (int x = touched.MinX; x <= touched.MaxX; x++)
			{
				field[x, z] = _before[x, z];
			}
		}

		_before = null;
		_touched = SampleRect.Empty;
		return touched;
	}

	private SampleRect DabAt(float x, float z)
	{
		SampleRect moved = TerrainBrush.Dab(Document.Terrain, _stroke, x, z, DabSeconds);
		_touched = _touched.Union(moved);
		StrokeDabs++;
		return moved;
	}

	// ---- a ramp -------------------------------------------------------------------------------

	/// <summary>
	/// A ramp between two clicks, as wide as the brush's ring — twice its radius — with its
	/// sides and ends skirted (§6.2), then rounded, the pads kept, and kept as a step. A
	/// ramp refused changes nothing.
	/// </summary>
	public RampResult Ramp(float fromX, float fromZ, float toX, float toZ, out EditOutcome outcome)
	{
		outcome = default;
		if (Stroking)
		{
			EndStroke();
		}

		_before = Document.Terrain.Clone();
		RampResult result = TerrainBrush.Ramp(Document.Terrain, fromX, fromZ, toX, toZ, 2f * _brush.RadiusMeters,
			out SampleRect touched);
		if (result == RampResult.Ok)
		{
			Document.Terrain.Round(touched);
			outcome = Keep(touched);
		}

		_before = null;
		return result;
	}

	// ---- undo -----------------------------------------------------------------------------------

	/// <summary>Reverts the newest step. False when there is none, or a stroke is under way.</summary>
	public bool Undo(out EditOutcome outcome)
	{
		outcome = default;
		MapEdit edit = History.NextUndo;
		if (Stroking || edit == null || !History.Undo(Document))
		{
			return false;
		}

		outcome = new EditOutcome(edit.Rect, edit.Rect, true, edit.Objects.Count > 0);
		Touch();
		return true;
	}

	/// <summary>Makes the step last undone again. False when there is none, or a stroke is under way.</summary>
	public bool Redo(out EditOutcome outcome)
	{
		outcome = default;
		MapEdit edit = History.NextRedo;
		if (Stroking || edit == null || !History.Redo(Document))
		{
			return false;
		}

		outcome = new EditOutcome(edit.Rect, edit.Rect, true, edit.Objects.Count > 0);
		Touch();
		return true;
	}

	// ---- prefabs (§8.4, LE5) ------------------------------------------------------------------------

	/// <summary>
	/// What placing would do, without doing it (§6.5): the ghost's answer. With
	/// <paramref name="replacing"/>, the object at that index taken up and placed again.
	/// </summary>
	public PlacementCheck Check(in PlacementRequest request, int replacing = -1) =>
		PrefabPlacement.Check(Document, request, replacing);

	/// <summary>
	/// Places a prefab and its pad as one step to undo, when §6.5's checks take it. The
	/// check comes back either way; <paramref name="outcome"/> is what to draw again.
	/// </summary>
	public PlacementCheck Place(in PlacementRequest request, out EditOutcome outcome) =>
		Commit(PrefabPlacement.Check(Document, request), out outcome);

	/// <summary>
	/// Takes the object at <paramref name="index"/> up and places it again as asked — turned,
	/// moved, or with other parameters — as one step, keeping its place in the file. Refused
	/// as §6.5 refuses a placement, and then the object stays where it was. The ground its old
	/// pad levelled stays, as any sculpted ground does.
	/// </summary>
	public PlacementCheck Replace(int index, in PlacementRequest request, out EditOutcome outcome)
	{
		if (index < 0 || index >= Document.Objects.Count)
		{
			outcome = default;
			return new PlacementCheck { Result = PrefabPlacementResult.UnknownPrefab };
		}

		return Commit(PrefabPlacement.Check(Document, request, index), out outcome);
	}

	/// <summary>Takes the object at <paramref name="index"/> up, as one step; its pad's ground stays.</summary>
	public bool TakeUp(int index, out EditOutcome outcome)
	{
		outcome = default;
		if (Stroking || index < 0 || index >= Document.Objects.Count)
		{
			return false;
		}

		History.Do(Document, PrefabPlacement.TakeUp(Document, index));
		Touch();
		outcome = new EditOutcome(SampleRect.Empty, SampleRect.Empty, true, true);
		return true;
	}

	/// <summary>
	/// The request that would place an object as it stands: its prefab, its point, its yaw and
	/// its parameters. What turning it or changing a parameter starts from.
	/// </summary>
	public static PlacementRequest RequestFor(MapObject placed)
	{
		var parameters = placed.Parameters.ToArray();
		return new PlacementRequest(placed.PrefabId, placed.Position.X, placed.Position.Z, placed.YawRadians,
			placed.Position.Y, parameters.Length > 0 ? parameters : null);
	}

	private PlacementCheck Commit(PlacementCheck check, out EditOutcome outcome)
	{
		outcome = default;
		if (!check.IsOk || Stroking)
		{
			return check;
		}

		History.Do(Document, check.Edit);
		Touch();
		outcome = new EditOutcome(check.Edit.Rect, check.Edit.Rect, true, true);
		return check;
	}

	/// <summary>
	/// The object under (x, z) in plan (§8.4: a click on a placed object selects it), or −1:
	/// one with a box over the point, else one whose footprint covers it, else a marker within
	/// <see cref="PickMarkerMeters"/>; of several, the one whose box or footprint is smallest, so
	/// that cover on a node's disc is picked over the node.
	/// </summary>
	public static int Pick(MapDocument document, float x, float z)
	{
		var point = new Vector3(x, 0f, z);
		int best = -1;
		float bestArea = float.MaxValue;
		var boxes = new List<PlacedBox>(4);
		for (int i = 0; i < document.Objects.Count; i++)
		{
			MapObject placed = document.Objects[i];
			boxes.Clear();
			PrefabSpecs.AddBoxes(placed, boxes);
			float area = float.MaxValue;
			foreach (PlacedBox box in boxes)
			{
				if (box.Plan.Contains(point))
				{
					area = MathF.Min(area, 4f * box.Plan.HalfWidth * box.Plan.HalfDepth);
				}
			}

			if (area == float.MaxValue && PrefabSpecs.TryGetPadArea(placed, out PadArea pad) && pad.Covers(x, z))
			{
				area = pad.IsDisc ? MathF.PI * pad.Radius * pad.Radius : 4f * pad.Rect.HalfWidth * pad.Rect.HalfDepth;
			}

			if (area == float.MaxValue && boxes.Count == 0)
			{
				float dx = placed.Position.X - x;
				float dz = placed.Position.Z - z;
				if ((dx * dx) + (dz * dz) <= PickMarkerMeters * PickMarkerMeters)
				{
					area = 0f;
				}
			}

			if (area < bestArea)
			{
				bestArea = area;
				best = i;
			}
		}

		return best;
	}

	/// <summary>How near a click must be to an object with no body — the strategist's anchor — to pick it [m].</summary>
	public const float PickMarkerMeters = 2f;

	// ---- the map's numbers and text -------------------------------------------------------------

	/// <summary>
	/// The water and the six band numbers (§6.4, §8.3): what the sliders set, as the file
	/// holds them. Not a step to undo: a slider is put back by moving it. False, and
	/// nothing changed, for numbers a map cannot mean or the ones it has.
	/// </summary>
	public bool SetBands(in MapBands bands)
	{
		if (bands.Equals(Document.Bands) || !bands.Settings.IsValid)
		{
			return false;
		}

		Document.Bands = bands;
		Touch();
		return true;
	}

	/// <summary>
	/// The map's name, author and description (§9.2): what is typed into its settings, and
	/// the author never anything else. Each at most what the file holds; a name not empty;
	/// no control character but a line break in the description (§6.8).
	/// </summary>
	public bool TrySetText(string name, string author, string description, out string error)
	{
		name = name?.Trim() ?? string.Empty;
		author = author?.Trim() ?? string.Empty;
		description = (description ?? string.Empty).Replace("\r\n", "\n").TrimEnd();
		error = name.Length == 0 ? "a map needs a name"
			: TextError("name", name, MapDocument.MaxNameBytes, false)
			?? TextError("author", author, MapDocument.MaxAuthorBytes, false)
			?? TextError("description", description, MapDocument.MaxDescriptionBytes, true);
		if (error != null)
		{
			return false;
		}

		if (name == Document.Name && author == Document.Author && description == Document.Description)
		{
			return true;
		}

		Document.Name = name;
		Document.Author = author;
		Document.Description = description;
		Touch();
		return true;
	}

	private static string TextError(string what, string text, int maxBytes, bool lineBreaks)
	{
		if (MapDocument.Utf8Bytes(text) > maxBytes)
		{
			return $"a map's {what} is at most {maxBytes} bytes";
		}

		foreach (char c in text)
		{
			if (char.IsControl(c) && !(lineBreaks && c == '\n'))
			{
				return $"a map's {what} may not hold a control character";
			}
		}

		return null;
	}

	/// <summary>The document is what was last saved, or what was opened.</summary>
	public void MarkSaved() => Dirty = false;

	/// <summary>The document is not what was saved, though nothing here changed it: an autosave taken back (§8.6).</summary>
	public void MarkUnsaved() => Touch();

	// ---- shared ---------------------------------------------------------------------------------

	/// <summary>
	/// Ends an edit of the ground over <paramref name="area"/>: every pad kept (§6.5), and
	/// the stored samples that differ from before it kept as one step.
	/// </summary>
	private EditOutcome Keep(SampleRect area)
	{
		SampleRect kept = PrefabPlacement.KeepPads(Document, area);
		area = area.Union(kept);
		SampleRect changed = Differences(_before, Document.Terrain, area);
		bool step = !changed.IsEmpty;
		if (step)
		{
			History.Push(MapEdit.Terrain(_before, Document.Terrain, changed));
			Touch();
		}

		_before = null;
		return new EditOutcome(area, changed, step, false);
	}

	/// <summary>The rectangle of the stored samples in <paramref name="area"/> that differ between two fields.</summary>
	public static SampleRect Differences(HeightField before, HeightField after, in SampleRect area)
	{
		SampleRect inside = area.Intersect(after.Bounds);
		SampleRect changed = SampleRect.Empty;
		for (int z = inside.MinZ; z <= inside.MaxZ; z++)
		{
			for (int x = inside.MinX; x <= inside.MaxX; x++)
			{
				if (HeightField.ToSample(before[x, z]) != HeightField.ToSample(after[x, z]))
				{
					changed = changed.With(x, z);
				}
			}
		}

		return changed;
	}

	private void Touch()
	{
		Dirty = true;
		Changes++;
	}
}
