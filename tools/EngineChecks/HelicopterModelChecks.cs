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
/// The helicopter as drawn (docs/GROUND_VEHICLES.md §17: Models/Vehicles/helicopter/helicopter.glb,
/// built by tools/blender/helicopter.py, drawn by <see cref="CrewVehicle"/>), run by
/// <c>scripts/helicopter-model-checks.sh</c> on a copy of the tree with this file added as an
/// autoload; not part of the game.
///
/// <c>HELICOPTER_MODEL_CHECK=model</c>, headless, at a fixed 60 frames a second: a floor built
/// here and the real <see cref="CrewVehicle"/>. Parked, it is drawn as the model, standing on
/// its wheels; each seat of <c>Vehicles/helicopter.tres</c> on the cushion the model draws for
/// it, the pilot's inside the hull's boxes and the two outer seats outside them; the rotor's hub
/// over the origin and above the hull, the fan in the fenestron's box. Its rotors stand still
/// with the rotor stopped and turn at their rates, the main one anticlockwise from above, as it
/// spins up. Tilted, and flown, every seat stays on its drawn cushion where the rules put a
/// seated body. A wreck is drawn burnt to the last mesh; with no model the greybox is drawn.
///
/// With <c>HELICOPTER_MODEL_SHOTS</c> set to a directory and a display, it also saves pictures:
/// parked from the front, the side and behind; from the pilot's chase camera; from the left
/// seat; and in flight, nose down.
///
/// One line per fact, <c>PASS</c> or <c>FAIL</c>; the exit code is the number that failed.
/// </summary>
public partial class HelicopterModelChecks : Node
{
	private const float Dt = SimConfig.TickDelta;

	private const float FloorTop = 0.5f;

	/// <summary>How fast the presentation turns the main rotor at full speed, and the tail's multiple of it (CrewVehicle).</summary>
	private const float RotorRadiansPerSecond = 28f;

	private const float TailRotorMultiple = 1.7f;

	private IEnumerator<bool> _run;
	private int _failures;
	private int _facts;
	private uint _tick;
	private string _shots;

