using System;
using Godot;

namespace Gdpyr.Sim;

/// <summary>
/// The three things a builder can put up, as the ids the structure catalog is
/// indexed by (<c>StructureCatalog</c>). Written down here rather than only in the
/// catalog because the computer strategist's plan is engine-free and has to name
/// them, and because a structure's kind is one byte on the wire: appending is
/// safe, reordering is a wire break (docs/NETCODE.md §10.5).
/// </summary>
public static class StructureKinds
{
	/// <summary>A concrete box with a gun in it. Shoots whoever it can see; shrugs off rifle fire.</summary>
	public const byte Pillbox = 0;

	/// <summary>Waist-high cover. No gun and no eyes: it stops rounds and bodies, and that is all.</summary>
	public const byte SandbagWall = 1;

	/// <summary>A marksman on a platform. Sees further than anything else the strategist owns.</summary>
	public const byte SniperTower = 2;

	public const int Count = 3;

	public static bool IsValid(byte kind) => kind < Count;
}

/// <summary>
/// One box of a structure's body, in the structure's own frame: an offset from the
/// point it stands on, turned with it about the vertical.
///
/// Boxes rather than capsules, because a wall four metres long is not a capsule,
/// and yaw-only because nothing here is built on a slope.
/// </summary>
public readonly struct StructureBox
{
	/// <summary>The box's centre, from the structure's base point, before the structure's yaw.</summary>
	public readonly Vector3 Center;

	public readonly Vector3 HalfExtents;

	public StructureBox(Vector3 center, Vector3 halfExtents)
	{
		Center = center;
		HalfExtents = new Vector3(MathF.Max(halfExtents.X, 0f), MathF.Max(halfExtents.Y, 0f),
			MathF.Max(halfExtents.Z, 0f));
	}

	/// <summary>A box standing on the ground (or on <paramref name="elevation"/>), centred on the base point.</summary>
	public static StructureBox Standing(float width, float depth, float height, float elevation = 0f) =>
		new(new Vector3(0f, elevation + (height * 0.5f), 0f), new Vector3(width * 0.5f, height * 0.5f, depth * 0.5f));

	public bool IsEmpty => HalfExtents.X <= 0f || HalfExtents.Y <= 0f || HalfExtents.Z <= 0f;

	/// <summary>The same box squashed towards the ground by <paramref name="fraction"/>, as a half-built one is.</summary>
	public StructureBox Raised(float fraction) =>
		new(new Vector3(Center.X, Center.Y * fraction, Center.Z),
			new Vector3(HalfExtents.X, HalfExtents.Y * fraction, HalfExtents.Z));

	/// <summary>
	/// Where a segment, already in the structure's frame, first enters this box
	/// grown by <paramref name="grow"/> on every side. Slab test: the entry is the
	/// latest of the three per-axis entries and the exit the earliest of the exits.
	/// A segment that starts inside enters at zero.
	/// </summary>
	public bool SegmentIntersects(Vector3 from, Vector3 to, float grow, out float t)
	{
		t = 0f;
		if (IsEmpty)
		{
			return false;
		}

		Vector3 min = Center - HalfExtents - (Vector3.One * grow);
		Vector3 max = Center + HalfExtents + (Vector3.One * grow);
		Vector3 d = to - from;

		float enter = 0f;
		float exit = 1f;

		for (int axis = 0; axis < 3; axis++)
		{
			float origin = from[axis];
			float delta = d[axis];

			if (MathF.Abs(delta) < 1e-7f)
			{
				if (origin < min[axis] || origin > max[axis])
				{
					return false;
				}
				continue;
			}

			float a = (min[axis] - origin) / delta;
			float b = (max[axis] - origin) / delta;
			if (a > b)
			{
				(a, b) = (b, a);
			}

			enter = MathF.Max(enter, a);
			exit = MathF.Min(exit, b);
			if (enter > exit)
			{
				return false;
			}
		}

		t = enter;
		return true;
	}

	/// <summary>How far a point, in the structure's frame, is from this box. Zero inside it.</summary>
	public float DistanceTo(Vector3 local)
	{
		if (IsEmpty)
		{
			return float.MaxValue;
		}

		Vector3 min = Center - HalfExtents;
		Vector3 max = Center + HalfExtents;
		var nearest = new Vector3(
			Math.Clamp(local.X, min.X, max.X),
			Math.Clamp(local.Y, min.Y, max.Y),
			Math.Clamp(local.Z, min.Z, max.Z));
		return local.DistanceTo(nearest);
	}
}

