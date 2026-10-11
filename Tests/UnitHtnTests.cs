using System;
using System.Collections.Generic;
using System.Linq;
using FluidHTN;
using FluidHTN.Factory;
using Gdpyr.Sim;
using Gdpyr.Sim.Htn;
using Godot;
using Xunit;

namespace Gdpyr.Tests;

/// <summary>
/// The RTS unit's domain (docs/HTN_BOTS.md §5.2, H3): for each row of the tree, a
/// fact vector and the task chain it must produce; the order as the top-level task;
/// Retreat pre-empting, and only for a computer strategist's order (D1); Resupply
/// (H5, D2) to the nearest supply source and its give-up; every long-running operator
/// ending when its premise goes (§3.4, P3); keeping pace with the squad; the fact
/// bands; and planning without allocating (P8).
/// </summary>
public class UnitHtnTests
{
	/// <summary>infantry.tres: sensor 45 m, engage 40 m, arrival 1.5 m, leash 20 m.</summary>
	private static readonly UnitTraits Rifleman = new(45f, 40f, 1.5f, 20f);

	private static readonly Vector3 Anchor = new(10f, 0.5f, 10f);
	private static readonly Vector3 Objective = new(10f, 0.5f, 110f);
	private static readonly Vector3 Supply = new(10f, 0.5f, -60f);

	private sealed class Squaddie
	{
		public readonly Domain<UnitContext> Domain;
		public readonly Planner<UnitContext> Planner = new();
		public readonly UnitContext C;
		public readonly List<string> Plans = new();
		public int Replacements;

		public bool HasTarget;
		public Vector3 TargetPoint;

		public Squaddie(bool executingConditions = true, IFactory factory = null)
		{
			factory ??= new PooledHtnFactory();
			Domain = UnitDomain.Build(factory, executingConditions);
			C = new UnitContext(factory, UnitPlanTraits.Default, Rifleman);
			C.Init();
			C.PlannerState.OnNewPlan = plan => Plans.Add(Names(plan));
			C.PlannerState.OnReplacePlan = (_, _, plan) =>
			{
				Replacements++;
				Plans.Add(Names(plan));
			};

			C.Tick = 1000;
			C.Position = Anchor;
			C.FallbackPoint = new Vector3(-40f, 0.5f, -40f);
		}

		public void Order(OrderKind kind, Vector3 target) => C.Track(C.Position, new UnitOrder
		{
			Kind = kind,
			Target = target,
			Anchor = kind == OrderKind.Defend ? target : C.Position,
		}, HasTarget, TargetPoint);

		public void See(Vector3 target)
		{
			HasTarget = true;
			TargetPoint = target;
		}

		public void LoseTarget() => HasTarget = false;

		public void Sense(UnitFact fact, Enum value) => C.Sense(fact, Convert.ToByte(value));

		public void Sense(UnitFact fact, bool value) => C.Sense(fact, value);

		/// <summary>One scan: its health, how far outside the nearest supply source's reach it is, and whose order it has.</summary>
		public void Scan(float health, float supplyEdge, bool autonomous = true, bool sideKnown = false,
			bool hasSupply = true) => C.Encode(new UnitSense
		{
			HealthFraction = health,
			Autonomous = autonomous,
			SideKnown = sideKnown,
			Odds = OddsBand.Even,
			HasSupply = hasSupply,
			SupplyEdgeMeters = supplyEdge,
		});

		/// <summary>One scan that found a spot behind cover at <paramref name="spot"/> (docs/COVER.md §6), or none.</summary>
		public void ScanCover(Vector3? spot, bool autonomous = false)
		{
			C.HasCoverPoint = spot.HasValue;
			C.CoverPoint = spot ?? Vector3.Zero;
			C.Encode(new UnitSense
			{
				HealthFraction = 1f,
				Autonomous = autonomous,
				Odds = OddsBand.Even,
				HasCover = spot.HasValue,
				CoverHides = spot.HasValue,
			});
		}

		public void Tick(int times = 1)
		{
			for (int i = 0; i < times; i++)
			{
				C.Tick++;
				C.Track(C.Position, C.Order, HasTarget, TargetPoint);
				Planner.Tick(Domain, C);
			}
		}

		public UnitGoal Goal => C.Intent.Goal;

		public string LastPlan => Plans.Count > 0 ? Plans[^1] : "(none)";
	}

	private static string Names(Queue<ITask> plan) => "[" + string.Join(", ", plan.Select(t => t.Name)) + "]";

	private static Squaddie Ordered(OrderKind kind, Vector3 target)
	{
		var unit = new Squaddie();
		unit.Order(kind, target);
		return unit;
	}

	/// <summary>A unit on an attack order from a computer strategist, critical and outnumbered.</summary>
	private static Squaddie Losing(OrderKind kind)
	{
		Squaddie unit = Ordered(kind, Objective);
		unit.Sense(UnitFact.Stance, UnitStance.Autonomous);
		unit.Sense(UnitFact.Health, HealthBand.Critical);
		unit.Sense(UnitFact.Odds, OddsBand.Outnumbered);
		return unit;
	}

	/// <summary>A unit on a computer strategist's order, at half health, <paramref name="supplyEdge"/> metres outside a supply source's reach.</summary>
	private static Squaddie Hurt(OrderKind kind = OrderKind.Attack, float supplyEdge = 30f, float health = 0.5f,
		bool autonomous = true)
	{
		Squaddie unit = Ordered(kind, kind == OrderKind.Defend ? Anchor : Objective);
		unit.C.SupplyPoint = Supply;
		unit.Scan(health, supplyEdge, autonomous);
		return unit;
	}

	// ---- the order is the top-level task -----------------------------------

	[Fact]
	public void AMoveOrder_IsObeyed_ShootingOnTheWalk()
	{
		Squaddie unit = Ordered(OrderKind.Move, Objective);
		unit.C.FocusOwnerId = OwnerId.ForPeer(5);
		unit.Tick();

		Assert.Equal("[Obey]", unit.LastPlan);
		Assert.Equal(UnitMove.Order, unit.C.Intent.Move);
		Assert.False(unit.C.Intent.StopToFight);
		Assert.Equal(OwnerId.ForPeer(5), unit.C.Intent.TargetOwnerId);
	}

	[Fact]
	public void APatrol_IsToday_AndDoesNotStopForAContact()
	{
		Squaddie unit = Ordered(OrderKind.Patrol, Objective);
		unit.See(Objective);
		unit.Tick();

		Assert.Equal("[Patrol]", unit.LastPlan);
		Assert.Equal(UnitMove.Order, unit.C.Intent.Move);
		Assert.False(unit.C.Intent.StopToFight);
	}

	[Fact]
	public void ABuilder_Works()
	{
		Squaddie unit = Ordered(OrderKind.Build, Objective);
		unit.Tick();

		Assert.Equal("[Work]", unit.LastPlan);
		Assert.Equal(UnitMove.Order, unit.C.Intent.Move);
	}

