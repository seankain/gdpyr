using System;
using System.Collections.Generic;
using System.Globalization;
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
/// Weapon audio in the game (docs/AUDIO.md, Scripts/Effects/WeaponAudio.cs), run headless by
/// <c>scripts/weapon-audio-checks.sh</c> on a copy of the tree with this file added as an
/// autoload; not part of the game.
///
/// A listen host on <c>Test.tscn</c> at <c>--fixed-fps 60</c>, its own player through the
/// input map: their own rifle heard at the ear once a round, and none of its rounds heard
/// going past them; a rifle round fired from 60 m in nobody's name heard at its muzzle, and
/// heard going past 2 m off once, where it came closest; one 8 m off not heard going past,
/// and a tank's shell 8 m off heard; a round fired from 5 m behind past a shoulder not heard
/// going past; a hand grenade thrown and not heard as a shot; every player taken back once
/// its sound is done; and the console's <c>sound</c>.
///
/// What it cannot say is what any of it sounds like: headless, the audio driver mixes into
/// nothing. One line per fact, <c>PASS</c> or <c>FAIL</c>, and <c>[checks] measured …</c>
/// lines; the process exits with the number of its facts that failed.
/// </summary>
public partial class WeaponAudioChecks : Node
{
	private const int Second = SimConfig.TickRate;
	private const float FloorY = 0.5f;

	/// <summary>Open floor with 60 m clear in front of it (the mortar check's site), looking along −Z.</summary>
	private static readonly Vector3 Site = new(40f, FloorY + 0.1f, 50f);

	private IEnumerator<bool> _run;
	private int _failures;
	private int _facts;
	private bool _timedOut;

