using System;
using Godot;

namespace Gdpyr.Sim;

/// <summary>
/// The parts of a crew vehicle's definition that say how the helicopter flies
/// (docs/GROUND_VEHICLES.md §5.2, §9). A struct of floats, as <see cref="FlightTraits"/>
/// is; speeds in m/s, accelerations in m/s², angles in radians. Every value is clamped
/// to something the step can run on.
/// </summary>
public readonly struct HelicopterTraits
{
	/// <summary>Seconds from still to full speed with a pilot seated.</summary>
	public readonly float SpinUpSeconds;

	/// <summary>Seconds from full speed to still without one.</summary>
	public readonly float SpinDownSeconds;

	/// <summary>Of full rotor speed, below which there is no lift (§5.2).</summary>
	public readonly float LiftRotor;

	/// <summary>Along the heading [m/s].</summary>
	public readonly float MaxForwardSpeed;

	/// <summary>Sideways, and backwards [m/s].</summary>
	public readonly float MaxSideSpeed;

	/// <summary>How fast it gets to the speed the stick asks for, and back to a hover [m/s²].</summary>
	public readonly float Acceleration;

	public readonly float ClimbSpeed;

	public readonly float DescentSpeed;

	/// <summary>How fast its vertical speed changes towards what the keys ask [m/s²].</summary>
	public readonly float VerticalAcceleration;

	/// <summary>How fast the heading comes round to where the pilot looks [rad/s].</summary>
	public readonly float TurnRate;

	/// <summary>The highest above the ground under it it climbs [m].</summary>
	public readonly float CeilingMeters;

	/// <summary>How fast one with no pilot, or no lift, comes down [m/s]: never faster.</summary>
	public readonly float UnpilotedDescentSpeed;

	/// <summary>How fast its speed across the ground dies away without a pilot [m/s²].</summary>
	public readonly float UnpilotedDrag;

	/// <summary>The most it tilts, nose down or banked [rad].</summary>
	public readonly float MaxTilt;

	/// <summary>How fast the tilt follows the acceleration that sets it [1/s].</summary>
	public readonly float TiltResponse;

	public HelicopterTraits(float spinUpSeconds, float spinDownSeconds, float liftRotor, float maxForwardSpeed,
		float maxSideSpeed, float acceleration, float climbSpeed, float descentSpeed, float verticalAcceleration,
		float turnRate, float ceilingMeters, float unpilotedDescentSpeed, float unpilotedDrag, float maxTilt,
		float tiltResponse)
	{
		SpinUpSeconds = AtLeast(spinUpSeconds, SimConfig.TickDelta);
		SpinDownSeconds = AtLeast(spinDownSeconds, SimConfig.TickDelta);
		LiftRotor = Math.Clamp(Finite(liftRotor), 0f, 1f);
		MaxForwardSpeed = AtLeast(maxForwardSpeed, 0f);
		MaxSideSpeed = AtLeast(maxSideSpeed, 0f);
		Acceleration = AtLeast(acceleration, 0.1f);
		ClimbSpeed = AtLeast(climbSpeed, 0f);
		DescentSpeed = AtLeast(descentSpeed, FlightTraits.MinVerticalSpeed);
		VerticalAcceleration = AtLeast(verticalAcceleration, 0.1f);
		TurnRate = AtLeast(turnRate, 0f);
		CeilingMeters = Math.Clamp(Finite(ceilingMeters), 0f, FlightTraits.MaxCruiseAltitudeMeters);
		UnpilotedDescentSpeed = AtLeast(unpilotedDescentSpeed, FlightTraits.MinVerticalSpeed);
		UnpilotedDrag = AtLeast(unpilotedDrag, 0f);
		MaxTilt = Math.Clamp(Finite(maxTilt), 0f, 1.2f);
		TiltResponse = AtLeast(tiltResponse, 0f);
	}

	/// <summary>The numbers §5.2 writes, and <c>Vehicles/helicopter.tres</c> carries.</summary>
	public static HelicopterTraits Default => new(
		spinUpSeconds: 2.5f,
		spinDownSeconds: 4f,
		liftRotor: 0.6f,
		maxForwardSpeed: 22f,
		maxSideSpeed: 10f,
		acceleration: 5f,
		climbSpeed: 5f,
		descentSpeed: 6f,
		verticalAcceleration: 8f,
		turnRate: Mathf.DegToRad(90f),
		ceilingMeters: 25f,
		unpilotedDescentSpeed: 4f,
		unpilotedDrag: 2f,
		maxTilt: Mathf.DegToRad(20f),
		tiltResponse: 6f);

	private static float AtLeast(float value, float min) => MathF.Max(Finite(value), min);

	private static float Finite(float value) => float.IsFinite(value) ? value : 0f;
}

