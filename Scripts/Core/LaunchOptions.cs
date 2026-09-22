using System.Collections.Generic;

namespace Gdpyr.Core;

/// <summary>How this process takes part in a session.</summary>
public enum LaunchMode
{
	/// <summary>
	/// No networking. What a build launched with no flags parses as — which now means
	/// the main menu, where the player picks one of the other three or a round on
	/// their own (<see cref="Session"/>) — and the editor default.
	/// </summary>
	Offline,

	/// <summary>Authoritative simulation, no local player. What runs on the EC2 box.</summary>
	Server,

	/// <summary>Connects to a remote authority.</summary>
	Client,

	/// <summary>Authority plus a local player, for solo testing without a second machine.</summary>
	Listen,
}

/// <summary>
/// The parsed command line. Deliberately engine-free so it can be unit-tested
/// without Godot; <see cref="Bootstrap"/> is the only thing that reads the real
/// process arguments.
/// </summary>
public sealed class LaunchOptions
{
	public const int DefaultPort = 7777;
	public const string DefaultHost = "127.0.0.1";

	/// <summary>Where the agent channel binds unless told otherwise (docs/AGENT_API.md §4.1).</summary>
	public const string AgentLoopbackHost = "127.0.0.1";

	/// <summary>The port the design names, and what a bare <c>--agent-api</c> would use if it took no value.</summary>
	public const int DefaultAgentPort = 7900;

	public const string Usage =
		"usage: gdpyr [--server [port]] | [--client <host[:port]>] | [--listen [port]]\n" +
		"             [--bots <ground>[:<strategists>]] | [--no-bots]\n" +
		"             [--name <text>] [--no-advertise]\n" +
		"             [--agent-api [host:]port] [--agent-token <token>]\n" +
		"             [--agent-unbounded] [--agent-omniscient]\n" +
		"  --server [port]         headless authority, no local player (default port 7777)\n" +
		"  --client <host[:port]>  connect to a server\n" +
		"  --listen [port]         authority plus a local player\n" +
		"  --bots <n>[:<m>]        fill each side to n ground and m strategists with bots\n" +
		"  --no-bots               no computer players, whatever the game mode says\n" +
		"  --name <text>           what this server calls itself in the browser (default: the hostname)\n" +
		"  --no-advertise          do not answer LAN discovery; the address still works\n" +
		"  --agent-api [host:]port listen for external policies (docs/AGENT_API.md); loopback by default\n" +
		"  --agent-token <token>   required for a non-loopback agent bind\n" +
		"  --agent-unbounded       lift the turn-rate and APM ceilings; research runs only\n" +
		"  --agent-omniscient      drop the fog for attached seats; labelled as cheating\n" +
		"  (no flags)              the main menu: the servers on this network, or a round on your own\n" +
		"Godot consumes its own arguments first, so these go after a bare `--`:\n" +
		"  gdpyr --headless -- --server 7777 --bots 6:1 --agent-api 7900";

	/// <summary>An offline launch: what you get with no flags, and the value used after a parse failure.</summary>
	public static readonly LaunchOptions Offline = new();

	public LaunchMode Mode { get; private init; } = LaunchMode.Offline;

	/// <summary>Only meaningful for <see cref="LaunchMode.Client"/>.</summary>
	public string Host { get; private init; } = DefaultHost;

	public int Port { get; private init; } = DefaultPort;

	/// <summary>
	/// Ground-force players to keep filled with computer players, humans included
	/// (docs/IMPLEMENTATION_PLAN.md §M3.5). Null means "whatever the game mode
	/// says", which is the difference between a flag that was not given and a flag
	/// that was given as zero.
	/// </summary>
	public int? GroundBots { get; private init; }

	/// <summary>Strategists to keep filled. See <see cref="GroundBots"/>.</summary>
	public int? StrategistBots { get; private init; }

