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
using Godot;

namespace Gdpyr.EngineChecks;

/// <summary>
/// The crew vehicles on the wire (docs/GROUND_VEHICLES.md §12, GV3): a dedicated server
/// and four clients, each its own headless process, the clients behind a relay that
/// delays every datagram 40 ± 10 ms each way — 80 ms RTT with 20 ms of jitter, what
/// DEPLOYMENT.md §4's <c>tc netem</c> line gives. Run by <c>scripts/crew-vehicle-checks.sh
/// net</c> on a copy of the tree with this file added as an autoload; not part of the game.
///
/// <c>CREW_NET_CHECK</c> names this process's part; <c>CREW_NET_DIR</c> is a directory the
/// parts leave marker files in, which is how they keep in step:
/// <list type="bullet">
/// <item><c>server</c>: puts a 4x4 down at the west end of the test strip, facing east,
/// and its driver and passenger at their doors; later a strategist builder in sight of
/// it, then none; asks for the joiner; asks everybody out.</item>
/// <item><c>driver</c>: gets in, drives east over the strip and down the clear lane at
/// z 50 at full throttle, brakes, and later gets out; then flies the helicopter up,
/// forward and down, and gets out. Measures its own prediction.</item>
/// <item><c>passenger</c>: gets in the bed and measures how smoothly its eye moves.</item>
/// <item><c>strategist</c>: counts what it is told about the vehicle and the two in it,
/// unseen, seen, and unseen again.</item>
/// <item><c>joiner</c>: connects with the 4x4 parked and two aboard, and looks.</item>
/// </list>
///
/// One line per fact, <c>PASS</c> or <c>FAIL</c>, and <c>[checks] measured …</c> lines;
/// each process exits with the number of its facts that failed.
/// </summary>
public partial class CrewVehicleNetChecks : Node
{
	private const int Second = SimConfig.TickRate;

	/// <summary>Where the 4x4 is put: the west end of the strip, on the lane at z 50 (§6.6).</summary>
	private static readonly Vector3 Start = new(-112f, 0.5f, 50f);

	private string _role;
	private string _dir;
	private IEnumerator<bool> _run;
	private int _failures;
	private int _facts;
	private bool _timedOut;

	public override void _Ready()
	{
		_role = System.Environment.GetEnvironmentVariable("CREW_NET_CHECK");
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
			"passenger" => Passenger(),
			"strategist" => Strategist(),
			"joiner" => Joiner(),
			_ => null,
		};

		if (_run == null)
		{
			GD.PrintErr($"[checks] no such part: {_role}");
			SetPhysicsProcess(false);
			GetTree().Quit(99);
			return;
		}

		GD.Print($"[checks] net check: {_role}");
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

	private void Mark(string name, string content = "")
	{
		File.WriteAllText(Path.Combine(_dir, name), content);
		GD.Print($"[checks] {_role} marks {name}");
	}

	private bool Has(string name) => File.Exists(Path.Combine(_dir, name));

	private int PeerOf(string role) =>
		int.TryParse(File.ReadAllText(Path.Combine(_dir, $"{role}.peer")).Trim(), out int peer) ? peer : 0;

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

