using System.Collections.Generic;
using System.Text;

namespace Gdpyr.Sim.AiDebug;

/// <summary>Plain text for the console and logs; Godot BBCode for a RichTextLabel.</summary>
public enum HtnTextStyle : byte
{
	Plain = 0,
	Rich = 1,
}

public struct HtnTreeOptions
{
	/// <summary>Show each task's conditions, marked holding or failing, and its executing conditions and effects.</summary>
	public bool Conditions;

	/// <summary>Fold every compound task off the plan's path down to one line.</summary>
	public bool Collapse;

	/// <summary>The tick "now" is, for how long the running task has run. 0 leaves durations out.</summary>
	public uint Tick;

	public static HtnTreeOptions Full(uint tick) => new() { Conditions = true, Tick = tick };
}

/// <summary>
/// HTNs as text (docs/AI_DEBUG.md §4.1): the domain as a tree with the plan marked on
/// it, the plan as one line, the facts, and the history. One renderer for the
/// console and the spectator's panel, so the two never disagree about what a
/// marker means.
///
/// <code>
/// ground
/// ├─ retreat
/// │  ├─ leave defences  [-InsideDefences=True]
/// ...
/// ├─ attack  [+Contact!=None]
/// │  ├─ engage  [+ThreatKind=Infantry +Contact=Visible]
/// │  │  └─ >> Engage  running 2.4s  {+while Contact=Visible +while ThreatKind=Infantry}
/// </code>
///
/// <c>&gt;&gt;</c> is the running task, <c>#n</c> the n-th task planned after it,
/// <c>||</c> a task set aside behind a pause; <c>+</c> a condition that holds now,
/// <c>-</c> one that does not.
/// </summary>
public static class HtnText
{
	private const string Running = "7cfc6a";
	private const string Planned = "ffd34d";
	private const string Paused = "5bd0ff";
	private const string Path = "ffffff";
	private const string Dim = "8a8f98";
	private const string Good = "6bd66b";
	private const string Bad = "ff6b6b";
	private const string Note = "c8b6ff";

	public static string Legend(HtnTextStyle style)
	{
		var w = new Writer(style);
		w.Text(">> running", Running).Text("   ").Text("#n planned", Planned).Text("   ")
			.Text("|| after a pause", Paused).Text("   ").Text("+ holds", Good).Text("  ").Text("- fails", Bad);
		return w.ToString();
	}

	/// <summary>The whole domain, with <paramref name="trace"/>'s plan marked on it; the bare domain when it is null.</summary>
	public static string Tree(HtnDomainMap map, HtnTrace trace, HtnTextStyle style, in HtnTreeOptions options)
	{
		if (map == null || map.Count == 0)
		{
			return string.Empty;
		}

		var w = new Writer(style);
		bool[] path = trace?.PathMask(map) ?? new bool[map.Count];
		var lead = new StringBuilder();
		Node(w, map, trace, path, 0, lead, last: true, root: true, options);
		return w.ToString().TrimEnd('\n');
	}

	/// <summary>
	/// The plan on one line: <c>running Stage 4.1s · then Strike after the pause</c>.
	/// "idle" between tasks, which with a domain whose last method always plans is a
	/// state that lasts one tick.
	/// </summary>
	public static string Plan(HtnDomainMap map, HtnTrace trace, HtnTextStyle style, uint tick = 0)
	{
		var w = new Writer(style);
		if (map == null || trace == null || !trace.HasPlan)
		{
			return w.Text("no plan", Dim).ToString();
		}

		if (trace.Current >= 0)
		{
			w.Text("running ", Dim).Text(map.NameOf(trace.Current), Running, bold: true);
			AppendRunTime(w, trace, tick);
		}
		else
		{
			w.Text("idle", Dim);
		}

		if (trace.Planned.Count > 0)
		{
			w.Text("  then ", Dim);
			for (int i = 0; i < trace.Planned.Count; i++)
			{
				w.Text(i == 0 ? string.Empty : " → ", Dim).Text(map.NameOf(trace.Planned[i]), Planned);
			}
		}

		if (trace.Paused.Count > 0)
		{
			w.Text("  ‖ after the pause ", Dim);
			bool first = true;
			foreach (int node in trace.Paused)
			{
				if (map.Contains(node) && map[node].Kind == HtnNodeKind.Pause)
				{
					continue;
				}

				w.Text(first ? string.Empty : " → ", Dim).Text(map.NameOf(node), Paused);
				first = false;
			}
		}

		if (trace.Traversal.Count > 0)
		{
			w.Text("   MTR ", Dim).Text(string.Join(".", trace.Traversal), Dim);
		}

		return w.ToString();
	}

