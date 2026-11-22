using System;
using Gdpyr.Sim;
using Xunit;

namespace Gdpyr.Tests;

/// <summary>A map's kill plane (docs/MAPS.md §3.2): what is below it, and what it deals.</summary>
public class KillPlaneTests
{
	[Theory]
	[InlineData(0.49f, 0.5f, true)]
	[InlineData(-30f, 0.5f, true)]
	[InlineData(0.5f, 0.5f, false)]
	[InlineData(70f, 0.5f, false)]
	[InlineData(-1000f, KillPlane.None, false)]
	[InlineData(float.NaN, 0.5f, false)]
	[InlineData(0f, float.NaN, false)]
	[InlineData(0f, float.PositiveInfinity, false)]
	public void BelowIsStrictlyUnderAPlaneThereIs(float y, float plane, bool below)
	{
		Assert.Equal(below, KillPlane.IsBelow(y, plane));
	}

	[Theory]
	[InlineData(BodyArmourTier.None)]
	[InlineData(BodyArmourTier.Light)]
	[InlineData(BodyArmourTier.Medium)]
	[InlineData(BodyArmourTier.Heavy)]
	public void ItKillsThroughAFullVestOfAnyTier(BodyArmourTier tier)
	{
		// What CombatManager.Damage does with it: the vest its share, the rest to health,
		// rounded as PlayerCombat.ApplyDamage rounds it.
		for (int health = 1; health <= SimConfig.MaxHealth; health++)
		{
			BodyArmourState vest = BodyArmourState.Full(tier);
			float lost = BodyArmour.Absorb(ref vest, KillPlane.LethalDamage(health, vest.Points), explosive: false);
			Assert.True(health - (int)MathF.Round(lost) <= 0, $"{tier} vest, {health} health: {lost} reached it");
		}
	}

	[Fact]
	public void ItNeverDealsNothing()
	{
		Assert.True(KillPlane.LethalDamage(0, 0) > 0f);
		Assert.True(KillPlane.LethalDamage(-5, -5) > 0f);
	}
}
