using Gdpyr.Sim;
using Godot;

namespace Gdpyr.Fps;

public partial class PlayerSlidingState : State
{
	[Export]
	public float TiltAmount = 0.09f;
	[Export]
	public float SlideAnimSpeed = 4.0f;

	[Export]
	public ShapeCast3D CrouchShapeCast;

	private int playerSlidingStateAnimTrackIndex = -1;
	private int cameraRotationAnimTrackIndex = -1;

	public override void _Ready()
	{
		var slideAnim = PlayerAnimation.GetAnimation("sliding");
		playerSlidingStateAnimTrackIndex = slideAnim.FindTrack("PlayerStateMachine/PlayerSlidingState:StatePlayerMoveSpeed", Animation.TrackType.Value);
		cameraRotationAnimTrackIndex = slideAnim.FindTrack("CameraController:rotation", Animation.TrackType.Value);
	}

	public override void Enter(State previousState)
	{
		SetTilt(playerController.YawRate);
		var slideAnim = PlayerAnimation.GetAnimation("sliding");
		slideAnim.TrackSetKeyValue(playerSlidingStateAnimTrackIndex, 0, playerController.Velocity.Length());
		PlayerAnimation.SpeedScale = 1.0f;
		PlayerAnimation.Play("sliding", -1.0, SlideAnimSpeed);
	}

	public override void Tick(in InputContext input, float dt)
	{
		// Not base.Tick: no steering while sliding, so the player cannot change
		// direction mid-slide.
		playerController.ApplyGravity(dt);
		playerController.Move();
	}

	/// <summary>Leans the camera into the turn. <paramref name="yawRate"/> is radians per second.</summary>
	public void SetTilt(float yawRate)
	{
		var tilt = Vector3.Zero;
		tilt.Z = (float)Mathf.Clamp(TiltAmount * yawRate, -0.1f, 0.1f);
		if (tilt.Z == 0.0)
		{
			tilt.Z = 0.05f;
		}
		PlayerAnimation.GetAnimation("sliding").TrackSetKeyValue(cameraRotationAnimTrackIndex, 1, tilt);
		PlayerAnimation.GetAnimation("sliding").TrackSetKeyValue(cameraRotationAnimTrackIndex, 2, tilt);
	}

	/// <summary>Called from the "sliding" animation's method track when the slide ends.</summary>
	private void Finish()
	{
		OnStateTransition("PlayerCrouchingState");
	}
}