	[Fact]
	public void ABuilderUnderFire_StepsToItsNearestFriend_ThenGoesBackToWork()
	{
		Squaddie unit = Ordered(OrderKind.Build, Objective);
		unit.C.FriendPoint = new Vector3(20f, 0.5f, 20f);
		unit.Tick();

		unit.Sense(UnitFact.UnderFire, true);
		unit.Sense(UnitFact.FriendNear, true);
		unit.Tick();

		Assert.Equal("[TakeCover]", unit.LastPlan);
		Assert.Equal(UnitMove.Point, unit.C.Intent.Move);
		Assert.Equal(unit.C.FriendPoint, unit.C.Intent.Point);
		Assert.False(unit.C.Intent.StopToFight);

		unit.Sense(UnitFact.UnderFire, false);
		unit.Tick();

		Assert.Equal(UnitGoal.Work, unit.Goal);
	}

	[Fact]
	public void ABuilderUnderFireWithNobodyNear_KeepsWorking()
	{
		Squaddie unit = Ordered(OrderKind.Build, Objective);
		unit.Sense(UnitFact.UnderFire, true);
		unit.Tick();

		Assert.Equal("[Work]", unit.LastPlan);
	}

	[Fact]
	public void AnAttackOrderWithNothingInSight_Advances_OnTheOrderedPoint()
	{
		Squaddie unit = Ordered(OrderKind.Attack, Objective);
		unit.Tick();

		Assert.Equal("[Advance]", unit.LastPlan);
		Assert.Equal(UnitMove.Point, unit.C.Intent.Move);
		Assert.Equal(Objective, unit.C.Intent.Point);
		Assert.True(unit.C.Intent.StopToFight);
	}

	[Fact]
	public void AnAttackOnANamedTarget_Advances_OnWhereTheSideLastSawIt()
	{
		Squaddie unit = Ordered(OrderKind.Attack, Objective);
		unit.C.HasNamedTargetPoint = true;
		unit.C.NamedTargetPoint = new Vector3(40f, 0.5f, 90f);
		unit.Tick();

		Assert.Equal(unit.C.NamedTargetPoint, unit.C.Intent.Point);
	}

	[Fact]
	public void AnAttackWithATarget_EngagesTheSquadsFocus_StandingToFight()
	{
		Squaddie unit = Ordered(OrderKind.Attack, Objective);
		unit.C.FocusOwnerId = OwnerId.ForPeer(9);
		unit.See(new Vector3(10f, 1f, 60f));
		unit.Tick();

		Assert.Equal("[EngageFocus]", unit.LastPlan);
		Assert.Equal(UnitMove.Target, unit.C.Intent.Move);
		Assert.Equal(Objective, unit.C.Intent.Point);
		Assert.True(unit.C.Intent.StopToFight);
		Assert.Equal(OwnerId.ForPeer(9), unit.C.Intent.TargetOwnerId);
	}

	[Fact]
	public void AnAttackWhoseSquadMateIsInAFight_GoesToIt()
	{
		Squaddie unit = Ordered(OrderKind.Attack, Objective);
		unit.C.HasSupport = true;
		unit.C.SupportPoint = new Vector3(30f, 0.5f, 70f);
		unit.Sense(UnitFact.FriendEngaged, true);
		unit.Tick();

		Assert.Equal("[Support]", unit.LastPlan);
		Assert.Equal(unit.C.SupportPoint, unit.C.Intent.Point);
		Assert.True(unit.C.Intent.StopToFight);
	}

	[Fact]
	public void ASupportingUnitThatFindsItsOwnTarget_EngagesIt()
	{
		Squaddie unit = Ordered(OrderKind.Attack, Objective);
		unit.C.HasSupport = true;
		unit.Sense(UnitFact.FriendEngaged, true);
		unit.Tick();

		unit.See(new Vector3(10f, 1f, 50f));
		unit.Tick();

		Assert.Equal(UnitGoal.EngageFocus, unit.Goal);
	}

	[Fact]
	public void AGatheringSquad_WaitsAtItsStagingPoint_ThenAdvances()
	{
		Squaddie unit = Ordered(OrderKind.Attack, Objective);
		unit.C.HasStaging = true;
		unit.C.StagingPoint = new Vector3(0f, 0.5f, 50f);
		unit.Sense(UnitFact.SquadPhase, SquadPhase.Gathering);
		unit.See(new Vector3(10f, 1f, 50f));
		unit.Tick();

		Assert.Equal("[WaitForSquad]", unit.LastPlan);
		Assert.Equal(unit.C.StagingPoint, unit.C.Intent.Point);

		unit.Sense(UnitFact.SquadPhase, SquadPhase.Moving);
		unit.LoseTarget();
		unit.Tick();

		Assert.Equal(UnitGoal.Advance, unit.Goal);
	}

	[Fact]
	public void AGatheringSquadWithNoStagingPoint_HoldsWhereItIs()
	{
		Squaddie unit = Ordered(OrderKind.Attack, Objective);
		unit.Sense(UnitFact.SquadPhase, SquadPhase.Gathering);
		unit.Tick();

		Assert.Equal(UnitMove.Hold, unit.C.Intent.Move);
	}

	[Fact]
	public void ADefendingUnit_TakesItsPost()
	{
		Squaddie unit = Ordered(OrderKind.Defend, Anchor);
		unit.C.HasPost = true;
		unit.C.PostPoint = Anchor + new Vector3(4f, 0f, 0f);
		unit.Tick();

		Assert.Equal("[HoldPost]", unit.LastPlan);
		Assert.Equal(UnitMove.Point, unit.C.Intent.Move);
		Assert.Equal(unit.C.PostPoint, unit.C.Intent.Point);
		Assert.True(unit.C.Intent.StopToFight);
	}

	[Fact]
	public void AUnitWithNoOrder_HoldsWhereItIsAsItAlwaysHas()
	{
		Squaddie unit = Ordered(OrderKind.None, Anchor);
		unit.C.HasPost = true;
		unit.Tick();

		Assert.Equal("[HoldPost]", unit.LastPlan);
		Assert.Equal(UnitMove.Order, unit.C.Intent.Move);
		Assert.True(unit.C.Intent.StopToFight);
	}

	[Fact]
	public void ADefendingUnit_EngagesInsideItsLeash_AndGoesBackToItsPostOutsideIt()
	{
		Squaddie unit = Ordered(OrderKind.Defend, Anchor);
		unit.See(Anchor + new Vector3(0f, 0.5f, 15f));
		unit.Tick();

		Assert.Equal("[EngageInLeash]", unit.LastPlan);
		Assert.Equal(UnitMove.Order, unit.C.Intent.Move);
		Assert.True(unit.C.Intent.StopToFight);

		unit.See(Anchor + new Vector3(0f, 0.5f, 25f));
		unit.Tick();

		Assert.Equal(UnitGoal.HoldPost, unit.Goal);
	}

	[Fact]
	public void ADefendingUnit_AnswersAFriendsFightInsideItsLeash()
	{
		Squaddie unit = Ordered(OrderKind.Defend, Anchor);
		unit.C.HasSupport = true;
		unit.C.SupportPoint = Anchor + new Vector3(12f, 0f, 0f);
		unit.Sense(UnitFact.FriendEngaged, true);
		unit.Tick();

		Assert.Equal("[AnswerCall]", unit.LastPlan);
		Assert.Equal(unit.C.SupportPoint, unit.C.Intent.Point);

		// Past the leash it is somebody else's fight.
		unit.C.SupportPoint = Anchor + new Vector3(30f, 0f, 0f);
		unit.Tick();

		Assert.Equal(UnitGoal.HoldPost, unit.Goal);
	}

