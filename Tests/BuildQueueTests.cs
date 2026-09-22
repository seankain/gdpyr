using Gdpyr.Sim;
using Xunit;

namespace Gdpyr.Tests;

public class BuildQueueTests
{
	private const byte Infantry = 0;
	private const int Cost = 50;
	private const int BuildTicks = 240;

	private static ResourceLedger Funded(int balance = 1000)
	{
		var ledger = new ResourceLedger();
		ledger.Reset(balance);
		return ledger;
	}

	[Fact]
	public void StartsEmpty()
	{
		var queue = new BuildQueue();

		Assert.Equal(0, queue.Count);
		Assert.False(queue.IsFull);
		Assert.Equal(0f, queue.Progress(0));
	}

	[Fact]
	public void QueueingChargesTheStrategist()
	{
		// Charged on order and not on completion: twenty queued riflemen have to be a
		// commitment, or there is nothing to choose between
		// (docs/IMPLEMENTATION_PLAN.md §M3).
		var queue = new BuildQueue();
		ResourceLedger ledger = Funded();

		Assert.True(queue.TryEnqueue(Infantry, Cost, BuildTicks, ledger));

		Assert.Equal(1, queue.Count);
		Assert.Equal(950, ledger.Balance);
	}

	[Fact]
	public void QueueingWithoutThePointsChargesNothing()
	{
		var queue = new BuildQueue();
		ResourceLedger ledger = Funded(20);

		Assert.False(queue.TryEnqueue(Infantry, Cost, BuildTicks, ledger));

		Assert.Equal(0, queue.Count);
		Assert.Equal(20, ledger.Balance);
	}

	[Fact]
	public void AFullQueueRefusesAndChargesNothing()
	{
		var queue = new BuildQueue();
		ResourceLedger ledger = Funded(BuildQueue.Capacity * Cost * 2);

		for (int i = 0; i < BuildQueue.Capacity; i++)
		{
			Assert.True(queue.TryEnqueue(Infantry, Cost, BuildTicks, ledger));
		}

		int balance = ledger.Balance;
		Assert.True(queue.IsFull);
		Assert.False(queue.TryEnqueue(Infantry, Cost, BuildTicks, ledger));
		Assert.Equal(balance, ledger.Balance);
	}

	[Fact]
	public void AUnitComesOutAfterItsBuildTime()
	{
		var queue = new BuildQueue();
		queue.TryEnqueue(Infantry, Cost, BuildTicks, Funded());

		// The head starts its clock on the first tick it is offered, not when it was
		// queued: nothing else knows what tick the enqueue happened on.
		Assert.False(queue.Tick(100, out _));
		Assert.False(queue.Tick(100 + BuildTicks - 1, out _));

		Assert.True(queue.Tick(100 + BuildTicks, out byte produced));
		Assert.Equal(Infantry, produced);
		Assert.Equal(0, queue.Count);
		Assert.Equal(1, queue.Produced);
	}

	[Fact]
	public void OnlyOneUnitComesOutPerTick()
	{
		// A barracks that emptied its queue into one tick would put twenty units
		// inside each other.
		var queue = new BuildQueue();
		ResourceLedger ledger = Funded();
		queue.TryEnqueue(Infantry, Cost, 1, ledger);
		queue.TryEnqueue(Infantry, Cost, 1, ledger);
		queue.TryEnqueue(Infantry, Cost, 1, ledger);

		Assert.False(queue.Tick(0, out _));
		Assert.True(queue.Tick(1000, out _));
		Assert.Equal(2, queue.Count);

		Assert.False(queue.Tick(1000, out _));
		Assert.True(queue.Tick(1001, out _));
		Assert.Equal(1, queue.Count);
	}

	[Fact]
	public void ProgressRunsFromZeroToOne()
	{
		var queue = new BuildQueue();
		queue.TryEnqueue(Infantry, Cost, BuildTicks, Funded());

		queue.Tick(0, out _);

		Assert.Equal(0f, queue.Progress(0), 3);
		Assert.Equal(0.5f, queue.Progress(BuildTicks / 2), 2);
		Assert.Equal(1f, queue.Progress(BuildTicks), 3);
		Assert.Equal(1f, queue.Progress(BuildTicks + 500), 3);
	}

