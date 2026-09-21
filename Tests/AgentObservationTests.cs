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
