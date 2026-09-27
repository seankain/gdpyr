using System;
using System.Collections.Generic;
using FluidHTN;
using FluidHTN.Contexts;
using FluidHTN.Debug;
using FluidHTN.Factory;
using FluidHTN.Operators;
using FluidHTN.PrimitiveTasks;
using Godot;

namespace Gdpyr.Sim.Htn;

/// <summary>
/// The RTS unit's facts (docs/HTN_BOTS.md §5.2): one byte each, as FluidHTN stores
/// its world state. Only a change here makes the planner look again (§3.7).
/// </summary>
public enum UnitFact : byte
{
	/// <summary>The standing order's <see cref="OrderKind"/>: the top-level task, never second-guessed.</summary>
	Order,

	/// <summary><see cref="UnitContact"/>.</summary>
	Contact,

	/// <summary><see cref="HealthBand"/>.</summary>
	Health,

	/// <summary><see cref="OddsBand"/>, from a <see cref="Neighbourhood"/> survey.</summary>
	Odds,

	/// <summary>A squad-mate, or any friend within <see cref="Neighbourhood.EngagedRadiusMeters"/>, is in a fight.</summary>
	FriendEngaged,

	/// <summary>The squad's <see cref="Htn.SquadPhase"/>; <see cref="Htn.SquadPhase.Moving"/> for a unit in none.</summary>
	SquadPhase,

	/// <summary>It and what it would go after — its target, else the fight it would answer — are inside its defend leash.</summary>
	InLeash,

	/// <summary><see cref="UnitStance"/>.</summary>
	Stance,

	/// <summary>It was hit within <see cref="UnitPlanTraits.UnderFireTicks"/>.</summary>
	UnderFire,

	/// <summary>A friendly unit within <see cref="Neighbourhood.FriendRadiusMeters"/>.</summary>
	FriendNear,

	Count,
}

/// <summary>What this unit is in contact with.</summary>
public enum UnitContact : byte
{
	None = 0,

	/// <summary>The side remembers one within sensor range of the unit or its anchor (<see cref="ContactMemory"/>).</summary>
	SideKnown = 1,

	/// <summary>The unit has a target of its own.</summary>
	Own = 2,
}

/// <summary>Who gave the standing order, which decides whether the unit may act on its own (§8, D1).</summary>
public enum UnitStance : byte
{
	/// <summary>A person, a policy's seat, or nobody: the order is carried out and never abandoned.</summary>
	Obey = 0,

	/// <summary>A computer strategist: the unit may also retreat on its own.</summary>
	Autonomous = 1,
}

/// <summary>What a primitive task asks the unit to do: the operator's name, and the intent's.</summary>
public enum UnitGoal : byte
{
	None = 0,
	Retreat,
	TakeCover,
	Work,
	WaitForSquad,
	EngageFocus,
	Support,
	Advance,
	EngageInLeash,
	AnswerCall,
	HoldPost,
	Patrol,
	Obey,
}

/// <summary>How the unit manager picks where to walk for an intent.</summary>
public enum UnitMove : byte
{
	/// <summary>Where the order says, as it always has (<see cref="UnitBrain.Destination"/>).</summary>
	Order = 0,

	/// <summary>At the target when there is one, else to <see cref="UnitIntent.Point"/>.</summary>
	Target,

	/// <summary>To <see cref="UnitIntent.Point"/>.</summary>
	Point,

	/// <summary>Nowhere: stand where it is.</summary>
	Hold,
}

/// <summary>
/// What the running primitive task wants from the unit (docs/HTN_BOTS.md §4.2 rule 1):
/// where to go, whether to stand still to shoot, and what to prefer shooting. The
/// engine half — <c>UnitManager.SimulateUnit</c> — carries it out; target acquisition,
/// the Idle/Moving/Engaging state, steering and firing are unchanged.
/// </summary>
public struct UnitIntent
{
	public UnitGoal Goal;

	public UnitMove Move;

