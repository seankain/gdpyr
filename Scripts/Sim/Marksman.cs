using System;
using Godot;

namespace Gdpyr.Sim;

/// <summary>
/// One enemy a marksman could shoot from a peek (docs/TACTICS.md §5.3): a contact the
/// side remembers, as the side remembers it.
/// </summary>
public readonly struct MarksmanCandidate
{
	/// <summary>Who it is, as an <see cref="Sim.OwnerId"/>.</summary>
	public readonly int OwnerId;

	/// <summary>Where the side last saw its feet.</summary>
	public readonly Vector3 Feet;

	/// <summary>How fast it was going across the ground when last seen.</summary>
	public readonly float Speed;

	public readonly float HealthFraction;

	/// <summary>A friendly unit has it as its target: it is looking the other way.</summary>
	public readonly bool EngagedByFriend;

	public MarksmanCandidate(int ownerId, Vector3 feet, float speed, float healthFraction, bool engagedByFriend)
	{
		OwnerId = ownerId;
		Feet = feet;
		Speed = speed;
		HealthFraction = healthFraction;
		EngagedByFriend = engagedByFriend;
	}
}

/// <summary>
/// Where a marksman hides and peeks from, and whom it peeks at (docs/TACTICS.md §5.3), as
/// numbers and pure functions over what the side remembers: no ray. The timing of a
/// peek is the planner's (<c>UnitPlanTraits</c>); the geometry here is the scan's.
/// </summary>
public static class MarksmanPlan
{
	/// <summary>
	/// How far from the marksman a contact is a threat [m]: the far edge of the band plus
	/// the search, the furthest a target can be from any fire spot it could reach.
	/// </summary>
	public const float ThreatMeters = 90f;

	/// <summary>How far from where it stands it looks for a hide spot [m].</summary>
	public const float SearchMeters = 25f;

	/// <summary>The band a fire spot is in from its target [m], inside the rifle's 70 m.</summary>
	public const float MinRangeMeters = 35f;

	/// <inheritdoc cref="MinRangeMeters"/>
	public const float MaxRangeMeters = 65f;

	/// <summary>A threat nearer than this is too near to peek at [m]: the marksman moves away, and no spot is nearer one.</summary>
	public const float StandoffMeters = 30f;

	/// <summary>How far from its hide spot a fire spot may be [m].</summary>
	public const float PairMeters = 2.5f;

	/// <summary>
	/// The contact to peek at, or -1 for none: the squad's focus when it is a candidate;
	/// else one a friend is engaging; else, and among those, the slowest, then the
	/// weakest, then the lowest id.
	/// </summary>
	public static int ChooseTarget(ReadOnlySpan<MarksmanCandidate> candidates, int focusOwnerId)
	{
		int best = -1;
		for (int i = 0; i < candidates.Length; i++)
		{
			MarksmanCandidate candidate = candidates[i];
			if (focusOwnerId != Sim.OwnerId.None && candidate.OwnerId == focusOwnerId)
			{
				return i;
			}

			if (best < 0 || Before(candidate, candidates[best]))
			{
				best = i;
			}
		}

		return best;
	}

	private static bool Before(in MarksmanCandidate a, in MarksmanCandidate b)
	{
		if (a.EngagedByFriend != b.EngagedByFriend)
		{
			return a.EngagedByFriend;
		}

		if (a.Speed != b.Speed)
		{
			return a.Speed < b.Speed;
		}

		if (a.HealthFraction != b.HealthFraction)
		{
			return a.HealthFraction < b.HealthFraction;
		}

		return a.OwnerId < b.OwnerId;
	}
}
