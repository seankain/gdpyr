using System;
using Godot;

namespace Gdpyr.Sim;

/// <summary>
/// What one wheel's ray found (docs/GROUND_VEHICLES.md §5.1): the engine half casts
/// each straight down from its hardpoint on the upright body,
/// <see cref="FourByFourTraits.RayLengthMeters"/> long, and reports whether it hit and
/// how far down.
/// </summary>
public readonly struct WheelContact
{
	public readonly bool Hit;

	/// <summary>From the hardpoint on the upright body down to what the ray hit [m].</summary>
	public readonly float Distance;

	public WheelContact(bool hit, float distance)
	{
		Hit = hit && float.IsFinite(distance);
		Distance = Hit ? MathF.Max(distance, 0f) : 0f;
	}

	public static WheelContact Miss => default;

	public static WheelContact At(float distance) => new(true, distance);
}

/// <summary>
/// The parts of a crew vehicle's definition that say how the 4x4 drives
/// (docs/GROUND_VEHICLES.md §5.1, §9): its wheels and springs, its engine and brakes,
/// its steering and its grip. A struct of floats, as <see cref="FlightTraits"/> is, so
/// the step is testable without an engine. Angles are radians; every value is clamped
/// to something the step can run on, so a file that says zero is a truck that does
/// little, never one that divides by it.
/// </summary>
public readonly struct FourByFourTraits
{
	/// <summary>Axle to axle [m].</summary>
	public readonly float WheelbaseMeters;

	/// <summary>Half of wheel to wheel across an axle [m].</summary>
	public readonly float HalfTrackMeters;

	public readonly float WheelRadiusMeters;

	/// <summary>A spring's free length, hardpoint to wheel centre, with nothing on it [m].</summary>
	public readonly float SpringRestMeters;

	/// <summary>How far a spring compresses before it is solid [m]; the last <see cref="FourByFourSim.BumpStopMeters"/> are the bump stop.</summary>
	public readonly float SpringTravelMeters;

	/// <summary>The chassis's natural frequency on its springs [Hz]: 1.6, a soft truck.</summary>
	public readonly float SpringFrequencyHz;

	/// <summary>Of critical damping: 0.3 overshoots by about 37 % and settles in about 1.3 s.</summary>
	public readonly float DampingRatio;

	/// <summary>Of the centre of mass, above the ground: what acceleration and turning pitch and roll it about [m].</summary>
	public readonly float CentreOfMassHeightMeters;

	/// <summary>The chassis's radius of gyration in pitch [m]: its pitch inertia per unit mass is the square.</summary>
	public readonly float PitchGyrationMeters;

	/// <summary>The same in roll [m].</summary>
	public readonly float RollGyrationMeters;

	/// <summary>Top speed forward [m/s].</summary>
	public readonly float MaxSpeed;

	/// <summary>Top speed in reverse [m/s].</summary>
	public readonly float ReverseSpeed;

	/// <summary>The engine's pull towards the speed the throttle sets [m/s²].</summary>
	public readonly float DriveAcceleration;

	/// <summary>The brakes, against the motion [m/s²].</summary>
	public readonly float BrakeDeceleration;

	/// <summary>Rolling with nothing pressed, or faster than the throttle asks [m/s²].</summary>
	public readonly float CoastDeceleration;

	/// <summary>The handbrake, against the motion [m/s²].</summary>
	public readonly float HandbrakeDeceleration;

	/// <summary>Of the grip, left to the tyres sideways while the handbrake is on: a third, so it slides.</summary>
	public readonly float HandbrakeGripScale;

	/// <summary>The most sideways acceleration the tyres give [m/s²]: what a turn and a slide are both held to.</summary>
	public readonly float GripAcceleration;

	/// <summary>How fast the front wheels turn [rad/s].</summary>
	public readonly float SteerRate;

	/// <summary>The steering's lock standing still [rad].</summary>
	public readonly float SteerLimitAtRest;

	/// <summary>The steering's lock at top speed [rad]; between the two it falls with speed.</summary>
	public readonly float SteerLimitAtTop;

	/// <summary>How fast the chassis's turning and rocking die away in the air [1/s].</summary>
	public readonly float AirborneRateDamping;

	public FourByFourTraits(float wheelbaseMeters, float halfTrackMeters, float wheelRadiusMeters,
		float springRestMeters, float springTravelMeters, float springFrequencyHz, float dampingRatio,
		float centreOfMassHeightMeters, float pitchGyrationMeters, float rollGyrationMeters,
		float maxSpeed, float reverseSpeed, float driveAcceleration, float brakeDeceleration,
		float coastDeceleration, float handbrakeDeceleration, float handbrakeGripScale, float gripAcceleration,
		float steerRate, float steerLimitAtRest, float steerLimitAtTop, float airborneRateDamping)
	{
		WheelbaseMeters = AtLeast(wheelbaseMeters, 0.5f);
		HalfTrackMeters = AtLeast(halfTrackMeters, 0.25f);
		WheelRadiusMeters = AtLeast(wheelRadiusMeters, 0.05f);
		SpringTravelMeters = AtLeast(springTravelMeters, 2f * FourByFourSim.BumpStopMeters);
		SpringRestMeters = AtLeast(springRestMeters, SpringTravelMeters);
		SpringFrequencyHz = AtLeast(springFrequencyHz, 0.1f);
		DampingRatio = Math.Clamp(Finite(dampingRatio), 0f, 2f);
		CentreOfMassHeightMeters = Math.Clamp(Finite(centreOfMassHeightMeters), 0f, 5f);
		PitchGyrationMeters = AtLeast(pitchGyrationMeters, 0.1f);
		RollGyrationMeters = AtLeast(rollGyrationMeters, 0.1f);
		MaxSpeed = AtLeast(maxSpeed, 0.1f);
		ReverseSpeed = Math.Clamp(Finite(reverseSpeed), 0f, MaxSpeed);
		DriveAcceleration = AtLeast(driveAcceleration, 0f);
		BrakeDeceleration = AtLeast(brakeDeceleration, 0f);
		CoastDeceleration = AtLeast(coastDeceleration, 0f);
		HandbrakeDeceleration = AtLeast(handbrakeDeceleration, 0f);
		HandbrakeGripScale = Math.Clamp(Finite(handbrakeGripScale), 0f, 1f);
		GripAcceleration = AtLeast(gripAcceleration, 0.1f);
		SteerRate = AtLeast(steerRate, 0f);
		SteerLimitAtRest = Math.Clamp(Finite(steerLimitAtRest), 0f, 1.2f);
		SteerLimitAtTop = Math.Clamp(Finite(steerLimitAtTop), 0f, SteerLimitAtRest);
		AirborneRateDamping = AtLeast(airborneRateDamping, 0f);
	}

	/// <summary>
	/// The numbers docs/GROUND_VEHICLES.md §5.1 writes, and <c>Vehicles/four_by_four.tres</c>
	/// carries: what the tests check the step against, and what a file is checked against.
	/// </summary>
	public static FourByFourTraits Default => new(
		wheelbaseMeters: 3.0f,
		halfTrackMeters: 0.85f,
		wheelRadiusMeters: 0.4f,
		springRestMeters: 0.4f,
		springTravelMeters: 0.25f,
		springFrequencyHz: 1.6f,
		dampingRatio: 0.3f,
		centreOfMassHeightMeters: 0.55f,
		pitchGyrationMeters: 1.5f,
		rollGyrationMeters: 0.85f,
		maxSpeed: 18f,
		reverseSpeed: 6f,
		driveAcceleration: 4f,
		brakeDeceleration: 9f,
		coastDeceleration: 1f,
		handbrakeDeceleration: 6f,
		handbrakeGripScale: 1f / 3f,
		gripAcceleration: 9f,
		steerRate: 2.5f,
		steerLimitAtRest: Mathf.DegToRad(32f),
		steerLimitAtTop: Mathf.DegToRad(9f),
		airborneRateDamping: 2f);

	/// <summary>How far down each wheel's ray reaches: a spring at full length, its travel and the wheel [m].</summary>
	public float RayLengthMeters => SpringRestMeters + SpringTravelMeters + WheelRadiusMeters;

	/// <summary>The chassis's angular natural frequency [rad/s].</summary>
	public float Omega => MathF.Tau * SpringFrequencyHz;

	/// <summary>
	/// One spring's stiffness per kilogram of truck [N/m/kg]: four of them put the chassis
	/// at <see cref="SpringFrequencyHz"/>, which is what makes the result independent of
	/// the truck's mass, so the file has none to tune.
	/// </summary>
	public float CornerStiffness => Omega * Omega / FourByFourSim.Wheels;

	/// <summary>One damper's coefficient per kilogram [N·s/m/kg]: four make <see cref="DampingRatio"/>.</summary>
	public float CornerDamping => 2f * DampingRatio * Omega / FourByFourSim.Wheels;

	/// <summary>A spring's compression with the truck standing still on it: g / ω² [m].</summary>
	public float StaticCompressionMeters => Ballistics.Gravity / (Omega * Omega);

	/// <summary>The hardpoints' height above flat ground with the truck standing still [m].</summary>
	public float RideHeightMeters => SpringRestMeters - StaticCompressionMeters + WheelRadiusMeters;

	private static float AtLeast(float value, float min) => MathF.Max(Finite(value), min);

	private static float Finite(float value) => float.IsFinite(value) ? value : 0f;
}

