using System;
using System.Collections.Generic;
using FluidHTN;
using FluidHTN.Contexts;
using FluidHTN.Debug;
using FluidHTN.Factory;
using FluidHTN.Operators;
using FluidHTN.PrimitiveTasks;
using Gdpyr.Sim.AiDebug;
using Godot;

namespace Gdpyr.Sim.Htn;

/// <summary>
/// One board squad's drill facts (docs/TACTICS.md §5.5): one byte each, as FluidHTN
/// stores its world state. <see cref="SquadDrill"/> writes them on the squad's decision;
/// only a change makes its planner look again (docs/HTN_BOTS.md §3.7).
/// </summary>
public enum DrillFact : byte
{
	/// <summary><see cref="DrillContact"/>: held <see cref="DrillTraits.QuietTicks"/> after the last threat point.</summary>
	Contact,

	/// <summary><see cref="DrillBase"/>: whether the base of fire has been sent to its positions, and is firing.</summary>
	Base,

	/// <summary>The base was sent <see cref="DrillTraits.BaseMaxTicks"/> ago: the squad bounds whether it fires or not.</summary>
	BaseLate,

	/// <summary><see cref="DrillRange"/>: the leading element to the threat point, banded with hysteresis.</summary>
	Range,

	/// <summary>Every member of the moving element is at its bound spot.</summary>
	Arrived,

	/// <summary><see cref="DrillObjective"/>: a control point to take, and whether an element is on it.</summary>
	Objective,

	/// <summary>A heavy gunner among the elements: the base of fire, and the riflemen the assault.</summary>
	HasGunner,

	/// <summary>A member of the assault has a grenade ready and the threat point within a throw.</summary>
	Grenade,

	/// <summary>
	/// The drill was in contact for <see cref="DrillTraits.DrillMaxTicks"/>: it approaches
	/// without a threat point for <see cref="DrillTraits.DrillRetryTicks"/> (P4).
	/// </summary>
	GivenUp,

	/// <summary><see cref="DrillElement"/>: which half is bounding. Written by the bound itself.</summary>
	Moving,

	Count,
}

/// <summary>What a drilling squad is in contact with.</summary>
public enum DrillContact : byte
{
	None = 0,

	/// <summary>A threat point (docs/TACTICS.md §4.2): a place an enemy is, was, or fired from.</summary>
	Known = 1,

	/// <summary>A threat point, and a member was fired upon in its window (§4.1).</summary>
	FiredUpon = 2,
}

/// <summary>Where the squad's base of fire is in getting to work.</summary>
public enum DrillBase : byte
{
	/// <summary>Not sent to its fire positions in this contact.</summary>
	None = 0,

	/// <summary>Sent, and not firing.</summary>
	Moving = 1,

	/// <summary>A member of it fired within <see cref="DrillTraits.BaseFiringTicks"/>.</summary>
	Firing = 2,
}

/// <summary>How far the leading element is from the threat point.</summary>
public enum DrillRange : byte
{
	None = 0,

	/// <summary>Further than <see cref="DrillTraits.FarMeters"/>.</summary>
	Far = 1,

	/// <summary>Between <see cref="DrillTraits.AssaultMeters"/> and <see cref="DrillTraits.FarMeters"/>: bounding range.</summary>
	Bound = 2,

	/// <summary>Nearer than <see cref="DrillTraits.AssaultMeters"/>.</summary>
	Assault = 3,
}

/// <summary>Whether the squad's order is a control point to take, and whether it is on it.</summary>
public enum DrillObjective : byte
{
	/// <summary>An attack on anything but a control point.</summary>
	None = 0,

	Approach = 1,

	/// <summary>An element stands on the point.</summary>
	On = 2,
}

/// <summary>A half of a squad's riflemen (docs/TACTICS.md §5.5).</summary>
public enum DrillElement : byte
{
	None = 0,
	A = 1,
	B = 2,
}

/// <summary>
/// What the drill's running task asks of the squad: the operator's name, and the phase
/// the <c>squad_drill</c> event reports (docs/AGENT_API.md §8). Protocol constants:
/// append, never renumber.
/// </summary>
public enum DrillPhase : byte
{
	None = 0,

	/// <summary>No contact: the squad takes the point, or carries its attack out, keeping pace (existing).</summary>
	Approach = 1,

	/// <summary>On the point: hold it from cover, or from posts on it (existing).</summary>
	HoldPoint = 2,

	/// <summary>Fired upon with nothing firing back: every member to cover from the threat point, the base to its fire positions.</summary>
	GoToGround = 3,

	/// <summary>The base to its fire positions; the rest hold in cover.</summary>
	SetBase = 4,

	/// <summary>One element to its next spots towards the point while the other holds and fires; then the other.</summary>
	Bound = 5,

	/// <summary>The assault in cover near the threat point, throwing grenades at it.</summary>
	Grenades = 6,

