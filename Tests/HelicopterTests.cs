using System;
using Gdpyr.Sim;
using Godot;
using Xunit;

namespace Gdpyr.Tests;

/// <summary>
/// How the helicopter flies (docs/GROUND_VEHICLES.md §5.2): the rotor and its lift, the
/// climb, the hold and the ceiling, the turn, the field, the tilt, coming down with
/// nobody at the controls, and standing still on the ground. Flat ground at y = 0 stands
/// in for the engine's ray down.
/// </summary>
public class HelicopterTests
{
	private const float Dt = SimConfig.TickDelta;

	private static readonly HelicopterTraits Traits = HelicopterTraits.Default;

	private static void Tick(ref HelicopterState state, in InputFrame frame, bool piloted = true, float ground = 0f)
	{
		Vector3 velocity = HelicopterSim.Step(ref state, Traits, frame, piloted, state.Position.Y - ground, Dt);
		HelicopterSim.AfterMove(ref state, state.Position + (velocity * Dt), velocity);
	}

	private static InputFrame Fly(float forward = 0f, float right = 0f, bool climb = false, bool descend = false,
		float yaw = 0f, uint tick = 0) =>
		InputFrame.Create(tick, right, -forward, yaw, 0f,
			(climb ? InputButtons.Jump : InputButtons.None) | (descend ? InputButtons.Crouch : InputButtons.None));

	private static HelicopterState OnThePad(float rotor = 1f)
	{
		HelicopterState state = HelicopterState.At(new Vector3(0f, 0f, 0f), 0f);
		state.Rotor = rotor;
		return state;
	}

	/// <summary>Airborne and hovering at <paramref name="height"/>, its rotor at full speed.</summary>
	private static HelicopterState Hovering(float height)
	{
		HelicopterState state = OnThePad();
		int guard = 0;
		while (state.Position.Y < height - 0.01f && guard++ < 6000)
		{
			float left = height - state.Position.Y;
			Tick(ref state, left > 2f ? Fly(climb: true) : Fly());
			if (left <= 2f && state.Velocity.Y <= 0.01f)
			{
				break;
			}
		}

		for (int i = 0; i < 120; i++)
		{
			Tick(ref state, Fly());
		}

		Assert.False(state.Grounded);
		return state;
	}

	private static float Horizontal(in HelicopterState s) => new Vector2(s.Velocity.X, s.Velocity.Z).Length();

	// ---- the rotor ------------------------------------------------------------------------

	[Fact]
	public void TheRotor_SpinsUpIn2AndAHalfSeconds_WithAPilot_AndDownIn4Without()
	{
		float rotor = 0f;
		int up = 0;
		while (rotor < 1f && up < 1000)
		{
			rotor = HelicopterSim.SpinRotor(Traits, rotor, piloted: true, Dt);
			up++;
		}

		int down = 0;
		while (rotor > 0f && down < 1000)
		{
			rotor = HelicopterSim.SpinRotor(Traits, rotor, piloted: false, Dt);
			down++;
		}

		Assert.InRange(up * Dt, 2.5f - Dt, 2.5f + Dt);
		Assert.InRange(down * Dt, 4f - Dt, 4f + Dt);
	}

	[Fact]
	public void BelowSixtyPercent_ThereIsNoLift()
	{
		HelicopterState slow = OnThePad(rotor: 0.5f);
		for (int i = 0; i < 5; i++)
		{
			Tick(ref slow, Fly(climb: true));
		}

		Assert.True(slow.Grounded);
		Assert.Equal(0f, slow.Position.Y);

		Assert.False(HelicopterSim.HasLift(Traits, 0.59f, piloted: true));
		Assert.True(HelicopterSim.HasLift(Traits, 0.6f, piloted: true));
		Assert.False(HelicopterSim.HasLift(Traits, 1f, piloted: false));
	}

