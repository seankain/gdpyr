using System.Collections.Generic;
using System.Diagnostics;
using Gdpyr.Core;
using Gdpyr.Level;
using Gdpyr.Match;
using Gdpyr.Sim.Level;
using Godot;

namespace Gdpyr.Net;

/// <summary>
/// A map from a server to a client that lacks it (docs/LEVEL_EDITOR.md §6.10, §9.3, §9.4, LE7): the
/// two messages, their channel and their budget, around the engine-free <see cref="MapSender"/> and
/// <see cref="MapReceiver"/>.
///
/// Both messages are reliable on transfer channel 1, a channel of their own: on it, "reliable data
/// will only block delivery of other packets on that channel" (§3), so a chunk that has to be sent
/// again holds back the chunks after it and nothing on the default channel, where the round's
/// reliable messages are. <c>TransportFactory</c> is not touched: a host made with the default
/// channel count has 255.
///
/// A child of <see cref="NetworkManager"/>, made in its <c>_Ready</c>, so its path is the same on
/// every peer. The server sends a chunk a tick to each peer that asked, at most
/// <see cref="MapTransfer.MaxChunksPerTick"/> a tick in all, from <see cref="Session.Bytes"/>: the
/// bytes it loaded. The client's half is driven by the menu (<c>MainMenu</c>), which knows when the
/// server has named a map it lacks and decides what to do with the answer.
///
/// The other way, a map from a client to a dedicated server, and that server's next map (§9.5, LE8),
/// is six more messages on the same channel, around the engine-free <see cref="MapUploadGate"/> and
/// <see cref="MapUploader"/>: <c>ClientOfferMap</c> and its answer <c>ServerMapOffer</c>;
/// <c>ClientMapChunk</c>, a chunk a tick, as a fetch's are; <c>ServerMapStored</c>, what became of
/// them; <c>ClientSetNextMap</c> and its answer <c>ServerNextMap</c>. A server takes none of it
/// unless it was started with <c>--map-upload-token</c>, and then only from a peer that proves it
/// holds the token (<see cref="MapProof"/>); what it takes is hashed, read and validated, and written
/// to its cache and nowhere else. Told its next map, it writes <c>maps/active</c> and, when the round
/// in progress is over, exits 0 for its supervisor to start it again (LE-D18). The client's half is
/// driven by the editor's <b>Publish</b> (<c>MapEditorRoot</c>).
/// </summary>
public partial class MapTransferService : Node
{
	/// <summary>The transfer channel the map's messages go on (§9.4).</summary>
	public const int Channel = 1;

	/// <summary>Bytes counted for a request, for the bandwidth HUD: a hash and its header.</summary>
	private const int RequestBytes = 72;

	/// <summary>Bytes counted for an offer: a hash as text (72), a length (5), a proof (40) and a header.</summary>
	private const int OfferBytes = 124;

	/// <summary>Bytes counted for an answer that is one small number, and its header.</summary>
	private const int AnswerBytes = 9;

	/// <summary>Bytes counted for a next-map request: a hash as text (72), a proof (40) and a header.</summary>
	private const int NextBytes = 119;

	/// <summary>Bytes counted for a message that is a hash as text (72), and a header, before anything said with it.</summary>
	private const int HashBytes = 79;

	public static MapTransferService Instance { get; private set; }

	private MapSender _sender;
	private readonly List<(int Peer, int Index)> _tick = new();
	private readonly Dictionary<int, long> _sendingSince = new();
	private readonly Dictionary<int, int> _requests = new();

	private long _fetchStarted;

	/// <summary>Server: who is answered what, and whose chunks are taken (§9.5). Takes nothing with no token.</summary>
	private MapUploadGate _gate = new(null);

	private long _uploadStarted;

	/// <summary>Whether something starts this process again when it exits (<see cref="MapChange.Supervised"/>).</summary>
	private bool _supervised;

	/// <summary>Server, leaving: real time left before the link is closed and the process goes.</summary>
	private double _leaveIn;
	private bool _gone;

