using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using OneOf;
using RLMatrix;

namespace Gdpyr.AgentClient;

/// <summary>
/// One strategist seat, as an RLMatrix environment.
///
/// The ground seat's twin (<see cref="GdpyrGroundEnv"/>), and the same three
/// pieces: <see cref="GdpyrConnection"/> speaks the socket,
/// <see cref="StrategistActionSpace"/> decides which slice of the command space a
/// policy may reach, and <see cref="StrategistReward"/> decides what it is being
/// asked to want. The middle one is the difference that matters. A ground action
/// is an <c>InputFrame</c> and discretizing it is arithmetic; a strategist action
/// is a list of commands naming arbitrary units and arbitrary points, and
/// choosing what a policy may say is a research decision rather than plumbing —
/// which is why M7 shipped the protocol and no environment, and why this one is
/// three files you can replace rather than one you cannot see into
/// (docs/TRAINING.md §7).
///
/// Two differences from a ground seat leak into this file:
///
/// - **A command list is consumed, not held.** A ground action repeats for the
///   seat's <c>step_mul</c> because that is what a held fire button is; a command
///   list run thirty times would queue thirty riflemen (docs/AGENT_API.md §7.4).
///   So one decision is one list, and the seat is quiet for the rest of the
///   window — which is a strategist deciding to do nothing, not a seat that has
///   stopped deciding.
/// - **The observation carries no unit ids**, and an order names ids. Every
///   decision therefore asks <c>list_units</c> for the army in the order the
///   observation carries it (§7.5). One round trip at 2 Hz, and the alternative
///   is a client-side roster inferred from the event stream that drifts the first
///   time a corpse outlives its <c>unit_lost</c>.
/// </summary>
public sealed class GdpyrStrategistEnv : IEnvironmentAsync<float[]>, IGdpyrSeat
{
	private readonly GdpyrConnection _connection;
	private readonly StrategistActionSpace _actions;
	private readonly StrategistReward _reward;
	private readonly ObservationStack _history;
	private readonly List<GdpyrCommand> _commands = new();
	private readonly List<GdpyrEvent> _events = new();
	private readonly bool _stepped;
	private readonly bool _resetsRound;
	private readonly bool _ownsConnection;

	private List<GdpyrUnit> _army = new();
	private float[] _state;
	private float[] _observation = Array.Empty<float>();
	private float _ticketFraction;
	private float _income;
	private int _stepsThisEpisode;
	private int _seed;
	private bool _done;

	private GdpyrStrategistEnv(GdpyrConnection connection, int seat, int stepMul, StrategistReward reward,
		bool stepped, int maxEpisodeSteps, int seed, int history, bool resetsRound, bool ownsConnection)
	{
		_connection = connection;
		_ownsConnection = ownsConnection;
		_stepped = stepped;
		_resetsRound = resetsRound;
		_reward = reward ?? new StrategistReward();
		_seed = seed;

		Seat = seat;
		StepMul = stepMul;
		MaxEpisodeSteps = maxEpisodeSteps;

		_actions = new StrategistActionSpace(connection.Schema);
		_history = new ObservationStack(connection.Schema.StrategistFloats, history);
		_state = new float[_history.Floats];

		stateSize = _history.Floats;
		actionSize = StrategistActionSpace.Heads;
	}

	/// <summary>The peer id of the strategist seat this policy is sitting in.</summary>
	public int Seat { get; }

	/// <summary>
	/// Ticks one decision is held for. 30 is 2 Hz, which is well inside the eight
	/// commands a second the APM cap allows (docs/AGENT_API.md §7.3) and is roughly
	/// the rate a person plays this chair at.
	/// </summary>
	public int StepMul { get; }

	/// <summary>Floats in one decision's state: the observation, times the frames stacked.</summary>
	public int StateFloats => _history.Floats;

	/// <summary>Observations concatenated into one state (docs/RL_ARCHITECTURE.md §4).</summary>
	public int History => _history.Frames;

	/// <summary>
	/// Decisions before the episode is truncated and the round restarted. A
	/// twenty-minute round at 2 Hz is 2,400 decisions, so the default is a whole
	/// round: a strategist's outcome terms are the win condition, and an episode
	/// that is always truncated never sees one.
	/// </summary>
	public int MaxEpisodeSteps { get; }

	public float EpisodeReward { get; private set; }

	public float LastEpisodeReward { get; private set; }

	public int Episodes { get; private set; }

	/// <summary>Rounds this side won: the ground force ran out of tickets.</summary>
	public int Wins { get; private set; }

	/// <summary>Rounds this side lost: the clock ran out, or it was eliminated.</summary>
	public int Losses { get; private set; }

	/// <summary>Decisions this seat's bot covered for, as the last step reported it.</summary>
	public int PilotFallbacks { get; private set; }

	/// <summary>The commands the last decision produced. For a trace and for the log.</summary>
	public IReadOnlyList<GdpyrCommand> LastCommands => _commands;

	public OneOf<int, (int, int)> stateSize { get; set; }

	public int[] actionSize { get; set; }

