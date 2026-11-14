using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Gdpyr.Core;
using Gdpyr.Sim;
using Xunit;

namespace Gdpyr.Tests;

/// <summary>
/// What a map scene has to hold for a round to be played on it (docs/MAPS.md §3), checked
/// against every map in <see cref="MapCatalog"/> and against the template new maps inherit
/// from. Read from the .tscn text, following inherited and instanced scenes, so it needs no
/// Godot: what the engine finds by group and by script is what is asserted here.
/// </summary>
public class MapSceneTests
{
	/// <summary>The scene new maps inherit from (docs/MAPS.md §2). Not in the catalog itself.</summary>
	public const string TemplatePath = "res://Scenes/Maps/MapBase.tscn";

	private const string WorldRootScript = "res://Scripts/Core/WorldRoot.cs";
	private const string BarracksScript = "res://Scripts/Rts/Barracks.cs";
	private const string ResourceNodeScript = "res://Scripts/Rts/ResourceNode.cs";
	private const string WeaponLockerScript = "res://Scripts/Fps/WeaponLocker.cs";

	public static IEnumerable<object[]> Maps()
	{
		yield return new object[] { TemplatePath };
		foreach (MapInfo map in MapCatalog.All)
		{
			// A data map's scene is the one that builds any file (docs/LEVEL_EDITOR.md §9.1):
			// what its map holds is BundledMapTests', asked of the file by the validator.
			if (map.Kind == MapKind.Scene)
			{
				yield return new object[] { map.ScenePath };
			}
		}
	}

	[Theory]
	[MemberData(nameof(Maps))]
	public void TheRootIsAWorldRoot(string scenePath)
	{
		// Without it the session never leaves the menu phase and nothing ticks.
		SceneNode root = MapScene.Load(scenePath).Root;
		Assert.Equal(WorldRootScript, root.Script);
	}

	[Theory]
	[MemberData(nameof(Maps))]
	public void ItHasPlayerSpawns(string scenePath)
	{
		Assert.NotEmpty(MapScene.Load(scenePath).InGroup("player_spawn"));
	}

	[Theory]
	[MemberData(nameof(Maps))]
	public void ItHasANavigationRegionAndGeometryToBakeIt(string scenePath)
	{
		MapScene scene = MapScene.Load(scenePath);
		Assert.Contains(scene.InGroup("navigation_region"), n => n.Type == "NavigationRegion3D");
		Assert.NotEmpty(scene.InGroup("navmesh"));
	}

	[Theory]
	[MemberData(nameof(Maps))]
	public void ItHasBarracksWithADoorAndNoMoreThanTheWireAddresses(string scenePath)
	{
		MapScene scene = MapScene.Load(scenePath);
		List<SceneNode> barracks = scene.WithScript(BarracksScript).ToList();

		Assert.InRange(barracks.Count, 1, SimConfig.MaxBarracks);
		foreach (SceneNode node in barracks)
		{
			Assert.True(scene.Has(node.Path + "/SpawnPoint"), $"{scenePath}: {node.Path} has no SpawnPoint child");
		}
	}

	[Theory]
	[MemberData(nameof(Maps))]
	public void ItHasResourceNodesAndNoMoreThanTheWireAddresses(string scenePath)
	{
		Assert.InRange(MapScene.Load(scenePath).WithScript(ResourceNodeScript).Count(), 1, SimConfig.MaxResourceNodes);
	}

	[Theory]
	[MemberData(nameof(Maps))]
	public void ItHasAWeaponLocker(string scenePath)
	{
		Assert.NotEmpty(MapScene.Load(scenePath).WithScript(WeaponLockerScript));
	}

	[Theory]
	[MemberData(nameof(Maps))]
	public void CoverIsBoxesTheBoardCanHold(string scenePath)
	{
		// Anything else in the group is skipped at load (UnitManager.CollectCover).
		List<SceneNode> cover = MapScene.Load(scenePath).InGroup("cover").ToList();
		Assert.All(cover, n => Assert.Equal("CSGBox3D", n.Type));
		Assert.InRange(cover.Count, 0, CoverBoard.LevelCapacity);
	}

