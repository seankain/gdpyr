using Gdpyr.Core;
using Gdpyr.Match;
using Gdpyr.Sim;
using Godot;

namespace Gdpyr.Net;

/// <summary>
/// Answers "who is listening on this network?" on behalf of this server
/// (docs/LAN.md §4).
///
/// A second, tiny UDP socket beside the game's: it holds no state, keeps no
/// connection and answers one datagram with one datagram. It exists so that the
/// room does not have to pass an IP address around by hand, which is the thing
/// everybody actually does wrong.
///
/// It is not a matchmaker and it does not reach past the broadcast domain
/// (docs/IMPLEMENTATION_PLAN.md §5). A server across the internet is still typed in
/// by address, and <c>--no-advertise</c> turns this off for a host that would
/// rather not be found.
/// </summary>
public partial class ServerAdvertiser : Node
{
	/// <summary>The most a well-behaved client sends per refresh. Anything past this is noise.</summary>
	private const int MaxQueriesPerFrame = 32;

	private readonly PacketPeerUdp _socket = new();

	private string _name;
	private int _gamePort;

	/// <summary>The port this is answering on, or 0 when the range was full.</summary>
	public int Port { get; private set; }

	public bool IsListening => Port != 0;

	/// <summary>Queries answered since start-up.</summary>
	public int Answered { get; private set; }

	/// <summary>
	/// Opens the socket on the first free port in the discovery range. Returns null
	/// when every port in it is taken, which is a host that cannot be found rather
	/// than a host that cannot run: discovery is a convenience and never fatal, which
	/// is the difference between it and the game's own bind (docs/LAN.md §7).
	/// </summary>
	public static ServerAdvertiser TryStart(string name, int gamePort)
	{
		var advertiser = new ServerAdvertiser
		{
			Name = "ServerAdvertiser",
			_name = name,
			_gamePort = gamePort,
		};

		for (int offset = 0; offset < DiscoveryCodec.PortSpan; offset++)
		{
			int port = DiscoveryCodec.PortBase + offset;
			if (advertiser._socket.Bind(port, "*", DiscoveryCodec.MaxReplyLength * 8) != Error.Ok)
			{
				continue;
			}

			// Replies go back to the address the query came from, but a query that
			// arrived as a broadcast is answered from a socket that has to be allowed
			// to speak to one.
			advertiser._socket.SetBroadcastEnabled(true);
			advertiser.Port = port;
			return advertiser;
		}

		advertiser.QueueFree();
		return null;
	}

	public override void _Ready() =>
		// Discovery keeps answering while the host's own pause menu is up: a host
		// standing in a menu is still a server, and a room that cannot see it would
		// conclude it had crashed.
		ProcessMode = ProcessModeEnum.Always;

	public override void _ExitTree() => _socket.Close();

	/// <summary>
	/// Drains the queries that arrived this frame. On the idle frame rather than the
	/// physics tick: this answers a question about the process, not about the
	/// simulation, and it must keep answering while the map is still loading.
	/// </summary>
	public override void _Process(double delta)
	{
		for (int i = 0; i < MaxQueriesPerFrame && _socket.GetAvailablePacketCount() > 0; i++)
		{
			byte[] packet = _socket.GetPacket();
			string address = _socket.GetPacketIP();
			int port = _socket.GetPacketPort();

			if (!DiscoveryCodec.TryDecodeQuery(packet) || string.IsNullOrEmpty(address))
			{
				continue;
			}

			if (_socket.SetDestAddress(address, port) != Error.Ok)
			{
				continue;
			}

			_socket.PutPacket(DiscoveryCodec.EncodeReply(Describe()));

			if (Answered++ == 0)
			{
				// Once, and only the first: "somebody's menu can see me" is the single
				// most useful line when a room is trying to get a game started, and a
				// line per query would be a line every 2.5 s per machine.
				GD.Print($"[net] discovery: answered {address}, and will not say so again");
			}
		}
	}

	/// <summary>
	/// What this server looks like from outside, right now. Read off the same
	/// managers the HUD reads, so a browser row cannot claim a round the server is
	/// not running.
	/// </summary>
	private ServerInfo Describe()
	{
		CombatManager combat = CombatManager.Instance;
		NetworkManager net = NetworkManager.Instance;

		int players = combat?.PlayerCount ?? 0;
		int bots = 0;
		for (int i = 0; i < players; i++)
		{
			if (BotRoster.IsBot(combat.PlayerAt(i)?.PeerId ?? 0))
			{
				bots++;
			}
		}

		return new ServerInfo
		{
			Name = _name,
			GamePort = _gamePort,
			Players = players,
			Bots = bots,
			MaxPlayers = TransportFactory.MaxClients,
			Phase = combat?.Match.Phase ?? RoundPhase.Warmup,
			SecondsRemaining = Mathf.RoundToInt(combat?.Match.SecondsRemaining(net?.Tick ?? 0) ?? 0f),
		};
	}

	/// <summary>
	/// What to call a host that was not given a name: the machine's own, which is
	/// what everybody in the room already calls it. <c>--name</c> overrides it, and
	/// <c>--no-advertise</c> stops it being said at all.
	///
	/// Read from .NET rather than from <c>OS.GetEnvironment("HOSTNAME")</c>, which is
	/// a shell variable on Linux and is usually not in a process's environment at
	/// all.
	/// </summary>
	public static string DefaultName()
	{
		try
		{
			string machine = System.Environment.MachineName;
			if (!string.IsNullOrWhiteSpace(machine))
			{
				return machine.Trim();
			}
		}
		catch (System.InvalidOperationException)
		{
			// No hostname to be had. A row that says "gdpyr" and an address is still a
			// row somebody can join.
		}

		return "gdpyr";
	}
}
