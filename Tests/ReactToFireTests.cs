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
/// React to fire (docs/TACTICS.md §5.1, T1): a unit fired upon takes cover from where the
/// fire came from and fires back from it; only with micro-autonomy on, only with a
/// doctrine, never under a build, a walk to a vehicle or an unload, never in a squad
/// falling back; under an order that goes somewhere for 8 s at most until the next hit;
/// pre-empting what it was doing, pre-empted by Retreat, and every operator ending when
/// its premise goes.
/// </summary>
public class ReactToFireTests
{
	private static readonly UnitTraits Rifleman = new(45f, 40f, 1.5f, 20f, UnitDoctrine.Rifleman);

	private static readonly Vector3 Anchor = new(10f, 0.5f, 10f);
	private static readonly Vector3 Objective = new(10f, 0.5f, 110f);

	/// <summary>Where the fire comes from: 35 m off, unseen, inside a rifleman's 40 m engage range, so it answers.</summary>
	private static readonly Vector3 Shooter = new(45f, 2f, 10f);

	/// <summary>A spot behind cover from there, two metres off.</summary>
	private static readonly Vector3 Spot = Anchor + new Vector3(2f, 0f, 0f);

	private const int Second = SimConfig.TickRate;

	private sealed class Rig
	{
		public readonly Domain<UnitContext> Domain;
		public readonly Planner<UnitContext> Planner = new();
		public readonly UnitContext C;
		public readonly List<string> Plans = new();
		public int Replacements;

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
			C.PlannerState.OnReplacePlan = (_, _, plan) =>
			{
				Replacements++;
				Plans.Add(Names(plan));
			};

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

		/// <summary>A round from <see cref="Shooter"/> on this tick.</summary>
		public void Round(FireKind kind = FireKind.NearMiss) =>
			FiredUpon.Record(ref Fire, kind, Shooter, OwnerId.ForPeer(7), C.Tick);

		/// <summary>One scan that found a spot behind cover from the fire at <paramref name="spot"/>, or none.</summary>
		public void Scan(Vector3? spot, bool autonomous = false, OddsBand odds = OddsBand.Even,
			float health = 1f, SquadPhase phase = SquadPhase.Moving)
		{
			C.HasFireCoverPoint = spot.HasValue;
			C.FireCoverPoint = spot ?? Vector3.Zero;
			C.Encode(new UnitSense
			{
				HealthFraction = health,
				Autonomous = autonomous,
				Odds = odds,
				HasSquad = phase != SquadPhase.Moving,
				SquadPhase = phase,
				HasFireCover = spot.HasValue,
				FireCoverHides = spot.HasValue,
			});
		}

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

		/// <summary>Ticks with a round arriving on every <paramref name="every"/>-th.</summary>
		public void UnderFire(int ticks, int every = 10, FireKind kind = FireKind.NearMiss)
		{
			for (int i = 0; i < ticks; i++)
			{
				if (i % every == 0)
				{
					Round(kind);
				}

				Tick();
			}
		}

		public UnitGoal Goal => C.Intent.Goal;
	}

	private static string Names(Queue<ITask> plan) => "[" + string.Join(", ", plan.Select(t => t.Name)) + "]";

	private static Rig FiredUponHolding(MicroAutonomy autonomy = MicroAutonomy.On)
	{
		var unit = new Rig { Autonomy = autonomy };
		unit.Order(OrderKind.None, Anchor);
		unit.Tick();
		unit.Round();
		unit.Scan(Spot);
		unit.Tick();
		return unit;
	}

	// ---- the rows of §5.1 -----------------------------------------------------

	[Fact]
	public void AUnitFiredUponFromOutOfSight_RunsForCover_FiringBack_ThenHoldsThereFiringBack()
	{
		Rig unit = FiredUponHolding();

		Assert.Equal("[TakeCoverFromFire]", unit.Plans[^1]);
		Assert.Equal(UnitMove.Point, unit.C.Intent.Move);
		Assert.Equal(Spot, unit.C.Intent.Point);
		Assert.False(unit.C.Intent.StopToFight);
		Assert.True(unit.C.Intent.FireAt);
		Assert.Equal(Shooter, unit.C.Intent.FirePoint);

		unit.C.Position = Spot;
		unit.Tick();

		Assert.Equal(UnitGoal.ReturnFire, unit.Goal);
		Assert.Equal(UnitMove.Hold, unit.C.Intent.Move);
		Assert.True(unit.C.Intent.StopToFight);
		Assert.True(unit.C.Intent.FireAt);
	}