	public Vector3 Point;

	/// <summary>Stand still while engaging, as an attack-move does; false shoots on the walk, as a move order does.</summary>
	public bool StopToFight;

	/// <summary>Acquire this one when the sensor can see it; 0 for the nearest.</summary>
	public int TargetOwnerId;
}

/// <summary>
/// The thresholds the unit planner bands its facts at and paces, posts and takes
/// cover by (docs/HTN_BOTS.md §9, "bots become too good": every one is a knob).
/// </summary>
public readonly struct UnitPlanTraits
{
	public readonly float HealthOkFraction;
	public readonly float HealthCriticalFraction;
	public readonly float HealthHysteresis;

	/// <summary>How far a friend in a fight is still one to answer.</summary>
	public readonly float EngagedFriendMeters;

	/// <summary>How far ahead of its squad's centroid a unit advancing on an attack order stops to let it close.</summary>
	public readonly float PaceAheadMeters;

	/// <summary>How close the squad has to come again before a unit waiting for it walks on.</summary>
	public readonly float PaceResumeMeters;

	/// <summary>
	/// How long one wait may last. A squad that has not closed up in that time has a
	/// unit that is not coming — stuck, or gone another way — and the advance stops
	/// waiting for it. A slow squad closes up again and again, and is waited for as
	/// often as it takes.
	/// </summary>
	public readonly int PaceMaxWaitTicks;

	/// <summary>How far apart neighbouring posts on a defended ring are.</summary>
	public readonly float PostSpacingMeters;

	public readonly float PostMinRadiusMeters;

	/// <summary>The ring's largest radius, as a share of the unit's leash: a post is somewhere the leash reaches past.</summary>
	public readonly float PostLeashShare;

	/// <summary>How long after a hit a unit counts as under fire.</summary>
	public readonly int UnderFireTicks;

	/// <summary>How far from its anchor a remembered contact still turns a defended ring towards it.</summary>
	public readonly float ThreatBearingMeters;

	public UnitPlanTraits(float healthOkFraction, float healthCriticalFraction, float healthHysteresis,
		float engagedFriendMeters, float paceAheadMeters, float paceResumeMeters, int paceMaxWaitTicks,
		float postSpacingMeters, float postMinRadiusMeters, float postLeashShare, int underFireTicks,
		float threatBearingMeters)
	{
		HealthOkFraction = healthOkFraction;
		HealthCriticalFraction = healthCriticalFraction;
		HealthHysteresis = MathF.Max(healthHysteresis, 0f);
		EngagedFriendMeters = MathF.Max(engagedFriendMeters, 0f);
		PaceAheadMeters = MathF.Max(paceAheadMeters, 0f);
		PaceResumeMeters = Math.Clamp(paceResumeMeters, 0f, PaceAheadMeters);
		PaceMaxWaitTicks = Math.Max(paceMaxWaitTicks, 0);
		PostSpacingMeters = MathF.Max(postSpacingMeters, 0f);
		PostMinRadiusMeters = MathF.Max(postMinRadiusMeters, 0f);
		PostLeashShare = Math.Clamp(postLeashShare, 0f, 1f);
		UnderFireTicks = Math.Max(underFireTicks, 1);
		ThreatBearingMeters = MathF.Max(threatBearingMeters, 0f);
	}

	/// <summary>
	/// §5.2's numbers. The health bands are the ground bot's. A unit more than 8 m
	/// ahead of its squad waits until it is within 3 m, for 8 s at a stretch at most;
	/// posts stand 4 m apart on a ring of at least 4 m and at most half the leash.
	/// </summary>
	public static UnitPlanTraits Default => new(
		healthOkFraction: 0.6f,
		healthCriticalFraction: 0.3f,
		healthHysteresis: 0.05f,
		engagedFriendMeters: Neighbourhood.EngagedRadiusMeters,
		paceAheadMeters: 8f,
		paceResumeMeters: 3f,
		paceMaxWaitTicks: SimConfig.TickRate * 8,
		postSpacingMeters: 4f,
		postMinRadiusMeters: 4f,
		postLeashShare: 0.5f,
		underFireTicks: SimConfig.TickRate * 2,
		threatBearingMeters: 80f);
}

