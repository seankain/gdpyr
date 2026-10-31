using Gdpyr.Sim;
using Xunit;

namespace Gdpyr.Tests;

/// <summary>
/// How a humanoid body is posed (Scripts/Sim/BodyAnimation.cs), layer by layer: the
/// ground speed picks the legs' stride and the air beats the ground; the arms come up
/// over whatever the legs do while the body aims or has just fired, pointed by the
/// pitch; a throw is the arms' and leaves the legs alone; a death takes everything
/// and holds until a respawn.
/// </summary>
public class BodyAnimatorTests
{
	/// <summary>The rig's times: UAL1_Standard.glb's clip lengths over the rates the rig plays them at.</summary>
	private static readonly BodyClipTimes Times = new(jumpStart: 0.89f, land: 0.84f, shoot: 0.63f, @throw: 1.1f,
		death: 2.4f);

	private const float Frame = 1f / 60f;

	private static BodyPose Moving(float speed) => new() { GroundSpeed = speed };

	/// <summary>Steps <paramref name="seconds"/> of frames and returns the last mix.</summary>
	private static BodyMix Run(BodyAnimator animator, BodyPose pose, float seconds)
	{
		BodyMix mix = animator.Step(pose, Frame);
		for (float t = Frame; t < seconds - 1e-4f; t += Frame)
		{
			mix = animator.Step(pose, Frame);
		}

		return mix;
	}

	// ---- the legs ----------------------------------------------------------

	[Theory]
	[InlineData(0f, BodyClip.Idle)]
	[InlineData(0.2f, BodyClip.Idle)]
	[InlineData(1f, BodyClip.Walk)]
	[InlineData(2.1f, BodyClip.Walk)]
	[InlineData(4.5f, BodyClip.Run)]
	[InlineData(5f, BodyClip.Run)]
	[InlineData(8f, BodyClip.Sprint)]
	public void TheGroundSpeed_PicksTheStride(float speed, BodyClip expected)
	{
		Assert.Equal(expected, new BodyAnimator(Times).Step(Moving(speed), Frame).Legs);
	}

	[Fact]
	public void Sprinting_ShowsTheSprint_AtASpeedAHeavyKitLeaves()
	{
		// The sprint state's 8 m/s at the heaviest kit's 0.70 is 5.6: a jog's speed, a sprint's state.
		var pose = new BodyPose { GroundSpeed = 5.6f, Sprinting = true };
		Assert.Equal(BodyClip.Sprint, new BodyAnimator(Times).Step(pose, Frame).Legs);

		pose.GroundSpeed = 0.1f;
		Assert.Equal(BodyClip.Idle, new BodyAnimator(Times).Step(pose, Frame).Legs);
	}

	[Fact]
	public void Crouched_IsTheCrouch_StillOrMoving()
	{
		var animator = new BodyAnimator(Times);
		Assert.Equal(BodyClip.CrouchIdle, animator.Step(new BodyPose { Crouched = true }, Frame).Legs);
		Assert.Equal(BodyClip.CrouchWalk, animator.Step(new BodyPose { Crouched = true, GroundSpeed = 2f }, Frame).Legs);
	}

	[Fact]
	public void AJump_PushesOff_ThenHangsInTheAir_ThenLands()
	{
		var animator = new BodyAnimator(Times);
		var air = new BodyPose { Airborne = true, Jumped = true };

		Assert.Equal(BodyClip.JumpStart, animator.Step(air, Frame).Legs);
		Assert.Equal(BodyClip.JumpStart, Run(animator, air, 0.5f).Legs);
		Assert.Equal(BodyClip.Airborne, Run(animator, air, 0.5f).Legs);

		Assert.Equal(BodyClip.Land, animator.Step(default, Frame).Legs);
		Assert.Equal(BodyClip.Land, Run(animator, default, 0.5f).Legs);
		Assert.Equal(BodyClip.Idle, Run(animator, default, 0.5f).Legs);
	}

	[Fact]
	public void APushOffAndALanding_EachStartTheLegsAgain()
	{
		var animator = new BodyAnimator(Times);
		int serial = animator.LegsSerial;

		Run(animator, new BodyPose { Airborne = true, Jumped = true }, 0.5f);
		Assert.Equal(serial + 1, animator.LegsSerial);
		Assert.Equal(BodyClip.JumpStart, animator.LegsAction);

		animator.Step(default, Frame);
		Assert.Equal(serial + 2, animator.LegsSerial);
		Assert.Equal(BodyClip.Land, animator.LegsAction);
		Assert.True(animator.IsLegsActing);
	}

	[Fact]
	public void AFall_IsInTheAirAtOnce_WithNoPushOff()
	{
		var animator = new BodyAnimator(Times);
		Assert.Equal(BodyClip.Airborne, animator.Step(new BodyPose { Airborne = true }, Frame).Legs);
	}

	[Fact]
	public void AStepOffAKerb_IsNoLanding()
	{
		var animator = new BodyAnimator(Times);
		Run(animator, new BodyPose { Airborne = true }, BodyAnimator.MinAirSeconds * 0.5f);
		Assert.Equal(BodyClip.Idle, animator.Step(default, Frame).Legs);
	}

