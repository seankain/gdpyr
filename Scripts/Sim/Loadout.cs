using System;

namespace Gdpyr.Sim;

/// <summary>
/// A ground-force player's vest (docs/LOADOUT.md L-D6). Chosen with the kit and
/// carried on the record from L1; what one absorbs is <c>BodyArmour</c>'s, from L3.
/// Named apart from <see cref="Armour"/>, the rule for tanks and structures.
/// </summary>
public enum BodyArmourTier : byte
{
	None = 0,
	Light = 1,
	Medium = 2,
	Heavy = 3,
}

/// <summary>
/// What carrying a weapon costs and what a pouch of its ammunition holds
/// (docs/LOADOUT.md §4.1). Per weapon, authored on its definition and loaded beside
/// its <see cref="WeaponStats"/>, so every process weighs a kit the same.
/// </summary>
public readonly struct CarryStats
{
	/// <summary>The weapon itself, with its first magazine.</summary>
	public readonly float WeightKg;

	/// <summary>Rounds one pouch holds: a magazine, or two launcher grenades. 0 for a weapon with no pouches.</summary>
	public readonly int PouchRounds;

	public readonly float PouchWeightKg;

	public CarryStats(float weightKg, int pouchRounds, float pouchWeightKg)
	{
		WeightKg = MathF.Max(weightKg, 0f);
		PouchRounds = Math.Max(pouchRounds, 0);
		PouchWeightKg = MathF.Max(pouchWeightKg, 0f);
	}
}

/// <summary>
/// The loadout's rules (docs/LOADOUT.md §4.1): what a kit may hold, what it weighs,
/// how fast it lets its carrier move and how many spare rounds it comes with.
///
/// Engine-free, and handed the catalog's numbers rather than reading them, because
/// the server and the owning client both derive a player's speed and reserve from
/// these and they have to agree to the round (docs/NETCODE.md §3.1).
/// </summary>
public static class Loadout
{
	/// <summary>
	/// A large slot with nothing in it: no primary (L-D1). Not a catalog id, and the
	/// one value outside the catalog a sanitized kit may hold.
	/// </summary>
	public const byte NoPrimary = byte.MaxValue;

	/// <summary>
	/// Pouches a kit has, each a reload of the primary or a hand grenade (L-D1). Nine,
	/// so the default kit below keeps today's three grenades with one to spare.
	/// </summary>
	public const int PouchSlots = 9;

	/// <summary>
	/// Spare magazines in the default kit (L-D3): the fewest that cover the 95th
	/// percentile of rifle rounds bots fired in a life, 170 (docs/LOADOUT.md L0).
	/// </summary>
	public const int DefaultMagazines = 5;

	/// <summary>Grenades in the default kit: today's three.</summary>
	public const int DefaultGrenades = SimConfig.MaxGrenades;

	/// <summary>The hammer, the pistol and its magazines, which every kit carries.</summary>
	public const float BaseKitWeightKg = 2f;

	/// <summary>One hand grenade in its pouch.</summary>
	public const float GrenadeWeightKg = 0.4f;

	/// <summary>Speed lost per kilogram over the default kit, and gained per kilogram under it (L-D4).</summary>
	public const float SpeedPerKg = 0.02f;

	public const float MinSpeedScale = 0.70f;
	public const float MaxSpeedScale = 1.12f;

	/// <summary>
	/// The primaries a kit may name, as a request spells them: "none", then the large
	/// weapons in the order <c>WeaponCatalog.LargeWeapons</c> holds them, which is a
	/// protocol constant (docs/NETCODE.md §10.6).
	/// </summary>
	public static readonly string[] PrimaryNames = { "none", "dmr", "rifle", "launcher" };

	/// <summary>The vests a kit may name, by <see cref="BodyArmourTier"/>.</summary>
	public static readonly string[] ArmourNames = { "none", "light", "medium", "heavy" };

	private static readonly float[] VestWeightsKg = { 0f, 3f, 6f, 10f };

