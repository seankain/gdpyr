using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using Gdpyr.Core;
using Gdpyr.Level;
using Gdpyr.MapEditor;
using Gdpyr.Match;
using Gdpyr.Net;
using Gdpyr.Sim;
using Gdpyr.Sim.Level;
using Godot;

namespace Gdpyr.EngineChecks;

/// <summary>
/// LE8, the dedicated server (docs/LEVEL_EDITOR.md §11, §9.5): a map from a client to a server, and
/// that server's next map. Run by <c>scripts/level-editor-checks.sh map-upload</c> on a copy of the
/// tree with this file added as an autoload; the game never compiles or loads it.
///
/// <c>LE8_CHECK</c> names this process's part, <c>LE8_DIR</c> is where the processes leave each other
/// marks, <c>LE8_PORT</c> is the server's, <c>LE8_TOKEN</c> its upload token, and <c>LE8_RIDGE</c>,
/// <c>LE8_VALLEY</c> and <c>LE8_THIRD</c> are three maps' files: the one the servers play at first,
/// the one that is published, and one that never arrives whole.
/// <list type="bullet">
/// <item><c>server</c>: a server, however it was started. It says where its maps are and what it
/// plays, and then holds itself to what the script writes to <c>expect-N</c> — how many offers and
/// requests it has answered, what it kept, what its cache and <c>maps/active</c> hold — answering
/// each with <c>seen-N</c>. It ends at <c>stop</c>, or when the game leaves for its next map.</item>
/// <item><c>answers</c>: a client that builds a request for each of §9.5's answers but the ones that
/// need a second connection at once: a wrong token, a proof for the other op, for another file, cut
/// short; a length no map has; a map the server has; chunks with no offer taken; a next map the
/// server lacks, and the one it plays; then, on a second connection, the first one's proofs
/// replayed; a stream with a byte changed; and a stream that stops, with an offer made while it is
/// stopped.</item>
/// <item><c>slow</c> and <c>busy</c>: two clients at once. The first's upload is held at half —
/// its own service's tick turned off, which is the node's and not the game's — while the second's
/// offer is answered <c>busy</c>; then it is let go on, is kept, and the second's offer of the same
/// map is answered <c>have_it</c>.</item>
/// <item><c>next</c>: a client that names the next map and is told what <c>LE8_EXPECT</c> says.</item>
/// <item><c>publish</c>: the editor, with a map of its own open, publishing it by its own command,
/// <c>MapEditorRoot.Publish</c>: what <c>LE8_EXPECT</c> says comes of it.</item>
/// <item><c>server-round</c>: a server with its bots and a round a minute long: told its next map
/// while the round is live, it plays the round out, and leaves when the intermission is over.</item>
/// <item><c>player</c>: a <c>--client</c> in that round, until the server drops it.</item>
/// </list>
/// </summary>
public partial class LevelUploadChecks : Node
{
	private const int Second = SimConfig.TickRate;

	private string _role;
	private string _label;
	private string _dir;
	private string _token;
	private int _port;
	private IEnumerator<bool> _run;
	private int _failures;
	private int _facts;
	private bool _timedOut;
	private bool _done;

	public override void _Ready()
	{
		_role = System.Environment.GetEnvironmentVariable("LE8_CHECK");
		_label = System.Environment.GetEnvironmentVariable("LE8_LABEL") ?? _role;
		_dir = System.Environment.GetEnvironmentVariable("LE8_DIR");
		_token = System.Environment.GetEnvironmentVariable("LE8_TOKEN") ?? string.Empty;
		int.TryParse(System.Environment.GetEnvironmentVariable("LE8_PORT"), out _port);
		if (string.IsNullOrEmpty(_role))
		{
			SetPhysicsProcess(false);
			SetProcess(false);
			return;
		}

		ProcessMode = ProcessModeEnum.Always;
		_run = _role switch
		{
			"server" => Server(),
			"server-round" => ServerRound(),
			"answers" => Answers(),
			"slow" => Slow(),
			"busy" => Busy(),
			"next" => Next(),
			"publish" => Publish(),
			"player" => Player(),
			_ => null,
		};

		GD.Print($"[checks] level upload check: {_label}; the maps are in {MapLibrary.UserRoot}");
		if (_run == null)
		{
			GD.PrintErr($"[checks] no such part: {_role}");
			GetTree().Quit(99);
		}
	}

	public override void _PhysicsProcess(double delta)
	{
		if (_run == null || _done)
		{
			return;
		}

		bool more;
		try
		{
			more = _run.MoveNext();
		}
		catch (Exception e)
		{
			GD.PrintErr($"[checks] EXCEPTION {e}");
			_failures++;
			more = false;
		}

		if (!more)
		{
			Finish();
			GetTree().Quit(_failures);
		}
	}

	private void Finish()
	{
		if (!_done)
		{
			_done = true;
			GD.Print($"[checks] {_label}: {_facts - _failures} of {_facts} facts hold");
		}
	}

	// ---- plumbing ----------------------------------------------------------------------------

	private void Fact(bool ok, string what)
	{
		_facts++;
		if (!ok)
		{
			_failures++;
		}

		GD.Print($"[checks] {(ok ? "PASS" : "FAIL")} {_label}: {what}");
	}

