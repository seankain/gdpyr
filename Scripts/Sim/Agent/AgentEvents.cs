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
	StructurePlaced = 9,
	StructureBuilt = 10,
	StructureLost = 11,

	/// <summary>
	/// A computer strategist's commander squad took a new goal (docs/HTN_BOTS.md §5.3,
	/// H6). Only <c>--bot-ai htn</c> has squads; legacy never emits one.
	/// </summary>
	SquadTask = 12,

	/// <summary>
	/// The strategist side's fog picked up a ground-force player it did not have in
	/// sight at the refresh before (docs/NETCODE.md §6.2, H6). Either bot AI.
	/// </summary>
	ContactSpotted = 13,

	/// <summary>
	/// A resource node became contested — both sides inside its radius — or stopped
	/// being, with how long it was (docs/IMPLEMENTATION_PLAN.md §M5, H6).
	/// </summary>
	NodeContested = 14,

	/// <summary>A unit got into a vehicle (docs/VEHICLES.md §10).</summary>
	UnitEmbarked = 15,

	/// <summary>A unit got out of a vehicle: an unload, or thrown out alive from a wreck.</summary>
	UnitDisembarked = 16,

	/// <summary>A dropship touched down, with how many were aboard (V6).</summary>
	DropshipLanded = 17,

	/// <summary>
	/// A hand grenade left somebody's hand (docs/NETCODE.md §10.9): a player's or a
	/// unit's, and for a unit whether what it threw at was hidden from it
	/// (docs/TACTICS.md §5.4). Either bot AI.
	/// </summary>
	GrenadeThrown = 18,

	/// <summary>
	/// A unit started one of the tactics of docs/TACTICS.md §5 — taking cover from fire,
	/// firing back from it, a grenade at a hidden enemy, a marksman's hiding, peeking and
	/// relocating — by its <c>UnitGoal</c> number.
	/// Only a unit whose micro-autonomy is on, under <c>--bot-ai htn</c>.
	/// </summary>
	UnitTask = 19,

	/// <summary>
	/// A board squad's drill changed phase (docs/TACTICS.md §5.5): to ground, setting its
	/// base, bounding, grenades, the rush, and back to approaching or holding the point,
	/// by its <c>DrillPhase</c> number; 0 when the squad stops drilling. Only a squad with
	/// two units or more whose micro-autonomy is on, under <c>--bot-ai htn</c>.
	/// </summary>
	SquadDrill = 20,

	/// <summary>
	/// A player took a kit at a weapon locker (docs/LOADOUT.md §5.3): a person's request,
	/// a bot's locker run or a policy's <c>loadout</c> op, through the one validator.
	/// </summary>
	Kit = 21,
}

/// <summary>
/// One record on the event stream.
///
/// **No reward function lives in the game.** What a trainer values is the
/// trainer's business, and baking one in would be a gameplay claim disguised as
/// plumbing. What the game owes is the raw record, because otherwise every
/// trainer re-derives kills from position deltas and gets it slightly wrong.
///
/// Three integers and three floats rather than a class per kind: the stream is
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

	/// <summary>
	/// The sixth slot, added for a damage record's <c>absorbed</c> (docs/LOADOUT.md
	/// §5.2) when the five were all spoken for. Unnamed, and so never written, for
	/// every kind that does not use it.
	/// </summary>
	public float Z;

	public static AgentEvent Make(AgentEventKind kind, uint tick, int a = 0, int b = 0, int c = 0,
		float x = 0f, float y = 0f, float z = 0f) =>
		new() { Kind = kind, Tick = tick, A = a, B = b, C = c, X = x, Y = y, Z = z };
}

/// <summary>
/// What each slot of an <see cref="AgentEvent"/> means, per kind. Published with
/// the observation schema at <c>welcome</c>, so a client names fields rather than
/// indexing a tuple.
/// </summary>
public static class AgentEventSchema
{
	/// <summary>
	/// A kill's <c>rounds_fired</c> when the victim was a unit or a structure, which
	/// carries no primary for it to count.
	/// </summary>
	public const float NoRoundsFired = -1f;

	/// <summary>A <c>kit</c> record's <c>primary</c> for a kit with none.</summary>
	public const int NoPrimary = -1;

