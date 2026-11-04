using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text.Json;
using Gdpyr.AgentClient;
using Gdpyr.Playtest;
using Gdpyr.Sim;
using Gdpyr.Sim.Agent;
using Godot;
using Xunit;

using ClientAction = Gdpyr.AgentClient.AgentGroundAction;
using ServerAction = Gdpyr.Sim.Agent.AgentGroundAction;

namespace Gdpyr.Tests;

/// <summary>
/// The crew vehicles in the agent API and the playtest harness (docs/GROUND_VEHICLES.md §11,
/// GV7): what a scenario's vehicle spawn and seat placement mean, and every sentence a
/// malformed one is refused with; a step's kiosk request and <c>look_at</c>; the harness's
/// copies of the vehicles' kinds, seats, health and refusals, pinned to the files and the
/// rules; the <c>first</c> aggregate; the vocabulary and records <c>list_vehicles</c>
/// writes and the client reads; the state hash over a vehicle; and an absolute look on
/// the wire.
/// </summary>
public class CrewVehicleAgentTests
{
	// ---- scenario spawns ------------------------------------------------------------

	/// <summary>A ground seat <c>me</c>, a second <c>you</c>, and <paramref name="spawns"/>.</summary>
	private static string WithSeats(string spawns, string seats = null) => $$"""
		{
		  "name": "crew",
		  "seats": [ {{seats ?? """{ "id": "me", "team": "ground", "policy": "ground" }, { "id": "you", "team": "ground", "policy": "ground" }"""}} ],
		  "spawns": [ {{spawns}} ],
		  "assert": [ { "metric": "events.vehicle_exited.count", "op": "==", "value": 0 } ]
		}
		""";

	[Fact]
	public void AVehicleSpawn_PutsDownAKindWithAHeadingAndHealth_AndASeatPlacementSitsInIt()
	{
		Scenario scenario = Scenario.Parse(WithSeats("""
			{ "vehicle": "4x4", "id": "truck", "at": [-60, 0.5, 50], "yaw": -1.5708, "health": 40 },
			{ "peer": "me", "seat": { "vehicle": "truck", "role": "gunner" } },
			{ "vehicle": "helicopter", "id": "heli", "at": [-20, 15.5, 50] },
			{ "peer": "you", "seat": { "vehicle": "heli", "role": "pilot" } }
			"""));

		ScenarioSpawn truck = scenario.Spawns[0];
		Assert.Equal("4x4", truck.Vehicle);
		Assert.Equal("truck", truck.Id);
		Assert.Equal(-1.5708f, truck.Yaw);
		Assert.Equal(40f, truck.Health);
		Assert.True(truck.HasAt);

		ScenarioSpawn gunner = scenario.Spawns[1];
		Assert.Equal("truck", gunner.SeatVehicle);
		Assert.Equal("gunner", gunner.SeatRole);
		Assert.False(gunner.HasAt);
		Assert.False(gunner.HasLook);
		Assert.Same(truck, Scenario.FindSpawn(scenario, gunner.SeatVehicle));

		// Health left out is the kind's full health, which the spawn sends as nothing.
		Assert.Equal(0f, scenario.Spawns[2].Health);
		Assert.Equal("driver", ScenarioVehicles.Role(scenario.Spawns[3].SeatRole));
	}