	/// <summary>Client: the map being fetched, or the last one fetched on this connection; null before.</summary>
	public MapReceiver Fetch { get; private set; }

	/// <summary>Client: what the server called the map being fetched, for the status line.</summary>
	public string FetchName { get; private set; }

	/// <summary>Client: requests this process has sent on this connection. For the checks and the log.</summary>
	public int RequestsSent { get; private set; }

	/// <summary>Server: requests a peer has made on its connection. For the checks and the log.</summary>
	public int RequestsFrom(int peer) => _requests.TryGetValue(peer, out int count) ? count : 0;

	/// <summary>Server: every request made since the process started. For the checks.</summary>
	public int RequestsReceived { get; private set; }

	/// <summary>Server: chunks a peer has been sent of the map. For the checks.</summary>
	public int ChunksSentTo(int peer) => _sender?.SentTo(peer) ?? 0;

	/// <summary>Server: whether a peer is still owed chunks.</summary>
	public bool IsSendingTo(int peer) => _sender?.IsSendingTo(peer) ?? false;

	/// <summary>Server: the map's chunks, once a request has made the sender; 0 before or on a scene map.</summary>
	public int Chunks => _sender?.Chunks ?? 0;

	/// <summary>Client: the map being offered or sent to the server, or the last one on this connection; null before.</summary>
	public MapUploader Upload { get; private set; }

	/// <summary>Client: what the map being sent is called, for the status line.</summary>
	public string UploadName { get; private set; }

	/// <summary>Client: the hash last asked to be the server's next map on this connection; null before.</summary>
	public string NextMapAsked { get; private set; }

	/// <summary>Client: the server's answer to that, as it came off the wire; null until it has answered.</summary>
	public int? NextMapAnswer { get; private set; }

	/// <summary>Client: every answer to an offer heard on this connection, and the last, as it came off the wire. For the checks.</summary>
	public int OfferAnswersHeard { get; private set; }

	public int? LastOfferAnswerHeard { get; private set; }

	/// <summary>Client: every answer to a next-map request heard on this connection, and the last. For the checks.</summary>
	public int NextAnswersHeard { get; private set; }

	public int? LastNextAnswerHeard { get; private set; }

	/// <summary>Client: every word on what became of an upload heard on this connection, and the last one's reason. For the checks.</summary>
	public int StoredHeard { get; private set; }

	public string LastStoredError { get; private set; }

	/// <summary>Server: whether it takes uploads at all: it was started with <c>--map-upload-token</c>.</summary>
	public bool TakesUploads => _gate.Enabled;

	/// <summary>Server: the peer whose upload is being taken; 0 when none is.</summary>
	public int UploadingPeer => _gate.UploaderPeer;

	/// <summary>Server: chunks taken of the upload under way. For the checks.</summary>
	public int UploadChunksTaken => _gate.Receiving?.Received ?? 0;

	/// <summary>Server: offers answered since the process started, and the last answer given. For the checks and the log.</summary>
	public int OffersAnswered { get; private set; }

	public MapOfferAnswer LastOfferAnswer { get; private set; }

	/// <summary>Server: uploads hashed, read, validated and written to the cache since the process started.</summary>
	public int UploadsKept { get; private set; }

	/// <summary>Server: uploads taken and then refused, or not kept.</summary>
	public int UploadsRefused { get; private set; }

	/// <summary>Server: chunks dropped because they came from a peer whose offer was not taken.</summary>
	public int ChunksDropped { get; private set; }

	/// <summary>Server: next-map requests answered since the process started, and the last answer given.</summary>
	public int NextMapsAnswered { get; private set; }

	public MapNextAnswer LastNextAnswer { get; private set; }

	/// <summary>
	/// Server: the hash of the map this process leaves for when its round is over (§9.5); null when
	/// it is told none, or told the one it is playing.
	/// </summary>
	public string NextMap { get; private set; }

	/// <summary>Server: it is on its way out, for its supervisor to start it on <see cref="NextMap"/>.</summary>
	public bool Leaving { get; private set; }

	/// <summary>Server: raised once, as it leaves, before the link is closed. For the checks.</summary>
	public event System.Action LeavingForNextMap;

