using System;
using System.Buffers;
using System.Text.Json;
using Gdpyr.Sim;
using Gdpyr.Sim.Agent;
using Godot;
using Xunit;

namespace Gdpyr.Tests;

/// <summary>
/// The ground-force observation (docs/AGENT_API.md §6.1).
///
/// The layout is self-describing — a client decodes by the schema <c>welcome</c>
/// publishes, not by this document — so the one failure mode worth ruling out is
/// the schema and the encoder drifting apart.
/// </summary>
public class AgentObservationTests
{
	private static float[] Encode(AgentRoundView round = default, AgentSelfView self = default,
		AgentWeaponView weapon = default, AgentContactView[] contacts = null, float[] rays = null,
		AgentObjectiveView objective = default)
	{
		var into = new float[AgentObservation.Floats];
		int written = AgentObservation.Encode(round, self, weapon,
			contacts ?? Array.Empty<AgentContactView>(), rays ?? Array.Empty<float>(), objective, into);

		Assert.Equal(AgentObservation.Floats, written);
		return into;
	}

	private static float At(float[] values, string field, int index = 0) =>
		values[AgentObservation.OffsetOf(field) + index];

	// ---- the layout --------------------------------------------------------

	[Fact]
	public void TheVectorIsTheBlocksTheDesignNames()
	{
		// 4 round + 20 self + 11 weapon + 88 contacts + 16 rays + 6 objective.
		// The design sketched a six-state movement one-hot; the shipped FSM has
		// seven, so the self block is 20 and the vector is 145 rather than 144.
		Assert.Equal(7, AgentObservation.MovementStates);
		Assert.Equal(145, AgentObservation.Floats);
		Assert.Equal(145 * 4, AgentObservation.Bytes);
		Assert.Equal(88, AgentObservation.MaxContacts * AgentObservation.ContactFloats);
	}

	[Fact]
	public void TheSchemaAccountsForEveryFloatExactlyOnce()
	{
		int at = 0;
		foreach (AgentField field in AgentObservation.Fields)
		{
			Assert.Equal(at, field.Offset);
			Assert.True(field.Count > 0, $"{field.Name} is empty");
			at += field.Count;
		}

		Assert.Equal(AgentObservation.Floats, at);
	}

	[Fact]
	public void TheSchemaWelcomePublishesMatchesTheEncoder()
	{
		var buffer = new ArrayBufferWriter<byte>(4096);
		using (var writer = new Utf8JsonWriter(buffer))
		{
			writer.WriteStartObject();
			AgentJson.WriteSchema(writer);
			writer.WriteEndObject();
		}

		using JsonDocument document = JsonDocument.Parse(buffer.WrittenMemory);
		JsonElement schema = document.RootElement.GetProperty("schema");

		Assert.Equal(AgentProtocol.SchemaVersion, schema.GetProperty("schema_version").GetString());

		JsonElement ground = schema.GetProperty("ground_observation");
		Assert.Equal(AgentObservation.Floats, ground.GetProperty("floats").GetInt32());
		Assert.Equal(AgentObservation.Bytes, ground.GetProperty("bytes").GetInt32());

		int at = 0;
		int fields = 0;
		foreach (JsonElement field in ground.GetProperty("fields").EnumerateArray())
		{
			Assert.Equal(at, field.GetProperty("offset").GetInt32());
			at += field.GetProperty("count").GetInt32();
			fields++;
		}

		Assert.Equal(AgentObservation.Floats, at);
		Assert.Equal(AgentObservation.Fields.Length, fields);
		Assert.Equal(AgentObservation.ContactFloats, AgentJson.ContactFields.Length);
	}

	[Fact]
	public void AnEncoderCursorThatOverranWouldBeCaughtHere()
	{
		// Every documented block starts where the table says it does.
		Assert.Equal(0, AgentObservation.OffsetOf("round.phase"));
		Assert.Equal(4, AgentObservation.OffsetOf("self.position"));
		Assert.Equal(24, AgentObservation.OffsetOf("weapon.slot"));
		Assert.Equal(35, AgentObservation.OffsetOf("contacts"));
		Assert.Equal(123, AgentObservation.OffsetOf("rays"));
		Assert.Equal(139, AgentObservation.OffsetOf("objective.bearing_sin_cos"));
		Assert.Equal(-1, AgentObservation.OffsetOf("nothing.at.all"));
	}

