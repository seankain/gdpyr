using System;

namespace Gdpyr.Sim;

/// <summary>
/// A vest as it is worn: its tier and the points it has left (docs/LOADOUT.md §4.3).
/// Full at a spawn and when a different vest is taken at a locker (L-D10); worn down
/// by <see cref="BodyArmour.Absorb"/>; carried on the player record, seven bits of
/// points and the kit byte's two of tier (§6.1).
/// </summary>
public struct BodyArmourState
{
	public BodyArmourTier Tier;

	/// <summary>Points left, 0 to the tier's <see cref="BodyArmour.MaxPoints"/>. At 0 the vest does nothing.</summary>
	public int Points;

	public BodyArmourState(BodyArmourTier tier, int points)
	{
		Tier = tier;
		Points = Math.Clamp(points, 0, BodyArmour.MaxPoints(tier));
	}

	/// <summary>A vest of <paramref name="tier"/> with every point it has: none for no vest.</summary>
	public static BodyArmourState Full(BodyArmourTier tier) => new(tier, BodyArmour.MaxPoints(tier));
}

/// <summary>
/// What a vest does to a hit on a ground-force player (docs/LOADOUT.md L-D6, §4.3):
/// it takes its share of each bullet or swing — light 35 %, medium 50 %, heavy 65 % —
/// and half that share of a blast, up to the points it has left, and what it takes
/// comes off those points. At zero it does nothing until a weapon locker resupplies
/// it (<see cref="LockerSupply.Step"/>).
///
/// The Counter-Strike model, which a player already understands. Named apart from
/// <see cref="Armour"/>, the rule for tanks and structures, which scales what bullets
/// do and never wears out. "Explosive" is the round's, as it is there
/// (<see cref="ProjectileStats.IsExplosive"/>): a grenade's direct impact and its
/// blast are both explosive, a swing is not.
///
/// Engine-free and a pure function of its arguments: the server is the only process
/// that applies damage, but the locker panel reads its hits-to-kill off the same
/// rule (§7.2), so the two cannot disagree.
/// </summary>
public static class BodyArmour
{
	private static readonly float[] Shares = { 0f, 0.35f, 0.50f, 0.65f };
	private static readonly int[] Points = { 0, 35, 60, 100 };

	/// <summary>The share of a bullet or a swing a vest of <paramref name="tier"/> takes; 0 for none or a tier this build does not have.</summary>
	public static float Share(BodyArmourTier tier) => (byte)tier < Shares.Length ? Shares[(byte)tier] : 0f;

	/// <summary>The points a full vest of <paramref name="tier"/> has; 0 for none or a tier this build does not have.</summary>
	public static int MaxPoints(BodyArmourTier tier) => (byte)tier < Points.Length ? Points[(byte)tier] : 0;

	/// <summary>A blast is taken at this fraction of a vest's share.</summary>
	public const float ExplosiveShareScale = 0.5f;

	/// <summary>
	/// Takes a hit on <paramref name="vest"/> and returns what is left of it for the
	/// player's health. The vest takes <c>damage × share</c> — halved for an explosive —
	/// rounded to a whole point, a half point to the vest, and no more than it has
	/// left. Never negative, and never more than <paramref name="damage"/>: what the
	/// vest takes plus what it returns is the hit.
	/// </summary>
	public static float Absorb(ref BodyArmourState vest, float damage, bool explosive)
	{
		if (damage <= 0f)
		{
			return 0f;
		}

		float share = Share(vest.Tier) * (explosive ? ExplosiveShareScale : 1f);
		int wanted = (int)MathF.Round(damage * share, MidpointRounding.AwayFromZero);
		int absorbed = Math.Clamp(wanted, 0, Math.Max(vest.Points, 0));
		vest.Points -= absorbed;
		return MathF.Max(damage - absorbed, 0f);
	}

	/// <summary>
	/// Hits of <paramref name="damage"/> that kill a player from <paramref name="health"/>
	/// in a full vest of <paramref name="tier"/>, each hit's remainder rounded to whole
	/// health as <c>PlayerCombat.ApplyDamage</c> rounds it. What the locker panel shows
	/// against a rifle (§7.2) and what <c>BodyArmourTests</c> pins §4.3's table with.
	/// 0 for a hit that does nothing; capped at <paramref name="health"/> hits, which
	/// one point of health a hit reaches.
	/// </summary>
	public static int HitsToKill(BodyArmourTier tier, float damage, bool explosive, int health = SimConfig.MaxHealth)
	{
		if (damage <= 0f || health <= 0)
		{
			return 0;
		}

		BodyArmourState vest = BodyArmourState.Full(tier);
		int left = health;
		for (int hits = 1; hits <= health; hits++)
		{
			left -= (int)MathF.Round(Absorb(ref vest, damage, explosive));
			if (left <= 0)
			{
				return hits;
			}
		}

		return 0;
	}
}
