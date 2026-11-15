using System;
using Gdpyr.Core;
using Gdpyr.Sim;
using Godot;

namespace Gdpyr.Net;

/// <summary>
/// Second autoload: the transport, the clocks and the counters behind them.
///
/// It owns the peer, the server's authoritative tick, the client's
/// <see cref="SimClock"/> and the RTT probe that feeds it. It deliberately knows
/// nothing about characters or gameplay — that is <see cref="PlayerManager"/>,
/// which drives this one tick by tick through <see cref="BeginTick"/> so the
/// order of the two is explicit rather than an accident of autoload order.
/// </summary>
public partial class NetworkManager : Node
{
	public static NetworkManager Instance { get; private set; }

	/// <summary>Probe timestamps kept in flight. Eight covers four seconds at 2 Hz.</summary>
	private const int ProbeSlots = 8;

	/// <summary>Payload sizes for the clock probe, for the bandwidth counters.</summary>
	private const int ProbeBytes = 6;
	private const int ReplyBytes = 10;

	private const int HeartbeatIntervalTicks = SimConfig.TickRate * 5;

	public LaunchMode Mode { get; private set; } = LaunchMode.Offline;

	public bool IsServer => Mode is LaunchMode.Server or LaunchMode.Listen;
	public bool IsClient => Mode == LaunchMode.Client;
	public bool IsOffline => Mode == LaunchMode.Offline;

	/// <summary>True when this process simulates a character the local player drives.</summary>
	public bool HasLocalPlayer => Mode is LaunchMode.Client or LaunchMode.Listen or LaunchMode.Offline;

	/// <summary>Godot's peer id for this process. 1 on a server, a listen host and offline.</summary>
	public int LocalPeerId { get; private set; } = 1;

	/// <summary>Client only: false until the handshake completes.</summary>
	public bool Connected { get; private set; }

	/// <summary>
	/// Client only: the link was refused, never answered, or dropped. The main menu
	/// reads it to decide whether "connecting…" is still true, which is the one
	/// question the transport could not answer before there was a menu to ask it
	/// (docs/LAN.md §4).
	/// </summary>
	public bool ConnectionFailed { get; private set; }

	/// <summary>Client only: the handshake is in flight. Neither connected nor refused.</summary>
	public bool IsConnecting => IsClient && !Connected && !ConnectionFailed;

	/// <summary>
	/// Client only: the id of the map the server is playing (<see cref="MapCatalog"/>),
	/// or null until it has said. The server says it first thing on every connection,
	/// before the roster, and the menu loads that map rather than its own default
	/// (docs/LAN.md §3.4). Empty for a data map that is not the catalog's, which is named by
	/// <see cref="ServerMapHash"/> (docs/LEVEL_EDITOR.md §9.3).
	/// </summary>
	public string ServerMapId { get; private set; }

	/// <summary>
	/// Client only: a data map's SHA-256, lower-case hex, as the server named it; empty for a scene
	/// map, null until the server has said (docs/LEVEL_EDITOR.md §9.3).
	/// </summary>
	public string ServerMapHash { get; private set; }

	/// <summary>Client only: a data map's file's length in bytes, as the server named it; 0 for a scene map.</summary>
	public int ServerMapLength { get; private set; }

	/// <summary>Client only: what the server calls its map, for the menu's status line.</summary>
	public string ServerMapName { get; private set; }

	/// <summary>
	/// Client only: the random bytes the server sent with its map on this connection, which an
	/// upload's proof is made over (docs/LEVEL_EDITOR.md §6.10, LE8).
	/// </summary>
	public byte[] ServerMapNonce { get; private set; }

	/// <summary>Server only: the nonce each connection was sent with the map.</summary>
	private readonly System.Collections.Generic.Dictionary<int, byte[]> _mapNonces = new();

	/// <summary>Server only: the nonce a peer's connection was sent with the map, or null.</summary>
	public byte[] MapNonceOf(int peerId) => _mapNonces.TryGetValue(peerId, out byte[] nonce) ? nonce : null;

	/// <summary>The fetch of a map a client lacks, and the server's half of it (docs/LEVEL_EDITOR.md §6.10).</summary>
	public MapTransferService MapTransfer { get; private set; }

	/// <summary>
	/// True once a transport has been chosen, whether by the command line or in the
	/// menu. A second choice is refused rather than layered on top of the first.
	/// </summary>
	public bool HasStarted { get; private set; }

	/// <summary>Answers LAN discovery while this process is a server. Null otherwise.</summary>
	public ServerAdvertiser Advertiser { get; private set; }

