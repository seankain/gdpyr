using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using Gdpyr.Core;
using Gdpyr.Fps;
using Gdpyr.Match;
using Gdpyr.Net;
using Gdpyr.Sim;
using Gdpyr.Sim.Demo;
using Gdpyr.Ui;
using Godot;

namespace Gdpyr.EngineChecks;

/// <summary>
/// The crew vehicles in a demo (docs/GROUND_VEHICLES.md §7.4, §12 GV7), run headless by
/// <c>scripts/crew-vehicle-checks.sh demo</c> on a copy of the tree with this file added as an
/// autoload; not part of the game.
///
/// <c>CREW_DEMO_CHECK</c> names the part, and <c>CREW_NET_DIR</c> the directory the two share:
/// <list type="bullet">
/// <item><c>listen</c>: a listen host on <c>Test.tscn</c> at <c>--fixed-fps 60</c>. Its own
/// player is seated at the wheel of a 4x4 the console put down, the console's <c>record</c>
/// starts a demo, the player drives it through the input map, stops on the handbrake, gets
/// out and the console's <c>stoprecord</c> closes the file, whose summary counts a crew vehicle
/// record on every snapshot tick. Where the 4x4 was and whether the player sat in it, tick by
/// tick, goes to <c>recorded</c>.</item>
/// <item><c>playback</c>: the same build started with <c>--playdemo</c>, which a live round
/// refuses from the console: the 4x4 is a puppet in its slot, drawn on the path the authority
/// recorded; the demo's body for the player is seated at the wheel while the recording had
/// them there — its eye at the seat's eye on the hull as drawn — and is put down when the
/// recording put them down; and the puppet is still there at the end.</item>
/// </list>
///
/// One line per fact, <c>PASS</c> or <c>FAIL</c>, and <c>[checks] measured …</c> lines; the
/// process exits with the number of its facts that failed.
/// </summary>
public partial class CrewDemoChecks : Node
{
	private const int Second = SimConfig.TickRate;

	private const string DemoName = "gv7-crew";

	/// <summary>The lane GV4's checks drive: open floor at z 50, east of the test strip, facing east.</summary>
	private static readonly Vector3 Start = new(-64f, 0.6f, 50f);

	private string _role;
	private string _dir;
	private IEnumerator<bool> _run;
	private int _failures;
	private int _facts;
	private bool _timedOut;

