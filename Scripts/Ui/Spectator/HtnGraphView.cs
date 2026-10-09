using System.Text;
using Gdpyr.Sim.AiDebug;
using Godot;

namespace Gdpyr.Ui.Spectator;

/// <summary>
/// A domain drawn as boxes and links (docs/AI_DEBUG.md §4.2): depth across, leaves
/// down (<see cref="HtnGraphLayout"/>). The running task is filled green, planned
/// tasks amber with their place in the plan, tasks behind a pause blue; the way the
/// plan came down the tree is outlined and its links are bright, everything else is
/// dim. A dot on a box says whether its conditions hold now — green all of them, red
/// not — and hovering a box lists them.
/// </summary>
public partial class HtnGraphView : Control
{
	private const float ColumnWidth = 142f;
	private const float RowHeight = 22f;
	private const float BoxGap = 14f;
	private const float Pad = 6f;
	private const int FontSize = 12;

	private static readonly Color Running = new(0.49f, 0.99f, 0.42f);
	private static readonly Color Planned = new(1f, 0.83f, 0.3f);
	private static readonly Color Paused = new(0.36f, 0.82f, 1f);
	private static readonly Color OnPath = new(0.95f, 0.95f, 0.95f);
	private static readonly Color Off = new(0.38f, 0.4f, 0.44f);
	private static readonly Color Fill = new(0.12f, 0.13f, 0.15f, 0.95f);
	private static readonly Color Holds = new(0.42f, 0.84f, 0.42f);
	private static readonly Color Fails = new(1f, 0.42f, 0.42f);

	private HtnDomainMap _map;
	private HtnTrace _trace;
	private HtnGraphLayout _layout;
	private bool[] _path;

	public override void _Ready()
	{
		MouseFilter = MouseFilterEnum.Pass;

		// A tooltip only shows when there is some text to show; the real text comes from
		// _GetTooltip for whichever box is under the pointer.
		TooltipText = " ";
	}

	/// <summary>What to draw. Null map clears it.</summary>
	public void Display(HtnDomainMap map, HtnTrace trace, bool collapse)
	{
		_map = map;
		_trace = trace;
		_layout = map == null ? null : HtnGraphLayout.Compute(map, trace, collapse);
		_path = map == null ? null : trace?.PathMask(map) ?? new bool[map.Count];

		CustomMinimumSize = _layout == null
			? Vector2.Zero
			: new Vector2((_layout.Columns * ColumnWidth) + (Pad * 2f), (_layout.Rows * RowHeight) + (Pad * 2f));
		QueueRedraw();
	}

	public override void _Draw()
	{
		if (_map == null || _layout == null)
		{
			return;
		}

		Font font = ThemeDB.FallbackFont;

		// Links first, under the boxes.
		for (int i = 1; i < _map.Count; i++)
		{
			if (!_layout.Visible[i])
			{
				continue;
			}

			int parent = _map[i].Parent;
			Rect2 from = BoxOf(parent);
			Rect2 to = BoxOf(i);
			Vector2 a = new(from.End.X, from.Position.Y + (from.Size.Y * 0.5f));
			Vector2 b = new(to.Position.X, to.Position.Y + (to.Size.Y * 0.5f));
			float elbow = a.X + (BoxGap * 0.5f);
			Color color = _path[i] ? OnPath : Off;
			float width = _path[i] ? 2f : 1f;
			DrawLine(a, new Vector2(elbow, a.Y), color, width);
			DrawLine(new Vector2(elbow, a.Y), new Vector2(elbow, b.Y), color, width);
			DrawLine(new Vector2(elbow, b.Y), b, color, width);
		}

		for (int i = 0; i < _map.Count; i++)
		{
			if (!_layout.Visible[i])
			{
				continue;
			}

			HtnNode node = _map[i];
			Rect2 box = BoxOf(i);
			HtnTaskState state = _trace?.StateOf(i) ?? HtnTaskState.None;

			Color fill = state switch
			{
				HtnTaskState.Running => Running.Darkened(0.45f),
				HtnTaskState.Planned => Planned.Darkened(0.55f),
				HtnTaskState.Paused => Paused.Darkened(0.55f),
				_ => Fill,
			};

			Color edge = state switch
			{
				HtnTaskState.Running => Running,
				HtnTaskState.Planned => Planned,
				HtnTaskState.Paused => Paused,
				_ => _path[i] ? OnPath : Off,
			};

			DrawRect(box, fill);
			DrawRect(box, edge, filled: false, width: state == HtnTaskState.Running || _path[i] ? 2f : 1f);

			string name = node.Kind == HtnNodeKind.Pause ? "|| pause" : node.Name;
			if (state == HtnTaskState.Planned)
			{
				name = $"#{_trace.OrderOf(i)} {name}";
			}
			else if (state == HtnTaskState.Running)
			{
				name = $"> {name}";
			}

			bool compound = node.IsCompound;
			Color text = state != HtnTaskState.None ? Colors.White : _path[i] ? OnPath : Off.Lightened(0.35f);
			DrawString(font, new Vector2(box.Position.X + 5f, box.Position.Y + 15f), name, HorizontalAlignment.Left,
				box.Size.X - 14f, FontSize, compound ? text.Lightened(0.1f) : text);

			if (TryConditionsHold(node, out bool all))
			{
				DrawCircle(new Vector2(box.End.X - 6f, box.Position.Y + 6f), 3f, all ? Holds : Fails);
			}
		}
	}

	public override string _GetTooltip(Vector2 atPosition)
	{
		if (_map == null || _layout == null)
		{
			return string.Empty;
		}

		for (int i = 0; i < _map.Count; i++)
		{
			if (!_layout.Visible[i] || !BoxOf(i).HasPoint(atPosition))
			{
				continue;
			}

			HtnNode node = _map[i];
			var text = new StringBuilder(_map.PathOf(i));
			HtnTaskState state = _trace?.StateOf(i) ?? HtnTaskState.None;
			if (state != HtnTaskState.None)
			{
				text.Append($"\n{state.ToString().ToLowerInvariant()}");
			}

			AppendConditions(text, node.Conditions, "if");
			AppendConditions(text, node.Executing, "keeps running while");
			if (node.Effects.Count > 0)
			{
				text.Append("\npredicts ").Append(string.Join(", ", node.Effects));
			}

			return text.ToString();
		}

		return string.Empty;
	}

	private void AppendConditions(StringBuilder text, System.Collections.Generic.IReadOnlyList<int> conditions,
		string heading)
	{
		if (conditions.Count == 0)
		{
			return;
		}

		text.Append('\n').Append(heading).Append(':');
		bool known = _trace != null && _trace.Holds.Count == _map.Conditions.Count;
		foreach (int c in conditions)
		{
			text.Append("\n  ");
			if (known)
			{
				text.Append(_trace.Held(c) ? "+ " : "- ");
			}

			text.Append(_map.ConditionName(c));
		}
	}

	private bool TryConditionsHold(HtnNode node, out bool all)
	{
		all = true;
		if (_trace == null || _trace.Holds.Count != _map.Conditions.Count || node.Conditions.Count == 0)
		{
			return false;
		}

		foreach (int c in node.Conditions)
		{
			all &= _trace.Held(c);
		}

		return true;
	}

	private Rect2 BoxOf(int node) => new(
		Pad + (_layout.Column[node] * ColumnWidth),
		Pad + (_layout.Row[node] * RowHeight),
		ColumnWidth - BoxGap,
		RowHeight - 4f);
}
