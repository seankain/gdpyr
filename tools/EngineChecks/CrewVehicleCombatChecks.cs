using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Gdpyr.Core;
using Gdpyr.Fps;
using Gdpyr.Match;
using Gdpyr.Net;
using Gdpyr.Rts;
using Gdpyr.Sim;
using Gdpyr.Sim.Agent;
using Godot;

namespace Gdpyr.EngineChecks;

/// <summary>
/// Combat from and against the crew vehicles (docs/GROUND_VEHICLES.md §12, GV4): a
/// dedicated server and three clients, each its own headless process, the clients behind
/// the relay that delays every datagram 40 ± 10 ms each way. Run by
/// <c>scripts/crew-vehicle-checks.sh combat</c> on a copy of the tree with this file
/// added as an autoload; not part of the game.
///
/// <c>CREW_COMBAT_CHECK</c> names this process's part — <c>server</c>, <c>driver</c>,
/// <c>gunner</c> or <c>passenger</c> — and <c>CREW_NET_DIR</c> a directory the parts
/// leave marker files in. In order:
/// <list type="number">
/// <item>All three get into a 4x4 on the lane at z 50, over the link.</item>
/// <item>Parked, the gunner fires the gun for a second: its box counts down on its client
/// on the tick the trigger is sampled, and the server's agrees afterwards.</item>
/// <item>The driver takes the lane at full throttle; on the straight the gunner and the
/// passenger fire to the left, and each client logs, by projectile id, where its tracer
/// left from and where the server's round did.</item>
/// <item>With only the driver aboard, a strategist rifleman shoots at them: every round
/// stops at the hull.</item>
/// <item>All three back in, a tank's round strikes the hull: one hit, a wreck, the driver
/// out at 50, the exposed crew hurt by the blast as well; the wreck goes two seconds
/// later.</item>
/// <item>A new 4x4 at full throttle through three riflemen abreast: each dies, in its
/// driver's name.</item>
/// <item>At a quarter throttle into three more: none dies.</item>
/// </list>
///
/// One line per fact, <c>PASS</c> or <c>FAIL</c>, and <c>[checks] measured …</c> lines;
/// each process exits with the number of its facts that failed.
/// </summary>
public partial class CrewVehicleCombatChecks : Node
{
	private const int Second = SimConfig.TickRate;

	/// <summary>Where each 4x4 is put: past the test strip, on the clear lane at z 50, facing east.</summary>
	private static readonly Vector3 Start = new(-60f, 0.5f, 50f);

	/// <summary>Out of every unit's way while it is not their turn.</summary>
	private static readonly Vector3 Aside = new(-100f, 1f, -100f);

	/// <summary>The lane's straight at full speed, as GV3 measured it.</summary>
	private const float StraightFromX = -20f;

	private const float StraightToX = 58f;

	private const float FullSpeed = 16.5f;

	/// <summary>A throttle whose strength, past the actions' 0.5 dead zone, reads as a quarter (§5.1: 4.5 m/s).</summary>
	private const float QuarterThrottleStrength = 0.625f;

	private string _role;
	private string _dir;
	private IEnumerator<bool> _run;
	private int _failures;
	private int _facts;
	private bool _timedOut;

	// ---- the server's view of the event stream ---------------------------------------------

	private AgentEventLog _log;
	private ulong _cursor;
	private readonly AgentEvent[] _read = new AgentEvent[AgentEventLog.Capacity];
	private readonly List<AgentEvent> _events = new();