	[Theory]
	[InlineData("""{ "vehicle": "tank", "id": "t" }""", "a vehicle spawn's 'vehicle' is one of 4x4, helicopter")]
	[InlineData("""{ "vehicle": "4x4", "health": 601 }""", "a 4x4's 'health' is above 0 and at most 600")]
	[InlineData("""{ "vehicle": "helicopter", "health": 451 }""", "at most 450")]
	[InlineData("""{ "vehicle": "4x4", "health": 0 }""", "only a vehicle spawn takes a 'health', and it is above 0")]
	[InlineData("""{ "unit": "infantry", "health": 10 }""", "only a vehicle spawn takes a 'health'")]
	[InlineData("""{ "vehicle": "4x4", "aboard": "x" }""", "only a unit spawn takes an 'aboard'")]
	[InlineData("""{ "vehicle": "4x4", "unit": "infantry" }""", "a spawn names exactly one of")]
	[InlineData("""{ "peer": "me", "seat": { "vehicle": "truck", "role": "driver" } }""",
		"a seat is in 'truck', which is not the 'id' of an earlier vehicle spawn")]
	[InlineData("""{ "peer": "me", "seat": { "vehicle": "truck", "role": "driver" } }, { "vehicle": "4x4", "id": "truck" }""",
		"a seat is in 'truck', which is not the 'id' of an earlier vehicle spawn")]
	[InlineData("""{ "unit": "technical", "id": "truck" }, { "peer": "me", "seat": { "vehicle": "truck", "role": "driver" } }""",
		"which is not the 'id' of an earlier vehicle spawn")]
	[InlineData("""{ "vehicle": "helicopter", "id": "heli" }, { "peer": "me", "seat": { "vehicle": "heli", "role": "gunner" } }""",
		"the helicopter has no 'gunner' seat; its seats are driver, passenger")]
	[InlineData("""{ "vehicle": "4x4", "id": "truck" }, { "peer": "me", "seat": { "vehicle": "truck", "role": "roof" } }""",
		"the 4x4 has no 'roof' seat")]
	[InlineData("""{ "vehicle": "4x4", "id": "truck" }, { "peer": "me", "seat": { "vehicle": "truck", "role": "gunner" } }, { "peer": "you", "seat": { "vehicle": "truck", "role": "gunner" } }""",
		"two spawns sit in 'truck''s gunner seat, and it has 1")]
	[InlineData("""{ "vehicle": "helicopter", "id": "heli" }, { "peer": "me", "seat": { "vehicle": "heli", "role": "pilot" } }, { "peer": "you", "seat": { "vehicle": "heli", "role": "driver" } }""",
		"two spawns sit in 'heli''s driver seat")]
	[InlineData("""{ "vehicle": "4x4", "id": "truck" }, { "peer": "me", "seat": { "vehicle": "truck" } }""",
		"a spawn's 'seat' is {\"vehicle\": <a vehicle spawn's id>, \"role\"")]
	[InlineData("""{ "vehicle": "4x4", "id": "truck" }, { "peer": "me", "seat": { "vehicle": "truck", "role": "driver", "door": 1 } }""",
		"has no field 'door'; it takes 'vehicle' and 'role'")]
	[InlineData("""{ "vehicle": "4x4", "id": "truck" }, { "unit": "infantry", "seat": { "vehicle": "truck", "role": "driver" } }""",
		"only a 'peer' sits in one")]
	[InlineData("""{ "vehicle": "4x4", "id": "truck" }, { "vehicle": "4x4", "id": "truck" }""", "two spawns have the id 'truck'")]
	public void AVehicleSpawnOrSeatThatCannotBeResolved_IsASentence(string spawns, string expected)
	{
		ScenarioException error = Assert.Throws<ScenarioException>(() => Scenario.Parse(WithSeats(spawns)));
		Assert.Contains(expected, error.Message);
	}

	[Fact]
	public void AUnitCannotRideInACrewVehicle_NorAStrategistCommandNameOne()
	{
		ScenarioException aboard = Assert.Throws<ScenarioException>(() => Scenario.Parse(WithSeats(
			"""{ "vehicle": "4x4", "id": "truck" }, { "unit": "infantry", "aboard": "truck" }""",
			"""{ "id": "chair", "team": "strategist", "policy": "strategist" }""")));
		Assert.Contains("not the 'id' of an earlier unit spawn", aboard.Message);

		ScenarioException commanded = Assert.Throws<ScenarioException>(() => Scenario.Parse(WithSeats(
			"""{ "vehicle": "4x4", "id": "truck" }""",
			"""{ "id": "chair", "team": "strategist", "policy": "strategist", "script": { "steps": [ { "ticks": 1, "commands": [ { "cmd": "unload", "units": ["truck"] } ] } ] } }""")));
		Assert.Contains("names 'truck', and no unit spawn has that 'id'", commanded.Message);
	}

	// ---- script steps ---------------------------------------------------------------

