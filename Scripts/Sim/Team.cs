namespace Gdpyr.Sim;

/// <summary>
/// The two sides of the asymmetry (docs/IMPLEMENTATION_PLAN.md §M3).
///
/// A protocol value, not a gameplay convenience: it rides one bit of the player
/// snapshot's flag byte, and it is what decides whether a projectile may damage
/// what it struck. Lives in <c>Scripts/Sim</c> for that reason — the codec and the
/// hit resolution both need it, and neither may depend on <c>Scripts/Match</c>.
///
/// Two values, so one bit. Adding a third team is a wire change, not a rename.
/// </summary>
public enum Team : byte
{
	/// <summary>The six on the ground. Spends the ticket pool when it dies.</summary>
	GroundForce = 0,

	/// <summary>The two above it. Spends points, and owns every unit on the field.</summary>
	Strategist = 1,
}
