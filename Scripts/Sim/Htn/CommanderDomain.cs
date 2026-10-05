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
/// One commander squad's facts (docs/HTN_BOTS.md §5.3): one byte each, as FluidHTN
/// stores its world state. The commander writes them once a decision; only a change
/// makes the squad's planner look again (§3.7).
/// </summary>
public enum CommandFact : byte
{
	/// <summary><see cref="CommandTask"/>: what the commander's assignment gave this squad.</summary>
	Task,

	/// <summary>The squad is falling back: latched when it is losing, cleared when it arrives.</summary>
	FallingBack,

	/// <summary>Below <see cref="CommanderTraits.ResupplyShare"/> of its strength at formation, with hysteresis.</summary>
	Depleted,

	/// <summary>A member is in a fight.</summary>
	Engaged,

	Count,
}

/// <summary>What the commander's assignment asks of a squad (§5.3). Retreat and Resupply are the squad's own.</summary>
public enum CommandTask : byte
{
	None = 0,

	/// <summary>Defend a zone: the garrison's standing task, or a squad sent to a threatened one.</summary>
	Defend = 1,

	/// <summary>Take a target: a live contact, a node the side does not hold, or a spawn swept in turn.</summary>
	Attack = 2,

	/// <summary>Patrol to the zone the side has gone longest without seeing.</summary>
	Recon = 3,

	/// <summary>Stand where the squads will want resupply: the supply trucks' standing task (D2, H5).</summary>
	Supply = 4,
}

/// <summary>
/// What a primitive task asks the squad to do: the operator's name, and the intent's.
/// The numbers are on the agent event stream as <c>squad_task</c>'s <c>goal</c>
/// (docs/AGENT_API.md §8), so they are protocol constants: append, never renumber.
/// </summary>
public enum CommandGoal : byte
{
	None = 0,
	FallBack = 1,
	Reinforce = 2,
	Stage = 3,
	Strike = 4,
	Scout = 5,
	Station = 6,
	Refill = 7,
	Hold = 8,
}

/// <summary>
/// What the running primitive task wants from the squad (docs/HTN_BOTS.md §4.2 rule 1):
/// the order its units should be under, and what the <see cref="SquadBoard"/> should
/// say about it. The engine half — <c>BotStrategist</c> — gives the order to every
/// member not already under it, through the request a human's RPC lands in, and
/// writes the board.
/// </summary>
public struct CommandIntent
{
	public CommandGoal Goal;

	public OrderKind Order;

	public Vector3 Point;

	/// <summary>The contact an attack order names; <see cref="OwnerId.None"/> for none. Only ever a live contact (D4).</summary>
	public int TargetOwnerId;

	public SquadMission Mission;

	/// <summary>
	/// The board phase this goal wants: <see cref="SquadPhase.Gathering"/> at
	/// <see cref="StagingPoint"/> while staging, <see cref="SquadPhase.FallingBack"/>
	/// while falling back, <see cref="SquadPhase.Moving"/> otherwise — which the census
	/// moves to and from <see cref="SquadPhase.Engaged"/> on its own.
	/// </summary>
	public SquadPhase Phase;

	public Vector3 StagingPoint;
}

