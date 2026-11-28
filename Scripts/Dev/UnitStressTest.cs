using System;
using System.Collections.Generic;
using Gdpyr.Core;
using Gdpyr.Fps;
using Gdpyr.Match;
using Gdpyr.Net;
using Gdpyr.Rts;
using Gdpyr.Sim;
using Godot;

namespace Gdpyr.Dev;

/// <summary>
/// The unit stress test (docs/UNIT_STRESS_TEST.md): an offline round on its own scene,
/// <c>Scenes/Dev/UnitStressTest.tscn</c>, where the local player fights as the ground force
/// against as many strategist units as the panel puts round the barracks, and the panel says
/// what that costs a frame.
///
/// A development tool, and nothing in the game knows it is here. It drives the game through
/// what is already public: <see cref="NetworkManager.PlayOffline"/> as the menu's "Practice
/// offline" does, <see cref="CombatManager.ApplyHostSettings"/> as the host panel does (and the
/// copy of the game mode that leaves for this process to tune), <see cref="CombatManager.RequestTeam"/>
/// as the role menu does, <see cref="UnitManager.ServerPlaceUnit"/> as a scenario does, and a
/// unit's <see cref="Unit.Order"/> as a barracks sets its fresh units'. Authority only: a client
/// of this scene places nothing.
/// </summary>
public partial class UnitStressTest : Node
{
	/// <summary>What <see cref="Session.Map"/> calls this scene. Not a catalog id: it is not hostable.</summary>
	public const string MapId = "dev-unit-stress";

	public const string ScenePath = "res://Scenes/Dev/UnitStressTest.tscn";

	/// <summary>The innermost ring's radius from the barracks' centre [m]: its 8 × 8 m building's corner is 5.7 m out.</summary>
	public const float InnerRingMeters = 8.5f;

	/// <summary>Rings tried before a wave gives up on finding room.</summary>
	public const int MaxRings = 24;

	/// <summary>How far the navigation mesh may move a spot before the spot is taken to be inside something [m].</summary>
	public const float MaxSnapMeters = 0.75f;

	/// <summary>How far the player has to move before a hunting unit's point is moved after them [m].</summary>
	public const float RetargetMeters = 3f;

	/// <summary>Ticks between hunters' refreshes and keep-alive top-ups.</summary>
	private const int UpkeepTicks = SimConfig.TickRate / 2;

	/// <summary>The kinds the panel offers, in its order, and what each is in the catalog. <c>Mixed*</c> cycle.</summary>
	public static readonly (string Label, byte[] Kinds)[] UnitChoices =
	{
		("Rifleman", new[] { UnitCatalog.Infantry }),
		("Heavy gunner", new[] { UnitCatalog.HeavyGunner }),
		("Sniper", new[] { UnitCatalog.Sniper }),
		("Operative", new[] { UnitCatalog.Operative }),
		("Technical", new[] { UnitCatalog.Technical }),
		("Tank", new[] { UnitCatalog.Tank }),
		("Supply truck", new[] { UnitCatalog.SupplyTruck }),
		("Builder", new[] { UnitCatalog.Builder }),
		("Scout drone", new[] { UnitCatalog.ScoutDrone }),
		("Dropship", new[] { UnitCatalog.Dropship }),
		("Mixed foot", new[]
		{
			UnitCatalog.Infantry, UnitCatalog.Infantry, UnitCatalog.HeavyGunner, UnitCatalog.Infantry,
			UnitCatalog.Sniper, UnitCatalog.Operative,
		}),
		("Mixed army", new[]
		{
			UnitCatalog.Infantry, UnitCatalog.Infantry, UnitCatalog.HeavyGunner, UnitCatalog.Infantry,
			UnitCatalog.Technical, UnitCatalog.Sniper, UnitCatalog.Infantry, UnitCatalog.Tank,
			UnitCatalog.Operative,
		}),
	};

	[ExportGroup("Round")]

	/// <summary>Ground bots beside the player. <c>--bots</c> on the command line wins over it.</summary>
	[Export(PropertyHint.Range, "0,14")]
	public int GroundBots;

