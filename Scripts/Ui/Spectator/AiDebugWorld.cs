using System.Collections.Generic;
using Gdpyr.Sim;
using Gdpyr.Sim.AiDebug;
using Gdpyr.Sim.Htn;
using Godot;

namespace Gdpyr.Ui.Spectator;

/// <summary>
/// The debugger drawn into the world (docs/AI_DEBUG.md §4.3): over a selected agent,
/// a ring, its running task, and a line to every place its plan knows about — where
/// it is going, its node, its buddy, its fall-back point, what it is shooting at;
/// over every planning agent when asked, its task and where it is heading; and over a
/// computer strategist's squads, each squad's middle, members, order and staging point.
///
/// Everything is drawn through walls: the point of the overlay is to see the plan,
/// and a plan that disappears behind a hedge has not stopped being one. Colours are
/// the five tasks' (<see cref="AiGoals.Color"/>), so a retreat reads as a retreat on
/// every layer; a selection's own colour marks what belongs to it.
/// </summary>
public partial class AiDebugWorld : Node3D
{
	/// <summary>One colour per selection slot: rings, and the lines to its places.</summary>
	public static readonly Color[] SelectionColors =
	{
		new(0.25f, 0.95f, 1f),
		new(1f, 0.4f, 0.95f),
		new(1f, 0.95f, 0.3f),
		new(0.55f, 1f, 0.45f),
	};

	private static readonly Color TargetColor = new(1f, 0.25f, 0.25f);
	private static readonly Color FocusColor = new(1f, 0.7f, 0.2f);

	private readonly List<Vector3> _points = new();
	private readonly List<Color> _colors = new();
	private readonly List<Label3D> _labels = new();
	private int _labelsUsed;

	private ImmediateMesh _mesh;
	private StandardMaterial3D _material;

	public override void _Ready()
	{
		_mesh = new ImmediateMesh();
		_material = new StandardMaterial3D
		{
			ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
			VertexColorUseAsAlbedo = true,
			NoDepthTest = true,
			Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
			RenderPriority = 10,
		};

		AddChild(new MeshInstance3D
		{
			Name = "Lines",
			Mesh = _mesh,
			CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
		});
	}

	/// <summary>Redraws everything from the newest frame. Once a render frame, so characters and units are where they are drawn.</summary>
	public void Render(AiDebugFrame frame, SpectatorView view)
	{
		_points.Clear();
		_colors.Clear();
		_labelsUsed = 0;

		if (frame != null && view.WorldMarkers)
		{
			if (view.Strategist)
			{
				DrawCommanders(frame, view);
			}

			if (view.LabelsAll)
			{
				DrawLabels(frame, view);
			}

			for (int i = 0; i < view.Selected.Count; i++)
			{
				DrawSelected(frame, view.Selected[i], SelectionColors[i % SelectionColors.Length]);
			}
		}

		Flush();
	}

	// ---- what is drawn -----------------------------------------------------

	private void DrawSelected(AiDebugFrame frame, AiEntityRef target, Color own)
	{
		if (!AiEntities.TryPosition(target, frame, out Vector3 at))
		{
			return;
		}

		Ring(at + (Vector3.Up * 0.05f), target.Kind == AiEntityKind.Squad ? 3f : 1.1f, own);
		Line(at, at + (Vector3.Up * 2.6f), own);

		AiInspect inspect = frame.Find(target);
		HtnDomainKind domain = AiGoals.DomainOf(target.Kind);
		AiTaskFamily family = inspect == null ? AiTaskFamily.None : AiGoals.Family(domain, inspect.Goal);
		Color task = Color.FromHtml(AiGoals.Color(family));

		string goal = inspect == null ? "…"
			: !inspect.Planning ? "no plan"
			: $"{AiGoals.Name(domain, inspect.Goal)} · {AiGoals.Describe(family)}";
		Label(at + (Vector3.Up * 3.1f), $"{AiEntities.NameOf(target)}\n{goal}", inspect == null ? own : task, 30);

		if (inspect == null)
		{
			return;
		}

		Vector3 chest = at + (Vector3.Up * 1.2f);
		foreach (AiMarker marker in inspect.Markers)
		{
			bool intent = marker.Key == AiMarkerKey.Intent;
			Color color = intent ? task : own.Darkened(0.25f);
			Line(chest, marker.Point + (Vector3.Up * 0.3f), color);
			Diamond(marker.Point + (Vector3.Up * 0.3f), intent ? 0.6f : 0.4f, color);
			Label(marker.Point + (Vector3.Up * 1.1f), MarkerName(marker.Key), color, intent ? 24 : 20);
		}

		if (inspect.TryValue(AiValueKey.Target, out int targetOwner)
			&& AiEntities.TryOwnerPosition(targetOwner, out Vector3 shot))
		{
			Line(chest, shot + (Vector3.Up * 1.2f), TargetColor);
			Label(shot + (Vector3.Up * 2.2f), "target", TargetColor, 20);
		}

		if (inspect.TryValue(AiValueKey.Focus, out int focusOwner) && focusOwner != targetOwner
			&& AiEntities.TryOwnerPosition(focusOwner, out Vector3 focus))
		{
			Line(chest, focus + (Vector3.Up * 1.4f), FocusColor);
			Label(focus + (Vector3.Up * 2.6f), "focus", FocusColor, 20);
		}

		if (target.Kind == AiEntityKind.Squad && frame.Commander(target.Id)?.Squad(target.Slot) is { } squad)
		{
			DrawMembers(squad, own.Darkened(0.35f));
		}
	}

