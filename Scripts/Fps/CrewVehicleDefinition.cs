using Gdpyr.Sim;
using Godot;

namespace Gdpyr.Fps;

/// <summary>
/// One kind of crew vehicle — a vehicle the ground force drives — as an editable
/// resource (docs/GROUND_VEHICLES.md §5.8): how tough it is, how often the kiosk hands
/// one out, the box it collides as and the boxes it is hit as, its seats, its gun, the
/// speed that runs a unit over, and how it drives or flies.
///
/// The seats and the hull are parallel arrays rather than sub-resources: a
/// <c>.tres</c> of packed arrays is what <c>dotnet test</c> can read without the
/// engine, so the facts the hull's shielding stands on — every enclosed seat inside its
/// box — are checked against the file that ships (§5.4). <see cref="CrewLayout"/> turns
/// them into the structs the rules read, for the engine and for those tests alike.
///
/// Every number is a playtest dial (§9). Positions are in the vehicle's own frame:
/// forward −Z, up +Y, right +X; the 4x4's origin is the middle of its hardpoints' plane
/// on the upright body, the helicopter's its skids.
/// </summary>
[GlobalClass]
public partial class CrewVehicleDefinition : Resource
{
	[Export]
	public StringName Name = "vehicle";

	/// <summary>Which rules move it: <see cref="CrewVehicleKind"/>, and its id in <c>CrewVehicleCatalog</c>.</summary>
	[Export]
	public int Kind;

	[ExportCategory("Body")]

	[Export]
	public float MaxHealth = 600f;

	/// <summary>Of a bullet's damage it takes (<see cref="Armour"/>); explosives do their whole, times the round's vehicle share.</summary>
	[Export]
	public float BulletDamageScale = 0.15f;

	/// <summary>Seconds from one being lost to the next being ready at the kiosk (GV-D13).</summary>
	[Export]
	public float RespawnSeconds = 45f;

	/// <summary>The most of this kind alive at once.</summary>
	[Export]
	public int Cap = 1;

	/// <summary>
	/// How fast its hull must be going, where it touches and towards the unit, to kill a
	/// unit on foot it strikes (GV-D17, §5.9) [m/s].
	/// </summary>
	[Export]
	public float RamKillSpeedMetersPerSecond = 8f;

	[ExportCategory("Collider")]

	/// <summary>The collider's centre: a bevelled box (<see cref="CrewLayout.BevelledBox"/>).</summary>
	[Export]
	public Vector3 ColliderCentre;

	[Export]
	public Vector3 ColliderHalfExtents = new(0.95f, 0.95f, 2.4f);

	/// <summary>How much of each edge is cut off, so it slides off a wall's end rather than catching [m].</summary>
	[Export]
	public float ColliderBevelMeters = 0.25f;

	[ExportCategory("Hull")]

	/// <summary>Each hull box's centre; up to <see cref="CrewLayout.MaxHullBoxes"/>.</summary>
	[Export]
	public Vector3[] HullCentres = System.Array.Empty<Vector3>();

	/// <summary>Each hull box's half extents, beside <see cref="HullCentres"/>.</summary>
	[Export]
	public Vector3[] HullHalfExtents = System.Array.Empty<Vector3>();

	[ExportCategory("Seats")]

	/// <summary>Each seat's <see cref="SeatRole"/>, in seat order; up to <see cref="CrewLayout.MaxSeats"/>.</summary>
	[Export]
	public int[] SeatRoles = System.Array.Empty<int>();

	/// <summary>1 for a seat inside the hull (GV-D11), 0 for an exposed one.</summary>
	[Export]
	public int[] SeatEnclosed = System.Array.Empty<int>();

	/// <summary>The seated player's capsule: its lower hemisphere's centre.</summary>
	[Export]
	public Vector3[] SeatCapsuleBottoms = System.Array.Empty<Vector3>();

	/// <summary>The seated player's capsule: its upper hemisphere's centre.</summary>
	[Export]
	public Vector3[] SeatCapsuleTops = System.Array.Empty<Vector3>();

	[Export]
	public float[] SeatCapsuleRadii = System.Array.Empty<float>();

	[Export]
	public Vector3[] SeatEyes = System.Array.Empty<Vector3>();

	/// <summary>Where a player getting in stands for each seat: the nearest free one's is taken.</summary>
	[Export]
	public Vector3[] SeatDoors = System.Array.Empty<Vector3>();

	/// <summary>Where each seat's occupant is put first when they get out.</summary>
	[Export]
	public Vector3[] SeatExits = System.Array.Empty<Vector3>();

	/// <summary>Each seat's fire arc in yaw, (min, max) degrees from the nose, positive to the left.</summary>
	[Export]
	public Vector2[] SeatArcYawDegrees = System.Array.Empty<Vector2>();

	/// <summary>Each seat's fire arc in pitch, (min, max) degrees from the hull's floor.</summary>
	[Export]
	public Vector2[] SeatArcPitchDegrees = System.Array.Empty<Vector2>();

	[ExportCategory("Gun")]

	/// <summary>The mounted gun's id in <c>WeaponCatalog</c>; −1 for a vehicle with none.</summary>
	[Export]
	public int GunWeaponId = -1;

	/// <summary>Where the gun pivots.</summary>
	[Export]
	public Vector3 GunMount;

	/// <summary>From the pivot to the muzzle along the barrel [m].</summary>
	[Export]
	public float GunBarrelMeters = 1f;

	/// <summary>Either side of the nose it turns [degrees]: 180 is all the way round.</summary>
	[Export]
	public float GunTraverseDegrees = 180f;

