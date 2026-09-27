using System;
using System.Collections.Generic;
using System.Linq;
using FluidHTN;
using FluidHTN.Contexts;
using FluidHTN.Debug;
using FluidHTN.Factory;
using Gdpyr.Sim.Htn;

namespace Gdpyr.HtnBench;

/// <summary>
/// The planner behaviours docs/HTN_BOTS.md relies on, each checked against running
/// FluidHTN code rather than taken from its README (§3.4). A probe that fails means
/// the plan's reading of the library is wrong, and the plan has to change with it.
/// </summary>
public static class Probes
{
	private static int _failures;

	public static int Run()
	{
		Check("P1 priority", Priority);
		Check("P2 pre-emption", PreEmption);
		Check("P3 no downgrade without an executing condition", NoDowngrade);
		Check("P4 lookahead through a PlanOnly effect", Lookahead);
		Check("P5 partial plan (PausePlan)", PartialPlan);
		Check("P6 one domain and one planner shared by every agent", SharedDomain);
		Check("P7 deterministic", Deterministic);
		Check("P8 zero allocation with a pooled factory", ZeroAllocation);
		Check("P9 memory", Memory);

		Console.WriteLine(_failures == 0 ? "all probes passed" : $"{_failures} probe(s) FAILED");
		return _failures == 0 ? 0 : 1;
	}

	private static void Check(string name, Func<string> probe)
	{
		string failure;
		try
		{
			failure = probe();
		}
		catch (Exception e)
		{
			failure = $"threw {e.GetType().Name}: {e.Message}";
		}

		if (failure == null || failure.StartsWith("info:", StringComparison.Ordinal))
		{
			Console.WriteLine($"PASS {name}{(failure != null ? $" — {failure[5..].Trim()}" : string.Empty)}");
			return;
		}

		_failures++;
		Console.WriteLine($"FAIL {name} — {failure}");
	}

	// ---- helpers ------------------------------------------------------------

	private sealed class Agent
	{
		public readonly Domain<GroundContext> Domain;
		public readonly Planner<GroundContext> Planner = new();
		public readonly GroundContext Context;
		public readonly List<string> Plans = new();
		public int Replacements;

		public Agent(bool executingConditions = true, Func<GroundContext, GroundGoal, TaskStatus> world = null)
		{
			IFactory factory = new PooledHtnFactory();
			Domain = GroundDomain.Build(factory, executingConditions);
			Context = new GroundContext(factory) { World = world };
			Context.Init();
			Context.PlannerState.OnNewPlan = plan => Plans.Add(Names(plan));
			Context.PlannerState.OnReplacePlan = (_, _, plan) =>
			{
				Replacements++;
				Plans.Add(Names(plan));
			};
		}

		public void Sense(Fact fact, Enum value) => Context.Sense(fact, Convert.ToByte(value));

		public void Sense(Fact fact, bool value) => Context.Sense(fact, (byte)(value ? 1 : 0));

		public void Tick(int times = 1)
		{
			for (int i = 0; i < times; i++)
			{
				Planner.Tick(Domain, Context);
			}
		}

		public GroundGoal Intent => Context.Intent;

		public string LastPlan => Plans.Count > 0 ? Plans[^1] : "(none)";
	}

	private static string Names(Queue<ITask> plan) => "[" + string.Join(", ", plan.Select(t => t.Name)) + "]";

	private static TaskStatus WalksFinish(GroundContext c, GroundGoal goal) => Population.Outcome(c, goal);

	// ---- probes -------------------------------------------------------------

	/// <summary>Attack is above Defend Zone, and "press with allies" above "engage".</summary>
	private static string Priority()
	{
		var a = new Agent();
		a.Sense(Fact.Contact, ContactLevel.Visible);
		a.Sense(Fact.ThreatKind, Threat.Infantry);
		a.Sense(Fact.AlliesNear, true);
		a.Sense(Fact.Odds, OddsBand.Even);
		a.Sense(Fact.Role, GroundRole.Denier);
		a.Tick();

		return a.LastPlan == "[TakeFocusTarget, AdvanceWithBuddy]" && a.Intent == GroundGoal.TakeFocusTarget
			? null
			: $"plan {a.LastPlan}, intent {a.Intent}";
	}

	/// <summary>Retreat replaces a running Attack plan the tick its facts appear.</summary>
	private static string PreEmption()
	{
		var a = new Agent(world: WalksFinish);
		a.Sense(Fact.Contact, ContactLevel.Visible);
		a.Sense(Fact.ThreatKind, Threat.Infantry);
		a.Sense(Fact.AlliesNear, true);
		a.Tick(10);
		if (a.Intent != GroundGoal.AdvanceWithBuddy)
		{
			return $"set-up: expected AdvanceWithBuddy running, got {a.Intent}";
		}

		a.Sense(Fact.Health, HealthBand.Critical);
		a.Sense(Fact.Odds, OddsBand.Outnumbered);
		a.Tick();

		return a.Intent == GroundGoal.FallBack && a.Replacements == 1
			? $"info: AdvanceWithBuddy -> {a.LastPlan} in 1 tick"
			: $"intent {a.Intent}, replacements {a.Replacements}, plan {a.LastPlan}";
	}