	private static Scenario Stepped(string step, string policy = "ground") => Scenario.Parse($$"""
		{
		  "name": "stepped",
		  "seats": [ { "id": "me", "team": "{{(policy == "ground" ? "ground" : "strategist")}}", "policy": "{{policy}}",
		               "script": { "repeat": false, "steps": [ {{step}} ] } } ],
		  "assert": [ { "metric": "counters.vehicles_taken", "op": "==", "value": 1 } ]
		}
		""");

	[Fact]
	public void AStepCanAskTheKioskForAKind_AndLookAtAPoint()
	{
		ScriptStep asks = Stepped("""{ "ticks": 60, "vehicle": "helicopter" }""").Seats[0].Script.Steps[0];
		Assert.Equal("helicopter", asks.Vehicle);
		Assert.False(asks.HasInput);

		ScriptStep looks = Stepped("""{ "ticks": 60, "look_at": [20, 1.4, 25], "buttons": ["fire"] }""").Seats[0].Script.Steps[0];
		Assert.Equal(new[] { 20f, 1.4f, 25f }, looks.LookAt);
		Assert.True(looks.HasInput);
		Assert.Null(looks.Vehicle);
	}

	[Theory]
	[InlineData("""{ "ticks": 1, "vehicle": "tank" }""", "a step's 'vehicle' is the kind to ask the kiosk for, one of 4x4, helicopter")]
	[InlineData("""{ "ticks": 1, "vehicle": 0 }""", "a step's 'vehicle' is the kind to ask the kiosk for")]
	[InlineData("""{ "ticks": 1, "look_at": [1, 2] }""", "a step's 'look_at' is a point of the map, [x, y, z]")]
	[InlineData("""{ "ticks": 1, "look_at": [1, 2, 3], "look": { "dyaw": 0.1 } }""", "not both")]
	public void AStepsVehicleOrLookAtThatIsNotOne_IsASentence(string step, string expected)
	{
		ScenarioException error = Assert.Throws<ScenarioException>(() => Stepped(step));
		Assert.Contains(expected, error.Message);
	}

	[Theory]
	[InlineData("""{ "ticks": 1, "vehicle": "4x4" }""")]
	[InlineData("""{ "ticks": 1, "look_at": [1, 2, 3] }""")]
	public void AStrategistsStep_NeitherAsksTheKioskNorLooks(string step)
	{
		ScenarioException error = Assert.Throws<ScenarioException>(() => Stepped(step, "strategist"));
		Assert.Contains("is a strategist", error.Message);
	}

	// ---- the harness's copies, pinned -------------------------------------------------

	[Fact]
	public void TheHarnessKnowsTheKinds_AsTheAgentApiNamesThem()
	{
		Assert.Equal(AgentJson.VehicleNames, ScenarioVehicles.Kinds);
		Assert.Equal(CrewVehicleCodec.Kinds, ScenarioVehicles.Kinds.Length);
		for (byte kind = 0; kind < ScenarioVehicles.Kinds.Length; kind++)
		{
			Assert.Equal(KioskMenu.NameOf(kind), ScenarioVehicles.Kinds[kind]);
		}
	}

	[Fact]
	public void TheHarnessKnowsEachKindsSeatsAndHealth_AsTheFilesHaveThem()
	{
		for (int kind = 0; kind < CrewVehicleFiles.Paths.Length; kind++)
		{
			string path = CrewVehicleFiles.Paths[kind];
			Assert.Equal(CrewVehicleFiles.F(CrewVehicleFiles.Resource(path), "MaxHealth"), ScenarioVehicles.MaxHealth[kind]);

			CrewSeat[] seats = CrewVehicleFiles.Seats(path);
			var roles = new string[seats.Length];
			for (int seat = 0; seat < seats.Length; seat++)
			{
				roles[seat] = AgentJson.SeatRoleNames[(int)seats[seat].Role];
			}

			Assert.Equal(roles, ScenarioVehicles.Roles[kind]);
		}
	}

