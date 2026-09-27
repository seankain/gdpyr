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
/// The ground bot's domain (docs/HTN_BOTS.md §5.1, H2): for each row of the tree, a
/// fact vector and the task chain it must produce; pre-emption by Retreat; every
/// long-running operator ending when its premise goes (§3.4, P3); the locker trip
/// and its give-up (P4); the fact bands; and planning without allocating (P8).
/// </summary>
public class GroundHtnTests
{
	private static readonly Vector3 Locker = new(8f, 0.5f, 11f);
	private static readonly Vector3 Node = new(-30f, 0.5f, -15f);

	private sealed class Bot
	{
		public readonly Domain<GroundContext> Domain;
		public readonly Planner<GroundContext> Planner = new();
		public readonly GroundContext C;
		public readonly List<string> Plans = new();
		public int Replacements;

		public Bot(bool executingConditions = true, IFactory factory = null)
		{
			factory ??= new PooledHtnFactory();
			Domain = GroundDomain.Build(factory, executingConditions);
			C = new GroundContext(factory, GroundPlanTraits.Default);
			C.Init();
			C.PlannerState.OnNewPlan = plan => Plans.Add(Names(plan));
			C.PlannerState.OnReplacePlan = (_, _, plan) =>
			{
				Replacements++;
				Plans.Add(Names(plan));
			};

			C.Tick = 1000;
			C.HasLocker = true;
			C.LockerPoint = Locker;
			C.Position = new Vector3(0f, 0.5f, -40f);
		}

		public void Sense(GroundFact fact, Enum value) => C.Sense(fact, Convert.ToByte(value));

		public void Sense(GroundFact fact, bool value) => C.Sense(fact, value);

		public void Tick(int times = 1)
		{
			for (int i = 0; i < times; i++)
			{
				C.Tick++;
				Planner.Tick(Domain, C);
			}
		}

		public GroundGoal Goal => C.Intent.Goal;

		public string LastPlan => Plans.Count > 0 ? Plans[^1] : "(none)";
	}

	private static string Names(Queue<ITask> plan) => "[" + string.Join(", ", plan.Select(t => t.Name)) + "]";

	private static Bot Seeing(Threat threat, bool explosive = false)
	{
		var bot = new Bot();
		bot.Sense(GroundFact.Contact, ContactLevel.Visible);
		bot.Sense(GroundFact.ThreatKind, threat);
		bot.Sense(GroundFact.Armed, explosive ? Arms.Explosive : Arms.SmallArms);
		bot.C.Explosive = explosive;
		return bot;
	}

	// ---- the rows of §5.1 --------------------------------------------------

	[Fact]
	public void NothingKnown_StandsOff()
	{
		var bot = new Bot();
		bot.Tick();

		Assert.Equal("[Standoff]", bot.LastPlan);
		Assert.Equal(GroundMove.Standoff, bot.C.Intent.Move);
	}

	[Fact]
	public void InsideDefences_LeavesThemFirst_WhateverElseIsTrue()
	{
		Bot bot = Seeing(Threat.Infantry);
		bot.C.ExitPoint = new Vector3(70f, 0.5f, -70f);
		bot.Sense(GroundFact.InsideDefences, true);
		bot.Sense(GroundFact.Role, GroundRole.Denier);
		bot.Tick();

		Assert.Equal("[LeaveDefences]", bot.LastPlan);
		Assert.Equal(GroundMove.Run, bot.C.Intent.Move);
		Assert.Equal(bot.C.ExitPoint, bot.C.Intent.Point);
	}

	[Fact]
	public void CriticalAndOutnumbered_FallsBackToItsFriends()
	{
		Bot bot = Seeing(Threat.Infantry);
		bot.C.FallbackPoint = new Vector3(2f, 0.5f, 7f);
		bot.Sense(GroundFact.Health, HealthBand.Critical);
		bot.Sense(GroundFact.Odds, OddsBand.Outnumbered);
		bot.Tick();

		Assert.Equal("[FallBack]", bot.LastPlan);
		Assert.Equal(GroundMove.Run, bot.C.Intent.Move);
		Assert.Equal(bot.C.FallbackPoint, bot.C.Intent.Point);
	}

	[Fact]
	public void CriticalButNotOutnumbered_KeepsFighting()
	{
		Bot bot = Seeing(Threat.Infantry);
		bot.Sense(GroundFact.Health, HealthBand.Critical);
		bot.Sense(GroundFact.Odds, OddsBand.Even);
		bot.Tick();

		Assert.Equal("[Engage]", bot.LastPlan);
	}

