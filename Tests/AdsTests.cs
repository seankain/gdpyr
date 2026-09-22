using System;
using Gdpyr.Sim;
using Xunit;

namespace Gdpyr.Tests;

/// <summary>
/// The raise, the zoom it comes to, and the sensitivity that has to move with it
/// (<see cref="Ads"/>). Engine-free like the rest of the simulation: what a scope
/// does is arithmetic over recorded input, and needs neither a camera nor a Godot
/// install to answer a question about.
/// </summary>
public class AdsTests
{
	private static readonly AimStats Scope = new(magnification: 4f, raiseTicks: 20, isScoped: true);
	private static readonly AimStats Irons = new(magnification: 1.25f, raiseTicks: 10, isScoped: false);

	private static InputContext Frame(InputButtons buttons, InputButtons previous = InputButtons.None) =>
		new(InputFrame.Create(0u, 0f, 0f, 0f, 0f, buttons), (ushort)previous, SimConfig.TickDelta);

	private static AimState Raise(in AimStats stats, int ticks, AimState from = default)
	{
		AimState state = from;
		for (int i = 0; i < ticks; i++)
		{
			Ads.Step(ref state, stats, Frame(InputButtons.Ads));
		}
		return state;
	}

	private static AimState Lower(in AimStats stats, int ticks, AimState from)
	{
		AimState state = from;
		for (int i = 0; i < ticks; i++)
		{
			Ads.Step(ref state, stats, Frame(InputButtons.None));
		}
		return state;
	}

	// ---- the raise ---------------------------------------------------------

	[Fact]
	public void HoldingAimRaisesTheSightsOneTickAtATime()
	{
		AimState state = default;
		for (int tick = 1; tick <= Scope.RaiseTicks; tick++)
		{
			Ads.Step(ref state, Scope, Frame(InputButtons.Ads));
			Assert.Equal(tick / (float)Scope.RaiseTicks, Ads.Progress(state, Scope), 5);
		}
	}

	[Fact]
	public void TheRaiseStopsAtFullyAimedHoweverLongItIsHeld()
	{
		AimState state = Raise(Scope, Scope.RaiseTicks * 3);

		Assert.Equal(Scope.RaiseTicks, state.Ticks);
		Assert.Equal(1f, Ads.Progress(state, Scope));
	}

	[Fact]
	public void ReleasingLowersTheSightsAtTheRateTheyWentUp()
	{
		AimState up = Raise(Scope, Scope.RaiseTicks);

		Assert.Equal(0.5f, Ads.Progress(Lower(Scope, Scope.RaiseTicks / 2, up), Scope), 5);
		Assert.Equal(0f, Ads.Progress(Lower(Scope, Scope.RaiseTicks, up), Scope));
	}

	[Fact]
	public void TheSightsNeverGoBelowTheHip()
	{
		Assert.Equal(0, Lower(Scope, 50, default).Ticks);
	}

	[Fact]
	public void AWeaponWithNothingToRaiseNeverAims()
	{
		// The hammer, the mounted guns, and anything left at a magnification of one.
		AimState state = Raise(AimStats.None, 120);

		Assert.Equal(0, state.Ticks);
		Assert.Equal(0f, Ads.Progress(state, AimStats.None));
		Assert.False(AimStats.None.CanAim);
	}

	[Fact]
	public void PuttingTheWeaponAwayPutsTheSightsAwayWithIt()
	{
		// What mounting a heavy gun or switching to the hammer does: there is nothing
		// left to look through, so there is nothing to lower gradually either.
		AimState state = Raise(Scope, Scope.RaiseTicks);
		Ads.Step(ref state, AimStats.None, Frame(InputButtons.Ads));

		Assert.Equal(0, state.Ticks);
	}

	[Fact]
	public void SwappingMidRaiseKeepsHowFarUpTheSightsAreAndNotHowManyTicks()
	{
		// Fully up behind a 20-tick optic, then a 10-tick weapon: still fully up, and
		// still ten ticks from the hip rather than twenty.
		AimState state = Raise(Scope, Scope.RaiseTicks);

		Ads.Step(ref state, Irons, Frame(InputButtons.Ads));
		Assert.Equal(1f, Ads.Progress(state, Irons));

		Assert.Equal(0f, Ads.Progress(Lower(Irons, Irons.RaiseTicks, state), Irons));
	}

