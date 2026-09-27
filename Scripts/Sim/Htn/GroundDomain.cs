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
/// The ground bot's facts (docs/HTN_BOTS.md §5.1): one byte each, as FluidHTN stores
/// its world state. Only a change here makes the planner look again (§3.7), so
/// every one is a band, not a measurement.
/// </summary>
public enum GroundFact : byte
{
	/// <summary><see cref="ContactLevel"/>.</summary>
	Contact,

	/// <summary><see cref="Threat"/>.</summary>
	ThreatKind,

	/// <summary><see cref="Arms"/>.</summary>
	Armed,

	/// <summary><see cref="HealthBand"/>.</summary>
	Health,

	/// <summary><see cref="OddsBand"/>.</summary>
	Odds,

	/// <summary>A teammate within <see cref="GroundPlanTraits.AlliesNearMeters"/>.</summary>
	AlliesNear,

	/// <summary><see cref="GroundRole"/>, from the coordinator.</summary>
	Role,

	/// <summary>Inside the capture radius of the node the coordinator assigned.</summary>
	AtZone,

	/// <summary>Inside an enemy barracks' defended ring.</summary>
	InsideDefences,

	/// <summary>Half a magazine or less, and not reloading.</summary>
	MagazineLow,

	/// <summary>The coordinator has given this bot a stale node to look at (Recon).</summary>
	Sweep,

	Count,
}

/// <summary>What this bot is in contact with.</summary>
public enum ContactLevel : byte
{
	None = 0,

	/// <summary>The team remembers one within <see cref="GroundPlanTraits.InvestigateRadiusMeters"/>, and this bot does not see it.</summary>
	Ghost = 1,

	/// <summary>This bot's own scan found a hostile unit or armed structure.</summary>
	Visible = 2,
}

/// <summary>What the contact is, as far as a weapon cares.</summary>
public enum Threat : byte
{
	/// <summary>Something bullets hurt is among it.</summary>
	Infantry = 0,

	/// <summary>All of it is bullet-proof: a tank, a pillbox, a tower (docs/NETCODE.md §10.6).</summary>
	Armour = 1,
}

public enum Arms : byte
{
	SmallArms = 0,

	/// <summary>A launcher in the large slot.</summary>
	Explosive = 1,
}

public enum HealthBand : byte
{
	/// <summary>At least <see cref="GroundPlanTraits.HealthOkFraction"/>.</summary>
	Ok = 0,

	Hurt = 1,

	/// <summary>Below <see cref="GroundPlanTraits.HealthCriticalFraction"/>.</summary>
	Critical = 2,
}

/// <summary>What the coordinator wants this bot for (§5.1). Humans are never given one.</summary>
public enum GroundRole : byte
{
	Assault = 0,

	/// <summary>Stand on the assigned node, which stops it paying the strategist (M5).</summary>
	Denier = 1,

	/// <summary>Fetch a launcher from the weapon locker: armour is known and too few explosives are in hand.</summary>
	LockerRunner = 2,
}

/// <summary>What a primitive task asks the pilot to do: the operator's name, and the intent's.</summary>
public enum GroundGoal : byte
{
	None = 0,
	LeaveDefences,
	FallBack,
	Reload,
	PathToLocker,
	UseLocker,
	EngageArmour,
	TakeFocusTarget,
	AdvanceWithBuddy,
	Engage,
	InvestigateGhost,
	TakeNode,
	HoldNode,
	SweepZone,
	Standoff,
}

/// <summary>How the pilot picks where to walk for an intent.</summary>
public enum GroundMove : byte
{
	/// <summary>Nowhere: stand and reload.</summary>
	Stay = 0,

	/// <summary>At the target when there is one, else to <see cref="GroundIntent.Point"/> when there is one.</summary>
	Target,

	/// <summary>To <see cref="GroundIntent.Point"/>, fighting as the brain does on the way: it strafes a target inside its preferred range.</summary>
	Point,

