using System;
using System.Collections.Generic;

namespace Gdpyr.Trainer;

/// <summary>
/// The trainer's command line. Engine-free, total, and small enough to read in
/// one screen, which is the standard the rest of this repository's option parsing
/// is held to (<c>Scripts/Core/LaunchOptions.cs</c>).
/// </summary>
public sealed class TrainerOptions
{
	public const string Usage =
		"usage: gdpyr-train [--port 7900[,7901,...]] [--host 127.0.0.1] [--token <token>]\n"
		+ "                   [--policy ground|strategist] [--algo ppo|dqn] [--steps 100000]\n"
		+ "                   [--step-mul N] [--episode-steps N] [--history 1] [--seed 0]\n"
		+ "                   [--lr 1e-4] [--width 512] [--save <dir>] [--load <dir>]\n"
		+ "                   [--play] [--episodes N] [--realtime] [--step-timeout 10000]\n"
		+ "                   [--report-every 100] [--save-every 5000] [--metrics <file>]\n"
		+ "                   [--no-reset]\n"
		+ "  --port          one or more agent-api ports; one gdpyr server per port\n"
		+ "  --policy        which seat to sit in: ground (default) or strategist\n"
		+ "  --algo          ppo (default) or dqn; see docs/RL_ARCHITECTURE.md §3\n"
		+ "  --steps         rollout steps to run\n"
		+ "  --step-mul      ticks one decision is held for; 4 on the ground, 30 in the chair\n"
		+ "  --episode-steps decisions before the round is restarted\n"
		+ "  --history       observations stacked into one state; 1 is no stacking\n"
		+ "  --save          directory to write checkpoints into; RLMatrix names the files\n"
		+ "  --load          directory to read a checkpoint back from\n"
		+ "  --play          run the loaded policy without learning from it\n"
		+ "  --episodes      stop after this many finished episodes, whatever --steps says\n"
		+ "  --realtime      do not engage stepped mode; for watching, not for training\n"
		+ "  --step-timeout  ms the server holds the sim for this learner before giving up\n"
		+ "  --metrics       append a CSV row per report window to this file\n"
		+ "  --no-reset      never restart the round; follow the learner that does (§8)\n"
		+ "Start a server for each port with:\n"
		+ "  godot --path . --headless -- --server 7777 --agent-api 7900 --bots 6:1\n"
		+ "See docs/TRAINING.md and docs/RL_ARCHITECTURE.md.";

	/// <summary>Ticks one decision is held for, per seat, when the command line does not say.</summary>
	private const int GroundStepMul = 4;

	private const int StrategistStepMul = 30;

	/// <summary>
	/// Decisions an episode is truncated at, per seat, when the command line does
	/// not say. A ground seat at 15 Hz gets two minutes of round, which is a
	/// credit-assignment horizon a fresh policy can actually climb; a strategist at
	/// 2 Hz gets the whole twenty, because its outcome terms *are* the win
	/// condition and an episode that always truncates never sees one.
	/// </summary>
	private const int GroundEpisodeSteps = 1_800;

	private const int StrategistEpisodeSteps = 2_400;

	public string Host { get; private set; } = "127.0.0.1";

	public List<int> Ports { get; } = new();

	public string Token { get; private set; }

	/// <summary>Which seat this run sits in: <c>ground</c> or <c>strategist</c>.</summary>
	public string Policy { get; private set; } = "ground";

	public bool Strategist => Policy == "strategist";

	public string Algorithm { get; private set; } = "ppo";

	public int Steps { get; private set; } = 100_000;

	/// <summary>Ticks one decision is held for. Resolved from <see cref="Policy"/> when unset.</summary>
	public int StepMul { get; private set; }

	/// <summary>Decisions before truncation. Resolved from <see cref="Policy"/> when unset.</summary>
	public int MaxEpisodeSteps { get; private set; }

	/// <summary>
	/// Observations concatenated into one state. Both seats are partially observed
	/// and a feed-forward policy has no memory of its own
	/// (docs/RL_ARCHITECTURE.md §4); 1 keeps the state exactly what the server sent.
	/// </summary>
	public int History { get; private set; } = 1;

	public int Seed { get; private set; }

	public float LearningRate { get; private set; } = 1e-4f;

	public int Width { get; private set; } = 512;

	/// <summary>
	/// Directory checkpoints are written into. RLMatrix writes one file per network
	/// and numbers them, so this is a folder and not a file name.
	/// </summary>
	public string SavePath { get; private set; }

	/// <summary>Directory a checkpoint is read back from. See <see cref="SavePath"/>.</summary>
	public string LoadPath { get; private set; }

	public bool PlayOnly { get; private set; }

	/// <summary>Finished episodes to stop after, or 0 for "however many fit in --steps".</summary>
	public int Episodes { get; private set; }

	/// <summary>
	/// Real time rather than stepped. Useful for watching a trained policy; wrong
	/// for learning, because the sim runs at 60 Hz whatever the optimizer is doing
	/// and a slow step means the seat's bot took over for a while
	/// (docs/AGENT_API.md §2.1).
	/// </summary>
	public bool RealTime { get; private set; }

