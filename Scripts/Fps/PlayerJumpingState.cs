using Gdpyr.Sim;
using Godot;

namespace Gdpyr.Fps;

public partial class PlayerJumpingState : State
{
	[Export]
	public float JumpVelocity = 4.5f;
	[Export]
	public float InputMultiplier = 0.85f;

	public override void Enter(State previousState)
	{
		playerController.ApplyJump(JumpVelocity);
		PlayerAnimation.Play("jumpstart");
	}

	public override void Tick(in InputContext input, float dt)
	{
		// Not base.Tick: air control runs at a fraction of the state's move speed.
		playerController.ApplyGravity(dt);
		playerController.ApplyMove(input.MoveAxes, StatePlayerMoveSpeed * InputMultiplier,
			StatePlayerDeceleration, StatePlayerAcceleration);
		playerController.Move();

		// On the way down, a ladder walked into is caught; on the way up it is not, so a jump
		// off one does not take it again at once (docs/LADDERS.md §4.1).
		if (playerController.TryGrabLadder(input))
		{
			OnStateTransition("PlayerClimbingState");
			return;
		}

		if (playerController.IsOnFloor())
		{
			PlayerAnimation.Play("jumpend");
			OnStateTransition("PlayerIdleState");
		}
	}
}
