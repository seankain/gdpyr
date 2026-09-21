using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Gdpyr.AgentClient;
using RLMatrix;
using RLMatrix.Agents.Common;

namespace Gdpyr.Trainer;

/// <summary>
/// Trains a ground-force policy against one or more headless gdpyr servers
/// (docs/TRAINING.md).
///
/// It is deliberately thin. Everything that decides what gets learned lives
/// somewhere a person can read it: <see cref="GroundActionSpace"/> for what the
/// policy may do, <see cref="GroundReward"/> for what it is being asked to want,
/// and the options below for how it learns. The game itself has no opinion on any
/// of the three (docs/AGENT_API.md §8).
/// </summary>
public static class Program
{
	public static async Task<int> Main(string[] args)
	{
		var options = TrainerOptions.Parse(args, out string error);
		if (error != null)
		{
			Console.Error.WriteLine($"gdpyr-train: {error}");
			Console.Error.WriteLine(TrainerOptions.Usage);
			return 2;
		}

		if (options.Help)
		{
			Console.WriteLine(TrainerOptions.Usage);
			return 0;
		}

		try
		{
			return await RunAsync(options).ConfigureAwait(false);
		}
		catch (Exception failure)
		{
			Console.Error.WriteLine($"gdpyr-train: {failure.Message}");
			return 1;
		}
	}

	private static async Task<int> RunAsync(TrainerOptions options)
	{
		var environments = new List<IEnvironmentAsync<float[]>>();
		var seats = new List<GdpyrGroundEnv>();

		try
		{
			// One environment per port. RLMatrix collects transitions from all of them
			// into one learner, and a gdpyr server is capped at 60 ticks of game per
			// second of wall clock (docs/AGENT_API.md §5.4), so more instances is the
			// answer to wanting more throughput — not a faster one.
			foreach (int port in options.Ports)
			{
				GdpyrGroundEnv env = await GdpyrGroundEnv.CreateAsync(
					host: options.Host,
					port: port,
					token: options.Token,
					stepMul: options.StepMul,
					stepped: !options.RealTime,
					maxEpisodeSteps: options.MaxEpisodeSteps,
					seed: options.Seed).ConfigureAwait(false);

				seats.Add(env);
				environments.Add(env);
				Console.WriteLine($"attached to {options.Host}:{port} seat {env.Seat}"
					+ $" | step_mul {env.StepMul} | {((int)env.stateSize.AsT0)} floats");
			}

			LocalDiscreteRolloutAgent<float[]> agent = options.Algorithm == "dqn"
				? new LocalDiscreteRolloutAgent<float[]>(Dqn(options), environments)
				: new LocalDiscreteRolloutAgent<float[]>(Ppo(options), environments);

			// RLMatrix saves and loads a *directory*: it writes an actor and a critic (or
			// a policy and a target) into it, auto-numbered, so a run that saves every
			// few thousand steps leaves a series rather than one clobbered file.
			if (options.LoadPath != null)
			{
				await agent.Load(WithSeparator(options.LoadPath)).ConfigureAwait(false);
				Console.WriteLine($"loaded {options.LoadPath}");
			}

			if (options.SavePath != null)
			{
				System.IO.Directory.CreateDirectory(options.SavePath);
			}

			bool learning = !options.PlayOnly;
			Console.WriteLine($"{(learning ? "training" : "playing")} for {options.Steps} steps"
				+ $" | {options.Algorithm} | {environments.Count} environment(s)");

			for (int step = 1; step <= options.Steps; step++)
			{
				await agent.Step(learning).ConfigureAwait(false);

				if (step % options.ReportEvery == 0)
				{
					Report(step, options.Steps, seats);
				}

				if (learning && options.SavePath != null && step % options.SaveEvery == 0)
				{
					await agent.Save(WithSeparator(options.SavePath)).ConfigureAwait(false);
				}
			}

			if (learning && options.SavePath != null)
			{
				await agent.Save(WithSeparator(options.SavePath)).ConfigureAwait(false);
				Console.WriteLine($"saved into {options.SavePath}");
			}

			Report(options.Steps, options.Steps, seats);
			return 0;
		}
		finally
		{
			foreach (GdpyrGroundEnv env in seats)
			{
				env.Dispose();
			}
		}
	}

	/// <summary>
	/// What the last window looked like. Reward is the only number a trainer
	/// actually watches, and it is the one this file made up
	/// (<see cref="GroundReward"/>) — so it is printed next to the episode count
	/// rather than on its own.
	/// </summary>
	private static void Report(int step, int steps, List<GdpyrGroundEnv> seats)
	{
		float reward = 0f;
		int episodes = 0;
		foreach (GdpyrGroundEnv env in seats)
		{
			reward += env.EpisodeReward;
			episodes += env.Episodes;
		}

		Console.WriteLine($"step {step}/{steps}"
			+ $" | episodes {episodes}"
			+ $" | reward in flight {reward / Math.Max(seats.Count, 1):0.00}");
	}

	/// <summary>
	/// RLMatrix appends the file names itself and only notices the directory when
	/// the path ends in a separator.
	/// </summary>
	private static string WithSeparator(string path) =>
		path.EndsWith(System.IO.Path.DirectorySeparatorChar) ? path : path + System.IO.Path.DirectorySeparatorChar;

	/// <summary>
	/// A DQN with the modifications that usually matter on a partially observed
	/// control problem: distributional heads, n-step returns, prioritized replay and
	/// noisy layers in place of ε-greedy.
	/// </summary>
	private static DQNAgentOptions Dqn(TrainerOptions options) => new(
		batchSize: 64,
		memorySize: 100_000,
		gamma: 0.99f,
		epsStart: 1f,
		epsEnd: 0.02f,
		epsDecay: 150f,
		tau: 0.005f,
		lr: options.LearningRate,
		depth: 2,
		width: options.Width,
		numAtoms: 51,
		vMin: -20f,
		vMax: 20f,
		nStepReturn: 3,
		doubleDQN: true,
		duelingDQN: true,
		noisyLayers: true,
		categoricalDQN: true,
		prioritizedExperienceReplay: true,
		batchedActionProcessing: true);

	/// <summary>
	/// PPO, which is the first thing to try when the reward is dense enough to have
	/// a gradient and the episode is long. <c>batchSize</c> is in *episodes*.
	/// </summary>
	private static PPOAgentOptions Ppo(TrainerOptions options) => new(
		batchSize: 4,
		memorySize: 10_000,
		gamma: 0.99f,
		gaeLambda: 0.95f,
		lr: options.LearningRate,
		depth: 2,
		width: options.Width,
		clipEpsilon: 0.2f,
		vClipRange: 0.2f,
		cValue: 0.5f,
		ppoEpochs: 4,
		clipGradNorm: 0.5f,
		entropyCoefficient: 0.01f);
}
