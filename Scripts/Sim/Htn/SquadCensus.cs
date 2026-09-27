using System;
using Godot;

namespace Gdpyr.Sim.Htn;

/// <summary>
/// One count of every squad on a <see cref="SquadBoard"/>: how many of its units are
/// alive, where they are gathered, how many are in a fight and where, which target
/// the squad focuses on, and each unit's place in its squad (docs/HTN_BOTS.md §5.2).
/// The unit domain reads it — a unit keeps pace with its squad's centroid, shoots
/// its focus, answers a squad-mate's fight, takes post k of n — and it writes the
/// one thing units report up: a squad with a unit in a fight is
/// <see cref="SquadPhase.Engaged"/>, and <see cref="SquadPhase.Moving"/> again when
/// none is. Gathering and falling back are the commander's, and left as they are.
///
/// The focus is a target a member holds — so one some unit of the side can see —
/// chosen once and kept while any member still holds it: the target the order named
/// when a member holds that; else the one already chosen; else the one most members
/// hold, then the weakest the side remembers, then the lowest id. Kept, because a
/// focus that moved to whichever target was weakest this tick would be no focus.
///
/// <see cref="Begin"/>, an <see cref="Add"/> per living unit in a squad, then
/// <see cref="End"/>. Engine-free and fixed-size: nothing allocated after
/// construction (§4.2 rule 3).
/// </summary>
public sealed class SquadCensus
{
	private readonly int[] _alive;
	private readonly Vector3[] _sum;
	private readonly int[] _engaging;
	private readonly Vector3[] _engagedSum;

	/// <summary>The chosen focus per squad, and the squad it was chosen for: a recycled slot is a new squad.</summary>
	private readonly int[] _focus;
	private readonly uint[] _focusFormedTick;

	/// <summary>The units this count found, and their squads. Packed at the front.</summary>
	private readonly ushort[] _memberUnit;
	private readonly byte[] _memberSquad;
	private int _members;

	/// <summary>The targets members hold: squad, owner and how many of its members hold it. Packed.</summary>
	private readonly byte[] _targetSquad;
	private readonly int[] _targetOwner;
	private readonly int[] _targetCount;
	private int _targets;

	public SquadCensus(int squads = SquadBoard.DefaultSquads, int members = SimConfig.MaxUnits)
	{
		squads = Math.Clamp(squads, 1, byte.MaxValue);
		members = Math.Max(members, 1);

		_alive = new int[squads];
		_sum = new Vector3[squads];
		_engaging = new int[squads];
		_engagedSum = new Vector3[squads];
		_focus = new int[squads];
		_focusFormedTick = new uint[squads];

		_memberUnit = new ushort[members];
		_memberSquad = new byte[members];
		_targetSquad = new byte[members];
		_targetOwner = new int[members];
		_targetCount = new int[members];
	}

	public int Capacity => _alive.Length;

	/// <summary>Units refused because the count was full. Anything but 0 is a sizing bug.</summary>
	public int Overflows { get; private set; }

	/// <summary>Starts a count. The chosen focus of each squad is kept.</summary>
	public void Begin()
	{
		Array.Clear(_alive);
		Array.Clear(_sum);
		Array.Clear(_engaging);
		Array.Clear(_engagedSum);
		_members = 0;
		_targets = 0;
	}

	/// <summary>
	/// One living unit of <paramref name="squad"/>. <paramref name="engaging"/> is
	/// true while it is shooting; <paramref name="targetOwnerId"/> is what it holds,
	/// <see cref="OwnerId.None"/> for nothing.
	/// </summary>
	public void Add(int squad, ushort unitId, Vector3 position, bool engaging, int targetOwnerId)
	{
		if (squad < 0 || squad >= _alive.Length || unitId == 0)
		{
			return;
		}

		if (_members == _memberUnit.Length)
		{
			Overflows++;
			return;
		}

		_memberUnit[_members] = unitId;
		_memberSquad[_members] = (byte)squad;
		_members++;

		_alive[squad]++;
		_sum[squad] += position;
		if (engaging)
		{
			_engaging[squad]++;
			_engagedSum[squad] += position;
		}

		if (targetOwnerId == OwnerId.None)
		{
			return;
		}

		for (int i = 0; i < _targets; i++)
		{
			if (_targetSquad[i] == squad && _targetOwner[i] == targetOwnerId)
			{
				_targetCount[i]++;
				return;
			}
		}

		// There are never more targets held than units counted, so this fits.
		_targetSquad[_targets] = (byte)squad;
		_targetOwner[_targets] = targetOwnerId;
		_targetCount[_targets] = 1;
		_targets++;
	}

	/// <summary>
	/// Ends the count: chooses each squad's focus and moves its phase between moving
	/// and engaged. <paramref name="memory"/> is the side's, for how weak a target is;
	/// it may be null.
	/// </summary>
	public void End(SquadBoard board, ContactMemory memory)
	{
		for (int s = 0; s < _alive.Length; s++)
		{
			Squad squad = board?.At(s) ?? default;
			if (!squad.Active)
			{
				_focus[s] = OwnerId.None;
				continue;
			}

			if (_focusFormedTick[s] != squad.FormedTick)
			{
				_focus[s] = OwnerId.None;
				_focusFormedTick[s] = squad.FormedTick;
			}

			_focus[s] = PickFocus(s, squad.FocusOwnerId, _focus[s], memory);

			if (squad.Phase is SquadPhase.Moving or SquadPhase.Engaged)
			{
				board.SetPhase(s, _engaging[s] > 0 ? SquadPhase.Engaged : SquadPhase.Moving);
			}
		}
	}

