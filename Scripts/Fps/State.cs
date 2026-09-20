using System;
using Gdpyr.Sim;
using Godot;

namespace Gdpyr.Fps;

/// <summary>
/// One movement state. States read intent from the <see cref="InputContext"/> they
/// are handed — never from the <c>Input</c> singleton — because a replayed tick
/// has to see the input that was recorded for it, not whatever the device says
/// now (docs/NETCODE.md §3.1).
/// </summary>
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

	protected virtual void OnStateTransition(string nextStateName)
	{
		StateTransitioned?.Invoke(this, nextStateName);
	}

	public virtual void Enter(State previousState) { }

	/// <summary>
	/// One simulation tick in this state. The base behaviour is gravity, steering
	/// and a collide-and-slide move; states add their own transitions around it.
	/// </summary>
	public virtual void Tick(in InputContext input, float dt)
	{
		playerController.ApplyGravity(dt);
		playerController.ApplyMove(input.MoveAxes, StatePlayerMoveSpeed, StatePlayerDeceleration, StatePlayerAcceleration);
		playerController.Move();
	}

	public virtual void Exit()
	{
		PlayerAnimation.SpeedScale = 1.0f;
	}
}
