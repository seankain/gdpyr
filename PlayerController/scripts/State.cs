using Godot;
using System;
using System.Collections;
using System.Runtime.Serialization;

public partial class State : Node
{

	[Export]
	public float StatePlayerMoveSpeed = 0;
	[Export]
	public float StatePlayerAcceleration = 0;
	[Export]
	public float StatePlayerDeceleration = 0;

	[Export]
	public fps_controller playerController;

	public event EventHandler<string> StateTransitioned;

	[Export]
	public AnimationPlayer PlayerAnimation;


	// Called when the node enters the scene tree for the first time.
	public override void _Ready()
	{
	}

	// Called every frame. 'delta' is the elapsed time since the previous frame.
	public override void _Process(double delta)
	{
	}

	protected virtual void OnStateTransition(string nextStateName)
	{
		StateTransitioned?.Invoke(this, nextStateName);
	}

	public virtual void Enter(State previousState) { }
	public virtual void Update(double delta)
	{
		playerController.UpdateGravity(delta);
		playerController.UpdateInput(StatePlayerMoveSpeed, StatePlayerDeceleration, StatePlayerAcceleration);
		playerController.UpdateVelocity();
	}

	public virtual void PhysicsUpdate(double delta) { }

	public virtual void Exit()
	{
		PlayerAnimation.SpeedScale = 1.0f;
	}

	public IEnumerable WaitForAnimation(string animationName)
	{
		if (PlayerAnimation.IsPlaying() && PlayerAnimation.CurrentAnimation == animationName)
		{
			yield return null;
		}
	}

}
