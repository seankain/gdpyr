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

	/// <summary>What a character body collides with: the level, and each other.</summary>
	public const uint CharacterMask = World | Players;
}
