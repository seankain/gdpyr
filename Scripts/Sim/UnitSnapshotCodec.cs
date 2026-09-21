using System;
using System.Buffers.Binary;

namespace Gdpyr.Sim;

/// <summary>
/// Packs the per-tick unit broadcast (docs/NETCODE.md §6.1, §7): a tick index, a
/// count, then one fixed-size record per unit.
///
/// This is the "single custom packed replicator" §6.1 offers as the alternative to
/// a <c>MultiplayerSpawner</c> plus a <c>MultiplayerSynchronizer</c> per unit, and
/// M3 takes it. Three reasons, none of them performance — at fifty units either
/// would do:
/// <list type="number">
/// <item>every other message in this project is already a hand-packed codec with a
/// total decoder and a unit test, and a second replication mechanism alongside
/// them is a second set of rules to remember;</item>
/// <item>the fog of war M4 is built around is a filter over *which records go in
/// which peer's packet*, which is a loop bound here and a per-node API call
/// there;</item>
/// <item>it closes the risk §6 flags against the built-in route — that per-peer
/// visibility gates synchronization but perhaps not spawning — because a unit a
/// peer is not told about has no record in its packet and therefore no node.</item>
/// </list>
///
/// Decoding is total, like every other codec here: a malformed datagram returns
/// false, never throws.
/// </summary>
public static class UnitSnapshotCodec
{
	/// <summary>
	/// Units in one packet. At <see cref="UnitSnapshot.SizeBytes"/> each this is
	/// 837 bytes with the header, which stays inside a single datagram — the
	/// broadcast is unreliable, so a fragmented one would be a packet that is
	/// mostly lost rather than mostly late.
	/// </summary>
	public const int MaxUnits = SimConfig.MaxUnits;

	public const int HeaderBytes = 5;

	public const int MaxPayloadBytes = HeaderBytes + (MaxUnits * UnitSnapshot.SizeBytes);

	public static int PayloadBytes(int unitCount) => HeaderBytes + (unitCount * UnitSnapshot.SizeBytes);

	public static byte[] Encode(uint tick, ReadOnlySpan<UnitSnapshot> units)
	{
		var payload = new byte[PayloadBytes(Math.Min(units.Length, MaxUnits))];
		Encode(tick, units, payload);
		return payload;
	}

	/// <summary>Returns the number of bytes written, or 0 if <paramref name="into"/> is too small.</summary>
	public static int Encode(uint tick, ReadOnlySpan<UnitSnapshot> units, Span<byte> into)
	{
		int count = Math.Min(units.Length, MaxUnits);
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
			ref readonly UnitSnapshot u = ref units[i];
			BinaryPrimitives.WriteUInt16LittleEndian(into[offset..], u.UnitId);
			into[offset + 2] = u.DefinitionId;
			WriteVector(into[(offset + 3)..], u.Position);
			BinaryPrimitives.WriteUInt16LittleEndian(into[(offset + 9)..], Quantize.YawToU16(u.Yaw));
			into[offset + 11] = UnitFlags.Pack(u.State, u.Team);
			into[offset + 12] = u.HealthPercent;
			offset += UnitSnapshot.SizeBytes;
		}

		return size;
	}

	public static bool TryDecode(ReadOnlySpan<byte> payload, Span<UnitSnapshot> into, out uint tick, out int count)
	{
		tick = 0;
		count = 0;
		if (payload.Length < HeaderBytes)
		{
			return false;
		}

		tick = BinaryPrimitives.ReadUInt32LittleEndian(payload);
		int units = payload[4];
		if (units > MaxUnits || payload.Length < PayloadBytes(units) || into.Length < units)
		{
			return false;
		}

		int offset = HeaderBytes;
		for (int i = 0; i < units; i++)
		{
			byte flags = payload[offset + 11];
			into[i] = new UnitSnapshot
			{
				UnitId = BinaryPrimitives.ReadUInt16LittleEndian(payload[offset..]),
				DefinitionId = payload[offset + 2],
				Position = ReadVector(payload[(offset + 3)..]),
				Yaw = Quantize.U16ToYaw(BinaryPrimitives.ReadUInt16LittleEndian(payload[(offset + 9)..])),
				State = UnitFlags.State(flags),
				Team = UnitFlags.TeamOf(flags),
				HealthPercent = payload[offset + 12],
			};
			offset += UnitSnapshot.SizeBytes;
		}

		count = units;
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
