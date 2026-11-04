using System;
using System.Collections.Generic;
using Gdpyr.Core;
using Gdpyr.Match;
using Gdpyr.Net;
using Gdpyr.Rts;
using Gdpyr.Sim;
using Gdpyr.Sim.Agent;
using Godot;

namespace Gdpyr.Fps;

/// <summary>
/// Sixth autoload, beside <see cref="EmplacementManager"/>: the ground force's crew
/// vehicles — the 4x4 and the helicopter (docs/GROUND_VEHICLES.md §6). The registry,
/// fixed at <see cref="SimConfig.MaxCrewVehicles"/> slots; who sits where; getting in,
/// changing seat and getting out; the vehicles' tick; and the pin that keeps every
/// seated body on its seat.
///
/// The server is the only authority, as everywhere else. Getting in, changing seat and
/// getting out are the use key in the input frame, resolved by
/// <see cref="EmplacementSim.Resolve"/> beside the guns and the lockers and acted on
/// here; they are not predicted (GV-D4, NETCODE.md §10.2).
///
/// <para><b>On the wire</b> (§7): the authority sends every client
/// <c>ServerCrewVehicles</c> on the player snapshot's tick, each strategist only the
/// vehicles its side sees (§7.3). A client draws every vehicle it does not drive as a
/// puppet at the render clock (§8.1), predicts the one it drives from its own frames and
/// replays it against each record (§8.2), and puts every seated body on its seat as it
/// draws the vehicle (§8.3). Who sits where on a client is what the records say, and
/// nothing else. People drive and ride; bots never drive or fly (GV-D15), and an HTN bot
/// rides — at the gun or as a passenger — with a person at the controls (GV8).</para>
///
/// <para><b>The kiosk</b> (§6.6, <c>CrewVehicleManager.Kiosk.cs</c>): the map's
/// <see cref="VehicleKiosk"/>, each kind's timer and cap, the pads' check at the start, a
/// request through <see cref="KioskRules.Check"/> and the vehicle on its pad, and a
/// vehicle left alone for a minute taken away.</para>
///
/// Like the other managers it never ticks itself: <see cref="PlayerManager"/> drives
/// it, after the players are simulated and before the units are (§4), and on a client
/// around its own prediction.
/// </summary>
public partial class CrewVehicleManager : Node
{
	/// <summary>A player's capsule, as <c>Scenes/fps_controller.tscn</c> authors it: what an exit point must have room for.</summary>
	private const float PlayerRadiusMeters = 0.5f;

	private const float PlayerHeightMeters = 2f;

	/// <summary>How high the line from a vehicle to an exit point runs, so a low wall in the way counts [m].</summary>
	private const float ExitSightHeightMeters = 1f;

	/// <summary>How far below an exit point the ground is looked for [m].</summary>
	private const float ExitGroundProbeMeters = 6f;

	/// <summary>How far off the ground a clearance shape is lifted, so the floor it stands on is not "in the way" [m].</summary>
	private const float ClearanceLiftMeters = 0.15f;

	/// <summary>How long the HUD says why a press of the use key did not get somebody out [ticks].</summary>
	private const int ExitRefusalShownTicks = SimConfig.TickRate * 2;

	/// <summary>Beyond the collider's flat radius, how far in front of a player the console puts a vehicle [m].</summary>
	private const float ConsoleSpawnGapMeters = 1.5f;

	private readonly struct SeatRef
	{
		public readonly int Slot;
		public readonly int Seat;

		public SeatRef(int slot, int seat)
		{
			Slot = slot;
			Seat = seat;
		}
	}

	/// <summary>Why a press did not get a player out, and when (§8.5's "too high" and "no room").</summary>
	private readonly struct ExitRefusal
	{
		public readonly uint Tick;
		public readonly bool TooHigh;

		public ExitRefusal(uint tick, bool tooHigh)
		{
			Tick = tick;
			TooHigh = tooHigh;
		}
	}

	/// <summary>Records queued between the idle frame they arrive on and the physics step that applies them.</summary>
	private const int MaxPendingPackets = 16;

	public static CrewVehicleManager Instance { get; private set; }

	private readonly CrewVehicle[] _vehicles = new CrewVehicle[SimConfig.MaxCrewVehicles];
	private readonly Dictionary<int, SeatRef> _seated = new();
	private readonly Dictionary<int, InputFrame> _frames = new();
	private readonly Dictionary<int, ExitRefusal> _exitRefusals = new();
	private readonly List<int> _scratch = new();

	private Node3D _root;
	private CrewSeatView _view;

	// ---- the wire (§7) ----------------------------------------------------------------

	private readonly CrewVehicleRecord[] _records = new CrewVehicleRecord[SimConfig.MaxCrewVehicles];
	private readonly CrewVehicleRecord[] _visibleRecords = new CrewVehicleRecord[SimConfig.MaxCrewVehicles];
	private readonly bool[] _seenScratch = new bool[SimConfig.MaxCrewVehicles];

	/// <summary>
	/// The kiosk's line for each kind (§7.1): on the authority from its timers
	/// (<c>RefreshKioskLines</c>), on a client as the server last said it; nothing offered
	/// on a map without a kiosk.
	/// </summary>
	private readonly KioskLine[] _kiosk = { KioskLine.None, KioskLine.None };

	// ---- a client (§8) ---------------------------------------------------------------

	private readonly List<byte[]> _pending = new();
	private readonly CrewPuppetRoster _roster = new();
	private readonly SnapshotInterpolator[] _interpolators = new SnapshotInterpolator[SimConfig.MaxCrewVehicles];
	private readonly CrewVehicleRecord[] _latest = new CrewVehicleRecord[SimConfig.MaxCrewVehicles];
	private readonly uint[] _latestTick = new uint[SimConfig.MaxCrewVehicles];
	private readonly bool[] _hasLatest = new bool[SimConfig.MaxCrewVehicles];
	private readonly PuppetChange[] _changes = new PuppetChange[SimConfig.MaxCrewVehicles];
	private readonly CrewVehicleRecord[] _decoded = new CrewVehicleRecord[SimConfig.MaxCrewVehicles];
	private readonly KioskLine[] _decodedKiosk = new KioskLine[CrewVehicleCodec.Kinds];
	private readonly Dictionary<int, SeatRef> _nextSeated = new();
	private readonly SmoothRenderClock _renderClock = new();
	private CrewLedger _crewLedger;
	private int _drivenSlot = -1;

	/// <summary>A hull's boxes placed for one query: no allocation per round or per unit.</summary>
	private readonly HitObb[] _hullScratch = new HitObb[CrewLayout.MaxHullBoxes];

	private PhysicsRayQueryParameters3D _ray;
	private PhysicsShapeQueryParameters3D _shape;
	private readonly Godot.Collections.Array<Rid> _shapeExclude = new();
	private CapsuleShape3D _playerCapsule;

	/// <summary>Vehicles on the field. For the debug HUD.</summary>
	public int Count
	{
		get
		{
			int count = 0;
			for (int i = 0; i < _vehicles.Length; i++)
			{
				count += _vehicles[i] != null ? 1 : 0;
			}

			return count;
		}
	}

	/// <summary>Players in a seat, every vehicle together.</summary>
	public int Aboard => _seated.Count;

	/// <summary>
	/// Client: the driven vehicle's corrections and their size, published once a second
	/// as the character's are (docs/NETCODE.md §8): mispredictions a second, and the mean
	/// and worst position error of every comparison, in sync or not.
	/// </summary>
	public NetStats PredictionStats { get; } = new();

	/// <summary>Client: ticks re-run by the last replay of the driven vehicle, a snap's included.</summary>
	public int LastReplayTicks { get; private set; }

	/// <summary>Client: the longest replay of the driven vehicle since this client last got in at the wheel.</summary>
	public int LongestReplayTicks { get; private set; }

	/// <summary>Client: the driven vehicle's snaps — the first record after getting in, or one past the ring — since it got in.</summary>
	public int Snaps { get; private set; }

	/// <summary>Client: replays of the driven vehicle since it got in.</summary>
	public int Replays { get; private set; }

	/// <summary>Client: comparisons of the driven vehicle with its record — in sync or replayed — since it got in.</summary>
	public int Comparisons { get; private set; }

	/// <summary>Client: the position error of the last comparison [m].</summary>
	public float LastComparisonErrorMeters { get; private set; }

	/// <summary>Client: the most a replay or a snap has moved the driven hull since it got in [m].</summary>
	public float LargestCorrectionMeters { get; private set; }

	/// <summary>
	/// Client: corrections of the driven hull past <see cref="SimConfig.MaxSmoothedErrorMeters"/>
	/// since it got in — shown at once rather than drawn away: what a driver sees as a snap.
	/// </summary>
	public int CorrectionsShownAtOnce { get; private set; }

	/// <summary>
	/// Client: how far behind the newest <c>ServerCrewVehicles</c> the newest record for
	/// <paramref name="slot"/> is [ticks]; <see cref="float.MaxValue"/> with no vehicle there.
	/// A strategist's client hides one once <see cref="Fog.IsLost"/> says so (§7.3).
	/// </summary>
	public float RecordAgeTicks(int slot) => _roster.AgeTicks(slot);

	/// <summary>Client: <c>ServerCrewVehicles</c> payloads that failed to decode. Anything but 0 is a bug or an attack.</summary>
	public int RejectedPackets { get; private set; }

	/// <summary>The kiosk's line for a kind (§7.1): the authority's own, or as the server last said it. For the HUD.</summary>
	public KioskLine KioskLineOf(byte kind) => kind < _kiosk.Length ? _kiosk[kind] : KioskLine.None;

	/// <summary>Client: the tick of the newest record for <paramref name="slot"/>; 0 with none.</summary>
	public uint NewestRecordTick(int slot) =>
		slot >= 0 && slot < _interpolators.Length && _interpolators[slot] is { Count: > 0 } buffer ? buffer.NewestTick : 0;

	/// <summary>Client: the tick every puppet was last drawn at — the render clock, smoothed (§8.1).</summary>
	public float DrawnRenderTick { get; private set; }

	/// <summary>The slot of the vehicle this client predicts, or −1 (§8.2).</summary>
	public int DrivenSlot => _drivenSlot;

	public override void _Ready()
	{
		Instance = this;
		ProcessMode = ProcessModeEnum.Always;

		CrewVehicleCatalog.Load();

		_root = new Node3D { Name = "CrewVehicles" };
		AddChild(_root);
	}

