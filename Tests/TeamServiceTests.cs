using Gdpyr.Match;
using Gdpyr.Sim;
using Xunit;

namespace Gdpyr.Tests;

public class TeamServiceTests
{
	[Fact]
	public void EveryoneStartsOnTheGround()
	{
		var teams = new TeamService();

		Assert.Equal(Team.GroundForce, teams.TeamOf(42));
		Assert.False(teams.Knows(42));
		Assert.Equal(0, teams.StrategistCount);
	}

	[Fact]
	public void TheStrategistSlotIsOptedInto()
	{
		var teams = new TeamService();

		Assert.Equal(Team.Strategist, teams.Assign(2, Team.Strategist));
		Assert.Equal(Team.Strategist, teams.TeamOf(2));
		Assert.Equal(1, teams.StrategistCount);
	}

	[Fact]
	public void TwoStrategistsFitAndAThirdDoesNot()
	{
		// The round is 6v2. A lobby where everyone picks the interesting-looking role
		// is a lobby with nobody on the ground.
		var teams = new TeamService();
		teams.Assign(1, Team.Strategist);
		teams.Assign(2, Team.Strategist);

		Assert.Equal(Team.GroundForce, teams.Assign(3, Team.Strategist));
		Assert.Equal(2, teams.StrategistCount);
		Assert.Equal(TeamService.MaxStrategists, teams.StrategistCount);
	}

	[Fact]
	public void AskingForTheSideYouAreAlreadyOnIsNeverRefused()
	{
		var teams = new TeamService();
		teams.Assign(1, Team.Strategist);
		teams.Assign(2, Team.Strategist);

		Assert.Equal(Team.Strategist, teams.Assign(1, Team.Strategist));
		Assert.Equal(2, teams.StrategistCount);
	}

	[Fact]
	public void LeavingTheChairFreesIt()
	{
		var teams = new TeamService();
		teams.Assign(1, Team.Strategist);
		teams.Assign(2, Team.Strategist);

		Assert.Equal(Team.GroundForce, teams.Assign(1, Team.GroundForce));
		Assert.Equal(Team.Strategist, teams.Assign(3, Team.Strategist));
	}

	[Fact]
	public void DisconnectingFreesTheChair()
	{
		var teams = new TeamService();
		teams.Assign(1, Team.Strategist);
		teams.Assign(2, Team.Strategist);
		teams.Remove(1);

		Assert.Equal(1, teams.StrategistCount);
		Assert.Equal(Team.Strategist, teams.Assign(3, Team.Strategist));
	}

	[Fact]
	public void CanJoinAnswersWithoutAssigning()
	{
		var teams = new TeamService();
		teams.Assign(1, Team.Strategist);
		teams.Assign(2, Team.Strategist);

		Assert.False(teams.CanJoin(3, Team.Strategist));
		Assert.True(teams.CanJoin(3, Team.GroundForce));
		Assert.False(teams.Knows(3));
	}

	[Fact]
	public void VersionAdvancesOnlyWhenSomethingChanges()
	{
		var teams = new TeamService();
		uint start = teams.Version;

		teams.Assign(1, Team.GroundForce);
		Assert.True(teams.Version > start);

		uint afterAssign = teams.Version;
		teams.Assign(1, Team.GroundForce);
		Assert.Equal(afterAssign, teams.Version);
	}

	[Fact]
	public void RemovingSomeoneWhoWasNeverHereChangesNothing()
	{
		var teams = new TeamService();
		teams.Assign(1, Team.GroundForce);
		uint version = teams.Version;

		teams.Remove(99);

		Assert.Equal(version, teams.Version);
	}

	[Fact]
	public void ClearEmptiesTheRoster()
	{
		var teams = new TeamService();
		teams.Assign(1, Team.Strategist);
		teams.Assign(2, Team.GroundForce);

		teams.Clear();

		Assert.Equal(0, teams.Count);
		Assert.Equal(0, teams.StrategistCount);
		Assert.Equal(Team.GroundForce, teams.TeamOf(1));
	}

	// ---- the role menu (docs/IMPLEMENTATION_PLAN.md §M3) --------------------