/// <summary>
/// One helicopter at one tick (docs/GROUND_VEHICLES.md §5.2): what the step reads and
/// writes, and what the pilot's client restores from a record to replay from.
/// <see cref="Position"/> is its skids — its feet, as a unit's is — so its height above
/// the ground is one ray straight down from it.
/// </summary>
public struct HelicopterState
{
	public Vector3 Position;

	public Vector3 Velocity;

	/// <summary>Heading, in the look convention.</summary>
	public float Yaw;

	/// <summary>How fast the heading turned on the last tick [rad/s], positive to the left.</summary>
	public float YawRate;

	/// <summary>Its tilt [rad]: nose up positive, so accelerating forward is negative.</summary>
	public float Pitch;

	public float PitchRate;

	/// <summary>Its bank [rad]: right side up positive, so a push to the right is negative.</summary>
	public float Roll;

	public float RollRate;

	/// <summary>Rotor speed, 0 to 1.</summary>
	public float Rotor;

	/// <summary>On the ground: it does not move across it, and nothing slides.</summary>
	public bool Grounded;

	public readonly CrewPose Pose => new(Position, Yaw, Pitch, Roll);

	/// <summary>A helicopter standing on the ground at <paramref name="position"/>, its rotor still.</summary>
	public static HelicopterState At(Vector3 position, float yaw) =>
		new() { Position = position, Yaw = yaw, Grounded = true };
}

/// <summary>
/// How the helicopter flies (docs/GROUND_VEHICLES.md §5.2): a pure function of its
/// state, its traits, its pilot's input frame, whether there is a pilot, and how high
/// it is above the ground under it.
///
/// <list type="bullet">
/// <item><b>Rotor:</b> spins up while a pilot is seated and down without one; no lift
/// below <see cref="HelicopterTraits.LiftRotor"/>.</item>
/// <item><b>Flying:</b> <c>W</c>/<c>S</c> along the heading and <c>A</c>/<c>D</c> across it;
/// <c>Space</c> climbs and <c>Ctrl</c> comes down, and with neither it holds its height;
/// the heading turns towards the frame's yaw, the short way round, the drone's rule; a
/// ceiling above the ground under it, and the field's square round it.</item>
/// <item><b>Tilt:</b> nose down to accelerate and banked into a sideways push, bounded,
/// in proportion to the acceleration. Drawn, and it moves the door seat.</item>
/// <item><b>No pilot, or no lift, in the air:</b> it comes down no faster than
/// <see cref="HelicopterTraits.UnpilotedDescentSpeed"/>, its speed across the ground
/// dying away, and lands.</item>
/// <item><b>Grounded</b> at <see cref="GroundedMeters"/> or less with no upward speed:
/// no speed across the ground, no turning.</item>
/// </list>
/// </summary>
public static class HelicopterSim
{
	/// <summary>At or below this height above the ground, coming no higher, it is on the ground [m].</summary>
	public const float GroundedMeters = 0.05f;

