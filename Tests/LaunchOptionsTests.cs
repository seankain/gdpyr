using Gdpyr.Core;
using Xunit;

namespace Gdpyr.Tests;

public class LaunchOptionsTests
{
	[Fact]
	public void NoArguments_IsOffline()
	{
		var options = LaunchOptions.Parse(System.Array.Empty<string>());

		Assert.Null(options.Error);
		Assert.Equal(LaunchMode.Offline, options.Mode);
	}

	[Fact]
	public void NoArguments_OnADedicatedServerBuild_IsServerOnTheDefaultPort()
	{
		var options = LaunchOptions.Parse(System.Array.Empty<string>(), dedicatedServer: true);

		Assert.Null(options.Error);
		Assert.Equal(LaunchMode.Server, options.Mode);
		Assert.Equal(LaunchOptions.DefaultPort, options.Port);
	}

	[Fact]
	public void Server_WithoutAPort_UsesTheDefault()
	{
		var options = LaunchOptions.Parse(new[] { "--server" });

		Assert.Null(options.Error);
		Assert.Equal(LaunchMode.Server, options.Mode);
		Assert.Equal(7777, options.Port);
	}

	[Fact]
	public void Server_WithAPort_UsesIt()
	{
		var options = LaunchOptions.Parse(new[] { "--server", "7778" });

		Assert.Null(options.Error);
		Assert.Equal(LaunchMode.Server, options.Mode);
		Assert.Equal(7778, options.Port);
	}

	[Fact]
	public void Listen_IsAServerWithALocalPlayer()
	{
		var options = LaunchOptions.Parse(new[] { "--listen" });

		Assert.Null(options.Error);
		Assert.Equal(LaunchMode.Listen, options.Mode);
		Assert.True(options.IsServer);
		Assert.True(options.HasLocalPlayer);
	}

	[Fact]
	public void Server_HasNoLocalPlayer()
	{
		Assert.False(LaunchOptions.Parse(new[] { "--server" }).HasLocalPlayer);
	}

	[Theory]
	[InlineData("203.0.113.10:7777", "203.0.113.10", 7777)]
	[InlineData("203.0.113.10", "203.0.113.10", LaunchOptions.DefaultPort)]
	[InlineData("gdpyr.example.com:25565", "gdpyr.example.com", 25565)]
	[InlineData("[::1]:7777", "::1", 7777)]
	[InlineData("[fe80::1]", "fe80::1", LaunchOptions.DefaultPort)]
	[InlineData("::1", "::1", LaunchOptions.DefaultPort)]
	public void Client_ParsesAddresses(string address, string expectedHost, int expectedPort)
	{
		var options = LaunchOptions.Parse(new[] { "--client", address });

		Assert.Null(options.Error);
		Assert.Equal(LaunchMode.Client, options.Mode);
		Assert.Equal(expectedHost, options.Host);
		Assert.Equal(expectedPort, options.Port);
	}

	[Fact]
	public void Client_WithoutAnAddress_Fails()
	{
		Assert.NotNull(LaunchOptions.Parse(new[] { "--client" }).Error);
	}

	[Fact]
	public void Client_DoesNotSwallowTheNextFlagAsAnAddress()
	{
		var options = LaunchOptions.Parse(new[] { "--client", "--listen" });

		Assert.NotNull(options.Error);
	}

	[Theory]
	[InlineData("0")]
	[InlineData("65536")]
	[InlineData("-1")]
	[InlineData("http")]
	public void Ports_OutsideTheValidRange_Fail(string port)
	{
		Assert.NotNull(LaunchOptions.Parse(new[] { "--server", port }).Error);
	}

	[Fact]
	public void ConflictingModeFlags_Fail()
	{
		Assert.NotNull(LaunchOptions.Parse(new[] { "--server", "--client", "127.0.0.1" }).Error);
	}

	[Fact]
	public void UnrecognizedArguments_Fail()
	{
		Assert.NotNull(LaunchOptions.Parse(new[] { "--sever" }).Error);
	}

	[Fact]
	public void AFailedParse_DoesNotLookLikeAnOfflineLaunch()
	{
		// Bootstrap quits on Error; this guards against a future caller that only
		// inspects Mode and would otherwise silently start a server offline.
		var options = LaunchOptions.Parse(new[] { "--server", "nope" });

		Assert.NotNull(options.Error);
		Assert.NotSame(LaunchOptions.Offline, options);
	}

	[Fact]
	public void NullArguments_AreTreatedAsNone()
	{
		var options = LaunchOptions.Parse(null);

		Assert.Null(options.Error);
		Assert.Equal(LaunchMode.Offline, options.Mode);
	}

	// ---- bots (docs/IMPLEMENTATION_PLAN.md §M3.5) --------------------------

	[Fact]
	public void NoBotFlag_LeavesTheNumbersToTheGameMode()
	{
		var options = LaunchOptions.Parse(new[] { "--listen" });

		Assert.Null(options.GroundBots);
		Assert.Null(options.StrategistBots);
	}