	[Fact]
	public void TheSameFramesAlwaysProduceTheSameSights()
	{
		// The whole reason this is a pure step: the server runs it over the frames it
		// received and the owning client over the frames it sent (docs/NETCODE.md §3.1).
		AimState server = default;
		AimState client = default;

		for (int tick = 0; tick < 200; tick++)
		{
			InputContext frame = Frame(tick % 7 < 4 ? InputButtons.Ads : InputButtons.Fire);
			Ads.Step(ref server, Scope, frame);
			Ads.Step(ref client, Scope, frame);
		}

		Assert.Equal(server.Ticks, client.Ticks);
	}

	[Fact]
	public void ARaiseIsAtLeastOneTickHoweverItIsAuthored()
	{
		// A weapon authored at zero seconds is instant, not un-aimable: the byte the
		// progress is counted in has to divide by something.
		var instant = new AimStats(2f, WeaponStats.SecondsToTicks(0f), isScoped: false);

		Assert.Equal(1, instant.RaiseTicks);
		Assert.Equal(1f, Ads.Progress(Raise(instant, 1), instant));
	}

	[Fact]
	public void ARaiseIsNeverLongerThanTheByteItIsCountedIn()
	{
		Assert.Equal(Ads.MaxRaiseTicks, new AimStats(2f, 10_000, isScoped: false).RaiseTicks);
	}

	// ---- what the raise is worth -------------------------------------------

	[Fact]
	public void MagnificationArrivesOverTheRaiseRatherThanAtTheEndOfIt()
	{
		Assert.Equal(1f, Ads.Magnification(Scope, 0f), 5);
		Assert.Equal(2.5f, Ads.Magnification(Scope, 0.5f), 5);
		Assert.Equal(4f, Ads.Magnification(Scope, 1f), 5);
	}

	[Fact]
	public void AimedFieldOfViewIsTheHipTangentDividedByTheMagnification()
	{
		const float hip = 75f;
		float expected = 2f * MathF.Atan(MathF.Tan(hip * 0.5f * MathF.PI / 180f) / 4f) * 180f / MathF.PI;

		Assert.Equal(expected, Ads.FovDegrees(hip, Scope, 1f), 4);
		Assert.Equal(hip, Ads.FovDegrees(hip, Scope, 0f), 4);
	}

	[Fact]
	public void TheViewOnlyEverNarrowsAsTheSightsComeUp()
	{
		float previous = Ads.FovDegrees(75f, Scope, 0f);

		for (int step = 1; step <= 20; step++)
		{
			float fov = Ads.FovDegrees(75f, Scope, step / 20f);
			Assert.True(fov < previous, $"field of view did not narrow at {step}/20");
			previous = fov;
		}
	}

	[Fact]
	public void AWeaponThatDoesNotAimLeavesTheFieldOfViewAlone()
	{
		Assert.Equal(75f, Ads.FovDegrees(75f, AimStats.None, 1f));
	}

	[Fact]
	public void SensitivityIsDividedByWhateverMagnificationIsInForce()
	{
		// A pixel of mouse travel has to be worth the same distance on screen at
		// every power, or a four-power scope turns four times too fast.
		Assert.Equal(1f, Ads.LookScale(Scope, 0f), 5);
		Assert.Equal(0.25f, Ads.LookScale(Scope, 1f), 5);
		Assert.Equal(1f / 1.25f, Ads.LookScale(Irons, 1f), 5);
		Assert.Equal(1f, Ads.LookScale(AimStats.None, 1f), 5);
	}

	[Fact]
	public void SensitivityAndZoomAreTheSameRatio()
	{
		// The look scale is only correct if magnification means the ratio of the two
		// tangents, which is the definition FovDegrees is written to.
		const float hip = 75f;

		for (int step = 0; step <= 10; step++)
		{
			float progress = step / 10f;
			float tanHip = MathF.Tan(hip * 0.5f * MathF.PI / 180f);
			float tanAimed = MathF.Tan(Ads.FovDegrees(hip, Scope, progress) * 0.5f * MathF.PI / 180f);

			Assert.Equal(tanAimed / tanHip, Ads.LookScale(Scope, progress), 4);
		}
	}
}
