using System.Collections.Generic;
using Gdpyr.Sim;
using Gdpyr.Sim.Level;
using Godot;

namespace Gdpyr.MapEditor;

/// <summary>
/// The bridge tool (docs/LEVEL_EDITOR.md §8.5, §6.7, LE6): pick a section, toggle supports, click.
/// Free connectors are drawn as rings, and the one a section would take is lit; near one, the
/// section's ghost takes the pose that joins it there, by its −Z end or, turned end for end with
/// <c>Tab</c>, by its +Z end. With none near it floats where the cursor is, at the height of the last
/// section placed, which <c>PgUp</c> and <c>PgDn</c> move a quarter metre, turned as <c>R</c> turns
/// it. A click on a placed section selects it; <c>Del</c> takes it up. Every one of these is a command
/// an engine check gives as the panel does.
/// </summary>
public partial class MapEditorRoot
{
	/// <summary>The section a click places: a span or a ramp.</summary>
	public ushort Section { get; private set; } = PrefabSpecs.BridgeSpan;

	/// <summary>Whether a section placed stands on piers (§6.7).</summary>
	public bool Supports { get; private set; } = true;

	/// <summary><c>Tab</c>: a section joins a connector by its +Z end, not its −Z end; a ramp goes down from it, not up.</summary>
	public bool EndForEnd { get; private set; }

	/// <summary>The deck's top a floating section is put at [m]: the last section placed's; null for the ground under the cursor.</summary>
	public float? FloatHeight { get; private set; }

	/// <summary>What placing the section where the cursor is would do; null while it is off the ground or the tool is another.</summary>
	public SectionCheck SectionCheck { get; private set; }

	private MeshInstance3D _bridgeLines;
	private ImmediateMesh _bridgeMesh;
	private Vector3 _sectionAt = new(float.NaN, 0f, 0f);
	private int _sectionChanges = -1;
	private string _sectionAsked;

	// ---- commands -------------------------------------------------------------------------------

	/// <summary>Picks the section a click places.</summary>
	public void SelectSection(ushort id)
	{
		if (PrefabSpecs.TryGet(id, out PrefabSpec spec) && spec.Kind == PrefabKind.Bridge)
		{
			Section = id;
			_sectionChanges = -1;
			Say($"{spec.Name}: click to place it; Tab turns it end for end, PgUp and PgDn raise and lower it, U its supports");
			RefreshBridgePanel();
		}
	}

	/// <summary><c>U</c>: supports on or off for the sections placed from now.</summary>
	public void SetSupports(bool on)
	{
		Supports = on;
		_sectionChanges = -1;
		Say(on ? "sections stand on piers" : "sections stand on nothing: free-floating, or on their joints");
		RefreshBridgePanel();
	}

	/// <summary><c>Tab</c>: a section joins by its other end.</summary>
	public void TurnEndForEnd()
	{
		EndForEnd = !EndForEnd;
		_sectionChanges = -1;
		Say(EndForEnd ? "joining by the section's +Z end" : "joining by the section's -Z end");
		RefreshBridgePanel();
	}

	/// <summary><c>PgUp</c> (+1) and <c>PgDn</c> (−1): a floating section a quarter metre up or down.</summary>
	public void NudgeSection(int direction)
	{
		float from = FloatHeight ?? (Cursor is { } at ? at.Y : 0f);
		FloatHeight = MapEditorControls.Nudge(from, direction);
		_sectionChanges = -1;
		Say($"a floating section's deck at {FloatHeight:0.00} m");
		RefreshBridgePanel();
	}

	/// <summary>Sets the height a floating section is put at [m], exactly: what <c>PgUp</c> and <c>PgDn</c> step.</summary>
	public void SetFloatHeight(float? height)
	{
		FloatHeight = height;
		_sectionChanges = -1;
		RefreshBridgePanel();
	}

	/// <summary>
	/// The request a click at (x, z) makes with the bridge tool (§6.7): on the nearest free connector
	/// within 3 m, or floating there at <see cref="FloatHeight"/> — or the ground's height there —
	/// turned to the ghost's yaw.
	/// </summary>
	public PlacementRequest SectionRequestAt(float x, float z, out Connector? target)
	{
		target = null;
		if (Editing == null)
		{
			return default;
		}

		float height = FloatHeight ?? Editing.Document.Terrain.Sample(x, z);
		return BridgeRules.Snap(Editing.Document, Section, EndForEnd, x, z, GhostYaw, height, Supports, -1, out target);
	}

	/// <summary>What placing the section at (x, z) would do, without doing it: the ghost's answer.</summary>
	public SectionCheck GhostSection(float x, float z)
	{
		PlacementRequest request = SectionRequestAt(x, z, out Connector? target);
		return Editing?.CheckSection(request, -1, target);
	}

