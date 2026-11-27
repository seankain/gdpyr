using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Gdpyr.Core;
using Gdpyr.Net;
using Gdpyr.Rts;
using Gdpyr.Sim;
using Godot;

namespace Gdpyr.EngineChecks;

/// <summary>
/// The supply truck as drawn (docs/UNIT_MODELS.md §7: Models/Vehicles/supply_truck/supply_truck.glb,
/// built by tools/blender/supply_truck.py, drawn by <c>Unit.BuildModel</c>), run by
/// <c>scripts/supply-truck-model-checks.sh</c> on a copy of the tree with this file added as an
/// autoload; not part of the game.
///
/// <c>listen</c>: a listen host places a supply truck. It is drawn as the model and not the
/// capsule, its lamps down -Z and its tilt behind, on the ground, casting no shadow; it still
/// collides as its capsule and still draws its supply ring, green. Firing on a place to its side
/// it does not turn to it; driving off ahead and to the right, it turns to the way it goes,
/// firing still; where it was sent, on its move order still, it holds that way although its
/// feet step back and forth over the point; and held, it keeps it. Its turret yaw is zero on the
/// wire throughout.
///
/// <c>server</c> and <c>client</c>: a server places a supply truck and drives it off; the client
/// draws the model, facing the way the server parked it.
///
/// One line per fact, <c>PASS</c> or <c>FAIL</c>; the process exits with the number that failed.
/// </summary>
public partial class SupplyTruckModelChecks : Node
{
	private const int Second = SimConfig.TickRate;

	private const string ModelPath = "res://Models/Vehicles/supply_truck/supply_truck.glb";

	/// <summary>Open floor (the tank model check's site).</summary>
	private static readonly Vector3 Site = new(40f, 0.6f, 50f);

	private string _role;
	private string _dir;
	private IEnumerator<bool> _run;
	private int _failures;
	private int _facts;
	private bool _timedOut;

	public override void _Ready()
	{
		_role = System.Environment.GetEnvironmentVariable("TRUCK_MODEL_CHECK");
		_dir = System.Environment.GetEnvironmentVariable("TRUCK_MODEL_DIR");
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
			return;
		}

		ProcessMode = ProcessModeEnum.Always;
		GD.Print($"[checks] supply truck model check: {_role}");
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

	private static string F(float v, string format = "0.00") => v.ToString(format, CultureInfo.InvariantCulture);

	private static string Deg(float radians) => F(Mathf.RadToDeg(radians)) + "°";

	private void Mark(string name, string content = "")
	{
		if (!string.IsNullOrEmpty(_dir))
		{
			File.WriteAllText(Path.Combine(_dir, name), content);
		}
	}

	private bool Has(string name) => File.Exists(Path.Combine(_dir, name));

	private string Read(string name) => File.ReadAllText(Path.Combine(_dir, name));

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

	private static uint Tick => NetworkManager.Instance.Tick;

	/// <summary>Which way a node's -Z points, flat, as a yaw.</summary>
	private static float DrawnYaw(Node3D node)
	{
		Vector3 forward = -node.GlobalBasis.Z;
		forward.Y = 0f;
		Aim.Angles(forward, out float yaw, out _);
		return yaw;
	}

	private static float Off(float a, float b) => Mathf.Abs(Mathf.AngleDifference(a, b));

	/// <summary>
	/// Has the place fired on from <paramref name="truck"/> until <paramref name="ticks"/> from
	/// now, as an operative's call does; the truck is kept from scanning, which would take the
	/// call back (<see cref="Unit.NextScanTick"/>).
	/// </summary>
	private static void CallFire(Unit truck, Vector3 at, int ticks)
	{
		truck.NextScanTick = uint.MaxValue;
		truck.HasCoverCall = true;
		truck.CoverCallPoint = at;
		truck.CoverCallUntil = Tick + (uint)ticks;
	}

	private static void Drive(Unit truck, Vector3 to) => truck.Order = new UnitOrder
	{
		Kind = OrderKind.Move,
		Target = to,
		Anchor = to,
		TargetOwnerId = OwnerId.None,
	};

	private static int CountShadowCasters(Node node)
	{
		int n = node is GeometryInstance3D { CastShadow: not GeometryInstance3D.ShadowCastingSetting.Off } ? 1 : 0;
		foreach (Node child in node.GetChildren())
		{
			n += CountShadowCasters(child);
		}

		return n;
	}

