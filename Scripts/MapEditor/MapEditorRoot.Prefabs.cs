using System;
using System.Collections.Generic;
using Gdpyr.Core;
using Gdpyr.Sim;
using Gdpyr.Sim.Level;
using Godot;

namespace Gdpyr.MapEditor;

/// <summary>
/// The prefab tool (docs/LEVEL_EDITOR.md §8.4, LE5): a palette by kind with each entry's count
/// against its limit; the selected prefab as a ghost of its boxes on its pad under the cursor,
/// green or red with §6.5's answer and the grade under it; a click places it and its pad, as
/// one step; a click on a placed object selects it, to turn, take up, or give other
/// parameters. Every one of these is a command an engine check gives as the panel does.
/// </summary>
public partial class MapEditorRoot
{
	/// <summary>The prefab the palette has selected: what a click places.</summary>
	public ushort Prefab { get; private set; } = PrefabSpecs.SpawnLine;

	/// <summary>The yaw the ghost is placed at [radians].</summary>
	public float GhostYaw { get; private set; }

	/// <summary><c>G</c>: a placement's point on the nearest whole metre.</summary>
	public bool SnapToGrid { get; private set; }

	/// <summary>The object selected, by its index in the file; −1 for none.</summary>
	public int Selected { get; private set; } = -1;

	/// <summary>What placing the ghost where it is would do; null while it is off the ground or the tool is another.</summary>
	public PlacementCheck GhostCheck { get; private set; }

	private Node3D _ghost;
	private Node3D _selection;
	private StandardMaterial3D _ghostOk;
	private StandardMaterial3D _ghostRefused;
	private StandardMaterial3D _selectedLook;
	private ushort _ghostShown = ushort.MaxValue;
	private Vector3 _ghostAt = new(float.NaN, 0f, 0f);
	private int _ghostChanges = -1;

	// ---- commands -------------------------------------------------------------------------------

	/// <summary>Selects a prefab on the palette; any selection of a placed object is dropped.</summary>
	public void SelectPrefab(ushort id)
	{
		if (!PrefabSpecs.TryGet(id, out PrefabSpec spec))
		{
			return;
		}

		Prefab = id;
		Select(-1);
		_ghostChanges = -1;
		Say($"{spec.Name}: click to place it; R and Shift+R turn it, G snaps it to the metre grid");
		RefreshPrefabPanel();
	}

	/// <summary>The request a click at (x, z) makes: the palette's prefab, on the grid if it snaps, at the ghost's yaw.</summary>
	public PlacementRequest RequestAt(float x, float z) =>
		new(Prefab, SnapToGrid ? MapEditorControls.Snap(x) : x, SnapToGrid ? MapEditorControls.Snap(z) : z, GhostYaw);

	/// <summary>What placing the palette's prefab at (x, z) would do, without doing it: the ghost's answer.</summary>
	public PlacementCheck Ghost(float x, float z) => Editing?.Check(RequestAt(x, z));

	/// <summary>Places the palette's prefab at (x, z) and its pad, as one step, when §6.5 takes it.</summary>
	public PlacementCheck PlaceAt(float x, float z) => Place(RequestAt(x, z));

	/// <summary>Places what is asked, as one step, when §6.5 takes it. Says why not when it does not.</summary>
	public PlacementCheck Place(in PlacementRequest request)
	{
		if (Editing == null)
		{
			return null;
		}

		PlacementCheck check = Editing.Place(request, out EditOutcome outcome);
		string name = PrefabSpecs.TryGet(request.PrefabId, out PrefabSpec spec) ? spec.Name : $"prefab {request.PrefabId}";
		if (check.IsOk)
		{
			Apply(outcome);
			Say($"placed {name} {Editing.Document.Objects.Count - 1} at ({check.Object.Position.X:0.0}, {check.Object.Position.Z:0.0}),"
				+ $" {check.Level:0.00} m, {check.GradeDegrees:0.0}° under it before its pad");
		}
		else
		{
			Say(spec != null ? MapEditorControls.Ghost(spec, check) : $"{name}: {PrefabPlacement.Describe(check.Result)}");
		}

		return check;
	}

	/// <summary>Selects the object at an index in the file, or none with −1.</summary>
	public void Select(int index)
	{
		Selected = Editing != null && index >= 0 && index < Editing.Document.Objects.Count ? index : -1;
		DrawSelection();
		RefreshPrefabPanel();
	}

	/// <summary>Selects what is under (x, z), as a click on it does. Returns its index, or −1.</summary>
	public int SelectAt(float x, float z)
	{
		int index = Editing == null ? -1 : MapEditSession.Pick(Editing.Document, x, z);
		Select(index);
		if (index >= 0)
		{
			MapObject placed = Editing.Document.Objects[index];
			Say($"selected {PrefabSpecs.Get(placed.PrefabId).Name} {index}: R and Shift+R turn it, Del takes it up");
		}

		return index;
	}

