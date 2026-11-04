using System;
using System.Collections.Generic;
using Gdpyr.Sim;
using Godot;
using Xunit;

namespace Gdpyr.Tests;

/// <summary>
/// What rounds and blasts do to a crew vehicle and its crew, and what its wreck does to
/// them (docs/GROUND_VEHICLES.md §6.5, GV-D9 to GV-D12), checked against the vehicle
/// files that ship.
/// </summary>
public class CrewCombatTests
{
	private static HitObb[] Placed(HullBox[] hull, in CrewPose pose)
	{
		var boxes = new HitObb[hull.Length];
		for (int i = 0; i < hull.Length; i++)
		{
			boxes[i] = hull[i].Place(pose);
		}

		return boxes;
	}

	private static float Next(Random random, float range) => (((float)random.NextDouble() * 2f) - 1f) * range;

	private static Vector3 RandomUnit(Random random)
	{
		Vector3 v;
		do
		{
			v = new Vector3(Next(random, 1f), Next(random, 1f), Next(random, 1f));
		}
		while (v.LengthSquared() < 1e-3f || v.LengthSquared() > 1f);

		return v.Normalized();
	}

	// ---- a round against the hull ----------------------------------------------------------

	[Fact]
	public void TheFirstBoxEntered_IsTheOneHit_AndOnlyWhenNearerThanWhatWasAlreadyFound()
	{
		HitObb[] hull =
		{
			HitObb.Aligned(new Vector3(0f, 0f, 0f), new Vector3(1f, 1f, 1f)),
			HitObb.Aligned(new Vector3(0f, 0f, -4f), new Vector3(1f, 1f, 1f)),
		};

		// From −Z towards +Z: the far box in the list is the one it enters first.
		var from = new Vector3(0f, 0f, -10f);
		var to = new Vector3(0f, 0f, 10f);
		float nearest = 1f;
		Assert.True(CrewCombat.FirstHit(hull, from, to, 0f, ref nearest, out int box));
		Assert.Equal(1, box);
		Assert.Equal(5f / 20f, nearest, 5);

		// Something already found nearer — a player, a unit, a wall — keeps it.
		nearest = 0.2f;
		Assert.False(CrewCombat.FirstHit(hull, from, to, 0f, ref nearest, out box));
		Assert.Equal(-1, box);
		Assert.Equal(0.2f, nearest);

		// And a segment that misses every box finds nothing.
		nearest = 1f;
		Assert.False(CrewCombat.FirstHit(hull, new Vector3(5f, 0f, -10f), new Vector3(5f, 0f, 10f), 0f, ref nearest,
			out box));
		Assert.Equal(1f, nearest);
	}

	[Theory]
	[InlineData(CrewVehicleFiles.FourByFourPath)]
	[InlineData(CrewVehicleFiles.HelicopterPath)]
	public void ARoundAtTheDriver_StopsAtTheHull_InTheOrderTheServerAsks(string path)
	{
		// CombatManager.FirstContact tests the players first and then the hull, each only
		// if strictly nearer than what was found (GV-D11). So the hull has to win from the
		// seat's own fraction: 20,000 segments from 40 m out, aimed through the enclosed
		// seat, with a bullet's sweep and a grenade's, whatever the hull's pose.
		var random = new Random(11);
		CrewSeat seat = Array.Find(CrewVehicleFiles.Seats(path), s => s.Enclosed);
		HullBox[] hull = CrewVehicleFiles.Hull(path);

		for (int i = 0; i < 20_000; i++)
		{
			var pose = new CrewPose(new Vector3(Next(random, 100f), Next(random, 10f), Next(random, 100f)),
				Next(random, MathF.PI), Next(random, 0.6f), Next(random, 0.6f));
			HitCapsule capsule = pose.Place(seat.Capsule);
			Vector3 target = capsule.Bottom.Lerp(capsule.Top, (float)random.NextDouble())
				+ (RandomUnit(random) * (capsule.Radius * (float)random.NextDouble()));
			Vector3 from = target + (RandomUnit(random) * 40f);
			Vector3 to = target + ((target - from) * 0.25f);
			float sweep = i % 4 == 0 ? 0.06f : 0f;

			Assert.True(Hitbox.SegmentIntersects(capsule, from, to, sweep, out float seatT));
			float nearest = seatT;
			Assert.True(CrewCombat.FirstHit(Placed(hull, pose), from, to, sweep, ref nearest, out _),
				$"segment {i} reached the {seat.Role} at {seatT} and no hull box before it");
		}
	}