	/// <summary>To <see cref="GroundIntent.Point"/>, and it does not stop to strafe: it shoots on the move.</summary>
	Run,

	/// <summary>Today's behaviour: at the target, else the pilot's own standoff point by the enemy barracks.</summary>
	Standoff,
}

/// <summary>
/// What the running primitive task wants from the pilot (docs/HTN_BOTS.md §4.2 rule 1):
/// where to go, how, what to prefer shooting, and whether to tap the use key.
/// The engine half — <c>BotPilot</c> — carries it out; nothing here calls the engine.
/// </summary>
public struct GroundIntent
{
	public GroundGoal Goal;

	public GroundMove Move;

	public bool HasPoint;

	public Vector3 Point;

	/// <summary>Shoot this one when the scan can see it and the weapon can hurt it; 0 for the nearest.</summary>
	public int TargetOwnerId;

	/// <summary>Tap the use key while the weapon in hand is not an explosive: a weapon locker cycles it.</summary>
	public bool UseLocker;
}

/// <summary>
/// The thresholds the ground planner bands its facts at and gives up at
/// (docs/HTN_BOTS.md §9, "bots become too good": every one is a knob).
/// </summary>
public readonly struct GroundPlanTraits
{
	public readonly float HealthOkFraction;
	public readonly float HealthCriticalFraction;

	/// <summary>Added to a band's edge once the band is entered, so health sitting on an edge does not replan every scan.</summary>
	public readonly float HealthHysteresis;

	public readonly float AlliesNearMeters;

	/// <summary>How far a teammate may drift before "allies near" is dropped once it is held.</summary>
	public readonly float AlliesNearHoldMeters;

	/// <summary>How far away a remembered contact is still this bot's business.</summary>
	public readonly float InvestigateRadiusMeters;

	/// <summary>
	/// How close to the locker counts as at it: inside <see cref="SimConfig.LockerReachMeters"/>, and
	/// outside the 1.5 m a bot's navigation agent calls arrived, so a bot that has stopped has got there.
	/// </summary>
	public readonly float LockerArriveMeters;

	/// <summary>How long a walk to the locker may take before the trip is given up.</summary>
	public readonly int PathToLockerTimeoutTicks;

	/// <summary>How long a bot at the locker may tap before the trip is given up (§3.4, P4: the give-up fact).</summary>
	public readonly int UseLockerTimeoutTicks;

	/// <summary>Ticks between use taps: long enough that the swap has landed before the next one is decided.</summary>
	public readonly int UseTapIntervalTicks;

	/// <summary>How far the buddy may be before advancing with it means walking to it first.</summary>
	public readonly float BuddyLeashMeters;

	public GroundPlanTraits(float healthOkFraction, float healthCriticalFraction, float healthHysteresis,
		float alliesNearMeters, float alliesNearHoldMeters, float investigateRadiusMeters, float lockerArriveMeters,
		int pathToLockerTimeoutTicks, int useLockerTimeoutTicks, int useTapIntervalTicks, float buddyLeashMeters)
	{
		HealthOkFraction = healthOkFraction;
		HealthCriticalFraction = healthCriticalFraction;
		HealthHysteresis = MathF.Max(healthHysteresis, 0f);
		AlliesNearMeters = MathF.Max(alliesNearMeters, 0f);
		AlliesNearHoldMeters = MathF.Max(alliesNearHoldMeters, AlliesNearMeters);
		InvestigateRadiusMeters = MathF.Max(investigateRadiusMeters, 0f);
		LockerArriveMeters = MathF.Max(lockerArriveMeters, 0f);
		PathToLockerTimeoutTicks = Math.Max(pathToLockerTimeoutTicks, 1);
		UseLockerTimeoutTicks = Math.Max(useLockerTimeoutTicks, 1);
		UseTapIntervalTicks = Math.Max(useTapIntervalTicks, 2);
		BuddyLeashMeters = MathF.Max(buddyLeashMeters, 0f);
	}

	/// <summary>
	/// §5.1's numbers. The locker gets two seconds of tapping — a DMR needs two swaps
	/// to reach the launcher (rifle → launcher → DMR → rifle), a quarter-second apart —
	/// and thirty seconds of walking, the length of the map at a jog.
	/// </summary>
	public static GroundPlanTraits Default => new(
		healthOkFraction: 0.6f,
		healthCriticalFraction: 0.3f,
		healthHysteresis: 0.05f,
		alliesNearMeters: 15f,
		alliesNearHoldMeters: 18f,
		investigateRadiusMeters: 100f,
		lockerArriveMeters: 2f,
		pathToLockerTimeoutTicks: SimConfig.TickRate * 30,
		useLockerTimeoutTicks: SimConfig.TickRate * 2,
		useTapIntervalTicks: SimConfig.TickRate / 4,
		buddyLeashMeters: 12f);
}

