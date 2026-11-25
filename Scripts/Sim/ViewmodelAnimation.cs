using System;

namespace Gdpyr.Sim;

/// <summary>
/// The clips an animated viewmodel is drawn with (docs/VIEWMODELS.md §3): what its glTF names
/// <c>idle</c>, <c>fire</c> and <c>reload</c>.
/// </summary>
public enum ViewmodelClip : byte
{
	/// <summary>Held, breathing. Looped on the render clock: nothing in the simulation says where it is.</summary>
	Idle,

	/// <summary>One round's kick and trigger, played once from the tick the round was fired.</summary>
	Fire,

	/// <summary>The whole reload, stretched over however long the weapon's reload is.</summary>
	Reload,
}

/// <summary>Which clip a viewmodel shows, and how far into it, from 0 to 1.</summary>
public readonly struct ViewmodelFrame
{
	public readonly ViewmodelClip Clip;
	public readonly float Progress;

	public ViewmodelFrame(ViewmodelClip clip, float progress)
	{
		Clip = clip;
		Progress = progress;
	}

	public static ViewmodelFrame Idle => new(ViewmodelClip.Idle, 0f);
}

/// <summary>
/// A viewmodel drawn at a field of view of its own (BaseMaterial3D.fov_override) is drawn as if
/// through another lens than the camera's: anything placed by where the viewmodel is drawn — the
/// muzzle flash — has to be moved to where the camera would show it in the same place.
/// </summary>
public static class ViewmodelOptics
{
	/// <summary>
	/// How much farther from the line of sight a point in front of the camera has to be, at the
	/// same depth, for the camera at <paramref name="cameraFovDegrees"/> to draw it where a
	/// viewmodel at <paramref name="viewmodelFovDegrees"/> draws it: the ratio of the two half
	/// angles' tangents. Both vertical (the camera keeps its height). 1 for any nonsense.
	/// </summary>
	public static float Spread(float cameraFovDegrees, float viewmodelFovDegrees)
	{
		if (cameraFovDegrees <= 0f || cameraFovDegrees >= 180f || viewmodelFovDegrees <= 0f
			|| viewmodelFovDegrees >= 180f)
		{
			return 1f;
		}

		const float halfDegreesToRadians = MathF.PI / 360f;
		return MathF.Tan(cameraFovDegrees * halfDegreesToRadians) / MathF.Tan(viewmodelFovDegrees * halfDegreesToRadians);
	}
}

/// <summary>
/// What the local player's viewmodel is doing, read from their weapon's simulated state on the
/// render frame — the arrangement the sights already have (<c>LocalAim</c>): the viewmodel polls,
/// never pushes, and is presentation only (docs/NETCODE.md §3.1).
///
/// A reload is a span of ticks the state already holds (<see cref="WeaponState.ReloadEndTick"/>
/// and the weapon's <see cref="WeaponStats.ReloadTicks"/>), so the clip is placed by where in
/// that span the drawn tick is: a reload corrected by the server, a hitch, or a weapon whose
/// reload is longer than the clip all show the barrel where the simulation has it. A shot is
/// an edge rather than a span: it is seen as <see cref="WeaponState.ShotIndex"/> going up, and
/// dated from <see cref="WeaponState.NextFireTick"/>, which the step that fired it set one
/// cadence ahead of the shot.
///
/// Engine-free, so that "a weapon just picked up does not play its recoil" and "the reload is
/// half done half way through" are answered by a test rather than by looking.
/// </summary>
public struct ViewmodelAnimator
{
	private bool _primed;
	private byte _definition;
	private uint _shotIndex;
	private uint _shotTick;
	private bool _hasShot;

	/// <summary>
	/// The clip for the drawn tick <paramref name="now"/>: the simulation's tick plus how far the
	/// render frame is into the next one, in the same ticks as the state's.
	/// <paramref name="fireSeconds"/> is the fire clip's length: how long a shot shows for.
	/// </summary>
	public ViewmodelFrame Step(in WeaponState state, in WeaponStats stats, double now, float fireSeconds)
	{
		Observe(state, stats);

		if (state.IsReloading && stats.ReloadTicks > 0)
		{
			double remaining = state.ReloadEndTick - now;
			float progress = (float)Math.Clamp(1.0 - remaining / stats.ReloadTicks, 0.0, 1.0);
			return new ViewmodelFrame(ViewmodelClip.Reload, progress);
		}

		if (_hasShot && fireSeconds > 0f)
		{
			double seconds = (now - _shotTick) / SimConfig.TickRate;
			if (seconds >= 0.0 && seconds < fireSeconds)
			{
				return new ViewmodelFrame(ViewmodelClip.Fire, (float)(seconds / fireSeconds));
			}
		}

		return ViewmodelFrame.Idle;
	}

	/// <summary>
	/// Notes a shot when the shot count goes up. A different weapon, or a count that went down —
	/// a respawn, a resupplied kit — is adopted silently: a weapon is not fired by being drawn.
	/// </summary>
	private void Observe(in WeaponState state, in WeaponStats stats)
	{
		if (!_primed || state.DefinitionId != _definition || state.ShotIndex < _shotIndex)
		{
			_primed = true;
			_definition = state.DefinitionId;
			_shotIndex = state.ShotIndex;
			_hasShot = false;
			return;
		}

		if (state.ShotIndex == _shotIndex)
		{
			return;
		}

		_shotIndex = state.ShotIndex;
		_hasShot = true;
		_shotTick = state.NextFireTick >= (uint)stats.TicksBetweenShots
			? state.NextFireTick - (uint)stats.TicksBetweenShots
			: 0u;
	}
}