	[Fact]
	public void AUnitToldToStop_DoesNotAnswerCalls()
	{
		Squaddie unit = Ordered(OrderKind.None, Anchor);
		unit.C.HasSupport = true;
		unit.C.SupportPoint = Anchor + new Vector3(5f, 0f, 0f);
		unit.Sense(UnitFact.FriendEngaged, true);
		unit.Tick();

		Assert.Equal("[HoldPost]", unit.LastPlan);
		Assert.Equal(UnitMove.Order, unit.C.Intent.Move);
	}

	// ---- retreat, and whose order it is ------------------------------------

	[Theory]
	[InlineData(OrderKind.Attack)]
	[InlineData(OrderKind.Defend)]
	[InlineData(OrderKind.Patrol)]
	[InlineData(OrderKind.None)]
	public void ABotsUnit_CriticalAndOutnumbered_Retreats_WithoutStoppingToFight(OrderKind order)
	{
		Squaddie unit = Losing(order);
		unit.See(Objective);
		unit.Tick();

		Assert.Equal("[Retreat]", unit.LastPlan);
		Assert.Equal(UnitMove.Point, unit.C.Intent.Move);
		Assert.Equal(unit.C.FallbackPoint, unit.C.Intent.Point);
		Assert.False(unit.C.Intent.StopToFight);
	}

	[Theory]
	[InlineData(OrderKind.Move)]
	[InlineData(OrderKind.Build)]
	public void AMoveOrABuildOrder_IsNeverAbandoned(OrderKind order)
	{
		Squaddie unit = Losing(order);
		unit.Tick();

		Assert.NotEqual(UnitGoal.Retreat, unit.Goal);
	}

	[Fact]
	public void AUnitAPersonOrdered_NeverRetreatsOnItsOwn()
	{
		Squaddie unit = Losing(OrderKind.Attack);
		unit.Sense(UnitFact.Stance, UnitStance.Obey);
		unit.See(Objective);
		unit.Tick();

		Assert.Equal("[EngageFocus]", unit.LastPlan);
	}

	[Fact]
	public void Retreat_ReplacesARunningAttackTheTickItsFactsAppear()
	{
		Squaddie unit = Ordered(OrderKind.Attack, Objective);
		unit.Sense(UnitFact.Stance, UnitStance.Autonomous);
		unit.See(Objective);
		unit.Tick(10);
		Assert.Equal(UnitGoal.EngageFocus, unit.Goal);
		Assert.Equal(0, unit.Replacements);

		unit.Sense(UnitFact.Health, HealthBand.Critical);
		unit.Sense(UnitFact.Odds, OddsBand.Outnumbered);
		unit.Tick();

		Assert.Equal(UnitGoal.Retreat, unit.Goal);
		Assert.Equal(1, unit.Replacements);
	}

	[Fact]
	public void ARetreat_EndsWhenTheOddsDo_AndTheOrderResumes()
	{
		Squaddie unit = Losing(OrderKind.Attack);
		unit.Tick();
		Assert.Equal(UnitGoal.Retreat, unit.Goal);

		unit.Sense(UnitFact.Odds, OddsBand.Even);
		unit.Tick();

		Assert.Equal(UnitGoal.Advance, unit.Goal);
	}

	[Fact]
	public void ANewOrder_EndsARunningPlanForALowerBranch()
	{
		// Defend Zone is below Attack: only the executing condition on the order lets
		// the new order in (§3.4, P3).
		Squaddie unit = Ordered(OrderKind.Attack, Objective);
		unit.Tick(5);
		Assert.Equal(UnitGoal.Advance, unit.Goal);

		unit.Order(OrderKind.Defend, Anchor);
		unit.Tick();

		Assert.Equal(UnitGoal.HoldPost, unit.Goal);
	}

	// ---- resupply (H5, D2) ---------------------------------------------------

	[Theory]
	[InlineData(OrderKind.Attack)]
	[InlineData(OrderKind.Defend)]
	[InlineData(OrderKind.Patrol)]
	[InlineData(OrderKind.None)]
	public void ABotsWoundedUnit_WithNothingInSight_WalksToTheNearestSupply(OrderKind order)
	{
		Squaddie unit = Hurt(order);
		unit.Tick();

		Assert.Equal("[Resupply]", unit.LastPlan);
		Assert.Equal(UnitMove.Point, unit.C.Intent.Move);
		Assert.Equal(Supply, unit.C.Intent.Point);
	}

	[Fact]
	public void AResupply_StandsInsideTheReach_UntilHealed_ThenTheOrderResumes()
	{
		Squaddie unit = Hurt();
		unit.Tick();
		Assert.Equal(UnitGoal.Resupply, unit.Goal);

		// Three metres inside the edge: it stops, and the task goes on while it heals.
		unit.Scan(0.5f, -3.5f);
		unit.Tick();
		Assert.Equal(UnitGoal.Resupply, unit.Goal);
		Assert.Equal(UnitMove.Hold, unit.C.Intent.Move);

		// Out of the hurt band, and still wanting more.
		unit.Scan(0.8f, -3.5f);
		unit.Tick();
		Assert.Equal(UnitGoal.Resupply, unit.Goal);

		unit.Scan(0.96f, -3.5f);
		unit.Tick();
		Assert.Equal(UnitGoal.Advance, unit.Goal);
	}

	[Fact]
	public void ATruckThatDrivesOff_IsFollowed()
	{
		Squaddie unit = Hurt();
		unit.Tick();
		unit.Scan(0.5f, -4f);
		unit.Tick();
		Assert.Equal(UnitMove.Hold, unit.C.Intent.Move);

		unit.C.SupplyPoint = Supply + new Vector3(20f, 0f, 0f);
		unit.Scan(0.5f, 5f);
		unit.Tick();

		Assert.Equal(UnitGoal.Resupply, unit.Goal);
		Assert.Equal(UnitMove.Point, unit.C.Intent.Move);
		Assert.Equal(unit.C.SupplyPoint, unit.C.Intent.Point);
	}

	[Fact]
	public void AUnitAPersonOrdered_NeverResuppliesOnItsOwn()
	{
		Squaddie unit = Hurt(autonomous: false);
		unit.Tick();

		Assert.Equal(UnitGoal.Advance, unit.Goal);
	}

	[Theory]
	[InlineData(OrderKind.Move)]
	[InlineData(OrderKind.Build)]
	public void AMoveOrABuildOrder_IsNeverAbandonedForResupply(OrderKind order)
	{
		Squaddie unit = Hurt(order);
		unit.Tick();

		Assert.NotEqual(UnitGoal.Resupply, unit.Goal);
	}

	[Fact]
	public void AWoundedUnit_WithAContactNearby_KeepsToItsOrder()
	{
		Squaddie unit = Hurt();
		unit.Scan(0.5f, 30f, sideKnown: true);
		unit.Tick();

		Assert.Equal(UnitGoal.Advance, unit.Goal);
	}

