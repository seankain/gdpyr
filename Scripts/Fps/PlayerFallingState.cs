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

		if (playerController.IsOnFloor())
		{
			// The animation is named "jumpend"; the mismatched case here used to log an
			// error and skip the landing animation on every landing.
			PlayerAnimation.Play("jumpend");
			OnStateTransition("PlayerIdleState");
		}
	}
}
