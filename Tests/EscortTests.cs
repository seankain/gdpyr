using System;
using System.Collections.Generic;
using System.Linq;
using FluidHTN;
using Gdpyr.Sim;
using Gdpyr.Sim.Htn;
using Godot;
using Xunit;

namespace Gdpyr.Tests;

/// <summary>
/// Escorting an exposed builder (docs/TACTICS.md §5.10, T9): which builders are exposed,
/// which units escort which, where an escort with no wall stands; and the unit's half — a
/// rifleman or a heavy gunner holding ground goes to its spot by the builder firing, stands
/// in it, and fires back at where the builder's fire came from; only with micro-autonomy on,
/// never under an order that goes somewhere, never in a squad falling back; pre-empting its
/// post, pre-empted by Retreat and by reacting to fire on itself, and ending when its duty does.
/// </summary>
public class EscortTests
{
	private static readonly UnitTraits Rifleman = new(45f, 40f, 1.5f, 20f, UnitDoctrine.Rifleman);
	private static readonly UnitTraits Gunner = new(45f, 45f, 1.5f, 15f, UnitDoctrine.Gunner);
	private static readonly UnitTraits Sniper = new(70f, 70f, 1.5f, 20f, UnitDoctrine.Marksman);
	private static readonly UnitTraits Builder = new(35f, 20f, 1.5f, 15f, UnitDoctrine.None);

	private static readonly Vector3 Anchor = new(10f, 0.5f, 10f);
	private static readonly Vector3 Objective = new(10f, 0.5f, 110f);

	/// <summary>Where the escort guards the builder from: a wall eight metres off its post.</summary>
	private static readonly Vector3 Spot = Anchor + new Vector3(6f, 0f, 6f);

	/// <summary>Where the builder reckons its fire came from: 30 m off it, at a standing eye.</summary>
	private static readonly Vector3 BuilderShooter = Anchor + new Vector3(36f, 1.5f, 6f);

	/// <summary>Where fire at the escort itself comes from, from another side.</summary>
	private static readonly Vector3 OwnShooter = Anchor + new Vector3(-30f, 1.5f, 0f);

	private const int Second = SimConfig.TickRate;

	private sealed class Rig
	{
		public readonly Domain<UnitContext> Domain;
		public readonly Planner<UnitContext> Planner = new();
		public readonly UnitContext C;
		public readonly List<string> Plans = new();

		public MicroAutonomy Autonomy = MicroAutonomy.On;
		public FireMemory Fire;
		public bool HasTarget;
		public Vector3 TargetPoint;

		public Rig(UnitTraits? traits = null, bool executingConditions = true)
		{
			var factory = new PooledHtnFactory();
			Domain = UnitDomain.Build(factory, executingConditions);
			C = new UnitContext(factory, UnitPlanTraits.Default, traits ?? Rifleman);
			C.Init();
			C.PlannerState.OnNewPlan = plan => Plans.Add(Names(plan));
			C.PlannerState.OnReplacePlan = (_, _, plan) => Plans.Add(Names(plan));

			C.Tick = 1000;
			C.Position = Anchor;
			C.FallbackPoint = new Vector3(-40f, 0.5f, -40f);
			C.HasPost = true;
			C.PostPoint = Anchor;
		}

		public void Order(OrderKind kind, Vector3 target) => C.Track(C.Position, new UnitOrder
		{
			Kind = kind,
			Target = target,
			Anchor = kind == OrderKind.Defend ? target : C.Position,
		}, HasTarget, TargetPoint);

		/// <summary>
		/// One scan's escort duty: a spot to guard the builder from at <paramref name="spot"/>, or
		/// none; whether a box there hides it; whether its fire has a line; the tick of the
		/// latest round at the builder.
		/// </summary>
		public void Escort(Vector3? spot, bool covered = true, bool fires = false, uint firedTick = 0) =>
			C.SenseGuard(spot.HasValue, spot ?? Vector3.Zero, covered, fires, BuilderShooter, firedTick);

