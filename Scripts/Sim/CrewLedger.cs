using System;
using Godot;

namespace Gdpyr.Sim;

/// <summary>
/// What the driver's prediction of a crew vehicle is compared on (docs/GROUND_VEHICLES.md
/// §5.7): where it is, how fast it is moving, and how it is turned. A record of either
/// kind reduces to one of these, and so does a predicted state.
/// </summary>
public struct CrewSample
{
	public uint Tick;

	public Vector3 Position;

	public Vector3 Velocity;

	public float Yaw;

	public float Pitch;

	public float Roll;

	public CrewSample(uint tick, Vector3 position, Vector3 velocity, float yaw, float pitch, float roll)
	{
		Tick = tick;
		Position = position;
		Velocity = velocity;
		Yaw = yaw;
		Pitch = pitch;
		Roll = roll;
	}

	public static CrewSample Of(uint tick, in FourByFourState state) =>
		new(tick, state.Position, state.Velocity, state.Yaw, state.Pitch, state.Roll);

	public static CrewSample Of(uint tick, in HelicopterState state) =>
		new(tick, state.Position, state.Velocity, state.Yaw, state.Pitch, state.Roll);
}

/// <summary>
/// The driver's record of what it predicted for its vehicle, and the decision of what to
/// do when the server disagrees (docs/GROUND_VEHICLES.md §5.7, GV-D4) — the vehicle's
/// half of <see cref="PredictionLedger"/>, whose input ring it shares: a driver's frames
/// are recorded once, and both the character's and the vehicle's replay read them.
///
/// <list type="bullet">
/// <item>An older or repeated acknowledgement is ignored.</item>
/// <item>No prediction on record for the acknowledged tick — the first record after
/// getting in — snaps to the server's state and replays the recorded inputs after it.</item>
/// <item>Within <see cref="PositionThresholdMeters"/>, <see cref="VelocityThreshold"/>
/// and <see cref="AttitudeThresholdRadians"/>, the prediction held.</item>
/// <item>Otherwise, a replay.</item>
/// </list>
///
/// The thresholds are a truck's, not a body's: 3 cm rather than 1, because a sprung
/// chassis carries more state than a capsule and a correction is a hull's worth of mesh
/// moving; and the attitude is in them, because a bounce that has drifted is a
/// misprediction a position check would miss (§14).
/// </summary>
public sealed class CrewLedger
{
	public const float PositionThresholdMeters = 0.03f;

	public const float VelocityThreshold = 0.5f;

	public static readonly float AttitudeThresholdRadians = Mathf.DegToRad(0.5f);

	private const int Capacity = SimConfig.InputBufferTicks;
	private const int Mask = Capacity - 1;

	private readonly PredictionLedger _inputs;
	private readonly CrewSample[] _states = new CrewSample[Capacity];
	private bool _recording;
	private bool _acknowledged;

	/// <param name="inputs">The character's ledger, whose input frames the vehicle's replay reads.</param>
	public CrewLedger(PredictionLedger inputs)
	{
		_inputs = inputs ?? throw new ArgumentNullException(nameof(inputs));
	}

	/// <summary>The input ring both ledgers share.</summary>
	public PredictionLedger Inputs => _inputs;

	/// <summary>The newest acknowledgement taken.</summary>
	public uint LastAckTick { get; private set; }

	/// <summary>The newest tick a vehicle state was predicted for.</summary>
	public uint LastRecordedTick { get; private set; }

	/// <summary>Corrections — replays, not snaps — since the last <see cref="Reset"/>.</summary>
	public int Corrections { get; private set; }

	/// <summary>Ticks the last decision asked to be re-run, a snap's included.</summary>
	public int LastReplayLength { get; private set; }

	/// <summary>The largest of the yaw, pitch and roll errors the last comparison found [rad].</summary>
	public float LastAttitudeError { get; private set; }

	/// <summary>Records the vehicle's predicted state after the tick its input frame was for.</summary>
	public void Record(in CrewSample state)
	{
		_states[state.Tick & Mask] = state;
		LastRecordedTick = state.Tick;
		_recording = true;
	}

	/// <summary>Overwrites a predicted state after it has been replayed.</summary>
	public void UpdateState(in CrewSample state)
	{
		if (_states[state.Tick & Mask].Tick == state.Tick || _inputs.TryGetInput(state.Tick, out _))
		{
			_states[state.Tick & Mask] = state;
		}
	}

	public bool TryGetState(uint tick, out CrewSample state)
	{
		state = _states[tick & Mask];
		return _recording && state.Tick == tick;
	}

	/// <summary>
	/// What to do with a vehicle record whose driver acknowledgement is
	/// <paramref name="ack"/> and whose state is <paramref name="server"/>. A replay — or a
	/// snap with inputs recorded after <paramref name="ack"/> — runs from <c>ack + 1</c>
	/// to the newest input the shared ring holds; a snap with nothing after it carries
	/// <c>FromTick &gt; ToTick</c>.
	/// </summary>
	public ReplayDecision Decide(uint ack, in CrewSample server)
	{
		if (_acknowledged && ack <= LastAckTick)
		{
			LastReplayLength = 0;
			return new ReplayDecision(ReplayAction.Ignore);
		}

		LastAttitudeError = 0f;
		if (ack == 0)
		{
			// Nobody's input consumed yet: there is nothing to compare and nothing to replay.
			LastReplayLength = 0;
			return new ReplayDecision(ReplayAction.Snap, 1, 0);
		}

		LastAckTick = ack;
		_acknowledged = true;

		uint newest = _inputs.LastRecordedTick;
		bool inRing = newest >= ack && newest - ack < Capacity;
		uint from = ack + 1;
		uint to = inRing ? newest : ack;

		if (!inRing || !_recording || _states[ack & Mask].Tick != ack)
		{
			// The first record after getting in, or one so far behind the inputs that the
			// ring no longer holds what it needs: take the server's state, and re-run
			// whatever inputs there are after it.
			LastReplayLength = to >= from ? (int)(to - from + 1) : 0;
			return new ReplayDecision(ReplayAction.Snap, from, to);
		}

		CrewSample predicted = _states[ack & Mask];
		float positionError = (predicted.Position - server.Position).Length();
		float velocityError = (predicted.Velocity - server.Velocity).Length();
		LastAttitudeError = MathF.Max(MathF.Abs(DroneSim.AngleBetween(predicted.Yaw, server.Yaw)),
			MathF.Max(MathF.Abs(predicted.Pitch - server.Pitch), MathF.Abs(predicted.Roll - server.Roll)));

		if (positionError <= PositionThresholdMeters && velocityError <= VelocityThreshold
			&& LastAttitudeError <= AttitudeThresholdRadians)
		{
			LastReplayLength = 0;
			return new ReplayDecision(ReplayAction.InSync, positionError: positionError, velocityError: velocityError);
		}

		Corrections++;
		LastReplayLength = to >= from ? (int)(to - from + 1) : 0;
		return new ReplayDecision(ReplayAction.Replay, from, to, positionError, velocityError);
	}

	/// <summary>Forgets every predicted state; the inputs are the character's ledger's to forget.</summary>
	public void Reset()
	{
		Array.Clear(_states);
		_recording = false;
		_acknowledged = false;
		LastAckTick = 0;
		LastRecordedTick = 0;
		LastReplayLength = 0;
		LastAttitudeError = 0f;
	}
}
