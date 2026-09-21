using System;

namespace Gdpyr.Sim;

/// <summary>
/// The strategist's points: what they have, what they have spent, and what they
/// have been paid (docs/IMPLEMENTATION_PLAN.md §3).
///
/// M3 needs only the spending half — a barracks queue deducts a unit's cost, and
/// refunds it if the order is cancelled. The income half is M5's resource nodes,
/// and exists here as <see cref="Add"/> so that adding them is a caller and not a
/// rewrite.
///
/// Server-authoritative and engine-free. Clients hold a replicated copy of
/// <see cref="Balance"/> and nothing else: a client that could spend points would
/// be a client that could conjure units.
/// </summary>
public sealed class ResourceLedger
{
	public int Balance { get; private set; }

	/// <summary>Total spent this round. Refunds subtract from it, so it reads as net outlay.</summary>
	public int Spent { get; private set; }

	/// <summary>Total earned this round, income and refunds alike. For M8's per-round CSV.</summary>
	public int Earned { get; private set; }

	/// <summary>Bumped on every change, so a replicated copy can ignore a stale message.</summary>
	public uint Version { get; private set; }

	public void Reset(int startingBalance)
	{
		Balance = Math.Max(startingBalance, 0);
		Spent = 0;
		Earned = 0;
		Version++;
	}

	public bool CanAfford(int cost) => cost >= 0 && Balance >= cost;

	/// <summary>
	/// Deducts a cost, or refuses. Refusing rather than going negative is the point:
	/// the barracks asks before it queues, and "queued but unpaid" is a state
	/// nothing downstream knows how to unwind.
	/// </summary>
	public bool TrySpend(int cost)
	{
		if (!CanAfford(cost))
		{
			return false;
		}

		Balance -= cost;
		Spent += cost;
		Version++;
		return true;
	}

	/// <summary>Income, or a refund for a cancelled build.</summary>
	public void Add(int amount)
	{
		if (amount <= 0)
		{
			return;
		}

		Balance += amount;
		Earned += amount;
		Version++;
	}

	/// <summary>Takes a cost back off <see cref="Spent"/> as well, so the counter stays honest.</summary>
	public void Refund(int cost)
	{
		if (cost <= 0)
		{
			return;
		}

		Balance += cost;
		Spent -= cost;
		Version++;
	}

	/// <summary>Applies a replicated balance on a client. The server never calls this.</summary>
	public void Apply(int balance, uint version)
	{
		if (version < Version)
		{
			return;
		}

		Balance = balance;
		Version = version;
	}
}
