using System.Collections.Generic;
using Gdpyr.Sim;
using Godot;
using Xunit;

namespace Gdpyr.Tests;

public class PredictionLedgerTests
{
	private static CharacterState State(uint tick, Vector3 position, Vector3 velocity = default) => new()
	{
		Tick = tick,
		Position = position,
		Velocity = velocity,
	};

	private static PlayerSnapshot Snapshot(uint ack, Vector3 position, Vector3 velocity = default) => new()
	{
		PeerId = 2,
		LastInputTick = ack,
		Position = position,
		Velocity = velocity,
	};

	private static PredictionLedger Recorded(uint from, uint to, Vector3 position)
	{
		var ledger = new PredictionLedger();
		for (uint tick = from; tick <= to; tick++)
		{
			ledger.Record(tick, InputFrame.Create(tick, 0f, -1f, 0f, 0f, InputButtons.None), State(tick, position));
		}
		return ledger;
	}

	[Fact]
	public void MatchingPrediction_NeedsNoCorrection()
	{
		PredictionLedger ledger = Recorded(1, 20, Vector3.Zero);

		ReplayDecision decision = ledger.Decide(Snapshot(10, Vector3.Zero));

		Assert.Equal(ReplayAction.InSync, decision.Action);
		Assert.Equal(0, ledger.Corrections);
	}

	[Fact]
	public void SubCentimetreError_IsLeftAlone()
	{
		// Correcting inside the quantization noise floor would replay on every single
		// snapshot and never converge.
		PredictionLedger ledger = Recorded(1, 20, Vector3.Zero);

		ReplayDecision decision = ledger.Decide(Snapshot(10, new Vector3(0.005f, 0f, 0f)));

		Assert.Equal(ReplayAction.InSync, decision.Action);
		Assert.InRange(decision.PositionError, 0.004f, 0.006f);
	}

	[Fact]
	public void PositionError_TriggersAReplayOfTheUnacknowledgedTicks()
	{
		PredictionLedger ledger = Recorded(1, 20, Vector3.Zero);

		ReplayDecision decision = ledger.Decide(Snapshot(10, new Vector3(0.5f, 0f, 0f)));

		Assert.Equal(ReplayAction.Replay, decision.Action);
		Assert.Equal(11u, decision.FromTick);
		Assert.Equal(20u, decision.ToTick);
		Assert.Equal(10, decision.ReplayLength);
		Assert.Equal(1, ledger.Corrections);
	}

	[Fact]
	public void VelocityError_TriggersAReplayEvenWhenThePositionAgrees()
	{
		// Restoring position without velocity is the classic reconciliation bug: the
		// next tick diverges again from a state that looked correct.
		PredictionLedger ledger = Recorded(1, 20, Vector3.Zero);

		ReplayDecision decision = ledger.Decide(Snapshot(10, Vector3.Zero, new Vector3(0f, -5f, 0f)));

		Assert.Equal(ReplayAction.Replay, decision.Action);
	}

	[Fact]
	public void RepeatedAcknowledgement_IsIgnored()
	{
		// The server repeats the last input when a packet is lost, so the same ack can
		// arrive several times; re-applying it would replay for no reason.
		PredictionLedger ledger = Recorded(1, 20, Vector3.Zero);
		Assert.Equal(ReplayAction.InSync, ledger.Decide(Snapshot(10, Vector3.Zero)).Action);

		Assert.Equal(ReplayAction.Ignore, ledger.Decide(Snapshot(10, new Vector3(5f, 0f, 0f))).Action);
	}

	[Fact]
	public void ReorderedSnapshot_IsIgnored()
	{
		PredictionLedger ledger = Recorded(1, 20, Vector3.Zero);
		ledger.Decide(Snapshot(12, Vector3.Zero));

		Assert.Equal(ReplayAction.Ignore, ledger.Decide(Snapshot(11, new Vector3(5f, 0f, 0f))).Action);
	}

	[Fact]
	public void UnacknowledgedServer_Snaps()
	{
		// Before the server has consumed any input there is nothing to compare, so the
		// only honest thing to do is take its state.
		PredictionLedger ledger = Recorded(1, 20, Vector3.Zero);

		Assert.Equal(ReplayAction.Snap, ledger.Decide(Snapshot(0, new Vector3(3f, 0f, 0f))).Action);
	}

	[Fact]
	public void AcknowledgementOutsideTheBuffer_Snaps()
	{
		var ledger = new PredictionLedger();
		ledger.Record(1, InputFrame.Create(1, 0f, 0f, 0f, 0f, InputButtons.None), State(1, Vector3.Zero));

		// The server is acknowledging a tick this client never predicted.
		Assert.Equal(ReplayAction.Snap, ledger.Decide(Snapshot(999, Vector3.Zero)).Action);
	}

	[Fact]
	public void Reset_ForgetsTheOldCharacter()
	{
		PredictionLedger ledger = Recorded(1, 20, Vector3.Zero);
		ledger.Decide(Snapshot(10, Vector3.Zero));

		ledger.Reset();

		Assert.Equal(0u, ledger.LastAckTick);
		Assert.Equal(ReplayAction.Snap, ledger.Decide(Snapshot(10, Vector3.Zero)).Action);
	}

	[Fact]
	public void RecordedInputs_AreReadableForReplay()
	{
		PredictionLedger ledger = Recorded(1, 20, Vector3.Zero);

		Assert.True(ledger.TryGetInput(15, out InputFrame frame));
		Assert.Equal(15u, frame.Tick);
		Assert.False(ledger.TryGetInput(21, out _));
	}