	/// <summary>
	/// The tick being simulated: authoritative on a server, the client's estimate of
	/// the server's tick otherwise (docs/NETCODE.md §2).
	/// </summary>
	public uint Tick { get; private set; }

	/// <summary>
	/// How many input ticks to sample and simulate this frame. Normally 1; 0 or 2
	/// when a client's clock is being nudged back into line.
	/// </summary>
	public int StepsThisTick { get; private set; }

	public SimClock Clock { get; } = new();
	public NetStats Stats { get; } = new();

	/// <summary>Client only: the server's physics frame time, piggybacked on the clock reply.</summary>
	public float ServerFrameMilliseconds { get; private set; }

	public event Action<int> PeerJoined;
	public event Action<int> PeerLeft;

	/// <summary>
	/// Server only: ticks of lag compensation owed to a peer's shots. Zero for the
	/// listen host's own character, which has none.
	/// </summary>
	public int LagCompensationTicks(int peerId) =>
		_peerLagTicks.TryGetValue(peerId, out int ticks) ? ticks : 0;

	/// <summary>
	/// Server only: how far back each peer's shots are compensated, in ticks
	/// (docs/NETCODE.md §4.3). Kept here because the clock probe is the only regular
	/// message that already knows about latency.
	/// </summary>
	private readonly System.Collections.Generic.Dictionary<int, int> _peerLagTicks = new();

	private readonly uint[] _probeIds = new uint[ProbeSlots];
	private readonly ulong[] _probeSentUsec = new ulong[ProbeSlots];
	private uint _nextProbeId;
	private float _probeTimer;

	public override void _Ready()
	{
		Instance = this;

		// The netcode keeps running while the local pause menu is up: stopping the
		// clock would strand the client several hundred ticks behind the server.
		ProcessMode = ProcessModeEnum.Always;

		for (int i = 0; i < ProbeSlots; i++)
		{
			_probeIds[i] = uint.MaxValue;
		}

		// Before any peer: its path, /root/NetworkManager/MapTransfer, is the same on every one.
		MapTransfer = new MapTransferService { Name = "MapTransfer" };
		AddChild(MapTransfer);

		LaunchOptions options = Bootstrap.Options;

		switch (options.Mode)
		{
			case LaunchMode.Server:
			case LaunchMode.Listen:
				Mode = options.Mode;
				HasStarted = true;
				StartServer(options.Port);
				break;

			case LaunchMode.Client:
				Mode = options.Mode;
				HasStarted = true;
				StartClient(options.Host, options.Port);
				break;

			default:
				// No mode flag: the main menu decides, and nothing is bound until it
				// does (Scripts/Ui/MainMenu.cs). A dedicated-server build never lands
				// here — LaunchOptions.Parse defaults it to Server.
				GD.Print("[net] no peer yet: waiting for the main menu");
				break;
		}
	}

	public override void _ExitTree()
	{
		if (Instance == this)
		{
			Instance = null;
		}
	}

	// ---- chosen in the menu (docs/LAN.md §4) -------------------------------

	/// <summary>
	/// Becomes the authority, with a local player. What "Host" in the main menu does.
	/// Returns false when the port could not be bound, which is a message for the
	/// menu rather than a reason to quit: nothing has been given up yet.
	/// </summary>
	public bool HostListen(int port)
	{
		if (HasStarted)
		{
			return false;
		}

		Error error = TransportFactory.CreateServer(port, out MultiplayerPeer peer);
		if (error != Error.Ok)
		{
			GD.PrintErr($"[net] could not listen on UDP {port}: {error}");
			return false;
		}

		Mode = LaunchMode.Listen;
		HasStarted = true;
		Multiplayer.MultiplayerPeer = peer;
		Multiplayer.PeerConnected += OnPeerConnected;
		Multiplayer.PeerDisconnected += OnPeerDisconnected;
		LocalPeerId = Multiplayer.GetUniqueId();
		Connected = true;
		GD.Print($"[net] listening on UDP {port} ({Mode})");

		StartAdvertising(port);
		return true;
	}

	/// <summary>
	/// Opens a socket to a server. ENet is asynchronous, so true here means the
	/// socket exists and the menu should wait on <see cref="Connected"/> and
	/// <see cref="ConnectionFailed"/>, not that anybody answered.
	/// </summary>
	public bool JoinServer(string host, int port)
	{
		if (HasStarted)
		{
			return false;
		}

		Mode = LaunchMode.Client;
		HasStarted = true;
		StartClient(host, port);

		if (Mode != LaunchMode.Client)
		{
			// StartClient resets the mode when the socket never opened.
			ConnectionFailed = true;
			HasStarted = false;
			return false;
		}

		return true;
	}