	/// <summary>
	/// Computer strategists. Zero, so nothing but the panel orders the units; a computer one also
	/// trains units of its own and orders the panel's. <c>--bots n:m</c> wins over it.
	/// </summary>
	[Export(PropertyHint.Range, "0,2")]
	public int StrategistBots;

	[Export(PropertyHint.Range, "1,600")]
	public int RoundMinutes = 240;

	[Export(PropertyHint.Range, "1,999")]
	public int GroundForceTickets = 999;

	/// <summary>Answers the role menu with the ground force for the local player, at every round's start.</summary>
	[Export]
	public bool JoinGroundForce = true;

	[ExportGroup("Measuring")]

	/// <summary>Starts with v-sync off, so a frame's cost is not hidden under the display's interval.</summary>
	[Export]
	public bool UncappedFrameRate = true;

	/// <summary>Seconds between <c>[stress]</c> lines in the log while anything is on the field.</summary>
	[Export(PropertyHint.Range, "1,60")]
	public float LogIntervalSeconds = 5f;

	/// <summary>The barracks units are put round; the first in the <c>barracks</c> group when empty.</summary>
	[Export]
	public NodePath BarracksPath;

	private readonly List<ushort> _hunters = new();
	private readonly List<Vector3> _taken = new();
	private readonly List<ushort> _hunterScratch = new();

	private UnitStressPanel _panel;
	private Barracks _barracks;
	private bool _authority;
	private string _disabledReason;

	private float _waveClock;
	private float _logClock;
	private int _cycle;

	/// <summary>One second of frames, for the panel and the log.</summary>
	public FrameWindow Frames { get; } = new(1f);

	/// <summary>What the panel's controls say. The panel writes these; the upkeep reads them.</summary>
	public int Kind { get; set; } = 10;

	public int Count { get; set; } = 16;

	public StressOrder Order { get; set; } = StressOrder.HuntPlayer;

	public bool Tactics { get; set; } = true;

	public float IntervalSeconds { get; set; } = 20f;

	public float FpsFloor { get; set; } = 45f;

	public StressMode Mode { get; private set; } = StressMode.Manual;

	/// <summary>The ramp while one runs or after it finished; null before the first.</summary>
	public StressRamp Ramp { get; private set; }

	/// <summary>Units the panel has put on the field this session.</summary>
	public int Spawned { get; private set; }

	/// <summary>The last thing the panel did, for its status line.</summary>
	public string LastAction { get; private set; } = "F10 opens the panel";

	/// <summary>Null when this process can place units; otherwise why not.</summary>
	public string DisabledReason => _disabledReason;

	public override void _Ready()
	{
		NetworkManager net = NetworkManager.Instance;
		CombatManager combat = CombatManager.Instance;
		if (net == null || combat == null || UnitManager.Instance == null)
		{
			_disabledReason = "the game's autoloads are not loaded";
			GD.PushError($"[stress] {_disabledReason}");
			return;
		}

		BuildCsgNow(Owner ?? GetParent());

		// Run as the scene on its own (the editor's Run Current Scene, or the scene's path on the
		// command line) nothing has started the network: this is the menu's "Practice offline".
		// A process launched with --listen or --server has started it already, and keeps it.
		net.PlayOffline();
		_authority = !net.IsClient;
		if (!_authority)
		{
			_disabledReason = "this process is a client; only the authority places units";
		}
		else
		{
			ConfigureRound(combat);
		}

		if (UncappedFrameRate && !Bootstrap.IsDedicatedServer)
		{
			DisplayServer.WindowSetVsyncMode(DisplayServer.VSyncMode.Disabled);
		}

		if (!Bootstrap.IsDedicatedServer)
		{
			_panel = new UnitStressPanel { Name = "UnitStressPanel", Stress = this };
			AddChild(_panel);
		}

		GD.Print($"[stress] unit stress test: field limit {SimConfig.MaxUnits} units"
			+ (_disabledReason != null ? $" | disabled: {_disabledReason}" : string.Empty));
	}