		public void Scan(bool autonomous = false, OddsBand odds = OddsBand.Even, float health = 1f,
			SquadPhase phase = SquadPhase.Moving, Vector3? fireCover = null)
		{
			C.HasFireCoverPoint = fireCover.HasValue;
			C.FireCoverPoint = fireCover ?? Vector3.Zero;
			C.Encode(new UnitSense
			{
				HealthFraction = health,
				Autonomous = autonomous,
				Odds = odds,
				HasSquad = phase != SquadPhase.Moving,
				SquadPhase = phase,
				HasFireCover = fireCover.HasValue,
				FireCoverHides = fireCover.HasValue,
			});
		}

		/// <summary>A round at the escort itself, from <see cref="OwnShooter"/>, on this tick.</summary>
		public void Round() => FiredUpon.Record(ref Fire, FireKind.NearMiss, OwnShooter, OwnerId.ForPeer(9), C.Tick);

		public void Tick(int times = 1)
		{
			for (int i = 0; i < times; i++)
			{
				C.Tick++;
				C.SenseAutonomy(Autonomy);
				C.SenseFire(FiredUpon.KindAt(Fire, C.Tick), Fire.Origin, Fire.LastTick, Fire.LastHitTick);
				C.Track(C.Position, C.Order, HasTarget, TargetPoint);
				Planner.Tick(Domain, C);
			}
		}

		public UnitGoal Goal => C.Intent.Goal;
	}

	private static string Names(Queue<ITask> plan) => "[" + string.Join(", ", plan.Select(t => t.Name)) + "]";

	/// <summary>A defender on its post, then given a builder to escort.</summary>
	private static Rig Escorting(UnitTraits? traits = null, bool covered = true, bool fires = false,
		bool executingConditions = true)
	{
		var unit = new Rig(traits, executingConditions);
		unit.Order(OrderKind.Defend, Anchor);
		unit.Scan();
		unit.Tick();
		unit.Escort(Spot, covered, fires, unit.C.Tick);
		unit.Tick();
		return unit;
	}

	// ---- the unit's half ------------------------------------------------------

	[Fact]
	public void AnEscortHoldingGround_GoesToItsSpotByTheBuilder_FiringOnTheWay_ThenStandsInIt()
	{
		Rig unit = Escorting();

		Assert.Equal("[GuardBuilder]", unit.Plans[^1]);
		Assert.Equal(UnitMove.Point, unit.C.Intent.Move);
		Assert.Equal(Spot, unit.C.Intent.Point);
		Assert.False(unit.C.Intent.StopToFight);
		Assert.Equal((byte)CoverState.Near, unit.C.Get(UnitFact.Guard));

		unit.C.Position = Spot + new Vector3(0.4f, 0f, 0f);
		unit.Tick();

		Assert.Equal(UnitGoal.GuardBuilder, unit.Goal);
		Assert.Equal((byte)CoverState.In, unit.C.Get(UnitFact.Guard));
		Assert.Equal(UnitMove.Hold, unit.C.Intent.Move);
		Assert.True(unit.C.Intent.StopToFight);
		Assert.False(unit.C.Intent.Crouch);
	}

	[Fact]
	public void ItTakesOverFromItsPost_AndGoesBackToItWhenTheBuilderIsNoLongerExposed()
	{
		var unit = new Rig();
		unit.Order(OrderKind.Defend, Anchor);
		unit.Scan();
		unit.Tick();
		Assert.Equal(UnitGoal.HoldPost, unit.Goal);

		unit.Escort(Spot);
		unit.Tick();
		Assert.Equal(UnitGoal.GuardBuilder, unit.Goal);

		unit.Escort(null);
		unit.Tick();
		Assert.Equal(UnitGoal.HoldPost, unit.Goal);
	}

	[Fact]
	public void UnderNoOrder_ItEscortsToo()
	{
		var unit = new Rig();
		unit.Order(OrderKind.None, Anchor);
		unit.Scan();
		unit.Escort(Spot);
		unit.Tick();

		Assert.Equal(UnitGoal.GuardBuilder, unit.Goal);
	}

	[Fact]
	public void ItCoversTheBuilder_FiringAtWhereItsFireCameFrom_ForTwoSecondsAfterEachRound()
	{
		Rig unit = Escorting(fires: true);
		Assert.True(unit.C.Intent.FireAt);
		Assert.Equal(BuilderShooter, unit.C.Intent.FirePoint);

		unit.C.Position = Spot;
		unit.Tick((2 * Second) - 2);
		Assert.Equal(UnitGoal.GuardBuilder, unit.Goal);
		Assert.True(unit.C.Intent.FireAt);

		unit.Tick(3);
		Assert.Equal(UnitGoal.GuardBuilder, unit.Goal);
		Assert.False(unit.C.Intent.FireAt);

		// The next round at the builder, on the next scan: covering fire again.
		unit.Escort(Spot, covered: true, fires: true, unit.C.Tick);
		unit.Tick();
		Assert.True(unit.C.Intent.FireAt);
	}

