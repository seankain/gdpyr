using System;
using System.Collections.Generic;

namespace Gdpyr.Sim.Agent;

/// <summary>Which half of the game a seat plays (docs/AGENT_API.md §2.2).</summary>
public enum AgentPolicyKind : byte
{
	Ground = 0,
	Strategist = 1,
}

/// <summary>Who is behind a roster slot, as <c>list_seats</c> reports it.</summary>
public enum AgentSeatController : byte
{
	Human = 0,
	Bot = 1,
	Agent = 2,
}

/// <summary>Why <c>attach</c> said no.</summary>
public enum AgentAttachRefusal : byte
{
	None = 0,

	/// <summary>The one refusal <c>attach</c> has: an agent may not take a seat a person is sitting in.</summary>
	SeatIsHuman,

	/// <summary>Another session holds it.</summary>
	SeatIsTaken,

	/// <summary>The side is at its target and the roster has no room.</summary>
	NoFreeSeat,
}

/// <summary>
/// One attached seat: the lease a policy holds on a bot
/// (docs/AGENT_API.md §2, §2.1).
///
/// A policy does not connect as a peer and does not get a peer id of its own — it
/// claims a seat <c>BotDirector</c> has already created, and keeps everything the
/// seat already has: a character, a team, a loadout, a roster slot, a ticket when
/// it dies. What this class holds is only the part that is new: whose socket owns
/// it, how often it decides, and what it last asked for.
///
/// Engine-free so that the interesting question — what happens when the trainer
/// crashes mid-round — is answered by a test rather than by a stopwatch.
/// </summary>
public sealed class AgentSeat
{
	public AgentSeat(int peerId, AgentPolicyKind kind, int sessionId, int stepMul, uint tick)
	{
		PeerId = peerId;
		Kind = kind;
		SessionId = sessionId;
		StepMul = Math.Clamp(stepMul, 1, MaxStepMul);
		AttachedTick = tick;
		LastActionTick = tick;
	}

	/// <summary>Ceiling on action repeat: ten seconds of holding one frame is already absurd.</summary>
	public const int MaxStepMul = SimConfig.TickRate * 10;

	/// <summary>Action repeat for a ground policy: 15 Hz (docs/AGENT_API.md §5.3).</summary>
	public const int DefaultGroundStepMul = 4;

	/// <summary>Action repeat for a strategist: 2 Hz, about what a strategist decision is worth.</summary>
	public const int DefaultStrategistStepMul = 30;

	public int PeerId { get; }

	public AgentPolicyKind Kind { get; }

	/// <summary>The session that holds the lease. Closing it releases the seat outright.</summary>
	public int SessionId { get; }

	public int StepMul { get; }

	public uint AttachedTick { get; }

	/// <summary>The last tick an action was submitted for this seat.</summary>
	public uint LastActionTick { get; private set; }

	/// <summary>False until the first action arrives; the seat is piloted by a bot until then.</summary>
	public bool HasAction { get; private set; }

	/// <summary>The action being held for this seat's <c>step_mul</c>.</summary>
	public AgentGroundAction Action;

	/// <summary>Commands this seat has spent, for a strategist policy (docs/AGENT_API.md §7.3).</summary>
	public AgentCommandBudget Budget;

	/// <summary>Actions submitted, and actions the grace window had to cover. For the trace.</summary>
	public int ActionsApplied { get; private set; }

	public int PilotFallbacks { get; private set; }

	/// <summary>Records an action for the current decision.</summary>
	public void Submit(uint tick, in AgentGroundAction action)
	{
		Action = action;
		LastActionTick = tick;
		HasAction = true;
		ActionsApplied++;
	}

	/// <summary>
	/// Ticks on which this seat is asked for a fresh action. Aligned to the tick the
	/// lease was taken on, so two seats attached a tick apart do not both decide on
	/// the same tick for the rest of the round.
	/// </summary>
	public bool IsDecisionTick(uint tick) => StepMul <= 1 || (tick - AttachedTick) % (uint)StepMul == 0;