	public override void _ExitTree()
	{
		if (Instance == this)
		{
			Instance = null;
		}
	}

	/// <summary>The local player's view from a seat: built the first time there is one to look out of.</summary>
	public override void _Process(double delta)
	{
		if (Bootstrap.IsDedicatedServer || _view != null || CombatManager.Instance?.Local == null)
		{
			return;
		}

		_view = new CrewSeatView { Name = "CrewSeatView" };
		AddChild(_view);
	}

	// ---- the registry ----------------------------------------------------------------

	public CrewVehicle VehicleAt(int slot) =>
		slot >= 0 && slot < _vehicles.Length && IsInstanceValid(_vehicles[slot]) ? _vehicles[slot] : null;

	/// <summary>
	/// Puts a vehicle of <paramref name="kind"/> on the field at rest, its origin at
	/// <paramref name="origin"/>, facing <paramref name="yaw"/>, for <paramref name="askedBy"/>
	/// (a peer, or 0). Server only. The slot it took, or −1 when every slot is taken or the
	/// kind is unknown.
	/// </summary>
	public int Spawn(byte kind, Vector3 origin, float yaw, int askedBy = 0)
	{
		if (NetworkManager.Instance is { IsClient: true } || CrewVehicleCatalog.Definition(kind) is not { } definition)
		{
			return -1;
		}

		int slot = FreeSlot();
		if (slot < 0)
		{
			return -1;
		}

		CrewVehicle vehicle = CrewVehicle.Create(definition, slot, origin, yaw);
		_vehicles[slot] = vehicle;
		_root.AddChild(vehicle);
		AgentEventBus.Emit(AgentEventKind.VehicleSpawned, NetworkManager.Instance?.Tick ?? 0, slot, kind, askedBy,
			origin.X, origin.Z);
		GD.Print($"[vehicles] {vehicle.KindName} in slot {slot} at ({origin.X:0.0}, {origin.Y:0.0}, {origin.Z:0.0})");
		return slot;
	}

	/// <summary>Takes a vehicle off the field, putting whoever was in it down where they sat.</summary>
	public void Remove(int slot)
	{
		CrewVehicle vehicle = VehicleAt(slot);
		if (vehicle == null)
		{
			return;
		}

		for (int seat = 0; seat < vehicle.Seats.Length; seat++)
		{
			if (vehicle.Occupants[seat] != 0)
			{
				ReleaseInPlace(vehicle.Occupants[seat]);
			}
		}

		_vehicles[slot] = null;
		vehicle.QueueFree();
		GD.Print($"[vehicles] slot {slot} removed");
	}

	/// <summary>Every vehicle off the field, for the next round (§6.6).</summary>
	public void ClearAll()
	{
		for (int slot = 0; slot < _vehicles.Length; slot++)
		{
			Remove(slot);
		}

		_frames.Clear();
		_exitRefusals.Clear();
	}

	private int FreeSlot()
	{
		for (int slot = 0; slot < _vehicles.Length; slot++)
		{
			if (VehicleAt(slot) == null)
			{
				return slot;
			}
		}

		return -1;
	}

	// ---- the tick, driven by PlayerManager -------------------------------------------

	/// <summary>
	/// A driver's frame, as they sent it, kept for their vehicle's step this tick (§4).
	/// Called for every player the authority simulates; only a driver's is kept.
	/// </summary>
	public void NoteInput(int peerId, in InputFrame frame)
	{
		if (_seated.TryGetValue(peerId, out SeatRef seat) && VehicleAt(seat.Slot) is { } vehicle
			&& vehicle.Seats[seat.Seat].Role == SeatRole.Driver)
		{
			_frames[peerId] = frame;
		}
	}

	/// <summary>
	/// One server tick (§4, §6.2): whoever cannot stay seated is put down, every vehicle
	/// steps on its driver's frame — or, with nobody at the controls, on a parked one —
	/// its pose is recorded, and every seated body is pinned to its seat.
	/// </summary>
	public void ServerTick(uint tick)
	{
		ReleaseTheGone();

		for (int slot = 0; slot < _vehicles.Length; slot++)
		{
			CrewVehicle vehicle = VehicleAt(slot);
			if (vehicle == null)
			{
				continue;
			}

			// A wreck burns where it stopped, and is taken away two seconds after (GV-D12).
			// Its loss was counted by the hit that wrecked it; asked again, once, in case
			// anything else ever wrecks one.
			if (vehicle.Destroyed)
			{
				NoteLost(vehicle, vehicle.DestroyedTick);
				if (tick - vehicle.DestroyedTick >= (uint)CrewCombat.WreckBurnTicks)
				{
					Remove(slot);
				}

				continue;
			}

			int driver = vehicle.DriverPeerId;
			InputFrame frame = driver != 0 && _frames.TryGetValue(driver, out InputFrame driven)
				? driven
				: ParkedFrame(vehicle, tick);

			vehicle.Step(frame, piloted: driver != 0, SimConfig.TickDelta);
			vehicle.Poses.Record(tick, vehicle.Pose);

			// The driver's acknowledgement (§7.1): the input tick whose slot this server tick
			// consumed for them, their own frame or a repeat when it was late — the vehicle
			// steps once a slot, so this is the tick its state is the state after. Whichever
			// frame the vehicle was stepped on: on the tick they got in it was a parked one,
			// and their replay starts after it all the same.
			vehicle.DriverInputTick = driver != 0 ? PlayerManager.Instance?.ConsumedInputTickOf(driver) ?? tick : 0;

			// Where it now is, before the units' own moves push them out of it (§6.5).
			RunOver(vehicle, tick);
		}

		_frames.Clear();
		PinSeated();

		// The kiosk last: its pads' check, the vehicles left alone, and each kind's line
		// (§6.6). A loss later this tick, in CombatManager's post-tick, starts its own timer.
		ServerKiosk(tick);
	}

	/// <summary>
	/// Every unit on foot the hull has struck this tick fast enough to kill (GV-D17, §5.9):
	/// tested where the vehicle now is and where the unit still stands, before its own
	/// <c>MoveAndSlide</c> pushes it aside. Within the hull's reach and a metre; at
	/// <see cref="CrewVehicleDefinition.RamKillSpeedMetersPerSecond"/> or more where it
	/// touches, towards the unit. The vehicle's motion is not changed by any of it.
	/// </summary>
	private void RunOver(CrewVehicle vehicle, uint tick)
	{
		UnitManager units = UnitManager.Instance;
		CombatManager combat = CombatManager.Instance;
		if (units == null || combat == null)
		{
			return;
		}

		float killSpeed = vehicle.Definition.RamKillSpeedMetersPerSecond;
		Vector3 velocity = vehicle.SimVelocity;
		float yawRate = vehicle.YawRate;
		float reach = vehicle.HullRadius + 1f;

		// No point of the hull moves faster than its flat speed and what its turn sweeps at
		// the far corner: when that would not kill, nothing need be looked at. Asked of the
		// one rule, so the threshold is in one place.
		float fastest = new Vector2(velocity.X, velocity.Z).Length() + (Mathf.Abs(yawRate) * vehicle.HullRadius);
		if (!Ramming.Kills(fastest, killSpeed, unitIsVehicle: false))
		{
			return;
		}

		CrewPose pose = vehicle.Pose;
		int boxes = vehicle.PlaceHull(pose, _hullScratch);
		for (int i = 0; i < units.SlotCount; i++)
		{
			Unit unit = units.UnitAt(i);
			if (unit == null || !unit.IsOnField || !unit.IsAlive || unit.Team == Team.GroundForce
				|| unit.Definition?.IsVehicle == true)
			{
				continue;
			}

			HitCapsule capsule = unit.Hitbox;
			Vector3 offset = capsule.Center - pose.Position;
			if (new Vector2(offset.X, offset.Z).Length() > reach + capsule.Radius)
			{
				continue;
			}

			float speed = Ramming.ImpactSpeed(_hullScratch.AsSpan(0, boxes), capsule, pose.Position, velocity, yawRate);
			if (!Ramming.Kills(speed, killSpeed, unitIsVehicle: false))
			{
				continue;
			}

			vehicle.RunOver++;
			RunOverTotal++;
			combat.RamUnit(unit, vehicle.Slot, vehicle.RamCredit(tick), speed, tick);
		}
	}

	/// <summary>Units run over by every vehicle since this process started. For the debug HUD.</summary>
	public int RunOverTotal { get; private set; }

	/// <summary>
	/// What a vehicle with nobody at the controls is stepped on (§6.2): the 4x4 on its
	/// handbrake, so it brakes to a stop; the helicopter on nothing, holding its heading,
	/// so its rotor spins down and, in the air, it comes down.
	/// </summary>
	private static InputFrame ParkedFrame(CrewVehicle vehicle, uint tick) =>
		vehicle.Kind == CrewVehicleKind.FourByFour
			? FourByFourSim.ParkedFrame(tick)
			: InputFrame.Neutral(tick, vehicle.Yaw, 0f);

	/// <summary>
	/// Every seated body where its seat is, after the vehicles have moved (§6.3): its
	/// eye at the seat's eye, its velocity the vehicle's, its hitbox the seat's capsule.
	/// Units then lead it, the fog then looks at it and the hitbox ring then records it at
	/// the right place with no code of their own.
	/// </summary>
	private void PinSeated()
	{
		CombatManager combat = CombatManager.Instance;
		foreach (KeyValuePair<int, SeatRef> entry in _seated)
		{
			CrewVehicle vehicle = VehicleAt(entry.Value.Slot);
			fps_controller character = combat?.Find(entry.Key)?.Character;
			if (vehicle == null || character == null)
			{
				continue;
			}

			Pin(character, vehicle, entry.Value.Seat);
		}
	}

	private static void Pin(fps_controller character, CrewVehicle vehicle, int seat)
	{
		CrewPose pose = vehicle.Pose;
		CrewSeat placed = vehicle.Seats[seat];
		character.PinToSeat(pose.ToWorld(placed.Eye), vehicle.SimVelocity, pose.Place(placed.Capsule));
		AimGun(character, vehicle, seat, pose);
	}