	private void Measured(string what) => GD.Print($"[checks] measured {_label}: {what}");

	private static string F(double v, string format = "0.00") => v.ToString(format, CultureInfo.InvariantCulture);

	private static double Seconds(long from) => (Stopwatch.GetTimestamp() - from) / (double)Stopwatch.Frequency;

	private static string Env(string name) => System.Environment.GetEnvironmentVariable(name) ?? string.Empty;

	private string Read(string name)
	{
		try
		{
			string path = Path.Combine(_dir ?? ".", name);
			return File.Exists(path) ? File.ReadAllText(path).Trim() : string.Empty;
		}
		catch (IOException)
		{
			return string.Empty;
		}
	}

	private bool Has(string name) => !string.IsNullOrEmpty(_dir) && File.Exists(Path.Combine(_dir, name));

	private void Mark(string name, string text = "")
	{
		if (!string.IsNullOrEmpty(_dir))
		{
			string path = Path.Combine(_dir, name);
			File.WriteAllText(path + ".part", text);
			File.Move(path + ".part", path, true);
		}
	}

	private IEnumerable<bool> Until(Func<bool> condition, int maxTicks)
	{
		_timedOut = false;
		int n = 0;
		while (!condition())
		{
			if (n++ >= maxTicks)
			{
				_timedOut = true;
				yield break;
			}

			yield return true;
		}
	}

	private static byte[] Map(string variable) => File.ReadAllBytes(Env(variable));

	private static string Short(string hash) => MapTransfer.Short(hash);

	/// <summary>What the cache holds: each file's name less <c>.map</c>, where it hashes to that; else its name and what it hashes to.</summary>
	private static string CacheHolds()
	{
		if (!Directory.Exists(MapLibrary.CacheRoot))
		{
			return "none";
		}

		var names = new List<string>();
		foreach (string path in Directory.GetFiles(MapLibrary.CacheRoot))
		{
			string name = Path.GetFileName(path);
			string hash = MapFile.HashHex(File.ReadAllBytes(path));
			names.Add(name == hash + MapFile.Extension ? hash : $"{name}!{hash}");
		}

		names.Sort(StringComparer.Ordinal);
		return names.Count == 0 ? "none" : string.Join(",", names);
	}

	private static string ActiveHolds() =>
		File.Exists(MapLibrary.ActivePath) ? File.ReadAllText(MapLibrary.ActivePath).Trim() : "none";

	// ---- a client's link ---------------------------------------------------------------------

	private static NetworkManager Net => NetworkManager.Instance;

	private static MapTransferService Transfer => NetworkManager.Instance.MapTransfer;

	/// <summary>Joins the server as the menu does, and waits for its map and this connection's nonce.</summary>
	private IEnumerable<bool> Connect()
	{
		bool opened = Net.JoinServer("127.0.0.1", _port);
		foreach (bool _ in Until(() => Net is { Connected: true, ServerMapId: not null } || Net.ConnectionFailed, 30 * Second))
		{
			yield return true;
		}

		Fact(opened && !_timedOut && Net.Connected && Net.ServerMapNonce is { Length: MapTransfer.NonceBytes },
			$"connected to 127.0.0.1:{_port}; the server named its map {Short(Net.ServerMapHash)} and sent a nonce of {Net.ServerMapNonce?.Length ?? 0} B");
	}

	/// <summary>Drops the link as the menu drops one it will not play on.</summary>
	private static void Disconnect()
	{
		PlayerManager.Instance?.ForgetClientRoster();
		Net.AbandonConnection();
	}

	private int? _heard;
	private string _heardError;

	/// <summary>An offer put together here, sent through the service's own message; <see cref="_heard"/> is the answer.</summary>
	private IEnumerable<bool> CraftOffer(string hash, int length, byte[] proof)
	{
		int before = Transfer.OfferAnswersHeard;
		_heard = null;
		Transfer.RpcId(1, "ClientOfferMap", hash, length, proof);
		foreach (bool _ in Until(() => Transfer.OfferAnswersHeard > before, 10 * Second))
		{
			yield return true;
		}

		_heard = _timedOut ? null : Transfer.LastOfferAnswerHeard;
	}

	private IEnumerable<bool> CraftNext(string hash, byte[] proof)
	{
		int before = Transfer.NextAnswersHeard;
		_heard = null;
		Transfer.RpcId(1, "ClientSetNextMap", hash, proof);
		foreach (bool _ in Until(() => Transfer.NextAnswersHeard > before, 10 * Second))
		{
			yield return true;
		}

		_heard = _timedOut ? null : Transfer.LastNextAnswerHeard;
	}

	/// <summary>The service's own next-map request, by a token; <see cref="_heard"/> is the answer.</summary>
	private IEnumerable<bool> AskNext(string hash, string token)
	{
		_heard = null;
		if (!Transfer.RequestNextMap(hash, token, out string error))
		{
			GD.PrintErr($"[checks] {_label}: the request was not made: {error}");
			yield break;
		}

		foreach (bool _ in Until(() => Transfer.NextMapAnswer != null, 10 * Second))
		{
			yield return true;
		}

		_heard = Transfer.NextMapAnswer;
	}

