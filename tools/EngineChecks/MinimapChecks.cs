using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using Gdpyr.Core;
using Gdpyr.Fps;
using Gdpyr.Match;
using Gdpyr.Net;
using Gdpyr.Rts;
using Gdpyr.Sim;
using Gdpyr.Ui;
using Godot;

namespace Gdpyr.EngineChecks;

/// <summary>
/// The strategist's map in the game (Scripts/Ui/Minimap.cs, Scripts/Ui/MinimapView.cs), run by
/// <c>scripts/minimap-checks.sh</c> on a copy of the tree with this file added as an autoload;
/// not part of the game.
///
/// A listen host on <c>Test.tscn</c> at <c>--fixed-fps 60</c> with no bots takes the strategist
/// seat. Then: the map is in the bottom-right corner and the command card beside it, not on it;
/// the picture of the ground is taken; the side's units on foot, vehicles, barracks, finished
/// structures and sites are drawn on it; a left click on the map puts the camera over that point
/// looking straight down, and a drag across it carries the camera along; the middle button and
/// the wheel over the map zoom the map about the pointer and leave the camera where it is, while
/// the wheel off the map still moves the camera; a click off the map is not a jump; zoomed in,
/// the map follows a camera flown off it. Every input is a mouse event through
/// <c>Input.ParseInputEvent</c>, as a mouse would send it.
///
/// With <c>MINIMAP_SHOTS</c> naming a directory and a display to draw on, it also saves
/// screenshots there. One line per fact, <c>PASS</c> or <c>FAIL</c>; the process exits with the
/// number that failed.
/// </summary>
public partial class MinimapChecks : Node
{
	private const int Second = SimConfig.TickRate;
	private const float FloorY = 0.5f;

	/// <summary>Open floor (the mortar check's site and the 60 m in front of it), where the side's things are put.</summary>
	private static readonly Vector3 RiflemanAt = new(36f, FloorY, 40f);
	private static readonly Vector3 TankAt = new(46f, FloorY, 28f);
	private static readonly Vector3 BuilderAt = new(40f, FloorY, 18f);
	private static readonly Vector3 PillboxAt = new(30f, FloorY, 2f);
	private static readonly Vector3 SiteAt = new(50f, FloorY, 8f);

	private IEnumerator<bool> _run;
	private int _failures;
	private int _facts;
	private bool _timedOut;
	private string _shots;

	public override void _Ready()
	{
		if (System.Environment.GetEnvironmentVariable("MINIMAP_CHECK") != "listen")
		{
			SetPhysicsProcess(false);
			return;
		}

		_shots = System.Environment.GetEnvironmentVariable("MINIMAP_SHOTS");
		ProcessMode = ProcessModeEnum.Always;
		_run = Listen();
		GD.Print("[checks] minimap check: listen");
	}