	[Fact]
	public void Bots_WithOneNumber_SetsOnlyTheGroundForce()
	{
		// `--bots 4` is "four on the ground"; how many strategists is still the game
		// mode's business, and silently zeroing it would be a surprise.
		var options = LaunchOptions.Parse(new[] { "--server", "--bots", "4" });

		Assert.Null(options.Error);
		Assert.Equal(4, options.GroundBots);
		Assert.Null(options.StrategistBots);
	}

	[Fact]
	public void Bots_WithBothNumbers_SetsBoth()
	{
		var options = LaunchOptions.Parse(new[] { "--listen", "--bots", "6:1" });

		Assert.Null(options.Error);
		Assert.Equal(6, options.GroundBots);
		Assert.Equal(1, options.StrategistBots);
	}

	[Fact]
	public void Bots_Zero_IsAChoiceAndNotAnAbsence()
	{
		var options = LaunchOptions.Parse(new[] { "--bots", "0:0" });

		Assert.Null(options.Error);
		Assert.Equal(0, options.GroundBots);
		Assert.Equal(0, options.StrategistBots);
	}

	[Fact]
	public void NoBots_TurnsBothSidesOff()
	{
		var options = LaunchOptions.Parse(new[] { "--server", "--no-bots" });

		Assert.Null(options.Error);
		Assert.Equal(0, options.GroundBots);
		Assert.Equal(0, options.StrategistBots);
	}

	[Theory]
	[InlineData("--bots", "--no-bots")]
	[InlineData("--no-bots", "--bots")]
	public void ContradictoryBotFlags_Fail(string first, string second)
	{
		string[] args = first == "--bots"
			? new[] { first, "4", second }
			: new[] { first, second, "4" };

		Assert.NotNull(LaunchOptions.Parse(args).Error);
	}

	[Theory]
	[InlineData("-1")]
	[InlineData("99")]
	[InlineData("some")]
	[InlineData("4:")]
	[InlineData("4:1:1")]
	public void BotCounts_OutsideTheValidRange_Fail(string value)
	{
		Assert.NotNull(LaunchOptions.Parse(new[] { "--bots", value }).Error);
	}

	[Fact]
	public void Bots_WithoutACount_Fails()
	{
		Assert.NotNull(LaunchOptions.Parse(new[] { "--bots" }).Error);
	}

	[Fact]
	public void Bots_DoesNotSwallowTheNextFlagAsACount()
	{
		Assert.NotNull(LaunchOptions.Parse(new[] { "--bots", "--listen" }).Error);
	}

	// ---- the agent channel (docs/AGENT_API.md §4.1) ------------------------

	[Fact]
	public void AgentApi_IsOffUnlessItIsAskedFor()
	{
		var options = LaunchOptions.Parse(new[] { "--server" });

		Assert.Null(options.Error);
		Assert.False(options.HasAgentApi);
		Assert.Null(options.AgentPort);
	}

	[Fact]
	public void AgentApi_WithABarePort_BindsLoopback()
	{
		var options = LaunchOptions.Parse(new[] { "--server", "--agent-api", "7900" });

		Assert.Null(options.Error);
		Assert.True(options.HasAgentApi);
		Assert.Equal(7900, options.AgentPort);
		Assert.Equal(LaunchOptions.AgentLoopbackHost, options.AgentHost);
		Assert.Null(options.AgentToken);
	}

	[Fact]
	public void AgentApi_OnLoopbackByName_NeedsNoToken()
	{
		var options = LaunchOptions.Parse(new[] { "--listen", "--agent-api", "localhost:7900" });

		Assert.Null(options.Error);
		Assert.Equal("localhost", options.AgentHost);
		Assert.Equal(7900, options.AgentPort);
	}

	[Fact]
	public void AgentApi_OnAPublicAddressWithoutAToken_IsFatal()
	{
		// The socket can spawn players, issue orders and reset rounds, and the box in
		// docs/DEPLOYMENT.md has a public Elastic IP. Not a warning.
		var options = LaunchOptions.Parse(new[] { "--server", "--agent-api", "0.0.0.0:7900" });

		Assert.NotNull(options.Error);
		Assert.Contains("--agent-token", options.Error);
	}

	[Fact]
	public void AgentApi_OnAPublicAddressWithAToken_IsAllowed()
	{
		var options = LaunchOptions.Parse(new[]
		{
			"--server", "--agent-api", "10.0.0.4:7900", "--agent-token", "hunter2",
		});

		Assert.Null(options.Error);
		Assert.Equal("10.0.0.4", options.AgentHost);
		Assert.Equal("hunter2", options.AgentToken);
	}

	[Fact]
	public void AgentApi_NeedsAnAuthority_NotAClient()
	{
		Assert.NotNull(LaunchOptions.Parse(new[] { "--client", "host", "--agent-api", "7900" }).Error);
		Assert.NotNull(LaunchOptions.Parse(new[] { "--agent-api", "7900" }).Error);
	}

