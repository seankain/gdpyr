namespace Gdpyr.Sim;

/// <summary>
/// One number that names whatever fired a shot, across the two kinds of thing that
/// can (docs/IMPLEMENTATION_PLAN.md §M3: "units fire the *same* projectiles as
/// players").
///
/// <see cref="ProjectileSpawn.OwnerPeerId"/> was a peer id when players were the
/// only shooters. Rather than add a second field to every shot on the wire, the
/// two id spaces are folded into one signed int: Godot peer ids are always
/// positive, so units take the negative half. A projectile therefore still costs
/// 23 bytes, and "did this round come from its own shooter" stays one comparison.
/// </summary>
public static class OwnerId
{
	/// <summary>No owner: world damage, or a hit against nothing.</summary>
	public const int None = 0;

	/// <summary>Godot's peer ids start at 1, so this is the identity.</summary>
	public static int ForPeer(int peerId) => peerId;

	/// <summary>Unit ids start at 1 for the same reason: 0 has to stay unambiguous.</summary>
	public static int ForUnit(ushort unitId) => -unitId;

	public static bool IsPeer(int ownerId) => ownerId > 0;

	public static bool IsUnit(int ownerId) => ownerId < 0;

	/// <summary>Only meaningful when <see cref="IsUnit"/>; 0 otherwise.</summary>
	public static ushort UnitOf(int ownerId) => ownerId < 0 ? (ushort)(-ownerId) : (ushort)0;

	/// <summary>Only meaningful when <see cref="IsPeer"/>; 0 otherwise.</summary>
	public static int PeerOf(int ownerId) => ownerId > 0 ? ownerId : 0;
}