	[Fact]
	public void ABufferThatIsTooSmallIsRefusedRatherThanPartiallyWritten()
	{
		var into = new float[AgentObservation.Floats - 1];
		Assert.Equal(0, AgentObservation.Encode(default, default, default,
			Array.Empty<AgentContactView>(), Array.Empty<float>(), default, into));
	}

	// ---- the blocks --------------------------------------------------------

	[Fact]
	public void SecondsRemainingAndTicketsAreFractions()
	{
		float[] values = Encode(round: new AgentRoundView
		{
			Phase = 1,
			SecondsRemaining = 300f,
			SecondsTotal = 1200f,
			GroundTickets = 25,
			StartingGroundTickets = 50,
		});

		Assert.Equal(1f, At(values, "round.phase"));
		Assert.Equal(0.25f, At(values, "round.seconds_remaining"), 4);
		Assert.Equal(0.5f, At(values, "round.ticket_fraction"), 4);
	}

	[Fact]
	public void ARoundThatHasNotStartedDoesNotDivideByZero()
	{
		float[] values = Encode(round: new AgentRoundView { SecondsTotal = 0f, StartingGroundTickets = 0 });

		Assert.Equal(0f, At(values, "round.seconds_remaining"));
		Assert.Equal(0f, At(values, "round.ticket_fraction"));
	}

	[Fact]
	public void TheStepPhaseCountsThroughTheActionRepeat()
	{
		for (uint tick = 0; tick < 8; tick++)
		{
			float[] values = Encode(round: new AgentRoundView { Tick = tick, StepMul = 4 });
			Assert.Equal((tick % 4) / 4f, At(values, "round.step_phase"), 4);
		}
	}

	[Fact]
	public void TheMovementStateIsAOneHot()
	{
		for (byte state = 0; state < AgentObservation.MovementStates; state++)
		{
			float[] values = Encode(self: new AgentSelfView { MovementState = state });
			float sum = 0f;
			for (int i = 0; i < AgentObservation.MovementStates; i++)
			{
				sum += At(values, "self.movement_state", i);
			}

			Assert.Equal(1f, sum, 4);
			Assert.Equal(1f, At(values, "self.movement_state", state));
		}
	}

	[Fact]
	public void AStateIdOutsideTheOneHotLeavesItAllZero()
	{
		float[] values = Encode(self: new AgentSelfView { MovementState = 200 });
		for (int i = 0; i < AgentObservation.MovementStates; i++)
		{
			Assert.Equal(0f, At(values, "self.movement_state", i));
		}
	}

	[Fact]
	public void YawIsCarriedAsSinAndCos_SoTheWrapIsNotADiscontinuity()
	{
		float[] values = Encode(self: new AgentSelfView { Yaw = 0.0001f });
		float[] wrapped = Encode(self: new AgentSelfView { Yaw = Quantize.TwoPi - 0.0001f });

		Assert.Equal(At(values, "self.yaw_sin_cos", 1), At(wrapped, "self.yaw_sin_cos", 1), 3);
		Assert.Equal(-At(wrapped, "self.yaw_sin_cos"), At(values, "self.yaw_sin_cos"), 3);
	}

	[Fact]
	public void PositionAndVelocityAreNormalizedAndClamped()
	{
		float[] values = Encode(self: new AgentSelfView
		{
			Position = new Vector3(AgentObservation.PositionScaleMeters * 4f, 0f, -128f),
			Velocity = new Vector3(0f, -1000f, 0f),
		});

		Assert.Equal(1f, At(values, "self.position"), 4);
		Assert.Equal(-0.5f, At(values, "self.position", 2), 4);
		Assert.Equal(-1f, At(values, "self.velocity", 1), 4);
	}

	[Fact]
	public void HealthIsAFractionOfTheMaximum()
	{
		float[] values = Encode(self: new AgentSelfView { Health = SimConfig.MaxHealth / 2, Alive = true });

		Assert.Equal(0.5f, At(values, "self.health"), 4);
		Assert.Equal(1f, At(values, "self.alive"));
	}

	[Fact]
	public void AWeaponWithNoMagazineReadsFullRatherThanDividingByZero()
	{
		float[] values = Encode(weapon: new AgentWeaponView { MagazineSize = 0, UnlimitedAmmo = true });

		Assert.Equal(1f, At(values, "weapon.magazine"), 4);
		Assert.Equal(1f, At(values, "weapon.unlimited"));
		Assert.Equal(0f, At(values, "weapon.shots_since_reload"), 4);
	}