	[Fact]
	public void ItFiresBackForTwoSecondsAfterARound_ThenHolds_ThenGoesBackToItsOrderWhenTheWindowCloses()
	{
		Rig unit = FiredUponHolding();
		unit.C.Position = Spot;
		unit.Tick();

		unit.Tick((2 * Second) - 3);
		Assert.Equal(UnitGoal.ReturnFire, unit.Goal);
		Assert.True(unit.C.Intent.FireAt);

		unit.Tick(5);
		Assert.Equal(UnitGoal.ReturnFire, unit.Goal);
		Assert.False(unit.C.Intent.FireAt);

		unit.Tick(Second);
		Assert.Equal(UnitGoal.HoldPost, unit.Goal);
	}

	[Fact]
	public void InItsSpot_ItCrouchesBetweenBursts_AndStandsToFireBack()
	{
		// docs/TACTICS.md §5.8: up for the two seconds it answers a round, down after.
		Rig unit = FiredUponHolding();
		Assert.Equal(UnitGoal.TakeCoverFromFire, unit.Goal);
		Assert.False(unit.C.Intent.Crouch);

		unit.C.Position = Spot;
		unit.Tick();
		Assert.Equal(UnitGoal.ReturnFire, unit.Goal);
		Assert.True(unit.C.Intent.FireAt);
		Assert.False(unit.C.Intent.Crouch);

		unit.Tick((2 * Second) + 2);
		Assert.Equal(UnitGoal.ReturnFire, unit.Goal);
		Assert.False(unit.C.Intent.FireAt);
		Assert.True(unit.C.Intent.Crouch);

		unit.Round();
		unit.Tick();
		Assert.True(unit.C.Intent.FireAt);
		Assert.False(unit.C.Intent.Crouch);
	}

	[Fact]
	public void AShooterBeyondItsEngageRange_IsHiddenFrom_NotAnswered_SoItStaysDown()
	{
		var unit = new Rig();
		unit.Order(OrderKind.None, Anchor);
		unit.Tick();
		FiredUpon.Record(ref unit.Fire, FireKind.NearMiss, Anchor + new Vector3(55f, 1.5f, 0f), OwnerId.ForPeer(7),
			unit.C.Tick);
		unit.Scan(Spot);
		unit.Tick();
		Assert.Equal(UnitGoal.TakeCoverFromFire, unit.Goal);
		Assert.False(unit.C.Intent.FireAt);

		unit.C.Position = Spot;
		unit.Tick();
		Assert.Equal(UnitGoal.ReturnFire, unit.Goal);
		Assert.False(unit.C.Intent.FireAt);
		Assert.True(unit.C.Intent.Crouch);
	}

	[Fact]
	public void InItsSpot_WithATargetOfItsOwn_ItStandsToFightIt()
	{
		// Under a move, a unit with a target of its own still reacts (it shoots on the
		// walk); in its spot it does not crouch away from that target.
		var unit = new Rig { HasTarget = true, TargetPoint = Shooter };
		unit.Order(OrderKind.Move, Objective);
		unit.Tick();
		unit.Round();
		unit.Scan(Spot);
		unit.C.Position = Spot;
		unit.Tick();
		unit.Tick((2 * Second) + 2);

		Assert.Equal(UnitGoal.ReturnFire, unit.Goal);
		Assert.False(unit.C.Intent.FireAt);
		Assert.False(unit.C.Intent.Crouch);
	}

	[Fact]
	public void WithMicroAutonomyOff_ItDoesExactlyWhatItDidBefore()
	{
		Rig off = FiredUponHolding(MicroAutonomy.Off);
		var quiet = new Rig { Autonomy = MicroAutonomy.Off };
		quiet.Order(OrderKind.None, Anchor);
		quiet.Tick(2);

		Assert.Equal(quiet.Goal, off.Goal);
		Assert.Equal(quiet.C.Intent.Move, off.C.Intent.Move);
		Assert.False(off.C.Intent.FireAt);
		Assert.DoesNotContain(off.Plans, p => p.Contains("Fire"));
	}

	[Fact]
	public void WithNoSpotInReach_ItDoesNotReact()
	{
		var unit = new Rig();
		unit.Order(OrderKind.None, Anchor);
		unit.Round();
		unit.Scan(null);
		unit.Tick();

		Assert.Equal(UnitGoal.HoldPost, unit.Goal);
	}

	[Fact]
	public void AUnitWithNoDoctrine_DoesNotReact()
	{
		var truck = new Rig(new UnitTraits(40f, 30f, 2.5f, 20f, UnitDoctrine.None));
		truck.Order(OrderKind.None, Anchor);
		truck.Round();
		truck.Scan(Spot);
		truck.Tick();

		Assert.Equal(UnitGoal.HoldPost, truck.Goal);
	}

