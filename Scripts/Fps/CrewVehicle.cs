using System;
using Gdpyr.Core;
using Gdpyr.Sim;
using Godot;

namespace Gdpyr.Fps;

// TODO(GV2, docs/GROUND_VEHICLES.md §12): check on a display: the greybox hull, the wheels on
// their springs over the test strip, the chassis's pitch and roll, the rotors. Never looked at.
/// <summary>
/// One crew vehicle on the field — a 4x4 or a helicopter the ground force drives
/// (docs/GROUND_VEHICLES.md §6.1): the engine half of <see cref="FourByFourSim"/> and
/// <see cref="HelicopterSim"/>.
///
/// A <see cref="CharacterBody3D"/> in <see cref="CharacterBody3D.MotionModeEnum.Floating"/>,
/// on the <see cref="CollisionLayers.Vehicles"/> layer, meeting the world and other
/// vehicles and nothing else (GV-D8). It casts its own rays — four wheel rays for the
/// 4x4, one down for the helicopter — hands what they found to the pure step, moves
/// itself by the velocity the step returns and gives the step back where it went. The
/// body stays upright and turns only with the heading (GV-D3): the sprung chassis's
/// pitch and roll, or the helicopter's tilt, are drawn on the mesh and applied to the
/// seats and the hull's boxes through <see cref="Pose"/>. The 4x4's collider shape takes
/// the chassis's pitch and roll on the upright body (GV-D3, LE-D24), so that on a slope its
/// floor lies along the ground rather than level into it.
///
/// It never ticks itself: <see cref="CrewVehicleManager"/> steps it, on the server's
/// tick, with its driver's frame or a parked one. Its presentation — greybox boxes in
/// the ground force's colour, wheels placed by their springs, the gun on its mount,
/// rotor discs — is built only off a dedicated server, as the dropship's is.
///
/// <para>On a client (§8) it is one of two things. A <see cref="IsPuppet">puppet</see>,
/// placed each tick where the records put it at the render clock and never stepped: its
/// wheels are put on the ground by rays cast here, for the eye alone. Or the vehicle this
/// client drives, stepped by the same <see cref="Step"/> over the driver's own frames and
/// replayed from a record when the server disagrees; a correction is drawn away by
/// <see cref="VisualOffset"/> rather than jumped.</para>
/// </summary>
public partial class CrewVehicle : CharacterBody3D
{
	/// <summary>How far above its skids the helicopter's ray down starts, so it never starts under the floor [m].</summary>
	private const float HeightProbeLiftMeters = 0.5f;

	/// <summary>How far down the helicopter looks for the ground [m]: past its ceiling and the field's floor.</summary>
	private const float HeightProbeMeters = CrewField.CeilingMeters - CrewField.FloorMeters;

	/// <summary>How fast a rotor turns at full speed [rad/s]. Cosmetic.</summary>
	private const float RotorRadiansPerSecond = 28f;

	/// <summary>A wheel's width, drawn [m].</summary>
	private const float WheelWidthMeters = 0.3f;

	private readonly WheelContact[] _wheels = new WheelContact[FourByFourSim.Wheels];
	private CollisionShape3D _collider;
	private readonly Godot.Collections.Array<Rid> _exclude = new();
	private PhysicsRayQueryParameters3D _ray;

	private Node3D _visual;
	private Node3D _presentation;

	/// <summary>Each wheel's mount on the upright body: placed on its spring and turned by its steering.</summary>
	private readonly Node3D[] _wheelNodes = new Node3D[FourByFourSim.Wheels];

	/// <summary>The model's running gear, by wheel (null with the greybox): see <see cref="WheelRig"/>.</summary>
	private WheelRig[] _rigs;

	/// <summary>How far round each wheel has rolled [rad], about its axle: drawn only.</summary>
	private readonly float[] _wheelSpin = new float[FourByFourSim.Wheels];

	private Node3D _rotor;
	private Node3D _tailRotor;
	private Node3D _gun;
	private Node3D _gunPitch;
	private float _rotorAngle;

	/// <summary>Which of <see cref="SimConfig.MaxCrewVehicles"/> it is: what the vehicle record names on the wire (§7.1).</summary>
	public int Slot { get; private set; }

	public CrewVehicleDefinition Definition { get; private set; }

	public CrewVehicleKind Kind { get; private set; }

	/// <summary>Its seats, in seat order (§5.3).</summary>
	public CrewSeat[] Seats { get; private set; } = Array.Empty<CrewSeat>();

	/// <summary>Its hull's boxes, in its own frame (§5.4).</summary>
	public HullBox[] Hull { get; private set; } = Array.Empty<HullBox>();

	/// <summary>Who is in each seat: a peer id, or 0 for empty. Beside <see cref="Seats"/>.</summary>
	public readonly int[] Occupants = new int[CrewLayout.MaxSeats];

	/// <summary>The 4x4's state; unused for a helicopter.</summary>
	public FourByFourState Drive;

	/// <summary>The helicopter's state; unused for a 4x4.</summary>
	public HelicopterState Flight;

	public FourByFourTraits DriveTraits { get; private set; }

	public HelicopterTraits FlightTraits { get; private set; }

	public float Health { get; set; }

	public bool Destroyed { get; set; }

	/// <summary>Server: the tick it was destroyed on, which the wreck burns from (GV-D12); 0 while it is not.</summary>
	public uint DestroyedTick { get; set; }

	/// <summary>Its mounted gun (§5.8); null for a kind with none, the helicopter.</summary>
	public CrewGun Gun { get; private set; }

	/// <summary>
	/// Server: whoever was last at the controls, and the tick they left them — 0 while
	/// they still are: who a unit run over is credited to for three seconds after the
	/// seat empties (§5.9).
	/// </summary>
	public int LastDriverPeerId { get; private set; }

	public uint DriverLeftTick { get; private set; }

	/// <summary>Server: units it has run over since it was put on the field (§6.5). For the debug HUD.</summary>
	public int RunOver { get; set; }

	/// <summary>
	/// Server: handed out by the kiosk (§6.6), so it counts against its kind's cap and its
	/// loss starts its kind's timer. False for one the console or a scenario put down.
	/// </summary>
	public bool FromKiosk { get; set; }

	/// <summary>Server: its loss has been counted against the kiosk's timer, once (GV-D13).</summary>
	public bool LossCounted { get; set; }

