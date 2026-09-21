using Gdpyr.Core;
using Gdpyr.Sim;
using Godot;

namespace Gdpyr.Fps;

/// <summary>
/// The player character. Simulated on a fixed 60 Hz tick from an
/// <see cref="InputFrame"/> and nothing else — no <c>Input</c> reads, no render
/// frame — which is what makes it replayable and therefore predictable
/// (docs/NETCODE.md §3.1).
///
/// The same scene plays three roles:
/// <list type="bullet">
/// <item>on the server, every character is simulated and none is local;</item>
/// <item>on a client, its own character is simulated (predicted) and local, and
/// every other character is a <see cref="ApplyRemoteTransform">remote</see> one
/// driven by interpolated snapshots;</item>
/// <item>offline and on a listen host, one character is both.</item>
/// </list>
/// <see cref="PlayerManager"/> sets the two flags before the node enters the tree
/// and drives <see cref="Simulate"/>; this class never ticks itself.
/// </summary>
public partial class fps_controller : CharacterBody3D
{
	[Export]
	public float TiltLowerLimit = Mathf.DegToRad(-90);
	[Export]
	public float TiltUpperLimit = Mathf.DegToRad(90);

	[Export]
	public Camera3D camera;
	[Export]
	public AnimationPlayer animationPlayer;
	[Export]
	CollisionObject3D selfCollider;
	[Export]
	public ShapeCast3D headShapeCast;

	/// <summary>Get the gravity from the project settings to be synced with RigidBody nodes.</summary>
	public float gravity = ProjectSettings.GetSetting("physics/3d/default_gravity").AsSingle();

	/// <summary>The peer this character belongs to. 1 for a server or listen host.</summary>
	public int PeerId { get; set; } = 1;

	/// <summary>
	/// True when this process runs the movement simulation for this character:
	/// always on the server, only for its own character on a client.
	/// </summary>
	public bool IsSimulated { get; set; } = true;

	/// <summary>True for the one character this process draws a first-person view from.</summary>
	public bool IsLocalPlayer { get; set; } = true;

	/// <summary>Absolute look angles, as the last simulated input frame set them.</summary>
	public float Yaw { get; private set; }

	public float Pitch { get; private set; }

	/// <summary>Turn rate in radians per second, for the slide camera tilt.</summary>
	public float YawRate { get; private set; }

	/// <summary>
	/// The authoritative (or predicted) position. Not the same as
	/// <c>GlobalPosition</c>, which carries the decaying visual correction offset.
	/// </summary>
	public Vector3 SimPosition => _simPosition;

	public byte StateId => _stateMachine?.CurrentStateId ?? (byte)0;

	public string StateName => _stateMachine?.CurrentState?.Name ?? string.Empty;

	/// <summary>
	/// The buttons the *previous* simulated tick saw. Read before
	/// <see cref="Simulate"/> to build the <see cref="InputContext"/> that combat
	/// needs, so that movement and weapons agree about which presses were edges
	/// (docs/NETCODE.md §3.1).
	/// </summary>
	public ushort PreviousButtons => _previousButtons;

	/// <summary>Where a shot leaves from, and where this character's view starts.</summary>
	public Vector3 EyePosition => camera != null ? camera.GlobalPosition : GlobalPosition + Vector3.Up;

	/// <summary>The direction this character is looking, from the angles the simulation holds.</summary>
	public Vector3 AimDirection => Aim.Direction(Yaw, Pitch);

	/// <summary>
	/// This character's damageable volume right now, read from the collision shape
	/// rather than from constants: crouching and sliding animate the capsule, and a
	/// hitbox that ignored that would let a sliding player be shot in the head they
	/// no longer have there.
	/// </summary>
	public HitCapsule Hitbox
	{
		get
		{
			if (_collisionShape?.Shape is not CapsuleShape3D capsule)
			{
				return HitCapsule.FromFeet(SimPosition, 2f, 0.5f);
			}

			// The shape's own origin is animated along with its height, so the capsule
			// is built from where the shape actually is, not from the character's feet.
			Vector3 center = SimPosition + _collisionShape.Position;
			float axis = Mathf.Max(capsule.Height - (2f * capsule.Radius), 0f) * 0.5f;
			return new HitCapsule(center - new Vector3(0f, axis, 0f), center + new Vector3(0f, axis, 0f),
				capsule.Radius);
		}
	}

	private StateMachine _stateMachine;
	private CollisionShape3D _collisionShape;
	private Vector3 _simPosition;
	private Vector3 _visualOffset;
	private ushort _previousButtons;
	private bool _deadPresentation;
	private bool _fogHidden;

