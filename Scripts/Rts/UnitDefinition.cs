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

	/// <summary>Seconds one takes to come out of a barracks.</summary>
	[Export]
	public float BuildSeconds = 4f;

	[ExportCategory("Body")]

	[Export]
	public float MaxHealth = 100f;

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

	[ExportCategory("Presentation")]

	[Export]
	public Color Color = new(0.85f, 0.25f, 0.2f);

	public byte WeaponDefinitionId => (byte)Mathf.Clamp(WeaponId, 0, SimConfig.MaxWeaponDefinitions - 1);

	/// <summary>The brain's view of this unit: four radii and nothing else.</summary>
	public UnitTraits ToTraits() => new(
		SensorRadiusMeters,
		Mathf.Min(EngageRangeMeters, SensorRadiusMeters),
		ArrivalRadiusMeters,
		LeashRadiusMeters);

	public float AccuracyConeRadians => Spread.ConeFromDegrees(AccuracyConeDegrees);

	public int BuildTicks => Mathf.Max(WeaponStats.SecondsToTicks(BuildSeconds), 1);
}
