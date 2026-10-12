using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Gdpyr.Sim;
using Godot;
using Xunit;

namespace Gdpyr.Tests;

/// <summary>
/// Hand grenades (docs/NETCODE.md §10.9): the throw, the arc a bot or a unit works
/// out, when they decide to, the fuse, what a grenade does to a vehicle or a
/// structure, and the files that say all of it.
/// </summary>
public class GrenadeTests
{
	private const byte Grenade = 1;
	private const byte Shell = 2;

	/// <summary>The hand grenade as its resource file writes it, so the arcs below are the real one's.</summary>
	private static ProjectileStats HandGrenade()
	{
		Dictionary<string, string> file = TrainedUnitResourceTests.Resource("Weapons/projectiles/hand_grenade.tres");
		float F(string key) => TrainedUnitResourceTests.Float(file, key);
		return new ProjectileStats(
			ProjectileParams.FromGramsAndMillimeters(F("MassGrams"), F("RadiusMillimeters"), F("DragCoefficient")),
			F("MuzzleVelocity"), F("Damage"), F("ExplosionRadiusMeters"), F("ExplosionDamage"), F("LifetimeSeconds"),
			F("SweepRadiusMeters"), file["Fused"] == "true", F("VehicleDamageScale"), F("StructureDamageScale"));
	}

	private static ProjectileStats[] Definitions()
	{
		var table = new ProjectileStats[3];
		table[Grenade] = HandGrenade();
		table[Shell] = new ProjectileStats(
			ProjectileParams.FromGramsAndMillimeters(230f, 20f, 0.6f),
			muzzleVelocity: 76f, damage: 40f, explosionRadiusMeters: 5f, explosionDamage: 90f,
			lifetimeSeconds: 1f, sweepRadiusMeters: 0.1f);
		return table;
	}

	private static InputContext Input(InputButtons held, InputButtons previous = InputButtons.None) =>
		new(InputFrame.Create(0, 0f, 0f, 0f, 0f, held), (ushort)previous, SimConfig.TickDelta);

	// ---- the throw ---------------------------------------------------------

	[Fact]
	public void ALifeStartsWithThree()
	{
		Assert.Equal(3, GrenadeSim.Carried);
		Assert.Equal(3, GrenadeState.Full(GrenadeSim.Carried).Count);

		// What the snapshot can count is the ceiling, whatever a definition asks for.
		Assert.Equal(SimConfig.MaxGrenades, GrenadeState.Full(9).Count);
		Assert.Equal(0, GrenadeState.Full(-1).Count);
	}

	[Fact]
	public void APressThrowsOne_AHoldDoesNotThrowAnother()
	{
		GrenadeState state = GrenadeState.Full(3);

		Assert.True(GrenadeSim.Step(ref state, Input(InputButtons.Grenade), 100));
		Assert.Equal(2, state.Count);
		Assert.Equal(1u, state.Thrown);
		Assert.Equal(100u, state.LastThrowTick);

		// Held on, long past the wind-up: still one grenade.
		for (uint tick = 101; tick < 400; tick++)
		{
			Assert.False(GrenadeSim.Step(ref state, Input(InputButtons.Grenade, InputButtons.Grenade), tick));
		}
		Assert.Equal(2, state.Count);
	}

	[Fact]
	public void TheNextThrowWaitsForTheWindUp()
	{
		GrenadeState state = GrenadeState.Full(3);
		Assert.True(GrenadeSim.Step(ref state, Input(InputButtons.Grenade), 100));

		uint early = 100 + (uint)GrenadeSim.ThrowIntervalTicks - 1;
		Assert.False(GrenadeSim.Step(ref state, Input(InputButtons.Grenade), early));
		Assert.True(GrenadeSim.Step(ref state, Input(InputButtons.Grenade), early + 1));
		Assert.Equal(1, state.Count);
	}

	[Fact]
	public void ThreeAndThenNoMore()
	{
		GrenadeState state = GrenadeState.Full(GrenadeSim.Carried);
		int thrown = 0;
		for (uint tick = 0; tick < 3000; tick += 2)
		{
			// Mashed: pressed on even ticks, released on odd ones.
			if (GrenadeSim.Step(ref state, Input(InputButtons.Grenade), tick))
			{
				thrown++;
			}
		}

		Assert.Equal(3, thrown);
		Assert.Equal(0, state.Count);
		Assert.False(GrenadeSim.TrySpend(ref state, 5000, GrenadeSim.ThrowIntervalTicks));
	}

