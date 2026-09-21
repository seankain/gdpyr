using Gdpyr.Match;
using Gdpyr.Sim;
using Xunit;

namespace Gdpyr.Tests;

/// <summary>
/// When a round ends and who won (docs/IMPLEMENTATION_PLAN.md §M5). The
/// strategist's defeat is four conditions at once, which is exactly the sort of
/// thing that is wrong for twenty minutes before anybody notices.
/// </summary>
public class WinConditionTests
{
	private const int Cheapest = 50;

	[Fact]
	public void BrokeWithNothingLeftIsDefeat()
	{
		Assert.True(WinConditions.IsStrategistEliminated(points: 49, Cheapest, liveUnits: 0, queuedUnits: 0,
			heldNodes: 0));
	}

	[Fact]
	public void AffordingTheCheapestUnitIsNotDefeat()
	{
		Assert.False(WinConditions.IsStrategistEliminated(points: 50, Cheapest, liveUnits: 0, queuedUnits: 0,
			heldNodes: 0));
	}

	[Fact]
	public void AUnitOnTheFieldIsNotDefeat()
	{
		Assert.False(WinConditions.IsStrategistEliminated(points: 0, Cheapest, liveUnits: 1, queuedUnits: 0,
			heldNodes: 0));
	}

	[Fact]
	public void AUnitInTheOvenIsNotDefeat()
	{
		// It has already been paid for (BuildQueue.TryEnqueue), so it is a unit.
		Assert.False(WinConditions.IsStrategistEliminated(points: 0, Cheapest, liveUnits: 0, queuedUnits: 1,
			heldNodes: 0));
	}

	[Fact]
	public void GroundHeldIsIncomeComingAndSoIsNotDefeat()
	{
		Assert.False(WinConditions.IsStrategistEliminated(points: 0, Cheapest, liveUnits: 0, queuedUnits: 0,
			heldNodes: 1));
	}

	[Fact]
	public void ACatalogWithNothingInItEndsTheRound()
	{
		// UnitCatalog.CheapestCost reports int.MaxValue when nothing can be built,
		// which is what a strategist with nothing to build is.
		Assert.True(WinConditions.IsStrategistEliminated(points: 1_000_000, int.MaxValue, liveUnits: 0,
			queuedUnits: 0, heldNodes: 0));
	}

	[Fact]
	public void AFreeUnitMeansNeverBeingBroke()
	{
		Assert.False(WinConditions.IsStrategistEliminated(points: 0, cheapestUnitCost: 0, liveUnits: 0,
			queuedUnits: 0, heldNodes: 0));
	}

	// ---- how the round records it ------------------------------------------

	private static MatchState Live()
	{
		var state = new MatchState();
		state.Start(0, groundTickets: 50, strategistPoints: 1000,
			durationTicks: SimConfig.TickRate * 60 * 20);
		return state;
	}

	[Fact]
	public void ARoundTheStrategistLosesIsWonByTheGroundForce()
	{
		MatchState state = Live();

		Assert.True(state.RegisterStrategistDefeat(600));

		Assert.Equal(RoundPhase.Ended, state.Phase);
		Assert.Equal(RoundOutcome.StrategistEliminated, state.Outcome);
		Assert.Equal(Team.GroundForce, state.Winner);
		Assert.Equal(600u, state.EndTick);
	}

	[Fact]
	public void TheStrategistCannotBeEliminatedTwiceOrOutsideALiveRound()
	{
		MatchState state = Live();
		Assert.True(state.RegisterStrategistDefeat(600));
		Assert.False(state.RegisterStrategistDefeat(700));
		Assert.Equal(600u, state.EndTick);

		Assert.False(new MatchState().RegisterStrategistDefeat(10));
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
	public void AnUndecidedRoundHasNoWinner()
	{
		Assert.Null(new MatchState().Winner);
		Assert.Null(Live().Winner);
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
