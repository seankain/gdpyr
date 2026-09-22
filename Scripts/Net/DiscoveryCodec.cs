using System;
using System.Text;
using Gdpyr.Match;

namespace Gdpyr.Net;

/// <summary>
/// One server, as the browser found it. The address is not on the wire: it is
/// whatever the reply arrived from, because a host that has to be told its own
/// address is a host that gets it wrong.
/// </summary>
public struct ServerInfo
{
	/// <summary>What the host called itself. Never empty by the time a client sees one.</summary>
	public string Name;

	/// <summary>Filled in by <see cref="ServerBrowser"/> from the datagram's source.</summary>
	public string Address;

	/// <summary>The UDP port the game is on, which is not the port this reply came from.</summary>
	public int GamePort;

	/// <summary>Players in the roster, humans and bots alike, as the snapshot counts them.</summary>
	public int Players;

	/// <summary>How many of <see cref="Players"/> are computer players.</summary>
	public int Bots;

	public int MaxPlayers;

	public RoundPhase Phase;

	/// <summary>Seconds left on the round clock. Zero outside a live round.</summary>
	public int SecondsRemaining;

	public readonly string Endpoint => $"{Address}:{GamePort}";

	/// <summary>Humans in the roster. Derived, because the wire carries the two totals.</summary>
	public readonly int People => Math.Max(0, Players - Bots);
}

/// <summary>
/// The LAN discovery datagrams: a query broadcast by a client that is looking for
/// somewhere to play, and one reply per server that hears it.
///
/// Deliberately its own protocol and its own socket rather than anything the game
/// transport knows about. It answers one question — "who is listening on this
/// network?" — before there is a connection to ask it over, and it has to be
/// answerable by a server that would refuse a real peer.
///
/// Engine-free and total: every field of a datagram from an unknown source is
/// bounds-checked and nothing here throws, because the socket is open to whatever
/// else is on the Wi-Fi (docs/LAN.md §3.2).
/// </summary>
public static class DiscoveryCodec
{
	/// <summary>
	/// The lowest port a server advertises on. Above the game's default 7777 and the
	/// 7778 a second host on one machine takes (docs/LAN.md §3.2), so that the two
	/// ranges cannot collide.
	/// </summary>
	public const int PortBase = 7780;

	/// <summary>
	/// How many ports the range covers. One server per port: two hosts on one
	/// machine cannot share a UDP port, so a client asks all four and a machine can
	/// hold four servers before discovery stops finding the fifth.
	/// </summary>
	public const int PortSpan = 4;

	/// <summary>'G' 'D' 'P' 'Y'. Anything that does not start with this is not ours.</summary>
	public const uint Magic = 0x4744_5059;

	/// <summary>
	/// Bumped when the layout below changes. A client ignores a reply it cannot read
	/// rather than guessing, which is the difference between an old server being
	/// invisible and an old server being listed with nonsense in it.
	/// </summary>
	public const byte Version = 1;

	public const byte KindQuery = 0;
	public const byte KindReply = 1;

	/// <summary>Bytes of UTF-8 a server name may carry. Longer names are cut at a character boundary.</summary>
	public const int MaxNameBytes = 32;

	public const int QueryLength = 6;

	/// <summary>Header, port, four counters, the clock and the length byte.</summary>
	public const int ReplyHeaderLength = 15;

	public const int MaxReplyLength = ReplyHeaderLength + MaxNameBytes;

	public static byte[] EncodeQuery()
	{
		var buffer = new byte[QueryLength];
		WriteHeader(buffer, KindQuery);
		return buffer;
	}

	public static bool TryDecodeQuery(ReadOnlySpan<byte> packet) =>
		packet.Length >= QueryLength && ReadHeader(packet) == KindQuery;