	/// <summary>The service's own upload, by a token, to its end; <see cref="_upload"/> is what became of it.</summary>
	private IEnumerable<bool> Upload(byte[] file, string name, string token)
	{
		_upload = null;
		if (!Transfer.BeginUpload(file, name, token, out string error))
		{
			GD.PrintErr($"[checks] {_label}: the upload was not begun: {error}");
			yield break;
		}

		_upload = Transfer.Upload;
		foreach (bool _ in Until(() => _upload.Done, 40 * Second))
		{
			yield return true;
		}
	}

	private MapUploader _upload;

	private static string OfferName(int? wire) =>
		wire is { } w ? (MapAnswers.TryOffer(w, out MapOfferAnswer a) ? MapAnswers.Name(a) : w.ToString()) : "no answer";

	private static string NextName(int? wire) =>
		wire is { } w ? (MapAnswers.TryNext(w, out MapNextAnswer a) ? MapAnswers.Name(a) : w.ToString()) : "no answer";

	private void OfferFact(MapOfferAnswer expected, string request) =>
		Fact(_heard == (int)expected, $"{request}: {OfferName(_heard)}");

	private void NextFact(MapNextAnswer expected, string request) =>
		Fact(_heard == (int)expected, $"{request}: {NextName(_heard)}");

	// ---- answers: a request built for each -----------------------------------------------------

