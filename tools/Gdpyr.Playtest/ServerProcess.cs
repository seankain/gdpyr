using System;
using System.Diagnostics;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Gdpyr.Playtest;

/// <summary>
/// A headless gdpyr server started for one scenario, and taken down again after
/// it (docs/AGENT_API.md §9).
///
/// The harness starts the server rather than the shell script doing it, because
/// the roster a scenario wants is in the scenario file: <c>--bots 6:1</c> is a
/// launch option, and a wrapper that had to read the JSON to build the command
/// line would be a JSON parser written in bash.
///
/// Everything binds loopback. The agent socket can spawn players and reset
/// rounds, and <c>deploy/gdpyr-server.service</c> never passes <c>--agent-api</c>
/// (§4.1).
/// </summary>
public sealed class ServerProcess : IDisposable
{
	private readonly Process _process;
	private readonly System.IO.StreamWriter _log;

	private ServerProcess(Process process, System.IO.StreamWriter log)
	{
		_process = process;
		_log = log;
	}

	/// <summary>Where the server's stdout and stderr went, for a harness error to point at.</summary>
	public string LogPath { get; private init; } = string.Empty;

	public static async Task<ServerProcess> StartAsync(string godot, string projectDirectory, int agentPort,
		int gamePort, int groundBots, int strategistBots, string logPath, CancellationToken cancel = default)
	{
		var start = new ProcessStartInfo(godot)
		{
			UseShellExecute = false,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			WorkingDirectory = projectDirectory,
		};

		start.ArgumentList.Add("--headless");
		start.ArgumentList.Add("--path");
		start.ArgumentList.Add(projectDirectory);
		start.ArgumentList.Add("--");
		start.ArgumentList.Add("--server");
		start.ArgumentList.Add(gamePort.ToString());
		start.ArgumentList.Add("--agent-api");
		start.ArgumentList.Add($"127.0.0.1:{agentPort}");
		start.ArgumentList.Add("--bots");
		start.ArgumentList.Add($"{groundBots}:{strategistBots}");

		Process process;
		try
		{
			process = Process.Start(start);
		}
		catch (Exception error)
		{
			throw new ScenarioException($"cannot start '{godot}': {error.Message}."
				+ " Set GODOT to a Godot .NET binary, or pass --attach to use a server you started");
		}

		if (process == null)
		{
			throw new ScenarioException($"cannot start '{godot}'");
		}

		var log = new System.IO.StreamWriter(logPath, append: false);
		process.OutputDataReceived += (_, line) => Write(log, line.Data);
		process.ErrorDataReceived += (_, line) => Write(log, line.Data);
		process.BeginOutputReadLine();
		process.BeginErrorReadLine();

		var server = new ServerProcess(process, log) { LogPath = logPath };
		await server.WaitForPortAsync(agentPort, cancel).ConfigureAwait(false);
		return server;
	}

	private static void Write(System.IO.StreamWriter log, string line)
	{
		if (line == null)
		{
			return;
		}

		lock (log)
		{
			try
			{
				log.WriteLine(line);
				log.Flush();
			}
			catch (Exception)
			{
				// The server is being taken down and the log is already closed. A line
				// of Godot's stdout is not worth failing a scenario over.
			}
		}
	}

	/// <summary>
	/// Polls the agent port until it accepts a connection. This is the one place the
	/// harness waits on a clock, and it is waiting for a process to exist rather than
	/// for the game to do anything — everything past here is stepped (§9.2).
	/// </summary>
	private async Task WaitForPortAsync(int port, CancellationToken cancel)
	{
		for (int attempt = 0; attempt < 150; attempt++)
		{
			if (_process.HasExited)
			{
				throw new ScenarioException(
					$"the server exited with code {_process.ExitCode} before opening the agent port;"
					+ $" see {LogPath}");
			}

			try
			{
				using var probe = new TcpClient();
				await probe.ConnectAsync("127.0.0.1", port, cancel).ConfigureAwait(false);
				return;
			}
			catch (Exception)
			{
				await Task.Delay(200, cancel).ConfigureAwait(false);
			}
		}

		throw new ScenarioException($"the server never opened the agent port on {port}; see {LogPath}");
	}

	public void Dispose()
	{
		try
		{
			if (!_process.HasExited)
			{
				_process.Kill(entireProcessTree: true);
				_process.WaitForExit(5000);
			}
		}
		catch (Exception)
		{
			// It is already gone, which is the state we wanted it in.
		}

		_process.Dispose();

		lock (_log)
		{
			_log.Dispose();
		}
	}
}
