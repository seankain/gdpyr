using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using Gdpyr.Core;
using Gdpyr.Level;
using Gdpyr.Match;
using Gdpyr.Net;
using Gdpyr.Rts;
using Gdpyr.Sim;
using Gdpyr.Sim.Level;
using Godot;

namespace Gdpyr.EngineChecks;

/// <summary>
/// LE3, the world from a file (docs/LEVEL_EDITOR.md §11): a data map played through the
/// game's own path — <c>--map</c>, <c>MapLibrary</c>, <c>MapHost.tscn</c>, <c>LevelRoot</c> —
/// and held to its file. Run by <c>scripts/level-editor-checks.sh</c> on a copy of the tree
/// with this file added as an autoload; the game never compiles or loads it.
///
/// <c>LE3_CHECK</c> names this process's part:
/// <list type="bullet">
/// <item><c>play</c>: the process playing the map — <c>--practice</c>, a <c>--server</c> or
/// the stripped export's. The map is the file's, by hash; the bake has vertices; what is in
/// the world is what the file holds, counted by group and by the game's own managers; a
/// ray straight down meets the ground where the file says and none at a slant passes
/// through it; the round goes live; and over <c>LE3_SECONDS</c> of ticks (600 unless
/// given) units are trained and a node is taken, and a tick's time is measured. On a scene
/// map — the slab, for comparison — only the round and the tick.</item>
/// <item><c>client</c>: a client of that server: it was told the map, built the same world
/// from its own copy, and its ground is where the file says.</item>
/// <item><c>shot</c>: a drawn process, under a virtual display: the map from a few places,
/// each saved as a picture in <c>LE3_DIR</c>, with each of the shader's overlays. For a person
/// to look at; nothing here is a fact about how it looks.</item>
/// </list>
/// <c>LE3_DIR</c> is where the two leave each other marks.
///
/// <c>LE3_BASIN=x,z,radius,depth</c> digs a basin in the session's document before the world is
/// built from it: a disc of ground <c>depth</c> metres under the map's water, skirted at 30°.
/// The bundled map has no ground under its water; this gives the bake's box (LE-D8, §7.3)
/// something to leave out, and <c>play</c> holds it to that.
/// </summary>
public partial class LevelMapChecks : Node
{
	private const int Second = SimConfig.TickRate;

	/// <summary>How far a ray's hit may be from §6.1's Sample [m].</summary>
	private const float RayTolerance = 0.001f;

	private string _role;
	private string _dir;
	private IEnumerator<bool> _run;
	private int _failures;
	private int _facts;
	private bool _timedOut;

	private long _lastFrame;
	private bool _recordFrames;
	private readonly List<float> _frameMs = new();
	private double _longestBeforeBake;
	private bool _unbaked = true;

	public override void _Ready()
	{
		_role = System.Environment.GetEnvironmentVariable("LE3_CHECK");
		_dir = System.Environment.GetEnvironmentVariable("LE3_DIR");
		if (string.IsNullOrEmpty(_role))
		{
			SetPhysicsProcess(false);
			return;
		}

		ProcessMode = ProcessModeEnum.Always;
		_run = _role switch
		{
			"play" => Play(),
			"client" => Client(),
			"shot" => Shot(),
			_ => null,
		};

		// Before the world is built: this autoload is ready before the menu changes scene.
		DigBasin();

		if (_run == null)
		{
			GD.PrintErr($"[checks] no such part: {_role}");
			SetPhysicsProcess(false);
			GetTree().Quit(99);
			return;
		}

		GD.Print($"[checks] level map check: {_role}");
	}

