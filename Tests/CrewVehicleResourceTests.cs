using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Gdpyr.Sim;
using Godot;
using Xunit;

namespace Gdpyr.Tests;

/// <summary>
/// The crew vehicles, their gun, the ram and the tank's round as the resource files and
/// the catalogs write them (docs/GROUND_VEHICLES.md §5.8, GV-D2, GV-D9, GV-D10, GV-D17),
/// read as text as <see cref="TrainedUnitResourceTests"/> reads the units.
/// </summary>
public class CrewVehicleResourceTests
{
	private static Dictionary<string, string> FourByFour => CrewVehicleFiles.Resource(CrewVehicleFiles.FourByFourPath);

	private static Dictionary<string, string> Helicopter => CrewVehicleFiles.Resource(CrewVehicleFiles.HelicopterPath);

	private static float F(Dictionary<string, string> file, string key) => TrainedUnitResourceTests.Float(file, key);

	private static int I(Dictionary<string, string> file, string key) => TrainedUnitResourceTests.Int(file, key);

	// ---- ids ------------------------------------------------------------------------------

	[Fact]
	public void Ids0And1_AreTheTwoKinds_AndEachFileSaysWhichItIs()
	{
		List<string> paths = CatalogPaths("Scripts/Core/CrewVehicleCatalog.cs");
		Assert.Equal(new[] { "res://Vehicles/four_by_four.tres", "res://Vehicles/helicopter.tres" }, paths);

		Assert.Equal(0, (int)CrewVehicleKind.FourByFour);
		Assert.Equal(1, (int)CrewVehicleKind.Helicopter);
		Assert.Equal((int)CrewVehicleKind.FourByFour, I(FourByFour, "Kind"));
		Assert.Equal((int)CrewVehicleKind.Helicopter, I(Helicopter, "Kind"));
		Assert.Equal("&\"4x4\"", FourByFour["Name"]);
		Assert.Equal("&\"helicopter\"", Helicopter["Name"]);
	}

	[Fact]
	public void WeaponIds0To13_AreWhereTheyWere_14IsTheGun_And15TheRam()
	{
		string[] expected =
		{
			"res://Weapons/geopick/geopick.tres", "res://Weapons/pistol/pistol.tres", "res://Weapons/dmr/dmr.tres",
			"res://Weapons/rifle/rifle.tres", "res://Weapons/launcher/launcher.tres", "res://Weapons/hmg/hmg.tres",
			"res://Weapons/cannon/cannon.tres", "res://Weapons/heavy/heavy.tres", "res://Weapons/turret/turret.tres",
			"res://Weapons/mortar/mortar.tres", "res://Weapons/marksman/marksman.tres", "res://Weapons/lmg/lmg.tres",
			"res://Weapons/grenade/grenade.tres", "res://Weapons/drone/drone.tres",
			"res://Weapons/vehicle_gun/vehicle_gun.tres", "res://Weapons/ram/ram.tres",
		};

		for (int id = 0; id < expected.Length; id++)
		{
			Assert.Equal(expected[id], TrainedUnitResourceTests.WeaponPath(id));
		}

		Assert.Equal(expected.Length, CatalogPaths("Scripts/Core/WeaponCatalog.cs").Count);
		Assert.True(expected.Length <= SimConfig.MaxWeaponDefinitions);

		string catalog = File.ReadAllText(TrainedUnitResourceTests.Repo("Scripts/Core/WeaponCatalog.cs"));
		Assert.Contains("public const byte VehicleGun = 14;", catalog);
		Assert.Contains("public const byte Ram = 15;", catalog);
	}

	[Fact]
	public void The4x4sGun_IsWeapon14_AndTheHelicopterHasNone()
	{
		Assert.Equal("res://Weapons/vehicle_gun/vehicle_gun.tres", TrainedUnitResourceTests.WeaponPath(I(FourByFour, "GunWeaponId")));
		Assert.Equal(-1, I(Helicopter, "GunWeaponId"));
	}

	// ---- the gun and the ram --------------------------------------------------------------

	[Fact]
	public void TheGun_FiresThe12Point7_At450AMinute_FromAHundredRoundBox_ReloadingIn5Seconds()
	{
		Dictionary<string, string> gun = TrainedUnitResourceTests.Resource("Weapons/vehicle_gun/vehicle_gun.tres");
		Assert.Contains("res://Weapons/projectiles/hmg_127.tres",
			File.ReadAllText(TrainedUnitResourceTests.Repo("Weapons/vehicle_gun/vehicle_gun.tres")));
		Assert.Equal(450f, F(gun, "RoundsPerMinute"));
		Assert.Equal(100, I(gun, "MagazineSize"));
		Assert.Equal(5f, F(gun, "ReloadSeconds"));
		Assert.Equal((int)FireMode.Auto, I(gun, "FireMode"));
		Assert.Equal(45f, F(TrainedUnitResourceTests.Resource("Weapons/projectiles/hmg_127.tres"), "Damage"));
	}