/// <summary>
/// The whole of a structure's body: a main box and, for the tower, a second one on
/// top of it. The same boxes are the collider every peer builds, the volume a
/// round is tested against and the footprint placement is checked with, so they
/// cannot disagree about where the structure is.
/// </summary>
public readonly struct StructureShape
{
	public readonly StructureBox Body;

	/// <summary>Empty for anything that is one box.</summary>
	public readonly StructureBox Top;

	public StructureShape(in StructureBox body, in StructureBox top = default)
	{
		Body = body;
		Top = top;
	}

	public bool HasTop => !Top.IsEmpty;

	/// <summary>Half the footprint along the structure's own X: across the front of a wall.</summary>
	public float HalfWidth => MathF.Max(Extent(Body, 0), HasTop ? Extent(Top, 0) : 0f);

	/// <summary>Half the footprint along the structure's own Z: front to back.</summary>
	public float HalfDepth => MathF.Max(Extent(Body, 2), HasTop ? Extent(Top, 2) : 0f);

	/// <summary>The top of the highest box, above the base point.</summary>
	public float Height => MathF.Max(Body.Center.Y + Body.HalfExtents.Y,
		HasTop ? Top.Center.Y + Top.HalfExtents.Y : 0f);

	/// <summary>
	/// The shape a site presents while it is <paramref name="fraction"/> built: every
	/// box squashed towards the ground by the same factor, which is exactly what the
	/// mesh a peer draws is scaled by. What you can see of a half-built wall is what
	/// stops a round.
	/// </summary>
	public StructureShape Raised(float fraction)
	{
		float f = Math.Clamp(fraction, 0f, 1f);
		return new StructureShape(Body.Raised(f), HasTop ? Top.Raised(f) : default);
	}

	/// <summary>The earliest point along a segment, in the structure's frame, where it enters either box.</summary>
	public bool SegmentIntersects(Vector3 from, Vector3 to, float grow, out float t)
	{
		t = float.MaxValue;
		bool hit = false;

		if (Body.SegmentIntersects(from, to, grow, out float body))
		{
			t = body;
			hit = true;
		}

		if (HasTop && Top.SegmentIntersects(from, to, grow, out float top) && top < t)
		{
			t = top;
			hit = true;
		}

		if (!hit)
		{
			t = 0f;
		}

		return hit;
	}

	/// <summary>The nearer of the two boxes to a point in the structure's frame.</summary>
	public float DistanceTo(Vector3 local) =>
		MathF.Min(Body.DistanceTo(local), HasTop ? Top.DistanceTo(local) : float.MaxValue);

	private static float Extent(in StructureBox box, int axis) =>
		box.IsEmpty ? 0f : MathF.Abs(box.Center[axis]) + box.HalfExtents[axis];
}

/// <summary>
/// Where a structure stands: the point on the ground it was put down on, and which
/// way it faces. Its frame's X runs across its front and its Z runs front to back,
/// turned by <see cref="Yaw"/> exactly as a node's <c>Rotation.Y</c> turns it.
/// </summary>
public readonly struct StructurePose
{
	public readonly Vector3 Base;
	public readonly float Yaw;

	public StructurePose(Vector3 basePoint, float yaw)
	{
		Base = basePoint;
		Yaw = yaw;
	}

	public Vector3 ToLocal(Vector3 world) => (world - Base).Rotated(Vector3.Up, -Yaw);

	public Vector3 ToWorld(Vector3 local) => Base + local.Rotated(Vector3.Up, Yaw);

	/// <summary>Where a round along <paramref name="from"/>–<paramref name="to"/> first enters the shape.</summary>
	public bool SegmentIntersects(in StructureShape shape, Vector3 from, Vector3 to, float grow, out float t) =>
		shape.SegmentIntersects(ToLocal(from), ToLocal(to), grow, out t);

	public float DistanceTo(in StructureShape shape, Vector3 world) => shape.DistanceTo(ToLocal(world));

	public Footprint FootprintOf(in StructureShape shape) =>
		new(Base, Yaw, shape.HalfWidth, shape.HalfDepth);
}