	public override void _Ready()
	{
		Instance = this;

		// As the clock: a pause menu does not stop a map arriving.
		ProcessMode = ProcessModeEnum.Always;

		// Off unless the command line gave a token, which it gives only to a dedicated server (§9.5).
		_gate = new MapUploadGate(Bootstrap.Options.MapUploadToken);
		_supervised = MapChange.Supervised(System.Environment.GetEnvironmentVariable(MapChange.SystemdVariable),
			System.Environment.GetEnvironmentVariable(MapChange.SupervisedVariable));
		if (_gate.Enabled)
		{
			GD.Print("[maps] this server takes maps from clients that hold its token, to "
				+ $"{MapLibrary.CacheRoot}, and lets them name its next map"
				+ (Bootstrap.Options.MapReference != null ? " — which --map overrides: it plays that map whenever it starts"
					: _supervised ? string.Empty : "; nothing here starts it again when it leaves for one"));
		}
	}

	public override void _ExitTree()
	{
		if (Instance == this)
		{
			Instance = null;
		}
	}

	// ---- the client's half -------------------------------------------------------------------

	/// <summary>
	/// Asks the server for the map it named, whose hash and length it gave (§9.3). False, with why,
	/// when no map can be what it named: then nothing is asked.
	/// </summary>
	public bool BeginFetch(string hash, int length, string name, out string error)
	{
		NetworkManager net = NetworkManager.Instance;
		if (net is not { IsClient: true, Connected: true })
		{
			error = "not connected to a server";
			return false;
		}

		if (!MapReceiver.TryStart(hash, length, out MapReceiver receiver, out error))
		{
			return false;
		}

		Fetch = receiver;
		FetchName = string.IsNullOrEmpty(name) ? MapTransfer.Short(hash) : name;
		_fetchStarted = Stopwatch.GetTimestamp();
		RpcId(1, MethodName.ClientRequestMap, hash);
		RequestsSent++;
		net.Stats.RecordSent(RequestBytes);
		GD.Print($"[maps] fetching '{FetchName}' from the server: {length:N0} B in {receiver.Chunks} chunks,"
			+ $" sha-256 {hash}");
		return true;
	}

	/// <summary>Client: forgets the fetch, when the link it was on is dropped.</summary>
	public void ForgetFetch()
	{
		Fetch = null;
		FetchName = null;
		RequestsSent = 0;
	}

