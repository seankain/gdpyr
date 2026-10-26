using System;
using Gdpyr.Sim;
using Xunit;

namespace Gdpyr.Tests;

/// <summary>
/// The loadout's rules (docs/LOADOUT.md §4.1): what a kit may hold, what it weighs,
/// how fast it moves and what it carries spare.
/// </summary>
public class LoadoutTests
{
	// The catalog's ids and §4.1's numbers, as WeaponCatalog loads them from the .tres
	// files; the catalog itself loads resources and is not compiled in here.
	private const byte Hammer = 0;
	private const byte Pistol = 1;
	private const byte Dmr = 2;
	private const byte Rifle = 3;
	private const byte Launcher = 4;
	private static readonly byte[] LargeWeapons = { Dmr, Rifle, Launcher };

	private static readonly CarryStats[] Carry =
	{
		default,                         // hammer: in the base kit
		default,                         // pistol: in the base kit
		new(4.4f, 10, 0.6f),             // DMR
		new(3.6f, 30, 0.5f),             // rifle
		new(6.0f, 2, 0.9f),              // launcher
	};

	private static LoadoutSelection Kit(byte large, int magazines, int grenades,
		BodyArmourTier armour = BodyArmourTier.None) => new()
	{
		Melee = Hammer,
		Sidearm = Pistol,
		Large = large,
		Magazines = (byte)magazines,
		Grenades = (byte)grenades,
		Armour = armour,
	};

	private static LoadoutSelection Default => Kit(Rifle, Loadout.DefaultMagazines, Loadout.DefaultGrenades);

	private static float Reference => Loadout.WeightKg(Default, Carry);

	private static LoadoutSelection Sanitize(LoadoutSelection kit) => Loadout.Sanitize(kit, LargeWeapons, Rifle);

	[Fact]
	public void TheDefaultKitIsARifleFiveMagazinesThreeGrenadesAndNoVest()
	{
		// L-D3, as L0 measured it: five magazines cover the 95th percentile of rifle
		// rounds bots fired in a life (170), and three grenades are today's three.
		Assert.Equal(5, Loadout.DefaultMagazines);
		Assert.Equal(3, Loadout.DefaultGrenades);
		Assert.Equal(9, Loadout.PouchSlots);
		Assert.True(Loadout.DefaultMagazines + Loadout.DefaultGrenades <= Loadout.PouchSlots);
		Assert.Equal(Default, Sanitize(Default));
	}

	[Fact]
	public void TheDefaultKitWeighsNinePointThreeAndMovesAtExactlyOne()
	{
		Assert.Equal(9.3f, Reference, 3);
		Assert.Equal(1f, Loadout.SpeedScale(Reference, Reference));
	}

	[Theory]
	// §4.1's kit table: weight, scale and walk / sprint, to 0.005.
	[InlineData(Loadout.NoPrimary, 0, 0, BodyArmourTier.None, 2.0f, 1.12f, 5.6f, 9.0f)]
	[InlineData(Rifle, 0, 0, BodyArmourTier.None, 5.6f, 1.07f, 5.4f, 8.6f)]
	[InlineData(Rifle, 5, 3, BodyArmourTier.None, 9.3f, 1.00f, 5.0f, 8.0f)]
	[InlineData(Rifle, 9, 0, BodyArmourTier.Light, 13.1f, 0.92f, 4.6f, 7.4f)]
	[InlineData(Rifle, 5, 3, BodyArmourTier.Medium, 15.3f, 0.88f, 4.4f, 7.0f)]
	[InlineData(Dmr, 5, 3, BodyArmourTier.Medium, 16.6f, 0.85f, 4.3f, 6.8f)]
	[InlineData(Rifle, 5, 3, BodyArmourTier.Heavy, 19.3f, 0.80f, 4.0f, 6.4f)]
	[InlineData(Launcher, 9, 0, BodyArmourTier.Heavy, 26.1f, 0.70f, 3.5f, 5.6f)]
	public void TheKitTableHolds(byte large, int magazines, int grenades, BodyArmourTier armour,
		float kg, float scale, float walk, float sprint)
	{
		LoadoutSelection kit = Kit(large, magazines, grenades, armour);
		Assert.Equal(kit, Sanitize(kit));

		float weight = Loadout.WeightKg(kit, Carry);
		float speed = Loadout.SpeedScale(weight, Reference);
		Assert.InRange(weight, kg - 0.005f, kg + 0.005f);
		Assert.InRange(speed, scale - 0.005f, scale + 0.005f);

		// The authored walk and sprint (Scenes/fps_controller.tscn), rounded as the table is.
		Assert.Equal(walk, MathF.Round(5f * speed, 1), 3);
		Assert.Equal(sprint, MathF.Round(8f * speed, 1), 3);
	}