	[Fact]
	public void ShotsSinceReloadIsWhatIsMissingFromTheMagazine()
	{
		float[] values = Encode(weapon: new AgentWeaponView { Ammo = 6, MagazineSize = 30 });

		Assert.Equal(0.2f, At(values, "weapon.magazine"), 4);
		Assert.Equal(0.8f, At(values, "weapon.shots_since_reload"), 4);
	}

	[Fact]
	public void TheWeaponSlotIsAOneHotOverTheSlotsThatExist()
	{
		for (int slot = 0; slot < SimConfig.WeaponSlots; slot++)
		{
			float[] values = Encode(weapon: new AgentWeaponView { Slot = slot });
			Assert.Equal(1f, At(values, "weapon.slot", slot));
		}
	}

	// ---- contacts ----------------------------------------------------------

	[Fact]
	public void ContactsAreCarriedInOrderAndZeroPadded()
	{
		var contacts = new[]
		{
			new AgentContactView { IsPlayer = true, Hostile = true, DistanceMeters = 10f, HealthFraction = 1f },
			new AgentContactView { IsPlayer = false, Hostile = false, DistanceMeters = 20f },
		};

		float[] values = Encode(contacts: contacts);
		int at = AgentObservation.OffsetOf("contacts");

		Assert.Equal(1f, values[at + 0]);
		Assert.Equal(0f, values[at + 1]);
		Assert.Equal(1f, values[at + 2]);
		Assert.Equal(10f / AgentObservation.DistanceScaleMeters, values[at + 6], 4);

		int second = at + AgentObservation.ContactFloats;
		Assert.Equal(0f, values[second + 0]);
		Assert.Equal(1f, values[second + 1]);
		Assert.Equal(0f, values[second + 2]);

		// Everything past the second record is zero, so the tensor shape never
		// changes mid-episode.
		for (int i = at + (2 * AgentObservation.ContactFloats);
			i < at + (AgentObservation.MaxContacts * AgentObservation.ContactFloats); i++)
		{
			Assert.Equal(0f, values[i]);
		}
	}

	[Fact]
	public void MoreContactsThanFitAreTruncatedRatherThanOverrunning()
	{
		var contacts = new AgentContactView[AgentObservation.MaxContacts * 3];
		for (int i = 0; i < contacts.Length; i++)
		{
			contacts[i] = new AgentContactView { DistanceMeters = i };
		}

		float[] values = Encode(contacts: contacts);
		int last = AgentObservation.OffsetOf("contacts")
			+ ((AgentObservation.MaxContacts - 1) * AgentObservation.ContactFloats);

		Assert.Equal((AgentObservation.MaxContacts - 1) / AgentObservation.DistanceScaleMeters,
			values[last + 6], 4);
	}

	[Fact]
	public void RaysAreClampedIntoZeroToOneAndPaddedClear()
	{
		float[] values = Encode(rays: new[] { -1f, 0.5f, 7f });
		int at = AgentObservation.OffsetOf("rays");

		Assert.Equal(0f, values[at + 0]);
		Assert.Equal(0.5f, values[at + 1], 4);
		Assert.Equal(1f, values[at + 2]);
		Assert.Equal(0f, values[at + 3]);
	}

	[Fact]
	public void AnUnknownObjectiveIsZeroRatherThanAFalseBearing()
	{
		float[] values = Encode(objective: new AgentObjectiveView
		{
			Known = false,
			Bearing = 1f,
			DistanceMeters = 50f,
			GroundNodes = 2,
			StrategistNodes = 4,
			ContestedNodes = 1,
		});

		Assert.Equal(0f, At(values, "objective.bearing_sin_cos"));
		Assert.Equal(0f, At(values, "objective.bearing_sin_cos", 1));
		Assert.Equal(0f, At(values, "objective.distance"));

		// The node counts are the map's, not the objective's, and survive.
		Assert.Equal(2f / SimConfig.MaxResourceNodes, At(values, "objective.ground_nodes"), 4);
		Assert.Equal(4f / SimConfig.MaxResourceNodes, At(values, "objective.strategist_nodes"), 4);
		Assert.Equal(1f / SimConfig.MaxResourceNodes, At(values, "objective.contested_nodes"), 4);
	}

