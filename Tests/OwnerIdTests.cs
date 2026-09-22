using Gdpyr.Sim;
using Xunit;

namespace Gdpyr.Tests;

public class OwnerIdTests
{
	[Fact]
	public void APeerIsItsOwnId()
	{
		Assert.Equal(3, OwnerId.ForPeer(3));
		Assert.True(OwnerId.IsPeer(OwnerId.ForPeer(3)));
		Assert.False(OwnerId.IsUnit(OwnerId.ForPeer(3)));
		Assert.Equal(3, OwnerId.PeerOf(OwnerId.ForPeer(3)));
	}

	[Fact]
	public void AUnitTakesTheNegativeHalf()
	{
		int owner = OwnerId.ForUnit(3);

		Assert.True(OwnerId.IsUnit(owner));
		Assert.False(OwnerId.IsPeer(owner));
		Assert.Equal((ushort)3, OwnerId.UnitOf(owner));
	}

	[Fact]
	public void ThePeerAndUnitSpacesNeverCollide()
	{
		// This is the whole reason a shot still carries one owner field: Godot peer
		// ids are positive, so the negative half is free for units and a projectile
		// stays 23 bytes (docs/NETCODE.md §4.2).
		for (int i = 1; i <= 1000; i++)
		{
			Assert.NotEqual(OwnerId.ForPeer(i), OwnerId.ForUnit((ushort)i));
		}
	}

	[Fact]
	public void ZeroIsNeitherAndMeansNobody()
	{
		Assert.False(OwnerId.IsPeer(OwnerId.None));
		Assert.False(OwnerId.IsUnit(OwnerId.None));
		Assert.Equal(0, OwnerId.PeerOf(OwnerId.None));
		Assert.Equal((ushort)0, OwnerId.UnitOf(OwnerId.None));
	}

	[Fact]
	public void TheHighestUnitIdSurvivesTheRoundTrip()
	{
		int owner = OwnerId.ForUnit(ushort.MaxValue);

		Assert.True(OwnerId.IsUnit(owner));
		Assert.Equal(ushort.MaxValue, OwnerId.UnitOf(owner));
	}

	[Fact]
	public void AskingAPeerIdForItsUnitGivesNothing()
	{
		Assert.Equal((ushort)0, OwnerId.UnitOf(OwnerId.ForPeer(5)));
		Assert.Equal(0, OwnerId.PeerOf(OwnerId.ForUnit(5)));
	}

	[Fact]
	public void EveryDefenseRoundTripsAndIsNothingElse()
	{
		for (int index = 0; index < SimConfig.MaxDefenses; index++)
		{
			int owner = OwnerId.ForDefense(index);

			Assert.True(OwnerId.IsDefense(owner));
			Assert.False(OwnerId.IsUnit(owner));
			Assert.False(OwnerId.IsPeer(owner));
			Assert.Equal(index, OwnerId.DefenseOf(owner));
		}
	}

	[Fact]
	public void ADefenseIsNeverReadAsAUnit()
	{
		// The defence range sits directly below the units'. Truncating one of its ids
		// to a ushort would land on a real unit id — defence 3 would be unit 3 — and
		// a round from a barracks gun would be charged to a rifleman.
		for (int index = 0; index < SimConfig.MaxDefenses; index++)
		{
			Assert.Equal((ushort)0, OwnerId.UnitOf(OwnerId.ForDefense(index)));
			Assert.Equal(0, OwnerId.PeerOf(OwnerId.ForDefense(index)));
		}

		Assert.NotEqual(OwnerId.ForUnit(ushort.MaxValue), OwnerId.ForDefense(0));
		Assert.Equal(-1, OwnerId.DefenseOf(OwnerId.ForUnit(ushort.MaxValue)));
		Assert.Equal(-1, OwnerId.DefenseOf(OwnerId.ForPeer(3)));
		Assert.Equal(-1, OwnerId.DefenseOf(OwnerId.None));
	}

	[Fact]
	public void EverythingTheStrategistsSideFiresWithIsNegative()
	{
		// What the agent client's reward functions read the stream by
		// (tools/Gdpyr.AgentClient/StrategistReward.cs): a negative attacker is the
		// strategist's, whether it is a unit or the building.
		Assert.True(OwnerId.ForUnit(1) < 0);
		Assert.True(OwnerId.ForDefense(0) < 0);
		Assert.True(OwnerId.ForDefense(SimConfig.MaxDefenses - 1) < 0);
	}

	[Fact]
	public void AnIndexPastTheCapIsNotADefense()
	{
		Assert.False(OwnerId.IsDefense(OwnerId.ForDefense(SimConfig.MaxDefenses)));
		Assert.False(OwnerId.IsDefense(OwnerId.ForDefense(-1)));
	}
}
