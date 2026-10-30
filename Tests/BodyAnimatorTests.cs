using Gdpyr.Sim;
using Xunit;

namespace Gdpyr.Tests;

/// <summary>
/// Which clip a humanoid body shows (Scripts/Sim/BodyAnimation.cs): the ground speed
/// picks the stride, the air beats the ground, a throw beats moving, a shot shows only
/// standing still, and a death holds until a respawn.
/// </summary>
public class BodyAnimatorTests
{
	/// <summary>The rig's times: UAL1_Standard.glb's clip lengths over the rates the rig plays them at.</summary>
	private static readonly BodyClipTimes Times = new(jumpStart: 0.89f, land: 0.84f, shoot: 0.63f, @throw: 1.1f,
		death: 2.4f);

	private const float Frame = 1f / 60f;

	private static BodyPose Moving(float speed) => new() { GroundSpeed = speed };

	/// <summary>Steps <paramref name="seconds"/> of frames and returns the last clip shown.</summary>
	private static BodyClip Run(BodyAnimator animator, BodyPose pose, float seconds)
	{
		BodyClip clip = animator.Step(pose, Frame);
		for (float t = Frame; t < seconds - 1e-4f; t += Frame)
		{
			clip = animator.Step(pose, Frame);
		}

		return clip;
	}

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
		Assert.Equal(expected, new BodyAnimator(Times).Step(Moving(speed), Frame));
	}

	[Fact]
	public void Sprinting_ShowsTheSprint_AtASpeedAHeavyKitLeaves()
	{
		// The sprint state's 8 m/s at the heaviest kit's 0.70 is 5.6: a jog's speed, a sprint's state.
		var pose = new BodyPose { GroundSpeed = 5.6f, Sprinting = true };
		Assert.Equal(BodyClip.Sprint, new BodyAnimator(Times).Step(pose, Frame));

		pose.GroundSpeed = 0.1f;
		Assert.Equal(BodyClip.Idle, new BodyAnimator(Times).Step(pose, Frame));
	}

	[Fact]
	public void Crouched_IsTheCrouch_StillOrMoving()
	{
		var animator = new BodyAnimator(Times);
		Assert.Equal(BodyClip.CrouchIdle, animator.Step(new BodyPose { Crouched = true }, Frame));
		Assert.Equal(BodyClip.CrouchWalk, animator.Step(new BodyPose { Crouched = true, GroundSpeed = 2f }, Frame));
	}

	[Fact]
	public void AJump_PushesOff_ThenHangsInTheAir_ThenLands()
	{
		var animator = new BodyAnimator(Times);
		var air = new BodyPose { Airborne = true, Jumped = true };

		Assert.Equal(BodyClip.JumpStart, animator.Step(air, Frame));
		Assert.Equal(BodyClip.JumpStart, Run(animator, air, 0.5f));
		Assert.Equal(BodyClip.Airborne, Run(animator, air, 0.5f));

		Assert.Equal(BodyClip.Land, animator.Step(default, Frame));
		Assert.Equal(BodyClip.Land, Run(animator, default, 0.5f));
		Assert.Equal(BodyClip.Idle, Run(animator, default, 0.5f));
	}

	[Fact]
	public void AFall_IsInTheAirAtOnce_WithNoPushOff()
	{
		var animator = new BodyAnimator(Times);
		Assert.Equal(BodyClip.Airborne, animator.Step(new BodyPose { Airborne = true }, Frame));
	}

	[Fact]
	public void AStepOffAKerb_IsNoLanding()
	{
		var animator = new BodyAnimator(Times);
		Run(animator, new BodyPose { Airborne = true }, BodyAnimator.MinAirSeconds * 0.5f);
		Assert.Equal(BodyClip.Idle, animator.Step(default, Frame));
	}

	[Fact]
	public void LandingOnTheRun_CarriesOnRunning_AndDropsTheLanding()
	{
		var animator = new BodyAnimator(Times);
		Run(animator, new BodyPose { Airborne = true, GroundSpeed = 5f }, 0.8f);

		Assert.Equal(BodyClip.Run, animator.Step(Moving(5f), Frame));

		// Stopping straight after does not bring back a landing already given up.
		Assert.Equal(BodyClip.Idle, animator.Step(default, Frame));
	}

	[Fact]
	public void AShot_StandingStill_Recoils_ThenHoldsTheAim_ThenRests()
	{
		var animator = new BodyAnimator(Times);
		animator.Shoot();

		Assert.Equal(BodyClip.Shoot, animator.Step(default, Frame));
		Assert.Equal(BodyClip.Aim, Run(animator, default, Times.Shoot));
		Assert.Equal(BodyClip.Idle, Run(animator, default, BodyAnimator.AimHoldSeconds));
	}

	[Fact]
	public void AutomaticFire_DoesNotRestartTheRecoil_OnEveryRound()
	{
		var animator = new BodyAnimator(Times);
		animator.Shoot();
		int serial = animator.ActionSerial;

		animator.Step(default, Frame);
		animator.Shoot();
		Assert.Equal(serial, animator.ActionSerial);

		// Once the recoil has played out, the next round plays it again.
		Run(animator, default, Times.Shoot);
		animator.Shoot();
		Assert.Equal(serial + 1, animator.ActionSerial);
	}

	[Fact]
	public void AShot_OnTheMove_KeepsTheLegs()
	{
		var animator = new BodyAnimator(Times);
		animator.Shoot();
		Assert.Equal(BodyClip.Run, animator.Step(Moving(5f), Frame));
	}

	[Theory]
	[InlineData(0f, BodyClip.Aim)]
	[InlineData(0.6f, BodyClip.AimUp)]
	[InlineData(-0.6f, BodyClip.AimDown)]
	public void AnEngagedUnit_AimsWhereItPoints(float pitch, BodyClip expected)
	{
		var pose = new BodyPose { Aiming = true, AimPitch = pitch };
		Assert.Equal(expected, new BodyAnimator(Times).Step(pose, Frame));
	}

	[Fact]
	public void AThrow_IsShownOverRunning_ForItsLength()
	{
		var animator = new BodyAnimator(Times);
		animator.Throw();

		Assert.Equal(BodyClip.Throw, animator.Step(Moving(5f), Frame));
		Assert.Equal(BodyClip.Throw, Run(animator, Moving(5f), Times.Throw - (3f * Frame)));
		Assert.Equal(BodyClip.Run, Run(animator, Moving(5f), 3f * Frame));
	}

	[Fact]
	public void AThrownAgain_StartsFromTheTop_AndAShotDoesNotCutItShort()
	{
		var animator = new BodyAnimator(Times);
		animator.Throw();
		animator.Step(default, Frame);
		int serial = animator.ActionSerial;

		animator.Shoot();
		Assert.Equal(serial, animator.ActionSerial);
		Assert.Equal(BodyClip.Throw, animator.Step(default, Frame));

		animator.Throw();
		Assert.Equal(serial + 1, animator.ActionSerial);
	}

	[Fact]
	public void Death_IsHeldOverEverything_UntilARevive()
	{
		var animator = new BodyAnimator(Times);
		animator.Die();
		animator.Shoot();
		animator.Throw();

		Assert.Equal(BodyClip.Death, animator.Step(new BodyPose { Airborne = true, GroundSpeed = 8f }, Frame));
		Assert.True(animator.IsDying);

		Assert.Equal(BodyClip.Death, Run(animator, default, Times.Death));
		Assert.True(animator.IsDead);
		Assert.False(animator.IsDying);

		animator.Revive();
		Assert.False(animator.IsDead);
		Assert.Equal(BodyClip.Idle, animator.Step(default, Frame));
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
