using Gdpyr.Sim;
using Godot;
using Xunit;

namespace Gdpyr.Tests;

public class UnitBrainTests
{
	/// <summary>The infantry's numbers: sees 45 m, shoots to 40 m, arrives within 1.5 m, leashes at 20 m.</summary>
	private static readonly UnitTraits Infantry = new(45f, 40f, 1.5f, 20f);

	private static UnitSituation Situation(OrderKind order = OrderKind.None, bool alive = true,
		bool hasTarget = false, float targetDistance = 999f, float destinationDistance = 0f) =>
		new(alive, order, hasTarget, targetDistance, destinationDistance);

	// ---- the FSM -----------------------------------------------------------

	[Fact]
	public void DeathBeatsEverythingElse()
	{
		UnitSituation situation = Situation(OrderKind.Attack, alive: false, hasTarget: true, targetDistance: 5f,
			destinationDistance: 100f);

		Assert.Equal(UnitStateId.Dead, UnitBrain.Next(situation, Infantry));
	}

	[Fact]
	public void ATargetInRangeMeansEngaging()
	{
		Assert.Equal(UnitStateId.Engaging,
			UnitBrain.Next(Situation(hasTarget: true, targetDistance: 30f), Infantry));
	}

	[Fact]
	public void ATargetItCanSeeButNotReachIsNotEngaged()
	{
		// Sensor 45 m, engage 40 m: the five metres between them is where a unit knows
		// something is there and cannot do anything about it.
		UnitSituation situation = Situation(OrderKind.Attack, hasTarget: true, targetDistance: 42f,
			destinationDistance: 42f);

		Assert.Equal(UnitStateId.Moving, UnitBrain.Next(situation, Infantry));
	}

	[Fact]
	public void ADestinationFurtherThanTheArrivalRadiusMeansMoving()
	{
		Assert.Equal(UnitStateId.Moving,
			UnitBrain.Next(Situation(OrderKind.Move, destinationDistance: 20f), Infantry));
	}

	[Fact]
	public void ArrivingWithNothingToShootMeansIdle()
	{
		Assert.Equal(UnitStateId.Idle,
			UnitBrain.Next(Situation(OrderKind.Move, destinationDistance: 1f), Infantry));
	}

	[Fact]
	public void EngagingOutranksMoving()
	{
		// A unit walking somewhere that meets something shootable is Engaging; whether
		// it stops is HoldsWhileEngaging's business, not the state's.
		UnitSituation situation = Situation(OrderKind.Move, hasTarget: true, targetDistance: 10f,
			destinationDistance: 80f);

		Assert.Equal(UnitStateId.Engaging, UnitBrain.Next(situation, Infantry));
	}

	[Theory]
	[InlineData(OrderKind.None, true)]
	[InlineData(OrderKind.Attack, true)]
	[InlineData(OrderKind.Defend, true)]
	[InlineData(OrderKind.Move, false)]
	[InlineData(OrderKind.Patrol, false)]
	[InlineData(OrderKind.Embark, false)]
	[InlineData(OrderKind.Unload, false)]
	public void OnlySomeOrdersStopToFight(OrderKind order, bool holds)
	{
		// A move order that stops for every contact is how a flank the strategist has
		// already paid for gets lost halfway.
		Assert.Equal(holds, UnitBrain.HoldsWhileEngaging(order));
	}

	// ---- destinations ------------------------------------------------------

	[Fact]
	public void NoOrderMeansStandStill()
	{
		var self = new Vector3(3f, 0f, 4f);
		var order = UnitOrder.Hold(self);

		Vector3 destination = UnitBrain.Destination(order, self, hasTarget: false, Vector3.Zero, Infantry,
			out bool hasDestination);

		Assert.False(hasDestination);
		Assert.Equal(self, destination);
	}

	[Fact]
	public void AMoveOrderIgnoresWhatItCanSee()
	{
		var order = new UnitOrder { Kind = OrderKind.Move, Target = new Vector3(50f, 0f, 0f) };

		Vector3 destination = UnitBrain.Destination(order, Vector3.Zero, hasTarget: true,
			new Vector3(0f, 0f, 10f), Infantry, out bool hasDestination);

		Assert.True(hasDestination);
		Assert.Equal(order.Target, destination);
	}

	[Fact]
	public void AnAttackOrderWalksToThePointUntilSomethingTurnsUp()
	{
		var order = new UnitOrder { Kind = OrderKind.Attack, Target = new Vector3(50f, 0f, 0f) };
		var enemy = new Vector3(0f, 0f, 12f);

		Assert.Equal(order.Target,
			UnitBrain.Destination(order, Vector3.Zero, hasTarget: false, Vector3.Zero, Infantry, out _));
		Assert.Equal(enemy,
			UnitBrain.Destination(order, Vector3.Zero, hasTarget: true, enemy, Infantry, out _));
	}

	[Fact]
	public void APatrolWalksBetweenItsTwoEnds()
	{
		var order = new UnitOrder
		{
			Kind = OrderKind.Patrol,
			Anchor = new Vector3(0f, 0f, 0f),
			Target = new Vector3(30f, 0f, 0f),
		};

		Assert.Equal(order.Target,
			UnitBrain.Destination(order, Vector3.Zero, false, Vector3.Zero, Infantry, out _));

		UnitBrain.AdvancePatrol(ref order, arrived: true);
		Assert.True(order.Returning);
		Assert.Equal(order.Anchor,
			UnitBrain.Destination(order, order.Target, false, Vector3.Zero, Infantry, out _));

		UnitBrain.AdvancePatrol(ref order, arrived: true);
		Assert.False(order.Returning);
	}

