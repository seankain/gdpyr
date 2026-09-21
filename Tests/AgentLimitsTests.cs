using System;
using Gdpyr.Sim;
using Gdpyr.Sim.Agent;
using Xunit;

namespace Gdpyr.Tests;

/// <summary>
/// The fairness ceilings (docs/AGENT_API.md §7.3).
///
/// An agent that snap-aims across 180° in one tick and fires the instant its
/// crosshair crosses a head is not playing the game a person plays, and a
/// playtest against one measures nothing.
/// </summary>
public class AgentLimitsTests
{
	private static readonly AgentLimits Bounded = AgentLimits.Default;
	private static readonly AgentLimits Unbounded = AgentLimits.From(BotTraits.Default, unbounded: true);

	[Fact]
	public void TheDefaultCeilingIsTheOneABotPlaysUnder()
	{
		Assert.Equal(BotTraits.Default.TurnRateRadians, Bounded.TurnRateRadians, 4);
		Assert.Equal(AgentLimits.DefaultCommandsPerSecond, Bounded.CommandsPerSecond);
		Assert.False(Bounded.Unbounded);
		Assert.False(Bounded.Omniscient);
	}

	[Fact]
	public void AHalfTurnInOneTickArrivesAsTheTurnRate()
	{
		float landed = Bounded.SlewYaw(0f, 3f, SimConfig.TickDelta);
		Assert.Equal(BotTraits.Default.TurnRateRadians * SimConfig.TickDelta, landed, 4);
	}

	[Fact]
	public void AnExactHalfTurnGoesOneWayOrTheOther_ButNoFurtherThanTheCeiling()
	{
		// π is the same distance either way round; which way it resolves is
		// arbitrary, and all the ceiling owes is that it is one tick's worth.
		float landed = Bounded.SlewYaw(0f, MathF.PI, SimConfig.TickDelta);
		Assert.Equal(BotTraits.Default.TurnRateRadians * SimConfig.TickDelta, MathF.Abs(landed), 4);
	}

	[Fact]
	public void TheClampTakesTheShortWayRound()
	{
		// Asking for 0.01 rad while the character sits at 6.27 is a nudge left, not
		// a full turn right.
		float landed = Bounded.SlewYaw(6.27f, 0.01f, SimConfig.TickDelta);
		Assert.True(landed > 6.27f);
		Assert.InRange(landed - 6.27f, 0f, BotTraits.Default.TurnRateRadians * SimConfig.TickDelta + 1e-4f);
	}

	[Fact]
	public void ShortestDeltaIsSignedAndWrapped()
	{
		Assert.Equal(0.02f, AgentLimits.ShortestDelta(6.27f, 6.29f), 4);
		Assert.InRange(AgentLimits.ShortestDelta(0.01f, 6.27f), -0.03f, -0.01f);
		Assert.InRange(AgentLimits.ShortestDelta(0f, MathF.PI * 1.5f), -1.58f, -1.56f);
	}

	[Fact]
	public void AnUnboundedSeatGetsTheWholeTurn()
	{
		float landed = Unbounded.SlewYaw(0f, 3f, SimConfig.TickDelta);
		Assert.Equal(3f, landed, 4);
	}

	[Fact]
	public void PitchIsClampedToTheLookLimitsAndThenSlewed()
	{
		float landed = Bounded.SlewPitch(0f, 10f, SimConfig.TickDelta);
		Assert.InRange(landed, 0f, BotTraits.Default.TurnRateRadians * SimConfig.TickDelta + 1e-4f);

		// Even unbounded, a policy cannot look further up than a person can.
		Assert.Equal(Quantize.HalfPi, Unbounded.SlewPitch(0f, 10f, SimConfig.TickDelta), 4);
		Assert.Equal(-Quantize.HalfPi, Unbounded.SlewPitch(0f, -10f, SimConfig.TickDelta), 4);
	}

	[Fact]
	public void AHeldLookTargetWalksTowardsItRatherThanSnapping()
	{
		float yaw = 0f;
		for (int i = 0; i < 4; i++)
		{
			yaw = Bounded.SlewYaw(yaw, 3f, SimConfig.TickDelta);
		}

		// Four ticks of ceiling, not four ticks' worth in one.
		Assert.Equal(BotTraits.Default.TurnRateRadians * SimConfig.TickDelta * 4, yaw, 4);
	}

	// ---- the APM cap -------------------------------------------------------

	[Fact]
	public void NineCommandsBecomeEight()
	{
		var budget = new AgentCommandBudget();
		Assert.Equal(8, budget.Take(0, 9, Bounded));
		Assert.Equal(8, budget.Accepted);
		Assert.Equal(1, budget.Refused);
	}

	[Fact]
	public void TheBudgetRefillsOverASecondAndNoFurther()
	{
		var budget = new AgentCommandBudget();
		budget.Take(0, 8, Bounded);

		// Half a second later: four more.
		Assert.Equal(4, budget.Take(SimConfig.TickRate / 2, 8, Bounded));

		// Ten seconds later it is full, not overflowing.
		Assert.Equal(8, budget.Take(SimConfig.TickRate * 10, 64, Bounded));
	}

	[Fact]
	public void AnUnboundedSeatIsNotRateLimited()
	{
		var budget = new AgentCommandBudget();
		Assert.Equal(1000, budget.Take(0, 1000, Unbounded));
		Assert.Equal(0, budget.Refused);
	}

	[Fact]
	public void AskingForNothingSpendsNothing()
	{
		var budget = new AgentCommandBudget();
		Assert.Equal(0, budget.Take(0, 0, Bounded));
		Assert.Equal(8, budget.Take(0, 8, Bounded));
	}
}