	[Fact]
	public void FromStill_ItLeavesTheGroundOnceTheRotorReachesSixtyPercent()
	{
		HelicopterState state = OnThePad(rotor: 0f);
		int ticks = 0;
		while (state.Grounded && ticks < 600)
		{
			Tick(ref state, Fly(climb: true, tick: (uint)ticks));
			ticks++;
		}

		Assert.InRange(ticks * Dt, (0.6f * 2.5f) - (2f * Dt), (0.6f * 2.5f) + (2f * Dt));
	}

	// ---- climbing, holding, the ceiling ---------------------------------------------------

	[Fact]
	public void ItClimbsAt5_ComesDownAt6_AndWithNeitherHoldsItsHeight()
	{
		HelicopterState state = Hovering(10f);

		for (int i = 0; i < 60; i++)
		{
			Tick(ref state, Fly(climb: true));
		}

		Assert.Equal(Traits.ClimbSpeed, state.Velocity.Y, 3);

		// From +5 to −6 at 8 m/s² takes 1.4 s.
		for (int i = 0; i < 90; i++)
		{
			Tick(ref state, Fly(descend: true));
		}

		Assert.Equal(-Traits.DescentSpeed, state.Velocity.Y, 3);

		for (int i = 0; i < 60; i++)
		{
			Tick(ref state, Fly());
		}

		float held = state.Position.Y;
		for (int i = 0; i < 300; i++)
		{
			Tick(ref state, Fly());
		}

		Assert.Equal(held, state.Position.Y, 3);
		Assert.Equal(0f, state.Velocity.Y, 4);
	}

	[Fact]
	public void ItClimbsNoHigherThan25MetresAboveTheGroundUnderIt()
	{
		HelicopterState state = OnThePad();
		for (int i = 0; i < 60 * 15; i++)
		{
			Tick(ref state, Fly(climb: true, tick: (uint)i));
			Assert.True(state.Position.Y <= Traits.CeilingMeters + 1e-3f);
		}

		Assert.Equal(Traits.CeilingMeters, state.Position.Y, 2);

		// Over a 6.5 m roof the ceiling is 31.5 m; off the roof's edge it comes back down to 25.
		for (int i = 0; i < 60 * 3; i++)
		{
			Tick(ref state, Fly(climb: true), ground: 6.5f);
		}

		Assert.Equal(Traits.CeilingMeters + 6.5f, state.Position.Y, 2);

		for (int i = 0; i < 60 * 3; i++)
		{
			Tick(ref state, Fly(climb: true));
			Assert.True(state.Velocity.Y >= -Traits.DescentSpeed - 1e-3f);
		}

		Assert.Equal(Traits.CeilingMeters, state.Position.Y, 2);
	}

	[Fact]
	public void CommandedDown_ItLandsOnTheGround_AndStops()
	{
		HelicopterState state = Hovering(12f);
		for (int i = 0; i < 60 * 5; i++)
		{
			Tick(ref state, Fly(descend: true, forward: 1f));
			Assert.True(state.Position.Y >= -1e-4f);
		}

		Assert.True(state.Grounded);
		Assert.Equal(0f, state.Position.Y, 4);
		Assert.Equal(0f, Horizontal(state));
	}

	// ---- moving across the ground -----------------------------------------------------------

	[Fact]
	public void ItFlies22MetresASecondForward_AndTenSidewaysOrBack_At5MetresASecondSquared()
	{
		HelicopterState state = Hovering(10f);
		state.Position = new Vector3(0f, state.Position.Y, 100f);
		for (int i = 0; i < 60; i++)
		{
			Tick(ref state, Fly(forward: 1f));
		}

		Assert.Equal(Traits.Acceleration, Horizontal(state), 2);

		for (int i = 0; i < 60 * 4; i++)
		{
			Tick(ref state, Fly(forward: 1f));
		}

		Assert.Equal(Traits.MaxForwardSpeed, -state.Velocity.Z, 3);

		HelicopterState back = Hovering(10f);
		HelicopterState side = Hovering(10f);
		for (int i = 0; i < 60 * 4; i++)
		{
			Tick(ref back, Fly(forward: -1f));
			Tick(ref side, Fly(right: 1f));
		}

		Assert.Equal(Traits.MaxSideSpeed, back.Velocity.Z, 3);
		Assert.Equal(Traits.MaxSideSpeed, side.Velocity.X, 3);
	}

