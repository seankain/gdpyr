using System;
using System.Collections.Generic;
using Gdpyr.Sim.Level;
using Godot;

namespace Gdpyr.MapEditor;

/// <summary>
/// The road tool (docs/LEVEL_EDITOR.md §8.5, LE6): a road is clicks, each adding a point; the
/// ribbon and its profile are previewed with the deepest cut and fill, <c>Enter</c> grades it and
/// <c>Esc</c> drops it. A road's end near a free connector joins it: the point goes on the connector,
/// and a point the road's width out along its heading with it, so that the road meets a deck square.
/// A click on a placed road takes its points up to move: a click near one picks it up and the next
/// puts it down, and <c>Enter</c> grades the new line, leaving the old line's ground as it is.
/// Every one of these is a command an engine check gives as the panel does.
/// </summary>
public partial class MapEditorRoot
{
	/// <summary>The points of the road being drawn, or of the placed road being moved [m] (x, z).</summary>
	public IReadOnlyList<Vector2> RoadDraft => _roadDraft;

	/// <summary>The placed road whose points are being moved, by its index; −1 while a new one is drawn.</summary>
	public int RoadEditing { get; private set; } = -1;

	/// <summary>The point of the draft picked up to be put down again, or −1.</summary>
	public int RoadPointHeld { get; private set; } = -1;

	/// <summary>The width a road is drawn at [m].</summary>
	public float RoadWidth { get; private set; } = MapEditorControls.DefaultRoadWidthMeters;

	/// <summary>The grade limit a road is drawn at, rise over run.</summary>
	public float RoadGrade { get; private set; } = MapEditorControls.DefaultRoadGrade;

	/// <summary>§6.6's answer for the draft as it stands, with its line and profile: the preview's. Null under two points.</summary>
	public RoadCheck RoadPreview { get; private set; }

	private readonly List<Vector2> _roadDraft = new();
	private MeshInstance3D _roadLines;
	private ImmediateMesh _roadMesh;
	private int _roadPreviewAt = -1;

	// ---- commands -------------------------------------------------------------------------------

	/// <summary>
	/// Adds a point to the road being drawn (§8.5). Within <see cref="BridgeRules.SnapMeters"/> of a
	/// free connector it goes on the connector, with a point the road's width out from it along the
	/// connector's heading: after it when the road starts there, before it when it ends there.
	/// Returns whether a point was added: a road has at most 64.
	/// </summary>
	public bool AddRoadPoint(float x, float z)
	{
		if (Editing == null || _roadDraft.Count >= MapLimits.MaxRoadPoints || !float.IsFinite(x) || !float.IsFinite(z))
		{
			return false;
		}

		var at = new Vector2(Math.Clamp(x, -MapLimits.FieldHalfMeters, MapLimits.FieldHalfMeters),
			Math.Clamp(z, -MapLimits.FieldHalfMeters, MapLimits.FieldHalfMeters));
		List<Connector> free = BridgeRules.Free(Editing.Document, -1, RoadEditing);
		if (BridgeRules.Nearest(free, at.X, at.Y, BridgeRules.SnapMeters, out Connector connector))
		{
			var point = new Vector2(connector.Point.X, connector.Point.Z);
			Vector2 lead = point + (connector.Direction * RoadWidth);
			if (_roadDraft.Count == 0)
			{
				_roadDraft.Add(point);
				_roadDraft.Add(lead);
			}
			else
			{
				if ((_roadDraft[^1] - lead).Length() > MapEditorControls.RoadPointPickMeters && _roadDraft.Count < MapLimits.MaxRoadPoints - 1)
				{
					_roadDraft.Add(lead);
				}

				_roadDraft.Add(point);
			}

			Say($"road: joined to {connector}");
		}
		else
		{
			_roadDraft.Add(at);
			Say($"road: point {_roadDraft.Count} at ({at.X:0.0}, {at.Y:0.0})");
		}

		RoadChanged();
		return true;
	}

	/// <summary><c>Enter</c>: grades the road drawn, or the placed road's new line, as one step, when §6.6 takes it.</summary>
	public RoadCheck ApplyRoad()
	{
		if (Editing == null || _roadDraft.Count < MapLimits.MinRoadPoints)
		{
			Say("a road needs two points");
			return null;
		}

		MapRoad road = RoadPath.Record(_roadDraft.ToArray(), RoadWidth, RoadGrade);
		RoadCheck check = Editing.ApplyRoad(road, RoadEditing, out EditOutcome outcome);
		if (!check.IsOk)
		{
			Say($"road not graded: {MapEditorControls.RoadPreview(check, _roadDraft.Count)}");
			return check;
		}

		int index = RoadEditing >= 0 ? RoadEditing : Editing.Document.Roads.Count - 1;
		Apply(outcome);
		Say($"road {index} graded: {check.Path.Length:0.0} m at {RoadGrade * 100f:0.#} %, deepest cut {check.Path.DeepestCut:0.00} m,"
			+ $" fill {check.Path.DeepestFill:0.00} m; {outcome}");
		DropRoad();
		return check;
	}

