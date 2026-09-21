using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Gdpyr.AgentClient;

/// <summary>What a seat's observation carries, decoded by the schema the server published.</summary>
public sealed class GdpyrObservation
{
	public int Seat;
	public uint Tick;
	public bool Omniscient;
	public bool Unbounded;
	public float[] Values = Array.Empty<float>();

	/// <summary>A named run of the vector, by the schema's field name.</summary>
	public ReadOnlySpan<float> Field(GdpyrSchema schema, string name)
	{
		(int offset, int count) = schema.Field(name);
		return offset < 0 ? ReadOnlySpan<float>.Empty : Values.AsSpan(offset, count);
	}

	public float Scalar(GdpyrSchema schema, string name)
	{
		ReadOnlySpan<float> run = Field(schema, name);
		return run.Length > 0 ? run[0] : 0f;
	}
}

/// <summary>One record off the event stream (docs/AGENT_API.md §8).</summary>
public sealed class GdpyrEvent
{
	public string Kind = string.Empty;
	public uint Tick;
	public readonly Dictionary<string, double> Fields = new();

	public double Get(string name) => Fields.TryGetValue(name, out double value) ? value : 0d;

	public override string ToString() => $"{Kind}@{Tick}";
}

/// <summary>
/// The observation layout, as <c>welcome</c> published it.
///
/// Fetched rather than compiled (docs/AGENT_API.md §4): the client is told the
/// field names, offsets, shapes and bounds at the handshake, so adding a float to
/// the observation is a server change and not a coordinated release.
/// </summary>
public sealed class GdpyrSchema
{
	private readonly Dictionary<string, (int Offset, int Count)> _fields = new();

	public string Version = string.Empty;
	public int Floats;
	public int Bytes;
	public int TickRate = 60;

	public IReadOnlyCollection<string> Names => _fields.Keys;

	public (int Offset, int Count) Field(string name) =>
		_fields.TryGetValue(name, out (int, int) found) ? found : (-1, 0);

	internal void Add(string name, int offset, int count) => _fields[name] = (offset, count);
}

/// <summary>
/// A connection to a gdpyr server's agent channel (docs/AGENT_API.md).
///
/// Small on purpose. The protocol is length-prefixed frames with a JSON control
/// plane and packed float32 observations, and it is meant to be usable without a
/// client library at all; this one exists so that a .NET trainer or a scripted
/// playtest does not have to write the same eighty lines again.
///
/// Not thread-safe: one connection belongs to one loop.
/// </summary>
public sealed class GdpyrConnection : IDisposable
{
	private readonly TcpClient _client;
	private readonly NetworkStream _stream;
	private readonly Queue<GdpyrObservation> _observations = new();
	private readonly Queue<GdpyrEvent> _events = new();

	/// <summary>Pushed observations held before the oldest is dropped.</summary>
	private const int MaxQueuedObservations = 64;

	/// <summary>Events held before the oldest is dropped. Two full rings of the server's own log.</summary>
	private const int MaxQueuedEvents = 4096;

	private byte[] _buffer = new byte[1 << 16];
	private int _length;
	private uint _correlation = 1;

	private GdpyrConnection(TcpClient client)
	{
		_client = client;
		_client.NoDelay = true;
		_stream = client.GetStream();
	}

	public GdpyrSchema Schema { get; private set; } = new();

	/// <summary>True when the server was started with <c>--agent-omniscient</c>. A result obtained under it is not a result about this game.</summary>
	public bool Omniscient { get; private set; }

	/// <summary>True when the server was started with <c>--agent-unbounded</c>.</summary>
	public bool Unbounded { get; private set; }

	/// <summary>The turn-rate ceiling an attached seat plays under [rad/s]. 0 when unbounded.</summary>
	public float TurnRateRadians { get; private set; }

	/// <summary>Ticks a seat may go without an action before its bot takes over.</summary>
	public int GraceTicks { get; private set; }

	public uint Tick { get; private set; }

	/// <summary>Connects and completes the handshake.</summary>
	public static async Task<GdpyrConnection> ConnectAsync(string host = "127.0.0.1", int port = 7900,
		string token = null, CancellationToken cancel = default)
	{
		var client = new TcpClient();
		await client.ConnectAsync(host, port, cancel).ConfigureAwait(false);

		var connection = new GdpyrConnection(client);
		await connection.HelloAsync(token, cancel).ConfigureAwait(false);
		return connection;
	}

