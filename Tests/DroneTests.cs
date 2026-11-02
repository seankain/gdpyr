using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Gdpyr.Sim;
using Gdpyr.Sim.Agent;
using Godot;
using Xunit;

namespace Gdpyr.Tests;

/// <summary>
/// The FPV drone, the ground force's perk for holding a drone node
/// (docs/CONTROL_POINTS.md §4): its flight, who may launch one, the side's shelf, the
/// state message, the HUD's line, a bot's autopilot, its key on the wire, and the files
/// that make its warhead a tank-killer and the map's nodes drone nodes.
/// </summary>
public class DroneTests
{
	private const float Dt = SimConfig.TickDelta;

	// ---- flight -----------------------------------------------------------------

	[Fact]
	public void ADrone_LeavesInFrontOfTheEye_HeadingWhereThePilotLooks()
	{
		Vector3 eye = new(3f, 1.6f, -10f);
		Drone drone = DroneSim.Launch(7, eye, yaw: 0f, pitch: 0f, tick: 100);

		Assert.True(drone.Active);
		Assert.Equal(7, drone.PilotPeerId);
		Assert.Equal(100u, drone.LaunchTick);
		Assert.Equal(eye.Z - DroneRules.LaunchForwardMeters, drone.Position.Z, 4);
		Assert.Equal(eye.X, drone.Position.X, 4);
		Assert.Equal(DroneRules.SpeedMetersPerSecond, drone.Velocity.Length(), 3);
	}

	[Fact]
	public void ADrone_FliesAtItsAirspeed_AndTheChordIsTheTicksTravel()
	{
		Drone drone = DroneSim.Launch(1, Vector3.Zero, 0f, 0f, 0);
		Vector3 start = drone.Position;
		Vector3 from = DroneSim.Step(ref drone, 0f, 0f, Dt);

		Assert.Equal(start, from);
		Assert.Equal(DroneRules.SpeedMetersPerSecond * Dt, from.DistanceTo(drone.Position), 4);
	}

	[Fact]
	public void ADrone_TurnsTowardsTheLook_NoFasterThanItsTurnRate()
	{
		Drone drone = DroneSim.Launch(1, Vector3.Zero, 0f, 0f, 0);
		float step = DroneRules.TurnRadiansPerSecond * Dt;

		DroneSim.Step(ref drone, MathF.PI / 2f, 0f, Dt);
		Assert.Equal(step, drone.Yaw, 4);

		// A small correction is made whole in one tick.
		DroneSim.Step(ref drone, drone.Yaw + (step * 0.5f), 0f, Dt);
		Assert.Equal(step * 1.5f, drone.Yaw, 4);

		// A whole second of holding the look at 90° gets it there.
		for (int i = 0; i < SimConfig.TickRate; i++)
		{
			DroneSim.Step(ref drone, MathF.PI / 2f, 0f, Dt);
		}

		Assert.Equal(MathF.PI / 2f, drone.Yaw, 3);
	}

	[Fact]
	public void ADrone_TurnsTheShortWayRound()
	{
		Drone drone = DroneSim.Launch(1, Vector3.Zero, yaw: 3f, pitch: 0f, tick: 0);

		// From +3 rad to -3 rad is 0.28 rad through π, not 6 rad back through zero.
		DroneSim.Step(ref drone, -3f, 0f, Dt);
		Assert.True(drone.Yaw > 3f || drone.Yaw < -3f, $"turned the long way: {drone.Yaw}");
	}

	[Fact]
	public void ADrone_NeverPitchesPastItsLimit()
	{
		Drone drone = DroneSim.Launch(1, Vector3.Zero, 0f, MathF.PI / 2f, 0);
		Assert.Equal(DroneRules.MaxPitchRadians, drone.Pitch, 4);

		for (int i = 0; i < SimConfig.TickRate * 2; i++)
		{
			DroneSim.Step(ref drone, 0f, -MathF.PI / 2f, Dt);
		}

		Assert.Equal(-DroneRules.MaxPitchRadians, drone.Pitch, 4);
	}

	[Fact]
	public void ItsBatteryRunsOut_OnItsLastTick()
	{
		Drone drone = DroneSim.Launch(1, Vector3.Zero, 0f, 0f, 1000);
		uint expire = 1000u + (uint)DroneRules.BatteryTicks;

		Assert.False(DroneSim.BatteryOut(drone, expire - 1));
		Assert.True(DroneSim.BatteryOut(drone, expire));
		Assert.Equal(DroneRules.BatteryTicks * Dt, drone.SecondsLeft(1000), 3);
		Assert.Equal(0f, drone.SecondsLeft(expire + 5));
	}

