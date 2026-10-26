using System;
using System.Collections.Generic;
using System.Linq;
using FluidHTN;
using FluidHTN.Factory;
using Gdpyr.Sim;
using Gdpyr.Sim.Agent;
using Gdpyr.Sim.AiDebug;
using Gdpyr.Sim.Htn;
using Godot;
using Xunit;

namespace Gdpyr.Tests;

/// <summary>
/// The squad drill (docs/TACTICS.md §5.5, T6). Its roles, by doctrine and unit id; what each
/// role does in each phase; the drill domain's rows, each as facts and the phase they must
/// produce — on the point it holds, fired upon it goes to ground, the base before any bound,
/// bounds by halves, grenades then the rush; its give-ups (4 s, 6 s, 8 s, 60 s then 20 s);
/// every phase ending when its premise goes. The squad's half over a cover board: its list
/// and threat point, the fire positions, bound spots and open points it hands out, claims,
/// lift and shift, a new order. And the unit's half: each task as an operator, under a move
/// onto a point and under an attack, ending when micro-autonomy goes off; reacting to fire
/// never stopping a bound; a marksman's peeks timed to the bounds; a gunner as the base.
/// Deterministic, and allocation-free over eight squads.
/// </summary>
public class SquadDrillTests
{
	private static readonly DrillTraits Traits = DrillTraits.Default;

	private static readonly int Interval = Traits.IntervalTicks;

	/// <summary>Decisions any loop below may take: two minutes of them. A guard removed may never end one.</summary>
	private const int LoopLimit = 720;

	// ---- the drill's domain ------------------------------------------------

	private sealed class Rig
	{
		public readonly Domain<DrillContext> Domain;
		public readonly Planner<DrillContext> Planner = new();
		public readonly DrillContext C;
		public readonly List<string> Plans = new();

		public Rig(bool executingConditions = true)
		{
			var factory = new PooledHtnFactory();
			Domain = DrillDomain.Build(factory, executingConditions);
			C = new DrillContext(factory, Traits);
			C.Init();
			C.Tick = 1000;
			C.PlannerState.OnNewPlan = plan => Plans.Add(Names(plan));
			C.PlannerState.OnReplacePlan = (_, _, plan) => Plans.Add(Names(plan));
		}

		/// <summary>One decision, <see cref="Interval"/> ticks after the last; a base that fires fires on it.</summary>
		public DrillPhase Step(DrillSense sense, bool baseFiring = false)
		{
			C.Tick += (uint)Interval;
			if (baseFiring)
			{
				sense.LastBaseFireTick = C.Tick;
			}

			C.Encode(sense);
			Planner.Tick(Domain, C);
			return C.Intent.Phase;
		}

		/// <summary>Decisions for <paramref name="ticks"/> ticks; the phase after the last.</summary>
		public DrillPhase Run(DrillSense sense, int ticks, bool baseFiring = false)
		{
			DrillPhase phase = C.Intent.Phase;
			for (int t = 0; t < ticks; t += Interval)
			{
				phase = Step(sense, baseFiring);
			}

			return phase;
		}
	}

	private static string Names(Queue<ITask> plan) => "[" + string.Join(", ", plan.Select(t => t.Name)) + "]";

	private static DrillSense Quiet(DrillObjective objective = DrillObjective.Approach) =>
		new() { Objective = objective, HasGunner = true, HasElementB = true };

	private static DrillSense InContact(float lead = 30f, bool fired = false, bool grenade = false,
		bool gunner = true, bool halves = true, DrillObjective objective = DrillObjective.Approach) => new()
	{
		HasThreat = true,
		FiredUpon = fired,
		LeadMeters = lead,
		HasGunner = gunner,
		HasElementB = halves,
		Grenade = grenade,
		Objective = objective,
	};

	/// <summary>A drill in contact with its base firing: bounding.</summary>
	private static Rig Bounding(bool gunner = true, bool halves = true)
	{
		var rig = new Rig();
		Assert.Equal(DrillPhase.SetBase, rig.Step(InContact(gunner: gunner, halves: halves)));
		Assert.Equal(DrillPhase.Bound, rig.Step(InContact(gunner: gunner, halves: halves), baseFiring: true));
		return rig;
	}

	[Fact]
	public void WithNoContact_TheSquadApproaches()
	{
		var rig = new Rig();
		Assert.Equal(DrillPhase.Approach, rig.Step(Quiet()));
		Assert.Equal("[Approach]", rig.Plans[^1]);
		Assert.Equal((byte)DrillContact.None, rig.C.Get(DrillFact.Contact));
	}

	[Fact]
	public void FiredUponWithNoBase_ItGoesToGround_ForFourSeconds_ThenSetsItsBase()
	{
		var rig = new Rig();
		Assert.Equal(DrillPhase.GoToGround, rig.Step(InContact(fired: true)));
		Assert.Equal((byte)DrillContact.FiredUpon, rig.C.Get(DrillFact.Contact));

		// The base is sent to its fire positions with everyone else to cover.
		Assert.Equal((byte)DrillBase.Moving, rig.C.Get(DrillFact.Base));

		Assert.Equal(DrillPhase.GoToGround, rig.Run(InContact(fired: true), Traits.GroundMaxTicks - Interval));
		Assert.Equal(DrillPhase.SetBase, rig.Step(InContact(fired: true)));
	}

	[Fact]
	public void InContact_TheBaseComesFirst_AndTheSquadBoundsOnceItFires()
	{
		var rig = new Rig();
		Assert.Equal(DrillPhase.SetBase, rig.Step(InContact()));
		Assert.Equal(DrillPhase.SetBase, rig.Run(InContact(), Traits.BaseMaxTicks - (2 * Interval)));
		Assert.Equal((byte)DrillBase.Moving, rig.C.Get(DrillFact.Base));

		Assert.Equal(DrillPhase.Bound, rig.Step(InContact(), baseFiring: true));
		Assert.Equal((byte)DrillBase.Firing, rig.C.Get(DrillFact.Base));
	}

	[Fact]
	public void ABaseThatNeverFires_StopsNobody_AfterSixSeconds()
	{
		var rig = new Rig();
		Assert.Equal(DrillPhase.SetBase, rig.Step(InContact()));
		uint sent = rig.C.Tick;
		for (int guard = 0; rig.Step(InContact()) == DrillPhase.SetBase; guard++)
		{
			Assert.True(guard < LoopLimit, "it never got out of the loop");
			Assert.True(rig.C.Tick - sent < Traits.BaseMaxTicks + Interval, $"still setting its base at {rig.C.Tick - sent}");
		}

		Assert.Equal(DrillPhase.Bound, rig.C.Intent.Phase);
		Assert.True(rig.C.Is(DrillFact.BaseLate));
		Assert.Equal(Traits.BaseMaxTicks, (int)(rig.C.Tick - sent));
	}

	[Fact]
	public void ABaseFiringFromBeforeItWasSent_IsNotFiring()
	{
		var rig = new Rig();
		DrillSense sense = InContact();
		sense.LastBaseFireTick = rig.C.Tick;
		Assert.Equal(DrillPhase.SetBase, rig.Step(sense));
		Assert.Equal(DrillPhase.SetBase, rig.Step(sense));
		Assert.Equal((byte)DrillBase.Moving, rig.C.Get(DrillFact.Base));
	}

	[Fact]
	public void ABound_GoesHalfByHalf_OnArrival_OrAfterEightSeconds()
	{
		Rig rig = Bounding();
		Assert.Equal(DrillElement.A, rig.C.Intent.Moving);
		int first = rig.C.Intent.BoundSerial;

		DrillSense arrived = InContact();
		arrived.Arrived = true;
		Assert.Equal(DrillPhase.Bound, rig.Step(arrived, baseFiring: true));
		Assert.Equal(DrillElement.B, rig.C.Intent.Moving);
		Assert.Equal(first + 1, rig.C.Intent.BoundSerial);

		// Never in its spots: the other half goes after eight seconds regardless.
		uint started = rig.C.Tick;
		for (int guard = 0; rig.C.Intent.Moving == DrillElement.B; guard++)
		{
			Assert.True(guard < LoopLimit, "it never got out of the loop");
			rig.Step(InContact(), baseFiring: true);
		}

		Assert.Equal(DrillElement.A, rig.C.Intent.Moving);
		Assert.Equal(Traits.BoundMaxTicks, (int)(rig.C.Tick - started));
		Assert.Equal(first + 2, rig.C.Intent.BoundSerial);
		Assert.Equal(3, rig.C.Bounds);
	}

	[Fact]
	public void WithoutAGunner_TheSecondHalfBoundsFirst_TheFirstBeingTheBase()
	{
		Rig rig = Bounding(gunner: false);
		Assert.Equal(DrillElement.B, rig.C.Intent.Moving);
	}

	[Fact]
	public void WithoutAGunner_AfterABound_TheHalfNotMovingIsTheBase()
	{
		Rig rig = Bounding(gunner: false);
		Assert.Equal(DrillElement.A, rig.C.RiflesBase);

		DrillSense arrived = InContact(gunner: false);
		arrived.Arrived = true;
		rig.Step(arrived, baseFiring: true);
		Assert.Equal(DrillElement.A, rig.C.Intent.Moving);
		Assert.Equal(DrillElement.B, rig.C.RiflesBase);
		Assert.Equal(DrillTask.FirePosition, DrillRoles.TaskFor(DrillPhase.SetBase, DrillRole.Assault, DrillElement.B,
			DrillElement.None, hasGunner: false, rig.C.RiflesBase));
		Assert.Equal(DrillTask.Hold, DrillRoles.TaskFor(DrillPhase.SetBase, DrillRole.Assault, DrillElement.A,
			DrillElement.None, hasGunner: false, rig.C.RiflesBase));

		// With one half, that half.
		Assert.Equal(DrillElement.A, Bounding(gunner: false, halves: false).C.RiflesBase);
	}

