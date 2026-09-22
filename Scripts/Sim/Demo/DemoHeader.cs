using System;
using System.Buffers.Binary;
using System.Text;

namespace Gdpyr.Sim.Demo;

/// <summary>
/// What a demo says about itself before the first record (docs/DEMOS.md §2.1).
///
/// The rates are in here rather than assumed because they are protocol constants
/// (<see cref="SimConfig"/>): a demo recorded at 60 Hz and played back by a build
/// that ticks at anything else is not the same round, and a reader that can say
/// so is better than one that plays it slightly wrong. Same reasoning for the
/// seed — it is what makes the round's spread cones and bot aim reproducible, so
/// it belongs in the file even though nothing reads it back yet.
/// </summary>
public struct DemoHeader
{
	/// <summary>Bytes before the two strings: magic, six bytes of fields, then four numbers.</summary>
	public const int FixedBytes = DemoFormat.MagicBytes + 6 + 4 + 8 + 4 + 4;

	public byte Version;
	public DemoKind Kind;

	/// <summary>Simulation rate the demo was recorded at. Must equal this build's.</summary>
	public byte TickRate;

	/// <summary>Player snapshot rate, for a reader that wants to know the keyframe spacing.</summary>
	public byte SnapshotRate;

	/// <summary>Unit snapshot rate. See <see cref="SnapshotRate"/>.</summary>
	public byte UnitSnapshotRate;

	/// <summary>Reserved; written as 0 and ignored on read.</summary>
	public byte Flags;

	/// <summary>The first tick the recorder saw. Records are absolute, not relative to it.</summary>
	public uint StartTick;

	/// <summary>Wall clock when recording began, for a listing that sorts by date.</summary>
	public long RecordedAtUnixSeconds;

	/// <summary>The round's seed, as the authority had it (<c>CombatManager.RoundSeed</c>).</summary>
	public int RoundSeed;

	/// <summary>
	/// The peer whose process recorded this. Meaningful for a
	/// <see cref="DemoKind.Stream"/> demo, where it says whose eyes the snapshots
	/// were filtered for; 1 on an authority, which has everybody's.
	/// </summary>
	public int LocalPeerId;

	/// <summary>The map scene the round was played on.</summary>
	public string Map;

	/// <summary>Whatever the recording process calls itself. Free text, for a listing.</summary>
	public string RecordedBy;

	public static DemoHeader Create(DemoKind kind, uint startTick, long unixSeconds, int roundSeed,
		int localPeerId, string map, string recordedBy) => new()
	{
		Version = DemoFormat.Version,
		Kind = kind,
		TickRate = (byte)SimConfig.TickRate,
		SnapshotRate = (byte)SimConfig.SnapshotRate,
		UnitSnapshotRate = (byte)SimConfig.UnitSnapshotRate,
		Flags = 0,
		StartTick = startTick,
		RecordedAtUnixSeconds = unixSeconds,
		RoundSeed = roundSeed,
		LocalPeerId = localPeerId,
		Map = map ?? string.Empty,
		RecordedBy = recordedBy ?? string.Empty,
	};

	/// <summary>
	/// True when this build can play the demo back: the same format version and the
	/// same tick rate. A different tick rate is refused rather than scaled, because
	/// every recorded tick index would mean something else
	/// (<see cref="SimConfig.TickRate"/>).
	/// </summary>
	public readonly bool IsPlayable(out string reason)
	{
		if (Version != DemoFormat.Version)
		{
			reason = $"demo format version {Version}; this build reads {DemoFormat.Version}";
			return false;
		}

		if (TickRate != SimConfig.TickRate)
		{
			reason = $"recorded at {TickRate} Hz; this build ticks at {SimConfig.TickRate} Hz";
			return false;
		}

		if (Kind is not (DemoKind.Journal or DemoKind.Stream))
		{
			reason = $"unknown demo kind {(byte)Kind}";
			return false;
		}

		reason = null;
		return true;
	}