	/// <summary>Places the section at (x, z), its piers and its landings, as one step, when §6.5 and §6.7 take it.</summary>
	public SectionCheck PlaceSectionAt(float x, float z)
	{
		if (Editing == null)
		{
			return null;
		}

		PlacementRequest request = SectionRequestAt(x, z, out Connector? target);
		SectionCheck check = Editing.PlaceSection(request, -1, out EditOutcome outcome, target);
		PrefabSpec spec = PrefabSpecs.Get(Section);
		if (check.IsOk)
		{
			Apply(outcome);
			MapObject placed = check.Placement.Object;
			FloatHeight = placed.Position.Y;
			Say($"placed {spec.Name} {Editing.Document.Objects.Count - 1}: {MapEditorControls.SectionGhost(spec, check, placed.Position.Y)}");
		}
		else
		{
			Say(MapEditorControls.SectionGhost(spec, check, request.Height));
		}

		RefreshBridgePanel();
		return check;
	}

	// ---- the click ------------------------------------------------------------------------------

	/// <summary>The left button with the bridge tool: a placed section selected, or the section placed. The right drops the selection.</summary>
	private void PressBridge(bool right)
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

		int picked = MapEditSession.Pick(Editing.Document, at.X, at.Z);
		if (picked >= 0 && PrefabSpecs.TryGet(Editing.Document.Objects[picked].PrefabId, out PrefabSpec spec)
			&& spec.Kind == PrefabKind.Bridge && SectionCheck?.Target == null)
		{
			Select(picked);
			Say($"selected {spec.Name} {picked}: Del takes it up");
			return;
		}

		Select(-1);
		PlaceSectionAt(at.X, at.Z);
	}

	// ---- the ghost and the rings, drawn ----------------------------------------------------------------

	private void BuildBridgeLines()
	{
		_bridgeMesh = new ImmediateMesh();
		_bridgeLines = new MeshInstance3D
		{
			Name = "Connectors",
			Mesh = _bridgeMesh,
			MaterialOverride = new StandardMaterial3D
			{
				ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
				VertexColorUseAsAlbedo = true,
				NoDepthTest = true,
			},
			CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
		};
		AddChild(_bridgeLines);
	}

	/// <summary>
	/// The section's ghost under the cursor — its deck, rails and piers — green, or red with §6.5's
	/// answer, asked again only when the cursor, the section, its settings or the map move; and the free
	/// connectors as rings, the one it would take lit.
	/// </summary>
	private void UpdateSectionGhost()
	{
		_bridgeMesh.ClearSurfaces();
		if (Tool != EditorTool.Bridges || Editing == null)
		{
			SectionCheck = null;
			return;
		}

		List<Connector> free = BridgeRules.Free(Editing.Document);
		if (Cursor is not { } at || Selected >= 0)
		{
			DrawConnectorRings(_bridgeMesh, free, null);
			_ghost.Visible = false;
			SectionCheck = null;
			return;
		}

		string asked = $"{Section} {Supports} {EndForEnd} {FloatHeight} {GhostYaw}";
		var where = new Vector3(at.X, 0f, at.Z);
		bool again = where != _sectionAt || _sectionChanges != Editing.Changes || asked != _sectionAsked || SectionCheck == null
			|| _ghostShown != ushort.MaxValue;
		if (again)
		{
			_sectionAt = where;
			_sectionChanges = Editing.Changes;
			_sectionAsked = asked;
			SectionCheck = GhostSection(at.X, at.Z);
		}

		DrawConnectorRings(_bridgeMesh, free, SectionCheck?.Target);
		MapObject placed = SectionCheck?.Placement?.Object;
		if (placed == null)
		{
			_ghost.Visible = false;
			return;
		}

		if (again)
		{
			// The ghost's boxes: the section's own and its piers', from what it would be stored as.
			Material look = SectionCheck.IsOk ? _ghostOk : _ghostRefused;
			foreach (Node child in _ghost.GetChildren())
			{
				_ghost.RemoveChild(child);
				child.QueueFree();
			}

			var pose = new StructurePose(placed.Position, placed.YawRadians);
			foreach (PlacedBox box in PrefabSpecs.BoxesOf(placed))
			{
				Vector3 centre = pose.ToLocal(new Vector3(box.Plan.Center.X, (box.Bottom + box.Top) * 0.5f, box.Plan.Center.Z));
				_ghost.AddChild(new MeshInstance3D
				{
					Position = centre,
					Mesh = new BoxMesh { Size = new Vector3(box.Plan.HalfWidth * 2f, box.Top - box.Bottom, box.Plan.HalfDepth * 2f) },
					MaterialOverride = look,
					CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
				});
			}
		}

		// The prefab tool draws its own boxes again when it is next used.
		_ghostShown = ushort.MaxValue;
		_ghost.Transform = new Transform3D(new Basis(Vector3.Up, placed.YawRadians), placed.Position);
		_ghost.Visible = true;
		_sectionLabel.Text = MapEditorControls.SectionGhost(PrefabSpecs.Get(Section), SectionCheck, placed.Position.Y);
		_sectionLabel.Modulate = SectionCheck.IsOk ? Colors.White : new Color(1f, 0.55f, 0.5f);
	}
}