	[Fact]
	public void TheGun_TurnsAllTheWayRound_AndElevatesFromMinus15ToPlus50()
	{
		Assert.Equal(180f, F(FourByFour, "GunTraverseDegrees"));
		Assert.Equal(new[] { -15f, 50f }, CrewVehicleFiles.Tuple(FourByFour, "GunElevationDegrees", "Vector2"));
	}

	[Fact]
	public void TheRam_HasNoRound_AndIsNoSwing()
	{
		string text = File.ReadAllText(TrainedUnitResourceTests.Repo("Weapons/ram/ram.tres"));
		Dictionary<string, string> ram = TrainedUnitResourceTests.Resource("Weapons/ram/ram.tres");

		Assert.DoesNotContain("Projectile", text);
		Assert.Equal("false", ram["IsMelee"]);
		Assert.Equal(0, I(ram, "MagazineSize"));
	}

	// ---- armour and the tank's round ------------------------------------------------------

	[Fact]
	public void TheArmour_IsGVD9s()
	{
		Assert.Equal(600f, F(FourByFour, "MaxHealth"));
		Assert.Equal(0.15f, F(FourByFour, "BulletDamageScale"));
		Assert.Equal(450f, F(Helicopter, "MaxHealth"));
		Assert.Equal(0.2f, F(Helicopter, "BulletDamageScale"));
	}

	[Theory]
	[InlineData(CrewVehicleFiles.FourByFourPath, 150)]
	[InlineData(CrewVehicleFiles.HelicopterPath, 100)]
	public void SmallArms_TakeAtLeastThisManyHitsToDestroyOne(string path, int atLeast)
	{
		Dictionary<string, string> vehicle = CrewVehicleFiles.Resource(path);
		float rifle = F(TrainedUnitResourceTests.Resource("Weapons/projectiles/rifle_556.tres"), "Damage");
		float perHit = Armour.DamageTaken(rifle, explosive: false, F(vehicle, "BulletDamageScale"), 1f);

		int hits = (int)MathF.Ceiling(F(vehicle, "MaxHealth") / perHit);
		Assert.True(hits >= atLeast, $"{path}: {hits} rifle hits");
	}

	[Fact]
	public void TheTanksRound_DestroysEitherOnADirectHit_AndItsBlastAtTheFaceDoesNot()
	{
		Dictionary<string, string> round = TrainedUnitResourceTests.Resource("Weapons/projectiles/cannon_76.tres");
		float impact = F(round, "VehicleImpactDamage");
		float blast = F(round, "ExplosionDamage");

		foreach (Dictionary<string, string> vehicle in new[] { FourByFour, Helicopter })
		{
			// A direct hit: explosive (the round has a blast), at its full share.
			Assert.True(Armour.DamageTaken(impact, true, F(vehicle, "BulletDamageScale"), 1f) >= F(vehicle, "MaxHealth"));

			// A near miss: the blast at the face, distance zero, is the blast's most.
			Assert.True(Armour.DamageTaken(blast, true, F(vehicle, "BulletDamageScale"), 1f) < F(vehicle, "MaxHealth"));
		}

		// And on everything else it strikes the round is what it was.
		Assert.Equal(90f, F(round, "Damage"));
		Assert.Equal(1000f, impact);
	}

	[Fact]
	public void NoOtherRound_HasAVehicleImpact()
	{
		foreach (string path in Directory.GetFiles(TrainedUnitResourceTests.Repo("Weapons/projectiles/cannon_76.tres")[..^"cannon_76.tres".Length], "*.tres"))
		{
			if (Path.GetFileName(path) != "cannon_76.tres")
			{
				Assert.DoesNotContain("VehicleImpactDamage", File.ReadAllText(path));
			}
		}
	}

