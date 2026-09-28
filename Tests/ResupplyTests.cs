using System;
using Gdpyr.Sim;
using Godot;
using Xunit;

namespace Gdpyr.Tests;

/// <summary>
/// The strategist's resupply rule (docs/HTN_BOTS.md §8, D2; H5): who is inside a
/// supply source's reach, whose sources count, when a hit stops the healing, and how
/// much a tick gives back.
/// </summary>
public class ResupplyTests
{
	private static readonly Vector3 Barracks = new(0f, 1f, 0f);
	private static readonly Vector3 Truck = new(200f, 0.5f, 0f);

	/// <summary>A barracks with a 117 m defended ring, and a truck with a 12 m reach, both the strategist's.</summary>
	private static SupplySource[] Field(ushort truckId = 40) => new[]
	{
		new SupplySource(Barracks, Resupply.BarracksReach(117f), 0, Team.Strategist),
		new SupplySource(Truck, 12f, truckId, Team.Strategist),
	};

	// ---- reach ---------------------------------------------------------------

	[Fact]
	public void InsideABarracksRing_IsResupplied_OutsideItIsNot()
	{
		Assert.True(Resupply.IsSupplied(new Vector3(116f, 0f, 0f), 7, Team.Strategist, Field()));
		Assert.False(Resupply.IsSupplied(new Vector3(0f, 0f, 118f), 7, Team.Strategist, Field()));
	}

	[Fact]
	public void TheReach_IsMeasuredFlat()
	{
		// A unit on a ridge above the ring's edge is inside it, as the ring on the ground says.
		Assert.True(Resupply.IsSupplied(new Vector3(0f, 30f, 116f), 7, Team.Strategist, Field()));
	}

	[Fact]
	public void ABarracksWithNoDefences_StillResuppliesRoundItsDoor()
	{
		Assert.Equal(Resupply.BarracksMinReachMeters, Resupply.BarracksReach(0f));
		Assert.Equal(117f, Resupply.BarracksReach(117f));

		var bare = new[] { new SupplySource(Barracks, Resupply.BarracksReach(0f), 0, Team.Strategist) };
		Assert.True(Resupply.IsSupplied(new Vector3(20f, 0f, 0f), 7, Team.Strategist, bare));
		Assert.False(Resupply.IsSupplied(new Vector3(30f, 0f, 0f), 7, Team.Strategist, bare));
	}

	[Fact]
	public void NearATruck_IsResupplied()
	{
		Assert.True(Resupply.IsSupplied(Truck + new Vector3(0f, 0f, 11.5f), 7, Team.Strategist, Field()));
		Assert.False(Resupply.IsSupplied(Truck + new Vector3(0f, 0f, 12.5f), 7, Team.Strategist, Field()));
	}

	[Fact]
	public void ATruck_DoesNotResupplyItself_ButAnotherTruckDoes()
	{
		Assert.False(Resupply.IsSupplied(Truck, 40, Team.Strategist, Field(truckId: 40)));

		SupplySource[] two =
		{
			new(Truck, 12f, 40, Team.Strategist),
			new(Truck + new Vector3(5f, 0f, 0f), 12f, 41, Team.Strategist),
		};
		Assert.True(Resupply.IsSupplied(Truck, 40, Team.Strategist, two));
	}

	[Fact]
	public void TheOtherSidesSources_ResupplyNobody()
	{
		var theirs = new[] { new SupplySource(Barracks, 117f, 0, Team.GroundForce) };
		Assert.False(Resupply.IsSupplied(Barracks, 7, Team.Strategist, theirs));
		Assert.Equal(-1, Resupply.Nearest(Barracks, 7, Team.Strategist, theirs, out _));
	}

