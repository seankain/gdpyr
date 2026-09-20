using Gdpyr.Sim;

namespace Gdpyr.Fps;

public partial class PlayerIdleState : State
{
	public override void Enter(State previousState)
	{
		PlayerAnimation.Pause();
	}

	// Transitions are tested in priority order and return on the first match. The
	// pre-M1 code evaluated all of them and let the last one win; the order here
	// reproduces that outcome, but as one transition per tick rather than several.
	public override void Tick(in InputContext input, float dt)
	{
		base.Tick(input, dt);

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

		if (playerController.Velocity.Length() > 0.0 && playerController.IsOnFloor())
		{
			OnStateTransition("PlayerWalkingState");
			return;
		}

		// Not doing IsOnFloor check because would like to have crouch jumps
		if (input.JustPressed(InputButtons.Crouch))
		{
			OnStateTransition("PlayerCrouchingState");
		}
	}
}
