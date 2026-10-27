using System.Collections.Generic;
using Gdpyr.Sim;
using Xunit;

namespace Gdpyr.Tests;

/// <summary>
/// What a vest does to a hit (docs/LOADOUT.md L-D6, §4.3): its share of a bullet or a
/// swing, half that of a blast, up to the points it has left.
/// </summary>
public class BodyArmourTests
{
	// The rounds of §4.3's table, as the .tres files author them: the rifle's and the
	// heavy gunner's LMG's 5.56, the turret's, pillbox's and technical's 12.7, the
	// DMR's, marksman's and sniper's 7.62, and a hand grenade's 140 at 3 m of its 7.
	private const float Rifle = 20f;
	private const float HeavyRound = 45f;
	private const float Dmr = 60f;
	private const float HandGrenadeAt3m = 140f * (1f - (3f / 7f));

	[Theory]
	// §4.3: hits to kill a still player from full health and a full vest.
	[InlineData(Rifle, false, 5, 7, 8, 10)]
	[InlineData(HeavyRound, false, 3, 3, 4, 5)]
	[InlineData(Dmr, false, 2, 3, 3, 4)]
	[InlineData(HandGrenadeAt3m, true, 2, 2, 2, 2)]
	public void TheHitsToKillTableHolds(float damage, bool explosive, int none, int light, int medium, int heavy)
	{
		Assert.Equal(none, BodyArmour.HitsToKill(BodyArmourTier.None, damage, explosive));
		Assert.Equal(light, BodyArmour.HitsToKill(BodyArmourTier.Light, damage, explosive));
		Assert.Equal(medium, BodyArmour.HitsToKill(BodyArmourTier.Medium, damage, explosive));
		Assert.Equal(heavy, BodyArmour.HitsToKill(BodyArmourTier.Heavy, damage, explosive));
	}

	[Theory]
	// The table's rounds are the ones the game fires, so the table cannot drift from them.
	[InlineData("rifle_556", Rifle)]
	[InlineData("hmg_127", HeavyRound)]
	[InlineData("dmr_762", Dmr)]
	public void TheTablesRoundsAreTheAuthoredOnes(string projectile, float damage) =>
		Assert.Equal(damage, TrainedUnitResourceTests.Float(
			TrainedUnitResourceTests.Resource($"Weapons/projectiles/{projectile}.tres"), "Damage"));

	[Fact]
	public void AHandGrenadesBlastIsEightyAtThreeMetres()
	{
		Dictionary<string, string> file = TrainedUnitResourceTests.Resource("Weapons/projectiles/hand_grenade.tres");
		var grenade = new ProjectileStats(default, 18f, 0f, TrainedUnitResourceTests.Float(file, "ExplosionRadiusMeters"),
			TrainedUnitResourceTests.Float(file, "ExplosionDamage"));
		Assert.True(grenade.IsExplosive);
		Assert.Equal(HandGrenadeAt3m, grenade.SplashDamageAt(3f), 3);
		Assert.Equal(80f, HandGrenadeAt3m, 3);
	}

	[Theory]
	[InlineData(BodyArmourTier.None, 0f, 0)]
	[InlineData(BodyArmourTier.Light, 0.35f, 35)]
	[InlineData(BodyArmourTier.Medium, 0.50f, 60)]
	[InlineData(BodyArmourTier.Heavy, 0.65f, 100)]
	public void EachTierHasItsShareAndItsPoints(BodyArmourTier tier, float share, int points)
	{
		Assert.Equal(share, BodyArmour.Share(tier));
		Assert.Equal(points, BodyArmour.MaxPoints(tier));
		Assert.Equal(new BodyArmourState(tier, points), BodyArmourState.Full(tier));
	}

	[Fact]
	public void EveryVestsPointsFitTheRecord()
	{
		for (var tier = BodyArmourTier.None; tier <= BodyArmourTier.Heavy; tier++)
		{
			Assert.InRange(BodyArmour.MaxPoints(tier), 0, KitFlags.MaxArmourPoints);
		}
	}

	[Fact]
	public void AVestTakesItsShareAndTheRestReachesHealth()
	{
		BodyArmourState vest = BodyArmourState.Full(BodyArmourTier.Medium);
		Assert.Equal(10f, BodyArmour.Absorb(ref vest, Rifle, explosive: false));
		Assert.Equal(50, vest.Points);

		vest = BodyArmourState.Full(BodyArmourTier.Heavy);
		Assert.Equal(7f, BodyArmour.Absorb(ref vest, Rifle, explosive: false));
		Assert.Equal(87, vest.Points);
	}

	[Fact]
	public void ABlastIsTakenAtHalfTheShare()
	{
		BodyArmourState vest = BodyArmourState.Full(BodyArmourTier.Medium);
		Assert.Equal(60f, BodyArmour.Absorb(ref vest, 80f, explosive: true));
		Assert.Equal(40, vest.Points);

		BodyArmourState bullet = BodyArmourState.Full(BodyArmourTier.Medium);
		Assert.Equal(40f, BodyArmour.Absorb(ref bullet, 80f, explosive: false));
		Assert.Equal(20, bullet.Points);
	}

