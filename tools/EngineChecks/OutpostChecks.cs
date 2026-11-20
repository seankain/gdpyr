using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using Gdpyr.Core;
using Gdpyr.Effects;
using Gdpyr.Match;
using Gdpyr.Net;
using Gdpyr.Rts;
using Gdpyr.Sim;
using Gdpyr.Ui;
using Godot;

namespace Gdpyr.EngineChecks;

/// <summary>
/// The outpost in the game (docs/OUTPOST.md), run by <c>scripts/outpost-checks.sh</c> on a
/// copy of the tree with this file added as an autoload; not part of the game.
///
/// A listen host on <c>Test.tscn</c> at <c>--fixed-fps 60</c> with no bots takes the strategist
/// seat and puts an outpost up on open floor. Then: it is drawn with its pad, mast and dish;
/// the strategist's <c>-</c> and <c>=</c> keys queue a scout drone and an operative at it, and
/// both come out of its door; clicked on, its card offers those two and nothing else; the drone
/// climbs to its cruise and holds it with nowhere to go, drawn with four rotors, and the
/// operative is drawn as a body; a smoke grenade thrown on the field goes off into a cloud that
/// the server's sight lines meet and the client draws. Every key is a key event through
/// <c>Input.ParseInputEvent</c>, as a keyboard would send it.
///
/// With <c>OUTPOST_SHOTS</c> naming a directory and a display to draw on, it also saves
/// screenshots there. One line per fact, <c>PASS</c> or <c>FAIL</c>; the process exits with the
/// number that failed.
/// </summary>
public partial class OutpostChecks : Node
{
	private const int Second = SimConfig.TickRate;
	private const float FloorY = 0.5f;

	/// <summary>Open floor on Test.tscn, the minimap check's, where the outpost goes up.</summary>
	private static readonly Vector3 OutpostAt = new(36f, FloorY, 8f);

	/// <summary>Where the smoke grenade is thrown from and towards.</summary>
	private static readonly Vector3 ThrowFrom = new(46f, FloorY + 1.5f, 18f);
	private static readonly Vector3 ThrowAt = new(46f, FloorY, 26f);

	/// <summary>The camera, south of it all and looking north, down at the floor.</summary>
	private static readonly Vector3 CameraAt = new(40f, FloorY + 9f, 36f);
	private static readonly Vector3 LookAt = new(40f, FloorY + 2f, 14f);

	private IEnumerator<bool> _run;
	private int _failures;
	private int _facts;
	private bool _timedOut;
	private string _shots;

	public override void _Ready()
	{
		if (System.Environment.GetEnvironmentVariable("OUTPOST_CHECK") != "listen")
		{
			SetPhysicsProcess(false);
			return;
		}

		_shots = System.Environment.GetEnvironmentVariable("OUTPOST_SHOTS");
		ProcessMode = ProcessModeEnum.Always;
		_run = Listen();
		GD.Print("[checks] outpost check: listen");
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

	/// <summary>A key pressed and let go, by where it is on the keyboard.</summary>
	private static IEnumerable<bool> Press(Key key)
	{
		Input.ParseInputEvent(new InputEventKey { PhysicalKeycode = key, Pressed = true });
		foreach (bool _ in Wait(2)) yield return true;
		Input.ParseInputEvent(new InputEventKey { PhysicalKeycode = key, Pressed = false });
		foreach (bool _ in Wait(2)) yield return true;
	}

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

	private static void SetPrivate(object target, string field, object value) =>
		target.GetType().GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)?.SetValue(target, value);