	/// <summary>
	/// Builds every CSG shape's geometry and collider now. A CSG shape builds them in a deferred
	/// call (Godot 4.6, <c>CSGShape3D::_make_dirty</c>), and a scene that is the startup scene —
	/// which this one is, run on its own — reaches its first physics tick before the first
	/// deferred call runs. That tick is where the unit manager bakes the navigation mesh, and a
	/// CSG shape with no mesh yet adds nothing to it (<c>CSGShape3D::get_meshes</c>): the cover
	/// would be missing from the bake, and units would path into it. A map reached through the
	/// menu's scene change has a frame between, so the game's own maps never meet this.
	/// </summary>
	private static void BuildCsgNow(Node root)
	{
		if (root == null)
		{
			return;
		}

		foreach (Node child in root.GetChildren())
		{
			if (child is CsgShape3D csg && csg.IsRootShape())
			{
				csg.Call("_update_shape");
			}

			BuildCsgNow(child);
		}
	}

	/// <summary>
	/// The round this scene plays: its own map in the session, a long round with tickets to spare,
	/// and the bots it asks for. Before the first tick, which is when the bot director reads them.
	/// </summary>
	private void ConfigureRound(CombatManager combat)
	{
		string path = string.IsNullOrEmpty(Owner?.SceneFilePath) ? ScenePath : Owner.SceneFilePath;
		var map = new MapInfo(MapId, "Unit stress test", path, "a development scene: waves of strategist units round a barracks");
		if (!Session.TrySelectMap(map, out string mapError))
		{
			GD.PushWarning($"[stress] the session keeps its map: {mapError}");
		}

		// The host panel's path: it replaces the combat manager's game mode with a copy, which
		// is this process's to change, and the shared resource on disk is left as it was.
		combat.ApplyHostSettings(new HostSettings(map, Math.Min(RoundMinutes, HostSettings.MaxRoundMinutes),
			GroundForceTickets, 1, Math.Clamp(StrategistBots, 1, HostSettings.MaxStrategistSize)));

		GameModeDefinition mode = combat.GameMode;
		mode.RoundDurationMinutes = Math.Max(RoundMinutes, 1);
		mode.GroundForceTickets = Math.Clamp(GroundForceTickets, 1, HostSettings.MaxGroundForceTickets);
		mode.BotGroundForce = Math.Max(GroundBots, 0);
		mode.BotStrategists = Math.Max(StrategistBots, 0);

		GD.Print($"[stress] round: {mode.RoundDurationMinutes} min, {mode.GroundForceTickets} tickets,"
			+ $" bots {mode.BotGroundForce}:{mode.BotStrategists} unless --bots says otherwise");
	}

	public override void _PhysicsProcess(double delta)
	{
		if (!_authority || !Session.InGame)
		{
			return;
		}

		CombatManager combat = CombatManager.Instance;
		if (JoinGroundForce && combat is { LocalAwaitingRole: true, Local: not null })
		{
			combat.RequestTeam(Team.GroundForce);
		}

		uint tick = NetworkManager.Instance?.Tick ?? 0;
		if (tick % UpkeepTicks != 0)
		{
			return;
		}

		RetargetHunters();

		switch (Mode)
		{
			case StressMode.KeepAlive:
				TopUp(Count);
				break;

			case StressMode.Ramp when Ramp is { Running: true }:
				TopUp(Ramp.Target);
				break;
		}
	}

	public override void _Process(double delta)
	{
		float frameMs = (float)(delta * 1000.0);
		Frames.Add(frameMs);

		if (!_authority)
		{
			return;
		}

		int alive = UnitManager.Instance?.LiveUnitCount ?? 0;
		if (Mode == StressMode.Ramp && Ramp is { Running: true } ramp && ramp.Sample(frameMs, alive, Costs()))
		{
			if (ramp.Finished)
			{
				LastAction = $"ramp done: {ramp.StopReason}";
				Mode = StressMode.Manual;
				LogRamp(ramp);
			}
			else
			{
				GD.Print($"[stress] ramp step: {ramp.Steps[^1]} -> next target {ramp.Target}");
			}
		}

		if (Mode == StressMode.Waves)
		{
			_waveClock += (float)delta;
			if (_waveClock >= IntervalSeconds)
			{
				_waveClock = 0f;
				SpawnWave(Count);
			}
		}

		_logClock += (float)delta;
		if (_logClock >= LogIntervalSeconds)
		{
			_logClock = 0f;
			if (alive > 0 || Mode != StressMode.Manual)
			{
				GD.Print($"[stress] {Describe()}");
			}
		}
	}

