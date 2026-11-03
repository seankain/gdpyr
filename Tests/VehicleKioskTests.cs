using System;
using Gdpyr.Sim;
using Xunit;

namespace Gdpyr.Tests;

/// <summary>
/// The vehicle kiosk's timer, its one validator and the rule for a vehicle left alone
/// (docs/GROUND_VEHICLES.md §5.5, GV-D13).
/// </summary>
public class VehicleKioskTests
{
	private const int Second = SimConfig.TickRate;

	private static KioskTimer FourByFour() => KioskTimer.FromSeconds(45f, 1);

	// ---- the timer ------------------------------------------------------------------------

	[Fact]
	public void BothKinds_AreReadyWhenTheRoundStarts()
	{
		Assert.Equal(KioskStatus.Ready, FourByFour().Status(0, 0));
		Assert.Equal(KioskStatus.Ready, KioskTimer.FromSeconds(90f, 1).Status(0, 0));
		Assert.Equal(0, FourByFour().SecondsLeft(0));
	}

	[Fact]
	public void TheTimer_RunsFromTheLoss_NotTheSpawn()
	{
		KioskTimer timer = FourByFour();
		timer.OnSpawned(0);
		Assert.Equal(KioskStatus.Out, timer.Status(0, 1));

		// Kept for a minute — longer than the timer — and then lost.
		uint lost = (uint)(60 * Second);
		Assert.Equal(KioskStatus.Out, timer.Status(lost - 1, 1));
		timer.OnLost(lost);

		Assert.Equal(KioskStatus.Counting, timer.Status(lost, 0));
		Assert.Equal(45, timer.SecondsLeft(lost));
		Assert.Equal(KioskStatus.Counting, timer.Status(lost + (uint)(45 * Second) - 1, 0));
		Assert.Equal(1, timer.SecondsLeft(lost + (uint)(45 * Second) - 1));
		Assert.Equal(KioskStatus.Ready, timer.Status(lost + (uint)(45 * Second), 0));
		Assert.Equal(1, timer.Spawned);
		Assert.Equal(1, timer.Lost);
	}

	[Fact]
	public void AtTheCap_TheKindIsOut_WhateverTheTimerSays()
	{
		KioskTimer timer = FourByFour();
		Assert.Equal(KioskStatus.Out, timer.Status(0, 1));
		Assert.Equal(KioskStatus.Out, timer.Status(0, 2));

		var two = new KioskTimer(10, 2);
		Assert.Equal(KioskStatus.Ready, two.Status(0, 1));
		Assert.Equal(KioskStatus.Out, two.Status(0, 2));
	}

	[Fact]
	public void ADisabledKind_IsDisabled_AndAResetKeepsIt()
	{
		KioskTimer timer = FourByFour();
		timer.SetDisabled(true);
		Assert.Equal(KioskStatus.Disabled, timer.Status(0, 0));

		timer.OnLost(100);
		timer.Reset();
		Assert.Equal(KioskStatus.Disabled, timer.Status(0, 0));
		Assert.Equal(0, timer.Lost);
	}

	[Fact]
	public void ANewRound_MakesItReadyAgain()
	{
		KioskTimer timer = FourByFour();
		timer.OnLost(1000);
		Assert.Equal(KioskStatus.Counting, timer.Status(1001, 0));

		timer.Reset();
		Assert.Equal(KioskStatus.Ready, timer.Status(1001, 0));
	}

	// ---- the validator --------------------------------------------------------------------

	/// <summary>A request that is honoured; each argument named breaks one gate.</summary>
	private static KioskSituation Asking(bool alive = true, bool ground = true, float distance = 0.5f,
		bool seated = false, bool mounted = false, CarryKind carrying = CarryKind.None, bool piloting = false,
		bool known = true, KioskStatus status = KioskStatus.Ready, bool padClear = true, uint tick = 1000,
		uint last = 0) =>
		new(alive, ground, distance, seated, mounted, carrying, piloting, known, status, padClear, tick, last);

	[Fact]
	public void ARequestThatPassesEveryGate_IsTaken()
	{
		Assert.Equal(KioskRefusal.None, KioskRules.Check(Asking()));
		Assert.Equal("taken", KioskRules.CodeOf(KioskRefusal.None));
	}

	[Fact]
	public void EveryRefusal_IsGiven_InOrder()
	{
		// The situation for refusal i fails gate i and every gate after it, so the answer
		// is i exactly when the gates are asked in the order §5.5 writes.
		KioskRefusal[] order =
		{
			KioskRefusal.Dead, KioskRefusal.NotGround, KioskRefusal.NotAtKiosk, KioskRefusal.Seated,
			KioskRefusal.Mounted, KioskRefusal.Carrying, KioskRefusal.Piloting, KioskRefusal.Invalid,
			KioskRefusal.Out, KioskRefusal.NotReady, KioskRefusal.PadBlocked, KioskRefusal.TooSoon,
		};

		for (int i = 0; i < order.Length; i++)
		{
			bool Fails(KioskRefusal r) => Array.IndexOf(order, r) >= i;
			KioskStatus status = Fails(KioskRefusal.Invalid) ? KioskStatus.Disabled
				: Fails(KioskRefusal.Out) ? KioskStatus.Out
				: Fails(KioskRefusal.NotReady) ? KioskStatus.Counting
				: KioskStatus.Ready;

			KioskSituation situation = Asking(
				alive: !Fails(KioskRefusal.Dead),
				ground: !Fails(KioskRefusal.NotGround),
				distance: Fails(KioskRefusal.NotAtKiosk) ? 3f : 0.5f,
				seated: Fails(KioskRefusal.Seated),
				mounted: Fails(KioskRefusal.Mounted),
				carrying: Fails(KioskRefusal.Carrying) ? CarryKind.AmmoCan : CarryKind.None,
				piloting: Fails(KioskRefusal.Piloting),
				known: !Fails(KioskRefusal.Invalid),
				status: status,
				padClear: !Fails(KioskRefusal.PadBlocked),
				tick: 1000,
				last: Fails(KioskRefusal.TooSoon) ? 995u : 0u);

			Assert.Equal(order[i], KioskRules.Check(situation));
		}
	}

