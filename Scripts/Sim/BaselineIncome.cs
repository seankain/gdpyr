using System;

namespace Gdpyr.Sim;

/// <summary>
/// The strategist's income that no node pays: a fixed trickle for the whole of a live
/// round, held or not.
///
/// The strategist directs the waves the ground force has to survive — a Firefight
/// director, not an opponent who can be bankrupted out of the round. It loses on the
/// clock, with ground tickets left, and in no other way; so it must never be left
/// with nothing to send. With every node lost, this is what still pays for a small
/// wave. The nodes are what pays for a big one.
///
/// In ticks, for the reason a node's payment is (<see cref="CaptureRules"/>): two peers
/// must agree on the balance, and a payment that landed on a different tick on each
/// would be a balance they disagree about.
/// </summary>
public readonly struct BaselineIncome
{
	/// <summary>Points paid per payment. Zero turns it off.</summary>
	public readonly int Points;

	/// <summary>Ticks between two payments.</summary>
	public readonly int IntervalTicks;

	public BaselineIncome(int points, int intervalTicks)
	{
		Points = Math.Max(points, 0);
		IntervalTicks = Math.Max(intervalTicks, 1);
	}

	/// <summary>False for <c>default</c> and for a zero payment: nothing is paid.</summary>
	public bool Pays => Points > 0 && IntervalTicks > 0;

	/// <summary>Points per minute, for the HUD and for tuning arguments.</summary>
	public float PointsPerMinute => Pays ? Points * SimConfig.TickRate * 60f / IntervalTicks : 0f;
}
