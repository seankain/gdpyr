using System;
using Gdpyr.Sim;

namespace Gdpyr.Match;

public enum RoundPhase : byte
{
	/// <summary>Before the round starts: players may shoot, nothing is counted.</summary>
	Warmup,

	Live,

	Ended,
}

public enum RoundOutcome : byte
{
	Undecided,

	/// <summary>The ground force ran out of tickets. The strategist wins.</summary>
	GroundForceEliminated,

	/// <summary>
	/// The clock ran out with tickets left. The ground force wins: twenty minutes
	/// with a ticket left is what surviving an assault looks like
	/// (docs/IMPLEMENTATION_PLAN.md §M5).
	/// </summary>
	TimeExpired,

	/// <summary>
	/// Retired: no round ends this way any more. The strategist had no points, no
	/// units, nothing building and no node held. It now directs the waves rather than
	/// playing an economy it can lose (<see cref="BaselineIncome"/>), and loses only on
	/// the clock. The value stays, as every value on this wire does: a demo, a
	/// scoreboard or an agent trace written before it was retired still reads 3.
	/// </summary>
	StrategistEliminated,
}

/// <summary>
/// The round: its phase, the ground force's ticket pool, the strategist's points
/// and its clock.
///
/// Engine-free and server-authoritative. The round ends two ways: the ground force
/// runs out of tickets, and the strategist wins; or the clock runs out with tickets
/// left, and the ground force wins. The strategist cannot run out of the round: its
/// points are the nodes' income plus a baseline nothing can stop
/// (<see cref="BaselineIncome"/>), so it can always send a wave.
///
/// Time is counted in ticks, not seconds: a round that lasts a different length
/// on a server having a bad minute is a measurement the playtest instrumentation
/// (M8) cannot use.
/// </summary>
public sealed class MatchState
{
	public RoundPhase Phase { get; private set; } = RoundPhase.Warmup;

	public RoundOutcome Outcome { get; private set; } = RoundOutcome.Undecided;

	/// <summary>Ground-force lives left. A death costs one; at zero the round is over.</summary>
	public int GroundTickets { get; private set; }

	public int StartingGroundTickets { get; private set; }

	/// <summary>The tick the round went live on.</summary>
	public uint StartTick { get; private set; }

	/// <summary>The tick the round is scheduled to end on, or did end on.</summary>
	public uint EndTick { get; private set; }

	/// <summary>Ground-force deaths this round. Not simply the ticket difference once M5 adds refunds.</summary>
	public int GroundDeaths { get; private set; }

	/// <summary>
	/// Which side won, or null while the round is undecided. Derived rather than
	/// stored: there is one outcome and two ways of reading it, and two fields would
	/// eventually disagree.
	/// </summary>
	public Team? Winner => Outcome switch
	{
		RoundOutcome.GroundForceEliminated => Team.Strategist,
		RoundOutcome.TimeExpired => Team.GroundForce,
		RoundOutcome.StrategistEliminated => Team.GroundForce,
		_ => null,
	};

	/// <summary>How long the round has run, in ticks. Reads as its full length once it is over.</summary>
	public uint ElapsedTicks(uint tick) =>
		Phase == RoundPhase.Warmup ? 0u : (Phase == RoundPhase.Ended ? EndTick : tick) - StartTick;

	/// <summary>Bumped on every change worth replicating, so a client can ignore a stale message.</summary>
	public uint Version { get; private set; }

	/// <summary>
	/// The strategist's points: what units cost come out of here
	/// (docs/IMPLEMENTATION_PLAN.md §M3). Filled at the start of the round, paid into
	/// by the nodes it holds (M5) and by <see cref="Baseline"/>.
	/// </summary>
	public ResourceLedger Strategist { get; } = new();

	public int StrategistPoints => Strategist.Balance;

	/// <summary>What the strategist is paid this round whatever it holds. Server-side.</summary>
	public BaselineIncome Baseline { get; private set; }

	/// <summary>Points <see cref="Baseline"/> has paid this round. Server-side; for the HUD and M8's CSV.</summary>
	public int BaselinePaid { get; private set; }

	private uint _nextBaselineTick;

	public bool IsLive => Phase == RoundPhase.Live;