/// <summary>
/// One scan's worth of what a ground bot knows, before it is banded into facts.
/// Filled by the pilot out of its own <c>GroundSensor</c> scan, the team's
/// <see cref="ContactMemory"/>, a <see cref="Neighbourhood"/> survey and its own body.
/// </summary>
public struct GroundSense
{
	/// <summary>Hostile units and armed structures this bot's own scan found.</summary>
	public int VisibleHostiles;

	/// <summary>Of those, the ones bullets hurt.</summary>
	public int VisibleSoft;

	/// <summary>
	/// The team remembers a contact within <see cref="GroundPlanTraits.InvestigateRadiusMeters"/>
	/// this bot's scan did not find. Only read when nothing is visible.
	/// </summary>
	public bool Remembered;

	/// <summary>Of what the team remembers there, something bullets hurt.</summary>
	public bool RememberedSoft;

	public bool Explosive;

	public float HealthFraction;

	public OddsBand Odds;

	/// <summary><see cref="float.MaxValue"/> with nobody in range.</summary>
	public float NearestFriendMeters;

	public bool AtZone;

	public bool InsideDefences;

	public bool MagazineLow;
}

/// <summary>
/// One ground bot's planning context: its facts, the places and ids its operators
/// steer by, and the intent the running operator last wrote (docs/HTN_BOTS.md §4.2).
///
/// The pilot writes the knowledge — facts on its ten-tick scan, position every tick,
/// assignments when the coordinator hands them out — and reads <see cref="Intent"/>.
/// Operators are functions of this context and nothing else, so what a bot does in
/// a situation is a question for <c>dotnet test</c>.
/// </summary>
public sealed class GroundContext : BaseContext
{
	private readonly byte[] _facts = new byte[(int)GroundFact.Count];

	public GroundContext(IFactory factory, in GroundPlanTraits traits)
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

	public GroundPlanTraits Traits { get; }

	/// <summary>What the running operator asked for. <see cref="GroundGoal.None"/> between tasks.</summary>
	public GroundIntent Intent;

	// ---- knowledge, written by the pilot -----------------------------------

	public uint Tick;

	/// <summary>Where the bot's feet are, every tick.</summary>
	public Vector3 Position;

	/// <summary>What is in its hands now, every tick. <see cref="GroundFact.Armed"/> is the same thing as a fact.</summary>
	public bool Explosive;

	/// <summary>The edge of the enemy barracks' ring straight out from the bot.</summary>
	public Vector3 ExitPoint;

	/// <summary>The nearest teammates' centroid, else the spawn.</summary>
	public Vector3 FallbackPoint;

	public bool HasLocker;
	public Vector3 LockerPoint;

	/// <summary>The remembered contact to walk to: the nearest one worth investigating.</summary>
	public Vector3 GhostPoint;

	/// <summary>The nearest known bullet-proof contact, for a launcher with nothing in sight.</summary>
	public bool HasArmourPoint;
	public Vector3 ArmourPoint;

