using Gdpyr.Core;
using Gdpyr.Sim;
using Godot;

namespace Gdpyr.Ui.Spectator;

/// <summary>
/// What a spectator looks through (docs/AI_DEBUG.md §2.2). <see cref="DemoCamera"/>'s
/// flight, with the pointer left free: a spectator clicks on things to inspect them,
/// so the mouse is a cursor until the right button is held, and only then a look.
///
/// It can sit behind something — a bot, a unit, a squad's middle — and the movement
/// keys let go of it, as the demo camera's do.
/// </summary>
public partial class SpectatorCamera : Camera3D
{
	/// <summary>Metres per second at a walk.</summary>
	private const float MoveSpeed = 14f;

	/// <summary>What holding sprint multiplies that by.</summary>
	private const float FastMultiplier = 4f;

	/// <summary>Radians per pixel of mouse motion while the right button is held.</summary>
	private const float LookSensitivity = 0.003f;

	/// <summary>How far from a followed thing the camera orbits it.</summary>
	private const float FollowDistance = 14f;

	/// <summary>Followed things are looked at a little above their feet.</summary>
	private static readonly Vector3 FollowLift = new(0f, 1.2f, 0f);

	private float _yaw;
	private float _pitch;
	private bool _hasFollow;
	private Vector3 _followPoint;

	/// <summary>True while the right button is held: the mouse looks rather than points.</summary>
	public bool Looking { get; private set; }

	/// <summary>True while something is being followed.</summary>
	public bool Following => _hasFollow;

	public override void _Ready()
	{
		ProcessMode = ProcessModeEnum.Always;
		Far = 600f;
		Current = true;

		// Above the middle of the map, looking down at it: a spectator who has just
		// stepped off the field starts with the field in shot.
		Position = new Vector3(0f, 45f, 55f);
		_pitch = -0.75f;
		ApplyLook();
	}

	/// <summary>Starts orbiting <paramref name="point"/>; <see cref="MoveFollow"/> keeps it up to date.</summary>
	public void StartFollow(Vector3 point)
	{
		_pitch = -0.45f;
		ApplyLook();
		_hasFollow = true;
		_followPoint = point;
	}

	/// <summary>Where the followed thing is now. Ignored once the movement keys have let go of it.</summary>
	public void MoveFollow(Vector3 point)
	{
		if (_hasFollow)
		{
			_followPoint = point;
		}
	}

	public void StopFollow() => _hasFollow = false;

	/// <summary>Puts the camera above <paramref name="point"/>, looking at it, without following it.</summary>
	public void LookAtFromAbove(Vector3 point)
	{
		_hasFollow = false;
		_pitch = -0.8f;
		ApplyLook();
		Position = point + (Basis * new Vector3(0f, 0f, 30f));
	}

	public override void _Process(double delta)
	{
		// Something else may have taken the view — a character spawned on this frame and
		// claimed it. While this camera exists the view is a spectator's.
		if (!Current)
		{
			Current = true;
		}

		if (InputFocus.TextEntry)
		{
			Looking = false;
			return;
		}

		Looking = Input.IsMouseButtonPressed(MouseButton.Right) && !GetTree().Paused;

		Vector2 axes = Input.GetVector("move_left", "move_right", "move_forward", "move_backward");
		float lift = (Input.IsActionPressed("jump") ? 1f : 0f) - (Input.IsActionPressed("crouch") ? 1f : 0f);

		if (axes != Vector2.Zero || !Mathf.IsZeroApprox(lift))
		{
			// The keys that would move you are the keys that free you.
			_hasFollow = false;
			float speed = MoveSpeed * (Input.IsActionPressed("sprint") ? FastMultiplier : 1f) * (float)delta;
			Position += Basis * new Vector3(axes.X, lift, axes.Y) * speed;
			return;
		}

		if (_hasFollow)
		{
			// An orbit: the camera's own heading, not the followed thing's, so a unit
			// turning on the spot does not swing the view round, and dragging with the
			// right button held walks the camera round it.
			Vector3 orbit = _followPoint + FollowLift + (Basis * new Vector3(0f, 0f, FollowDistance));
			Position = Position.Lerp(orbit, 1f - Mathf.Exp(-8f * (float)delta));
		}
	}

	public override void _UnhandledInput(InputEvent @event)
	{
		if (!Looking || @event is not InputEventMouseMotion motion)
		{
			return;
		}

		_yaw -= motion.Relative.X * LookSensitivity;
		_pitch = Mathf.Clamp(_pitch - (motion.Relative.Y * LookSensitivity), -Quantize.HalfPi, Quantize.HalfPi);
		ApplyLook();
		GetViewport().SetInputAsHandled();
	}

	private void ApplyLook() => Basis = Basis.FromEuler(new Vector3(_pitch, _yaw, 0f));
}
