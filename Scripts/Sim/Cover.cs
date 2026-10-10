using System;
using Godot;

namespace Gdpyr.Sim;

/// <summary>
/// Where a seeker stands against one threat, as one byte a planner can hold
/// (docs/COVER.md §3).
/// </summary>
public enum CoverState : byte
{
	/// <summary>No spot against this threat within reach.</summary>
	None = 0,

	/// <summary>A spot within reach that it is not standing in.</summary>
	Near = 1,

	/// <summary>Standing in its spot, and the box beside it still hides it from the threat.</summary>
	In = 2,
}

/// <summary>
/// One search for cover: who is looking, from where, against what, and what the
/// spot has to do for them (docs/COVER.md §2). Heights are above the feet of the
/// body they belong to, so one query serves a rifleman, a ground player and a
/// builder alike.
/// </summary>
public struct CoverQuery
{
	/// <summary>The seeker's feet.</summary>
	public Vector3 From;

	/// <summary>The threat's feet.</summary>
	public Vector3 Threat;

	/// <summary>Where the threat's sight and fire leave from, above its feet.</summary>
	public float ThreatEyeMeters;

	/// <summary>Where the seeker would aim at the threat, above its feet: the chest.</summary>
	public float ThreatAimMeters;

	/// <summary>How far from <see cref="From"/> a spot may be, measured flat.</summary>
	public float SearchMeters;

	/// <summary>The seeker's capsule radius: how far off the box's face it stands.</summary>
	public float BodyRadiusMeters;

	/// <summary>
	/// The point on the seeker's body the box must hide, above its feet — the
	/// middle of its hitbox, which is what a unit, a bot and a barracks gun aim at.
	/// </summary>
	public float ProtectMeters;

	/// <summary>Where the seeker fires from, above its feet.</summary>
	public float EyeMeters;

	/// <summary>Only spots it can shoot from: the box is low enough to fire over.</summary>
	public bool MustFire;

	/// <summary>A spot nearer the threat than this is not taken.</summary>
	public float MinThreatMeters;

	/// <summary>A spot further from the threat than this is not taken. Zero for no limit.</summary>
	public float MaxThreatMeters;
}

/// <summary>A place to stand behind one cover box.</summary>
public struct CoverSpot
{
	/// <summary>On the ground, at the box's base height.</summary>
	public Vector3 Position;

	/// <summary>The board index of the box it is behind.</summary>
	public int Object;

	/// <summary>The box is low enough for the seeker to fire over it.</summary>
	public bool Low;
}

/// <summary>
/// The cover on the field, and where to stand behind it (docs/COVER.md).
///
/// Cover is already physical: a box on the world layer stops rounds, bodies and
/// sight lines whoever stands behind it, and a crouched player behind a sandbag
/// wall is hidden from the rifleman in front of it (docs/NETCODE.md §10.5). What a
/// physics world does not say is where to stand so that a particular box is
/// between you and a particular threat. That is this: every box the map marks as
/// cover and every finished sandbag wall, as oriented boxes, and a search that
/// walks the faces turned away from a threat for spots the box hides a body at.
///
/// Engine-free, fixed-size and allocation-free, as the boards of docs/HTN_BOTS.md
/// §4.3 are: the engine adds the boxes, the planners ask the questions, and which
/// spot a rifleman picks is a question for <c>dotnet test</c>.
/// </summary>
public sealed class CoverBoard
{
	/// <summary>Boxes the map can mark as cover. The structure slots come after them.</summary>
	public const int LevelCapacity = 96;

	/// <summary>Lower than this hides nothing worth planning for: not a crouched player's chest.</summary>
	public const float MinHeightMeters = 0.9f;

	/// <summary>How far apart neighbouring spots along one face are: two bodies and a little air.</summary>
	public const float SlotSpacingMeters = 1.3f;

	/// <summary>Air between a body and the face it stands behind.</summary>
	public const float GapMeters = 0.2f;

	/// <summary>Spots per face, whatever its length.</summary>
	public const int MaxSlotsPerFace = 8;

