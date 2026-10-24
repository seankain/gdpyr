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
/// The marksman (docs/TACTICS.md §5.3, T4), the planner's half: with a peek pair it hides,
/// holding its fire on the way; hidden two seconds with somebody to shoot, it pops out,
/// fires two shots or for 4.5 s, and ducks; two peeks, or a round while it is out, burn
/// the pair and it moves on, as a threat too near does; only with micro-autonomy on, only
/// a marksman, only under an attack, a defend or no order; pre-empted by Retreat and by
/// reacting to fire, and every operator ending when its premise goes. Where the pair is,
/// and whom it peeks at, is the engine's (<c>CoverTests</c>, <see cref="MarksmanPlan"/>).
/// </summary>
public class MarksmanTests
{
	private static readonly UnitTraits Sniper = new(70f, 70f, 1.5f, 20f, UnitDoctrine.Marksman);
	private static readonly UnitTraits Rifleman = new(45f, 40f, 1.5f, 20f, UnitDoctrine.Rifleman);

	private static readonly Vector3 Anchor = new(10f, 0.5f, 10f);

	/// <summary>Its hide spot, behind a wall three metres off; past a block's end, its fire spot.</summary>
	private static readonly Vector3 Hide = Anchor + new Vector3(0f, 0f, -3f);
	private static readonly Vector3 BlockFire = Hide + new Vector3(1.3f, 0f, 0f);

	/// <summary>Another pair, ten metres along: far enough from the first to move on to.</summary>
	private static readonly Vector3 NextHide = Hide + new Vector3(10f, 0f, 0f);

	private static readonly int Target = OwnerId.ForPeer(7);

	/// <summary>Where a round comes from: 50 m off.</summary>
	private static readonly Vector3 Shooter = Anchor + new Vector3(0f, 1.5f, 50f);

	private const int Second = SimConfig.TickRate;
	private static readonly UnitPlanTraits Traits = UnitPlanTraits.Default;

	private sealed class Rig
	{
		public readonly Domain<UnitContext> Domain;
		public readonly Planner<UnitContext> Planner = new();
		public readonly UnitContext C;
		public readonly List<string> Plans = new();

		public MicroAutonomy Autonomy = MicroAutonomy.On;
		public FireMemory Fire;
		public bool HasTarget;
		public uint Shots;

		public Rig(UnitTraits? traits = null, bool executingConditions = true, IFactory factory = null)
		{
			factory ??= new PooledHtnFactory();
			Domain = UnitDomain.Build(factory, executingConditions);
			C = new UnitContext(factory, Traits, traits ?? Sniper);
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
		}, HasTarget, Shooter);

		/// <summary>One scan: a pair at <paramref name="hide"/> (none for null), and whether it has somebody to peek at.</summary>
		public void Scan(Vector3? hide = null, bool low = true, Vector3? fire = null, bool target = true,
			bool standoff = false, OddsBand odds = OddsBand.Even, float health = 1f, bool autonomous = false,
			bool pair = true, Vector3? fireCover = null)
		{
			C.HasFireCoverPoint = fireCover.HasValue;
			C.FireCoverPoint = fireCover ?? Vector3.Zero;
			C.Encode(new UnitSense
			{
				HealthFraction = health,
				Odds = odds,
				Autonomous = autonomous,
				HasFireCover = fireCover.HasValue,
				FireCoverHides = fireCover.HasValue,
			});

			Vector3 at = hide ?? Hide;
			C.SensePeek(pair, at, fire ?? (low ? at : at + new Vector3(1.3f, 0f, 0f)), low,
				target ? Target : OwnerId.None, standoff);
		}

		public void NoPair(bool standoff = false) => C.SensePeek(false, Vector3.Zero, Vector3.Zero, false, OwnerId.None,
			standoff);

		/// <summary>A round from <see cref="Shooter"/> on this tick.</summary>
		public void Round() => FiredUpon.Record(ref Fire, FireKind.NearMiss, Shooter, OwnerId.ForPeer(9), C.Tick);

		public void Tick(int times = 1)
		{
			for (int i = 0; i < times; i++)
			{
				C.Tick++;
				C.SenseAutonomy(Autonomy);
				C.SenseFire(FiredUpon.KindAt(Fire, C.Tick), Fire.Origin, Fire.LastTick, Fire.LastHitTick);
				C.SenseWeapon(false, Shots);
				C.Track(C.Position, C.Order, HasTarget, Shooter);
				Planner.Tick(Domain, C);
			}
		}

