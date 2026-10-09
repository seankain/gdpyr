using System;
using System.Collections.Generic;
using FluidHTN;
using FluidHTN.Conditions;
using FluidHTN.PrimitiveTasks;

namespace Gdpyr.Sim.AiDebug;

/// <summary>What happened to a plan (docs/AI_DEBUG.md §3.3).</summary>
public enum HtnEventKind : byte
{
	None = 0,

	/// <summary>A plan was found with none running. <see cref="HtnEvent.Node"/> is its first task.</summary>
	NewPlan = 1,

	/// <summary>
	/// A higher-priority plan replaced the running one (docs/HTN_BOTS.md §3.4, P2).
	/// <see cref="HtnEvent.Node"/> is the new plan's first task, <see cref="HtnEvent.Other"/>
	/// the task it interrupted.
	/// </summary>
	ReplacePlan = 2,

	/// <summary>The task's operator started.</summary>
	Started = 3,

	Succeeded = 4,

	/// <summary>The operator reported failure: a give-up (§3.4, P4), or nothing to do.</summary>
	Failed = 5,

	/// <summary>An executing condition stopped holding (§3.4, P3). <see cref="HtnEvent.Other"/> is the condition.</summary>
	ConditionFailed = 6,

	/// <summary>The running task was stopped because its plan was replaced.</summary>
	Stopped = 7,

	/// <summary>The next task of a plan failed its own conditions when its turn came. <see cref="HtnEvent.Other"/> is the condition.</summary>
	Rejected = 8,

	/// <summary>The engine dropped the plan: a death, a new order, a squad slot reused.</summary>
	Reset = 9,
}

/// <summary>One planner event. <see cref="HtnHistory.None"/> where a node or condition does not apply.</summary>
public struct HtnEvent
{
	public uint Tick;

	public HtnEventKind Kind;

	public byte Node;

	public byte Other;
}

/// <summary>
/// The last <see cref="Capacity"/> things one agent's planner did, and when
/// (docs/AI_DEBUG.md §3.3): which plans it found, which it threw away for a better
/// one, and why each task ended. It answers the question a frozen picture of the
/// plan cannot — "why did it stop doing that?" — which is the question an HTN is
/// most often asked (docs/HTN_BOTS.md §9, plan thrash; §3.4, P3).
///
/// Fed from FluidHTN's <see cref="IPlannerState"/> callbacks, which fire only on
/// plan and task transitions, never on a task that simply continues. Recording is a
/// reference lookup and a write into a fixed ring: nothing is allocated after
/// <see cref="Attach"/>, so the planner's zero-allocation guard holds with it on
/// (docs/HTN_BOTS.md §4.2 rule 3).
/// </summary>
public sealed class HtnHistory
{
	public const int Capacity = 16;

	/// <summary>No node, no condition.</summary>
	public const byte None = byte.MaxValue;

	private readonly HtnEvent[] _ring = new HtnEvent[Capacity];
	private int _next;
	private HtnDomainMap _map;
	private Func<uint> _clock;

	/// <summary>Events held, at most <see cref="Capacity"/>.</summary>
	public int Count { get; private set; }

	/// <summary>Every event ever recorded, including those the ring has dropped.</summary>
	public int Total { get; private set; }

	/// <summary>The <paramref name="index"/>-th held event, oldest first.</summary>
	public HtnEvent this[int index]
	{
		get
		{
			if (index < 0 || index >= Count)
			{
				throw new ArgumentOutOfRangeException(nameof(index));
			}

			int start = (_next - Count + Capacity) % Capacity;
			return _ring[(start + index) % Capacity];
		}
	}

	/// <summary>
	/// Hooks this history to a planner. Callbacks already on <paramref name="state"/>
	/// are kept and run first. <paramref name="clock"/> stamps each event — the
	/// context's own tick, which the engine half writes before every planner tick.
	/// </summary>
	public void Attach(IPlannerState state, HtnDomainMap map, Func<uint> clock)
	{
		if (state == null || map == null)
		{
			throw new ArgumentNullException(state == null ? nameof(state) : nameof(map));
		}

		_map = map;
		_clock = clock;

		state.OnNewPlan += OnNewPlan;
		state.OnReplacePlan += OnReplacePlan;
		state.OnCurrentTaskStarted += OnStarted;
		state.OnCurrentTaskCompletedSuccessfully += OnSucceeded;
		state.OnCurrentTaskFailed += OnFailed;
		state.OnCurrentTaskExecutingConditionFailed += OnConditionFailed;
		state.OnStopCurrentTask += OnStopped;
		state.OnNewTaskConditionFailed += OnRejected;
	}

	/// <summary>Records that the engine dropped the plan (<see cref="HtnEventKind.Reset"/>).</summary>
	public void RecordReset() => Record(HtnEventKind.Reset, -1, -1);

	public void Clear()
	{
		Array.Clear(_ring);
		_next = 0;
		Count = 0;
		Total = 0;
	}

	/// <summary>Copies the held events, oldest first, into <paramref name="into"/>.</summary>
	public void CopyTo(List<HtnEvent> into)
	{
		for (int i = 0; i < Count; i++)
		{
			into.Add(this[i]);
		}
	}

	/// <summary>Records an event by index. Public for the tests and for events the engine half knows about.</summary>
	public void Record(HtnEventKind kind, int node, int other)
	{
		_ring[_next] = new HtnEvent
		{
			Tick = _clock?.Invoke() ?? 0u,
			Kind = kind,
			Node = ToByte(node),
			Other = ToByte(other),
		};

		_next = (_next + 1) % Capacity;
		Count = Math.Min(Count + 1, Capacity);
		Total++;
	}

	private void OnNewPlan(Queue<ITask> plan) =>
		Record(HtnEventKind.NewPlan, plan != null && plan.Count > 0 ? IndexOf(plan.Peek()) : -1, -1);

	private void OnReplacePlan(Queue<ITask> old, ITask current, Queue<ITask> plan) =>
		Record(HtnEventKind.ReplacePlan, plan != null && plan.Count > 0 ? IndexOf(plan.Peek()) : -1,
			IndexOf(current ?? (old != null && old.Count > 0 ? old.Peek() : null)));

	private void OnStarted(IPrimitiveTask task) => Record(HtnEventKind.Started, IndexOf(task), -1);

	private void OnSucceeded(IPrimitiveTask task) => Record(HtnEventKind.Succeeded, IndexOf(task), -1);

	private void OnFailed(IPrimitiveTask task) => Record(HtnEventKind.Failed, IndexOf(task), -1);

	private void OnConditionFailed(IPrimitiveTask task, ICondition condition) =>
		Record(HtnEventKind.ConditionFailed, IndexOf(task), _map.IndexOf(condition));

	private void OnStopped(IPrimitiveTask task) => Record(HtnEventKind.Stopped, IndexOf(task), -1);

	private void OnRejected(ITask task, ICondition condition) =>
		Record(HtnEventKind.Rejected, IndexOf(task), _map.IndexOf(condition));

	private int IndexOf(ITask task) => _map?.IndexOf(task) ?? -1;

	private static byte ToByte(int index) => index >= 0 && index < None ? (byte)index : None;
}
