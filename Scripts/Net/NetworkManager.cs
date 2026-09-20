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
	private const int ProbeBytes = 4;
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
		Mode = options.Mode;

		switch (options.Mode)
		{
			case LaunchMode.Server:
			case LaunchMode.Listen:
				StartServer(options.Port);
				break;

			case LaunchMode.Client:
				StartClient(options.Host, options.Port);
				break;

			default:
				GD.Print("[net] offline: no peer");
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

		RpcId(1, MethodName.ClockProbe, id);
		Stats.RecordSent(ProbeBytes);
	}

	/// <summary>
	/// Client -> server. Unreliable: a lost probe costs half a second, and a
	/// retransmitted one would measure the retransmission rather than the link.
	/// </summary>
	[Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false,
		TransferMode = MultiplayerPeer.TransferModeEnum.Unreliable)]
	private void ClockProbe(uint probeId)
	{
		if (!IsServer)
		{
			return;
		}

		Stats.RecordReceived(ProbeBytes);
		int sender = Multiplayer.GetRemoteSenderId();

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
		GD.PrintErr("[net] connection failed: no answer from the server. "
			+ "Check the address, and that UDP 7777 is open on the host.");
	}

	private void OnServerDisconnected()
	{
		Connected = false;
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