	public override void _PhysicsProcess(double delta)
	{
		long now = Stopwatch.GetTimestamp();
		if (_recordFrames && _lastFrame != 0)
		{
			_frameMs.Add((now - _lastFrame) * 1000f / Stopwatch.Frequency);
		}

		// The first tick's bake is synchronous, and runs before this autoload's turn in the frame
		// it is in: a frame that began with no mesh is one the loading or the bake was in.
		if (_lastFrame != 0 && _unbaked)
		{
			_longestBeforeBake = Math.Max(_longestBeforeBake, (now - _lastFrame) * 1000.0 / Stopwatch.Frequency);
		}

		_unbaked = !(UnitManager.Instance?.NavigationReady ?? false);

		_lastFrame = now;

		bool more;
		try
		{
			more = _run.MoveNext();
		}
		catch (Exception e)
		{
			GD.PrintErr($"[checks] EXCEPTION {e}");
			_failures++;
			more = false;
		}

		if (!more)
		{
			GD.Print($"[checks] {_role}: {_facts - _failures} of {_facts} facts hold");
			Mark($"finished-{_role}");
			SetPhysicsProcess(false);
			GetTree().Quit(_failures);
		}
	}

	// ---- plumbing ----------------------------------------------------------------------

	private string MapName => Session.Map.IsData ? $"{Session.Map.Id} (data)" : $"{Session.Map.Id} (scene)";

	private void Fact(bool ok, string what)
	{
		_facts++;
		if (!ok)
		{
			_failures++;
		}

		GD.Print($"[checks] {(ok ? "PASS" : "FAIL")} {_role}: {MapName}: {what}");
	}

	private void Measured(string what) => GD.Print($"[checks] measured {_role}: {MapName}: {what}");

	private static string F(double v, string format = "0.00") => v.ToString(format, CultureInfo.InvariantCulture);

	private static double Ms(long from) => (Stopwatch.GetTimestamp() - from) * 1000.0 / Stopwatch.Frequency;

	private void Mark(string name)
	{
		if (!string.IsNullOrEmpty(_dir))
		{
			File.WriteAllText(Path.Combine(_dir, name), string.Empty);
		}
	}

	private bool Has(string name) => !string.IsNullOrEmpty(_dir) && File.Exists(Path.Combine(_dir, name));

	private IEnumerable<bool> Until(Func<bool> condition, int maxTicks)
	{
		_timedOut = false;
		int n = 0;
		while (!condition())
		{
			if (n++ >= maxTicks)
			{
				_timedOut = true;
				yield break;
			}

			yield return true;
		}
	}

	private static string Stats(List<float> samples)
	{
		if (samples.Count == 0)
		{
			return "no samples";
		}

		var sorted = new List<float>(samples);
		sorted.Sort();
		double sum = 0;
		foreach (float s in sorted)
		{
			sum += s;
		}

		float At(double p) => sorted[Math.Min((int)(p * sorted.Count), sorted.Count - 1)];
		return $"n {sorted.Count}, mean {F(sum / sorted.Count, "0.000")} ms, p50 {F(At(0.50), "0.000")}, p95 {F(At(0.95), "0.000")},"
			+ $" p99 {F(At(0.99), "0.000")}, max {F(sorted[^1], "0.000")}";
	}

	private int InGroup(string group) => GetTree().GetNodesInGroup(group).Count;

	// ---- a basin under the water ------------------------------------------------------------

	private Vector4? _basin;

	/// <summary>LE3_BASIN's disc, dug into the session's document; the validator asked again.</summary>
	private void DigBasin()
	{
		string text = System.Environment.GetEnvironmentVariable("LE3_BASIN");
		if (string.IsNullOrEmpty(text) || Session.Document == null)
		{
			return;
		}

		string[] parts = text.Split(',');
		float[] v = Array.ConvertAll(parts, p => float.Parse(p, CultureInfo.InvariantCulture));
		_basin = new Vector4(v[0], v[1], v[2], v[3]);
		MapDocument document = Session.Document;
		float bed = document.Bands.WaterLevelMeters - v[3];
		SampleRect touched = TerrainConform.Disc(document.Terrain, v[0], v[1], v[2], bed, 0f,
			HeightField.Grade(30f));
		document.Terrain.Round(touched);
		MapReport report = MapValidator.Validate(document);
		GD.Print($"[checks] LE3_BASIN: a disc of {F(v[2], "0")} m at ({F(v[0], "0")}, {F(v[1], "0")}), its bed at {F(bed)} m,"
			+ $" {F(v[3], "0.0")} m under the water; {touched.Count} samples touched; the validator: {report.Errors} errors, {report.Warnings} warnings");
	}

