using System;
using System.Collections.Generic;
using System.Linq;
using FluidHTN;
using FluidHTN.Contexts;
using FluidHTN.Debug;
using FluidHTN.Factory;
using Gdpyr.HtnBench;
using Gdpyr.Sim;
using Gdpyr.Sim.Htn;
using Xunit;

namespace Gdpyr.Tests;

/// <summary>
/// The FluidHTN behaviours the bot plan is built on — probes P1–P8 of
/// docs/HTN_BOTS.md §3.4 — against the vendored copy in ThirdParty/FluidHTN and the
/// game's <see cref="PooledHtnFactory"/>. A bump of the pinned commit that changes
/// priority, pre-emption, lookahead, partial planning, sharing, determinism or
/// allocation fails here (§3.8).
///
/// The domain is §5.1's sketch (tools/Gdpyr.HtnBench/GroundSketch.cs), the one
/// <c>./scripts/htn-bench.sh --probe</c> runs; these move onto the real domains as
/// H2–H4 write them.
/// </summary>
public class HtnPlannerTests
{
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

	// ---- P1 priority -------------------------------------------------------

	[Fact]
	public void P1_SelectorOrderIsPriority()
	{
		// Visible infantry, an ally near and the denier role: Attack is above Defend
		// Zone, and "press with allies" is above "engage".
		var a = new Agent();
		a.Sense(Fact.Contact, ContactLevel.Visible);
		a.Sense(Fact.ThreatKind, Threat.Infantry);
		a.Sense(Fact.AlliesNear, true);
		a.Sense(Fact.Odds, OddsBand.Even);
		a.Sense(Fact.Role, GroundRole.Denier);
		a.Tick();

		Assert.Equal("[TakeFocusTarget, AdvanceWithBuddy]", a.LastPlan);
		Assert.Equal(GroundGoal.TakeFocusTarget, a.Intent);
	}

	[Fact]
	public void P1_TheNextMethodDownWinsWhenTheOneAboveCannot()
	{
		var a = new Agent();
		a.Sense(Fact.Contact, ContactLevel.Visible);
		a.Sense(Fact.ThreatKind, Threat.Infantry);
		a.Sense(Fact.AlliesNear, false);
		a.Sense(Fact.Role, GroundRole.Denier);
		a.Tick();

		Assert.Equal("[Engage]", a.LastPlan);
	}

	// ---- P2 pre-emption ----------------------------------------------------

	[Fact]
	public void P2_RetreatReplacesARunningAttackTheTickItsFactsAppear()
	{
		var a = new Agent(world: Population.Outcome);
		a.Sense(Fact.Contact, ContactLevel.Visible);
		a.Sense(Fact.ThreatKind, Threat.Infantry);
		a.Sense(Fact.AlliesNear, true);
		a.Tick(10);
		Assert.Equal(GroundGoal.AdvanceWithBuddy, a.Intent);
		Assert.Equal(0, a.Replacements);

		a.Sense(Fact.Health, HealthBand.Critical);
		a.Sense(Fact.Odds, OddsBand.Outnumbered);
		a.Tick();

		Assert.Equal(GroundGoal.FallBack, a.Intent);
		Assert.Equal(1, a.Replacements);
		Assert.Equal("[FallBack]", a.LastPlan);
	}

	// ---- P3 no downgrade ---------------------------------------------------

	[Theory]
	[InlineData(false, GroundGoal.Engage)]
	[InlineData(true, GroundGoal.TakeNode)]
	public void P3_ALowerMethodNeverReplacesARunningOne_OnlyAnExecutingConditionEndsIt(bool executingConditions,
		GroundGoal thirtyTicksLater)
	{
		// Contact lost while engaging, with the denier role set. Defend Zone is below
		// Attack, so the method traversal record will not let it replace Engage: without
		// an executing condition the bot engages nothing for ever (§3.7).
		var a = new Agent(executingConditions);
		a.Sense(Fact.Contact, ContactLevel.Visible);
		a.Sense(Fact.ThreatKind, Threat.Infantry);
		a.Sense(Fact.Role, GroundRole.Denier);
		a.Tick();
		Assert.Equal(GroundGoal.Engage, a.Intent);

		a.Sense(Fact.Contact, ContactLevel.None);
		a.Tick(30);

		Assert.Equal(thirtyTicksLater, a.Intent);
	}

