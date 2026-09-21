using Gdpyr.Sim;
using Godot;

namespace Gdpyr.Fps;

/// <summary>
/// What a weapon launches: a projectile's mass, size, drag and muzzle velocity,
/// plus what it does on arrival.
///
/// Separate from <see cref="WeaponDefinition"/> because the simulation addresses
/// projectiles by their own byte id on the wire (docs/NETCODE.md §4.2) and because
/// ballistics are tuned against the table in docs/NETCODE.md §4.4, which is about
/// rounds and not about the guns that fire them.
///
/// Every number here is a gameplay knob. Muzzle velocity especially: at realistic
/// speeds the travel time this whole system exists to model is invisible, so
/// expect to run 200–400 m/s rather than 940 (docs/NETCODE.md §4.4).
/// </summary>
[GlobalClass]
public partial class ProjectileDefinition : Resource
{
	[Export]
	public StringName Name;

	[ExportCategory("Ballistics")]

	[Export]
	public float MassGrams = 4f;

	[Export]
	public float RadiusMillimeters = 2.8f;

	/// <summary>
	/// The dimensionless C_d of the drag equation. The pyrrhic implementation this
	/// was ported from passed a G1 ballistic coefficient in here, which is a
	/// different quantity and silently mis-scales drag; see
	/// <see cref="ProjectileParams"/>.
	/// </summary>
	[Export]
	public float DragCoefficient = 0.295f;

	[Export]
	public float MuzzleVelocity = 400f;

	[Export]
	public float LifetimeSeconds = SimConfig.MaxProjectileLifetimeSeconds;

	[ExportCategory("Damage")]

	[Export]
	public float Damage = 25f;

	/// <summary>Blast radius, or 0 for a round that only damages what it strikes.</summary>
	[Export]
	public float ExplosionRadiusMeters;

	[Export]
	public float ExplosionDamage;

	/// <summary>
	/// The projectile's own radius for hit tests. A bullet is a ray; a grenade has a
	/// body, and testing one as a ray makes it pass through a shoulder.
	/// </summary>
	[Export]
	public float SweepRadiusMeters;

	[ExportCategory("Tracer")]

	[Export]
	public Color TracerColor = new(1f, 0.85f, 0.4f);

	[Export]
	public float TracerLengthMeters = 1.2f;

	[Export]
	public float TracerRadiusMeters = 0.03f;

	public ProjectileStats ToStats() => new(
		ProjectileParams.FromGramsAndMillimeters(MassGrams, RadiusMillimeters, DragCoefficient),
		MuzzleVelocity,
		Damage,
		ExplosionRadiusMeters,
		ExplosionDamage,
		LifetimeSeconds,
		SweepRadiusMeters);
}