/// <summary>
/// One scan's worth of what a unit knows, before it is banded into facts. Filled by
/// the unit manager out of the side's <see cref="ContactMemory"/>, a
/// <see cref="Neighbourhood"/> survey, the <see cref="SquadBoard"/> and its
/// <see cref="SquadCensus"/>, and the unit's own body.
/// </summary>
public struct UnitSense
{
	/// <summary>The side remembers a contact within sensor range of the unit or its anchor.</summary>
	public bool SideKnown;

	public float HealthFraction;

	public OddsBand Odds;

	/// <summary>A squad-mate or a friend within <see cref="UnitPlanTraits.EngagedFriendMeters"/> is in a fight, and there is a point to go to.</summary>
	public bool FriendEngaged;

	public bool HasSquad;

	public SquadPhase SquadPhase;

	/// <summary>The order came from a computer strategist.</summary>
	public bool Autonomous;

	public bool UnderFire;

	public bool FriendNear;
}

/// <summary>
/// One unit's planning context: its facts, the places and ids its operators steer
/// by, and the intent the running operator last wrote (docs/HTN_BOTS.md §4.2).
///
/// The unit manager writes the knowledge — facts on the unit's ten-tick scan, the
/// order, position, target and leash every tick, the squad's census as it is
/// counted — and reads <see cref="Intent"/>. Operators are functions of this context
/// and nothing else, so what a unit does in a situation is a question for
/// <c>dotnet test</c>.
/// </summary>
public sealed class UnitContext : BaseContext
{
	private readonly byte[] _facts = new byte[(int)UnitFact.Count];

	private bool _sideKnown;
	private bool _hasTarget;
	private Vector3 _targetPoint;
	private uint _paceTick = uint.MaxValue;

	public UnitContext(IFactory factory, in UnitPlanTraits traits, in UnitTraits unitTraits)
	{
		Factory = factory;
		Traits = traits;
		UnitTraits = unitTraits;
	}

	public override IFactory Factory { get; protected set; }
	public override IPlannerState PlannerState { get; protected set; } = new DefaultPlannerState();
	public override List<string> MTRDebug { get; set; }
	public override List<string> LastMTRDebug { get; set; }
	public override bool DebugMTR => false;
	public override Queue<IBaseDecompositionLogEntry> DecompositionLog { get; set; }
	public override bool LogDecomposition => false;
	public override byte[] WorldState => _facts;

	public UnitPlanTraits Traits { get; }

	/// <summary>What this kind of unit is: its leash, its arrival radius.</summary>
	public UnitTraits UnitTraits { get; }

	/// <summary>What the running operator asked for. <see cref="UnitGoal.None"/> between tasks.</summary>
	public UnitIntent Intent;

	// ---- knowledge, written by the unit manager ----------------------------

	public uint Tick;

	/// <summary>Where the unit is, every tick.</summary>
	public Vector3 Position;

	/// <summary>Its standing order, every tick.</summary>
	public UnitOrder Order;

	/// <summary>Where the target an attack order named was last seen by the side. Only with <see cref="HasNamedTargetPoint"/>.</summary>
	public bool HasNamedTargetPoint;
	public Vector3 NamedTargetPoint;

	/// <summary>The squad's focus target (<see cref="SquadCensus.FocusOf"/>); 0 for none.</summary>
	public int FocusOwnerId;

	/// <summary>In a squad the census counted.</summary>
	public bool HasSquad;

	/// <summary>Its squad's living units, itself included.</summary>
	public int SquadAlive;

	public Vector3 SquadCentroid;

	/// <summary>Where the fight a squad-mate or a nearby friend is in is: the point <see cref="UnitGoal.Support"/> and <see cref="UnitGoal.AnswerCall"/> go to.</summary>
	public bool HasSupport;
	public Vector3 SupportPoint;