	/// <summary>
	/// LE-D8's box: the bake leaves out ground more than 1 m under the water. No vertex of the
	/// mesh is under the bake's floor, and the closest point of it to the basin's bed is on
	/// the bank, not on the bed.
	/// </summary>
	private void TheBedIsLeftOut(NavigationRegion3D region)
	{
		if (_basin is not Vector4 basin || LevelRoot.Current is not { } level)
		{
			return;
		}

		Fact(level.HasWater, $"the ground is under the water at {level.Terrain.SubmergedSamples} samples");
		Aabb box = region.NavigationMesh.FilterBakingAabb;
		Fact(MathF.Abs(box.Position.Y - level.BakeFloorMeters) < 1e-4f,
			$"the bake's box has its floor at {F(box.Position.Y)} m, 1 m under the water at {F(level.WaterLevelMeters)} m");

		Vector3[] vertices = region.NavigationMesh.GetVertices();
		int under = 0;
		foreach (Vector3 vertex in vertices)
		{
			under += vertex.Y < level.BakeFloorMeters - 0.15f ? 1 : 0;
		}

		Fact(under == 0, $"{under} of the mesh's {vertices.Length} vertices are under the floor");

		// Asked of the region's own map once it has the new mesh: a map not yet synchronised
		// answers the origin for everything.
		Rid map = NavigationServer3D.RegionGetMap(region.GetRid());
		var bed = new Vector3(basin.X, level.Document.Terrain.Sample(basin.X, basin.Y), basin.Y);
		Vector3 closest = NavigationServer3D.MapGetClosestPoint(map, bed);
		float flat = new Vector2(closest.X - bed.X, closest.Z - bed.Z).Length();
		float bank = level.Document.Terrain.Sample(closest.X, closest.Z);
		Fact(NavigationServer3D.MapGetIterationId(map) > 0 && closest != Vector3.Zero
				&& closest.Y >= level.BakeFloorMeters - 0.15f && flat > basin.Z * 0.5f && flat < basin.Z * 4f
				&& MathF.Abs(closest.Y - bank) < 0.5f,
			$"the mesh's closest point to the bed at ({F(bed.X, "0")}, {F(bed.Y)}, {F(bed.Z, "0")}) is on the bank, {F(flat, "0.0")} m off,"
			+ $" at ({F(closest.X, "0.0")}, {F(closest.Y)}, {F(closest.Z, "0.0")}), the ground there at {F(bank)} m");
	}

	// ---- the map and the world -------------------------------------------------------------

