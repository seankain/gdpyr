using System;
using System.Collections.Generic;
using System.Globalization;
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
/// The mortar launcher in the game (Scripts/Fps/MortarLauncher.cs), run headless by
/// <c>scripts/mortar-checks.sh</c> on a copy of the tree with this file added as an
/// autoload; not part of the game.
///
/// A listen host on <c>Test.tscn</c> at <c>--fixed-fps 60</c>, its own player through the
/// input map: the map's launcher found, picked up with a held use key, carried and put
/// down somewhere else, and mounted; a tank 60 m off destroyed by the third of three
/// shells laid on the ground under it, and not by the second; the tube run dry; a can put
/// down 1.5 m off it left alone and the same can put down 0.8 m off it feeding five rounds;
/// a can brought to a full tube set down beside it and taken, whole, once the tube is
/// empty again.
///
/// One line per fact, <c>PASS</c> or <c>FAIL</c>, and <c>[checks] measured …</c> lines; the
/// process exits with the number of its facts that failed.
/// </summary>
public partial class MortarChecks : Node
{
	private const int Second = SimConfig.TickRate;
	private const float FloorY = 0.5f;

	/// <summary>Open floor, far from the barracks' guns, where the launcher is put down.</summary>
	private static readonly Vector3 Site = new(40f, FloorY + 0.1f, 50f);

	/// <summary>Where the tank is put: 60 m in front of the site, outside its own 38 m engagement.</summary>
	private static readonly Vector3 TankAt = new(40f, FloorY, -10f);

	/// <summary>Where shells nobody is meant to be under come down: 70 m out, 20 m to the side of the tank.</summary>
	private static readonly Vector3 Empty = new(60f, FloorY, -15f);

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
		if (System.Environment.GetEnvironmentVariable("MORTAR_CHECK") != "listen")
		{
			SetPhysicsProcess(false);
			return;
		}

