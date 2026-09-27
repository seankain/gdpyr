using System;
using System.Collections.Generic;
using System.Diagnostics;
using FluidHTN;
using FluidHTN.Contexts;
using FluidHTN.Debug;
using FluidHTN.Factory;

namespace Gdpyr.HtnBench;

/// <summary>
/// What FluidHTN costs on the server tick (docs/HTN_BOTS.md §3.2).
///
/// The question the plan has to answer before it vendors a planner is whether the
/// planner breaks the rule every unit path in this codebase keeps — no per-tick
/// allocation (docs/IMPLEMENTATION_PLAN.md §3) — and what a tick of it costs at
/// the unit ceiling. This is a measurement, not a test: the numbers depend on the
/// machine, and the allocation count is the one that must come out at zero with
/// the pooled factory.
///
/// The domain is shaped like the ground bot's in §5.1 (five goals, three levels,
/// about twenty primitives) and shared by every agent, as the real one will be.
/// The operators do nothing but count, so what is measured is the planner and not
/// the game.
/// </summary>
public static class Program
{
	/// <summary>The unit ceiling, SimConfig.MaxUnits: the largest population a planner here runs for.</summary>
	private const int Agents = 64;

	/// <summary>One simulated minute at the server's 60 Hz.</summary>
	private const int Ticks = 60 * 60;

	/// <summary>How often each agent's facts change, staggered: the unit target refresh.</summary>
	private const int SensorIntervalTicks = 10;

	public static int Main(string[] args)
	{
		bool pooled = Array.IndexOf(args, "--default-factory") < 0;
		Console.WriteLine($"factory: {(pooled ? "pooled" : "DefaultFactory")}");

		Domain<BenchContext> domain = GroundDomain.Build();
		var planner = new Planner<BenchContext>();
		var agents = new BenchContext[Agents];
		for (int i = 0; i < agents.Length; i++)
		{
			agents[i] = new BenchContext(pooled ? new PooledFactory() : new DefaultFactory());
			agents[i].Init();
		}

		// Warm the JIT, the pools and every branch of the domain.
		for (int t = 0; t < 2000; t++)
		{
			Step(domain, planner, agents, t, SensorIntervalTicks);
		}

		Measure("steady: facts change every 10 ticks per agent", domain, planner, agents, SensorIntervalTicks);
		Measure("worst:  facts change every tick for every agent", domain, planner, agents, 1);

		return 0;
	}

	private static void Measure(string label, Domain<BenchContext> domain, Planner<BenchContext> planner,
		BenchContext[] agents, int interval)
	{
		// The stopwatch is itself 40 bytes, so it is made before the count starts.
		var clock = new Stopwatch();
		int gen0 = GC.CollectionCount(0);
		long before = GC.GetAllocatedBytesForCurrentThread();
		clock.Start();

		int replans = 0;
		for (int t = 0; t < Ticks; t++)
		{
			replans += Step(domain, planner, agents, t, interval);
		}

		clock.Stop();
		long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
		double perTickMicros = clock.Elapsed.TotalMilliseconds * 1000.0 / Ticks;

		Console.WriteLine(label);
		Console.WriteLine($"  {Agents} agents x {Ticks} ticks, {replans} replans");
		Console.WriteLine($"  {perTickMicros:F1} us per server tick for all agents"
			+ $" ({perTickMicros / 16_667.0 * 100.0:F2}% of a 60 Hz tick)");
		Console.WriteLine($"  {bytes} bytes allocated ({bytes / (double)Ticks:F1} per tick),"
			+ $" {GC.CollectionCount(0) - gen0} gen0 collections");
	}

	/// <summary>
	/// One server tick: agents whose sensors are due get new facts, as a scan would
	/// set them, and every agent's planner ticks.
	/// </summary>
	private static int Step(Domain<BenchContext> domain, Planner<BenchContext> planner, BenchContext[] agents,
		int tick, int interval)
	{
		int replans = 0;
		for (int i = 0; i < agents.Length; i++)
		{
			BenchContext agent = agents[i];
			if ((tick + i) % interval == 0)
			{
				int phase = ((tick + i) / interval) % 6;
				agent.Sense(Fact.EnemyVisible, phase is 1 or 2 or 3);
				agent.Sense(Fact.AlliesNear, phase == 2);
				agent.Sense(Fact.Suppressed, phase == 3);
				agent.Sense(Fact.NeedsExplosive, phase == 4);
				agent.Sense(Fact.OrderedToDefend, phase == 5);
				if (agent.IsDirty)
				{
					replans++;
				}
			}

			planner.Tick(domain, agent);
		}

		return replans;
	}
}

/// <summary>The facts the bench domain plans over. One byte each, as FluidHTN stores them.</summary>
public enum Fact : byte
{
	EnemyVisible,
	AlliesNear,
	Suppressed,
	LowHealth,
	NeedsExplosive,
	OrderedToDefend,
	AtObjective,
	HasCover,
	Count,
}

public sealed class BenchContext : BaseContext
{
	private readonly byte[] _facts = new byte[(int)Fact.Count];

	public BenchContext(IFactory factory) => Factory = factory;

	public override IFactory Factory { get; protected set; }
	public override IPlannerState PlannerState { get; protected set; } = new DefaultPlannerState();
	public override List<string> MTRDebug { get; set; }
	public override List<string> LastMTRDebug { get; set; }
	public override bool DebugMTR => false;
	public override Queue<IBaseDecompositionLogEntry> DecompositionLog { get; set; }
	public override bool LogDecomposition => false;
	public override byte[] WorldState => _facts;