	/// <summary>The map is the one asked for, from its file, and the world is that file's.</summary>
	private void TheMapIsTheFiles(bool server)
	{
		string wanted = System.Environment.GetEnvironmentVariable("LE3_MAP") ?? "hills";
		Fact(Session.Map.Id == wanted, $"this process plays '{Session.Map.Id}', which it was asked to ('{wanted}')");
		if (!Session.Map.IsData)
		{
			return;
		}

		LevelRoot level = LevelRoot.Current;
		Fact(level?.Document != null && ReferenceEquals(level.Document, Session.Document),
			"MapHost.tscn's LevelRoot built the world from the session's document");
		if (level?.Document == null)
		{
			return;
		}

		// The file again, read here and not through the game: its hash, and what it holds.
		bool read = MapLibrary.TryReadBytes(Session.Map.Path, out byte[] bytes, out string error);
		Fact(read, $"{Session.Map.Path} is in this build: {bytes?.Length ?? 0} bytes{(read ? string.Empty : ", " + error)}");
		if (!read)
		{
			return;
		}

		Fact(MapFile.HashHex(bytes) == Session.Map.Hash,
			$"its SHA-256 is the catalog's, {Session.Map.Hash[..16]}…");
		if (_basin == null)
		{
			Fact(MapFile.TryRead(bytes, out MapDocument file, out _) && file.SameAs(level.Document, out _),
				"the document the world was built from is the file read again, field by field and sample by sample");
		}

		MapDocument d = level.Document;
		int spawnLines = d.CountOf(PrefabGroup.SpawnLine);
		(string What, int World, int File)[] counts =
		{
			("player_spawn markers", InGroup("player_spawn"), 4 * spawnLines),
			("barracks", InGroup(Barracks.Group), d.CountOf(PrefabGroup.Barracks)),
			("resource nodes", InGroup(ResourceNode.Group), d.CountOf(PrefabGroup.ResourceNode)),
			("weapon lockers", InGroup(Gdpyr.Fps.WeaponLocker.Group), d.CountOf(PrefabGroup.WeaponLocker)),
			("vehicle kiosks", InGroup(Gdpyr.Fps.VehicleKiosk.Group), d.CountOf(PrefabGroup.VehicleKiosk)),
			("emplacements", InGroup(Gdpyr.Fps.Emplacement.Group), d.CountOf(PrefabGroup.Emplacement)),
			("ammo cans", InGroup(Gdpyr.Fps.AmmoCan.Group), d.CountOf(PrefabGroup.AmmoCan)),
			("cover boxes", InGroup("cover"), d.CountOf(PrefabGroup.Cover)),
			("strategist anchors", InGroup("strategist_anchor"), d.CountOf(PrefabGroup.StrategistAnchor)),
		};
		foreach ((string what, int world, int inFile) in counts)
		{
			Fact(world == inFile, $"{what}: {world} in the world, {inFile} in the file");
		}

		if (server)
		{
			Fact(UnitManager.Instance.BarracksCount == d.CountOf(PrefabGroup.Barracks)
				&& CombatManager.Instance.Economy.NodeCount == d.CountOf(PrefabGroup.ResourceNode),
				$"the managers found them: {UnitManager.Instance.BarracksCount} barracks, {CombatManager.Instance.Economy.NodeCount} nodes");
		}

		// Every object stands on the ground the file holds under it.
		float worst = 0f;
		foreach (MapObject placed in d.Objects)
		{
			if (PrefabSpecs.TryGet(placed.PrefabId, out PrefabSpec spec) && spec.MakesPad)
			{
				Transform3D at = LevelBuilder.PoseOf(placed, spec);
				worst = MathF.Max(worst, MathF.Abs(at.Origin.Y - d.Terrain.Sample(at.Origin.X, at.Origin.Z)));
			}
		}

		Fact(worst == 0f, $"every padded object stands on its pad to the bit: the worst is {F(worst * 1000f, "0.000")} mm off");
	}

