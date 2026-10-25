using System;
using Godot;

namespace Gdpyr.Sim.Htn;

/// <summary>One member of a board squad, as its drill sees it on a decision (docs/TACTICS.md §5.5).</summary>
public struct DrillMember
{
	public ushort UnitId;

	public Vector3 Position;

	public UnitDoctrine Doctrine;

	/// <summary>It takes part: alive and on foot, a doctrine of its own, and its micro-autonomy on.</summary>
	public bool Element;

	/// <summary>The last tick it fired a round; 0 for never.</summary>
	public uint LastShotTick;

	/// <summary>A round came near it in its window (§4.1); where it reckons it came from, at the shooter's feet; and when.</summary>
	public bool FiredUpon;

	/// <inheritdoc cref="FiredUpon"/>
	public Vector3 FiredFrom;

	/// <inheritdoc cref="FiredUpon"/>
	public uint FiredTick;

	/// <summary>A grenade in its hand and ready (<see cref="GrenadeSim.AiReady"/>).</summary>
	public bool GrenadeReady;

	/// <summary>The last tick it threw one; 0 for never.</summary>
	public uint LastThrowTick;

	/// <summary>
	/// A cover search with its own body's numbers — where it stands, its radius, what a box
	/// must hide and where it fires from. The drill sets the threat and the search.
	/// </summary>
	public CoverQuery Body;
}

/// <summary>A board squad's order, as its drill reads it.</summary>
public struct DrillOrder
{
	/// <summary>An order a squad drills under: an attack, or a move onto a control point (§5.5).</summary>
	public bool Drills;

	/// <summary>Which order this is: a squad formed or ordered again is a new drill.</summary>
	public uint FormedTick;

	/// <inheritdoc cref="FormedTick"/>
	public uint OrderedTick;

	/// <summary>Where the order goes: the point to take, or the attack's point.</summary>
	public Vector3 Toward;

	/// <summary>A control point to take, its centre and its capture radius.</summary>
	public bool Objective;

	/// <inheritdoc cref="Objective"/>
	public Vector3 Point;

	/// <inheritdoc cref="Objective"/>
	public float Radius;
}

/// <summary>What a squad's drill tells one member (docs/TACTICS.md §5.5), and the spot it keeps between decisions.</summary>
public struct DrillDirective
{
	public ushort UnitId;

	public DrillRole Role;

	public DrillElement Element;

	public DrillTask Task;

	/// <summary>Where its task takes it: a fire position, cover from the threat point, a bound spot.</summary>
	public bool HasSpot;

	/// <inheritdoc cref="HasSpot"/>
	public Vector3 Spot;

	/// <summary>A box beside the spot hides it from the threat point; an open bound point is not.</summary>
	public bool Covered;

	/// <summary>What the spot was found for, and for a bound, which one.</summary>
	public DrillTask SpotTask;

	/// <inheritdoc cref="SpotTask"/>
	public int SpotSerial;
}

/// <summary>One place on a squad's list (docs/TACTICS.md §4.2): where an enemy is, was seen last, or fired from.</summary>
public struct DrillEntry
{
	public Vector3 Feet;

	/// <summary>The contact it is, by owner id; <see cref="Sim.OwnerId.None"/> for a fired-from point.</summary>
	public int OwnerId;

	/// <summary>When the side saw it, or the round came.</summary>
	public uint Tick;

	public bool FiredFrom => OwnerId == Sim.OwnerId.None;
}

/// <summary>
/// Who does what in a squad's drill (docs/TACTICS.md §5.5), as plain functions, as the
/// ground coordinator's roles are: by doctrine, in unit id order. Gunners are the base,
/// marksmen overwatch, riflemen the assault — in halves A and B when there are
/// <see cref="DrillTraits.HalvesFrom"/> or more with a gunner, and always in halves
/// without one, where the first half is the base until the halves start leapfrogging.
/// </summary>
public static class DrillRoles
{
	/// <summary>
	/// Writes each member's role and element into <paramref name="into"/>, beside
	/// <paramref name="members"/>. <paramref name="order"/> is scratch, at least as long as
	/// the members, that ends holding their indices in unit id order.
	/// </summary>
	public static void Assign(ReadOnlySpan<DrillMember> members, Span<DrillDirective> into, Span<int> order,
		int halvesFrom)
	{
		int count = Math.Min(members.Length, Math.Min(into.Length, order.Length));
		for (int i = 0; i < count; i++)
		{
			// Insertion by unit id: a handful of members, and the same answer every time.
			int at = i;
			while (at > 0 && members[order[at - 1]].UnitId > members[i].UnitId)
			{
				order[at] = order[at - 1];
				at--;
			}

			order[at] = i;
		}

		bool gunner = false;
		int riflemen = 0;
		for (int i = 0; i < count; i++)
		{
			if (members[i].Element)
			{
				gunner |= members[i].Doctrine == UnitDoctrine.Gunner;
				riflemen += members[i].Doctrine == UnitDoctrine.Rifleman ? 1 : 0;
			}
		}

		bool halves = !gunner || riflemen >= halvesFrom;
		int firstHalf = halves ? (riflemen + 1) / 2 : riflemen;
		int rank = 0;
		for (int k = 0; k < count; k++)
		{
			int i = order[k];
			into[i].UnitId = members[i].UnitId;
			into[i].Element = DrillElement.None;
			into[i].Role = !members[i].Element ? DrillRole.None : members[i].Doctrine switch
			{
				UnitDoctrine.Gunner => DrillRole.Base,
				UnitDoctrine.Marksman => DrillRole.Overwatch,
				UnitDoctrine.Rifleman => DrillRole.Assault,
				_ => DrillRole.None,
			};

			if (into[i].Role == DrillRole.Assault)
			{
				into[i].Element = rank < firstHalf ? DrillElement.A : DrillElement.B;
				rank++;
			}
		}
	}

