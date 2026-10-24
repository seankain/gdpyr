using System.Collections.Generic;
using System.Linq;
using FluidHTN;
using Gdpyr.Sim;
using Gdpyr.Sim.Htn;
using Godot;
using Xunit;

namespace Gdpyr.Tests;

/// <summary>
/// A grenade at a hidden enemy (docs/TACTICS.md §5.4, T2), the planner's half: a unit
/// whose scan found one stands for one throw and goes back to what it was doing; only
/// with micro-autonomy on, only with a doctrine, never with a target of its own, under a
/// build, a walk to a vehicle or an unload, or in a squad falling back; pre-empting what
/// it was doing, pre-empted by Retreat, and ending when its premise goes. Where the
/// enemy is and whether an arc reaches is the engine's (<c>GrenadeTests</c>).
/// </summary>
public class ThrowGrenadeTests
{
	private static readonly UnitTraits Rifleman = new(45f, 40f, 1.5f, 20f, UnitDoctrine.Rifleman);

	private static readonly Vector3 Anchor = new(10f, 0.5f, 10f);

	/// <summary>Where the hidden enemy is reckoned to be: 18 m off, behind a wall.</summary>
	private static readonly Vector3 Hidden = Anchor + new Vector3(0f, 0f, 18f);

	private sealed class Rig
	{
		public readonly Domain<UnitContext> Domain;
		public readonly Planner<UnitContext> Planner = new();
		public readonly UnitContext C;
		public readonly List<string> Plans = new();

