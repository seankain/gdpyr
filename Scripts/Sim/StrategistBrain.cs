using System;

namespace Gdpyr.Sim;

/// <summary>
/// How a computer strategist spends and commands
/// (docs/IMPLEMENTATION_PLAN.md §M3.5).
/// </summary>
public readonly struct StrategistTraits
{
	/// <summary>Ticks between decisions. An RTS player clicks a few times a second at most.</summary>
	public readonly int DecisionIntervalTicks;

	/// <summary>How deep it keeps each barracks queue. Deeper is a commitment it cannot take back.</summary>
	public readonly int QueueDepth;

	/// <summary>Units it keeps at home defending the barracks before sending anything forward.</summary>
	public readonly int GarrisonUnits;

	/// <summary>How far an objective may move before standing orders are reissued [m].</summary>
	public readonly float ReorderRadiusMeters;

	/// <summary>
	/// Riflemen it wants on the field for each heavy thing it owns before it buys
	/// another one (docs/IMPLEMENTATION_PLAN.md §M5).
	///
	/// Without it the rule "buy the best you can afford" spends a whole round's
	/// income on two tanks that walk into six players and die, which is a worse
	/// opponent and, worse than that, a worse *instrument*: the question a bot round
	/// is asked is whether twenty riflemen are a threat, and a bot that never builds
	/// twenty riflemen cannot answer it.
	/// </summary>
	public readonly int InfantryPerHeavy;

	public StrategistTraits(int decisionIntervalTicks, int queueDepth, int garrisonUnits,
		float reorderRadiusMeters, int infantryPerHeavy = 3)
	{
		DecisionIntervalTicks = Math.Max(decisionIntervalTicks, 1);
		QueueDepth = Math.Max(queueDepth, 0);
		GarrisonUnits = Math.Max(garrisonUnits, 0);
		ReorderRadiusMeters = MathF.Max(reorderRadiusMeters, 0.1f);
		InfantryPerHeavy = Math.Max(infantryPerHeavy, 0);
	}

	/// <summary>
	/// The one profile M3.5 ships. Half a second between decisions, two units on the
	/// queue at a time, four kept home: enough pressure that the ground force has to
	/// go and deal with it, not so much that twenty riflemen arrive at once and the
	/// round is a rout in either direction.
	/// </summary>
	public static StrategistTraits Default => new(
		decisionIntervalTicks: SimConfig.TickRate / 2,
		queueDepth: 2,
		garrisonUnits: 4,
		reorderRadiusMeters: 12f);
}

/// <summary>
/// The strategist AI's decisions, as pure functions
/// (docs/IMPLEMENTATION_PLAN.md §M3.5).
///
/// It plays the same game a human strategist does and through the same two
/// requests: put a unit on a queue, and give units an order (docs/NETCODE.md §6.3).
/// It has no privileged access to anything — in particular it is told where the
/// enemy is by the same <c>VisibilityService</c> that decides what a human
/// strategist's client is sent, so the two are behind one fog rather than two
/// (docs/NETCODE.md §6.2, §9).
///
/// Engine-free, so the economy ("does it ever queue itself broke") is a test
/// rather than a twenty-minute round.
/// </summary>
public static class StrategistBrain
{
	/// <summary>
	/// Whether to put another unit on a barracks queue.
	///
	/// Spending down to zero is correct: points buy units, units are the only thing
	/// that applies pressure, and a strategist sitting on a thousand unspent points
	/// at the end of a round has simply not played. The queue depth is what keeps
	/// the whole balance from being committed to one wave.
	/// </summary>
	public static bool ShouldQueue(int balance, int cost, int queued, int liveUnits, in StrategistTraits traits,
		int unitCap)
	{
		if (cost <= 0 || balance < cost)
		{
			return false;
		}

		if (queued >= traits.QueueDepth)
		{
			return false;
		}

		// The field cap is the unit pool's, not a decision: a queue that outruns it
		// spends points on units the manager will refund a tick later.
		return liveUnits + queued < unitCap;
	}

	/// <summary>
	/// Whether a unit should be held back to defend the barracks rather than sent
	/// forward. The first <see cref="StrategistTraits.GarrisonUnits"/> stay home;
	/// everything after that attacks.
	/// </summary>
	public static bool ShouldGarrison(int garrisoned, in StrategistTraits traits) =>
		garrisoned < traits.GarrisonUnits;

	/// <summary>
	/// Whether a unit needs a fresh order, given the one it is following and the one
	/// its current role calls for.
	///
	/// Two cases, and no others: it is doing the wrong sort of thing, or the place it
	/// was sent to is no longer where its role points. Everything else is left alone
	/// — including a unit that has arrived and is standing there, because a standing
	/// order it has already carried out still says where to go when the enemy turns
	/// up. Reissuing more often than this is how an RTS AI ends up re-pathing every
	/// unit twice a second and arriving nowhere, and each reissue also resets the
	/// unit's staggered target scan (<c>UnitManager.ApplyOrder</c>), which is the one
	/// per-unit cost the plan is explicit about (§6).
	/// </summary>
	public static bool NeedsOrder(UnitStateId state, OrderKind order, OrderKind wanted, float objectiveDrift,
		in StrategistTraits traits)
	{
		if (state == UnitStateId.Dead)
		{
			return false;
		}

		return order != wanted || objectiveDrift > traits.ReorderRadiusMeters;
	}

	/// <summary>
	/// The order a unit being sent forward gets: attack-move, always.
	///
	/// A plain move order walks past a firefight without stopping
	/// (<see cref="UnitBrain.HoldsWhileEngaging"/>), which is right for a human
	/// flanking manoeuvre and wrong for everything a bot does — it has no plan
	/// beyond "go where the enemy is", so stopping to fight what it meets is the
	/// plan.
	/// </summary>
	public static OrderKind OrderFor(bool garrison) => garrison ? OrderKind.Defend : OrderKind.Attack;

	/// <summary>
	/// Which of the three tiers to put on a queue (docs/IMPLEMENTATION_PLAN.md §M5).
	///
	/// The best thing it can afford, provided the army it already has is enough
	/// infantry to screen it: <paramref name="liveByTier"/> is the census, tier 0 is
	/// the rifleman, and everything above it counts as a heavy. Falling back to tier
	/// 0 rather than waiting is deliberate — a strategist saving for a tank while
	/// nothing is being built is a strategist not playing, and the queue depth in
	/// <see cref="StrategistTraits.QueueDepth"/> is already what stops it committing
	/// the whole balance to one wave.
	///
	/// <paramref name="costs"/> and <paramref name="liveByTier"/> are parallel to the
	/// unit catalog, which is where the caller gets them; nothing here knows what a
	/// tank is beyond "expensive and outnumbered".
	/// </summary>
	public static bool TryChooseTier(int balance, ReadOnlySpan<int> costs, ReadOnlySpan<int> liveByTier,
		in StrategistTraits traits, out byte tier)
	{
		tier = 0;
		if (costs.Length == 0)
		{
			return false;
		}

		int infantry = liveByTier.Length > 0 ? liveByTier[0] : 0;
		int heavies = 0;
		for (int i = 1; i < liveByTier.Length; i++)
		{
			heavies += liveByTier[i];
		}

		bool screened = infantry >= (heavies + 1) * traits.InfantryPerHeavy;

		for (int i = costs.Length - 1; i >= 1; i--)
		{
			if (screened && costs[i] > 0 && balance >= costs[i])
			{
				tier = (byte)i;
				return true;
			}
		}

		return costs[0] > 0 && balance >= costs[0];
	}
}