	/// <summary>The gun on its mount turned to where its gunner looks, on the hull at <paramref name="pose"/>. Presentation only.</summary>
	private static void AimGun(fps_controller character, CrewVehicle vehicle, int seat, in CrewPose pose)
	{
		if (vehicle.Gun is not { } gun || vehicle.Seats[seat].Role != SeatRole.Gunner)
		{
			return;
		}

		gun.Pose = pose;
		vehicle.PointGun(pose.DirectionToLocal(gun.AimFor(character.Yaw, character.Pitch)));
	}

	/// <summary>
	/// Puts down, where they sat, everybody who can no longer be seated (§6.3): the dead,
	/// whose body falls from the seat; whoever changed side or is choosing one; and
	/// whoever has left.
	/// </summary>
	private void ReleaseTheGone()
	{
		if (_seated.Count == 0)
		{
			return;
		}

		CombatManager combat = CombatManager.Instance;
		_scratch.Clear();
		foreach (KeyValuePair<int, SeatRef> entry in _seated)
		{
			// A seat whose policy has detached is a bot again: a bot never drives (GV-D15),
			// and only an HTN bot rides, at the gun or as a passenger (GV8). A rider whose
			// driver has gone is not put down here: it gets out by the use key, at an exit
			// point, as a person does (§10).
			PlayerCombat player = combat?.Find(entry.Key);
			CrewVehicle vehicle = VehicleAt(entry.Value.Slot);
			if (player?.Character == null || !player.IsAlive || vehicle == null || !CanBeSeated(entry.Key)
				|| (!IsPerson(entry.Key) && !CrewSeats.MayRide(vehicle.Seats[entry.Value.Seat].Role, personDriving: true)))
			{
				_scratch.Add(entry.Key);
			}
		}

		for (int i = 0; i < _scratch.Count; i++)
		{
			ReleaseInPlace(_scratch[i]);
		}
	}

	// ---- seats -----------------------------------------------------------------------

	public bool IsSeated(int peerId) => _seated.ContainsKey(peerId);

	/// <summary>The vehicle and seat <paramref name="peerId"/> is in.</summary>
	public bool TrySeat(int peerId, out CrewVehicle vehicle, out int seat)
	{
		if (_seated.TryGetValue(peerId, out SeatRef found) && VehicleAt(found.Slot) is { } at)
		{
			vehicle = at;
			seat = found.Seat;
			return true;
		}

		vehicle = null;
		seat = -1;
		return false;
	}

	/// <summary>Whether this peer is at the controls of a vehicle.</summary>
	public bool IsDriving(int peerId, out CrewVehicle vehicle)
	{
		if (TrySeat(peerId, out vehicle, out int seat) && vehicle.Seats[seat].Role == SeatRole.Driver)
		{
			return true;
		}

		vehicle = null;
		return false;
	}

	/// <summary>
	/// Whether this peer is a person to the seats: anybody who is not a bot, on whichever
	/// process they play — the vehicles are on the wire, so a remote client sees the hull it
	/// is put in (§7) — or a seat an external policy is driving (§11, GV7), whose frames are
	/// the policy's and not a bot's. A person may sit in any seat, the driver's included. The
	/// rule the strategist's cargo keeps for who may load it (<c>UnitManager.IsSeatedStrategist</c>).
	/// </summary>
	public static bool IsPerson(int peerId) =>
		peerId != 0 && (!BotRoster.IsBot(peerId) || (Gdpyr.Agent.AgentServer.Instance?.IsAttached(peerId) ?? false));

	/// <summary>
	/// Whether this peer is a bot that may ride with a person (docs/GROUND_VEHICLES.md §10,
	/// GV8): a ground bot that plans with the HTN, and no policy on its seat. Only at the gun
	/// or as a passenger, and only while a person is at the controls
	/// (<see cref="CrewSeats.MayRide"/>): bots never drive or fly (GV-D15). The legacy bots
	/// never get in — they are the baseline policies are measured against.
	/// </summary>
	public static bool IsRider(int peerId) =>
		peerId != 0 && BotRoster.IsBot(peerId) && !(Gdpyr.Agent.AgentServer.Instance?.IsAttached(peerId) ?? false)
		&& (PlayerManager.Instance?.Bots?.PilotOf(peerId)?.Plans ?? false);

	/// <summary>
	/// Whether this peer may be put in a seat at all: a person (<see cref="IsPerson"/>), or a
	/// bot that rides with one (<see cref="IsRider"/>). A seat whose policy detaches is a bot
	/// again: put down when it may not ride, or sits at the controls
	/// (<see cref="ReleaseTheGone"/>).
	/// </summary>
	public static bool CanBeSeated(int peerId) => IsPerson(peerId) || IsRider(peerId);

	/// <summary>Whether a person is at <paramref name="vehicle"/>'s controls (GV8): what a bot rides with.</summary>
	public static bool PersonDriving(CrewVehicle vehicle) => vehicle != null && IsPerson(vehicle.DriverPeerId);

	/// <summary>
	/// The seat a bot getting into <paramref name="vehicle"/> would get (GV8): the gunner's,
	/// then the passenger's, while a person is at the controls; −1 for none
	/// (<see cref="CrewSeats.PickRiderSeat"/>).
	/// </summary>
	public static int RiderSeat(CrewVehicle vehicle) =>
		vehicle == null ? -1 : CrewSeats.PickRiderSeat(vehicle.Seats, vehicle.Occupants, PersonDriving(vehicle));

	/// <summary>
	/// The ride on offer to an HTN bot whose feet are at <paramref name="feet"/>
	/// (docs/GROUND_VEHICLES.md §10, GV8): the nearest vehicle whose hull is within
	/// <paramref name="offerMeters"/>, that is stopped, a helicopter on the ground, with a
	/// person at the controls and the gun or the passenger's seat free
	/// (<see cref="CrewSeats.OffersRide"/>). Where to stand to get in is the door of the
	/// seat it would get. −1 with nothing on offer. Server only.
	/// </summary>
	public int NearestRide(Vector3 feet, float offerMeters, out Vector3 door, out float distance)
	{
		door = Vector3.Zero;
		distance = float.MaxValue;
		int best = -1;
		for (int slot = 0; slot < _vehicles.Length; slot++)
		{
			CrewVehicle vehicle = VehicleAt(slot);
			if (vehicle == null || vehicle.IsPuppet)
			{
				continue;
			}

			int seat = RiderSeat(vehicle);
			float d = vehicle.HullDistance(feet);
			if (!CrewSeats.OffersRide(vehicle.Destroyed, vehicle.Speed, vehicle.Kind, vehicle.Grounded, seat, d,
					offerMeters)
				|| d >= distance)
			{
				continue;
			}

			distance = d;
			best = slot;
			door = vehicle.Pose.ToWorld(vehicle.Seats[seat].Door);
		}

		return best;
	}

	/// <summary>
	/// The vehicle a player at <paramref name="feet"/> could get into with a tap of the use
	/// key (§5.3): the nearest whose hull is within <see cref="SimConfig.EmplacementReachMeters"/>,
	/// that is not destroyed, is moving at a walk or less, has a free seat and, for a
	/// helicopter, is on the ground. −1 with <paramref name="distance"/> at
	/// <see cref="float.MaxValue"/> when there is none, or the player may not be seated.
	/// </summary>
	public int NearestEnterable(Vector3 feet, int peerId, out float distance)
	{
		distance = float.MaxValue;
		if (!CanBeSeated(peerId) || IsSeated(peerId))
		{
			return -1;
		}

		// A bot rides only where it has a seat it may take (GV8): the gun or the passenger's,
		// with a person at the controls.
		bool rider = !IsPerson(peerId);
		int best = -1;
		for (int slot = 0; slot < _vehicles.Length; slot++)
		{
			CrewVehicle vehicle = VehicleAt(slot);
			if (vehicle == null || vehicle.Destroyed || !vehicle.HasFreeSeat || !vehicle.Grounded
				|| vehicle.Speed > CrewSeats.MaxEnterSpeedMetersPerSecond || (rider && RiderSeat(vehicle) < 0))
			{
				continue;
			}

			float d = vehicle.HullDistance(feet);
			if (d <= SimConfig.EmplacementReachMeters && d < distance)
			{
				distance = d;
				best = slot;
			}
		}

		return best;
	}

	/// <summary>
	/// Gets a player in (§6.3), at the free seat whose door is nearest, if
	/// <see cref="CrewSeats.MayEnter"/> lets them. Server only; from the use key's
	/// <see cref="UseAction.EnterVehicle"/>.
	/// </summary>
	public bool Enter(PlayerCombat combat, int slot)
	{
		CrewVehicle vehicle = VehicleAt(slot);
		if (vehicle == null || combat?.Character == null || !CanBeSeated(combat.PeerId))
		{
			return false;
		}

		EmplacementManager emplacements = EmplacementManager.Instance;
		var situation = new EnterSituation(
			combat.IsAlive,
			combat.Team == Team.GroundForce,
			emplacements?.CarryOf(combat.PeerId) ?? CarryKind.None,
			emplacements != null && emplacements.TryMounted(combat.PeerId, out _),
			CombatManager.Instance?.IsPiloting(combat.PeerId) ?? false,
			IsSeated(combat.PeerId),
			vehicle.Destroyed,
			vehicle.Speed,
			vehicle.HasFreeSeat,
			vehicle.Kind,
			vehicle.Grounded);

		if (!CrewSeats.MayEnter(situation))
		{
			return false;
		}

		// A person gets the free seat whose door is nearest; a bot the gun, else the
		// passenger's seat, and only with a person at the controls (GV8).
		int seat = IsPerson(combat.PeerId)
			? CrewSeats.PickSeat(vehicle.Seats, vehicle.Occupants, vehicle.Pose, combat.Character.SimPosition)
			: RiderSeat(vehicle);
		if (seat < 0)
		{
			return false;
		}

		SeatAt(combat, vehicle, seat);
		return true;
	}

