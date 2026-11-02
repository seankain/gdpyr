using Gdpyr.Match;
using Gdpyr.Sim;
using Xunit;

namespace Gdpyr.Tests;

/// <summary>
/// When a round ends and who won (docs/IMPLEMENTATION_PLAN.md §M5). Two ways and
/// no third: the ground force runs out of tickets, or the clock runs out with
/// tickets left. The strategist directs the waves and is never bankrupted out of
/// the round — the baseline always pays for another one (<see cref="BaselineIncome"/>).
/// </summary>
public class WinConditionTests
{
	private const int Duration = 1000;

	/// <summary>A round with the strategist broke from the first tick: no points, no baseline.</summary>
	private static MatchState Broke(BaselineIncome baseline = default)
	{
		var state = new MatchState();
		state.Start(0, groundTickets: 50, strategistPoints: 0, durationTicks: Duration, baseline);
		return state;
	}

	[Fact]
	public void RunningOutOfTicketsIsAStrategistWin()
	{
		var state = new MatchState();
		state.Start(0, groundTickets: 1, strategistPoints: 1000, durationTicks: 1000);

		Assert.True(state.RegisterGroundDeath(120));
		Assert.Equal(Team.Strategist, state.Winner);
	}

	[Fact]
	public void SurvivingTheClockIsAGroundForceWin()
	{
		var state = new MatchState();
		state.Start(0, groundTickets: 50, strategistPoints: 1000, durationTicks: 100);

		Assert.True(state.Advance(100));
		Assert.Equal(RoundOutcome.TimeExpired, state.Outcome);
		Assert.Equal(Team.GroundForce, state.Winner);
	}

	[Fact]
	public void ABrokeStrategistIsStillInTheRoundUntilTheClock()
	{
		// No points, no units, no nodes, nothing building: what used to end the round as
		// StrategistEliminated. Every tick but the last leaves it live.
		MatchState state = Broke();

		for (uint tick = 1; tick < Duration; tick++)
		{
			Assert.False(state.Advance(tick));
		}

		Assert.True(state.IsLive);
		Assert.Equal(RoundOutcome.Undecided, state.Outcome);

		Assert.True(state.Advance(Duration));
		Assert.Equal(RoundOutcome.TimeExpired, state.Outcome);
		Assert.Equal(Team.GroundForce, state.Winner);
	}

	[Fact]
	public void TheClockIsTheStrategistsOnlyLoss()
	{
		// The two outcomes a live round can still reach, and who each one is a win for.
		MatchState onTheClock = Broke();
		onTheClock.Advance(Duration);
		Assert.Equal(Team.GroundForce, onTheClock.Winner);

		var onTickets = new MatchState();
		onTickets.Start(0, groundTickets: 1, strategistPoints: 0, durationTicks: Duration);
		onTickets.RegisterGroundDeath(10);
		Assert.Equal(Team.Strategist, onTickets.Winner);
	}

	[Fact]
	public void ABrokeStrategistIsPaidForAWaveBeforeTheClock()
	{
		// 10 points every 60 ticks from nothing: the cheapest unit (50) inside 300 ticks,
		// well before a 1,000-tick round ends.
		MatchState state = Broke(new BaselineIncome(10, 60));

		for (uint tick = 1; tick <= 300; tick++)
		{
			state.PayBaseline(tick);
			state.Advance(tick);
		}

		Assert.True(state.IsLive);
		Assert.Equal(50, state.StrategistPoints);
	}

	[Fact]
	public void AnUndecidedRoundHasNoWinner()
	{
		Assert.Null(new MatchState().Winner);
		Assert.Null(Live().Winner);
	}

	[Fact]
	public void TheRetiredOutcomeStillReadsAsAGroundForceWin()
	{
		// No round ends this way any more, but a demo or a trace recorded before can
		// still carry it, and what it meant then is what it means read back now.
		var state = new MatchState();
		state.End(10, RoundOutcome.StrategistEliminated);

		Assert.Equal(3, (int)RoundOutcome.StrategistEliminated);
		Assert.Equal(Team.GroundForce, state.Winner);
	}

	private static MatchState Live()
	{
		var state = new MatchState();
		state.Start(0, groundTickets: 50, strategistPoints: 1000,
			durationTicks: SimConfig.TickRate * 60 * 20);
		return state;
	}

	[Fact]
	public void TheRoundKnowsHowLongItRan()
	{
		var state = new MatchState();
		Assert.Equal(0u, state.ElapsedTicks(500));

		state.Start(100, groundTickets: 50, strategistPoints: 1000, durationTicks: 1000);
		Assert.Equal(400u, state.ElapsedTicks(500));

		state.End(700, RoundOutcome.TimeExpired);
		Assert.Equal(600u, state.ElapsedTicks(5000));
	}
}