	[Fact]
	public void TheScaleStopsAtTheCapAndTheFloor()
	{
		Assert.Equal(Loadout.MaxSpeedScale, Loadout.SpeedScale(0f, Reference));
		Assert.Equal(Loadout.MinSpeedScale, Loadout.SpeedScale(100f, Reference));
		Assert.Equal(1.12f, Loadout.MaxSpeedScale);
		Assert.Equal(0.70f, Loadout.MinSpeedScale);
	}

	[Fact]
	public void EveryComponentWeighsSomethingAndSlowsTheCarrier()
	{
		LoadoutSelection light = Kit(Rifle, 2, 1);
		float before = Loadout.WeightKg(light, Carry);

		foreach (LoadoutSelection heavier in new[]
		{
			Kit(Rifle, 3, 1), Kit(Rifle, 2, 2), Kit(Rifle, 2, 1, BodyArmourTier.Light),
			Kit(Dmr, 2, 1), Kit(Launcher, 2, 1),
		})
		{
			float weight = Loadout.WeightKg(heavier, Carry);
			Assert.True(weight > before, $"{heavier.Large}/{heavier.Magazines}/{heavier.Grenades}/{heavier.Armour}");
			Assert.True(Loadout.SpeedScale(weight, Reference) < Loadout.SpeedScale(before, Reference));
		}

		Assert.True(Loadout.VestWeightKg(BodyArmourTier.Light) < Loadout.VestWeightKg(BodyArmourTier.Medium));
		Assert.True(Loadout.VestWeightKg(BodyArmourTier.Medium) < Loadout.VestWeightKg(BodyArmourTier.Heavy));
	}

	[Fact]
	public void AReserveIsAPouchsRoundsForEveryMagazinePouch()
	{
		Assert.Equal(150, Loadout.ReserveFor(Default, Carry[Rifle]));
		Assert.Equal(50, Loadout.ReserveFor(Kit(Dmr, 5, 3), Carry[Dmr]));
		Assert.Equal(10, Loadout.ReserveFor(Kit(Launcher, 5, 3), Carry[Launcher]));
		Assert.Equal(0, Loadout.ReserveFor(Kit(Rifle, 0, 3), Carry[Rifle]));
		Assert.Equal(270, Loadout.ReserveFor(Kit(Rifle, 9, 0), Carry[Rifle]));
		Assert.Equal(0, Loadout.ReserveFor(Kit(Loadout.NoPrimary, 0, 3), default));
	}

	[Fact]
	public void SanitizeMakesAnUnknownPrimaryTheRifleAndKeepsNoPrimary()
	{
		Assert.Equal(Rifle, Sanitize(Kit(200, 2, 1)).Large);
		Assert.Equal(Rifle, Sanitize(Kit(Pistol, 2, 1)).Large);
		Assert.Equal(Loadout.NoPrimary, Sanitize(Kit(Loadout.NoPrimary, 0, 1)).Large);
		Assert.Equal(Launcher, Sanitize(Kit(Launcher, 2, 1)).Large);
	}

	[Fact]
	public void SanitizeGivesNoPrimaryNoMagazines()
	{
		LoadoutSelection kit = Sanitize(Kit(Loadout.NoPrimary, 6, 2));
		Assert.Equal(0, kit.Magazines);
		Assert.Equal(2, kit.Grenades);
	}