	[Theory]
	[InlineData("--agent-token", "secret")]
	[InlineData("--agent-unbounded", null)]
	[InlineData("--agent-omniscient", null)]
	public void AgentFlags_WithoutTheChannel_Fail(string flag, string value)
	{
		string[] args = value == null
			? new[] { "--server", flag }
			: new[] { "--server", flag, value };

		Assert.NotNull(LaunchOptions.Parse(args).Error);
	}

	[Fact]
	public void AgentApi_ResearchFlagsAreCarried()
	{
		var options = LaunchOptions.Parse(new[]
		{
			"--server", "--agent-api", "7900", "--agent-unbounded", "--agent-omniscient",
		});

		Assert.Null(options.Error);
		Assert.True(options.AgentUnbounded);
		Assert.True(options.AgentOmniscient);
		Assert.Contains("unbounded", options.ToString());
		Assert.Contains("omniscient", options.ToString());
	}

	[Theory]
	[InlineData("")]
	[InlineData("nope")]
	[InlineData("127.0.0.1")]
	[InlineData("127.0.0.1:0")]
	[InlineData("127.0.0.1:99999")]
	public void AgentApi_WithABadAddress_Fails(string value)
	{
		Assert.NotNull(LaunchOptions.Parse(new[] { "--server", "--agent-api", value }).Error);
	}

	[Fact]
	public void AgentApi_GivenTwice_Fails()
	{
		Assert.NotNull(LaunchOptions.Parse(new[]
		{
			"--server", "--agent-api", "7900", "--agent-api", "7901",
		}).Error);
	}

	[Fact]
	public void AgentApi_WithoutAPort_Fails()
	{
		Assert.NotNull(LaunchOptions.Parse(new[] { "--server", "--agent-api" }).Error);
	}

	// ---- the server browser (docs/LAN.md §4) -------------------------------

	[Fact]
	public void NoName_MeansTheMachinesOwn()
	{
		// Resolved where the environment is, not here: a parse stays a pure function.
		var options = LaunchOptions.Parse(new[] { "--server" });

		Assert.Null(options.Error);
		Assert.Null(options.ServerName);
		Assert.True(options.Advertise);
	}

	[Fact]
	public void Name_IsTakenAsGiven()
	{
		var options = LaunchOptions.Parse(new[] { "--server", "--name", "the kitchen box" });

		Assert.Null(options.Error);
		Assert.Equal("the kitchen box", options.ServerName);
	}

	[Fact]
	public void Name_WithoutAName_Fails()
	{
		Assert.NotNull(LaunchOptions.Parse(new[] { "--server", "--name" }).Error);
		Assert.NotNull(LaunchOptions.Parse(new[] { "--name", "--no-bots" }).Error);
	}

	[Fact]
	public void Name_GivenTwice_Fails()
	{
		Assert.NotNull(LaunchOptions.Parse(new[] { "--server", "--name", "a", "--name", "b" }).Error);
	}

	[Fact]
	public void NoAdvertise_IsAcceptedWithoutAModeFlag()
	{
		// A process with no mode flag picks one in the main menu and may end up
		// hosting from there, so neither of these needs --server to make sense.
		var options = LaunchOptions.Parse(new[] { "--no-advertise", "--name", "quiet" });

		Assert.Null(options.Error);
		Assert.Equal(LaunchMode.Offline, options.Mode);
		Assert.False(options.Advertise);
		Assert.Equal("quiet", options.ServerName);
	}

	[Theory]
	[InlineData("192.168.1.20", "192.168.1.20", 7777)]
	[InlineData("192.168.1.20:7778", "192.168.1.20", 7778)]
	[InlineData("  hostbox.local:7777  ", "hostbox.local", 7777)]
	[InlineData("[::1]:7779", "::1", 7779)]
	public void TypedAddresses_ParseTheSameWayTheFlagDoes(string text, string host, int port)
	{
		Assert.True(LaunchOptions.TryParseAddress(text, out string parsedHost, out int parsedPort, out _));
		Assert.Equal(host, parsedHost);
		Assert.Equal(port, parsedPort);
	}

	[Theory]
	[InlineData("")]
	[InlineData("   ")]
	[InlineData("192.168.1.20:70000")]
	[InlineData("192.168.1.20:")]
	[InlineData("[::1")]
	public void TypedAddresses_ThatAreNotAddresses_ComeBackWithASentence(string text)
	{
		Assert.False(LaunchOptions.TryParseAddress(text, out _, out _, out string error));
		Assert.False(string.IsNullOrWhiteSpace(error));
	}

	[Theory]
	[InlineData("127.0.0.1", true)]
	[InlineData("127.1.2.3", true)]
	[InlineData("localhost", true)]
	[InlineData("::1", true)]
	[InlineData("0.0.0.0", false)]
	[InlineData("203.0.113.10", false)]
	[InlineData("", false)]
	[InlineData(null, false)]
	public void Loopback_IsRecognizedTextually_AndAnythingElseIsTreatedAsPublic(string host, bool loopback)
	{
		Assert.Equal(loopback, LaunchOptions.IsLoopback(host));
	}
}