/// <summary>
/// The commander's thresholds (docs/HTN_BOTS.md §5.3, §9: every one is a knob).
/// Strengths are <see cref="ForceRatio.Strength"/>: cost times health fraction.
/// </summary>
public readonly struct CommanderTraits
{
	/// <summary>Units kept at home defending the first barracks (<see cref="StrategistTraits.GarrisonUnits"/>).</summary>
	public readonly int GarrisonUnits;

	/// <summary>Members an assault squad forms with before it is given a target.</summary>
	public readonly int AssaultSquadSize;

	/// <summary>Retreat: below this share of its strength at formation…</summary>
	public readonly float RetreatShare;

	/// <summary>…with the enemy near it at least this many times what is left.</summary>
	public readonly float RetreatEnemyRatio;

	/// <summary>How far round a squad's centroid a remembered contact is in its fight.</summary>
	public readonly float EnemyNearMeters;

	/// <summary>How far round a held zone a remembered contact threatens it.</summary>
	public readonly float ThreatMeters;

	/// <summary>A squad sent to defend a zone is at least this many times the threat there.</summary>
	public readonly float ReinforceRatio;

	/// <summary>A squad sent to attack is at least this many times the estimate there.</summary>
	public readonly float AttackRatio;

	/// <summary>How far round a target a remembered contact counts towards its estimate.</summary>
	public readonly float ClusterMeters;

	/// <summary>How far short of its target a squad stages: past what a ground player's eyes reach.</summary>
	public readonly float StagingMeters;

	/// <summary>How near the staging point a member has to be to count as present.</summary>
	public readonly float AssembleMeters;

	/// <summary>The share of a squad present at its staging point before it strikes.</summary>
	public readonly float AssembleShare;

	/// <summary>How long a squad stages before it strikes with whoever has arrived: a unit that is not coming is not waited for (§3.4, P4).</summary>
	public readonly int StageMaxTicks;

	/// <summary>Recon when no contact has been live for this long…</summary>
	public readonly int ReconQuietTicks;

	/// <summary>…or a node has gone this long unobserved.</summary>
	public readonly int ReconStaleTicks;

	/// <summary>Resupply below this share of strength at formation, and until it is back above it by <see cref="ResupplyHysteresis"/>.</summary>
	public readonly float ResupplyShare;

	public readonly float ResupplyHysteresis;

	/// <summary>How long a person's or a policy's order keeps the commander's hands off a unit (D1).</summary>
	public readonly int ClaimTicks;

	/// <summary>How near its fall-back point a retreating squad's centroid has to be to have arrived.</summary>
	public readonly float ArriveMeters;

	/// <summary>How far in front of home, towards the threat, a squad with nothing to do waits.</summary>
	public readonly float ReserveOffsetMeters;

	/// <summary>How long an assault sweeps one ground spawn before the next, when nothing else is known (legacy's sweep).</summary>
	public readonly int SweepIntervalTicks;

	/// <summary>
	/// How long a depleted squad that healing can bring back waits at a supply source
	/// for it, walk included, before it goes home to be merged as one that cannot
	/// (§3.4, P4: what the world has to confirm needs a give-up).
	/// </summary>
	public readonly int RefillHealTicks;

	/// <summary>How far behind a squad defending a zone, towards home, the supply trucks stand.</summary>
	public readonly float SupplyStandoffMeters;

	public CommanderTraits(int garrisonUnits, int assaultSquadSize, float retreatShare, float retreatEnemyRatio,
		float enemyNearMeters, float threatMeters, float reinforceRatio, float attackRatio, float clusterMeters,
		float stagingMeters, float assembleMeters, float assembleShare, int stageMaxTicks, int reconQuietTicks,
		int reconStaleTicks, float resupplyShare, float resupplyHysteresis, int claimTicks, float arriveMeters,
		float reserveOffsetMeters, int sweepIntervalTicks, int refillHealTicks, float supplyStandoffMeters)
	{
		GarrisonUnits = Math.Max(garrisonUnits, 0);
		AssaultSquadSize = Math.Max(assaultSquadSize, 1);
		RetreatShare = Math.Clamp(retreatShare, 0f, 1f);
		RetreatEnemyRatio = MathF.Max(retreatEnemyRatio, 0f);
		EnemyNearMeters = MathF.Max(enemyNearMeters, 0f);
		ThreatMeters = MathF.Max(threatMeters, 0f);
		ReinforceRatio = MathF.Max(reinforceRatio, 0f);
		AttackRatio = MathF.Max(attackRatio, 0f);
		ClusterMeters = MathF.Max(clusterMeters, 0f);
		StagingMeters = MathF.Max(stagingMeters, 0f);
		AssembleMeters = MathF.Max(assembleMeters, 0f);
		AssembleShare = Math.Clamp(assembleShare, 0f, 1f);
		StageMaxTicks = Math.Max(stageMaxTicks, 0);
		ReconQuietTicks = Math.Max(reconQuietTicks, 0);
		ReconStaleTicks = Math.Max(reconStaleTicks, 0);
		ResupplyShare = Math.Clamp(resupplyShare, 0f, 1f);
		ResupplyHysteresis = MathF.Max(resupplyHysteresis, 0f);
		ClaimTicks = Math.Max(claimTicks, 0);
		ArriveMeters = MathF.Max(arriveMeters, 0f);
		ReserveOffsetMeters = MathF.Max(reserveOffsetMeters, 0f);
		SweepIntervalTicks = Math.Max(sweepIntervalTicks, 1);
		RefillHealTicks = Math.Max(refillHealTicks, 0);
		SupplyStandoffMeters = MathF.Max(supplyStandoffMeters, 0f);
	}

	/// <summary>
	/// §5.3's numbers: retreat below 40 % against 1.5 times the enemy, reinforce at 1.2
	/// times the threat within 60 m, attack at 1.5 times the estimate, strike once 80 %
	/// have assembled, recon after 20 s without a live contact or 60 s without seeing a
	/// node, resupply below 60 %, and 30 s off a unit a person ordered (D1). A
	/// ground bot sees 45 m, so a squad stages 60 m short. A depleted squad healing can
	/// bring back waits 60 s at a supply source for it (D2); trucks stand 40 m behind
	/// a defence, where a ground player at the zone is not on top of them and a unit
	/// defending it is well inside the 60 m it walks for resupply (§5.2).
	/// </summary>
	public static CommanderTraits Default => new(
		garrisonUnits: 4,
		assaultSquadSize: 4,
		retreatShare: 0.4f,
		retreatEnemyRatio: 1.5f,
		enemyNearMeters: 40f,
		threatMeters: 60f,
		reinforceRatio: 1.2f,
		attackRatio: 1.5f,
		clusterMeters: 25f,
		stagingMeters: 60f,
		assembleMeters: 12f,
		assembleShare: 0.8f,
		stageMaxTicks: SimConfig.TickRate * 30,
		reconQuietTicks: SimConfig.TickRate * 20,
		reconStaleTicks: SimConfig.TickRate * 60,
		resupplyShare: 0.6f,
		resupplyHysteresis: 0.1f,
		claimTicks: SimConfig.TickRate * 30,
		arriveMeters: 10f,
		reserveOffsetMeters: 10f,
		sweepIntervalTicks: SimConfig.TickRate * 25,
		refillHealTicks: SimConfig.TickRate * 60,
		supplyStandoffMeters: 40f);
}