	/// <summary>The assault onto the point while the base lifts its fire and shifts it.</summary>
	Rush = 7,
}

/// <summary>A member's place in its squad's drill, by doctrine (<see cref="DrillRoles.Assign"/>).</summary>
public enum DrillRole : byte
{
	/// <summary>Not an element: micro-autonomy off, no doctrine, a vehicle, or dead.</summary>
	None = 0,

	/// <summary>A heavy gunner: the base of fire.</summary>
	Base = 1,

	/// <summary>A rifleman: the assault, bounding in halves.</summary>
	Assault = 2,

	/// <summary>A marksman: no element; it peeks while one moves.</summary>
	Overwatch = 3,
}

/// <summary>What one member is told to do now, from its role and the drill's phase.</summary>
public enum DrillTask : byte
{
	/// <summary>Nothing from the drill: the order's own methods.</summary>
	None = 0,

	/// <summary>To cover from the threat point, firing on the way; in it, hold.</summary>
	Ground = 1,

	/// <summary>To a low wall 20–40 m from the threat point it can fire over at it; in it, fire at it.</summary>
	FirePosition = 2,

	/// <summary>To its next spot towards the point, firing on the way, not stopping.</summary>
	Bound = 3,

	/// <summary>In its spot, holding; firing at the threat point while an element moves.</summary>
	Hold = 4,

	/// <summary>Onto the point, or the threat point, without stopping.</summary>
	Rush = 5,

	/// <summary>A marksman: its own peeks, timed to the bounds (§5.3).</summary>
	Overwatch = 6,
}

