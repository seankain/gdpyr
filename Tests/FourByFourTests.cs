using System;
using Gdpyr.Sim;
using Godot;
using Xunit;

namespace Gdpyr.Tests;

/// <summary>
/// The 4x4's sprung chassis and its driving (docs/GROUND_VEHICLES.md §5.1): springy and
/// stable, the speeds and the brakes, the steering held to grip, the handbrake's slide,
/// and a step that is bounded and the same every time it is run.
///
/// The engine half is stood in for by flat ground at y = 0 and a body that goes where
/// its velocity takes it: each wheel's ray reports the hardpoint's height, which is what
/// the engine's would on the test map's floor.
/// </summary>
public class FourByFourTests
{
	private const float Dt = SimConfig.TickDelta;

	private static readonly FourByFourTraits Traits = FourByFourTraits.Default;

	/// <summary>What four rays straight down onto flat ground at <paramref name="ground"/> find.</summary>
	private static WheelContact[] Flat(in FourByFourState state, float ground = 0f)
	{
		var wheels = new WheelContact[FourByFourSim.Wheels];
		for (int i = 0; i < wheels.Length; i++)
		{
			float distance = FourByFourSim.Hardpoint(state, Traits, i).Y - ground;
			wheels[i] = distance >= 0f && distance <= Traits.RayLengthMeters ? WheelContact.At(distance) : WheelContact.Miss;
		}

		return wheels;
	}

	/// <summary>One tick on flat ground, the body moving freely.</summary>
	private static void Tick(ref FourByFourState state, in InputFrame frame)
	{
		WheelContact[] wheels = Flat(state);
		Vector3 velocity = FourByFourSim.Step(ref state, Traits, frame, wheels, Dt);
		FourByFourSim.AfterMove(ref state, state.Position + (velocity * Dt), velocity);
	}

	/// <summary>One tick with whatever contacts are given, no ground at all.</summary>
	private static void Tick(ref FourByFourState state, in InputFrame frame, ReadOnlySpan<WheelContact> wheels)
	{
		Vector3 velocity = FourByFourSim.Step(ref state, Traits, frame, wheels, Dt);
		FourByFourSim.AfterMove(ref state, state.Position + (velocity * Dt), velocity);
	}

	private static InputFrame Drive(float throttle, float steer = 0f, bool handbrake = false, uint tick = 0) =>
		InputFrame.Create(tick, steer, -throttle, 0f, 0f, handbrake ? InputButtons.Jump : InputButtons.None);

	private static FourByFourState AtRest(float above = 0f) =>
		FourByFourState.At(new Vector3(0f, Traits.RideHeightMeters + above, 0f), 0f);

	private static float Speed(in FourByFourState s) => s.Velocity.Dot(Aim.Direction(s.Yaw, 0f));

	private static float Sideways(in FourByFourState s)
	{
		Vector3 forward = Aim.Direction(s.Yaw, 0f);
		return s.Velocity.Dot(new Vector3(-forward.Z, 0f, forward.X));
	}

	/// <summary>
	/// Up to speed in a straight line, the bounce of getting there settled: from z = 100,
	/// heading −Z, so six seconds flat out ends 33 m short of the field's middle and
	/// nowhere near its edge.
	/// </summary>
	private static FourByFourState AtSpeed(float throttle, int ticks = 360)
	{
		FourByFourState state = AtRest();
		state.Position.Z = 100f;
		for (int i = 0; i < ticks; i++)
		{
			Tick(ref state, Drive(throttle));
		}

		return state;
	}

	// ---- the springs ----------------------------------------------------------------------

	[Fact]
	public void StandingStill_ItRestsWhereTheSpringsCarryIt_AndStaysThere()
	{
		FourByFourState state = AtRest();
		for (int i = 0; i < 600; i++)
		{
			Tick(ref state, InputFrame.Neutral((uint)i));
		}

		Assert.Equal(Traits.RideHeightMeters, state.Position.Y, 4);
		Assert.Equal(0f, state.Pitch, 4);
		Assert.Equal(0f, state.Roll, 4);
		Assert.Equal(Ballistics.Gravity / (Traits.Omega * Traits.Omega), Traits.StaticCompressionMeters, 5);
	}