	[Fact]
	public void WithOneHalf_ItBoundsAgain_FromWhereItGotTo()
	{
		Rig rig = Bounding(halves: false);
		Assert.Equal(DrillElement.A, rig.C.Intent.Moving);
		DrillSense arrived = InContact(halves: false);
		arrived.Arrived = true;
		int serial = rig.C.Intent.BoundSerial;
		rig.Step(arrived, baseFiring: true);

		Assert.Equal(DrillElement.A, rig.C.Intent.Moving);
		Assert.Equal(serial + 1, rig.C.Intent.BoundSerial);
	}

	[Fact]
	public void ABoundChangesHandsOnceADecision_HoweverOftenItRuns()
	{
		// FluidHTN can run an operator twice in one tick (#21, §3.2).
		Rig rig = Bounding();
		rig.C.Sense(DrillFact.Arrived, true);
		rig.C.Tick += (uint)Interval;
		rig.C.Perform(DrillPhase.Bound);
		rig.C.Sense(DrillFact.Arrived, true);
		rig.C.Perform(DrillPhase.Bound);

		Assert.Equal(DrillElement.B, rig.C.Intent.Moving);
	}

	[Fact]
	public void InAssaultRange_WithAGrenade_TheAssaultThrows_ThenRushesTwoSecondsAfter()
	{
		Rig rig = Bounding();
		Assert.Equal(DrillPhase.Grenades, rig.Step(InContact(lead: 15f, grenade: true), baseFiring: true));
		Assert.Equal(DrillPhase.Grenades, rig.Step(InContact(lead: 15f, grenade: true), baseFiring: true));

		DrillSense thrown = InContact(lead: 15f);
		thrown.LastAssaultThrowTick = rig.C.Tick;
		Assert.Equal(DrillPhase.Grenades, rig.Run(thrown, Traits.GrenadeWaitTicks - Interval, baseFiring: true));
		rig.Step(thrown, baseFiring: true);
		Assert.Equal(DrillPhase.Rush, rig.Step(thrown, baseFiring: true));
		Assert.Equal("[Grenades, Rush]", rig.Plans.Last(p => p.StartsWith("[Grenades")));
	}

	[Fact]
	public void InAssaultRange_AGrenadeNeverThrown_IsWaitedForFiveSeconds()
	{
		Rig rig = Bounding();
		Assert.Equal(DrillPhase.Grenades, rig.Step(InContact(lead: 15f, grenade: true), baseFiring: true));
		uint started = rig.C.Tick;
		for (int guard = 0; rig.Step(InContact(lead: 15f), baseFiring: true) == DrillPhase.Grenades; guard++)
		{
			Assert.True(guard < LoopLimit, "it never got out of the loop");
		}

		Assert.Equal(DrillPhase.Rush, rig.C.Intent.Phase);
		Assert.InRange((int)(rig.C.Tick - started), Traits.GrenadesMaxTicks, Traits.GrenadesMaxTicks + (2 * Interval));
	}

	[Fact]
	public void InAssaultRange_WithNoGrenade_TheAssaultRushes()
	{
		Rig rig = Bounding();
		Assert.Equal(DrillPhase.Rush, rig.Step(InContact(lead: 15f), baseFiring: true));
	}

	[Fact]
	public void InAssaultRange_WithTheBaseNotYetFiring_ItSetsTheBaseFirst()
	{
		var rig = new Rig();
		Assert.Equal(DrillPhase.SetBase, rig.Step(InContact(lead: 15f)));
	}

	[Fact]
	public void OnThePoint_TheSquadHoldsIt_PreemptingABound()
	{
		Rig rig = Bounding();
		Assert.Equal(DrillPhase.HoldPoint, rig.Step(InContact(objective: DrillObjective.On), baseFiring: true));
		Assert.Equal("[HoldPoint]", rig.Plans[^1]);
	}

	[Fact]
	public void Contact_IsHeldFiveSecondsAfterTheLastThreatPoint_ThenTheSquadApproaches()
	{
		Rig rig = Bounding();
		uint last = rig.C.Tick;
		for (int guard = 0; rig.Step(Quiet(), baseFiring: true) == DrillPhase.Bound; guard++)
		{
			Assert.True(guard < LoopLimit, "it never got out of the loop");
		}

		Assert.Equal(DrillPhase.Approach, rig.C.Intent.Phase);
		Assert.Equal(Traits.QuietTicks + Interval, (int)(rig.C.Tick - last));
		Assert.Equal((byte)DrillBase.None, rig.C.Get(DrillFact.Base));

		// The next contact sets its base again before it bounds.
		Assert.Equal(DrillPhase.SetBase, rig.Step(InContact()));
	}

	[Theory]
	[InlineData(DrillRange.Bound, 19.9f, DrillRange.Assault)]
	[InlineData(DrillRange.Bound, 20.5f, DrillRange.Bound)]
	[InlineData(DrillRange.Assault, 21.9f, DrillRange.Assault)]
	[InlineData(DrillRange.Assault, 22.1f, DrillRange.Bound)]
	[InlineData(DrillRange.Bound, 45.5f, DrillRange.Far)]
	[InlineData(DrillRange.Far, 43.1f, DrillRange.Far)]
	[InlineData(DrillRange.Far, 42.9f, DrillRange.Bound)]
	public void Range_IsBandedWithTwoMetresOfHysteresis(DrillRange previous, float lead, DrillRange expected)
	{
		var rig = new Rig();
		rig.C.Sense(DrillFact.Range, (byte)previous);
		rig.Step(InContact(lead: lead));
		Assert.Equal((byte)expected, rig.C.Get(DrillFact.Range));
	}

	[Fact]
	public void SixtySecondsInContact_GiveTheDrillUp_ForTwentyOfApproach_ThenItDrillsAgain()
	{
		Rig rig = Bounding();
		DrillPhase phase = rig.Run(InContact(), Traits.DrillMaxTicks - (3 * Interval), baseFiring: true);
		Assert.Equal(DrillPhase.Bound, phase);
		for (int guard = 0; rig.Step(InContact(), baseFiring: true) == DrillPhase.Bound; guard++)
		{
			Assert.True(guard < LoopLimit, "it never got out of the loop");
		}

		Assert.Equal(DrillPhase.Approach, rig.C.Intent.Phase);
		Assert.True(rig.C.Is(DrillFact.GivenUp));
		Assert.Equal(1, rig.C.GiveUps);
		Assert.Equal("[Approach]", rig.Plans[^1]);

		Assert.Equal(DrillPhase.Approach, rig.Run(InContact(), Traits.DrillRetryTicks - Interval, baseFiring: true));
		Assert.Equal(DrillPhase.SetBase, rig.Step(InContact()));
		Assert.False(rig.C.Is(DrillFact.GivenUp));
	}

	[Fact]
	public void TheGiveUpClock_StartsAgainOutOfContact()
	{
		Rig rig = Bounding();
		rig.Run(InContact(), Traits.DrillMaxTicks / 2, baseFiring: true);
		rig.Run(Quiet(), Traits.QuietTicks + (2 * Interval));
		Assert.Equal(0, rig.C.ContactTicks);
		rig.Run(InContact(), Traits.DrillMaxTicks / 2, baseFiring: true);
		Assert.False(rig.C.Is(DrillFact.GivenUp));
	}

	public static IEnumerable<object[]> Premises()
	{
		// The phase, how to get it running, what changes, and the phase that follows when the
		// running one has an executing condition for it — which, without one, keeps running,
		// because every phase that follows is a lower branch (§3.4, P3).
		yield return new object[] { DrillPhase.Bound, "contact ends", DrillPhase.Approach };
		yield return new object[] { DrillPhase.Rush, "out of assault range", DrillPhase.Bound };
		yield return new object[] { DrillPhase.SetBase, "contact ends", DrillPhase.Approach };
		yield return new object[] { DrillPhase.HoldPoint, "off the point", DrillPhase.Approach };
	}

	[Theory]
	[MemberData(nameof(Premises))]
	public void EveryPhase_EndsWhenItsPremiseGoes_AndALowerOneNeverReplacesIt(DrillPhase phase, string change,
		DrillPhase then)
	{
		foreach (bool executing in new[] { true, false })
		{
			var rig = new Rig(executing);
			DrillSense running = phase switch
			{
				DrillPhase.Rush => InContact(lead: 15f),
				DrillPhase.HoldPoint => Quiet(DrillObjective.On),
				_ => InContact(),
			};

			rig.Step(running, baseFiring: phase != DrillPhase.SetBase);
			rig.Step(running, baseFiring: phase != DrillPhase.SetBase);
			Assert.Equal(phase, rig.C.Intent.Phase);

			DrillSense changed = change switch
			{
				"contact ends" => Quiet(),
				"out of assault range" => InContact(lead: 30f),
				_ => Quiet(),
			};

			DrillPhase now = rig.Run(changed, Traits.QuietTicks + (2 * Interval), baseFiring: phase != DrillPhase.SetBase);
			Assert.Equal(executing ? then : phase, now);
		}
	}