	[Fact]
	public void AnEmptyQueueReportsNoProgress()
	{
		var queue = new BuildQueue();

		Assert.Equal(0f, queue.Progress(500));
		Assert.Equal((byte)0, queue.Head);
	}

	[Fact]
	public void CancellingRefundsTheLastOrderedUnit()
	{
		// The last, not the first: cancelling must not throw away work already done.
		var queue = new BuildQueue();
		ResourceLedger ledger = Funded();
		queue.TryEnqueue(Infantry, Cost, BuildTicks, ledger);
		queue.TryEnqueue(Infantry, 200, BuildTicks, ledger);

		Assert.True(queue.CancelLast(ledger));

		Assert.Equal(1, queue.Count);
		Assert.Equal(950, ledger.Balance);
	}

	[Fact]
	public void CancellingTheOnlyUnitStopsTheClock()
	{
		var queue = new BuildQueue();
		ResourceLedger ledger = Funded();
		queue.TryEnqueue(Infantry, Cost, BuildTicks, ledger);
		queue.Tick(0, out _);

		Assert.True(queue.CancelLast(ledger));
		Assert.Equal(0, queue.Count);
		Assert.Equal(0f, queue.Progress(BuildTicks));
		Assert.Equal(1000, ledger.Balance);
	}

	[Fact]
	public void CancellingAnEmptyQueueIsANoOp()
	{
		var queue = new BuildQueue();
		ResourceLedger ledger = Funded();

		Assert.False(queue.CancelLast(ledger));
		Assert.Equal(1000, ledger.Balance);
	}

	[Fact]
	public void TheHeadRestartsItsClockAfterTheOneBeforeItFinishes()
	{
		var queue = new BuildQueue();
		ResourceLedger ledger = Funded();
		queue.TryEnqueue(Infantry, Cost, 10, ledger);
		queue.TryEnqueue(Infantry, Cost, 10, ledger);

		queue.Tick(0, out _);
		Assert.True(queue.Tick(10, out _));

		// The second unit starts its clock on the next tick it is offered, so two
		// ten-tick units take twenty-one ticks rather than ten. The one tick of slack
		// is the dequeue itself, and it is a tick rather than a special case.
		Assert.False(queue.Tick(11, out _));
		Assert.False(queue.Tick(20, out _));
		Assert.True(queue.Tick(21, out _));
	}

	[Fact]
	public void ClearEmptiesTheQueueWithoutRefunding()
	{
		// Between rounds the whole ledger is reset, so a refund here would be points
		// paid into a pool that is about to be replaced.
		var queue = new BuildQueue();
		ResourceLedger ledger = Funded();
		queue.TryEnqueue(Infantry, Cost, BuildTicks, ledger);

		queue.Clear();

		Assert.Equal(0, queue.Count);
		Assert.Equal(950, ledger.Balance);
	}

	[Fact]
	public void AZeroLengthBuildStillTakesATick()
	{
		var queue = new BuildQueue();
		queue.TryEnqueue(Infantry, Cost, 0, Funded());

		Assert.False(queue.Tick(5, out _));
		Assert.True(queue.Tick(6, out _));
	}

	[Fact]
	public void TheQueueCountsEachKindOnIt()
	{
		const byte Builder = 3;
		var queue = new BuildQueue();
		ResourceLedger ledger = Funded();

		queue.TryEnqueue(Infantry, Cost, BuildTicks, ledger);
		queue.TryEnqueue(Builder, 60, BuildTicks, ledger);
		queue.TryEnqueue(Infantry, Cost, BuildTicks, ledger);

		Assert.Equal(2, queue.CountOf(Infantry));
		Assert.Equal(1, queue.CountOf(Builder));
		Assert.Equal(0, queue.CountOf(2));

		queue.CancelLast(ledger);
		Assert.Equal(1, queue.CountOf(Infantry));
	}
}
