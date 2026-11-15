using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace Gdpyr.Sim.Level;

/// <summary>What a server does with a peer's request for its map (docs/LEVEL_EDITOR.md §6.10).</summary>
public enum MapRequestAnswer : byte
{
	/// <summary>The peer is sent the map, a chunk a tick.</summary>
	Sending,

	/// <summary>The peer asked for a map this server is not playing: it is sent nothing.</summary>
	NotThisMap,

	/// <summary>The peer has asked once on this connection already: it is sent nothing more.</summary>
	AlreadyAsked,
}

/// <summary>Where a fetch stands (docs/LEVEL_EDITOR.md §6.10).</summary>
public enum MapReceiveState : byte
{
	/// <summary>Chunks are arriving, or are awaited.</summary>
	Receiving,

	/// <summary>Every chunk is in, the bytes hash to what the server named, read as a map and validate.</summary>
	Complete,

	/// <summary>Refused, with its reason: nothing of it is kept.</summary>
	Refused,
}

/// <summary>
/// A map on the wire (docs/LEVEL_EDITOR.md §6.10, §9.3): the file in chunks of 1,024 B, each behind
/// its index as a little-endian <c>u32</c>, sent reliably, in order, on a channel of their own.
/// Godot sends a message whose only argument is a byte array as those bytes behind a header of at
/// most 7 B (§3), so a chunk is at most 1,035 B: under ENet's 1,392 B MTU, one datagram and never
/// a fragment.
///
/// The sender and the receiver are state machines over those chunks, with no socket in them
/// (<see cref="MapSender"/>, <see cref="MapReceiver"/>), so what a receiver does with a short,
/// long, repeated, out-of-order or never-finished stream is a test (<c>MapTransferTests</c>).
/// Decided by LE7.
/// </summary>
public static class MapTransfer
{
	/// <summary>The file's bytes a chunk carries; the last carries what is left.</summary>
	public const int ChunkBytes = 1024;

	/// <summary>The chunk's index ahead of its bytes.</summary>
	public const int IndexBytes = 4;

	/// <summary>The most a chunk's message holds.</summary>
	public const int MaxPacketBytes = IndexBytes + ChunkBytes;

	/// <summary>The most Godot puts ahead of a message that is one byte array (§3).</summary>
	public const int RpcHeaderBytes = 7;

	/// <summary>ENet's MTU at its default (§3): a chunk's datagram stays under it.</summary>
	public const int EnetMtuBytes = 1392;

	/// <summary>A chunk a tick to a peer: 61,440 B/s at 60 Hz.</summary>
	public const int ChunksPerTickToAPeer = 1;

	/// <summary>
	/// The most chunks a tick across every peer that is fetching: 4 × 1,024 B × 60 Hz is
	/// 245,760 B/s, under the 256 KB/s a server spends on maps at the most.
	/// </summary>
	public const int MaxChunksPerTick = 4;

	/// <summary>A stream that brings no chunk for this long is refused.</summary>
	public const double StallSeconds = 10.0;

	/// <summary>The random bytes a server sends each connection with its map, for an upload's proof (LE8).</summary>
	public const int NonceBytes = 16;

	/// <summary>The chunks a file of <paramref name="length"/> bytes is sent in.</summary>
	public static int ChunkCount(int length) => length <= 0 ? 0 : ((length - 1) / ChunkBytes) + 1;

	/// <summary>The file's bytes in chunk <paramref name="index"/>: 1,024 but in the last.</summary>
	public static int ChunkLength(int length, int index)
	{
		int count = ChunkCount(length);
		if (index < 0 || index >= count)
		{
			return 0;
		}

		return index < count - 1 ? ChunkBytes : length - ((count - 1) * ChunkBytes);
	}

	/// <summary>Chunk <paramref name="index"/> of a file, behind its index.</summary>
	public static byte[] Packet(ReadOnlySpan<byte> file, int index)
	{
		int length = ChunkLength(file.Length, index);
		if (length == 0)
		{
			throw new ArgumentOutOfRangeException(nameof(index), $"a file of {file.Length} B has no chunk {index}");
		}

		var packet = new byte[IndexBytes + length];
		BinaryPrimitives.WriteUInt32LittleEndian(packet, (uint)index);
		file.Slice(index * ChunkBytes, length).CopyTo(packet.AsSpan(IndexBytes));
		return packet;
	}

	/// <summary>Whether a text is a map's hash as the wire names it: 64 lower-case hex digits (LE-D15).</summary>
	public static bool IsHash(string text)
	{
		if (text == null || text.Length != MapNames.HashHexLength)
		{
			return false;
		}

		foreach (char c in text)
		{
			if (c is not ((>= '0' and <= '9') or (>= 'a' and <= 'f')))
			{
				return false;
			}
		}

		return true;
	}

	/// <summary>The menu's status line while a map arrives (§9.3): <c>fetching 'ridge' 47 / 114</c>.</summary>
	public static string Progress(string name, int received, int chunks) =>
		$"fetching '{name}' {received} / {chunks}";

