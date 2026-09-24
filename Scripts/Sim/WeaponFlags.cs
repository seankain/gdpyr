using System;

namespace Gdpyr.Sim;

/// <summary>
/// The equipped slot, the reload state, the player's team and which large weapon
/// they are carrying, packed into the snapshot's one spare byte
/// (docs/NETCODE.md §7).
///
/// One byte rather than four fields because it is per player per snapshot at
/// 30 Hz, and because everything in it is either two bits wide or a flag.
///
/// The team bit was M3's addition. It could have been its own reliable message —
/// a player changes side about once a round — but the snapshot is the one thing a
/// client is never wrong about for long, and a missed side-change would leave a
/// strategist's abandoned body being shot at.
///
/// The large weapon came with the weapon locker (docs/NETCODE.md §10.6), for the
/// same reason: it changes mid-life now, and the owning client predicts its own
/// weapon, so it has to be told which one it is holding by something that cannot
/// be missed. It is an index into the large-weapon choices plus one, so that a
/// zero — every byte written before it existed, including every demo — means "not
/// said" rather than "the first choice".
/// </summary>
public static class WeaponFlags
{
	private const int SlotMask = 0b11;
	private const int ReloadingBit = 1 << 2;
	private const int StrategistBit = 1 << 3;
	private const int LargeShift = 4;
	private const int LargeMask = 0b11 << LargeShift;

	/// <param name="largeChoice">
	/// Which of the large weapons the player is carrying, as an index into the
	/// choices (<c>WeaponCatalog.LargeWeapons</c>), or -1 to say nothing.
	/// </param>
	public static byte Pack(int slot, bool reloading, Team team, int largeChoice = -1)
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
		if (largeChoice >= 0 && largeChoice < SimConfig.LargeWeaponChoices)
		{
			packed |= ((largeChoice + 1) << LargeShift) & LargeMask;
		}
		return (byte)packed;
	}

	public static int Slot(byte flags) => Math.Min(flags & SlotMask, SimConfig.WeaponSlots - 1);

	public static bool IsReloading(byte flags) => (flags & ReloadingBit) != 0;

	public static Team TeamOf(byte flags) => (flags & StrategistBit) != 0 ? Team.Strategist : Team.GroundForce;

	/// <summary>
	/// Which large weapon the player is carrying, as an index into the choices, or
	/// -1 when the byte does not say — an old demo, or a value this build has no
	/// weapon for.
	/// </summary>
	public static int LargeChoice(byte flags)
	{
		int choice = ((flags & LargeMask) >> LargeShift) - 1;
		return choice < SimConfig.LargeWeaponChoices ? choice : -1;
	}
}