	[Fact]
	public void WithNoLineOrNoReachToWhereTheFireCameFrom_ItDoesNotFireAtThePlace()
	{
		Rig unit = Escorting(fires: false);

		Assert.Equal(UnitGoal.GuardBuilder, unit.Goal);
		Assert.False(unit.C.Intent.FireAt);
	}

	[Fact]
	public void WithATargetOfItsOwn_ItFightsItFromItsSpot()
	{
		Rig unit = Escorting();
		unit.HasTarget = true;
		unit.TargetPoint = Anchor + new Vector3(20f, 0.5f, 6f);
		unit.C.Position = Spot;
		unit.Tick();

		Assert.Equal(UnitGoal.GuardBuilder, unit.Goal);
		Assert.Equal(UnitMove.Hold, unit.C.Intent.Move);
		Assert.True(unit.C.Intent.StopToFight);
	}

	[Theory]
	[InlineData(OrderKind.Move, UnitGoal.Obey)]
	[InlineData(OrderKind.Attack, UnitGoal.Advance)]
	[InlineData(OrderKind.Patrol, UnitGoal.Patrol)]
	public void UnderAnOrderThatGoesSomewhere_ItCarriesTheOrderOut(OrderKind order, UnitGoal goal)
	{
		var unit = new Rig();
		unit.Order(order, Objective);
		unit.Scan();
		unit.Escort(Spot);
		unit.Tick();

		Assert.Equal(goal, unit.Goal);
	}

	[Fact]
	public void ANewOrderThatGoesSomewhere_EndsTheEscortOnTheTick()
	{
		Rig unit = Escorting();
		Assert.Equal(UnitGoal.GuardBuilder, unit.Goal);

		unit.Order(OrderKind.Move, Objective);
		unit.Tick();

		Assert.Equal(UnitGoal.Obey, unit.Goal);
	}

	[Fact]
	public void WithMicroAutonomyOff_ItHoldsItsPost_AndSwitchingItOffEndsTheEscortOnTheTick()
	{
		var off = new Rig { Autonomy = MicroAutonomy.Off };
		off.Order(OrderKind.Defend, Anchor);
		off.Scan();
		off.Escort(Spot);
		off.Tick();
		Assert.Equal(UnitGoal.HoldPost, off.Goal);

		Rig unit = Escorting();
		Assert.Equal(UnitGoal.GuardBuilder, unit.Goal);
		unit.Autonomy = MicroAutonomy.Off;
		unit.Tick();
		Assert.Equal(UnitGoal.HoldPost, unit.Goal);

		unit.Autonomy = MicroAutonomy.On;
		unit.Tick();
		Assert.Equal(UnitGoal.GuardBuilder, unit.Goal);
	}

	public static IEnumerable<object[]> Doctrines()
	{
		yield return new object[] { "rifleman", true };
		yield return new object[] { "gunner", true };
		yield return new object[] { "marksman", false };
		yield return new object[] { "builder", false };
	}

	[Theory]
	[MemberData(nameof(Doctrines))]
	public void ARiflemanAndAHeavyGunnerEscort_AMarksmanAndABuilderDoNot(string kind, bool escorts)
	{
		UnitTraits traits = kind switch
		{
			"gunner" => Gunner,
			"marksman" => Sniper,
			"builder" => Builder,
			_ => Rifleman,
		};
		Rig unit = Escorting(traits);

		Assert.Equal(escorts, unit.Goal == UnitGoal.GuardBuilder);
		Assert.Equal(escorts, UnitContext.Escorts(traits.Doctrine));
	}