	/// <summary>A spot this close to one already claimed is the same spot.</summary>
	public const float ClaimMeters = 1.1f;

	/// <summary>How far a box may be from a body and still count as what it is standing behind.</summary>
	public const float AdjacentMeters = 1.5f;

	/// <summary>
	/// How much clear air the seeker's line of fire needs over a box's top. Rounds
	/// leave the eye with an error cone, and a line that grazes the top of the wall
	/// spends half of them on it.
	/// </summary>
	public const float FireClearanceMeters = 0.15f;

	/// <summary>
	/// What a metre of falling back from the threat costs against a metre of
	/// walking: a spot behind the seeker is worth taking, but not one far behind it.
	/// </summary>
	public const float RetreatWeight = 0.5f;

	private readonly Vector3[] _base = new Vector3[Capacity];
	private readonly float[] _yaw = new float[Capacity];
	private readonly Vector3[] _size = new Vector3[Capacity];
	private readonly bool[] _active = new bool[Capacity];

	/// <summary>Every box the board can hold: the map's, then one per structure slot.</summary>
	public const int Capacity = LevelCapacity + SimConfig.MaxStructures;

	/// <summary>Map boxes added since the last <see cref="ClearLevel"/>.</summary>
	public int LevelCount { get; private set; }

	/// <summary>Map boxes refused for want of room. 0 when the board is sized right.</summary>
	public int Overflow { get; private set; }

	/// <summary>Boxes on the board now, the map's and the structures'.</summary>
	public int Count { get; private set; }

	public bool IsActive(int index) => index >= 0 && index < Capacity && _active[index];

	/// <summary>The box's footprint on the ground.</summary>
	public Footprint FootprintOf(int index) =>
		new(_base[index], _yaw[index], _size[index].X * 0.5f, _size[index].Z * 0.5f);

	/// <summary>The box's height above its base.</summary>
	public float HeightOf(int index) => _size[index].Y;

	/// <summary>The middle of the box's base: where it stands.</summary>
	public Vector3 BaseOf(int index) => _base[index];

	public float YawOf(int index) => _yaw[index];

	/// <summary>
	/// A box of the map's, by its centre, its turn about the vertical and its whole
	/// size — what a <c>CSGBox3D</c> says about itself. Returns its index, or -1 for
	/// one too low to be cover or a board with no room.
	/// </summary>
	public int AddLevel(Vector3 center, float yaw, Vector3 size)
	{
		if (!Valid(size))
		{
			return -1;
		}

		if (LevelCount >= LevelCapacity)
		{
			Overflow++;
			return -1;
		}

		int index = LevelCount++;
		Set(index, center - (Vector3.Up * (size.Y * 0.5f)), yaw, size);
		return index;
	}

	/// <summary>
	/// A finished structure in <paramref name="slot"/>, standing on
	/// <paramref name="basePoint"/>: a sandbag wall, by its body box. Replaces
	/// whatever the slot held.
	/// </summary>
	public void SetStructure(int slot, Vector3 basePoint, float yaw, Vector3 size)
	{
		if (slot < 0 || slot >= SimConfig.MaxStructures)
		{
			return;
		}

		int index = LevelCapacity + slot;
		if (!Valid(size))
		{
			ClearIndex(index);
			return;
		}

		Set(index, basePoint, yaw, size);
	}

	/// <summary>Takes a structure slot's box off the board: knocked down, or never finished.</summary>
	public void ClearStructure(int slot)
	{
		if (slot >= 0 && slot < SimConfig.MaxStructures)
		{
			ClearIndex(LevelCapacity + slot);
		}
	}

	/// <summary>The board index a structure slot's box lives at.</summary>
	public static int StructureIndex(int slot) => LevelCapacity + slot;

	/// <summary>Forgets the map's boxes: a new map.</summary>
	public void ClearLevel()
	{
		for (int i = 0; i < LevelCapacity; i++)
		{
			ClearIndex(i);
		}

		LevelCount = 0;
		Overflow = 0;
	}

	public void Clear()
	{
		for (int i = 0; i < Capacity; i++)
		{
			ClearIndex(i);
		}

		LevelCount = 0;
		Overflow = 0;
	}