	/// <summary>
	/// Drops a link that never came up, or one to a host playing a map this build does
	/// not have, so the menu can try a different address.
	///
	/// Only ever called before the map is loaded: once it is there is a roster, a clock
	/// and a prediction ledger built around this peer, and none of them is torn down
	/// here. Leaving a round you are in is still quitting the process (docs/LAN.md §4).
	/// </summary>
	public void AbandonConnection()
	{
		if (!IsClient || Session.InGame)
		{
			return;
		}

		if (Multiplayer.HasMultiplayerPeer())
		{
			Multiplayer.MultiplayerPeer.Close();
			Multiplayer.MultiplayerPeer = null;
		}

		Multiplayer.ConnectedToServer -= OnConnectedToServer;
		Multiplayer.ConnectionFailed -= OnConnectionFailed;
		Multiplayer.ServerDisconnected -= OnServerDisconnected;

		Mode = LaunchMode.Offline;
		HasStarted = false;
		Connected = false;
		ConnectionFailed = false;
		ServerMapId = null;
		ServerMapHash = null;
		ServerMapLength = 0;
		ServerMapName = null;
		ServerMapNonce = null;
		MapTransfer?.ForgetFetch();
	}

	/// <summary>A round on your own, against the bots: no peer, no packets (docs/NETCODE.md §9).</summary>
	public void PlayOffline()
	{
		if (HasStarted)
		{
			return;
		}

		Mode = LaunchMode.Offline;
		HasStarted = true;
		GD.Print("[net] offline: no peer");
	}

	/// <summary>
	/// Opens a simulation tick. Called by <see cref="PlayerManager"/> at the top of
	/// its physics step, so that "advance the clock" and "simulate" cannot be
	/// reordered by a change to the autoload list.
	/// </summary>
	public void BeginTick()
	{
		if (IsClient)
		{
			StepsThisTick = Clock.Advance();
			Tick = Clock.ServerTick;
			SendClockProbe();
		}
		else
		{
			Tick++;
			StepsThisTick = HasLocalPlayer ? 1 : 0;
		}

		Stats.Advance(SimConfig.TickDelta);
		Heartbeat();
	}

	private void StartServer(int port)
	{
		Error error = TransportFactory.CreateServer(port, out MultiplayerPeer peer);
		if (error != Error.Ok)
		{
			// Fatal on purpose: `systemctl is-active` after a deploy must fail loudly
			// rather than leave a process up that never bound the port
			// (docs/DEPLOYMENT.md §3).
			GD.PrintErr($"[net] could not listen on UDP {port}: {error}");
			GetTree().Quit(1);
			return;
		}

		Multiplayer.MultiplayerPeer = peer;
		Multiplayer.PeerConnected += OnPeerConnected;
		Multiplayer.PeerDisconnected += OnPeerDisconnected;
		LocalPeerId = Multiplayer.GetUniqueId();
		Connected = true;
		GD.Print($"[net] listening on UDP {port} ({Mode})");

		StartAdvertising(port);
	}

	/// <summary>
	/// Opens the discovery socket so that other machines' menus can list this server
	/// (docs/LAN.md §4). Never fatal: a host that cannot be found still plays, and
	/// <c>--no-advertise</c> asks for exactly that.
	/// </summary>
	private void StartAdvertising(int port)
	{
		if (!Bootstrap.Options.Advertise || Advertiser != null)
		{
			return;
		}

		string name = Bootstrap.Options.ServerName ?? ServerAdvertiser.DefaultName();
		Advertiser = ServerAdvertiser.TryStart(name, port);

		if (Advertiser == null)
		{
			GD.PushWarning($"[net] every discovery port in {DiscoveryCodec.PortBase}"
				+ $"-{DiscoveryCodec.PortBase + DiscoveryCodec.PortSpan - 1} is taken;"
				+ " this server will not appear in anybody's browser");
			return;
		}

		AddChild(Advertiser);
		GD.Print($"[net] advertising '{name}' on UDP {Advertiser.Port} for UDP {port}");
	}

