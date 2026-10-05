using System;
using System.Collections.Generic;
using FluidHTN;
using FluidHTN.Compounds;

namespace Gdpyr.Sim.AiDebug;

/// <summary>
/// One agent's plan at one moment, by index into its domain's
/// <see cref="HtnDomainMap"/> (docs/AI_DEBUG.md §3.2): the task running, the tasks
/// planned behind it, the tasks set aside behind a pause, the facts, which
/// conditions hold, and the recent history. Everything the text tree, the graph
/// and the world overlay draw comes out of one of these.
/// </summary>
public sealed class HtnTrace
{
	/// <summary>Bounds a malformed or hostile payload; no domain here comes near it.</summary>
	public const int MaxTasks = 64;

	public const int MaxFacts = 32;

	public const int MaxTraversal = 32;

	public HtnDomainKind Domain;

	/// <summary>The running task's index, or -1 between tasks.</summary>
	public int Current = -1;

	/// <summary>FluidHTN's <see cref="TaskStatus"/> of the last operator update.</summary>
	public TaskStatus LastStatus;

	/// <summary>Tasks planned after <see cref="Current"/>, in the order they will run.</summary>
	public readonly List<int> Planned = new();

	/// <summary>
	/// Tasks after a <c>PausePlan</c>: part of the plan, but not decomposed yet — they are
	/// planned against the world as it is once everything before the pause has run
	/// (docs/HTN_BOTS.md §3.4, P5). The commander's strike, while a squad stages.
	/// </summary>
	public readonly List<int> Paused = new();

	/// <summary>
	/// The method traversal record: the branch the plan took at each selector, root
	/// first. A new plan replaces this one only if it beats it (§3.4, P2, P3).
	/// </summary>
	public readonly List<int> Traversal = new();

	/// <summary>The world state, one byte per fact.</summary>
	public readonly List<byte> Facts = new();

	/// <summary>Per condition of the domain, in map order: whether it holds now.</summary>
	public readonly List<bool> Holds = new();

	public readonly List<HtnEvent> History = new();

	/// <summary>True when there is anything to show: a domain, and a plan or a fact.</summary>
	public bool HasPlan => Domain != HtnDomainKind.None;

	public void Clear()
	{
		Domain = HtnDomainKind.None;
		Current = -1;
		LastStatus = TaskStatus.Continue;
		Planned.Clear();
		Paused.Clear();
		Traversal.Clear();
		Facts.Clear();
		Holds.Clear();
		History.Clear();
	}

	/// <summary>Whether <paramref name="node"/> is the running task, planned, or paused.</summary>
	public HtnTaskState StateOf(int node)
	{
		if (node < 0)
		{
			return HtnTaskState.None;
		}

		if (node == Current)
		{
			return HtnTaskState.Running;
		}

		if (Planned.Contains(node))
		{
			return HtnTaskState.Planned;
		}

		return Paused.Contains(node) ? HtnTaskState.Paused : HtnTaskState.None;
	}

	/// <summary>Where <paramref name="node"/> stands in the plan: 0 running, 1 the next task, and so on; -1 when it is not in it.</summary>
	public int OrderOf(int node)
	{
		if (node < 0)
		{
			return -1;
		}

		if (node == Current)
		{
			return 0;
		}

		int planned = Planned.IndexOf(node);
		if (planned >= 0)
		{
			return planned + (Current >= 0 ? 1 : 0);
		}

		int paused = Paused.IndexOf(node);
		return paused < 0 ? -1 : Planned.Count + (Current >= 0 ? 1 : 0) + paused;
	}

	/// <summary>Whether <paramref name="condition"/> held when this was captured; false when it was not reported.</summary>
	public bool Held(int condition) => condition >= 0 && condition < Holds.Count && Holds[condition];

	/// <summary>The fact's value; 0 when it was not reported.</summary>
	public byte Fact(int fact) => fact >= 0 && fact < Facts.Count ? Facts[fact] : (byte)0;