	[Fact]
	public void EveryFloatStaysInsideThePublishedBounds()
	{
		var contacts = new[]
		{
			new AgentContactView
			{
				IsPlayer = true,
				Hostile = true,
				Bearing = 9f,
				Elevation = 9f,
				DistanceMeters = 1e6f,
				ClosingSpeed = -1e6f,
				LateralSpeed = 1e6f,
				HealthFraction = 7f,
				TicksSinceSeen = 100_000,
			},
		};

		float[] values = Encode(
			round: new AgentRoundView { Phase = 2, SecondsRemaining = 1e6f, SecondsTotal = 1f },
			self: new AgentSelfView
			{
				Position = new Vector3(1e6f, -1e6f, 1e6f),
				Velocity = new Vector3(-1e6f, 1e6f, -1e6f),
				Pitch = 9f,
				MoveSpeedScale = 9f,
				Health = 1_000,
			},
			weapon: new AgentWeaponView { Ammo = 900, MagazineSize = 30, ReloadProgress = 9f },
			contacts: contacts,
			rays: new[] { -5f },
			objective: new AgentObjectiveView { Known = true, DistanceMeters = 1e6f, GroundNodes = 900 });

		foreach (AgentField field in AgentObservation.Fields)
		{
			for (int i = 0; i < field.Count; i++)
			{
				float value = values[field.Offset + i];
				Assert.True(float.IsFinite(value), $"{field.Name}[{i}] is not finite");
				Assert.InRange(value, field.Min, field.Max);
			}
		}
	}

	// ---- the strategist's vector (docs/AGENT_API.md §6.2) -----------------

	private static float[] EncodeStrategist(AgentStrategistRoundView round = default,
		AgentUnitView[] units = null, AgentBarracksView[] barracks = null, AgentNodeView[] nodes = null,
		AgentStrategistContactView[] contacts = null)
	{
		var into = new float[AgentStrategistObservation.Floats];
		int written = AgentStrategistObservation.Encode(round,
			units ?? Array.Empty<AgentUnitView>(),
			barracks ?? Array.Empty<AgentBarracksView>(),
			nodes ?? Array.Empty<AgentNodeView>(),
			contacts ?? Array.Empty<AgentStrategistContactView>(), into);

		Assert.Equal(AgentStrategistObservation.Floats, written);
		return into;
	}

	private static float StrategistAt(float[] values, string field, int index = 0) =>
		values[AgentStrategistObservation.OffsetOf(field) + index];

	[Fact]
	public void TheStrategistVectorIsTheBlocksTheDesignNames()
	{
		// 8 round + 64x14 units + 4x7 barracks + 8x8 nodes + 16x6 contacts.
		Assert.Equal(896, AgentStrategistObservation.MaxUnits * AgentStrategistObservation.UnitFloats);
		Assert.Equal(28, AgentStrategistObservation.MaxBarracks * AgentStrategistObservation.BarracksFloats);
		Assert.Equal(64, AgentStrategistObservation.MaxNodes * AgentStrategistObservation.NodeFloats);
		Assert.Equal(96, AgentStrategistObservation.MaxContacts * AgentStrategistObservation.ContactFloats);
		Assert.Equal(1092, AgentStrategistObservation.Floats);
		Assert.Equal(1092 * 4, AgentStrategistObservation.Bytes);

		// The unit block is the whole field cap and the contact block the whole
		// roster, so the tensor shape never changes mid-episode.
		Assert.Equal(SimConfig.MaxUnits, AgentStrategistObservation.MaxUnits);
		Assert.Equal(SnapshotCodec.MaxPlayers, AgentStrategistObservation.MaxContacts);
	}

	[Fact]
	public void TheStrategistSchemaAccountsForEveryFloatExactlyOnce()
	{
		int at = 0;
		foreach (AgentField field in AgentStrategistObservation.Fields)
		{
			Assert.Equal(at, field.Offset);
			Assert.True(field.Count > 0, $"{field.Name} is empty");
			at += field.Count;
		}

		Assert.Equal(AgentStrategistObservation.Floats, at);
		Assert.Equal(-1, AgentStrategistObservation.OffsetOf("nothing.at.all"));
	}