	/// <summary>
	/// What a member does in <paramref name="phase"/> (§5.0): the base to its fire positions
	/// whenever the squad is in contact; the assault to ground, then holding in cover, then
	/// bounding by halves — the half not moving holds and fires — then holding for the
	/// grenades and rushing; a marksman to its peeks. Without a gunner the first half is the
	/// base until the bounds begin, and then whichever half is not moving:
	/// <paramref name="riflesBase"/>, <see cref="DrillContext.RiflesBase"/>.
	/// </summary>
	public static DrillTask TaskFor(DrillPhase phase, DrillRole role, DrillElement element, DrillElement moving,
		bool hasGunner, DrillElement riflesBase = DrillElement.A)
	{
		if (role == DrillRole.None || phase is DrillPhase.None or DrillPhase.Approach or DrillPhase.HoldPoint)
		{
			return DrillTask.None;
		}

		if (role == DrillRole.Overwatch)
		{
			return DrillTask.Overwatch;
		}

		if (role == DrillRole.Base)
		{
			return DrillTask.FirePosition;
		}

		bool riflemenBase = !hasGunner && element == riflesBase;
		return phase switch
		{
			DrillPhase.GoToGround => riflemenBase ? DrillTask.FirePosition : DrillTask.Ground,
			DrillPhase.SetBase => riflemenBase ? DrillTask.FirePosition : DrillTask.Hold,
			DrillPhase.Bound => element == moving ? DrillTask.Bound : DrillTask.Hold,
			DrillPhase.Grenades => DrillTask.Hold,
			DrillPhase.Rush => DrillTask.Rush,
			_ => DrillTask.None,
		};
	}
}

/// <summary>
/// What every board squad's drill has decided (docs/TACTICS.md §5.5): its phase, its list
/// and threat point (§4.2), where an assault goes, and what each member is to do and
/// where. Written on each squad's decision; read by its units on their scans. The
/// fired-from points its members reported are kept here, a squad's own, for
/// <see cref="DrillTraits.FiredFromTicks"/>: they never reach the side's memory (rule 4).
///
/// Engine-free and fixed-size: nothing allocated after construction.
/// </summary>
public sealed class DrillBoard
{
	/// <summary>Fired-from points one squad keeps.</summary>
	public const int FiredCapacity = 16;

	/// <summary>Entries on one squad's list: every ground-force player, and the fired-from points.</summary>
	public const int EntryCapacity = SnapshotCodec.MaxPlayers + FiredCapacity;

	private readonly int _squads;
	private readonly int _members;

	private readonly bool[] _active;
	private readonly DrillPhase[] _phase;
	private readonly DrillElement[] _moving;
	private readonly bool[] _hasThreat;
	private readonly Vector3[] _threat;
	private readonly Vector3[] _rush;
	private readonly bool[] _lifted;

	private readonly DrillEntry[] _entries;
	private readonly int[] _entryCount;
	private readonly Vector3[] _suppress;
	private readonly int[] _suppressCount;

	private readonly Vector3[] _fired;
	private readonly uint[] _firedTick;

	private readonly DrillDirective[] _directives;
	private readonly int[] _directiveCount;

	public DrillBoard(int squads = SquadBoard.DefaultSquads, int members = SimConfig.MaxUnits)
	{
		_squads = Math.Clamp(squads, 1, byte.MaxValue);
		_members = Math.Max(members, 1);
		_active = new bool[_squads];
		_phase = new DrillPhase[_squads];
		_moving = new DrillElement[_squads];
		_hasThreat = new bool[_squads];
		_threat = new Vector3[_squads];
		_rush = new Vector3[_squads];
		_lifted = new bool[_squads];
		_entries = new DrillEntry[_squads * EntryCapacity];
		_entryCount = new int[_squads];
		_suppress = new Vector3[_squads * EntryCapacity];
		_suppressCount = new int[_squads];
		_fired = new Vector3[_squads * FiredCapacity];
		_firedTick = new uint[_squads * FiredCapacity];
		_directives = new DrillDirective[_squads * _members];
		_directiveCount = new int[_squads];
	}

	public int Capacity => _squads;

	/// <summary>Members one squad's drill holds directives for.</summary>
	public int MemberCapacity => _members;

	/// <summary>The squad drills: two elements or more under an order it drills under.</summary>
	public bool IsActive(int squad) => InRange(squad) && _active[squad];

	public DrillPhase PhaseOf(int squad) => IsActive(squad) ? _phase[squad] : DrillPhase.None;

	/// <summary>The element bounding, in <see cref="DrillPhase.Bound"/>.</summary>
	public DrillElement MovingOf(int squad) => IsActive(squad) ? _moving[squad] : DrillElement.None;

	/// <summary>The squad's threat point, at the enemy's feet, and whether it has one (it is held through a lull).</summary>
	public bool TryThreat(int squad, out Vector3 feet)
	{
		bool has = IsActive(squad) && _hasThreat[squad];
		feet = has ? _threat[squad] : Vector3.Zero;
		return has;
	}

	/// <summary>Where the assault rushes: the point to take, else the threat point.</summary>
	public Vector3 RushPointOf(int squad) => InRange(squad) ? _rush[squad] : Vector3.Zero;

	/// <summary>The assault is within <see cref="DrillTraits.LiftMeters"/> of the threat point in a rush: the base has lifted its fire.</summary>
	public bool IsLifted(int squad) => IsActive(squad) && _lifted[squad];

	/// <summary>The squad's list (§4.2), as its last decision gathered it.</summary>
	public ReadOnlySpan<DrillEntry> EntriesOf(int squad) =>
		IsActive(squad) ? _entries.AsSpan(squad * EntryCapacity, _entryCount[squad]) : ReadOnlySpan<DrillEntry>.Empty;

	/// <summary>
	/// Where the base's gunners suppress (§5.2, "in a drill"): the threat point first, then
	/// the rest of the list; after the lift, the entries beyond it it shifts to — and none
	/// once there are none, when it goes onto the point with the assault.
	/// </summary>
	public ReadOnlySpan<Vector3> SuppressOf(int squad) =>
		IsActive(squad) ? _suppress.AsSpan(squad * EntryCapacity, _suppressCount[squad]) : ReadOnlySpan<Vector3>.Empty;