	private async Task HelloAsync(string token, CancellationToken cancel)
	{
		JsonElement welcome = await RequestAsync(writer =>
		{
			writer.WriteString("op", "hello");
			writer.WriteNumber("protocol", AgentProtocol.Version);
			writer.WriteString("observations", "binary");
			if (token != null)
			{
				writer.WriteString("token", token);
			}
		}, cancel).ConfigureAwait(false);

		Omniscient = welcome.TryGetProperty("omniscient", out JsonElement omniscient) && omniscient.GetBoolean();
		Unbounded = welcome.TryGetProperty("unbounded", out JsonElement unbounded) && unbounded.GetBoolean();
		TurnRateRadians = welcome.TryGetProperty("turn_rate_radians", out JsonElement turn)
			? turn.GetSingle()
			: 0f;
		GraceTicks = welcome.TryGetProperty("grace_ticks", out JsonElement grace) ? grace.GetInt32() : 30;
		Tick = welcome.TryGetProperty("tick", out JsonElement tick) ? tick.GetUInt32() : 0;

		Schema = ReadSchema(welcome);
	}

	private static GdpyrSchema ReadSchema(JsonElement welcome)
	{
		JsonElement schema = welcome.GetProperty("schema");
		string version = schema.GetProperty("schema_version").GetString();

		// A client that does not recognise the schema version refuses to decode
		// rather than misreading a float (docs/AGENT_API.md §4).
		if (version != AgentProtocol.SchemaVersion)
		{
			throw new InvalidOperationException(
				$"server publishes observation schema '{version}'; this client speaks"
				+ $" '{AgentProtocol.SchemaVersion}'. Rebuild the client against the server's gdpyr.");
		}

		var read = new GdpyrSchema
		{
			Version = version,
			TickRate = welcome.TryGetProperty("tick_rate", out JsonElement rate) ? rate.GetInt32() : 60,
		};

		JsonElement ground = schema.GetProperty("ground_observation");
		read.Floats = ground.GetProperty("floats").GetInt32();
		read.Bytes = ground.GetProperty("bytes").GetInt32();

		foreach (JsonElement field in ground.GetProperty("fields").EnumerateArray())
		{
			read.Add(field.GetProperty("name").GetString(), field.GetProperty("offset").GetInt32(),
				field.GetProperty("count").GetInt32());
		}

		return read;
	}

	// ---- operations --------------------------------------------------------

	/// <summary>
	/// Claims a ground seat, spawning a bot when the side has none free
	/// (docs/AGENT_API.md §2.2). Returns the seat's peer id.
	/// </summary>
	public async Task<int> AttachGroundAsync(int stepMul = 4, int peerId = 0,
		CancellationToken cancel = default)
	{
		JsonElement response = await RequestAsync(writer =>
		{
			writer.WriteString("op", "attach");
			writer.WriteString("team", "ground");
			writer.WriteString("policy", "ground");
			writer.WriteNumber("step_mul", stepMul);
			if (peerId != 0)
			{
				writer.WriteNumber("peer_id", peerId);
			}
		}, cancel).ConfigureAwait(false);

		return response.GetProperty("seat").GetInt32();
	}

	public Task DetachAsync(int seat, CancellationToken cancel = default) =>
		RequestAsync(writer =>
		{
			writer.WriteString("op", "detach");
			writer.WriteNumber("seat", seat);
		}, cancel);

	/// <summary>Switches this session between real time and stepped (docs/AGENT_API.md §5).</summary>
	public Task ConfigureAsync(bool stepped, int stepTimeoutMilliseconds = 10_000,
		CancellationToken cancel = default) =>
		RequestAsync(writer =>
		{
			writer.WriteString("op", "config");
			writer.WriteString("mode", stepped ? "stepped" : "realtime");
			writer.WriteNumber("step_timeout_ms", stepTimeoutMilliseconds);
		}, cancel);

	/// <summary>
	/// Submits one seat's decision. Fire-and-forget: a trainer at 15 Hz per seat
	/// does not want an acknowledgement per action.
	/// </summary>
	public void Act(int seat, in AgentGroundAction action, uint tick = 0) =>
		Write(AgentFrameKind.Observation, 0, action.Pack(seat, tick));

	/// <summary>Advances the simulation by <paramref name="ticks"/>. Stepped mode only.</summary>
	public async Task<bool> StepAsync(int ticks = 1, CancellationToken cancel = default)
	{
		JsonElement response = await RequestAsync(writer =>
		{
			writer.WriteString("op", "step");
			writer.WriteNumber("n", ticks);
		}, cancel).ConfigureAwait(false);

		Tick = response.GetProperty("tick").GetUInt32();
		return response.TryGetProperty("truncated", out JsonElement truncated) && truncated.GetBoolean();
	}