/// <summary>
/// One commander squad's planning context: its facts, the places its operators
/// order it to, and the intent the running operator last wrote (docs/HTN_BOTS.md
/// §4.2). The <see cref="Commander"/> writes the knowledge once a decision and reads
/// <see cref="Intent"/>; operators are functions of this context and nothing else.
/// </summary>
public sealed class CommandContext : BaseContext
{
	private readonly byte[] _facts = new byte[(int)CommandFact.Count];

	public CommandContext(IFactory factory, in CommanderTraits traits)
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

	public CommanderTraits Traits { get; }

	/// <summary>What the running operator asked for. <see cref="CommandGoal.None"/> between tasks.</summary>
	public CommandIntent Intent;

	// ---- knowledge, written by the commander --------------------------------

	public uint Tick;

	/// <summary>Where the task sends it: the zone it defends, the target it attacks, the zone it scouts.</summary>
	public Vector3 TaskPoint;

	/// <summary>The live contact an attack names; <see cref="OwnerId.None"/> for a place.</summary>
	public int TaskOwnerId;

	public Vector3 StagingPoint;

	/// <summary>At least <see cref="CommanderTraits.AssembleShare"/> of the squad is at <see cref="StagingPoint"/>.</summary>
	public bool Assembled;

	/// <summary>Where a retreat goes, fixed when it starts; and whether the squad is there.</summary>
	public Vector3 FallbackPoint;
	public bool AtFallback;

	/// <summary>Where a depleted squad refills: home; and whether the squad is there.</summary>
	public Vector3 HomePoint;
	public bool AtHome;

	/// <summary>Where a squad with nothing to do waits.</summary>
	public Vector3 ReservePoint;

	/// <summary>
	/// Healing can bring the squad back above its resupply share: its units at full
	/// health would be worth enough (D2). A squad that has lost too many cannot be, and
	/// goes home to be merged.
	/// </summary>
	public bool Healable;

	/// <summary>The supply source nearest the squad: home, or a supply truck.</summary>
	public Vector3 SupplyPoint;

	/// <summary>The tick the running operator started on.</summary>
	public uint GoalStartTick;

	/// <summary>A retreat or a refill arrived: the commander returns the squad's units to its pool, which merges them.</summary>
	public bool Arrived;

	/// <summary>What its planner has done lately, for the spectator's debugger (docs/AI_DEBUG.md §3.3). Null for a context made outside <see cref="CommanderPlanning"/>.</summary>
	public HtnHistory History { get; internal set; }

	public byte Get(CommandFact fact) => GetState((int)fact);

	public bool Is(CommandFact fact) => GetState((int)fact) != 0;

	/// <summary>What the commander writes. Marks the context dirty only when the value changes.</summary>
	public void Sense(CommandFact fact, byte value) => SetState((int)fact, value, true, EffectType.Permanent);

	public void Sense(CommandFact fact, bool value) => Sense(fact, value ? (byte)1 : (byte)0);

