using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using Gdpyr.Core;
using Gdpyr.Fps;
using Gdpyr.Match;
using Gdpyr.Net;
using Gdpyr.Rts;
using Gdpyr.Sim;
using Gdpyr.Sim.Agent;
using Gdpyr.Ui;
using Godot;

namespace Gdpyr.EngineChecks;

/// <summary>
/// The helicopter in the game (docs/GROUND_VEHICLES.md §12, GV5), run headless by
/// <c>scripts/crew-vehicle-checks.sh heli</c> and <c>heli-net</c> on a copy of the tree
/// with this file added as an autoload; not part of the game.
///
/// <c>CREW_HELI_CHECK</c> names this process's part:
/// <list type="bullet">
/// <item><c>listen</c>: a listen host on <c>Test.tscn</c> at <c>--fixed-fps 60</c>, its
/// own player at the controls through the input map: the rotor's spin-up and the lift
/// it waits for; the ceiling and holding height; the heading coming round to the look,
/// the short way; the chase camera; the field's edge on both axes at full speed; landing
/// on the floor, where it neither slides nor turns; landing on the bunker's roof, out
/// and in again there; the ceiling over the roof, and back down to it off the roof's
/// edge; the pilot out at 4 m while it flies, and it settling; the barracks mortar
/// against it in the air, in its shells' reach and out of it, and on the ground (§14); and
/// the bunker, solid since GV5: its wall, its door's sill and the units' way round it.</item>
/// <item><c>server</c>, <c>pilot</c>, <c>passenger</c>: a dedicated server and two
/// clients behind the relay (40 ± 10 ms each way): both in over the link; the door's arc
/// on the floor; the passenger firing from the door while the pilot flies at full speed,
/// each round's two origins logged by projectile id, at a strategist rifleman on the
/// floor; landing on the floor; the pilot out at 4 m and it settling with the passenger
/// aboard; landing on the bunker's roof and both out on it; and the mortar letting go of
/// the pilot hovering out of its shells' reach and shelling the passenger on foot in it.</item>
/// </list>
///
/// One line per fact, <c>PASS</c> or <c>FAIL</c>, and <c>[checks] measured …</c> lines;
/// each process exits with the number of its facts that failed.
/// </summary>
public partial class CrewHelicopterChecks : Node
{
	private const int Second = SimConfig.TickRate;

	/// <summary>The floor's top on <c>Test.tscn</c>.</summary>
	private const float FloorY = 0.5f;

	/// <summary>The bunker's roof: <c>Pillbox</c>'s origin and its roof's height, 7.1246 m above it.</summary>
	private static readonly Vector3 Roof = new(-75.83365f, 0.5f + 7.1246f, 87.4643f);

	/// <summary>Where each helicopter is put: open floor, out of every barracks defence's reach.</summary>
	private static readonly Vector3 Pad = new(-40f, FloorY, 30f);

	/// <summary>The barracks mortar's muzzle on <c>Test.tscn</c>: its mount on the barracks' roof, 0.6 m up.</summary>
	private static readonly Vector3 MortarMuzzle = new(100f, 7.1f, -100f);

	private string _role;
	private string _dir;
	private IEnumerator<bool> _run;
	private int _failures;
	private int _facts;
	private bool _timedOut;

	private AgentEventLog _log;
	private ulong _cursor;
	private readonly AgentEvent[] _read = new AgentEvent[AgentEventLog.Capacity];
	private readonly List<AgentEvent> _events = new();

	public override void _Ready()
	{
		_role = System.Environment.GetEnvironmentVariable("CREW_HELI_CHECK");
		_dir = System.Environment.GetEnvironmentVariable("CREW_NET_DIR");
		if (string.IsNullOrEmpty(_role) || (_role != "listen" && string.IsNullOrEmpty(_dir)))
		{
			SetPhysicsProcess(false);
			return;
		}

		ProcessMode = ProcessModeEnum.Always;
		_run = _role switch
		{
			"listen" => Listen(),
			"server" => Server(),
			"pilot" => Pilot(),
			"passenger" => Passenger(),
			_ => null,
		};

		if (_run == null)
		{
			GD.PrintErr($"[checks] no such part: {_role}");
			SetPhysicsProcess(false);
			GetTree().Quit(99);
			return;
		}

		GD.Print($"[checks] helicopter check: {_role}");
	}