	[Theory]
	[InlineData(0f, 5f, 0f, false)]
	[InlineData(150f, 5f, 0f, true)]
	[InlineData(0f, 5f, -150f, true)]
	[InlineData(0f, -30f, 0f, true)]
	[InlineData(0f, DroneRules.CeilingMeters + 1f, 0f, true)]
	public void OffTheField_UnderIt_OrOutOfReachAbove_IsOutOfBounds(float x, float y, float z, bool outOfBounds)
	{
		Assert.Equal(outOfBounds, DroneSim.OutOfBounds(new Vector3(x, y, z), Cargo.FieldHalfExtentMeters));
	}

	[Theory]
	[InlineData(DroneEnd.Impact, true)]
	[InlineData(DroneEnd.Detonated, true)]
	[InlineData(DroneEnd.Battery, true)]
	[InlineData(DroneEnd.Lost, false)]
	[InlineData(DroneEnd.OutOfBounds, false)]
	public void OnlyALostDrone_GoesDownWithoutGoingOff(DroneEnd end, bool explodes)
	{
		Assert.Equal(explodes, DroneSim.Explodes(end));
	}

	[Fact]
	public void WrapAngle_IsInMinusPiToPi()
	{
		for (float angle = -20f; angle <= 20f; angle += 0.37f)
		{
			float wrapped = DroneSim.WrapAngle(angle);
			Assert.InRange(wrapped, -MathF.PI + 1e-5f, MathF.PI + 1e-5f);
			Assert.Equal(MathF.Sin(angle), MathF.Sin(wrapped), 3);
			Assert.Equal(MathF.Cos(angle), MathF.Cos(wrapped), 3);
		}
	}

	// ---- who may launch -----------------------------------------------------------

	[Fact]
	public void ALaunch_NeedsALivingGroundPlayer_NotFlyingOrMounted_WithOneReady()
	{
		Assert.True(DroneSim.MayLaunch(true, true, false, false, 500, 0, true));

		Assert.False(DroneSim.MayLaunch(false, true, false, false, 500, 0, true));
		Assert.False(DroneSim.MayLaunch(true, false, false, false, 500, 0, true));
		Assert.False(DroneSim.MayLaunch(true, true, true, false, 500, 0, true));
		Assert.False(DroneSim.MayLaunch(true, true, false, true, 500, 0, true));
		Assert.False(DroneSim.MayLaunch(true, true, false, false, 500, 0, false));
	}

	[Fact]
	public void ALaunch_WaitsOutTheRelaunchLockout()
	{
		uint ended = 1000;
		uint open = ended + (uint)DroneRules.RelaunchTicks;
		Assert.False(DroneSim.MayLaunch(true, true, false, false, open - 1, ended, true));
		Assert.True(DroneSim.MayLaunch(true, true, false, false, open, ended, true));
	}

	// ---- the shelf -----------------------------------------------------------------

	/// <summary>Holds <paramref name="nodes"/> drone nodes for <paramref name="ticks"/> ticks.</summary>
	private static void Hold(DroneStock stock, int nodes, int ticks)
	{
		for (int i = 0; i < ticks; i++)
		{
			stock.Tick(nodes);
		}
	}

	[Fact]
	public void TakingADroneNode_GivesNothingAtOnce_TheFirstIsBuiltByHoldingIt()
	{
		var stock = new DroneStock();
		stock.Tick(heldDroneNodes: 1);
		Assert.Equal(0, stock.Ready);
		Assert.False(stock.CanLaunch);
		Assert.False(stock.TryTake());

		Hold(stock, 1, DroneRules.RechargeTicks - 1);
		Assert.Equal(1, stock.Ready);
		Assert.True(stock.TryTake());
		Assert.Equal(0, stock.Ready);
		Assert.Equal(1, stock.Launched);
		Assert.False(stock.TryTake());
	}

	[Fact]
	public void AHeldDroneNode_BuildsOneEveryRecharge_AndTwoBuildTwiceAsFast()
	{
		var one = new DroneStock();
		Hold(one, 1, DroneRules.RechargeTicks - 1);
		Assert.Equal(0, one.Ready);
		Assert.InRange(one.Progress, 0.99f, 1f);
		one.Tick(1);
		Assert.Equal(1, one.Ready);

		var two = new DroneStock();
		Hold(two, 2, (DroneRules.RechargeTicks / 2) - 1);
		Assert.Equal(0, two.Ready);
		two.Tick(2);
		Assert.Equal(1, two.Ready);
	}