	// ---- what the panel asks for ----------------------------------------------------------

	/// <summary>Puts a wave of the panel's kind, order and tactics round the barracks.</summary>
	public int SpawnWave(int count)
	{
		int placed = Place(count);
		LastAction = placed == count
			? $"spawned {placed} x {UnitChoices[Kind].Label}"
			: $"spawned {placed} of {count} x {UnitChoices[Kind].Label}: {Shortfall()}";
		GD.Print($"[stress] {LastAction} | alive {UnitManager.Instance?.LiveUnitCount ?? 0}");
		return placed;
	}

	/// <summary>Takes every unit and structure off the field, as a round's restart does, and stops any mode.</summary>
	public void ClearField()
	{
		SetMode(StressMode.Manual);
		if (_authority && UnitManager.Instance is { } units)
		{
			units.ClearUnits();
		}

		_hunters.Clear();
		LastAction = "field cleared";
		GD.Print("[stress] field cleared");
	}

	public void SetMode(StressMode mode)
	{
		if (Mode == StressMode.Ramp && mode != StressMode.Ramp && Ramp is { Running: true } running)
		{
			running.Stop("stopped from the panel");
			LogRamp(running);
		}

		Mode = mode;
		_waveClock = 0f;

		switch (mode)
		{
			case StressMode.Waves:
				LastAction = $"a wave of {Count} every {IntervalSeconds:0} s";
				SpawnWave(Count);
				break;

			case StressMode.KeepAlive:
				LastAction = $"keeping {Count} alive";
				break;

			case StressMode.Ramp:
				// Top-ups land up to half a second into a step, and the engine's two times and the
				// unit pass are each the last whole second's: 2.5 s keeps the step's own placing out
				// of what it measures.
				Ramp = new StressRamp(Count, SimConfig.MaxUnits, IntervalSeconds, Math.Clamp(IntervalSeconds * 0.3f, 2.5f, 5f), FpsFloor);
				Ramp.Start();
				LastAction = $"ramp: +{Count} every {IntervalSeconds:0} s until under {FpsFloor:0} fps";
				GD.Print($"[stress] {LastAction} (the first {Ramp.SettleSeconds:0.#} s of each step are not measured)");
				break;

			default:
				LastAction = "manual";
				break;
		}
	}

	/// <summary>One line: what is on the field and what a frame costs. The panel's stats and the log's lines.</summary>
	public string Describe()
	{
		UnitManager units = UnitManager.Instance;
		FrameCosts costs = Costs();
		return $"alive {units?.LiveUnitCount ?? 0} | slots {units?.SlotCount ?? 0}/{SimConfig.MaxUnits}"
			+ $" | {Frames.Fps:0.0} fps | frame {Frames.MeanMs:0.00} ms (p95 {Frames.P95Ms:0.00}, worst {Frames.WorstMs:0.00})"
			+ $" | unit pass {costs.UnitPassMs:0.000} ms | worst physics tick {costs.PhysicsWorstMs:0.00} ms"
			+ $" | worst idle frame {costs.IdleWorstMs:0.00} ms"
			+ $" | draws {Monitor(Performance.Monitor.RenderTotalDrawCallsInFrame):0}"
			+ $" | objects {Monitor(Performance.Monitor.RenderTotalObjectsInFrame):0}"
			+ $" | primitives {Monitor(Performance.Monitor.RenderTotalPrimitivesInFrame):0}";
	}