	[Fact]
	public void TheNearestSource_IsTheOneWhoseEdgeIsNearest_NotWhoseMiddleIs()
	{
		// 150 m from the barracks is 33 m outside its ring; 50 m from the truck is 38 m
		// outside its reach.
		Vector3 at = new(150f, 0f, 0f);
		int nearest = Resupply.Nearest(at, 7, Team.Strategist, Field(), out float edge);

		Assert.Equal(0, nearest);
		Assert.Equal(33f, edge, 3);
	}

	[Fact]
	public void TheEdgeDistance_IsNegativeInside()
	{
		Assert.Equal(-2f, Resupply.EdgeDistance(Truck + new Vector3(10f, 0f, 0f), Field()[1]), 3);
	}

	[Fact]
	public void NoSources_IsNoneNearest()
	{
		Assert.Equal(-1, Resupply.Nearest(Vector3.Zero, 7, Team.Strategist, ReadOnlySpan<SupplySource>.Empty,
			out float edge));
		Assert.Equal(float.MaxValue, edge);
	}

	// ---- the hit that stops it -----------------------------------------------

	[Fact]
	public void AUnitNeverHit_Heals()
	{
		Assert.True(Resupply.CanHeal(tick: 5000, lastDamagedTick: 0));
	}

	[Fact]
	public void AUnitHitWithinThreeSeconds_DoesNotHeal_ThenDoes()
	{
		const uint hit = 5000;
		Assert.False(Resupply.CanHeal(hit, hit));
		Assert.False(Resupply.CanHeal(hit + (uint)Resupply.DamageLockoutTicks - 1, hit));
		Assert.True(Resupply.CanHeal(hit + (uint)Resupply.DamageLockoutTicks, hit));
	}

	[Fact]
	public void ATickFromBeforeTheHit_IsNotAWrapAround()
	{
		// A round reset puts the clock where it is; it never makes a unit heal early
		// because an unsigned difference wrapped.
		Assert.True(Resupply.CanHeal(100, 5000));
	}

	// ---- how much ------------------------------------------------------------

	[Fact]
	public void ATick_GivesBackTheUnitsRate()
	{
		Assert.Equal(50f + (5f / SimConfig.TickRate), Resupply.Heal(50f, 100f, 5f, SimConfig.TickDelta), 4);
	}

	[Fact]
	public void ARiflemanAtHalfHealth_IsFullAfterTenSeconds_AndNoMore()
	{
		float health = 50f;
		for (int t = 0; t < SimConfig.TickRate * 10; t++)
		{
			health = Resupply.Heal(health, 100f, 5f, SimConfig.TickDelta);
		}

		Assert.Equal(100f, health, 2);

		for (int t = 0; t < SimConfig.TickRate; t++)
		{
			health = Resupply.Heal(health, 100f, 5f, SimConfig.TickDelta);
		}

		Assert.Equal(100f, health);
	}

	[Theory]
	[InlineData(0f, 100f, 5f)]
	[InlineData(100f, 100f, 5f)]
	[InlineData(40f, 100f, 0f)]
	public void TheDead_TheWhole_AndTheUnhealable_AreUnchanged(float health, float max, float regen)
	{
		Assert.Equal(health, Resupply.Heal(health, max, regen, SimConfig.TickDelta));
	}

	[Fact]
	public void CheckingAFullField_AllocatesNothing()
	{
		SupplySource[] field = new SupplySource[SimConfig.MaxBarracks + SimConfig.MaxUnits];
		for (int i = 0; i < field.Length; i++)
		{
			field[i] = new SupplySource(new Vector3(i * 30f, 0f, 0f), 12f, (ushort)(i + 1), Team.Strategist);
		}

		int supplied = 0;
		long before = GC.GetAllocatedBytesForCurrentThread();
		for (int u = 0; u < SimConfig.MaxUnits; u++)
		{
			supplied += Resupply.IsSupplied(new Vector3(u * 31f, 0f, 0f), (ushort)(u + 1), Team.Strategist, field) ? 1 : 0;
		}

		Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
		Assert.True(supplied > 0);
	}
}