	[Fact]
	public void TheHarnessKnowsEveryRefusal_AsTheKioskSpellsThem()
	{
		var codes = new List<string>();
		for (byte refusal = 1; KioskRules.TryRead(refusal, out KioskRefusal read); refusal++)
		{
			codes.Add(KioskRules.CodeOf(read));
		}

		Assert.Equal(codes, ScenarioVehicles.Refusals);
		Assert.Equal("taken", KioskRules.CodeOf(KioskRefusal.None));
	}

	// ---- the first aggregate ------------------------------------------------------------

	[Fact]
	public void FirstIsWhatHappenedFirst_OverAKindAField_AFilterAndAnObservation()
	{
		var metrics = new EpisodeMetrics(1) { StartTick = 100, EndTick = 1000 };
		metrics.Seat("driver", 5);
		metrics.Event("damage", 300, Fields(("victim", 5), ("amount", 50), ("absorbed", 0)));
		metrics.Event("damage", 320, Fields(("victim", 5), ("amount", 20), ("absorbed", 0)));
		metrics.Event("damage", 310, Fields(("victim", 9), ("amount", 7), ("absorbed", 0)));
		metrics.Sample("self.health", 300, 0.5);
		metrics.Sample("self.health", 320, 0.3);

		Assert.True(metrics.TryValue("events.damage[victim=@driver].amount.first", out double first, out uint tick));
		Assert.Equal(50d, first);
		Assert.Equal(300u, tick);

		Assert.True(metrics.TryValue("events.damage.first", out double shorthand, out _));
		Assert.Equal(50d, shorthand);
		Assert.True(metrics.TryValue("events.damage[victim=9].first", out double other, out _));
		Assert.Equal(7d, other);
		Assert.True(metrics.TryValue("observation.self.health.first", out double health, out _));
		Assert.Equal(0.5d, health);

		// A kind that never fired has a first, and it is zero, as its count is.
		Assert.True(metrics.TryValue("events.vehicle_destroyed.weapon.first", out double none, out _));
		Assert.Equal(0d, none);
	}

	private static Dictionary<string, double> Fields(params (string Name, double Value)[] entries)
	{
		var fields = new Dictionary<string, double>();
		foreach ((string name, double value) in entries)
		{
			fields[name] = value;
		}

		return fields;
	}

	// ---- look_at -------------------------------------------------------------------------

	[Fact]
	public void LookAt_IsTheGamesLookConvention_FromTheEyeToThePoint()
	{
		float[] eye = { 0f, 2f, 0f };

		Assert.True(ScriptAim.Toward(eye, eye, 0, 1, new[] { 0f, 2f, -10f }, out float yaw, out float pitch));
		Assert.Equal(0f, yaw, 5);
		Assert.Equal(0f, pitch, 5);

		Assert.True(ScriptAim.Toward(eye, eye, 0, 1, new[] { 10f, 12f, 0f }, out yaw, out pitch));
		Assert.Equal(-MathF.PI / 2f, yaw, 5);
		Assert.Equal(MathF.PI / 4f, pitch, 5);

		// And the inverse of the game's own direction for a pair of angles.
		Assert.True(ScriptAim.Toward(eye, eye, 0, 1, new[] { 3f, -1f, 7f }, out yaw, out pitch));
		Vector3 along = Aim.Direction(yaw, pitch);
		Vector3 wanted = new Vector3(3f, -3f, 7f).Normalized();
		Assert.True(along.DistanceTo(wanted) < 1e-5f, $"{along} against {wanted}");
	}

	[Fact]
	public void LookAt_LeadsTheEyesOwnMotion_ToTheTickItShoots()
	{
		// A passenger moving east at 0.3 m a tick, one tick from the shot, 25 m off a point
		// abreast of where it will be: the look is square to the motion, not a tick behind.
		float[] before = { -0.3f, 2f, 0f };
		float[] now = { 0f, 2f, 0f };
		Assert.True(ScriptAim.Toward(now, before, 1, 1, new[] { 0.3f, 2f, -25f }, out float yaw, out _));
		Assert.Equal(0f, yaw, 5);

		Assert.True(ScriptAim.Toward(now, now, 0, 1, new[] { 0.3f, 2f, -25f }, out float unled, out _));
		Assert.True(unled < -0.01f, $"{unled}");
	}

