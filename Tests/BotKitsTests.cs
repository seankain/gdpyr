using System;
using Gdpyr.Sim;
using Xunit;

namespace Gdpyr.Tests;

/// <summary>
/// What a ground bot carries (docs/LOADOUT.md §8, L-D14): its roster slot's primary
/// and the default kit's pouches, or a kit for that primary's role — what each role
/// holds, weighs and lets it move at.
/// </summary>
public class BotKitsTests
{
	// The catalog's ids and §4.1's numbers, as LoadoutTests has them.
	private const byte Hammer = 0;
	private const byte Pistol = 1;
	private const byte Dmr = 2;
	private const byte Rifle = 3;
	private const byte Launcher = 4;
	private static readonly byte[] LargeWeapons = { Dmr, Rifle, Launcher };

	private static readonly CarryStats[] Carry =
	{
		default,
		default,
		new(4.4f, 10, 0.6f),
		new(3.6f, 30, 0.5f),
		new(6.0f, 2, 0.9f),
	};

	/// <summary>What <c>BotDirector.Equip</c> hands the kits: the default kit with the slot's primary.</summary>
	private static LoadoutSelection Roster(byte large) => new()
	{
		Melee = Hammer,
		Sidearm = Pistol,
		Large = large,
		Magazines = Loadout.DefaultMagazines,
		Grenades = Loadout.DefaultGrenades,
		Armour = BodyArmourTier.None,
	};

	private static float Reference => Loadout.WeightKg(Roster(Rifle), Carry);

	[Theory]
	[InlineData(Rifle, 4, 2, BodyArmourTier.Light, 11.4f, 0.958f, 120)]
	[InlineData(Dmr, 5, 1, BodyArmourTier.None, 9.8f, 0.990f, 50)]
	[InlineData(Launcher, 3, 3, BodyArmourTier.Medium, 17.9f, 0.828f, 6)]
	public void EachRole_CarriesItsPlannedKit(byte primary, int magazines, int grenades, BodyArmourTier armour,
		float kg, float scale, int spareRounds)
	{
		LoadoutSelection kit = BotKitNames.KitFor(BotKits.Role, Roster(primary), LargeWeapons);

		Assert.Equal(primary, kit.Large);
		Assert.Equal(magazines, kit.Magazines);
		Assert.Equal(grenades, kit.Grenades);
		Assert.Equal(armour, kit.Armour);
		Assert.Equal(Hammer, kit.Melee);
		Assert.Equal(Pistol, kit.Sidearm);
		Assert.Equal(kg, Loadout.WeightKg(kit, Carry), 3);
		Assert.Equal(scale, Loadout.SpeedScale(Loadout.WeightKg(kit, Carry), Reference), 3);
		Assert.Equal(spareRounds, Loadout.ReserveFor(kit, Carry[primary]));

		// A kit the locker's validator would take as it is, so a bot is never in one a
		// person could not choose.
		Assert.True(Loadout.IsValid(kit, LargeWeapons));
	}

	[Fact]
	public void TheHeaviestRoleKit_IsInsideTheSpeedFloorTheScenariosAreSizedFor()
	{
		// docs/LOADOUT.md L4: a scenario whose claims wait on ground bots walking is sized
		// for the floor, 0.70; the launcher's role kit is the slowest a bot is in either.
		foreach (byte primary in LargeWeapons)
		{
			LoadoutSelection kit = BotKitNames.KitFor(BotKits.Role, Roster(primary), LargeWeapons);
			Assert.True(Loadout.SpeedScale(Loadout.WeightKg(kit, Carry), Reference) > Loadout.MinSpeedScale);
		}
	}

	[Theory]
	[InlineData(Rifle)]
	[InlineData(Dmr)]
	[InlineData(Launcher)]
	public void TheDefaultKits_LeaveTheRosterKitAsItIs(byte primary)
	{
		Assert.Equal(Roster(primary), BotKitNames.KitFor(BotKits.Default, Roster(primary), LargeWeapons));
	}

	[Fact]
	public void APrimaryThatIsNoRole_KeepsTheRosterKit()
	{
		LoadoutSelection none = Roster(Loadout.NoPrimary);
		none.Magazines = 0;

		Assert.Equal(none, BotKitNames.KitFor(BotKits.Role, none, LargeWeapons));
		Assert.Equal(Roster(Pistol), BotKitNames.KitFor(BotKits.Role, Roster(Pistol), LargeWeapons));
	}

	[Fact]
	public void EveryRolesKit_IsADifferentTradeFromTheDefault()
	{
		// Rifle and launcher wear a vest, the DMR does not; each gives up pouches the
		// default kit has. Nothing here is the default kit under another name.
		foreach (byte primary in LargeWeapons)
		{
			LoadoutSelection kit = BotKitNames.KitFor(BotKits.Role, Roster(primary), LargeWeapons);
			Assert.NotEqual(Roster(primary), kit);
			Assert.True(kit.Magazines + kit.Grenades < Loadout.PouchSlots);
		}
	}

	[Fact]
	public void TheRolesFollowTheLargeWeaponsProtocolOrder_NotTheirIds()
	{
		// The table is read by the primary's place in the large weapons (DMR, rifle,
		// launcher), so the same ids in another order give each primary another role's kit.
		byte[] shuffled = { Rifle, Dmr, Launcher };
		Assert.Equal(BodyArmourTier.None, BotKitNames.KitFor(BotKits.Role, Roster(Rifle), shuffled).Armour);
		Assert.Equal(BodyArmourTier.Light, BotKitNames.KitFor(BotKits.Role, Roster(Rifle), LargeWeapons).Armour);
	}
}
