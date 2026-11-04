using System;
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
	public void TheViewTick_IsLatencyPlusBufferPlusTheInterpolationDelay_PlusTheStepBetweenDrawingAndSampling()
	{
		// At 100 ms RTT, three ticks each way and a three-tick buffer: 3 + 3 + ½ + 6 + 1 =
		// 13.5 ticks, which at 18 m/s is 4.05 m.
		Assert.Equal(986.5f, PoseHistory.ViewTick(1000, 3, 3f, remoteClient: true), 4);
		Assert.Equal(4.05f, (1000 - PoseHistory.ViewTick(1000, 3, 3f, true)) * SimConfig.TickDelta * 18f, 4);
		Assert.Equal(1000f - 7.5f, PoseHistory.ViewTick(1000, 0, 0f, true), 4);

		// The buffer's depth is smoothed, so a fraction of a frame is a fraction of a tick.
		Assert.Equal(986.9f, PoseHistory.ViewTick(1000, 3, 2.6f, true), 4);
	}

	[Fact]
	public void TheViewTick_IsClampedToTheRing()
	{
		float deepest = PoseHistory.Capacity - 1;
		Assert.Equal(1000f - deepest, PoseHistory.ViewTick(1000, 15, 30f, true));
		Assert.Equal(1000f - deepest, PoseHistory.ViewTick(1000, int.MaxValue, float.MaxValue, true));
		Assert.Equal(0f, PoseHistory.ViewTick(5, 3, 3f, true));

		// A lying client's negative latency or depth is none, and a depth that is not a number too.
		Assert.Equal(1000f - 7.5f, PoseHistory.ViewTick(1000, -50, -50f, true), 4);
		Assert.Equal(1000f - 10.5f, PoseHistory.ViewTick(1000, 3, float.NaN, true), 4);
	}

	[Fact]
	public void ASeatWithNoRemoteClient_SeesTheServersTick()
	{
		Assert.Equal(1000f, PoseHistory.ViewTick(1000, 3, 3f, remoteClient: false));
		Assert.Equal(1000f, PoseHistory.ViewTick(1000, 15, 30f, remoteClient: false));
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
				float view = PoseHistory.ViewTick(tick, lag, lag * 0.7f, true);
				uint below = (uint)MathF.Floor(view);
				Assert.True(history.TryGet(below, out _) || below == 0, $"tick {tick}, lag {lag}: view {view} not held");
			}
		}
	}

	[Fact]
	public void BetweenTwoRecordedTicks_ThePoseIsInterpolated_AsAClientDrawsIt()
	{
		var history = new PoseHistory();
		history.Record(100, new CrewPose(new Vector3(0f, 1f, 0f), 3.1f, 0.1f, -0.2f));
		history.Record(101, new CrewPose(new Vector3(0.3f, 1f, 0f), -3.1f, 0.3f, 0f));

		CrewPose quarter = history.Sample(100.25f);
		Assert.Equal(0.075f, quarter.Position.X, 4);
		Assert.Equal(0.15f, quarter.Pitch, 4);
		Assert.Equal(-0.15f, quarter.Roll, 4);

		// The heading the short way round, across ±π.
		Assert.True(MathF.Abs(Mathf.AngleDifference(3.1f, quarter.Yaw)) < 0.03f, $"yaw {quarter.Yaw}");

		// A whole tick is that tick's pose; past the newest, the newest.
		Assert.Equal(0f, history.Sample(100f).Position.X);
		Assert.Equal(0.3f, history.Sample(101.5f).Position.X, 4);
		Assert.Equal(0f, history.Sample(-3f).Position.X);
	}

	[Fact]
	public void WhatTheClientSaysItDrew_IsTheViewTick_ClampedToTheRing()
	{
		Assert.Equal(984.75f, PoseHistory.ViewTick(1000, 984.75f, 3, 3f, remoteClient: true));

		// Never later than the server's own tick, nor older than the ring holds.
		Assert.Equal(1000f, PoseHistory.ViewTick(1000, 1004f, 3, 3f, true));
		Assert.Equal(1000f - (PoseHistory.Capacity - 1), PoseHistory.ViewTick(1000, 500f, 3, 3f, true));
		Assert.Equal(0f, PoseHistory.ViewTick(5, -2f, 3, 3f, true));

		// Not said: the estimate; no client across a link: the server's tick, whatever is said.
		Assert.Equal(PoseHistory.ViewTick(1000, 3, 3f, true), PoseHistory.ViewTick(1000, float.NaN, 3, 3f, true));
		Assert.Equal(1000f, PoseHistory.ViewTick(1000, 984.75f, 3, 3f, remoteClient: false));
	}
}
