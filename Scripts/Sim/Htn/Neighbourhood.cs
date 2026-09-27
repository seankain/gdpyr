using System;
using Godot;

namespace Gdpyr.Sim.Htn;

/// <summary>One friendly body a <see cref="Neighbourhood"/> is surveyed over: a unit, or a ground-force player.</summary>
public readonly struct Neighbour
{
	/// <summary>A unit id or a peer id; never 0, which means nobody.</summary>
	public readonly int Id;

	public readonly Vector3 Position;

	public readonly float HealthFraction;

	/// <summary>True while it is shooting at something.</summary>
	public readonly bool Engaging;

	public Neighbour(int id, Vector3 position, float healthFraction, bool engaging)
	{
		Id = id;
		Position = position;
		HealthFraction = healthFraction;
		Engaging = engaging;
	}
}

/// <summary>
/// What is around one unit or one ground bot (docs/HTN_BOTS.md §4.3): the friends
/// within <see cref="FriendRadiusMeters"/> — how many, how healthy, how many are in
/// a fight, the nearest one and where they are gathered — and the remembered
/// hostiles within <see cref="HostileRadiusMeters"/>. Out of the two comes the
/// local force ratio, as an <see cref="OddsBand"/>.
///
/// Counted in bodies, as §5.1 defines "outnumbered", with the one surveying
/// counted on its own side: a bot alone against one rifleman is even, not
/// outnumbered. Hostiles are everything the side remembers there, ghosts included:
/// a squad that lost sight of the tanks three seconds ago has not stopped being
/// outnumbered by them. <see cref="LiveHostiles"/> is the part being watched now.
///
/// A pure function of its arguments, over positions the caller already has in hand
/// — <c>UnitManager</c>'s units, <c>CombatManager</c>'s players — on the scan the
/// caller already runs: 64 units surveying each other is at most 4,096 distance
/// checks, spread over the ten-tick stagger.
/// </summary>
public struct Neighbourhood
{
	public const float FriendRadiusMeters = 30f;

	public const float HostileRadiusMeters = 40f;

	/// <summary>Friends in range, not counting the one surveying.</summary>
	public int Friends;

	/// <summary>Their health fractions, summed: 3.0 is three friends at full health.</summary>
	public float FriendHealth;

	public int FriendsEngaging;

	/// <summary>The nearest friend's id, or 0 when there is none in range.</summary>
	public int NearestFriendId;

	/// <summary><see cref="float.MaxValue"/> when there is none in range.</summary>
	public float NearestFriendMeters;

	/// <summary>Where the friends in range are gathered; the surveyor's own position when there are none.</summary>
	public Vector3 Centroid;

	/// <summary>Remembered hostiles in range, live and ghost.</summary>
	public int Hostiles;

	/// <summary>The hostiles in range an observer can see now.</summary>
	public int LiveHostiles;

	public OddsBand Odds;

	/// <summary>
	/// Surveys the ground around <paramref name="self"/>. <paramref name="friends"/> may
	/// include the surveyor, which is skipped by <paramref name="selfId"/>;
	/// <paramref name="hostiles"/> may be null, for a side that remembers nothing.
	/// <paramref name="previous"/> is the band this surveyor was in last time, for the
	/// hysteresis (<see cref="ForceRatio.HoldRatio"/>).
	/// </summary>
	public static Neighbourhood Survey(int selfId, Vector3 self, ReadOnlySpan<Neighbour> friends,
		ContactMemory hostiles, uint tick, OddsBand previous, float friendRadiusMeters = FriendRadiusMeters,
		float hostileRadiusMeters = HostileRadiusMeters)
	{
		var around = new Neighbourhood { NearestFriendMeters = float.MaxValue };
		float friendRadius = friendRadiusMeters * friendRadiusMeters;
		Vector3 sum = Vector3.Zero;

		for (int i = 0; i < friends.Length; i++)
		{
			Neighbour friend = friends[i];
			if (friend.Id == selfId)
			{
				continue;
			}

			float distance = self.DistanceSquaredTo(friend.Position);
			if (distance > friendRadius)
			{
				continue;
			}

			around.Friends++;
			around.FriendHealth += Math.Clamp(friend.HealthFraction, 0f, 1f);
			sum += friend.Position;

			if (friend.Engaging)
			{
				around.FriendsEngaging++;
			}

			// Ties go to the lower id, so two friends at one distance give one answer.
			float meters = MathF.Sqrt(distance);
			if (meters < around.NearestFriendMeters
				|| (meters == around.NearestFriendMeters && friend.Id < around.NearestFriendId))
			{
				around.NearestFriendMeters = meters;
				around.NearestFriendId = friend.Id;
			}
		}

		around.Centroid = around.Friends > 0 ? sum / around.Friends : self;

		float hostileRadius = hostileRadiusMeters * hostileRadiusMeters;
		for (int i = 0; hostiles != null && i < hostiles.Count; i++)
		{
			if (!hostiles.IsRemembered(i, tick) || self.DistanceSquaredTo(hostiles.At(i).Position) > hostileRadius)
			{
				continue;
			}

			around.Hostiles++;
			if (hostiles.IsLive(i, tick))
			{
				around.LiveHostiles++;
			}
		}

		around.Odds = ForceRatio.Band(around.Friends + 1, around.Hostiles, previous);
		return around;
	}
}