	[Fact]
	public void TheSchemaWelcomePublishesMatchesTheStrategistEncoder()
	{
		var buffer = new ArrayBufferWriter<byte>(16384);
		using (var writer = new Utf8JsonWriter(buffer))
		{
			writer.WriteStartObject();
			AgentJson.WriteSchema(writer);
			writer.WriteEndObject();
		}

		using JsonDocument document = JsonDocument.Parse(buffer.WrittenMemory);
		JsonElement schema = document.RootElement.GetProperty("schema");
		JsonElement strategist = schema.GetProperty("strategist_observation");

		Assert.Equal(AgentStrategistObservation.Floats, strategist.GetProperty("floats").GetInt32());
		Assert.Equal(AgentStrategistObservation.Bytes, strategist.GetProperty("bytes").GetInt32());

		int at = 0;
		int fields = 0;
		foreach (JsonElement field in strategist.GetProperty("fields").EnumerateArray())
		{
			Assert.Equal(at, field.GetProperty("offset").GetInt32());
			at += field.GetProperty("count").GetInt32();
			fields++;
		}

		Assert.Equal(AgentStrategistObservation.Floats, at);
		Assert.Equal(AgentStrategistObservation.Fields.Length, fields);

		// Every repeated block names each of its floats, so a client reads a record
		// by field rather than by counting.
		Assert.Equal(AgentStrategistObservation.UnitFloats, AgentJson.UnitFields.Length);
		Assert.Equal(AgentStrategistObservation.BarracksFloats, AgentJson.BarracksFields.Length);
		Assert.Equal(AgentStrategistObservation.NodeFloats, AgentJson.NodeFields.Length);
		Assert.Equal(AgentStrategistObservation.ContactFloats, AgentJson.StrategistContactFields.Length);
		Assert.Equal(AgentStrategistObservation.UnitFloats,
			strategist.GetProperty("units").GetProperty("floats_each").GetInt32());
	}

	[Fact]
	public void AStrategistBufferThatIsTooSmallIsRefusedRatherThanPartiallyWritten()
	{
		var into = new float[AgentStrategistObservation.Floats - 1];
		Assert.Equal(0, AgentStrategistObservation.Encode(default, Array.Empty<AgentUnitView>(),
			Array.Empty<AgentBarracksView>(), Array.Empty<AgentNodeView>(),
			Array.Empty<AgentStrategistContactView>(), into));
	}

	[Fact]
	public void AUnitCarriesItsTierAndItsOrderAsOneHots()
	{
		var units = new[]
		{
			new AgentUnitView
			{
				Alive = true,
				Tier = 2,
				X = 128f,
				Z = -128f,
				HealthFraction = 0.5f,
				Order = (byte)OrderKind.Attack,
				HasTarget = true,
				TargetDistanceMeters = 64f,
			},
		};

		float[] values = EncodeStrategist(units: units);
		int at = AgentStrategistObservation.OffsetOf("units");

		Assert.Equal(1f, values[at + 0]);
		Assert.Equal(0f, values[at + 1]);
		Assert.Equal(0f, values[at + 2]);
		Assert.Equal(1f, values[at + 3]);
		Assert.Equal(0.5f, values[at + 4], 4);
		Assert.Equal(-0.5f, values[at + 5], 4);
		Assert.Equal(0.5f, values[at + 6], 4);

		// none, move, attack, patrol, defend — attack is the third.
		Assert.Equal(0f, values[at + 7]);
		Assert.Equal(0f, values[at + 8]);
		Assert.Equal(1f, values[at + 9]);
		Assert.Equal(1f, values[at + 12]);
		Assert.Equal(0.5f, values[at + 13], 4);
	}

	[Fact]
	public void StopIsNeverStoredAndFallsOutsideTheOrderOneHot()
	{
		// UnitOrder says so: a unit told to stop ends up holding None. An order byte
		// past the one-hot leaves it all zero rather than writing into the next unit.
		var units = new[] { new AgentUnitView { Order = (byte)OrderKind.Stop } };
		float[] values = EncodeStrategist(units: units);
		int at = AgentStrategistObservation.OffsetOf("units");

		for (int i = 0; i < AgentStrategistObservation.OrderKinds; i++)
		{
			Assert.Equal(0f, values[at + 7 + i]);
		}
	}

	[Fact]
	public void MoreUnitsThanFitAreTruncatedRatherThanOverrunning()
	{
		var units = new AgentUnitView[AgentStrategistObservation.MaxUnits * 2];
		for (int i = 0; i < units.Length; i++)
		{
			units[i] = new AgentUnitView { Alive = true };
		}

		float[] values = EncodeStrategist(units: units);
		int at = AgentStrategistObservation.OffsetOf("units");
		int last = at + ((AgentStrategistObservation.MaxUnits - 1) * AgentStrategistObservation.UnitFloats);

		Assert.Equal(1f, values[last]);
		Assert.Equal(AgentStrategistObservation.OffsetOf("barracks"),
			at + (AgentStrategistObservation.MaxUnits * AgentStrategistObservation.UnitFloats));
	}