	// ---- assignments, written by the coordinator ---------------------------

	/// <summary>The node this bot denies: where it stands on it. Only with <see cref="GroundRole.Denier"/>.</summary>
	public bool HasZone;
	public Vector3 ZonePoint;

	/// <summary>The stale node to look at. Only while <see cref="GroundFact.Sweep"/> holds.</summary>
	public Vector3 SweepPoint;

	/// <summary>The coordinator's focus target for this bot's contact cluster; 0 for none.</summary>
	public int FocusOwnerId;

	public bool HasBuddy;
	public Vector3 BuddyPoint;

	/// <summary>Trips to the locker given up on, for the coordinator's give-up rule (§3.4, P4).</summary>
	public int FailedLockerTrips;

	/// <summary>The tick the running operator started on.</summary>
	public uint GoalStartTick;

	public byte Get(GroundFact fact) => GetState((int)fact);

	public bool Is(GroundFact fact) => GetState((int)fact) != 0;

	/// <summary>What a sensor writes. Marks the context dirty only when the value changes.</summary>
	public void Sense(GroundFact fact, byte value) => SetState((int)fact, value, true, EffectType.Permanent);

	public void Sense(GroundFact fact, bool value) => Sense(fact, value ? (byte)1 : (byte)0);

	public void SetRole(GroundRole role) => Sense(GroundFact.Role, (byte)role);

	public GroundRole Role => (GroundRole)Get(GroundFact.Role);

	/// <summary>
	/// Bands one scan into facts (§5.1's table), each against the band it was in
	/// last time where there is hysteresis to apply.
	/// </summary>
	public void Encode(in GroundSense sense)
	{
		ContactLevel contact = sense.VisibleHostiles > 0 ? ContactLevel.Visible
			: sense.Remembered ? ContactLevel.Ghost
			: ContactLevel.None;
		Sense(GroundFact.Contact, (byte)contact);

		// Armour only when nothing in the contact is soft: a rifleman facing a tank and
		// the infantry beside it has a fight it can have.
		bool soft = contact == ContactLevel.Visible ? sense.VisibleSoft > 0 : sense.RememberedSoft;
		Sense(GroundFact.ThreatKind, (byte)(contact != ContactLevel.None && !soft ? Threat.Armour : Threat.Infantry));

		Sense(GroundFact.Armed, (byte)(sense.Explosive ? Arms.Explosive : Arms.SmallArms));
		Sense(GroundFact.Health, (byte)Band(sense.HealthFraction, (HealthBand)Get(GroundFact.Health), Traits));
		Sense(GroundFact.Odds, (byte)sense.Odds);

		float allies = Is(GroundFact.AlliesNear) ? Traits.AlliesNearHoldMeters : Traits.AlliesNearMeters;
		Sense(GroundFact.AlliesNear, sense.NearestFriendMeters <= allies);

		Sense(GroundFact.AtZone, sense.AtZone && HasZone);
		Sense(GroundFact.InsideDefences, sense.InsideDefences);
		Sense(GroundFact.MagazineLow, sense.MagazineLow);
	}

	/// <summary>The health band for a fraction, held past its edge by <see cref="GroundPlanTraits.HealthHysteresis"/> once entered.</summary>
	public static HealthBand Band(float fraction, HealthBand previous, in GroundPlanTraits traits)
	{
		float critical = traits.HealthCriticalFraction + (previous == HealthBand.Critical ? traits.HealthHysteresis : 0f);
		if (fraction < critical)
		{
			return HealthBand.Critical;
		}

		float ok = traits.HealthOkFraction + (previous == HealthBand.Ok ? -traits.HealthHysteresis : 0f);
		return fraction >= ok ? HealthBand.Ok : HealthBand.Hurt;
	}

	// ---- operators ---------------------------------------------------------