	/// <summary>
	/// The extents, in the model's own frame, of every surface under <paramref name="node"/> whose
	/// material is named <paramref name="material"/>; of every surface, given null.
	/// </summary>
	private static Aabb Extents(Node3D model, Node node, string material)
	{
		var points = new List<Vector3>();
		Collect(model, node, material, points);
		if (points.Count == 0)
		{
			return default;
		}

		var box = new Aabb(points[0], Vector3.Zero);
		foreach (Vector3 p in points)
		{
			box = box.Expand(p);
		}

		return box;
	}

	private static void Collect(Node3D model, Node node, string material, List<Vector3> into)
	{
		if (node is MeshInstance3D { Mesh: { } mesh } instance)
		{
			for (int s = 0; s < mesh.GetSurfaceCount(); s++)
			{
				if (material == null || mesh.SurfaceGetMaterial(s)?.ResourceName == material)
				{
					foreach (Vector3 v in mesh.SurfaceGetArrays(s)[(int)Mesh.ArrayType.Vertex].AsVector3Array())
					{
						into.Add(model.ToLocal(instance.ToGlobal(v)));
					}
				}
			}
		}

		foreach (Node child in node.GetChildren())
		{
			Collect(model, child, material, into);
		}
	}

	/// <summary>The facts about how a supply truck is drawn, the same on whichever peer draws it.</summary>
	private void DrawnAsTheModel(Unit truck)
	{
		Node3D model = truck.GetNodeOrNull<Node3D>("Model");
		Fact(model != null && model.SceneFilePath == ModelPath && truck.GetNodeOrNull("Mesh") == null,
			$"it is drawn as {ModelPath} and not as the capsule ({model?.SceneFilePath ?? "no model"})");
		if (model == null)
		{
			return;
		}

		Aabb all = Extents(model, model, null);
		Aabb canvas = Extents(model, model, "TruckCanvas");
		Aabb lenses = Extents(model, model, "TruckLens");
		Vector3 end = all.End;
		Measured($"extents x {F(all.Position.X)}..{F(end.X)}, y {F(all.Position.Y, "0.000")}..{F(end.Y)},"
			+ $" z {F(all.Position.Z)}..{F(end.Z)}; tilt z {F(canvas.Position.Z)}..{F(canvas.End.Z)},"
			+ $" lamps z {F(lenses.Position.Z)}..{F(lenses.End.Z)}");
		Fact(all.Size.Z is > 7f and < 8f && all.Size.X is > 2.4f and < 3.2f && end.Y is > 2.6f and < 3f
			&& Mathf.Abs(all.Position.Y) < 0.02f && Mathf.Abs(all.GetCenter().X) < 0.01f && Mathf.Abs(all.GetCenter().Z) < 0.1f,
			$"it is a truck's size, {F(all.Size.Z)} m long, {F(all.Size.X)} m across its mirrors and {F(end.Y)} m high,"
			+ " over its feet, standing on the ground");
		Fact(canvas.Size.Z > 3f && canvas.Position.Z > 0f && lenses.Size != Vector3.Zero && lenses.End.Z < -3f,
			"its lamps are ahead, down -Z, the way a unit at yaw 0 faces, and its tilt behind");
		Fact(CountShadowCasters(model) == 0, "it casts no shadow, as the capsule cast none");
		Fact(truck.GetNodeOrNull<CollisionShape3D>("CollisionShape3D")?.Shape is CapsuleShape3D { Height: 2.4f, Radius: 1.1f },
			"it still collides, and is hit, as its 2.4 m by 1.1 m capsule");
		var ring = truck.GetNodeOrNull<MeshInstance3D>("SupplyRing");
		Fact(ring is { Mesh: TorusMesh { InnerRadius: 11.85f, OuterRadius: 12.15f }, MaterialOverride: StandardMaterial3D material }
			&& material.AlbedoColor.IsEqualApprox(new Color(0.3f, 0.75f, 0.45f)),
			$"it still draws its 12 m supply ring, in the supply green ({(ring?.MaterialOverride as StandardMaterial3D)?.AlbedoColor})");
	}

	// ---- listen --------------------------------------------------------------------------