	/// <summary>
	/// A running higher-priority task is not replaced by a lower-priority method when a
	/// fact makes the lower one valid (MTR). Without an executing condition the bot keeps
	/// engaging a contact that has gone; with one, it moves on the next tick.
	/// </summary>
	private static string NoDowngrade()
	{
		GroundGoal Run(bool executingConditions)
		{
			var a = new Agent(executingConditions);
			a.Sense(Fact.Contact, ContactLevel.Visible);
			a.Sense(Fact.ThreatKind, Threat.Infantry);
			a.Sense(Fact.Role, GroundRole.Denier);
			a.Tick();
			a.Sense(Fact.Contact, ContactLevel.None);
			a.Tick(30);
			return a.Intent;
		}

		GroundGoal without = Run(executingConditions: false);
		GroundGoal with = Run(executingConditions: true);

		return without == GroundGoal.Engage && with == GroundGoal.TakeNode
			? $"info: 30 ticks after the contact went — without: {without}, with: {with}"
			: $"without executing conditions: {without} (expected Engage); with: {with} (expected TakeNode)";
	}

	/// <summary>
	/// "EngageArmour" requires an explosive in hand. The locker branch is plannable only
	/// because UseLocker predicts one; a PlanOnly prediction is dropped before execution,
	/// so if the scan does not confirm it the plan fails rather than engaging a tank
	/// with a rifle — and, with nothing else changed, the same trip is planned again,
	/// which is why the domain needs a give-up fact (docs/HTN_BOTS.md §3.4).
	/// </summary>
	private static string Lookahead()
	{
		Agent Armour(GroundRole role, bool lockerWorks)
		{
			var a = new Agent(world: (c, goal) =>
			{
				TaskStatus status = Population.Outcome(c, goal);
				if (goal == GroundGoal.UseLocker && status == TaskStatus.Success && lockerWorks)
				{
					c.Sense(Fact.Armed, (byte)Arms.Explosive);
				}

				return status;
			});
			a.Sense(Fact.Contact, ContactLevel.Visible);
			a.Sense(Fact.ThreatKind, Threat.Armour);
			a.Sense(Fact.Armed, Arms.SmallArms);
			a.Sense(Fact.Role, role);
			return a;
		}

		Agent runner = Armour(GroundRole.LockerRunner, lockerWorks: true);
		runner.Tick();
		string first = runner.LastPlan;
		runner.Tick(10);

		Agent assault = Armour(GroundRole.Assault, lockerWorks: true);
		assault.Tick();

		Agent unconfirmed = Armour(GroundRole.LockerRunner, lockerWorks: false);
		unconfirmed.Tick(10);
		bool engagedUnarmed = unconfirmed.Plans.Count > 0 && unconfirmed.Intent == GroundGoal.EngageArmour;

		if (first != "[PathToLocker, UseLocker, EngageArmour]")
		{
			return $"runner planned {first}";
		}

		if (runner.Intent != GroundGoal.EngageArmour)
		{
			return $"runner ended on {runner.Intent}, expected EngageArmour";
		}

		if (assault.LastPlan != "[Standoff]")
		{
			return $"a rifleman with no locker role planned {assault.LastPlan}, expected [Standoff]";
		}

		if (engagedUnarmed)
		{
			return "engaged armour although the locker never produced an explosive";
		}

		return $"info: runner {first}; locker unconfirmed -> replanned to {unconfirmed.LastPlan}, intent {unconfirmed.Intent}";
	}

	private sealed class SquadContext : BaseContext
	{
		private readonly byte[] _facts = new byte[1];

		public SquadContext() => Factory = new PooledHtnFactory();

		public override IFactory Factory { get; protected set; }
		public override IPlannerState PlannerState { get; protected set; } = new DefaultPlannerState();
		public override List<string> MTRDebug { get; set; }
		public override List<string> LastMTRDebug { get; set; }
		public override bool DebugMTR => false;
		public override Queue<IBaseDecompositionLogEntry> DecompositionLog { get; set; }
		public override bool LogDecomposition => false;
		public override byte[] WorldState => _facts;

		public bool Assembled;
		public string Running;
	}

	/// <summary>
	/// A squad stages, then strikes (§5.3). With a pause between the two, the strike is
	/// planned only once staging has finished, against the world as it is then.
	/// </summary>
	private static string PartialPlan()
	{
		Domain<SquadContext> domain = new DomainBuilder<SquadContext>("squad", new PooledHtnFactory())
			.Sequence("attack")
				.Action("stage").Do(c =>
				{
					c.Running = "stage";
					return c.Assembled ? TaskStatus.Success : TaskStatus.Continue;
				}).End()
				.PausePlan()
				.Action("strike").Do(c =>
				{
					c.Running = "strike";
					return TaskStatus.Continue;
				}).End()
			.End()
			.Build();

		var ctx = new SquadContext();
		ctx.Init();
		var plans = new List<string>();
		ctx.PlannerState.OnNewPlan = plan => plans.Add(Names(plan));
		var planner = new Planner<SquadContext>();

		planner.Tick(domain, ctx);
		planner.Tick(domain, ctx);
		string whileStaging = ctx.Running;
		ctx.Assembled = true;
		planner.Tick(domain, ctx);
		planner.Tick(domain, ctx);

		string trace = string.Join(" then ", plans);
		return plans.Count == 2 && plans[0] == "[stage]" && plans[1] == "[strike]" && whileStaging == "stage"
			&& ctx.Running == "strike"
			? $"info: {trace}"
			: $"plans {trace}, running {ctx.Running}";
	}