	[Fact]
	public void ABarracksCarriesItsQueueAndItsRally()
	{
		var barracks = new[]
		{
			new AgentBarracksView
			{
				X = -256f,
				Z = 256f,
				QueueDepth = SimConfig.MaxBuildQueue,
				HeadTier = 1,
				HeadProgress = 0.25f,
				RallyX = 64f,
				RallyZ = -64f,
			},
		};

		float[] values = EncodeStrategist(barracks: barracks);
		int at = AgentStrategistObservation.OffsetOf("barracks");

		Assert.Equal(-1f, values[at + 0], 4);
		Assert.Equal(1f, values[at + 1], 4);
		Assert.Equal(1f, values[at + 2], 4);
		Assert.Equal(0.5f, values[at + 3], 4);
		Assert.Equal(0.25f, values[at + 4], 4);
		Assert.Equal(0.25f, values[at + 5], 4);
		Assert.Equal(-0.25f, values[at + 6], 4);
	}

	[Fact]
	public void ANodeCarriesWhoHoldsItAsAOneHot()
	{
		var nodes = new[]
		{
			new AgentNodeView
			{
				Owner = (byte)NodeHolder.Strategist,
				Contested = true,
				CaptureProgress = 0.5f,
				IncomePaid = 400,
			},
		};

		float[] values = EncodeStrategist(nodes: nodes);
		int at = AgentStrategistObservation.OffsetOf("nodes");

		Assert.Equal(0f, values[at + 2]);
		Assert.Equal(0f, values[at + 3]);
		Assert.Equal(1f, values[at + 4]);
		Assert.Equal(1f, values[at + 5]);
		Assert.Equal(0.5f, values[at + 6], 4);
		Assert.Equal(400f / AgentStrategistObservation.PointsScale, values[at + 7], 4);
	}

	// ---- fog parity (docs/AGENT_API.md §6.2) ------------------------------

	/// <summary>
	/// The engine half of the strategist view reads <c>VisibilityService</c>: a peer
	/// is carried when <c>TryContact</c> has a record for it — the same answer
	/// <c>IsVisibleTo</c> writes into a human strategist's snapshot, and the same one
	/// their HUD draws a ghost from. What is engine-free, and therefore checked here,
	/// is the rule that turns those answers into the vector: a peer nobody has ever
	/// seen is not in it, one that was seen and lost is a ghost with its age, and
	/// live contacts sort ahead of ghosts.
	/// </summary>
	private static AgentStrategistContactView Contact(bool known, bool visible, int age = 0,
		float x = 0f, Team team = Team.GroundForce) =>
		new()
		{
			Known = known,
			Visible = visible,
			TicksSinceSeen = age,
			X = x,
			IsPlayer = true,
			Team = (byte)team,
		};

	[Fact]
	public void APeerNobodyHasEverSeenIsNotInTheVector()
	{
		var candidates = new[]
		{
			Contact(known: false, visible: false, x: 10f),
			Contact(known: true, visible: true, x: 20f),
		};

		var selected = new AgentStrategistContactView[AgentStrategistObservation.MaxContacts];
		Assert.Equal(1, AgentStrategistObservation.SelectContacts(candidates, selected));
		Assert.Equal(20f, selected[0].X);

		// And the encoder agrees, in case a caller hands it a raw list.
		float[] values = EncodeStrategist(contacts: candidates);
		int at = AgentStrategistObservation.OffsetOf("contacts");
		Assert.Equal(20f / AgentStrategistObservation.PositionScaleMeters, values[at], 4);
		Assert.Equal(1f, values[at + 2]);
	}

	[Fact]
	public void ALostContactIsAGhostWithItsAgeRatherThanADisappearance()
	{
		var candidates = new[] { Contact(known: true, visible: false, age: SimConfig.TickRate * 4, x: 30f) };
		float[] values = EncodeStrategist(contacts: candidates);
		int at = AgentStrategistObservation.OffsetOf("contacts");

		Assert.Equal(30f / AgentStrategistObservation.PositionScaleMeters, values[at], 4);
		Assert.Equal(0f, values[at + 2]);
		Assert.Equal(SimConfig.TickRate * 4f / AgentStrategistObservation.GhostAgeScaleTicks,
			values[at + 3], 4);
	}

