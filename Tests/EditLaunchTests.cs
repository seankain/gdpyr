using System;
using Gdpyr.Core;
using Xunit;

namespace Gdpyr.Tests;

/// <summary>
/// <c>--edit [name]</c> (docs/LEVEL_EDITOR.md §8.1, LE4): the map editor from the command
/// line, on one's own map of that name or the editor's list, and nothing else alongside it.
/// </summary>
public class EditLaunchTests
{
	[Fact]
	public void Edit_AloneIsTheEditorsList_Offline()
	{
		var options = LaunchOptions.Parse(new[] { "--edit" });
		Assert.Null(options.Error);
		Assert.True(options.Edit);
		Assert.Null(options.EditName);
		Assert.Equal(LaunchMode.Offline, options.Mode);
		Assert.Contains("editing", options.ToString());
	}

	[Fact]
	public void Edit_TakesALibraryName()
	{
		var options = LaunchOptions.Parse(new[] { "--edit", "ridge_2" });
		Assert.Null(options.Error);
		Assert.Equal("ridge_2", options.EditName);
		Assert.EndsWith("editing ridge_2", options.ToString());

		Assert.Equal("ridge", LaunchOptions.Parse(new[] { "--edit", "ridge", "--no-advertise" }).EditName);
		Assert.Null(LaunchOptions.Parse(new[] { "--edit", "--no-advertise" }).EditName);
	}

	[Fact]
	public void NoEdit_IsNotTheEditor()
	{
		Assert.False(LaunchOptions.Parse(Array.Empty<string>()).Edit);
		Assert.False(LaunchOptions.Parse(new[] { "--practice" }).Edit);
		Assert.False(LaunchOptions.Offline.Edit);
	}

	[Theory]
	[InlineData("Ridge")]
	[InlineData("../ridge")]
	[InlineData("ridge.map")]
	[InlineData("a b")]
	public void Edit_RefusesWhatIsNotAName(string name) =>
		Assert.StartsWith("--edit:", LaunchOptions.Parse(new[] { "--edit", name }).Error);

	[Theory]
	[InlineData("--server")]
	[InlineData("--listen")]
	[InlineData("--client", "10.0.0.2")]
	public void Edit_RefusesAMode(params string[] mode)
	{
		var args = new System.Collections.Generic.List<string> { "--edit" };
		args.AddRange(mode);
		Assert.Contains("--edit", LaunchOptions.Parse(args).Error);
		args.Reverse();
		Assert.NotNull(LaunchOptions.Parse(args).Error);
	}

	[Theory]
	[InlineData("--practice")]
	[InlineData("--playdemo", "a.demo")]
	[InlineData("--record", "a.demo")]
	[InlineData("--spectate")]
	public void Edit_RefusesPlayingAlongside(params string[] other)
	{
		var args = new System.Collections.Generic.List<string> { "--edit", "ridge" };
		args.AddRange(other);
		string error = LaunchOptions.Parse(args).Error;
		Assert.Contains("--edit and " + other[0], error);
	}

	[Fact]
	public void Edit_RefusesAMap_AServer_AndBeingGivenTwice()
	{
		Assert.Contains("--edit", LaunchOptions.Parse(new[] { "--edit", "--map", "hills" }).Error);
		Assert.Contains("dedicated server", LaunchOptions.Parse(new[] { "--edit" }, dedicatedServer: true).Error);
		Assert.Contains("twice", LaunchOptions.Parse(new[] { "--edit", "--edit" }).Error);
		Assert.Contains("--edit [name]", LaunchOptions.Usage);
	}
}