	public override void _Ready()
	{
		_stateMachine = GetNode<StateMachine>("PlayerStateMachine");
		_collisionShape = GetNodeOrNull<CollisionShape3D>("CollisionShape3D");
		headShapeCast.AddException(selfCollider);

		// Crouching and sliding animate the collision capsule, so the animation has
		// to advance on the simulation tick. Left on the render frame it would make
		// the server's hitbox depend on the server's framerate.
		animationPlayer.CallbackModeProcess = AnimationMixer.AnimationCallbackModeProcess.Physics;

		MakeMutatedResourcesUnique();
		ConfigureForRole();

		_simPosition = GlobalPosition;
		ApplyLook(Rotation.Y, 0f);
	}

	/// <summary>
	/// Renders the decaying correction offset. The simulation is never touched from
	/// here: a reconciliation correction is applied to the state immediately and
	/// only *shown* gradually, so the camera never snaps (docs/NETCODE.md §3.2).
	/// </summary>
	public override void _Process(double delta)
	{
		if (_visualOffset == Vector3.Zero)
		{
			return;
		}

		_visualOffset *= Mathf.Pow(0.5f, (float)delta / SimConfig.VisualErrorHalfLifeSeconds);
		if (_visualOffset.LengthSquared() < 1e-8f)
		{
			_visualOffset = Vector3.Zero;
		}
		GlobalPosition = _simPosition + _visualOffset;
	}

	/// <summary>
	/// One simulation tick. Deterministic given the same starting state and frame:
	/// the client replays this over stored inputs on every correction, and it has to
	/// land where the server did.
	/// </summary>
	public void Simulate(in InputFrame frame)
	{
		if (!IsSimulated)
		{
			return;
		}

		float dt = SimConfig.TickDelta;

		// Physics must run from the exact simulated position; the visual offset is
		// re-applied afterwards so it never feeds back into the simulation.
		GlobalPosition = _simPosition;

		float previousYaw = Yaw;
		ApplyLook(frame.YawRadians, frame.PitchRadians);
		YawRate = Mathf.AngleDifference(previousYaw, Yaw) / dt;

		_stateMachine.Tick(new InputContext(frame, _previousButtons, dt), dt);

		_previousButtons = frame.Buttons;
		_simPosition = GlobalPosition;
		GlobalPosition = _simPosition + _visualOffset;
	}

	/// <summary>
	/// Moves the character bodily, as a respawn does. Velocity is cleared and the
	/// visual offset dropped: this is a teleport, and smoothing it would drag the
	/// camera across the map (docs/NETCODE.md §3.2).
	/// </summary>
	public void Teleport(Transform3D transform)
	{
		_visualOffset = Vector3.Zero;
		_simPosition = transform.Origin;
		GlobalPosition = transform.Origin;
		Velocity = Vector3.Zero;
		ApplyLook(transform.Basis.GetEuler().Y, 0f);
	}

	/// <summary>
	/// Puts a character into or out of its dead presentation: hidden, and off the
	/// player collision layer so a corpse is not an invisible wall for the living to
	/// walk into.
	///
	/// The server and the owning client both call this — the client on the health
	/// byte in a snapshot — so the collision they each simulate agrees, a round trip
	/// apart, and reconciliation closes the gap.
	/// </summary>
	public void SetDeadPresentation(bool dead)
	{
		_deadPresentation = dead;
		CollisionLayer = dead ? 0u : CollisionLayers.Players;
		ApplyPresentation();
	}

	/// <summary>
	/// Takes a character off a local strategist's screen because the fog says they
	/// cannot see it (docs/IMPLEMENTATION_PLAN.md §M4).
	///
	/// Presentation only, and only ever set on a process that is not simulating this
	/// character for real: on a client the records stop arriving and this hides the
	/// body they would have moved; on a listen host it is the whole of the fog. The
	/// collision layer is deliberately left alone — being invisible to the commander
	/// is not the same as not being there, and a round fired at a hidden player must
	/// still hit them.
	/// </summary>
	public void SetFogHidden(bool hidden)
	{
		if (_fogHidden == hidden)
		{
			return;
		}

		_fogHidden = hidden;
		ApplyPresentation();
	}

	/// <summary>
	/// The one place <see cref="Node3D.Visible"/> is decided. Two reasons to hide a
	/// character — it is dead, and it is out of sight — and either of them setting
	/// the flag on its own would make the other one's state depend on the order they
	/// arrived in.
	/// </summary>
	private void ApplyPresentation() => Visible = !_deadPresentation && !_fogHidden;

	/// <summary>
	/// Takes the camera and the mouse back for the first-person view.
	///
	/// A player who goes up to the strategist's chair and comes back down again is
	/// the same character node throughout (docs/IMPLEMENTATION_PLAN.md §M3), so
	/// <see cref="ConfigureForRole"/>'s one-shot setup at spawn is not enough on its
	/// own — this is the half of it that has to be repeatable.
	/// </summary>
	public void EnterFirstPerson()
	{
		if (!IsLocalPlayer || Bootstrap.IsDedicatedServer)
		{
			return;
		}

		Input.MouseMode = Input.MouseModeEnum.Captured;
		if (camera != null)
		{
			camera.Current = true;
		}
	}

