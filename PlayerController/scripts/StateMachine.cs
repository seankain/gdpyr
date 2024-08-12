using Godot;
using System;
using System.Collections.Generic;

public partial class StateMachine : Node
{
	[Export]
	public State CurrentState;

	public Dictionary<string, State> States = new();

	// Called when the node enters the scene tree for the first time.
	public override void _Ready()
	{
		foreach (var child in this.GetChildren())
		{
			var state = child.GetScript().As<State>();
			if (state != null)
			{
				States.Add(state.Name, state);
				//child.transition.connect(on_child_transition);
			}
		}
		CurrentState.Enter();
	}

	// Called every frame. 'delta' is the elapsed time since the previous frame.
	public override void _Process(double delta)
	{
		CurrentState.Update(delta);
	}

	public override void _PhysicsProcess(double delta)
	{
		CurrentState.PhysicsUpdate(delta);
	}

	public void OnChildTransition(string nextStateName)
	{
		var nextStateExists = States.TryGetValue(nextStateName, out var nextState);
		if (!nextStateExists) { GD.PushWarning($"Non-existent state name {nextStateName}"); return; }
		CurrentState.Exit();
		nextState.Enter();
		CurrentState = nextState;
	}

}