	// ---- P4 lookahead ------------------------------------------------------

	private static Agent FacingArmour(GroundRole role, bool lockerWorks)
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

	[Fact]
	public void P4_APlanOnlyPredictionChainsTheLockerIntoEngagingArmour()
	{
		Agent runner = FacingArmour(GroundRole.LockerRunner, lockerWorks: true);
		runner.Tick();
		Assert.Equal("[PathToLocker, UseLocker, EngageArmour]", runner.LastPlan);

		runner.Tick(10);
		Assert.Equal(GroundGoal.EngageArmour, runner.Intent);
	}

	[Fact]
	public void P4_WithoutTheLockerRole_ARiflemanDoesNotPlanTheTrip()
	{
		Agent assault = FacingArmour(GroundRole.Assault, lockerWorks: true);
		assault.Tick();

		Assert.Equal("[Standoff]", assault.LastPlan);
	}

	[Fact]
	public void P4_APredictionTheWorldNeverConfirms_NeverEngagesUnarmed_AndPlansTheSameTripAgain()
	{
		// The prediction is dropped before execution, so EngageArmour's own condition
		// fails once the locker has not produced an explosive. With nothing else
		// changed the same method wins again, which is why the domain needs a give-up
		// fact (§3.4).
		Agent unconfirmed = FacingArmour(GroundRole.LockerRunner, lockerWorks: false);
		for (int t = 0; t < 30; t++)
		{
			unconfirmed.Tick();
			Assert.NotEqual(GroundGoal.EngageArmour, unconfirmed.Intent);
		}

		Assert.True(unconfirmed.Plans.Count(p => p == "[PathToLocker, UseLocker, EngageArmour]") >= 2,
			string.Join(" then ", unconfirmed.Plans));
	}

	// ---- P5 partial plan ---------------------------------------------------

	private sealed class SquadContext : BaseContext
	{
		private readonly byte[] _facts = new byte[1];

		public SquadContext(IFactory factory = null) => Factory = factory ?? new PooledHtnFactory();

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

		public bool Threatened => GetState(0) == 1;

		public void Sense(bool threatened) => SetState(0, (byte)(threatened ? 1 : 0), true, EffectType.Permanent);
	}

	[Fact]
	public void P5_PausePlanDefersTheRestOfASequenceUntilTheFirstPartHasFinished()
	{
		// A squad stages, then strikes (§5.3), and the strike is planned against the
		// world as it is once the squad has assembled.
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
		Assert.Equal("stage", ctx.Running);
		Assert.Equal(new[] { "[stage]" }, plans);

		ctx.Assembled = true;
		planner.Tick(domain, ctx);
		planner.Tick(domain, ctx);

		Assert.Equal("strike", ctx.Running);
		Assert.Equal(new[] { "[stage]", "[strike]" }, plans);
	}

	// ---- P6 sharing, P7 determinism ----------------------------------------

	/// <summary>
	/// Each agent's facts follow its own pseudo-random schedule (an LCG seeded by its
	/// index, so the schedule itself is reproducible); an agent's trace is a hash of
	/// its intent on every tick.
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

	[Fact]
	public void P6_OneDomainAndOnePlannerForEveryAgent_GiveEachTheTraceItGetsAlone()
	{
		// Selector and Sequence keep a scratch plan queue per instance (§3.7), so this
		// holds only because agents are planned one after another on one thread.
		Assert.Equal(Traces(16, 3000, sharedDomain: false), Traces(16, 3000, sharedDomain: true));
	}

	[Fact]
	public void P7_TheSameFactsGiveTheSameTrace()
	{
		Assert.Equal(Traces(16, 3000, sharedDomain: true), Traces(16, 3000, sharedDomain: true));
	}

