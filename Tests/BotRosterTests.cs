using Gdpyr.Sim;
using Xunit;

namespace Gdpyr.Tests;

public class BotRosterTests
{
	private const int Seats = SnapshotCodec.MaxPlayers;

	/// <summary>The cap TeamService enforces. Passed in, because Scripts/Sim may not depend on Scripts/Match.</summary>
	private const int StrategistCap = 2;

	private static BotFill Plan(int groundHumans, int strategistHumans, int groundTarget = 6,
		int strategistTarget = 1, int seats = Seats) =>
		BotFillPolicy.Plan(new BotDemand(groundTarget, strategistTarget, groundHumans, strategistHumans,
			seats, StrategistCap));

	// ---- the id band -------------------------------------------------------

	[Fact]
	public void BotIdsAreOutsideTheRangeAGodotPeerIdNormallyLandsIn()
	{
		Assert.True(BotRoster.FirstPeerId > 0);
		Assert.False(BotRoster.IsBot(1));
		Assert.False(BotRoster.IsBot(0));
	}

	[Fact]
	public void BotIdsStayPositive_SoTheUnitHalfOfOwnerIdIsUntouched()
	{
		// OwnerId folds units into the negative half; a bot's shots must not collide
		// with that (docs/IMPLEMENTATION_PLAN.md §M3, OwnerId).
		for (int slot = 0; slot < BotRoster.MaxBots; slot++)
		{
			int peerId = BotRoster.PeerIdFor(slot);
			Assert.True(OwnerId.IsPeer(OwnerId.ForPeer(peerId)));
		}
	}

	[Fact]
	public void SlotsRoundTripThroughPeerIds()
	{
		for (int slot = 0; slot < BotRoster.MaxBots; slot++)
		{
			int peerId = BotRoster.PeerIdFor(slot);
			Assert.True(BotRoster.IsBot(peerId));
			Assert.Equal(slot, BotRoster.SlotOf(peerId));
		}
	}

	[Fact]
	public void SlotsOutsideTheBandHaveNoId()
	{
		Assert.Equal(0, BotRoster.PeerIdFor(-1));
		Assert.Equal(0, BotRoster.PeerIdFor(BotRoster.MaxBots));
		Assert.Equal(-1, BotRoster.SlotOf(1));
	}

	// ---- the fill policy ---------------------------------------------------

	[Fact]
	public void AnEmptyServerGetsNoBots()
	{
		// Nobody is watching, and a headless server fighting itself spends the EC2
		// box's CPU credits before a playtest starts (docs/DEPLOYMENT.md §5).
		BotFill fill = Plan(groundHumans: 0, strategistHumans: 0);

		Assert.Equal(0, fill.Total);
	}

	[Fact]
	public void OnePersonOnTheGroundGetsAFullOppositionAndAFullSquad()
	{
		BotFill fill = Plan(groundHumans: 1, strategistHumans: 0);

		Assert.Equal(5, fill.Ground);
		Assert.Equal(1, fill.Strategist);
	}

	[Fact]
	public void OnePersonInTheStrategistChairGetsSomebodyToFight()
	{
		BotFill fill = Plan(groundHumans: 0, strategistHumans: 1);

		Assert.Equal(6, fill.Ground);
		Assert.Equal(0, fill.Strategist);
	}

	[Fact]
	public void EveryHumanWhoArrivesDisplacesABot()
	{
		for (int humans = 1; humans <= 6; humans++)
		{
			Assert.Equal(6 - humans, Plan(groundHumans: humans, strategistHumans: 0).Ground);
		}
	}

	[Fact]
	public void AFullHouseGetsNoGroundBots()
	{
		BotFill fill = Plan(groundHumans: 6, strategistHumans: 2);

		Assert.Equal(0, fill.Ground);
		Assert.Equal(0, fill.Strategist);
	}

	[Fact]
	public void MoreHumansThanTheTargetIsNotANegativeNumberOfBots()
	{
		BotFill fill = Plan(groundHumans: 9, strategistHumans: 0);

		Assert.Equal(0, fill.Ground);
		Assert.True(fill.Total >= 0);
	}

	[Fact]
	public void TheStrategistCapIsNeverExceeded()
	{
		// Two humans already hold both chairs, and the target says one: there is no
		// seat to fill however it is counted.
		Assert.Equal(0, Plan(groundHumans: 1, strategistHumans: 2, strategistTarget: 2).Strategist);
		Assert.Equal(1, Plan(groundHumans: 1, strategistHumans: 1, strategistTarget: 2).Strategist);
		Assert.Equal(2, Plan(groundHumans: 1, strategistHumans: 0, strategistTarget: 4).Strategist);
	}

	[Fact]
	public void TheRosterNeverOutgrowsTheSnapshot()
	{
		// A player the snapshot cannot carry is a player nobody can see
		// (SnapshotCodec.MaxPlayers bounds the broadcast).
		BotFill fill = Plan(groundHumans: 10, strategistHumans: 2, groundTarget: 16, strategistTarget: 2);

		Assert.True(fill.Total <= Seats - 12);
	}

	[Fact]
	public void TheStrategistSeatIsFilledBeforeTheGroundOnes()
	{
		// One seat left: a ground force with nothing shooting back is not a game.
		BotFill fill = Plan(groundHumans: 3, strategistHumans: 0, seats: 4);

		Assert.Equal(1, fill.Strategist);
		Assert.Equal(0, fill.Ground);
	}

	[Fact]
	public void ATargetOfZeroTurnsASideOff()
	{
		BotFill fill = Plan(groundHumans: 1, strategistHumans: 0, groundTarget: 0, strategistTarget: 0);

		Assert.Equal(0, fill.Total);
	}

	[Fact]
	public void NegativeDemandsAreClampedRatherThanBelieved()
	{
		BotFill fill = BotFillPolicy.Plan(new BotDemand(-4, -4, -1, -1, -1, -1));

		Assert.Equal(0, fill.Total);
	}
}