	/// <summary>What the drill told <paramref name="unitId"/>, if it is a member of a drilling squad.</summary>
	public bool TryGet(int squad, ushort unitId, out DrillDirective directive)
	{
		directive = default;
		if (!IsActive(squad))
		{
			return false;
		}

		ReadOnlySpan<DrillDirective> all = DirectivesOf(squad);
		for (int i = 0; i < all.Length; i++)
		{
			if (all[i].UnitId == unitId)
			{
				directive = all[i];
				return true;
			}
		}

		return false;
	}

	/// <summary>Every member's directive, as the last decision wrote them.</summary>
	public ReadOnlySpan<DrillDirective> DirectivesOf(int squad) =>
		InRange(squad) ? _directives.AsSpan(squad * _members, _directiveCount[squad]) : ReadOnlySpan<DrillDirective>.Empty;

	internal Span<DrillDirective> DirectiveStore(int squad) => _directives.AsSpan(squad * _members, _members);

	internal void SetDirectiveCount(int squad, int count) => _directiveCount[squad] = Math.Clamp(count, 0, _members);

	internal Span<DrillEntry> EntryStore(int squad) => _entries.AsSpan(squad * EntryCapacity, EntryCapacity);

	internal void SetEntryCount(int squad, int count) => _entryCount[squad] = Math.Clamp(count, 0, EntryCapacity);

	internal Span<Vector3> SuppressStore(int squad) => _suppress.AsSpan(squad * EntryCapacity, EntryCapacity);

	internal void SetSuppressCount(int squad, int count) => _suppressCount[squad] = Math.Clamp(count, 0, EntryCapacity);

	internal void Set(int squad, DrillPhase phase, DrillElement moving, bool hasThreat, Vector3 threat, Vector3 rush,
		bool lifted)
	{
		_active[squad] = true;
		_phase[squad] = phase;
		_moving[squad] = moving;
		_hasThreat[squad] = hasThreat;
		_threat[squad] = threat;
		_rush[squad] = rush;
		_lifted[squad] = lifted;
	}

	/// <summary>
	/// Keeps a member's fired-from point for <see cref="DrillTraits.FiredFromTicks"/>: one
	/// within <see cref="GunnerPlan.SameEnemyMeters"/> of a kept one is that one, newer; a
	/// full table gives up its oldest.
	/// </summary>
	internal void RememberFired(int squad, Vector3 feet, uint tick)
	{
		int start = squad * FiredCapacity;
		int slot = -1;
		int oldest = start;
		for (int i = start; i < start + FiredCapacity; i++)
		{
			if (_firedTick[i] != 0 && Flat(_fired[i], feet) <= GunnerPlan.SameEnemyMeters)
			{
				slot = i;
				break;
			}

			if (_firedTick[i] < _firedTick[oldest])
			{
				oldest = i;
			}
		}

		slot = slot < 0 ? oldest : slot;
		if (tick >= _firedTick[slot])
		{
			_fired[slot] = feet;
			_firedTick[slot] = Math.Max(tick, 1u);
		}
	}

	/// <summary>The squad's fired-from points no older than <paramref name="maxAge"/>, into <paramref name="into"/>, oldest slot first.</summary>
	internal int Fired(int squad, uint tick, int maxAge, Span<DrillEntry> into)
	{
		int count = 0;
		int start = squad * FiredCapacity;
		for (int i = start; i < start + FiredCapacity && count < into.Length; i++)
		{
			if (_firedTick[i] != 0 && tick >= _firedTick[i] && tick - _firedTick[i] <= (uint)maxAge)
			{
				into[count++] = new DrillEntry { Feet = _fired[i], OwnerId = OwnerId.None, Tick = _firedTick[i] };
			}
		}

		return count;
	}

	/// <summary>The squad does not drill: no directives, no list. Its fired-from points are kept unless <paramref name="forget"/>.</summary>
	public void Stop(int squad, bool forget)
	{
		if (!InRange(squad))
		{
			return;
		}

		_active[squad] = false;
		_phase[squad] = DrillPhase.None;
		_moving[squad] = DrillElement.None;
		_hasThreat[squad] = false;
		_lifted[squad] = false;
		_entryCount[squad] = 0;
		_suppressCount[squad] = 0;
		_directiveCount[squad] = 0;
		if (forget)
		{
			Array.Clear(_firedTick, squad * FiredCapacity, FiredCapacity);
		}
	}

	/// <summary>Every squad stops and forgets, between rounds.</summary>
	public void Clear()
	{
		for (int s = 0; s < _squads; s++)
		{
			Stop(s, forget: true);
		}
	}

	private bool InRange(int squad) => squad >= 0 && squad < _squads;

	private static float Flat(Vector3 a, Vector3 b)
	{
		float x = a.X - b.X;
		float z = a.Z - b.Z;
		return MathF.Sqrt((x * x) + (z * z));
	}
}

/// <summary>
/// The squad drill (docs/TACTICS.md §5.5): for each board squad with two elements or more
/// under an attack or a point to take, on its decision, the squad's list and threat point
/// (§4.2), its facts, a tick of its planner over <see cref="DrillDomain"/>, and from the
/// phase that leaves, what each member does and where — fire positions, cover, bound spots
/// — on the <see cref="DrillBoard"/>. The units carry their parts out with their own
/// domain (§5.0.1).
///
/// Engine-free, deterministic (unit id order, ties to the earlier) and allocation-free
/// after construction: what the engine hands it is its members, the side's memory and the
/// cover board, and every spot is a cover-board search (rule 6: no rays).
/// </summary>
public sealed class SquadDrill
{
	/// <summary>Spots a fire position is chosen among: the best few, for one off the assault's axis.</summary>
	private const int FireChoices = 4;

	private readonly DrillPlanning _planning;
	private readonly DrillContext[] _contexts;
	private readonly bool[] _drilling;
	private readonly uint[] _formedTick;
	private readonly uint[] _orderedTick;

