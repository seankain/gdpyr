using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using Gdpyr.Core;
using Gdpyr.Fps;
using Gdpyr.Match;
using Gdpyr.Net;
using Gdpyr.Rts;
using Gdpyr.Sim;
using Godot;

namespace Gdpyr.EngineChecks;

/// <summary>
/// LE0, the level editor's baseline and spike (docs/LEVEL_EDITOR.md §11): what the plan
/// could not know without the engine, measured. Run headless by
/// <c>scripts/level-editor-checks.sh</c> on a copy of the tree with this file added as an
/// autoload and, for every part but the slab's, the greybox's floor replaced by
/// <see cref="LevelTerrain"/>; not part of the game.
///
/// <c>LE0_CHECK</c> names this process's part:
/// <list type="bullet">
/// <item><c>ground</c>: a listen host. Rays against §6.1's <c>Sample</c>; what handing the
/// collider its data costs; the navigation bake, whole and in its two halves, a tile of
/// it, and the game's own re-bake around a structure.</item>
/// <item><c>slopes</c>: a listen host whose ground is changed under it. A body and a unit
/// on 44°, 45° and 46°; the 4x4 from rest, at speed and over a ridge; the helicopter over
/// hills; structure sites on slopes, through the computer strategist's entry point and
/// through a scenario's.</item>
/// <item><c>frame</c>: a dedicated server with its bots: the time a tick takes.</item>
/// <item><c>net-server</c>, <c>net-client</c>: a dedicated server and one client behind
/// the delay relay, walking: mispredictions a second and mean prediction error.</item>
/// <item><c>export</c>: the stripped server export: the collider from bytes, and the bake.</item>
/// </list>
///
/// One line per fact, <c>PASS</c> or <c>FAIL</c>, and <c>[checks] measured …</c> lines; the
/// process exits with the number of its facts that failed.
/// </summary>
public partial class LevelChecks : Node
{
	private const int Second = SimConfig.TickRate;

	/// <summary>The middle of the open ground a ramp is put in (LevelTerrain): the lane everything is driven and walked on.</summary>
	private const float LaneZ = 81f;

	/// <summary>Open floor the greybox leaves free, which <see cref="LevelTerrain"/> keeps level on every ground: where a check's own structure goes.</summary>
	private static readonly Vector3 OpenSite = new(40f, LevelTerrain.Base, 30f);

	private string _role;
	private string _dir;
	private IEnumerator<bool> _run;
	private int _failures;
	private int _facts;
	private bool _timedOut;

	private long _lastFrame;
	private bool _recordFrames;
	private readonly List<float> _frameMs = new();
	private readonly Godot.Collections.Array<Rid> _exclude = new();

	public override void _Ready()
	{
		_role = System.Environment.GetEnvironmentVariable("LE0_CHECK");
		_dir = System.Environment.GetEnvironmentVariable("LE0_DIR");
		if (string.IsNullOrEmpty(_role))
		{
			SetPhysicsProcess(false);
			return;
		}

		ProcessMode = ProcessModeEnum.Always;
		_run = _role switch
		{
			"ground" => Ground(),
			"slopes" => Slopes(),
			"frame" => Frame(),
			"net-server" => NetServer(),
			"net-client" => NetClient(),
			"export" => Export(),
			_ => null,
		};

		if (_run == null)
		{
			GD.PrintErr($"[checks] no such part: {_role}");
			SetPhysicsProcess(false);
			GetTree().Quit(99);
			return;
		}

		GD.Print($"[checks] level check: {_role}, ground {GroundName}");
	}