	[Fact]
	public void P6_TheTraceIsNotTrivial()
	{
		// Every agent walking the same intents would make P6 and P7 pass vacuously.
		Assert.Equal(16, Traces(16, 3000, sharedDomain: true).Distinct().Count());
	}

	// ---- P8 zero allocation ------------------------------------------------

	/// <summary>
	/// Bytes this thread allocates while <see cref="SimConfig.MaxUnits"/> agents share
	/// one domain and one planner for <paramref name="ticks"/> ticks with facts
	/// changing every 10 ticks (the unit scan), then as many with facts changing every
	/// tick — after a warm-up that fills the pools on every branch of the domain.
	/// </summary>
	private static long AllocatedWhilePlanning(IFactory factory, int ticks, out int dirtied)
	{
		Domain<GroundContext> domain = GroundDomain.Build(factory);
		var planner = new Planner<GroundContext>();
		GroundContext[] agents = Population.Create(SimConfig.MaxUnits, factory);

		for (int t = 0; t < 2000; t++)
		{
			Population.Step(domain, planner, agents, t, 1);
		}

		dirtied = 0;
		long before = GC.GetAllocatedBytesForCurrentThread();
		for (int t = 0; t < ticks; t++)
		{
			dirtied += Population.Step(domain, planner, agents, t, 10);
		}

		for (int t = 0; t < ticks; t++)
		{
			dirtied += Population.Step(domain, planner, agents, t, 1);
		}

		return GC.GetAllocatedBytesForCurrentThread() - before;
	}

	[Fact]
	public void P8_WithThePooledFactory_PlanningAllocatesNothingOnTheTick()
	{
		long bytes = AllocatedWhilePlanning(new PooledHtnFactory(), SimConfig.TickRate * 60, out int dirtied);

		Assert.True(dirtied > 100_000, $"only {dirtied} fact changes dirtied a context");
		Assert.Equal(0, bytes);
	}

	[Fact]
	public void P8_TheMeasurementSeesWhatFluidHtnsOwnFactoryAllocates()
	{
		// The guard above is only worth something if it can fail.
		Assert.True(AllocatedWhilePlanning(new DefaultFactory(), 100, out _) > 0);
	}

	// ---- P8 and partial plans ----------------------------------------------
	//
	// The ground sketch has no PausePlan, so the tests above never reach the one
	// collection FluidHTN borrows on a replan: the paused remainder of a partial
	// plan, set aside in a Queue<PartialPlanEntry> while the planner looks for a
	// better plan. Planner.TryFindNewPlan hands it back only when it finds none; a
	// plan that replaces the paused one drops it, so each such pre-emption allocates
	// even with a pooled factory (docs/HTN_BOTS.md §3.4, D6). These pin both sides.

	private sealed class CountingFactory : IFactory
	{
		private readonly PooledHtnFactory _pooled = new();
		public int QueuesCreated;
		public int QueuesFreed;

		public T[] CreateArray<T>(int length) => _pooled.CreateArray<T>(length);
		public bool FreeArray<T>(ref T[] array) => _pooled.FreeArray(ref array);

		public Queue<T> CreateQueue<T>()
		{
			QueuesCreated++;
			return _pooled.CreateQueue<T>();
		}

		public bool FreeQueue<T>(ref Queue<T> queue)
		{
			QueuesFreed += queue != null ? 1 : 0;
			return _pooled.FreeQueue(ref queue);
		}

		public List<T> CreateList<T>() => _pooled.CreateList<T>();
		public bool FreeList<T>(ref List<T> list) => _pooled.FreeList(ref list);
		public T Create<T>() where T : new() => _pooled.Create<T>();
		public bool Free<T>(ref T obj) => _pooled.Free(ref obj);
	}