	[Fact]
	public void ASquadFallingBack_DoesNotEscort()
	{
		var unit = new Rig();
		unit.Order(OrderKind.Defend, Anchor);
		unit.Scan(phase: SquadPhase.FallingBack);
		unit.Escort(Spot);
		unit.Tick(3);

		// Not planned at all, rather than planned and aborted on the tick, every tick.
		Assert.DoesNotContain("[GuardBuilder]", unit.Plans);
		Assert.Equal(UnitGoal.HoldPost, unit.Goal);

		Rig escort = Escorting();
		escort.Scan(phase: SquadPhase.FallingBack);
		escort.Tick();
		Assert.Equal(UnitGoal.HoldPost, escort.Goal);
	}

	[Fact]
	public void Retreat_ReplacesTheEscort()
	{
		Rig unit = Escorting();
		unit.Scan(autonomous: true, odds: OddsBand.Outnumbered, health: 0.2f);
		unit.Tick();

		Assert.Equal(UnitGoal.Retreat, unit.Goal);
	}

	[Fact]
	public void FireAtTheEscortItself_IsAnsweredFirst_ThenItGoesBackToTheBuilder()
	{
		Rig unit = Escorting();
		Vector3 cover = Anchor + new Vector3(2f, 0f, 0f);
		unit.Round();
		unit.Scan(fireCover: cover);
		unit.Tick();

		Assert.Equal(UnitGoal.TakeCoverFromFire, unit.Goal);
		Assert.Equal(cover, unit.C.Intent.Point);

		// The window closes three seconds after the round: back to the builder.
		unit.Tick(FiredUpon.WindowTicks + 1);
		Assert.Equal(UnitGoal.GuardBuilder, unit.Goal);
	}

	[Fact]
	public void AWalkThatDoesNotArrive_CoversTheBuilderFromWhereItStands()
	{
		Rig unit = Escorting();
		Assert.Equal(UnitMove.Point, unit.C.Intent.Move);

		unit.Tick(UnitPlanTraits.Default.GuardWalkMaxTicks - 2);
		Assert.Equal(UnitMove.Point, unit.C.Intent.Move);

		unit.Tick(3);
		Assert.Equal(UnitGoal.GuardBuilder, unit.Goal);
		Assert.Equal(UnitMove.Hold, unit.C.Intent.Move);
	}

	[Fact]
	public void InASpotBehindABox_ItCrouchesToReload_InTheOpenItDoesNot()
	{
		Rig covered = Escorting(covered: true);
		covered.C.SenseWeapon(reloading: true);
		covered.Tick();
		Assert.False(covered.C.Intent.Crouch); // on its way

		covered.C.Position = Spot;
		covered.Tick();
		Assert.True(covered.C.Intent.Crouch);

		covered.C.SenseWeapon(reloading: false);
		covered.Tick();
		Assert.False(covered.C.Intent.Crouch);

		Rig open = Escorting(covered: false);
		open.C.SenseWeapon(reloading: true);
		open.C.Position = Spot;
		open.Tick();
		Assert.Equal(UnitGoal.GuardBuilder, open.Goal);
		Assert.False(open.C.Intent.Crouch);
	}

	[Fact]
	public void WithoutItsExecutingCondition_TheEscortWouldOutliveItsDuty()
	{
		// The guard's own check (§3.4, P3): a lower branch never replaces a running plan, so it
		// is the executing condition that ends the escort when the builder is safe.
		Rig unguarded = Escorting(executingConditions: false);
		unguarded.Escort(null);
		unguarded.Tick();
		Assert.Equal(UnitGoal.GuardBuilder, unguarded.Goal);

		Rig guarded = Escorting();
		guarded.Escort(null);
		guarded.Tick();
		Assert.Equal(UnitGoal.HoldPost, guarded.Goal);
	}

	[Fact]
	public void TheEscortIsATactic_OnTheAgentStream_AndADefendZoneTaskToTheDebugger()
	{
		Assert.True(UnitContext.IsTactic(UnitGoal.GuardBuilder));
		Assert.Equal(Gdpyr.Sim.AiDebug.AiTaskFamily.DefendZone,
			Gdpyr.Sim.AiDebug.AiGoals.Family(Gdpyr.Sim.AiDebug.HtnDomainKind.Unit, (byte)UnitGoal.GuardBuilder));
		Assert.Equal("Guard", Gdpyr.Sim.AiDebug.HtnSchema.Unit.FactName((int)UnitFact.Guard));
		Assert.Equal("Near", Gdpyr.Sim.AiDebug.HtnSchema.Unit.FactValue((int)UnitFact.Guard, (byte)CoverState.Near));
		Assert.True(UnitContext.EscortsUnder(OrderKind.Defend));
		Assert.True(UnitContext.EscortsUnder(OrderKind.None));
		Assert.False(UnitContext.EscortsUnder(OrderKind.Move));
		Assert.False(UnitContext.EscortsUnder(OrderKind.Build));
	}