	public override void _PhysicsProcess(double delta)
	{
		long now = Stopwatch.GetTimestamp();
		if (_recordFrames && _lastFrame != 0)
		{
			_frameMs.Add((now - _lastFrame) * 1000f / Stopwatch.Frequency);
		}

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
			if (!string.IsNullOrEmpty(_dir))
			{
				Mark($"finished-{_role}");
			}

			SetPhysicsProcess(false);
			GetTree().Quit(_failures);
		}
	}

	// ---- plumbing ----------------------------------------------------------------------

	private static LevelTerrain Terrain => LevelTerrain.Instance;

	private static string GroundName => System.Environment.GetEnvironmentVariable("LE0_TERRAIN") is { Length: > 0 } kind
		? kind + (System.Environment.GetEnvironmentVariable("LE0_COLLIDER") == "trimesh" ? ", trimesh" : ", heightmap")
		: "slab";

	private void Fact(bool ok, string what)
	{
		_facts++;
		if (!ok)
		{
			_failures++;
		}

		GD.Print($"[checks] {(ok ? "PASS" : "FAIL")} {_role}: {what}");
	}

	private void Measured(string what) => GD.Print($"[checks] measured {_role}: {what}");

	private static string F(float v, string format = "0.00") => v.ToString(format, CultureInfo.InvariantCulture);

	private static string F(double v, string format = "0.00") => v.ToString(format, CultureInfo.InvariantCulture);

	private static double Ms(long from) => (Stopwatch.GetTimestamp() - from) * 1000.0 / Stopwatch.Frequency;

	private void Mark(string name, string content = "")
	{
		File.WriteAllText(Path.Combine(_dir, name), content);
		GD.Print($"[checks] {_role} marks {name}");
	}

	private bool Has(string name) => File.Exists(Path.Combine(_dir, name));

	private static IEnumerable<bool> Wait(int ticks)
	{
		for (int i = 0; i < ticks; i++)
		{
			yield return true;
		}
	}

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

	private static float YawFacing(Vector3 direction) => Mathf.Atan2(-direction.X, -direction.Z);

	private static LocalInputSampler Sampler => PlayerManager.Instance.GetNode<LocalInputSampler>("LocalInputSampler");

	private static uint Tick => NetworkManager.Instance.Tick;

	private PhysicsDirectSpaceState3D Space => GetTree().Root.World3D.DirectSpaceState;

	private static T Private<T>(object target, string field) =>
		(T)target.GetType().GetField(field, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(target);

	private static void Call(object target, string method) =>
		target.GetType().GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance).Invoke(target, null);

	private NavigationRegion3D Region()
	{
		foreach (Node node in GetTree().GetNodesInGroup("navigation_region"))
		{
			if (node is NavigationRegion3D region)
			{
				return region;
			}
		}

		return null;
	}

	/// <summary>
	/// A ray against the terrain alone: whatever else of the world it meets first is
	/// excluded and the ray cast again. False when it meets no terrain.
	/// </summary>
	private bool TerrainRay(Vector3 from, Vector3 to, out Vector3 hit)
	{
		_exclude.Clear();
		PhysicsDirectSpaceState3D space = Space;
		for (int i = 0; i < 24; i++)
		{
			var query = PhysicsRayQueryParameters3D.Create(from, to, CollisionLayers.World);
			query.Exclude = _exclude;
			Godot.Collections.Dictionary result = space.IntersectRay(query);
			if (result.Count == 0)
			{
				break;
			}

			if (result["collider"].AsGodotObject() == Terrain)
			{
				hit = result["position"].AsVector3();
				return true;
			}

			_exclude.Add(result["rid"].AsRid());
		}

		hit = default;
		return false;
	}

	/// <summary>The ground under (x, z), whatever it is made of: a ray down from 80 m, as the agent's planes cast one.</summary>
	private float GroundAt(float x, float z)
	{
		var query = PhysicsRayQueryParameters3D.Create(new Vector3(x, 80f, z), new Vector3(x, -40f, z), CollisionLayers.World);
		Godot.Collections.Dictionary result = Space.IntersectRay(query);
		return result.Count == 0 ? float.NaN : result["position"].AsVector3().Y;
	}

	private static string Stats(List<float> values, string unit = "ms")
	{
		if (values.Count == 0)
		{
			return "no samples";
		}

		var sorted = new List<float>(values);
		sorted.Sort();
		double sum = 0;
		foreach (float v in sorted)
		{
			sum += v;
		}

		float At(double p) => sorted[Math.Min((int)(p * sorted.Count), sorted.Count - 1)];
		return $"n {sorted.Count}, mean {F(sum / sorted.Count, "0.000")} {unit}, p50 {F(At(0.50), "0.000")}, p95 {F(At(0.95), "0.000")},"
			+ $" p99 {F(At(0.99), "0.000")}, max {F(sorted[^1], "0.000")}";
	}

	private IEnumerable<bool> InGame(bool wantLocal)
	{
		foreach (bool _ in Until(() => Session.InGame && UnitManager.Instance != null && CombatManager.Instance != null
			&& (!wantLocal || CombatManager.Instance.Local?.Character != null), 60 * Second))
		{
			yield return true;
		}

		foreach (bool _ in Until(() => UnitManager.Instance.NavigationReady, 30 * Second)) yield return true;
	}

	private IEnumerable<bool> LocalOnGround()
	{
		PlayerCombat local = CombatManager.Instance.Local;
		if (local.AwaitingRole || local.Team != Team.GroundForce)
		{
			CombatManager.Instance.RequestTeam(Team.GroundForce);
		}

		foreach (bool _ in Until(() => local.IsAlive && local.Team == Team.GroundForce, 20 * Second)) yield return true;
		Fact(!_timedOut, "the listen host's player is alive on the ground force");
	}

	// ---- ground: rays, the collider's data, the bake ---------------------------------------

	private IEnumerator<bool> Ground()
	{
		_recordFrames = true;
		foreach (bool _ in InGame(wantLocal: false)) yield return true;
		Fact(!_timedOut && UnitManager.Instance.NavigationReady, "in the game, the first tick's bake done");
		if (_timedOut)
		{
			yield break;
		}

		float longest = 0f;
		foreach (float ms in _frameMs)
		{
			longest = MathF.Max(longest, ms);
		}

		_recordFrames = false;
		Measured($"{GroundName}: the longest frame up to the first tick's bake: {F(longest, "0.0")} ms");

		NavigationRegion3D region = Region();
		NavigationMesh mesh = region.NavigationMesh;
		Measured($"{GroundName}: the first tick's mesh: {mesh.GetVertices().Length} vertices, {mesh.GetPolygonCount()} polygons");
		foreach (bool _ in Wait(30)) yield return true;

		if (Terrain != null)
		{
			foreach (bool _ in Rays()) yield return true;
			foreach (bool _ in SetData()) yield return true;
		}

		foreach (bool _ in Bake(region)) yield return true;
		foreach (bool _ in Rebake(region)) yield return true;
	}

	/// <summary>
	/// Whether a ray met the first ground on it: 0 it did; 1 it met no terrain; 2 what it
	/// met is beyond ground it had already passed under. Marched every 0.1 m against
	/// <see cref="LevelTerrain.Sample"/>, short of what it met.
	/// </summary>
	private static int Judge(float[] heights, Vector3 from, Vector3 to, bool met, Vector3 hit, out float deepest)
	{
		float reach = met ? (hit - from).Length() : (to - from).Length();
		Vector3 direction = (to - from).Normalized();
		deepest = 0f;
		for (float s = 0f; s < reach - 0.05f; s += 0.1f)
		{
			Vector3 p = from + (direction * s);
			deepest = MathF.Max(deepest, LevelTerrain.Sample(heights, p.X, p.Z) - p.Y);
		}

		return !met ? 1 : deepest > 0.002f ? 2 : 0;
	}

	/// <summary>The ray cast as pieces no longer than <paramref name="pieceMeters"/> in plan, in order: the first that meets the terrain.</summary>
	private bool TerrainRayInPieces(Vector3 from, Vector3 to, float pieceMeters, out Vector3 hit)
	{
		float flat = new Vector2(to.X - from.X, to.Z - from.Z).Length();
		int pieces = Math.Max((int)MathF.Ceiling(flat / pieceMeters), 1);
		for (int i = 0; i < pieces; i++)
		{
			if (TerrainRay(from.Lerp(to, i / (float)pieces), from.Lerp(to, (i + 1) / (float)pieces), out hit))
			{
				return true;
			}
		}

		hit = default;
		return false;
	}

	/// <summary>
	/// Rays against <see cref="LevelTerrain.Sample"/> to 1 mm: 1,000 straight down; then
	/// 1,000 at a slant under 16 m long in plan and 1,000 from 16 m to 200 m, 16 m being
	/// where GodotPhysics3D's heightmap changes from walking cells to walking its chunks
	/// of them (godot_shape_3d.cpp, intersect_segment). A slanted ray starts over the
	/// ground and ends under it, so there is ground on it; each is marched, to see that
	/// what it met is the first.
	///
	/// The plan's collider is held to all of it. On the heightmap the long rays are a
	/// measure and not a fact: what they found there is why the plan's collider is not it
	/// (LE-D5).
	/// </summary>
	private IEnumerable<bool> Rays()
	{
		float[] heights = Terrain.Heights;
		string on = GroundName;
		float steepest = LevelTerrain.SteepestFace(heights, out float over45);
		float lowest = float.MaxValue;
		float highest = float.MinValue;
		foreach (float h in heights)
		{
			lowest = MathF.Min(lowest, h);
			highest = MathF.Max(highest, h);
		}

		Measured($"{on}: heights {F(lowest)} to {F(highest)} m, steepest face {F(Mathf.RadToDeg(MathF.Atan(steepest)), "0.0")}°,"
			+ $" {F(over45 * 100f, "0.00")} % of faces over 45°");

		var random = new Random(1);
		int met = 0;
		double sum = 0;
		float worst = 0f;
		int otherDiagonal = 0;
		for (int i = 0; i < 1000; i++)
		{
			float x = ((float)random.NextDouble() * 236f) - 118f;
			float z = ((float)random.NextDouble() * 236f) - 118f;
			if (!TerrainRay(new Vector3(x, 80f, z), new Vector3(x, -40f, z), out Vector3 hit))
			{
				continue;
			}

			met++;
			float error = MathF.Abs(hit.Y - LevelTerrain.Sample(heights, x, z));
			sum += error;
			worst = MathF.Max(worst, error);
			if (MathF.Abs(hit.Y - LevelTerrain.SampleOtherDiagonal(heights, x, z)) > 0.001f)
			{
				otherDiagonal++;
			}
		}

		Fact(met == 1000, $"{on}: 1,000 rays down at random points all meet the terrain ({met})");
		Fact(worst <= 0.001f, $"{on}: each agrees with §6.1's Sample to 1 mm (worst {F(worst * 1000f, "0.0000")} mm,"
			+ $" mean {F(sum / Math.Max(met, 1) * 1000.0, "0.0000")} mm)");
		Measured($"{on}: split along the cell's other diagonal, {otherDiagonal} of {met} would be off by over 1 mm");
		yield return true;

		var failed = new List<(Vector3 From, Vector3 To)>();
		foreach ((string name, int count, float shortest, float longest, bool held) in new[]
		{
			("under 16 m long in plan", 1000, 2f, 15.5f, true),
			("16 m to 200 m long in plan", 10000, 16.5f, 200f, Terrain.IsTrimesh),
		})
		{
			int missed = 0;
			int beyond = 0;
			int offSurface = 0;
			float worstSlant = 0f;
			float deepestBefore = 0f;
			failed.Clear();
			for (int i = 0; i < count; i++)
			{
				Vector3 from;
				Vector3 to;
				do
				{
					float angle = (float)random.NextDouble() * MathF.Tau;
					float length = shortest + ((float)random.NextDouble() * (longest - shortest));
					from = new Vector3(((float)random.NextDouble() * 224f) - 112f, 0f, ((float)random.NextDouble() * 224f) - 112f);
					to = from + new Vector3(MathF.Cos(angle) * length, 0f, MathF.Sin(angle) * length);
				}
				while (MathF.Abs(to.X) > 112f || MathF.Abs(to.Z) > 112f);

				from.Y = LevelTerrain.Sample(heights, from.X, from.Z) + 1f + ((float)random.NextDouble() * 30f);
				to.Y = LevelTerrain.Sample(heights, to.X, to.Z) - 1f - ((float)random.NextDouble() * 15f);

				bool hitTerrain = TerrainRay(from, to, out Vector3 hit);
				int verdict = Judge(heights, from, to, hitTerrain, hit, out float deepest);
				if (verdict != 0)
				{
					missed += verdict == 1 ? 1 : 0;
					beyond += verdict == 2 ? 1 : 0;
					deepestBefore = MathF.Max(deepestBefore, deepest);
					if (failed.Count < 3)
					{
						GD.Print($"[checks] {on}: a ray from {from} to {to} {(verdict == 1 ? "met no terrain" : "met ground beyond ground it passed under")},"
							+ $" {_exclude.Count} other colliders on it, {deepest:0.00} m under the surface on the way");
					}

					failed.Add((from, to));
					continue;
				}

				float error = MathF.Abs(hit.Y - LevelTerrain.Sample(heights, hit.X, hit.Z));
				worstSlant = MathF.Max(worstSlant, error);
				offSurface += error > 0.001f ? 1 : 0;
				if (i == 0)
				{
					// One that met it, as exactly as the ones that did not: for
					// tools/level_editor_checks/heightmap_ray_walk.py.
					GD.Print($"[checks] {on}: a ray from {from} to {to} met the terrain at {hit}");
				}
			}

			string what = $"{on}: of {count:N0} slanted rays {name}, from over the ground to under it, {missed} met no terrain and {beyond} met"
				+ $" ground beyond ground they had passed under (deepest {F(deepestBefore)} m); of the rest {offSurface} are off Sample's"
				+ $" surface by over 1 mm (worst {F(worstSlant * 1000f, "0.0000")} mm)";
			if (held)
			{
				Fact(failed.Count == 0 && offSurface == 0, what);
			}
			else
			{
				Measured(what);
			}

			if (failed.Count > 0)
			{
				// The same rays as pieces of at most 12 m in plan: the cell walk alone. And
				// which way they were heading: the cell walk's start is treated differently
				// for a step up an axis and a step down it.
				int still = 0;
				int downBoth = 0;
				foreach ((Vector3 from, Vector3 to) in failed)
				{
					bool hitTerrain = TerrainRayInPieces(from, to, 12f, out Vector3 hit);
					still += Judge(heights, from, to, hitTerrain, hit, out _) != 0 ? 1 : 0;
					downBoth += to.X < from.X && to.Z < from.Z ? 1 : 0;
				}

				Measured($"{on}: those {failed.Count} cast again as pieces of at most 12 m in plan: {still} still do not meet the first ground on them;"
					+ $" {downBoth} of the {failed.Count} were heading down both x and z, where a quarter of all the rays were");
			}

			yield return true;
		}
	}

	/// <summary>What handing the collider its 58,081 samples costs, and what making them from the file's bytes does.</summary>
	private IEnumerable<bool> SetData()
	{
		string on = GroundName;
		float[] kept = Terrain.Heights;
		float[] other = LevelTerrain.Flat();
		var times = new List<float>();
		for (int i = 0; i < (Terrain.IsTrimesh ? 10 : 40); i++)
		{
			times.Add(Terrain.Set(i % 2 == 0 ? other : kept) / 1000f);
			yield return true;
		}

		Terrain.Set(kept);
		Measured(Terrain.IsTrimesh
			? $"{on}: setting all {Terrain.Chunks.Length} chunks' triangles, 115,200 of them: {Stats(times)}"
			: $"{on}: setting the collider's data, 58,081 samples: {Stats(times)}");
		if (Terrain.IsTrimesh)
		{
			var one = new List<float>();
			for (int i = 0; i < 60; i++)
			{
				one.Add(Terrain.SetChunk(i % LevelTerrain.ChunksAcross, (i * 7) % LevelTerrain.ChunksAcross) / 1000f);
				if (i % 4 == 3)
				{
					yield return true;
				}
			}

			Measured($"{on}: setting one chunk's 512 triangles: {Stats(one)}");
		}

		// The file's samples to floats: int16 little-endian, 1/256 m.
		var bytes = new byte[LevelTerrain.Size * LevelTerrain.Size * 2];
		for (int i = 0; i < kept.Length; i++)
		{
			short q = (short)MathF.Round(kept[i] / LevelTerrain.Step);
			bytes[i * 2] = (byte)q;
			bytes[(i * 2) + 1] = (byte)(q >> 8);
		}

		var decoded = new float[kept.Length];
		var decode = new List<float>();
		bool exact = true;
		for (int n = 0; n < 20; n++)
		{
			long before = Stopwatch.GetTimestamp();
			for (int i = 0; i < decoded.Length; i++)
			{
				decoded[i] = (short)(bytes[i * 2] | (bytes[(i * 2) + 1] << 8)) * LevelTerrain.Step;
			}

			decode.Add((float)Ms(before));
		}

		for (int i = 0; i < decoded.Length; i++)
		{
			exact &= decoded[i] == kept[i];
		}

		Measured($"{on}: int16 bytes to floats, 58,081 samples: {Stats(decode)}");
		Fact(exact, $"{on}: every height is a whole number of 1/256 m steps, and reads back from int16 bit for bit");
		foreach (bool _ in Wait(5)) yield return true;
	}

	/// <summary>
	/// The bake as the first tick runs it, three times; then in its halves — the parse of
	/// the source geometry, which is the main thread's, and the bake from it, which need
	/// not be (§7.3) — and one 48 m tile of it from the kept geometry.
	/// </summary>
	private IEnumerable<bool> Bake(NavigationRegion3D region)
	{
		NavigationMesh mesh = region.NavigationMesh;
		var whole = new List<float>();
		for (int i = 0; i < 3; i++)
		{
			long before = Stopwatch.GetTimestamp();
			region.BakeNavigationMesh(onThread: false);
			whole.Add((float)Ms(before));
			yield return true;
		}

		Measured($"{GroundName}: the first tick's bake, BakeNavigationMesh(onThread: false): {F(whole[0], "0")}, {F(whole[1], "0")}, {F(whole[2], "0")} ms;"
			+ $" {mesh.GetVertices().Length} vertices, {mesh.GetPolygonCount()} polygons");

		var source = new NavigationMeshSourceGeometryData3D();
		long parseStart = Stopwatch.GetTimestamp();
		NavigationServer3D.ParseSourceGeometryData(mesh, source, region);
		double parse = Ms(parseStart);
		long bakeStart = Stopwatch.GetTimestamp();
		NavigationServer3D.BakeFromSourceGeometryData(mesh, source);
		double bake = Ms(bakeStart);
		Measured($"{GroundName}: in halves: parse {F(parse, "0.0")} ms ({source.GetVertices().Length / 3} source vertices,"
			+ $" {source.GetIndices().Length / 3} triangles), bake from it {F(bake, "0")} ms ({mesh.GetVertices().Length} vertices)");
		yield return true;

		// One tile: 48 m square round the open site, 2 m of border, from the kept geometry.
		Aabb all = mesh.FilterBakingAabb;
		float border = mesh.BorderSize;
		mesh.FilterBakingAabb = new Aabb(new Vector3(OpenSite.X - 26f, -20f, OpenSite.Z - 26f), new Vector3(52f, 100f, 52f));
		mesh.BorderSize = 2f;
		long tileStart = Stopwatch.GetTimestamp();
		NavigationServer3D.BakeFromSourceGeometryData(mesh, source);
		double tile = Ms(tileStart);
		Measured($"{GroundName}: one 48 m tile from the kept geometry: {F(tile, "0.0")} ms ({mesh.GetVertices().Length} vertices)");
		mesh.FilterBakingAabb = all;
		mesh.BorderSize = border;
		yield return true;

		region.BakeNavigationMesh(onThread: false);
		foreach (bool _ in Wait(10)) yield return true;
	}

	/// <summary>The game's own re-bake, on a thread, when a structure goes up (UnitManager.ServerNavigation).</summary>
	private IEnumerable<bool> Rebake(NavigationRegion3D region)
	{
		UnitManager units = UnitManager.Instance;
		for (int round = 0; round < 3; round++)
		{
			int before = units.NavigationRebakes;
			long start = Stopwatch.GetTimestamp();
			_frameMs.Clear();
			_recordFrames = true;
			int slot = units.ServerPlaceStructure(StructureKinds.Pillbox, OpenSite, 0f, Team.Strategist, out PlacementResult result);
			if (round == 0)
			{
				Fact(slot >= 0, $"{GroundName}: a finished pillbox on the open site ({result})");
			}

			if (slot < 0)
			{
				_recordFrames = false;
				yield break;
			}

			foreach (bool _ in Until(() => units.NavigationRebakes > before && !region.IsBaking(), 120 * Second)) yield return true;
			double took = Ms(start);
			_recordFrames = false;
			float longest = 0f;
			foreach (float ms in _frameMs)
			{
				longest = MathF.Max(longest, ms);
			}

			ulong blocked = Private<ulong>(units, "_rebakeBlockedUsec");
			Measured($"{GroundName}: a re-bake around a structure: {F(took, "0")} ms from the placement to the new mesh,"
				+ $" {F(blocked / 1000.0, "0.0")} ms of it on the tick; the longest frame meanwhile {F(longest, "0.0")} ms over {_frameMs.Count} frames");
			foreach (bool _ in Wait(10)) yield return true;

			// Down again, which is a re-bake of its own; wait it out.
			int cleared = units.NavigationRebakes;
			Call(units, "ClearStructures");
			foreach (bool _ in Wait(5)) yield return true;
			foreach (bool _ in Until(() => !region.IsBaking(), 120 * Second)) yield return true;
			foreach (bool _ in Wait(30)) yield return true;
			if (round == 0)
			{
				Measured($"{GroundName}: clearing the structures asked for {units.NavigationRebakes - cleared} re-bake(s)");
			}
		}
	}

	// ---- slopes: a body, a unit, the 4x4, the helicopter, structure sites -----------------------

	private IEnumerator<bool> Slopes()
	{
		foreach (bool _ in InGame(wantLocal: true)) yield return true;
		Fact(Terrain != null, $"the ground is the terrain ({Terrain?.ColliderName})");
		if (Terrain == null)
		{
			yield break;
		}

		foreach (bool _ in Wait(30)) yield return true;
		foreach (bool _ in LocalOnGround()) yield return true;

		string only = System.Environment.GetEnvironmentVariable("LE0_ONLY") ?? "";
		bool All(string part) => only.Length == 0 || only.Contains(part);

		if (All("body")) foreach (bool _ in BodySlopes()) yield return true;
		if (All("unit")) foreach (bool _ in UnitSlopes()) yield return true;
		if (All("4x4")) foreach (bool _ in FourByFour()) yield return true;
		if (All("heli")) foreach (bool _ in Helicopter()) yield return true;
		if (All("sites")) foreach (bool _ in Sites()) yield return true;
	}

	private IEnumerable<bool> SetGround(float[] heights)
	{
		Terrain.Set(heights);
		foreach (bool _ in Wait(3)) yield return true;
	}

	private void Put(fps_controller me, Vector3 at, float yaw)
	{
		me.Teleport(new Transform3D(new Basis(Vector3.Up, yaw), at));
		Sampler.Align(yaw, 0f);
	}

	/// <summary>
	/// The slab's kind of ramp, for comparison: one <see cref="BoxShape3D"/> turned to the
	/// grade, its top face the plane a terrain ramp of the same grade is, on the lane.
	/// </summary>
	private StaticBody3D BoxRamp(float grade, int run)
	{
		float angle = MathF.Atan(grade);
		float length = run / MathF.Cos(angle);
		var along = new Vector3(MathF.Cos(angle), MathF.Sin(angle), 0f);
		var normal = new Vector3(-MathF.Sin(angle), MathF.Cos(angle), 0f);
		var foot = new Vector3(LevelTerrain.RampWest, LevelTerrain.Base, LaneZ);
		var body = new StaticBody3D { Name = "BoxRamp", CollisionLayer = CollisionLayers.World, CollisionMask = 0 };
		body.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(length, 1f, 20f) } });
		body.Transform = new Transform3D(new Basis(Vector3.Back, angle), foot + (along * (length * 0.5f)) - (normal * 0.5f));
		body.AddToGroup("navmesh");
		GetTree().CurrentScene.AddChild(body);
		return body;
	}

	/// <summary>
	/// The local body walked east at a ramp from 3 m short of its foot until it is over the
	/// top or <paramref name="ticks"/> are up. result: 0 up it (1/0), 1 seconds,
	/// 2 metres it rose, 3 the x it ended at, 4 ticks on the floor, 5 ticks on a wall,
	/// 6 and 7 the least and greatest slope [°] of the normals its slide met on the ramp.
	/// </summary>
	private IEnumerable<bool> Walk(fps_controller me, float topX, float rise, int ticks, float[] result)
	{
		float east = YawFacing(Vector3.Right);
		Put(me, new Vector3(LevelTerrain.RampWest - 3f, LevelTerrain.Base + 0.1f, LaneZ), east);
		foreach (bool _ in Wait(30)) yield return true;
		Array.Clear(result);
		result[6] = float.MaxValue;
		float startY = me.SimPosition.Y;
		Input.ActionPress("move_forward");
		int i = 0;
		for (; i < ticks; i++)
		{
			yield return true;
			Vector3 at = me.SimPosition;
			result[2] = MathF.Max(result[2], at.Y - startY);
			if (at.X > LevelTerrain.RampWest + 0.5f && at.X < topX - 0.5f)
			{
				result[4] += me.IsOnFloor() ? 1 : 0;
				result[5] += me.IsOnWall() ? 1 : 0;
				// Every contact of every slide this tick, not only each slide's first: a
				// capsule within its margin of a triangle's edge touches the triangle beyond
				// it too, on a normal that is not the face's.
				for (int c = 0; c < me.GetSlideCollisionCount(); c++)
				{
					KinematicCollision3D slide = me.GetSlideCollision(c);
					for (int n = 0; n < slide.GetCollisionCount(); n++)
					{
						float slope = Mathf.RadToDeg(MathF.Acos(Math.Clamp(slide.GetNormal(n).Y, -1f, 1f)));
						result[6] = MathF.Min(result[6], slope);
						result[7] = MathF.Max(result[7], slope);
					}
				}
			}

			// Up it: as high as its top, whatever is beyond that.
			if (at.Y - startY > rise - 0.1f)
			{
				result[0] = 1f;
				break;
			}
		}

		Input.ActionRelease("move_forward");
		result[1] = i / (float)Second;
		result[3] = me.SimPosition.X;
		result[6] = result[6] == float.MaxValue ? 0f : result[6];
		foreach (bool _ in Wait(10)) yield return true;
	}

	/// <summary>
	/// A body at ramps from 20° to 46°: on the terrain, and on a box turned to the same
	/// grade, which is the kind of slope the greybox has had (its test strip's ramp).
	/// </summary>
	private IEnumerable<bool> BodySlopes()
	{
		fps_controller me = CombatManager.Instance.Local.Character;
		string collider = Terrain.ColliderName;
		Measured($"the body: floor_max_angle {F(Mathf.RadToDeg(me.FloorMaxAngle), "0.0")}°, floor_snap_length {F(me.FloorSnapLength)} m,"
			+ $" floor_constant_speed {me.FloorConstantSpeed}, floor_block_on_wall {me.FloorBlockOnWall}, safe_margin {F(me.SafeMargin, "0.000")} m");

		var result = new float[8];
		float[] grades = { 20f, 30f, 35f, 38f, 39f, 40f, 41f, 42f, 43f, 44f, 45f, 46f };
		foreach (bool box in new[] { false, true })
		{
			string on = box ? "a box" : $"the {collider}";
			float steepest = 0f;
			bool walks46 = false;
			foreach (float degrees in grades)
			{
				float[] heights = LevelTerrain.Ramp(degrees, 8f, ridge: false, out float grade, out int run);
				StaticBody3D ramp = null;
				if (box)
				{
					foreach (bool _ in SetGround(LevelTerrain.Flat())) yield return true;
					ramp = BoxRamp(grade, run);
					foreach (bool _ in Wait(3)) yield return true;
				}
				else
				{
					foreach (bool _ in SetGround(heights)) yield return true;
				}

				float actual = Mathf.RadToDeg(MathF.Atan(grade));
				float top = grade * run;
				foreach (bool _ in Walk(me, LevelTerrain.RampWest + run, top, 12 * Second, result)) yield return true;
				bool climbed = result[0] > 0f;

				// What the engine's slide gives a body that meets nothing but the face: the
				// walk speed and a tick of gravity, along the slope.
				float expected = ((5f * MathF.Cos(MathF.Atan(grade))) - (9.8f / Second * MathF.Sin(MathF.Atan(grade)))) * MathF.Cos(MathF.Atan(grade));
				Measured($"a body walking at {F(actual)}° on {on}, {run} m long and {F(top)} m high:"
					+ $" {(climbed ? $"up it in {F(result[1], "0.0")} s" : $"not up it in 12 s, up {F(result[2])} m, at x {F(result[3], "0.0")}")};"
					+ $" on the face, {F(result[4], "0")} ticks on the floor and {F(result[5], "0")} on a wall, the normals it met {F(result[6], "0.0")}° to {F(result[7], "0.0")}°"
					+ $" (a clean slide would make {F(expected)} m/s over the ground)");
				steepest = climbed ? MathF.Max(steepest, actual) : steepest;
				walks46 |= climbed && degrees >= 46f;
				ramp?.QueueFree();
				foreach (bool _ in Wait(3)) yield return true;
			}

			Measured($"the steepest face a body walked up on {on}: {F(steepest)}°");
			Fact(!walks46, $"a body does not walk 46° on {on}");
			if (!box)
			{
				// What the plan's skirts leave: 39.2° from a 30° skirt (§6.3).
				Fact(steepest >= 39f, $"a body walks the {F(39.2f, "0.0")}° faces a 30° skirt can leave, on {on} (steepest walked {F(steepest)}°)");
			}
		}

		// Speed: on the flat, then up and down 20°.
		float[] slope20 = LevelTerrain.Ramp(20f, 12f, ridge: false, out float grade20, out int run20);
		foreach (bool _ in SetGround(slope20)) yield return true;
		float east = YawFacing(Vector3.Right);
		float west = YawFacing(Vector3.Left);
		float Speed(Vector3 a, Vector3 b, int ticks) => new Vector2(b.X - a.X, b.Z - a.Z).Length() / (ticks / (float)Second);

		Put(me, new Vector3(-40f, LevelTerrain.Base + 0.1f, LaneZ), east);
		foreach (bool _ in Wait(30)) yield return true;
		Input.ActionPress("move_forward");
		foreach (bool _ in Wait(Second)) yield return true;
		Vector3 a = me.SimPosition;
		foreach (bool _ in Wait(3 * Second)) yield return true;
		float flat = Speed(a, me.SimPosition, 3 * Second);
		Input.ActionRelease("move_forward");

		Put(me, new Vector3(LevelTerrain.RampWest + 3f, GroundAt(LevelTerrain.RampWest + 3f, LaneZ) + 0.1f, LaneZ), east);
		foreach (bool _ in Wait(30)) yield return true;
		Input.ActionPress("move_forward");
		foreach (bool _ in Wait(Second)) yield return true;
		a = me.SimPosition;
		foreach (bool _ in Wait(3 * Second)) yield return true;
		float up = Speed(a, me.SimPosition, 3 * Second);
		Input.ActionRelease("move_forward");

		float startDown = LevelTerrain.RampWest + run20 - 3f;
		Put(me, new Vector3(startDown, GroundAt(startDown, LaneZ) + 0.1f, LaneZ), west);
		foreach (bool _ in Wait(30)) yield return true;
		Input.ActionPress("move_forward");
		foreach (bool _ in Wait(Second)) yield return true;
		a = me.SimPosition;
		int airborne = 0;
		for (int i = 0; i < 3 * Second; i++)
		{
			yield return true;
			airborne += me.IsOnFloor() ? 0 : 1;
		}

		float down = Speed(a, me.SimPosition, 3 * Second);
		Input.ActionRelease("move_forward");
		Measured($"a body's speed over the ground on the {collider}: {F(flat)} m/s on the flat, {F(up)} m/s up {F(Mathf.RadToDeg(MathF.Atan(grade20)))}°,"
			+ $" {F(down)} m/s down it ({airborne} of {3 * Second} ticks off the floor going down)");
		foreach (bool _ in Wait(10)) yield return true;
	}

	/// <summary>A rifleman ordered up ramps from 20° to 46°, the mesh baked again for each: on the terrain, and on a box.</summary>
	private IEnumerable<bool> UnitSlopes()
	{
		UnitManager units = UnitManager.Instance;
		NavigationRegion3D region = Region();
		string collider = Terrain.ColliderName;
		float[] grades = { 20f, 30f, 35f, 38f, 40f, 42f, 43f, 44f, 45f, 46f };
		foreach (bool box in new[] { false, true })
		{
			string on = box ? "a box" : $"the {collider}";
			float steepest = 0f;
			float steepestBaked = 0f;
			bool walks46 = false;
			foreach (float degrees in grades)
			{
				float[] heights = LevelTerrain.Ramp(degrees, 8f, ridge: false, out float grade, out int run);
				StaticBody3D ramp = null;
				if (box)
				{
					foreach (bool _ in SetGround(LevelTerrain.Flat())) yield return true;
					ramp = BoxRamp(grade, run);
					foreach (bool _ in Wait(3)) yield return true;
				}
				else
				{
					foreach (bool _ in SetGround(heights)) yield return true;
				}

				foreach (bool _ in Until(() => !region.IsBaking(), 60 * Second)) yield return true;
				long before = Stopwatch.GetTimestamp();
				region.BakeNavigationMesh(onThread: false);
				double bake = Ms(before);
				foreach (bool _ in Wait(10)) yield return true;

				float actual = Mathf.RadToDeg(MathF.Atan(grade));
				float top = LevelTerrain.Base + (grade * run);
				var middle = new Vector3(LevelTerrain.RampWest + (run * 0.5f), LevelTerrain.Base + (grade * run * 0.5f), LaneZ);
				Rid map = GetTree().Root.World3D.NavigationMap;
				float offMesh = (NavigationServer3D.MapGetClosestPoint(map, middle) - middle).Length();
				bool baked = offMesh < 0.5f;

				// On the box the top is its far edge, which is a drop: the goal is 1 m short of it.
				var foot = new Vector3(LevelTerrain.RampWest - 5f, LevelTerrain.Base, LaneZ);
				float goalX = box ? LevelTerrain.RampWest + run - 1f : LevelTerrain.RampWest + run + 6f;
				float goalY = box ? top - grade : top;
				var goal = new Vector3(goalX, goalY, LaneZ);
				ushort id = units.ServerPlaceUnit(UnitCatalog.Infantry, foot, Team.Strategist, OrderKind.None);
				Unit unit = units.Find(id);
				if (unit == null)
				{
					Fact(false, "a rifleman at the foot of the ramp");
					ramp?.QueueFree();
					continue;
				}

				foreach (bool _ in Wait(30)) yield return true;
				unit.Order = new UnitOrder { Kind = OrderKind.Move, Target = goal, Anchor = goal, TargetOwnerId = OwnerId.None };
				int took = 0;
				bool arrived = false;
				int floor = 0;
				int wall = 0;
				for (; took < 30 * Second; took++)
				{
					yield return true;
					Vector3 at = unit.GlobalPosition;
					if (at.X > LevelTerrain.RampWest + 0.5f)
					{
						floor += unit.IsOnFloor() ? 1 : 0;
						wall += unit.IsOnWall() ? 1 : 0;
					}

					if (new Vector2(at.X - goal.X, at.Z - goal.Z).Length() < 1.5f && at.Y > goalY - 0.5f)
					{
						arrived = true;
						break;
					}
				}

				Vector3 ended = unit.GlobalPosition;
				Measured($"a rifleman ordered up {F(actual)}° on {on} (baked in {F(bake, "0")} ms; the mesh is {F(offMesh)} m from the face's middle):"
					+ $" {(arrived ? $"there in {F(took / (float)Second, "0.0")} s" : "not there in 30 s")}, at ({F(ended.X, "0.0")}, {F(ended.Y, "0.0")}, {F(ended.Z, "0.0")});"
					+ $" past the foot, {floor} ticks on the floor and {wall} on a wall");
				steepest = arrived ? MathF.Max(steepest, actual) : steepest;
				steepestBaked = baked ? MathF.Max(steepestBaked, actual) : steepestBaked;
				walks46 |= arrived && degrees >= 46f;

				units.ClearUnits();
				ramp?.QueueFree();
				foreach (bool _ in Wait(10)) yield return true;
			}

			Measured($"the steepest face the bake kept on {on}: {F(steepestBaked)}°; the steepest a rifleman walked up: {F(steepest)}°");
			Fact(!walks46, $"a rifleman does not walk 46° on {on}");
			if (!box)
			{
				Fact(steepest >= 39f, $"a rifleman walks the {F(39.2f, "0.0")}° faces a 30° skirt can leave, on {on} (steepest walked {F(steepest)}°)");
			}
		}
	}

	/// <summary>
	/// A 4x4 put on the flat at <paramref name="startX"/> on the lane, facing the ramp, the
	/// local player at its wheel and the throttle held: whether it got its origin past
	/// <paramref name="goalX"/>, and what it did on the way. result: 0 past it (1/0),
	/// 1 seconds, 2 speed at the ramp's foot, 3 slowest past the foot, 4 steepest nose-up
	/// [°], 5 longest it was off all four wheels [ticks], 6 x it ended at, 7 highest its
	/// origin got.
	/// </summary>
	private IEnumerable<bool> Drive(float startX, float goalX, int maxTicks, float[] result)
	{
		CrewVehicleManager crew = CrewVehicleManager.Instance;
		PlayerCombat local = CombatManager.Instance.Local;
		FourByFourTraits traits = CrewVehicleCatalog.Definition(CrewVehicleCatalog.FourByFour).ToFourByFourTraits();
		Array.Clear(result);

		int slot = crew.Spawn(CrewVehicleCatalog.FourByFour,
			new Vector3(startX, LevelTerrain.Base + traits.RideHeightMeters, LaneZ), YawFacing(Vector3.Right));
		if (slot < 0)
		{
			result[0] = -1f;
			yield break;
		}

		foreach (bool _ in Wait(Second)) yield return true;
		CrewVehicle truck = crew.VehicleAt(slot);
		string refused = crew.ServerSeatPlaced(local.PeerId, slot, SeatRole.Driver);
		if (refused != null)
		{
			GD.Print($"[checks] the driver's seat: {refused}");
			result[0] = -1f;
			crew.Remove(slot);
			yield break;
		}

		foreach (bool _ in Wait(10)) yield return true;
		Sampler.Align(YawFacing(Vector3.Right), 0f);
		Input.ActionPress("move_forward");
		float slowest = float.MaxValue;
		bool pastFoot = false;
		int sinceFoot = 0;
		int ticks = 0;
		for (; ticks < maxTicks; ticks++)
		{
			yield return true;
			float x = truck.Drive.Position.X;
			if (!pastFoot && x >= LevelTerrain.RampWest)
			{
				pastFoot = true;
				result[2] = truck.Speed;
			}

			if (pastFoot && sinceFoot++ > 30)
			{
				slowest = MathF.Min(slowest, truck.Speed);
			}

			result[4] = MathF.Max(result[4], Mathf.RadToDeg(truck.Drive.Pitch));
			result[5] = MathF.Max(result[5], truck.Drive.AirTicks);
			result[7] = MathF.Max(result[7], truck.Drive.Position.Y);
			if (x >= goalX)
			{
				result[0] = 1f;
				break;
			}
		}

		Input.ActionRelease("move_forward");
		result[1] = ticks / (float)Second;
		result[3] = slowest == float.MaxValue ? 0f : slowest;
		result[6] = truck.Drive.Position.X;
		crew.Remove(slot);
		foreach (bool _ in Wait(20)) yield return true;
	}

	/// <summary>
	/// The 4x4 driven onto a slope from the flat, stopped on it with the handbrake, held
	/// there, then left. result: 0 the x it stopped at, 1 its speed after 3 s of handbrake,
	/// 2 metres it moved in 5 s more of handbrake with its driver in, 3 metres it moved in
	/// the 5 s after the driver was taken out, 4 its nose [°] at rest.
	/// </summary>
	private IEnumerable<bool> Park(float[] result)
	{
		CrewVehicleManager crew = CrewVehicleManager.Instance;
		PlayerCombat local = CombatManager.Instance.Local;
		FourByFourTraits traits = CrewVehicleCatalog.Definition(CrewVehicleCatalog.FourByFour).ToFourByFourTraits();
		Array.Clear(result);
		int slot = crew.Spawn(CrewVehicleCatalog.FourByFour,
			new Vector3(LevelTerrain.RampWest - 12f, LevelTerrain.Base + traits.RideHeightMeters, LaneZ), YawFacing(Vector3.Right));
		if (slot < 0 || crew.ServerSeatPlaced(local.PeerId, slot, SeatRole.Driver) != null)
		{
			result[0] = float.NaN;
			yield break;
		}

		foreach (bool _ in Wait(Second)) yield return true;
		CrewVehicle truck = crew.VehicleAt(slot);
		Sampler.Align(YawFacing(Vector3.Right), 0f);
		Input.ActionPress("move_forward");
		foreach (bool _ in Until(() => truck.Drive.Position.X > LevelTerrain.RampWest + 8f, 10 * Second)) yield return true;
		Input.ActionRelease("move_forward");
		Input.ActionPress("jump");
		foreach (bool _ in Wait(3 * Second)) yield return true;
		result[0] = truck.Drive.Position.X;
		result[1] = truck.Speed;
		result[4] = Mathf.RadToDeg(truck.Drive.Pitch);
		Vector3 held = truck.Drive.Position;
		foreach (bool _ in Wait(5 * Second)) yield return true;
		result[2] = (truck.Drive.Position - held).Length();
		Input.ActionRelease("jump");

		// Empty, it is stepped on the parked frame: nothing pressed but the handbrake.
		crew.Remove(slot);
		foreach (bool _ in Wait(5)) yield return true;
		Vector3 at = held;
		slot = crew.Spawn(CrewVehicleCatalog.FourByFour, at, YawFacing(Vector3.Right));
		if (slot >= 0)
		{
			truck = crew.VehicleAt(slot);
			truck.Drive.Pitch = Mathf.DegToRad(result[4]);
			foreach (bool _ in Wait(Second)) yield return true;
			Vector3 settled = truck.Drive.Position;
			foreach (bool _ in Wait(5 * Second)) yield return true;
			result[3] = (truck.Drive.Position - settled).Length();
			crew.Remove(slot);
		}

		foreach (bool _ in Wait(20)) yield return true;
	}

	private IEnumerable<bool> FourByFour()
	{
		fps_controller me = CombatManager.Instance.Local.Character;
		var result = new float[8];
		float[] grades = { 10f, 15f, 20f, 25f, 30f, 35f, 40f, 45f, 50f, 60f, 70f, 75f, 80f, 85f };
		string collider = Terrain.ColliderName;

		// From rest: on the flat, its front axle 1 m short of the foot. A degree at a time,
		// since its hull is level whatever its chassis does and has 0.25 m under it.
		float restBest = 0f;
		int restFails = 0;
		foreach (float degrees in new[] { 3f, 4f, 5f, 6f, 7f, 8f, 9f, 10f, 12f, 15f, 20f })
		{
			if (restFails >= 2)
			{
				break;
			}

			float[] ramp = LevelTerrain.Ramp(degrees, 4f, ridge: false, out float grade, out int run);
			float actual = Mathf.RadToDeg(MathF.Atan(grade));
			foreach (bool _ in SetGround(ramp)) yield return true;
			Put(me, new Vector3(-20f, LevelTerrain.Base + 0.1f, 60f), 0f);
			foreach (bool _ in Drive(LevelTerrain.RampWest - 2.5f, LevelTerrain.RampWest + run + 4f, 20 * Second, result)) yield return true;
			bool reached = result[0] > 0f;
			Measured($"4x4 from rest at the foot of {F(actual, "0.0")}° on the {collider} ({run} m, {F(grade * run, "0.0")} m high): {F(result[2], "0.0")} m/s at the foot;"
				+ $" {(reached ? $"over the top in {F(result[1], "0.0")} s" : $"not over the top in 20 s, at x {F(result[6], "0.0")}")},"
				+ $" slowest {F(result[3], "0.0")} m/s, nose up to {F(result[4], "0.0")}°, off its wheels up to {F(result[5], "0")} ticks");
			restBest = reached ? actual : restBest;
			restFails = reached ? 0 : restFails + 1;
		}

		float speedBest = 0f;
		float ridgeBest = 0f;
		int speedFails = 0;
		int ridgeFails = 0;
		foreach (float degrees in grades)
		{
			float[] ramp = LevelTerrain.Ramp(degrees, 8f, ridge: false, out float grade, out int run);
			float actual = Mathf.RadToDeg(MathF.Atan(grade));
			float goal = LevelTerrain.RampWest + run + 4f;

			if (speedFails < 2)
			{
				foreach (bool _ in SetGround(ramp)) yield return true;
				Put(me, new Vector3(-20f, LevelTerrain.Base + 0.1f, 60f), 0f);
				foreach (bool _ in Drive(-40f, goal, 20 * Second, result)) yield return true;
				bool reached = result[0] > 0f;
				Measured($"4x4 at speed onto {F(actual, "0.0")}° on the {collider}: {F(result[2], "0.0")} m/s at the foot;"
					+ $" {(reached ? $"over the top in {F(result[1], "0.0")} s" : $"not over the top in 20 s, at x {F(result[6], "0.0")}")},"
					+ $" slowest {F(result[3], "0.0")} m/s, nose up to {F(result[4], "0.0")}°, off its wheels up to {F(result[5], "0")} ticks,"
					+ $" its origin up to {F(result[7] - LevelTerrain.Base - (grade * run), "0.0")} m over the top");
				speedBest = reached ? actual : speedBest;
				speedFails = reached ? 0 : speedFails + 1;
			}

			if (ridgeFails < 2)
			{
				float[] ridge = LevelTerrain.Ramp(degrees, 4f, ridge: true, out float ridgeGrade, out int ridgeRun);
				foreach (bool _ in SetGround(ridge)) yield return true;
				Put(me, new Vector3(-20f, LevelTerrain.Base + 0.1f, 60f), 0f);
				foreach (bool _ in Drive(-40f, LevelTerrain.RampWest + (2 * ridgeRun) + 8f, 20 * Second, result)) yield return true;
				bool reached = result[0] > 0f;
				Measured($"4x4 at speed over a {F(actual, "0.0")}° ridge {F(ridgeGrade * ridgeRun, "0.0")} m high on the {collider}: {F(result[2], "0.0")} m/s at the foot;"
					+ $" {(reached ? $"across in {F(result[1], "0.0")} s" : $"not across in 20 s, at x {F(result[6], "0.0")}")},"
					+ $" slowest {F(result[3], "0.0")} m/s, nose up to {F(result[4], "0.0")}°, off its wheels up to {F(result[5], "0")} ticks,"
					+ $" its origin up to {F(result[7] - LevelTerrain.Base - (ridgeGrade * ridgeRun), "0.0")} m over the crest");
				ridgeBest = reached ? actual : ridgeBest;
				ridgeFails = reached ? 0 : ridgeFails + 1;
			}
		}

		Measured($"4x4 on the {collider}: the steepest it got up from rest at the foot {F(restBest, "0.0")}°, at speed {F(speedBest, "0.0")}°,"
			+ $" over a ridge {F(ridgeBest, "0.0")}°");
		Fact(restBest >= 5f && speedBest >= 10f, "the 4x4 climbs 5° from rest and 10° at speed");

		foreach (float degrees in new[] { 5f, 10f, 15f, 20f })
		{
			float[] slope = LevelTerrain.Ramp(degrees, 12f, ridge: false, out float grade, out _);
			foreach (bool _ in SetGround(slope)) yield return true;
			Put(me, new Vector3(-20f, LevelTerrain.Base + 0.1f, 60f), 0f);
			foreach (bool _ in Park(result)) yield return true;
			Measured($"4x4 stopped on {F(Mathf.RadToDeg(MathF.Atan(grade)), "0.0")}° with the handbrake: at x {F(result[0], "0.0")}, nose {F(result[4], "0.0")}°,"
				+ $" {F(result[1])} m/s after 3 s of it; then it moved {F(result[2])} m in 5 s with its driver holding it, and {F(result[3])} m in 5 s left empty");
		}
	}

	private IEnumerable<bool> Helicopter()
	{
		foreach (bool _ in SetGround(Terrain.Build("hills"))) yield return true;
		CrewVehicleManager crew = CrewVehicleManager.Instance;
		PlayerCombat local = CombatManager.Instance.Local;
		fps_controller me = local.Character;
		Put(me, OpenSite + new Vector3(6f, 0.1f, 0f), 0f);

		int slot = crew.Spawn(CrewVehicleCatalog.Helicopter, OpenSite, YawFacing(Vector3.Left));
		Fact(slot >= 0, "a helicopter on the open site, on the hills");
		if (slot < 0)
		{
			yield break;
		}

		foreach (bool _ in Wait(30)) yield return true;
		CrewVehicle heli = crew.VehicleAt(slot);
		string refused = crew.ServerSeatPlaced(local.PeerId, slot, SeatRole.Driver);
		Fact(refused == null, $"the local player at its controls ({refused ?? "seated"})");
		if (refused != null)
		{
			yield break;
		}

		float ceiling = heli.FlightTraits.CeilingMeters;
		foreach (bool _ in Wait(3 * Second)) yield return true;

		// West along z = 30 with the climb held; then down to 8 m over the ground there,
		// and back east with nothing held.
		foreach (bool climb in new[] { true, false })
		{
			Vector3 direction = climb ? Vector3.Left : Vector3.Right;
			Sampler.Align(YawFacing(direction), 0f);
			if (climb)
			{
				Input.ActionPress("jump");
				foreach (bool _ in Until(() => heli.HeightAboveGround > ceiling - 0.5f, 30 * Second)) yield return true;
			}
			else
			{
				Input.ActionPress("crouch");
				foreach (bool _ in Until(() => heli.HeightAboveGround < 8f, 30 * Second)) yield return true;
				Input.ActionRelease("crouch");
				foreach (bool _ in Wait(2 * Second)) yield return true;
			}

			float startX = heli.Flight.Position.X;
			float startY = heli.Flight.Position.Y;
			float startHeight = heli.HeightAboveGround;
			float lowest = float.MaxValue;
			float highest = float.MinValue;
			float groundLow = float.MaxValue;
			float groundHigh = float.MinValue;
			float altitudeLow = float.MaxValue;
			float altitudeHigh = float.MinValue;
			float slowest = float.MaxValue;
			int grounded = 0;
			int ticks = 0;
			Input.ActionPress("move_forward");
			for (; ticks < 40 * Second && (climb ? heli.Flight.Position.X > -100f : heli.Flight.Position.X < OpenSite.X); ticks++)
			{
				yield return true;
				float height = heli.HeightAboveGround;
				Vector3 at = heli.Flight.Position;
				if (float.IsFinite(height))
				{
					lowest = MathF.Min(lowest, height);
					highest = MathF.Max(highest, height);
					groundLow = MathF.Min(groundLow, at.Y - height);
					groundHigh = MathF.Max(groundHigh, at.Y - height);
				}

				altitudeLow = MathF.Min(altitudeLow, at.Y);
				altitudeHigh = MathF.Max(altitudeHigh, at.Y);
				grounded += heli.Grounded ? 1 : 0;
				if (ticks > 3 * Second)
				{
					slowest = MathF.Min(slowest, heli.Speed);
				}
			}

			Input.ActionRelease("move_forward");
			Input.ActionRelease("jump");
			float endX = heli.Flight.Position.X;
			string pass = climb
				? "west, the climb held"
				: $"east, nothing held, from {F(startHeight, "0.0")} m over the ground (altitude {F(startY, "0.0")} m)";
			Measured($"helicopter over the hills, {pass}: x {F(startX, "0.0")} to {F(endX, "0.0")} in {F(ticks / (float)Second, "0.0")} s;"
				+ $" ground under it {F(groundLow, "0.0")} to {F(groundHigh, "0.0")} m; skids over the ground {F(lowest, "0.0")} to {F(highest, "0.0")} m"
				+ $" (ceiling {F(ceiling, "0")} m); altitude {F(altitudeLow, "0.0")} to {F(altitudeHigh, "0.0")} m; slowest {F(slowest, "0.0")} m/s;"
				+ $" grounded {grounded} ticks");
			Fact(grounded == 0 && (climb ? endX <= -100f : endX >= OpenSite.X),
				climb
					? $"with the climb held it crosses the hills without touching them, at most {F(highest - ceiling, "0.0")} m over its ceiling"
					: $"with nothing held it crosses the hills without touching them, at least {F(lowest, "0.0")} m over the ground");
		}

		crew.Remove(slot);
		foreach (bool _ in Wait(20)) yield return true;
	}

	/// <summary>
	/// Structure sites on a slope. Through <c>ServerConstruct</c>, the computer strategist's
	/// entry point, with a builder and the mesh baked, on level ground, 5° and 10°; then
	/// through <c>ServerPlaceStructure</c>, which asks the same ground questions without a
	/// builder, grade by grade to where each structure is first refused.
	/// </summary>
	private IEnumerable<bool> Sites()
	{
		UnitManager units = UnitManager.Instance;
		CombatManager combat = CombatManager.Instance;
		PlayerCombat local = combat.Local;
		NavigationRegion3D region = Region();
		combat.RequestTeam(Team.Strategist);
		foreach (bool _ in Until(() => local.Team == Team.Strategist, 20 * Second)) yield return true;
		Fact(!_timedOut, "the listen host's player is the strategist");

		byte[] kinds = { StructureKinds.Pillbox, StructureKinds.SandbagWall, StructureKinds.SniperTower };
		float[] facings = { 0f, 45f, 90f };
		var builders = new int[1];

		foreach (float degrees in new[] { 0f, 5f, 10f })
		{
			float grade = 0f;
			int run = 36;
			float[] heights = degrees == 0f
				? LevelTerrain.Flat()
				: LevelTerrain.Ramp(degrees, degrees == 5f ? 3f : 6f, ridge: false, out grade, out run);
			foreach (bool _ in SetGround(heights)) yield return true;
			region.BakeNavigationMesh(onThread: false);
			foreach (bool _ in Wait(10)) yield return true;
			combat.Match.Strategist.Reset(1_000_000);

			float siteX = LevelTerrain.RampWest + (run * 0.5f);
			float groundY = LevelTerrain.Base + (grade * run * 0.5f);
			ushort builder = units.ServerPlaceUnit(UnitCatalog.Builder, new Vector3(siteX - 8f, groundY - (grade * 8f), 56f),
				Team.Strategist, OrderKind.None);
			foreach (bool _ in Wait(30)) yield return true;
			builders[0] = builder;

			var row = new List<string>();
			int site = 0;
			foreach (byte kind in kinds)
			{
				foreach (float facing in facings)
				{
					var at = new Vector3(siteX, groundY, 60f + (site++ * 5.5f));
					PlacementResult result = Construct(local.PeerId, builders, kind, at, Mathf.DegToRad(facing));
					row.Add($"{StructureCatalog.NameOf(kind)} at {F(facing, "0")}° {result}");
					yield return true;
				}
			}

			Measured($"ServerConstruct with a builder on {F(Mathf.RadToDeg(MathF.Atan(grade)), "0.0")}°: {string.Join("; ", row)}");
			units.ClearUnits();
			Call(units, "ClearStructures");
			foreach (bool _ in Wait(10)) yield return true;
			foreach (bool _ in Until(() => !region.IsBaking(), 120 * Second)) yield return true;
		}

		// Grade by grade, a step of 1/256 m a metre at a time is 0.22°: every other step.
		foreach (byte kind in kinds)
		{
			StructureShape shape = StructureCatalog.ShapeOf(kind);
			foreach (float facing in new[] { 0f, 45f, 90f })
			{
				float lastOk = -1f;
				float firstRefused = -1f;
				PlacementResult refusal = PlacementResult.Ok;
				float gap = 0f;
				for (int steps = 0; steps <= 80; steps += 2)
				{
					float grade = steps * LevelTerrain.Step;
					float[] heights = LevelTerrain.Flat();
					for (int z = LevelTerrain.RampSouth; z <= LevelTerrain.RampNorth; z++)
					{
						for (int x = LevelTerrain.RampWest; x <= LevelTerrain.RampWest + 40; x++)
						{
							heights[((z + LevelTerrain.Half) * LevelTerrain.Size) + x + LevelTerrain.Half] =
								LevelTerrain.Base + (grade * (x - LevelTerrain.RampWest));
						}
					}

					foreach (bool _ in SetGround(heights)) yield return true;
					var at = new Vector3(LevelTerrain.RampWest + 20f, LevelTerrain.Base + (grade * 20f), LaneZ);
					int slot = units.ServerPlaceStructure(kind, at, Mathf.DegToRad(facing), Team.Strategist, out PlacementResult result);
					float actual = Mathf.RadToDeg(MathF.Atan(grade));
					if (slot >= 0)
					{
						lastOk = actual;

						// How far the base's downhill edge stands over the ground.
						float reach = (MathF.Abs(MathF.Cos(Mathf.DegToRad(facing))) * shape.HalfWidth)
							+ (MathF.Abs(MathF.Sin(Mathf.DegToRad(facing))) * shape.HalfDepth);
						gap = grade * reach;
						Call(units, "ClearStructures");
						foreach (bool _ in Wait(2)) yield return true;
					}
					else if (firstRefused < 0f)
					{
						firstRefused = actual;
						refusal = result;
						break;
					}
				}

				Measured($"ServerPlaceStructure, {StructureCatalog.NameOf(kind)} ({F(shape.HalfWidth * 2f, "0.0")} x {F(shape.HalfDepth * 2f, "0.0")} m) at {F(facing, "0")}°:"
					+ $" taken up to {F(lastOk)}°, its downhill edge then {F(gap)} m over the ground;"
					+ (firstRefused < 0f ? " never refused up to 17.4°" : $" refused from {F(firstRefused)}° as {refusal} (\"{StructurePlacement.Describe(refusal)}\")"));
			}
		}

		foreach (bool _ in Until(() => !region.IsBaking(), 120 * Second)) yield return true;
	}

	private static PlacementResult Construct(int peerId, int[] builders, byte kind, Vector3 at, float facing) =>
		UnitManager.Instance.ServerConstruct(peerId, builders, kind, at, facing, out _);

	// ---- frame: a tick's wall time with the bots playing -----------------------------------------

	private IEnumerator<bool> Frame()
	{
		foreach (bool _ in InGame(wantLocal: false)) yield return true;
		Fact(!_timedOut && UnitManager.Instance.NavigationReady, $"{GroundName}: a dedicated server in the game, baked");
		if (_timedOut)
		{
			yield break;
		}

		// The bots are put in while somebody is connected, and the round is live once one of
		// them is on a side: the script starts a client that joins and picks nothing. This
		// server runs at --fixed-fps, far ahead of that client's clock, so the wait is by the
		// wall's.
		int seconds = int.TryParse(System.Environment.GetEnvironmentVariable("LE0_FRAME_SECONDS"), out int asked) ? asked : 300;
		UnitManager units = UnitManager.Instance;
		CombatManager combat = CombatManager.Instance;
		long waiting = Stopwatch.GetTimestamp();
		while (!combat.Match.IsLive && Ms(waiting) < 180_000)
		{
			yield return true;
		}

		Fact(combat.Match.IsLive, $"{GroundName}: a client is in, the bots are on their sides ({combat.Teams.ReadyCount} ready) and the round is live");
		if (!combat.Match.IsLive)
		{
			yield break;
		}

		_frameMs.Clear();
		_recordFrames = true;
		int most = 0;
		int rounds = 1;
		bool live = true;
		for (int i = 0; i < seconds * Second; i++)
		{
			yield return true;
			most = Math.Max(most, units.SlotCount);
			if (combat.Match.IsLive != live)
			{
				live = combat.Match.IsLive;
				rounds += live ? 1 : 0;
			}
		}

		_recordFrames = false;
		int ground = 0;
		int strategist = 0;
		for (int i = 0; i < combat.Economy.NodeCount; i++)
		{
			NodeHolder owner = combat.Economy.NodeAt(i).Capture.Owner;
			ground += owner == NodeHolder.GroundForce ? 1 : 0;
			strategist += owner == NodeHolder.Strategist ? 1 : 0;
		}

		Measured($"{GroundName}: a tick, frame to frame, over {seconds} s of ticks at --fixed-fps 60: {Stats(_frameMs)}");
		Measured($"{GroundName}: the play in those {seconds} s: {rounds} round(s), phase {combat.Match.Phase} at the end; units produced {units.UnitsProduced}, lost {units.UnitsLost},"
			+ $" on the field {units.SlotCount} (most {most}); structures built {units.StructuresBuilt}, lost {units.StructuresLost};"
			+ $" re-bakes {units.NavigationRebakes}; rejected orders {units.RejectedOrders}; ground deaths {combat.Match.GroundDeaths},"
			+ $" tickets {combat.Match.GroundTickets}; nodes held: ground {ground}, strategist {strategist} of {combat.Economy.NodeCount}");
		Fact(units.UnitsProduced > 0, $"{GroundName}: the computer strategist trained units ({units.UnitsProduced})");
	}

	// ---- net: prediction on a slope over an 80 ms link ---------------------------------------------

	private int PeerOf(string role) =>
		int.TryParse(File.ReadAllText(Path.Combine(_dir, $"{role}.peer")).Trim(), out int peer) ? peer : 0;

	/// <summary>The legs a client walks: a name, where it starts, the heading it sets out on and how far before it turns back.</summary>
	private static (string Name, Vector3 Start, Vector3 Heading, float Length)[] Legs(float grade, int run)
	{
		float mid = LevelTerrain.RampWest + (run * 0.5f);
		var flat = ("on the flat", new Vector3(-40f, 0f, LaneZ), Vector3.Right, 36f);
		if (grade <= 0f)
		{
			return new[] { flat };
		}

		return new[]
		{
			flat,
			("up and down the slope", new Vector3(LevelTerrain.RampWest + 3f, 0f, LaneZ), Vector3.Right, run - 6f),
			("across the slope", new Vector3(mid, 0f, 58f), Vector3.Back, 46f),
			("diagonally on the slope", new Vector3(LevelTerrain.RampWest + 3f, 0f, 60f), new Vector3(1f, 0f, 1f).Normalized(), (run - 6f) * 1.4142f),
			("over the crease at its foot", new Vector3(LevelTerrain.RampWest - 6f, 0f, LaneZ), Vector3.Right, 12f),
			("over the crease at its top", new Vector3(LevelTerrain.RampWest + run - 6f, 0f, LaneZ), Vector3.Right, 12f),
		};
	}

	private static void NetRamp(out float grade, out int run)
	{
		grade = 0f;
		run = 0;
		string[] parts = (System.Environment.GetEnvironmentVariable("LE0_TERRAIN") ?? "").Split(':');
		if (parts[0] == "ramp")
		{
			LevelTerrain.Ramp(float.Parse(parts[1], CultureInfo.InvariantCulture),
				parts.Length > 2 ? float.Parse(parts[2], CultureInfo.InvariantCulture) : 8f, false, out grade, out run);
		}
	}

	private IEnumerator<bool> NetServer()
	{
		foreach (bool _ in InGame(wantLocal: false)) yield return true;
		foreach (bool _ in Until(() => Has("client.peer"), 120 * Second)) yield return true;
		Fact(!_timedOut, $"{GroundName}: the client is in");
		if (_timedOut)
		{
			yield break;
		}

		int peer = PeerOf("client");
		NetRamp(out float grade, out int run);
		var legs = Legs(grade, run);
		for (int leg = 0; leg < legs.Length; leg++)
		{
			foreach (bool _ in Until(() => Has($"want-{leg}"), 120 * Second)) yield return true;
			if (_timedOut)
			{
				Fact(false, $"{GroundName}: the client asked for leg {leg}");
				yield break;
			}

			Vector3 start = legs[leg].Start;
			start.Y = GroundAt(start.X, start.Z) + 0.1f;
			CombatManager.Instance.Find(peer).Character.Teleport(new Transform3D(
				new Basis(Vector3.Up, YawFacing(legs[leg].Heading)), start));
			Mark($"placed-{leg}");
		}

		foreach (bool _ in Until(() => Has("finished-net-client"), 300 * Second)) yield return true;
		Fact(!_timedOut, $"{GroundName}: the client walked its legs");
	}

	private IEnumerator<bool> NetClient()
	{
		foreach (bool _ in Until(() => Session.InGame && CombatManager.Instance?.Local?.Character != null
			&& NetworkManager.Instance is { Connected: true }, 60 * Second))
		{
			yield return true;
		}

		Fact(!_timedOut, $"{GroundName}: connected, in the game, with a character");
		if (_timedOut)
		{
			yield break;
		}

		foreach (bool _ in Wait(30)) yield return true;
		PlayerCombat local = CombatManager.Instance.Local;
		CombatManager.Instance.RequestTeam(Team.GroundForce);
		foreach (bool _ in Until(() => local.Team == Team.GroundForce && local.IsAlive, 20 * Second)) yield return true;
		Fact(!_timedOut, $"{GroundName}: alive on the ground force");
		Mark("client.peer", NetworkManager.Instance.LocalPeerId.ToString(CultureInfo.InvariantCulture));

		fps_controller me = local.Character;
		NetStats stats = NetworkManager.Instance.Stats;
		NetRamp(out float grade, out int run);
		var legs = Legs(grade, run);
		int legSeconds = int.TryParse(System.Environment.GetEnvironmentVariable("LE0_LEG_SECONDS"), out int asked) ? asked : 60;

		for (int leg = 0; leg < legs.Length; leg++)
		{
			Mark($"want-{leg}");
			foreach (bool _ in Until(() => Has($"placed-{leg}"), 60 * Second)) yield return true;

			// The teleport is a correction of its own: let it land and settle.
			foreach (bool _ in Wait(2 * Second)) yield return true;
			Vector3 heading = legs[leg].Heading;
			Vector3 origin = me.SimPosition;
			Sampler.Align(YawFacing(heading), 0f);
			foreach (bool _ in Wait(20)) yield return true;

			Input.ActionPress("move_forward");
			foreach (bool _ in Wait(Second)) yield return true;

			// A second at a time, as NetStats publishes: the corrections in it, its mean and
			// worst error, and whether the clock was resynchronised in it or in the two
			// seconds before — which is a correction of the clock's, whatever the ground is.
			SimClock clock = NetworkManager.Instance.Clock;
			long mispredictionsBefore = stats.TotalMispredictions;
			int resyncsBefore = clock.Resyncs;
			int nudgesBefore = clock.Nudges;
			long windowMispredictions = stats.TotalMispredictions;
			int windowResyncs = clock.Resyncs;
			int sinceResync = 3;
			double errorSum = 0;
			float errorMax = 0f;
			int windows = 0;
			double quietErrorSum = 0;
			float quietErrorMax = 0f;
			long quietMispredictions = 0;
			int quietWindows = 0;
			int turns = 0;
			int offFloor = 0;
			bool outward = true;
			int ticks = legSeconds * Second;
			for (int i = 0; i < ticks; i++)
			{
				yield return true;
				offFloor += me.IsOnFloor() ? 0 : 1;
				float along = (me.SimPosition - origin).Dot(heading);
				if ((outward && along >= legs[leg].Length) || (!outward && along <= 0f))
				{
					outward = !outward;
					turns++;
					Sampler.Align(YawFacing(outward ? heading : -heading), 0f);
				}

				if (i % Second == Second - 1)
				{
					errorSum += stats.MeanErrorMeters;
					errorMax = MathF.Max(errorMax, stats.MaxErrorMeters);
					windows++;
					sinceResync = clock.Resyncs != windowResyncs ? 0 : sinceResync + 1;
					if (sinceResync >= 3)
					{
						quietErrorSum += stats.MeanErrorMeters;
						quietErrorMax = MathF.Max(quietErrorMax, stats.MaxErrorMeters);
						quietMispredictions += stats.TotalMispredictions - windowMispredictions;
						quietWindows++;
					}

					windowResyncs = clock.Resyncs;
					windowMispredictions = stats.TotalMispredictions;
				}
			}

			Input.ActionRelease("move_forward");
			long mispredictions = stats.TotalMispredictions - mispredictionsBefore;
			Measured($"{GroundName}, {legs[leg].Name}: {legSeconds} s walking, {turns} turns, {offFloor} ticks off the floor,"
				+ $" {clock.Resyncs - resyncsBefore} clock resyncs and {clock.Nudges - nudgesBefore} nudges:"
				+ $" {F(mispredictions / (float)legSeconds)} mispredictions/s ({mispredictions}),"
				+ $" mean prediction error {F(errorSum / Math.Max(windows, 1) * 1000.0, "0.000")} mm, worst {F(errorMax * 1000f, "0.00")} mm;"
				+ $" in the {quietWindows} s clear of a resync: {F(quietMispredictions / (float)Math.Max(quietWindows, 1))} mispredictions/s ({quietMispredictions}),"
				+ $" mean {F(quietErrorSum / Math.Max(quietWindows, 1) * 1000.0, "0.000")} mm, worst {F(quietErrorMax * 1000f, "0.00")} mm");
			foreach (bool _ in Wait(Second)) yield return true;
		}

		Measured($"{GroundName}: this client's clock: {NetworkManager.Instance.Clock.Resyncs} resyncs, {NetworkManager.Instance.Clock.Nudges} nudges");
	}

	// ---- export: the stripped server ------------------------------------------------------------------

	private IEnumerator<bool> Export()
	{
		foreach (bool _ in InGame(wantLocal: false)) yield return true;
		Fact(!_timedOut, "the exported server is in the game");
		if (_timedOut)
		{
			yield break;
		}

		Fact(OS.HasFeature("dedicated_server") && !OS.HasFeature("editor"), "it is the dedicated-server export, not the editor");
		Fact(Terrain != null && Terrain.Kind == "hills" && Terrain.DecodeMicroseconds > 0,
			"its ground is the heightmap, read from bytes in the pack");
		if (Terrain == null)
		{
			yield break;
		}

		Measured($"export: {LevelTerrain.Size * LevelTerrain.Size * 2} bytes read and decoded in {F(Terrain.DecodeMicroseconds / 1000.0)} ms,"
			+ $" the collider's data set in {F(Terrain.LastSetMicroseconds / 1000.0)} ms");
		foreach (bool _ in Wait(30)) yield return true;
		foreach (bool _ in Rays()) yield return true;

		NavigationMesh mesh = Region().NavigationMesh;
		Fact(UnitManager.Instance.NavigationReady && mesh.GetVertices().Length > 0,
			$"the first tick baked the navigation mesh from the collider: {mesh.GetVertices().Length} vertices, {mesh.GetPolygonCount()} polygons");

		// A mesh is not what it was baked from: the terrain has none, on any build.
		int meshes = 0;
		foreach (Node child in Terrain.GetChildren())
		{
			meshes += child is MeshInstance3D ? 1 : 0;
		}

		Fact(meshes == 0, "the terrain has a collider and no mesh");
	}
}
