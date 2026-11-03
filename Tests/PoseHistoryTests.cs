using Gdpyr.Sim;
using Godot;
using Xunit;

namespace Gdpyr.Tests;

/// <summary>
/// Each crew vehicle's last half second of poses, and the tick a seated shooter's client
/// was drawing when it fired (docs/GROUND_VEHICLES.md §5.6, GV-D7).
/// </summary>
public class PoseHistoryTests
{
	private static CrewPose PoseAt(uint tick) => new(new Vector3(tick * 0.3f, 1f, -tick * 0.1f), tick * 0.01f, 0.02f, -0.01f);

	[Fact]
	public void ASampledPose_IsTheOneRecordedForItsTick()
	{
		var history = new PoseHistory();
		for (uint tick = 100; tick < 120; tick++)
		{
			history.Record(tick, PoseAt(tick));
		}

		for (uint tick = 100; tick < 120; tick++)
		{
			Assert.True(history.TryGet(tick, out CrewPose pose));
			Assert.Equal(PoseAt(tick).Position, pose.Position);
			Assert.Equal(PoseAt(tick).Yaw, pose.Yaw);
			Assert.Equal(PoseAt(tick).Position, history.Sample(tick).Position);
		}
	}

	[Fact]
	public void TheRing_Wraps_AndForgetsWhatItNoLongerHolds()
	{
		var history = new PoseHistory();
		for (uint tick = 1; tick <= 200; tick++)
		{
			history.Record(tick, PoseAt(tick));
		}

		Assert.Equal(200u, history.NewestTick);
		Assert.True(history.TryGet(200 - PoseHistory.Capacity + 1, out _));
		Assert.False(history.TryGet(200 - PoseHistory.Capacity, out _));
		Assert.False(history.TryGet(5, out _));
	}

	[Fact]
	public void ATickOutsideTheRing_SamplesTheNearestPoseItHolds()
	{
		var history = new PoseHistory();
		for (uint tick = 1; tick <= 200; tick++)
		{
			history.Record(tick, PoseAt(tick));
		}

		uint oldest = 200 - PoseHistory.Capacity + 1;
		Assert.Equal(PoseAt(oldest).Position, history.Sample(5).Position);
		Assert.Equal(PoseAt(200).Position, history.Sample(400).Position);

		// A vehicle that has just appeared has only its first pose.
		var fresh = new PoseHistory();
		fresh.Record(50, PoseAt(50));
		Assert.Equal(PoseAt(50).Position, fresh.Sample(30).Position);
		Assert.Equal(default(CrewPose).Position, new PoseHistory().Sample(30).Position);
	}

	[Fact]
	public void TheViewTick_IsLatencyPlusBufferPlusTheInterpolationDelay_Behind()
	{
		// At 100 ms RTT, three ticks each way and a three-tick buffer: 3 + 3 + 6 = 12 ticks,
		// which at 18 m/s is 3.6 m.
		Assert.Equal(988u, PoseHistory.ViewTick(1000, 3, 3, remoteClient: true));
		Assert.Equal(3.6f, (1000 - PoseHistory.ViewTick(1000, 3, 3, true)) * SimConfig.TickDelta * 18f, 4);
		Assert.Equal(1000u - 6u, PoseHistory.ViewTick(1000, 0, 0, true));
	}

	[Fact]
	public void TheViewTick_IsClampedToTheRing()
	{
		uint deepest = (uint)(PoseHistory.Capacity - 1);
		Assert.Equal(1000u - deepest, PoseHistory.ViewTick(1000, 15, 30, true));
		Assert.Equal(1000u - deepest, PoseHistory.ViewTick(1000, int.MaxValue, int.MaxValue, true));
		Assert.Equal(0u, PoseHistory.ViewTick(5, 3, 3, true));

		// A lying client's negative latency is none.
		Assert.Equal(1000u - 6u, PoseHistory.ViewTick(1000, -50, -50, true));
	}

	[Fact]
	public void ASeatWithNoRemoteClient_SeesTheServersTick()
	{
		Assert.Equal(1000u, PoseHistory.ViewTick(1000, 3, 3, remoteClient: false));
		Assert.Equal(1000u, PoseHistory.ViewTick(1000, 15, 30, remoteClient: false));
	}

	[Fact]
	public void TheViewTicksPose_IsAlwaysInTheRing()
	{
		var history = new PoseHistory();
		for (uint tick = 1; tick <= 500; tick++)
		{
			history.Record(tick, PoseAt(tick));
			for (int lag = 0; lag <= 40; lag += 5)
			{
				uint view = PoseHistory.ViewTick(tick, lag, lag, true);
				Assert.True(history.TryGet(view, out _) || view == 0, $"tick {tick}, lag {lag}: view {view} not held");
			}
		}
	}
}
