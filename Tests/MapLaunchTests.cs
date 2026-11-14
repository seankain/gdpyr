using System;
using System.Collections.Generic;
using Gdpyr.Core;
using Gdpyr.Sim.Level;
using Xunit;

namespace Gdpyr.Tests;

/// <summary>
/// <c>--map</c> and <c>--practice</c> (docs/LEVEL_EDITOR.md §8.7, §9.5), and the one parser a
/// typed map reference goes through (<see cref="MapNames"/>, §9.1).
/// </summary>
public class MapLaunchTests
{
	// ---- --practice ------------------------------------------------------------------------

	[Fact]
	public void PracticeIsAnOfflineRoundWithNoMenu()
	{
		var options = LaunchOptions.Parse(new[] { "--practice" });

		Assert.Null(options.Error);
		Assert.Equal(LaunchMode.Offline, options.Mode);
		Assert.True(options.Practice);
		Assert.True(options.HasLocalPlayer);
		Assert.Contains("practice", options.ToString());
	}

	[Fact]
	public void NoFlagsIsNotPractice()
	{
		Assert.False(LaunchOptions.Parse(Array.Empty<string>()).Practice);
		Assert.False(LaunchOptions.Parse(new[] { "--listen" }).Practice);
	}

	[Theory]
	[InlineData("--server")]
	[InlineData("--listen")]
	[InlineData("--client", "127.0.0.1")]
	public void PracticeIsNotAnotherMode(params string[] mode)
	{
		var before = new List<string> { "--practice" };
		before.AddRange(mode);
		var after = new List<string>(mode) { "--practice" };

		Assert.Contains("--practice", LaunchOptions.Parse(before).Error);
		Assert.Contains("--practice", LaunchOptions.Parse(after).Error);
	}

	[Fact]
	public void PracticeIsRefusedWhereNobodyCanPlay()
	{
		Assert.Contains("--playdemo", LaunchOptions.Parse(new[] { "--practice", "--playdemo", "a.demo" }).Error);
		Assert.Contains("dedicated server", LaunchOptions.Parse(new[] { "--practice" }, dedicatedServer: true).Error);
		Assert.Contains("twice", LaunchOptions.Parse(new[] { "--practice", "--practice" }).Error);
		Assert.Contains("--agent-api", LaunchOptions.Parse(new[] { "--practice", "--agent-api", "7900" }).Error);
	}

	[Fact]
	public void PracticeTakesTheRoundsOtherFlags()
	{
		var options = LaunchOptions.Parse(new[] { "--practice", "--bots", "3:1", "--record", "p.demo", "--map", "greybox" });

		Assert.Null(options.Error);
		Assert.True(options.Practice);
		Assert.Equal(3, options.GroundBots);
		Assert.Equal("greybox", options.MapReference);
	}

	// ---- --map -----------------------------------------------------------------------------

	[Theory]
	[InlineData("--server")]
	[InlineData("--listen")]
	[InlineData("--practice")]
	public void MapIsTheAuthoritysToChoose(string mode)
	{
		var options = LaunchOptions.Parse(new[] { mode, "--map", "hills" });

		Assert.Null(options.Error);
		Assert.Equal("hills", options.MapReference);
		Assert.Contains("map hills", options.ToString());
	}

	[Fact]
	public void ADedicatedServerTakesAMapWithNoModeFlag()
	{
		var options = LaunchOptions.Parse(new[] { "--map", "user://maps/ridge.map" }, dedicatedServer: true);

		Assert.Null(options.Error);
		Assert.Equal(LaunchMode.Server, options.Mode);
		Assert.Equal("user://maps/ridge.map", options.MapReference);
	}

	[Fact]
	public void NoMapIsTheCatalogsDefault()
	{
		Assert.Null(LaunchOptions.Parse(new[] { "--server" }).MapReference);
	}

	[Fact]
	public void AClientAndTheMenuHaveNoMapToGive()
	{
		Assert.Contains("client plays the map its server names",
			LaunchOptions.Parse(new[] { "--client", "10.0.0.2", "--map", "hills" }).Error);
		Assert.Contains("needs --server, --listen or --practice", LaunchOptions.Parse(new[] { "--map", "hills" }).Error);
		Assert.Contains("needs --server, --listen or --practice",
			LaunchOptions.Parse(new[] { "--playdemo", "a.demo", "--map", "hills" }).Error);
	}

	[Theory]
	[InlineData("--map")]
	[InlineData("--map", "--server")]
	public void MapNeedsAValue(params string[] args)
	{
		Assert.Contains("--map needs a map", LaunchOptions.Parse(args).Error);
	}

	[Theory]
	[InlineData("Hills")]
	[InlineData("a b")]
	[InlineData("../hills")]
	[InlineData("hills.tscn")]
	[InlineData("-hills")]
	public void AMapThatIsNoReferenceIsRefusedOnTheCommandLine(string text)
	{
		string error = LaunchOptions.Parse(new[] { "--server", "--map", text }).Error;

		Assert.NotNull(error);
		Assert.StartsWith("--map", error);
	}

	[Fact]
	public void MapTwiceIsRefused()
	{
		Assert.Contains("twice", LaunchOptions.Parse(new[] { "--server", "--map", "a", "--map", "b" }).Error);
	}

	// ---- names -----------------------------------------------------------------------------