	[Fact]
	public void LandingOnTheRun_CarriesOnRunning_AndDropsTheLanding()
	{
		var animator = new BodyAnimator(Times);
		Run(animator, new BodyPose { Airborne = true, GroundSpeed = 5f }, 0.8f);

		Assert.Equal(BodyClip.Run, animator.Step(Moving(5f), Frame).Legs);

		// Stopping straight after does not bring back a landing already given up.
		Assert.Equal(BodyClip.Idle, animator.Step(default, Frame).Legs);
	}

	// ---- the arms, over the legs ---------------------------------------------

	[Theory]
	[InlineData(0f, BodyClip.Idle)]
	[InlineData(1f, BodyClip.Walk)]
	[InlineData(5f, BodyClip.Run)]
	public void AShot_RaisesTheArms_AndTheLegsKeepTheirStride(float speed, BodyClip legs)
	{
		var animator = new BodyAnimator(Times);
		animator.Shoot();

		BodyMix mix = Run(animator, Moving(speed), BodyAnimator.ArmsBlendSeconds);
		Assert.Equal(legs, mix.Legs);
		Assert.Equal(1f, mix.Arms, 3);
		Assert.True(animator.IsArmsActing);
		Assert.Equal(BodyClip.Shoot, animator.ArmsAction);
	}

	[Fact]
	public void CrouchedAndShooting_IsTheCrouchUnderTheAim()
	{
		var animator = new BodyAnimator(Times);
		animator.Shoot();

		BodyMix mix = Run(animator, new BodyPose { Crouched = true, GroundSpeed = 1f }, BodyAnimator.ArmsBlendSeconds);
		Assert.Equal(BodyClip.CrouchWalk, mix.Legs);
		Assert.Equal(1f, mix.Arms, 3);
	}

	[Fact]
	public void TheArms_ComeUpAndGoDown_OverTheBlendTime()
	{
		var animator = new BodyAnimator(Times);
		var aiming = new BodyPose { Aiming = true };

		Assert.Equal(Frame / BodyAnimator.ArmsBlendSeconds, animator.Step(aiming, Frame).Arms, 4);
		Assert.Equal(1f, Run(animator, aiming, BodyAnimator.ArmsBlendSeconds).Arms, 3);

		float lowering = animator.Step(default, Frame).Arms;
		Assert.InRange(lowering, 0.5f, 0.99f);
		Assert.Equal(0f, Run(animator, default, BodyAnimator.ArmsBlendSeconds).Arms, 3);
	}

	[Fact]
	public void AShot_Recoils_ThenHoldsTheAim_ThenLowersIt()
	{
		var animator = new BodyAnimator(Times);
		int serial = animator.ArmsSerial;
		animator.Shoot();
		Assert.Equal(serial + 1, animator.ArmsSerial);

		Run(animator, default, Times.Shoot);
		Assert.False(animator.IsArmsActing);

		Assert.Equal(1f, Run(animator, default, BodyAnimator.AimHoldSeconds - Times.Shoot - (2f * Frame)).Arms, 3);
		Assert.Equal(0f, Run(animator, default, BodyAnimator.ArmsBlendSeconds + (3f * Frame)).Arms, 3);
	}

	[Fact]
	public void AutomaticFire_DoesNotRestartTheRecoil_OnEveryRound()
	{
		var animator = new BodyAnimator(Times);
		animator.Shoot();
		int serial = animator.ArmsSerial;

		animator.Step(default, Frame);
		animator.Shoot();
		Assert.Equal(serial, animator.ArmsSerial);

		// Once the recoil has played out, the next round plays it again.
		Run(animator, default, Times.Shoot);
		animator.Shoot();
		Assert.Equal(serial + 1, animator.ArmsSerial);
	}

	[Fact]
	public void ASprint_CarriesTheWeapon_ButARoundFiredOnOneRaisesItForTheRecoil()
	{
		var animator = new BodyAnimator(Times);
		var sprint = new BodyPose { GroundSpeed = 8f, Sprinting = true };
		animator.Shoot();

		BodyMix mix = Run(animator, sprint, BodyAnimator.ArmsBlendSeconds);
		Assert.Equal(BodyClip.Sprint, mix.Legs);
		Assert.Equal(1f, mix.Arms, 3);

		// The recoil over, the aim it left is not carried at a sprint ...
		Assert.Equal(0f, Run(animator, sprint, Times.Shoot).Arms, 3);

		// ... and comes back up when the body slows to a run inside the hold.
		Assert.Equal(1f, Run(animator, Moving(5f), BodyAnimator.ArmsBlendSeconds).Arms, 3);
	}

	[Fact]
	public void AnEngagedUnit_RaisesItsArms_WhileItEngages()
	{
		var animator = new BodyAnimator(Times);
		var engaged = new BodyPose { Aiming = true, GroundSpeed = 1f };

		BodyMix mix = Run(animator, engaged, 1f);
		Assert.Equal(BodyClip.Walk, mix.Legs);
		Assert.Equal(1f, mix.Arms, 3);
		Assert.False(animator.IsArmsActing);
	}