	[Fact]
	public void APatrolOnlyTurnsRoundWhenItArrives()
	{
		var order = new UnitOrder { Kind = OrderKind.Patrol };

		UnitBrain.AdvancePatrol(ref order, arrived: false);

		Assert.False(order.Returning);
	}

	[Fact]
	public void OnlyAPatrolTurnsRound()
	{
		var order = new UnitOrder { Kind = OrderKind.Move };

		UnitBrain.AdvancePatrol(ref order, arrived: true);

		Assert.False(order.Returning);
	}

	[Fact]
	public void ADefendingUnitChasesOnlyAsFarAsItsLeash()
	{
		var anchor = new Vector3(0f, 0f, 0f);
		var order = new UnitOrder { Kind = OrderKind.Defend, Target = anchor, Anchor = anchor };

		var near = new Vector3(0f, 0f, 10f);
		Assert.Equal(near, UnitBrain.Destination(order, anchor, hasTarget: true, near, Infantry, out _));

		// Leash is 20 m: a probe at 30 m drags nobody off the point.
		var far = new Vector3(0f, 0f, 30f);
		Assert.Equal(anchor, UnitBrain.Destination(order, anchor, hasTarget: true, far, Infantry, out _));
	}

	[Fact]
	public void ADefendingUnitThatHasStrayedComesHome()
	{
		var anchor = Vector3.Zero;
		var order = new UnitOrder { Kind = OrderKind.Defend, Target = anchor, Anchor = anchor };
		var self = new Vector3(0f, 0f, 25f);
		var enemy = new Vector3(0f, 0f, 26f);

		Assert.Equal(anchor, UnitBrain.Destination(order, self, hasTarget: true, enemy, Infantry, out _));
	}

	[Fact]
	public void ABoarderWalksToWhereItWasToldToStand_AndKeepsWalkingWithATarget()
	{
		// docs/VEHICLES.md §5.2: beside the vehicle, shooting on the way as on a move.
		var order = new UnitOrder
		{
			Kind = OrderKind.Embark,
			Target = new Vector3(12f, 0f, -3f),
			Anchor = Vector3.Zero,
			TargetOwnerId = OwnerId.ForUnit(9),
		};

		Vector3 destination = UnitBrain.Destination(order, new Vector3(30f, 0f, 0f), hasTarget: true,
			new Vector3(40f, 0f, 10f), Infantry, out bool hasDestination);

		Assert.True(hasDestination);
		Assert.Equal(order.Target, destination);
	}

	[Fact]
	public void AnUnloadingVehicleHasNoDestination()
	{
		// None at all, rather than its own position: a destination within the re-path
		// threshold would keep it driving at an old path corner (docs/VEHICLES.md §5.2).
		var self = new Vector3(5f, 0f, 5f);
		var order = new UnitOrder { Kind = OrderKind.Unload, Target = self, Anchor = self };

		Vector3 destination = UnitBrain.Destination(order, self, hasTarget: true, new Vector3(9f, 0f, 9f), Infantry,
			out bool hasDestination);

		Assert.False(hasDestination);
		Assert.Equal(self, destination);
	}

	[Fact]
	public void EmbarkAndUnload_CannotArriveThroughTheGenericOrderMessage()
	{
		Assert.False(UnitOrder.IsIssuable((byte)OrderKind.Embark));
		Assert.False(UnitOrder.IsIssuable((byte)OrderKind.Unload));
		Assert.True(UnitOrder.IsIssuable((byte)OrderKind.Stop));
	}

	[Fact]
	public void TheNewOrderKinds_AreAppended()
	{
		// On the wire, in the unit HTN's order fact and in a policy's observation bytes.
		Assert.Equal(7, (byte)OrderKind.Embark);
		Assert.Equal(8, (byte)OrderKind.Unload);
	}

	// ---- acquisition -------------------------------------------------------

	[Fact]
	public void AcquisitionStopsAtTheSensorRadius()
	{
		Assert.True(UnitBrain.CanAcquire(44f, Infantry));
		Assert.False(UnitBrain.CanAcquire(46f, Infantry));
	}

	[Fact]
	public void AnAcquiredTargetIsKeptPastTheSensorRadius()
	{
		// Hysteresis: without it a player walking the boundary makes the unit flicker
		// between Idle and Engaging every tick.
		Assert.False(UnitBrain.ShouldDropTarget(50f, Infantry));
		Assert.True(UnitBrain.ShouldDropTarget(60f, Infantry));
	}

	// ---- leading -----------------------------------------------------------

	[Fact]
	public void AStationaryTargetIsNotLed()
	{
		var target = new Vector3(0f, 0f, -50f);

		Assert.Equal(target, UnitBrain.Lead(Vector3.Zero, target, Vector3.Zero, 340f));
	}

	[Fact]
	public void ACrossingTargetIsLedByItsFlightTime()
	{
		// 50 m at 340 m/s is 0.147 s; a target crossing at 6 m/s covers 0.88 m in that
		// time, which is most of a body width (docs/NETCODE.md §4.4).
		var target = new Vector3(0f, 0f, -50f);
		var velocity = new Vector3(6f, 0f, 0f);

		Vector3 aim = UnitBrain.Lead(Vector3.Zero, target, velocity, 340f);

		Assert.Equal(50f / 340f * 6f, aim.X, 3);
		Assert.Equal(target.Z, aim.Z, 3);
	}

	[Fact]
	public void AProjectileWithNoSpeedIsNotLedIntoADivideByZero()
	{
		var target = new Vector3(0f, 0f, -50f);

		Assert.Equal(target, UnitBrain.Lead(Vector3.Zero, target, new Vector3(6f, 0f, 0f), 0f));
	}
}