	[Fact]
	public void Dropped30Centimetres_ItOvershootsAtLeastOnce_AndSettlesWithinASecondAndAHalf()
	{
		const float drop = 0.3f;
		FourByFourState state = AtRest(drop);
		float rest = Traits.RideHeightMeters;
		bool wentBelow = false;
		bool overshot = false;
		int lastOutside = -1;

		for (int i = 0; i < 60 * 5; i++)
		{
			Tick(ref state, InputFrame.Neutral((uint)i));
			wentBelow |= state.Position.Y < rest - 0.001f;
			overshot |= wentBelow && state.Position.Y > rest + 0.001f;
			if (MathF.Abs(state.Position.Y - rest) > 0.02f * drop)
			{
				lastOutside = i;
			}
		}

		Assert.True(wentBelow, "it never compressed past its rest");
		Assert.True(overshot, "it came to rest without bouncing: not springy");
		Assert.InRange((lastOutside + 1) * Dt, 0f, 1.5f);
	}

	[Fact]
	public void InItsLinearRange_ItOvershootsByAbout37Percent_AndSettlesInAbout1Point3Seconds()
	{
		// Three centimetres up, every wheel still on the ground: a damped oscillator at
		// 1.6 Hz and a damping ratio of 0.3, whose first swing past rest is e^(−πζ/√(1−ζ²)).
		const float lift = 0.03f;
		FourByFourState state = AtRest(lift);
		float rest = Traits.RideHeightMeters;
		float lowest = 0f;
		int lastOutside = -1;

		for (int i = 0; i < 60 * 4; i++)
		{
			Tick(ref state, InputFrame.Neutral((uint)i));
			lowest = MathF.Min(lowest, state.Position.Y - rest);
			if (MathF.Abs(state.Position.Y - rest) > 0.02f * lift)
			{
				lastOutside = i;
			}
		}

		Assert.InRange(-lowest / lift, 0.32f, 0.42f);
		Assert.InRange((lastOutside + 1) * Dt, 1.0f, 1.45f);
	}

	[Fact]
	public void WithNoInputOnFlatGround_ItsEnergyNeverGrows_AndIsGoneIn10Seconds()
	{
		var state = FourByFourState.At(new Vector3(0f, Traits.RideHeightMeters + 0.08f, 0f), 0.4f);
		state.Pitch = Mathf.DegToRad(3f);
		state.Roll = Mathf.DegToRad(-4f);
		state.RollRate = 0.3f;

		float rest = Energy(AtRest());
		float start = Energy(state) - rest;
		Assert.True(start > 0.05f);

		for (int i = 0; i < 60 * 10; i++)
		{
			Tick(ref state, InputFrame.Neutral((uint)i));
			Assert.True(Energy(state) - rest <= 1.01f * start, $"tick {i}: {Energy(state) - rest} of {start}");
		}

		Assert.True(Energy(state) - rest < 0.01f * start);
	}

	[Fact]
	public void TheLastFiveCentimetresOfTravel_AreTenTimesAsStiff()
	{
		float travel = Traits.SpringTravelMeters;
		float soft = FourByFourSim.SpringForce(Traits, 0.11f, 0f) - FourByFourSim.SpringForce(Traits, 0.10f, 0f);
		float stop = FourByFourSim.SpringForce(Traits, travel - 0.01f, 0f) - FourByFourSim.SpringForce(Traits, travel - 0.02f, 0f);

		Assert.Equal(Traits.CornerStiffness * 0.01f, soft, 4);
		Assert.Equal(10f * soft, stop, 3);
	}

	[Fact]
	public void AHardLanding_IsCaughtByTheBumpStop_BeforeTheEndOfTheTravel()
	{
		// From 60 cm the bump stop is what holds the chassis off the end of its springs;
		// without it this landing runs every spring solid.
		FourByFourState state = AtRest(0.6f);
		float deepest = 0f;
		for (int i = 0; i < 120; i++)
		{
			WheelContact[] wheels = Flat(state);
			for (int w = 0; w < FourByFourSim.Wheels; w++)
			{
				deepest = MathF.Max(deepest, FourByFourSim.Compression(Traits, w, wheels[w], state.Pitch, state.Roll));
			}

			Tick(ref state, InputFrame.Neutral((uint)i), wheels);
		}

		Assert.InRange(deepest, Traits.SpringTravelMeters - FourByFourSim.BumpStopMeters, Traits.SpringTravelMeters - 0.005f);
	}