	[Fact]
	public void ALockerRunnerFacingArmourWithSmallArms_PlansTheTripAndTheFight()
	{
		Bot bot = Seeing(Threat.Armour);
		bot.Sense(GroundFact.Role, GroundRole.LockerRunner);
		bot.Tick();

		Assert.Equal("[PathToLocker, UseLocker, EngageArmour]", bot.LastPlan);
		Assert.Equal(GroundGoal.PathToLocker, bot.Goal);
		Assert.Equal(GroundMove.Run, bot.C.Intent.Move);
		Assert.Equal(Locker, bot.C.Intent.Point);
	}

	[Fact]
	public void TheRunnerAlsoGoesForAGhostOfArmour()
	{
		var bot = new Bot();
		bot.Sense(GroundFact.Contact, ContactLevel.Ghost);
		bot.Sense(GroundFact.ThreatKind, Threat.Armour);
		bot.Sense(GroundFact.Role, GroundRole.LockerRunner);
		bot.Tick();

		Assert.Equal("[PathToLocker, UseLocker, EngageArmour]", bot.LastPlan);
	}

	[Fact]
	public void TheLockerTrip_WalksThere_TapsUntilALauncherIsInHand_ThenEngages()
	{
		Bot bot = Seeing(Threat.Armour);
		bot.Sense(GroundFact.Role, GroundRole.LockerRunner);
		bot.Tick(5);
		Assert.Equal(GroundGoal.PathToLocker, bot.Goal);

		bot.C.Position = Locker + new Vector3(1f, 0f, 0f);
		bot.Tick(2);
		Assert.Equal(GroundGoal.UseLocker, bot.Goal);
		Assert.True(bot.C.Intent.UseLocker);
		Assert.Equal(GroundMove.Point, bot.C.Intent.Move);

		// The swap lands: what is in hand is a launcher.
		bot.C.Explosive = true;
		bot.Tick(2);

		Assert.Equal(GroundGoal.EngageArmour, bot.Goal);
		Assert.False(bot.C.Intent.UseLocker);
		Assert.Equal((byte)Arms.Explosive, bot.C.Get(GroundFact.Armed));
		Assert.Equal(0, bot.C.FailedLockerTrips);
	}

	[Fact]
	public void ALockerThatNeverDelivers_IsGivenUpOn_AndTheBotNeverEngagesArmourUnarmed()
	{
		Bot bot = Seeing(Threat.Armour);
		bot.Sense(GroundFact.Role, GroundRole.LockerRunner);
		bot.C.Position = Locker;

		int timeout = GroundPlanTraits.Default.UseLockerTimeoutTicks;
		for (int t = 0; t < timeout + 5; t++)
		{
			bot.Tick();
			Assert.NotEqual(GroundGoal.EngageArmour, bot.Goal);
		}

		// Counted, so the coordinator can take the role away (§3.4, P4). Without that
		// the same method wins again, which is what it goes on doing here.
		Assert.Equal(1, bot.C.FailedLockerTrips);
		Assert.Equal(GroundGoal.UseLocker, bot.Goal);
	}

	[Fact]
	public void AWalkToTheLockerThatTakesTooLong_IsGivenUpOn()
	{
		Bot bot = Seeing(Threat.Armour);
		bot.Sense(GroundFact.Role, GroundRole.LockerRunner);
		bot.Tick(GroundPlanTraits.Default.PathToLockerTimeoutTicks + 2);

		Assert.Equal(1, bot.C.FailedLockerTrips);
	}

	[Fact]
	public void NoLockerOnTheMap_TheTripFailsAtOnce()
	{
		Bot bot = Seeing(Threat.Armour);
		bot.C.HasLocker = false;
		bot.Sense(GroundFact.Role, GroundRole.LockerRunner);
		bot.Tick();

		Assert.NotEqual(GroundGoal.PathToLocker, bot.Goal);
		Assert.True(bot.C.FailedLockerTrips >= 1);
	}

	[Fact]
	public void TakingTheRunnerRoleAway_EndsTheTrip()
	{
		Bot bot = Seeing(Threat.Armour);
		bot.Sense(GroundFact.Role, GroundRole.LockerRunner);
		bot.Tick(3);
		Assert.Equal(GroundGoal.PathToLocker, bot.Goal);

		bot.Sense(GroundFact.Role, GroundRole.Assault);
		bot.Tick();

		Assert.Equal(GroundGoal.Standoff, bot.Goal);
	}

