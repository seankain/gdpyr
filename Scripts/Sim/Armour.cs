using System;

namespace Gdpyr.Sim;

/// <summary>
/// What a hit does to something armoured: a unit or a structure whose definition
/// says how much of a bullet it takes (docs/NETCODE.md §10.6).
///
/// One rule for both, because a tank and a pillbox are armoured the same way and a
/// ground-force player has to learn it once: explosives do everything they would do
/// to anything else, and bullets do <c>bulletScale</c> of it. At a scale of zero —
/// the tank, the pillbox and the sniper tower — small arms do nothing at all, and
/// the launcher is the answer.
///
/// "Explosive" is the round's, not the weapon's (<see cref="ProjectileStats.IsExplosive"/>),
/// so a grenade's direct impact counts as well as its blast, and a hammer — which
/// fires no round — is a bullet here. So is the heavy gun's 12.7 mm: it is not
/// small arms, but it is not an explosive either, and the rule is about what
/// arrives rather than how big the gun was.
/// </summary>
public static class Armour
{
	/// <summary>
	/// The damage a hit actually does. Never negative: a scale below zero is an
	/// authoring mistake, and a round that healed what it hit would be a worse one.
	/// </summary>
	public static float DamageTaken(float amount, bool explosive, float bulletScale) =>
		amount <= 0f ? 0f : explosive ? amount : amount * MathF.Max(bulletScale, 0f);

	/// <summary>
	/// True for something bullets cannot hurt at all. What a ground bot asks before it
	/// spends a magazine on a target (<c>GroundSensor.NearestHostileTarget</c>).
	/// </summary>
	public static bool IsBulletProof(float bulletScale) => bulletScale <= 0f;

	/// <summary>Whether a round of this kind can hurt something with this armour.</summary>
	public static bool CanHurt(bool explosive, float bulletScale) => explosive || !IsBulletProof(bulletScale);
}
