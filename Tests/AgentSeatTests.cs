using System.Collections.Generic;
using Gdpyr.Sim;
using Gdpyr.Sim.Agent;
using Xunit;

namespace Gdpyr.Tests;

/// <summary>
/// The seat lease (docs/AGENT_API.md §2, §2.1).
///
/// An agent that stops sending is a body standing in the open, and a round that
/// stalls on a crashed trainer is worse than no API at all — so the interesting
/// question is what happens when the policy goes away, and it is answered here
/// rather than with a stopwatch.
/// </summary>
public class AgentSeatTests
{
	private const int Ground = 2_147_483_641;
	private const int Session = 1;

	private static AgentSeatBook Book() => new();

	private static AgentSeat Attach(AgentSeatBook book, int peerId = Ground,
		AgentSeatController controller = AgentSeatController.Bot, int sessionId = Session,
		int stepMul = 4, uint tick = 0) =>
		book.Attach(peerId, controller, AgentPolicyKind.Ground, sessionId, stepMul, tick, out _);

	// ---- taking a seat -----------------------------------------------------

	[Fact]
	public void ABotSeatCanBeClaimed()
	{
		AgentSeatBook book = Book();
		AgentSeat seat = Attach(book);

		Assert.NotNull(seat);
		Assert.True(book.IsAttached(Ground));
		Assert.Equal(1, book.Count);
		Assert.Equal(4, seat.StepMul);
	}

	[Fact]
	public void APersonsSeatIsRefused()
	{
		AgentSeatBook book = Book();
		AgentSeat seat = book.Attach(5, AgentSeatController.Human, AgentPolicyKind.Ground, Session, 4, 0,
			out AgentAttachRefusal refusal);

		Assert.Null(seat);
		Assert.Equal(AgentAttachRefusal.SeatIsHuman, refusal);
		Assert.False(book.IsAttached(5));
	}

	[Fact]
	public void AnotherSessionsSeatIsRefused()
	{
		AgentSeatBook book = Book();
		Attach(book);

		AgentSeat stolen = book.Attach(Ground, AgentSeatController.Agent, AgentPolicyKind.Ground, 2, 4, 0,
			out AgentAttachRefusal refusal);

		Assert.Null(stolen);
		Assert.Equal(AgentAttachRefusal.SeatIsTaken, refusal);
	}

	[Fact]
	public void TheSameSessionMayReattachToASeatItAlreadyHolds()
	{
		AgentSeatBook book = Book();
		Attach(book, stepMul: 4, tick: 0);

		AgentSeat again = book.Attach(Ground, AgentSeatController.Agent, AgentPolicyKind.Ground, Session, 30,
			600, out AgentAttachRefusal refusal);

		Assert.NotNull(again);
		Assert.Equal(AgentAttachRefusal.None, refusal);
		Assert.Equal(30, again.StepMul);
		Assert.Equal(600u, again.AttachedTick);
		Assert.Equal(1, book.Count);
	}

	[Fact]
	public void StepMulIsClampedIntoSomethingPlayable()
	{
		AgentSeatBook book = Book();
		Assert.Equal(1, Attach(book, stepMul: 0).StepMul);

		book.Clear();
		Assert.Equal(AgentSeat.MaxStepMul, Attach(book, stepMul: int.MaxValue).StepMul);
	}

	[Fact]
	public void TheDefaultRepeatIsFifteenHertzOnTheGroundAndTwoInTheChair()
	{
		Assert.Equal(4, AgentSeatBook.DefaultStepMul(AgentPolicyKind.Ground));
		Assert.Equal(30, AgentSeatBook.DefaultStepMul(AgentPolicyKind.Strategist));
	}

	// ---- giving it back ----------------------------------------------------

	[Fact]
	public void DetachReleasesOneSeat()
	{
		AgentSeatBook book = Book();
		Attach(book);

		Assert.True(book.Detach(Ground));
		Assert.False(book.IsAttached(Ground));
		Assert.False(book.Detach(Ground));
	}

