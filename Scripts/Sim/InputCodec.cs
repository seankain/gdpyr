using System;
using System.Buffers.Binary;

namespace Gdpyr.Sim;

/// <summary>
/// Packs the redundant input packet: the last few <see cref="InputFrame"/>s, sent
/// unreliably every tick, so one lost datagram costs nothing
/// (docs/NETCODE.md §3.2, §7).
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

	public const int HeaderBytes = 1;

	public const int MaxPayloadBytes = HeaderBytes + MaxFrames * InputFrame.SizeBytes;

	public static int PayloadBytes(int frameCount) => HeaderBytes + frameCount * InputFrame.SizeBytes;

	public static byte[] Encode(ReadOnlySpan<InputFrame> frames)
	{
		var payload = new byte[PayloadBytes(Math.Min(frames.Length, MaxFrames))];
		Encode(frames, payload);
		return payload;
	}

	/// <summary>Returns the number of bytes written, or 0 if <paramref name="into"/> is too small.</summary>
	public static int Encode(ReadOnlySpan<InputFrame> frames, Span<byte> into)
	{
		int count = Math.Min(frames.Length, MaxFrames);
		int size = PayloadBytes(count);
		if (into.Length < size)
		{
			return 0;
		}

		into[0] = (byte)count;
		int offset = HeaderBytes;
		for (int i = 0; i < count; i++)
		{
			ref readonly InputFrame frame = ref frames[i];
			BinaryPrimitives.WriteUInt32LittleEndian(into[offset..], frame.Tick);
			into[offset + 4] = (byte)frame.MoveX;
			into[offset + 5] = (byte)frame.MoveZ;
			BinaryPrimitives.WriteUInt16LittleEndian(into[(offset + 6)..], frame.Yaw);
			BinaryPrimitives.WriteInt16LittleEndian(into[(offset + 8)..], frame.Pitch);
			BinaryPrimitives.WriteUInt16LittleEndian(into[(offset + 10)..], frame.Buttons);
			offset += InputFrame.SizeBytes;
		}
		return size;
	}

	public static bool TryDecode(ReadOnlySpan<byte> payload, Span<InputFrame> into, out int count)
	{
		count = 0;
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
			into[i] = new InputFrame
			{
				Tick = BinaryPrimitives.ReadUInt32LittleEndian(payload[offset..]),
				// -128 has no positive counterpart, so it is clamped away here rather
				// than being left for the movement code to deal with.
				MoveX = (sbyte)Math.Max((sbyte)payload[offset + 4], (sbyte)(-127)),
				MoveZ = (sbyte)Math.Max((sbyte)payload[offset + 5], (sbyte)(-127)),
				Yaw = BinaryPrimitives.ReadUInt16LittleEndian(payload[(offset + 6)..]),
				Pitch = BinaryPrimitives.ReadInt16LittleEndian(payload[(offset + 8)..]),
				Buttons = BinaryPrimitives.ReadUInt16LittleEndian(payload[(offset + 10)..]),
			};
			offset += InputFrame.SizeBytes;
		}

		count = frames;
		return true;
	}
}
