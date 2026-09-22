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
	/// Drops a link that never came up, so the menu can try a different address.
	///
	/// Only ever called before a round starts: once the map is loaded there is a
	/// roster, a clock and a prediction ledger built around this peer, and none of
	/// them is torn down here. Leaving a round you are in is still quitting the
	/// process (docs/LAN.md §4).
	/// </summary>
	public void AbandonConnection()
	{
		if (!IsClient || Connected)
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

	// ---- peer lifecycle ----------------------------------------------------

	private void OnPeerConnected(long id)
	{
		GD.Print($"[net] peer {id} connected ({Multiplayer.GetPeers().Length} total)");
		PeerJoined?.Invoke((int)id);
	}

	private void OnPeerDisconnected(long id)
	{
		_peerLagTicks.Remove((int)id);
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