	/// <summary>
	/// What this server calls itself in another machine's server browser
	/// (docs/LAN.md §4). Null means "the machine's own name", which is resolved where
	/// the environment is — <c>ServerAdvertiser.DefaultName</c> — rather than here,
	/// so that this class stays engine-free and a parse stays a pure function.
	/// </summary>
	public string ServerName { get; private init; }

	/// <summary>
	/// Whether this process answers LAN discovery queries while it is a server.
	/// On by default: a host nobody can find is the problem the browser exists to
	/// solve. <c>--no-advertise</c> turns it off without changing anything else —
	/// the address still works, because discovery is a convenience and not the
	/// transport.
	///
	/// It is not restricted to a server mode: a process launched with no flags picks
	/// its mode in the main menu, and may end up hosting from there.
	/// </summary>
	public bool Advertise { get; private init; } = true;

	/// <summary>
	/// Port the agent control channel listens on, or null when it was not asked for
	/// (docs/AGENT_API.md §4). The socket can spawn players, issue orders and reset
	/// rounds, so it is off unless it is named and the deploy unit never names it.
	/// </summary>
	public int? AgentPort { get; private init; }

	/// <summary>Address the agent channel binds. Loopback unless a bare <c>--agent-api</c> was given an address.</summary>
	public string AgentHost { get; private init; } = AgentLoopbackHost;

	/// <summary>Shared secret the first frame of an agent session must carry. Null for an unauthenticated loopback bind.</summary>
	public string AgentToken { get; private init; }

	/// <summary>Lifts the turn-rate and APM ceilings an attached seat plays under (docs/AGENT_API.md §7.3).</summary>
	public bool AgentUnbounded { get; private init; }

	/// <summary>Drops the fog for attached seats. Stamped into every observation and every trace.</summary>
	public bool AgentOmniscient { get; private init; }

	public bool HasAgentApi => AgentPort.HasValue;

	/// <summary>Null when parsing succeeded; a one-line diagnostic otherwise.</summary>
	public string Error { get; private init; }

	public bool IsServer => Mode is LaunchMode.Server or LaunchMode.Listen;

	public bool HasLocalPlayer => Mode is LaunchMode.Client or LaunchMode.Listen or LaunchMode.Offline;

	public override string ToString()
	{
		string mode = Mode switch
		{
			LaunchMode.Client => $"{Mode} -> {Host}:{Port}",
			LaunchMode.Server or LaunchMode.Listen => $"{Mode} on port {Port}",
			_ => Mode.ToString(),
		};

		if (GroundBots is not null || StrategistBots is not null)
		{
			mode += $" | bots {Describe(GroundBots)} ground, {Describe(StrategistBots)} strategist";
		}

		if (ServerName != null)
		{
			mode += $" | name '{ServerName}'";
		}

		if (!Advertise)
		{
			mode += " | not advertised";
		}

		if (AgentPort is { } agentPort)
		{
			mode += $" | agent api {AgentHost}:{agentPort}"
				+ (AgentToken != null ? " (token)" : string.Empty)
				+ (AgentUnbounded ? " unbounded" : string.Empty)
				+ (AgentOmniscient ? " omniscient" : string.Empty);
		}

		return mode;
	}

	private static string Describe(int? count) => count?.ToString() ?? "default";

