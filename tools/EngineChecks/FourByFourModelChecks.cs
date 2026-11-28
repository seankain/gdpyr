using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Gdpyr.Core;
using Gdpyr.Fps;
using Gdpyr.Sim;
using Godot;

namespace Gdpyr.EngineChecks;

/// <summary>
/// The 4x4 as drawn (docs/GROUND_VEHICLES.md §6.1, §12: Models/Vehicles/four_by_four/four_by_four.glb,
/// built by tools/blender/four_by_four.py, drawn by <see cref="CrewVehicle"/>), run by
/// <c>scripts/four-by-four-model-checks.sh</c> on a copy of the tree with this file added as an
/// autoload; not part of the game.
///
/// <c>FOUR_BY_FOUR_MODEL_CHECK=model</c>, headless, at a fixed 60 frames a second: a floor and the
/// vehicle test strip built here, and the real <see cref="CrewVehicle"/> stepped tick by tick.
/// Parked, it is drawn as the model, every wheel where the rules put it and on the floor, the
/// wishbones and coilovers at rest, the gun where the definition mounts it. Driven, every wheel
/// rolls forward by the distance it went; steered, the front pair turn about one centre and the
/// tandem does not, and the outside wheels roll further than the inside ones. Over the strip's
/// bumps every wheel rises and falls on its own spring, within its travel, the front axle first
/// and the tandem's two after it in turn, the wishbones following their knuckles and the
/// coilovers reaching their feet throughout. A wreck is drawn burnt to the last wheel; with no
/// model the greybox is drawn and its wheels roll too.
///
/// With <c>FOUR_BY_FOUR_MODEL_SHOTS</c> set to a directory and a display, it also saves pictures:
/// the gun turned; parked, from the front, the side and behind; steered; and articulated over a bump.
///
/// One line per fact, <c>PASS</c> or <c>FAIL</c>; the exit code is the number that failed.
/// </summary>
public partial class FourByFourModelChecks : Node
{
	private const float Dt = SimConfig.TickDelta;

	private const float FloorTop = 0.5f;

	private IEnumerator<bool> _run;
	private int _failures;
	private int _facts;
	private uint _tick;
	private string _shots;

	public override void _Ready()
	{
		if (System.Environment.GetEnvironmentVariable("FOUR_BY_FOUR_MODEL_CHECK") != "model")
		{
			SetPhysicsProcess(false);
			return;
		}

		_shots = System.Environment.GetEnvironmentVariable("FOUR_BY_FOUR_MODEL_SHOTS");
		ProcessMode = ProcessModeEnum.Always;
		_run = Model();
		GD.Print("[checks] 4x4 model check");
	}