	[Fact]
	public void OtherButtonsThrowNothing()
	{
		GrenadeState state = GrenadeState.Full(3);
		InputButtons everythingElse = (InputButtons)0xFFFF & ~InputButtons.Grenade;

		Assert.False(GrenadeSim.Step(ref state, Input(everythingElse), 10));
		Assert.Equal(3, state.Count);
	}

	[Fact]
	public void BotsAndUnitsPaceThemselves()
	{
		GrenadeState state = GrenadeState.Full(3);
		Assert.True(GrenadeSim.AiReady(state, 0));
		Assert.True(GrenadeSim.TrySpend(ref state, 100, GrenadeSim.ThrowIntervalTicks));

		// A person may throw again after the wind-up; a bot or a unit waits longer.
		Assert.False(GrenadeSim.AiReady(state, 100 + (uint)GrenadeSim.ThrowIntervalTicks));
		Assert.False(GrenadeSim.AiReady(state, 100 + (uint)GrenadeSim.AiThrowIntervalTicks - 1));
		Assert.True(GrenadeSim.AiReady(state, 100 + (uint)GrenadeSim.AiThrowIntervalTicks));

		Assert.False(GrenadeSim.AiReady(GrenadeState.Full(0), 10_000));
	}

	[Theory]
	[InlineData(0f, 0f)]
	[InlineData(1.3f, -0.4f)]
	[InlineData(-2.9f, 0.7f)]
	public void AThrowLeavesTenDegreesAboveTheSights(float yaw, float pitch)
	{
		Aim.Angles(GrenadeSim.ThrowDirection(yaw, pitch), out float thrownYaw, out float thrownPitch);

		Assert.Equal(yaw, thrownYaw, 4);
		Assert.Equal(pitch + GrenadeSim.LoftRadians, thrownPitch, 4);

		// And a bot that has worked out an arc knows where to look for it.
		Vector3 look = GrenadeSim.LookFor(GrenadeSim.ThrowDirection(yaw, pitch));
		Assert.True(look.Dot(Aim.Direction(yaw, pitch)) > 0.99999f);
	}

	[Fact]
	public void LookingStraightUpThrowsStraightUp()
	{
		Vector3 thrown = GrenadeSim.ThrowDirection(0f, Quantize.HalfPi);
		Assert.Equal(1f, thrown.Y, 4);
	}

	[Fact]
	public void LookingLevel_AGrenadeLandsSeventeenMetresOff()
	{
		// The comment on LoftRadians, held to: from a standing eye on flat ground.
		ProjectileStats round = HandGrenade();
		float carried = GrenadeSim.Carry(new Vector3(0f, 1.6f, 0f), GrenadeSim.ThrowDirection(0f, 0f), round, 0f);

		Assert.InRange(carried, 16.5f, 17.5f);
	}

	[Fact]
	public void ThrownAsFarAsItGoes_ItStillLandsBeforeTheFuseRunsOut()
	{
		ProjectileStats round = HandGrenade();
		float carried = GrenadeSim.Carry(new Vector3(0f, 1.6f, 0f),
			GrenadeSim.ThrowDirection(0f, Mathf.DegToRad(35f)), round, 0f);

		Assert.InRange(carried, 32f, 34f);
	}

	[Fact]
	public void ThrownHigherStill_ItGoesOffInTheAir()
	{
		ProjectileStats round = HandGrenade();
		Assert.Equal(-1f, GrenadeSim.Carry(new Vector3(0f, 1.6f, 0f),
			GrenadeSim.ThrowDirection(0f, Mathf.DegToRad(45f)), round, 0f));
	}

	// ---- the arc a bot or a unit works out ------------------------------------

