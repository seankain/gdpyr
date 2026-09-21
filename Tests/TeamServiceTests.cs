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
}