	private void DrawLabels(AiDebugFrame frame, SpectatorView view)
	{
		foreach (AiLabel label in frame.Labels)
		{
			if (view.IsSelected(label.Ref) || !AiEntities.TryPosition(label.Ref, frame, out Vector3 at))
			{
				continue;
			}

			HtnDomainKind domain = AiGoals.DomainOf(label.Ref.Kind);
			Color color = Color.FromHtml(AiGoals.Color(AiGoals.Family(domain, label.Goal)));

			string text = AiGoals.Name(domain, label.Goal);
			if ((label.Flags & AiLabelFlags.Waiting) != 0)
			{
				text += " (waiting)";
			}
			else if ((label.Flags & AiLabelFlags.Sweeping) != 0)
			{
				text += " (sweep)";
			}

			if (label.Ref.Kind == AiEntityKind.Player && label.Detail != 0)
			{
				text += $" · {AiGoals.Detail(label.Ref.Kind, label.Detail)}";
			}

			Label(at + (Vector3.Up * 2.4f), text, color, 20);

			if ((label.Flags & AiLabelFlags.HasPoint) != 0)
			{
				Line(at + (Vector3.Up * 0.5f), label.Point + (Vector3.Up * 0.2f), new Color(color, 0.55f));
			}
		}
	}

	private void DrawCommanders(AiDebugFrame frame, SpectatorView view)
	{
		foreach (AiCommander commander in frame.Commanders)
		{
			foreach (AiSquad squad in commander.Squads)
			{
				AiTaskFamily family = AiGoals.Family(HtnDomainKind.Squad, squad.Goal);
				Color color = Color.FromHtml(AiGoals.Color(family));
				bool chosen = view.IsSelected(AiEntityRef.Squad(commander.PeerId, squad.Slot));

				Ring(squad.Centroid + (Vector3.Up * 0.1f), 2f + (squad.Members.Count * 0.35f), color);
				DrawMembers(squad, new Color(color, 0.45f));

				if (!chosen)
				{
					string role = SquadText.RoleLetter(squad);
					Label(squad.Centroid + (Vector3.Up * 3.6f),
						$"{role}{squad.Slot} {AiGoals.Name(HtnDomainKind.Squad, squad.Goal)}"
						+ $"\n{squad.Members.Count}u {SquadText.Strength(squad)}", color, 24);
				}

				if (squad.Goal != 0)
				{
					Line(squad.Centroid + Vector3.Up, squad.OrderPoint + (Vector3.Up * 0.3f), color);
					Diamond(squad.OrderPoint + (Vector3.Up * 0.3f), 0.8f, color);
				}

				if (squad.Phase == (byte)SquadPhase.Gathering)
				{
					Ring(squad.StagingPoint + (Vector3.Up * 0.1f), 12f, new Color(color, 0.6f));
					Label(squad.StagingPoint + (Vector3.Up * 1.5f), $"staging {SquadText.RoleLetter(squad)}{squad.Slot}", color, 20);
				}

				if ((squad.Flags & AiSquadFlags.FallingBack) != 0)
				{
					Line(squad.Centroid + Vector3.Up, squad.FallbackPoint + Vector3.Up, TargetColor);
					Label(squad.FallbackPoint + (Vector3.Up * 1.8f), "falling back here", TargetColor, 20);
				}
			}
		}
	}