	public override void _Ready()
	{
		_role = System.Environment.GetEnvironmentVariable("CREW_DEMO_CHECK");
		_dir = System.Environment.GetEnvironmentVariable("CREW_NET_DIR");
		if (string.IsNullOrEmpty(_role) || string.IsNullOrEmpty(_dir))
		{
			SetPhysicsProcess(false);
			return;
		}

		ProcessMode = ProcessModeEnum.Always;
		_run = _role switch
		{
			"listen" => Listen(),
			"playback" => Play(),
			_ => null,
		};
		if (_run == null)
		{
			GD.PrintErr($"[checks] no such part: {_role}");
			SetPhysicsProcess(false);
			GetTree().Quit(99);
			return;
		}

		GD.Print($"[checks] demo check: {_role}");
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

	private static string F(float v, string format = "0.000") => v.ToString(format, CultureInfo.InvariantCulture);

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

	private static LocalInputSampler Sampler => PlayerManager.Instance.GetNode<LocalInputSampler>("LocalInputSampler");

	private static uint Tick => NetworkManager.Instance.Tick;

	private static void Console(string line)
	{
		MethodInfo submit = typeof(GameConsole).GetMethod("Submit", BindingFlags.NonPublic | BindingFlags.Instance);
		submit.Invoke(GameConsole.Instance, new object[] { line });
	}

	private static DemoPlayback Playback =>
		typeof(PlayerManager).GetField("_playback", BindingFlags.NonPublic | BindingFlags.Instance)
			?.GetValue(PlayerManager.Instance) as DemoPlayback;

	/// <summary>The recorded path between two ticks, as an interpolator draws it.</summary>
	private static bool PathAt(SortedDictionary<uint, Vector3> path, float tick, out Vector3 at)
	{
		at = default;
		uint below = (uint)Mathf.FloorToInt(tick);
		if (!path.TryGetValue(below, out Vector3 a) || !path.TryGetValue(below + 1, out Vector3 b))
		{
			return false;
		}

		at = a.Lerp(b, tick - below);
		return true;
	}

	// ---- the listen host -------------------------------------------------------------------

	private IEnumerator<bool> Listen()
	{
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
		int peer = local.PeerId;

		// ---- a 4x4, and its driver ----
		float east = Mathf.Atan2(-1f, 0f);
		me.Teleport(new Transform3D(new Basis(Vector3.Up, east), Start));
		Sampler.Align(east, 0f);
		foreach (bool _ in Wait(10)) yield return true;
		Console("vehicle 4x4");
		CrewVehicle truck = crew.VehicleAt(0);
		Fact(truck != null, "the console puts a 4x4 in slot 0");
		if (truck == null)
		{
			yield break;
		}

		foreach (bool _ in Wait(30)) yield return true;
		string seated = crew.ServerSeatPlaced(peer, 0, SeatRole.Driver);
		Fact(seated == null && crew.IsDriving(peer, out _), $"the player is put at its wheel ({seated ?? "seated"})");

		// ---- recorded: a drive and a stop, and out ----
		Console($"record {DemoName}");
		Fact(PlayerManager.Instance.Recorder != null, "the console's record starts a demo");
		var path = new SortedDictionary<uint, Vector3>();
		var seatedAt = new SortedDictionary<uint, bool>();
		uint first = Tick;

		Input.ActionPress("move_forward");
		for (int i = 0; i < 2 * Second; i++)
		{
			path[Tick] = truck.Pose.Position;
			seatedAt[Tick] = crew.IsSeated(peer);
			yield return true;
		}

		Input.ActionRelease("move_forward");
		Input.ActionPress("jump");
		for (int i = 0; i < Second; i++)
		{
			path[Tick] = truck.Pose.Position;
			seatedAt[Tick] = crew.IsSeated(peer);
			yield return true;
		}

		Input.ActionRelease("jump");
		bool out_ = crew.Exit(peer, Tick);
		uint outAt = Tick;
		Fact(out_ && !crew.IsSeated(peer), "the player gets out of the stopped 4x4");
		for (int i = 0; i < Second; i++)
		{
			path[Tick] = truck.Pose.Position;
			seatedAt[Tick] = crew.IsSeated(peer);
			yield return true;
		}

		uint last = Tick;
		Console("stoprecord");
		Fact(PlayerManager.Instance.Recorder == null, "the console's stoprecord closes it");
		Vector3 recordedStart = path[first];
		Vector3 recordedEnd = path[last - 1];
		Measured($"recorded ticks {first}–{last}: the 4x4 from ({F(recordedStart.X, "0.00")}, {F(recordedStart.Z, "0.00")})"
			+ $" to ({F(recordedEnd.X, "0.00")}, {F(recordedEnd.Z, "0.00")}), {F(recordedStart.DistanceTo(recordedEnd), "0.00")} m;"
			+ $" the player out at tick {outAt}");

		// ---- the file ----
		string file = DemoFiles.Resolve(DemoName, out _, out string error);
		DemoSummary summary = default;
		bool read = false;
		using (Stream stream = file == null ? null : DemoFiles.TryOpen(file, out error))
		{
			read = stream != null && DemoSummary.TryRead(stream, out summary, out error);
		}

		int snapshots = (int)(last - first) / SimConfig.SnapshotIntervalTicks;
		Fact(read && summary.Header.Version == DemoFormat.Version && summary.CrewVehicles >= snapshots - 1
				&& summary.CrewVehicles == summary.PlayerSnapshots,
			$"the file is version {DemoFormat.Version}, a crew vehicle record on every snapshot tick"
			+ $" ({(read ? $"version {summary.Header.Version}, {summary.CrewVehicles} vehicle, {summary.PlayerSnapshots} player" : error)}"
			+ $" over {last - first} ticks)");

		// ---- for the playback ----
		var lines = new List<string> { $"peer {peer}" };
		foreach (KeyValuePair<uint, Vector3> entry in path)
		{
			lines.Add(string.Join(' ', entry.Key.ToString(CultureInfo.InvariantCulture), F(entry.Value.X, "0.000000"),
				F(entry.Value.Y, "0.000000"), F(entry.Value.Z, "0.000000"), seatedAt[entry.Key] ? "1" : "0"));
		}

		File.WriteAllLines(Path.Combine(_dir, "recorded"), lines);
		Measured($"wrote {lines.Count} recorded ticks for the playback");
	}

	// ---- the playback ----------------------------------------------------------------------

	private IEnumerator<bool> Play()
	{
		var path = new SortedDictionary<uint, Vector3>();
		var seatedAt = new SortedDictionary<uint, bool>();
		int peer = 0;
		foreach (string line in File.ReadAllLines(Path.Combine(_dir, "recorded")))
		{
			string[] parts = line.Split(' ');
			if (parts[0] == "peer")
			{
				peer = int.Parse(parts[1], CultureInfo.InvariantCulture);
				continue;
			}

			uint at = uint.Parse(parts[0], CultureInfo.InvariantCulture);
			path[at] = new Vector3(float.Parse(parts[1], CultureInfo.InvariantCulture),
				float.Parse(parts[2], CultureInfo.InvariantCulture), float.Parse(parts[3], CultureInfo.InvariantCulture));
			seatedAt[at] = parts[4] == "1";
		}

		Fact(path.Count > 0, $"the listen host's recording is read ({path.Count} ticks)");
		foreach (bool _ in Until(() => Playback != null && CrewVehicleManager.Instance != null, 10 * Second)) yield return true;
		Fact(Playback != null, $"--playdemo plays {DemoName}");
		if (Playback == null)
		{
			yield break;
		}

		CrewVehicleManager crew = CrewVehicleManager.Instance;
		float worst = 0f;
		int compared = 0;
		int seatedRight = 0;
		int seatedWrong = 0;
		int eyeCompared = 0;
		float worstEye = 0f;
		bool sawPuppet = false;
		bool puppetAtEnd = false;
		uint lastPlayed = 0;
		while (Playback is { } playback && !playback.Ended)
		{
			yield return true;
			if (Playback == null)
			{
				break;
			}

			CrewVehicle drawn = crew.VehicleAt(0);
			float render = crew.DrawnRenderTick;
			lastPlayed = playback.Tick;
			if (drawn == null)
			{
				continue;
			}

			sawPuppet |= drawn.IsPuppet;
			if (PathAt(path, render, out Vector3 expected))
			{
				worst = Mathf.Max(worst, drawn.Pose.Position.DistanceTo(expected));
				compared++;
			}

			// Who sits where at the read head's tick, as the records say; the recording's at the
			// tick of the newest record that says it.
			uint about = crew.NewestRecordTick(0);
			if (seatedAt.TryGetValue(about, out bool was))
			{
				bool now = crew.IsSeated(peer);
				if (now == was)
				{
					seatedRight++;
				}
				else
				{
					seatedWrong++;
				}

				if (now && PlayerManager.Instance.CharacterOf(peer) is { } body && crew.TrySeat(peer, out CrewVehicle into, out int seat))
				{
					float eye = body.EyePosition.DistanceTo(into.DrawnPose.ToWorld(into.Seats[seat].Eye));
					worstEye = Mathf.Max(worstEye, eye);
					eyeCompared++;
				}
			}
		}

		puppetAtEnd = crew.VehicleAt(0) is { IsPuppet: true };
		Measured($"played back to tick {lastPlayed}: the puppet against the recorded path on {compared} frames, worst"
			+ $" {F(worst)} m; seated as recorded on {seatedRight}, not on {seatedWrong}; the body's eye at the seat's on"
			+ $" {eyeCompared} frames, worst {F(worstEye, "0.0000")} m");
		Fact(sawPuppet && compared >= 2 * Second, $"the 4x4 is a puppet in slot 0, drawn on {compared} frames");
		Fact(compared > 0 && worst <= 0.02f, $"on the path the authority recorded, to the record's centimetre ({F(worst)} m)");
		Fact(seatedRight > Second && seatedWrong == 0,
			$"the player's body is seated as the recording had it, and put down when it was ({seatedRight} right, {seatedWrong} wrong)");
		Fact(eyeCompared > Second && worstEye <= 0.001f, $"at the wheel, its eye at the seat's on the hull as drawn ({F(worstEye, "0.0000")} m)");
		Fact(puppetAtEnd, "and the 4x4 is still there when the demo ends");
	}
}
