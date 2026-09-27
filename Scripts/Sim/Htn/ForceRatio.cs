using System;

namespace Gdpyr.Sim.Htn;

/// <summary>
/// Who has the numbers, as one of three bands (docs/HTN_BOTS.md §5.1, §5.2). A
/// planner fact, so it is a byte, and it changes only when the band does: a change
/// in world state is a replan (§3.7).
/// </summary>
public enum OddsBand : byte
{
	/// <summary>Ours are at least twice theirs, or there are none of theirs.</summary>
	Favourable = 0,

	/// <summary>Neither side has twice the other.</summary>
	Even = 1,

	/// <summary>Theirs are at least twice ours (§5.1: "local hostiles ≥ 2 × friends").</summary>
	Outnumbered = 2,
}

/// <summary>
/// The arithmetic the boards share (docs/HTN_BOTS.md §4.3): what one body is worth
/// to a strength estimate, and which band a ratio of two falls in.
/// </summary>
public static class ForceRatio
{
	/// <summary>The ratio that enters <see cref="OddsBand.Favourable"/> or <see cref="OddsBand.Outnumbered"/>.</summary>
	public const float EnterRatio = 2f;

	/// <summary>
	/// The ratio a band is held down to once it has been entered. Hysteresis, as
	/// <c>UnitBrain.ShouldDropTarget</c> has for range: a fight that swings between
	/// 2:1 and 1.9:1 from one scan to the next must not replan on every scan
	/// (§9, plan thrash).
	/// </summary>
	public const float HoldRatio = 1.5f;

	/// <summary>
	/// The band <paramref name="ours"/> against <paramref name="theirs"/> is in, given
	/// the band it was in last time. Counts or strengths, as long as both sides are
	/// measured the same way. Nobody on either side is <see cref="OddsBand.Even"/>.
	/// </summary>
	public static OddsBand Band(float ours, float theirs, OddsBand previous)
	{
		ours = MathF.Max(ours, 0f);
		theirs = MathF.Max(theirs, 0f);

		float outnumbered = previous == OddsBand.Outnumbered ? HoldRatio : EnterRatio;
		if (theirs > 0f && theirs >= ours * outnumbered)
		{
			return OddsBand.Outnumbered;
		}

		float favourable = previous == OddsBand.Favourable ? HoldRatio : EnterRatio;
		if (ours > 0f && ours >= theirs * favourable)
		{
			return OddsBand.Favourable;
		}

		return OddsBand.Even;
	}

	/// <summary>
	/// What one body is worth to a strength estimate: what it cost, scaled by what is
	/// left of it (§4.3, "cost-weighted"). A tank at half health is worth half a
	/// tank, and still more than a rifleman at full.
	/// </summary>
	public static float Strength(float cost, float healthFraction) =>
		MathF.Max(cost, 0f) * Math.Clamp(healthFraction, 0f, 1f);
}
