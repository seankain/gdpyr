using Gdpyr.Sim;
using Godot;

namespace Gdpyr.Fps;

public partial class PlayerCrouchingState : State
{
	[Export]
	public ShapeCast3D CrouchShapeCast;

	[Export]
	public float CrouchSpeed;

	public override void Enter(State previousState)
	{
		PlayerAnimation.SpeedScale = 1.0f;

		if (previousState.Name == "PlayerSlidingState")
		{
			PlayerAnimation.CurrentAnimation = "crouch";
			PlayerAnimation.Seek(1.0, true);
		}
		else
		{
			PlayerAnimation.Play("crouch", -1.0, CrouchSpeed);
		}
	}

	public override void Tick(in InputContext input, float dt)
	{
		base.Tick(input, dt);

		if (input.JustPressed(InputButtons.Jump) && playerController.IsOnFloor())
		{
			OnStateTransition("PlayerJumpingState");
			return;
		}

		// Standing up used to be a coroutine that resolved on a render frame, which
		// made the transition tick-dependent and therefore unreplayable. It resolves
		// within the tick now; the shape cast still keeps the player crouched under
		// anything too low to stand up in.
		if (!input.Held(InputButtons.Crouch) && !CrouchShapeCast.IsColliding())
		{
			PlayerAnimation.Play("crouch", -1.0, -CrouchSpeed * 1.5f, true);
			OnStateTransition("PlayerIdleState");
		}
	}
}
