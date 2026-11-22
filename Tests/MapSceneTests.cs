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
	private const string WaterSurfaceScript = "res://Scripts/Tools/WaterSurfaceGenerator.cs";

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
		foreach (SceneNode node in RoundPieces(scene))
		{
			Vec3 at = scene.GlobalOrigin(node.Path);
			Assert.True(MathF.Abs(at.X) <= Cargo.FieldHalfExtentMeters && MathF.Abs(at.Z) <= Cargo.FieldHalfExtentMeters,
				$"{scenePath}: {node.Path} is at ({at.X}, {at.Z}), outside ±{Cargo.FieldHalfExtentMeters} m");
		}
	}

	[Theory]
	[MemberData(nameof(Maps))]
	public void NoColliderIsScaled(string scenePath)
	{
		// Godot's physics does not support a scaled body or shape (docs/MAPS.md §3.1, item 8).
		// Its segment–triangle test has an absolute epsilon in the shape's own space, so a
		// trimesh authored small and scaled up misses short rays: Gully's terrain, 2 m across and
		// scaled ×100, missed every ray of ±2 m or less on flat ground, so the structure check's
		// corner probes found no ground and every site was refused as too steep.
		MapScene scene = MapScene.Load(scenePath);
		foreach (SceneNode node in scene.Nodes.Where(n => IsCollider(scene, n)))
		{
			Vec3 scale = scene.GlobalScale(node.Path);
			Assert.True(MathF.Abs(scale.X - 1f) < 1e-3f && MathF.Abs(scale.Y - 1f) < 1e-3f && MathF.Abs(scale.Z - 1f) < 1e-3f,
				$"{scenePath}: {node.Path} ({node.Type ?? node.Instance}) is scaled ({scale.X}, {scale.Y}, {scale.Z});"
				+ " put the scale in the collider's vertices or the CSG's size, and scale only the mesh");
		}
	}

	/// <summary>
	/// A node the physics engine collides with: a body, a shape, the root of a CSG tree (its
	/// children are merged into its mesh, and only the root has a body), or an imported model,
	/// whose <c>-col</c> objects import as static bodies.
	/// </summary>
	private static bool IsCollider(MapScene scene, SceneNode node) =>
		node.Type is "StaticBody3D" or "RigidBody3D" or "CharacterBody3D" or "AnimatableBody3D" or "VehicleBody3D"
			or "Area3D" or "CollisionShape3D" or "CollisionPolygon3D"
		|| (IsCsg(node) && (node.Path == "." || !IsCsg(scene.Get(ParentPath(node.Path)))))
		|| node.Instance?.EndsWith(".glb", StringComparison.Ordinal) == true
		|| node.Instance?.EndsWith(".gltf", StringComparison.Ordinal) == true;

	private static bool IsCsg(SceneNode node) => node?.Type?.StartsWith("CSG", StringComparison.Ordinal) == true;

	[Theory]
	[MemberData(nameof(Maps))]
	public void NothingARoundNeedsIsUnderTheKillPlane(string scenePath)
	{
		// The first node in the group is the plane (CombatManager.KillPlaneOfMap): a second
		// would be ignored. A spawn under it is a player who dies as they appear.
		MapScene scene = MapScene.Load(scenePath);
		List<SceneNode> planes = scene.InGroup(KillPlane.Group).ToList();
		Assert.InRange(planes.Count, 0, 1);
		if (planes.Count == 0)
		{
			return;
		}

		float plane = scene.GlobalOrigin(planes[0].Path).Y;
		foreach (SceneNode node in RoundPieces(scene))
		{
			float y = scene.GlobalOrigin(node.Path).Y;
			Assert.True(!KillPlane.IsBelow(y, plane), $"{scenePath}: {node.Path} is at y {y}, under the kill plane at {plane}");
		}
	}

	[Fact]
	public void GullysKillPlaneIsAtItsRiversSurface()
	{
		// WaterSurfaceGenerator draws the water at its path's height.
		MapScene scene = MapScene.Load("res://Scenes/Maps/Gully.tscn");
		SceneNode water = Assert.Single(scene.WithScript(WaterSurfaceScript));
		SceneNode plane = Assert.Single(scene.InGroup(KillPlane.Group));
		Assert.Equal(scene.GlobalOrigin(ParentPath(water.Path)).Y, scene.GlobalOrigin(plane.Path).Y, 3);
		Assert.Equal(0.5f, scene.GlobalOrigin(plane.Path).Y, 3);
	}

	/// <summary>Spawns, the anchor, barracks and their doors, nodes and lockers.</summary>
	private static IEnumerable<SceneNode> RoundPieces(MapScene scene) =>
		scene.InGroup("player_spawn")
			.Concat(scene.InGroup("strategist_anchor"))
			.Concat(scene.WithScript(BarracksScript))
			.Concat(scene.WithScript(BarracksScript).Select(b => scene.Get(b.Path + "/SpawnPoint")).Where(n => n != null))
			.Concat(scene.WithScript(ResourceNodeScript))
			.Concat(scene.WithScript(WeaponLockerScript));

	private static string ParentPath(string path)
	{
		int slash = path.LastIndexOf('/');
		return slash < 0 ? "." : path[..slash];
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

		/// <summary>The res:// path of an imported model (a .glb) this node instances, or null.</summary>
		public string Instance;

		public SceneNode Clone(string path) => new()
		{
			Path = path,
			Type = Type,
			Script = Script,
			Groups = new HashSet<string>(Groups, StringComparer.Ordinal),
			Transform = Transform,
			Instance = Instance,
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

		public IEnumerable<SceneNode> Nodes => _nodes.Values;

		/// <summary>Where a node is in the map: its transform composed with every ancestor's.</summary>
		public Vec3 GlobalOrigin(string path)
		{
			// Applied from the node outwards: p' = B·p + o at each level.
			float x = 0f, y = 0f, z = 0f;
			foreach (SceneNode node in Chain(path))
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

		/// <summary>
		/// How much a node's own axes are stretched in the map: the lengths of its global
		/// basis's columns, its basis composed with every ancestor's.
		/// </summary>
		public Vec3 GlobalScale(string path)
		{
			// Row-major, as a .tscn writes it: G = A_root · … · A_parent · A_node, built from the
			// node outwards as G ← A · G.
			float[] g = { 1f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f };
			foreach (SceneNode node in Chain(path))
			{
				float[] a = node.Transform;
				if (a == null)
				{
					continue;
				}

				var product = new float[9];
				for (int row = 0; row < 3; row++)
				{
					for (int column = 0; column < 3; column++)
					{
						product[(row * 3) + column] = (a[row * 3] * g[column]) + (a[(row * 3) + 1] * g[3 + column])
							+ (a[(row * 3) + 2] * g[6 + column]);
					}
				}

				g = product;
			}

			static float Column(float[] m, int c) => MathF.Sqrt((m[c] * m[c]) + (m[3 + c] * m[3 + c]) + (m[6 + c] * m[6 + c]));
			return new Vec3(Column(g, 0), Column(g, 1), Column(g, 2));
		}

		/// <summary>The node, then each ancestor up to the root.</summary>
		private List<SceneNode> Chain(string path)
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

			return chain;
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

			// An instanced (or, at the root, inherited) scene brings its whole tree with it. An
			// imported model (a .glb) is a binary the engine turns into a scene at import: it is
			// read as the one node it is instanced as, which is all a map's groups and scripts see.
			if (attributes.TryGetValue("instance", out string instance))
			{
				string instanced = resources[Quoted.Match(instance).Groups[1].Value];
				if (instanced.EndsWith(".tscn", StringComparison.Ordinal))
				{
					foreach ((string subPath, SceneNode sub) in Flatten(ToFile(instanced), depth + 1))
					{
						string at = subPath == "." ? path : path == "." ? subPath : path + "/" + subPath;
						nodes[at] = sub.Clone(at);
					}
				}
				else
				{
					Assert.True(File.Exists(ToFile(instanced)), $"{instanced} is not in the checkout");
				}
			}

			// A node already there is one this scene overrides: its properties win, groups add.
			if (!nodes.TryGetValue(path, out SceneNode node))
			{
				node = new SceneNode { Path = path };
				nodes[path] = node;
			}

			if (attributes.TryGetValue("instance", out string model)
				&& resources[Quoted.Match(model).Groups[1].Value] is { } modelPath
				&& !modelPath.EndsWith(".tscn", StringComparison.Ordinal))
			{
				node.Instance = modelPath;
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