	[Fact]
	public void AResupply_EndsWhenATargetOfItsOwnAppears_ButNotForAContactItOnlyKnowsOf()
	{
		Squaddie unit = Hurt();
		unit.Tick();

		// Walking back is not turned round by every ghost the side remembers.
		unit.Scan(0.5f, 25f, sideKnown: true);
		unit.Tick();
		Assert.Equal(UnitGoal.Resupply, unit.Goal);

		unit.See(Objective);
		unit.Tick();
		Assert.Equal(UnitGoal.EngageFocus, unit.Goal);
	}

	[Fact]
	public void AWoundedUnitAlreadyInReach_KeepsToItsOrder_UntilItWalksOutOfIt()
	{
		// Healed where it stands: nothing to walk to.
		Squaddie unit = Hurt(supplyEdge: -10f);
		unit.Tick(5);
		Assert.Equal(UnitGoal.Advance, unit.Goal);
		Assert.Equal(0, unit.Replacements);

		// Its order takes it past the edge: back in, and it stays until it is healed.
		unit.Scan(0.5f, 1f);
		unit.Tick();
		Assert.Equal(UnitGoal.Resupply, unit.Goal);
		Assert.Equal(1, unit.Replacements);
	}

	[Fact]
	public void ASourceOutOfRange_IsLeftAlone_AndOneInRangeStaysInRangeALittleFurther()
	{
		float range = UnitPlanTraits.Default.SupplyRangeMeters;
		Squaddie far = Hurt(supplyEdge: range + 1f);
		far.Tick();
		Assert.Equal(UnitGoal.Advance, far.Goal);

		Squaddie near = Hurt(supplyEdge: range);
		near.Tick();
		Assert.Equal(UnitGoal.Resupply, near.Goal);

		near.Scan(0.5f, range + UnitPlanTraits.Default.SupplyRangeHysteresisMeters - 1f);
		near.Tick();
		Assert.Equal(UnitGoal.Resupply, near.Goal);

		near.Scan(0.5f, range + UnitPlanTraits.Default.SupplyRangeHysteresisMeters + 1f);
		near.Tick();
		Assert.Equal(UnitGoal.Advance, near.Goal);
	}

	[Fact]
	public void NoSourceThatCanHealIt_IsNoResupply()
	{
		Squaddie unit = Hurt();
		unit.Scan(0.5f, 0f, hasSupply: false);
		unit.Tick();

		Assert.Equal(UnitGoal.Advance, unit.Goal);
	}

	[Fact]
	public void AResupplyThatTakesTooLong_IsGivenUp_AndNotTriedAgainFor30s()
	{
		UnitPlanTraits traits = UnitPlanTraits.Default;
		Squaddie unit = Hurt();
		unit.Tick();
		Assert.Equal(UnitGoal.Resupply, unit.Goal);

		unit.Tick(traits.ResupplyMaxTicks);
		Assert.Equal(UnitGoal.Resupply, unit.Goal);

		unit.Tick();
		Assert.Equal(UnitGoal.Advance, unit.Goal);
		Assert.Equal(1, unit.C.FailedResupplies);

		// The same scan again does not bring the same trip back (§3.4, P4)…
		unit.Scan(0.5f, 30f);
		unit.Tick(traits.ResupplyRetryTicks - 1);
		unit.Scan(0.5f, 30f);
		unit.Tick();
		Assert.Equal(UnitGoal.Advance, unit.Goal);

		// …until its time is up.
		unit.Tick(2);
		unit.Scan(0.5f, 30f);
		unit.Tick();
		Assert.Equal(UnitGoal.Resupply, unit.Goal);
	}

	[Fact]
	public void Retreat_OutranksResupply()
	{
		Squaddie unit = Hurt(health: 0.2f);
		unit.Sense(UnitFact.Odds, OddsBand.Outnumbered);
		unit.Tick();

		Assert.Equal(UnitGoal.Retreat, unit.Goal);
	}

	[Theory]
	[InlineData(0.61f, false, false)]
	[InlineData(0.54f, false, true)]
	[InlineData(0.9f, true, true)]
	[InlineData(0.95f, true, false)]
	public void Wounded_IsEnteredOutOfOk_AndHeldUntil95Percent(float health, bool before, bool expected)
	{
		UnitContext c = Context();
		c.Sense(UnitFact.Wounded, before);
		c.Sense(UnitFact.Health, (byte)(before ? HealthBand.Hurt : HealthBand.Ok));
		c.Encode(new UnitSense { HealthFraction = health });

		Assert.Equal(expected, c.Is(UnitFact.Wounded));
	}

	[Theory]
	[InlineData(-2f, false, false)]
	[InlineData(-3f, false, true)]
	[InlineData(-0.5f, true, true)]
	[InlineData(0.5f, true, false)]
	public void Supplied_IsEnteredThreeMetresInside_AndHeldToTheEdge(float edge, bool before, bool expected)
	{
		UnitContext c = Context();
		c.Sense(UnitFact.Supplied, before);
		c.Encode(new UnitSense { HealthFraction = 1f, HasSupply = true, SupplyEdgeMeters = edge });

		Assert.Equal(expected, c.Is(UnitFact.Supplied));
	}

	[Theory]
	[InlineData(false, UnitGoal.EngageInLeash)]
	[InlineData(true, UnitGoal.HoldPost)]
	public void ALowerMethodNeverReplacesARunningOne_OnlyAnExecutingConditionEndsIt(bool executingConditions,
		UnitGoal thirtyTicksLater)
	{
		var unit = new Squaddie(executingConditions);
		unit.Order(OrderKind.Defend, Anchor);
		unit.See(Anchor + new Vector3(0f, 0.5f, 10f));
		unit.Tick();
		Assert.Equal(UnitGoal.EngageInLeash, unit.Goal);

		unit.LoseTarget();
		unit.Tick(30);

		Assert.Equal(thirtyTicksLater, unit.Goal);
	}

	/// <summary>
	/// For every operator, an order and a situation that plan it, the change that must
	/// end it, and what the unit does next (§3.4, P3).
	/// </summary>
	/// <summary>A spot behind a wall four metres from the anchor, inside a rifleman's leash.</summary>
	private static readonly Vector3 CoverSpot = Anchor + new Vector3(0f, 0f, 4f);

	// ---- cover (docs/COVER.md §6) ---------------------------------------------

	[Fact]
	public void AnAttackerWithATargetAndCoverNear_WalksToItFiringThenStandsInIt()
	{
		Squaddie unit = Ordered(OrderKind.Attack, Objective);
		unit.ScanCover(CoverSpot);
		unit.See(Anchor + new Vector3(0f, 0.5f, 20f));
		unit.Tick();

		Assert.Equal("[HoldCover]", unit.LastPlan);
		Assert.Equal(UnitMove.Point, unit.C.Intent.Move);
		Assert.Equal(CoverSpot, unit.C.Intent.Point);
		Assert.False(unit.C.Intent.StopToFight);

		unit.C.Position = CoverSpot + new Vector3(0.3f, 0f, 0f);
		unit.Tick();

		Assert.Equal(UnitGoal.HoldCover, unit.Goal);
		Assert.Equal(UnitMove.Hold, unit.C.Intent.Move);
		Assert.True(unit.C.Intent.StopToFight);
		Assert.Equal((byte)CoverState.In, unit.C.Get(UnitFact.Cover));
	}