	[Theory]
	[InlineData(0f, 0f)]
	[InlineData(BodyAnimator.AimUpRadians * 0.5f, 0.5f)]
	[InlineData(BodyAnimator.AimUpRadians, 1f)]
	[InlineData(1.5f, 1f)]
	[InlineData(-BodyAnimator.AimDownRadians * 0.25f, -0.25f)]
	[InlineData(-BodyAnimator.AimDownRadians, -1f)]
	[InlineData(-1.5f, -1f)]
	public void TheAim_IsBlendedByThePitch_UpToThePitchEachIsDrawnAt(float pitch, float expected)
	{
		Assert.Equal(expected, BodyAnimator.AimBlend(pitch), 4);

		var pose = new BodyPose { Aiming = true, AimPitch = pitch };
		Assert.Equal(expected, new BodyAnimator(Times).Step(pose, Frame).AimBlend, 4);
	}

	[Fact]
	public void AThrow_IsTheArms_AndTheLegsKeepRunning_ForItsLength()
	{
		var animator = new BodyAnimator(Times);
		int serial = animator.ArmsSerial;
		animator.Throw();
		Assert.Equal(serial + 1, animator.ArmsSerial);

		Assert.Equal(BodyClip.Run, animator.Step(Moving(5f), Frame).Legs);
		Assert.Equal(BodyClip.Throw, animator.ArmsAction);

		Assert.Equal(BodyClip.Run, Run(animator, Moving(5f), Times.Throw - (3f * Frame)).Legs);
		Assert.True(animator.IsArmsActing);

		Run(animator, Moving(5f), 3f * Frame);
		Assert.False(animator.IsArmsActing);
	}

	[Fact]
	public void AThrow_LowersTheAimItInterrupts()
	{
		var animator = new BodyAnimator(Times);
		animator.Shoot();
		Run(animator, default, BodyAnimator.ArmsBlendSeconds);

		animator.Throw();
		Assert.Equal(0f, Run(animator, default, Times.Throw).Arms, 3);
	}

	[Fact]
	public void AJump_InTheMiddleOfAThrow_StillPushesOff()
	{
		var animator = new BodyAnimator(Times);
		animator.Throw();
		animator.Step(Moving(5f), Frame);

		Assert.Equal(BodyClip.JumpStart, animator.Step(new BodyPose { Airborne = true, Jumped = true }, Frame).Legs);
		Assert.True(animator.IsArmsActing);
		Assert.Equal(BodyClip.Throw, animator.ArmsAction);
	}

	[Fact]
	public void AThrownAgain_StartsFromTheTop_AndAShotDoesNotCutItShort()
	{
		var animator = new BodyAnimator(Times);
		animator.Throw();
		animator.Step(default, Frame);
		int serial = animator.ArmsSerial;

		animator.Shoot();
		Assert.Equal(serial, animator.ArmsSerial);
		animator.Step(default, Frame);
		Assert.Equal(BodyClip.Throw, animator.ArmsAction);

		animator.Throw();
		Assert.Equal(serial + 1, animator.ArmsSerial);
	}

	// ---- the death, over everything -----------------------------------------

	[Fact]
	public void Death_TakesTheBodyOver_AndHoldsItUntilARevive()
	{
		var animator = new BodyAnimator(Times);
		animator.Shoot();
		Run(animator, Moving(5f), BodyAnimator.ArmsBlendSeconds);

		animator.Die();
		int serial = animator.ArmsSerial;
		animator.Shoot();
		animator.Throw();
		Assert.Equal(serial, animator.ArmsSerial);

		var running = new BodyPose { Airborne = true, GroundSpeed = 8f };
		BodyMix mix = animator.Step(running, Frame);
		Assert.Equal(Frame / BodyAnimator.DeathBlendSeconds, mix.Death, 4);
		Assert.True(animator.IsDying);

		mix = Run(animator, running, Times.Death);
		Assert.Equal(1f, mix.Death);
		Assert.Equal(0f, mix.Arms);
		Assert.Equal(BodyClip.Idle, mix.Legs);
		Assert.True(animator.IsDead);
		Assert.False(animator.IsDying);

		animator.Revive();
		Assert.False(animator.IsDead);
		mix = animator.Step(default, Frame);
		Assert.Equal(0f, mix.Death);
		Assert.Equal(0f, mix.Arms);
		Assert.Equal(BodyClip.Idle, mix.Legs);
	}

	[Theory]
	[InlineData(5f, 5.357f, 0.933f)]
	[InlineData(4.5f, 0.975f, BodyAnimator.MaxStrideScale)]
	[InlineData(0.1f, 0.975f, BodyAnimator.MinStrideScale)]
	[InlineData(3f, 0f, 1f)]
	public void TheStride_IsScaledToTheGroundSpeed_WithinBounds(float speed, float clipSpeed, float expected)
	{
		Assert.Equal(expected, BodyAnimator.StrideScale(speed, clipSpeed), 3);
	}
}