/// <summary>
/// One 4x4 at one tick (docs/GROUND_VEHICLES.md §5.1). Everything the step reads and
/// writes, and everything the driver's client restores from a vehicle record to replay
/// from (§7.1): the body's position and velocity, its heading and how fast it turns,
/// the sprung chassis's pitch and roll and their rates, and the front wheels' angle.
///
/// The body itself never tilts (GV-D3): <see cref="Position"/> is the middle of the
/// hardpoints' plane on the upright collider, and the pitch and roll are drawn on the
/// mesh and applied to the seats and hull boxes through <see cref="Pose"/>.
/// </summary>
public struct FourByFourState
{
	public Vector3 Position;

	public Vector3 Velocity;

	/// <summary>Heading, in the look convention: forward is <c>Aim.Direction(Yaw, 0)</c>.</summary>
	public float Yaw;

	/// <summary>[rad/s], positive to the left.</summary>
	public float YawRate;

	/// <summary>The chassis's pitch on its springs [rad], nose up positive.</summary>
	public float Pitch;

	public float PitchRate;

	/// <summary>The chassis's roll on its springs [rad], right side up positive.</summary>
	public float Roll;

	public float RollRate;

	/// <summary>The front wheels' angle [rad], to the right positive, as <c>MoveX</c> is.</summary>
	public float Steer;

