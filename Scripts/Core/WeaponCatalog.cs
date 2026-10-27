using Gdpyr.Fps;
using Gdpyr.Sim;
using Godot;

namespace Gdpyr.Core;

/// <summary>
/// Every weapon in the game, in a fixed order.
///
/// The order is a protocol constant, not a convenience: a loadout and a projectile
/// spawn both identify a weapon by a single byte (docs/NETCODE.md §4.2, §7), and
/// that byte means nothing unless the server and every client resolve it to the
/// same weapon. Appending is safe; reordering or removing is a wire break that
/// will silently hand someone else's gun to a player.
///
/// A projectile carries the id of the weapon that launched it. One weapon fires
/// one kind of round in this prototype, so a second id space would be two tables
/// to keep in step for no gain.
/// </summary>
public static class WeaponCatalog
{
	/// <summary>Index is the wire id. Do not reorder.</summary>
	private static readonly string[] Paths =
	{
		"res://Weapons/geopick/geopick.tres",   // 0 — hammer, melee
		"res://Weapons/pistol/pistol.tres",     // 1 — sidearm
		"res://Weapons/dmr/dmr.tres",           // 2 — large
		"res://Weapons/rifle/rifle.tres",       // 3 — large
		"res://Weapons/launcher/launcher.tres", // 4 — large
		"res://Weapons/hmg/hmg.tres",           // 5 — technical (M5)
		"res://Weapons/cannon/cannon.tres",     // 6 — tank (M5)
		"res://Weapons/heavy/heavy.tres",       // 7 — deployed emplacement (M5)
		"res://Weapons/turret/turret.tres",     // 8 — barracks gun
		"res://Weapons/mortar/mortar.tres",     // 9 — barracks mortar
		"res://Weapons/marksman/marksman.tres", // 10 — sniper tower, sniper
		"res://Weapons/lmg/lmg.tres",           // 11 — heavy gunner
		"res://Weapons/grenade/grenade.tres",   // 12 — hand grenade, thrown
	};

	public const byte Hammer = 0;
	public const byte Pistol = 1;
	public const byte Dmr = 2;
	public const byte Rifle = 3;
	public const byte Launcher = 4;

	/// <summary>The technical's gun. Not a loadout choice: no player carries one.</summary>
	public const byte Hmg = 5;

	/// <summary>The tank's gun.</summary>
	public const byte Cannon = 6;

	/// <summary>
	/// The heavy gun a ground-force player deploys and mounts
	/// (docs/IMPLEMENTATION_PLAN.md §M5). It fires the technical's round out of a
	/// bigger belt, and it is the one weapon here with no reload at all: its
	/// magazine is its ammunition, and filling it again is a walk back to the spawn
	/// for a can (<see cref="Fps.EmplacementManager"/>).
	/// </summary>
	public const byte Heavy = 7;

	/// <summary>
	/// A barracks' own gun (docs/NETCODE.md §10.4). The technical's round again, out
	/// of a belt with no bottom: a defence that could run dry is one the ground
	/// force waits out from behind a wall, and then the door is theirs.
	/// </summary>
	public const byte Turret = 8;

	/// <summary>
	/// A barracks' mortar. The one round in the catalog fired on a high arc, and the
	/// answer to cover next to the door.
	/// </summary>
	public const byte Mortar = 9;

	/// <summary>
	/// The marksman in a sniper tower (docs/NETCODE.md §10.5): the DMR's round, one
	/// every two seconds, from a belt with no bottom. Slow enough that a player who
	/// has been seen has a moment to get behind something, and two hits to a kill.
	/// The sniper a tower trains carries the same gun on foot (docs/NETCODE.md §10.8).
	/// </summary>
	public const byte Marksman = 10;

	/// <summary>
	/// The heavy gunner's belt-fed gun (docs/NETCODE.md §10.8): the rifle's round,
	/// faster than a rifle fires it and a hundred and fifty of them before a reload,
	/// which is sixteen seconds of fire. What the gunner is for is how long it keeps
	/// shooting, not how hard (<see cref="Sim.Suppression"/>).
	/// </summary>
	public const byte Lmg = 11;

	/// <summary>
	/// The hand grenade (docs/NETCODE.md §10.9). Not a slot: it is thrown from the off
	/// hand by its own key, whatever is in the other, by a ground-force player and by
	/// a strategist's unit on foot. Its round is on a fuse and its blast is meant for
	/// people — a quarter of it reaches a vehicle or a structure.
	/// </summary>
	public const byte HandGrenade = 12;

	/// <summary>
	/// No primary (docs/LOADOUT.md L-D1): the large slot's id when a kit carries none.
	/// <see cref="Loadout.NoPrimary"/>, which lives in <c>Scripts/Sim</c> so that the
	/// rules testable without the engine can name it.
	/// </summary>
	public const byte None = Loadout.NoPrimary;

