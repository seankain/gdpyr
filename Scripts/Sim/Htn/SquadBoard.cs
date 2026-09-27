using System;
using Godot;

namespace Gdpyr.Sim.Htn;

/// <summary>What a squad is for: the five tasks of docs/HTN_BOTS.md §5.4, or none.</summary>
public enum SquadMission : byte
{
	/// <summary>An order with no mission behind it: a plain move.</summary>
	None = 0,

	DefendZone = 1,

	Recon = 2,

	Attack = 3,

	Retreat = 4,

	Resupply = 5,
}

/// <summary>Where a squad is in carrying its mission out (docs/HTN_BOTS.md §4.3, §5.2).</summary>
public enum SquadPhase : byte
{
	/// <summary>Assembling at a staging point; its units wait for each other.</summary>
	Gathering = 0,

	Moving = 1,

	Engaged = 2,

	FallingBack = 3,
}

/// <summary>One squad on a <see cref="SquadBoard"/>.</summary>
public struct Squad
{
	public bool Active;

	/// <summary>The peer whose order formed it: a person, a computer strategist or an agent's seat.</summary>
	public int Issuer;

	public OrderKind Order;

	public SquadMission Mission;

	/// <summary>Where the order sends it.</summary>
	public Vector3 Target;

	/// <summary>What it shoots at first, as an <see cref="OwnerId"/>; <see cref="OwnerId.None"/> for whatever it finds.</summary>
	public int FocusOwnerId;

	public SquadPhase Phase;

	/// <summary>Where it assembles before it strikes, and falls back to (§5.2, §5.3). Only with <see cref="HasStaging"/>.</summary>
	public Vector3 StagingPoint;

	public bool HasStaging;

	/// <summary>Units in it now.</summary>
	public int Members;

	/// <summary>
	/// What its units were worth when they joined it (<see cref="ForceRatio.Strength"/>):
	/// what the commander's Retreat measures what is left against (§5.3).
	/// </summary>
	public float StrengthAtFormation;

	public uint FormedTick;

	/// <summary>The last tick an order formed it or added to it. The least recent is the one a full board recycles.</summary>
	public uint OrderedTick;
}

/// <summary>
/// The strategist side's squads (docs/HTN_BOTS.md §4.3): which units are in which,
/// what each is for, where it is going and what it focuses on. Read by the unit
/// HTNs from H3, written by a computer strategist's commander from H4 — and, from
/// H1, by every order: the units one order names are one squad, whoever gave it, so a human's
/// selection gets the same cohesion and focus fire a bot's does, and the commander
/// can tell a human's units from its own (D1).
///
/// One order is one squad, and the same order again is the same squad: a batch
/// from the same issuer, of the same kind, with the same focus, sent within
/// <see cref="MergeRadiusMeters"/> of an existing squad's target joins it. That is
/// what keeps a computer strategist that reinforces its garrison one unit at a time
/// to one garrison.
///
/// A unit is in at most one squad. It leaves when it is given any other order, is
/// told to stop, is sent to build, or dies; a squad with nobody left in it is gone.
/// A full board recycles the squad ordered least recently, whose units carry on
/// with their orders and no squad (<see cref="Evictions"/>).
///
/// Engine-free and fixed-size: a membership table of at most
/// <see cref="SimConfig.MaxUnits"/> rows, searched linearly, and nothing allocated
/// after construction (§4.2 rule 3). Server-only.
/// </summary>
public sealed class SquadBoard
{
	/// <summary>Up to eight squads (§4.3).</summary>
	public const int DefaultSquads = 8;

	/// <summary>How close two orders' targets are to be the same order. About a unit's arrival radius.</summary>
	public const float MergeRadiusMeters = 2f;

	private readonly Squad[] _squads;

	/// <summary>Membership rows: a unit, and the squad it is in. Packed at the front.</summary>
	private readonly ushort[] _memberUnit;
	private readonly byte[] _memberSquad;

	public SquadBoard(int squads = DefaultSquads, int members = SimConfig.MaxUnits)
	{
		_squads = new Squad[Math.Clamp(squads, 1, byte.MaxValue)];
		_memberUnit = new ushort[Math.Max(members, 1)];
		_memberSquad = new byte[_memberUnit.Length];
	}

