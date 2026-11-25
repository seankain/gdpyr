using System;
using System.Collections.Generic;
using System.Globalization;
using Gdpyr.Characters;
using Gdpyr.Core;
using Gdpyr.Effects;
using Gdpyr.Fps;
using Gdpyr.Match;
using Gdpyr.Net;
using Gdpyr.Sim;
using Godot;

namespace Gdpyr.EngineChecks;

/// <summary>
/// The launcher's animated viewmodel in the game (docs/VIEWMODELS.md, Scripts/Fps/AnimatedViewmodel.cs),
/// run by <c>scripts/viewmodel-checks.sh</c> on a copy of the tree with this file added as an
/// autoload; not part of the game.
///
/// A listen host on <c>Test.tscn</c> at <c>--fixed-fps 60</c>, its own player given the launcher
/// and driven through the input map: the viewmodel is the glTF scene and not the mesh, drawn at
/// its own field of view, shadowless; it idles, kicks when a round leaves and settles; the next
/// pull reloads it, and the barrel is open half way through and shut at the end with a round in;
/// the muzzle flash is drawn where the barrel is drawn. With <c>VIEWMODEL_SHOTS</c> and a display
/// it photographs the hip, the shot, the reload and the sights, and then — for the comparison in
/// docs/VIEWMODELS.md §2 — what the camera sees of the body it looks out of when that is drawn.
/// One line per fact, <c>PASS</c> or <c>FAIL</c>; the process exits with the number that failed.
/// </summary>
public partial class ViewmodelChecks : Node
{
	private const int Second = SimConfig.TickRate;
	private const float FloorY = 0.5f;
	private static readonly Vector3 Site = new(40f, FloorY + 0.1f, 50f);

	private IEnumerator<bool> _run;
	private int _failures;
	private int _facts;
	private bool _timedOut;
	private string _shots;