	[Fact]
	public void ARiflemanWithoutTheRole_DoesNotPlanTheTrip_AndDoesNotWalkAtArmour()
	{
		Bot bot = Seeing(Threat.Armour);
		bot.Tick();

		Assert.Equal("[Standoff]", bot.LastPlan);
	}

	[Fact]
	public void ALauncherEngagesArmour()
	{
		Bot bot = Seeing(Threat.Armour, explosive: true);
		bot.C.HasArmourPoint = true;
		bot.C.ArmourPoint = new Vector3(8f, 1f, -35f);
		bot.Tick();

		Assert.Equal("[EngageArmour]", bot.LastPlan);
		Assert.Equal(GroundMove.Target, bot.C.Intent.Move);
		Assert.Equal(bot.C.ArmourPoint, bot.C.Intent.Point);
	}

	[Fact]
	public void ReloadsBetweenFights_NotDuringOne()
	{
		var bot = new Bot();
		bot.Sense(GroundFact.MagazineLow, true);
		bot.Tick();
		Assert.Equal("[Reload]", bot.LastPlan);
		Assert.Equal(GroundMove.Stay, bot.C.Intent.Move);

		Bot fighting = Seeing(Threat.Infantry);
		fighting.Sense(GroundFact.MagazineLow, true);
		fighting.Tick();
		Assert.Equal("[Engage]", fighting.LastPlan);
	}

	[Fact]
	public void AReloadEndsWhenSomethingComesIntoView()
	{
		// Resupply is above Attack, so only the executing condition can end it (P3).
		var bot = new Bot();
		bot.Sense(GroundFact.MagazineLow, true);
		bot.Tick();

		bot.Sense(GroundFact.Contact, ContactLevel.Visible);
		bot.Sense(GroundFact.ThreatKind, Threat.Infantry);
		bot.Tick();

		Assert.Equal(GroundGoal.Engage, bot.Goal);
	}

	[Fact]
	public void WithAnAllyNear_PressesTheFocusTargetTogether()
	{
		Bot bot = Seeing(Threat.Infantry);
		bot.C.FocusOwnerId = OwnerId.ForUnit(7);
		bot.C.HasBuddy = true;
		bot.C.BuddyPoint = bot.C.Position + new Vector3(5f, 0f, 0f);
		bot.Sense(GroundFact.AlliesNear, true);
		bot.Sense(GroundFact.Odds, OddsBand.Even);
		bot.Tick();

		Assert.Equal("[TakeFocusTarget, AdvanceWithBuddy]", bot.LastPlan);
		Assert.Equal(OwnerId.ForUnit(7), bot.C.Intent.TargetOwnerId);

		bot.Tick();
		Assert.Equal(GroundGoal.AdvanceWithBuddy, bot.Goal);
		Assert.Equal(GroundMove.Target, bot.C.Intent.Move);
		Assert.Equal(OwnerId.ForUnit(7), bot.C.Intent.TargetOwnerId);
	}

	[Fact]
	public void ABuddyThatHasDriftedAway_IsWalkedToFirst()
	{
		Bot bot = Seeing(Threat.Infantry);
		bot.C.HasBuddy = true;
		bot.C.BuddyPoint = bot.C.Position + new Vector3(14f, 0f, 0f);
		bot.Sense(GroundFact.AlliesNear, true);
		bot.Tick(2);

		Assert.Equal(GroundGoal.AdvanceWithBuddy, bot.Goal);
		Assert.Equal(GroundMove.Point, bot.C.Intent.Move);
		Assert.Equal(bot.C.BuddyPoint, bot.C.Intent.Point);
	}

	[Fact]
	public void Outnumbered_DoesNotPress_ItEngages()
	{
		Bot bot = Seeing(Threat.Infantry);
		bot.Sense(GroundFact.AlliesNear, true);
		bot.Sense(GroundFact.Odds, OddsBand.Outnumbered);
		bot.Tick();

		Assert.Equal("[Engage]", bot.LastPlan);
	}

