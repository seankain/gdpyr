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
}