	public override void _Ready()
	{
		_role = System.Environment.GetEnvironmentVariable("CREW_COMBAT_CHECK");
		_dir = System.Environment.GetEnvironmentVariable("CREW_NET_DIR");
		if (string.IsNullOrEmpty(_role) || string.IsNullOrEmpty(_dir))
		{
			SetPhysicsProcess(false);
			return;
		}

		ProcessMode = ProcessModeEnum.Always;
		_run = _role switch
		{
			"server" => Server(),
			"driver" => Driver(),
			"gunner" => Gunner(),
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

		GD.Print($"[checks] combat check: {_role}");
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

	private static LocalInputSampler Sampler => PlayerManager.Instance.GetNode<LocalInputSampler>("LocalInputSampler");

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

	// ---- the server ----------------------------------------------------------------------

	private int _driver;
	private int _gunner;
	private int _passenger;

	private void Place(int peer, CrewVehicle truck, SeatRole role)
	{
		int seat = SeatOfRole(truck, role);
		Vector3 door = truck.Pose.ToWorld(truck.Seats[seat].Door);
		CombatManager.Instance.Find(peer).Character.Teleport(new Transform3D(
			new Basis(Vector3.Up, YawFacing(truck.Pose.Position - door)), door + (Vector3.Up * 0.1f)));
	}

	private void PutAside(int peer, int index) =>
		CombatManager.Instance.Find(peer).Character.Teleport(new Transform3D(Basis.Identity,
			Aside + new Vector3(index * 3f, 0f, 0f)));

	private CrewVehicle SpawnTruck()
	{
		CrewVehicleManager crew = CrewVehicleManager.Instance;
		CrewVehicleDefinition definition = CrewVehicleCatalog.Definition(CrewVehicleCatalog.FourByFour);
		int slot = crew.Spawn(CrewVehicleCatalog.FourByFour,
			Start + (Vector3.Up * definition.ToFourByFourTraits().RideHeightMeters), YawFacing(Vector3.Right));
		return crew.VehicleAt(slot);
	}

	private IEnumerator<bool> Server()
	{
		_log = AgentEventBus.Active ??= new AgentEventLog();
		_cursor = _log.Sequence;

		foreach (bool _ in Until(() => Session.InGame && CrewVehicleManager.Instance != null, 60 * Second)) yield return true;
		foreach (bool _ in Until(() => Has("driver.peer") && Has("gunner.peer") && Has("passenger.peer"), 120 * Second))
		{
			yield return true;
		}

		Fact(!_timedOut, "the driver, the gunner and the passenger are in");
		if (_timedOut)
		{
			yield break;
		}

		_driver = PeerOf("driver");
		_gunner = PeerOf("gunner");
		_passenger = PeerOf("passenger");
		CrewVehicleManager crew = CrewVehicleManager.Instance;
		CombatManager combat = CombatManager.Instance;
		UnitManager units = UnitManager.Instance;
		PlayerManager players = PlayerManager.Instance;
		combat.SeatedRoundFired += (id, peer, tick, view) => Measured(
			$"seated round {id} by {(peer == _gunner ? "gunner" : peer == _passenger ? "passenger" : peer.ToString())}:"
			+ $" tick {tick}, input {players.ConsumedInputTickOf(peer)}, lag {combat.Find(peer).LagCompensationTicks},"
			+ $" depth {players.InputBufferDepthOf(peer)}, drawn {players.DrawnTickOf(peer)}, view {view}");

		// 1. All three in.
		CrewVehicle truck = SpawnTruck();
		Fact(truck != null && truck.Slot == 0 && truck.Gun != null, "a 4x4 with its gun in slot 0 on the lane");
		if (truck == null)
		{
			yield break;
		}

		foreach (bool _ in Wait(30)) yield return true;
		Place(_driver, truck, SeatRole.Driver);
		Place(_gunner, truck, SeatRole.Gunner);
		Place(_passenger, truck, SeatRole.Passenger);
		Mark("board");
		foreach (bool _ in Until(() => crew.IsDriving(_driver, out _) && truck.Aboard == 3, 20 * Second)) yield return true;
		Fact(!_timedOut, "over the link, the driver is at the wheel, the gunner at the gun and the passenger in the bed");

		// 2. The gun's box, parked.
		Mark("gun");
		foreach (bool _ in Until(() => Has("gunner-fired"), 30 * Second)) yield return true;
		foreach (bool _ in Wait(Second)) yield return true;
		Mark("server-box", truck.Gun.Box.Ammo.ToString(CultureInfo.InvariantCulture));
		Measured($"the server's box after the gunner's burst: {truck.Gun.Box.Ammo}");

		// Forward and down at the gun's lowest, its muzzle inside the cab's box: a round fired
		// from a seat never hits that vehicle (GV-D6).
		float healthBeforeDown = truck.Health;
		int damagedBeforeDown = Count(AgentEventKind.VehicleDamaged, 0);
		short boxBeforeDown = truck.Gun.Box.Ammo;
		foreach (bool _ in Until(() => Has("gunner-down"), 30 * Second)) yield return true;
		foreach (bool _ in Wait(Second)) yield return true;
		truck.Gun.Pose = truck.Pose;
		Vector3 muzzle0 = truck.Gun.MuzzleFor(truck.Gun.AimFor(YawFacing(Vector3.Right), -0.26f));
		float muzzleOut = Hitbox.DistanceTo(truck.Hull[0].Place(truck.Pose), muzzle0);
		Measured($"forward and down: {boxBeforeDown - truck.Gun.Box.Ammo} rounds from a muzzle {F(muzzleOut, "0.000")} m"
			+ $" outside the cab's box; the 4x4 {F(healthBeforeDown, "0")} → {F(truck.Health, "0")}");
		Fact(boxBeforeDown - truck.Gun.Box.Ammo >= 3 && muzzleOut == 0f && truck.Health == healthBeforeDown
			&& Count(AgentEventKind.VehicleDamaged, 0) == damagedBeforeDown,
			"the gun fired forward and down, its muzzle inside the cab, never hits its own 4x4");

		// 3. Full speed, the gunner and the passenger firing to the left.
		float healthBeforeDrive = truck.Health;
		int damagedBeforeDrive = Count(AgentEventKind.VehicleDamaged, 0);
		Mark("drive");
		foreach (bool _ in Until(() => Has("driver-drove"), 90 * Second)) yield return true;
		Fact(!_timedOut, "the driver drove the lane");
		Fact(truck.Health == healthBeforeDrive && Count(AgentEventKind.VehicleDamaged, 0) == damagedBeforeDrive,
			$"no round its own gunner or passenger fired on the move hit the 4x4 ({F(truck.Health, "0")}/600)");

		// 4. A rifleman 35 m off, with only the driver aboard.
		Mark("dismount");
		foreach (bool _ in Until(() => Has("gunner-dismount") && Has("passenger-dismount"), 20 * Second)) yield return true;
		PutAside(_gunner, 0);
		PutAside(_passenger, 1);
		foreach (bool _ in Wait(Second)) yield return true;

		PlayerCombat driverCombat = combat.Find(_driver);
		int driverHealth = driverCombat.Health;
		float hullBefore = truck.Health;
		int marker = _events.Count;
		Vector3 rifleAt = truck.Pose.Position + new Vector3(0f, 0f, -35f);
		rifleAt.Y = 0.5f;
		ushort rifleman = units.ServerPlaceUnit(UnitCatalog.Infantry, rifleAt, Team.Strategist, OrderKind.None);
		Fact(rifleman != 0, "a strategist rifleman 35 m from the parked 4x4");
		foreach (bool _ in Wait(8 * Second)) yield return true;

		int hullHits = Count(AgentEventKind.VehicleDamaged, marker, e => e.B == truck.Slot);
		Measured($"8 s of a rifleman at the driver: the hull hit {hullHits} times, {F(hullBefore, "0")} → {F(truck.Health, "0")};"
			+ $" the driver {driverHealth} → {driverCombat.Health}");
		Fact(hullHits >= 3, $"the rifleman's rounds reach the hull ({hullHits} hits)");
		Fact(driverCombat.Health == driverHealth && driverCombat.IsAlive,
			$"and none reaches the driver inside it ({driverCombat.Health})");
		Fact(Mathf.IsEqualApprox(hullBefore - truck.Health, hullHits * 20f * 0.15f),
			$"each takes 0.15 of the rifle's 20 ({F(hullBefore - truck.Health, "0.0")} for {hullHits})");
		units.ClearUnits();
		foreach (bool _ in Wait(Second)) yield return true;

		// 5. All three in again, and a tank's round at the hull.
		Place(_gunner, truck, SeatRole.Gunner);
		Place(_passenger, truck, SeatRole.Passenger);
		Mark("reboard");
		foreach (bool _ in Until(() => truck.Aboard == 3, 20 * Second)) yield return true;
		Fact(!_timedOut, "the gunner and the passenger are back in");
		foreach (bool _ in Wait(Second)) yield return true;

		int[] crewPeers = { _driver, _gunner, _passenger };
		var before = new int[3];
		for (int i = 0; i < 3; i++)
		{
			before[i] = combat.Find(crewPeers[i]).Health;
		}

		// To its left: the lane runs east to the field's edge, and the truck stopped near it.
		Vector3 ahead = truck.Pose.Position + new Vector3(0f, 0f, -30f);
		ahead.Y = 0.5f;
		ushort tankId = units.ServerPlaceUnit(UnitCatalog.Tank, ahead, Team.Strategist, OrderKind.None);
		Unit tank = units.Find(tankId);
		Fact(tank != null, "a strategist tank 30 m off its left side");
		if (tank == null)
		{
			yield break;
		}

		marker = _events.Count;
		Vector3 muzzle = tank.GlobalPosition + (Vector3.Up * 1.8f);
		Vector3 aim = (truck.HullCentre - muzzle).Normalized();
		uint shot = combat.SpawnProjectile(NetworkManager.Instance.Tick, OwnerId.ForUnit(tankId), WeaponCatalog.Cannon,
			muzzle, aim, 0);
		Measured($"the tank's round {shot} from {V(muzzle)} at the hull's centre {V(truck.HullCentre)}");
		foreach (bool _ in Until(() => truck.Destroyed && !crew.IsSeated(_driver), 2 * Second)) yield return true;
		foreach (bool _ in Wait(2)) yield return true;
		var after = new int[3];
		for (int i = 0; i < 3; i++)
		{
			after[i] = combat.Find(crewPeers[i]).Health;
		}

		units.ClearUnits();
		int damaged = Count(AgentEventKind.VehicleDamaged, marker, e => e.B == truck.Slot);
		int destroyed = Count(AgentEventKind.VehicleDestroyed, marker, e => e.B == truck.Slot);
		int exited = Count(AgentEventKind.VehicleExited, marker, e => e.B == truck.Slot);
		Measured($"after the tank's round: {damaged} vehicle_damaged, {destroyed} vehicle_destroyed, {exited} vehicle_exited;"
			+ $" health driver {before[0]} → {after[0]}, gunner {before[1]} → {after[1]}, passenger {before[2]} → {after[2]}");
		Fact(truck.Destroyed && damaged == 1 && destroyed == 1,
			"one direct hit by the tank's round, and the 4x4 is a wreck");
		Fact(exited == 3 && !crew.IsSeated(_driver) && !crew.IsSeated(_gunner) && !crew.IsSeated(_passenger),
			$"everybody aboard is thrown out ({exited} vehicle_exited)");
		Fact(after[0] == before[0] - 50, $"the driver, whom the hull shielded from the blast, gets out at 50 ({after[0]})");
		Fact(after[1] < before[1] - 50 && after[2] < before[2] - 50,
			$"the gunner and the passenger take the shell's blast as well as the wreck's 50 ({after[1]}, {after[2]})");
		fps_controller driverBody = driverCombat.Character;
		float clear = truck.HullDistance(driverBody.SimPosition + Vector3.Up);
		Fact(clear > 0.3f, $"the driver is put down clear of the wreck ({F(clear)} m from it)");
		Mark("wrecked", $"{after[0]} {after[1]} {after[2]}");

		uint wreckedAt = truck.DestroyedTick;
		foreach (bool _ in Until(() => crew.VehicleAt(0) == null, 4 * Second)) yield return true;
		uint gone = NetworkManager.Instance.Tick;
		Fact(!_timedOut && gone - wreckedAt >= (uint)CrewCombat.WreckBurnTicks && gone - wreckedAt <= (uint)CrewCombat.WreckBurnTicks + 2,
			$"the wreck burns two seconds and goes ({gone - wreckedAt} ticks)");

		// 6. A new 4x4 at full throttle through three riflemen abreast.
		foreach (bool _ in Until(() => driverCombat.IsAlive, 20 * Second)) yield return true;
		PutAside(_gunner, 0);
		PutAside(_passenger, 1);
		truck = SpawnTruck();
		Fact(truck != null, "a new 4x4 on the lane");
		if (truck == null)
		{
			yield break;
		}

		foreach (bool _ in Wait(30)) yield return true;
		Place(_driver, truck, SeatRole.Driver);
		Mark("ram-board");
		foreach (bool _ in Until(() => crew.IsDriving(_driver, out _), 20 * Second)) yield return true;
		Fact(!_timedOut, "the driver is at its wheel");

		var abreast = new List<ushort>();
		for (int i = -1; i <= 1; i++)
		{
			abreast.Add(units.ServerPlaceUnit(UnitCatalog.Infantry, new Vector3(0f, 0.5f, 50f + (i * 0.8f)), Team.Strategist,
				OrderKind.None));
		}

		Fact(!abreast.Contains(0), "three strategist riflemen abreast across the lane, 0.8 m apart, 60 m ahead");
		int killsBefore = driverCombat.Kills;
		marker = _events.Count;
		Mark("ram");
		foreach (bool _ in Until(() => Has("driver-rammed"), 40 * Second)) yield return true;
		int rammed = Count(AgentEventKind.VehicleRammed, marker);
		int ramKills = Count(AgentEventKind.Kill, marker, e => e.C == WeaponCatalog.Ram && e.A == OwnerId.ForPeer(_driver));
		int lost = Count(AgentEventKind.UnitLost, marker, e => e.C == OwnerId.ForPeer(_driver));
		var speeds = new List<string>();
		for (int i = marker; i < _events.Count; i++)
		{
			if (_events[i].Kind == AgentEventKind.VehicleRammed)
			{
				speeds.Add(F(_events[i].X, "0.0"));
			}
		}

		int alive = 0;
		foreach (ushort id in abreast)
		{
			alive += units.Find(id) is { IsAlive: true } ? 1 : 0;
		}

		Measured($"full throttle through them: {rammed} vehicle_rammed at {string.Join(", ", speeds)} m/s; {ramKills} kills"
			+ $" with weapon 15 in the driver's name; {lost} unit_lost naming the driver; {alive} alive; driver's kills"
			+ $" {killsBefore} → {driverCombat.Kills}; the 4x4 {F(truck.Health, "0")}/600; debug: {crew.DebugLine()}");
		Fact(rammed == 3 && ramKills == 3 && lost == 3 && alive == 0,
			"at full speed the 4x4 kills each rifleman it strikes, in its driver's name, by the ram");
		Fact(driverCombat.Kills - killsBefore == 3, $"and the driver is counted three kills ({driverCombat.Kills - killsBefore})");
		units.ClearUnits();

		// 7. At a quarter throttle into three more.
		Mark("push-out");
		foreach (bool _ in Until(() => !crew.IsSeated(_driver), 20 * Second)) yield return true;
		crew.Remove(truck.Slot);
		foreach (bool _ in Wait(Second)) yield return true;
		truck = SpawnTruck();
		foreach (bool _ in Wait(30)) yield return true;
		Place(_driver, truck, SeatRole.Driver);
		Mark("push-board");
		foreach (bool _ in Until(() => crew.IsDriving(_driver, out _), 20 * Second)) yield return true;

		var line = new List<ushort>();
		for (int i = -1; i <= 1; i++)
		{
			line.Add(units.ServerPlaceUnit(UnitCatalog.Infantry, new Vector3(Start.X + 15f, 0.5f, 50f + (i * 0.8f)),
				Team.Strategist, OrderKind.None));
		}

		var startedAt = new Dictionary<ushort, Vector3>();
		foreach (ushort id in line)
		{
			startedAt[id] = units.Find(id).GlobalPosition;
		}

		marker = _events.Count;
		Mark("push");
		float fastest = 0f;
		foreach (bool _ in Until(() => Has("driver-pushed"), 40 * Second))
		{
			fastest = Mathf.Max(fastest, truck.Speed);
			yield return true;
		}

		alive = 0;
		float movedLeast = float.MaxValue;
		foreach (ushort id in line)
		{
			if (units.Find(id) is { IsAlive: true } unit)
			{
				alive++;
				movedLeast = Mathf.Min(movedLeast, unit.GlobalPosition.DistanceTo(startedAt[id]));
			}
		}

		rammed = Count(AgentEventKind.VehicleRammed, marker);
		lost = Count(AgentEventKind.UnitLost, marker);
		Measured($"a quarter throttle into them: fastest {F(fastest, "0.00")} m/s, the 4x4 to x {F(truck.Pose.Position.X, "0.0")};"
			+ $" {rammed} vehicle_rammed, {lost} unit_lost, {alive} alive"
			+ (alive > 0 ? $", each moved at least {F(movedLeast)} m" : string.Empty));
		Fact(fastest < 8f && fastest > 3.5f, $"at walking pace, about 4.5 m/s ({F(fastest)} m/s)");
		Fact(rammed == 0 && lost == 0 && alive == 3, "it kills none of the riflemen it reaches");
		Fact(alive == 3 && movedLeast > 0.3f,
			$"and pushes each of them aside ({(alive > 0 ? F(movedLeast) : "none alive")} m at least)");
		units.ClearUnits();

		Mark("done");
		foreach (bool _ in Until(() => Has("finished-driver") && Has("finished-gunner") && Has("finished-passenger"),
			60 * Second))
		{
			yield return true;
		}
	}

	// ---- the driver ----------------------------------------------------------------------

	private IEnumerator<bool> Driver()
	{
		foreach (bool _ in Join()) yield return true;
		CrewVehicleManager crew = CrewVehicleManager.Instance;
		PlayerCombat local = CombatManager.Instance.Local;
		fps_controller me = local.Character;

		foreach (bool _ in GetIn("board", SeatRole.Driver)) yield return true;
		foreach (bool _ in Until(() => crew.DrivenSlot == 0, 5 * Second)) yield return true;
		CrewVehicle truck = crew.VehicleAt(0);

		// 3. The lane at full throttle, then a stop.
		foreach (bool _ in Until(() => Has("drive"), 60 * Second)) yield return true;
		Input.ActionPress("move_forward");
		int guard = 0;
		while (truck.Drive.Position.X < 60f && guard++ < 25 * Second)
		{
			yield return true;
		}

		Input.ActionRelease("move_forward");
		Input.ActionPress("move_backward");
		foreach (bool _ in Until(() => truck.Speed < 0.5f, 6 * Second)) yield return true;
		Input.ActionRelease("move_backward");
		Input.ActionPress("jump");
		foreach (bool _ in Wait(Second)) yield return true;
		Input.ActionRelease("jump");
		Fact(truck.Drive.Position.X > 55f, $"it drove the lane ({F(truck.Drive.Position.X, "0.0")})");
		Mark("driver-drove");

		// 5. The tank's round, from the wheel: out at 50, on this client too.
		foreach (bool _ in Until(() => Has("wrecked"), 120 * Second)) yield return true;
		foreach (bool _ in Until(() => !me.IsSeated && crew.DrivenSlot < 0, 2 * Second)) yield return true;
		Fact(!_timedOut, "this client, wrecked, stops predicting the 4x4 and is out of its seat");
		foreach (bool _ in Until(() => local.Health == 50, 2 * Second)) yield return true;
		Fact(local.Health == 50, $"and its record says 50 health ({local.Health})");
		CrewVehicle wreck = crew.VehicleAt(0);
		Fact(wreck != null && wreck.Destroyed, "it draws the 4x4 as a wreck");
		foreach (bool _ in Until(() => crew.VehicleAt(0) == null || crew.VehicleAt(0) != wreck, 6 * Second)) yield return true;
		Fact(!_timedOut, "and frees it once the server stops naming it");

		// 6. Full throttle through the riflemen.
		foreach (bool _ in GetIn("ram-board", SeatRole.Driver)) yield return true;
		foreach (bool _ in Until(() => crew.DrivenSlot == 0 && Has("ram"), 20 * Second)) yield return true;
		truck = crew.VehicleAt(0);
		Input.ActionPress("move_forward");
		float atLine = 0f;
		guard = 0;
		while (truck.Drive.Position.X < 30f && guard++ < 20 * Second)
		{
			if (Mathf.Abs(truck.Drive.Position.X - (-2.5f)) < 0.6f)
			{
				atLine = truck.Speed;
			}

			yield return true;
		}

		Input.ActionRelease("move_forward");
		Input.ActionPress("move_backward");
		foreach (bool _ in Until(() => truck.Speed < 0.5f, 6 * Second)) yield return true;
		Input.ActionRelease("move_backward");
		Measured($"at the riflemen's line the 4x4 was doing {F(atLine, "0.0")} m/s");
		Mark("driver-rammed");

		// 7. Out, into a new one, and a quarter throttle.
		foreach (bool _ in GetOut("push-out")) yield return true;
		foreach (bool _ in GetIn("push-board", SeatRole.Driver)) yield return true;
		foreach (bool _ in Until(() => crew.DrivenSlot == 0 && Has("push"), 20 * Second)) yield return true;
		truck = crew.VehicleAt(0);
		Input.ActionPress("move_forward", QuarterThrottleStrength);
		foreach (bool _ in Wait(8 * Second)) yield return true;
		Input.ActionRelease("move_forward");
		Input.ActionPress("jump");
		foreach (bool _ in Until(() => truck.Speed < 0.3f, 6 * Second)) yield return true;
		Input.ActionRelease("jump");
		Measured($"a quarter throttle for 8 s: to x {F(truck.Drive.Position.X, "0.0")}");
		Mark("driver-pushed");
		foreach (bool _ in Until(() => Has("done"), 60 * Second)) yield return true;
	}

	// ---- the gunner ----------------------------------------------------------------------

	private IEnumerator<bool> Gunner()
	{
		foreach (bool _ in Join()) yield return true;
		CrewVehicleManager crew = CrewVehicleManager.Instance;
		PlayerCombat local = CombatManager.Instance.Local;

		foreach (bool _ in GetIn("board", SeatRole.Gunner)) yield return true;
		CrewVehicle truck = crew.VehicleAt(0);

		// 2. The box, parked: down on the tick the trigger is sampled, never up while firing.
		foreach (bool _ in Until(() => Has("gun"), 30 * Second)) yield return true;
		foreach (bool _ in Wait(Second)) yield return true;
		Sampler.Align(0f, 0.15f);
		foreach (bool _ in Wait(10)) yield return true;

		short start = truck.Gun.Box.Ammo;
		uint pressedAt = NetworkManager.Instance.Clock.InputTick;
		Input.ActionPress("fire");
		int ticksToFirst = -1;
		int firstDrop = 0;
		bool rose = false;
		short last = start;
		for (int i = 0; i < Second; i++)
		{
			yield return true;
			short now = truck.Gun.Box.Ammo;
			if (ticksToFirst < 0 && now != start)
			{
				ticksToFirst = i + 1;
				firstDrop = start - now;
			}

			rose |= now > last;
			last = now;
		}

		Input.ActionRelease("fire");
		string prompt = EmplacementManager.Instance.PromptFor(local, NetworkManager.Instance.Tick);
		Measured($"the box {start} → {last} in a second of fire; it first fell by {firstDrop}, {ticksToFirst} tick(s) after"
			+ $" the press (input tick {pressedAt}); the line under the reticle: {prompt.Replace("\n", " / ")}");
		Fact(ticksToFirst == 1 && firstDrop == 1,
			$"the gun's box counts down on this client on the tick the trigger is sampled ({ticksToFirst} tick, by {firstDrop})");
		Fact(!rose && last <= start - 6, $"and keeps counting down, never back up, while it fires ({start} → {last})");
		Fact(prompt.StartsWith($"4x4 · gun · {last}/100"), $"the gunner's line says the box ({prompt.Split('\n')[0]})");
		Mark("gunner-fired");
		foreach (bool _ in Until(() => Has("server-box"), 10 * Second)) yield return true;
		Fact(Read("server-box") == last.ToString(CultureInfo.InvariantCulture),
			$"the server's box agrees ({Read("server-box")} and {last})");

		// Forward and down, at the gun's lowest: its muzzle is inside the cab's box.
		Sampler.Align(YawFacing(Vector3.Right), -0.26f);
		foreach (bool _ in Wait(10)) yield return true;
		Input.ActionPress("fire");
		foreach (bool _ in Wait(Second / 2)) yield return true;
		Input.ActionRelease("fire");
		Mark("gunner-down");

		// 3. Firing to the left on the straight at full speed.
		foreach (bool _ in FireOnTheMove(truck)) yield return true;

		foreach (bool _ in GetOut("dismount")) yield return true;
		foreach (bool _ in GetIn("reboard", SeatRole.Gunner)) yield return true;
		foreach (bool _ in Until(() => Has("wrecked"), 120 * Second)) yield return true;
		foreach (bool _ in Until(() => Has("done"), 240 * Second)) yield return true;
	}

	// ---- the passenger -------------------------------------------------------------------

	private IEnumerator<bool> Passenger()
	{
		foreach (bool _ in Join()) yield return true;
		CrewVehicleManager crew = CrewVehicleManager.Instance;
		PlayerCombat local = CombatManager.Instance.Local;

		foreach (bool _ in GetIn("board", SeatRole.Passenger)) yield return true;
		CrewVehicle truck = crew.VehicleAt(0);

		// The arc, parked: no shot over the cab, a shot to the side.
		Sampler.Align(YawFacing(Vector3.Right), 0f);
		foreach (bool _ in Wait(10)) yield return true;
		string forward = EmplacementManager.Instance.PromptFor(local, NetworkManager.Instance.Tick);
		Sampler.Align(0f, 0f);
		foreach (bool _ in Wait(10)) yield return true;
		string left = EmplacementManager.Instance.PromptFor(local, NetworkManager.Instance.Tick);
		Fact(forward.Split('\n')[0].EndsWith("no shot") && !left.Split('\n')[0].EndsWith("no shot"),
			$"over the cab the passenger has no shot, and to the left one ({forward.Split('\n')[0]}; {left.Split('\n')[0]})");

		short rounds = local.Weapons[SimConfig.LargeSlot].Ammo;
		Sampler.Align(YawFacing(Vector3.Right), 0f);
		foreach (bool _ in Wait(10)) yield return true;
		Input.ActionPress("fire");
		foreach (bool _ in Wait(Second / 2)) yield return true;
		Input.ActionRelease("fire");
		Fact(local.Weapons[SimConfig.LargeSlot].Ammo == rounds,
			$"and the trigger does nothing over the cab ({rounds} → {local.Weapons[SimConfig.LargeSlot].Ammo})");

		foreach (bool _ in FireOnTheMove(truck)) yield return true;

		foreach (bool _ in GetOut("dismount")) yield return true;
		foreach (bool _ in GetIn("reboard", SeatRole.Passenger)) yield return true;
		foreach (bool _ in Until(() => Has("done"), 240 * Second)) yield return true;
	}

	/// <summary>
	/// 3. To the left, every round fired while the 4x4 is on the straight at full speed,
	/// each adopted round's two origins logged by the server's id: this client's tracer's
	/// and the server's round's. They are within 0.3 m of each other.
	/// </summary>
	private IEnumerable<bool> FireOnTheMove(CrewVehicle truck)
	{
		var apart = new List<float>();
		bool window = false;
		bool recording = false;
		CrewVehicleManager crew = CrewVehicleManager.Instance;
		SimClock clock = NetworkManager.Instance.Clock;

		// What this client had drawn when it sampled each input tick: the frame before's.
		var drawnFor = new Dictionary<uint, float>();
		uint lastInput = clock.InputTick;
		float lastDrawn = crew.DrawnRenderTick;
		void Adopted(uint id, uint tick, Vector3 tracer, Vector3 server)
		{
			if (!recording)
			{
				return;
			}

			float d = tracer.DistanceTo(server);
			apart.Add(d);
			Vector3 velocity = truck.SimVelocity;
			float ahead = velocity.LengthSquared() > 1f
				? (server - tracer).Dot(velocity.Normalized()) / (velocity.Length() * SimConfig.TickDelta)
				: 0f;
			string drawn = drawnFor.TryGetValue(tick, out float r) ? F(r, "0.00") : "?";
			Measured($"round {id}: tracer from {V(tracer)}, the server's from {V(server)}, {F(d, "0.000")} m apart,"
				+ $" the server's {F(ahead, "0.00")} ticks ahead; input tick {tick}, drawn at {drawn}");
		}

		CombatManager.Instance.PredictionAdopted += Adopted;
		foreach (bool _ in Until(() => Has("drive"), 60 * Second)) yield return true;
		Sampler.Align(0f, 0.05f);

		int guard = 0;
		int fired = 0;
		int windowTicks = 0;
		while (!Has("driver-drove") && guard++ < 60 * Second)
		{
			for (uint t = lastInput + 1; t <= clock.InputTick; t++)
			{
				drawnFor[t] = lastDrawn;
			}

			lastInput = clock.InputTick;
			lastDrawn = crew.DrawnRenderTick;
			if (window)
			{
				Measured($"DIAG clock: server {clock.ServerTick}, input {clock.InputTick}, render {F(clock.RenderTick, "0.00")},"
					+ $" drawn {F(crew.DrawnRenderTick, "0.00")}, rtt {F(clock.RttSeconds * 1000f, "0")} ms, buffer {clock.InputBufferDepth}");
			}

			float x = truck.DrawnPose.Position.X;
			bool onStraight = x >= StraightFromX && x <= StraightToX && truck.Speed > FullSpeed;
			if (onStraight && !window)
			{
				window = true;
				recording = true;
				Input.ActionPress("fire");
			}
			else if (!onStraight && window)
			{
				window = false;
				Input.ActionRelease("fire");
			}

			if (window)
			{
				windowTicks++;
			}

			yield return true;
		}

		Input.ActionRelease("fire");
		foreach (bool _ in Wait(Second)) yield return true;
		recording = false;
		CombatManager.Instance.PredictionAdopted -= Adopted;

		apart.Sort();
		fired = apart.Count;
		float worst = fired > 0 ? apart[^1] : float.MaxValue;
		float mean = 0f;
		foreach (float d in apart)
		{
			mean += d;
		}

		mean = fired > 0 ? mean / fired : float.MaxValue;
		Measured($"{F(windowTicks / (float)Second, "0.0")} s of fire at full speed: {fired} rounds adopted, origins"
			+ $" {F(mean, "0.000")} m apart on average, {F(fired > 0 ? apart[fired / 2] : 0f, "0.000")} m the median,"
			+ $" {F(worst, "0.000")} m at worst");
		Fact(fired >= 10, $"at least ten rounds fired from the seat at full speed ({fired})");
		Fact(worst < 0.3f, $"at full speed this client's tracer and the server's round leave within 0.3 m of each other ({F(worst, "0.000")} m)");
	}
}
