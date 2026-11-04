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
/// The vehicle kiosk in the game (docs/GROUND_VEHICLES.md §12, GV6), run headless by
/// <c>scripts/crew-vehicle-checks.sh kiosk</c> and <c>kiosk-pad</c> on a copy of the tree
/// with this file added as an autoload; not part of the game.
///
/// <c>CREW_KIOSK_CHECK</c> names the part:
/// <list type="bullet">
/// <item><c>listen</c>: a listen host on <c>Test.tscn</c> at <c>--fixed-fps 60</c>, its own
/// player through the input map and the panel's keys: the kiosk and its pads where §6.6
/// puts them, checked at the start; the prompt, and the panel opened by the use key; each
/// kind taken on its pad; out, too soon and a blocked pad refused on the panel; the panel
/// closed by E, Escape and walking off; each kind lost to a tank's round, refused while its
/// timer runs, ready again 45 s and 90 s after the loss to the tick, and taken again; a
/// vehicle left alone for a minute taken away, its timer started, and one a person stood
/// by kept; the gates the panel cannot reach, through the console's <c>kiosk</c>; and the
/// round's reset.</item>
/// <item><c>badpad</c>: the same map with the helicopter's pad moved into the bunker's
/// wall: its check fails, the kiosk does not offer it, and the 4x4 is still offered.</item>
/// <item><c>server</c>, <c>client</c>: a dedicated server and one client behind the relay
/// (40 ± 10 ms each way), keeping in step by marker files: the panel opened from the
/// client's own prediction of its tap, the request and its answer over the link
/// (<c>ClientRequestVehicle</c>, <c>ServerKioskResult</c>, §7.2), the kiosk's lines in the
/// vehicle message's header, and the console's answer a round trip later.</item>
/// </list>
///
/// One line per fact, <c>PASS</c> or <c>FAIL</c>, and <c>[checks] measured …</c> lines; the
/// process exits with the number of its facts that failed.
/// </summary>
public partial class CrewKioskChecks : Node
{
	private const int Second = SimConfig.TickRate;

	/// <summary>The floor's top on <c>Test.tscn</c>.</summary>
	private const float FloorY = 0.5f;

	/// <summary>Open floor, far from the kiosk, the pads and every defence.</summary>
	private static readonly Vector3 Away = new(-40f, FloorY + 0.1f, 30f);

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
		_role = System.Environment.GetEnvironmentVariable("CREW_KIOSK_CHECK");
		_dir = System.Environment.GetEnvironmentVariable("CREW_NET_DIR");
		if (string.IsNullOrEmpty(_role) || ((_role == "server" || _role == "client") && string.IsNullOrEmpty(_dir)))
		{
			SetPhysicsProcess(false);
			return;
		}

		ProcessMode = ProcessModeEnum.Always;
		_run = _role switch
		{
			"listen" => Listen(),
			"badpad" => BadPad(),
			"server" => Server(),
			"client" => Client(),
			_ => null,
		};

		if (_run == null)
		{
			GD.PrintErr($"[checks] no such part: {_role}");
			SetPhysicsProcess(false);
			GetTree().Quit(99);
			return;
		}

		GD.Print($"[checks] kiosk check: {_role}");
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

	/// <summary>A key pressed and let go, as a keyboard's events: what the panel's <c>_Input</c> reads.</summary>
	private static IEnumerable<bool> Key(Key key)
	{
		Input.ParseInputEvent(new InputEventKey { Keycode = key, PhysicalKeycode = key, Pressed = true });
		foreach (bool _ in Wait(2)) yield return true;
		Input.ParseInputEvent(new InputEventKey { Keycode = key, PhysicalKeycode = key, Pressed = false });
		foreach (bool _ in Wait(2)) yield return true;
	}

	private static float YawFacing(Vector3 direction) => Mathf.Atan2(-direction.X, -direction.Z);

	private static LocalInputSampler Sampler => PlayerManager.Instance.GetNode<LocalInputSampler>("LocalInputSampler");

	private static float Flat(Vector3 v) => new Vector2(v.X, v.Z).Length();

	private static uint Tick => NetworkManager.Instance.Tick;

	private static KioskPanel Panel => CombatManager.Instance.GetNode<CombatHud>("CombatHud").Kiosk;

	private static string HudLine => KioskHud.Line(CrewVehicleManager.Instance.KioskLines);

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

	private AgentEvent? Last(AgentEventKind kind, int from)
	{
		for (int i = _events.Count - 1; i >= from; i--)
		{
			if (_events[i].Kind == kind)
			{
				return _events[i];
			}
		}

		return null;
	}

	/// <summary>The slot of the kiosk's vehicle of a kind on the field, or −1.</summary>
	private static int KioskSlot(byte kind)
	{
		CrewVehicleManager crew = CrewVehicleManager.Instance;
		for (int slot = 0; slot < SimConfig.MaxCrewVehicles; slot++)
		{
			if (crew.VehicleAt(slot) is { FromKiosk: true } vehicle && (byte)vehicle.Kind == kind)
			{
				return slot;
			}
		}

		return -1;
	}