	[Theory]
	[InlineData(OrderKind.Build, UnitGoal.Work)]
	[InlineData(OrderKind.Embark, UnitGoal.Obey)]
	[InlineData(OrderKind.Unload, UnitGoal.Obey)]
	public void UnderABuild_AWalkToAVehicle_OrAnUnload_ItDoesNotReact(OrderKind order, UnitGoal goal)
	{
		var unit = new Rig();
		unit.Order(order, Objective);
		unit.Round();
		unit.Scan(Spot);
		unit.Tick();

		Assert.Equal(goal, unit.Goal);
	}

	[Theory]
	[InlineData(OrderKind.Move, UnitGoal.TakeCoverFromFire)]
	[InlineData(OrderKind.Patrol, UnitGoal.TakeCoverFromFire)]
	[InlineData(OrderKind.Attack, UnitGoal.EngageFocus)]
	[InlineData(OrderKind.Defend, UnitGoal.EngageInLeash)]
	public void WithATargetOfItsOwn_ItReactsOnlyUnderAnOrderWithNoFightOfItsOwn(OrderKind order, UnitGoal goal)
	{
		// A move and a patrol shoot on the walk, with nothing to fight from: they take cover.
		// Every other order fights a target it can see from cover against it (COVER.md §6).
		var unit = new Rig();
		unit.Order(order, order == OrderKind.Defend ? Anchor : Objective);
		unit.HasTarget = true;
		unit.TargetPoint = Anchor + new Vector3(0f, 0.5f, 15f);
		unit.Round();
		unit.Scan(Spot);
		unit.Tick();

		Assert.Equal(goal, unit.Goal);
	}

	[Fact]
	public void AUnitInASquadFallingBack_DoesNotStopToTakeCover()
	{
		var unit = new Rig();
		unit.Order(OrderKind.Move, Objective);
		unit.Round();
		unit.Scan(Spot, phase: SquadPhase.FallingBack);
		unit.Tick();

		Assert.Equal(UnitGoal.Obey, unit.Goal);
	}

	// ---- pre-emption ----------------------------------------------------------

	[Theory]
	[InlineData(OrderKind.Move, UnitGoal.Obey)]
	[InlineData(OrderKind.Attack, UnitGoal.Advance)]
	[InlineData(OrderKind.Defend, UnitGoal.HoldPost)]
	public void FireArriving_PreemptsWhatItWasDoing_OnTheTick(OrderKind order, UnitGoal before)
	{
		var unit = new Rig();
		unit.Order(order, order == OrderKind.Defend ? Anchor : Objective);
		unit.Scan(Spot);
		unit.Tick(3);
		Assert.Equal(before, unit.Goal);

		unit.Round();
		unit.Tick();

		Assert.Equal(UnitGoal.TakeCoverFromFire, unit.Goal);
		Assert.Equal(1, unit.Replacements);
	}

	[Fact]
	public void SwitchingItOn_UnderFire_Preempts_AndSwitchingItOff_EndsTheReactionOnTheTick()
	{
		Rig unit = FiredUponHolding(MicroAutonomy.Off);
		var order = new UnitOrder { Kind = OrderKind.Move, Target = Objective, Anchor = Anchor };
		unit.C.Track(unit.C.Position, order, false, Vector3.Zero);
		unit.Tick();
		Assert.Equal(UnitGoal.Obey, unit.Goal);

		unit.Autonomy = MicroAutonomy.On;
		unit.Round();
		unit.Tick();
		Assert.Equal(UnitGoal.TakeCoverFromFire, unit.Goal);

		unit.Autonomy = MicroAutonomy.Off;
		unit.Tick();
		Assert.Equal(UnitGoal.Obey, unit.Goal);
	}

	[Fact]
	public void Retreat_OutranksTakingCover()
	{
		var unit = new Rig();
		unit.Order(OrderKind.Attack, Objective);
		unit.Round();
		unit.Scan(Spot, autonomous: true, odds: OddsBand.Outnumbered, health: 0.2f);
		unit.Tick();

		Assert.Equal(UnitGoal.Retreat, unit.Goal);
	}

	// ---- the cap ----------------------------------------------------------------

	[Fact]
	public void UnderAMove_ItReactsForEightSecondsInAll_ThenGoesOn_UntilItIsNextHit()
	{
		var unit = new Rig();
		unit.Order(OrderKind.Move, Objective);
		unit.Scan(Spot);
		unit.UnderFire(8 * Second - 5);
		Assert.Equal(UnitGoal.TakeCoverFromFire, unit.Goal);
		Assert.False(unit.C.Is(UnitFact.ReactSpent));

		unit.UnderFire(10);
		Assert.True(unit.C.Is(UnitFact.ReactSpent));
		Assert.Equal(UnitGoal.Obey, unit.Goal);

		// Near misses keep coming: it keeps going.
		unit.UnderFire(3 * Second);
		Assert.Equal(UnitGoal.Obey, unit.Goal);

		// A hit is a reason to take cover again.
		unit.Round(FireKind.Hit);
		unit.Tick();
		Assert.False(unit.C.Is(UnitFact.ReactSpent));
		Assert.Equal(UnitGoal.TakeCoverFromFire, unit.Goal);
	}

