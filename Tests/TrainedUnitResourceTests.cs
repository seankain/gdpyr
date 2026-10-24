using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Gdpyr.Tests;

/// <summary>
/// The sniper and the heavy gunner as the resource files and the catalogs write them
/// (docs/NETCODE.md §10.8). The ids in the files — a structure's <c>TrainsUnitId</c>,
/// a unit's <c>WeaponId</c> — are positions in the catalogs' path tables, and the
/// engine is the only thing that loads both; this reads the text of each, so a
/// reordered table or a mistyped id fails here rather than as a pillbox that trains
/// tanks.
/// </summary>
public class TrainedUnitResourceTests
{
	[Fact]
	public void TheTowerTrainsSnipers_AndThePillboxHeavyGunners()
	{
		Assert.Equal("res://Units/sniper.tres", UnitPath(Int(Resource("Structures/sniper_tower.tres"), "TrainsUnitId")));
		Assert.Equal("res://Units/heavy_gunner.tres", UnitPath(Int(Resource("Structures/pillbox.tres"), "TrainsUnitId")));
		Assert.False(Resource("Structures/sandbag_wall.tres").ContainsKey("TrainsUnitId"));
	}

	[Fact]
	public void ASniperCostsSlightlyMoreThanARifleman()
	{
		int rifleman = Int(Resource("Units/infantry.tres"), "Cost");
		int sniper = Int(Resource("Units/sniper.tres"), "Cost");

		Assert.InRange(sniper, rifleman + 1, rifleman * 5 / 4);
	}

	[Fact]
	public void NeitherComesOutOfABarracks_EverythingElseStillDoes()
	{
		Assert.Equal("false", Resource("Units/sniper.tres")["TrainedInBarracks"]);
		Assert.Equal("false", Resource("Units/heavy_gunner.tres")["TrainedInBarracks"]);

		foreach (string unit in new[] { "infantry", "technical", "tank", "builder", "supply_truck", "dropship" })
		{
			Assert.False(Resource($"Units/{unit}.tres").ContainsKey("TrainedInBarracks"));
		}
	}

	[Fact]
	public void TheSniperShootsTheTowersGun()
	{
		Assert.Equal("res://Weapons/marksman/marksman.tres", WeaponPath(Int(Resource("Units/sniper.tres"), "WeaponId")));
		Assert.Equal(Int(Resource("Structures/sniper_tower.tres"), "WeaponId"),
			Int(Resource("Units/sniper.tres"), "WeaponId"));
	}

	[Fact]
	public void TheHeavyGunner_HasALongBelt_AndSuppresses()
	{
		Dictionary<string, string> gunner = Resource("Units/heavy_gunner.tres");
		Assert.Equal("res://Weapons/lmg/lmg.tres", WeaponPath(Int(gunner, "WeaponId")));
		Assert.True(Float(gunner, "SuppressSeconds") > 0f);

		Dictionary<string, string> lmg = Resource("Weapons/lmg/lmg.tres");
		Dictionary<string, string> rifle = Resource("Weapons/rifle/rifle.tres");
		Assert.True(Int(lmg, "MagazineSize") >= 4 * Int(rifle, "MagazineSize"));

		// Sixteen seconds of fire before a reload, against a rifle's three.
		Assert.True(Int(lmg, "MagazineSize") / Float(lmg, "RoundsPerMinute") * 60f >= 15f);
	}

	[Theory]
	[InlineData("infantry", Gdpyr.Sim.UnitDoctrine.Rifleman)]
	[InlineData("heavy_gunner", Gdpyr.Sim.UnitDoctrine.Gunner)]
	[InlineData("sniper", Gdpyr.Sim.UnitDoctrine.Marksman)]
	[InlineData("builder", Gdpyr.Sim.UnitDoctrine.None)]
	[InlineData("technical", Gdpyr.Sim.UnitDoctrine.None)]
	[InlineData("tank", Gdpyr.Sim.UnitDoctrine.None)]
	[InlineData("supply_truck", Gdpyr.Sim.UnitDoctrine.None)]
	[InlineData("dropship", Gdpyr.Sim.UnitDoctrine.None)]
	public void EveryUnit_SaysHowItFights(string unit, Gdpyr.Sim.UnitDoctrine doctrine)
	{
		// docs/TACTICS.md §3: named in every file, not left to the export's default.
		Assert.Equal((int)doctrine, Int(Resource($"Units/{unit}.tres"), "Doctrine"));
	}

