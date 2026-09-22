using Gdpyr.Core;
using Gdpyr.Fps;
using Gdpyr.Sim;
using Godot;

namespace Gdpyr.Ui;

/// <summary>
/// What a scoped weapon looks like once it is up: the eyepiece, the black the rest
/// of the sight picture is behind, and the optic's own reticle.
///
/// Drawn rather than textured, like everything else in the greybox — a circle, a
/// cross and four stadia marks, sized off the shorter screen axis so it is the same
/// optic at every aspect ratio.
///
/// It covers the screen over the last part of the raise rather than all at once,
/// and over the same part of it the viewmodel leaves the shot
/// (<see cref="Fps.WeaponInit"/>): the eyepiece arrives as the gun goes. The zoom
/// itself is not here — that is the camera's, in <see cref="AimZoom"/> — and
/// neither is any decision: this polls the local player's sights and draws them
/// (docs/NETCODE.md §3.1).
/// </summary>
public partial class ScopeOverlay : Control
{
	/// <summary>
	/// How far into the raise the eyepiece starts to take the screen. Before this
	/// the player is still bringing the weapon up and wants to see past it.
	/// </summary>
	[Export]
	public float FadeInAt = 0.55f;

	/// <summary>How much of the shorter screen axis the eyepiece takes, as a radius.</summary>
	[Export]
	public float EyepieceRadius = 0.42f;

	/// <summary>Everything outside the eyepiece.</summary>
	[Export]
	public Color SurroundColor = new(0f, 0f, 0f, 1f);

	/// <summary>The optic's crosshair and stadia marks.</summary>
	[Export]
	public Color ReticleColor = new(0.05f, 0.05f, 0.05f, 1f);

	/// <summary>The rim of the eyepiece, so the black reads as a tube and not as a letterbox.</summary>
	[Export]
	public Color RimColor = new(0.15f, 0.15f, 0.15f, 1f);

	/// <summary>Segments in the circle. Enough that a 4K eyepiece has no visible facets.</summary>
	private const int ArcSegments = 96;

	private float _opacity;
	private Vector2 _drawnSize;

	public override void _Ready()
	{
		// Never in the way of a click: the pause menu and the role select sit above
		// this and have to keep receiving them.
		MouseFilter = MouseFilterEnum.Ignore;
		Visible = false;
		SetProcess(!Bootstrap.IsDedicatedServer);
	}

	public override void _Process(double delta)
	{
		float opacity = Opacity();
		if (Mathf.IsEqualApprox(opacity, _opacity) && _drawnSize == Size)
		{
			return;
		}

		_opacity = opacity;
		_drawnSize = Size;
		Visible = opacity > 0f;
		QueueRedraw();
	}

	public override void _Draw()
	{
		if (_opacity <= 0f)
		{
			return;
		}

		Vector2 size = Size;
		Vector2 centre = size * 0.5f;
		float radius = Mathf.Min(size.X, size.Y) * EyepieceRadius;
		if (radius <= 0f)
		{
			return;
		}

		Color surround = Fade(SurroundColor);

		// Everything outside the eyepiece's bounding box...
		DrawRect(new Rect2(0f, 0f, size.X, centre.Y - radius), surround);
		DrawRect(new Rect2(0f, centre.Y + radius, size.X, size.Y - centre.Y - radius), surround);
		DrawRect(new Rect2(0f, centre.Y - radius, centre.X - radius, radius * 2f), surround);
		DrawRect(new Rect2(centre.X + radius, centre.Y - radius, size.X - centre.X - radius, radius * 2f),
			surround);

		// ...and a ring thick enough to reach that box's corners, which is what turns
		// the square hole the four rectangles leave into a round one. A corner is
		// radius × √2 out, so 0.9 × radius of stroke past the rim covers it.
		float ringWidth = radius * 0.9f;
		DrawArc(centre, radius + (ringWidth * 0.5f), 0f, Mathf.Tau, ArcSegments, surround, ringWidth);

		DrawArc(centre, radius, 0f, Mathf.Tau, ArcSegments, Fade(RimColor), 2f, true);
		DrawReticle(centre, radius);
	}

	/// <summary>
	/// A duplex cross: a gap in the middle so the target is not under the lines, and
	/// stadia below the centre to hold over with.
	/// </summary>
	private void DrawReticle(Vector2 centre, float radius)
	{
		Color reticle = Fade(ReticleColor);
		float gap = radius * 0.06f;
		float width = Mathf.Max(radius * 0.004f, 1f);

		DrawLine(centre - new Vector2(radius, 0f), centre - new Vector2(gap, 0f), reticle, width, true);
		DrawLine(centre + new Vector2(gap, 0f), centre + new Vector2(radius, 0f), reticle, width, true);
		DrawLine(centre - new Vector2(0f, radius), centre - new Vector2(0f, gap), reticle, width, true);
		DrawLine(centre + new Vector2(0f, gap), centre + new Vector2(0f, radius), reticle, width, true);

		for (int mark = 1; mark <= 4; mark++)
		{
			float y = centre.Y + (radius * 0.14f * mark);
			float half = radius * (mark % 2 == 0 ? 0.05f : 0.03f);
			DrawLine(new Vector2(centre.X - half, y), new Vector2(centre.X + half, y), reticle, width, true);
		}
	}

	/// <summary>How much of the screen the optic has, from the local player's sights.</summary>
	private float Opacity()
	{
		if (!LocalAim.TryRead(out AimStats stats, out float progress) || !stats.IsScoped)
		{
			return 0f;
		}

		float fadeIn = Mathf.Clamp(FadeInAt, 0f, 0.99f);
		return Mathf.Clamp((progress - fadeIn) / (1f - fadeIn), 0f, 1f);
	}

	private Color Fade(Color color) => new(color.R, color.G, color.B, color.A * _opacity);
}