	/// <summary>How far it depresses and elevates relative to the hull, (min, max) degrees.</summary>
	[Export]
	public Vector2 GunElevationDegrees = new(-15f, 50f);

	[ExportCategory("Driving")]

	[Export]
	public float WheelbaseMeters = 3f;

	[Export]
	public float HalfTrackMeters = 0.85f;

	[Export]
	public float WheelRadiusMeters = 0.4f;

	[Export]
	public float SpringRestMeters = 0.4f;

	[Export]
	public float SpringTravelMeters = 0.25f;

	[Export]
	public float SpringFrequencyHz = 1.6f;

	[Export]
	public float DampingRatio = 0.3f;

	[Export]
	public float CentreOfMassHeightMeters = 0.55f;

	[Export]
	public float PitchGyrationMeters = 1.5f;

	[Export]
	public float RollGyrationMeters = 0.85f;

	[Export]
	public float MaxSpeed = 18f;

	[Export]
	public float ReverseSpeed = 6f;

	[Export]
	public float DriveAcceleration = 4f;

	[Export]
	public float BrakeDeceleration = 9f;

	[Export]
	public float CoastDeceleration = 1f;

	[Export]
	public float HandbrakeDeceleration = 6f;

	[Export]
	public float HandbrakeGripScale = 1f / 3f;

	[Export]
	public float GripAcceleration = 9f;

	[Export]
	public float SteerRate = 2.5f;

	[Export]
	public float SteerLimitAtRestDegrees = 32f;

	[Export]
	public float SteerLimitAtTopDegrees = 9f;

	[Export]
	public float AirborneRateDamping = 2f;

	[ExportCategory("Flying")]

	[Export]
	public float SpinUpSeconds = 2.5f;

	[Export]
	public float SpinDownSeconds = 4f;

	[Export]
	public float LiftRotor = 0.6f;

	[Export]
	public float MaxForwardSpeed = 22f;

	[Export]
	public float MaxSideSpeed = 10f;

	[Export]
	public float FlightAcceleration = 5f;

	[Export]
	public float ClimbSpeed = 5f;

	[Export]
	public float DescentSpeed = 6f;

	[Export]
	public float VerticalAcceleration = 8f;

	[Export]
	public float TurnRateDegrees = 90f;

	[Export]
	public float CeilingMeters = 25f;

	[Export]
	public float UnpilotedDescentSpeed = 4f;

	[Export]
	public float UnpilotedDrag = 2f;

	[Export]
	public float MaxTiltDegrees = 20f;

	[Export]
	public float TiltResponse = 6f;

	[ExportCategory("Presentation")]

	[Export]
	public Color Color = new(0.25f, 0.45f, 0.75f);

	public CrewVehicleKind VehicleKind => (CrewVehicleKind)Mathf.Clamp(Kind, 0, (int)CrewVehicleKind.Helicopter);

	public bool HasGun => GunWeaponId >= 0;

	public byte GunWeaponDefinitionId => (byte)Mathf.Clamp(GunWeaponId, 0, SimConfig.MaxWeaponDefinitions - 1);

	/// <summary>The kiosk's timer for this kind (GV-D13).</summary>
	public KioskTimer ToKioskTimer() => KioskTimer.FromSeconds(RespawnSeconds, Cap);

	/// <summary>Its seats, in seat order (§5.3).</summary>
	public CrewSeat[] ToSeats() => CrewLayout.Seats(SeatRoles, SeatEnclosed, SeatCapsuleBottoms, SeatCapsuleTops,
		SeatCapsuleRadii, SeatEyes, SeatDoors, SeatExits, SeatArcYawDegrees, SeatArcPitchDegrees);

	/// <summary>Its hull's boxes (§5.4).</summary>
	public HullBox[] ToHull() => CrewLayout.Hull(HullCentres, HullHalfExtents);

	/// <summary>The points of its bevelled collider (§6.1).</summary>
	public Vector3[] ToColliderPoints() =>
		CrewLayout.BevelledBox(ColliderCentre, ColliderHalfExtents, ColliderBevelMeters);

	/// <summary>How the 4x4 drives (<see cref="FourByFourSim"/>).</summary>
	public FourByFourTraits ToFourByFourTraits() => new(
		WheelbaseMeters,
		HalfTrackMeters,
		WheelRadiusMeters,
		SpringRestMeters,
		SpringTravelMeters,
		SpringFrequencyHz,
		DampingRatio,
		CentreOfMassHeightMeters,
		PitchGyrationMeters,
		RollGyrationMeters,
		MaxSpeed,
		ReverseSpeed,
		DriveAcceleration,
		BrakeDeceleration,
		CoastDeceleration,
		HandbrakeDeceleration,
		HandbrakeGripScale,
		GripAcceleration,
		SteerRate,
		Mathf.DegToRad(SteerLimitAtRestDegrees),
		Mathf.DegToRad(SteerLimitAtTopDegrees),
		AirborneRateDamping);

	/// <summary>How the helicopter flies (<see cref="HelicopterSim"/>).</summary>
	public HelicopterTraits ToHelicopterTraits() => new(
		SpinUpSeconds,
		SpinDownSeconds,
		LiftRotor,
		MaxForwardSpeed,
		MaxSideSpeed,
		FlightAcceleration,
		ClimbSpeed,
		DescentSpeed,
		VerticalAcceleration,
		Mathf.DegToRad(TurnRateDegrees),
		CeilingMeters,
		UnpilotedDescentSpeed,
		UnpilotedDrag,
		Mathf.DegToRad(MaxTiltDegrees),
		TiltResponse);
}
