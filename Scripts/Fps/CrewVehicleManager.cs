using System.Collections.Generic;
using Gdpyr.Core;
using Gdpyr.Match;
using Gdpyr.Net;
using Gdpyr.Sim;
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
/// <para><b>On the authority only, until GV3.</b> Nothing about a vehicle is on the wire
/// yet (§7): a client neither draws one nor knows who is in it. So a vehicle is driven
/// offline or by a listen host's own player, and nobody else can be seated
/// (<see cref="CanBeSeated"/>) — a remote client seated by a server it cannot see the
/// truck of would be corrected onto an invisible seat on every snapshot. Bots never
/// drive or ride (GV-D15).</para>
///
/// Like the other managers it never ticks itself: <see cref="PlayerManager"/> drives
/// it, after the players are simulated and before the units are (§4).
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

	public static CrewVehicleManager Instance { get; private set; }

	private readonly CrewVehicle[] _vehicles = new CrewVehicle[SimConfig.MaxCrewVehicles];
	private readonly Dictionary<int, SeatRef> _seated = new();
	private readonly Dictionary<int, InputFrame> _frames = new();
	private readonly Dictionary<int, ExitRefusal> _exitRefusals = new();
	private readonly List<int> _scratch = new();

	private Node3D _root;
	private CrewSeatView _view;

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
	/// <paramref name="origin"/>, facing <paramref name="yaw"/>. Server only. The slot it
	/// took, or −1 when every slot is taken or the kind is unknown.
	/// </summary>
	public int Spawn(byte kind, Vector3 origin, float yaw)
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
			if (vehicle == null || vehicle.Destroyed)
			{
				continue;
			}

			int driver = vehicle.DriverPeerId;
			InputFrame frame = driver != 0 && _frames.TryGetValue(driver, out InputFrame driven)
				? driven
				: ParkedFrame(vehicle, tick);

			vehicle.Step(frame, piloted: driver != 0, SimConfig.TickDelta);
			vehicle.Poses.Record(tick, vehicle.Pose);
		}

		_frames.Clear();
		PinSeated();
	}

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
			PlayerCombat player = combat?.Find(entry.Key);
			if (player?.Character == null || !player.IsAlive || VehicleAt(entry.Value.Slot) == null)
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
	/// Whether this peer may be put in a seat at all. Until the vehicles are on the wire
	/// (GV3) only a player whose own process is the authority can see one: offline, or a
	/// listen host's own player. Bots never ride (GV-D15).
	/// </summary>
	public static bool CanBeSeated(int peerId) =>
		NetworkManager.Instance is { IsClient: false } net && net.HasLocalPlayer && peerId == net.LocalPeerId;

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

		int best = -1;
		for (int slot = 0; slot < _vehicles.Length; slot++)
		{
			CrewVehicle vehicle = VehicleAt(slot);
			if (vehicle == null || vehicle.Destroyed || !vehicle.HasFreeSeat || !vehicle.Grounded
				|| vehicle.Speed > CrewSeats.MaxEnterSpeedMetersPerSecond)
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

		int seat = CrewSeats.PickSeat(vehicle.Seats, vehicle.Occupants, vehicle.Pose, combat.Character.SimPosition);
		if (seat < 0)
		{
			return false;
		}

		vehicle.Occupants[seat] = combat.PeerId;
		_seated[combat.PeerId] = new SeatRef(slot, seat);
		_exitRefusals.Remove(combat.PeerId);

		combat.Character.SetSeated(true);
		Pin(combat.Character, vehicle, seat);

		GD.Print($"[vehicles] peer {combat.PeerId} in the {vehicle.KindName}'s {SeatName(vehicle, seat)} (slot {slot})");
		return true;
	}

	/// <summary>A hold of the use key (§5.3): to the next free seat in seat order, if there is one.</summary>
	public bool ChangeSeat(int peerId)
	{
		if (!TrySeat(peerId, out CrewVehicle vehicle, out int seat))
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

		CrewPose pose = vehicle.Pose;
		Vector3 vehicleGround = vehicle.GroundBelow();
		int candidates = CrewSeats.ExitCandidateCount(vehicle.Seats.Length, vehicle.FlatRadius, PlayerRadiusMeters);
		for (int ordinal = 0; ordinal < candidates; ordinal++)
		{
			Vector3 candidate = CrewSeats.ExitCandidate(vehicle.Seats, seat, ordinal, pose, vehicle.FlatRadius,
				PlayerRadiusMeters);
			if (!IsExitClear(vehicle, vehicleGround, candidate, out Vector3 feet))
			{
				continue;
			}

			Release(peerId, feet, Vector3.Zero);
			GD.Print($"[vehicles] peer {peerId} out of the {vehicle.KindName} at ({feet.X:0.0}, {feet.Y:0.0}, {feet.Z:0.0})"
				+ $" (candidate {ordinal})");
			return true;
		}

		_exitRefusals[peerId] = new ExitRefusal(tick, tooHigh: false);
		return false;
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

		if (VehicleAt(seat.Slot) is { } vehicle && vehicle.Occupants[seat.Seat] == peerId)
		{
			vehicle.Occupants[seat.Seat] = 0;
		}

		_frames.Remove(peerId);

		fps_controller character = CombatManager.Instance?.Find(peerId)?.Character;
		if (IsInstanceValid(character))
		{
			character.LeaveSeat(feet, velocity);
		}
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
	/// stand-in for the kiosk (GV6), with none of its timers or caps.
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
		if (vehicle.Seats[seat].Role == SeatRole.Driver)
		{
			line += $" · {Mathf.CeilToInt(vehicle.Health)}/{Mathf.CeilToInt(vehicle.Definition.MaxHealth)}";
			line += vehicle.Kind == CrewVehicleKind.Helicopter && float.IsFinite(vehicle.HeightAboveGround)
				? $" · {vehicle.HeightAboveGround:0} m up"
				: $" · {vehicle.Speed:0} m/s";
		}

		string help;
		if (_exitRefusals.TryGetValue(peerId, out ExitRefusal refusal) && tick - refusal.Tick < ExitRefusalShownTicks)
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

	/// <summary>The debug HUD's row (§8.5): <c>vehicles 2 · 3 aboard</c>, what GV2 has of it.</summary>
	public string DebugLine() => $"{Count} · {Aboard} aboard";
}