	[Fact]
	public void ASpringNeverPulls()
	{
		Assert.Equal(0f, FourByFourSim.SpringForce(Traits, 0.05f, 20f));
		Assert.Equal(0f, FourByFourSim.SpringForce(Traits, -0.1f, 0f));
		Assert.True(FourByFourSim.SpringForce(Traits, 0.05f, -1f) > FourByFourSim.SpringForce(Traits, 0.05f, 0f));
	}

	// ---- driving --------------------------------------------------------------------------

	[Fact]
	public void FullThrottle_RaisesSpeedMonotonicallyTo18_InAbout4AndAHalfSeconds_WithTheNoseUp()
	{
		FourByFourState state = AtRest();
		float last = 0f;
		int reached = -1;

		for (int i = 0; i < 60 * 8; i++)
		{
			Tick(ref state, Drive(1f, tick: (uint)i));
			float speed = Speed(state);
			Assert.True(speed >= last - 1e-5f, $"tick {i}: {speed} after {last}");
			if (speed > last + 1e-5f && i > 0)
			{
				Assert.True(state.Pitch > 0f, $"tick {i}: nose at {Mathf.RadToDeg(state.Pitch)}° while accelerating");
			}

			if (reached < 0 && speed >= 17.6f)
			{
				reached = i;
			}

			last = speed;
		}

		Assert.InRange(last, 17.6f, 18.4f);
		Assert.InRange((reached + 1) * Dt, 4f, 5f);
	}

	[Fact]
	public void AQuarterThrottle_Holds4AndAHalfMetresASecond()
	{
		FourByFourState state = AtRest();
		for (int i = 0; i < 60 * 10; i++)
		{
			Tick(ref state, Drive(0.25f, tick: (uint)i));
			if (i >= 60 * 3)
			{
				Assert.InRange(Speed(state), 4.3f, 4.7f);
			}
		}
	}

	[Fact]
	public void Braking_StopsItAt9MetresASecondSquared_WithTheNoseDown()
	{
		FourByFourState state = AtSpeed(1f);
		Assert.InRange(Speed(state), 17.99f, 18.01f);

		int ticks = 0;
		float lowest = 0f;
		while (Speed(state) > 0f && ticks < 600)
		{
			Tick(ref state, Drive(-1f, tick: (uint)ticks));
			ticks++;
			if (ticks >= 10 && Speed(state) > 1f)
			{
				Assert.True(state.Pitch < 0f, $"tick {ticks}: nose at {Mathf.RadToDeg(state.Pitch)}° while braking");
			}

			lowest = MathF.Min(lowest, state.Pitch);
		}

		Assert.InRange(ticks * Dt, (18f / 9f) - (2f * Dt), (18f / 9f) + (2f * Dt));
		Assert.True(lowest < Mathf.DegToRad(-0.5f));
	}

	[Fact]
	public void HeldBack_ItReversesTo6MetresASecond_AndCoastsAt1()
	{
		FourByFourState state = AtRest();
		for (int i = 0; i < 60 * 4; i++)
		{
			Tick(ref state, Drive(-1f, tick: (uint)i));
		}

		Assert.Equal(-6f, Speed(state), 3);

		FourByFourState coasting = AtSpeed(0.5f);
		float before = Speed(coasting);
		for (int i = 0; i < 60; i++)
		{
			Tick(ref coasting, Drive(0f, tick: (uint)i));
		}

		Assert.Equal(before - 1f, Speed(coasting), 2);
	}

	[Fact]
	public void TheSteeringsLock_FallsFrom32DegreesAtRestTo9AtTopSpeed_AndTurnsAt2Point5RadiansASecond()
	{
		Assert.Equal(Mathf.DegToRad(32f), FourByFourSim.SteerLimit(Traits, 0f), 5);
		Assert.Equal(Mathf.DegToRad(9f), FourByFourSim.SteerLimit(Traits, 18f), 5);
		Assert.Equal(Mathf.DegToRad(9f), FourByFourSim.SteerLimit(Traits, -30f), 5);
		Assert.InRange(FourByFourSim.SteerLimit(Traits, 9f), Mathf.DegToRad(20f), Mathf.DegToRad(21f));

		FourByFourState state = AtRest();
		Tick(ref state, Drive(0f, 1f));
		Assert.Equal(2.5f * Dt, state.Steer, 5);
	}

