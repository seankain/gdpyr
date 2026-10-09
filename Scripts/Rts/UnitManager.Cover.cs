using System;
using Gdpyr.Sim;
using Godot;

namespace Gdpyr.Rts;

/// <summary>
/// The cover board's engine half (docs/COVER.md §4): the map's cover boxes, read
/// once when the map is up, and every finished sandbag wall, mirrored once a tick.
/// Server-only, like every planner that reads it.
/// </summary>
public partial class UnitManager
{
	/// <summary>Map nodes in this group are cover: a <c>CSGBox3D</c> by its size and transform.</summary>
	public const string CoverGroup = "cover";

	/// <summary>
	/// Where a body can stand behind something on the field (docs/COVER.md). The
	/// map's boxes and the finished sandbag walls, either side's: cover is cover.
	/// Server-side; read by the ground bots, the units and the computer strategist.
	/// </summary>
	public CoverBoard Cover { get; } = new();

	/// <summary>Reused by every unit's cover search: the spots other units have taken.</summary>
	private readonly Vector3[] _coverClaims = new Vector3[SimConfig.MaxUnits];

	/// <summary>
	/// Reads the map's cover boxes onto the board. Only boxes: a <c>CSGBox3D</c>'s
	/// size scaled by its transform, turned by its yaw. A box that is tilted is
	/// read as though it were not, which is the same cut <see cref="StructurePose"/>
	/// makes — nothing here is built on a slope.
	/// </summary>
	private void CollectCover()
	{
		Cover.ClearLevel();
		int skipped = 0;
		foreach (Node node in GetTree().GetNodesInGroup(CoverGroup))
		{
			if (node is not CsgBox3D box)
			{
				skipped++;
				continue;
			}

			Basis basis = box.GlobalBasis;
			Vector3 size = box.Size * basis.Scale;
			float yaw = Mathf.Atan2(basis.Z.X, basis.Z.Z);
			if (Cover.AddLevel(box.GlobalPosition, yaw, size) < 0)
			{
				skipped++;
			}
		}

		GD.Print($"[cover] {Cover.LevelCount} boxes from the map"
			+ (skipped > 0 ? $", {skipped} in the '{CoverGroup}' group not read" : string.Empty));
	}

	/// <summary>
	/// Every structure slot onto the board as it stands this tick: a finished
	/// sandbag wall is cover, anything else — a site still going up, rubble, a
	/// pillbox that shoots whoever stands behind it — is not.
	/// </summary>
	private void SyncStructureCover()
	{
		for (int slot = 0; slot < _structures.Length; slot++)
		{
			Structure structure = _structures[slot];
			if (structure == null || !structure.IsBuilt || structure.IsDestroyed
				|| structure.Kind != StructureKinds.SandbagWall || structure.Definition == null)
			{
				Cover.ClearStructure(slot);
				continue;
			}

			Cover.SetStructure(slot, structure.Pose.Base, structure.Pose.Yaw, structure.Definition.BodySize);
		}
	}

	/// <summary>
	/// A cover search for one unit against a threat standing at <paramref name="threat"/>:
	/// its own body's numbers, and the spots every other unit on its side has taken.
	/// </summary>
	private CoverQuery UnitCoverQuery(Unit unit, Vector3 threat, bool mustFire, float searchMeters)
	{
		UnitDefinition definition = unit.Definition;
		float height = definition?.HeightMeters ?? 1.8f;
		return new CoverQuery
		{
			From = unit.GlobalPosition,
			Threat = threat,
			ThreatEyeMeters = CoverThreatEyeMeters,
			ThreatAimMeters = CoverThreatChestMeters,
			SearchMeters = searchMeters,
			BodyRadiusMeters = definition?.RadiusMeters ?? 0.4f,
			ProtectMeters = height * 0.5f,
			EyeMeters = definition?.EyeHeightMeters ?? 1.5f,
			MustFire = mustFire,
			MinThreatMeters = mustFire ? CoverMinThreatMeters : 0f,
			MaxThreatMeters = mustFire ? unit.Traits.EngageRangeMeters : 0f,
		};
	}

