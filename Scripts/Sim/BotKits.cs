using System;

namespace Gdpyr.Sim;

/// <summary>
/// What a ground bot carries besides the primary its roster slot gives it
/// (docs/LOADOUT.md §8, L-D14). Chosen by <c>--bot-kits</c>, else by the game mode's
/// <c>BotKits</c>. Only bots that plan with the HTN take a role's kit: legacy is the
/// training baseline and keeps the default.
/// </summary>
public enum BotKits : byte
{
	/// <summary>The default kit's pouches and vest — five magazines, three grenades, none — whatever the primary.</summary>
	Default = 0,

	/// <summary>A kit for the primary's role: a rifleman lightly armoured, a marksman light on its feet, a launcher heavy.</summary>
	Role = 1,
}

/// <summary>The spellings <c>--bot-kits</c> and a scenario take, and the role kits themselves.</summary>
public static class BotKitNames
{
	public const string Default = "default";
	public const string Role = "role";

	/// <summary>
	/// What bots carry when neither <c>--bot-kits</c> nor the game mode says. The role
	/// kits become it only if bot-only rounds in them leave the ground force's tickets
	/// lost per minute within the spread L0 measured (docs/LOADOUT.md §10 L7).
	/// </summary>
	public const BotKits Fallback = BotKits.Default;

	/// <summary><c>--bot-kits</c>, else the game mode's, else <see cref="Fallback"/>.</summary>
	public static BotKits Resolve(BotKits? option, BotKits? gameMode) => option ?? gameMode ?? Fallback;

	public static bool TryParse(string text, out BotKits kits)
	{
		switch (text?.Trim().ToLowerInvariant())
		{
			case Default:
				kits = BotKits.Default;
				return true;
			case Role:
				kits = BotKits.Role;
				return true;
			default:
				kits = BotKits.Default;
				return false;
		}
	}

	public static string Name(BotKits kits) => kits == BotKits.Role ? Role : Default;

	/// <summary>Magazine pouches, grenade pouches and vest of each role, by the primary's place in the catalog's large weapons.</summary>
	private static readonly (byte Magazines, byte Grenades, BodyArmourTier Armour)[] RoleKits =
	{
		// DMR: 5 mags, 1 grenade, no vest — 9.8 kg, 0.99.
		(5, 1, BodyArmourTier.None),

		// Rifle: 4 mags, 2 grenades, light — 11.4 kg, 0.958.
		(4, 2, BodyArmourTier.Light),

		// Launcher: 3 pouches (6 rounds), 3 grenades, medium — 17.9 kg, 0.828.
		(3, 3, BodyArmourTier.Medium),
	};

	/// <summary>
	/// The kit a bot spawns in under <paramref name="kits"/> (docs/LOADOUT.md §8): the
	/// roster's kit unchanged under <see cref="BotKits.Default"/>, else its primary
	/// with that primary's role's pouches and vest. <paramref name="largeWeapons"/> is
	/// the catalog's large weapons in their protocol order — DMR, rifle, launcher — and a
	/// primary that is none of them keeps the roster's kit.
	/// </summary>
	public static LoadoutSelection KitFor(BotKits kits, in LoadoutSelection rosterKit, ReadOnlySpan<byte> largeWeapons)
	{
		int role = Loadout.IndexOf(rosterKit.Large, largeWeapons);
		if (kits != BotKits.Role || role < 0 || role >= RoleKits.Length)
		{
			return rosterKit;
		}

		LoadoutSelection kit = rosterKit;
		kit.Magazines = RoleKits[role].Magazines;
		kit.Grenades = RoleKits[role].Grenades;
		kit.Armour = RoleKits[role].Armour;
		return kit;
	}
}