/// <summary>
/// The drill's numbers (docs/TACTICS.md §5.5; docs/HTN_BOTS.md §9: every threshold is a knob).
/// </summary>
public readonly struct DrillTraits
{
	/// <summary>How often a squad's drill decides; staggered by squad slot.</summary>
	public readonly int IntervalTicks;

	/// <summary>How long a squad stays in contact after its last threat point.</summary>
	public readonly int QuietTicks;

	/// <summary>How long getting into cover may last before the base is set regardless.</summary>
	public readonly int GroundMaxTicks;

	/// <summary>How long after the base is sent the squad bounds whether it is firing or not.</summary>
	public readonly int BaseMaxTicks;

	/// <summary>How long a base member's last round keeps the base firing.</summary>
	public readonly int BaseFiringTicks;

	/// <summary>How long one bound may last before the other element goes.</summary>
	public readonly int BoundMaxTicks;

	/// <summary>How long the assault waits after a grenade before it rushes…</summary>
	public readonly int GrenadeWaitTicks;

	/// <summary>…and how long it waits for one at most.</summary>
	public readonly int GrenadesMaxTicks;

	/// <summary>How long a drill may be in contact before it gives up and approaches (P4)…</summary>
	public readonly int DrillMaxTicks;

	/// <summary>…and how long it approaches without a threat point before it drills again.</summary>
	public readonly int DrillRetryTicks;

	/// <summary>How long a member's fired-from point stays on the squad's list.</summary>
	public readonly int FiredFromTicks;

	/// <summary>How far from the squad's centroid, or the line to its objective, an entry is the squad's.</summary>
	public readonly float ThreatMeters;

	/// <summary>How near an entry a member's fired-from point is, to count that member as fired upon from it.</summary>
	public readonly float FiredNearMeters;

	/// <summary>Range bands: assault inside this…</summary>
	public readonly float AssaultMeters;

	/// <summary>…far outside this…</summary>
	public readonly float FarMeters;

	/// <summary>…each held this far past its edge.</summary>
	public readonly float RangeHysteresisMeters;

	/// <summary>A fire position's band from the threat point.</summary>
	public readonly float FireMinMeters;

	/// <inheritdoc cref="FireMinMeters"/>
	public readonly float FireMaxMeters;

	/// <summary>How far a base member goes for a fire position.</summary>
	public readonly float FireSearchMeters;

	/// <summary>The bearing off the assault's axis, at the threat point, a fire position is preferred at.</summary>
	public readonly float FireOffAxisRadians;

	/// <summary>How far a bounding member goes for its next spot.</summary>
	public readonly float BoundSearchMeters;

	/// <summary>How much nearer the objective a bound spot must be than where the member stands.</summary>
	public readonly float BoundProgressMeters;

	/// <summary>How far off the line to the objective a bound spot may be.</summary>
	public readonly float BoundOffLineMeters;

	/// <summary>No bound spot is nearer the threat point than this.</summary>
	public readonly float BoundStandoffMeters;

	/// <summary>With no spot, how far on along the axis a bound goes…</summary>
	public readonly float OpenBoundMeters;

	/// <summary>…and how far apart, across it, the members' points are.</summary>
	public readonly float OpenRankMeters;

	/// <summary>How near its bound spot a member is to have arrived.</summary>
	public readonly float ArriveMeters;

	/// <summary>How near the threat point the assault is when the base lifts its fire…</summary>
	public readonly float LiftMeters;

	/// <summary>…and how far beyond it the base looks for the next entry to shift to.</summary>
	public readonly float ShiftMeters;

	/// <summary>Riflemen with a gunner bound in two halves from this many; fewer bound together.</summary>
	public readonly int HalvesFrom;

	public DrillTraits(int intervalTicks, int quietTicks, int groundMaxTicks, int baseMaxTicks, int baseFiringTicks,
		int boundMaxTicks, int grenadeWaitTicks, int grenadesMaxTicks, int drillMaxTicks, int drillRetryTicks,
		int firedFromTicks, float threatMeters, float firedNearMeters, float assaultMeters, float farMeters,
		float rangeHysteresisMeters, float fireMinMeters, float fireMaxMeters, float fireSearchMeters,
		float fireOffAxisRadians, float boundSearchMeters, float boundProgressMeters, float boundOffLineMeters,
		float boundStandoffMeters, float openBoundMeters, float openRankMeters, float arriveMeters, float liftMeters,
		float shiftMeters, int halvesFrom)
	{
		IntervalTicks = Math.Max(intervalTicks, 1);
		QuietTicks = Math.Max(quietTicks, 0);
		GroundMaxTicks = Math.Max(groundMaxTicks, 1);
		BaseMaxTicks = Math.Max(baseMaxTicks, 0);
		BaseFiringTicks = Math.Max(baseFiringTicks, 1);
		BoundMaxTicks = Math.Max(boundMaxTicks, 1);
		GrenadeWaitTicks = Math.Max(grenadeWaitTicks, 0);
		GrenadesMaxTicks = Math.Max(grenadesMaxTicks, 1);
		DrillMaxTicks = Math.Max(drillMaxTicks, 1);
		DrillRetryTicks = Math.Max(drillRetryTicks, 0);
		FiredFromTicks = Math.Max(firedFromTicks, 0);
		ThreatMeters = MathF.Max(threatMeters, 0f);
		FiredNearMeters = MathF.Max(firedNearMeters, 0f);
		AssaultMeters = MathF.Max(assaultMeters, 0f);
		FarMeters = MathF.Max(farMeters, AssaultMeters);
		RangeHysteresisMeters = MathF.Max(rangeHysteresisMeters, 0f);
		FireMinMeters = MathF.Max(fireMinMeters, 0f);
		FireMaxMeters = MathF.Max(fireMaxMeters, FireMinMeters);
		FireSearchMeters = MathF.Max(fireSearchMeters, 0f);
		FireOffAxisRadians = Math.Clamp(fireOffAxisRadians, 0f, MathF.PI);
		BoundSearchMeters = MathF.Max(boundSearchMeters, 0f);
		BoundProgressMeters = MathF.Max(boundProgressMeters, 0f);
		BoundOffLineMeters = MathF.Max(boundOffLineMeters, 0f);
		BoundStandoffMeters = MathF.Max(boundStandoffMeters, 0f);
		OpenBoundMeters = MathF.Max(openBoundMeters, 0f);
		OpenRankMeters = MathF.Max(openRankMeters, 0f);
		ArriveMeters = MathF.Max(arriveMeters, 0f);
		LiftMeters = MathF.Max(liftMeters, 0f);
		ShiftMeters = MathF.Max(shiftMeters, 0f);
		HalvesFrom = Math.Max(halvesFrom, 2);
	}

	/// <summary>
	/// §5.5's numbers. A decision every 10 ticks; contact held 5 s after the last threat
	/// point; 4 s to get into cover, then the base is set; the squad bounds once the base
	/// fires, or 6 s after it was sent; a bound lasts 8 s at most; the assault rushes 2 s
	/// after a grenade, or after 5 s without one; 60 s in contact, then 20 s of approach
	/// (P4). Fired-from points are kept 8 s, as the side keeps a ghost. Entries within
	/// 60 m; a member counts as fired upon from one within 5 m of where its round came
	/// from. Assault inside 20 m, far beyond 45 m, 2 m of hysteresis. Fire positions 20–40 m
	/// from the threat point, within 15 m, 30° off the assault's axis by preference. Bound
	/// spots within 12 m, 5 m nearer the objective, within 8 m of the line to it and no
	/// nearer the threat point than 12 m; with none, 8 m on along the axis, 3 m apart. A
	/// member within 1.5 m of its spot has arrived. The base lifts its fire once the
	/// assault is within 8 m of the threat point, and shifts to an entry within 25 m beyond
	/// it. With a gunner, four riflemen or more bound in halves.
	/// </summary>
	public static DrillTraits Default => new(
		intervalTicks: SimConfig.UnitTargetRefreshTicks,
		quietTicks: SimConfig.TickRate * 5,
		groundMaxTicks: SimConfig.TickRate * 4,
		baseMaxTicks: SimConfig.TickRate * 6,
		baseFiringTicks: SimConfig.TickRate * 2,
		boundMaxTicks: SimConfig.TickRate * 8,
		grenadeWaitTicks: SimConfig.TickRate * 2,
		grenadesMaxTicks: SimConfig.TickRate * 5,
		drillMaxTicks: SimConfig.TickRate * 60,
		drillRetryTicks: SimConfig.TickRate * 20,
		firedFromTicks: SimConfig.GhostLifetimeTicks,
		threatMeters: 60f,
		firedNearMeters: 5f,
		assaultMeters: 20f,
		farMeters: 45f,
		rangeHysteresisMeters: 2f,
		fireMinMeters: 20f,
		fireMaxMeters: 40f,
		fireSearchMeters: 15f,
		fireOffAxisRadians: MathF.PI / 6f,
		boundSearchMeters: 12f,
		boundProgressMeters: 5f,
		boundOffLineMeters: 8f,
		boundStandoffMeters: 12f,
		openBoundMeters: 8f,
		openRankMeters: 3f,
		arriveMeters: 1.5f,
		liftMeters: 8f,
		shiftMeters: 25f,
		halvesFrom: 4);
}

