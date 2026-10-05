using System;
using System.Collections.Generic;
using FluidHTN;
using FluidHTN.Compounds;
using FluidHTN.Conditions;
using FluidHTN.PrimitiveTasks;

namespace Gdpyr.Sim.AiDebug;

/// <summary>Which of the three planners a map, a trace or a fact is about (docs/HTN_BOTS.md §5).</summary>
public enum HtnDomainKind : byte
{
	None = 0,

	/// <summary>The ground bot's domain (§5.1).</summary>
	Ground = 1,

	/// <summary>The RTS unit's domain (§5.2).</summary>
	Unit = 2,

	/// <summary>A commander squad's domain (§5.3).</summary>
	Squad = 3,
}

public enum HtnNodeKind : byte
{
	/// <summary>The domain's root: a selector over the top-level tasks.</summary>
	Root = 0,

	Selector = 1,

	Sequence = 2,

	/// <summary>A primitive task: the thing that runs.</summary>
	Primitive = 3,

	/// <summary>A <c>PausePlan</c>: the rest of its sequence is planned once what is before it has run.</summary>
	Pause = 4,

	Slot = 5,
}

/// <summary>One task of a domain, as the debugger knows it: where it sits and what guards it.</summary>
public sealed class HtnNode
{
	internal HtnNode(int index, int parent, int depth, int branch, HtnNodeKind kind, string name)
	{
		Index = index;
		Parent = parent;
		Depth = depth;
		Branch = branch;
		Kind = kind;
		Name = name ?? string.Empty;
	}

	/// <summary>Its place in a depth-first walk of the domain: what the wire carries for it.</summary>
	public int Index { get; }

	/// <summary>-1 for the root.</summary>
	public int Parent { get; }

	public int Depth { get; }

	/// <summary>Its index among its parent's subtasks: the number a method traversal record holds for a selector's choice.</summary>
	public int Branch { get; }

	public HtnNodeKind Kind { get; }

	public string Name { get; }

	public bool IsCompound => Kind is HtnNodeKind.Root or HtnNodeKind.Selector or HtnNodeKind.Sequence;

	/// <summary>Indices into <see cref="HtnDomainMap.Conditions"/>: what has to hold for it to be planned.</summary>
	public IReadOnlyList<int> Conditions { get; internal set; } = Array.Empty<int>();

	/// <summary>Indices into <see cref="HtnDomainMap.Conditions"/>: what has to keep holding while it runs (§3.4, P3).</summary>
	public IReadOnlyList<int> Executing { get; internal set; } = Array.Empty<int>();

	/// <summary>Its effects' names: for this game, the predictions a plan is made through (§3.4, P4).</summary>
	public IReadOnlyList<string> Effects { get; internal set; } = Array.Empty<string>();

	public IReadOnlyList<int> Children { get; internal set; } = Array.Empty<int>();
}

/// <summary>One condition of a domain: a planning condition or an executing one, on one task.</summary>
public sealed class HtnConditionInfo
{
	internal HtnConditionInfo(int index, int node, string name, bool executing, ICondition source)
	{
		Index = index;
		Node = node;
		Name = name ?? string.Empty;
		Executing = executing;
		Source = source;
	}

	public int Index { get; }

	/// <summary>The task it guards.</summary>
	public int Node { get; }

	public string Name { get; }

	/// <summary>Checked before every operator update rather than when the task is planned.</summary>
	public bool Executing { get; }

	/// <summary>The condition itself: present on the process that built the map from a live domain.</summary>
	internal ICondition Source { get; }
}

/// <summary>
/// A FluidHTN domain, flattened for the debugger (docs/AI_DEBUG.md §3): every task
/// numbered in a depth-first walk, every condition numbered in the same walk, and
/// the parent and child links between them.
///
/// Both ends of the wire build the same map from the same code — the server from
/// the domain its bots plan with, a spectator from a fresh build of it — so a task
/// travels as its index and never as its name. <see cref="Signature"/> is what says
/// the two builds agree: a client built from different sources reads every index
/// wrong, and is told so instead.
///
/// Built once per domain, never on the tick. Looking a task or a condition up by
/// reference allocates nothing, which is what lets <see cref="HtnHistory"/> hang off
/// the planner's callbacks without breaking the planner's zero-allocation rule
/// (docs/HTN_BOTS.md §4.2 rule 3).
/// </summary>
public sealed class HtnDomainMap
{
	/// <summary>A task index or a condition index has to fit in a byte on the wire, with 255 left for "none".</summary>
	public const int MaxNodes = byte.MaxValue;

