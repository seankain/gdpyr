using System;
using Gdpyr.Sim;
using Gdpyr.Sim.Htn;
using Godot;
using Xunit;

namespace Gdpyr.Tests;

/// <summary>
/// The local force ratio (docs/HTN_BOTS.md §4.3): the bands, their hysteresis, and
/// the survey of friends and remembered hostiles they are computed from.
/// </summary>
public class NeighbourhoodTests
{
	private const int SelfId = 1;

	// ---- ForceRatio -------------------------------------------------------

	[Theory]
	[InlineData(1f, 1f, OddsBand.Even)]
	[InlineData(2f, 1f, OddsBand.Favourable)]
	[InlineData(1f, 2f, OddsBand.Outnumbered)]
	[InlineData(3f, 2f, OddsBand.Even)]
	[InlineData(1f, 0f, OddsBand.Favourable)]
	[InlineData(0f, 1f, OddsBand.Outnumbered)]
	[InlineData(0f, 0f, OddsBand.Even)]
	public void TwiceTheOtherSideEntersABand(float ours, float theirs, OddsBand expected)
	{
		Assert.Equal(expected, ForceRatio.Band(ours, theirs, OddsBand.Even));
	}

	[Fact]
	public void Outnumbered_IsHeldUntilTheRatioFallsBelowTheHoldRatio()
	{
		// §9, plan thrash: a fight swinging either side of 2:1 must not replan every scan.
		Assert.Equal(OddsBand.Even, ForceRatio.Band(4f, 7f, OddsBand.Even));
		Assert.Equal(OddsBand.Outnumbered, ForceRatio.Band(4f, 6f, OddsBand.Outnumbered));
		Assert.Equal(OddsBand.Even, ForceRatio.Band(4f, 5.9f, OddsBand.Outnumbered));
	}

	[Fact]
	public void Favourable_IsHeldUntilTheRatioFallsBelowTheHoldRatio()
	{
		Assert.Equal(OddsBand.Even, ForceRatio.Band(7f, 4f, OddsBand.Even));
		Assert.Equal(OddsBand.Favourable, ForceRatio.Band(6f, 4f, OddsBand.Favourable));
		Assert.Equal(OddsBand.Even, ForceRatio.Band(5.9f, 4f, OddsBand.Favourable));
	}

	[Fact]
	public void AFightThatTurns_GoesStraightAcrossTheBands()
	{
		Assert.Equal(OddsBand.Favourable, ForceRatio.Band(4f, 1f, OddsBand.Outnumbered));
		Assert.Equal(OddsBand.Outnumbered, ForceRatio.Band(1f, 4f, OddsBand.Favourable));
	}

	[Fact]
	public void Strength_IsCostTimesWhatIsLeft()
	{
		Assert.Equal(150f, ForceRatio.Strength(300f, 0.5f));
		Assert.Equal(300f, ForceRatio.Strength(300f, 1.5f));
		Assert.Equal(0f, ForceRatio.Strength(300f, -1f));
		Assert.Equal(0f, ForceRatio.Strength(-50f, 1f));
	}

	// ---- Neighbourhood ----------------------------------------------------

	private static ContactMemory Memory() => new(capacity: 8, observers: 1, liveWindowTicks: 8);

	private static void See(ContactMemory memory, uint tick, int peerId, float x) =>
		memory.Observe(tick, 0, new KnownContact
		{
			OwnerId = OwnerId.ForPeer(peerId),
			Kind = ContactKind.Player,
			Position = new Vector3(x, 0f, 0f),
			HealthFraction = 1f,
		});

	[Fact]
	public void FriendsAreCountedInsideTheRadius_AndTheSurveyorIsNot()
	{
		Neighbour[] friends =
		{
			new(SelfId, Vector3.Zero, 1f, false),
			new(2, new Vector3(Neighbourhood.FriendRadiusMeters, 0f, 0f), 0.5f, true),
			new(3, new Vector3(Neighbourhood.FriendRadiusMeters + 0.01f, 0f, 0f), 1f, true),
		};

		Neighbourhood around = Neighbourhood.Survey(SelfId, Vector3.Zero, friends, null, 0, OddsBand.Even);

		Assert.Equal(1, around.Friends);
		Assert.Equal(0.5f, around.FriendHealth);
		Assert.Equal(1, around.FriendsEngaging);
	}

	[Fact]
	public void TheNearestFriendAndTheCentroid()
	{
		Neighbour[] friends =
		{
			new(9, new Vector3(10f, 0f, 0f), 1f, false),
			new(4, new Vector3(-10f, 0f, 0f), 1f, false),
			new(5, new Vector3(0f, 0f, 20f), 1f, false),
		};

		Neighbourhood around = Neighbourhood.Survey(SelfId, Vector3.Zero, friends, null, 0, OddsBand.Even);

		// Two at ten metres: the lower id wins, so the answer does not depend on order.
		Assert.Equal(4, around.NearestFriendId);
		Assert.Equal(10f, around.NearestFriendMeters, 3);
		Assert.Equal(new Vector3(0f, 0f, 20f / 3f), around.Centroid);
	}