	[Fact]
	public void AGhostOfInfantry_IsInvestigated()
	{
		var bot = new Bot();
		bot.C.GhostPoint = new Vector3(-10f, 0.5f, -50f);
		bot.Sense(GroundFact.Contact, ContactLevel.Ghost);
		bot.Sense(GroundFact.ThreatKind, Threat.Infantry);
		bot.Tick();

		Assert.Equal("[InvestigateGhost]", bot.LastPlan);
		Assert.Equal(bot.C.GhostPoint, bot.C.Intent.Point);
	}

	[Fact]
	public void ADenier_DoesNotChaseGhosts_ItGoesToItsNode()
	{
		var bot = new Bot();
		bot.C.HasZone = true;
		bot.C.ZonePoint = Node;
		bot.Sense(GroundFact.Role, GroundRole.Denier);
		bot.Sense(GroundFact.Contact, ContactLevel.Ghost);
		bot.Sense(GroundFact.ThreatKind, Threat.Infantry);
		bot.Tick();

		Assert.Equal("[TakeNode, HoldNode]", bot.LastPlan);
	}

	[Fact]
	public void AGhostOfArmour_IsNotARiflemansBusiness()
	{
		var bot = new Bot();
		bot.Sense(GroundFact.Contact, ContactLevel.Ghost);
		bot.Sense(GroundFact.ThreatKind, Threat.Armour);
		bot.Tick();

		Assert.Equal("[Standoff]", bot.LastPlan);
	}

	[Fact]
	public void ADenier_TakesItsNode_ThenHoldsIt()
	{
		var bot = new Bot();
		bot.C.HasZone = true;
		bot.C.ZonePoint = Node;
		bot.Sense(GroundFact.Role, GroundRole.Denier);
		bot.Tick();

		Assert.Equal("[TakeNode, HoldNode]", bot.LastPlan);
		Assert.Equal(GroundMove.Run, bot.C.Intent.Move);
		Assert.Equal(Node, bot.C.Intent.Point);

		bot.Sense(GroundFact.AtZone, true);
		bot.Tick(2);

		Assert.Equal(GroundGoal.HoldNode, bot.Goal);
		Assert.Equal(GroundMove.Point, bot.C.Intent.Move);
	}

	[Fact]
	public void ADenierAlreadyOnItsNode_HoldsIt()
	{
		var bot = new Bot();
		bot.C.HasZone = true;
		bot.C.ZonePoint = Node;
		bot.Sense(GroundFact.Role, GroundRole.Denier);
		bot.Sense(GroundFact.AtZone, true);
		bot.Tick();

		Assert.Equal("[HoldNode]", bot.LastPlan);
	}

	[Fact]
	public void ADenierWhoseRoleIsTakenAway_LeavesTheNode()
	{
		var bot = new Bot();
		bot.C.HasZone = true;
		bot.C.ZonePoint = Node;
		bot.Sense(GroundFact.Role, GroundRole.Denier);
		bot.Sense(GroundFact.AtZone, true);
		bot.Tick();

		bot.Sense(GroundFact.Role, GroundRole.Assault);
		bot.Tick();

		Assert.Equal(GroundGoal.Standoff, bot.Goal);
	}

	[Fact]
	public void ASweeper_GoesToTheStaleNode()
	{
		var bot = new Bot();
		bot.C.SweepPoint = new Vector3(-25f, 0.5f, -100f);
		bot.Sense(GroundFact.Sweep, true);
		bot.Tick();

		Assert.Equal("[SweepZone]", bot.LastPlan);
		Assert.Equal(bot.C.SweepPoint, bot.C.Intent.Point);

		bot.Sense(GroundFact.Sweep, false);
		bot.Tick();

		Assert.Equal(GroundGoal.Standoff, bot.Goal);
	}

	// ---- pre-emption and premises ------------------------------------------

	[Fact]
	public void Retreat_ReplacesARunningAttackTheTickItsFactsAppear()
	{
		Bot bot = Seeing(Threat.Infantry);
		bot.Sense(GroundFact.AlliesNear, true);
		bot.Tick(10);
		Assert.Equal(GroundGoal.AdvanceWithBuddy, bot.Goal);
		Assert.Equal(0, bot.Replacements);

		bot.Sense(GroundFact.Health, HealthBand.Critical);
		bot.Sense(GroundFact.Odds, OddsBand.Outnumbered);
		bot.Tick();

		Assert.Equal(GroundGoal.FallBack, bot.Goal);
		Assert.Equal(1, bot.Replacements);
		Assert.Equal("[FallBack]", bot.LastPlan);
	}