	[Fact]
	public void ADefenderFightsFromCoverInsideItsLeash()
	{
		Squaddie unit = Ordered(OrderKind.Defend, Anchor);
		unit.ScanCover(CoverSpot);
		unit.See(Anchor + new Vector3(0f, 0.5f, 15f));
		unit.Tick();

		Assert.Equal(UnitGoal.HoldCover, unit.Goal);
	}

	[Fact]
	public void WithoutCover_TheFightIsAsItWas()
	{
		Squaddie attacker = Ordered(OrderKind.Attack, Objective);
		attacker.ScanCover(null);
		attacker.See(Anchor + new Vector3(0f, 0.5f, 20f));
		attacker.Tick();
		Assert.Equal(UnitGoal.EngageFocus, attacker.Goal);

		Squaddie defender = Ordered(OrderKind.Defend, Anchor);
		defender.ScanCover(null);
		defender.See(Anchor + new Vector3(0f, 0.5f, 15f));
		defender.Tick();
		Assert.Equal(UnitGoal.EngageInLeash, defender.Goal);
	}

	[Fact]
	public void ABuilderUnderFire_HidesBehindCoverRatherThanBesideAFriend()
	{
		Squaddie unit = Ordered(OrderKind.Build, Objective);
		unit.C.FriendPoint = Anchor + new Vector3(-6f, 0f, 0f);
		unit.ScanCover(CoverSpot);
		unit.Sense(UnitFact.UnderFire, true);
		unit.Sense(UnitFact.FriendNear, true);
		unit.Tick();

		Assert.Equal("[TakeCover]", unit.LastPlan);
		Assert.Equal(CoverSpot, unit.C.Intent.Point);

		// No cover in reach: beside the friend, as before.
		Squaddie other = Ordered(OrderKind.Build, Objective);
		other.C.FriendPoint = Anchor + new Vector3(-6f, 0f, 0f);
		other.ScanCover(null);
		other.Sense(UnitFact.UnderFire, true);
		other.Sense(UnitFact.FriendNear, true);
		other.Tick();

		Assert.Equal(UnitGoal.TakeCover, other.Goal);
		Assert.Equal(other.C.FriendPoint, other.C.Intent.Point);
	}

	[Fact]
	public void Retreat_ReplacesFightingFromCover()
	{
		Squaddie unit = Ordered(OrderKind.Attack, Objective);
		unit.ScanCover(CoverSpot, autonomous: true);
		unit.See(Anchor + new Vector3(0f, 0.5f, 20f));
		unit.Tick();
		Assert.Equal(UnitGoal.HoldCover, unit.Goal);

		unit.Sense(UnitFact.Health, HealthBand.Critical);
		unit.Sense(UnitFact.Odds, OddsBand.Outnumbered);
		unit.Tick();

		Assert.Equal(UnitGoal.Retreat, unit.Goal);
	}

	[Fact]
	public void AMoveOrderIsNeverSecondGuessedForCover()
	{
		Squaddie unit = Ordered(OrderKind.Move, Objective);
		unit.ScanCover(CoverSpot);
		unit.See(Anchor + new Vector3(0f, 0.5f, 20f));
		unit.Tick();

		Assert.Equal(UnitGoal.Obey, unit.Goal);
	}

	[Fact]
	public void InCover_IsHeldUntilShovedClearOfTheSpot()
	{
		Squaddie unit = Ordered(OrderKind.Attack, Objective);
		unit.C.Position = CoverSpot;
		unit.ScanCover(CoverSpot);
		unit.See(Anchor + new Vector3(0f, 0.5f, 20f));
		unit.Tick();
		Assert.Equal((byte)CoverState.In, unit.C.Get(UnitFact.Cover));

		unit.C.Position = CoverSpot + new Vector3(1.4f, 0f, 0f);
		unit.Tick();
		Assert.Equal((byte)CoverState.In, unit.C.Get(UnitFact.Cover));

		unit.C.Position = CoverSpot + new Vector3(2f, 0f, 0f);
		unit.Tick();
		Assert.Equal((byte)CoverState.Near, unit.C.Get(UnitFact.Cover));
		Assert.Equal(UnitMove.Point, unit.C.Intent.Move);
	}

	public static IEnumerable<object[]> Premises()
	{
		yield return new object[] { OrderKind.Attack, UnitGoal.Retreat, "losing", "odds even", UnitGoal.Advance };
		yield return new object[] { OrderKind.Attack, UnitGoal.Resupply, "wounded", "healed", UnitGoal.Advance };
		yield return new object[] { OrderKind.Defend, UnitGoal.Resupply, "wounded", "supply gone", UnitGoal.HoldPost };
		yield return new object[] { OrderKind.Attack, UnitGoal.Resupply, "wounded", "order move", UnitGoal.Obey };
		yield return new object[] { OrderKind.Build, UnitGoal.TakeCover, "under fire", "fire stops", UnitGoal.Work };
		yield return new object[] { OrderKind.Build, UnitGoal.TakeCover, "under fire, cover", "fire stops", UnitGoal.Work };
		yield return new object[] { OrderKind.Attack, UnitGoal.HoldCover, "target, cover", "target lost", UnitGoal.Advance };
		yield return new object[] { OrderKind.Attack, UnitGoal.HoldCover, "target, cover", "cover gone", UnitGoal.EngageFocus };
		yield return new object[] { OrderKind.Defend, UnitGoal.HoldCover, "target, cover", "target lost", UnitGoal.HoldPost };
		yield return new object[] { OrderKind.Build, UnitGoal.Work, "", "order none", UnitGoal.HoldPost };
		yield return new object[] { OrderKind.Attack, UnitGoal.WaitForSquad, "gathering", "moving", UnitGoal.Advance };
		yield return new object[] { OrderKind.Attack, UnitGoal.EngageFocus, "target", "target lost", UnitGoal.Advance };
		yield return new object[] { OrderKind.Attack, UnitGoal.Support, "friend engaged", "friend done", UnitGoal.Advance };
		yield return new object[] { OrderKind.Attack, UnitGoal.Advance, "", "order defend", UnitGoal.HoldPost };
		yield return new object[] { OrderKind.Defend, UnitGoal.EngageInLeash, "target", "target lost", UnitGoal.HoldPost };
		yield return new object[] { OrderKind.Defend, UnitGoal.AnswerCall, "friend engaged", "friend done", UnitGoal.HoldPost };
		yield return new object[] { OrderKind.Defend, UnitGoal.HoldPost, "", "order move", UnitGoal.Obey };
		yield return new object[] { OrderKind.Patrol, UnitGoal.Patrol, "", "order defend", UnitGoal.HoldPost };
		yield return new object[] { OrderKind.Move, UnitGoal.Obey, "", "order attack", UnitGoal.Advance };
	}