	[Fact]
	public void EveryUnitInTheCatalog_NamesItsDoctrine()
	{
		// A unit added to the catalog without one would play as a rifleman by default.
		foreach (string path in CatalogPaths("Scripts/Core/UnitCatalog.cs"))
		{
			Assert.True(Resource(path["res://".Length..]).ContainsKey("Doctrine"), $"{path} names no Doctrine");
		}
	}

	[Fact]
	public void NoOtherUnitSuppresses()
	{
		foreach (string unit in new[] { "infantry", "technical", "tank", "builder", "supply_truck", "sniper", "dropship" })
		{
			Assert.False(Resource($"Units/{unit}.tres").ContainsKey("SuppressSeconds"));
		}
	}

	[Fact]
	public void TheTechnicalSeatsFour_TheDropshipEight_AndNothingElseCarriesAnybody()
	{
		// docs/VEHICLES.md V-D1, V-D2: vehicles with seats, inside the bound every hold is made at.
		foreach ((string unit, int seats) in new[] { ("technical", 4), ("dropship", 8) })
		{
			Dictionary<string, string> vehicle = Resource($"Units/{unit}.tres");
			Assert.Equal(seats, Int(vehicle, "Seats"));
			Assert.InRange(Int(vehicle, "Seats"), 1, Gdpyr.Sim.SimConfig.MaxPassengers);
			Assert.Equal("true", vehicle["IsVehicle"]);
		}

		foreach (string unit in new[] { "infantry", "tank", "builder", "supply_truck", "sniper", "heavy_gunner" })
		{
			Assert.False(Resource($"Units/{unit}.tres").ContainsKey("Seats"), unit);
		}
	}

	[Fact]
	public void Id7_IsTheDropship_AndEveryEarlierIdIsWhereItWas()
	{
		// docs/VEHICLES.md §8.1: appended, so demos, scenarios and trained policies keep their ids.
		string[] expected =
		{
			"res://Units/infantry.tres", "res://Units/technical.tres", "res://Units/tank.tres",
			"res://Units/builder.tres", "res://Units/supply_truck.tres", "res://Units/sniper.tres",
			"res://Units/heavy_gunner.tres", "res://Units/dropship.tres",
		};

		for (int id = 0; id < expected.Length; id++)
		{
			Assert.Equal(expected[id], UnitPath(id));
		}

		Assert.Matches(@"public const byte Dropship = 7;", File.ReadAllText(Repo("Scripts/Core/UnitCatalog.cs")));
		Assert.True(expected.Length <= Gdpyr.Sim.SimConfig.MaxUnitDefinitions);
	}

	private static readonly string[] FlightKeys =
	{
		"CruiseAltitudeMeters", "ClimbMetersPerSecond", "DescentMetersPerSecond", "ClearAltitudeMeters",
	};

	[Fact]
	public void OnlyTheDropship_HasFlightKeys()
	{
		Dictionary<string, string> dropship = Resource("Units/dropship.tres");
		foreach (string key in FlightKeys)
		{
			Assert.True(Float(dropship, key) > 0f, key);
		}

		foreach (string unit in new[] { "infantry", "technical", "tank", "builder", "supply_truck", "sniper", "heavy_gunner" })
		{
			Dictionary<string, string> file = Resource($"Units/{unit}.tres");
			foreach (string key in FlightKeys)
			{
				Assert.False(file.ContainsKey(key), $"{unit}.{key}");
			}
		}
	}

