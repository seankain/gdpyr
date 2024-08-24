using Godot;
using System;

public partial class PlayerIdleState : State
{
	public override void Enter()
	{
		PlayerAnimation.Pause();
	}

	public override void Update(double delta)
	{
		base.Update(delta);
		if (playerController.Velocity.Length() > 0.0 && playerController.IsOnFloor())
		{
			OnStateTransition("PlayerWalkingState");
		}

	}

}