	private readonly DrillDirective[] _previous;
	private readonly int[] _order;
	private readonly DrillEntry[] _fired = new DrillEntry[DrillBoard.FiredCapacity];
	private readonly CoverSpot[] _choices = new CoverSpot[FireChoices];
	private readonly Vector3[] _claims;

	public SquadDrill(DrillPlanning planning, int squads = SquadBoard.DefaultSquads, int members = SimConfig.MaxUnits)
	{
		_planning = planning ?? throw new ArgumentNullException(nameof(planning));
		Board = new DrillBoard(squads, members);
		_contexts = new DrillContext[Board.Capacity];
		_drilling = new bool[Board.Capacity];
		_formedTick = new uint[Board.Capacity];
		_orderedTick = new uint[Board.Capacity];
		for (int s = 0; s < _contexts.Length; s++)
		{
			_contexts[s] = planning.CreateContext();
		}

		_previous = new DrillDirective[Board.MemberCapacity];
		_order = new int[Board.MemberCapacity];
		_claims = new Vector3[ClaimCapacity + Board.MemberCapacity];
	}

	/// <summary>The most spots the side can have taken that a search is asked to keep clear of: a cover spot, a hide spot and a drill spot a unit.</summary>
	public const int ClaimCapacity = SimConfig.MaxUnits * 3;

	public DrillBoard Board { get; }

	public DrillTraits Traits => _planning.Traits;

	public DrillPlanning Planning => _planning;

	/// <summary>A squad's drill context, for the debugger and the tests.</summary>
	public DrillContext ContextOf(int squad) => squad >= 0 && squad < _contexts.Length ? _contexts[squad] : null;

	/// <summary>Whether <paramref name="squad"/> decides on <paramref name="tick"/>: every interval, staggered by slot.</summary>
	public bool IsDue(int squad, uint tick) => tick % (uint)Traits.IntervalTicks == (uint)squad % (uint)Traits.IntervalTicks;

	/// <summary>The squad is not on the board, or under an order it does not drill under: its drill ends.</summary>
	public void Stop(int squad)
	{
		if (squad < 0 || squad >= _contexts.Length)
		{
			return;
		}

		if (_drilling[squad])
		{
			_planning.Reset(_contexts[squad]);
			_contexts[squad].Clear();
			_drilling[squad] = false;
		}

		Board.Stop(squad, forget: true);
	}

	/// <summary>Every squad stops, between rounds.</summary>
	public void Clear()
	{
		for (int s = 0; s < _contexts.Length; s++)
		{
			Stop(s);
		}
	}

	/// <summary>
	/// One decision for <paramref name="squad"/>. <paramref name="claimed"/> is every spot
	/// other units on the side have taken; <paramref name="alive"/> says whether the player an
	/// owner id names is alive (null for all). Returns the phase the drill is in, or
	/// <see cref="DrillPhase.None"/> for a squad that does not drill.
	/// </summary>
	public DrillPhase Decide(int squad, uint tick, in DrillOrder order, ReadOnlySpan<DrillMember> members,
		ContactMemory memory, Func<int, bool> alive, CoverBoard cover, ReadOnlySpan<Vector3> claimed)
	{
		if (squad < 0 || squad >= _contexts.Length)
		{
			return DrillPhase.None;
		}

		DrillTraits traits = Traits;
		members = members[..Math.Min(members.Length, Board.MemberCapacity)];
		int elements = 0;
		for (int i = 0; i < members.Length; i++)
		{
			elements += members[i].Element && members[i].Doctrine != UnitDoctrine.None ? 1 : 0;
		}

		if (!order.Drills || elements < 2)
		{
			Stop(squad);
			return DrillPhase.None;
		}

		DrillContext context = _contexts[squad];
		if (!_drilling[squad] || order.FormedTick != _formedTick[squad] || order.OrderedTick != _orderedTick[squad])
		{
			// A new order is a new drill (§5.5, "pre-emption").
			Stop(squad);
			_drilling[squad] = true;
			_formedTick[squad] = order.FormedTick;
			_orderedTick[squad] = order.OrderedTick;
		}

		// The last decision's directives, by unit: the spots carry over, and what each did is
		// what the base's fire and the bound's arrival are judged on.
		ReadOnlySpan<DrillDirective> last = Board.DirectivesOf(squad);
		int previous = last.Length;
		last.CopyTo(_previous);
		DrillPhase lastPhase = Board.PhaseOf(squad);
		DrillElement lastMoving = Board.MovingOf(squad);

		Span<DrillDirective> directives = Board.DirectiveStore(squad)[..members.Length];
		DrillRoles.Assign(members, directives, _order, traits.HalvesFrom);
		for (int i = 0; i < members.Length; i++)
		{
			int was = FindPrevious(previous, members[i].UnitId);
			DrillDirective carried = was >= 0 ? _previous[was] : default;
			directives[i].Task = carried.Task;
			directives[i].HasSpot = carried.HasSpot;
			directives[i].Spot = carried.Spot;
			directives[i].Covered = carried.Covered;
			directives[i].SpotTask = carried.SpotTask;
			directives[i].SpotSerial = carried.SpotSerial;
			if (members[i].Element && members[i].FiredUpon)
			{
				Board.RememberFired(squad, members[i].FiredFrom, members[i].FiredTick);
			}
		}

		Vector3 centroid = Centroid(members, directives, DrillRole.None);
		int entries = GatherEntries(squad, tick, memory, alive, centroid, order.Toward);
		bool hasThreat = ChooseThreat(squad, entries, members, directives, centroid, order.Toward, out Vector3 threat);
		if (!hasThreat && Board.TryThreat(squad, out Vector3 held))
		{
			// Held through the lull, for as long as the drill is still in contact.
			threat = held;
		}

		bool keepsThreat = hasThreat || Board.TryThreat(squad, out _);
		var sense = new DrillSense
		{
			HasThreat = hasThreat,
			Objective = !order.Objective ? DrillObjective.None
				: OnPoint(members, directives, order) && !Contested(squad, entries, order) ? DrillObjective.On
				: DrillObjective.Approach,
		};

		float lead = float.MaxValue;
		float leadAny = float.MaxValue;
		bool arrivedSome = false;
		bool arrived = true;
		for (int i = 0; i < members.Length; i++)
		{
			DrillDirective d = directives[i];
			if (d.Role == DrillRole.None)
			{
				continue;
			}

			DrillMember m = members[i];
			sense.FiredUpon |= m.FiredUpon;
			sense.HasGunner |= d.Role == DrillRole.Base;
			sense.HasElementB |= d.Element == DrillElement.B;
			float toThreat = Flat(m.Position, threat);
			leadAny = MathF.Min(leadAny, toThreat);
			if (d.Role == DrillRole.Assault)
			{
				lead = MathF.Min(lead, toThreat);
				sense.Grenade |= m.GrenadeReady && toThreat >= GrenadeSim.MinThrowMeters
					&& toThreat <= GrenadeSim.MaxThrowMeters;
				sense.LastAssaultThrowTick = Math.Max(sense.LastAssaultThrowTick, m.LastThrowTick);
			}

			// The base: whoever was at a fire position, and in a bound whoever held.
			if (d.Task == DrillTask.FirePosition || (d.Task == DrillTask.Hold && lastPhase == DrillPhase.Bound))
			{
				sense.LastBaseFireTick = Math.Max(sense.LastBaseFireTick, m.LastShotTick);
			}

			// The bound: every member of the element moving in its spot.
			if (lastPhase == DrillPhase.Bound && d.Element == lastMoving && d.Task == DrillTask.Bound)
			{
				arrivedSome = true;
				arrived &= !d.HasSpot || Flat(m.Position, d.Spot) <= traits.ArriveMeters;
			}
		}

		sense.LeadMeters = lead < float.MaxValue ? lead : leadAny;
		sense.Arrived = arrivedSome && arrived;

		context.Tick = tick;
		context.Encode(sense);
		_planning.Tick(context);

		DrillPhase phase = context.Intent.Phase == DrillPhase.None ? DrillPhase.Approach : context.Intent.Phase;
		DrillElement moving = phase == DrillPhase.Bound ? context.Intent.Moving : DrillElement.None;
		bool inContact = context.Get(DrillFact.Contact) != (byte)DrillContact.None && keepsThreat;

		Vector3 assault = Centroid(members, directives, DrillRole.Assault);
		bool lifted = phase == DrillPhase.Rush && inContact && AnyWithin(members, directives, DrillRole.Assault, threat,
			traits.LiftMeters);
		int suppress = inContact ? Suppress(squad, entries, threat, assault, lifted, members, directives) : 0;

		AssignTasks(members, directives, phase, moving, context.Is(DrillFact.HasGunner), context.RiflesBase,
			lifted && suppress == 0);
		if (inContact)
		{
			AssignSpots(members, directives, threat, assault, centroid, order.Toward, context.Intent.BoundSerial, cover,
				claimed);
		}
		else
		{
			for (int i = 0; i < directives.Length; i++)
			{
				directives[i].HasSpot = false;
			}
		}

		Board.SetDirectiveCount(squad, members.Length);
		Board.Set(squad, phase, moving, inContact, threat, order.Objective ? order.Point : threat, lifted);
		return phase;
	}