	/// <summary>Ticks in a row with fewer than two wheels down.</summary>
	public int AirTicks;

	public readonly bool Airborne => AirTicks > 0;

	/// <summary>Where it is and how the chassis is turned: what its seats and hull are placed by.</summary>
	public readonly CrewPose Pose => new(Position, Yaw, Pitch, Roll);

	/// <summary>A truck standing at rest with its hardpoints at <paramref name="position"/>.</summary>
	public static FourByFourState At(Vector3 position, float yaw) => new() { Position = position, Yaw = yaw };
}

/// <summary>
/// How the 4x4 drives (docs/GROUND_VEHICLES.md §5.1, GV-D3): a kinematic body on four
/// spring-dampers, as a pure function of its state, its traits, the driver's input
/// frame and what its four wheel rays found.
///
/// Pure because the driver's client replays it: a correction rewinds to the server's
/// state and re-runs this over the frames since, through the same
/// <c>MoveAndSlide</c>, and the result has to be the one the server got (GV-D4). A
/// rigid body cannot be stepped alone for a replay and its solver's state is not the
/// same on two peers.
///
/// <list type="number">
/// <item><b>Springs.</b> A corner's spring length comes from its wheel's contact and
/// the chassis's attitude; its force is stiffness × compression, ten times as stiff in
/// the last <see cref="BumpStopMeters"/>, less damping × the corner's vertical speed,
/// and never pulls. The sum against gravity heaves the chassis; their moments, and the
/// load moved by this tick's acceleration at the centre of mass's height, pitch and
/// roll it.</item>
/// <item><b>Traction</b>, with two wheels or more down: the throttle sets a speed and
/// the engine pulls towards it; the brakes, reverse and coasting; the steering, whose
/// lock falls with speed; a yaw rate from the steering, cut to what grip allows; grip
/// bleeding off sideways speed, a third of it on the handbrake.</item>
/// <item><b>Airborne</b>, with fewer: gravity, and the rates damped.</item>
/// </list>
///
/// Wheels in order: front left, front right, rear left, rear right.
/// </summary>
public static class FourByFourSim
{
	public const int Wheels = 4;

	/// <summary>The last stretch of a spring's travel that is its bump stop [m].</summary>
	public const float BumpStopMeters = 0.05f;

	/// <summary>How much stiffer the bump stop is than the spring.</summary>
	public const float BumpStopStiffnessScale = 10f;

