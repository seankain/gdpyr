using Gdpyr.Sim;
using Xunit;

namespace Gdpyr.Tests;

/// <summary>
/// The one rule for taking a kit at a weapon locker (docs/LOADOUT.md L-D9, §5.3):
/// every refusal, the latency slack, and the quarter-second between kits.
/// </summary>
public class KitRequestTests
{
	private const uint Now = 10_000;

	private static KitSituation At(float distance = 0.5f, bool alive = true, bool mounted = false,
		CarryKind carrying = CarryKind.None, bool valid = true, uint tick = Now, uint lastKit = 0) =>
		new(alive, mounted, carrying, distance, valid, tick, lastKit);

	[Fact]
	public void ALivingPlayerAtTheLockerWithEmptyHandsTakesAValidKit()
	{
		Assert.Equal(KitRefusal.None, KitRequest.Check(At()));
		Assert.Equal(KitRefusal.None, KitRequest.Check(At(distance: 0f)));
	}

	[Fact]
	public void TheDeadAndTheStrategistTakeNothing()
	{
		// A strategist is never alive (PlayerCombat.IsAlive), so they are refused the same way.
		Assert.Equal(KitRefusal.Dead, KitRequest.Check(At(alive: false)));
	}

	[Fact]
	public void HandsOnAGunOrSomethingOnTheBackTakeNothing()
	{
		Assert.Equal(KitRefusal.Mounted, KitRequest.Check(At(mounted: true)));
		Assert.Equal(KitRefusal.Carrying, KitRequest.Check(At(carrying: CarryKind.Gun)));
		Assert.Equal(KitRefusal.Carrying, KitRequest.Check(At(carrying: CarryKind.AmmoCan)));
	}

	[Fact]
	public void AKitSanitizeWouldChangeIsRefusedNotTrimmed()
	{
		Assert.Equal(KitRefusal.Invalid, KitRequest.Check(At(valid: false)));
	}

	[Fact]
	public void TheReachIsAMetreFromTheFootprintPlusHalfAMetreOfSlack()
	{
		Assert.Equal(1f, SimConfig.LockerReachMeters);
		Assert.Equal(0.5f, KitRequest.SlackMeters);

		Assert.Equal(KitRefusal.None, KitRequest.Check(At(distance: 1f)));
		Assert.Equal(KitRefusal.None, KitRequest.Check(At(distance: 1.49f)));
		Assert.Equal(KitRefusal.None, KitRequest.Check(At(distance: 1.5f)));
		Assert.Equal(KitRefusal.NotAtLocker, KitRequest.Check(At(distance: 1.51f)));
		Assert.Equal(KitRefusal.NotAtLocker, KitRequest.Check(At(distance: 3f)));
	}

	[Fact]
	public void AMapWithoutALockerIsNeverAtOne()
	{
		Assert.Equal(KitRefusal.NotAtLocker, KitRequest.Check(At(distance: float.PositiveInfinity)));
		Assert.Equal(KitRefusal.NotAtLocker, KitRequest.Check(At(distance: float.NaN)));
	}

	[Fact]
	public void OneKitAQuarterSecond()
	{
		Assert.Equal(SimConfig.TickRate / 4, KitRequest.MinIntervalTicks);

		uint interval = (uint)KitRequest.MinIntervalTicks;
		Assert.Equal(KitRefusal.TooSoon, KitRequest.Check(At(lastKit: Now)));
		Assert.Equal(KitRefusal.TooSoon, KitRequest.Check(At(lastKit: Now - interval + 1)));
		Assert.Equal(KitRefusal.None, KitRequest.Check(At(lastKit: Now - interval)));
	}

	[Fact]
	public void NeverHavingTakenOneIsNotTooSoon()
	{
		Assert.Equal(KitRefusal.None, KitRequest.Check(At(tick: 3, lastKit: 0)));
	}

	[Fact]
	public void ALastKitAheadOfTheClockIsNotTooSoon()
	{
		// The server's tick only goes forward; this is the guard against a wrapped
		// subtraction reading as "a moment ago" rather than a refusal anybody meant.
		Assert.Equal(KitRefusal.None, KitRequest.Check(At(tick: 100, lastKit: 200)));
	}

	[Fact]
	public void ARequestThatCouldNeverBeHonouredIsRefusedAsSuchFirst()
	{
		Assert.Equal(KitRefusal.Invalid,
			KitRequest.Check(At(distance: 9f, alive: false, mounted: true, valid: false, lastKit: Now)));
		Assert.Equal(KitRefusal.Dead,
			KitRequest.Check(At(distance: 9f, alive: false, mounted: true, lastKit: Now)));
		Assert.Equal(KitRefusal.Mounted,
			KitRequest.Check(At(distance: 9f, mounted: true, carrying: CarryKind.Gun, lastKit: Now)));
		Assert.Equal(KitRefusal.Carrying, KitRequest.Check(At(distance: 9f, carrying: CarryKind.Gun, lastKit: Now)));
		Assert.Equal(KitRefusal.NotAtLocker, KitRequest.Check(At(distance: 9f, lastKit: Now)));
	}

	[Fact]
	public void EveryRefusalHasTheCodeTheProtocolNamesIt()
	{
		Assert.Equal("applied", KitRequest.CodeOf(KitRefusal.None));
		Assert.Equal("dead", KitRequest.CodeOf(KitRefusal.Dead));
		Assert.Equal("not_at_locker", KitRequest.CodeOf(KitRefusal.NotAtLocker));
		Assert.Equal("mounted", KitRequest.CodeOf(KitRefusal.Mounted));
		Assert.Equal("carrying", KitRequest.CodeOf(KitRefusal.Carrying));
		Assert.Equal("invalid", KitRequest.CodeOf(KitRefusal.Invalid));
		Assert.Equal("too_soon", KitRequest.CodeOf(KitRefusal.TooSoon));
		Assert.Equal("unknown", KitRequest.CodeOf((KitRefusal)200));
	}

	[Fact]
	public void ARefusalIsReadOffTheWireOnlyWhenThisBuildHasIt()
	{
		Assert.True(KitRequest.TryRead(2, out KitRefusal refusal));
		Assert.Equal(KitRefusal.NotAtLocker, refusal);
		Assert.True(KitRequest.TryRead(0, out refusal));
		Assert.Equal(KitRefusal.None, refusal);
		Assert.False(KitRequest.TryRead(7, out _));
		Assert.False(KitRequest.TryRead(255, out _));
	}
}