	/// <summary>Server: how long it has been left alone, checked once a second (GV-D13).</summary>
	public AbandonClock Abandon;

	/// <summary>Its pose over the last half second (§5.6): what a seated shooter's round will leave from.</summary>
	public PoseHistory Poses { get; } = new();

	/// <summary>
	/// Server: the newest input frame of its driver's that the server had consumed when it
	/// last stepped — the driver's acknowledgement the record carries (§7.1). 0 with nobody
	/// at the controls.
	/// </summary>
	public uint DriverInputTick { get; set; }

	/// <summary>
	/// Client: drawn where the records put it and never stepped (§8.1). False on the
	/// authority, and for the vehicle this client drives (§8.2).
	/// </summary>
	public bool IsPuppet { get; set; }

	/// <summary>
	/// Client, the vehicle this client drives: what is left of the last correction, drawn on
	/// the hull and decaying (§8.2). Zero everywhere else.
	/// </summary>
	public CrewVisualOffset VisualOffset;

	/// <summary>Where it is drawn: its pose with what is left of a correction on it.</summary>
	public CrewPose DrawnPose => VisualOffset.Apply(Pose);

	/// <summary>The middle of its hull, where the fog looks for it (§7.3).</summary>
	public Vector3 HullCentre => CrewFog.HullCentre(Hull, Pose);

	/// <summary>What one of the 4x4's wheel rays found on the last tick, in <see cref="FourByFourSim.Wheels"/> order.</summary>
	public WheelContact Wheel(int wheel) => wheel >= 0 && wheel < _wheels.Length ? _wheels[wheel] : WheelContact.Miss;

	/// <summary>The helicopter's skids above the ground under them, from its last ray [m]; NaN when it found none.</summary>
	public float HeightAboveGround { get; private set; } = float.NaN;

	/// <summary>The most its collider reaches from its origin, measured flat [m]: the ring its exit slots stand on.</summary>
	public float FlatRadius { get; private set; }

	/// <summary>Where it is and how it is turned (<see cref="CrewPose"/>): what its seats and hull are placed by.</summary>
	public CrewPose Pose => Kind == CrewVehicleKind.Helicopter ? Flight.Pose : Drive.Pose;

	/// <summary>Its velocity, as the step last left it.</summary>
	public Vector3 SimVelocity => Kind == CrewVehicleKind.Helicopter ? Flight.Velocity : Drive.Velocity;

	/// <summary>How fast it is turning [rad/s], as the step last left it: what sweeps its corners into a unit (§5.9).</summary>
	public float YawRate => Kind == CrewVehicleKind.Helicopter ? Flight.YawRate : Drive.YawRate;

	/// <summary>Its speed, all three axes [m/s]: what getting in is measured against (§5.3).</summary>
	public float Speed => SimVelocity.Length();

	/// <summary>Its heading, in the look convention.</summary>
	public float Yaw => Kind == CrewVehicleKind.Helicopter ? Flight.Yaw : Drive.Yaw;

	/// <summary>On the ground, as getting in and out of a helicopter asks (§5.3); a 4x4 always is, for that.</summary>
	public bool Grounded => Kind != CrewVehicleKind.Helicopter || Flight.Grounded;

	/// <summary>Its kind as the HUD and the console say it: <c>4x4</c>, <c>helicopter</c>.</summary>
	public string KindName => CrewVehicleCatalog.NameOf((byte)Kind);

	/// <summary>
	/// A vehicle of <paramref name="definition"/>'s kind in <paramref name="slot"/>, at rest
	/// with its origin at <paramref name="position"/> — the 4x4's hardpoints' plane, the
	/// helicopter's skids — facing <paramref name="yaw"/>. Not yet in the tree.
	/// </summary>
	public static CrewVehicle Create(CrewVehicleDefinition definition, int slot, Vector3 position, float yaw)
	{
		var vehicle = new CrewVehicle
		{
			Name = $"CrewVehicle{slot}",
			Slot = slot,
			Definition = definition,
			Kind = definition.VehicleKind,
			Seats = definition.ToSeats(),
			Hull = definition.ToHull(),
			DriveTraits = definition.ToFourByFourTraits(),
			FlightTraits = definition.ToHelicopterTraits(),
			Health = definition.MaxHealth,
			Gun = definition.HasGun ? new CrewGun(definition) : null,
		};

		vehicle.Drive = FourByFourState.At(position, yaw);
		vehicle.Flight = HelicopterState.At(position, yaw);
		vehicle.Transform = new Transform3D(new Basis(Vector3.Up, yaw), position);
		return vehicle;
	}

	public override void _Ready()
	{
		MotionMode = MotionModeEnum.Floating;
		CollisionLayer = CollisionLayers.Vehicles;
		CollisionMask = CollisionLayers.VehicleMask;

		Vector3[] points = Definition.ToColliderPoints();
		_collider = new CollisionShape3D
		{
			Name = "Collider",
			Shape = new ConvexPolygonShape3D { Points = points },
		};
		AddChild(_collider);
		TiltCollider();

		FlatRadius = 0f;
		for (int i = 0; i < points.Length; i++)
		{
			FlatRadius = Mathf.Max(FlatRadius, new Vector2(points[i].X, points[i].Z).Length());
		}

		_exclude.Add(GetRid());
		_ray = new PhysicsRayQueryParameters3D { CollisionMask = CollisionLayers.VehicleMask, Exclude = _exclude };

		if (!Bootstrap.IsDedicatedServer)
		{
			BuildPresentation();
		}
	}

	// ---- the step ------------------------------------------------------------------