	public readonly byte[] Encode()
	{
		byte[] map = Clamp(Map);
		byte[] by = Clamp(RecordedBy);

		var bytes = new byte[FixedBytes + 1 + map.Length + 1 + by.Length];
		Span<byte> into = bytes;

		DemoFormat.Magic.CopyTo(into);
		int offset = DemoFormat.MagicBytes;

		into[offset++] = Version;
		into[offset++] = (byte)Kind;
		into[offset++] = TickRate;
		into[offset++] = SnapshotRate;
		into[offset++] = UnitSnapshotRate;
		into[offset++] = Flags;

		BinaryPrimitives.WriteUInt32LittleEndian(into[offset..], StartTick);
		offset += 4;
		BinaryPrimitives.WriteInt64LittleEndian(into[offset..], RecordedAtUnixSeconds);
		offset += 8;
		BinaryPrimitives.WriteInt32LittleEndian(into[offset..], RoundSeed);
		offset += 4;
		BinaryPrimitives.WriteInt32LittleEndian(into[offset..], LocalPeerId);
		offset += 4;

		into[offset++] = (byte)map.Length;
		map.CopyTo(into[offset..]);
		offset += map.Length;

		into[offset++] = (byte)by.Length;
		by.CopyTo(into[offset..]);

		return bytes;
	}

	/// <summary>
	/// Reads a header. Total, like every decoder here: a truncated or hostile file
	/// comes back false rather than throwing, and <paramref name="bytesRead"/> says
	/// where the record stream starts.
	/// </summary>
	public static bool TryDecode(ReadOnlySpan<byte> bytes, out DemoHeader header, out int bytesRead)
	{
		header = default;
		bytesRead = 0;

		if (!DemoFormat.HasMagic(bytes) || bytes.Length < FixedBytes + 2)
		{
			return false;
		}

		int offset = DemoFormat.MagicBytes;
		header.Version = bytes[offset++];
		header.Kind = (DemoKind)bytes[offset++];
		header.TickRate = bytes[offset++];
		header.SnapshotRate = bytes[offset++];
		header.UnitSnapshotRate = bytes[offset++];
		header.Flags = bytes[offset++];

		header.StartTick = BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]);
		offset += 4;
		header.RecordedAtUnixSeconds = BinaryPrimitives.ReadInt64LittleEndian(bytes[offset..]);
		offset += 8;
		header.RoundSeed = BinaryPrimitives.ReadInt32LittleEndian(bytes[offset..]);
		offset += 4;
		header.LocalPeerId = BinaryPrimitives.ReadInt32LittleEndian(bytes[offset..]);
		offset += 4;

		if (!TryReadText(bytes, ref offset, out header.Map)
			|| !TryReadText(bytes, ref offset, out header.RecordedBy))
		{
			return false;
		}

		bytesRead = offset;
		return true;
	}

	public override readonly string ToString() =>
		$"{Kind} demo, {TickRate} Hz, from tick {StartTick}"
		+ (string.IsNullOrEmpty(Map) ? string.Empty : $", {Map}")
		+ (string.IsNullOrEmpty(RecordedBy) ? string.Empty : $", recorded by {RecordedBy}");

	private static bool TryReadText(ReadOnlySpan<byte> bytes, ref int offset, out string text)
	{
		text = string.Empty;
		if (offset >= bytes.Length)
		{
			return false;
		}

		int length = bytes[offset++];
		if (offset + length > bytes.Length)
		{
			return false;
		}

		text = Encoding.UTF8.GetString(bytes.Slice(offset, length));
		offset += length;
		return true;
	}

	/// <summary>
	/// UTF-8 bytes, truncated to what a one-byte length can name. Truncation is on
	/// a character boundary: half a rune in a file that is otherwise fine is a
	/// listing with a replacement glyph in it forever.
	/// </summary>
	private static byte[] Clamp(string text)
	{
		if (string.IsNullOrEmpty(text))
		{
			return Array.Empty<byte>();
		}

		byte[] bytes = Encoding.UTF8.GetBytes(text);
		if (bytes.Length <= DemoFormat.MaxTextBytes)
		{
			return bytes;
		}

		int length = DemoFormat.MaxTextBytes;
		while (length > 0 && (bytes[length] & 0b1100_0000) == 0b1000_0000)
		{
			length--;
		}

		return bytes.AsSpan(0, length).ToArray();
	}
}
