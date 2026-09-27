using System;
using FluidHTN;
using FluidHTN.Factory;

namespace Gdpyr.HtnBench;

/// <summary>
/// A population of ground agents whose facts walk through the situations §5.1 is
/// written for, staggered so that they do not all change on one tick.
/// </summary>
public static class Population
{
	private static readonly Func<GroundContext, GroundGoal, TaskStatus> ScriptedWorld = Outcome;

	public static GroundContext[] Create(int count, IFactory factory)
	{
		var agents = new GroundContext[count];
		for (int i = 0; i < agents.Length; i++)
		{
			agents[i] = new GroundContext(factory) { World = ScriptedWorld };
			agents[i].Init();
		}

		return agents;
	}

	/// <summary>
	/// Goals that are a walk somewhere finish after three ticks; goals that are a
	/// stance — engage, hold, stand off — run until a fact takes them away.
	/// </summary>
	public static TaskStatus Outcome(GroundContext c, GroundGoal goal) => goal switch
	{
		GroundGoal.Engage or GroundGoal.EngageArmour or GroundGoal.HoldNode or GroundGoal.Standoff
			or GroundGoal.AdvanceWithBuddy => TaskStatus.Continue,
		_ => c.GoalTicks >= 3 ? TaskStatus.Success : TaskStatus.Continue,
	};

	/// <summary>One server tick. Returns how many contexts a sensor dirtied.</summary>
	public static int Step(Domain<GroundContext> domain, Planner<GroundContext> planner, GroundContext[] agents,
		int tick, int interval)
	{
		int dirtied = 0;
		for (int i = 0; i < agents.Length; i++)
		{
			GroundContext agent = agents[i];
			if ((tick + i) % interval == 0)
			{
				Situation(agent, ((tick + i) / interval) % 8);
				if (agent.IsDirty)
				{
					dirtied++;
				}
			}

			planner.Tick(domain, agent);
		}

		return dirtied;
	}

	/// <summary>Eight situations, one per branch of §5.1.</summary>
	public static void Situation(GroundContext c, int phase)
	{
		c.Sense(Fact.Contact, (byte)(phase switch
		{
			1 => ContactLevel.Ghost,
			2 or 3 or 4 or 5 => ContactLevel.Visible,
			_ => ContactLevel.None,
		}));
		c.Sense(Fact.ThreatKind, (byte)(phase == 4 ? Threat.Armour : Threat.Infantry));
		c.Sense(Fact.AlliesNear, (byte)(phase == 2 ? 1 : 0));
		c.Sense(Fact.Role, (byte)(phase switch
		{
			4 => GroundRole.LockerRunner,
			6 => GroundRole.Denier,
			_ => GroundRole.Assault,
		}));
		c.Sense(Fact.Armed, (byte)Arms.SmallArms);
		c.Sense(Fact.Health, (byte)(phase == 5 ? HealthBand.Critical : HealthBand.Ok));
		c.Sense(Fact.Odds, (byte)(phase == 5 ? OddsBand.Outnumbered : OddsBand.Even));
		c.Sense(Fact.AtZone, 0);
		c.Sense(Fact.MagazineLow, (byte)(phase == 7 ? 1 : 0));
	}
}
