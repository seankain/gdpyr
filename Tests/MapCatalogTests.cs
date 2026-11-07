using System.Collections.Generic;
using Gdpyr.Core;
using Xunit;

namespace Gdpyr.Tests;

public class MapCatalogTests
{
	[Fact]
	public void TheDefaultIsTheGreybox()
	{
		// Every path into the world loaded this scene before there was a catalog; a
		// launch that picks nothing must still load it.
		Assert.Equal("res://Scenes/Test.tscn", MapCatalog.Default.ScenePath);
		Assert.Equal(0, MapCatalog.IndexOf(MapCatalog.Default.Id));
	}

	[Fact]
	public void EveryMapHasAnIdANameAndAScene()
	{
		Assert.True(MapCatalog.Count > 0);

		foreach (MapInfo map in MapCatalog.All)
		{
			Assert.True(map.IsValid);
			Assert.False(string.IsNullOrWhiteSpace(map.Name));
			Assert.StartsWith("res://", map.ScenePath);
			Assert.EndsWith(".tscn", map.ScenePath);
		}
	}

	[Fact]
	public void IdsAreUnique()
	{
		// A client loads the map its server names by id; two maps under one id is a
		// client on the wrong floor.
		var seen = new HashSet<string>();
		foreach (MapInfo map in MapCatalog.All)
		{
			Assert.True(seen.Add(map.Id), $"map id '{map.Id}' is listed twice");
		}
	}

	[Fact]
	public void TheScenesAreInTheCheckout()
	{
		foreach (MapInfo map in MapCatalog.All)
		{
			string path = System.IO.Path.Combine(RepositoryRoot(), map.ScenePath["res://".Length..]);
			Assert.True(System.IO.File.Exists(path), $"{map} names {map.ScenePath}, which is not in the checkout");
		}
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("GREYBOX")]
	[InlineData("no-such-map")]
	public void AnIdTheCatalogDoesNotHaveIsNotFound(string id)
	{
		Assert.False(MapCatalog.TryFind(id, out MapInfo map));
		Assert.False(map.IsValid);
		Assert.Equal(-1, MapCatalog.IndexOf(id));
	}

	[Fact]
	public void AnIdTheCatalogHasIsFound()
	{
		Assert.True(MapCatalog.TryFind(MapCatalog.Default.Id, out MapInfo map));
		Assert.Equal(MapCatalog.Default.ScenePath, map.ScenePath);
	}

	private static string RepositoryRoot()
	{
		var directory = new System.IO.DirectoryInfo(System.AppContext.BaseDirectory);
		while (directory != null && !System.IO.File.Exists(System.IO.Path.Combine(directory.FullName, "project.godot")))
		{
			directory = directory.Parent;
		}

		Assert.NotNull(directory);
		return directory.FullName;
	}
}