	private IEnumerator<bool> Listen()
	{
		foreach (bool _ in Until(() => Session.InGame && UnitManager.Instance != null, 30 * Second)) yield return true;
		foreach (bool _ in Wait(Second)) yield return true;
		UnitManager units = UnitManager.Instance;

		ushort id = units.ServerPlaceUnit(UnitCatalog.SupplyTruck, Site, Team.GroundForce, OrderKind.None);
		units.ServerSetPlacedAutonomy(id, MicroAutonomy.Off);
		Unit truck = units.Find(id);
		foreach (bool _ in Wait(Second)) yield return true;
		Fact(truck is { IsAlive: true, Definition.IsSupply: true, FacesTravel: true, HasTurret: false },
			"a supply truck is placed; it faces the way it goes, and has no turret");
		if (truck == null)
		{
			yield break;
		}

		DrawnAsTheModel(truck);
		Node3D model = truck.GetNodeOrNull<Node3D>("Model");

		// ---- firing on a place to its side, it does not turn to it ----
		float rest = truck.Yaw;
		Vector3 side = truck.GlobalPosition + (truck.GlobalBasis.X * 15f);
		CallFire(truck, side, 20 * Second);
		foreach (bool _ in Until(() => truck.LastShotTick > 0, 5 * Second)) yield return true;
		Fact(truck.LastShotTick > 0, "it fires on a place 15 m to its right");
		foreach (bool _ in Wait(Second)) yield return true;
		Fact(Off(truck.Yaw, rest) < 0.001f && Quantize.TurretYawToU8(truck.TurretYaw) == 0,
			$"standing a second, firing, it does not turn to it ({Deg(rest)} → {Deg(truck.Yaw)};"
			+ $" turret byte {Quantize.TurretYawToU8(truck.TurretYaw)})");

		// ---- driving off ahead and to the right, the place in reach all the way, firing still: a
		// truck that turned to it would face well round from the way it goes ----
		Vector3 ahead = truck.GlobalPosition + ((-truck.GlobalBasis.Z + truck.GlobalBasis.X) * 8f);
		Vector3 start = truck.GlobalPosition;
		uint shotsBefore = truck.LastShotTick;
		Drive(truck, ahead);
		CallFire(truck, side, 20 * Second);
		float arrival = truck.Traits.ArrivalRadiusMeters;
		float travelYaw = 0f;
		float worstOff = 0f;
		int turretBytes = 0;
		int moving = 0;
		float arrivedYaw = float.NaN;
		float worstParked = 0f;
		float fastestParked = 0f;
		int parked = 0;
		for (int i = 0; i < 4 * Second; i++)
		{
			yield return true;
			Vector3 v = truck.Velocity;
			v.Y = 0f;
			Vector3 left = ahead - truck.GlobalPosition;
			left.Y = 0f;
			turretBytes |= Quantize.TurretYawToU8(truck.TurretYaw);
			if (left.Length() > arrival)
			{
				if (v.Length() > 1f && i > Second / 2)
				{
					moving++;
					Aim.Angles(v, out travelYaw, out _);
					worstOff = Mathf.Max(worstOff, Off(truck.Yaw, travelYaw));
				}
			}
			else
			{
				// There, on its move order still.
				if (float.IsNaN(arrivedYaw))
				{
					arrivedYaw = truck.Yaw;
				}

				parked++;
				worstParked = Mathf.Max(worstParked, Off(truck.Yaw, arrivedYaw));
				fastestParked = Mathf.Max(fastestParked, v.Length());
			}
		}

		float moved = truck.GlobalPosition.DistanceTo(start);
		Measured($"moved {F(moved, "0.0")} m; facing {Deg(truck.Yaw)}, last travel {Deg(travelYaw)}; {parked} ticks"
			+ $" within {F(arrival, "0.0")} m of where it was sent, moving at up to {F(fastestParked, "0.0")} m/s there");
		Fact(moving > Second / 2 && moved > 5f, $"it drives off ({F(moved, "0.0")} m, {moving} ticks on the way)");
		Fact(Off(arrivedYaw, rest) > Mathf.DegToRad(30f) && worstOff < Mathf.DegToRad(5f),
			$"it turns to the way it is going ({Deg(rest)} → {Deg(arrivedYaw)}), never more than {Deg(worstOff)} off it");
		Fact(truck.LastShotTick > shotsBefore, "firing on the place as it goes");
		Fact(parked > Second && worstParked < 0.001f,
			$"there, on its move order still, it holds the way it faces, its feet never still: {Deg(worstParked)} over {parked} ticks");
		Fact(model != null && Off(DrawnYaw(model), truck.Yaw) < 0.001f, "and is drawn facing that way");
		// Zero on the wire: the yaw itself is the float noise of wrapping zero into [-π, π)
		// (Unit.TurnTurretTo, which every model-drawn unit goes through), well under a step.
		Fact(turretBytes == 0, $"its turret yaw stays zero on the wire, every tick ({Deg(truck.TurretYaw)})");

		// ---- held, firing still: it keeps the way it faces ----
		truck.Order = UnitOrder.Hold(truck.GlobalPosition);
		CallFire(truck, side, 20 * Second);
		foreach (bool _ in Until(() => new Vector2(truck.Velocity.X, truck.Velocity.Z).Length() < 0.1f, 3 * Second)) yield return true;
		float stopped = truck.Yaw;
		foreach (bool _ in Wait(2 * Second)) yield return true;
		Fact(!_timedOut && Off(truck.Yaw, stopped) < 0.001f && truck.LastShotTick + Second > Tick,
			$"held and firing still, it keeps the way it faces ({Deg(stopped)} → {Deg(truck.Yaw)})");
		units.ClearUnits();
	}

