using System.Collections.Generic;
using System.IO;
using System.Linq;
using Gdpyr.Core;
using Gdpyr.MapTool;
using Gdpyr.Sim.Level;
using Xunit;

namespace Gdpyr.Tests;

/// <summary>
/// The data maps bundled with the build (docs/LEVEL_EDITOR.md §9.1): each catalog entry's
/// file is in the checkout, hashes to what the entry says, reads, and passes the validator
/// — what <c>MapSceneTests</c> asks of a scene map, asked of a file.
/// </summary>
public class BundledMapTests
{
	public static IEnumerable<object[]> Bundled() =>
		MapCatalog.All.Where(m => m.Kind == MapKind.Data).Select(m => new object[] { m.Id });

	private static MapInfo Entry(string id)
	{
		Assert.True(MapCatalog.TryFind(id, out MapInfo map));
		return map;
	}

	private static byte[] FileOf(MapInfo map)
	{
		string path = Path.Combine(RepositoryRoot(), map.Path["res://".Length..]);
		Assert.True(File.Exists(path), $"{map} names {map.Path}, which is not in the checkout");
		return File.ReadAllBytes(path);
	}

	[Fact]
	public void ThereIsOne()
	{
		Assert.Single(Bundled());
	}

	[Theory]
	[MemberData(nameof(Bundled))]
	public void ItIsADataMapUnderMaps(string id)
	{
		MapInfo map = Entry(id);

		Assert.True(map.IsValid);
		Assert.True(map.IsData);
		Assert.Equal(MapCatalog.DataMapScenePath, map.ScenePath);
		Assert.Equal(MapCatalog.BundledDirectory + id + MapFile.Extension, map.Path);
		Assert.True(MapNames.TryParseName(id, out _, out _), "a bundled map's id is a map's name");
		Assert.Equal(MapNames.HashHexLength, map.Hash.Length);
		Assert.Equal(map.Hash.ToLowerInvariant(), map.Hash);
	}

	[Theory]
	[MemberData(nameof(Bundled))]
	public void ItsFileHashesToItsEntry(string id)
	{
		MapInfo map = Entry(id);

		Assert.Equal(map.Hash, MapFile.HashHex(FileOf(map)));
	}

	[Theory]
	[MemberData(nameof(Bundled))]
	public void ItReadsAndPlaysWithNoWarning(string id)
	{
		MapInfo map = Entry(id);

		Assert.True(MapFile.TryRead(FileOf(map), out MapDocument document, out string error), error);
		MapReport report = MapValidator.Validate(document);
		Assert.True(report.IsPlayable, string.Join("; ", report.Issues));
		Assert.Equal(0, report.Warnings);
		Assert.Equal(map.Name, document.Name);
		Assert.Equal(PrefabSpecs.Count, document.PrefabCatalogCount);
	}

	[Theory]
	[MemberData(nameof(Bundled))]
	public void ItHoldsWhatARoundNeeds(string id)
	{
		// MAPS.md §3.1, counted from the file: the numbers LE3's engine check finds in the world.
		Assert.True(MapFile.TryRead(FileOf(Entry(id)), out MapDocument document, out _));

		Assert.Equal(1, document.CountOf(PrefabGroup.SpawnLine));
		Assert.Equal(1, document.CountOf(PrefabGroup.WeaponLocker));
		Assert.InRange(document.CountOf(PrefabGroup.Barracks), 1, 4);
		Assert.InRange(document.CountOf(PrefabGroup.ResourceNode), 1, 8);
		Assert.Equal(1, document.CountOf(PrefabGroup.StrategistAnchor));
		Assert.Equal(1, document.CountOf(PrefabGroup.VehicleKiosk));
	}

	[Fact]
	public void TheHillsAreTheGeneratorsSeedThree()
	{
		// What the catalog's comment says it is, byte for byte: the bundled map can be made
		// again from the generator, and a generator that changes is seen to change it.
		byte[] made = MapFile.Write(MapGenerator.Generate(3, "Hills"));

		Assert.Equal(FileOf(Entry("hills")), made);
	}

	[Fact]
	public void TheGreyboxIsStillTheDefaultAndFirst()
	{
		Assert.Equal(MapKind.Scene, MapCatalog.Default.Kind);
		int firstData = MapCatalog.All.ToList().FindIndex(m => m.IsData);
		int lastScene = MapCatalog.All.ToList().FindLastIndex(m => !m.IsData);
		Assert.True(firstData > lastScene, "scene maps are listed before the bundled data maps");
	}

	private static string RepositoryRoot()
	{
		var directory = new DirectoryInfo(System.AppContext.BaseDirectory);
		while (directory != null && !File.Exists(Path.Combine(directory.FullName, "project.godot")))
		{
			directory = directory.Parent;
		}

		Assert.NotNull(directory);
		return directory.FullName;
	}
}
