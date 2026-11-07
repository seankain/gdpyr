using System;
using System.Collections.Generic;
using System.IO;
using Gdpyr.Sim;
using Godot;
using Xunit;

namespace Gdpyr.Tests;

/// <summary>
/// The mortar launcher (Scripts/Fps/MortarLauncher.cs): what it fires and how hard, what
/// feeds it, and where its shell comes down.
///
/// The numbers are read from the files the engine loads — the weapon, its round and the
/// tank — so a retune that breaks "three direct hits destroy a tank" fails here, with the
/// file that broke it.
/// </summary>
public class MortarLauncherTests
{
	private const string WeaponFile = "Weapons/mortar_launcher/mortar_launcher.tres";
	private const string RoundFile = "Weapons/projectiles/mortar_81_launcher.tres";
	private const string TankFile = "Units/tank.tres";

	/// <summary>A gunner's eye above the ground they and the launcher stand on [m].</summary>
	private const float EyeHeight = 1.6f;

	private static Dictionary<string, string> Weapon => TrainedUnitResourceTests.Resource(WeaponFile);

	private static Dictionary<string, string> RoundValues => TrainedUnitResourceTests.Resource(RoundFile);

	private static Dictionary<string, string> Tank => TrainedUnitResourceTests.Resource(TankFile);

	private static float F(Dictionary<string, string> values, string key) => TrainedUnitResourceTests.Float(values, key);

	private static float FOr(Dictionary<string, string> values, string key, float fallback) =>
		values.ContainsKey(key) ? F(values, key) : fallback;

	/// <summary>The round as <c>ProjectileDefinition.ToStats</c> builds it, from its file.</summary>
	private static ProjectileStats Round
	{
		get
		{
			Dictionary<string, string> r = RoundValues;
			return new ProjectileStats(
				ProjectileParams.FromGramsAndMillimeters(F(r, "MassGrams"), F(r, "RadiusMillimeters"),
					F(r, "DragCoefficient")),
				F(r, "MuzzleVelocity"), F(r, "Damage"), F(r, "ExplosionRadiusMeters"), F(r, "ExplosionDamage"),
				F(r, "LifetimeSeconds"), F(r, "SweepRadiusMeters"), r.TryGetValue("Fused", out string fused) && fused == "true",
				FOr(r, "VehicleDamageScale", 1f), FOr(r, "StructureDamageScale", 1f), FOr(r, "VehicleImpactDamage", 0f));
		}
	}

	private static int TicksPerShot => WeaponStats.TicksPerShot(F(Weapon, "RoundsPerMinute"));

	// ---- the files ---------------------------------------------------------------------

	[Fact]
	public void Weapon16_IsTheLauncher_FiringItsOwnShell_FiveToATube_WithNoReload()
	{
		Assert.Equal("res://" + WeaponFile, TrainedUnitResourceTests.WeaponPath(16));
		Assert.Contains("res://" + RoundFile, File.ReadAllText(TrainedUnitResourceTests.Repo(WeaponFile)));

		Assert.Equal(SimConfig.MortarRoundsPerCan, TrainedUnitResourceTests.Int(Weapon, "MagazineSize"));
		Assert.Equal(0f, F(Weapon, "ReloadSeconds"));
		Assert.Equal((int)FireMode.Semi, TrainedUnitResourceTests.Int(Weapon, "FireMode"));
	}

	[Fact]
	public void TheShell_IsTheBarracksMortarsArc_WithOnlyItsDirectHitHeavier()
	{
		Dictionary<string, string> barracks = TrainedUnitResourceTests.Resource("Weapons/projectiles/mortar_81.tres");
		Dictionary<string, string> launcher = RoundValues;

		foreach (string key in new[] { "MassGrams", "RadiusMillimeters", "DragCoefficient", "MuzzleVelocity",
			"LifetimeSeconds", "ExplosionRadiusMeters", "ExplosionDamage", "SweepRadiusMeters" })
		{
			Assert.Equal(barracks[key], launcher[key]);
		}

		// The barracks' own is left as it was: this is the ground force's shell, not a retune of the strategist's.
		Assert.Equal(100f, F(barracks, "Damage"));
		Assert.True(F(launcher, "Damage") > F(barracks, "Damage"));

		// A direct hit on a crew vehicle is its ordinary damage: GV-D10's override stays the tank's alone.
		Assert.DoesNotContain("VehicleImpactDamage", File.ReadAllText(TrainedUnitResourceTests.Repo(RoundFile)));
	}