	/// <summary>
	/// Parses the user portion of the command line — what Godot hands back from
	/// <c>OS.GetCmdlineUserArgs()</c>, i.e. everything after a bare <c>--</c>.
	/// Never throws: a bad command line comes back as an options object with
	/// <see cref="Error"/> set.
	/// </summary>
	/// <param name="args">Arguments, without the executable name.</param>
	/// <param name="dedicatedServer">
	/// True when running a dedicated-server export. Such a build with no mode flag
	/// defaults to <see cref="LaunchMode.Server"/> rather than offline, so a missing
	/// systemd argument cannot silently produce a server that never listens.
	/// </param>
	public static LaunchOptions Parse(IReadOnlyList<string> args, bool dedicatedServer = false)
	{
		LaunchMode? mode = null;
		string host = DefaultHost;
		int port = DefaultPort;
		int? groundBots = null;
		int? strategistBots = null;
		bool botsRefused = false;
		string serverName = null;
		bool advertise = true;
		int? agentPort = null;
		string agentHost = AgentLoopbackHost;
		string agentToken = null;
		bool agentUnbounded = false;
		bool agentOmniscient = false;

		args ??= System.Array.Empty<string>();

		for (int i = 0; i < args.Count; i++)
		{
			string arg = args[i];
			if (string.IsNullOrWhiteSpace(arg))
			{
				continue;
			}

			switch (arg)
			{
				case "--server":
				case "--listen":
				{
					var requested = arg == "--server" ? LaunchMode.Server : LaunchMode.Listen;
					if (mode is { } already)
					{
						return Failure($"conflicting mode flags: {already} and {requested}");
					}
					mode = requested;

					// The port is optional: `--server` alone means the default port.
					if (TryTakeValue(args, ref i, out string value))
					{
						if (!TryParsePort(value, out port))
						{
							return Failure($"{arg}: '{value}' is not a port in 1-65535");
						}
					}
					break;
				}

				case "--client":
				{
					if (mode is { } conflicting)
					{
						return Failure($"conflicting mode flags: {conflicting} and {LaunchMode.Client}");
					}
					mode = LaunchMode.Client;

					if (!TryTakeValue(args, ref i, out string value))
					{
						return Failure("--client needs an address, e.g. --client 203.0.113.10:7777");
					}
					if (!TryParseEndpoint(value, out host, out port, out string error))
					{
						return Failure($"--client: {error}");
					}
					break;
				}

				case "--bots":
				{
					if (botsRefused)
					{
						return Failure("conflicting bot flags: --no-bots and --bots");
					}

					if (!TryTakeValue(args, ref i, out string value))
					{
						return Failure("--bots needs a count, e.g. --bots 6:1");
					}

					// Only what was named is overridden: `--bots 6` leaves the number of
					// strategists to the game mode rather than silently zeroing it.
					if (!TryParseBots(value, ref groundBots, ref strategistBots, out string error))
					{
						return Failure($"--bots: {error}");
					}
					break;
				}

				case "--name":
				{
					if (serverName != null)
					{
						return Failure("--name given twice");
					}

					if (!TryTakeValue(args, ref i, out string value) || string.IsNullOrWhiteSpace(value))
					{
						return Failure("--name needs a name, e.g. --name \"the kitchen box\"");
					}

					serverName = value.Trim();
					break;
				}

				case "--no-advertise":
					advertise = false;
					break;

				case "--agent-api":
				{
					if (agentPort.HasValue)
					{
						return Failure("--agent-api given twice");
					}

					if (!TryTakeValue(args, ref i, out string value))
					{
						return Failure($"--agent-api needs a port, e.g. --agent-api {DefaultAgentPort}");
					}

					if (!TryParseAgentEndpoint(value, out agentHost, out int parsed, out string error))
					{
						return Failure($"--agent-api: {error}");
					}
					agentPort = parsed;
					break;
				}

				case "--agent-token":
				{
					if (!TryTakeValue(args, ref i, out string value) || string.IsNullOrWhiteSpace(value))
					{
						return Failure("--agent-token needs a token");
					}
					agentToken = value;
					break;
				}

				case "--agent-unbounded":
					agentUnbounded = true;
					break;

				case "--agent-omniscient":
					agentOmniscient = true;
					break;

				case "--no-bots":
				{
					if (groundBots.HasValue || strategistBots.HasValue)
					{
						return Failure("conflicting bot flags: --bots and --no-bots");
					}

					botsRefused = true;
					groundBots = 0;
					strategistBots = 0;
					break;
				}

				default:
					return Failure($"unrecognized argument '{arg}'");
			}
		}

		mode ??= dedicatedServer ? LaunchMode.Server : LaunchMode.Offline;

		if (agentPort.HasValue)
		{
			// The agent socket takes a seat in the round, so there has to be a round on
			// this side of the wire to take a seat in.
			if (mode is not (LaunchMode.Server or LaunchMode.Listen))
			{
				return Failure("--agent-api needs --server or --listen: it drives the authority, not a client");
			}

			// A non-loopback bind without a token is fatal, the same way a server that
			// cannot bind its UDP port is fatal. The socket can spawn players, issue
			// orders and reset rounds, and the box in docs/DEPLOYMENT.md has a public
			// address (docs/AGENT_API.md §4.1).
			if (!IsLoopback(agentHost) && string.IsNullOrWhiteSpace(agentToken))
			{
				return Failure($"--agent-api {agentHost}:{agentPort} is not loopback and has no --agent-token;"
					+ " refusing to expose the agent channel");
			}
		}
		else if (agentToken != null || agentUnbounded || agentOmniscient)
		{
			return Failure("--agent-token, --agent-unbounded and --agent-omniscient need --agent-api");
		}

		return new LaunchOptions
		{
			Mode = mode.Value,
			Host = host,
			Port = port,
			GroundBots = groundBots,
			StrategistBots = strategistBots,
			ServerName = serverName,
			Advertise = advertise,
			AgentPort = agentPort,
			AgentHost = agentHost,
			AgentToken = agentToken,
			AgentUnbounded = agentUnbounded,
			AgentOmniscient = agentOmniscient,
		};
	}

