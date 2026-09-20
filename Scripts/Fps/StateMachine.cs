using System.Collections.Generic;
using Gdpyr.Sim;
using Godot;

namespace Gdpyr.Fps;

/// <summary>
/// The movement FSM. Ticked by <see cref="fps_controller.Simulate"/> on the fixed
/// simulation tick — it no longer runs itself on <c>_Process</c>, which is the
/// framerate-dependent movement defect M1 exists to fix
/// (docs/IMPLEMENTATION_PLAN.md §1).
/// </summary>
public partial class StateMachine : Node
{
	[Export]
	public State CurrentState;

	public Dictionary<string, State> States = new();

	/// <summary>
	/// The current state's index among the child states. This is what the snapshot
	/// replicates and what a reconciliation rewind restores, so it must be derived
	/// from scene order — identical in every process running the same build.
	/// </summary>
	public byte CurrentStateId { get; private set; }

	private readonly List<State> _ordered = new();

	public override void _Ready()
	{
		foreach (var child in GetChildren())
		{
			if (child is State state)
			{
				States.Add(state.Name, state);
				_ordered.Add(state);
				state.StateTransitioned += (o, s) => { OnChildTransition(s); };
			}
		}

		CurrentStateId = (byte)Mathf.Max(_ordered.IndexOf(CurrentState), 0);
		CurrentState.Enter(CurrentState);
	}

	public void Tick(in InputContext input, float dt) => CurrentState.Tick(input, dt);

	public void OnChildTransition(string nextStateName)
	{
		var nextStateExists = States.TryGetValue(nextStateName, out var nextState);
		if (!nextStateExists) { GD.PushWarning($"Non-existent state name {nextStateName}"); return; }
		CurrentState.Exit();
		nextState.Enter(CurrentState);
		CurrentState = nextState;
		CurrentStateId = (byte)Mathf.Max(_ordered.IndexOf(nextState), 0);
	}

	/// <summary>
	/// Rewinds to the server's state before a replay, without running
	/// <c>Exit</c>/<c>Enter</c>. Those have side effects — a jump sets velocity, a
	/// crouch starts an animation that resizes the capsule — and applying them here
	/// would add a second jump on top of the one being replayed.
	/// </summary>
	public void ForceState(byte stateId)
	{
		if (stateId >= _ordered.Count || stateId == CurrentStateId)
		{
			return;
		}

		CurrentState = _ordered[stateId];
		CurrentStateId = stateId;
	}
}