	/// <summary>
	/// One tick (§6.2): the rays, the pure step over <paramref name="frame"/>, the move and
	/// what the move did. <paramref name="piloted"/> is whether anybody is in the driver's
	/// seat — the helicopter's rotor spins up only then; an empty 4x4 is handed a parked
	/// frame instead (<see cref="FourByFourSim.ParkedFrame"/>).
	/// </summary>
	public void Step(in InputFrame frame, bool piloted, float dt)
	{
		PhysicsDirectSpaceState3D space = GetWorld3D()?.DirectSpaceState;
		if (space == null)
		{
			return;
		}

		if (Kind == CrewVehicleKind.Helicopter)
		{
			HeightAboveGround = CastHeight(space, Flight.Position);
			Vector3 velocity = HelicopterSim.Step(ref Flight, FlightTraits, frame, piloted, HeightAboveGround, dt);
			Move(Flight.Position, Flight.Yaw, velocity, out Vector3 position, out Vector3 moved);
			HelicopterSim.AfterMove(ref Flight, position, moved);
			return;
		}

		FourByFourTraits traits = DriveTraits;
		for (int wheel = 0; wheel < FourByFourSim.Wheels; wheel++)
		{
			_wheels[wheel] = CastWheel(space, FourByFourSim.Hardpoint(Drive, traits, wheel), traits);
		}

		Vector3 driveVelocity = FourByFourSim.Step(ref Drive, traits, frame, _wheels, dt);
		Move(Drive.Position, Drive.Yaw, driveVelocity, out Vector3 drivePosition, out Vector3 driveMoved);
		FourByFourSim.AfterMove(ref Drive, drivePosition, driveMoved);
	}

	/// <summary>
	/// Puts the upright body where the state says, turned to its new heading, and moves
	/// it by <paramref name="velocity"/> through <c>MoveAndSlide</c>: the world and other
	/// vehicles stop it or slide it, and nothing else does (GV-D8). A heading turned into
	/// a wall is pushed back out by the move's own recovery.
	///
	/// What the step is given back is how fast the body actually went
	/// (<see cref="CharacterBody3D.GetRealVelocity"/>), not <see cref="CharacterBody3D.Velocity"/>:
	/// a floating body driven head-on into a wall has its motion stopped and keeps its
	/// velocity, and a truck parked against a wall at 18 m/s is a truck its driver cannot
	/// get into, and that runs units over standing still.
	/// </summary>
	private void Move(Vector3 position, float yaw, Vector3 velocity, out Vector3 after, out Vector3 afterVelocity)
	{
		PlaceBody(position, yaw);
		Velocity = velocity;
		MoveAndSlide();
		after = GlobalPosition;
		afterVelocity = GetRealVelocity();
		Velocity = afterVelocity;
	}

	/// <summary>
	/// The upright body at <paramref name="position"/>, turned to <paramref name="yaw"/>, and
	/// the 4x4's collider on it at the chassis's pitch and roll (<see cref="TiltCollider"/>).
	/// </summary>
	private void PlaceBody(Vector3 position, float yaw)
	{
		GlobalTransform = new Transform3D(new Basis(Vector3.Up, yaw), position);
		TiltCollider();
	}

	/// <summary>
	/// The 4x4's collider shape turned on the upright body by the chassis's pitch and roll,
	/// about the body's origin as the mesh and the hull's boxes are (LE-D24). Level, its floor
	/// has 0.25 m under it and meets any slope past about 7° a few metres from the middle,
	/// which beaches it off its wheels; turned with the chassis, it lies along the ground.
	/// The wheel rays are still cast from the upright body. The helicopter's stays upright.
	/// </summary>
	private void TiltCollider()
	{
		if (_collider == null || Kind != CrewVehicleKind.FourByFour)
		{
			return;
		}

		_collider.Transform = new Transform3D(CrewPose.Rotation(0f, Drive.Pitch, Drive.Roll), Vector3.Zero);
	}

	/// <summary>
	/// One wheel's ray, straight down past its hardpoint on the upright body (§5.1): from
	/// <see cref="FourByFourTraits.RayLiftMeters"/> above it to
	/// <see cref="FourByFourTraits.RayLengthMeters"/> below.
	/// </summary>
	private WheelContact CastWheel(PhysicsDirectSpaceState3D space, Vector3 hardpoint, in FourByFourTraits traits)
	{
		_ray.From = hardpoint + (Vector3.Up * traits.RayLiftMeters);
		_ray.To = hardpoint + (Vector3.Down * traits.RayLengthMeters);
		Godot.Collections.Dictionary hit = space.IntersectRay(_ray);
		return hit.Count == 0
			? WheelContact.Miss
			: WheelContact.At(hardpoint.Y - hit["position"].AsVector3().Y, hit["normal"].AsVector3());
	}

	/// <summary>The helicopter's skids above the ground under them (§5.2); NaN when there is none within reach.</summary>
	private float CastHeight(PhysicsDirectSpaceState3D space, Vector3 skids)
	{
		Vector3 from = skids + (Vector3.Up * HeightProbeLiftMeters);
		_ray.From = from;
		_ray.To = from + (Vector3.Down * HeightProbeMeters);
		Godot.Collections.Dictionary hit = space.IntersectRay(_ray);
		return hit.Count == 0 ? float.NaN : skids.Y - hit["position"].AsVector3().Y;
	}

	/// <summary>
	/// The ground under this vehicle's origin, for the exit checks' "ground within 4 m of
	/// the vehicle's" (§6.3); its origin when there is none.
	/// </summary>
	public Vector3 GroundBelow()
	{
		PhysicsDirectSpaceState3D space = GetWorld3D()?.DirectSpaceState;
		Vector3 origin = Pose.Position;
		if (space == null)
		{
			return origin;
		}

		_ray.From = origin + (Vector3.Up * HeightProbeLiftMeters);
		_ray.To = origin + (Vector3.Down * HeightProbeMeters);
		Godot.Collections.Dictionary hit = space.IntersectRay(_ray);
		return hit.Count == 0 ? origin : hit["position"].AsVector3();
	}

	// ---- the wire (§7.1, §8) ----------------------------------------------------------

	/// <summary>What the driver's prediction of it is compared on (§5.7), at <paramref name="tick"/>.</summary>
	public CrewSample Sample(uint tick) =>
		Kind == CrewVehicleKind.Helicopter ? CrewSample.Of(tick, Flight) : CrewSample.Of(tick, Drive);

	/// <summary>Server: this vehicle as <c>ServerCrewVehicles</c> carries it.</summary>
	public CrewVehicleRecord ToRecord()
	{
		var record = new CrewVehicleRecord
		{
			Slot = (byte)Slot,
			Kind = Kind,
			Destroyed = Destroyed,
			DriverSeated = DriverPeerId != 0,
			Health = CrewVehicleCodec.HealthToU16(Health),
			DriverInputTick = DriverPeerId != 0 ? DriverInputTick : 0,
			GunRounds = Gun == null ? (byte)0 : (byte)Mathf.Clamp(Gun.Box.Ammo, 0, byte.MaxValue),
			GunReloading = Gun != null && Gun.Box.IsReloading,
		};

		if (Kind == CrewVehicleKind.Helicopter)
		{
			record.SetFlight(Flight);
		}
		else
		{
			record.SetDrive(Drive);
		}

		for (int seat = 0; seat < Seats.Length && seat < CrewLayout.MaxSeats; seat++)
		{
			record.SetSeatPeer(seat, Occupants[seat]);
		}

		return record;
	}