	/// <summary><c>Esc</c>: drops the road being drawn, or the move of a placed road's points, changing nothing.</summary>
	public void DropRoad()
	{
		bool any = _roadDraft.Count > 0;
		_roadDraft.Clear();
		RoadEditing = -1;
		RoadPointHeld = -1;
		RoadChanged();
		if (any)
		{
			RefreshRoadPanel();
		}
	}

	/// <summary>Takes a placed road's points up to move (§8.5): the draft is its line, at its width and grade.</summary>
	public bool SelectRoad(int index)
	{
		if (Editing == null || index < 0 || index >= Editing.Document.Roads.Count)
		{
			return false;
		}

		MapRoad road = Editing.Document.Roads[index];
		_roadDraft.Clear();
		foreach (RoadPoint point in road.Points)
		{
			_roadDraft.Add(new Vector2(point.XMeters, point.ZMeters));
		}

		RoadEditing = index;
		RoadPointHeld = -1;
		RoadWidth = road.WidthDecimeters / 10f;
		RoadGrade = MapEditorControls.ClampRoadGrade(road.GradeLimitPermille / 1000f);
		RoadChanged();
		Say($"road {index}: click a point to pick it up and again to put it down; Enter grades the new line, Del takes the road up");
		return true;
	}

	/// <summary>Moves a point of the draft to (x, z), which is what picking it up and putting it down does.</summary>
	public bool MoveRoadPoint(int point, float x, float z)
	{
		if (Editing == null || point < 0 || point >= _roadDraft.Count || !float.IsFinite(x) || !float.IsFinite(z))
		{
			return false;
		}

		_roadDraft[point] = new Vector2(Math.Clamp(x, -MapLimits.FieldHalfMeters, MapLimits.FieldHalfMeters),
			Math.Clamp(z, -MapLimits.FieldHalfMeters, MapLimits.FieldHalfMeters));
		RoadPointHeld = -1;
		RoadChanged();
		return true;
	}

	/// <summary><c>Del</c> with the road tool: takes the placed road being moved up, as one step; the ground it graded stays.</summary>
	public bool TakeUpRoad(int index)
	{
		if (Editing == null || !Editing.TakeUpRoad(index, out EditOutcome outcome))
		{
			return false;
		}

		DropRoad();
		Apply(outcome);
		Say($"took road {index} up; the ground it graded stays");
		return true;
	}

	/// <summary>The width roads are drawn at [m]: 4, 6 or 8.</summary>
	public void SetRoadWidth(float width)
	{
		if (Array.IndexOf(MapEditorControls.RoadWidths, width) >= 0)
		{
			RoadWidth = width;
			RoadChanged();
		}
	}

	/// <summary>The grade limit roads are drawn at, rise over run: 6–15 %.</summary>
	public void SetRoadGrade(float grade)
	{
		RoadGrade = MapEditorControls.ClampRoadGrade(grade);
		RoadChanged();
	}

	// ---- the click ------------------------------------------------------------------------------

	/// <summary>
	/// A button over the view with the road tool: the right drops the draft; the left puts a held
	/// point down, picks a point of the draft up, takes a placed road's points up to move, or adds a
	/// point.
	/// </summary>
	private void PressRoad(bool right)
	{
		if (right)
		{
			DropRoad();
			Say("road dropped");
			return;
		}

		if (Cursor is not { } at)
		{
			return;
		}

		if (RoadPointHeld >= 0)
		{
			MoveRoadPoint(RoadPointHeld, at.X, at.Z);
			Say("point put down: Enter grades the new line");
			return;
		}

		if (RoadEditing >= 0)
		{
			int nearest = NearestDraftPoint(at.X, at.Z);
			if (nearest >= 0)
			{
				RoadPointHeld = nearest;
				Say($"point {nearest} picked up: click where it goes");
				RoadChanged();
			}

			return;
		}

		if (_roadDraft.Count == 0 && MapEditSession.PickRoad(Editing.Document, at.X, at.Z) is int road && road >= 0)
		{
			SelectRoad(road);
			return;
		}

		AddRoadPoint(at.X, at.Z);
	}

	private int NearestDraftPoint(float x, float z)
	{
		int best = -1;
		float nearest = MapEditorControls.RoadPointPickMeters;
		for (int i = 0; i < _roadDraft.Count; i++)
		{
			float distance = (_roadDraft[i] - new Vector2(x, z)).Length();
			if (distance <= nearest)
			{
				nearest = distance;
				best = i;
			}
		}

		return best;
	}

	// ---- the preview -----------------------------------------------------------------------------

	/// <summary>The draft changed: §6.6 asked of it again, and the preview and the panel drawn again.</summary>
	private void RoadChanged()
	{
		RoadPreview = Editing != null && _roadDraft.Count >= MapLimits.MinRoadPoints
			? Editing.CheckRoad(RoadPath.Record(_roadDraft.ToArray(), RoadWidth, RoadGrade), RoadEditing)
			: null;
		_roadPreviewAt = Editing?.Changes ?? -1;
		RefreshRoadPanel();
	}

