using System;
using System.Buffers.Binary;

namespace Gdpyr.Sim.Agent;

/// <summary>What a frame on the agent socket is (docs/AGENT_API.md §4).</summary>
public enum AgentFrameKind : byte
{
	Request = 1,
	Response = 2,
	Observation = 3,
	Event = 4,
	Error = 5,
}

/// <summary>
/// Framing for the agent control channel: length-prefixed, little-endian, one
/// frame per message (docs/AGENT_API.md §4).
///
/// <code>
/// u32 length          bytes that follow
/// u8  kind            1 request · 2 response · 3 observation · 4 event · 5 error
/// u32 correlation     echoed on the response; 0 for unsolicited frames
/// ... body            UTF-8 JSON, or packed float32 for kind 3
/// </code>
///
/// Engine-free and total: a malformed or hostile prefix comes back as false
/// rather than as an exception, the same posture <see cref="InputCodec"/> takes
/// towards a datagram. This is a trust boundary even on loopback — the socket can
/// spawn players and reset rounds (docs/AGENT_API.md §4.1).
/// </summary>
public static class AgentProtocol
{
	/// <summary>Bumped when the wire format changes in a way a client must notice.</summary>
	public const int Version = 1;

	/// <summary>
	/// Identifies the observation layout <c>welcome</c> publishes. A client that
	/// does not recognise it must refuse to decode rather than misread a float
	/// (docs/AGENT_API.md §4).
	/// </summary>
	public const string SchemaVersion = "gdpyr-agent-obs-1";

	/// <summary>The u32 length prefix itself.</summary>
	public const int LengthBytes = 4;

	/// <summary>Kind and correlation: what the length counts before the body.</summary>
	public const int HeaderBytes = 5;

	/// <summary>
	/// Largest frame either end will write or accept. A strategist observation with
	/// feature planes is ~20 KB, so this is two orders of magnitude of headroom and
	/// still bounds what one prefix can make the server allocate.
	/// </summary>
	public const int MaxFrameBytes = 1 << 20;

	/// <summary>Total bytes on the wire for a body of the given length.</summary>
	public static int FrameBytes(int bodyLength) => LengthBytes + HeaderBytes + bodyLength;

	/// <summary>
	/// Writes one frame. Returns the bytes written, or 0 when
	/// <paramref name="into"/> is too small — never a partial frame.
	/// </summary>
	public static int Encode(AgentFrameKind kind, uint correlation, ReadOnlySpan<byte> body, Span<byte> into)
	{
		int size = FrameBytes(body.Length);
		if (into.Length < size || size > MaxFrameBytes)
		{
			return 0;
		}

		BinaryPrimitives.WriteUInt32LittleEndian(into, (uint)(HeaderBytes + body.Length));
		into[LengthBytes] = (byte)kind;
		BinaryPrimitives.WriteUInt32LittleEndian(into[(LengthBytes + 1)..], correlation);
		body.CopyTo(into[(LengthBytes + HeaderBytes)..]);
		return size;
	}

	public static byte[] Encode(AgentFrameKind kind, uint correlation, ReadOnlySpan<byte> body)
	{
		var frame = new byte[FrameBytes(body.Length)];
		Encode(kind, correlation, body, frame);
		return frame;
	}

	/// <summary>
	/// Reads one frame out of a stream buffer.
	///
	/// <paramref name="consumed"/> is the bytes to drop from the front of
	/// <paramref name="buffer"/> once the body has been handled. A return of false
	/// with <paramref name="fatal"/> false means "not enough bytes yet"; with
	/// <paramref name="fatal"/> true it means the stream is unusable and the
	/// session must be dropped.
	/// </summary>
	public static bool TryDecode(ReadOnlySpan<byte> buffer, out AgentFrameKind kind, out uint correlation,
		out int bodyOffset, out int bodyLength, out int consumed, out bool fatal)
	{
		kind = default;
		correlation = 0;
		bodyOffset = 0;
		bodyLength = 0;
		consumed = 0;
		fatal = false;

		if (buffer.Length < LengthBytes)
		{
			return false;
		}

		uint length = BinaryPrimitives.ReadUInt32LittleEndian(buffer);
		if (length < HeaderBytes || length > MaxFrameBytes - LengthBytes)
		{
			fatal = true;
			return false;
		}

		int total = LengthBytes + (int)length;
		if (buffer.Length < total)
		{
			return false;
		}

		byte raw = buffer[LengthBytes];
		if (raw is < (byte)AgentFrameKind.Request or > (byte)AgentFrameKind.Error)
		{
			fatal = true;
			return false;
		}

		kind = (AgentFrameKind)raw;
		correlation = BinaryPrimitives.ReadUInt32LittleEndian(buffer[(LengthBytes + 1)..]);
		bodyOffset = LengthBytes + HeaderBytes;
		bodyLength = (int)length - HeaderBytes;
		consumed = total;
		return true;
	}
}