	[Theory]
	[InlineData(false, GroundGoal.Engage)]
	[InlineData(true, GroundGoal.TakeNode)]
	public void ALowerMethodNeverReplacesARunningOne_OnlyAnExecutingConditionEndsIt(bool executingConditions,
		GroundGoal thirtyTicksLater)
	{
		var bot = new Bot(executingConditions);
		bot.C.HasZone = true;
		bot.C.ZonePoint = Node;
		bot.Sense(GroundFact.Contact, ContactLevel.Visible);
		bot.Sense(GroundFact.Role, GroundRole.Denier);
		bot.Tick();
		Assert.Equal(GroundGoal.Engage, bot.Goal);

		bot.Sense(GroundFact.Contact, ContactLevel.None);
		bot.Tick(30);

		Assert.Equal(thirtyTicksLater, bot.Goal);
	}

	/// <summary>
	/// For every operator that runs until something changes, a situation that plans
	/// it and the one fact whose change must end it (§3.4, P3). With nothing else
	/// true, the bot ends up standing off.
	/// </summary>
	public static IEnumerable<object[]> Premises()
	{
		yield return new object[] { GroundGoal.LeaveDefences, new[] { (GroundFact.InsideDefences, (byte)1) },
			(GroundFact.InsideDefences, (byte)0) };
		yield return new object[] { GroundGoal.FallBack,
			new[] { (GroundFact.Health, (byte)HealthBand.Critical), (GroundFact.Odds, (byte)OddsBand.Outnumbered) },
			(GroundFact.Odds, (byte)OddsBand.Even) };
		yield return new object[] { GroundGoal.Reload, new[] { (GroundFact.MagazineLow, (byte)1) },
			(GroundFact.MagazineLow, (byte)0) };
		yield return new object[] { GroundGoal.PathToLocker,
			new[] { (GroundFact.Role, (byte)GroundRole.LockerRunner), (GroundFact.Contact, (byte)ContactLevel.Visible),
				(GroundFact.ThreatKind, (byte)Threat.Armour) },
			(GroundFact.Role, (byte)GroundRole.Assault) };
		yield return new object[] { GroundGoal.EngageArmour,
			new[] { (GroundFact.Armed, (byte)Arms.Explosive), (GroundFact.Contact, (byte)ContactLevel.Visible),
				(GroundFact.ThreatKind, (byte)Threat.Armour) },
			(GroundFact.Contact, (byte)ContactLevel.None) };
		yield return new object[] { GroundGoal.AdvanceWithBuddy,
			new[] { (GroundFact.Contact, (byte)ContactLevel.Visible), (GroundFact.AlliesNear, (byte)1) },
			(GroundFact.Contact, (byte)ContactLevel.None) };
		yield return new object[] { GroundGoal.Engage, new[] { (GroundFact.Contact, (byte)ContactLevel.Visible) },
			(GroundFact.Contact, (byte)ContactLevel.None) };
		yield return new object[] { GroundGoal.InvestigateGhost, new[] { (GroundFact.Contact, (byte)ContactLevel.Ghost) },
			(GroundFact.Contact, (byte)ContactLevel.None) };
		yield return new object[] { GroundGoal.HoldNode,
			new[] { (GroundFact.Role, (byte)GroundRole.Denier), (GroundFact.AtZone, (byte)1) },
			(GroundFact.Role, (byte)GroundRole.Assault) };
		yield return new object[] { GroundGoal.SweepZone, new[] { (GroundFact.Sweep, (byte)1) },
			(GroundFact.Sweep, (byte)0) };
	}

	[Theory]
	[MemberData(nameof(Premises))]
	public void EveryLongRunningOperator_EndsWhenItsPremiseGoes(GroundGoal goal, (GroundFact, byte)[] situation,
		(GroundFact, byte) premiseGone)
	{
		var bot = new Bot();
		bot.C.HasZone = true;
		bot.C.ZonePoint = Node;
		foreach ((GroundFact fact, byte value) in situation)
		{
			bot.C.Sense(fact, value);
		}

		bot.Tick(3);
		Assert.Equal(goal, bot.Goal);

		bot.C.Sense(premiseGone.Item1, premiseGone.Item2);
		bot.Tick(3);

		Assert.Equal(GroundGoal.Standoff, bot.Goal);
	}