	[Fact]
	public void LookAt_OnThePointItself_KeepsTheLook()
	{
		float[] eye = { 1f, 2f, 3f };
		Assert.False(ScriptAim.Toward(eye, eye, 1, 1, eye, out _, out _));
		Assert.False(ScriptAim.Toward(new[] { 1f }, eye, 1, 1, eye, out _, out _));
	}

	// ---- the agent API's vocabulary ------------------------------------------------------

	[Theory]
	[InlineData("4x4", 0)]
	[InlineData("helicopter", 1)]
	[InlineData("0", 0)]
	[InlineData("1", 1)]
	public void AVehicleIsNamedOrNumbered(string name, byte kind)
	{
		Assert.True(AgentJson.TryParseVehicle(name, out byte read));
		Assert.Equal(kind, read);
		Assert.Equal(AgentJson.VehicleNames[kind], AgentJson.VehicleName(kind));
	}

	[Theory]
	[InlineData("tank")]
	[InlineData("2")]
	[InlineData("")]
	[InlineData(null)]
	public void AnythingElseIsNoVehicle(string name) => Assert.False(AgentJson.TryParseVehicle(name, out _));

	[Theory]
	[InlineData("driver", SeatRole.Driver)]
	[InlineData("pilot", SeatRole.Driver)]
	[InlineData("gunner", SeatRole.Gunner)]
	[InlineData("passenger", SeatRole.Passenger)]
	public void ASeatIsNamedByItsRole(string name, SeatRole role)
	{
		Assert.True(AgentJson.TryParseSeatRole(name, out SeatRole read));
		Assert.Equal(role, read);
	}

	[Fact]
	public void ARoleNoSeatHasIsNone()
	{
		Assert.False(AgentJson.TryParseSeatRole("roof", out _));
		Assert.False(AgentJson.TryParseSeatRole(null, out _));
		Assert.Equal(3, AgentJson.SeatRoleNames.Length);
		Assert.Equal("passenger", AgentJson.SeatRoleNames[(int)SeatRole.Passenger]);
	}

	private static CrewVehicleRecord Truck()
	{
		var record = new CrewVehicleRecord
		{
			Slot = 2,
			Kind = CrewVehicleKind.FourByFour,
			Position = new Vector3(-60f, 1.2f, 50f),
			Velocity = new Vector3(3f, 0f, 4f),
			Yaw = -1.5f,
			Health = 412,
			GunRounds = 87,
			Seat0 = 7,
			Seat2 = 9,
		};
		return record;
	}

	[Fact]
	public void ListVehicles_WritesWhatTheRecordCarries_AndTheClientReadsItBack()
	{
		CrewSeat[] seats = CrewVehicleFiles.Seats(CrewVehicleFiles.FourByFourPath);
		var buffer = new ArrayBufferWriter<byte>();
		using (var writer = new Utf8JsonWriter(buffer))
		{
			writer.WriteStartObject();
			writer.WriteString("op", "vehicles");
			writer.WriteStartArray("vehicles");
			AgentJson.WriteVehicle(writer, Truck(), seats, fromKiosk: true);
			writer.WriteEndArray();
			writer.WriteStartArray("kiosk");
			AgentJson.WriteKioskLine(writer, 0, new KioskLine(KioskStatus.Out, 0));
			AgentJson.WriteKioskLine(writer, 1, new KioskLine(KioskStatus.Counting, 42));
			writer.WriteEndArray();
			writer.WriteEndObject();
		}

		using JsonDocument document = JsonDocument.Parse(buffer.WrittenMemory);
		(List<GdpyrVehicle> vehicles, List<GdpyrKioskLine> kiosk) = GdpyrConnection.ReadVehicles(document.RootElement);

		GdpyrVehicle truck = Assert.Single(vehicles);
		Assert.Equal(2, truck.Slot);
		Assert.Equal("4x4", truck.Type);
		Assert.Equal(-60f, truck.X);
		Assert.Equal(1.2f, truck.Y);
		Assert.Equal(50f, truck.Z);
		Assert.Equal(-1.5f, truck.Yaw);
		Assert.Equal(412f, truck.Health);
		Assert.Equal(5f, truck.Speed, 4);
		Assert.False(truck.Destroyed);
		Assert.False(truck.Airborne);
		Assert.True(truck.FromKiosk);
		Assert.Equal(new[] { "driver", "gunner", "passenger" }, truck.Roles);
		Assert.Equal(new[] { 7, 0, 9 }, truck.Peers);

		Assert.Equal(2, kiosk.Count);
		Assert.Equal("out", kiosk[0].Status);
		Assert.Equal("helicopter", kiosk[1].Type);
		Assert.Equal("counting", kiosk[1].Status);
		Assert.Equal(42, kiosk[1].SecondsLeft);
	}

