namespace Gdpyr.Core;

/// <summary>
/// The project's physics layers, as masks.
///
/// Players sit on their own layer from M2 on, because a projectile's world query
/// must not hit them: a player's hit is resolved analytically, against the capsule
/// recorded for the tick in question (docs/NETCODE.md §5), so that the decision can
/// be re-derived later — and tested — without the physics server's current state.
/// These have to stay in step with <c>[layer_names]</c> in <c>project.godot</c>.
/// </summary>
public static class CollisionLayers
{
	/// <summary>Layer 1: level geometry. What a bullet stops against.</summary>
	public const uint World = 1 << 0;

	/// <summary>Layer 2: character bodies. Collided with, never raycast for damage.</summary>
	public const uint Players = 1 << 1;

	/// <summary>
	/// Layer 3: RTS units, from M3. Their own layer for the same reason players
	/// have one — a projectile's world query must not find them, because a unit's
	/// hit is resolved against its capsule in engine-free code — and so that a
	/// strategist's selection ray can ask for units without also selecting the
	/// scenery.
	/// </summary>
	public const uint Units = 1 << 2;

	/// <summary>
	/// Layer 4: the ground force's crew vehicles (docs/GROUND_VEHICLES.md §6.1). Their
	/// own layer for the reason players and units have one — a round meets a hull in
	/// engine-free code, against its boxes, so a projectile's world query must not find
	/// the collider — and because bodies and vehicles are solid to each other one way
	/// only (GV-D8): a body collides with a hull, a hull does not collide with a body.
	/// </summary>
	public const uint Vehicles = 1 << 3;

	/// <summary>
	/// What a character body collides with: the level, each other, units, and crew
	/// vehicles — it cannot walk into one, and one that drives into it pushes it aside
	/// through its own <c>MoveAndSlide</c> (GV-D8).
	/// </summary>
	public const uint CharacterMask = World | Players | Units | Vehicles;

	/// <summary>What a unit body collides with. The same set: units are bodies too.</summary>
	public const uint UnitMask = World | Players | Units | Vehicles;

	/// <summary>
	/// What a crew vehicle collides with: the level, structures (which are on the world
	/// layer once built) and other crew vehicles — not bodies, which would let one
	/// rifleman stop a truck, and which the driver's prediction only knows a round trip
	/// late (GV-D8).
	/// </summary>
	public const uint VehicleMask = World | Vehicles;
}