	/// <summary>
	/// §5.3's shape: a squad stages, pauses, then strikes; a retreat above it
	/// pre-empts. Staging ends once the squad has assembled, the strike once it
	/// has not (a new round), the retreat once the threat has gone.
	/// </summary>
	private static Domain<SquadContext> StagedAttack(IFactory factory) =>
		new DomainBuilder<SquadContext>("squad", factory)
			.Select("squad")
				.Sequence("retreat")
					.Condition("threatened", c => c.Threatened)
					.Action("fall back").Do(c => c.Threatened ? TaskStatus.Continue : TaskStatus.Success).End()
				.End()
				.Sequence("attack")
					.Action("stage").Do(c => c.Assembled ? TaskStatus.Success : TaskStatus.Continue).End()
					.PausePlan()
					.Action("strike").Do(c => c.Assembled ? TaskStatus.Continue : TaskStatus.Success).End()
				.End()
			.End()
			.Build();

	/// <summary>
	/// Runs <paramref name="rounds"/> rounds of stage → (retreat →) strike after as
	/// many again to warm the pools. Everything it returns is for the measured rounds.
	/// </summary>
	private static long AllocatedOverRounds(CountingFactory factory, bool retreatWhileStaging, int rounds,
		out int preEmptions, out int queuesBorrowed, out int queuesReturned)
	{
		Domain<SquadContext> domain = StagedAttack(factory);
		var planner = new Planner<SquadContext>();
		var ctx = new SquadContext(factory);
		ctx.Init();
		int replacements = 0;
		ctx.PlannerState.OnReplacePlan = (_, _, _) => replacements++;

		void Tick()
		{
			for (int t = 0; t < 5; t++)
			{
				planner.Tick(domain, ctx);
			}
		}

		// Counted rather than asserted inside the rounds, so that nothing but the
		// planner runs between the two allocation readings.
		int roundsNotPaused = 0;
		int roundsNotStriking = 0;

		void Round()
		{
			ctx.Assembled = false;
			Tick();
			roundsNotPaused += ctx.HasPausedPartialPlan ? 0 : 1;

			if (retreatWhileStaging)
			{
				ctx.Sense(threatened: true);
				Tick();
				ctx.Sense(threatened: false);
			}

			ctx.Assembled = true;
			Tick();
			roundsNotStriking += ctx.PlannerState.CurrentTask?.Name == "strike" ? 0 : 1;
		}

		for (int i = 0; i < rounds; i++)
		{
			Round();
		}

		int replacementsBefore = replacements;
		int createdBefore = factory.QueuesCreated;
		int freedBefore = factory.QueuesFreed;
		long bytes = GC.GetAllocatedBytesForCurrentThread();
		for (int i = 0; i < rounds; i++)
		{
			Round();
		}

		bytes = GC.GetAllocatedBytesForCurrentThread() - bytes;
		Assert.Equal(0, roundsNotPaused);
		Assert.Equal(0, roundsNotStriking);
		preEmptions = replacements - replacementsBefore;
		queuesBorrowed = factory.QueuesCreated - createdBefore;
		queuesReturned = factory.QueuesFreed - freedBefore;
		return bytes;
	}

	[Fact]
	public void P8_AStagedPlanThatNothingInterrupts_AllocatesNothing()
	{
		long bytes = AllocatedOverRounds(new CountingFactory(), retreatWhileStaging: false, rounds: 100,
			out int preEmptions, out int borrowed, out _);

		Assert.Equal(0, preEmptions);
		Assert.Equal(0, borrowed);
		Assert.Equal(0, bytes);
	}

	[Fact]
	public void P8_APlanThatReplacesAPausedOne_LeavesThePausedRemaindersQueueUnreturned()
	{
		// FluidHTN at the pinned commit. When this starts failing, the leak has been
		// fixed (upstream or here): flip it to assert 0 bytes and update §3.4 and D6.
		long bytes = AllocatedOverRounds(new CountingFactory(), retreatWhileStaging: true, rounds: 100,
			out int preEmptions, out int borrowed, out int returned);

		Assert.Equal(100, preEmptions);
		Assert.Equal(100, borrowed);
		Assert.Equal(0, returned);
		Assert.True(bytes > 0);
	}
}