	[Fact]
	public void TheShelf_HoldsNoMoreThanItsCap()
	{
		var stock = new DroneStock();
		Hold(stock, 2, DroneRules.RechargeTicks * 5);

		Assert.Equal(DroneRules.MaxReady, stock.Ready);
		Assert.Equal(0f, stock.Progress);
		Assert.Equal(0, stock.ProgressTicks);
	}

	[Fact]
	public void WithNoDroneNodeHeld_NothingLaunches_WhateverIsOnTheShelf()
	{
		var stock = new DroneStock();
		Hold(stock, 1, DroneRules.RechargeTicks);
		Assert.Equal(1, stock.Ready);

		stock.Tick(0);
		Assert.Equal(1, stock.Ready);
		Assert.False(stock.CanLaunch);
		Assert.False(stock.TryTake());

		stock.Tick(1);
		Assert.True(stock.CanLaunch);
	}

	[Fact]
	public void LosingTheDroneNodes_PausesBuildingWhereItWas()
	{
		var stock = new DroneStock();
		Hold(stock, 1, DroneRules.RechargeTicks / 2);
		int progress = stock.ProgressTicks;
		Assert.Equal(DroneRules.RechargeTicks / 2, progress);

		Hold(stock, 0, DroneRules.RechargeTicks);
		Assert.Equal(progress, stock.ProgressTicks);
		Assert.Equal(0f, stock.Progress);

		stock.Tick(1);
		Assert.Equal(progress + 1, stock.ProgressTicks);
	}

	[Fact]
	public void Reset_EmptiesTheShelf()
	{
		var stock = new DroneStock();
		Hold(stock, 2, DroneRules.RechargeTicks);
		Assert.True(stock.TryTake());
		stock.Reset();

		Assert.Equal(0, stock.Ready);
		Assert.Equal(0, stock.ProgressTicks);
		Assert.Equal(0, stock.HeldNodes);
		Assert.Equal(0, stock.Launched);
	}

	// ---- the state message -----------------------------------------------------------

	[Fact]
	public void TheStateMessage_RoundTrips()
	{
		var drones = new[]
		{
			DroneSim.Launch(1073741824, new Vector3(4f, 1.6f, -35f), 0.4f, -0.2f, 100),
			DroneSim.Launch(5, new Vector3(-20f, 2f, 10f), 5.9f, 1.4f, 200),
		};

		var bytes = new byte[DroneCodec.SizeFor(drones.Length)];
		Assert.Equal(bytes.Length, DroneCodec.Encode(bytes, 2, 1, 0.5f, drones, tick: 300));

		var back = new Drone[DroneRules.MaxInFlight];
		var seconds = new byte[DroneRules.MaxInFlight];
		Assert.True(DroneCodec.TryDecode(bytes, out int ready, out int held, out float progress, back, seconds,
			out int count));

		Assert.Equal(2, ready);
		Assert.Equal(1, held);
		Assert.Equal(0.5f, progress, 2);
		Assert.Equal(2, count);
		for (int i = 0; i < count; i++)
		{
			Assert.True(back[i].Active);
			Assert.Equal(drones[i].PilotPeerId, back[i].PilotPeerId);
			Assert.Equal(drones[i].Position, back[i].Position);
			Assert.Equal(MathF.Sin(drones[i].Yaw), MathF.Sin(back[i].Yaw), 3);
			Assert.Equal(MathF.Cos(drones[i].Yaw), MathF.Cos(back[i].Yaw), 3);
			Assert.Equal(drones[i].Pitch, back[i].Pitch, 1);
			Assert.Equal((byte)MathF.Round(drones[i].SecondsLeft(300)), seconds[i]);
		}
	}

	[Fact]
	public void AnEmptySky_IsJustTheHeader()
	{
		var bytes = new byte[DroneCodec.SizeFor(0)];
		Assert.Equal(DroneCodec.HeaderBytes, DroneCodec.Encode(bytes, 0, 0, 0f, ReadOnlySpan<Drone>.Empty, 0));
		Assert.True(DroneCodec.TryDecode(bytes, out _, out _, out _, new Drone[4], new byte[4], out int count));
		Assert.Equal(0, count);
	}