	/// <summary>Starts an operator: the intent is new, and so is its clock.</summary>
	public TaskStatus Begin(GroundGoal goal)
	{
		GoalStartTick = Tick;
		return Perform(goal);
	}

	/// <summary>Ends one, whether it was stopped, aborted or replaced: the pilot is asked for nothing.</summary>
	public void End() => Intent = default;

	/// <summary>
	/// One tick of an operator. Writes the intent and reports whether the goal has
	/// finished. Idempotent — FluidHTN can run an operator twice in one tick (#21,
	/// §3.2) — because all it does is write the same intent from the same context.
	/// </summary>
	public TaskStatus Perform(GroundGoal goal)
	{
		Intent = new GroundIntent { Goal = goal };
		uint running = Tick >= GoalStartTick ? Tick - GoalStartTick : 0u;

		switch (goal)
		{
			case GroundGoal.LeaveDefences:
				return Walk(GroundMove.Run, ExitPoint);

			case GroundGoal.FallBack:
				return Walk(GroundMove.Run, FallbackPoint);

			case GroundGoal.Reload:
				Intent.Move = GroundMove.Stay;
				return TaskStatus.Continue;

			case GroundGoal.PathToLocker:
				if (!HasLocker || running > (uint)Traits.PathToLockerTimeoutTicks)
				{
					return GiveUpLockerTrip();
				}

				Walk(GroundMove.Run, LockerPoint);
				return AtLocker ? TaskStatus.Success : TaskStatus.Continue;

			case GroundGoal.UseLocker:
				if (!HasLocker)
				{
					return GiveUpLockerTrip();
				}

				Walk(GroundMove.Point, LockerPoint);
				if (Explosive)
				{
					// What the swap handed over is the scan's to say, but the hands are
					// the bot's own: the fact is set here so the next task's condition
					// sees it this tick.
					Sense(GroundFact.Armed, (byte)Arms.Explosive);
					return TaskStatus.Success;
				}

				if (running > (uint)Traits.UseLockerTimeoutTicks)
				{
					return GiveUpLockerTrip();
				}

				Intent.UseLocker = true;
				return TaskStatus.Continue;

			case GroundGoal.EngageArmour:
				Intent.Move = GroundMove.Target;
				Intent.HasPoint = HasArmourPoint;
				Intent.Point = ArmourPoint;
				return TaskStatus.Continue;

			case GroundGoal.TakeFocusTarget:
				// One tick: it names the target, and AdvanceWithBuddy fights it.
				Intent.Move = GroundMove.Target;
				Intent.TargetOwnerId = FocusOwnerId;
				return TaskStatus.Success;

			case GroundGoal.AdvanceWithBuddy:
				Intent.TargetOwnerId = FocusOwnerId;
				if (HasBuddy && Flat(Position, BuddyPoint) > Traits.BuddyLeashMeters)
				{
					return Walk(GroundMove.Point, BuddyPoint);
				}

				Intent.Move = GroundMove.Target;
				return TaskStatus.Continue;

			case GroundGoal.Engage:
				Intent.Move = GroundMove.Target;
				Intent.TargetOwnerId = FocusOwnerId;
				return TaskStatus.Continue;

			case GroundGoal.InvestigateGhost:
				Intent.Move = GroundMove.Target;
				Intent.HasPoint = true;
				Intent.Point = GhostPoint;
				return TaskStatus.Continue;

			case GroundGoal.TakeNode:
				if (!HasZone)
				{
					return TaskStatus.Failure;
				}

				Walk(GroundMove.Run, ZonePoint);
				return Is(GroundFact.AtZone) ? TaskStatus.Success : TaskStatus.Continue;

			case GroundGoal.HoldNode:
				if (!HasZone)
				{
					return TaskStatus.Failure;
				}

				return Walk(GroundMove.Point, ZonePoint);

			case GroundGoal.SweepZone:
				return Walk(GroundMove.Point, SweepPoint);

			case GroundGoal.Standoff:
				Intent.Move = GroundMove.Standoff;
				return TaskStatus.Continue;

			default:
				return TaskStatus.Failure;
		}
	}