	private int FindPrevious(int count, ushort unitId)
	{
		for (int i = 0; i < count; i++)
		{
			if (_previous[i].UnitId == unitId)
			{
				return i;
			}
		}

		return -1;
	}

	/// <summary>
	/// The squad's list (§4.2): every contact the side remembers, at its live position or
	/// where it was last seen, and has not died since; then the squad's fired-from points,
	/// one within <see cref="GunnerPlan.SameEnemyMeters"/> of a contact being that contact.
	/// Only those within <see cref="DrillTraits.ThreatMeters"/> of the squad, or of the line
	/// from it to where its order goes. Returns how many.
	/// </summary>
	private int GatherEntries(int squad, uint tick, ContactMemory memory, Func<int, bool> alive, Vector3 centroid,
		Vector3 toward)
	{
		DrillTraits traits = Traits;
		Span<DrillEntry> store = Board.EntryStore(squad);
		float reach = traits.ThreatMeters * traits.ThreatMeters;
		int count = 0;
		for (int i = 0; memory != null && i < memory.Count && count < SnapshotCodec.MaxPlayers; i++)
		{
			KnownContact known = memory.At(i);
			if (!memory.IsRemembered(i, tick) || (alive != null && !alive(known.OwnerId))
				|| OffLineSquared(known.Position, centroid, toward) > reach)
			{
				continue;
			}

			store[count++] = new DrillEntry { Feet = known.Position, OwnerId = known.OwnerId, Tick = known.LastSeenTick };
		}

		int contacts = count;
		int fired = Board.Fired(squad, tick, traits.FiredFromTicks, _fired);
		for (int f = 0; f < fired && count < store.Length; f++)
		{
			DrillEntry entry = _fired[f];
			bool same = false;
			for (int c = 0; c < contacts && !same; c++)
			{
				same = Flat(store[c].Feet, entry.Feet) <= GunnerPlan.SameEnemyMeters;
			}

			if (!same && OffLineSquared(entry.Feet, centroid, toward) <= reach)
			{
				store[count++] = entry;
			}
		}

		Board.SetEntryCount(squad, count);
		return count;
	}

	/// <summary>
	/// The threat point (§4.2): the entry with the most members fired upon from within
	/// <see cref="DrillTraits.FiredNearMeters"/> of it, then the nearest the line to the
	/// objective, then the lowest owner id — a contact before a fired-from point — then the
	/// earlier.
	/// </summary>
	private bool ChooseThreat(int squad, int entries, ReadOnlySpan<DrillMember> members,
		ReadOnlySpan<DrillDirective> directives, Vector3 centroid, Vector3 toward, out Vector3 threat)
	{
		threat = Vector3.Zero;
		ReadOnlySpan<DrillEntry> list = Board.EntryStore(squad)[..entries];
		int best = -1;
		int bestFired = -1;
		float bestLine = float.MaxValue;
		int bestOwner = int.MaxValue;
		float near = Traits.FiredNearMeters;
		for (int e = 0; e < list.Length; e++)
		{
			int fired = 0;
			for (int i = 0; i < members.Length; i++)
			{
				fired += directives[i].Role != DrillRole.None && members[i].FiredUpon
					&& Flat(members[i].FiredFrom, list[e].Feet) <= near ? 1 : 0;
			}

			float line = OffLineSquared(list[e].Feet, centroid, toward);
			int owner = list[e].FiredFrom ? int.MaxValue : list[e].OwnerId;
			if (fired > bestFired || (fired == bestFired && (line < bestLine || (line == bestLine && owner < bestOwner))))
			{
				best = e;
				bestFired = fired;
				bestLine = line;
				bestOwner = owner;
			}
		}

		if (best < 0)
		{
			return false;
		}

		threat = list[best].Feet;
		return true;
	}