	[Fact]
	public void EachRefusal_OnItsOwn()
	{
		Assert.Equal(KioskRefusal.Dead, KioskRules.Check(Asking(alive: false)));
		Assert.Equal(KioskRefusal.NotGround, KioskRules.Check(Asking(ground: false)));
		Assert.Equal(KioskRefusal.NotAtKiosk, KioskRules.Check(Asking(distance: 1.51f)));
		Assert.Equal(KioskRefusal.NotAtKiosk, KioskRules.Check(Asking(distance: float.PositiveInfinity)));
		Assert.Equal(KioskRefusal.NotAtKiosk, KioskRules.Check(Asking(distance: float.NaN)));
		Assert.Equal(KioskRefusal.Seated, KioskRules.Check(Asking(seated: true)));
		Assert.Equal(KioskRefusal.Mounted, KioskRules.Check(Asking(mounted: true)));
		Assert.Equal(KioskRefusal.Carrying, KioskRules.Check(Asking(carrying: CarryKind.Gun)));
		Assert.Equal(KioskRefusal.Piloting, KioskRules.Check(Asking(piloting: true)));
		Assert.Equal(KioskRefusal.Invalid, KioskRules.Check(Asking(known: false)));
		Assert.Equal(KioskRefusal.Invalid, KioskRules.Check(Asking(status: KioskStatus.Disabled)));
		Assert.Equal(KioskRefusal.Out, KioskRules.Check(Asking(status: KioskStatus.Out)));
		Assert.Equal(KioskRefusal.NotReady, KioskRules.Check(Asking(status: KioskStatus.Counting)));
		Assert.Equal(KioskRefusal.PadBlocked, KioskRules.Check(Asking(padClear: false)));
		Assert.Equal(KioskRefusal.TooSoon, KioskRules.Check(Asking(tick: 1000, last: 1000 - 14)));
		Assert.Equal(KioskRefusal.None, KioskRules.Check(Asking(tick: 1000, last: 1000 - 15)));
	}

	[Fact]
	public void TheKiosksReach_IsTheLockersMetre_PlusItsHalfMetreOfSlack()
	{
		Assert.Equal(KioskRefusal.None, KioskRules.Check(Asking(distance: 1.5f)));
		Assert.True(KioskRules.InReach(1f));
		Assert.False(KioskRules.InReach(1.01f));
	}

	[Fact]
	public void EveryRefusal_HasACode_AndReadsBackOffTheWire()
	{
		string[] codes =
		{
			"taken", "dead", "not_ground", "not_at_kiosk", "seated", "mounted", "carrying", "piloting", "invalid",
			"out", "not_ready", "pad_blocked", "too_soon",
		};

		for (int i = 0; i < codes.Length; i++)
		{
			Assert.Equal(codes[i], KioskRules.CodeOf((KioskRefusal)i));
			Assert.True(KioskRules.TryRead((byte)i, out KioskRefusal refusal));
			Assert.Equal((KioskRefusal)i, refusal);
		}

		Assert.False(KioskRules.TryRead((byte)codes.Length, out _));
		Assert.Equal("unknown", KioskRules.CodeOf((KioskRefusal)200));
	}

	// ---- abandonment ----------------------------------------------------------------------

	[Fact]
	public void AVehicleEmpty_WithNobodyWithin15Metres_For60Seconds_IsAbandoned()
	{
		var clock = new AbandonClock();
		for (int s = 0; s < 60; s++)
		{
			Assert.False(clock.Check((uint)(1000 + (s * Second)), 0, 40f), $"abandoned after {s} s");
		}

		Assert.True(clock.Check((uint)(1000 + (60 * Second)), 0, 40f));
		Assert.True(clock.Check((uint)(1000 + (60 * Second)), 0, float.PositiveInfinity));
	}

	[Fact]
	public void SomebodyInIt_IsNeverAbandoned()
	{
		var clock = new AbandonClock();
		for (int s = 0; s <= 120; s++)
		{
			Assert.False(clock.Check((uint)(s * Second), 1, float.PositiveInfinity));
		}
	}

	[Fact]
	public void SomebodyWithin15Metres_IsNeverAbandoned_AndStartsTheMinuteAgain()
	{
		var clock = new AbandonClock();
		for (int s = 0; s <= 120; s++)
		{
			Assert.False(clock.Check((uint)(s * Second), 0, 15f));
		}

		// Fifty seconds left alone, then somebody walks past, then fifty more: not yet.
		for (int s = 121; s <= 171; s++)
		{
			Assert.False(clock.Check((uint)(s * Second), 0, 30f));
		}

		Assert.False(clock.Check((uint)(172 * Second), 0, 10f));
		for (int s = 173; s < 233; s++)
		{
			Assert.False(clock.Check((uint)(s * Second), 0, 30f));
		}

		Assert.True(clock.Check((uint)(233 * Second), 0, 30f));
	}
}