	/// <summary>A vest's weight [kg]; nothing for no vest, or for a tier this build does not have.</summary>
	public static float VestWeightKg(BodyArmourTier tier) =>
		(byte)tier < VestWeightsKg.Length ? VestWeightsKg[(byte)tier] : 0f;

	/// <summary>
	/// Clamps a kit to one this build can spawn (§4.1). A primary that is not a large
	/// weapon becomes <paramref name="unknownLarge"/> — the rifle, as before L1 — and
	/// <see cref="NoPrimary"/> is kept; no primary carries no magazines; at most
	/// <see cref="SimConfig.MaxGrenades"/> grenades; magazines are trimmed before
	/// grenades to fit <see cref="PouchSlots"/>; a vest this build has no tier for is
	/// none. The melee and sidearm slots are the caller's, as the catalog's ids are.
	/// </summary>
	public static LoadoutSelection Sanitize(LoadoutSelection kit, ReadOnlySpan<byte> largeWeapons, byte unknownLarge)
	{
		if (kit.Large != NoPrimary && IndexOf(kit.Large, largeWeapons) < 0)
		{
			kit.Large = unknownLarge;
		}

		kit.Grenades = (byte)Math.Min((int)kit.Grenades, SimConfig.MaxGrenades);
		kit.Magazines = kit.Large == NoPrimary
			? (byte)0
			: (byte)Math.Min((int)kit.Magazines, PouchSlots - kit.Grenades);

		if (kit.Armour > BodyArmourTier.Heavy)
		{
			kit.Armour = BodyArmourTier.None;
		}

		return kit;
	}

	/// <summary>
	/// Whether <see cref="Sanitize"/> would leave a kit's choices as they are — the
	/// primary, the pouches and the vest — which is what a locker asks of a request
	/// rather than trimming it (docs/LOADOUT.md §5.3): a kit the asker did not mean is
	/// worse than a refusal that says why. The melee and sidearm slots are not the
	/// asker's to choose and are not compared.
	/// </summary>
	public static bool IsValid(in LoadoutSelection kit, ReadOnlySpan<byte> largeWeapons)
	{
		// An unknown primary is sanitized to none here, never to itself, so it is unequal.
		LoadoutSelection sane = Sanitize(kit, largeWeapons, NoPrimary);
		return sane.Large == kit.Large && sane.Magazines == kit.Magazines && sane.Grenades == kit.Grenades
			&& sane.Armour == kit.Armour;
	}

	/// <summary>Where <paramref name="id"/> is in <paramref name="largeWeapons"/>, or -1.</summary>
	public static int IndexOf(byte id, ReadOnlySpan<byte> largeWeapons)
	{
		for (int i = 0; i < largeWeapons.Length; i++)
		{
			if (largeWeapons[i] == id)
			{
				return i;
			}
		}

		return -1;
	}

	/// <summary>
	/// What a kit taken at a locker leaves in the large slot (L-D10): a different
	/// primary comes with a full magazine and full pouches, as at a spawn; the same one
	/// keeps its magazine and its spare rounds — fewer pouches lose what no longer
	/// fits, and more come empty, left to the locker's resupply. Opening the panel under
	/// fire then cannot refill what the resupply's lockout would have refused.
	/// </summary>
	public static WeaponState RefitPrimary(in WeaponState carried, in LoadoutSelection kit, in WeaponStats stats,
		in CarryStats carry)
	{
		if (!kit.HasPrimary)
		{
			return WeaponState.Empty;
		}

		if (carried.DefinitionId != kit.Large)
		{
			return WeaponState.Ready(kit.Large, stats, ReserveFor(kit, carry));
		}

		WeaponState kept = carried;
		if (!kept.HasUnlimitedReserve)
		{
			kept.Reserve = (short)Math.Min((int)kept.Reserve, ReserveFor(kit, carry));
		}

		return kept;
	}

	/// <summary>
	/// The grenades a kit taken at a locker leaves: those carried, up to its grenade
	/// pouches. A pouch added is empty until resupplied, as a magazine's is (L-D10).
	/// </summary>
	public static byte RefitGrenades(byte carried, in LoadoutSelection kit) => Math.Min(carried, kit.Grenades);

