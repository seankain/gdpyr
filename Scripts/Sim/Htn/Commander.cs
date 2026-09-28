using System;
using FluidHTN;
using FluidHTN.Factory;
using Godot;

namespace Gdpyr.Sim.Htn;

/// <summary>One of the side's fighting units, as the commander counts it. Builders are not passed: they are Fortify's.</summary>
public struct CommandUnit
{
	public ushort Id;

	public Vector3 Position;

	/// <summary><see cref="ForceRatio.Strength"/>: cost times health fraction.</summary>
	public float Strength;

	/// <summary>A tank: it goes only with an infantry squad (§5.3, "escort armour").</summary>
	public bool Armour;

	/// <summary>A technical, the scout of choice: a 70 m sensor at 8 m/s.</summary>
	public bool Scout;

	/// <summary>A supply truck (D2, H5): never in a fighting squad; the commander stations it where its squads will want it.</summary>
	public bool Supply;

	/// <summary>What <see cref="Strength"/> is at full health: its cost. What resupply can bring it back to.</summary>
	public float FullStrength;

	public bool Engaging;

	/// <summary>Somebody else's to order (D1): a person or a policy's seat in the last 30 s, or another computer strategist.</summary>
	public bool Claimed;
}

/// <summary>What the commander is told about the map besides the boards.</summary>
public struct CommanderWorld
{
	public uint Tick;

	/// <summary>The first barracks' rally point: what the garrison defends and where a depleted squad refills.</summary>
	public Vector3 Home;

	/// <summary>Where the ground force comes from: the centroid of its spawns.</summary>
	public Vector3 Threat;
}

/// <summary>What a commander squad is for, apart from the task it has now.</summary>
public enum CommandRole : byte
{
	None = 0,

	/// <summary>The standing Defend Zone squad at home: the first <see cref="CommanderTraits.GarrisonUnits"/> units.</summary>
	Garrison = 1,

	/// <summary>One unit, a technical if there is one, on recon.</summary>
	Scout = 2,

	/// <summary>Everything else: formed from production, sent where the assignment says.</summary>
	Assault = 3,

	/// <summary>The supply trucks (D2, H5), stationed where the squads will want resupply.</summary>
	Supply = 4,
}

/// <summary>What an attack is aimed at.</summary>
public enum CommandTarget : byte
{
	None = 0,

	/// <summary>A live contact, named in the order (D4: never a ghost).</summary>
	Contact = 1,

	/// <summary>A node the side does not hold: retaking a denied one, or taking a neutral one.</summary>
	Zone = 2,

	/// <summary>A ground spawn, one at a time, when nothing else is known: legacy's sweep.</summary>
	Sweep = 3,
}

/// <summary>One of the commander's squads, as the last decision left it.</summary>
public struct CommandSquad
{
	public bool Active;

	public CommandRole Role;

	/// <summary>An assault squad that has reached <see cref="CommanderTraits.AssaultSquadSize"/> once, and may be given a target.</summary>
	public bool Ready;

	public int Members;

	public int Armour;

	public int Engaging;

	public float Strength;

	/// <summary>What its units would be worth at full health: what resupply can bring it back to (D2).</summary>
	public float FullStrength;

	/// <summary>What its units were worth when they joined: Retreat and Resupply measure what is left against it.</summary>
	public float StrengthAtFormation;

	public Vector3 Centroid;

	public CommandTask Task;

	public CommandTarget Target;

	/// <summary>The board zone the task is at: the zone defended, the node attacked, the spawn swept; -1 for none.</summary>
	public int Zone;

	/// <summary>The contact an attack names, or <see cref="OwnerId.None"/>.</summary>
	public int TargetOwnerId;

	public Vector3 TaskPoint;

	public Vector3 StagingPoint;

	/// <summary>The tick the task was given.</summary>
	public uint TaskTick;

	/// <summary>An idle assault squad too weak for every known target: production goes to it first (§5.3, Economy).</summary>
	public bool Short;

	public bool Depleted;

	/// <summary>Healing its units would bring it back above its resupply share: it heals rather than being merged (D2).</summary>
	public bool Healable;

	public bool FallingBack;

	public Vector3 FallbackPoint;

	public uint FormedTick;
}

/// <summary>
/// What every commander plans with: one squad domain, one planner and one pooled
/// factory, shared by every squad of every computer strategist (§3.4, P6 — safe
/// because the server tick is single-threaded).
/// </summary>
public sealed class CommanderPlanning
{
	public CommanderPlanning(in CommanderTraits traits, IFactory factory = null)
	{
		Factory = factory ?? new PooledHtnFactory();
		Traits = traits;
		Domain = CommanderDomain.Build(Factory);
	}

	public IFactory Factory { get; }

	public CommanderTraits Traits { get; }

	public Domain<CommandContext> Domain { get; }

	public Planner<CommandContext> Planner { get; } = new();

	/// <summary>
	/// A context, walked once through every branch of the domain and cleared again: a
	/// context grows its plan queue, traversal record and change stacks the first
	/// time a plan needs them, and a squad slot that first falls back or strikes an
	/// hour into a round would otherwise allocate then (§4.2 rule 3).
	/// </summary>
	public CommandContext CreateContext()
	{
		var context = new CommandContext(Factory, Traits);
		context.Init();

		for (int branch = 0; branch < 7; branch++)
		{
			context.Clear();
			context.Sense(CommandFact.FallingBack, branch == 0);
			context.Sense(CommandFact.Task, (byte)(branch switch
			{
				1 => CommandTask.Defend,
				2 or 3 => CommandTask.Attack,
				4 => CommandTask.Recon,
				6 => CommandTask.Supply,
				_ => CommandTask.None,
			}));
			context.Sense(CommandFact.Depleted, branch == 5);
			context.Assembled = branch == 3;

			// Twice: a staged attack strikes on its second decision.
			Planner.Tick(Domain, context);
			Planner.Tick(Domain, context);

			// And a pre-emption of whatever is running.
			context.Sense(CommandFact.FallingBack, true);
			Planner.Tick(Domain, context);
			Planner.Reset(context);
		}

		context.Clear();
		return context;
	}
}

