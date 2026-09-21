using Gdpyr.Match;
using Gdpyr.Sim;
using Xunit;

namespace Gdpyr.Tests;

public class MatchStateTests
{
	private const int Duration = SimConfig.TickRate * 60 * 20;

	private static MatchState Live(int tickets = 5)
	{
		var state = new MatchState();
		state.Start(0, tickets, Duration);
		return state;
	}

	[Fact]
	public void StartsInWarmup()
	{
		var state = new MatchState();

		Assert.Equal(RoundPhase.Warmup, state.Phase);
		Assert.False(state.IsLive);
		Assert.Equal(RoundOutcome.Undecided, state.Outcome);
	}

	[Fact]
	public void StartingARoundFillsTheTicketPoolAndTheClock()
	{
		MatchState state = Live(50);

		Assert.True(state.IsLive);
		Assert.Equal(50, state.GroundTickets);
		Assert.Equal(50, state.StartingGroundTickets);
		Assert.Equal((uint)Duration, state.EndTick);
		Assert.Equal(20f * 60f, state.SecondsRemaining(0), 1);
	}

	[Fact]
	public void EachDeathCostsATicket()
	{
		MatchState state = Live(3);

		Assert.False(state.RegisterGroundDeath(10));
		Assert.Equal(2, state.GroundTickets);
		Assert.False(state.RegisterGroundDeath(20));
		Assert.Equal(1, state.GroundTickets);
		Assert.Equal(2, state.GroundDeaths);
	}

	[Fact]
	public void TheLastTicketEndsTheRound()
	{
		// docs/IMPLEMENTATION_PLAN.md §M2: tickets decrement to zero and end the round.
		MatchState state = Live(2);

		Assert.False(state.RegisterGroundDeath(10));
		Assert.True(state.RegisterGroundDeath(20));

		Assert.Equal(RoundPhase.Ended, state.Phase);
		Assert.Equal(RoundOutcome.GroundForceEliminated, state.Outcome);
		Assert.Equal(0, state.GroundTickets);
		Assert.Equal(20u, state.EndTick);
	}

	[Fact]
	public void DeathsAfterTheRoundEndsCostNothing()
	{
		MatchState state = Live(1);
		state.RegisterGroundDeath(10);

		Assert.False(state.RegisterGroundDeath(11));
		Assert.Equal(0, state.GroundTickets);
		Assert.Equal(RoundOutcome.GroundForceEliminated, state.Outcome);
	}

	[Fact]
	public void DeathsDuringWarmupCostNothing()
	{
		// Otherwise the round starts short because someone was messing about.
		var state = new MatchState();

		Assert.False(state.RegisterGroundDeath(5));
		Assert.Equal(1, state.GroundDeaths);

		state.Start(100, 10, Duration);
		Assert.Equal(10, state.GroundTickets);
	}

	[Fact]
	public void TheClockRunningOutEndsTheRound()
	{
		MatchState state = Live(50);

		Assert.False(state.Advance(Duration - 1));
		Assert.True(state.Advance(Duration));

		Assert.Equal(RoundPhase.Ended, state.Phase);
		Assert.Equal(RoundOutcome.TimeExpired, state.Outcome);
		Assert.Equal(50, state.GroundTickets);
	}

	[Fact]
	public void TheClockOnlyEndsARoundOnce()
	{
		MatchState state = Live();
		Assert.True(state.Advance(Duration));
		Assert.False(state.Advance(Duration + 60));
	}

	[Fact]
	public void SecondsRemainingCountsDownAndFloorsAtZero()
	{
		MatchState state = Live();

		Assert.Equal(20f * 60f, state.SecondsRemaining(0), 1);
		Assert.Equal(10f * 60f, state.SecondsRemaining((uint)Duration / 2), 1);
		Assert.Equal(0f, state.SecondsRemaining((uint)Duration + 600));

		state.End(100, RoundOutcome.TimeExpired);
		Assert.Equal(0f, state.SecondsRemaining(100));
	}

	[Fact]
	public void VersionAdvancesOnEveryChangeWorthReplicating()
	{
		var state = new MatchState();
		uint start = state.Version;

		state.Start(0, 10, Duration);
		Assert.True(state.Version > start);

		uint afterStart = state.Version;
		state.RegisterGroundDeath(10);
		Assert.True(state.Version > afterStart);
	}

	[Fact]
	public void ApplyTakesTheServersView()
	{
		var client = new MatchState();
		client.Apply(RoundPhase.Live, RoundOutcome.Undecided, groundTickets: 33, startingTickets: 50,
			endTick: 9000, version: 7);

		Assert.True(client.IsLive);
		Assert.Equal(33, client.GroundTickets);
		Assert.Equal(50, client.StartingGroundTickets);
		Assert.Equal(9000u, client.EndTick);
	}

	[Fact]
	public void ApplyIgnoresAStaleUpdate()
	{
		// A client joining mid-round gets the roster replay and the live state in
		// whichever order they were queued.
		var client = new MatchState();
		client.Apply(RoundPhase.Live, RoundOutcome.Undecided, 33, 50, 9000, version: 7);
		client.Apply(RoundPhase.Warmup, RoundOutcome.Undecided, 50, 50, 0, version: 2);

		Assert.Equal(33, client.GroundTickets);
		Assert.True(client.IsLive);
	}

	[Fact]
	public void ResetReturnsToWarmup()
	{
		MatchState state = Live();
		state.RegisterGroundDeath(10);
		state.Reset();

		Assert.Equal(RoundPhase.Warmup, state.Phase);
		Assert.Equal(0, state.GroundTickets);
		Assert.Equal(0, state.GroundDeaths);
	}

	[Fact]
	public void NegativeTicketsAreRefusedAtTheDoor()
	{
		var state = new MatchState();
		state.Start(0, -5, Duration);

		Assert.Equal(0, state.GroundTickets);
	}
}