	/// <summary>Back to a squad with no history, for a slot that closes or opens. The caller resets the planner.</summary>
	public void Clear()
	{
		for (int i = 0; i < (int)CommandFact.Count; i++)
		{
			Sense((CommandFact)i, 0);
		}

		IsDirty = false;
		Intent = default;
		TaskPoint = default;
		TaskOwnerId = OwnerId.None;
		StagingPoint = default;
		Assembled = false;
		FallbackPoint = default;
		AtFallback = false;
		AtHome = false;
		Healable = false;
		SupplyPoint = default;
		GoalStartTick = 0;
		Arrived = false;
	}

	// ---- operators ---------------------------------------------------------

	public TaskStatus Begin(CommandGoal goal)
	{
		GoalStartTick = Tick;
		return Perform(goal);
	}

	public void End() => Intent = default;

	/// <summary>
	/// One decision of an operator: writes the intent, and says whether it is done.
	/// Idempotent — FluidHTN can run an operator twice in one tick (#21, §3.2).
	/// </summary>
	public TaskStatus Perform(CommandGoal goal)
	{
		Intent = new CommandIntent { Goal = goal, Phase = SquadPhase.Moving };

		switch (goal)
		{
			case CommandGoal.FallBack:
				if (AtFallback)
				{
					// Arrived: the latch comes off here, so the plan that replaces this one is
					// planned without it, and the commander merges the squad (§5.3).
					Sense(CommandFact.FallingBack, false);
					Arrived = true;
					return TaskStatus.Success;
				}

				// A move, not an attack-move: a squad falling back that stopped for every
				// contact would not be falling back.
				return Order(OrderKind.Move, FallbackPoint, OwnerId.None, SquadMission.Retreat, SquadPhase.FallingBack);

			case CommandGoal.Reinforce:
				return Order(OrderKind.Defend, TaskPoint, OwnerId.None, SquadMission.DefendZone, SquadPhase.Moving);

			case CommandGoal.Stage:
				// The attack order is given now, and its units wait for each other at the
				// staging point while the board says gathering (§5.2, "wait for squad").
				Order(OrderKind.Attack, TaskPoint, TaskOwnerId, SquadMission.Attack, SquadPhase.Gathering);
				Intent.StagingPoint = StagingPoint;
				return Assembled || Tick - GoalStartTick >= (uint)Traits.StageMaxTicks
					? TaskStatus.Success
					: TaskStatus.Continue;

			case CommandGoal.Strike:
				return Order(OrderKind.Attack, TaskPoint, TaskOwnerId, SquadMission.Attack, SquadPhase.Moving);

			case CommandGoal.Scout:
				// A patrol, not an attack: a scout's job is to see (§5.3).
				return Order(OrderKind.Patrol, TaskPoint, OwnerId.None, SquadMission.Recon, SquadPhase.Moving);

			case CommandGoal.Station:
				// A move, not an attack-move: a truck's job is to be there when it is wanted.
				return Order(OrderKind.Move, TaskPoint, OwnerId.None, SquadMission.Resupply, SquadPhase.Moving);

			case CommandGoal.Refill:
				if (Healable && (Tick < GoalStartTick || Tick - GoalStartTick < (uint)Traits.RefillHealTicks))
				{
					// Healing will bring it back (D2): to the nearest supply source, where it
					// stays until it is no longer depleted — which ends this task — or its
					// time is up.
					return Order(OrderKind.Move, SupplyPoint, OwnerId.None, SquadMission.Resupply, SquadPhase.Moving);
				}

				if (AtHome)
				{
					// Home: merged into whatever is forming there, which production fills
					// whether or not any comes (§5.3, "route the next produced units into it").
					Arrived = true;
					return TaskStatus.Success;
				}

				return Order(OrderKind.Move, HomePoint, OwnerId.None, SquadMission.Resupply, SquadPhase.Moving);

			case CommandGoal.Hold:
				return Order(OrderKind.Defend, ReservePoint, OwnerId.None, SquadMission.DefendZone, SquadPhase.Moving);

			default:
				return TaskStatus.Failure;
		}
	}

	private TaskStatus Order(OrderKind order, Vector3 point, int targetOwnerId, SquadMission mission, SquadPhase phase)
	{
		Intent.Order = order;
		Intent.Point = point;
		Intent.TargetOwnerId = targetOwnerId;
		Intent.Mission = mission;
		Intent.Phase = phase;
		return TaskStatus.Continue;
	}
}

/// <summary>FluidHTN's builder with the commander's vocabulary, as <see cref="UnitDomainBuilder"/> is for the unit's.</summary>
public sealed class CommandDomainBuilder : BaseDomainBuilder<CommandDomainBuilder, CommandContext>
{
	private readonly bool _executingConditions;