	[Fact]
	public void LiveContactsSortAheadOfGhostsAndGhostsSortByAge()
	{
		var candidates = new[]
		{
			Contact(known: true, visible: false, age: 600, x: 1f),
			Contact(known: true, visible: false, age: 60, x: 2f),
			Contact(known: true, visible: true, x: 3f),
		};

		var selected = new AgentStrategistContactView[AgentStrategistObservation.MaxContacts];
		Assert.Equal(3, AgentStrategistObservation.SelectContacts(candidates, selected));

		Assert.Equal(3f, selected[0].X);
		Assert.Equal(2f, selected[1].X);
		Assert.Equal(1f, selected[2].X);
	}

	[Fact]
	public void TheSidesOwnPeersAreCarriedAndFlagged()
	{
		var candidates = new[]
		{
			Contact(known: true, visible: true, x: 5f, team: Team.Strategist),
			Contact(known: true, visible: true, x: 6f),
		};

		float[] values = EncodeStrategist(contacts: candidates);
		int at = AgentStrategistObservation.OffsetOf("contacts");

		Assert.Equal(1f, values[at + 5]);
		Assert.Equal(0f, values[at + AgentStrategistObservation.ContactFloats + 5]);
	}

	[Fact]
	public void MoreContactsThanTheRosterHoldsAreTruncated()
	{
		var candidates = new AgentStrategistContactView[AgentStrategistObservation.MaxContacts * 2];
		for (int i = 0; i < candidates.Length; i++)
		{
			candidates[i] = Contact(known: true, visible: true, x: i);
		}

		var selected = new AgentStrategistContactView[AgentStrategistObservation.MaxContacts];
		Assert.Equal(AgentStrategistObservation.MaxContacts,
			AgentStrategistObservation.SelectContacts(candidates, selected));
	}

	[Fact]
	public void EveryStrategistFloatStaysInsideThePublishedBounds()
	{
		var units = new[]
		{
			new AgentUnitView
			{
				Alive = true,
				Tier = 200,
				X = 1e6f,
				Z = -1e6f,
				HealthFraction = 9f,
				Order = 200,
				TargetDistanceMeters = 1e6f,
			},
		};

		var barracks = new[]
		{
			new AgentBarracksView
			{
				X = -1e6f,
				Z = 1e6f,
				QueueDepth = 10_000,
				HeadTier = 200,
				HeadProgress = 9f,
				RallyX = 1e6f,
				RallyZ = -1e6f,
			},
		};

		var nodes = new[]
		{
			new AgentNodeView { Owner = 200, CaptureProgress = 9f, IncomePaid = 1_000_000, X = 1e6f },
		};

		var contacts = new[] { Contact(known: true, visible: true, age: 1_000_000, x: 1e6f) };

		float[] values = EncodeStrategist(
			round: new AgentStrategistRoundView
			{
				Phase = 3,
				SecondsRemaining = 1e6f,
				SecondsTotal = 1f,
				Points = 1_000_000,
				IncomePaid = 1_000_000,
				GroundTickets = 500,
				StartingGroundTickets = 50,
				LiveUnits = 10_000,
				QueuedUnits = 10_000,
				UnitsLost = 1_000_000,
			},
			units: units, barracks: barracks, nodes: nodes, contacts: contacts);

		foreach (AgentField field in AgentStrategistObservation.Fields)
		{
			for (int i = 0; i < field.Count; i++)
			{
				float value = values[field.Offset + i];
				Assert.True(float.IsFinite(value), $"{field.Name}[{i}] is not finite");
				Assert.InRange(value, field.Min, field.Max);
			}
		}
	}

	[Fact]
	public void AStrategistObservationReadsBackByNameToo()
	{
		float[] values = EncodeStrategist(round: new AgentStrategistRoundView
		{
			Points = 2000,
			LiveUnits = 32,
		});

		var buffer = new ArrayBufferWriter<byte>(65536);
		using (var writer = new Utf8JsonWriter(buffer))
		{
			writer.WriteStartObject();
			AgentJson.WriteObservation(writer, AgentStrategistObservation.Fields, values);
			writer.WriteEndObject();
		}

		using JsonDocument document = JsonDocument.Parse(buffer.WrittenMemory);
		JsonElement observation = document.RootElement.GetProperty("observation");

		Assert.Equal(0.5f, observation.GetProperty("round.points").GetSingle(), 4);
		Assert.Equal(0.5f, observation.GetProperty("round.live_units").GetSingle(), 4);
		Assert.Equal(AgentStrategistObservation.MaxUnits * AgentStrategistObservation.UnitFloats,
			observation.GetProperty("units").GetArrayLength());
	}

