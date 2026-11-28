using System;
using System.Collections.Generic;
using System.Linq;
using Gdpyr.Dev;
using Godot;
using Xunit;

namespace Gdpyr.Tests;

/// <summary>
/// The unit stress test's engine-free half (Scripts/Dev/UnitStressRules.cs, docs/UNIT_STRESS_TEST.md):
/// where a wave stands round the barracks, what a second of frames measures, and how the ramp
/// steps, measures and stops.
/// </summary>
public class UnitStressRulesTests
{
	// ---- the rings ----------------------------------------------------------------------------

	[Theory]
	[InlineData(0.4f, 1.8f)] // a rifleman
	[InlineData(1.3f, 3.6f)] // a tank
	[InlineData(0.3f, 1.6f)] // a scout drone: never under a barracks row's spacing
	[InlineData(0f, 1.6f)]
	public void Spacing_IsTwoBodiesAndAMetre_NeverUnderABarracksRow(float radius, float expected)
	{
		Assert.Equal(expected, StressRings.SpacingFor(radius), 3);
	}

	[Fact]
	public void TheFirstRing_IsAtTheInnerRadius_AndHasItsCountOfSpots()
	{
		var centre = new Vector3(0f, 0f, -10f);
		int first = StressRings.SpotsOn(0, 8.5f, 1.8f);
		List<Vector3> spots = StressRings.Spots(centre, 8.5f, 1.8f, 1).ToList();

		Assert.Equal(first, spots.Count);
		Assert.Equal((int)MathF.Floor(MathF.Tau * 8.5f / 1.8f), first);
		Assert.All(spots, spot =>
		{
			Assert.Equal(8.5f, new Vector2(spot.X - centre.X, spot.Z - centre.Z).Length(), 3);
			Assert.Equal(centre.Y, spot.Y);
		});
	}

	[Theory]
	[InlineData(0.4f)]
	[InlineData(1.3f)]
	public void NoTwoSpots_AreCloserThanTheSpacing(float radius)
	{
		float spacing = StressRings.SpacingFor(radius);
		List<Vector3> spots = StressRings.Spots(Vector3.Zero, 8.5f, spacing, 6).ToList();

		for (int i = 0; i < spots.Count; i++)
		{
			for (int j = i + 1; j < spots.Count; j++)
			{
				float apart = new Vector2(spots[i].X - spots[j].X, spots[i].Z - spots[j].Z).Length();
				Assert.True(apart >= spacing * 0.99f, $"spots {i} and {j} are {apart:0.00} m apart, under {spacing:0.00}");
			}
		}
	}

	[Fact]
	public void TheFieldsWholeLimit_OfRiflemen_FitsInTwoRings_AndOfTanksInFour_ClearOfTheBuilding()
	{
		// The barracks is an 8 x 8 m box: its corner is 5.66 m from its centre.
		float rifle = StressRings.SpacingFor(0.4f);
		List<Vector3> riflemen = StressRings.Spots(Vector3.Zero, 8.5f, rifle, 2).ToList();
		Assert.Equal(64, riflemen.Count);
		Assert.All(riflemen, spot => Assert.True(new Vector2(spot.X, spot.Z).Length() - 0.4f > MathF.Sqrt(32f)));
		Assert.Equal(8.5f + rifle, riflemen.Max(spot => new Vector2(spot.X, spot.Z).Length()), 2);

		float tank = StressRings.SpacingFor(1.3f);
		Assert.True(StressRings.Spots(Vector3.Zero, 8.5f, tank, 3).Count() < 64);
		List<Vector3> tanks = StressRings.Spots(Vector3.Zero, 8.5f, tank, 4).ToList();
		Assert.True(tanks.Count >= 64, $"four rings hold {tanks.Count}");
		Assert.All(tanks, spot => Assert.True(new Vector2(spot.X, spot.Z).Length() - 1.3f > MathF.Sqrt(32f)));
	}

	[Fact]
	public void ASmallRing_HasAtLeastSixSpots()
	{
		Assert.Equal(StressRings.MinPerRing, StressRings.SpotsOn(0, 0.5f, 3.6f));
	}

	[Fact]
	public void ASpot_IsClear_OnlyOfEverybodyFurtherThanTheClearance()
	{
		var taken = new List<Vector3> { new(0f, 0f, 0f), new(5f, 3f, 0f) };

		Assert.True(StressRings.IsClear(new Vector3(2f, 0f, 0f), taken, 1.8f));
		Assert.False(StressRings.IsClear(new Vector3(1f, 0f, 0f), taken, 1.8f));

		// Measured flat: a unit standing above the spot is still in the way.
		Assert.False(StressRings.IsClear(new Vector3(5f, 0f, 1f), taken, 1.8f));
		Assert.True(StressRings.IsClear(Vector3.Zero, new List<Vector3>(), 1.8f));
	}

