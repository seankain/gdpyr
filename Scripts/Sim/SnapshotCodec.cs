using System;
using System.Buffers.Binary;

namespace Gdpyr.Sim;

/// <summary>
/// Packs the per-tick player snapshot broadcast (docs/NETCODE.md §7): a tick
/// index, a count, then one fixed-size record per player. Positions and
/// velocities go out as int16 centimetres and angles as 16-bit fixed point, which
/// is what keeps a 60 Hz broadcast to 8 players inside ~15 KB/s.
///
/// Like <see cref="InputCodec"/>, decoding is total: a client must not be able to
/// be crashed by a malformed datagram either.
/// </summary>
public static class SnapshotCodec
{
	/// <summary>Matches <c>TransportFactory.MaxClients</c>; bounds the decode buffer.</summary>
	public const int MaxPlayers = 16;

	public const int HeaderBytes = 5;

	public const int MaxPayloadBytes = HeaderBytes + MaxPlayers * PlayerSnapshot.SizeBytes;

	public static int PayloadBytes(int playerCount) => HeaderBytes + playerCount * PlayerSnapshot.SizeBytes;

	public static byte[] Encode(uint tick, ReadOnlySpan<PlayerSnapshot> players)
	{
		var payload = new byte[PayloadBytes(Math.Min(players.Length, MaxPlayers))];
		Encode(tick, players, payload);
		return payload;
	}

	/// <summary>Returns the number of bytes written, or 0 if <paramref name="into"/> is too small.</summary>
	public static int Encode(uint tick, ReadOnlySpan<PlayerSnapshot> players, Span<byte> into)
	{
		int count = Math.Min(players.Length, MaxPlayers);
		int size = PayloadBytes(count);
		if (into.Length < size)
		{
			return 0;
		}

		BinaryPrimitives.WriteUInt32LittleEndian(into, tick);
		into[4] = (byte)count;

		int offset = HeaderBytes;
		for (int i = 0; i < count; i++)
		{
			ref readonly PlayerSnapshot p = ref players[i];
			BinaryPrimitives.WriteInt32LittleEndian(into[offset..], p.PeerId);
			WriteVector(into[(offset + 4)..], p.Position);
			WriteVector(into[(offset + 10)..], p.Velocity);
			BinaryPrimitives.WriteUInt16LittleEndian(into[(offset + 16)..], Quantize.YawToU16(p.Yaw));
			BinaryPrimitives.WriteInt16LittleEndian(into[(offset + 18)..], Quantize.PitchToI16(p.Pitch));
			BinaryPrimitives.WriteUInt32LittleEndian(into[(offset + 20)..], p.LastInputTick);
			into[offset + 24] = p.StateId;
			into[offset + 25] = p.InputBufferDepth;
			offset += PlayerSnapshot.SizeBytes;
		}
		return size;
	}

	public static bool TryDecode(ReadOnlySpan<byte> payload, Span<PlayerSnapshot> into, out uint tick, out int count)
	{
		tick = 0;
		count = 0;
		if (payload.Length < HeaderBytes)
		{
			return false;
		}

		tick = BinaryPrimitives.ReadUInt32LittleEndian(payload);
		int players = payload[4];
		if (players > MaxPlayers || payload.Length < PayloadBytes(players) || into.Length < players)
		{
			return false;
		}

		int offset = HeaderBytes;
		for (int i = 0; i < players; i++)
		{
			into[i] = new PlayerSnapshot
			{
				PeerId = BinaryPrimitives.ReadInt32LittleEndian(payload[offset..]),
				Position = ReadVector(payload[(offset + 4)..]),
				Velocity = ReadVector(payload[(offset + 10)..]),
				Yaw = Quantize.U16ToYaw(BinaryPrimitives.ReadUInt16LittleEndian(payload[(offset + 16)..])),
				Pitch = Quantize.I16ToPitch(BinaryPrimitives.ReadInt16LittleEndian(payload[(offset + 18)..])),
				LastInputTick = BinaryPrimitives.ReadUInt32LittleEndian(payload[(offset + 20)..]),
				StateId = payload[offset + 24],
				InputBufferDepth = payload[offset + 25],
			};
			offset += PlayerSnapshot.SizeBytes;
		}

		count = players;
		return true;
	}

	private static void WriteVector(Span<byte> into, Godot.Vector3 v)
	{
		BinaryPrimitives.WriteInt16LittleEndian(into, Quantize.MetersToI16(v.X));
		BinaryPrimitives.WriteInt16LittleEndian(into[2..], Quantize.MetersToI16(v.Y));
		BinaryPrimitives.WriteInt16LittleEndian(into[4..], Quantize.MetersToI16(v.Z));
	}

	private static Godot.Vector3 ReadVector(ReadOnlySpan<byte> from) => new(
		Quantize.I16ToMeters(BinaryPrimitives.ReadInt16LittleEndian(from)),
		Quantize.I16ToMeters(BinaryPrimitives.ReadInt16LittleEndian(from[2..])),
		Quantize.I16ToMeters(BinaryPrimitives.ReadInt16LittleEndian(from[4..])));
}
