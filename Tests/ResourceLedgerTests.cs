using Gdpyr.Sim;
using Xunit;

namespace Gdpyr.Tests;

public class ResourceLedgerTests
{
	private static ResourceLedger Funded(int balance = 1000)
	{
		var ledger = new ResourceLedger();
		ledger.Reset(balance);
		return ledger;
	}

	[Fact]
	public void StartsEmpty()
	{
		var ledger = new ResourceLedger();

		Assert.Equal(0, ledger.Balance);
		Assert.False(ledger.CanAfford(1));
		Assert.True(ledger.CanAfford(0));
	}

	[Fact]
	public void ResetFillsThePoolAndClearsTheCounters()
	{
		ResourceLedger ledger = Funded();
		ledger.TrySpend(100);
		ledger.Reset(500);

		Assert.Equal(500, ledger.Balance);
		Assert.Equal(0, ledger.Spent);
		Assert.Equal(0, ledger.Earned);
	}

	[Fact]
	public void SpendingDeductsAndCounts()
	{
		ResourceLedger ledger = Funded();

		Assert.True(ledger.TrySpend(250));
		Assert.Equal(750, ledger.Balance);
		Assert.Equal(250, ledger.Spent);
	}

	[Fact]
	public void SpendingMoreThanIsThereIsRefusedRatherThanGoingNegative()
	{
		// The barracks asks before it queues; "queued but unpaid" is a state nothing
		// downstream knows how to unwind.
		ResourceLedger ledger = Funded(100);

		Assert.False(ledger.TrySpend(101));
		Assert.Equal(100, ledger.Balance);
		Assert.Equal(0, ledger.Spent);
	}

	[Fact]
	public void ANegativeCostIsRefused()
	{
		ResourceLedger ledger = Funded(100);

		Assert.False(ledger.TrySpend(-50));
		Assert.Equal(100, ledger.Balance);
	}

	[Fact]
	public void SpendingEverythingIsAllowed()
	{
		ResourceLedger ledger = Funded(100);

		Assert.True(ledger.TrySpend(100));
		Assert.Equal(0, ledger.Balance);
		Assert.False(ledger.TrySpend(1));
	}

	[Fact]
	public void IncomeAddsAndCounts()
	{
		ResourceLedger ledger = Funded(0);
		ledger.Add(75);

		Assert.Equal(75, ledger.Balance);
		Assert.Equal(75, ledger.Earned);
	}

	[Fact]
	public void ARefundTakesTheCostBackOffTheOutlayToo()
	{
		// Otherwise "spent" counts a cancelled build, and M6's per-round CSV reports
		// points the strategist never lost.
		ResourceLedger ledger = Funded();
		ledger.TrySpend(200);
		ledger.Refund(200);

		Assert.Equal(1000, ledger.Balance);
		Assert.Equal(0, ledger.Spent);
		Assert.Equal(0, ledger.Earned);
	}

	[Fact]
	public void VersionAdvancesOnEveryChange()
	{
		ResourceLedger ledger = Funded();
		uint start = ledger.Version;

		ledger.TrySpend(10);
		Assert.True(ledger.Version > start);

		uint afterSpend = ledger.Version;
		ledger.Add(10);
		Assert.True(ledger.Version > afterSpend);
	}

	[Fact]
	public void ARefusedSpendChangesNothingAtAll()
	{
		ResourceLedger ledger = Funded(10);
		uint version = ledger.Version;

		Assert.False(ledger.TrySpend(11));
		Assert.Equal(version, ledger.Version);
	}

	[Fact]
	public void ApplyTakesTheServersBalance()
	{
		var client = new ResourceLedger();
		client.Apply(640, version: 9);

		Assert.Equal(640, client.Balance);
	}

	[Fact]
	public void ApplyIgnoresAStaleUpdate()
	{
		var client = new ResourceLedger();
		client.Apply(640, version: 9);
		client.Apply(1000, version: 4);

		Assert.Equal(640, client.Balance);
	}
}