	/// <summary>
	/// The ground where the file says: rays straight down meet the terrain at §6.1's
	/// Sample; rays at a slant from over it to under it meet something, every one. A hit on
	/// an object is stepped past, so the ground itself is what is measured.
	/// </summary>
	private void Rays(int down, int slanted)
	{
		if (LevelRoot.Current?.Terrain is not TerrainNode terrain)
		{
			return;
		}

		PhysicsDirectSpaceState3D space = GetViewport().World3D.DirectSpaceState;
		HeightField field = LevelRoot.Current.Document.Terrain;
		var random = new Random(3);
		int met = 0;
		float worst = 0f;
		for (int i = 0; i < down; i++)
		{
			float x = (float)((random.NextDouble() * 236d) - 118d);
			float z = (float)((random.NextDouble() * 236d) - 118d);
			if (TerrainHit(space, terrain, new Vector3(x, 80f, z), new Vector3(x, -40f, z), out Vector3 hit))
			{
				met++;
				worst = MathF.Max(worst, MathF.Abs(hit.Y - field.Sample(x, z)));
			}
		}

		Fact(met == down && worst <= RayTolerance,
			$"{met} of {down} rays straight down met the ground, the worst {F(worst * 1000f, "0.000")} mm from the file's");

		int through = 0;
		int offSample = 0;
		for (int i = 0; i < slanted; i++)
		{
			float x = (float)((random.NextDouble() * 220d) - 110d);
			float z = (float)((random.NextDouble() * 220d) - 110d);
			float angle = (float)(random.NextDouble() * Math.PI * 2d);
			float length = 16f + (float)(random.NextDouble() * 184d);
			float ex = Math.Clamp(x + (MathF.Cos(angle) * length), -118f, 118f);
			float ez = Math.Clamp(z + (MathF.Sin(angle) * length), -118f, 118f);
			var from = new Vector3(x, field.Sample(x, z) + 1f + (float)(random.NextDouble() * 29d), z);
			var to = new Vector3(ex, field.Sample(ex, ez) - 0.5f - (float)(random.NextDouble() * 4.5d), ez);
			if (!TerrainHit(space, terrain, from, to, out Vector3 hit))
			{
				through++;
				continue;
			}

			offSample += MathF.Abs(hit.Y - field.Sample(hit.X, hit.Z)) > RayTolerance ? 1 : 0;
		}

		Fact(through == 0 && offSample == 0,
			$"{slanted} rays at a slant, 16-200 m in plan, from over the ground to under it: {through} passed through, {offSample} met it off the file's");
	}

	/// <summary>The first hit on the terrain along a segment, stepping past whatever else is in the way.</summary>
	private static bool TerrainHit(PhysicsDirectSpaceState3D space, TerrainNode terrain, Vector3 from, Vector3 to,
		out Vector3 hit)
	{
		hit = default;
		Vector3 direction = (to - from).Normalized();
		var exclude = new Godot.Collections.Array<Rid>();
		for (int step = 0; step < 32; step++)
		{
			var query = PhysicsRayQueryParameters3D.Create(from, to, CollisionLayers.World, exclude);
			Godot.Collections.Dictionary result = space.IntersectRay(query);
			if (result.Count == 0)
			{
				return false;
			}

			if (result["collider"].AsGodotObject() == terrain)
			{
				hit = result["position"].AsVector3();
				return true;
			}

			exclude.Add(result["rid"].AsRid());
			from = result["position"].AsVector3() + (direction * 0.001f);
		}

		return false;
	}

	// ---- play ------------------------------------------------------------------------------