/// <summary>
/// The computer strategist's commander (docs/HTN_BOTS.md §5.3): once a decision,
/// which of its units are in which squad, what each squad is asked to do, and — by
/// ticking each squad's planner over <see cref="CommanderDomain"/> — the order each
/// squad should be under.
///
/// Two halves, as the ground side has (§5.1): an assignment, which is a plain
/// function because it assigns rather than sequences — squads formed from
/// production, a threatened zone given the nearest squad strong enough for it, an
/// idle squad given the nearest target it is strong enough for, one scout, the supply
/// trucks stationed — and a planner per squad, which sequences: stage then strike,
/// fall back when losing, refill when depleted — at a supply source when healing can
/// bring the squad back (D2, H5), else at home where it is merged.
///
/// It knows what the side knows (§4.2 rule 2): its own units, the
/// <see cref="ZoneBoard"/> the engine half fills from the economy, the fog and the
/// unit census, and the side's <see cref="ContactMemory"/>, live contacts and
/// ghosts — ghosts for estimates, recon and staging, never to name a target (D4).
/// It leaves alone every unit somebody else has claimed (D1).
///
/// Engine-free, deterministic (ties go to the lower index) and allocation-free
/// after construction.
/// </summary>
public sealed class Commander
{
	/// <summary>The garrison's slot. The others are scouts and assaults, in any order.</summary>
	public const int GarrisonSlot = 0;

	private readonly CommanderPlanning _planning;
	private readonly CommanderTraits _traits;
	private readonly ThreatWeights _weights;

	private readonly CommandSquad[] _squads;
	private readonly CommandContext[] _contexts;

	/// <summary>The binding each squad's plan was made for: a new one resets the plan.</summary>
	private readonly CommandTask[] _boundTask;
	private readonly int[] _boundZone;
	private readonly int[] _boundOwner;
	private readonly CommandTarget[] _boundTarget;

	/// <summary>Membership rows: a unit, and the squad it is in. Packed at the front.</summary>
	private readonly ushort[] _memberUnit;
	private readonly byte[] _memberSquad;
	private int _members;

	/// <summary>Per input unit, this decision: its squad, or -1.</summary>
	private readonly int[] _unitSquad;

	private readonly Vector3[] _sum;

	/// <summary>Per zone, this decision: already given a defender.</summary>
	private readonly bool[] _zoneDone = new bool[ZoneBoard.DefaultCapacity];

	/// <summary>The last tick a contact was live; the first decision's until one is, so a quiet start counts as quiet.</summary>
	private uint _lastLiveTick;
	private bool _started;

	public Commander(CommanderPlanning planning, in ThreatWeights weights, int squads = SquadBoard.DefaultSquads,
		int members = SimConfig.MaxUnits)
	{
		_planning = planning ?? throw new ArgumentNullException(nameof(planning));
		_traits = planning.Traits;
		_weights = weights;

		squads = Math.Clamp(squads, 2, byte.MaxValue);
		members = Math.Max(members, 1);

		_squads = new CommandSquad[squads];
		_contexts = new CommandContext[squads];
		_boundTask = new CommandTask[squads];
		_boundZone = new int[squads];
		_boundOwner = new int[squads];
		_boundTarget = new CommandTarget[squads];
		_sum = new Vector3[squads];
		for (int s = 0; s < squads; s++)
		{
			_contexts[s] = planning.CreateContext();
			_boundZone[s] = -1;
		}

		_memberUnit = new ushort[members];
		_memberSquad = new byte[members];
		_unitSquad = new int[members];
	}

	/// <summary>Squad slots.</summary>
	public int Capacity => _squads.Length;

	/// <summary>Units the last decision counted, past what it can hold. Anything but 0 is a sizing bug.</summary>
	public int Overflows { get; private set; }

	/// <summary>
	/// The zone the last decision sent reserve production to because no squad was
	/// strong enough for its threat (§5.3, "buy time"); -1 for none. The engine half
	/// fortifies it first.
	/// </summary>
	public int BuyTimeZone { get; private set; } = -1;

	/// <summary>Whether the last decision wanted a scout out.</summary>
	public bool ReconWanted { get; private set; }

	public CommandSquad SquadAt(int squad) => InRange(squad) ? _squads[squad] : default;

	/// <summary>What the squad's running task asked for at the last decision.</summary>
	public CommandIntent IntentOf(int squad) => InRange(squad) && _squads[squad].Active ? _contexts[squad].Intent : default;

	/// <summary>The squad's planning context. For the tests and the debug HUD.</summary>
	public CommandContext ContextOf(int squad) => InRange(squad) ? _contexts[squad] : null;

	/// <summary>The squad this unit is in, or -1.</summary>
	public int SquadOf(ushort unitId)
	{
		int row = RowOf(unitId);
		return row < 0 ? -1 : _memberSquad[row];
	}

	/// <summary>Writes a squad's units into <paramref name="into"/>, as many as fit, and returns how many.</summary>
	public int MembersOf(int squad, Span<ushort> into)
	{
		int count = 0;
		for (int row = 0; row < _members && count < into.Length; row++)
		{
			if (_memberSquad[row] == squad)
			{
				into[count++] = _memberUnit[row];
			}
		}

		return count;
	}

	/// <summary>Disbands everything, between rounds.</summary>
	public void Clear()
	{
		for (int s = 0; s < _squads.Length; s++)
		{
			Close(s);
		}

		_members = 0;
		_started = false;
		_lastLiveTick = 0;
		BuyTimeZone = -1;
		ReconWanted = false;
		Overflows = 0;
	}

	/// <summary>
	/// One decision. <paramref name="units"/> is every fighting unit of the side, in
	/// the unit manager's order; <paramref name="zones"/> the side's board, rebuilt
	/// for this decision; <paramref name="contacts"/> the side's memory, which may be
	/// null. Afterwards each active squad's <see cref="IntentOf"/> is the order its
	/// members should be under.
	/// </summary>
	public void Decide(in CommanderWorld world, ReadOnlySpan<CommandUnit> units, ZoneBoard zones,
		ContactMemory contacts)
	{
		uint tick = world.Tick;
		if (units.Length > _unitSquad.Length)
		{
			Overflows += units.Length - _unitSquad.Length;
			units = units[.._unitSquad.Length];
		}

		if (!_started || (contacts != null && contacts.LiveCount(tick) > 0))
		{
			_lastLiveTick = tick;
			_started = true;
		}

		Sync(units);
		Aggregate(units);

		ReconWanted = WantsRecon(tick, zones);
		for (int s = 0; s < _squads.Length; s++)
		{
			if (_squads[s].Active && (_squads[s].Members == 0 || (_squads[s].Role == CommandRole.Scout && !ReconWanted)))
			{
				Dissolve(s, units);
			}
		}

		Route(tick, units);
		Aggregate(units);

		for (int s = 0; s < _squads.Length; s++)
		{
			if (_squads[s].Active)
			{
				Assess(s, world, units, zones, contacts);
			}
		}

		BuyTimeZone = -1;
		AssignGarrison(world, zones);
		AssignDefence(world, zones, contacts);
		AssignAttacks(tick, zones, contacts);
		AssignScout(tick, zones);
		AssignSupply(world, contacts);

		for (int s = 0; s < _squads.Length; s++)
		{
			if (_squads[s].Active)
			{
				Plan(s, world, units);
			}
		}
	}