	/// <summary>
	/// Client: what a record says that is not where it is — its health, whether it is a
	/// wreck, who sits where, how fast it is going, its steering or rotor, and its gun's
	/// box unless this client is behind the gun and keeps its own (<paramref name="keepGun"/>,
	/// NETCODE.md §10.2). The pose is the interpolator's (<see cref="PlaceAt"/>) or the
	/// prediction's (<see cref="Restore"/>). A record that turns it into a wreck draws the
	/// wreck going up; the first record this client has of one does not (EFFECTS.md §2.3).
	/// </summary>
	public void ApplyRecord(in CrewVehicleRecord record, bool withMotion, bool keepGun = false, bool first = false)
	{
		bool wasDestroyed = Destroyed;
		Health = record.Health;
		Destroyed = record.Destroyed;
		for (int seat = 0; seat < Occupants.Length; seat++)
		{
			Occupants[seat] = seat < Seats.Length ? record.SeatPeer(seat) : 0;
		}

		if (Gun != null && !keepGun)
		{
			Gun.Box.Ammo = record.GunRounds;
		}

		if (Destroyed && !wasDestroyed)
		{
			PresentDestroyed(effect: !first);
		}

		if (!withMotion)
		{
			return;
		}

		if (Kind == CrewVehicleKind.Helicopter)
		{
			Flight.Velocity = record.Velocity;
			Flight.Rotor = record.Rotor;
			Flight.Grounded = !record.Airborne;
		}
		else
		{
			Drive.Velocity = record.Velocity;
			Drive.Steer = record.Steer;
			Drive.AirTicks = record.Airborne ? 1 : 0;
		}
	}

	/// <summary>
	/// Client, the vehicle it drives: the whole state a record restores, which a replay
	/// then steps on from (§8.2). The body is put where the state says.
	/// </summary>
	public void Restore(in CrewVehicleRecord record)
	{
		if (Kind == CrewVehicleKind.Helicopter)
		{
			Flight = record.ToFlight();
		}
		else
		{
			Drive = record.ToDrive();
		}

		CrewPose pose = Pose;
		PlaceBody(pose.Position, pose.Yaw);
		Velocity = SimVelocity;
	}

	/// <summary>
	/// Client, a puppet: placed at <paramref name="pose"/>, the records interpolated at the
	/// render clock (§8.1), its body upright and turned to its heading, and a 4x4's collider
	/// tilted with its chassis, so the local player meets it there. The wheels are put on
	/// the ground by rays cast here — or the helicopter's height measured — for the eye and
	/// the HUD alone.
	/// </summary>
	public void PlaceAt(in CrewPose pose)
	{
		if (Kind == CrewVehicleKind.Helicopter)
		{
			Flight.Position = pose.Position;
			Flight.Yaw = pose.Yaw;
			Flight.Pitch = pose.Pitch;
			Flight.Roll = pose.Roll;
		}
		else
		{
			Drive.Position = pose.Position;
			Drive.Yaw = pose.Yaw;
			Drive.Pitch = pose.Pitch;
			Drive.Roll = pose.Roll;
		}

		PlaceBody(pose.Position, pose.Yaw);

		PhysicsDirectSpaceState3D space = GetWorld3D()?.DirectSpaceState;
		if (space == null || _ray == null)
		{
			return;
		}

		if (Kind == CrewVehicleKind.Helicopter)
		{
			HeightAboveGround = CastHeight(space, pose.Position);
			return;
		}

		FourByFourTraits traits = DriveTraits;
		for (int wheel = 0; wheel < FourByFourSim.Wheels; wheel++)
		{
			_wheels[wheel] = CastWheel(space, FourByFourSim.Hardpoint(Drive, traits, wheel), traits);
		}
	}

	/// <summary>
	/// The gun drawn pointing along <paramref name="local"/>, a direction in the hull's own
	/// frame: where its gunner's look puts it (§6.4). The model's pintle head and shield turn
	/// with the yaw and its gun elevates in them; the greybox's one mount does both.
	/// Presentation only.
	/// </summary>
	public void PointGun(Vector3 local)
	{
		if (_gun == null || !(local.LengthSquared() > 1e-8f))
		{
			return;
		}

		Aim.Angles(local, out float yaw, out float pitch);
		if (_gunPitch != null)
		{
			_gun.Rotation = new Vector3(0f, yaw, 0f);
			_gunPitch.Rotation = new Vector3(pitch, 0f, 0f);
			return;
		}

		_gun.Rotation = new Vector3(pitch, yaw, 0f);
	}

	/// <summary>The gun's pintle head as drawn, which turns with its yaw: null before it is built or on a dedicated server.</summary>
	public Node3D GunYawNode => _gun;

	/// <summary>The model's gun as drawn, which elevates in the pintle head: null for the greybox.</summary>
	public Node3D GunPitchNode => _gunPitch;

	/// <summary>Wheel <paramref name="wheel"/>'s mount as drawn — on its spring, turned by its steering — or null.</summary>
	public Node3D WheelMount(int wheel) => wheel >= 0 && wheel < _wheelNodes.Length ? _wheelNodes[wheel] : null;

	/// <summary>The model's running gear at wheel <paramref name="wheel"/>, or null with the greybox or none drawn.</summary>
	public WheelRig Rig(int wheel) => _rigs != null && wheel >= 0 && wheel < _rigs.Length ? _rigs[wheel] : null;

	/// <summary>The sprung part as drawn — what pitches and rolls with the chassis — or null.</summary>
	public Node3D Chassis => _visual;

	/// <summary>How far round wheel <paramref name="wheel"/> has rolled [rad], as drawn.</summary>
	public float WheelSpin(int wheel) => wheel >= 0 && wheel < _wheelSpin.Length ? _wheelSpin[wheel] : 0f;

	/// <summary>Client: hides it from a strategist the fog no longer tells about it (§7.3). Presentation only; it is still solid.</summary>
	public void SetFogHidden(bool hidden)
	{
		if (_presentation != null)
		{
			_presentation.Visible = !hidden;
		}
	}

