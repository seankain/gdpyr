using System;

namespace Gdpyr.Sim.Agent;

/// <summary>The round, as the strategist's chair sees it (docs/AGENT_API.md §6.2).</summary>
public struct AgentStrategistRoundView
{
	/// <summary><see cref="RoundPhase"/>.</summary>
	public byte Phase;

	public float SecondsRemaining;
	public float SecondsTotal;

	/// <summary>Unspent points.</summary>
	public int Points;

	/// <summary>Points the nodes have paid this round, which is the income rate integrated.</summary>
	public int IncomePaid;

	public int GroundTickets;
	public int StartingGroundTickets;
	public int LiveUnits;
	public int QueuedUnits;
	public int UnitsLost;

	public uint Tick;

	/// <summary>The seat's action repeat, so a policy can align its own clock (docs/AGENT_API.md §5.3).</summary>
	public int StepMul;
}

/// <summary>One unit the seat owns.</summary>
public struct AgentUnitView
{
	public bool Alive;

	/// <summary>The catalog id: infantry, technical, tank (docs/IMPLEMENTATION_PLAN.md §M5).</summary>
	public byte Tier;

	public float X;
	public float Z;
	public float HealthFraction;

	/// <summary><see cref="OrderKind"/>, as stored — never <see cref="OrderKind.Stop"/>.</summary>
	public byte Order;

	public bool HasTarget;

	/// <summary>Distance to the order's target [m]. Meaningless when the order is none.</summary>
	public float TargetDistanceMeters;
}

/// <summary>One barracks the seat owns.</summary>
public struct AgentBarracksView
{
	public float X;
	public float Z;
	public int QueueDepth;
	public byte HeadTier;
	public float HeadProgress;
	public float RallyX;
	public float RallyZ;
}

/// <summary>One resource node, as everyone can see it: nodes are map geometry.</summary>
public struct AgentNodeView
{
	public float X;
	public float Z;

	/// <summary><see cref="NodeHolder"/>: neutral, ground force, strategist.</summary>
	public byte Owner;

	public bool Contested;
	public float CaptureProgress;

	/// <summary>Points this node has paid the strategist this round.</summary>
	public int IncomePaid;
}

/// <summary>
/// One ground-force contact, at the fog the human strategist is behind
/// (docs/IMPLEMENTATION_PLAN.md §M4).
///
/// A contact that is not visible right now is a <em>ghost</em>: where it was last
/// seen and how long ago, which is exactly what a human strategist's client draws
/// (docs/NETCODE.md §6.2).
/// </summary>
public struct AgentStrategistContactView
{
	/// <summary>False for a peer no unit has ever laid eyes on. Such a contact is not carried at all.</summary>
	public bool Known;

	/// <summary>True while a sensor has it now; false means the position is a ghost.</summary>
	public bool Visible;

	public float X;
	public float Z;
	public int TicksSinceSeen;
	public bool IsPlayer;

	/// <summary><see cref="Team"/>.</summary>
	public byte Team;
}

/// <summary>
/// The strategist observation: what a global, fogged policy is handed every
/// decision (docs/AGENT_API.md §6.2).
///
/// Built the same way the ground vector is — a field table the encoder writes
/// through a cursor, so the schema <c>welcome</c> publishes and the bytes the
/// encoder produces cannot drift apart. The unit, barracks, node and contact
/// blocks are the whole ceiling, zero-padded, so the tensor shape never changes
/// mid-episode.
///
/// Engine-free. The filtering — what this seat is allowed to know — happens
/// before the view structs are filled, in <c>VisibilityService</c>, which is the
/// same call that decides what a human strategist's snapshot may carry. What is
/// engine-free here, and therefore testable, is the rule that decides which
/// contacts reach the vector at all: <see cref="SelectContacts"/>.
/// </summary>
public static class AgentStrategistObservation
{
	/// <summary>Units carried. The whole <see cref="SimConfig.MaxUnits"/> ceiling, zero-padded.</summary>
	public const int MaxUnits = SimConfig.MaxUnits;

	public const int UnitFloats = 14;

	/// <summary>Barracks carried, which is the ceiling a map may have (<see cref="SimConfig.MaxBarracks"/>).</summary>
	public const int MaxBarracks = SimConfig.MaxBarracks;

	public const int BarracksFloats = 7;

	public const int MaxNodes = SimConfig.MaxResourceNodes;

	public const int NodeFloats = 8;

	/// <summary>Contacts carried: <c>SnapshotCodec.MaxPlayers</c>, for the same reason the units block is the unit cap.</summary>
	public const int MaxContacts = 16;

	public const int ContactFloats = 6;

	/// <summary>Order kinds in the one-hot: none, move, attack, patrol, defend. Stop is never stored.</summary>
	public const int OrderKinds = 5;

	/// <summary>Unit tiers in the one-hot (docs/IMPLEMENTATION_PLAN.md §M5).</summary>
	public const int Tiers = 3;

	/// <summary>Node owners in the one-hot: neutral, ground force, strategist.</summary>
	public const int NodeOwners = 3;

