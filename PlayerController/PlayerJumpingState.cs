using Godot;
using System;

public partial class PlayerJumpingState : State
{
	[Export]
	public float JumpVelocity = 4.5f;
	[Export]
	public float InputMultiplier = 0.85f;

	// Called when the node enters the scene tree for the first time.
	public override void _Ready()
	{
	}

	// Called every frame. 'delta' is the elapsed time since the previous frame.
	public override void _Process(double delta)
	{
	}

	public override void Enter(State previousState)
	{
		playerController.Velocity += new Vector3(0, JumpVelocity, 0);
		PlayerAnimation.Pause();
	}

	public override void Update(double delta)
	{
		//base.Update(delta);
		playerController.UpdateGravity(delta);
		playerController.UpdateInput(StatePlayerMoveSpeed * InputMultiplier, StatePlayerDeceleration, StatePlayerAcceleration);
		playerController.UpdateVelocity();

		if (playerController.IsOnFloor())
		{
			OnStateTransition("PlayerIdleState");
		}
	}
}
