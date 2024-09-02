using Godot;
using System;

public partial class PlayerFallingState : State
{

	public override void Enter(State previousState)
	{
		PlayerAnimation.Pause();
	}

	public override void Update(double delta)
	{
		base.Update(delta);
		if (playerController.IsOnFloor())
		{
			PlayerAnimation.Play("JumpEnd");
			OnStateTransition("PlayerIdleState");
		}
	}
	// Called when the node enters the scene tree for the first time.
	public override void _Ready()
	{
	}

	// Called every frame. 'delta' is the elapsed time since the previous frame.
	public override void _Process(double delta)
	{
	}
}