	[Fact]
	public void ARoundsVehicleImpact_ReplacesItsDamage_OnlyAgainstACrewVehicle_AndOnlyWhenSet()
	{
		var physics = ProjectileParams.FromGramsAndMillimeters(6000f, 38f, 0.28f);
		var tank = new ProjectileStats(physics, 320f, 90f, 4.5f, 80f, vehicleImpactDamage: 1000f);
		var rifle = new ProjectileStats(physics, 400f, 20f);

		Assert.Equal(1000f, tank.DirectDamageAgainstCrewVehicle);
		Assert.Equal(90f, tank.Damage);
		Assert.Equal(20f, rifle.DirectDamageAgainstCrewVehicle);
		Assert.Equal(0f, new ProjectileStats(physics, 1f, 1f, vehicleImpactDamage: -5f).VehicleImpactDamage);
		Assert.Equal(0f, new ProjectileStats(physics, 1f, 1f, vehicleImpactDamage: float.NaN).VehicleImpactDamage);
	}

	// ---- the kiosk, ramming -----------------------------------------------------------------

	[Fact]
	public void TheKiosksTimers_AreGVD13s_AndBothKindsFitOnTheWire()
	{
		Assert.Equal(45f, F(FourByFour, "RespawnSeconds"));
		Assert.Equal(90f, F(Helicopter, "RespawnSeconds"));
		Assert.Equal(1, I(FourByFour, "Cap"));
		Assert.Equal(1, I(Helicopter, "Cap"));
		Assert.True(I(FourByFour, "Cap") + I(Helicopter, "Cap") <= SimConfig.MaxCrewVehicles);
		Assert.Equal(4, SimConfig.MaxCrewVehicles);
	}

	[Fact]
	public void TheSpeedThatRunsAUnitOver_IsAboveEveryUnitOnFoot_AndAboveThe4x4sReverse()
	{
		foreach (Dictionary<string, string> vehicle in new[] { FourByFour, Helicopter })
		{
			float kill = F(vehicle, "RamKillSpeedMetersPerSecond");
			Assert.Equal(8f, kill);

			foreach (string unit in Directory.GetFiles(TrainedUnitResourceTests.Repo("Units/infantry.tres")[..^"infantry.tres".Length], "*.tres"))
			{
				Dictionary<string, string> file = TrainedUnitResourceTests.Resource(Path.Combine("Units", Path.GetFileName(unit)));
				if (!file.ContainsKey("IsVehicle"))
				{
					Assert.True(kill > F(file, "MoveSpeed"), $"{unit} walks at {F(file, "MoveSpeed")} m/s");
				}
			}

			Assert.True(kill > F(FourByFour, "ReverseSpeed"));
		}
	}

	// ---- seats and hulls --------------------------------------------------------------------

	[Fact]
	public void TheSeats_AreGVD2s()
	{
		CrewSeat[] truck = CrewVehicleFiles.Seats(CrewVehicleFiles.FourByFourPath);
		Assert.Equal(new[] { SeatRole.Driver, SeatRole.Gunner, SeatRole.Passenger }, Array.ConvertAll(truck, s => s.Role));
		Assert.Equal(new[] { true, false, false }, Array.ConvertAll(truck, s => s.Enclosed));

		CrewSeat[] helicopter = CrewVehicleFiles.Seats(CrewVehicleFiles.HelicopterPath);
		Assert.Equal(new[] { SeatRole.Driver, SeatRole.Passenger }, Array.ConvertAll(helicopter, s => s.Role));
		Assert.Equal(new[] { true, false }, Array.ConvertAll(helicopter, s => s.Enclosed));

		// The helicopter's passenger is in the left door: their capsule is on the −X side.
		Assert.True(helicopter[1].Capsule.Center.X < -0.9f);
	}

	[Theory]
	[InlineData(CrewVehicleFiles.FourByFourPath, 3, 4)]
	[InlineData(CrewVehicleFiles.HelicopterPath, 2, 2)]
	public void EverySeatArray_HoldsOneEntryASeat_AndEveryHullArrayOneABox(string path, int seats, int boxes)
	{
		Dictionary<string, string> file = CrewVehicleFiles.Resource(path);
		Assert.Equal(seats, CrewVehicleFiles.Ints(file, "SeatRoles").Length);
		Assert.Equal(seats, CrewVehicleFiles.Ints(file, "SeatEnclosed").Length);
		Assert.Equal(seats, CrewVehicleFiles.Vectors(file, "SeatCapsuleBottoms").Length);
		Assert.Equal(seats, CrewVehicleFiles.Vectors(file, "SeatCapsuleTops").Length);
		Assert.Equal(seats, CrewVehicleFiles.Floats(file, "SeatCapsuleRadii").Length);
		Assert.Equal(seats, CrewVehicleFiles.Vectors(file, "SeatEyes").Length);
		Assert.Equal(seats, CrewVehicleFiles.Vectors(file, "SeatDoors").Length);
		Assert.Equal(seats, CrewVehicleFiles.Vectors(file, "SeatExits").Length);
		Assert.Equal(seats, CrewVehicleFiles.Vectors2(file, "SeatArcYawDegrees").Length);
		Assert.Equal(seats, CrewVehicleFiles.Vectors2(file, "SeatArcPitchDegrees").Length);
		Assert.Equal(boxes, CrewVehicleFiles.Vectors(file, "HullCentres").Length);
		Assert.Equal(boxes, CrewVehicleFiles.Vectors(file, "HullHalfExtents").Length);
		Assert.InRange(seats, 1, CrewLayout.MaxSeats);
		Assert.InRange(boxes, 1, CrewLayout.MaxHullBoxes);

		foreach (HullBox box in CrewVehicleFiles.Hull(path))
		{
			Assert.True(box.Local.IsValid, $"{path}: a hull box with no size");
		}
	}