	/// <summary>Its post on its squad's defended ring. Only with <see cref="HasPost"/>.</summary>
	public bool HasPost;
	public Vector3 PostPoint;

	/// <summary>The squad's staging point, set by a computer strategist's commander (§5.3).</summary>
	public bool HasStaging;
	public Vector3 StagingPoint;

	/// <summary>Where a retreat goes: the staging point, else the nearest friendly barracks' rally point.</summary>
	public Vector3 FallbackPoint;

	/// <summary>The nearest friendly unit, for a builder under fire.</summary>
	public Vector3 FriendPoint;

	/// <summary>The tick the running operator started on.</summary>
	public uint GoalStartTick;

	/// <summary>True while an advance is holding for its squad to close up.</summary>
	public bool Waiting;

	/// <summary>Ticks the current wait has lasted, against <see cref="UnitPlanTraits.PaceMaxWaitTicks"/>.</summary>
	public int WaitedTicks;

	/// <summary>A wait ran out: this advance no longer waits for its squad.</summary>
	public bool PaceGivenUp;

	public byte Get(UnitFact fact) => GetState((int)fact);

	public bool Is(UnitFact fact) => GetState((int)fact) != 0;

	/// <summary>What a sensor writes. Marks the context dirty only when the value changes.</summary>
	public void Sense(UnitFact fact, byte value) => SetState((int)fact, value, true, EffectType.Permanent);

	public void Sense(UnitFact fact, bool value) => Sense(fact, value ? (byte)1 : (byte)0);

	/// <summary>A context with no squad is moving, not gathering: 0 is <see cref="Htn.SquadPhase.Gathering"/>.</summary>
	public override void Init()
	{
		base.Init();
		Sense(UnitFact.SquadPhase, (byte)SquadPhase.Moving);
	}

	/// <summary>Where an attack order is going: the named target's last-known position, else the ordered point.</summary>
	public Vector3 AdvancePoint => HasNamedTargetPoint ? NamedTargetPoint : Order.Target;

	/// <summary>Bands one scan into facts (§5.2's table), each against its last band where there is hysteresis.</summary>
	public void Encode(in UnitSense sense)
	{
		_sideKnown = sense.SideKnown;

		Sense(UnitFact.Health, (byte)HealthBands.Band(sense.HealthFraction, (HealthBand)Get(UnitFact.Health),
			Traits.HealthOkFraction, Traits.HealthCriticalFraction, Traits.HealthHysteresis));
		Sense(UnitFact.Odds, (byte)sense.Odds);
		Sense(UnitFact.FriendEngaged, sense.FriendEngaged);
		Sense(UnitFact.SquadPhase, (byte)(sense.HasSquad ? sense.SquadPhase : SquadPhase.Moving));
		Sense(UnitFact.Stance, (byte)(sense.Autonomous ? UnitStance.Autonomous : UnitStance.Obey));
		Sense(UnitFact.UnderFire, sense.UnderFire);
		Sense(UnitFact.FriendNear, sense.FriendNear);

		SenseContact();
		SenseLeash();
	}

	/// <summary>
	/// What changes between scans, every tick: where the unit is, its order, and
	/// whether it still has a target and where. A target that dies or slips out of
	/// the sensor ends <see cref="UnitGoal.EngageFocus"/> on the tick it goes, as
	/// today's code stops engaging on that tick.
	/// </summary>
	public void Track(Vector3 position, in UnitOrder order, bool hasTarget, Vector3 targetPoint)
	{
		Position = position;
		Order = order;
		_hasTarget = hasTarget;
		_targetPoint = targetPoint;

		Sense(UnitFact.Order, (byte)order.Kind);
		SenseContact();
		SenseLeash();
	}

	private void SenseContact() =>
		Sense(UnitFact.Contact, (byte)(_hasTarget ? UnitContact.Own : _sideKnown ? UnitContact.SideKnown : UnitContact.None));

