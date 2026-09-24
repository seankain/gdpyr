using Gdpyr.Sim;
using Xunit;

namespace Gdpyr.Tests;

/// <summary>
/// What a hit does to a tank, a pillbox, a sniper tower and a wall of sandbags
/// (docs/NETCODE.md §10.6): explosives do the whole of it, bullets a share, and at a
/// share of zero, nothing.
/// </summary>
public class ArmourTests
{
	[Fact]
	public void BulletsAreScaledAndExplosivesAreNot()
	{
		Assert.Equal(5f, Armour.DamageTaken(20f, explosive: false, bulletScale: 0.25f), 4);
		Assert.Equal(20f, Armour.DamageTaken(20f, explosive: true, bulletScale: 0.25f), 4);
		Assert.Equal(0f, Armour.DamageTaken(-3f, explosive: true, bulletScale: 1f));
		Assert.Equal(0f, Armour.DamageTaken(20f, explosive: false, bulletScale: -1f));
	}

	[Theory]
	[InlineData(20f)]  // a rifle round
	[InlineData(45f)]  // the heavy gun's 12.7 mm: not small arms, and not an explosive either
	[InlineData(60f)]  // a DMR round
	public void ArmourTakesNothingFromABullet(float amount)
	{
		Assert.Equal(0f, Armour.DamageTaken(amount, explosive: false, bulletScale: 0f));
	}

	[Theory]
	[InlineData(40f)]  // a grenade's direct impact
	[InlineData(90f)]  // its blast, at the centre
	[InlineData(12.5f)] // its blast, near the edge
	public void ArmourTakesAllOfAnExplosive(float amount)
	{
		Assert.Equal(amount, Armour.DamageTaken(amount, explosive: true, bulletScale: 0f));
	}

	[Fact]
	public void SoftTargetsTakeAllOfEither()
	{
		Assert.Equal(20f, Armour.DamageTaken(20f, explosive: false, bulletScale: 1f));
		Assert.Equal(20f, Armour.DamageTaken(20f, explosive: true, bulletScale: 1f));
	}

	[Theory]
	[InlineData(0f, true)]
	[InlineData(-0.5f, true)]
	[InlineData(0.3f, false)]
	[InlineData(1f, false)]
	public void BulletProofMeansAScaleOfZeroOrLess(float bulletScale, bool bulletProof)
	{
		Assert.Equal(bulletProof, Armour.IsBulletProof(bulletScale));
	}

	[Fact]
	public void OnlyAnExplosiveCanHurtArmour()
	{
		// What a ground bot asks before it picks a target: a rifleman passes a tank
		// over, a grenadier does not, and neither passes over a rifleman.
		Assert.False(Armour.CanHurt(explosive: false, bulletScale: 0f));
		Assert.True(Armour.CanHurt(explosive: true, bulletScale: 0f));
		Assert.True(Armour.CanHurt(explosive: false, bulletScale: 1f));
		Assert.True(Armour.CanHurt(explosive: false, bulletScale: 0.3f));
	}
}
