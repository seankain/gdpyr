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
/// The tank as drawn (docs/UNIT_MODELS.md: Models/Vehicles/tank/tank.glb, built by
/// tools/blender/tank.py, drawn by <c>Unit.BuildModel</c>), run by
/// <c>scripts/tank-model-checks.sh</c> on a copy of the tree with this file added as an
/// autoload; not part of the game.
///
/// <c>listen</c>: a listen host places a tank. It is drawn as the model and not the capsule,
/// with a turret node, casting no shadow, and still collides as its capsule; its turret lays
/// on a place to its side while the hull stays put; driving off at right angles to it, the hull
/// turns to the way it goes while the turret stays on the place, so the turret stands well round
/// against the hull; and with nothing to shoot at the turret comes back square.
///
/// <c>server</c> and <c>client</c>: a dedicated server places a tank and lays its turret; the
/// client draws the model with the turret where the server has it, within the wire's step; and
/// as the server swings the turret round through straight behind, the client's drawn turret
/// turns the short way, never jumping.
///
/// One line per fact, <c>PASS</c> or <c>FAIL</c>; the process exits with the number that failed.
/// </summary>
public partial class TankModelChecks : Node
{
	private const int Second = SimConfig.TickRate;

	/// <summary>Open floor (the muzzle and mortar checks' site).</summary>
	private static readonly Vector3 Site = new(40f, 0.6f, 50f);

	private string _role;
	private string _dir;
	private IEnumerator<bool> _run;
	private int _failures;
	private int _facts;
	private bool _timedOut;