	[Fact]
	public void TheDrillsPhases_AreProtocolNumbers_AndTheDebuggerNamesItsFacts()
	{
		Assert.Equal(1, (int)DrillPhase.Approach);
		Assert.Equal(2, (int)DrillPhase.HoldPoint);
		Assert.Equal(3, (int)DrillPhase.GoToGround);
		Assert.Equal(4, (int)DrillPhase.SetBase);
		Assert.Equal(5, (int)DrillPhase.Bound);
		Assert.Equal(6, (int)DrillPhase.Grenades);
		Assert.Equal(7, (int)DrillPhase.Rush);

		Assert.Equal(20, (int)AgentEventKind.SquadDrill);
		Assert.Equal("squad_drill", AgentEventSchema.NameOf(AgentEventKind.SquadDrill));
		Assert.Equal(new[] { "squad", "phase", "elements", "x", "z" },
			AgentEventSchema.FieldsOf(AgentEventKind.SquadDrill).ToArray());

		Assert.Equal((int)DrillFact.Count, HtnSchema.Drill.FactCount);
		Assert.Equal("Contact", HtnSchema.Drill.FactName((int)DrillFact.Contact));
		Assert.Equal("FiredUpon", HtnSchema.Drill.FactValue((int)DrillFact.Contact, (byte)DrillContact.FiredUpon));
		Assert.Equal("B", HtnSchema.Drill.FactValue((int)DrillFact.Moving, (byte)DrillElement.B));
		Assert.Equal("Bound", HtnSchema.Drill.GoalName((byte)DrillPhase.Bound));
		Assert.Equal(AiTaskFamily.Attack, AiGoals.Family(HtnDomainKind.Drill, (byte)DrillPhase.Rush));
		Assert.True(HtnDomains.TryParse("drill", out HtnDomainKind kind) && kind == HtnDomainKind.Drill);

		var planning = new DrillPlanning(Traits);
		Assert.Equal(HtnDomains.Drill.Signature, planning.Map.Signature);
	}

	// ---- roles -------------------------------------------------------------

	private static DrillMember Member(ushort id, UnitDoctrine doctrine, Vector3 at, bool on = true) => new()
	{
		UnitId = id,
		Doctrine = doctrine,
		Position = at,
		Element = on && doctrine != UnitDoctrine.None,
		Body = new CoverQuery
		{
			From = at,
			BodyRadiusMeters = 0.4f,
			ProtectMeters = 0.9f,
			EyeMeters = 1.5f,
		},
	};

	private static DrillDirective[] Roles(params DrillMember[] members)
	{
		var into = new DrillDirective[members.Length];
		DrillRoles.Assign(members, into, new int[members.Length], Traits.HalvesFrom);
		return into;
	}

	private static DrillMember[] Squad(int riflemen, bool gunner, Vector3 at, ushort firstId = 1)
	{
		var members = new List<DrillMember>();
		ushort id = firstId;
		if (gunner)
		{
			members.Add(Member(id++, UnitDoctrine.Gunner, at));
		}

		for (int i = 0; i < riflemen; i++)
		{
			members.Add(Member(id++, UnitDoctrine.Rifleman, at + new Vector3((i - (riflemen / 2)) * 2f, 0f, -(i % 2))));
		}

		return members.ToArray();
	}

	[Fact]
	public void AGunnerAndThree_TheGunnerIsTheBase_AndTheRiflemenBoundTogether()
	{
		DrillDirective[] roles = Roles(Squad(3, gunner: true, Vector3.Zero));
		Assert.Equal(DrillRole.Base, roles[0].Role);
		Assert.All(roles[1..], r => Assert.Equal((DrillRole.Assault, DrillElement.A), (r.Role, r.Element)));
	}

	[Fact]
	public void AGunnerAndSix_TheRiflemenBoundInHalves_InUnitIdOrder()
	{
		DrillMember[] members = Squad(6, gunner: true, Vector3.Zero);

		// Listed in any order, the halves are by unit id.
		DrillMember[] shuffled = { members[4], members[0], members[6], members[2], members[1], members[5], members[3] };
		DrillDirective[] roles = Roles(shuffled);
		var halves = shuffled.Zip(roles).Where(p => p.Second.Role == DrillRole.Assault)
			.ToDictionary(p => p.First.UnitId, p => p.Second.Element);
		Assert.Equal(new[] { DrillElement.A, DrillElement.A, DrillElement.A, DrillElement.B, DrillElement.B, DrillElement.B },
			halves.OrderBy(p => p.Key).Select(p => p.Value).ToArray());
		Assert.Equal(DrillRole.Base, roles[1].Role);
	}

	[Fact]
	public void FourRiflemen_BoundInHalves_TheFirstHalfTheBase()
	{
		DrillDirective[] roles = Roles(Squad(4, gunner: false, Vector3.Zero));
		Assert.Equal(new[] { DrillElement.A, DrillElement.A, DrillElement.B, DrillElement.B },
			roles.Select(r => r.Element).ToArray());
		Assert.Equal(DrillTask.FirePosition,
			DrillRoles.TaskFor(DrillPhase.SetBase, roles[0].Role, roles[0].Element, DrillElement.None, hasGunner: false));
		Assert.Equal(DrillTask.Hold,
			DrillRoles.TaskFor(DrillPhase.SetBase, roles[3].Role, roles[3].Element, DrillElement.None, hasGunner: false));
	}

	[Fact]
	public void TwoRiflemen_AreAHalfEach()
	{
		DrillDirective[] roles = Roles(Squad(2, gunner: false, Vector3.Zero));
		Assert.Equal(new[] { DrillElement.A, DrillElement.B }, roles.Select(r => r.Element).ToArray());
	}

	[Fact]
	public void AMarksman_IsOverwatch_NotAnElement()
	{
		DrillMember[] members = Squad(4, gunner: true, Vector3.Zero).Append(Member(9, UnitDoctrine.Marksman, Vector3.Zero))
			.ToArray();
		DrillDirective[] roles = Roles(members);
		Assert.Equal(DrillRole.Overwatch, roles[^1].Role);
		Assert.Equal(DrillElement.None, roles[^1].Element);
		Assert.Equal(new[] { DrillElement.A, DrillElement.A, DrillElement.B, DrillElement.B },
			roles[1..5].Select(r => r.Element).ToArray());
	}

	[Fact]
	public void AUnitWithMicroAutonomyOff_HasNoRole_AndIsNotCountedInTheHalves()
	{
		DrillMember[] members = Squad(4, gunner: true, Vector3.Zero);
		members[2].Element = false;
		DrillDirective[] roles = Roles(members);
		Assert.Equal(DrillRole.None, roles[2].Role);

		// Three riflemen with a gunner bound together.
		Assert.All(roles.Where(r => r.Role == DrillRole.Assault), r => Assert.Equal(DrillElement.A, r.Element));
		Assert.Equal(DrillTask.None,
			DrillRoles.TaskFor(DrillPhase.Bound, roles[2].Role, roles[2].Element, DrillElement.A, hasGunner: true));
	}

	[Theory]
	[InlineData(DrillPhase.Approach, DrillRole.Assault, DrillElement.A, DrillElement.None, true, DrillTask.None)]
	[InlineData(DrillPhase.HoldPoint, DrillRole.Base, DrillElement.None, DrillElement.None, true, DrillTask.None)]
	[InlineData(DrillPhase.GoToGround, DrillRole.Base, DrillElement.None, DrillElement.None, true, DrillTask.FirePosition)]
	[InlineData(DrillPhase.GoToGround, DrillRole.Assault, DrillElement.A, DrillElement.None, true, DrillTask.Ground)]
	[InlineData(DrillPhase.GoToGround, DrillRole.Assault, DrillElement.A, DrillElement.None, false, DrillTask.FirePosition)]
	[InlineData(DrillPhase.GoToGround, DrillRole.Assault, DrillElement.B, DrillElement.None, false, DrillTask.Ground)]
	[InlineData(DrillPhase.SetBase, DrillRole.Assault, DrillElement.B, DrillElement.None, true, DrillTask.Hold)]
	[InlineData(DrillPhase.SetBase, DrillRole.Assault, DrillElement.B, DrillElement.None, false, DrillTask.Hold)]
	[InlineData(DrillPhase.Bound, DrillRole.Assault, DrillElement.A, DrillElement.A, true, DrillTask.Bound)]
	[InlineData(DrillPhase.Bound, DrillRole.Assault, DrillElement.B, DrillElement.A, true, DrillTask.Hold)]
	[InlineData(DrillPhase.Bound, DrillRole.Assault, DrillElement.A, DrillElement.B, false, DrillTask.Hold)]
	[InlineData(DrillPhase.Bound, DrillRole.Base, DrillElement.None, DrillElement.A, true, DrillTask.FirePosition)]
	[InlineData(DrillPhase.Grenades, DrillRole.Assault, DrillElement.B, DrillElement.None, true, DrillTask.Hold)]
	[InlineData(DrillPhase.Rush, DrillRole.Assault, DrillElement.B, DrillElement.None, true, DrillTask.Rush)]
	[InlineData(DrillPhase.Rush, DrillRole.Base, DrillElement.None, DrillElement.None, true, DrillTask.FirePosition)]
	[InlineData(DrillPhase.Bound, DrillRole.Overwatch, DrillElement.None, DrillElement.A, true, DrillTask.Overwatch)]
	[InlineData(DrillPhase.Bound, DrillRole.None, DrillElement.None, DrillElement.A, true, DrillTask.None)]
	public void EachRole_HasItsTaskInEachPhase(DrillPhase phase, DrillRole role, DrillElement element,
		DrillElement moving, bool hasGunner, DrillTask expected)
	{
		Assert.Equal(expected, DrillRoles.TaskFor(phase, role, element, moving, hasGunner));
	}

	// ---- the squad ---------------------------------------------------------

	/// <summary>The point to take: the test map's middle node.</summary>
	private static readonly Vector3 Point = new(0f, 0.5f, -35f);

	private const float PointRadius = 12f;

	private static readonly DrillOrder OntoThePoint = new()
	{
		Drills = true,
		FormedTick = 100,
		OrderedTick = 100,
		Toward = Point,
		Objective = true,
		Point = Point,
		Radius = PointRadius,
	};

	/// <summary>A sandbag wall's body: four metres across, 1.1 high, its faces towards ±Z at yaw 0.</summary>
	private static readonly Vector3 Sandbags = new(4f, 1.1f, 0.8f);