	[Fact]
	public void LeftAlone_ItSlowsToAHover()
	{
		HelicopterState state = Hovering(10f);
		for (int i = 0; i < 120; i++)
		{
			Tick(ref state, Fly(forward: 1f));
		}

		for (int i = 0; i < 60 * 3; i++)
		{
			Tick(ref state, Fly());
		}

		Assert.Equal(0f, Horizontal(state), 4);
	}

	[Fact]
	public void ItIsKeptInsideTheField()
	{
		HelicopterState state = Hovering(10f);
		for (int i = 0; i < 60 * 20; i++)
		{
			Tick(ref state, Fly(forward: 1f, right: 1f, yaw: MathF.PI / 4f));
			Assert.True(Cargo.InField(state.Position), $"tick {i}: {state.Position}");
		}

		Assert.Equal(CrewField.HalfExtentMeters, MathF.Max(MathF.Abs(state.Position.X), MathF.Abs(state.Position.Z)), 2);
	}

	// ---- turning ------------------------------------------------------------------------------

	[Fact]
	public void TheHeading_TurnsTowardsTheLookAt90DegreesASecond_TheShortWayRound()
	{
		HelicopterState state = Hovering(10f);
		state.Yaw = 3f;

		// From 3 rad to −3 rad is 0.28 rad through π, not 6 the other way.
		Tick(ref state, Fly(yaw: -3f));
		Assert.Equal(DroneSim.WrapAngle(3f + (Traits.TurnRate * Dt)), state.Yaw, 4);
		Assert.Equal(Traits.TurnRate, state.YawRate, 3);

		for (int i = 0; i < 60; i++)
		{
			Tick(ref state, Fly(yaw: -3f));
		}

		Assert.Equal(-3f, state.Yaw, 3);
		Assert.Equal(0f, state.YawRate, 3);
	}

	// ---- nobody at the controls ---------------------------------------------------------------

	[Fact]
	public void AnUnpilotedHelicopterAt20Metres_Lands_AndNeverComesDownFasterThan4MetresASecond()
	{
		HelicopterState state = Hovering(20f);
		for (int i = 0; i < 60; i++)
		{
			Tick(ref state, Fly(forward: 1f));
		}

		float horizontal = Horizontal(state);
		Assert.True(horizontal > 3f);

		int ticks = 0;
		while (!state.Grounded && ticks < 60 * 30)
		{
			Tick(ref state, InputFrame.Neutral((uint)ticks), piloted: false);
			Assert.True(state.Velocity.Y >= -Traits.UnpilotedDescentSpeed - 1e-4f, $"tick {ticks}: {state.Velocity.Y}");
			Assert.True(Horizontal(state) <= horizontal + 1e-4f);
			horizontal = Horizontal(state);
			ticks++;
		}

		Assert.True(state.Grounded);
		Assert.InRange(state.Position.Y, 0f, HelicopterSim.GroundedMeters);
		Assert.InRange(ticks * Dt, 20f / Traits.UnpilotedDescentSpeed, (20f / Traits.UnpilotedDescentSpeed) + 2f);

		// The next tick settles the last centimetres, no faster than it came down.
		Tick(ref state, InputFrame.Neutral(0), piloted: false);
		Assert.Equal(0f, state.Position.Y, 4);
		Assert.True(state.Grounded);
	}

	[Fact]
	public void Grounded_ItNeverSlides()
	{
		var random = new Random(3);
		HelicopterState state = OnThePad();
		Vector3 at = state.Position;
		for (int i = 0; i < 60 * 20; i++)
		{
			InputFrame frame = Fly((float)random.NextDouble() * 2f - 1f, (float)random.NextDouble() * 2f - 1f,
				climb: false, descend: random.Next(2) == 0, yaw: (float)random.NextDouble() * 6f - 3f, tick: (uint)i);
			Tick(ref state, frame, piloted: random.Next(3) != 0);

			Assert.True(state.Grounded);
			Assert.Equal(at, state.Position);
			Assert.Equal(0f, Horizontal(state));
		}
	}