	/// <summary>
	/// The large-weapon choice a ground-force player makes before each spawn, and
	/// what a weapon locker holds (docs/NETCODE.md §10.6). The order is the locker's
	/// cycle and, like the catalog's, a protocol constant: the snapshot names the
	/// large weapon a player carries by its index here (<see cref="WeaponFlags"/>).
	/// Appending is safe up to <see cref="SimConfig.LargeWeaponChoices"/>.
	/// </summary>
	public static readonly byte[] LargeWeapons = { Dmr, Rifle, Launcher };

	public static WeaponDefinition[] Definitions { get; private set; } = System.Array.Empty<WeaponDefinition>();

	/// <summary>The simulation's view of each weapon, parallel to <see cref="Definitions"/>.</summary>
	public static WeaponStats[] Stats { get; private set; } = System.Array.Empty<WeaponStats>();

	/// <summary>Each weapon's projectile, indexed by the *weapon's* id.</summary>
	public static ProjectileStats[] Projectiles { get; private set; } = System.Array.Empty<ProjectileStats>();

	/// <summary>
	/// What raising each weapon's sights does (<see cref="Ads"/>), parallel to
	/// <see cref="Definitions"/>. Local to whoever is holding the weapon — an aim
	/// state never goes on the wire — but read from the same catalog on every
	/// process, so a weapon's optic is the same one everywhere.
	/// </summary>
	public static AimStats[] Aim { get; private set; } = System.Array.Empty<AimStats>();

	/// <summary>
	/// What carrying each weapon costs (docs/LOADOUT.md §4.1), parallel to
	/// <see cref="Definitions"/>. Tuned in the <c>.tres</c> files and loaded on every
	/// process, so a kit weighs the same on the server and on the client wearing it.
	/// </summary>
	public static CarryStats[] Carry { get; private set; } = System.Array.Empty<CarryStats>();

	/// <summary>
	/// What the default kit weighs: L-D4's reference, at which a player moves at the
	/// speeds their movement states author. Computed from <see cref="Carry"/> rather
	/// than written down, so the default kit's speed is exactly 1 however the weights
	/// are tuned.
	/// </summary>
	public static float ReferenceWeightKg { get; private set; } = Loadout.BaseKitWeightKg;

	public static bool IsLoaded { get; private set; }

	public static int Count => Definitions.Length;

	/// <summary>
	/// Loads the catalog. Called once, from the combat manager's <c>_Ready</c>;
	/// resource loading on a dedicated server is fine — headless Godot substitutes
	/// placeholder meshes for the stripped visuals and the numbers survive
	/// (docs/DEPLOYMENT.md §2).
	/// </summary>
	public static void Load()
	{
		if (IsLoaded)
		{
			return;
		}

		if (LargeWeapons.Length > SimConfig.LargeWeaponChoices)
		{
			// The snapshot has two bits for which one a player carries; a choice past
			// them would be carried and never reported.
			GD.PushError($"[combat] {LargeWeapons.Length} large weapons; the snapshot can name"
				+ $" {SimConfig.LargeWeaponChoices}");
		}

		var definitions = new WeaponDefinition[Paths.Length];
		var stats = new WeaponStats[Paths.Length];
		var projectiles = new ProjectileStats[Paths.Length];
		var aim = new AimStats[Paths.Length];
		var carry = new CarryStats[Paths.Length];

		for (int i = 0; i < Paths.Length; i++)
		{
			var definition = GD.Load<WeaponDefinition>(Paths[i]);
			if (definition == null)
			{
				// Fatal on purpose. A missing weapon means this build disagrees with
				// every other build about what id `i` is, and a shot fired from it would
				// arrive somewhere nobody can account for.
				GD.PushError($"[combat] weapon {i} missing at {Paths[i]}");
				continue;
			}

			definitions[i] = definition;
			stats[i] = definition.ToStats();
			projectiles[i] = definition.Projectile?.ToStats() ?? default;
			aim[i] = definition.ToAimStats();
			carry[i] = definition.ToCarryStats();
		}

		Definitions = definitions;
		Stats = stats;
		Projectiles = projectiles;
		Aim = aim;
		Carry = carry;
		ReferenceWeightKg = Loadout.WeightKg(DefaultLoadout, carry);
		IsLoaded = true;
	}

	public static WeaponDefinition Definition(byte id) =>
		id < Definitions.Length ? Definitions[id] : null;

	public static WeaponStats StatsFor(byte id) => id < Stats.Length ? Stats[id] : default;

	/// <summary>The round a weapon fires; default for an id this build does not have, or a swing.</summary>
	public static ProjectileStats RoundFor(byte id) => id < Projectiles.Length ? Projectiles[id] : default;

	/// <summary>
	/// What raising this weapon's sights does. <see cref="AimStats.None"/> for an id
	/// this build does not have, which is the same answer as "there is nothing to
	/// raise" and keeps the caller from having to ask twice.
	/// </summary>
	public static AimStats AimFor(byte id) => id < Aim.Length ? Aim[id] : AimStats.None;