	/// <summary>
	/// A squad's drill over a cover board and the side's memory, its members moved towards
	/// where their tasks take them — 0.75 m a decision, a rifleman's walk — and a member at a
	/// fire position or holding in a bound counted as firing.
	/// </summary>
	private sealed class Field
	{
		public readonly SquadDrill Drill;
		public readonly CoverBoard Cover = new();
		public readonly ContactMemory Memory = new(capacity: 8, observers: 1, liveWindowTicks: 10);
		public DrillMember[] Members;
		public DrillOrder Order = OntoThePoint;
		public uint Tick = 1000;
		public bool Walk = true;
		public readonly HashSet<int> Dead = new();

		/// <summary>Members, by index, that never fire: no shot from where they are.</summary>
		public readonly HashSet<int> Silent = new();

		public Field(DrillMember[] members, IFactory factory = null, DrillTraits? traits = null)
		{
			Drill = new SquadDrill(new DrillPlanning(traits ?? Traits, factory));
			Members = members;
		}

		public void Wall(Vector3 at, float yaw = 0f) => Cover.AddLevel(at + new Vector3(0f, Sandbags.Y * 0.5f, 0f), yaw, Sandbags);

		public void See(int peer, Vector3 at) => Memory.Observe(Tick, 0, new KnownContact
		{
			OwnerId = OwnerId.ForPeer(peer),
			Position = at,
			HealthFraction = 1f,
		});

		/// <summary>A round at <paramref name="member"/> from <paramref name="from"/>, in its window from now.</summary>
		public void Fire(int member, Vector3 from)
		{
			Members[member].FiredUpon = true;
			Members[member].FiredFrom = from;
			Members[member].FiredTick = Tick;
		}

		public DrillPhase Decide(int squad = 0)
		{
			Tick += (uint)Interval;
			for (int i = 0; i < Members.Length; i++)
			{
				Members[i].Body.From = Members[i].Position;
				if (Members[i].FiredUpon && Tick - Members[i].FiredTick > FiredUpon.WindowTicks)
				{
					Members[i].FiredUpon = false;
				}
			}

			DrillPhase phase = Drill.Decide(squad, Tick, Order, Members, Memory, owner => !Dead.Contains(owner), Cover,
				ReadOnlySpan<Vector3>.Empty);
			if (Walk)
			{
				Step(squad, phase);
			}

			return phase;
		}

		public DrillPhase Run(int ticks, int squad = 0)
		{
			DrillPhase phase = DrillPhase.None;
			for (int t = 0; t < ticks; t += Interval)
			{
				phase = Decide(squad);
			}

			return phase;
		}

		private void Step(int squad, DrillPhase phase)
		{
			for (int i = 0; i < Members.Length; i++)
			{
				if (!Drill.Board.TryGet(squad, Members[i].UnitId, out DrillDirective d))
				{
					continue;
				}

				Vector3 to = d.Task == DrillTask.Rush ? Point : d.HasSpot ? d.Spot : Members[i].Position;
				Vector3 step = to - Members[i].Position;
				step.Y = 0f;
				Members[i].Position += step.Length() <= 0.75f ? step : step.Normalized() * 0.75f;

				if (!Silent.Contains(i)
					&& (d.Task == DrillTask.FirePosition || (d.Task == DrillTask.Hold && phase == DrillPhase.Bound)))
				{
					Members[i].LastShotTick = Tick;
				}
			}
		}

		public DrillDirective Directive(int member)
		{
			Assert.True(Drill.Board.TryGet(0, Members[member].UnitId, out DrillDirective d));
			return d;
		}
	}

	/// <summary>A gunner and six riflemen 30 m short of the point, with an enemy on it.</summary>
	private static Field Approaching()
	{
		var field = new Field(Squad(6, gunner: true, Point + new Vector3(0f, 0f, -30f)));
		field.See(3, Point + new Vector3(0f, 0f, 2f));
		return field;
	}

	[Fact]
	public void ASquadWithOneElement_HasNoDrill()
	{
		DrillMember[] members = Squad(2, gunner: false, Point + new Vector3(0f, 0f, -30f));
		members[1].Element = false;
		var field = new Field(members);
		Assert.Equal(DrillPhase.None, field.Decide());
		Assert.False(field.Drill.Board.IsActive(0));
		Assert.False(field.Drill.Board.TryGet(0, members[0].UnitId, out _));
	}

	[Fact]
	public void AnOrderItDoesNotDrillUnder_EndsTheDrill()
	{
		Field field = Approaching();
		Assert.NotEqual(DrillPhase.None, field.Decide());
		Assert.True(field.Drill.Board.IsActive(0));

		field.Order = default;
		Assert.Equal(DrillPhase.None, field.Decide());
		Assert.False(field.Drill.Board.IsActive(0));
	}

	[Fact]
	public void AnEnemyOnThePoint_IsTheThreatPoint_AndTheSquadSetsItsBase()
	{
		Field field = Approaching();
		Assert.Equal(DrillPhase.SetBase, field.Decide());
		Assert.True(field.Drill.Board.TryThreat(0, out Vector3 threat));
		Assert.Equal(Point + new Vector3(0f, 0f, 2f), threat);
		Assert.Equal(DrillTask.FirePosition, field.Directive(0).Task);
		Assert.All(Enumerable.Range(1, 6), i => Assert.Equal(DrillTask.Hold, field.Directive(i).Task));
	}

	[Fact]
	public void TheThreatPoint_IsWhereMostOfTheSquadWasFiredUponFrom()
	{
		Field field = Approaching();
		Vector3 flank = Point + new Vector3(20f, 0f, -10f);
		field.See(4, flank);
		field.Fire(1, flank + new Vector3(1f, 0f, 0f));
		field.Fire(2, flank + new Vector3(-1f, 0f, 2f));

		Assert.Equal(DrillPhase.GoToGround, field.Decide());
		Assert.True(field.Drill.Board.TryThreat(0, out Vector3 threat));
		Assert.Equal(flank, threat);
	}

	[Fact]
	public void WhereRoundsCameFrom_IsTheSquadsForEightSeconds_AndNeverTheSides()
	{
		var field = new Field(Squad(4, gunner: true, Point + new Vector3(0f, 0f, -30f)));
		Vector3 from = Point + new Vector3(4f, 0f, 0f);
		field.Fire(1, from);
		field.Decide();
		Assert.Contains(field.Drill.Board.EntriesOf(0).ToArray(), e => e.FiredFrom && e.Feet == from);
		Assert.Equal(0, field.Memory.Count);

		// The window closes after three seconds; the point stays on the list for eight.
		field.Run(Traits.FiredFromTicks - (2 * Interval));
		Assert.Contains(field.Drill.Board.EntriesOf(0).ToArray(), e => e.FiredFrom && e.Feet == from);
		field.Run(3 * Interval);
		Assert.DoesNotContain(field.Drill.Board.EntriesOf(0).ToArray(), e => e.FiredFrom);
	}

	[Fact]
	public void AFiredFromPointBesideAContact_IsThatContact()
	{
		Field field = Approaching();
		field.Fire(1, Point + new Vector3(1f, 0f, 3f));
		field.Decide();
		Assert.Single(field.Drill.Board.EntriesOf(0).ToArray());
	}

	[Fact]
	public void AnEnemyFarFromTheSquadAndItsWay_IsNotOnItsList()
	{
		var field = new Field(Squad(4, gunner: true, Point + new Vector3(0f, 0f, -30f)));
		field.See(3, Point + new Vector3(90f, 0f, 0f));
		Assert.Equal(DrillPhase.Approach, field.Decide());
		Assert.Empty(field.Drill.Board.EntriesOf(0).ToArray());
	}

	[Fact]
	public void AnEnemyBehindTheSquad_IsNotOnItsList_UntilItFiresOnIt()
	{
		// T8's suite: the riflemen sent on from the middle node to the far one set a base facing
		// the raider 50 m behind them, and bounded, and the far node was not taken in a minute.
		var field = new Field(Squad(4, gunner: true, Point + new Vector3(0f, 0f, -40f)));
		Vector3 behind = Centroid(field.Members) + new Vector3(0f, 0f, -40f);
		field.See(3, behind);
		Assert.Equal(DrillPhase.Approach, field.Decide());
		Assert.Empty(field.Drill.Board.EntriesOf(0).ToArray());

		// Fired upon from it, it is the squad's to answer.
		field.Fire(1, behind + new Vector3(0f, 0f, 2f));
		Assert.Equal(DrillPhase.GoToGround, field.Decide());
		Assert.Equal(behind, Assert.Single(field.Drill.Board.EntriesOf(0).ToArray()).Feet);
		Assert.True(field.Drill.Board.TryThreat(0, out Vector3 threat));
		Assert.Equal(behind, threat);
	}

	[Fact]
	public void AnEnemyAbreastOfTheSquad_IsOnItsList()
	{
		// Behind is more than BehindMeters back along the line to the point; just short of it
		// and 40 m out to the side is beside the squad, not behind it.
		var field = new Field(Squad(4, gunner: true, Point + new Vector3(0f, 0f, -40f)));
		Vector3 abreast = Centroid(field.Members) + new Vector3(40f, 0f, 0.5f - SquadDrill.BehindMeters);
		field.See(3, abreast);
		Assert.NotEqual(DrillPhase.Approach, field.Decide());
		Assert.Equal(abreast, Assert.Single(field.Drill.Board.EntriesOf(0).ToArray()).Feet);
	}

	private static Vector3 Centroid(DrillMember[] members)
	{
		Vector3 sum = Vector3.Zero;
		foreach (DrillMember member in members)
		{
			sum += member.Position;
		}

		return sum / members.Length;
	}

	[Fact]
	public void NearThePoint_AnEnemyBehindTheSquad_IsStillOnItsList()
	{
		// Within 20 m of where its order goes the squad's fight is on every side of it.
		var field = new Field(Squad(4, gunner: true, Point + new Vector3(0f, 0f, -15f)));
		field.See(3, Point + new Vector3(0f, 0f, -45f));
		field.Decide();
		Assert.Single(field.Drill.Board.EntriesOf(0).ToArray());
	}