	private void BuildRoadLines()
	{
		_roadMesh = new ImmediateMesh();
		_roadLines = new MeshInstance3D
		{
			Name = "RoadPreview",
			Mesh = _roadMesh,
			MaterialOverride = new StandardMaterial3D
			{
				ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
				VertexColorUseAsAlbedo = true,
				NoDepthTest = true,
			},
			CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
		};
		AddChild(_roadLines);
	}

	/// <summary>
	/// The preview (§8.5): the points, the centreline at the profile's height and the ribbon's edges —
	/// green when §6.6 takes it, red when not — a line from the last point to the cursor, and the free
	/// connectors as rings, the one a click would join lit.
	/// </summary>
	private void DrawRoadPreview()
	{
		_roadMesh.ClearSurfaces();
		if (Editing == null || Tool != EditorTool.Roads)
		{
			return;
		}

		if (_roadPreviewAt != Editing.Changes)
		{
			RoadChanged();
		}

		HeightField field = Editing.Document.Terrain;
		List<Connector> free = BridgeRules.Free(Editing.Document, -1, RoadEditing);
		Connector? lit = Cursor is { } c && BridgeRules.Nearest(free, c.X, c.Z, BridgeRules.SnapMeters, out Connector near) ? near : null;
		DrawConnectorRings(_roadMesh, free, lit);

		var points = new List<Vector3>();
		foreach (Vector2 p in _roadDraft)
		{
			points.Add(new Vector3(p.X, field.Sample(p.X, p.Y) + RingLift, p.Y));
		}

		if (points.Count > 0)
		{
			_roadMesh.SurfaceBegin(Mesh.PrimitiveType.Lines);
			for (int i = 0; i < points.Count; i++)
			{
				Color colour = i == RoadPointHeld ? Colors.Yellow : Colors.White;
				Cross(_roadMesh, points[i], 0.8f, colour);
			}

			if (Cursor is { } cursor && RoadEditing < 0 && RoadPointHeld < 0)
			{
				_roadMesh.SurfaceSetColor(new Color(1f, 1f, 1f, 0.6f));
				_roadMesh.SurfaceAddVertex(points[^1]);
				_roadMesh.SurfaceSetColor(new Color(1f, 1f, 1f, 0.6f));
				_roadMesh.SurfaceAddVertex(cursor + (Vector3.Up * RingLift));
			}

			_roadMesh.SurfaceEnd();
		}

		RoadPath path = RoadPreview?.Path;
		if (path == null || path.Shape != RoadResult.Ok || path.Profile.Length != path.Count)
		{
			return;
		}

		Color line = RoadPreview.IsOk ? new Color(0.3f, 0.95f, 0.35f) : new Color(0.95f, 0.25f, 0.2f);
		for (int side = -1; side <= 1; side++)
		{
			_roadMesh.SurfaceBegin(Mesh.PrimitiveType.LineStrip);
			for (int i = 0; i < path.Count; i++)
			{
				Vector2 normal = new Vector2(-path.Tangents[i].Y, path.Tangents[i].X) * (side * path.HalfWidth);
				Vector2 at = path.Points[i] + normal;
				_roadMesh.SurfaceSetColor(side == 0 ? line : line * 0.8f);
				_roadMesh.SurfaceAddVertex(new Vector3(at.X, path.Profile[i] + RingLift, at.Y));
			}

			_roadMesh.SurfaceEnd();
		}
	}

	/// <summary>Free connectors as rings on the deck or the road, the one a click would take lit.</summary>
	private static void DrawConnectorRings(ImmediateMesh mesh, List<Connector> free, Connector? lit)
	{
		const int Segments = 24;
		foreach (Connector connector in free)
		{
			bool on = lit is { } l && l.Point == connector.Point;
			float radius = on ? 1.2f : 0.8f;
			Color colour = on ? Colors.Yellow : new Color(0.5f, 0.8f, 1f);
			mesh.SurfaceBegin(Mesh.PrimitiveType.LineStrip);
			for (int i = 0; i <= Segments; i++)
			{
				float angle = i * Mathf.Tau / Segments;
				mesh.SurfaceSetColor(colour);
				mesh.SurfaceAddVertex(connector.Point + new Vector3(radius * Mathf.Cos(angle), RingLift, radius * Mathf.Sin(angle)));
			}

			mesh.SurfaceEnd();
		}
	}

	private static void Cross(ImmediateMesh mesh, Vector3 at, float size, Color colour)
	{
		foreach (Vector3 axis in new[] { Vector3.Right, Vector3.Back })
		{
			mesh.SurfaceSetColor(colour);
			mesh.SurfaceAddVertex(at - (axis * size));
			mesh.SurfaceSetColor(colour);
			mesh.SurfaceAddVertex(at + (axis * size));
		}
	}
}