/// <summary>
/// A structure's rectangle on the ground, measured flat. What placement is checked
/// against, what "close enough to build" is measured from, and what the
/// strategist's placement ghost draws.
/// </summary>
public readonly struct Footprint
{
	/// <summary>The middle of the rectangle. Its height is carried along and ignored.</summary>
	public readonly Vector3 Center;

	public readonly float Yaw;
	public readonly float HalfWidth;
	public readonly float HalfDepth;

	public Footprint(Vector3 center, float yaw, float halfWidth, float halfDepth)
	{
		Center = center;
		Yaw = yaw;
		HalfWidth = MathF.Max(halfWidth, 0f);
		HalfDepth = MathF.Max(halfDepth, 0f);
	}

	/// <summary>The rectangle's own X axis, in the world: across the front.</summary>
	public Vector3 AxisX => new(MathF.Cos(Yaw), 0f, -MathF.Sin(Yaw));

	/// <summary>The rectangle's own Z axis, in the world: the way it faces.</summary>
	public Vector3 AxisZ => new(MathF.Sin(Yaw), 0f, MathF.Cos(Yaw));

	/// <summary>How far a point is from the rectangle, measured flat. Zero on or inside it.</summary>
	public float DistanceTo(Vector3 point)
	{
		Local(point, out float x, out float z);
		float dx = MathF.Max(MathF.Abs(x) - HalfWidth, 0f);
		float dz = MathF.Max(MathF.Abs(z) - HalfDepth, 0f);
		return MathF.Sqrt((dx * dx) + (dz * dz));
	}

	/// <summary>Whether a point is inside the rectangle grown by <paramref name="margin"/>, measured flat.</summary>
	public bool Contains(Vector3 point, float margin = 0f)
	{
		Local(point, out float x, out float z);
		return MathF.Abs(x) <= HalfWidth + margin && MathF.Abs(z) <= HalfDepth + margin;
	}

	/// <summary>
	/// Whether two rectangles overlap by more than <paramref name="allowance"/>
	/// metres. Separating axes: two rectangles are apart exactly when one of their
	/// four edge directions has their shadows apart.
	/// </summary>
	public bool Overlaps(in Footprint other, float allowance = 0f)
	{
		Vector3 offset = other.Center - Center;
		offset.Y = 0f;

		return !Separated(AxisX, offset, other, allowance)
			&& !Separated(AxisZ, offset, other, allowance)
			&& !Separated(other.AxisX, offset, other, allowance)
			&& !Separated(other.AxisZ, offset, other, allowance);
	}

	/// <summary>
	/// Where somebody coming from <paramref name="from"/> should stand to work on
	/// this rectangle: the nearest point on it, pushed straight out to
	/// <paramref name="standoff"/> from its edge — round a corner, the corner's
	/// arc. A point already on or inside it is walked out through the nearest side.
	/// </summary>
	public Vector3 ApproachPoint(Vector3 from, float standoff)
	{
		Local(from, out float x, out float z);
		float gap = MathF.Max(standoff, 0f);

		float nx = Math.Clamp(x, -HalfWidth, HalfWidth);
		float nz = Math.Clamp(z, -HalfDepth, HalfDepth);
		float ox = x - nx;
		float oz = z - nz;
		float outside = MathF.Sqrt((ox * ox) + (oz * oz));

		if (outside > 1e-4f)
		{
			x = nx + (ox / outside * gap);
			z = nz + (oz / outside * gap);
		}
		else if (HalfWidth - MathF.Abs(x) < HalfDepth - MathF.Abs(z))
		{
			x = (x < 0f ? -1f : 1f) * (HalfWidth + gap);
		}
		else
		{
			z = (z < 0f ? -1f : 1f) * (HalfDepth + gap);
		}

		Vector3 world = Center + (AxisX * x) + (AxisZ * z);
		world.Y = Center.Y;
		return world;
	}

	/// <summary>The four corners, in order round the rectangle, at the centre's height.</summary>
	public Vector3 Corner(int index)
	{
		float sx = index is 0 or 3 ? -1f : 1f;
		float sz = index is 0 or 1 ? -1f : 1f;
		return Center + (AxisX * (sx * HalfWidth)) + (AxisZ * (sz * HalfDepth));
	}

	private void Local(Vector3 point, out float x, out float z)
	{
		float dx = point.X - Center.X;
		float dz = point.Z - Center.Z;
		Vector3 ax = AxisX;
		Vector3 az = AxisZ;
		x = (dx * ax.X) + (dz * ax.Z);
		z = (dx * az.X) + (dz * az.Z);
	}

	private float Radius(Vector3 axis) =>
		(HalfWidth * MathF.Abs(AxisX.Dot(axis))) + (HalfDepth * MathF.Abs(AxisZ.Dot(axis)));

	private bool Separated(Vector3 axis, Vector3 offset, in Footprint other, float allowance) =>
		MathF.Abs(offset.Dot(axis)) >= Radius(axis) + other.Radius(axis) - MathF.Max(allowance, 0f);
}

