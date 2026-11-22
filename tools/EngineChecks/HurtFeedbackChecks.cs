using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using Gdpyr.Core;
using Gdpyr.Effects;
using Gdpyr.Fps;
using Gdpyr.Match;
using Gdpyr.Net;
using Gdpyr.Sim;
using Gdpyr.Ui;
using Godot;

namespace Gdpyr.EngineChecks;

/// <summary>
/// The hurt feedback in the game (docs/HURT_FEEDBACK.md: Scripts/Effects/CameraShake.cs,
/// Scripts/Effects/TunnelVision.cs and their scenes in Effects/Hurt/), run by
/// <c>scripts/hurt-feedback-checks.sh</c> on a copy of the tree with this file added as an
/// autoload; not part of the game.
///
/// <c>listen</c>: a listen host's own player. The effects are wired into the player's scene
/// with their curves, noise and shader; a hit of each severity goes as deep and shakes as
/// hard as the scenes' curves say, deeper and longer the more grievous; the shake turns and
/// slides what is drawn and never where a shot leaves from or where it goes, and gives the
/// look back exactly; the tunnel fades without ever deepening again and is gone in its time;
/// two hits stack; rounds that really hit the player draw it through the authority's damage,
/// as grievous as what they took, a killing blow the deepest, and a respawn clears it; and
/// the console's <c>hurt</c>. With <c>HURT_SHOTS</c> and a display, screenshots of each depth.
///
/// <c>server</c> and <c>client</c>: a dedicated server fires rounds at a client's player, and
/// the client draws each hit from its player record alone — a rifle round, a DMR round and a
/// killing blow — as grievous as what the record says it lost.
///
/// One line per fact, <c>PASS</c> or <c>FAIL</c>; the process exits with the number that failed.
/// </summary>
public partial class HurtFeedbackChecks : Node
{
	private const int Second = SimConfig.TickRate;

	/// <summary>Open floor (the muzzle and mortar checks' site), looking along −Z.</summary>
	private static readonly Vector3 Site = new(40f, 0.6f, 50f);

	private string _role;
	private string _dir;
	private string _shots;
	private IEnumerator<bool> _run;
	private int _failures;
	private int _facts;
	private bool _timedOut;

	// What was drawn on the last render frame, read after every other node's _Process.
	private Camera3D _probe;
	private Basis _drawnBasis;
	private float _drawnH;
	private float _drawnV;
	private int _frames;

	public override void _Ready()
	{
		_role = System.Environment.GetEnvironmentVariable("HURT_FEEDBACK_CHECK");
		_dir = System.Environment.GetEnvironmentVariable("HURT_FEEDBACK_DIR");
		_shots = System.Environment.GetEnvironmentVariable("HURT_SHOTS");
		_run = _role switch
		{
			"listen" => Listen(),
			"server" when !string.IsNullOrEmpty(_dir) => Server(),
			"client" when !string.IsNullOrEmpty(_dir) => Client(),
			_ => null,
		};

		if (_run == null)
		{
			SetPhysicsProcess(false);
			SetProcess(false);
			return;
		}

		ProcessMode = ProcessModeEnum.Always;

		// Last of every node's _Process, so what it reads is what the frame draws.
		ProcessPriority = 100000;
		GD.Print($"[checks] hurt feedback check: {_role}");
	}

	public override void _Process(double delta)
	{
		if (_probe != null && IsInstanceValid(_probe))
		{
			_drawnBasis = _probe.Basis;
			_drawnH = _probe.HOffset;
			_drawnV = _probe.VOffset;
			_frames++;
		}
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
			GD.Print($"[checks] {_role}: {_facts - _failures} of {_facts} facts hold");
			Mark($"finished-{_role}");
			SetPhysicsProcess(false);
			Engine.TimeScale = 1f;
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

		GD.Print($"[checks] {(ok ? "PASS" : "FAIL")} {_role}: {what}");
	}

	private void Measured(string what) => GD.Print($"[checks] measured {_role}: {what}");

	private static string F(float v, string format = "0.000") => v.ToString(format, CultureInfo.InvariantCulture);

