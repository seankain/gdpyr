using System;

namespace Gdpyr.Sim;

/// <summary>
/// The equipped slot, the reload state and the player's team, packed into the
/// snapshot's one spare byte (docs/NETCODE.md §7).
///
/// One byte rather than three fields because it is per player per snapshot at
/// 30 Hz, and because everything in it is either two bits wide or a flag.
///
/// The team bit was M3's addition. It could have been its own reliable message —
/// a player changes side about once a round — but the snapshot is the one thing a
/// client is never wrong about for long, and a missed side-change would leave a
/// strategist's abandoned body being shot at.
/// </summary>
public static class WeaponFlags
{
	private const int SlotMask = 0b11;
	private const int ReloadingBit = 1 << 2;
	private const int StrategistBit = 1 << 3;

	public static byte Pack(int slot, bool reloading, Team team)
	{
		int packed = Math.Clamp(slot, 0, SimConfig.WeaponSlots - 1) & SlotMask;
		if (reloading)
		{
			packed |= ReloadingBit;
		}
		if (team == Team.Strategist)
		{
			packed |= StrategistBit;
		}
		return (byte)packed;
	}

	public static int Slot(byte flags) => Math.Min(flags & SlotMask, SimConfig.WeaponSlots - 1);

	public static bool IsReloading(byte flags) => (flags & ReloadingBit) != 0;

	public static Team TeamOf(byte flags) => (flags & StrategistBit) != 0 ? Team.Strategist : Team.GroundForce;
}