	[Fact]
	public void TheBase_TakesALowWallInTheBand_OffTheAssaultsAxis()
	{
		Field field = Approaching();
		Vector3 threat = Point + new Vector3(0f, 0f, 2f);

		// Two walls 30 m short of the enemy, both in the gunner's reach: one straight down the
		// assault's axis, one swung 40 degrees off it.
		Vector3 onAxis = threat + new Vector3(0f, 0f, -30f);
		Vector3 off = threat + new Vector3(30f * MathF.Sin(0.7f), 0f, -30f * MathF.Cos(0.7f));
		field.Members[0].Position = (onAxis + off) * 0.5f + new Vector3(0f, 0f, -3f);
		field.Wall(onAxis);
		field.Wall(off, -0.7f);

		field.Decide();
		DrillDirective gunner = field.Directive(0);
		Assert.Equal(DrillTask.FirePosition, gunner.Task);
		Assert.True(gunner.HasSpot && gunner.Covered);
		float range = new Vector2(gunner.Spot.X - threat.X, gunner.Spot.Z - threat.Z).Length();
		Assert.InRange(range, Traits.FireMinMeters, Traits.FireMaxMeters);
		Assert.True(gunner.Spot.X > 8f, $"the spot down the axis was taken: {gunner.Spot}");
	}

	[Fact]
	public void ABaseWithNoWallInReach_ClosesToTheBand_InTheOpen()
	{
		var field = new Field(Squad(3, gunner: true, Point + new Vector3(0f, 0f, -60f)));
		Vector3 threat = Point + new Vector3(0f, 0f, 2f);
		field.See(3, threat);
		field.Decide();

		DrillDirective gunner = field.Directive(0);
		Assert.True(gunner.HasSpot);
		Assert.False(gunner.Covered);
		float range = new Vector2(gunner.Spot.X - threat.X, gunner.Spot.Z - threat.Z).Length();
		Assert.InRange(range, Traits.FireMaxMeters - 4f, Traits.FireMaxMeters);

		// Inside the band already, it fires from where it stands.
		var near = new Field(Squad(3, gunner: true, Point + new Vector3(0f, 0f, -28f)));
		near.See(3, threat);
		near.Decide();
		Assert.False(near.Directive(0).HasSpot);
	}

	[Fact]
	public void FiredUpon_EveryoneGoesToCoverFromTheThreatPoint_OnTheWay()
	{
		Field field = Approaching();
		Vector3 start = field.Members[1].Position;
		field.Wall(start + new Vector3(0f, 0f, 4f));
		field.Fire(1, Point + new Vector3(0f, 0f, 2f));
		Assert.Equal(DrillPhase.GoToGround, field.Decide());

		DrillDirective ground = field.Directive(1);
		Assert.Equal(DrillTask.Ground, ground.Task);
		Assert.True(ground.HasSpot && ground.Covered);

		// Behind the wall from the enemy, which is the near side of it, and no further from
		// the point than where it stood.
		Assert.True(ground.Spot.Z < start.Z + 4f);
		Assert.True(ground.Spot.DistanceTo(Point) < start.DistanceTo(Point) + SquadDrill.GroundBackMeters);
	}

	[Fact]
	public void ABoundSpot_GainsGround_AndIsKeptForTheWholeBound()
	{
		Field field = Approaching();
		field.Walk = false;
		Vector3 start = field.Members[1].Position;

		// A wall two metres on is a shorter walk, and gains too little ground to be a bound.
		field.Wall(start + new Vector3(0f, 0f, 2.5f));
		field.Wall(start + new Vector3(0f, 0f, 7f));
		field.Decide();
		Assert.True(field.Drill.Board.PhaseOf(0) == DrillPhase.SetBase);

		// The gunner fires: the first half bounds.
		field.Members[0].LastShotTick = field.Tick + (uint)Interval;
		Assert.Equal(DrillPhase.Bound, field.Decide());
		DrillDirective bound = field.Directive(1);
		Assert.Equal(DrillTask.Bound, bound.Task);
		Assert.True(bound.HasSpot && bound.Covered);
		Assert.True(start.DistanceTo(Point) - bound.Spot.DistanceTo(Point) >= Traits.BoundProgressMeters);
		Assert.True(bound.Spot.DistanceTo(Point + new Vector3(0f, 0f, 2f)) >= Traits.BoundStandoffMeters);

		// Halfway there, the spot is the same, though it no longer gains five metres on it.
		field.Members[1].Position = (start + bound.Spot) * 0.5f;
		field.Members[0].LastShotTick = field.Tick + (uint)Interval;
		field.Decide();
		Assert.Equal(bound.Spot, field.Directive(1).Spot);
	}

	[Fact]
	public void WithNoWallForABound_ItIsAnOpenPointOnTheAxis_ShortOfTheThreat()
	{
		Field field = Approaching();
		field.Walk = false;
		field.Decide();
		field.Members[0].LastShotTick = field.Tick + (uint)Interval;
		Assert.Equal(DrillPhase.Bound, field.Decide());

		var moving = Enumerable.Range(1, 6).Where(i => field.Directive(i).Task == DrillTask.Bound).ToArray();
		Assert.Equal(3, moving.Length);
		foreach (int i in moving)
		{
			DrillDirective d = field.Directive(i);
			Assert.True(d.HasSpot);
			Assert.False(d.Covered);
			Assert.InRange(field.Members[i].Position.DistanceTo(Point) - d.Spot.DistanceTo(Point), 6f, 8.5f);
		}

		// Side by side, a rank apart and more: members two metres apart end up five apart.
		var spots = moving.Select(i => field.Directive(i).Spot).ToArray();
		for (int a = 0; a < spots.Length; a++)
		{
			for (int b = a + 1; b < spots.Length; b++)
			{
				Assert.True(spots[a].DistanceTo(spots[b]) >= Traits.OpenRankMeters, $"{spots[a]} and {spots[b]}");
			}
		}

		// Inside the standoff, it stays where it is.
		var close = new Field(Squad(4, gunner: true, Point + new Vector3(0f, 0f, -13f)));
		close.See(3, Point + new Vector3(0f, 0f, 2f));
		close.Walk = false;
		close.Decide();
		close.Members[0].LastShotTick = close.Tick + (uint)Interval;
		close.Decide();
		Assert.All(Enumerable.Range(1, 4).Where(i => close.Directive(i).Task == DrillTask.Bound),
			i => Assert.True(close.Directive(i).Spot.DistanceTo(Point + new Vector3(0f, 0f, 2f)) >= Traits.BoundStandoffMeters
				|| close.Directive(i).Spot == close.Members[i].Position));
	}

	[Fact]
	public void AnOpenBound_StopsShortOfTheStandoff()
	{
		// From bounding range an 8 m step cannot reach the 12 m standoff; a longer one can,
		// and is halved until it does not.
		DrillTraits d = Traits;
		var longStride = new DrillTraits(d.IntervalTicks, d.QuietTicks, d.GroundMaxTicks, d.BaseMaxTicks,
			d.BaseFiringTicks, d.BoundMaxTicks, d.GrenadeWaitTicks, d.GrenadesMaxTicks, d.DrillMaxTicks, d.DrillRetryTicks,
			d.FiredFromTicks, d.ThreatMeters, d.FiredNearMeters, d.AssaultMeters, d.FarMeters, d.RangeHysteresisMeters,
			d.FireMinMeters, d.FireMaxMeters, d.FireSearchMeters, d.FireOffAxisRadians, d.BoundSearchMeters,
			d.BoundProgressMeters, d.BoundOffLineMeters, d.BoundStandoffMeters, openBoundMeters: 16f, d.OpenRankMeters,
			d.ArriveMeters, d.LiftMeters, d.ShiftMeters, d.HalvesFrom);
		Vector3 enemy = Point + new Vector3(0f, 0f, 2f);
		var field = new Field(Squad(3, gunner: true, enemy + new Vector3(0f, 0f, -23f)), traits: longStride);
		field.See(3, enemy);
		field.Walk = false;
		field.Decide();
		field.Members[0].LastShotTick = field.Tick + (uint)Interval;
		Assert.Equal(DrillPhase.Bound, field.Decide());

		foreach (int i in Enumerable.Range(1, 3))
		{
			DrillDirective bound = field.Directive(i);
			Assert.Equal(DrillTask.Bound, bound.Task);
			Assert.True(bound.Spot.DistanceTo(enemy) >= Traits.BoundStandoffMeters, $"{bound.Spot}");
			Assert.InRange(field.Members[i].Position.DistanceTo(bound.Spot), 7f, 9f);
		}
	}

	[Fact]
	public void TwoMembers_NeverTakeOneSpot()
	{
		Field field = Approaching();
		Vector3 start = field.Members[1].Position;
		field.Wall(start + new Vector3(0f, 0f, 7f));
		field.Fire(1, Point);
		field.Fire(2, Point);
		field.Fire(3, Point);
		field.Decide();

		var spots = Enumerable.Range(1, 6).Select(field.Directive).Where(d => d.HasSpot).Select(d => d.Spot).ToArray();
		for (int a = 0; a < spots.Length; a++)
		{
			for (int b = a + 1; b < spots.Length; b++)
			{
				Assert.True(spots[a].DistanceTo(spots[b]) >= CoverBoard.ClaimMeters, $"{spots[a]} and {spots[b]}");
			}
		}
	}

	[Fact]
	public void TheSquadWalksIn_BoundByBound_AndAssaultsTheEnemyOnThePoint()
	{
		Field field = Approaching();
		var phases = new List<DrillPhase>();
		for (int t = 0; t < SimConfig.TickRate * 60 && field.Drill.Board.PhaseOf(0) != DrillPhase.Rush; t += Interval)
		{
			DrillPhase phase = field.Decide();
			if (phases.Count == 0 || phases[^1] != phase)
			{
				phases.Add(phase);
			}
		}

		Assert.Equal(new[] { DrillPhase.SetBase, DrillPhase.Bound, DrillPhase.Rush }, phases);
		Assert.True(field.Drill.ContextOf(0).Bounds >= 2, $"{field.Drill.ContextOf(0).Bounds} bounds");
	}