	/// <summary>The body put half a metre in front of the kiosk's cabinet, facing it.</summary>
	private static void AtKiosk(fps_controller me, float out_ = 0.5f, bool align = true)
	{
		VehicleKiosk kiosk = CrewVehicleManager.Instance.Kiosk;
		Vector3 front = kiosk.GlobalBasis.Z;
		Vector3 at = kiosk.GlobalPosition + (front * ((LockerSupply.CabinetDepthMeters * 0.5f) + out_)) + (Vector3.Up * 0.1f);
		float yaw = YawFacing(-front);
		me.Teleport(new Transform3D(new Basis(Vector3.Up, yaw), at));
		if (align)
		{
			Sampler.Align(yaw, 0f);
		}
	}

	private static void Put(fps_controller me, Vector3 at, bool align = true)
	{
		me.Teleport(new Transform3D(Basis.Identity, at));
		if (align)
		{
			Sampler.Align(0f, 0f);
		}
	}

	/// <summary>Opens the panel as a person does: at the kiosk, a tap of the use key.</summary>
	private IEnumerable<bool> OpenPanel(fps_controller me)
	{
		AtKiosk(me);
		foreach (bool _ in Wait(6)) yield return true;
		foreach (bool _ in Tap("use")) yield return true;
		foreach (bool _ in Until(() => Panel.IsOpen, 20)) yield return true;
	}

	/// <summary>A key on the open panel, and the answer waited for.</summary>
	private IEnumerable<bool> Ask(Key key)
	{
		int answers = CrewVehicleManager.Instance.KioskAnswers;
		foreach (bool _ in Key(key)) yield return true;
		foreach (bool _ in Until(() => CrewVehicleManager.Instance.KioskAnswers > answers, 20)) yield return true;
	}

	private static string LastConsoleLine()
	{
		FieldInfo lines = typeof(GameConsole).GetField("_lines", BindingFlags.NonPublic | BindingFlags.Instance);
		return lines?.GetValue(GameConsole.Instance) is List<string> { Count: > 0 } list ? list[^1] : string.Empty;
	}

	private static void Console(string line)
	{
		MethodInfo submit = typeof(GameConsole).GetMethod("Submit", BindingFlags.NonPublic | BindingFlags.Instance);
		submit.Invoke(GameConsole.Instance, new object[] { line });
	}

	/// <summary>
	/// A tank's round at a vehicle's hull from 20 m over it, in nobody's name — from above,
	/// because both pads have the bunker or the perimeter on one side or another.
	/// </summary>
	private IEnumerable<bool> Wreck(CrewVehicle vehicle)
	{
		Vector3 centre = vehicle.HullCentre;
		Vector3 from = centre + new Vector3(0.5f, 20f, 0f);
		CombatManager.Instance.SpawnProjectile(Tick, OwnerId.None, WeaponCatalog.Cannon, from, (centre - from).Normalized(), 0);
		foreach (bool _ in Until(() => vehicle.Destroyed, 2 * Second)) yield return true;
	}

