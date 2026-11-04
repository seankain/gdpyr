using Gdpyr.Core;
using Gdpyr.Sim;
using Godot;

namespace Gdpyr.Fps;

/// <summary>
/// A crew vehicle's mounted gun (docs/GROUND_VEHICLES.md §5.8, §6.4): the 4x4's 12.7 mm,
/// its 100-round box and where it is on the hull. Its gunner's trigger is its trigger.
///
/// It traverses and elevates relative to the hull, so where it points and where its
/// muzzle is depend on where the hull is — and a round from a moving 4x4 has to leave
/// from the hull its gunner's client was drawing, not the one the server has now
/// (§5.6). So the hull is handed in, as <see cref="Pose"/>, before every step: the
/// server's pose at the gunner's view tick, or this client's drawn pose.
/// </summary>
public sealed class CrewGun : IGunMount
{
	private readonly Vector3 _mount;
	private readonly float _barrelMeters;
	private readonly float _traverseRadians;
	private readonly float _elevationMin;
	private readonly float _elevationMax;

	/// <summary>Its box. The server's is the authority; the gunner's client steps its own (NETCODE.md §10.2).</summary>
	public WeaponState Box;

	public CrewGun(CrewVehicleDefinition definition)
	{
		WeaponDefinitionId = definition.GunWeaponDefinitionId;
		_mount = definition.GunMount;
		_barrelMeters = definition.GunBarrelMeters;
		_traverseRadians = Mathf.DegToRad(definition.GunTraverseDegrees);
		_elevationMin = Mathf.DegToRad(definition.GunElevationDegrees.X);
		_elevationMax = Mathf.DegToRad(definition.GunElevationDegrees.Y);
		Refill();
	}

	public byte WeaponDefinitionId { get; }

	public WeaponStats Stats => WeaponCatalog.StatsFor(WeaponDefinitionId);

	/// <summary>The hull the next step is fired from: set by whoever steps it, every time.</summary>
	public CrewPose Pose { get; set; }

	public ref WeaponState Belt => ref Box;

	/// <summary>A full box, reloading for ever (<c>Reserve</c> −1), as every mounted gun's.</summary>
	public void Refill() => Box = WeaponState.Ready(WeaponDefinitionId, Stats);

	/// <summary>
	/// A new gunner (§6.4): the box as it is, its spread counted again from their first
	/// round. Both ends count a gunner's rounds from the seat, so the server's cone and
	/// the gunner's predicted tracer are the same line without the count on the wire.
	/// </summary>
	public void NewGunner()
	{
		Box.ShotIndex = 0;
		Box.NextFireTick = 0;
	}

	public Vector3 AimFor(float yaw, float pitch) =>
		CrewSeats.GunDirection(Pose, yaw, pitch, _traverseRadians, _elevationMin, _elevationMax);

	public Vector3 MuzzleFor(Vector3 aim) => CrewSeats.GunMuzzle(Pose, _mount, aim, _barrelMeters);
}