	// ---- membership --------------------------------------------------------

	/// <summary>Drops the rows of units that are gone or claimed, and finds every unit's squad.</summary>
	private void Sync(ReadOnlySpan<CommandUnit> units)
	{
		for (int row = _members - 1; row >= 0; row--)
		{
			int index = IndexOf(units, _memberUnit[row]);
			if (index < 0 || units[index].Claimed)
			{
				RemoveRow(row);
			}
		}

		for (int i = 0; i < units.Length; i++)
		{
			_unitSquad[i] = units[i].Claimed ? -1 : SquadOf(units[i].Id);
		}
	}

	private void Aggregate(ReadOnlySpan<CommandUnit> units)
	{
		for (int s = 0; s < _squads.Length; s++)
		{
			_squads[s].Members = 0;
			_squads[s].Armour = 0;
			_squads[s].Engaging = 0;
			_squads[s].Strength = 0f;
			_squads[s].FullStrength = 0f;
			_sum[s] = Vector3.Zero;
		}

		for (int i = 0; i < units.Length; i++)
		{
			int s = _unitSquad[i];
			if (s < 0)
			{
				continue;
			}

			ref CommandSquad squad = ref _squads[s];
			squad.Members++;
			squad.Armour += units[i].Armour ? 1 : 0;
			squad.Engaging += units[i].Engaging ? 1 : 0;
			squad.Strength += MathF.Max(units[i].Strength, 0f);
			squad.FullStrength += MathF.Max(units[i].FullStrength, 0f);
			_sum[s] += units[i].Position;
		}

		for (int s = 0; s < _squads.Length; s++)
		{
			if (_squads[s].Members > 0)
			{
				_squads[s].Centroid = _sum[s] / _squads[s].Members;
			}
		}
	}

	/// <summary>
	/// Puts every unit in no squad into one, in the unit manager's order: a supply
	/// truck into the trucks' squad, and never anywhere else; the garrison while it is
	/// short, riflemen only — a tank goes with infantry, and a technical's 70 m sensor
	/// is wasted at home; one scout when recon is wanted; then a depleted squad that is
	/// refilling; then, for a tank, an infantry squad to escort it; then an idle squad
	/// too weak for its target; then the squad forming from production.
	/// </summary>
	private void Route(uint tick, ReadOnlySpan<CommandUnit> units)
	{
		for (int i = 0; i < units.Length; i++)
		{
			if (!Free(units, i) || !units[i].Supply)
			{
				continue;
			}

			int supply = FindRole(CommandRole.Supply);
			int slot = supply >= 0 ? supply : FreeSlot();
			if (slot >= 0)
			{
				Join(Ensure(slot, CommandRole.Supply, tick), units, i);
			}
		}

		for (int i = 0; i < units.Length; i++)
		{
			if (Free(units, i) && !units[i].Armour && !units[i].Scout && !units[i].Supply
				&& _squads[GarrisonSlot].Members < _traits.GarrisonUnits)
			{
				Join(Ensure(GarrisonSlot, CommandRole.Garrison, tick), units, i);
			}
		}

		if (ReconWanted && FindRole(CommandRole.Scout) < 0)
		{
			int scout = Recruit(units);
			int slot = scout >= 0 ? FreeSlot() : -1;
			if (slot >= 0)
			{
				Leave(units, scout);
				Join(Ensure(slot, CommandRole.Scout, tick), units, scout);
			}
		}

		for (int i = 0; i < units.Length; i++)
		{
			// A truck with no slot of its own is left alone rather than sent to fight.
			if (!Free(units, i) || units[i].Supply)
			{
				continue;
			}

			int squad = Refilling();
			if (squad < 0 && units[i].Armour)
			{
				squad = Escort();
			}

			if (squad < 0)
			{
				squad = ShortSquad();
			}

			if (squad < 0)
			{
				squad = Forming(tick);
			}

			Join(squad, units, i);
		}
	}

	private bool Free(ReadOnlySpan<CommandUnit> units, int i) => _unitSquad[i] < 0 && !units[i].Claimed;

	/// <summary>
	/// The scout (§5.3, "one technical — else one rifleman"): a technical in no squad,
	/// else one in an assault squad with nothing to do and no fight, else a rifleman
	/// in no squad, else one from the squad still forming. -1 for none.
	/// </summary>
	private int Recruit(ReadOnlySpan<CommandUnit> units)
	{
		for (int pass = 0; pass < 4; pass++)
		{
			for (int i = 0; i < units.Length; i++)
			{
				if (units[i].Claimed || units[i].Armour || units[i].Supply || (pass < 2 && !units[i].Scout))
				{
					continue;
				}

				int squad = _unitSquad[i];
				bool candidate = pass switch
				{
					0 or 2 => squad < 0,
					1 => squad >= 0 && Idle(_squads[squad]) && _squads[squad].Engaging == 0,
					_ => squad >= 0 && Idle(_squads[squad]) && !_squads[squad].Ready,
				};

				if (candidate)
				{
					return i;
				}
			}
		}

		return -1;
	}

	/// <summary>Takes a unit out of its squad, and what it brought with it: it was moved, not lost.</summary>
	private void Leave(ReadOnlySpan<CommandUnit> units, int i)
	{
		int squad = _unitSquad[i];
		int row = RowOf(units[i].Id);
		if (squad < 0 || row < 0)
		{
			return;
		}

		RemoveRow(row);
		_unitSquad[i] = -1;

		ref CommandSquad left = ref _squads[squad];
		float strength = MathF.Max(units[i].Strength, 0f);
		left.Members--;
		left.Armour -= units[i].Armour ? 1 : 0;
		left.Strength -= strength;
		left.StrengthAtFormation = MathF.Max(left.StrengthAtFormation - strength, 0f);
		if (left.Members == 0)
		{
			Dissolve(squad, units);
		}
	}