	/// <summary>Every fact, <c>Name=Value</c>, <paramref name="perLine"/> to a line.</summary>
	public static string Facts(HtnSchema schema, HtnTrace trace, HtnTextStyle style, int perLine = 3)
	{
		var w = new Writer(style);
		if (schema == null || trace == null || trace.Facts.Count == 0)
		{
			return w.Text("no facts", Dim).ToString();
		}

		int count = System.Math.Min(trace.Facts.Count, schema.FactCount);
		int width = 0;
		for (int f = 0; f < count; f++)
		{
			width = System.Math.Max(width, schema.FactName(f).Length + 1 + schema.FactValue(f, trace.Facts[f]).Length);
		}

		for (int f = 0; f < count; f++)
		{
			string name = schema.FactName(f);
			string value = schema.FactValue(f, trace.Facts[f]);
			bool quiet = trace.Facts[f] == 0;
			w.Text(name, Dim).Text("=", Dim).Text(value, quiet ? Dim : Path, bold: !quiet);

			bool endOfLine = (f + 1) % perLine == 0 || f == count - 1;
			w.Text(endOfLine ? "\n" : new string(' ', width - name.Length - 1 - value.Length + 2));
		}

		return w.ToString().TrimEnd('\n');
	}

	/// <summary>The history, newest first: <c>-1.2s  Engage ended: while Contact=Visible failed</c>.</summary>
	public static string History(HtnDomainMap map, HtnTrace trace, HtnTextStyle style, uint tick = 0)
	{
		var w = new Writer(style);
		if (map == null || trace == null || trace.History.Count == 0)
		{
			return w.Text("no history yet", Dim).ToString();
		}

		for (int i = trace.History.Count - 1; i >= 0; i--)
		{
			HtnEvent e = trace.History[i];
			if (tick > 0)
			{
				float seconds = (tick >= e.Tick ? tick - e.Tick : 0u) / (float)SimConfig.TickRate;
				w.Text($"-{seconds,5:0.0}s  ", Dim);
			}
			else
			{
				w.Text($"t{e.Tick,-7} ", Dim);
			}

			Describe(w, map, e);
			w.Text("\n");
		}

		return w.ToString().TrimEnd('\n');
	}

	/// <summary>One history event, in words.</summary>
	public static string Describe(HtnDomainMap map, in HtnEvent e, HtnTextStyle style = HtnTextStyle.Plain)
	{
		var w = new Writer(style);
		Describe(w, map, e);
		return w.ToString();
	}

	private static void Describe(Writer w, HtnDomainMap map, in HtnEvent e)
	{
		string node = e.Node == HtnHistory.None ? "?" : map.NameOf(e.Node);
		string other = e.Other == HtnHistory.None ? null : e.Other.ToString();

		switch (e.Kind)
		{
			case HtnEventKind.NewPlan:
				w.Text("planned ", Dim).Text(node, Planned);
				break;

			case HtnEventKind.ReplacePlan:
				w.Text("replanned to ", Dim).Text(node, Planned);
				if (e.Other != HtnHistory.None)
				{
					w.Text(", interrupting ", Dim).Text(map.NameOf(e.Other), Bad);
				}
				break;

			case HtnEventKind.Started:
				w.Text("started ", Dim).Text(node, Running);
				break;

			case HtnEventKind.Succeeded:
				w.Text(node, Path).Text(" succeeded", Good);
				break;

			case HtnEventKind.Failed:
				w.Text(node, Path).Text(" failed", Bad).Text(" (its operator gave up)", Dim);
				break;

			case HtnEventKind.ConditionFailed:
				w.Text(node, Path).Text(" ended: ", Dim)
					.Text(other == null ? "an executing condition" : map.ConditionName(e.Other), Bad)
					.Text(" stopped holding", Dim);
				break;

			case HtnEventKind.Stopped:
				w.Text(node, Path).Text(" stopped", Note).Text(" (plan replaced)", Dim);
				break;

			case HtnEventKind.Rejected:
				w.Text(node, Path).Text(" not started: ", Dim)
					.Text(other == null ? "a condition" : map.ConditionName(e.Other), Bad).Text(" failed", Dim);
				break;

			case HtnEventKind.Reset:
				w.Text("plan dropped", Note).Text(" (death, new order or reset)", Dim);
				break;

			default:
				w.Text(e.Kind.ToString(), Dim);
				break;
		}
	}