	[Fact]
	public void Alone_TheCentroidIsWhereTheSurveyorStands()
	{
		var self = new Vector3(3f, 0f, 4f);

		Neighbourhood around = Neighbourhood.Survey(SelfId, self, ReadOnlySpan<Neighbour>.Empty, null, 0,
			OddsBand.Even);

		Assert.Equal(0, around.Friends);
		Assert.Equal(0, around.NearestFriendId);
		Assert.Equal(float.MaxValue, around.NearestFriendMeters);
		Assert.Equal(self, around.Centroid);
		Assert.Equal(OddsBand.Favourable, around.Odds);
	}

	[Fact]
	public void HostilesAreWhatTheSideRemembersInsideTheRadius_GhostsIncluded()
	{
		ContactMemory memory = Memory();
		See(memory, 100, 10, 5f);
		See(memory, 100, 11, Neighbourhood.HostileRadiusMeters + 1f);
		See(memory, 130, 12, 20f);

		Neighbourhood around = Neighbourhood.Survey(SelfId, Vector3.Zero, ReadOnlySpan<Neighbour>.Empty, memory,
			130, OddsBand.Even);

		Assert.Equal(2, around.Hostiles);
		Assert.Equal(1, around.LiveHostiles);
	}

	[Fact]
	public void AForgottenContact_IsNotAHostile_EvenBeforeTheMemoryIsAged()
	{
		ContactMemory memory = Memory();
		See(memory, 100, 10, 5f);

		Neighbourhood around = Neighbourhood.Survey(SelfId, Vector3.Zero, ReadOnlySpan<Neighbour>.Empty, memory,
			100 + (uint)SimConfig.GhostLifetimeTicks + 1, OddsBand.Even);

		Assert.Equal(0, around.Hostiles);
	}

	[Fact]
	public void TheSurveyorCountsOnItsOwnSide()
	{
		// §5.1's "local hostiles ≥ 2 × friends", with the bot itself among the friends:
		// alone against one rifleman is even, and against two is outnumbered.
		ContactMemory memory = Memory();
		See(memory, 100, 10, 5f);

		Assert.Equal(OddsBand.Even, Neighbourhood.Survey(SelfId, Vector3.Zero, ReadOnlySpan<Neighbour>.Empty,
			memory, 100, OddsBand.Even).Odds);

		See(memory, 100, 11, 6f);
		Assert.Equal(OddsBand.Outnumbered, Neighbourhood.Survey(SelfId, Vector3.Zero, ReadOnlySpan<Neighbour>.Empty,
			memory, 100, OddsBand.Even).Odds);

		Neighbour[] buddy = { new(2, new Vector3(1f, 0f, 0f), 1f, true) };
		Assert.Equal(OddsBand.Even, Neighbourhood.Survey(SelfId, Vector3.Zero, buddy, memory, 100,
			OddsBand.Even).Odds);
	}

	[Fact]
	public void TheBandCarriesItsHysteresis()
	{
		// Two of us against three: even from even, still outnumbered from outnumbered.
		ContactMemory memory = Memory();
		See(memory, 100, 10, 5f);
		See(memory, 100, 11, 6f);
		See(memory, 100, 12, 7f);
		Neighbour[] buddy = { new(2, new Vector3(1f, 0f, 0f), 1f, false) };

		Assert.Equal(OddsBand.Even,
			Neighbourhood.Survey(SelfId, Vector3.Zero, buddy, memory, 100, OddsBand.Even).Odds);
		Assert.Equal(OddsBand.Outnumbered,
			Neighbourhood.Survey(SelfId, Vector3.Zero, buddy, memory, 100, OddsBand.Outnumbered).Odds);
	}

	[Fact]
	public void Surveying_AllocatesNothing()
	{
		ContactMemory memory = new(SimConfig.MaxUnits, 1, 8);
		var friends = new Neighbour[SimConfig.MaxUnits];
		for (int i = 0; i < friends.Length; i++)
		{
			friends[i] = new Neighbour(i + 1, new Vector3(i, 0f, 0f), 1f, (i & 1) == 0);
			See(memory, 0, 100 + i, i * 0.5f);
		}

		Run();
		long before = GC.GetAllocatedBytesForCurrentThread();
		Run();
		Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);

		void Run()
		{
			OddsBand odds = OddsBand.Even;
			for (int i = 0; i < friends.Length; i++)
			{
				odds = Neighbourhood.Survey(friends[i].Id, friends[i].Position, friends, memory, 4, odds).Odds;
			}
		}
	}
}
