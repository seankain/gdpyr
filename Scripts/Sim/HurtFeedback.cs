using System;

namespace Gdpyr.Sim;

/// <summary>
/// What a hit on the local player is felt as (docs/HURT_FEEDBACK.md): how grievous it was, as
/// one number from 0 to 1 that the camera shake and the tunnel vision read their curves at,
/// and how hits that land close together add up.
///
/// What it looks like is not here: the curves, the colours, the shake's reach and the fade
/// are the effects' scenes (<c>Effects/Hurt/</c>), edited in the Godot editor. This is the
/// part with an answer — a rifle round is lighter than a DMR's, a vest softens a hit, a
/// killing blow is the worst there is, two hits are worse than one — kept engine-free so the
/// answers are tested (Tests/HurtFeedbackTests.cs). Presentation only: it reads health the
/// simulation already lost and writes nothing back.
/// </summary>
public static class HurtFeedback
{
	/// <summary>
	/// What a point the vest took counts for against a point of health. Less than one: the
	/// vest is why the hit hurt less, and the feedback says so; more than none: a hit the vest
	/// soaked is still a hit, and the player should know they are being shot.
	/// </summary>
	public const float VestWeight = 0.35f;

	/// <summary>
	/// How grievous a hit was, 0 to 1: the health it took as a share of a full life's, and the
	/// vest's points at <see cref="VestWeight"/> of that, to 1. A killing blow is 1, whatever it
	/// took — the last 5 points of a life are not a light hit. Nothing lost is 0, and a fall
	/// in either that is negative (a locker's resupply in the same record) counts as none.
	/// </summary>
	public static float Severity(int healthLost, int vestLost, bool killed, int maxHealth = SimConfig.MaxHealth)
	{
		if (killed)
		{
			return 1f;
		}

		if (maxHealth <= 0)
		{
			return 0f;
		}

		float felt = Math.Max(healthLost, 0) + (VestWeight * Math.Max(vestLost, 0));
		return Math.Clamp(felt / maxHealth, 0f, 1f);
	}

	/// <summary>
	/// What an effect still at <paramref name="current"/> goes to when a hit asking for
	/// <paramref name="incoming"/> lands, both 0 to 1: the larger of the two, and
	/// <paramref name="stacking"/> of the smaller on top, to 1. Hits close together deepen it
	/// rather than replace it, and no hit makes it shallower than it was.
	/// </summary>
	public static float Stack(float current, float incoming, float stacking)
	{
		current = Math.Clamp(current, 0f, 1f);
		incoming = Math.Clamp(incoming, 0f, 1f);
		float share = Math.Clamp(stacking, 0f, 1f);
		return Math.Clamp(Math.Max(current, incoming) + (share * Math.Min(current, incoming)), 0f, 1f);
	}

	/// <summary>
	/// How long an effect that went to <paramref name="depth"/> lasts [s]: from
	/// <paramref name="shortest"/> for the lightest to <paramref name="longest"/> for the
	/// deepest, in a straight line between. Never negative.
	/// </summary>
	public static float Seconds(float depth, float shortest, float longest)
	{
		float t = Math.Clamp(depth, 0f, 1f);
		return Math.Max(0f, shortest + ((longest - shortest) * t));
	}
}

/// <summary>
/// One showing of the tunnel vision: how deep it went, how long it lasts and how far through
/// that it is. The shape of its fade is the effect's curve, sampled at <see cref="Progress"/>
/// and multiplied by <see cref="Peak"/>; this only keeps the time.
/// </summary>
public struct HurtPulse
{
	/// <summary>How deep this showing went, 0 to 1.</summary>
	public float Peak;

	/// <summary>How long it has been showing [s].</summary>
	public float Age;

	/// <summary>How long it shows for [s].</summary>
	public float Seconds;

	/// <summary>True while there is something to draw.</summary>
	public readonly bool IsActive => Peak > 0f && Age < Seconds;

	/// <summary>How far through its time it is: 0 as it starts, 1 once it is over.</summary>
	public readonly float Progress => Seconds <= 0f ? 1f : Math.Clamp(Age / Seconds, 0f, 1f);

	/// <summary>Starts it again from the beginning, at <paramref name="peak"/> for <paramref name="seconds"/>.</summary>
	public void Start(float peak, float seconds)
	{
		Peak = Math.Clamp(peak, 0f, 1f);
		Seconds = Math.Max(seconds, 0f);
		Age = 0f;
	}

	/// <summary>Moves it on by <paramref name="delta"/> seconds; time does not run backwards.</summary>
	public void Advance(float delta)
	{
		if (IsActive)
		{
			Age += Math.Max(delta, 0f);
		}
	}

	public void Clear() => this = default;
}

/// <summary>
/// Camera shake as trauma (Squirrel Eiserloh, "Math for Game Programmers: Juicing Your
/// Cameras With Math", GDC 2016): a hit adds to it, it drains at a steady rate, and the camera
/// is shaken by it raised to a power — so a light hit is a twitch, a heavy one a jolt, and
/// the shake dies away smoothly rather than stopping. Kept from 0 to 1.
/// </summary>
public struct ShakeTrauma
{
	public float Value;

	public readonly bool IsActive => Value > 0f;

	/// <summary>Adds <paramref name="amount"/>, to 1. Nothing negative is added.</summary>
	public void Add(float amount) => Value = Math.Clamp(Value + Math.Max(amount, 0f), 0f, 1f);

	/// <summary>Drains <paramref name="perSecond"/> a second for <paramref name="delta"/> seconds, to 0.</summary>
	public void Decay(float delta, float perSecond) =>
		Value = Math.Max(0f, Value - (Math.Max(delta, 0f) * Math.Max(perSecond, 0f)));

	/// <summary>
	/// How hard the camera shakes now, 0 to 1: <see cref="Value"/> to the power
	/// <paramref name="exponent"/> (at least 1, so the shake never outruns the trauma).
	/// </summary>
	public readonly float Shake(float exponent) =>
		Value <= 0f ? 0f : MathF.Pow(Value, Math.Max(exponent, 1f));

	public void Clear() => Value = 0f;
}