	private void StartClient(string host, int port)
	{
		Error error = TransportFactory.CreateClient(host, port, out MultiplayerPeer peer);
		if (error != Error.Ok)
		{
			// Keep Mode honest: without a peer this process is offline, whatever the
			// command line asked for.
			Mode = LaunchMode.Offline;
			GD.PrintErr($"[net] could not open a socket to {host}:{port}: {error}");
			return;
		}

		Multiplayer.MultiplayerPeer = peer;
		Multiplayer.ConnectedToServer += OnConnectedToServer;
		Multiplayer.ConnectionFailed += OnConnectionFailed;
		Multiplayer.ServerDisconnected += OnServerDisconnected;
		GD.Print($"[net] connecting to {host}:{port}");
	}

	// ---- clock probe (docs/NETCODE.md §2) ----------------------------------

	private void SendClockProbe()
	{
		if (!Connected)
		{
			return;
		}

		_probeTimer += SimConfig.TickDelta;
		if (_probeTimer < SimConfig.ClockProbeIntervalSeconds)
		{
			return;
		}
		_probeTimer = 0f;

		uint id = _nextProbeId++;
		int slot = (int)(id % ProbeSlots);
		_probeIds[slot] = id;
		_probeSentUsec[slot] = Time.GetTicksUsec();

		// The client's own RTT rides along: the server needs it to lag-compensate this
		// peer's shots, and this is the one message that already costs a round trip.
		// It is a claim rather than a measurement, so the server clamps it.
		var rttMilliseconds = (ushort)Mathf.Clamp(Clock.RttSeconds * 1000f, 0f, ushort.MaxValue);

		RpcId(1, MethodName.ClockProbe, id, rttMilliseconds);
		Stats.RecordSent(ProbeBytes);
	}