	// ---- normalization bounds, published with the schema -------------------

	/// <summary>Half-extent the map's coordinates are divided by. The ground vector's, so the two agree.</summary>
	public const float PositionScaleMeters = AgentObservation.PositionScaleMeters;

	public const float DistanceScaleMeters = AgentObservation.DistanceScaleMeters;

	/// <summary>Points the balance and the income are divided by, and clamped at.</summary>
	public const float PointsScale = 4000f;

	/// <summary>Units the loss counter is divided by: a long round loses a lot of infantry.</summary>
	public const float UnitsLostScale = SimConfig.MaxUnits * 4f;

	/// <summary>Ticks a ghost is normalized against before it saturates. The client's own fade.</summary>
	public const float GhostAgeScaleTicks = SimConfig.GhostLifetimeTicks;

	private static readonly AgentField[] Table = Build();

	/// <summary>Floats in one strategist observation.</summary>
	public static int Floats { get; } = Total(Table);

	/// <summary>Bytes in one strategist observation, packed float32.</summary>
	public static int Bytes => Floats * sizeof(float);

	/// <summary>The layout, in order. This is what <c>welcome</c> publishes.</summary>
	public static ReadOnlySpan<AgentField> Fields => Table;

	/// <summary>Where a named run starts, or -1. For tests and for readable assertions.</summary>
	public static int OffsetOf(string name)
	{
		for (int i = 0; i < Table.Length; i++)
		{
			if (Table[i].Name == name)
			{
				return Table[i].Offset;
			}
		}

		return -1;
	}

	private static AgentField[] Build()
	{
		int at = 0;
		AgentField Next(string name, int count, float min, float max)
		{
			var field = new AgentField(name, at, count, min, max);
			at += count;
			return field;
		}

		return new[]
		{
			// Round
			Next("round.phase", 1, 0f, 3f),
			Next("round.seconds_remaining", 1, 0f, 1f),
			Next("round.points", 1, 0f, 1f),
			Next("round.income_paid", 1, 0f, 1f),
			Next("round.ticket_fraction", 1, 0f, 1f),
			Next("round.live_units", 1, 0f, 1f),
			Next("round.queued_units", 1, 0f, 1f),
			Next("round.units_lost", 1, 0f, 1f),

			// The army, the doors, the map, and what can be seen of the enemy.
			Next("units", MaxUnits * UnitFloats, -1f, 1f),
			Next("barracks", MaxBarracks * BarracksFloats, -1f, 1f),
			Next("nodes", MaxNodes * NodeFloats, -1f, 1f),
			Next("contacts", MaxContacts * ContactFloats, -1f, 1f),
		};
	}

	private static int Total(AgentField[] table)
	{
		int total = 0;
		for (int i = 0; i < table.Length; i++)
		{
			total += table[i].Count;
		}

		return total;
	}

	/// <summary>
	/// The fog, as a rule rather than as a scan: which contacts reach the vector,
	/// and in what order.
	///
	/// A ground-force peer is carried iff the side has <em>ever</em> seen it —
	/// which is what <c>VisibilityService.TryContact</c> answers and what a human
	/// strategist's client draws a ghost from. Visibility right now is a flag on
	/// the record, not a condition of carrying it; a peer nobody has ever laid eyes
	/// on is not in the vector at all, and a policy cannot learn that the absence of
	/// a record means somebody is flanking, because an unseen enemy and a dead one
	/// look the same.
	///
	/// Visible contacts sort ahead of ghosts and ghosts sort by age, so the nearest
	/// thing to live information is always in the low slots — the ordering a
	/// convolutional or attention policy would otherwise have to learn.
	/// </summary>
	public static int SelectContacts(ReadOnlySpan<AgentStrategistContactView> candidates,
		Span<AgentStrategistContactView> into)
	{
		int count = 0;
		for (int i = 0; i < candidates.Length && count < into.Length; i++)
		{
			if (!candidates[i].Known)
			{
				continue;
			}

			// Insertion sort: sixteen records at 2 Hz, and it keeps the ordering rule
			// in one readable comparison rather than in a comparer somewhere else.
			int at = count++;
			while (at > 0 && Precedes(candidates[i], into[at - 1]))
			{
				into[at] = into[at - 1];
				at--;
			}

			into[at] = candidates[i];
		}

		return count;
	}

	private static bool Precedes(in AgentStrategistContactView a, in AgentStrategistContactView b)
	{
		if (a.Visible != b.Visible)
		{
			return a.Visible;
		}

		return a.TicksSinceSeen < b.TicksSinceSeen;
	}