	/// <summary>
	/// Seats a player in a seat of a vehicle for a scenario (docs/GROUND_VEHICLES.md §11): the
	/// first free seat of <paramref name="role"/>, directly, as V5's riders are placed
	/// (<c>UnitManager.ServerSeatPlaced</c>). Harness-only, through the agent API's
	/// <c>spawn</c>; nothing a person or a policy can ask for, which is the use key's
	/// <see cref="Enter"/>. Null when seated, or why not.
	/// </summary>
	public string ServerSeatPlaced(int peerId, int slot, SeatRole role)
	{
		CrewVehicle vehicle = VehicleAt(slot);
		if (vehicle == null || vehicle.IsPuppet)
		{
			return $"no crew vehicle in slot {slot}";
		}

		if (vehicle.Destroyed)
		{
			return $"the {vehicle.KindName} in slot {slot} is a wreck";
		}

		PlayerCombat combat = CombatManager.Instance?.Find(peerId);
		if (combat?.Character == null)
		{
			return $"seat {peerId} has no character this tick";
		}

		if (!IsPerson(peerId))
		{
			return $"seat {peerId} is a bot with no policy on it: a bot gets in only by the use key, beside a person at the controls (GV8)";
		}

		if (!combat.IsAlive || combat.Team != Team.GroundForce)
		{
			return $"seat {peerId} is not alive on the ground force";
		}

		if (IsSeated(peerId))
		{
			return $"seat {peerId} is seated already";
		}

		bool has = false;
		for (int seat = 0; seat < vehicle.Seats.Length; seat++)
		{
			if (vehicle.Seats[seat].Role != role)
			{
				continue;
			}

			has = true;
			if (vehicle.Occupants[seat] == 0)
			{
				SeatAt(combat, vehicle, seat);
				return null;
			}
		}

		string named = (int)role < AgentJson.SeatRoleNames.Length ? AgentJson.SeatRoleNames[(int)role] : $"#{(int)role}";
		return has
			? $"the {vehicle.KindName}'s {named} seat is taken"
			: $"the {vehicle.KindName} has no {named} seat";
	}

	/// <summary>A player into one seat of a vehicle, on the authority: the seat's, pinned to it, and announced.</summary>
	private void SeatAt(PlayerCombat combat, CrewVehicle vehicle, int seat)
	{
		int slot = vehicle.Slot;
		vehicle.Occupants[seat] = combat.PeerId;
		_seated[combat.PeerId] = new SeatRef(slot, seat);
		_exitRefusals.Remove(combat.PeerId);
		SeatTaken(vehicle, seat);

		combat.Character.SetSeated(true);
		Pin(combat.Character, vehicle, seat);
		AgentEventBus.Emit(AgentEventKind.VehicleEntered, NetworkManager.Instance?.Tick ?? 0, combat.PeerId, slot,
			(int)vehicle.Seats[seat].Role);

		GD.Print($"[vehicles] peer {combat.PeerId} in the {vehicle.KindName}'s {SeatName(vehicle, seat)} (slot {slot})");
	}

	/// <summary>
	/// After a seat changed hands on the authority: who is at the controls, for the
	/// credit of a unit run over (§5.9); and a new gunner's box counted from their first
	/// round, as their client counts it (<see cref="CrewGun.NewGunner"/>).
	/// </summary>
	private static void SeatTaken(CrewVehicle vehicle, int seat)
	{
		vehicle.NoteDriver(vehicle.DriverPeerId, NetworkManager.Instance?.Tick ?? 0);
		if (seat >= 0 && seat < vehicle.Seats.Length && vehicle.Seats[seat].Role == SeatRole.Gunner)
		{
			vehicle.Gun?.NewGunner();
		}
	}

	/// <summary>A hold of the use key (§5.3): to the next free seat in seat order, if there is one.</summary>
	public bool ChangeSeat(int peerId)
	{
		// A bot stays in the seat it got (GV8): the next free one could be the controls.
		if (!IsPerson(peerId) || !TrySeat(peerId, out CrewVehicle vehicle, out int seat))
		{
			return false;
		}

		int next = CrewSeats.NextSeat(vehicle.Seats.Length, vehicle.Occupants, seat);
		if (next < 0)
		{
			return false;
		}

		vehicle.Occupants[seat] = 0;
		vehicle.Occupants[next] = peerId;
		_seated[peerId] = new SeatRef(vehicle.Slot, next);
		_frames.Remove(peerId);
		SeatTaken(vehicle, next);

		if (CombatManager.Instance?.Find(peerId)?.Character is { } character)
		{
			Pin(character, vehicle, next);
		}

		GD.Print($"[vehicles] peer {peerId} to the {vehicle.KindName}'s {SeatName(vehicle, next)}");
		return true;
	}

	/// <summary>
	/// A tap of the use key in a seat (§6.3): out at the first exit point that is in the
	/// field, reachable from the hull, on ground within 4 m of the vehicle's and clear of
	/// everything solid but this hull — this seat's own, then the other seats', then the
	/// ring round the vehicle. Out of a helicopter only within 4 m of the ground. With
	/// nowhere clear, or too high, the player stays seated and the HUD says why.
	/// </summary>
	public bool Exit(int peerId, uint tick)
	{
		if (!TrySeat(peerId, out CrewVehicle vehicle, out int seat))
		{
			return false;
		}

		float height = vehicle.Kind == CrewVehicleKind.Helicopter ? vehicle.HeightAboveGround : 0f;
		if (!CrewSeats.MayExit(vehicle.Kind, float.IsFinite(height) ? height : float.MaxValue))
		{
			_exitRefusals[peerId] = new ExitRefusal(tick, tooHigh: true);
			return false;
		}

		if (FindExit(vehicle, seat, out Vector3 feet, out int ordinal))
		{
			Release(peerId, feet, Vector3.Zero);
			GD.Print($"[vehicles] peer {peerId} out of the {vehicle.KindName} at ({feet.X:0.0}, {feet.Y:0.0}, {feet.Z:0.0})"
				+ $" (candidate {ordinal})");
			return true;
		}

		_exitRefusals[peerId] = new ExitRefusal(tick, tooHigh: false);
		return false;
	}

	/// <summary>
	/// The first place the occupant of <paramref name="seat"/> can be put down (§6.3):
	/// this seat's exit point, the other seats', then the ring — the first that passes
	/// VEHICLES.md §5.4's checks. False with nowhere clear.
	/// </summary>
	private bool FindExit(CrewVehicle vehicle, int seat, out Vector3 feet, out int ordinal)
	{
		CrewPose pose = vehicle.Pose;
		Vector3 vehicleGround = vehicle.GroundBelow();
		int candidates = CrewSeats.ExitCandidateCount(vehicle.Seats.Length, vehicle.FlatRadius, PlayerRadiusMeters);
		for (ordinal = 0; ordinal < candidates; ordinal++)
		{
			Vector3 candidate = CrewSeats.ExitCandidate(vehicle.Seats, seat, ordinal, pose, vehicle.FlatRadius,
				PlayerRadiusMeters);
			if (IsExitClear(vehicle, vehicleGround, candidate, out feet))
			{
				return true;
			}
		}

		feet = pose.Position;
		ordinal = -1;
		return false;
	}

	/// <summary>
	/// Everybody out of a wreck (GV-D12, §6.5), from <c>CombatManager.ResolveCrewWrecks</c>:
	/// each at the first clear place for their seat, or — when the wreck kills everybody
	/// aboard, a helicopter higher than 4 m, or nowhere is clear for them — where they
	/// sat. Writes who and whether each was put down; how many. The damage is the caller's,
	/// once they are out, so a body killed by it falls from where it was put.
	/// </summary>
	public int EjectFromWreck(int slot, bool killsAll, Span<int> peers, Span<bool> placed)
	{
		CrewVehicle vehicle = VehicleAt(slot);
		if (vehicle == null)
		{
			return 0;
		}

		int count = 0;
		for (int seat = 0; seat < vehicle.Seats.Length && count < peers.Length; seat++)
		{
			int peer = vehicle.Occupants[seat];
			if (peer == 0)
			{
				continue;
			}

			Vector3 feet = Vector3.Zero;
			bool clear = !killsAll && FindExit(vehicle, seat, out feet, out _);
			if (clear)
			{
				Release(peer, feet, Vector3.Zero);
			}
			else
			{
				ReleaseInPlace(peer);
			}

			peers[count] = peer;
			placed[count] = clear;
			count++;
		}

		return count;
	}

	/// <summary>
	/// VEHICLES.md §5.4's checks of an exit point, against the world as the authority has
	/// it: in the field; nothing on the world layer between the hull and the point, a
	/// metre up; ground under it within 4 m of the vehicle's; and a player's capsule there
	/// clear of the world, players, units and every vehicle but this one.
	/// </summary>
	private bool IsExitClear(CrewVehicle vehicle, Vector3 vehicleGround, Vector3 candidate, out Vector3 feet)
	{
		feet = candidate;
		if (!Cargo.InField(candidate))
		{
			return false;
		}

		PhysicsDirectSpaceState3D space = vehicle.GetWorld3D()?.DirectSpaceState;
		if (space == null)
		{
			return true;
		}

		// A metre off the ground under the vehicle, across to a metre over the point: a
		// low wall between them counts, and a truck parked against the perimeter does not
		// put anybody out through it.
		Vector3 origin = vehicle.Pose.Position;
		Vector3 from = new(origin.X, vehicleGround.Y + ExitSightHeightMeters, origin.Z);
		Vector3 to = new(candidate.X, from.Y, candidate.Z);
		if (WorldRay(space, from, to, out _))
		{
			return false;
		}

		if (!WorldRay(space, to, new Vector3(candidate.X, vehicleGround.Y - ExitGroundProbeMeters, candidate.Z), out feet)
			|| Mathf.Abs(feet.Y - vehicleGround.Y) > CapturePad.VerticalToleranceMeters)
		{
			return false;
		}

		_playerCapsule ??= new CapsuleShape3D { Radius = PlayerRadiusMeters, Height = PlayerHeightMeters };
		_shape ??= new PhysicsShapeQueryParameters3D
		{
			CollisionMask = CollisionLayers.World | CollisionLayers.Players | CollisionLayers.Units
				| CollisionLayers.Vehicles,
		};
		_shape.Shape = _playerCapsule;
		_shape.Transform = new Transform3D(Basis.Identity,
			feet + (Vector3.Up * ((PlayerHeightMeters * 0.5f) + ClearanceLiftMeters)));
		_shapeExclude.Clear();
		_shapeExclude.Add(vehicle.GetRid());
		_shape.Exclude = _shapeExclude;

		return space.IntersectShape(_shape, 1).Count == 0;
	}

