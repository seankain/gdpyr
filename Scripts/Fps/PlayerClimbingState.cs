using Gdpyr.Sim;
using Godot;

namespace Gdpyr.Fps;

/// <summary>
/// On a ladder (docs/LADDERS.md §4). Entered from idle, walking, sprinting, a jump on its way
/// down and a fall, when <see cref="fps_controller.TryGrabLadder"/> says the walk took one.
///
/// No gravity and no steering: <see cref="Ladders.Climb"/> sets the whole velocity from where
/// the feet are and the way the body is asked to walk, and the collide-and-slide moves it.
/// The ladder is found again every tick from the feet (<see cref="fps_controller.TryHoldLadder"/>),
/// never remembered, so a rewind restores everything this state needs by restoring the
/// position (docs/NETCODE.md §3.2).
///
/// Left by going over the top or off an end of the rungs, which take the feet out of the
/// ladder's reach; by standing on the ground at the foot and not climbing; or by a jump,
/// which pushes off. The last of this FSM's children, so the states before it keep the
/// indices snapshots, demos and the agent observation already give them.
/// </summary>
public partial class PlayerClimbingState : State
{
	public override void Enter(State previousState)
	{
		PlayerAnimation.Pause();
	}

	public override void Tick(in InputContext input, float dt)
	{
		if (!playerController.TryHoldLadder(out LadderSpan ladder))
		{
			// Over the landing, or off the side: the ground has it from here.
			base.Tick(input, dt);
			OnStateTransition("PlayerIdleState");
			return;
		}

		if (input.JustPressed(InputButtons.Jump))
		{
			playerController.Velocity = Ladders.JumpOff(ladder);
			playerController.Move();
			OnStateTransition("PlayerFallingState");
			return;
		}

		Vector3 wish = playerController.WishDirection(input.MoveAxes);
		playerController.Velocity = Ladders.Climb(ladder, playerController.GlobalPosition, wish,
			playerController.MoveSpeedScale, dt).Velocity;
		playerController.Move();

		if (Ladders.LetsGoAtFoot(ladder, playerController.GlobalPosition, wish, playerController.IsOnFloor()))
		{
			OnStateTransition("PlayerIdleState");
		}
	}
}