	private IEnumerator<bool> Answers()
	{
		foreach (bool _ in Until(() => MapEditorRoot.Current != null, 30 * Second))
		{
			yield return true;
		}

		byte[] ridge = Map("LE8_RIDGE");
		byte[] valley = Map("LE8_VALLEY");
		byte[] third = Map("LE8_THIRD");
		string rh = MapFile.HashHex(ridge);
		string vh = MapFile.HashHex(valley);
		string th = MapFile.HashHex(third);
		const string wrongToken = "not-the-servers-token-at-all";

		foreach (bool _ in Connect())
		{
			yield return true;
		}

		if (!Net.Connected)
		{
			yield break;
		}

		byte[] nonce1 = (byte[])Net.ServerMapNonce.Clone();
		Fact(Net.ServerMapHash == rh, $"the server plays the first map, {Short(rh)}");

		// A wrong token, by the service's own upload.
		foreach (bool _ in Upload(valley, "Valley", wrongToken))
		{
			yield return true;
		}

		Fact(_upload is { State: MapUploadState.Refused, Answer: MapOfferAnswer.BadProof, Sent: 0 }
				&& _upload.Error == MapAnswers.Describe(MapOfferAnswer.BadProof),
			$"an upload by a wrong token: {(_upload?.Answer is { } a ? MapAnswers.Name(a) : "no answer")}, {_upload?.Sent} chunks sent; '{_upload?.Error}'");

		// The right token's proof for this map on this connection: kept, to be replayed on the next.
		byte[] proof1 = MapProof.Compute(_token, nonce1, vh, MapProofOp.Upload);
		byte[] nextProof1 = MapProof.Compute(_token, nonce1, rh, MapProofOp.SetNext);

		foreach (bool _ in CraftOffer(vh, valley.Length, MapProof.Compute(_token, nonce1, vh, MapProofOp.SetNext)))
		{
			yield return true;
		}

		OfferFact(MapOfferAnswer.BadProof, "an offer with the right token's proof for the other op, naming the next map");

		foreach (bool _ in CraftOffer(vh, valley.Length, MapProof.Compute(_token, nonce1, th, MapProofOp.Upload)))
		{
			yield return true;
		}

		OfferFact(MapOfferAnswer.BadProof, "an offer with the right token's proof for another file");

		foreach (bool _ in CraftOffer(vh, valley.Length, proof1.AsSpan(0, MapProof.Bytes - 1).ToArray()))
		{
			yield return true;
		}

		OfferFact(MapOfferAnswer.BadProof, "an offer with the right proof less its last byte");

		foreach (bool _ in CraftOffer(vh, valley.Length, Array.Empty<byte>()))
		{
			yield return true;
		}

		OfferFact(MapOfferAnswer.BadProof, "an offer with no proof");

		foreach (bool _ in CraftOffer(vh.ToUpperInvariant(), valley.Length, proof1))
		{
			yield return true;
		}

		OfferFact(MapOfferAnswer.BadProof, "an offer of a map named by its hash in upper case");

		// The right proof, and a length no map has.
		foreach (bool _ in CraftOffer(vh, MapLimits.MaxFileBytes + 1, proof1))
		{
			yield return true;
		}

		OfferFact(MapOfferAnswer.TooLarge, $"an offer of {MapLimits.MaxFileBytes + 1:N0} B with the right proof");

		foreach (bool _ in CraftOffer(vh, 0, proof1))
		{
			yield return true;
		}

		OfferFact(MapOfferAnswer.TooLarge, "an offer of 0 B with the right proof");

		// And a wrong proof says nothing of the length.
		foreach (bool _ in CraftOffer(vh, MapLimits.MaxFileBytes + 1, MapProof.Compute(wrongToken, nonce1, vh, MapProofOp.Upload)))
		{
			yield return true;
		}

		OfferFact(MapOfferAnswer.BadProof, $"an offer of {MapLimits.MaxFileBytes + 1:N0} B by a wrong token");

		// The map the server is playing: it has it.
		foreach (bool _ in Upload(ridge, "Ridge", _token))
		{
			yield return true;
		}

		Fact(_upload is { State: MapUploadState.HaveIt, Answer: MapOfferAnswer.HaveIt, Sent: 0 },
			$"an upload of the map the server plays: {(_upload?.Answer is { } b ? MapAnswers.Name(b) : "no answer")}, {_upload?.Sent} chunks sent");

		// Chunks with no offer taken: the server drops them, which is its fact to hold.
		for (int i = 0; i < 3; i++)
		{
			Transfer.RpcId(1, "ClientMapChunk", MapTransfer.Packet(valley, i));
			yield return true;
		}

		// The next map.
		foreach (bool _ in AskNext(vh, wrongToken))
		{
			yield return true;
		}

		NextFact(MapNextAnswer.BadProof, "a next-map request by a wrong token");

		foreach (bool _ in CraftNext(vh, proof1))
		{
			yield return true;
		}

		NextFact(MapNextAnswer.BadProof, "a next-map request with the right token's proof for an upload of that map");

		foreach (bool _ in AskNext(vh, _token))
		{
			yield return true;
		}

		NextFact(MapNextAnswer.NotHere, "a next-map request, rightly proved, for a map the server has not got");

		foreach (bool _ in AskNext(rh, _token))
		{
			yield return true;
		}

		NextFact(MapNextAnswer.Playing, "a next-map request, rightly proved, for the map the server plays");

		// A second connection: its nonce is another, and the first one's proofs are not proofs on it.
		Disconnect();
		for (int i = 0; i < Second / 2; i++)
		{
			yield return true;
		}

		foreach (bool _ in Connect())
		{
			yield return true;
		}

		if (!Net.Connected)
		{
			yield break;
		}

		byte[] nonce2 = (byte[])Net.ServerMapNonce.Clone();
		Fact(!nonce2.AsSpan().SequenceEqual(nonce1), "a second connection was sent another nonce");

		foreach (bool _ in CraftOffer(vh, valley.Length, proof1))
		{
			yield return true;
		}

		OfferFact(MapOfferAnswer.BadProof, "the first connection's proof, right there, replayed on the second");

		foreach (bool _ in CraftNext(rh, nextProof1))
		{
			yield return true;
		}

		NextFact(MapNextAnswer.BadProof, "the first connection's next-map proof replayed on the second");

		// A stream with one byte changed: taken, and refused when it is whole.
		byte[] proofThird = MapProof.Compute(_token, nonce2, th, MapProofOp.Upload);
		foreach (bool _ in CraftOffer(th, third.Length, proofThird))
		{
			yield return true;
		}

		OfferFact(MapOfferAnswer.Ok, "an offer of a third map, rightly proved");
		byte[] forged = (byte[])third.Clone();
		forged[forged.Length / 2] ^= 0x10;
		int stored = Transfer.StoredHeard;
		for (int i = 0; i < MapTransfer.ChunkCount(forged.Length); i++)
		{
			Transfer.RpcId(1, "ClientMapChunk", MapTransfer.Packet(forged, i));
			yield return true;
		}

		foreach (bool _ in Until(() => Transfer.StoredHeard > stored, 10 * Second))
		{
			yield return true;
		}

		Fact(!_timedOut && Transfer.LastStoredError.Contains($"not the {Short(th)} the uploader named", StringComparison.Ordinal),
			$"its {MapTransfer.ChunkCount(forged.Length)} chunks sent with byte {forged.Length / 2} changed: the server did not keep it, '{Transfer.LastStoredError}'");

		// A stream that stops after two chunks: refused after 10 s, and the one upload until then.
		foreach (bool _ in CraftOffer(th, third.Length, proofThird))
		{
			yield return true;
		}

		OfferFact(MapOfferAnswer.Ok, "the third map offered again, the refused upload let go of");
		stored = Transfer.StoredHeard;
		Transfer.RpcId(1, "ClientMapChunk", MapTransfer.Packet(third, 0));
		yield return true;
		Transfer.RpcId(1, "ClientMapChunk", MapTransfer.Packet(third, 1));
		long stopped = Stopwatch.GetTimestamp();
		for (int i = 0; i < Second; i++)
		{
			yield return true;
		}

		foreach (bool _ in CraftOffer(vh, valley.Length, MapProof.Compute(_token, nonce2, vh, MapProofOp.Upload)))
		{
			yield return true;
		}

		OfferFact(MapOfferAnswer.Busy, "an offer on the same connection while its own upload is stopped at 2 chunks");

		foreach (bool _ in Until(() => Transfer.StoredHeard > stored, 20 * Second))
		{
			yield return true;
		}

		double waited = Seconds(stopped);
		Fact(!_timedOut && Transfer.LastStoredError.Contains("stopped arriving: 2 of", StringComparison.Ordinal)
				&& waited >= MapTransfer.StallSeconds && waited < MapTransfer.StallSeconds + 2.5,
			$"the upload stopped at 2 chunks was refused {F(waited, "0.0")} s after the second: '{Transfer.LastStoredError}'");

		Disconnect();
		Mark("answers-done");
	}

	// ---- two clients at once -------------------------------------------------------------------