	/// <summary>A world-layer ray, through the one cached query: true and where, when it hits.</summary>
	private bool WorldRay(PhysicsDirectSpaceState3D space, Vector3 from, Vector3 to, out Vector3 point)
	{
		_ray ??= new PhysicsRayQueryParameters3D { CollisionMask = CollisionLayers.World };
		_ray.From = from;
		_ray.To = to;

		Godot.Collections.Dictionary hit = space.IntersectRay(_ray);
		if (hit.Count == 0)
		{
			point = to;
			return false;
		}

		point = hit["position"].AsVector3();
		return true;
	}

	/// <summary>Out of the seat where the body is now: death, a change of side, leaving, a vehicle removed (§6.3).</summary>
	private void ReleaseInPlace(int peerId)
	{
		fps_controller character = CombatManager.Instance?.Find(peerId)?.Character;
		Release(peerId, IsInstanceValid(character) ? character.SimPosition : Vector3.Zero, Vector3.Zero);
	}

	private void Release(int peerId, Vector3 feet, Vector3 velocity)
	{
		if (!_seated.Remove(peerId, out SeatRef seat))
		{
			return;
		}

		int role = -1;
		if (VehicleAt(seat.Slot) is { } vehicle && vehicle.Occupants[seat.Seat] == peerId)
		{
			vehicle.Occupants[seat.Seat] = 0;
			role = (int)vehicle.Seats[seat.Seat].Role;
			vehicle.NoteDriver(vehicle.DriverPeerId, NetworkManager.Instance?.Tick ?? 0);
		}

		_frames.Remove(peerId);

		fps_controller character = CombatManager.Instance?.Find(peerId)?.Character;
		if (IsInstanceValid(character))
		{
			character.LeaveSeat(feet, velocity);
		}

		AgentEventBus.Emit(AgentEventKind.VehicleExited, NetworkManager.Instance?.Tick ?? 0, peerId, seat.Slot, role,
			feet.X, feet.Z);
	}

	// ---- combat (§6.4, §6.5), asked for by CombatManager ------------------------------

	/// <summary>
	/// The first hull a round's segment reaches nearer than <paramref name="nearest"/>, a
	/// fraction along it (§6.5): every vehicle on the field but a wreck, and but the one
	/// the round's owner is sitting in — a round fired from a seat never hits that vehicle
	/// (GV-D6). Asked after the players, units and structures, against the same fraction,
	/// so whichever the round reaches first wins. Its hull is where the authority has it
	/// now, as a unit's capsule is (§15: no rewound hits against vehicles).
	/// </summary>
	public CrewVehicle QuerySegment(Vector3 from, Vector3 to, float sweepRadius, int ownerId, ref float nearest,
		out Vector3 point)
	{
		point = to;
		int own = OwnSlotOf(ownerId);
		CrewVehicle hit = null;
		for (int slot = 0; slot < _vehicles.Length; slot++)
		{
			CrewVehicle vehicle = VehicleAt(slot);
			if (vehicle == null || vehicle.Destroyed || slot == own)
			{
				continue;
			}

			int boxes = vehicle.PlaceHull(vehicle.Pose, _hullScratch);
			if (CrewCombat.FirstHit(_hullScratch.AsSpan(0, boxes), from, to, sweepRadius, ref nearest, out _))
			{
				hit = vehicle;
				point = from + ((to - from) * nearest);
			}
		}

		return hit;
	}

	/// <summary>How far a blast at <paramref name="point"/> is from the face of a vehicle's hull (§6.5).</summary>
	public float BlastDistance(CrewVehicle vehicle, Vector3 point)
	{
		int boxes = vehicle.PlaceHull(vehicle.Pose, _hullScratch);
		return CrewCombat.BlastDistance(_hullScratch.AsSpan(0, boxes), point);
	}

	/// <summary>The slot of the vehicle a round's owner is sitting in, or −1: only a seated player's rounds have one.</summary>
	private int OwnSlotOf(int ownerId) =>
		OwnerId.IsPeer(ownerId) && _seated.TryGetValue(OwnerId.PeerOf(ownerId), out SeatRef own) ? own.Slot : -1;

	/// <summary>
	/// Whether <paramref name="peerId"/> is in the vehicle a round's owner is sitting in, the
	/// owner themselves included: who a round fired from a seat never hits (GV-D6).
	/// </summary>
	public bool RidesWithOwner(int peerId, int ownerId)
	{
		int own = OwnSlotOf(ownerId);
		return own >= 0 && _seated.TryGetValue(peerId, out SeatRef seat) && seat.Slot == own;
	}

	/// <summary>Whether a blast skips <paramref name="peerId"/> because the hull around their seat takes it (GV-D11).</summary>
	public bool ShieldedFromBlast(int peerId) =>
		TrySeat(peerId, out CrewVehicle vehicle, out int seat) && !CrewCombat.BlastReaches(vehicle.Seats[seat]);

	/// <summary>
	/// Whether any hull on the field is within <paramref name="reach"/> of <paramref name="point"/>:
	/// what a ground bot minds before it sets off a blast beside one of its side's
	/// vehicles (§10). Every crew vehicle is the ground force's.
	/// </summary>
	public bool HullNear(Vector3 point, float reach)
	{
		for (int slot = 0; slot < _vehicles.Length; slot++)
		{
			if (VehicleAt(slot) is { Destroyed: false } vehicle && BlastDistance(vehicle, point) <= reach)
			{
				return true;
			}
		}

		return false;
	}

	/// <summary>
	/// Whether the segment passes through any hull on the field, grown by
	/// <paramref name="sweepRadius"/> (§10) — but the one <paramref name="ownerId"/> sits in,
	/// which a round fired from its seat never hits (GV-D6, GV8).
	/// </summary>
	public bool HullInLine(Vector3 from, Vector3 to, float sweepRadius, int ownerId = OwnerId.None)
	{
		float nearest = 1f + 1e-6f;
		return QuerySegment(from, to, sweepRadius, ownerId, ref nearest, out _) != null;
	}

	/// <summary>
	/// Whether any vehicle's collider stands in <paramref name="footprint"/> between
	/// <paramref name="bottom"/> and <paramref name="top"/>: a structure site with a truck
	/// parked on it does not finish (§6.7). Its collider's own rectangle, turned with it,
	/// against the site's, flat; and their heights overlapping.
	/// </summary>
	public bool Obstructs(in Footprint footprint, float bottom, float top)
	{
		for (int slot = 0; slot < _vehicles.Length; slot++)
		{
			CrewVehicle vehicle = VehicleAt(slot);
			if (vehicle == null)
			{
				continue;
			}

			CrewVehicleDefinition definition = vehicle.Definition;
			var basis = new Basis(Vector3.Up, vehicle.Yaw);
			Vector3 centre = vehicle.Pose.Position + (basis * definition.ColliderCentre);
			Vector3 half = definition.ColliderHalfExtents;
			if (centre.Y + half.Y <= bottom || centre.Y - half.Y >= top)
			{
				continue;
			}

			if (footprint.Overlaps(new Footprint(centre, vehicle.Yaw, half.X, half.Z)))
			{
				return true;
			}
		}

		return false;
	}

	// ---- the console (GV2) -----------------------------------------------------------

	/// <summary>
	/// The console's <c>vehicle &lt;4x4|helicopter&gt;</c> (docs/GROUND_VEHICLES.md §12, GV2):
	/// one of <paramref name="kind"/> on the ground in front of <paramref name="combat"/>,
	/// facing the way they look. Offline or on a listen host, for its own player; a test
	/// stand-in for the kiosk (§6.6), with none of its timers or caps: what it puts down
	/// counts against no kind's cap, and its loss starts no timer.
	/// </summary>
	public bool SpawnInFrontOf(PlayerCombat combat, byte kind, out string message)
	{
		if (NetworkManager.Instance is not { IsClient: false } || !Session.InGame)
		{
			message = "vehicles are put on the field by the server: offline or on a listen host";
			return false;
		}

		if (combat?.Character == null || !combat.IsAlive || IsSeated(combat.PeerId))
		{
			message = "no player on foot to put one in front of";
			return false;
		}

		if (CrewVehicleCatalog.Definition(kind) is not { } definition)
		{
			message = $"no vehicle kind {kind}";
			return false;
		}

		if (FreeSlot() < 0)
		{
			message = $"{SimConfig.MaxCrewVehicles} vehicles are out already: `vehicle clear` takes them away";
			return false;
		}

		fps_controller character = combat.Character;
		float yaw = character.Yaw;
		Vector3[] points = definition.ToColliderPoints();
		float radius = 0f;
		for (int i = 0; i < points.Length; i++)
		{
			radius = Mathf.Max(radius, new Vector2(points[i].X, points[i].Z).Length());
		}

		Vector3 ahead = character.SimPosition + (Aim.Direction(yaw, 0f) * (radius + PlayerRadiusMeters + ConsoleSpawnGapMeters));
		PhysicsDirectSpaceState3D space = character.GetWorld3D()?.DirectSpaceState;
		if (space == null || !WorldRay(space, ahead + (Vector3.Up * 3f), ahead + (Vector3.Down * 20f), out Vector3 ground))
		{
			message = "no ground in front of you";
			return false;
		}

		// The 4x4's origin is its hardpoints' plane, standing on its springs; the helicopter's, its skids.
		Vector3 origin = definition.VehicleKind == CrewVehicleKind.FourByFour
			? ground + (Vector3.Up * definition.ToFourByFourTraits().RideHeightMeters)
			: ground;

		if (IsBlocked(space, definition, origin, yaw))
		{
			message = "no room in front of you";
			return false;
		}

		int slot = Spawn(kind, origin, yaw);
		message = slot < 0 ? "no free slot" : $"{CrewVehicleCatalog.NameOf(kind)} in slot {slot}";
		return slot >= 0;
	}

	/// <summary>How far a scenario's vehicle spawn looks for the ground under it [m]: below the helicopter's 25 m ceiling.</summary>
	private const float PlacedGroundProbeMeters = 60f;

	/// <summary>Above the ground by more than this, a helicopter a scenario puts down is in the air [m] (§5.2's grounded).</summary>
	private const float PlacedAirborneMeters = 0.05f;

