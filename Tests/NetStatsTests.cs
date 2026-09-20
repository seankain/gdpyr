using Gdpyr.Sim;
using Xunit;

namespace Gdpyr.Tests;

public class NetStatsTests
{
	private static void AdvanceOneSecond(NetStats stats)
	{
		for (int tick = 0; tick < SimConfig.TickRate; tick++)
		{
			stats.Advance(SimConfig.TickDelta);
		}
	}

	[Fact]
	public void RatesArePublishedOncePerSecond()
	{
		var stats = new NetStats();
		stats.RecordSent(37);
		stats.RecordReceived(200);

		// Nothing is published mid-window: a partial count would read as a dip.
		stats.Advance(0.5f);
		Assert.Equal(0f, stats.BytesOutPerSecond);

		stats.Advance(0.5f);
		Assert.Equal(37f, stats.BytesOutPerSecond, precision: 1);
		Assert.Equal(200f, stats.BytesInPerSecond, precision: 1);
	}

	[Fact]
	public void InputAtSixtyHertzMatchesTheBudget()
	{
		// docs/NETCODE.md §7 budgets ~3 KB/s upstream per client.
		var stats = new NetStats();
		for (int tick = 0; tick < SimConfig.TickRate; tick++)
		{
			stats.RecordSent(InputCodec.PayloadBytes(SimConfig.InputRedundancy));
			stats.Advance(SimConfig.TickDelta);
		}

		Assert.InRange(stats.BytesOutPerSecond, 2000f, 3000f);
	}

	[Fact]
	public void ErrorsAreMeanedAndPeakedOverTheWindow()
	{
		var stats = new NetStats();
		stats.RecordPredictionError(0.01f);
		stats.RecordPredictionError(0.03f);
		AdvanceOneSecond(stats);

		Assert.Equal(0.02f, stats.MeanErrorMeters, precision: 4);
		Assert.Equal(0.03f, stats.MaxErrorMeters, precision: 4);
	}

	[Fact]
	public void WindowResetsBetweenPublications()
	{
		var stats = new NetStats();
		stats.RecordMisprediction();
		stats.RecordPredictionError(1f);
		AdvanceOneSecond(stats);
		Assert.Equal(1f, stats.MispredictionsPerSecond, precision: 2);

		AdvanceOneSecond(stats);
		Assert.Equal(0f, stats.MispredictionsPerSecond);
		Assert.Equal(0f, stats.MaxErrorMeters);
		// Totals are cumulative even though the rates are not.
		Assert.Equal(1, stats.TotalMispredictions);
	}

	[Fact]
	public void FormatRate_SwitchesUnitAtAKilobyte()
	{
		Assert.Equal("512 B/s", NetStats.FormatRate(512f));
		Assert.Equal("2.0 KB/s", NetStats.FormatRate(2048f));
	}
}
