using Gdpyr.Core;
using Gdpyr.Sim;
using Godot;

namespace Gdpyr.Fps;

/// <summary>
/// The one place the simulation is allowed to touch the <c>Input</c> singleton:
/// the boundary where device state becomes an <see cref="InputFrame"/>
/// (docs/NETCODE.md §3.1). Everything downstream — prediction, replay, the
/// server — works from the recorded frames alone.
///
/// The combat buttons M1 reserved on the wire (fire, aim, reload, melee) are
/// sampled from M2 on, and the use bit from M5, when heavy guns gave it something
/// to mean (docs/IMPLEMENTATION_PLAN.md §M5); nothing else about the boundary
/// changed.
///
/// Look angles are accumulated here and sent as absolute, already-quantized
/// values. Absolute, because replaying a delta would apply it again; quantized,
/// because the client must predict with exactly the angle the server will
/// simulate.
/// </summary>
public partial class LocalInputSampler : Node
{
	/// <summary>
	/// Radians per pixel of mouse motion, per tick. The pre-M1 controller turned by
	/// (delta × sensitivity × frame time), so turn rate depended on framerate;
	/// scaling by the fixed tick delta keeps the tuned feel and drops the
	/// dependency.
	/// </summary>
	[Export]
	public float MouseSensitivity = 0.1f;

	public float Yaw { get; private set; }

	public float Pitch { get; private set; }

	/// <summary>Mouse motion accumulated since the last sample, in pixels.</summary>
	private Vector2 _mouseDelta;

	private bool _enabled;

	public override void _Ready()
	{
		// A dedicated server has no device to sample and no window to capture.
		_enabled = !Bootstrap.IsDedicatedServer;
		SetProcessUnhandledInput(_enabled);

		// Motion has to keep accumulating while the tree is paused so that unpausing
		// does not deliver a frame's worth of backlog as one enormous turn.
		ProcessMode = ProcessModeEnum.Always;
	}

	public override void _UnhandledInput(InputEvent @event)
	{
		if (@event is InputEventMouseMotion motion && Input.MouseMode == Input.MouseModeEnum.Captured)
		{
			_mouseDelta += motion.Relative;
		}
	}

	/// <summary>Points the sampler at a character's spawn orientation.</summary>
	public void Align(float yaw, float pitch)
	{
		Yaw = yaw;
		Pitch = pitch;
		_mouseDelta = Vector2.Zero;
	}

	public InputFrame Sample(uint tick)
	{
		// Paused means neutral input, not "no input frame": the server keeps
		// simulating either way, and a missing frame reads to it as packet loss.
		if (!_enabled || GetTree().Paused)
		{
			_mouseDelta = Vector2.Zero;
			return InputFrame.Create(tick, 0f, 0f, Yaw, Pitch, InputButtons.None);
		}

		Yaw -= _mouseDelta.X * MouseSensitivity * SimConfig.TickDelta;
		Pitch = Mathf.Clamp(Pitch - (_mouseDelta.Y * MouseSensitivity * SimConfig.TickDelta),
			-Quantize.HalfPi, Quantize.HalfPi);
		_mouseDelta = Vector2.Zero;

		Vector2 axes = Input.GetVector("move_left", "move_right", "move_forward", "move_backward");

		InputButtons buttons = InputButtons.None;
		if (Input.IsActionPressed("jump")) { buttons |= InputButtons.Jump; }
		if (Input.IsActionPressed("crouch")) { buttons |= InputButtons.Crouch; }
		if (Input.IsActionPressed("sprint")) { buttons |= InputButtons.Sprint; }
		if (Input.IsActionPressed("fire")) { buttons |= InputButtons.Fire; }
		if (Input.IsActionPressed("aim")) { buttons |= InputButtons.Ads; }
		if (Input.IsActionPressed("reload")) { buttons |= InputButtons.Reload; }
		if (Input.IsActionPressed("melee")) { buttons |= InputButtons.Melee; }
		if (Input.IsActionPressed("use")) { buttons |= InputButtons.Use; }
		if (Input.IsActionPressed("weapon_melee")) { buttons |= InputButtons.Weapon1; }
		if (Input.IsActionPressed("weapon_sidearm")) { buttons |= InputButtons.Weapon2; }
		if (Input.IsActionPressed("weapon_large")) { buttons |= InputButtons.Weapon3; }

		InputFrame frame = InputFrame.Create(tick, axes.X, axes.Y, Yaw, Pitch, buttons);

		// Predict with the quantized angles, not the raw ones: otherwise every tick
		// mispredicts by the rounding error.
		Yaw = frame.YawRadians;
		Pitch = frame.PitchRadians;
		return frame;
	}
}