		ProcessMode = ProcessModeEnum.Always;
		_run = Listen();
		GD.Print("[checks] mortar check: listen");
	}

	public override void _PhysicsProcess(double delta)
	{
		if (_log != null)
		{
			int count = _log.Since(_cursor, _read, out _cursor, out _);
			for (int i = 0; i < count; i++)
			{
				_events.Add(_read[i]);
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
			GD.Print($"[checks] listen: {_facts - _failures} of {_facts} facts hold");
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

		GD.Print($"[checks] {(ok ? "PASS" : "FAIL")} listen: {what}");
	}

	private static void Measured(string what) => GD.Print($"[checks] measured listen: {what}");

	private static string F(float v, string format = "0.00") => v.ToString(format, CultureInfo.InvariantCulture);

	private static string V(Vector3 v) => $"({F(v.X, "0.00")}, {F(v.Y, "0.00")}, {F(v.Z, "0.00")})";

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

	/// <summary>A press shorter than <see cref="UseTracker.HoldTicks"/>, or one held past it.</summary>
	private static IEnumerable<bool> Press(string action, int ticks)
	{
		Input.ActionPress(action);
		foreach (bool _ in Wait(ticks)) yield return true;
		Input.ActionRelease(action);
		foreach (bool _ in Wait(4)) yield return true;
	}

	private static IEnumerable<bool> Tap(string action) => Press(action, 4);

	private static IEnumerable<bool> Hold(string action) => Press(action, UseTracker.HoldTicks + 10);

	private static LocalInputSampler Sampler => PlayerManager.Instance.GetNode<LocalInputSampler>("LocalInputSampler");

	private static uint Tick => NetworkManager.Instance.Tick;

	private static EmplacementManager Guns => EmplacementManager.Instance;

	/// <summary>The body put at <paramref name="at"/>, looking along <paramref name="yaw"/>, level.</summary>
	private static void Put(fps_controller me, Vector3 at, float yaw = 0f)
	{
		me.Teleport(new Transform3D(new Basis(Vector3.Up, yaw), at));
		Sampler.Align(yaw, 0f);
	}

	/// <summary>The look turned onto a point on the ground.</summary>
	private static void LookAt(fps_controller me, Vector3 point)
	{
		Aim.Angles(point - me.EyePosition, out float yaw, out float pitch);
		Sampler.Align(yaw, pitch);
	}

	private int CanIndexAt(Vector3 near)
	{
		for (int i = 0; i < SimConfig.MaxAmmoCans; i++)
		{
			if (Guns.CanAt(i) is { Available: true, IsCarried: false } can && can.RestPosition.DistanceTo(near) < 0.5f)
			{
				return i;
			}
		}

		return -1;
	}

	/// <summary>The first available can on the ground nowhere near the launcher.</summary>
	private int SpareCan(MortarLauncher launcher)
	{
		for (int i = 0; i < SimConfig.MaxAmmoCans; i++)
		{
			if (Guns.CanAt(i) is { Available: true, IsCarried: false } can
				&& can.RestPosition.DistanceTo(launcher.DeployPosition) > 5f)
			{
				return i;
			}
		}

		return -1;
	}

	/// <summary>Walks up to a can and takes it: a tap with nothing nearer.</summary>
	private IEnumerable<bool> TakeCan(fps_controller me, int peer, AmmoCan can)
	{
		Put(me, can.RestPosition + (Vector3.Up * 0.1f));
		foreach (bool _ in Wait(6)) yield return true;
		foreach (bool _ in Tap("use")) yield return true;
		foreach (bool _ in Until(() => Guns.CarryOf(peer) == CarryKind.AmmoCan, 20)) yield return true;
	}

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

	// ---- the check ---------------------------------------------------------------------

	private IEnumerator<bool> Listen()
	{
		_log = AgentEventBus.Active ??= new AgentEventLog();
		_cursor = _log.Sequence;

		while (!Session.InGame || CombatManager.Instance?.Local?.Character == null || Guns == null)
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
		fps_controller me = local.Character;
		int peer = local.PeerId;

		// ---- the map's launcher ----
		int index = -1;
		for (int i = 0; i < Guns.GunCount; i++)
		{
			if (Guns.GunAt(i) is MortarLauncher)
			{
				index = i;
			}
		}

		var launcher = Guns.GunAt(index) as MortarLauncher;
		Fact(launcher is { Name: { } name } && name == "MortarLauncher1" && index == 2 && Guns.GunCount == 3,
			$"the map's launcher is gun {index} of {Guns.GunCount} ({launcher?.Name}), after the two heavy guns");
		if (launcher == null)
		{
			yield break;
		}

		Fact(launcher.WeaponDefinitionId == WeaponCatalog.MortarLauncher
			&& launcher.Weapon.Ammo == 5 && launcher.Stats.MagazineSize == 5,
			$"it fires weapon {launcher.WeaponDefinitionId} ({WeaponCatalog.NameOf(launcher.WeaponDefinitionId)}),"
			+ $" {launcher.Weapon.Ammo}/{launcher.Stats.MagazineSize} rounds");
		Measured($"its reach {F(launcher.MinRangeMeters, "0.0")} m to {F(launcher.MaxRangeMeters, "0.0")} m");

		// ---- picked up, carried, put down, mounted ----
		Put(me, launcher.DeployPosition + new Vector3(0f, 0.1f, 1.2f));
		foreach (bool _ in Wait(6)) yield return true;
		string prompt = Guns.PromptFor(local, Tick);
		Fact(prompt == "[E] mount   [hold E] carry the mortar", $"beside it the prompt is \"{prompt}\"");

		foreach (bool _ in Hold("use")) yield return true;
		foreach (bool _ in Until(() => Guns.CarryOf(peer) == CarryKind.Gun, 20)) yield return true;
		Fact(Guns.CarryOf(peer) == CarryKind.Gun && launcher.State == EmplacementStateId.Carried
			&& launcher.CarrierPeerId == peer, $"a held use key picks it up ({launcher.State}, carrier {launcher.CarrierPeerId})");
		prompt = Guns.PromptFor(local, Tick);
		Fact(prompt == "carrying the mortar launcher   [E] deploy", $"carrying it the prompt is \"{prompt}\"");
		Fact(Mathf.IsEqualApprox(Guns.MoveScaleOf(peer), SimConfig.CarryMoveScale),
			$"carrying it slows its carrier as a heavy gun does ({F(Guns.MoveScaleOf(peer))})");

		Put(me, Site);
		foreach (bool _ in Wait(10)) yield return true;
		Fact(launcher.GlobalPosition.DistanceTo(me.GlobalPosition) < 2f,
			$"it rides on its carrier to the site ({V(launcher.GlobalPosition)} beside {V(me.GlobalPosition)})");

		foreach (bool _ in Tap("use")) yield return true;
		foreach (bool _ in Until(() => launcher.State == EmplacementStateId.Deployed, 20)) yield return true;
		Fact(launcher.State == EmplacementStateId.Deployed && Guns.CarryOf(peer) == CarryKind.None
			&& launcher.DeployPosition.DistanceTo(me.SimPosition) < 0.05f && Mathf.Abs(launcher.DeployYaw) < 0.01f,
			$"a tap puts it down where its carrier stands, facing their way ({V(launcher.DeployPosition)}, yaw {F(launcher.DeployYaw)})");

		Put(me, launcher.DeployPosition + new Vector3(0f, 0.1f, 1.2f));
		foreach (bool _ in Wait(6)) yield return true;
		foreach (bool _ in Tap("use")) yield return true;
		foreach (bool _ in Until(() => Guns.TryMounted(peer, out _), 20)) yield return true;
		Fact(Guns.TryMounted(peer, out Emplacement mounted) && mounted == launcher && launcher.GunnerPeerId == peer,
			"a tap beside it mounts it");

		// ---- a tank, three shells ----
		UnitManager units = UnitManager.Instance;
		ushort tankId = units.ServerPlaceUnit(UnitCatalog.Tank, TankAt, Team.Strategist, OrderKind.None);
		units.ServerSetPlacedAutonomy(tankId, MicroAutonomy.Off);
		Unit tank = units.Find(tankId);
		Fact(tank != null && tank.IsAlive, $"a tank is put {F(Flat(TankAt - launcher.DeployPosition), "0.0")} m off ({tank?.Health} health)");
		if (tank == null)
		{
			yield break;
		}

		float hull = tank.Health;
		int victim = OwnerId.ForUnit(tankId);
		int marker = _events.Count;
		int firstShot = local.ShotsFired;
		foreach (bool _ in Wait(10)) yield return true;

		var dealt = new List<float>();
		bool aliveAfterTwo = false;
		for (int shot = 1; shot <= 3; shot++)
		{
			Vector3 under = tank.GlobalPosition;
			under.Y = launcher.DeployPosition.Y;
			LookAt(me, under);
			foreach (bool _ in Wait(4)) yield return true;

			Vector3 laid = launcher.TargetFor(me.EyePosition, me.Yaw, me.Pitch);
			if (shot == 1)
			{
				prompt = Guns.PromptFor(local, Tick);
				Measured($"mounted, laid on the tank: \"{prompt}\"; laid {F(laid.DistanceTo(under))} m from the ground under it");
				Fact(prompt.StartsWith("5/5 rounds  +0 in 0 cans  range 60 m", StringComparison.Ordinal),
					$"the prompt gives the tube, the cans and the range (\"{prompt}\")");
			}

			int before = Count(AgentEventKind.Damage, marker, e => e.B == victim);
			short ammo = launcher.Weapon.Ammo;
			foreach (bool _ in Tap("fire")) yield return true;
			Fact(launcher.Weapon.Ammo == ammo - 1, $"shell {shot} leaves the tube ({ammo} → {launcher.Weapon.Ammo})");

			foreach (bool _ in Until(() => Count(AgentEventKind.Damage, marker, e => e.B == victim) > before, 10 * Second)) yield return true;
			AgentEvent? hit = null;
			for (int i = _events.Count - 1; i >= marker; i--)
			{
				if (_events[i].Kind == AgentEventKind.Damage && _events[i].B == victim)
				{
					hit = _events[i];
					break;
				}
			}

			Fact(hit is { } h && h.C == WeaponCatalog.MortarLauncher && h.A == OwnerId.ForPeer(peer),
				$"shell {shot} strikes the tank, in the gunner's name, as weapon {hit?.C} ({F(hit?.X ?? 0f, "0")} dealt, {F(hit?.Y ?? -1f, "0")} left)");
			dealt.Add(hit?.X ?? 0f);
			if (shot == 2)
			{
				aliveAfterTwo = tank.IsAlive;
			}

			// The next round no sooner than the tube allows.
			foreach (bool _ in Until(() => Tick >= launcher.Weapon.NextFireTick, 5 * Second)) yield return true;
		}

		int kills = Count(AgentEventKind.Kill, marker, e => e.B == victim && e.C == WeaponCatalog.MortarLauncher);
		Fact(aliveAfterTwo, $"two direct hits leave the tank standing ({F(dealt.Count > 1 ? dealt[0] + dealt[1] : 0f, "0")} of {F(hull, "0")})");
		Fact(!tank.IsAlive && kills == 1,
			$"the third destroys it: {string.Join(" + ", dealt.ConvertAll(d => F(d, "0")))} on a {F(hull, "0")} hull, {kills} kill");

		// ---- the tube run dry ----
		while (launcher.Weapon.Ammo > 0)
		{
			LookAt(me, Empty);
			foreach (bool _ in Wait(2)) yield return true;
			foreach (bool _ in Tap("fire")) yield return true;
			foreach (bool _ in Until(() => Tick >= launcher.Weapon.NextFireTick, 5 * Second)) yield return true;
		}

		int shots = local.ShotsFired;
		foreach (bool _ in Tap("fire")) yield return true;
		foreach (bool _ in Wait(30)) yield return true;
		Fact(launcher.Weapon.Ammo == 0 && local.ShotsFired == shots,
			$"an empty tube fires nothing, and does not reload itself ({local.ShotsFired - shots} shots)");

		foreach (bool _ in Tap("use")) yield return true;
		foreach (bool _ in Until(() => !Guns.TryMounted(peer, out _), 20)) yield return true;
		Fact(!Guns.TryMounted(peer, out _), "a tap gets off it");

		// ---- a can 1.5 m off, then 0.8 m off ----
		int canIndex = SpareCan(launcher);
		AmmoCan can = Guns.CanAt(canIndex);
		Fact(can != null, $"a can is on the ground at the spawn (can {canIndex})");
		if (can == null)
		{
			yield break;
		}

		foreach (bool _ in TakeCan(me, peer, can)) yield return true;
		Fact(Guns.CarryOf(peer) == CarryKind.AmmoCan, "a tap at a can takes it");

		Vector3 off = launcher.DeployPosition + new Vector3(1.5f, 0.1f, 0f);
		Put(me, off);
		foreach (bool _ in Wait(6)) yield return true;
		prompt = Guns.PromptFor(local, Tick);
		Fact(prompt == "carrying a can   [E] set it down by the mortar", $"carrying it to the launcher the prompt is \"{prompt}\"");
		foreach (bool _ in Hold("use")) yield return true;
		foreach (bool _ in Wait(30)) yield return true;
		float apart = can.RestPosition.DistanceTo(launcher.DeployPosition);
		Fact(!can.IsCarried && can.Available && apart > SimConfig.MortarFeedReachMeters && launcher.Weapon.Ammo == 0,
			$"a can held down {F(apart)} m off it stays on the ground, whole, and the tube stays empty ({launcher.Weapon.Ammo})");

		int spent = Guns.CansSpent;
		foreach (bool _ in Tap("use")) yield return true;
		foreach (bool _ in Until(() => Guns.CarryOf(peer) == CarryKind.AmmoCan, 20)) yield return true;
		Put(me, launcher.DeployPosition + new Vector3(0.8f, 0.1f, 0f));
		foreach (bool _ in Wait(6)) yield return true;
		foreach (bool _ in Hold("use")) yield return true;
		foreach (bool _ in Until(() => launcher.Weapon.Ammo > 0, 30)) yield return true;
		Fact(launcher.Weapon.Ammo == 5 && !can.Available && Guns.CansSpent == spent + 1,
			$"the same can held down 0.80 m off it gives it five rounds ({launcher.Weapon.Ammo}/5, can spent {!can.Available})");

		// ---- a can brought to a full tube ----
		canIndex = SpareCan(launcher);
		can = Guns.CanAt(canIndex);
		if (can == null)
		{
			Fact(false, "a second can is on the ground at the spawn");
			yield break;
		}

		foreach (bool _ in TakeCan(me, peer, can)) yield return true;
		Put(me, launcher.DeployPosition + new Vector3(0f, 0.1f, 1.8f));
		foreach (bool _ in Wait(6)) yield return true;
		foreach (bool _ in Tap("use")) yield return true;
		foreach (bool _ in Wait(30)) yield return true;
		apart = can.RestPosition.DistanceTo(launcher.DeployPosition);
		Fact(!can.IsCarried && can.Available && apart <= SimConfig.MortarFeedReachMeters && launcher.Weapon.Ammo == 5,
			$"a can tapped onto a full tube is set down {F(apart)} m from it and waits, whole ({launcher.Weapon.Ammo}/5)");

		// From the side the can was brought from, where it is nearer than the tube.
		Put(me, launcher.DeployPosition + new Vector3(0f, 0.1f, 1.2f));
		foreach (bool _ in Wait(6)) yield return true;
		prompt = Guns.PromptFor(local, Tick);
		Fact(prompt == "[E] mount   [hold E] carry the mortar", $"there the prompt is the tube's (\"{prompt}\")");
		foreach (bool _ in Tap("use")) yield return true;
		foreach (bool _ in Until(() => Guns.TryMounted(peer, out _), 20)) yield return true;
		Fact(Guns.TryMounted(peer, out _) && Guns.CarryOf(peer) == CarryKind.None && can.Available && !can.IsCarried,
			$"a tap from where the can is nearer than the tube mounts the tube and leaves the can ({Guns.CarryOf(peer)})");
		prompt = Guns.PromptFor(local, Tick);
		Fact(prompt.StartsWith("5/5 rounds  +5 in 1 can", StringComparison.Ordinal), $"behind it, the prompt counts the can (\"{prompt}\")");
		if (!Guns.TryMounted(peer, out _))
		{
			yield break;
		}

		spent = Guns.CansSpent;
		int fired = 0;
		while (launcher.Weapon.Ammo > 0 && fired < 5)
		{
			LookAt(me, Empty);
			foreach (bool _ in Wait(2)) yield return true;
			short loaded = launcher.Weapon.Ammo;
			foreach (bool _ in Tap("fire")) yield return true;
			fired++;
			Fact(launcher.Weapon.Ammo == loaded - 1 || (fired == 5 && launcher.Weapon.Ammo == 5),
				$"shell {fired} of the tube leaves it ({loaded} → {launcher.Weapon.Ammo})");
			if (fired < 5)
			{
				Fact(can.Available, $"after shell {fired} of the tube the can still waits ({launcher.Weapon.Ammo}/5)");
			}

			foreach (bool _ in Until(() => Tick >= launcher.Weapon.NextFireTick, 5 * Second)) yield return true;
		}

		foreach (bool _ in Until(() => launcher.Weapon.Ammo > 0, 10)) yield return true;
		Fact(fired == 5 && launcher.Weapon.Ammo == 5 && !can.Available && Guns.CansSpent == spent + 1,
			$"the fifth empties the tube and the waiting can fills it ({launcher.Weapon.Ammo}/5 after {fired})");

		// Every shell that missed the tank landed and went off; let the last ones come down.
		foreach (bool _ in Wait(8 * Second)) yield return true;
		Fact(local.IsAlive && local.Health == SimConfig.MaxHealth,
			$"the gunner is unhurt after {local.ShotsFired - firstShot} of their own shells ({local.Health} health)");
	}

	private static float Flat(Vector3 v) => new Vector2(v.X, v.Z).Length();
}
