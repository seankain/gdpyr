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
/// A heavy gunner's base of fire without the drill (docs/TACTICS.md §5.2, T5). The scan's
/// half (<see cref="GunnerPlan"/>): the places it may suppress — live contacts, ghosts and
/// where rounds came from, never one the side has forgotten or one that has died — in its
/// gun's reach with a line to them over the cover board, nearest first, and which the next
/// burst goes to. The planner's half: one-second bursts every two seconds at them, the
/// nearest and then the next; the reserve it keeps and the reload it fills its belt with,
/// crouched in its spot; nothing without a place, with a target of its own, with
/// micro-autonomy off, for any other doctrine or under a move; the cap under an attack and
/// its give-up; pre-emption both ways; and every operator ending when its premise goes.
/// </summary>
public class GunnerTests
{
	private static readonly UnitTraits Gunner = new(45f, 45f, 1.5f, 15f, UnitDoctrine.Gunner);
	private static readonly UnitTraits Rifleman = new(45f, 40f, 1.5f, 20f, UnitDoctrine.Rifleman);
	private static readonly UnitTraits Sniper = new(70f, 70f, 1.5f, 20f, UnitDoctrine.Marksman);

	private static readonly Vector3 Anchor = new(10f, 0.5f, 10f);

	/// <summary>Three places to suppress, nearest first: 20, 30 and 40 m off.</summary>
	private static readonly Vector3 Near = Anchor + new Vector3(0f, 0f, 20f);
	private static readonly Vector3 Middle = Anchor + new Vector3(10f, 0f, 28f);
	private static readonly Vector3 Far = Anchor + new Vector3(-12f, 0f, 38f);

	private static readonly Vector3[] One = { Near };
	private static readonly Vector3[] Three = { Near, Middle, Far };
	private static readonly Vector3[] None = Array.Empty<Vector3>();

	private static readonly UnitPlanTraits Traits = UnitPlanTraits.Default;

	private sealed class Rig
	{
		public readonly Domain<UnitContext> Domain;
		public readonly Planner<UnitContext> Planner = new();
		public readonly UnitContext C;
		public readonly List<string> Plans = new();

		public MicroAutonomy Autonomy = MicroAutonomy.On;
		public bool HasTarget;
		public int Rounds = 150;
		public bool Reloading;

		public Rig(UnitTraits? traits = null, bool executingConditions = true)
		{
			var factory = new PooledHtnFactory();
			Domain = UnitDomain.Build(factory, executingConditions);
			C = new UnitContext(factory, Traits, traits ?? Gunner);
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
		}, HasTarget, Near);

		/// <summary>One scan: the places it may suppress, and whether it has a spot behind cover against the nearest.</summary>
		public void Scan(ReadOnlySpan<Vector3> places, CoverState cover = CoverState.None, OddsBand odds = OddsBand.Even,
			float health = 1f, bool autonomous = false, SquadPhase phase = SquadPhase.Moving)
		{
			C.HasCoverPoint = cover != CoverState.None;
			C.CoverPoint = cover == CoverState.In ? C.Position : Anchor + new Vector3(4f, 0f, 2f);
			C.Encode(new UnitSense
			{
				HealthFraction = health,
				Odds = odds,
				Autonomous = autonomous,
				HasSquad = phase != SquadPhase.Moving,
				SquadPhase = phase,
				HasCover = cover != CoverState.None,
				CoverHides = cover != CoverState.None,
			});
			C.SenseSuppress(places);
		}

		public void Tick(int times = 1)
		{
			for (int i = 0; i < times; i++)
			{
				C.Tick++;
				C.SenseAutonomy(Autonomy);
				C.SenseWeapon(Reloading, 0, Rounds);
				C.Track(C.Position, C.Order, HasTarget, Near);
				Planner.Tick(Domain, C);
			}
		}

