using System;
using System.Buffers;
using System.Text.Json;

namespace Gdpyr.Sim.Agent;

/// <summary>
/// The JSON half of the control plane (docs/AGENT_API.md §4).
///
/// Written with <see cref="Utf8JsonWriter"/> rather than a serializer: nothing
/// here reflects over a type, so the same code runs in a trimmed export and in
/// the test project, and the bytes a client decodes are the bytes this file
/// spells out.
///
/// The observation schema is **fetched, not compiled**. There is no protobuf
/// toolchain, no code generation, and no version skew that fails silently: a
/// client that does not recognise <see cref="AgentProtocol.SchemaVersion"/>
/// refuses to decode rather than misreading a float.
/// </summary>
public static class AgentJson
{
	/// <summary>
	/// Writes the observation layout: ordered field names, shapes and
	/// normalization bounds, plus the button vocabulary and the event field names.
	/// </summary>
	public static void WriteSchema(Utf8JsonWriter writer)
	{
		writer.WriteStartObject("schema");
		writer.WriteString("schema_version", AgentProtocol.SchemaVersion);
		writer.WriteNumber("protocol_version", AgentProtocol.Version);

		writer.WriteStartObject("ground_observation");
		writer.WriteNumber("floats", AgentObservation.Floats);
		writer.WriteNumber("bytes", AgentObservation.Bytes);
		writer.WriteString("dtype", "float32");
		writer.WriteStartArray("fields");
		foreach (AgentField field in AgentObservation.Fields)
		{
			writer.WriteStartObject();
			writer.WriteString("name", field.Name);
			writer.WriteNumber("offset", field.Offset);
			writer.WriteNumber("count", field.Count);
			writer.WriteNumber("min", field.Min);
			writer.WriteNumber("max", field.Max);
			writer.WriteEndObject();
		}
		writer.WriteEndArray();

		writer.WriteStartObject("contacts");
		writer.WriteNumber("max", AgentObservation.MaxContacts);
		writer.WriteNumber("floats_each", AgentObservation.ContactFloats);
		writer.WriteStartArray("fields");
		foreach (string name in ContactFields)
		{
			writer.WriteStringValue(name);
		}
		writer.WriteEndArray();
		writer.WriteEndObject();

		writer.WriteStartObject("rays");
		writer.WriteNumber("count", AgentObservation.Rays);
		writer.WriteNumber("half_angle_radians", AgentObservation.RayFanHalfAngleRadians);
		writer.WriteNumber("range_meters", AgentObservation.RayRangeMeters);
		writer.WriteEndObject();

		writer.WriteStartObject("scales");
		writer.WriteNumber("position_meters", AgentObservation.PositionScaleMeters);
		writer.WriteNumber("velocity_meters_per_second", AgentObservation.VelocityScaleMetersPerSecond);
		writer.WriteNumber("distance_meters", AgentObservation.DistanceScaleMeters);
		writer.WriteNumber("closing_speed_meters_per_second", AgentObservation.ClosingSpeedScaleMetersPerSecond);
		writer.WriteEndObject();
		writer.WriteEndObject();

		writer.WriteStartObject("ground_action");
		writer.WriteNumber("bytes", AgentActionCodec.GroundSizeBytes);
		writer.WriteStartArray("buttons");
		foreach (string name in AgentActionCodec.ButtonNames)
		{
			writer.WriteStringValue(name);
		}
		writer.WriteEndArray();
		writer.WriteEndObject();

		writer.WriteStartObject("events");
		for (byte kind = 0; kind <= (byte)AgentEventKind.SeatReleased; kind++)
		{
			var value = (AgentEventKind)kind;
			writer.WriteStartArray(AgentEventSchema.NameOf(value));
			foreach (string name in AgentEventSchema.FieldsOf(value))
			{
				if (name.Length > 0)
				{
					writer.WriteStringValue(name);
				}
			}
			writer.WriteEndArray();
		}
		writer.WriteEndObject();

		writer.WriteEndObject();
	}

	/// <summary>The per-contact field names, in the order the encoder writes them.</summary>
	public static readonly string[] ContactFields =
	{
		"is_player", "is_unit", "hostile", "bearing_sin", "bearing_cos", "elevation", "distance",
		"closing_speed", "lateral_speed", "health", "ticks_since_seen",
	};

	/// <summary>Writes one event as the object docs/AGENT_API.md §8 shows.</summary>
	public static void WriteEvent(Utf8JsonWriter writer, in AgentEvent record)
	{
		writer.WriteStartObject();
		writer.WriteString("kind", AgentEventSchema.NameOf(record.Kind));
		writer.WriteNumber("tick", record.Tick);

		ReadOnlySpan<string> fields = AgentEventSchema.FieldsOf(record.Kind);
		if (fields.Length == 5)
		{
			WriteSlot(writer, fields[0], record.A);
			WriteSlot(writer, fields[1], record.B);
			WriteSlot(writer, fields[2], record.C);
			WriteSlot(writer, fields[3], record.X);
			WriteSlot(writer, fields[4], record.Y);
		}

		writer.WriteEndObject();
	}

	/// <summary>
	/// Writes an observation as named fields rather than as an offset into a float
	/// array — what makes a playtest assertion read
	/// <c>observation.self.health</c> (docs/AGENT_API.md §9.2).
	/// </summary>
	public static void WriteObservation(Utf8JsonWriter writer, ReadOnlySpan<float> values)
	{
		writer.WriteStartObject("observation");
		foreach (AgentField field in AgentObservation.Fields)
		{
			if (field.Offset + field.Count > values.Length)
			{
				break;
			}

			if (field.Count == 1)
			{
				writer.WriteNumber(field.Name, values[field.Offset]);
				continue;
			}

			writer.WriteStartArray(field.Name);
			for (int i = 0; i < field.Count; i++)
			{
				writer.WriteNumberValue(values[field.Offset + i]);
			}
			writer.WriteEndArray();
		}
		writer.WriteEndObject();
	}

	/// <summary>An error frame's body: a machine-readable code and a sentence for a person.</summary>
	public static byte[] Error(string code, string message)
	{
		var buffer = new ArrayBufferWriter<byte>(128);
		using (var writer = new Utf8JsonWriter(buffer))
		{
			writer.WriteStartObject();
			writer.WriteString("op", "error");
			writer.WriteString("code", code);
			writer.WriteString("message", message);
			writer.WriteEndObject();
		}

		return buffer.WrittenSpan.ToArray();
	}

	private static void WriteSlot(Utf8JsonWriter writer, string name, int value)
	{
		if (name.Length > 0)
		{
			writer.WriteNumber(name, value);
		}
	}

	private static void WriteSlot(Utf8JsonWriter writer, string name, float value)
	{
		if (name.Length > 0)
		{
			writer.WriteNumber(name, value);
		}
	}
}
