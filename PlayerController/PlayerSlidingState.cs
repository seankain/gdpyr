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

	// Called when the node enters the scene tree for the first time.
	public override void _Ready()
	{
	}

	// Called every frame. 'delta' is the elapsed time since the previous frame.
	public override void _Process(double delta)
	{
	}
	public override void Enter()
	{
		//SetTilt(playerController.CurrentRotation);
		var slideAnim = PlayerAnimation.GetAnimation("sliding");
		slideAnim.TrackSetKeyValue(4, 0, playerController.Velocity.Length());
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
		PlayerAnimation.GetAnimation("sliding").TrackSetKeyValue(8, 1, tilt);
		PlayerAnimation.GetAnimation("sliding").TrackSetKeyValue(8, 2, tilt);
	}

	private void Finish()
	{
		OnStateTransition("CrouchingPlayerState");
	}

}
