using System.Diagnostics;
using Gdpyr.Core;
using Gdpyr.Sim.Level;
using Godot;

namespace Gdpyr.Level;

/// <summary>
/// The root of <c>Scenes/Maps/MapHost.tscn</c>, the one scene every data map is played in
/// (docs/LEVEL_EDITOR.md §7.1). It builds the world from <see cref="Session.Document"/> —
/// the terrain, then the map's objects — and only then opens the session, as
/// <see cref="WorldRoot"/> does for a scene map: no tick sees half a world.
///
/// After that nothing differs from a scene map. Every autoload finds what it always
/// found, by group and by script, on the first tick (MAPS.md §3); the two things that
/// ask what kind of map this is — the navigation bake's slope and its box (§7.3) — ask
/// <see cref="Current"/>.
/// </summary>
public partial class LevelRoot : WorldRoot
{
	/// <summary>The data map in the tree, or null on a scene map.</summary>
	public static LevelRoot Current { get; private set; }

	/// <summary>What the world was built from.</summary>
	public MapDocument Document { get; private set; }

	public TerrainNode Terrain { get; private set; }

	/// <summary>What <see cref="LevelBuilder"/> put in the world, counted.</summary>
	public LevelBuilder.Summary Built { get; private set; }

	/// <summary>The map's water level [m].</summary>
	public float WaterLevelMeters => Document?.Bands.WaterLevelMeters ?? float.NegativeInfinity;

	/// <summary>Whether any of the ground is under the water: then the water is drawn and the bake has a floor (LE-D8).</summary>
	public bool HasWater => Terrain?.HasWater ?? false;

	/// <summary>
	/// The navigation bake's floor on this map [m] (LE-D8, §7.3): 1 m under the water, so
	/// that units do not path where the bed is deeper than that. Negative infinity on a
	/// map with no ground under its water.
	/// </summary>
	public float BakeFloorMeters => HasWater ? WaterLevelMeters - 1f : float.NegativeInfinity;

	/// <summary>Whether this process draws: not a dedicated server, and not headless (§7.1).</summary>
	public static bool Draws => !Bootstrap.IsDedicatedServer && DisplayServer.GetName() != "headless";

	public override void _EnterTree()
	{
		Current = this;
		Document = Session.Document;
		if (Document == null)
		{
			// MapHost.tscn opened without a map: from the editor, or a demo of a data map,
			// whose playback is LE7's. The session still opens, and every warning a map with
			// nothing in it gives follows.
			GD.PushError("[level] MapHost.tscn was loaded with no map document: nothing to build");
			base._EnterTree();
			return;
		}

		long started = Stopwatch.GetTimestamp();
		bool draws = Draws;
		Node3D geometry = GetNode<Node3D>("Geometry");

		Terrain = new TerrainNode { Name = "Terrain" };
		Terrain.Build(Document, draws);
		geometry.AddChild(Terrain);

		Built = LevelBuilder.Build(this, geometry, Document, Terrain, draws);
		double milliseconds = (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;

		GD.Print($"[level] built '{Document.Name}' ({Session.Map}) in {milliseconds:0} ms:"
			+ $" terrain {Terrain.Width} x {Terrain.Depth} samples, {Terrain.ShapeCount} shapes set in {Terrain.SetMilliseconds:0.0} ms,"
			+ $" {Terrain.MinHeight:0.00} … {Terrain.MaxHeight:0.00} m, water {WaterLevelMeters:0.00} m"
			+ (HasWater ? $" over {Terrain.SubmergedSamples} samples" : ", over none")
			+ (draws ? $", {Terrain.MeshChunks} mesh chunks" : ", not drawn"));
		GD.Print($"[level] {Built}");

		// After, so that nothing ticks on half a world.
		base._EnterTree();
	}

	public override void _ExitTree()
	{
		if (Current == this)
		{
			Current = null;
		}
	}
}
