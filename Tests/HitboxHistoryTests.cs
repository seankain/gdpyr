using Gdpyr.Sim;
using Godot;
using Xunit;

namespace Gdpyr.Tests;

public class HitboxHistoryTests
{
	private static HitCapsule At(float z) => HitCapsule.FromFeet(new Vector3(0f, 0f, z), 2f, 0.5f);

	[Fact]
	public void CoversHalfASecond()
	{
		// docs/NETCODE.md §5 asks for 500 ms; 32 ticks at 60 Hz is 533 ms.
		Assert.Equal(32, HitboxHistory.Capacity);
		Assert.True(HitboxHistory.Capacity * SimConfig.TickDelta >= 0.5f);
	}

	[Fact]
	public void RecordsAndReadsBackAnExactTick()
	{
		var history = new HitboxHistory();
		history.Record(100, At(-5f));

		Assert.True(history.TrySample(100, out HitCapsule capsule));
		Assert.Equal(-5f, capsule.Bottom.Z);
		Assert.Equal(100u, history.NewestTick);
	}

	[Fact]
	public void EmptyHistoryAnswersNothing()
	{
		var history = new HitboxHistory();

		Assert.False(history.HasSamples);
		Assert.False(history.TrySample(0, out _));
		Assert.False(history.TryLatest(out _, out _));
		Assert.False(history.TrySampleOrLatest(7, out _));
	}

	[Fact]
	public void OldSamplesAgeOutRatherThanAliasing()
	{
		var history = new HitboxHistory();
		for (uint tick = 0; tick < 40; tick++)
		{
			history.Record(tick, At(-(float)tick));
		}

		// Tick 5 was overwritten by tick 37, which shares its ring slot. The point of
		// storing the tick alongside the capsule is that this reads as a miss rather
		// than as tick 37's position wearing tick 5's label.
		Assert.False(history.TrySample(5, out _));
		Assert.True(history.TrySample(37, out HitCapsule recent));
		Assert.Equal(-37f, recent.Bottom.Z);
	}

	[Fact]
	public void RewindFindsWhereATargetWas()
	{
		var history = new HitboxHistory();
		for (uint tick = 0; tick < 20; tick++)
		{
			history.Record(tick, At(-(float)tick));
		}

		// Six ticks of latency: the attacker was looking at tick 13, not 19.
		Assert.True(history.TrySample(19 - 6, out HitCapsule then));
		Assert.Equal(-13f, then.Bottom.Z);
	}

	[Fact]
	public void SampleOrLatestFallsBackWhenTheWindowHasRunOut()
	{
		var history = new HitboxHistory();
		for (uint tick = 0; tick < 100; tick++)
		{
			history.Record(tick, At(-(float)tick));
		}

		// A compensation window longer than the ring stops compensating; it does not
		// stop registering hits.
		Assert.True(history.TrySampleOrLatest(2, out HitCapsule capsule));
		Assert.Equal(-99f, capsule.Bottom.Z);
	}

	[Fact]
	public void ClearForgetsEverything()
	{
		var history = new HitboxHistory();
		history.Record(10, At(-1f));
		history.Clear();

		Assert.False(history.HasSamples);
		Assert.False(history.TrySample(10, out _));
	}

	[Fact]
	public void LatestTracksTheNewestRecord()
	{
		var history = new HitboxHistory();
		history.Record(10, At(-1f));
		history.Record(11, At(-2f));

		Assert.True(history.TryLatest(out uint tick, out HitCapsule capsule));
		Assert.Equal(11u, tick);
		Assert.Equal(-2f, capsule.Bottom.Z);
	}
}