/// <summary>
/// How a structure goes up, gets mended and comes down, as pure functions
/// (docs/NETCODE.md §10.5).
///
/// A site is paid for when it is placed and then built by standing next to it:
/// every builder in reach adds one tick of work a tick, and a site's health rises
/// with its work, so a site shot at while it is going up finishes as hurt as it
/// was made. Nothing here knows who the builders are or where they stand — that
/// is the unit manager's — and so what "twice the builders" buys, and what a
/// pillbox shrugs off, are tests rather than rounds.
/// </summary>
public static class Construction
{
	/// <summary>The health a site has the moment it is put down, as a fraction of the finished structure's.</summary>
	public const float InitialHealthFraction = 0.1f;

	/// <summary>Builders on one site that count. Past four they are in each other's way.</summary>
	public const int MaxBuildersPerSite = 4;

	/// <summary>How fast a finished structure is mended, as a fraction of the rate it went up at.</summary>
	public const float RepairRate = 0.5f;

	/// <summary>
	/// The least of itself a site shows and is hit as, however little work is in it:
	/// the foundations are there as soon as it is placed.
	/// </summary>
	public const float MinRaisedFraction = 0.15f;

	/// <summary>How far from a site's edge a builder may stand and still work on it [m].</summary>
	public const float ReachMeters = 2.5f;

	/// <summary>How far from the edge a builder is sent to stand: inside the reach, clear of the collider.</summary>
	public const float StandoffMeters = 1.2f;

	public static float Progress(int workTicks, int buildTicks) =>
		buildTicks <= 0 ? 1f : Math.Clamp(workTicks / (float)buildTicks, 0f, 1f);

	public static bool IsBuilt(int workTicks, int buildTicks) => workTicks >= buildTicks;

	public static float InitialHealth(float maxHealth) => MathF.Max(maxHealth, 1f) * InitialHealthFraction;

	/// <summary>How much of itself a site shows at <paramref name="progress"/>.</summary>
	public static float RaisedFraction(float progress) => Math.Clamp(MathF.Max(progress, MinRaisedFraction), 0f, 1f);

	/// <summary>
	/// One tick of work by <paramref name="builders"/> builders. Returns true on the
	/// tick the work is finished, and on no other.
	///
	/// Health rises with the work, from <see cref="InitialHealthFraction"/> to the
	/// whole, so an untouched site finishes at full health and a damaged one
	/// finishes as damaged as it was made; it never rises past the maximum.
	/// </summary>
	public static bool Work(ref int workTicks, ref float health, int builders, int buildTicks, float maxHealth)
	{
		if (buildTicks <= 0 || workTicks >= buildTicks)
		{
			return false;
		}

		int work = Math.Min(Math.Clamp(builders, 0, MaxBuildersPerSite), buildTicks - workTicks);
		if (work == 0)
		{
			return false;
		}

		workTicks += work;
		float perTick = MathF.Max(maxHealth, 0f) * (1f - InitialHealthFraction) / buildTicks;
		health = MathF.Min(MathF.Max(maxHealth, 0f), health + (perTick * work));

		return workTicks >= buildTicks;
	}

	/// <summary>
	/// One tick of mending by <paramref name="builders"/> builders, at
	/// <see cref="RepairRate"/> of the rate it went up at. Returns true once it is
	/// whole again. Free: the points were spent when it was placed, and the price of
	/// a repair is a builder standing in the open next to something being shot at.
	/// </summary>
	public static bool Repair(ref float health, int builders, int buildTicks, float maxHealth)
	{
		float max = MathF.Max(maxHealth, 0f);
		int working = Math.Clamp(builders, 0, MaxBuildersPerSite);

		if (health > 0f && working > 0 && buildTicks > 0)
		{
			health = MathF.Min(max, health + (RepairRate * max / buildTicks * working));
		}

		return health >= max;
	}

	/// <summary>
	/// What a hit does to a structure. Explosives do everything they would do to
	/// anything else; bullets do <paramref name="bulletScale"/> of it, which is what
	/// makes a launcher the answer to a pillbox and a rifle a poor one.
	/// </summary>
	public static float DamageTaken(float amount, bool explosive, float bulletScale) =>
		amount <= 0f ? 0f : explosive ? amount : amount * MathF.Max(bulletScale, 0f);