	[Theory]
	[MemberData(nameof(Premises))]
	public void EveryOperator_EndsWhenItsPremiseGoes(OrderKind order, UnitGoal goal, string situation, string change,
		UnitGoal next)
	{
		Squaddie unit = Ordered(order, order == OrderKind.Defend ? Anchor : Objective);
		unit.C.HasSupport = true;
		unit.C.SupportPoint = Anchor + new Vector3(5f, 0f, 0f);
		switch (situation)
		{
			case "losing":
				unit.Sense(UnitFact.Stance, UnitStance.Autonomous);
				unit.Sense(UnitFact.Health, HealthBand.Critical);
				unit.Sense(UnitFact.Odds, OddsBand.Outnumbered);
				break;
			case "under fire":
				unit.Sense(UnitFact.UnderFire, true);
				unit.Sense(UnitFact.FriendNear, true);
				break;
			case "under fire, cover":
				unit.ScanCover(CoverSpot);
				unit.Sense(UnitFact.UnderFire, true);
				break;
			case "target, cover":
				unit.ScanCover(CoverSpot);
				unit.See(Anchor + new Vector3(0f, 0.5f, 15f));
				break;
			case "gathering":
				unit.Sense(UnitFact.SquadPhase, SquadPhase.Gathering);
				break;
			case "target":
				unit.See(Anchor + new Vector3(0f, 0.5f, 5f));
				break;
			case "friend engaged":
				unit.Sense(UnitFact.FriendEngaged, true);
				break;
			case "wounded":
				unit.Sense(UnitFact.Stance, UnitStance.Autonomous);
				unit.Sense(UnitFact.Wounded, true);
				unit.Sense(UnitFact.Supply, true);
				break;
		}

		unit.Tick(3);
		Assert.Equal(goal, unit.Goal);

		switch (change)
		{
			case "odds even":
				unit.Sense(UnitFact.Odds, OddsBand.Even);
				break;
			case "fire stops":
				unit.Sense(UnitFact.UnderFire, false);
				break;
			case "moving":
				unit.Sense(UnitFact.SquadPhase, SquadPhase.Moving);
				break;
			case "target lost":
				unit.LoseTarget();
				break;
			case "cover gone":
				unit.ScanCover(null);
				break;
			case "friend done":
				unit.Sense(UnitFact.FriendEngaged, false);
				break;
			case "healed":
				unit.Sense(UnitFact.Wounded, false);
				break;
			case "supply gone":
				unit.Sense(UnitFact.Supply, false);
				break;
			case "order none":
				unit.Order(OrderKind.None, unit.C.Position);
				break;
			case "order defend":
				unit.Order(OrderKind.Defend, Anchor);
				break;
			case "order move":
				unit.Order(OrderKind.Move, Objective);
				break;
			case "order attack":
				unit.Order(OrderKind.Attack, Objective);
				break;
		}

		unit.Tick(3);
		Assert.Equal(next, unit.Goal);
	}

	[Fact]
	public void ResettingThePlan_StopsTheOperatorAndClearsTheIntent()
	{
		var planning = new UnitPlanning(UnitPlanTraits.Default);
		UnitContext c = planning.CreateContext(Rifleman);
		c.Track(Anchor, new UnitOrder { Kind = OrderKind.Move, Target = Objective }, false, Vector3.Zero);
		planning.Tick(c);
		Assert.Equal(UnitGoal.Obey, c.Intent.Goal);

		planning.Reset(c);

		Assert.Equal(UnitGoal.None, c.Intent.Goal);
	}

	// ---- keeping pace with the squad ---------------------------------------

	/// <summary>A unit advancing on <see cref="Objective"/> with its squad's centroid <paramref name="behind"/> metres further back.</summary>
	private static Squaddie Advancing(float behind)
	{
		Squaddie unit = Ordered(OrderKind.Attack, Objective);
		unit.C.Position = new Vector3(10f, 0.5f, 60f);
		unit.C.HasSquad = true;
		unit.C.SquadAlive = 4;
		unit.C.SquadCentroid = unit.C.Position - new Vector3(0f, 0f, behind);
		return unit;
	}

	[Fact]
	public void AUnitWellAheadOfItsSquad_WaitsForIt()
	{
		Squaddie unit = Advancing(behind: 12f);
		unit.Tick();

		Assert.Equal(UnitGoal.Advance, unit.Goal);
		Assert.Equal(UnitMove.Hold, unit.C.Intent.Move);
		Assert.True(unit.C.Waiting);
	}

	[Fact]
	public void AUnitWithItsSquad_WalksOn()
	{
		Squaddie unit = Advancing(behind: 6f);
		unit.Tick();

		Assert.Equal(UnitMove.Point, unit.C.Intent.Move);
		Assert.Equal(Objective, unit.C.Intent.Point);
	}

	[Fact]
	public void AWaitingUnit_WalksOnOnceTheSquadHasClosedUp_NotBefore()
	{
		Squaddie unit = Advancing(behind: 12f);
		unit.Tick();
		Assert.True(unit.C.Waiting);

		// Inside the 8 m that starts a wait, outside the 3 m that ends one.
		unit.C.SquadCentroid = unit.C.Position - new Vector3(0f, 0f, 5f);
		unit.Tick();
		Assert.Equal(UnitMove.Hold, unit.C.Intent.Move);

		unit.C.SquadCentroid = unit.C.Position - new Vector3(0f, 0f, 2f);
		unit.Tick();
		Assert.Equal(UnitMove.Point, unit.C.Intent.Move);
		Assert.False(unit.C.Waiting);
	}

	[Fact]
	public void AUnitBehindItsSquad_NeverWaits()
	{
		Squaddie unit = Advancing(behind: -20f);
		unit.Tick();

		Assert.Equal(UnitMove.Point, unit.C.Intent.Move);
	}

	[Fact]
	public void AUnitAlone_NeverWaits()
	{
		Squaddie unit = Advancing(behind: 30f);
		unit.C.SquadAlive = 1;
		unit.Tick();

		Assert.Equal(UnitMove.Point, unit.C.Intent.Move);
	}

	[Fact]
	public void ASquadThatNeverClosesUp_IsWaitedForEightSeconds_ThenLeft()
	{
		// A squad-mate that is stuck is not coming.
		Squaddie unit = Advancing(behind: 30f);
		int limit = UnitPlanTraits.Default.PaceMaxWaitTicks;

		unit.Tick(limit);
		Assert.Equal(UnitMove.Hold, unit.C.Intent.Move);

		unit.Tick();
		Assert.Equal(UnitMove.Point, unit.C.Intent.Move);
		Assert.True(unit.C.PaceGivenUp);

		// Left for the rest of the advance, however far ahead it gets.
		unit.C.SquadCentroid -= new Vector3(0f, 0f, 50f);
		unit.Tick(60);
		Assert.Equal(UnitMove.Point, unit.C.Intent.Move);
	}

	[Fact]
	public void ASlowSquadThatKeepsClosingUp_IsWaitedForAsOftenAsItTakes()
	{
		// A technical at 8 m/s with riflemen at 4.5: many short waits, more than eight
		// seconds of them in all.
		Squaddie unit = Advancing(behind: 12f);
		int limit = UnitPlanTraits.Default.PaceMaxWaitTicks;
		int held = 0;
		for (int stretch = 0; stretch < 6; stretch++)
		{
			unit.C.SquadCentroid = unit.C.Position - new Vector3(0f, 0f, 12f);
			for (int t = 0; t < limit / 2; t++)
			{
				unit.Tick();
				held += unit.C.Intent.Move == UnitMove.Hold ? 1 : 0;
			}

			unit.C.SquadCentroid = unit.C.Position - new Vector3(0f, 0f, 1f);
			unit.Tick();
			Assert.Equal(UnitMove.Point, unit.C.Intent.Move);
		}

		Assert.False(unit.C.PaceGivenUp);
		Assert.True(held > limit * 2, $"held {held} ticks");
	}