	[Theory]
	[InlineData(1f)]
	[InlineData(-1f)]
	public void ATurn_TurnsTheWaySteered_AndRollsTheBodyOutward(float steer)
	{
		FourByFourState state = AtSpeed(0.66f);
		float yaw = state.Yaw;

		for (int i = 0; i < 120; i++)
		{
			Tick(ref state, Drive(0.66f, steer, tick: (uint)i));
		}

		// Right (positive steer) turns the heading clockwise, which is negative yaw, and
		// leans the body out to the left: the right side up, which is positive roll.
		Assert.Equal(-MathF.Sign(steer), MathF.Sign(DroneSim.AngleBetween(yaw, state.Yaw)));
		Assert.Equal(MathF.Sign(steer), MathF.Sign(state.Roll));
		Assert.True(MathF.Abs(state.Roll) > Mathf.DegToRad(2f));
	}

	[Fact]
	public void NoTurn_AsksForMoreThanGrip()
	{
		var random = new Random(11);
		FourByFourState state = AtSpeed(1f, 300);
		for (int i = 0; i < 60 * 60; i++)
		{
			float throttle = (float)random.NextDouble() * 2f - 1f;
			float steer = (float)random.NextDouble() * 2f - 1f;
			bool handbrake = random.Next(8) == 0;
			Tick(ref state, Drive(throttle, steer, handbrake, (uint)i));

			Assert.True(MathF.Abs(Speed(state) * state.YawRate) <= Traits.GripAcceleration + 0.15f,
				$"tick {i}: {Speed(state)} m/s at {state.YawRate} rad/s");
		}
	}

	[Fact]
	public void TheHandbrake_Slides()
	{
		float Slide(bool handbrake)
		{
			FourByFourState state = AtSpeed(0.85f);
			float most = 0f;
			for (int i = 0; i < 60; i++)
			{
				Tick(ref state, Drive(0.85f, 1f, handbrake, (uint)i));
				most = MathF.Max(most, MathF.Abs(Sideways(state)));
			}

			return most;
		}

		float gripping = Slide(false);
		float sliding = Slide(true);

		Assert.True(gripping < 0.2f, $"{gripping} m/s sideways with the tyres gripping");
		Assert.True(sliding > 2f, $"{sliding} m/s sideways on the handbrake");
	}

	[Fact]
	public void AnEmpty4x4_BrakesToAStopOnItsHandbrake()
	{
		FourByFourState state = AtSpeed(1f);
		for (int i = 0; i < 60 * 4; i++)
		{
			Tick(ref state, FourByFourSim.ParkedFrame((uint)i));
		}

		Assert.Equal(0f, state.Velocity.Length(), 3);
		Assert.True(FourByFourSim.ParkedFrame(5).Held(InputButtons.Jump));
	}

	// ---- bounds -----------------------------------------------------------------------------

	[Fact]
	public void ItsAttitude_StaysWithin35Degrees_OverTenThousandRandomInputsAndContacts()
	{
		var random = new Random(23);
		FourByFourState state = AtSpeed(1f, 120);
		float max = Mathf.DegToRad(35f) + 1e-5f;
		var wheels = new WheelContact[FourByFourSim.Wheels];

		for (int i = 0; i < 10_000; i++)
		{
			for (int w = 0; w < wheels.Length; w++)
			{
				wheels[w] = random.Next(5) == 0
					? WheelContact.Miss
					: WheelContact.At((float)random.NextDouble() * Traits.RayLengthMeters);
			}

			InputFrame frame = Drive((float)random.NextDouble() * 2f - 1f, (float)random.NextDouble() * 2f - 1f,
				random.Next(4) == 0, (uint)i);
			Tick(ref state, frame, wheels);

			Assert.InRange(state.Pitch, -max, max);
			Assert.InRange(state.Roll, -max, max);
			Assert.True(float.IsFinite(state.PitchRate) && float.IsFinite(state.RollRate) && float.IsFinite(state.YawRate));
			Assert.True(float.IsFinite(state.Position.X) && float.IsFinite(state.Position.Y) && float.IsFinite(state.Position.Z));
		}
	}

	[Fact]
	public void WithNoContacts_ItFallsAtG_AndTheThrottleChangesNothing()
	{
		FourByFourState still = AtSpeed(0.5f);
		FourByFourState driven = still;
		ReadOnlySpan<WheelContact> none = new WheelContact[FourByFourSim.Wheels];
		float start = still.Velocity.Y;

		for (int i = 0; i < 60; i++)
		{
			Tick(ref still, Drive(0f, tick: (uint)i), none);
			Tick(ref driven, Drive(1f, 1f, tick: (uint)i), none);
		}

		Assert.Equal(start - Ballistics.Gravity, still.Velocity.Y, 3);
		Assert.Equal(still.Velocity, driven.Velocity);
		Assert.Equal(still.Position, driven.Position);
		Assert.True(still.Airborne);
	}