	/// <summary>Client -> server: the map named by this hash, please. Once a connection.</summary>
	[Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false,
		TransferMode = MultiplayerPeer.TransferModeEnum.Reliable, TransferChannel = Channel)]
	private void ClientRequestMap(string hash)
	{
		NetworkManager net = NetworkManager.Instance;
		if (net is not { IsServer: true })
		{
			return;
		}

		int peer = Multiplayer.GetRemoteSenderId();
		net.Stats.RecordReceived(RequestBytes);
		_requests[peer] = RequestsFrom(peer) + 1;
		RequestsReceived++;

		if (Session.Bytes == null)
		{
			GD.PushWarning($"[maps] peer {peer} asked for map {MapTransfer.Short(hash)}, and this server plays a scene map");
			return;
		}

		_sender ??= new MapSender(Session.Bytes);
		MapRequestAnswer answer = _sender.Request(peer, hash);
		if (answer == MapRequestAnswer.Sending)
		{
			_sendingSince[peer] = Stopwatch.GetTimestamp();
			GD.Print($"[maps] sending '{Session.Map.Name}' to peer {peer}: {_sender.Length:N0} B in {_sender.Chunks} chunks");
		}
		else
		{
			GD.PushWarning($"[maps] peer {peer} asked for map {MapTransfer.Short(hash)}: {answer}; it is sent nothing");
		}
	}

	/// <summary>Server -> the peer that asked: one chunk, behind its index.</summary>
	[Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false,
		TransferMode = MultiplayerPeer.TransferModeEnum.Reliable, TransferChannel = Channel)]
	private void ServerMapChunk(byte[] packet)
	{
		NetworkManager net = NetworkManager.Instance;
		if (net is not { IsClient: true })
		{
			return;
		}

		net.Stats.RecordReceived((packet?.Length ?? 0) + MapTransfer.RpcHeaderBytes);
		if (Fetch is not { State: MapReceiveState.Receiving } fetch)
		{
			return;
		}

		MapReceiveState state = fetch.Accept(packet ?? System.Array.Empty<byte>());
		if (state != MapReceiveState.Receiving)
		{
			Finished(fetch);
		}
	}

	/// <summary>The stall's clock: real time, since the menu that waits for the map does not tick.</summary>
	public override void _Process(double delta)
	{
		if (Fetch is { State: MapReceiveState.Receiving } fetch && fetch.Advance(delta) != MapReceiveState.Receiving)
		{
			Finished(fetch);
		}

		// An upload's two clocks, real time as a fetch's is: the client's, for a server that owes an
		// answer; the server's, for a stream that stopped.
		if (Upload is { Done: false } upload)
		{
			upload.Advance(delta);
			if (upload.Done)
			{
				UploadOver(upload);
			}
		}

		if (_gate.Receiving != null)
		{
			_gate.Advance(delta);
			if (_gate.Finished)
			{
				FinishUpload();
			}
		}

		// Leaving for the next map: the link stays up for what was said on it to arrive, then goes.
		if (Leaving && !_gone)
		{
			_leaveIn -= delta;
			if (_leaveIn <= 0)
			{
				_gone = true;

				// Closing it tells every client now, where a process that only went would leave them to time out.
				if (Multiplayer.HasMultiplayerPeer())
				{
					Multiplayer.MultiplayerPeer.Close();
				}

				GetTree().Quit(0);
			}
		}
	}

	private void Finished(MapReceiver fetch)
	{
		double seconds = (Stopwatch.GetTimestamp() - _fetchStarted) / (double)Stopwatch.Frequency;
		if (fetch.State == MapReceiveState.Complete)
		{
			GD.Print($"[maps] fetched '{FetchName}': {fetch.Length:N0} B in {fetch.Chunks} chunks, {seconds:0.00} s;"
				+ $" it hashes to {fetch.Hash}, reads and validates ({fetch.Report.Warnings} warnings)");
		}
		else
		{
			GD.PrintErr($"[maps] refused '{FetchName}' after {seconds:0.00} s: {fetch.Error}");
		}
	}

	// ---- the server's half -------------------------------------------------------------------

	public override void _PhysicsProcess(double delta)
	{
		NetworkManager net = NetworkManager.Instance;
		if (net is { IsClient: true })
		{
			SendUploadChunk(net);
			return;
		}

		if (net is not { IsServer: true })
		{
			return;
		}

		LeaveIfNoRoundIsInTheWay();
		if (_sender == null || _sender.Sending == 0)
		{
			return;
		}

		_sender.Tick(_tick);
		foreach ((int peer, int index) in _tick)
		{
			byte[] packet = _sender.Packet(index);
			RpcId(peer, MethodName.ServerMapChunk, packet);
			net.Stats.RecordSent(packet.Length + MapTransfer.RpcHeaderBytes);
			if (!_sender.IsSendingTo(peer) && _sendingSince.Remove(peer, out long since))
			{
				GD.Print($"[maps] sent '{Session.Map.Name}' to peer {peer}: {_sender.Chunks} chunks,"
					+ $" {_sender.Length:N0} B, in {(Stopwatch.GetTimestamp() - since) / (double)Stopwatch.Frequency:0.00} s");
			}
		}
	}

	/// <summary>Server: a peer's connection is gone, and what it asked for with it.</summary>
	public void ForgetPeer(int peer)
	{
		if (_sender != null && _sender.IsSendingTo(peer))
		{
			GD.Print($"[maps] peer {peer} left with {_sender.SentTo(peer)} of {_sender.Chunks} chunks sent");
		}

		_sender?.Forget(peer);
		_sendingSince.Remove(peer);
		_requests.Remove(peer);

		if (_gate.UploaderPeer == peer && peer != 0)
		{
			MapReceiver upload = _gate.Receiving;
			GD.Print($"[maps] peer {peer} left with {upload?.Received ?? 0} of {upload?.Chunks ?? 0} chunks of its upload taken:"
				+ " nothing of it is kept");
			_gate.Forget(peer);
			UploadsRefused++;
		}
	}

	// ---- an upload: the client's half (§9.5, LE8) ----------------------------------------------

	/// <summary>
	/// Offers the server a map, with the token's proof over this connection's nonce (§6.10). False,
	/// with why, when there is nothing to offer it over: no link, or a server that sent no nonce.
	/// The token itself goes nowhere.
	/// </summary>
	public bool BeginUpload(byte[] file, string name, string token, out string error)
	{
		NetworkManager net = NetworkManager.Instance;
		if (net is not { IsClient: true, Connected: true })
		{
			error = "not connected to a server";
			return false;
		}

		if (Upload is { Done: false })
		{
			error = "a map is being sent to this server already";
			return false;
		}

		if (file == null || file.Length == 0 || file.Length > MapLimits.MaxFileBytes)
		{
			error = $"a map is 1 to {MapLimits.MaxFileBytes:N0} bytes";
			return false;
		}

		var uploader = new MapUploader(file);
		byte[] proof = MapProof.Compute(token, net.ServerMapNonce, uploader.Hash, MapProofOp.Upload);
		if (proof == null)
		{
			error = string.IsNullOrEmpty(token) ? "a token is needed" : "the server sent nothing to prove the token over: it is an older build";
			return false;
		}

		Upload = uploader;
		UploadName = string.IsNullOrEmpty(name) ? MapTransfer.Short(uploader.Hash) : name;
		_uploadStarted = Stopwatch.GetTimestamp();
		RpcId(1, MethodName.ClientOfferMap, uploader.Hash, uploader.Length, proof);
		net.Stats.RecordSent(OfferBytes);
		GD.Print($"[maps] offering '{UploadName}' to the server: {uploader.Length:N0} B in {uploader.Chunks} chunks,"
			+ $" sha-256 {uploader.Hash}");
		error = null;
		return true;
	}

	/// <summary>
	/// Asks the server to play a map it has when it next starts (§9.5), with the token's proof for
	/// that over this connection's nonce. False, with why, when there is nothing to ask it over.
	/// </summary>
	public bool RequestNextMap(string hash, string token, out string error)
	{
		NetworkManager net = NetworkManager.Instance;
		if (net is not { IsClient: true, Connected: true })
		{
			error = "not connected to a server";
			return false;
		}

		byte[] proof = MapProof.Compute(token, net.ServerMapNonce, hash, MapProofOp.SetNext);
		if (proof == null)
		{
			error = !MapTransfer.IsHash(hash) ? $"'{hash}' is not a map's hash"
				: string.IsNullOrEmpty(token) ? "a token is needed" : "the server sent nothing to prove the token over: it is an older build";
			return false;
		}

		NextMapAsked = hash;
		NextMapAnswer = null;
		RpcId(1, MethodName.ClientSetNextMap, hash, proof);
		net.Stats.RecordSent(NextBytes);
		GD.Print($"[maps] asking the server to play {MapTransfer.Short(hash)} next");
		error = null;
		return true;
	}

	/// <summary>Client: forgets the upload and the request, when the link they were on is dropped.</summary>
	public void ForgetUpload()
	{
		Upload = null;
		UploadName = null;
		NextMapAsked = null;
		NextMapAnswer = null;
		OfferAnswersHeard = 0;
		LastOfferAnswerHeard = null;
		NextAnswersHeard = 0;
		LastNextAnswerHeard = null;
		StoredHeard = 0;
		LastStoredError = null;
	}

	/// <summary>Client: this tick's chunk of a map the server took the offer of.</summary>
	private void SendUploadChunk(NetworkManager net)
	{
		if (Upload is not { State: MapUploadState.Sending } upload || !net.Connected)
		{
			return;
		}

		byte[] packet = upload.NextPacket();
		RpcId(1, MethodName.ClientMapChunk, packet);
		net.Stats.RecordSent(packet.Length + MapTransfer.RpcHeaderBytes);
	}

	private void UploadOver(MapUploader upload)
	{
		double seconds = (Stopwatch.GetTimestamp() - _uploadStarted) / (double)Stopwatch.Frequency;
		if (upload.ServerHasIt)
		{
			GD.Print(upload.State == MapUploadState.HaveIt
				? $"[maps] the server has '{UploadName}' already ({MapTransfer.Short(upload.Hash)}): nothing was sent"
				: $"[maps] the server kept '{UploadName}': {upload.Length:N0} B in {upload.Chunks} chunks, {seconds:0.00} s");
		}
		else
		{
			GD.PrintErr($"[maps] '{UploadName}' was not published after {seconds:0.00} s: {upload.Error}");
		}
	}

	/// <summary>Server -> the peer that offered: its answer (§9.5), as <see cref="MapOfferAnswer"/>'s number.</summary>
	[Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false,
		TransferMode = MultiplayerPeer.TransferModeEnum.Reliable, TransferChannel = Channel)]
	private void ServerMapOffer(int answer)
	{
		NetworkManager net = NetworkManager.Instance;
		if (net is not { IsClient: true })
		{
			return;
		}

		net.Stats.RecordReceived(AnswerBytes);
		OfferAnswersHeard++;
		LastOfferAnswerHeard = answer;
		if (Upload is not { State: MapUploadState.Offered } upload)
		{
			return;
		}

		upload.Answered(answer);
		GD.Print($"[maps] the server answered the offer of '{UploadName}': "
			+ (upload.Answer is { } known ? MapAnswers.Name(known) : answer.ToString()));
		if (upload.Done)
		{
			UploadOver(upload);
		}
	}

	/// <summary>Server -> the peer whose upload it took: kept, when the reason is empty; else why not.</summary>
	[Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false,
		TransferMode = MultiplayerPeer.TransferModeEnum.Reliable, TransferChannel = Channel)]
	private void ServerMapStored(string hash, string error)
	{
		NetworkManager net = NetworkManager.Instance;
		if (net is not { IsClient: true })
		{
			return;
		}

		net.Stats.RecordReceived(HashBytes + (error?.Length ?? 0));
		StoredHeard++;
		LastStoredError = error ?? string.Empty;
		if (Upload is not { Done: false } upload)
		{
			return;
		}

		upload.Kept(hash, error);
		if (upload.Done)
		{
			UploadOver(upload);
		}
	}

	/// <summary>Server -> the peer that asked: its answer (§9.5), as <see cref="MapNextAnswer"/>'s number, and the map it is about.</summary>
	[Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false,
		TransferMode = MultiplayerPeer.TransferModeEnum.Reliable, TransferChannel = Channel)]
	private void ServerNextMap(int answer, string hash)
	{
		NetworkManager net = NetworkManager.Instance;
		if (net is not { IsClient: true })
		{
			return;
		}

		net.Stats.RecordReceived(HashBytes + AnswerBytes);
		NextAnswersHeard++;
		LastNextAnswerHeard = answer;
		if (NextMapAsked == null || NextMapAnswer != null || !string.Equals(hash, NextMapAsked, System.StringComparison.Ordinal))
		{
			return;
		}

		NextMapAnswer = answer;
		GD.Print($"[maps] the server answered the next map, {MapTransfer.Short(hash)}: "
			+ (MapAnswers.TryNext(answer, out MapNextAnswer known) ? MapAnswers.Name(known) : answer.ToString()));
	}

	// ---- an upload: the server's half ----------------------------------------------------------

	/// <summary>Whether this server has a map with this hash that plays on its build: bundled, in its cache, or its own.</summary>
	private static bool HasPlayable(string hash) =>
		MapLibrary.TryFindHash(hash, out _, out _, out _, out _, out _, out _);

	/// <summary>Client -> server: this map, please take it; the proof is the token's, over this connection's nonce.</summary>
	[Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false,
		TransferMode = MultiplayerPeer.TransferModeEnum.Reliable, TransferChannel = Channel)]
	private void ClientOfferMap(string hash, int length, byte[] proof)
	{
		NetworkManager net = NetworkManager.Instance;
		if (net is not { IsServer: true })
		{
			return;
		}

		int peer = Multiplayer.GetRemoteSenderId();
		net.Stats.RecordReceived(OfferBytes);
		MapOfferAnswer answer = _gate.Offer(peer, net.MapNonceOf(peer), hash, length, proof, HasPlayable);
		OffersAnswered++;
		LastOfferAnswer = answer;
		if (answer == MapOfferAnswer.Ok)
		{
			_uploadStarted = Stopwatch.GetTimestamp();
			GD.Print($"[maps] taking an upload from peer {peer}: {length:N0} B in {_gate.Receiving.Chunks} chunks, sha-256 {hash}");
		}
		else
		{
			// What a peer without the token offered is not worth a line that names it.
			GD.Print($"[maps] peer {peer} offered a map: {MapAnswers.Name(answer)}"
				+ (answer is MapOfferAnswer.HaveIt or MapOfferAnswer.Busy or MapOfferAnswer.TooLarge
					? $" ({MapTransfer.Short(hash)}, {length:N0} B)" : string.Empty));
		}

		RpcId(peer, MethodName.ServerMapOffer, (int)answer);
		net.Stats.RecordSent(AnswerBytes);
	}

	/// <summary>Client -> server: one chunk of the map whose offer was taken, behind its index.</summary>
	[Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false,
		TransferMode = MultiplayerPeer.TransferModeEnum.Reliable, TransferChannel = Channel)]
	private void ClientMapChunk(byte[] packet)
	{
		NetworkManager net = NetworkManager.Instance;
		if (net is not { IsServer: true })
		{
			return;
		}

		int peer = Multiplayer.GetRemoteSenderId();
		net.Stats.RecordReceived((packet?.Length ?? 0) + MapTransfer.RpcHeaderBytes);
		if (!_gate.Chunk(peer, packet ?? System.Array.Empty<byte>()))
		{
			// From a peer whose offer was not taken: with no token, that is every peer.
			ChunksDropped++;
			return;
		}

		if (_gate.Finished)
		{
			FinishUpload();
		}
	}

	/// <summary>
	/// The upload is over (§6.10): whole, hashing to what was offered, a map, and one this build plays —
	/// then, and only then, written to the cache — or refused, and nothing of it kept. Its peer is told
	/// which, and the next offer is taken.
	/// </summary>
	private void FinishUpload()
	{
		MapReceiver upload = _gate.Release(out int peer);
		NetworkManager net = NetworkManager.Instance;
		if (upload == null || net == null)
		{
			return;
		}

		double seconds = (Stopwatch.GetTimestamp() - _uploadStarted) / (double)Stopwatch.Frequency;
		string error = upload.Error;
		if (upload.State == MapReceiveState.Complete)
		{
			if (MapLibrary.TryCache(upload.File, upload.Document, out MapInfo kept, out string unkept))
			{
				UploadsKept++;
				GD.Print($"[maps] kept the upload from peer {peer} as {kept.Path}: '{upload.Document.Name}', {upload.Length:N0} B in"
					+ $" {upload.Chunks} chunks, {seconds:0.00} s; it hashes to {upload.Hash}, reads and validates"
					+ $" ({upload.Report.Warnings} warnings)");
			}
			else
			{
				// The path is this box's business, not the peer's.
				GD.PrintErr($"[maps] the upload from peer {peer} was whole and valid, and was not kept: {unkept}");
				error = "the server's disk did not take it";
			}
		}

		if (!string.IsNullOrEmpty(error))
		{
			UploadsRefused++;
			GD.PushWarning($"[maps] refused the upload from peer {peer} after {seconds:0.00} s,"
				+ $" {upload.Received} of {upload.Chunks} chunks: {error}");
		}

		if (System.Array.IndexOf(Multiplayer.GetPeers(), peer) >= 0)
		{
			RpcId(peer, MethodName.ServerMapStored, upload.Hash, error ?? string.Empty);
			net.Stats.RecordSent(HashBytes + (error?.Length ?? 0));
		}
	}

	/// <summary>Client -> server: play this map when you next start; the proof is the token's for that, over this connection's nonce.</summary>
	[Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false,
		TransferMode = MultiplayerPeer.TransferModeEnum.Reliable, TransferChannel = Channel)]
	private void ClientSetNextMap(string hash, byte[] proof)
	{
		NetworkManager net = NetworkManager.Instance;
		if (net is not { IsServer: true })
		{
			return;
		}

		int peer = Multiplayer.GetRemoteSenderId();
		net.Stats.RecordReceived(NextBytes);
		string playing = Session.Map.IsData ? Session.Map.Hash : null;
		MapNextAnswer answer = _gate.Next(net.MapNonceOf(peer), hash, proof, HasPlayable, playing,
			pinned: Bootstrap.Options.MapReference != null, _supervised);
		if (MapAnswers.Accepted(answer))
		{
			if (!MapLibrary.TryWriteActive(hash, out string unwritten))
			{
				GD.PrintErr($"[maps] peer {peer} named the next map, and it was not written: {unwritten}");
				answer = MapNextAnswer.NotWritten;
			}
			else
			{
				NextMap = answer == MapNextAnswer.Playing ? null : hash;
				GD.Print($"[maps] peer {peer} named the next map: {MapLibrary.ActivePath} names {hash}; "
					+ (answer == MapNextAnswer.Playing ? "it is the map being played, and nothing restarts"
						: "this process exits 0 when the round in progress is over"
							+ (answer == MapNextAnswer.OkStaysDown
								? $", and nothing here starts it again: it was run by hand ({MapChange.SystemdVariable} is not set)"
								: ", for its supervisor to start it on that map")));
			}
		}
		else
		{
			GD.Print($"[maps] peer {peer} named the next map: {MapAnswers.Name(answer)}");
		}

		NextMapsAnswered++;
		LastNextAnswer = answer;
		RpcId(peer, MethodName.ServerNextMap, (int)answer, hash ?? string.Empty);
		net.Stats.RecordSent(HashBytes + AnswerBytes);
	}

	// ---- the next map (§9.5, LE-D18) -----------------------------------------------------------

	/// <summary>
	/// A server told its next map goes at once when no round is in progress, or when nobody is
	/// connected (<see cref="MapChange.LeavesNow"/>). Otherwise the round's end is where it goes
	/// (<see cref="LeaveForNextMap"/>, from <c>CombatManager</c>).
	/// </summary>
	private void LeaveIfNoRoundIsInTheWay()
	{
		if (NextMap == null || Leaving || !Session.InGame || CombatManager.Instance is not { } combat)
		{
			return;
		}

		bool roundInProgress = combat.Match.Phase != RoundPhase.Warmup;
		int peers = Multiplayer.HasMultiplayerPeer() ? Multiplayer.GetPeers().Length : 0;
		if (MapChange.LeavesNow(true, roundInProgress, peers))
		{
			LeaveForNextMap(roundInProgress ? "nobody is connected" : "no round is in progress");
		}
	}

	/// <summary>
	/// Leaves for the next map, if this server was told one (§9.5): after
	/// <see cref="MapChange.LeaveGraceSeconds"/>, for what it has said to arrive, every client is
	/// dropped and the process exits 0. The supervisor starts it again, and it plays what
	/// <c>maps/active</c> names. True when it is leaving: the round that would have started does not.
	/// </summary>
	public bool LeaveForNextMap(string why)
	{
		if (NextMap == null || NetworkManager.Instance is not { IsServer: true })
		{
			return false;
		}

		if (Leaving)
		{
			return true;
		}

		Leaving = true;
		_leaveIn = MapChange.LeaveGraceSeconds;
		GD.Print($"[maps] {why}: leaving for the next map, {NextMap}; exit 0 in {MapChange.LeaveGraceSeconds:0} s"
			+ (_supervised ? string.Empty : ", and nothing here starts this server again"));
		LeavingForNextMap?.Invoke();
		return true;
	}
}