	/// <summary>The first eight digits of a hash, for a line a person reads.</summary>
	public static string Short(string hash) => hash is { Length: >= 8 } ? hash[..8] + "…" : hash ?? string.Empty;
}

/// <summary>
/// A server's half (docs/LEVEL_EDITOR.md §6.10): the map it is playing, from the bytes it loaded —
/// a copy, so a file saved again under a running server changes nothing — sent only to a peer
/// that asked for it by its hash, once a connection, a chunk a tick to a peer and at most
/// <see cref="MapTransfer.MaxChunksPerTick"/> a tick across peers, the peers taken in turn.
/// </summary>
public sealed class MapSender
{
	private readonly byte[] _file;

	/// <summary>Every peer that has asked on this connection, served or not.</summary>
	private readonly HashSet<int> _asked = new();

	/// <summary>Each peer being sent the map: the next chunk it is owed.</summary>
	private readonly Dictionary<int, int> _next = new();

	/// <summary>The peers being sent the map, in the order they are next served.</summary>
	private readonly List<int> _turns = new();

	/// <summary>What each peer has been sent, finished or not.</summary>
	private readonly Dictionary<int, int> _sent = new();

	public MapSender(byte[] file)
	{
		if (file == null || file.Length == 0)
		{
			throw new ArgumentException("a map is not empty", nameof(file));
		}

		_file = (byte[])file.Clone();
		Hash = MapFile.HashHex(_file);
		Chunks = MapTransfer.ChunkCount(_file.Length);
	}

	/// <summary>The SHA-256 of the bytes it sends, lower-case hex: what <c>ServerMap</c> names the map by.</summary>
	public string Hash { get; }

	public int Length => _file.Length;

	public int Chunks { get; }

	/// <summary>Peers the map is being sent to now.</summary>
	public int Sending => _turns.Count;

	/// <summary>
	/// A peer asks for a map by its hash. It is sent this one only when the hash is this one's,
	/// and only the first time it asks on its connection: a second request, whatever it names, is
	/// answered with nothing.
	/// </summary>
	public MapRequestAnswer Request(int peer, string hash)
	{
		if (!_asked.Add(peer))
		{
			return MapRequestAnswer.AlreadyAsked;
		}

		if (!string.Equals(hash, Hash, StringComparison.Ordinal))
		{
			return MapRequestAnswer.NotThisMap;
		}

		_next[peer] = 0;
		_sent[peer] = 0;
		_turns.Add(peer);
		return MapRequestAnswer.Sending;
	}

	/// <summary>A peer's connection is gone: what it asked and was sent is forgotten with it.</summary>
	public void Forget(int peer)
	{
		_asked.Remove(peer);
		_next.Remove(peer);
		_sent.Remove(peer);
		_turns.Remove(peer);
	}

	/// <summary>How many chunks a peer has been sent.</summary>
	public int SentTo(int peer) => _sent.TryGetValue(peer, out int sent) ? sent : 0;

	/// <summary>Whether a peer is still owed chunks.</summary>
	public bool IsSendingTo(int peer) => _turns.Contains(peer);

	/// <summary>
	/// This tick's chunks, as (peer, index): one to each of the next
	/// <see cref="MapTransfer.MaxChunksPerTick"/> peers in turn, each then put at the back of the
	/// line until it has every chunk.
	/// </summary>
	public void Tick(List<(int Peer, int Index)> into)
	{
		into.Clear();
		int serve = Math.Min(_turns.Count, MapTransfer.MaxChunksPerTick);
		for (int i = 0; i < serve; i++)
		{
			int peer = _turns[0];
			_turns.RemoveAt(0);
			int index = _next[peer];
			into.Add((peer, index));
			_next[peer] = index + 1;
			_sent[peer] = index + 1;
			if (index + 1 < Chunks)
			{
				_turns.Add(peer);
			}
		}
	}

	/// <summary>Chunk <paramref name="index"/> of the bytes it loaded, behind its index.</summary>
	public byte[] Packet(int index) => MapTransfer.Packet(_file, index);
}

/// <summary>
/// A client's half (docs/LEVEL_EDITOR.md §6.10): it knows the length and the hash before the first
/// chunk, from <c>ServerMap</c>. It refuses a length over <see cref="MapLimits.MaxFileBytes"/>; a
/// chunk that is not one, past the length, out of turn or of the wrong size; and a stream that
/// stalls for <see cref="MapTransfer.StallSeconds"/>. At the last chunk it hashes the bytes,
/// reads them (§6.8) and validates them (§6.9), and only a map that passes all three is
/// <see cref="MapReceiveState.Complete"/>: whole, the server's, and one this build plays.
/// A refusal keeps nothing, and nothing after it changes it.
/// </summary>
public sealed class MapReceiver
{
	private byte[] _buffer;
	private double _quiet;