	[Fact]
	public void ResettingThePlan_StopsTheOperatorAndClearsTheIntent()
	{
		var planning = new GroundPlanning(GroundPlanTraits.Default);
		GroundContext c = planning.CreateContext();
		c.Sense(GroundFact.MagazineLow, true);
		planning.Tick(c);
		Assert.Equal(GroundGoal.Reload, c.Intent.Goal);

		planning.Reset(c);

		Assert.Equal(GroundGoal.None, c.Intent.Goal);
	}

	// ---- facts -------------------------------------------------------------

	private static GroundContext Encoded(in GroundSense sense, Action<GroundContext> before = null)
	{
		var c = new GroundContext(new PooledHtnFactory(), GroundPlanTraits.Default);
		c.Init();
		before?.Invoke(c);
		c.Encode(sense);
		return c;
	}

	[Fact]
	public void Contact_IsVisibleOverGhostOverNone()
	{
		Assert.Equal((byte)ContactLevel.Visible,
			Encoded(new GroundSense { VisibleHostiles = 1, VisibleSoft = 1, Remembered = true }).Get(GroundFact.Contact));
		Assert.Equal((byte)ContactLevel.Ghost,
			Encoded(new GroundSense { Remembered = true, RememberedSoft = true }).Get(GroundFact.Contact));
		Assert.Equal((byte)ContactLevel.None, Encoded(new GroundSense()).Get(GroundFact.Contact));
	}

	[Theory]
	[InlineData(1, 0, Threat.Armour)]
	[InlineData(2, 1, Threat.Infantry)]
	[InlineData(1, 1, Threat.Infantry)]
	public void Threat_IsArmourOnlyWhenNothingInSightIsSoft(int visible, int soft, Threat expected)
	{
		GroundContext c = Encoded(new GroundSense { VisibleHostiles = visible, VisibleSoft = soft, HealthFraction = 1f });
		Assert.Equal((byte)expected, c.Get(GroundFact.ThreatKind));
	}

	[Fact]
	public void Threat_OfAGhost_IsWhatTheTeamRemembers()
	{
		Assert.Equal((byte)Threat.Armour,
			Encoded(new GroundSense { Remembered = true, RememberedSoft = false }).Get(GroundFact.ThreatKind));
		Assert.Equal((byte)Threat.Infantry,
			Encoded(new GroundSense { Remembered = true, RememberedSoft = true }).Get(GroundFact.ThreatKind));
	}

	[Theory]
	[InlineData(1f, HealthBand.Ok, HealthBand.Ok)]
	[InlineData(0.59f, HealthBand.Ok, HealthBand.Ok)]
	[InlineData(0.54f, HealthBand.Ok, HealthBand.Hurt)]
	[InlineData(0.59f, HealthBand.Hurt, HealthBand.Hurt)]
	[InlineData(0.6f, HealthBand.Hurt, HealthBand.Ok)]
	[InlineData(0.29f, HealthBand.Hurt, HealthBand.Critical)]
	[InlineData(0.32f, HealthBand.Critical, HealthBand.Critical)]
	[InlineData(0.36f, HealthBand.Critical, HealthBand.Hurt)]
	[InlineData(0.32f, HealthBand.Hurt, HealthBand.Hurt)]
	public void HealthBands_HoldPastTheirEdgeOnceEntered(float fraction, HealthBand previous, HealthBand expected)
	{
		Assert.Equal(expected, GroundContext.Band(fraction, previous, GroundPlanTraits.Default));
	}

	[Fact]
	public void AlliesNear_IsEnteredAt15AndHeldTo18()
	{
		var c = new GroundContext(new PooledHtnFactory(), GroundPlanTraits.Default);
		c.Init();

		c.Encode(new GroundSense { HealthFraction = 1f, NearestFriendMeters = 16f });
		Assert.False(c.Is(GroundFact.AlliesNear));

		c.Encode(new GroundSense { HealthFraction = 1f, NearestFriendMeters = 14f });
		Assert.True(c.Is(GroundFact.AlliesNear));

		c.Encode(new GroundSense { HealthFraction = 1f, NearestFriendMeters = 17f });
		Assert.True(c.Is(GroundFact.AlliesNear));

		c.Encode(new GroundSense { HealthFraction = 1f, NearestFriendMeters = 19f });
		Assert.False(c.Is(GroundFact.AlliesNear));
	}

	[Fact]
	public void AtZone_NeedsAnAssignedZone()
	{
		Assert.False(Encoded(new GroundSense { AtZone = true }).Is(GroundFact.AtZone));
		Assert.True(Encoded(new GroundSense { AtZone = true }, c => c.HasZone = true).Is(GroundFact.AtZone));
	}

