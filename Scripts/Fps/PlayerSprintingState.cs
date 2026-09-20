using Gdpyr.Sim;
using Godot;

namespace Gdpyr.Fps;

public partial class PlayerSprintingState : State
{
	[Export]
	public float TopAnimationSpeed = 1.6f;

	public override void Enter(State previousState)
	{
		PlayerAnimation.Play("sprinting", 0.5, 1.0f);
	}

	public override void Tick(in InputContext input, float dt)
	{
		base.Tick(input, dt);
		SetAnimatonSpeed(playerController.Velocity.Length());

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

		if (input.JustPressed(InputButtons.Crouch) && playerController.Velocity.Length() > 6)
		{
			OnStateTransition("PlayerSlidingState");
			return;
		}

		if (input.JustReleased(InputButtons.Sprint))
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