	/// <summary>
	/// Client -> server. Unreliable: a lost probe costs half a second, and a
	/// retransmitted one would measure the retransmission rather than the link.
	/// </summary>
	[Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false,
		TransferMode = MultiplayerPeer.TransferModeEnum.Unreliable)]
	private void ClockProbe(uint probeId, ushort clientRttMilliseconds)
	{
		if (!IsServer)
		{
			return;
		}

		Stats.RecordReceived(ProbeBytes);
		int sender = Multiplayer.GetRemoteSenderId();

		// Half the round trip, in ticks, capped: a peer claiming a second of latency
		// would otherwise be handed a second of rewind to shoot into.
		int lagTicks = Mathf.Clamp(
			Mathf.RoundToInt(clientRttMilliseconds * 0.5f / 1000f * SimConfig.TickRate),
			0, SimConfig.MaxLagCompensationTicks);
		_peerLagTicks[sender] = lagTicks;

		// Server frame time rides along: the HUD that matters is the client's, and
		// this is the only regular server->client message that is not per-tick.
		double physicsSeconds = (double)Performance.GetMonitor(Performance.Monitor.TimePhysicsProcess);
		ushort frameUsec = (ushort)Mathf.Clamp(physicsSeconds * 1_000_000.0, 0, ushort.MaxValue);

		RpcId(sender, MethodName.ClockReply, probeId, Tick, frameUsec);
		Stats.RecordSent(ReplyBytes);
	}

	/// <summary>Server -> the probing client.</summary>
	[Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false,
		TransferMode = MultiplayerPeer.TransferModeEnum.Unreliable)]
	private void ClockReply(uint probeId, uint serverTick, ushort serverFrameUsec)
	{
		Stats.RecordReceived(ReplyBytes);

		int slot = (int)(probeId % ProbeSlots);
		if (_probeIds[slot] != probeId)
		{
			// A reply to a probe that has already been answered or aged out. Timing it
			// would measure the wrong interval.
			return;
		}
		_probeIds[slot] = uint.MaxValue;

		float rtt = (float)((Time.GetTicksUsec() - _probeSentUsec[slot]) / 1_000_000.0);
		ServerFrameMilliseconds = serverFrameUsec / 1000f;
		Clock.OnClockReply(serverTick, rtt);
	}

	// ---- the map (docs/LAN.md §3.4) ------------------------------------------

	/// <summary>
	/// Server -> a client that has just connected: the map this round is played on.
	/// Reliable, and sent before <see cref="PeerJoined"/> is raised, so it is the
	/// first thing on the channel — ahead of the roster, which is about characters
	/// standing on it.
	///
	/// A scene map is its catalog id, with no hash and no length (docs/LAN.md §3.4). A data map
	/// is its hash and its file's length as well, which is what a client that lacks it fetches it
	/// by, and its id only when it is the catalog's; the name is what the menu shows while it
	/// arrives. The nonce is this connection's, 16 random bytes an upload's proof is made over
	/// (docs/LEVEL_EDITOR.md §9.3, §6.10).
	/// </summary>
	[Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false,
		TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
	private void ServerMap(string mapId, string hash, int length, string name, byte[] nonce)
	{
		if (!IsClient)
		{
			return;
		}

		Stats.RecordReceived(MapMessageBytes(mapId, hash, name));
		ServerMapId = mapId ?? string.Empty;
		ServerMapHash = hash ?? string.Empty;
		ServerMapLength = length;
		ServerMapName = name ?? string.Empty;
		ServerMapNonce = nonce;
		GD.Print(ServerMapHash.Length == 0
			? $"[net] the server is playing map '{ServerMapId}'"
			: $"[net] the server is playing map '{ServerMapName}'"
				+ (ServerMapId.Length > 0 ? $" ('{ServerMapId}')" : string.Empty)
				+ $", {ServerMapLength:N0} B, sha-256 {ServerMapHash}");
	}

	/// <summary>Bytes counted for the map message, for the bandwidth HUD: its strings, the length, the nonce, a header.</summary>
	private static int MapMessageBytes(string mapId, string hash, string name) =>
		8 + (mapId?.Length ?? 0) + (hash?.Length ?? 0) + 4 + System.Text.Encoding.UTF8.GetByteCount(name ?? string.Empty)
		+ Gdpyr.Sim.Level.MapTransfer.NonceBytes;

	/// <summary>Tells a peer that has just connected which map this is, with its connection's nonce.</summary>
	private void SendServerMap(int peerId)
	{
		var nonce = new byte[Gdpyr.Sim.Level.MapTransfer.NonceBytes];
		System.Security.Cryptography.RandomNumberGenerator.Fill(nonce);
		_mapNonces[peerId] = nonce;

		MapInfo map = Session.Map;
		string hash = map.IsData ? map.Hash ?? string.Empty : string.Empty;
		int length = map.IsData ? Session.Bytes?.Length ?? 0 : 0;
		string name = map.IsData && Session.Document != null ? Session.Document.Name : map.Name;
		RpcId(peerId, MethodName.ServerMap, map.Id ?? string.Empty, hash, length, name ?? string.Empty, nonce);
		Stats.RecordSent(MapMessageBytes(map.Id, hash, name));
	}

	// ---- peer lifecycle ----------------------------------------------------

	private void OnPeerConnected(long id)
	{
		GD.Print($"[net] peer {id} connected ({Multiplayer.GetPeers().Length} total)");

		SendServerMap((int)id);

		PeerJoined?.Invoke((int)id);
	}

	private void OnPeerDisconnected(long id)
	{
		_peerLagTicks.Remove((int)id);
		_mapNonces.Remove((int)id);
		MapTransfer?.ForgetPeer((int)id);
		GD.Print($"[net] peer {id} disconnected ({Multiplayer.GetPeers().Length} remaining)");
		PeerLeft?.Invoke((int)id);
	}

	private void OnConnectedToServer()
	{
		LocalPeerId = Multiplayer.GetUniqueId();
		Connected = true;
		// Probe immediately: nothing can be predicted until the clock is initialized.
		_probeTimer = SimConfig.ClockProbeIntervalSeconds;
		GD.Print($"[net] connected as peer {LocalPeerId}");
	}

	private void OnConnectionFailed()
	{
		Connected = false;
		ConnectionFailed = true;
		GD.PrintErr("[net] connection failed: no answer from the server. "
			+ "Check the address, and that UDP 7777 is open on the host.");
	}

	private void OnServerDisconnected()
	{
		Connected = false;
		ConnectionFailed = true;
		GD.PrintErr("[net] server disconnected");
	}

	/// <summary>
	/// One line every five seconds. A dedicated server has no HUD, and "is anything
	/// happening" has to be answerable from `journalctl` (docs/DEPLOYMENT.md §4).
	/// </summary>
	private void Heartbeat()
	{
		if (Tick == 0 || Tick % HeartbeatIntervalTicks != 0)
		{
			return;
		}

		if (IsServer)
		{
			GD.Print($"[net] tick {Tick} | peers {Multiplayer.GetPeers().Length}"
				+ $" | in {NetStats.FormatRate(Stats.BytesInPerSecond)}"
				+ $" | out {NetStats.FormatRate(Stats.BytesOutPerSecond)}");
		}
		else if (IsClient && Connected)
		{
			GD.Print($"[net] tick {Tick} | rtt {Clock.RttSeconds * 1000f:0} ms"
				+ $" | lead {Clock.InputLeadTicks} | buffer {Clock.InputBufferDepth}"
				+ $" | mispredictions/s {Stats.MispredictionsPerSecond:0.0}");
		}
	}
}