	/// <summary>
	/// Puts a crew vehicle on the field for a scenario (docs/GROUND_VEHICLES.md §11), at rest,
	/// facing <paramref name="yaw"/>, with <paramref name="health"/>: the 4x4 standing on its
	/// springs on the ground under <paramref name="at"/> — the first found looking down from a
	/// metre above it — and the helicopter with its skids at <paramref name="at"/>,
	/// hovering with its rotor at full speed when that is off the ground, so a pilot seated
	/// in it holds its height and nobody at the controls brings it down at 4 m/s. Like the
	/// console's, it counts against no kind's cap and its loss starts no timer. The slot, or
	/// −1 with why not in <paramref name="refused"/>.
	/// </summary>
	public int ServerPlaceVehicle(byte kind, Vector3 at, float yaw, float health, out string refused)
	{
		refused = null;
		if (NetworkManager.Instance is { IsClient: true })
		{
			refused = "vehicles are put on the field by the server";
			return -1;
		}

		if (CrewVehicleCatalog.Definition(kind) is not { } definition)
		{
			refused = $"no crew vehicle kind {kind}";
			return -1;
		}

		if (!(health > 0f) || health > definition.MaxHealth)
		{
			refused = $"a {definition.Name}'s health is above 0 and at most {definition.MaxHealth:0}, not {health:0.##}";
			return -1;
		}

		if (FreeSlot() < 0)
		{
			refused = $"{SimConfig.MaxCrewVehicles} crew vehicles are on the field already";
			return -1;
		}

		PhysicsDirectSpaceState3D space = _root?.GetWorld3D()?.DirectSpaceState;
		Vector3 ground = at;
		if (space != null && !WorldRay(space, at + Vector3.Up, at + (Vector3.Down * PlacedGroundProbeMeters), out ground))
		{
			ground = at;
		}

		bool airborne = definition.VehicleKind == CrewVehicleKind.Helicopter && at.Y - ground.Y > PlacedAirborneMeters;
		Vector3 origin = definition.VehicleKind == CrewVehicleKind.Helicopter
			? (airborne ? at : ground)
			: KioskRules.PadOrigin(definition.VehicleKind, ground, definition.ToFourByFourTraits().RideHeightMeters);

		if (space != null && IsBlocked(space, definition, origin, yaw))
		{
			refused = $"something solid is where a {definition.Name} at ({at.X:0.#}, {at.Y:0.#}, {at.Z:0.#}) would stand";
			return -1;
		}

		int slot = Spawn(kind, origin, yaw);
		if (VehicleAt(slot) is not { } vehicle)
		{
			refused = "no free slot";
			return -1;
		}

		vehicle.Health = health;
		if (airborne)
		{
			vehicle.Flight.Grounded = false;
			vehicle.Flight.Rotor = 1f;
		}

		return slot;
	}

	/// <summary>Whether a vehicle's collider at <paramref name="origin"/>, lifted off the floor, would be in anything solid.</summary>
	private static bool IsBlocked(PhysicsDirectSpaceState3D space, CrewVehicleDefinition definition, Vector3 origin,
		float yaw)
	{
		var basis = new Basis(Vector3.Up, yaw);
		var query = new PhysicsShapeQueryParameters3D
		{
			Shape = new BoxShape3D { Size = definition.ColliderHalfExtents * 2f },
			Transform = new Transform3D(basis, origin + (basis * definition.ColliderCentre)
				+ (Vector3.Up * ClearanceLiftMeters)),
			CollisionMask = CollisionLayers.World | CollisionLayers.Players | CollisionLayers.Units
				| CollisionLayers.Vehicles,
		};

		return space.IntersectShape(query, 1).Count > 0;
	}

	// ---- what the HUD asks -----------------------------------------------------------

	/// <summary>
	/// A seat as the HUD names it (§8.5): the driver's seat is the 4x4's "driver" and the
	/// helicopter's "pilot"; the gunner's is the "gun"; a passenger's is the 4x4's "bed"
	/// and the helicopter's "door".
	/// </summary>
	public static string SeatName(CrewVehicle vehicle, int seat)
	{
		bool helicopter = vehicle.Kind == CrewVehicleKind.Helicopter;
		return vehicle.Seats[seat].Role switch
		{
			SeatRole.Driver => helicopter ? "pilot" : "driver",
			SeatRole.Gunner => "gun",
			_ => helicopter ? "door" : "bed",
		};
	}

	/// <summary>
	/// What a tap would get a player at <paramref name="feet"/> into (§8.5):
	/// <c>[E] drive 4x4</c>, <c>[E] 4x4 gun</c>, <c>[E] 4x4 bed</c>, <c>[E] fly helicopter</c>,
	/// <c>[E] helicopter door</c>.
	/// </summary>
	public string EnterPrompt(int slot, Vector3 feet)
	{
		CrewVehicle vehicle = VehicleAt(slot);
		if (vehicle == null)
		{
			return string.Empty;
		}

		int seat = CrewSeats.PickSeat(vehicle.Seats, vehicle.Occupants, vehicle.Pose, feet);
		if (seat < 0)
		{
			return string.Empty;
		}

		return vehicle.Seats[seat].Role == SeatRole.Driver
			? vehicle.Kind == CrewVehicleKind.Helicopter ? "[E] fly helicopter" : $"[E] drive {vehicle.KindName}"
			: $"[E] {vehicle.KindName} {SeatName(vehicle, seat)}";
	}

	/// <summary>
	/// The seated player's two lines (§8.5): <c>4x4 · driver · 412/600 · 14 m/s</c> or
	/// <c>helicopter · pilot · 380/450 · 12 m up</c>, and under it <c>[E] get out · hold [E]
	/// gun</c>, or why the last press did not get them out. Empty for somebody not seated.
	/// </summary>
	public string SeatedPrompt(int peerId, uint tick)
	{
		if (!TrySeat(peerId, out CrewVehicle vehicle, out int seat))
		{
			return string.Empty;
		}

		string line = $"{vehicle.KindName} · {SeatName(vehicle, seat)}";
		if (vehicle.Seats[seat].Role == SeatRole.Gunner && vehicle.Gun != null)
		{
			// The gunner's own box on their client, which steps it; the server's elsewhere.
			line += vehicle.Gun.Box.IsReloading
				? " · reloading"
				: $" · {vehicle.Gun.Box.Ammo}/{vehicle.Gun.Stats.MagazineSize}";
		}

		// A passenger's trigger is dead outside the seat's arc, and the line under the
		// reticle says so (§8.4), measured on the hull as this process draws it.
		if (vehicle.Seats[seat].Role == SeatRole.Passenger
			&& CombatManager.Instance?.Find(peerId)?.Character is { } shooter)
		{
			CrewSeats.RelativeAim(vehicle.DrawnPose, shooter.Yaw, shooter.Pitch, out float yaw, out float pitch);
			if (!CrewSeats.MayFire(vehicle.Seats[seat], yaw, pitch))
			{
				line += " · no shot";
			}
		}

		if (vehicle.Seats[seat].Role == SeatRole.Driver)
		{
			line += $" · {Mathf.CeilToInt(vehicle.Health)}/{Mathf.CeilToInt(vehicle.Definition.MaxHealth)}";
			line += vehicle.Kind == CrewVehicleKind.Helicopter && float.IsFinite(vehicle.HeightAboveGround)
				? $" · {vehicle.HeightAboveGround:0} m up"
				: $" · {vehicle.Speed:0} m/s";
		}

		// Too high is §5.3's rule against a height every process measures for itself, so it
		// is said for as long as it is true, on a client as on the authority. No room is
		// the authority's finding alone, said for two seconds after a press it refused.
		float height = vehicle.Kind == CrewVehicleKind.Helicopter ? vehicle.HeightAboveGround : 0f;
		string help;
		if (!CrewSeats.MayExit(vehicle.Kind, float.IsFinite(height) ? height : float.MaxValue))
		{
			help = "too high to get out";
		}
		else if (_exitRefusals.TryGetValue(peerId, out ExitRefusal refusal) && tick - refusal.Tick < ExitRefusalShownTicks)
		{
			help = refusal.TooHigh ? "too high to get out" : "no room to get out";
		}
		else
		{
			int next = CrewSeats.NextSeat(vehicle.Seats.Length, vehicle.Occupants, seat);
			help = next >= 0 ? $"[E] get out · hold [E] {SeatName(vehicle, next)}" : "[E] get out";
		}

		return $"{line}\n{help}";
	}

	/// <summary>
	/// The debug HUD's row (§8.5): <c>vehicles 2 · 3 aboard · 4x4 out · helicopter 0:42</c>, and for the driver's client
	/// <c>· mispredicted 0.4/s · replay 9 (longest 12) · error 0.8/2.1 cm</c>.
	/// </summary>
	public string DebugLine()
	{
		string line = $"{Count} · {Aboard} aboard · {KioskDebugLine()} · run over {RunOverTotal}";
		if (_drivenSlot < 0)
		{
			return line;
		}

		NetStats stats = PredictionStats;
		return $"{line} · mispredicted {stats.MispredictionsPerSecond:0.0}/s · replay {LastReplayTicks}"
			+ $" (longest {LongestReplayTicks}) · error {stats.MeanErrorMeters * 100f:0.0}/{stats.MaxErrorMeters * 100f:0.0} cm";
	}

	// ---- the wire: the authority (§7.1, §7.3) ----------------------------------------

	/// <summary>
	/// Sends every connected peer <c>ServerCrewVehicles</c> (§7.1): on the player snapshot's
	/// tick and before it, so over a link that keeps order a client knows who sits where
	/// before it reconciles its own record. Everybody but a strategist gets every vehicle in
	/// one encoding; a strategist only those its side's sensors see (§7.3), whose occupants
	/// the player snapshot then carries exactly with them.
	/// </summary>
	public void Replicate(NetworkManager net, int[] peers, DemoRecorder demo = null)
	{
		if (peers.Length == 0 && demo == null)
		{
			return;
		}

		// The kiosk's lines as they stand after this tick's losses (§7.1).
		RefreshKioskLines(net.Tick);

		int count = 0;
		for (int slot = 0; slot < _vehicles.Length; slot++)
		{
			if (VehicleAt(slot) is { } vehicle)
			{
				_records[count++] = vehicle.ToRecord();
			}
		}

		CombatManager combat = CombatManager.Instance;
		VisibilityService fog = combat?.Visibility;
		byte[] everything = null;

		// A demo the authority records is a record of the round, not of one peer's view of
		// it: the payload the ground force is sent, which no fog has thinned (§7.4, as the
		// player snapshot's unfiltered roster, DEMOS.md §4.1).
		if (demo != null)
		{
			everything = CrewVehicleCodec.Encode(net.Tick, _kiosk, _records.AsSpan(0, count));
			demo.CrewVehicles(net.Tick, everything);
		}

		for (int i = 0; i < peers.Length; i++)
		{
			int peerId = peers[i];
			byte[] payload;
			if (fog == null || combat.TeamOf(peerId) != Team.Strategist)
			{
				payload = everything ??= CrewVehicleCodec.Encode(net.Tick, _kiosk, _records.AsSpan(0, count));
			}
			else
			{
				for (int slot = 0; slot < _seenScratch.Length; slot++)
				{
					_seenScratch[slot] = fog.IsVehicleVisible(slot);
				}

				int visible = CrewFog.Filter(Team.Strategist, _records.AsSpan(0, count), _seenScratch, _visibleRecords);
				fog.CountWithheld(count - visible);
				payload = CrewVehicleCodec.Encode(net.Tick, _kiosk, _visibleRecords.AsSpan(0, visible));
			}

			RpcId(peerId, MethodName.ServerCrewVehicles, payload);
			net.Stats.RecordSent(payload.Length);
		}
	}