	private static readonly string[][] Names =
	{
		new[] { "seed", "ground_tickets", "strategist_points", "duration_minutes", "", "" },  // RoundStart
		new[] { "outcome", "ground_tickets", "units_lost", "duration_minutes", "units_built", "" }, // RoundEnd
		// rounds_fired: the rounds a player victim's large slot fired in the life that just
		// ended (docs/LOADOUT.md L0); NoRoundsFired for a unit or a structure.
		new[] { "attacker", "victim", "weapon", "distance", "rounds_fired", "" },              // Kill
		// amount: the health a player victim lost, what a reward reads; absorbed: what its
		// vest took on top of that (docs/LOADOUT.md §5.2), 0 with none and for a unit or a
		// structure, whose armour is a scale and not a vest.
		new[] { "attacker", "victim", "weapon", "amount", "remaining", "absorbed" },           // Damage
		// A unit a sniper tower or pillbox trained names it as barracks -(slot + 1)
		// (docs/NETCODE.md §10.8): negative is "not a barracks" to a reader that only knows those.
		new[] { "unit", "tier", "barracks", "x", "z", "" },                                    // UnitBuilt
		new[] { "unit", "tier", "killer", "x", "z", "" },                                      // UnitLost
		new[] { "node", "owner", "claimant", "x", "z", "" },                                   // NodeCaptured
		// No slot may be called "kind" or "tick": WriteEvent already writes those two
		// for every record, and a duplicate key is a JSON document a strict reader
		// throws on rather than one it merges.
		new[] { "seat", "policy", "step_mul", "", "", "" },                                    // SeatAttached
		new[] { "seat", "policy", "reason", "", "", "" },                                      // SeatReleased
		// A structure is named by its slot here and by OwnerId.ForStructure(slot) as
		// the attacker or victim of a kill or damage record (docs/NETCODE.md §10.5).
		new[] { "structure", "type", "builders", "x", "z", "" },                               // StructurePlaced
		new[] { "structure", "type", "", "x", "z", "" },                                       // StructureBuilt
		new[] { "structure", "type", "killer", "x", "z", "" },                                 // StructureLost
		// The strategist's peer id, the commander's squad slot, and the goal by its
		// CommandGoal number (0 when the squad disbands); x and z are where the goal's
		// order points.
		new[] { "strategist", "squad", "goal", "x", "z", "" },                                // SquadTask
		// The player spotted, the tier of the unit that saw it (-1 for a structure),
		// and 1 when nothing had seen it before this round.
		new[] { "peer", "observer", "first", "x", "z", "" },                                   // ContactSpotted
		// Who holds the node; 1 when the contest starts and 0 when it ends; and for an
		// end, the ticks it lasted. One that the round ends in the middle of has no end.
		new[] { "node", "owner", "contested", "ticks", "", "" },                              // NodeContested
		// The passenger, the vehicle it got into or out of, the passenger's tier, and
		// where it was: at the vehicle for a boarding, at the place it got out for the other.
		new[] { "unit", "vehicle", "tier", "x", "z", "" },                                     // UnitEmbarked
		new[] { "unit", "vehicle", "tier", "x", "z", "" },                                     // UnitDisembarked
		new[] { "unit", "aboard", "", "x", "z", "" },                                          // DropshipLanded
		// Who threw it as an owner id; its catalog id, -1 for a player; 1 when a unit threw
		// at something hidden from it, 0 when it could see it, -1 for a player, whose aim
		// is its own; and where the thrower stood.
		new[] { "thrower", "tier", "hidden", "x", "z", "" },                                   // GrenadeThrown
		// The unit, its catalog id, the goal by its UnitGoal number — 18 take cover from
		// fire, 19 return fire, 20 throw a grenade, 21 to hide, 22 pop out, 23 fire from a
		// peek, 24 duck, 25 relocate, 26 suppress a place, 27 fill its belt, and in its
		// squad's drill 28 to ground, 29 to a fire position, 30 bound, 31 hold and 32 rush —
		// and where it was when it started it.
		new[] { "unit", "tier", "goal", "x", "z", "" },                                        // UnitTask
		// The board squad's slot, the phase by its DrillPhase number — 1 approach, 2 hold the
		// point, 3 go to ground, 4 set the base, 5 bound, 6 grenades, 7 rush, 0 no drill —
		// how many of its units take part, and its threat point, else where its order goes.
		new[] { "squad", "phase", "elements", "x", "z", "" },                                  // SquadDrill
		// The player; the primary's catalog id, -1 for none; the vest's tier, 0 none to 3
		// heavy; and the pouches: magazines and grenades (docs/LOADOUT.md §5.3).
		new[] { "peer", "primary", "armour", "magazines", "grenades", "" },                    // Kit
	};

	private static readonly string[] Kinds =
	{
		"round_start", "round_end", "kill", "damage", "unit_built", "unit_lost", "node_captured",
		"seat_attached", "seat_released", "structure_placed", "structure_built", "structure_lost",
		"squad_task", "contact_spotted", "node_contested", "unit_embarked", "unit_disembarked", "dropship_landed",
		"grenade_thrown", "unit_task", "squad_drill", "kit",
	};

	/// <summary>The highest kind there is. Everything from 0 to this has a name and a slate of fields.</summary>
	public static AgentEventKind Last => (AgentEventKind)(Kinds.Length - 1);

	public static string NameOf(AgentEventKind kind) =>
		(byte)kind < Kinds.Length ? Kinds[(byte)kind] : "unknown";

	/// <summary>Names for <c>A</c>, <c>B</c>, <c>C</c>, <c>X</c>, <c>Y</c>, <c>Z</c>. An empty name means the slot is unused.</summary>
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
		float x = 0f, float y = 0f, float z = 0f) =>
		Active?.Emit(AgentEvent.Make(kind, tick, a, b, c, x, y, z));
}