	[Fact]
	public void TheDropship_IsAnUnarmedTransport_WithTheNumbersThePlanWrites()
	{
		// docs/VEHICLES.md V-D8, §8.1: every number a playtest knob, these the defaults.
		Dictionary<string, string> dropship = Resource("Units/dropship.tres");
		Assert.Equal(250, Int(dropship, "Cost"));
		Assert.Equal(14f, Float(dropship, "BuildSeconds"));
		Assert.Equal(400f, Float(dropship, "MaxHealth"));
		Assert.Equal(0.5f, Float(dropship, "BulletDamageScale"));
		Assert.Equal(12f, Float(dropship, "MoveSpeed"));
		Assert.Equal(14f, Float(dropship, "CruiseAltitudeMeters"));
		Assert.Equal(40f, Float(dropship, "SensorRadiusMeters"));

		// Unarmed: it shoots nothing, so it never picks a target (UnitManager.SimulateUnit).
		Assert.Equal(0f, Float(dropship, "EngageRangeMeters"));

		// Below clear altitude it only climbs or comes down, and clear is above every roof
		// on the map: the barracks' 6.5 m and the sniper tower's 8.4 m cabin.
		float clear = Float(dropship, "ClearAltitudeMeters");
		Assert.InRange(clear, 8.4f, Float(dropship, "CruiseAltitudeMeters"));

		// Its hitbox stays a capsule: height at least twice the radius.
		Assert.True(Float(dropship, "HeightMeters") >= 2f * Float(dropship, "RadiusMeters"));
	}

	// ---- reading the files -------------------------------------------------------------

	private static readonly Regex PathLine = new("\"(res://[^\"]+)\"", RegexOptions.Compiled);

	internal static string UnitPath(int id) => CatalogPaths("Scripts/Core/UnitCatalog.cs")[id];

	internal static string WeaponPath(int id) => CatalogPaths("Scripts/Core/WeaponCatalog.cs")[id];

	/// <summary>The <c>res://</c> paths in a catalog's table, in order: index is the wire id.</summary>
	private static List<string> CatalogPaths(string source)
	{
		string text = File.ReadAllText(Repo(source));
		int start = text.IndexOf("Paths =", StringComparison.Ordinal);
		int end = text.IndexOf("};", start, StringComparison.Ordinal);
		var paths = new List<string>();
		foreach (System.Text.RegularExpressions.Match match in PathLine.Matches(text.Substring(start, end - start)))
		{
			paths.Add(match.Groups[1].Value);
		}
		return paths;
	}

	/// <summary>The <c>key = value</c> lines of a resource's <c>[resource]</c> section.</summary>
	internal static Dictionary<string, string> Resource(string path)
	{
		var values = new Dictionary<string, string>();
		bool inResource = false;
		foreach (string line in File.ReadAllLines(Repo(path)))
		{
			if (line.StartsWith('['))
			{
				inResource = line == "[resource]";
				continue;
			}

			int equals = line.IndexOf(" = ", StringComparison.Ordinal);
			if (inResource && equals > 0)
			{
				values[line[..equals]] = line[(equals + 3)..];
			}
		}
		return values;
	}

	internal static int Int(Dictionary<string, string> values, string key) =>
		int.Parse(values[key], CultureInfo.InvariantCulture);

	internal static float Float(Dictionary<string, string> values, string key) =>
		float.Parse(values[key], CultureInfo.InvariantCulture);

	private static string Repo(string relative)
	{
		for (var at = new DirectoryInfo(AppContext.BaseDirectory); at != null; at = at.Parent)
		{
			string candidate = Path.Combine(at.FullName, relative);
			if (File.Exists(candidate) && File.Exists(Path.Combine(at.FullName, "project.godot")))
			{
				return candidate;
			}
		}

		throw new InvalidOperationException($"no {relative} above {AppContext.BaseDirectory}");
	}
}