	[Theory]
	[InlineData("hills")]
	[InlineData("ridge_2")]
	[InlineData("a")]
	[InlineData("0-9_z")]
	[InlineData("  padded  ")]
	public void ANameIsLowerCaseLettersDigitsUnderscoresAndHyphens(string text)
	{
		Assert.True(MapNames.TryParseName(text, out string name, out string error), error);
		Assert.Equal(text.Trim(), name);
		Assert.Equal(name + ".map", MapNames.FileName(name));
	}

	[Theory]
	[InlineData(null, "needs a name")]
	[InlineData("", "needs a name")]
	[InlineData("   ", "needs a name")]
	[InlineData("Hills", "'H'")]
	[InlineData("hills.map", "'.'")]
	[InlineData("../x", "'.'")]
	[InlineData("a/b", "'/'")]
	[InlineData("a\\b", "'\\'")]
	[InlineData("C:x", "'C'")]
	[InlineData("a b", "' '")]
	[InlineData("é", "'é'")]
	[InlineData("a\u0000b", "\\u0000")]
	[InlineData("-x", "start with '-'")]
	public void EverythingElseIsNotAName(string text, string why)
	{
		Assert.False(MapNames.TryParseName(text, out string name, out string error));
		Assert.Null(name);
		Assert.Contains(why, error);
	}

	[Fact]
	public void ANameIsAtMost48Characters()
	{
		Assert.True(MapNames.TryParseName(new string('a', 48), out _, out _));
		Assert.False(MapNames.TryParseName(new string('a', 49), out _, out string error));
		Assert.Contains("48", error);
	}

	[Theory]
	[InlineData("deadbeef", true)]
	[InlineData("DEADBEEF00", true)]
	[InlineData("deadbee", false)]
	[InlineData("deadbeeg", false)]
	[InlineData("", false)]
	[InlineData(null, false)]
	public void AHashPrefixIsEightToSixtyFourHexDigits(string text, bool expected)
	{
		Assert.Equal(expected, MapNames.IsHashPrefix(text));
	}

	[Fact]
	public void AWholeHashIsAPrefixAndOneDigitMoreIsNot()
	{
		Assert.True(MapNames.IsHashPrefix(new string('a', 64)));
		Assert.False(MapNames.IsHashPrefix(new string('a', 65)));
	}

	// ---- references ------------------------------------------------------------------------

	private static readonly string[] Hashes =
	{
		"0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
		"0123456700000000000000000000000000000000000000000000000000000000",
		"ffeeddcc00000000000000000000000000000000000000000000000000000000",
	};

	private static MapReferenceKind Resolve(string text, out string key, out string error) =>
		MapNames.Resolve(text, id => id is "greybox" or "hills", name => name is "ridge" or "greybox" or "deadbeef",
			Hashes, out key, out error);

	[Fact]
	public void APathEndingDotMapIsAPathAndNothingElse()
	{
		Assert.Equal(MapReferenceKind.Path, Resolve("/srv/maps/greybox.map", out string key, out _));
		Assert.Equal("/srv/maps/greybox.map", key);
		Assert.Equal(MapReferenceKind.Path, Resolve("user://maps/Ridge.MAP", out _, out _));
		Assert.Equal(MapReferenceKind.Path, Resolve("res://Maps/hills.map", out _, out _));
	}

	[Fact]
	public void ACatalogIdComesBeforeALibraryName()
	{
		Assert.Equal(MapReferenceKind.Catalog, Resolve("greybox", out string key, out _));
		Assert.Equal("greybox", key);
		Assert.Equal(MapReferenceKind.Library, Resolve("ridge", out key, out _));
		Assert.Equal("ridge", key);
	}

	[Fact]
	public void ALibraryNameComesBeforeAHash()
	{
		// "deadbeef" is a name in the library and could be a hash's start: it is the name.
		Assert.Equal(MapReferenceKind.Library, Resolve("deadbeef", out string key, out _));
		Assert.Equal("deadbeef", key);
	}

	[Fact]
	public void AHashPrefixNamesTheOneMapItStartsAndNotTwo()
	{
		Assert.Equal(MapReferenceKind.Hash, Resolve("FFEEDDCC", out string key, out _));
		Assert.Equal(Hashes[2], key);
		Assert.Equal(MapReferenceKind.Hash, Resolve("0123456789", out key, out _));
		Assert.Equal(Hashes[0], key);

		Assert.Equal(MapReferenceKind.None, Resolve("01234567", out key, out string error));
		Assert.Null(key);
		Assert.Contains("2 maps' hashes", error);
	}

	[Fact]
	public void TheSameHashTwiceIsOneMap()
	{
		// A bundled map that is in the cache too is one map, not two.
		MapReferenceKind kind = MapNames.Resolve("ffeeddcc", _ => false, _ => false,
			new[] { Hashes[2], Hashes[2] }, out string key, out _);
		Assert.Equal(MapReferenceKind.Hash, kind);
		Assert.Equal(Hashes[2], key);
	}

	[Theory]
	[InlineData("nowhere", "no map is called 'nowhere'")]
	[InlineData("abababab", "no map is called")]
	[InlineData("Nowhere", "is not a map")]
	[InlineData("", "empty")]
	[InlineData("a\tb.map", "control character")]
	public void WhatNamesNothingSaysSo(string text, string why)
	{
		Assert.Equal(MapReferenceKind.None, Resolve(text, out string key, out string error));
		Assert.Null(key);
		Assert.Contains(why, error);
	}
}