	public static bool InReach(in Footprint footprint, Vector3 builder, float reach = ReachMeters) =>
		footprint.DistanceTo(builder) <= reach;
}

/// <summary>Why a site was or was not accepted. Reported to the log and the agent socket, never to the wire.</summary>
public enum PlacementResult : byte
{
	Ok = 0,
	BadKind = 1,
	NoBuilder = 2,
	OutOfBounds = 3,
	Overlaps = 4,
	TooCloseToBarracks = 5,
	Blocked = 6,
	OffNavigation = 7,
	CannotAfford = 8,
	PoolFull = 9,
	NotStrategist = 10,
}

/// <summary>
/// Where a structure may go, as far as that can be answered without a physics
/// world (docs/NETCODE.md §10.5). The world half — is there a wall or a body in
/// the way, is the ground walkable — is the unit manager's.
/// </summary>
public static class StructurePlacement
{
	/// <summary>
	/// How far two structures may interpenetrate and still both be built. Just above
	/// zero, so that sandbags laid end to end make one wall rather than being refused
	/// for touching.
	/// </summary>
	public const float OverlapAllowanceMeters = 0.05f;

	/// <summary>
	/// Ground kept clear in front of a barracks door. A wall across the door would
	/// trap every unit the strategist pays for inside it.
	/// </summary>
	public const float DoorClearanceMeters = 10f;

	/// <summary>How far inside the representable position range a site has to be.</summary>
	public const float BoundsMarginMeters = 5f;

	public static PlacementResult Check(in Footprint candidate, ReadOnlySpan<Footprint> existing,
		ReadOnlySpan<Vector3> doors)
	{
		Vector3 c = candidate.Center;
		float limit = SimConfig.MaxPositionMeters - BoundsMarginMeters;
		if (!float.IsFinite(c.X) || !float.IsFinite(c.Y) || !float.IsFinite(c.Z) || !float.IsFinite(candidate.Yaw)
			|| MathF.Abs(c.X) > limit || MathF.Abs(c.Z) > limit || MathF.Abs(c.Y) > limit)
		{
			return PlacementResult.OutOfBounds;
		}

		for (int i = 0; i < doors.Length; i++)
		{
			if (candidate.DistanceTo(doors[i]) < DoorClearanceMeters)
			{
				return PlacementResult.TooCloseToBarracks;
			}
		}

		for (int i = 0; i < existing.Length; i++)
		{
			if (candidate.Overlaps(existing[i], OverlapAllowanceMeters))
			{
				return PlacementResult.Overlaps;
			}
		}

		return PlacementResult.Ok;
	}

	/// <summary>
	/// The yaw that turns a structure's front towards <paramref name="towards"/>
	/// from <paramref name="from"/>: its Z axis along the line, so a wall lies across
	/// it. Straight ahead when the two points are on top of each other.
	/// </summary>
	public static float YawFacing(Vector3 from, Vector3 towards)
	{
		float dx = towards.X - from.X;
		float dz = towards.Z - from.Z;
		return (dx * dx) + (dz * dz) < 1e-6f ? 0f : MathF.Atan2(dx, dz);
	}

	public static string Describe(PlacementResult result) => result switch
	{
		PlacementResult.Ok => "ok",
		PlacementResult.BadKind => "no such structure",
		PlacementResult.NoBuilder => "no builder selected",
		PlacementResult.OutOfBounds => "off the map",
		PlacementResult.Overlaps => "on top of another structure",
		PlacementResult.TooCloseToBarracks => "in front of a barracks door",
		PlacementResult.Blocked => "something is in the way",
		PlacementResult.OffNavigation => "nowhere a builder can walk to",
		PlacementResult.CannotAfford => "not enough points",
		PlacementResult.PoolFull => "too many structures",
		PlacementResult.NotStrategist => "not a strategist",
		_ => result.ToString(),
	};
}

/// <summary>What a structure's state byte on the wire says, besides its health and its progress.</summary>
[Flags]
public enum StructureFlags : byte
{
	None = 0,

	/// <summary>Finished: solid, and — for the two with a gun — manned.</summary>
	Built = 1 << 0,

	/// <summary>Knocked down. Rubble for a moment, then gone.</summary>
	Destroyed = 1 << 1,

	/// <summary>All the work is in, and somebody is standing where it has to go.</summary>
	Obstructed = 1 << 2,
}