	/// <summary>
	/// One domain and one planner for every agent gives each agent exactly the trace it
	/// gets with its own. FluidHTN keeps a scratch queue on each Selector and Sequence
	/// (§3.3), so this holds only because agents are planned one after another.
	/// </summary>
	private static string SharedDomain()
	{
		const int agents = 16;
		const int ticks = 3000;

		ulong[] shared = Traces(agents, ticks, sharedDomain: true);
		ulong[] alone = Traces(agents, ticks, sharedDomain: false);

		for (int i = 0; i < agents; i++)
		{
			if (shared[i] != alone[i])
			{
				return $"agent {i}: shared trace {shared[i]:x16} != own-domain trace {alone[i]:x16}";
			}
		}

		return $"info: {agents} agents x {ticks} ticks, traces identical";
	}

	/// <summary>The same facts give the same task trace, run to run.</summary>
	private static string Deterministic()
	{
		ulong[] first = Traces(16, 3000, sharedDomain: true);
		ulong[] second = Traces(16, 3000, sharedDomain: true);
		return first.SequenceEqual(second) ? null : "two runs of the same fact schedule diverged";
	}

	/// <summary>
	/// Each agent's facts follow its own pseudo-random schedule (an LCG seeded by its
	/// index, so the schedule itself is reproducible); the trace is a hash of the
	/// intent on every tick.
	/// </summary>
	private static ulong[] Traces(int agents, int ticks, bool sharedDomain)
	{
		IFactory factory = new PooledHtnFactory();
		Domain<GroundContext> one = GroundDomain.Build(factory);
		var planner = new Planner<GroundContext>();

		var domains = new Domain<GroundContext>[agents];
		var planners = new Planner<GroundContext>[agents];
		GroundContext[] contexts = Population.Create(agents, factory);
		var seeds = new uint[agents];
		var hashes = new ulong[agents];

		for (int i = 0; i < agents; i++)
		{
			domains[i] = sharedDomain ? one : GroundDomain.Build(factory);
			planners[i] = sharedDomain ? planner : new Planner<GroundContext>();
			seeds[i] = (uint)(i * 2654435761u + 1u);
			hashes[i] = 14695981039346656037ul;
		}

		for (int t = 0; t < ticks; t++)
		{
			for (int i = 0; i < agents; i++)
			{
				seeds[i] = (seeds[i] * 1664525u) + 1013904223u;
				if ((seeds[i] >> 28) == 0)
				{
					Population.Situation(contexts[i], (int)((seeds[i] >> 8) % 8));
				}

				planners[i].Tick(domains[i], contexts[i]);
				hashes[i] = (hashes[i] ^ (byte)contexts[i].Intent) * 1099511628211ul;
			}
		}

		return hashes;
	}

	private static string ZeroAllocation()
	{
		IFactory factory = new PooledHtnFactory();
		Domain<GroundContext> domain = GroundDomain.Build(factory);
		var planner = new Planner<GroundContext>();
		GroundContext[] agents = Population.Create(Program.Agents, factory);

		for (int t = 0; t < 2000; t++)
		{
			Population.Step(domain, planner, agents, t, 1);
		}

		long before = GC.GetAllocatedBytesForCurrentThread();
		int dirtied = 0;
		for (int t = 0; t < Program.Ticks; t++)
		{
			dirtied += Population.Step(domain, planner, agents, t, Program.SensorIntervalTicks);
		}

		for (int t = 0; t < Program.Ticks; t++)
		{
			dirtied += Population.Step(domain, planner, agents, t, 1);
		}

		long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
		return bytes == 0
			? $"info: {Program.Agents} agents, {2 * Program.Ticks} ticks, {dirtied} dirtying fact changes, 0 bytes"
			: $"{bytes} bytes allocated over {2 * Program.Ticks} ticks";
	}

	/// <summary>What a domain and one agent cost to create. Reported, not judged.</summary>
	private static string Memory()
	{
		IFactory factory = new PooledHtnFactory();

		long before = GC.GetAllocatedBytesForCurrentThread();
		GroundDomain.Build(factory);
		long domainBytes = GC.GetAllocatedBytesForCurrentThread() - before;

		before = GC.GetAllocatedBytesForCurrentThread();
		var ctx = new GroundContext(factory);
		ctx.Init();
		long contextBytes = GC.GetAllocatedBytesForCurrentThread() - before;

		return $"info: building the §5.1 domain allocates {domainBytes} B once; one agent's context {contextBytes} B"
			+ " before its first plan";
	}
}
