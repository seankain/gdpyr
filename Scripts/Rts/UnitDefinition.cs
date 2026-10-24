using Gdpyr.Sim;
using Godot;

namespace Gdpyr.Rts;

/// <summary>
/// One kind of unit, as an editable resource: how tough it is, how fast it walks,
/// how far it sees, what it shoots with and what it costs
/// (docs/IMPLEMENTATION_PLAN.md §M3 — "one class, parameterised by a
/// <c>UnitDefinition</c>").
///
/// This is deliberately the whole of what makes a rifleman different from a tank.
/// The pyrrhic original composed units out of runtime-added capability components
/// (<c>UnitBase</c>/<c>UnitCapability</c>); §1 of the plan rejects that for a
/// greybox, and M5's three tiers are three of these files rather than three class
/// hierarchies.
///
/// A unit shoots with a weapon from the same catalog players use, because a
/// projectile carries one byte naming what launched it and two tables would be two
/// things to keep in step (docs/NETCODE.md §4.2).
/// </summary>
[GlobalClass]
public partial class UnitDefinition : Resource
{
	[Export]
	public StringName Name = "unit";

	[ExportCategory("Economy")]

	/// <summary>Points the strategist pays to put one on the queue.</summary>
	[Export]
	public int Cost = 50;

	/// <summary>Seconds one takes to come out of a barracks, or of the structure that trains it.</summary>
	[Export]
	public float BuildSeconds = 4f;

	/// <summary>
	/// Whether a barracks trains it. False for a unit only a structure trains — the
	/// sniper a sniper tower turns out, the heavy gunner a pillbox does
	/// (<see cref="StructureDefinition.TrainsUnitId"/>, docs/NETCODE.md §10.8): what
	/// makes it worth building the structure, and what keeps the barracks' card the
	/// five things it always was.
	/// </summary>
	[Export]
	public bool TrainedInBarracks = true;

	[ExportCategory("Body")]

	[Export]
	public float MaxHealth = 100f;

	/// <summary>
	/// What fraction of a bullet's damage it takes; explosives always do the whole
	/// (<see cref="Armour"/>). One for anything soft. Zero is armour — the tank's —
	/// which small arms cannot hurt at all, so the launcher is the only answer to it
	/// (docs/NETCODE.md §10.6).
	/// </summary>
	[Export]
	public float BulletDamageScale = 1f;

	/// <summary>True for a unit bullets do nothing to.</summary>
	public bool IsBulletProof => Armour.IsBulletProof(BulletDamageScale);

	/// <summary>
	/// True for a truck or a tank: what an anti-personnel round does only its
	/// <see cref="ProjectileStats.VehicleDamageScale"/> to — a hand grenade a quarter of
	/// itself (docs/NETCODE.md §10.9). Separate from armour on purpose: the technical
	/// and the supply truck are vehicles that a bullet hurts.
	/// </summary>
	[Export]
	public bool IsVehicle;

	/// <summary>
	/// Health it gets back per second while it is resupplied: inside a friendly
	/// barracks' defended ring or a supply truck's reach, and not hit for
	/// <see cref="Resupply.DamageLockoutTicks"/> (docs/HTN_BOTS.md §8, D2). Zero is a
	/// unit that never heals, which is what a file that says nothing gets.
	/// </summary>
	[Export]
	public float RegenPerSecond;

	/// <summary>Ground speed [m/s]. A rifleman jogs; nothing here sprints.</summary>
	[Export]
	public float MoveSpeed = 4.5f;

	/// <summary>Collision and hitbox capsule. The same capsule serves both.</summary>
	[Export]
	public float HeightMeters = 1.8f;

	[Export]
	public float RadiusMeters = 0.4f;

	/// <summary>
	/// Where the muzzle is, above the unit's feet. Shots leave from here and hit
	/// tests start here, so a unit behind a waist-high wall shoots over it.
	/// </summary>
	[Export]
	public float EyeHeightMeters = 1.5f;

	/// <summary>
	/// Its height crouched (docs/TACTICS.md §5.8): the hitbox and collider then, below
	/// a 1.1 m sandbag wall. Zero for a body that cannot crouch, which is what a file
	/// that says nothing gets — a vehicle, an aircraft.
	/// </summary>
	[Export]
	public float CrouchHeightMeters;

	/// <summary>Where its eye is crouched: below the wall it crouches behind, so concealment cuts both ways.</summary>
	[Export]
	public float CrouchEyeHeightMeters;

	/// <summary>
	/// How many units on foot can ride in it (docs/VEHICLES.md V-D1, V-D2): the
	/// technical's four. Zero carries nobody, which is what a file that says nothing
	/// gets. More than <see cref="SimConfig.MaxPassengers"/> is that many.
	/// </summary>
	[Export]
	public int Seats;

	/// <summary>True for a vehicle other units can get into.</summary>
	public bool CarriesUnits => Seats > 0;

	[ExportCategory("Flight")]

	/// <summary>
	/// The height above the ground it cruises at (docs/VEHICLES.md §4.2, §8.1): the
	/// dropship's fourteen metres. Zero is a unit that does not fly, which is what a
	/// file that says nothing gets. It takes off when it has somewhere to go and lands
	/// when it has nowhere, and moves across the ground only in the air.
	/// </summary>
	[Export]
	public float CruiseAltitudeMeters;

	/// <summary>How fast it climbs [m/s].</summary>
	[Export]
	public float ClimbMetersPerSecond;