	/// <summary>
	/// Today's leash test (<see cref="UnitBrain.InLeash"/>), on the target when there
	/// is one and else on the fight a friend is in. Defending and idle units only:
	/// nothing else has a leash.
	/// </summary>
	private void SenseLeash()
	{
		bool leash = false;
		if (Order.Kind is OrderKind.Defend or OrderKind.None)
		{
			if (_hasTarget)
			{
				leash = UnitBrain.InLeash(Position, Order.Anchor, _targetPoint, UnitTraits);
			}
			else if (HasSupport && Is(UnitFact.FriendEngaged))
			{
				leash = UnitBrain.InLeash(Position, Order.Anchor, SupportPoint, UnitTraits);
			}
		}

		Sense(UnitFact.InLeash, leash);
	}

	// ---- operators ---------------------------------------------------------

	/// <summary>Starts an operator: the intent is new, and so is its clock.</summary>
	public TaskStatus Begin(UnitGoal goal)
	{
		GoalStartTick = Tick;
		Waiting = false;
		WaitedTicks = 0;
		PaceGivenUp = false;
		_paceTick = uint.MaxValue;
		return Perform(goal);
	}

	/// <summary>Ends one, whether it was stopped, aborted or replaced: the unit goes back to its order.</summary>
	public void End() => Intent = default;

	/// <summary>
	/// One tick of an operator. Writes the intent and keeps running: every unit task
	/// lasts until a fact ends it. Idempotent — FluidHTN can run an operator twice in
	/// one tick (#21, §3.2) — so the one piece of state an operator keeps, how long an
	/// advance has waited, is counted once per tick.
	/// </summary>
	public TaskStatus Perform(UnitGoal goal)
	{
		Intent = new UnitIntent { Goal = goal, TargetOwnerId = FocusOwnerId };

		switch (goal)
		{
			case UnitGoal.Retreat:
				// Without stopping to fight: a unit that turned to shoot would not be retreating.
				return Go(FallbackPoint, stopToFight: false);

			case UnitGoal.TakeCover:
				return Go(FriendPoint, stopToFight: false);

			case UnitGoal.Work:
			case UnitGoal.EngageInLeash:
			case UnitGoal.Patrol:
			case UnitGoal.Obey:
				return FollowOrder();

			case UnitGoal.WaitForSquad:
				return HasStaging ? Go(StagingPoint, stopToFight: true) : Hold();

			case UnitGoal.EngageFocus:
				Intent.Move = UnitMove.Target;
				Intent.Point = AdvancePoint;
				Intent.StopToFight = true;
				return TaskStatus.Continue;

			case UnitGoal.Support:
				return Go(HasSupport ? SupportPoint : AdvancePoint, stopToFight: true);

			case UnitGoal.Advance:
				return KeepsPace() ? Hold() : Go(AdvancePoint, stopToFight: true);

			case UnitGoal.AnswerCall:
				return HasSupport ? Go(SupportPoint, stopToFight: true) : FollowOrder();

			case UnitGoal.HoldPost:
				return Order.Kind == OrderKind.Defend && HasPost ? Go(PostPoint, stopToFight: true) : FollowOrder();

			default:
				return TaskStatus.Failure;
		}
	}