	/// <summary>A remote client's jitter buffer, read by reflection: what the server consumed of its frames, and how.</summary>
	private static string QueueStats(int peerId)
	{
		var players = (System.Collections.IDictionary)typeof(PlayerManager)
			.GetField("_players", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
			.GetValue(PlayerManager.Instance);
		object player = players[peerId];
		var queue = (ServerInputQueue)player.GetType().GetField("Queue").GetValue(player);
		return queue == null ? "none"
			: $"starved {queue.Starvations}, discarded {queue.Discards}, resynced {queue.Resyncs}, duplicates {queue.Duplicates}, depth {queue.Depth}";
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

	/// <summary>A client in the game, on <paramref name="team"/>, alive if on the ground; its peer id marked for the server.</summary>
	private IEnumerable<bool> JoinAs(Team team)
	{
		foreach (bool _ in Until(() => Session.InGame && CombatManager.Instance?.Local?.Character != null
			&& CrewVehicleManager.Instance != null && NetworkManager.Instance is { Connected: true }, 60 * Second))
		{
			yield return true;
		}

		Fact(!_timedOut, "connected, in the game, with a character");
		foreach (bool _ in Wait(30)) yield return true;

		PlayerCombat local = CombatManager.Instance.Local;
		CombatManager.Instance.RequestTeam(team);
		foreach (bool _ in Until(() => local.Team == team && (team == Team.Strategist || local.IsAlive), 20 * Second))
		{
			yield return true;
		}

		Fact(!_timedOut, $"on the {team}");
		Mark($"{_role}.peer", NetworkManager.Instance.LocalPeerId.ToString(CultureInfo.InvariantCulture));
	}

	// ---- the server ----------------------------------------------------------------------

	private IEnumerator<bool> Server()
	{
		foreach (bool _ in Until(() => Session.InGame && CrewVehicleManager.Instance != null, 60 * Second)) yield return true;
		foreach (bool _ in Until(() => Has("driver.peer") && Has("passenger.peer") && Has("strategist.peer"), 120 * Second))
		{
			yield return true;
		}

		Fact(!_timedOut, "the driver, the passenger and the strategist are in");
		if (_timedOut)
		{
			yield break;
		}

		int driver = PeerOf("driver");
		int passenger = PeerOf("passenger");
		CrewVehicleManager crew = CrewVehicleManager.Instance;
		CombatManager combat = CombatManager.Instance;

		CrewVehicleDefinition definition = CrewVehicleCatalog.Definition(CrewVehicleCatalog.FourByFour);
		float east = YawFacing(Vector3.Right);
		int slot = crew.Spawn(CrewVehicleCatalog.FourByFour,
			Start + (Vector3.Up * definition.ToFourByFourTraits().RideHeightMeters), east);
		Fact(slot == 0, $"a 4x4 in slot 0 at the west end of the strip ({slot})");
		if (slot != 0)
		{
			yield break;
		}

		foreach (bool _ in Wait(30)) yield return true;
		CrewVehicle truck = crew.VehicleAt(0);

		// Each beside the door of the seat it is to take, facing the hull.
		foreach ((int peer, SeatRole role) in new[] { (driver, SeatRole.Driver), (passenger, SeatRole.Passenger) })
		{
			int seat = SeatOfRole(truck, role);
			Vector3 door = truck.Pose.ToWorld(truck.Seats[seat].Door);
			combat.Find(peer).Character.Teleport(new Transform3D(
				new Basis(Vector3.Up, YawFacing(truck.Pose.Position - door)), door + (Vector3.Up * 0.1f)));
		}

		Mark("placed");
		foreach (bool _ in Until(() => crew.IsDriving(driver, out _) && crew.TrySeat(passenger, out _, out int s)
			&& truck.Seats[s].Role == SeatRole.Passenger, 20 * Second))
		{
			yield return true;
		}

		Fact(!_timedOut, "over the link, the driver is at the wheel and the passenger in the bed");
		Mark("seated");

		float startX = truck.Pose.Position.X;
		foreach (bool _ in Until(() => Has("drove"), 90 * Second)) yield return true;
		Fact(!_timedOut, "the driver drove");
		Measured($"the driver's input queue: {QueueStats(driver)}");
		Measured($"the 4x4 drove from x {F(startX, "0.0")} to x {F(truck.Pose.Position.X, "0.0")}");

		// The fog: a strategist's builder 30 m from the parked truck, in sight of it and
		// outside the 20 m it engages at; then none.
		VisibilityService fog = combat.Visibility;
		Fact(!fog.IsVehicleVisible(0) && !fog.IsVisible(driver) && !fog.IsVisible(passenger),
			"with no strategist unit on the field, neither the 4x4 nor the two in it are visible");
		Vector3 at = truck.Pose.Position + new Vector3(0f, 0f, -30f);
		at.Y = 0.5f;
		ushort unit = UnitManager.Instance.ServerPlaceUnit(UnitCatalog.Builder, at, Team.Strategist, OrderKind.None);
		Fact(unit != 0, "a strategist builder 30 m from the truck");
		Mark("unit");
		foreach (bool _ in Wait(4 * Second)) yield return true;
		Fact(fog.IsVehicleVisible(0) && fog.IsVisible(driver) && fog.IsVisible(passenger),
			"in the builder's sight, the 4x4 and both in it are visible");

		UnitManager.Instance.ClearUnits();
		foreach (bool _ in Wait(SimConfig.FogRefreshIntervalTicks * 2)) yield return true;
		Fact(!fog.IsVehicleVisible(0) && !fog.IsVisible(driver) && !fog.IsVisible(passenger),
			"with the builder gone, neither is");
		Mark("unit-gone");

		foreach (bool _ in Until(() => Has("finished-strategist"), 30 * Second)) yield return true;
		Mark("join");
		foreach (bool _ in Until(() => Has("finished-joiner"), 120 * Second)) yield return true;
		Fact(!_timedOut, "the joiner came and looked");

		Mark("exit");
		foreach (bool _ in Until(() => Has("out-driver") && Has("out-passenger"), 30 * Second)) yield return true;
		Fact(!crew.IsSeated(driver) && !crew.IsSeated(passenger) && truck.Aboard == 0, "both got out");

		// The helicopter, over open floor east of the bunker, its pilot at the door.
		CrewVehicleDefinition heliDefinition = CrewVehicleCatalog.Definition(CrewVehicleCatalog.Helicopter);
		int heliSlot = crew.Spawn(CrewVehicleCatalog.Helicopter, new Vector3(90f, 0.5f, 80f), YawFacing(Vector3.Forward));
		Fact(heliSlot == 1 && heliDefinition != null, $"a helicopter in slot 1 ({heliSlot})");
		if (heliSlot != 1)
		{
			yield break;
		}

		foreach (bool _ in Wait(30)) yield return true;
		CrewVehicle heli = crew.VehicleAt(1);
		Vector3 pilotDoor = heli.Pose.ToWorld(heli.Seats[SeatOfRole(heli, SeatRole.Driver)].Door);
		combat.Find(driver).Character.Teleport(new Transform3D(
			new Basis(Vector3.Up, YawFacing(heli.Pose.Position - pilotDoor)), pilotDoor + (Vector3.Up * 0.1f)));
		Mark("heli-placed");
		foreach (bool _ in Until(() => Has("heli-done"), 90 * Second)) yield return true;
		Fact(!_timedOut && !crew.IsSeated(driver) && heli.Grounded, "the pilot flew it, landed it and got out");
		Measured($"the driver's input queue: {QueueStats(driver)}");
		foreach (bool _ in Until(() => Has("finished-driver") && Has("finished-passenger"), 30 * Second)) yield return true;
	}

	// ---- the driver ----------------------------------------------------------------------

	private IEnumerator<bool> Driver()
	{
		foreach (bool _ in JoinAs(Team.GroundForce)) yield return true;
		foreach (bool _ in Until(() => Has("placed"), 120 * Second)) yield return true;
		CrewVehicleManager crew = CrewVehicleManager.Instance;
		PlayerCombat local = CombatManager.Instance.Local;
		fps_controller me = local.Character;

		foreach (bool _ in Until(() => crew.VehicleAt(0) != null, 10 * Second)) yield return true;
		Fact(!_timedOut && crew.VehicleAt(0).IsPuppet && crew.VehicleAt(0).Kind == CrewVehicleKind.FourByFour,
			"the 4x4 is drawn here, a puppet, of the kind its record says");
		foreach (bool _ in Wait(Second)) yield return true;
		string prompt = EmplacementManager.Instance.PromptFor(local, NetworkManager.Instance.Tick);
		Fact(prompt == "[E] drive 4x4", $"at the driver's door the prompt is `[E] drive 4x4` ({prompt})");

		foreach (bool _ in Tap("use")) yield return true;
		foreach (bool _ in Until(() => crew.DrivenSlot == 0, 5 * Second)) yield return true;
		Fact(!_timedOut, "a tap of E, over the link, puts this client at the wheel, predicting the 4x4");
		CrewVehicle truck = crew.VehicleAt(0);
		Fact(truck != null && !truck.IsPuppet && me.IsSeated, "the driven 4x4 is not a puppet, and the body is seated");
		Measured($"seated after the tap: snaps {crew.Snaps}, replays {crew.Replays}");

		foreach (bool _ in Until(() => Has("seated"), 20 * Second)) yield return true;
		foreach (bool _ in Wait(Second)) yield return true;
		int snapsBefore = crew.Snaps;
		int shownBefore = crew.CorrectionsShownAtOnce;
		Fact(snapsBefore == 1, $"one snap, the first record after getting in ({snapsBefore})");

		// East, over the strip and down the lane at full throttle.
		Input.ActionPress("move_forward");
		int replaysAtStart = crew.Replays;
		int stripReplays = 0;
		float stripLargest = 0f;
		int steadyTicks = 0;
		int steadyReplays = 0;
		int steadyComparisons = 0;
		float steadyErrorSum = 0f;
		float steadyErrorMax = 0f;
		int lastComparisons = crew.Comparisons;
		int lastReplays = crew.Replays;
		float fastest = 0f;
		int guard = 0;

		while (truck.Drive.Position.X < 85f && guard++ < 25 * Second)
		{
			yield return true;
			if (crew.VehicleAt(0) != truck)
			{
				break;
			}

			float x = truck.Drive.Position.X;
			float speed = truck.Speed;
			fastest = Mathf.Max(fastest, speed);
			int newReplays = crew.Replays - lastReplays;
			bool newComparison = crew.Comparisons != lastComparisons;
			lastReplays = crew.Replays;
			lastComparisons = crew.Comparisons;

			if (x < -60f)
			{
				stripReplays += newReplays;
				stripLargest = Mathf.Max(stripLargest, crew.LargestCorrectionMeters);
			}

			// The steady straight: past the strip, settled, at full speed.
			if (x >= -20f && x <= 80f && speed > 16.5f)
			{
				steadyTicks++;
				steadyReplays += newReplays;
				if (newComparison)
				{
					steadyComparisons++;
					steadyErrorSum += crew.LastComparisonErrorMeters;
					steadyErrorMax = Mathf.Max(steadyErrorMax, crew.LastComparisonErrorMeters);
				}
			}
		}

		Input.ActionRelease("move_forward");
		Input.ActionPress("move_backward");
		foreach (bool _ in Until(() => truck.Speed < 0.5f, 6 * Second)) yield return true;
		Input.ActionRelease("move_backward");
		Input.ActionPress("jump");
		foreach (bool _ in Wait(Second)) yield return true;
		Input.ActionRelease("jump");
		foreach (bool _ in Wait(Second)) yield return true;

		float steadySeconds = steadyTicks / (float)Second;
		float mispredictedPerSecond = steadySeconds > 0f ? steadyReplays / steadySeconds : float.MaxValue;
		float meanError = steadyComparisons > 0 ? steadyErrorSum / steadyComparisons : float.MaxValue;
		Measured($"top speed {F(fastest, "0.0")} m/s; the strip: {stripReplays} replays, largest correction {F(stripLargest * 100f, "0.0")} cm");
		Measured($"steady straight: {F(steadySeconds, "0.0")} s, {steadyComparisons} comparisons, mean error {F(meanError * 100f, "0.00")} cm,"
			+ $" worst {F(steadyErrorMax * 100f, "0.00")} cm, {steadyReplays} replays ({F(mispredictedPerSecond, "0.00")}/s)");
		Measured($"the whole drive: {crew.Replays - replaysAtStart} replays, longest {crew.LongestReplayTicks} ticks,"
			+ $" snaps {crew.Snaps}, largest correction {F(crew.LargestCorrectionMeters * 100f, "0.0")} cm,"
			+ $" shown at once {crew.CorrectionsShownAtOnce}; mispredicted/s on the HUD {F(crew.PredictionStats.MispredictionsPerSecond, "0.0")}");
		Measured($"the debug row: {crew.DebugLine()}");
		Measured($"this client's clock: {NetworkManager.Instance.Clock.Resyncs} resyncs, {NetworkManager.Instance.Clock.Nudges} nudges");

		Fact(steadySeconds >= 3f, $"at least 3 s of steady straight at full speed ({F(steadySeconds, "0.0")} s)");
		Fact(meanError < 0.05f, $"driving straight, the steady prediction error is under 5 cm ({F(meanError * 100f, "0.00")} cm)");
		Fact(mispredictedPerSecond < 1f, $"driving straight, fewer than one misprediction a second ({F(mispredictedPerSecond, "0.00")}/s)");
		Fact(crew.LongestReplayTicks <= 12, $"no replay longer than twelve ticks ({crew.LongestReplayTicks})");
		Fact(crew.Snaps == snapsBefore && crew.CorrectionsShownAtOnce == shownBefore,
			$"nothing snaps over the strip or after it (snaps {crew.Snaps}, shown at once {crew.CorrectionsShownAtOnce - shownBefore})");
		Fact(truck.Drive.Position.X > 60f, $"it drove the lane ({F(truck.Drive.Position.X, "0.0")})");
		Mark("drove");

		// Out, when the server asks.
		foreach (bool _ in Until(() => Has("exit"), 240 * Second)) yield return true;
		foreach (bool _ in Tap("use")) yield return true;
		foreach (bool _ in Until(() => !me.IsSeated && crew.DrivenSlot < 0, 5 * Second)) yield return true;
		Fact(!_timedOut, "a tap of E gets the driver out, and this client stops predicting the 4x4");
		foreach (bool _ in Wait(Second)) yield return true;
		float outside = truck.HullDistance(me.SimPosition + Vector3.Up);
		Fact(truck.IsPuppet && !me.IsSeated && outside > 0.4f && me.IsOnFloor(),
			$"out, standing on the floor clear of the hull ({F(outside)} m), the 4x4 a puppet again");
		Mark("out-driver");

		// The helicopter: in, the rotor up, climb, forward, down, out.
		foreach (bool _ in Until(() => Has("heli-placed") && crew.VehicleAt(1) != null, 60 * Second)) yield return true;
		foreach (bool _ in Wait(Second)) yield return true;
		prompt = EmplacementManager.Instance.PromptFor(local, NetworkManager.Instance.Tick);
		Fact(prompt == "[E] fly helicopter", $"at the pilot's door the prompt is `[E] fly helicopter` ({prompt})");
		foreach (bool _ in Tap("use")) yield return true;
		foreach (bool _ in Until(() => crew.DrivenSlot == 1, 5 * Second)) yield return true;
		Fact(!_timedOut, "a tap of E puts this client at the helicopter's controls, predicting it");
		CrewVehicle heli = crew.VehicleAt(1);
		foreach (bool _ in Wait(3 * Second)) yield return true;
		int heliSnaps = crew.Snaps;

		Input.ActionPress("jump");
		foreach (bool _ in Wait(3 * Second)) yield return true;
		Input.ActionRelease("jump");
		float up = heli.HeightAboveGround;
		prompt = EmplacementManager.Instance.PromptFor(local, NetworkManager.Instance.Tick);
		Input.ActionPress("move_forward");
		foreach (bool _ in Wait(3 * Second)) yield return true;
		Input.ActionRelease("move_forward");
		float flown = heli.Flight.Position.Z;
		Input.ActionPress("crouch");
		foreach (bool _ in Until(() => heli.Grounded, 20 * Second)) yield return true;
		Input.ActionRelease("crouch");
		bool landed = !_timedOut;
		foreach (bool _ in Wait(Second)) yield return true;

		Measured($"helicopter: {F(up, "0.0")} m up after 3 s of Space, to z {F(flown, "0.0")} after 3 s of W;"
			+ $" {crew.Replays} replays, longest {crew.LongestReplayTicks} ticks, snaps {crew.Snaps},"
			+ $" largest correction {F(crew.LargestCorrectionMeters * 100f, "0.0")} cm, shown at once {crew.CorrectionsShownAtOnce}");
		Fact(up > 6f && landed, $"predicted over the link, it climbs on Space and lands on Ctrl ({F(up, "0.0")} m)");
		Fact(prompt.EndsWith("too high to get out"),
			$"above 4 m this client says it is too high to get out ({prompt.Replace("\n", " / ")})");
		Fact(crew.Snaps == heliSnaps && crew.CorrectionsShownAtOnce == 0 && crew.LongestReplayTicks <= 12,
			$"nothing snaps in the air, and no replay is longer than twelve ticks ({crew.LongestReplayTicks})");
		foreach (bool _ in Tap("use")) yield return true;
		foreach (bool _ in Until(() => !me.IsSeated && crew.DrivenSlot < 0, 5 * Second)) yield return true;
		Fact(!_timedOut, "on the ground, a tap gets the pilot out");
		Mark("heli-done");
	}

	// ---- the passenger -------------------------------------------------------------------

	private IEnumerator<bool> Passenger()
	{
		foreach (bool _ in JoinAs(Team.GroundForce)) yield return true;
		foreach (bool _ in Until(() => Has("placed"), 120 * Second)) yield return true;
		CrewVehicleManager crew = CrewVehicleManager.Instance;
		PlayerCombat local = CombatManager.Instance.Local;
		fps_controller me = local.Character;

		foreach (bool _ in Until(() => crew.VehicleAt(0) != null, 10 * Second)) yield return true;
		foreach (bool _ in Wait(Second)) yield return true;
		foreach (bool _ in Tap("use")) yield return true;
		foreach (bool _ in Until(() => crew.TrySeat(local.PeerId, out CrewVehicle v, out int s) && v.Seats[s].Role == SeatRole.Passenger,
			5 * Second))
		{
			yield return true;
		}

		Fact(!_timedOut && me.IsSeated, "a tap of E, over the link, seats this client in the bed");
		CrewVehicle truck = crew.VehicleAt(0);
		Fact(truck.IsPuppet && crew.DrivenSlot < 0, "a passenger does not predict the 4x4: it is a puppet here");

		foreach (bool _ in Until(() => Has("seated"), 20 * Second)) yield return true;
		int driver = PeerOf("driver");

		// The marker is on disk the tick the server seats the driver; the record saying so is a
		// one-way trip away.
		foreach (bool _ in Until(() => crew.IsDriving(driver, out _), 2 * Second)) yield return true;
		Fact(!_timedOut, "this client has the driver at the wheel");

		// The eye, every tick, while the truck is on the steady straight.
		var steps = new List<Vector3>();
		var seatOffsets = new List<float>();
		Vector3 previous = me.EyePosition;
		float previousRender = NetworkManager.Instance.Clock.RenderTick;
		Vector3 lastStep = Vector3.Zero;
		int guard = 0;
		while (!Has("drove") && guard++ < 60 * Second)
		{
			yield return true;
			Vector3 eye = me.EyePosition;
			float render = NetworkManager.Instance.Clock.RenderTick;
			float x = truck.Drive.Position.X;
			if (x >= -20f && x <= 80f && truck.Speed > 16.5f)
			{
				if (steps.Count > 0 && ((eye - previous) - lastStep).Length() > 0.05f)
				{
					Measured($"DIAG step {F((eye - previous).Length(), "0.000")} after {F(lastStep.Length(), "0.000")}:"
						+ $" render clock {F(render, "0.0")} (+{F(render - previousRender, "0.0")}),"
						+ $" newest record {crew.NewestRecordTick(0)}, {F(render - crew.NewestRecordTick(0), "0.0")} past it");
				}

				lastStep = eye - previous;
				steps.Add(eye - previous);
				int seat = SeatOfRole(truck, SeatRole.Passenger);
				seatOffsets.Add(eye.DistanceTo(truck.DrawnPose.ToWorld(truck.Seats[seat].Eye)));
			}

			previous = eye;
			previousRender = render;
		}

		var changes = new List<float>();
		float meanStep = 0f;
		for (int i = 0; i < steps.Count; i++)
		{
			meanStep += steps[i].Length();
			if (i > 0)
			{
				changes.Add((steps[i] - steps[i - 1]).Length());
			}
		}

		meanStep = steps.Count > 0 ? meanStep / steps.Count : 0f;
		changes.Sort();
		float worst = changes.Count > 0 ? changes[^1] : float.MaxValue;
		float p99 = changes.Count > 0 ? changes[(int)(changes.Count * 0.99f)] : float.MaxValue;
		int over = changes.FindAll(c => c > 0.05f).Count;
		float seatWorst = 0f;
		foreach (float d in seatOffsets)
		{
			seatWorst = Mathf.Max(seatWorst, d);
		}

		Measured($"the eye on the steady straight: {steps.Count} ticks, {F(meanStep, "0.000")} m a tick;"
			+ $" the step changes by {F(p99 * 100f, "0.0")} cm at the 99th percentile, {F(worst * 100f, "0.0")} cm at worst;"
			+ $" {over} ticks over 5 cm; the eye off the drawn seat by {F(seatWorst, "0.0000")} m at most");
		Fact(steps.Count >= 3 * Second, $"at least 3 s of the steady straight seen from the bed ({steps.Count} ticks)");
		Fact(seatWorst < 0.001f, "the passenger's eye is at the seat's eye on the 4x4 as drawn, every tick");
		Fact(worst < 0.05f, $"the passenger's view is smooth: no tick's step differs from the last by 5 cm or more ({F(worst * 100f, "0.0")} cm)");

		foreach (bool _ in Until(() => Has("exit"), 240 * Second)) yield return true;
		foreach (bool _ in Tap("use")) yield return true;
		foreach (bool _ in Until(() => !me.IsSeated, 5 * Second)) yield return true;
		Fact(!_timedOut, "a tap of E gets the passenger out");
		Mark("out-passenger");
	}

	// ---- the strategist ------------------------------------------------------------------

	private IEnumerator<bool> Strategist()
	{
		foreach (bool _ in JoinAs(Team.Strategist)) yield return true;
		foreach (bool _ in Until(() => Has("seated"), 180 * Second)) yield return true;
		CrewVehicleManager crew = CrewVehicleManager.Instance;
		PlayerManager players = PlayerManager.Instance;
		int driver = PeerOf("driver");
		int passenger = PeerOf("passenger");

		bool Told(int peer) => !Fog.IsLost(players.SnapshotAgeTicks(peer));
		bool VehicleTold() => crew.VehicleAt(0) != null && !Fog.IsLost(crew.RecordAgeTicks(0));

		// Unseen: told nothing about the vehicle or the two in it, the whole drive.
		int toldVehicle = 0;
		int toldOccupants = 0;
		int ticks = 0;
		while (!Has("unit") && ticks++ < 120 * Second)
		{
			yield return true;
			toldVehicle += VehicleTold() ? 1 : 0;
			toldOccupants += Told(driver) || Told(passenger) ? 1 : 0;
		}

		Measured($"unseen, {ticks} ticks: told about the 4x4 on {toldVehicle}, about the two in it on {toldOccupants}");
		Fact(toldVehicle == 0 && toldOccupants == 0, "unseen, a strategist is told nothing about the 4x4 or who is in it");

		// Seen: told about the vehicle and both in it, together.
		foreach (bool _ in Wait(Second)) yield return true;
		int both = 0;
		int vehicleOnly = 0;
		int occupantsOnly = 0;
		int window = 2 * Second;
		for (int i = 0; i < window; i++)
		{
			yield return true;
			bool vehicle = VehicleTold();
			bool occupants = Told(driver) && Told(passenger);
			both += vehicle && occupants ? 1 : 0;
			vehicleOnly += vehicle && !occupants ? 1 : 0;
			occupantsOnly += occupants && !vehicle ? 1 : 0;
		}

		Measured($"seen, {window} ticks: both told on {both}, the 4x4 alone on {vehicleOnly}, its occupants alone on {occupantsOnly}");
		Fact(both >= window - 6, $"in the builder's sight, a strategist is told about the 4x4 and both in it ({both} of {window} ticks)");
		Fact(crew.TrySeat(driver, out CrewVehicle seen, out int seat) && seen.Seats[seat].Role == SeatRole.Driver
			&& crew.TrySeat(passenger, out _, out _), "and its client has them in their seats");
		Fact(crew.VehicleAt(0) is { } drawn && drawn.GetNode<Node3D>("Presentation").Visible, "and draws the 4x4");

		// Unseen again: the hull hidden, and nothing more arrives about either.
		foreach (bool _ in Until(() => Has("unit-gone"), 30 * Second)) yield return true;
		foreach (bool _ in Wait(Second)) yield return true;
		toldVehicle = 0;
		toldOccupants = 0;
		for (int i = 0; i < 2 * Second; i++)
		{
			yield return true;
			toldVehicle += VehicleTold() ? 1 : 0;
			toldOccupants += Told(driver) || Told(passenger) ? 1 : 0;
		}

		Measured($"unseen again, {2 * Second} ticks: told about the 4x4 on {toldVehicle}, about the two in it on {toldOccupants};"
			+ $" the 4x4's record {F(crew.RecordAgeTicks(0), "0")} ticks old");
		Fact(toldVehicle == 0 && toldOccupants == 0, "out of sight again, a strategist is told nothing more about either");
		Fact(crew.VehicleAt(0) is { } hidden && !hidden.GetNode<Node3D>("Presentation").Visible,
			"and its client hides the hull it last saw");
		Fact(!crew.IsSeated(driver) && !crew.IsSeated(passenger), "and seats nobody in a vehicle it has lost");
	}

	// ---- the joiner ----------------------------------------------------------------------

	private IEnumerator<bool> Joiner()
	{
		foreach (bool _ in JoinAs(Team.GroundForce)) yield return true;
		CrewVehicleManager crew = CrewVehicleManager.Instance;
		int driver = PeerOf("driver");
		int passenger = PeerOf("passenger");
		foreach (bool _ in Until(() => crew.VehicleAt(0) != null && crew.IsSeated(driver) && crew.IsSeated(passenger),
			5 * Second))
		{
			yield return true;
		}

		CrewVehicle truck = crew.VehicleAt(0);
		Fact(!_timedOut && truck != null && truck.Kind == CrewVehicleKind.FourByFour,
			"a client that joins sees the 4x4");
		if (truck == null)
		{
			yield break;
		}

		foreach (bool _ in Wait(Second)) yield return true;
		Fact(crew.IsDriving(driver, out _), "with its driver at the wheel");
		Fact(crew.TrySeat(passenger, out _, out int seat) && truck.Seats[seat].Role == SeatRole.Passenger,
			"and its passenger in the bed");

		fps_controller body = CombatManager.Instance.Find(driver)?.Character;
		int driverSeat = SeatOfRole(truck, SeatRole.Driver);
		float off = body == null ? float.MaxValue
			: body.EyePosition.DistanceTo(truck.DrawnPose.ToWorld(truck.Seats[driverSeat].Eye));
		Fact(body != null && body.IsSeated && off < 0.001f,
			$"the driver's body sits at the wheel of the 4x4 as drawn here ({F(off, "0.0000")} m)");
		Measured($"the joiner's 4x4 at ({F(truck.Pose.Position.X, "0.0")}, {F(truck.Pose.Position.Z, "0.0")}); {crew.DebugLine()}");
	}
}
