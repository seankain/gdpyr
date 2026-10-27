using System;

namespace Gdpyr.Sim;

/// <summary>
/// The kit a player spawned or was resupplied with, packed into the player record's
/// last byte (docs/LOADOUT.md §6.1): vest 2 bits · magazine pouches 4 · grenade
/// pouches 2. With the large weapon the weapon byte already names, it is the whole
/// kit, and so what the owning client derives its reserve, its grenade ceiling and
/// its speed from, without a message of its own (L-D13).
///
/// Beside it, bytes 29–30 carry the primary's spare rounds and the vest's points as
/// one 16-bit word, 9 bits and 7: a rifle's nine pouches are 270 rounds, past a byte,
/// and the heaviest vest's 100 points fit in seven.
/// </summary>
public static class KitFlags
{
	private const int VestMask = 0b11;
	private const int MagazineShift = 2;
	private const int MagazineMask = 0b1111 << MagazineShift;
	private const int GrenadeShift = 6;
	private const int GrenadeMask = 0b11 << GrenadeShift;

	/// <summary>The most spare rounds the record can say: nine bits.</summary>
	public const int MaxReserve = (1 << 9) - 1;

	/// <summary>The most vest points the record can say: seven bits.</summary>
	public const int MaxArmourPoints = (1 << 7) - 1;

	public static byte Pack(in LoadoutSelection kit) =>
		(byte)(((int)kit.Armour & VestMask)
			| ((Math.Min((int)kit.Magazines, 15) << MagazineShift) & MagazineMask)
			| ((Math.Min((int)kit.Grenades, SimConfig.MaxGrenades) << GrenadeShift) & GrenadeMask));

	public static BodyArmourTier Vest(byte kit) => (BodyArmourTier)(kit & VestMask);

	/// <summary>Magazine pouches. Up to 15 from a malformed byte; whoever reads it sanitizes the kit.</summary>
	public static int Magazines(byte kit) => (kit & MagazineMask) >> MagazineShift;

	public static int Grenades(byte kit) => (kit & GrenadeMask) >> GrenadeShift;

	/// <summary>Spare rounds in the low nine bits, vest points in the high seven, each clamped to its field.</summary>
	public static ushort PackSpare(int reserve, int armourPoints) =>
		(ushort)(Math.Clamp(reserve, 0, MaxReserve) | (Math.Clamp(armourPoints, 0, MaxArmourPoints) << 9));

	public static int Reserve(ushort spare) => spare & MaxReserve;

	public static int ArmourPoints(ushort spare) => spare >> 9;
}
