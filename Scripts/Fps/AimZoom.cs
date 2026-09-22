using Gdpyr.Core;
using Gdpyr.Sim;
using Godot;

namespace Gdpyr.Fps;

/// <summary>
/// The first-person camera, plus the one thing a scope does to it: narrows the
/// field of view while the sights are up (<see cref="Ads"/>).
///
/// Rendering-only work on the render frame, like the viewmodel's sway and the
/// correction offset the character is drawn with (docs/NETCODE.md §3.1). Nothing
/// here feeds the simulation — the progress it reads was advanced by the
/// simulation from the recorded input frame — and nothing here is anyone else's
/// business: a player's own optic is not on the wire and not on anybody else's
/// screen.
///
/// The hip field of view is whatever the scene was authored with, captured once.
/// While the sights are down this script does not touch <see cref="Camera3D.Fov"/>
/// at all, which is what leaves the slide animation's FOV kick alone; it writes
/// the hip value back exactly once on the way down so that a raise interrupted
/// part-way cannot leave the camera a fraction of a degree off.
/// </summary>
public partial class AimZoom : Camera3D
{
	private float _hipFovDegrees = 75f;
	private bool _zoomed;

	public override void _Ready()
	{
		_hipFovDegrees = Fov;

		// One camera per process draws a first-person view, and a dedicated server
		// draws none (docs/DEPLOYMENT.md §2). Every other character's camera is in the
		// tree and is not current; it has no sights of its own to answer for.
		var character = OwningCharacter();
		SetProcess(!Bootstrap.IsDedicatedServer && character is { IsLocalPlayer: true });
	}

	public override void _Process(double delta)
	{
		if (!LocalAim.TryRead(out AimStats stats, out float progress) || progress <= 0f)
		{
			if (_zoomed)
			{
				Fov = _hipFovDegrees;
				_zoomed = false;
			}
			return;
		}

		_zoomed = true;
		Fov = Ads.FovDegrees(_hipFovDegrees, stats, progress);
	}

	/// <summary>
	/// The character this camera belongs to. Walked rather than exported because the
	/// camera sits under a controller node the crouch and slide animations move, and
	/// a node path would have to be kept in step with that.
	/// </summary>
	private fps_controller OwningCharacter()
	{
		for (Node node = GetParent(); node != null; node = node.GetParent())
		{
			if (node is fps_controller character)
			{
				return character;
			}
		}
		return null;
	}
}