/// <summary>What the drill's running task asks of the squad (docs/HTN_BOTS.md §4.2 rule 1).</summary>
public struct DrillIntent
{
	public DrillPhase Phase;

	/// <summary>The element bounding, while <see cref="Phase"/> is <see cref="DrillPhase.Bound"/>.</summary>
	public DrillElement Moving;

	/// <summary>Which bound this is: a new number is a new set of spots for the element moving.</summary>
	public int BoundSerial;
}

/// <summary>
/// One decision's worth of what a drilling squad knows, before it is banded into facts.
/// <see cref="SquadDrill"/> fills it from its members and the squad's list (§4.2).
/// </summary>
public struct DrillSense
{
	public bool HasThreat;

	/// <summary>A member of the drill was fired upon in its window.</summary>
	public bool FiredUpon;

	/// <summary>From the leading element to the threat point, flat.</summary>
	public float LeadMeters;

	/// <summary>The last tick a member of the base fired a round.</summary>
	public uint LastBaseFireTick;

	public bool Arrived;

	public DrillObjective Objective;

	public bool HasGunner;

	/// <summary>Riflemen in element B: there is another half to bound.</summary>
	public bool HasElementB;

	public bool Grenade;

	/// <summary>The last tick a member of the assault threw a grenade.</summary>
	public uint LastAssaultThrowTick;
}

/// <summary>
/// One board squad's drill context: its facts, the clocks its operators keep, and the
/// intent the running operator last wrote. <see cref="SquadDrill"/> writes the knowledge
/// on the squad's decision and reads <see cref="Intent"/>; operators are functions of
/// this context and nothing else, so what a squad does in a situation is a question for
/// <c>dotnet test</c>.
/// </summary>
public sealed class DrillContext : BaseContext
{
	private readonly byte[] _facts = new byte[(int)DrillFact.Count];

	private bool _baseSent;
	private uint _baseSentTick;
	private uint _lastThreatTick;
	private bool _everThreat;
	private int _contactTicks;
	private uint _contactTick = uint.MaxValue;
	private uint _givenUpUntil;
	private uint _boundStartTick;
	private uint _boundTick = uint.MaxValue;
	private int _boundSerial;
	private DrillElement _moving;

	public DrillContext(IFactory factory, in DrillTraits traits)
	{
		Factory = factory;
		Traits = traits;
	}

	public override IFactory Factory { get; protected set; }
	public override IPlannerState PlannerState { get; protected set; } = new DefaultPlannerState();
	public override List<string> MTRDebug { get; set; }
	public override List<string> LastMTRDebug { get; set; }
	public override bool DebugMTR => false;
	public override Queue<IBaseDecompositionLogEntry> DecompositionLog { get; set; }
	public override bool LogDecomposition => false;
	public override byte[] WorldState => _facts;

	public DrillTraits Traits { get; }

	/// <summary>What the running operator asked for. <see cref="DrillPhase.None"/> between tasks.</summary>
	public DrillIntent Intent;

	public uint Tick;

	/// <summary>Riflemen in element B: a bound alternates between the halves.</summary>
	public bool HasElementB;

	/// <summary>The last tick a member of the assault threw a grenade, for <see cref="DrillPhase.Grenades"/>.</summary>
	public uint LastAssaultThrowTick;

	/// <summary>The tick the running operator started on.</summary>
	public uint GoalStartTick;

	/// <summary>Ticks this drill has been in contact since it last gave up, against <see cref="DrillTraits.DrillMaxTicks"/>.</summary>
	public int ContactTicks => _contactTicks;