	[Fact]
	public void AWait_IsCountedOncePerTick_HoweverOftenTheOperatorRuns()
	{
		// FluidHTN can run an operator twice in one tick (#21, §3.2).
		Squaddie unit = Advancing(behind: 12f);
		unit.Tick();
		unit.C.Tick++;
		unit.C.Perform(UnitGoal.Advance);
		unit.C.Perform(UnitGoal.Advance);
		unit.C.Perform(UnitGoal.Advance);

		Assert.Equal(1, unit.C.WaitedTicks);
	}

	[Fact]
	public void ANewAdvance_WaitsAfreshForItsSquad()
	{
		Squaddie unit = Advancing(behind: 30f);
		unit.Tick(UnitPlanTraits.Default.PaceMaxWaitTicks + 2);
		Assert.True(unit.C.PaceGivenUp);
		Assert.Equal(UnitMove.Point, unit.C.Intent.Move);

		// A fight, then the advance again: a new operator, a new clock.
		unit.See(Objective);
		unit.Tick();
		unit.LoseTarget();
		unit.Tick();

		Assert.Equal(UnitGoal.Advance, unit.Goal);
		Assert.Equal(UnitMove.Hold, unit.C.Intent.Move);
	}

	// ---- facts -------------------------------------------------------------

	private static UnitContext Context()
	{
		var c = new UnitContext(new PooledHtnFactory(), UnitPlanTraits.Default, Rifleman);
		c.Init();
		return c;
	}

	[Fact]
	public void AFreshContext_IsInNoSquadRatherThanGathering()
	{
		Assert.Equal((byte)SquadPhase.Moving, Context().Get(UnitFact.SquadPhase));
	}

	[Fact]
	public void Contact_IsOwnOverSideKnownOverNone()
	{
		UnitContext c = Context();
		c.Encode(new UnitSense { SideKnown = true, HealthFraction = 1f });
		Assert.Equal((byte)UnitContact.SideKnown, c.Get(UnitFact.Contact));

		c.Track(Anchor, default, true, Objective);
		Assert.Equal((byte)UnitContact.Own, c.Get(UnitFact.Contact));

		c.Track(Anchor, default, false, Objective);
		Assert.Equal((byte)UnitContact.SideKnown, c.Get(UnitFact.Contact));

		c.Encode(new UnitSense { HealthFraction = 1f });
		Assert.Equal((byte)UnitContact.None, c.Get(UnitFact.Contact));
	}

	[Fact]
	public void NoSquad_ReadsAsMoving_AndASquadsPhaseAsItIs()
	{
		UnitContext c = Context();
		c.Encode(new UnitSense { HasSquad = false, SquadPhase = SquadPhase.Gathering, HealthFraction = 1f });
		Assert.Equal((byte)SquadPhase.Moving, c.Get(UnitFact.SquadPhase));

		c.Encode(new UnitSense { HasSquad = true, SquadPhase = SquadPhase.Gathering, HealthFraction = 1f });
		Assert.Equal((byte)SquadPhase.Gathering, c.Get(UnitFact.SquadPhase));
	}

	[Theory]
	[InlineData(0.59f, HealthBand.Ok, HealthBand.Ok)]
	[InlineData(0.54f, HealthBand.Ok, HealthBand.Hurt)]
	[InlineData(0.29f, HealthBand.Hurt, HealthBand.Critical)]
	[InlineData(0.32f, HealthBand.Critical, HealthBand.Critical)]
	[InlineData(0.36f, HealthBand.Critical, HealthBand.Hurt)]
	public void HealthBands_AreTheGroundBotsWithTheirHysteresis(float fraction, HealthBand previous,
		HealthBand expected)
	{
		UnitContext c = Context();
		c.Sense(UnitFact.Health, (byte)previous);
		c.Encode(new UnitSense { HealthFraction = fraction });

		Assert.Equal((byte)expected, c.Get(UnitFact.Health));
	}

	[Fact]
	public void Stance_IsAutonomousOnlyForAComputerStrategistsOrder()
	{
		UnitContext c = Context();
		c.Encode(new UnitSense { Autonomous = true, HealthFraction = 1f });
		Assert.Equal((byte)UnitStance.Autonomous, c.Get(UnitFact.Stance));

		c.Encode(new UnitSense { Autonomous = false, HealthFraction = 1f });
		Assert.Equal((byte)UnitStance.Obey, c.Get(UnitFact.Stance));
	}

	[Theory]
	[InlineData(OrderKind.Defend, 15f, true)]
	[InlineData(OrderKind.Defend, 25f, false)]
	[InlineData(OrderKind.None, 15f, true)]
	[InlineData(OrderKind.Attack, 15f, false)]
	[InlineData(OrderKind.Move, 15f, false)]
	public void InLeash_IsTodaysLeashTest_ForDefendingAndIdleUnitsOnly(OrderKind kind, float targetMeters,
		bool expected)
	{
		UnitContext c = Context();
		var order = new UnitOrder { Kind = kind, Target = Anchor, Anchor = Anchor };
		Vector3 target = Anchor + new Vector3(targetMeters, 0f, 0f);
		c.Track(Anchor + new Vector3(2f, 0f, 0f), order, true, target);

		Assert.Equal(expected, c.Is(UnitFact.InLeash));
		if (kind == OrderKind.Defend)
		{
			// The fact and today's destination agree on who is chased.
			Assert.Equal(expected, UnitBrain.Destination(order, c.Position, true, target, Rifleman, out _) == target);
		}
	}

	[Fact]
	public void InLeash_WithNoTarget_IsMeasuredOnTheFightAFriendIsIn()
	{
		UnitContext c = Context();
		var order = new UnitOrder { Kind = OrderKind.Defend, Target = Anchor, Anchor = Anchor };
		c.HasSupport = true;
		c.SupportPoint = Anchor + new Vector3(12f, 0f, 0f);
		c.Track(Anchor, order, false, Vector3.Zero);
		Assert.False(c.Is(UnitFact.InLeash));

		c.Encode(new UnitSense { FriendEngaged = true, HealthFraction = 1f });
		Assert.True(c.Is(UnitFact.InLeash));
	}

	[Fact]
	public void RescanningAnUnchangedSituation_DoesNotDirtyTheContext()
	{
		UnitContext c = Context();
		var order = new UnitOrder { Kind = OrderKind.Defend, Target = Anchor, Anchor = Anchor };
		var sense = new UnitSense { SideKnown = true, HealthFraction = 0.5f, Odds = OddsBand.Even, HasSquad = true };
		c.Encode(sense);
		c.Track(Anchor, order, true, Anchor + new Vector3(5f, 0f, 0f));
		c.IsDirty = false;

		c.Encode(sense);
		c.Track(Anchor + new Vector3(0.5f, 0f, 0f), order, true, Anchor + new Vector3(5.5f, 0f, 0f));

		Assert.False(c.IsDirty);
	}