	public override void _Ready()
	{
		if (System.Environment.GetEnvironmentVariable("WEAPON_AUDIO_CHECK") != "listen")
		{
			SetPhysicsProcess(false);
			return;
		}

		ProcessMode = ProcessModeEnum.Always;
		_run = Listen();
		GD.Print("[checks] weapon audio check: listen");
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

	private static IEnumerable<bool> Tap(string action)
	{
		Input.ActionPress(action);
		foreach (bool _ in Wait(4)) yield return true;
		Input.ActionRelease(action);
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

	/// <summary>The 3D player of <paramref name="audio"/> named like <paramref name="prefix"/> nearest <paramref name="at"/>.</summary>
	private static AudioStreamPlayer3D Nearest(WeaponAudio audio, string prefix, Vector3 at)
	{
		AudioStreamPlayer3D best = null;
		foreach (Node child in audio.GetChildren())
		{
			if (child is AudioStreamPlayer3D player && player.Name.ToString().StartsWith(prefix, StringComparison.Ordinal)
				&& (best == null || player.GlobalPosition.DistanceTo(at) < best.GlobalPosition.DistanceTo(at)))
			{
				best = player;
			}
		}

		return best;
	}

	/// <summary>
	/// A round of <paramref name="weapon"/> in nobody's name from <paramref name="metersOut"/>
	/// ahead of the ear and <paramref name="side"/> to its right, flying straight back past it.
	/// </summary>
	private static Vector3 FireBack(Vector3 ear, byte weapon, float metersOut, float side)
	{
		Vector3 origin = ear + new Vector3(side, 0f, -metersOut);
		CombatManager.Instance.SpawnProjectile(Tick, OwnerId.None, weapon, origin, Vector3.Back, 0);
		return origin;
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

		WeaponAudio audio = WeaponAudio.Instance;
		Fact(audio != null && audio.GetParent() == CombatManager.Instance,
			$"weapon sounds are played here, by {audio?.GetPath()}");
		if (audio == null)
		{
			yield break;
		}

		Camera3D camera = me.GetViewport().GetCamera3D();
		Fact(camera != null, $"there is a camera to listen from ({camera?.GetPath()})");
		if (camera == null)
		{
			yield break;
		}

		Put(me, Site);
		foreach (bool _ in Wait(20)) yield return true;

		// ---- their own rifle ----
		if (local.EquippedDefinitionId != WeaponCatalog.Rifle)
		{
			foreach (bool _ in Tap("weapon_large")) yield return true;
			foreach (bool _ in Wait(30)) yield return true;
		}

		Fact(local.EquippedDefinitionId == WeaponCatalog.Rifle,
			$"the rifle is up ({WeaponCatalog.NameOf(local.EquippedDefinitionId)})");

		// Over the open floor and a little up, so nothing comes back past them.
		Put(me, Site, 0f, 0.05f);
		foreach (bool _ in Wait(4)) yield return true;
		int shots = audio.ShotsPlayed;
		int flybys = audio.FlybysPlayed;
		int fired = local.ShotsFired;
		for (int i = 0; i < 3; i++)
		{
			foreach (bool _ in Tap("fire")) yield return true;
		}

		foreach (bool _ in Wait(Second)) yield return true;
		int ownFired = local.ShotsFired - fired;
		Fact(ownFired >= 3 && audio.ShotsPlayed - shots == ownFired,
			$"each of their own rounds is heard once ({ownFired} fired, {audio.ShotsPlayed - shots} heard)");
		Fact(audio.FlybysPlayed == flybys, $"none of their own rounds is heard going past them ({audio.FlybysPlayed - flybys})");

		AudioStreamPlayer own = audio.GetNodeOrNull<AudioStreamPlayer>("OwnShot0");
		SoundVoice rifle = WeaponSounds.Shot(ShotKind.Rifle);
		int expectedBytes = 2 * (int)MathF.Round(rifle.Seconds * WeaponSounds.SampleRate);
		Fact(own?.Stream is AudioStreamWav { Format: AudioStreamWav.FormatEnum.Format16Bits, Stereo: false } wav
			&& wav.MixRate == WeaponSounds.SampleRate && wav.Data.Length == expectedBytes
			&& Mathf.IsEqualApprox(own.VolumeDb, rifle.VolumeDb + WeaponSounds.OwnShotDb),
			$"at the ear, flat, as the rifle's placeholder: {(own?.Stream as AudioStreamWav)?.Data.Length} bytes of 16-bit mono"
			+ $" at {(own?.Stream as AudioStreamWav)?.MixRate} Hz (expected {expectedBytes}), {F(own?.VolumeDb ?? float.NaN, "0.0")} dB");

		// ---- someone else's rifle, from 60 m, 2 m past the ear ----
		foreach (bool _ in Wait(Second)) yield return true;
		Vector3 ear = camera.GlobalPosition;
		Measured($"the ear at {V(ear)}");
		shots = audio.ShotsPlayed;
		flybys = audio.FlybysPlayed;
		uint firedAt = Tick;
		Vector3 muzzle = FireBack(ear, WeaponCatalog.Rifle, 60f, 2f);
		Fact(audio.ShotsPlayed == shots + 1, "a round fired by someone else is heard as a shot");
		AudioStreamPlayer3D shot = Nearest(audio, "Shot", muzzle);
		Fact(shot != null && shot.GlobalPosition.DistanceTo(muzzle) < 0.01f
			&& Mathf.IsEqualApprox(shot.UnitSize, rifle.UnitSizeMeters) && Mathf.IsEqualApprox(shot.MaxDistance, rifle.MaxDistanceMeters),
			$"in 3D at its muzzle {V(muzzle)} ({(shot != null ? V(shot.GlobalPosition) : "none")}, unit size"
			+ $" {F(shot?.UnitSize ?? 0f, "0")} m, heard to {F(shot?.MaxDistance ?? 0f, "0")} m)");

		foreach (bool _ in Until(() => audio.FlybysPlayed > flybys, Second)) yield return true;
		Fact(!_timedOut, $"it is heard going past {Tick - firedAt} ticks after it was fired"
			+ $" ({F(60f / WeaponCatalog.RoundFor(WeaponCatalog.Rifle).MuzzleVelocity * Second, "0.0")} at its muzzle velocity)");
		Vector3 closest = ear + new Vector3(2f, 0f, 0f);
		AudioStreamPlayer3D flyby = Nearest(audio, "Flyby", closest);
		Fact(flyby != null && flyby.GlobalPosition.DistanceTo(closest) < 0.25f,
			$"where it came closest, {V(closest)} ({(flyby != null ? V(flyby.GlobalPosition) : "none")},"
			+ $" {F(flyby?.GlobalPosition.DistanceTo(ear) ?? 0f)} m from the ear)");

		foreach (bool _ in Wait(Second)) yield return true;
		Fact(audio.FlybysPlayed == flybys + 1, $"and only once ({audio.FlybysPlayed - flybys})");

		// ---- 8 m off: a rifle round is not heard going past, a tank's shell is ----
		flybys = audio.FlybysPlayed;
		FireBack(ear, WeaponCatalog.Rifle, 60f, 8f);
		foreach (bool _ in Wait(Second)) yield return true;
		Fact(audio.FlybysPlayed == flybys, $"a rifle round 8 m off is not heard going past ({audio.FlybysPlayed - flybys})");

		flybys = audio.FlybysPlayed;
		FireBack(ear, WeaponCatalog.Cannon, 60f, 8f);
		foreach (bool _ in Until(() => audio.FlybysPlayed > flybys, Second)) yield return true;
		Fact(!_timedOut && audio.FlybysPlayed == flybys + 1, $"a tank's shell 8 m off is ({audio.FlybysPlayed - flybys})");

		// ---- past a shoulder from 5 m behind ----
		flybys = audio.FlybysPlayed;
		Vector3 behind = ear + new Vector3(1f, 0f, 5f);
		CombatManager.Instance.SpawnProjectile(Tick, OwnerId.None, WeaponCatalog.Rifle, behind, Vector3.Forward, 0);
		foreach (bool _ in Wait(Second)) yield return true;
		Fact(audio.FlybysPlayed == flybys,
			$"a round fired from 5 m behind past a shoulder is the shot, not a flyby ({audio.FlybysPlayed - flybys})");

		// ---- a hand grenade is thrown, not fired ----
		shots = audio.ShotsPlayed;
		int thrown = CombatManager.Instance.ShotsFired;
		byte grenades = local.Grenades.Count;
		Put(me, Site, 0f, 0.5f);
		foreach (bool _ in Wait(4)) yield return true;
		foreach (bool _ in Tap("throw_grenade")) yield return true;
		foreach (bool _ in Wait(10)) yield return true;
		Fact(local.Grenades.Count == grenades - 1 && audio.ShotsPlayed == shots,
			$"a hand grenade thrown ({grenades} → {local.Grenades.Count}, {CombatManager.Instance.ShotsFired - thrown} rounds)"
			+ $" is not heard as a shot ({audio.ShotsPlayed - shots})");

		// ---- every player taken back ----
		foreach (bool _ in Until(() => audio.Active == 0, 4 * Second)) yield return true;
		Fact(!_timedOut && audio.Evictions == 0,
			$"every sound is taken back when it is done ({audio.Active} playing, {audio.Evictions} cut short)");

		// ---- the console ----
		shots = audio.ShotsPlayed;
		Console("sound rifle");
		string line = LastConsoleLine();
		Fact(audio.ShotsPlayed == shots + 1 && line.Contains("rifle: a shot 20 m ahead (placeholder rifle)"),
			$"`sound rifle` plays the rifle's shot ({line})");

		flybys = audio.FlybysPlayed;
		Console("sound flyby shell");
		line = LastConsoleLine();
		Fact(audio.FlybysPlayed == flybys + 1 && line.Contains("shell going past"), $"`sound flyby shell` plays one ({line})");

		shots = audio.ShotsPlayed;
		Console("sound mortar launcher own");
		string spaced = LastConsoleLine();
		Console("sound 4x4_gun");
		line = LastConsoleLine();
		Fact(audio.ShotsPlayed == shots + 2 && spaced.Contains("mortar launcher: your own shot (placeholder mortar)")
			&& line.Contains("4x4 gun: a shot 20 m ahead (placeholder heavy_machine_gun)"),
			$"a weapon's name of two words is taken whole or with underscores ({spaced}; {line})");

		shots = audio.ShotsPlayed;
		Console("sound geopick");
		line = LastConsoleLine();
		Fact(audio.ShotsPlayed == shots && line.Contains("weapons:") && line.Contains("rifle") && !line.Contains("geopick"),
			$"`sound geopick` is refused, with the weapons that have a shot ({line})");
	}
}