	/// <summary>Places a remote character from an interpolated snapshot (docs/NETCODE.md §3.3).</summary>
	public void ApplyRemoteTransform(Vector3 position, float yaw, float pitch)
	{
		_simPosition = position;
		GlobalPosition = position;
		ApplyLook(yaw, pitch);
	}

	public CharacterState CaptureState(uint tick) => new()
	{
		Tick = tick,
		Position = _simPosition,
		Velocity = Velocity,
		Yaw = Yaw,
		Pitch = Pitch,
		StateId = StateId,
		OnFloor = IsOnFloor(),
	};

	/// <summary>
	/// Rewinds to an authoritative state before a replay. Position *and* velocity
	/// are both restored — restoring position alone is the classic source of
	/// reconciliation drift (docs/NETCODE.md §3.2).
	/// </summary>
	public void RestoreState(in CharacterState state)
	{
		_simPosition = state.Position;
		GlobalPosition = state.Position + _visualOffset;
		Velocity = state.Velocity;
		ApplyLook(state.Yaw, state.Pitch);
		_stateMachine.ForceState(state.StateId);
	}

	/// <summary>
	/// Turns a correction into a visual offset that decays away, given where the
	/// character was being drawn before the correction. Anything past
	/// <see cref="SimConfig.MaxSmoothedErrorMeters"/> is shown immediately: that is
	/// a teleport or a respawn, not a misprediction to hide.
	/// </summary>
	public void AbsorbVisualError(Vector3 renderPositionBeforeCorrection)
	{
		Vector3 error = renderPositionBeforeCorrection - _simPosition;
		_visualOffset = error.Length() > SimConfig.MaxSmoothedErrorMeters ? Vector3.Zero : error;
		GlobalPosition = _simPosition + _visualOffset;
	}

	// ---- movement, called by the FSM states --------------------------------

	public void ApplyGravity(float dt) => Velocity = Movement.ApplyGravity(Velocity, gravity, dt);

	public void ApplyMove(Vector2 moveAxes, float speed, float deceleration, float acceleration) =>
		Velocity = Movement.ApplyMove(Velocity, moveAxes, Yaw, new MoveParams(speed, acceleration, deceleration));

	/// <summary>
	/// Sets upward velocity rather than adding to it: a replayed tick must produce
	/// the same jump as the original, however many times it is replayed.
	/// </summary>
	public void ApplyJump(float jumpVelocity) => Velocity = Movement.ApplyJump(Velocity, jumpVelocity);

	public void Move() => MoveAndSlide();

	private void ApplyLook(float yaw, float pitch)
	{
		Yaw = yaw;
		Pitch = Mathf.Clamp(pitch, TiltLowerLimit, TiltUpperLimit);
		Basis = Basis.FromEuler(new Vector3(0f, Yaw, 0f));
		if (camera != null)
		{
			camera.Basis = Basis.FromEuler(new Vector3(Pitch, 0f, 0f));
		}
	}

	/// <summary>
	/// Sub-resources of a PackedScene are shared by every instance of it, and this
	/// scene mutates two of them: the crouch and slide animations resize the
	/// collision capsule, and the slide state writes its own move speed into the
	/// animation's keys. Shared, one player crouching would shrink everyone's
	/// hitbox. Nothing below M1 noticed, because there was only ever one character.
	/// </summary>
	private void MakeMutatedResourcesUnique()
	{
		var collisionShape = GetNodeOrNull<CollisionShape3D>("CollisionShape3D");
		if (collisionShape?.Shape != null)
		{
			collisionShape.Shape = (Shape3D)collisionShape.Shape.Duplicate(true);
		}

		var libraries = new Godot.Collections.Array<StringName>(animationPlayer.GetAnimationLibraryList());
		foreach (StringName libraryName in libraries)
		{
			AnimationLibrary library = animationPlayer.GetAnimationLibrary(libraryName);
			if (library == null)
			{
				continue;
			}

			animationPlayer.RemoveAnimationLibrary(libraryName);
			animationPlayer.AddAnimationLibrary(libraryName, (AnimationLibrary)library.Duplicate(true));
		}
	}

	/// <summary>
	/// The player scene carries a whole first-person rig — camera, HUD, pause menu,
	/// viewmodel. Exactly one character per process may own it, and a dedicated
	/// server owns none (docs/DEPLOYMENT.md §2).
	/// </summary>
	private void ConfigureForRole()
	{
		if (IsLocalPlayer && !Bootstrap.IsDedicatedServer)
		{
			Input.MouseMode = Input.MouseModeEnum.Captured;
			if (camera != null)
			{
				camera.Current = true;
			}
			return;
		}

		GetNodeOrNull("UserInterface")?.QueueFree();
		GetNodeOrNull("WeaponViewport")?.QueueFree();
		GetNodeOrNull("CameraController/Camera3D/WeaponRig")?.QueueFree();
		if (camera != null)
		{
			camera.Current = false;
		}
	}
}