	/// <summary>Whether box <paramref name="index"/> stands across the segment from <paramref name="from"/> to <paramref name="to"/>.</summary>
	public bool Occludes(int index, Vector3 from, Vector3 to, float grow = 0f)
	{
		if (!IsActive(index))
		{
			return false;
		}

		var pose = new StructurePose(_base[index], _yaw[index]);
		StructureBox box = StructureBox.Standing(_size[index].X, _size[index].Z, _size[index].Y);
		return box.SegmentIntersects(pose.ToLocal(from), pose.ToLocal(to), grow, out _);
	}

	/// <summary>
	/// Whether a body standing at <paramref name="position"/> is behind cover from
	/// the query's threat: a box within <see cref="AdjacentMeters"/> of it hides the
	/// point <see cref="CoverQuery.ProtectMeters"/> up its body from the threat's eye.
	/// </summary>
	public bool Protects(Vector3 position, in CoverQuery query) => ProtectingBox(position, query) >= 0;

	/// <summary>
	/// The box hiding a body at <paramref name="position"/> from the threat, or -1.
	///
	/// The body is measured up from the ground the box stands on, not from the
	/// height <paramref name="position"/> carries: a point snapped to the navigation
	/// mesh sits a cell above the floor, and a character's origin is wherever its
	/// collider put it. Both stand on the same ground as the box beside them.
	/// </summary>
	public int ProtectingBox(Vector3 position, in CoverQuery query)
	{
		Vector3 eye = query.Threat + (Vector3.Up * query.ThreatEyeMeters);
		float reach = AdjacentMeters + MathF.Max(query.BodyRadiusMeters, 0f);

		for (int i = 0; i < Capacity; i++)
		{
			if (!_active[i] || FootprintOf(i).DistanceTo(position) > reach)
			{
				continue;
			}

			var body = new Vector3(position.X, _base[i].Y + query.ProtectMeters, position.Z);
			if (Occludes(i, eye, body))
			{
				return i;
			}
		}

		return -1;
	}

	/// <summary>
	/// The best spot for the query that nobody has claimed: on a face turned away
	/// from the threat, within <see cref="CoverQuery.SearchMeters"/> of the seeker,
	/// clear of every other box, with the box between the threat's eye and the
	/// seeker's body and — when it has to fire — not between the seeker's eye and
	/// the threat's chest. Best is the shortest walk, with falling back from the
	/// threat charged at <see cref="RetreatWeight"/>; ties go to the lower box index
	/// and the earlier slot, so the answer is the same every time.
	/// </summary>
	public bool TryFindSpot(in CoverQuery query, ReadOnlySpan<Vector3> claimed, out CoverSpot spot) =>
		Search(query, claimed, default, out spot);

	/// <summary>
	/// Up to <paramref name="spots"/>.Length spots for one query, best first, each at
	/// least <see cref="ClaimMeters"/> from the others and from
	/// <paramref name="claimed"/>: how many bodies a place can hide from one threat.
	/// </summary>
	public int FindSpots(in CoverQuery query, ReadOnlySpan<Vector3> claimed, Span<CoverSpot> spots)
	{
		int found = 0;
		while (found < spots.Length && Search(query, claimed, spots[..found], out CoverSpot spot))
		{
			spots[found++] = spot;
		}

		return found;
	}