	// ---- tilt -----------------------------------------------------------------------------------

	[Fact]
	public void ItsTilt_IsInProportionToTheAcceleration_NoseDownForward_BankedIntoAPush()
	{
		HelicopterSim.TiltFor(Traits, Traits.Acceleration, 0f, out float pitch, out float roll);
		Assert.Equal(-Traits.MaxTilt, pitch, 5);
		Assert.Equal(0f, roll);

		HelicopterSim.TiltFor(Traits, Traits.Acceleration * 0.5f, Traits.Acceleration * 0.25f, out pitch, out roll);
		Assert.Equal(-Traits.MaxTilt * 0.5f, pitch, 5);
		Assert.Equal(-Traits.MaxTilt * 0.25f, roll, 5);

		HelicopterSim.TiltFor(Traits, -100f, 100f, out pitch, out roll);
		Assert.Equal(Traits.MaxTilt, pitch, 5);
		Assert.Equal(-Traits.MaxTilt, roll, 5);

		// In the air, accelerating forward puts the nose down; pushing right banks right.
		HelicopterState state = Hovering(10f);
		for (int i = 0; i < 30; i++)
		{
			Tick(ref state, Fly(forward: 1f, right: 1f));
		}

		Assert.True(state.Pitch < Mathf.DegToRad(-10f));
		Assert.True(state.Roll < Mathf.DegToRad(-10f));
	}

	[Fact]
	public void ItsTilt_StaysWithin20Degrees_AndItsAltitudeWithinTheWire_OverRandomFlying()
	{
		var random = new Random(9);
		HelicopterState state = OnThePad();
		float max = Mathf.DegToRad(20f) + 1e-5f;
		for (int i = 0; i < 60 * 120; i++)
		{
			InputFrame frame = Fly((float)random.NextDouble() * 2f - 1f, (float)random.NextDouble() * 2f - 1f,
				random.Next(2) == 0, random.Next(3) == 0, (float)random.NextDouble() * 6f - 3f, (uint)i);
			Tick(ref state, frame, piloted: random.Next(50) != 0);

			Assert.InRange(state.Pitch, -max, max);
			Assert.InRange(state.Roll, -max, max);
			Assert.InRange(state.Position.Y, -1e-4f, Traits.CeilingMeters + 1e-3f);
			Assert.True(MathF.Abs(state.Position.X) <= SimConfig.MaxPositionMeters);
			Assert.True(MathF.Abs(state.Position.Z) <= SimConfig.MaxPositionMeters);
		}
	}

	[Fact]
	public void TheSameInputs_GiveBitIdenticalStates()
	{
		string Run()
		{
			var random = new Random(17);
			HelicopterState state = OnThePad(rotor: 0f);
			for (int i = 0; i < 600; i++)
			{
				Tick(ref state, Fly((float)random.NextDouble() * 2f - 1f, (float)random.NextDouble() * 2f - 1f,
					random.Next(2) == 0, random.Next(4) == 0, (float)random.NextDouble() * 6f - 3f, (uint)i),
					piloted: random.Next(20) != 0);
			}

			return string.Join(",", BitConverter.SingleToInt32Bits(state.Position.X),
				BitConverter.SingleToInt32Bits(state.Position.Y), BitConverter.SingleToInt32Bits(state.Position.Z),
				BitConverter.SingleToInt32Bits(state.Velocity.X), BitConverter.SingleToInt32Bits(state.Velocity.Y),
				BitConverter.SingleToInt32Bits(state.Velocity.Z), BitConverter.SingleToInt32Bits(state.Yaw),
				BitConverter.SingleToInt32Bits(state.Pitch), BitConverter.SingleToInt32Bits(state.Roll),
				BitConverter.SingleToInt32Bits(state.Rotor), state.Grounded);
		}

		string first = Run();
		for (int run = 1; run < 1000; run++)
		{
			Assert.Equal(first, Run());
		}
	}
}
