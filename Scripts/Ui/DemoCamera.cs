using Gdpyr.Core;
using Gdpyr.Fps;
using Gdpyr.Net;
using Gdpyr.Sim;
using Godot;

namespace Gdpyr.Ui;

/// <summary>
/// What a demo is watched through (docs/DEMOS.md §5.3).
///
/// A replay has no local player — nobody in it is this process's character — so
/// there is no camera in the scene that wants to be current, and this is it. It
/// flies freely by default and will sit on a player's shoulder when told to;
/// touching the movement keys takes it off again, because the one thing that
/// should never need a command is getting the camera back.
///
/// It is a render-frame node on purpose. The read head advances on the physics
/// tick and can be paused or run at eight times speed, and a camera that stopped
/// when the demo was paused would make a paused demo unusable.
/// </summary>
public partial class DemoCamera : Camera3D
{
	/// <summary>Metres per second at a walk.</summary>
	private const float MoveSpeed = 12f;

	/// <summary>What holding sprint multiplies that by.</summary>
	private const float FastMultiplier = 4f;

	/// <summary>Radians per pixel of mouse motion. The hip sensitivity, near enough.</summary>
	private const float LookSensitivity = 0.003f;

	/// <summary>Where a followed player's camera sits relative to their eye: behind and above.</summary>
	private static readonly Vector3 ChaseOffset = new(0f, 0.6f, 3.2f);

	private float _yaw;
	private float _pitch;

	/// <summary>The peer being followed, or 0 for the free camera.</summary>
	public int FollowedPeer { get; private set; }

	public override void _Ready()
	{
		// A demo can be paused, and pausing a demo does not pause the tree — but if
		// something else does, the camera still has to answer.
		ProcessMode = ProcessModeEnum.Always;
		Current = true;

		// Somewhere above the middle of the map, looking down, so that a demo opened
		// with no idea where anything is starts with the map in shot.
		Position = new Vector3(0f, 24f, 28f);
		_pitch = -0.6f;
		ApplyLook();
	}

	public override void _ExitTree() => Input.MouseMode = Input.MouseModeEnum.Visible;

	/// <summary>Follows a peer's character. Peer 0 — or one that is not on the field — means the free camera.</summary>
	public void SetFollow(int peerId)
	{
		FollowedPeer = peerId;
		Follow();
	}

	/// <summary>
	/// Places the camera when it is following somebody. Called once per physics
	/// step by <see cref="PlayerManager"/>, after that tick's records have moved
	/// the characters — a chase camera placed before them lags by a tick, which at
	/// a sprint is 13 cm of visible swim.
	/// </summary>
	public void Follow()
	{
		if (FollowedPeer == 0)
		{
			return;
		}

		fps_controller character = PlayerManager.Instance?.CharacterOf(FollowedPeer);
		if (character == null)
		{
			// They left the round, or the demo has not spawned them yet. Let go rather
			// than sit where they were: a camera stuck on a corner of the map with no
			// way back is worse than a free one.
			FollowedPeer = 0;
			return;
		}

		_yaw = character.Yaw;
		_pitch = character.Pitch;
		ApplyLook();

		// Behind the eye along the character's own aim, so the shoulder view points
		// where they are pointing.
		Position = character.EyePosition + (Basis * ChaseOffset);
	}

	public override void _Process(double delta)
	{
		if (InputFocus.TextEntry)
		{
			return;
		}

		// Taken here rather than once at start-up, because a demo is usually opened
		// by typing into the console and the console owns the pointer while it is
		// down. This is the moment it stops owning it.
		if (Input.MouseMode != Input.MouseModeEnum.Captured)
		{
			Input.MouseMode = Input.MouseModeEnum.Captured;
		}

		Vector2 axes = Input.GetVector("move_left", "move_right", "move_forward", "move_backward");
		float lift = (Input.IsActionPressed("jump") ? 1f : 0f) - (Input.IsActionPressed("crouch") ? 1f : 0f);

		if (axes == Vector2.Zero && Mathf.IsZeroApprox(lift))
		{
			return;
		}

		// Asking to move is asking for the camera back. No command, no menu: the key
		// that would have moved you is the key that frees you.
		FollowedPeer = 0;

		float speed = MoveSpeed * (Input.IsActionPressed("sprint") ? FastMultiplier : 1f) * (float)delta;
		var local = new Vector3(axes.X, lift, axes.Y);
		Position += Basis * local * speed;
	}

	public override void _UnhandledInput(InputEvent @event)
	{
		// While following somebody the view is theirs, so the mouse does nothing:
		// this is a shoulder camera and it points where they point. The movement keys
		// are what let go of it.
		if (FollowedPeer != 0 || @event is not InputEventMouseMotion motion
			|| Input.MouseMode != Input.MouseModeEnum.Captured)
		{
			return;
		}

		_yaw -= motion.Relative.X * LookSensitivity;
		_pitch = Mathf.Clamp(_pitch - (motion.Relative.Y * LookSensitivity), -Quantize.HalfPi, Quantize.HalfPi);
		ApplyLook();
	}

	private void ApplyLook() => Basis = Basis.FromEuler(new Vector3(_pitch, _yaw, 0f));
}
