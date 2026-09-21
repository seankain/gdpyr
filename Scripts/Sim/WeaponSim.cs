using System;

namespace Gdpyr.Sim;

/// <summary>How a weapon responds to the trigger.</summary>
public enum FireMode : byte
{
	/// <summary>One round per trigger pull.</summary>
	Semi,

	/// <summary>Fires for as long as the trigger is held.</summary>
	Auto,

	/// <summary>
	/// One round, then a cycle before the next: a single-shot launcher or a bolt
	/// gun. Mechanically a <see cref="Semi"/> weapon with a magazine of one, whose
	/// reload is the cycling.
	/// </summary>
	Single,
}

/// <summary>What one weapon does, in ticks and rounds rather than seconds and RPM.</summary>
public readonly struct WeaponStats
{
	public readonly FireMode Mode;

	/// <summary>Minimum ticks between two shots. Never below one: the tick is the floor on rate of fire.</summary>
	public readonly int TicksBetweenShots;

	/// <summary>Rounds per magazine, or 0 for a weapon that does not consume ammunition.</summary>
	public readonly int MagazineSize;

	public readonly int ReloadTicks;

	/// <summary>Damage to an unarmoured target at the point of impact.</summary>
	public readonly float Damage;

	/// <summary>
	/// True for a weapon resolved as an instantaneous swing rather than as a
	/// projectile. The sim treats the two identically; only the resolution differs.
	/// </summary>
	public readonly bool IsMelee;

	/// <summary>Reach of a melee swing [m]. Zero for everything else.</summary>
	public readonly float RangeMeters;

	/// <summary>Sweep radius of a melee swing [m]: a hammer is a shapecast, not a ray.</summary>
	public readonly float SweepRadiusMeters;

	public WeaponStats(FireMode mode, int ticksBetweenShots, int magazineSize, int reloadTicks, float damage,
		bool isMelee = false, float rangeMeters = 0f, float sweepRadiusMeters = 0f)
	{
		Mode = mode;
		TicksBetweenShots = Math.Max(ticksBetweenShots, 1);
		MagazineSize = Math.Max(magazineSize, 0);
		ReloadTicks = Math.Max(reloadTicks, 0);
		Damage = damage;
		IsMelee = isMelee;
		RangeMeters = rangeMeters;
		SweepRadiusMeters = sweepRadiusMeters;
	}

	/// <summary>True for a weapon with no magazine to run dry, such as the hammer.</summary>
	public bool HasUnlimitedAmmo => MagazineSize == 0;

	/// <summary>
	/// Rate of fire as a whole number of ticks. Rounding here rather than carrying a
	/// fractional accumulator is deliberate: a weapon's cadence has to be a function
	/// of the tick index alone, or a replayed tick could produce a shot the original
	/// did not. The cost is that authored RPM is quantized — at 60 Hz, 600 RPM is
	/// exact and 700 RPM becomes 720.
	/// </summary>
	public static int TicksPerShot(float roundsPerMinute) =>
		roundsPerMinute <= 0f ? 1 : Math.Max(1, (int)MathF.Round(SimConfig.TickRate * 60f / roundsPerMinute));

	public static int SecondsToTicks(float seconds) =>
		seconds <= 0f ? 0 : Math.Max(1, (int)MathF.Round(seconds * SimConfig.TickRate));
}

/// <summary>
/// One weapon's mutable state. A struct, and small: it is copied per tick, and on
/// a client it is copied again every time the HUD asks what it holds.
/// </summary>
public struct WeaponState
{
	/// <summary>Index into the weapon catalog. A protocol value, not a pointer.</summary>
	public byte DefinitionId;

	public short Ammo;

	/// <summary>The earliest tick this weapon may fire on.</summary>
	public uint NextFireTick;

	/// <summary>The tick a reload in progress completes on, or 0 when none is.</summary>
	public uint ReloadEndTick;

	public readonly bool IsReloading => ReloadEndTick != 0;

	public static WeaponState Ready(byte definitionId, in WeaponStats stats) => new()
	{
		DefinitionId = definitionId,
		Ammo = (short)stats.MagazineSize,
		NextFireTick = 0,
		ReloadEndTick = 0,
	};
}

