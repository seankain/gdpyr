using System;

namespace Gdpyr.Sim;

/// <summary>
/// The equipped slot and reload state packed into the snapshot's one spare byte
/// (docs/NETCODE.md §7).
///
/// One byte rather than two fields because it is per player per snapshot at
/// 30 Hz, and because everything in it is either two bits wide or a flag.
/// </summary>
public static class WeaponFlags
{
	private const int SlotMask = 0b11;
	private const int ReloadingBit = 1 << 2;

	public static byte Pack(int slot, bool reloading)
	{
		int packed = Math.Clamp(slot, 0, SimConfig.WeaponSlots - 1) & SlotMask;
		if (reloading)
		{
			packed |= ReloadingBit;
		}
		return (byte)packed;
	}

	public static int Slot(byte flags) => Math.Min(flags & SlotMask, SimConfig.WeaponSlots - 1);

	public static bool IsReloading(byte flags) => (flags & ReloadingBit) != 0;
}