	// ---- the threat --------------------------------------------------------

	private static readonly Vector3 BuilderAt = new(0f, 0.5f, 0f);

	private static KnownContact Contact(int peer, Vector3 at) => new()
	{
		OwnerId = OwnerId.ForPeer(peer),
		Kind = ContactKind.Player,
		Position = at,
		HealthFraction = 1f,
	};

	[Fact]
	public void ABuilderFiredUpon_IsThreatenedFromWhereTheFireCameFrom()
	{
		Vector3 from = new(25f, 0f, 10f);
		var memory = new ContactMemory(capacity: 4, observers: 1, liveWindowTicks: 10);
		memory.Observe(1000, 0, Contact(1, new Vector3(5f, 0f, 0f)));

		Assert.True(EscortPlan.TryThreat(BuilderAt, firedUpon: true, from, memory, 1000, null, out Vector3 threat));
		Assert.Equal(from, threat);
	}

	[Fact]
	public void WithoutARound_ItIsThreatenedByTheNearestRememberedContactWithin30m_NeverOneDeadOrForgotten()
	{
		const uint now = 10_000;
		var memory = new ContactMemory(capacity: 8, observers: 1, liveWindowTicks: 10);
		Vector3 far = new(0f, 0f, 31f);
		Vector3 ghost = new(0f, 0f, 20f);
		Vector3 dead = new(0f, 0f, 8f);
		Vector3 forgotten = new(0f, 0f, 5f);
		memory.Observe(now - (uint)SimConfig.GhostLifetimeTicks - 1, 0, Contact(4, forgotten));
		memory.Observe(now - 200, 0, Contact(2, ghost));
		memory.Observe(now, 0, Contact(1, far));
		memory.Observe(now, 0, Contact(3, dead));
		Func<int, bool> alive = ownerId => ownerId != OwnerId.ForPeer(3);

		Assert.True(EscortPlan.TryThreat(BuilderAt, firedUpon: false, Vector3.Zero, memory, now, alive, out Vector3 threat));
		Assert.Equal(ghost, threat);

		// Only the one beyond 30 m: not exposed.
		var quiet = new ContactMemory(capacity: 4, observers: 1, liveWindowTicks: 10);
		quiet.Observe(now, 0, Contact(1, far));
		Assert.False(EscortPlan.TryThreat(BuilderAt, firedUpon: false, Vector3.Zero, quiet, now, null, out _));
		Assert.False(EscortPlan.TryThreat(BuilderAt, firedUpon: false, Vector3.Zero, null, now, null, out _));
	}

	[Fact]
	public void ABuilderIsAtItsSite_Within20mOfItsBasePoint()
	{
		Assert.True(EscortPlan.AtSite(new Vector3(19f, 3f, 0f), Vector3.Zero));
		Assert.False(EscortPlan.AtSite(new Vector3(15f, 0f, 15f), Vector3.Zero));
	}

	// ---- who escorts whom ----------------------------------------------------

	private static EscortCharge Charge(ushort id, Vector3 at, bool fired = false) => new()
	{
		UnitId = id,
		Position = at,
		Threat = at + new Vector3(20f, 0f, 0f),
		FiredUpon = fired,
	};

	private static EscortCandidate Candidate(ushort id, Vector3 at, Vector3? anchor = null, float leash = 20f,
		ushort previous = 0) => new()
	{
		UnitId = id,
		Position = at,
		Anchor = anchor ?? at,
		LeashMeters = leash,
		Previous = previous,
	};

	private static (int[] chargeOf, int[] slotOf, int[] countOf, int assigned) Assign(EscortCharge[] charges,
		EscortCandidate[] candidates)
	{
		var chargeOf = new int[candidates.Length];
		var slotOf = new int[candidates.Length];
		var countOf = new int[charges.Length];
		int assigned = EscortPlan.Assign(charges, candidates, chargeOf, slotOf, countOf);
		return (chargeOf, slotOf, countOf, assigned);
	}