	private bool AtLocker => Flat(Position, LockerPoint) <= Traits.LockerArriveMeters;

	private TaskStatus Walk(GroundMove move, Vector3 point)
	{
		Intent.Move = move;
		Intent.HasPoint = true;
		Intent.Point = point;
		return TaskStatus.Continue;
	}

	private TaskStatus GiveUpLockerTrip()
	{
		FailedLockerTrips++;
		return TaskStatus.Failure;
	}

	private static float Flat(Vector3 a, Vector3 b)
	{
		Vector3 d = a - b;
		d.Y = 0f;
		return d.Length();
	}
}

/// <summary>
/// FluidHTN's builder, extended with the vocabulary the domain is written in
/// (FluidHTN README, "Extending the Domain Builder"; docs/HTN_BOTS.md §3.3). Every
/// lambda is made once, when the domain is built.
/// </summary>
public sealed class GroundDomainBuilder : BaseDomainBuilder<GroundDomainBuilder, GroundContext>
{
	private readonly bool _executingConditions;

	/// <param name="executingConditions">
	/// False leaves every <see cref="While"/> out, which is how a test shows what an
	/// operator without one does after its premise goes (§3.4, P3).
	/// </param>
	public GroundDomainBuilder(string name, IFactory factory, bool executingConditions = true)
		: base(name, factory) => _executingConditions = executingConditions;

	public GroundDomainBuilder If<TValue>(GroundFact fact, TValue value) where TValue : struct, Enum
	{
		byte wanted = Convert.ToByte(value);
		return Condition($"{fact}={value}", c => c.Get(fact) == wanted);
	}

	public GroundDomainBuilder IfNot<TValue>(GroundFact fact, TValue value) where TValue : struct, Enum
	{
		byte unwanted = Convert.ToByte(value);
		return Condition($"{fact}!={value}", c => c.Get(fact) != unwanted);
	}

	public GroundDomainBuilder If(GroundFact fact, bool value = true)
	{
		byte wanted = value ? (byte)1 : (byte)0;
		return Condition($"{fact}={value}", c => c.Get(fact) == wanted);
	}

	/// <summary>
	/// Executing condition: checked before every operator update; the task fails, and
	/// the planner replans, the tick it stops holding (§3.4, P3).
	/// </summary>
	public GroundDomainBuilder While<TValue>(GroundFact fact, TValue value) where TValue : struct, Enum
	{
		if (!_executingConditions)
		{
			return this;
		}

		byte wanted = Convert.ToByte(value);
		return ExecutingCondition($"while {fact}={value}", c => c.Get(fact) == wanted);
	}

	public GroundDomainBuilder WhileNot<TValue>(GroundFact fact, TValue value) where TValue : struct, Enum
	{
		if (!_executingConditions)
		{
			return this;
		}

		byte unwanted = Convert.ToByte(value);
		return ExecutingCondition($"while {fact}!={value}", c => c.Get(fact) != unwanted);
	}

	public GroundDomainBuilder While(GroundFact fact, bool value = true)
	{
		if (!_executingConditions)
		{
			return this;
		}

		byte wanted = value ? (byte)1 : (byte)0;
		return ExecutingCondition($"while {fact}={value}", c => c.Get(fact) == wanted);
	}

	/// <summary>
	/// A primitive task whose operator asks the pilot for <paramref name="goal"/>.
	/// Set directly rather than through <c>Do</c>, which takes no abort callback: an
	/// operator ended by a failed executing condition must clear its intent too
	/// (§3.7).
	/// </summary>
	public GroundDomainBuilder Act(GroundGoal goal)
	{
		Action(goal.ToString());
		if (Pointer is IPrimitiveTask task)
		{
			task.SetOperator(new FuncOperator<GroundContext>(
				c => c.Perform(goal),
				start: c => c.Begin(goal),
				funcStop: c => c.End(),
				funcAborted: c => c.End()));
		}

		return this;
	}