	/// <summary>How fast it comes down [m/s].</summary>
	[Export]
	public float DescentMetersPerSecond;

	/// <summary>
	/// Below this height it moves only up and down, so it never slides into a barracks
	/// roof (6.5 m) or a sniper tower's cabin (8.4 m) on its way up or down.
	/// </summary>
	[Export]
	public float ClearAltitudeMeters;

	/// <summary>True for a unit that flies.</summary>
	public bool IsAircraft => CruiseAltitudeMeters > 0f;

	[ExportCategory("Senses and weapons")]

	/// <summary>
	/// How far it notices an enemy, and — since M4 — how far the side that owns it
	/// can see at all (docs/NETCODE.md §6.2). It is the lever that makes scouting a
	/// real decision, so different tiers are meant to differ here.
	/// </summary>
	[Export]
	public float SensorRadiusMeters = 45f;

	/// <summary>How far it will shoot from. Kept at or under the sensor: a unit cannot shoot what it cannot see.</summary>
	[Export]
	public float EngageRangeMeters = 40f;

	/// <summary>How far a defending unit strays from its anchor before turning back.</summary>
	[Export]
	public float LeashRadiusMeters = 20f;

	/// <summary>How close to a destination counts as arrived.</summary>
	[Export]
	public float ArrivalRadiusMeters = 1.5f;

	/// <summary>
	/// Index into <c>WeaponCatalog</c>. Units fire the same projectiles players do
	/// (docs/IMPLEMENTATION_PLAN.md §M3).
	/// </summary>
	[Export]
	public int WeaponId = 3;

	/// <summary>
	/// Half-angle of this unit's accuracy cone, in degrees. This is the number that
	/// decides whether twenty riflemen are a threat or a firing squad, and it is a
	/// pure playtest knob.
	/// </summary>
	[Export]
	public float AccuracyConeDegrees = 2.5f;

	/// <summary>
	/// Seconds it keeps firing at the place it last saw its target after losing sight
	/// of it (<see cref="Suppression"/>): the heavy gunner's trait, which is what makes
	/// its long belt suppressive fire rather than only a lot of it — a player who
	/// ducks behind a wall finds the wall still being shot at when they look up. Zero
	/// for everything else, which stops shooting when it stops seeing.
	/// </summary>
	[Export]
	public float SuppressSeconds;

	/// <summary><see cref="SuppressSeconds"/> in ticks; zero for a unit that does not suppress.</summary>
	public int SuppressTicks => WeaponStats.SecondsToTicks(SuppressSeconds);

	/// <summary>
	/// Hand grenades it carries for its life (docs/NETCODE.md §10.9): three for anything
	/// on foot, which is what a file that says nothing gets, and none for a vehicle,
	/// whose crew does not lean out to throw one. It throws at a target in throwing
	/// range that is behind something or standing still (<see cref="GrenadeSim"/>).
	/// </summary>
	[Export]
	public int Grenades = GrenadeSim.Carried;

	/// <summary>
	/// Whether it can put up structures (docs/NETCODE.md §10.5). The builder's one
	/// trait: a flag rather than a tier, because what makes a builder different is
	/// what it may be ordered to do, not how hard it is to kill.
	/// </summary>
	[Export]
	public bool CanConstruct;

	/// <summary>
	/// How far round it, measured flat, friendly units are resupplied
	/// (<see cref="Resupply"/>): the supply truck's one trait, as building is the
	/// builder's. Zero for everything else. It does not resupply itself.
	/// </summary>
	[Export]
	public float SupplyRadiusMeters;

	/// <summary>True for a unit other units come to for resupply.</summary>
	public bool IsSupply => SupplyRadiusMeters > 0f;

	/// <summary>
	/// How it fights when it has the say (docs/TACTICS.md §3): a rifleman, which is
	/// what a file that says nothing gets; the squad's gunner; a marksman; or none, for
	/// a builder, a vehicle or a truck, which keeps the behaviour it has.
	/// </summary>
	[Export]
	public UnitDoctrine Doctrine = UnitDoctrine.Rifleman;

	[ExportCategory("Presentation")]

	[Export]
	public Color Color = new(0.85f, 0.25f, 0.2f);

	public byte WeaponDefinitionId => (byte)Mathf.Clamp(WeaponId, 0, SimConfig.MaxWeaponDefinitions - 1);

	/// <summary>Its body, standing and crouched (docs/TACTICS.md §5.8).</summary>
	public UnitBody ToBody() =>
		new(HeightMeters, RadiusMeters, EyeHeightMeters, CrouchHeightMeters, CrouchEyeHeightMeters);

	/// <summary>The brain's view of this unit: four radii and how it fights.</summary>
	public UnitTraits ToTraits() => new(
		SensorRadiusMeters,
		Mathf.Min(EngageRangeMeters, SensorRadiusMeters),
		ArrivalRadiusMeters,
		LeashRadiusMeters,
		Doctrine);

	/// <summary>The phase machine's view of how it flies (<see cref="Flight"/>); a cruise altitude of zero for a unit that does not.</summary>
	public FlightTraits ToFlightTraits() => new(
		CruiseAltitudeMeters,
		ClimbMetersPerSecond,
		DescentMetersPerSecond,
		ClearAltitudeMeters);

	public float AccuracyConeRadians => Spread.ConeFromDegrees(AccuracyConeDegrees);

	public int BuildTicks => Mathf.Max(WeaponStats.SecondsToTicks(BuildSeconds), 1);
}