	/// <summary>Times it gave up and approached. For the tests and the debugger.</summary>
	public int GiveUps;

	/// <summary>Bounds started. For the tests.</summary>
	public int Bounds;

	/// <summary>What its planner has done lately, for the spectator's debugger. Null for a context made outside <see cref="DrillPlanning"/>.</summary>
	public HtnHistory History { get; internal set; }

	public byte Get(DrillFact fact) => GetState((int)fact);

	public bool Is(DrillFact fact) => GetState((int)fact) != 0;

	/// <summary>What the drill writes. Marks the context dirty only when the value changes.</summary>
	public void Sense(DrillFact fact, byte value) => SetState((int)fact, value, true, EffectType.Permanent);

	public void Sense(DrillFact fact, bool value) => Sense(fact, value ? (byte)1 : (byte)0);

	/// <summary>The base fires, or was sent long enough ago that the squad goes regardless.</summary>
	public bool BaseReady => Get(DrillFact.Base) == (byte)DrillBase.Firing || Is(DrillFact.BaseLate);

	/// <summary>
	/// The rifle half that is the base without a gunner (§5.5): the first until a half has
	/// bounded, then the half not moving — the one holding behind the last bound, so a bound
	/// that stops for want of its fire sends that half to its fire positions and not the half
	/// on its way, which would bound again the moment it fired. With one half, that half.
	/// </summary>
	public DrillElement RiflesBase => !HasElementB || _moving == DrillElement.None ? DrillElement.A : Other(_moving);

	/// <summary>
	/// Bands one decision into facts (§5.5): contact, held <see cref="DrillTraits.QuietTicks"/>
	/// after the last threat point; the base; the range, with its hysteresis; the give-up
	/// clock, counted once a decision tick while in contact. Call with <see cref="Tick"/> set.
	/// </summary>
	public void Encode(in DrillSense sense)
	{
		HasElementB = sense.HasElementB;
		LastAssaultThrowTick = sense.LastAssaultThrowTick;

		if (sense.HasThreat)
		{
			_lastThreatTick = Tick;
			_everThreat = true;
		}

		bool inContact = _everThreat && Tick >= _lastThreatTick && Tick - _lastThreatTick <= (uint)Traits.QuietTicks;
		DrillContact contact = !inContact ? DrillContact.None
			: sense.HasThreat && sense.FiredUpon ? DrillContact.FiredUpon
			: DrillContact.Known;
		Sense(DrillFact.Contact, (byte)contact);

		// Out of contact, the base stands down: the next contact sets it again.
		if (contact == DrillContact.None)
		{
			_baseSent = false;
		}

		bool firing = _baseSent && sense.LastBaseFireTick != 0 && sense.LastBaseFireTick >= _baseSentTick
			&& Tick >= sense.LastBaseFireTick && Tick - sense.LastBaseFireTick <= (uint)Traits.BaseFiringTicks;
		Sense(DrillFact.Base, (byte)(!_baseSent ? DrillBase.None : firing ? DrillBase.Firing : DrillBase.Moving));
		Sense(DrillFact.BaseLate, _baseSent && Tick - _baseSentTick >= (uint)Traits.BaseMaxTicks);

		Sense(DrillFact.Range, (byte)(sense.HasThreat ? BandRange(sense.LeadMeters, (DrillRange)Get(DrillFact.Range))
			: contact == DrillContact.None ? DrillRange.None : (DrillRange)Get(DrillFact.Range)));
		Sense(DrillFact.Arrived, sense.Arrived);
		Sense(DrillFact.Objective, (byte)sense.Objective);
		Sense(DrillFact.HasGunner, sense.HasGunner);
		Sense(DrillFact.Grenade, sense.Grenade && sense.HasThreat);

		CountContact(contact != DrillContact.None);
	}

	/// <summary>The range band, each edge held <see cref="DrillTraits.RangeHysteresisMeters"/> past itself.</summary>
	private DrillRange BandRange(float meters, DrillRange previous)
	{
		float slack = Traits.RangeHysteresisMeters;
		float assault = Traits.AssaultMeters + (previous == DrillRange.Assault ? slack : 0f);
		float far = Traits.FarMeters - (previous == DrillRange.Far ? slack : 0f);
		return meters < assault ? DrillRange.Assault : meters > far ? DrillRange.Far : DrillRange.Bound;
	}