	private int Refilling()
	{
		for (int s = 0; s < _squads.Length; s++)
		{
			ref CommandSquad squad = ref _squads[s];
			if (squad.Active && squad.Role == CommandRole.Assault && squad.Depleted && !squad.FallingBack
				&& squad.Strength < squad.StrengthAtFormation)
			{
				return s;
			}
		}

		return -1;
	}

	/// <summary>The assault squad with infantry in it and the fewest tanks, then the most members.</summary>
	private int Escort()
	{
		int best = -1;
		for (int s = 0; s < _squads.Length; s++)
		{
			ref CommandSquad squad = ref _squads[s];
			if (!squad.Active || squad.Role != CommandRole.Assault || squad.FallingBack || squad.Members - squad.Armour <= 0)
			{
				continue;
			}

			if (best < 0 || squad.Armour < _squads[best].Armour
				|| (squad.Armour == _squads[best].Armour && squad.Members > _squads[best].Members))
			{
				best = s;
			}
		}

		return best;
	}

	private int ShortSquad()
	{
		for (int s = 0; s < _squads.Length; s++)
		{
			ref CommandSquad squad = ref _squads[s];
			if (squad.Active && squad.Role == CommandRole.Assault && squad.Short && squad.Task == CommandTask.None
				&& !squad.FallingBack && !squad.Depleted)
			{
				return s;
			}
		}

		return -1;
	}

	/// <summary>The assault squad still forming, else a new one, else the smallest assault squad, else the garrison.</summary>
	private int Forming(uint tick)
	{
		for (int s = 0; s < _squads.Length; s++)
		{
			if (_squads[s].Active && _squads[s].Role == CommandRole.Assault && !_squads[s].Ready)
			{
				return s;
			}
		}

		int slot = FreeSlot();
		if (slot >= 0)
		{
			return Ensure(slot, CommandRole.Assault, tick);
		}

		int smallest = -1;
		for (int s = 0; s < _squads.Length; s++)
		{
			if (_squads[s].Active && _squads[s].Role == CommandRole.Assault
				&& (smallest < 0 || _squads[s].Members < _squads[smallest].Members))
			{
				smallest = s;
			}
		}

		return smallest >= 0 ? smallest : Ensure(GarrisonSlot, CommandRole.Garrison, tick);
	}

	private int FindRole(CommandRole role)
	{
		for (int s = 0; s < _squads.Length; s++)
		{
			if (_squads[s].Active && _squads[s].Role == role)
			{
				return s;
			}
		}

		return -1;
	}

	/// <summary>The first free slot after the garrison's, or -1.</summary>
	private int FreeSlot()
	{
		for (int s = GarrisonSlot + 1; s < _squads.Length; s++)
		{
			if (!_squads[s].Active)
			{
				return s;
			}
		}

		return -1;
	}

	private int Ensure(int slot, CommandRole role, uint tick)
	{
		if (!_squads[slot].Active)
		{
			Close(slot);
			_squads[slot].Active = true;
			_squads[slot].Role = role;
			_squads[slot].FormedTick = tick;
		}

		return slot;
	}

	private void Join(int squad, ReadOnlySpan<CommandUnit> units, int i)
	{
		if (squad < 0)
		{
			return;
		}

		if (_members == _memberUnit.Length)
		{
			Overflows++;
			return;
		}

		_memberUnit[_members] = units[i].Id;
		_memberSquad[_members] = (byte)squad;
		_members++;
		_unitSquad[i] = squad;

		ref CommandSquad joined = ref _squads[squad];
		joined.Members++;
		joined.Armour += units[i].Armour ? 1 : 0;
		joined.Strength += MathF.Max(units[i].Strength, 0f);

		// The most it has been: a unit refilling a depleted squad makes up what it lost
		// rather than raising the bar it is measured against.
		joined.StrengthAtFormation = MathF.Max(joined.StrengthAtFormation, joined.Strength);
		if (joined.Role == CommandRole.Assault && joined.Members >= _traits.AssaultSquadSize)
		{
			joined.Ready = true;
		}
	}

	/// <summary>Takes every unit out of a squad and closes it. Its units are routed again at the next decision.</summary>
	private void Dissolve(int squad, ReadOnlySpan<CommandUnit> units)
	{
		for (int row = _members - 1; row >= 0; row--)
		{
			if (_memberSquad[row] == squad)
			{
				RemoveRow(row);
			}
		}

		for (int i = 0; i < units.Length; i++)
		{
			if (_unitSquad[i] == squad)
			{
				_unitSquad[i] = -1;
			}
		}

		Close(squad);
	}

	private void Close(int squad)
	{
		_planning.Planner.Reset(_contexts[squad]);
		_contexts[squad].Clear();
		_squads[squad] = new CommandSquad { Zone = -1 };
		_boundTask[squad] = CommandTask.None;
		_boundZone[squad] = -1;
		_boundOwner[squad] = OwnerId.None;
		_boundTarget[squad] = CommandTarget.None;
	}

	private int RowOf(ushort unitId)
	{
		for (int row = 0; row < _members; row++)
		{
			if (_memberUnit[row] == unitId)
			{
				return row;
			}
		}

		return -1;
	}

	private void RemoveRow(int row)
	{
		int last = _members - 1;
		_memberUnit[row] = _memberUnit[last];
		_memberSquad[row] = _memberSquad[last];
		_memberUnit[last] = 0;
		_memberSquad[last] = 0;
		_members--;
	}

	private static int IndexOf(ReadOnlySpan<CommandUnit> units, ushort id)
	{
		for (int i = 0; i < units.Length; i++)
		{
			if (units[i].Id == id)
			{
				return i;
			}
		}

		return -1;
	}

	// ---- what each squad's facts are ----------------------------------------

	/// <summary>Depleted, and losing: a squad losing its fight starts falling back, and gives its task up.</summary>
	private void Assess(int s, in CommanderWorld world, ReadOnlySpan<CommandUnit> units, ZoneBoard zones,
		ContactMemory contacts)
	{
		ref CommandSquad squad = ref _squads[s];
		float formation = squad.StrengthAtFormation;

		if (squad.Role == CommandRole.Assault && squad.Ready)
		{
			float share = squad.Depleted ? _traits.ResupplyShare + _traits.ResupplyHysteresis : _traits.ResupplyShare;
			squad.Depleted = squad.Strength < share * formation;

			// Whether healing alone would end it: its units at full health are worth what
			// a depleted squad has to get back to. One that has lost too many cannot heal
			// its way back, and is merged at home as before D2.
			squad.Healable = squad.FullStrength >= (_traits.ResupplyShare + _traits.ResupplyHysteresis) * formation;
		}

		if (squad.Role != CommandRole.Garrison && !squad.FallingBack && squad.Strength < _traits.RetreatShare * formation)
		{
			float enemy = EnemyNear(contacts, world.Tick, squad.Centroid, _traits.EnemyNearMeters);
			if (enemy > 0f && enemy >= _traits.RetreatEnemyRatio * squad.Strength)
			{
				squad.FallingBack = true;
				squad.FallbackPoint = Fallback(s, world, zones, contacts);
				Drop(ref squad);
			}
		}
	}

