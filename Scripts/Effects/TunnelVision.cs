using Gdpyr.Sim;
using Godot;

namespace Gdpyr.Effects;

/// <summary>
/// The red tunnel vision a hit on the local player draws (docs/HURT_FEEDBACK.md §3): a
/// full-screen rect whose shader (<c>Effects/Hurt/tunnel_vision.gdshader</c>) closes a red
/// edge in from the rim, deeper the more grievous the hit, and lets it go again over a time
/// that is longer the deeper it went.
///
/// Everything about how it looks is the scene's, <c>Effects/Hurt/TunnelVision.tscn</c>, and
/// edited in the Godot editor: the shader's colours, shape and motion under its material,
/// and on this node how deep a hit of each severity takes it (<see cref="DepthBySeverity"/>),
/// the shape of the fade (<see cref="Envelope"/>), how long it lasts and how hits stack. This
/// script only turns a hit's severity (<see cref="HurtFeedback.Severity"/>) into the shader's
/// <c>intensity</c>, every frame while it shows, and hides the rect when there is nothing to
/// show, so it costs nothing between hits.
///
/// Presentation only, on the render frame: nothing here feeds the simulation.
/// </summary>
public partial class TunnelVision : ColorRect
{
	/// <summary>
	/// How deep a hit takes the tunnel: across, the hit's severity (0 a scratch, 1 a killing
	/// blow); up, the shader's <c>intensity</c> at the top of its fade.
	/// </summary>
	[ExportGroup("Depth")]
	[Export]
	public Curve DepthBySeverity { get; set; }

	/// <summary>
	/// How much of the shallower of two overlapping showings is added to the deeper
	/// (<see cref="HurtFeedback.Stack"/>): 0, a hit only ever replaces a shallower one; 1, they add.
	/// </summary>
	[Export(PropertyHint.Range, "0,1,0.01")]
	public float Stacking { get; set; } = 0.5f;

	/// <summary>
	/// The shape of a showing over its time: across, 0 as the hit lands to 1 as it is gone; up,
	/// the share of its depth shown then. Should end at 0, or it will be cut off there.
	/// </summary>
	[ExportGroup("Fade")]
	[Export]
	public Curve Envelope { get; set; }

	/// <summary>How long the lightest hit's showing lasts [s].</summary>
	[Export(PropertyHint.Range, "0.05,10,0.05,suffix:s")]
	public float ShortestSeconds { get; set; } = 0.8f;

	/// <summary>How long the deepest showing lasts [s]; one in between, in proportion.</summary>
	[Export(PropertyHint.Range, "0.05,10,0.05,suffix:s")]
	public float LongestSeconds { get; set; } = 3f;

	/// <summary>The shader parameter this writes the depth into.</summary>
	[ExportGroup("Shader")]
	[Export]
	public StringName IntensityParameter { get; set; } = "intensity";

	private HurtPulse _pulse;
	private ShaderMaterial _material;

	/// <summary>How deep it is drawn now, 0 to 1: the shader's <c>intensity</c>.</summary>
	public float Level { get; private set; }

	/// <summary>How deep the showing now up went at its top, 0 when there is none.</summary>
	public float Peak => _pulse.IsActive ? _pulse.Peak : 0f;

	/// <summary>How long the showing now up lasts in all [s], 0 when there is none.</summary>
	public float Seconds => _pulse.IsActive ? _pulse.Seconds : 0f;

	public override void _Ready()
	{
		// Never in the way of a click: the pause menu sits above it.
		MouseFilter = MouseFilterEnum.Ignore;
		_material = Material as ShaderMaterial;
		if (_material == null)
		{
			GD.PushWarning($"[effects] {GetPath()} has no ShaderMaterial: hits will draw no tunnel vision");
		}

		ShowLevel(0f);
		SetProcess(false);
	}

	/// <summary>
	/// A hit of <paramref name="severity"/> (0 to 1, <see cref="HurtFeedback.Severity"/>): the
	/// tunnel goes as deep as its curve says, on top of whatever is still showing, and starts
	/// its fade again.
	/// </summary>
	public void Hurt(float severity)
	{
		if (severity <= 0f)
		{
			return;
		}

		float depth = Sample(DepthBySeverity, severity, severity);
		float peak = HurtFeedback.Stack(Level, depth, Stacking);
		if (peak <= 0f)
		{
			return;
		}

		_pulse.Start(peak, HurtFeedback.Seconds(peak, ShortestSeconds, LongestSeconds));
		ShowLevel(Current());
		SetProcess(_pulse.IsActive);
	}

	/// <summary>Takes it off the screen at once: a respawn, or a player leaving the field.</summary>
	public void Clear()
	{
		_pulse.Clear();
		ShowLevel(0f);
		SetProcess(false);
	}

	public override void _Process(double delta)
	{
		_pulse.Advance((float)delta);
		if (!_pulse.IsActive)
		{
			Clear();
			return;
		}

		ShowLevel(Current());
	}

	/// <summary>The depth the showing now up is at: its peak, times its fade curve where it has got to.</summary>
	private float Current() =>
		_pulse.IsActive ? _pulse.Peak * Sample(Envelope, _pulse.Progress, 1f - _pulse.Progress) : 0f;

	private void ShowLevel(float level)
	{
		Level = Mathf.Clamp(level, 0f, 1f);
		_material?.SetShaderParameter(IntensityParameter, Level);
		Visible = Level > 0f;
	}

	/// <summary><paramref name="curve"/> at <paramref name="x"/>, clamped to 0–1; <paramref name="fallback"/> with no curve.</summary>
	private static float Sample(Curve curve, float x, float fallback) =>
		Mathf.Clamp(curve != null ? curve.Sample(x) : fallback, 0f, 1f);
}
