using System;
using System.Threading.Tasks;
using Gdpyr.AgentClient;

namespace Gdpyr.Probe;

/// <summary>
/// The divergence probe (docs/AGENT_API.md §3, §10).
///
/// It runs the same seeded episode twice against one headless server and reports
/// the tick at which the two state hashes part company. That number is the point
/// of the exercise: <c>Scripts/Sim</c> is reproducible, <c>MoveAndSlide()</c> and
/// <c>NavigationAgent3D</c> are not, and how tightly a regression assertion may be
/// written depends on a measurement rather than on an assumption.
///
/// Its output is a number to be believed, not a pass or a fail. If the two runs
/// stay together for tens of thousands of ticks the engine is more stable than the
/// design assumes and assertions can tighten; if they part in the first second,
/// the distributional vocabulary in §3 is the contract.
/// </summary>
public static class Program
{
	public static async Task<int> Main(string[] args)
	{
		string host = "127.0.0.1";
		int port = 7900;
		string token = null;
		int ticks = 600;
		int seed = 7;

		for (int i = 0; i < args.Length; i++)
		{
			switch (args[i])
			{
				case "--host" when i + 1 < args.Length: host = args[++i]; break;
				case "--port" when i + 1 < args.Length: port = int.Parse(args[++i]); break;
				case "--token" when i + 1 < args.Length: token = args[++i]; break;
				case "--ticks" when i + 1 < args.Length: ticks = int.Parse(args[++i]); break;
				case "--seed" when i + 1 < args.Length: seed = int.Parse(args[++i]); break;
				case "--help":
				case "-h":
					Console.WriteLine(Usage);
					return 0;
				default:
					Console.Error.WriteLine($"gdpyr-probe: unrecognized argument '{args[i]}'");
					Console.Error.WriteLine(Usage);
					return 2;
			}
		}

		try
		{
			using GdpyrConnection connection = await GdpyrConnection.ConnectAsync(host, port, token)
				.ConfigureAwait(false);

			Console.WriteLine($"connected | schema {connection.Schema.Version}"
				+ $" | {connection.Schema.Floats} floats"
				+ (connection.Omniscient ? " | OMNISCIENT" : string.Empty)
				+ (connection.Unbounded ? " | UNBOUNDED" : string.Empty));

			await connection.ConfigureAsync(stepped: true).ConfigureAwait(false);

			// A seat is attached and held on a fixed script. It is not there to play:
			// an attached policy counts as somebody playing, so the backfill arrives
			// around it and the probe measures a round with characters walking, units
			// pathing and rounds in the air, rather than an empty map
			// (docs/AGENT_API.md §2).
			int seat = await connection.AttachGroundAsync(StepMul).ConfigureAwait(false);
			Console.WriteLine($"holding seat {seat} on a fixed script | {ticks} ticks per run");

			// Let the backfill settle before either run, so both start from the same
			// roster rather than the first one starting alone and the second against
			// six bots and a strategist.
			await connection.StepAsync(WarmUpTicks).ConfigureAwait(false);

			string[] first = await RunAsync(connection, seat, seed, ticks).ConfigureAwait(false);
			string[] second = await RunAsync(connection, seat, seed, ticks).ConfigureAwait(false);

			for (int tick = 0; tick < ticks; tick++)
			{
				if (first[tick] == second[tick])
				{
					continue;
				}

				Console.WriteLine($"diverged at tick {tick + 1} of {ticks}"
					+ $" ({(tick + 1) / (float)connection.Schema.TickRate:0.00} s)");
				Console.WriteLine($"  run 1: {first[tick]}");
				Console.WriteLine($"  run 2: {second[tick]}");

				if (tick == 0)
				{
					// Qualitatively different from drifting apart after a few thousand
					// ticks: the two episodes never agreed at all.
					Console.WriteLine("the two episodes never shared a state. reset starts a *fresh* round,");
					Console.WriteLine("not a repeatable one: every stochastic decision in the game is a hash");
					Console.WriteLine("of the absolute server tick (Spread.Seed), and the tick never rewinds.");
				}

				Console.WriteLine("assertions must be distributional past this tick (docs/AGENT_API.md §3)");
				return 0;
			}

			Console.WriteLine($"no divergence in {ticks} ticks"
				+ $" ({ticks / (float)connection.Schema.TickRate:0.00} s), seed {seed}");
			return 0;
		}
		catch (Exception error)
		{
			Console.Error.WriteLine($"gdpyr-probe: {error.Message}");
			return 2;
		}
	}

	/// <summary>Ticks the held action covers. The ground default (docs/AGENT_API.md §5.3).</summary>
	private const int StepMul = 4;

	/// <summary>
	/// Ticks run before the first measured episode. <c>BotDirector</c> reconciles the
	/// roster twice a second and adds one bot at a time, so seven seats take about
	/// four seconds to arrive.
	/// </summary>
	private const int WarmUpTicks = 420;

	/// <summary>
	/// One seeded episode, hashed every tick.
	///
	/// The seat walks forward and holds its fire the whole way. Deterministic on
	/// purpose: what is being measured is the game's own drift, and a policy
	/// choosing differently between the two runs would only add its own noise to
	/// it.
	/// </summary>
	private static async Task<string[]> RunAsync(GdpyrConnection connection, int seat, int seed, int ticks)
	{
		await connection.ResetAsync(seed).ConfigureAwait(false);

		var script = new AgentGroundAction { MoveZ = -1f, Buttons = AgentButtons.Fire };
		var hashes = new string[ticks];

		for (int tick = 0; tick < ticks; tick++)
		{
			if (tick % StepMul == 0)
			{
				connection.Act(seat, script, connection.Tick);
			}

			await connection.StepAsync(1).ConfigureAwait(false);
			hashes[tick] = await connection.StateHashAsync().ConfigureAwait(false);
		}

		return hashes;
	}

	private const string Usage =
		"usage: gdpyr-probe [--host 127.0.0.1] [--port 7900] [--token <token>]\n"
		+ "                   [--ticks 600] [--seed 7]\n"
		+ "Runs the same seeded episode twice against one headless gdpyr server and\n"
		+ "reports the tick at which their state hashes part company.\n"
		+ "Start the server with:\n"
		+ "  godot --path . --headless -- --server --agent-api 7900";
}
