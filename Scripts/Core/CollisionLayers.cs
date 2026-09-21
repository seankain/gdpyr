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

	/// <summary>What a character body collides with: the level, each other, and units.</summary>
	public const uint CharacterMask = World | Players | Units;

	/// <summary>What a unit body collides with. The same set: units are bodies too.</summary>
	public const uint UnitMask = World | Players | Units;
}