	public override void _PhysicsProcess(double delta)
	{
		if (_log != null)
		{
			int count = _log.Since(_cursor, _read, out _cursor, out ulong lost);
			for (int i = 0; i < count; i++)
			{
				_events.Add(_read[i]);
			}

			if (lost > 0)
			{
				Measured($"DIAG lost {lost} events");
			}
		}

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
			if (_dir != null)
			{
				Mark($"finished-{_role}");
			}

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

	private static string V(Vector3 v) => $"({F(v.X, "0.000")}, {F(v.Y, "0.000")}, {F(v.Z, "0.000")})";

	private void Mark(string name, string content = "")
	{
		File.WriteAllText(Path.Combine(_dir, name), content);
		GD.Print($"[checks] {_role} marks {name}");
	}

	private bool Has(string name) => File.Exists(Path.Combine(_dir, name));

	private string Read(string name) => File.ReadAllText(Path.Combine(_dir, name)).Trim();

	private int PeerOf(string role) =>
		int.TryParse(Read($"{role}.peer"), out int peer) ? peer : 0;

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

	private static float YawFacing(Vector3 direction) => Mathf.Atan2(-direction.X, -direction.Z);

	private static LocalInputSampler Sampler => PlayerManager.Instance.GetNode<LocalInputSampler>("LocalInputSampler");

	private static float Flat(Vector3 v) => new Vector2(v.X, v.Z).Length();

	private int Count(AgentEventKind kind, int from, Func<AgentEvent, bool> where = null)
	{
		int count = 0;
		for (int i = from; i < _events.Count; i++)
		{
			if (_events[i].Kind == kind && (where == null || where(_events[i])))
			{
				count++;
			}
		}

		return count;
	}

	private static int SeatOfRole(CrewVehicle vehicle, SeatRole role)
	{
		for (int i = 0; i < vehicle.Seats.Length; i++)
		{
			if (vehicle.Seats[i].Role == role)
			{
				return i;
			}
		}

		return -1;
	}

	private static CrewVehicle SpawnHelicopter(Vector3 at, float yaw)
	{
		CrewVehicleManager crew = CrewVehicleManager.Instance;
		int slot = crew.Spawn(CrewVehicleCatalog.Helicopter, at, yaw);
		return crew.VehicleAt(slot);
	}

	/// <summary>The body stood at a seat's door, facing the hull.</summary>
	private static void AtDoor(fps_controller character, CrewVehicle heli, SeatRole role, bool align)
	{
		int seat = SeatOfRole(heli, role);
		Vector3 door = heli.Pose.ToWorld(heli.Seats[seat].Door);
		float yaw = YawFacing(heli.Pose.Position - door);
		character.Teleport(new Transform3D(new Basis(Vector3.Up, yaw), door + (Vector3.Up * 0.1f)));
		if (align)
		{
			Sampler.Align(yaw, 0f);
		}
	}

	// ---- flying, through the input map -------------------------------------------------------

	/// <summary>Holds Space until the helicopter is at least <paramref name="height"/> above the ground under it.</summary>
	private IEnumerable<bool> ClimbTo(CrewVehicle heli, float height, int maxTicks)
	{
		Input.ActionPress("jump");
		foreach (bool _ in Until(() => heli.HeightAboveGround >= height, maxTicks)) yield return true;
		Input.ActionRelease("jump");
	}

	/// <summary>Holds Ctrl until it is on the ground; the fastest it came down, into <paramref name="fastest"/>.</summary>
	private IEnumerable<bool> LandHere(CrewVehicle heli, float[] fastest, int maxTicks)
	{
		Input.ActionPress("crouch");
		int n = 0;
		while (!heli.Grounded && n++ < maxTicks)
		{
			fastest[0] = Mathf.Max(fastest[0], -heli.Flight.Velocity.Y);
			yield return true;
		}

		Input.ActionRelease("crouch");
		foreach (bool _ in Wait(10)) yield return true;
	}

	/// <summary>
	/// Flies to over <paramref name="target"/> as a person would: the look on it, so the
	/// heading comes round to it; <c>W</c> while the way left is more than it takes to stop
	/// from the speed it has at <see cref="HelicopterTraits.Acceleration"/>; then nothing,
	/// and it comes back to a hover. Ends over the point, nearly still.
	/// </summary>
	private IEnumerable<bool> FlyTo(CrewVehicle heli, Vector3 target, int maxTicks, Action<CrewVehicle> each = null)
	{
		float accel = heli.FlightTraits.Acceleration;
		bool forward = false;
		_timedOut = false;
		int n = 0;
		while (true)
		{
			Vector3 to = target - heli.Flight.Position;
			to.Y = 0f;
			float d = to.Length();
			float v = Flat(heli.Flight.Velocity);
			if (d < 2f && v < 0.3f)
			{
				break;
			}

			if (n++ >= maxTicks)
			{
				_timedOut = true;
				break;
			}

			float want = d > 0.5f ? YawFacing(to) : heli.Flight.Yaw;
			Sampler.Align(want, -0.35f);
			bool facing = Mathf.Abs(Mathf.AngleDifference(heli.Flight.Yaw, want)) < 0.08f;
			bool go = facing && d > ((v * v) / (2f * accel)) + 1.5f;
			if (go != forward)
			{
				forward = go;
				if (go)
				{
					Input.ActionPress("move_forward");
				}
				else
				{
					Input.ActionRelease("move_forward");
				}
			}

			each?.Invoke(heli);
			yield return true;
		}

		Input.ActionRelease("move_forward");
	}

	// ---- the listen host -------------------------------------------------------------------

	private IEnumerator<bool> Listen()
	{
		_log = AgentEventBus.Active ??= new AgentEventLog();
		_cursor = _log.Sequence;

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

		foreach (bool _ in Until(() => local.IsAlive && local.Team == Team.GroundForce, 10 * Second)) yield return true;
		Fact(!_timedOut, "the listen host's player is alive on the ground force");
		CrewVehicleManager crew = CrewVehicleManager.Instance;
		fps_controller me = local.Character;
		HelicopterTraits traits = HelicopterTraits.Default;

		// ---- the rotor ----
		CrewVehicle heli = SpawnHelicopter(Pad, YawFacing(Vector3.Right));
		Fact(heli != null && heli.Kind == CrewVehicleKind.Helicopter, "a helicopter on the open floor, facing east");
		if (heli == null)
		{
			yield break;
		}

		foreach (bool _ in Wait(20)) yield return true;
		AtDoor(me, heli, SeatRole.Driver, align: true);
		foreach (bool _ in Wait(10)) yield return true;
		foreach (bool _ in Tap("use")) yield return true;
		Fact(crew.IsDriving(local.PeerId, out _), "a tap of E at the pilot's door seats the pilot");

		// Space at once: it leaves the ground only once the rotor can lift it.
		int seatedFor = 8;
		Input.ActionPress("jump");
		int liftTick = -1;
		int offTick = -1;
		int fullTick = -1;
		float rotorAtOff = 0f;
		for (int i = 0; i < 4 * Second && fullTick < 0; i++)
		{
			yield return true;
			seatedFor++;
			if (liftTick < 0 && heli.Flight.Rotor >= traits.LiftRotor)
			{
				liftTick = seatedFor;
			}

			if (offTick < 0 && !heli.Grounded)
			{
				offTick = seatedFor;
				rotorAtOff = heli.Flight.Rotor;
			}

			if (fullTick < 0 && heli.Flight.Rotor >= 1f)
			{
				fullTick = seatedFor;
			}
		}

		Measured($"the rotor: lift (60 %) {liftTick} ticks after the tap's release, full {fullTick}; off the ground"
			+ $" {offTick} ticks after, the rotor at {F(rotorAtOff)}");
		Fact(offTick > 0 && rotorAtOff >= traits.LiftRotor - 1e-4f && rotorAtOff < traits.LiftRotor + 0.05f,
			$"holding Space, it leaves the ground only once the rotor reaches 60 % ({F(rotorAtOff)})");
		Fact(Mathf.Abs(liftTick - (1.5f * Second)) <= 10 && Mathf.Abs(fullTick - (2.5f * Second)) <= 10,
			$"the rotor spins up in 2.5 s, lift at 1.5 s ({F(liftTick / (float)Second)} s, {F(fullTick / (float)Second)} s)");

		// ---- the ceiling ----
		float top = 0f;
		float climb = 0f;
		for (int i = 0; i < 8 * Second; i++)
		{
			top = Mathf.Max(top, heli.HeightAboveGround);
			climb = Mathf.Max(climb, heli.Flight.Velocity.Y);
			yield return true;
		}

		Input.ActionRelease("jump");
		float atCeiling = heli.HeightAboveGround;
		Measured($"8 s of Space: {F(atCeiling, "0.000")} m up, the highest {F(top, "0.000")} m, climbing at up to {F(climb)} m/s");
		Fact(climb <= traits.ClimbSpeed + 0.01f && climb > traits.ClimbSpeed - 0.1f, $"it climbs at 5 m/s ({F(climb)})");
		Fact(Mathf.Abs(atCeiling - traits.CeilingMeters) < 0.05f && top < traits.CeilingMeters + 0.05f,
			$"and no higher than its 25 m ceiling ({F(top, "0.000")} m)");
		foreach (bool _ in Wait(2 * Second)) yield return true;
		Fact(Mathf.Abs(heli.HeightAboveGround - atCeiling) < 0.05f,
			$"with neither Space nor Ctrl it holds its height ({F(atCeiling, "0.000")} → {F(heli.HeightAboveGround, "0.000")} m)");

		// ---- the camera ----
		Camera3D camera = GetViewport().GetCamera3D();
		Vector3 behind = camera != null ? camera.GlobalPosition - heli.Pose.Position : Vector3.Zero;
		Vector3 look = Aim.Direction(me.Yaw, 0f);
		Measured($"the pilot's camera: {camera?.Name}, {F(behind.Dot(look))} m along the look, {F(behind.Length())} m from the skids");
		Fact(camera != null && camera.Name == "ChaseCamera" && behind.Dot(look) < -5f,
			"the pilot sees through the chase camera, behind the helicopter along the look");

		// ---- the heading ----
		float h0 = heli.Flight.Yaw;
		Sampler.Align(h0 + (Mathf.Pi * 0.5f), -0.3f);
		int turned = 0;
		float fastestTurn = 0f;
		while (Mathf.Abs(Mathf.AngleDifference(heli.Flight.Yaw, h0 + (Mathf.Pi * 0.5f))) > 0.001f && turned < 3 * Second)
		{
			yield return true;
			turned++;
			fastestTurn = Mathf.Max(fastestTurn, Mathf.Abs(heli.Flight.YawRate));
		}

		Fact(fastestTurn <= traits.TurnRate + 1e-3f && Mathf.Abs(turned - Second) <= 3,
			$"the heading comes round 90° to the look in a second, at 90°/s ({turned} ticks, {F(Mathf.RadToDeg(fastestTurn), "0.0")}°/s)");
		float h1 = heli.Flight.Yaw;
		Sampler.Align(h1 + (Mathf.Pi * 1.5f), -0.3f);
		foreach (bool _ in Wait(5)) yield return true;
		float firstWay = Mathf.AngleDifference(h1, heli.Flight.Yaw);
		foreach (bool _ in Until(() => Mathf.Abs(Mathf.AngleDifference(heli.Flight.Yaw, h1 + (Mathf.Pi * 1.5f))) < 0.001f,
			3 * Second))
		{
			yield return true;
		}

		Fact(firstWay < 0f && !_timedOut, $"a look 270° to the left turns it 90° to the right, the short way ({F(Mathf.RadToDeg(firstWay), "0.0")}° in 5 ticks)");

		// ---- the field's edge, east then north ----
		float east = YawFacing(Vector3.Right);
		Sampler.Align(east, -0.3f);
		foreach (bool _ in Until(() => Mathf.Abs(Mathf.AngleDifference(heli.Flight.Yaw, east)) < 0.001f, 3 * Second)) yield return true;
		Input.ActionPress("move_forward");
		float fastest = 0f;
		float noseDown = 0f;
		float furthestX = float.MinValue;
		for (int i = 0; i < 12 * Second; i++)
		{
			fastest = Mathf.Max(fastest, Flat(heli.Flight.Velocity));
			noseDown = Mathf.Min(noseDown, heli.Flight.Pitch);
			furthestX = Mathf.Max(furthestX, heli.Flight.Position.X);
			yield return true;
		}

		Input.ActionRelease("move_forward");
		Measured($"12 s of W east from x {F(Pad.X, "0.0")}: {F(fastest)} m/s at most, nose down to {F(Mathf.RadToDeg(noseDown), "0.0")}°,"
			+ $" as far as x {F(furthestX, "0.000")}, now {F(Flat(heli.Flight.Velocity))} m/s");
		Fact(Mathf.Abs(fastest - traits.MaxForwardSpeed) < 0.1f, $"W flies it at 22 m/s ({F(fastest)})");
		Fact(noseDown < Mathf.DegToRad(-15f) && noseDown >= -traits.MaxTilt - 1e-4f,
			$"nose down while it gathers speed, no more than 20° ({F(Mathf.RadToDeg(noseDown), "0.0")}°)");
		Fact(furthestX <= CrewField.HalfExtentMeters + 0.01f && furthestX > CrewField.HalfExtentMeters - 0.1f,
			$"and it stops at the field's edge, x 115 ({F(furthestX, "0.000")})");

		float north = YawFacing(Vector3.Back);
		Sampler.Align(north, -0.3f);
		foreach (bool _ in Until(() => Mathf.Abs(Mathf.AngleDifference(heli.Flight.Yaw, north)) < 0.001f, 3 * Second)) yield return true;
		Input.ActionPress("move_forward");
		float furthestZ = float.MinValue;
		for (int i = 0; i < 8 * Second; i++)
		{
			furthestZ = Mathf.Max(furthestZ, heli.Flight.Position.Z);
			furthestX = Mathf.Max(furthestX, heli.Flight.Position.X);
			yield return true;
		}

		Input.ActionRelease("move_forward");
		Fact(furthestZ <= CrewField.HalfExtentMeters + 0.01f && furthestZ > CrewField.HalfExtentMeters - 0.1f
			&& furthestX <= CrewField.HalfExtentMeters + 0.01f,
			$"and at z 115 ({F(furthestZ, "0.000")}), still inside x 115 ({F(furthestX, "0.000")})");
		foreach (bool _ in Until(() => Flat(heli.Flight.Velocity) < 0.05f, 6 * Second)) yield return true;

		// ---- landing on the floor ----
		float[] down = { 0f };
		foreach (bool _ in LandHere(heli, down, 15 * Second)) yield return true;
		Measured($"down in the corner: skids at y {F(heli.Flight.Position.Y, "0.000")}, {F(heli.HeightAboveGround, "0.000")} m up,"
			+ $" coming down at up to {F(down[0])} m/s");
		Fact(heli.Grounded && heli.HeightAboveGround <= HelicopterSim.GroundedMeters && Mathf.Abs(heli.Flight.Position.Y - FloorY) < 0.06f,
			"Ctrl lands it on the floor");
		Fact(down[0] <= traits.DescentSpeed + 0.01f && down[0] > traits.DescentSpeed - 0.1f, $"coming down at 6 m/s ({F(down[0])})");

		Vector3 landed = heli.Flight.Position;
		float landedYaw = heli.Flight.Yaw;
		Input.ActionPress("move_forward");
		Input.ActionPress("move_right");
		Sampler.Align(landedYaw + 1.2f, -0.3f);
		foreach (bool _ in Wait(Second)) yield return true;
		Input.ActionRelease("move_forward");
		Input.ActionRelease("move_right");
		Measured($"W, D and a look 69° round, for a second on the ground: moved {F(heli.Flight.Position.DistanceTo(landed), "0.0000")} m,"
			+ $" turned {F(Mathf.RadToDeg(Mathf.AngleDifference(landedYaw, heli.Flight.Yaw)), "0.000")}°");
		Fact(heli.Flight.Position.DistanceTo(landed) < 0.005f && Mathf.Abs(Mathf.AngleDifference(landedYaw, heli.Flight.Yaw)) < 1e-4f,
			"on the ground, with the rotor up, the stick and the look neither slide it nor turn it");

		// ---- the bunker's roof ----
		foreach (bool _ in ClimbTo(heli, 15f, 10 * Second)) yield return true;
		foreach (bool _ in FlyTo(heli, Roof, 40 * Second)) yield return true;
		Fact(!_timedOut, $"flown across the arena to over the bunker's roof ({F(Flat(heli.Flight.Position - Roof))} m from its middle)");
		down[0] = 0f;
		foreach (bool _ in LandHere(heli, down, 15 * Second)) yield return true;
		Measured($"down on the roof: skids at {V(heli.Flight.Position)}, {F(heli.HeightAboveGround, "0.000")} m up; the roof at y {F(Roof.Y, "0.000")}");
		Fact(heli.Grounded && Mathf.Abs(heli.Flight.Position.Y - Roof.Y) < 0.06f && Flat(heli.Flight.Position - Roof) < 4f,
			"and lands on the bunker's roof");

		foreach (bool _ in Tap("use")) yield return true;
		foreach (bool _ in Wait(30)) yield return true;
		Measured($"out on the roof at {V(me.SimPosition)}, on the floor {me.IsOnFloor()}");
		Fact(!crew.IsSeated(local.PeerId) && Mathf.Abs(me.SimPosition.Y - Roof.Y) < 0.1f && me.IsOnFloor(),
			"a tap of E puts the pilot out on the roof, standing on it");
		AtDoor(me, heli, SeatRole.Driver, align: true);
		foreach (bool _ in Wait(10)) yield return true;
		string prompt = EmplacementManager.Instance.PromptFor(local, NetworkManager.Instance.Tick);
		foreach (bool _ in Tap("use")) yield return true;
		Fact(prompt == "[E] fly helicopter" && crew.IsDriving(local.PeerId, out _),
			$"and back in from the roof ({prompt})");

		// ---- the ceiling over the roof, and off its edge ----
		foreach (bool _ in Wait(30)) yield return true;
		Input.ActionPress("jump");
		foreach (bool _ in Wait(9 * Second)) yield return true;
		Input.ActionRelease("jump");
		float overRoof = heli.Flight.Position.Y - Roof.Y;
		Measured($"8 s of Space over the roof: skids at y {F(heli.Flight.Position.Y, "0.000")}, {F(overRoof, "0.000")} m over the roof");
		Fact(Mathf.Abs(overRoof - traits.CeilingMeters) < 0.05f, "over the roof its ceiling is 25 m above the roof");

		Vector3 offEdge = new(Roof.X + 45f, FloorY, Roof.Z);
		float sinking = 0f;
		foreach (bool _ in FlyTo(heli, offEdge, 20 * Second, h => sinking = Mathf.Max(sinking, -h.Flight.Velocity.Y))) yield return true;
		foreach (bool _ in Wait(2 * Second)) yield return true;
		Measured($"flown off the roof's edge: {F(heli.HeightAboveGround, "0.000")} m above the floor, coming down at up to {F(sinking)} m/s");
		Fact(Mathf.Abs(heli.HeightAboveGround - traits.CeilingMeters) < 0.05f && sinking <= traits.DescentSpeed + 0.01f,
			"off the roof's edge it comes back down to 25 m above the floor, no faster than it descends");

		// ---- the pilot out at 4 m, flying ----
		down[0] = 0f;
		foreach (bool _ in LandHere(heli, down, 15 * Second)) yield return true;
		foreach (bool _ in ClimbTo(heli, 2.1f, 5 * Second)) yield return true;
		foreach (bool _ in Wait(Second + (Second / 2))) yield return true;
		float hover = heli.HeightAboveGround;
		Sampler.Align(east, -0.3f);
		Input.ActionPress("move_forward");
		foreach (bool _ in Wait(2 * Second)) yield return true;
		float atExit = heli.HeightAboveGround;
		float speedAtExit = Flat(heli.Flight.Velocity);
		Vector3 exitFrom = heli.Flight.Position;
		foreach (bool _ in Tap("use")) yield return true;
		Input.ActionRelease("move_forward");
		bool outAt4 = !crew.IsSeated(local.PeerId);
		float settleDescent = 0f;
		int groundedAfter = -1;
		for (int i = 0; i < 5 * Second; i++)
		{
			settleDescent = Mathf.Max(settleDescent, -heli.Flight.Velocity.Y);
			if (groundedAfter < 0 && heli.Grounded)
			{
				groundedAfter = i;
			}

			yield return true;
		}

		Vector3 settled = heli.Flight.Position;
		float rotorAfter4 = heli.Flight.Rotor;
		foreach (bool _ in Wait(3 * Second)) yield return true;
		Measured($"hovering at {F(hover, "0.00")} m, then {F(atExit)} m up at {F(speedAtExit)} m/s: the pilot out at"
			+ $" {V(me.SimPosition)}; the helicopter came down at up to {F(settleDescent)} m/s, on the ground {groundedAfter} ticks"
			+ $" later, {F(Flat(settled - exitFrom), "0.0")} m on; the rotor {F(rotorAfter4)} 5 s after; moved"
			+ $" {F(heli.Flight.Position.DistanceTo(settled), "0.0000")} m in the 3 s after");
		Fact(outAt4 && atExit <= 4f && atExit > 3f, $"the pilot gets out 4 m up or less, while it flies ({F(atExit)} m, {F(speedAtExit)} m/s)");
		Fact(Mathf.Abs(me.SimPosition.Y - FloorY) < 0.1f && me.IsOnFloor(), "and is put down on the floor");
		Fact(settleDescent <= traits.UnpilotedDescentSpeed + 0.01f && groundedAfter > 0 && groundedAfter < 2 * Second,
			$"with nobody at the controls it comes down no faster than 4 m/s and lands ({F(settleDescent)} m/s, {groundedAfter} ticks)");
		Fact(heli.Grounded && heli.Flight.Position.DistanceTo(settled) < 0.005f && rotorAfter4 == 0f,
			"and settles: on the ground, still, its rotor stopped");

		// ---- the barracks mortar (§14) ----
		foreach (bool _ in Mortar(local, heli)) yield return true;

		// ---- the bunker ----
		foreach (bool _ in TheBunker(local)) yield return true;

		Console("vehicle clear");
		foreach (bool _ in Wait(5)) yield return true;
		Fact(crew.Count == 0, "`vehicle clear` takes it away");
	}

	private void Console(string line)
	{
		MethodInfo submit = typeof(GameConsole).GetMethod("Submit", BindingFlags.NonPublic | BindingFlags.Instance);
		submit.Invoke(GameConsole.Instance, new object[] { line });
	}

	private static DefenseMount TheMortar()
	{
		DefenseBattery battery = UnitManager.Instance.Defenses;
		for (int i = 0; i < battery.Count; i++)
		{
			if (battery.At(i) is { Kind: DefenseKind.Mortar } mount)
			{
				return mount;
			}
		}

		return null;
	}

	/// <summary>
	/// The barracks' two guns taken out of the battery, so that only its mortar engages:
	/// what is measured is the mortar alone.
	/// </summary>
	private void StandTheGunsDown()
	{
		foreach (Node node in GetTree().GetNodesInGroup(DefenseMount.Group))
		{
			if (node is DefenseMount { Kind: DefenseKind.Gun } gun)
			{
				gun.RemoveFromGroup(DefenseMount.Group);
			}
		}

		UnitManager.Instance.Defenses.Collect(GetTree());
	}

	private static ProjectileSim Projectiles =>
		(ProjectileSim)typeof(CombatManager).GetField("_projectiles", BindingFlags.NonPublic | BindingFlags.Instance)
			.GetValue(CombatManager.Instance);

	/// <summary>
	/// <paramref name="ticks"/> of the mortar with <paramref name="heli"/> where it is: how
	/// many shells it fired, how many struck the hull, whether it held the pilot as its
	/// target, and how far from the hull's middle each shell came down through its height.
	/// The hull is kept at full health, so that the mortar's shells do not end it.
	/// </summary>
	private IEnumerable<bool> UnderTheMortar(CrewVehicle heli, int pilot, int ticks, int[] result, List<float> passes)
	{
		DefenseMount mortar = TheMortar();
		uint pulls = mortar.Weapon.ShotIndex;
		int marker = _events.Count;
		int held = 0;
		var crossed = new HashSet<uint>();
		var shells = new HashSet<uint>();

		// Shells already in the air when it starts were fired at somewhere else.
		var before = new HashSet<uint>();
		Passes(mortar.ShooterId, heli.HullCentre, null, before, new HashSet<uint>(), new List<float>());
		for (int i = 0; i < ticks; i++)
		{
			heli.Health = heli.Definition.MaxHealth;
			held += mortar.TargetPeerId == pilot ? 1 : 0;
			Passes(mortar.ShooterId, heli.HullCentre, before, shells, crossed, passes);
			yield return true;
		}

		// Shells in the air: the weapon's own count is of pulls, and a pull the solver
		// finds no arc for spawns nothing (DefenseBattery.FireMortar).
		result[0] = shells.Count;
		result[1] = Count(AgentEventKind.VehicleDamaged, marker, e => e.A == mortar.ShooterId && e.B == heli.Slot);
		result[2] = held;
		result[3] = (int)(mortar.Weapon.ShotIndex - pulls);
	}

	/// <summary>
	/// <paramref name="owner"/>'s shells in the air this tick but those in <paramref name="ignore"/>,
	/// into <paramref name="shells"/>, and each coming down through <paramref name="centre"/>'s
	/// height: how far from it, flat.
	/// </summary>
	private static void Passes(int owner, Vector3 centre, HashSet<uint> ignore, HashSet<uint> shells, HashSet<uint> crossed,
		List<float> passes)
	{
		ReadOnlySpan<ProjectileSegment> segments = Projectiles.Segments;
		for (int i = 0; i < segments.Length; i++)
		{
			ProjectileSegment segment = segments[i];
			if (segment.OwnerPeerId != owner || (ignore != null && ignore.Contains(segment.Id)))
			{
				continue;
			}

			shells.Add(segment.Id);
			if (segment.From.Y > centre.Y && segment.To.Y <= centre.Y && crossed.Add(segment.Id))
			{
				float f = (segment.From.Y - centre.Y) / (segment.From.Y - segment.To.Y);
				passes.Add(Flat(segment.PointAt(f) - centre));
			}
		}
	}

	private IEnumerable<bool> Mortar(PlayerCombat local, CrewVehicle heli)
	{
		CrewVehicleManager crew = CrewVehicleManager.Instance;
		fps_controller me = local.Character;
		DefenseMount mortar = TheMortar();
		Fact(mortar != null, "the barracks has its mortar");
		if (mortar == null)
		{
			yield break;
		}

		StandTheGunsDown();
		Fact(UnitManager.Instance.Defenses.Count == 1, "its two guns stood down, so the mortar is measured alone");
		mortar = TheMortar();

		AtDoor(me, heli, SeatRole.Driver, align: true);
		foreach (bool _ in Wait(10)) yield return true;
		foreach (bool _ in Tap("use")) yield return true;
		foreach (bool _ in Wait(2 * Second)) yield return true;
		foreach (bool _ in ClimbTo(heli, 24.9f, 10 * Second)) yield return true;

		// 63 m from the muzzle, 25 m up: inside its reach (§14).
		Vector3 near = new(MortarMuzzle.X - 60f, FloorY, MortarMuzzle.Z);
		foreach (bool _ in FlyTo(heli, near, 30 * Second, h => h.Health = h.Definition.MaxHealth)) yield return true;
		foreach (bool _ in Wait(Second)) yield return true;
		var result = new int[4];
		var passes = new List<float>();
		float distance = heli.Pose.Position.DistanceTo(MortarMuzzle);
		foreach (bool _ in UnderTheMortar(heli, local.PeerId, 24 * Second, result, passes)) yield return true;
		passes.Sort();
		Measured($"hovering {F(heli.HeightAboveGround, "0.0")} m up, {F(distance, "0.0")} m from the mortar, 24 s: {result[3]} pulls,"
			+ $" {result[0]} shells, {result[1]} on the hull, the pilot its target on {result[2]} ticks; each came down through the hull's height"
			+ $" {string.Join(", ", passes.ConvertAll(p => F(p, "0.0")))} m from its middle");
		Fact(result[0] >= 5 && result[1] >= 1 && result[2] > 0,
			$"hovering 25 m up in its reach, the mortar shells it, and its shells come down on the hull ({result[1]} of {result[0]})");

		// 103 m flat, 25 m up: inside the ring it holds a target in, out of its shells' reach.
		Vector3 far = new(MortarMuzzle.X - 103f, FloorY, MortarMuzzle.Z);
		foreach (bool _ in FlyTo(heli, far, 30 * Second, h => h.Health = h.Definition.MaxHealth)) yield return true;
		foreach (bool _ in Wait(Second)) yield return true;
		passes.Clear();
		distance = heli.Pose.Position.DistanceTo(MortarMuzzle);
		foreach (bool _ in UnderTheMortar(heli, local.PeerId, 20 * Second, result, passes)) yield return true;
		Measured($"hovering {F(heli.HeightAboveGround, "0.0")} m up, {F(distance, "0.0")} m from the mortar, 20 s: {result[3]} pulls,"
			+ $" {result[0]} shells, {result[1]} on the hull, the pilot its target on {result[2]} ticks");
		Fact(result[0] == 0 && result[2] == 0,
			$"103 m off and 25 m up, out of its shells' reach, the mortar does not hold it ({result[2]} ticks held, {result[0]} shells)");

		// 97 m flat, on the ground: in reach, a target like any other.
		Vector3 ground = new(MortarMuzzle.X - 97f, FloorY, MortarMuzzle.Z);
		foreach (bool _ in FlyTo(heli, ground, 30 * Second, h => h.Health = h.Definition.MaxHealth)) yield return true;
		float[] down = { 0f };
		foreach (bool _ in LandHere(heli, down, 15 * Second)) yield return true;
		passes.Clear();
		distance = heli.Pose.Position.DistanceTo(MortarMuzzle);
		foreach (bool _ in UnderTheMortar(heli, local.PeerId, 20 * Second, result, passes)) yield return true;
		passes.Sort();
		Measured($"on the ground {F(distance, "0.0")} m from the mortar, 20 s: {result[3]} pulls, {result[0]} shells, {result[1]} on the hull,"
			+ $" the pilot its target on {result[2]} ticks; each came down through the hull's height"
			+ $" {string.Join(", ", passes.ConvertAll(p => F(p, "0.0")))} m from its middle");
		Fact(result[0] >= 3 && result[2] > 0, $"landed there, it is shelled like anybody on the ground ({result[0]} shells)");

		foreach (bool _ in Tap("use")) yield return true;
		Fact(!crew.IsSeated(local.PeerId), "and the pilot gets out");
	}

	// ---- the bunker, now solid -------------------------------------------------------------

	/// <summary>The bunker's frame on <c>Test.tscn</c>: its origin and its turn.</summary>
	private static readonly Transform3D Bunker = new(
		new Basis(new Vector3(-0.7202122f, 0f, 0.69375384f), Vector3.Up, new Vector3(-0.69375384f, 0f, -0.7202122f)),
		new Vector3(-75.83365f, 0.5f, 87.46430f));

	/// <summary>
	/// The bunker is solid (Scenes/Environment/Pillbox.tscn): a player walking into its
	/// wall stops at it; one gets in through its door by jumping the sill; the units'
	/// navigation goes round it.
	/// </summary>
	private IEnumerable<bool> TheBunker(PlayerCombat local)
	{
		fps_controller me = local.Character;
		Vector3 outward = Bunker.Basis * Vector3.Right;

		// Into its +Z wall, from outside, 3 m off.
		Vector3 wallFace = Bunker * new Vector3(0f, 0f, 15f);
		Vector3 wallNormal = Bunker.Basis * Vector3.Back;
		Vector3 start = wallFace + (wallNormal * 3f) + (Vector3.Up * 0.05f);
		me.Teleport(new Transform3D(new Basis(Vector3.Up, YawFacing(-wallNormal)), start));
		Sampler.Align(YawFacing(-wallNormal), 0f);
		foreach (bool _ in Wait(10)) yield return true;
		Input.ActionPress("move_forward");
		foreach (bool _ in Wait(2 * Second)) yield return true;
		Input.ActionRelease("move_forward");
		float off = (me.SimPosition - wallFace).Dot(wallNormal);
		Measured($"walking 2 s into the bunker's wall: {F(off, "0.000")} m from its face");
		Fact(off > 0.2f && off < 0.6f, "a player walking into the bunker's wall stops at it");

		// Through the door: up to its sill, then a jump.
		Vector3 door = Bunker * new Vector3(15f, 0f, 0f);
		start = door + (outward * 4f) + (Vector3.Up * 0.05f);
		me.Teleport(new Transform3D(new Basis(Vector3.Up, YawFacing(-outward)), start));
		Sampler.Align(YawFacing(-outward), 0f);
		foreach (bool _ in Wait(10)) yield return true;
		Input.ActionPress("move_forward");
		foreach (bool _ in Wait(2 * Second)) yield return true;
		float atSill = (me.SimPosition - door).Dot(outward);
		Measured($"walking at the door: stopped {F(atSill, "0.000")} m outside its face, at y {F(me.SimPosition.Y, "0.000")}");
		Input.ActionPress("jump");
		foreach (bool _ in Wait(6)) yield return true;
		Input.ActionRelease("jump");
		foreach (bool _ in Wait(2 * Second)) yield return true;
		Input.ActionRelease("move_forward");
		foreach (bool _ in Wait(20)) yield return true;
		Vector3 local3 = Bunker.AffineInverse() * me.SimPosition;
		Measured($"after a jump at the sill and 2 s more of W: at {V(me.SimPosition)}, {V(local3)} in the bunker's frame");
		Fact(atSill > 0.2f, "walking, a player stops at the door's 0.42 m sill");
		Fact(local3.X < 13.7f && Mathf.Abs(local3.Z) < 13.7f && me.IsOnFloor() && local3.Y < 0.5f,
			"and gets in over it with a jump, onto the floor inside");

		// The units' navigation, across it.
		Rid map = me.GetWorld3D().NavigationMap;
		Vector3 west = Bunker * new Vector3(-25f, 0f, 0f);
		Vector3 eastOf = Bunker * new Vector3(25f, 0f, 0f);
		eastOf += Bunker.Basis * new Vector3(0f, 0f, 20f);
		Vector3[] path = NavigationServer3D.MapGetPath(map, west, eastOf, true);
		float length = 0f;
		bool inside = false;
		for (int i = 1; i < path.Length; i++)
		{
			length += path[i].DistanceTo(path[i - 1]);
			for (int k = 0; k <= 10; k++)
			{
				Vector3 p = Bunker.AffineInverse() * path[i - 1].Lerp(path[i], k / 10f);
				inside |= Mathf.Abs(p.X) < 14.9f && Mathf.Abs(p.Z) < 14.9f && p.Y < 6f;
			}
		}

		Measured($"a unit's path across the bunker, {F(west.DistanceTo(eastOf), "0.0")} m as the crow flies: {path.Length} points,"
			+ $" {F(length, "0.0")} m");
		Fact(path.Length > 1 && !inside && length > west.DistanceTo(eastOf) + 1f,
			"the units' navigation goes round the bunker, not through it");
	}

	// ---- the dedicated server, a pilot and a passenger -----------------------------------------

	/// <summary>Where the helicopter is put for the link's checks: open floor, the bunker 25 m off.</summary>
	private static readonly Vector3 NetPad = new(-65f, FloorY, 65f);

	/// <summary>The strategist rifleman the passenger fires at, 25 m off the line the pilot flies east along.</summary>
	private static readonly Vector3 PassTarget = new(20f, FloorY, 40f);

	/// <summary>Where the pass ends, and the pilot lets it come to a hover, short of the field's edge.</summary>
	private const float PassEndX = 45f;

	/// <summary>The passenger on foot for the mortar: 97 m flat north-west of it, on the ground, in its reach.</summary>
	private static readonly Vector3 OnFootUnderTheMortar = new(MortarMuzzle.X - 68.6f, FloorY, MortarMuzzle.Z + 68.6f);

	private int _pilot;
	private int _passenger;

	/// <summary>A client in the game on the ground force, alive; its peer id marked for the server.</summary>
	private IEnumerable<bool> Join()
	{
		foreach (bool _ in Until(() => Session.InGame && CombatManager.Instance?.Local?.Character != null
			&& CrewVehicleManager.Instance != null && NetworkManager.Instance is { Connected: true }, 60 * Second))
		{
			yield return true;
		}

		Fact(!_timedOut, "connected, in the game, with a character");
		foreach (bool _ in Wait(30)) yield return true;

		PlayerCombat local = CombatManager.Instance.Local;
		CombatManager.Instance.RequestTeam(Team.GroundForce);
		foreach (bool _ in Until(() => local.Team == Team.GroundForce && local.IsAlive, 20 * Second)) yield return true;
		Fact(!_timedOut, "on the ground force");
		Mark($"{_role}.peer", NetworkManager.Instance.LocalPeerId.ToString(CultureInfo.InvariantCulture));
	}

	/// <summary>Taps E when the server says, and waits to be in the seat of <paramref name="role"/>.</summary>
	private IEnumerable<bool> GetIn(string marker, SeatRole role)
	{
		foreach (bool _ in Until(() => Has(marker), 240 * Second)) yield return true;
		CrewVehicleManager crew = CrewVehicleManager.Instance;
		PlayerCombat local = CombatManager.Instance.Local;
		foreach (bool _ in Until(() => crew.VehicleAt(0) is { Destroyed: false }, 10 * Second)) yield return true;
		foreach (bool _ in Wait(Second)) yield return true;
		foreach (bool _ in Tap("use")) yield return true;
		foreach (bool _ in Until(() => crew.TrySeat(local.PeerId, out CrewVehicle v, out int s) && v.Seats[s].Role == role,
			5 * Second))
		{
			yield return true;
		}

		Fact(!_timedOut, $"{marker}: a tap of E, over the link, puts this client in the {role}'s seat");
		Mark($"{_role}-{marker}");
	}

	/// <summary>Taps E when the server says, and waits to be out.</summary>
	private IEnumerable<bool> GetOut(string marker)
	{
		foreach (bool _ in Until(() => Has(marker), 240 * Second)) yield return true;
		fps_controller me = CombatManager.Instance.Local.Character;
		foreach (bool _ in Tap("use")) yield return true;
		foreach (bool _ in Until(() => !me.IsSeated, 5 * Second)) yield return true;
		Fact(!_timedOut, $"{marker}: a tap of E gets this client out");
		Mark($"{_role}-{marker}");
	}

	private static void Place(int peer, CrewVehicle heli, SeatRole role) =>
		AtDoor(CombatManager.Instance.Find(peer).Character, heli, role, align: false);

	private IEnumerator<bool> Server()
	{
		_log = AgentEventBus.Active ??= new AgentEventLog();
		_cursor = _log.Sequence;

		foreach (bool _ in Until(() => Session.InGame && CrewVehicleManager.Instance != null, 60 * Second)) yield return true;
		foreach (bool _ in Until(() => Has("pilot.peer") && Has("passenger.peer"), 120 * Second)) yield return true;
		Fact(!_timedOut, "the pilot and the passenger are in");
		if (_timedOut)
		{
			yield break;
		}

		_pilot = PeerOf("pilot");
		_passenger = PeerOf("passenger");
		CrewVehicleManager crew = CrewVehicleManager.Instance;
		CombatManager combat = CombatManager.Instance;
		UnitManager units = UnitManager.Instance;
		PlayerManager players = PlayerManager.Instance;
		combat.SeatedRoundFired += (id, peer, tick, view) => Measured(
			$"seated round {id} by {(peer == _passenger ? "passenger" : peer.ToString())}: tick {tick},"
			+ $" input {players.ConsumedInputTickOf(peer)}, drawn {players.DrawnTickOf(peer)}, view {view}");

		// 1. Both in, on the floor.
		CrewVehicle heli = SpawnHelicopter(NetPad, YawFacing(Vector3.Right));
		Fact(heli != null && heli.Slot == 0, "a helicopter in slot 0, facing east");
		if (heli == null)
		{
			yield break;
		}

		foreach (bool _ in Wait(30)) yield return true;
		Place(_pilot, heli, SeatRole.Driver);
		Place(_passenger, heli, SeatRole.Passenger);
		Mark("board");
		foreach (bool _ in Until(() => crew.IsDriving(_pilot, out _) && heli.Aboard == 2, 20 * Second)) yield return true;
		Fact(!_timedOut, "over the link, the pilot is at the controls and the passenger in the door");

		// 2. The door's arc, parked: the passenger's own facts.
		Mark("arc");
		foreach (bool _ in Until(() => Has("passenger-arc"), 30 * Second)) yield return true;

		// 3. Up, and a pass at full speed with the passenger firing from the door.
		Mark("fly");
		foreach (bool _ in Until(() => Has("pilot-climbed"), 30 * Second)) yield return true;
		Measured($"climbed: {F(heli.HeightAboveGround)} m up");
		ushort rifleman = units.ServerPlaceUnit(UnitCatalog.Infantry, PassTarget, Team.Strategist, OrderKind.None);
		Fact(rifleman != 0, "a strategist rifleman on the floor, 25 m off the line the pilot will fly");
		int marker = _events.Count;
		float fastest = 0f;
		float healthBefore = heli.Health;
		Mark("pass");
		foreach (bool _ in Until(() => Has("pilot-passed"), 40 * Second))
		{
			fastest = Mathf.Max(fastest, Flat(heli.Flight.Velocity));
			yield return true;
		}

		int hits = Count(AgentEventKind.Damage, marker, e => e.A == OwnerId.ForPeer(_passenger) && e.B == OwnerId.ForUnit(rifleman));
		int ownHull = Count(AgentEventKind.VehicleDamaged, marker, e => e.A == OwnerId.ForPeer(_passenger));
		int hullHits = Count(AgentEventKind.VehicleDamaged, marker, e => e.B == heli.Slot);
		Measured($"the pass: {F(fastest)} m/s at most, {F(heli.HeightAboveGround)} m up after it; the passenger hit the rifleman"
			+ $" {hits} times; their rounds on their own helicopter {ownHull}; the hull hit {hullHits} times,"
			+ $" {F(healthBefore, "0")} → {F(heli.Health, "0")}; the passenger {combat.Find(_passenger).Health}");
		Fact(fastest > 21.5f, $"the pilot flew the pass at full speed ({F(fastest)} m/s)");
		Fact(hits >= 3, $"the passenger's rounds from the door hit the rifleman on the floor ({hits})");
		Fact(ownHull == 0, "and none of them hit their own helicopter");
		units.ClearUnits();

		// 4. Down on the floor.
		Mark("land");
		foreach (bool _ in Until(() => Has("pilot-landed"), 30 * Second)) yield return true;
		Fact(heli.Grounded && Mathf.Abs(heli.Flight.Position.Y - FloorY) < 0.06f,
			$"the pilot lands it on the floor, over the link (skids at y {F(heli.Flight.Position.Y, "0.000")})");

		// 5. The pilot out at 4 m, flying; the passenger aboard.
		Mark("bail");
		foreach (bool _ in Until(() => !crew.IsSeated(_pilot), 40 * Second)) yield return true;
		float atExit = heli.HeightAboveGround;
		float speedAtExit = Flat(heli.Flight.Velocity);
		float settleDescent = 0f;
		int groundedAfter = -1;
		for (int i = 0; i < 5 * Second; i++)
		{
			settleDescent = Mathf.Max(settleDescent, -heli.Flight.Velocity.Y);
			if (groundedAfter < 0 && heli.Grounded)
			{
				groundedAfter = i;
			}

			yield return true;
		}

		Vector3 settled = heli.Flight.Position;
		foreach (bool _ in Wait(2 * Second)) yield return true;
		fps_controller pilotBody = combat.Find(_pilot).Character;
		Measured($"the pilot out {F(atExit)} m up at {F(speedAtExit)} m/s, put down at {V(pilotBody.SimPosition)}; the helicopter"
			+ $" came down at up to {F(settleDescent)} m/s, on the ground {groundedAfter} ticks later; moved"
			+ $" {F(heli.Flight.Position.DistanceTo(settled), "0.0000")} m in the 2 s after; the passenger seated {crew.IsSeated(_passenger)}");
		Fact(atExit <= 4f && atExit > 3f && speedAtExit > 5f, $"the pilot got out 4 m up or less while it flew ({F(atExit)} m, {F(speedAtExit)} m/s)");
		Fact(Mathf.Abs(pilotBody.SimPosition.Y - FloorY) < 0.1f, "and is put down on the floor");
		Fact(settleDescent <= 4.01f && groundedAfter > 0 && groundedAfter < 2 * Second,
			$"with nobody at the controls it came down no faster than 4 m/s and landed ({F(settleDescent)} m/s, {groundedAfter} ticks)");
		Fact(heli.Grounded && heli.Flight.Position.DistanceTo(settled) < 0.005f && crew.IsSeated(_passenger),
			"and settled, still, the passenger still aboard");
		Mark("settled");

		// 6. The bunker's roof: the pilot back in, flown there and down; both out on it.
		foreach (bool _ in Wait(Second)) yield return true;
		Place(_pilot, heli, SeatRole.Driver);
		Mark("reboard");
		foreach (bool _ in Until(() => Has("pilot-roof"), 90 * Second)) yield return true;
		Measured($"on the roof: skids at {V(heli.Flight.Position)}");
		Fact(heli.Grounded && Mathf.Abs(heli.Flight.Position.Y - Roof.Y) < 0.06f && Flat(heli.Flight.Position - Roof) < 5f,
			"the pilot lands it on the bunker's roof, over the link");
		Mark("roof-out");
		foreach (bool _ in Until(() => !crew.IsSeated(_pilot) && !crew.IsSeated(_passenger), 20 * Second)) yield return true;
		foreach (bool _ in Wait(Second)) yield return true;
		Vector3 pilotOut = combat.Find(_pilot).Character.SimPosition;
		Vector3 passengerOut = combat.Find(_passenger).Character.SimPosition;
		Measured($"out on the roof: the pilot at {V(pilotOut)}, the passenger at {V(passengerOut)}");
		Fact(Mathf.Abs(pilotOut.Y - Roof.Y) < 0.1f && Mathf.Abs(passengerOut.Y - Roof.Y) < 0.1f,
			"both get out on the roof and stand on it");

		// 7. The mortar (§14): the pilot hovering out of its reach, the passenger on foot in it.
		foreach (Node node in GetTree().GetNodesInGroup(DefenseMount.Group))
		{
			if (node is DefenseMount { Kind: DefenseKind.Gun } gun)
			{
				gun.RemoveFromGroup(DefenseMount.Group);
			}
		}

		units.Defenses.Collect(GetTree());
		DefenseMount mortar = TheMortar();
		Place(_pilot, heli, SeatRole.Driver);
		Mark("roof-in");
		foreach (bool _ in Until(() => Has("pilot-near"), 90 * Second))
		{
			heli.Health = heli.Definition.MaxHealth;
			yield return true;
		}

		foreach (bool _ in Until(() => mortar.TargetPeerId == _pilot, 10 * Second)) yield return true;
		Fact(!_timedOut, $"hovering 25 m up {F(heli.Pose.Position.DistanceTo(MortarMuzzle), "0.0")} m from the mortar, in its reach, the pilot is its target");

		combat.Find(_passenger).Character.Teleport(new Transform3D(Basis.Identity, OnFootUnderTheMortar + (Vector3.Up * 0.05f)));
		Mark("away");
		foreach (bool _ in Until(() => Has("pilot-far"), 60 * Second))
		{
			heli.Health = heli.Definition.MaxHealth;
			yield return true;
		}

		int heldPilot = 0;
		int heldPassenger = 0;
		var shells = new HashSet<uint>();
		var before = new HashSet<uint>();
		Passes(mortar.ShooterId, heli.HullCentre, null, before, new HashSet<uint>(), new List<float>());
		marker = _events.Count;
		PlayerCombat passenger = combat.Find(_passenger);
		float passengerAway = passenger.Character.SimPosition.DistanceTo(MortarMuzzle);
		for (int i = 0; i < 20 * Second; i++)
		{
			heli.Health = heli.Definition.MaxHealth;
			heldPilot += mortar.TargetPeerId == _pilot ? 1 : 0;
			heldPassenger += mortar.TargetPeerId == _passenger ? 1 : 0;
			Passes(mortar.ShooterId, heli.HullCentre, before, shells, new HashSet<uint>(), new List<float>());
			yield return true;
		}

		int hurt = Count(AgentEventKind.Damage, marker, e => e.A == mortar.ShooterId && e.B == _passenger);
		int killed = Count(AgentEventKind.Kill, marker, e => e.A == mortar.ShooterId && e.B == _passenger);
		Measured($"the pilot hovering {F(heli.Pose.Position.DistanceTo(MortarMuzzle), "0.0")} m from the mortar, 25 m up; the"
			+ $" passenger on foot {F(passengerAway, "0.0")} m from it: 20 s, the pilot its target on {heldPilot} ticks, the"
			+ $" passenger on {heldPassenger}; {shells.Count} shells; {hurt} hurt the passenger, {killed} killed them");
		Fact(heldPilot == 0, "out of its shells' reach, the pilot is not held by the mortar");
		Fact(heldPassenger > 0 && shells.Count >= 1 && hurt >= 1,
			$"and it takes the passenger on foot in its reach instead, and shells them ({shells.Count} shells, {hurt} hits)");

		Mark("done");
		foreach (bool _ in Until(() => Has("finished-pilot") && Has("finished-passenger"), 60 * Second)) yield return true;
	}

	// ---- the pilot ---------------------------------------------------------------------------

	private IEnumerator<bool> Pilot()
	{
		foreach (bool _ in Join()) yield return true;
		CrewVehicleManager crew = CrewVehicleManager.Instance;
		fps_controller me = CombatManager.Instance.Local.Character;

		foreach (bool _ in GetIn("board", SeatRole.Driver)) yield return true;
		foreach (bool _ in Until(() => crew.DrivenSlot == 0, 5 * Second)) yield return true;
		CrewVehicle heli = crew.VehicleAt(0);
		Fact(!_timedOut && heli != null, "this client predicts the helicopter it flies");
		float east = YawFacing(Vector3.Right);
		Sampler.Align(east, -0.3f);

		// 3. Up, then east at full throttle along z 65.
		foreach (bool _ in Until(() => Has("fly"), 60 * Second)) yield return true;
		foreach (bool _ in Wait(3 * Second)) yield return true;
		foreach (bool _ in ClimbTo(heli, 10f, 10 * Second)) yield return true;
		foreach (bool _ in Wait(Second + (Second / 2))) yield return true;
		Measured($"climbed to {F(heli.HeightAboveGround)} m; replays so far {crew.Replays}, snaps {crew.Snaps}");
		Mark("pilot-climbed");

		foreach (bool _ in Until(() => Has("pass"), 30 * Second)) yield return true;
		Sampler.Align(east, -0.3f);
		Input.ActionPress("move_forward");
		foreach (bool _ in Until(() => heli.Flight.Position.X >= PassEndX, 20 * Second)) yield return true;
		Input.ActionRelease("move_forward");
		foreach (bool _ in Until(() => Flat(heli.Flight.Velocity) < 0.3f, 10 * Second)) yield return true;
		Measured($"after the pass: at {V(heli.Flight.Position)}; replays {crew.Replays}, the longest {crew.LongestReplayTicks}"
			+ $" ticks, the largest correction {F(crew.LargestCorrectionMeters, "0.000")} m, {crew.CorrectionsShownAtOnce} shown at once");
		Mark("pilot-passed");

		// 4. Down.
		foreach (bool _ in Until(() => Has("land"), 30 * Second)) yield return true;
		float[] down = { 0f };
		foreach (bool _ in LandHere(heli, down, 15 * Second)) yield return true;
		Fact(heli.Grounded, "Ctrl lands it, on this client");
		Mark("pilot-landed");

		// 5. Out at 4 m, flying forward, back west.
		foreach (bool _ in Until(() => Has("bail"), 30 * Second)) yield return true;
		float west = YawFacing(Vector3.Left);
		foreach (bool _ in ClimbTo(heli, 2.1f, 5 * Second)) yield return true;
		Sampler.Align(west, -0.3f);
		foreach (bool _ in Until(() => Mathf.Abs(Mathf.AngleDifference(heli.Flight.Yaw, west)) < 0.01f, 4 * Second))
		{
			yield return true;
		}

		foreach (bool _ in Wait(Second / 2)) yield return true;
		Input.ActionPress("move_forward");
		foreach (bool _ in Wait(2 * Second)) yield return true;
		float atExit = heli.HeightAboveGround;
		foreach (bool _ in Tap("use")) yield return true;
		Input.ActionRelease("move_forward");
		foreach (bool _ in Until(() => !me.IsSeated, 3 * Second)) yield return true;
		Fact(!_timedOut, $"a tap of E {F(atExit)} m up gets this client out while it flies");
		foreach (bool _ in Until(() => crew.DrivenSlot < 0, 2 * Second)) yield return true;
		Fact(!_timedOut, "and it stops predicting the helicopter");
		Mark("pilot-bailed");

		// 6. Back in, to the bunker's roof, down on it, out and in again.
		foreach (bool _ in GetIn("reboard", SeatRole.Driver)) yield return true;
		foreach (bool _ in Until(() => crew.DrivenSlot == 0, 5 * Second)) yield return true;
		heli = crew.VehicleAt(0);
		foreach (bool _ in Wait(3 * Second)) yield return true;
		foreach (bool _ in ClimbTo(heli, 15f, 10 * Second)) yield return true;
		foreach (bool _ in FlyTo(heli, Roof, 50 * Second)) yield return true;
		down[0] = 0f;
		foreach (bool _ in LandHere(heli, down, 15 * Second)) yield return true;
		Fact(heli.Grounded && Mathf.Abs(heli.Flight.Position.Y - Roof.Y) < 0.06f,
			$"down on the bunker's roof, on this client (skids at y {F(heli.Flight.Position.Y, "0.000")})");
		Measured($"to the roof: replays {crew.Replays}, the longest {crew.LongestReplayTicks} ticks, the largest correction"
			+ $" {F(crew.LargestCorrectionMeters, "0.000")} m, {crew.CorrectionsShownAtOnce} shown at once");
		Mark("pilot-roof");
		foreach (bool _ in GetOut("roof-out")) yield return true;
		foreach (bool _ in Wait(Second)) yield return true;
		Fact(Mathf.Abs(me.SimPosition.Y - Roof.Y) < 0.1f, $"out on the roof, on this client (y {F(me.SimPosition.Y, "0.000")})");

		// 7. In again; to 63 m from the mortar, 25 m up; then 103 m flat.
		foreach (bool _ in GetIn("roof-in", SeatRole.Driver)) yield return true;
		foreach (bool _ in Until(() => crew.DrivenSlot == 0, 5 * Second)) yield return true;
		heli = crew.VehicleAt(0);
		foreach (bool _ in Wait(3 * Second)) yield return true;
		foreach (bool _ in ClimbTo(heli, 24.9f, 15 * Second)) yield return true;
		foreach (bool _ in FlyTo(heli, new Vector3(MortarMuzzle.X - 60f, FloorY, MortarMuzzle.Z), 60 * Second)) yield return true;
		Mark("pilot-near");
		foreach (bool _ in Until(() => Has("away"), 30 * Second)) yield return true;
		foreach (bool _ in FlyTo(heli, new Vector3(MortarMuzzle.X - 103f, FloorY, MortarMuzzle.Z), 60 * Second)) yield return true;
		Mark("pilot-far");
		foreach (bool _ in Until(() => Has("done"), 60 * Second)) yield return true;
	}

	// ---- the passenger -----------------------------------------------------------------------

	private IEnumerator<bool> Passenger()
	{
		foreach (bool _ in Join()) yield return true;
		CrewVehicleManager crew = CrewVehicleManager.Instance;
		PlayerCombat local = CombatManager.Instance.Local;
		fps_controller me = local.Character;

		foreach (bool _ in GetIn("board", SeatRole.Passenger)) yield return true;
		CrewVehicle heli = crew.VehicleAt(0);

		// 2. The door's arc, on the floor, facing east: the door is on the left, -Z.
		foreach (bool _ in Until(() => Has("arc"), 30 * Second)) yield return true;
		Sampler.Align(YawFacing(Vector3.Back), 0f);
		foreach (bool _ in Wait(10)) yield return true;
		string right = SeatedLine(local);
		Sampler.Align(YawFacing(Vector3.Forward), 0f);
		foreach (bool _ in Wait(10)) yield return true;
		string left = SeatedLine(local);
		Sampler.Align(YawFacing(Vector3.Forward), -1.4f);
		foreach (bool _ in Wait(10)) yield return true;
		string under = SeatedLine(local);
		Fact(right.EndsWith("no shot") && !left.EndsWith("no shot") && under.EndsWith("no shot"),
			$"in the door, no shot to the right or straight down, a shot to the left ({right}; {left}; {under})");

		short rounds = local.Weapons[SimConfig.LargeSlot].Ammo;
		Sampler.Align(YawFacing(Vector3.Back), 0f);
		foreach (bool _ in Wait(10)) yield return true;
		Input.ActionPress("fire");
		foreach (bool _ in Wait(Second / 2)) yield return true;
		Input.ActionRelease("fire");
		Fact(local.Weapons[SimConfig.LargeSlot].Ammo == rounds,
			$"and the trigger does nothing to the right ({rounds} → {local.Weapons[SimConfig.LargeSlot].Ammo})");
		Mark("passenger-arc");

		// 3. The pass: every round's two origins, by projectile id.
		var apart = new List<float>();
		bool recording = false;
		void Adopted(uint id, uint tick, Vector3 tracer, Vector3 server)
		{
			if (!recording)
			{
				return;
			}

			float d = tracer.DistanceTo(server);
			apart.Add(d);
			Measured($"round {id}: tracer from {V(tracer)}, the server's from {V(server)}, {F(d, "0.000")} m apart; input tick {tick}");
		}

		CombatManager.Instance.PredictionAdopted += Adopted;
		foreach (bool _ in Until(() => Has("pass"), 60 * Second)) yield return true;
		bool firing = false;
		int fireTicks = 0;
		int guard = 0;
		while (!Has("pilot-passed") && guard++ < 40 * Second)
		{
			Aim.Angles(PassTarget + (Vector3.Up * 1f) - me.EyePosition, out float yaw, out float pitch);
			Sampler.Align(yaw, pitch);
			CrewPose pose = heli.DrawnPose;
			bool fast = Flat(heli.Flight.Velocity) > 20f;
			bool abreast = Mathf.Abs(pose.Position.X - PassTarget.X) < 25f;
			bool fire = fast && abreast;
			if (fire != firing)
			{
				firing = fire;
				recording |= fire;
				if (fire)
				{
					Input.ActionPress("fire");
				}
				else
				{
					Input.ActionRelease("fire");
				}
			}

			fireTicks += firing ? 1 : 0;
			yield return true;
		}

		Input.ActionRelease("fire");
		foreach (bool _ in Wait(Second)) yield return true;
		recording = false;
		CombatManager.Instance.PredictionAdopted -= Adopted;
		apart.Sort();
		float worst = apart.Count > 0 ? apart[^1] : float.MaxValue;
		float mean = 0f;
		foreach (float d in apart)
		{
			mean += d;
		}

		mean = apart.Count > 0 ? mean / apart.Count : float.MaxValue;
		Measured($"{F(fireTicks / (float)Second)} s of fire from the door at full speed: {apart.Count} rounds adopted, origins"
			+ $" {F(mean, "0.000")} m apart on average, {F(worst, "0.000")} m at worst");
		Fact(apart.Count >= 10, $"at least ten rounds fired from the door at full speed ({apart.Count})");
		Fact(worst < 0.3f, $"this client's tracer and the server's round leave within 0.3 m of each other ({F(worst, "0.000")} m)");

		// 5. The pilot gets out at 4 m; the passenger rides it down.
		foreach (bool _ in Until(() => Has("settled"), 90 * Second)) yield return true;
		foreach (bool _ in Wait(Second)) yield return true;
		crew.TrySeat(local.PeerId, out CrewVehicle seatedIn, out int seat);
		Vector3 eye = heli.DrawnPose.ToWorld(heli.Seats[Mathf.Max(seat, 0)].Eye);
		Fact(seatedIn == heli && heli.Grounded && me.EyePosition.DistanceTo(eye) < 0.01f,
			$"riding it down with nobody at the controls, this client is still in the door of a helicopter on the ground ({F(me.EyePosition.DistanceTo(eye), "0.000")} m from the seat's eye)");

		// 6. Out on the roof.
		foreach (bool _ in GetOut("roof-out")) yield return true;
		foreach (bool _ in Wait(Second)) yield return true;
		Fact(Mathf.Abs(me.SimPosition.Y - Roof.Y) < 0.1f, $"out on the roof, on this client (y {F(me.SimPosition.Y, "0.000")})");

		foreach (bool _ in Until(() => Has("done"), 240 * Second)) yield return true;
	}

	private static string SeatedLine(PlayerCombat local) =>
		EmplacementManager.Instance.PromptFor(local, NetworkManager.Instance.Tick).Split('\n')[0];
}
