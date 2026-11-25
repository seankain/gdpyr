using System;
using Gdpyr.Sim;
using Xunit;

namespace Gdpyr.Tests;

/// <summary>
/// What an animated viewmodel shows (Scripts/Sim/ViewmodelAnimation.cs), from states the weapon
/// step itself produces: the launcher's fire clip from the tick its round left and for as long
/// as the clip is, its reload placed by where the drawn tick is in the reload's span, the reload
/// over the recoil, nothing for a weapon just drawn or a count that went back to zero; and where
/// the camera has to draw a muzzle flash for it to sit on a barrel drawn at another field of view.
/// </summary>
public class ViewmodelAnimationTests
{
	private const byte LauncherId = 6;
	private const float FireSeconds = 0.6f;

	/// <summary>The launcher as Weapons/launcher/launcher.tres has it: 40 RPM, one round, 2.5 s to reload.</summary>
	private static readonly WeaponStats Launcher =
		new(FireMode.Single, WeaponStats.TicksPerShot(40f), magazineSize: 1, reloadTicks: WeaponStats.SecondsToTicks(2.5f),
			damage: 0f);

	private static InputContext Pull(bool held, bool wasHeld) =>
		new(InputFrame.Create(0, 0f, 0f, 0f, 0f, held ? InputButtons.Fire : InputButtons.None),
			(ushort)(wasHeld ? InputButtons.Fire : InputButtons.None), SimConfig.TickDelta);

	private static WeaponState Loaded() => WeaponState.Ready(LauncherId, Launcher, reserve: 4);

	/// <summary>Pulls the trigger on <paramref name="tick"/>, the step the server and the owner both run.</summary>
	private static WeaponAction PullOn(ref WeaponState state, uint tick) =>
		WeaponSim.Step(ref state, Launcher, Pull(held: true, wasHeld: false), tick);

	[Fact]
	public void AWeaponJustDrawn_IsIdle()
	{
		var animator = new ViewmodelAnimator();
		WeaponState state = Loaded();

		Assert.Equal(ViewmodelClip.Idle, animator.Step(state, Launcher, 10.0, FireSeconds).Clip);
	}

	[Fact]
	public void AWeaponDrawnWithRoundsAlreadyFired_DoesNotPlayItsRecoil()
	{
		WeaponState state = Loaded();
		Assert.Equal(WeaponAction.Fire, PullOn(ref state, 100));

		var animator = new ViewmodelAnimator();
		Assert.Equal(ViewmodelClip.Idle, animator.Step(state, Launcher, 100.5, FireSeconds).Clip);
	}

	[Fact]
	public void AShot_PlaysTheFireClipFromItsOwnTick()
	{
		var animator = new ViewmodelAnimator();
		WeaponState state = Loaded();
		animator.Step(state, Launcher, 99.0, FireSeconds);

		Assert.Equal(WeaponAction.Fire, PullOn(ref state, 100));

		// Seen a few ticks late, as a slow frame would: the clip is where the shot has got to.
		ViewmodelFrame frame = animator.Step(state, Launcher, 106.0, FireSeconds);
		Assert.Equal(ViewmodelClip.Fire, frame.Clip);
		Assert.Equal(6.0 / SimConfig.TickRate / FireSeconds, frame.Progress, 4);
	}

	[Fact]
	public void TheFireClip_EndsWhenItsLengthHasPassed()
	{
		var animator = new ViewmodelAnimator();
		WeaponState state = Loaded();
		animator.Step(state, Launcher, 99.0, FireSeconds);
		PullOn(ref state, 100);

		double justBefore = 100 + FireSeconds * SimConfig.TickRate - 0.5;
		Assert.Equal(ViewmodelClip.Fire, animator.Step(state, Launcher, justBefore, FireSeconds).Clip);
		Assert.Equal(ViewmodelClip.Idle, animator.Step(state, Launcher, justBefore + 1.0, FireSeconds).Clip);
	}

