using System;

namespace Gdpyr.Sim;

/// <summary>
/// What raising one weapon's sights does: how much it magnifies, how long it
/// takes, and whether the player ends up looking through an optic or over irons.
///
/// Expressed here in ticks and in a magnification factor rather than in seconds
/// and a field of view, for the reason <see cref="WeaponStats"/> is: the raise has
/// to be a whole number of ticks to be reproducible from the recorded frames
/// alone (docs/NETCODE.md §3.1), and a magnification survives a player changing
/// their field of view where a hard-coded aimed FOV does not.
/// </summary>
public readonly struct AimStats
{
	/// <summary>
	/// How much the view is magnified when the sights are all the way up. 1 is a
	/// weapon with nothing to look through, and <see cref="CanAim"/> is false for it:
	/// the mounted guns and anything nobody has given sights to are in that case.
	/// </summary>
	public readonly float Magnification;

	/// <summary>Ticks from the hip to fully aimed, and back down again.</summary>
	public readonly int RaiseTicks;

	/// <summary>
	/// True for a weapon aimed through an optic rather than over its own sights.
	/// The difference is presentation and nothing else — the scope's surround takes
	/// the screen and the viewmodel goes with it, where irons simply come up.
	/// </summary>
	public readonly bool IsScoped;

	public AimStats(float magnification, int raiseTicks, bool isScoped)
	{
		Magnification = MathF.Max(magnification, 1f);
		RaiseTicks = Math.Clamp(raiseTicks, 1, Ads.MaxRaiseTicks);
		IsScoped = isScoped;
	}

	/// <summary>A weapon there is nothing to raise: a swing, or a gun in nobody's hands.</summary>
	public static AimStats None => default;

	/// <summary>
	/// Whether this weapon aims at all. <see cref="None"/> is <c>default</c>, whose
	/// zero magnification and zero raise both fail this — a struct that skipped the
	/// constructor is the one case the constructor's clamps do not cover.
	/// </summary>
	public bool CanAim => Magnification > 1f && RaiseTicks > 0;
}

/// <summary>
/// How far up one player's sights are, in ticks of the raise.
///
/// A byte, and not a fraction: like <see cref="WeaponState"/> it is advanced by a
/// pure step over the recorded input, so the server and the owning client reach
/// the same number without it ever going on the wire.
/// </summary>
public struct AimState
{
	/// <summary>Ticks into the raise, from 0 (hip) to the weapon's <see cref="AimStats.RaiseTicks"/>.</summary>
	public byte Ticks;
}

/// <summary>
/// Aiming down the sights: the state behind the <see cref="InputButtons.Ads"/> bit
/// that has been on the wire, unread, since M1.
///
/// A pure function of (state, stats, input) for the same reason
/// <see cref="WeaponSim"/> is (docs/NETCODE.md §3.1) — no wall clock, no render
/// frame and no <c>Input</c> — even though nothing authoritative reads the result
/// yet. What it decides today is what the local player sees: the camera's field of
/// view, where the viewmodel is held, whether the scope's surround is up, and how
/// far the mouse turns them. All four are presentation, and all four are driven
/// from the recorded frame rather than from the device, so a paused or a dead
/// player's sights behave like everything else that reads an
/// <see cref="InputFrame"/>.
///
/// Zoom and look sensitivity move together on purpose: a four-power scope turned
/// at the hip's radians-per-pixel sweeps four times as much of what the player can
/// see, which makes the magnification worse than useless. Dividing the sampled
/// motion by the magnification in force keeps a pixel of mouse travel worth the
/// same distance on screen at every power (<see cref="LookScale"/>).
/// </summary>
public static class Ads
{
	/// <summary>Ceiling on a raise, in ticks: <see cref="AimState.Ticks"/> is a byte.</summary>
	public const int MaxRaiseTicks = 255;

	private const float DegreesToRadians = MathF.PI / 180f;
	private const float RadiansToDegrees = 180f / MathF.PI;

	/// <summary>
	/// Advances the raise by one tick. Held means up, anything else means down, at
	/// the same rate in both directions.
	/// </summary>
	public static void Step(ref AimState state, in AimStats stats, in InputContext input)
	{
		if (!stats.CanAim)
		{
			// Nothing to raise, so nothing to lower gradually either: switching to the
			// hammer or mounting a heavy gun puts the sights away in the same instant
			// it changes what is in the player's hands.
			state.Ticks = 0;
			return;
		}

		// A swap mid-raise keeps how far up the sights are and not how many ticks of
		// the last weapon's raise that took, so a slow optic swapped for a fast one is
		// already up rather than spending the difference coming down.
		int ticks = Math.Min(state.Ticks, stats.RaiseTicks);
		ticks += input.Held(InputButtons.Ads) ? 1 : -1;
		state.Ticks = (byte)Math.Clamp(ticks, 0, stats.RaiseTicks);
	}

	/// <summary>How far up the sights are, from 0 at the hip to 1 fully aimed.</summary>
	public static float Progress(in AimState state, in AimStats stats) =>
		stats.CanAim ? Math.Clamp(state.Ticks / (float)stats.RaiseTicks, 0f, 1f) : 0f;

	/// <summary>
	/// The magnification actually in force part-way through a raise. Linear in the
	/// progress, which is what makes the raise feel like one movement rather than
	/// like a zoom that arrives all at once at the end.
	/// </summary>
	public static float Magnification(in AimStats stats, float progress) =>
		1f + ((MathF.Max(stats.Magnification, 1f) - 1f) * Math.Clamp(progress, 0f, 1f));

	/// <summary>
	/// The field of view that magnification comes to, given the one the weapon is
	/// carried at.
	///
	/// Interpolated through the tangent rather than through the angle: magnifying by
	/// two means half the *extent* on screen, and lerping the angle instead would
	/// make a four-power scope show appreciably more than a quarter of what the hip
	/// does.
	/// </summary>
	public static float FovDegrees(float hipFovDegrees, in AimStats stats, float progress)
	{
		float magnification = Magnification(stats, progress);
		if (magnification <= 1f)
		{
			return hipFovDegrees;
		}

		float halfHip = Math.Clamp(hipFovDegrees, 1f, 179f) * 0.5f * DegreesToRadians;
		return 2f * MathF.Atan(MathF.Tan(halfHip) / magnification) * RadiansToDegrees;
	}

	/// <summary>
	/// What to multiply sampled mouse motion by so that a pixel of travel is worth
	/// the same distance on screen aimed as it is at the hip. Exactly the reciprocal
	/// of the magnification, because <see cref="FovDegrees"/> defines magnification
	/// as the ratio of the two tangents.
	/// </summary>
	public static float LookScale(in AimStats stats, float progress) => 1f / Magnification(stats, progress);
}
