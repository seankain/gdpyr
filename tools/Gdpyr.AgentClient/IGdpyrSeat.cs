using System;

namespace Gdpyr.AgentClient;

/// <summary>
/// What a trainer needs from a seat that is not "hand me a tensor": the counters
/// a run is watched and evaluated by.
///
/// RLMatrix's <c>IEnvironmentAsync</c> deliberately knows nothing about episodes,
/// rounds or outcomes — it asks for a state and hands back a reward. This is the
/// other half, and it is the same on both sides of the round so that
/// <c>gdpyr-train</c> can report a ground run and a strategist run with one
/// function (docs/TRAINING.md §9).
/// </summary>
public interface IGdpyrSeat : IDisposable
{
	/// <summary>The peer id of the bot seat this policy is sitting in.</summary>
	int Seat { get; }

	/// <summary>Ticks one decision is held for (docs/AGENT_API.md §5.3).</summary>
	int StepMul { get; }

	/// <summary>Floats in one decision's state, history included.</summary>
	int StateFloats { get; }

	/// <summary>Reward accumulated since the last reset.</summary>
	float EpisodeReward { get; }

	/// <summary>What the last finished episode scored, or 0 before one has.</summary>
	float LastEpisodeReward { get; }

	/// <summary>Episodes finished.</summary>
	int Episodes { get; }

	/// <summary>Episodes this seat's side won. A truncated episode counts as neither.</summary>
	int Wins { get; }

	/// <summary>Episodes this seat's side lost.</summary>
	int Losses { get; }

	/// <summary>Decisions the seat's bot had to cover for, as the last step reported it.</summary>
	int PilotFallbacks { get; }

	/// <summary>
	/// Changes the seed the next reset will label its episode with. The simulation
	/// has no global RNG to seed, so this labels an episode rather than determining
	/// one (docs/AGENT_API.md §3.1).
	/// </summary>
	void SetSeed(int seed);
}
