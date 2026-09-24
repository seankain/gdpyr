using System;
using Gdpyr.Core;
using Gdpyr.Fps;
using Gdpyr.Match;
using Gdpyr.Net;
using Gdpyr.Sim;
using Gdpyr.Sim.Agent;
using Godot;

namespace Gdpyr.Rts;

/// <summary>
/// The structures the strategist's builders put up: pillboxes, sandbag walls and
/// sniper towers (docs/NETCODE.md §10.5).
///
/// Here rather than in a manager of their own because everything about them is
/// the unit manager's already: builders are units, a structure is placed by the
/// same strategist whose orders this validates, it is ticked between the units and
/// the barracks' defences for the reason those are, and it is torn down with the
/// field between rounds. It is a second file of the same class so that the half
/// of the manager that is about units still reads as one.
///
/// The server is the only authority. A site is placed by a request that pays for
/// it (<see cref="RequestConstruct"/>), goes up while builders stand next to it,
/// and becomes solid when the last of the work is in and nobody on the other side
/// is standing where it has to go. Its guns fire through the same code a barracks'
/// guns do. What a client is told is three reliable messages — a spawn, a state
/// that is sent when it changes, a despawn — and the rounds its gun fires, which
/// are ordinary projectile spawns.
/// </summary>
public partial class UnitManager
{
	/// <summary>How often a structure's health and progress are re-sent while they change. 5 Hz is a progress bar.</summary>
	private const int StructureReportIntervalTicks = SimConfig.TickRate / 5;

	/// <summary>Bytes counted for one structure-state RPC, for the bandwidth HUD.</summary>
	private const int StructureStateBytes = 8;

	/// <summary>Bytes counted for one structure-spawn RPC.</summary>
	private const int StructureSpawnBytes = 26;

	/// <summary>Ticks a knocked-down structure lies as rubble before it is taken away. Three seconds.</summary>
	private const int RubbleTicks = SimConfig.TickRate * 3;

	/// <summary>
	/// Grown onto every box the analytic hit test uses, so that a round reaching a
	/// finished structure's face is credited to the structure rather than to the
	/// world ray that stops at the same face (<c>CombatManager.ResolveSegment</c>).
	/// </summary>
	private const float HitTieBreakMeters = 0.02f;

	/// <summary>How far above and below a requested site the ground is looked for.</summary>
	private const float GroundProbeMeters = 6f;

	/// <summary>How far the placement probe is lifted, so the floor a structure stands on is not "in the way".</summary>
	private const float PlacementProbeLiftMeters = 0.15f;

	/// <summary>How far past its footprint a site may be from walkable ground and still be reached.</summary>
	private const float NavigationSlackMeters = 1.5f;

	/// <summary>How far above or below the navigation mesh a site may be: not on a roof.</summary>
	private const float NavigationClimbMeters = 1.5f;

	/// <summary>Flags no structure ever has, which makes the next state message go out.</summary>
	private const StructureFlags Unsent = (StructureFlags)0xFF;

	private readonly Structure[] _structures = new Structure[SimConfig.MaxStructures];
	private readonly Footprint[] _footprints = new Footprint[SimConfig.MaxStructures];
	private readonly Vector3[] _doors = new Vector3[SimConfig.MaxBarracks];
	private readonly Unit[] _builders = new Unit[SimConfig.MaxUnits];
	private readonly byte[] _sentHealth = new byte[SimConfig.MaxStructures];
	private readonly byte[] _sentProgress = new byte[SimConfig.MaxStructures];
	private readonly StructureFlags[] _sentFlags = new StructureFlags[SimConfig.MaxStructures];

	private Node3D _structuresRoot;
	private NavigationRegion3D _navigationRegion;
	private bool _navigationDirty;
	private bool _rebakeWatched;
	private ulong _rebakeStartedMs;
	private ulong _rebakeBlockedUsec;

	/// <summary>Structures standing, finished or not. Rubble is not counted.</summary>
	public int StructureCount
	{
		get
		{
			int standing = 0;
			for (int i = 0; i < _structures.Length; i++)
			{
				if (_structures[i] is { IsDestroyed: false })
				{
					standing++;
				}
			}
			return standing;
		}
	}

	/// <summary>Sites still going up. For the strategist HUD.</summary>
	public int SitesUnderConstruction
	{
		get
		{
			int sites = 0;
			for (int i = 0; i < _structures.Length; i++)
			{
				if (_structures[i] is { IsDestroyed: false, IsBuilt: false })
				{
					sites++;
				}
			}
			return sites;
		}
	}