	/// <summary>Operator ticks, so that an action takes a few ticks to finish.</summary>
	public int Work;

	public bool Has(Fact fact) => GetState((int)fact) != 0;

	public void Plan(Fact fact, bool value, EffectType effect) =>
		SetState((int)fact, (byte)(value ? 1 : 0), true, effect);

	public void Sense(Fact fact, bool value) =>
		SetState((int)fact, (byte)(value ? 1 : 0), true, EffectType.Permanent);
}

/// <summary>
/// The five goals of docs/HTN_BOTS.md §5.1, in priority order. Operators finish
/// on every third tick; nothing here is meant to be good play.
/// </summary>
public static class GroundDomain
{
	private static TaskStatus Work(BenchContext c) => ++c.Work % 3 == 0 ? TaskStatus.Success : TaskStatus.Continue;

	public static Domain<BenchContext> Build() => new DomainBuilder<BenchContext>("ground")
		.Select("retreat")
			.Condition("hurt", c => c.Has(Fact.LowHealth))
			.Sequence("fall back to allies")
				.Action("break contact").Do(Work).End()
				.Action("move to allies").Do(Work)
					.Effect("out of sight", EffectType.PlanOnly, (c, e) => c.Plan(Fact.EnemyVisible, false, e)).End()
			.End()
		.End()
		.Select("resupply")
			.Condition("armour known, no explosive", c => c.Has(Fact.NeedsExplosive) && !c.Has(Fact.EnemyVisible))
			.Sequence("swap at locker")
				.Action("path to locker").Do(Work).End()
				.Action("use locker").Do(Work)
					.Effect("armed", EffectType.PlanAndExecute, (c, e) => c.Plan(Fact.NeedsExplosive, false, e)).End()
			.End()
		.End()
		.Select("attack")
			.Condition("contact", c => c.Has(Fact.EnemyVisible))
			.Sequence("suppressed")
				.Condition("suppressed", c => c.Has(Fact.Suppressed))
				.Action("find cover").Do(Work)
					.Effect("cover", EffectType.PlanOnly, (c, e) => c.Plan(Fact.HasCover, true, e)).End()
				.Action("move to cover").Do(Work).End()
				.Action("return fire").Do(Work).End()
			.End()
			.Sequence("with allies")
				.Condition("allies near", c => c.Has(Fact.AlliesNear))
				.Action("take assigned target").Do(Work).End()
				.Action("advance with buddy").Do(Work).End()
				.Action("engage").Do(Work).End()
			.End()
			.Sequence("alone")
				.Action("hold range").Do(Work).End()
				.Action("engage").Do(Work).End()
			.End()
		.End()
		.Select("defend zone")
			.Condition("ordered to defend", c => c.Has(Fact.OrderedToDefend))
			.Sequence("hold")
				.Condition("there", c => c.Has(Fact.AtObjective))
				.Action("take post").Do(Work).End()
				.Action("watch sector").Do(Work).End()
			.End()
			.Sequence("go")
				.Action("path to zone").Do(Work)
					.Effect("arrive", EffectType.PlanOnly, (c, e) => c.Plan(Fact.AtObjective, true, e)).End()
				.Action("take post").Do(Work).End()
				.Action("watch sector").Do(Work).End()
			.End()
		.End()
		.Select("recon")
			.Sequence("sweep")
				.Action("pick stalest area").Do(Work).End()
				.Action("path to area").Do(Work).End()
				.Action("look around").Do(Work).End()
			.End()
		.End()
		.Build();
}

/// <summary>
/// FluidHTN hands every array and queue it borrows back through <c>Free*</c>
/// (its README calls the factory "used internally with the support of pooling in
/// mind"), so a free list per element type and length is all it takes to plan
/// without allocating. Single-threaded, as the server tick is.
/// </summary>
public sealed class PooledFactory : IFactory
{
	private static class Arrays<T>
	{
		public static readonly Dictionary<int, Stack<T[]>> ByLength = new();
	}

	private static class Queues<T>
	{
		public static readonly Stack<Queue<T>> Free = new();
	}

	private static class Lists<T>
	{
		public static readonly Stack<List<T>> Free = new();
	}

	public T[] CreateArray<T>(int length) =>
		Arrays<T>.ByLength.TryGetValue(length, out Stack<T[]> free) && free.Count > 0 ? free.Pop() : new T[length];

	public bool FreeArray<T>(ref T[] array)
	{
		if (array != null)
		{
			if (!Arrays<T>.ByLength.TryGetValue(array.Length, out Stack<T[]> free))
			{
				Arrays<T>.ByLength[array.Length] = free = new Stack<T[]>();
			}

			free.Push(array);
			array = null;
		}

		return true;
	}

	public Queue<T> CreateQueue<T>() => Queues<T>.Free.Count > 0 ? Queues<T>.Free.Pop() : new Queue<T>();

	public bool FreeQueue<T>(ref Queue<T> queue)
	{
		if (queue != null)
		{
			queue.Clear();
			Queues<T>.Free.Push(queue);
			queue = null;
		}

		return true;
	}

	public List<T> CreateList<T>() => Lists<T>.Free.Count > 0 ? Lists<T>.Free.Pop() : new List<T>();

	public bool FreeList<T>(ref List<T> list)
	{
		if (list != null)
		{
			list.Clear();
			Lists<T>.Free.Push(list);
			list = null;
		}

		return true;
	}

	public T Create<T>() where T : new() => new();

	public bool Free<T>(ref T obj)
	{
		obj = default;
		return true;
	}
}