	/// <summary>
	/// The rotor's speed after one tick: up at <c>1 / SpinUpSeconds</c> while a pilot is
	/// seated, down at <c>1 / SpinDownSeconds</c> without one, inside [0, 1].
	/// </summary>
	public static float SpinRotor(in HelicopterTraits traits, float rotor, bool piloted, float dt)
	{
		float r = float.IsFinite(rotor) ? rotor : 0f;
		r += piloted ? dt / traits.SpinUpSeconds : -dt / traits.SpinDownSeconds;

		// A hundred and fifty sixtieths of 1/2.5 add up to a hair under one in floats;
		// that hair is not worth a tick.
		return r >= 1f - 1e-5f ? 1f : r <= 1e-5f ? 0f : r;
	}

	/// <summary>Whether it can lift: a pilot seated and the rotor at <see cref="HelicopterTraits.LiftRotor"/> or more.</summary>
	public static bool HasLift(in HelicopterTraits traits, float rotor, bool piloted) =>
		piloted && rotor >= traits.LiftRotor - 1e-6f;

	/// <summary>
	/// The tilt the acceleration across the ground calls for (§5.2): nose down by
	/// <see cref="HelicopterTraits.MaxTilt"/> at full acceleration forward, banked as far
	/// into a push at full acceleration sideways, in proportion between, and no more.
	/// <paramref name="forward"/> and <paramref name="right"/> are the acceleration
	/// along the heading and to its right [m/s²].
	/// </summary>
	public static void TiltFor(in HelicopterTraits traits, float forward, float right, out float pitch, out float roll)
	{
		float scale = traits.MaxTilt / traits.Acceleration;
		pitch = Math.Clamp(-forward * scale, -traits.MaxTilt, traits.MaxTilt);
		roll = Math.Clamp(-right * scale, -traits.MaxTilt, traits.MaxTilt);
	}

