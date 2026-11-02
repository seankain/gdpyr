using System;
using Gdpyr.Core;
using Gdpyr.Match;
using Gdpyr.Sim;
using Godot;

namespace Gdpyr.Rts;

/// <summary>
/// Forward deployment (docs/CONTROL_POINTS.md §2): a barracks whose rally point is on
/// a resource node the strategist holds puts its finished units out at that node
/// rather than at its door. The rule is <see cref="ForwardDeploy"/>; this half finds
/// the node, and a clear place on it to stand a unit.
/// </summary>
public partial class UnitManager
{
	/// <summary>The sites, one per resource node in wire order, rebuilt on each ask. Pre-sized.</summary>
	private readonly DeploySite[] _deploySites = new DeploySite[SimConfig.MaxResourceNodes];

	private PhysicsShapeQueryParameters3D _deployShape;
	private readonly CapsuleShape3D[] _deployCapsules = new CapsuleShape3D[SimConfig.MaxUnitDefinitions];

	/// <summary>Units put out at a node rather than a door this round. For the debug HUD and the server log.</summary>
	public int UnitsDeployedForward { get; private set; }

	/// <summary>
	/// The node a barracks deploys at now (<see cref="ForwardDeploy.Choose"/>), or
	/// <see cref="ForwardDeploy.Door"/>. Server-side: whether the ground force is on a
	/// pad is the server's to know.
	/// </summary>
	public int DeployNodeOf(Barracks barracks)
	{
		if (barracks == null || barracks.Team != Team.Strategist)
		{
			return ForwardDeploy.Door;
		}

		int count = FillDeploySites(CombatManager.Instance?.Economy);
		return ForwardDeploy.Choose(barracks.RallyPoint, _deploySites.AsSpan(0, count));
	}

	/// <summary>
	/// Where a barracks puts a finished unit of <paramref name="definitionId"/>: a clear
	/// slot on the node it deploys at, or its door. An aircraft always leaves from the
	/// door — it takes off from where it is, and a node is where the fighting is.
	/// </summary>
	private Vector3 DeployPoint(Barracks barracks, byte definitionId, uint tick, out int node)
	{
		node = ForwardDeploy.Door;
		if (!UnitCatalog.IsAircraft(definitionId))
		{
			int chosen = DeployNodeOf(barracks);
			ResourceNode resource = CombatManager.Instance?.Economy?.NodeAt(chosen);
			if (resource != null
				&& TryDeploySlot(resource.GlobalPosition, barracks.RallyPoint, definitionId, tick, out Vector3 slot))
			{
				node = chosen;
				return slot;
			}
		}

		return SpawnPoint(barracks, definitionId);
	}

	private int FillDeploySites(EconomyService economy)
	{
		int count = Math.Min(economy?.NodeCount ?? 0, _deploySites.Length);
		for (int i = 0; i < count; i++)
		{
			ResourceNode node = economy.NodeAt(i);
			_deploySites[i] = new DeploySite(node.GlobalPosition, node.CaptureRadiusMeters, node.Capture.Owner,
				node.Capture.GroundPresent);
		}

		return count;
	}

	/// <summary>
	/// The first of <see cref="ForwardDeploy.Slot"/>'s candidates round a node that a
	/// unit can be put at, on the terms a passenger getting out of a vehicle is
	/// (<see cref="IsExitClear"/>): in the field and not taken this tick, in sight of the
	/// node's middle a metre up, on the ground within the pad's height band, on the
	/// navigation mesh, and clear of the world, units and players. False when none is,
	/// and the barracks' door is used: a node fortified shut is not a reason to lose the
	/// unit.
	/// </summary>
	private bool TryDeploySlot(Vector3 centre, Vector3 towards, byte definitionId, uint tick, out Vector3 slot)
	{
		slot = centre;
		PhysicsDirectSpaceState3D space = GetTree()?.Root?.World3D?.DirectSpaceState;
		if (space == null)
		{
			return false;
		}

		UnitDefinition definition = UnitCatalog.Definition(definitionId);
		float radius = definition?.RadiusMeters ?? 0.4f;
		float height = definition?.HeightMeters ?? 1.8f;
		Vector3 lift = Vector3.Up * ExitSightHeightMeters;

		for (int ordinal = 0; ordinal < ForwardDeploy.SlotCount; ordinal++)
		{
			Vector3 candidate = ForwardDeploy.Slot(centre, towards, ordinal);
			if (!Cargo.InField(candidate) || IsExitTaken(candidate, radius, tick))
			{
				continue;
			}

			if (WorldRay(space, centre + lift, candidate + lift, out _))
			{
				continue;
			}

			if (!WorldRay(space, candidate + lift, candidate - (Vector3.Up * GroundProbeMeters), out Vector3 ground)
				|| Mathf.Abs(ground.Y - centre.Y) > CapturePad.VerticalToleranceMeters)
			{
				continue;
			}

			if (NavigationReady)
			{
				Vector3 walkable = NavigationServer3D.MapGetClosestPoint(GetTree().Root.World3D.NavigationMap, ground);
				if (Flat(walkable, ground) > radius + ExitNavigationSlackMeters
					|| Mathf.Abs(walkable.Y - ground.Y) > ExitNavigationClimbMeters)
				{
					continue;
				}
			}

			if (IsDeployCrowded(space, ground, definitionId, radius, height))
			{
				continue;
			}

			TakeExitSlot(ground, radius, tick);
			slot = ground;
			return true;
		}

		return false;
	}

	/// <summary>Whether anything solid — the world, a unit or a player — is where a body of this size standing at <paramref name="feet"/> would be.</summary>
	private bool IsDeployCrowded(PhysicsDirectSpaceState3D space, Vector3 feet, byte definitionId, float radius,
		float height)
	{
		_deployShape ??= new PhysicsShapeQueryParameters3D
		{
			CollisionMask = CollisionLayers.World | CollisionLayers.Units | CollisionLayers.Players,
		};

		// One capsule per kind, made on first use, as a passenger's is. Never shorter than
		// its own diameter, which Godot would refuse.
		CapsuleShape3D capsule = definitionId < _deployCapsules.Length
			? (_deployCapsules[definitionId] ??= new CapsuleShape3D
			{
				Height = Mathf.Max(height, (radius * 2f) + 0.01f),
				Radius = radius,
			})
			: new CapsuleShape3D { Height = Mathf.Max(height, (radius * 2f) + 0.01f), Radius = radius };
		_deployShape.Shape = capsule;
		_deployShape.Transform = new Transform3D(Basis.Identity,
			feet + (Vector3.Up * ((height * 0.5f) + PlacementProbeLiftMeters)));
		return space.IntersectShape(_deployShape, 1).Count > 0;
	}
}