	/// <summary>
	/// Whether an advance holds for its squad: from more than
	/// <see cref="UnitPlanTraits.PaceAheadMeters"/> nearer the objective than the
	/// squad's centroid until it is within <see cref="UnitPlanTraits.PaceResumeMeters"/>
	/// again. A wait that lasts <see cref="UnitPlanTraits.PaceMaxWaitTicks"/> ends the
	/// pacing for the rest of the advance.
	/// </summary>
	private bool KeepsPace()
	{
		if (!HasSquad || SquadAlive < 2 || PaceGivenUp)
		{
			Waiting = false;
			return false;
		}

		if (Tick != _paceTick)
		{
			_paceTick = Tick;
			if (Waiting)
			{
				WaitedTicks++;
			}
		}

		if (WaitedTicks >= Traits.PaceMaxWaitTicks)
		{
			PaceGivenUp = true;
			Waiting = false;
			return false;
		}

		Vector3 objective = AdvancePoint;
		float ahead = Flat(SquadCentroid, objective) - Flat(Position, objective);
		Waiting = ahead > (Waiting ? Traits.PaceResumeMeters : Traits.PaceAheadMeters);
		if (!Waiting)
		{
			// The squad closed up: the next wait is a new one.
			WaitedTicks = 0;
		}

		return Waiting;
	}

	/// <summary>The order's own destination, standing to fight exactly when today's code does.</summary>
	private TaskStatus FollowOrder()
	{
		Intent.Move = UnitMove.Order;
		Intent.StopToFight = UnitBrain.HoldsWhileEngaging(Order.Kind);
		return TaskStatus.Continue;
	}

	private TaskStatus Go(Vector3 point, bool stopToFight)
	{
		Intent.Move = UnitMove.Point;
		Intent.Point = point;
		Intent.StopToFight = stopToFight;
		return TaskStatus.Continue;
	}

	private TaskStatus Hold()
	{
		Intent.Move = UnitMove.Hold;
		Intent.StopToFight = true;
		return TaskStatus.Continue;
	}

	private static float Flat(Vector3 a, Vector3 b)
	{
		Vector3 d = a - b;
		d.Y = 0f;
		return d.Length();
	}
}

/// <summary>
/// FluidHTN's builder with the unit domain's vocabulary (docs/HTN_BOTS.md §3.3), as
/// <see cref="GroundDomainBuilder"/> is for the ground bot's. Every lambda is made
/// once, when the domain is built.
/// </summary>
public sealed class UnitDomainBuilder : BaseDomainBuilder<UnitDomainBuilder, UnitContext>
{
	private readonly bool _executingConditions;

	/// <param name="executingConditions">
	/// False leaves every <c>While</c> out, which is how a test shows what an operator
	/// without one does after its premise goes (§3.4, P3).
	/// </param>
	public UnitDomainBuilder(string name, IFactory factory, bool executingConditions = true)
		: base(name, factory) => _executingConditions = executingConditions;

	public UnitDomainBuilder If<TValue>(UnitFact fact, TValue value) where TValue : struct, Enum
	{
		byte wanted = Convert.ToByte(value);
		return Condition($"{fact}={value}", c => c.Get(fact) == wanted);
	}

	public UnitDomainBuilder IfNot<TValue>(UnitFact fact, TValue value) where TValue : struct, Enum
	{
		byte unwanted = Convert.ToByte(value);
		return Condition($"{fact}!={value}", c => c.Get(fact) != unwanted);
	}

	public UnitDomainBuilder IfAny<TValue>(UnitFact fact, TValue first, TValue second) where TValue : struct, Enum
	{
		byte a = Convert.ToByte(first);
		byte b = Convert.ToByte(second);
		return Condition($"{fact}∈{{{first},{second}}}", c => c.Get(fact) == a || c.Get(fact) == b);
	}

	public UnitDomainBuilder If(UnitFact fact, bool value = true)
	{
		byte wanted = value ? (byte)1 : (byte)0;
		return Condition($"{fact}={value}", c => c.Get(fact) == wanted);
	}

	/// <summary>Executing condition: the task fails, and the planner replans, the tick it stops holding (§3.4, P3).</summary>
	public UnitDomainBuilder While<TValue>(UnitFact fact, TValue value) where TValue : struct, Enum
	{
		if (!_executingConditions)
		{
			return this;
		}

		byte wanted = Convert.ToByte(value);
		return ExecutingCondition($"while {fact}={value}", c => c.Get(fact) == wanted);
	}