	/// <summary>
	/// The vest a kit taken at a locker leaves (L-D10): a different one full, as at a
	/// spawn; the same one with the points it has left, which only the resupply puts
	/// back (<see cref="LockerSupply.Step"/>). Taking off a vest and putting it back on is two kits and two tiers,
	/// and so a full vest — as walking away from the locker and back is not.
	/// </summary>
	public static BodyArmourState RefitVest(in BodyArmourState worn, in LoadoutSelection kit) =>
		worn.Tier == kit.Armour ? new BodyArmourState(worn.Tier, worn.Points) : BodyArmourState.Full(kit.Armour);

	/// <summary>
	/// What a kit weighs [kg]: the base kit, the primary and its pouches, the grenades
	/// and the vest. <paramref name="carry"/> is indexed by weapon id, as the catalog is.
	/// </summary>
	public static float WeightKg(in LoadoutSelection kit, ReadOnlySpan<CarryStats> carry)
	{
		float kg = BaseKitWeightKg + (kit.Grenades * GrenadeWeightKg) + VestWeightKg(kit.Armour);
		if (kit.Large != NoPrimary && kit.Large < carry.Length)
		{
			CarryStats primary = carry[kit.Large];
			kg += primary.WeightKg + (kit.Magazines * primary.PouchWeightKg);
		}

		return kg;
	}

	/// <summary>
	/// L-D4's line: 1 at the default kit's weight, 2 % faster for every kilogram under
	/// it and 2 % slower for every one over, between 0.70 and 1.12. The reference is
	/// handed in rather than written down, so that the default kit is exactly 1 however
	/// the weights are tuned.
	/// </summary>
	public static float SpeedScale(float weightKg, float referenceKg) =>
		Math.Clamp(1f + (SpeedPerKg * (referenceKg - weightKg)), MinSpeedScale, MaxSpeedScale);

	/// <summary>The spare rounds a kit's primary starts with: a pouch's rounds for each magazine pouch.</summary>
	public static int ReserveFor(in LoadoutSelection kit, in CarryStats primary) =>
		kit.Large == NoPrimary ? 0 : kit.Magazines * primary.PouchRounds;

	/// <summary>
	/// Reads a kit spelt out — by a console line, a scenario or an agent's request —
	/// into a selection, refusing rather than trimming one that breaks a rule, so that
	/// whoever asked is told which. <paramref name="largeWeapons"/> is the catalog's
	/// large weapons, in <see cref="PrimaryNames"/>' order after "none".
	/// </summary>
	public static bool TryParse(string primary, int magazines, int grenades, string armour,
		ReadOnlySpan<byte> largeWeapons, out LoadoutSelection kit, out string error)
	{
		kit = default;
		int choice = Array.IndexOf(PrimaryNames, primary?.ToLowerInvariant());
		if (choice < 0 || choice > largeWeapons.Length)
		{
			error = $"primary is {string.Join(", ", PrimaryNames)}, not '{primary}'";
			return false;
		}

		int tier = Array.IndexOf(ArmourNames, (armour ?? ArmourNames[0]).ToLowerInvariant());
		if (tier < 0)
		{
			error = $"armour is {string.Join(", ", ArmourNames)}, not '{armour}'";
			return false;
		}

		if (grenades < 0 || grenades > SimConfig.MaxGrenades)
		{
			error = $"grenades is 0 to {SimConfig.MaxGrenades}, not {grenades}";
			return false;
		}

		if (magazines < 0 || magazines + grenades > PouchSlots)
		{
			error = $"{magazines} magazines and {grenades} grenades do not fit {PouchSlots} pouches";
			return false;
		}

		if (choice == 0 && magazines > 0)
		{
			error = "no primary carries no magazines";
			return false;
		}

		kit = new LoadoutSelection
		{
			Large = choice == 0 ? NoPrimary : largeWeapons[choice - 1],
			Magazines = (byte)magazines,
			Grenades = (byte)grenades,
			Armour = (BodyArmourTier)tier,
		};
		error = null;
		return true;
	}
}