	/// <summary>
	/// Where the base's gunners suppress (§5.2): the threat point, then the rest of the list,
	/// nearest the threat point first. Lifted — the assault within
	/// <see cref="DrillTraits.LiftMeters"/> of the threat point in its rush — only the entries
	/// within <see cref="DrillTraits.ShiftMeters"/> beyond it, none near the assault. Returns how many.
	/// </summary>
	private int Suppress(int squad, int entries, Vector3 threat, Vector3 assault, bool lifted,
		ReadOnlySpan<DrillMember> members, ReadOnlySpan<DrillDirective> directives)
	{
		DrillTraits traits = Traits;
		ReadOnlySpan<DrillEntry> list = Board.EntryStore(squad)[..entries];
		Span<Vector3> store = Board.SuppressStore(squad);
		int count = 0;
		if (!lifted)
		{
			store[count++] = threat;
		}

		float beyond = Flat(assault, threat);
		for (int pass = 0; pass < list.Length && count < store.Length; pass++)
		{
			// Nearest the threat point first: the next not yet taken.
			int pick = -1;
			float pickMeters = float.MaxValue;
			for (int e = 0; e < list.Length; e++)
			{
				Vector3 at = list[e].Feet;
				float meters = Flat(at, threat);
				if (meters >= pickMeters || meters <= GunnerPlan.SameEnemyMeters || Taken(store[..count], at))
				{
					continue;
				}

				if (lifted && (meters > traits.ShiftMeters || Flat(at, assault) <= beyond
					|| AnyWithin(members, directives, DrillRole.Assault, at, traits.LiftMeters)))
				{
					continue;
				}

				pick = e;
				pickMeters = meters;
			}

			if (pick < 0)
			{
				break;
			}

			store[count++] = list[pick].Feet;
		}

		Board.SetSuppressCount(squad, count);
		return count;
	}

	private static bool Taken(ReadOnlySpan<Vector3> taken, Vector3 at)
	{
		for (int i = 0; i < taken.Length; i++)
		{
			if (taken[i] == at)
			{
				return true;
			}
		}

		return false;
	}

	/// <summary>
	/// Each member's task for the phase (<see cref="DrillRoles.TaskFor"/>). A base whose fire
	/// has lifted with nothing beyond to shift to goes onto the point with the assault (§5.2).
	/// </summary>
	private static void AssignTasks(ReadOnlySpan<DrillMember> members, Span<DrillDirective> directives,
		DrillPhase phase, DrillElement moving, bool hasGunner, DrillElement riflesBase, bool baseFollows)
	{
		for (int i = 0; i < members.Length; i++)
		{
			DrillTask task = DrillRoles.TaskFor(phase, directives[i].Role, directives[i].Element, moving, hasGunner,
				riflesBase);
			if (task == DrillTask.FirePosition && baseFollows && directives[i].Role == DrillRole.Base)
			{
				task = DrillTask.Rush;
			}

			directives[i].Task = task;
		}
	}