	[Fact]
	public void SomebodyWhoHasBeenAskedIsNotOnTheFieldYet()
	{
		var teams = new TeamService();

		Assert.True(teams.RequireChoice(1));

		Assert.True(teams.AwaitingChoice(1));
		Assert.Equal(1, teams.AwaitingCount);
		Assert.Equal(0, teams.ReadyCount);

		// They are still in the roster, and on the ground: the snapshot's team bit has
		// two values and no third state to put them in.
		Assert.Equal(1, teams.Count);
		Assert.Equal(Team.GroundForce, teams.TeamOf(1));
	}

	[Fact]
	public void AnAnswerPutsThemOnTheField()
	{
		var teams = new TeamService();
		teams.RequireChoice(1);

		Assert.Equal(Team.Strategist, teams.Assign(1, Team.Strategist));

		Assert.False(teams.AwaitingChoice(1));
		Assert.Equal(0, teams.AwaitingCount);
		Assert.Equal(1, teams.ReadyCount);
	}

	[Fact]
	public void AnsweringWithTheSideYouWereFiledUnderStillCounts()
	{
		// Everyone is filed on the ground while they choose, so "ground force" is the
		// answer that changes no assignment at all — and it still has to put a body
		// back on the field.
		var teams = new TeamService();
		teams.RequireChoice(1);
		uint version = teams.Version;

		Assert.Equal(Team.GroundForce, teams.Assign(1, Team.GroundForce));

		Assert.False(teams.AwaitingChoice(1));
		Assert.Equal(1, teams.ReadyCount);
		Assert.True(teams.Version > version);
	}

	[Fact]
	public void AComputerPlayerIsNeverAsked()
	{
		// It has no menu to answer with, and the fill policy has already counted its
		// seat as taken.
		var teams = new TeamService();
		int bot = BotRoster.PeerIdFor(0);

		Assert.False(teams.RequireChoice(bot));
		Assert.False(teams.AwaitingChoice(bot));
		Assert.Equal(0, teams.Count);
	}

	[Fact]
	public void AFreshRoundAsksThePeopleAndNotTheBots()
	{
		var teams = new TeamService();
		teams.Assign(1, Team.Strategist);
		teams.Assign(2, Team.GroundForce);
		teams.Assign(BotRoster.PeerIdFor(0), Team.GroundForce);

		Assert.Equal(2, teams.RequireChoiceFromPeople());

		Assert.True(teams.AwaitingChoice(1));
		Assert.True(teams.AwaitingChoice(2));
		Assert.False(teams.AwaitingChoice(BotRoster.PeerIdFor(0)));
		Assert.Equal(1, teams.ReadyCount);
	}

	[Fact]
	public void AStrategistKeepsTheirChairWhileTheyAnswer()
	{
		// The cap holds both seats over the intermission: a strategist being asked
		// again has not given theirs up, and a third player still lands on the ground.
		var teams = new TeamService();
		teams.Assign(1, Team.Strategist);
		teams.Assign(2, Team.Strategist);
		teams.RequireChoiceFromPeople();

		Assert.Equal(2, teams.StrategistCount);
		Assert.Equal(Team.GroundForce, teams.Assign(3, Team.Strategist));

		// One of them picks the ground this round, and the chair is free that instant.
		Assert.Equal(Team.GroundForce, teams.Assign(1, Team.GroundForce));
		Assert.Equal(1, teams.StrategistCount);
		Assert.Equal(Team.Strategist, teams.Assign(3, Team.Strategist));
	}

	[Fact]
	public void LeavingWhileTheMenuIsUpForgetsTheQuestion()
	{
		var teams = new TeamService();
		teams.RequireChoice(1);

		teams.Remove(1);

		Assert.False(teams.AwaitingChoice(1));
		Assert.Equal(0, teams.AwaitingCount);
		Assert.Equal(0, teams.Count);
	}

	[Fact]
	public void ClearForgetsTheQuestionsToo()
	{
		var teams = new TeamService();
		teams.RequireChoice(1);
		teams.RequireChoice(2);

		teams.Clear();

		Assert.Equal(0, teams.AwaitingCount);
		Assert.Equal(0, teams.ReadyCount);
	}

	[Fact]
	public void AskingTwiceAsksOnce()
	{
		var teams = new TeamService();

		Assert.True(teams.RequireChoice(1));
		Assert.False(teams.RequireChoice(1));
		Assert.Equal(1, teams.AwaitingCount);
	}
}