	[Fact]
	public void TheReload_IsPlacedByWhereTheDrawnTickIsInIt()
	{
		var animator = new ViewmodelAnimator();
		WeaponState state = Loaded();
		animator.Step(state, Launcher, 99.0, FireSeconds);
		PullOn(ref state, 100);

		// The launcher reloads itself on the next pull, once its cadence allows one.
		uint next = state.NextFireTick;
		Assert.Equal(WeaponAction.ReloadStart, PullOn(ref state, next));
		Assert.True(state.IsReloading);

		Assert.Equal(0f, animator.Step(state, Launcher, next, FireSeconds).Progress, 4);
		ViewmodelFrame half = animator.Step(state, Launcher, next + Launcher.ReloadTicks / 2.0, FireSeconds);
		Assert.Equal(ViewmodelClip.Reload, half.Clip);
		Assert.Equal(0.5f, half.Progress, 4);
		Assert.Equal(1f, animator.Step(state, Launcher, state.ReloadEndTick + 3.0, FireSeconds).Progress, 4);
	}

	[Fact]
	public void TheReload_IsShownOverTheRecoil()
	{
		var animator = new ViewmodelAnimator();
		WeaponState state = Loaded();
		animator.Step(state, Launcher, 99.0, FireSeconds);
		PullOn(ref state, 100);

		// A reload begun while the shot's clip would still be running.
		state.ReloadEndTick = 101 + (uint)Launcher.ReloadTicks;

		Assert.Equal(ViewmodelClip.Reload, animator.Step(state, Launcher, 102.0, FireSeconds).Clip);
	}

	[Fact]
	public void ARespawnedWeapon_IsNotFiredByItsCountStartingAgain()
	{
		var animator = new ViewmodelAnimator();
		WeaponState state = Loaded();
		animator.Step(state, Launcher, 99.0, FireSeconds);
		PullOn(ref state, 100);
		animator.Step(state, Launcher, 200.0, FireSeconds);

		state = Loaded();
		Assert.Equal(ViewmodelClip.Idle, animator.Step(state, Launcher, 201.0, FireSeconds).Clip);
	}

	[Fact]
	public void SwitchingWeapons_AdoptsTheOtherWeaponsCountSilently()
	{
		var animator = new ViewmodelAnimator();
		WeaponState launcher = Loaded();
		animator.Step(launcher, Launcher, 99.0, FireSeconds);

		WeaponState other = WeaponState.Ready(1, Launcher);
		other.ShotIndex = 7;
		Assert.Equal(ViewmodelClip.Idle, animator.Step(other, Launcher, 100.0, FireSeconds).Clip);
	}

	[Theory]
	[InlineData(75f, 55f, 1.4740f)]
	[InlineData(55f, 55f, 1f)]
	[InlineData(65.1929f, 55f, 1.2284f)] // the launcher's sights up: 75 degrees magnified 1.2 times (Ads.FovDegrees)
	public void TheSpread_IsTheRatioOfTheHalfAnglesTangents(float camera, float viewmodel, float expected)
	{
		Assert.Equal(expected, ViewmodelOptics.Spread(camera, viewmodel), 3);
	}

	[Theory]
	[InlineData(0f, 55f)]
	[InlineData(75f, 0f)]
	[InlineData(180f, 55f)]
	public void ANonsenseFieldOfView_SpreadsNothing(float camera, float viewmodel)
	{
		Assert.Equal(1f, ViewmodelOptics.Spread(camera, viewmodel));
	}

	[Fact]
	public void TheSpread_PutsAPointWhereTheOtherLensDrawsIt()
	{
		// A point 0.2 m right of the line of sight, 0.7 m ahead, as each lens puts it on screen.
		float viewmodel = 55f;
		float camera = 75f;
		float x = 0.2f;
		float z = 0.7f;
		float drawn = x / z / MathF.Tan(viewmodel * MathF.PI / 360f);
		float moved = x * ViewmodelOptics.Spread(camera, viewmodel) / z / MathF.Tan(camera * MathF.PI / 360f);
		Assert.Equal(drawn, moved, 5);
	}
}