	private bool Search(in CoverQuery query, ReadOnlySpan<Vector3> claimed, ReadOnlySpan<CoverSpot> taken,
		out CoverSpot best)
	{
		best = default;
		best.Object = -1;
		float bestScore = float.MaxValue;

		float search = MathF.Max(query.SearchMeters, 0f);
		float body = MathF.Max(query.BodyRadiusMeters, 0f);
		float fromThreat = Flat(query.From, query.Threat);

		for (int i = 0; i < Capacity; i++)
		{
			if (!_active[i])
			{
				continue;
			}

			Footprint footprint = FootprintOf(i);
			if (footprint.DistanceTo(query.From) > search + body + GapMeters)
			{
				continue;
			}

			var pose = new StructurePose(_base[i], _yaw[i]);
			Vector3 threat = pose.ToLocal(query.Threat);
			bool low = _size[i].Y <= query.EyeMeters - FireClearanceMeters;
			if (query.MustFire && !low)
			{
				continue;
			}

			// Four faces: across X at ±half width, across Z at ±half depth. A face
			// whose outward normal points at the threat is the side the rounds arrive on.
			for (int face = 0; face < 4; face++)
			{
				bool alongX = face >= 2;
				float sign = (face & 1) == 0 ? 1f : -1f;
				float normalThreat = (alongX ? threat.Z : threat.X) * sign;
				if (normalThreat >= 0f)
				{
					continue;
				}

				float halfAcross = alongX ? _size[i].Z * 0.5f : _size[i].X * 0.5f;
				float halfAlong = alongX ? _size[i].X * 0.5f : _size[i].Z * 0.5f;
				float usable = MathF.Max((2f * halfAlong) - (2f * body), 0f);
				int slots = Math.Clamp((int)(usable / SlotSpacingMeters) + 1, 1, MaxSlotsPerFace);
				float out_ = halfAcross + body + GapMeters;

				for (int s = 0; s < slots; s++)
				{
					float along = (s - ((slots - 1) * 0.5f)) * SlotSpacingMeters;
					Vector3 local = alongX
						? new Vector3(along, 0f, sign * out_)
						: new Vector3(sign * out_, 0f, along);
					Vector3 at = pose.ToWorld(local);

					float walk = Flat(at, query.From);
					if (walk > search)
					{
						continue;
					}

					float range = Flat(at, query.Threat);
					if (range < query.MinThreatMeters || (query.MaxThreatMeters > 0f && range > query.MaxThreatMeters))
					{
						continue;
					}

					float score = walk + (RetreatWeight * MathF.Max(range - fromThreat, 0f));
					if (score >= bestScore || IsClaimed(at, claimed, taken) || IsInsideOther(i, at, body))
					{
						continue;
					}

					if (!Hides(i, at, query) || (query.MustFire && !FiresFrom(i, at, query)))
					{
						continue;
					}

					bestScore = score;
					best = new CoverSpot { Position = at, Object = i, Low = low };
				}
			}
		}

		return best.Object >= 0;
	}

	/// <summary>Box <paramref name="index"/> hides the body at <paramref name="at"/> from the threat's eye.</summary>
	private bool Hides(int index, Vector3 at, in CoverQuery query) =>
		Occludes(index, query.Threat + (Vector3.Up * query.ThreatEyeMeters), at + (Vector3.Up * query.ProtectMeters));

	/// <summary>Nothing of box <paramref name="index"/> stands between the seeker's eye at <paramref name="at"/> and the threat's chest.</summary>
	private bool FiresFrom(int index, Vector3 at, in CoverQuery query) =>
		!Occludes(index, at + (Vector3.Up * query.EyeMeters), query.Threat + (Vector3.Up * query.ThreatAimMeters),
			FireClearanceMeters);

	/// <summary>Whether a body of <paramref name="radius"/> at <paramref name="at"/> would stand inside a box other than <paramref name="index"/>.</summary>
	private bool IsInsideOther(int index, Vector3 at, float radius)
	{
		for (int j = 0; j < Capacity; j++)
		{
			if (j != index && _active[j] && FootprintOf(j).Contains(at, radius))
			{
				return true;
			}
		}

		return false;
	}

	private static bool IsClaimed(Vector3 at, ReadOnlySpan<Vector3> claimed, ReadOnlySpan<CoverSpot> taken)
	{
		const float claim = ClaimMeters * ClaimMeters;
		for (int c = 0; c < claimed.Length; c++)
		{
			if (FlatSquared(at, claimed[c]) < claim)
			{
				return true;
			}
		}

		for (int c = 0; c < taken.Length; c++)
		{
			if (FlatSquared(at, taken[c].Position) < claim)
			{
				return true;
			}
		}

		return false;
	}