	/// <summary>The most the chassis pitches or rolls on its springs: it does not roll over (§5.1).</summary>
	public static readonly float MaxAttitudeRadians = Mathf.DegToRad(35f);

	/// <summary>The wheels a 4x4 needs on the ground to drive.</summary>
	public const int WheelsForTraction = 2;

	/// <summary>A wheel's hardpoint in the body's frame: ± half the track across, ± half the wheelbase along.</summary>
	public static Vector3 HardpointLocal(in FourByFourTraits traits, int wheel)
	{
		float x = (wheel & 1) == 0 ? -traits.HalfTrackMeters : traits.HalfTrackMeters;
		float z = wheel < 2 ? -traits.WheelbaseMeters * 0.5f : traits.WheelbaseMeters * 0.5f;
		return new Vector3(x, 0f, z);
	}

	/// <summary>Where a wheel's ray starts, on the upright body: the engine half casts it straight down from here.</summary>
	public static Vector3 Hardpoint(in FourByFourState state, in FourByFourTraits traits, int wheel) =>
		state.Position + new Basis(Vector3.Up, state.Yaw) * HardpointLocal(traits, wheel);

	/// <summary>
	/// A spring's push, per kilogram of truck [m/s²], at <paramref name="compression"/>
	/// (clamped to its travel) with its corner rising at <paramref name="cornerSpeed"/>:
	/// stiffness × compression, plus <see cref="BumpStopStiffnessScale"/> − 1 times that
	/// again past the start of the bump stop, less damping × the corner's speed. Never
	/// negative: a spring pushes, and a wheel lifting off the ground lets go.
	/// </summary>
	public static float SpringForce(in FourByFourTraits traits, float compression, float cornerSpeed)
	{
		float c = Math.Clamp(compression, 0f, traits.SpringTravelMeters);
		float k = traits.CornerStiffness;
		float bump = MathF.Max(c - (traits.SpringTravelMeters - BumpStopMeters), 0f);
		float force = (k * c) + ((BumpStopStiffnessScale - 1f) * k * bump) - (traits.CornerDamping * cornerSpeed);
		return MathF.Max(force, 0f);
	}

	/// <summary>
	/// How much higher a corner at (<paramref name="x"/>, <paramref name="z"/>) in the
	/// body's frame is for the chassis's attitude than with it level [m].
	/// </summary>
	public static float CornerLift(float x, float z, float pitch, float roll) =>
		(x * MathF.Sin(roll) * MathF.Cos(pitch)) - (z * MathF.Sin(pitch));

	/// <summary>
	/// A spring's compression for a wheel whose ray found <paramref name="contact"/>, with
	/// the chassis at <paramref name="pitch"/> and <paramref name="roll"/>; zero or less
	/// is a wheel off the ground. Unclamped.
	/// </summary>
	public static float Compression(in FourByFourTraits traits, int wheel, in WheelContact contact, float pitch,
		float roll)
	{
		if (!contact.Hit)
		{
			return float.NegativeInfinity;
		}

		Vector3 local = HardpointLocal(traits, wheel);
		float length = contact.Distance + CornerLift(local.X, local.Z, pitch, roll) - traits.WheelRadiusMeters;
		return traits.SpringRestMeters - length;
	}

	/// <summary>The frame an empty 4x4 is stepped on (§6.2): nothing pressed but the handbrake, so it brakes to a stop.</summary>
	public static InputFrame ParkedFrame(uint tick) => InputFrame.Create(tick, 0f, 0f, 0f, 0f, InputButtons.Jump);