	[Fact]
	public void ABlastIsMeasuredToTheFaceOfTheNearestBox()
	{
		HitObb[] hull =
		{
			HitObb.Aligned(new Vector3(0f, 0f, 0f), new Vector3(1f, 1f, 1f)),
			HitObb.Aligned(new Vector3(0f, 0f, -4f), new Vector3(1f, 1f, 1f)),
		};

		Assert.Equal(0f, CrewCombat.BlastDistance(hull, new Vector3(0.5f, 0.5f, -4.5f)));
		Assert.Equal(2f, CrewCombat.BlastDistance(hull, new Vector3(3f, 0f, 0f)), 5);
		Assert.Equal(1f, CrewCombat.BlastDistance(hull, new Vector3(0f, 0f, 2f)), 5);
		Assert.Equal(float.PositiveInfinity, CrewCombat.BlastDistance(ReadOnlySpan<HitObb>.Empty, Vector3.Zero));
	}

	// ---- what the hull takes --------------------------------------------------------------

	private static ProjectileStats Round(string file, out Dictionary<string, string> values)
	{
		Dictionary<string, string> read = TrainedUnitResourceTests.Resource($"Weapons/projectiles/{file}.tres");
		values = read;
		var physics = ProjectileParams.FromGramsAndMillimeters(1f, 1f, 0.3f);
		float F(string key, float fallback) => read.ContainsKey(key) ? TrainedUnitResourceTests.Float(read, key) : fallback;
		return new ProjectileStats(physics, 100f, F("Damage", 25f), F("ExplosionRadiusMeters", 0f),
			F("ExplosionDamage", 0f), vehicleDamageScale: F("VehicleDamageScale", 1f),
			vehicleImpactDamage: F("VehicleImpactDamage", 0f));
	}

	[Theory]
	[InlineData(CrewVehicleFiles.FourByFourPath)]
	[InlineData(CrewVehicleFiles.HelicopterPath)]
	public void TheTanksRound_DestroysEitherWithOneDirectHit_AndARifleNeedsAHundredOrMore(string path)
	{
		Dictionary<string, string> vehicle = CrewVehicleFiles.Resource(path);
		float health = CrewVehicleFiles.F(vehicle, "MaxHealth");
		float bullets = CrewVehicleFiles.F(vehicle, "BulletDamageScale");

		ProjectileStats tank = Round("cannon_76", out _);
		ProjectileStats rifle = Round("rifle_556", out _);

		Assert.True(tank.IsExplosive);
		float direct = CrewCombat.DamageTaken(CrewCombat.DirectDamage(tank), tank.IsExplosive, bullets,
			tank.VehicleDamageScale);
		Assert.True(direct >= health, $"{path}: the tank's direct hit does {direct} of {health}");

		float perRifleHit = CrewCombat.DamageTaken(CrewCombat.DirectDamage(rifle), rifle.IsExplosive, bullets,
			rifle.VehicleDamageScale);
		Assert.Equal(rifle.Damage * bullets, perRifleHit, 4);
		Assert.True(health / perRifleHit >= 100f, $"{path}: {health / perRifleHit} rifle hits");

		// Its blast at the face does not, so a near miss leaves it running (GV-D10).
		float blast = CrewCombat.DamageTaken(tank.SplashDamageAt(0f), explosive: true, bullets, tank.VehicleDamageScale);
		Assert.True(blast < health, $"{path}: the tank's blast at the face does {blast} of {health}");
	}