	private IEnumerator<bool> Slow()
	{
		foreach (bool _ in Until(() => MapEditorRoot.Current != null, 30 * Second))
		{
			yield return true;
		}

		byte[] valley = Map("LE8_VALLEY");
		foreach (bool _ in Connect())
		{
			yield return true;
		}

		foreach (bool _ in Until(() => Has("busy-ready"), 60 * Second))
		{
			yield return true;
		}

		Fact(!_timedOut, "the second client is connected, and waiting");
		long began = Stopwatch.GetTimestamp();
		bool begun = Transfer.BeginUpload(valley, "Valley", _token, out string error);
		Fact(begun, $"an upload begun, of {valley.Length:N0} B{(begun ? string.Empty : ": " + error)}");
		if (!begun)
		{
			yield break;
		}

		MapUploader upload = Transfer.Upload;
		foreach (bool _ in Until(() => upload.Sent >= upload.Chunks / 2 || upload.Done, 20 * Second))
		{
			yield return true;
		}

		// Held: the service's tick is what sends a chunk.
		Transfer.SetPhysicsProcess(false);
		int held = upload.Sent;
		long heldAt = Stopwatch.GetTimestamp();
		Fact(upload is { State: MapUploadState.Sending, Answer: MapOfferAnswer.Ok } && held < upload.Chunks,
			$"the offer was answered ok, and the upload is held at {held} of {upload.Chunks} chunks");
		Mark("slow-half");

		foreach (bool _ in Until(() => Has("busy-done"), 9 * Second))
		{
			yield return true;
		}

		Fact(!_timedOut && upload.Sent == held && upload.State == MapUploadState.Sending,
			$"it was still held at {upload.Sent} chunks when the second client had its answer, {F(Seconds(heldAt), "0.0")} s on");
		Transfer.SetPhysicsProcess(true);

		foreach (bool _ in Until(() => upload.Done, 20 * Second))
		{
			yield return true;
		}

		Fact(upload.State == MapUploadState.Stored && upload.Sent == upload.Chunks,
			$"let go on, it was kept: {upload.State}, {upload.Sent} of {upload.Chunks} chunks, {F(Seconds(began))} s in all{(upload.Error == null ? string.Empty : ": " + upload.Error)}");
		Mark("slow-done");
		Disconnect();
	}

	private IEnumerator<bool> Busy()
	{
		foreach (bool _ in Until(() => MapEditorRoot.Current != null, 30 * Second))
		{
			yield return true;
		}

		byte[] valley = Map("LE8_VALLEY");
		foreach (bool _ in Connect())
		{
			yield return true;
		}

		Mark("busy-ready");
		foreach (bool _ in Until(() => Has("slow-half"), 90 * Second))
		{
			yield return true;
		}

		Fact(!_timedOut, "the first client's upload is under way, and held");
		foreach (bool _ in Upload(valley, "Valley", _token))
		{
			yield return true;
		}

		Fact(_upload is { State: MapUploadState.Refused, Answer: MapOfferAnswer.Busy, Sent: 0 },
			$"the same map offered while the server is taking it from another: {(_upload?.Answer is { } a ? MapAnswers.Name(a) : "no answer")}, {_upload?.Sent} chunks sent; '{_upload?.Error}'");
		Mark("busy-done");

		foreach (bool _ in Until(() => Has("slow-done"), 30 * Second))
		{
			yield return true;
		}

		Fact(!_timedOut, "the first client's upload was kept");
		foreach (bool _ in Upload(valley, "Valley", _token))
		{
			yield return true;
		}

		Fact(_upload is { State: MapUploadState.HaveIt, Answer: MapOfferAnswer.HaveIt, Sent: 0 },
			$"offered again, once the server has kept it: {(_upload?.Answer is { } b ? MapAnswers.Name(b) : "no answer")}, {_upload?.Sent} chunks sent");
		Disconnect();
	}

	// ---- naming the next map -------------------------------------------------------------------

	/// <summary>
	/// <c>LE8_EXPECT</c> is the answer's name. With <c>LE8_LEAVES=1</c> the server is one that goes at
	/// once: the link is dropped by it, after the answer. With <c>LE8_STREAM=1</c> the map is offered
	/// first — <c>LE8_OFFER</c> is that answer's name — and its chunks sent whatever the answer.
	/// </summary>
	private IEnumerator<bool> Next()
	{
		foreach (bool _ in Until(() => MapEditorRoot.Current != null, 30 * Second))
		{
			yield return true;
		}

		byte[] valley = Map("LE8_VALLEY");
		string hash = MapFile.HashHex(valley);
		foreach (bool _ in Connect())
		{
			yield return true;
		}

		if (!Net.Connected)
		{
			yield break;
		}

		if (Env("LE8_STREAM") == "1")
		{
			// The map offered, rightly proved by the token this server was not given, and then every
			// chunk of it sent whatever the answer: the server's fact is what it did with them.
			foreach (bool _ in CraftOffer(hash, valley.Length, MapProof.Compute(_token, Net.ServerMapNonce, hash, MapProofOp.Upload)))
			{
				yield return true;
			}

			Fact(OfferName(_heard) == Env("LE8_OFFER"), $"the map offered, proved by a token: {OfferName(_heard)}; {Env("LE8_OFFER")} expected");
			int chunks = MapTransfer.ChunkCount(valley.Length);
			for (int i = 0; i < chunks; i++)
			{
				Transfer.RpcId(1, "ClientMapChunk", MapTransfer.Packet(valley, i));
				yield return true;
			}

			Measured($"and its {chunks} chunks sent all the same");
		}

		foreach (bool _ in AskNext(hash, _token))
		{
			yield return true;
		}

		long answered = Stopwatch.GetTimestamp();
		string expect = Env("LE8_EXPECT");
		Fact(NextName(_heard) == expect, $"a next-map request, rightly proved, for the map the server now has: {NextName(_heard)}; {expect} expected");
		if (MapAnswers.TryNext(_heard ?? -1, out MapNextAnswer answer))
		{
			Measured($"the editor would say: {MapAnswers.Describe(answer)}");
		}

		if (Env("LE8_LEAVES") == "1")
		{
			foreach (bool _ in Until(() => Net.ConnectionFailed, 10 * Second))
			{
				yield return true;
			}

			double after = Seconds(answered);
			Fact(!_timedOut && after >= MapChange.LeaveGraceSeconds * 0.8 && after < MapChange.LeaveGraceSeconds + 2,
				$"the server dropped the link {F(after)} s after its answer: no round was in its way");
		}

		Disconnect();
	}