	/// <summary>
	/// What the engine and the unit manager last said a frame cost: each is set once a second —
	/// the engine's two to the slowest step of that second (Godot's <c>Main::iteration</c>), the
	/// unit pass to the mean of that second's ticks.
	/// </summary>
	public static FrameCosts Costs() => new(
		UnitManager.Instance?.UnitMilliseconds ?? 0f,
		(float)(Monitor(Performance.Monitor.TimePhysicsProcess) * 1000.0),
		(float)(Monitor(Performance.Monitor.TimeProcess) * 1000.0));

	public static double Monitor(Performance.Monitor monitor) => Performance.GetMonitor(monitor);

	// ---- placing ---------------------------------------------------------------------------

	/// <summary>Tops the field up to <paramref name="wanted"/> living units, with the panel's kind and order.</summary>
	private void TopUp(int wanted)
	{
		int alive = UnitManager.Instance?.LiveUnitCount ?? 0;
		if (alive < wanted)
		{
			Place(wanted - alive);
		}
	}

	/// <summary>Places up to <paramref name="count"/> units round the barracks; how many it placed.</summary>
	private int Place(int count)
	{
		UnitManager units = UnitManager.Instance;
		if (!_authority || units == null || count <= 0)
		{
			return 0;
		}

		Barracks barracks = FindBarracks(units);
		Vector3 centre = barracks?.GlobalPosition ?? Vector3.Zero;
		byte[] kinds = UnitChoices[Math.Clamp(Kind, 0, UnitChoices.Length - 1)].Kinds;

		float radius = 0f;
		foreach (byte kind in kinds)
		{
			radius = MathF.Max(radius, UnitCatalog.Definition(kind)?.RadiusMeters ?? 0.4f);
		}

		float spacing = StressRings.SpacingFor(radius);

		// Everybody already standing there, so a second wave forms outside the first.
		_taken.Clear();
		for (int i = 0; i < units.SlotCount; i++)
		{
			if (units.UnitAt(i) is { IsOnField: true } standing)
			{
				_taken.Add(standing.GlobalPosition);
			}
		}

		// The map takes a baked mesh in at the end of the physics frame it was baked on, and until
		// then answers every query with the origin: on that one tick the rings are used as they are.
		Rid navigation = barracks?.GetWorld3D()?.NavigationMap ?? default;
		bool snap = units.NavigationReady && navigation.IsValid
			&& NavigationServer3D.MapGetClosestPoint(navigation, centre + new Vector3(InnerRingMeters, 0f, 0f)) != Vector3.Zero;
		uint tick = NetworkManager.Instance?.Tick ?? 0;
		int placed = 0;

		foreach (Vector3 spot in StressRings.Spots(centre, InnerRingMeters, spacing, MaxRings))
		{
			if (placed >= count || units.SlotCount >= SimConfig.MaxUnits)
			{
				break;
			}

			float clearance = spacing * 0.9f;
			if (!StressRings.IsClear(spot, _taken, clearance))
			{
				continue;
			}

			Vector3 at = spot;
			if (snap)
			{
				// Off the mesh is inside a wall or a building: the closest point on it is then
				// further away than the mesh's erosion moves an open spot.
				Vector3 onMesh = NavigationServer3D.MapGetClosestPoint(navigation, spot);
				if (new Vector2(onMesh.X - spot.X, onMesh.Z - spot.Z).Length() > MaxSnapMeters)
				{
					continue;
				}

				at = onMesh;
				if (!StressRings.IsClear(at, _taken, clearance))
				{
					continue;
				}
			}

			byte kind = kinds[_cycle % kinds.Length];
			ushort id = units.ServerPlaceUnit(kind, at, Team.Strategist, OrderKind.None);
			if (id == 0)
			{
				break;
			}

			_cycle++;
			_taken.Add(at);
			placed++;
			Spawned++;

			if (units.Find(id) is { } unit)
			{
				Command(unit, tick);
			}

			units.ServerSetPlacedAutonomy(id, Tactics ? MicroAutonomy.On : MicroAutonomy.Off);
		}

		return placed;
	}