	/// <summary>
	/// The nearest zone the side holds with friends at it that are not themselves
	/// outmatched — the squad's own strength not counted — else home (§5.3, "the
	/// nearest held zone with friends").
	/// </summary>
	private Vector3 Fallback(int s, in CommanderWorld world, ZoneBoard zones, ContactMemory contacts)
	{
		ref CommandSquad squad = ref _squads[s];
		Vector3 best = world.Home;
		float bestDistance = float.MaxValue;

		for (int z = 0; zones != null && z < zones.Count; z++)
		{
			Zone zone = zones.At(z);
			if (!Held(zone))
			{
				continue;
			}

			float friends = zone.FriendlyStrength;
			if (Flat(zone.Position, squad.Centroid) <= zone.RadiusMeters)
			{
				friends -= squad.Strength;
			}

			if (friends <= 0f || EnemyNear(contacts, world.Tick, zone.Position, _traits.ThreatMeters) >= friends)
			{
				continue;
			}

			float distance = Flat(zone.Position, squad.Centroid);
			if (distance < bestDistance)
			{
				bestDistance = distance;
				best = zone.Position;
			}
		}

		return best;
	}

	private bool WantsRecon(uint tick, ZoneBoard zones)
	{
		if (tick - _lastLiveTick >= (uint)_traits.ReconQuietTicks)
		{
			return true;
		}

		int stalest = zones?.Stalest(ZoneKind.ResourceNode, tick) ?? -1;
		return stalest >= 0 && zones.TicksSinceObserved(stalest, tick) >= (uint)_traits.ReconStaleTicks;
	}

	// ---- assignment --------------------------------------------------------

	/// <summary>
	/// Defend Zone (§5.3): every zone the side holds that is contested, or has a
	/// remembered contact within <see cref="CommanderTraits.ThreatMeters"/>, most
	/// threatened first, is given the nearest idle or forming squad at least
	/// <see cref="CommanderTraits.ReinforceRatio"/> times the threat — unless the
	/// squads already there are. With none that strong, the forming squad goes anyway
	/// and the zone is fortified first: buying time.
	/// </summary>
	private void AssignDefence(in CommanderWorld world, ZoneBoard zones, ContactMemory contacts)
	{
		uint tick = world.Tick;

		// A squad keeps a zone while the zone is still threatened.
		for (int s = 0; s < _squads.Length; s++)
		{
			ref CommandSquad squad = ref _squads[s];
			if (squad.Active && squad.Role == CommandRole.Assault && squad.Task == CommandTask.Defend
				&& !Threatened(zones, squad.Zone, contacts, tick, out _))
			{
				Drop(ref squad);
			}
		}

		// Most threatened first, then the lower index; a zone at a time.
		int count = zones == null ? 0 : Math.Min(zones.Count, _zoneDone.Length);
		Array.Clear(_zoneDone);
		for (int handled = 0; handled < count; handled++)
		{
			int worst = -1;
			float worstThreat = -1f;
			for (int z = 0; z < count; z++)
			{
				if (!_zoneDone[z] && Threatened(zones, z, contacts, tick, out float threat) && threat > worstThreat)
				{
					worst = z;
					worstThreat = threat;
				}
			}

			if (worst < 0)
			{
				break;
			}

			_zoneDone[worst] = true;
			Defend(worst, worstThreat, world, zones);
		}
	}

	private void Defend(int zone, float threat, in CommanderWorld world, ZoneBoard zones)
	{
		Vector3 at = zones.At(zone).Position;
		float needed = _traits.ReinforceRatio * threat;

		float covered = 0f;
		for (int s = 0; s < _squads.Length; s++)
		{
			ref CommandSquad squad = ref _squads[s];
			if (squad.Active && squad.Task == CommandTask.Defend && squad.Zone == zone)
			{
				covered += squad.Strength;
			}
		}

		if (covered > 0f && covered >= needed)
		{
			return;
		}

		int best = -1;
		float bestDistance = float.MaxValue;
		for (int s = 0; s < _squads.Length; s++)
		{
			ref CommandSquad squad = ref _squads[s];
			if (!Idle(squad) || squad.Strength <= 0f || squad.Strength < needed)
			{
				continue;
			}

			float distance = Flat(squad.Centroid, at);
			if (distance < bestDistance)
			{
				bestDistance = distance;
				best = s;
			}
		}

		if (best < 0)
		{
			// Buy time: the reserve goes now, and the zone is fortified first.
			for (int s = 0; s < _squads.Length && best < 0; s++)
			{
				best = Idle(_squads[s]) && !_squads[s].Ready ? s : -1;
			}

			if (BuyTimeZone < 0)
			{
				BuyTimeZone = zone;
			}
		}

		if (best >= 0)
		{
			Give(ref _squads[best], CommandTask.Defend, CommandTarget.Zone, zone, OwnerId.None, at, world.Tick);
		}
	}

	/// <summary>An assault squad with no task, not falling back: what Defend Zone may take.</summary>
	private static bool Idle(in CommandSquad squad) =>
		squad.Active && squad.Role == CommandRole.Assault && squad.Task == CommandTask.None && !squad.FallingBack;

	private bool Threatened(ZoneBoard zones, int z, ContactMemory contacts, uint tick, out float threat)
	{
		threat = 0f;
		if (zones == null || z < 0 || z >= zones.Count)
		{
			return false;
		}

		Zone zone = zones.At(z);
		if (!Held(zone))
		{
			return false;
		}

		threat = EnemyNear(contacts, tick, zone.Position, _traits.ThreatMeters);
		return zone.Contested || threat > 0f;
	}