	// ---- the editor's Publish --------------------------------------------------------------------

	/// <summary>
	/// <c>LE8_EXPECT</c> is <c>offer:next</c>, the answers' names, <c>-</c> for none; <c>LE8_NEXT=1</c>
	/// asks for the map to be played next. With <c>LE8_WAIT</c>, it waits for that mark first; with
	/// <c>LE8_AGAIN=1</c> it then publishes twice more: the same map, which the server has, and by a
	/// wrong token.
	/// </summary>
	private IEnumerator<bool> Publish()
	{
		foreach (bool _ in Until(() => MapEditorRoot.Current?.Editing != null, 60 * Second))
		{
			yield return true;
		}

		MapEditorRoot editor = MapEditorRoot.Current;
		Fact(!_timedOut && editor is { EverSaved: true } && !editor.Editing.Dirty,
			$"the editor has {editor?.FileName}.map open, saved: sha-256 {Short(editor?.SavedHash)}");
		if (_timedOut)
		{
			yield break;
		}

		string wait = Env("LE8_WAIT");
		if (wait.Length > 0)
		{
			foreach (bool _ in Until(() => Has(wait), 180 * Second))
			{
				yield return true;
			}

			Fact(!_timedOut, $"the server's round is live ({wait})");
		}

		string[] expect = Env("LE8_EXPECT").Split(':');
		string expectNext = expect.Length > 1 ? expect[1] : "-";
		bool makeNext = Env("LE8_NEXT") == "1";
		string address = $"127.0.0.1:{_port}";
		string hash = editor.SavedHash;
		uint revision = editor.Editing.Document.Revision;

		foreach (bool _ in PublishOnce(editor, address, _token, "published", makeNext, expect[0], expectNext))
		{
			yield return true;
		}

		Fact(editor.PublishedHash == hash && editor.SavedHash == hash && editor.Editing.Document.Revision == revision,
			$"what it sent is the file as it was saved, {Short(editor.PublishedHash)}: nothing was saved again");

		if (Env("LE8_AGAIN") == "1")
		{
			foreach (bool _ in PublishOnce(editor, address, _token, "published again", makeNext, "have_it", expectNext))
			{
				yield return true;
			}

			foreach (bool _ in PublishOnce(editor, address, "not-the-servers-token-at-all", "published by a wrong token", makeNext, "bad_proof", "-"))
			{
				yield return true;
			}

			// What cannot be begun: nothing is connected to.
			Fact(!editor.Publish("127.0.0.1:99999", _token, true, out string badAddress) && badAddress.Contains("not a port")
					&& !editor.Publish(address, " ", true, out string noToken) && noToken.Contains("--map-upload-token")
					&& Net.IsOffline && !Net.HasStarted,
				"an address that is not one, and no token, begin nothing");
		}

		Mark(Env("LE8_MARK").Length > 0 ? Env("LE8_MARK") : "published");
	}

	private IEnumerable<bool> PublishOnce(MapEditorRoot editor, string address, string token, string what, bool makeNext, string offer,
		string next)
	{
		bool begun = editor.Publish(address, token, makeNext, out string error);
		if (!begun)
		{
			Fact(false, $"{what}: not begun: {error}");
			yield break;
		}

		long started = Stopwatch.GetTimestamp();
		foreach (bool _ in Until(() => editor.Publishing is PublishState.Done or PublishState.Failed, 60 * Second))
		{
			yield return true;
		}

		string gotOffer = editor.PublishAnswer is { } a ? MapAnswers.Name(a) : "-";
		string gotNext = editor.PublishNextAnswer is { } n ? NextName(n) : "-";
		bool has = offer is "ok" or "have_it";
		Fact(!_timedOut && gotOffer == offer && gotNext == next
				&& editor.Publishing == (has ? PublishState.Done : PublishState.Failed)
				&& editor.PublishUpload == (offer == "ok" ? MapUploadState.Stored : offer == "have_it" ? MapUploadState.HaveIt : MapUploadState.Refused),
			$"{what}, {F(Seconds(started))} s: the offer {gotOffer}, the upload {editor.PublishUpload}, the next map {gotNext}; {editor.Publishing}");
		Measured($"the editor says: {editor.PublishMessage}");
		Fact(Net.IsOffline && !Net.HasStarted && Net.MapTransfer.Upload == null && !Session.InGame,
			"and the link is dropped: the editor is offline again, in its menu phase");
	}