	// ---- the optional feature planes (docs/AGENT_API.md §6.3) -------------

	[Fact]
	public void TheGridIsThirtyTwoSquareByFourChannels()
	{
		Assert.Equal(32, AgentFeaturePlanes.Size);
		Assert.Equal(4, AgentFeaturePlanes.Channels);
		Assert.Equal(4096, AgentFeaturePlanes.Floats);
		Assert.Equal(16384, AgentFeaturePlanes.Bytes);
		Assert.Equal(AgentFeaturePlanes.Channels, AgentFeaturePlanes.ChannelNames.Length);
	}

	[Fact]
	public void TheGridCoversTheMapAndRefusesWhatIsOffIt()
	{
		Assert.True(AgentFeaturePlanes.TryCell(0f, 0f, out int row, out int column));
		Assert.Equal(AgentFeaturePlanes.Size / 2, row);
		Assert.Equal(AgentFeaturePlanes.Size / 2, column);

		// A unit that has fallen off the map is not standing on the edge of it.
		Assert.False(AgentFeaturePlanes.TryCell(AgentFeaturePlanes.HalfExtentMeters * 2f, 0f, out _, out _));
		Assert.False(AgentFeaturePlanes.TryCell(0f, -AgentFeaturePlanes.HalfExtentMeters * 2f, out _, out _));
	}

	[Fact]
	public void ScatterSaturatesRatherThanRunningAway()
	{
		var grid = new float[AgentFeaturePlanes.Floats];
		for (int i = 0; i < 100; i++)
		{
			AgentFeaturePlanes.Scatter(grid, AgentFeaturePlanes.OwnUnits, 0f, 0f);
		}

		AgentFeaturePlanes.TryCell(0f, 0f, out int row, out int column);
		Assert.Equal(1f, grid[AgentFeaturePlanes.Index(row, column, AgentFeaturePlanes.OwnUnits)]);
	}

	[Fact]
	public void ClearingTheGridKeepsThePassabilityTheMapProbeWrote()
	{
		var grid = new float[AgentFeaturePlanes.Floats];
		AgentFeaturePlanes.Set(grid, AgentFeaturePlanes.Passability, 0f, 0f, 1f);
		AgentFeaturePlanes.Scatter(grid, AgentFeaturePlanes.OwnUnits, 0f, 0f);
		AgentFeaturePlanes.ClearDynamic(grid);

		AgentFeaturePlanes.TryCell(0f, 0f, out int row, out int column);
		Assert.Equal(1f, grid[AgentFeaturePlanes.Index(row, column, AgentFeaturePlanes.Passability)]);
		Assert.Equal(0f, grid[AgentFeaturePlanes.Index(row, column, AgentFeaturePlanes.OwnUnits)]);
	}

	[Fact]
	public void NodeOwnershipIsOneOrderedChannelRatherThanThreeSparseOnes()
	{
		Assert.Equal(0f, AgentFeaturePlanes.OwnershipValue((byte)NodeHolder.Neutral));
		Assert.Equal(0.5f, AgentFeaturePlanes.OwnershipValue((byte)NodeHolder.GroundForce));
		Assert.Equal(1f, AgentFeaturePlanes.OwnershipValue((byte)NodeHolder.Strategist));
	}

	[Fact]
	public void JsonObservationsReadBackByName()
	{
		float[] values = Encode(self: new AgentSelfView { Health = SimConfig.MaxHealth, Alive = true });

		var buffer = new ArrayBufferWriter<byte>(8192);
		using (var writer = new Utf8JsonWriter(buffer))
		{
			writer.WriteStartObject();
			AgentJson.WriteObservation(writer, values);
			writer.WriteEndObject();
		}

		using JsonDocument document = JsonDocument.Parse(buffer.WrittenMemory);
		JsonElement observation = document.RootElement.GetProperty("observation");

		// This is the shape a playtest assertion reads: observation.self.health.
		Assert.Equal(1f, observation.GetProperty("self.health").GetSingle(), 4);
		Assert.Equal(AgentObservation.MovementStates,
			observation.GetProperty("self.movement_state").GetArrayLength());
	}
}
