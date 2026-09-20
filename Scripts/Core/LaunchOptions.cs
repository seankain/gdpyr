using System.Collections.Generic;

namespace Gdpyr.Core;

/// <summary>How this process takes part in a session.</summary>
public enum LaunchMode
{
	/// <summary>No networking. The editor default, and the fallback for a build launched with no flags.</summary>
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

	public const string Usage =
		"usage: gdpyr [--server [port]] | [--client <host[:port]>] | [--listen [port]]\n" +
		"  --server [port]         headless authority, no local player (default port 7777)\n" +
		"  --client <host[:port]>  connect to a server\n" +
		"  --listen [port]         authority plus a local player\n" +
		"  (no flags)              offline, no networking\n" +
		"Godot consumes its own arguments first, so these go after a bare `--`:\n" +
		"  gdpyr --headless -- --server 7777";

	/// <summary>An offline launch: what you get with no flags, and the value used after a parse failure.</summary>
	public static readonly LaunchOptions Offline = new();

	public LaunchMode Mode { get; private init; } = LaunchMode.Offline;

	/// <summary>Only meaningful for <see cref="LaunchMode.Client"/>.</summary>
	public string Host { get; private init; } = DefaultHost;

	public int Port { get; private init; } = DefaultPort;

	/// <summary>Null when parsing succeeded; a one-line diagnostic otherwise.</summary>
	public string Error { get; private init; }

	public bool IsServer => Mode is LaunchMode.Server or LaunchMode.Listen;

	public bool HasLocalPlayer => Mode is LaunchMode.Client or LaunchMode.Listen or LaunchMode.Offline;

	public override string ToString() => Mode switch
	{
		LaunchMode.Client => $"{Mode} -> {Host}:{Port}",
		LaunchMode.Server or LaunchMode.Listen => $"{Mode} on port {Port}",
		_ => Mode.ToString(),
	};

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

				default:
					return Failure($"unrecognized argument '{arg}'");
			}
		}

		mode ??= dedicatedServer ? LaunchMode.Server : LaunchMode.Offline;

		return new LaunchOptions { Mode = mode.Value, Host = host, Port = port };
	}

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
