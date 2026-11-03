using Gdpyr.Sim;
using Godot;
using Xunit;

namespace Gdpyr.Tests;

/// <summary>
/// The driver's record of its vehicle's predicted states, and what to do with each
/// record the server sends (docs/GROUND_VEHICLES.md §5.7) — the facts
/// <see cref="PredictionLedgerTests"/> holds the character's ledger to, at a truck's
/// thresholds and with its attitude in them.
/// </summary>
public class CrewLedgerTests
{
	private static CrewSample Sample(uint tick, Vector3 position = default, Vector3 velocity = default,
		float yaw = 0f, float pitch = 0f, float roll = 0f) => new(tick, position, velocity, yaw, pitch, roll);

	/// <summary>Inputs and vehicle states recorded for every tick from <paramref name="from"/> to <paramref name="to"/>.</summary>
	private static CrewLedger Recorded(uint from, uint to, Vector3 position = default)
	{
		var inputs = new PredictionLedger();
		var ledger = new CrewLedger(inputs);
		for (uint tick = from; tick <= to; tick++)
		{
			inputs.RecordInput(tick, InputFrame.Create(tick, 0f, -1f, 0f, 0f, InputButtons.None));
			ledger.Record(Sample(tick, position));
		}

		return ledger;
	}

	[Fact]
	public void AMatchingPrediction_NeedsNoCorrection()
	{
		CrewLedger ledger = Recorded(1, 20);

		ReplayDecision decision = ledger.Decide(10, Sample(10));

		Assert.Equal(ReplayAction.InSync, decision.Action);
		Assert.Equal(0, ledger.Corrections);
		Assert.Equal(0, ledger.LastReplayLength);
	}

	[Fact]
	public void WithinThreeCentimetres_IsLeftAlone_AndPastItReplaysTheUnacknowledgedTicks()
	{
		CrewLedger ledger = Recorded(1, 20);
		Assert.Equal(ReplayAction.InSync, ledger.Decide(10, Sample(10, new Vector3(0.029f, 0f, 0f))).Action);

		ReplayDecision decision = ledger.Decide(11, Sample(11, new Vector3(0.031f, 0f, 0f)));
		Assert.Equal(ReplayAction.Replay, decision.Action);
		Assert.Equal(12u, decision.FromTick);
		Assert.Equal(20u, decision.ToTick);
		Assert.Equal(9, decision.ReplayLength);
		Assert.Equal(9, ledger.LastReplayLength);
		Assert.Equal(1, ledger.Corrections);
	}

	[Fact]
	public void AVelocityErrorPastHalfAMetreASecond_Replays()
	{
		CrewLedger ledger = Recorded(1, 20);
		Assert.Equal(ReplayAction.InSync, ledger.Decide(10, Sample(10, velocity: new Vector3(0f, 0.49f, 0f))).Action);
		Assert.Equal(ReplayAction.Replay, ledger.Decide(11, Sample(11, velocity: new Vector3(0f, 0.51f, 0f))).Action);
	}

	[Theory]
	[InlineData(0)]
	[InlineData(1)]
	[InlineData(2)]
	public void AnAttitudeErrorPastHalfADegree_Replays_InYawPitchOrRoll(int axis)
	{
		CrewSample Off(uint tick, float degrees) => Sample(tick,
			yaw: axis == 0 ? Mathf.DegToRad(degrees) : 0f,
			pitch: axis == 1 ? Mathf.DegToRad(degrees) : 0f,
			roll: axis == 2 ? Mathf.DegToRad(degrees) : 0f);

		CrewLedger ledger = Recorded(1, 20);
		Assert.Equal(ReplayAction.InSync, ledger.Decide(10, Off(10, 0.49f)).Action);
		Assert.Equal(ReplayAction.Replay, ledger.Decide(11, Off(11, 0.51f)).Action);
		Assert.InRange(ledger.LastAttitudeError, Mathf.DegToRad(0.5f), Mathf.DegToRad(0.52f));
	}

