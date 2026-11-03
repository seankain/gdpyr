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
/// The crew vehicles' engine checks (docs/GROUND_VEHICLES.md §12, GV2): what the design
/// relies on the engine for, and the 4x4 on the authority end to end, run headless by
/// <c>scripts/crew-vehicle-checks.sh</c> on a copy of the tree with this file added as an
/// autoload. Not part of the game: <c>tools/</c> is neither compiled into it nor imported.
///
/// <list type="bullet">
/// <item><c>CREW_VEHICLE_CHECK=physics</c>: a world built here — a floor, the test strip's
/// scene, blocks — with the real <see cref="CrewVehicle"/>, <c>fps_controller</c> and
/// <see cref="Unit"/>, stepped tick by tick: the ride height; the strip at 5 and 12 m/s;
/// the handbrake; a wall's end at 18 m/s, with the shipped bevel and with none; a body
/// pushed out of a hull that moved into it by its own <c>MoveAndSlide</c>, on the hull's
/// line and off it; a body walking into a parked hull.</item>
/// <item><c>CREW_VEHICLE_CHECK=game</c>: a listen host on <c>Test.tscn</c>, its own player
/// driven through the input map as a person's keys would be: the console's
/// <c>vehicle</c>, the prompts, getting in, driving, the handbrake, the look following the
/// truck, changing seat round it, getting out, walking into it, and the helicopter up,
/// refused out above 4 m, down and out.</item>
/// </list>
///
/// One line per fact, <c>PASS</c> or <c>FAIL</c>; the exit code is the number that failed.
/// </summary>
public partial class CrewVehicleChecks : Node
{
	private const float Dt = SimConfig.TickDelta;

	private IEnumerator<bool> _run;
	private int _failures;
	private int _facts;
	private uint _tick;