	[Fact]
	public void AMalformedMessage_IsDropped()
	{
		var drones = new[] { DroneSim.Launch(1, Vector3.Zero, 0f, 0f, 0) };
		var bytes = new byte[DroneCodec.SizeFor(1)];
		DroneCodec.Encode(bytes, 1, 1, 0f, drones, 0);

		var into = new Drone[DroneRules.MaxInFlight];
		var seconds = new byte[DroneRules.MaxInFlight];
		Assert.False(DroneCodec.TryDecode(bytes.AsSpan(0, bytes.Length - 1), out _, out _, out _, into, seconds,
			out _));
		Assert.False(DroneCodec.TryDecode(bytes.AsSpan(0, 2), out _, out _, out _, into, seconds, out _));

		byte[] claimsMore = (byte[])bytes.Clone();
		claimsMore[3] = DroneRules.MaxInFlight + 1;
		Assert.False(DroneCodec.TryDecode(claimsMore, out _, out _, out _, into, seconds, out _));

		byte[] notANumber = (byte[])bytes.Clone();
		BitConverter.TryWriteBytes(notANumber.AsSpan(DroneCodec.HeaderBytes + 4), float.NaN);
		Assert.False(DroneCodec.TryDecode(notANumber, out _, out _, out _, into, seconds, out _));
	}

	// ---- the HUD's line --------------------------------------------------------------

	[Fact]
	public void TheHudLine_SaysWhatADroneNeeds()
	{
		Assert.Equal("drone 12 s · fire sets it off", DroneHud.Line(0, 1, 0f, flying: true, secondsLeft: 11.2f));
		Assert.Equal("[Q] drone", DroneHud.Line(1, 1, 0f, false, 0f));
		Assert.Equal("[Q] drone ×2", DroneHud.Line(2, 2, 0f, false, 0f));
		Assert.Equal("drone 40 %", DroneHud.Line(0, 1, 0.4f, false, 0f));
		Assert.Equal("2 drones — hold a drone node to launch", DroneHud.Line(2, 0, 0f, false, 0f));
		Assert.Equal("1 drone — hold a drone node to launch", DroneHud.Line(1, 0, 0f, false, 0f));
		Assert.Equal(string.Empty, DroneHud.Line(0, 0, 0f, false, 0f));
	}

	// ---- a bot's autopilot -----------------------------------------------------------

	[Theory]
	[InlineData(true, 60f, 100f, true)]
	[InlineData(false, 60f, 100f, false)]
	[InlineData(true, DroneAutopilot.MinLaunchRangeMeters - 1f, 100f, false)]
	[InlineData(true, DroneAutopilot.LaunchRangeMeters + 1f, 100f, false)]
	[InlineData(true, 60f, DroneAutopilot.ClearOfInfantryMeters, false)]
	public void ABot_LaunchesAtATankInRange_WithNothingOnFootClose(bool ready, float tank, float infantry, bool launch)
	{
		Assert.Equal(launch, DroneAutopilot.ShouldLaunch(ready, tank, infantry));
	}

	[Fact]
	public void TheAutopilot_FliesAtTheTarget()
	{
		Vector3 from = new(0f, 2f, 0f);
		Vector3 target = new(30f, 1f, -40f);
		DroneAutopilot.Steer(from, target, out float yaw, out float pitch);

		Drone drone = DroneSim.Launch(1, from, yaw, pitch, 0);
		Vector3 heading = (target - from).Normalized();
		Assert.True(drone.Forward.Dot(heading) > 0.999f);

		// Flown with those angles held, it passes within a hand's width of the target.
		float nearest = float.MaxValue;
		for (int i = 0; i < SimConfig.TickRate * 3; i++)
		{
			DroneSim.Step(ref drone, yaw, pitch, Dt);
			nearest = MathF.Min(nearest, drone.Position.DistanceTo(target));
		}

		Assert.True(nearest < DroneRules.SpeedMetersPerSecond * Dt, $"passed {nearest:0.00} m off");
	}

	// ---- its key on the wire -------------------------------------------------------

