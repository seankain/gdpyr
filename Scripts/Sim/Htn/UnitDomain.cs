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

	/// <summary>
	/// It wants resupply: entered when <see cref="Health"/> leaves ok, held until it is
	/// back to <see cref="UnitPlanTraits.ResupplyDoneFraction"/> (§5.2, D2).
	/// </summary>
	Wounded,

	/// <summary>
	/// A friendly supply source that can heal it is within
	/// <see cref="UnitPlanTraits.SupplyRangeMeters"/> of the edge of its reach, and the
	/// last trip to one was not given up on too recently (<see cref="Resupply"/>).
	/// </summary>
	Supply,

	/// <summary>
	/// Inside that source's reach: entered <see cref="UnitPlanTraits.SupplyMarginMeters"/>
	/// inside the edge, held to the edge, so a unit that stops on entering is healed.
	/// </summary>
	Supplied,

	/// <summary>
	/// <see cref="CoverState"/>: a spot behind cover from its own target — a low wall
	/// it can fire over — or, for a builder, from whatever is shooting at it, or for a
	/// heavy gunner with no target, from the nearest place it suppresses
	/// (docs/TACTICS.md §5.2), and whether it is standing in it (docs/COVER.md §6).
	/// </summary>
	Cover,

	/// <summary>
	/// Its order is an attack on a control point: an attack that names no contact,
	/// at a point on a resource node (<see cref="CapturePad"/>). The point is to be
	/// taken and held: the unit goes onto it, fights from cover there when there is
	/// any, and does not chase what it sees off it.
	/// </summary>
	Objective,

	/// <summary>
	/// It is on that point: entered <see cref="UnitPlanTraits.ObjectiveEnterMeters"/>
	/// inside the edge, held to the edge, which is where it stops counting.
	/// </summary>
	OnObjective,

	/// <summary>
	/// <see cref="Sim.MicroAutonomy"/>: whether the strategist lets this unit use the
	/// tactics of docs/TACTICS.md on its own. Written every tick from the unit, as the
	/// order is, so switching it is a replan on that tick; every method docs/TACTICS.md
	/// §5 adds requires it on, and every operator it adds holds only while it is.
	/// </summary>
	MicroAutonomy,

	/// <summary>
	/// <see cref="FireKind"/>: the worst round that has come near it in the last
	/// <see cref="Sim.FiredUpon.WindowTicks"/> (docs/TACTICS.md §4.1), every tick, from
	/// the unit's fire memory.
	/// </summary>
	FiredUpon,

	/// <summary>
	/// <see cref="CoverState"/> against where it was last fired upon from: a spot behind
	/// cover from there, on its way when its order goes somewhere, and whether it is
	/// standing in it (docs/TACTICS.md §5.1).
	/// </summary>
	FireCover,

	/// <summary>
	/// It has reacted to fire for <see cref="UnitPlanTraits.ReactMaxTicks"/> under an
	/// order that goes somewhere, and does not again until it is next hit, or given a
	/// new order: the order still arrives (docs/TACTICS.md §5.1).
	/// </summary>
	ReactSpent,

	/// <summary>
	/// A hidden enemy to throw a grenade at (docs/TACTICS.md §5.4): one the side saw, or
	/// a round came from, within the fuse, behind a box from the unit, an arc that comes
	/// to rest within a kill of it, nobody else's grenade on its way there, and one in
	/// the unit's hand. On the scan; off once the unit has tried.
	/// </summary>
	Grenade,

	/// <summary>
	/// <see cref="PeekState"/>: a marksman's peek pair (docs/TACTICS.md §5.3) and where it
	/// is in it — away from it, hidden in it and for how long, or out on a peek. The pair
	/// is the scan's; the rest is every tick's.
	/// </summary>
	Peek,

	/// <summary>
	/// A contact to peek at: one the side remembers that the pair's fire spot sees, chosen
	/// by <see cref="MarksmanPlan.ChooseTarget"/>. On the scan.
	/// </summary>
	PeekTarget,

	/// <summary>A threat inside <see cref="MarksmanPlan.StandoffMeters"/> of it: too near to peek at, so it moves away. On the scan.</summary>
	Standoff,

	/// <summary>
	/// Its pair is burned: it was fired upon while out on a peek, or has peeked from it
	/// <see cref="UnitPlanTraits.PeeksPerPair"/> times. Held until it is hidden at a pair
	/// whose fire spot is <see cref="UnitPlanTraits.RelocateMeters"/> from the last, or for
	/// <see cref="UnitPlanTraits.BurnedMaxTicks"/>.
	/// </summary>
	Burned,

	/// <summary>
	/// A place for a heavy gunner to suppress (docs/TACTICS.md §5.2): where the side sees an
	/// enemy, last saw one, or a round came from, in reach and with a line to it over the
	/// cover board. On the scan; off for <see cref="UnitPlanTraits.SuppressRetryTicks"/>
	/// after suppressing for <see cref="UnitPlanTraits.SuppressMaxTicks"/> under an order
	/// that goes somewhere.
	/// </summary>
	Suppress,

	/// <summary>
	/// A heavy gunner's belt is under <see cref="UnitPlanTraits.BeltReserveRounds"/>, until a
	/// reload fills it: it keeps those for what it can see. Every tick, from the weapon.
	/// </summary>
	BeltLow,

	Count,
}

/// <summary>Where a marksman is in its peek pair (docs/TACTICS.md §5.3).</summary>
public enum PeekState : byte
{
	/// <summary>No pair: it fights as a rifleman does.</summary>
	None = 0,

	/// <summary>A pair, and it is not in the hide spot.</summary>
	Away = 1,

	/// <summary>In the hide spot, for less than <see cref="UnitPlanTraits.MinHideTicks"/>.</summary>
	Hidden = 2,

	/// <summary>In the hide spot for <see cref="UnitPlanTraits.MinHideTicks"/> or more: ready to pop out.</summary>
	Ready = 3,

	/// <summary>Out on a peek: from popping out until it has ducked back.</summary>
	Exposed = 4,
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
	Resupply,
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

	/// <summary>To a spot behind a low wall from its target, firing on the way; in it, it stands and fights (docs/COVER.md §6).</summary>
	HoldCover,

	/// <summary>Onto the control point its attack is on, to its post there, firing on the way and keeping pace with its squad.</summary>
	TakePoint,

	/// <summary>On the point: to its post there, and standing to fight whatever it sees from it.</summary>
	HoldPoint,

	/// <summary>To its spot behind cover on or beside the point, firing on the way; in it, it stands and fights.</summary>
	HoldPointCover,

	/// <summary>To a spot behind cover from where it was fired upon, firing back on the way (docs/TACTICS.md §5.1).</summary>
	TakeCoverFromFire,

	/// <summary>In that spot: firing back at where the fire came from while it keeps coming, then holding.</summary>
	ReturnFire,

	/// <summary>A grenade at a hidden enemy, standing for the throw (docs/TACTICS.md §5.4).</summary>
	ThrowGrenade,

	/// <summary>A marksman to its hide spot, holding its fire on the way; in it, down behind a low wall (docs/TACTICS.md §5.3).</summary>
	ToHide,

	/// <summary>Out of the hide spot to the fire spot beside it, or up from behind its low wall.</summary>
	PopOut,

	/// <summary>Standing in the fire spot, firing at its peek target: two shots, or 4.5 s, whichever comes first.</summary>
	FireFromPeek,

	/// <summary>Back into the hide spot, or down behind the low wall.</summary>
	Duck,

	/// <summary>To another pair, holding its fire on the way: its pair is burned, or a threat is too near.</summary>
	Relocate,

	/// <summary>
	/// A heavy gunner's bursts at the places it may suppress, the nearest first and then the
	/// next, from a low wall it can fire over when there is one (docs/TACTICS.md §5.2).
	/// </summary>
	SuppressPoint,

	/// <summary>A heavy gunner fills its belt, down behind its wall when it is in its spot.</summary>
	Reload,
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

	/// <summary>
	/// With no target of its own, fire at <see cref="FirePoint"/>: a place, not a player —
	/// where the fire came from (docs/TACTICS.md §5.1).
	/// </summary>
	public bool FireAt;

	/// <inheritdoc cref="FireAt"/>
	public Vector3 FirePoint;

	/// <summary>
	/// Throw a grenade at <see cref="ThrowPoint"/>: where a hidden enemy is reckoned to
	/// be, with no target of its own (docs/TACTICS.md §5.4).
	/// </summary>
	public bool ThrowAt;

	/// <inheritdoc cref="ThrowAt"/>
	public Vector3 ThrowPoint;

	/// <summary>
	/// Crouch (docs/TACTICS.md §5.8): down behind its cover, not firing, as it is in
	/// <see cref="UnitGoal.ReturnFire"/> between the bursts it answers rounds with, and
	/// in any spot behind cover while it reloads. It stands to shoot.
	/// </summary>
	public bool Crouch;

	/// <summary>
	/// Fire at nothing further off than <see cref="UnitPlanTraits.HoldFireMeters"/>: a
	/// marksman on its way to hide, ducking back or relocating does not give itself away
	/// (docs/TACTICS.md §5.3). What is nearer it still shoots.
	/// </summary>
	public bool HoldFire;