	[Fact]
	public void AVestNeverTakesMoreThanItHasLeft()
	{
		var vest = new BodyArmourState(BodyArmourTier.Heavy, 9);
		Assert.Equal(11f, BodyArmour.Absorb(ref vest, Rifle, explosive: false));
		Assert.Equal(0, vest.Points);
	}

	[Fact]
	public void AVestWithNoPointsLeftTakesNothing()
	{
		var vest = new BodyArmourState(BodyArmourTier.Heavy, 0);
		Assert.Equal(Rifle, BodyArmour.Absorb(ref vest, Rifle, explosive: false));
		Assert.Equal(Dmr, BodyArmour.Absorb(ref vest, Dmr, explosive: false));
		Assert.Equal(0, vest.Points);
	}

	[Fact]
	public void NoVestTakesNothing()
	{
		BodyArmourState vest = BodyArmourState.Full(BodyArmourTier.None);
		Assert.Equal(Rifle, BodyArmour.Absorb(ref vest, Rifle, explosive: false));
		Assert.Equal(0, vest.Points);
	}

	[Fact]
	public void AHalfPointGoesToTheVest()
	{
		// 45 × 0.5 = 22.5: the vest takes 23, not the 22 rounding to even would give it.
		BodyArmourState vest = BodyArmourState.Full(BodyArmourTier.Medium);
		Assert.Equal(22f, BodyArmour.Absorb(ref vest, HeavyRound, explosive: false));
		Assert.Equal(37, vest.Points);
	}

	[Theory]
	[InlineData(0f)]
	[InlineData(-20f)]
	public void NothingOrLessIsNothing(float damage)
	{
		BodyArmourState vest = BodyArmourState.Full(BodyArmourTier.Heavy);
		Assert.Equal(0f, BodyArmour.Absorb(ref vest, damage, explosive: false));
		Assert.Equal(0f, BodyArmour.Absorb(ref vest, damage, explosive: true));
		Assert.Equal(100, vest.Points);
		Assert.Equal(0, BodyArmour.HitsToKill(BodyArmourTier.Heavy, damage, explosive: false));
	}

	[Theory]
	[InlineData(0.4f)]
	[InlineData(1f)]
	[InlineData(Rifle)]
	[InlineData(HeavyRound)]
	[InlineData(Dmr)]
	[InlineData(500f)]
	public void WhatTheVestTakesAndWhatItReturnsAreTheHit(float damage)
	{
		for (var tier = BodyArmourTier.None; tier <= BodyArmourTier.Heavy; tier++)
		{
			foreach (bool explosive in new[] { false, true })
			{
				BodyArmourState vest = BodyArmourState.Full(tier);
				int before = vest.Points;
				float health = BodyArmour.Absorb(ref vest, damage, explosive);

				Assert.InRange(health, 0f, damage);
				Assert.InRange(vest.Points, 0, before);
				Assert.Equal(damage, health + (before - vest.Points), 3);
			}
		}
	}

	[Fact]
	public void AVestThatWouldTakeAWholeHitStillLeavesItsShareOfTheRest()
	{
		// A hit smaller than a point rounds to nothing for the vest, and reaches health whole.
		BodyArmourState vest = BodyArmourState.Full(BodyArmourTier.Light);
		Assert.Equal(1f, BodyArmour.Absorb(ref vest, 1f, explosive: false));
		Assert.Equal(35, vest.Points);
	}

	[Fact]
	public void AStateIsClampedToItsTiersPoints()
	{
		Assert.Equal(35, new BodyArmourState(BodyArmourTier.Light, 200).Points);
		Assert.Equal(0, new BodyArmourState(BodyArmourTier.Heavy, -5).Points);
		Assert.Equal(0, new BodyArmourState(BodyArmourTier.None, 50).Points);
		Assert.Equal(0, new BodyArmourState((BodyArmourTier)7, 50).Points);
		Assert.Equal(0f, BodyArmour.Share((BodyArmourTier)7));
	}

	[Fact]
	public void TheSameHitsOnTheSameVestCostTheSame()
	{
		BodyArmourState a = BodyArmourState.Full(BodyArmourTier.Heavy);
		BodyArmourState b = BodyArmourState.Full(BodyArmourTier.Heavy);
		float[] hits = { Rifle, 63.7f, HeavyRound, 12.25f, Dmr };
		for (int i = 0; i < hits.Length; i++)
		{
			bool explosive = i % 2 == 1;
			Assert.Equal(BodyArmour.Absorb(ref a, hits[i], explosive), BodyArmour.Absorb(ref b, hits[i], explosive));
			Assert.Equal(a.Points, b.Points);
		}
	}

	[Fact]
	public void AHeavierVestNeverDiesSooner()
	{
		foreach (float damage in new[] { 5f, Rifle, 25f, HeavyRound, Dmr, 80f, 90f, 140f })
		{
			foreach (bool explosive in new[] { false, true })
			{
				int previous = 0;
				for (var tier = BodyArmourTier.None; tier <= BodyArmourTier.Heavy; tier++)
				{
					int hits = BodyArmour.HitsToKill(tier, damage, explosive);
					Assert.True(hits >= previous, $"{damage} {explosive} {tier}");
					previous = hits;
				}
			}
		}
	}
}
