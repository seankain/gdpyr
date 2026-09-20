using Gdpyr.Core;
using Gdpyr.Sim;
using Godot;

namespace Gdpyr.Net;

/// <summary>
/// Second autoload. Brings the peer up in the mode <see cref="Bootstrap"/> parsed
/// and runs the fixed-tick counter both ends agree on.
///
/// M0 scope: connectivity and a shared clock you can eyeball in the logs. The tick
/// beacon below is a placeholder for the real <c>SimClock</c> — RTT estimation, a
/// jitter buffer and the two-clock arrangement land in M1 (docs/NETCODE.md §2).
/// </summary>
public partial class NetworkManager : Node
{
	/// <summary>Ticks since this process started simulating.</summary>
	public uint Tick { get; private set; }

	/// <summary>
	/// The most recent tick the server reported, or 0 before the first beacon.
	/// On a server this tracks <see cref="Tick"/>.
	/// </summary>
	public uint ServerTick { get; private set; }

	/// <summary>Local tick minus server tick. Meaningless until the first beacon arrives.</summary>
	public long TickDrift => (long)Tick - ServerTick;

	public LaunchMode Mode { get; private set; } = LaunchMode.Offline;

	/// <summary>One beacon per second: enough to prove the clocks agree, cheap enough to leave on.</summary>
	private const int BeaconIntervalTicks = SimConfig.TickRate;

	public override void _Ready()
	{
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

	public override void _PhysicsProcess(double delta)
	{
		Tick++;

		if (Tick % BeaconIntervalTicks != 0)
		{
			return;
		}

		// Branch on the launch mode rather than on Multiplayer.IsServer(): an offline
		// process carries Godot's OfflineMultiplayerPeer, for which IsServer() is true.
		switch (Mode)
		{
			case LaunchMode.Server:
			case LaunchMode.Listen:
			{
				ServerTick = Tick;
				int peers = Multiplayer.GetPeers().Length;
				GD.Print($"[net] server tick {Tick} | peers {peers}");
				if (peers > 0)
				{
					Rpc(MethodName.ServerTickBeacon, Tick);
				}
				break;
			}

			case LaunchMode.Client:
			{
				if (Multiplayer.MultiplayerPeer?.GetConnectionStatus()
					== MultiplayerPeer.ConnectionStatus.Connected)
				{
					GD.Print($"[net] client tick {Tick} | server tick {ServerTick} | drift {TickDrift}");
				}
				break;
			}
		}
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

	/// <summary>
	/// Server -> clients, once a second, unreliable: a dropped beacon costs nothing
	/// because the next one carries the same information.
	/// </summary>
	[Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false,
		TransferMode = MultiplayerPeer.TransferModeEnum.Unreliable)]
	private void ServerTickBeacon(uint serverTick)
	{
		ServerTick = serverTick;
	}

	private void OnPeerConnected(long id) =>
		GD.Print($"[net] peer {id} connected ({Multiplayer.GetPeers().Length} total)");

	private void OnPeerDisconnected(long id) =>
		GD.Print($"[net] peer {id} disconnected ({Multiplayer.GetPeers().Length} remaining)");

	private void OnConnectedToServer() =>
		GD.Print($"[net] connected as peer {Multiplayer.GetUniqueId()}");

	private void OnConnectionFailed() =>
		GD.PrintErr("[net] connection failed: no answer from the server. "
			+ "Check the address, and that UDP 7777 is open on the host.");

	private void OnServerDisconnected() =>
		GD.PrintErr("[net] server disconnected");
}