	private static void CallPrivate(object target, string method, params object[] arguments) =>
		target.GetType().GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)?.Invoke(target, arguments);

	private static Unit FirstOf(UnitManager units, byte kind)
	{
		for (int i = 0; i < units.SlotCount; i++)
		{
			if (units.UnitAt(i) is { IsAlive: true } unit && unit.DefinitionId == kind)
			{
				return unit;
			}
		}

		return null;
	}

	// ---- the check ---------------------------------------------------------------------

	private IEnumerator<bool> Listen()
	{
		while (!Session.InGame || CombatManager.Instance?.Local?.Character == null)
		{
			yield return true;
		}

		if (DisplayServer.GetName() == "headless")
		{
			GetTree().Root.Size = new Vector2I(1280, 720);
		}

		foreach (bool _ in Wait(30)) yield return true;
		CombatManager combat = CombatManager.Instance;
		if (combat.Local.Team != Team.Strategist)
		{
			combat.RequestTeam(Team.Strategist);
		}

		StrategistController controller = null;
		foreach (bool _ in Until(() => (controller = combat.GetNodeOrNull<StrategistController>(
			"StrategistController")) != null, 10 * Second)) yield return true;
		Fact(!_timedOut && combat.Local.Team == Team.Strategist, "the listen host's player is the strategist, with a controller");
		if (controller == null)
		{
			yield break;
		}

		// The camera south of the site, looking north and down at it.
		Vector3 to = LookAt - CameraAt;
		SetPrivate(controller, "_position", CameraAt);
		SetPrivate(controller, "_yaw", 0f);
		SetPrivate(controller, "_pitch", Mathf.Atan2(-to.Y, -to.Z));
		Input.ParseInputEvent(new InputEventMouseMotion { Position = new Vector2(640f, 360f), GlobalPosition = new Vector2(640f, 360f) });

		// ---- the outpost ----
		UnitManager units = UnitManager.Instance;
		combat.Match.Strategist.Add(1000);
		int slot = units.ServerPlaceStructure(StructureKinds.Outpost, OutpostAt, 0f, Team.Strategist, out PlacementResult placed);
		foreach (bool _ in Wait(10)) yield return true;
		Structure outpost = units.StructureAt(slot);
		Fact(slot >= 0 && outpost is { IsBuilt: true, Kind: StructureKinds.Outpost },
			$"an outpost stands finished on the floor (slot {slot}, {placed})");
		if (outpost == null)
		{
			yield break;
		}

		Node model = outpost.GetNodeOrNull("Model");
		Fact(model?.GetNodeOrNull("Dish") != null, "the outpost is drawn with its mast's dish");
		Fact(outpost.TrainedKinds == 2 && outpost.CanTrain(UnitCatalog.ScoutDrone) && outpost.CanTrain(UnitCatalog.Operative)
			&& !outpost.CanTrain(UnitCatalog.Sniper), "it trains the scout drone and the operative, and nothing else");

		// ---- its card ----
		CallPrivate(controller, "SelectStructure", slot);
		foreach (bool _ in Wait(6)) yield return true;
		var hud = controller.GetNode<StrategistHud>("StrategistHud");
		Button[] buttons = Private<Button[]>(hud, "_cardButtons");
		var offered = new List<string>();
		foreach (Button button in buttons ?? Array.Empty<Button>())
		{
			if (button.Visible)
			{
				offered.Add(button.Text.Replace('\n', ' '));
			}
		}

		Measured($"the outpost's card offers: {string.Join(" | ", offered)}");
		Fact(offered.Count == 2 && offered[0].StartsWith("Scout Drone (-)", StringComparison.Ordinal)
			&& offered[1].StartsWith("Operative (=)", StringComparison.Ordinal),
			"clicked on, its card offers the scout drone on - and the operative on =");
		Shot("outpost-1-card");

		// ---- the keys ----
		foreach (bool _ in Press(Key.Minus)) yield return true;
		foreach (bool _ in Press(Key.Equal)) yield return true;
		foreach (bool _ in Wait(4)) yield return true;
		Fact(outpost.Queue.Count == 2 && outpost.Queue.Head == UnitCatalog.ScoutDrone,
			$"- and = queue a scout drone and an operative there ({outpost.Queue.Count} queued, head {outpost.Queue.Head})");

		Unit drone = null;
		Unit operative = null;
		foreach (bool _ in Until(() => (drone = FirstOf(units, UnitCatalog.ScoutDrone)) != null
			&& (operative = FirstOf(units, UnitCatalog.Operative)) != null, 20 * Second)) yield return true;
		Fact(drone != null && operative != null, "both come out of its door");
		if (drone == null || operative == null)
		{
			yield break;
		}

		CallPrivate(controller, "SelectStructure", -1);

		// ---- the drone: up, and held there ----
		foreach (bool _ in Until(() => drone.Phase == FlightPhase.Cruising && Mathf.Abs(drone.HeightAboveGround - 12f) < 0.3f,
			10 * Second)) yield return true;
		foreach (bool _ in Wait(3 * Second)) yield return true;
		Measured($"the drone at {F(drone.HeightAboveGround)} m, {drone.Phase}, at ({F(drone.GlobalPosition.X)}, {F(drone.GlobalPosition.Z)})");
		Fact(drone.Phase == FlightPhase.Cruising && Mathf.Abs(drone.HeightAboveGround - 12f) < 0.5f,
			"the scout drone climbs to its 12 m and stays up there with nowhere to go");

		Node airframe = drone.GetNodeOrNull("Airframe");
		int rotors = 0;
		for (int i = 0; airframe != null && i < 4; i++)
		{
			rotors += airframe.GetNodeOrNull($"Rotor{i}") != null ? 1 : 0;
		}

		Fact(rotors == 4, $"the scout drone is drawn as a quadcopter ({rotors} rotors)");
		Fact(operative.GetNodeOrNull("Body") != null, "the operative is drawn as a body, as anything on foot is");
		Shot("outpost-2-trained");

		// ---- smoke ----
		int effectsBefore = ExplosionEffects.Instance?.Played ?? 0;
		ProjectileStats round = WeaponCatalog.RoundFor(WeaponCatalog.SmokeGrenade);
		bool solved = GrenadeSim.TrySolve(ThrowFrom, ThrowAt, round, out Vector3 aim);
		uint tick = NetworkManager.Instance?.Tick ?? 0;
		combat.SpawnProjectile(tick, OwnerId.ForUnit(operative.UnitId), WeaponCatalog.SmokeGrenade, ThrowFrom, aim, 0);
		foreach (bool _ in Until(() => combat.Smoke.Count > 0, 5 * Second)) yield return true;
		Fact(solved && combat.Smoke.Count == 1, "a smoke grenade thrown on the field goes off into one cloud");
		if (combat.Smoke.Count == 0)
		{
			yield break;
		}

		SmokeCloud cloud = combat.Smoke.At(0);
		Measured($"the cloud went down at ({F(cloud.Ground.X)}, {F(cloud.Ground.Y)}, {F(cloud.Ground.Z)})");
		Fact((ExplosionEffects.Instance?.Played ?? 0) > effectsBefore, "and the client draws it");

		foreach (bool _ in Wait(2 * Second)) yield return true;
		Vector3 centre = cloud.Centre;
		Fact(combat.Smoke.Blocks(centre + new Vector3(0f, 0.3f, -15f), centre + new Vector3(0f, 0.3f, 15f)),
			"a sight line through the cloud is stopped by it");
		Fact(!combat.Smoke.Blocks(centre + new Vector3(10f, 0.3f, -15f), centre + new Vector3(10f, 0.3f, 15f)),
			"and one ten metres beside it is not");
		Shot("outpost-3-smoke");

		foreach (bool _ in Wait(10)) yield return true;
	}
}