	/// <summary>
	/// Where each member's task takes it (§5.5). A fire position is kept while it is still in
	/// the band and its box still hides it from the threat point, and found otherwise. Cover
	/// from the threat point likewise. A bound spot is found once per bound, from where the
	/// member stands then, and kept for it. Holding keeps whatever spot it has. A rush, an
	/// overwatch and nothing at all have none. Every spot is claimed against the rest of the
	/// squad and against <paramref name="claimed"/>.
	/// </summary>
	private void AssignSpots(ReadOnlySpan<DrillMember> members, Span<DrillDirective> directives, Vector3 threat,
		Vector3 assault, Vector3 centroid, Vector3 toward, int serial, CoverBoard cover, ReadOnlySpan<Vector3> claimed)
	{
		DrillTraits traits = Traits;
		int movers = 0;
		for (int i = 0; i < members.Length; i++)
		{
			DrillTask task = directives[i].Task;
			if (task is DrillTask.None or DrillTask.Rush or DrillTask.Overwatch)
			{
				directives[i].HasSpot = false;
			}

			movers += task == DrillTask.Bound ? 1 : 0;
		}

		Vector3 axis = Axis(centroid, toward, threat);
		for (int k = 0; k < members.Length; k++)
		{
			int i = _order[k];
			ref DrillDirective d = ref directives[i];
			DrillMember m = members[i];
			if (d.Task is DrillTask.None or DrillTask.Rush or DrillTask.Overwatch || cover == null)
			{
				continue;
			}

			ReadOnlySpan<Vector3> claims = Claims(claimed, directives, i);
			CoverQuery query = m.Body;
			query.From = m.Position;
			query.Threat = threat;
			query.ThreatEyeMeters = ThreatEyeMeters;
			query.ThreatAimMeters = ThreatChestMeters;
			query.HasToward = false;
			query.MinProgressMeters = 0f;
			query.StaySameSide = false;

			switch (d.Task)
			{
				case DrillTask.FirePosition:
					query.ThreatAimMeters = GunnerPlan.SuppressAimMeters;
					query.SearchMeters = traits.FireSearchMeters;
					query.MustFire = true;
					query.MinThreatMeters = traits.FireMinMeters;
					query.MaxThreatMeters = traits.FireMaxMeters;
					if (!(d.HasSpot && d.SpotTask == DrillTask.FirePosition && KeepsFire(d.Spot, query, cover)))
					{
						int found = cover.FindSpots(query, claims, _choices);
						int pick = found > 0 ? 0 : -1;
						for (int c = 0; c < found; c++)
						{
							if (OffAxis(_choices[c].Position, threat, assault) >= traits.FireOffAxisRadians)
							{
								pick = c;
								break;
							}
						}

						if (pick >= 0)
						{
							Place(ref d, true, _choices[pick].Position, true, DrillTask.FirePosition, serial);
						}
						else if (Flat(m.Position, threat) > traits.FireMaxMeters)
						{
							// No wall in the band within reach, and out of it: in the open, to inside its far edge.
							Vector3 back = m.Position - threat;
							back.Y = 0f;
							Vector3 at = threat + (back.Normalized() * (traits.FireMaxMeters - FireOpenInsideMeters));
							at.Y = m.Position.Y;
							Place(ref d, true, at, false, DrillTask.FirePosition, serial);
						}
						else
						{
							d.HasSpot = false;
						}
					}

					break;

				case DrillTask.Ground:
					query.SearchMeters = UnitCoverSearchMeters;
					query.MustFire = true;
					query.MinThreatMeters = 0f;
					query.MaxThreatMeters = 0f;
					query.StaySameSide = true;
					query.HasToward = true;
					query.Toward = toward;
					query.MaxBackMeters = GroundBackMeters;
					query.MaxOffLineMeters = GroundOffLineMeters;
					if (!(d.HasSpot && d.SpotTask == DrillTask.Ground && cover.ProtectingBox(d.Spot, query) >= 0))
					{
						bool found = cover.TryFindSpot(query, claims, out CoverSpot spot);
						if (!found)
						{
							query.MustFire = false;
							query.SearchMeters = GroundHideMeters;
							found = cover.TryFindSpot(query, claims, out spot);
						}

						// None in reach: whatever spot it has, while that hides it.
						if (found)
						{
							Place(ref d, true, spot.Position, true, DrillTask.Ground, serial);
						}
						else if (!(d.HasSpot && cover.ProtectingBox(d.Spot, query) >= 0))
						{
							d.HasSpot = false;
						}
					}

					break;

				case DrillTask.Bound:
					if (!(d.SpotTask == DrillTask.Bound && d.SpotSerial == serial))
					{
						query.SearchMeters = traits.BoundSearchMeters;
						query.MustFire = true;
						query.MinThreatMeters = traits.BoundStandoffMeters;
						query.MaxThreatMeters = 0f;
						query.StaySameSide = true;
						query.HasToward = true;
						query.Toward = toward;
						query.MaxBackMeters = 0f;
						query.MaxOffLineMeters = traits.BoundOffLineMeters;
						query.MinProgressMeters = traits.BoundProgressMeters;
						bool found = cover.TryFindSpot(query, claims, out CoverSpot spot);
						if (!found)
						{
							query.MustFire = false;
							found = cover.TryFindSpot(query, claims, out spot);
						}

						Vector3 at = found ? spot.Position
							: OpenBound(m.Position, axis, toward, threat, AcrossRank(members, directives, i, axis), movers);
						Place(ref d, true, at, found, DrillTask.Bound, serial);
					}

					break;

				case DrillTask.Hold:
					if (d.HasSpot)
					{
						d.Covered = cover.ProtectingBox(d.Spot, query) >= 0;
					}

					break;
			}
		}
	}

	/// <summary>The engine's claims, and every other member's spot as this decision has it.</summary>
	private ReadOnlySpan<Vector3> Claims(ReadOnlySpan<Vector3> claimed, ReadOnlySpan<DrillDirective> directives,
		int self)
	{
		int count = Math.Min(claimed.Length, ClaimCapacity);
		claimed[..count].CopyTo(_claims);
		for (int i = 0; i < directives.Length && count < _claims.Length; i++)
		{
			if (i != self && directives[i].HasSpot)
			{
				_claims[count++] = directives[i].Spot;
			}
		}

		return _claims.AsSpan(0, count);
	}

	private static void Place(ref DrillDirective d, bool has, Vector3 spot, bool covered, DrillTask task, int serial)
	{
		d.HasSpot = has;
		d.Spot = spot;
		d.Covered = has && covered;
		d.SpotTask = task;
		d.SpotSerial = serial;
	}

	/// <summary>A fire position still in the band, behind a box low enough to fire over that still hides it from the threat point.</summary>
	private static bool KeepsFire(Vector3 spot, in CoverQuery query, CoverBoard cover)
	{
		float range = Flat(spot, query.Threat);
		int box = cover.ProtectingBox(spot, query);
		return box >= 0 && range >= query.MinThreatMeters && range <= query.MaxThreatMeters
			&& cover.HeightOf(box) <= query.EyeMeters - CoverBoard.FireClearanceMeters;
	}

	/// <summary>
	/// With no spot for a bound: <see cref="DrillTraits.OpenBoundMeters"/> on along the axis,
	/// short of where the order goes, offset across it by its rank across the axis
	/// (<see cref="AcrossRank"/>), and never nearer the threat point than
	/// <see cref="DrillTraits.BoundStandoffMeters"/>: the step halved twice, then where it stands.
	/// </summary>
	private Vector3 OpenBound(Vector3 from, Vector3 axis, Vector3 toward, Vector3 threat, int rank, int movers)
	{
		DrillTraits traits = Traits;
		float meters = MathF.Min(traits.OpenBoundMeters, MathF.Max(Flat(from, toward) - 1f, 0f));
		var across = new Vector3(-axis.Z, 0f, axis.X);
		float offset = (rank - ((Math.Max(movers, 1) - 1) * 0.5f)) * traits.OpenRankMeters;
		for (int halve = 0; halve < 3 && meters > 0.5f; halve++)
		{
			Vector3 at = from + (axis * meters) + (across * offset);
			if (Flat(at, threat) >= traits.BoundStandoffMeters)
			{
				return at;
			}

			meters *= 0.5f;
		}

		return from;
	}