	public override void _Ready()
	{
		_role = System.Environment.GetEnvironmentVariable("TANK_MODEL_CHECK");
		_dir = System.Environment.GetEnvironmentVariable("TANK_MODEL_DIR");
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
		GD.Print($"[checks] tank model check: {_role}");
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

	private static string F(float v, string format = "0.0") => v.ToString(format, CultureInfo.InvariantCulture);

	private static string Deg(float radians) => F(Mathf.RadToDeg(radians), "0.00") + "°";

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

	/// <summary>The world yaw from <paramref name="from"/> to <paramref name="to"/>, as a unit faces (Aim.Angles).</summary>
	private static float YawTo(Vector3 from, Vector3 to)
	{
		Vector3 d = to - from;
		d.Y = 0f;
		Aim.Angles(d, out float yaw, out _);
		return yaw;
	}

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
	/// Has the place been fired on from <paramref name="tank"/> until <paramref name="ticks"/> from
	/// now, as an operative's call does. A unit's scan takes back any call no operative is making,
	/// so the tank is kept from scanning while it is checked (<see cref="Unit.NextScanTick"/>).
	/// </summary>
	private static void CallFire(Unit tank, Vector3 at, int ticks)
	{
		tank.NextScanTick = uint.MaxValue;
		tank.HasCoverCall = true;
		tank.CoverCallPoint = at;
		tank.CoverCallUntil = Tick + (uint)ticks;
	}

	private static void Drive(Unit tank, Vector3 to) => tank.Order = new UnitOrder
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

	/// <summary>The facts about how a tank is drawn, the same on whichever peer draws it.</summary>
	private void DrawnAsTheModel(Unit tank)
	{
		Node3D model = tank.GetNodeOrNull<Node3D>("Model");
		Fact(model != null && model.SceneFilePath == "res://Models/Vehicles/tank/tank.glb"
			&& tank.GetNodeOrNull("Mesh") == null,
			$"it is drawn as Models/Vehicles/tank/tank.glb and not as the capsule ({model?.SceneFilePath ?? "no model"})");
		Node3D turret = tank.Turret;
		Node3D gun = turret?.GetNodeOrNull<Node3D>("Gun");
		Node3D muzzle = gun?.GetNodeOrNull<Node3D>("Muzzle");
		Fact(turret != null && turret.Name == "Turret" && gun != null && muzzle != null,
			$"its turret carries the gun, the gun the muzzle ({turret?.GetPath()}, {gun?.Name}, {muzzle?.Name})");
		if (turret != null && muzzle != null && model != null)
		{
			// The ring's axis is on the hull's middle line, and the muzzle is ahead of the nose.
			Vector3 ring = model.ToLocal(turret.GlobalPosition);
			Vector3 tip = turret.ToLocal(muzzle.GlobalPosition);
			Fact(Mathf.Abs(ring.X) < 0.01f && ring.Y > 1f && tip.Z < -5f,
				$"the turret turns on the hull's middle line {ring}, the muzzle {F(-tip.Z, "0.00")} m ahead of its axis");
		}

		Fact(model != null && CountShadowCasters(model) == 0, "it casts no shadow, as the capsule cast none");
		Fact(tank.GetNodeOrNull<CollisionShape3D>("CollisionShape3D")?.Shape is CapsuleShape3D { Height: 3f, Radius: 1.3f },
			"it still collides, and is hit, as its 3 m by 1.3 m capsule");
	}

	// ---- listen --------------------------------------------------------------------------

	private IEnumerator<bool> Listen()
	{
		foreach (bool _ in Until(() => Session.InGame && UnitManager.Instance != null, 30 * Second)) yield return true;
		foreach (bool _ in Wait(Second)) yield return true;
		UnitManager units = UnitManager.Instance;

		// Ground force, as the local player is: it has nobody to pick a fight with but the place.
		ushort id = units.ServerPlaceUnit(UnitCatalog.Tank, Site, Team.GroundForce, OrderKind.None);
		units.ServerSetPlacedAutonomy(id, MicroAutonomy.Off);
		Unit tank = units.Find(id);
		foreach (bool _ in Wait(Second)) yield return true;
		Fact(tank is { IsAlive: true, HasTurret: true }, "a tank is placed, and has a turret");
		if (tank == null)
		{
			yield break;
		}

		DrawnAsTheModel(tank);
		float hull = tank.Yaw;
		float drawnAtRest = tank.Turret != null ? DrawnYaw(tank.Turret) : float.NaN;
		Fact(Mathf.Abs(tank.TurretYaw) < 1e-6f && Off(drawnAtRest, hull) < 0.001f,
			$"at rest its turret is square to the hull (hull {Deg(hull)}, turret {Deg(tank.TurretYaw)} against it,"
			+ $" drawn at {Deg(drawnAtRest)})");

		// ---- the turret lays on a place to the side; the hull does not turn ----
		Vector3 side = tank.GlobalPosition + (tank.GlobalBasis.X * 20f);
		CallFire(tank, side, 10 * Second);
		foreach (bool _ in Wait(Second)) yield return true;
		float wanted = YawTo(tank.GlobalPosition, side);
		Fact(Off(tank.Yaw, hull) < 0.001f, $"the hull stays as it was ({Deg(hull)} → {Deg(tank.Yaw)})");
		Fact(Off(tank.Yaw + tank.TurretYaw, wanted) < Mathf.DegToRad(1f) && Mathf.Abs(tank.TurretYaw) > Mathf.DegToRad(80f),
			$"the turret lays on the place to its right ({Deg(tank.TurretYaw)} against the hull; off by"
			+ $" {Deg(Off(tank.Yaw + tank.TurretYaw, wanted))})");
		Fact(tank.Turret != null && Off(DrawnYaw(tank.Turret), tank.Yaw + tank.TurretYaw) < 0.001f,
			"the drawn turret is where it is laid");
		foreach (bool _ in Until(() => tank.LastShotTick > 0, 5 * Second)) yield return true;
		Fact(tank.LastShotTick > 0, "and the tank fires on it");

		// ---- driving off ahead and to the left, away from the place: the hull turns to its way,
		// the turret stays on the place ----
		Vector3 ahead = tank.GlobalPosition + ((-tank.GlobalBasis.Z - tank.GlobalBasis.X).Normalized() * 25f);
		Vector3 start = tank.GlobalPosition;
		Drive(tank, ahead);
		CallFire(tank, side, 10 * Second);
		float travelYaw = 0f;
		float worstLay = 0f;
		float widest = 0f;
		int moving = 0;
		for (int i = 0; i < 3 * Second; i++)
		{
			yield return true;
			Vector3 v = tank.Velocity;
			v.Y = 0f;
			if (v.Length() > 1f && i > Second / 2)
			{
				moving++;
				Aim.Angles(v, out travelYaw, out _);
				worstLay = Mathf.Max(worstLay, Off(tank.Yaw + tank.TurretYaw, YawTo(tank.GlobalPosition, side)));
				widest = Mathf.Max(widest, Mathf.Abs(tank.TurretYaw));
			}
		}

		float moved = tank.GlobalPosition.DistanceTo(start);
		Measured($"moved {F(moved)} m; hull {Deg(tank.Yaw)}, travel {Deg(travelYaw)}, turret {Deg(tank.TurretYaw)}");
		Fact(moving > Second && moved > 5f, $"it drives off ({F(moved)} m, {moving} ticks moving)");
		Fact(Off(tank.Yaw, hull) > Mathf.DegToRad(30f) && Off(tank.Yaw, travelYaw) < Mathf.DegToRad(5f),
			$"its hull turns to the way it is going ({Deg(hull)} → {Deg(tank.Yaw)}, off by {Deg(Off(tank.Yaw, travelYaw))})");
		Fact(worstLay < Mathf.DegToRad(3f), $"its turret stays on the place as it drives, never more than {Deg(worstLay)} off");
		Fact(widest > Mathf.DegToRad(60f), $"so the turret stands round against the hull, as far as {Deg(widest)}");
		Fact(tank.Turret != null && Off(DrawnYaw(tank.Turret), tank.Yaw + tank.TurretYaw) < 0.001f,
			"and the drawn turret with it");

		// ---- nothing to shoot at: the turret comes back square ----
		tank.HasCoverCall = false;
		tank.Order = UnitOrder.Hold(tank.GlobalPosition);
		foreach (bool _ in Until(() => Mathf.Abs(tank.TurretYaw) < 1e-4f, 2 * Second)) yield return true;
		Fact(!_timedOut && Mathf.Abs(tank.TurretYaw) < 1e-4f, $"with nothing to shoot at, the turret comes back square ({Deg(tank.TurretYaw)})");
		units.ClearUnits();
	}

	// ---- server --------------------------------------------------------------------------

	private IEnumerator<bool> Server()
	{
		foreach (bool _ in Until(() => Has("client-ready"), 60 * Second)) yield return true;
		Fact(!_timedOut, "a client has joined");
		UnitManager units = UnitManager.Instance;
		ushort id = units.ServerPlaceUnit(UnitCatalog.Tank, Site, Team.GroundForce, OrderKind.None);
		units.ServerSetPlacedAutonomy(id, MicroAutonomy.Off);
		Unit tank = units.Find(id);
		foreach (bool _ in Wait(Second)) yield return true;
		Fact(tank is { IsAlive: true }, $"a tank is placed (unit {id})");
		if (tank == null)
		{
			yield break;
		}

		// Laid on a place ahead and to its left, then held there while the client looks.
		Vector3 left = tank.GlobalPosition + (-tank.GlobalBasis.X * 14f) + (-tank.GlobalBasis.Z * 14f);
		CallFire(tank, left, 30 * Second);
		foreach (bool _ in Wait(Second)) yield return true;
		Mark("laid", string.Join(" ", id.ToString(CultureInfo.InvariantCulture),
			tank.Yaw.ToString("R", CultureInfo.InvariantCulture), tank.TurretYaw.ToString("R", CultureInfo.InvariantCulture)));
		Measured($"laid: hull {Deg(tank.Yaw)}, turret {Deg(tank.TurretYaw)} against it");
		foreach (bool _ in Until(() => Has("client-saw-laid"), 30 * Second)) yield return true;

		// Swung round through straight behind: from 150° one way to 150° the other, the short way.
		Mark("swing");
		Vector3 at = tank.GlobalPosition;
		float hull = tank.Yaw;
		for (int step = 0; step <= 6 * Second; step++)
		{
			float against = Mathf.DegToRad(150f + (60f * step / (6f * Second)));
			float world = hull + against;
			CallFire(tank, at + (new Vector3(-Mathf.Sin(world), 0f, -Mathf.Cos(world)) * 20f), Second);
			yield return true;
		}

		Measured($"swung to {Deg(tank.TurretYaw)} against the hull");
		Mark("swung");
		foreach (bool _ in Until(() => Has("finished-client"), 30 * Second)) yield return true;
	}

	// ---- client --------------------------------------------------------------------------

	private IEnumerator<bool> Client()
	{
		foreach (bool _ in Until(() => Session.InGame && UnitManager.Instance != null, 60 * Second)) yield return true;
		foreach (bool _ in Wait(Second)) yield return true;
		Mark("client-ready");

		foreach (bool _ in Until(() => Has("laid"), 60 * Second)) yield return true;
		string[] laid = Read("laid").Split(' ');
		ushort id = ushort.Parse(laid[0], CultureInfo.InvariantCulture);
		float hull = float.Parse(laid[1], CultureInfo.InvariantCulture);
		float turret = float.Parse(laid[2], CultureInfo.InvariantCulture);
		Unit tank = null;
		foreach (bool _ in Until(() => (tank = UnitManager.Instance.Find(id)) is { Visible: true }, 10 * Second)) yield return true;
		foreach (bool _ in Wait(Second / 2)) yield return true;
		Fact(tank != null && !tank.IsSimulated, $"the client has the server's tank (unit {id}), as a puppet");
		if (tank == null)
		{
			yield break;
		}

		DrawnAsTheModel(tank);

		// Half a step of the wire's 1.4°, and a little for the hull's 16-bit yaw.
		float step = Mathf.Pi / 256f;
		Fact(Off(tank.Yaw, hull) < 0.001f && Off(tank.TurretYaw, turret) <= step + 0.001f,
			$"its turret is where the server laid it: {Deg(tank.TurretYaw)} against {Deg(turret)}, hull {Deg(tank.Yaw)} against {Deg(hull)}");
		Fact(tank.Turret != null && Off(DrawnYaw(tank.Turret), tank.Yaw + tank.TurretYaw) < 0.001f,
			"and is drawn there");
		Mark("client-saw-laid");

		// Every frame of the swing: no jump, however it crosses straight behind.
		foreach (bool _ in Until(() => Has("swing"), 30 * Second)) yield return true;
		float last = tank.TurretYaw;
		float worst = 0f;
		bool crossed = false;
		foreach (bool _ in Until(() => Has("swung"), 30 * Second))
		{
			float now = tank.TurretYaw;
			worst = Mathf.Max(worst, Off(now, last));
			crossed |= Mathf.Abs(now) > Mathf.DegToRad(178f);
			last = now;
			yield return true;
		}

		foreach (bool _ in Wait(Second / 2))
		{
			worst = Mathf.Max(worst, Off(tank.TurretYaw, last));
			last = tank.TurretYaw;
			yield return true;
		}

		Fact(crossed, $"the drawn turret swings round through straight behind (now {Deg(tank.TurretYaw)})");
		Fact(worst < Mathf.DegToRad(20f), $"turning the short way: never more than {Deg(worst)} in a tick");
	}
}