	// ---- a second of frames ---------------------------------------------------------------------

	[Fact]
	public void AnEmptyWindow_SaysNothing()
	{
		var window = new FrameWindow(1f);

		Assert.Equal(0f, window.MeanMs);
		Assert.Equal(0f, window.Fps);
		Assert.Equal(0f, window.P95Ms);
		Assert.Equal(0f, window.WorstMs);
	}

	[Fact]
	public void TheWindow_MeasuresMeanPercentileAndWorst()
	{
		var window = new FrameWindow(10f);
		for (int i = 1; i <= 100; i++)
		{
			window.Add(i);
		}

		Assert.Equal(50.5f, window.MeanMs, 3);
		Assert.Equal(1000f / 50.5f, window.Fps, 3);
		Assert.Equal(95f, window.P95Ms);
		Assert.Equal(100f, window.WorstMs);
		Assert.Equal(1f, window.Percentile(0f));
	}

	[Fact]
	public void TheWindow_IsTime_NotFrames()
	{
		var window = new FrameWindow(1f);
		for (int i = 0; i < 200; i++)
		{
			window.Add(10f);
		}

		// 100 frames of 10 ms are the second; a slow frame then pushes out the frames it outlasts.
		Assert.Equal(100, window.Count);
		window.Add(500f);
		Assert.Equal(51, window.Count);
		Assert.Equal(500f, window.WorstMs);
		Assert.Equal(1000f / 51f, window.MeanMs, 2);
	}

	[Fact]
	public void ACost_ThatIsNotATime_IsNothing()
	{
		var costs = new FrameCosts(float.NaN, -3f, float.PositiveInfinity);

		Assert.Equal(0f, costs.UnitPassMs);
		Assert.Equal(0f, costs.PhysicsWorstMs);
		Assert.Equal(0f, costs.IdleWorstMs);
	}

	[Fact]
	public void ARampStep_AveragesTheUnitPass_AndKeepsTheWorstTicks_OfItsMeasuredFrames()
	{
		var ramp = new StressRamp(step: 8, limit: 64, stepSeconds: 2f, settleSeconds: 1f, fpsFloor: 0f);
		ramp.Start();

		// The settle's spike — a wave's paths planned — is not the step's worst.
		for (int i = 0; i < 8; i++)
		{
			ramp.Sample(125f, 8, new FrameCosts(9f, 80f, 90f));
		}

		// Then a second of 125 ms frames: the unit pass 1 then 3 ms, one slow tick among them.
		for (int i = 0; i < 8; i++)
		{
			ramp.Sample(125f, 8, new FrameCosts(i < 4 ? 1f : 3f, i == 5 ? 21f : 6f, i == 2 ? 14f : 4f));
		}

		RampStep step = Assert.Single(ramp.Steps);
		Assert.Equal(8, step.Frames);
		Assert.Equal(2f, step.MeanUnitPassMs, 3);
		Assert.Equal(21f, step.WorstPhysicsMs);
		Assert.Equal(14f, step.WorstIdleMs);
		Assert.Contains("worst physics tick  21.00 ms", step.ToString());

		// The next step starts from nothing.
		for (int i = 0; i < 16; i++)
		{
			ramp.Sample(125f, 16, new FrameCosts(1f, 5f, 5f));
		}

		Assert.Equal(2, ramp.Steps.Count);
		Assert.Equal(5f, ramp.Steps[1].WorstPhysicsMs);
		Assert.Equal(1f, ramp.Steps[1].MeanUnitPassMs, 3);
	}

	[Fact]
	public void TheWindow_IgnoresWhatIsNotAFrameTime()
	{
		var window = new FrameWindow(1f);
		window.Add(float.NaN);
		window.Add(-1f);
		window.Add(float.PositiveInfinity);

		Assert.Equal(0, window.Count);
	}

	// ---- the ramp -------------------------------------------------------------------------------

	/// <summary>Feeds the ramp frames of <paramref name="frameMs"/> until its current step ends; the frames fed.</summary>
	private static int RunStep(StressRamp ramp, float frameMs, int alive)
	{
		for (int frames = 1; frames < 1_000_000; frames++)
		{
			if (ramp.Sample(frameMs, alive))
			{
				return frames;
			}
		}

		throw new InvalidOperationException("the step never ended");
	}

