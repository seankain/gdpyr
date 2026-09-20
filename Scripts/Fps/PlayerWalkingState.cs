using Gdpyr.Sim;
using Godot;

namespace Gdpyr.Fps;

public partial class PlayerWalkingState : State
{
	[Export]
	public float TopAnimationSpeed = 2.2f;

	public override void Enter(State previousState)
	{
		PlayerAnimation.Play("walking", -1.0, 1.0f);
	}

	public override void Tick(in InputContext input, float dt)
	{
		base.Tick(input, dt);

		if (input.JustPressed(InputButtons.Sprint))
		{
			OnStateTransition("PlayerSprintingState");
			return;
		}

		if (playerController.Velocity.Y < -3.0 && !playerController.IsOnFloor())
		{
			OnStateTransition("PlayerFallingState");
			return;
		}

		if (input.JustPressed(InputButtons.Jump) && playerController.IsOnFloor())
		{
			OnStateTransition("PlayerJumpingState");
			return;
		}

		if (input.JustPressed(InputButtons.Crouch))
		{
			OnStateTransition("PlayerCrouchingState");
			return;
		}

		if (playerController.Velocity.Length() == 0.0)
		{
			OnStateTransition("PlayerIdleState");
		}
	}

	public void SetAnimatonSpeed(float speed)
	{
		var alpha = Mathf.Remap(speed, 0.0, StatePlayerMoveSpeed, 0.0, 1.0);
		PlayerAnimation.SpeedScale = (float)Mathf.Lerp(0.0, TopAnimationSpeed, alpha);
	}
}