	/// <summary>Connects, attaches to a strategist seat and gets the round moving.</summary>
	public static async Task<GdpyrStrategistEnv> CreateAsync(string host = "127.0.0.1", int port = 7900,
		string token = null, int stepMul = 30, bool stepped = true, int maxEpisodeSteps = 2400,
		int seed = 0, int history = 1, int stepTimeoutMilliseconds = 10_000, bool resetsRound = true,
		StrategistReward reward = null, CancellationToken cancel = default)
	{
		GdpyrConnection connection = await GdpyrConnection
			.ConnectAsync(host, port, token, cancel)
			.ConfigureAwait(false);

		try
		{
			if (connection.Schema.StrategistFloats <= 0)
			{
				throw new InvalidOperationException(
					"this server publishes no strategist observation; it is older than M7");
			}

			if (stepped)
			{
				await connection
					.ConfigureAsync(stepped: true, stepTimeoutMilliseconds: stepTimeoutMilliseconds,
						cancel: cancel)
					.ConfigureAwait(false);
			}

			int seat = await connection.AttachStrategistAsync(stepMul, cancel: cancel).ConfigureAwait(false);
			var env = new GdpyrStrategistEnv(connection, seat, stepMul, reward, stepped, maxEpisodeSteps,
				seed, history, resetsRound, ownsConnection: true);

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
		// `reset` ends the round for everybody on the server, so when a ground
		// learner shares it exactly one of the two may own the episode boundary
		// (docs/TRAINING.md §8). A follower skips the call and picks the new round
		// up off its own observation.
		if (_resetsRound)
		{
			await _connection.ResetAsync(_seed).ConfigureAwait(false);
		}

		if (_stepped)
		{
			// One tick so the round is live: the barracks exist and the nodes have been
			// placed before the first observation is taken, which is what the action
			// space fits its grid to. A follower takes this tick too — the round it is
			// picking up was reset by somebody else just as recently.
			await _connection.StepAsync(1).ConfigureAwait(false);
		}

		GdpyrObservation observation = await ObserveAsync().ConfigureAwait(false);
		Adopt(observation, fresh: true);
		await RefreshArmyAsync().ConfigureAwait(false);

		// The round_end the reset itself caused arrives a tick after ResetAsync
		// returned; left in the queue the first Step would read it and end the
		// episode one decision in, for ever.
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

		_actions.Decode(actions, _observation, _army, _commands);

		// An empty list is still a decision: it says this seat chose to do nothing
		// this tick, and it keeps the lease alive so the seat does not fall back to
		// BotStrategist (docs/AGENT_API.md §2.1).
		_connection.ActCommands(Seat, _commands, _connection.Tick);

		bool truncated = false;
		if (_stepped)
		{
			truncated = await _connection.StepAsync(StepMul).ConfigureAwait(false);
		}
		else
		{
			await _connection.PollAsync().ConfigureAwait(false);
		}

		Fallbacks();

		GdpyrObservation observation = await ObserveAsync().ConfigureAwait(false);
		float previousTickets = _ticketFraction;
		float previousIncome = _income;
		Adopt(observation, fresh: false);
		await RefreshArmyAsync().ConfigureAwait(false);

		_events.Clear();
		foreach (GdpyrEvent record in _connection.DrainEvents())
		{
			_events.Add(record);
		}

		float reward = _reward.Score(_events, previousTickets, _ticketFraction, previousIncome, _income,
			out bool roundEnded);

		_stepsThisEpisode++;
		EpisodeReward += reward;

		_done = roundEnded || truncated || _stepsThisEpisode >= MaxEpisodeSteps;
		if (_done)
		{
			Episodes++;
			LastEpisodeReward = EpisodeReward;

			// RoundOutcome: 1 ground eliminated — the strategist's win — against 2
			// time expired and 3 strategist eliminated. A truncated episode has no
			// outcome and counts as neither.
			switch (Outcome())
			{
				case 1: Wins++; break;
				case 2:
				case 3: Losses++; break;
			}
		}

		return (reward, _done);
	}

	public void SetSeed(int seed) => _seed = seed;

	public void Dispose()
	{
		if (_ownsConnection)
		{
			_connection.Dispose();
		}
	}

	private int Outcome()
	{
		for (int i = 0; i < _events.Count; i++)
		{
			if (_events[i].Kind == "round_end")
			{
				return (int)_events[i].Get("outcome");
			}
		}

		return 0;
	}

	private void Fallbacks()
	{
		for (int i = 0; i < _connection.LastStep.Count; i++)
		{
			if (_connection.LastStep[i].Seat == Seat)
			{
				PilotFallbacks = _connection.LastStep[i].PilotFallbacks;
				return;
			}
		}
	}

	private async Task<GdpyrObservation> ObserveAsync() =>
		_stepped
			? await _connection.ObserveAsync(Seat).ConfigureAwait(false)
			: await _connection.NextObservationAsync().ConfigureAwait(false);

	/// <summary>
	/// The ids the next order may name, in the order the observation carries the
	/// army (docs/AGENT_API.md §7.5). A seat that has just lost its chair gets an
	/// empty roster rather than an exception: the next observation will say so.
	/// </summary>
	private async Task RefreshArmyAsync()
	{
		try
		{
			_army = await _connection.ListUnitsAsync(Seat).ConfigureAwait(false);
		}
		catch (InvalidOperationException)
		{
			_army = new List<GdpyrUnit>();
		}
	}

	private void Adopt(GdpyrObservation observation, bool fresh)
	{
		_observation = observation.Values;
		_state = fresh ? _history.Fill(observation.Values) : _history.Push(observation.Values);
		_ticketFraction = observation.Scalar(_connection.Schema, "round.ticket_fraction");
		_income = observation.Scalar(_connection.Schema, "round.income_paid");
	}
}
