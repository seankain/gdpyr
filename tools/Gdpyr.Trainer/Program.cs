using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using Gdpyr.AgentClient;
using RLMatrix;
using RLMatrix.Agents.Common;

namespace Gdpyr.Trainer;

/// <summary>
/// Trains a policy for either gdpyr seat against one or more headless servers
/// (docs/TRAINING.md).
///
/// It is deliberately thin. Everything that decides what gets learned lives
/// somewhere a person can read it: <see cref="GroundActionSpace"/> and
/// <see cref="StrategistActionSpace"/> for what a policy may do,
/// <see cref="GroundReward"/> and <see cref="StrategistReward"/> for what it is
/// being asked to want, and the options below for how it learns. The game itself
/// has no opinion on any of them (docs/AGENT_API.md §8), and which algorithm to
/// reach for is argued in docs/RL_ARCHITECTURE.md rather than decided here.
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
		var seats = new List<IGdpyrSeat>();
		var metrics = new MetricsLog(options.MetricsPath);

		try
		{
			// One environment per port. RLMatrix collects transitions from all of them
			// into one learner, and a gdpyr server is capped at 60 ticks of game per
			// second of wall clock (docs/AGENT_API.md §5.4), so more instances is the
			// answer to wanting more throughput — not a faster one.
			foreach (int port in options.Ports)
			{
				IGdpyrSeat seat = options.Strategist
					? await Strategist(options, port).ConfigureAwait(false)
					: await Ground(options, port).ConfigureAwait(false);

				seats.Add(seat);
				environments.Add((IEnvironmentAsync<float[]>)seat);
				Console.WriteLine($"attached to {options.Host}:{port} seat {seat.Seat}"
					+ $" | {options.Policy} | step_mul {seat.StepMul}"
					+ $" | {seat.StateFloats} floats ({options.History} frame(s))");
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
				+ $" | {options.Algorithm} | {environments.Count} environment(s)"
				+ (options.Episodes > 0 ? $" | stopping at {options.Episodes} episodes" : string.Empty));

			int step = 0;
			for (step = 1; step <= options.Steps; step++)
			{
				await agent.Step(learning).ConfigureAwait(false);

				if (step % options.ReportEvery == 0)
				{
					Report(step, options.Steps, seats, metrics);
				}

				if (learning && options.SavePath != null && step % options.SaveEvery == 0)
				{
					await agent.Save(WithSeparator(options.SavePath)).ConfigureAwait(false);
				}

				// An evaluation run is counted in episodes rather than in steps: "how
				// often does this policy win" is a question about rounds, and a round is
				// a different number of decisions every time.
				if (options.Episodes > 0 && Episodes(seats) >= options.Episodes)
				{
					break;
				}
			}

			if (learning && options.SavePath != null)
			{
				await agent.Save(WithSeparator(options.SavePath)).ConfigureAwait(false);
				Console.WriteLine($"saved into {options.SavePath}");
			}

			Report(Math.Min(step, options.Steps), options.Steps, seats, metrics);
			Summarize(options, seats);
			return 0;
		}
		finally
		{
			metrics.Dispose();
			foreach (IGdpyrSeat seat in seats)
			{
				seat.Dispose();
			}
		}
	}

	private static async Task<IGdpyrSeat> Ground(TrainerOptions options, int port) =>
		await GdpyrGroundEnv.CreateAsync(
			host: options.Host,
			port: port,
			token: options.Token,
			stepMul: options.StepMul,
			stepped: !options.RealTime,
			maxEpisodeSteps: options.MaxEpisodeSteps,
			seed: options.Seed,
			history: options.History,
			stepTimeoutMilliseconds: options.StepTimeoutMilliseconds,
			resetsRound: options.ResetsRound).ConfigureAwait(false);

	private static async Task<IGdpyrSeat> Strategist(TrainerOptions options, int port) =>
		await GdpyrStrategistEnv.CreateAsync(
			host: options.Host,
			port: port,
			token: options.Token,
			stepMul: options.StepMul,
			stepped: !options.RealTime,
			maxEpisodeSteps: options.MaxEpisodeSteps,
			seed: options.Seed,
			history: options.History,
			stepTimeoutMilliseconds: options.StepTimeoutMilliseconds,
			resetsRound: options.ResetsRound).ConfigureAwait(false);

	private static int Episodes(List<IGdpyrSeat> seats)
	{
		int episodes = 0;
		for (int i = 0; i < seats.Count; i++)
		{
			episodes += seats[i].Episodes;
		}

		return episodes;
	}

	/// <summary>
	/// What the last window looked like. Reward is the only number a trainer
	/// actually watches, and it is the one this repository made up
	/// (<see cref="GroundReward"/>, <see cref="StrategistReward"/>) — so it is
	/// printed next to the episode count and the win rate, which are the game's own.
	/// </summary>
	private static void Report(int step, int steps, List<IGdpyrSeat> seats, MetricsLog metrics)
	{
		float inFlight = 0f;
		float finished = 0f;
		int episodes = 0;
		int wins = 0;
		int losses = 0;
		int fallbacks = 0;

		foreach (IGdpyrSeat seat in seats)
		{
			inFlight += seat.EpisodeReward;
			finished += seat.LastEpisodeReward;
			episodes += seat.Episodes;
			wins += seat.Wins;
			losses += seat.Losses;
			fallbacks += seat.PilotFallbacks;
		}

		int count = Math.Max(seats.Count, 1);
		int decided = wins + losses;

		Console.WriteLine($"step {step}/{steps}"
			+ $" | episodes {episodes}"
			+ $" | last episode {finished / count:0.00}"
			+ $" | reward in flight {inFlight / count:0.00}"
			+ $" | wins {wins}/{decided}"
			+ (fallbacks > 0 ? $" | bot fallbacks {fallbacks}" : string.Empty));

		metrics.Write(step, episodes, finished / count, inFlight / count, wins, losses, fallbacks);
	}

	/// <summary>
	/// The line a run is judged by. A truncated episode has no outcome, so the
	/// denominator is decided rounds rather than episodes — a policy whose episodes
	/// all truncate has no win rate, and saying so is more useful than dividing by
	/// the wrong thing (docs/RL_ARCHITECTURE.md §6).
	/// </summary>
	private static void Summarize(TrainerOptions options, List<IGdpyrSeat> seats)
	{
		int episodes = 0;
		int wins = 0;
		int losses = 0;

		foreach (IGdpyrSeat seat in seats)
		{
			episodes += seat.Episodes;
			wins += seat.Wins;
			losses += seat.Losses;
		}

		int decided = wins + losses;
		string rate = decided > 0
			? (wins / (float)decided).ToString("P1", CultureInfo.InvariantCulture)
			: "no decided rounds";

		Console.WriteLine($"{options.Policy}: {episodes} episodes, {decided} decided,"
			+ $" {wins} won, {losses} lost, win rate {rate}");
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
	/// noisy layers in place of ε-greedy. That is most of Rainbow, and it is the
	/// off-policy arm of the comparison docs/RL_ARCHITECTURE.md §3 asks you to run.
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