	public const int MaxConditions = byte.MaxValue;

	private readonly List<HtnNode> _nodes = new();
	private readonly List<HtnConditionInfo> _conditions = new();
	private readonly Dictionary<ITask, int> _taskIndex = new(ReferenceComparer<ITask>.Instance);
	private readonly Dictionary<ICondition, int> _conditionIndex = new(ReferenceComparer<ICondition>.Instance);

	private HtnDomainMap(HtnDomainKind kind) => Kind = kind;

	public HtnDomainKind Kind { get; }

	/// <summary>The root's name: <c>ground</c>, <c>unit</c>, <c>squad</c>.</summary>
	public string Name => _nodes.Count > 0 ? _nodes[0].Name : string.Empty;

	public IReadOnlyList<HtnNode> Nodes => _nodes;

	public IReadOnlyList<HtnConditionInfo> Conditions => _conditions;

	/// <summary>
	/// FNV-1a over every node's kind, parent, name, conditions and effects. Equal on
	/// two processes when, and only when, they number the domain the same way.
	/// </summary>
	public uint Signature { get; private set; }

	public HtnNode this[int index] => _nodes[index];

	public int Count => _nodes.Count;

	public bool Contains(int index) => index >= 0 && index < _nodes.Count;

	/// <summary>Flattens <paramref name="domain"/>. Throws when it is too large to put on the wire.</summary>
	public static HtnDomainMap Build<T>(HtnDomainKind kind, Domain<T> domain) where T : IContext =>
		Build(kind, domain?.Root ?? throw new ArgumentNullException(nameof(domain)));

	public static HtnDomainMap Build(HtnDomainKind kind, TaskRoot root)
	{
		if (root == null)
		{
			throw new ArgumentNullException(nameof(root));
		}

		var map = new HtnDomainMap(kind);
		map.Add(root, parent: -1, depth: 0, branch: 0);

		if (map._nodes.Count > MaxNodes || map._conditions.Count > MaxConditions)
		{
			throw new InvalidOperationException($"domain '{root.Name}' has {map._nodes.Count} tasks and"
				+ $" {map._conditions.Count} conditions; the debugger's wire format holds {MaxNodes} of each");
		}

		map.Signature = map.ComputeSignature();
		return map;
	}

	/// <summary>The task's index, or -1 for null or a task from another domain.</summary>
	public int IndexOf(ITask task) => task != null && _taskIndex.TryGetValue(task, out int index) ? index : -1;

	/// <summary>The condition's index, or -1.</summary>
	public int IndexOf(ICondition condition) =>
		condition != null && _conditionIndex.TryGetValue(condition, out int index) ? index : -1;

	/// <summary>
	/// Whether a condition holds for <paramref name="context"/> now. False when this
	/// map was not built from a live domain, and for a condition that throws: the
	/// debugger must never be the thing that takes a server down.
	/// </summary>
	public bool Holds(int condition, IContext context)
	{
		if (condition < 0 || condition >= _conditions.Count || context == null
			|| _conditions[condition].Source is not { } source)
		{
			return false;
		}

		try
		{
			return source.IsValid(context);
		}
		catch (Exception)
		{
			return false;
		}
	}

	/// <summary>True when <paramref name="ancestor"/> is <paramref name="node"/> or above it.</summary>
	public bool IsAncestorOf(int ancestor, int node)
	{
		for (int at = node; at >= 0 && at < _nodes.Count; at = _nodes[at].Parent)
		{
			if (at == ancestor)
			{
				return true;
			}
		}

		return false;
	}

	/// <summary>The node's name, or <c>?</c> for an index this map does not have.</summary>
	public string NameOf(int index) => Contains(index) ? _nodes[index].Name : "?";

	/// <summary>The condition's name, or <c>?</c>.</summary>
	public string ConditionName(int index) =>
		index >= 0 && index < _conditions.Count ? _conditions[index].Name : "?";

