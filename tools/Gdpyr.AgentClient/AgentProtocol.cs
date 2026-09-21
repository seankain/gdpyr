using System;
using System.Buffers.Binary;

namespace Gdpyr.AgentClient;

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
/// The client half of the framing (docs/AGENT_API.md §4).
///
/// Deliberately a second, independent implementation rather than a shared
/// assembly: this library is what an external process links, and a client that
/// compiles against the game's own source would hide exactly the skew the
/// published schema exists to catch.
/// </summary>
public static class AgentProtocol
{
	public const int Version = 1;

	/// <summary>
	/// The observation layout this client knows how to decode. A <c>welcome</c>
	/// carrying anything else is refused rather than misread — the whole point of
	/// fetching the schema instead of compiling it.
	/// </summary>
	public const string SchemaVersion = "gdpyr-agent-obs-1";

	public const int LengthBytes = 4;
	public const int HeaderBytes = 5;
	public const int MaxFrameBytes = 1 << 20;

	public static int FrameBytes(int bodyLength) => LengthBytes + HeaderBytes + bodyLength;

	public static byte[] Encode(AgentFrameKind kind, uint correlation, ReadOnlySpan<byte> body)
	{
		var frame = new byte[FrameBytes(body.Length)];
		BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)(HeaderBytes + body.Length));
		frame[LengthBytes] = (byte)kind;
		BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(LengthBytes + 1), correlation);
		body.CopyTo(frame.AsSpan(LengthBytes + HeaderBytes));
		return frame;
	}

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

/// <summary>Buttons a ground policy may hold, matching <c>InputButtons</c> on the wire.</summary>
[Flags]
public enum AgentButtons : ushort
{
	None = 0,
	Jump = 1 << 0,
	Crouch = 1 << 1,
	Sprint = 1 << 2,
	Fire = 1 << 3,
	Ads = 1 << 4,
	Reload = 1 << 5,
	Use = 1 << 6,
	Melee = 1 << 7,
	Weapon1 = 1 << 8,
	Weapon2 = 1 << 9,
	Weapon3 = 1 << 10,
}

/// <summary>
/// One decision for one ground seat, packed as the sixteen bytes the server
/// expects: seat, flags, then an <c>InputFrame</c> (docs/AGENT_API.md §7.2).
/// </summary>
public struct AgentGroundAction
{
	public float MoveX;
	public float MoveZ;

	/// <summary>Yaw as a delta from where the character is looking. The parameterization to learn in.</summary>
	public float DeltaYaw;

	public float DeltaPitch;

	public AgentButtons Buttons;

	/// <summary>Seat (i32) · flags (u16) · reserved (u16) · an <c>InputFrame</c> (12).</summary>
	public const int SizeBytes = 20;

	private const ushort FlagLookIsDelta = 1 << 0;
	private const float TwoPi = MathF.PI * 2f;
	private const float HalfPi = MathF.PI * 0.5f;

	public readonly byte[] Pack(int seat, uint tick)
	{
		var into = new byte[SizeBytes];
		BinaryPrimitives.WriteInt32LittleEndian(into, seat);
		BinaryPrimitives.WriteUInt16LittleEndian(into.AsSpan(4), FlagLookIsDelta);
		BinaryPrimitives.WriteUInt16LittleEndian(into.AsSpan(6), 0);
		BinaryPrimitives.WriteUInt32LittleEndian(into.AsSpan(8), tick);
		into[12] = unchecked((byte)(sbyte)Math.Clamp(MathF.Round(MoveX * 127f), -127f, 127f));
		into[13] = unchecked((byte)(sbyte)Math.Clamp(MathF.Round(MoveZ * 127f), -127f, 127f));
		BinaryPrimitives.WriteUInt16LittleEndian(into.AsSpan(14), YawToU16(DeltaYaw));
		BinaryPrimitives.WriteInt16LittleEndian(into.AsSpan(16), PitchToI16(DeltaPitch));
		BinaryPrimitives.WriteUInt16LittleEndian(into.AsSpan(18), (ushort)Buttons);
		return into;
	}

	private static ushort YawToU16(float radians)
	{
		float wrapped = radians - (TwoPi * MathF.Floor(radians / TwoPi));
		return (ushort)((int)MathF.Round(wrapped * (65536f / TwoPi)) & 0xFFFF);
	}

	private static short PitchToI16(float radians) =>
		(short)MathF.Round(Math.Clamp(radians, -HalfPi, HalfPi) * (short.MaxValue / HalfPi));
}