	public int AliveIn(int squad) => InRange(squad) ? _alive[squad] : 0;

	/// <summary>Where the squad's living units are gathered; the origin for a squad with none.</summary>
	public Vector3 CentroidOf(int squad) => AliveIn(squad) > 0 ? _sum[squad] / _alive[squad] : Vector3.Zero;

	public int EngagingIn(int squad) => InRange(squad) ? _engaging[squad] : 0;

	/// <summary>Where its units in a fight are gathered; the origin for a squad with none.</summary>
	public Vector3 EngagedCentroidOf(int squad) =>
		EngagingIn(squad) > 0 ? _engagedSum[squad] / _engaging[squad] : Vector3.Zero;

	/// <summary>The target the squad focuses on, as an <see cref="OwnerId"/>; <see cref="OwnerId.None"/> for none.</summary>
	public int FocusOf(int squad) => InRange(squad) ? _focus[squad] : OwnerId.None;

	/// <summary>
	/// A unit's place in its squad, 0 to <see cref="AliveIn"/> − 1: how many of the
	/// squad's counted units have a lower id. The same on every tick the squad does
	/// not change, whatever order the units were counted in. -1 for a unit not counted.
	/// </summary>
	public int Rank(int squad, ushort unitId)
	{
		bool found = false;
		int rank = 0;
		for (int i = 0; i < _members; i++)
		{
			if (_memberSquad[i] != squad)
			{
				continue;
			}

			if (_memberUnit[i] == unitId)
			{
				found = true;
			}
			else if (_memberUnit[i] < unitId)
			{
				rank++;
			}
		}

		return found ? rank : -1;
	}

	private bool InRange(int squad) => squad >= 0 && squad < _alive.Length;

	private int PickFocus(int squad, int named, int current, ContactMemory memory)
	{
		if (named != OwnerId.None && Holders(squad, named) > 0)
		{
			return named;
		}

		if (current != OwnerId.None && Holders(squad, current) > 0)
		{
			return current;
		}

		int best = OwnerId.None;
		int bestCount = 0;
		float bestHealth = float.MaxValue;
		for (int i = 0; i < _targets; i++)
		{
			if (_targetSquad[i] != squad)
			{
				continue;
			}

			int owner = _targetOwner[i];
			int count = _targetCount[i];
			float health = HealthOf(memory, owner);
			if (count > bestCount
				|| (count == bestCount && (health < bestHealth || (health == bestHealth && owner < best))))
			{
				best = owner;
				bestCount = count;
				bestHealth = health;
			}
		}

		return best;
	}

	private int Holders(int squad, int owner)
	{
		for (int i = 0; i < _targets; i++)
		{
			if (_targetSquad[i] == squad && _targetOwner[i] == owner)
			{
				return _targetCount[i];
			}
		}

		return 0;
	}

	/// <summary>What the side remembers of its health; full for one it does not remember.</summary>
	private static float HealthOf(ContactMemory memory, int owner)
	{
		int index = memory?.IndexOf(owner) ?? -1;
		return index >= 0 ? memory.At(index).HealthFraction : 1f;
	}
}

/// <summary>
/// Where a defending squad stands (docs/HTN_BOTS.md §5.2, "hold post"): post k of n
/// on a ring round the anchor, the first on the bearing of the threat, the rest
/// evenly round from it. A squad of one stands on the anchor, as a defending unit
/// always has.
/// </summary>
public static class DefendPosts
{
	/// <summary>
	/// The ring's radius for <paramref name="count"/> units: wide enough that
	/// neighbours stand <paramref name="spacingMeters"/> apart, no less than
	/// <paramref name="minMeters"/> and no more than <paramref name="maxMeters"/>,
	/// which wins when the two disagree. 0 for one unit.
	/// </summary>
	public static float Radius(int count, float spacingMeters, float minMeters, float maxMeters)
	{
		if (count <= 1)
		{
			return 0f;
		}

		float ring = count * MathF.Max(spacingMeters, 0f) / MathF.Tau;
		return MathF.Min(MathF.Max(ring, minMeters), MathF.Max(maxMeters, 0f));
	}

	/// <summary>Post <paramref name="index"/> of <paramref name="count"/>, level with the anchor.</summary>
	public static Vector3 Post(Vector3 anchor, int index, int count, float bearingRadians, float radiusMeters)
	{
		if (count <= 1 || radiusMeters <= 0f)
		{
			return anchor;
		}

		float angle = bearingRadians + (MathF.Tau * Math.Clamp(index, 0, count - 1) / count);
		return anchor + (new Vector3(MathF.Sin(angle), 0f, MathF.Cos(angle)) * radiusMeters);
	}

	/// <summary>
	/// The bearing from <paramref name="from"/> to <paramref name="towards"/> on the
	/// ground, rounded to one of <paramref name="sectors"/>: a threat that walks a few
	/// metres does not send the whole ring round to new posts. 0 when the two points
	/// are one.
	/// </summary>
	public static float Bearing(Vector3 from, Vector3 towards, int sectors = 8)
	{
		float x = towards.X - from.X;
		float z = towards.Z - from.Z;
		if ((x * x) + (z * z) < 0.0001f)
		{
			return 0f;
		}

		float step = MathF.Tau / Math.Max(sectors, 1);
		float bearing = MathF.Round(MathF.Atan2(x, z) / step) * step;
		return bearing < 0f ? bearing + MathF.Tau : bearing;
	}
}