	[Fact]
	public void TheRamp_StartsAtOneStep_AndRisesByAStepEachInterval()
	{
		var ramp = new StressRamp(step: 8, limit: 64, stepSeconds: 5f, settleSeconds: 2f, fpsFloor: 30f);
		Assert.Equal(0, ramp.Target);
		Assert.False(ramp.Running);

		ramp.Start();
		Assert.Equal(8, ramp.Target);
		Assert.True(ramp.Running);

		int frames = RunStep(ramp, 10f, 8);
		Assert.InRange(frames, 499, 501);
		Assert.Equal(16, ramp.Target);
		Assert.Single(ramp.Steps);
		Assert.Equal(8, ramp.Steps[0].Target);
		Assert.Equal(100f, ramp.Steps[0].Fps, 2);
		Assert.Equal(8f, ramp.Steps[0].MeanAlive);
	}

	[Fact]
	public void TheSettle_IsNotMeasured()
	{
		var ramp = new StressRamp(step: 4, limit: 64, stepSeconds: 5f, settleSeconds: 2f, fpsFloor: 0f);
		ramp.Start();

		// Two seconds of 125 ms frames (exact in binary) while the wave settles, then 10 ms frames.
		for (int i = 0; i < 16; i++)
		{
			Assert.False(ramp.Measuring);
			ramp.Sample(125f, 0);
		}

		Assert.True(ramp.Measuring);
		RunStep(ramp, 10f, 4);

		RampStep step = ramp.Steps[0];
		Assert.Equal(10f, step.MeanMs, 2);
		Assert.Equal(10f, step.WorstMs, 2);
		Assert.Equal(4f, step.MeanAlive);
		Assert.InRange(step.Frames, 299, 301);
	}

	[Fact]
	public void TheRamp_Stops_AtTheFirstStepUnderTheFloor()
	{
		var ramp = new StressRamp(step: 10, limit: 64, stepSeconds: 2f, settleSeconds: 0.5f, fpsFloor: 50f);
		ramp.Start();

		RunStep(ramp, 10f, 10); // 100 fps
		RunStep(ramp, 16f, 20); // 62.5 fps
		RunStep(ramp, 25f, 30); // 40 fps: under the floor

		Assert.False(ramp.Running);
		Assert.True(ramp.Finished);
		Assert.Equal(3, ramp.Steps.Count);
		Assert.Equal(30, ramp.Target);
		Assert.Equal(20, ramp.HeldTarget);
		Assert.Contains("under the 50 fps floor", ramp.StopReason);
		Assert.False(ramp.Sample(10f, 30));
	}

	[Fact]
	public void TheRamp_Stops_AfterTheStepAtTheLimit_WhichIsNotOvershot()
	{
		var ramp = new StressRamp(step: 24, limit: 64, stepSeconds: 1f, settleSeconds: 0f, fpsFloor: 30f);
		ramp.Start();

		RunStep(ramp, 10f, 24);
		Assert.Equal(48, ramp.Target);
		RunStep(ramp, 10f, 48);
		Assert.Equal(64, ramp.Target);
		RunStep(ramp, 10f, 64);

		Assert.True(ramp.Finished);
		Assert.Equal(new[] { 24, 48, 64 }, ramp.Steps.Select(step => step.Target));
		Assert.Equal(64, ramp.HeldTarget);
		Assert.Contains("limit", ramp.StopReason);
	}

	[Fact]
	public void Stopping_KeepsTheStepsMeasured()
	{
		var ramp = new StressRamp(step: 8, limit: 64, stepSeconds: 1f, settleSeconds: 0f, fpsFloor: 30f);
		ramp.Start();
		RunStep(ramp, 10f, 8);
		ramp.Sample(10f, 16);

		ramp.Stop("stopped from the panel");

		Assert.True(ramp.Finished);
		Assert.Single(ramp.Steps);
		Assert.Equal("stopped from the panel", ramp.StopReason);
		Assert.Equal(8, ramp.HeldTarget);
	}

	[Fact]
	public void TheRampsNumbers_ArePulledIntoRange()
	{
		var ramp = new StressRamp(step: 0, limit: 0, stepSeconds: 0f, settleSeconds: 10f, fpsFloor: -5f);

		Assert.Equal(1, ramp.Step);
		Assert.Equal(1, ramp.Limit);
		Assert.Equal(1f, ramp.StepSeconds);
		Assert.True(ramp.SettleSeconds < ramp.StepSeconds);
		Assert.Equal(0f, ramp.FpsFloor);
	}
}
