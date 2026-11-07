using System;
using System.Collections.Generic;

namespace Gdpyr.Core;

/// <summary>A map a round can be played on: what it is called and which scene it is.</summary>
public readonly struct MapInfo
{
	/// <summary>
	/// What the map is called on the wire and in a host's settings. Stable across
	/// builds, unlike its place in <see cref="MapCatalog.All"/>: a client is told the
	/// host's map by this, and two builds that list their maps in a different order
	/// must still agree on which one it is.
	/// </summary>
	public readonly string Id;

	/// <summary>What the host menu calls it.</summary>
	public readonly string Name;

	/// <summary>The scene the map is. Loaded by the menu on the host and on every client.</summary>
	public readonly string ScenePath;

	/// <summary>One line under the menu's picker.</summary>
	public readonly string Description;

	public MapInfo(string id, string name, string scenePath, string description)
	{
		Id = id;
		Name = name;
		ScenePath = scenePath;
		Description = description;
	}

	public bool IsValid => !string.IsNullOrEmpty(Id) && !string.IsNullOrEmpty(ScenePath);

	public override string ToString() => $"{Name} ({Id})";
}

/// <summary>
/// The maps this build can host and join (docs/LAN.md §3.4).
///
/// A list in code rather than a scan of a folder: a map is a scene with spawn
/// points, resource nodes, a barracks, cover and a navigation mesh in it, and a
/// scene that is merely in the folder is not one. Adding a map is one entry here,
/// for a scene that inherits <c>Scenes/Maps/MapBase.tscn</c> (docs/MAPS.md);
/// <c>Tests/MapSceneTests.cs</c> holds every entry's scene to what a round needs.
///
/// Engine-free, so "what does a host's unknown map id resolve to" is a test.
/// </summary>
public static class MapCatalog
{
	private static readonly MapInfo[] Maps =
	{
		new("greybox", "Greybox", "res://Scenes/Test.tscn",
			"the prototype map: three resource nodes, a barracks, cover and the vehicle strip"),
	};

	/// <summary>Every map, in the order the host menu lists them.</summary>
	public static IReadOnlyList<MapInfo> All => Maps;

	public static int Count => Maps.Length;

	/// <summary>What a process plays on until a host says otherwise.</summary>
	public static MapInfo Default => Maps[0];

	/// <summary>The map with this id. Ids are matched exactly: they are written by a build, not by a person.</summary>
	public static bool TryFind(string id, out MapInfo map)
	{
		int index = IndexOf(id);
		map = index >= 0 ? Maps[index] : default;
		return index >= 0;
	}

	/// <summary>Where the map with this id is in <see cref="All"/>, or -1.</summary>
	public static int IndexOf(string id)
	{
		if (string.IsNullOrEmpty(id))
		{
			return -1;
		}

		for (int i = 0; i < Maps.Length; i++)
		{
			if (string.Equals(Maps[i].Id, id, StringComparison.Ordinal))
			{
				return i;
			}
		}

		return -1;
	}
}