	[Fact]
	public void WithoutAGunner_ABoundItsHoldingHalfCannotCover_SendsThatHalfToFire_NotTheHalfOnItsWay()
	{
		// Four riflemen 30 m short of an enemy on the point; half B has no shot from where it holds.
		var field = new Field(Squad(4, gunner: false, Point + new Vector3(0f, 0f, -30f)));
		field.See(3, Point + new Vector3(0f, 0f, 2f));
		field.Silent.UnionWith(new[] { 2, 3 });

		// B bounds first, behind A's fire; then the bound changes hands, and A goes.
		for (int guard = 0; !(field.Decide() == DrillPhase.Bound && field.Directive(0).Task == DrillTask.Bound); guard++)
		{
			Assert.True(guard < LoopLimit, "it never got out of the loop");
		}

		// Nothing covers A: the bound stops, and B goes to its fire positions while A holds.
		for (int guard = 0; field.Decide() != DrillPhase.SetBase; guard++)
		{
			Assert.True(guard < LoopLimit, "it never got out of the loop");
		}

		Assert.False(field.Drill.ContextOf(0).Is(DrillFact.BaseLate));
		DrillTask[] Tasks() => Enumerable.Range(0, 4).Select(i => field.Directive(i).Task).ToArray();
		Assert.Equal(new[] { DrillTask.Hold, DrillTask.Hold, DrillTask.FirePosition, DrillTask.FirePosition }, Tasks());

		// A does not bound again on its own fire, a decision later, while B still cannot cover it.
		for (int i = 0; i < 3; i++)
		{
			Assert.Equal(DrillPhase.SetBase, field.Decide());
			Assert.Equal(new[] { DrillTask.Hold, DrillTask.Hold, DrillTask.FirePosition, DrillTask.FirePosition }, Tasks());
		}

		// Once B fires, A goes on.
		field.Silent.Clear();
		field.Decide();
		Assert.Equal(DrillPhase.Bound, field.Decide());
		Assert.Equal(new[] { DrillTask.Bound, DrillTask.Bound, DrillTask.Hold, DrillTask.Hold }, Tasks());
	}

	[Fact]
	public void InTheRush_TheBaseLiftsItsFire_ShiftsItBeyond_AndElseFollowsTheAssault()
	{
		Field field = Approaching();
		Vector3 enemy = Point + new Vector3(0f, 0f, 2f);
		Vector3 beyond = enemy + new Vector3(3f, 0f, 15f);
		field.See(4, beyond);
		for (int guard = 0; field.Drill.Board.PhaseOf(0) != DrillPhase.Rush; guard++)
		{
			Assert.True(guard < LoopLimit, "it never got out of the loop");
			field.Decide();
		}

		Assert.Equal(enemy, field.Drill.Board.SuppressOf(0)[0]);
		for (int guard = 0; !field.Drill.Board.IsLifted(0); guard++)
		{
			Assert.True(guard < LoopLimit, "it never got out of the loop");
			field.Decide();
		}

		Assert.Equal(new[] { beyond }, field.Drill.Board.SuppressOf(0).ToArray());
		Assert.Equal(DrillTask.FirePosition, field.Directive(0).Task);

		// Nothing beyond to shift to: the base goes onto the point with the assault.
		field.Dead.Add(OwnerId.ForPeer(4));
		field.Decide();
		Assert.Empty(field.Drill.Board.SuppressOf(0).ToArray());
		Assert.Equal(DrillTask.Rush, field.Directive(0).Task);
	}

	[Fact]
	public void AnElementOnAPointAnEnemyIsStillOn_DoesNotHoldIt()
	{
		Field field = Approaching();
		field.Members[1].Position = Point + new Vector3(0f, 0f, -8f);
		field.Decide();
		Assert.Equal((byte)DrillObjective.Approach, field.Drill.ContextOf(0).Get(DrillFact.Objective));

		field.Dead.Add(OwnerId.ForPeer(3));
		field.Decide();
		Assert.Equal((byte)DrillObjective.On, field.Drill.ContextOf(0).Get(DrillFact.Objective));
		Assert.Equal(DrillPhase.HoldPoint, field.Drill.Board.PhaseOf(0));
		Assert.All(Enumerable.Range(0, 7), i => Assert.Equal(DrillTask.None, field.Directive(i).Task));
	}

	[Fact]
	public void ANewOrder_StartsTheDrillAgain()
	{
		Field field = Approaching();
		field.Walk = false;
		field.Run(Traits.BaseMaxTicks + (4 * Interval));
		Assert.Equal(DrillPhase.Bound, field.Drill.Board.PhaseOf(0));
		Assert.True(field.Drill.ContextOf(0).Intent.BoundSerial > 0);

		// The base is sent again, and no bound has begun under the new order.
		field.Order.OrderedTick = field.Tick;
		Assert.Equal(DrillPhase.SetBase, field.Decide());
		Assert.Equal((byte)DrillBase.Moving, field.Drill.ContextOf(0).Get(DrillFact.Base));
		Assert.Equal(0, field.Drill.ContextOf(0).Intent.BoundSerial);
		Assert.All(Enumerable.Range(1, 6), i => Assert.Equal(DrillTask.Hold, field.Directive(i).Task));
	}

	[Fact]
	public void SquadsDecideOnTheirOwnTicks_AtMostOneATick()
	{
		var drill = new SquadDrill(new DrillPlanning(Traits));
		for (uint t = 0; t < 100; t++)
		{
			int due = Enumerable.Range(0, SquadBoard.DefaultSquads).Count(s => drill.IsDue(s, t));
			Assert.InRange(due, 0, 1);
		}

		Assert.Equal(10, Enumerable.Range(0, 100).Count(t => drill.IsDue(3, (uint)t)));
	}

	// ---- determinism and allocation ----------------------------------------

	/// <summary>Eight squads at once, each a gunner and five riflemen walking at its own enemy, fired upon now and then.</summary>
	private static (Field[] fields, SquadDrill drill) Population(IFactory factory = null)
	{
		var shared = new SquadDrill(new DrillPlanning(Traits, factory));
		var fields = new Field[SquadBoard.DefaultSquads];
		for (int s = 0; s < fields.Length; s++)
		{
			fields[s] = new Field(Squad(5, gunner: true, Point + new Vector3(s * 3f, 0f, -40f - s), (ushort)(1 + (s * 6))));
			fields[s].See(3 + s, Point + new Vector3(s * 2f, 0f, 2f));
			fields[s].Wall(Point + new Vector3(s * 3f, 0f, -30f));
			fields[s].Wall(Point + new Vector3(s * 3f - 4f, 0f, -20f));
		}

		return (fields, shared);
	}

	private static void Tick(Field[] fields, SquadDrill drill, uint t)
	{
		for (int s = 0; s < fields.Length; s++)
		{
			Field f = fields[s];
			if (t % 600 == (uint)(s * 37) % 600)
			{
				f.Fire(1, Point + new Vector3(s * 2f, 0f, 2f));
			}

			if (t % 1800 == 0)
			{
				// Back where it started, every thirty seconds, so the drill goes round again.
				for (int i = 0; i < f.Members.Length; i++)
				{
					f.Members[i].Position = Point + new Vector3((s * 3f) + (i * 2f), 0f, -40f - s);
				}

				f.Order.OrderedTick = t;
			}

			if (!drill.IsDue(s, t))
			{
				continue;
			}

			f.Tick = t;
			for (int i = 0; i < f.Members.Length; i++)
			{
				f.Members[i].Body.From = f.Members[i].Position;
				if (f.Members[i].FiredUpon && t - f.Members[i].FiredTick > FiredUpon.WindowTicks)
				{
					f.Members[i].FiredUpon = false;
				}
			}

			DrillPhase phase = drill.Decide(s, t, f.Order, f.Members, f.Memory, null, f.Cover, ReadOnlySpan<Vector3>.Empty);
			for (int i = 0; i < f.Members.Length; i++)
			{
				if (!drill.Board.TryGet(s, f.Members[i].UnitId, out DrillDirective d))
				{
					continue;
				}

				Vector3 to = d.Task == DrillTask.Rush ? Point : d.HasSpot ? d.Spot : f.Members[i].Position;
				Vector3 step = to - f.Members[i].Position;
				step.Y = 0f;
				f.Members[i].Position += step.Length() <= 0.75f ? step : step.Normalized() * 0.75f;
				if (d.Task == DrillTask.FirePosition || (d.Task == DrillTask.Hold && phase == DrillPhase.Bound))
				{
					f.Members[i].LastShotTick = t;
				}
			}

			// The side keeps seeing the enemy.
			f.Memory.Observe(t, 0, new KnownContact { OwnerId = OwnerId.ForPeer(3 + s), Position = Point + new Vector3(s * 2f, 0f, 2f) });
		}
	}

	private static ulong Trace(int ticks)
	{
		(Field[] fields, SquadDrill drill) = Population();
		ulong hash = 14695981039346656037ul;
		for (uint t = 1; t <= ticks; t++)
		{
			Tick(fields, drill, t);
			for (int s = 0; s < fields.Length; s++)
			{
				foreach (DrillDirective d in drill.Board.DirectivesOf(s))
				{
					hash = (hash ^ ((uint)d.Task | ((uint)drill.Board.PhaseOf(s) << 8) | ((uint)(d.Spot.X * 10f) << 16)))
						* 1099511628211ul;
				}
			}
		}

		return hash;
	}

