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
	};

	public const byte Hammer = 0;
	public const byte Pistol = 1;
	public const byte Dmr = 2;
	public const byte Rifle = 3;
	public const byte Launcher = 4;

	/// <summary>The large-weapon choice a ground-force player makes before each spawn.</summary>
	public static readonly byte[] LargeWeapons = { Dmr, Rifle, Launcher };

	public static WeaponDefinition[] Definitions { get; private set; } = System.Array.Empty<WeaponDefinition>();

	/// <summary>The simulation's view of each weapon, parallel to <see cref="Definitions"/>.</summary>
	public static WeaponStats[] Stats { get; private set; } = System.Array.Empty<WeaponStats>();

	/// <summary>Each weapon's projectile, indexed by the *weapon's* id.</summary>
	public static ProjectileStats[] Projectiles { get; private set; } = System.Array.Empty<ProjectileStats>();

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

		var definitions = new WeaponDefinition[Paths.Length];
		var stats = new WeaponStats[Paths.Length];
		var projectiles = new ProjectileStats[Paths.Length];

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
		}

		Definitions = definitions;
		Stats = stats;
		Projectiles = projectiles;
		IsLoaded = true;
	}

	public static WeaponDefinition Definition(byte id) =>
		id < Definitions.Length ? Definitions[id] : null;

	public static WeaponStats StatsFor(byte id) => id < Stats.Length ? Stats[id] : default;

	public static string NameOf(byte id) => Definition(id)?.Name.ToString() ?? $"#{id}";

	/// <summary>
	/// Clamps a loadout to something this build can actually spawn. Loadout choices
	/// arrive from clients, so "id 200" and "a launcher in the melee slot" are both
	/// things the server has to answer for.
	/// </summary>
	public static LoadoutSelection Sanitize(LoadoutSelection loadout) => new()
	{
		Melee = Hammer,
		Sidearm = Pistol,
		Large = IsLargeWeapon(loadout.Large) ? loadout.Large : Rifle,
	};

	public static bool IsLargeWeapon(byte id)
	{
		foreach (byte large in LargeWeapons)
		{
			if (large == id)
			{
				return true;
			}
		}
		return false;
	}

	/// <summary>The loadout a player who has not chosen one spawns with.</summary>
	public static LoadoutSelection DefaultLoadout => new()
	{
		Melee = Hammer,
		Sidearm = Pistol,
		Large = Rifle,
	};
}