	[Fact]
	public void EncodingTheSameScanTwice_DoesNotDirtyTheContext()
	{
		var c = new GroundContext(new PooledHtnFactory(), GroundPlanTraits.Default);
		c.Init();
		var sense = new GroundSense { VisibleHostiles = 2, VisibleSoft = 1, HealthFraction = 0.5f, MagazineLow = true };
		c.Encode(sense);
		c.IsDirty = false;

		c.Encode(sense);

		Assert.False(c.IsDirty);
	}

	// ---- sharing and allocation --------------------------------------------

	/// <summary>Ten situations, one per branch of §5.1, as facts.</summary>
	private static void Situation(GroundContext c, int phase)
	{
		c.Sense(GroundFact.Contact, (byte)(phase switch
		{
			1 => ContactLevel.Ghost,
			2 or 3 or 4 or 5 => ContactLevel.Visible,
			_ => ContactLevel.None,
		}));
		c.Sense(GroundFact.ThreatKind, (byte)(phase == 4 ? Threat.Armour : Threat.Infantry));
		c.Sense(GroundFact.AlliesNear, phase == 2);
		c.Sense(GroundFact.Role, (byte)(phase switch
		{
			4 => GroundRole.LockerRunner,
			6 => GroundRole.Denier,
			_ => GroundRole.Assault,
		}));
		c.Sense(GroundFact.Health, (byte)(phase == 5 ? HealthBand.Critical : HealthBand.Ok));
		c.Sense(GroundFact.Odds, (byte)(phase == 5 ? OddsBand.Outnumbered : OddsBand.Even));
		c.Sense(GroundFact.AtZone, phase == 6 && c.Tick % 20 < 10);
		c.Sense(GroundFact.MagazineLow, phase == 7);
		c.Sense(GroundFact.Sweep, phase == 8);
		c.Sense(GroundFact.InsideDefences, phase == 9);
	}

	private static ulong[] Traces(int agents, int ticks)
	{
		var planning = new GroundPlanning(GroundPlanTraits.Default);
		var contexts = new GroundContext[agents];
		var hashes = new ulong[agents];
		for (int i = 0; i < agents; i++)
		{
			contexts[i] = planning.CreateContext();
			contexts[i].HasZone = true;
			contexts[i].HasLocker = true;
			hashes[i] = 14695981039346656037ul;
		}

		for (uint t = 0; t < ticks; t++)
		{
			for (int i = 0; i < agents; i++)
			{
				GroundContext c = contexts[i];
				c.Tick = t;
				if ((t + i) % 10 == 0)
				{
					Situation(c, (int)(((t + i) / 10 + i) % 10));
				}

				planning.Tick(c);
				hashes[i] = (hashes[i] ^ (byte)c.Intent.Goal) * 1099511628211ul;
			}
		}

		return hashes;
	}

	[Fact]
	public void OneDomainForEveryBot_IsDeterministic_AndNotTrivial()
	{
		ulong[] first = Traces(16, 2000);

		Assert.Equal(first, Traces(16, 2000));
		Assert.True(first.Distinct().Count() > 8);
	}

	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public void PlanningEveryGroundBot_AllocatesNothingOnTheTick_WithThePooledFactory(bool pooled)
	{
		// The unpooled row is the guard's own check: FluidHTN's DefaultFactory
		// allocates on every replan, so a measurement that saw nothing there would be
		// seeing nothing at all.
		var planning = new GroundPlanning(GroundPlanTraits.Default, pooled ? null : new DefaultFactory());
		var contexts = new GroundContext[SimConfig.MaxUnits];
		for (int i = 0; i < contexts.Length; i++)
		{
			contexts[i] = planning.CreateContext();
			contexts[i].HasZone = true;
			contexts[i].HasLocker = true;
		}

		int dirtied = 0;
		void Run(uint from, uint ticks, int interval)
		{
			for (uint t = from; t < from + ticks; t++)
			{
				for (int i = 0; i < contexts.Length; i++)
				{
					GroundContext c = contexts[i];
					c.Tick = t;
					if ((t + i) % interval == 0)
					{
						Situation(c, (int)(((t + i) / interval + i) % 10));
						dirtied += c.IsDirty ? 1 : 0;
					}

					planning.Tick(c);
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