	public override void _PhysicsProcess(double delta)
	{
		_tick++;
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
			GD.Print($"[checks] {_facts - _failures} of {_facts} facts hold");
			SetPhysicsProcess(false);
			GetTree().Quit(_failures);
		}
	}

	private void Fact(bool ok, string what)
	{
		_facts++;
		if (!ok)
		{
			_failures++;
		}

		GD.Print($"[checks] {(ok ? "PASS" : "FAIL")} {what}");
	}

	private static string F(float v, string format = "0.000") => v.ToString(format, CultureInfo.InvariantCulture);

	private static string Deg(float radians) => F(Mathf.RadToDeg(radians), "0.0") + "°";

	private static IEnumerable<bool> Wait(int ticks)
	{
		for (int i = 0; i < ticks; i++)
		{
			yield return true;
		}
	}

	private static float YawFacing(Vector3 direction) => Mathf.Atan2(-direction.X, -direction.Z);

	private static InputFrame Drive(uint tick, float throttle, float steer = 0f, bool handbrake = false) =>
		InputFrame.Create(tick, steer, -throttle, 0f, 0f, handbrake ? InputButtons.Jump : InputButtons.None);

	private void Box(Vector3 centre, Vector3 size)
	{
		var body = new StaticBody3D { CollisionLayer = CollisionLayers.World, CollisionMask = 0 };
		body.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = size } });
		if (!string.IsNullOrEmpty(_shots))
		{
			body.AddChild(new MeshInstance3D
			{
				Mesh = new BoxMesh { Size = size },
				MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.62f, 0.6f, 0.55f), Roughness = 1f },
			});
		}

		AddChild(body);
		body.GlobalPosition = centre;
	}

	private CrewVehicle Truck(Vector3 ground, float yaw, CrewVehicleDefinition definition = null)
	{
		definition ??= CrewVehicleCatalog.Definition(CrewVehicleCatalog.FourByFour);
		var truck = CrewVehicle.Create(definition, 0, ground + (Vector3.Up * definition.ToFourByFourTraits().RideHeightMeters), yaw);
		AddChild(truck);
		return truck;
	}

	/// <summary>A wheel's spring as drawn: from its corner of the chassis down to its centre, along the chassis's down [m].</summary>
	private static float DrawnSpring(CrewVehicle truck, int wheel) =>
		-(truck.Chassis.Transform.AffineInverse() * truck.WheelMount(wheel).Position).Y;

	/// <summary>How far right of the chassis's nose a wheel is drawn turned [rad].</summary>
	private static float DrawnSteer(CrewVehicle truck, int wheel)
	{
		Basis local = truck.Chassis.Basis.Inverse() * truck.WheelMount(wheel).Basis;
		Aim.Angles(-local.Z, out float yaw, out _);
		return -yaw;
	}

	/// <summary>
	/// How far each wishbone's outer end is from its ball joint on the knuckle, and each
	/// coilover's drawn end from its foot on the lower wishbone, the worst of the six wheels [m].
	/// </summary>
	private static (float Arms, float Shocks) Linkage(CrewVehicle truck)
	{
		Transform3D toChassis = truck.Chassis.Transform.AffineInverse();
		float arms = 0f, shocks = 0f;
		for (int wheel = 0; wheel < FourByFourSim.Wheels; wheel++)
		{
			CrewVehicle.WheelRig rig = truck.Rig(wheel);
			Transform3D mount = truck.WheelMount(wheel).Transform;
			Vector3 lowerEnd = rig.LowerArm.Transform * (rig.RestHub + rig.LowerJoint - rig.LowerPivot);
			Vector3 upperEnd = rig.UpperArm.Transform * (rig.RestHub + rig.UpperJoint - rig.UpperPivot);
			arms = Mathf.Max(arms, lowerEnd.DistanceTo(toChassis * (mount * rig.LowerJoint)));
			arms = Mathf.Max(arms, upperEnd.DistanceTo(toChassis * (mount * rig.UpperJoint)));
			Vector3 drawnFoot = rig.Shock.Transform * (Vector3.Down * rig.ShockRestLength);
			shocks = Mathf.Max(shocks, drawnFoot.DistanceTo(rig.ShockFootNow));
		}

		return (arms, shocks);
	}

	private static int CountMeshes(Node node, Func<MeshInstance3D, bool> where)
	{
		int n = node is MeshInstance3D mesh && where(mesh) ? 1 : 0;
		foreach (Node child in node.GetChildren())
		{
			n += CountMeshes(child, where);
		}

		return n;
	}

	// ---- pictures --------------------------------------------------------------------

	private Camera3D _camera;

	private void Stage()
	{
		if (GetTree().CurrentScene is { } menu)
		{
			menu.QueueFree();
		}

		var environment = new Godot.Environment
		{
			BackgroundMode = Godot.Environment.BGMode.Color,
			BackgroundColor = new Color(0.78f, 0.8f, 0.83f),
			AmbientLightSource = Godot.Environment.AmbientSource.Color,
			AmbientLightColor = new Color(0.75f, 0.77f, 0.8f),
			AmbientLightEnergy = 0.6f,
		};
		AddChild(new WorldEnvironment { Environment = environment });
		var sun = new DirectionalLight3D { LightEnergy = 1.2f, ShadowEnabled = true };
		AddChild(sun);
		sun.GlobalRotation = new Vector3(Mathf.DegToRad(-50f), Mathf.DegToRad(-35f), 0f);
		_camera = new Camera3D { Fov = 40f };
		AddChild(_camera);
		_camera.MakeCurrent();
	}

	private IEnumerable<bool> Shoot(string name, CrewVehicle truck, Vector3 from, Vector3 at)
	{
		if (string.IsNullOrEmpty(_shots) || _camera == null)
		{
			yield break;
		}

		CrewPose pose = truck.Pose;
		_camera.LookAtFromPosition(pose.ToWorld(from), pose.ToWorld(at), Vector3.Up);
		foreach (bool _ in Wait(4)) yield return true;
		Image image = GetViewport().GetTexture().GetImage();
		string path = Path.Combine(_shots, name + ".png");
		image.SavePng(path);
		GD.Print($"[checks] shot {path}");
	}

	// ---- the check -------------------------------------------------------------------

	private IEnumerator<bool> Model()
	{
		if (!string.IsNullOrEmpty(_shots))
		{
			Stage();
		}

		Box(new Vector3(0f, 0f, 0f), new Vector3(600f, 1f, 600f));
		var strip = GD.Load<PackedScene>("res://Scenes/Environment/VehicleTestStrip.tscn").Instantiate<Node3D>();
		AddChild(strip);
		strip.GlobalPosition = new Vector3(-20f, FloorTop, 2.5f);
		foreach (bool _ in Wait(5)) yield return true;

		FourByFourTraits traits = FourByFourTraits.Default;
		CrewVehicleDefinition definition = CrewVehicleCatalog.Definition(CrewVehicleCatalog.FourByFour);
		float r = traits.WheelRadiusMeters;
		float restSpring = traits.SpringRestMeters - traits.StaticCompressionMeters;

		// ---- parked ----------------------------------------------------------------------
		{
			CrewVehicle truck = Truck(new Vector3(-40f, FloorTop, -40f), 0.6f);
			for (int i = 0; i < 180; i++)
			{
				truck.Step(FourByFourSim.ParkedFrame(_tick), false, Dt);
				yield return true;
			}

			Fact(definition.Model != null, $"the 4x4's definition names a model ({definition.Model?.ResourcePath})");
			Fact(truck.Chassis?.FindChild("Body", false, false) is MeshInstance3D
				&& truck.Chassis.FindChild("Hull0", false, false) == null,
				"it is drawn as the model's body, not the greybox's boxes");

			int rigs = 0;
			float hubError = 0f, mountError = 0f, floorError = 0f, armSwing = 0f, shockStretch = 0f;
			for (int wheel = 0; wheel < FourByFourSim.Wheels; wheel++)
			{
				CrewVehicle.WheelRig rig = truck.Rig(wheel);
				if (rig == null)
				{
					continue;
				}

				rigs++;
				Vector3 expected = FourByFourSim.HardpointLocal(traits, wheel) + (Vector3.Down * restSpring);
				hubError = Mathf.Max(hubError, rig.RestHub.DistanceTo(expected));
				mountError = Mathf.Max(mountError, truck.WheelMount(wheel).Position.DistanceTo(rig.RestHub));
				floorError = Mathf.Max(floorError, Mathf.Abs(truck.WheelMount(wheel).GlobalPosition.Y - r - FloorTop));
				armSwing = Mathf.Max(armSwing, Mathf.Max(Mathf.Abs(rig.LowerArm.Rotation.Z), Mathf.Abs(rig.UpperArm.Rotation.Z)));
				shockStretch = Mathf.Max(shockStretch, Mathf.Abs((rig.ShockTop - rig.ShockFootNow).Length() - rig.ShockRestLength));
			}

			Fact(rigs == 6, $"all six wheels have their running gear: {rigs}");
			Fact(hubError < 0.005f, $"the model's hubs at rest are where the rules put the wheels at rest, within {F(hubError)} m");
			Fact(mountError < 0.01f && floorError < 0.01f,
				$"parked, every wheel is drawn at its rest place ({F(mountError)} m) on the floor ({F(floorError)} m)");
			Fact(armSwing < Mathf.DegToRad(0.5f) && shockStretch < 0.005f,
				$"parked, the wishbones are at rest ({Deg(armSwing)}) and so are the coilovers ({F(shockStretch)} m)");
			(float arms, float shocks) = Linkage(truck);
			Fact(arms < 0.005f && shocks < 0.001f, $"parked, the wishbones meet their joints ({F(arms)} m) and the coilovers their feet ({F(shocks)} m)");

			Node3D muzzle = truck.GunPitchNode?.FindChild("Muzzle", false, false) as Node3D;
			Fact(truck.GunYawNode != null && truck.GunYawNode.Position.DistanceTo(definition.GunMount) < 0.01f,
				$"the gun's pintle head is at the definition's mount ({truck.GunYawNode?.Position})");
			Fact(muzzle != null && muzzle.Position.DistanceTo(Vector3.Forward * definition.GunBarrelMeters) < 0.01f,
				$"its muzzle is {definition.GunBarrelMeters} m down the bore from the pivot ({muzzle?.Position})");
			truck.PointGun(Aim.Direction(Mathf.DegToRad(90f), Mathf.DegToRad(30f)));
			Vector3 bore = truck.GunPitchNode != null ? -(truck.GunYawNode.Basis * truck.GunPitchNode.Basis).Z : Vector3.Zero;
			Aim.Angles(bore, out float boreYaw, out float borePitch);
			Fact(Mathf.Abs(Mathf.AngleDifference(boreYaw, Mathf.DegToRad(90f))) < 0.01f && Mathf.Abs(borePitch - Mathf.DegToRad(30f)) < 0.01f,
				$"pointed 90° left and 30° up, the bore points {Deg(boreYaw)} and {Deg(borePitch)}, the shield turning with it");

			foreach (bool _ in Shoot("gun_turned", truck, new Vector3(4.8f, 2.2f, 6.0f), new Vector3(0f, 0.2f, 0f))) yield return true;
			truck.PointGun(Vector3.Forward);
			foreach (bool _ in Shoot("parked_front", truck, new Vector3(-4.2f, 1.2f, -6.5f), new Vector3(0f, 0.1f, 0f))) yield return true;
			foreach (bool _ in Shoot("parked_side", truck, new Vector3(8.5f, 0.4f, 0f), new Vector3(0f, 0.15f, 0f))) yield return true;
			foreach (bool _ in Shoot("parked_rear", truck, new Vector3(4.8f, 2.2f, 6.0f), new Vector3(0f, 0.2f, 0f))) yield return true;
			truck.QueueFree();
		}

		// ---- rolling and steering ----------------------------------------------------------
		{
			CrewVehicle truck = Truck(new Vector3(-100f, FloorTop, 60f), YawFacing(Vector3.Right));
			foreach (bool _ in Wait(2)) yield return true;
			var spun = new float[FourByFourSim.Wheels];
			var last = new float[FourByFourSim.Wheels];
			float travelled = 0f;
			for (int i = 0; i < 180; i++)
			{
				for (int w = 0; w < spun.Length; w++)
				{
					last[w] = truck.WheelSpin(w);
				}

				Vector3 before = truck.Drive.Position;
				truck.Step(Drive(_tick, 0.5f), true, Dt);
				yield return true;
				Vector3 moved = truck.Drive.Position - before;
				travelled += new Vector2(moved.X, moved.Z).Length();
				for (int w = 0; w < spun.Length; w++)
				{
					spun[w] += Mathf.AngleDifference(last[w], truck.WheelSpin(w));
				}
			}

			float expected = travelled / r;
			float worst = 0f;
			bool forward = true;
			foreach (float s in spun)
			{
				worst = Mathf.Max(worst, Mathf.Abs(-s - expected) / expected);
				forward &= s < 0f;
			}

			GD.Print($"[checks] measured: {F(travelled, "0.00")} m in 3 s; rolled {string.Join(", ", Array.ConvertAll(spun, s => F(-s, "0.00")))} rad against {F(expected, "0.00")}");
			Fact(forward, "driving ahead, every wheel rolls forward: about −X, its top towards the nose");
			Fact(worst < 0.03f, $"every wheel rolls by the distance driven over its radius, within {F(worst * 100f, "0.0")} %");

			// Steered right, slowly: the front pair turn, the right one more; the tandem stays straight.
			for (int w = 0; w < spun.Length; w++)
			{
				spun[w] = 0f;
			}

			for (int i = 0; i < 120; i++)
			{
				for (int w = 0; w < spun.Length; w++)
				{
					last[w] = truck.WheelSpin(w);
				}

				truck.Step(Drive(_tick, 0.25f, 1f), true, Dt);
				yield return true;
				for (int w = 0; w < spun.Length; w++)
				{
					spun[w] += Mathf.AngleDifference(last[w], truck.WheelSpin(w));
				}
			}

			float left = DrawnSteer(truck, 0);
			float right = DrawnSteer(truck, 1);
			float tandem = 0f;
			for (int w = 2; w < FourByFourSim.Wheels; w++)
			{
				tandem = Mathf.Max(tandem, Mathf.Abs(DrawnSteer(truck, w)));
			}

			GD.Print($"[checks] measured: at full right lock, {F(truck.Speed, "0.0")} m/s, steer {Deg(truck.Drive.Steer)}: front left {Deg(left)}, front right {Deg(right)}");
			Fact(left > 0f && right > left && truck.Drive.Steer > left && truck.Drive.Steer < right,
				$"steered right, both front wheels turn right, the inside one more ({Deg(right)}) and the outside one less ({Deg(left)}) than the step's {Deg(truck.Drive.Steer)}");
			Fact(Mathf.Abs(left - FourByFourSim.WheelSteer(traits, 0, truck.Drive.Steer)) < 1e-4f
				&& Mathf.Abs(right - FourByFourSim.WheelSteer(traits, 1, truck.Drive.Steer)) < 1e-4f,
				"they are drawn at the rules' Ackermann angles, which meet at one centre");
			Fact(tandem < 1e-5f, "the tandem's four wheels do not steer");
			float outside = -(spun[0] + spun[2] + spun[4]);
			float inside = -(spun[1] + spun[3] + spun[5]);
			Fact(outside > inside * 1.05f, $"turning right, the left wheels roll further than the right ones ({F(outside, "0.00")} against {F(inside, "0.00")} rad)");
			foreach (bool _ in Shoot("steered", truck, new Vector3(0.6f, 0.9f, -6.5f), new Vector3(0f, -0.25f, -1.2f))) yield return true;

			for (int i = 0; i < 60; i++)
			{
				truck.Step(Drive(_tick, 0f, -1f, true), true, Dt);
				yield return true;
			}

			Fact(DrawnSteer(truck, 1) < 0f && DrawnSteer(truck, 0) < DrawnSteer(truck, 1),
				$"steered left, they turn left, the left one, inside, more ({Deg(-DrawnSteer(truck, 0))} and {Deg(-DrawnSteer(truck, 1))})");
			(float steeredArms, _) = Linkage(truck);
			Fact(steeredArms < 0.01f, $"at full lock the wishbones still meet the knuckles' joints, within {F(steeredArms)} m: the wheel turns about them");
			truck.QueueFree();
		}

		// ---- the strip's bumps ---------------------------------------------------------------
		{
			CrewVehicle truck = Truck(new Vector3(-45f, FloorTop, 0f), YawFacing(Vector3.Right));
			var lowest = new float[FourByFourSim.Wheels];
			var highest = new float[FourByFourSim.Wheels];
			var firstUp = new int[FourByFourSim.Wheels];
			float minArm = 0f, maxArm = 0f, arms = 0f, shocks = 0f, stretch = 0f;
			for (int w = 0; w < FourByFourSim.Wheels; w++)
			{
				lowest[w] = float.MaxValue;
				highest[w] = float.MinValue;
				firstUp[w] = -1;
			}

			float shortest = traits.SpringRestMeters - traits.SpringTravelMeters - 1e-4f;
			float longest = traits.SpringRestMeters + 1e-4f;
			bool shot = false;
			for (int i = 0; i < 60 * 8 && truck.Drive.Position.X < -4f; i++)
			{
				truck.Step(Drive(_tick, 6f / 18f), true, Dt);
				yield return true;
				for (int w = 0; w < FourByFourSim.Wheels; w++)
				{
					float spring = DrawnSpring(truck, w);
					lowest[w] = Mathf.Min(lowest[w], spring);
					highest[w] = Mathf.Max(highest[w], spring);
					// The wheel itself climbing a bump, not the chassis's pitch squeezing its spring.
					if (firstUp[w] < 0 && truck.WheelMount(w).GlobalPosition.Y > FloorTop + r + 0.05f)
					{
						firstUp[w] = i;
					}
				}

				minArm = Mathf.Min(minArm, truck.Rig(0).LowerArm.Rotation.Z);
				maxArm = Mathf.Max(maxArm, truck.Rig(0).LowerArm.Rotation.Z);
				for (int w = 0; w < FourByFourSim.Wheels; w++)
				{
					stretch = Mathf.Max(stretch, Mathf.Abs(truck.Rig(w).LowerArm.Basis.X.Length() - 1f));
					stretch = Mathf.Max(stretch, Mathf.Abs(truck.Rig(w).UpperArm.Basis.X.Length() - 1f));
				}
				(float a, float s) = Linkage(truck);
				arms = Mathf.Max(arms, a);
				shocks = Mathf.Max(shocks, s);

				if (!shot && !string.IsNullOrEmpty(_shots) && DrawnSpring(truck, 2) < restSpring - 0.10f)
				{
					shot = true;
					foreach (bool _ in Shoot("articulated", truck, new Vector3(-6.0f, 0.6f, 1.2f), new Vector3(0f, -0.1f, 0.3f))) yield return true;
				}
			}

			var travel = new string[FourByFourSim.Wheels];
			float least = float.MaxValue;
			bool within = true;
			for (int w = 0; w < FourByFourSim.Wheels; w++)
			{
				travel[w] = $"{w}: {F(lowest[w])}..{F(highest[w])} m from tick {firstUp[w]}";
				least = Mathf.Min(least, highest[w] - lowest[w]);
				within &= lowest[w] >= shortest && highest[w] <= longest;
			}

			GD.Print($"[checks] measured: over the bumps at 6 m/s, springs {string.Join("; ", travel)}");
			Fact(least > 0.08f, $"over the bumps every wheel rides up and down on its own spring, by {F(least)} m at least");
			Fact(within, "and never past its free length nor its bump stop");
			Fact(firstUp[0] >= 0 && firstUp[1] >= 0 && firstUp[0] < firstUp[2] && firstUp[1] < firstUp[3]
				&& firstUp[2] < firstUp[4] && firstUp[3] < firstUp[5],
				$"the wheels climb the first bump the front axle first, then the tandem's leading axle, then its trailing one (ticks {string.Join(", ", firstUp)})");
			Fact(maxArm - minArm > Mathf.DegToRad(8f), $"the front left lower wishbone swings {Deg(minArm)}..{Deg(maxArm)} with its wheel");
			Fact(arms < 0.005f && stretch < 0.15f,
				$"throughout, every wishbone's end is within {F(arms)} m of its ball joint, drawn at most {F(stretch * 100f, "0.0")} % long or short to reach it");
			Fact(shocks < 0.001f, $"throughout, every coilover reaches its foot, within {F(shocks)} m");
			truck.QueueFree();
		}

		// ---- the wreck and the greybox ---------------------------------------------------------
		{
			CrewVehicle wreck = Truck(new Vector3(-60f, FloorTop, -80f), 0f);
			foreach (bool _ in Wait(2)) yield return true;
			wreck.PresentDestroyed(effect: false);
			int meshes = CountMeshes(wreck, _ => true);
			int burnt = CountMeshes(wreck, m => m.MaterialOverride != null);
			Fact(meshes > 30 && burnt == meshes, $"a wreck is drawn burnt to the last wheel: {burnt} of {meshes} meshes");
			wreck.QueueFree();

			var plain = (CrewVehicleDefinition)definition.Duplicate();
			plain.Model = null;
			CrewVehicle grey = Truck(new Vector3(-60f, FloorTop, -100f), 0f, plain);
			foreach (bool _ in Wait(2)) yield return true;
			float before = grey.WheelSpin(4);
			for (int i = 0; i < 30; i++)
			{
				grey.Step(Drive(_tick, 1f), true, Dt);
				yield return true;
			}

			Fact(grey.Chassis.FindChild("Hull0", false, false) != null && grey.Rig(0) == null && grey.WheelMount(5) != null
				&& Mathf.Abs(Mathf.AngleDifference(before, grey.WheelSpin(4))) > 0.1f,
				"with no model the greybox is drawn, on six wheels that roll");
			grey.QueueFree();
		}
	}
}