	[Theory]
	[InlineData(8f, -1.6f)]
	[InlineData(15f, -1.6f)]
	[InlineData(22f, -1.6f)]
	[InlineData(28f, -1.6f)]
	[InlineData(12f, 0f)]
	[InlineData(20f, 1.5f)]
	[InlineData(18f, -4f)]
	public void TheSolvedArcComesDownWhereItWasAimed(float distance, float height)
	{
		ProjectileStats[] table = Definitions();
		var eye = new Vector3(3f, 1.6f, -2f);
		Vector3 flat = new Vector3(0.6f, 0f, -0.8f);
		Vector3 target = eye + (flat * distance) + (Vector3.Up * height);

		Assert.True(GrenadeSim.TrySolve(eye, target, table[Grenade], out Vector3 direction));
		Assert.Equal(1f, direction.Length(), 4);

		// Flown by the projectile pool itself, chord by chord, not by the solver's own
		// integration: the point it comes down through the target's height is the point
		// it was aimed at.
		var sim = new ProjectileSim(table, 4);
		uint id = sim.Spawn(0, 1, Grenade, eye, direction, 0);
		Vector3? crossing = null;
		for (int tick = 0; tick < 400 && crossing == null; tick++)
		{
			sim.Step(SimConfig.TickDelta);
			foreach (ProjectileSegment segment in sim.Segments)
			{
				if (segment.Id == id && segment.To.Y <= target.Y && segment.To.Y < segment.From.Y)
				{
					float t = (segment.From.Y - target.Y) / (segment.From.Y - segment.To.Y);
					crossing = segment.PointAt(t);
				}
			}
		}

		Assert.NotNull(crossing);
		var miss = new Vector2(crossing.Value.X - target.X, crossing.Value.Z - target.Z);
		Assert.True(miss.Length() < 0.3f, $"came down {miss.Length():0.00} m from where it was aimed");
	}

	[Fact]
	public void TheSolvedArcIsTheLowOne()
	{
		ProjectileStats round = HandGrenade();
		var eye = new Vector3(0f, 1.6f, 0f);

		Assert.True(GrenadeSim.TrySolve(eye, new Vector3(0f, 0f, -20f), round, out Vector3 direction));
		Aim.Angles(direction, out _, out float elevation);
		Assert.InRange(elevation, 0f, Mathf.Pi / 4f);
	}

	[Theory]
	[InlineData(45f)]
	[InlineData(80f)]
	public void NoArcReachesPastAnArm(float distance)
	{
		var eye = new Vector3(0f, 1.6f, 0f);
		Assert.False(GrenadeSim.TrySolve(eye, new Vector3(0f, 0f, -distance), HandGrenade(), out _));
	}

	[Fact]
	public void NothingToSolveForAtTheThrowersFeet()
	{
		var eye = new Vector3(0f, 1.6f, 0f);
		Assert.False(GrenadeSim.TrySolve(eye, Vector3.Zero, HandGrenade(), out _));
	}

	// ---- when a bot or a unit throws ---------------------------------------------

	private static ThrowSituation Situation(bool hasGrenade = true, bool ready = true, float distance = 18f,
		float speed = 0f, bool inCover = false, bool friends = false) =>
		new(hasGrenade, ready, distance, speed, inCover, friends);

	[Fact]
	public void AStillTargetInRangeGetsAGrenade()
	{
		Assert.True(GrenadeSim.ShouldThrow(Situation()));
	}

	[Fact]
	public void AMovingTargetInTheOpenDoesNot_UnlessItIsInCover()
	{
		Assert.False(GrenadeSim.ShouldThrow(Situation(speed: 5f)));
		Assert.True(GrenadeSim.ShouldThrow(Situation(speed: 5f, inCover: true)));
		Assert.True(GrenadeSim.ShouldThrow(Situation(speed: GrenadeSim.SlowTargetMetersPerSecond)));
	}

	[Theory]
	[InlineData(GrenadeSim.MinThrowMeters - 0.1f, false)]
	[InlineData(GrenadeSim.MinThrowMeters, true)]
	[InlineData(GrenadeSim.MaxThrowMeters, true)]
	[InlineData(GrenadeSim.MaxThrowMeters + 0.1f, false)]
	public void OnlyInThrowingRange(float distance, bool throws)
	{
		Assert.Equal(throws, GrenadeSim.ShouldThrow(Situation(distance: distance)));
	}

	[Fact]
	public void NotWithoutOne_NotBeforeItIsReady_AndNeverIntoAFriend()
	{
		Assert.False(GrenadeSim.ShouldThrow(Situation(hasGrenade: false)));
		Assert.False(GrenadeSim.ShouldThrow(Situation(ready: false)));
		Assert.False(GrenadeSim.ShouldThrow(Situation(friends: true, inCover: true)));
	}

	// ---- the fuse ------------------------------------------------------------------

