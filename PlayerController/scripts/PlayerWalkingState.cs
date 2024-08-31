using Godot;
using System;

public partial class PlayerWalkingState : State
{
	[Export]
	public float TopAnimationSpeed = 2.2f;
	public override void Enter(State previousState)
	{
		PlayerAnimation.Play("walking", -1.0, 1.0f);
		//playerController.CurrentMoveSpeed = StatePlayerMoveSpeed;
	}
	public override void Update(double delta)
	{
		base.Update(delta);
		if (Input.IsActionJustPressed("sprint"))
		{
			OnStateTransition("PlayerSprintingState");
			return;
		}
		if (playerController.Velocity.Length() == 0.0)
		{
			OnStateTransition("PlayerIdleState");
		}
		if (Input.IsActionJustPressed("crouch"))
		{
			OnStateTransition("PlayerCrouchingState");
		}

		if (Input.IsActionJustPressed("jump") && playerController.IsOnFloor())
		{
			OnStateTransition("PlayerJumpingState");
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
	// 		OnStateTransition("PlayerSprintingState");
	// 	}
	// }
}
