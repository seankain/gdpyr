using Godot;
using System;
using System.Collections;
using System.IO;

public partial class PlayerIdleState : State
{
	public override void Enter(State previousState)
	{
		if (PlayerAnimation.IsPlaying() && PlayerAnimation.CurrentAnimation == "jumpend")
		{
			Coroutines.StartCoroutine(WaitForAnimation("jumpend"));
		}
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

		if (Input.IsActionJustPressed("jump") && playerController.IsOnFloor())
		{
			OnStateTransition("PlayerJumpingState");
		}

		if (playerController.Velocity.Y < -3.0 && !playerController.IsOnFloor())
		{
			OnStateTransition("PlayerFallingState");
		}

	}

}
