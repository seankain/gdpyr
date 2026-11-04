using System;
using System.Buffers.Binary;

namespace Gdpyr.Sim;

/// <summary>
/// Packs the redundant input packet: the last few <see cref="InputFrame"/>s, sent
/// unreliably every tick, so one lost datagram costs nothing
/// (docs/NETCODE.md §3.2, §7) — and how far behind the newest of them this client was
/// drawing the field when it sampled it, which is where a seated shooter's round leaves
/// from (docs/GROUND_VEHICLES.md §5.6). The frames themselves are unchanged.
///
/// Decoding is the server's trust boundary. It is total — a malformed or hostile
/// payload returns false rather than throwing — and it clamps every field into
/// range, because everything downstream assumes sane values.
/// </summary>
public static class InputCodec
{
	/// <summary>
	/// Hard cap on frames per packet. Bounds the work a single datagram can ask
	/// the server to do; <see cref="SimConfig.InputRedundancy"/> is what we send.
	/// </summary>
	public const int MaxFrames = 8;

	/// <summary>The frame count, then how far behind the newest frame the client drew (<see cref="DrawnBehind"/>).</summary>
	public const int HeaderBytes = 2;

	/// <summary>What one step of the drawn-behind byte is worth [ticks]: 7.5 cm at 18 m/s.</summary>
	public const float DrawnBehindQuantumTicks = 0.25f;

	/// <summary>
	/// How far behind a frame's tick its client was drawing the field when it sampled it,
	/// as the header carries it: quarter ticks, 1 to 255; 0 for not known — before the
	/// client has drawn anything, or further behind than a byte holds.
	/// </summary>
	public static byte DrawnBehind(float ticks)
	{
		if (!float.IsFinite(ticks) || ticks <= 0f)
		{
			return 0;
		}

		float steps = MathF.Round(ticks / DrawnBehindQuantumTicks);
		return steps is >= 1f and <= 255f ? (byte)steps : (byte)0;
	}

	/// <summary>The header's byte back in ticks; NaN for 0, not known.</summary>
	public static float DrawnBehindTicks(byte quarterTicks) =>
		quarterTicks == 0 ? float.NaN : quarterTicks * DrawnBehindQuantumTicks;

	public const int MaxPayloadBytes = HeaderBytes + MaxFrames * InputFrame.SizeBytes;

	public static int PayloadBytes(int frameCount) => HeaderBytes + frameCount * InputFrame.SizeBytes;

	public static byte[] Encode(ReadOnlySpan<InputFrame> frames, byte drawnBehind = 0)
	{
		var payload = new byte[PayloadBytes(Math.Min(frames.Length, MaxFrames))];
		Encode(frames, payload, drawnBehind);
		return payload;
	}

	/// <summary>
	/// Returns the number of bytes written, or 0 if <paramref name="into"/> is too small.
	/// <paramref name="drawnBehind"/> is the newest frame's, as <see cref="DrawnBehind"/> makes it.
	/// </summary>
	public static int Encode(ReadOnlySpan<InputFrame> frames, Span<byte> into, byte drawnBehind = 0)
	{
		int count = Math.Min(frames.Length, MaxFrames);
		int size = PayloadBytes(count);
		if (into.Length < size)
		{
			return 0;
		}

		into[0] = (byte)count;
		into[1] = drawnBehind;
		int offset = HeaderBytes;
		for (int i = 0; i < count; i++)
		{
			WriteFrame(frames[i], into[offset..]);
			offset += InputFrame.SizeBytes;
		}
		return size;
	}

	/// <summary>
	/// Writes one frame's <see cref="InputFrame.SizeBytes"/> bytes. Split out of
	/// <see cref="Encode"/> so that the demo journal, which stores frames one at a
	/// time rather than in a redundant batch, writes the same layout by
	/// construction rather than by a comment asking it to
	/// (<see cref="Demo.DemoRecordKind.Input"/>).
	/// </summary>
	public static void WriteFrame(in InputFrame frame, Span<byte> into)
	{
		BinaryPrimitives.WriteUInt32LittleEndian(into, frame.Tick);
		into[4] = (byte)frame.MoveX;
		into[5] = (byte)frame.MoveZ;
		BinaryPrimitives.WriteUInt16LittleEndian(into[6..], frame.Yaw);
		BinaryPrimitives.WriteInt16LittleEndian(into[8..], frame.Pitch);
		BinaryPrimitives.WriteUInt16LittleEndian(into[10..], frame.Buttons);
	}

	/// <summary>
	/// Reads one frame back, clamping every field into range. The counterpart to
	/// <see cref="WriteFrame"/>; see <see cref="TryDecode"/> for why the clamping
	/// is here rather than downstream.
	/// </summary>
	public static InputFrame ReadFrame(ReadOnlySpan<byte> from) => new()
	{
		Tick = BinaryPrimitives.ReadUInt32LittleEndian(from),
		// -128 has no positive counterpart, so it is clamped away here rather
		// than being left for the movement code to deal with.
		MoveX = (sbyte)Math.Max((sbyte)from[4], (sbyte)(-127)),
		MoveZ = (sbyte)Math.Max((sbyte)from[5], (sbyte)(-127)),
		Yaw = BinaryPrimitives.ReadUInt16LittleEndian(from[6..]),
		Pitch = BinaryPrimitives.ReadInt16LittleEndian(from[8..]),
		Buttons = BinaryPrimitives.ReadUInt16LittleEndian(from[10..]),
	};

	public static bool TryDecode(ReadOnlySpan<byte> payload, Span<InputFrame> into, out int count) =>
		TryDecode(payload, into, out count, out _);

	/// <summary>
	/// The frames, oldest first, and the newest one's drawn-behind byte (0 for not known).
	/// Total: a malformed payload is false, never an exception.
	/// </summary>
	public static bool TryDecode(ReadOnlySpan<byte> payload, Span<InputFrame> into, out int count, out byte drawnBehind)
	{
		count = 0;
		drawnBehind = 0;
		if (payload.Length < HeaderBytes)
		{
			return false;
		}

		int frames = payload[0];
		if (frames == 0 || frames > MaxFrames || payload.Length < PayloadBytes(frames) || into.Length < frames)
		{
			return false;
		}

		int offset = HeaderBytes;
		for (int i = 0; i < frames; i++)
		{
			into[i] = ReadFrame(payload[offset..]);
			offset += InputFrame.SizeBytes;
		}

		count = frames;
		drawnBehind = payload[1];
		return true;
	}
}