	public UnitDomainBuilder WhileNot<TValue>(UnitFact fact, TValue value) where TValue : struct, Enum
	{
		if (!_executingConditions)
		{
			return this;
		}

		byte unwanted = Convert.ToByte(value);
		return ExecutingCondition($"while {fact}!={value}", c => c.Get(fact) != unwanted);
	}

	public UnitDomainBuilder WhileAny<TValue>(UnitFact fact, TValue first, TValue second) where TValue : struct, Enum
	{
		if (!_executingConditions)
		{
			return this;
		}

		byte a = Convert.ToByte(first);
		byte b = Convert.ToByte(second);
		return ExecutingCondition($"while {fact}∈{{{first},{second}}}", c => c.Get(fact) == a || c.Get(fact) == b);
	}

	public UnitDomainBuilder While(UnitFact fact, bool value = true)
	{
		if (!_executingConditions)
		{
			return this;
		}

		byte wanted = value ? (byte)1 : (byte)0;
		return ExecutingCondition($"while {fact}={value}", c => c.Get(fact) == wanted);
	}

	/// <summary>
	/// A primitive task whose operator asks the unit for <paramref name="goal"/>. Set
	/// directly rather than through <c>Do</c>, which takes no abort callback: an
	/// operator ended by a failed executing condition must clear its intent too (§3.7).
	/// </summary>
	public UnitDomainBuilder Act(UnitGoal goal)
	{
		Action(goal.ToString());
		if (Pointer is IPrimitiveTask task)
		{
			task.SetOperator(new FuncOperator<UnitContext>(
				c => c.Perform(goal),
				start: c => c.Begin(goal),
				funcStop: c => c.End(),
				funcAborted: c => c.End()));
		}

		return this;
	}
}