	/// <summary>
	/// The give-up clock (P4): a decision in contact counts its interval; one that has lasted
	/// <see cref="DrillTraits.DrillMaxTicks"/> gives the drill up for
	/// <see cref="DrillTraits.DrillRetryTicks"/>, in which it approaches. Out of contact the
	/// clock starts again.
	/// </summary>
	private void CountContact(bool inContact)
	{
		if (Is(DrillFact.GivenUp) && Tick >= _givenUpUntil)
		{
			Sense(DrillFact.GivenUp, false);
			_baseSent = false;
			Sense(DrillFact.Base, (byte)DrillBase.None);
			Sense(DrillFact.BaseLate, false);
		}

		if (!inContact)
		{
			_contactTicks = 0;
			_contactTick = uint.MaxValue;
			return;
		}

		if (Is(DrillFact.GivenUp))
		{
			return;
		}

		if (_contactTick != uint.MaxValue && Tick > _contactTick)
		{
			_contactTicks += (int)(Tick - _contactTick);
		}

		_contactTick = Tick;
		if (_contactTicks >= Traits.DrillMaxTicks)
		{
			GiveUps++;
			_contactTicks = 0;
			_contactTick = uint.MaxValue;
			_givenUpUntil = Tick + (uint)Traits.DrillRetryTicks;
			Sense(DrillFact.GivenUp, true);
		}
	}

	/// <summary>Back to a squad with no drill: for a slot that stops drilling, or a new order. The caller resets the planner.</summary>
	public void Clear()
	{
		for (int i = 0; i < (int)DrillFact.Count; i++)
		{
			Sense((DrillFact)i, 0);
		}

		IsDirty = false;
		Intent = default;
		_baseSent = false;
		_baseSentTick = 0;
		_lastThreatTick = 0;
		_everThreat = false;
		_contactTicks = 0;
		_contactTick = uint.MaxValue;
		_givenUpUntil = 0;
		_boundStartTick = 0;
		_boundTick = uint.MaxValue;
		_boundSerial = 0;
		_moving = DrillElement.None;
		HasElementB = false;
		LastAssaultThrowTick = 0;
		GoalStartTick = 0;
	}

	// ---- operators ---------------------------------------------------------

	public TaskStatus Begin(DrillPhase phase)
	{
		GoalStartTick = Tick;
		if (phase is DrillPhase.GoToGround or DrillPhase.SetBase && !_baseSent)
		{
			// The base goes to its fire positions from this decision (§5.2): its clock starts.
			_baseSent = true;
			_baseSentTick = Tick;
			Sense(DrillFact.Base, (byte)DrillBase.Moving);
		}
		else if (phase == DrillPhase.Bound)
		{
			StartBound(_moving == DrillElement.None ? FirstElement() : _moving);
		}

		return Perform(phase);
	}

	public void End() => Intent = default;

	/// <summary>
	/// One decision of an operator: writes the intent, and says whether it is done.
	/// Idempotent — FluidHTN can run an operator twice in one tick (#21, §3.2) — so a bound
	/// changes hands once a tick at most.
	/// </summary>
	public TaskStatus Perform(DrillPhase phase)
	{
		Intent = new DrillIntent { Phase = phase, Moving = DrillElement.None, BoundSerial = _boundSerial };
		switch (phase)
		{
			case DrillPhase.Approach:
			case DrillPhase.HoldPoint:
			case DrillPhase.SetBase:
			case DrillPhase.Rush:
				return TaskStatus.Continue;

			case DrillPhase.GoToGround:
				return Tick - GoalStartTick >= (uint)Traits.GroundMaxTicks ? TaskStatus.Success : TaskStatus.Continue;

			case DrillPhase.Bound:
				if (Tick != _boundTick && (Is(DrillFact.Arrived) || Tick - _boundStartTick >= (uint)Traits.BoundMaxTicks))
				{
					// The element moving is in its spots, or has been on its way long enough:
					// the other goes — or, with one half, the same again from where it is.
					StartBound(HasElementB ? Other(_moving) : _moving);
				}

				Intent.Moving = _moving;
				Intent.BoundSerial = _boundSerial;
				return TaskStatus.Continue;

			case DrillPhase.Grenades:
				bool thrown = LastAssaultThrowTick != 0 && LastAssaultThrowTick >= GoalStartTick
					&& Tick - LastAssaultThrowTick >= (uint)Traits.GrenadeWaitTicks;
				return thrown || Tick - GoalStartTick >= (uint)Traits.GrenadesMaxTicks
					? TaskStatus.Success
					: TaskStatus.Continue;

			default:
				return TaskStatus.Failure;
		}
	}

	/// <summary>
	/// The first element to bound: the riflemen's first half when a gunner is the base; else
	/// the second, the first being the base of fire (§5.5, "the halves leapfrog").
	/// </summary>
	private DrillElement FirstElement() => Is(DrillFact.HasGunner) || !HasElementB ? DrillElement.A : DrillElement.B;

	private static DrillElement Other(DrillElement element) => element == DrillElement.A ? DrillElement.B : DrillElement.A;

	private void StartBound(DrillElement element)
	{
		_moving = element;
		_boundStartTick = Tick;
		_boundTick = Tick;
		_boundSerial++;
		Bounds++;
		Sense(DrillFact.Moving, (byte)element);
		Sense(DrillFact.Arrived, false);
	}
}