	/// <summary>
	/// Reload now, with nothing to fire at: a heavy gunner filling its belt before it is
	/// empty (docs/TACTICS.md §5.2).
	/// </summary>
	public bool Reload;
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

	/// <summary>The health fraction a wounded unit is resupplied back to before it goes back to its order.</summary>
	public readonly float ResupplyDoneFraction;

	/// <summary>
	/// How far outside a supply source's reach a wounded unit still goes to it. Past
	/// that it stays with its order, and its squad's Refill is the commander's to call
	/// (§5.3): a unit that walked home on its own from across the map would be a unit
	/// its squad attacks without.
	/// </summary>
	public readonly float SupplyRangeMeters;

	/// <summary>How much further than <see cref="SupplyRangeMeters"/> a source already in range stays in it.</summary>
	public readonly float SupplyRangeHysteresisMeters;

	/// <summary>How far inside a source's reach a unit walking to it stops.</summary>
	public readonly float SupplyMarginMeters;

	/// <summary>
	/// How long a resupply may take, walk and all, before it is given up on — a unit
	/// that cannot reach the source, or keeps being hit inside it (§3.4, P4: a method
	/// the world has to confirm needs a give-up)…
	/// </summary>
	public readonly int ResupplyMaxTicks;

	/// <summary>…and how long after that the unit goes back to its order before it tries again.</summary>
	public readonly int ResupplyRetryTicks;

	/// <summary>How far inside a control point's edge a unit taking it counts as on it; it stays on it to the edge.</summary>
	public readonly float ObjectiveEnterMeters;

	/// <summary>
	/// How many of the units taking a point must stand on it. Presence is boolean — one
	/// body takes a node — so the rest may fight from cover just off it (<see cref="ControlPoint"/>).
	/// </summary>
	public readonly int HoldersPerPoint;

	/// <summary>How far outside a point's edge a unit that need not stand on it may take cover to fight for it.</summary>
	public readonly float PointCoverSlackMeters;

	/// <summary>How much further in than it has to be a post or a holder's cover on the point is: what a shove does not undo.</summary>
	public readonly float PointPostMarginMeters;

	/// <summary>
	/// How long a unit whose order goes somewhere may spend reacting to fire under that
	/// order, in all, before it goes on regardless until it is next hit (docs/TACTICS.md
	/// §5.1): a squad pinned for ever is the failure to guard against (§9).
	/// </summary>
	public readonly int ReactMaxTicks;

	/// <summary>How long after each round that reaches it a unit in cover keeps firing back at where it came from.</summary>
	public readonly int ReturnFireTicks;

	/// <summary>How long a marksman stays hidden in its spot before it pops out again (docs/TACTICS.md §5.3).</summary>
	public readonly int MinHideTicks;

	/// <summary>How long one peek may keep it out, from popping out to the end of its firing.</summary>
	public readonly int PeekMaxTicks;

	/// <summary>How many shots one peek fires before it ducks.</summary>
	public readonly int ShotsPerPeek;

	/// <summary>How many peeks one pair is good for before the marksman moves on.</summary>
	public readonly int PeeksPerPair;

	/// <summary>How near a target has to be for a marksman holding its fire to shoot it anyway.</summary>
	public readonly float HoldFireMeters;

	/// <summary>
	/// How long a marksman may walk to a hide spot, under one operator, before it gives
	/// peeking up (P4): a spot it cannot reach is not one to keep walking at…
	/// </summary>
	public readonly int PeekWalkMaxTicks;

	/// <summary>…and how long it fights as a rifleman before it looks for a pair again.</summary>
	public readonly int PeekRetryTicks;

	/// <summary>How long a burned pair keeps it relocating when it finds no other: then it may peek from anywhere again.</summary>
	public readonly int BurnedMaxTicks;

	/// <summary>How far from the last fire spot a relocation's must be.</summary>
	public readonly float RelocateMeters;

	/// <summary>How far from every fire spot of the last <see cref="RecentFireTicks"/> a relocation's must be.</summary>
	public readonly float RecentFireMeters;

	/// <inheritdoc cref="RecentFireMeters"/>
	public readonly int RecentFireTicks;

	/// <summary>How long one of a heavy gunner's bursts at a place lasts (docs/TACTICS.md §5.2)…</summary>
	public readonly int SuppressBurstTicks;

	/// <summary>…and how often one starts.</summary>
	public readonly int SuppressCycleTicks;

	/// <summary>The rounds a heavy gunner keeps in its belt for what it can see: under this, it suppresses nothing, and reloads.</summary>
	public readonly int BeltReserveRounds;

	/// <summary>
	/// How long a heavy gunner may suppress under an order that goes somewhere before it
	/// gives it up (P4): the order still arrives…
	/// </summary>
	public readonly int SuppressMaxTicks;

	/// <summary>…and how long it carries the order out before it suppresses again.</summary>
	public readonly int SuppressRetryTicks;

