using Godot;
using System;

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

	// Called when the node enters the scene tree for the first time.
	public override void _Ready()
	{
		var slideAnim = PlayerAnimation.GetAnimation("sliding");
		playerSlidingStateAnimTrackIndex = slideAnim.FindTrack("PlayerStateMachine/PlayerSlidingState:StatePlayerMoveSpeed", Animation.TrackType.Value);
		cameraRotationAnimTrackIndex = slideAnim.FindTrack("CameraController:rotation", Animation.TrackType.Value);
	}

	// Called every frame. 'delta' is the elapsed time since the previous frame.
	public override void _Process(double delta)
	{
	}
	public override void Enter(State previousState)
	{
		SetTilt((float)playerController.CurrentRotation);
		var slideAnim = PlayerAnimation.GetAnimation("sliding");
		GD.Print($"track ids slide: {playerSlidingStateAnimTrackIndex} camera rot : {cameraRotationAnimTrackIndex}");
		slideAnim.TrackSetKeyValue(playerSlidingStateAnimTrackIndex, 0, playerController.Velocity.Length());
		PlayerAnimation.SpeedScale = 1.0f;
		PlayerAnimation.Play("sliding", -1.0, SlideAnimSpeed);


	}
	public override void Update(double delta)
	{
		//base.Update(delta);
		//Not doing base to avoid updating input so player cant change direction while sliding
		playerController.UpdateGravity(delta);
		playerController.UpdateVelocity();
	}

	public void SetTilt(float playerRotation)
	{
		var tilt = Vector3.Zero;
		tilt.Z = (float)Mathf.Clamp(TiltAmount * playerRotation, -0.1f, 0.1f);
		if (tilt.Z == 0.0)
		{
			tilt.Z = 0.05f;
		}
		PlayerAnimation.GetAnimation("sliding").TrackSetKeyValue(cameraRotationAnimTrackIndex, 1, tilt);
		PlayerAnimation.GetAnimation("sliding").TrackSetKeyValue(cameraRotationAnimTrackIndex, 2, tilt);
	}

	private void Finish()
	{
		OnStateTransition("PlayerCrouchingState");
	}

}
