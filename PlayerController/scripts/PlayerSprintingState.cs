using Godot;
using System;

public partial class PlayerSprintingState : State
{
	[Export]
	public float TopAnimationSpeed = 1.6f;

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
		PlayerAnimation.Play("sprinting", 0.5, 1.0f);
		//playerController.CurrentMoveSpeed = StatePlayerMoveSpeed;
	}

	public override void Update(double delta)
	{
		base.Update(delta);
		SetAnimatonSpeed(playerController.Velocity.Length());
		if (Input.IsActionJustReleased("sprint"))
		{
			OnStateTransition("PlayerIdleState");
		}
		if (Input.IsActionJustPressed("crouch") && playerController.Velocity.Length() > 6)
		{
			OnStateTransition("PlayerSlidingState");
		}
		if (Input.IsActionJustPressed("jump") && playerController.IsOnFloor())
		{
			OnStateTransition("PlayerJumpingState");
		}

		if (playerController.Velocity.Y < -3.0 && !playerController.IsOnFloor())
		{
			OnStateTransition("PlayerFallingState");
		}
	}

	public void SetAnimatonSpeed(float speed)
	{
		var alpha = Mathf.Remap(speed, 0.0, StatePlayerMoveSpeed, 0.0, 1.0);
		PlayerAnimation.SpeedScale = (float)Mathf.Lerp(0.0, TopAnimationSpeed, alpha);
	}

	// public override void _Input(InputEvent @event)
	// {
	// 	if (@event.IsActionReleased("sprint"))
	// 	{
	// 		OnStateTransition("PlayerWalkingState");
	// 	}
	// }

}
