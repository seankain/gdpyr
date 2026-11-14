using System;
using System.Collections.Generic;

namespace Gdpyr.Core;

/// <summary>What a map is made of (docs/LEVEL_EDITOR.md LE-D1).</summary>
public enum MapKind : byte
{
	/// <summary>A scene that inherits <c>Scenes/Maps/MapBase.tscn</c> (docs/MAPS.md).</summary>
	Scene,

	/// <summary>A <c>.map</c> file, which <see cref="MapCatalog.DataMapScenePath"/> builds the world from.</summary>
	Data,
}

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

	/// <summary>A scene or a file.</summary>
	public readonly MapKind Kind;

	/// <summary>
	/// A data map's file: <c>res://Maps/&lt;id&gt;.map</c> for one bundled with the build,
	/// a <c>user://</c> path or a path on the machine for one from the library. Null for a
	/// scene map.
	/// </summary>
	public readonly string Path;

	/// <summary>
	/// A data map's SHA-256, 64 lower-case hex digits (LE-D15): what the wire will name it
	/// by (LE7), and for a bundled map what its file must hash to. Null for a scene map.
	/// </summary>
	public readonly string Hash;

	public MapInfo(string id, string name, string scenePath, string description)
	{
		Id = id;
		Name = name;
		ScenePath = scenePath;
		Description = description;
		Kind = MapKind.Scene;
		Path = null;
		Hash = null;
	}

	private MapInfo(string id, string name, string path, string hash, string description)
	{
		Id = id ?? string.Empty;
		Name = name;
		ScenePath = MapCatalog.DataMapScenePath;
		Description = description;
		Kind = MapKind.Data;
		Path = path;
		Hash = hash;
	}

	/// <summary>
	/// A data map (docs/LEVEL_EDITOR.md §9.1). <paramref name="id"/> is its catalog id for
	/// one bundled with the build, and empty for one from the library, which the
	/// catalog does not list.
	/// </summary>
	public static MapInfo Data(string id, string name, string path, string hash, string description) =>
		new(id, name, path, hash, description);

	/// <summary>
	/// A scene map has an id and a scene. A data map has a file, and an id only when it is
	/// bundled: one from the library is not the catalog's and is named by its file.
	/// </summary>
	public bool IsValid => Kind == MapKind.Data
		? !string.IsNullOrEmpty(Path) && !string.IsNullOrEmpty(ScenePath)
		: !string.IsNullOrEmpty(Id) && !string.IsNullOrEmpty(ScenePath);

	public bool IsData => Kind == MapKind.Data;

	public override string ToString() =>
		Kind == MapKind.Data
			? $"{Name} ({(string.IsNullOrEmpty(Id) ? Path : Id)}, {(Hash is { Length: >= 8 } h ? h[..8] : "unhashed")})"
			: $"{Name} ({Id})";
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
/// Data maps bundled with the build are listed here too, after the scene maps
/// (docs/LEVEL_EDITOR.md §9.1): a <c>.map</c> file under <c>res://Maps/</c> with its hash,
/// which <c>Tests/BundledMapTests.cs</c> holds to the file and holds the file to the
/// validator. A file that is merely in the folder is not a map either.
///
/// Engine-free, so "what does a host's unknown map id resolve to" is a test.
/// </summary>
public static class MapCatalog
{
	/// <summary>The one scene every data map is built in (docs/LEVEL_EDITOR.md §7.1).</summary>
	public const string DataMapScenePath = "res://Scenes/Maps/MapHost.tscn";

	/// <summary>Where bundled data maps are kept.</summary>
	public const string BundledDirectory = "res://Maps/";

	private static readonly MapInfo[] Maps =
	{
		new("greybox", "Greybox", "res://Scenes/Test.tscn",
			"the prototype map: three resource nodes, a barracks, cover and the vehicle strip"),

		// docs/LEVEL_EDITOR.md LE3: `gdpyr-map generate --seed 3 --name Hills`, unchanged since.
		// The hash is the file's; BundledMapTests fails if either moves without the other.
		MapInfo.Data("hills", "Hills", BundledDirectory + "hills.map", BundledHillsHash,
			"a data map: rolling hills, three nodes, a barracks, cover and a bunker"),
	};

	/// <summary>The bundled hills' SHA-256.</summary>
	private const string BundledHillsHash = "6d0aa3ff6ebe3487e53d4f4488318b2ebe47ada2e27fb3ae568320a4a8450958";

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