/// <summary>FluidHTN's builder with the drill's vocabulary, as <see cref="CommandDomainBuilder"/> is for the commander's.</summary>
public sealed class DrillDomainBuilder : BaseDomainBuilder<DrillDomainBuilder, DrillContext>
{
	private readonly bool _executingConditions;

	/// <param name="executingConditions">False leaves every <c>While</c> out (§3.4, P3).</param>
	public DrillDomainBuilder(string name, IFactory factory, bool executingConditions = true)
		: base(name, factory) => _executingConditions = executingConditions;

	public DrillDomainBuilder If<TValue>(DrillFact fact, TValue value) where TValue : struct, Enum
	{
		byte wanted = Convert.ToByte(value);
		return Condition($"{fact}={value}", c => c.Get(fact) == wanted);
	}

	public DrillDomainBuilder IfNot<TValue>(DrillFact fact, TValue value) where TValue : struct, Enum
	{
		byte unwanted = Convert.ToByte(value);
		return Condition($"{fact}!={value}", c => c.Get(fact) != unwanted);
	}

	public DrillDomainBuilder If(DrillFact fact, bool value = true)
	{
		byte wanted = value ? (byte)1 : (byte)0;
		return Condition($"{fact}={value}", c => c.Get(fact) == wanted);
	}

	/// <summary>The base fires, or was sent long enough ago (<see cref="DrillContext.BaseReady"/>).</summary>
	public DrillDomainBuilder IfBaseReady() => Condition("Base=Firing or BaseLate", c => c.BaseReady);

	public DrillDomainBuilder While<TValue>(DrillFact fact, TValue value) where TValue : struct, Enum
	{
		if (!_executingConditions)
		{
			return this;
		}

		byte wanted = Convert.ToByte(value);
		return ExecutingCondition($"while {fact}={value}", c => c.Get(fact) == wanted);
	}

	public DrillDomainBuilder WhileNot<TValue>(DrillFact fact, TValue value) where TValue : struct, Enum
	{
		if (!_executingConditions)
		{
			return this;
		}

		byte unwanted = Convert.ToByte(value);
		return ExecutingCondition($"while {fact}!={value}", c => c.Get(fact) != unwanted);
	}

	public DrillDomainBuilder While(DrillFact fact, bool value = true)
	{
		if (!_executingConditions)
		{
			return this;
		}

		byte wanted = value ? (byte)1 : (byte)0;
		return ExecutingCondition($"while {fact}={value}", c => c.Get(fact) == wanted);
	}

	/// <summary>Executing condition: as <see cref="IfBaseReady"/>, every decision.</summary>
	public DrillDomainBuilder WhileBaseReady() =>
		_executingConditions ? ExecutingCondition("while Base=Firing or BaseLate", c => c.BaseReady) : this;

	/// <summary>A primitive task whose operator asks the squad for <paramref name="phase"/>, clearing its intent however it ends (§3.7).</summary>
	public DrillDomainBuilder Act(DrillPhase phase)
	{
		Action(phase.ToString());
		if (Pointer is IPrimitiveTask task)
		{
			task.SetOperator(new FuncOperator<DrillContext>(
				c => c.Perform(phase),
				start: c => c.Begin(phase),
				funcStop: c => c.End(),
				funcAborted: c => c.End()));
		}

		return this;
	}
}