	[Fact]
	public void AGrenadeInFlightGoesOffWhenItsFuseRunsOut_AndOnlyThen()
	{
		ProjectileStats[] table = Definitions();
		var sim = new ProjectileSim(table, 4);
		uint id = sim.Spawn(0, 1, Grenade, new Vector3(0f, 200f, 0f), Vector3.Up, 0);

		int fuseTicks = (int)MathF.Ceiling(table[Grenade].LifetimeSeconds * SimConfig.TickRate);
		int detonations = 0;
		int lastTick = -1;
		for (int tick = 0; tick < fuseTicks + 30; tick++)
		{
			sim.Step(SimConfig.TickDelta);
			foreach (ProjectileSegment segment in sim.Segments)
			{
				Assert.Equal(id, segment.Id);
				Assert.False(segment.Resting);
				if (segment.Detonates)
				{
					detonations++;
					lastTick = tick;
				}
			}
		}

		Assert.Equal(1, detonations);
		Assert.InRange(lastTick, fuseTicks - 2, fuseTicks);
		Assert.False(sim.TryGet(id, out _));
	}

	[Fact]
	public void ALandedGrenadeLiesStill_FliesNoChord_AndGoesOffWhereItLies()
	{
		ProjectileStats[] table = Definitions();
		var sim = new ProjectileSim(table, 4);
		uint id = sim.Spawn(0, 1, Grenade, new Vector3(0f, 1.6f, 0f), Vector3.Forward, 0);

		sim.Step(SimConfig.TickDelta);
		var floor = new Vector3(0.1f, 0.06f, -4f);
		Assert.True(sim.Land(id, floor));

		int quiet = 0;
		ProjectileSegment? last = null;
		for (int tick = 1; tick < 400; tick++)
		{
			sim.Step(SimConfig.TickDelta);
			if (sim.Segments.Length == 0)
			{
				quiet++;
				Assert.True(sim.TryGet(id, out Projectile resting));
				Assert.True(resting.Resting);
				Assert.Equal(floor, resting.Position);
				Assert.Equal(Vector3.Zero, resting.Velocity);
				continue;
			}

			last = sim.Segments[0];
			break;
		}

		Assert.True(quiet > SimConfig.TickRate);
		Assert.NotNull(last);
		Assert.True(last.Value.Detonates);
		Assert.True(last.Value.Resting);
		Assert.Equal(floor, last.Value.From);
		Assert.Equal(floor, last.Value.To);
		Assert.Equal(0, sim.LiveCount);
	}

	[Fact]
	public void ARoundThatGoesOffOnContactIsNeverMarkedToDetonate()
	{
		ProjectileStats[] table = Definitions();
		var sim = new ProjectileSim(table, 4);
		sim.Spawn(0, 1, Shell, new Vector3(0f, 100f, 0f), Vector3.Forward, 0);

		for (int tick = 0; tick < 90; tick++)
		{
			sim.Step(SimConfig.TickDelta);
			foreach (ProjectileSegment segment in sim.Segments)
			{
				Assert.False(segment.Detonates);
			}
		}

		Assert.Equal(0, sim.LiveCount);
	}

	[Fact]
	public void OnlyAGrenadeInTheAirCanBePutDown()
	{
		var sim = new ProjectileSim(Definitions(), 4);
		Assert.False(sim.Land(42, Vector3.Zero));
	}

	[Fact]
	public void TheClientsCopyIsPutDownAtTheSamePoint_AfterCatchingUp()
	{
		// A client fast-forwards everybody else's throw to the tick it is drawing, then
		// is told where it landed: it lies there for the rest of its fuse, as the
		// server's does.
		ProjectileStats[] table = Definitions();
		var server = new ProjectileSim(table, 4);
		var client = new ProjectileSim(table, 4);
		var eye = new Vector3(0f, 1.6f, 0f);
		Vector3 direction = GrenadeSim.ThrowDirection(0f, 0f);

		uint id = server.Spawn(0, 1, Grenade, eye, direction, 0);
		Assert.True(client.TrySpawn(id, 0, 1, Grenade, eye, direction, catchUpTicks: 6, out _));

		var floor = new Vector3(0f, 0.06f, -9f);
		server.Step(SimConfig.TickDelta);
		client.Step(SimConfig.TickDelta);
		Assert.True(server.Land(id, floor));
		Assert.True(client.Land(id, floor));

		for (int tick = 0; tick < 30; tick++)
		{
			server.Step(SimConfig.TickDelta);
			client.Step(SimConfig.TickDelta);
		}

		Assert.True(server.TryGet(id, out Projectile a));
		Assert.True(client.TryGet(id, out Projectile b));
		Assert.Equal(a.Position, b.Position);
	}

	// ---- what it does to a vehicle or a structure ------------------------------------

