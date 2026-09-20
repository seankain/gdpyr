using Godot;

namespace Gdpyr.Sim;

/// <summary>
/// The part of a character the netcode owns: what a snapshot carries, what a
/// client stores per predicted tick, and what gets restored before a replay.
///
/// Position *and* velocity are both here deliberately — restoring position alone
/// is the classic cause of reconciliation drift (docs/NETCODE.md §3.2). Floor
/// contact is not restored: it is re-derived by the first replayed
/// <c>MoveAndSlide()</c>, and carrying it would be a lie one tick out of date.
/// </summary>
public struct CharacterState
{
	/// <summary>The tick whose simulation produced this state.</summary>
	public uint Tick;

	public Vector3 Position;
	public Vector3 Velocity;
	public float Yaw;
	public float Pitch;

	/// <summary>Index of the movement state in the FSM (see <c>StateMachine</c>).</summary>
	public byte StateId;

	/// <summary>Local bookkeeping only; never replicated.</summary>
	public bool OnFloor;

	public readonly float PositionErrorTo(in CharacterState other) =>
		(Position - other.Position).Length();
}