	// ---- server --------------------------------------------------------------------------

	private IEnumerator<bool> Server()
	{
		foreach (bool _ in Until(() => Has("client-ready"), 60 * Second)) yield return true;
		Fact(!_timedOut, "a client has joined");
		UnitManager units = UnitManager.Instance;
		ushort id = units.ServerPlaceUnit(UnitCatalog.SupplyTruck, Site, Team.GroundForce, OrderKind.None);
		units.ServerSetPlacedAutonomy(id, MicroAutonomy.Off);
		Unit truck = units.Find(id);
		foreach (bool _ in Wait(Second)) yield return true;
		Fact(truck is { IsAlive: true }, $"a supply truck is placed (unit {id})");
		if (truck == null)
		{
			yield break;
		}

		// Off to its right and a little ahead, then stopped there while the client looks.
		Drive(truck, truck.GlobalPosition + (truck.GlobalBasis.X * 18f) + (-truck.GlobalBasis.Z * 6f));
		foreach (bool _ in Wait(5 * Second)) yield return true;
		truck.Order = UnitOrder.Hold(truck.GlobalPosition);
		foreach (bool _ in Wait(Second)) yield return true;
		Mark("parked", string.Join(" ", id.ToString(CultureInfo.InvariantCulture),
			truck.Yaw.ToString("R", CultureInfo.InvariantCulture)));
		Measured($"parked facing {Deg(truck.Yaw)}");
		foreach (bool _ in Until(() => Has("finished-client"), 30 * Second)) yield return true;
	}

	// ---- client --------------------------------------------------------------------------

	private IEnumerator<bool> Client()
	{
		foreach (bool _ in Until(() => Session.InGame && UnitManager.Instance != null, 60 * Second)) yield return true;
		foreach (bool _ in Wait(Second)) yield return true;
		Mark("client-ready");

		foreach (bool _ in Until(() => Has("parked"), 60 * Second)) yield return true;
		string[] parked = Read("parked").Split(' ');
		ushort id = ushort.Parse(parked[0], CultureInfo.InvariantCulture);
		float yaw = float.Parse(parked[1], CultureInfo.InvariantCulture);
		Unit truck = null;
		foreach (bool _ in Until(() => (truck = UnitManager.Instance.Find(id)) is { Visible: true }, 10 * Second)) yield return true;
		foreach (bool _ in Wait(Second / 2)) yield return true;
		Fact(truck != null && !truck.IsSimulated, $"the client has the server's supply truck (unit {id}), as a puppet");
		if (truck == null)
		{
			yield break;
		}

		DrawnAsTheModel(truck);
		Node3D model = truck.GetNodeOrNull<Node3D>("Model");
		Fact(Off(truck.Yaw, yaw) < 0.001f && Mathf.Abs(truck.TurretYaw) < 1e-6f,
			$"it faces the way the server parked it: {Deg(truck.Yaw)} against {Deg(yaw)}");
		Fact(model != null && Off(DrawnYaw(model), yaw) < 0.001f, "and is drawn that way");
	}
}