	public override void _PhysicsProcess(double delta)
	{
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
			GD.Print($"[checks] listen: {_facts - _failures} of {_facts} facts hold");
			SetPhysicsProcess(false);
			GetTree().Quit(_failures);
		}
	}

	// ---- plumbing ----------------------------------------------------------------------

	private void Fact(bool ok, string what)
	{
		_facts++;
		if (!ok)
		{
			_failures++;
		}

		GD.Print($"[checks] {(ok ? "PASS" : "FAIL")} listen: {what}");
	}

	private static void Measured(string what) => GD.Print($"[checks] measured listen: {what}");

	private static string F(float v, string format = "0.00") => v.ToString(format, CultureInfo.InvariantCulture);

	private static string V(Vector3 v) => $"({F(v.X)}, {F(v.Y)}, {F(v.Z)})";

	private static string V(Vector2 v) => $"({F(v.X)}, {F(v.Y)})";

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

	/// <summary>The pointer to <paramref name="at"/>, window pixels, with <paramref name="held"/> down.</summary>
	private static void Move(Vector2 at, MouseButtonMask held = 0)
	{
		Input.ParseInputEvent(new InputEventMouseMotion { Position = at, GlobalPosition = at, ButtonMask = held });
	}

	private static void Button(MouseButton button, Vector2 at, bool pressed)
	{
		Input.ParseInputEvent(new InputEventMouseButton
		{
			ButtonIndex = button,
			Pressed = pressed,
			Position = at,
			GlobalPosition = at,
			Factor = 1f,
			ButtonMask = pressed ? MouseButtonMaskOf(button) : 0,
		});
	}

	private static MouseButtonMask MouseButtonMaskOf(MouseButton button) => button switch
	{
		MouseButton.Left => MouseButtonMask.Left,
		MouseButton.Right => MouseButtonMask.Right,
		MouseButton.Middle => MouseButtonMask.Middle,
		_ => 0,
	};

	/// <summary>A press and a release where the pointer is, as a click.</summary>
	private static IEnumerable<bool> Click(MouseButton button, Vector2 at)
	{
		Move(at);
		foreach (bool _ in Wait(2)) yield return true;
		Button(button, at, true);
		foreach (bool _ in Wait(2)) yield return true;
		Button(button, at, false);
		foreach (bool _ in Wait(3)) yield return true;
	}

	/// <summary>Saves what the screen shows as <paramref name="name"/>.png, when there is a display and somewhere to put it.</summary>
	private void Shot(string name)
	{
		if (string.IsNullOrEmpty(_shots) || DisplayServer.GetName() == "headless")
		{
			return;
		}

		Image image = GetViewport().GetTexture()?.GetImage();
		if (image == null || image.IsEmpty())
		{
			Measured($"no picture for {name}");
			return;
		}

		string path = System.IO.Path.Combine(_shots, $"{name}.png");
		image.SavePng(path);
		Measured($"saved {path}");
	}

	private static T Private<T>(object target, string field) =>
		(T)target.GetType().GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(target);

	private static float XzDistance(Vector3 a, Vector2 b) => new Vector2(a.X, a.Z).DistanceTo(b);

	// ---- the check ---------------------------------------------------------------------

	private IEnumerator<bool> Listen()
	{
		while (!Session.InGame || CombatManager.Instance?.Local?.Character == null)
		{
			yield return true;
		}

		// Headless, the window is 64 x 64 whatever --resolution says: too small for a map 208 px
		// across with world beside it to click on.
		if (DisplayServer.GetName() == "headless")
		{
			GetTree().Root.Size = new Vector2I(1280, 720);
		}

		foreach (bool _ in Wait(30)) yield return true;
		PlayerCombat local = CombatManager.Instance.Local;
		if (local.Team != Team.Strategist)
		{
			CombatManager.Instance.RequestTeam(Team.Strategist);
		}

		StrategistController controller = null;
		foreach (bool _ in Until(() => (controller = CombatManager.Instance.GetNodeOrNull<StrategistController>(
			"StrategistController")) != null, 10 * Second)) yield return true;
		Fact(!_timedOut && local.Team == Team.Strategist, "the listen host's player is the strategist, with a controller");
		if (controller == null)
		{
			yield break;
		}

		var hud = controller.GetNode<StrategistHud>("StrategistHud");
		var camera = controller.GetNode<Camera3D>("StrategistCamera");
		Minimap map = hud.Minimap;
		Fact(map != null, $"the HUD has a map ({map?.GetPath()})");
		if (map == null)
		{
			yield break;
		}

		// The pointer off the screen's edges, or it scrolls the camera on its own.
		Vector2 screen = GetViewport().GetVisibleRect().Size;
		Fact(screen.X >= 640f && screen.Y >= 400f,
			$"the window is big enough to hold the map and the world beside it ({V(screen)})");
		Vector2 middle = screen * 0.5f;
		Move(middle);
		foreach (bool _ in Wait(5)) yield return true;

		// ---- where it is ----
		Rect2 rect = map.GetGlobalRect();
		Measured($"window {V(screen)}, map {V(rect.Position)} + {V(rect.Size)}");
		Fact(Mathf.IsEqualApprox(rect.Size.X, Minimap.SizePixels) && Mathf.IsEqualApprox(rect.Size.Y, Minimap.SizePixels),
			$"the map is {F(Minimap.SizePixels, "0")} px square ({V(rect.Size)})");
		Fact(Mathf.IsEqualApprox(rect.End.X, screen.X - 16f) && Mathf.IsEqualApprox(rect.End.Y, screen.Y - 16f),
			$"the map is in the bottom-right corner, 16 px in ({V(rect.End)} of {V(screen)})");

		// ---- the picture of the ground ----
		int frames = 0;
		foreach (bool _ in Until(() => { frames++; return map.HasTerrain; }, 5 * Second)) yield return true;
		Fact(map.HasTerrain, $"the picture of the ground is taken, in {frames} ticks");
		if (map.HasTerrain)
		{
			var texture = Private<ImageTexture>(map, "_terrain");
			Image image = texture?.GetImage();
			int ground = 0;
			int cells = 0;
			Color empty = MinimapTerrain.Empty;
			for (int y = 0; image != null && y < image.GetHeight(); y += 4)
			{
				for (int x = 0; x < image.GetWidth(); x += 4)
				{
					cells++;
					Color pixel = image.GetPixel(x, y);
					ground += pixel.R8 != empty.R8 || pixel.G8 != empty.G8 || pixel.B8 != empty.B8 ? 1 : 0;
				}
			}

			Measured($"picture {image?.GetWidth()} x {image?.GetHeight()}, ground under {ground} of {cells} sampled cells");
			Fact(image != null && ground > cells / 4, "the picture has ground in it, not only background");
		}

		// What the picture costs: one frame's rays, against this world, timed here.
		PhysicsDirectSpaceState3D space = GetViewport().FindWorld3D().DirectSpaceState;
		var ray = new PhysicsRayQueryParameters3D { CollisionMask = CollisionLayers.World };
		var clock = System.Diagnostics.Stopwatch.StartNew();
		for (int i = 0; i < 480; i++)
		{
			ray.From = new Vector3(-118f + (i % 160 * 1.5f), 200f, -60f + (i / 160 * 1.5f));
			ray.To = ray.From - new Vector3(0f, 300f, 0f);
			space.IntersectRay(ray);
		}

		Measured($"480 rays, one frame's share of the picture: {F((float)clock.Elapsed.TotalMilliseconds, "0.000")} ms");

		// ---- markers ----
		UnitManager units = UnitManager.Instance;
		ushort rifleman = units.ServerPlaceUnit(UnitCatalog.Infantry, RiflemanAt, Team.Strategist, OrderKind.None);
		ushort tank = units.ServerPlaceUnit(UnitCatalog.Tank, TankAt, Team.Strategist, OrderKind.None);
		ushort builder = units.ServerPlaceUnit(UnitCatalog.Builder, BuilderAt, Team.Strategist, OrderKind.None);
		int pillbox = units.ServerPlaceStructure(StructureKinds.Pillbox, PillboxAt, 0f, Team.Strategist,
			out PlacementResult placed);
		Measured($"placed rifleman {rifleman}, tank {tank}, builder {builder}, pillbox slot {pillbox} ({placed})");
		foreach (bool _ in Wait(10)) yield return true;

		// A site: the builder sent to put a sandbag wall up, through the strategist's own request.
		CombatManager.Instance.Match.Strategist.Add(500);
		units.RequestConstruct(new[] { (int)builder }, StructureKinds.SandbagWall, SiteAt, 0f);
		foreach (bool _ in Until(() => map.Drawn.Sites > 0, 3 * Second)) yield return true;

		// The sites here are Test.tscn's open floor; on another map (MAP=) they may be in the way
		// of something, and a refused site says nothing about the map.
		bool siteRefused = units.LastPlacement != PlacementResult.Ok;
		if (siteRefused)
		{
			Measured($"the sandbag wall's site was refused here: {StructurePlacement.Describe(units.LastPlacement)}");
		}

		var drawn = map.Drawn;
		Measured($"drawn: {drawn.Units} on foot, {drawn.Vehicles} vehicles, {drawn.Barracks} barracks,"
			+ $" {drawn.Structures} structures, {drawn.Sites} sites");
		Fact(drawn.Units >= 2, "units on foot are drawn (the rifleman and the builder)");
		Fact(drawn.Vehicles >= 1, "vehicles are drawn (the tank)");
		Fact(drawn.Barracks >= 1, "the barracks is drawn");
		Fact(drawn.Structures >= 1, "a finished structure is drawn (the pillbox)");
		if (!siteRefused)
		{
			Fact(drawn.Sites >= 1, "a structure going up is drawn (the sandbag wall's site)");
		}
		Shot("minimap-1-whole-field");

		// ---- a click: the camera straight over that point, looking straight down ----
		Vector3 before = camera.GlobalPosition;
		var target = new Vector2(TankAt.X, TankAt.Z);
		Vector2 onMap = rect.Position + map.View.ToMap(target, map.MapSize);
		foreach (bool _ in Click(MouseButton.Left, onMap)) yield return true;

		float metresPerPixel = 1f / map.View.PixelsPerMeter(map.MapSize);
		Vector3 forward = -camera.GlobalBasis.Z;
		Measured($"clicked {V(onMap)} for {V(target)}: camera {V(before)} -> {V(camera.GlobalPosition)}, forward {V(forward)}");
		Fact(XzDistance(camera.GlobalPosition, target) <= metresPerPixel,
			$"a click on the map puts the camera over that point, within a pixel's {F(metresPerPixel)} m"
			+ $" ({F(XzDistance(camera.GlobalPosition, target))} m off)");
		Fact(forward.Dot(Vector3.Down) > 0.9999f, $"looking straight down (forward {V(forward)})");
		Fact(camera.GlobalPosition.Y >= before.Y - 0.01f, "at the height it was, or lifted clear of the ground");
		Fact(map.Drawn.Units + map.Drawn.Vehicles > 0, "and the units are still drawn");
		Shot("minimap-2-jump-to-tank");

		// ---- a drag: the camera follows the pointer across the map ----
		var from = new Vector2(-60f, 60f);
		var to = new Vector2(70f, -50f);
		Vector2 fromPx = rect.Position + map.View.ToMap(from, map.MapSize);
		Vector2 toPx = rect.Position + map.View.ToMap(to, map.MapSize);
		Move(fromPx);
		foreach (bool _ in Wait(2)) yield return true;
		Button(MouseButton.Left, fromPx, true);
		foreach (bool _ in Wait(3)) yield return true;
		Fact(XzDistance(camera.GlobalPosition, from) <= metresPerPixel, "pressing on the map moves the camera at once");
		for (int step = 1; step <= 8; step++)
		{
			Move(fromPx.Lerp(toPx, step / 8f), MouseButtonMask.Left);
			foreach (bool _ in Wait(1)) yield return true;
		}

		foreach (bool _ in Wait(2)) yield return true;
		Fact(XzDistance(camera.GlobalPosition, to) <= metresPerPixel,
			$"dragging carries it along ({V(camera.GlobalPosition)} for {V(to)})");
		Button(MouseButton.Left, toPx, false);
		foreach (bool _ in Wait(3)) yield return true;
		Fact(!Private<bool>(controller, "_mapHeld"), "and the release lets go");

		// ---- the zoom input: the middle button over the map ----
		Vector2 pointer = rect.Position + new Vector2(150f, 60f);
		Vector2 under = map.WorldAt(pointer - rect.Position);
		before = camera.GlobalPosition;
		foreach (bool _ in Click(MouseButton.Middle, pointer)) yield return true;
		Fact(map.View.ZoomIndex == 1, $"the middle button over the map zooms it in (x{F(map.View.Zoom, "0")})");
		Fact(map.WorldAt(pointer - rect.Position).DistanceTo(under) < 0.05f,
			$"about the point under the pointer ({V(under)} -> {V(map.WorldAt(pointer - rect.Position))})");
		Fact(camera.GlobalPosition.DistanceTo(before) < 0.01f, "and the camera is where it was");
		foreach (bool _ in Click(MouseButton.Middle, pointer)) yield return true;
		Fact(map.View.ZoomIndex == 2, $"again, further in (x{F(map.View.Zoom, "0")})");
		Shot("minimap-3-zoomed-x4");
		foreach (bool _ in Click(MouseButton.Middle, pointer)) yield return true;
		Fact(map.View.ZoomIndex == 0 && map.View.Center == Vector2.Zero, "and again, back out to the whole field");

		// ---- the wheel over the map zooms the map, not the camera ----
		before = camera.GlobalPosition;
		foreach (bool _ in Click(MouseButton.WheelUp, pointer)) yield return true;
		Fact(map.View.ZoomIndex == 1 && camera.GlobalPosition.DistanceTo(before) < 0.01f,
			$"the wheel over the map zooms the map in and leaves the camera (x{F(map.View.Zoom, "0")},"
			+ $" camera moved {F(camera.GlobalPosition.DistanceTo(before))} m)");
		foreach (bool _ in Click(MouseButton.WheelDown, pointer)) yield return true;
		Fact(map.View.ZoomIndex == 0, "and out");

		// ---- off the map, the wheel is the camera's and a click is not a jump ----
		before = camera.GlobalPosition;
		foreach (bool _ in Click(MouseButton.WheelUp, middle)) yield return true;
		Fact(map.View.ZoomIndex == 0 && camera.GlobalPosition.DistanceTo(before) > 1f,
			$"the wheel off the map still moves the camera ({F(camera.GlobalPosition.DistanceTo(before))} m)"
			+ " and not the map");
		before = camera.GlobalPosition;
		foreach (bool _ in Click(MouseButton.Left, middle)) yield return true;
		Fact(camera.GlobalPosition.DistanceTo(before) < 0.01f, "a click off the map does not move the camera");

		// A box select dragged from the world and let go over the map is a box select: it ends,
		// and the camera does not jump to where it was let go.
		Vector2 overMap = rect.Position + (map.MapSize * 0.5f);
		before = camera.GlobalPosition;
		Button(MouseButton.Left, middle, true);
		foreach (bool _ in Wait(2)) yield return true;
		for (int step = 1; step <= 6; step++)
		{
			Move(middle.Lerp(overMap, step / 6f), MouseButtonMask.Left);
			foreach (bool _ in Wait(1)) yield return true;
		}

		Button(MouseButton.Left, overMap, false);
		foreach (bool _ in Wait(3)) yield return true;
		Fact(!Private<bool>(controller, "_dragging") && !Private<bool>(controller, "_mapHeld")
			&& camera.GlobalPosition.DistanceTo(before) < 0.01f,
			"a box select let go over the map ends as a box select, and the camera stays");
		Move(middle);
		foreach (bool _ in Wait(2)) yield return true;

		// ---- zoomed in, the map follows a camera flown off it ----
		foreach (bool _ in Click(MouseButton.Left, rect.Position + map.View.ToMap(Vector2.Zero, map.MapSize))) yield return true;
		for (int i = 0; i < MinimapView.Zooms.Length && map.View.ZoomIndex != MinimapView.Zooms.Length - 1; i++)
		{
			foreach (bool _ in Click(MouseButton.Middle, rect.Position + (map.MapSize * 0.5f))) yield return true;
		}

		Vector2 centerBefore = map.View.Center;
		Move(middle);
		Input.ActionPress("move_right");
		foreach (bool _ in Wait(Second * 3 / 4)) yield return true;
		Input.ActionRelease("move_right");
		foreach (bool _ in Wait(3)) yield return true;
		var focus = new Vector2(camera.GlobalPosition.X, camera.GlobalPosition.Z);
		Measured($"flew to {V(focus)}; map centre {V(centerBefore)} -> {V(map.View.Center)} at x{F(map.View.Zoom, "0")},"
			+ $" showing {F(map.View.HalfSpan)} m each way");
		Fact(map.View.ZoomIndex == MinimapView.Zooms.Length - 1 && map.View.Shows(focus)
			&& map.View.Center != centerBefore && Mathf.Abs(focus.X - centerBefore.X) > map.View.HalfSpan,
			"zoomed in, the map follows the camera off it");

		// ---- the card beside the map, not on it ----
		controller.GetType().GetMethod("SelectBarracks", BindingFlags.NonPublic | BindingFlags.Instance)
			?.Invoke(controller, new object[] { 0 });
		foreach (bool _ in Wait(4)) yield return true;
		var card = Private<Control>(hud, "_card");
		Rect2 cardRect = card?.GetGlobalRect() ?? default;
		Measured($"barracks card {V(cardRect.Position)} + {V(cardRect.Size)}, visible {card?.Visible}");
		Fact(card is { Visible: true } && !cardRect.Intersects(rect)
			&& Mathf.IsEqualApprox(cardRect.End.X, rect.Position.X - 8f),
			"the command card stands just left of the map, clear of it");
		Shot("minimap-4-card-beside-map");
	}
}
