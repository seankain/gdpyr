using Gdpyr.Sim;

namespace Gdpyr.Fps;

public partial class PlayerFallingState : State
{
	public override void Enter(State previousState)
	{
		PlayerAnimation.Pause();
	}

	public override void Tick(in InputContext input, float dt)
	{
		base.Tick(input, dt);

		// Coming down past a ladder and walking into it catches it (docs/LADDERS.md §4.1).
		if (playerController.TryGrabLadder(input))
		{
			OnStateTransition("PlayerClimbingState");
			return;
		}

		if (playerController.IsOnFloor())
		{
			// The animation is named "jumpend"; the mismatched case here used to log an
			// error and skip the landing animation on every landing.
			PlayerAnimation.Play("jumpend");
			OnStateTransition("PlayerIdleState");
		}
	}
}
