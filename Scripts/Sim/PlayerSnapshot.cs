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
	/// <summary>Bytes on the wire: 4 + 6 + 6 + 2 + 2 + 4 + 1 + 1 + 1 + 1 + 1.</summary>
	public const int SizeBytes = 29;

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

	/// <summary>
	/// Hit points, 0 for dead. Carried in the snapshot rather than as its own
	/// reliable message because it is small, because it is the one fact a client
	/// must never be wrong about for long, and because a snapshot that is always
	/// right is self-healing where a missed event is not.
	/// </summary>
	public byte Health;

	/// <summary>Rounds in the equipped weapon's magazine, for the owner's HUD.</summary>
	public byte Ammo;

	/// <summary>Equipped slot and reload state, packed (see <see cref="WeaponFlags"/>).</summary>
	public byte WeaponFlags;

	public readonly bool IsAlive => Health > 0;

	public readonly int EquippedSlot => Sim.WeaponFlags.Slot(WeaponFlags);

	public readonly bool IsReloading => Sim.WeaponFlags.IsReloading(WeaponFlags);

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