	/// <summary>Server → clients, unreliable, at <see cref="SimConfig.SnapshotRate"/> (§7.1).</summary>
	[Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false,
		TransferMode = MultiplayerPeer.TransferModeEnum.Unreliable)]
	private void ServerCrewVehicles(byte[] payload)
	{
		NetworkManager net = NetworkManager.Instance;
		if (net == null || !net.IsClient)
		{
			return;
		}

		net.Stats.RecordReceived(payload.Length);

		// Queued, as the player snapshot is: the driver's replay moves a body, which is only
		// valid inside the physics step, and this arrives on the idle frame.
		_pending.Add(payload);
		if (_pending.Count > MaxPendingPackets)
		{
			_pending.RemoveAt(0);
		}
	}

	/// <summary>
	/// The listen host's curtain for a local strategist (§6.7): a vehicle its side does not
	/// see is not drawn. The authority is the state, so this hides rather than filters, as
	/// <c>PlayerManager.ApplyLocalFog</c> does the players.
	/// </summary>
	public void ApplyLocalFog(bool fogged, VisibilityService visibility)
	{
		for (int slot = 0; slot < _vehicles.Length; slot++)
		{
			if (VehicleAt(slot) is { } vehicle)
			{
				vehicle.SetFogHidden(fogged && visibility != null && !visibility.IsVehicleVisible(slot));
			}
		}
	}

	// ---- a client (§8) ---------------------------------------------------------------

	/// <summary>
	/// Every record that arrived since the last tick, applied before the player snapshot
	/// is, so the local player's own record is reconciled — or not — knowing whether they
	/// are seated (§8.3). Puppets are made, remade and freed (§8.1); each record goes to its
	/// slot's interpolator; who sits where is taken from the newest record of every vehicle
	/// still in sight; and the vehicle this client drives is compared with its record and,
	/// when they disagree, replayed (§8.2).
	/// </summary>
	public void ClientApplyPending(NetworkManager net, DemoRecorder demo = null) =>
		ApplyPending(net.Tick, net.LocalPeerId, demo);

	/// <summary>
	/// The records queued since the last tick, on a client or in a demo's playback (§7.4):
	/// what <see cref="ClientApplyPending"/> does, at <paramref name="now"/>, for
	/// <paramref name="localPeer"/> — 0 in playback, which drives nothing and predicts
	/// nothing — each decoded payload written to <paramref name="demo"/> at its own tick.
	/// </summary>
	private void ApplyPending(uint now, int localPeer, DemoRecorder demo)
	{
		for (int p = 0; p < _pending.Count; p++)
		{
			if (!CrewVehicleCodec.TryDecode(_pending[p], _decodedKiosk, _decoded, out uint tick, out int count))
			{
				RejectedPackets++;
				continue;
			}

			// Recorded here rather than where it arrives, as the player snapshot is: this is
			// where its own tick is decoded, and a demo is ordered by the tick a record is about.
			demo?.CrewVehicles(tick, _pending[p]);

			if (tick >= _roster.NewestPacketTick)
			{
				_decodedKiosk.CopyTo(_kiosk, 0);
			}

			for (int slot = 0; slot < _changes.Length; slot++)
			{
				_changes[slot] = PuppetChange.Ignore;
			}

			_roster.Receive(tick, _decoded.AsSpan(0, count), _changes);
			for (int i = 0; i < count; i++)
			{
				ApplyRecord(tick, _decoded[i], _changes[_decoded[i].Slot]);
			}
		}

		_pending.Clear();

		bool fogged = CombatManager.Instance is { Local.Team: Team.Strategist };
		for (int slot = 0; slot < _vehicles.Length; slot++)
		{
			if (_roster.DueToFree(slot, now, fogged))
			{
				FreePuppet(slot);
				_roster.Free(slot);
			}
		}

		ClientSeats(localPeer, now);

		if (_drivenSlot >= 0 && VehicleAt(_drivenSlot) is { } driven && _hasLatest[_drivenSlot])
		{
			ReconcileDriven(driven, _latest[_drivenSlot]);
		}
	}

	private void ApplyRecord(uint tick, in CrewVehicleRecord record, PuppetChange change)
	{
		int slot = record.Slot;
		if (change == PuppetChange.Ignore || CrewVehicleCatalog.Definition(record.KindId) is not { } definition)
		{
			return;
		}

		bool created = false;
		if (change is PuppetChange.Create or PuppetChange.Replace || VehicleAt(slot) == null)
		{
			created = true;
			FreePuppet(slot);
			CrewVehicle puppet = CrewVehicle.Create(definition, slot, record.Position, record.Yaw);
			puppet.IsPuppet = true;
			_vehicles[slot] = puppet;
			_root.AddChild(puppet);
			_interpolators[slot] = new SnapshotInterpolator();
			_hasLatest[slot] = false;
		}
		else if (change == PuppetChange.Restart)
		{
			_interpolators[slot].DropBefore(tick);
		}

		_interpolators[slot].Push(tick, record.Position, record.Yaw, record.Pitch, record.Roll);

		if (!_hasLatest[slot] || tick >= _latestTick[slot])
		{
			_latest[slot] = record;
			_latestTick[slot] = tick;
			_hasLatest[slot] = true;
			VehicleAt(slot).ApplyRecord(record, withMotion: slot != _drivenSlot, keepGun: IsLocalGunner(slot),
				first: created);
		}
	}

	/// <summary>
	/// Client: whether this client is behind <paramref name="slot"/>'s gun, and so steps its
	/// box itself and does not take the server's, which is a round trip old (§6.4,
	/// NETCODE.md §10.2).
	/// </summary>
	private bool IsLocalGunner(int slot)
	{
		int local = NetworkManager.Instance?.LocalPeerId ?? 0;
		return local != 0 && _seated.TryGetValue(local, out SeatRef seat) && seat.Slot == slot
			&& VehicleAt(slot) is { } vehicle && seat.Seat < vehicle.Seats.Length
			&& vehicle.Seats[seat.Seat].Role == SeatRole.Gunner;
	}

	private void FreePuppet(int slot)
	{
		CrewVehicle vehicle = _vehicles[slot];
		_vehicles[slot] = null;
		_interpolators[slot] = null;
		_hasLatest[slot] = false;
		if (slot == _drivenSlot)
		{
			_drivenSlot = -1;
		}

		if (IsInstanceValid(vehicle))
		{
			vehicle.QueueFree();
		}
	}

	/// <summary>
	/// Who sits where, on a client: the newest record of every vehicle it has and has not
	/// lost to the fog (§7.3) — a lost vehicle's last record is old news about its seats,
	/// and a player who got out of it may be in sight on their own. Bodies are put in and
	/// out of their seats as the records say, the local player's among them, and the
	/// vehicle this client drives is the one whose driver's seat names it.
	/// </summary>
	private void ClientSeats(int localPeer, uint now)
	{
		_nextSeated.Clear();
		int driven = -1;
		for (int slot = 0; slot < _vehicles.Length; slot++)
		{
			CrewVehicle vehicle = VehicleAt(slot);
			if (vehicle == null || !_hasLatest[slot] || Fog.IsLost(_roster.AgeTicks(slot)))
			{
				continue;
			}

			for (int seat = 0; seat < vehicle.Seats.Length; seat++)
			{
				int peer = _latest[slot].SeatPeer(seat);
				if (peer == 0)
				{
					continue;
				}

				_nextSeated[peer] = new SeatRef(slot, seat);
				if (peer != 0 && peer == localPeer && vehicle.Seats[seat].Role == SeatRole.Driver && !vehicle.Destroyed)
				{
					driven = slot;
				}
			}
		}

		CombatManager combat = CombatManager.Instance;
		_scratch.Clear();
		foreach (KeyValuePair<int, SeatRef> entry in _seated)
		{
			if (!_nextSeated.TryGetValue(entry.Key, out SeatRef next) || next.Slot != entry.Value.Slot)
			{
				_scratch.Add(entry.Key);
			}
		}

		// Out first: a body that moved from one vehicle to another is put down and picked up.
		for (int i = 0; i < _scratch.Count; i++)
		{
			int peer = _scratch[i];
			SeatRef was = _seated[peer];
			_seated.Remove(peer);
			if (CharacterOf(peer) is not { } character || !IsInstanceValid(character))
			{
				continue;
			}

			// The local player is put at their seat's own exit point as this client draws the
			// vehicle — where the server most often puts them — and the next record of theirs
			// corrects that like any other (§8.2). Anybody else is placed by their record.
			Vector3 feet = character.SimPosition;
			if (peer == localPeer && VehicleAt(was.Slot) is { } from && was.Seat < from.Seats.Length)
			{
				feet = from.DrawnPose.ToWorld(from.Seats[was.Seat].Exit);
			}

			character.LeaveSeat(feet, Vector3.Zero);
		}

		foreach (KeyValuePair<int, SeatRef> entry in _nextSeated)
		{
			bool wasSeated = _seated.TryGetValue(entry.Key, out SeatRef before);
			_seated[entry.Key] = entry.Value;
			if (!wasSeated && CharacterOf(entry.Key) is { } character && IsInstanceValid(character))
			{
				character.SetSeated(true);
			}

			// This client behind the gun now: its box from the last record, its rounds counted
			// from the first, as the server counts a new gunner's (CrewGun.NewGunner).
			if (entry.Key == localPeer && (!wasSeated || before.Seat != entry.Value.Seat)
				&& VehicleAt(entry.Value.Slot) is { } gunned && entry.Value.Seat < gunned.Seats.Length
				&& gunned.Seats[entry.Value.Seat].Role == SeatRole.Gunner)
			{
				gunned.Gun?.NewGunner();
			}
		}

		if (driven == _drivenSlot)
		{
			return;
		}

		if (VehicleAt(_drivenSlot) is { } left)
		{
			// Back to the records: drawn at the render clock from here on.
			left.IsPuppet = true;
			left.VisualOffset = default;
		}

		_drivenSlot = driven;
		if (VehicleAt(driven) is { } drivenNow)
		{
			drivenNow.IsPuppet = false;
			_crewLedger ??= new CrewLedger(PlayerManager.Instance.Prediction);
			_crewLedger.Reset();
			LastReplayTicks = 0;
			LongestReplayTicks = 0;
			Snaps = 0;
			Replays = 0;
			Comparisons = 0;
			LargestCorrectionMeters = 0f;
			CorrectionsShownAtOnce = 0;
			GD.Print($"[vehicles] driving slot {driven}: predicting it from here (tick {now})");
		}
	}

	/// <summary>
	/// The driven vehicle against its newest record (§8.2): the ledger decides; a replay —
	/// or the snap of the first record after getting in — restores the record's state and
	/// re-runs the frames the server has not consumed yet, through the same step, rays and
	/// <c>MoveAndSlide</c> as the prediction; and what moved is drawn away from where the
	/// hull was being drawn rather than jumped.
	/// </summary>
	private void ReconcileDriven(CrewVehicle vehicle, in CrewVehicleRecord record)
	{
		CrewSample server = record.ToSample(record.DriverInputTick);
		ReplayDecision decision = _crewLedger.Decide(record.DriverInputTick, server);

		switch (decision.Action)
		{
			case ReplayAction.Ignore:
				return;

			case ReplayAction.InSync:
				PredictionStats.RecordPredictionError(decision.PositionError);
				Comparisons++;
				LastComparisonErrorMeters = decision.PositionError;
				return;
		}

		if (decision.Action == ReplayAction.Replay)
		{
			Comparisons++;
			LastComparisonErrorMeters = decision.PositionError;
			PredictionStats.RecordPredictionError(decision.PositionError);
			PredictionStats.RecordMisprediction();
			Replays++;
		}
		else
		{
			// The first record after getting in, or one the prediction has no state for: rare
			// after the first, and a reason to look at the clock (§5.7).
			Snaps++;
			GD.Print($"[vehicles] the driven {vehicle.KindName} snapped to the record for tick {record.DriverInputTick}:"
				+ $" newest input {_crewLedger.Inputs.LastRecordedTick}, a prediction for it"
				+ $" {(_crewLedger.TryGetState(record.DriverInputTick, out _) ? "on record" : "not on record")}, replaying"
				+ $" {Math.Max(0, (long)decision.ToTick - decision.FromTick + 1)} ticks");
		}

		CrewPose drawnBefore = vehicle.DrawnPose;
		vehicle.Restore(record);

		PredictionLedger inputs = _crewLedger.Inputs;
		for (uint tick = decision.FromTick; tick <= decision.ToTick && decision.FromTick <= decision.ToTick; tick++)
		{
			if (!inputs.TryGetInput(tick, out InputFrame frame))
			{
				// Never sampled — the clock stalled that tick to let the server catch up.
				continue;
			}

			vehicle.Step(frame, piloted: true, SimConfig.TickDelta);
			_crewLedger.UpdateState(vehicle.Sample(tick));
		}

		LastReplayTicks = _crewLedger.LastReplayLength;
		LongestReplayTicks = Mathf.Max(LongestReplayTicks, LastReplayTicks);

		float moved = drawnBefore.Position.DistanceTo(vehicle.Pose.Position);
		LargestCorrectionMeters = Mathf.Max(LargestCorrectionMeters, moved);
		if (moved > SimConfig.MaxSmoothedErrorMeters)
		{
			// Rare, and what a driver sees as a snap: worth a line in the log.
			CorrectionsShownAtOnce++;
			GD.Print($"[vehicles] the driven {vehicle.KindName} corrected by {moved:0.00} m at once: {decision.Action}"
				+ $" from tick {record.DriverInputTick}, error {decision.PositionError:0.00} m, {decision.VelocityError:0.00} m/s");
		}

		vehicle.VisualOffset = CrewVisualOffset.Absorb(drawnBefore, vehicle.Pose);
	}

	/// <summary>
	/// One predicted tick of the vehicle this client drives (§8.2), on the frame the
	/// character was just simulated on — the frame the server will step it on — and its
	/// state recorded beside that frame. Nothing for a client that is not driving.
	/// </summary>
	public void ClientPredict(uint tick, in InputFrame frame)
	{
		if (_drivenSlot < 0 || VehicleAt(_drivenSlot) is not { } vehicle || _crewLedger == null)
		{
			return;
		}

		vehicle.Step(frame, piloted: true, SimConfig.TickDelta);
		_crewLedger.Record(vehicle.Sample(tick));
	}

	// TODO(GV3, docs/GROUND_VEHICLES.md §12): check on a display, on a dedicated server over a real
	// link: the driven hull's corrections drawn away, the chase camera behind a predicted truck, the
	// passenger's view at the smoothed clock, a strategist's hull appearing and going. Driven headless
	// by scripts/crew-vehicle-checks.sh net through a delaying relay, never looked at.
	/// <summary>
	/// The client's render-clock half (§8.1, §8.3), after the remote characters are placed:
	/// every puppet at <paramref name="renderTick"/> — followed by
	/// <see cref="SmoothRenderClock"/>, so the clock's whole-tick corrections do not jump a
	/// fast vehicle, and its passengers' view, by a tick's travel — hidden from a strategist
	/// once its records stop arriving; then every seated body, the local player's included,
	/// at its seat on the vehicle as this client draws it: predicted for the driver,
	/// interpolated for everybody else.
	/// </summary>
	public void ClientTick(float renderTick)
	{
		PredictionStats.Advance(SimConfig.TickDelta);
		bool fogged = CombatManager.Instance is { Local.Team: Team.Strategist };
		renderTick = _renderClock.Step(renderTick);
		DrawnRenderTick = renderTick;

		for (int slot = 0; slot < _vehicles.Length; slot++)
		{
			CrewVehicle vehicle = VehicleAt(slot);
			if (vehicle == null)
			{
				continue;
			}

			if (vehicle.IsPuppet && _interpolators[slot] is { } interpolator
				&& interpolator.TrySample(renderTick, out Vector3 position, out float yaw, out float pitch, out float roll))
			{
				vehicle.PlaceAt(new CrewPose(position, yaw, pitch, roll));
				interpolator.Prune(renderTick);
			}

			vehicle.SetFogHidden(fogged && Fog.IsLost(_roster.AgeTicks(slot)));
		}

		PinDrawn();
	}

	/// <summary>Every seated body at its seat on the vehicle as it is drawn here, moving at the vehicle's speed.</summary>
	private void PinDrawn()
	{
		foreach (KeyValuePair<int, SeatRef> entry in _seated)
		{
			CrewVehicle vehicle = VehicleAt(entry.Value.Slot);
			fps_controller character = CharacterOf(entry.Key);
			if (vehicle == null || character == null || !IsInstanceValid(character) || entry.Value.Seat >= vehicle.Seats.Length)
			{
				continue;
			}

			CrewPose drawn = vehicle.DrawnPose;
			CrewSeat seat = vehicle.Seats[entry.Value.Seat];
			character.PinToSeat(drawn.ToWorld(seat.Eye), vehicle.SimVelocity, drawn.Place(seat.Capsule));
			AimGun(character, vehicle, entry.Value.Seat, drawn);
		}
	}

	/// <summary>
	/// Client: everything forgotten, for a new connection — whose server's ticks do not
	/// continue the last one's and whose vehicles are its own.
	/// </summary>
	public void ResetClient()
	{
		foreach (KeyValuePair<int, SeatRef> entry in _seated)
		{
			if (CharacterOf(entry.Key) is { } character && IsInstanceValid(character))
			{
				character.LeaveSeat(character.SimPosition, Vector3.Zero);
			}
		}

		for (int slot = 0; slot < _vehicles.Length; slot++)
		{
			FreePuppet(slot);
		}

		_roster.Clear();
		_pending.Clear();
		_seated.Clear();
		Array.Fill(_kiosk, KioskLine.None);
		_renderClock.Reset();
		_drivenSlot = -1;
		_crewLedger?.Reset();
	}

	// ---- a demo's playback (§7.4) --------------------------------------------------------

	/// <summary>
	/// A body by its peer: the round's, or — in a demo's playback, where nobody has combat
	/// state — the one the demo spawned.
	/// </summary>
	private static fps_controller CharacterOf(int peerId) =>
		CombatManager.Instance?.Find(peerId)?.Character ?? PlayerManager.Instance?.CharacterOf(peerId);

	/// <summary>
	/// A recorded <c>ServerCrewVehicles</c> payload from a demo (§7.4), queued as a received
	/// one is and applied by <see cref="PlaybackTick"/>, through the client's own path.
	/// </summary>
	public void ApplyRecorded(byte[] payload)
	{
		if (payload == null)
		{
			return;
		}

		_pending.Add(payload);
		if (_pending.Count > MaxPendingPackets)
		{
			_pending.RemoveAt(0);
		}
	}

	/// <summary>
	/// One frame of a demo's playback (§7.4): the recorded payloads applied as a client
	/// applies them — puppets made and freed, who sits where — at the read head's tick, then
	/// every puppet at <paramref name="renderTick"/> and every seated body on its seat, as
	/// <see cref="ClientTick"/> places them. Nobody drives: nothing is predicted.
	/// </summary>
	public void PlaybackTick(uint tick, float renderTick)
	{
		ApplyPending(tick, localPeer: 0, demo: null);
		ClientTick(renderTick);
	}

	/// <summary>Every vehicle and puppet off the field, and nobody seated, for a demo that brings its own (§7.4).</summary>
	public void ClearForPlayback()
	{
		ClearAll();
		ResetClient();
	}
}
