using Godot;
using System;
using System.Collections;

public partial class PlayerCrouchingState : State
{
	[Export]
	public ShapeCast3D CrouchShapeCast;

	[Export]
	public float CrouchSpeed;

	private bool released = false;

	// Called when the node enters the scene tree for the first time.
	public override void _Ready()
	{
	}

	// Called every frame. 'delta' is the elapsed time since the previous frame.
	public override void _Process(double delta)
	{
	}

	public override void Update(double delta)
	{
		base.Update(delta);
		if (Input.IsActionJustReleased("crouch"))
		{
			Coroutines.StartCoroutine(Uncrouch());
		}
		else if (Input.IsActionPressed("crouch") == false && released == false)
		{
			released = true;
			Coroutines.StartCoroutine(Uncrouch());
		}
		if (Input.IsActionJustPressed("jump") && playerController.IsOnFloor())
		{
			OnStateTransition("PlayerJumpingState");
		}
	}

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

	public override void Exit()
	{
		base.Exit();
		released = false;
	}

	private IEnumerable Uncrouch()
	{
		if (!CrouchShapeCast.IsColliding() && !Input.IsActionPressed("crouch"))
		{
			PlayerAnimation.Play("crouch", -1.0, -CrouchSpeed * 1.5f, true);
			if (PlayerAnimation.IsPlaying()) { yield return null; }
			OnStateTransition("PlayerIdleState");
		}
		else if (CrouchShapeCast.IsColliding())
		{
			yield return null;
		}

	}


}
