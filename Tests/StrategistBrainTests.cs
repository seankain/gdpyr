using Gdpyr.Sim;
using Xunit;

namespace Gdpyr.Tests;

public class StrategistBrainTests
{
	/// <summary>The infantry's cost, from <c>Units/infantry.tres</c>.</summary>
	private const int InfantryCost = 50;

	private static readonly StrategistTraits Traits = StrategistTraits.Default;

	// ---- the economy -------------------------------------------------------

	[Fact]
	public void QueuesWhileThePointsLast()
	{
		Assert.True(StrategistBrain.ShouldQueue(balance: 1000, InfantryCost, queued: 0, liveUnits: 0, Traits,
			SimConfig.MaxUnits));
	}

	[Fact]
	public void DoesNotQueueWhatItCannotAfford()
	{
		// A strategist that queued on credit would be a strategist for whom the whole
		// economy is decoration (BuildQueue charges on order, not on completion).
		Assert.False(StrategistBrain.ShouldQueue(balance: 49, InfantryCost, queued: 0, liveUnits: 0, Traits,
			SimConfig.MaxUnits));
	}

	[Fact]
	public void DoesNotCommitTheWholeBalanceToOneWave()
	{
		Assert.False(StrategistBrain.ShouldQueue(balance: 1000, InfantryCost, queued: Traits.QueueDepth,
			liveUnits: 0, Traits, SimConfig.MaxUnits));
	}

	[Fact]
	public void DoesNotQueuePastTheFieldCap()
	{
		// Past the cap the manager refunds the unit a tick later; queueing into that
		// is a busy loop that spends and unspends the same points forever.
		Assert.False(StrategistBrain.ShouldQueue(balance: 1000, InfantryCost, queued: 0,
			liveUnits: SimConfig.MaxUnits, Traits, SimConfig.MaxUnits));

		Assert.False(StrategistBrain.ShouldQueue(balance: 1000, InfantryCost, queued: 2,
			liveUnits: SimConfig.MaxUnits - 2, Traits, SimConfig.MaxUnits));
	}

	[Fact]
	public void AFreeUnitIsNotQueuedForever()
	{
		// A zero cost would make ShouldQueue true on every decision regardless of the
		// balance; it is a misconfigured resource, not an invitation.
		Assert.False(StrategistBrain.ShouldQueue(balance: 0, cost: 0, queued: 0, liveUnits: 0, Traits,
			SimConfig.MaxUnits));
	}

	// ---- the garrison ------------------------------------------------------

	[Fact]
	public void TheFirstFewUnitsStayHome()
	{
		for (int i = 0; i < Traits.GarrisonUnits; i++)
		{
			Assert.True(StrategistBrain.ShouldGarrison(i, Traits));
		}

		Assert.False(StrategistBrain.ShouldGarrison(Traits.GarrisonUnits, Traits));
	}

	[Fact]
	public void AGarrisonOfZeroSendsEverything()
	{
		var allIn = new StrategistTraits(decisionIntervalTicks: 30, queueDepth: 2, garrisonUnits: 0,
			reorderRadiusMeters: 12f);

		Assert.False(StrategistBrain.ShouldGarrison(0, allIn));
	}

	// ---- reissuing orders --------------------------------------------------

	[Fact]
	public void AUnitWithNoStandingOrderNeedsOne()
	{
		Assert.True(StrategistBrain.NeedsOrder(UnitStateId.Idle, OrderKind.None, OrderKind.Attack, 0f, Traits));
	}

	[Fact]
	public void AUnitDoingTheWrongSortOfThingNeedsANewOrder()
	{
		// It has just been moved from the assault to the garrison, or the other way.
		Assert.True(StrategistBrain.NeedsOrder(UnitStateId.Moving, OrderKind.Attack, OrderKind.Defend, 0f,
			Traits));
	}

	[Fact]
	public void AUnitOnItsWayIsLeftAlone()
	{
		// Reissuing every half second is how an RTS AI ends up re-pathing constantly
		// and arriving nowhere.
		Assert.False(StrategistBrain.NeedsOrder(UnitStateId.Moving, OrderKind.Attack, OrderKind.Attack, 1f,
			Traits));
		Assert.False(StrategistBrain.NeedsOrder(UnitStateId.Engaging, OrderKind.Attack, OrderKind.Attack, 1f,
			Traits));
	}