	/// <summary>
	/// A collisionless stand-in for the character: the same pure velocity step the
	/// real one runs, integrated directly instead of through <c>MoveAndSlide</c>.
	/// Identical on both sides, which is the property reconciliation depends on.
	/// </summary>
	private sealed class FakeCharacter
	{
		private static readonly MoveParams Walk = new(speed: 5f, acceleration: 0.1f, deceleration: 0.25f);

		public Vector3 Position;
		public Vector3 Velocity;
		public float Yaw;

		public void Step(in InputFrame frame)
		{
			Yaw = frame.YawRadians;
			Vector3 velocity = Movement.ApplyGravity(Velocity, 9.8f, SimConfig.TickDelta);
			velocity = Movement.ApplyMove(velocity, frame.MoveAxes, Yaw, Walk);
			if (Position.Y <= 0f && velocity.Y < 0f)
			{
				velocity.Y = 0f; // a floor, without a physics engine
			}

			Velocity = velocity;
			Position += velocity * SimConfig.TickDelta;
			if (Position.Y < 0f)
			{
				Position = new Vector3(Position.X, 0f, Position.Z);
			}
		}

		public CharacterState Capture(uint tick) => new()
		{
			Tick = tick,
			Position = Position,
			Velocity = Velocity,
			Yaw = Yaw,
		};

		public void Restore(in CharacterState state)
		{
			Position = state.Position;
			Velocity = state.Velocity;
			Yaw = state.Yaw;
		}
	}

	[Fact]
	public void ReplayConvergesOnTheServerTrajectoryAfterADivergence()
	{
		// The whole of M1 in one test: a client predicting ahead of a server it only
		// hears from a round trip later, knocked off course mid-run, has to end up
		// exactly where the server's own simulation of the same inputs did.
		const uint roundTripTicks = 12;
		const uint divergenceTick = 40;
		const uint lastTick = 120;

		var ledger = new PredictionLedger();
		var server = new FakeCharacter { Position = new Vector3(0f, 2f, 0f) };
		var client = new FakeCharacter { Position = new Vector3(0f, 2f, 0f) };
		var serverStates = new Dictionary<uint, CharacterState>();

		int replays = 0;
		int replaysAfterCorrection = 0;

		for (uint tick = 1; tick <= lastTick; tick++)
		{
			InputFrame frame = InputFrame.Create(tick, 0.5f, -1f, 0.3f, 0f, InputButtons.None);

			server.Step(frame);
			serverStates[tick] = server.Capture(tick);

			client.Step(frame);
			if (tick == divergenceTick)
			{
				// However it happened — a mispredicted collision, a lost correction —
				// the client's prediction for this tick is half a metre out.
				client.Position += new Vector3(0.5f, 0f, 0f);
			}
			ledger.Record(tick, frame, client.Capture(tick));

			if (tick <= roundTripTicks)
			{
				continue;
			}

			uint ack = tick - roundTripTicks;
			CharacterState authoritative = serverStates[ack];
			var snapshot = new PlayerSnapshot
			{
				PeerId = 2,
				LastInputTick = ack,
				Position = authoritative.Position,
				Velocity = authoritative.Velocity,
				Yaw = authoritative.Yaw,
			};

			ReplayDecision decision = ledger.Decide(snapshot);
			if (decision.Action != ReplayAction.Replay)
			{
				continue;
			}

			replays++;
			if (ack > divergenceTick)
			{
				// The correction for the divergence lands on its own tick; anything
				// later means the replay did not actually put the client back on course.
				replaysAfterCorrection++;
			}

			client.Restore(authoritative);
			for (uint replayTick = decision.FromTick; replayTick <= decision.ToTick; replayTick++)
			{
				Assert.True(ledger.TryGetInput(replayTick, out InputFrame replayed));
				client.Step(replayed);
				ledger.UpdateState(replayTick, client.Capture(replayTick));
			}
		}

		// One correction, when the snapshot covering the divergence arrived.
		Assert.Equal(1, replays);
		Assert.Equal(0, replaysAfterCorrection);

		// And the client is back on the server's trajectory, not merely close to it.
		Assert.True((client.Position - serverStates[lastTick].Position).Length() < 1e-4f);
	}

	[Fact]
	public void SteadyStatePrediction_NeverCorrects()
	{
		// With no divergence at all, a client one round trip ahead must never see a
		// correction: any drift here would be a bug in the scheme, not in the link.
		const uint roundTripTicks = 12;

		var ledger = new PredictionLedger();
		var server = new FakeCharacter { Position = new Vector3(0f, 2f, 0f) };
		var client = new FakeCharacter { Position = new Vector3(0f, 2f, 0f) };
		var serverStates = new Dictionary<uint, CharacterState>();

		for (uint tick = 1; tick <= 300; tick++)
		{
			// A turning, strafing run: the worst case for accumulated float error.
			float yaw = tick * 0.01f;
			InputFrame frame = InputFrame.Create(tick, Mathf.Sin(tick * 0.05f), -1f, yaw, 0f, InputButtons.None);

			server.Step(frame);
			serverStates[tick] = server.Capture(tick);

			client.Step(frame);
			ledger.Record(tick, frame, client.Capture(tick));

			if (tick <= roundTripTicks)
			{
				continue;
			}

			CharacterState authoritative = serverStates[tick - roundTripTicks];
			ReplayDecision decision = ledger.Decide(new PlayerSnapshot
			{
				PeerId = 2,
				LastInputTick = tick - roundTripTicks,
				Position = authoritative.Position,
				Velocity = authoritative.Velocity,
				Yaw = authoritative.Yaw,
			});

			Assert.Equal(ReplayAction.InSync, decision.Action);
		}

		Assert.Equal(0, ledger.Corrections);
	}
}