	[Fact]
	public void The4x4sCollider_ClearsTheGround_WithEverySpringSolid()
	{
		FourByFourTraits traits = CrewVehicleFiles.FourByFourTraits();
		float[] centre = CrewVehicleFiles.Tuple(FourByFour, "ColliderCentre", "Vector3");
		float[] half = CrewVehicleFiles.Tuple(FourByFour, "ColliderHalfExtents", "Vector3");
		float solid = traits.SpringRestMeters - traits.SpringTravelMeters + traits.WheelRadiusMeters;

		Assert.True(centre[1] - half[1] > -solid, "the collider's floor is below the ground before the springs bottom out");

		Vector3[] points = CrewLayout.BevelledBox(new Vector3(centre[0], centre[1], centre[2]),
			new Vector3(half[0], half[1], half[2]), F(FourByFour, "ColliderBevelMeters"));
		Assert.Equal(24, points.Length);
		foreach (Vector3 p in points)
		{
			Assert.True(MathF.Abs(p.X - centre[0]) <= half[0] + 1e-5f && MathF.Abs(p.Y - centre[1]) <= half[1] + 1e-5f
				&& MathF.Abs(p.Z - centre[2]) <= half[2] + 1e-5f);
		}
	}

	// ---- the files and the definition agree -------------------------------------------------

	[Fact]
	public void TheFilesDriveAndFlightTraits_AreTheOnesTheRulesAreTestedWith()
	{
		Assert.Equal(FourByFourTraits.Default, CrewVehicleFiles.FourByFourTraits(), new TraitComparer<FourByFourTraits>());
		Assert.Equal(HelicopterTraits.Default, CrewVehicleFiles.HelicopterTraits(), new TraitComparer<HelicopterTraits>());
	}

	[Theory]
	[InlineData(CrewVehicleFiles.FourByFourPath)]
	[InlineData(CrewVehicleFiles.HelicopterPath)]
	public void EveryKeyInTheFile_IsAFieldOfTheDefinition(string path)
	{
		// The engine ignores a key it has no field for, which would leave a dial at its
		// default with nothing said.
		string source = File.ReadAllText(TrainedUnitResourceTests.Repo("Scripts/Fps/CrewVehicleDefinition.cs"));
		foreach (string key in CrewVehicleFiles.Resource(path).Keys)
		{
			if (key == "script")
			{
				continue;
			}

			Assert.Matches(new Regex($@"\[Export\]\s+public [\w\[\]]+ {key}\b"), source);
		}
	}

	private static List<string> CatalogPaths(string source)
	{
		string text = File.ReadAllText(TrainedUnitResourceTests.Repo(source));
		int start = text.IndexOf("Paths =", StringComparison.Ordinal);
		int end = text.IndexOf("};", start, StringComparison.Ordinal);
		var paths = new List<string>();
		foreach (System.Text.RegularExpressions.Match match in Regex.Matches(text[start..end], "\"(res://[^\"]+)\""))
		{
			paths.Add(match.Groups[1].Value);
		}

		return paths;
	}

	/// <summary>Two trait structs equal field by field to a part in a million: a file's 0.333333 is a third.</summary>
	private sealed class TraitComparer<T> : IEqualityComparer<T>
		where T : struct
	{
		public bool Equals(T a, T b)
		{
			foreach (System.Reflection.FieldInfo field in typeof(T).GetFields())
			{
				if (field.IsStatic || field.FieldType != typeof(float))
				{
					continue;
				}

				float x = (float)field.GetValue(a)!;
				float y = (float)field.GetValue(b)!;
				if (MathF.Abs(x - y) > 1e-5f * MathF.Max(1f, MathF.Abs(x)))
				{
					return false;
				}
			}

			return true;
		}

		public int GetHashCode(T value) => 0;
	}
}