	/// <summary>
	/// Whether an address is one only this machine can reach. The test is textual
	/// rather than a DNS lookup on purpose: a start-up refusal must not depend on a
	/// resolver, and anything it cannot recognise is treated as public, which is the
	/// safe direction to be wrong in.
	/// </summary>
	public static bool IsLoopback(string host)
	{
		if (string.IsNullOrWhiteSpace(host))
		{
			return false;
		}

		host = host.Trim();
		return host is "localhost" or "::1" or "[::1]" or "0:0:0:0:0:0:0:1"
			|| host.StartsWith("127.", System.StringComparison.Ordinal);
	}

	/// <summary>
	/// Parses the agent channel's <c>port</c> or <c>host:port</c>. A bare number is
	/// a port on loopback, which is what makes <c>--agent-api 7900</c> the safe
	/// spelling and the one every example uses.
	/// </summary>
	private static bool TryParseAgentEndpoint(string text, out string host, out int port, out string error)
	{
		host = AgentLoopbackHost;
		port = DefaultAgentPort;
		error = null;

		text = text?.Trim();
		if (string.IsNullOrEmpty(text))
		{
			error = "empty address";
			return false;
		}

		if (TryParsePort(text, out port))
		{
			return true;
		}

		if (!TryParseEndpoint(text, out host, out port, out error))
		{
			return false;
		}

		// TryParseEndpoint defaults a missing port to the *game's* port, which would
		// put the agent channel on top of the UDP listener's number by accident.
		if (text.IndexOf(':') < 0 || text.EndsWith("]", System.StringComparison.Ordinal))
		{
			error = $"'{text}' has no port, e.g. --agent-api {host}:{DefaultAgentPort}";
			return false;
		}

		return true;
	}

	/// <summary>
	/// Parses an address a person typed — the main menu's box — with exactly the
	/// rules <c>--client</c> uses. Two spellings of "where is the server" that
	/// disagreed about IPv6 or about a missing port would be one spelling too many,
	/// so this is the same parser and not a second one.
	/// </summary>
	public static bool TryParseAddress(string text, out string host, out int port, out string error) =>
		TryParseEndpoint(text, out host, out port, out error);

	private static LaunchOptions Failure(string error) => new() { Error = error };