	[Fact]
	public void TheDroneKey_IsBit12_ClearOfEveryOther_AndSurvivesTheFrame()
	{
		Assert.Equal(1 << 12, (int)InputButtons.Drone);
		ushort others = (ushort)(InputButtons.Jump | InputButtons.Crouch | InputButtons.Sprint | InputButtons.Fire
			| InputButtons.Ads | InputButtons.Reload | InputButtons.Use | InputButtons.Melee | InputButtons.Weapon1
			| InputButtons.Weapon2 | InputButtons.Weapon3 | InputButtons.Grenade);
		Assert.Equal(0, (ushort)InputButtons.Drone & others);

		var frame = InputFrame.Create(9, 0f, 0f, 0f, 0f, InputButtons.Drone);
		Span<byte> bytes = stackalloc byte[InputFrame.SizeBytes];
		InputCodec.WriteFrame(frame, bytes);
		Assert.True(InputCodec.ReadFrame(bytes).Held(InputButtons.Drone));

		// A look-only frame — a dead player's, a pilot's body's — holds no drone key.
		Assert.False(InputFrame.LookOnly(frame).Held(InputButtons.Drone));
	}

	[Fact]
	public void APolicy_NamesTheDroneKey()
	{
		Assert.Contains("drone", AgentActionCodec.ButtonNames);
		Assert.Equal(InputButtons.Drone, AgentActionCodec.ButtonFromName("drone"));
		Assert.Equal((ushort)InputButtons.Drone, (ushort)Gdpyr.AgentClient.AgentButtons.Drone);
	}

	// ---- the files ------------------------------------------------------------------

	[Fact]
	public void TheCatalogsDrone_IsTheDroneFile_AndItsDirectHitKillsATank()
	{
		string source = File.ReadAllText(Repo("Scripts/Core/WeaponCatalog.cs"));
		int id = int.Parse(Regex.Match(source, @"public const byte FpvDrone = (\d+);").Groups[1].Value);
		Assert.Equal("res://Weapons/drone/drone.tres", TrainedUnitResourceTests.WeaponPath(id));
		Assert.Contains("res://Weapons/projectiles/fpv_drone.tres", File.ReadAllText(Repo("Weapons/drone/drone.tres")));

		Dictionary<string, string> round = TrainedUnitResourceTests.Resource("Weapons/projectiles/fpv_drone.tres");
		Dictionary<string, string> tank = TrainedUnitResourceTests.Resource("Units/tank.tres");
		float hull = TrainedUnitResourceTests.Float(tank, "MaxHealth");

		// The direct victim takes the round's damage and not the blast besides it, so the
		// round alone has to be enough — and with a margin for the tank's regeneration.
		float direct = TrainedUnitResourceTests.Float(round, "Damage");
		Assert.True(direct >= hull, $"a direct hit does {direct} to a {hull} tank");
		Assert.False(round.ContainsKey("VehicleDamageScale") && TrainedUnitResourceTests.Float(round, "VehicleDamageScale") < 1f);
		Assert.InRange(TrainedUnitResourceTests.Float(round, "ExplosionRadiusMeters"), 2f, 6f);
		Assert.False(round.ContainsKey("Fused"));

		// A miss by a few metres does not: the drone has to be flown onto the hull.
		Assert.True(TrainedUnitResourceTests.Float(round, "ExplosionDamage") < hull / 2f);

		// Its speed, written in the file for anybody reading the round, is the rule's.
		Assert.Equal(DroneRules.SpeedMetersPerSecond, TrainedUnitResourceTests.Float(round, "MuzzleVelocity"));
	}

	[Fact]
	public void TheMap_HasDroneNodes_AndTheNodeNearestTheGroundSpawnIsNotOne()
	{
		// Block by block: a node's header, then its properties up to the next header.
		var perks = new Dictionary<string, int>();
		string current = null;
		foreach (string line in File.ReadAllLines(Repo("Scenes/Test.tscn")))
		{
			if (line.StartsWith('['))
			{
				System.Text.RegularExpressions.Match header = Regex.Match(line, @"^\[node name=""(ResourceNode\d)""");
				current = header.Success ? header.Groups[1].Value : null;
				if (current != null)
				{
					perks[current] = (int)NodePerk.None;
				}

				continue;
			}

			if (current != null && line.StartsWith("Perk = ", StringComparison.Ordinal))
			{
				perks[current] = int.Parse(line["Perk = ".Length..]);
			}
		}

		Assert.Equal(3, perks.Count);
		Assert.Equal((int)NodePerk.None, perks["ResourceNode1"]);
		Assert.Equal((int)NodePerk.Drone, perks["ResourceNode2"]);
		Assert.Equal((int)NodePerk.Drone, perks["ResourceNode3"]);
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

		throw new FileNotFoundException(relative);
	}
}