	public override void _Ready()
	{
		if (System.Environment.GetEnvironmentVariable("VIEWMODEL_CHECK") != "listen")
		{
			SetPhysicsProcess(false);
			return;
		}

		_shots = System.Environment.GetEnvironmentVariable("VIEWMODEL_SHOTS");
		ProcessMode = ProcessModeEnum.Always;
		_run = Listen();
		GD.Print("[checks] viewmodel check: listen");
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

	private static void Put(fps_controller me, Vector3 at, float yaw = 0f, float pitch = 0f)
	{
		me.Teleport(new Transform3D(new Basis(Vector3.Up, yaw), at));
		Sampler.Align(yaw, pitch);
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

	/// <summary>A bone's turn away from its rest [deg].</summary>
	private static float Turned(Skeleton3D skeleton, string bone)
	{
		int i = skeleton.FindBone(bone);
		if (i < 0)
		{
			return float.NaN;
		}

		Quaternion rest = skeleton.GetBoneRest(i).Basis.GetRotationQuaternion();
		Quaternion pose = skeleton.GetBonePoseRotation(i);
		return Mathf.RadToDeg(rest.AngleTo(pose));
	}

	/// <summary>Where on the screen a point is drawn by a material at <paramref name="fovDegrees"/>, the camera keeping its height.</summary>
	private static Vector2 DrawnAt(Camera3D camera, Vector3 world, float fovDegrees)
	{
		Vector3 p = camera.GlobalTransform.AffineInverse() * world;
		Vector2 size = camera.GetViewport().GetVisibleRect().Size;
		float t = Mathf.Tan(Mathf.DegToRad(fovDegrees) * 0.5f);
		float x = p.X / -p.Z / (t * size.X / size.Y);
		float y = p.Y / -p.Z / t;
		return new Vector2((x * 0.5f + 0.5f) * size.X, (0.5f - y * 0.5f) * size.Y);
	}

	private static MuzzleFlash Newest(MuzzleEffects muzzles)
	{
		MuzzleFlash newest = null;
		foreach (Node child in muzzles.GetChildren())
		{
			if (child is MuzzleFlash { Visible: true } flash)
			{
				newest = flash;
			}
		}

		return newest;
	}

	private static IEnumerable<MeshInstance3D> Meshes(Node node)
	{
		if (node is MeshInstance3D mesh)
		{
			yield return mesh;
		}

		foreach (Node child in node.GetChildren())
		{
			foreach (MeshInstance3D m in Meshes(child))
			{
				yield return m;
			}
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
		Camera3D camera = me.GetViewport().GetCamera3D();
		WeaponInit view = me.Viewmodel;
		Fact(camera != null && view != null, $"there is a camera ({camera?.GetPath()}) and a viewmodel ({view?.GetPath()})");
		if (camera == null || view == null)
		{
			yield break;
		}

		// The launcher, with spare rounds, as a locker would give it.
		local.ApplyKit(WeaponCatalog.Sanitize(new LoadoutSelection
		{
			Melee = WeaponCatalog.Hammer,
			Sidearm = WeaponCatalog.Pistol,
			Large = WeaponCatalog.Launcher,
			Magazines = 2,
		}));
		Put(me, Site, 0f, -0.05f);
		foreach (bool _ in Wait(20)) yield return true;
		if (local.EquippedDefinitionId != WeaponCatalog.Launcher)
		{
			foreach (bool _ in Tap("weapon_large")) yield return true;
			foreach (bool _ in Wait(30)) yield return true;
		}

		Fact(local.EquippedDefinitionId == WeaponCatalog.Launcher,
			$"the launcher is up ({WeaponCatalog.NameOf(local.EquippedDefinitionId)}, {local.Equipped.Ammo} in it, {local.Equipped.Reserve} spare)");

		// ---- the scene, not the mesh ----
		AnimatedViewmodel animated = view.Animated;
		Fact(animated != null && animated.Root.IsVisibleInTree() && !view.WeaponMesh.Visible,
			$"the launcher is drawn by its viewmodel scene ({animated?.Root.GetPath()}), not the mesh");
		if (animated == null)
		{
			yield break;
		}

		Skeleton3D skeleton = animated.Skeleton;
		var bones = new List<string>();
		foreach (string bone in new[] { "Weapon", "Barrel", "Round", "Latch", "Trigger", "Palm.L", "Palm.R", "Index3.R" })
		{
			if (skeleton == null || skeleton.FindBone(bone) < 0)
			{
				bones.Add(bone);
			}
		}

		Fact(skeleton != null && bones.Count == 0,
			$"one skeleton holds the arms and the weapon ({skeleton?.GetBoneCount()} bones{(bones.Count > 0 ? "; missing " + string.Join(", ", bones) : "")})");
		Fact(animated.Muzzle != null, $"the scene has its muzzle ({animated.Muzzle?.GetPath()})");

		int surfaces = 0;
		int overridden = 0;
		int shadowed = 0;
		foreach (MeshInstance3D mesh in Meshes(animated.Root))
		{
			if (mesh.CastShadow != GeometryInstance3D.ShadowCastingSetting.Off)
			{
				shadowed++;
			}

			for (int i = 0; i < mesh.Mesh.GetSurfaceCount(); i++)
			{
				surfaces++;
				if (mesh.GetSurfaceOverrideMaterial(i) is BaseMaterial3D { UseFovOverride: true, UseZClipScale: true } m
					&& Mathf.IsEqualApprox(m.FovOverride, animated.FovDegrees))
				{
					overridden++;
				}
			}
		}

		Fact(surfaces > 0 && overridden == surfaces && shadowed == 0,
			$"every surface is drawn at {F(animated.FovDegrees, "0")} degrees and pulled in from walls, none casts a shadow ({overridden} of {surfaces}; {shadowed} casting)");

		foreach (bool _ in Wait(10)) yield return true;
		Fact(animated.Clip == "idle", $"held, it idles ({animated.Clip})");
		Shot("launcher_hip");

		// ---- one round ----
		MuzzleEffects muzzles = MuzzleEffects.Instance;
		Vector2 barrel = DrawnAt(camera, animated.Muzzle.GlobalPosition, animated.FovDegrees);
		int fired = local.ShotsFired;
		int drawn = muzzles?.Drawn ?? 0;
		Input.ActionPress("fire");
		foreach (bool _ in Until(() => local.ShotsFired > fired, Second)) yield return true;
		Fact(!_timedOut, "the launcher fires");
		MuzzleFlash flash = muzzles != null ? Newest(muzzles) : null;
		if (flash != null && muzzles.Drawn > drawn)
		{
			Vector2 at = camera.UnprojectPosition(flash.GlobalPosition);
			Fact(at.DistanceTo(barrel) < 12f,
				$"its flash is drawn on the end of the barrel as drawn ({F(at.X, "0")}, {F(at.Y, "0")} against {F(barrel.X, "0")}, {F(barrel.Y, "0")})");
		}
		else
		{
			Fact(false, "a flash is drawn for the round");
		}

		foreach (bool _ in Wait(4)) yield return true;
		Input.ActionRelease("fire");
		float kick = Turned(skeleton, "Weapon");
		Fact(animated.Clip == "fire" && kick > 4f, $"it kicks: the fire clip, the weapon {F(kick, "0.0")} degrees up");
		Shot("launcher_fire");
		foreach (bool _ in Wait(Second)) yield return true;
		kick = Turned(skeleton, "Weapon");
		Fact(animated.Clip == "idle" && kick < 1.5f, $"and settles ({animated.Clip}, {F(kick, "0.0")} degrees)");
		Fact(local.Equipped.Ammo == 0, $"the launcher is empty ({local.Equipped.Ammo})");

		// ---- the next pull reloads it ----
		uint next = local.Equipped.NextFireTick;
		foreach (bool _ in Until(() => NetworkManager.Instance.Tick >= next, 3 * Second)) yield return true;
		foreach (bool _ in Press("fire", 4)) yield return true;
		foreach (bool _ in Until(() => local.Equipped.IsReloading, Second)) yield return true;
		Fact(!_timedOut, "an empty launcher's next pull reloads it");
		uint end = local.Equipped.ReloadEndTick;
		uint start = end - (uint)local.EquippedStats.ReloadTicks;
		var reloadShots = new (float At, string Name)[]
		{
			(0.14f, "launcher_reload_1_latch"), (0.26f, "launcher_reload_2_open"), (0.32f, "launcher_reload_3_eject"),
			(0.55f, "launcher_reload_4_fetch"), (0.64f, "launcher_reload_5_insert"), (0.81f, "launcher_reload_6_close"),
		};
		float widest = 0f;
		foreach ((float at, string name) in reloadShots)
		{
			uint tick = start + (uint)(at * local.EquippedStats.ReloadTicks);
			foreach (bool _ in Until(() => NetworkManager.Instance.Tick >= tick, 3 * Second)) yield return true;
			yield return true;
			widest = Mathf.Max(widest, Turned(skeleton, "Barrel"));
			Measured($"reload at {F(at)}: {animated.Clip}, barrel {F(Turned(skeleton, "Barrel"), "0.0")} degrees, round {F(skeleton.GetBonePoseScale(skeleton.FindBone("Round")).X)} drawn");
			Shot(name);
		}

		Fact(animated.Clip == "reload" && widest > 40f, $"part way through, the reload clip has the barrel open ({F(widest, "0.0")} degrees)");
		foreach (bool _ in Until(() => !local.Equipped.IsReloading, 4 * Second)) yield return true;
		foreach (bool _ in Wait(5)) yield return true;
		Fact(local.Equipped.Ammo == 1 && animated.Clip == "idle" && Turned(skeleton, "Barrel") < 1f,
			$"at its end the barrel is shut on a round and the launcher idles ({local.Equipped.Ammo} in it, {animated.Clip}, barrel {F(Turned(skeleton, "Barrel"), "0.0")} degrees)");

		// ---- the sights ----
		Input.ActionPress("aim");
		foreach (bool _ in Wait(Second)) yield return true;
		// The bore 66 mm under the line of sight, the muzzle 0.60 m out along it (tools/blender/launcher.py).
		Vector3 bore = camera.GlobalTransform.AffineInverse() * animated.Muzzle.GlobalPosition;
		Fact(animated.AimWeight > 0.99f && Mathf.Abs(bore.X) < 0.01f && Mathf.Abs(bore.Y + 0.066f) < 0.01f
				&& Mathf.Abs(bore.Z + 0.601f) < 0.02f,
			$"with the sights up the weapon is on the line of sight (aim {F(animated.AimWeight)}; muzzle at ({F(bore.X, "0.000")}, {F(bore.Y, "0.000")}, {F(bore.Z, "0.000")}) from the eye)");
		Shot("launcher_aimed");
		fired = local.ShotsFired;
		Input.ActionPress("fire");
		foreach (bool _ in Until(() => local.ShotsFired > fired, Second)) yield return true;
		foreach (bool _ in Wait(3)) yield return true;
		Input.ActionRelease("fire");
		Fact(!_timedOut && animated.Clip == "fire" && animated.AimWeight > 0.99f,
			$"a round from the sights plays the shot from the sights ({animated.Clip}, aim {F(animated.AimWeight)})");
		Shot("launcher_aimed_fire");
		Input.ActionRelease("aim");
		foreach (bool _ in Wait(Second)) yield return true;
		Fact(animated.AimWeight < 0.01f, $"and the sights come down ({F(animated.AimWeight)})");

		// ---- the other way: the body the camera is in, drawn ----
		HumanoidRig body = me.Humanoid;
		if (body != null)
		{
			Node3D rig = me.GetNode<Node3D>("CameraController/Camera3D/WeaponRig");
			rig.Visible = false;
			body.SetShadowCasting(GeometryInstance3D.ShadowCastingSetting.On);
			foreach (bool _ in Wait(10)) yield return true;
			Shot("body_from_eye_hip");
			Input.ActionPress("aim");
			foreach (bool _ in Wait(Second)) yield return true;
			Shot("body_from_eye_aimed");
			Put(me, Site, 0f, -1.3f);
			foreach (bool _ in Wait(10)) yield return true;
			Shot("body_from_eye_looking_down");
			Input.ActionRelease("aim");
			Put(me, Site, 0f, 0f);

			// From outside, aiming, to say where the eye is in the body.
			Input.ActionPress("aim");
			foreach (bool _ in Wait(20)) yield return true;
			Vector3 eye = camera.GlobalPosition;
			var outside = new Camera3D { Fov = 50f };
			me.GetParent().AddChild(outside);
			outside.GlobalPosition = eye + new Vector3(1.6f, 0.1f, -1.2f);
			outside.LookAt(eye + new Vector3(0f, -0.3f, 0f), Vector3.Up);
			outside.Current = true;
			var marker = new MeshInstance3D
			{
				Mesh = new SphereMesh { Radius = 0.03f, Height = 0.06f },
				MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(1f, 0.85f, 0f), ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded },
			};
			me.GetParent().AddChild(marker);
			marker.GlobalPosition = eye;
			foreach (bool _ in Wait(5)) yield return true;
			Skeleton3D bodySkeleton = body.FindChild("Skeleton3D", true, false) as Skeleton3D;
			int head = bodySkeleton?.FindBone("Head") ?? -1;
			if (head >= 0)
			{
				Vector3 headAt = bodySkeleton.GlobalTransform * bodySkeleton.GetBoneGlobalPose(head).Origin;
				Measured($"the eye is {F(eye.Y - headAt.Y, "0.00")} m above and {F((eye - headAt).Slide(Vector3.Up).Length(), "0.00")} m across from the mannequin's head bone");
			}

			Shot("body_from_outside_aimed");
			Input.ActionRelease("aim");
			outside.QueueFree();
			marker.QueueFree();
			camera.Current = true;
			body.SetShadowCasting(GeometryInstance3D.ShadowCastingSetting.ShadowsOnly);
			rig.Visible = true;
		}
	}
}