	// ---- a server, held to what the script expects ---------------------------------------------

	private IEnumerator<bool> Server()
	{
		foreach (bool _ in Until(() => Session.InGame && Net?.MapTransfer != null, 60 * Second))
		{
			yield return true;
		}

		ServerStarted();
		MapTransferService service = Net.MapTransfer;
		service.LeavingForNextMap += () =>
		{
			Fact(service.NextMap == ActiveHolds() && service.NextMap != Session.Map.Hash,
				$"leaving for the next map, {Short(service.NextMap)}, which maps/active names; it plays {Session.Map} to the last");
			Fact(CombatManager.Instance.Match.Phase == RoundPhase.Warmup,
				$"no round is in progress: {CombatManager.Instance.Match.Phase}, {Multiplayer.GetPeers().Length} peer(s) connected");
			Finish();
		};

		Mark("server-up");
		int next = 1;
		while (!Has("stop") && !service.Leaving)
		{
			if (Has($"expect-{next}"))
			{
				// A tick on, so that what the last client sent as it left has been taken.
				for (int i = 0; i < Second / 2; i++)
				{
					yield return true;
				}

				Expect(service, Read($"expect-{next}"));
				Mark($"seen-{next}");
				next++;
			}

			yield return true;
		}

		// Leaving: the game goes, with 0, and this part with it.
		while (service.Leaving)
		{
			yield return true;
		}
	}

	private void ServerStarted()
	{
		string maps = Env("LE8_MAPS");
		if (maps.Length > 0)
		{
			Fact(Path.GetFullPath(MapLibrary.UserRoot).TrimEnd('/') == Path.GetFullPath(maps).TrimEnd('/'),
				$"the server's user://maps is {MapLibrary.UserRoot}: where deploy-map.sh put the map");
		}

		string plays = Env("LE8_PLAYS");
		Fact(Session.Map.IsData && Session.Map.Hash == plays && Session.Bytes != null && MapFile.HashHex(Session.Bytes) == plays,
			$"the server plays {Session.Map}, from {Session.Map.Path}");
		string from = Env("LE8_FROM");
		if (from.Length > 0)
		{
			string expected = from == "cache" ? MapLibrary.CachePathOf(plays) : from;
			Fact(Path.GetFullPath(Session.Map.Path) == Path.GetFullPath(expected),
				from == "cache" ? $"which is the cache's file for that hash, {expected}" : "which is the file it was given");
			Fact(LevelRoot.Current?.Document != null && MapFile.TryRead(File.ReadAllBytes(Session.Map.Path), out MapDocument file, out _)
					&& file.SameAs(LevelRoot.Current.Document, out _),
				"and the world was built from that file, read again field by field");
		}

		bool takes = Env("LE8_TAKES") == "1";
		Fact(Net.MapTransfer.TakesUploads == takes && (Bootstrap.Options.MapUploadToken != null) == takes,
			takes ? "it takes uploads: it was given a token" : "it takes no uploads: it was given no token");
		Measured($"maps/active holds {ActiveHolds()}; the cache holds {CacheHolds()}");
	}

	/// <summary><c>key=value</c> pairs, each a fact: what the server has answered, kept and holds.</summary>
	private void Expect(MapTransferService service, string expectation)
	{
		foreach (string pair in expectation.Split(' ', StringSplitOptions.RemoveEmptyEntries))
		{
			int at = pair.IndexOf('=');
			string key = pair[..at];
			string want = pair[(at + 1)..];
			string have = key switch
			{
				"offers" => service.OffersAnswered.ToString(),
				"nexts" => service.NextMapsAnswered.ToString(),
				"dropped" => service.ChunksDropped.ToString(),
				"kept" => service.UploadsKept.ToString(),
				"refused" => service.UploadsRefused.ToString(),
				"last_offer" => MapAnswers.Name(service.LastOfferAnswer),
				"last_next" => MapAnswers.Name(service.LastNextAnswer),
				"cache" => CacheHolds(),
				"active" => ActiveHolds(),
				"next" => service.NextMap ?? "none",
				"uploading" => service.UploadingPeer == 0 ? "nobody" : "somebody",
				"leaving" => service.Leaving ? "yes" : "no",
				_ => "?",
			};
			string what = key switch
			{
				"offers" => "offers answered",
				"nexts" => "next-map requests answered",
				"dropped" => "chunks dropped, from peers whose offer was not taken",
				"kept" => "uploads kept",
				"refused" => "uploads taken and then refused",
				"last_offer" => "the last offer's answer",
				"last_next" => "the last next-map request's answer",
				"cache" => "the cache holds, each file hashing to its name",
				"active" => "maps/active holds",
				"next" => "the map it leaves for",
				"uploading" => "an upload is being taken from",
				"leaving" => "leaving",
				_ => key,
			};
			Fact(have == want, $"{what}: {(have.Length > 20 ? Short(have) : have)}{(have == want ? string.Empty : $", not {want}")}");
		}
	}

	// ---- a server with a round, told its next map ------------------------------------------------