	[Fact]
	public void TheDrill_IsDeterministic_AndNotTrivial()
	{
		Assert.Equal(Trace(3600), Trace(3600));

		(Field[] fields, SquadDrill drill) = Population();
		var seen = new HashSet<DrillPhase>();
		for (uint t = 1; t <= 3600; t++)
		{
			Tick(fields, drill, t);
			for (int s = 0; s < fields.Length; s++)
			{
				seen.Add(drill.Board.PhaseOf(s));
			}
		}

		Assert.Superset(new HashSet<DrillPhase> { DrillPhase.SetBase, DrillPhase.Bound, DrillPhase.GoToGround }, seen);
	}

	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public void EightSquads_AllocateNothingOnTheTick_WithThePooledFactory(bool pooled)
	{
		// The unpooled row is the guard's own check: FluidHTN's DefaultFactory allocates on
		// every replan, so a measurement that saw nothing there would be seeing nothing at all.
		(Field[] fields, SquadDrill drill) = Population(pooled ? null : new DefaultFactory());
		for (uint t = 1; t <= 1800; t++)
		{
			Tick(fields, drill, t);
		}

		long before = GC.GetAllocatedBytesForCurrentThread();
		for (uint t = 1801; t <= 1800 + 7200; t++)
		{
			Tick(fields, drill, t);
		}

		long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
		Assert.True(drill.ContextOf(0).Bounds > 0);
		if (pooled)
		{
			Assert.Equal(0, bytes);
		}
		else
		{
			Assert.True(bytes > 0);
		}
	}
	// ---- the unit's half ----------------------------------------------------

	private static readonly UnitTraits Rifleman = new(45f, 40f, 1.5f, 20f, UnitDoctrine.Rifleman);
	private static readonly UnitTraits Gunner = new(45f, 45f, 1.5f, 15f, UnitDoctrine.Gunner);
	private static readonly UnitTraits Sniper = new(70f, 70f, 1.5f, 20f, UnitDoctrine.Marksman);

	/// <summary>Where a unit stands: 30 m short of the point.</summary>
	private static readonly Vector3 Short = Point + new Vector3(0f, 0f, -30f);

	/// <summary>Its post on the point, and where a rush goes when there is no point.</summary>
	private static readonly Vector3 Post = Point + new Vector3(3f, 0f, 0f);
	private static readonly Vector3 RushTo = Point + new Vector3(0f, 0f, 2f);
	private static readonly Vector3 Aim = RushTo + new Vector3(0f, GunnerPlan.SuppressAimMeters, 0f);

	/// <summary>Its drill spot, five metres on.</summary>
	private static readonly Vector3 Spot = Short + new Vector3(0f, 0f, 5f);

	private sealed class Squaddie
	{
		public readonly Domain<UnitContext> Domain;
		public readonly Planner<UnitContext> Planner = new();
		public readonly UnitContext C;
		public readonly List<string> Plans = new();

		public MicroAutonomy Autonomy = MicroAutonomy.On;
		public FireMemory Fire;
		public bool HasTarget;
		public int Rounds = 150;

		public Squaddie(UnitTraits? traits = null, bool executingConditions = true)
		{
			var factory = new PooledHtnFactory();
			Domain = UnitDomain.Build(factory, executingConditions);
			C = new UnitContext(factory, UnitPlanTraits.Default, traits ?? Rifleman);
			C.Init();
			C.PlannerState.OnNewPlan = plan => Plans.Add(Names(plan));
			C.PlannerState.OnReplacePlan = (_, _, plan) => Plans.Add(Names(plan));

			C.Tick = 1000;
			C.Position = Short;
			C.FallbackPoint = new Vector3(-40f, 0.5f, -40f);
			C.HasSquad = true;
			C.SquadAlive = 7;
			C.SquadCentroid = Short;
			C.ObjectivePoint = Point;
			C.ObjectiveRadius = PointRadius;
			C.ObjectivePost = Post;
		}

		public void Order(OrderKind kind, Vector3 target) => C.Track(C.Position, new UnitOrder
		{
			Kind = kind,
			Target = target,
			Anchor = C.Position,
		}, HasTarget, RushTo);

		/// <summary>One scan: what its drill tells it, and whether its order is onto the point.</summary>
		public void Scan(DrillPhase phase, DrillTask task, Vector3? spot = null, bool covered = true, bool fires = true,
			bool objective = true, SquadPhase squad = SquadPhase.Moving, Vector3? fireCover = null, Vector3? cover = null)
		{
			C.HasFireCoverPoint = fireCover.HasValue;
			C.FireCoverPoint = fireCover ?? Vector3.Zero;
			C.HasCoverPoint = cover.HasValue;
			C.CoverPoint = cover ?? Vector3.Zero;
			C.SenseDrill(phase, task, spot.HasValue, spot ?? Vector3.Zero, covered, fires, Aim, RushTo);
			C.Encode(new UnitSense
			{
				HealthFraction = 1f,
				Odds = OddsBand.Even,
				HasSquad = true,
				SquadPhase = squad,
				Objective = objective,
				HasFireCover = fireCover.HasValue,
				FireCoverHides = fireCover.HasValue,
				HasCover = cover.HasValue,
				CoverHides = cover.HasValue,
			});
		}

		public void Round() => FiredUpon.Record(ref Fire, FireKind.NearMiss, RushTo + new Vector3(0f, 1.5f, 0f),
			OwnerId.ForPeer(7), C.Tick);

		public void Tick(int times = 1)
		{
			for (int i = 0; i < times; i++)
			{
				C.Tick++;
				C.SenseAutonomy(Autonomy);
				C.SenseFire(FiredUpon.KindAt(Fire, C.Tick), Fire.Origin, Fire.LastTick, Fire.LastHitTick);
				C.SenseWeapon(false, 0, Rounds);
				C.Track(C.Position, C.Order, HasTarget, RushTo);
				Planner.Tick(Domain, C);
			}
		}

		public UnitGoal Goal => C.Intent.Goal;
	}

	/// <summary>A unit with micro-autonomy on, moved onto the point, its squad's drill telling it <paramref name="task"/>.</summary>
	private static Squaddie MovedOnto(DrillPhase phase, DrillTask task, Vector3? spot = null, UnitTraits? traits = null,
		bool covered = true, bool fires = true, bool executingConditions = true)
	{
		var unit = new Squaddie(traits, executingConditions);
		unit.Order(OrderKind.Move, Point);
		unit.Scan(phase, task, spot, covered, fires);
		unit.Tick();
		return unit;
	}

	[Theory]
	[InlineData(DrillPhase.GoToGround, DrillTask.Ground, UnitGoal.DrillGround)]
	[InlineData(DrillPhase.SetBase, DrillTask.FirePosition, UnitGoal.DrillFirePosition)]
	[InlineData(DrillPhase.Bound, DrillTask.Bound, UnitGoal.DrillBound)]
	[InlineData(DrillPhase.Bound, DrillTask.Hold, UnitGoal.DrillHold)]
	[InlineData(DrillPhase.Rush, DrillTask.Rush, UnitGoal.DrillRush)]
	[InlineData(DrillPhase.Bound, DrillTask.Overwatch, UnitGoal.TakePoint)]
	[InlineData(DrillPhase.Approach, DrillTask.None, UnitGoal.TakePoint)]
	public void UnderAMoveOntoThePoint_EachDrillTask_IsItsOperator(DrillPhase phase, DrillTask task, UnitGoal goal)
	{
		Squaddie unit = MovedOnto(phase, task, Spot);
		Assert.True(unit.C.Is(UnitFact.Objective));
		Assert.Equal(goal, unit.Goal);
		Assert.Equal(task is DrillTask.None or DrillTask.Overwatch ? false : true, UnitContext.IsTactic(unit.Goal));
	}

	[Fact]
	public void UnderAnAttack_ItsPartInTheDrill_ComesBeforeFightingFromCover()
	{
		var unit = new Squaddie { HasTarget = true };
		unit.Order(OrderKind.Attack, Point + new Vector3(60f, 0f, 0f));
		unit.Scan(DrillPhase.Bound, DrillTask.Hold, Spot, objective: false, cover: Short + new Vector3(2f, 0f, 0f));
		unit.Tick();
		Assert.False(unit.C.Is(UnitFact.Objective));
		Assert.Equal(UnitGoal.DrillHold, unit.Goal);

		unit.Scan(DrillPhase.Approach, DrillTask.None, objective: false, cover: Short + new Vector3(2f, 0f, 0f));
		unit.Tick();
		Assert.Equal(UnitGoal.HoldCover, unit.Goal);
	}

	[Fact]
	public void ABound_WalksToItsSpotWithoutStopping_AndHoldsInIt()
	{
		Squaddie unit = MovedOnto(DrillPhase.Bound, DrillTask.Bound, Spot);
		Assert.Equal(UnitMove.Point, unit.C.Intent.Move);
		Assert.Equal(Spot, unit.C.Intent.Point);
		Assert.False(unit.C.Intent.StopToFight);
		Assert.False(unit.C.Intent.FireAt);

		unit.C.Position = Spot + new Vector3(0.5f, 0f, 0f);
		unit.Tick();
		Assert.Equal((byte)CoverState.In, unit.C.Get(UnitFact.DrillSpot));
		Assert.Equal(UnitMove.Hold, unit.C.Intent.Move);
	}

	[Fact]
	public void TheBase_FiresAtTheThreatPoint_WhenItsLineIsClear_OnTheWayAndFromItsWall()
	{
		Squaddie unit = MovedOnto(DrillPhase.SetBase, DrillTask.FirePosition, Spot);
		Assert.True(unit.C.Intent.FireAt);
		Assert.Equal(Aim, unit.C.Intent.FirePoint);
		Assert.Equal(Spot, unit.C.Intent.Point);

		unit.Scan(DrillPhase.SetBase, DrillTask.FirePosition, Spot, fires: false);
		unit.Tick();
		Assert.False(unit.C.Intent.FireAt);
		Assert.Equal(UnitGoal.DrillFirePosition, unit.Goal);
	}