		public MicroAutonomy Autonomy = MicroAutonomy.On;
		public bool HasTarget;

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
		}, HasTarget, Hidden);

		/// <summary>One scan: whether it found a hidden enemy to throw at.</summary>
		public void Scan(bool grenade, OddsBand odds = OddsBand.Even, float health = 1f, bool autonomous = false,
			SquadPhase phase = SquadPhase.Moving)
		{
			C.Encode(new UnitSense
			{
				HealthFraction = health,
				Odds = odds,
				Autonomous = autonomous,
				HasSquad = phase != SquadPhase.Moving,
				SquadPhase = phase,
			});
			C.SenseGrenade(grenade, Hidden);
		}

		public void Tick(int times = 1)
		{
			for (int i = 0; i < times; i++)
			{
				C.Tick++;
				C.SenseAutonomy(Autonomy);
				C.Track(C.Position, C.Order, HasTarget, Hidden);
				Planner.Tick(Domain, C);
			}
		}

		public UnitGoal Goal => C.Intent.Goal;
	}

	private static string Names(Queue<ITask> plan) => "[" + string.Join(", ", plan.Select(t => t.Name)) + "]";

	private static Rig Holding(MicroAutonomy autonomy = MicroAutonomy.On, UnitTraits? traits = null)
	{
		var unit = new Rig(traits) { Autonomy = autonomy };
		unit.Order(OrderKind.None, Anchor);
		unit.Tick();
		return unit;
	}

	[Fact]
	public void AUnitWithAHiddenEnemyToThrowAt_StandsForOneThrow_ThenGoesBackToWhatItWasDoing()
	{
		Rig unit = Holding();
		Assert.Equal(UnitGoal.HoldPost, unit.Goal);

		unit.Scan(grenade: true);
		unit.Tick();

		Assert.Equal("[ThrowGrenade]", unit.Plans[^1]);
		Assert.Equal(UnitGoal.ThrowGrenade, unit.Goal);
		Assert.True(unit.C.Intent.ThrowAt);
		Assert.Equal(Hidden, unit.C.Intent.ThrowPoint);
		Assert.Equal(UnitMove.Hold, unit.C.Intent.Move);
		Assert.True(UnitContext.IsTactic(UnitGoal.ThrowGrenade));

		// The engine has tried it: nothing to throw at until the next scan.
		unit.C.Threw();
		unit.Tick();
		Assert.Equal(UnitGoal.HoldPost, unit.Goal);
		Assert.False(unit.C.Intent.ThrowAt);
	}

	[Fact]
	public void WithMicroAutonomyOff_ItDoesExactlyWhatItDidBefore()
	{
		Rig off = Holding(MicroAutonomy.Off);
		off.Scan(grenade: true);
		off.Tick(3);

		Rig quiet = Holding(MicroAutonomy.Off);
		quiet.Scan(grenade: false);
		quiet.Tick(3);

		Assert.Equal(quiet.Goal, off.Goal);
		Assert.Equal(quiet.C.Intent.Move, off.C.Intent.Move);
		Assert.False(off.C.Intent.ThrowAt);
		Assert.DoesNotContain(off.Plans, p => p.Contains("Grenade"));
	}

	[Fact]
	public void AUnitWithNoDoctrine_DoesNotThrow()
	{
		Rig builder = Holding(traits: new UnitTraits(45f, 0f, 1.5f, 20f));
		builder.Scan(grenade: true);
		builder.Tick();

		Assert.NotEqual(UnitGoal.ThrowGrenade, builder.Goal);
		Assert.False(builder.C.Intent.ThrowAt);
	}

	[Fact]
	public void WithATargetOfItsOwn_ItShootsIt_AndATargetSeenMidThrowEndsTheThrow()
	{
		// It fights its target as it would have with nothing to throw at, from the first tick.
		var unit = new Rig { HasTarget = true };
		unit.Order(OrderKind.None, Anchor);
		unit.Scan(grenade: true);
		unit.Tick(2);

		var fighting = new Rig { HasTarget = true };
		fighting.Order(OrderKind.None, Anchor);
		fighting.Scan(grenade: false);
		fighting.Tick(2);

		Assert.NotEqual(UnitGoal.None, fighting.Goal);
		Assert.Equal(fighting.Goal, unit.Goal);
		Assert.DoesNotContain(unit.Plans, p => p.Contains("Grenade"));

		Rig throwing = Holding();
		throwing.Scan(grenade: true);
		throwing.Tick();
		Assert.Equal(UnitGoal.ThrowGrenade, throwing.Goal);

		throwing.HasTarget = true;
		throwing.Tick();
		Assert.NotEqual(UnitGoal.ThrowGrenade, throwing.Goal);
		Assert.False(throwing.C.Intent.ThrowAt);
	}

	[Theory]
	[InlineData(OrderKind.Build)]
	[InlineData(OrderKind.Embark)]
	[InlineData(OrderKind.Unload)]
	public void UnderABuild_AWalkToAVehicle_OrAnUnload_ItDoesNotThrow(OrderKind order)
	{
		var unit = new Rig();
		unit.Order(order, Anchor + new Vector3(5f, 0f, 0f));
		unit.Scan(grenade: true);
		unit.Tick();

		Assert.NotEqual(UnitGoal.ThrowGrenade, unit.Goal);
		Assert.False(unit.C.Intent.ThrowAt);
	}

	[Fact]
	public void InASquadFallingBack_ItDoesNotStopToThrow()
	{
		var unit = new Rig();
		unit.Order(OrderKind.Move, new Vector3(-30f, 0.5f, 10f));
		unit.Scan(grenade: true, phase: SquadPhase.FallingBack);
		unit.Tick();

		Assert.Equal(UnitGoal.Obey, unit.Goal);
	}

	[Theory]
	[InlineData(OrderKind.Move, UnitGoal.Obey)]
	[InlineData(OrderKind.Defend, UnitGoal.HoldPost)]
	[InlineData(OrderKind.Patrol, UnitGoal.Patrol)]
	public void AThrow_PreemptsWhatItWasDoing_OnTheTick_AndItsOrderResumes(OrderKind order, UnitGoal before)
	{
		var unit = new Rig();
		unit.Order(order, Anchor + new Vector3(30f, 0f, 0f));
		unit.Tick(2);
		Assert.Equal(before, unit.Goal);

		unit.Scan(grenade: true);
		unit.Tick();
		Assert.Equal(UnitGoal.ThrowGrenade, unit.Goal);

		unit.C.Threw();
		unit.Tick();
		Assert.Equal(before, unit.Goal);
	}

	[Fact]
	public void SwitchingMicroAutonomyOff_EndsTheThrowOnTheTick()
	{
		Rig unit = Holding();
		unit.Scan(grenade: true);
		unit.Tick();
		Assert.Equal(UnitGoal.ThrowGrenade, unit.Goal);

		unit.Autonomy = MicroAutonomy.Off;
		unit.Tick();
		Assert.Equal(UnitGoal.HoldPost, unit.Goal);
		Assert.False(unit.C.Intent.ThrowAt);
	}

	[Fact]
	public void Retreat_OutranksAThrow()
	{
		var unit = new Rig();
		unit.Order(OrderKind.Defend, Anchor);
		unit.Scan(grenade: true, odds: OddsBand.Outnumbered, health: 0.1f, autonomous: true);
		unit.Tick();

		Assert.Equal(UnitGoal.Retreat, unit.Goal);
		Assert.False(unit.C.Intent.ThrowAt);
	}

	[Fact]
	public void WithoutExecutingConditions_AThrowOutlivesItsPremise()
	{
		// The guard's own check: it is the executing conditions that end it.
		var unit = new Rig(executingConditions: false);
		unit.Order(OrderKind.None, Anchor);
		unit.Tick();
		unit.Scan(grenade: true);
		unit.Tick();
		Assert.Equal(UnitGoal.ThrowGrenade, unit.Goal);

		unit.C.Threw();
		unit.Tick();
		Assert.Equal(UnitGoal.ThrowGrenade, unit.Goal);
	}

	[Fact]
	public void TheDebuggerNamesTheFact_AndTheGoalIsAnAttack()
	{
		int fact = (int)UnitFact.Grenade;
		Assert.Equal("Grenade", Gdpyr.Sim.AiDebug.HtnSchema.Unit.FactName(fact));
		Assert.Equal(Gdpyr.Sim.AiDebug.AiTaskFamily.Attack,
			Gdpyr.Sim.AiDebug.AiGoals.Family(Gdpyr.Sim.AiDebug.HtnDomainKind.Unit, (byte)UnitGoal.ThrowGrenade));
		Assert.Equal(20, (int)UnitGoal.ThrowGrenade);
	}
}
