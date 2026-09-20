using Gdpyr.Sim;
using Xunit;

namespace Gdpyr.Tests;

public class SimClockTests
{
	private const float Rtt80Ms = 0.08f;

	[Fact]
	public void Uninitialized_ProducesNoInput()
	{
		// Before the first probe reply the client has no idea what tick to stamp;
		// guessing would put a burst of garbage in the server's buffer.
		var clock = new SimClock();
		Assert.False(clock.Initialized);
		Assert.Equal(0, clock.Advance());
	}

	[Fact]
	public void FirstReply_PutsTheInputClockAheadOfTheServer()
	{
		var clock = new SimClock();
		clock.OnClockReply(serverTickAtReply: 1000, rttSeconds: Rtt80Ms);

		// 80 ms RTT is ~5 ticks; half of that each way, plus the jitter margin.
		Assert.True(clock.Initialized);
		Assert.Equal(1003u, clock.ServerTick);
		Assert.Equal(1003u + 3 + SimConfig.JitterBufferTicks, clock.NextInputTick());
		Assert.True(clock.InputLeadTicks > 0);
	}

	[Fact]
	public void RenderClock_TrailsTheServerEstimate()
	{
		var clock = new SimClock();
		clock.OnClockReply(1000, Rtt80Ms);
		Assert.Equal(clock.ServerTick - SimConfig.InterpolationDelayTicks, clock.RenderTick, precision: 3);
	}

	[Fact]
	public void Advance_MovesBothClocksOnePerTick()
	{
		var clock = new SimClock();
		clock.OnClockReply(1000, Rtt80Ms);
		uint server = clock.ServerTick;
		uint input = clock.InputTick;

		for (int i = 0; i < 10; i++)
		{
			Assert.Equal(1, clock.Advance());
			clock.NextInputTick();
		}

		Assert.Equal(server + 10, clock.ServerTick);
		Assert.Equal(input + 10, clock.InputTick);
	}

	[Fact]
	public void DeepBuffer_StallsOneTick()
	{
		// The server is holding more input than it needs: the client is running fast.
		var clock = new SimClock();
		clock.OnClockReply(1000, Rtt80Ms);
		clock.OnInputBufferDepth(SimConfig.MaxInputBufferDepth + 2);

		Assert.Equal(0, clock.Advance());
		Assert.Equal(1, clock.Nudges);
		Assert.Equal(1, clock.Advance());
	}

	[Fact]
	public void StarvedBuffer_ProducesAnExtraTick()
	{
		var clock = new SimClock();
		clock.OnClockReply(1000, Rtt80Ms);
		clock.OnInputBufferDepth(0);

		Assert.Equal(2, clock.Advance());
		Assert.Equal(1, clock.Nudges);
	}

	[Fact]
	public void HealthyBuffer_DoesNotNudge()
	{
		var clock = new SimClock();
		clock.OnClockReply(1000, Rtt80Ms);
		clock.OnInputBufferDepth(SimConfig.MinInputBufferDepth);
		Assert.Equal(1, clock.Advance());
		clock.OnInputBufferDepth(SimConfig.MaxInputBufferDepth);
		Assert.Equal(1, clock.Advance());
		Assert.Equal(0, clock.Nudges);
	}

	[Fact]
	public void PersistentlyDeepBuffer_NudgesOncePerRoundTrip()
	{
		// The depth in a snapshot is a round trip old. Nudging on every snapshot would
		// correct for the same error over and over and set the clock oscillating.
		var clock = new SimClock();
		clock.OnClockReply(1000, Rtt80Ms);

		int stalls = 0;
		for (int tick = 0; tick < 60; tick++)
		{
			// Reported twice a snapshot interval, as the real feed does.
			if (tick % 2 == 0)
			{
				clock.OnInputBufferDepth(SimConfig.MaxInputBufferDepth + 4);
			}

			if (clock.Advance() == 0)
			{
				stalls++;
			}
		}

		// One second at 80 ms RTT: a correction every ~8 ticks, not every snapshot.
		Assert.InRange(stalls, 4, 8);
	}

	[Fact]
	public void NudgeCooldown_ScalesWithTheRoundTrip()
	{
		var clock = new SimClock();
		clock.OnClockReply(1000, rttSeconds: 0.2f);
		clock.OnInputBufferDepth(0);
		clock.Advance();

		// 200 ms is 12 ticks, plus the jitter margin.
		Assert.InRange(clock.NudgeCooldownTicks, 12, SimConfig.MaxNudgeCooldownTicks);
	}

	[Fact]
	public void Rtt_IsSmoothedRatherThanTakenRaw()
	{
		// One 300 ms outlier on an 80 ms link must not shove the input clock.
		var clock = new SimClock();
		clock.OnClockReply(1000, Rtt80Ms);
		clock.OnClockReply(1060, 0.3f);

		Assert.InRange(clock.RttSeconds, Rtt80Ms, 0.15f);
		Assert.True(clock.RttJitterSeconds > 0f);
	}

	[Fact]
	public void SmallDrift_IsWalkedOffRatherThanSnapped()
	{
		var clock = new SimClock();
		clock.OnClockReply(1000, Rtt80Ms);
		uint before = clock.ServerTick;

		// The server is three ticks further along than we thought.
		clock.OnClockReply(1006, Rtt80Ms);

		Assert.Equal(before + 1, clock.ServerTick);
		Assert.Equal(0, clock.Resyncs);
	}

	[Fact]
	public void LargeDrift_Resyncs()
	{
		var clock = new SimClock();
		clock.OnClockReply(1000, Rtt80Ms);
		clock.OnClockReply(5000, Rtt80Ms);

		Assert.Equal(5003u, clock.ServerTick);
		Assert.True(clock.Resyncs > 0);
	}

	[Fact]
	public void InputClock_StaysAheadAcrossASecondOfTicking()
	{
		// 60 ticks of free-running plus 2 Hz probes: the lead should hold steady, not
		// creep in either direction. A reply is stamped half an RTT ago, so the tick
		// it carries always trails the server's real one by that much.
		const uint halfRttTicks = 3;
		var clock = new SimClock();
		uint replyStamp = 1000;
		clock.OnClockReply(replyStamp, Rtt80Ms);
		Assert.Equal(replyStamp + halfRttTicks, clock.ServerTick);
		long initialLead = clock.InputLeadTicks;

		for (int tick = 0; tick < 60; tick++)
		{
			int steps = clock.Advance();
			for (int i = 0; i < steps; i++)
			{
				clock.NextInputTick();
			}
			replyStamp++;
			if (tick % 30 == 29)
			{
				clock.OnClockReply(replyStamp, Rtt80Ms);
				clock.OnInputBufferDepth(SimConfig.MinInputBufferDepth + 1);
			}
		}

		Assert.Equal(replyStamp + halfRttTicks, clock.ServerTick);

		Assert.InRange(clock.InputLeadTicks, initialLead - 1, initialLead + 1);
		Assert.Equal(0, clock.Resyncs);
	}
}