	// ---- seats -----------------------------------------------------------------------

	/// <summary>The seat <paramref name="peerId"/> is in, or −1.</summary>
	public int SeatOf(int peerId)
	{
		for (int i = 0; i < Seats.Length; i++)
		{
			if (peerId != 0 && Occupants[i] == peerId)
			{
				return i;
			}
		}

		return -1;
	}

	/// <summary>Whoever is at the controls, or 0.</summary>
	public int DriverPeerId
	{
		get
		{
			for (int i = 0; i < Seats.Length; i++)
			{
				if (Seats[i].Role == SeatRole.Driver)
				{
					return Occupants[i];
				}
			}

			return 0;
		}
	}

	/// <summary>How many seats have somebody in them.</summary>
	public int Aboard => CrewSeats.Occupied(Occupants, Seats.Length);

	/// <summary>
	/// Server: who is at the controls now, after a seat changed (§5.9's credit): somebody
	/// new takes it; nobody, after somebody, starts their three seconds.
	/// </summary>
	public void NoteDriver(int driverPeerId, uint tick)
	{
		if (driverPeerId != 0)
		{
			LastDriverPeerId = driverPeerId;
			DriverLeftTick = 0;
			return;
		}

		if (LastDriverPeerId != 0 && DriverLeftTick == 0)
		{
			DriverLeftTick = tick;
		}
	}

	/// <summary>Whose a unit run over on <paramref name="tick"/> is (<see cref="Ramming.Credit"/>): a peer id, or 0.</summary>
	public int RamCredit(uint tick) => Ramming.Credit(DriverPeerId, LastDriverPeerId, DriverLeftTick, tick);

	/// <summary>Its hull's boxes with the vehicle at <paramref name="pose"/>, into <paramref name="into"/>; how many.</summary>
	public int PlaceHull(in CrewPose pose, Span<HitObb> into)
	{
		int count = Math.Min(Hull.Length, into.Length);
		for (int i = 0; i < count; i++)
		{
			into[i] = Hull[i].Place(pose);
		}

		return count;
	}

	/// <summary>The most any of its hull's boxes reaches from its origin [m]: the ring a unit is looked for in.</summary>
	public float HullRadius
	{
		get
		{
			float radius = 0f;
			for (int i = 0; i < Hull.Length; i++)
			{
				radius = Mathf.Max(radius, Hull[i].Center.Length() + Hull[i].HalfExtents.Length());
			}

			return radius;
		}
	}

	public bool HasFreeSeat => CrewSeats.HasFreeSeat(Occupants, Seats.Length);

	/// <summary>The hull box nearest <paramref name="point"/> and how far it is (§5.3's reach is measured to it).</summary>
	public float HullDistance(Vector3 point)
	{
		CrewPose pose = Pose;
		float best = float.PositiveInfinity;
		for (int i = 0; i < Hull.Length; i++)
		{
			best = Mathf.Min(best, Hitbox.DistanceTo(Hull[i].Place(pose), point));
		}

		return best;
	}

	// ---- presentation ----------------------------------------------------------------

	/// <summary>
	/// Draws the chassis's attitude, the wheels on their springs and the rotors. Render
	/// frame only, and only off a dedicated server; nothing here is read back.
	/// </summary>
	public override void _Process(double delta)
	{
		VisualOffset.Decay((float)delta);
		if (_visual == null)
		{
			return;
		}

		// The correction still being drawn away, on top of the body (§8.2): the body is
		// upright and turned to its heading, so the offset's position goes in its frame.
		_presentation.Position = new Basis(Vector3.Up, -Yaw) * VisualOffset.Position;
		_presentation.Rotation = new Vector3(0f, VisualOffset.Yaw, 0f);

		CrewPose pose = DrawnPose;
		_visual.Basis = CrewPose.Rotation(0f, pose.Pitch, pose.Roll);

		if (Kind == CrewVehicleKind.FourByFour)
		{
			RollWheels((float)delta);
			PlaceWheels(pose.Pitch, pose.Roll);
			return;
		}

		_rotorAngle = Mathf.Wrap(_rotorAngle + (Flight.Rotor * RotorRadiansPerSecond * (float)delta), 0f, Mathf.Tau);
		if (_rotor != null)
		{
			_rotor.Rotation = new Vector3(0f, _rotorAngle, 0f);
		}

		if (_tailRotor != null)
		{
			_tailRotor.Rotation = new Vector3(_rotorAngle * 1.7f, 0f, 0f);
		}
	}

	/// <summary>
	/// Each wheel rolled on by how far its tread went over the ground this frame: the body's
	/// speed along its nose, and what its turning adds on the outside and takes off the
	/// inside. Forward is −Z, so a wheel rolling forward turns about −X. Drawn only.
	/// </summary>
	private void RollWheels(float delta)
	{
		Vector3 forward = Aim.Direction(Drive.Yaw, 0f);
		float along = Drive.Velocity.Dot(forward);
		float radius = DriveTraits.WheelRadiusMeters;
		for (int wheel = 0; wheel < FourByFourSim.Wheels; wheel++)
		{
			float x = FourByFourSim.HardpointLocal(DriveTraits, wheel).X;
			float speed = along + (Drive.YawRate * x);
			if (float.IsFinite(speed))
			{
				_wheelSpin[wheel] = Mathf.Wrap(_wheelSpin[wheel] - (speed / radius * delta), -Mathf.Pi, Mathf.Pi);
			}
		}
	}