	/// <summary>Ends the round now and starts a fresh one (docs/AGENT_API.md §7.1).</summary>
	public async Task ResetAsync(int seed = 0, CancellationToken cancel = default)
	{
		await RequestAsync(writer =>
		{
			writer.WriteString("op", "reset");
			writer.WriteNumber("seed", seed);
		}, cancel).ConfigureAwait(false);

		_observations.Clear();
		_events.Clear();
	}

	/// <summary>A hash of the tick's simulation state, for measuring divergence (docs/AGENT_API.md §3).</summary>
	public async Task<string> StateHashAsync(CancellationToken cancel = default)
	{
		JsonElement response = await RequestAsync(writer => writer.WriteString("op", "state_hash"), cancel)
			.ConfigureAwait(false);

		return response.GetProperty("hash").GetString();
	}

	/// <summary>Asks for one seat's observation now, rather than waiting for the pushed one.</summary>
	public async Task<GdpyrObservation> ObserveAsync(int seat, CancellationToken cancel = default)
	{
		uint correlation = _correlation++;
		Write(AgentFrameKind.Request, correlation, Body(writer =>
		{
			writer.WriteString("op", "observe");
			writer.WriteNumber("seat", seat);
		}));

		while (true)
		{
			(AgentFrameKind kind, uint echoed, byte[] body) = await ReadFrameAsync(cancel).ConfigureAwait(false);
			if (kind == AgentFrameKind.Observation && echoed == correlation)
			{
				return DecodeObservation(body);
			}

			if (kind == AgentFrameKind.Error && echoed == correlation)
			{
				throw Failure(body);
			}

			Park(kind, body);
		}
	}

	/// <summary>The next pushed observation, blocking until one arrives.</summary>
	public async Task<GdpyrObservation> NextObservationAsync(CancellationToken cancel = default)
	{
		while (_observations.Count == 0)
		{
			(AgentFrameKind kind, uint _, byte[] body) = await ReadFrameAsync(cancel).ConfigureAwait(false);
			Park(kind, body);
		}

		return _observations.Dequeue();
	}

	/// <summary>Everything on the event stream that has arrived and not yet been read.</summary>
	public IEnumerable<GdpyrEvent> DrainEvents()
	{
		while (_events.Count > 0)
		{
			yield return _events.Dequeue();
		}
	}

	/// <summary>Reads whatever has arrived without blocking, so events and observations queue up.</summary>
	public async Task PollAsync(CancellationToken cancel = default)
	{
		while (_stream.DataAvailable || TryDecodeBuffered(out _, out _, out _))
		{
			(AgentFrameKind kind, uint _, byte[] body) = await ReadFrameAsync(cancel).ConfigureAwait(false);
			Park(kind, body);
		}
	}

	public void Dispose()
	{
		try
		{
			Write(AgentFrameKind.Request, 0, Body(writer => writer.WriteString("op", "quit")));
		}
		catch (Exception)
		{
			// The socket is already gone; the server releases the seats either way.
		}

		_client.Dispose();
	}

	// ---- plumbing ----------------------------------------------------------

	private async Task<JsonElement> RequestAsync(Action<Utf8JsonWriter> write, CancellationToken cancel)
	{
		uint correlation = _correlation++;
		Write(AgentFrameKind.Request, correlation, Body(write));

		while (true)
		{
			(AgentFrameKind kind, uint echoed, byte[] body) = await ReadFrameAsync(cancel).ConfigureAwait(false);
			if (echoed == correlation && kind == AgentFrameKind.Response)
			{
				return JsonDocument.Parse(body).RootElement.Clone();
			}

			if (echoed == correlation && kind == AgentFrameKind.Error)
			{
				throw Failure(body);
			}

			Park(kind, body);
		}
	}

	/// <summary>Files an unsolicited frame so a request's reply is not lost behind it.</summary>
	private void Park(AgentFrameKind kind, byte[] body)
	{
		switch (kind)
		{
			case AgentFrameKind.Observation:
				// The server also pushes an observation on every decision tick. A client
				// that asks for its own with `observe` never reads those, so the queue is
				// bounded rather than allowed to grow for twenty minutes.
				while (_observations.Count >= MaxQueuedObservations)
				{
					_observations.Dequeue();
				}

				_observations.Enqueue(DecodeObservation(body));
				break;

			case AgentFrameKind.Event:
				ReadEvents(body);
				break;

			case AgentFrameKind.Error:
				// An unsolicited error is the server telling the session something went
				// wrong outside a request — a step timeout, or a human connecting into a
				// stepped round. Loud, because both change what the episode means.
				Console.Error.WriteLine($"[gdpyr] {Encoding.UTF8.GetString(body)}");
				break;
		}
	}