	[Fact]
	public void TheNearestTwo_WithTheBuilderInsideTheirLeash_EscortIt()
	{
		EscortCharge[] charges = { Charge(50, BuilderAt) };
		EscortCandidate[] candidates =
		{
			Candidate(1, new Vector3(12f, 0f, 0f)),
			Candidate(2, new Vector3(4f, 0f, 0f)),
			Candidate(3, new Vector3(3f, 0f, 0f), anchor: new Vector3(40f, 0f, 0f)), // the builder outside its leash
			Candidate(4, new Vector3(8f, 0f, 0f)),
			Candidate(5, new Vector3(6f, 0f, 0f), leash: 15f, anchor: new Vector3(0f, 0f, 16f)),
		};

		(int[] chargeOf, int[] slotOf, int[] countOf, int assigned) = Assign(charges, candidates);

		Assert.Equal(2, assigned);
		Assert.Equal(2, countOf[0]);
		Assert.Equal(new[] { -1, 0, -1, 0, -1 }, chargeOf);
		Assert.Equal(0, slotOf[1]);
		Assert.Equal(1, slotOf[3]);
	}

	[Fact]
	public void ABuilderFiredUpon_IsEscortedFirst_AndNoUnitEscortsTwo()
	{
		// Both builders want the same two units; the one being shot at, though listed second, gets them.
		EscortCharge[] charges = { Charge(50, BuilderAt), Charge(51, new Vector3(2f, 0f, 0f), fired: true) };
		EscortCandidate[] candidates =
		{
			Candidate(1, new Vector3(1f, 0f, 3f)),
			Candidate(2, new Vector3(1f, 0f, -3f)),
			Candidate(3, new Vector3(15f, 0f, 0f)),
		};

		(int[] chargeOf, _, int[] countOf, int assigned) = Assign(charges, candidates);

		Assert.Equal(3, assigned);
		Assert.Equal(1, chargeOf[0]);
		Assert.Equal(1, chargeOf[1]);
		Assert.Equal(0, chargeOf[2]);
		Assert.Equal(1, countOf[0]);
		Assert.Equal(2, countOf[1]);
	}

	[Fact]
	public void AnEscortKeepsItsBuilder_AgainstAUnitLessThan3mNearer_AndTiesGoToTheLowerId()
	{
		EscortCharge[] charges = { Charge(50, BuilderAt) };
		EscortCandidate[] kept =
		{
			Candidate(1, new Vector3(5f, 0f, 0f)),
			Candidate(2, new Vector3(5f, 0f, 2f)),
			Candidate(3, new Vector3(7.5f, 0f, 0f), previous: 50),
		};
		(int[] chargeOf, _, _, _) = Assign(charges, kept);
		Assert.Equal(new[] { 0, -1, 0 }, chargeOf);

		// More than 3 m nearer takes it: both the others are, now.
		kept[2] = Candidate(3, new Vector3(8.6f, 0f, 0f), previous: 50);
		(chargeOf, _, _, _) = Assign(charges, kept);
		Assert.Equal(new[] { 0, 0, -1 }, chargeOf);

		// Two the same distance off: the lower id first, wherever it is listed.
		EscortCandidate[] tied =
		{
			Candidate(9, new Vector3(0f, 0f, 5f)),
			Candidate(4, new Vector3(5f, 0f, 0f)),
			Candidate(7, new Vector3(-5f, 0f, 0f)),
		};
		int[] slots;
		(chargeOf, slots, _, _) = Assign(charges, tied);
		Assert.Equal(new[] { -1, 0, 0 }, chargeOf);
		Assert.Equal(0, slots[1]);
		Assert.Equal(1, slots[2]);
	}

	[Fact]
	public void WithNoBuilderExposed_NobodyEscorts_AndTheAnswerIsTheSameEveryTime()
	{
		EscortCandidate[] candidates = { Candidate(1, BuilderAt), Candidate(2, BuilderAt) };
		(int[] chargeOf, _, _, int assigned) = Assign(Array.Empty<EscortCharge>(), candidates);
		Assert.Equal(0, assigned);
		Assert.All(chargeOf, c => Assert.Equal(-1, c));

		EscortCharge[] charges = { Charge(50, BuilderAt), Charge(51, new Vector3(9f, 0f, 9f), fired: true) };
		EscortCandidate[] many = Enumerable.Range(1, 12)
			.Select(i => Candidate((ushort)i, new Vector3(i % 5 * 2f, 0f, i / 5 * 3f))).ToArray();
		var first = Assign(charges, many);
		var second = Assign(charges, many);
		Assert.Equal(first.chargeOf, second.chargeOf);
		Assert.Equal(first.slotOf, second.slotOf);
		Assert.Equal(4, first.assigned);
	}