	[Fact]
	public void AVehicleOrAStructureTakesAQuarterOfTheBlast()
	{
		ProjectileStats round = HandGrenade();
		float blast = round.SplashDamageAt(0f);

		// A soft body takes all of it.
		Assert.Equal(blast, Armour.DamageTaken(blast, explosive: true, bulletScale: 1f, roundScale: 1f));

		// A technical — a vehicle bullets hurt — and a tank, which only explosives do.
		Assert.Equal(blast * 0.25f, Armour.DamageTaken(blast, true, 1f, round.VehicleDamageScale), 3);
		Assert.Equal(blast * 0.25f, Armour.DamageTaken(blast, true, 0f, round.VehicleDamageScale), 3);

		// A pillbox, and a sandbag wall.
		Assert.Equal(blast * 0.25f, Armour.DamageTaken(blast, true, 0f, round.StructureDamageScale), 3);
		Assert.Equal(blast * 0.25f, Armour.DamageTaken(blast, true, 0.3f, round.StructureDamageScale), 3);
	}

	[Fact]
	public void TheShareComesAfterTheBulletRule()
	{
		// A bullet into armour is still nothing, whatever the round's share.
		Assert.Equal(0f, Armour.DamageTaken(20f, explosive: false, bulletScale: 0f, roundScale: 1f));
		Assert.Equal(6f, Armour.DamageTaken(20f, explosive: false, bulletScale: 0.3f, roundScale: 1f), 4);
		Assert.Equal(0f, Armour.DamageTaken(20f, explosive: true, bulletScale: 1f, roundScale: -1f));
	}

	[Fact]
	public void EveryOtherRoundHasNoShare()
	{
		ProjectileStats shell = Definitions()[Shell];
		Assert.Equal(1f, shell.VehicleDamageScale);
		Assert.Equal(1f, shell.StructureDamageScale);
		Assert.False(shell.Fused);
	}

	[Fact]
	public void ItIsAntiPersonnel()
	{
		// Lethal to a player near it, over a wider blast than the launcher's — and the
		// most it can do to a vehicle is well short of what the launcher's does at the
		// face, before the launcher's direct hit, which a grenade does not have.
		ProjectileStats grenade = HandGrenade();
		ProjectileStats launcher = LauncherGrenade();

		Assert.True(grenade.SplashDamageAt(1.5f) >= SimConfig.MaxHealth);
		Assert.True(grenade.ExplosionRadiusMeters > launcher.ExplosionRadiusMeters);
		Assert.Equal(0f, grenade.Damage);
		Assert.True(grenade.SplashDamageAt(0f) * grenade.VehicleDamageScale < launcher.SplashDamageAt(0f) / 2f);
	}

	private static ProjectileStats LauncherGrenade()
	{
		Dictionary<string, string> file = TrainedUnitResourceTests.Resource("Weapons/projectiles/grenade_40mm.tres");
		return new ProjectileStats(default, 90f, TrainedUnitResourceTests.Float(file, "Damage"),
			TrainedUnitResourceTests.Float(file, "ExplosionRadiusMeters"),
			TrainedUnitResourceTests.Float(file, "ExplosionDamage"));
	}

	// ---- on the wire -------------------------------------------------------------------

	[Fact]
	public void TheSnapshotCountsAPlayersGrenades()
	{
		for (int grenades = 0; grenades <= SimConfig.MaxGrenades; grenades++)
		{
			byte packed = WeaponFlags.Pack(2, reloading: true, Team.GroundForce, largeChoice: 1, grenades);

			Assert.Equal(grenades, WeaponFlags.Grenades(packed));
			Assert.Equal(2, WeaponFlags.Slot(packed));
			Assert.True(WeaponFlags.IsReloading(packed));
			Assert.Equal(Team.GroundForce, WeaponFlags.TeamOf(packed));
			Assert.Equal(1, WeaponFlags.LargeChoice(packed));
		}

		Assert.Equal(SimConfig.MaxGrenades, WeaponFlags.Grenades(WeaponFlags.Pack(0, false, Team.GroundForce, -1, 7)));
		Assert.Equal(0, WeaponFlags.Grenades(WeaponFlags.Pack(0, false, Team.GroundForce, -1, -2)));

		// A byte from before grenades says none.
		Assert.Equal(0, WeaponFlags.Grenades(WeaponFlags.Pack(1, false, Team.Strategist, 2)));
	}