	/// <param name="executingConditions">False leaves every <c>While</c> out (§3.4, P3).</param>
	public CommandDomainBuilder(string name, IFactory factory, bool executingConditions = true)
		: base(name, factory) => _executingConditions = executingConditions;

	public CommandDomainBuilder If(CommandFact fact, CommandTask value)
	{
		byte wanted = (byte)value;
		return Condition($"{fact}={value}", c => c.Get(fact) == wanted);
	}

	public CommandDomainBuilder If(CommandFact fact, bool value = true)
	{
		byte wanted = value ? (byte)1 : (byte)0;
		return Condition($"{fact}={value}", c => c.Get(fact) == wanted);
	}

	public CommandDomainBuilder While(CommandFact fact, CommandTask value)
	{
		if (!_executingConditions)
		{
			return this;
		}

		byte wanted = (byte)value;
		return ExecutingCondition($"while {fact}={value}", c => c.Get(fact) == wanted);
	}

	public CommandDomainBuilder While(CommandFact fact, bool value = true)
	{
		if (!_executingConditions)
		{
			return this;
		}

		byte wanted = value ? (byte)1 : (byte)0;
		return ExecutingCondition($"while {fact}={value}", c => c.Get(fact) == wanted);
	}

	/// <summary>A primitive task whose operator asks the squad for <paramref name="goal"/>, clearing its intent however it ends (§3.7).</summary>
	public CommandDomainBuilder Act(CommandGoal goal)
	{
		Action(goal.ToString());
		if (Pointer is IPrimitiveTask task)
		{
			task.SetOperator(new FuncOperator<CommandContext>(
				c => c.Perform(goal),
				start: c => c.Begin(goal),
				funcStop: c => c.End(),
				funcAborted: c => c.End()));
		}

		return this;
	}
}

/// <summary>
/// docs/HTN_BOTS.md §5.3 as FluidHTN code: what one of the commander's squads does
/// with the task the commander's assignment gave it. Built once and shared by every
/// squad of every computer strategist (§3.4, P6).
///
/// Selectors are in priority order. Retreat is the squad's own and pre-empts
/// anything (P2); Defend Zone pre-empts an attack, so a squad the commander pulls
/// back to a threatened node goes; Resupply is below the assignment, because the
/// commander takes a depleted squad's attack away when it is not in a fight. Supply
/// is the trucks' standing task (H5), which no other squad is ever given. Every
/// operator but Hold has an executing condition for its premise (P3); Hold is the
/// bottom of the tree, and anything above it replaces it.
///
/// Attack is the one partial plan in the game: stage, then <c>PausePlan</c>, then
/// strike, planned against the world as it is once the squad has assembled (P5,
/// D6).
/// </summary>
public static class CommanderDomain
{
	public static Domain<CommandContext> Build(IFactory factory, bool executingConditions = true) =>
		new CommandDomainBuilder("squad", factory, executingConditions)
			.Select("retreat")
				.Sequence("fall back")
					.If(CommandFact.FallingBack)
					.Act(CommandGoal.FallBack).While(CommandFact.FallingBack).End()
				.End()
			.End()
			.Select("defend zone")
				.Sequence("reinforce")
					.If(CommandFact.Task, CommandTask.Defend)
					.Act(CommandGoal.Reinforce).While(CommandFact.Task, CommandTask.Defend).End()
				.End()
			.End()
			.Select("attack")
				.Sequence("stage and strike")
					.If(CommandFact.Task, CommandTask.Attack)
					.Act(CommandGoal.Stage).While(CommandFact.Task, CommandTask.Attack).End()
					.PausePlan()
					.Act(CommandGoal.Strike).If(CommandFact.Task, CommandTask.Attack)
						.While(CommandFact.Task, CommandTask.Attack).End()
				.End()
			.End()
			.Select("recon")
				.Sequence("scout")
					.If(CommandFact.Task, CommandTask.Recon)
					.Act(CommandGoal.Scout).While(CommandFact.Task, CommandTask.Recon).End()
				.End()
			.End()
			.Select("supply")
				.Sequence("station")
					.If(CommandFact.Task, CommandTask.Supply)
					.Act(CommandGoal.Station).While(CommandFact.Task, CommandTask.Supply).End()
				.End()
			.End()
			.Select("resupply")
				.Sequence("refill")
					.If(CommandFact.Depleted)
					.If(CommandFact.Engaged, false)
					.Act(CommandGoal.Refill).While(CommandFact.Depleted).While(CommandFact.Engaged, false).End()
				.End()
			.End()
			.Select("hold")
				.Sequence("reserve")
					.Act(CommandGoal.Hold).End()
				.End()
			.End()
			.Build();
}