	private IEnumerator<bool> ServerRound()
	{
		foreach (bool _ in Until(() => Session.InGame && Net?.MapTransfer != null && CombatManager.Instance != null, 60 * Second))
		{
			yield return true;
		}

		ServerStarted();
		MapTransferService service = Net.MapTransfer;
		CombatManager combat = CombatManager.Instance;
		string valley = MapFile.HashHex(Map("LE8_VALLEY"));
		string ridge = Env("LE8_PLAYS");

		// A round a minute long and an intermission of five seconds: the game mode's own numbers, set
		// before anybody is on the field.
		combat.GameMode.RoundDurationMinutes = 1;
		combat.GameMode.IntermissionSeconds = 5f;
		Fact(combat.Match.Phase == RoundPhase.Warmup, "no round yet: a round of 1 minute and an intermission of 5 s are set");
		Mark("server-up");

		long endedAt = 0;
		int peersAtEnd = 0;
		bool left = false;
		service.LeavingForNextMap += () =>
		{
			left = true;
			double after = endedAt == 0 ? -1 : Seconds(endedAt);
			Fact(combat.Match.Phase == RoundPhase.Ended && after >= 4.8 && after < 6.5,
				$"it leaves with the round over and not started again ({combat.Match.Phase}, {combat.Match.Outcome}), {F(after)} s after it ended: the intermission's 5 s");
			Fact(Multiplayer.GetPeers().Length >= 1 && peersAtEnd >= 1,
				$"with {Multiplayer.GetPeers().Length} peer(s) still connected: it did not wait for them to go");
			Fact(ActiveHolds() == valley && Session.Map.Hash == ridge, $"maps/active names {Short(valley)}, and it played {Short(ridge)} to the last");
			Finish();
		};

		foreach (bool _ in Until(() => combat.Match.Phase == RoundPhase.Live, 120 * Second))
		{
			yield return true;
		}

		long liveAt = Stopwatch.GetTimestamp();
		Fact(!_timedOut, $"the round is live, {Multiplayer.GetPeers().Length} peer(s) connected, {combat.PlayerCount} on the field");
		if (_timedOut)
		{
			yield break;
		}

		Mark("round-live");
		foreach (bool _ in Until(() => service.NextMap != null, 50 * Second))
		{
			yield return true;
		}

		double toldAt = Seconds(liveAt);
		Fact(!_timedOut && service.NextMap == valley && service.LastNextAnswer == MapNextAnswer.Ok,
			$"told its next map {F(toldAt, "0.0")} s into the round: {Short(service.NextMap)}, answered {MapAnswers.Name(service.LastNextAnswer)}");
		Fact(service.UploadsKept == 1 && CacheHolds() == valley,
			$"the uploaded map is in the cache under its hash, and hashes to it: {Short(CacheHolds())}");
		Fact(ActiveHolds() == valley, $"maps/active holds {Short(ActiveHolds())}");
		Fact(Session.Map.Hash == ridge && combat.Match.Phase == RoundPhase.Live && !service.Leaving,
			$"and it goes on playing {Short(ridge)}: the round is {combat.Match.Phase}, {F(combat.Match.SecondsRemaining(Net.Tick), "0")} s left");

		// The round is played out: it does not leave while it is live, whoever comes and goes.
		int leftWhileLive = 0;
		int ticksLive = 0;
		while (combat.Match.Phase == RoundPhase.Live && !left)
		{
			ticksLive++;
			leftWhileLive += service.Leaving ? 1 : 0;
			yield return true;
		}

		endedAt = Stopwatch.GetTimestamp();
		peersAtEnd = Multiplayer.GetPeers().Length;
		double played = Seconds(liveAt);
		Fact(leftWhileLive == 0 && !left && combat.Match.Phase == RoundPhase.Ended && played > 58 && played < 63,
			$"the round was played out, {F(played, "0.0")} s of it ({ticksLive} ticks after it was told), and ended {combat.Match.Outcome}; it had not left");

		while (!left)
		{
			yield return true;
		}

		while (service.Leaving)
		{
			yield return true;
		}
	}

	// ---- a player in that round ---------------------------------------------------------------------

	private IEnumerator<bool> Player()
	{
		foreach (bool _ in Until(() => Session.InGame && PlayerManager.Instance is { PlayerCount: > 0 } players
			&& players.LocalStateName != "-", 120 * Second))
		{
			yield return true;
		}

		Fact(!_timedOut && Session.Map.Hash == Env("LE8_PLAYS"), $"in the round, on {Session.Map}");
		if (_timedOut)
		{
			yield break;
		}

		long joined = Stopwatch.GetTimestamp();
		bool sawLive = false;
		bool sawEnded = false;
		foreach (bool _ in Until(() =>
		{
			RoundPhase phase = CombatManager.Instance.Match.Phase;
			sawLive |= phase == RoundPhase.Live;
			sawEnded |= sawLive && phase == RoundPhase.Ended;
			return !Net.Connected;
		}, 150 * Second))
		{
			yield return true;
		}

		Fact(!_timedOut && Net.ConnectionFailed && sawLive && sawEnded,
			$"the round was live, then over, and then the server dropped the link, {F(Seconds(joined), "0.0")} s after this client was in it");
		Mark("player-dropped");
	}
}