	[Fact]
	public void TheGrenadeKeyIsItsOwnBitOfTheFrame()
	{
		var buttons = (ushort)InputButtons.Grenade;
		Assert.Equal(1 << 11, buttons);

		ushort others = (ushort)(InputButtons.Jump | InputButtons.Crouch | InputButtons.Sprint | InputButtons.Fire
			| InputButtons.Ads | InputButtons.Reload | InputButtons.Use | InputButtons.Melee | InputButtons.Weapon1
			| InputButtons.Weapon2 | InputButtons.Weapon3);
		Assert.Equal(0, buttons & others);

		var frame = InputFrame.Create(7, 0f, 0f, 0f, 0f, InputButtons.Grenade | InputButtons.Fire);
		Span<byte> bytes = stackalloc byte[InputFrame.SizeBytes];
		InputCodec.WriteFrame(frame, bytes);
		Assert.True(InputCodec.ReadFrame(bytes).Held(InputButtons.Grenade));
	}

	[Fact]
	public void ALandingSurvivesTheHitMessage()
	{
		var hit = new ProjectileHit { Id = 77, Point = new Vector3(4f, 0.06f, -9f), Flags = HitFlags.Landed };
		Assert.True(ProjectileCodec.TryDecodeHit(ProjectileCodec.EncodeHit(hit), out ProjectileHit back));

		Assert.Equal(77u, back.Id);
		Assert.Equal(HitFlags.Landed, back.Flags);
		Assert.Equal(hit.Point.X, back.Point.X, 2);
		Assert.Equal(hit.Point.Y, back.Point.Y, 2);
		Assert.Equal(hit.Point.Z, back.Point.Z, 2);
	}

	// ---- the files -----------------------------------------------------------------------

	[Fact]
	public void TheCatalogsGrenadeIsTheGrenadeFile_AndItThrowsTheFusedRound()
	{
		string source = File.ReadAllText(Repo("Scripts/Core/WeaponCatalog.cs"));
		int id = int.Parse(Regex.Match(source, @"public const byte HandGrenade = (\d+);").Groups[1].Value);

		Assert.Equal("res://Weapons/grenade/grenade.tres", TrainedUnitResourceTests.WeaponPath(id));
		Assert.Contains("res://Weapons/projectiles/hand_grenade.tres",
			File.ReadAllText(Repo("Weapons/grenade/grenade.tres")));

		Dictionary<string, string> round = TrainedUnitResourceTests.Resource("Weapons/projectiles/hand_grenade.tres");
		Assert.Equal("true", round["Fused"]);
		Assert.InRange(TrainedUnitResourceTests.Float(round, "LifetimeSeconds"), 2f, 5f);
		Assert.InRange(TrainedUnitResourceTests.Float(round, "VehicleDamageScale"), 0.01f, 0.99f);
		Assert.InRange(TrainedUnitResourceTests.Float(round, "StructureDamageScale"), 0.01f, 0.99f);
	}

	[Fact]
	public void UnitsOnFootCarryThree_VehiclesNone()
	{
		foreach (string vehicle in new[] { "technical", "tank", "supply_truck" })
		{
			Dictionary<string, string> file = TrainedUnitResourceTests.Resource($"Units/{vehicle}.tres");
			Assert.Equal("true", file["IsVehicle"]);
			Assert.Equal(0, TrainedUnitResourceTests.Int(file, "Grenades"));
		}

		// Nothing on foot says either, so each is a person and carries the default.
		foreach (string person in new[] { "infantry", "builder", "sniper", "heavy_gunner" })
		{
			Dictionary<string, string> file = TrainedUnitResourceTests.Resource($"Units/{person}.tres");
			Assert.False(file.ContainsKey("IsVehicle"));
			Assert.False(file.ContainsKey("Grenades"));
		}
	}

	[Fact]
	public void TheGrenadeKeyIsBound()
	{
		string project = File.ReadAllText(Repo("project.godot"));
		System.Text.RegularExpressions.Match action = Regex.Match(project, @"throw_grenade=\{[^}]*""physical_keycode"":(\d+)");

		Assert.True(action.Success);
		Assert.Equal(71, int.Parse(action.Groups[1].Value)); // G
		Assert.Contains("\"throw_grenade\"", File.ReadAllText(Repo("Scripts/Fps/LocalInputSampler.cs")));

		// And nothing else is on G.
		Assert.Single(Regex.Matches(project, @"""physical_keycode"":71\b"));
	}

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
