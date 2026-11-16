using System;
using System.Text;

namespace Gdpyr.Sim.Level;

/// <summary>What a server answers a map offered to it (docs/LEVEL_EDITOR.md §9.5). The numbers are the wire's.</summary>
public enum MapOfferAnswer : byte
{
	/// <summary><c>ok</c>: send it. The server takes its chunks, from this peer alone, until it is whole or refused.</summary>
	Ok = 0,

	/// <summary><c>have_it</c>: the server has a map with this hash, and it plays there: there is nothing to send.</summary>
	HaveIt = 1,

	/// <summary><c>uploads_off</c>: the server was started with no token, and takes no map from anyone.</summary>
	UploadsOff = 2,

	/// <summary><c>bad_proof</c>: the proof is not this token's for this map on this connection.</summary>
	BadProof = 3,

	/// <summary><c>too_large</c>: no map is that long: nothing, or over <see cref="MapLimits.MaxFileBytes"/>.</summary>
	TooLarge = 4,

	/// <summary><c>busy</c>: the server is taking an upload, and takes one at a time.</summary>
	Busy = 5,
}

/// <summary>What a server answers a request to play a map when it next starts (docs/LEVEL_EDITOR.md §9.5). The numbers are the wire's.</summary>
public enum MapNextAnswer : byte
{
	/// <summary><c>ok</c>: <c>maps/active</c> names it; the process exits when its round ends and its supervisor starts it again.</summary>
	Ok = 0,

	/// <summary><c>ok_stays_down</c>: as <see cref="Ok"/>, and nothing here starts the process again: it was run by hand.</summary>
	OkStaysDown = 1,

	/// <summary><c>playing</c>: it is the map being played. <c>maps/active</c> names it, and nothing restarts.</summary>
	Playing = 2,

	/// <summary><c>uploads_off</c>: the server was started with no token, and is told nothing by anyone.</summary>
	UploadsOff = 3,

	/// <summary><c>bad_proof</c>: the proof is not this token's for this map and this request on this connection.</summary>
	BadProof = 4,

	/// <summary><c>not_here</c>: the server has no map with this hash that plays on its build.</summary>
	NotHere = 5,

	/// <summary><c>pinned</c>: the server was started with <c>--map</c>, and plays that map whenever it starts.</summary>
	Pinned = 6,

	/// <summary><c>not_written</c>: the server's disk did not take <c>maps/active</c>: nothing changes.</summary>
	NotWritten = 7,
}

/// <summary>
/// The answers as the wire and a person have them (docs/LEVEL_EDITOR.md §9.5): a number off the wire
/// that is no answer this build knows is not one, and each answer has §9.5's name and a line for the
/// editor to say.
/// </summary>
public static class MapAnswers
{
	/// <summary>The most of a server's own words a client shows.</summary>
	public const int MaxReasonLength = 240;

	public static bool TryOffer(int wire, out MapOfferAnswer answer)
	{
		answer = (MapOfferAnswer)wire;
		return wire is >= (int)MapOfferAnswer.Ok and <= (int)MapOfferAnswer.Busy;
	}

	public static bool TryNext(int wire, out MapNextAnswer answer)
	{
		answer = (MapNextAnswer)wire;
		return wire is >= (int)MapNextAnswer.Ok and <= (int)MapNextAnswer.NotWritten;
	}

	/// <summary>§9.5's name for an answer.</summary>
	public static string Name(MapOfferAnswer answer) => answer switch
	{
		MapOfferAnswer.Ok => "ok",
		MapOfferAnswer.HaveIt => "have_it",
		MapOfferAnswer.UploadsOff => "uploads_off",
		MapOfferAnswer.BadProof => "bad_proof",
		MapOfferAnswer.TooLarge => "too_large",
		MapOfferAnswer.Busy => "busy",
		_ => $"unknown ({(int)answer})",
	};

	public static string Name(MapNextAnswer answer) => answer switch
	{
		MapNextAnswer.Ok => "ok",
		MapNextAnswer.OkStaysDown => "ok_stays_down",
		MapNextAnswer.Playing => "playing",
		MapNextAnswer.UploadsOff => "uploads_off",
		MapNextAnswer.BadProof => "bad_proof",
		MapNextAnswer.NotHere => "not_here",
		MapNextAnswer.Pinned => "pinned",
		MapNextAnswer.NotWritten => "not_written",
		_ => $"unknown ({(int)answer})",
	};