	// ---- sharing and allocation --------------------------------------------

	/// <summary>Fourteen situations, one per operator of §5.2 and two for Advance, as an order and facts.</summary>
	private static void Situation(UnitContext c, int phase, out OrderKind order, out bool hasTarget)
	{
		order = phase switch
		{
			0 or 4 or 5 or 6 or 7 or 13 => OrderKind.Attack,
			1 or 2 => OrderKind.Build,
			3 => OrderKind.Attack,
			8 or 9 or 10 => OrderKind.Defend,
			11 => OrderKind.Patrol,
			_ => OrderKind.Move,
		};
		hasTarget = phase is 5 or 8;
		c.Sense(UnitFact.Stance, (byte)(phase is 0 or 13 ? UnitStance.Autonomous : UnitStance.Obey));
		c.Sense(UnitFact.Health, (byte)(phase == 0 ? HealthBand.Critical : HealthBand.Ok));
		c.Sense(UnitFact.Odds, (byte)(phase == 0 ? OddsBand.Outnumbered : OddsBand.Even));
		c.Sense(UnitFact.UnderFire, phase == 1);
		c.Sense(UnitFact.FriendNear, phase == 1);
		c.Sense(UnitFact.SquadPhase, (byte)(phase == 4 ? SquadPhase.Gathering : SquadPhase.Moving));
		c.Sense(UnitFact.FriendEngaged, phase is 6 or 9);
		c.Sense(UnitFact.Wounded, phase == 13);
		c.Sense(UnitFact.Supply, phase is 0 or 13);
		c.Sense(UnitFact.Supplied, false);
	}

	/// <summary>One unit's tick; true when what it was told dirtied its context.</summary>
	private static bool Step(UnitPlanning planning, UnitContext c, uint t, int i, int interval,
		ref OrderKind order, ref bool hasTarget)
	{
		c.Tick = t;
		if ((t + i) % interval == 0)
		{
			Situation(c, (int)(((t + i) / (uint)interval + (uint)i) % 14), out order, out hasTarget);
		}

		c.Track(c.Position, new UnitOrder { Kind = order, Target = Objective, Anchor = Anchor }, hasTarget,
			Anchor + new Vector3(0f, 0.5f, 5f));
		bool dirty = c.IsDirty;
		planning.Tick(c);
		return dirty;
	}

	private static (UnitContext[] contexts, OrderKind[] orders, bool[] targets) Population(UnitPlanning planning,
		int agents)
	{
		var contexts = new UnitContext[agents];
		for (int i = 0; i < agents; i++)
		{
			// Inside the leash round the anchor, and for three in five, far enough ahead
			// of the squad on the way to the objective to wait for it.
			UnitContext c = planning.CreateContext(Rifleman);
			c.Position = Anchor + new Vector3(i * 0.1f, 0f, 3f);
			c.HasSquad = true;
			c.SquadAlive = 4;
			c.SquadCentroid = Anchor + new Vector3(0f, 0f, 3f - (i % 5 * 5f));
			c.HasSupport = true;
			c.SupportPoint = Anchor + new Vector3(3f, 0f, 3f);
			c.HasPost = true;
			c.PostPoint = Anchor + new Vector3(4f, 0f, 0f);
			c.SupplyPoint = Supply;
			contexts[i] = c;
		}

		return (contexts, new OrderKind[agents], new bool[agents]);
	}

	private static ulong[] Traces(int agents, int ticks)
	{
		var planning = new UnitPlanning(UnitPlanTraits.Default);
		(UnitContext[] contexts, OrderKind[] orders, bool[] targets) = Population(planning, agents);
		var hashes = new ulong[agents];
		Array.Fill(hashes, 14695981039346656037ul);

		for (uint t = 0; t < ticks; t++)
		{
			for (int i = 0; i < agents; i++)
			{
				Step(planning, contexts[i], t, i, 10, ref orders[i], ref targets[i]);
				hashes[i] = (hashes[i] ^ ((uint)contexts[i].Intent.Goal | ((uint)contexts[i].Intent.Move << 8)))
					* 1099511628211ul;
			}
		}

		return hashes;
	}

	[Fact]
	public void OneDomainForEveryUnit_IsDeterministic_AndNotTrivial()
	{
		ulong[] first = Traces(16, 2000);

		Assert.Equal(first, Traces(16, 2000));
		Assert.True(first.Distinct().Count() > 8);
	}

	[Fact]
	public void EverySituation_PlansTheOperatorItIsFor()
	{
		var planning = new UnitPlanning(UnitPlanTraits.Default);
		(UnitContext[] contexts, _, _) = Population(planning, 1);
		UnitContext c = contexts[0];
		UnitGoal[] expected =
		{
			UnitGoal.Retreat, UnitGoal.TakeCover, UnitGoal.Work, UnitGoal.Advance, UnitGoal.WaitForSquad,
			UnitGoal.EngageFocus, UnitGoal.Support, UnitGoal.Advance, UnitGoal.EngageInLeash, UnitGoal.AnswerCall,
			UnitGoal.HoldPost, UnitGoal.Patrol, UnitGoal.Obey, UnitGoal.Resupply,
		};

		for (int phase = 0; phase < expected.Length; phase++)
		{
			planning.Reset(c);
			Situation(c, phase, out OrderKind order, out bool hasTarget);
			c.Track(c.Position, new UnitOrder { Kind = order, Target = Objective, Anchor = Anchor }, hasTarget,
				Anchor + new Vector3(0f, 0.5f, 5f));
			planning.Tick(c);

			Assert.Equal(expected[phase], c.Intent.Goal);
		}
	}

	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public void PlanningEveryUnit_AllocatesNothingOnTheTick_WithThePooledFactory(bool pooled)
	{
		// The unpooled row is the guard's own check: FluidHTN's DefaultFactory
		// allocates on every replan, so a measurement that saw nothing there would be
		// seeing nothing at all.
		var planning = new UnitPlanning(UnitPlanTraits.Default, pooled ? null : new DefaultFactory());
		(UnitContext[] contexts, OrderKind[] orders, bool[] targets) = Population(planning, SimConfig.MaxUnits);

		int dirtied = 0;
		void Run(uint from, uint ticks, int interval)
		{
			for (uint t = from; t < from + ticks; t++)
			{
				for (int i = 0; i < contexts.Length; i++)
				{
					dirtied += Step(planning, contexts[i], t, i, interval, ref orders[i], ref targets[i]) ? 1 : 0;
				}
			}
		}

		Run(0, 3000, 1);
		dirtied = 0;
		long before = GC.GetAllocatedBytesForCurrentThread();
		Run(3000, 3600, 10);
		Run(6600, 3600, 1);
		long bytes = GC.GetAllocatedBytesForCurrentThread() - before;

		Assert.True(dirtied > 100_000, $"only {dirtied} fact changes dirtied a context");
		if (pooled)
		{
			Assert.Equal(0, bytes);
		}
		else
		{
			Assert.True(bytes > 0);
		}
	}
}