	/// <summary>
	/// One tick. Moves <paramref name="state"/>'s rotor, heading and tilt on, and returns
	/// the velocity the engine half gives <c>MoveAndSlide</c>; <see cref="AfterMove"/>
	/// takes back where it went. <paramref name="heightAboveGround"/> is the height of
	/// its skids over the ground under it before this tick's move; one that is not known
	/// — the ray found nothing — is taken to be the ceiling, which it neither climbs past
	/// nor lands from.
	/// </summary>
	public static Vector3 Step(ref HelicopterState state, in HelicopterTraits traits, in InputFrame frame,
		bool piloted, float heightAboveGround, float dt)
	{
		float step = dt > 0f && float.IsFinite(dt) ? dt : SimConfig.TickDelta;
		float height = float.IsFinite(heightAboveGround) ? MathF.Max(heightAboveGround, 0f) : traits.CeilingMeters;

		state.Rotor = SpinRotor(traits, state.Rotor, piloted, step);
		bool lift = HasLift(traits, state.Rotor, piloted);

		var horizontal = new Vector3(state.Velocity.X, 0f, state.Velocity.Z);
		Vector3 before = horizontal;
		float vertical = Finite(state.Velocity.Y);
		float yawBefore = state.Yaw;

		if (lift)
		{
			// The heading comes round to the pilot's look, as a drone's does.
			float turn = traits.TurnRate * step;
			state.Yaw = DroneSim.WrapAngle(state.Yaw
				+ Math.Clamp(DroneSim.AngleBetween(state.Yaw, frame.YawRadians), -turn, turn));

			// The stick: the speed it asks for along and across the new heading, and the
			// acceleration that gets there — or back to a hover.
			Vector3 forward = Aim.Direction(state.Yaw, 0f);
			Vector3 right = new(-forward.Z, 0f, forward.X);
			float ahead = Math.Clamp(-frame.MoveZAxis, -1f, 1f);
			float side = Math.Clamp(frame.MoveXAxis, -1f, 1f);
			float wantAlong = ahead * (ahead >= 0f ? traits.MaxForwardSpeed : traits.MaxSideSpeed);
			float wantAcross = side * traits.MaxSideSpeed;
			float accel = traits.Acceleration * step;
			float along = Toward(horizontal.Dot(forward), wantAlong, accel);
			float across = Toward(horizontal.Dot(right), wantAcross, accel);
			horizontal = (forward * along) + (right * across);

			float wantVertical = frame.Held(InputButtons.Jump)
				? traits.ClimbSpeed
				: frame.Held(InputButtons.Crouch) ? -traits.DescentSpeed : 0f;
			vertical = Toward(vertical, wantVertical, traits.VerticalAcceleration * step);
		}
		else
		{
			// Coming down on what is left of the rotor: never faster than the unpiloted
			// descent, and its way across the ground dying off.
			float speed = horizontal.Length();
			if (speed > 0f)
			{
				horizontal *= MathF.Max(speed - (traits.UnpilotedDrag * step), 0f) / speed;
			}

			float want = state.Grounded ? 0f : -traits.UnpilotedDescentSpeed;
			vertical = Toward(vertical, want, traits.VerticalAcceleration * step);
			vertical = MathF.Max(vertical, -traits.UnpilotedDescentSpeed);
		}

		// No higher than the ceiling over the ground under it — one that finds itself
		// above it, off the edge of a roof, comes back down at its descent rate — and no
		// further down than the ground: it arrives on it rather than through it.
		vertical = MathF.Min(vertical, MathF.Max((traits.CeilingMeters - height) / step, -traits.DescentSpeed));
		vertical = MathF.Max(vertical, -height / step);

		bool grounded = height + (vertical * step) <= GroundedMeters && vertical <= 0f;
		if (grounded)
		{
			// Nothing slides on the ground, and it does not turn there either; it settles
			// the last few centimetres onto it.
			horizontal = Vector3.Zero;
			vertical = height <= GroundedMeters ? -height / step : MathF.Max(vertical, -height / step);
			state.Yaw = yawBefore;
		}

		state.Grounded = grounded;
		state.YawRate = DroneSim.AngleBetween(yawBefore, state.Yaw) / step;

		// The tilt follows the acceleration it was given, as a first-order lag.
		Vector3 accelWorld = (horizontal - before) / step;
		Vector3 bodyForward = Aim.Direction(state.Yaw, 0f);
		Vector3 bodyRight = new(-bodyForward.Z, 0f, bodyForward.X);
		TiltFor(traits, accelWorld.Dot(bodyForward), accelWorld.Dot(bodyRight), out float pitchTarget,
			out float rollTarget);
		if (grounded)
		{
			pitchTarget = 0f;
			rollTarget = 0f;
		}

		float follow = Math.Clamp(traits.TiltResponse * step, 0f, 1f);
		float pitch = state.Pitch + ((pitchTarget - state.Pitch) * follow);
		float roll = state.Roll + ((rollTarget - state.Roll) * follow);
		pitch = Math.Clamp(pitch, -traits.MaxTilt, traits.MaxTilt);
		roll = Math.Clamp(roll, -traits.MaxTilt, traits.MaxTilt);
		state.PitchRate = (pitch - state.Pitch) / step;
		state.RollRate = (roll - state.Roll) / step;
		state.Pitch = pitch;
		state.Roll = roll;

		Vector3 velocity = CrewField.Limit(state.Position, new Vector3(horizontal.X, vertical, horizontal.Z), step);
		state.Velocity = velocity;
		return velocity;
	}

	/// <summary>After the engine half has moved it: where it went and how fast it is going, inside the field.</summary>
	public static void AfterMove(ref HelicopterState state, Vector3 position, Vector3 velocity)
	{
		state.Position = CrewField.Clamp(position);
		state.Velocity = new Vector3(Finite(velocity.X), Finite(velocity.Y), Finite(velocity.Z));
		if (state.Grounded)
		{
			state.Velocity = new Vector3(0f, MathF.Min(state.Velocity.Y, 0f), 0f);
		}
	}

	private static float Toward(float value, float target, float maxStep) =>
		value < target ? MathF.Min(value + maxStep, target) : MathF.Max(value - maxStep, target);

	private static float Finite(float value) => float.IsFinite(value) ? value : 0f;
}