	/// <summary>
	/// A bounding member's place in its element across the axis, from the left: how many of
	/// the members bounding stand further left, ties to the lower unit id. Open points are
	/// spread by it, so neighbours stay neighbours and no two paths cross.
	/// </summary>
	private static int AcrossRank(ReadOnlySpan<DrillMember> members, ReadOnlySpan<DrillDirective> directives, int self,
		Vector3 axis)
	{
		var across = new Vector3(-axis.Z, 0f, axis.X);
		float mine = members[self].Position.Dot(across);
		int rank = 0;
		for (int i = 0; i < members.Length; i++)
		{
			if (i == self || directives[i].Task != DrillTask.Bound)
			{
				continue;
			}

			float theirs = members[i].Position.Dot(across);
			rank += theirs < mine || (theirs == mine && members[i].UnitId < members[self].UnitId) ? 1 : 0;
		}

		return rank;
	}

	/// <summary>The direction the squad advances in, flat: to where its order goes, else to the threat point.</summary>
	private static Vector3 Axis(Vector3 from, Vector3 toward, Vector3 threat)
	{
		Vector3 d = toward - from;
		d.Y = 0f;
		if (d.LengthSquared() < 1f)
		{
			d = threat - from;
			d.Y = 0f;
		}

		return d.LengthSquared() > 1e-6f ? d.Normalized() : Vector3.Forward;
	}

	/// <summary>The angle at the threat point between a spot and the assault: how far off the assault's axis it fires from.</summary>
	private static float OffAxis(Vector3 spot, Vector3 threat, Vector3 assault)
	{
		float ax = spot.X - threat.X;
		float az = spot.Z - threat.Z;
		float bx = assault.X - threat.X;
		float bz = assault.Z - threat.Z;
		float a = MathF.Sqrt((ax * ax) + (az * az));
		float b = MathF.Sqrt((bx * bx) + (bz * bz));
		if (a < 1e-3f || b < 1e-3f)
		{
			return 0f;
		}

		return MathF.Acos(Math.Clamp(((ax * bx) + (az * bz)) / (a * b), -1f, 1f));
	}

	/// <summary>Where the members of <paramref name="role"/> stand on average — every element's for <see cref="DrillRole.None"/>.</summary>
	private static Vector3 Centroid(ReadOnlySpan<DrillMember> members, ReadOnlySpan<DrillDirective> directives,
		DrillRole role)
	{
		Vector3 sum = Vector3.Zero;
		int count = 0;
		for (int i = 0; i < members.Length; i++)
		{
			if (directives[i].Role != DrillRole.None && (role == DrillRole.None || directives[i].Role == role))
			{
				sum += members[i].Position;
				count++;
			}
		}

		return count > 0 ? sum / count : role == DrillRole.None ? Vector3.Zero : Centroid(members, directives, DrillRole.None);
	}

	private static bool AnyWithin(ReadOnlySpan<DrillMember> members, ReadOnlySpan<DrillDirective> directives,
		DrillRole role, Vector3 at, float meters)
	{
		for (int i = 0; i < members.Length; i++)
		{
			if (directives[i].Role == role && Flat(members[i].Position, at) <= meters)
			{
				return true;
			}
		}

		return false;
	}

	/// <summary>
	/// An entry of the squad's list on the point: an enemy is there, was last seen there, or
	/// fired from there. A squad with an element on a point like that has not taken it yet.
	/// </summary>
	private bool Contested(int squad, int entries, in DrillOrder order)
	{
		ReadOnlySpan<DrillEntry> list = Board.EntryStore(squad)[..entries];
		for (int e = 0; e < list.Length; e++)
		{
			if (CapturePad.Covers(order.Point, order.Radius, list[e].Feet))
			{
				return true;
			}
		}

		return false;
	}

	/// <summary>An element on the point, by the rule the economy counts bodies with.</summary>
	private static bool OnPoint(ReadOnlySpan<DrillMember> members, ReadOnlySpan<DrillDirective> directives,
		in DrillOrder order)
	{
		for (int i = 0; i < members.Length; i++)
		{
			if (directives[i].Role != DrillRole.None && CapturePad.Covers(order.Point, order.Radius, members[i].Position))
			{
				return true;
			}
		}

		return false;
	}

	/// <summary>The squared flat distance from <paramref name="point"/> to the segment from <paramref name="a"/> to <paramref name="b"/>.</summary>
	private static float OffLineSquared(Vector3 point, Vector3 a, Vector3 b)
	{
		float dx = b.X - a.X;
		float dz = b.Z - a.Z;
		float length = (dx * dx) + (dz * dz);
		float s = length > 1e-8f ? Math.Clamp((((point.X - a.X) * dx) + ((point.Z - a.Z) * dz)) / length, 0f, 1f) : 0f;
		float ox = point.X - (a.X + (dx * s));
		float oz = point.Z - (a.Z + (dz * s));
		return (ox * ox) + (oz * oz);
	}

	private static float Flat(Vector3 a, Vector3 b)
	{
		float x = a.X - b.X;
		float z = a.Z - b.Z;
		return MathF.Sqrt((x * x) + (z * z));
	}

	/// <summary>How far inside the far edge of the band a base with no wall goes, in the open.</summary>
	public const float FireOpenInsideMeters = 3f;

	/// <summary>A standing ground player's eye above their feet, as the units' cover searches take it.</summary>
	public const float ThreatEyeMeters = 1.5f;

	/// <summary>Where a standing ground player is aimed at, above their feet.</summary>
	public const float ThreatChestMeters = 1.0f;

	/// <summary>How far a member goes to ground for a low wall to fire over, as a unit fired upon does (§5.1).</summary>
	public const float UnitCoverSearchMeters = 10f;

	/// <summary>…and for anything that hides it, with no such wall.</summary>
	public const float GroundHideMeters = 6f;

	/// <summary>How much further from where the order goes cover from the threat point may be.</summary>
	public const float GroundBackMeters = 3f;

	/// <summary>How far off the line to where the order goes it may be.</summary>
	public const float GroundOffLineMeters = 6f;
}