	/// <summary>
	/// Each wheel where its spring holds it: slid along its corner's own down — the chassis's,
	/// pitched and rolled, as wishbones guide a wheel — until its tyre stands on the ground its
	/// ray found, or hanging at full length; never further from its corner than the spring's
	/// free length nor nearer than its travel allows. The front pair turned by their steering
	/// (<see cref="FourByFourSim.WheelSteer"/>) — on the model about the kingpin through their
	/// ball joints — and every wheel rolled by <see cref="RollWheels"/>; and on the model, each
	/// wheel's wishbones and coilover put between the chassis and its knuckle.
	/// </summary>
	private void PlaceWheels(float pitch, float roll)
	{
		FourByFourTraits traits = DriveTraits;
		float shortest = traits.SpringRestMeters - traits.SpringTravelMeters;
		Basis chassis = CrewPose.Rotation(0f, pitch, roll);
		Vector3 down = chassis * Vector3.Down;
		float drop = Mathf.Max(-down.Y, 0.1f);
		Transform3D toChassis = new Transform3D(chassis, Vector3.Zero).AffineInverse();
		for (int wheel = 0; wheel < FourByFourSim.Wheels; wheel++)
		{
			if (_wheelNodes[wheel] == null)
			{
				continue;
			}

			// The ray's distance is from the hardpoint on the upright body straight down: the
			// tyre's foot is there, its centre a radius up, reached along the chassis's down.
			Vector3 corner = chassis * FourByFourSim.HardpointLocal(traits, wheel);
			WheelContact contact = _wheels[wheel];
			float spring = contact.Hit
				? Mathf.Clamp((corner.Y + contact.Distance - traits.WheelRadiusMeters) / drop, shortest, traits.SpringRestMeters)
				: traits.SpringRestMeters;
			var hub = new Transform3D(chassis, corner + (down * spring));

			// Positive is to the right, which is a turn about −Y.
			var steer = new Basis(Vector3.Up, -FourByFourSim.WheelSteer(traits, wheel, Drive.Steer));
			if (_rigs?[wheel] is { } rig)
			{
				Vector3 kingpin = rig.Kingpin;
				_wheelNodes[wheel].Transform = hub * new Transform3D(steer, kingpin - (steer * kingpin));
				rig.Pose(_wheelNodes[wheel].Transform, toChassis, _wheelSpin[wheel]);
				continue;
			}

			_wheelNodes[wheel].Transform = hub * new Transform3D(steer, Vector3.Zero);
			if (_wheelNodes[wheel].GetChildCount() > 0 && _wheelNodes[wheel].GetChild(0) is Node3D tyre)
			{
				tyre.Rotation = new Vector3(_wheelSpin[wheel], 0f, tyre.Rotation.Z);
			}
		}
	}

	/// <summary>
	/// One wheel's running gear on the model (docs/GROUND_VEHICLES.md §6.1), from the nodes
	/// <c>tools/blender/four_by_four.py</c> names: the tyre and rim (<c>Wheel</c>), which roll,
	/// and the upright (<c>Knuckle</c>), which does not, both on the wheel's mount; and on the
	/// chassis, the two wishbones, each swung about its inboard pivot until it points at its
	/// ball joint on the upright, and the coilover, pointed from its top mount at its foot on
	/// the lower wishbone and stretched or squeezed to reach it. The rest pose — where the
	/// model was exported, at rest on flat ground — is where each piece is unswung.
	/// </summary>
	public sealed class WheelRig
	{
		public Node3D Wheel { get; init; }

		public Node3D Knuckle { get; init; }

		public Node3D LowerArm { get; init; }

		public Node3D UpperArm { get; init; }

		public Node3D Shock { get; init; }

		/// <summary>+1 for a wheel on the right, −1 on the left: which way along X its wishbones reach.</summary>
		public float Side { get; init; }

		/// <summary>The ball joints, in the knuckle's (the mount's) frame.</summary>
		public Vector3 LowerJoint { get; init; }

		public Vector3 UpperJoint { get; init; }

		/// <summary>The coilover's foot, in the lower wishbone's frame.</summary>
		public Vector3 ShockFoot { get; init; }

		/// <summary>
		/// Where the steering axis crosses the axle's height, in the mount's frame: between the
		/// two ball joints, inboard of the hub, so that a wheel steered turns about its joints
		/// and they stay where the wishbones reach.
		/// </summary>
		public Vector3 Kingpin => new((LowerJoint.X + UpperJoint.X) * 0.5f, 0f, (LowerJoint.Z + UpperJoint.Z) * 0.5f);

		/// <summary>The wheel's centre at rest, on the upright body, as the model was exported.</summary>
		public Vector3 RestHub { get; init; }

		/// <summary>The inboard pivots and the coilover's top mount, on the chassis.</summary>
		public Vector3 LowerPivot { get; init; }

		public Vector3 UpperPivot { get; init; }

		public Vector3 ShockTop { get; init; }

		/// <summary>Each wishbone's angle to its ball joint and its span to it, and the coilover's length, at rest.</summary>
		public float LowerRestAngle { get; init; }

		public float UpperRestAngle { get; init; }

		public float LowerRestLength { get; init; }

		public float UpperRestLength { get; init; }

		public float ShockRestLength { get; init; }

		/// <summary>Where the coilover's foot is now, on the chassis: what its length is measured to.</summary>
		public Vector3 ShockFootNow => LowerArm.Transform * ShockFoot;

		/// <summary>
		/// The wishbones and the coilover for the wheel's mount at <paramref name="mount"/> on
		/// the upright body, the chassis being <paramref name="toChassis"/>'s inverse from it, and
		/// the tyre rolled to <paramref name="spin"/>.
		/// </summary>
		public void Pose(Transform3D mount, Transform3D toChassis, float spin)
		{
			Wheel.Transform = new Transform3D(new Basis(Vector3.Right, spin), Vector3.Zero);

			Swing(LowerArm, LowerPivot, toChassis * (mount * LowerJoint), LowerRestAngle, LowerRestLength);
			Swing(UpperArm, UpperPivot, toChassis * (mount * UpperJoint), UpperRestAngle, UpperRestLength);

			Vector3 along = ShockTop - ShockFootNow;
			float length = along.Length();
			if (!(length > 1e-4f))
			{
				return;
			}

			Vector3 up = along / length;
			Vector3 right = up.Cross(Vector3.Back).Normalized();
			Vector3 back = right.Cross(up);
			Shock.Transform = new Transform3D(new Basis(right, up * (length / ShockRestLength), back), ShockTop);
		}

		/// <summary>
		/// A wishbone swung about its pivot, in the chassis's cross-section, until it points at
		/// <paramref name="joint"/>, and drawn as much longer or shorter in that section as it
		/// takes to reach it: a hub that moves straight along the chassis's down leaves a rigid
		/// arm's arc by up to a tenth of its length at the ends of the travel.
		/// </summary>
		private void Swing(Node3D arm, Vector3 pivot, Vector3 joint, float restAngle, float restLength)
		{
			float angle = Side * (AngleOf(Side, pivot, joint) - restAngle);
			float stretch = SpanOf(pivot, joint) / restLength;
			arm.Transform = new Transform3D(new Basis(Vector3.Back, angle) * Basis.FromScale(new Vector3(stretch, stretch, 1f)),
				pivot);
		}

