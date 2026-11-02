using System;
using Godot;

namespace Gdpyr.Ui;

/// <summary>
/// The engine-free half of the health bar the strategist's overlay draws over every unit
/// (<c>SelectionOverlay</c>): its colour, which runs from green at full health through
/// yellow and orange to red as the unit is hurt. Kept apart from the overlay so that
/// "a unit at half health is drawn between yellow and orange" is answered by
/// <c>dotnet test</c> rather than by squinting at a match.
/// </summary>
public static class UnitBars
{
	/// <summary>Whole.</summary>
	public static readonly Color Green = new(0.3f, 0.85f, 0.35f);

	/// <summary>Two thirds.</summary>
	public static readonly Color Yellow = new(0.95f, 0.88f, 0.25f);

	/// <summary>One third.</summary>
	public static readonly Color Orange = new(1f, 0.55f, 0.15f);

	/// <summary>Dead, or nearly.</summary>
	public static readonly Color Red = new(0.9f, 0.18f, 0.15f);

	/// <summary>Behind the bar: the part of it that is gone.</summary>
	public static readonly Color Back = new(0f, 0f, 0f, 0.6f);

	/// <summary>
	/// The colour of a bar at <paramref name="fraction"/> of full health: green at one,
	/// yellow at two thirds, orange at one third, red at none, and blended between each
	/// pair, so a unit going down passes through every one of them in that order.
	/// Outside nought to one is clamped.
	/// </summary>
	public static Color HealthColor(float fraction)
	{
		float f = float.IsNaN(fraction) ? 0f : Math.Clamp(fraction, 0f, 1f);
		const float Third = 1f / 3f;
		if (f >= 2f * Third)
		{
			return Yellow.Lerp(Green, (f - (2f * Third)) / Third);
		}

		if (f >= Third)
		{
			return Orange.Lerp(Yellow, (f - Third) / Third);
		}

		return Red.Lerp(Orange, f / Third);
	}
}
