using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using Gdpyr.Core;
using Gdpyr.Level;
using Gdpyr.Net;
using Gdpyr.Sim;
using Gdpyr.Sim.Level;
using Godot;

namespace Gdpyr.EngineChecks;

/// <summary>
/// LE7, fetching a map (docs/LEVEL_EDITOR.md §11): a <c>--server</c> on a data map no client has, and
/// clients behind <c>tools/EngineChecks/delay_relay.py</c>, each with a home of its own. Run by
/// <c>scripts/level-editor-checks.sh map-fetch</c> on a copy of the tree with this file added as an
/// autoload; the game never compiles or loads it.
///
/// <c>LE7_CHECK</c> names this process's part, <c>LE7_DIR</c> is where the processes leave each other
/// marks, and <c>LE7_HASH</c> is the map's SHA-256:
/// <list type="bullet">
/// <item><c>server</c>: the server. It counts what each peer asks and is sent, by the phase the
/// script wrote to <c>phase</c> when it joined, and at <c>stop</c> holds each phase to what it should
/// have been. What it writes to <c>fault</c> it does to the next fetch: <c>cut</c> stops the sender at
/// half the chunks — the map service's tick is turned off, which is the node's and not the game's —
/// and <c>mismatch</c> sends the map's chunks in turn, in place of the service and through its own
/// message, with one byte of the middle one changed. In the <c>drops</c> phase it sends the fetching
/// peer, each tick after the map service has sent that tick's chunk, a reliable message on the
/// default channel and one on the map's channel, each saying how many chunks had been sent, each
/// bigger than a chunk (<see cref="ProbePadBytes"/>).</item>
/// <item><c>fetch</c>, <c>cached</c>, <c>wrong</c>: a client that has not got the map, has it in its
/// cache, or has another map's bytes under its hash's name: it is in the round on the map, whose
/// world is the file's, having asked once, never, or once.</item>
/// <item><c>refused</c>: a client whose fetch is refused, for the reason in <c>LE7_EXPECT</c>: it
/// keeps nothing, and the menu drops the link and exits 1.</item>
/// <item><c>drops</c>: <c>fetch</c> behind a relay that drops datagrams: what arrived ahead of what,
/// written to <c>drops-&lt;pid&gt;</c> for the server to add up.</item>
/// <item><c>playback</c>: <c>--playdemo</c> of a demo a <c>fetch</c> client recorded: it plays on the
/// map it names.</item>
/// </list>
/// </summary>
public partial class LevelFetchChecks : Node
{
	private const int Second = SimConfig.TickRate;

	/// <summary>
	/// A probe's padding: with it a probe is bigger than a chunk's message, so it never shares a datagram
	/// with one, and ENet's reliable window — which shrinks when datagrams are lost, and passes over a
	/// command too big for what is left of it to send a smaller one after (thirdparty/enet/protocol.c,
	/// enet_protocol_check_outgoing_commands) — cannot send a probe ahead of a chunk it is holding back.
	/// Behind a relay whose delay is fixed, a probe then arrives before a chunk sent ahead of it only
	/// when that chunk, or one before it on its channel, was lost and sent again.
	/// </summary>
	private const int ProbePadBytes = 1024;

	private string _role;
	private string _dir;
	private string _hash;
	private IEnumerator<bool> _run;
	private int _failures;
	private int _facts;
	private bool _timedOut;
	private bool _done;

	public override void _Ready()
	{
		_role = System.Environment.GetEnvironmentVariable("LE7_CHECK");
		_dir = System.Environment.GetEnvironmentVariable("LE7_DIR");
		_hash = System.Environment.GetEnvironmentVariable("LE7_HASH") ?? string.Empty;
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
			"fetch" => Client(expectRequests: 1, expectFetch: true),
			"cached" => Client(expectRequests: 0, expectFetch: false),
			"wrong" => Client(expectRequests: 1, expectFetch: true),
			"drops" => Client(expectRequests: 1, expectFetch: true),
			"playback" => Playback(),
			"refused" => Refused(),
			_ => null,
		};