	[Fact]
	public void AUnitStandingOnItsObjectiveIsNotReorderedOntoIt()
	{
		// The garrison sitting on its anchor is the common case, and re-issuing the
		// order it is already following would reset its target scan twice a second.
		Assert.False(StrategistBrain.NeedsOrder(UnitStateId.Idle, OrderKind.Defend, OrderKind.Defend, 0f,
			Traits));
	}

	[Fact]
	public void AnObjectiveThatHasMovedFarEnoughIsWorthReissuingFor()
	{
		Assert.True(StrategistBrain.NeedsOrder(UnitStateId.Moving, OrderKind.Attack, OrderKind.Attack,
			Traits.ReorderRadiusMeters + 1f, Traits));
	}

	[Fact]
	public void TheDeadAreNotGivenOrders()
	{
		Assert.False(StrategistBrain.NeedsOrder(UnitStateId.Dead, OrderKind.None, OrderKind.Attack, 999f,
			Traits));
	}

	// ---- what the order is -------------------------------------------------

	[Fact]
	public void TheAssaultAttackMovesAndTheGarrisonDefends()
	{
		// A plain move order walks past a firefight (UnitBrain.HoldsWhileEngaging),
		// which is right for a human flank and wrong for a bot with no further plan.
		Assert.Equal(OrderKind.Attack, StrategistBrain.OrderFor(garrison: false));
		Assert.Equal(OrderKind.Defend, StrategistBrain.OrderFor(garrison: true));
	}

	// ---- which tier to buy (docs/IMPLEMENTATION_PLAN.md §M5) ----------------

	private static readonly int[] Costs = { 50, 150, 400 };

	[Fact]
	public void AnEmptyArmyBuysRiflemen()
	{
		int[] live = { 0, 0, 0 };

		Assert.True(StrategistBrain.TryChooseTier(1000, Costs, live, Traits, out byte tier));
		Assert.Equal(0, tier);
	}

	[Fact]
	public void ItBuysTheBestItCanAffordOnceTheRiflemenAreOutThere()
	{
		// Three riflemen per heavy thing, and nothing heavy yet: one tank is screened.
		int[] live = { 3, 0, 0 };

		Assert.True(StrategistBrain.TryChooseTier(400, Costs, live, Traits, out byte tier));
		Assert.Equal(2, tier);

		Assert.True(StrategistBrain.TryChooseTier(399, Costs, live, Traits, out tier));
		Assert.Equal(1, tier);

		Assert.True(StrategistBrain.TryChooseTier(149, Costs, live, Traits, out tier));
		Assert.Equal(0, tier);
	}

	[Fact]
	public void AnArmyOfTanksGoesBackToBuyingRiflemen()
	{
		// Three riflemen and one tank: a second tank needs six.
		int[] live = { 3, 0, 1 };

		Assert.True(StrategistBrain.TryChooseTier(10_000, Costs, live, Traits, out byte tier));
		Assert.Equal(0, tier);

		int[] screened = { 6, 0, 1 };
		Assert.True(StrategistBrain.TryChooseTier(10_000, Costs, screened, Traits, out tier));
		Assert.Equal(2, tier);
	}

	[Fact]
	public void NothingAffordableIsNotAChoice()
	{
		int[] live = { 10, 0, 0 };

		Assert.False(StrategistBrain.TryChooseTier(49, Costs, live, Traits, out _));
	}

	[Fact]
	public void ACatalogOfOneStillBuildsIt()
	{
		// The catalog was one unit long until M5 and may be again on a branch; the
		// rule must not need a second tier to exist.
		int[] costs = { 50 };
		int[] live = { 0 };

		Assert.True(StrategistBrain.TryChooseTier(50, costs, live, Traits, out byte tier));
		Assert.Equal(0, tier);
	}

	// ---- the traits themselves ---------------------------------------------

	[Fact]
	public void TraitsClampTheirOwnNonsense()
	{
		var silly = new StrategistTraits(decisionIntervalTicks: 0, queueDepth: -1, garrisonUnits: -1,
			reorderRadiusMeters: -5f);

		// A decision interval of zero would make the bot think on every tick, which is
		// sixty commands a second at a hundred units.
		Assert.True(silly.DecisionIntervalTicks >= 1);
		Assert.Equal(0, silly.QueueDepth);
		Assert.Equal(0, silly.GarrisonUnits);
		Assert.True(silly.ReorderRadiusMeters > 0f);
	}
}