		/// <summary>The angle up from the horizontal, outward (towards <paramref name="side"/>), from a pivot to a point, in the chassis's cross-section.</summary>
		public static float AngleOf(float side, Vector3 pivot, Vector3 point) =>
			Mathf.Atan2(point.Y - pivot.Y, side * (point.X - pivot.X));

		/// <summary>From a pivot to a point in the chassis's cross-section, fore and aft left out [m].</summary>
		public static float SpanOf(Vector3 pivot, Vector3 point) => Mathf.Max(new Vector2(point.X - pivot.X, point.Y - pivot.Y).Length(), 1e-3f);
	}

	/// <summary>
	/// The 4x4 drawn as its model (<see cref="CrewVehicleDefinition.Model"/>, docs/GROUND_VEHICLES.md
	/// §6.1) in place of the greybox: the body and the gun on the sprung chassis, and for each
	/// wheel a mount on the upright body carrying its tyre and its knuckle, with the wishbones and
	/// the coilover between them and the chassis (<see cref="WheelRig"/>). Every piece is found by
	/// the name <c>tools/blender/four_by_four.py</c> gives it; the rest pose it was exported in
	/// is read off the pieces before they are moved. False, with nothing built, when the model
	/// lacks a piece: the greybox is drawn instead.
	/// </summary>
	private bool BuildModel(PackedScene scene)
	{
		var model = scene.Instantiate<Node3D>();
		Node3D Find(string name) => model.FindChild(name, recursive: true, owned: false) as Node3D;

		var parts = new Node3D[FourByFourSim.Wheels, 5];
		var joints = new Node3D[FourByFourSim.Wheels, 3];
		Node3D bodyNode = Find("Body");
		Node3D gunYaw = Find("GunYaw");
		bool whole = bodyNode != null && gunYaw != null && gunYaw.FindChild("GunPitch", true, false) is Node3D;
		string[] names = { "Wheel", "Knuckle", "LowerArm", "UpperArm", "Shock" };
		for (int wheel = 0; wheel < FourByFourSim.Wheels && whole; wheel++)
		{
			for (int part = 0; part < names.Length; part++)
			{
				parts[wheel, part] = Find($"{names[part]}{wheel}");
				whole &= parts[wheel, part] != null;
			}

			joints[wheel, 0] = whole ? parts[wheel, 1].FindChild($"LowerJoint{wheel}", true, false) as Node3D : null;
			joints[wheel, 1] = whole ? parts[wheel, 1].FindChild($"UpperJoint{wheel}", true, false) as Node3D : null;
			joints[wheel, 2] = whole ? parts[wheel, 2].FindChild($"ShockMount{wheel}", true, false) as Node3D : null;
			whole &= joints[wheel, 0] != null && joints[wheel, 1] != null && joints[wheel, 2] != null;
		}

		if (!whole)
		{
			GD.PushError($"{Definition.ResourcePath}: its model lacks a piece CrewVehicle moves; drawing the greybox");
			model.Free();
			return false;
		}

		// Owned by the model's root, which is freed once its pieces are out of it.
		void Move(Node3D node, Node3D parent)
		{
			node.Owner = null;
			node.GetParent().RemoveChild(node);
			parent.AddChild(node);
		}

		Move(bodyNode, _visual);
		Move(gunYaw, _visual);
		_gun = gunYaw;
		_gunPitch = gunYaw.FindChild("GunPitch", true, false) as Node3D;

		_rigs = new WheelRig[FourByFourSim.Wheels];
		for (int wheel = 0; wheel < FourByFourSim.Wheels; wheel++)
		{
			Node3D tyre = parts[wheel, 0], knuckle = parts[wheel, 1], lower = parts[wheel, 2], upper = parts[wheel, 3],
				shock = parts[wheel, 4];

			// The rest pose, before anything moves: the hub where the knuckle stands, the pivots
			// and the top mount where the arms and the coilover start.
			Vector3 hub = knuckle.Position;
			Vector3 lowerJoint = joints[wheel, 0].Position;
			Vector3 upperJoint = joints[wheel, 1].Position;
			Vector3 foot = joints[wheel, 2].Position;
			float side = hub.X >= 0f ? 1f : -1f;

			var mount = new Node3D { Name = $"Wheel{wheel}Mount" };
			_presentation.AddChild(mount);
			_wheelNodes[wheel] = mount;
			Move(tyre, mount);
			Move(knuckle, mount);
			tyre.Transform = Transform3D.Identity;
			knuckle.Transform = Transform3D.Identity;
			Move(lower, _visual);
			Move(upper, _visual);
			Move(shock, _visual);

			_rigs[wheel] = new WheelRig
			{
				Wheel = tyre,
				Knuckle = knuckle,
				LowerArm = lower,
				UpperArm = upper,
				Shock = shock,
				Side = side,
				LowerJoint = lowerJoint,
				UpperJoint = upperJoint,
				ShockFoot = foot,
				RestHub = hub,
				LowerPivot = lower.Position,
				UpperPivot = upper.Position,
				ShockTop = shock.Position,
				LowerRestAngle = WheelRig.AngleOf(side, lower.Position, hub + lowerJoint),
				UpperRestAngle = WheelRig.AngleOf(side, upper.Position, hub + upperJoint),
				LowerRestLength = WheelRig.SpanOf(lower.Position, hub + lowerJoint),
				UpperRestLength = WheelRig.SpanOf(upper.Position, hub + upperJoint),
				ShockRestLength = Mathf.Max((shock.Position - (lower.Transform * foot)).Length(), 1e-3f),
			};
		}

		model.Free();
		return true;
	}

