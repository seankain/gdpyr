using Godot;
using System;
using System.Runtime.Serialization;

public partial class State : Node
{
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

	public virtual void Enter() { }
	public virtual void Update(double delta) { }

	public virtual void PhysicsUpdate(double delta) { }

	public void Exit() { }

}