	private void DrawMembers(AiSquad squad, Color color)
	{
		foreach (ushort member in squad.Members)
		{
			if (AiEntities.TryPosition(AiEntityRef.Unit(member), null, out Vector3 at))
			{
				Line(at + (Vector3.Up * 0.3f), squad.Centroid + (Vector3.Up * 0.3f), color);
			}
		}
	}

	public static string MarkerName(AiMarkerKey key) => key switch
	{
		AiMarkerKey.Intent => "going here",
		AiMarkerKey.Exit => "ring exit",
		AiMarkerKey.Fallback => "fall back",
		AiMarkerKey.Locker => "locker",
		AiMarkerKey.Ghost => "ghost",
		AiMarkerKey.Armour => "armour",
		AiMarkerKey.Zone => "denied node",
		AiMarkerKey.Sweep => "sweep",
		AiMarkerKey.Buddy => "buddy",
		AiMarkerKey.Order => "order",
		AiMarkerKey.Anchor => "anchor",
		AiMarkerKey.NamedTarget => "named target",
		AiMarkerKey.Squad => "squad middle",
		AiMarkerKey.Support => "friend's fight",
		AiMarkerKey.Post => "post",
		AiMarkerKey.Staging => "staging",
		AiMarkerKey.Friend => "cover",
		AiMarkerKey.Supply => "supply",
		_ => key.ToString(),
	};

	// ---- primitives --------------------------------------------------------

	private void Line(Vector3 a, Vector3 b, Color color)
	{
		_points.Add(a);
		_colors.Add(color);
		_points.Add(b);
		_colors.Add(color);
	}

	private void Ring(Vector3 centre, float radius, Color color, int segments = 28)
	{
		Vector3 previous = centre + new Vector3(radius, 0f, 0f);
		for (int i = 1; i <= segments; i++)
		{
			float angle = i * Mathf.Tau / segments;
			Vector3 next = centre + new Vector3(Mathf.Cos(angle) * radius, 0f, Mathf.Sin(angle) * radius);
			Line(previous, next, color);
			previous = next;
		}
	}

	private void Diamond(Vector3 at, float size, Color color)
	{
		Vector3 up = at + (Vector3.Up * size);
		Vector3 down = at - (Vector3.Up * size);
		Vector3 x = Vector3.Right * size;
		Vector3 z = Vector3.Back * size;
		Vector3[] around = { at + x, at + z, at - x, at - z };
		for (int i = 0; i < around.Length; i++)
		{
			Line(around[i], around[(i + 1) % around.Length], color);
			Line(around[i], up, color);
			Line(around[i], down, color);
		}
	}

	private void Label(Vector3 at, string text, Color color, int size)
	{
		Label3D label;
		if (_labelsUsed < _labels.Count)
		{
			label = _labels[_labelsUsed];
		}
		else
		{
			label = new Label3D
			{
				Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
				NoDepthTest = true,
				FixedSize = true,
				PixelSize = 0.001f,
				OutlineSize = 8,
				OutlineModulate = new Color(0f, 0f, 0f, 0.85f),
				RenderPriority = 20,
				OutlineRenderPriority = 19,
				HorizontalAlignment = HorizontalAlignment.Center,
				VerticalAlignment = VerticalAlignment.Bottom,
			};
			AddChild(label);
			_labels.Add(label);
		}

		_labelsUsed++;
		label.Visible = true;
		label.Position = at;
		label.Text = text;
		label.FontSize = size;
		label.Modulate = color;
	}

	private void Flush()
	{
		for (int i = _labelsUsed; i < _labels.Count; i++)
		{
			_labels[i].Visible = false;
		}

		_mesh.ClearSurfaces();
		if (_points.Count == 0)
		{
			return;
		}

		_mesh.SurfaceBegin(Mesh.PrimitiveType.Lines, _material);
		for (int i = 0; i < _points.Count; i++)
		{
			_mesh.SurfaceSetColor(_colors[i]);
			_mesh.SurfaceAddVertex(_points[i]);
		}

		_mesh.SurfaceEnd();
	}
}