	[Fact]
	public void TheCap_CountsAcrossItsReactions_AndANewOrderStartsItOver()
	{
		var planning = new UnitPlanning(UnitPlanTraits.Default);
		var unit = new Rig();
		unit.Order(OrderKind.Attack, Objective);
		unit.Scan(Spot);
		unit.UnderFire(2 * Second);

		// The window closes 3 s after the last round, at tick 110: back to the order, with
		// the 290 ticks it reacted for spent — the window counts, it was in cover for it.
		unit.Tick(4 * Second);
		Assert.Equal(UnitGoal.Advance, unit.Goal);
		Assert.InRange(unit.C.ReactedTicks, 110 + FiredUpon.WindowTicks - 2, 110 + FiredUpon.WindowTicks + 2);

		// Another 200 ticks of it, and the 480 are spent.
		unit.UnderFire(200);
		Assert.True(unit.C.Is(UnitFact.ReactSpent));
		Assert.Equal(UnitGoal.Advance, unit.Goal);

		// A new order (UnitPlanning.Reset): it starts over.
		planning.Reset(unit.C);
		Assert.Equal(0, unit.C.ReactedTicks);
		Assert.False(unit.C.Is(UnitFact.ReactSpent));
	}

	[Fact]
	public void UnderADefendOrder_ThereIsNoCap()
	{
		var unit = new Rig();
		unit.Order(OrderKind.Defend, Anchor);
		unit.Scan(Spot);
		unit.UnderFire(20 * Second);

		Assert.False(unit.C.Is(UnitFact.ReactSpent));
		Assert.Equal(UnitGoal.TakeCoverFromFire, unit.Goal);
	}

	// ---- premises ---------------------------------------------------------------

	public static IEnumerable<object[]> Premises()
	{
		foreach (UnitGoal goal in new[] { UnitGoal.TakeCoverFromFire, UnitGoal.ReturnFire })
		{
			yield return new object[] { goal, "the window closes", UnitGoal.HoldPost };
			yield return new object[] { goal, "the spot goes", UnitGoal.HoldPost };
			yield return new object[] { goal, "micro-autonomy off", UnitGoal.HoldPost };
			yield return new object[] { goal, "a target of its own", UnitGoal.EngageInLeash };
			yield return new object[] { goal, "its squad falls back", UnitGoal.HoldPost };
			yield return new object[] { goal, "a build order", UnitGoal.Work };
		}
	}

	[Theory]
	[MemberData(nameof(Premises))]
	public void EveryReaction_EndsWhenItsPremiseGoes(UnitGoal goal, string change, UnitGoal next)
	{
		var unit = new Rig();
		unit.Order(OrderKind.Defend, Anchor);
		if (goal == UnitGoal.ReturnFire)
		{
			unit.C.Position = Spot;
		}

		unit.Round();
		unit.Scan(Spot);
		unit.Tick();
		Assert.Equal(goal, unit.Goal);

		switch (change)
		{
			case "the window closes":
				unit.Tick(FiredUpon.WindowTicks + 1);
				break;
			case "the spot goes":
				unit.Scan(null);
				unit.Tick();
				break;
			case "micro-autonomy off":
				unit.Autonomy = MicroAutonomy.Off;
				unit.Tick();
				break;
			case "a target of its own":
				unit.HasTarget = true;
				unit.TargetPoint = Anchor + new Vector3(0f, 0.5f, 5f);
				unit.Tick();
				break;
			case "its squad falls back":
				unit.Scan(Spot, phase: SquadPhase.FallingBack);
				unit.Tick();
				break;
			case "a build order":
				unit.C.Track(unit.C.Position, new UnitOrder { Kind = OrderKind.Build, Target = Objective }, false,
					Vector3.Zero);
				unit.Tick();
				break;
		}

		Assert.Equal(next, unit.Goal);
	}

	[Fact]
	public void WithoutExecutingConditions_AReactionOutlivesItsPremise()
	{
		// §3.4, P3, on the new branch: the executing conditions are what end it.
		var unit = new Rig(executingConditions: false);
		unit.Order(OrderKind.Defend, Anchor);
		unit.Round();
		unit.Scan(Spot);
		unit.Tick();
		Assert.Equal(UnitGoal.TakeCoverFromFire, unit.Goal);

		unit.Autonomy = MicroAutonomy.Off;
		unit.Tick(FiredUpon.WindowTicks + 10);
		Assert.Equal(UnitGoal.TakeCoverFromFire, unit.Goal);
	}
}