	/// <summary>
	/// The path from the root to <paramref name="node"/>, root first, as names joined
	/// by <c> › </c>: <c>ground › attack › engage › Engage</c>.
	/// </summary>
	public string PathOf(int node)
	{
		if (!Contains(node))
		{
			return "?";
		}

		var names = new List<string>();
		for (int at = node; at >= 0; at = _nodes[at].Parent)
		{
			names.Add(_nodes[at].Name);
		}

		names.Reverse();
		return string.Join(" › ", names);
	}

	private int Add(ITask task, int parent, int depth, int branch)
	{
		int index = _nodes.Count;
		var node = new HtnNode(index, parent, depth, branch, KindOf(task), task.Name);
		_nodes.Add(node);

		if (!_taskIndex.ContainsKey(task))
		{
			_taskIndex.Add(task, index);
		}

		node.Conditions = AddConditions(task.Conditions, index, executing: false);

		if (task is IPrimitiveTask primitive)
		{
			node.Executing = AddConditions(primitive.ExecutingConditions, index, executing: true);
			if (primitive.Effects is { Count: > 0 } effects)
			{
				var names = new string[effects.Count];
				for (int i = 0; i < effects.Count; i++)
				{
					names[i] = effects[i]?.Name ?? string.Empty;
				}

				node.Effects = names;
			}
		}

		if (task is ICompoundTask compound && compound.Subtasks.Count > 0)
		{
			var children = new int[compound.Subtasks.Count];
			for (int i = 0; i < compound.Subtasks.Count; i++)
			{
				children[i] = Add(compound.Subtasks[i], index, depth + 1, i);
			}

			node.Children = children;
		}

		return index;
	}

	private int[] AddConditions(List<ICondition> conditions, int node, bool executing)
	{
		if (conditions == null || conditions.Count == 0)
		{
			return Array.Empty<int>();
		}

		var indices = new int[conditions.Count];
		for (int i = 0; i < conditions.Count; i++)
		{
			ICondition condition = conditions[i];
			int index = _conditions.Count;
			_conditions.Add(new HtnConditionInfo(index, node, condition?.Name, executing, condition));
			if (condition != null && !_conditionIndex.ContainsKey(condition))
			{
				_conditionIndex.Add(condition, index);
			}

			indices[i] = index;
		}

		return indices;
	}

	private static HtnNodeKind KindOf(ITask task) => task switch
	{
		TaskRoot => HtnNodeKind.Root,
		Selector => HtnNodeKind.Selector,
		Sequence => HtnNodeKind.Sequence,
		IPrimitiveTask => HtnNodeKind.Primitive,
		PausePlanTask => HtnNodeKind.Pause,
		_ => HtnNodeKind.Slot,
	};

	private uint ComputeSignature()
	{
		uint hash = 2166136261u;
		Mix(ref hash, (byte)Kind);
		foreach (HtnNode node in _nodes)
		{
			Mix(ref hash, (byte)node.Kind);
			Mix(ref hash, node.Parent + 1);
			Mix(ref hash, node.Name);
			foreach (int c in node.Conditions)
			{
				Mix(ref hash, _conditions[c].Name);
			}

			Mix(ref hash, 0xFE);
			foreach (int c in node.Executing)
			{
				Mix(ref hash, _conditions[c].Name);
			}

			Mix(ref hash, 0xFD);
			foreach (string effect in node.Effects)
			{
				Mix(ref hash, effect);
			}
		}

		return hash;
	}

	private static void Mix(ref uint hash, int value)
	{
		for (int i = 0; i < 4; i++)
		{
			hash = (hash ^ (byte)(value >> (i * 8))) * 16777619u;
		}
	}

	private static void Mix(ref uint hash, string text)
	{
		foreach (char c in text ?? string.Empty)
		{
			hash = (hash ^ (byte)c) * 16777619u;
			hash = (hash ^ (byte)(c >> 8)) * 16777619u;
		}

		hash = (hash ^ 0xFFu) * 16777619u;
	}

	/// <summary>Identity, not equality: two tasks with one name are two tasks.</summary>
	private sealed class ReferenceComparer<TRef> : IEqualityComparer<TRef> where TRef : class
	{
		public static readonly ReferenceComparer<TRef> Instance = new();

		public bool Equals(TRef x, TRef y) => ReferenceEquals(x, y);

		public int GetHashCode(TRef obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
	}
}