		/// <summary>Ticks with <see cref="UnitIntent.FireAt"/> on over the next <paramref name="ticks"/>, and the places they were aimed at, in order, one entry a burst.</summary>
		public (int firing, List<Vector3> bursts) Fire(int ticks)
		{
			int firing = 0;
			var bursts = new List<Vector3>();
			bool was = false;
			for (int i = 0; i < ticks; i++)
			{
				Tick();
				bool on = C.Intent.FireAt;
				firing += on ? 1 : 0;
				if (on && (!was || bursts[^1] != C.Intent.FirePoint))
				{
					bursts.Add(C.Intent.FirePoint);
				}

				was = on;
			}

			return (firing, bursts);
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

	private static Vector3 Aim(Vector3 feet) => feet + new Vector3(0f, GunnerPlan.SuppressAimMeters, 0f);

	// ---- the bursts ---------------------------------------------------------

	[Fact]
	public void AGunnerWithAPlaceToSuppress_FiresAOneSecondBurstEveryTwoSeconds_AtItsAimPoint()
	{
		Rig unit = Holding();
		Assert.Equal(UnitGoal.HoldPost, unit.Goal);

		unit.Scan(One);
		unit.Tick();
		Assert.Equal("[SuppressPoint]", unit.Plans[^1]);
		Assert.Equal(UnitGoal.SuppressPoint, unit.Goal);
		Assert.True(UnitContext.IsTactic(UnitGoal.SuppressPoint));

		// Its first burst is at once, and fires for a second.
		Assert.True(unit.C.Intent.FireAt);
		Assert.Equal(Aim(Near), unit.C.Intent.FirePoint);
		Assert.Equal(UnitMove.Hold, unit.C.Intent.Move);
		(int firing, List<Vector3> bursts) = unit.Fire(Traits.SuppressCycleTicks * 3 - 1);

		// Three cycles, the first tick of which was the one above: half of each on.
		Assert.Equal(Traits.SuppressBurstTicks * 3 - 1, firing);
		Assert.Equal(new[] { Aim(Near), Aim(Near), Aim(Near) }, bursts);
		Assert.Equal(UnitGoal.SuppressPoint, unit.Goal);
	}

	[Fact]
	public void EachBurst_GoesToTheNextPlace_TheNearestFirst_AndRoundAgain()
	{
		Rig unit = Holding();
		unit.Scan(Three);
		(_, List<Vector3> bursts) = unit.Fire(Traits.SuppressCycleTicks * 4);

		Assert.Equal(new[] { Aim(Near), Aim(Middle), Aim(Far), Aim(Near) }, bursts);
	}

	[Fact]
	public void APlaceGoneFromTheListMidBurst_IsNotFiredAtAgain()
	{
		Rig unit = Holding();
		unit.Scan(Three);
		unit.Tick(5);
		Assert.Equal(Aim(Near), unit.C.Intent.FirePoint);

		// The side forgot the nearest: the rest of the burst goes to what is left.
		unit.Scan(new[] { Middle, Far });
		unit.Tick();
		Assert.True(unit.C.Intent.FireAt);
		Assert.Equal(Aim(Middle), unit.C.Intent.FirePoint);
	}

	[Fact]
	public void AReplan_DoesNotRestartTheRhythm_ButALullOfACycleDoes()
	{
		Rig unit = Holding();
		unit.Scan(One);
		unit.Tick(Traits.SuppressBurstTicks + 5);
		Assert.Equal(UnitGoal.SuppressPoint, unit.Goal);
		Assert.False(unit.C.Intent.FireAt);

		// A grenade thrown between bursts, and back: the operator begins again, and the
		// gap between bursts stays a gap.
		unit.C.SenseGrenade(true, Near);
		unit.Tick();
		Assert.Equal(UnitGoal.ThrowGrenade, unit.Goal);
		unit.C.Threw();
		unit.Tick();
		Assert.Equal(UnitGoal.SuppressPoint, unit.Goal);
		Assert.False(unit.C.Intent.FireAt);
		// The next burst comes when the cycle says, not before.
		unit.Tick(Traits.SuppressCycleTicks - (Traits.SuppressBurstTicks + 6) - 1);
		Assert.False(unit.C.Intent.FireAt);
		unit.Tick();
		Assert.True(unit.C.Intent.FireAt);

		// Nothing to suppress for longer than a cycle, then a place again: it fires at once.
		unit.Scan(None);
		unit.Tick(Traits.SuppressCycleTicks + 1);
		Assert.Equal(UnitGoal.HoldPost, unit.Goal);
		unit.Scan(One);
		unit.Tick();
		Assert.Equal(UnitGoal.SuppressPoint, unit.Goal);
		Assert.True(unit.C.Intent.FireAt);
	}

	[Fact]
	public void WithNoPlace_ItDoesWhatItDidBefore_AndAListThatEmpties_EndsTheBursts()
	{
		Rig unit = Holding();
		unit.Scan(None);
		unit.Tick(3);
		Assert.Equal(UnitGoal.HoldPost, unit.Goal);
		Assert.DoesNotContain(unit.Plans, p => p.Contains("Suppress"));

		unit.Scan(One);
		unit.Tick();
		Assert.Equal(UnitGoal.SuppressPoint, unit.Goal);

		unit.Scan(None);
		unit.Tick();
		Assert.Equal(UnitGoal.HoldPost, unit.Goal);
		Assert.False(unit.C.Intent.FireAt);
	}

	[Fact]
	public void WithASpotBehindCover_ItWalksToItFiring_AndHoldsInIt()
	{
		Rig unit = Holding();
		unit.Scan(One, CoverState.Near);
		unit.Tick();
		Assert.Equal(UnitGoal.SuppressPoint, unit.Goal);
		Assert.Equal(UnitMove.Point, unit.C.Intent.Move);
		Assert.Equal(unit.C.CoverPoint, unit.C.Intent.Point);
		Assert.False(unit.C.Intent.StopToFight);
		Assert.True(unit.C.Intent.FireAt);

		unit.Scan(One, CoverState.In);
		unit.Tick();
		Assert.Equal(UnitGoal.SuppressPoint, unit.Goal);
		Assert.Equal(UnitMove.Hold, unit.C.Intent.Move);
	}

	// ---- the belt -------------------------------------------------------------

	[Fact]
	public void UnderItsReserve_ItSuppressesNothing_AndFillsItsBelt()
	{
		Rig unit = Holding();
		unit.Scan(One);
		unit.Tick();
		Assert.Equal(UnitGoal.SuppressPoint, unit.Goal);

		unit.Rounds = Traits.BeltReserveRounds;
		unit.Tick();
		Assert.Equal(UnitGoal.SuppressPoint, unit.Goal);

		unit.Rounds = Traits.BeltReserveRounds - 1;
		unit.Tick();
		Assert.True(unit.C.Is(UnitFact.BeltLow));
		Assert.Equal(UnitGoal.Reload, unit.Goal);
		Assert.True(unit.C.Intent.Reload);
		Assert.False(unit.C.Intent.FireAt);
		Assert.True(UnitContext.IsTactic(UnitGoal.Reload));

		// The reload runs; the belt is filled when it is over, and the bursts go on.
		unit.Reloading = true;
		unit.Tick(10);
		Assert.Equal(UnitGoal.Reload, unit.Goal);
		unit.Reloading = false;
		unit.Rounds = 150;
		unit.Tick();
		Assert.Equal(UnitGoal.SuppressPoint, unit.Goal);
		Assert.False(unit.C.Intent.Reload);
	}

	[Fact]
	public void UnderItsReserve_ItReloadsWithNothingToSuppressToo()
	{
		Rig unit = Holding();
		unit.Rounds = 12;
		unit.Tick();

		Assert.Equal(UnitGoal.Reload, unit.Goal);
		Assert.True(unit.C.Intent.Reload);
	}

	[Fact]
	public void ItReloadsInItsSpotBehindCover_Crouched_AndStandingAnywhereElse()
	{
		Rig unit = Holding();
		unit.Rounds = 10;
		unit.Scan(One, CoverState.Near);
		unit.Tick();
		Assert.Equal(UnitGoal.Reload, unit.Goal);
		Assert.Equal(UnitMove.Point, unit.C.Intent.Move);

		// On its way it reloads standing; in the spot, crouched.
		unit.Reloading = true;
		unit.Tick();
		Assert.False(unit.C.Intent.Crouch);

		unit.Scan(One, CoverState.In);
		unit.Tick();
		Assert.Equal(UnitMove.Hold, unit.C.Intent.Move);
		Assert.True(unit.C.Intent.Crouch);

		// No reload running, no crouch: it presses for one first.
		unit.Reloading = false;
		unit.Tick();
		Assert.False(unit.C.Intent.Crouch);
		Assert.True(unit.C.Intent.Reload);
	}

	[Fact]
	public void ARiflemansMagazine_IsNoBelt()
	{
		Rig unit = Holding(traits: Rifleman);
		unit.Rounds = 5;
		unit.Tick();

		Assert.False(unit.C.Is(UnitFact.BeltLow));
		Assert.NotEqual(UnitGoal.Reload, unit.Goal);
	}

	// ---- who, and under what -----------------------------------------------------

	[Fact]
	public void WithMicroAutonomyOff_ItDoesExactlyWhatItDidBefore()
	{
		Rig off = Holding(MicroAutonomy.Off);
		off.Rounds = 10;
		off.Scan(Three);
		off.Tick(Traits.SuppressCycleTicks);

		Rig quiet = Holding(MicroAutonomy.Off);
		quiet.Scan(None);
		quiet.Tick(Traits.SuppressCycleTicks);

		Assert.Equal(quiet.Goal, off.Goal);
		Assert.Equal(quiet.C.Intent.Move, off.C.Intent.Move);
		Assert.False(off.C.Intent.FireAt);
		Assert.False(off.C.Intent.Reload);
		Assert.DoesNotContain(off.Plans, p => p.Contains("Suppress") || p.Contains("Reload"));
	}

	[Fact]
	public void SwitchingMicroAutonomyOff_EndsTheBurstsOnTheTick_AndOn_StartsThem()
	{
		Rig unit = Holding(MicroAutonomy.Off);
		unit.Scan(One);
		unit.Tick();
		Assert.Equal(UnitGoal.HoldPost, unit.Goal);

		unit.Autonomy = MicroAutonomy.On;
		unit.Tick();
		Assert.Equal(UnitGoal.SuppressPoint, unit.Goal);

		unit.Autonomy = MicroAutonomy.Off;
		unit.Tick();
		Assert.Equal(UnitGoal.HoldPost, unit.Goal);
		Assert.False(unit.C.Intent.FireAt);
	}

	[Fact]
	public void ARiflemanOrAMarksman_DoesNotSuppress()
	{
		foreach (UnitTraits traits in new[] { Rifleman, Sniper })
		{
			Rig unit = Holding(traits: traits);
			unit.Scan(One);
			unit.Tick();

			Assert.NotEqual(UnitGoal.SuppressPoint, unit.Goal);
			Assert.False(unit.C.Intent.FireAt);
		}
	}

	[Fact]
	public void WithATargetOfItsOwn_ItFightsItAsBefore_AndOneSeenMidBurstEndsTheBurst()
	{
		var unit = new Rig { HasTarget = true };
		unit.Order(OrderKind.None, Anchor);
		unit.Scan(One);
		unit.Tick(2);

		var fighting = new Rig { HasTarget = true };
		fighting.Order(OrderKind.None, Anchor);
		fighting.Scan(None);
		fighting.Tick(2);

		Assert.NotEqual(UnitGoal.None, fighting.Goal);
		Assert.Equal(fighting.Goal, unit.Goal);
		Assert.DoesNotContain(unit.Plans, p => p.Contains("Suppress"));

		Rig bursting = Holding();
		bursting.Scan(One);
		bursting.Tick();
		Assert.Equal(UnitGoal.SuppressPoint, bursting.Goal);

		bursting.HasTarget = true;
		bursting.Tick();
		Assert.NotEqual(UnitGoal.SuppressPoint, bursting.Goal);
		Assert.False(bursting.C.Intent.FireAt);
	}

	[Theory]
	[InlineData(OrderKind.Move, UnitGoal.Obey)]
	[InlineData(OrderKind.Patrol, UnitGoal.Patrol)]
	public void OnAMoveOrAPatrol_ItShootsOnTheWalk_AsGiven(OrderKind order, UnitGoal expected)
	{
		var unit = new Rig { Rounds = 10 };
		unit.Order(order, Anchor + new Vector3(30f, 0f, 0f));
		unit.Scan(One);
		unit.Tick(3);

		Assert.Equal(expected, unit.Goal);
		Assert.False(unit.C.Intent.FireAt);
		Assert.False(unit.C.Intent.Reload);
	}

	[Fact]
	public void UnderADefendOrder_ItSuppressesForAsLongAsThereIsAPlace()
	{
		var unit = new Rig();
		unit.Order(OrderKind.Defend, Anchor);
		unit.Scan(One);
		unit.Tick(Traits.SuppressMaxTicks * 3);

		Assert.Equal(UnitGoal.SuppressPoint, unit.Goal);
		Assert.Equal(0, unit.C.SuppressionsGivenUp);
	}

	[Fact]
	public void UnderAnAttack_ItSuppressesFor8s_ThenCarriesTheOrderOutFor8s_ThenSuppressesAgain()
	{
		var unit = new Rig();
		unit.Order(OrderKind.Attack, Anchor + new Vector3(0f, 0f, 60f));
		unit.Scan(One);
		unit.Tick();
		Assert.Equal(UnitGoal.SuppressPoint, unit.Goal);

		unit.Tick(Traits.SuppressMaxTicks);
		Assert.Equal(UnitGoal.Advance, unit.Goal);
		Assert.Equal(1, unit.C.SuppressionsGivenUp);
		Assert.False(unit.C.Is(UnitFact.Suppress));

		// The scans in the meantime find the place and leave it alone.
		unit.Scan(One);
		unit.Tick(Traits.SuppressRetryTicks - 10);
		Assert.Equal(UnitGoal.Advance, unit.Goal);

		unit.Tick(10);
		unit.Scan(One);
		unit.Tick();
		Assert.Equal(UnitGoal.SuppressPoint, unit.Goal);
	}

	[Fact]
	public void ANewOrder_LiftsTheGiveUp()
	{
		var planning = new UnitPlanning(Traits);
		UnitContext c = planning.CreateContext(Gunner);
		c.Tick = 1000;
		c.Position = Anchor;
		var attack = new UnitOrder { Kind = OrderKind.Attack, Target = Anchor + new Vector3(0f, 0f, 60f), Anchor = Anchor };
		c.SenseAutonomy(MicroAutonomy.On);
		c.Encode(new UnitSense { HealthFraction = 1f });
		c.SenseSuppress(One);
		for (int i = 0; i < Traits.SuppressMaxTicks + 2; i++)
		{
			c.Tick++;
			c.Track(Anchor, attack, false, Near);
			planning.Tick(c);
		}

		Assert.Equal(1, c.SuppressionsGivenUp);
		Assert.NotEqual(0u, c.SuppressRetryTick);

		planning.Reset(c);
		Assert.Equal(0u, c.SuppressRetryTick);
		Assert.Equal(0, c.SuppressedTicks);
		c.SenseSuppress(One);
		c.Tick++;
		c.Track(Anchor, attack, false, Near);
		planning.Tick(c);
		Assert.Equal(UnitGoal.SuppressPoint, c.Intent.Goal);
	}

	[Fact]
	public void InASquadFallingBack_ItDoesNotStopToSuppress()
	{
		var unit = new Rig();
		unit.Order(OrderKind.Attack, Anchor + new Vector3(0f, 0f, 60f));
		unit.Scan(One, phase: SquadPhase.FallingBack);
		unit.Tick();

		Assert.NotEqual(UnitGoal.SuppressPoint, unit.Goal);
		Assert.False(unit.C.Intent.FireAt);
		Assert.DoesNotContain(unit.Plans, p => p.Contains("Suppress"));
	}

	[Fact]
	public void Retreat_OutranksSuppressing()
	{
		var unit = new Rig();
		unit.Order(OrderKind.Defend, Anchor);
		unit.Scan(One, odds: OddsBand.Outnumbered, health: 0.1f, autonomous: true);
		unit.Tick();

		Assert.Equal(UnitGoal.Retreat, unit.Goal);
		Assert.False(unit.C.Intent.FireAt);
	}

	[Fact]
	public void ReactingToFire_PreemptsSuppressing_AndSuppressingResumesAfter()
	{
		Rig unit = Holding();
		unit.Scan(One);
		unit.Tick();
		Assert.Equal(UnitGoal.SuppressPoint, unit.Goal);

		// Fired upon, with a spot from the fire: React is higher, and pre-empts.
		unit.C.HasFireCoverPoint = true;
		unit.C.FireCoverPoint = Anchor + new Vector3(-3f, 0f, 2f);
		unit.C.Encode(new UnitSense { HealthFraction = 1f, HasFireCover = true, FireCoverHides = true });
		unit.C.SenseFire(FireKind.NearMiss, Far + new Vector3(0f, 1.5f, 0f), unit.C.Tick, 0);
		unit.Tick();
		Assert.Equal(UnitGoal.TakeCoverFromFire, unit.Goal);

		unit.C.SenseFire(FireKind.None, Far, unit.C.Tick - 200, 0);
		unit.Tick();
		Assert.Equal(UnitGoal.SuppressPoint, unit.Goal);
	}

	[Fact]
	public void WithoutExecutingConditions_TheBurstsOutliveTheirPremise()
	{
		// The guard's own check: it is the executing conditions that end it.
		var unit = new Rig(executingConditions: false);
		unit.Order(OrderKind.None, Anchor);
		unit.Tick();
		unit.Scan(One);
		unit.Tick();
		Assert.Equal(UnitGoal.SuppressPoint, unit.Goal);

		unit.Autonomy = MicroAutonomy.Off;
		unit.Rounds = 10;
		unit.Scan(None);
		unit.Tick();
		Assert.Equal(UnitGoal.SuppressPoint, unit.Goal);
	}

	[Fact]
	public void TheDebuggerNamesTheFacts_AndTheGoals()
	{
		Assert.Equal("Suppress", Gdpyr.Sim.AiDebug.HtnSchema.Unit.FactName((int)UnitFact.Suppress));
		Assert.Equal("BeltLow", Gdpyr.Sim.AiDebug.HtnSchema.Unit.FactName((int)UnitFact.BeltLow));
		Assert.Equal(Gdpyr.Sim.AiDebug.AiTaskFamily.Attack,
			Gdpyr.Sim.AiDebug.AiGoals.Family(Gdpyr.Sim.AiDebug.HtnDomainKind.Unit, (byte)UnitGoal.SuppressPoint));
		Assert.Equal(Gdpyr.Sim.AiDebug.AiTaskFamily.Resupply,
			Gdpyr.Sim.AiDebug.AiGoals.Family(Gdpyr.Sim.AiDebug.HtnDomainKind.Unit, (byte)UnitGoal.Reload));
		Assert.True((int)UnitFact.Count <= Gdpyr.Sim.AiDebug.HtnTrace.MaxFacts);
	}

	// ---- the places (the scan's half) -----------------------------------------------------

	private static readonly Vector3 Wall = new(4f, 1.2f, 0.8f);
	private static readonly Vector3 Block = new(3f, 2.8f, 3f);

	/// <summary>A gunner's standing eye, 40 m from the box at the origin, the box between them.</summary>
	private static readonly Vector3 Eye = new(0f, 1.5f, 40f);

	/// <summary>Somebody crouched a metre behind the box.</summary>
	private static readonly Vector3 Behind = new(0f, 0f, -1f);

	private static CoverBoard BoardWith(Vector3 size)
	{
		var board = new CoverBoard();
		Assert.Equal(0, board.AddLevel(new Vector3(0f, size.Y * 0.5f, 0f), 0f, size));
		return board;
	}

	[Fact]
	public void APlaceBehindALowWall_IsOneToSuppress_AndTheBurstClearsTheWallJustAboveIt()
	{
		CoverBoard board = BoardWith(Wall);
		Assert.True(GunnerPlan.IsEntry(Eye, Behind, 45f, board));

		// The rounds go over the wall by little: aimed a metre lower, they would strike it.
		Assert.False(board.ClearLine(Eye, Behind + new Vector3(0f, GunnerPlan.SuppressAimMeters - 1f, 0f)));
		Assert.InRange(GunnerPlan.SuppressAimMeters - Wall.Y, 0f, 0.2f);
	}

	[Fact]
	public void APlaceBehindABlock_OrOutOfReach_IsNot()
	{
		Assert.False(GunnerPlan.IsEntry(Eye, Behind, 45f, BoardWith(Block)));
		Assert.False(GunnerPlan.IsEntry(Eye, Behind, 40f, BoardWith(Wall)));
		Assert.True(GunnerPlan.IsEntry(Eye, Behind, 45f, new CoverBoard()));
	}

	[Fact]
	public void ThePlaces_AreLiveContactsGhostsAndWhereRoundsCameFrom_NeverOneForgottenOrDead()
	{
		const uint now = 10_000;
		var memory = new ContactMemory(capacity: 8, observers: 1, liveWindowTicks: 10);
		Vector3 live = new(0f, 0f, 10f);
		Vector3 ghost = new(5f, 0f, 12f);
		Vector3 forgotten = new(-5f, 0f, 14f);
		Vector3 dead = new(8f, 0f, 16f);
		memory.Observe(now - (uint)SimConfig.GhostLifetimeTicks - 1, 0, Contact(3, forgotten));
		memory.Observe(now - 200, 0, Contact(2, ghost));
		memory.Observe(now - 100, 0, Contact(4, dead));
		memory.Observe(now, 0, Contact(1, live));
		Assert.True(memory.IsLive(memory.IndexOf(OwnerId.ForPeer(1)), now));
		Assert.True(memory.IsGhost(memory.IndexOf(OwnerId.ForPeer(2)), now));
		Assert.False(memory.IsRemembered(memory.IndexOf(OwnerId.ForPeer(3)), now));

		Vector3 firedFrom = new(-20f, 0f, 30f);
		var into = new Vector3[GunnerPlan.Capacity];
		int count = GunnerPlan.Gather(memory, now, ownerId => ownerId != OwnerId.ForPeer(4), firedFrom: true,
			firedFrom, into, out int contacts);

		Assert.Equal(2, contacts);
		Assert.Equal(3, count);
		Assert.Contains(live, into[..contacts]);
		Assert.Contains(ghost, into[..contacts]);
		Assert.Equal(firedFrom, into[2]);

		// No round, no place for it; and the one that died is a place only to a rule that does not ask.
		Assert.Equal(3, GunnerPlan.Gather(memory, now, null, firedFrom: false, firedFrom, into, out int all));
		Assert.Equal(3, all);
		Assert.Contains(dead, into[..all]);
	}

	private static KnownContact Contact(int peer, Vector3 at) => new()
	{
		OwnerId = OwnerId.ForPeer(peer),
		Kind = ContactKind.Player,
		Position = at,
		HealthFraction = 1f,
	};

	[Fact]
	public void TheEntries_AreNearestFirst_AndARoundFromWhereAContactStandsIsThatContact()
	{
		Vector3 eye = new(0f, 1.5f, 0f);
		Vector3[] places =
		{
			new(0f, 0f, 30f), // a contact, far
			new(0f, 0f, 10f), // a contact, near
			new(0f, 0f, 60f), // a contact out of reach
			new(1f, 0f, 29f), // where a round came from: the far contact
			new(-15f, 0f, 15f), // where a round came from: nobody the side saw
		};

		int count = GunnerPlan.Entries(places, contacts: 3, eye, 45f, new CoverBoard());

		Assert.Equal(3, count);
		Assert.Equal(new Vector3(0f, 0f, 10f), places[0]);
		Assert.Equal(new Vector3(-15f, 0f, 15f), places[1]);
		Assert.Equal(new Vector3(0f, 0f, 30f), places[2]);
	}

	[Fact]
	public void TheNextBurst_IsTheNearestFirst_ThenTheOneAfterTheLast_AndRoundAgain()
	{
		Assert.Equal(-1, GunnerPlan.Next(None, false, Vector3.Zero));
		Assert.Equal(0, GunnerPlan.Next(Three, false, Vector3.Zero));
		Assert.Equal(1, GunnerPlan.Next(Three, true, Near));
		Assert.Equal(2, GunnerPlan.Next(Three, true, Middle + new Vector3(1f, 0f, 1f)));
		Assert.Equal(0, GunnerPlan.Next(Three, true, Far));

		// The last one gone from the list: the nearest.
		Assert.Equal(0, GunnerPlan.Next(Three, true, Anchor + new Vector3(100f, 0f, 0f)));
		Assert.Equal(0, GunnerPlan.Next(One, true, Near));
	}
}