	/// <summary>
	/// Attack (§5.3): an idle, ready, undepleted squad is given the nearest target it
	/// is at least <see cref="CommanderTraits.AttackRatio"/> times — a live contact,
	/// then a node the ground force holds, then a neutral node — that no other squad
	/// has. When nothing at all is known it sweeps the ground spawns in turn, as
	/// legacy does. A squad too weak for every known target waits, and is sent the
	/// next units produced.
	/// </summary>
	private void AssignAttacks(uint tick, ZoneBoard zones, ContactMemory contacts)
	{
		bool known = false;
		for (int i = 0; contacts != null && i < contacts.Count && !known; i++)
		{
			known = contacts.IsLive(i, tick);
		}

		for (int z = 0; zones != null && z < zones.Count && !known; z++)
		{
			known = Takeable(zones.At(z));
		}

		// Keep what is still worth taking; let go of the rest.
		for (int s = 0; s < _squads.Length; s++)
		{
			ref CommandSquad squad = ref _squads[s];
			if (!squad.Active || squad.Task != CommandTask.Attack)
			{
				continue;
			}

			bool keep = squad.Target switch
			{
				CommandTarget.Contact => TryRemembered(contacts, squad.TargetOwnerId, tick, out squad.TaskPoint),
				CommandTarget.Zone => zones != null && squad.Zone < zones.Count && Takeable(zones.At(squad.Zone)),
				CommandTarget.Sweep => !known && tick - squad.TaskTick < (uint)_traits.SweepIntervalTicks
					&& Flat(squad.Centroid, squad.TaskPoint) > _traits.ArriveMeters,
				_ => false,
			};

			// A depleted squad out of the fight goes to refill rather than on.
			if (!keep || (squad.Depleted && squad.Engaging == 0))
			{
				Drop(ref squad);
			}
		}

		for (int s = 0; s < _squads.Length; s++)
		{
			ref CommandSquad squad = ref _squads[s];
			squad.Short = false;
			if (!Idle(squad) || !squad.Ready || squad.Depleted)
			{
				continue;
			}

			if (TryTarget(s, tick, zones, contacts, known, out CommandTarget target, out int zone, out int owner,
				out Vector3 point))
			{
				Give(ref squad, CommandTask.Attack, target, zone, owner, point, tick);
				squad.StagingPoint = Staging(point, squad.Centroid);
			}
			else
			{
				squad.Short = known;
			}
		}
	}

	private bool TryTarget(int s, uint tick, ZoneBoard zones, ContactMemory contacts, bool known,
		out CommandTarget target, out int zone, out int owner, out Vector3 point)
	{
		ref CommandSquad squad = ref _squads[s];
		target = CommandTarget.None;
		zone = -1;
		owner = OwnerId.None;
		point = Vector3.Zero;
		float bestDistance = float.MaxValue;

		// Live contacts: the only ones it may name (D4).
		for (int i = 0; contacts != null && i < contacts.Count; i++)
		{
			if (!contacts.IsLive(i, tick))
			{
				continue;
			}

			KnownContact contact = contacts.At(i);
			float distance = Flat(squad.Centroid, contact.Position);
			if (distance >= bestDistance || Targeted(CommandTarget.Contact, -1, contact.OwnerId)
				|| !Beats(squad.Strength, EnemyNear(contacts, tick, contact.Position, _traits.ClusterMeters)))
			{
				continue;
			}

			bestDistance = distance;
			target = CommandTarget.Contact;
			owner = contact.OwnerId;
			point = contact.Position;
		}

		if (target != CommandTarget.None)
		{
			return true;
		}

		// Nodes: a denied one first, then a neutral one.
		for (int pass = 0; pass < 2 && target == CommandTarget.None; pass++)
		{
			NodeHolder wanted = pass == 0 ? NodeHolder.GroundForce : NodeHolder.Neutral;
			for (int z = 0; zones != null && z < zones.Count; z++)
			{
				Zone candidate = zones.At(z);
				if (candidate.Kind != ZoneKind.ResourceNode || candidate.Holder != wanted)
				{
					continue;
				}

				float distance = Flat(squad.Centroid, candidate.Position);
				if (distance >= bestDistance || Targeted(CommandTarget.Zone, z, OwnerId.None)
					|| !Beats(squad.Strength, EnemyNear(contacts, tick, candidate.Position,
						candidate.RadiusMeters + _traits.ClusterMeters)))
				{
					continue;
				}

				bestDistance = distance;
				target = CommandTarget.Zone;
				zone = z;
				point = candidate.Position;
			}
		}

		if (target != CommandTarget.None || known)
		{
			return target != CommandTarget.None;
		}

		// Nothing known: legacy's sweep, one spawn at a time.
		int spawns = 0;
		for (int z = 0; zones != null && z < zones.Count; z++)
		{
			spawns += zones.At(z).Kind == ZoneKind.GroundSpawn ? 1 : 0;
		}

		if (spawns == 0)
		{
			return false;
		}

		int turn = (int)(tick / (uint)_traits.SweepIntervalTicks % (uint)spawns);
		for (int z = 0; z < zones.Count; z++)
		{
			if (zones.At(z).Kind != ZoneKind.GroundSpawn || turn-- != 0)
			{
				continue;
			}

			target = CommandTarget.Sweep;
			zone = z;
			point = zones.At(z).Position;
			return true;
		}

		return false;
	}

	private bool Beats(float strength, float estimate) => strength > 0f && strength >= _traits.AttackRatio * estimate;

	private bool Targeted(CommandTarget target, int zone, int owner)
	{
		for (int s = 0; s < _squads.Length; s++)
		{
			ref CommandSquad squad = ref _squads[s];
			if (squad.Active && squad.Task == CommandTask.Attack && squad.Target == target
				&& (target == CommandTarget.Contact ? squad.TargetOwnerId == owner : squad.Zone == zone))
			{
				return true;
			}
		}

		return false;
	}

	/// <summary>
	/// Where a squad stages: <see cref="CommanderTraits.StagingMeters"/> short of the
	/// target on its own side, past what the target's eyes reach; where it stands
	/// when it is already that near.
	/// </summary>
	public Vector3 Staging(Vector3 target, Vector3 centroid)
	{
		Vector3 away = centroid - target;
		away.Y = 0f;
		float distance = away.Length();
		if (distance <= _traits.StagingMeters + _traits.AssembleMeters)
		{
			return centroid;
		}

		Vector3 point = target + (away / distance * _traits.StagingMeters);
		point.Y = centroid.Y;
		return point;
	}