	private MapReceiver(string hash, int length)
	{
		Hash = hash;
		Length = length;
		Chunks = MapTransfer.ChunkCount(length);
		_buffer = new byte[length];
	}

	/// <summary>
	/// A fetch of the map <c>ServerMap</c> named. False, with why, when no map can be that: a hash
	/// that is not one, or a length of nothing or past what a map may be.
	/// </summary>
	public static bool TryStart(string hash, int length, out MapReceiver receiver, out string error)
	{
		receiver = null;
		if (!MapTransfer.IsHash(hash))
		{
			error = "the host named its map by something that is not a hash";
			return false;
		}

		if (length <= 0)
		{
			error = $"the host's map is {length} bytes long";
			return false;
		}

		if (length > MapLimits.MaxFileBytes)
		{
			error = $"the host's map is {length:N0} bytes, over the {MapLimits.MaxFileBytes:N0} a map may be";
			return false;
		}

		error = null;
		receiver = new MapReceiver(hash, length);
		return true;
	}

	/// <summary>What the server named the map by: its bytes must hash to this.</summary>
	public string Hash { get; }

	public int Length { get; }

	public int Chunks { get; }

	/// <summary>Chunks taken, in turn.</summary>
	public int Received { get; private set; }

	public MapReceiveState State { get; private set; } = MapReceiveState.Receiving;

	/// <summary>Why it was refused; null otherwise.</summary>
	public string Error { get; private set; }

	/// <summary>The file, once <see cref="MapReceiveState.Complete"/>; null otherwise.</summary>
	public byte[] File { get; private set; }

	/// <summary>The map the file reads as, once complete; null otherwise.</summary>
	public MapDocument Document { get; private set; }

	/// <summary>What the validator found in it, once complete: no error, perhaps warnings.</summary>
	public MapReport Report { get; private set; }

	/// <summary>Takes a chunk's message. Anything once the fetch is over is let go of.</summary>
	public MapReceiveState Accept(ReadOnlySpan<byte> packet)
	{
		if (State != MapReceiveState.Receiving)
		{
			return State;
		}

		if (packet.Length <= MapTransfer.IndexBytes || packet.Length > MapTransfer.MaxPacketBytes)
		{
			return Refuse($"a message of {packet.Length} B is not a chunk of a map");
		}

		uint index = BinaryPrimitives.ReadUInt32LittleEndian(packet);
		if (index >= (uint)Chunks)
		{
			return Refuse($"chunk {index} is past the map's {Chunks}");
		}

		if (index != (uint)Received)
		{
			return Refuse($"chunk {index} arrived out of turn: {Received} was next");
		}

		int expected = MapTransfer.ChunkLength(Length, (int)index);
		ReadOnlySpan<byte> data = packet[MapTransfer.IndexBytes..];
		if (data.Length != expected)
		{
			return Refuse($"chunk {index} is {data.Length} B, not {expected}");
		}

		data.CopyTo(_buffer.AsSpan((int)index * MapTransfer.ChunkBytes));
		Received++;
		_quiet = 0;
		return Received == Chunks ? Finish() : State;
	}

	/// <summary>Time passes with nothing arriving: past <see cref="MapTransfer.StallSeconds"/>, the stream is refused.</summary>
	public MapReceiveState Advance(double seconds)
	{
		if (State != MapReceiveState.Receiving || !(seconds > 0))
		{
			return State;
		}

		_quiet += seconds;
		return _quiet >= MapTransfer.StallSeconds
			? Refuse($"the map stopped arriving: {Received} of {Chunks} chunks, then nothing for {MapTransfer.StallSeconds:0} s")
			: State;
	}

	/// <summary>Hashed, read, validated: only then is it the map.</summary>
	private MapReceiveState Finish()
	{
		string hash = MapFile.HashHex(_buffer);
		if (!string.Equals(hash, Hash, StringComparison.Ordinal))
		{
			return Refuse($"the bytes that arrived hash to {MapTransfer.Short(hash)}, not the {MapTransfer.Short(Hash)} the host named");
		}

		if (!MapFile.TryRead(_buffer, out MapDocument document, out string error))
		{
			return Refuse($"the host's map does not read: {error}");
		}

		MapReport report = MapValidator.Validate(document);
		if (!report.IsPlayable)
		{
			var reasons = new List<string>();
			foreach (MapIssue issue in report.Issues)
			{
				if (issue.Severity == MapIssueSeverity.Error)
				{
					reasons.Add(issue.Message);
				}
			}

			return Refuse($"the host's map does not play on this build: {string.Join("; ", reasons)}");
		}

		File = _buffer;
		Document = document;
		Report = report;
		State = MapReceiveState.Complete;
		return State;
	}

	private MapReceiveState Refuse(string why)
	{
		State = MapReceiveState.Refused;
		Error = why;
		_buffer = null;
		File = null;
		Document = null;
		Report = null;
		return State;
	}
}