	/// <summary>Gives a placed unit the panel's order, as a barracks gives a fresh unit its rally.</summary>
	private void Command(Unit unit, uint tick)
	{
		switch (Order)
		{
			case StressOrder.HuntPlayer:
			{
				bool player = TryPlayer(out Vector3 at, out int owner);
				unit.Order = new UnitOrder
				{
					Kind = OrderKind.Attack,
					Target = player ? at : SpawnCentroid(),
					Anchor = unit.GlobalPosition,
					TargetOwnerId = player ? owner : OwnerId.None,
				};
				_hunters.Add(unit.UnitId);
				break;
			}

			case StressOrder.AttackSpawn:
				unit.Order = new UnitOrder
				{
					Kind = OrderKind.Attack,
					Target = SpawnCentroid(),
					Anchor = unit.GlobalPosition,
					TargetOwnerId = OwnerId.None,
				};
				break;

			default:
				// ServerPlaceUnit left it holding where it stands.
				return;
		}

		unit.OrderTick = tick;
		unit.NextScanTick = 0;
	}

	/// <summary>
	/// Moves each hunting unit's point after the player, and names the player if it did not
	/// (it was placed while nobody was on the field). A unit somebody else ordered since — a
	/// computer strategist's — is not a hunter any more.
	/// </summary>
	private void RetargetHunters()
	{
		if (_hunters.Count == 0 || !TryPlayer(out Vector3 at, out int owner) || UnitManager.Instance is not { } units)
		{
			return;
		}

		_hunterScratch.Clear();
		foreach (ushort id in _hunters)
		{
			if (units.Find(id) is not { IsAlive: true } unit || unit.Order.Kind != OrderKind.Attack
				|| (unit.Order.TargetOwnerId != owner && unit.Order.TargetOwnerId != OwnerId.None))
			{
				continue;
			}

			_hunterScratch.Add(id);
			float dx = unit.Order.Target.X - at.X;
			float dz = unit.Order.Target.Z - at.Z;
			if ((dx * dx) + (dz * dz) > RetargetMeters * RetargetMeters || unit.Order.TargetOwnerId == OwnerId.None)
			{
				unit.Order.Target = at;
				unit.Order.TargetOwnerId = owner;
			}
		}

		_hunters.Clear();
		_hunters.AddRange(_hunterScratch);
	}

	/// <summary>Where the local player's feet are and who they are, while they are alive on the ground.</summary>
	private static bool TryPlayer(out Vector3 at, out int owner)
	{
		at = Vector3.Zero;
		owner = OwnerId.None;
		if (CombatManager.Instance?.Local is not { IsAlive: true, Character: not null } local)
		{
			return false;
		}

		at = local.Character.SimPosition;
		owner = OwnerId.ForPeer(local.PeerId);
		return true;
	}

	private static Vector3 SpawnCentroid() => PlayerManager.Instance?.SpawnCentroid ?? Vector3.Zero;

	private Barracks FindBarracks(UnitManager units)
	{
		if (IsInstanceValid(_barracks))
		{
			return _barracks;
		}

		_barracks = BarracksPath is { IsEmpty: false } ? GetNodeOrNull<Barracks>(BarracksPath) : null;
		_barracks ??= units.BarracksCount > 0 ? units.BarracksAt(0) : null;
		return _barracks;
	}

	private string Shortfall()
	{
		UnitManager units = UnitManager.Instance;
		if (units == null)
		{
			return "no unit manager";
		}

		if (units.SlotCount >= SimConfig.MaxUnits)
		{
			return $"the field holds {SimConfig.MaxUnits} (SimConfig.MaxUnits), the dead for 2 s after they fall";
		}

		return $"no free spot within {MaxRings} rings of the barracks";
	}

	private static void LogRamp(StressRamp ramp)
	{
		GD.Print($"[stress] ramp: +{ramp.Step} every {ramp.StepSeconds:0} s, floor {ramp.FpsFloor:0} fps — {ramp.StopReason}");
		foreach (RampStep step in ramp.Steps)
		{
			GD.Print($"[stress]   {step}");
		}

		GD.Print($"[stress] ramp: held {ramp.FpsFloor:0} fps up to {ramp.HeldTarget} units");
	}
}
