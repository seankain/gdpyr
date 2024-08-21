using Godot;
using System;

public partial class PlayerWalkingState : State
{
	[Export]
	public float TopAnimationSpeed = 2.2f;
	public override void Enter()
	{
		PlayerAnimation.Play("walking", -1.0, 1.0f);
		playerController.CurrentMoveSpeed = playerController.MoveSpeed;
	}
	public override void Update(double delta)
	{
		if (playerController.Velocity.Length() == 0.0)
		{
			OnStateTransition("PlayerIdleState");
		}

	}

	public void SetAnimatonSpeed(float speed)
	{
		var alpha = Mathf.Remap(speed, 0.0, playerController.MoveSpeed, 0.0, 1.0);
		PlayerAnimation.SpeedScale = (float)Mathf.Lerp(0.0, TopAnimationSpeed, alpha);
	}


	public override void _Input(InputEvent @event)
	{
		if (@event.IsActionReleased("sprint"))
		{
			OnStateTransition("PlayerSprintingState");
		}
	}
}