	private IEnumerator<bool> Play()
	{
		foreach (bool _ in Until(() => Session.InGame && UnitManager.Instance != null && CombatManager.Instance != null,
			60 * Second))
		{
			yield return true;
		}

		foreach (bool _ in Until(() => UnitManager.Instance.NavigationReady, 60 * Second)) yield return true;
		NavigationRegion3D region = null;
		foreach (Node node in GetTree().GetNodesInGroup("navigation_region"))
		{
			region ??= node as NavigationRegion3D;
		}

		int vertices = region?.NavigationMesh?.GetVertices().Length ?? 0;
		Fact(UnitManager.Instance.NavigationReady && vertices > 0, $"navigation baked: {vertices} vertices");
		Measured($"the longest frame up to the first tick's bake: {F(_longestBeforeBake, "0.0")} ms");
		if (Session.Map.IsData)
		{
			Fact(region?.NavigationMesh?.AgentMaxSlope == MapLimits.WalkableSlopeDegrees,
				$"baked at {F(region?.NavigationMesh?.AgentMaxSlope ?? 0f, "0")}°, LE-D23's walkable");
		}

		TheMapIsTheFiles(server: true);
		Rays(1_000, 2_000);

		// The navigation map takes the baked mesh on a later frame.
		Rid navigationMap = region != null ? NavigationServer3D.RegionGetMap(region.GetRid()) : default;
		foreach (bool _ in Until(() => region == null || NavigationServer3D.MapGetIterationId(navigationMap) > 0, 5 * Second))
		{
			yield return true;
		}

		for (int i = 0; i < 5; i++)
		{
			yield return true;
		}

		TheBedIsLeftOut(region);

		CombatManager combat = CombatManager.Instance;
		UnitManager units = UnitManager.Instance;
		long waiting = Stopwatch.GetTimestamp();
		while (!combat.Match.IsLive && Ms(waiting) < 240_000)
		{
			yield return true;
		}

		Fact(combat.Match.IsLive, $"the round is live ({combat.Teams.ReadyCount} on their sides)");
		if (!combat.Match.IsLive)
		{
			yield break;
		}

		int seconds = int.TryParse(System.Environment.GetEnvironmentVariable("LE3_SECONDS"), out int asked) ? asked : 600;
		Func<int>[] read =
		{
			() => units.UnitsProduced, () => units.UnitsLost, () => units.StructuresBuilt, () => units.StructuresLost,
			() => units.NavigationRebakes, () => units.RejectedOrders, () => combat.Match.GroundDeaths,
		};
		var last = new int[read.Length];
		var total = new int[read.Length];
		for (int c = 0; c < read.Length; c++)
		{
			last[c] = read[c]();
		}

		_frameMs.Clear();
		_recordFrames = true;
		int most = 0;
		long fielded = 0;
		int rounds = 1;
		int captures = 0;
		bool live = true;
		var holders = new NodeHolder[combat.Economy.NodeCount];
		long started = Stopwatch.GetTimestamp();
		for (int i = 0; i < seconds * Second; i++)
		{
			yield return true;
			most = Math.Max(most, units.SlotCount);
			fielded += units.SlotCount;
			if (combat.Match.IsLive != live)
			{
				live = combat.Match.IsLive;
				rounds += live ? 1 : 0;
			}

			for (int c = 0; c < read.Length; c++)
			{
				int now = read[c]();
				total[c] += Math.Max(now - last[c], 0);
				last[c] = now;
			}

			for (int n = 0; n < holders.Length; n++)
			{
				NodeHolder owner = combat.Economy.NodeAt(n).Capture.Owner;
				captures += owner != holders[n] && owner != NodeHolder.Neutral ? 1 : 0;
				holders[n] = owner;
			}
		}

		_recordFrames = false;
		Measured($"a tick, frame to frame, over {seconds} s of ticks ({F(Ms(started) / 1000.0, "0")} s of wall time): {Stats(_frameMs)}");
		Measured($"the play: {rounds} round(s), phase {combat.Match.Phase} at the end; units produced {total[0]}, lost {total[1]},"
			+ $" on the field {F(fielded / (double)(seconds * Second), "0.0")} on average and {most} at the most;"
			+ $" structures built {total[2]}, lost {total[3]}; re-bakes {total[4]}; rejected orders {total[5]};"
			+ $" ground deaths {total[6]}; nodes taken {captures} times");
		Fact(total[0] > 0, $"the strategist trained units: {total[0]}");
		Fact(captures > 0, $"a node was taken: {captures} times");
		Fact(total[6] > 0, $"there was a fight: {total[6]} ground deaths");

		// The client, if there is one, is told it may stop.
		Mark("server-done");
	}

	// ---- shot ------------------------------------------------------------------------------

	/// <summary>Where the pictures are taken from: a name, the eye, and what it looks at.</summary>
	private static readonly (string Name, Vector3 Eye, Vector3 At)[] Views =
	{
		("overview", new Vector3(-150f, 95f, 150f), new Vector3(0f, 0f, 0f)),
		("spawn", new Vector3(-60f, 18f, 120f), new Vector3(-80f, 2f, 85f)),
		("middle", new Vector3(30f, 25f, 40f), new Vector3(0f, 4f, 0f)),
		("barracks", new Vector3(60f, 20f, -40f), new Vector3(90f, 4f, -90f)),
		("seams", new Vector3(-8f, 6f, -8f), new Vector3(8f, 0f, 8f)),
	};

