using Gdpyr.Core;
using Gdpyr.Match;
using Gdpyr.Sim;
using Xunit;

namespace Gdpyr.Tests;

public class HostSettingsTests
{
	[Fact]
	public void WhatTheHostPicksIsWhatTheRoundGets()
	{
		var settings = new HostSettings(MapCatalog.Default.Id, roundMinutes: 15, groundForceTickets: 120,
			groundForceSize: 4, strategistSize: 2);

		Assert.Equal(MapCatalog.Default.Id, settings.MapId);
		Assert.Equal(15, settings.RoundMinutes);
		Assert.Equal(120, settings.GroundForceTickets);
		Assert.Equal(4, settings.GroundForceSize);
		Assert.Equal(2, settings.StrategistSize);
		Assert.Equal(15 * 60 * SimConfig.TickRate, settings.RoundTicks);
	}

	[Theory]
	[InlineData(0, HostSettings.MinRoundMinutes)]
	[InlineData(-5, HostSettings.MinRoundMinutes)]
	[InlineData(61, HostSettings.MaxRoundMinutes)]
	public void TheRoundLengthIsPulledIntoRange(int asked, int expected)
	{
		Assert.Equal(expected, new HostSettings(MapCatalog.Default.Id, asked, 50, 6, 1).RoundMinutes);
	}

	[Theory]
	[InlineData(0, HostSettings.MinGroundForceTickets)]
	[InlineData(1000, HostSettings.MaxGroundForceTickets)]
	public void TheTicketPoolIsPulledIntoRange(int asked, int expected)
	{
		// Zero tickets would end the round before anybody had spawned.
		Assert.Equal(expected, new HostSettings(MapCatalog.Default.Id, 20, asked, 6, 1).GroundForceTickets);
	}

	[Theory]
	[InlineData(0, 0, HostSettings.MinTeamSize, HostSettings.MinTeamSize)]
	[InlineData(99, 99, HostSettings.MaxGroundForceSize, HostSettings.MaxStrategistSize)]
	public void TeamSizesArePulledIntoRange(int ground, int strategists, int expectedGround, int expectedStrategists)
	{
		var settings = new HostSettings(MapCatalog.Default.Id, 20, 50, ground, strategists);

		Assert.Equal(expectedGround, settings.GroundForceSize);
		Assert.Equal(expectedStrategists, settings.StrategistSize);
	}

	[Fact]
	public void TheLargestSidesFitInOneSnapshot()
	{
		// A full ground force and a full strategists' side are filled at once; the
		// fill policy would otherwise quietly hand out fewer seats than the menu said.
		Assert.Equal(SnapshotCodec.MaxPlayers, HostSettings.MaxGroundForceSize + HostSettings.MaxStrategistSize);
		Assert.Equal(TeamService.MaxStrategists, HostSettings.MaxStrategistSize);
	}

	[Fact]
	public void AMapTheCatalogDoesNotHaveIsTheDefault()
	{
		var settings = new HostSettings("no-such-map", 20, 50, 6, 1);

		Assert.Equal(MapCatalog.Default.Id, settings.MapId);
		Assert.Equal(MapCatalog.Default.ScenePath, settings.Map.ScenePath);
	}

	[Fact]
	public void TheDefaultsAreTheGameModesNumbersOnTheDefaultMap()
	{
		// default_gamemode.tres: 20 minutes, 50 tickets, 6 ground and 1 strategist.
		HostSettings defaults = HostSettings.Defaults(20, 50, 6, 1);

		Assert.Equal(MapCatalog.Default.Id, defaults.MapId);
		Assert.Equal(20, defaults.RoundMinutes);
		Assert.Equal(50, defaults.GroundForceTickets);
		Assert.Equal(6, defaults.GroundForceSize);
		Assert.Equal(1, defaults.StrategistSize);
	}

	[Fact]
	public void NoBotsOnTheCommandLineStillOffersASideOfOne()
	{
		// `--no-bots` is a count of zero; a team of nobody is not a choice the panel offers.
		HostSettings defaults = HostSettings.Defaults(20, 50, 0, 0);

		Assert.Equal(HostSettings.MinTeamSize, defaults.GroundForceSize);
		Assert.Equal(HostSettings.MinTeamSize, defaults.StrategistSize);
	}

	[Fact]
	public void TheStrategistSizeCapsTheChairsAndFillsThem()
	{
		var settings = new HostSettings(MapCatalog.Default.Id, 20, 50, 6, 1);
		var teams = new TeamService();
		teams.SetStrategistCap(settings.StrategistSize);

		// One person in the chair, one alone on a server: the fill policy puts no
		// second strategist beside them, and the team service refuses a second person.
		BotFill fill = BotFillPolicy.Plan(new BotDemand(settings.GroundForceSize, settings.StrategistSize,
			groundHumans: 0, strategistHumans: 1, seats: SnapshotCodec.MaxPlayers, strategistCap: teams.StrategistCap));
		Assert.Equal(0, fill.Strategist);
		Assert.Equal(6, fill.Ground);

		Assert.Equal(Team.Strategist, teams.Assign(1, Team.Strategist));
		Assert.Equal(Team.GroundForce, teams.Assign(2, Team.Strategist));
	}
}