	public UnitPlanTraits(float healthOkFraction, float healthCriticalFraction, float healthHysteresis,
		float engagedFriendMeters, float paceAheadMeters, float paceResumeMeters, int paceMaxWaitTicks,
		float postSpacingMeters, float postMinRadiusMeters, float postLeashShare, int underFireTicks,
		float threatBearingMeters, float resupplyDoneFraction, float supplyRangeMeters,
		float supplyRangeHysteresisMeters, float supplyMarginMeters, int resupplyMaxTicks, int resupplyRetryTicks,
		float objectiveEnterMeters, int holdersPerPoint, float pointCoverSlackMeters, float pointPostMarginMeters,
		int reactMaxTicks = SimConfig.TickRate * 8, int returnFireTicks = SimConfig.TickRate * 2,
		int minHideTicks = SimConfig.TickRate * 2, int peekMaxTicks = SimConfig.TickRate * 9 / 2, int shotsPerPeek = 2,
		int peeksPerPair = 2, float holdFireMeters = 30f, int peekWalkMaxTicks = SimConfig.TickRate * 12,
		int peekRetryTicks = SimConfig.TickRate * 10, int burnedMaxTicks = SimConfig.TickRate * 20,
		float relocateMeters = 8f, float recentFireMeters = 6f, int recentFireTicks = SimConfig.TickRate * 20,
		int suppressBurstTicks = SimConfig.TickRate, int suppressCycleTicks = SimConfig.TickRate * 2,
		int beltReserveRounds = 30, int suppressMaxTicks = SimConfig.TickRate * 8,
		int suppressRetryTicks = SimConfig.TickRate * 8)
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
		ResupplyDoneFraction = Math.Clamp(resupplyDoneFraction, HealthOkFraction, 1f);
		SupplyRangeMeters = MathF.Max(supplyRangeMeters, 0f);
		SupplyRangeHysteresisMeters = MathF.Max(supplyRangeHysteresisMeters, 0f);
		SupplyMarginMeters = MathF.Max(supplyMarginMeters, 0f);
		ResupplyMaxTicks = Math.Max(resupplyMaxTicks, 1);
		ResupplyRetryTicks = Math.Max(resupplyRetryTicks, 0);
		ObjectiveEnterMeters = MathF.Max(objectiveEnterMeters, 0f);
		HoldersPerPoint = Math.Max(holdersPerPoint, 1);
		PointCoverSlackMeters = MathF.Max(pointCoverSlackMeters, 0f);
		PointPostMarginMeters = MathF.Max(pointPostMarginMeters, 0f);
		ReactMaxTicks = Math.Max(reactMaxTicks, 1);
		ReturnFireTicks = Math.Max(returnFireTicks, 0);
		MinHideTicks = Math.Max(minHideTicks, 0);
		PeekMaxTicks = Math.Max(peekMaxTicks, 1);
		ShotsPerPeek = Math.Max(shotsPerPeek, 1);
		PeeksPerPair = Math.Max(peeksPerPair, 1);
		HoldFireMeters = MathF.Max(holdFireMeters, 0f);
		PeekWalkMaxTicks = Math.Max(peekWalkMaxTicks, 1);
		PeekRetryTicks = Math.Max(peekRetryTicks, 0);
		BurnedMaxTicks = Math.Max(burnedMaxTicks, 1);
		RelocateMeters = MathF.Max(relocateMeters, 0f);
		RecentFireMeters = MathF.Max(recentFireMeters, 0f);
		RecentFireTicks = Math.Max(recentFireTicks, 0);
		SuppressCycleTicks = Math.Max(suppressCycleTicks, 1);
		SuppressBurstTicks = Math.Clamp(suppressBurstTicks, 1, SuppressCycleTicks);
		BeltReserveRounds = Math.Max(beltReserveRounds, 0);
		SuppressMaxTicks = Math.Max(suppressMaxTicks, 1);
		SuppressRetryTicks = Math.Max(suppressRetryTicks, 0);
	}

	/// <summary>
	/// §5.2's numbers. The health bands are the ground bot's. A unit more than 8 m
	/// ahead of its squad waits until it is within 3 m, for 8 s at a stretch at most;
	/// posts stand 4 m apart on a ring of at least 4 m and at most half the leash.
	/// A unit out of ok health goes to a supply source within 60 m of its reach, stops
	/// 3 m inside it and comes back at 95 %; a resupply that has not finished in 60 s
	/// is dropped for 30 s. A unit taking a point counts as on it 1 m inside its
	/// edge; two stand on it, and the rest may take cover up to 4 m outside it; posts
	/// and a holder's cover are a metre further in than they have to be. A unit whose
	/// order goes somewhere reacts to fire for 8 s at most under it; one in cover fires
	/// back for 2 s after each round that reaches it (docs/TACTICS.md §5.1). A marksman
	/// hides 2 s between peeks, fires two shots or for 4.5 s a peek, and two peeks a pair;
	/// on its way it fires at nothing beyond 30 m; a walk of 12 s to a hide spot gives
	/// peeking up for 10 s; a burned pair moves it 8 m from the last fire spot and 6 m
	/// from every one of the last 20 s, for 20 s at most (§5.3). A heavy gunner fires a
	/// one-second burst every two seconds at a place it suppresses, keeps the last 30
	/// rounds of its belt for what it sees, and under an order that goes somewhere
	/// suppresses for 8 s and then carries the order out for 8 s (§5.2).
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
		threatBearingMeters: 80f,
		resupplyDoneFraction: 0.95f,
		supplyRangeMeters: 60f,
		supplyRangeHysteresisMeters: 10f,
		supplyMarginMeters: 3f,
		resupplyMaxTicks: SimConfig.TickRate * 60,
		resupplyRetryTicks: SimConfig.TickRate * 30,
		objectiveEnterMeters: 1f,
		holdersPerPoint: 2,
		pointCoverSlackMeters: 4f,
		pointPostMarginMeters: 1f);
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

	/// <summary>A friendly supply source can heal this unit: there is one, and the unit has a rate to heal at (<see cref="Resupply"/>).</summary>
	public bool HasSupply;

	/// <summary>How far outside the nearest source's reach the unit is, measured flat; negative inside.</summary>
	public float SupplyEdgeMeters;

	/// <summary>A spot behind cover from the fight it is in, or from what is shooting at a builder (docs/COVER.md §6).</summary>
	public bool HasCover;

	/// <summary>The box beside that spot hid it from the threat when the scan looked.</summary>
	public bool CoverHides;

	/// <summary>
	/// Its order is an attack on the control point at <see cref="UnitContext.ObjectivePoint"/>,
	/// which the scan has filled in with the point's radius and the unit's post on it.
	/// </summary>
	public bool Objective;

	/// <summary>A spot behind cover from where it was last fired upon (docs/TACTICS.md §5.1).</summary>
	public bool HasFireCover;

	/// <summary>The box beside that spot hid it from there when the scan looked.</summary>
	public bool FireCoverHides;
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
	private bool _hasCover;
	private bool _coverHides;
	private bool _hasTarget;
	private Vector3 _targetPoint;
	private bool _objective;
	private uint _paceTick = uint.MaxValue;
	private bool _hasFireCover;
	private bool _fireCoverHides;
	private uint _reactTick = uint.MaxValue;
	private uint _reactSpentTick;
	private bool _peeking;
	private uint _hiddenSince;
	private uint _peekStartTick;
	private uint _peekShots;
	private uint _walkTick = uint.MaxValue;
	private uint _burnedTick;
	private uint _lastRoundTick;
	private readonly Vector3[] _recentFire = new Vector3[RecentFireCapacity];
	private readonly uint[] _recentFireTick = new uint[RecentFireCapacity];
	private int _recentFireNext;
	private readonly Vector3[] _suppress = new Vector3[GunnerPlan.Capacity];
	private int _suppressCount;
	private uint _suppressTick = uint.MaxValue;
	private uint _cycleStart;
	private uint _cycleIndex = uint.MaxValue;
	private uint _lastBurstTick = uint.MaxValue;
	private bool _hasBurstFeet;
	private Vector3 _burstFeet;

	/// <summary>How many fire spots a marksman remembers for relocating away from: a peek every 4.5 s or so for 20 s, and some to spare.</summary>
	public const int RecentFireCapacity = 8;

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

	/// <summary>The middle of the nearest friendly supply source's reach: where a resupply walks (D2).</summary>
	public Vector3 SupplyPoint;

	/// <summary>Its spot behind cover (docs/COVER.md §6). Only with <see cref="HasCoverPoint"/>.</summary>
	public bool HasCoverPoint;
	public Vector3 CoverPoint;

	/// <summary>The control point its attack is on: the node's centre and capture radius. Only with <see cref="UnitFact.Objective"/>.</summary>
	public Vector3 ObjectivePoint;
	public float ObjectiveRadius;

	/// <summary>Its post on that point, inside the radius by what keeps it counting there.</summary>
	public Vector3 ObjectivePost;

	/// <summary>Where it reckons it was last fired upon from (docs/TACTICS.md §4.1). Meaningful while <see cref="UnitFact.FiredUpon"/> is not none.</summary>
	public Vector3 FiredFromPoint;

	/// <summary>The tick the latest round that came near it came on.</summary>
	public uint LastFiredUponTick;

	/// <summary>Its spot behind cover from <see cref="FiredFromPoint"/>. Only with <see cref="HasFireCoverPoint"/>.</summary>
	public bool HasFireCoverPoint;
	public Vector3 FireCoverPoint;

	/// <summary>Where a hidden enemy's feet are reckoned to be, for a grenade. Only with <see cref="UnitFact.Grenade"/>.</summary>
	public Vector3 ThrowPoint;

	/// <summary>Its weapon is reloading, this tick (docs/TACTICS.md §5.8). Not a fact: nothing is planned on it.</summary>
	public bool Reloading;

	/// <summary>How many rounds its weapon has fired in all, this tick: what a peek counts its shots by (docs/TACTICS.md §5.3).</summary>
	public uint Shots;

	/// <summary>
	/// A marksman's peek pair (docs/TACTICS.md §5.3), from its scan: the spot it hides in,
	/// the spot it fires from, and whether they are one spot behind a low wall. Only with
	/// <see cref="HasPeekPoint"/>.
	/// </summary>
	public bool HasPeekPoint;

	/// <inheritdoc cref="HasPeekPoint"/>
	public Vector3 HidePoint;

	/// <inheritdoc cref="HasPeekPoint"/>
	public Vector3 FirePoint;

	/// <inheritdoc cref="HasPeekPoint"/>
	public bool PeekLow;

	/// <summary>Whom a peek shoots at: the contact <see cref="UnitFact.PeekTarget"/> is about.</summary>
	public int PeekTargetOwnerId;

	/// <summary>Peeks from the pair it has now, against <see cref="UnitPlanTraits.PeeksPerPair"/>.</summary>
	public int PeeksFromPair;

	/// <summary>The fire spot of its last peek. Only with <see cref="HasLastFireSpot"/>.</summary>
	public bool HasLastFireSpot;

	/// <inheritdoc cref="HasLastFireSpot"/>
	public Vector3 LastFireSpot;

	/// <summary>A walk to a hide spot was given up on: no pair until this tick.</summary>
	public uint PeekRetryTick;

	/// <summary>Walks to a hide spot given up on. For the tests.</summary>
	public int FailedPeeks;

	/// <summary>Ticks the running operator has spent walking to a hide spot, against <see cref="UnitPlanTraits.PeekWalkMaxTicks"/>.</summary>
	public int WalkedTicks;

	/// <summary>Ticks it has spent reacting to fire under its current order, against <see cref="UnitPlanTraits.ReactMaxTicks"/>.</summary>
	public int ReactedTicks;

	/// <summary>
	/// Ticks a heavy gunner has suppressed under an order that goes somewhere since it last
	/// gave it up, or was given the order, against <see cref="UnitPlanTraits.SuppressMaxTicks"/>.
	/// </summary>
	public int SuppressedTicks;

	/// <summary>Suppressing was given up on: <see cref="UnitFact.Suppress"/> stays off until this tick.</summary>
	public uint SuppressRetryTick;

	/// <summary>Times it gave suppressing up. For the tests.</summary>
	public int SuppressionsGivenUp;

	/// <summary>A resupply was given up on: <see cref="UnitFact.Supply"/> stays off until this tick.</summary>
	public uint SupplyRetryTick;

	/// <summary>Resupplies given up on. For the tests and the debug HUD.</summary>
	public int FailedResupplies;

	/// <summary>The tick the running operator started on.</summary>
	public uint GoalStartTick;

	/// <summary>True while an advance is holding for its squad to close up.</summary>
	public bool Waiting;

	/// <summary>Ticks the current wait has lasted, against <see cref="UnitPlanTraits.PaceMaxWaitTicks"/>.</summary>
	public int WaitedTicks;

	/// <summary>A wait ran out: this advance no longer waits for its squad.</summary>
	public bool PaceGivenUp;

	/// <summary>The goals that keep pace with the squad, and whose <see cref="Waiting"/> means it is waiting for it.</summary>
	public static bool Paces(UnitGoal goal) => goal is UnitGoal.Advance or UnitGoal.TakePoint;

	/// <summary>The goals of docs/TACTICS.md §5: what the agent stream's <c>unit_task</c> reports a unit starting.</summary>
	public static bool IsTactic(UnitGoal goal) =>
		goal is UnitGoal.TakeCoverFromFire or UnitGoal.ReturnFire or UnitGoal.ThrowGrenade or UnitGoal.ToHide
			or UnitGoal.PopOut or UnitGoal.FireFromPeek or UnitGoal.Duck or UnitGoal.Relocate or UnitGoal.SuppressPoint
			or UnitGoal.Reload;

	/// <summary>
	/// The orders a marksman peeks under (docs/TACTICS.md §5.3): an attack, a defend, none,
	/// or a control point to take. A move, a patrol, a build or a ride is carried out as given.
	/// </summary>
	public static bool PeeksUnder(OrderKind order, bool objective) =>
		order is OrderKind.Attack or OrderKind.Defend or OrderKind.None || objective;

	/// <summary>
	/// The orders a heavy gunner suppresses under (docs/TACTICS.md §5.2): those a marksman
	/// peeks under, the orders with a fight of their own. On a move or a patrol it shoots on
	/// the walk, as it always has.
	/// </summary>
	public static bool SuppressesUnder(OrderKind order, bool objective) => PeeksUnder(order, objective);

	/// <summary>The places it may suppress, from its scan, nearest first.</summary>
	public ReadOnlySpan<Vector3> SuppressPlaces => _suppress.AsSpan(0, _suppressCount);

	/// <summary>
	/// An order that goes somewhere: one that reacting to fire must not stop for good,
	/// so a unit under it reacts for <see cref="UnitPlanTraits.ReactMaxTicks"/> at most
	/// (docs/TACTICS.md §5.1).
	/// </summary>
	public static bool GoesSomewhere(OrderKind order) => order is OrderKind.Move or OrderKind.Patrol or OrderKind.Attack;

	/// <summary>
	/// An order with no fight of its own to take cover in: a move or a patrol, which shoot
	/// on the walk. A unit under one reacts to fire even at a target it can see; under any
	/// other it reacts only to fire from one it cannot, and fights one it can from
	/// cover against it (docs/COVER.md §6).
	/// </summary>
	public static bool FightsOnTheWay(OrderKind order) => order is OrderKind.Move or OrderKind.Patrol;

	/// <summary>What its planner has done lately, for the spectator's debugger (docs/AI_DEBUG.md §3.3). Null for a context made outside <see cref="UnitPlanning"/>.</summary>
	public HtnHistory History { get; internal set; }

	public byte Get(UnitFact fact) => GetState((int)fact);

	public bool Is(UnitFact fact) => GetState((int)fact) != 0;

	/// <summary>What a sensor writes. Marks the context dirty only when the value changes.</summary>
	public void Sense(UnitFact fact, byte value) => SetState((int)fact, value, true, EffectType.Permanent);

	public void Sense(UnitFact fact, bool value) => Sense(fact, value ? (byte)1 : (byte)0);

	/// <summary>What the strategist lets it do on its own (docs/TACTICS.md §5.9), every tick, from the unit.</summary>
	public void SenseAutonomy(Sim.MicroAutonomy mode) => Sense(UnitFact.MicroAutonomy, (byte)mode);

	/// <summary>
	/// Being fired upon (docs/TACTICS.md §4.1), every tick, from the unit's fire memory:
	/// the worst round in the window, where the latest came from and when, and when it
	/// was last hit — which, after the cap on reacting was spent, lifts the cap.
	/// </summary>
	public void SenseFire(FireKind kind, Vector3 firedFrom, uint lastTick, uint lastHitTick)
	{
		FiredFromPoint = firedFrom;
		LastFiredUponTick = lastTick;
		Sense(UnitFact.FiredUpon, (byte)kind);

		if (Is(UnitFact.ReactSpent) && lastHitTick > _reactSpentTick)
		{
			ResetReact();
		}

		// A round that reaches a marksman out on a peek burns its pair (docs/TACTICS.md §5.3).
		if (_peeking && kind != FireKind.None && lastTick > _lastRoundTick)
		{
			Burn();
		}

		_lastRoundTick = lastTick;
	}

	/// <summary>A new order, or a new life: reacting to fire starts over (docs/TACTICS.md §5.1).</summary>
	public void ResetReact()
	{
		ReactedTicks = 0;
		_reactTick = uint.MaxValue;
		_reactSpentTick = 0;
		Sense(UnitFact.ReactSpent, false);
	}

	/// <summary>
	/// A hidden enemy to throw a grenade at, from the unit's scan (docs/TACTICS.md §5.4),
	/// or none. The engine has already found the arc; the planner says when.
	/// </summary>
	public void SenseGrenade(bool hasThrow, Vector3 throwPoint)
	{
		ThrowPoint = throwPoint;
		Sense(UnitFact.Grenade, hasThrow);
	}

	/// <summary>The unit has tried its throw, whether a grenade left its hand or not: nothing to throw at until the next scan.</summary>
	public void Threw() => Sense(UnitFact.Grenade, false);

	/// <summary>
	/// What its weapon is doing, every tick: reloading or not, how many rounds it has fired,
	/// and how many it has left — which, for a heavy gunner, is whether it is into the
	/// rounds it keeps (docs/TACTICS.md §5.2).
	/// </summary>
	public void SenseWeapon(bool reloading, uint shots = 0, int rounds = int.MaxValue)
	{
		Reloading = reloading;
		Shots = shots;
		Sense(UnitFact.BeltLow, UnitTraits.Doctrine == UnitDoctrine.Gunner && rounds < Traits.BeltReserveRounds);
	}

	/// <summary>
	/// The places a heavy gunner may suppress, from its scan (docs/TACTICS.md §5.2), nearest
	/// first; none for a unit that may not. The engine has found them; the planner says
	/// when to fire at which.
	/// </summary>
	public void SenseSuppress(ReadOnlySpan<Vector3> places)
	{
		_suppressCount = Math.Min(places.Length, _suppress.Length);
		places[.._suppressCount].CopyTo(_suppress);
		Sense(UnitFact.Suppress, _suppressCount > 0 && Tick >= SuppressRetryTick);
	}

	/// <summary>A new order, or a new life: suppressing starts over, its cap and any give-up with it.</summary>
	public void ResetSuppress()
	{
		SuppressedTicks = 0;
		_suppressTick = uint.MaxValue;
		SuppressRetryTick = 0;
	}

	/// <summary>
	/// A marksman's pair and target, from its scan (docs/TACTICS.md §5.3), or none: the
	/// engine has searched the cover board; the planner says when to hide and when to peek.
	/// A pair whose hide spot is somewhere else is a new pair, with no peeks from it yet.
	/// </summary>
	public void SensePeek(bool hasPair, Vector3 hide, Vector3 fire, bool low, int targetOwnerId, bool standoff)
	{
		if (hasPair && (!HasPeekPoint || Flat(hide, HidePoint) > CoverBoard.ClaimMeters))
		{
			PeeksFromPair = 0;
		}

		HasPeekPoint = hasPair;
		HidePoint = hide;
		FirePoint = fire;
		PeekLow = low;
		PeekTargetOwnerId = hasPair ? targetOwnerId : OwnerId.None;
		Sense(UnitFact.PeekTarget, hasPair && targetOwnerId != OwnerId.None);
		Sense(UnitFact.Standoff, standoff);
		SensePeekState();
	}

	/// <summary>
	/// The fire spots of its peeks within <see cref="UnitPlanTraits.RecentFireTicks"/>, into
	/// <paramref name="into"/>: what a relocation keeps away from. Returns how many.
	/// </summary>
	public int RecentFireSpots(Span<Vector3> into)
	{
		int count = 0;
		for (int i = 0; i < RecentFireCapacity && count < into.Length; i++)
		{
			if (_recentFireTick[i] != 0 && Tick >= _recentFireTick[i]
				&& Tick - _recentFireTick[i] <= (uint)Traits.RecentFireTicks)
			{
				into[count++] = _recentFire[i];
			}
		}

		return count;
	}

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
		SenseSupply(sense);

		_hasCover = sense.HasCover && HasCoverPoint;
		_coverHides = sense.CoverHides;
		_objective = sense.Objective;
		_hasFireCover = sense.HasFireCover && HasFireCoverPoint;
		_fireCoverHides = sense.FireCoverHides;

		SenseContact();
		SenseLeash();
		SenseCover();
		SenseObjective();
		SenseFireCover();
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
		SenseCover();
		SenseObjective();
		SenseFireCover();
		SensePeekState();
	}

	/// <summary>
	/// Where a marksman is in its pair, every tick (docs/TACTICS.md §5.3): the pair is the
	/// scan's; out on a peek from popping out to ducking back; in the hide spot from
	/// <see cref="CoverBands.ArriveMeters"/> of it, held to <see cref="CoverBands.LeaveMeters"/>,
	/// and ready once it has been there <see cref="UnitPlanTraits.MinHideTicks"/>. Hidden
	/// at a pair whose fire spot is far enough from the last, a burned pair is behind it.
	/// </summary>
	private void SensePeekState()
	{
		var previous = (PeekState)Get(UnitFact.Peek);
		PeekState state;
		if (!HasPeekPoint)
		{
			state = PeekState.None;
		}
		else if (_peeking)
		{
			state = PeekState.Exposed;
		}
		else
		{
			bool wasIn = previous is PeekState.Hidden or PeekState.Ready;
			if (Flat(Position, HidePoint) > (wasIn ? CoverBands.LeaveMeters : CoverBands.ArriveMeters))
			{
				state = PeekState.Away;
			}
			else
			{
				if (!wasIn)
				{
					_hiddenSince = Tick;
				}

				state = Tick - _hiddenSince >= (uint)Traits.MinHideTicks ? PeekState.Ready : PeekState.Hidden;
			}
		}

		if (Is(UnitFact.Burned) && (Tick - _burnedTick >= (uint)Traits.BurnedMaxTicks
			|| (state is PeekState.Hidden or PeekState.Ready
				&& (!HasLastFireSpot || Flat(FirePoint, LastFireSpot) >= Traits.RelocateMeters))))
		{
			PeeksFromPair = 0;
			Sense(UnitFact.Burned, false);
		}

		Sense(UnitFact.Peek, (byte)state);
	}

	/// <summary>
	/// The point, every tick: the scan says which point the order is on, and the order
	/// as it stands now must still be an attack on it — a new order is a new plan on the
	/// tick it comes, not at the next scan. On it from <see cref="UnitPlanTraits.ObjectiveEnterMeters"/>
	/// inside the edge, and to the edge, by the rule the economy counts bodies with.
	/// </summary>
	private void SenseObjective()
	{
		bool objective = _objective && Order.Kind == OrderKind.Attack && Order.TargetOwnerId == OwnerId.None
			&& CapturePad.Covers(ObjectivePoint, ObjectiveRadius, Order.Target);
		Sense(UnitFact.Objective, objective);

		float edge = MathF.Max(Is(UnitFact.OnObjective) ? ObjectiveRadius : ObjectiveRadius - Traits.ObjectiveEnterMeters, 0f);
		Sense(UnitFact.OnObjective, objective && CapturePad.Covers(ObjectivePoint, edge, Position));
	}

	/// <summary>
	/// Cover against the fight, every tick: the spot and whether its box hides the
	/// unit are the scan's, whether the unit has reached it is now's.
	/// </summary>
	private void SenseCover() =>
		Sense(UnitFact.Cover, (byte)CoverBands.Band(_hasCover, Flat(Position, CoverPoint), _coverHides,
			(CoverState)Get(UnitFact.Cover)));

	/// <summary>Cover from the fire, every tick, as <see cref="SenseCover"/> is from the fight.</summary>
	private void SenseFireCover() =>
		Sense(UnitFact.FireCover, (byte)CoverBands.Band(_hasFireCover, Flat(Position, FireCoverPoint), _fireCoverHides,
			(CoverState)Get(UnitFact.FireCover)));

	/// <summary>
	/// Resupply's three facts (D2), each with its hysteresis: wounded from the moment
	/// health leaves ok until it is back to <see cref="UnitPlanTraits.ResupplyDoneFraction"/>;
	/// a source in range, held a little further once it is; inside its reach, entered
	/// a margin inside the edge and held to the edge.
	/// </summary>
	private void SenseSupply(in UnitSense sense)
	{
		bool wounded = Is(UnitFact.Wounded)
			? sense.HealthFraction < Traits.ResupplyDoneFraction
			: Get(UnitFact.Health) != (byte)HealthBand.Ok;
		Sense(UnitFact.Wounded, wounded);

		float range = Traits.SupplyRangeMeters + (Is(UnitFact.Supply) ? Traits.SupplyRangeHysteresisMeters : 0f);
		Sense(UnitFact.Supply, sense.HasSupply && sense.SupplyEdgeMeters <= range && Tick >= SupplyRetryTick);

		float inside = Is(UnitFact.Supplied) ? 0f : -Traits.SupplyMarginMeters;
		Sense(UnitFact.Supplied, sense.HasSupply && sense.SupplyEdgeMeters <= inside);
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

	/// <summary>
	/// Under micro-autonomy, a unit in its spot behind cover crouches while it reloads
	/// (docs/TACTICS.md §5.8): the spot the fight's cover operators and ReturnFire stand
	/// in. Applied after the operator has written its intent.
	/// </summary>
	private void CrouchToReload(UnitGoal goal)
	{
		bool inSpot = goal switch
		{
			UnitGoal.HoldCover or UnitGoal.HoldPointCover or UnitGoal.SuppressPoint or UnitGoal.Reload =>
				Get(UnitFact.Cover) == (byte)CoverState.In,
			UnitGoal.ReturnFire => Get(UnitFact.FireCover) == (byte)CoverState.In,
			_ => false,
		};

		if (inSpot && Reloading && Get(UnitFact.MicroAutonomy) == (byte)MicroAutonomy.On)
		{
			Intent.Crouch = true;
		}
	}

	/// <summary>Starts an operator: the intent is new, and so is its clock.</summary>
	public TaskStatus Begin(UnitGoal goal)
	{
		GoalStartTick = Tick;
		Waiting = false;
		WaitedTicks = 0;
		PaceGivenUp = false;
		_paceTick = uint.MaxValue;
		WalkedTicks = 0;
		_walkTick = uint.MaxValue;

		if (goal == UnitGoal.PopOut)
		{
			StartPeek();
		}
		else if (goal == UnitGoal.FireFromPeek)
		{
			_peekShots = Shots;
		}

		return Perform(goal);
	}

	/// <summary>
	/// Ends one, whether it was stopped, aborted or replaced: the unit goes back to its
	/// order, and a peek cut short is over — it is no longer out on one.
	/// </summary>
	public void End()
	{
		Intent = default;
		if (_peeking)
		{
			_peeking = false;
			SensePeekState();
		}
	}

	/// <summary>
	/// Out on a peek from this tick (docs/TACTICS.md §5.3): the exposure's clock starts, the
	/// pair has one more peek on it, and its fire spot is the last and a recent one.
	/// </summary>
	private void StartPeek()
	{
		_peeking = true;
		_peekStartTick = Tick;
		PeeksFromPair++;
		HasLastFireSpot = true;
		LastFireSpot = FirePoint;
		_recentFire[_recentFireNext] = FirePoint;
		_recentFireTick[_recentFireNext] = Math.Max(Tick, 1u);
		_recentFireNext = (_recentFireNext + 1) % RecentFireCapacity;
		SensePeekState();
	}

	/// <summary>Back in the hide spot: the peek is over, and a pair peeked from often enough is burned.</summary>
	private void EndPeek()
	{
		_peeking = false;
		if (PeeksFromPair >= Traits.PeeksPerPair)
		{
			Burn();
		}

		SensePeekState();
	}

	/// <summary>Its pair is burned from this tick: it moves on (docs/TACTICS.md §5.3).</summary>
	private void Burn()
	{
		_burnedTick = Tick;
		Sense(UnitFact.Burned, true);
	}

	/// <summary>
	/// One tick of an operator. Writes the intent and keeps running: every unit task
	/// lasts until a fact ends it. Idempotent — FluidHTN can run an operator twice in
	/// one tick (#21, §3.2) — so the one piece of state an operator keeps, how long an
	/// advance has waited, is counted once per tick.
	/// </summary>
	public TaskStatus Perform(UnitGoal goal)
	{
		Intent = new UnitIntent { Goal = goal, TargetOwnerId = FocusOwnerId };
		TaskStatus status = Operate(goal);
		CrouchToReload(goal);
		return status;
	}

	/// <summary>The operator for <paramref name="goal"/>: where to go and how, written into <see cref="Intent"/>.</summary>
	private TaskStatus Operate(UnitGoal goal)
	{
		switch (goal)
		{
			case UnitGoal.Retreat:
				// Without stopping to fight: a unit that turned to shoot would not be retreating.
				return Go(FallbackPoint, stopToFight: false);

			case UnitGoal.Resupply:
				if (Tick >= GoalStartTick && Tick - GoalStartTick > (uint)Traits.ResupplyMaxTicks)
				{
					return GiveUpResupply();
				}

				// Inside the reach it stands and heals; outside it, it walks to the middle,
				// which crosses the edge wherever it starts. A truck that drives off is
				// followed on the next scan.
				return Is(UnitFact.Supplied) ? Hold() : Go(SupplyPoint, stopToFight: true);

			case UnitGoal.TakeCover:
				// Behind a box from what is shooting at it when there is one in reach,
				// else beside the nearest friend (docs/COVER.md §6).
				return Go(Get(UnitFact.Cover) != (byte)CoverState.None ? CoverPoint : FriendPoint, stopToFight: false);

			case UnitGoal.HoldCover:
				// Walking to the spot it fires on the move; in it, it stands and fights.
				return Get(UnitFact.Cover) == (byte)CoverState.In ? Hold() : Go(CoverPoint, stopToFight: false);

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

			case UnitGoal.TakePoint:
				// Onto the point without stopping for what it sees: it fires on the way, and
				// it is on the point that it stands to fight.
				return KeepsPace() ? Hold() : Go(ObjectivePost, stopToFight: false);

			case UnitGoal.HoldPoint:
				return Go(ObjectivePost, stopToFight: true);

			case UnitGoal.HoldPointCover:
				return Get(UnitFact.Cover) == (byte)CoverState.In ? Hold() : Go(CoverPoint, stopToFight: false);

			case UnitGoal.TakeCoverFromFire:
				// To the spot without stopping, firing back on the way (docs/TACTICS.md §5.1).
				if (Reacted())
				{
					return TaskStatus.Failure;
				}

				FireBack();
				return Go(FireCoverPoint, stopToFight: false);

			case UnitGoal.ReturnFire:
				if (Reacted())
				{
					return TaskStatus.Failure;
				}

				// Down behind the wall between bursts; up for the two seconds after a round
				// that it answers, and for a target of its own (docs/TACTICS.md §5.8).
				FireBack();
				Intent.Crouch = !Intent.FireAt && Get(UnitFact.Contact) != (byte)UnitContact.Own;
				return Hold();

			case UnitGoal.ThrowGrenade:
				// It stands for the throw, which the engine makes this tick (docs/TACTICS.md §5.4).
				Intent.ThrowAt = true;
				Intent.ThrowPoint = ThrowPoint;
				return Hold();

			case UnitGoal.ToHide:
				// To the hide spot without giving itself away; in it, down behind a low wall
				// and standing behind anything taller (docs/TACTICS.md §5.3).
				if (Walked())
				{
					return GiveUpPeek();
				}

				Intent.HoldFire = true;
				if (InHide)
				{
					Intent.Crouch = PeekLow;
					return Hold();
				}

				return Go(HidePoint, stopToFight: false);

			case UnitGoal.PopOut:
				// Up, or out past the end of its box, until it stands in the fire spot.
				if (PeekTimedOut())
				{
					return TaskStatus.Failure;
				}

				Intent.TargetOwnerId = PeekTargetOwnerId;
				if (Flat(Position, FirePoint) <= CoverBands.ArriveMeters)
				{
					Hold();
					return TaskStatus.Success;
				}

				return Go(FirePoint, stopToFight: false);

			case UnitGoal.FireFromPeek:
				// Standing in the fire spot, its peek target the one it acquires first, for
				// so many shots or so long out, whichever comes first.
				Intent.TargetOwnerId = PeekTargetOwnerId;
				Hold();
				return Shots - _peekShots >= (uint)Traits.ShotsPerPeek || PeekTimedOut()
					? TaskStatus.Success
					: TaskStatus.Continue;

			case UnitGoal.Duck:
				// Back into the hide spot, or down behind the wall, holding its fire.
				Intent.HoldFire = true;
				if (Flat(Position, HidePoint) <= CoverBands.ArriveMeters)
				{
					Hold();
					Intent.Crouch = PeekLow;
					EndPeek();
					return TaskStatus.Success;
				}

				if (Tick - GoalStartTick > (uint)Traits.PeekMaxTicks)
				{
					return TaskStatus.Failure;
				}

				return Go(HidePoint, stopToFight: false);

			case UnitGoal.Relocate:
				// To the next pair, holding its fire on the way; in its hide spot it waits for
				// the burned pair to be behind it, or the threat to be far enough off.
				if (Walked())
				{
					return GiveUpPeek();
				}

				Intent.HoldFire = true;
				if (InHide)
				{
					Intent.Crouch = PeekLow;
					return Hold();
				}

				return Go(HidePoint, stopToFight: false);

			case UnitGoal.SuppressPoint:
				// Bursts at the places it may suppress, from its spot behind a low wall it can
				// fire over at the nearest when it has one, walking to it firing; else from
				// where it stands (docs/TACTICS.md §5.2).
				if (SuppressedLongEnough())
				{
					return GiveUpSuppress();
				}

				Burst();
				return FromCover();

			case UnitGoal.Reload:
				// Its belt filled where it fights from: down behind the wall in its spot.
				Intent.Reload = true;
				return FromCover();

			default:
				return TaskStatus.Failure;
		}
	}

	/// <summary>In its spot behind cover it stands its ground; with one in reach it walks to it, firing; with none it stands where it is.</summary>
	private TaskStatus FromCover() =>
		Get(UnitFact.Cover) == (byte)CoverState.In ? Hold()
		: Get(UnitFact.Cover) != (byte)CoverState.None ? Go(CoverPoint, stopToFight: false)
		: Hold();

	/// <summary>
	/// A heavy gunner's burst, this tick (docs/TACTICS.md §5.2): fire at the place for the
	/// first <see cref="UnitPlanTraits.SuppressBurstTicks"/> of every
	/// <see cref="UnitPlanTraits.SuppressCycleTicks"/>, and each new burst at the next place
	/// after the last one's — the nearest, then the next, and round again. A replan does not
	/// restart the rhythm; a lull of a whole cycle does, so the first burst after one is at
	/// once. A place gone from the list mid-burst is not fired at again. Idempotent within a
	/// tick.
	/// </summary>
	private void Burst()
	{
		uint cycle = (uint)Traits.SuppressCycleTicks;
		if (_lastBurstTick == uint.MaxValue || Tick < _lastBurstTick || Tick - _lastBurstTick > cycle)
		{
			_cycleStart = Tick;
			_cycleIndex = uint.MaxValue;
			_hasBurstFeet = false;
		}

		_lastBurstTick = Tick;
		ReadOnlySpan<Vector3> places = SuppressPlaces;
		uint elapsed = Tick - _cycleStart;
		uint index = elapsed / cycle;
		if (index != _cycleIndex || (_hasBurstFeet && GunnerPlan.Near(places, _burstFeet) < 0))
		{
			int next = GunnerPlan.Next(places, _hasBurstFeet, _burstFeet);
			_cycleIndex = index;
			_hasBurstFeet = next >= 0;
			_burstFeet = next >= 0 ? places[next] : _burstFeet;
		}

		if (_hasBurstFeet && elapsed % cycle < (uint)Traits.SuppressBurstTicks)
		{
			Intent.FireAt = true;
			Intent.FirePoint = GunnerPlan.AimPoint(_burstFeet);
		}
	}

	/// <summary>
	/// Counts a tick of suppressing, once a tick however often the operator runs, under an
	/// order that goes somewhere; true once that has lasted <see cref="UnitPlanTraits.SuppressMaxTicks"/>.
	/// </summary>
	private bool SuppressedLongEnough()
	{
		if (!GoesSomewhere(Order.Kind))
		{
			return false;
		}

		if (Tick != _suppressTick)
		{
			_suppressTick = Tick;
			SuppressedTicks++;
		}

		return SuppressedTicks > Traits.SuppressMaxTicks;
	}

	/// <summary>
	/// Suppressing that ran out of time under an order that goes somewhere: counted, and no
	/// place to suppress for <see cref="UnitPlanTraits.SuppressRetryTicks"/>, so the next plan
	/// is the order's (§3.4, P4). Set here rather than on the next scan so the replan this
	/// failure causes already sees it.
	/// </summary>
	private TaskStatus GiveUpSuppress()
	{
		SuppressionsGivenUp++;
		SuppressedTicks = 0;
		SuppressRetryTick = Tick + (uint)Traits.SuppressRetryTicks;
		Sense(UnitFact.Suppress, false);
		return TaskStatus.Failure;
	}

	/// <summary>In its hide spot, hidden or ready.</summary>
	private bool InHide => Get(UnitFact.Peek) is (byte)PeekState.Hidden or (byte)PeekState.Ready;

	/// <summary>Out on this peek for <see cref="UnitPlanTraits.PeekMaxTicks"/>.</summary>
	private bool PeekTimedOut() => Tick - _peekStartTick >= (uint)Traits.PeekMaxTicks;

	/// <summary>
	/// Counts a tick of walking to a hide spot, once a tick however often the operator
	/// runs; true once the running operator has walked for <see cref="UnitPlanTraits.PeekWalkMaxTicks"/>.
	/// </summary>
	private bool Walked()
	{
		if (Get(UnitFact.Peek) != (byte)PeekState.Away)
		{
			return false;
		}

		if (Tick != _walkTick)
		{
			_walkTick = Tick;
			WalkedTicks++;
		}

		return WalkedTicks > Traits.PeekWalkMaxTicks;
	}

	/// <summary>
	/// A walk to a hide spot that ran out of time: counted, and no pair for
	/// <see cref="UnitPlanTraits.PeekRetryTicks"/>, so the next plan is the order's (§3.4,
	/// P4). Set here rather than on the next scan so the replan this failure causes
	/// already sees it.
	/// </summary>
	private TaskStatus GiveUpPeek()
	{
		FailedPeeks++;
		PeekRetryTick = Tick + (uint)Traits.PeekRetryTicks;
		HasPeekPoint = false;
		PeekTargetOwnerId = OwnerId.None;
		Sense(UnitFact.PeekTarget, false);
		SensePeekState();
		return TaskStatus.Failure;
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

	/// <summary>
	/// Counts a tick of reacting to fire, once a tick however often the operator runs,
	/// under an order that goes somewhere; true, with the cap spent, once that has lasted
	/// <see cref="UnitPlanTraits.ReactMaxTicks"/>. Set here rather than on the next scan
	/// so the replan this failure causes already sees it, as a given-up resupply is.
	/// </summary>
	private bool Reacted()
	{
		if (!GoesSomewhere(Order.Kind))
		{
			return false;
		}

		if (Tick != _reactTick)
		{
			_reactTick = Tick;
			ReactedTicks++;
		}

		if (ReactedTicks < Traits.ReactMaxTicks)
		{
			return false;
		}

		_reactSpentTick = Tick;
		Sense(UnitFact.ReactSpent, true);
		return true;
	}

	/// <summary>
	/// Fire at where the fire came from, for <see cref="UnitPlanTraits.ReturnFireTicks"/>
	/// after each round that reaches it, when that is inside its engage range: a place,
	/// with no player named, so it is nobody's focus. A shooter further off is hidden
	/// from, not answered — the engine would not fire at it (<c>FireAtPlace</c>), and a
	/// unit standing up to fire at it would only be standing (docs/TACTICS.md §5.8). A
	/// target of its own it fires at anyway.
	/// </summary>
	private void FireBack()
	{
		if (Tick >= LastFiredUponTick && Tick - LastFiredUponTick <= (uint)Traits.ReturnFireTicks
			&& Flat(Position, FiredFromPoint) <= UnitTraits.EngageRangeMeters)
		{
			Intent.FireAt = true;
			Intent.FirePoint = FiredFromPoint;
		}
	}

	/// <summary>
	/// A resupply that ran out of time: counted, and the source put out of reach for
	/// <see cref="UnitPlanTraits.ResupplyRetryTicks"/> so that the next plan is the
	/// order's rather than the same trip again (§3.4, P4). Set here rather than on the
	/// next scan so the replan this failure causes already sees it.
	/// </summary>
	private TaskStatus GiveUpResupply()
	{
		FailedResupplies++;
		SupplyRetryTick = Tick + (uint)Traits.ResupplyRetryTicks;
		Sense(UnitFact.Supply, false);
		return TaskStatus.Failure;
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

	/// <summary>A condition on how the unit fights (docs/TACTICS.md §3): a builder, a vehicle or a truck has no tactics of its own.</summary>
	public UnitDomainBuilder IfHasDoctrine() =>
		Condition("Doctrine!=None", c => c.UnitTraits.Doctrine != UnitDoctrine.None);

	/// <summary>
	/// It has nothing of its own to fight from cover: no target, or an order that shoots
	/// on the walk (<see cref="UnitContext.FightsOnTheWay"/>).
	/// </summary>
	public UnitDomainBuilder IfNothingToFightFromCover() =>
		Condition("Contact!=Own or travelling", NothingToFightFromCover);

	/// <summary>Executing condition: as <see cref="IfNothingToFightFromCover"/>, every tick.</summary>
	public UnitDomainBuilder WhileNothingToFightFromCover() =>
		_executingConditions
			? ExecutingCondition("while Contact!=Own or travelling", NothingToFightFromCover)
			: this;

	private static bool NothingToFightFromCover(UnitContext c) =>
		c.Get(UnitFact.Contact) != (byte)UnitContact.Own || UnitContext.FightsOnTheWay(c.Order.Kind);

	/// <summary>
	/// The executing conditions every reaction to fire holds by (docs/TACTICS.md §5.1):
	/// the strategist still lets it, rounds are still coming, the spot still hides it,
	/// the cap is not spent, its squad is not falling back, its order is not one it
	/// never reacts under, it has nothing of its own to fight from cover, and it is not
	/// a marksman that has found a peek pair (§5.3).
	/// </summary>
	public UnitDomainBuilder WhileReacting() =>
		While(UnitFact.MicroAutonomy, MicroAutonomy.On).While(UnitFact.Peek, PeekState.None)
			.WhileNot(UnitFact.FiredUpon, FireKind.None)
			.WhileNot(UnitFact.FireCover, CoverState.None).While(UnitFact.ReactSpent, false)
			.WhileNot(UnitFact.SquadPhase, SquadPhase.FallingBack)
			.WhileNot(UnitFact.Order, OrderKind.Build).WhileNot(UnitFact.Order, OrderKind.Embark)
			.WhileNot(UnitFact.Order, OrderKind.Unload).WhileNothingToFightFromCover();

	/// <summary>
	/// The executing conditions a throw holds by (docs/TACTICS.md §5.4): the strategist
	/// still lets it, there is still a hidden enemy to throw at, it has no target of its
	/// own, its squad is not falling back, and its order is not one it never throws under.
	/// </summary>
	public UnitDomainBuilder WhileThrowing() =>
		While(UnitFact.MicroAutonomy, MicroAutonomy.On).While(UnitFact.Grenade, true)
			.WhileNot(UnitFact.Contact, UnitContact.Own).WhileNot(UnitFact.SquadPhase, SquadPhase.FallingBack)
			.WhileNot(UnitFact.Order, OrderKind.Build).WhileNot(UnitFact.Order, OrderKind.Embark)
			.WhileNot(UnitFact.Order, OrderKind.Unload);

	/// <summary>A marksman under an order it peeks under (docs/TACTICS.md §5.3, <see cref="UnitContext.PeeksUnder"/>).</summary>
	public UnitDomainBuilder IfMarksman() =>
		Condition("Doctrine=Marksman and Order∈{Attack,Defend,None} or Objective", IsMarksman);

	/// <summary>
	/// The executing conditions every marksman operator holds by (docs/TACTICS.md §5.3): the
	/// strategist still lets it, it still has a pair, and its order is still one it peeks under.
	/// </summary>
	public UnitDomainBuilder WhilePeeking() =>
		_executingConditions
			? While(UnitFact.MicroAutonomy, MicroAutonomy.On).WhileNot(UnitFact.Peek, PeekState.None)
				.ExecutingCondition("while Doctrine=Marksman and Order∈{Attack,Defend,None} or Objective", IsMarksman)
			: this;

	/// <summary>Its pair is burned, or a threat is too near: it moves on.</summary>
	public UnitDomainBuilder IfDisplaced() => Condition("Burned or Standoff", Displaced);

	/// <summary>Executing condition: as <see cref="IfDisplaced"/>, every tick.</summary>
	public UnitDomainBuilder WhileDisplaced() =>
		_executingConditions ? ExecutingCondition("while Burned or Standoff", Displaced) : this;

	private static bool IsMarksman(UnitContext c) =>
		c.UnitTraits.Doctrine == UnitDoctrine.Marksman
		&& UnitContext.PeeksUnder((OrderKind)c.Get(UnitFact.Order), c.Is(UnitFact.Objective));

	private static bool Displaced(UnitContext c) => c.Is(UnitFact.Burned) || c.Is(UnitFact.Standoff);

	/// <summary>A heavy gunner under an order it suppresses under (docs/TACTICS.md §5.2, <see cref="UnitContext.SuppressesUnder"/>).</summary>
	public UnitDomainBuilder IfGunner() =>
		Condition("Doctrine=Gunner and Order∈{Attack,Defend,None} or Objective", IsGunner);

	/// <summary>
	/// The executing conditions both of a heavy gunner's operators hold by (docs/TACTICS.md
	/// §5.2): the strategist still lets it, its order is still one it suppresses under, it
	/// has no target of its own, and its squad is not falling back.
	/// </summary>
	public UnitDomainBuilder WhileGunning() =>
		_executingConditions
			? While(UnitFact.MicroAutonomy, MicroAutonomy.On)
				.ExecutingCondition("while Doctrine=Gunner and Order∈{Attack,Defend,None} or Objective", IsGunner)
				.WhileNot(UnitFact.Contact, UnitContact.Own).WhileNot(UnitFact.SquadPhase, SquadPhase.FallingBack)
			: this;

	private static bool IsGunner(UnitContext c) =>
		c.UnitTraits.Doctrine == UnitDoctrine.Gunner
		&& UnitContext.SuppressesUnder((OrderKind)c.Get(UnitFact.Order), c.Is(UnitFact.Objective));

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
/// planner chooses how to carry it out and never whether — and the two things a unit
/// does on its own, retreating and resupplying, it does only for a computer
/// strategist's order (D1).
///
/// Selectors are in priority order; a higher method pre-empts a running plan when a
/// fact changes (P2) and a lower one never does (P3), so every operator has an
/// executing condition for its premise, and every one for its order: a new order is
/// usually a lower branch, which could not replace the old one otherwise.
///
/// Resupply (H5) is second: a wounded unit with nothing in sight goes to the nearest
/// supply source in range — a barracks' ring or a supply truck (D2) — and stands in
/// it until it is healed. Nothing below it replaces it, so it ends only on its
/// executing conditions: healed, a target of its own, the source gone, a new order.
///
/// Neither retreating nor resupplying takes a unit off a move, a build, a walk to a
/// vehicle or an unload (docs/VEHICLES.md §9.2). A unit riding in a vehicle is never
/// planned at all.
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
					.IfNot(UnitFact.Order, OrderKind.Embark)
					.IfNot(UnitFact.Order, OrderKind.Unload)
					.Act(UnitGoal.Retreat).While(UnitFact.Odds, OddsBand.Outnumbered)
						.While(UnitFact.Stance, UnitStance.Autonomous)
						.WhileNot(UnitFact.Order, OrderKind.Move).WhileNot(UnitFact.Order, OrderKind.Build)
						.WhileNot(UnitFact.Order, OrderKind.Embark).WhileNot(UnitFact.Order, OrderKind.Unload).End()
				.End()
			.End()
			// A grenade at a hidden enemy (docs/TACTICS.md §5.4): only with the strategist's
			// leave, below Retreat, and above reacting to fire, as a throw is one tick's
			// standing still — a unit running for cover throws on the way.
			.Select("grenade")
				.Sequence("throw at a hidden enemy")
					.If(UnitFact.MicroAutonomy, MicroAutonomy.On)
					.IfHasDoctrine()
					.If(UnitFact.Grenade, true)
					.IfNot(UnitFact.Contact, UnitContact.Own)
					.IfNot(UnitFact.SquadPhase, SquadPhase.FallingBack)
					.IfNot(UnitFact.Order, OrderKind.Build)
					.IfNot(UnitFact.Order, OrderKind.Embark)
					.IfNot(UnitFact.Order, OrderKind.Unload)
					.Act(UnitGoal.ThrowGrenade).WhileThrowing().End()
				.End()
			.End()
			// Reacting to fire (docs/TACTICS.md §5.1): only with the strategist's leave
			// (micro-autonomy), and below Retreat, which runs rather than hides. Not a
			// marksman with a peek pair: its hide spot is concealed from where the fire came
			// from already, and it answers fire by moving on (§5.3).
			.Select("react to fire")
				.If(UnitFact.MicroAutonomy, MicroAutonomy.On)
				.IfHasDoctrine()
				.If(UnitFact.Peek, PeekState.None)
				.IfNot(UnitFact.FiredUpon, FireKind.None)
				.IfNot(UnitFact.FireCover, CoverState.None)
				.If(UnitFact.ReactSpent, false)
				.IfNot(UnitFact.SquadPhase, SquadPhase.FallingBack)
				.IfNot(UnitFact.Order, OrderKind.Build)
				.IfNot(UnitFact.Order, OrderKind.Embark)
				.IfNot(UnitFact.Order, OrderKind.Unload)
				.IfNothingToFightFromCover()
				.Sequence("return fire from cover")
					.If(UnitFact.FireCover, CoverState.In)
					.Act(UnitGoal.ReturnFire).WhileReacting().While(UnitFact.FireCover, CoverState.In).End()
				.End()
				.Sequence("get to cover")
					.Act(UnitGoal.TakeCoverFromFire).WhileReacting().End()
				.End()
			.End()
			.Select("resupply")
				.Sequence("resupply")
					.If(UnitFact.Stance, UnitStance.Autonomous)
					.If(UnitFact.Wounded)
					.If(UnitFact.Supply)
					.If(UnitFact.Contact, UnitContact.None)
					.If(UnitFact.Supplied, false)
					.IfNot(UnitFact.Order, OrderKind.Move)
					.IfNot(UnitFact.Order, OrderKind.Build)
					.IfNot(UnitFact.Order, OrderKind.Embark)
					.IfNot(UnitFact.Order, OrderKind.Unload)
					.Act(UnitGoal.Resupply).While(UnitFact.Wounded).While(UnitFact.Supply)
						.WhileNot(UnitFact.Contact, UnitContact.Own).While(UnitFact.Stance, UnitStance.Autonomous)
						.WhileNot(UnitFact.Order, OrderKind.Move).WhileNot(UnitFact.Order, OrderKind.Build)
						.WhileNot(UnitFact.Order, OrderKind.Embark).WhileNot(UnitFact.Order, OrderKind.Unload).End()
				.End()
			.End()
			.Select("build")
				.If(UnitFact.Order, OrderKind.Build)
				.Sequence("hide behind cover")
					.If(UnitFact.UnderFire)
					.IfNot(UnitFact.Cover, CoverState.None)
					.Act(UnitGoal.TakeCover).While(UnitFact.UnderFire).WhileNot(UnitFact.Cover, CoverState.None)
						.While(UnitFact.Order, OrderKind.Build).End()
				.End()
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
			// A marksman (docs/TACTICS.md §5.3): only with the strategist's leave, and only with
			// a pair the scan found — without one it falls through to its order, as a rifleman.
			// Moving on outranks peeking, and peeking outranks waiting in the hide spot, which
			// it does until it has been hidden long enough and has somebody to shoot.
			.Select("marksman")
				.If(UnitFact.MicroAutonomy, MicroAutonomy.On)
				.IfMarksman()
				.IfNot(UnitFact.Peek, PeekState.None)
				.Sequence("displace")
					.IfDisplaced()
					.Act(UnitGoal.Relocate).WhilePeeking().WhileDisplaced().End()
				.End()
				.Sequence("engage")
					.If(UnitFact.Peek, PeekState.Ready)
					.If(UnitFact.PeekTarget)
					.Act(UnitGoal.PopOut).WhilePeeking().While(UnitFact.PeekTarget).End()
					.Act(UnitGoal.FireFromPeek).WhilePeeking().While(UnitFact.PeekTarget).End()
					.Act(UnitGoal.Duck).WhilePeeking().End()
				.End()
				.Sequence("hide")
					.Act(UnitGoal.ToHide).WhilePeeking().End()
				.End()
			.End()
			// A heavy gunner (docs/TACTICS.md §5.2): only with the strategist's leave, and only
			// with nothing of its own to shoot — a target it sees it fights as before. Into its
			// reserve it fills its belt; else it suppresses the places the side knows of. With
			// neither it falls through to its order.
			.Select("gunner")
				.If(UnitFact.MicroAutonomy, MicroAutonomy.On)
				.IfGunner()
				.IfNot(UnitFact.Contact, UnitContact.Own)
				.IfNot(UnitFact.SquadPhase, SquadPhase.FallingBack)
				.Sequence("reload")
					.If(UnitFact.BeltLow)
					.Act(UnitGoal.Reload).WhileGunning().While(UnitFact.BeltLow).End()
				.End()
				.Sequence("suppress")
					.If(UnitFact.Suppress)
					// Into its reserve, "reload" above takes over on the tick (P2).
					.Act(UnitGoal.SuppressPoint).WhileGunning().While(UnitFact.Suppress).End()
				.End()
			.End()
			.Select("attack")
				.If(UnitFact.Order, OrderKind.Attack)
				.Sequence("wait for squad")
					.If(UnitFact.SquadPhase, SquadPhase.Gathering)
					.Act(UnitGoal.WaitForSquad).While(UnitFact.SquadPhase, SquadPhase.Gathering)
						.While(UnitFact.Order, OrderKind.Attack).End()
				.End()
				// An attack on a control point takes it: onto it, from cover there when there
				// is any, and without chasing what it sees off it — above the fights below.
				.Sequence("hold the point from cover")
					.If(UnitFact.Objective)
					.IfNot(UnitFact.Cover, CoverState.None)
					.Act(UnitGoal.HoldPointCover).While(UnitFact.Objective).WhileNot(UnitFact.Cover, CoverState.None)
						.While(UnitFact.Order, OrderKind.Attack).End()
				.End()
				.Sequence("hold the point")
					.If(UnitFact.Objective)
					.If(UnitFact.OnObjective)
					.Act(UnitGoal.HoldPoint).While(UnitFact.Objective).While(UnitFact.OnObjective)
						.While(UnitFact.Order, OrderKind.Attack).End()
				.End()
				.Sequence("take the point")
					.If(UnitFact.Objective)
					.Act(UnitGoal.TakePoint).While(UnitFact.Objective).While(UnitFact.Order, OrderKind.Attack).End()
				.End()
				.Sequence("engage from cover")
					.If(UnitFact.Contact, UnitContact.Own)
					.IfNot(UnitFact.Cover, CoverState.None)
					.Act(UnitGoal.HoldCover).While(UnitFact.Contact, UnitContact.Own)
						.WhileNot(UnitFact.Cover, CoverState.None).While(UnitFact.Order, OrderKind.Attack).End()
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
				.Sequence("engage from cover")
					.If(UnitFact.Contact, UnitContact.Own)
					.If(UnitFact.InLeash)
					.IfNot(UnitFact.Cover, CoverState.None)
					.Act(UnitGoal.HoldCover).While(UnitFact.Contact, UnitContact.Own).While(UnitFact.InLeash)
						.WhileNot(UnitFact.Cover, CoverState.None)
						.WhileAny(UnitFact.Order, OrderKind.Defend, OrderKind.None).End()
				.End()
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
			// Walking to a vehicle to get in, and a vehicle letting its riders out
			// (docs/VEHICLES.md §9.2): carried out as given, shooting on the way as a move
			// does. Without a branch of their own they would fall back to the order all the
			// same, but by a failed plan every tick.
			.Select("carry")
				.IfAny(UnitFact.Order, OrderKind.Embark, OrderKind.Unload)
				.Sequence("carry")
					.Act(UnitGoal.Obey).WhileAny(UnitFact.Order, OrderKind.Embark, OrderKind.Unload).End()
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
		Map = HtnDomainMap.Build(HtnDomainKind.Unit, Domain);
	}

	public IFactory Factory { get; }

	public UnitPlanTraits Traits { get; }

	public Domain<UnitContext> Domain { get; }

	/// <summary>The domain numbered for the debugger (docs/AI_DEBUG.md §3.1): what a task index on the wire means.</summary>
	public HtnDomainMap Map { get; }

	public Planner<UnitContext> Planner { get; } = new();

	public UnitContext CreateContext(in UnitTraits unitTraits)
	{
		var context = new UnitContext(Factory, Traits, unitTraits);
		context.Init();
		context.History = new HtnHistory();
		context.History.Attach(context.PlannerState, Map, () => context.Tick);
		return context;
	}

	/// <summary>One tick: replans if a fact changed or the plan ended, then runs the current operator.</summary>
	public void Tick(UnitContext context) => Planner.Tick(Domain, context);

	/// <summary>Drops the plan, stopping its operator: for a new order, or a unit that has died.</summary>
	public void Reset(UnitContext context)
	{
		Planner.Reset(context);
		context.ResetReact();
		context.ResetSuppress();
		context.History?.RecordReset();
	}
}