	/// <summary>Squads the board can hold.</summary>
	public int Capacity => _squads.Length;

	/// <summary>Squads with anybody in them.</summary>
	public int Count { get; private set; }

	/// <summary>Units in any squad.</summary>
	public int MemberCount { get; private set; }

	/// <summary>Squads recycled to make room for a new one.</summary>
	public int Evictions { get; private set; }

	/// <summary>Units refused a squad because the membership table was full. Anything but 0 is a sizing bug.</summary>
	public int Overflows { get; private set; }

	public Squad At(int squad) => squad >= 0 && squad < _squads.Length ? _squads[squad] : default;

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
		for (int row = 0; row < MemberCount && count < into.Length; row++)
		{
			if (_memberSquad[row] == squad)
			{
				into[count++] = _memberUnit[row];
			}
		}

		return count;
	}

	/// <summary>The mission an order implies when nobody has said otherwise.</summary>
	public static SquadMission MissionFor(OrderKind order) => order switch
	{
		OrderKind.Attack => SquadMission.Attack,
		OrderKind.Defend => SquadMission.DefendZone,
		OrderKind.Patrol => SquadMission.Recon,
		_ => SquadMission.None,
	};

	/// <summary>
	/// Puts the units one order named into one squad — the matching squad if there
	/// is one, else a new one — and returns it, or -1 when nobody was put anywhere.
	/// Each unit leaves whatever squad it was in; a unit already in the squad it is
	/// being put in stays, and adds nothing to its strength a second time.
	/// <paramref name="strengths"/> runs beside <paramref name="units"/>; a missing
	/// one counts for nothing.
	/// </summary>
	public int Form(uint tick, int issuer, OrderKind order, Vector3 target, int focusOwnerId,
		ReadOnlySpan<ushort> units, ReadOnlySpan<float> strengths)
	{
		// Checked before a squad is opened, because opening one on a full board
		// recycles another.
		if (!NamesAnybody(units))
		{
			return -1;
		}

		int squad = Matching(issuer, order, target, focusOwnerId);
		if (squad < 0)
		{
			squad = Open(tick, issuer, order, target, focusOwnerId);
		}

		for (int i = 0; i < units.Length; i++)
		{
			ushort unit = units[i];
			if (unit == 0)
			{
				continue;
			}

			int row = RowOf(unit);
			if (row >= 0 && _memberSquad[row] == squad)
			{
				continue;
			}

			if (row >= 0)
			{
				RemoveRow(row);
			}

			if (MemberCount == _memberUnit.Length)
			{
				Overflows++;
				continue;
			}

			_memberUnit[MemberCount] = unit;
			_memberSquad[MemberCount] = (byte)squad;
			MemberCount++;

			_squads[squad].Members++;
			_squads[squad].StrengthAtFormation += i < strengths.Length ? MathF.Max(strengths[i], 0f) : 0f;
		}

		if (_squads[squad].Members == 0)
		{
			Close(squad);
			return -1;
		}

		_squads[squad].OrderedTick = tick;
		return squad;
	}

	/// <summary>
	/// Sets where a squad is in its mission. The unit census moves a squad between
	/// <see cref="SquadPhase.Moving"/> and <see cref="SquadPhase.Engaged"/> from what
	/// its units are doing (H3); gathering and falling back are the commander's (H4).
	/// Returns false for a squad that is not on the board.
	/// </summary>
	public bool SetPhase(int squad, SquadPhase phase)
	{
		if (squad < 0 || squad >= _squads.Length || !_squads[squad].Active)
		{
			return false;
		}

		_squads[squad].Phase = phase;
		return true;
	}

	/// <summary>
	/// Says what a squad is for, where the order alone does not: a commander's move
	/// order is a retreat or a refill, its defend order a reinforcement (H4). Returns
	/// false for a squad that is not on the board.
	/// </summary>
	public bool SetMission(int squad, SquadMission mission)
	{
		if (squad < 0 || squad >= _squads.Length || !_squads[squad].Active)
		{
			return false;
		}

		_squads[squad].Mission = mission;
		return true;
	}

	/// <summary>
	/// Gives a squad a staging point and sets it gathering there: its units wait for
	/// each other at the point (§5.2, "wait for squad"). Returns false for a squad
	/// that is not on the board.
	/// </summary>
	public bool Stage(int squad, Vector3 point)
	{
		if (!SetPhase(squad, SquadPhase.Gathering))
		{
			return false;
		}

		_squads[squad].StagingPoint = point;
		_squads[squad].HasStaging = true;
		return true;
	}

	/// <summary>Takes a unit out of its squad. Returns false when it was in none.</summary>
	public bool Leave(ushort unitId)
	{
		int row = RowOf(unitId);
		if (row < 0)
		{
			return false;
		}

		RemoveRow(row);
		return true;
	}

	/// <summary>Disbands everything and zeroes the counters, between rounds.</summary>
	public void Clear()
	{
		Array.Clear(_squads);
		Count = 0;
		MemberCount = 0;
		Evictions = 0;
		Overflows = 0;
	}

	private static bool NamesAnybody(ReadOnlySpan<ushort> units)
	{
		for (int i = 0; i < units.Length; i++)
		{
			if (units[i] != 0)
			{
				return true;
			}
		}

		return false;
	}

	private int Matching(int issuer, OrderKind order, Vector3 target, int focusOwnerId)
	{
		for (int i = 0; i < _squads.Length; i++)
		{
			ref Squad squad = ref _squads[i];
			if (squad.Active && squad.Issuer == issuer && squad.Order == order && squad.FocusOwnerId == focusOwnerId
				&& squad.Target.DistanceSquaredTo(target) <= MergeRadiusMeters * MergeRadiusMeters)
			{
				return i;
			}
		}

		return -1;
	}

	/// <summary>A new, empty squad: the first free slot, else the least recently ordered squad's.</summary>
	private int Open(uint tick, int issuer, OrderKind order, Vector3 target, int focusOwnerId)
	{
		int slot = -1;
		for (int i = 0; i < _squads.Length && slot < 0; i++)
		{
			if (!_squads[i].Active)
			{
				slot = i;
			}
		}

		if (slot < 0)
		{
			slot = 0;
			for (int i = 1; i < _squads.Length; i++)
			{
				if (_squads[i].OrderedTick < _squads[slot].OrderedTick)
				{
					slot = i;
				}
			}

			Disband(slot);
			Evictions++;
		}

		_squads[slot] = new Squad
		{
			Active = true,
			Issuer = issuer,
			Order = order,
			Mission = MissionFor(order),
			Target = target,
			FocusOwnerId = focusOwnerId,
			Phase = SquadPhase.Moving,
			FormedTick = tick,
			OrderedTick = tick,
		};
		Count++;
		return slot;
	}

	/// <summary>Takes every unit out of a squad, which closes it.</summary>
	private void Disband(int squad)
	{
		for (int row = MemberCount - 1; row >= 0; row--)
		{
			if (_memberSquad[row] == squad)
			{
				RemoveRow(row);
			}
		}

		// A squad with nobody in it is already closed by the last removal; one opened
		// and never filled is closed here.
		if (_squads[squad].Active)
		{
			Close(squad);
		}
	}

	private void Close(int squad)
	{
		_squads[squad] = default;
		Count--;
	}

	private int RowOf(ushort unitId)
	{
		for (int row = 0; row < MemberCount; row++)
		{
			if (_memberUnit[row] == unitId)
			{
				return row;
			}
		}

		return -1;
	}

	/// <summary>Moves the last row into the hole, and closes the squad if that was its last unit.</summary>
	private void RemoveRow(int row)
	{
		int squad = _memberSquad[row];
		int last = MemberCount - 1;

		_memberUnit[row] = _memberUnit[last];
		_memberSquad[row] = _memberSquad[last];
		_memberUnit[last] = 0;
		_memberSquad[last] = 0;
		MemberCount--;

		if (--_squads[squad].Members == 0)
		{
			Close(squad);
		}
	}
}