	/// <summary>
	/// The garrison is a standing Defend Zone squad at home (§5.3), counted as the
	/// defence of the barracks zone it stands in, so that a threat there sends
	/// another squad only when the garrison is not enough.
	/// </summary>
	private void AssignGarrison(in CommanderWorld world, ZoneBoard zones)
	{
		ref CommandSquad garrison = ref _squads[GarrisonSlot];
		if (!garrison.Active)
		{
			return;
		}

		int home = -1;
		float nearest = float.MaxValue;
		for (int z = 0; zones != null && z < zones.Count; z++)
		{
			Zone zone = zones.At(z);
			float distance = Flat(zone.Position, world.Home);
			if (zone.Kind == ZoneKind.Barracks && Held(zone) && distance <= zone.RadiusMeters && distance < nearest)
			{
				home = z;
				nearest = distance;
			}
		}

		garrison.Task = CommandTask.Defend;
		garrison.Target = CommandTarget.Zone;
		garrison.Zone = home;
		garrison.TaskPoint = world.Home;
	}

	/// <summary>The scout patrols to the zone the side has gone longest without seeing.</summary>
	private void AssignScout(uint tick, ZoneBoard zones)
	{
		int s = FindRole(CommandRole.Scout);
		if (s < 0 || _squads[s].FallingBack)
		{
			return;
		}

		ref CommandSquad squad = ref _squads[s];
		int zone = ReconZone(tick, zones);
		if (zone >= 0 && (squad.Task != CommandTask.Recon || squad.Zone != zone))
		{
			Give(ref squad, CommandTask.Recon, CommandTarget.Zone, zone, OwnerId.None, zones.At(zone).Position, tick);
		}
	}

	/// <summary>
	/// Where the supply trucks stand (D2, H5), as one squad: with a depleted squad that
	/// healing will restore and that is out of its fight — one coming to them to heal,
	/// to meet it, or one still holding a zone, to heal it there; else at the staging
	/// point of the nearest attack — out of the target's sight, and never nearer it
	/// than a staging point would be — which its units come back to; else behind the
	/// nearest squad defending a zone away from home; else in reserve in front of home.
	/// Only where the side remembers no enemy within
	/// <see cref="CommanderTraits.SupplyStandoffMeters"/>: a truck is soft, and one sent
	/// to a squad that has only just broken contact is shot where the squad was.
	/// </summary>
	private void AssignSupply(in CommanderWorld world, ContactMemory contacts)
	{
		int s = FindRole(CommandRole.Supply);
		if (s < 0 || _squads[s].FallingBack)
		{
			return;
		}

		ref CommandSquad trucks = ref _squads[s];
		Vector3 point = Station(trucks.Centroid, world, contacts);
		if (trucks.Task != CommandTask.Supply)
		{
			Give(ref trucks, CommandTask.Supply, CommandTarget.None, -1, OwnerId.None, point, world.Tick);
		}
		else
		{
			trucks.TaskPoint = point;
		}
	}

	private Vector3 Station(Vector3 trucks, in CommanderWorld world, ContactMemory contacts)
	{
		for (int kind = 0; kind < 3; kind++)
		{
			int best = -1;
			float bestDistance = float.MaxValue;
			Vector3 bestPoint = Vector3.Zero;
			for (int s = 0; s < _squads.Length; s++)
			{
				ref CommandSquad squad = ref _squads[s];
				if (!squad.Active || !StationFor(kind, squad, trucks, world, out Vector3 point))
				{
					continue;
				}

				float distance = Flat(point, trucks);
				if (distance < bestDistance && EnemyNear(contacts, world.Tick, point, _traits.SupplyStandoffMeters) <= 0f)
				{
					best = s;
					bestDistance = distance;
					bestPoint = point;
				}
			}

			if (best >= 0)
			{
				return bestPoint;
			}
		}

		return Reserve(world);
	}

	/// <summary>
	/// Where the trucks would stand for <paramref name="squad"/>, by kind of station: 0,
	/// a depleted squad healing will restore, out of its fight and nearer the trucks
	/// than home, whatever its task — at it; 1, an attack — a staging distance back from
	/// its target; 2, a defence away from home — behind it.
	/// </summary>
	private bool StationFor(int kind, in CommandSquad squad, Vector3 trucks, in CommanderWorld world, out Vector3 point)
	{
		point = Vector3.Zero;
		switch (kind)
		{
			case 0:
				// One that home is nearer is going home (NearestSupply), so it is not met.
				if (squad.Role != CommandRole.Assault || !squad.Depleted || !squad.Healable || squad.FallingBack
					|| squad.Engaging > 0 || Flat(squad.Centroid, trucks) >= Flat(squad.Centroid, world.Home))
				{
					return false;
				}

				point = squad.Centroid;
				return true;

			case 1:
				if (squad.Task != CommandTask.Attack)
				{
					return false;
				}

				point = Behind(squad.TaskPoint, squad.StagingPoint, world.Home, _traits.StagingMeters);
				return true;

			default:
				if (squad.Role != CommandRole.Assault || squad.Task != CommandTask.Defend
					|| Flat(squad.TaskPoint, world.Home) <= _traits.ArriveMeters)
				{
					return false;
				}

				point = Behind(squad.TaskPoint, squad.TaskPoint, world.Home, _traits.SupplyStandoffMeters);
				return true;
		}
	}

	/// <summary>
	/// <paramref name="point"/> when it is at least <paramref name="meters"/> from
	/// <paramref name="target"/>; else the point that far from the target towards home,
	/// or home when home is nearer than that. A squad that staged where it stood staged
	/// in the fight, and a truck there dies in it.
	/// </summary>
	private static Vector3 Behind(Vector3 target, Vector3 point, Vector3 home, float meters)
	{
		if (Flat(point, target) >= meters - 0.5f)
		{
			return point;
		}

		Vector3 back = home - target;
		back.Y = 0f;
		float length = back.Length();
		return length > meters ? target + (back / length * meters) : home;
	}

	/// <summary>A node unseen for <see cref="CommanderTraits.ReconStaleTicks"/>, the stalest; else the stalest zone of any kind.</summary>
	private int ReconZone(uint tick, ZoneBoard zones)
	{
		if (zones == null)
		{
			return -1;
		}

		int node = zones.Stalest(ZoneKind.ResourceNode, tick);
		if (node >= 0 && zones.TicksSinceObserved(node, tick) >= (uint)_traits.ReconStaleTicks)
		{
			return node;
		}

		int best = -1;
		uint oldest = 0;
		for (int z = 0; z < zones.Count; z++)
		{
			uint age = zones.TicksSinceObserved(z, tick);
			if (!Held(zones.At(z)) && (best < 0 || age > oldest))
			{
				best = z;
				oldest = age;
			}
		}

		return best >= 0 ? best : node;
	}