	/// <summary>
	/// One tick. Moves <paramref name="state"/>'s chassis and steering on, and returns the
	/// velocity the engine half gives <c>MoveAndSlide</c>; <see cref="AfterMove"/> then
	/// takes back where the body went and how fast it is moving. <paramref name="wheels"/>
	/// holds one contact per wheel, in <see cref="Wheels"/> order; a missing one is a miss.
	///
	/// <c>W</c>/<c>S</c> (<see cref="InputFrame.MoveZ"/>) are the throttle, the brake and
	/// reverse; <c>A</c>/<c>D</c> (<see cref="InputFrame.MoveX"/>) steer; <c>Space</c>
	/// (<see cref="InputButtons.Jump"/>) is the handbrake. The look angles are not read:
	/// the camera follows the truck, the truck never follows the camera (§8.4).
	/// </summary>
	public static Vector3 Step(ref FourByFourState state, in FourByFourTraits traits, in InputFrame frame,
		ReadOnlySpan<WheelContact> wheels, float dt)
	{
		float step = dt > 0f && float.IsFinite(dt) ? dt : SimConfig.TickDelta;

		// ---- 1. springs ---------------------------------------------------------------
		float sinPitch = MathF.Sin(state.Pitch);
		float cosPitch = MathF.Cos(state.Pitch);
		float sinRoll = MathF.Sin(state.Roll);
		float cosRoll = MathF.Cos(state.Roll);

		float heave = 0f;
		float pitchTorque = 0f;
		float rollTorque = 0f;
		int down = 0;

		for (int wheel = 0; wheel < Wheels; wheel++)
		{
			WheelContact contact = wheel < wheels.Length ? wheels[wheel] : WheelContact.Miss;
			float compression = Compression(traits, wheel, contact, state.Pitch, state.Roll);
			if (!(compression > 0f))
			{
				continue;
			}

			down++;
			Vector3 local = HardpointLocal(traits, wheel);

			// How the corner's height moves with pitch and with roll: the lever each
			// spring has on them, and with the rates the corner's own vertical speed.
			float liftPerPitch = (-local.X * sinRoll * sinPitch) - (local.Z * cosPitch);
			float liftPerRoll = local.X * cosRoll * cosPitch;
			float cornerSpeed = state.Velocity.Y + (liftPerPitch * state.PitchRate) + (liftPerRoll * state.RollRate);

			float force = SpringForce(traits, compression, cornerSpeed);
			heave += force;
			pitchTorque += force * liftPerPitch;
			rollTorque += force * liftPerRoll;
		}

		bool traction = down >= WheelsForTraction;
		state.AirTicks = traction ? 0 : Math.Min(state.AirTicks + 1, int.MaxValue - 1);

		// ---- 2. traction ----------------------------------------------------------------
		var horizontal = new Vector3(state.Velocity.X, 0f, state.Velocity.Z);
		Vector3 before = horizontal;

		float lockNow = SteerLimit(traits, horizontal.Dot(Aim.Direction(state.Yaw, 0f)));
		float targetSteer = Math.Clamp(frame.MoveXAxis, -1f, 1f) * lockNow;
		state.Steer += Math.Clamp(targetSteer - state.Steer, -traits.SteerRate * step, traits.SteerRate * step);

		if (traction)
		{
			bool handbrake = frame.Held(InputButtons.Jump);
			Vector3 forward = Aim.Direction(state.Yaw, 0f);
			Vector3 right = new(-forward.Z, 0f, forward.X);
			float along = horizontal.Dot(forward);
			float across = horizontal.Dot(right);

			along = Longitudinal(traits, along, -frame.MoveZAxis, handbrake, step);

			// The yaw rate the steering asks for, cut so the sideways acceleration it
			// would take stays inside the tyres' grip.
			float yawRate = -along * MathF.Tan(state.Steer) / traits.WheelbaseMeters;
			float cap = traits.GripAcceleration / MathF.Max(MathF.Abs(along), 0.1f);
			state.YawRate = Math.Clamp(yawRate, -cap, cap);
			state.Yaw = WrapAngle(state.Yaw + (state.YawRate * step));

			// The velocity stays where it was in the world as the nose comes round; grip
			// then takes off what is now sideways, less of it on the handbrake.
			horizontal = (forward * along) + (right * across);
			Vector3 newForward = Aim.Direction(state.Yaw, 0f);
			Vector3 newRight = new(-newForward.Z, 0f, newForward.X);
			along = horizontal.Dot(newForward);
			across = horizontal.Dot(newRight);
			float grip = traits.GripAcceleration * (handbrake ? traits.HandbrakeGripScale : 1f) * step;
			across -= Math.Clamp(across, -grip, grip);
			horizontal = (newForward * along) + (newRight * across);
		}
		else
		{
			float decay = MathF.Max(1f - (traits.AirborneRateDamping * step), 0f);
			state.YawRate *= decay;
			state.Yaw = WrapAngle(state.Yaw + (state.YawRate * step));
		}

		// ---- 3. the chassis -----------------------------------------------------------
		Vector3 accel = (horizontal - before) / step;
		Vector3 bodyForward = Aim.Direction(state.Yaw, 0f);
		Vector3 bodyRight = new(-bodyForward.Z, 0f, bodyForward.X);
		float accelAlong = accel.Dot(bodyForward);
		float accelAcross = accel.Dot(bodyRight);

		// Accelerating, the tyres push forward below the centre of mass and the nose
		// lifts; turning, they push inward below it and the body leans out.
		float h = traits.CentreOfMassHeightMeters;
		float pitchInertia = traits.PitchGyrationMeters * traits.PitchGyrationMeters;
		float rollInertia = traits.RollGyrationMeters * traits.RollGyrationMeters;
		state.PitchRate += (pitchTorque + (accelAlong * h)) / pitchInertia * step;
		state.RollRate += (rollTorque + (accelAcross * h)) / rollInertia * step;

		if (!traction)
		{
			float decay = MathF.Max(1f - (traits.AirborneRateDamping * step), 0f);
			state.PitchRate *= decay;
			state.RollRate *= decay;
		}

		state.Pitch += state.PitchRate * step;
		state.Roll += state.RollRate * step;
		ClampAttitude(ref state.Pitch, ref state.PitchRate);
		ClampAttitude(ref state.Roll, ref state.RollRate);

		float vertical = state.Velocity.Y + ((heave - Ballistics.Gravity) * step);

		Vector3 velocity = CrewField.Limit(state.Position, new Vector3(horizontal.X, vertical, horizontal.Z), step);
		state.Velocity = velocity;
		return velocity;
	}