	/// <summary>
	/// The tick the running task started on, from the history; null when the history
	/// does not go back that far.
	/// </summary>
	public uint? CurrentStartedTick
	{
		get
		{
			for (int i = History.Count - 1; i >= 0; i--)
			{
				if (History[i].Kind == HtnEventKind.Started && History[i].Node == Current)
				{
					return History[i].Tick;
				}
			}

			return null;
		}
	}

	/// <summary>
	/// The nodes on the plan's path: every ancestor of the running, planned and paused
	/// tasks, the tasks themselves included. What the views draw as "the way the plan
	/// came down".
	/// </summary>
	public bool[] PathMask(HtnDomainMap map)
	{
		var mask = new bool[map?.Count ?? 0];
		if (map == null)
		{
			return mask;
		}

		Mark(map, mask, Current);
		foreach (int node in Planned)
		{
			Mark(map, mask, node);
		}

		foreach (int node in Paused)
		{
			Mark(map, mask, node);
		}

		return mask;
	}

	public void CopyFrom(HtnTrace other)
	{
		Clear();
		if (other == null)
		{
			return;
		}

		Domain = other.Domain;
		Current = other.Current;
		LastStatus = other.LastStatus;
		Planned.AddRange(other.Planned);
		Paused.AddRange(other.Paused);
		Traversal.AddRange(other.Traversal);
		Facts.AddRange(other.Facts);
		Holds.AddRange(other.Holds);
		History.AddRange(other.History);
	}

	private static void Mark(HtnDomainMap map, bool[] mask, int node)
	{
		for (int at = node; map.Contains(at) && !mask[at]; at = map[at].Parent)
		{
			mask[at] = true;
		}
	}
}

/// <summary>Where a task stands in an agent's plan.</summary>
public enum HtnTaskState : byte
{
	None = 0,
	Running = 1,
	Planned = 2,
	Paused = 3,
}

/// <summary>Reads a live planning context into an <see cref="HtnTrace"/>. Server side: the contexts are there.</summary>
public static class HtnTracer
{
	/// <summary>
	/// Fills <paramref name="into"/> from <paramref name="context"/>'s planner state.
	/// Reads only — no condition here writes, and none is evaluated in planning mode,
	/// so the world state they read is the one the agent is executing against.
	/// </summary>
	public static void Capture(HtnDomainMap map, IContext context, HtnHistory history, HtnTrace into)
	{
		if (into == null)
		{
			throw new ArgumentNullException(nameof(into));
		}

		into.Clear();
		if (map == null || context == null || !context.IsInitialized)
		{
			return;
		}

		into.Domain = map.Kind;

		IPlannerState state = context.PlannerState;
		if (state != null)
		{
			into.Current = map.IndexOf(state.CurrentTask);
			into.LastStatus = state.LastStatus;

			if (state.Plan != null)
			{
				foreach (ITask task in state.Plan)
				{
					if (into.Planned.Count >= HtnTrace.MaxTasks)
					{
						break;
					}

					into.Planned.Add(map.IndexOf(task));
				}
			}
		}

		if (context.HasPausedPartialPlan && context.PartialPlanQueue != null)
		{
			foreach (PartialPlanEntry entry in context.PartialPlanQueue)
			{
				if (entry.Task is not ICompoundTask compound)
				{
					continue;
				}

				for (int i = entry.TaskIndex; i < compound.Subtasks.Count && into.Paused.Count < HtnTrace.MaxTasks; i++)
				{
					into.Paused.Add(map.IndexOf(compound.Subtasks[i]));
				}
			}
		}

		if (context.MethodTraversalRecord != null)
		{
			foreach (int branch in context.MethodTraversalRecord)
			{
				if (into.Traversal.Count >= HtnTrace.MaxTraversal)
				{
					break;
				}

				into.Traversal.Add(branch);
			}
		}

		byte[] facts = context.WorldState;
		if (facts != null)
		{
			for (int i = 0; i < facts.Length && i < HtnTrace.MaxFacts; i++)
			{
				into.Facts.Add(facts[i]);
			}
		}

		for (int c = 0; c < map.Conditions.Count; c++)
		{
			into.Holds.Add(map.Holds(c, context));
		}

		history?.CopyTo(into.History);
	}
}
