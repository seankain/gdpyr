using System;
using System.Diagnostics;
using FluidHTN;
using FluidHTN.Factory;
using Gdpyr.Sim.Htn;

namespace Gdpyr.HtnBench;

/// <summary>
/// What FluidHTN does and costs on the server tick (docs/HTN_BOTS.md §3).
///
///   gdpyr-htn-bench                     cost: the §5.1 domain, 64 agents, pooled factory
///   gdpyr-htn-bench --default-factory   the same with FluidHTN's own factory
///   gdpyr-htn-bench --probe             the planner behaviours the plan relies on; exit 1 on any failure
///
/// The cost half answers whether the planner breaks the rule every unit path in
/// this codebase keeps — no per-tick allocation (docs/IMPLEMENTATION_PLAN.md §3) —
/// and what a tick of it costs at the unit ceiling. Its times depend on the
/// machine; its byte counts do not.
/// </summary>
public static class Program
{
	/// <summary>The unit ceiling, SimConfig.MaxUnits: the largest population a planner here runs for.</summary>
	public const int Agents = 64;

	/// <summary>One simulated minute at the server's 60 Hz.</summary>
	public const int Ticks = 60 * 60;

	/// <summary>How often each agent's facts change, staggered: the unit target refresh.</summary>
	public const int SensorIntervalTicks = 10;

	public static int Main(string[] args)
	{
		if (Array.IndexOf(args, "--probe") >= 0)
		{
			return Probes.Run();
		}

		bool pooled = Array.IndexOf(args, "--default-factory") < 0;
		Console.WriteLine($"factory: {(pooled ? "pooled" : "DefaultFactory")}, domain: docs/HTN_BOTS.md §5.1 (GroundSketch.cs)");

		IFactory factory = pooled ? new PooledHtnFactory() : new DefaultFactory();
		Domain<GroundContext> domain = GroundDomain.Build(factory);
		var planner = new Planner<GroundContext>();
		GroundContext[] agents = Population.Create(Agents, factory);

		// Warm the pools and every branch of the domain.
		for (int t = 0; t < 2000; t++)
		{
			Population.Step(domain, planner, agents, t, SensorIntervalTicks);
		}

		Measure("steady: facts change every 10 ticks per agent", domain, planner, agents, SensorIntervalTicks);
		Measure("worst:  facts change every tick for every agent", domain, planner, agents, 1);

		return 0;
	}

	private static void Measure(string label, Domain<GroundContext> domain, Planner<GroundContext> planner,
		GroundContext[] agents, int interval)
	{
		// The stopwatch is itself 40 bytes, so it is made before the count starts.
		var clock = new Stopwatch();
		int gen0 = GC.CollectionCount(0);
		long before = GC.GetAllocatedBytesForCurrentThread();
		clock.Start();

		int replans = 0;
		for (int t = 0; t < Ticks; t++)
		{
			replans += Population.Step(domain, planner, agents, t, interval);
		}

		clock.Stop();
		long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
		double perTickMicros = clock.Elapsed.TotalMilliseconds * 1000.0 / Ticks;

		Console.WriteLine(label);
		Console.WriteLine($"  {agents.Length} agents x {Ticks} ticks, {replans} fact changes that dirtied a context");
		Console.WriteLine($"  {perTickMicros:F1} us per server tick for all agents"
			+ $" ({perTickMicros / 16_667.0 * 100.0:F2}% of a 60 Hz tick)");
		Console.WriteLine($"  {bytes} bytes allocated ({bytes / (double)Ticks:F1} per tick),"
			+ $" {GC.CollectionCount(0) - gen0} gen0 collections");
	}
}
