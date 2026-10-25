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

	// ---- units a structure trains (docs/TACTICS.md §6, T7) -------------------

	/// <summary>The heavy gunner's cost, from <c>Units/heavy_gunner.tres</c>.</summary>
	private const int GunnerCost = 90;

	/// <summary>A commander's slots for assault squads with nothing else in its eight: all but the garrison's.</summary>
	private const int Slots = 7;

	[Fact]
	public void AGunner_ForEveryThreeRiflemenPastTheGarrison()
	{
		// A squad of four forms round one gunner: three riflemen to each, and the
		// garrison's four are not an assault's.
		Assert.Equal(0, StrategistBrain.TrainedWanted(UnitDoctrine.Gunner, Traits.GarrisonUnits, Slots, Traits));
		Assert.Equal(0, StrategistBrain.TrainedWanted(UnitDoctrine.Gunner, Traits.GarrisonUnits + 2, Slots, Traits));
		Assert.Equal(1, StrategistBrain.TrainedWanted(UnitDoctrine.Gunner, Traits.GarrisonUnits + 3, Slots, Traits));
		Assert.Equal(2, StrategistBrain.TrainedWanted(UnitDoctrine.Gunner, Traits.GarrisonUnits + 6, Slots, Traits));
		Assert.Equal(0, StrategistBrain.TrainedWanted(UnitDoctrine.Gunner, 0, Slots, Traits));
	}

	[Fact]
	public void NoMoreGunners_ThanTheCommanderHasSquadsFor()
	{
		// Forty riflemen past the garrison would be thirteen gunners; a gunner goes
		// one to an assault squad, and a commander with five slots for them places five.
		Assert.Equal(5, StrategistBrain.TrainedWanted(UnitDoctrine.Gunner, Traits.GarrisonUnits + 40, 5, Traits));
		Assert.Equal(0, StrategistBrain.TrainedWanted(UnitDoctrine.Gunner, Traits.GarrisonUnits + 40, 0, Traits));

		// Snipers hold ground with the garrison, which is not an assault's slot.
		Assert.Equal(Traits.Snipers, StrategistBrain.TrainedWanted(UnitDoctrine.Marksman, 40, 0, Traits));
	}

	[Fact]
	public void Snipers_OnceItHasTheGarrisonsRiflemen()
	{
		Assert.Equal(0, StrategistBrain.TrainedWanted(UnitDoctrine.Marksman, Traits.GarrisonUnits - 1, Slots, Traits));
		Assert.Equal(Traits.Snipers, StrategistBrain.TrainedWanted(UnitDoctrine.Marksman, Traits.GarrisonUnits, Slots, Traits));

		// However large the army: a sniper holds ground, and there is not more ground.
		Assert.Equal(Traits.Snipers, StrategistBrain.TrainedWanted(UnitDoctrine.Marksman, 40, Slots, Traits));
	}

	[Fact]
	public void NothingIsWanted_OfADoctrineNoStructureTrains()
	{
		Assert.Equal(0, StrategistBrain.TrainedWanted(UnitDoctrine.Rifleman, 40, Slots, Traits));
		Assert.Equal(0, StrategistBrain.TrainedWanted(UnitDoctrine.None, 40, Slots, Traits));
	}

	[Fact]
	public void ZeroOfEither_TrainsNone()
	{
		var none = new StrategistTraits(decisionIntervalTicks: 30, queueDepth: 2, garrisonUnits: 4,
			reorderRadiusMeters: 12f, riflemenPerGunner: 0, snipers: 0);

		Assert.Equal(0, StrategistBrain.TrainedWanted(UnitDoctrine.Gunner, 40, Slots, none));
		Assert.Equal(0, StrategistBrain.TrainedWanted(UnitDoctrine.Marksman, 40, Slots, none));
	}

	[Fact]
	public void ItTrains_WhileShortOfWhatItWants_CountingTheOnesQueued()
	{
		Assert.True(StrategistBrain.ShouldTrain(1000, GunnerCost, trained: 1, queuedTrained: 0, wanted: 2, queued: 0,
			liveUnits: 10, Traits, SimConfig.MaxUnits));

		Assert.False(StrategistBrain.ShouldTrain(1000, GunnerCost, trained: 2, queuedTrained: 0, wanted: 2, queued: 0,
			liveUnits: 10, Traits, SimConfig.MaxUnits));

		// Queued at another pillbox: the same gunner, already paid for.
		Assert.False(StrategistBrain.ShouldTrain(1000, GunnerCost, trained: 1, queuedTrained: 1, wanted: 2, queued: 0,
			liveUnits: 10, Traits, SimConfig.MaxUnits));
	}

	[Fact]
	public void ATrainingQueue_KeepsTheBarracksRules()
	{
		Assert.False(StrategistBrain.ShouldTrain(GunnerCost - 1, GunnerCost, trained: 0, queuedTrained: 0, wanted: 4,
			queued: 0, liveUnits: 10, Traits, SimConfig.MaxUnits));
		Assert.False(StrategistBrain.ShouldTrain(1000, GunnerCost, trained: 0, queuedTrained: 0, wanted: 4,
			queued: Traits.QueueDepth, liveUnits: 10, Traits, SimConfig.MaxUnits));
		Assert.False(StrategistBrain.ShouldTrain(1000, GunnerCost, trained: 0, queuedTrained: 0, wanted: 4, queued: 0,
			liveUnits: SimConfig.MaxUnits, Traits, SimConfig.MaxUnits));
		Assert.False(StrategistBrain.ShouldTrain(1000, cost: 0, trained: 0, queuedTrained: 0, wanted: 4, queued: 0,
			liveUnits: 10, Traits, SimConfig.MaxUnits));
	}

	// ---- the traits themselves ---------------------------------------------

	[Fact]
	public void TraitsClampTheirOwnNonsense()
	{
		var silly = new StrategistTraits(decisionIntervalTicks: 0, queueDepth: -1, garrisonUnits: -1,
			reorderRadiusMeters: -5f, riflemenPerGunner: -3, snipers: -2);

		// A decision interval of zero would make the bot think on every tick, which is
		// sixty commands a second at a hundred units.
		Assert.True(silly.DecisionIntervalTicks >= 1);
		Assert.Equal(0, silly.QueueDepth);
		Assert.Equal(0, silly.GarrisonUnits);
		Assert.True(silly.ReorderRadiusMeters > 0f);
		Assert.Equal(0, silly.RiflemenPerGunner);
		Assert.Equal(0, silly.Snipers);
	}
}