	// ---- three direct hits destroy a tank ---------------------------------------------------

	/// <summary>
	/// What one direct hit takes off a tank: the round's damage under the armour rule a unit
	/// is hurt by (<c>UnitManager.Damage</c> → <see cref="Armour.DamageTaken(float, bool, float, float)"/>).
	/// The direct victim is left out of the blast (<c>CombatManager.Explode</c>), so this is all of it.
	/// </summary>
	private static float DirectHitOnTank()
	{
		ProjectileStats round = Round;
		Assert.True(round.IsExplosive, "a tank only takes explosives");
		Assert.Equal("true", Tank["IsVehicle"]);
		return Armour.DamageTaken(round.Damage, round.IsExplosive, F(Tank, "BulletDamageScale"), round.VehicleDamageScale);
	}

	[Fact]
	public void ThreeDirectHits_DestroyATank_AndTwoDoNot()
	{
		float hull = F(Tank, "MaxHealth");
		float hit = DirectHitOnTank();

		Assert.True(2f * hit < hull, $"two hits do {2f * hit} to a {hull} tank");
		Assert.True(3f * hit >= hull, $"three hits do {3f * hit} to a {hull} tank");
	}

	[Fact]
	public void ThreeDirectHits_DestroyATank_EvenOneHealingTheWholeTimeAtTheLaunchersCadence()
	{
		// The worst case the resupply rule allows: a tank inside a supply ring regaining its
		// full rate for every second between the first hit and the third, lockout or none.
		float hull = F(Tank, "MaxHealth");
		float healed = 2f * TicksPerShot * SimConfig.TickDelta * F(Tank, "RegenPerSecond");

		Assert.True(3f * DirectHitOnTank() >= hull + healed,
			$"three hits do {3f * DirectHitOnTank()}; the tank has {hull} and heals {healed} between them");
	}

	[Fact]
	public void TheLaunchersCadence_IsNoShorterThanTheResupplyLockout_SoATankUnderItsFireNeverHeals()
	{
		Assert.True(TicksPerShot >= Resupply.DamageLockoutTicks,
			$"{TicksPerShot} ticks a shell, {Resupply.DamageLockoutTicks} ticks of lockout");
	}

	[Theory]
	[InlineData(0)]
	[InlineData(60)]
	[InlineData(300)]
	public void ATankInASupplyRing_HitEveryCycle_IsDestroyedOnTheThirdHit(int extraTicksBetweenHits)
	{
		// The two rules UnitManager runs a tank through, tick by tick: a hit through the
		// armour, and the resupply rule's heal once the lockout has run out.
		float hull = F(Tank, "MaxHealth");
		float regen = F(Tank, "RegenPerSecond");
		float hit = DirectHitOnTank();
		int gap = TicksPerShot + extraTicksBetweenHits;

		float health = hull;
		uint lastDamaged = 0;
		int hits = 0;

		for (uint tick = 1; tick <= (uint)(gap * 4) && health > 0f; tick++)
		{
			if (Resupply.CanHeal(tick, lastDamaged))
			{
				health = Resupply.Heal(health, hull, regen, SimConfig.TickDelta);
			}

			if ((tick - 1) % gap == 0)
			{
				health = MathF.Max(0f, health - hit);
				lastDamaged = tick;
				hits++;
			}
		}

		// Five extra seconds between hits is 87.5 HP healed each time, inside the 100 HP the
		// third hit has to spare; the tank still goes on the third.
		Assert.Equal(0f, health);
		Assert.Equal(3, hits);
	}

	[Fact]
	public void NearMisses_AreNotHowATankIsKilled()
	{
		// The blast at the hull's face is the most a shell that misses does, and three of
		// those leave a tank standing: it is the direct hits that count.
		ProjectileStats round = Round;
		float atFace = Armour.DamageTaken(round.SplashDamageAt(0f), true, F(Tank, "BulletDamageScale"),
			round.VehicleDamageScale);

		Assert.True(atFace < DirectHitOnTank());
		Assert.True(3f * atFace < F(Tank, "MaxHealth"));
	}

	// ---- the cans ------------------------------------------------------------------------

