using Godot;
using System;
using System.Collections.Generic;

public partial class StateMachine : Node
{

public State CurrentState;

public Dictionary<string,State> States = new();

	// Called when the node enters the scene tree for the first time.
	public override void _Ready()
	{
		foreach(var child in this.GetChildren()){
			var state = child.GetScript().As<State>();
			if(state != null){
			States.Add(state.Name,state);
			}
		}
	}

	// Called every frame. 'delta' is the elapsed time since the previous frame.
	public override void _Process(double delta)
	{
	}
}