	private static void Give(ref CommandSquad squad, CommandTask task, CommandTarget target, int zone, int owner,
		Vector3 point, uint tick)
	{
		squad.Task = task;
		squad.Target = target;
		squad.Zone = zone;
		squad.TargetOwnerId = owner;
		squad.TaskPoint = point;
		squad.TaskTick = tick;
	}

	private static void Drop(ref CommandSquad squad)
	{
		squad.Task = CommandTask.None;
		squad.Target = CommandTarget.None;
		squad.Zone = -1;
		squad.TargetOwnerId = OwnerId.None;
	}

	// ---- planning ----------------------------------------------------------

	/// <summary>The squad's facts and places into its context, then one tick of its planner.</summary>
	private void Plan(int s, in CommanderWorld world, ReadOnlySpan<CommandUnit> units)
	{
		ref CommandSquad squad = ref _squads[s];
		CommandContext context = _contexts[s];

		// A new task, or the same task somewhere else, is a new plan: an attack on a new
		// target stages again.
		if (_boundTask[s] != squad.Task || _boundZone[s] != squad.Zone || _boundOwner[s] != squad.TargetOwnerId
			|| _boundTarget[s] != squad.Target)
		{
			_planning.Planner.Reset(context);
			context.End();
			_boundTask[s] = squad.Task;
			_boundZone[s] = squad.Zone;
			_boundOwner[s] = squad.TargetOwnerId;
			_boundTarget[s] = squad.Target;
		}

		context.Tick = world.Tick;
		context.TaskPoint = squad.TaskPoint;
		context.TaskOwnerId = squad.TargetOwnerId;
		context.StagingPoint = squad.StagingPoint;
		context.Assembled = Assembled(s, squad.StagingPoint, units);
		context.FallbackPoint = squad.FallbackPoint;
		context.AtFallback = Flat(squad.Centroid, squad.FallbackPoint) <= _traits.ArriveMeters;
		context.HomePoint = world.Home;
		context.AtHome = Flat(squad.Centroid, world.Home) <= _traits.ArriveMeters;
		context.ReservePoint = Reserve(world);
		context.Healable = squad.Healable;
		context.SupplyPoint = NearestSupply(squad.Centroid, world.Home, units);
		context.Arrived = false;

		context.Sense(CommandFact.Task, (byte)squad.Task);
		context.Sense(CommandFact.FallingBack, squad.FallingBack);
		context.Sense(CommandFact.Depleted, squad.Depleted);
		context.Sense(CommandFact.Engaged, squad.Engaging > 0);

		_planning.Planner.Tick(_planning.Domain, context);

		if (context.Arrived)
		{
			// Merged on arrival (§5.3): its units go back to the pool, and the next
			// decision routes them — to a squad refilling, else the one forming. A
			// depleted squad that has reached home is merged the same way, rather than
			// holding a slot until production that may never come refills it.
			Dissolve(s, units);
		}
	}

	private bool Assembled(int s, Vector3 staging, ReadOnlySpan<CommandUnit> units)
	{
		int members = _squads[s].Members;
		if (members == 0)
		{
			return false;
		}

		int present = 0;
		for (int i = 0; i < units.Length; i++)
		{
			if (_unitSquad[i] == s && Flat(units[i].Position, staging) <= _traits.AssembleMeters)
			{
				present++;
			}
		}

		return present >= MathF.Ceiling(_traits.AssembleShare * members);
	}

	/// <summary>
	/// The supply source nearest a point (D2): home — the first barracks' rally point,
	/// inside its ring — or a supply truck, whoever's it is; a truck heals a squad
	/// whoever gave it its orders.
	/// </summary>
	private static Vector3 NearestSupply(Vector3 from, Vector3 home, ReadOnlySpan<CommandUnit> units)
	{
		Vector3 best = home;
		float bestDistance = Flat(from, home);
		for (int i = 0; i < units.Length; i++)
		{
			float distance = units[i].Supply ? Flat(from, units[i].Position) : float.MaxValue;
			if (distance < bestDistance)
			{
				best = units[i].Position;
				bestDistance = distance;
			}
		}

		return best;
	}

	/// <summary>Where a squad with nothing to do waits: in front of home, towards the threat.</summary>
	private Vector3 Reserve(in CommanderWorld world)
	{
		Vector3 towards = world.Threat - world.Home;
		towards.Y = 0f;
		float length = towards.Length();
		return length > 0.001f ? world.Home + (towards / length * _traits.ReserveOffsetMeters) : world.Home;
	}

	// ---- knowledge ---------------------------------------------------------

	/// <summary>
	/// The estimated enemy strength within <paramref name="radius"/> of a point: every
	/// contact the side remembers there, live or ghost, at its kind's weight times its
	/// health (§4.3).
	/// </summary>
	public float EnemyNear(ContactMemory contacts, uint tick, Vector3 point, float radius)
	{
		float strength = 0f;
		for (int i = 0; contacts != null && i < contacts.Count; i++)
		{
			if (!contacts.IsRemembered(i, tick))
			{
				continue;
			}

			KnownContact contact = contacts.At(i);
			if (Flat(contact.Position, point) <= radius)
			{
				strength += ForceRatio.Strength(_weights.Of(contact.Kind), contact.HealthFraction);
			}
		}

		return strength;
	}

	private static bool TryRemembered(ContactMemory contacts, int ownerId, uint tick, out Vector3 position)
	{
		int index = ownerId == OwnerId.None ? -1 : contacts?.IndexOf(ownerId) ?? -1;
		bool remembered = index >= 0 && contacts.IsRemembered(index, tick);
		position = remembered ? contacts.At(index).Position : Vector3.Zero;
		return remembered;
	}

	/// <summary>A node or barracks the side holds.</summary>
	private static bool Held(in Zone zone) =>
		zone.Kind != ZoneKind.GroundSpawn && zone.Holder == NodeHolder.Strategist;

	/// <summary>A node the side does not hold.</summary>
	private static bool Takeable(in Zone zone) =>
		zone.Kind == ZoneKind.ResourceNode && zone.Holder != NodeHolder.Strategist;

	private bool InRange(int squad) => squad >= 0 && squad < _squads.Length;

	private static float Flat(Vector3 a, Vector3 b)
	{
		float x = a.X - b.X;
		float z = a.Z - b.Z;
		return MathF.Sqrt((x * x) + (z * z));
	}
}