	/// <summary>
	/// Turns the selected object to the next 15° step either way: taken up and placed again where it
	/// is in the file, as one step, or refused and left as it was.
	/// </summary>
	public PlacementCheck TurnSelected(int direction)
	{
		if (Editing == null || Selected < 0)
		{
			return null;
		}

		PlacementRequest request = MapEditSession.RequestFor(Editing.Document.Objects[Selected]);
		request.Yaw = MapEditorControls.Turn(request.Yaw, direction);
		return ReplaceSelected(request, "turned");
	}

	/// <summary>Gives the selected object a parameter's value (§6.5: a node's perk and capture radius), placing it again.</summary>
	public PlacementCheck SetSelectedParameter(byte key, int value)
	{
		if (Editing == null || Selected < 0)
		{
			return null;
		}

		MapObject placed = Editing.Document.Objects[Selected];
		var values = new List<MapParameter>();
		foreach (MapParameter parameter in placed.Parameters)
		{
			if (parameter.Key != key)
			{
				values.Add(parameter);
			}
		}

		values.Add(new MapParameter(key, value));
		values.Sort((a, b) => a.Key.CompareTo(b.Key));
		PlacementRequest request = MapEditSession.RequestFor(placed);
		request.Parameters = values.ToArray();
		return ReplaceSelected(request, $"given {key} = {value}");
	}

	private PlacementCheck ReplaceSelected(in PlacementRequest request, string what)
	{
		int index = Selected;
		PrefabSpec spec = PrefabSpecs.Get(Editing.Document.Objects[index].PrefabId);
		PlacementCheck check = Editing.Replace(index, request, out EditOutcome outcome);
		if (check.IsOk)
		{
			Apply(outcome);
			Select(index);
			Say($"{spec.Name} {index} {what}");
		}
		else
		{
			Say($"{spec.Name} {index} not {what}: {MapEditorControls.Ghost(spec, check)}");
		}

		return check;
	}

	/// <summary><c>Del</c>: takes the selected object up, as one step; the ground its pad levelled stays.</summary>
	public bool TakeUpSelected()
	{
		if (Editing == null || Selected < 0)
		{
			return false;
		}

		int index = Selected;
		string name = PrefabSpecs.Get(Editing.Document.Objects[index].PrefabId).Name;
		if (!Editing.TakeUp(index, out EditOutcome outcome))
		{
			return false;
		}

		Select(-1);
		Apply(outcome);
		Say($"took {name} {index} up; its pad's ground stays");
		return true;
	}

	/// <summary><c>R</c> and <c>Shift+R</c>: the selected object, or else the ghost, turned to the next 15° step.</summary>
	public void Turn(int direction)
	{
		if (Selected >= 0)
		{
			TurnSelected(direction);
			return;
		}

		GhostYaw = MapEditorControls.Turn(GhostYaw, direction);
		_ghostChanges = -1;
		Say($"the ghost turned to {Mathf.RadToDeg(GhostYaw):0}°");
	}

	/// <summary>Sets the yaw the ghost is placed at [radians]: what <c>R</c> steps, said exactly.</summary>
	public void SetGhostYaw(float yaw)
	{
		GhostYaw = yaw;
		_ghostChanges = -1;
	}

	/// <summary><c>G</c>: snapping to the metre grid, on or off.</summary>
	public void ToggleSnap()
	{
		SnapToGrid = !SnapToGrid;
		_ghostChanges = -1;
		Say(SnapToGrid ? "placing on the metre grid" : "placing where the cursor is");
		RefreshPrefabPanel();
	}

	// ---- the click ------------------------------------------------------------------------------

	/// <summary>The left button over the view with the prefab tool: a placed object selected, or the ghost placed.</summary>
	private void PressPrefab(bool right)
	{
		if (right)
		{
			Select(-1);
			return;
		}

		if (Cursor is not { } at)
		{
			return;
		}

		if (SelectAt(at.X, at.Z) < 0)
		{
			PlaceAt(at.X, at.Z);
		}
	}

	// ---- the ghost and the selection, drawn ------------------------------------------------------

	private void BuildGhost()
	{
		_ghostOk = Translucent(new Color(0.25f, 0.85f, 0.3f, 0.35f));
		_ghostRefused = Translucent(new Color(0.9f, 0.2f, 0.15f, 0.35f));
		_selectedLook = Translucent(new Color(0.35f, 0.65f, 1f, 0.3f));
		_ghost = new Node3D { Name = "Ghost", Visible = false };
		AddChild(_ghost);
		_selection = new Node3D { Name = "Selection", Visible = false };
		AddChild(_selection);
	}

