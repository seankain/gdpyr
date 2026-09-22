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
		WriteFields(writer, AgentObservation.Fields);

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

		// ---- the strategist's vector (docs/AGENT_API.md §6.2) ----------------
		writer.WriteStartObject("strategist_observation");
		writer.WriteNumber("floats", AgentStrategistObservation.Floats);
		writer.WriteNumber("bytes", AgentStrategistObservation.Bytes);
		writer.WriteString("dtype", "float32");
		WriteFields(writer, AgentStrategistObservation.Fields);

		WriteBlock(writer, "units", AgentStrategistObservation.MaxUnits,
			AgentStrategistObservation.UnitFloats, UnitFields);
		WriteBlock(writer, "barracks", AgentStrategistObservation.MaxBarracks,
			AgentStrategistObservation.BarracksFloats, BarracksFields);
		WriteBlock(writer, "nodes", AgentStrategistObservation.MaxNodes,
			AgentStrategistObservation.NodeFloats, NodeFields);
		WriteBlock(writer, "contacts", AgentStrategistObservation.MaxContacts,
			AgentStrategistObservation.ContactFloats, StrategistContactFields);

		writer.WriteStartObject("scales");
		writer.WriteNumber("position_meters", AgentStrategistObservation.PositionScaleMeters);
		writer.WriteNumber("distance_meters", AgentStrategistObservation.DistanceScaleMeters);
		writer.WriteNumber("points", AgentStrategistObservation.PointsScale);
		writer.WriteNumber("units_lost", AgentStrategistObservation.UnitsLostScale);
		writer.WriteNumber("ghost_age_ticks", AgentStrategistObservation.GhostAgeScaleTicks);
		writer.WriteEndObject();
		writer.WriteEndObject();

		// ---- the optional planes (docs/AGENT_API.md §6.3) --------------------
		writer.WriteStartObject("feature_planes");
		writer.WriteNumber("size", AgentFeaturePlanes.Size);
		writer.WriteNumber("channels", AgentFeaturePlanes.Channels);
		writer.WriteNumber("floats", AgentFeaturePlanes.Floats);
		writer.WriteNumber("bytes", AgentFeaturePlanes.Bytes);
		writer.WriteString("layout", "row_major_channel_last");
		writer.WriteNumber("half_extent_meters", AgentFeaturePlanes.HalfExtentMeters);
		writer.WriteString("policy", "strategist");
		writer.WriteStartArray("channel_names");
		foreach (string name in AgentFeaturePlanes.ChannelNames)
		{
			writer.WriteStringValue(name);
		}
		writer.WriteEndArray();
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

		writer.WriteStartObject("strategist_action");
		writer.WriteStartArray("commands");
		foreach (string name in CommandNames)
		{
			writer.WriteStringValue(name);
		}
		writer.WriteEndArray();
		writer.WriteStartArray("orders");
		foreach (string name in OrderNames)
		{
			writer.WriteStringValue(name);
		}
		writer.WriteEndArray();
		writer.WriteStartArray("structures");
		foreach (string name in StructureNames)
		{
			writer.WriteStringValue(name);
		}
		writer.WriteEndArray();
		writer.WriteNumber("max_commands", AgentCommandList.MaxCommands);
		writer.WriteEndObject();

		writer.WriteStartObject("events");
		for (byte kind = 0; kind <= (byte)AgentEventSchema.Last; kind++)
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

	/// <summary>Per-unit field names in a strategist observation (docs/AGENT_API.md §6.2).</summary>
	public static readonly string[] UnitFields =
	{
		"alive", "tier_infantry", "tier_technical", "tier_tank", "x", "z", "health",
		"order_none", "order_move", "order_attack", "order_patrol", "order_defend",
		"has_target", "target_distance",
	};

	public static readonly string[] BarracksFields =
	{
		"x", "z", "queue_depth", "head_tier", "head_progress", "rally_x", "rally_z",
	};

	public static readonly string[] NodeFields =
	{
		"x", "z", "owner_neutral", "owner_ground", "owner_strategist", "contested", "capture_progress",
		"income_paid",
	};

	public static readonly string[] StrategistContactFields =
	{
		"x", "z", "visible", "ticks_since_seen", "is_player", "is_strategist",
	};

	/// <summary>Every command a strategist seat may name (docs/AGENT_API.md §7.4).</summary>
	public static readonly string[] CommandNames = { "order", "build", "cancel", "rally", "noop", "construct" };

	/// <summary>
	/// The structures a <c>construct</c> command may name, in structure-catalog order
	/// (docs/NETCODE.md §10.5). A command may also name one by its index.
	/// </summary>
	public static readonly string[] StructureNames = { "pillbox", "sandbag_wall", "sniper_tower" };

	/// <summary>Every order kind a command may name. <c>stop</c> is a command and never a stored state.</summary>
	public static readonly string[] OrderNames = { "move", "attack", "patrol", "defend", "stop" };

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
	public static void WriteObservation(Utf8JsonWriter writer, ReadOnlySpan<float> values) =>
		WriteObservation(writer, AgentObservation.Fields, values);

	/// <summary>Writes an observation in whichever layout its seat's policy kind uses.</summary>
	public static void WriteObservation(Utf8JsonWriter writer, ReadOnlySpan<AgentField> fields,
		ReadOnlySpan<float> values)
	{
		writer.WriteStartObject("observation");
		foreach (AgentField field in fields)
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

	/// <summary>
	/// Writes the feature planes as a flat array in the layout the schema publishes
	/// — row major, channel last (docs/AGENT_API.md §6.3).
	///
	/// Flat rather than nested: a JSON observation is what a coding agent reads by
	/// name, and nobody writes a playtest assertion against cell [17][3][1]. A
	/// policy that wants the grid takes the binary lane.
	/// </summary>
	public static void WritePlanes(Utf8JsonWriter writer, ReadOnlySpan<float> grid)
	{
		writer.WriteStartArray("planes");
		for (int i = 0; i < grid.Length; i++)
		{
			writer.WriteNumberValue(grid[i]);
		}

		writer.WriteEndArray();
	}

	private static void WriteFields(Utf8JsonWriter writer, ReadOnlySpan<AgentField> fields)
	{
		writer.WriteStartArray("fields");
		foreach (AgentField field in fields)
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
	}

	/// <summary>One repeated block of a vector: how many records, how wide, and what each float is.</summary>
	private static void WriteBlock(Utf8JsonWriter writer, string name, int max, int floatsEach,
		string[] fields)
	{
		writer.WriteStartObject(name);
		writer.WriteNumber("max", max);
		writer.WriteNumber("floats_each", floatsEach);
		writer.WriteStartArray("fields");
		foreach (string field in fields)
		{
			writer.WriteStringValue(field);
		}

		writer.WriteEndArray();
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