	private static void Node(Writer w, HtnDomainMap map, HtnTrace trace, bool[] path, int index, StringBuilder lead,
		bool last, bool root, in HtnTreeOptions options)
	{
		HtnNode node = map[index];
		bool onPath = path[index];

		if (!root)
		{
			w.Text(lead.ToString(), Dim).Text(last ? "└─ " : "├─ ", Dim);
		}

		HtnTaskState state = trace?.StateOf(index) ?? HtnTaskState.None;
		switch (state)
		{
			case HtnTaskState.Running:
				w.Text(">> ", Running, bold: true).Text(node.Name, Running, bold: true);
				w.Text("  running", Running);
				AppendRunTime(w, trace, options.Tick);
				break;

			case HtnTaskState.Planned:
				w.Text($"#{trace.OrderOf(index)} ", Planned).Text(node.Name, Planned).Text("  planned", Planned);
				break;

			case HtnTaskState.Paused:
				w.Text("|| ", Paused).Text(node.Name, Paused).Text("  after the pause", Paused);
				break;

			default:
				if (node.Kind == HtnNodeKind.Pause)
				{
					w.Text("-- pause --", onPath ? Paused : Dim);
				}
				else
				{
					w.Text(node.Name, onPath ? Path : Dim, bold: onPath && node.IsCompound);
				}
				break;
		}

		bool folded = options.Collapse && !root && node.IsCompound && !onPath && node.Children.Count > 0;
		if (folded)
		{
			w.Text($"  (+{node.Children.Count})", Dim);
		}

		if (options.Conditions)
		{
			AppendConditions(w, map, trace, node.Conditions, "[", "]");
			AppendConditions(w, map, trace, node.Executing, "{", "}");
			if (node.Effects.Count > 0)
			{
				w.Text("  => ", Dim).Text(string.Join(" ", node.Effects), Note);
			}
		}

		w.Text("\n");

		if (folded)
		{
			return;
		}

		int before = lead.Length;
		if (!root)
		{
			lead.Append(last ? "   " : "│  ");
		}

		IReadOnlyList<int> children = node.Children;
		for (int i = 0; i < children.Count; i++)
		{
			Node(w, map, trace, path, children[i], lead, i == children.Count - 1, root: false, options);
		}

		lead.Length = before;
	}

	private static void AppendConditions(Writer w, HtnDomainMap map, HtnTrace trace, IReadOnlyList<int> conditions,
		string open, string close)
	{
		if (conditions.Count == 0)
		{
			return;
		}

		bool known = trace != null && trace.Holds.Count == map.Conditions.Count;
		w.Text("  ").Text(open, Dim);
		for (int i = 0; i < conditions.Count; i++)
		{
			int c = conditions[i];
			if (i > 0)
			{
				w.Text(" ");
			}

			if (!known)
			{
				w.Text(map.ConditionName(c), Dim);
				continue;
			}

			bool holds = trace.Held(c);
			w.Text((holds ? "+" : "-") + map.ConditionName(c), holds ? Good : Bad);
		}

		w.Text(close, Dim);
	}

	private static void AppendRunTime(Writer w, HtnTrace trace, uint tick)
	{
		if (tick == 0 || trace.CurrentStartedTick is not { } started || started > tick)
		{
			return;
		}

		w.Text($" {(tick - started) / (float)SimConfig.TickRate:0.0}s", Running);
	}

	/// <summary>Appends text, coloured and escaped in the rich style and bare in the plain one.</summary>
	private sealed class Writer
	{
		private readonly StringBuilder _text = new();
		private readonly HtnTextStyle _style;

		public Writer(HtnTextStyle style) => _style = style;

		public Writer Text(string text, string color = null, bool bold = false)
		{
			if (string.IsNullOrEmpty(text))
			{
				return this;
			}

			if (_style == HtnTextStyle.Plain)
			{
				_text.Append(text);
				return this;
			}

			if (bold)
			{
				_text.Append("[b]");
			}

			if (color != null)
			{
				_text.Append("[color=#").Append(color).Append(']');
			}

			Escape(_text, text);

			if (color != null)
			{
				_text.Append("[/color]");
			}

			if (bold)
			{
				_text.Append("[/b]");
			}

			return this;
		}

		public override string ToString() => _text.ToString();
	}

	/// <summary>A literal <c>[</c> in BBCode is <c>[lb]</c>; everything else passes through.</summary>
	public static void Escape(StringBuilder into, string text)
	{
		foreach (char c in text)
		{
			if (c == '[')
			{
				into.Append("[lb]");
			}
			else if (c == ']')
			{
				into.Append("[rb]");
			}
			else
			{
				into.Append(c);
			}
		}
	}

	public static string Escape(string text)
	{
		var builder = new StringBuilder(text?.Length ?? 0);
		Escape(builder, text ?? string.Empty);
		return builder.ToString();
	}
}
