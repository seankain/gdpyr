using System;
using System.Buffers.Binary;

namespace Gdpyr.Sim;

/// <summary>One player's round, as the scoreboard shows it.</summary>
public struct ScoreEntry
{
	/// <summary>Bytes on the wire: 4 + 2 + 2 + 1.</summary>
	public const int SizeBytes = 9;

	public int PeerId;

	public short Kills;

	public short Deaths;

	public Team Team;

	/// <summary>
	/// True for a computer player. A bot is a player everywhere else in this design
	/// (docs/NETCODE.md §9) and is one here too — it takes a row, it has a score —
	/// but a scoreboard that does not say which rows were people is a scoreboard M8
	/// cannot read a playtest off.
	/// </summary>
	public bool IsBot;
}

/// <summary>The round, as one line above the table.</summary>
public struct RoundSummary
{
	/// <summary>Bytes on the wire: 1 + 1 + 2 + 4 + 2 + 2 + 4.</summary>
	public const int SizeBytes = 16;

	/// <summary>
	/// The round's outcome. A byte here rather than the enum, because
	/// <c>Match.RoundOutcome</c> belongs to the round and <c>Scripts/Sim</c> does not
	/// depend on <c>Scripts/Match</c> — the same arrangement every other enum on this
	/// wire has.
	/// </summary>
	public byte Outcome;

	public short GroundTickets;

	public int StrategistPoints;

	public ushort UnitsBuilt;

	public ushort UnitsLost;

	/// <summary>How long the round lasted, in ticks. M8's first column.</summary>
	public uint RoundTicks;

	public float Minutes => RoundTicks / (float)SimConfig.TickRate / 60f;
}

/// <summary>
/// Packs the end-of-round scoreboard (docs/IMPLEMENTATION_PLAN.md §M5): the
/// round's summary, then one fixed-size row per player.
///
/// One reliable message per round, which is the only reason kills and deaths are
/// on the wire at all — they are server-side counters everywhere else, and a
/// snapshot carrying them thirty times a second would be paying a continuous
/// price for a table that is looked at once.
///
/// Decoding is total, like every other codec here: a malformed payload returns
/// false rather than throwing.
/// </summary>
public static class ScoreboardCodec
{
	/// <summary>Rows in one scoreboard. The same bound the player snapshot has, for the same reason.</summary>
	public const int MaxEntries = SnapshotCodec.MaxPlayers;

	public const int HeaderBytes = RoundSummary.SizeBytes;

	public static int PayloadBytes(int entries) => HeaderBytes + (entries * ScoreEntry.SizeBytes);

	public const int MaxPayloadBytes = HeaderBytes + (MaxEntries * ScoreEntry.SizeBytes);

	public static byte[] Encode(in RoundSummary summary, ReadOnlySpan<ScoreEntry> entries)
	{
		int count = Math.Min(entries.Length, MaxEntries);
		var payload = new byte[PayloadBytes(count)];

		payload[0] = summary.Outcome;
		payload[1] = (byte)count;
		BinaryPrimitives.WriteInt16LittleEndian(payload.AsSpan(2), summary.GroundTickets);
		BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(4), summary.StrategistPoints);
		BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(8), summary.UnitsBuilt);
		BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(10), summary.UnitsLost);
		BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(12), summary.RoundTicks);

		int offset = HeaderBytes;
		for (int i = 0; i < count; i++)
		{
			ref readonly ScoreEntry entry = ref entries[i];
			BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(offset), entry.PeerId);
			BinaryPrimitives.WriteInt16LittleEndian(payload.AsSpan(offset + 4), entry.Kills);
			BinaryPrimitives.WriteInt16LittleEndian(payload.AsSpan(offset + 6), entry.Deaths);
			payload[offset + 8] = Pack(entry);
			offset += ScoreEntry.SizeBytes;
		}

		return payload;
	}

	/// <summary>
	/// Unpacks a scoreboard into <paramref name="into"/>. False for anything that is
	/// not exactly one well-formed payload — a short buffer, a count that does not
	/// match the length, a row that would not fit.
	/// </summary>
	public static bool TryDecode(ReadOnlySpan<byte> payload, ScoreEntry[] into, out RoundSummary summary,
		out int count)
	{
		summary = default;
		count = 0;

		if (into == null || payload.Length < HeaderBytes)
		{
			return false;
		}

		int entries = payload[1];
		if (entries > MaxEntries || entries > into.Length || payload.Length < PayloadBytes(entries))
		{
			return false;
		}

		summary = new RoundSummary
		{
			Outcome = payload[0],
			GroundTickets = BinaryPrimitives.ReadInt16LittleEndian(payload[2..]),
			StrategistPoints = BinaryPrimitives.ReadInt32LittleEndian(payload[4..]),
			UnitsBuilt = BinaryPrimitives.ReadUInt16LittleEndian(payload[8..]),
			UnitsLost = BinaryPrimitives.ReadUInt16LittleEndian(payload[10..]),
			RoundTicks = BinaryPrimitives.ReadUInt32LittleEndian(payload[12..]),
		};

		int offset = HeaderBytes;
		for (int i = 0; i < entries; i++)
		{
			byte flags = payload[offset + 8];
			into[i] = new ScoreEntry
			{
				PeerId = BinaryPrimitives.ReadInt32LittleEndian(payload[offset..]),
				Kills = BinaryPrimitives.ReadInt16LittleEndian(payload[(offset + 4)..]),
				Deaths = BinaryPrimitives.ReadInt16LittleEndian(payload[(offset + 6)..]),
				Team = (flags & 1) != 0 ? Team.Strategist : Team.GroundForce,
				IsBot = (flags & 2) != 0,
			};
			offset += ScoreEntry.SizeBytes;
		}

		count = entries;
		return true;
	}

	private static byte Pack(in ScoreEntry entry) =>
		(byte)((entry.Team == Team.Strategist ? 1 : 0) | (entry.IsBot ? 2 : 0));
}
