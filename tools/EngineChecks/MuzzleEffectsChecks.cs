using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using Gdpyr.Core;
using Gdpyr.Effects;
using Gdpyr.Fps;
using Gdpyr.Match;
using Gdpyr.Net;
using Gdpyr.Rts;
using Gdpyr.Sim;
using Gdpyr.Ui;
using Godot;

namespace Gdpyr.EngineChecks;

/// <summary>
/// Muzzle effects in the game (docs/MUZZLE_EFFECTS.md, Scripts/Effects/MuzzleEffects.cs), run
/// by <c>scripts/muzzle-effects-checks.sh</c> on a copy of the tree with this file added as an
/// autoload; not part of the game.
///
/// A listen host on <c>Test.tscn</c> at <c>--fixed-fps 60</c>, its own player through the
/// input map: their own rifle draws a flash at the end of the viewmodel's barrel and following
/// it, a puff of smoke and a case a round, and the cases fly out to the right and come to rest
/// on the floor; a round fired in nobody's name draws every part 3 m from the camera, fewer
/// further off and nothing at 30 m; a strategist's rifleman 4 m off draws its flash ahead of
/// its eye, following it, and a tank's at the edge of its hull with no case; a hand grenade
/// draws nothing; everything is taken back; and the console's <c>muzzle</c>.
///
/// Headless it says what is drawn, where and for how long, not what it looks like. With
/// <c>MUZZLE_SHOTS</c> naming a directory and a display to draw on, it also saves screenshots
/// there, with time slowed so a flash of three frames is held for long enough to be caught.
/// One line per fact, <c>PASS</c> or <c>FAIL</c>; the process exits with the number that failed.
/// </summary>
public partial class MuzzleEffectsChecks : Node
{
	private const int Second = SimConfig.TickRate;
	private const float FloorY = 0.5f;

	/// <summary>Open floor with 60 m clear in front of it (the mortar check's site), looking along −Z.</summary>
	private static readonly Vector3 Site = new(40f, FloorY + 0.1f, 50f);

	private IEnumerator<bool> _run;
	private int _failures;
	private int _facts;
	private bool _timedOut;
	private string _shots;