	/// <summary>Structures finished this round. Server-side.</summary>
	public int StructuresBuilt { get; private set; }

	/// <summary>Structures knocked down this round, finished or not. Server-side.</summary>
	public int StructuresLost { get; private set; }

	/// <summary>Times the navigation mesh has been re-baked around a structure. What they cost, in one number.</summary>
	public int NavigationRebakes { get; private set; }

	/// <summary>What the last placement came to, for the server log and the agent socket.</summary>
	public PlacementResult LastPlacement { get; private set; }

	/// <summary>The structure in a slot, or null. Every slot, rubble included.</summary>
	public Structure StructureAt(int slot) =>
		slot >= 0 && slot < _structures.Length ? _structures[slot] : null;

	/// <summary>The structure a round's owner id names, or null for anything else.</summary>
	public Structure StructureOf(int ownerId) => StructureAt(OwnerId.StructureOf(ownerId));

	/// <summary>Live builders on a side. For the computer strategist and the HUD.</summary>
	public int BuilderCount(Team team)
	{
		int builders = 0;
		for (int i = 0; i < _ordered.Count; i++)
		{
			Unit unit = _ordered[i];
			if (unit.IsAlive && unit.Team == team && UnitCatalog.CanConstruct(unit.DefinitionId))
			{
				builders++;
			}
		}
		return builders;
	}

	private void ReadyStructures()
	{
		StructureCatalog.Load();

		_structuresRoot = new Node3D { Name = "Structures" };
		AddChild(_structuresRoot);
	}

	// ---- requests (docs/NETCODE.md §10.5) ------------------------------------

	/// <summary>
	/// Asks the server to put a structure up at <paramref name="site"/>, facing
	/// <paramref name="facing"/>, with the builders among <paramref name="unitIds"/>
	/// sent to build it. Applied immediately when this process is the authority; a
	/// client finds out by the site appearing, or not.
	/// </summary>
	public void RequestConstruct(int[] unitIds, byte kind, Vector3 site, float facing)
	{
		if (unitIds == null || unitIds.Length == 0)
		{
			return;
		}

		NetworkManager net = NetworkManager.Instance;
		if (net != null && net.IsClient)
		{
			RpcId(1, MethodName.ClientConstruct, unitIds, kind, site, facing);
			net.Stats.RecordSent(24 + (unitIds.Length * 4));
			return;
		}

		ApplyConstruct(net?.LocalPeerId ?? 1, unitIds, kind, site, facing, out _);
	}

	/// <summary>Asks the server to send builders to finish or mend a structure the sender already owns.</summary>
	public void RequestAssist(int[] unitIds, int slot)
	{
		if (unitIds == null || unitIds.Length == 0)
		{
			return;
		}

		NetworkManager net = NetworkManager.Instance;
		if (net != null && net.IsClient)
		{
			RpcId(1, MethodName.ClientAssist, unitIds, slot);
			net.Stats.RecordSent(8 + (unitIds.Length * 4));
			return;
		}

		ApplyAssist(net?.LocalPeerId ?? 1, unitIds, slot);
	}