	[Theory]
	[InlineData(0f, true)]
	[InlineData(0.6f, true)]
	[InlineData(0.999f, true)]
	[InlineData(1f, true)]
	[InlineData(1.01f, false)]
	[InlineData(2.5f, false)]
	public void ACanFeedsALauncher_WithinAMetre(float meters, bool feeds)
	{
		var launcher = new Vector3(4f, 0.5f, 4f);
		Vector3 bearing = new Vector3(0.6f, 0f, -0.8f);
		Assert.Equal(feeds, EmplacementSim.FeedsFrom(launcher, launcher + (bearing * meters)));
	}

	[Fact]
	public void ACan_GivesFiveRounds_AndOnlyToATubeWithRoomForAllFive()
	{
		int tube = TrainedUnitResourceTests.Int(Weapon, "MagazineSize");
		Assert.Equal(5, SimConfig.MortarRoundsPerCan);

		Assert.True(EmplacementSim.TakesCan(0, tube, SimConfig.MortarRoundsPerCan));
		for (short ammo = 1; ammo <= tube; ammo++)
		{
			Assert.False(EmplacementSim.TakesCan(ammo, tube, SimConfig.MortarRoundsPerCan));
		}

		Assert.False(EmplacementSim.TakesCan(0, tube, 0));
	}

	[Fact]
	public void TwoCansBesideAnEmptyTube_FeedItOneAtATime()
	{
		// The manager's loop: the first can that fits is taken, then the tube is full and the
		// second waits on the ground, whole, for the tube to empty again.
		int tube = TrainedUnitResourceTests.Int(Weapon, "MagazineSize");
		short ammo = 0;
		int spent = 0;

		for (int can = 0; can < 2 && EmplacementSim.TakesCan(ammo, tube, SimConfig.MortarRoundsPerCan); can++)
		{
			ammo += SimConfig.MortarRoundsPerCan;
			spent++;
		}

		Assert.Equal(5, ammo);
		Assert.Equal(1, spent);
	}

	[Theory]
	[InlineData(2f, 0f, 0f)]
	[InlineData(-1f, 0.3f, 2f)]
	[InlineData(0f, 0f, 0f)]
	public void ACanBroughtToALauncher_IsSetDownInsideItsMetre_OnItsGround(float x, float y, float z)
	{
		var launcher = new Vector3(4f, 0.5f, 4f);
		Vector3 point = EmplacementSim.StackPoint(launcher, 0.3f, launcher + new Vector3(x, y, z));

		Assert.True(EmplacementSim.FeedsFrom(launcher, point));
		Assert.Equal(launcher.Y, point.Y, 4);
		Assert.Equal(SimConfig.MortarStackMeters, launcher.DistanceTo(point), 4);
	}

	// ---- the lay -------------------------------------------------------------------------

	private static (float Min, float Max) Limits()
	{
		Assert.True(MortarLay.TryRangeLimits(Round, SimConfig.MortarMuzzleHeightMeters, out float min, out float max));
		return (min, max);
	}

	[Fact]
	public void ItsNearestReach_IsOutsideItsOwnBlast_ForAGunnerAtArmsLength()
	{
		(float min, float max) = Limits();
		Assert.True(min >= Round.ExplosionRadiusMeters + SimConfig.EmplacementReachMeters, $"nearest {min} m");
		Assert.InRange(max, 80f, 130f);
	}

	[Fact]
	public void EveryDistanceBetweenItsReaches_Solves()
	{
		(float min, float max) = Limits();
		var ground = new Vector3(4f, 0.5f, 4f);
		Vector3 muzzle = ground + (Vector3.Up * SimConfig.MortarMuzzleHeightMeters);

		for (float d = min; d <= max; d += (max - min) / 40f)
		{
			Assert.True(MortarSolver.TrySolve(muzzle, ground + new Vector3(0f, 0f, -d), Round, out _, out _),
				$"no solve at {d} m");
		}

		Assert.True(MortarSolver.TrySolve(muzzle, ground + new Vector3(0f, 0f, -max), Round, out _, out _));
	}