	/// <summary>What carrying a weapon costs; nothing for an id this build does not have, or for no primary.</summary>
	public static CarryStats CarryFor(byte id) => id < Carry.Length ? Carry[id] : default;

	public static string NameOf(byte id) =>
		id == None ? "—" : Definition(id)?.Name.ToString() ?? $"#{id}";

	/// <summary>
	/// Clamps a loadout to something this build can actually spawn. Loadout choices
	/// arrive from clients, so "id 200" and "a launcher in the melee slot" are both
	/// things the server has to answer for. The hammer and the pistol are always
	/// carried; the rest is <see cref="Loadout.Sanitize"/>'s (docs/LOADOUT.md §4.1).
	/// </summary>
	public static LoadoutSelection Sanitize(LoadoutSelection loadout)
	{
		LoadoutSelection kit = Loadout.Sanitize(loadout, LargeWeapons, Rifle);
		kit.Melee = Hammer;
		kit.Sidearm = Pistol;
		return kit;
	}

	/// <summary>
	/// Reads a kit spelt out by name — <c>rifle 5 3 none</c> — into one this build can
	/// spawn, or says which rule it breaks (<see cref="Loadout.TryParse"/>).
	/// </summary>
	public static bool TryParseKit(string primary, int magazines, int grenades, string armour,
		out LoadoutSelection kit, out string error)
	{
		if (!Loadout.TryParse(primary, magazines, grenades, armour, LargeWeapons, out kit, out error))
		{
			return false;
		}

		kit = Sanitize(kit);
		return true;
	}

	/// <summary>Whether a kit's choices are already ones this build can spawn (<see cref="Loadout.IsValid"/>).</summary>
	public static bool IsValidKit(in LoadoutSelection kit) => Loadout.IsValid(kit, LargeWeapons);

	/// <summary>What a kit weighs [kg], from the catalog's carry stats.</summary>
	public static float WeightOf(in LoadoutSelection kit) => Loadout.WeightKg(kit, Carry);

	/// <summary>
	/// The speed a kit allows, against the default kit's (L-D4): the factor every
	/// movement state's speed is multiplied by, beside what the player carries in
	/// their arms (docs/LOADOUT.md §5.5).
	/// </summary>
	public static float SpeedScaleOf(in LoadoutSelection kit) => Loadout.SpeedScale(WeightOf(kit), ReferenceWeightKg);

	/// <summary>
	/// The kit a player record says (docs/LOADOUT.md §6.1): the primary from the weapon
	/// byte's large bits, zero for none, and the pouches and the vest from the kit byte.
	/// What a client's prediction and a demo's playback both weigh, so neither needs
	/// more than the record.
	/// </summary>
	public static LoadoutSelection RecordedKit(byte weaponFlags, byte kit) => Sanitize(new LoadoutSelection
	{
		Large = TryLargeWeaponAt(WeaponFlags.LargeChoice(weaponFlags), out byte id) ? id : None,
		Magazines = (byte)KitFlags.Magazines(kit),
		Grenades = (byte)KitFlags.Grenades(kit),
		Armour = KitFlags.Vest(kit),
	});

	/// <summary>A kit in one line, for a log, the console or a death screen: <c>rifle · 5 mags · 3 grenades · no vest</c>.</summary>
	public static string Describe(in LoadoutSelection kit) =>
		$"{(kit.HasPrimary ? NameOf(kit.Large) : "no primary")}"
		+ (kit.HasPrimary ? $" · {kit.Magazines} mag{(kit.Magazines == 1 ? string.Empty : "s")}" : string.Empty)
		+ $" · {kit.Grenades} grenade{(kit.Grenades == 1 ? string.Empty : "s")}"
		+ $" · {(kit.Armour == BodyArmourTier.None ? "no vest" : $"{Loadout.ArmourNames[(int)kit.Armour]} vest")}";

	public static bool IsLargeWeapon(byte id) => LargeChoiceOf(id) >= 0;

	/// <summary>Where a large weapon is in <see cref="LargeWeapons"/>, or -1 for anything else.</summary>
	public static int LargeChoiceOf(byte id) => Loadout.IndexOf(id, LargeWeapons);

	/// <summary>
	/// The large weapon at an index in <see cref="LargeWeapons"/>, as a snapshot
	/// names it. False for an index this build has no weapon at.
	/// </summary>
	public static bool TryLargeWeaponAt(int choice, out byte id)
	{
		bool known = choice >= 0 && choice < LargeWeapons.Length;
		id = known ? LargeWeapons[choice] : Rifle;
		return known;
	}

	/// <summary>
	/// The kit a player who has not chosen one spawns with, every bot's but for its
	/// primary, and every agent seat's (docs/LOADOUT.md L-D3).
	/// </summary>
	public static LoadoutSelection DefaultLoadout => new()
	{
		Melee = Hammer,
		Sidearm = Pistol,
		Large = Rifle,
		Magazines = Loadout.DefaultMagazines,
		Grenades = Loadout.DefaultGrenades,
		Armour = BodyArmourTier.None,
	};
}
