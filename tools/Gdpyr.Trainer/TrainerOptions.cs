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
		+ "                   [--algo ppo|dqn] [--steps 100000] [--step-mul 4]\n"
		+ "                   [--episode-steps 1800] [--seed 0] [--lr 1e-4] [--width 512]\n"
		+ "                   [--save <dir>] [--load <dir>] [--play] [--realtime]\n"
		+ "                   [--report-every 100] [--save-every 5000]\n"
		+ "  --port      one or more agent-api ports; one gdpyr server per port\n"
		+ "  --algo      ppo (default) or dqn\n"
		+ "  --steps     rollout steps to run\n"
		+ "  --step-mul  ticks one decision is held for; 4 is 15 Hz\n"
		+ "  --save      directory to write checkpoints into; RLMatrix names the files\n"
		+ "  --load      directory to read a checkpoint back from\n"
		+ "  --play      run the loaded policy without learning from it\n"
		+ "  --realtime  do not engage stepped mode; for watching, not for training\n"
		+ "Start a server for each port with:\n"
		+ "  godot --path . --headless -- --server 7777 --agent-api 7900\n"
		+ "See docs/TRAINING.md.";

	public string Host { get; private set; } = "127.0.0.1";

	public List<int> Ports { get; } = new();

	public string Token { get; private set; }

	public string Algorithm { get; private set; } = "ppo";

	public int Steps { get; private set; } = 100_000;

	public int StepMul { get; private set; } = 4;

	public int MaxEpisodeSteps { get; private set; } = 1_800;

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

	/// <summary>
	/// Real time rather than stepped. Useful for watching a trained policy; wrong
	/// for learning, because the sim runs at 60 Hz whatever the optimizer is doing
	/// and a slow step means the seat's bot took over for a while
	/// (docs/AGENT_API.md §2.1).
	/// </summary>
	public bool RealTime { get; private set; }

	public int ReportEvery { get; private set; } = 100;

	public int SaveEvery { get; private set; } = 5_000;

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
		}

		if (options.PlayOnly && options.LoadPath == null)
		{
			error = "--play needs --load: there is nothing to play without a policy";
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
			case "--algo": options.Algorithm = value.ToLowerInvariant(); return true;
			case "--save": options.SavePath = value; return true;
			case "--load": options.LoadPath = value; return true;

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
			case "--seed": return Number(value, arg, v => options.Seed = v, out error, minimum: 0);
			case "--width": return Number(value, arg, v => options.Width = v, out error);
			case "--report-every": return Number(value, arg, v => options.ReportEvery = v, out error);
			case "--save-every": return Number(value, arg, v => options.SaveEvery = v, out error);

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
