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
}
