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
/// collider stays upright and turns only with the heading (GV-D3): the sprung chassis's
/// pitch and roll, or the helicopter's tilt, are drawn on the mesh and applied to the
/// seats and the hull's boxes through <see cref="Pose"/>.
///
/// It never ticks itself: <see cref="CrewVehicleManager"/> steps it, on the server's
/// tick, with its driver's frame or a parked one. Its presentation — greybox boxes in
/// the ground force's colour, wheels placed by their springs, the gun on its mount,
/// rotor discs — is built only off a dedicated server, as the dropship's is.
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
	private readonly Godot.Collections.Array<Rid> _exclude = new();
	private PhysicsRayQueryParameters3D _ray;

	private Node3D _visual;
	private readonly Node3D[] _wheelNodes = new Node3D[FourByFourSim.Wheels];
	private Node3D _rotor;
	private Node3D _tailRotor;
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

	/// <summary>Its pose over the last half second (§5.6): what a seated shooter's round will leave from.</summary>
	public PoseHistory Poses { get; } = new();

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
		AddChild(new CollisionShape3D
		{
			Name = "Collider",
			Shape = new ConvexPolygonShape3D { Points = points },
		});

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
			_wheels[wheel] = CastWheel(space, FourByFourSim.Hardpoint(Drive, traits, wheel), traits.RayLengthMeters);
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
		GlobalTransform = new Transform3D(new Basis(Vector3.Up, yaw), position);
		Velocity = velocity;
		MoveAndSlide();
		after = GlobalPosition;
		afterVelocity = GetRealVelocity();
		Velocity = afterVelocity;
	}

	/// <summary>One wheel's ray, straight down from its hardpoint on the upright body (§5.1).</summary>
	private WheelContact CastWheel(PhysicsDirectSpaceState3D space, Vector3 from, float length)
	{
		_ray.From = from;
		_ray.To = from + (Vector3.Down * length);
		Godot.Collections.Dictionary hit = space.IntersectRay(_ray);
		return hit.Count == 0 ? WheelContact.Miss : WheelContact.At(from.Y - hit["position"].AsVector3().Y);
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
		if (_visual == null)
		{
			return;
		}

		CrewPose pose = Pose;
		_visual.Basis = CrewPose.Rotation(0f, pose.Pitch, pose.Roll);

		if (Kind == CrewVehicleKind.FourByFour)
		{
			PlaceWheels();
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

	/// <summary>Each wheel where its spring holds it: on the ground its ray found, or hanging at full length.</summary>
	private void PlaceWheels()
	{
		FourByFourTraits traits = DriveTraits;
		float shortest = traits.SpringRestMeters - traits.SpringTravelMeters;
		for (int wheel = 0; wheel < FourByFourSim.Wheels; wheel++)
		{
			if (_wheelNodes[wheel] == null)
			{
				continue;
			}

			WheelContact contact = _wheels[wheel];
			float spring = contact.Hit
				? Mathf.Clamp(contact.Distance - traits.WheelRadiusMeters, shortest, traits.SpringRestMeters)
				: traits.SpringRestMeters;

			Vector3 hardpoint = FourByFourSim.HardpointLocal(traits, wheel);
			_wheelNodes[wheel].Position = hardpoint + (Vector3.Down * spring);

			// The front pair steer: positive is to the right, which is a turn about −Y.
			_wheelNodes[wheel].Rotation = new Vector3(0f, wheel < 2 ? -Drive.Steer : 0f, 0f);
		}
	}

	private void BuildPresentation()
	{
		var body = new StandardMaterial3D { AlbedoColor = Definition.Color, Roughness = 0.7f };
		var dark = new StandardMaterial3D { AlbedoColor = new Color(0.12f, 0.12f, 0.13f), Roughness = 0.9f };

		// The sprung part: everything that pitches and rolls with the chassis or the tilt.
		_visual = new Node3D { Name = "Visual" };
		AddChild(_visual);

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
				AddChild(node);
				_wheelNodes[wheel] = node;
			}

			PlaceWheels();
			return;
		}

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