	[Fact]
	public void TheSameInputs_GiveBitIdenticalStates_OverAThousandRuns()
	{
		FourByFourState Run()
		{
			var random = new Random(5);
			FourByFourState state = AtRest(0.2f);
			for (int i = 0; i < 400; i++)
			{
				Tick(ref state, Drive((float)random.NextDouble() * 2f - 1f, (float)random.NextDouble() * 2f - 1f,
					random.Next(6) == 0, (uint)i));
			}

			return state;
		}

		string first = Bits(Run());
		for (int run = 1; run < 1000; run++)
		{
			Assert.Equal(first, Bits(Run()));
		}
	}

	[Fact]
	public void ItsPosition_StaysInsideWhatTheWireCanCarry()
	{
		// A minute flat out in one direction, and twenty seconds falling with nothing under it.
		FourByFourState state = AtRest();
		for (int i = 0; i < 60 * 60; i++)
		{
			Tick(ref state, Drive(1f, tick: (uint)i));
			Assert.True(MathF.Abs(state.Position.Z) <= CrewField.HalfExtentMeters);
		}

		ReadOnlySpan<WheelContact> none = new WheelContact[FourByFourSim.Wheels];
		for (int i = 0; i < 60 * 20; i++)
		{
			Tick(ref state, Drive(1f, tick: (uint)i), none);
		}

		Assert.InRange(state.Position.X, -SimConfig.MaxPositionMeters, SimConfig.MaxPositionMeters);
		Assert.InRange(state.Position.Y, -SimConfig.MaxPositionMeters, SimConfig.MaxPositionMeters);
		Assert.InRange(state.Position.Z, -SimConfig.MaxPositionMeters, SimConfig.MaxPositionMeters);
	}

	// ---- helpers ----------------------------------------------------------------------------

	/// <summary>
	/// The chassis's energy per kilogram above what it would have at the floor: heave and
	/// rocking, the springs' and the bump stops' store, and its height. Horizontal motion
	/// is left out — it is the engine's and the brakes', not the springs'.
	/// </summary>
	private static float Energy(in FourByFourState state)
	{
		float kinetic = 0.5f * state.Velocity.Y * state.Velocity.Y
			+ (0.5f * Traits.PitchGyrationMeters * Traits.PitchGyrationMeters * state.PitchRate * state.PitchRate)
			+ (0.5f * Traits.RollGyrationMeters * Traits.RollGyrationMeters * state.RollRate * state.RollRate);

		float stored = 0f;
		WheelContact[] wheels = Flat(state);
		float k = Traits.CornerStiffness;
		float bumpStart = Traits.SpringTravelMeters - FourByFourSim.BumpStopMeters;
		for (int w = 0; w < FourByFourSim.Wheels; w++)
		{
			float c = FourByFourSim.Compression(Traits, w, wheels[w], state.Pitch, state.Roll);
			if (c > 0f)
			{
				float bump = MathF.Max(c - bumpStart, 0f);
				stored += (0.5f * k * c * c) + (0.5f * 9f * k * bump * bump);
			}
		}

		return kinetic + stored + (Ballistics.Gravity * state.Position.Y);
	}

	private static string Bits(in FourByFourState s) => string.Join(",",
		BitConverter.SingleToInt32Bits(s.Position.X), BitConverter.SingleToInt32Bits(s.Position.Y),
		BitConverter.SingleToInt32Bits(s.Position.Z), BitConverter.SingleToInt32Bits(s.Velocity.X),
		BitConverter.SingleToInt32Bits(s.Velocity.Y), BitConverter.SingleToInt32Bits(s.Velocity.Z),
		BitConverter.SingleToInt32Bits(s.Yaw), BitConverter.SingleToInt32Bits(s.YawRate),
		BitConverter.SingleToInt32Bits(s.Pitch), BitConverter.SingleToInt32Bits(s.PitchRate),
		BitConverter.SingleToInt32Bits(s.Roll), BitConverter.SingleToInt32Bits(s.RollRate),
		BitConverter.SingleToInt32Bits(s.Steer), s.AirTicks);
}