	/// <summary>
	/// How long the server will hold the simulation for this learner before
	/// dropping it back to real time (docs/AGENT_API.md §5.2). Worth raising when
	/// two learners share one server, because the hold one of them is waiting
	/// through is the other one's optimizer step (docs/TRAINING.md §8).
	/// </summary>
	public int StepTimeoutMilliseconds { get; private set; } = 10_000;

	public int ReportEvery { get; private set; } = 100;

	public int SaveEvery { get; private set; } = 5_000;

	/// <summary>A CSV file to append one row per report window to, or null.</summary>
	public string MetricsPath { get; private set; }

	/// <summary>
	/// Whether this learner may end the round. <c>reset</c> restarts it for
	/// everybody on the server, so when two learners share one exactly one of them
	/// owns the episode boundary and the other follows it
	/// (docs/TRAINING.md §8).
	/// </summary>
	public bool ResetsRound { get; private set; } = true;

	public bool Help { get; private set; }

	public static TrainerOptions Parse(string[] args, out string error)
	{
		var options = new TrainerOptions();
		error = null;

		for (int i = 0; i < args.Length; i++)
		{
			string arg = args[i];
			if (string.IsNullOrWhiteSpace(arg))
			{
				continue;
			}

			switch (arg)
			{
				case "--help":
				case "-h":
					options.Help = true;
					return options;

				case "--play":
					options.PlayOnly = true;
					break;

				case "--realtime":
					options.RealTime = true;
					break;

				case "--no-reset":
					options.ResetsRound = false;
					break;

				default:
					if (!TryTake(args, ref i, out string value))
					{
						error = $"{arg} needs a value";
						return options;
					}

					if (!Apply(options, arg, value, out error))
					{
						return options;
					}

					break;
			}
		}

		if (options.Ports.Count == 0)
		{
			options.Ports.Add(7900);
		}

		if (options.Algorithm is not ("ppo" or "dqn"))
		{
			error = $"--algo is 'ppo' or 'dqn', not '{options.Algorithm}'";
			return options;
		}

		if (options.Policy is not ("ground" or "strategist"))
		{
			error = $"--policy is 'ground' or 'strategist', not '{options.Policy}'";
			return options;
		}

		if (options.PlayOnly && options.LoadPath == null)
		{
			error = "--play needs --load: there is nothing to play without a policy";
			return options;
		}

		// The two seats are played at different rates and want different horizons,
		// so the defaults follow --policy rather than being one number that is wrong
		// for one of them.
		if (options.StepMul == 0)
		{
			options.StepMul = options.Strategist ? StrategistStepMul : GroundStepMul;
		}

		if (options.MaxEpisodeSteps == 0)
		{
			options.MaxEpisodeSteps = options.Strategist ? StrategistEpisodeSteps : GroundEpisodeSteps;
		}

		return options;
	}

	private static bool Apply(TrainerOptions options, string arg, string value, out string error)
	{
		error = null;
		switch (arg)
		{
			case "--host": options.Host = value; return true;
			case "--token": options.Token = value; return true;
			case "--policy": options.Policy = value.ToLowerInvariant(); return true;
			case "--algo": options.Algorithm = value.ToLowerInvariant(); return true;
			case "--save": options.SavePath = value; return true;
			case "--load": options.LoadPath = value; return true;
			case "--metrics": options.MetricsPath = value; return true;

			case "--port":
				foreach (string part in value.Split(',', StringSplitOptions.RemoveEmptyEntries))
				{
					if (!int.TryParse(part.Trim(), out int port) || port is <= 0 or > 65535)
					{
						error = $"--port: '{part}' is not a port in 1-65535";
						return false;
					}

					options.Ports.Add(port);
				}

				return true;

			case "--steps": return Number(value, arg, v => options.Steps = v, out error);
			case "--step-mul": return Number(value, arg, v => options.StepMul = v, out error);
			case "--episode-steps": return Number(value, arg, v => options.MaxEpisodeSteps = v, out error);
			case "--history": return Number(value, arg, v => options.History = v, out error);
			case "--episodes": return Number(value, arg, v => options.Episodes = v, out error, minimum: 0);
			case "--seed": return Number(value, arg, v => options.Seed = v, out error, minimum: 0);
			case "--width": return Number(value, arg, v => options.Width = v, out error);
			case "--report-every": return Number(value, arg, v => options.ReportEvery = v, out error);
			case "--save-every": return Number(value, arg, v => options.SaveEvery = v, out error);

			case "--step-timeout":
				return Number(value, arg, v => options.StepTimeoutMilliseconds = v, out error,
					minimum: 100);

			case "--lr":
				if (!float.TryParse(value, System.Globalization.NumberStyles.Float,
					System.Globalization.CultureInfo.InvariantCulture, out float lr) || lr <= 0f)
				{
					error = $"--lr: '{value}' is not a positive learning rate";
					return false;
				}

				options.LearningRate = lr;
				return true;

			default:
				error = $"unrecognized argument '{arg}'";
				return false;
		}
	}

	private static bool Number(string value, string arg, Action<int> set, out string error, int minimum = 1)
	{
		error = null;
		if (!int.TryParse(value, out int parsed) || parsed < minimum)
		{
			error = $"{arg}: '{value}' is not a number >= {minimum}";
			return false;
		}

		set(parsed);
		return true;
	}

	private static bool TryTake(string[] args, ref int i, out string value)
	{
		if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
		{
			value = args[++i];
			return true;
		}

		value = null;
		return false;
	}
}
