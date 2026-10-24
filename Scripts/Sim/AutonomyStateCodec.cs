using System;
using System.Buffers.Binary;

namespace Gdpyr.Sim;

/// <summary>One unit whose micro-autonomy is not the side's default for new units.</summary>
public struct UnitAutonomyRecord
{
	public ushort UnitId;

	public MicroAutonomy Mode;
}

/// <summary>
/// Packs <c>ServerAutonomyState</c> (docs/TACTICS.md §5.9): the strategist side's
/// micro-autonomy as its strategists' clients need it to draw the selection card.
/// Sent reliably, to strategist peers only, whole, whenever it changes and to a peer
/// that takes the chair — a setting is an order, and orders are not on the wire to
/// the ground force (docs/NETCODE.md §6.3).
///
/// <code>
/// [default: u8] [count: u16] count × [unit id: u16] [mode: u8]
/// </code>
///
/// Only the units whose mode differs from the default are listed: every other unit
/// is the default. A unit that has died is simply no longer listed. Decoding is
/// total, as every codec here is: a malformed payload returns false and never
/// throws, and a mode byte that names no mode is malformed.
/// </summary>
public static class AutonomyStateCodec
{
	public const int HeaderBytes = 3;

	public const int RecordBytes = 3;

	/// <summary>Never more units on the field than this, so never more exceptions.</summary>
	public const int MaxRecords = SimConfig.MaxUnits;

	public const int MaxPayloadBytes = HeaderBytes + (MaxRecords * RecordBytes);

	public static int PayloadBytes(int records) => HeaderBytes + (records * RecordBytes);

	public static byte[] Encode(MicroAutonomy teamDefault, ReadOnlySpan<UnitAutonomyRecord> records)
	{
		var payload = new byte[PayloadBytes(Math.Min(records.Length, MaxRecords))];
		Encode(teamDefault, records, payload);
		return payload;
	}

	/// <summary>Returns the number of bytes written, or 0 if <paramref name="into"/> is too small.</summary>
	public static int Encode(MicroAutonomy teamDefault, ReadOnlySpan<UnitAutonomyRecord> records, Span<byte> into)
	{
		int count = Math.Min(records.Length, MaxRecords);
		int size = PayloadBytes(count);
		if (into.Length < size)
		{
			return 0;
		}

		into[0] = (byte)teamDefault;
		BinaryPrimitives.WriteUInt16LittleEndian(into[1..], (ushort)count);

		int offset = HeaderBytes;
		for (int i = 0; i < count; i++)
		{
			BinaryPrimitives.WriteUInt16LittleEndian(into[offset..], records[i].UnitId);
			into[offset + 2] = (byte)records[i].Mode;
			offset += RecordBytes;
		}

		return size;
	}

	public static bool TryDecode(ReadOnlySpan<byte> payload, Span<UnitAutonomyRecord> into,
		out MicroAutonomy teamDefault, out int count)
	{
		teamDefault = MicroAutonomy.Off;
		count = 0;
		if (payload.Length < HeaderBytes || !MicroAutonomyNames.IsValid(payload[0]))
		{
			return false;
		}

		int records = BinaryPrimitives.ReadUInt16LittleEndian(payload[1..]);
		if (records > MaxRecords || payload.Length < PayloadBytes(records) || into.Length < records)
		{
			return false;
		}

		int offset = HeaderBytes;
		for (int i = 0; i < records; i++)
		{
			byte mode = payload[offset + 2];
			if (!MicroAutonomyNames.IsValid(mode))
			{
				return false;
			}

			into[i] = new UnitAutonomyRecord
			{
				UnitId = BinaryPrimitives.ReadUInt16LittleEndian(payload[offset..]),
				Mode = (MicroAutonomy)mode,
			};
			offset += RecordBytes;
		}

		teamDefault = (MicroAutonomy)payload[0];
		count = records;
		return true;
	}
}