	private void Mark(string name, string content = "")
	{
		if (!string.IsNullOrEmpty(_dir))
		{
			File.WriteAllText(Path.Combine(_dir, name), content);
		}
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

	private static LocalInputSampler Sampler => PlayerManager.Instance.GetNode<LocalInputSampler>("LocalInputSampler");

	private static uint Tick => NetworkManager.Instance.Tick;

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

	/// <summary>The angle between two rotations [°].</summary>
	private static float Degrees(Basis a, Basis b)
	{
		float dot = Mathf.Abs(a.GetRotationQuaternion().Dot(b.GetRotationQuaternion()));
		return Mathf.RadToDeg(2f * Mathf.Acos(Mathf.Clamp(dot, -1f, 1f)));
	}

	private static float Intensity(TunnelVision vision) =>
		vision.Material is ShaderMaterial material ? material.GetShaderParameter(vision.IntensityParameter).AsSingle() : -1f;

	/// <summary>A round in nobody's name, dropped onto the top of <paramref name="target"/>'s head from 3.5 m up.</summary>
	private static void ShootAt(fps_controller target, byte weapon)
	{
		Vector3 above = target.SimPosition + new Vector3(0f, 3.5f, 0f);
		CombatManager.Instance.SpawnProjectile(Tick, OwnerId.None, weapon, above, Vector3.Down, 0);
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

		string path = Path.Combine(_shots, $"{name}.png");
		image.SavePng(path);
		Measured($"saved {path}");
	}

	private IEnumerable<bool> Onto(Team team)
	{
		while (!Session.InGame || CombatManager.Instance?.Local?.Character == null)
		{
			yield return true;
		}

		foreach (bool _ in Wait(30)) yield return true;
		PlayerCombat local = CombatManager.Instance.Local;
		if (local.AwaitingRole || local.Team != team)
		{
			CombatManager.Instance.RequestTeam(team);
		}

		foreach (bool _ in Until(() => local.IsAlive && local.Team == team, 20 * Second)) yield return true;
	}

	// ---- listen --------------------------------------------------------------------------

	private IEnumerator<bool> Listen()
	{
		foreach (bool _ in Onto(Team.GroundForce)) yield return true;
		PlayerCombat local = CombatManager.Instance.Local;
		Fact(!_timedOut && local.IsAlive, "the listen host's player is alive on the ground force");
		fps_controller me = local.Character;
		Camera3D camera = me.camera;
		CameraShake shake = me.HurtShake;
		TunnelVision vision = me.HurtVision;
		_probe = camera;

		// ---- the scenes ----
		Fact(shake != null && shake.GetParent() == camera && shake.SceneFilePath == "res://Effects/Hurt/CameraShake.tscn",
			$"the camera shake is Effects/Hurt/CameraShake.tscn, under the local camera ({shake?.GetPath()})");
		Fact(vision != null && vision.SceneFilePath == "res://Effects/Hurt/TunnelVision.tscn"
			&& vision.Material is ShaderMaterial { Shader.ResourcePath: "res://Effects/Hurt/tunnel_vision.gdshader" },
			$"the tunnel vision is Effects/Hurt/TunnelVision.tscn with its shader ({vision?.GetPath()})");
		if (shake == null || vision == null)
		{
			yield break;
		}

		Fact(shake.TraumaBySeverity != null && shake.Noise != null && vision.DepthBySeverity != null
			&& vision.Envelope != null,
			"the scenes' curves and noise are loaded with them");
		Rect2 screen = me.GetViewport().GetVisibleRect();
		Fact(vision.GetGlobalRect().Encloses(screen) || vision.Size.IsEqualApprox(screen.Size),
			$"the tunnel covers the screen ({vision.Size} over {screen.Size})");
		Fact(!vision.Visible && vision.Level == 0f && Intensity(vision) == 0f && !shake.IsShaking && shake.Trauma == 0f,
			"before a hit nothing shows and the camera is still");

		me.Teleport(new Transform3D(Basis.Identity, Site));
		Sampler.Align(0f, -0.1f);
		foreach (bool _ in Wait(20)) yield return true;
		Shot("hurt_none");

		// ---- a hit of each severity, through the scenes' curves ----
		float[] severities = { 0.2f, 0.6f, 1f };
		var depths = new float[severities.Length];
		var traumas = new float[severities.Length];
		var seconds = new float[severities.Length];
		bool matchesCurves = true;
		for (int i = 0; i < severities.Length; i++)
		{
			me.ClearHurt();
			me.PresentHurt(severities[i]);
			depths[i] = vision.Level;
			traumas[i] = shake.Trauma;
			seconds[i] = vision.Seconds;
			float depthCurve = Mathf.Clamp(vision.DepthBySeverity.Sample(severities[i]), 0f, 1f)
				* Mathf.Clamp(vision.Envelope.Sample(0f), 0f, 1f);
			float traumaCurve = Mathf.Clamp(shake.TraumaBySeverity.Sample(severities[i]), 0f, 1f);
			matchesCurves &= Mathf.Abs(depths[i] - depthCurve) < 1e-4f && Mathf.Abs(traumas[i] - traumaCurve) < 1e-4f
				&& Mathf.Abs(Intensity(vision) - depths[i]) < 1e-6f && vision.Visible;
			Measured($"severity {F(severities[i], "0.0")}: tunnel {F(depths[i])} for {F(seconds[i], "0.00")} s,"
				+ $" trauma {F(traumas[i])}");
		}

		Fact(matchesCurves, "each hit goes as deep and shakes as hard as the scenes' curves say, into the shader's intensity");
		Fact(depths[0] > 0f && depths[0] < depths[1] && depths[1] < depths[2],
			$"deeper the more grievous ({F(depths[0])} < {F(depths[1])} < {F(depths[2])})");
		Fact(traumas[0] > 0f && traumas[0] < traumas[1] && traumas[1] < traumas[2],
			$"a harder shake the more grievous ({F(traumas[0])} < {F(traumas[1])} < {F(traumas[2])})");
		Fact(seconds[0] < seconds[1] && seconds[1] < seconds[2],
			$"longer the more grievous ({F(seconds[0], "0.00")} < {F(seconds[1], "0.00")} < {F(seconds[2], "0.00")} s)");
		me.ClearHurt();

		// ---- the shake moves what is drawn, not the simulation ----
		foreach (bool _ in Wait(10)) yield return true;
		Basis look = Basis.FromEuler(new Vector3(me.Pitch, 0f, 0f));
		Vector3 eye = me.EyePosition;
		Vector3 aim = me.AimDirection;
		Vector3 cameraAt = camera.GlobalPosition;
		Fact(Degrees(camera.Basis, look) < 1e-3f, "at rest the camera is turned as the look says");

		me.PresentHurt(1f);
		float maxAngle = 0f;
		float maxOffset = 0f;
		float maxSimDrift = 0f;
		int shaken = 0;
		int frames = _frames;
		foreach (bool _ in Until(() => shake.Trauma == 0f && !shake.IsShaking, 3 * Second))
		{
			if (_frames > frames && shake.IsShaking)
			{
				frames = _frames;
				float angle = Degrees(_drawnBasis, Basis.FromEuler(new Vector3(me.Pitch, 0f, 0f)));
				maxAngle = Mathf.Max(maxAngle, angle);
				maxOffset = Mathf.Max(maxOffset, Mathf.Max(Mathf.Abs(_drawnH), Mathf.Abs(_drawnV)));
				shaken += angle > 0.01f ? 1 : 0;
			}

			maxSimDrift = Mathf.Max(maxSimDrift, Mathf.Max(me.EyePosition.DistanceTo(eye),
				Mathf.Max(me.AimDirection.DistanceTo(aim), camera.GlobalPosition.DistanceTo(cameraAt))));
			yield return true;
		}

		Vector3 reach = shake.MaxAngleDegrees;
		Measured($"a killing blow's shake: {shaken} frames, up to {F(maxAngle, "0.00")}° and {F(maxOffset)} m");
		Fact(!_timedOut && shaken >= 10 && maxAngle > 0.5f && maxAngle <= reach.Length() + 0.01f,
			$"a killing blow turns the camera for a moment, within its reach ({shaken} frames, up to {F(maxAngle, "0.00")}°,"
			+ $" reach {F(reach.Length(), "0.00")}°)");
		Fact(maxOffset > 0f && maxOffset <= Mathf.Max(shake.MaxOffset.X, shake.MaxOffset.Y) + 1e-4f,
			$"and slides the picture within its reach ({F(maxOffset)} m)");
		Fact(maxSimDrift < 1e-5f,
			$"while shaking, the eye a shot leaves from, the way it goes and the camera's position stay put ({maxSimDrift:E1})");
		foreach (bool _ in Wait(3)) yield return true;
		Fact(Degrees(camera.Basis, Basis.FromEuler(new Vector3(me.Pitch, 0f, 0f))) < 1e-3f && camera.HOffset == 0f
			&& camera.VOffset == 0f && shake.Trauma == 0f,
			$"once settled, the camera is given back to the look exactly (h {camera.HOffset}, v {camera.VOffset})");
		me.ClearHurt();

		// ---- the fade ----
		me.PresentHurt(0.6f);
		float peak = vision.Level;
		float duration = vision.Seconds;
		float last = peak;
		bool deepened = false;
		float half = -1f;
		int ticks = 0;
		foreach (bool _ in Until(() => !vision.Visible, 10 * Second))
		{
			deepened |= vision.Level > last + 1e-5f;
			last = vision.Level;
			ticks++;
			if (half < 0f && ticks >= (int)(duration * Second * 0.5f))
			{
				half = vision.Level;
			}

			yield return true;
		}

		float took = ticks / (float)Second;
		Measured($"0.6 fades from {F(peak)}: {F(half)} half way, gone after {F(took, "0.00")} s of {F(duration, "0.00")}");
		Fact(!_timedOut && !deepened, "the tunnel only ever fades once a hit has landed");
		Fact(half > 0f && half < peak, $"half way through it is shallower ({F(half)} of {F(peak)})");
		Fact(Mathf.Abs(took - duration) < 0.1f && vision.Level == 0f && Intensity(vision) == 0f,
			$"and gone in its time ({F(took, "0.00")} s of {F(duration, "0.00")})");

		// ---- two hits close together ----
		me.ClearHurt();
		me.PresentHurt(0.3f);
		float one = vision.Level;
		float oneTrauma = shake.Trauma;
		me.PresentHurt(0.3f);
		Fact(vision.Level > one && shake.Trauma > oneTrauma,
			$"a second hit on the first goes deeper ({F(one)} to {F(vision.Level)}) and shakes harder"
			+ $" ({F(oneTrauma)} to {F(shake.Trauma)})");
		me.ClearHurt();
		Fact(!vision.Visible && !shake.IsShaking, "clearing takes both off at once");

		// ---- rounds that really hit the player: the authority's damage ----
		Measured($"the kit's vest: {local.Vest.Tier}, {local.Vest.Points} points");
		foreach (bool _ in Wait(10)) yield return true;
		foreach ((byte weapon, string name) in new[] { (WeaponCatalog.Rifle, "rifle"), (WeaponCatalog.Dmr, "DMR") })
		{
			int health = local.Health;
			int vest = local.Vest.Points;
			ShootAt(me, weapon);
			foreach (bool _ in Until(() => local.Health < health, Second)) yield return true;
			float expected = HurtFeedback.Severity(health - local.Health, vest - local.Vest.Points, !local.IsAlive);
			float depth = vision.DepthBySeverity.Sample(expected);
			Fact(!_timedOut && local.IsAlive && vision.Level <= depth + 1e-4f && vision.Level > depth * 0.97f
				&& shake.Trauma > 0f,
				$"a {name} round that took {health - local.Health} health and {vest - local.Vest.Points} vest is felt at"
				+ $" {F(expected)}: tunnel {F(vision.Level)} of {F(depth)}, trauma {F(shake.Trauma)}");
			foreach (bool _ in Until(() => !vision.Visible && !shake.IsShaking, 5 * Second)) yield return true;
		}

		// DMR rounds until one kills, each on the last before it has faded.
		int before = local.Health;
		float fading = 0f;
		for (int round = 0; round < 6 && local.IsAlive; round++)
		{
			before = local.Health;
			fading = vision.Level;
			ShootAt(me, WeaponCatalog.Dmr);
			foreach (bool _ in Until(() => local.Health < before, Second)) yield return true;
			foreach (bool _ in Wait(Second / 4)) yield return true;
		}

		Fact(local.IsDowned && vision.Level > fading && vision.Level >= vision.DepthBySeverity.Sample(1f) * 0.9f,
			$"a killing blow on {before} health is the deepest ({F(vision.Level)}, from {F(fading)})");
		foreach (bool _ in Wait(Second / 2)) yield return true;
		Fact(vision.Visible && vision.Level > 0f, $"and still fading over the death screen ({F(vision.Level)})");
		foreach (bool _ in Until(() => local.IsAlive, 10 * Second)) yield return true;
		Fact(!_timedOut && !vision.Visible && vision.Level == 0f && !shake.IsShaking,
			"a respawn starts with a clear view");

		// ---- a vest: the next life in a medium one, killed into by DMR rounds ----
		Console("loadout rifle 4 2 medium");
		for (int round = 0; round < 8 && local.IsAlive; round++)
		{
			int health = local.Health;
			ShootAt(me, WeaponCatalog.Dmr);
			foreach (bool _ in Until(() => local.Health < health, Second)) yield return true;
		}

		foreach (bool _ in Until(() => local.IsAlive, 10 * Second)) yield return true;
		foreach (bool _ in Wait(10)) yield return true;
		Measured($"the next life's vest: {local.Vest.Tier}, {local.Vest.Points} points");
		if (local.Vest.Points > 0)
		{
			int health = local.Health;
			int vest = local.Vest.Points;
			ShootAt(me, WeaponCatalog.Rifle);
			foreach (bool _ in Until(() => local.Health < health, Second)) yield return true;
			float expected = HurtFeedback.Severity(health - local.Health, vest - local.Vest.Points, false);
			float bare = HurtFeedback.Severity(health - local.Health + (vest - local.Vest.Points), 0, false);
			float depth = vision.DepthBySeverity.Sample(expected);
			Fact(!_timedOut && vest > local.Vest.Points && expected < bare && vision.Level <= depth + 1e-4f
				&& vision.Level > depth * 0.97f,
				$"a rifle round on a vest, {health - local.Health} health and {vest - local.Vest.Points} vest, is felt"
				+ $" at {F(expected)}, lighter than {F(bare)} bare: tunnel {F(vision.Level)} of {F(depth)}");
		}
		else
		{
			Fact(false, "the next life wears the medium vest asked for");
		}

		me.ClearHurt();

		// ---- the console ----
		Console("hurt 0.5");
		string said = LastConsoleLine();
		Fact(said.StartsWith("hurt 0.50: tunnel ", StringComparison.Ordinal) && vision.Visible && shake.Trauma > 0f,
			$"`hurt 0.5` draws it ({said})");
		Console("hurt clear");
		Fact(!vision.Visible && shake.Trauma == 0f, "`hurt clear` takes it off");
		Console("hurt 2");
		said = LastConsoleLine();
		Fact(said.Contains("hurt <severity 0-1>|clear", StringComparison.Ordinal) && !vision.Visible,
			$"`hurt 2` is refused ({said})");

		// ---- pictures: each depth at its top ----
		if (!string.IsNullOrEmpty(_shots) && DisplayServer.GetName() != "headless")
		{
			foreach (float severity in new[] { 0.2f, 0.6f, 1f })
			{
				me.ClearHurt();
				foreach (bool _ in Wait(10)) yield return true;
				Engine.TimeScale = 0.01f;
				me.PresentHurt(severity);
				shake.Clear();
				foreach (bool _ in Wait(2)) yield return true;
				Shot($"hurt_{severity.ToString("0.0", CultureInfo.InvariantCulture)}");
				Engine.TimeScale = 1f;
			}

			me.ClearHurt();
		}
	}

	// ---- server and client ---------------------------------------------------------------

	/// <summary>The most rounds the server fires at the client: a rifle's, then DMR rounds until one kills.</summary>
	private const int Volley = 6;

	private static (byte Weapon, string Name) Round(int i) => i == 0 ? (WeaponCatalog.Rifle, "rifle") : (WeaponCatalog.Dmr, "DMR");

	private IEnumerator<bool> Server()
	{
		foreach (bool _ in Until(() => Has("client.peer"), 120 * Second)) yield return true;
		int peer = int.TryParse(File.ReadAllText(Path.Combine(_dir, "client.peer")).Trim(), out int p) ? p : 0;
		PlayerCombat target = null;
		foreach (bool _ in Until(() => (target = CombatManager.Instance?.Find(peer)) is { IsAlive: true, Character: not null },
			60 * Second)) yield return true;
		Fact(!_timedOut, $"the client's player {peer} is on the field");
		if (_timedOut)
		{
			yield break;
		}

		for (int i = 0; i < Volley && target.IsAlive; i++)
		{
			foreach (bool _ in Until(() => Has($"clear-{i}"), 30 * Second)) yield return true;
			if (_timedOut)
			{
				Fact(false, $"the client is ready for round {i}");
				yield break;
			}

			int health = target.Health;
			ShootAt(target.Character, Round(i).Weapon);
			foreach (bool _ in Until(() => target.Health < health, Second)) yield return true;
			Fact(!_timedOut, $"round {i}, a {Round(i).Name}'s, hits the client's player ({health} to {target.Health})");
		}

		Fact(target.IsDowned, "the last of them killed the client's player");
		foreach (bool _ in Until(() => Has("finished-client"), 60 * Second)) yield return true;
	}

	private IEnumerator<bool> Client()
	{
		foreach (bool _ in Onto(Team.GroundForce)) yield return true;
		PlayerCombat local = CombatManager.Instance.Local;
		Fact(!_timedOut && local.IsAlive, "the client's player is alive on the ground force");
		fps_controller me = local.Character;
		CameraShake shake = me.HurtShake;
		TunnelVision vision = me.HurtVision;
		Fact(shake != null && vision != null, "the client's character has the hurt feedback");
		if (shake == null || vision == null)
		{
			yield break;
		}

		Mark("client.peer", NetworkManager.Instance.LocalPeerId.ToString(CultureInfo.InvariantCulture));
		foreach (bool _ in Wait(Second)) yield return true;

		bool killed = false;
		for (int i = 0; i < Volley && !killed; i++)
		{
			foreach (bool _ in Until(() => local.IsAlive && !vision.Visible && !shake.IsShaking, 15 * Second))
				yield return true;

			int health = local.Health;
			int vest = local.Vest.Points;
			Mark($"clear-{i}");
			foreach (bool _ in Until(() => local.Health < health, 10 * Second)) yield return true;
			killed = local.IsDowned;
			float expected = HurtFeedback.Severity(health - local.Health, vest - local.Vest.Points, killed);
			float depth = vision.DepthBySeverity.Sample(expected);
			Fact(!_timedOut && vision.Level <= depth + 1e-4f && vision.Level > depth * 0.97f && shake.Trauma > 0f,
				$"a {Round(i).Name} round: the record's {health} to {local.Health} health, {vest} to {local.Vest.Points} vest"
				+ $"{(killed ? ", killed" : string.Empty)}, is felt at {F(expected)}: tunnel {F(vision.Level)}"
				+ $" of {F(depth)}, trauma {F(shake.Trauma)}");
		}

		Fact(killed, "the last round was a killing blow, felt as the deepest");
		foreach (bool _ in Until(() => local.IsAlive, 10 * Second)) yield return true;
		Fact(!_timedOut && !vision.Visible && !shake.IsShaking, "the client's respawn starts with a clear view");
	}
}
