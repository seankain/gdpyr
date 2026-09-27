using System;
using FluidHTN;
using FluidHTN.Contexts;
using FluidHTN.Debug;
using FluidHTN.Factory;
using System.Collections.Generic;

namespace Gdpyr.HtnBench;

/// <summary>
/// The ground bot's facts (docs/HTN_BOTS.md §5.1): one byte each, as FluidHTN stores
/// its world state. Only a change here makes the planner look again.
/// </summary>
public enum Fact : byte
{
	Contact,
	ThreatKind,
	Armed,
	Health,
	Odds,
	AlliesNear,
	Role,
	AtZone,
	InsideDefences,
	MagazineLow,
	Count,
}

public enum ContactLevel : byte { None, Ghost, Visible }

public enum Threat : byte { Infantry, Armour }

public enum Arms : byte { SmallArms, Explosive }

public enum HealthBand : byte { Ok, Hurt, Critical }

public enum OddsBand : byte { Favourable, Even, Outnumbered }

public enum GroundRole : byte { Assault, Denier, LockerRunner }

/// <summary>
/// What a primitive task asks the pilot to do. In the game this is the
/// <c>GroundIntent</c> <c>BotPilot</c> carries out; here it is only recorded.
/// </summary>
public enum GroundGoal : byte
{
	None,
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
	Standoff,
}

public sealed class GroundContext : BaseContext
{
	private readonly byte[] _facts = new byte[(int)Fact.Count];

	public GroundContext(IFactory factory) => Factory = factory;

	public override IFactory Factory { get; protected set; }
	public override IPlannerState PlannerState { get; protected set; } = new DefaultPlannerState();
	public override List<string> MTRDebug { get; set; }
	public override List<string> LastMTRDebug { get; set; }
	public override bool DebugMTR => false;
	public override Queue<IBaseDecompositionLogEntry> DecompositionLog { get; set; }
	public override bool LogDecomposition => false;
	public override byte[] WorldState => _facts;

	/// <summary>The intent the running primitive last wrote.</summary>
	public GroundGoal Intent;

	/// <summary>
	/// Stands in for the game: whether the goal the pilot is carrying out has finished.
	/// The bench and the probes script it; a null world keeps every goal running.
	/// </summary>
	public Func<GroundContext, GroundGoal, TaskStatus> World;

	/// <summary>Ticks the current goal has been running for, for the scripted world.</summary>
	public int GoalTicks;

	public byte Get(Fact fact) => GetState((int)fact);

	/// <summary>What a sensor scan writes. Marks the context dirty only when the value changes.</summary>
	public void Sense(Fact fact, byte value) => SetState((int)fact, value, true, EffectType.Permanent);

	public TaskStatus Perform(GroundGoal goal)
	{
		if (Intent != goal)
		{
			Intent = goal;
			GoalTicks = 0;
		}

		GoalTicks++;
		return World?.Invoke(this, goal) ?? TaskStatus.Continue;
	}
}

/// <summary>
/// FluidHTN's builder, extended with the vocabulary the domain is written in
/// (FluidHTN README, "Extending the Domain Builder"). Every lambda is made once,
/// when the domain is built; nothing here runs per tick but the lambdas themselves.
/// </summary>
public sealed class GroundDomainBuilder : BaseDomainBuilder<GroundDomainBuilder, GroundContext>
{
	private readonly bool _executingConditions;

	/// <param name="executingConditions">
	/// False builds the same domain with every <see cref="While"/> left out, which is
	/// how probe P3 shows what an operator without one does after its premise goes.
	/// </param>
	public GroundDomainBuilder(string name, IFactory factory, bool executingConditions = true)
		: base(name, factory) => _executingConditions = executingConditions;

	/// <summary>Decomposition condition: the fact holds this value.</summary>
	public GroundDomainBuilder If<TValue>(Fact fact, TValue value) where TValue : struct, Enum
	{
		byte wanted = Convert.ToByte(value);
		return Condition($"{fact}={value}", c => c.Get(fact) == wanted);
	}

	/// <summary>Decomposition condition: the fact does not hold this value.</summary>
	public GroundDomainBuilder IfNot<TValue>(Fact fact, TValue value) where TValue : struct, Enum
	{
		byte unwanted = Convert.ToByte(value);
		return Condition($"{fact}!={value}", c => c.Get(fact) != unwanted);
	}

	/// <summary>Decomposition condition on a yes/no fact.</summary>
	public GroundDomainBuilder If(Fact fact, bool value = true)
	{
		byte wanted = value ? (byte)1 : (byte)0;
		return Condition($"{fact}={value}", c => c.Get(fact) == wanted);
	}

	/// <summary>
	/// Executing condition: checked before every operator update, and the task fails —
	/// and the planner replans — the tick it stops holding (docs/HTN_BOTS.md §3.4, P3).
	/// </summary>
	public GroundDomainBuilder While<TValue>(Fact fact, TValue value) where TValue : struct, Enum
	{
		if (!_executingConditions)
		{
			return this;
		}

		byte wanted = Convert.ToByte(value);
		return ExecutingCondition($"while {fact}={value}", c => c.Get(fact) == wanted);
	}