	/// <summary>What an offer's answer means, for the person who made it.</summary>
	public static string Describe(MapOfferAnswer answer) => answer switch
	{
		MapOfferAnswer.Ok => "the server takes it",
		MapOfferAnswer.HaveIt => "the server has this map already",
		MapOfferAnswer.UploadsOff => "the server takes no uploads: it was started with no --map-upload-token",
		MapOfferAnswer.BadProof => "the server refused the token",
		MapOfferAnswer.TooLarge => $"the server takes no map that long: {MapLimits.MaxFileBytes:N0} bytes at the most",
		MapOfferAnswer.Busy => "the server is taking another upload: try again in a moment",
		_ => Unknown((int)answer),
	};

	/// <summary>What a next-map request's answer means, for the person who made it.</summary>
	public static string Describe(MapNextAnswer answer) => answer switch
	{
		MapNextAnswer.Ok => "the server changes to it when its round ends: everybody is dropped, and joins again",
		MapNextAnswer.OkStaysDown => "the server stops when its round ends, and nothing there starts it again:"
			+ " it plays this map the next time somebody starts it",
		MapNextAnswer.Playing => "the server is playing it now",
		MapNextAnswer.UploadsOff => "the server is told nothing: it was started with no --map-upload-token",
		MapNextAnswer.BadProof => "the server refused the token",
		MapNextAnswer.NotHere => "the server has no such map that plays on its build",
		MapNextAnswer.Pinned => "the server was started with --map, and plays that map whenever it starts",
		MapNextAnswer.NotWritten => "the server's disk did not take it: nothing changes",
		_ => Unknown((int)answer),
	};

	/// <summary>What is said of a number off the wire that is no answer this build knows.</summary>
	public static string Unknown(int wire) => $"the server gave an answer this build does not know ({wire})";

	/// <summary>Whether a next-map request was taken: <c>maps/active</c> names the map.</summary>
	public static bool Accepted(MapNextAnswer answer) =>
		answer is MapNextAnswer.Ok or MapNextAnswer.OkStaysDown or MapNextAnswer.Playing;

	/// <summary>
	/// A server's own words, for a client to show: one line, no control character, at most
	/// <see cref="MaxReasonLength"/> characters. What comes off the wire is shown, never trusted.
	/// </summary>
	public static string Clean(string text)
	{
		if (string.IsNullOrEmpty(text))
		{
			return string.Empty;
		}

		var clean = new StringBuilder(Math.Min(text.Length, MaxReasonLength));
		foreach (char c in text)
		{
			if (clean.Length == MaxReasonLength)
			{
				clean[^1] = '…';
				break;
			}

			clean.Append(char.IsControl(c) ? ' ' : c);
		}

		return clean.ToString();
	}
}

/// <summary>
/// A server's side of an upload and of a next-map request (docs/LEVEL_EDITOR.md §9.5, LE8), with no
/// socket and no disk in it: who is answered what, and whose chunks are taken.
///
/// With no token it takes nothing from anyone and is told nothing by anyone: every offer and every
/// request is answered <c>uploads_off</c>, and every chunk is dropped. With one, a peer is asked for
/// the token's proof over its own connection's nonce before it is told anything else — whether the
/// server has a map, or is busy, is not for a peer without the token to learn. It takes one upload at
/// a time, from the peer whose offer it took, through a <see cref="MapReceiver"/>: hashed, read and
/// validated before anything is kept.
/// </summary>
public sealed class MapUploadGate
{
	private readonly string _token;

	/// <param name="token">The server's <c>--map-upload-token</c>; null or empty for a server that takes no uploads.</param>
	public MapUploadGate(string token)
	{
		_token = string.IsNullOrEmpty(token) ? null : token;
	}

	/// <summary>Whether this server takes uploads at all: it was given a token.</summary>
	public bool Enabled => _token != null;

	/// <summary>The peer whose upload is being taken; 0 when none is.</summary>
	public int UploaderPeer { get; private set; }

	/// <summary>The upload being taken; null when none is.</summary>
	public MapReceiver Receiving { get; private set; }