	private void ReadEvents(byte[] body)
	{
		using JsonDocument document = JsonDocument.Parse(body);
		if (!document.RootElement.TryGetProperty("events", out JsonElement events))
		{
			return;
		}

		foreach (JsonElement element in events.EnumerateArray())
		{
			var record = new GdpyrEvent();
			foreach (JsonProperty property in element.EnumerateObject())
			{
				// Read by value kind rather than by name: a server that ever emits a
				// numeric field called "kind" would otherwise take the client down with
				// an exception rather than being ignored.
				if (property.Name == "kind" && property.Value.ValueKind == JsonValueKind.String)
				{
					record.Kind = property.Value.GetString();
				}
				else if (property.Name == "tick" && property.Value.ValueKind == JsonValueKind.Number)
				{
					record.Tick = property.Value.GetUInt32();
				}
				else if (property.Value.ValueKind == JsonValueKind.Number)
				{
					record.Fields[property.Name] = property.Value.GetDouble();
				}
			}

			while (_events.Count >= MaxQueuedEvents)
			{
				_events.Dequeue();
			}

			_events.Enqueue(record);
		}
	}

	private GdpyrObservation DecodeObservation(byte[] body)
	{
		const int header = 12;
		if (body.Length < header + (Schema.Floats * 4))
		{
			throw new InvalidOperationException(
				$"observation is {body.Length} bytes; the schema says {header + Schema.Bytes}");
		}

		int seat = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(body);
		ushort flags = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(4));
		uint tick = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(8));

		var values = new float[Schema.Floats];
		for (int i = 0; i < values.Length; i++)
		{
			values[i] = System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(
				body.AsSpan(header + (i * 4)));
		}

		Tick = tick;
		return new GdpyrObservation
		{
			Seat = seat,
			Tick = tick,
			Omniscient = (flags & 1) != 0,
			Unbounded = (flags & 2) != 0,
			Values = values,
		};
	}

	private static InvalidOperationException Failure(byte[] body)
	{
		using JsonDocument document = JsonDocument.Parse(body);
		JsonElement root = document.RootElement;
		string code = root.TryGetProperty("code", out JsonElement value) ? value.GetString() : "error";
		string message = root.TryGetProperty("message", out JsonElement text) ? text.GetString() : string.Empty;
		return new InvalidOperationException($"{code}: {message}");
	}

	private static byte[] Body(Action<Utf8JsonWriter> write)
	{
		var buffer = new System.Buffers.ArrayBufferWriter<byte>(256);
		using (var writer = new Utf8JsonWriter(buffer))
		{
			writer.WriteStartObject();
			write(writer);
			writer.WriteEndObject();
		}

		return buffer.WrittenSpan.ToArray();
	}

	private void Write(AgentFrameKind kind, uint correlation, ReadOnlySpan<byte> body)
	{
		byte[] frame = AgentProtocol.Encode(kind, correlation, body);
		_stream.Write(frame, 0, frame.Length);
	}

	private async Task<(AgentFrameKind Kind, uint Correlation, byte[] Body)> ReadFrameAsync(
		CancellationToken cancel)
	{
		while (true)
		{
			if (TryDecodeBuffered(out AgentFrameKind kind, out uint correlation, out byte[] body))
			{
				return (kind, correlation, body);
			}

			if (_length == _buffer.Length)
			{
				Array.Resize(ref _buffer, Math.Min(_buffer.Length * 2, AgentProtocol.MaxFrameBytes));
			}

			int read = await _stream
				.ReadAsync(_buffer.AsMemory(_length, _buffer.Length - _length), cancel)
				.ConfigureAwait(false);

			if (read <= 0)
			{
				throw new InvalidOperationException("the gdpyr agent channel closed");
			}

			_length += read;
		}
	}

	private bool TryDecodeBuffered(out AgentFrameKind kind, out uint correlation, out byte[] body)
	{
		body = null;
		if (!AgentProtocol.TryDecode(_buffer.AsSpan(0, _length), out kind, out correlation, out int offset,
			out int length, out int consumed, out bool fatal))
		{
			if (fatal)
			{
				throw new InvalidOperationException("the gdpyr agent channel sent an undecodable frame");
			}

			return false;
		}

		body = _buffer.AsSpan(offset, length).ToArray();
		Buffer.BlockCopy(_buffer, consumed, _buffer, 0, _length - consumed);
		_length -= consumed;
		return true;
	}
}