	/// <summary>
	/// Writes one strategist observation. Returns the floats written, or 0 when
	/// <paramref name="into"/> is too small.
	///
	/// Every span is truncated at its ceiling and padded with zeros. Contacts are
	/// expected to have been through <see cref="SelectContacts"/>; passing a raw
	/// list works and is simply less ordered.
	/// </summary>
	public static int Encode(in AgentStrategistRoundView round, ReadOnlySpan<AgentUnitView> units,
		ReadOnlySpan<AgentBarracksView> barracks, ReadOnlySpan<AgentNodeView> nodes,
		ReadOnlySpan<AgentStrategistContactView> contacts, Span<float> into)
	{
		if (into.Length < Floats)
		{
			return 0;
		}

		into[..Floats].Clear();
		int at = 0;

		// ---- round ----------------------------------------------------------
		into[at++] = round.Phase;
		into[at++] = round.SecondsTotal > 0f
			? Math.Clamp(round.SecondsRemaining / round.SecondsTotal, 0f, 1f)
			: 0f;
		into[at++] = Fraction(round.Points, PointsScale);
		into[at++] = Fraction(round.IncomePaid, PointsScale);
		into[at++] = round.StartingGroundTickets > 0
			? Math.Clamp(round.GroundTickets / (float)round.StartingGroundTickets, 0f, 1f)
			: 0f;
		into[at++] = Fraction(round.LiveUnits, SimConfig.MaxUnits);
		into[at++] = Fraction(round.QueuedUnits, SimConfig.MaxBuildQueue * MaxBarracks);
		into[at++] = Fraction(round.UnitsLost, UnitsLostScale);

		// ---- units ----------------------------------------------------------
		int carried = Math.Min(units.Length, MaxUnits);
		for (int i = 0; i < carried; i++)
		{
			ref readonly AgentUnitView unit = ref units[i];
			int slot = at + (i * UnitFloats);
			into[slot + 0] = unit.Alive ? 1f : 0f;
			if (unit.Tier < Tiers)
			{
				into[slot + 1 + unit.Tier] = 1f;
			}

			into[slot + 4] = Normalize(unit.X, PositionScaleMeters);
			into[slot + 5] = Normalize(unit.Z, PositionScaleMeters);
			into[slot + 6] = Math.Clamp(unit.HealthFraction, 0f, 1f);
			if (unit.Order < OrderKinds)
			{
				into[slot + 7 + unit.Order] = 1f;
			}

			into[slot + 12] = unit.HasTarget ? 1f : 0f;
			into[slot + 13] = Normalize(unit.TargetDistanceMeters, DistanceScaleMeters);
		}
		at += MaxUnits * UnitFloats;

		// ---- barracks --------------------------------------------------------
		carried = Math.Min(barracks.Length, MaxBarracks);
		for (int i = 0; i < carried; i++)
		{
			ref readonly AgentBarracksView door = ref barracks[i];
			int slot = at + (i * BarracksFloats);
			into[slot + 0] = Normalize(door.X, PositionScaleMeters);
			into[slot + 1] = Normalize(door.Z, PositionScaleMeters);
			into[slot + 2] = Fraction(door.QueueDepth, SimConfig.MaxBuildQueue);
			into[slot + 3] = Fraction(door.HeadTier, Math.Max(Tiers - 1, 1));
			into[slot + 4] = Math.Clamp(door.HeadProgress, 0f, 1f);
			into[slot + 5] = Normalize(door.RallyX, PositionScaleMeters);
			into[slot + 6] = Normalize(door.RallyZ, PositionScaleMeters);
		}
		at += MaxBarracks * BarracksFloats;

		// ---- nodes -----------------------------------------------------------
		carried = Math.Min(nodes.Length, MaxNodes);
		for (int i = 0; i < carried; i++)
		{
			ref readonly AgentNodeView node = ref nodes[i];
			int slot = at + (i * NodeFloats);
			into[slot + 0] = Normalize(node.X, PositionScaleMeters);
			into[slot + 1] = Normalize(node.Z, PositionScaleMeters);
			if (node.Owner < NodeOwners)
			{
				into[slot + 2 + node.Owner] = 1f;
			}

			into[slot + 5] = node.Contested ? 1f : 0f;
			into[slot + 6] = Math.Clamp(node.CaptureProgress, 0f, 1f);
			into[slot + 7] = Fraction(node.IncomePaid, PointsScale);
		}
		at += MaxNodes * NodeFloats;

		// ---- contacts ---------------------------------------------------------
		carried = Math.Min(contacts.Length, MaxContacts);
		int written = 0;
		for (int i = 0; i < carried; i++)
		{
			ref readonly AgentStrategistContactView contact = ref contacts[i];
			if (!contact.Known)
			{
				continue;
			}

			int slot = at + (written * ContactFloats);
			into[slot + 0] = Normalize(contact.X, PositionScaleMeters);
			into[slot + 1] = Normalize(contact.Z, PositionScaleMeters);
			into[slot + 2] = contact.Visible ? 1f : 0f;
			into[slot + 3] = Fraction(contact.TicksSinceSeen, GhostAgeScaleTicks);
			into[slot + 4] = contact.IsPlayer ? 1f : 0f;
			into[slot + 5] = contact.Team == (byte)Team.Strategist ? 1f : 0f;
			written++;
		}
		at += MaxContacts * ContactFloats;

		return at;
	}

	private static float Normalize(float value, float scale) => Math.Clamp(value / scale, -1f, 1f);

	private static float Fraction(float value, float scale) =>
		scale > 0f ? Math.Clamp(value / scale, 0f, 1f) : 0f;
}