	/// <summary>
	/// The console's <c>kiosk 4x4</c>, and its answer: the scrollback's last line and the
	/// <c>vehicle_requested</c> event's refusal.
	/// </summary>
	private IEnumerable<bool> ConsoleGate(KioskRefusal expected, string where)
	{
		int from = _events.Count;
		Console("kiosk 4x4");
		foreach (bool _ in Wait(2)) yield return true;
		AgentEvent? asked = Last(AgentEventKind.VehicleRequested, from);
		string line = LastConsoleLine();
		string code = KioskRules.CodeOf(expected);
		Fact(asked is { } e && e.C == (int)expected && line.Contains($"refused ({code})"),
			$"{where}: `kiosk 4x4` is refused {code} ({line})");
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
		int peer = local.PeerId;

		// ---- the kiosk and its pads, where §6.6 puts them ----
		VehicleKiosk kiosk = crew.Kiosk;
		Fact(kiosk is { Name: { } name } && name == "VehicleKiosk1", $"the map's kiosk is found ({kiosk?.Name})");
		if (kiosk == null)
		{
			yield break;
		}

		foreach (bool _ in Until(() => crew.PadsChecked, 2 * Second)) yield return true;
		Fact(crew.PadsChecked && crew.PadFailure(0) == null && crew.PadFailure(1) == null,
			$"both pads pass their check at the start ({crew.PadFailure(0) ?? "clear"}, {crew.PadFailure(1) ?? "clear"})");

		Node3D locker = GetTree().CurrentScene.GetNode<Node3D>("WeaponLocker1");
		Node3D bunker = GetTree().CurrentScene.GetNode<Node3D>("Pillbox");
		PhysicsDirectSpaceState3D space = kiosk.GetWorld3D().DirectSpaceState;
		var ray = new PhysicsRayQueryParameters3D { CollisionMask = CollisionLayers.World };
		ray.From = kiosk.GlobalPosition + Vector3.Up;
		ray.To = kiosk.GlobalPosition + (Vector3.Down * 3f);
		Godot.Collections.Dictionary under = space.IntersectRay(ray);
		float floorUnderKiosk = under.Count > 0 ? under["position"].AsVector3().Y : float.NaN;
		ray.From = locker.GlobalPosition + Vector3.Up;
		ray.To = locker.GlobalPosition + (Vector3.Down * 3f);
		under = space.IntersectRay(ray);
		float floorUnderLocker = under.Count > 0 ? under["position"].AsVector3().Y : float.NaN;
		float apart = Flat(kiosk.GlobalPosition - locker.GlobalPosition);

		// Nowhere is in both reaches: a 5 cm grid over the two cabinets and round them.
		int both = 0;
		Vector3 mid = (kiosk.GlobalPosition + locker.GlobalPosition) * 0.5f;
		for (float x = -4f; x <= 4f; x += 0.05f)
		{
			for (float z = -3f; z <= 3f; z += 0.05f)
			{
				Vector3 p = mid + new Vector3(x, 0f, z);
				if (KioskRules.InReach(kiosk.FootprintDistance(p))
					&& LockerSupply.InReach(LockerSupply.FootprintDistance(p, locker.GlobalPosition, Mathf.Atan2(locker.GlobalBasis.Z.X, locker.GlobalBasis.Z.Z))))
				{
					both++;
				}
			}
		}

		Measured($"the kiosk at {V(kiosk.GlobalPosition)}, the floor under it at y {F(floorUnderKiosk, "0.000")}; the locker at"
			+ $" {V(locker.GlobalPosition)}, the floor under it at y {F(floorUnderLocker, "0.000")}; {F(apart)} m apart");
		Fact(Mathf.Abs(floorUnderKiosk - kiosk.GlobalPosition.Y) < 0.01f && apart < 4f && both == 0,
			$"the kiosk stands on the bunker's floor beside the locker, their reaches apart ({both} points in both)");

		Vector3 door = bunker.GlobalPosition + (bunker.GlobalBasis.X * 15f);
		Vector3 outward = bunker.GlobalBasis.X;
		kiosk.TryPad(0, out Transform3D pad4);
		kiosk.TryPad(1, out Transform3D padHeli);
		Vector3 rel = pad4.Origin - door;
		float along = rel.X * outward.X + rel.Z * outward.Z;
		float across = Mathf.Abs(rel.X * outward.Z - rel.Z * outward.X);
		float facing4 = (-pad4.Basis.Z).Dot(outward);
		Measured($"the 4x4's pad at {V(pad4.Origin)}: {F(along, "0.000")} m out of the door, {F(across, "0.000")} m off its axis,"
			+ $" facing it by {F(facing4, "0.0000")}");
		Fact(Mathf.Abs(along - 8f) < 0.01f && across < 0.01f && facing4 > 0.9999f,
			"the 4x4's pad is 8 m out from the bunker's door on its axis, facing away from it");

		float heliFromDoor = Flat(padHeli.Origin - door);
		var cylinder = new CylinderShape3D { Radius = 8f, Height = 8f };
		var clear = new PhysicsShapeQueryParameters3D
		{
			Shape = cylinder,
			Transform = new Transform3D(Basis.Identity, padHeli.Origin + (Vector3.Up * (0.2f + 4f))),
			CollisionMask = CollisionLayers.World | CollisionLayers.Vehicles,
		};
		int inTheWay = space.IntersectShape(clear, 4).Count;
		float nearestSpawn = float.MaxValue;
		foreach (Node node in GetTree().GetNodesInGroup("player_spawn"))
		{
			if (node is Node3D spawn)
			{
				nearestSpawn = Mathf.Min(nearestSpawn, Flat(spawn.GlobalPosition - padHeli.Origin));
			}
		}

		Vector3 toMiddle = new Vector3(-padHeli.Origin.X, 0f, -padHeli.Origin.Z).Normalized();
		float facingHeli = (-padHeli.Basis.Z).Dot(toMiddle);
		Measured($"the helicopter's pad at {V(padHeli.Origin)}: {F(heliFromDoor, "0.000")} m from the door's middle; the nearest"
			+ $" player spawn {F(nearestSpawn)} m; {inTheWay} bodies in 8 m round it; facing the field's middle by {F(facingHeli, "0.0000")}");
		Fact(Mathf.Abs(heliFromDoor - 25f) < 0.05f && inTheWay == 0 && nearestSpawn > 8f && Cargo.InField(padHeli.Origin),
			"the helicopter's pad is 25 m from the bunker's door, with 8 m clear round it, inside the field");
		Fact(facingHeli > 0.9999f, "and faces the field's middle");
		Fact(HudLine == "4x4 ready · helicopter ready", $"both kinds are ready when the round starts: `{HudLine}`");

		// ---- the use key and the panel ----
		Put(me, locker.GlobalPosition + (locker.GlobalBasis.Z * 0.8f) + (Vector3.Up * 0.1f));
		foreach (bool _ in Wait(6)) yield return true;
		string atLocker = EmplacementManager.Instance.PromptFor(local, Tick);
		AtKiosk(me);
		foreach (bool _ in Wait(6)) yield return true;
		string atKiosk = EmplacementManager.Instance.PromptFor(local, Tick);
		Fact(atLocker.StartsWith("[E] weapon locker") && atKiosk == "[E] vehicle kiosk",
			$"the prompt names whichever is nearer: `{atLocker.Split('\n')[0]}` at the locker, `{atKiosk}` at the kiosk");

		// A console 4x4 counts against nobody's cap.
		int console4 = crew.Spawn(CrewVehicleCatalog.FourByFour, Away + new Vector3(10f, 0.6f, 0f), 0f);
		foreach (bool _ in Wait(3)) yield return true;
		Fact(console4 >= 0 && crew.StatusOf(0, Tick) == KioskStatus.Ready,
			"a 4x4 the console put down leaves the kiosk's 4x4 ready");
		crew.Remove(console4);

		foreach (bool _ in OpenPanel(me)) yield return true;
		Fact(Panel.IsOpen && InputFocus.Menu, "a tap of E at the kiosk opens its panel, which holds the keyboard");

		int mark = _events.Count;
		foreach (bool _ in Ask(Godot.Key.Key1)) yield return true;
		int slot4 = KioskSlot(0);
		CrewVehicle truck = crew.VehicleAt(slot4);
		AgentEvent? spawned = Last(AgentEventKind.VehicleSpawned, mark);
		AgentEvent? requested = Last(AgentEventKind.VehicleRequested, mark);
		Vector3 spawnedAt = spawned is { } sp ? new Vector3(sp.X, 0f, sp.Y) : Vector3.Zero;
		float yaw4 = truck?.Yaw ?? float.NaN;
		Measured($"`1`: slot {slot4}, put down at ({F(spawnedAt.X, "0.000")}, {F(spawnedAt.Z, "0.000")}),"
			+ $" heading {F(Mathf.RadToDeg(yaw4), "0.00")}°; the pad's {F(Mathf.RadToDeg(KioskRules.PadYaw(pad4.Basis.Z)), "0.00")}°");
		Fact(truck != null && requested is { A: var asker, B: 0, C: 0 } && asker == peer && spawned is { C: var by } && by == peer,
			"`1` on the panel takes a 4x4: vehicle_requested taken, vehicle_spawned in the asker's name");
		Fact(truck != null && Flat(spawnedAt - pad4.Origin) < 0.01f
			&& Mathf.Abs(Mathf.AngleDifference(yaw4, KioskRules.PadYaw(pad4.Basis.Z))) < 0.001f
			&& (truck.Pose.Forward).Dot(-pad4.Basis.Z) > 0.999f,
			"on its pad, facing the marker's −Z");
		Fact(!Panel.IsOpen && !InputFocus.Menu, "the panel closes on the vehicle taken");
		Fact(HudLine == "4x4 out · helicopter ready", $"the HUD's line: `{HudLine}`");

		foreach (bool _ in OpenPanel(me)) yield return true;
		foreach (bool _ in Ask(Godot.Key.Key1)) yield return true;
		string outLine = Panel.Menu.StatusLine(crew.KioskLines);
		Fact(Panel.IsOpen && Panel.Menu.LastRefusal == KioskRefusal.Out && outLine.StartsWith("refused (out): "),
			$"`1` again is refused out, in the panel's footer, the panel open ({outLine})");

		foreach (bool _ in Ask(Godot.Key.Key2)) yield return true;
		Fact(Panel.Menu.LastRefusal == KioskRefusal.TooSoon && Panel.Menu.RefusedKind == 1,
			$"`2` a tenth of a second later is refused too_soon ({Panel.Menu.StatusLine(crew.KioskLines)})");

		// Something on the helicopter's pad: a 4x4 the console put there.
		var padPose = new CrewPose(padHeli.Origin, KioskRules.PadYaw(padHeli.Basis.Z));
		int blocker = crew.Spawn(CrewVehicleCatalog.FourByFour, padPose.Position + new Vector3(0f, 0.703f, 0f), padPose.Yaw);
		foreach (bool _ in Wait(Second / 2)) yield return true;
		foreach (bool _ in Ask(Godot.Key.Key2)) yield return true;
		Fact(Panel.Menu.LastRefusal == KioskRefusal.PadBlocked,
			$"`2` with a 4x4 on the helicopter's pad is refused pad_blocked ({Panel.Menu.StatusLine(crew.KioskLines)})");
		crew.Remove(blocker);
		foreach (bool _ in Wait(Second / 2)) yield return true;

		mark = _events.Count;
		foreach (bool _ in Ask(Godot.Key.Key2)) yield return true;
		int slotHeli = KioskSlot(1);
		CrewVehicle heli = crew.VehicleAt(slotHeli);
		Fact(heli != null && !Panel.IsOpen && Flat(heli.Pose.Position - padHeli.Origin) < 0.01f
			&& heli.Pose.Forward.Dot(-padHeli.Basis.Z) > 0.999f && Count(AgentEventKind.VehicleSpawned, mark) == 1,
			"`2` then takes the helicopter, on its pad facing the field's middle, and closes the panel");
		Fact(HudLine == "4x4 out · helicopter out", $"the HUD's line: `{HudLine}`");

		// ---- the panel closes ----
		foreach (bool _ in OpenPanel(me)) yield return true;
		bool opened = Panel.IsOpen;
		foreach (bool _ in Key(Godot.Key.E)) yield return true;
		bool byE = !Panel.IsOpen;
		foreach (bool _ in OpenPanel(me)) yield return true;
		foreach (bool _ in Key(Godot.Key.Escape)) yield return true;
		bool byEscape = !Panel.IsOpen;
		foreach (bool _ in OpenPanel(me)) yield return true;
		AtKiosk(me, 1.4f);
		foreach (bool _ in Wait(4)) yield return true;
		bool byWalking = !Panel.IsOpen;
		Fact(opened && byE && byEscape && byWalking && !InputFocus.Menu,
			$"the panel closes on E ({byE}), Escape ({byEscape}) and leaving the kiosk's reach ({byWalking})");

		// ---- both lost, their timers, and taken again ----
		// The helicopter first, then the 4x4: nothing of the kiosk's is on the field while
		// the 4x4's timer runs, so nothing is left alone meanwhile.
		foreach (bool _ in Wreck(heli)) yield return true;
		uint lostHeli = heli.DestroyedTick;
		Fact(heli.Destroyed && crew.KioskLines[1] is { Status: KioskStatus.Counting, SecondsLeft: 90 },
			$"a tank's round wrecks the helicopter and its kind counts down from 90 s (`{HudLine}`)");

		mark = _events.Count;
		foreach (bool _ in Wreck(truck)) yield return true;
		uint lost4 = truck.DestroyedTick;
		KioskLine line4 = crew.KioskLines[0];
		Fact(truck.Destroyed && line4.Status == KioskStatus.Counting && line4.SecondsLeft == 45,
			$"a tank's round wrecks the kiosk's 4x4 and its kind counts down from 45 s (`{HudLine}`)");

		foreach (bool _ in OpenPanel(me)) yield return true;
		foreach (bool _ in Ask(Godot.Key.Key1)) yield return true;
		string notReady = Panel.Menu.StatusLine(crew.KioskLines);
		Fact(Panel.Menu.LastRefusal == KioskRefusal.NotReady && notReady.StartsWith("refused (not_ready): ready in 0:4"),
			$"`1` while it counts down is refused not_ready ({notReady})");
		foreach (bool _ in Key(Godot.Key.Escape)) yield return true;

		foreach (bool _ in Until(() => crew.Count == 0, 3 * Second)) yield return true;
		Fact(!_timedOut && Tick - lost4 <= CrewCombat.WreckBurnTicks + 2,
			$"both wrecks are taken away two seconds on ({crew.Count} on the field)");
		foreach (bool _ in Until(() => crew.StatusOf(0, Tick) == KioskStatus.Ready, 50 * Second)) yield return true;
		uint ready4 = Tick;
		Measured($"the 4x4 lost on tick {lost4}, ready on {ready4}: {ready4 - lost4} ticks");
		Fact(ready4 - lost4 == 45 * Second, $"the 4x4 is ready again 45 s after its loss, to the tick ({ready4 - lost4})");

		foreach (bool _ in OpenPanel(me)) yield return true;
		foreach (bool _ in Ask(Godot.Key.Key1)) yield return true;
		slot4 = KioskSlot(0);
		truck = crew.VehicleAt(slot4);
		Fact(truck != null && !truck.Destroyed && crew.KioskLines[0].Status == KioskStatus.Out,
			"and the panel takes it again");

		// Beside it while the helicopter's timer runs, so it is not left alone.
		Vector3 besideTruck = truck.Pose.Position + new Vector3(0f, -0.6f, 0f) + (truck.Pose.Basis * new Vector3(-4f, 0f, 0f));
		Put(me, besideTruck);
		foreach (bool _ in Until(() => crew.StatusOf(1, Tick) == KioskStatus.Ready, 95 * Second)) yield return true;
		uint readyHeli = Tick;
		Measured($"the helicopter lost on tick {lostHeli}, ready on {readyHeli}: {readyHeli - lostHeli} ticks");
		Fact(readyHeli - lostHeli == 90 * Second, $"the helicopter is ready again 90 s after its loss, to the tick ({readyHeli - lostHeli})");
		foreach (bool _ in OpenPanel(me)) yield return true;
		foreach (bool _ in Ask(Godot.Key.Key2)) yield return true;
		slotHeli = KioskSlot(1);
		heli = crew.VehicleAt(slotHeli);
		Fact(heli != null && !heli.Destroyed, "and the panel takes it again");

		// ---- left alone ----
		// By the 4x4 first, so its minute has not started; then by the helicopter, which
		// starts the 4x4's and stops the helicopter's.
		Put(me, besideTruck);
		foreach (bool _ in Wait(2 * Second)) yield return true;
		bool truckClockBefore = truck.Abandon.Running;
		Put(me, heli.Pose.Position + new Vector3(5f, 0.1f, 0f));
		uint since = 0;
		foreach (bool _ in Until(() => truck.Abandon.Running, 2 * Second)) yield return true;
		since = Tick;
		foreach (bool _ in Until(() => crew.VehicleAt(slot4) != truck, 65 * Second)) yield return true;
		uint taken = Tick;
		Measured($"the 4x4 left alone from tick {since}, taken away on {taken}: {taken - since} ticks;"
			+ $" the helicopter, a person 5 m off, still there: {crew.VehicleAt(slotHeli) == heli}");
		Fact(!truckClockBefore && !_timedOut && taken - since == 60 * Second && crew.VehicleAt(slotHeli) == heli,
			"a 4x4 left alone for a minute is taken away, to the tick; the helicopter with a person by it is not");
		Fact(crew.KioskLines[0] is { Status: KioskStatus.Counting, SecondsLeft: 45 } && crew.Abandoned >= 1,
			$"and its kind counts down from 45 s, as for a loss (`{HudLine}`)");

		Put(me, Away);
		foreach (bool _ in Until(() => crew.VehicleAt(slotHeli) != heli, 65 * Second)) yield return true;
		Fact(!_timedOut && crew.KioskLines[1].Status == KioskStatus.Counting,
			$"the helicopter left alone goes too, its kind counting down (`{HudLine}`)");

		// ---- the gates the panel cannot reach: the console's `kiosk` ----
		Put(me, Away);
		foreach (bool _ in Wait(4)) yield return true;
		foreach (bool _ in ConsoleGate(KioskRefusal.NotAtKiosk, $"{F(crew.KioskDistance(me.SimPosition), "0.0")} m from the kiosk")) yield return true;

		// Seated: a console 4x4 in the bunker, its driver's seat in the kiosk's reach.
		VehicleKiosk k = crew.Kiosk;
		Vector3 front = k.GlobalBasis.Z;
		Vector3 side = new Vector3(front.Z, 0f, -front.X);
		float truckYaw = YawFacing(side);
		Vector3 seatOffset = new Basis(Vector3.Up, truckYaw) * new Vector3(-0.45f, 0f, -0.3f);
		Vector3 driverAt = k.GlobalPosition + (front * ((LockerSupply.CabinetDepthMeters * 0.5f) + 0.8f));
		Vector3 origin = driverAt - seatOffset;
		origin.Y = k.GlobalPosition.Y + 0.703f;
		int inside = crew.Spawn(CrewVehicleCatalog.FourByFour, origin, truckYaw);
		CrewVehicle parked = crew.VehicleAt(inside);
		foreach (bool _ in Wait(Second)) yield return true;

		// In at the bed's door, on the far side — the driver's door is in the kiosk's reach,
		// where a tap opens the kiosk — and a hold of E round to the wheel, as a person would.
		int bedSeat = 2;
		Vector3 doorAt = parked.Pose.ToWorld(parked.Seats[bedSeat].Door);
		me.Teleport(new Transform3D(new Basis(Vector3.Up, YawFacing(parked.Pose.Position - doorAt)), doorAt + (Vector3.Up * 0.1f)));
		foreach (bool _ in Wait(6)) yield return true;
		foreach (bool _ in Tap("use")) yield return true;
		bool inBed = crew.TrySeat(peer, out _, out int seatIn) && parked.Seats[seatIn].Role == SeatRole.Passenger;
		Input.ActionPress("use");
		foreach (bool _ in Wait(UseTracker.HoldTicks + 4)) yield return true;
		Input.ActionRelease("use");
		foreach (bool _ in Wait(6)) yield return true;
		bool atWheel = crew.IsDriving(peer, out _);
		Measured($"in the bed {inBed}, then at the wheel {atWheel} of a 4x4 in the bunker,"
			+ $" {F(crew.KioskDistance(me.SimPosition))} m from the kiosk's footprint");
		foreach (bool _ in ConsoleGate(KioskRefusal.Seated, "seated in a 4x4 in the kiosk's reach")) yield return true;
		foreach (bool _ in Tap("use")) yield return true;
		crew.Remove(inside);

		// Carrying, then mounted: the heavy gun brought to the kiosk.
		Emplacement gun = EmplacementManager.Instance.GunAt(0);
		Put(me, gun.DeployPosition + new Vector3(0.5f, 0.1f, 0f));
		foreach (bool _ in Wait(6)) yield return true;
		Input.ActionPress("use");
		foreach (bool _ in Wait(UseTracker.HoldTicks + 4)) yield return true;
		Input.ActionRelease("use");
		foreach (bool _ in Wait(6)) yield return true;
		AtKiosk(me);
		foreach (bool _ in Wait(4)) yield return true;
		Measured($"carrying {EmplacementManager.Instance.CarryOf(peer)}, {F(crew.KioskDistance(me.SimPosition))} m from the kiosk's footprint");
		foreach (bool _ in ConsoleGate(KioskRefusal.Carrying, "carrying the heavy gun at the kiosk")) yield return true;
		AtKiosk(me, 1.3f);
		foreach (bool _ in Wait(4)) yield return true;
		foreach (bool _ in Tap("use")) yield return true;
		foreach (bool _ in Wait(4)) yield return true;
		foreach (bool _ in Tap("use")) yield return true;
		Measured($"mounted {EmplacementManager.Instance.TryMounted(peer, out _)}, {F(crew.KioskDistance(me.SimPosition))} m from the kiosk's footprint");
		foreach (bool _ in ConsoleGate(KioskRefusal.Mounted, "behind the heavy gun, 1.3 m from the kiosk")) yield return true;
		foreach (bool _ in Tap("use")) yield return true;

		// An unknown kind.
		int before = _events.Count;
		AtKiosk(me);
		foreach (bool _ in Wait(4)) yield return true;
		crew.RequestVehicle(7, out KioskRefusal unknown);
		foreach (bool _ in Wait(2)) yield return true;
		Fact(unknown == KioskRefusal.Invalid && Last(AgentEventKind.VehicleRequested, before) is { C: (int)KioskRefusal.Invalid },
			"a kind this build has none of is refused invalid");

		// Dead.
		MethodInfo damage = typeof(CombatManager).GetMethod("Damage", BindingFlags.NonPublic | BindingFlags.Instance,
			new[] { typeof(PlayerCombat), typeof(float), typeof(bool), typeof(PlayerCombat), typeof(int), typeof(byte), typeof(uint) });
		damage.Invoke(CombatManager.Instance, new object[] { local, 1000f, true, null, OwnerId.None, (byte)0, Tick });
		foreach (bool _ in Wait(2)) yield return true;
		foreach (bool _ in ConsoleGate(KioskRefusal.Dead, $"dead at the kiosk (health {local.Health})")) yield return true;
		foreach (bool _ in Until(() => local.IsAlive, 20 * Second)) yield return true;

		// The strategist.
		CombatManager.Instance.RequestTeam(Team.Strategist);
		foreach (bool _ in Until(() => local.Team == Team.Strategist, 5 * Second)) yield return true;
		foreach (bool _ in ConsoleGate(KioskRefusal.NotGround, "in the strategist's chair")) yield return true;
		CombatManager.Instance.RequestTeam(Team.GroundForce);
		foreach (bool _ in Until(() => local.IsAlive && local.Team == Team.GroundForce, 20 * Second)) yield return true;

		// ---- a new round ----
		// A 4x4 taken and wrecked, so its kind is counting down when the round restarts.
		foreach (bool _ in Until(() => crew.StatusOf(0, Tick) == KioskStatus.Ready, 50 * Second)) yield return true;
		AtKiosk(me);
		foreach (bool _ in Wait(Second)) yield return true;
		crew.RequestVehicle(0, out KioskRefusal again);
		foreach (bool _ in Wait(2)) yield return true;
		CrewVehicle last = crew.VehicleAt(KioskSlot(0));
		if (last != null)
		{
			foreach (bool _ in Wreck(last)) yield return true;
		}

		foreach (bool _ in Wait(2)) yield return true;
		string beforeReset = HudLine;
		int onField = crew.Count;
		CombatManager.Instance.ServerResetRound(Tick);
		foreach (bool _ in Wait(2)) yield return true;
		Fact(again == KioskRefusal.None && beforeReset.StartsWith("4x4 0:4") && onField > 0 && crew.Count == 0
			&& HudLine == "4x4 ready · helicopter ready",
			$"a new round takes every vehicle away and makes both kinds ready (`{beforeReset}`, {onField} on the field → `{HudLine}`)");
		Measured($"the kiosk answered {crew.KioskRequests} requests, {crew.KioskTaken} taken; {crew.Abandoned} taken away left alone");
	}

