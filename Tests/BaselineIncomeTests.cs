using Gdpyr.Match;
using Gdpyr.Sim;
using Xunit;

namespace Gdpyr.Tests;

/// <summary>
/// The strategist's baseline income (<see cref="BaselineIncome"/>): what it is paid
/// whatever it holds, so that with every node lost it can still send a small wave.
/// </summary>
public class BaselineIncomeTests
{
	private const int Interval = SimConfig.TickRate * 5;
	private const int Duration = SimConfig.TickRate * 60 * 20;

	private static MatchState Live(BaselineIncome baseline, int points = 0, uint start = 0)
	{
		var state = new MatchState();
		state.Start(start, groundTickets: 50, points, Duration, baseline);
		return state;
	}

	[Fact]
	public void TenEveryFiveSecondsIsOneHundredAndTwentyAMinute()
	{
		Assert.Equal(120f, new BaselineIncome(10, Interval).PointsPerMinute, 3);
	}

	[Fact]
	public void TheDefaultAndAZeroPaymentPayNothing()
	{
		Assert.False(default(BaselineIncome).Pays);
		Assert.Equal(0f, default(BaselineIncome).PointsPerMinute);
		Assert.False(new BaselineIncome(0, Interval).Pays);
	}

	[Fact]
	public void ANegativePaymentOrIntervalIsClamped()
	{
		var income = new BaselineIncome(-5, -1);

		Assert.Equal(0, income.Points);
		Assert.Equal(1, income.IntervalTicks);
		Assert.False(income.Pays);
	}

	[Fact]
	public void NothingIsPaidBeforeTheFirstInterval()
	{
		// The starting pool is the first interval's payment, as a node's first payment
		// is an interval after it is taken.
		MatchState state = Live(new BaselineIncome(10, Interval), start: 100);

		for (uint tick = 100; tick < 100 + Interval; tick++)
		{
			Assert.Equal(0, state.PayBaseline(tick));
		}

		Assert.Equal(10, state.PayBaseline(100 + Interval));
		Assert.Equal(10, state.StrategistPoints);
	}

	[Fact]
	public void ItPaysOncePerIntervalAndCountsWhatItPaid()
	{
		MatchState state = Live(new BaselineIncome(10, Interval));

		for (uint tick = 1; tick <= Interval * 6; tick++)
		{
			state.PayBaseline(tick);
		}

		Assert.Equal(60, state.StrategistPoints);
		Assert.Equal(60, state.BaselinePaid);
		Assert.Equal(60, state.Strategist.Earned);
	}

	[Fact]
	public void AMinuteBrokeIsTwoRiflemen()
	{
		// The tuning argument in GameModeDefinition: with every node lost the strategist
		// still affords the cheapest unit (50) more than twice a minute.
		MatchState state = Live(new BaselineIncome(10, Interval));

		for (uint tick = 1; tick <= SimConfig.TickRate * 60; tick++)
		{
			state.PayBaseline(tick);
		}

		Assert.Equal(120, state.StrategistPoints);
	}

	[Fact]
	public void ASkippedTickIsPaidLateNotLost()
	{
		MatchState state = Live(new BaselineIncome(10, Interval));

		// Nothing asked for three intervals: the next ask pays all three.
		Assert.Equal(30, state.PayBaseline(Interval * 3));
		Assert.Equal(0, state.PayBaseline(Interval * 3 + 1));
		Assert.Equal(10, state.PayBaseline(Interval * 4));
	}

	[Fact]
	public void ItPaysOnlyWhileTheRoundIsLive()
	{
		var warmup = new MatchState();
		Assert.Equal(0, warmup.PayBaseline(Interval * 10));

		MatchState state = Live(new BaselineIncome(10, Interval));
		state.End(Interval, RoundOutcome.TimeExpired);

		Assert.Equal(0, state.PayBaseline(Interval * 10));
		Assert.Equal(0, state.StrategistPoints);
	}

	[Fact]
	public void APaymentBumpsTheLedgersVersionSoTheBalanceReplicates()
	{
		MatchState state = Live(new BaselineIncome(10, Interval));
		uint version = state.Strategist.Version;

		state.PayBaseline(Interval);

		Assert.True(state.Strategist.Version > version);
	}

	[Fact]
	public void ANewRoundStartsTheScheduleAndTheCountOver()
	{
		MatchState state = Live(new BaselineIncome(10, Interval));
		state.PayBaseline(Interval * 2);
		state.End(Interval * 2, RoundOutcome.TimeExpired);

		state.Reset();
		Assert.Equal(0, state.BaselinePaid);
		Assert.False(state.Baseline.Pays);

		state.Start(10_000, groundTickets: 50, strategistPoints: 0, Duration, new BaselineIncome(10, Interval));
		Assert.Equal(0, state.PayBaseline(10_000 + Interval - 1));
		Assert.Equal(10, state.PayBaseline(10_000 + Interval));
		Assert.Equal(10, state.BaselinePaid);
	}

	[Fact]
	public void ARoundStartedWithoutOneIsPaidNothing()
	{
		// The pre-baseline signature: what every caller that does not ask for one gets.
		var state = new MatchState();
		state.Start(0, groundTickets: 50, strategistPoints: 100, Duration);

		Assert.Equal(0, state.PayBaseline(Interval * 100));
		Assert.Equal(100, state.StrategistPoints);
	}
}