	public static byte[] EncodeReply(in ServerInfo info)
	{
		byte[] name = TruncateUtf8(info.Name, MaxNameBytes);
		var buffer = new byte[ReplyHeaderLength + name.Length];

		WriteHeader(buffer, KindReply);
		WriteUInt16(buffer, 6, info.GamePort);
		buffer[8] = Clamp(info.Players);
		buffer[9] = Clamp(info.Bots);
		buffer[10] = Clamp(info.MaxPlayers);
		buffer[11] = (byte)info.Phase;
		WriteUInt16(buffer, 12, info.SecondsRemaining);
		buffer[14] = (byte)name.Length;
		name.CopyTo(buffer, ReplyHeaderLength);

		return buffer;
	}

	public static bool TryDecodeReply(ReadOnlySpan<byte> packet, out ServerInfo info)
	{
		info = default;

		if (packet.Length < ReplyHeaderLength || ReadHeader(packet) != KindReply)
		{
			return false;
		}

		int nameLength = packet[14];
		if (nameLength > MaxNameBytes || packet.Length < ReplyHeaderLength + nameLength)
		{
			// A truncated name is a truncated datagram: the rest of the record may be
			// whatever followed it in somebody else's buffer.
			return false;
		}

		info = new ServerInfo
		{
			Name = nameLength == 0
				? string.Empty
				: Encoding.UTF8.GetString(packet.Slice(ReplyHeaderLength, nameLength)),
			GamePort = ReadUInt16(packet, 6),
			Players = packet[8],
			Bots = packet[9],
			MaxPlayers = packet[10],
			Phase = SanitizePhase(packet[11]),
			SecondsRemaining = ReadUInt16(packet, 12),
		};

		// A port of zero is not a port, and it is the one field a client would act on
		// by dialling it.
		return info.GamePort > 0;
	}

	/// <summary>A phase byte that arrived from the wire. Anything unknown is warmup.</summary>
	private static RoundPhase SanitizePhase(byte phase) =>
		phase <= (byte)RoundPhase.Ended ? (RoundPhase)phase : RoundPhase.Warmup;

	/// <summary>
	/// The UTF-8 of <paramref name="text"/>, cut to <paramref name="maxBytes"/> at a
	/// character boundary. Cutting mid-sequence would put a replacement character on
	/// somebody's screen instead of the last letter of their hostname.
	/// </summary>
	public static byte[] TruncateUtf8(string text, int maxBytes)
	{
		byte[] bytes = Encoding.UTF8.GetBytes(text ?? string.Empty);
		if (bytes.Length <= maxBytes)
		{
			return bytes;
		}

		int end = maxBytes;
		while (end > 0 && (bytes[end] & 0xC0) == 0x80)
		{
			end--;
		}

		return bytes.AsSpan(0, end).ToArray();
	}

	private static void WriteHeader(byte[] buffer, byte kind)
	{
		buffer[0] = (byte)((Magic >> 24) & 0xFF);
		buffer[1] = (byte)((Magic >> 16) & 0xFF);
		buffer[2] = (byte)((Magic >> 8) & 0xFF);
		buffer[3] = (byte)(Magic & 0xFF);
		buffer[4] = Version;
		buffer[5] = kind;
	}

	/// <summary>The datagram's kind, or -1 when it is not one of ours or not this version.</summary>
	private static int ReadHeader(ReadOnlySpan<byte> packet)
	{
		uint magic = ((uint)packet[0] << 24) | ((uint)packet[1] << 16) | ((uint)packet[2] << 8) | packet[3];
		return magic == Magic && packet[4] == Version ? packet[5] : -1;
	}

	private static void WriteUInt16(byte[] buffer, int offset, int value)
	{
		int clamped = Math.Clamp(value, 0, ushort.MaxValue);
		buffer[offset] = (byte)(clamped >> 8);
		buffer[offset + 1] = (byte)clamped;
	}

	private static int ReadUInt16(ReadOnlySpan<byte> packet, int offset) =>
		(packet[offset] << 8) | packet[offset + 1];

	private static byte Clamp(int value) => (byte)Math.Clamp(value, 0, byte.MaxValue);
}
