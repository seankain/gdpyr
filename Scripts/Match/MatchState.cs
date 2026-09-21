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

	/// <summary>The ground force ran out of tickets.</summary>
	GroundForceEliminated,

	/// <summary>The clock ran out with tickets left.</summary>
	TimeExpired,
}

/// <summary>
/// The round: its phase, the ground force's ticket pool and its clock.
///
/// Engine-free and server-authoritative. It holds only what M2 needs — the
/// strategist's points, resource nodes and the full win condition are M5
/// (docs/IMPLEMENTATION_PLAN.md §M5) — but it is the thing those extend, so it
/// lives under <c>Scripts/Match</c> and is unit-tested rather than being three
/// fields on a manager.
///
/// Time is counted in ticks, not seconds: a round that lasts a different length
/// on a server having a bad minute is a measurement the playtest instrumentation
/// (M6) cannot use.
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

	/// <summary>Bumped on every change worth replicating, so a client can ignore a stale message.</summary>
	public uint Version { get; private set; }

	public bool IsLive => Phase == RoundPhase.Live;

	public void Start(uint tick, int groundTickets, int durationTicks)
	{
		Phase = RoundPhase.Live;
		Outcome = RoundOutcome.Undecided;
		StartingGroundTickets = Math.Max(groundTickets, 0);
		GroundTickets = StartingGroundTickets;
		GroundDeaths = 0;
		StartTick = tick;
		EndTick = tick + (uint)Math.Max(durationTicks, 0);
		Version++;
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
		uint endTick, uint version)
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
		Version++;
	}
}
