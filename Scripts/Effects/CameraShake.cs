using Gdpyr.Sim;
using Godot;

namespace Gdpyr.Effects;

/// <summary>
/// A small shake of the camera this node is a child of, when the local player is hit
/// (docs/HURT_FEEDBACK.md §2): trauma that a hit adds to and that drains away
/// (<see cref="ShakeTrauma"/>), drawn as a turn of the camera and a slide of its picture by a
/// noise that wanders in each axis, harder the more grievous the hit.
///
/// Everything about how it moves is the scene's, <c>Effects/Hurt/CameraShake.tscn</c>, and
/// edited in the Godot editor: how much trauma a hit of each severity adds
/// (<see cref="TraumaBySeverity"/>), the noise and how fast it is read, how far the camera
/// turns and slides at full trauma, and how fast it settles.
///
/// It moves only what is drawn, never what is simulated. A shot leaves from the camera's
/// position (<c>fps_controller.EyePosition</c>) along the look's yaw and pitch
/// (<c>fps_controller.AimDirection</c>); this turns the camera about its own position, which
/// moves neither, and slides the picture by <see cref="Camera3D.HOffset"/> and
/// <see cref="Camera3D.VOffset"/>, which move the projection and not the node. The look
/// writes the camera's rotation on every simulated tick; the shake is laid over whatever the
/// look last wrote, every render frame, and the look's own rotation is put back when the
/// shake is over.
/// </summary>
public partial class CameraShake : Node
{
	/// <summary>
	/// How much trauma a hit adds: across, the hit's severity (0 a scratch, 1 a killing blow);
	/// up, trauma, 0 to 1. Trauma adds up over hits, to 1, and drains at
	/// <see cref="DecayPerSecond"/>.
	/// </summary>
	[ExportGroup("Hits")]
	[Export]
	public Curve TraumaBySeverity { get; set; }

	/// <summary>How much trauma drains a second: 2 settles a full shake in half a second.</summary>
	[Export(PropertyHint.Range, "0.1,10,0.05,suffix:/s")]
	public float DecayPerSecond { get; set; } = 2f;

	/// <summary>
	/// The power trauma is raised to for the shake: 1, a straight line; 2 or 3, light hits a
	/// twitch and heavy ones a jolt.
	/// </summary>
	[Export(PropertyHint.Range, "1,4,0.1")]
	public float Exponent { get; set; } = 2f;

	/// <summary>
	/// What the shake follows in each axis. Read along a line at <see cref="NoiseSpeed"/>, one
	/// row per axis; its own frequency scales the speed as well. None: a sine per axis.
	/// </summary>
	[ExportGroup("Motion")]
	[Export]
	public FastNoiseLite Noise { get; set; }

	/// <summary>How fast the noise is read, in noise units a second: faster is a buzz, slower a sway.</summary>
	[Export(PropertyHint.Range, "0.1,100,0.1")]
	public float NoiseSpeed { get; set; } = 22f;

	/// <summary>How far the camera turns at full trauma [°]: pitch (x), yaw (y) and roll (z).</summary>
	[Export(PropertyHint.Range, "0,15,0.05,suffix:°")]
	public Vector3 MaxAngleDegrees { get; set; } = new(1.5f, 1.5f, 4f);

	/// <summary>How far the picture slides at full trauma [m]: across (x) and up (y).</summary>
	[Export(PropertyHint.Range, "0,0.5,0.005,suffix:m")]
	public Vector2 MaxOffset { get; set; } = new(0.04f, 0.04f);

	private Camera3D _camera;
	private ShakeTrauma _trauma;
	private float _time;

	/// <summary>The look's rotation, as the simulation last wrote it, the shake is laid over.</summary>
	private Basis _rest;

	/// <summary>What this last wrote; anything else in the camera's basis was written since by the look.</summary>
	private Basis _written;

	private bool _applied;

	/// <summary>The trauma now, 0 to 1.</summary>
	public float Trauma => _trauma.Value;

	/// <summary>True while the camera is being moved.</summary>
	public bool IsShaking => _applied;

	public override void _Ready()
	{
		_camera = GetParent() as Camera3D;
		if (_camera == null)
		{
			GD.PushWarning($"[effects] {GetPath()} is not a child of a Camera3D: hits will not shake the camera");
		}

		SetProcess(false);
	}

	/// <summary>A hit of <paramref name="severity"/> (0 to 1, <see cref="HurtFeedback.Severity"/>).</summary>
	public void Hurt(float severity)
	{
		if (severity > 0f)
		{
			AddTrauma(Mathf.Clamp(TraumaBySeverity != null ? TraumaBySeverity.Sample(severity) : severity, 0f, 1f));
		}
	}

	/// <summary>Adds <paramref name="amount"/> of trauma, 0 to 1, and starts the shake.</summary>
	public void AddTrauma(float amount)
	{
		if (_camera == null || amount <= 0f)
		{
			return;
		}

		_trauma.Add(amount);
		SetProcess(true);
	}

	/// <summary>Stops it at once and gives the camera back to the look.</summary>
	public void Clear()
	{
		_trauma.Clear();
		Restore();
		SetProcess(false);
	}

	public override void _Process(double delta)
	{
		float dt = (float)delta;
		_trauma.Decay(dt, DecayPerSecond);
		if (!_trauma.IsActive || _camera == null)
		{
			Clear();
			return;
		}

		_time += dt;
		float shake = _trauma.Shake(Exponent);
		Vector3 degrees = MaxAngleDegrees * shake;
		var turn = new Vector3(
			Mathf.DegToRad(degrees.X * Channel(0)),
			Mathf.DegToRad(degrees.Y * Channel(1)),
			Mathf.DegToRad(degrees.Z * Channel(2)));

		Basis now = _camera.Basis;
		if (!_applied || now != _written)
		{
			_rest = now;
		}

		_camera.Basis = _rest * Basis.FromEuler(turn);
		_written = _camera.Basis;
		_camera.HOffset = MaxOffset.X * shake * Channel(3);
		_camera.VOffset = MaxOffset.Y * shake * Channel(4);
		_applied = true;
	}

	/// <summary>One axis of the shake now, about −1 to 1.</summary>
	private float Channel(int axis)
	{
		float x = _time * NoiseSpeed;
		return Noise != null
			? Mathf.Clamp(Noise.GetNoise2D(x, axis * 61.7f) * 1.4f, -1f, 1f)
			: Mathf.Sin((x * (0.9f + (0.23f * axis))) + (axis * 1.7f));
	}

	/// <summary>
	/// Puts back the look's rotation, unless the look has written one since, and the picture
	/// where it was.
	/// </summary>
	private void Restore()
	{
		if (!_applied || _camera == null)
		{
			return;
		}

		if (_camera.Basis == _written)
		{
			_camera.Basis = _rest;
		}

		_camera.HOffset = 0f;
		_camera.VOffset = 0f;
		_applied = false;
	}
}