	[Theory]
	[InlineData(20f, 0f, 1.5f)]
	[InlineData(45f, 0.3f, 1.5f)]
	[InlineData(70f, -0.5f, 2f)]
	[InlineData(95f, 0.6f, 1f)]
	public void TheShell_ComesDownWhereTheGunnerLooksAtTheGround(float distance, float bearing, float behind)
	{
		// The gunner stands behind the launcher, not on it, and looks at a point on the
		// ground in front: the round is laid on that point and, flown by the simulation's
		// own integrator, comes down on it.
		const float deployYaw = 0.2f;
		var ground = new Vector3(4f, 0.5f, 4f);
		Vector3 point = ground + (Aim.Direction(deployYaw + bearing, 0f) * distance);
		Vector3 eye = ground - (Aim.Direction(deployYaw, 0f) * behind) + (Vector3.Up * EyeHeight);
		Aim.Angles(point - eye, out float yaw, out float pitch);

		(float min, float max) = Limits();
		Vector3 target = MortarLay.Target(eye, yaw, pitch, ground, deployYaw, min, max);
		Assert.True(target.DistanceTo(point) < 0.01f, $"laid {target.DistanceTo(point):0.000} m off the look");

		Vector3 muzzle = ground + (Vector3.Up * SimConfig.MortarMuzzleHeightMeters);
		Vector3 landed = Fly(muzzle, MortarLay.Direction(muzzle, target, Round), ground.Y);
		Vector3 miss = landed - point;
		miss.Y = 0f;

		// Inside a tank's 1.3 m radius by a distance: a stationary tank under the marker is a direct hit.
		Assert.True(miss.Length() < 0.5f, $"missed by {miss.Length():0.00} m at {distance} m");
	}

	[Fact]
	public void ALookAtOrAboveTheHorizon_IsItsFurthestReach_AlongTheLook()
	{
		(float min, float max) = Limits();
		var ground = new Vector3(0f, 0f, 0f);
		Vector3 eye = ground + (Vector3.Up * EyeHeight);

		foreach (float pitch in new[] { 0f, 0.4f, -0.00001f })
		{
			Vector3 target = MortarLay.Target(eye, 0.1f, pitch, ground, 0f, min, max);
			Assert.Equal(max, MortarLay.RangeOf(ground, target), 3);
			Aim.Angles(target - ground, out float yaw, out _);
			Assert.Equal(0.1f, yaw, 4);
		}
	}

	[Fact]
	public void ALookTooCloseOrTooFar_IsHeldToItsReach()
	{
		(float min, float max) = Limits();
		var ground = Vector3.Zero;
		Vector3 eye = ground + (Vector3.Up * EyeHeight);

		Vector3 feet = MortarLay.Target(eye, 0f, -1.4f, ground, 0f, min, max);
		Assert.Equal(min, MortarLay.RangeOf(ground, feet), 3);

		Aim.Angles(new Vector3(0f, -EyeHeight, -400f), out float yaw, out float pitch);
		Vector3 far = MortarLay.Target(eye, yaw, pitch, ground, 0f, min, max);
		Assert.Equal(max, MortarLay.RangeOf(ground, far), 3);
	}

	[Fact]
	public void ItTraversesNoFurtherThanTheHeavyGun()
	{
		(float min, float max) = Limits();
		var ground = Vector3.Zero;
		Vector3 eye = ground + (Vector3.Up * EyeHeight);

		Vector3 target = MortarLay.Target(eye, 2.5f, -0.05f, ground, 0f, min, max);
		Aim.Angles(target - ground, out float yaw, out _);
		Assert.Equal(SimConfig.EmplacementTraverseRadians, yaw, 4);
	}

	[Fact]
	public void ItsArc_IsTheHighOne()
	{
		(float min, float max) = Limits();
		Vector3 muzzle = Vector3.Up * SimConfig.MortarMuzzleHeightMeters;
		foreach (float d in new[] { min, 0.5f * (min + max), max - 0.01f })
		{
			Aim.Angles(MortarLay.Direction(muzzle, new Vector3(0f, 0f, -d), Round), out _, out float pitch);
			Assert.True(pitch >= Mathf.DegToRad(44f), $"{Mathf.RadToDeg(pitch):0.0}° at {d} m");
		}
	}

	/// <summary>Where a shell fired from <paramref name="origin"/> comes down through <paramref name="height"/>, flown as the server flies it.</summary>
	private static Vector3 Fly(Vector3 origin, Vector3 direction, float height)
	{
		var sim = new ProjectileSim(new[] { Round }, 4);
		Assert.NotEqual(0u, sim.Spawn(0, OwnerId.ForPeer(1), 0, origin, direction, 0));

		for (int tick = 1; tick < SimConfig.TickRate * 20; tick++)
		{
			sim.Step(SimConfig.TickDelta);
			ProjectileSegment segment = sim.Segments[0];

			if (segment.From.Y > height && segment.To.Y <= height)
			{
				float f = (segment.From.Y - height) / (segment.From.Y - segment.To.Y);
				return segment.PointAt(f);
			}
		}

		throw new InvalidOperationException("the shell never came down");
	}
}
