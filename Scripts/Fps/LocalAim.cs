using Gdpyr.Match;
using Gdpyr.Sim;

namespace Gdpyr.Fps;

/// <summary>
/// Where the local player's sights are, for the four things that draw them: the
/// camera's field of view (<see cref="AimZoom"/>), the viewmodel
/// (<see cref="WeaponInit"/>), the scope's surround (<see cref="Ui.ScopeOverlay"/>)
/// and the hip reticle (<see cref="Ui.Reticle"/>). The input sampler reads it too,
/// because zoom and turn rate have to move together (<see cref="Ads.LookScale"/>).
///
/// All of them poll, and none of them may write: the state itself is advanced once
/// per tick, inside the simulation, from the recorded input frame
/// (<see cref="Match.CombatManager.ClientSimulateLocal"/>). This is the same
/// arrangement the viewmodel already had for which weapon is in shot — read the
/// simulation on the render frame, never call back into it (docs/NETCODE.md §3.1).
/// </summary>
public static class LocalAim
{
	/// <summary>
	/// The local player's sights, or <see cref="AimStats.None"/> at zero progress
	/// when there is no local player at all — a dedicated server, or a client that
	/// has not been registered yet.
	/// </summary>
	public static bool TryRead(out AimStats stats, out float progress)
	{
		PlayerCombat local = CombatManager.Instance?.Local;
		if (local == null)
		{
			stats = AimStats.None;
			progress = 0f;
			return false;
		}

		stats = local.EquippedAim;
		progress = Ads.Progress(local.Aim, stats);
		return true;
	}

	/// <summary>How far up the local player's sights are, from 0 at the hip to 1 fully aimed.</summary>
	public static float Progress
	{
		get
		{
			TryRead(out _, out float progress);
			return progress;
		}
	}

	/// <summary>
	/// What to multiply sampled mouse motion by, so that a pixel of travel covers
	/// the same distance on screen at every magnification.
	/// </summary>
	public static float LookScale
	{
		get
		{
			TryRead(out AimStats stats, out float progress);
			return Ads.LookScale(stats, progress);
		}
	}
}
