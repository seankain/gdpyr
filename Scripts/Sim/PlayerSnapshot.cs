using Godot;

namespace Gdpyr.Sim;

/// <summary>
/// One player's authoritative state at a server tick (docs/NETCODE.md §7).
///
/// <see cref="LastInputTick"/> is the acknowledgement the whole prediction scheme
/// turns on: it tells the owning client which of its inputs this state already
/// contains, and therefore which ones to replay.
/// </summary>
public struct PlayerSnapshot
{
	/// <summary>Bytes on the wire: 4 + 6 + 6 + 2 + 2 + 4 + 1 + 1.</summary>
	public const int SizeBytes = 26;

	public int PeerId;
	public Vector3 Position;
	public Vector3 Velocity;
	public float Yaw;
	public float Pitch;

	/// <summary>The last input from this peer the server has consumed.</summary>
	public uint LastInputTick;

	public byte StateId;

	/// <summary>
	/// How many of this peer's inputs the server still has buffered. The client
	/// steers its input clock by this (docs/NETCODE.md §2); other peers ignore it.
	/// </summary>
	public byte InputBufferDepth;

	public readonly CharacterState ToCharacterState() => new()
	{
		Tick = LastInputTick,
		Position = Position,
		Velocity = Velocity,
		Yaw = Yaw,
		Pitch = Pitch,
		StateId = StateId,
	};
}