	/// <summary>
	/// Whether the seat falls back to <c>BotPilot</c> this tick: it has never acted,
	/// or it has not acted for the grace window.
	///
	/// Silent by design. A policy running slower than 60 Hz is the normal case, not
	/// an error, and the alternative — a body standing in the open because a Python
	/// process is garbage collecting — is worse than a bot taking the wheel for a
	/// third of a second (docs/AGENT_API.md §2.1).
	/// </summary>
	public bool ShouldPilot(uint tick, int graceTicks)
	{
		if (!HasAction)
		{
			return true;
		}

		return tick > LastActionTick && tick - LastActionTick > (uint)Math.Max(graceTicks, 0);
	}

	public void CountFallback() => PilotFallbacks++;
}

/// <summary>
/// Every lease the agent socket holds, and the rules for taking one
/// (docs/AGENT_API.md §2.2).
/// </summary>
public sealed class AgentSeatBook
{
	/// <summary>
	/// Ticks a seat may go without an action before its bot takes over — half a
	/// second (docs/AGENT_API.md §2.1).
	/// </summary>
	public const int DefaultGraceTicks = SimConfig.TickRate / 2;

	private readonly Dictionary<int, AgentSeat> _seats = new();

	public int Count => _seats.Count;

	public IEnumerable<AgentSeat> Seats => _seats.Values;

	public bool IsAttached(int peerId) => _seats.ContainsKey(peerId);

	public bool TryGet(int peerId, out AgentSeat seat) => _seats.TryGetValue(peerId, out seat);

	/// <summary>
	/// Claims <paramref name="peerId"/> for <paramref name="sessionId"/>.
	///
	/// <paramref name="controller"/> is what the roster says the seat is *now*,
	/// supplied by the caller because the roster is the engine's business and this
	/// class is not allowed to know about it.
	/// </summary>
	public AgentSeat Attach(int peerId, AgentSeatController controller, AgentPolicyKind kind, int sessionId,
		int stepMul, uint tick, out AgentAttachRefusal refusal)
	{
		refusal = AgentAttachRefusal.None;

		if (controller == AgentSeatController.Human)
		{
			refusal = AgentAttachRefusal.SeatIsHuman;
			return null;
		}

		if (_seats.TryGetValue(peerId, out AgentSeat existing))
		{
			// A caller may name a peer id to re-attach to a seat it held before a
			// reconnect; another session's lease is refused.
			if (existing.SessionId != sessionId)
			{
				refusal = AgentAttachRefusal.SeatIsTaken;
				return null;
			}

			_seats.Remove(peerId);
		}

		var seat = new AgentSeat(peerId, kind, sessionId, stepMul, tick);
		_seats[peerId] = seat;
		return seat;
	}

	/// <summary>Releases one seat back to its bot. True when there was one to release.</summary>
	public bool Detach(int peerId) => _seats.Remove(peerId);

	/// <summary>
	/// Releases every seat a session held — what a closed socket does. A crashed
	/// trainer cannot leave a body standing in the open or stall a round.
	/// </summary>
	public int ReleaseSession(int sessionId, List<int> released = null)
	{
		int count = 0;
		List<int> doomed = null;

		foreach (KeyValuePair<int, AgentSeat> pair in _seats)
		{
			if (pair.Value.SessionId != sessionId)
			{
				continue;
			}

			(doomed ??= new List<int>()).Add(pair.Key);
		}

		if (doomed == null)
		{
			return 0;
		}

		for (int i = 0; i < doomed.Count; i++)
		{
			if (_seats.Remove(doomed[i]))
			{
				released?.Add(doomed[i]);
				count++;
			}
		}

		return count;
	}

	public void Clear() => _seats.Clear();

	/// <summary>
	/// The default action repeat for a policy kind: 15 Hz on the ground, 2 Hz in the
	/// strategist's chair.
	/// </summary>
	public static int DefaultStepMul(AgentPolicyKind kind) =>
		kind == AgentPolicyKind.Strategist
			? AgentSeat.DefaultStrategistStepMul
			: AgentSeat.DefaultGroundStepMul;
}