	/// <summary>
	/// A peer offers a map: its hash, its length, and the proof over its connection's nonce. In this
	/// order: <c>uploads_off</c>; <c>bad_proof</c>; <c>too_large</c>; <c>have_it</c>, when
	/// <paramref name="has"/> says the server has a map with that hash; <c>busy</c>, while another
	/// upload is being taken — this peer's own among them; else <c>ok</c>, and the peer's chunks are
	/// taken.
	/// </summary>
	public MapOfferAnswer Offer(int peer, ReadOnlySpan<byte> nonce, string hash, int length, ReadOnlySpan<byte> proof,
		Func<string, bool> has)
	{
		if (!Enabled)
		{
			return MapOfferAnswer.UploadsOff;
		}

		if (!MapProof.Verify(_token, nonce, hash, MapProofOp.Upload, proof))
		{
			return MapOfferAnswer.BadProof;
		}

		if (length <= 0 || length > MapLimits.MaxFileBytes)
		{
			return MapOfferAnswer.TooLarge;
		}

		if (has != null && has(hash))
		{
			return MapOfferAnswer.HaveIt;
		}

		if (UploaderPeer != 0)
		{
			return MapOfferAnswer.Busy;
		}

		UploaderPeer = peer;
		Receiving = MapReceiver.StartUpload(hash, length);
		return MapOfferAnswer.Ok;
	}

	/// <summary>
	/// A chunk from a peer. Taken only from the peer whose offer was taken, while its upload is still
	/// arriving; from anybody else, and at any other time, it is dropped, and false is returned.
	/// </summary>
	public bool Chunk(int peer, ReadOnlySpan<byte> packet)
	{
		if (peer == 0 || peer != UploaderPeer || Receiving is not { State: MapReceiveState.Receiving })
		{
			return false;
		}

		Receiving.Accept(packet);
		return true;
	}

	/// <summary>Time passes: an upload that brings no chunk for <see cref="MapTransfer.StallSeconds"/> is refused.</summary>
	public void Advance(double seconds) => Receiving?.Advance(seconds);

	/// <summary>Whether the upload being taken is over: whole and valid, or refused.</summary>
	public bool Finished => Receiving is { State: not MapReceiveState.Receiving };

	/// <summary>Lets go of the upload, whole or not, and takes the next offer. Returns what it was.</summary>
	public MapReceiver Release(out int peer)
	{
		MapReceiver receiver = Receiving;
		peer = UploaderPeer;
		Receiving = null;
		UploaderPeer = 0;
		return receiver;
	}

	/// <summary>A peer's connection is gone: an upload of its own is let go of with it. True when it had one.</summary>
	public bool Forget(int peer)
	{
		if (peer == 0 || peer != UploaderPeer)
		{
			return false;
		}

		Release(out _);
		return true;
	}

	/// <summary>
	/// A peer names the map to play next: its hash, and the proof for that over its connection's
	/// nonce. In this order: <c>uploads_off</c>; <c>bad_proof</c>; <c>pinned</c>, on a server started
	/// with <c>--map</c>; <c>not_here</c>, unless <paramref name="plays"/> says the server has that map
	/// and it plays; <c>playing</c>, when it is the map being played; else <c>ok</c>, or
	/// <c>ok_stays_down</c> on a server nothing restarts. Writing <c>maps/active</c> is the caller's,
	/// for the three that are taken (<see cref="MapAnswers.Accepted"/>).
	/// </summary>
	/// <param name="playing">The hash of the data map being played; null on a scene map.</param>
	public MapNextAnswer Next(ReadOnlySpan<byte> nonce, string hash, ReadOnlySpan<byte> proof, Func<string, bool> plays,
		string playing, bool pinned, bool supervised)
	{
		if (!Enabled)
		{
			return MapNextAnswer.UploadsOff;
		}

		if (!MapProof.Verify(_token, nonce, hash, MapProofOp.SetNext, proof))
		{
			return MapNextAnswer.BadProof;
		}

		if (pinned)
		{
			return MapNextAnswer.Pinned;
		}

		if (plays == null || !plays(hash))
		{
			return MapNextAnswer.NotHere;
		}

		if (string.Equals(hash, playing, StringComparison.Ordinal))
		{
			return MapNextAnswer.Playing;
		}

		return supervised ? MapNextAnswer.Ok : MapNextAnswer.OkStaysDown;
	}
}

/// <summary>Where a client's upload stands (docs/LEVEL_EDITOR.md §9.5).</summary>
public enum MapUploadState : byte
{
	/// <summary>The map is offered, and the server has not answered.</summary>
	Offered,

	/// <summary>The server took the offer: the chunks are going, one a tick.</summary>
	Sending,

