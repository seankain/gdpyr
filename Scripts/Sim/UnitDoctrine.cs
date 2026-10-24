namespace Gdpyr.Sim;

/// <summary>
/// How a unit fights when it has the say (docs/TACTICS.md §3): the part it takes in
/// its squad's drill, and the tactics it uses on its own. Named in each unit's
/// definition rather than inferred from its range or its suppression window, so a
/// new unit is what its file says it is.
/// </summary>
public enum UnitDoctrine : byte
{
	/// <summary>No tactics of its own: builders, vehicles, supply trucks, the dropship.</summary>
	None = 0,

	/// <summary>Takes cover, returns fire, bounds with its squad and throws grenades.</summary>
	Rifleman = 1,

	/// <summary>A rifleman that is also the squad's base of fire: it suppresses where the enemy is or was (§5.2).</summary>
	Gunner = 2,

	/// <summary>Fights from range out of concealment: hides, pops out to fire, and moves on (§5.3).</summary>
	Marksman = 3,
}