	/// <summary>Where the units of <paramref name="team"/> other than <paramref name="self"/> have taken cover.</summary>
	private ReadOnlySpan<Vector3> CoverClaims(Unit self, Team team)
	{
		int count = 0;
		for (int i = 0; i < _ordered.Count && count < _coverClaims.Length; i++)
		{
			Unit unit = _ordered[i];
			if (unit != self && unit.IsAlive && unit.Team == team && unit.HasCoverSpot)
			{
				_coverClaims[count++] = unit.CoverSpot;
			}
		}

		return _coverClaims.AsSpan(0, count);
	}

	/// <summary>
	/// Keeps a unit's spot against the query's threat while the box beside it
	/// still hides it from there and it is still within reach; otherwise finds the
	/// best one nobody else has. Returns false, and lets the claim go, when there is
	/// none. A kept spot is what stops a squad shuffling between walls every scan.
	/// </summary>
	private bool UpdateUnitCover(Unit unit, in CoverQuery query, out CoverSpot spot)
	{
		if (unit.HasCoverSpot)
		{
			int box = Cover.ProtectingBox(unit.CoverSpot, query);
			if (box >= 0 && KeepsSpot(box, unit.CoverSpot, query))
			{
				spot = new CoverSpot { Position = unit.CoverSpot, Object = box, Low = IsLow(box, query) };
				return true;
			}
		}

		if (Cover.TryFindSpot(query, CoverClaims(unit, unit.Team), out spot))
		{
			unit.HasCoverSpot = true;
			unit.CoverSpot = spot.Position;
			return true;
		}

		unit.HasCoverSpot = false;
		return false;
	}

	/// <summary>
	/// Whether a spot already taken behind box <paramref name="box"/> still does: within
	/// reach of the body, and — for a spot to fight from — still in the fight's range
	/// with a box low enough to fire over.
	/// </summary>
	private bool KeepsSpot(int box, Vector3 at, in CoverQuery query)
	{
		if (Flat(at, query.From) > query.SearchMeters)
		{
			return false;
		}

		if (!query.MustFire)
		{
			return true;
		}

		float range = Flat(at, query.Threat);
		return IsLow(box, query) && range >= query.MinThreatMeters
			&& (query.MaxThreatMeters <= 0f || range <= query.MaxThreatMeters);
	}

	private bool IsLow(int box, in CoverQuery query) =>
		Cover.HeightOf(box) <= query.EyeMeters - CoverBoard.FireClearanceMeters;

	/// <summary>The board and its use, for the debug HUD: map boxes, sandbag walls, units holding a spot.</summary>
	public string DescribeCover()
	{
		int holding = 0;
		for (int i = 0; i < _ordered.Count; i++)
		{
			holding += _ordered[i].IsAlive && _ordered[i].HasCoverSpot ? 1 : 0;
		}

		return $"{Cover.LevelCount} map boxes  {Cover.Count - Cover.LevelCount} sandbag walls  {holding} units holding a spot"
			+ (Cover.Overflow > 0 ? $"  ({Cover.Overflow} map boxes not read)" : string.Empty);
	}

	/// <summary>
	/// Where a standing ground player's sight leaves from, above their feet: for a
	/// cover search against one, and for a ground bot's own search.
	/// </summary>
	public const float CoverThreatEyeMeters = 1.5f;

	/// <summary>Where a standing ground player is aimed at, above their feet: the middle of the 2 m capsule.</summary>
	public const float CoverThreatChestMeters = 1.0f;

	/// <summary>A fight closer than this is not one to walk to cover for: it is already at the wall.</summary>
	public const float CoverMinThreatMeters = 6f;

	/// <summary>How far from where it stands a unit in a fight looks for a low wall to fight from.</summary>
	public const float UnitCoverSearchMeters = 10f;

	/// <summary>How far a builder under fire will go to get something between it and the shooter.</summary>
	public const float BuilderCoverSearchMeters = 15f;

	/// <summary>How far past its ring's far side a unit on a defended ring looks for a wall to stand behind.</summary>
	public const float PostCoverSearchMeters = 5f;

	private static float Flat(Vector3 a, Vector3 b)
	{
		float x = a.X - b.X;
		float z = a.Z - b.Z;
		return MathF.Sqrt((x * x) + (z * z));
	}
}