		/// <summary>Ticks until the goal is <paramref name="goal"/>, at most <paramref name="limit"/>; how many it took.</summary>
		public int TickUntil(UnitGoal goal, int limit = 20 * Second)
		{
			for (int i = 1; i <= limit; i++)
			{
				Tick();
				if (Goal == goal)
				{
					return i;
				}
			}

			return -1;
		}

		public PeekState Peek => (PeekState)C.Get(UnitFact.Peek);

		public UnitGoal Goal => C.Intent.Goal;
	}

	private static string Names(Queue<ITask> plan) => "[" + string.Join(", ", plan.Select(t => t.Name)) + "]";

	/// <summary>A sniper defending its anchor, with a pair behind a low wall or a block, standing in its hide spot.</summary>
	private static Rig Hidden(bool low = true, UnitTraits? traits = null, MicroAutonomy autonomy = MicroAutonomy.On,
		bool executingConditions = true, IFactory factory = null)
	{
		var unit = new Rig(traits, executingConditions, factory) { Autonomy = autonomy };
		unit.Order(OrderKind.Defend, Anchor);
		unit.C.Position = Hide;
		unit.Scan(low: low);
		unit.Tick();
		return unit;
	}

	/// <summary>
	/// Through one whole peek from a hidden sniper: out, two shots, back in. Behind a low
	/// wall the duck is down where it stands, and over on the tick it starts.
	/// </summary>
	private static void PeekOnce(Rig unit, bool low = true)
	{
		Assert.True(unit.TickUntil(UnitGoal.PopOut) > 0);
		unit.C.Position = low ? Hide : BlockFire;
		Assert.True(unit.TickUntil(UnitGoal.FireFromPeek, 5) > 0);
		unit.Shots += 2;
		if (!low)
		{
			Assert.True(unit.TickUntil(UnitGoal.Duck, 5) > 0);
			unit.C.Position = Hide;
		}

		for (int i = 0; i < 5 && (unit.Peek == PeekState.Exposed || unit.Goal == UnitGoal.FireFromPeek); i++)
		{
			unit.Tick();
		}

		Assert.NotEqual(PeekState.Exposed, unit.Peek);
	}

	// ---- the rows of §5.3 -----------------------------------------------------

	[Fact]
	public void AMarksmanWithNoPair_FightsAsARiflemanDoes()
	{
		var unit = new Rig();
		unit.Order(OrderKind.Defend, Anchor);
		unit.Scan(pair: false);
		unit.Tick();

		Assert.Equal(UnitGoal.HoldPost, unit.Goal);
		Assert.Equal(PeekState.None, unit.Peek);
		Assert.False(unit.C.Intent.HoldFire);
	}

	[Fact]
	public void WithAPair_ItGoesToItsHideSpot_HoldingItsFireOnTheWay()
	{
		var unit = new Rig();
		unit.Order(OrderKind.Defend, Anchor);
		unit.Scan(hide: NextHide);
		unit.Tick();

		Assert.Equal(UnitGoal.ToHide, unit.Goal);
		Assert.Equal(PeekState.Away, unit.Peek);
		Assert.Equal(UnitMove.Point, unit.C.Intent.Move);
		Assert.Equal(NextHide, unit.C.Intent.Point);
		Assert.False(unit.C.Intent.StopToFight);
		Assert.True(unit.C.Intent.HoldFire);
		Assert.False(unit.C.Intent.Crouch);
		Assert.True(UnitContext.IsTactic(UnitGoal.ToHide));
	}

	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public void InItsHideSpot_ItWaits_DownBehindALowWall_StandingBehindABlock(bool low)
	{
		Rig unit = Hidden(low);

		Assert.Equal(UnitGoal.ToHide, unit.Goal);
		Assert.Equal(PeekState.Hidden, unit.Peek);
		Assert.Equal(UnitMove.Hold, unit.C.Intent.Move);
		Assert.Equal(low, unit.C.Intent.Crouch);
		Assert.True(unit.C.Intent.HoldFire);
	}

	[Fact]
	public void HiddenTwoSecondsWithSomebodyToShoot_ItPopsOut_FiresTwoShots_AndDucks()
	{
		Rig unit = Hidden();

		// Hidden on the tick it got there; ready two seconds later, and not before — and out
		// on the tick it is.
		unit.Tick(Traits.MinHideTicks - 2);
		Assert.Equal(PeekState.Hidden, unit.Peek);
		Assert.Equal(UnitGoal.ToHide, unit.Goal);
		unit.Tick();
		Assert.Equal("[PopOut, FireFromPeek, Duck]", unit.Plans[^1]);
		Assert.Equal(UnitGoal.PopOut, unit.Goal);

		// Behind a low wall it stands where it is: out at once, and its target named.
		Assert.Equal(PeekState.Exposed, unit.Peek);
		Assert.False(unit.C.Intent.Crouch);
		Assert.False(unit.C.Intent.HoldFire);
		Assert.Equal(Target, unit.C.Intent.TargetOwnerId);

		unit.Tick();
		Assert.Equal(UnitGoal.FireFromPeek, unit.Goal);
		Assert.Equal(UnitMove.Hold, unit.C.Intent.Move);
		Assert.True(unit.C.Intent.StopToFight);
		Assert.Equal(Target, unit.C.Intent.TargetOwnerId);

		unit.Shots++;
		unit.Tick(30);
		Assert.Equal(UnitGoal.FireFromPeek, unit.Goal);

		unit.Shots++;
		unit.Tick(2);
		Assert.Equal(UnitGoal.ToHide, unit.Goal);
		Assert.True(unit.C.Intent.Crouch);
		Assert.Equal(PeekState.Hidden, unit.Peek);
		Assert.Equal(1, unit.C.PeeksFromPair);

		// And the clock is a new one: two seconds hidden again before the next.
		Assert.Equal(Traits.MinHideTicks, unit.TickUntil(UnitGoal.PopOut));
	}

	[Fact]
	public void APeekThatFiresNothing_EndsFourAndAHalfSecondsAfterItPoppedOut()
	{
		Rig unit = Hidden();
		unit.TickUntil(UnitGoal.PopOut);

		int out_ = 1 + unit.TickUntil(UnitGoal.ToHide);
		Assert.InRange(out_, Traits.PeekMaxTicks, Traits.PeekMaxTicks + 3);
		Assert.Equal(PeekState.Hidden, unit.Peek);
	}

	[Fact]
	public void ShotsFiredBeforeThePeek_DoNotCountTowardsIt()
	{
		Rig unit = Hidden();
		unit.Shots = 40;
		unit.TickUntil(UnitGoal.FireFromPeek);

		unit.Shots++;
		unit.Tick(5);
		Assert.Equal(UnitGoal.FireFromPeek, unit.Goal);
	}

	[Fact]
	public void BehindABlock_ItStepsPastItsEndToFire_AndStepsBack()
	{
		Rig unit = Hidden(low: false);
		unit.TickUntil(UnitGoal.PopOut);
		Assert.Equal(UnitMove.Point, unit.C.Intent.Move);
		Assert.Equal(BlockFire, unit.C.Intent.Point);
		Assert.False(unit.C.Intent.StopToFight);

		// Still walking out: out on a peek, and the fire spot not reached.
		unit.Tick(10);
		Assert.Equal(UnitGoal.PopOut, unit.Goal);
		Assert.Equal(PeekState.Exposed, unit.Peek);

		unit.C.Position = BlockFire;
		Assert.True(unit.TickUntil(UnitGoal.FireFromPeek, 3) > 0);
		unit.Shots += 2;
		Assert.True(unit.TickUntil(UnitGoal.Duck, 3) > 0);
		Assert.Equal(Hide, unit.C.Intent.Point);
		Assert.True(unit.C.Intent.HoldFire);

		unit.C.Position = Hide;
		unit.Tick(2);
		Assert.Equal(UnitGoal.ToHide, unit.Goal);
		Assert.False(unit.C.Intent.Crouch);
		Assert.Equal(PeekState.Hidden, unit.Peek);
	}

	[Fact]
	public void WithNobodyToShoot_ItStaysHidden()
	{
		var unit = new Rig();
		unit.Order(OrderKind.Defend, Anchor);
		unit.C.Position = Hide;
		unit.Scan(target: false);
		unit.Tick(Traits.MinHideTicks * 3);

		Assert.Equal(PeekState.Ready, unit.Peek);
		Assert.Equal(UnitGoal.ToHide, unit.Goal);
		Assert.DoesNotContain(unit.Plans, p => p.Contains("PopOut"));
	}

	[Fact]
	public void ATargetLostMidPeek_EndsIt_AndItGoesBackDown()
	{
		Rig unit = Hidden();
		unit.TickUntil(UnitGoal.FireFromPeek);

		unit.Scan(target: false);
		unit.Tick();
		Assert.Equal(UnitGoal.ToHide, unit.Goal);
		Assert.True(unit.C.Intent.Crouch);
		Assert.Equal(PeekState.Hidden, unit.Peek);
	}

	// ---- moving on ---------------------------------------------------------------

	[Fact]
	public void TwoPeeksFromOnePair_BurnIt_AndItMovesToAnother_ThenPeeksFromThere()
	{
		Rig unit = Hidden();
		PeekOnce(unit);
		Assert.False(unit.C.Is(UnitFact.Burned));
		PeekOnce(unit);
		Assert.True(unit.C.Is(UnitFact.Burned));
		Assert.Equal(UnitGoal.Relocate, unit.Goal);
		Assert.True(unit.C.Intent.HoldFire);

		// The scan offers the same pair again — as it would not — and it stays burned.
		unit.Scan();
		unit.Tick(Second);
		Assert.True(unit.C.Is(UnitFact.Burned));
		Assert.Equal(UnitGoal.Relocate, unit.Goal);

		// Another, ten metres off: it walks there, holding its fire.
		unit.Scan(hide: NextHide);
		unit.Tick();
		Assert.Equal(UnitGoal.Relocate, unit.Goal);
		Assert.Equal(NextHide, unit.C.Intent.Point);
		Assert.True(unit.C.Intent.HoldFire);
		Assert.Equal(0, unit.C.PeeksFromPair);

		// Hidden there, the burned pair is behind it.
		unit.C.Position = NextHide;
		unit.Tick();
		Assert.False(unit.C.Is(UnitFact.Burned));
		Assert.Equal(UnitGoal.ToHide, unit.Goal);
		Assert.True(unit.TickUntil(UnitGoal.PopOut) > 0);
	}

	[Fact]
	public void ABurnedPairWithNoOtherToGoTo_LeavesItARifleman_UntilTheBurnRunsOut()
	{
		Rig unit = Hidden();
		PeekOnce(unit);
		PeekOnce(unit);
		unit.NoPair();
		unit.Tick();
		Assert.Equal(UnitGoal.HoldPost, unit.Goal);

		unit.Tick(Traits.BurnedMaxTicks);
		Assert.False(unit.C.Is(UnitFact.Burned));
	}

	[Fact]
	public void ARoundWhileItIsOutOnAPeek_BurnsThePair_AndItMovesOnAtOnce()
	{
		Rig unit = Hidden();
		unit.TickUntil(UnitGoal.FireFromPeek);

		unit.Round();
		unit.Tick();
		Assert.True(unit.C.Is(UnitFact.Burned));
		Assert.Equal(UnitGoal.Relocate, unit.Goal);
		Assert.True(unit.C.Intent.HoldFire);
		Assert.NotEqual(PeekState.Exposed, unit.Peek);
	}

	[Fact]
	public void ARoundWhileItIsHidden_DoesNotBurnThePair()
	{
		Rig unit = Hidden();
		unit.Round();
		unit.Tick(10);

		Assert.False(unit.C.Is(UnitFact.Burned));
		Assert.Equal(UnitGoal.ToHide, unit.Goal);
	}

	[Fact]
	public void AThreatTooNear_MovesItOn_UntilItIsFarEnough()
	{
		Rig unit = Hidden();
		unit.Scan(hide: NextHide, standoff: true);
		unit.Tick();
		Assert.Equal(UnitGoal.Relocate, unit.Goal);
		Assert.Equal(NextHide, unit.C.Intent.Point);

		unit.C.Position = NextHide;
		unit.Scan(hide: NextHide, standoff: false);
		unit.Tick();
		Assert.Equal(UnitGoal.ToHide, unit.Goal);
	}

	[Fact]
	public void ANewPair_HasNoPeeksFromItYet_AndTheSamePairKeepsItsCount()
	{
		Rig unit = Hidden();
		PeekOnce(unit);
		Assert.Equal(1, unit.C.PeeksFromPair);

		unit.Scan(hide: Hide + new Vector3(0.5f, 0f, 0f));
		Assert.Equal(1, unit.C.PeeksFromPair);

		unit.Scan(hide: NextHide);
		Assert.Equal(0, unit.C.PeeksFromPair);
	}

	[Fact]
	public void TheFireSpotsItKeepsAwayFrom_AreThoseOfTheLastTwentySeconds()
	{
		Rig unit = Hidden(low: false);
		PeekOnce(unit, low: false);
		unit.Scan(low: false, target: false);
		Span<Vector3> recent = stackalloc Vector3[UnitContext.RecentFireCapacity];
		Assert.Equal(1, unit.C.RecentFireSpots(recent));
		Assert.Equal(BlockFire, recent[0]);
		Assert.True(unit.C.HasLastFireSpot);
		Assert.Equal(BlockFire, unit.C.LastFireSpot);

		unit.Tick(Traits.RecentFireTicks);
		Assert.Equal(0, unit.C.RecentFireSpots(recent));
	}

	[Fact]
	public void AWalkToAHideSpotThatTakesTooLong_IsGivenUp_AndNotTriedAgainFor10s()
	{
		var unit = new Rig();
		unit.Order(OrderKind.Defend, Anchor);
		unit.Scan(hide: NextHide);
		unit.Tick(Traits.PeekWalkMaxTicks);
		Assert.Equal(UnitGoal.ToHide, unit.Goal);

		unit.Tick();
		Assert.Equal(UnitGoal.HoldPost, unit.Goal);
		Assert.Equal(1, unit.C.FailedPeeks);
		Assert.Equal(PeekState.None, unit.Peek);
		Assert.Equal(unit.C.Tick + (uint)Traits.PeekRetryTicks, unit.C.PeekRetryTick);
	}

	[Fact]
	public void AWalkIsCountedOncePerTick_HoweverOftenTheOperatorRuns()
	{
		var unit = new Rig();
		unit.Order(OrderKind.Defend, Anchor);
		unit.Scan(hide: NextHide);
		unit.Tick();
		unit.C.Tick++;
		unit.C.Perform(UnitGoal.ToHide);
		unit.C.Perform(UnitGoal.ToHide);
		unit.C.Perform(UnitGoal.ToHide);

		Assert.Equal(2, unit.C.WalkedTicks);
	}

	// ---- who, and when not -------------------------------------------------------

	[Fact]
	public void WithMicroAutonomyOff_ItDoesExactlyWhatItDidBefore()
	{
		Rig off = Hidden(autonomy: MicroAutonomy.Off);
		off.Tick(Traits.MinHideTicks * 2);

		var quiet = new Rig { Autonomy = MicroAutonomy.Off };
		quiet.Order(OrderKind.Defend, Anchor);
		quiet.C.Position = Hide;
		quiet.Scan(pair: false);
		quiet.Tick(Traits.MinHideTicks * 2 + 1);

		Assert.Equal(quiet.Goal, off.Goal);
		Assert.Equal(quiet.C.Intent.Move, off.C.Intent.Move);
		Assert.False(off.C.Intent.HoldFire);
		Assert.False(off.C.Intent.Crouch);
		Assert.DoesNotContain(off.Plans, p => p.Contains("Hide") || p.Contains("Peek") || p.Contains("PopOut"));
	}

	[Fact]
	public void SwitchingMicroAutonomyOffMidPeek_EndsItOnTheTick()
	{
		Rig unit = Hidden();
		unit.TickUntil(UnitGoal.FireFromPeek);

		unit.Autonomy = MicroAutonomy.Off;
		unit.Tick();
		Assert.Equal(UnitGoal.HoldPost, unit.Goal);
		Assert.False(unit.C.Intent.Crouch);
		Assert.NotEqual(PeekState.Exposed, unit.Peek);
	}

	[Fact]
	public void ARifleman_WithAPairOffered_DoesNotPeek()
	{
		Rig unit = Hidden(traits: Rifleman);
		unit.Tick(Traits.MinHideTicks * 2);

		Assert.Equal(UnitGoal.HoldPost, unit.Goal);
		Assert.DoesNotContain(unit.Plans, p => p.Contains("Hide") || p.Contains("PopOut"));
	}

	[Theory]
	[InlineData(OrderKind.Defend, UnitGoal.ToHide)]
	[InlineData(OrderKind.None, UnitGoal.ToHide)]
	[InlineData(OrderKind.Attack, UnitGoal.ToHide)]
	[InlineData(OrderKind.Move, UnitGoal.Obey)]
	[InlineData(OrderKind.Patrol, UnitGoal.Patrol)]
	[InlineData(OrderKind.Build, UnitGoal.Work)]
	public void ItPeeksUnderAnAttackADefendOrNone_AndCarriesAnythingElseOutAsGiven(OrderKind order, UnitGoal goal)
	{
		var unit = new Rig();
		unit.Order(order, Anchor + new Vector3(0f, 0f, 40f));
		unit.Scan(hide: NextHide);
		unit.Tick();

		Assert.Equal(goal, unit.Goal);
	}

	[Fact]
	public void ANewOrderItDoesNotPeekUnder_EndsAPeekOnTheTick()
	{
		Rig unit = Hidden();
		unit.TickUntil(UnitGoal.FireFromPeek);

		unit.Order(OrderKind.Move, Anchor + new Vector3(0f, 0f, -40f));
		unit.Tick();
		Assert.Equal(UnitGoal.Obey, unit.Goal);
		Assert.NotEqual(PeekState.Exposed, unit.Peek);
	}

	[Fact]
	public void Retreat_OutranksPeeking()
	{
		Rig unit = Hidden();
		unit.Scan(odds: OddsBand.Outnumbered, health: 0.1f, autonomous: true);
		unit.Tick();

		Assert.Equal(UnitGoal.Retreat, unit.Goal);
	}

	[Fact]
	public void FiredUponWithAPair_ItStaysHidden_ForItsHideSpotIsHiddenFromTheFireAlready()
	{
		// The pair is concealed from every threat, where the fire came from among them: it
		// does not stand up to fire back, which would show it, and then hide, every scan.
		Rig unit = Hidden();
		unit.Round();
		unit.Scan(fireCover: Hide + new Vector3(-4f, 0f, 0f));
		unit.Tick(10);
		Assert.Equal(UnitGoal.ToHide, unit.Goal);
		Assert.True(unit.C.Intent.Crouch);
		Assert.DoesNotContain(unit.Plans, p => p.Contains("Fire]") || p.Contains("CoverFromFire"));

		// Without a pair, it reacts as a rifleman does; one found mid-reaction takes over.
		unit.Round();
		unit.Scan(pair: false, fireCover: Hide + new Vector3(-4f, 0f, 0f));
		unit.Tick();
		Assert.Equal(UnitGoal.TakeCoverFromFire, unit.Goal);

		unit.Scan(fireCover: Hide + new Vector3(-4f, 0f, 0f));
		unit.Tick();
		Assert.Equal(UnitGoal.ToHide, unit.Goal);
	}

	[Fact]
	public void AGrenadeAtAHiddenEnemy_OutranksPeeking()
	{
		Rig unit = Hidden();
		unit.C.SenseGrenade(true, Anchor + new Vector3(0f, 0f, 18f));
		unit.Tick();

		Assert.Equal(UnitGoal.ThrowGrenade, unit.Goal);
	}

	[Fact]
	public void WithoutExecutingConditions_APeekOutlivesItsTarget()
	{
		// The guard's own check: it is the executing conditions that end it.
		Rig unit = Hidden(executingConditions: false);
		unit.TickUntil(UnitGoal.FireFromPeek);

		unit.Scan(target: false);
		unit.Tick();
		Assert.Equal(UnitGoal.FireFromPeek, unit.Goal);
	}

	[Fact]
	public void TheDebuggerNamesTheFacts_AndTheGoalsAreAttacksWithProtocolNumbers()
	{
		Assert.Equal("Peek", Gdpyr.Sim.AiDebug.HtnSchema.Unit.FactName((int)UnitFact.Peek));
		Assert.Equal("Ready", Gdpyr.Sim.AiDebug.HtnSchema.Unit.FactValue((int)UnitFact.Peek, (byte)PeekState.Ready));
		Assert.Equal("Burned", Gdpyr.Sim.AiDebug.HtnSchema.Unit.FactName((int)UnitFact.Burned));

		UnitGoal[] goals = { UnitGoal.ToHide, UnitGoal.PopOut, UnitGoal.FireFromPeek, UnitGoal.Duck, UnitGoal.Relocate };
		for (int i = 0; i < goals.Length; i++)
		{
			Assert.Equal(21 + i, (int)goals[i]);
			Assert.True(UnitContext.IsTactic(goals[i]));
			Assert.Equal(Gdpyr.Sim.AiDebug.AiTaskFamily.Attack,
				Gdpyr.Sim.AiDebug.AiGoals.Family(Gdpyr.Sim.AiDebug.HtnDomainKind.Unit, (byte)goals[i]));
		}
	}

	// ---- whom it peeks at ------------------------------------------------------

	private static MarksmanCandidate Candidate(int peer, float speed = 0f, float health = 1f, bool engaged = false) =>
		new(OwnerId.ForPeer(peer), Vector3.Zero, speed, health, engaged);

	[Fact]
	public void TheSquadsFocus_IsWhomItPeeksAt()
	{
		MarksmanCandidate[] candidates = { Candidate(3), Candidate(4, engaged: true), Candidate(5, speed: 4f) };
		Assert.Equal(2, MarksmanPlan.ChooseTarget(candidates, OwnerId.ForPeer(5)));
	}

	[Fact]
	public void WithoutAFocus_OneAFriendIsEngaging_ThenTheSlowest_TheWeakest_TheLowestId()
	{
		Assert.Equal(1, MarksmanPlan.ChooseTarget(new[] { Candidate(3), Candidate(4, speed: 5f, engaged: true) },
			OwnerId.None));
		Assert.Equal(1, MarksmanPlan.ChooseTarget(new[] { Candidate(3, speed: 4f), Candidate(4, speed: 1f) },
			OwnerId.None));
		Assert.Equal(1, MarksmanPlan.ChooseTarget(new[] { Candidate(3, health: 0.9f), Candidate(4, health: 0.4f) },
			OwnerId.None));
		Assert.Equal(1, MarksmanPlan.ChooseTarget(new[] { Candidate(4), Candidate(3) }, OwnerId.None));
		Assert.Equal(-1, MarksmanPlan.ChooseTarget(ReadOnlySpan<MarksmanCandidate>.Empty, OwnerId.None));

		// A focus it has no sighting of is no target.
		Assert.Equal(0, MarksmanPlan.ChooseTarget(new[] { Candidate(3) }, OwnerId.ForPeer(9)));
	}

	// ---- allocation --------------------------------------------------------------

	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public void PeekingAndMovingOn_AllocatesNothingOnTheTick_WithThePooledFactory(bool pooled)
	{
		// The unpooled row is the guard's own check: FluidHTN's DefaultFactory allocates
		// on every replan, so a measurement that saw nothing there would see nothing at all.
		Rig unit = Hidden(factory: pooled ? null : new DefaultFactory());
		unit.C.PlannerState.OnNewPlan = null;
		unit.C.PlannerState.OnReplacePlan = null;
		Cycle(unit);

		long before = GC.GetAllocatedBytesForCurrentThread();
		for (int i = 0; i < 20; i++)
		{
			Cycle(unit);
		}

		long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
		if (pooled)
		{
			Assert.Equal(0, bytes);
		}
		else
		{
			Assert.True(bytes > 0);
		}

		// Two peeks from one pair, a move to another and back, and a round while out.
		static void Cycle(Rig unit)
		{
			for (int pair = 0; pair < 2; pair++)
			{
				for (int peek = 0; peek < 2; peek++)
				{
					unit.TickUntil(UnitGoal.PopOut, 40 * Second);
					unit.Tick();
					unit.Shots += 2;
					unit.Tick(3);
				}

				unit.Scan(hide: pair == 0 ? NextHide : Hide);
				unit.Tick();
				unit.C.Position = pair == 0 ? NextHide : Hide;
				unit.Tick();
			}

			unit.TickUntil(UnitGoal.FireFromPeek, 40 * Second);
			unit.Round();
			unit.Tick(3);
		}
	}
}