	/// <summary>Every chunk is sent, and the server has not said what became of them.</summary>
	Sent,

	/// <summary>The server hashed, read, validated and kept it.</summary>
	Stored,

	/// <summary>The server had it already: nothing was sent.</summary>
	HaveIt,

	/// <summary>The server did not take it, or did not keep it, or never answered: <see cref="MapUploader.Error"/> says which.</summary>
	Refused,
}

/// <summary>
/// A client's side of an upload (docs/LEVEL_EDITOR.md §9.5, LE8), with no socket in it: a map
/// offered, the server's answer, the chunks one a tick — 1,024 B behind their index, as a fetch's
/// are — and what the server says became of them. From a copy of the bytes it was given. A server
/// that says nothing for <see cref="MapTransfer.StallSeconds"/>, to the offer or after the last
/// chunk, is one that will not.
/// </summary>
public sealed class MapUploader
{
	private readonly byte[] _file;
	private double _quiet;

	public MapUploader(byte[] file)
	{
		if (file == null || file.Length == 0)
		{
			throw new ArgumentException("a map is not empty", nameof(file));
		}

		_file = (byte[])file.Clone();
		Hash = MapFile.HashHex(_file);
		Chunks = MapTransfer.ChunkCount(_file.Length);
	}

	/// <summary>The SHA-256 of the bytes it sends, lower-case hex: what the offer names them by.</summary>
	public string Hash { get; }

	public int Length => _file.Length;

	public int Chunks { get; }

	/// <summary>Chunks handed over to be sent.</summary>
	public int Sent { get; private set; }

	public MapUploadState State { get; private set; } = MapUploadState.Offered;

	/// <summary>What the server answered the offer; null until it has.</summary>
	public MapOfferAnswer? Answer { get; private set; }

	/// <summary>Why it is <see cref="MapUploadState.Refused"/>; null otherwise.</summary>
	public string Error { get; private set; }

	/// <summary>Whether it is over: kept, there already, or refused.</summary>
	public bool Done => State is MapUploadState.Stored or MapUploadState.HaveIt or MapUploadState.Refused;

	/// <summary>Whether the server has the map now: it kept this upload, or had it already.</summary>
	public bool ServerHasIt => State is MapUploadState.Stored or MapUploadState.HaveIt;

	/// <summary>The server's answer to the offer, as it came off the wire. Only the first counts.</summary>
	public void Answered(int wire)
	{
		if (State != MapUploadState.Offered)
		{
			return;
		}

		// Answered: what it waits for next, once the last chunk is gone, is waited for from then.
		_quiet = 0;
		if (!MapAnswers.TryOffer(wire, out MapOfferAnswer answer))
		{
			Refuse(MapAnswers.Unknown(wire));
			return;
		}

		Answer = answer;
		switch (answer)
		{
			case MapOfferAnswer.Ok:
				State = MapUploadState.Sending;
				break;
			case MapOfferAnswer.HaveIt:
				State = MapUploadState.HaveIt;
				break;
			default:
				Refuse(MapAnswers.Describe(answer));
				break;
		}
	}

	/// <summary>This tick's chunk, behind its index; null when there is none to send.</summary>
	public byte[] NextPacket()
	{
		if (State != MapUploadState.Sending)
		{
			return null;
		}

		byte[] packet = MapTransfer.Packet(_file, Sent);
		Sent++;
		if (Sent == Chunks)
		{
			State = MapUploadState.Sent;
		}

		return packet;
	}

	/// <summary>
	/// What the server says became of the chunks: kept, when it names this map and gives no reason,
	/// once every chunk is sent; refused, with its reason, at any time after it took the offer.
	/// </summary>
	public void Kept(string hash, string error)
	{
		if (State is not (MapUploadState.Sending or MapUploadState.Sent))
		{
			return;
		}

		if (!string.IsNullOrEmpty(error))
		{
			Refuse($"the server did not keep it: {MapAnswers.Clean(error)}");
		}
		else if (State != MapUploadState.Sent || !string.Equals(hash, Hash, StringComparison.Ordinal))
		{
			Refuse(State != MapUploadState.Sent
				? $"the server said it kept the map with {Sent} of {Chunks} chunks sent"
				: "the server said it kept another map");
		}
		else
		{
			State = MapUploadState.Stored;
		}
	}