/// <summary>
/// docs/TACTICS.md §5.5 as FluidHTN code: the squad's half of "take the point" and of an
/// attack — fire and manoeuvre. Built once and shared by every board squad (§3.4, P6).
///
/// Selectors are in priority order, and a higher one pre-empts a running plan when a fact
/// changes (P2): on the point the squad holds it; a drill that has given up approaches;
/// fired upon with no base yet, it goes to ground; in assault range with the base firing it
/// throws and rushes; in contact with the base firing it bounds; in contact it sets its
/// base; and with none it approaches. Every phase in contact is bounded (P4): 4 s to get
/// into cover, 6 s for the base, 8 s a bound, 5 s for a grenade, 60 s in all.
/// </summary>
public static class DrillDomain
{
	public static Domain<DrillContext> Build(IFactory factory, bool executingConditions = true) =>
		new DrillDomainBuilder("drill", factory, executingConditions)
			.Select("hold the point")
				.Sequence("hold the point")
					.If(DrillFact.Objective, DrillObjective.On)
					.Act(DrillPhase.HoldPoint).While(DrillFact.Objective, DrillObjective.On).End()
				.End()
			.End()
			.Select("given up")
				.Sequence("approach regardless")
					.If(DrillFact.GivenUp)
					.Act(DrillPhase.Approach).While(DrillFact.GivenUp).End()
				.End()
			.End()
			.Select("get into cover")
				.Sequence("go to ground")
					.If(DrillFact.Contact, DrillContact.FiredUpon)
					.If(DrillFact.Base, DrillBase.None)
					.Act(DrillPhase.GoToGround).WhileNot(DrillFact.Contact, DrillContact.None).End()
				.End()
			.End()
			.Select("assault")
				.Sequence("grenades, then rush")
					.If(DrillFact.Range, DrillRange.Assault)
					.IfBaseReady()
					.If(DrillFact.Grenade)
					.Act(DrillPhase.Grenades).WhileNot(DrillFact.Contact, DrillContact.None)
						.While(DrillFact.Range, DrillRange.Assault).End()
					.Act(DrillPhase.Rush).WhileNot(DrillFact.Contact, DrillContact.None)
						.While(DrillFact.Range, DrillRange.Assault).End()
				.End()
				.Sequence("rush")
					.If(DrillFact.Range, DrillRange.Assault)
					.IfBaseReady()
					.Act(DrillPhase.Rush).WhileNot(DrillFact.Contact, DrillContact.None)
						.While(DrillFact.Range, DrillRange.Assault).End()
				.End()
			.End()
			.Select("bound")
				.Sequence("bound")
					.IfNot(DrillFact.Contact, DrillContact.None)
					.IfBaseReady()
					// Out of contact the base stands down (Encode), so "base ready" ends a bound
					// whose contact has gone as well.
					.Act(DrillPhase.Bound).WhileBaseReady().End()
				.End()
			.End()
			.Select("establish base")
				.Sequence("set base")
					.IfNot(DrillFact.Contact, DrillContact.None)
					.Act(DrillPhase.SetBase).WhileNot(DrillFact.Contact, DrillContact.None).End()
				.End()
			.End()
			.Select("approach")
				.Sequence("approach")
					.Act(DrillPhase.Approach).End()
				.End()
			.End()
			.Build();
}

/// <summary>
/// What every squad drills with: one domain, one planner and one pooled factory, shared
/// (§3.4, P6 — the server tick is single-threaded), and a context per board slot.
/// </summary>
public sealed class DrillPlanning
{
	public DrillPlanning(in DrillTraits traits, IFactory factory = null)
	{
		Factory = factory ?? new PooledHtnFactory();
		Traits = traits;
		Domain = DrillDomain.Build(Factory);
		Map = HtnDomainMap.Build(HtnDomainKind.Drill, Domain);
	}

	public IFactory Factory { get; }

	public DrillTraits Traits { get; }

	public Domain<DrillContext> Domain { get; }

	/// <summary>The domain numbered for the debugger (docs/AI_DEBUG.md §3.1).</summary>
	public HtnDomainMap Map { get; }

	public Planner<DrillContext> Planner { get; } = new();

	/// <summary>
	/// A context, walked once through every branch and cleared again, so a slot that first
	/// bounds an hour into a round does not grow its queues then (§4.2 rule 3).
	/// </summary>
	public DrillContext CreateContext()
	{
		var context = new DrillContext(Factory, Traits);
		context.Init();

		// Hold, give up, go to ground, set the base, bound, grenades and rush, rush, approach.
		for (int branch = 0; branch < 8; branch++)
		{
			context.Clear();
			context.Tick = 1000;
			context.Sense(DrillFact.Objective, (byte)(branch == 0 ? DrillObjective.On : DrillObjective.Approach));
			context.Sense(DrillFact.GivenUp, branch == 1);
			context.Sense(DrillFact.Contact, (byte)(branch is 0 or 1 or 7 ? DrillContact.None
				: branch == 2 ? DrillContact.FiredUpon : DrillContact.Known));
			context.Sense(DrillFact.Base, (byte)(branch == 2 ? DrillBase.None
				: branch >= 4 ? DrillBase.Firing : DrillBase.Moving));
			context.Sense(DrillFact.Range, (byte)(branch is 5 or 6 ? DrillRange.Assault : DrillRange.Bound));
			context.Sense(DrillFact.Grenade, branch == 5);

			Planner.Tick(Domain, context);
			context.Tick += 10;
			Planner.Tick(Domain, context);

			// And a pre-emption of whatever is running.
			context.Sense(DrillFact.Objective, (byte)DrillObjective.On);
			Planner.Tick(Domain, context);
			Planner.Reset(context);
		}

		context.Clear();
		context.Bounds = 0;
		context.GiveUps = 0;
		context.History = new HtnHistory();
		context.History.Attach(context.PlannerState, Map, () => context.Tick);
		return context;
	}

	/// <summary>One decision: replans if a fact changed or the plan ended, then runs the current operator.</summary>
	public void Tick(DrillContext context) => Planner.Tick(Domain, context);

	/// <summary>Drops a squad's plan, stopping its operator: a new order, or a squad that stops drilling.</summary>
	public void Reset(DrillContext context)
	{
		Planner.Reset(context);
		context.History?.RecordReset();
	}
}