	public override void _Ready()
	{
		string mode = System.Environment.GetEnvironmentVariable("CREW_VEHICLE_CHECK");
		if (mode != "physics" && mode != "game")
		{
			SetPhysicsProcess(false);
			return;
		}

		ProcessMode = ProcessModeEnum.Always;
		_run = mode == "physics" ? Physics() : Game();
		GD.Print($"[checks] checks: {mode}");
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

	private static string F(float v, string format = "0.00") => v.ToString(format, CultureInfo.InvariantCulture);

	private static IEnumerable<bool> Wait(int ticks)
	{
		for (int i = 0; i < ticks; i++)
		{
			yield return true;
		}
	}

	// ---- physics ---------------------------------------------------------------------

	private const float FloorTop = 0.5f;

	private static float YawFacing(Vector3 direction) => Mathf.Atan2(-direction.X, -direction.Z);

	private StaticBody3D Box(Vector3 centre, Vector3 size, float yaw = 0f)
	{
		var body = new StaticBody3D { CollisionLayer = CollisionLayers.World, CollisionMask = 0 };
		body.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = size } });
		AddChild(body);
		body.GlobalTransform = new Transform3D(new Basis(Vector3.Up, yaw), centre);
		return body;
	}

	private CrewVehicle Truck(Vector3 ground, float yaw, float bevel = -1f)
	{
		CrewVehicleDefinition definition = CrewVehicleCatalog.Definition(CrewVehicleCatalog.FourByFour);
		if (bevel >= 0f)
		{
			definition = (CrewVehicleDefinition)definition.Duplicate();
			definition.ColliderBevelMeters = bevel;
		}

		var truck = CrewVehicle.Create(definition, 0, ground + (Vector3.Up * definition.ToFourByFourTraits().RideHeightMeters), yaw);
		AddChild(truck);
		return truck;
	}

	private static InputFrame Drive(uint tick, float throttle, float steer = 0f, bool handbrake = false) =>
		InputFrame.Create(tick, steer, -throttle, 0f, 0f, handbrake ? InputButtons.Jump : InputButtons.None);

	/// <summary>
	/// How deep a body's capsule is in a truck's collider [m]: its radius less how far its
	/// axis is outside the bevelled box — the box's six faces and the twelve 45° bevel
	/// planes, the largest signed distance among them (exact off a face or a bevel, a
	/// little short off a corner, so it overstates there if anything).
	/// </summary>
	private static float Penetration(CollisionObject3D body, CrewVehicle truck)
	{
		CollisionShape3D shape = null;
		foreach (Node child in body.GetChildren())
		{
			if (child is CollisionShape3D s && s.Shape is CapsuleShape3D)
			{
				shape = s;
				break;
			}
		}

		if (shape?.Shape is not CapsuleShape3D capsule || truck == null)
		{
			return float.NaN;
		}

		CrewVehicleDefinition definition = truck.Definition;
		var basis = new Basis(Vector3.Up, truck.Yaw);
		Vector3 boxCentre = truck.GlobalPosition + (basis * definition.ColliderCentre);
		Vector3 h = definition.ColliderHalfExtents;
		float b = definition.ColliderBevelMeters;
		Vector3 centre = shape.GlobalPosition;
		float half = Mathf.Max((capsule.Height * 0.5f) - capsule.Radius, 0f);
		float nearest = float.MaxValue;
		for (int i = 0; i <= 40; i++)
		{
			Vector3 p = (basis.Transposed() * (centre + (Vector3.Up * Mathf.Lerp(-half, half, i / 40f)) - boxCentre)).Abs();
			nearest = Mathf.Min(nearest, DistanceToBevelledBox(p, h, b));
		}

		return Mathf.Max(capsule.Radius - nearest, 0f);
	}

	/// <summary>
	/// Exact distance from a point in the box's positive octant to the bevelled box: Dykstra's
	/// alternating projection onto its three faces and three 45° bevels there. Negative inside,
	/// by the largest of the signed plane distances.
	/// </summary>
	private static float DistanceToBevelledBox(Vector3 p, Vector3 h, float b)
	{
		Vector3[] n =
		{
			Vector3.Right, Vector3.Up, Vector3.Back,
			new Vector3(1f, 0f, 1f).Normalized(), new Vector3(1f, 1f, 0f).Normalized(), new Vector3(0f, 1f, 1f).Normalized(),
		};
		float[] c =
		{
			h.X, h.Y, h.Z,
			(h.X + h.Z - b) / Mathf.Sqrt2, (h.X + h.Y - b) / Mathf.Sqrt2, (h.Y + h.Z - b) / Mathf.Sqrt2,
		};

		float inside = float.MinValue;
		for (int k = 0; k < n.Length; k++)
		{
			inside = Mathf.Max(inside, n[k].Dot(p) - c[k]);
		}

		if (inside <= 0f)
		{
			return inside;
		}

		var y = new Vector3[n.Length];
		Vector3 x = p;
		for (int iteration = 0; iteration < 400; iteration++)
		{
			for (int k = 0; k < n.Length; k++)
			{
				Vector3 z = x + y[k];
				float over = n[k].Dot(z) - c[k];
				Vector3 projected = over > 0f ? z - (n[k] * over) : z;
				y[k] = z - projected;
				x = projected;
			}
		}

		return p.DistanceTo(x);
	}

	private fps_controller Player(Vector3 feet, int peer)
	{
		var character = GD.Load<PackedScene>("res://Scenes/fps_controller.tscn").Instantiate<fps_controller>();
		character.Name = $"CheckPlayer{peer}";
		character.PeerId = peer;
		character.IsSimulated = true;
		character.IsLocalPlayer = false;
		character.Position = feet;
		AddChild(character);
		return character;
	}

	private Unit Rifleman(Vector3 feet, int id)
	{
		var unit = new Unit
		{
			Name = $"CheckUnit{id}",
			UnitId = (ushort)id,
			DefinitionId = UnitCatalog.Infantry,
			Definition = UnitCatalog.Definition(UnitCatalog.Infantry),
			Team = Team.Strategist,
			IsSimulated = true,
			Position = feet,
		};
		AddChild(unit);
		return unit;
	}

	private IEnumerator<bool> Physics()
	{
		Box(new Vector3(0f, 0f, 0f), new Vector3(600f, 1f, 600f));
		var strip = GD.Load<PackedScene>("res://Scenes/Environment/VehicleTestStrip.tscn").Instantiate<Node3D>();
		AddChild(strip);
		strip.GlobalPosition = new Vector3(-20f, FloorTop, 2.5f);

		foreach (bool _ in Wait(5)) yield return true;

		// Which way the strip's lane runs from its origin: CSGPolygon3D extrudes along one Z.
		PhysicsDirectSpaceState3D space = GetViewport().World3D.DirectSpaceState;
		float BumpTop(float z)
		{
			var ray = PhysicsRayQueryParameters3D.Create(new Vector3(-18.75f, 5f, z), new Vector3(-18.75f, -1f, z), CollisionLayers.World);
			Godot.Collections.Dictionary hit = space.IntersectRay(ray);
			return hit.Count > 0 ? hit["position"].AsVector3().Y : float.NaN;
		}

		float atZero = BumpTop(0f);
		float atFive = BumpTop(5f);
		GD.Print($"[checks] strip bump top at z=0: {F(atZero)}, at z=5: {F(atFive)}");
		Fact(Mathf.Abs(atZero - (FloorTop + 0.3f)) < 0.01f, "the strip's lane is centred on its origin's z − 2.5 (CSGPolygon3D extrudes along −Z)");
		var rampRay = PhysicsRayQueryParameters3D.Create(new Vector3(9f, 5f, 0f), new Vector3(9f, -1f, 0f), CollisionLayers.World);
		Godot.Collections.Dictionary rampHit = space.IntersectRay(rampRay);
		float rampTop = rampHit.Count > 0 ? rampHit["position"].AsVector3().Y : float.NaN;
		Fact(Mathf.Abs(rampTop - (FloorTop + 1f)) < 0.01f, $"the ramp's top is 1 m up ({F(rampTop)})");

		// ---- standing ---------------------------------------------------------------
		{
			FourByFourTraits traits = FourByFourTraits.Default;
			CrewVehicle truck = Truck(new Vector3(-40f, FloorTop, -40f), 0f);
			for (int i = 0; i < 180; i++)
			{
				truck.Step(FourByFourSim.ParkedFrame(_tick), false, Dt);
				yield return true;
			}

			float ride = truck.Drive.Position.Y - FloorTop;
			Fact(Mathf.Abs(ride - traits.RideHeightMeters) < 0.01f,
				$"parked on flat ground it stands at its ride height: {F(ride, "0.000")} m, the rules say {F(traits.RideHeightMeters, "0.000")}");
			Fact(truck.Drive.Velocity.Length() < 0.01f && truck.GetSlideCollisionCount() == 0,
				"standing, it is still and its collider touches nothing");
			truck.QueueFree();
		}

		// ---- the strip ----------------------------------------------------------------
		foreach (float speed in new[] { 5f, 12f })
		{
			CrewVehicle truck = Truck(new Vector3(-45f, FloorTop, 0f), YawFacing(Vector3.Right));
			float throttle = speed / 18f;
			float ride = FourByFourTraits.Default.RideHeightMeters;
			float bumpPitchMax = 0f, bumpPitchMin = 0f, rampPitchMax = 0f, rampPitchMin = 0f, minSpeed = float.MaxValue;
			int bumpContacts = 0, rampContacts = 0, bumpAir = 0, rampAir = 0, longestAir = 0, run = 0;
			int lastUnsettled = -1, offRamp = -1, i = 0;
			for (; i < 60 * 30; i++)
			{
				truck.Step(Drive(_tick, throttle), true, Dt);
				float x = truck.Drive.Position.X;
				float pitch = Mathf.RadToDeg(truck.Drive.Pitch);
				bool contact = truck.GetSlideCollisionCount() > 0;
				bool bumps = x > -24f && x < -4f;
				bool ramp = x >= -4f && x < 14f;
				if (bumps)
				{
					bumpPitchMax = Mathf.Max(bumpPitchMax, pitch);
					bumpPitchMin = Mathf.Min(bumpPitchMin, pitch);
					bumpContacts += contact ? 1 : 0;
					bumpAir += truck.Drive.Airborne ? 1 : 0;
				}
				else if (ramp)
				{
					rampPitchMax = Mathf.Max(rampPitchMax, pitch);
					rampPitchMin = Mathf.Min(rampPitchMin, pitch);
					rampContacts += contact ? 1 : 0;
					rampAir += truck.Drive.Airborne ? 1 : 0;
				}

				if (bumps || ramp)
				{
					minSpeed = Mathf.Min(minSpeed, new Vector2(truck.Drive.Velocity.X, truck.Drive.Velocity.Z).Length());
				}

				run = truck.Drive.Airborne ? run + 1 : 0;
				longestAir = Mathf.Max(longestAir, run);

				if (x > 10f && offRamp < 0)
				{
					offRamp = i;
				}

				if (Mathf.Abs(truck.Drive.Pitch) > Mathf.DegToRad(0.5f) || Mathf.Abs(truck.Drive.Velocity.Y) > 0.05f
					|| truck.Drive.Airborne)
				{
					lastUnsettled = i;
				}


				yield return true;
				if (offRamp >= 0 && i - offRamp > 360)
				{
					break;
				}
			}

			float settle = (lastUnsettled + 1 - offRamp) / 60f;
			bool held = i - lastUnsettled >= 60;
			float finalRide = truck.Drive.Position.Y - FloorTop;
			GD.Print($"[checks]   ends at ({F(truck.Drive.Position.X, "0.0")}, {F(truck.Drive.Position.Z, "0.00")}) yaw {F(Mathf.RadToDeg(truck.Drive.Yaw), "0.0")}° pitch {F(Mathf.RadToDeg(truck.Drive.Pitch), "0.00")}° roll {F(Mathf.RadToDeg(truck.Drive.Roll), "0.00")}° steer {F(truck.Drive.Steer, "0.000")}"
				+ $" wheels {F(truck.Wheel(0).Distance, "0.000")} {F(truck.Wheel(1).Distance, "0.000")} {F(truck.Wheel(2).Distance, "0.000")} {F(truck.Wheel(3).Distance, "0.000")}"
				+ $" contact {truck.GetSlideCollisionCount()} vy {F(truck.Drive.Velocity.Y, "0.000")} speed {F(truck.Speed, "0.00")}");
			GD.Print($"[checks] strip at {speed} m/s: over the bumps pitch {F(bumpPitchMin, "0.0")}..{F(bumpPitchMax, "0.0")}°,"
				+ $" {bumpAir} ticks with fewer than two wheels down, collider touching {bumpContacts} ticks;"
				+ $" over the ramp pitch {F(rampPitchMin, "0.0")}..{F(rampPitchMax, "0.0")}°, {rampAir} ticks airborne (longest run {longestAir}),"
				+ $" collider touching {rampContacts} ticks; slowest {F(minSpeed, "0.0")} m/s; settled {F(settle, "0.00")} s after passing the ramp's foot;"
				+ $" ride after {F(finalRide, "0.000")} m");
			Fact(truck.Drive.Position.X > 12f, $"at {speed} m/s it crosses the strip");
			Fact(bumpPitchMax - bumpPitchMin > 2f, $"at {speed} m/s the springs pitch it over the bumps ({F(bumpPitchMin, "0.0")}..{F(bumpPitchMax, "0.0")}°)");
			Fact(rampAir > 0, $"at {speed} m/s it leaves the ground off the ramp");
			Fact(Mathf.Max(rampPitchMax, bumpPitchMax) < 35.01f && Mathf.Min(rampPitchMin, bumpPitchMin) > -35.01f, $"at {speed} m/s the attitude stays inside ±35°");
			Fact(minSpeed > speed * 0.5f, $"at {speed} m/s nothing on the strip stops it (slowest {F(minSpeed, "0.0")} m/s)");
			Fact(held && settle < 4f && Mathf.Abs(finalRide - ride) < 0.01f, $"at {speed} m/s it is back at its ride height, settled for good {F(settle, "0.00")} s after the ramp's foot");
			truck.QueueFree();
		}

		// ---- the handbrake --------------------------------------------------------------
		foreach (bool handbrake in new[] { false, true })
		{
			CrewVehicle truck = Truck(new Vector3(-100f, FloorTop, 100f), YawFacing(Vector3.Right));
			while (truck.Drive.Velocity.Length() < 12f)
			{
				truck.Step(Drive(_tick, 1f), true, Dt);
				yield return true;
			}

			float maxSide = 0f;
			float yaw0 = truck.Drive.Yaw;
			for (int i = 0; i < 90; i++)
			{
				truck.Step(Drive(_tick, handbrake ? 0f : 0.67f, 1f, handbrake), true, Dt);
				Vector3 forward = Aim.Direction(truck.Drive.Yaw, 0f);
				Vector3 right = new(-forward.Z, 0f, forward.X);
				maxSide = Mathf.Max(maxSide, Mathf.Abs(truck.Drive.Velocity.Dot(right)));
				yield return true;
			}

			float turned = Mathf.RadToDeg(Mathf.AngleDifference(yaw0, truck.Drive.Yaw));
			GD.Print($"[checks] full lock from 12 m/s{(handbrake ? " on the handbrake" : string.Empty)}: sideways ≤{F(maxSide, "0.0")} m/s, turned {F(turned, "0")}° in 1.5 s");
			Fact(handbrake ? maxSide > 2f : maxSide < 0.5f,
				handbrake ? "on the handbrake it slides" : "without the handbrake it does not");
			truck.QueueFree();
		}

		// ---- wall ends at 18 m/s ------------------------------------------------------------
		var catches = new Dictionary<string, string>();
		int lane = 0;
		// The shipped file's bevel (−1), then none, for comparison.
		foreach (float bevel in new[] { -1f, 0f })
		{
			foreach (float overlap in new[] { 0.1f, 0.2f, 0.3f, 0.5f })
			{
				float z = 20f + (lane++ * 10f);
				float nearEdge = z + 0.95f - overlap;
				StaticBody3D block = Box(new Vector3(0f, FloorTop + 1.5f, nearEdge + 2f), new Vector3(1f, 3f, 4f));
				CrewVehicle truck = Truck(new Vector3(-30f, FloorTop, z), YawFacing(Vector3.Right), bevel);
				for (int i = 0; i < 60; i++)
				{
					truck.Step(FourByFourSim.ParkedFrame(_tick), false, Dt);
					yield return true;
				}

				truck.Drive.Velocity = new Vector3(18f, 0f, 0f);
				float slowest = float.MaxValue;
				float pastAt = float.NaN;
				for (int i = 0; i < 150; i++)
				{
					truck.Step(Drive(_tick, 1f), true, Dt);
					if (truck.Drive.Position.X > -4f && truck.Drive.Position.X < 6f)
					{
						slowest = Mathf.Min(slowest, truck.Drive.Velocity.Length());
					}

					if (float.IsNaN(pastAt) && truck.Drive.Position.X > 4f)
					{
						pastAt = truck.Drive.Velocity.Length();
					}

					yield return true;
				}

				bool passed = truck.Drive.Position.X > 4f;
				string key = $"bevel {F(truck.Definition.ColliderBevelMeters)} overlap {F(overlap, "0.0")}";
				catches[key] = passed ? $"past, slowest {F(slowest, "0.0")} m/s, {F(pastAt, "0.0")} m/s past the end" : $"CAUGHT at x {F(truck.Drive.Position.X, "0.0")}, {F(truck.Drive.Velocity.Length(), "0.0")} m/s";
				GD.Print($"[checks] wall end at 18 m/s, {key}: {catches[key]}");
				if (bevel < 0f && overlap <= 0.2f)
				{
					Fact(passed && slowest > 9f, $"the shipped hull (bevel {F(truck.Definition.ColliderBevelMeters)}) slides off a wall end it overlaps by {F(overlap, "0.0")} m at 18 m/s");
				}

				if (overlap >= 0.5f)
				{
					Fact(!passed && truck.Speed < 0.5f, $"driven into a wall's face (bevel {F(truck.Definition.ColliderBevelMeters)}) it stops, and its speed says so ({F(truck.Speed)} m/s)");
				}

				truck.QueueFree();
				block.QueueFree();
			}
		}

		// ---- bodies: pushed out by their own MoveAndSlide ---------------------------------------
		int peer = 900;
		foreach (bool unit in new[] { false, true })
		{
			foreach ((float speed, float offset) in new[] { (5f, 0f), (5f, 0.8f), (15f, 0f), (15f, 0.8f) })
			{
				float z = -110f + ((lane++ - 8) * 10f);
				CrewVehicle truck = Truck(new Vector3(speed < 10f ? -10f : -30f, FloorTop, z), YawFacing(Vector3.Right));
				CharacterBody3D body = unit
					? Rifleman(new Vector3(0f, FloorTop + 0.05f, z + offset), peer++)
					: Player(new Vector3(0f, FloorTop + 0.05f, z + offset), peer++);

				for (int i = 0; i < 30; i++)
				{
					StepBody(body);
					truck.Step(FourByFourSim.ParkedFrame(_tick), false, Dt);
					yield return true;
				}

				truck.Drive.Velocity = new Vector3(speed, 0f, 0f);
				Vector3 start = body.GlobalPosition;
				float worstAfterTruck = 0f, worstAfterOwn = 0f, slowest = float.MaxValue;
				int contactTicks = 0;
				for (int i = 0; i < 150; i++)
				{
					StepBody(body);
					float own = Penetration(body, truck);
					worstAfterOwn = Mathf.Max(worstAfterOwn, own);
					truck.Step(Drive(_tick, speed / 18f), true, Dt);
					float after = Penetration(body, truck);
					worstAfterTruck = Mathf.Max(worstAfterTruck, after);
					contactTicks += after > 0.001f ? 1 : 0;
					slowest = Mathf.Min(slowest, truck.Drive.Velocity.Length());
					yield return true;
				}

				Vector3 moved = body.GlobalPosition - start;
				string who = unit ? "a rifleman" : "a player";
				GD.Print($"[checks] {who} {F(offset, "0.0")} m off the line of a 4x4 at {speed} m/s: in the hull ≤{F(worstAfterTruck, "0.000")} m after the truck moves,"
					+ $" ≤{F(worstAfterOwn, "0.000")} m after its own MoveAndSlide; {contactTicks} ticks touched; moved ({F(moved.X, "0.0")}, {F(moved.Y, "0.0")}, {F(moved.Z, "0.0")});"
					+ $" truck slowest {F(slowest, "0.0")} m/s");
				Fact(worstAfterOwn < 0.1f && worstAfterOwn < worstAfterTruck, $"{who} {F(offset, "0.0")} m off a 4x4's line at {speed} m/s: its own MoveAndSlide takes it out of the hull (≤{F(worstAfterOwn, "0.000")} m left, from ≤{F(worstAfterTruck, "0.000")} m)");
				StepBody(body);
				Fact(Penetration(body, truck) < 0.005f, $"{who} {F(offset, "0.0")} m off a 4x4's line at {speed} m/s is out of the hull at the end, after its own step");
				Fact(slowest > speed - 0.3f, $"{who} does not slow a 4x4 at {speed} m/s");
				Fact(contactTicks > 0 && moved.Length() > 0.5f && (offset == 0f || Mathf.Abs(moved.Z) > 0.3f), offset == 0f ? $"{who} on its line was pushed ahead of it" : $"{who} {F(offset, "0.0")} m off its line was pushed aside, off the bevel");
				Fact(body.GlobalPosition.Y > FloorTop - 0.1f, $"{who} is still on the floor, not under it");
				truck.QueueFree();
				body.QueueFree();
			}
		}

		// ---- walking into a parked hull -----------------------------------------------------------
		foreach (bool unit in new[] { false, true })
		{
			float z = -20f + ((lane++ - 16) * 10f);
			CrewVehicle truck = Truck(new Vector3(0f, FloorTop, z), YawFacing(Vector3.Right));
			for (int i = 0; i < 30; i++)
			{
				truck.Step(FourByFourSim.ParkedFrame(_tick), false, Dt);
				yield return true;
			}

			Vector3 parked = truck.GlobalPosition;
			CharacterBody3D body = unit
				? Rifleman(new Vector3(0.5f, FloorTop + 0.05f, z + 4f), peer++)
				: Player(new Vector3(0.5f, FloorTop + 0.05f, z + 4f), peer++);
			float worst = 0f;
			for (int i = 0; i < 150; i++)
			{
				if (body is fps_controller walker)
				{
					walker.Simulate(InputFrame.Create(_tick, 0f, -1f, 0f, 0f, InputButtons.None));
				}
				else if (body is Unit rifleman)
				{
					rifleman.Velocity = new Vector3(0f, rifleman.Velocity.Y - (9.8f * Dt), -4.5f);
					rifleman.MoveAndSlide();
				}

				worst = Mathf.Max(worst, Penetration(body, truck));
				truck.Step(FourByFourSim.ParkedFrame(_tick), false, Dt);
				yield return true;
			}

			float gap = truck.HullDistance(body.GlobalPosition + Vector3.Up);
			string who = unit ? "a rifleman" : "a player";
			GD.Print($"[checks] {who} walking into a parked 4x4's side for 2.5 s: deepest {F(worst, "0.000")} m, ends {F(body.GlobalPosition.Z - z, "0.00")} m from its centre line, truck moved {F((truck.GlobalPosition - parked).Length(), "0.000")} m");
			Fact(worst < 0.02f && body.GlobalPosition.Z - z > 0.95f, $"{who} cannot walk into a parked hull");
			Fact((truck.GlobalPosition - parked).Length() < 0.01f, $"{who} walking into it does not move it");
			truck.QueueFree();
			body.QueueFree();
		}
	}

	private void StepBody(CharacterBody3D body)
	{
		if (body is fps_controller character)
		{
			character.Simulate(InputFrame.Neutral(_tick, character.Yaw, 0f));
		}
		else if (body is Unit unit)
		{
			unit.Steer(unit.GlobalPosition, false, Dt);
		}
	}

	// ---- the game, on a listen host ------------------------------------------------------

	private static void Press(string action) => Input.ActionPress(action);

	private static void Release(string action) => Input.ActionRelease(action);

	private void Console(string line)
	{
		MethodInfo submit = typeof(GameConsole).GetMethod("Submit", BindingFlags.NonPublic | BindingFlags.Instance);
		submit.Invoke(GameConsole.Instance, new object[] { line });
	}

	private static LocalInputSampler Sampler => PlayerManager.Instance.GetNode<LocalInputSampler>("LocalInputSampler");

	private void Face(fps_controller character, Vector3 at, float yaw)
	{
		character.Teleport(new Transform3D(new Basis(Vector3.Up, yaw), at));
		Sampler.Align(yaw, 0f);
	}

	private IEnumerable<bool> Tap(string action, int ticks = 4)
	{
		Press(action);
		foreach (bool _ in Wait(ticks)) yield return true;
		Release(action);
		foreach (bool _ in Wait(4)) yield return true;
	}

	private IEnumerable<bool> Hold(string action, int ticks)
	{
		Press(action);
		foreach (bool _ in Wait(ticks)) yield return true;
		Release(action);
		foreach (bool _ in Wait(4)) yield return true;
	}

	private IEnumerator<bool> Game()
	{
		// The world, a local player, and the ground force.
		while (!Session.InGame || CombatManager.Instance?.Local?.Character == null || CrewVehicleManager.Instance == null)
		{
			yield return true;
		}

		foreach (bool _ in Wait(30)) yield return true;
		PlayerCombat local = CombatManager.Instance.Local;
		if (local.AwaitingRole || local.Team != Team.GroundForce)
		{
			CombatManager.Instance.RequestTeam(Team.GroundForce);
		}

		int guard = 0;
		while (!local.IsAlive && guard++ < 600)
		{
			yield return true;
		}

		Fact(local.IsAlive && local.Team == Team.GroundForce, "the listen host's player is alive on the ground force");
		CrewVehicleManager crew = CrewVehicleManager.Instance;
		fps_controller me = local.Character;
		Fact(me.CollisionMask == CollisionLayers.CharacterMask, $"the player's mask includes Vehicles ({me.CollisionMask})");

		// Open ground north of the strip, facing +X.
		float east = YawFacing(Vector3.Right);
		Face(me, new Vector3(-70f, 0.6f, 25f), east);
		foreach (bool _ in Wait(30)) yield return true;

		Console("vehicle 4x4");
		foreach (bool _ in Wait(10)) yield return true;
		CrewVehicle truck = crew.VehicleAt(0);
		Fact(truck != null && truck.Kind == CrewVehicleKind.FourByFour, "`vehicle 4x4` puts a 4x4 in slot 0");
		if (truck == null)
		{
			yield break;
		}

		Fact(truck.GlobalPosition.X > me.SimPosition.X + 3f, $"in front of the player (truck x {F(truck.GlobalPosition.X, "0.0")}, player x {F(me.SimPosition.X, "0.0")})");
		foreach (bool _ in Wait(60)) yield return true;
		Fact(truck.CollisionLayer == CollisionLayers.Vehicles && truck.CollisionMask == CollisionLayers.VehicleMask,
			"the 4x4 is on the Vehicles layer, meeting the world and vehicles");

		// The driver's door, and the prompt there.
		CrewPose pose = truck.Pose;
		Vector3 door = pose.ToWorld(truck.Seats[0].Door);
		Face(me, new Vector3(door.X, door.Y + 0.1f, door.Z), YawFacing(pose.Position - door));
		foreach (bool _ in Wait(20)) yield return true;
		string prompt = EmplacementManager.Instance.PromptFor(local, NetworkManager.Instance.Tick);
		GD.Print($"[checks] at the driver's door the prompt is \"{prompt}\"");
		Fact(prompt == "[E] drive 4x4", "at the driver's door the prompt is `[E] drive 4x4`");

		foreach (bool _ in Tap("use")) yield return true;
		Fact(crew.IsDriving(local.PeerId, out _), "a tap of E at the driver's door seats the player at the wheel");
		Fact(me.IsSeated && me.CollisionLayer == 0 && me.CollisionMask == 0, "seated, the body is on no layer and no mask");
		HitCapsule seatCapsule = me.Hitbox;
		Fact(truck.HullDistance(seatCapsule.Center) == 0f, "the seated hitbox is the driver's capsule, inside the cab");
		prompt = EmplacementManager.Instance.PromptFor(local, NetworkManager.Instance.Tick);
		GD.Print($"[checks] seated: \"{prompt.Replace("\n", " / ")}\"");
		Fact(prompt.StartsWith("4x4 · driver · 600/600 · 0 m/s\n[E] get out · hold [E] gun"), "the seated prompt is §8.5's");

		// Drive: four seconds of W.
		Vector3 from = truck.GlobalPosition;
		Vector3 bodyFrom = me.SimPosition;
		Press("move_forward");
		float maxPitch = 0f;
		for (int i = 0; i < 240; i++)
		{
			maxPitch = Mathf.Max(maxPitch, Mathf.RadToDeg(truck.Drive.Pitch));
			yield return true;
		}

		float speed = truck.Speed;
		float travelled = (truck.GlobalPosition - from).Length();
		GD.Print($"[checks] W for 4 s: {F(speed, "0.0")} m/s, {F(travelled, "0.0")} m, nose up to {F(maxPitch, "0.0")}°, body moved {F((me.SimPosition - bodyFrom).Length(), "0.0")} m");
		Fact(speed > 14f && travelled > 30f, "W drives it: over 14 m/s and 30 m in 4 s");
		Fact(Mathf.Abs((me.SimPosition - bodyFrom).Length() - travelled) < 0.5f, "the seated body rides with it (the pin)");
		Fact(me.Velocity.DistanceTo(truck.SimVelocity) < 0.01f, "the seated body's velocity is the truck's");
		Fact(truck.HullDistance(me.Hitbox.Center) == 0f, "and its hitbox stays in the cab");

		// Steer and slide.
		Press("move_right");
		Press("jump");
		float side = 0f;
		float lookStart = me.Yaw;
		float hullStart = truck.Drive.Yaw;
		for (int i = 0; i < 60; i++)
		{
			Vector3 forward = Aim.Direction(truck.Drive.Yaw, 0f);
			side = Mathf.Max(side, Mathf.Abs(truck.Drive.Velocity.Dot(new Vector3(-forward.Z, 0f, forward.X))));
			yield return true;
		}

		Release("move_forward");
		Release("move_right");
		float hullTurn = Mathf.RadToDeg(Mathf.AngleDifference(hullStart, truck.Drive.Yaw));
		float lookTurn = Mathf.RadToDeg(Mathf.AngleDifference(lookStart, me.Yaw));
		GD.Print($"[checks] D + Space for 1 s: sideways up to {F(side, "0.0")} m/s; the hull turned {F(hullTurn, "0.0")}°, the look {F(lookTurn, "0.0")}°");
		Fact(side > 2f, "on the handbrake at speed it slides");
		Fact(Mathf.Abs(hullTurn) > 10f && Mathf.Abs(lookTurn - hullTurn) < 3f, "the driver's look follows the truck round (§8.4)");

		// Stop on the handbrake.
		guard = 0;
		while (truck.Speed > 0.05f && guard++ < 600)
		{
			yield return true;
		}

		Release("jump");
		foreach (bool _ in Wait(20)) yield return true;
		Fact(truck.Speed < 0.05f, $"Space stops it ({F(truck.Speed, "0.00")} m/s)");

		// Change seat round the truck: gun, bed, wheel.
		string[] expected = { "gun", "bed", "driver" };
		for (int s = 0; s < 3; s++)
		{
			foreach (bool _ in Hold("use", UseTracker.HoldTicks + 5)) yield return true;
			crew.TrySeat(local.PeerId, out _, out int seat);
			string name = seat >= 0 ? CrewVehicleManager.SeatName(truck, seat) : "none";
			Fact(name == expected[s], $"a hold of E moves the player to the {expected[s]} ({name})");
			if (s == 0)
			{
				Vector3 eye = truck.Pose.ToWorld(truck.Seats[seat].Eye);
				Fact(me.EyePosition.DistanceTo(eye) < 0.01f, $"at the gun the body's eye is the seat's ({F(me.EyePosition.DistanceTo(eye), "0.000")} m)");
				Vector3 before = truck.GlobalPosition;
				foreach (bool _ in Wait(60)) yield return true;
				Fact((truck.GlobalPosition - before).Length() < 0.02f, "with nobody at the wheel it stays parked");
			}
		}

		// Out.
		foreach (bool _ in Tap("use")) yield return true;
		Fact(!crew.IsSeated(local.PeerId) && !me.IsSeated, "a tap of E gets the driver out");
		Fact(me.CollisionLayer == CollisionLayers.Players && me.CollisionMask == CollisionLayers.CharacterMask, "back on the players' layer, colliding again");
		float outside = truck.HullDistance(me.SimPosition + Vector3.Up);
		Vector3 exitPoint = truck.Pose.ToWorld(truck.Seats[0].Exit);
		GD.Print($"[checks] out at ({F(me.SimPosition.X)}, {F(me.SimPosition.Y)}, {F(me.SimPosition.Z)}), {F(outside)} m from the hull; the driver's exit point is ({F(exitPoint.X)}, {F(exitPoint.Y)}, {F(exitPoint.Z)})");
		Fact(outside > 0.4f, "out at a point clear of the hull");
		foreach (bool _ in Wait(30)) yield return true;
		Fact(me.IsOnFloor(), "and standing on the floor");

		// Walk into the truck.
		Vector3 parked = truck.GlobalPosition;
		Vector3 toTruck = truck.GlobalPosition - me.SimPosition;
		toTruck.Y = 0f;
		Face(me, me.SimPosition + (Vector3.Up * 0.05f), YawFacing(toTruck.Normalized()));
		Press("move_forward");
		float deepest = 0f;
		for (int i = 0; i < 120; i++)
		{
			deepest = Mathf.Max(deepest, Penetration(me, truck));
			yield return true;
		}

		Release("move_forward");
		GD.Print($"[checks] walking into it for 2 s: deepest {F(deepest, "0.000")} m; {F(truck.HullDistance(me.SimPosition + Vector3.Up), "0.00")} m from the hull; truck moved {F((truck.GlobalPosition - parked).Length(), "0.000")} m");
		Fact(deepest < 0.02f, "a player cannot walk into the hull");
		Fact((truck.GlobalPosition - parked).Length() < 0.01f, "and walking into it does not move it");

		// The helicopter: up, refused out, down, out.
		Face(me, new Vector3(-40f, 0.6f, -10f), east);
		foreach (bool _ in Wait(20)) yield return true;
		Console("vehicle helicopter");
		foreach (bool _ in Wait(20)) yield return true;
		CrewVehicle heli = crew.VehicleAt(1);
		Fact(heli != null && heli.Kind == CrewVehicleKind.Helicopter && heli.Grounded, "`vehicle helicopter` puts a helicopter down in slot 1");
		if (heli == null)
		{
			yield break;
		}

		Vector3 pilotDoor = heli.Pose.ToWorld(heli.Seats[0].Door);
		Face(me, pilotDoor + (Vector3.Up * 0.1f), YawFacing(heli.Pose.Position - pilotDoor));
		foreach (bool _ in Wait(10)) yield return true;
		prompt = EmplacementManager.Instance.PromptFor(local, NetworkManager.Instance.Tick);
		Fact(prompt == "[E] fly helicopter", $"at the pilot's door the prompt is `[E] fly helicopter` ({prompt})");
		foreach (bool _ in Tap("use")) yield return true;
		Fact(crew.IsDriving(local.PeerId, out _), "a tap seats the pilot");
		foreach (bool _ in Wait(170)) yield return true;
		Press("jump");
		foreach (bool _ in Wait(180)) yield return true;
		Release("jump");
		foreach (bool _ in Wait(30)) yield return true;
		float up = heli.HeightAboveGround;
		GD.Print($"[checks] helicopter: rotor {F(heli.Flight.Rotor)}, {F(up, "0.0")} m up after Space");
		Fact(up > 6f, "Space climbs once the rotor is up");
		foreach (bool _ in Tap("use")) yield return true;
		Fact(crew.IsSeated(local.PeerId), "getting out higher than 4 m is refused");
		prompt = EmplacementManager.Instance.PromptFor(local, NetworkManager.Instance.Tick);
		Fact(prompt.EndsWith("too high to get out"), $"and the prompt says so ({prompt.Replace("\n", " / ")})");
		Press("crouch");
		guard = 0;
		while (!heli.Grounded && guard++ < 600)
		{
			yield return true;
		}

		Release("crouch");
		foreach (bool _ in Wait(10)) yield return true;
		Fact(heli.Grounded, "Ctrl brings it down to the ground");
		foreach (bool _ in Tap("use")) yield return true;
		Fact(!crew.IsSeated(local.PeerId), "on the ground, a tap gets the pilot out");

		Console("vehicle clear");
		foreach (bool _ in Wait(5)) yield return true;
		Fact(crew.Count == 0, "`vehicle clear` takes them away");
	}
}