	/// <summary>Time passes with the server owing an answer: past <see cref="MapTransfer.StallSeconds"/>, it will not come.</summary>
	public void Advance(double seconds)
	{
		if (State is not (MapUploadState.Offered or MapUploadState.Sent) || !(seconds > 0))
		{
			return;
		}

		_quiet += seconds;
		if (_quiet >= MapTransfer.StallSeconds)
		{
			Refuse(State == MapUploadState.Offered
				? $"the server did not answer the offer in {MapTransfer.StallSeconds:0} s: it may be an older build"
				: $"the server did not say what became of the map in {MapTransfer.StallSeconds:0} s");
		}
	}

	/// <summary>The link is gone: an upload not yet over is over.</summary>
	public void Dropped()
	{
		if (!Done)
		{
			Refuse($"the link to the server dropped with {Sent} of {Chunks} chunks sent");
		}
	}

	private void Refuse(string why)
	{
		State = MapUploadState.Refused;
		Error = why;
	}
}

/// <summary>
/// A dedicated server's map from one start to the next (docs/LEVEL_EDITOR.md §9.5, LE-D18, LE8):
/// <c>maps/active</c>, a file that holds a map's hash; and when a server told its next map goes. A
/// round cannot be torn down in place, so changing the map is the process exiting 0 and whatever
/// supervises it starting it again.
/// </summary>
public static class MapChange
{
	/// <summary>The file in <c>user://maps/</c> that names the map a server with no <c>--map</c> plays.</summary>
	public const string ActiveFileName = "active";

	/// <summary>What the file is written through, and renamed from.</summary>
	public const string ActiveTempSuffix = ".tmp";

	/// <summary>Set by systemd in everything a unit starts: the process has a supervisor.</summary>
	public const string SystemdVariable = "INVOCATION_ID";

	/// <summary>Set to <c>1</c> by anything else that starts the server again when it exits: a loop in a shell.</summary>
	public const string SupervisedVariable = "GDPYR_SUPERVISED";

	/// <summary>The most a <c>maps/active</c> may hold: a hash, and whatever space an editor left round it.</summary>
	public const int MaxActiveBytes = 256;

	/// <summary>
	/// How long a server that is leaving keeps its link up before it closes it and goes: long enough
	/// for what it has said to arrive — the answer to the request that sent it away among it. Closing
	/// a link drops what is queued on it.
	/// </summary>
	public const double LeaveGraceSeconds = 1.0;

	/// <summary>
	/// Whether something starts this process again when it exits, from its environment: systemd's
	/// <see cref="SystemdVariable"/> is set, or <see cref="SupervisedVariable"/> is <c>1</c>. A server
	/// run by hand has neither, stays down when it leaves for its next map, and says so (§9.5).
	/// </summary>
	public static bool Supervised(string invocationId, string supervised) =>
		!string.IsNullOrWhiteSpace(invocationId) || string.Equals(supervised?.Trim(), "1", StringComparison.Ordinal);

	/// <summary>What <c>maps/active</c> holds for a map: its hash, and a line's end.</summary>
	public static string ActiveText(string hash) => hash + "\n";

	/// <summary>
	/// The hash a <c>maps/active</c> names: 64 lower-case hex digits, with nothing round them but
	/// white space. Anything else is not one, and says so: a file that names no map is not taken for
	/// no file.
	/// </summary>
	public static bool TryParseActive(string text, out string hash, out string error)
	{
		hash = null;
		error = null;
		string trimmed = text?.Trim();
		if (string.IsNullOrEmpty(trimmed))
		{
			error = "it is empty";
			return false;
		}

		if (!MapTransfer.IsHash(trimmed))
		{
			error = $"it holds '{MapAnswers.Clean(trimmed.Length > 80 ? trimmed[..80] : trimmed)}', which is not a map's hash:"
				+ $" {MapNames.HashHexLength} lower-case hex digits";
			return false;
		}

		hash = trimmed;
		return true;
	}

	/// <summary>
	/// Whether a server told its next map goes now, between rounds or not: when no round is in
	/// progress — nobody has been on the field since the last one ended — or when nobody is connected,
	/// since a round nobody is in is not one to wait out. Otherwise it goes when the round in progress
	/// is over and its intermission with it, where the next round would have started.
	/// </summary>
	public static bool LeavesNow(bool nextMapNamed, bool roundInProgress, int peers) =>
		nextMapNamed && (!roundInProgress || peers <= 0);
}
