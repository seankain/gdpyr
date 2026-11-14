using System.Diagnostics;
using Gdpyr.Rts;
using Gdpyr.Sim.Level;
using Godot;

namespace Gdpyr.MapEditor;

/// <summary>
/// <b>Deep</b> (docs/LEVEL_EDITOR.md §6.9, §8.7, LE5): the validator, and then reach — the
/// navigation mesh baked in the editor as a round bakes it (<see cref="UnitManager.ConfigureBake"/>,
/// from the same colliders: the ground, the round's pieces, the structures' boxes), and a path
/// asked of it from every barracks door and spawn line to every node. A walk that ends short of
/// its node is a warning: the map still plays.
/// </summary>
public partial class MapEditorRoot
{
	/// <summary>How near a path's end must come to its node, flat, to reach it [m].</summary>
	public const float ReachMeters = 3f;

	/// <summary>How near in height [m]: a little more than a unit climbs at once.</summary>
	public const float ReachHeightMeters = 2f;

	/// <summary>Whether a deep check is under way: it takes a bake, and a frame for the map to have it.</summary>
	public bool DeepRunning { get; private set; }

	/// <summary>The last deep check's report; stale once the map has changed (<see cref="DeepIsCurrent"/>).</summary>
	public MapReport DeepReport { get; private set; }

	/// <summary>Whether <see cref="DeepReport"/> is of the map as it is.</summary>
	public bool DeepIsCurrent => DeepReport != null && Editing != null && _deepAt == Editing.Changes;

	/// <summary>What the last deep check baked: vertices, and how long it took [ms].</summary>
	public int DeepVertices { get; private set; }

	public double DeepBakeMilliseconds { get; private set; }

	/// <summary>How many walks the last deep check asked.</summary>
	public int DeepWalks { get; private set; }

	private NavigationRegion3D _navigation;
	private int _deepAt = -1;

	/// <summary>Runs the deep check; its answer is <see cref="DeepReport"/>, and the issues list shows it.</summary>
	public async void Deep()
	{
		if (Editing == null || DeepRunning)
		{
			return;
		}

		DeepRunning = true;
		Say("deep check: baking the navigation mesh…");
		await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
		if (Editing == null)
		{
			DeepRunning = false;
			return;
		}

		var mesh = new NavigationMesh();
		UnitManager.ConfigureBake(mesh, true,
			Terrain.HasWater ? Editing.Document.Bands.WaterLevelMeters - 1f : float.NegativeInfinity);
		_navigation.NavigationMesh = mesh;

		Rid map = GetWorld3D().NavigationMap;
		Rid region = _navigation.GetRid();
		ulong regionWas = NavigationServer3D.RegionGetIterationId(region);
		long started = Stopwatch.GetTimestamp();
		_navigation.BakeNavigationMesh(onThread: false);
		DeepBakeMilliseconds = (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;
		DeepVertices = mesh.GetVertices().Length;

		// The region builds its polygons from the new mesh on a later frame, and the map takes them
		// on a frame after that: asked before, it answers with the old mesh, or none.
		for (int frames = 0; frames < 240 && NavigationServer3D.RegionGetIterationId(region) == regionWas; frames++)
		{
			await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
		}

		ulong mapWas = NavigationServer3D.MapGetIterationId(map);
		for (int frames = 0; frames < 240 && NavigationServer3D.MapGetIterationId(map) == mapWas; frames++)
		{
			await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
		}

		if (Editing == null)
		{
			DeepRunning = false;
			return;
		}

		MapReport report = MapValidator.Validate(Editing.Document);
		DeepWalks = MapValidator.Reach(Editing.Document, (from, to) => Reaches(map, from, to), report);
		DeepReport = report;
		_deepAt = Editing.Changes;
		DeepRunning = false;
		GD.Print($"[editor] deep check: baked {DeepVertices} vertices in {DeepBakeMilliseconds:0} ms, {DeepWalks} walks asked:"
			+ $" {MapStore.Describe(report)}");
		ShowIssues();
		Say($"deep check: {DeepVertices} vertices baked in {DeepBakeMilliseconds:0} ms, {DeepWalks} walks: {MapStore.Describe(report)}");
	}

	/// <summary>
	/// A path asked of the baked mesh from one point to another ends near enough the second, from
	/// near enough the first. One that does not is logged with where it began and ended.
	/// </summary>
	private static bool Reaches(Rid map, Vector3 from, Vector3 to)
	{
		Vector3[] path = NavigationServer3D.MapGetPath(map, from, to, true);
		bool reaches = path.Length > 0 && Near(path[0], from) && Near(path[^1], to);
		if (!reaches)
		{
			GD.Print($"[editor] deep check: no walk from {from} to {to}"
				+ (path.Length == 0 ? ": no path at all" : $": the path runs from {path[0]} to {path[^1]}"));
		}

		return reaches;
	}

	private static bool Near(Vector3 a, Vector3 b)
	{
		Vector3 flat = a - b;
		float height = Mathf.Abs(flat.Y);
		flat.Y = 0f;
		return flat.Length() <= ReachMeters && height <= ReachHeightMeters;
	}
}