	public GroundDomainBuilder While(Fact fact, bool value = true)
	{
		if (!_executingConditions)
		{
			return this;
		}

		byte wanted = value ? (byte)1 : (byte)0;
		return ExecutingCondition($"while {fact}={value}", c => c.Get(fact) == wanted);
	}

	/// <summary>A primitive task whose operator asks the pilot for <paramref name="goal"/>.</summary>
	public GroundDomainBuilder Act(GroundGoal goal)
	{
		Action(goal.ToString());
		return Do(c => c.Perform(goal), forceStopAction: c => c.Intent = GroundGoal.None);
	}

	/// <summary>
	/// A predicted outcome: true while planning, forgotten before execution. The sensor
	/// scan is what makes it true in the world, so it is never re-applied.
	/// </summary>
	public GroundDomainBuilder Predict<TValue>(Fact fact, TValue value) where TValue : struct, Enum
	{
		byte predicted = Convert.ToByte(value);
		return Effect($"{fact}:={value}", EffectType.PlanOnly, (c, type) => c.SetState((int)fact, predicted, false, type));
	}

	public GroundDomainBuilder Predict(Fact fact, bool value)
	{
		byte predicted = value ? (byte)1 : (byte)0;
		return Effect($"{fact}:={value}", EffectType.PlanOnly, (c, type) => c.SetState((int)fact, predicted, false, type));
	}
}

/// <summary>
/// docs/HTN_BOTS.md §5.1 as FluidHTN code. Selectors are in priority order; the
/// first method whose conditions hold wins, and a higher one pre-empts a running
/// plan when a fact changes.
/// </summary>
public static class GroundDomain
{
	public static Domain<GroundContext> Build(IFactory factory, bool executingConditions = true) =>
		new GroundDomainBuilder("ground", factory, executingConditions)
			.Select("retreat")
				.Sequence("leave defences")
					.If(Fact.InsideDefences)
					.Act(GroundGoal.LeaveDefences).While(Fact.InsideDefences).End()
				.End()
				.Sequence("fall back to allies")
					.If(Fact.Health, HealthBand.Critical)
					.If(Fact.Odds, OddsBand.Outnumbered)
					.Act(GroundGoal.FallBack).While(Fact.Odds, OddsBand.Outnumbered).End()
				.End()
			.End()
			.Select("resupply")
				.Sequence("rearm for armour")
					.If(Fact.Role, GroundRole.LockerRunner)
					.If(Fact.ThreatKind, Threat.Armour)
					.If(Fact.Armed, Arms.SmallArms)
					.IfNot(Fact.Contact, ContactLevel.None)
					.Act(GroundGoal.PathToLocker).End()
					.Act(GroundGoal.UseLocker).Predict(Fact.Armed, Arms.Explosive).End()
					// Only plannable through the prediction above: probe P4 checks it.
					.Act(GroundGoal.EngageArmour).If(Fact.Armed, Arms.Explosive)
						.While(Fact.Armed, Arms.Explosive).End()
				.End()
				.Sequence("reload")
					.IfNot(Fact.Contact, ContactLevel.Visible)
					.If(Fact.MagazineLow)
					.Act(GroundGoal.Reload).While(Fact.MagazineLow).End()
				.End()
			.End()
			.Select("attack")
				.IfNot(Fact.Contact, ContactLevel.None)
				.Sequence("engage armour")
					.If(Fact.ThreatKind, Threat.Armour)
					.Act(GroundGoal.EngageArmour).If(Fact.Armed, Arms.Explosive)
						.While(Fact.Armed, Arms.Explosive).End()
				.End()
				.Sequence("press with allies")
					.If(Fact.ThreatKind, Threat.Infantry)
					.If(Fact.AlliesNear)
					.IfNot(Fact.Odds, OddsBand.Outnumbered)
					.Act(GroundGoal.TakeFocusTarget).End()
					.Act(GroundGoal.AdvanceWithBuddy).While(Fact.AlliesNear).While(Fact.Contact, ContactLevel.Visible).End()
				.End()
				.Sequence("engage")
					.If(Fact.ThreatKind, Threat.Infantry)
					.If(Fact.Contact, ContactLevel.Visible)
					.Act(GroundGoal.Engage).While(Fact.Contact, ContactLevel.Visible).End()
				.End()
				.Sequence("investigate ghost")
					.If(Fact.Contact, ContactLevel.Ghost)
					.Act(GroundGoal.InvestigateGhost).While(Fact.Contact, ContactLevel.Ghost).End()
				.End()
			.End()
			.Select("defend zone")
				.If(Fact.Role, GroundRole.Denier)
				.Sequence("hold node")
					.If(Fact.AtZone)
					.Act(GroundGoal.HoldNode).While(Fact.AtZone).End()
				.End()
				.Sequence("take node")
					.Act(GroundGoal.TakeNode).Predict(Fact.AtZone, true).End()
					.Act(GroundGoal.HoldNode).If(Fact.AtZone).While(Fact.AtZone).End()
				.End()
			.End()
			.Select("recon")
				.Sequence("standoff")
					.Act(GroundGoal.Standoff).End()
				.End()
			.End()
			.Build();
}