	public override void _Ready()
	{
		if (System.Environment.GetEnvironmentVariable("MUZZLE_EFFECTS_CHECK") != "listen")
		{
			SetPhysicsProcess(false);
			return;
		}

		_shots = System.Environment.GetEnvironmentVariable("MUZZLE_SHOTS");
		ProcessMode = ProcessModeEnum.Always;
		_run = Listen();
		GD.Print("[checks] muzzle effects check: listen");
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

	private static IEnumerable<bool> Press(string action, int ticks)
	{
		Input.ActionPress(action);
		foreach (bool _ in Wait(ticks)) yield return true;
		Input.ActionRelease(action);
	}

	private static IEnumerable<bool> Tap(string action)
	{
		foreach (bool _ in Press(action, 4)) yield return true;
		foreach (bool _ in Wait(4)) yield return true;
	}

	private static LocalInputSampler Sampler => PlayerManager.Instance.GetNode<LocalInputSampler>("LocalInputSampler");

	private static uint Tick => NetworkManager.Instance.Tick;

	private static void Put(fps_controller me, Vector3 at, float yaw = 0f, float pitch = 0f)
	{
		me.Teleport(new Transform3D(new Basis(Vector3.Up, yaw), at));
		Sampler.Align(yaw, pitch);
	}

	private static string LastConsoleLine()
	{
		FieldInfo lines = typeof(GameConsole).GetField("_lines", BindingFlags.NonPublic | BindingFlags.Instance);
		return lines?.GetValue(GameConsole.Instance) is List<string> { Count: > 0 } list ? list[^1] : string.Empty;
	}

	private static void Console(string line)
	{
		MethodInfo submit = typeof(GameConsole).GetMethod("Submit", BindingFlags.NonPublic | BindingFlags.Instance);
		submit.Invoke(GameConsole.Instance, new object[] { line });
	}

	/// <summary>The flashes of <paramref name="muzzles"/> that are up now.</summary>
	private static List<MuzzleFlash> Lit(MuzzleEffects muzzles)
	{
		var lit = new List<MuzzleFlash>();
		foreach (Node child in muzzles.GetChildren())
		{
			if (child is MuzzleFlash { Visible: true } flash)
			{
				lit.Add(flash);
			}
		}

		return lit;
	}

	/// <summary>The newest flash up nearest <paramref name="at"/>.</summary>
	private static MuzzleFlash LitNearest(MuzzleEffects muzzles, Vector3 at)
	{
		MuzzleFlash best = null;
		foreach (MuzzleFlash flash in Lit(muzzles))
		{
			if (best == null || flash.GlobalPosition.DistanceTo(at) < best.GlobalPosition.DistanceTo(at))
			{
				best = flash;
			}
		}

		return best;
	}

	/// <summary>Every live case's position and whether it is still, read from the effects' own particles.</summary>
	private static List<(Vector3 Position, bool Resting, float Diameter)> Cases(MuzzleEffects muzzles)
	{
		var cases = new List<(Vector3, bool, float)>();
		FieldInfo field = typeof(MuzzleEffects).GetField("_casings", BindingFlags.NonPublic | BindingFlags.Instance);
		if (field?.GetValue(muzzles) is not Array array)
		{
			return cases;
		}

		for (int i = 0; i < muzzles.Casings; i++)
		{
			object casing = array.GetValue(i);
			Type type = casing.GetType();
			var body = (CasingBody)type.GetField("Body").GetValue(casing);
			var diameter = (float)type.GetField("Diameter").GetValue(casing);
			cases.Add((body.Position, body.Resting, diameter));
		}

		return cases;
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

	/// <summary>Holds time at <paramref name="scale"/> while there is a display to show it on, so a three-frame flash can be photographed.</summary>
	private void Slow(float scale)
	{
		if (!string.IsNullOrEmpty(_shots) && DisplayServer.GetName() != "headless")
		{
			Engine.TimeScale = scale;
		}
	}

	// ---- the check ---------------------------------------------------------------------

	private IEnumerator<bool> Listen()
	{
		while (!Session.InGame || CombatManager.Instance?.Local?.Character == null)
		{
			yield return true;
		}

		foreach (bool _ in Wait(30)) yield return true;
		PlayerCombat local = CombatManager.Instance.Local;
		if (local.AwaitingRole || local.Team != Team.GroundForce)
		{
			CombatManager.Instance.RequestTeam(Team.GroundForce);
		}

		foreach (bool _ in Until(() => local.IsAlive && local.Team == Team.GroundForce, 10 * Second)) yield return true;
		Fact(!_timedOut, "the listen host's player is alive on the ground force");
		fps_controller me = local.Character;

		MuzzleEffects muzzles = MuzzleEffects.Instance;
		Fact(muzzles != null && muzzles.GetParent() == CombatManager.Instance,
			$"muzzle effects are drawn here, by {muzzles?.GetPath()}");
		if (muzzles == null)
		{
			yield break;
		}

		Camera3D camera = me.GetViewport().GetCamera3D();
		Fact(camera != null && me.Viewmodel != null,
			$"there is a camera to draw for ({camera?.GetPath()}) and a viewmodel ({me.Viewmodel?.GetPath()})");
		if (camera == null || me.Viewmodel == null)
		{
			yield break;
		}

		Put(me, Site);
		foreach (bool _ in Wait(20)) yield return true;

		if (local.EquippedDefinitionId != WeaponCatalog.Rifle)
		{
			foreach (bool _ in Tap("weapon_large")) yield return true;
			foreach (bool _ in Wait(30)) yield return true;
		}

		Fact(local.EquippedDefinitionId == WeaponCatalog.Rifle,
			$"the rifle is up ({WeaponCatalog.NameOf(local.EquippedDefinitionId)})");

		// ---- their own rifle: one round ----
		Put(me, Site, 0f, -0.1f);
		foreach (bool _ in Wait(10)) yield return true;
		MuzzleProfile rifle = Muzzles.Profile(ShotKind.Rifle);
		int drawn = muzzles.Drawn;
		int cases = muzzles.Casings;
		int puffs = muzzles.Puffs;
		int fired = local.ShotsFired;
		Slow(0.05f);
		Input.ActionPress("fire");
		foreach (bool _ in Until(() => local.ShotsFired > fired, Second)) yield return true;
		Input.ActionRelease("fire");
		Fact(!_timedOut && local.ShotsFired == fired + 1, $"one round fired ({local.ShotsFired - fired})");
		Fact(muzzles.Drawn == drawn + 1 && muzzles.Flashes >= 1 && muzzles.Casings == cases + 1
			&& muzzles.Puffs == puffs + rifle.SmokePuffs,
			$"their own round draws a flash, {rifle.SmokePuffs} puff and a case ({muzzles.Drawn - drawn} drawn,"
			+ $" {muzzles.Flashes} flashes, {muzzles.Puffs - puffs} puffs, {muzzles.Casings - cases} cases)");

		WeaponInit viewmodel = me.Viewmodel;
		bool hasBarrel = viewmodel.TryGetMuzzle(out Vector3 barrel, out Vector3 forward);
		MuzzleFlash own = LitNearest(muzzles, barrel);
		Fact(hasBarrel && own != null && own.Anchor == viewmodel && own.GlobalPosition.DistanceTo(barrel) < 0.02f,
			$"at the end of the viewmodel's barrel {V(barrel)}, following it ({(own != null ? V(own.GlobalPosition) : "none")},"
			+ $" following {own?.Anchor?.Name})");
		Vector3 eye = camera.GlobalPosition;
		Vector3 toBarrel = barrel - eye;
		Fact(toBarrel.Dot(forward) is > 0.3f and < 1.2f && toBarrel.Length() < 1.2f,
			$"in front of the eye, {F(toBarrel.Dot(forward))} m ahead and {F(toBarrel.Length())} m off it");

		foreach (bool _ in Wait(1)) yield return true;
		Shot("own_rifle_hip");
		Slow(1f);

		// The flash goes with the barrel when the view turns within its frames.
		foreach (bool _ in Until(() => muzzles.Flashes == 0, Second)) yield return true;
		Fact(!_timedOut, $"the flash is gone within its {F(rifle.FlashSeconds * 1000f, "0")} ms");

		// ---- the case: out to the right, onto the floor, and still ----
		Vector3 right = camera.GlobalBasis.X;
		foreach (bool _ in Wait(Second / 4)) yield return true;
		List<(Vector3 Position, bool Resting, float Diameter)> flying = Cases(muzzles);
		Fact(flying.Count >= 1 && (flying[^1].Position - eye).Dot(right) > 0.2f,
			$"the case goes out to the right ({(flying.Count > 0 ? F((flying[^1].Position - eye).Dot(right)) : "?")} m right of the eye"
			+ $" after a quarter second)");

		foreach (bool _ in Until(() => Cases(muzzles).TrueForAll(c => c.Resting), 3 * Second)) yield return true;
		List<(Vector3 Position, bool Resting, float Diameter)> resting = Cases(muzzles);
		bool onFloor = resting.Count >= 1 && resting.TrueForAll(c => Mathf.Abs(c.Position.Y - (FloorY + (c.Diameter * 0.5f))) < 0.01f);
		Fact(!_timedOut && onFloor,
			$"and comes to rest on the floor ({resting.Count} cases, {(resting.Count > 0 ? V(resting[^1].Position) : "none")},"
			+ $" floor at {F(FloorY)})");

		// ---- a burst from the hip ----
		drawn = muzzles.Drawn;
		fired = local.ShotsFired;
		foreach (bool _ in Press("fire", Second / 2)) yield return true;
		foreach (bool _ in Wait(2)) yield return true;
		int burst = local.ShotsFired - fired;
		Fact(burst >= 3 && muzzles.Drawn - drawn == burst,
			$"a burst of {burst} draws {muzzles.Drawn - drawn}, one a round");
		Shot("own_rifle_burst");

		// ---- the sights up ----
		Input.ActionPress("aim");
		foreach (bool _ in Wait(Second / 2)) yield return true;
		Slow(0.05f);
		fired = local.ShotsFired;
		Input.ActionPress("fire");
		foreach (bool _ in Until(() => local.ShotsFired > fired, Second)) yield return true;
		Input.ActionRelease("fire");
		viewmodel.TryGetMuzzle(out barrel, out _);
		own = LitNearest(muzzles, barrel);
		Fact(own != null && own.GlobalPosition.DistanceTo(barrel) < 0.02f,
			$"with the sights up, at the barrel's end {V(barrel)} too ({(own != null ? V(own.GlobalPosition) : "none")})");
		foreach (bool _ in Wait(1)) yield return true;
		Shot("own_rifle_aimed");
		Slow(1f);
		Input.ActionRelease("aim");
		foreach (bool _ in Wait(Second / 2)) yield return true;

		// ---- the pistol: a modelled mesh, turned in the hand ----
		foreach (bool _ in Tap("weapon_sidearm")) yield return true;
		foreach (bool _ in Wait(Second)) yield return true;
		Fact(local.EquippedDefinitionId == WeaponCatalog.Pistol,
			$"the pistol is up ({WeaponCatalog.NameOf(local.EquippedDefinitionId)})");
		Slow(0.05f);
		fired = local.ShotsFired;
		Input.ActionPress("fire");
		foreach (bool _ in Until(() => local.ShotsFired > fired, Second)) yield return true;
		Input.ActionRelease("fire");
		hasBarrel = viewmodel.TryGetMuzzle(out barrel, out forward);
		own = LitNearest(muzzles, barrel);
		toBarrel = barrel - camera.GlobalPosition;
		Fact(hasBarrel && own != null && own.GlobalPosition.DistanceTo(barrel) < 0.02f && toBarrel.Dot(forward) > 0.2f,
			$"its flash is at the end of its barrel {V(barrel)}, {F(toBarrel.Dot(forward))} m ahead of the eye"
			+ $" ({(own != null ? V(own.GlobalPosition) : "none")})");
		foreach (bool _ in Wait(1)) yield return true;
		Shot("own_pistol");
		Slow(1f);
		foreach (bool _ in Tap("weapon_large")) yield return true;
		foreach (bool _ in Wait(Second)) yield return true;

		// ---- someone else's rounds, by how far from the camera ----
		MuzzleRanges ranges = muzzles.Ranges;
		Measured($"ranges: flash {F(ranges.FlashMeters, "0.#")} m, smoke {F(ranges.SmokeMeters, "0.#")} m,"
			+ $" cases {F(ranges.CasingMeters, "0.#")} m");
		eye = camera.GlobalPosition;
		foreach ((float meters, MuzzleParts expected) in new[]
		{
			(3f, MuzzleParts.All),
			(ranges.CasingMeters + 1f, MuzzleParts.Flash | MuzzleParts.Smoke),
			(ranges.SmokeMeters + 1f, MuzzleParts.Flash),
			(30f, MuzzleParts.None),
		})
		{
			drawn = muzzles.Drawn;
			int skipped = muzzles.Skipped;
			int flashes = muzzles.Flashes;
			puffs = muzzles.Puffs;
			cases = muzzles.Casings;
			Vector3 origin = eye + new Vector3(meters, 0f, 0f);
			CombatManager.Instance.SpawnProjectile(Tick, OwnerId.None, WeaponCatalog.Rifle, origin, Vector3.Forward, 0);
			MuzzleParts got = (muzzles.Flashes > flashes ? MuzzleParts.Flash : MuzzleParts.None)
				| (muzzles.Puffs > puffs ? MuzzleParts.Smoke : MuzzleParts.None)
				| (muzzles.Casings > cases ? MuzzleParts.Casing : MuzzleParts.None);
			bool counted = expected == MuzzleParts.None
				? muzzles.Skipped == skipped + 1 && muzzles.Drawn == drawn
				: muzzles.Drawn == drawn + 1 && muzzles.Skipped == skipped;
			Fact(got == expected && counted, $"a rifle round {F(meters, "0.#")} m from the camera draws {expected} ({got},"
				+ $" {muzzles.Drawn - drawn} drawn, {muzzles.Skipped - skipped} too far)");
			foreach (bool _ in Wait(4)) yield return true;
		}

		// ---- a rifleman 4 m off, in view, firing across it ----
		// On the player's side: one on the strategist's would find the player and open fire,
		// which is not what is being looked at. Whose side a unit is on is nothing to its muzzle.
		UnitManager units = UnitManager.Instance;
		Put(me, Site);
		foreach (bool _ in Wait(10)) yield return true;
		Vector3 riflemanAt = new(Site.X - 1.5f, FloorY, Site.Z - 4f);
		ushort riflemanId = units.ServerPlaceUnit(UnitCatalog.Infantry, riflemanAt, Team.GroundForce, OrderKind.None);
		units.ServerSetPlacedAutonomy(riflemanId, MicroAutonomy.Off);
		Unit rifleman = units.Find(riflemanId);

		foreach (bool _ in Wait(10)) yield return true;
		Fact(rifleman != null && rifleman.IsAlive, $"a rifleman is put {F(Site.DistanceTo(riflemanAt), "0.0")} m off");
		if (rifleman == null)
		{
			yield break;
		}

		Slow(0.05f);
		Vector3 across = Vector3.Right;
		Vector3 riflemanEye = rifleman.EyePosition;
		CombatManager.Instance.SpawnProjectile(Tick, OwnerId.ForUnit(riflemanId), WeaponCatalog.Rifle, riflemanEye, across, 0);
		Vector3 held = Muzzles.HeldMuzzle(riflemanEye, across, rifle);
		MuzzleFlash theirs = LitNearest(muzzles, held);
		Fact(theirs != null && theirs.Anchor == rifleman && theirs.GlobalPosition.DistanceTo(held) < 0.02f,
			$"its flash is held ahead of its eye, at {V(held)}, following it ({(theirs != null ? V(theirs.GlobalPosition) : "none")},"
			+ $" following {theirs?.Anchor?.Name})");
		foreach (bool _ in Wait(1)) yield return true;
		Shot("rifleman_4m");
		Slow(1f);
		units.ClearUnits();
		foreach (bool _ in Wait(Second)) yield return true;

		// ---- a tank 9 m off, in view: its cannon at the edge of its hull, smoke past the small arms'
		// reach, and no case ----
		Vector3 tankAt = new(Site.X - 4f, FloorY, Site.Z - 8.5f);
		ushort tankId = units.ServerPlaceUnit(UnitCatalog.Tank, tankAt, Team.GroundForce, OrderKind.None);
		units.ServerSetPlacedAutonomy(tankId, MicroAutonomy.Off);
		Unit tank = units.Find(tankId);
		foreach (bool _ in Wait(10)) yield return true;
		Fact(tank != null && tank.IsAlive, $"a tank is put {F(Site.DistanceTo(tankAt), "0.0")} m off");
		if (tank == null)
		{
			yield break;
		}

		Slow(0.05f);
		cases = muzzles.Casings;
		puffs = muzzles.Puffs;
		Vector3 tankEye = tank.EyePosition;
		CombatManager.Instance.SpawnProjectile(Tick, OwnerId.ForUnit(tankId), WeaponCatalog.Cannon, tankEye, Vector3.Right, 0);
		Vector3 hull = Muzzles.HullMuzzle(tankEye, Vector3.Right, tank.Definition.RadiusMeters);
		MuzzleFlash cannon = LitNearest(muzzles, hull);
		float hullMeters = camera.GlobalPosition.DistanceTo(hull);
		Fact(cannon != null && cannon.Anchor == tank && cannon.GlobalPosition.DistanceTo(hull) < 0.02f
			&& muzzles.Casings == cases && muzzles.Puffs > puffs && hullMeters > ranges.SmokeMeters,
			$"its cannon flashes at the edge of its hull {V(hull)} ({(cannon != null ? V(cannon.GlobalPosition) : "none")}),"
			+ $" with smoke {F(hullMeters, "0.0")} m off ({muzzles.Puffs - puffs} puffs) and no case ({muzzles.Casings - cases})");
		foreach (bool _ in Wait(2)) yield return true;
		Shot("tank_cannon_9m");
		Slow(1f);
		units.ClearUnits();
		foreach (bool _ in Wait(Second)) yield return true;

		// ---- a hand grenade is thrown, not fired ----
		foreach (bool _ in Until(() => local.IsAlive, 15 * Second)) yield return true;
		Fact(!_timedOut && local.IsAlive, $"the player is alive ({local.Health} health)");
		drawn = muzzles.Drawn;
		int skippedBefore = muzzles.Skipped;
		byte grenades = local.Grenades.Count;
		Put(me, Site, 0f, 0.5f);
		foreach (bool _ in Wait(4)) yield return true;
		foreach (bool _ in Tap("throw_grenade")) yield return true;
		foreach (bool _ in Wait(10)) yield return true;
		Fact(local.Grenades.Count == grenades - 1 && muzzles.Drawn == drawn && muzzles.Skipped == skippedBefore,
			$"a hand grenade thrown ({grenades} → {local.Grenades.Count}) draws nothing ({muzzles.Drawn - drawn})");

		// ---- the console ----
		Put(me, Site);
		foreach (bool _ in Wait(10)) yield return true;
		drawn = muzzles.Drawn;
		Console("muzzle rifle");
		string line = LastConsoleLine();
		Fact(muzzles.Drawn == drawn + 1 && line.Contains("rifle: a shot 3 m ahead (flash, smoke, case)"),
			$"`muzzle rifle` draws the rifle's ({line})");

		Console("muzzle launcher own");
		line = LastConsoleLine();
		Fact(line.Contains("launcher: your own shot (flash, smoke)"), $"`muzzle launcher own` draws no case ({line})");

		Console("muzzle range 2 2 2");
		line = LastConsoleLine();
		drawn = muzzles.Drawn;
		Console("muzzle rifle");
		string far = LastConsoleLine();
		Fact(line.Contains("flash within 2 m, smoke 2 m, cases 2 m") && far.Contains("(nothing drawn)") && muzzles.Drawn == drawn,
			$"`muzzle range 2 2 2` draws nothing 3 m off ({line}; {far})");
		Console($"muzzle range {F(ranges.FlashMeters, "0.#")} {F(ranges.SmokeMeters, "0.#")} {F(ranges.CasingMeters, "0.#")}");

		Console("muzzle geopick");
		line = LastConsoleLine();
		Fact(line.Contains("weapons:") && line.Contains("rifle") && !line.Contains("geopick"),
			$"`muzzle geopick` is refused, with the weapons that have a shot ({line})");

		// ---- everything taken back ----
		foreach (bool _ in Until(() => muzzles.Flashes == 0 && muzzles.Puffs == 0 && muzzles.Casings == 0, 8 * Second))
		{
			yield return true;
		}

		Fact(!_timedOut, $"everything is taken back when it is done ({muzzles.Flashes} flashes, {muzzles.Puffs} puffs,"
			+ $" {muzzles.Casings} cases; {muzzles.Evictions} cut short)");
	}
}