	/// <summary>Client -> server, reliable: a site is paid for, so it is a decision and not a sample.</summary>
	[Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false,
		TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
	private void ClientConstruct(int[] unitIds, byte kind, Vector3 site, float facing)
	{
		if (NetworkManager.Instance is not { IsServer: true })
		{
			return;
		}

		NetworkManager.Instance.Stats.RecordReceived(24 + ((unitIds?.Length ?? 0) * 4));
		ApplyConstruct(Multiplayer.GetRemoteSenderId(), unitIds, kind, site, facing, out _);
	}

	[Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false,
		TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
	private void ClientAssist(int[] unitIds, int slot)
	{
		if (NetworkManager.Instance is not { IsServer: true })
		{
			return;
		}

		NetworkManager.Instance.Stats.RecordReceived(8 + ((unitIds?.Length ?? 0) * 4));
		ApplyAssist(Multiplayer.GetRemoteSenderId(), unitIds, slot);
	}

	/// <summary>
	/// Server-side: places a site on behalf of a peer with no client — the computer
	/// strategist, or a strategist policy — through the same checks a person's
	/// request gets, as <see cref="ServerIssueOrder"/> does for orders.
	/// </summary>
	public PlacementResult ServerConstruct(int peerId, ReadOnlySpan<int> unitIds, byte kind, Vector3 site,
		float facing, out int slot)
	{
		slot = -1;
		if (NetworkManager.Instance is { IsClient: true })
		{
			return PlacementResult.NotStrategist;
		}

		return ApplyConstruct(peerId, unitIds, kind, site, facing, out slot);
	}

	/// <summary>Server-side: sends builders to a structure on behalf of a peer with no client.</summary>
	public bool ServerAssist(int peerId, ReadOnlySpan<int> unitIds, int slot) =>
		NetworkManager.Instance is not { IsClient: true } && ApplyAssist(peerId, unitIds, slot);

	/// <summary>
	/// Puts a finished structure where a scenario asked for one, free and without a
	/// builder (docs/AGENT_API.md §9.1): the structure half of
	/// <see cref="ServerPlaceUnit"/>, and as narrow. It still has to fit — a scenario
	/// that puts a pillbox inside a wall has a typo in it, not a test — but it skips
	/// the navigation check, because a scenario may run before the bake. Returns the
	/// slot, or -1.
	/// </summary>
	public int ServerPlaceStructure(byte kind, Vector3 site, float facing, Team team, out PlacementResult result)
	{
		result = PlacementResult.NotStrategist;
		if (NetworkManager.Instance is { IsClient: true })
		{
			return -1;
		}

		if (!StructureKinds.IsValid(kind) || !StructureCatalog.IsBuildable(kind))
		{
			result = PlacementResult.BadKind;
			return -1;
		}

		int slot = FreeStructureSlot();
		if (slot < 0)
		{
			result = PlacementResult.PoolFull;
			return -1;
		}

		result = ValidateSite(kind, ref site, facing, requireNavigation: false);
		if (result != PlacementResult.Ok)
		{
			return -1;
		}

		Structure structure = CreateStructure(slot, kind, site, facing, team, simulated: true);
		structure.PlaceFinished();
		StructuresBuilt++;
		RequestNavigationRebake();
		AnnounceStructure(structure);
		return slot;
	}

	private PlacementResult ApplyConstruct(int senderPeerId, ReadOnlySpan<int> unitIds, byte kind, Vector3 site,
		float facing, out int slot)
	{
		PlacementResult result = PlaceSite(senderPeerId, unitIds, kind, site, facing, out slot);
		LastPlacement = result;

		if (result != PlacementResult.Ok)
		{
			RejectedOrders++;
			GD.Print($"[rts] {StructureCatalog.NameOf(kind)} refused for peer {senderPeerId}:"
				+ $" {StructurePlacement.Describe(result)}");
		}

		return result;
	}

	/// <summary>
	/// Validates and pays for a site. Every check answers something a client chose:
	/// is the sender a strategist, does the structure exist, are any of the units
	/// its builders, is there room in the table, does it fit where it was put, and
	/// can the side pay for it. Charged here, on placement, exactly as a unit is
	/// charged on the queue: a site is a commitment, and one knocked down before it
	/// is finished is not refunded.
	/// </summary>
	private PlacementResult PlaceSite(int senderPeerId, ReadOnlySpan<int> unitIds, byte kind, Vector3 site,
		float facing, out int slot)
	{
		slot = -1;

		Team team = CombatManager.Instance?.TeamOf(senderPeerId) ?? Team.GroundForce;
		if (team != Team.Strategist)
		{
			return PlacementResult.NotStrategist;
		}

		if (!StructureKinds.IsValid(kind) || !StructureCatalog.IsBuildable(kind))
		{
			return PlacementResult.BadKind;
		}

		int builders = GatherBuilders(unitIds, team);
		if (builders == 0)
		{
			return PlacementResult.NoBuilder;
		}

		int free = FreeStructureSlot();
		if (free < 0)
		{
			return PlacementResult.PoolFull;
		}

		if (!float.IsFinite(facing))
		{
			facing = 0f;
		}

		PlacementResult where = ValidateSite(kind, ref site, facing, requireNavigation: true);
		if (where != PlacementResult.Ok)
		{
			return where;
		}

		ResourceLedger ledger = CombatManager.Instance?.Match.Strategist;
		if (ledger == null || !ledger.TrySpend(StructureCatalog.CostOf(kind)))
		{
			return PlacementResult.CannotAfford;
		}

		Structure structure = CreateStructure(free, kind, site, facing, team, simulated: true);
		AnnounceStructure(structure);
		AssignBuilders(builders, structure);

		AgentEventBus.Emit(AgentEventKind.StructurePlaced, NetworkManager.Instance?.Tick ?? 0, free, kind, builders,
			site.X, site.Z);
		GD.Print($"[rts] {StructureCatalog.NameOf(kind)} placed in slot {free} at {site} by peer {senderPeerId},"
			+ $" {builders} builder(s)");

		slot = free;
		return PlacementResult.Ok;
	}

	private bool ApplyAssist(int senderPeerId, ReadOnlySpan<int> unitIds, int slot)
	{
		Team team = CombatManager.Instance?.TeamOf(senderPeerId) ?? Team.GroundForce;
		Structure structure = StructureAt(slot);

		if (team != Team.Strategist || structure == null || structure.IsDestroyed || structure.Team != team
			|| (structure.IsBuilt && structure.Health >= structure.MaxHealth))
		{
			RejectedOrders++;
			return false;
		}

		int builders = GatherBuilders(unitIds, team);
		if (builders == 0)
		{
			RejectedOrders++;
			return false;
		}

		AssignBuilders(builders, structure);
		return true;
	}

	/// <summary>
	/// The half of placement that needs the world: the flat rules first
	/// (<see cref="StructurePlacement.Check"/>), then the ground under the site —
	/// which is where the structure is put, whatever height it was asked for at —
	/// then whether a builder can walk to it, then whether anything solid is where
	/// its boxes would go.
	/// </summary>
	private PlacementResult ValidateSite(byte kind, ref Vector3 site, float facing, bool requireNavigation)
	{
		StructureShape shape = StructureCatalog.ShapeOf(kind);

		int existing = CollectFootprints();
		int doors = CollectDoors();
		Footprint footprint = new StructurePose(site, facing).FootprintOf(shape);

		PlacementResult flat = StructurePlacement.Check(footprint, _footprints.AsSpan(0, existing),
			_doors.AsSpan(0, doors));
		if (flat != PlacementResult.Ok)
		{
			return flat;
		}

		PhysicsDirectSpaceState3D space = GetTree()?.Root?.World3D?.DirectSpaceState;
		if (space != null)
		{
			var down = PhysicsRayQueryParameters3D.Create(site + (Vector3.Up * GroundProbeMeters),
				site - (Vector3.Up * GroundProbeMeters), CollisionLayers.World);
			Godot.Collections.Dictionary ground = space.IntersectRay(down);
			if (ground.Count == 0)
			{
				return PlacementResult.OffNavigation;
			}

			site = ground["position"].AsVector3();
		}

		if (requireNavigation && NavigationReady)
		{
			Rid map = GetTree().Root.World3D.NavigationMap;
			Vector3 walkable = NavigationServer3D.MapGetClosestPoint(map, site);
			float reach = Mathf.Max(shape.HalfWidth, shape.HalfDepth) + NavigationSlackMeters;
			Vector3 flatOffset = walkable - site;
			flatOffset.Y = 0f;

			if (flatOffset.Length() > reach || Mathf.Abs(walkable.Y - site.Y) > NavigationClimbMeters)
			{
				return PlacementResult.OffNavigation;
			}
		}

		if (space != null && (Obstructs(space, shape.Body, site, facing)
			|| (shape.HasTop && Obstructs(space, shape.Top, site, facing))))
		{
			return PlacementResult.Blocked;
		}

		return PlacementResult.Ok;
	}

	/// <summary>
	/// Whether anything on the world layer — the map, a barracks, a finished
	/// structure — is inside one of a structure's boxes. Shrunk a little and lifted
	/// off the ground, so that the floor under it and a neighbour it only touches
	/// are not counted.
	/// </summary>
	private static bool Obstructs(PhysicsDirectSpaceState3D space, in StructureBox box, Vector3 site, float facing)
	{
		var pose = new StructurePose(site, facing);
		Vector3 size = (box.HalfExtents * 2f) - (Vector3.One * 0.1f);
		if (size.X <= 0f || size.Y <= 0f || size.Z <= 0f)
		{
			return false;
		}

		var query = new PhysicsShapeQueryParameters3D
		{
			Shape = new BoxShape3D { Size = size },
			Transform = new Transform3D(Basis.FromEuler(new Vector3(0f, facing, 0f)),
				pose.ToWorld(box.Center) + (Vector3.Up * PlacementProbeLiftMeters)),
			CollisionMask = CollisionLayers.World,
		};

		return space.IntersectShape(query, 1).Count > 0;
	}

	/// <summary>The builders among some unit ids: alive, on the sender's side, and able to build.</summary>
	private int GatherBuilders(ReadOnlySpan<int> unitIds, Team team)
	{
		int count = 0;
		for (int i = 0; i < unitIds.Length && count < _builders.Length; i++)
		{
			int raw = unitIds[i];
			if (raw <= 0 || raw > ushort.MaxValue)
			{
				continue;
			}

			Unit unit = Find((ushort)raw);
			if (unit == null || !unit.IsAlive || unit.Team != team || !UnitCatalog.CanConstruct(unit.DefinitionId))
			{
				continue;
			}

			_builders[count++] = unit;
		}

		return count;
	}

	/// <summary>
	/// Sends builders to a structure: each to the nearest point of its own side of
	/// it, at <see cref="Construction.StandoffMeters"/> from the edge — inside the
	/// reach, and clear of the collider it will have when it is finished.
	/// </summary>
	private void AssignBuilders(int count, Structure structure)
	{
		Footprint footprint = structure.Footprint;
		for (int i = 0; i < count; i++)
		{
			Unit builder = _builders[i];
			builder.Order = new UnitOrder
			{
				Kind = OrderKind.Build,
				Target = footprint.ApproachPoint(builder.GlobalPosition, Construction.StandoffMeters),
				Anchor = structure.Pose.Base,
				TargetOwnerId = structure.ShooterId,
				Returning = false,
			};
			builder.NextScanTick = 0;
			_builders[i] = null;
		}
	}

	// ---- server: the tick ---------------------------------------------------

	/// <summary>
	/// The structure a builder's order names, if it is still one to work on: there,
	/// standing, on its side, the same one it was sent to, and not both finished and
	/// whole. Anything else and the builder is stood down where it is.
	/// </summary>
	private Structure ResolveBuildOrder(Unit unit)
	{
		Structure structure = StructureOf(unit.Order.TargetOwnerId);

		bool valid = structure != null && !structure.IsDestroyed && structure.Team == unit.Team
			&& structure.Pose.Base.DistanceSquaredTo(unit.Order.Anchor) < 0.25f
			&& UnitCatalog.CanConstruct(unit.DefinitionId);

		if (valid && !(structure.IsBuilt && structure.Health >= structure.MaxHealth))
		{
			return structure;
		}

		unit.Order = UnitOrder.Hold(unit.GlobalPosition);
		return null;
	}

	/// <summary>
	/// One tick of every structure: the work its builders put in this tick, the
	/// finish when that was the last of it, mending, and the guns. Called after the
	/// units have been stepped, because the units are what counted the builders.
	/// </summary>
	private void ServerStructures(uint tick)
	{
		CombatManager combat = CombatManager.Instance;
		PhysicsDirectSpaceState3D space = GetTree()?.Root?.World3D?.DirectSpaceState;

		for (int slot = 0; slot < _structures.Length; slot++)
		{
			Structure structure = _structures[slot];
			if (structure == null)
			{
				continue;
			}

			int workers = structure.Workers;
			structure.Workers = 0;

			if (structure.IsDestroyed)
			{
				if (tick >= structure.DespawnTick)
				{
					RemoveStructure(slot, broadcast: true);
				}
				continue;
			}

			if (!structure.IsBuilt)
			{
				bool done = Construction.IsBuilt(structure.WorkTicks, structure.Definition?.BuildTicks ?? 1)
					|| (workers > 0 && structure.Work(workers));
				if (done)
				{
					TryComplete(structure, tick, combat);
				}

				if (structure.IsArmed)
				{
					DefenseBattery.Idle(structure, tick);
				}
				continue;
			}

			if (workers > 0 && structure.Health < structure.MaxHealth)
			{
				structure.Repair(workers);
			}

			if (structure.IsArmed && combat != null)
			{
				DefenseBattery.StepGun(structure, tick, combat, space);
			}
		}

		BroadcastStructureState(tick);
		ServerNavigation();
	}

	/// <summary>
	/// Makes a site solid, once nobody on the other side is standing where it has to
	/// go. A ground-force body inside the footprint holds it at its last tick of work
	/// — standing on a site is how the ground force keeps one from finishing, as
	/// standing on a node is how it keeps one from paying — and the strategist's own
	/// units are walked out of it, because they are the strategist's to move and a
	/// wall that finished around a rifleman would have to decide which of them wins.
	/// </summary>
	private void TryComplete(Structure structure, uint tick, CombatManager combat)
	{
		Footprint footprint = structure.Footprint;

		for (int i = 0; combat != null && i < combat.PlayerCount; i++)
		{
			PlayerCombat player = combat.PlayerAt(i);
			if (player?.Character == null || !player.IsAlive || player.Team == structure.Team)
			{
				continue;
			}

			HitCapsule body = player.Character.Hitbox;
			if (footprint.Contains(player.Character.SimPosition, body.Radius)
				&& body.Top.Y > structure.Pose.Base.Y && body.Bottom.Y < structure.Pose.Base.Y + structure.Shape.Height)
			{
				structure.SetObstructed(true);
				return;
			}
		}

		for (int i = 0; i < _ordered.Count; i++)
		{
			Unit unit = _ordered[i];
			float radius = unit.Definition?.RadiusMeters ?? 0.4f;
			if (!unit.IsAlive || !footprint.Contains(unit.GlobalPosition, radius))
			{
				continue;
			}

			Vector3 outside = footprint.ApproachPoint(unit.GlobalPosition, radius + 0.3f);
			outside.Y = unit.GlobalPosition.Y;
			unit.GlobalPosition = outside;
		}

		structure.SetObstructed(false);
		structure.Complete();
		StructuresBuilt++;
		RequestNavigationRebake();

		AgentEventBus.Emit(AgentEventKind.StructureBuilt, tick, structure.Slot, structure.Kind, 0,
			structure.Pose.Base.X, structure.Pose.Base.Z);
		GD.Print($"[rts] {StructureCatalog.NameOf(structure.Kind)} in slot {structure.Slot} finished");
	}

	// ---- server: damage, asked for by CombatManager ---------------------------

	/// <summary>
	/// The first structure a segment runs into, if any is nearer than
	/// <paramref name="nearest"/>. Its own side's rounds pass through a site — a
	/// finished structure stops them anyway, as the world collider it is — and are
	/// never credited to it, as a unit's are never credited to another unit.
	/// </summary>
	public Structure QueryStructureSegment(Vector3 from, Vector3 to, float sweepRadius, Team attackerTeam,
		ref float nearest, out Vector3 point)
	{
		point = to;
		Structure hit = null;

		for (int i = 0; i < _structures.Length; i++)
		{
			Structure structure = _structures[i];
			if (structure == null || structure.IsDestroyed || structure.Team == attackerTeam)
			{
				continue;
			}

			if (structure.Pose.SegmentIntersects(structure.HitShape, from, to, sweepRadius + HitTieBreakMeters,
					out float t)
				&& t < nearest)
			{
				nearest = t;
				hit = structure;
				point = from + ((to - from) * t);
			}
		}

		return hit;
	}

	/// <summary>
	/// Applies a hit to a structure, scaled for what it was hit with
	/// (<see cref="Armour.DamageTaken"/>), and reports what that came to.
	/// Returns true when it brought the structure down.
	/// </summary>
	public bool DamageStructure(Structure structure, float amount, bool explosive, int attackerOwnerId, uint tick,
		out float dealt)
	{
		dealt = 0f;
		if (structure == null || structure.IsDestroyed)
		{
			return false;
		}

		dealt = Armour.DamageTaken(amount, explosive, structure.Definition?.BulletDamageScale ?? 1f);
		bool wasBuilt = structure.IsBuilt;
		if (!structure.ApplyDamage(dealt))
		{
			return false;
		}

		StructuresLost++;
		structure.DespawnTick = tick + RubbleTicks;
		structure.TargetPeerId = 0;
		if (wasBuilt)
		{
			RequestNavigationRebake();
		}

		AgentEventBus.Emit(AgentEventKind.StructureLost, tick, structure.Slot, structure.Kind, attackerOwnerId,
			structure.Pose.Base.X, structure.Pose.Base.Z);
		return true;
	}

	/// <summary>Distance from a blast to what stands of a structure, for splash damage.</summary>
	public static float DistanceToBlast(Structure structure, Vector3 point) =>
		structure.Pose.DistanceTo(structure.HitShape, point);

	/// <summary>
	/// Client-side: points the structure that fired a round along it, as a
	/// barracks' defence is pointed (docs/NETCODE.md §10.4).
	/// </summary>
	public void OnStructureShot(int ownerId, Vector3 direction) => StructureOf(ownerId)?.PointAlong(direction);

	// ---- the table ------------------------------------------------------------

	private int FreeStructureSlot()
	{
		for (int i = 0; i < _structures.Length; i++)
		{
			if (_structures[i] == null)
			{
				return i;
			}
		}

		return -1;
	}

	private int CollectFootprints()
	{
		int count = 0;
		for (int i = 0; i < _structures.Length; i++)
		{
			if (_structures[i] is { IsDestroyed: false } structure)
			{
				_footprints[count++] = structure.Footprint;
			}
		}
		return count;
	}

	private int CollectDoors()
	{
		int count = 0;
		for (int i = 0; i < _barracks.Count && count < _doors.Length; i++)
		{
			_doors[count++] = _barracks[i].SpawnPosition;
		}
		return count;
	}

	private Structure CreateStructure(int slot, byte kind, Vector3 position, float facing, Team team, bool simulated)
	{
		var structure = new Structure
		{
			Name = $"Structure_{slot}",
			Slot = slot,
			Kind = kind,
			Team = team,
			IsSimulated = simulated,
			Facing = facing,
			Position = position,
		};

		_structuresRoot.AddChild(structure);
		_structures[slot] = structure;
		_sentFlags[slot] = Unsent;
		return structure;
	}

	/// <summary>Tells every client about a new structure. Its state follows on the next tick's report.</summary>
	private void AnnounceStructure(Structure structure)
	{
		if (!Multiplayer.HasMultiplayerPeer() || Multiplayer.GetPeers().Length == 0)
		{
			return;
		}

		Rpc(MethodName.ServerSpawnStructure, structure.Slot, structure.Kind, (byte)structure.Team,
			structure.Pose.Base, structure.Facing);
		NetworkManager.Instance?.Stats.RecordSent(StructureSpawnBytes * Multiplayer.GetPeers().Length);
	}

	private void RemoveStructure(int slot, bool broadcast)
	{
		Structure structure = StructureAt(slot);
		if (structure == null)
		{
			return;
		}

		// Out of the bake's group and off the world layer now rather than when the
		// node is freed at the end of the frame: a second physics step in the same
		// frame may start a bake, and it must not find a wall that is gone.
		structure.Retire();
		structure.QueueFree();
		_structures[slot] = null;

		if (broadcast && Multiplayer.HasMultiplayerPeer() && Multiplayer.GetPeers().Length > 0)
		{
			Rpc(MethodName.ServerDespawnStructure, slot);
		}
	}

	/// <summary>Takes every structure off the field. Called between rounds, with the units.</summary>
	private void ClearStructures()
	{
		bool solid = false;
		for (int i = 0; i < _structures.Length; i++)
		{
			if (_structures[i] != null)
			{
				solid |= _structures[i].IsBuilt && !_structures[i].IsDestroyed;
				RemoveStructure(i, broadcast: NetworkManager.Instance is not { IsClient: true });
			}
		}

		if (solid)
		{
			RequestNavigationRebake();
		}

		StructuresBuilt = 0;
		StructuresLost = 0;
	}

	// ---- replication --------------------------------------------------------------

	/// <summary>
	/// Sends each structure's state when it has changed: at once when it is finished,
	/// knocked down or held up, because those change what a client collides with, and
	/// at 5 Hz while only its health or its progress is moving.
	/// </summary>
	private void BroadcastStructureState(uint tick)
	{
		int peers = Multiplayer.HasMultiplayerPeer() ? Multiplayer.GetPeers().Length : 0;
		bool report = tick % StructureReportIntervalTicks == 0;

		for (int slot = 0; slot < _structures.Length; slot++)
		{
			Structure structure = _structures[slot];
			if (structure == null)
			{
				continue;
			}

			byte health = structure.HealthPercent;
			byte progress = structure.ProgressByte;
			StructureFlags flags = structure.Flags;

			bool changed = flags != _sentFlags[slot]
				|| (report && (health != _sentHealth[slot] || progress != _sentProgress[slot]));
			if (!changed)
			{
				continue;
			}

			_sentFlags[slot] = flags;
			_sentHealth[slot] = health;
			_sentProgress[slot] = progress;

			if (peers > 0)
			{
				Rpc(MethodName.ServerStructureState, slot, health, progress, (byte)flags);
				NetworkManager.Instance?.Stats.RecordSent(StructureStateBytes * peers);
			}
		}
	}

	/// <summary>Everything a joining peer needs to know about the structures already standing.</summary>
	private void SendStructuresTo(int peerId)
	{
		for (int slot = 0; slot < _structures.Length; slot++)
		{
			Structure structure = _structures[slot];
			if (structure == null)
			{
				continue;
			}

			RpcId(peerId, MethodName.ServerSpawnStructure, slot, structure.Kind, (byte)structure.Team,
				structure.Pose.Base, structure.Facing);
			RpcId(peerId, MethodName.ServerStructureState, slot, structure.HealthPercent, structure.ProgressByte,
				(byte)structure.Flags);
		}
	}

	[Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false,
		TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
	private void ServerSpawnStructure(int slot, byte kind, byte team, Vector3 position, float facing)
	{
		if (NetworkManager.Instance is not { IsClient: true } || slot < 0 || slot >= _structures.Length
			|| !StructureKinds.IsValid(kind) || !float.IsFinite(facing))
		{
			return;
		}

		NetworkManager.Instance.Stats.RecordReceived(StructureSpawnBytes);

		// A slot is reused only after its despawn, and reliable messages arrive in
		// order, so an occupied slot here is a stale copy to throw away.
		RemoveStructure(slot, broadcast: false);
		CreateStructure(slot, kind, position, facing, (Team)team, simulated: false);
	}

	[Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false,
		TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
	private void ServerStructureState(int slot, byte health, byte progress, byte flags)
	{
		NetworkManager net = NetworkManager.Instance;
		if (net == null || !net.IsClient)
		{
			return;
		}

		net.Stats.RecordReceived(StructureStateBytes);

		// Only the three flags there are: a byte a server sent is still a byte off
		// the wire (docs/NETCODE.md §1).
		var known = (StructureFlags)(flags
			& (byte)(StructureFlags.Built | StructureFlags.Destroyed | StructureFlags.Obstructed));
		StructureAt(slot)?.ApplyRemoteState(health, progress, known);
	}

	[Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false,
		TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
	private void ServerDespawnStructure(int slot)
	{
		if (NetworkManager.Instance is { IsClient: true })
		{
			RemoveStructure(slot, broadcast: false);
		}
	}

	// ---- navigation ------------------------------------------------------------------

	/// <summary>Asks for the navigation mesh to be baked again, on the next tick nothing else is baking it.</summary>
	private void RequestNavigationRebake() => _navigationDirty = true;

	/// <summary>
	/// Re-bakes the map's navigation mesh around the structures, on a thread
	/// (docs/NETCODE.md §10.5).
	///
	/// Not the synchronous bake the round starts with: that one runs before anybody
	/// is connected, and this one runs in the middle of a firefight. The geometry is
	/// read on this thread and the mesh is built on a worker, and units keep pathing
	/// on the old mesh until the new one is swapped in — so for a second or so a
	/// unit may walk into a new wall and slide along it, which is the fallback every
	/// unit already has. Requests made while a bake is running are folded into the
	/// next one rather than queued one per structure.
	/// </summary>
	private void ServerNavigation()
	{
		if (!_navigationDirty || _navigationRegion == null || !IsInstanceValid(_navigationRegion)
			|| _navigationRegion.IsBaking())
		{
			return;
		}

		if (!_rebakeWatched)
		{
			_rebakeWatched = true;
			_navigationRegion.BakeFinished += OnNavigationRebaked;
		}

		_navigationDirty = false;
		NavigationRebakes++;
		_rebakeStartedMs = Time.GetTicksMsec();
		ulong before = Time.GetTicksUsec();
		_navigationRegion.BakeNavigationMesh(onThread: true);

		// The geometry is parsed on this thread before the worker takes over, and
		// that part is a hitch in the tick: worth a number in the log.
		_rebakeBlockedUsec = Time.GetTicksUsec() - before;
	}

	/// <summary>What a re-bake cost, in the server log: the number that decides whether it stays on the tick's thread's doorstep.</summary>
	private void OnNavigationRebaked()
	{
		if (_rebakeStartedMs == 0)
		{
			return;
		}

		GD.Print($"[rts] navigation re-baked around the structures in {Time.GetTicksMsec() - _rebakeStartedMs} ms,"
			+ $" {_rebakeBlockedUsec / 1000.0:0.0} ms of it on the tick ({NavigationRebakes} this round)");
		_rebakeStartedMs = 0;
	}
}