	// TODO(GV4, docs/GROUND_VEHICLES.md §12): check on a display: the wreck going up and burning, the
	// gun turning with its gunner, tracers leaving the gun and the bed of a moving 4x4, the bed's
	// "no shot". Driven headless by scripts/crew-vehicle-checks.sh combat, never looked at.
	/// <summary>
	/// The wreck (GV-D12, EFFECTS.md §2.3): its hull drawn burnt, and — unless this peer is
	/// only now hearing of a wreck that went up before it was told about it — the
	/// vehicle's explosion at the middle of the hull, as a unit vehicle's wreck is drawn.
	/// Presentation only, and nothing on a peer that draws nothing.
	/// </summary>
	public void PresentDestroyed(bool effect = true)
	{
		if (_visual == null)
		{
			return;
		}

		var burnt = new StandardMaterial3D { AlbedoColor = new Color(0.08f, 0.08f, 0.08f), Roughness = 1f };
		Burn(_presentation, burnt);

		if (!effect || Gdpyr.Effects.ExplosionEffects.Instance is not { } effects || Hull.Length == 0)
		{
			return;
		}

		// From the bottom of the hull's lowest box, as tall as the boxes stand together.
		float bottom = float.MaxValue;
		float top = float.MinValue;
		for (int i = 0; i < Hull.Length; i++)
		{
			bottom = Mathf.Min(bottom, Hull[i].Center.Y - Hull[i].HalfExtents.Y);
			top = Mathf.Max(top, Hull[i].Center.Y + Hull[i].HalfExtents.Y);
		}

		Vector3 centre = CrewLayout.HullCentre(Hull);
		effects.SpawnWreck(Pose.ToWorld(new Vector3(centre.X, bottom, centre.Z)), top - bottom, FlatRadius,
			Definition.Color);
	}

	/// <summary>Every mesh under <paramref name="node"/> drawn in <paramref name="material"/>: the wreck's.</summary>
	private static void Burn(Node node, Material material)
	{
		if (node is MeshInstance3D mesh)
		{
			mesh.MaterialOverride = material;
		}

		foreach (Node child in node.GetChildren())
		{
			Burn(child, material);
		}
	}

	private void BuildPresentation()
	{
		var body = new StandardMaterial3D { AlbedoColor = Definition.Color, Roughness = 0.7f };
		var dark = new StandardMaterial3D { AlbedoColor = new Color(0.12f, 0.12f, 0.13f), Roughness = 0.9f };

		// Everything drawn, which a driver's correction offsets (§8.2); in it, the sprung
		// part: everything that pitches and rolls with the chassis or the tilt.
		_presentation = new Node3D { Name = "Presentation" };
		AddChild(_presentation);
		_visual = new Node3D { Name = "Visual" };
		_presentation.AddChild(_visual);

		if (Kind == CrewVehicleKind.FourByFour && Definition.Model is { } model && BuildModel(model))
		{
			PlaceWheels(Drive.Pitch, Drive.Roll);
			return;
		}

		for (int i = 0; i < Hull.Length; i++)
		{
			_visual.AddChild(new MeshInstance3D
			{
				Name = $"Hull{i}",
				Position = Hull[i].Center,
				Mesh = new BoxMesh { Size = Hull[i].HalfExtents * 2f },
				MaterialOverride = body,
			});
		}

		if (Definition.HasGun)
		{
			var mount = new Node3D { Name = "Gun", Position = Definition.GunMount };
			_gun = mount;
			mount.AddChild(new MeshInstance3D
			{
				Mesh = new BoxMesh { Size = new Vector3(0.18f, 0.22f, 0.4f) },
				MaterialOverride = dark,
			});
			mount.AddChild(new MeshInstance3D
			{
				Position = new Vector3(0f, 0.05f, -Definition.GunBarrelMeters * 0.5f),
				Mesh = new BoxMesh { Size = new Vector3(0.07f, 0.07f, Definition.GunBarrelMeters) },
				MaterialOverride = dark,
			});
			_visual.AddChild(mount);
		}

		if (Kind == CrewVehicleKind.FourByFour)
		{
			// Wheels belong to the upright body, not the sprung chassis: they stay on the ground.
			FourByFourTraits traits = DriveTraits;
			for (int wheel = 0; wheel < FourByFourSim.Wheels; wheel++)
			{
				var node = new Node3D { Name = $"Wheel{wheel}" };
				node.AddChild(new MeshInstance3D
				{
					Rotation = new Vector3(0f, 0f, Mathf.Pi * 0.5f),
					Mesh = new CylinderMesh
					{
						TopRadius = traits.WheelRadiusMeters,
						BottomRadius = traits.WheelRadiusMeters,
						Height = WheelWidthMeters,
						RadialSegments = 16,
					},
					MaterialOverride = dark,
				});
				_presentation.AddChild(node);
				_wheelNodes[wheel] = node;
			}

			PlaceWheels(Drive.Pitch, Drive.Roll);
			return;
		}

		// TODO(GV5, docs/GROUND_VEHICLES.md §12): check on a display, on a listen host and a dedicated
		// server: the cabin and boom, the rotor spinning up and its blades at speed, the tilt in flight,
		// landing on the bunker's roof. Flown headless by scripts/crew-vehicle-checks.sh heli and
		// heli-net, never looked at.
		// The helicopter: two skids under the cabin, and the rotors.
		for (int side = -1; side <= 1; side += 2)
		{
			_visual.AddChild(new MeshInstance3D
			{
				Position = new Vector3(side * 0.75f, 0.04f, -0.3f),
				Mesh = new BoxMesh { Size = new Vector3(0.08f, 0.08f, 2.6f) },
				MaterialOverride = dark,
			});
		}

		HitObb cabin = Hull.Length > 0 ? Hull[0].Local : HitObb.Aligned(Vector3.Up, Vector3.One);
		_rotor = new Node3D { Name = "Rotor", Position = cabin.Center + new Vector3(0f, cabin.HalfExtents.Y + 0.25f, 0f) };
		for (int blade = 0; blade < 2; blade++)
		{
			_rotor.AddChild(new MeshInstance3D
			{
				Rotation = new Vector3(0f, blade * Mathf.Pi * 0.5f, 0f),
				Mesh = new BoxMesh { Size = new Vector3(9.5f, 0.04f, 0.25f) },
				MaterialOverride = dark,
			});
		}
		_visual.AddChild(_rotor);

		if (Hull.Length > 1)
		{
			HitObb boom = Hull[1].Local;
			_tailRotor = new Node3D
			{
				Name = "TailRotor",
				Position = boom.Center + new Vector3(boom.HalfExtents.X + 0.1f, 0.2f, boom.HalfExtents.Z - 0.2f),
			};
			_tailRotor.AddChild(new MeshInstance3D
			{
				Mesh = new BoxMesh { Size = new Vector3(0.04f, 1.4f, 0.15f) },
				MaterialOverride = dark,
			});
			_visual.AddChild(_tailRotor);
		}
	}
}
