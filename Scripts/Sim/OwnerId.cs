namespace Gdpyr.Sim;

/// <summary>
/// One number that names whatever fired a shot, across the three kinds of thing
/// that can (docs/IMPLEMENTATION_PLAN.md §M3: "units fire the *same* projectiles as
/// players").
///
/// <see cref="ProjectileSpawn.OwnerPeerId"/> was a peer id when players were the
/// only shooters. Rather than add a second field to every shot on the wire, the
/// id spaces are folded into one signed int: Godot peer ids are always positive,
/// so everything the strategist's side fires with takes the negative half — units
/// from -1 to -65535, a barracks' defences below that, and the structures its
/// builders put up below those. A projectile therefore
/// still costs 23 bytes, "did this round come from its own shooter" stays one
/// comparison, and "negative means the strategist's" stays true for anyone reading
/// the event stream (docs/AGENT_API.md §8).
/// </summary>
public static class OwnerId
{
	/// <summary>No owner: world damage, or a hit against nothing.</summary>
	public const int None = 0;

	/// <summary>
	/// Magnitude of the first defence id: one past the lowest unit id, so the two
	/// negative ranges cannot meet however high unit ids climb.
	/// </summary>
	private const int DefenseBase = ushort.MaxValue + 1;

	/// <summary>Magnitude of the first structure id: one past the last defence.</summary>
	private const int StructureBase = DefenseBase + SimConfig.MaxDefenses;

	/// <summary>Godot's peer ids start at 1, so this is the identity.</summary>
	public static int ForPeer(int peerId) => peerId;

	/// <summary>Unit ids start at 1 for the same reason: 0 has to stay unambiguous.</summary>
	public static int ForUnit(ushort unitId) => -unitId;

	/// <summary>
	/// A barracks defence, by its index in the map's defence group
	/// (<see cref="SimConfig.MaxDefenses"/>). Index 0 is a real defence: the range
	/// starts below the units', not at zero.
	/// </summary>
	public static int ForDefense(int index) => -(DefenseBase + index);

	/// <summary>
	/// A structure a builder put up, by its slot in the unit manager's structure
	/// table (<see cref="SimConfig.MaxStructures"/>). Slot 0 is a real structure, as
	/// defence 0 is a real defence.
	/// </summary>
	public static int ForStructure(int slot) => -(StructureBase + slot);

	public static bool IsPeer(int ownerId) => ownerId > 0;

	public static bool IsUnit(int ownerId) => ownerId < 0 && ownerId >= -ushort.MaxValue;

	public static bool IsDefense(int ownerId) =>
		ownerId <= -DefenseBase && ownerId > -(DefenseBase + SimConfig.MaxDefenses);

	public static bool IsStructure(int ownerId) =>
		ownerId <= -StructureBase && ownerId > -(StructureBase + SimConfig.MaxStructures);

	/// <summary>Only meaningful when <see cref="IsUnit"/>; 0 otherwise.</summary>
	public static ushort UnitOf(int ownerId) => IsUnit(ownerId) ? (ushort)(-ownerId) : (ushort)0;

	/// <summary>Only meaningful when <see cref="IsPeer"/>; 0 otherwise.</summary>
	public static int PeerOf(int ownerId) => ownerId > 0 ? ownerId : 0;

	/// <summary>Only meaningful when <see cref="IsDefense"/>; -1 otherwise, because 0 is a defence.</summary>
	public static int DefenseOf(int ownerId) => IsDefense(ownerId) ? -ownerId - DefenseBase : -1;

	/// <summary>Only meaningful when <see cref="IsStructure"/>; -1 otherwise, because 0 is a structure.</summary>
	public static int StructureOf(int ownerId) => IsStructure(ownerId) ? -ownerId - StructureBase : -1;
}
