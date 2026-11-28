using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Gdpyr.Core;
using Gdpyr.Dev;
using Gdpyr.Fps;
using Gdpyr.Match;
using Gdpyr.Net;
using Gdpyr.Rts;
using Gdpyr.Sim;
using Godot;

namespace Gdpyr.EngineChecks;

/// <summary>
/// The unit stress test in the game (Scenes/Dev/UnitStressTest.tscn, docs/UNIT_STRESS_TEST.md),
/// run by <c>scripts/unit-stress-checks.sh</c> on a copy of the tree with this file added as an
/// autoload; not part of the game.
///
/// The scene is run on its own, as the editor's Run Current Scene runs it, at
/// <c>--fixed-fps 60</c>. Then: it is an offline round on its own map, with no bots; the local
/// player is put on the ground force without being asked; the navigation is baked; the locker
/// and the kiosk are there, both of the kiosk's pads pass their check and the kiosk hands out
/// each kind; F10 opens and closes the panel and the pointer follows it; a wave is put round
/// the barracks, spaced, told to hunt the player, and closes on them; a wave bigger than the
/// field stops at its limit; Clear empties it; Keep alive tops it up after units die; and a
/// ramp steps up to the limit and stops there.
///
/// With <c>UNIT_STRESS_SHOTS</c> naming a directory and a display to draw on, it also saves
/// screenshots there. One line per fact, <c>PASS</c> or <c>FAIL</c>; the process exits with the
/// number that failed.
/// </summary>
public partial class UnitStressChecks : Node
{
	private const int Second = SimConfig.TickRate;

	private IEnumerator<bool> _run;
	private int _failures;
	private int _facts;
	private bool _timedOut;
	private string _shots;

	public override void _Ready()
	{
		if (System.Environment.GetEnvironmentVariable("UNIT_STRESS_CHECK") != "1")
		{
			SetPhysicsProcess(false);
			return;
		}

		_shots = System.Environment.GetEnvironmentVariable("UNIT_STRESS_SHOTS");
		ProcessMode = ProcessModeEnum.Always;
		_run = Run();
		GD.Print("[checks] unit stress check");
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
			GD.Print($"[checks] unit stress: {_facts - _failures} of {_facts} facts hold");
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

		GD.Print($"[checks] {(ok ? "PASS" : "FAIL")} unit stress: {what}");
	}

	private static void Measured(string what) => GD.Print($"[checks] measured unit stress: {what}");

	private static string F(float v, string format = "0.00") => v.ToString(format, CultureInfo.InvariantCulture);

	private static string V(Vector3 v) => $"({F(v.X)}, {F(v.Y)}, {F(v.Z)})";

	private static float Flat(Vector3 a, Vector3 b) => new Vector2(a.X - b.X, a.Z - b.Z).Length();

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