/// <summary>
/// docs/HTN_BOTS.md §5.2 as FluidHTN code: the RTS unit's domain, built once and
/// shared by every unit (§3.4, P6). The standing order is the top-level task — the
/// planner chooses how to carry it out and never whether — and the one thing a unit
/// does on its own, retreating, it does only for a computer strategist's order (D1).
///
/// Selectors are in priority order; a higher method pre-empts a running plan when a
/// fact changes (P2) and a lower one never does (P3), so every operator has an
/// executing condition for its premise, and every one for its order: a new order is
/// usually a lower branch, which could not replace the old one otherwise.
///
/// Resupply is not here: without healing (D2, H5) a unit has nothing to resupply.
/// </summary>
public static class UnitDomain
{
	public static Domain<UnitContext> Build(IFactory factory, bool executingConditions = true) =>
		new UnitDomainBuilder("unit", factory, executingConditions)
			.Select("retreat")
				.Sequence("fall back")
					.If(UnitFact.Stance, UnitStance.Autonomous)
					.If(UnitFact.Health, HealthBand.Critical)
					.If(UnitFact.Odds, OddsBand.Outnumbered)
					.IfNot(UnitFact.Order, OrderKind.Move)
					.IfNot(UnitFact.Order, OrderKind.Build)
					.Act(UnitGoal.Retreat).While(UnitFact.Odds, OddsBand.Outnumbered)
						.While(UnitFact.Stance, UnitStance.Autonomous)
						.WhileNot(UnitFact.Order, OrderKind.Move).WhileNot(UnitFact.Order, OrderKind.Build).End()
				.End()
			.End()
			.Select("build")
				.If(UnitFact.Order, OrderKind.Build)
				.Sequence("take cover")
					.If(UnitFact.UnderFire)
					.If(UnitFact.FriendNear)
					.Act(UnitGoal.TakeCover).While(UnitFact.UnderFire).While(UnitFact.FriendNear)
						.While(UnitFact.Order, OrderKind.Build).End()
				.End()
				.Sequence("work")
					.Act(UnitGoal.Work).While(UnitFact.Order, OrderKind.Build).End()
				.End()
			.End()
			.Select("attack")
				.If(UnitFact.Order, OrderKind.Attack)
				.Sequence("wait for squad")
					.If(UnitFact.SquadPhase, SquadPhase.Gathering)
					.Act(UnitGoal.WaitForSquad).While(UnitFact.SquadPhase, SquadPhase.Gathering)
						.While(UnitFact.Order, OrderKind.Attack).End()
				.End()
				.Sequence("engage focus")
					.If(UnitFact.Contact, UnitContact.Own)
					.Act(UnitGoal.EngageFocus).While(UnitFact.Contact, UnitContact.Own)
						.While(UnitFact.Order, OrderKind.Attack).End()
				.End()
				.Sequence("support")
					.If(UnitFact.FriendEngaged)
					.Act(UnitGoal.Support).While(UnitFact.FriendEngaged).WhileNot(UnitFact.Contact, UnitContact.Own)
						.While(UnitFact.Order, OrderKind.Attack).End()
				.End()
				.Sequence("advance")
					.Act(UnitGoal.Advance).While(UnitFact.Order, OrderKind.Attack).End()
				.End()
			.End()
			.Select("defend zone")
				.IfAny(UnitFact.Order, OrderKind.Defend, OrderKind.None)
				.Sequence("engage in leash")
					.If(UnitFact.Contact, UnitContact.Own)
					.If(UnitFact.InLeash)
					.Act(UnitGoal.EngageInLeash).While(UnitFact.Contact, UnitContact.Own).While(UnitFact.InLeash)
						.WhileAny(UnitFact.Order, OrderKind.Defend, OrderKind.None).End()
				.End()
				// Defend only: a unit told to stop holds where it is, as it always has.
				.Sequence("answer a call")
					.If(UnitFact.Order, OrderKind.Defend)
					.If(UnitFact.FriendEngaged)
					.If(UnitFact.InLeash)
					.IfNot(UnitFact.Contact, UnitContact.Own)
					.Act(UnitGoal.AnswerCall).While(UnitFact.FriendEngaged).While(UnitFact.InLeash)
						.WhileNot(UnitFact.Contact, UnitContact.Own).While(UnitFact.Order, OrderKind.Defend).End()
				.End()
				.Sequence("hold post")
					.Act(UnitGoal.HoldPost).WhileAny(UnitFact.Order, OrderKind.Defend, OrderKind.None).End()
				.End()
			.End()
			.Select("recon")
				.If(UnitFact.Order, OrderKind.Patrol)
				.Sequence("patrol")
					.Act(UnitGoal.Patrol).While(UnitFact.Order, OrderKind.Patrol).End()
				.End()
			.End()
			.Select("obey")
				.If(UnitFact.Order, OrderKind.Move)
				.Sequence("obey")
					.Act(UnitGoal.Obey).While(UnitFact.Order, OrderKind.Move).End()
				.End()
			.End()
			.Build();
}

/// <summary>
/// What every unit plans with: one domain, one planner and one pooled factory,
/// shared (§3.4, P6 — safe because the server tick is single-threaded), and a
/// context per unit made from them.
/// </summary>
public sealed class UnitPlanning
{
	public UnitPlanning(in UnitPlanTraits traits, IFactory factory = null)
	{
		Factory = factory ?? new PooledHtnFactory();
		Traits = traits;
		Domain = UnitDomain.Build(Factory);
	}

	public IFactory Factory { get; }

	public UnitPlanTraits Traits { get; }

	public Domain<UnitContext> Domain { get; }

	public Planner<UnitContext> Planner { get; } = new();

	public UnitContext CreateContext(in UnitTraits unitTraits)
	{
		var context = new UnitContext(Factory, Traits, unitTraits);
		context.Init();
		return context;
	}

	/// <summary>One tick: replans if a fact changed or the plan ended, then runs the current operator.</summary>
	public void Tick(UnitContext context) => Planner.Tick(Domain, context);

	/// <summary>Drops the plan, stopping its operator: for a new order, or a unit that has died.</summary>
	public void Reset(UnitContext context) => Planner.Reset(context);
}