	[Fact]
	public void AssigningEscorts_AllocatesNothing()
	{
		var charges = new EscortCharge[EscortPlan.ChargeCapacity];
		for (int b = 0; b < charges.Length; b++)
		{
			charges[b] = Charge((ushort)(200 + b), new Vector3(b * 7f, 0f, 0f), fired: b % 3 == 0);
		}

		var candidates = new EscortCandidate[SimConfig.MaxUnits];
		for (int i = 0; i < candidates.Length; i++)
		{
			candidates[i] = Candidate((ushort)(i + 1), new Vector3(i * 1.7f % 110f, 0f, i % 4), previous: (ushort)(200 + (i % 16)));
		}

		var chargeOf = new int[candidates.Length];
		var slotOf = new int[candidates.Length];
		var countOf = new int[charges.Length];
		EscortPlan.Assign(charges, candidates, chargeOf, slotOf, countOf);

		long before = GC.GetAllocatedBytesForCurrentThread();
		int assigned = 0;
		for (int t = 0; t < 600; t++)
		{
			assigned += EscortPlan.Assign(charges, candidates, chargeOf, slotOf, countOf);
		}

		Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
		Assert.True(assigned > 0);
	}

	// ---- where an escort with no wall stands ------------------------------------

	[Fact]
	public void AScreen_Stands4mFromTheBuilderTowardsTheThreat_Two3mApartAcrossTheLine()
	{
		Vector3 threat = new(0f, 0.5f, 30f);

		Vector3 alone = EscortPlan.ScreenPoint(BuilderAt, threat, 0, 1);
		Assert.Equal(0f, alone.X, 3);
		Assert.Equal(4f, alone.Z, 3);
		Assert.Equal(BuilderAt.Y, alone.Y);

		Vector3 left = EscortPlan.ScreenPoint(BuilderAt, threat, 0, 2);
		Vector3 right = EscortPlan.ScreenPoint(BuilderAt, threat, 1, 2);
		Assert.Equal(4f, left.Z, 3);
		Assert.Equal(4f, right.Z, 3);
		Assert.Equal(3f, MathF.Abs(left.X - right.X), 3);
		Assert.Equal(0f, left.X + right.X, 3);
	}

	[Fact]
	public void AScreen_NeverStandsWithin6mOfTheThreat_AndAThreatOnTheBuilderLeavesItBeside()
	{
		Vector3 near = EscortPlan.ScreenPoint(BuilderAt, new Vector3(8f, 0.5f, 0f), 0, 1);
		Assert.Equal(2f, near.X, 3);

		Vector3 onTop = EscortPlan.ScreenPoint(BuilderAt, new Vector3(3f, 0.5f, 0f), 0, 1);
		Assert.Equal(0f, onTop.X, 3);
		Assert.Equal(0f, onTop.Z, 3);

		Vector3 same = EscortPlan.ScreenPoint(BuilderAt, BuilderAt, 1, 2);
		Assert.Equal(3f * 0.5f, MathF.Abs(same.Z), 3);
	}

	[Fact]
	public void AnEscortsSpot_IsBroughtInsideItsLeash()
	{
		Vector3 anchor = new(0f, 0f, 0f);
		Vector3 inside = new(10f, 0.5f, 5f);
		Assert.Equal(inside, EscortPlan.IntoLeash(inside, anchor, 20f));

		Vector3 brought = EscortPlan.IntoLeash(new Vector3(30f, 0.5f, 40f), anchor, 20f);
		Assert.Equal(20f, MathF.Sqrt((brought.X * brought.X) + (brought.Z * brought.Z)), 3);
		Assert.Equal(12f, brought.X, 3);
		Assert.Equal(16f, brought.Z, 3);
		Assert.Equal(0.5f, brought.Y);
	}
}
