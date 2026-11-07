using Gdpyr.Sim;
using Godot;

namespace Gdpyr.Fps;

/// <summary>
/// A gun a player is put behind rather than one they carry (docs/GROUND_VEHICLES.md
/// §6.4): the heavy gun on its tripod (<see cref="Emplacement"/>), the mortar launcher
/// on its baseplate (<see cref="MortarLauncher"/>) and the 4x4's mounted gun
/// (<see cref="CrewGun"/>). What <c>CombatManager.FireMounted</c> needs of any of them:
/// the belt the weapon step advances, the weapon it is, where its barrel points given
/// where the gunner looks, and where a round leaves it.
/// </summary>
public interface IGunMount
{
	/// <summary>Its belt or box: stepped by the same <see cref="WeaponSim.Step"/> on both ends.</summary>
	ref WeaponState Belt { get; }

	byte WeaponDefinitionId { get; }

	WeaponStats Stats { get; }

	/// <summary>
	/// Where the barrel points for a gunner whose eye is at <paramref name="eye"/>, looking
	/// at <paramref name="yaw"/>, <paramref name="pitch"/>: their look, clamped to the mount
	/// — or, for a mortar, the arc that brings a round down where that look meets the
	/// ground, which is what the eye is for (<see cref="Sim.MortarLay"/>).
	/// </summary>
	Vector3 AimFor(Vector3 eye, float yaw, float pitch);

	/// <summary>Where a round leaves the barrel when it points along <paramref name="aim"/>.</summary>
	Vector3 MuzzleFor(Vector3 aim);
}