	[Fact]
	public void AHandGrenadesBlast_DoesAQuarterOfItselfToAHull()
	{
		ProjectileStats grenade = Round("hand_grenade", out _);
		Assert.Equal(0.25f, grenade.VehicleDamageScale);
		float full = grenade.SplashDamageAt(0f);
		Assert.Equal(full * 0.25f, CrewCombat.DamageTaken(full, explosive: true, 0.15f, grenade.VehicleDamageScale), 4);
	}

	// ---- who a blast reaches -----------------------------------------------------------------

	[Theory]
	[InlineData(CrewVehicleFiles.FourByFourPath)]
	[InlineData(CrewVehicleFiles.HelicopterPath)]
	public void ABlast_SkipsTheDriverAndThePilot_AndReachesEveryExposedSeat(string path)
	{
		foreach (CrewSeat seat in CrewVehicleFiles.Seats(path))
		{
			Assert.Equal(!seat.Enclosed, CrewCombat.BlastReaches(seat));
			Assert.Equal(seat.Role == SeatRole.Driver, !CrewCombat.BlastReaches(seat));
		}
	}

	// ---- the wreck --------------------------------------------------------------------------

	[Fact]
	public void AWreck_KillsEverybodyAboardOnlyWhenItIsAHelicopterMoreThan4MetresUp()
	{
		Assert.False(CrewCombat.WreckKillsAll(CrewVehicleKind.FourByFour, 0f));
		Assert.False(CrewCombat.WreckKillsAll(CrewVehicleKind.FourByFour, 100f));
		Assert.False(CrewCombat.WreckKillsAll(CrewVehicleKind.FourByFour, float.NaN));

		Assert.False(CrewCombat.WreckKillsAll(CrewVehicleKind.Helicopter, 0f));
		Assert.False(CrewCombat.WreckKillsAll(CrewVehicleKind.Helicopter, 4f));
		Assert.True(CrewCombat.WreckKillsAll(CrewVehicleKind.Helicopter, 4.01f));
		Assert.True(CrewCombat.WreckKillsAll(CrewVehicleKind.Helicopter, 15f));

		// No ground found under it is not "on the ground".
		Assert.True(CrewCombat.WreckKillsAll(CrewVehicleKind.Helicopter, float.NaN));
		Assert.Equal(CrewSeats.MaxExitHeightMeters, CrewCombat.WreckSurvivableHeightMeters);
	}

	[Fact]
	public void AWreck_DealsHalfAPlayer_AndKillsOutrightThroughAnyVestWhoeverItCannotPutDown()
	{
		// The driver of a 4x4 a tank wrecks gets out at 50 (GV-D12), with no vest.
		BodyArmourState none = BodyArmourState.Full(BodyArmourTier.None);
		float placed = CrewCombat.WreckDamageTo(placed: true, SimConfig.MaxHealth, none.Points);
		Assert.Equal(50f, placed);
		Assert.Equal(50f, SimConfig.MaxHealth - BodyArmour.Absorb(ref none, placed, explosive: true));

		foreach (BodyArmourTier tier in new[] { BodyArmourTier.None, BodyArmourTier.Light, BodyArmourTier.Medium,
			BodyArmourTier.Heavy })
		{
			BodyArmourState vest = BodyArmourState.Full(tier);
			float outright = CrewCombat.WreckDamageTo(placed: false, SimConfig.MaxHealth, vest.Points);
			float lost = BodyArmour.Absorb(ref vest, outright, explosive: true);
			Assert.True(lost >= SimConfig.MaxHealth, $"{tier}: {lost} of {SimConfig.MaxHealth}");
		}
	}

	[Fact]
	public void AWreckBurnsForTwoSeconds()
	{
		Assert.Equal(2f * SimConfig.TickRate, CrewCombat.WreckBurnTicks);
	}
}
