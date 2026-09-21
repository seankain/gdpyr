using System;
using System.Buffers;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Text.Json;
using Gdpyr.Sim.Agent;

namespace Gdpyr.Agent;

/// <summary>
/// One connection on the agent control channel (docs/AGENT_API.md §4).
///
/// Transport only: it reads frames out of the socket, writes frames into it, and
/// holds the per-session settings — whether this session is stepped, which
/// observation format it asked for, where its event cursor is, and which seats it
/// has leases on. What the frames *mean* is <see cref="AgentServer"/>'s business,
/// because that is the half that is allowed to touch the game.
///
/// Reads are non-blocking and happen from inside the physics tick, so nothing
/// here starts a thread and no game state is touched from one.
/// </summary>
public sealed class AgentSession : IDisposable
{
	/// <summary>A connection that has not authenticated within this is dropped (docs/AGENT_API.md §4.1).</summary>
	public const int AuthDeadlineMilliseconds = 2000;

	/// <summary>Default wait before a stepped session that has stopped stepping is dropped back to real time.</summary>
	public const int DefaultStepTimeoutMilliseconds = 10_000;

	private readonly TcpClient _client;
	private readonly NetworkStream _stream;
	private readonly List<int> _seats = new();

	private byte[] _read = new byte[8192];
	private int _readLength;

	private readonly byte[] _scratch = new byte[16384];

	public AgentSession(int id, TcpClient client, long nowMilliseconds)
	{
		Id = id;
		_client = client;
		_client.NoDelay = true;
		_stream = client.GetStream();
		ConnectedAtMilliseconds = nowMilliseconds;
	}

	public int Id { get; }

	/// <summary>True once <c>hello</c> has been accepted. Nothing else is answered before it.</summary>
	public bool Authenticated { get; set; }

	public long ConnectedAtMilliseconds { get; }

	/// <summary>Stepped mode: the sim does not advance until this session steps (docs/AGENT_API.md §5.2).</summary>
	public bool Stepped { get; set; }

	public int StepTimeoutMilliseconds { get; set; } = DefaultStepTimeoutMilliseconds;

	/// <summary>Ticks this session has asked for and not yet been given.</summary>
	public int PendingSteps { get; set; }

	/// <summary>Set while a <c>step</c> is waiting on those ticks to elapse.</summary>
	public bool StepOutstanding { get; set; }

	public uint StepCorrelation { get; set; }

	/// <summary>Observations as packed float32 rather than as named JSON fields.</summary>
	public bool BinaryObservations { get; set; } = true;

	/// <summary>
	/// The optional feature planes ride along with a strategist observation
	/// (docs/AGENT_API.md §6.3). Off unless asked for: they are 16 KB a decision and
	/// a scatter over live units, and most policies do not want them.
	/// </summary>
	public bool FeaturePlanes { get; set; }

	/// <summary>How far this session has read the event stream.</summary>
	public ulong EventCursor { get; set; }

	public bool Closed { get; private set; }

	public IReadOnlyList<int> Seats => _seats;

	public void HoldSeat(int peerId)
	{
		if (!_seats.Contains(peerId))
		{
			_seats.Add(peerId);
		}
	}

	public void DropSeat(int peerId) => _seats.Remove(peerId);

	// ---- transport ---------------------------------------------------------

	/// <summary>
	/// Pulls whatever has arrived into the read buffer and hands each complete
	/// frame to <paramref name="handle"/>. Returns false when the peer has gone or
	/// has sent something the framing cannot survive.
	/// </summary>
	public bool Receive(Action<AgentSession, AgentFrameKind, uint, ReadOnlyMemory<byte>> handle)
	{
		if (Closed)
		{
			return false;
		}

		try
		{
			while (_stream.DataAvailable)
			{
				if (_readLength == _read.Length)
				{
					if (_read.Length >= AgentProtocol.MaxFrameBytes)
					{
						return false;
					}

					Array.Resize(ref _read, Math.Min(_read.Length * 2, AgentProtocol.MaxFrameBytes));
				}

				int read = _stream.Read(_read, _readLength, _read.Length - _readLength);
				if (read <= 0)
				{
					return false;
				}

				_readLength += read;
			}
		}
		catch (Exception)
		{
			return false;
		}

		int consumedTotal = 0;
		while (true)
		{
			ReadOnlySpan<byte> pending = _read.AsSpan(consumedTotal, _readLength - consumedTotal);
			if (!AgentProtocol.TryDecode(pending, out AgentFrameKind kind, out uint correlation,
				out int bodyOffset, out int bodyLength, out int consumed, out bool fatal))
			{
				if (fatal)
				{
					return false;
				}

				break;
			}

			handle(this, kind, correlation,
				new ReadOnlyMemory<byte>(_read, consumedTotal + bodyOffset, bodyLength));
			consumedTotal += consumed;

			if (Closed)
			{
				return false;
			}
		}

		if (consumedTotal > 0)
		{
			Buffer.BlockCopy(_read, consumedTotal, _read, 0, _readLength - consumedTotal);
			_readLength -= consumedTotal;
		}

		return true;
	}

	public bool Send(AgentFrameKind kind, uint correlation, ReadOnlySpan<byte> body)
	{
		if (Closed)
		{
			return false;
		}

		int size = AgentProtocol.FrameBytes(body.Length);
		byte[] frame = size <= _scratch.Length ? _scratch : new byte[size];
		if (AgentProtocol.Encode(kind, correlation, body, frame) == 0)
		{
			return false;
		}

		try
		{
			_stream.Write(frame, 0, size);
			return true;
		}
		catch (Exception)
		{
			Closed = true;
			return false;
		}
	}

	/// <summary>Writes a JSON body built by <paramref name="write"/> as one frame.</summary>
	public bool SendJson(AgentFrameKind kind, uint correlation, Action<Utf8JsonWriter> write)
	{
		var buffer = new ArrayBufferWriter<byte>(512);
		using (var writer = new Utf8JsonWriter(buffer))
		{
			writer.WriteStartObject();
			write(writer);
			writer.WriteEndObject();
		}

		return Send(kind, correlation, buffer.WrittenSpan);
	}

	public bool SendError(uint correlation, string code, string message) =>
		Send(AgentFrameKind.Error, correlation, AgentJson.Error(code, message));

	public void Close()
	{
		if (Closed)
		{
			return;
		}

		Closed = true;
		try
		{
			_client.Close();
		}
		catch (Exception)
		{
			// Closing a socket that is already gone is not news.
		}
	}

	public void Dispose() => Close();
}