	[Fact]
	public void AYawEitherSideOfPi_IsTheSameHeading()
	{
		var inputs = new PredictionLedger();
		var ledger = new CrewLedger(inputs);
		inputs.RecordInput(10, InputFrame.Neutral(10));
		ledger.Record(Sample(10, yaw: Mathf.Pi - 0.001f));

		Assert.Equal(ReplayAction.InSync, ledger.Decide(10, Sample(10, yaw: -Mathf.Pi + 0.001f)).Action);
	}

	[Fact]
	public void AnOlderOrRepeatedAcknowledgement_IsIgnored()
	{
		CrewLedger ledger = Recorded(1, 20);
		Assert.Equal(ReplayAction.InSync, ledger.Decide(10, Sample(10)).Action);

		Assert.Equal(ReplayAction.Ignore, ledger.Decide(10, Sample(10, new Vector3(5f, 0f, 0f))).Action);
		Assert.Equal(ReplayAction.Ignore, ledger.Decide(8, Sample(8, new Vector3(5f, 0f, 0f))).Action);
		Assert.Equal(0, ledger.Corrections);
	}

	[Fact]
	public void TheFirstRecordAfterGettingIn_SnapsAndReplaysTheRecordedInputs()
	{
		// The character's ledger has the frames; the vehicle's has no prediction yet.
		var inputs = new PredictionLedger();
		for (uint tick = 1; tick <= 30; tick++)
		{
			inputs.RecordInput(tick, InputFrame.Neutral(tick));
		}

		var ledger = new CrewLedger(inputs);
		ReplayDecision decision = ledger.Decide(22, Sample(22, new Vector3(4f, 0f, 0f)));

		Assert.Equal(ReplayAction.Snap, decision.Action);
		Assert.Equal(23u, decision.FromTick);
		Assert.Equal(30u, decision.ToTick);
		Assert.Equal(8, ledger.LastReplayLength);
		Assert.Equal(0, ledger.Corrections);
	}

	[Fact]
	public void NobodysInputConsumed_IsASnapWithNothingToReplay()
	{
		CrewLedger ledger = Recorded(1, 20);
		ReplayDecision decision = ledger.Decide(0, Sample(0));

		Assert.Equal(ReplayAction.Snap, decision.Action);
		Assert.True(decision.FromTick > decision.ToTick);
		Assert.Equal(0, ledger.LastReplayLength);
	}

	[Fact]
	public void AnAcknowledgementTheRingHasOutrun_Snaps()
	{
		var inputs = new PredictionLedger();
		var ledger = new CrewLedger(inputs);
		ledger.Record(Sample(10));
		for (uint tick = 1; tick <= 10 + SimConfig.InputBufferTicks + 5; tick++)
		{
			inputs.RecordInput(tick, InputFrame.Neutral(tick));
		}

		ReplayDecision decision = ledger.Decide(10, Sample(10, new Vector3(3f, 0f, 0f)));

		Assert.Equal(ReplayAction.Snap, decision.Action);
		Assert.Equal(0, ledger.LastReplayLength);
	}

	[Fact]
	public void AReplayedState_OverwritesThePrediction()
	{
		CrewLedger ledger = Recorded(1, 20);
		ledger.UpdateState(Sample(15, new Vector3(1f, 2f, 3f)));

		Assert.True(ledger.TryGetState(15, out CrewSample state));
		Assert.Equal(new Vector3(1f, 2f, 3f), state.Position);
		Assert.False(ledger.TryGetState(21, out _));
	}

	[Fact]
	public void AResetForgetsEveryPrediction_ButNotTheInputs()
	{
		CrewLedger ledger = Recorded(1, 20);
		ledger.Decide(10, Sample(10, new Vector3(1f, 0f, 0f)));
		ledger.Reset();

		Assert.False(ledger.TryGetState(15, out _));
		Assert.Equal(0u, ledger.LastAckTick);
		Assert.True(ledger.Inputs.TryGetInput(15, out _));

		// After a reset the first record snaps, as the first after getting in does.
		Assert.Equal(ReplayAction.Snap, ledger.Decide(16, Sample(16)).Action);
	}
}
