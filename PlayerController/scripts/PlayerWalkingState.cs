using Godot;
using System;

public partial class PlayerWalkingState : State
{
		public override void Update(double delta){
		if(playerController.Velocity.Length() == 0.0){
			OnStateTransition("PlayerIdleState");
		}
	}
}