	/// <summary>A predicted outcome: true while planning, forgotten before execution; a scan has to make it true.</summary>
	public GroundDomainBuilder Predict<TValue>(GroundFact fact, TValue value) where TValue : struct, Enum
	{
		byte predicted = Convert.ToByte(value);
		return Effect($"{fact}:={value}", EffectType.PlanOnly, (c, type) => c.SetState((int)fact, predicted, false, type));
	}

	public GroundDomainBuilder Predict(GroundFact fact, bool value)
	{
		byte predicted = value ? (byte)1 : (byte)0;
		return Effect($"{fact}:={value}", EffectType.PlanOnly, (c, type) => c.SetState((int)fact, predicted, false, type));
	}
}

/// <summary>
/// docs/HTN_BOTS.md §5.1 as FluidHTN code: the ground bot's domain, built once and
/// shared by every ground bot (§3.4, P6). Selectors are in priority order; the first
/// method whose conditions hold wins, and a higher one pre-empts a running plan when
/// a fact changes (P2) while a lower one never does (P3) — so every operator that
/// runs until something changes has an executing condition for its premise.
/// </summary>
public static class GroundDomain
{
	public static Domain<GroundContext> Build(IFactory factory, bool executingConditions = true) =>
		new GroundDomainBuilder("ground", factory, executingConditions)
			.Select("retreat")
				.Sequence("leave defences")
					.If(GroundFact.InsideDefences)
					.Act(GroundGoal.LeaveDefences).While(GroundFact.InsideDefences).End()
				.End()
				.Sequence("fall back to allies")
					.If(GroundFact.Health, HealthBand.Critical)
					.If(GroundFact.Odds, OddsBand.Outnumbered)
					.Act(GroundGoal.FallBack).While(GroundFact.Odds, OddsBand.Outnumbered).End()
				.End()
			.End()
			.Select("resupply")
				.Sequence("rearm for armour")
					.If(GroundFact.Role, GroundRole.LockerRunner)
					.If(GroundFact.ThreatKind, Threat.Armour)
					.If(GroundFact.Armed, Arms.SmallArms)
					.IfNot(GroundFact.Contact, ContactLevel.None)
					.Act(GroundGoal.PathToLocker).While(GroundFact.Role, GroundRole.LockerRunner).End()
					.Act(GroundGoal.UseLocker).While(GroundFact.Role, GroundRole.LockerRunner)
						.Predict(GroundFact.Armed, Arms.Explosive).End()
					// Only plannable through the prediction above (§3.4, P4).
					.Act(GroundGoal.EngageArmour).If(GroundFact.Armed, Arms.Explosive)
						.While(GroundFact.Armed, Arms.Explosive).WhileNot(GroundFact.Contact, ContactLevel.None)
						.While(GroundFact.ThreatKind, Threat.Armour).End()
				.End()
				.Sequence("reload")
					.IfNot(GroundFact.Contact, ContactLevel.Visible)
					.If(GroundFact.MagazineLow)
					.Act(GroundGoal.Reload).While(GroundFact.MagazineLow)
						.WhileNot(GroundFact.Contact, ContactLevel.Visible).End()
				.End()
			.End()
			.Select("attack")
				.IfNot(GroundFact.Contact, ContactLevel.None)
				.Sequence("engage armour")
					.If(GroundFact.ThreatKind, Threat.Armour)
					.Act(GroundGoal.EngageArmour).If(GroundFact.Armed, Arms.Explosive)
						.While(GroundFact.Armed, Arms.Explosive).WhileNot(GroundFact.Contact, ContactLevel.None)
						.While(GroundFact.ThreatKind, Threat.Armour).End()
				.End()
				.Sequence("press with allies")
					.If(GroundFact.ThreatKind, Threat.Infantry)
					.If(GroundFact.Contact, ContactLevel.Visible)
					.If(GroundFact.AlliesNear)
					.IfNot(GroundFact.Odds, OddsBand.Outnumbered)
					.Act(GroundGoal.TakeFocusTarget).End()
					.Act(GroundGoal.AdvanceWithBuddy).While(GroundFact.AlliesNear)
						.While(GroundFact.Contact, ContactLevel.Visible).While(GroundFact.ThreatKind, Threat.Infantry)
						.WhileNot(GroundFact.Odds, OddsBand.Outnumbered).End()
				.End()
				.Sequence("engage")
					.If(GroundFact.ThreatKind, Threat.Infantry)
					.If(GroundFact.Contact, ContactLevel.Visible)
					.Act(GroundGoal.Engage).While(GroundFact.Contact, ContactLevel.Visible)
						.While(GroundFact.ThreatKind, Threat.Infantry).End()
				.End()
				// Infantry only: a rifleman sent to a tank's last-known position has
				// nothing to do when it gets there, and a launcher has "engage armour".
				// Not for a denier: a ghost is not a fight, and with the team remembering
				// something most of the time a denier that chased them never reached its
				// node (docs/HTN_BOTS.md §6, H2).
				.Sequence("investigate ghost")
					.If(GroundFact.Contact, ContactLevel.Ghost)
					.If(GroundFact.ThreatKind, Threat.Infantry)
					.IfNot(GroundFact.Role, GroundRole.Denier)
					.Act(GroundGoal.InvestigateGhost).While(GroundFact.Contact, ContactLevel.Ghost)
						.While(GroundFact.ThreatKind, Threat.Infantry).End()
				.End()
			.End()
			.Select("defend zone")
				.If(GroundFact.Role, GroundRole.Denier)
				.Sequence("hold node")
					.If(GroundFact.AtZone)
					.Act(GroundGoal.HoldNode).While(GroundFact.AtZone).While(GroundFact.Role, GroundRole.Denier).End()
				.End()
				.Sequence("take node")
					.Act(GroundGoal.TakeNode).While(GroundFact.Role, GroundRole.Denier).Predict(GroundFact.AtZone, true).End()
					.Act(GroundGoal.HoldNode).If(GroundFact.AtZone).While(GroundFact.AtZone)
						.While(GroundFact.Role, GroundRole.Denier).End()
				.End()
			.End()
			.Select("recon")
				.Sequence("sweep stalest zone")
					.If(GroundFact.Sweep)
					.Act(GroundGoal.SweepZone).While(GroundFact.Sweep).End()
				.End()
				.Sequence("standoff")
					.Act(GroundGoal.Standoff).End()
				.End()
			.End()
			.Build();
}

/// <summary>
/// What every ground bot plans with: one domain, one planner and one pooled factory,
/// shared (§3.4, P6 — safe because the server tick is single-threaded), and a
/// context per bot made from them.
/// </summary>
public sealed class GroundPlanning
{
	public GroundPlanning(in GroundPlanTraits traits, IFactory factory = null)
	{
		Factory = factory ?? new PooledHtnFactory();
		Traits = traits;
		Domain = GroundDomain.Build(Factory);
	}

	public IFactory Factory { get; }

	public GroundPlanTraits Traits { get; }

	public Domain<GroundContext> Domain { get; }

	public Planner<GroundContext> Planner { get; } = new();

	public GroundContext CreateContext()
	{
		var context = new GroundContext(Factory, Traits);
		context.Init();
		return context;
	}

	/// <summary>One tick: replans if a fact changed or the plan ended, then runs the current operator.</summary>
	public void Tick(GroundContext context) => Planner.Tick(Domain, context);

	/// <summary>Drops the plan, stopping its operator: for a bot that has died.</summary>
	public void Reset(GroundContext context) => Planner.Reset(context);
}