/// <summary>What a weapon did on one tick.</summary>
public enum WeaponAction : byte
{
	None,
	Fire,
	ReloadStart,
	ReloadFinish,

	/// <summary>The trigger was pulled on an empty magazine with no reload possible.</summary>
	DryFire,
}

/// <summary>
/// The firing half of the character simulation: a pure function of
/// (state, stats, input, tick), for exactly the reason movement is
/// (docs/NETCODE.md §3.1).
///
/// The server is the only authority on whether a shot happened — it runs this over
/// the inputs it received — but the owning client runs the identical function on
/// the identical frame, which is what lets it put a tracer and an ammo count on
/// screen on the same tick it pulled the trigger, without waiting a round trip
/// (docs/NETCODE.md §4.3).
///
/// Everything that could make the two disagree is kept out: no wall-clock time, no
/// random spread, no <c>Input</c>.
/// </summary>
public static class WeaponSim
{
	/// <summary>
	/// Advances one weapon by a tick and reports what it did. The caller turns a
	/// <see cref="WeaponAction.Fire"/> into a projectile or a swing; this function
	/// knows nothing about either.
	/// </summary>
	public static WeaponAction Step(ref WeaponState state, in WeaponStats stats, in InputContext input, uint tick)
	{
		if (state.IsReloading)
		{
			if (tick < state.ReloadEndTick)
			{
				return WeaponAction.None;
			}

			state.Ammo = (short)stats.MagazineSize;
			state.ReloadEndTick = 0;

			// The round that just finished chambering cannot also be fired this tick;
			// without this a held trigger fires on the reload's last tick.
			state.NextFireTick = Math.Max(state.NextFireTick, tick + 1);
			return WeaponAction.ReloadFinish;
		}

		if (input.JustPressed(InputButtons.Reload) && CanReload(state, stats))
		{
			return BeginReload(ref state, stats, tick);
		}

		bool wantsToFire = stats.Mode == FireMode.Auto
			? input.Held(InputButtons.Fire)
			: input.JustPressed(InputButtons.Fire);

		if (!wantsToFire || tick < state.NextFireTick)
		{
			return WeaponAction.None;
		}

		if (!stats.HasUnlimitedAmmo && state.Ammo <= 0)
		{
			// An empty magazine reloads itself rather than clicking: for a
			// single-shot launcher this *is* the cycling, and for everything else it
			// is what a player would have pressed next anyway.
			return CanReload(state, stats) ? BeginReload(ref state, stats, tick) : WeaponAction.DryFire;
		}

		if (!stats.HasUnlimitedAmmo)
		{
			state.Ammo--;
		}
		state.NextFireTick = tick + (uint)stats.TicksBetweenShots;
		return WeaponAction.Fire;
	}

	/// <summary>
	/// The slot the character has switched to, from the weapon keys. Edge-triggered
	/// off the recorded frame rather than the device, so a replay picks the same
	/// weapon the original tick did.
	/// </summary>
	public static int SelectSlot(int currentSlot, in InputContext input)
	{
		if (input.JustPressed(InputButtons.Weapon1)) { return 0; }
		if (input.JustPressed(InputButtons.Weapon2)) { return 1; }
		if (input.JustPressed(InputButtons.Weapon3)) { return 2; }
		return currentSlot;
	}

	private static bool CanReload(in WeaponState state, in WeaponStats stats) =>
		!stats.HasUnlimitedAmmo && stats.ReloadTicks > 0 && state.Ammo < stats.MagazineSize;

	private static WeaponAction BeginReload(ref WeaponState state, in WeaponStats stats, uint tick)
	{
		state.ReloadEndTick = tick + (uint)stats.ReloadTicks;
		return WeaponAction.ReloadStart;
	}
}

/// <summary>
/// A ground-force player's three weapons, as catalog ids. Chosen before a spawn
/// and fixed until the next one (docs/IMPLEMENTATION_PLAN.md §M2) — swapping mid-life
/// would make every respawn a loadout screen and every firefight a menu.
/// </summary>
public struct LoadoutSelection
{
	public byte Melee;
	public byte Sidearm;
	public byte Large;

	public readonly byte this[int slot] => slot switch
	{
		0 => Melee,
		1 => Sidearm,
		_ => Large,
	};
}
