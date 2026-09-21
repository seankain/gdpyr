using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using OneOf;
using RLMatrix;

namespace Gdpyr.AgentClient;

/// <summary>
/// One ground-force seat, as an RLMatrix environment.
///
/// The whole adapter is this file: RLMatrix asks for a state, hands back a vector
/// of discrete heads, and wants a reward and a done flag;
/// <see cref="GdpyrConnection"/> speaks the agent channel, and
/// <see cref="GroundActionSpace"/> and <see cref="GroundReward"/> are the two
/// choices in between. Neither of those is the game's opinion — the server emits
/// events and no rewards on purpose (docs/AGENT_API.md §8).
///
/// Use it in **stepped** mode for training. In real time the simulation runs at
/// 60 Hz whatever the learner is doing, so a slow optimizer step means the seat
/// falls back to its bot for a while (docs/AGENT_API.md §2.1) and the transition
/// the trainer records is not the transition the game played. That is fine for
/// watching a policy; it is not fine for learning from one.
/// </summary>
public sealed class GdpyrGroundEnv : IEnvironmentAsync<float[]>, IDisposable
{
	private readonly GdpyrConnection _connection;
	private readonly GroundActionSpace _actions;
	private readonly GroundReward _reward;
	private readonly bool _stepped;
	private readonly bool _ownsConnection;

	private float[] _state;
	private float _objectiveDistance;
	private int _stepsThisEpisode;
	private int _seed;
	private bool _done;

	private GdpyrGroundEnv(GdpyrConnection connection, int seat, int stepMul, GroundReward reward,
		bool stepped, int maxEpisodeSteps, int seed, bool ownsConnection)
	{
		_connection = connection;
		_ownsConnection = ownsConnection;
		_stepped = stepped;
		_reward = reward ?? new GroundReward();
		_seed = seed;

		Seat = seat;
		StepMul = stepMul;
		MaxEpisodeSteps = maxEpisodeSteps;

		_actions = new GroundActionSpace(connection.TurnRateRadians, stepMul, connection.Schema.TickRate);
		_state = new float[connection.Schema.Floats];

		stateSize = connection.Schema.Floats;
		actionSize = GroundActionSpace.Heads;
	}

	/// <summary>The peer id of the bot seat this policy is sitting in.</summary>
	public int Seat { get; }

	/// <summary>Ticks one decision is held for (docs/AGENT_API.md §5.3).</summary>
	public int StepMul { get; }

	/// <summary>
	/// Decisions before the episode is truncated and the round restarted. A gdpyr
	/// round is twenty minutes, which at 15 Hz is 18,000 decisions — far too long a
	/// credit-assignment horizon to start from.
	/// </summary>
	public int MaxEpisodeSteps { get; }

	/// <summary>Reward accumulated since the last reset. For the log.</summary>
	public float EpisodeReward { get; private set; }

	/// <summary>Episodes finished. For the log.</summary>
	public int Episodes { get; private set; }

	// RLMatrix's interface spells these in camelCase; they are set once, in the
	// constructor, from the schema the server published.
	public OneOf<int, (int, int)> stateSize { get; set; }

	public int[] actionSize { get; set; }

	/// <summary>
	/// Connects, attaches to a ground seat and gets the round moving.
	/// </summary>
	public static async Task<GdpyrGroundEnv> CreateAsync(string host = "127.0.0.1", int port = 7900,
		string token = null, int stepMul = 4, bool stepped = true, int maxEpisodeSteps = 1800,
		int seed = 0, GroundReward reward = null, CancellationToken cancel = default)
	{
		GdpyrConnection connection = await GdpyrConnection
			.ConnectAsync(host, port, token, cancel)
			.ConfigureAwait(false);

		try
		{
			if (stepped)
			{
				await connection.ConfigureAsync(stepped: true, cancel: cancel).ConfigureAwait(false);
			}

			int seat = await connection.AttachGroundAsync(stepMul, cancel: cancel).ConfigureAwait(false);
			var env = new GdpyrGroundEnv(connection, seat, stepMul, reward, stepped, maxEpisodeSteps, seed,
				ownsConnection: true);

			await env.Reset().ConfigureAwait(false);
			return env;
		}
		catch
		{
			connection.Dispose();
			throw;
		}
	}

	public Task<float[]> GetCurrentState() => Task.FromResult(_state);

	public async Task Reset()
	{
		// A fresh round rather than a fresh connection: the seat, the loadout and the
		// roster slot survive, which is what makes an episode boundary cheap.
		await _connection.ResetAsync(_seed).ConfigureAwait(false);

		if (_stepped)
		{
			// One tick so the round is live and the character has landed before the
			// first observation is taken.
			await _connection.StepAsync(1).ConfigureAwait(false);
		}

		GdpyrObservation observation = await ObserveAsync().ConfigureAwait(false);
		Adopt(observation);

		// The round_end the reset itself caused, and the round_start that followed
		// it, arrive on the stream a tick later — after ResetAsync has already
		// returned. Left in the queue they would be read by the first Step of the new
		// episode, which would see a round_end and end it immediately: an episode one
		// decision long, for ever.
		foreach (GdpyrEvent _ in _connection.DrainEvents())
		{
		}

		EpisodeReward = 0f;
		_stepsThisEpisode = 0;
		_done = false;
	}

	public async Task<(float, bool)> Step(int[] actions)
	{
		if (_done)
		{
			await Reset().ConfigureAwait(false);
		}

		AgentGroundAction action = _actions.Decode(actions);
		_connection.Act(Seat, action, _connection.Tick);

		bool truncated = false;
		if (_stepped)
		{
			truncated = await _connection.StepAsync(StepMul).ConfigureAwait(false);
		}
		else
		{
			await _connection.PollAsync().ConfigureAwait(false);
		}

		GdpyrObservation observation = await ObserveAsync().ConfigureAwait(false);
		float previousDistance = _objectiveDistance;
		Adopt(observation);

		var events = new List<GdpyrEvent>();
		foreach (GdpyrEvent record in _connection.DrainEvents())
		{
			events.Add(record);
		}

		float reward = _reward.Score(Seat, events, previousDistance, _objectiveDistance,
			out bool roundEnded, out bool _);

		_stepsThisEpisode++;
		EpisodeReward += reward;

		_done = roundEnded || truncated || _stepsThisEpisode >= MaxEpisodeSteps;
		if (_done)
		{
			Episodes++;
		}

		return (reward, _done);
	}

	/// <summary>
	/// Changes the seed the next reset will label its episode with
	/// (docs/AGENT_API.md §7.1). The simulation has no global RNG to seed, so this
	/// labels the episode rather than determining it.
	/// </summary>
	public void SetSeed(int seed) => _seed = seed;

	public void Dispose()
	{
		if (_ownsConnection)
		{
			_connection.Dispose();
		}
	}

	private async Task<GdpyrObservation> ObserveAsync()
	{
		// Asked for rather than waited for: the server also pushes an observation on
		// every decision tick, and a trainer that read those would be one decision
		// behind whenever a step boundary and a decision tick did not line up.
		return _stepped
			? await _connection.ObserveAsync(Seat).ConfigureAwait(false)
			: await _connection.NextObservationAsync().ConfigureAwait(false);
	}

	private void Adopt(GdpyrObservation observation)
	{
		_state = observation.Values;
		_objectiveDistance = observation.Scalar(_connection.Schema, "objective.distance");
	}
}
