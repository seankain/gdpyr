using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Gdpyr.AgentClient;

namespace Gdpyr.Playtest;

/// <summary>
/// <c>gdpyr-playtest</c>: run a scenario against a headless server and say
/// whether its claims held (docs/AGENT_API.md §9).
///
/// The whole CI contract is three things: a deterministic exit code, a
/// machine-readable summary on <c>--json</c>, and a trace file that replays.
/// That is what makes it usable from an agent loop — a coding agent with no Godot
/// knowledge adds a scenario file, runs it, and gets back a failed assertion with
/// the tick it failed on.
///
/// Exit codes: <c>0</c> every claim held · <c>1</c> a claim failed · <c>2</c> the
/// harness could not run the scenario.
/// </summary>
public static class Program
{
	private const int ExitPassed = 0;
	private const int ExitFailed = 1;
	private const int ExitHarness = 2;

	public static async Task<int> Main(string[] args)
	{
		var scenarios = new List<string>();
		string godot = Environment.GetEnvironmentVariable("GODOT") ?? "godot";
		string project = Directory.GetCurrentDirectory();
		string traceDirectory = Path.Combine("build", "playtest");
		string token = null;
		string host = "127.0.0.1";
		int port = 7900;
		int gamePort = 7777;
		bool attach = false;
		bool json = false;

		for (int i = 0; i < args.Length; i++)
		{
			switch (args[i])
			{
				case "--godot" when i + 1 < args.Length: godot = args[++i]; break;
				case "--project" when i + 1 < args.Length: project = args[++i]; break;
				case "--trace-dir" when i + 1 < args.Length: traceDirectory = args[++i]; break;
				case "--host" when i + 1 < args.Length: host = args[++i]; break;
				case "--port" when i + 1 < args.Length: port = int.Parse(args[++i]); break;
				case "--game-port" when i + 1 < args.Length: gamePort = int.Parse(args[++i]); break;
				case "--token" when i + 1 < args.Length: token = args[++i]; break;
				case "--attach": attach = true; break;
				case "--json": json = true; break;
				case "--help":
				case "-h":
					Console.WriteLine(Usage);
					return ExitPassed;
				default:
					if (args[i].StartsWith("-", StringComparison.Ordinal))
					{
						Console.Error.WriteLine($"gdpyr-playtest: unrecognized argument '{args[i]}'");
						Console.Error.WriteLine(Usage);
						return ExitHarness;
					}

					scenarios.Add(args[i]);
					break;
			}
		}

		if (scenarios.Count == 0)
		{
			Console.Error.WriteLine("gdpyr-playtest: name at least one scenario file");
			Console.Error.WriteLine(Usage);
			return ExitHarness;
		}

		var results = new List<ScenarioResult>();
		int exit = ExitPassed;

		foreach (string path in scenarios)
		{
			ScenarioResult result;
			try
			{
				Scenario scenario = Scenario.Load(path);
				result = await RunAsync(scenario, godot, project, host, port, gamePort, token, attach,
					traceDirectory).ConfigureAwait(false);
			}
			catch (ScenarioException error)
			{
				result = new ScenarioResult { Error = error.Message };
				result.Scenario = new Scenario { Name = Path.GetFileNameWithoutExtension(path) };
			}
			catch (Exception error)
			{
				result = new ScenarioResult { Error = error.Message };
				result.Scenario = new Scenario { Name = Path.GetFileNameWithoutExtension(path) };
			}

			results.Add(result);

			if (result.Error != null)
			{
				exit = ExitHarness;
			}
			else if (!result.Passed && exit == ExitPassed)
			{
				exit = ExitFailed;
			}

			if (!json)
			{
				Report(result, traceDirectory);
			}
		}

		if (json)
		{
			Console.WriteLine(Json(results, traceDirectory));
		}

		return exit;
	}

	private static async Task<ScenarioResult> RunAsync(Scenario scenario, string godot, string project,
		string host, int port, int gamePort, string token, bool attach, string traceDirectory)
	{
		ServerProcess server = null;
		try
		{
			if (!attach)
			{
				Directory.CreateDirectory(traceDirectory);
				server = await ServerProcess.StartAsync(godot, project, port, gamePort, scenario.GroundBots,
					scenario.StrategistBots,
					Path.Combine(traceDirectory, $"server-{port}.log")).ConfigureAwait(false);
			}

			using GdpyrConnection connection = await GdpyrConnection.ConnectAsync(host, port, token)
				.ConfigureAwait(false);

			var runner = new Runner(connection, traceDirectory);
			return await runner.RunAsync(scenario).ConfigureAwait(false);
		}
		finally
		{
			server?.Dispose();
		}
	}