	[Fact]
	public void SanitizeKeepsGrenadesFirstWhenThePouchesOverflow()
	{
		LoadoutSelection kit = Sanitize(Kit(Rifle, 9, 3));
		Assert.Equal(3, kit.Grenades);
		Assert.Equal(6, kit.Magazines);
		Assert.Equal(3, Sanitize(Kit(Rifle, 0, 200)).Grenades);
	}

	[Fact]
	public void SanitizeMakesAnUnknownVestNone()
	{
		Assert.Equal(BodyArmourTier.None, Sanitize(Kit(Rifle, 1, 1, (BodyArmourTier)7)).Armour);
		Assert.Equal(BodyArmourTier.Heavy, Sanitize(Kit(Rifle, 1, 1, BodyArmourTier.Heavy)).Armour);
	}

	[Fact]
	public void SanitizeSurvivesEveryByte()
	{
		// Kits arrive from clients: whatever the bytes, the result is one this build
		// can spawn, and sanitizing it again changes nothing.
		var random = new Random(4);
		for (int i = 0; i < 2000; i++)
		{
			LoadoutSelection kit = Sanitize(new LoadoutSelection
			{
				Melee = Hammer,
				Sidearm = Pistol,
				Large = (byte)random.Next(256),
				Magazines = (byte)random.Next(256),
				Grenades = (byte)random.Next(256),
				Armour = (BodyArmourTier)random.Next(256),
			});

			Assert.True(kit.Large == Loadout.NoPrimary || Array.IndexOf(LargeWeapons, kit.Large) >= 0);
			Assert.True(kit.Magazines + kit.Grenades <= Loadout.PouchSlots);
			Assert.True(kit.Grenades <= SimConfig.MaxGrenades);
			Assert.True(kit.Armour <= BodyArmourTier.Heavy);
			Assert.True(kit.HasPrimary || kit.Magazines == 0);
			Assert.Equal(kit, Sanitize(kit));
		}
	}

	[Fact]
	public void AKitIsParsedFromItsNames()
	{
		Assert.True(Loadout.TryParse("launcher", 3, 2, "medium", LargeWeapons, out LoadoutSelection kit, out _));
		Assert.Equal(Launcher, kit.Large);
		Assert.Equal(3, kit.Magazines);
		Assert.Equal(2, kit.Grenades);
		Assert.Equal(BodyArmourTier.Medium, kit.Armour);

		Assert.True(Loadout.TryParse("none", 0, 0, null, LargeWeapons, out kit, out _));
		Assert.Equal(Loadout.NoPrimary, kit.Large);
		Assert.Equal(BodyArmourTier.None, kit.Armour);

		Assert.True(Loadout.TryParse("DMR", 9, 0, "heavy", LargeWeapons, out kit, out _));
		Assert.Equal(Dmr, kit.Large);
	}

	[Theory]
	[InlineData("shotgun", 1, 1, "none", "primary")]
	[InlineData("rifle", 1, 1, "kevlar", "armour")]
	[InlineData("rifle", 1, 4, "none", "grenades")]
	[InlineData("rifle", -1, 1, "none", "pouches")]
	[InlineData("rifle", 7, 3, "none", "pouches")]
	[InlineData("none", 1, 0, "none", "no primary")]
	public void AKitThatBreaksARuleIsRefusedSayingWhich(string primary, int magazines, int grenades, string armour,
		string says)
	{
		Assert.False(Loadout.TryParse(primary, magazines, grenades, armour, LargeWeapons, out _, out string error));
		Assert.Contains(says, error);
	}

	[Fact]
	public void TheNamesFollowTheLargeWeaponsOrder()
	{
		// "none", then WeaponCatalog.LargeWeapons' protocol order (DMR, rifle, launcher).
		for (int i = 0; i < LargeWeapons.Length; i++)
		{
			Assert.True(Loadout.TryParse(Loadout.PrimaryNames[i + 1], 0, 0, "none", LargeWeapons,
				out LoadoutSelection kit, out _));
			Assert.Equal(LargeWeapons[i], kit.Large);
		}

		Assert.Equal(new[] { "none", "light", "medium", "heavy" }, Loadout.ArmourNames);
	}
}