		GD.Print($"[checks] level fetch check: {_role}; the cache is {MapLibrary.CacheRoot}");
		if (_role != "server" && _role != "playback")
		{
			BeforeTheLink();
		}

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
		_done = true;
		GD.Print($"[checks] {_role}: {_facts - _failures} of {_facts} facts hold");
	}

	// ---- plumbing ----------------------------------------------------------------------------

	private void Fact(bool ok, string what)
	{
		_facts++;
		if (!ok)
		{
			_failures++;
		}

		GD.Print($"[checks] {(ok ? "PASS" : "FAIL")} {_role}: {what}");
	}

	private void Measured(string what) => GD.Print($"[checks] measured {_role}: {what}");

	private static string F(double v, string format = "0.00") => v.ToString(format, CultureInfo.InvariantCulture);

	private static double Seconds(long from) => (Stopwatch.GetTimestamp() - from) / (double)Stopwatch.Frequency;

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

	private string CachedPath => MapLibrary.CachePathOf(_hash);

	/// <summary>The cache's file under the map's hash, and what its bytes hash to; null when there is none.</summary>
	private string CachedHash() =>
		File.Exists(CachedPath) ? MapFile.HashHex(File.ReadAllBytes(CachedPath)) : null;

	private int FilesInCache() =>
		Directory.Exists(MapLibrary.CacheRoot) ? Directory.GetFiles(MapLibrary.CacheRoot).Length : 0;

	// ---- the client, before the link is up -----------------------------------------------------

	/// <summary>What the cache holds before the client has heard from the server.</summary>
	private void BeforeTheLink()
	{
		string cached = CachedHash();
		switch (_role)
		{
			case "fetch":
			case "drops":
			case "refused":
				Fact(cached == null && FilesInCache() == 0, $"before joining, the cache holds nothing ({FilesInCache()} files)");
				break;
			case "cached":
				Fact(cached == _hash, $"before joining, the cache holds the map under its hash, and it hashes to it ({MapTransfer.Short(cached)})");
				break;
			case "wrong":
				Fact(cached != null && cached != _hash,
					$"before joining, the cache holds a file under the map's hash that is not the map: it hashes to {MapTransfer.Short(cached)}");
				break;
		}
	}

	// ---- the client in the round ---------------------------------------------------------------

	private IEnumerator<bool> Client(int expectRequests, bool expectFetch)
	{
		long started = Stopwatch.GetTimestamp();
		foreach (bool _ in Until(() => NetworkManager.Instance is { Connected: true, ServerMapHash: not null }, 60 * Second))
		{
			yield return true;
		}

		Fact(!_timedOut, "connected, and the server named its map");
		if (_timedOut)
		{
			yield break;
		}

		NetworkManager net = NetworkManager.Instance;
		Fact(net.ServerMapHash == _hash && net.ServerMapId == string.Empty && net.ServerMapLength > 0
				&& net.ServerMapNonce is { Length: MapTransfer.NonceBytes },
			$"the server named map '{net.ServerMapName}' by its hash {MapTransfer.Short(net.ServerMapHash)}, {net.ServerMapLength:N0} B,"
			+ $" with no catalog id and a nonce of {net.ServerMapNonce?.Length ?? 0} B");

		foreach (bool _ in Until(() => Session.InGame && LevelRoot.Current?.Document != null, 120 * Second))
		{
			yield return true;
		}

		Fact(!_timedOut, $"in the game, {F(Seconds(started))} s after starting");
		if (_timedOut)
		{
			yield break;
		}

		MapTransferService transfer = net.MapTransfer;
		Fact(transfer.RequestsSent == expectRequests, $"it asked for the map {transfer.RequestsSent} times; {expectRequests} expected");
		if (expectFetch)
		{
			MapReceiver fetch = transfer.Fetch;
			Fact(fetch is { State: MapReceiveState.Complete } && fetch.Received == fetch.Chunks
					&& fetch.Chunks == MapTransfer.ChunkCount(net.ServerMapLength),
				$"the fetch is complete: {fetch?.Received ?? 0} of {fetch?.Chunks ?? 0} chunks, {fetch?.State}");
		}
		else
		{
			Fact(transfer.Fetch == null, "nothing was fetched");
		}

		string cached = CachedHash();
		Fact(cached == _hash, $"the cache holds the map under its hash, and it hashes to it ({MapTransfer.Short(cached)})");
		Fact(FilesInCache() == 1, $"and nothing else: {FilesInCache()} file(s) in the cache");
		Fact(Session.Map.IsData && Session.Map.Hash == _hash && Session.Map.Path == CachedPath && Session.Map.Id == string.Empty,
			$"this client plays {Session.Map}");

		TheWorldIsTheFiles(CachedPath);

		// In the round: the server has put this peer's character on the field, held off it until a
		// side is picked as anybody who has not picked one is.
		foreach (bool _ in Until(() => PlayerManager.Instance is { PlayerCount: > 0 } players && players.LocalStateName != "-",
			20 * Second))
		{
			yield return true;
		}

		Fact(!_timedOut, $"in the round: {PlayerManager.Instance?.PlayerCount ?? 0} on the roster, its own character among them"
			+ $" ({PlayerManager.Instance?.LocalStateName})");

		if (_role == "drops")
		{
			Drops();
		}

		// A few seconds of the round, for a demo recorded here to have something in it.
		for (int i = 0; i < 3 * Second; i++)
		{
			yield return true;
		}
	}

	/// <summary>The world was built from the document, which is the file, and its ground is where the file says.</summary>
	private void TheWorldIsTheFiles(string path)
	{
		LevelRoot level = LevelRoot.Current;
		Fact(level?.Document != null && ReferenceEquals(level.Document, Session.Document),
			"MapHost.tscn's LevelRoot built the world from the session's document");
		if (level?.Document == null)
		{
			return;
		}

		byte[] bytes = File.ReadAllBytes(path);
		Fact(MapFile.TryRead(bytes, out MapDocument file, out string error) && file.SameAs(level.Document, out error),
			$"the document the world was built from is {Path.GetFileName(path)} read again, field by field and sample by sample{(error == null ? string.Empty : ": " + error)}");

		PhysicsDirectSpaceState3D space = GetViewport().World3D.DirectSpaceState;
		var random = new Random(5);
		int met = 0;
		float worst = 0f;
		const int rays = 500;
		for (int i = 0; i < rays; i++)
		{
			float x = (float)((random.NextDouble() * 236d) - 118d);
			float z = (float)((random.NextDouble() * 236d) - 118d);
			var query = PhysicsRayQueryParameters3D.Create(new Vector3(x, 80f, z), new Vector3(x, -40f, z), CollisionLayers.World);
			Godot.Collections.Dictionary hit = space.IntersectRay(query);
			if (hit.Count > 0 && hit["collider"].AsGodotObject() == level.Terrain)
			{
				met++;
				worst = MathF.Max(worst, MathF.Abs(hit["position"].AsVector3().Y - level.Document.Terrain.Sample(x, z)));
			}
		}

		// Some rays meet an object first; every one that meets the ground meets it where the file says.
		Fact(met > rays * 0.8 && worst <= 0.001f,
			$"{met} of {rays} rays straight down met the ground first, the worst {F(worst * 1000f, "0.000")} mm from the file's");

		int spawnLines = level.Document.CountOf(PrefabGroup.SpawnLine);
		int markers = GetTree().GetNodesInGroup("player_spawn").Count;
		int nodes = GetTree().GetNodesInGroup(Gdpyr.Rts.ResourceNode.Group).Count;
		Fact(markers == 4 * spawnLines && nodes == level.Document.CountOf(PrefabGroup.ResourceNode),
			$"the file's pieces are in the world: {markers} spawn markers, {nodes} resource nodes");
	}

	// ---- the client whose fetch is refused -----------------------------------------------------

	private bool _refusalSeen;

	/// <summary>A refusal that never comes, and a game entered instead, is a failure the process exits 2 for.</summary>
	private IEnumerator<bool> Refused()
	{
		foreach (bool _ in Until(() => _refusalSeen || Session.InGame, 90 * Second))
		{
			yield return true;
		}

		if (!_refusalSeen)
		{
			Fact(false, Session.InGame ? $"the fetch was not refused: it is playing {Session.Map}" : "no refusal in 90 s");
			_failures = Math.Max(_failures, 2);
		}
	}

	/// <summary>
	/// Watched every frame, ahead of the menu, which drops the link and quits on the same frame it
	/// finds the refusal: the facts are written before it does.
	/// </summary>
	public override void _Process(double delta)
	{
		if (_role != "refused" || _refusalSeen || NetworkManager.Instance?.MapTransfer?.Fetch is not { } fetch
			|| fetch.State == MapReceiveState.Receiving)
		{
			return;
		}

		_refusalSeen = true;
		string expect = System.Environment.GetEnvironmentVariable("LE7_EXPECT") ?? "?";
		Fact(fetch.State == MapReceiveState.Refused && fetch.Error.Contains(expect, StringComparison.Ordinal),
			$"the fetch was refused, with '{expect}': {fetch.Error}");
		Measured($"{fetch.Received} of {fetch.Chunks} chunks had arrived");
		Fact(fetch.File == null && fetch.Document == null, "it keeps no bytes and no map");
		Fact(CachedHash() == null && FilesInCache() == 0, $"and nothing is in the cache: {FilesInCache()} files");
		Fact(!Session.InGame && Session.Map.Hash != _hash, $"and it is not playing the map: {Session.Map}");
		Finish();
	}

	// ---- the client behind a relay that drops ---------------------------------------------------

	private int _defaultProbes;
	private int _defaultAhead;
	private int _transferProbes;
	private int _transferAhead;
	private readonly HashSet<int> _heldChunks = new();

	private int Received => NetworkManager.Instance?.MapTransfer?.Fetch?.Received ?? 0;

	/// <summary>Server -> client, reliable, on the default channel: how many chunks had been sent when it was.</summary>
	[Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
	private void ProbeDefault(int sent, byte[] pad)
	{
		Trace("default", sent);
		_defaultProbes++;
		int received = Received;
		if (received < sent)
		{
			// A chunk sent before this message has not arrived: the default channel was not held back
			// behind it. With the relay's delay fixed, a chunk is behind a later message only when it,
			// or one ahead of it on its channel, was lost and is being sent again.
			_defaultAhead++;
			_heldChunks.Add(received);
		}
	}

	/// <summary>The same on the map's own channel: the control, which ENet must hold back behind any chunk sent before it.</summary>
	[Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable,
		TransferChannel = MapTransferService.Channel)]
	private void ProbeTransfer(int sent, byte[] pad)
	{
		Trace("map's", sent);
		_transferProbes++;
		_transferAhead += Received < sent ? 1 : 0;
	}

	private static readonly bool Tracing = System.Environment.GetEnvironmentVariable("LE7_TRACE") == "1";

	/// <summary>With <c>LE7_TRACE=1</c>, a line for each probe as it arrives: when, in which frame, and what had arrived.</summary>
	private void Trace(string channel, int sent)
	{
		if (Tracing)
		{
			GD.Print($"[checks] trace {_role}: {F(Time.GetTicksUsec() / 1000.0, "0.0")} ms, frame {Engine.GetProcessFrames()}:"
				+ $" the {channel} channel's probe sent after chunk {sent - 1}; {Received} chunks in");
		}
	}

	/// <summary>
	/// What arrived ahead of what. Each channel is also held behind its own lost messages, as a
	/// reliable channel is, so how long a probe was ahead of a chunk says nothing of when that chunk
	/// was sent again, and is not measured.
	/// </summary>
	private void Drops()
	{
		Measured($"drops: {_defaultProbes} default-channel probes, {_defaultAhead} of them ahead of a chunk sent before them,"
			+ $" past {_heldChunks.Count} chunk(s) not yet arrived;"
			+ $" {_transferProbes} probes on the map's channel, {_transferAhead} ahead of a chunk");
		Fact(_transferProbes > 0 && _transferAhead == 0,
			$"on the map's own channel, no probe of {_transferProbes} arrived ahead of a chunk sent before it: the control");
		if (!string.IsNullOrEmpty(_dir))
		{
			File.WriteAllText(Path.Combine(_dir, $"drops-{OS.GetProcessId()}"),
				$"{_defaultProbes} {_defaultAhead} {_heldChunks.Count} {_transferProbes} {_transferAhead}");
		}
	}

	// ---- the demo -------------------------------------------------------------------------------

	private IEnumerator<bool> Playback()
	{
		foreach (bool _ in Until(() => Session.InGame && PlayerManager.Instance?.Playback != null, 60 * Second))
		{
			yield return true;
		}

		Fact(!_timedOut, "in the map, a demo playing");
		if (_timedOut)
		{
			yield break;
		}

		DemoPlayback playback = PlayerManager.Instance.Playback;
		Fact(playback.Header.Map == "map:" + _hash, $"the demo's header names its map {playback.Header.Map}");
		Fact(Session.Map.IsData && Session.Map.Hash == _hash && Session.Map.Path == CachedPath,
			$"and it is played on that map, from the cache: {Session.Map}");
		TheWorldIsTheFiles(CachedPath);

		uint first = playback.Tick;
		foreach (bool _ in Until(() => playback.Ended || playback.TicksPlayed > 2 * Second, 30 * Second))
		{
			yield return true;
		}

		Fact(playback.TicksPlayed > 0 && PlayerManager.Instance.IsDemoSession,
			$"it plays: from tick {first} to {playback.Tick}, {playback.TicksPlayed} ticks played{(playback.Ended ? ", to its end" : string.Empty)};"
			+ $" {PlayerManager.Instance.PlayerCount} on the roster");
	}

	// ---- the server -------------------------------------------------------------------------------

	private sealed class Peer
	{
		public int Id;
		public string Phase;
		public string Fault;
		public int Requests;
		public int Sent;
		public bool Left;
		public long Joined;
		public long CutAt;
		public int CutSent;
		public double LeftAfterCut = -1;
		public int Forged;
		public double LeftAfterForged = -1;
		public long ForgedDone;
	}

	private readonly List<Peer> _peers = new();
	private Peer _forging;
	private int _probesSent;

	private IEnumerator<bool> Server()
	{
		foreach (bool _ in Until(() => Session.InGame && NetworkManager.Instance?.MapTransfer != null, 60 * Second))
		{
			yield return true;
		}

		Fact(Session.Map.IsData && Session.Map.Hash == _hash && Session.Bytes != null && MapFile.HashHex(Session.Bytes) == _hash,
			$"the server plays {Session.Map}, {Session.Bytes?.Length ?? 0:N0} B loaded, hashing to it");
		NetworkManager net = NetworkManager.Instance;
		MapTransferService service = net.MapTransfer;
		int chunks = MapTransfer.ChunkCount(Session.Bytes.Length);
		byte[] forged = (byte[])Session.Bytes.Clone();
		forged[forged.Length / 2] ^= 0x10;

		net.PeerJoined += id => _peers.Add(new Peer
		{
			Id = id, Phase = Read("phase"), Fault = Read("fault"), Joined = Stopwatch.GetTimestamp(),
		});
		net.PeerLeft += id =>
		{
			foreach (Peer peer in _peers)
			{
				if (peer.Id == id && !peer.Left)
				{
					peer.Left = true;
					peer.Requests = Math.Max(peer.Requests, service.RequestsFrom(id));
					if (peer.CutAt != 0)
					{
						peer.LeftAfterCut = Seconds(peer.CutAt);
						service.SetPhysicsProcess(true);
					}

					if (peer.ForgedDone != 0)
					{
						peer.LeftAfterForged = Seconds(peer.ForgedDone);
					}

					GD.Print($"[checks] server: peer {id} ({peer.Phase}) left: asked {peer.Requests} times, sent {peer.Sent} of {chunks} chunks"
						+ (peer.Forged > 0 ? $", {peer.Forged} forged" : string.Empty));
				}
			}

			if (_forging?.Id == id)
			{
				_forging = null;
			}
		};

		while (!Has("stop"))
		{
			// A forged stream is the service's turned off and this node's sent in its place.
			service.SetPhysicsProcess(Read("fault") != "mismatch" && !_peers.Exists(p => p.CutAt != 0 && !p.Left));

			foreach (Peer peer in _peers)
			{
				if (peer.Left)
				{
					continue;
				}

				peer.Requests = Math.Max(peer.Requests, service.RequestsFrom(peer.Id));
				peer.Sent = Math.Max(peer.Sent, service.ChunksSentTo(peer.Id));

				if (peer.Fault == "cut" && peer.CutAt == 0 && peer.Sent >= chunks / 2)
				{
					service.SetPhysicsProcess(false);
					peer.CutAt = Stopwatch.GetTimestamp();
					peer.CutSent = peer.Sent;
					GD.Print($"[checks] server: the map's sender stopped at {peer.Sent} of {chunks} chunks for peer {peer.Id}");
				}

				if (peer.Fault == "mismatch" && service.IsSendingTo(peer.Id) && _forging == null && peer.Forged == 0)
				{
					_forging = peer;
				}

				if (peer.Phase == "drops" && service.IsSendingTo(peer.Id))
				{
					int sent = service.ChunksSentTo(peer.Id);
					var pad = new byte[ProbePadBytes];
					RpcId(peer.Id, MethodName.ProbeDefault, sent, pad);
					RpcId(peer.Id, MethodName.ProbeTransfer, sent, pad);
					_probesSent++;
				}
			}

			if (_forging is { Left: false } forging && forging.Forged < chunks)
			{
				service.RpcId(forging.Id, "ServerMapChunk", MapTransfer.Packet(forged, forging.Forged));
				forging.Forged++;
				if (forging.Forged == chunks)
				{
					forging.ForgedDone = Stopwatch.GetTimestamp();
					GD.Print($"[checks] server: sent peer {forging.Id} the map's {chunks} chunks with byte {forged.Length / 2} changed");
				}
			}

			yield return true;
		}

		ServerFacts(chunks);
	}

	private void ServerFacts(int chunks)
	{
		Measured($"{_peers.Count} peers joined; {_probesSent} pairs of probes sent");
		foreach (Peer peer in _peers)
		{
			string what = $"peer {peer.Id}, {peer.Phase}: asked {peer.Requests} times, sent {peer.Sent} of {chunks} chunks";
			switch (peer.Phase)
			{
				case "fetch":
				case "wrong":
				case "drops":
					Fact(peer.Requests == 1 && peer.Sent == chunks, what);
					break;
				case "cached":
					Fact(peer.Requests == 0 && peer.Sent == 0, what + ": the second join sent no request");
					break;
				case "cut":
					Fact(peer.Requests == 1 && peer.CutSent == chunks / 2 && peer.Sent == peer.CutSent && peer.Left
							&& peer.LeftAfterCut >= MapTransfer.StallSeconds && peer.LeftAfterCut < MapTransfer.StallSeconds + 3,
						what + $"; the sender stopped at {peer.CutSent}, and the peer dropped the link {F(peer.LeftAfterCut, "0.0")} s later");
					break;
				case "mismatch":
					Fact(peer.Requests == 1 && peer.Sent == 0 && peer.Forged == chunks && peer.Left && peer.LeftAfterForged >= 0
							&& peer.LeftAfterForged < 3,
						what + $"; {peer.Forged} chunks sent with a byte changed, and the peer dropped the link {F(peer.LeftAfterForged, "0.0")} s after the last");
					break;
				default:
					Measured(what);
					break;
			}
		}

		// The drop clients' counts, added up: a resent chunk held back nothing on the default channel.
		int clients = 0;
		long defaultProbes = 0, ahead = 0, held = 0, transferProbes = 0, transferAhead = 0;
		foreach (string file in Directory.GetFiles(_dir, "drops-*"))
		{
			string[] parts = File.ReadAllText(file).Split(' ');
			clients++;
			defaultProbes += long.Parse(parts[0], CultureInfo.InvariantCulture);
			ahead += long.Parse(parts[1], CultureInfo.InvariantCulture);
			held += long.Parse(parts[2], CultureInfo.InvariantCulture);
			transferProbes += long.Parse(parts[3], CultureInfo.InvariantCulture);
			transferAhead += long.Parse(parts[4], CultureInfo.InvariantCulture);
		}

		if (clients > 0)
		{
			Fact(ahead > 0 && held > 0 && transferProbes > 0 && transferAhead == 0,
				$"over {clients} fetches behind a relay that drops datagrams: {ahead} of {defaultProbes} default-channel messages arrived ahead of a chunk"
				+ $" sent before them, past {held} chunk(s) not yet arrived; on the map's channel {transferAhead} of {transferProbes}");
		}
	}
}
