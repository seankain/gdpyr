using System;

namespace Gdpyr.Sim.Agent;

/// <summary>What happened (docs/AGENT_API.md §8).</summary>
public enum AgentEventKind : byte
{
	RoundStart = 0,
	RoundEnd = 1,
	Kill = 2,
	Damage = 3,
	UnitBuilt = 4,
	UnitLost = 5,
	NodeCaptured = 6,
	SeatAttached = 7,
	SeatReleased = 8,
}

/// <summary>
/// One record on the event stream.
///
/// **No reward function lives in the game.** What a trainer values is the
/// trainer's business, and baking one in would be a gameplay claim disguised as
/// plumbing. What the game owes is the raw record, because otherwise every
/// trainer re-derives kills from position deltas and gets it slightly wrong.
///
/// Three integers and two floats rather than a class per kind: the stream is
/// written on the tick and read off a socket, and
/// <see cref="AgentEventSchema"/> names the slots per kind so the JSON stays
/// readable and the layout stays allocation-free.
/// </summary>
public struct AgentEvent
{
	public AgentEventKind Kind;
	public uint Tick;
	public int A;
	public int B;
	public int C;
	public float X;
	public float Y;

	public static AgentEvent Make(AgentEventKind kind, uint tick, int a = 0, int b = 0, int c = 0,
		float x = 0f, float y = 0f) =>
		new() { Kind = kind, Tick = tick, A = a, B = b, C = c, X = x, Y = y };
}

/// <summary>
/// What each slot of an <see cref="AgentEvent"/> means, per kind. Published with
/// the observation schema at <c>welcome</c>, so a client names fields rather than
/// indexing a tuple.
/// </summary>
public static class AgentEventSchema
{
	private static readonly string[][] Names =
	{
		new[] { "seed", "ground_tickets", "strategist_points", "duration_minutes", "" },      // RoundStart
		new[] { "outcome", "ground_tickets", "units_lost", "duration_minutes", "units_built" }, // RoundEnd
		new[] { "attacker", "victim", "weapon", "distance", "" },                              // Kill
		new[] { "attacker", "victim", "weapon", "amount", "remaining" },                       // Damage
		new[] { "unit", "tier", "barracks", "x", "z" },                                        // UnitBuilt
		new[] { "unit", "tier", "killer", "x", "z" },                                          // UnitLost
		new[] { "node", "owner", "claimant", "x", "z" },                                       // NodeCaptured
		// No slot may be called "kind" or "tick": WriteEvent already writes those two
		// for every record, and a duplicate key is a JSON document a strict reader
		// throws on rather than one it merges.
		new[] { "seat", "policy", "step_mul", "", "" },                                        // SeatAttached
		new[] { "seat", "policy", "reason", "", "" },                                          // SeatReleased
	};

	private static readonly string[] Kinds =
	{
		"round_start", "round_end", "kill", "damage", "unit_built", "unit_lost", "node_captured",
		"seat_attached", "seat_released",
	};

	public static string NameOf(AgentEventKind kind) =>
		(byte)kind < Kinds.Length ? Kinds[(byte)kind] : "unknown";

	/// <summary>Names for <c>A</c>, <c>B</c>, <c>C</c>, <c>X</c>, <c>Y</c>. An empty name means the slot is unused.</summary>
	public static ReadOnlySpan<string> FieldsOf(AgentEventKind kind) =>
		(byte)kind < Names.Length ? Names[(byte)kind] : Array.Empty<string>();
}

/// <summary>
/// The event stream: a fixed ring the tick writes into and sessions read out of
/// by sequence number.
///
/// Fixed rather than grown because it is written from inside the simulation tick,
/// and a reader that has fallen far enough behind to lose records is told how many
/// it lost rather than being allowed to believe it saw everything.
/// </summary>
public sealed class AgentEventLog
{
	/// <summary>Records retained. ~8 seconds of a busy round.</summary>
	public const int Capacity = 1024;

	private readonly AgentEvent[] _ring = new AgentEvent[Capacity];

	/// <summary>Records ever written. A reader's cursor is one of these.</summary>
	public ulong Sequence { get; private set; }

	/// <summary>Records overwritten before anyone read them.</summary>
	public ulong Dropped { get; private set; }

	public void Emit(in AgentEvent record)
	{
		_ring[(int)(Sequence % Capacity)] = record;
		Sequence++;
	}

	/// <summary>
	/// Copies every record written since <paramref name="cursor"/>.
	///
	/// A cursor older than the ring is advanced to the oldest record still held and
	/// the gap is reported in <paramref name="lost"/>, because a trainer that
	/// silently misses a kill would train on a reward it never saw.
	/// </summary>
	public int Since(ulong cursor, Span<AgentEvent> into, out ulong next, out ulong lost)
	{
		lost = 0;
		ulong oldest = Sequence > Capacity ? Sequence - Capacity : 0;

		if (cursor < oldest)
		{
			lost = oldest - cursor;
			cursor = oldest;
		}

		int count = 0;
		while (cursor < Sequence && count < into.Length)
		{
			into[count++] = _ring[(int)(cursor % Capacity)];
			cursor++;
		}

		next = cursor;
		return count;
	}

	public void Clear()
	{
		Sequence = 0;
		Dropped = 0;
		Array.Clear(_ring);
	}
}

/// <summary>
/// Where the game posts events, without having to know whether anybody is
/// listening.
///
/// <see cref="Active"/> is null unless <c>--agent-api</c> was passed, so the call
/// sites scattered through <c>CombatManager</c>, <c>UnitManager</c> and
/// <c>EconomyService</c> cost a null check on a round nobody is watching. This
/// stream is also most of M8's per-round CSV, which is the ordering argument for
/// building it first (docs/AGENT_API.md §8).
/// </summary>
public static class AgentEventBus
{
	/// <summary>Set by the agent server while it is listening; null otherwise.</summary>
	public static AgentEventLog Active { get; set; }

	public static void Emit(AgentEventKind kind, uint tick, int a = 0, int b = 0, int c = 0,
		float x = 0f, float y = 0f) =>
		Active?.Emit(AgentEvent.Make(kind, tick, a, b, c, x, y));
}
