using System;

namespace Gdpyr.Sim;

/// <summary>
/// Who holds a resource node (docs/IMPLEMENTATION_PLAN.md §M5).
///
/// Not <see cref="Team"/>, because a node starts out belonging to nobody and
/// "nobody" is a third value. It rides one byte of the node's state message, so
/// the order is a protocol constant like every other enum on this wire.
/// </summary>
public enum NodeHolder : byte
{
	Neutral = 0,

	GroundForce = 1,

	Strategist = 2,
}

/// <summary>
/// One resource node's tuning, in ticks rather than seconds, for the reason every
/// other cadence in this simulation is: a capture that completed on a different
/// tick on two peers would be a capture two peers disagree about.
/// </summary>
public readonly struct CaptureRules
{
	/// <summary>Ticks of uncontested presence that flip a node.</summary>
	public readonly int CaptureTicks;

	/// <summary>Ticks between two occupancy scans. Progress advances by one of these per scan.</summary>
	public readonly int IntervalTicks;

	/// <summary>Ticks between two income payments while the strategist holds it uncontested.</summary>
	public readonly int IncomeIntervalTicks;

	/// <summary>Points paid per payment.</summary>
	public readonly int IncomePoints;

	public CaptureRules(int captureTicks, int intervalTicks, int incomeIntervalTicks, int incomePoints)
	{
		CaptureTicks = Math.Max(captureTicks, 1);
		IntervalTicks = Math.Max(intervalTicks, 1);
		IncomeIntervalTicks = Math.Max(incomeIntervalTicks, 1);
		IncomePoints = Math.Max(incomePoints, 0);
	}

	/// <summary>Points per minute while held, for the HUD and for tuning arguments.</summary>
	public float PointsPerMinute => IncomePoints * SimConfig.TickRate * 60f / IncomeIntervalTicks;
}

/// <summary>
/// One resource node's capture and income, as engine-free state
/// (docs/IMPLEMENTATION_PLAN.md §M5).
///
/// The rule is presence, not a builder unit: §5 of the plan cuts builders from the
/// first pass because a proximity test exercises the same economy loop with far
/// less code. So a side that stands on a node alone takes it, a side that stands
/// on its own node undoes whatever the other side had started, and both sides at
/// once freezes everything — which is what makes a node worth contesting rather
/// than worth visiting once.
///
/// Progress is counted in ticks rather than in a float, so a capture completes on
/// the same tick however it was scheduled, and income is paid on a tick index
/// rather than accumulated — two peers must agree about how much the strategist
/// has, and a float that accumulates sixty times a second will not.
/// </summary>
public sealed class CaptureState
{
	public NodeHolder Owner { get; private set; }

	/// <summary>The side currently pushing a capture, or <see cref="NodeHolder.Neutral"/>.</summary>
	public NodeHolder Claimant { get; private set; }

	/// <summary>Ticks of progress the claimant has accumulated, out of <see cref="CaptureRules.CaptureTicks"/>.</summary>
	public int ProgressTicks { get; private set; }

	/// <summary>True while both sides are inside the radius. Nothing moves and nothing is paid.</summary>
	public bool Contested { get; private set; }

	/// <summary>Points this node has paid the strategist this round. For the HUD and M6's CSV.</summary>
	public int Paid { get; private set; }

	/// <summary>Bumped on every change worth replicating, so a client can ignore a stale message.</summary>
	public uint Version { get; private set; }

	private uint _nextIncomeTick;

	/// <summary>Capture progress in [0,1], for a bar.</summary>
	public float Progress(in CaptureRules rules) =>
		Math.Clamp(ProgressTicks / (float)rules.CaptureTicks, 0f, 1f);

	public bool IsHeldBy(NodeHolder holder) => Owner == holder && holder != NodeHolder.Neutral;

	/// <summary>
	/// One occupancy scan. Returns the points the node paid on this scan, which is
	/// 0 on all but one scan in <see cref="CaptureRules.IncomeIntervalTicks"/>.
	///
	/// The caller counts the bodies; this function has no opinion about how far
	/// away they were.
	/// </summary>
	public int Tick(uint tick, int groundOccupants, int strategistOccupants, in CaptureRules rules)
	{
		bool ground = groundOccupants > 0;
		bool strategist = strategistOccupants > 0;
		bool contested = ground && strategist;

		if (contested != Contested)
		{
			Contested = contested;
			Version++;
		}

		// One rifleman standing on a node stops it paying, whether or not anybody is
		// there to stop him. Denial is the whole reason the ground force would walk
		// to one of these rather than towards the barracks.
		bool paying = Owner == NodeHolder.Strategist && !ground;

		if (contested)
		{
			// Frozen, not reset: a contested node is a firefight, and a firefight one
			// side walks away from should leave the other where it got to.
			return Income(tick, rules, paying);
		}

		NodeHolder present = ground ? NodeHolder.GroundForce
			: strategist ? NodeHolder.Strategist
			: NodeHolder.Neutral;

		if (present == NodeHolder.Neutral || present == Owner)
		{
			// Nobody, or the owner: whatever the other side had started decays. The
			// owner standing on it is not faster than nobody standing on it — holding
			// ground you already hold is not work.
			Decay(rules);
			return Income(tick, rules, paying);
		}

		if (present != Claimant)
		{
			Claimant = present;
			ProgressTicks = 0;
			Version++;
		}

		ProgressTicks += rules.IntervalTicks;
		Version++;

		if (ProgressTicks < rules.CaptureTicks)
		{
			return Income(tick, rules, paying);
		}

		Owner = present;
		Claimant = NodeHolder.Neutral;
		ProgressTicks = 0;

		// The payment clock restarts on a flip, so taking a node does not pay out on
		// the tick it was taken.
		_nextIncomeTick = tick + (uint)rules.IncomeIntervalTicks;
		return 0;
	}

	/// <summary>Applies a replicated summary on a client. The server never calls this.</summary>
	public void Apply(NodeHolder owner, NodeHolder claimant, bool contested, float progress,
		in CaptureRules rules)
	{
		Owner = owner;
		Claimant = claimant;
		Contested = contested;
		ProgressTicks = (int)Math.Clamp(progress * rules.CaptureTicks, 0f, rules.CaptureTicks);
		Version++;
	}

	/// <summary>Back to nobody's, for the next round.</summary>
	public void Reset()
	{
		Owner = NodeHolder.Neutral;
		Claimant = NodeHolder.Neutral;
		ProgressTicks = 0;
		Contested = false;
		Paid = 0;
		_nextIncomeTick = 0;
		Version++;
	}

	private void Decay(in CaptureRules rules)
	{
		if (ProgressTicks == 0)
		{
			return;
		}

		ProgressTicks = Math.Max(0, ProgressTicks - rules.IntervalTicks);
		if (ProgressTicks == 0)
		{
			Claimant = NodeHolder.Neutral;
		}
		Version++;
	}

	private int Income(uint tick, in CaptureRules rules, bool paying)
	{
		if (!paying)
		{
			// The clock is pushed along while nothing is being paid, so a node that
			// changes hands does not immediately pay out the interval it spent in
			// somebody else's hands.
			_nextIncomeTick = tick + (uint)rules.IncomeIntervalTicks;
			return 0;
		}

		if (_nextIncomeTick == 0)
		{
			_nextIncomeTick = tick + (uint)rules.IncomeIntervalTicks;
			return 0;
		}

		if (tick < _nextIncomeTick)
		{
			return 0;
		}

		_nextIncomeTick = tick + (uint)rules.IncomeIntervalTicks;
		Paid += rules.IncomePoints;
		Version++;
		return rules.IncomePoints;
	}
}
