using System;
using Godot;

namespace Gdpyr.Sim;

/// <summary>
/// What rounds and blasts do to a crew vehicle and the people in it, and what its
/// wreck does to them (docs/GROUND_VEHICLES.md §6.5, GV-D9 to GV-D12), as pure
/// functions.
///
/// Engine-free for the reason the rest of the vehicle's rules are: a hull is analytic
/// boxes (<see cref="HitObb"/>), tested by the same code a unit's capsule is, so which of
/// a hull, a player and a unit a round reaches first is decided by fractions along the
/// segment that a test can state. The engine half finds the vehicles, deals the damage
/// and puts the crew out.
/// </summary>
public static class CrewCombat
{
	/// <summary>What a wreck deals everybody it throws out, as an explosive (GV-D12): half a player's 100.</summary>
	public const float WreckDamage = 50f;

	/// <summary>
	/// Higher above the ground than this a helicopter's wreck kills everybody aboard
	/// (GV-D12) [m]: the height anybody may get out at (<see cref="CrewSeats.MaxExitHeightMeters"/>).
	/// </summary>
	public const float WreckSurvivableHeightMeters = CrewSeats.MaxExitHeightMeters;

	/// <summary>How long a wreck burns on the field before it is taken away (GV-D12) [s].</summary>
	public const float WreckBurnSeconds = 2f;

	public const int WreckBurnTicks = (int)(WreckBurnSeconds * SimConfig.TickRate);

	/// <summary>
	/// The first box of <paramref name="hull"/> the segment from <paramref name="from"/> to
	/// <paramref name="to"/> enters nearer than <paramref name="nearest"/>, a fraction along
	/// it, grown by the round's <paramref name="sweepRadius"/> (§5.4's slab test). Narrows
	/// <paramref name="nearest"/> and writes which box; false, and −1, when none is nearer.
	/// </summary>
	public static bool FirstHit(ReadOnlySpan<HitObb> hull, Vector3 from, Vector3 to, float sweepRadius,
		ref float nearest, out int box)
	{
		box = -1;
		for (int i = 0; i < hull.Length; i++)
		{
			if (Hitbox.SegmentIntersects(hull[i], from, to, sweepRadius, out float t) && t < nearest)
			{
				nearest = t;
				box = i;
			}
		}

		return box >= 0;
	}

	/// <summary>
	/// How far a blast at <paramref name="point"/> is from the hull (§6.5): to the face of
	/// its nearest box, 0 inside one. <see cref="float.PositiveInfinity"/> for no boxes.
	/// </summary>
	public static float BlastDistance(ReadOnlySpan<HitObb> hull, Vector3 point)
	{
		float best = float.PositiveInfinity;
		for (int i = 0; i < hull.Length; i++)
		{
			best = MathF.Min(best, Hitbox.DistanceTo(hull[i], point));
		}

		return best;
	}

	/// <summary>
	/// What a round that strikes the hull deals it before armour (GV-D10): the round's
	/// <see cref="ProjectileStats.VehicleImpactDamage"/> when it has one — the tank's 1,000
	/// — and its ordinary damage otherwise.
	/// </summary>
	public static float DirectDamage(in ProjectileStats round) => round.DirectDamageAgainstCrewVehicle;

	/// <summary>
	/// What a hull takes (GV-D9): explosives whole, bullets <paramref name="bulletScale"/>
	/// of themselves, and then the round's own share against a vehicle — the hand
	/// grenade's quarter (<see cref="Armour.DamageTaken(float, bool, float, float)"/>).
	/// </summary>
	public static float DamageTaken(float amount, bool explosive, float bulletScale, float roundVehicleScale) =>
		Armour.DamageTaken(amount, explosive, bulletScale, roundVehicleScale);

	/// <summary>
	/// Whether a blast reaches a seat's occupant (GV-D11): an enclosed seat's never — the
	/// hull is their armour and takes the blast for them — an exposed seat's as it reaches
	/// anybody standing there.
	/// </summary>
	public static bool BlastReaches(in CrewSeat seat) => !seat.Enclosed;

	/// <summary>
	/// Whether a wreck kills everybody aboard rather than throwing them out (GV-D12): a
	/// helicopter higher than <see cref="WreckSurvivableHeightMeters"/> off the ground, or
	/// one whose height is not known. A 4x4's never does by its height.
	/// </summary>
	public static bool WreckKillsAll(CrewVehicleKind kind, float heightAboveGround) =>
		kind == CrewVehicleKind.Helicopter && !(heightAboveGround <= WreckSurvivableHeightMeters);

	/// <summary>
	/// What a wreck deals one occupant it throws out (GV-D12): <see cref="WreckDamage"/>, as
	/// an explosive, the vest its share. One it cannot put anywhere — too high, or nowhere
	/// clear — is dealt more than <paramref name="health"/> and <paramref name="vestPoints"/>
	/// together, which no vest can leave standing.
	/// </summary>
	public static float WreckDamageTo(bool placed, int health, int vestPoints) =>
		placed ? WreckDamage : Math.Max(health, 0) + Math.Max(vestPoints, 0) + 1f;
}