	private static StandardMaterial3D Translucent(Color colour) => new()
	{
		AlbedoColor = colour,
		ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
		Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
		CullMode = BaseMaterial3D.CullModeEnum.Disabled,
	};

	/// <summary>A prefab's boxes as meshes under <paramref name="parent"/>, in its own frame: what the ghost and the selection draw.</summary>
	private static void DrawBoxes(Node3D parent, PrefabSpec spec, Material look)
	{
		foreach (Node child in parent.GetChildren())
		{
			parent.RemoveChild(child);
			child.QueueFree();
		}

		foreach (StructureBox box in spec.Boxes)
		{
			parent.AddChild(new MeshInstance3D
			{
				Position = box.Center,
				Mesh = new BoxMesh { Size = box.HalfExtents * 2f },
				MaterialOverride = look,
				CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
			});
		}

		if (spec.Boxes.Count == 0)
		{
			// A marker with no body — the anchor — is a post, to see where it is.
			parent.AddChild(new MeshInstance3D
			{
				Position = new Vector3(0f, 1.5f, 0f),
				Mesh = new BoxMesh { Size = new Vector3(0.4f, 3f, 0.4f) },
				MaterialOverride = look,
			});
		}
	}

	/// <summary>
	/// The ghost under the cursor, standing where it would on its pad, and §6.5's answer for it,
	/// asked again only when the cursor, the yaw, the prefab or the map has moved.
	/// </summary>
	private void UpdateGhost()
	{
		if (Tool != EditorTool.Prefabs || Cursor is not { } at || Editing == null || Selected >= 0)
		{
			_ghost.Visible = false;
			GhostCheck = null;
			return;
		}

		PlacementRequest request = RequestAt(at.X, at.Z);
		var where = new Vector3(request.X, 0f, request.Z);
		if (where != _ghostAt || _ghostChanges != Editing.Changes || GhostCheck == null)
		{
			_ghostAt = where;
			_ghostChanges = Editing.Changes;
			GhostCheck = Editing.Check(request);
		}

		PrefabSpec spec = PrefabSpecs.Get(Prefab);
		if (_ghostShown != Prefab)
		{
			DrawBoxes(_ghost, spec, _ghostOk);
			_ghostShown = Prefab;
		}

		Material look = GhostCheck.IsOk ? _ghostOk : _ghostRefused;
		foreach (Node child in _ghost.GetChildren())
		{
			((MeshInstance3D)child).MaterialOverride = look;
		}

		_ghost.Transform = new Transform3D(new Basis(Vector3.Up, GhostYaw), new Vector3(request.X, GhostCheck.Level, request.Z));
		_ghost.Visible = true;
		_ghostLabel.Text = MapEditorControls.Ghost(spec, GhostCheck);
		_ghostLabel.Modulate = GhostCheck.IsOk ? Colors.White : new Color(1f, 0.55f, 0.5f);
	}

	private void DrawSelection()
	{
		if (_selection == null)
		{
			return;
		}

		if (Editing == null || Selected < 0)
		{
			_selection.Visible = false;
			return;
		}

		MapObject placed = Editing.Document.Objects[Selected];
		PrefabSpec spec = PrefabSpecs.Get(placed.PrefabId);
		DrawBoxes(_selection, spec, _selectedLook);
		_selection.Transform = PrefabCatalog.PoseOf(placed, spec);
		_selection.Visible = true;
	}

	/// <summary>
	/// The map's structures as their boxes, which a round puts up itself (§6.5): drawn and solid in
	/// the editor, so that one is seen where it stands and the deep check's bake goes round it.
	/// </summary>
	private void BuildStructureStandIns()
	{
		var look = Draws ? new StandardMaterial3D { AlbedoColor = new Color(0.55f, 0.55f, 0.52f) } : null;
		for (int i = 0; i < Editing.Document.Objects.Count; i++)
		{
			MapObject placed = Editing.Document.Objects[i];
			if (!PrefabSpecs.TryGetStructureKind(placed.PrefabId, out _) || !PrefabSpecs.TryGet(placed.PrefabId, out PrefabSpec spec))
			{
				continue;
			}

			var body = new StaticBody3D
			{
				Name = $"Structure{i}",
				Transform = PrefabCatalog.PoseOf(placed, spec),
				CollisionLayer = CollisionLayers.World,
				CollisionMask = 0,
			};
			foreach (StructureBox box in spec.Boxes)
			{
				PrefabCatalog.AddBox(body, new Transform3D(Basis.Identity, box.Center), box.HalfExtents * 2f, look);
			}

			_built.AddChild(body);
		}
	}
}