	public void Start(uint tick, int groundTickets, int strategistPoints, int durationTicks,
		BaselineIncome baseline = default)
	{
		Phase = RoundPhase.Live;
		Outcome = RoundOutcome.Undecided;
		StartingGroundTickets = Math.Max(groundTickets, 0);
		GroundTickets = StartingGroundTickets;
		GroundDeaths = 0;
		StartTick = tick;
		EndTick = tick + (uint)Math.Max(durationTicks, 0);
		Strategist.Reset(strategistPoints);
		Baseline = baseline;
		BaselinePaid = 0;
		// The first payment is one interval in, as a node's is after it is taken: the
		// starting pool is the payment for the first interval.
		_nextBaselineTick = tick + (uint)baseline.IntervalTicks;
		Version++;
	}

	/// <summary>
	/// Pays the strategist its baseline when one is due. Returns the points paid,
	/// which is 0 on all but one tick in <see cref="BaselineIncome.IntervalTicks"/>.
	///
	/// Every live tick, nodes or none, contested or not: it is the part of the
	/// strategist's income the ground force cannot deny. Payments are scheduled off
	/// the round's start rather than counted, so a tick the server skipped is paid on
	/// the next one rather than lost.
	/// </summary>
	public int PayBaseline(uint tick)
	{
		if (Phase != RoundPhase.Live || !Baseline.Pays)
		{
			return 0;
		}

		int paid = 0;
		while (tick >= _nextBaselineTick)
		{
			paid += Baseline.Points;
			_nextBaselineTick += (uint)Baseline.IntervalTicks;
		}

		if (paid > 0)
		{
			// The ledger bumps its own version, which is what replicates a balance.
			Strategist.Add(paid);
			BaselinePaid += paid;
		}

		return paid;
	}

	/// <summary>
	/// Charges the ground force for a death. Returns true when that was the death
	/// that ended the round.
	///
	/// A death outside a live round still counts as a death but costs nothing: a
	/// warmup kill that drained the pool would make the round start short.
	/// </summary>
	public bool RegisterGroundDeath(uint tick)
	{
		GroundDeaths++;
		Version++;

		if (Phase != RoundPhase.Live)
		{
			return false;
		}

		GroundTickets = Math.Max(0, GroundTickets - 1);
		if (GroundTickets > 0)
		{
			return false;
		}

		End(tick, RoundOutcome.GroundForceEliminated);
		return true;
	}

	/// <summary>
	/// Advances the clock. Returns true on the tick the round runs out of time.
	/// </summary>
	public bool Advance(uint tick)
	{
		if (Phase != RoundPhase.Live || tick < EndTick)
		{
			return false;
		}

		End(tick, RoundOutcome.TimeExpired);
		return true;
	}

	public void End(uint tick, RoundOutcome outcome)
	{
		Phase = RoundPhase.Ended;
		Outcome = outcome;
		EndTick = tick;
		Version++;
	}

	/// <summary>
	/// Seconds left on the clock, clamped at zero. Reads as the round's full length
	/// before it starts and as zero once it is over.
	/// </summary>
	public float SecondsRemaining(uint tick)
	{
		if (Phase == RoundPhase.Ended)
		{
			return 0f;
		}

		if (Phase == RoundPhase.Warmup)
		{
			return 0f;
		}

		return tick >= EndTick ? 0f : (EndTick - tick) * SimConfig.TickDelta;
	}

	/// <summary>Applies a replicated summary on a client. The server never calls this.</summary>
	public void Apply(RoundPhase phase, RoundOutcome outcome, int groundTickets, int startingTickets,
		int strategistPoints, uint endTick, uint version)
	{
		// Reliable RPCs arrive in order, but a client that joins mid-round gets the
		// roster replay and the live state in whichever order they were queued.
		if (version < Version)
		{
			return;
		}

		Phase = phase;
		Outcome = outcome;
		GroundTickets = groundTickets;
		StartingGroundTickets = startingTickets;
		EndTick = endTick;
		Version = version;

		// The ledger keeps its own version so that a strategist's balance can be
		// replicated more often than the round's phase changes; here it is simply
		// told the number, which is all a client is ever allowed to know about it.
		Strategist.Apply(strategistPoints, version);
	}

	public void Reset()
	{
		Phase = RoundPhase.Warmup;
		Outcome = RoundOutcome.Undecided;
		GroundTickets = 0;
		StartingGroundTickets = 0;
		GroundDeaths = 0;
		StartTick = 0;
		EndTick = 0;
		Strategist.Reset(0);
		Baseline = default;
		BaselinePaid = 0;
		_nextBaselineTick = 0;
		Version++;
	}
}
