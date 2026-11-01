using System;
using Gdpyr.Sim;
using Godot;
using Xunit;

namespace Gdpyr.Tests;

/// <summary>
/// A humanoid body's ground speed (Scripts/Sim/BodyAnimation.cs), measured from where it
/// is drawn: bodies move on the 60 Hz tick and are drawn at whatever the display runs at,
/// so the speed, and the stride it picks, must not depend on the frame rate.
/// </summary>
public class GroundSpeedMeterTests
{
	private const float Tick = 1f / 60f;

	private static readonly BodyClipTimes Times = new(jumpStart: 0.89f, land: 0.84f, shoot: 0.63f, @throw: 1.1f,
		death: 2.4f);

	/// <summary>
	/// Godot's main loop: each frame runs the ticks its time has come to, which move the
	/// body at <paramref name="speed"/> along X, then draws — and the rig measures and poses.
	/// Frame times vary by <paramref name="jitter"/> either way, and one frame in fifty
	/// takes a thirtieth of a second.
	/// </summary>
	private static (float Speed, int[] Shown) Play(float speed, float frameRate, float seconds, float jitter = 0f,
		bool sprinting = false)
	{
		var random = new Random(7);
		var meter = new GroundSpeedMeter(Tick, Vector3.Zero, 0);
		var animator = new BodyAnimator(Times);
		var shown = new int[Enum.GetValues<BodyClip>().Length];
		var position = Vector3.Zero;
		ulong ticks = 0;
		float pending = 0f;
		for (float t = 0f; t < seconds;)
		{
			float frame = 1f / frameRate * (1f + (jitter * (((float)random.NextDouble() * 2f) - 1f)));
			if (jitter > 0f && random.Next(50) == 0)
			{
				frame = 1f / 30f;
			}

			pending += frame;
			while (pending >= Tick - 1e-6f)
			{
				position.X += speed * Tick;
				ticks++;
				pending -= Tick;
			}

			var pose = new BodyPose { GroundSpeed = meter.Observe(position, ticks), Sprinting = sprinting };
			BodyMix mix = animator.Step(pose, frame);
			t += frame;
			if (t > 0.5f)
			{
				shown[(int)mix.Legs]++;
			}
		}

		return (meter.Speed, shown);
	}

	[Theory]
	[InlineData(30f)]
	[InlineData(60f)]
	[InlineData(144f)]
	[InlineData(165f)]
	[InlineData(240f)]
	[InlineData(360f)]
	public void TheSpeed_IsTheBodys_WhateverTheFrameRate(float frameRate)
	{
		foreach (float speed in new[] { 1f, 3.6f, 4.5f, 5f, 8f })
		{
			Assert.Equal(speed, Play(speed, frameRate, 2f).Speed, 2);
		}
	}

	[Theory]
	[InlineData(60f)]
	[InlineData(144f)]
	[InlineData(240f)]
	[InlineData(360f)]
	public void ABodyOnTheMove_RunsAndSprints_OnAFastDisplay(float frameRate)
	{
		// A unit at 4.5 m/s, a player walking at 5 and sprinting at 8: every frame after
		// the first half second, on frame times that wander and hitch.
		foreach ((float speed, bool sprinting, BodyClip expected) in new[]
		{
			(4.5f, false, BodyClip.Run),
			(5f, false, BodyClip.Run),
			(8f, true, BodyClip.Sprint),
		})
		{
			int[] shown = Play(speed, frameRate, 10f, jitter: 0.3f, sprinting: sprinting).Shown;
			int frames = 0;
			foreach (int count in shown)
			{
				frames += count;
			}

			Assert.Equal(frames, shown[(int)expected]);
		}
	}

	[Fact]
	public void AFrameWithNoTick_MeasuresNothing()
	{
		var meter = new GroundSpeedMeter(Tick, Vector3.Zero, 0);
		meter.Observe(new Vector3(5f * Tick, 0f, 0f), 1);
		float speed = meter.Speed;

		Assert.True(speed > 0f);
		Assert.Equal(speed, meter.Observe(new Vector3(5f * Tick, 0f, 0f), 1));
	}

	[Fact]
	public void AStepOverSeveralTicks_IsMeasuredOverAllOfThem()
	{
		var meter = new GroundSpeedMeter(Tick, Vector3.Zero, 0);
		for (ulong tick = 3; tick <= 300; tick += 3)
		{
			meter.Observe(new Vector3(4f * Tick * tick, 0f, 0f), tick);
		}

		Assert.Equal(4f, meter.Speed, 3);
	}

	[Fact]
	public void OnlyTheGround_IsMeasured()
	{
		var meter = new GroundSpeedMeter(Tick, Vector3.Zero, 0);
		for (ulong tick = 1; tick <= 120; tick++)
		{
			meter.Observe(new Vector3(3f * Tick * tick, 10f * Tick * tick, 4f * Tick * tick), tick);
		}

		Assert.Equal(5f, meter.Speed, 3);
	}

	[Fact]
	public void ATeleport_IsNotAStride()
	{
		var meter = new GroundSpeedMeter(Tick, Vector3.Zero, 0);
		for (ulong tick = 1; tick <= 120; tick++)
		{
			meter.Observe(new Vector3(5f * Tick * tick, 0f, 0f), tick);
		}

		float speed = meter.Speed;
		Assert.Equal(speed, meter.Observe(new Vector3(50f, 0f, 0f), 121));
	}

	[Fact]
	public void AHiddenBody_IsMeasuredFromWhereItIsNextSeen()
	{
		var meter = new GroundSpeedMeter(Tick, Vector3.Zero, 0);
		for (ulong tick = 1; tick <= 120; tick++)
		{
			meter.Observe(new Vector3(5f * Tick * tick, 0f, 0f), tick);
		}

		// Hidden for eight seconds, in which it went 12 m: the next tick seen is a step from there.
		meter.Track(new Vector3(22f, 0f, 0f), 600);
		Assert.Equal(5f, meter.Observe(new Vector3(22f + (5f * Tick), 0f, 0f), 601), 3);
	}

	[Fact]
	public void AReset_Stands()
	{
		var meter = new GroundSpeedMeter(Tick, Vector3.Zero, 0);
		for (ulong tick = 1; tick <= 120; tick++)
		{
			meter.Observe(new Vector3(5f * Tick * tick, 0f, 0f), tick);
		}

		meter.Reset(new Vector3(-40f, 0f, 0f), 121);
		Assert.Equal(0f, meter.Speed);
		Assert.Equal(0f, meter.Observe(new Vector3(-40f, 0f, 0f), 122));
	}
}
