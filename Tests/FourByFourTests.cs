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

	/// <summary>What the six rays straight down onto flat ground at <paramref name="ground"/> find.</summary>
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

	[Theory]
	[InlineData(0)]
	[InlineData(3)]
	[InlineData(4)]
	public void TheLastFiveCentimetresOfTravel_AreTenTimesAsStiff(int wheel)
	{
		float travel = Traits.SpringTravelMeters;
		float soft = FourByFourSim.SpringForce(Traits, wheel, 0.11f, 0f) - FourByFourSim.SpringForce(Traits, wheel, 0.10f, 0f);
		float stop = FourByFourSim.SpringForce(Traits, wheel, travel - 0.01f, 0f)
			- FourByFourSim.SpringForce(Traits, wheel, travel - 0.02f, 0f);

		Assert.Equal(Traits.WheelStiffness(wheel) * 0.01f, soft, 4);
		Assert.Equal(10f * soft, stop, 3);
	}

	// ---- three axles ---------------------------------------------------------------------

	[Fact]
	public void SixWheels_OnASteeredFrontAxleAndARearTandem_LeftThenRight()
	{
		Assert.Equal(6, FourByFourSim.Wheels);
		Assert.Equal(3, FourByFourSim.Axles);

		var front = FourByFourSim.HardpointLocal(Traits, 0);
		var leading = FourByFourSim.HardpointLocal(Traits, 2);
		var trailing = FourByFourSim.HardpointLocal(Traits, 4);
		Assert.Equal(-1.75f, front.Z, 5);
		Assert.Equal(1.40f - 0.43f, leading.Z, 5);
		Assert.Equal(1.40f + 0.43f, trailing.Z, 5);
		Assert.Equal(3.15f, ((leading.Z + trailing.Z) * 0.5f) - front.Z, 5);

		for (int w = 0; w < FourByFourSim.Wheels; w++)
		{
			Vector3 p = FourByFourSim.HardpointLocal(Traits, w);
			Assert.Equal(w % 2 == 0 ? -Traits.HalfTrackMeters : Traits.HalfTrackMeters, p.X);
			Assert.Equal(0f, p.Y);
			Assert.Equal(w / 2, FourByFourSim.AxleOf(w));
			Assert.Equal(w < 2, FourByFourSim.Steers(w));
		}

		// The tyres of the tandem's two axles do not overlap.
		Assert.True(trailing.Z - leading.Z > 2f * Traits.WheelRadiusMeters);
	}

	[Fact]
	public void TheSprings_CarryTheWholeWeight_AndTheirMomentsAboutTheCentreOfMassCancel()
	{
		// Every spring compressed alike, as at rest: their sum is g, and the pitch and roll
		// they make are none, so the truck stands level.
		float c = Traits.StaticCompressionMeters;
		float heave = 0f;
		float pitch = 0f;
		float roll = 0f;
		float shares = 0f;
		for (int w = 0; w < FourByFourSim.Wheels; w++)
		{
			Vector3 p = FourByFourSim.HardpointLocal(Traits, w);
			float force = FourByFourSim.SpringForce(Traits, w, c, 0f);
			heave += force;
			pitch += force * p.Z;
			roll += force * p.X;
			shares += Traits.WheelShare(w);
		}

		Assert.Equal(Ballistics.Gravity, heave, 4);
		Assert.Equal(0f, pitch, 4);
		Assert.Equal(0f, roll, 4);
		Assert.Equal(1f, shares, 5);

		// The lever rule: the front axle carries the tandem's middle over the wheelbase.
		Assert.Equal(1.40f / 3.15f, Traits.FrontShare, 5);
		Assert.True(Traits.WheelStiffness(0) > Traits.WheelStiffness(2));
		Assert.Equal(Traits.WheelStiffness(2), Traits.WheelStiffness(5));
	}

	[Theory]
	[InlineData(0.3f)]
	[InlineData(-0.3f)]
	[InlineData(0.05f)]
	public void TheFrontPair_AreDrawnTurnedAboutOneCentre_TheInsideWheelMore_TheTandemNot(float steer)
	{
		float left = FourByFourSim.WheelSteer(Traits, 0, steer);
		float right = FourByFourSim.WheelSteer(Traits, 1, steer);
		float inside = steer > 0f ? right : left;
		float outside = steer > 0f ? left : right;

		Assert.Equal(MathF.Sign(steer), MathF.Sign(left));
		Assert.Equal(MathF.Sign(steer), MathF.Sign(right));
		Assert.True(MathF.Abs(inside) > MathF.Abs(steer) && MathF.Abs(outside) < MathF.Abs(steer),
			$"inside {inside}, outside {outside}, steer {steer}");

		// Each front wheel's axle, carried across to the tandem's middle line, meets it at the
		// same point: one turning centre, which is what the step turns the truck about.
		float back = Traits.WheelbaseMeters;
		float Centre(int wheel, float angle) =>
			FourByFourSim.HardpointLocal(Traits, wheel).X + (back / MathF.Tan(angle));
		Assert.Equal(Centre(0, left), Centre(1, right), 3);
		Assert.Equal(back / MathF.Tan(steer), Centre(0, left), 3);

		for (int w = 2; w < FourByFourSim.Wheels; w++)
		{
			Assert.Equal(0f, FourByFourSim.WheelSteer(Traits, w, steer));
		}

		Assert.Equal(0f, FourByFourSim.WheelSteer(Traits, 0, 0f));
		Assert.Equal(0f, FourByFourSim.WheelSteer(Traits, 1, float.NaN));
	}

	[Fact]
	public void TheRaysReach_IsFromTheAxleFarthestFromTheMiddle()
	{
		float farthest = MathF.Max(Traits.FrontAxleMeters, FourByFourSim.HardpointLocal(Traits, 4).Z);
		Assert.Equal((farthest + Traits.HalfTrackMeters) * MathF.Sin(FourByFourSim.MaxAttitudeRadians),
			Traits.AttitudeReachMeters, 5);
	}

	[Fact]
	public void AFrontAxleOnTheCentreOfMass_OrPastTheTandem_IsClampedBetweenThem()
	{
		var onIt = new FourByFourTraits(3f, 0f, 0.8f, 0.85f, 0.4f, 0.4f, 0.25f, 1.6f, 0.3f, 0.55f, 1.5f, 0.85f,
			18f, 6f, 4f, 9f, 1f, 6f, 1f / 3f, 9f, 2.5f, 0.5f, 0.15f, 2f);
		var pastIt = new FourByFourTraits(3f, 5f, 0.8f, 0.85f, 0.4f, 0.4f, 0.25f, 1.6f, 0.3f, 0.55f, 1.5f, 0.85f,
			18f, 6f, 4f, 9f, 1f, 6f, 1f / 3f, 9f, 2.5f, 0.5f, 0.15f, 2f);

		Assert.Equal(0.3f, onIt.FrontAxleMeters, 5);
		Assert.Equal(2.7f, pastIt.FrontAxleMeters, 5);
		Assert.InRange(onIt.FrontShare, 0.05f, 0.95f);
		Assert.InRange(pastIt.FrontShare, 0.05f, 0.95f);
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
		for (int w = 0; w < FourByFourSim.Wheels; w++)
		{
			Assert.Equal(0f, FourByFourSim.SpringForce(Traits, w, 0.05f, 20f));
			Assert.Equal(0f, FourByFourSim.SpringForce(Traits, w, -0.1f, 0f));
			Assert.True(FourByFourSim.SpringForce(Traits, w, 0.05f, -1f) > FourByFourSim.SpringForce(Traits, w, 0.05f, 0f));
		}
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

	// ---- slopes ----------------------------------------------------------------------------

	/// <summary>
	/// What the six rays straight down onto a plane rising <paramref name="grade"/> towards −Z
	/// find — the ground under a truck heading uphill at yaw 0 — through z = 0 at y = 0:
	/// the engine half's rays, from <see cref="FourByFourTraits.RayLiftMeters"/> above each
	/// hardpoint to <see cref="FourByFourTraits.RayLengthMeters"/> below, with the plane's normal.
	/// </summary>
	private static WheelContact[] Slope(in FourByFourState state, float grade)
	{
		var wheels = new WheelContact[FourByFourSim.Wheels];
		Vector3 normal = new Vector3(0f, 1f, grade).Normalized();
		for (int i = 0; i < wheels.Length; i++)
		{
			Vector3 hardpoint = FourByFourSim.Hardpoint(state, Traits, i);
			float distance = hardpoint.Y + (grade * hardpoint.Z);
			wheels[i] = distance >= -Traits.RayLiftMeters && distance <= Traits.RayLengthMeters
				? WheelContact.At(distance, normal)
				: WheelContact.Miss;
		}

		return wheels;
	}

	/// <summary>The spring compressions the step will see for these contacts.</summary>
	private static float[] Compressions(in FourByFourState state, ReadOnlySpan<WheelContact> wheels)
	{
		var compressions = new float[FourByFourSim.Wheels];
		for (int i = 0; i < compressions.Length; i++)
		{
			compressions[i] = FourByFourSim.Compression(Traits, i, wheels[i], state.Pitch, state.Roll);
		}

		return compressions;
	}

	[Fact]
	public void TheGroundsRise_IsItsGradeAlongTheVelocity_NoneOnTheLevel_AndHeldToTheAttitudeStop()
	{
		float grade = MathF.Tan(Mathf.DegToRad(20f));
		WheelContact slope = WheelContact.At(0.7f, new Vector3(0f, 1f, grade).Normalized());
		var uphill = new Vector3(0f, 0f, -10f);

		Assert.Equal(0f, FourByFourSim.GroundRise(WheelContact.At(0.7f), uphill));
		Assert.Equal(0f, FourByFourSim.GroundRise(WheelContact.Miss, uphill));
		Assert.Equal(10f * grade, FourByFourSim.GroundRise(slope, uphill), 4);
		Assert.Equal(-10f * grade, FourByFourSim.GroundRise(slope, -uphill), 4);
		Assert.Equal(0f, FourByFourSim.GroundRise(slope, new Vector3(10f, 0f, 0f)), 4);

		// The side of a step, which a ray down only grazes, is read as the stop's grade.
		WheelContact wall = WheelContact.At(0.7f, new Vector3(0f, 0.01f, 1f));
		Assert.Equal(10f * MathF.Tan(FourByFourSim.MaxAttitudeRadians), FourByFourSim.GroundRise(wall, uphill), 3);

		// A normal facing down or sideways, or none at all, is the level.
		Assert.Equal(Vector3.Up, WheelContact.At(0.7f, new Vector3(0f, -1f, 0f)).Normal);
		Assert.Equal(Vector3.Up, WheelContact.At(0.7f, new Vector3(float.NaN, 1f, 0f)).Normal);
	}

	[Theory]
	[InlineData(10f)]
	[InlineData(20f)]
	[InlineData(25f)]
	public void ClimbingASteadySlopeFlatOut_ItsSixWheelsStayDown_OffTheirBumpStops(float degrees)
	{
		// A truck climbing steadily is not lengthening its springs: damped on its own vertical
		// speed rather than on its speed over the ground, it sagged onto its bump stops as it
		// gathered speed uphill (at 12 m/s up 20°, 0.36 m of a 0.25 m travel).
		float grade = MathF.Tan(Mathf.DegToRad(degrees));
		const float z = 30f;
		FourByFourState state = FourByFourState.At(new Vector3(0f, (-grade * z) + Traits.RideHeightMeters, z), 0f);
		state.Pitch = MathF.Asin(grade);
		for (int i = 0; i < 120; i++)
		{
			Tick(ref state, Drive(0f, handbrake: true, tick: (uint)i), Slope(state, grade));
		}

		float bumpStop = Traits.SpringTravelMeters - FourByFourSim.BumpStopMeters;
		for (int i = 0; i < 180; i++)
		{
			WheelContact[] wheels = Slope(state, grade);
			float[] compressions = Compressions(state, wheels);
			for (int w = 0; w < compressions.Length; w++)
			{
				Assert.True(compressions[w] > 0f, $"wheel {w} off the ground at tick {i}: {compressions[w]:0.000} m");
				Assert.True(compressions[w] < bumpStop, $"wheel {w} on its bump stop at tick {i}: {compressions[w]:0.000} m");
			}

			Tick(ref state, Drive(1f, tick: (uint)(120 + i)), wheels);
			Assert.False(state.Airborne);
		}

		Assert.True(Speed(state) > 10f, $"{Speed(state):0.0} m/s after 3 s");
		Assert.InRange(state.Pitch, MathF.Asin(grade) - Mathf.DegToRad(3f), MathF.Asin(grade) + Mathf.DegToRad(3f));
	}

	[Theory]
	[InlineData(0f)]
	[InlineData(15f)]
	[InlineData(30f)]
	public void OnASlope_EveryWheelsRayFindsTheGround_UphillPairAndDownhill(float degrees)
	{
		// Cast from the hardpoint to a spring's reach, the downhill pair's rays missed from
		// about 13° and the uphill pair's started under the ground from about 25°.
		float grade = MathF.Tan(Mathf.DegToRad(degrees));
		FourByFourState state = FourByFourState.At(new Vector3(0f, Traits.RideHeightMeters, 0f), 0f);
		state.Pitch = MathF.Min(MathF.Asin(MathF.Min(grade, 1f)), FourByFourSim.MaxAttitudeRadians);
		WheelContact[] wheels = Slope(state, grade);
		float[] compressions = Compressions(state, wheels);
		for (int w = 0; w < wheels.Length; w++)
		{
			Assert.True(wheels[w].Hit, $"wheel {w}'s ray missed");
			Assert.True(compressions[w] > 0f, $"wheel {w} off the ground: {compressions[w]:0.000} m");
		}
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
		float bumpStart = Traits.SpringTravelMeters - FourByFourSim.BumpStopMeters;
		for (int w = 0; w < FourByFourSim.Wheels; w++)
		{
			float k = Traits.WheelStiffness(w);
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
