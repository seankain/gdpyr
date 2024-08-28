using Godot;
using System;

public partial class PlayerIdleState : State
{
	public override void Enter(State previousState)
	{
		PlayerAnimation.Pause();
	}

	public override void Update(double delta)
	{
		base.Update(delta);
		// Not doing IsOnFloor check because would like to have crouch jumps
		if (Input.IsActionJustPressed("crouch"))
		{
			OnStateTransition("PlayerCrouchingState");
		}

		if (playerController.Velocity.Length() > 0.0 && playerController.IsOnFloor())
		{
			OnStateTransition("PlayerWalkingState");
		}

	}

}