	private static void Key(Key key)
	{
		Input.ParseInputEvent(new InputEventKey { Keycode = key, PhysicalKeycode = key, Pressed = true });
		Input.ParseInputEvent(new InputEventKey { Keycode = key, PhysicalKeycode = key, Pressed = false });
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

	private static List<Unit> Alive()
	{
		var alive = new List<Unit>();
		UnitManager units = UnitManager.Instance;
		for (int i = 0; i < units.SlotCount; i++)
		{
			if (units.UnitAt(i) is { IsAlive: true } unit)
			{
				alive.Add(unit);
			}
		}

		return alive;
	}

	/// <summary>The body half a metre in front of the kiosk's cabinet, facing it.</summary>
	private static void AtKiosk(fps_controller me)
	{
		VehicleKiosk kiosk = CrewVehicleManager.Instance.Kiosk;
		Vector3 front = kiosk.GlobalBasis.Z;
		Vector3 at = kiosk.GlobalPosition + (front * ((LockerSupply.CabinetDepthMeters * 0.5f) + 0.5f)) + (Vector3.Up * 0.1f);
		me.Teleport(new Transform3D(new Basis(Vector3.Up, Mathf.Atan2(-front.X, -front.Z)), at));
	}

	private static int KioskVehicles(byte kind)
	{
		int count = 0;
		CrewVehicleManager crew = CrewVehicleManager.Instance;
		for (int slot = 0; slot < SimConfig.MaxCrewVehicles; slot++)
		{
			if (crew.VehicleAt(slot) is { FromKiosk: true } vehicle && (byte)vehicle.Kind == kind)
			{
				count++;
			}
		}

		return count;
	}

	// ---- the check ---------------------------------------------------------------------

	private IEnumerator<bool> Run()
	{
		foreach (bool _ in Until(() => Session.InGame && CombatManager.Instance?.Local?.Character != null, 20 * Second)) yield return true;
		Fact(!_timedOut, "the scene, run on its own, is a round with a local player");
		if (_timedOut)
		{
			yield break;
		}

		// Headless, the window is 64 x 64 whatever --resolution says.
		if (DisplayServer.GetName() == "headless")
		{
			GetTree().Root.Size = new Vector2I(1280, 720);
		}

		var stress = GetTree().CurrentScene.GetNodeOrNull<UnitStressTest>("UnitStress");
		Fact(stress != null, "the scene has its stress node");
		if (stress == null)
		{
			yield break;
		}

		NetworkManager net = NetworkManager.Instance;
		Fact(net.HasStarted && net.IsOffline && stress.DisabledReason == null,
			$"it started the network offline, as the authority ({net.Mode}, {stress.DisabledReason ?? "enabled"})");
		Fact(Session.Map.Id == UnitStressTest.MapId && Session.WorldScenePath == UnitStressTest.ScenePath,
			$"the session's map is the scene ({Session.Map.Id}, {Session.WorldScenePath})");
		Fact(!MapCatalog.TryFind(UnitStressTest.MapId, out _), "it is not in the host panel's catalog");

		PlayerCombat local = CombatManager.Instance.Local;
		foreach (bool _ in Until(() => local.Team == Team.GroundForce && local.IsAlive
			&& !CombatManager.Instance.LocalAwaitingRole, 10 * Second)) yield return true;
		Fact(!_timedOut, $"the local player is on the ground force, alive, without picking a side ({local.Team})");

		var bots = PlayerManager.Instance.Bots;
		Fact(bots != null && bots.GroundTarget == 0 && bots.StrategistTarget == 0,
			$"no bots on either side ({bots?.GroundTarget}:{bots?.StrategistTarget})");
		foreach (bool _ in Until(() => CombatManager.Instance.Match.Phase == RoundPhase.Live, 5 * Second)) yield return true;
		Fact(CombatManager.Instance.Match.Phase == RoundPhase.Live && CombatManager.Instance.Match.GroundTickets == 999,
			$"the round is live with 999 tickets ({CombatManager.Instance.Match.Phase}, {CombatManager.Instance.Match.GroundTickets})");
		Fact(CombatManager.Instance.GameMode.RoundDurationMinutes == stress.RoundMinutes,
			$"the round is {stress.RoundMinutes} minutes ({CombatManager.Instance.GameMode.RoundDurationMinutes})");

		UnitManager units = UnitManager.Instance;
		foreach (bool _ in Until(() => units.NavigationReady, 5 * Second)) yield return true;
		Fact(units.NavigationReady, "the navigation mesh is baked");

		// The cover is in the bake: the mesh's nearest point to a low wall's middle is off its
		// face by about the rifleman's radius. Run as the startup scene, a CSG shape has no
		// geometry on the tick the bake runs unless the scene builds it first, and the ground
		// under the wall is then walkable. (A low wall, not a block: the bake rasterizes surfaces,
		// so the inside of a 3 m block is 3 m of clearance and bakes as an island of its own.)
		// The map takes the baked mesh in at the end of a physics frame; until then it answers
		// every query with the origin.
		Rid navigation = GetViewport().FindWorld3D().NavigationMap;
		var wall = GetTree().CurrentScene.GetNode<CsgBox3D>("Geometry/Cover/CoverLow04");
		Vector3 middle = new(wall.GlobalPosition.X, 0f, wall.GlobalPosition.Z);
		Vector3 nearestOnMesh = Vector3.Zero;
		foreach (bool _ in Until(() => (nearestOnMesh = NavigationServer3D.MapGetClosestPoint(navigation, middle)) != Vector3.Zero,
			Second)) yield return true;
		float offFace = MathF.Abs(nearestOnMesh.Z - middle.Z) - (wall.Size.Z * 0.5f);
		Measured($"the mesh's nearest point to {wall.Name}'s middle {V(middle)} is {V(nearestOnMesh)}");
		Fact(!_timedOut && offFace >= 0.3f && offFace <= 1f,
			$"the cover is carved out of the navigation mesh, by about the rifleman's radius ({F(offFace)} m off the wall's face)");
		Fact(units.BarracksCount == 1 && units.BarracksAt(0).DefendedRadiusMeters == 0f,
			$"one barracks, with no guns ({units.BarracksCount}, defended {F(units.BarracksAt(0)?.DefendedRadiusMeters ?? -1f)} m)");

		// ---- the locker and the kiosk ----
		var lockers = GetTree().GetNodesInGroup(WeaponLocker.Group).OfType<WeaponLocker>().ToList();
		Fact(lockers.Count == 1 && lockers[0].IsUpright, $"one weapon locker, upright ({lockers.Count})");

		CrewVehicleManager crew = CrewVehicleManager.Instance;
		foreach (bool _ in Until(() => crew.PadsChecked, 3 * Second)) yield return true;
		Fact(crew.HasKiosk && crew.PadsChecked && crew.PadFailure(0) == null && crew.PadFailure(1) == null,
			$"the vehicle kiosk is there and both pads pass ({crew.PadFailure(0) ?? "clear"}, {crew.PadFailure(1) ?? "clear"})");

		fps_controller me = local.Character;
		for (byte kind = 0; kind < 2; kind++)
		{
			AtKiosk(me);
			foreach (bool _ in Wait(Second / 2)) yield return true;
			crew.RequestVehicle(kind, out KioskRefusal refusal);
			byte asked = kind;
			foreach (bool _ in Until(() => KioskVehicles(asked) == 1, 2 * Second)) yield return true;
			Fact(refusal == KioskRefusal.None && KioskVehicles(kind) == 1,
				$"the kiosk hands out a {(kind == 0 ? "4x4" : "helicopter")} ({refusal})");
		}

		me.Teleport(new Transform3D(Basis.Identity, PlayerManager.Instance.SpawnCentroid + (Vector3.Up * 0.1f)));
		foreach (bool _ in Wait(Second)) yield return true;
		Shot("unit-stress-1-spawn");

		// ---- the panel ----
		var panel = stress.GetNodeOrNull<UnitStressPanel>("UnitStressPanel");
		Fact(panel is { IsOpen: false }, "the panel is there, closed");
		if (panel == null)
		{
			yield break;
		}

		Key(Godot.Key.F10);
		foreach (bool _ in Wait(3)) yield return true;
		Fact(panel.IsOpen && InputFocus.Menu && Input.MouseMode == Input.MouseModeEnum.Visible,
			$"F10 opens it, holds the menu focus and frees the pointer ({panel.IsOpen}, {InputFocus.Menu}, {Input.MouseMode})");
		Shot("unit-stress-2-panel");
		Key(Godot.Key.Escape);
		foreach (bool _ in Wait(3)) yield return true;
		Fact(!panel.IsOpen && !InputFocus.Menu, $"Escape closes it and gives the focus back ({panel.IsOpen}, {InputFocus.Menu})");
		Fact(!GetTree().Paused, "Escape on the panel does not pause the game");
		Key(Godot.Key.F10);
		foreach (bool _ in Wait(3)) yield return true;
		Key(Godot.Key.F10);
		foreach (bool _ in Wait(3)) yield return true;
		Fact(!panel.IsOpen && !InputFocus.Menu, "F10 opens and closes it again");

		// ---- a wave ----
		Barracks barracks = units.BarracksAt(0);
		stress.Kind = 10; // mixed foot
		stress.Count = 16;
		stress.Order = StressOrder.HuntPlayer;
		stress.Tactics = true;
		int placed = stress.SpawnWave(16);
		foreach (bool _ in Wait(2)) yield return true;

		List<Unit> wave = Alive();
		Fact(placed == 16 && wave.Count == 16, $"a wave of 16 is put on the field ({placed} placed, {wave.Count} alive)");
		Fact(wave.All(unit => unit.Team == Team.Strategist), "every one is the strategist's");
		Fact(wave.Select(unit => unit.DefinitionId).Distinct().Count() == 4,
			"mixed foot is riflemen, heavy gunners, snipers and operatives");

		float spacing = StressRings.SpacingFor(0.45f);
		float nearest = wave.Min(unit => Flat(unit.GlobalPosition, barracks.GlobalPosition));
		float furthest = wave.Max(unit => Flat(unit.GlobalPosition, barracks.GlobalPosition));
		float closest = float.MaxValue;
		for (int i = 0; i < wave.Count; i++)
		{
			for (int j = i + 1; j < wave.Count; j++)
			{
				closest = MathF.Min(closest, Flat(wave[i].GlobalPosition, wave[j].GlobalPosition));
			}
		}

		Measured($"the wave stands {F(nearest)}-{F(furthest)} m from the barracks, the closest two {F(closest)} m apart");
		Fact(nearest >= UnitStressTest.InnerRingMeters - 1f && furthest <= UnitStressTest.InnerRingMeters + (2f * spacing) + 1f,
			"it stands in rings round the barracks, outside the building");
		Fact(closest >= spacing * 0.8f, $"no two stand closer than the spacing ({F(spacing)} m)");

		int owner = OwnerId.ForPeer(local.PeerId);
		Fact(wave.All(unit => unit.Order.Kind == OrderKind.Attack && unit.Order.TargetOwnerId == owner
			&& Flat(unit.Order.Target, me.SimPosition) <= UnitStressTest.RetargetMeters + 0.5f),
			"each is told to attack-move on the player, naming them");
		Fact(wave.All(unit => unit.Autonomy == MicroAutonomy.On), "each has its tactics on");

		float before = wave.Average(unit => Flat(unit.GlobalPosition, me.SimPosition));
		foreach (bool _ in Wait(12 * Second)) yield return true;
		List<Unit> still = wave.Where(unit => IsInstanceValid(unit) && unit.IsAlive).ToList();
		float after = still.Count > 0 ? still.Average(unit => Flat(unit.GlobalPosition, me.SimPosition)) : 0f;
		Measured($"mean distance to the player {F(before)} m, 12 s later {F(after)} m; {still.Count} alive;"
			+ $" player health {local.Health}, deaths {local.Deaths}");
		Fact(after < before - 15f, "the wave closes on the player");
		Measured(stress.Describe());
		Shot("unit-stress-3-wave");

		// ---- the field's limit ----
		stress.Kind = 0; // riflemen
		int more = stress.SpawnWave(100);
		foreach (bool _ in Wait(2)) yield return true;
		Measured($"a wave of 100 placed {more}; slots {units.SlotCount}, alive {units.LiveUnitCount}; '{stress.LastAction}'");
		Fact(units.SlotCount == SimConfig.MaxUnits && stress.LastAction.Contains("MaxUnits"),
			$"a wave past the field's limit stops at it, {SimConfig.MaxUnits}, and says so");
		foreach (bool _ in Wait(2 * Second)) yield return true;
		Measured(stress.Describe());
		Shot("unit-stress-4-full");

		stress.ClearField();
		foreach (bool _ in Wait(2)) yield return true;
		Fact(units.SlotCount == 0 && stress.Mode == StressMode.Manual, $"Clear empties the field ({units.SlotCount})");

		// ---- keep alive ----
		stress.Count = 10;
		stress.Order = StressOrder.Hold;
		stress.SetMode(StressMode.KeepAlive);
		foreach (bool _ in Until(() => units.LiveUnitCount == 10, 2 * Second)) yield return true;
		Fact(units.LiveUnitCount == 10, $"Keep alive fills the field to the count ({units.LiveUnitCount})");
		Fact(Alive().All(unit => unit.Order.Kind == OrderKind.None), "units told to hold have no standing order");

		List<Unit> victims = Alive().Take(4).ToList();
		foreach (Unit victim in victims)
		{
			units.Damage(victim, 10_000f, explosive: false, vehicleScale: 1f, OwnerId.ForPeer(local.PeerId), net.Tick, out _);
		}

		int afterKills = units.LiveUnitCount;
		foreach (bool _ in Until(() => units.LiveUnitCount == 10, 2 * Second)) yield return true;
		Fact(afterKills == 6 && units.LiveUnitCount == 10,
			$"four killed are replaced within a second ({afterKills} after the kills, {units.LiveUnitCount} after)");

		stress.SetMode(StressMode.Manual);
		stress.ClearField();
		foreach (bool _ in Wait(2)) yield return true;

		// ---- the ramp ----
		stress.Count = 16;
		stress.IntervalSeconds = 5f;
		stress.FpsFloor = 1f;
		stress.Order = StressOrder.AttackSpawn;
		stress.SetMode(StressMode.Ramp);
		Fact(stress.Ramp is { Running: true, Target: 16 }, "a ramp starts at one step");
		foreach (bool _ in Until(() => stress.Ramp is { Finished: true }, 30 * Second)) yield return true;
		StressRamp ramp = stress.Ramp;
		Measured($"ramp: {ramp.StopReason}; steps {string.Join(", ", ramp.Steps.Select(step => $"{step.Target}: {F(step.MeanAlive, "0.0")} alive"))}");
		Fact(!_timedOut && ramp.Steps.Select(step => step.Target).SequenceEqual(new[] { 16, 32, 48, 64 }),
			"the ramp steps 16, 32, 48, 64");
		Fact(ramp.Steps.All(step => step.MeanAlive >= step.Target - 2), "each step holds its target on the field");
		Fact(ramp.StopReason.Contains("limit") && ramp.HeldTarget == 64 && stress.Mode == StressMode.Manual,
			"it stops at the field's limit, and goes back to manual");
		Fact(Alive().All(unit => unit.Order.Kind == OrderKind.Attack
			&& Flat(unit.Order.Target, PlayerManager.Instance.SpawnCentroid) < 0.5f),
			"units told to attack-move on the spawn are told the spawn line's middle");

		Measured(stress.Describe());
		panel.SetOpen(true);
		foreach (bool _ in Wait(Second / 2)) yield return true;
		Shot("unit-stress-5-ramp");
		panel.SetOpen(false);
		stress.ClearField();
		foreach (bool _ in Wait(2)) yield return true;
	}
}