	/// <summary>
	/// Consumes the next argument as this flag's value when there is one and it is
	/// not itself a flag. Advances <paramref name="i"/> only when it consumes.
	/// </summary>
	private static bool TryTakeValue(IReadOnlyList<string> args, ref int i, out string value)
	{
		if (i + 1 < args.Count && !args[i + 1].StartsWith("--"))
		{
			value = args[++i];
			return true;
		}
		value = null;
		return false;
	}

	private static bool TryParsePort(string text, out int port) =>
		int.TryParse(text, out port) && port is > 0 and <= 65535;

	/// <summary>
	/// Parses <c>n</c> or <c>n:m</c> — how many players each side should be filled
	/// to, humans included. The ceiling is the bot id band, which is the roster's
	/// ceiling too (<see cref="Gdpyr.Sim.BotRoster.MaxBots"/>); the director clamps
	/// the strategists again against the real cap, because that is a rule of the
	/// game rather than of the command line.
	/// </summary>
	private static bool TryParseBots(string text, ref int? ground, ref int? strategists, out string error)
	{
		error = null;
		text = text.Trim();

		int colon = text.IndexOf(':');
		string first = colon < 0 ? text : text[..colon];
		string second = colon < 0 ? null : text[(colon + 1)..];

		if (second != null && second.IndexOf(':') >= 0)
		{
			error = $"'{text}' is not <ground> or <ground>:<strategists>";
			return false;
		}

		if (!TryParseCount(first, out int groundCount, out error))
		{
			return false;
		}
		ground = groundCount;

		if (second == null)
		{
			return true;
		}

		if (!TryParseCount(second, out int strategistCount, out error))
		{
			return false;
		}
		strategists = strategistCount;
		return true;
	}

	private static bool TryParseCount(string text, out int count, out string error)
	{
		error = null;
		if (int.TryParse(text, out count) && count >= 0 && count <= Gdpyr.Sim.BotRoster.MaxBots)
		{
			return true;
		}

		error = $"'{text}' is not a count in 0-{Gdpyr.Sim.BotRoster.MaxBots}";
		return false;
	}

	/// <summary>
	/// Parses <c>host</c>, <c>host:port</c>, <c>[v6]</c> or <c>[v6]:port</c>. A bare
	/// IPv6 literal (more than one colon, no brackets) is treated as a host with the
	/// default port, since there is no unambiguous port to split off.
	/// </summary>
	private static bool TryParseEndpoint(string text, out string host, out int port, out string error)
	{
		host = DefaultHost;
		port = DefaultPort;
		error = null;

		if (string.IsNullOrWhiteSpace(text))
		{
			error = "empty address";
			return false;
		}

		text = text.Trim();

		if (text.StartsWith('['))
		{
			int close = text.IndexOf(']');
			if (close < 0)
			{
				error = $"'{text}' is missing a closing ']'";
				return false;
			}

			host = text[1..close];
			string rest = text[(close + 1)..];
			if (rest.Length == 0)
			{
				return HostNotEmpty(host, ref error);
			}
			if (rest[0] != ':')
			{
				error = $"'{text}' has trailing characters after ']'";
				return false;
			}
			if (!TryParsePort(rest[1..], out port))
			{
				error = $"'{rest[1..]}' is not a port in 1-65535";
				return false;
			}
			return HostNotEmpty(host, ref error);
		}

		int lastColon = text.LastIndexOf(':');
		bool looksLikeBareIpv6 = text.IndexOf(':') != lastColon;

		if (lastColon < 0 || looksLikeBareIpv6)
		{
			host = text;
			return HostNotEmpty(host, ref error);
		}

		host = text[..lastColon];
		if (!TryParsePort(text[(lastColon + 1)..], out port))
		{
			error = $"'{text[(lastColon + 1)..]}' is not a port in 1-65535";
			return false;
		}
		return HostNotEmpty(host, ref error);
	}

	private static bool HostNotEmpty(string host, ref string error)
	{
		if (string.IsNullOrWhiteSpace(host))
		{
			error = "empty host";
			return false;
		}
		return true;
	}
}