	private static bool Valid(Vector3 size) =>
		float.IsFinite(size.X) && float.IsFinite(size.Y) && float.IsFinite(size.Z)
		&& size.X > 0f && size.Z > 0f && size.Y >= MinHeightMeters;

	private void Set(int index, Vector3 basePoint, float yaw, Vector3 size)
	{
		if (!_active[index])
		{
			Count++;
		}

		_active[index] = true;
		_base[index] = basePoint;
		_yaw[index] = yaw;
		_size[index] = size;
	}

	private void ClearIndex(int index)
	{
		if (_active[index])
		{
			Count--;
		}

		_active[index] = false;
	}

	private static float Flat(Vector3 a, Vector3 b) => MathF.Sqrt(FlatSquared(a, b));

	private static float FlatSquared(Vector3 a, Vector3 b)
	{
		float x = a.X - b.X;
		float z = a.Z - b.Z;
		return (x * x) + (z * z);
	}
}

/// <summary>
/// The band a seeker's cover is in, with the hysteresis every banded fact has
/// (docs/HTN_BOTS.md §9, plan thrash): in its spot once within
/// <see cref="ArriveMeters"/> of it, held until it is <see cref="LeaveMeters"/> away,
/// and only while the box still hides it.
/// </summary>
public static class CoverBands
{
	/// <summary>Close enough to the spot to count as in it: inside a unit's arrival radius.</summary>
	public const float ArriveMeters = 1.0f;

	/// <summary>How far a body in its spot may be shoved before it is out of it.</summary>
	public const float LeaveMeters = 1.6f;

	public static CoverState Band(bool hasSpot, float spotMeters, bool hidden, CoverState previous)
	{
		if (!hasSpot)
		{
			return CoverState.None;
		}

		float reach = previous == CoverState.In ? LeaveMeters : ArriveMeters;
		return hidden && spotMeters <= reach ? CoverState.In : CoverState.Near;
	}
}

/// <summary>
/// The spots one side's seekers have taken, by owner slot, so two of them do not
/// walk to the same place behind the same wall (docs/COVER.md §2). Fixed-size;
/// gathering allocates nothing.
/// </summary>
public sealed class CoverClaims
{
	private readonly Vector3[] _spots;
	private readonly bool[] _held;

	public CoverClaims(int capacity)
	{
		_spots = new Vector3[Math.Max(capacity, 0)];
		_held = new bool[_spots.Length];
	}

	public int Capacity => _spots.Length;

	public void Claim(int owner, Vector3 spot)
	{
		if (owner >= 0 && owner < _spots.Length)
		{
			_spots[owner] = spot;
			_held[owner] = true;
		}
	}

	public void Release(int owner)
	{
		if (owner >= 0 && owner < _held.Length)
		{
			_held[owner] = false;
		}
	}

	public bool TryGet(int owner, out Vector3 spot)
	{
		bool held = owner >= 0 && owner < _held.Length && _held[owner];
		spot = held ? _spots[owner] : Vector3.Zero;
		return held;
	}

	/// <summary>Every spot held by somebody other than <paramref name="except"/>, into <paramref name="into"/>.</summary>
	public int Gather(int except, Span<Vector3> into)
	{
		int count = 0;
		for (int i = 0; i < _held.Length && count < into.Length; i++)
		{
			if (_held[i] && i != except)
			{
				into[count++] = _spots[i];
			}
		}

		return count;
	}

	public void Clear() => Array.Clear(_held);
}

/// <summary>
/// How much of a body has to show over cover before it is aimed at by its head
/// (docs/COVER.md §3). A crouched player's eye is a few centimetres under a sandbag
/// wall's top; a line that just clears it is a line to the top of a skull, and
/// rounds aimed there clip the wall or go over. So a head counts as showing only
/// when a point <see cref="HeadMeters"/> below the eye can be seen as well.
/// </summary>
public static class Exposure
{
	public const float HeadMeters = 0.2f;

	/// <summary>The point under <paramref name="eye"/> a shooter has to see for the head to count as showing.</summary>
	public static Vector3 Below(Vector3 eye) => eye - (Vector3.Up * HeadMeters);
}