	[Theory]
	[MemberData(nameof(Maps))]
	public void WhatThePlayIsAboutIsInsideTheField(string scenePath)
	{
		// Exit slots, landing spots, aircraft destinations and drones are kept inside this
		// square whatever the map is (Cargo.FieldHalfExtentMeters).
		MapScene scene = MapScene.Load(scenePath);
		IEnumerable<SceneNode> markers = scene.InGroup("player_spawn")
			.Concat(scene.InGroup("strategist_anchor"))
			.Concat(scene.WithScript(BarracksScript))
			.Concat(scene.WithScript(BarracksScript).Select(b => scene.Get(b.Path + "/SpawnPoint")).Where(n => n != null))
			.Concat(scene.WithScript(ResourceNodeScript))
			.Concat(scene.WithScript(WeaponLockerScript));

		foreach (SceneNode node in markers)
		{
			Vec3 at = scene.GlobalOrigin(node.Path);
			Assert.True(MathF.Abs(at.X) <= Cargo.FieldHalfExtentMeters && MathF.Abs(at.Z) <= Cargo.FieldHalfExtentMeters,
				$"{scenePath}: {node.Path} is at ({at.X}, {at.Z}), outside ±{Cargo.FieldHalfExtentMeters} m");
		}
	}

	[Fact]
	public void TheTemplateIsNotAMapOfItsOwn()
	{
		// It is a starting point: a map is a scene that inherits it and has a catalog entry.
		Assert.DoesNotContain(MapCatalog.All, m => m.ScenePath == TemplatePath);
	}

