using System.Collections.Generic;
using System.Diagnostics;
using Gdpyr.Core;
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
/// </summary>
public partial class MapTransferService : Node
{
	/// <summary>The transfer channel the map's messages go on (§9.4).</summary>
	public const int Channel = 1;

	/// <summary>Bytes counted for a request, for the bandwidth HUD: a hash and its header.</summary>
	private const int RequestBytes = 72;

	public static MapTransferService Instance { get; private set; }

	private MapSender _sender;
	private readonly List<(int Peer, int Index)> _tick = new();
	private readonly Dictionary<int, long> _sendingSince = new();
	private readonly Dictionary<int, int> _requests = new();

	private long _fetchStarted;

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

	public override void _Ready()
	{
		Instance = this;

		// As the clock: a pause menu does not stop a map arriving.
		ProcessMode = ProcessModeEnum.Always;
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
		if (_sender == null || _sender.Sending == 0 || net is not { IsServer: true })
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
	}
}