	[Fact]
	public void AClosedSocketReleasesEverySeatThatSessionHeld()
	{
		AgentSeatBook book = Book();
		Attach(book, peerId: Ground);
		Attach(book, peerId: Ground + 1);
		Attach(book, peerId: Ground + 2, sessionId: 2);

		var released = new List<int>();
		Assert.Equal(2, book.ReleaseSession(Session, released));
		Assert.Equal(2, released.Count);

		// The other session keeps its lease: one trainer crashing is not both.
		Assert.True(book.IsAttached(Ground + 2));
		Assert.Equal(1, book.Count);
	}

	[Fact]
	public void ReleasingASessionThatHoldsNothingIsNotAnError()
	{
		AgentSeatBook book = Book();
		Assert.Equal(0, book.ReleaseSession(99));
	}

	// ---- the grace window --------------------------------------------------

	[Fact]
	public void ASeatThatHasNeverActedIsPiloted()
	{
		AgentSeatBook book = Book();
		AgentSeat seat = Attach(book);

		Assert.True(seat.ShouldPilot(0, AgentSeatBook.DefaultGraceTicks));
		Assert.False(seat.HasAction);
	}

	[Fact]
	public void AnActionHoldsTheSeatForTheGraceWindowAndThenGivesItBack()
	{
		AgentSeatBook book = Book();
		AgentSeat seat = Attach(book);
		seat.Submit(100, AgentGroundAction.Neutral);

		int grace = AgentSeatBook.DefaultGraceTicks;
		Assert.Equal(SimConfig.TickRate / 2, grace);

		Assert.False(seat.ShouldPilot(100, grace));
		Assert.False(seat.ShouldPilot(100 + (uint)grace, grace));
		Assert.True(seat.ShouldPilot(101 + (uint)grace, grace));
	}

	[Fact]
	public void APolicyThatComesBackTakesTheSeatStraightBack()
	{
		AgentSeatBook book = Book();
		AgentSeat seat = Attach(book);
		seat.Submit(100, AgentGroundAction.Neutral);

		int grace = AgentSeatBook.DefaultGraceTicks;
		Assert.True(seat.ShouldPilot(500, grace));

		seat.Submit(500, AgentGroundAction.Neutral);
		Assert.False(seat.ShouldPilot(500, grace));
		Assert.True(book.IsAttached(Ground));
	}

	[Fact]
	public void SteppedSeatsHaveNoGraceBecauseTheGateAlreadyGuaranteesFreshness()
	{
		AgentSeatBook book = Book();
		AgentSeat seat = Attach(book);
		seat.Submit(100, AgentGroundAction.Neutral);

		Assert.False(seat.ShouldPilot(100_000, int.MaxValue));
	}

	[Fact]
	public void FallbacksAreCountedSoATraceCanShowThem()
	{
		AgentSeatBook book = Book();
		AgentSeat seat = Attach(book);

		seat.CountFallback();
		seat.CountFallback();
		seat.Submit(1, AgentGroundAction.Neutral);

		Assert.Equal(2, seat.PilotFallbacks);
		Assert.Equal(1, seat.ActionsApplied);
	}

	// ---- action repeat -----------------------------------------------------

	[Fact]
	public void DecisionTicksAreAlignedToWhenTheLeaseWasTaken()
	{
		AgentSeatBook book = Book();
		AgentSeat seat = Attach(book, stepMul: 4, tick: 51);

		Assert.True(seat.IsDecisionTick(51));
		Assert.False(seat.IsDecisionTick(52));
		Assert.False(seat.IsDecisionTick(54));
		Assert.True(seat.IsDecisionTick(55));
	}

	[Fact]
	public void TwoSeatsTakenATickApartDoNotDecideOnTheSameTick()
	{
		AgentSeatBook book = Book();
		AgentSeat first = Attach(book, peerId: Ground, stepMul: 4, tick: 100);
		AgentSeat second = Attach(book, peerId: Ground + 1, stepMul: 4, tick: 101);

		for (uint tick = 100; tick < 140; tick++)
		{
			Assert.False(first.IsDecisionTick(tick) && second.IsDecisionTick(tick));
		}
	}

	[Fact]
	public void AStepMulOfOneDecidesEveryTick()
	{
		AgentSeatBook book = Book();
		AgentSeat seat = Attach(book, stepMul: 1, tick: 7);

		for (uint tick = 7; tick < 20; tick++)
		{
			Assert.True(seat.IsDecisionTick(tick));
		}
	}
}
