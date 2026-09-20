namespace Gdpyr.Sim;

/// <summary>What a client should do with an authoritative state it just received.</summary>
public enum ReplayAction
{
	/// <summary>Older than a state already applied. UDP reorders; nothing to do.</summary>
	Ignore,

	/// <summary>The prediction held. Correcting would cost a replay and gain nothing.</summary>
	InSync,

	/// <summary>No prediction on record for that tick — take the server's state as-is.</summary>
	Snap,

	/// <summary>Rewind to the server's state and re-run the inputs it has not seen yet.</summary>
	Replay,
}

/// <summary>The outcome of comparing one snapshot against the prediction for its tick.</summary>
public readonly struct ReplayDecision
{
	public readonly ReplayAction Action;

	/// <summary>First tick to replay. Only meaningful for <see cref="ReplayAction.Replay"/>.</summary>
	public readonly uint FromTick;

	/// <summary>Last tick to replay, inclusive.</summary>
	public readonly uint ToTick;

	public readonly float PositionError;
	public readonly float VelocityError;

	public ReplayDecision(ReplayAction action, uint fromTick = 0, uint toTick = 0,
		float positionError = 0f, float velocityError = 0f)
	{
		Action = action;
		FromTick = fromTick;
		ToTick = toTick;
		PositionError = positionError;
		VelocityError = velocityError;
	}

	/// <summary>Ticks this decision asks to be re-simulated.</summary>
	public int ReplayLength => Action == ReplayAction.Replay ? (int)(ToTick - FromTick + 1) : 0;
}

/// <summary>
/// The client's record of what it predicted, and the decision of what to do when
/// the server disagrees (docs/NETCODE.md §3.2).
///
/// Separated from the character so the decision — the part that is all off-by-one
/// risk and no engine — can be tested without one. The replay itself has to run
/// through the physics engine, so the caller does that; this only says which ticks
/// and from what state.
/// </summary>
public sealed class PredictionLedger
{
	private const int Capacity = SimConfig.InputBufferTicks;
	private const int Mask = Capacity - 1;

	private readonly InputFrame[] _inputs = new InputFrame[Capacity];
	private readonly CharacterState[] _states = new CharacterState[Capacity];

	/// <summary>The newest tick predicted locally.</summary>
	public uint LastRecordedTick { get; private set; }

	/// <summary>The newest input tick the server has acknowledged.</summary>
	public uint LastAckTick { get; private set; }

	/// <summary>Corrections applied since the last <see cref="Reset"/>.</summary>
	public int Corrections { get; private set; }

	/// <summary>Ticks re-simulated by the last correction.</summary>
	public int LastReplayLength { get; private set; }

	private bool _recording;
	private bool _acknowledged;

	/// <summary>
	/// Records an input that was sampled but not predicted — before the local
	/// character exists, there is intent to send but no state to compare.
	/// </summary>
	public void RecordInput(uint tick, in InputFrame input)
	{
		_inputs[tick & Mask] = input;
		_states[tick & Mask] = default;
		LastRecordedTick = tick;
	}

	public void Record(uint tick, in InputFrame input, in CharacterState state)
	{
		_inputs[tick & Mask] = input;
		_states[tick & Mask] = state;
		LastRecordedTick = tick;
		_recording = true;
	}

	/// <summary>Overwrites a predicted state after it has been replayed.</summary>
	public void UpdateState(uint tick, in CharacterState state)
	{
		if (_states[tick & Mask].Tick == tick || _inputs[tick & Mask].Tick == tick)
		{
			_states[tick & Mask] = state;
		}
	}

	public bool TryGetInput(uint tick, out InputFrame input)
	{
		input = _inputs[tick & Mask];
		return input.Tick == tick;
	}

	public bool TryGetState(uint tick, out CharacterState state)
	{
		state = _states[tick & Mask];
		return state.Tick == tick;
	}

	public ReplayDecision Decide(in PlayerSnapshot snapshot)
	{
		uint ack = snapshot.LastInputTick;

		// A repeated acknowledgement means the server starved and re-ran our last
		// input; an older one means the snapshots arrived out of order. Either way
		// this state says nothing new about our prediction.
		if (_acknowledged && ack <= LastAckTick)
		{
			return new ReplayDecision(ReplayAction.Ignore);
		}

		if (ack != 0)
		{
			LastAckTick = ack;
			_acknowledged = true;
		}

		bool replayable = ack != 0
			&& _recording
			&& LastRecordedTick >= ack
			&& LastRecordedTick - ack < Capacity
			&& _states[ack & Mask].Tick == ack;

		if (!replayable)
		{
			// A fresh spawn, a clock resync, or a server that has not consumed any of
			// our input yet. There is nothing to compare against.
			return new ReplayDecision(ReplayAction.Snap);
		}

		CharacterState predicted = _states[ack & Mask];
		float positionError = (predicted.Position - snapshot.Position).Length();
		float velocityError = (predicted.Velocity - snapshot.Velocity).Length();

		if (positionError <= SimConfig.MispredictionThreshold
			&& velocityError <= SimConfig.VelocityCorrectionThreshold)
		{
			return new ReplayDecision(ReplayAction.InSync, positionError: positionError, velocityError: velocityError);
		}

		Corrections++;
		LastReplayLength = (int)(LastRecordedTick - ack);
		return new ReplayDecision(ReplayAction.Replay, ack + 1, LastRecordedTick, positionError, velocityError);
	}

	/// <summary>Forgets everything. Called when the local character is (re)spawned.</summary>
	public void Reset()
	{
		_recording = false;
		_acknowledged = false;
		LastRecordedTick = 0;
		LastAckTick = 0;
		LastReplayLength = 0;
		for (int i = 0; i < Capacity; i++)
		{
			_inputs[i] = default;
			_states[i] = default;
		}
	}
}