	// ---- a pad that fails its check ----------------------------------------------------------

	private IEnumerator<bool> BadPad()
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
		CrewVehicleManager crew = CrewVehicleManager.Instance;
		foreach (bool _ in Until(() => crew.PadsChecked, 2 * Second)) yield return true;
		Measured($"the helicopter's pad: {crew.PadFailure(1) ?? "passed"}; the 4x4's: {crew.PadFailure(0) ?? "passed"}");
		Fact(crew.PadFailure(1) != null && crew.PadFailure(0) == null,
			"the helicopter's pad in the bunker's wall fails its check; the 4x4's passes");
		Fact(crew.StatusOf(1, Tick) == KioskStatus.Disabled && HudLine == "4x4 ready",
			$"the kiosk does not offer the helicopter: `{HudLine}`");
		string debug = crew.DebugLine();
		Fact(debug.Contains("helicopter off") && debug.Contains("helicopter pad failed"), $"the debug HUD says so: `{debug}`");

		fps_controller me = local.Character;
		foreach (bool _ in OpenPanel(me)) yield return true;
		Panel.Refresh(new KioskMenuSituation(true, true, false, false, false, 0f));
		foreach (bool _ in Ask(Godot.Key.Key2)) yield return true;
		string footer = Panel.Menu.StatusLine(crew.KioskLines);
		Fact(Panel.Menu.LastRefusal == KioskRefusal.Invalid && footer == "refused (invalid): this kiosk does not offer it"
			&& KioskMenu.RowLabel(1, crew.KioskLines[1]) == "[2] helicopter — not offered here",
			$"`2` is refused invalid ({footer})");
		foreach (bool _ in Wait(Second / 2)) yield return true;
		foreach (bool _ in Ask(Godot.Key.Key1)) yield return true;
		Fact(KioskSlot(0) >= 0 && !Panel.IsOpen, "`1` still takes a 4x4");
	}

	// ---- over the link -----------------------------------------------------------------------

	private IEnumerator<bool> Server()
	{
		_log = AgentEventBus.Active ??= new AgentEventLog();
		_cursor = _log.Sequence;

		foreach (bool _ in Until(() => Session.InGame && CrewVehicleManager.Instance != null, 60 * Second)) yield return true;
		foreach (bool _ in Until(() => Has("client.peer"), 120 * Second)) yield return true;
		Fact(!_timedOut, "the client is in");
		if (_timedOut)
		{
			yield break;
		}

		int peer = int.TryParse(Read("client.peer"), out int read) ? read : 0;
		CrewVehicleManager crew = CrewVehicleManager.Instance;
		fps_controller body = CombatManager.Instance.Find(peer).Character;
		foreach (bool _ in Until(() => crew.PadsChecked, 5 * Second)) yield return true;
		Fact(crew.PadsChecked && crew.PadFailure(0) == null && crew.PadFailure(1) == null,
			"the dedicated server checked both pads, and both pass");

		int mark = _events.Count;
		AtKiosk(body, align: false);
		Mark("at-kiosk");
		foreach (bool _ in Until(() => Has("client-asked"), 60 * Second)) yield return true;
		foreach (bool _ in Wait(2)) yield return true;
		int taken = Count(AgentEventKind.VehicleRequested, mark, e => e.A == peer && e.B == 0 && e.C == 0);
		int refusedOut = Count(AgentEventKind.VehicleRequested, mark, e => e.A == peer && e.C == (int)KioskRefusal.Out);
		int spawned = Count(AgentEventKind.VehicleSpawned, mark, e => e.B == 0 && e.C == peer);
		Fact(taken == 1 && refusedOut == 1 && spawned == 1 && KioskSlot(0) >= 0,
			$"the client's requests reach the one validator: taken {taken}, out {refusedOut}; a 4x4 spawned in its name {spawned}");

		mark = _events.Count;
		Put(body, Away, align: false);
		Mark("away");
		foreach (bool _ in Until(() => Has("client-done"), 60 * Second)) yield return true;
		foreach (bool _ in Wait(2)) yield return true;
		Fact(Count(AgentEventKind.VehicleRequested, mark, e => e.A == peer && e.B == 1 && e.C == (int)KioskRefusal.NotAtKiosk) == 1,
			"and its console's `kiosk helicopter` from the open floor is refused not_at_kiosk");
	}

	private IEnumerator<bool> Client()
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
		Mark("client.peer", NetworkManager.Instance.LocalPeerId.ToString(CultureInfo.InvariantCulture));

		CrewVehicleManager crew = CrewVehicleManager.Instance;
		fps_controller me = local.Character;
		foreach (bool _ in Until(() => Has("at-kiosk"), 60 * Second)) yield return true;
		foreach (bool _ in Until(() => crew.KioskDistance(me.SimPosition) < 0.8f, 5 * Second)) yield return true;
		foreach (bool _ in Wait(Second)) yield return true;
		Fact(crew.HasKiosk && HudLine == "4x4 ready · helicopter ready",
			$"this client finds the kiosk, and the header's lines say both kinds are ready: `{HudLine}`");
		string prompt = EmplacementManager.Instance.PromptFor(local, Tick);

		foreach (bool _ in Tap("use")) yield return true;
		foreach (bool _ in Until(() => Panel.IsOpen, 20)) yield return true;
		Fact(Panel.IsOpen && prompt == "[E] vehicle kiosk",
			$"`{prompt}`: a tap of E opens the panel from this client's own reading of it");

		ulong frameBefore = Engine.GetPhysicsFrames();
		int answers = crew.KioskAnswers;
		foreach (bool _ in Key(Godot.Key.Key1)) yield return true;
		foreach (bool _ in Until(() => crew.KioskAnswers > answers, 5 * Second)) yield return true;
		bool answered = !_timedOut;
		ulong answeredIn = Engine.GetPhysicsFrames() - frameBefore;
		foreach (bool _ in Until(() => KioskSlot0OnClient(crew) >= 0, 2 * Second)) yield return true;
		int slot = KioskSlot0OnClient(crew);
		crew.Kiosk.TryPad(0, out Transform3D pad4);
		CrewVehicle puppet = crew.VehicleAt(slot);
		Measured($"`1` answered {answeredIn} ticks after the key, over the link: {KioskRules.CodeOf(crew.LastKioskAnswer)};"
			+ $" the 4x4 drawn in slot {slot} at {(puppet != null ? V(puppet.Pose.Position) : "nowhere")}");
		Fact(answered && crew.LastKioskAnswer == KioskRefusal.None && !Panel.IsOpen,
			"`1` is answered taken over the link, and the panel closes");
		Fact(puppet != null && Flat(puppet.Pose.Position - pad4.Origin) < 0.05f,
			"and this client draws the 4x4 on its pad");
		foreach (bool _ in Until(() => HudLine == "4x4 out · helicopter ready", Second)) yield return true;
		Fact(!_timedOut, $"the header's lines follow: `{HudLine}`");

		foreach (bool _ in Tap("use")) yield return true;
		foreach (bool _ in Until(() => Panel.IsOpen, 20)) yield return true;
		answers = crew.KioskAnswers;
		foreach (bool _ in Key(Godot.Key.Key1)) yield return true;
		foreach (bool _ in Until(() => crew.KioskAnswers > answers, 5 * Second)) yield return true;
		string footer = Panel.Menu.StatusLine(crew.KioskLines);
		Fact(Panel.IsOpen && Panel.Menu.LastRefusal == KioskRefusal.Out && footer.StartsWith("refused (out): "),
			$"`1` again is refused out over the link, in the footer ({footer})");
		foreach (bool _ in Key(Godot.Key.Escape)) yield return true;
		Mark("client-asked");

		foreach (bool _ in Until(() => Has("away"), 30 * Second)) yield return true;
		foreach (bool _ in Until(() => crew.KioskDistance(me.SimPosition) > 10f, 5 * Second)) yield return true;
		foreach (bool _ in Wait(10)) yield return true;
		answers = crew.KioskAnswers;
		Console("kiosk helicopter");
		string said = LastConsoleLine();
		foreach (bool _ in Until(() => crew.KioskAnswers > answers, 5 * Second)) yield return true;
		foreach (bool _ in Wait(2)) yield return true;
		string answer = LastConsoleLine();
		Fact(said == "asked the kiosk for a helicopter" && answer.Contains("kiosk, helicopter: refused (not_at_kiosk)"),
			$"the console's `kiosk helicopter` from the open floor: `{said}`, then a round trip later `{answer}`");
		Mark("client-done");
	}

	private static int KioskSlot0OnClient(CrewVehicleManager crew)
	{
		for (int slot = 0; slot < SimConfig.MaxCrewVehicles; slot++)
		{
			if (crew.VehicleAt(slot) is { Kind: CrewVehicleKind.FourByFour, Destroyed: false })
			{
				return slot;
			}
		}

		return -1;
	}
}