	[Fact]
	public void TheHalfHolding_FiresOnlyWhileTheOtherMoves_AndIsDownBetweenWhiles()
	{
		Squaddie unit = MovedOnto(DrillPhase.SetBase, DrillTask.Hold, Short);
		Assert.Equal(UnitGoal.DrillHold, unit.Goal);
		Assert.False(unit.C.Intent.FireAt);
		Assert.True(unit.C.Intent.Crouch);

		unit.Scan(DrillPhase.Bound, DrillTask.Hold, Short);
		unit.Tick();
		Assert.True(unit.C.Intent.FireAt);
		Assert.False(unit.C.Intent.Crouch);

		// In the open it gets down too, smaller; walking to its spot it does not.
		unit.Scan(DrillPhase.SetBase, DrillTask.Hold, Short, covered: false);
		unit.Tick();
		Assert.True(unit.C.Intent.Crouch);
		unit.Scan(DrillPhase.SetBase, DrillTask.Hold, Spot);
		unit.Tick();
		Assert.Equal(UnitMove.Point, unit.C.Intent.Move);
		Assert.False(unit.C.Intent.Crouch);

		// With a target of its own it stands and fights it.
		unit.HasTarget = true;
		unit.Scan(DrillPhase.SetBase, DrillTask.Hold, Short);
		unit.Tick();
		Assert.False(unit.C.Intent.Crouch);
	}

	[Fact]
	public void GoneToGround_ItGetsDown_InItsSpotOrWithNone_ButNotOnTheWay()
	{
		Squaddie unit = MovedOnto(DrillPhase.GoToGround, DrillTask.Ground, Spot);
		Assert.Equal(UnitMove.Point, unit.C.Intent.Move);
		Assert.False(unit.C.Intent.Crouch);

		unit.C.Position = Spot;
		unit.Tick();
		Assert.True(unit.C.Intent.Crouch);

		Squaddie open = MovedOnto(DrillPhase.GoToGround, DrillTask.Ground);
		Assert.Equal(UnitMove.Hold, open.C.Intent.Move);
		Assert.True(open.C.Intent.Crouch);
	}

	[Fact]
	public void ARush_GoesOntoThePoint_NotWaitingForTheSquad_OrOntoTheThreatPoint()
	{
		Squaddie unit = MovedOnto(DrillPhase.Rush, DrillTask.Rush);
		unit.C.SquadCentroid = Short + new Vector3(0f, 0f, -20f);
		unit.Tick();
		Assert.Equal(UnitGoal.DrillRush, unit.Goal);
		Assert.Equal(Post, unit.C.Intent.Point);
		Assert.False(unit.C.Intent.StopToFight);

		var attacker = new Squaddie();
		attacker.Order(OrderKind.Attack, Point + new Vector3(60f, 0f, 0f));
		attacker.Scan(DrillPhase.Rush, DrillTask.Rush, objective: false);
		attacker.Tick();
		Assert.Equal(UnitGoal.DrillRush, attacker.Goal);
		Assert.Equal(RushTo, attacker.C.Intent.Point);
	}

	[Theory]
	[InlineData(DrillTask.Ground)]
	[InlineData(DrillTask.FirePosition)]
	[InlineData(DrillTask.Bound)]
	[InlineData(DrillTask.Hold)]
	[InlineData(DrillTask.Rush)]
	public void MicroAutonomyOff_EndsEveryDrillTask_OnTheTick_AndTheMoveIsObeyed(DrillTask task)
	{
		Squaddie unit = MovedOnto(DrillPhase.Bound, task, Spot);
		Assert.True(UnitContext.IsTactic(unit.Goal));

		unit.Autonomy = MicroAutonomy.Off;
		unit.Tick();
		Assert.False(unit.C.Is(UnitFact.Objective));
		Assert.Equal(UnitGoal.Obey, unit.Goal);
	}

	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public void ANewTask_EndsTheOldOne_OnlyByItsExecutingCondition(bool executingConditions)
	{
		// Rush is the last of the drill's sequences: a bound is never replaced by it, only
		// ended because its task is no longer a bound (§3.4, P3).
		Squaddie unit = MovedOnto(DrillPhase.Bound, DrillTask.Bound, Spot, executingConditions: executingConditions);
		unit.Scan(DrillPhase.Rush, DrillTask.Rush);
		unit.Tick();
		Assert.Equal(executingConditions ? UnitGoal.DrillRush : UnitGoal.DrillBound, unit.Goal);
	}

	[Fact]
	public void ASquadFallingBack_EndsTheDrill()
	{
		var unit = new Squaddie();
		unit.Order(OrderKind.Attack, Point);
		unit.Scan(DrillPhase.Bound, DrillTask.Bound, Spot);
		unit.Tick();
		Assert.Equal(UnitGoal.DrillBound, unit.Goal);

		unit.Scan(DrillPhase.Bound, DrillTask.Bound, Spot, squad: SquadPhase.FallingBack);
		unit.Tick();
		Assert.NotEqual(UnitGoal.DrillBound, unit.Goal);
	}

	[Fact]
	public void ReactingToFire_NeverStopsABound_ButTakesTheHalfHoldingToCoverFromIt()
	{
		Vector3 fireCover = Short + new Vector3(3f, 0f, 1f);
		var bounding = new Squaddie();
		bounding.Order(OrderKind.Move, Point);
		bounding.Round();
		bounding.Scan(DrillPhase.Bound, DrillTask.Bound, Spot, fireCover: fireCover);
		bounding.Tick();
		Assert.Equal(UnitGoal.DrillBound, bounding.Goal);
		Assert.True(bounding.C.Get(UnitFact.FiredUpon) != (byte)FireKind.None);

		var holding = new Squaddie();
		holding.Order(OrderKind.Move, Point);
		holding.Round();
		holding.Scan(DrillPhase.Bound, DrillTask.Hold, Spot, fireCover: fireCover);
		holding.Tick();
		Assert.Equal(UnitGoal.TakeCoverFromFire, holding.Goal);

		// And a reaction under way is ended by a bound.
		holding.Scan(DrillPhase.Bound, DrillTask.Bound, Spot, fireCover: fireCover);
		holding.Tick();
		Assert.Equal(UnitGoal.DrillBound, holding.Goal);
	}

	[Fact]
	public void AMarksmanAsOverwatch_PeeksOnlyWhileAnElementMoves()
	{
		var unit = new Squaddie(Sniper);
		unit.Order(OrderKind.Move, Point);
		unit.Scan(DrillPhase.SetBase, DrillTask.Overwatch, objective: true);
		unit.C.SensePeek(true, Short, Short, true, OwnerId.ForPeer(7), standoff: false);
		unit.Tick(UnitPlanTraits.Default.MinHideTicks + 2);
		Assert.Equal((byte)PeekState.Ready, unit.C.Get(UnitFact.Peek));
		Assert.Equal(UnitGoal.ToHide, unit.Goal);

		unit.Scan(DrillPhase.Bound, DrillTask.Overwatch, objective: true);
		unit.C.SensePeek(true, Short, Short, true, OwnerId.ForPeer(7), standoff: false);
		unit.Tick();
		Assert.Equal(UnitGoal.PopOut, unit.Goal);
	}

	[Fact]
	public void TheBaseGunner_SpendsItsReserveWhileTheSquadMoves_AndOnlyThen()
	{
		var unit = new Squaddie(Gunner) { Rounds = 10 };
		unit.Order(OrderKind.Move, Point);
		unit.Scan(DrillPhase.Bound, DrillTask.FirePosition, Spot);
		unit.C.SenseSuppress(new[] { RushTo });
		unit.Tick();
		Assert.False(unit.C.Is(UnitFact.BeltLow));
		Assert.Equal(UnitGoal.SuppressPoint, unit.Goal);

		unit.Scan(DrillPhase.SetBase, DrillTask.FirePosition, Spot);
		unit.C.SenseSuppress(new[] { RushTo });
		unit.Tick();
		Assert.True(unit.C.Is(UnitFact.BeltLow));
		Assert.Equal(UnitGoal.Reload, unit.Goal);
	}

	[Fact]
	public void TheBaseGunner_IsBoundedByTheDrill_NotByItsOwnCap()
	{
		var unit = new Squaddie(Gunner);
		unit.Order(OrderKind.Move, Point);
		unit.Scan(DrillPhase.Bound, DrillTask.FirePosition, Spot);
		unit.C.SenseSuppress(new[] { RushTo });
		unit.Tick(UnitPlanTraits.Default.SuppressMaxTicks + 60);
		Assert.Equal(UnitGoal.SuppressPoint, unit.Goal);
		Assert.Equal(0, unit.C.SuppressionsGivenUp);

		// Out of the drill, under the same move onto the point, its own cap stands.
		unit.Scan(DrillPhase.Approach, DrillTask.None);
		unit.C.SenseSuppress(new[] { RushTo });
		unit.Tick(UnitPlanTraits.Default.SuppressMaxTicks + 60);
		Assert.Equal(1, unit.C.SuppressionsGivenUp);
	}

	[Fact]
	public void TheDrillGoals_AreProtocolNumbersAfterTheGunners()
	{
		Assert.Equal(28, (int)UnitGoal.DrillGround);
		Assert.Equal(29, (int)UnitGoal.DrillFirePosition);
		Assert.Equal(30, (int)UnitGoal.DrillBound);
		Assert.Equal(31, (int)UnitGoal.DrillHold);
		Assert.Equal(32, (int)UnitGoal.DrillRush);
		Assert.Equal(AiTaskFamily.Attack, AiGoals.Family(HtnDomainKind.Unit, (byte)UnitGoal.DrillBound));
		Assert.Equal("DrillTask", HtnSchema.Unit.FactName((int)UnitFact.DrillTask));
		Assert.Equal("Bound", HtnSchema.Unit.FactValue((int)UnitFact.DrillTask, (byte)DrillTask.Bound));
	}
}