	[Fact]
	public void AnAnswerWithNothingInIt_ReadsEmpty()
	{
		using JsonDocument document = JsonDocument.Parse("""{ "op": "vehicles" }""");
		(List<GdpyrVehicle> vehicles, List<GdpyrKioskLine> kiosk) = GdpyrConnection.ReadVehicles(document.RootElement);
		Assert.Empty(vehicles);
		Assert.Empty(kiosk);
	}

	// ---- the state hash ---------------------------------------------------------------------

	private static ulong Hash(in CrewVehicleRecord record)
	{
		AgentStateHash hash = AgentStateHash.Start();
		hash.Add(record);
		return hash.Value;
	}

	[Fact]
	public void TheStateHash_FoldsWhereAVehicleIsWhoSitsInItAndItsHealth()
	{
		CrewVehicleRecord truck = Truck();
		ulong baseline = Hash(truck);
		Assert.Equal(baseline, Hash(Truck()));

		CrewVehicleRecord moved = Truck();
		moved.Position.X += 0.002f;
		Assert.NotEqual(baseline, Hash(moved));

		CrewVehicleRecord gunned = Truck();
		gunned.Seat1 = 8;
		Assert.NotEqual(baseline, Hash(gunned));

		CrewVehicleRecord hurt = Truck();
		hurt.Health = 411;
		Assert.NotEqual(baseline, Hash(hurt));

		CrewVehicleRecord wrecked = Truck();
		wrecked.Destroyed = true;
		Assert.NotEqual(baseline, Hash(wrecked));

		CrewVehicleRecord tilted = Truck();
		tilted.Roll = 0.01f;
		Assert.NotEqual(baseline, Hash(tilted));
	}

	[Fact]
	public void TheStateHash_IgnoresWhatIsBelowItsQuanta_AndTheWiresAcknowledgement()
	{
		CrewVehicleRecord truck = Truck();
		ulong baseline = Hash(truck);

		CrewVehicleRecord nudged = Truck();
		nudged.Position.X += 0.0001f;
		Assert.Equal(baseline, Hash(nudged));

		CrewVehicleRecord acknowledged = Truck();
		acknowledged.DriverInputTick = 12345;
		Assert.Equal(baseline, Hash(acknowledged));
	}

	// ---- an absolute look on the wire ----------------------------------------------------------

	[Fact]
	public void AnAbsoluteLook_IsPackedWithoutTheDeltaFlag_AndTheServerReadsItAsTheLook()
	{
		var action = new ClientAction { DeltaYaw = 2.5f, DeltaPitch = -0.2f, LookIsAbsolute = true };
		Assert.True(AgentActionCodec.TryDecodeGround(action.Pack(3, 99), out int seat, out uint tick, out ServerAction decoded));
		Assert.Equal(3, seat);
		Assert.Equal(99u, tick);
		Assert.False(decoded.LookIsDelta);

		AgentActionCodec.Target(decoded, currentYaw: 0.4f, currentPitch: 0.1f, out float yaw, out float pitch);
		Assert.Equal(2.5f, yaw, 3);
		Assert.Equal(-0.2f, pitch, 3);

		// And a delta, the default, is still a delta.
		var nudge = new ClientAction { DeltaYaw = 0.1f };
		Assert.True(AgentActionCodec.TryDecodeGround(nudge.Pack(3, 99), out _, out _, out ServerAction read));
		Assert.True(read.LookIsDelta);
	}
}