	public override void _Ready()
	{
		if (System.Environment.GetEnvironmentVariable("HELICOPTER_MODEL_CHECK") != "model")
		{
			SetPhysicsProcess(false);
			return;
		}

		_shots = System.Environment.GetEnvironmentVariable("HELICOPTER_MODEL_SHOTS");
		ProcessMode = ProcessModeEnum.Always;
		_run = Model();
		GD.Print("[checks] helicopter model check");
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

	private static void Measured(string what) => GD.Print($"[checks] measured: {what}");

	private static string F(float v, string format = "0.000") => v.ToString(format, CultureInfo.InvariantCulture);

	private static string V(Vector3 v) => $"({F(v.X, "0.00")}, {F(v.Y, "0.00")}, {F(v.Z, "0.00")})";

	private static string Deg(float radians) => F(Mathf.RadToDeg(radians), "0.0") + "°";

	private static IEnumerable<bool> Wait(int ticks)
	{
		for (int i = 0; i < ticks; i++)
		{
			yield return true;
		}
	}

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

	private CrewVehicle Helicopter(Vector3 at, float yaw, CrewVehicleDefinition definition = null)
	{
		definition ??= CrewVehicleCatalog.Definition(CrewVehicleCatalog.Helicopter);
		var heli = CrewVehicle.Create(definition, 0, at, yaw);
		AddChild(heli);
		return heli;
	}

	/// <summary>The empty the model marks seat <paramref name="seat"/>'s cushion with: the pilot's, or the outer seat on its side.</summary>
	private static string SeatMarker(in CrewSeat seat) =>
		seat.Role == SeatRole.Driver ? "PilotSeat" : seat.Capsule.Center.X < 0f ? "LeftSeat" : "RightSeat";

	/// <summary>Where a seat's capsule rests: the bottom of its lower hemisphere, in the vehicle's frame.</summary>
	private static Vector3 Foot(in CrewSeat seat) => seat.Capsule.Bottom + (Vector3.Down * seat.Capsule.Radius);

	private static int CountMeshes(Node node, Func<MeshInstance3D, bool> where)
	{
		int n = node is MeshInstance3D mesh && where(mesh) ? 1 : 0;
		foreach (Node child in node.GetChildren())
		{
			n += CountMeshes(child, where);
		}

		return n;
	}

	/// <summary>The box round <paramref name="hull"/>'s boxes, in the vehicle's frame.</summary>
	private static Aabb HullBounds(HullBox[] hull)
	{
		var bounds = new Aabb(hull[0].Center - hull[0].HalfExtents, hull[0].HalfExtents * 2f);
		foreach (HullBox box in hull)
		{
			bounds = bounds.Merge(new Aabb(box.Center - box.HalfExtents, box.HalfExtents * 2f));
		}

		return bounds;
	}

	private static bool InsideAny(HullBox[] hull, Vector3 point)
	{
		foreach (HullBox box in hull)
		{
			Vector3 d = (point - box.Center).Abs();
			if (d.X <= box.HalfExtents.X && d.Y <= box.HalfExtents.Y && d.Z <= box.HalfExtents.Z)
			{
				return true;
			}
		}

		return false;
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

	private IEnumerable<bool> Shoot(string name, CrewVehicle heli, Vector3 from, Vector3 at, float fov = 40f)
	{
		if (string.IsNullOrEmpty(_shots) || _camera == null)
		{
			yield break;
		}

		CrewPose pose = heli.Pose;
		_camera.Fov = fov;
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
		foreach (bool _ in Wait(5)) yield return true;

		CrewVehicleDefinition definition = CrewVehicleCatalog.Definition(CrewVehicleCatalog.Helicopter);
		CrewSeat[] seats = definition.ToSeats();
		HullBox[] hull = definition.ToHull();

		// ---- parked ----------------------------------------------------------------------
		{
			ulong before = Time.GetTicksUsec();
			CrewVehicle heli = Helicopter(new Vector3(-40f, FloorTop, -40f), 0.6f);
			ulong built = Time.GetTicksUsec() - before;
			Measured($"made with its model in {F(built / 1000f, "0.0")} ms");
			foreach (bool _ in Wait(30)) yield return true;

			Fact(definition.Model != null, $"the helicopter's definition names a model ({definition.Model?.ResourcePath})");
			Node3D chassis = heli.Chassis;
			var body = chassis?.FindChild("Body", false, false) as MeshInstance3D;
			Fact(body != null && heli.RotorNode?.Name == "Rotor" && heli.TailRotorNode?.Name == "TailRotor"
				&& chassis.FindChild("Hull0", false, false) == null,
				"it is drawn as the model's body and its two rotors, not the greybox's boxes");

			// Standing on its wheels: the body's lowest point is the origin's height, and the
			// origin is on the floor.
			Aabb drawn = body?.GetAabb() ?? default;
			Measured($"the body spans x {F(drawn.Position.X, "0.00")}..{F(drawn.End.X, "0.00")}, y {F(drawn.Position.Y)}..{F(drawn.End.Y, "0.00")},"
				+ $" z {F(drawn.Position.Z, "0.00")}..{F(drawn.End.Z, "0.00")}");
			Fact(heli.Grounded && Mathf.Abs(heli.Flight.Position.Y - FloorTop) < 0.01f && Mathf.Abs(drawn.Position.Y) < 0.01f,
				$"parked, it stands on its wheels on the floor: their lowest {F(drawn.Position.Y)} m from its origin, which is {F(heli.Flight.Position.Y - FloorTop)} m above the floor");

			// The hull's boxes are where the model is.
			Aabb boxes = HullBounds(hull);
			Measured($"the hull's boxes span x {F(boxes.Position.X, "0.00")}..{F(boxes.End.X, "0.00")}, y {F(boxes.Position.Y, "0.00")}..{F(boxes.End.Y, "0.00")},"
				+ $" z {F(boxes.Position.Z, "0.00")}..{F(boxes.End.Z, "0.00")}");
			Fact(drawn.Position.Z > boxes.Position.Z - 0.6f && drawn.End.Z < boxes.End.Z + 0.05f && drawn.End.Y < boxes.End.Y + 0.05f,
				"the hull's boxes reach from the nose — all but the chin gun's barrel — to the end of the tail and the top of the fin");

			// Every seat on the cushion the model draws for it.
			float seatError = 0f;
			int found = 0;
			bool pilotInside = true, outerOutside = true;
			for (int i = 0; i < seats.Length; i++)
			{
				var marker = body?.FindChild(SeatMarker(seats[i]), false, false) as Node3D;
				if (marker == null)
				{
					continue;
				}

				found++;
				seatError = Mathf.Max(seatError, marker.Position.DistanceTo(Foot(seats[i])));
				Measured($"seat {i} ({seats[i].Role}): the model's {marker.Name} at {V(marker.Position)}, the capsule's foot at {V(Foot(seats[i]))}");
				if (seats[i].Enclosed)
				{
					pilotInside &= InsideAny(hull, marker.Position) && InsideAny(hull, seats[i].Eye);
				}
				else
				{
					outerOutside &= !InsideAny(hull, marker.Position) && !InsideAny(hull, seats[i].Eye);
				}
			}

			Fact(seats.Length == 3 && found == 3, $"the model marks a cushion for each of the definition's {seats.Length} seats ({found})");
			Fact(seatError < 0.01f, $"each seat's capsule rests on its cushion, within {F(seatError)} m");
			Fact(pilotInside && outerOutside, "the pilot's seat and eye are inside the hull's boxes; the outer seats' are outside them");

			// The rotors where they turn.
			Vector3 hub = heli.RotorNode?.Position ?? Vector3.Zero;
			Vector3 fan = heli.TailRotorNode?.Position ?? Vector3.Zero;
			Measured($"the rotor's hub at {V(hub)}, the fan at {V(fan)}");
			HullBox cabin = Array.Find(hull, b => Mathf.Abs(b.Center.Z) <= b.HalfExtents.Z);
			Fact(new Vector2(hub.X, hub.Z).Length() < 0.01f && hub.Y > cabin.Center.Y + cabin.HalfExtents.Y,
				"the main rotor's hub is over the origin, which the helicopter tilts about, and above the cabin's box");
			Fact(InsideAny(new[] { hull[^1] }, fan), "the tail rotor is in the fenestron's box, the last of the hull's");
			Fact(heli.RotorNode?.Rotation.LengthSquared() < 1e-8f && heli.TailRotorNode?.Rotation.LengthSquared() < 1e-8f,
				"parked, with the rotor stopped, neither rotor has turned");

			foreach (bool _ in Shoot("parked_front", heli, new Vector3(-9.5f, 5.5f, -10.5f), new Vector3(0f, 1.3f, 0.3f))) yield return true;
			foreach (bool _ in Shoot("parked_side", heli, new Vector3(-16.5f, 1.9f, 0.7f), new Vector3(0f, 1.9f, 0.7f))) yield return true;
			foreach (bool _ in Shoot("parked_rear", heli, new Vector3(9f, 5.0f, 12f), new Vector3(0f, 1.4f, 0.6f))) yield return true;

			// The chase camera as CrewSeatView sets it for a level look ahead: pivot 3.4 m over
			// the origin, at the rotor's head, the arm 10 m back, 0.22 rad below the look.
			Vector3 pivot = new(0f, 3.4f, 0f);
			Vector3 chase = pivot + new Vector3(0f, 10f * Mathf.Sin(0.22f), 10f * Mathf.Cos(0.22f));
			foreach (bool _ in Shoot("chase_camera", heli, chase, chase + new Vector3(0f, -Mathf.Sin(0.22f), -Mathf.Cos(0.22f)), 75f)) yield return true;

			// From the left seat's eye, looking out to the left and down, near the foot of its arc.
			CrewSeat left = Array.Find(seats, s => s.Role == SeatRole.Passenger && s.Capsule.Center.X < 0f);
			foreach (bool _ in Shoot("left_seat", heli, left.Eye, left.Eye + new Vector3(-1f, -0.85f, -0.25f), 75f)) yield return true;

			// ---- the rotors turning ----------------------------------------------------------
			heli.Flight.Rotor = 1f;
			float main = 0f, tail = 0f;
			float lastMain = heli.RotorNode.Rotation.Y, lastTail = heli.TailRotorNode.Rotation.X;
			for (int i = 0; i < 60; i++)
			{
				yield return true;
				main += Mathf.AngleDifference(lastMain, heli.RotorNode.Rotation.Y);
				tail += Mathf.AngleDifference(lastTail, heli.TailRotorNode.Rotation.X);
				lastMain = heli.RotorNode.Rotation.Y;
				lastTail = heli.TailRotorNode.Rotation.X;
			}

			Measured($"a second at full rotor: the main rotor turned {F(main, "0.00")} rad, the tail rotor {F(tail, "0.00")} rad");
			Fact(main > 0f && Mathf.Abs(main - RotorRadiansPerSecond) < RotorRadiansPerSecond * 0.02f,
				$"at full rotor the main rotor turns anticlockwise from above at {F(RotorRadiansPerSecond, "0")} rad/s, within 2 %");
			Fact(Mathf.Abs(tail - (main * TailRotorMultiple)) < main * TailRotorMultiple * 0.05f,
				$"and the tail rotor at {TailRotorMultiple} times that");
			Fact(heli.RotorNode.Position.DistanceTo(hub) < 1e-4f && heli.TailRotorNode.Position.DistanceTo(fan) < 1e-4f,
				"turning, each stays on its axis");

			heli.Flight.Rotor = 0.5f;
			main = 0f;
			lastMain = heli.RotorNode.Rotation.Y;
			for (int i = 0; i < 60; i++)
			{
				yield return true;
				main += Mathf.AngleDifference(lastMain, heli.RotorNode.Rotation.Y);
				lastMain = heli.RotorNode.Rotation.Y;
			}

			Fact(Mathf.Abs(main - (RotorRadiansPerSecond * 0.5f)) < RotorRadiansPerSecond * 0.02f,
				$"at half rotor, half as fast ({F(main, "0.00")} rad in a second)");
			heli.QueueFree();
		}

		// ---- tilted, and flown -----------------------------------------------------------------
		{
			CrewVehicle heli = Helicopter(new Vector3(40f, FloorTop + 12f, -40f), -0.4f);
			foreach (bool _ in Wait(2)) yield return true;
			var body = heli.Chassis.FindChild("Body", false, false) as Node3D;

			float SeatsOffCushions()
			{
				float worst = 0f;
				CrewPose pose = heli.DrawnPose;
				foreach (CrewSeat seat in seats)
				{
					var marker = body.FindChild(SeatMarker(seat), false, false) as Node3D;
					worst = Mathf.Max(worst, marker.GlobalPosition.DistanceTo(pose.ToWorld(Foot(seat))));
				}

				return worst;
			}

			heli.PlaceAt(new CrewPose(heli.Flight.Position, heli.Flight.Yaw, Mathf.DegToRad(-20f), Mathf.DegToRad(15f)));
			foreach (bool _ in Wait(2)) yield return true;
			Vector3 hubWorld = heli.RotorNode.GlobalPosition;
			Vector3 hubRules = heli.Pose.ToWorld(heli.RotorNode.Position);
			float tilted = SeatsOffCushions();
			Fact(tilted < 0.01f && hubWorld.DistanceTo(hubRules) < 0.01f,
				$"tilted 20° nose down and 15° rolled, each seat where the rules put a seated body is on its drawn cushion, within {F(tilted)} m, and the hub where the pose puts it");

			// Flown: up off the floor, then ahead at full speed, the nose going down.
			heli.PlaceAt(new CrewPose(new Vector3(40f, FloorTop, -40f), -0.4f));
			heli.Flight.Grounded = true;
			float worstFlown = 0f, worstTilt = 0f, steepest = 0f;
			bool shot = false;
			for (int i = 0; i < 60 * 9; i++)
			{
				float climb = i < 60 * 6 ? 1f : 0f;
				float ahead = i >= 60 * 5 ? 1f : 0f;
				var frame = InputFrame.Create(_tick, 0f, -ahead, heli.Flight.Yaw, 0f, climb > 0f ? InputButtons.Jump : InputButtons.None);
				heli.Step(frame, true, Dt);
				yield return true;
				worstFlown = Mathf.Max(worstFlown, SeatsOffCushions());
				Basis rules = CrewPose.Rotation(0f, heli.DrawnPose.Pitch, heli.DrawnPose.Roll);
				worstTilt = Mathf.Max(worstTilt, (heli.Chassis.Basis.Z - rules.Z).Length() + (heli.Chassis.Basis.Y - rules.Y).Length());
				steepest = Mathf.Min(steepest, heli.Flight.Pitch);
				if (!shot && i > 60 * 7 && heli.Flight.Pitch < Mathf.DegToRad(-15f))
				{
					shot = true;
					foreach (bool _ in Shoot("flying", heli, new Vector3(-16f, 1.0f, -3f), new Vector3(0f, 1.6f, 0.8f))) yield return true;
				}
			}

			Measured($"flown: {F(heli.HeightAboveGround, "0.0")} m up at {F(heli.Speed, "0.0")} m/s, the nose down to {Deg(-steepest)}, the rotor at {F(heli.Flight.Rotor, "0.00")}");
			Fact(!heli.Flight.Grounded && steepest < Mathf.DegToRad(-10f) && worstTilt < 1e-3f,
				"flown off the floor and ahead, the model tilts as the step does, nose down to accelerate");
			Fact(worstFlown < 0.01f, $"and throughout, every seat is on its drawn cushion, within {F(worstFlown)} m");
			heli.QueueFree();
		}

		// ---- the wreck and the greybox ---------------------------------------------------------
		{
			CrewVehicle wreck = Helicopter(new Vector3(-60f, FloorTop, -80f), 0f);
			foreach (bool _ in Wait(2)) yield return true;
			wreck.PresentDestroyed(effect: false);
			int meshes = CountMeshes(wreck, _ => true);
			int burnt = CountMeshes(wreck, m => m.MaterialOverride != null);
			Fact(meshes >= 3 && burnt == meshes, $"a wreck is drawn burnt to the last mesh, rotors included: {burnt} of {meshes}");
			wreck.QueueFree();

			var plain = (CrewVehicleDefinition)definition.Duplicate();
			plain.Model = null;
			ulong before = Time.GetTicksUsec();
			CrewVehicle grey = Helicopter(new Vector3(-60f, FloorTop, -100f), 0f, plain);
			Measured($"made with the greybox in {F((Time.GetTicksUsec() - before) / 1000f, "0.0")} ms");
			foreach (bool _ in Wait(2)) yield return true;
			Aabb boxes = HullBounds(hull);
			Fact(grey.Chassis.FindChild("Hull0", false, false) != null && grey.Chassis.FindChild("Hull3", false, false) != null
				&& grey.Chassis.FindChild("Body", false, false) == null && grey.RotorNode != null && grey.TailRotorNode != null
				&& new Vector2(grey.RotorNode.Position.X, grey.RotorNode.Position.Z).Length() < 1e-4f
				&& grey.RotorNode.Position.Y > boxes.End.Y,
				"with no model the greybox is drawn: its four boxes, a rotor over the origin above them and a tail rotor");
			grey.QueueFree();
		}
	}
}