	// ---- output ------------------------------------------------------------

	private static void Report(ScenarioResult result, string traceDirectory)
	{
		string name = result.Scenario?.Name ?? "scenario";
		string dots = new('.', Math.Max(2, 34 - name.Length));

		if (result.Error != null)
		{
			Console.WriteLine($"{name} {dots} ERROR");
			Console.WriteLine($"  {result.Error}");
			return;
		}

		double simSeconds = result.Ticks / 60d;
		Console.WriteLine($"{name} {dots} {(result.Passed ? "PASS" : "FAIL")}"
			+ $"  ({result.Ticks:N0} ticks, {simSeconds.ToString("0.0", CultureInfo.InvariantCulture)} s sim,"
			+ $" {result.WallSeconds.ToString("0.0", CultureInfo.InvariantCulture)} s wall,"
			+ $" {result.Episodes.Count} seed{(result.Episodes.Count == 1 ? string.Empty : "s")})");

		foreach (AssertionResult assertion in result.Assertions)
		{
			string claim = assertion.Assertion.ToString();
			string pad = new(' ', Math.Max(1, 44 - claim.Length));

			if (assertion.Error != null)
			{
				Console.WriteLine($"  {claim}{pad}{assertion.Error}");
				continue;
			}

			Console.WriteLine($"  {claim}{pad}{assertion.Observed}"
				+ (assertion.Passed ? "  ok" : $"  FAILED at tick {assertion.Tick} (seed {assertion.Seed})"));
		}

		if (!result.Passed)
		{
			Console.WriteLine($"  traces: {traceDirectory}");
		}
	}

	private static string Json(List<ScenarioResult> results, string traceDirectory)
	{
		var buffer = new ArrayBufferWriter<byte>(4096);
		using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
		{
			writer.WriteStartObject();
			writer.WriteString("trace_dir", traceDirectory);
			writer.WriteStartArray("scenarios");

			foreach (ScenarioResult result in results)
			{
				writer.WriteStartObject();
				writer.WriteString("name", result.Scenario?.Name ?? "scenario");
				writer.WriteBoolean("passed", result.Passed);
				writer.WriteNumber("ticks", result.Ticks);
				writer.WriteNumber("wall_seconds", Math.Round(result.WallSeconds, 3));

				if (result.Error != null)
				{
					writer.WriteString("error", result.Error);
				}

				writer.WriteStartArray("seeds");
				foreach (EpisodeMetrics episode in result.Episodes)
				{
					writer.WriteNumberValue(episode.Seed);
				}

				writer.WriteEndArray();

				writer.WriteStartArray("assertions");
				foreach (AssertionResult assertion in result.Assertions)
				{
					writer.WriteStartObject();
					writer.WriteString("claim", assertion.Assertion.ToString());
					writer.WriteString("metric", assertion.Assertion.Metric);
					writer.WriteString("op", Assertion.Symbol(assertion.Assertion.Op));
					writer.WriteBoolean("passed", assertion.Passed);
					writer.WriteString("observed", assertion.Observed);
					writer.WriteNumber("tick", assertion.Tick);
					writer.WriteNumber("seed", assertion.Seed);
					if (assertion.Error != null)
					{
						writer.WriteString("error", assertion.Error);
					}

					writer.WriteEndObject();
				}

				writer.WriteEndArray();
				writer.WriteEndObject();
			}

			writer.WriteEndArray();
			writer.WriteEndObject();
		}

		return Encoding.UTF8.GetString(buffer.WrittenSpan);
	}

	private const string Usage = """
		gdpyr-playtest — run a scenario against a headless gdpyr server (docs/AGENT_API.md §9)

		  gdpyr-playtest <scenario.json> [more.json ...] [options]

		  --godot <path>       Godot .NET binary to start the server with (default $GODOT, then 'godot')
		  --project <dir>      the gdpyr project directory (default: the working directory)
		  --attach             use a server that is already listening rather than starting one
		  --host <host>        agent channel host (default 127.0.0.1)
		  --port <port>        agent channel port (default 7900)
		  --game-port <port>   the server's own UDP port (default 7777)
		  --token <token>      agent token, when the server was started with one
		  --trace-dir <dir>    where traces and server logs go (default build/playtest)
		  --json               print a machine-readable summary instead of the human one

		Exit codes: 0 every claim held · 1 a claim failed · 2 the harness could not run it.
		""";
}