	private IEnumerator<bool> Shot()
	{
		foreach (bool _ in Until(() => Session.InGame && LevelRoot.Current?.Terrain != null, 60 * Second))
		{
			yield return true;
		}

		Fact(!_timedOut, "in the game, the world built");
		if (_timedOut)
		{
			yield break;
		}

		// Nothing in front of the picture but the world.
		foreach (Node node in GetTree().Root.FindChildren("*", nameof(CanvasLayer), true, false))
		{
			((CanvasLayer)node).Visible = false;
		}

		var camera = new Camera3D { Name = "ShotCamera", Far = 1000f, Fov = 60f };
		LevelRoot.Current.AddChild(camera);
		var materials = new List<ShaderMaterial>();
		foreach (Node chunk in LevelRoot.Current.Terrain.GetChildren())
		{
			if (chunk is MeshInstance3D { Mesh: ArrayMesh mesh } && mesh.SurfaceGetMaterial(0) is ShaderMaterial material
				&& !materials.Contains(material))
			{
				materials.Add(material);
			}
		}

		Fact(LevelRoot.Current.Terrain.MeshChunks == 225 && materials.Count == 1,
			$"the ground is drawn: {LevelRoot.Current.Terrain.MeshChunks} chunks with {materials.Count} terrain material");

		string[] overlays = { "plain", "bands", "slope", "contours" };
		var views = new List<(string Name, Vector3 Eye, Vector3 At)>(Views);
		if (_basin is Vector4 basin)
		{
			views.Add(("basin", new Vector3(basin.X + basin.Z + 20f, 18f, basin.Y + basin.Z + 20f), new Vector3(basin.X, -5f, basin.Y)));
		}

		foreach ((string name, Vector3 eye, Vector3 at) in views)
		{
			for (int overlay = 0; overlay < (name is "overview" or "basin" ? overlays.Length : 1); overlay++)
			{
				foreach (ShaderMaterial material in materials)
				{
					material.SetShaderParameter("overlay", overlay);
				}

				camera.GlobalTransform = new Transform3D(Basis.Identity, eye).LookingAt(at, Vector3.Up);
				camera.MakeCurrent();
				for (int i = 0; i < 20; i++)
				{
					yield return true;
				}

				Image image = GetViewport().GetTexture().GetImage();
				string file = Path.Combine(_dir ?? ".", $"le3-{name}{(overlay > 0 ? "-" + overlays[overlay] : string.Empty)}.png");
				Error saved = image.SavePng(file);
				Fact(saved == Error.Ok && image.GetWidth() > 0, $"{Path.GetFileName(file)}: {image.GetWidth()} x {image.GetHeight()}");
			}
		}
	}

	// ---- client ----------------------------------------------------------------------------

	private IEnumerator<bool> Client()
	{
		foreach (bool _ in Until(() => Session.InGame && NetworkManager.Instance is { Connected: true }, 120 * Second))
		{
			yield return true;
		}

		Fact(!_timedOut, "connected and in the game");
		if (_timedOut)
		{
			yield break;
		}

		Fact(NetworkManager.Instance.ServerMapId == Session.Map.Id,
			$"the server named '{NetworkManager.Instance.ServerMapId}', and this client plays it");
		TheMapIsTheFiles(server: false);
		Rays(1_000, 1_000);

		// Watch the round from here until the server is done with it.
		foreach (bool _ in Until(() => Has("server-done") || NetworkManager.Instance is not { Connected: true },
			3_600 * Second))
		{
			yield return true;
		}

		Fact(Has("server-done"), "connected until the server had finished");
	}
}