	/// <summary>
	/// After the engine half has moved the body: where it went and how fast it is going
	/// now, which a wall or the floor may have changed. Kept inside the field.
	/// </summary>
	public static void AfterMove(ref FourByFourState state, Vector3 position, Vector3 velocity)
	{
		state.Position = CrewField.Clamp(position);
		state.Velocity = new Vector3(Finite(velocity.X), Finite(velocity.Y), Finite(velocity.Z));
	}

	/// <summary>
	/// The speed along the nose after one tick of throttle, brakes, reverse, coasting
	/// or handbrake. <paramref name="throttle"/> is in [−1, 1], forward positive; it sets
	/// the speed the engine drives towards — full, top speed — and against the motion it
	/// is the brake until the truck has stopped, and then reverse.
	/// </summary>
	public static float Longitudinal(in FourByFourTraits traits, float along, float throttle, bool handbrake, float dt)
	{
		float input = Math.Clamp(throttle, -1f, 1f);
		if (handbrake)
		{
			return Toward(along, 0f, traits.HandbrakeDeceleration * dt);
		}

		if (input > 0f)
		{
			if (along < 0f)
			{
				return Toward(along, 0f, traits.BrakeDeceleration * dt);
			}

			float target = input * traits.MaxSpeed;
			return along < target
				? MathF.Min(along + (traits.DriveAcceleration * dt), target)
				: MathF.Max(along - (traits.CoastDeceleration * dt), target);
		}

		if (input < 0f)
		{
			if (along > 0f)
			{
				return Toward(along, 0f, traits.BrakeDeceleration * dt);
			}

			float target = input * traits.ReverseSpeed;
			return along > target
				? MathF.Max(along - (traits.DriveAcceleration * dt), target)
				: MathF.Min(along + (traits.CoastDeceleration * dt), target);
		}

		return Toward(along, 0f, traits.CoastDeceleration * dt);
	}

	/// <summary>The steering's lock at a speed along the nose: falling from rest to top speed.</summary>
	public static float SteerLimit(in FourByFourTraits traits, float speed)
	{
		float t = Math.Clamp(MathF.Abs(speed) / traits.MaxSpeed, 0f, 1f);
		return traits.SteerLimitAtRest + ((traits.SteerLimitAtTop - traits.SteerLimitAtRest) * t);
	}

	private static void ClampAttitude(ref float angle, ref float rate)
	{
		float max = MaxAttitudeRadians;
		if (!float.IsFinite(angle) || !float.IsFinite(rate))
		{
			angle = 0f;
			rate = 0f;
			return;
		}

		if (angle > max)
		{
			angle = max;
			rate = MathF.Min(rate, 0f);
		}
		else if (angle < -max)
		{
			angle = -max;
			rate = MathF.Max(rate, 0f);
		}
	}

	private static float Toward(float value, float target, float maxStep) =>
		value < target ? MathF.Min(value + maxStep, target) : MathF.Max(value - maxStep, target);

	private static float Finite(float value) => float.IsFinite(value) ? value : 0f;

	/// <summary>An angle in (−π, π].</summary>
	public static float WrapAngle(float angle) => DroneSim.WrapAngle(angle);
}