	[Fact]
	public void TheReaderFollowsInheritanceAndOverrides()
	{
		// An inherited scene that moves one of the template's nodes and adds one of its own:
		// what MapScene reads is what the engine instantiates.
		string directory = Path.Combine(Path.GetTempPath(), "gdpyr-mapscene-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(directory);
		try
		{
			string child = Path.Combine(directory, "Child.tscn");
			File.WriteAllText(child, string.Join('\n',
				"[gd_scene format=3]",
				$"[ext_resource type=\"PackedScene\" path=\"{TemplatePath}\" id=\"1_base\"]",
				$"[ext_resource type=\"Script\" path=\"{ResourceNodeScript}\" id=\"2_node\"]",
				"[node name=\"Child\" instance=ExtResource(\"1_base\")]",
				"[node name=\"ResourceNode1\" parent=\".\"]",
				"transform = Transform3D(1, 0, 0, 0, 1, 0, 0, 0, 1, 7, 0, -3)",
				"[node name=\"ResourceNode2\" type=\"Node3D\" parent=\".\" groups=[\"resource_node\"]]",
				"script = ExtResource(\"2_node\")"));

			MapScene scene = MapScene.LoadFile(child);
			Assert.Equal(WorldRootScript, scene.Root.Script);
			Assert.Equal(2, scene.WithScript(ResourceNodeScript).Count());
			Vec3 moved = scene.GlobalOrigin("ResourceNode1");
			Assert.Equal(7f, moved.X, 3);
			Assert.Equal(-3f, moved.Z, 3);

			// The barracks comes from a scene the template instances, a level further down.
			Assert.True(scene.Has("Barracks1/SpawnPoint"));
		}
		finally
		{
			Directory.Delete(directory, recursive: true);
		}
	}

	// ---- a .tscn reader: nodes, groups, scripts, transforms ----------------------

	public readonly record struct Vec3(float X, float Y, float Z);

	public sealed class SceneNode
	{
		public string Path = ".";
		public string Type;
		public string Script;
		public HashSet<string> Groups = new(StringComparer.Ordinal);

		/// <summary>Basis rows then origin, as a .tscn writes a Transform3D. Null is identity.</summary>
		public float[] Transform;

		public SceneNode Clone(string path) => new()
		{
			Path = path,
			Type = Type,
			Script = Script,
			Groups = new HashSet<string>(Groups, StringComparer.Ordinal),
			Transform = Transform,
		};
	}

	public sealed class MapScene
	{
		private static readonly Regex Attribute = new("(\\w+)=(\"[^\"]*\"|ExtResource\\(\"[^\"]*\"\\)|\\[[^\\]]*\\])");
		private static readonly Regex Quoted = new("\"([^\"]*)\"");

		private readonly Dictionary<string, SceneNode> _nodes;

		private MapScene(Dictionary<string, SceneNode> nodes) => _nodes = nodes;

		public SceneNode Root => _nodes["."];

		public static MapScene Load(string resPath) => new(Flatten(ToFile(resPath), 0));

		public static MapScene LoadFile(string file) => new(Flatten(file, 0));

		public bool Has(string path) => _nodes.ContainsKey(path);

		public SceneNode Get(string path) => _nodes.GetValueOrDefault(path);

		public IEnumerable<SceneNode> InGroup(string group) => _nodes.Values.Where(n => n.Groups.Contains(group));

		public IEnumerable<SceneNode> WithScript(string script) => _nodes.Values.Where(n => n.Script == script);

		/// <summary>Where a node is in the map: its transform composed with every ancestor's.</summary>
		public Vec3 GlobalOrigin(string path)
		{
			var chain = new List<SceneNode>();
			for (string at = path; ; at = ParentOf(at))
			{
				chain.Add(_nodes[at]);
				if (at == ".")
				{
					break;
				}
			}

			// Applied from the node outwards: p' = B·p + o at each level.
			float x = 0f, y = 0f, z = 0f;
			foreach (SceneNode node in chain)
			{
				float[] t = node.Transform;
				if (t == null)
				{
					continue;
				}

				(x, y, z) = (
					(t[0] * x) + (t[1] * y) + (t[2] * z) + t[9],
					(t[3] * x) + (t[4] * y) + (t[5] * z) + t[10],
					(t[6] * x) + (t[7] * y) + (t[8] * z) + t[11]);
			}

			return new Vec3(x, y, z);
		}

		private static string ParentOf(string path)
		{
			int slash = path.LastIndexOf('/');
			return slash < 0 ? "." : path[..slash];
		}

		private static string ToFile(string resPath)
		{
			Assert.StartsWith("res://", resPath);
			return Path.Combine(RepositoryRoot(), resPath["res://".Length..]);
		}

		private static Dictionary<string, SceneNode> Flatten(string file, int depth)
		{
			Assert.True(depth < 8, $"{file}: scenes instanced more than 8 deep");
			Assert.True(File.Exists(file), $"{file} is not in the checkout");

			var resources = new Dictionary<string, string>(StringComparer.Ordinal);
			var nodes = new Dictionary<string, SceneNode>(StringComparer.Ordinal);
			SceneNode current = null;

			foreach (string raw in File.ReadLines(file))
			{
				string line = raw.Trim();
				if (line.StartsWith("[ext_resource ", StringComparison.Ordinal))
				{
					Dictionary<string, string> attributes = Attributes(line);
					resources[Unquote(attributes["id"])] = Unquote(attributes["path"]);
					current = null;
				}
				else if (line.StartsWith("[node ", StringComparison.Ordinal))
				{
					current = Node(line, resources, nodes, depth);
				}
				else if (line.StartsWith('['))
				{
					current = null;
				}
				else if (current != null && line.StartsWith("script = ExtResource(", StringComparison.Ordinal))
				{
					current.Script = resources[Quoted.Match(line).Groups[1].Value];
				}
				else if (current != null && line.StartsWith("transform = Transform3D(", StringComparison.Ordinal))
				{
					string[] parts = line["transform = Transform3D(".Length..^1].Split(',');
					current.Transform = parts.Select(p => float.Parse(p, CultureInfo.InvariantCulture)).ToArray();
					Assert.Equal(12, current.Transform.Length);
				}
			}

			Assert.True(nodes.ContainsKey("."), $"{file} has no root node");
			return nodes;
		}

		private static SceneNode Node(string line, Dictionary<string, string> resources,
			Dictionary<string, SceneNode> nodes, int depth)
		{
			Dictionary<string, string> attributes = Attributes(line);
			string name = Unquote(attributes["name"]);
			string path = !attributes.TryGetValue("parent", out string parent)
				? "."
				: Unquote(parent) == "." ? name : Unquote(parent) + "/" + name;

			// An instanced (or, at the root, inherited) scene brings its whole tree with it.
			if (attributes.TryGetValue("instance", out string instance))
			{
				string id = Quoted.Match(instance).Groups[1].Value;
				foreach ((string subPath, SceneNode sub) in Flatten(ToFile(resources[id]), depth + 1))
				{
					string at = subPath == "." ? path : path == "." ? subPath : path + "/" + subPath;
					nodes[at] = sub.Clone(at);
				}
			}

			// A node already there is one this scene overrides: its properties win, groups add.
			if (!nodes.TryGetValue(path, out SceneNode node))
			{
				node = new SceneNode { Path = path };
				nodes[path] = node;
			}

			if (attributes.TryGetValue("type", out string type))
			{
				node.Type = Unquote(type);
			}

			if (attributes.TryGetValue("groups", out string groups))
			{
				foreach (System.Text.RegularExpressions.Match group in Quoted.Matches(groups))
				{
					node.Groups.Add(group.Groups[1].Value);
				}
			}

			return node;
		}

		private static Dictionary<string, string> Attributes(string header)
		{
			var attributes = new Dictionary<string, string>(StringComparer.Ordinal);
			foreach (System.Text.RegularExpressions.Match match in Attribute.Matches(header))
			{
				attributes[match.Groups[1].Value] = match.Groups[2].Value;
			}
			return attributes;
		}

		private static string Unquote(string value) => value.Trim('"');
	}

	private static string RepositoryRoot()
	{
		var directory = new DirectoryInfo(AppContext.BaseDirectory);
		while (directory != null && !File.Exists(Path.Combine(directory.FullName, "project.godot")))
		{
			directory = directory.Parent;
		}

		Assert.NotNull(directory);
		return directory.FullName;
	}
}
