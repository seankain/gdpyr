using System;
using System.Collections.Generic;
using Gdpyr.Core;
using Gdpyr.Fps;
using Gdpyr.Match;
using Gdpyr.Net;
using Gdpyr.Sim;
using Godot;

namespace Gdpyr.Rts;

/// <summary>
/// Fourth autoload: the units, the barracks that make them and the orders that
/// move them (docs/IMPLEMENTATION_PLAN.md §M3).
///
/// The server is the only authority. It walks every unit, decides what each one
/// can see, fires their weapons through the same <c>ProjectileSim</c> players use,
/// and broadcasts one packed snapshot of the lot at 20 Hz. Clients hold puppet
/// nodes, interpolate them and are allowed to ask for two things: a unit on a
/// queue, and an order for units they own (docs/NETCODE.md §6).
///
/// It is also the registry §1 of the plan insists on. The pyrrhic original ran
/// <c>FindObjectsOfType&lt;PyrrhicPlayer&gt;()</c> inside per-unit logic, which at
/// fifty units is fifty scene scans a tick; here every query walks an array that
/// was already in hand, and target acquisition is staggered across
/// <see cref="SimConfig.UnitTargetRefreshTicks"/> on top of that.
///
/// Like every other manager here it never ticks itself: <see cref="PlayerManager"/>
/// drives it, before combat resolves projectiles, so that a round a unit fired on
/// this tick is in the air on this tick.
/// </summary>
public partial class UnitManager : Node
{
	public static UnitManager Instance { get; private set; }

	/// <summary>Nodes in this group carry the map's navigation mesh.</summary>
	private const string NavigationGroup = "navigation_region";

	/// <summary>Ticks a dead unit stays on the field before it is freed. Two seconds.</summary>
	private const int CorpseTicks = SimConfig.TickRate * 2;

	/// <summary>How often a barracks tells everyone what it is building. 5 Hz is a progress bar.</summary>
	private const int BarracksReportIntervalTicks = SimConfig.TickRate / 5;

	/// <summary>Bytes counted for the barracks-state RPC, for the bandwidth HUD.</summary>
	private const int BarracksStateBytes = 8;

	/// <summary>Messages arriving on the idle frame wait for the physics step, as snapshots do.</summary>
	private const int MaxPendingSnapshots = 8;

	/// <summary>What a client knows about one barracks: enough to draw a queue and a bar.</summary>
	public struct BarracksStatus
	{
		public byte Queued;
		public byte Head;
		public float Progress;
	}

	private readonly Dictionary<ushort, Unit> _units = new();
	private readonly List<Unit> _ordered = new();
	private readonly List<Barracks> _barracks = new();
	private readonly List<byte[]> _pendingSnapshots = new();

	private readonly UnitSnapshot[] _snapshotScratch = new UnitSnapshot[SimConfig.MaxUnits];
	private readonly Dictionary<ushort, SnapshotInterpolator> _interpolators = new();

	private BarracksStatus[] _barracksStatus = Array.Empty<BarracksStatus>();

	private Node3D _unitsRoot;
	private ushort _nextUnitId = 1;
	private bool _started;

	/// <summary>
	/// True once the map's navigation mesh has been baked and has geometry. Units
	/// steer straight at their destination while it is false, which is worse pathing
	/// and a playable milestone (see <c>Unit.NextPathPosition</c>).
	/// </summary>
	public bool NavigationReady { get; private set; }

	/// <summary>Units alive on the field. For the net debug HUD (docs/NETCODE.md §8).</summary>
	public int LiveUnitCount
	{
		get
		{
			int live = 0;
			for (int i = 0; i < _ordered.Count; i++)
			{
				if (_ordered[i].IsAlive)
				{
					live++;
				}
			}
			return live;
		}
	}

	/// <summary>Every unit node in this process, alive or freshly dead.</summary>
	public int SlotCount => _ordered.Count;

	public Unit UnitAt(int index) => index >= 0 && index < _ordered.Count ? _ordered[index] : null;

	public Unit Find(ushort unitId) => _units.GetValueOrDefault(unitId);

	public int BarracksCount => _barracks.Count;

	public Barracks BarracksAt(int index) => index >= 0 && index < _barracks.Count ? _barracks[index] : null;

	/// <summary>
	/// Units on every barracks queue. Server-side: a queued unit has already been
	/// paid for, so it is the difference between a strategist who is broke and one
	/// who is out of the round (<see cref="Sim.WinConditions.IsStrategistEliminated"/>).
	/// </summary>
	public int QueuedUnits
	{
		get
		{
			int queued = 0;
			for (int i = 0; i < _barracks.Count; i++)
			{
				queued += _barracks[i].Queue.Count;
			}
			return queued;
		}
	}

	/// <summary>Units the server has produced this round. For the HUD and M8's CSV.</summary>
	public int UnitsProduced { get; private set; }

	/// <summary>Units killed. Server-side.</summary>
	public int UnitsLost { get; private set; }

	/// <summary>Orders refused because the sender did not own the units or the order made no sense.</summary>
	public int RejectedOrders { get; private set; }

	public override void _Ready()
	{
		Instance = this;
		ProcessMode = ProcessModeEnum.Always;

		WeaponCatalog.Load();
		UnitCatalog.Load();

		_unitsRoot = new Node3D { Name = "Units" };
		AddChild(_unitsRoot);

		if (NetworkManager.Instance is { } net)
		{
			net.PeerJoined += OnPeerJoined;
		}
	}

	public override void _ExitTree()
	{
		if (Instance == this)
		{
			Instance = null;
		}
	}

	// ---- tick, driven by PlayerManager -------------------------------------

	/// <summary>
	/// One server tick: build, then think, then walk and shoot, then tell everyone.
	/// Called before combat steps the projectiles, so a unit's shot flies on the
	/// tick it was fired.
	/// </summary>
	public void ServerTick(uint tick)
	{
		EnsureStarted(server: true);

		ServerBuild(tick);

		for (int i = 0; i < _ordered.Count; i++)
		{
			SimulateUnit(_ordered[i], tick);
		}

		ServerReapCorpses(tick);

		if (tick % SimConfig.UnitSnapshotIntervalTicks == 0)
		{
			BroadcastUnitSnapshot(tick);
		}

		if (tick % BarracksReportIntervalTicks == 0)
		{
			BroadcastBarracksState(tick);
		}
	}

	/// <summary>
	/// One client tick: apply what the server said, then place the puppets at the
	/// render clock — ~100 ms behind the server estimate, the same place remote
	/// characters are drawn (docs/NETCODE.md §2, §3.3).
	/// </summary>
	public void ClientTick(float renderTick)
	{
		EnsureStarted(server: false);
		ApplyPendingSnapshots();

		for (int i = 0; i < _ordered.Count; i++)
		{
			Unit unit = _ordered[i];
			if (!_interpolators.TryGetValue(unit.UnitId, out SnapshotInterpolator interpolator))
			{
				continue;
			}

			if (interpolator.TrySample(renderTick, out Vector3 position, out float yaw, out _))
			{
				unit.ApplyRemoteTransform(position, yaw);
			}
			interpolator.Prune(renderTick);
		}
	}

	/// <summary>
	/// Autoloads are ready before the map is, so the barracks and the navigation
	/// mesh cannot be found in <c>_Ready</c>. Same arrangement as the player
	/// roster's spawn points.
	/// </summary>
	private void EnsureStarted(bool server)
	{
		if (_started)
		{
			return;
		}

		_started = true;
		CollectBarracks();

		if (server)
		{
			BakeNavigation();
		}
	}

	// ---- server: production ------------------------------------------------

	private void ServerBuild(uint tick)
	{
		for (int i = 0; i < _barracks.Count; i++)
		{
			Barracks barracks = _barracks[i];
			if (!barracks.Queue.Tick(tick, out byte definitionId))
			{
				continue;
			}

			Unit unit = SpawnUnit(definitionId, barracks.SpawnSlot(barracks.Queue.Produced), barracks.Team);
			if (unit == null)
			{
				// The field is full. Refund rather than swallow it: the strategist paid
				// for a unit and has to get either the unit or the points back.
				UnitDefinition definition = UnitCatalog.Definition(definitionId);
				CombatManager.Instance?.Match.Strategist.Refund(definition?.Cost ?? 0);
				GD.PushWarning($"[rts] unit pool full ({SimConfig.MaxUnits}); refunded a {UnitCatalog.NameOf(definitionId)}");
				continue;
			}

			// Fresh units walk out of the door rather than standing in it.
			unit.Order = new UnitOrder
			{
				Kind = OrderKind.Move,
				Target = barracks.RallyPoint,
				Anchor = unit.GlobalPosition,
				TargetOwnerId = OwnerId.None,
			};
			UnitsProduced++;
		}
	}

	/// <summary>
	/// Puts a unit on the field and tells every client about it. Reliable, because
	/// the roster is state and not a sample.
	/// </summary>
	private Unit SpawnUnit(byte definitionId, Vector3 position, Team team)
	{
		if (_ordered.Count >= SimConfig.MaxUnits)
		{
			return null;
		}

		ushort id = NextUnitId();
		if (id == 0)
		{
			return null;
		}

		Unit unit = CreateUnit(id, definitionId, position, team, simulated: true);

		if (Multiplayer.HasMultiplayerPeer() && Multiplayer.GetPeers().Length > 0)
		{
			Rpc(MethodName.ServerSpawnUnit, (int)id, definitionId, (byte)team, position);
		}

		return unit;
	}

	/// <summary>
	/// The lowest unused id. Ids are a ushort on the wire, so they wrap; at the
	/// production rates this game can sustain, wrapping means "a very long round",
	/// not "a collision".
	/// </summary>
	private ushort NextUnitId()
	{
		for (int attempt = 0; attempt <= ushort.MaxValue; attempt++)
		{
			ushort candidate = _nextUnitId++;
			if (_nextUnitId == 0)
			{
				// 0 is "no unit" everywhere it appears; skip it on the wrap.
				_nextUnitId = 1;
			}

			if (candidate != 0 && !_units.ContainsKey(candidate))
			{
				return candidate;
			}
		}

		return 0;
	}

	// ---- server: simulation ------------------------------------------------

	/// <summary>
	/// One unit's tick: re-scan for a target when it is due, decide what that makes
	/// it, walk, turn and shoot.
	/// </summary>
	private void SimulateUnit(Unit unit, uint tick)
	{
		if (!unit.IsAlive)
		{
			return;
		}

		if (tick >= unit.NextScanTick)
		{
			AcquireTarget(unit);

			// Staggered by the unit's own id, so fifty units do not all re-scan on the
			// same tick (docs/IMPLEMENTATION_PLAN.md §6). The offset is taken once, on
			// the unit's first scan, and then every scan is a fixed interval later.
			uint stagger = unit.NextScanTick == 0 ? unit.UnitId % (uint)SimConfig.UnitTargetRefreshTicks : 0u;
			unit.NextScanTick = tick + (uint)SimConfig.UnitTargetRefreshTicks + stagger;
		}

		bool hasTarget = TryResolveTarget(unit, out Vector3 targetPoint, out Vector3 targetVelocity,
			out float targetDistance);

		if (!hasTarget)
		{
			unit.TargetOwnerId = OwnerId.None;
		}

		Vector3 destination = UnitBrain.Destination(unit.Order, unit.GlobalPosition, hasTarget, targetPoint,
			unit.Traits, out bool hasDestination);

		float destinationDistance = hasDestination ? unit.GlobalPosition.DistanceTo(destination) : 0f;

		var situation = new UnitSituation(unit.IsAlive, unit.Order.Kind, hasTarget, targetDistance,
			destinationDistance);
		unit.State = UnitBrain.Next(situation, unit.Traits);

		bool arrived = hasDestination && destinationDistance <= unit.Traits.ArrivalRadiusMeters;
		UnitBrain.AdvancePatrol(ref unit.Order, arrived);

		// A unit that stops to fight stops; one on a move order shoots on the walk.
		bool holding = unit.State == UnitStateId.Engaging && UnitBrain.HoldsWhileEngaging(unit.Order.Kind);
		unit.Steer(destination, hasDestination && !holding, SimConfig.TickDelta);
		unit.FaceTowards(hasTarget ? targetPoint : destination, SimConfig.TickDelta);

		ServerFire(unit, tick, hasTarget, targetPoint, targetVelocity);
	}

	/// <summary>
	/// Runs a unit's weapon over a synthesized input frame. The frame holds the
	/// trigger down and reports it as a fresh press every tick, which is right for
	/// both fire modes: <see cref="FireMode.Auto"/> reads the hold and everything
	/// else reads the edge, and <see cref="WeaponState.NextFireTick"/> is what
	/// actually decides the cadence either way.
	/// </summary>
	private void ServerFire(Unit unit, uint tick, bool hasTarget, Vector3 targetPoint, Vector3 targetVelocity)
	{
		CombatManager combat = CombatManager.Instance;
		if (combat == null)
		{
			return;
		}

		bool pullingTrigger = hasTarget && unit.State == UnitStateId.Engaging;
		InputButtons buttons = pullingTrigger ? InputButtons.Fire : InputButtons.None;

		var frame = InputFrame.Create(tick, 0f, 0f, unit.Yaw, 0f, buttons);
		var context = new InputContext(frame, (ushort)InputButtons.None, SimConfig.TickDelta);

		WeaponStats stats = WeaponCatalog.StatsFor(unit.Weapon.DefinitionId);
		if (WeaponSim.Step(ref unit.Weapon, stats, context, tick) != WeaponAction.Fire)
		{
			return;
		}

		Vector3 muzzle = unit.EyePosition;
		ProjectileStats projectile = WeaponCatalog.Projectiles.Length > unit.Weapon.DefinitionId
			? WeaponCatalog.Projectiles[unit.Weapon.DefinitionId]
			: default;

		Vector3 lead = UnitBrain.Lead(muzzle, targetPoint, targetVelocity, projectile.MuzzleVelocity);
		Vector3 aim = (lead - muzzle).Normalized();

		// The unit's own cone, not the weapon's: the same rifle in a rifleman's hands
		// and a tank's should not shoot the same (docs/IMPLEMENTATION_PLAN.md §M3).
		float cone = unit.Definition?.AccuracyConeRadians ?? 0f;
		int owner = OwnerId.ForUnit(unit.UnitId);
		Vector3 direction = Spread.Apply(aim, cone,
			Spread.Seed(owner, unit.Weapon.DefinitionId, unit.Weapon.ShotIndex));

		// No lag compensation: a unit has no client and therefore no latency to owe.
		combat.SpawnProjectile(tick, owner, unit.Weapon.DefinitionId, muzzle, direction, catchUpTicks: 0);
	}

	/// <summary>
	/// Finds the nearest ground-force player this unit can see, and keeps the one it
	/// has while that is still true. One line-of-sight ray per unit per scan, which
	/// at fifty units and a ten-tick stagger is five rays a tick.
	/// </summary>
	private void AcquireTarget(Unit unit)
	{
		CombatManager combat = CombatManager.Instance;
		if (combat == null)
		{
			return;
		}

		Vector3 eye = unit.EyePosition;

		// A target the strategist named outranks whatever the sensor finds, for as
		// long as it is alive and in range. Past that the unit falls back to the
		// sensor and walks towards where the order said it was.
		if (unit.Order.Kind == OrderKind.Attack && OwnerId.IsPeer(unit.Order.TargetOwnerId))
		{
			PlayerCombat named = combat.Find(OwnerId.PeerOf(unit.Order.TargetOwnerId));
			if (named?.Character != null && named.IsAlive && named.Team != unit.Team
				&& UnitBrain.CanAcquire(eye.DistanceTo(named.Character.EyePosition), unit.Traits))
			{
				unit.TargetOwnerId = unit.Order.TargetOwnerId;
				return;
			}
		}

		PlayerCombat best = null;
		float bestDistance = float.MaxValue;

		for (int i = 0; i < combat.PlayerCount; i++)
		{
			PlayerCombat player = combat.PlayerAt(i);
			if (player?.Character == null || !player.IsAlive || player.Team == unit.Team)
			{
				continue;
			}

			float distance = eye.DistanceTo(player.Character.EyePosition);
			if (!UnitBrain.CanAcquire(distance, unit.Traits) || distance >= bestDistance)
			{
				continue;
			}

			if (!HasLineOfSight(eye, player.Character.EyePosition))
			{
				continue;
			}

			best = player;
			bestDistance = distance;
		}

		unit.TargetOwnerId = best != null ? OwnerId.ForPeer(best.PeerId) : OwnerId.None;
	}

	/// <summary>
	/// Where this unit's current target is right now, if it still has one worth
	/// having. Runs every tick — unlike acquisition — because a target's position is
	/// what the unit is aiming at.
	/// </summary>
	private bool TryResolveTarget(Unit unit, out Vector3 point, out Vector3 velocity, out float distance)
	{
		point = Vector3.Zero;
		velocity = Vector3.Zero;
		distance = float.MaxValue;

		if (unit.TargetOwnerId == OwnerId.None || CombatManager.Instance is not { } combat)
		{
			return false;
		}

		if (!OwnerId.IsPeer(unit.TargetOwnerId))
		{
			return false;
		}

		PlayerCombat target = combat.Find(OwnerId.PeerOf(unit.TargetOwnerId));
		if (target?.Character == null || !target.IsAlive)
		{
			return false;
		}

		// Chest rather than eye: the centre of the capsule is what a hit test is most
		// likely to agree with.
		point = target.Character.Hitbox.Center;
		velocity = target.Character.Velocity;
		distance = unit.EyePosition.DistanceTo(point);

		return !UnitBrain.ShouldDropTarget(distance, unit.Traits);
	}

	/// <summary>
	/// Takes corpses off the field once clients have had time to see them die. A
	/// dead unit stays in the snapshot until then, which is what makes its death
	/// visible rather than a node that vanished.
	/// </summary>
	private void ServerReapCorpses(uint tick)
	{
		for (int i = _ordered.Count - 1; i >= 0; i--)
		{
			Unit unit = _ordered[i];
			if (unit.IsAlive || unit.DespawnTick == 0 || tick < unit.DespawnTick)
			{
				continue;
			}

			ushort id = unit.UnitId;
			Despawn(id);

			if (Multiplayer.HasMultiplayerPeer() && Multiplayer.GetPeers().Length > 0)
			{
				Rpc(MethodName.ServerDespawnUnit, (int)id);
			}
		}
	}

	// ---- server: damage, asked for by CombatManager -------------------------

	/// <summary>
	/// The first unit a segment runs into, if any is nearer than
	/// <paramref name="nearest"/>.
	///
	/// Like a player's, a unit's hit is decided here and not by a physics query:
	/// units are on their own collision layer precisely so that a projectile's world
	/// cast finds the wall behind them and not them (docs/NETCODE.md §5).
	/// </summary>
	public Unit QuerySegment(Vector3 from, Vector3 to, float sweepRadius, int attackerOwnerId, Team attackerTeam,
		ref float nearest, out Vector3 point)
	{
		point = to;
		Unit hit = null;

		for (int i = 0; i < _ordered.Count; i++)
		{
			Unit unit = _ordered[i];

			// Its own muzzle sits inside its own capsule, and friendly fire between
			// units would make a firing line a suicide pact.
			if (!unit.IsAlive || unit.Team == attackerTeam || OwnerId.ForUnit(unit.UnitId) == attackerOwnerId)
			{
				continue;
			}

			if (Hitbox.SegmentIntersects(unit.Hitbox, from, to, sweepRadius, out float t) && t < nearest)
			{
				nearest = t;
				hit = unit;
				point = from + ((to - from) * t);
			}
		}

		return hit;
	}

	/// <summary>Applies damage to a unit. Returns true when that killed it.</summary>
	public bool Damage(Unit unit, float amount, uint tick)
	{
		if (unit == null || !unit.ApplyDamage(amount))
		{
			return false;
		}

		UnitsLost++;
		unit.DespawnTick = tick + CorpseTicks;
		return true;
	}

	/// <summary>Distance from a blast to a unit's capsule, for splash damage.</summary>
	public static float DistanceToBlast(Unit unit, Vector3 point)
	{
		HitCapsule capsule = unit.Hitbox;
		return Mathf.Max(0f, Mathf.Sqrt(Hitbox.DistanceToAxisSquared(capsule, point)) - capsule.Radius);
	}

	// ---- orders (docs/NETCODE.md §6.3) --------------------------------------

	/// <summary>
	/// Asks the server to order units about. Applied immediately when this process
	/// is the authority; a client shows its own marker and finds out what happened
	/// by watching where the units go.
	/// </summary>
	public void RequestOrder(int[] unitIds, OrderKind kind, Vector3 target, int targetOwnerId)
	{
		if (unitIds == null || unitIds.Length == 0)
		{
			return;
		}

		NetworkManager net = NetworkManager.Instance;
		if (net != null && net.IsClient)
		{
			RpcId(1, MethodName.ClientIssueOrder, unitIds, (byte)kind, target, targetOwnerId);
			net.Stats.RecordSent(16 + (unitIds.Length * 4));
			return;
		}

		ApplyOrder(net?.LocalPeerId ?? 1, unitIds, (byte)kind, target, targetOwnerId);
	}

	/// <summary>Client -> server, reliable: an order is a decision, not a sample.</summary>
	[Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false,
		TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
	private void ClientIssueOrder(int[] unitIds, byte kind, Vector3 target, int targetOwnerId)
	{
		if (NetworkManager.Instance is not { IsServer: true })
		{
			return;
		}

		NetworkManager.Instance.Stats.RecordReceived(16 + ((unitIds?.Length ?? 0) * 4));
		ApplyOrder(Multiplayer.GetRemoteSenderId(), unitIds, kind, target, targetOwnerId);
	}

	/// <summary>
	/// Server-side: issues an order on behalf of a peer that has no client to send
	/// one, which is what a computer strategist is
	/// (docs/IMPLEMENTATION_PLAN.md §M3.5).
	///
	/// It lands in the same <see cref="ApplyOrder"/> a client's RPC does, so the bot
	/// is subject to every ownership check a person is. A span rather than an array
	/// because the caller batches its orders out of a buffer it keeps, and the unit
	/// path allocates nothing per tick (docs/IMPLEMENTATION_PLAN.md §3).
	/// </summary>
	public void ServerIssueOrder(int peerId, ReadOnlySpan<int> unitIds, OrderKind kind, Vector3 target,
		int targetOwnerId)
	{
		if (NetworkManager.Instance is { IsClient: true })
		{
			return;
		}

		ApplyOrder(peerId, unitIds, (byte)kind, target, targetOwnerId);
	}

	/// <summary>Server-side: queues a unit on behalf of a peer with no client. See <see cref="ServerIssueOrder"/>.</summary>
	public bool ServerQueueUnit(int peerId, int barracksIndex, byte definitionId)
	{
		if (NetworkManager.Instance is { IsClient: true })
		{
			return false;
		}

		return ApplyBuild(peerId, barracksIndex, definitionId);
	}

	/// <summary>
	/// Validates and applies an order. Every one of these checks is answering a
	/// byte a client chose (docs/NETCODE.md §1, §6.3): is the sender a strategist,
	/// does the order type exist, does the sender's side own each unit.
	/// </summary>
	private void ApplyOrder(int senderPeerId, ReadOnlySpan<int> unitIds, byte kind, Vector3 target,
		int targetOwnerId)
	{
		if (unitIds.Length == 0 || !UnitOrder.IsIssuable(kind))
		{
			RejectedOrders++;
			return;
		}

		Team senderTeam = CombatManager.Instance?.TeamOf(senderPeerId) ?? Team.GroundForce;
		if (senderTeam != Team.Strategist)
		{
			RejectedOrders++;
			return;
		}

		var ordered = (OrderKind)kind;

		for (int i = 0; i < unitIds.Length; i++)
		{
			int raw = unitIds[i];
			if (raw <= 0 || raw > ushort.MaxValue)
			{
				RejectedOrders++;
				continue;
			}

			Unit unit = Find((ushort)raw);
			if (unit == null || !unit.IsAlive || unit.Team != senderTeam)
			{
				RejectedOrders++;
				continue;
			}

			unit.Order = ordered == OrderKind.Stop
				? UnitOrder.Hold(unit.GlobalPosition)
				: new UnitOrder
				{
					Kind = ordered,
					Target = target,
					// A patrol runs between where the unit was told and where it stood
					// when it was told; a defend order anchors on the point itself.
					Anchor = ordered == OrderKind.Defend ? target : unit.GlobalPosition,
					TargetOwnerId = ordered == OrderKind.Attack ? targetOwnerId : OwnerId.None,
					Returning = false,
				};

			// Re-evaluate on the next tick rather than at the end of the current scan
			// interval: an order the units do not react to for a sixth of a second
			// reads as an order that was dropped.
			unit.NextScanTick = 0;
		}
	}

	// ---- production requests ------------------------------------------------

	public void RequestBuild(int barracksIndex, byte definitionId)
	{
		NetworkManager net = NetworkManager.Instance;
		if (net != null && net.IsClient)
		{
			RpcId(1, MethodName.ClientQueueUnit, barracksIndex, definitionId);
			net.Stats.RecordSent(6);
			return;
		}

		ApplyBuild(net?.LocalPeerId ?? 1, barracksIndex, definitionId);
	}

	public void RequestCancelBuild(int barracksIndex)
	{
		NetworkManager net = NetworkManager.Instance;
		if (net != null && net.IsClient)
		{
			RpcId(1, MethodName.ClientCancelBuild, barracksIndex);
			net.Stats.RecordSent(5);
			return;
		}

		ApplyCancelBuild(net?.LocalPeerId ?? 1, barracksIndex);
	}

	public void RequestRally(int barracksIndex, Vector3 point)
	{
		NetworkManager net = NetworkManager.Instance;
		if (net != null && net.IsClient)
		{
			RpcId(1, MethodName.ClientSetRally, barracksIndex, point);
			net.Stats.RecordSent(17);
			return;
		}

		ApplyRally(net?.LocalPeerId ?? 1, barracksIndex, point);
	}

	[Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false,
		TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
	private void ClientQueueUnit(int barracksIndex, byte definitionId)
	{
		if (NetworkManager.Instance is { IsServer: true })
		{
			ApplyBuild(Multiplayer.GetRemoteSenderId(), barracksIndex, definitionId);
		}
	}

	[Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false,
		TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
	private void ClientCancelBuild(int barracksIndex)
	{
		if (NetworkManager.Instance is { IsServer: true })
		{
			ApplyCancelBuild(Multiplayer.GetRemoteSenderId(), barracksIndex);
		}
	}

	[Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false,
		TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
	private void ClientSetRally(int barracksIndex, Vector3 point)
	{
		if (NetworkManager.Instance is { IsServer: true })
		{
			ApplyRally(Multiplayer.GetRemoteSenderId(), barracksIndex, point);
		}
	}

	/// <summary>Returns true when the unit was actually charged for and queued.</summary>
	private bool ApplyBuild(int senderPeerId, int barracksIndex, byte definitionId)
	{
		Barracks barracks = ValidateBarracks(senderPeerId, barracksIndex);
		if (barracks == null || !UnitCatalog.TrySanitize(definitionId, out byte id))
		{
			RejectedOrders++;
			return false;
		}

		UnitDefinition definition = UnitCatalog.Definition(id);
		ResourceLedger ledger = CombatManager.Instance?.Match.Strategist;
		if (definition == null || ledger == null)
		{
			RejectedOrders++;
			return false;
		}

		return barracks.Queue.TryEnqueue(id, definition.Cost, definition.BuildTicks, ledger);
	}

	private void ApplyCancelBuild(int senderPeerId, int barracksIndex)
	{
		Barracks barracks = ValidateBarracks(senderPeerId, barracksIndex);
		barracks?.Queue.CancelLast(CombatManager.Instance?.Match.Strategist);
	}

	private void ApplyRally(int senderPeerId, int barracksIndex, Vector3 point)
	{
		Barracks barracks = ValidateBarracks(senderPeerId, barracksIndex);
		if (barracks != null)
		{
			barracks.RallyPoint = point;
		}
	}

	private Barracks ValidateBarracks(int senderPeerId, int index)
	{
		Team senderTeam = CombatManager.Instance?.TeamOf(senderPeerId) ?? Team.GroundForce;
		if (senderTeam != Team.Strategist)
		{
			return null;
		}

		Barracks barracks = BarracksAt(index);
		return barracks != null && barracks.Team == senderTeam ? barracks : null;
	}

	// ---- replication --------------------------------------------------------

	private void BroadcastUnitSnapshot(uint tick)
	{
		int peers = Multiplayer.HasMultiplayerPeer() ? Multiplayer.GetPeers().Length : 0;
		if (peers == 0)
		{
			return;
		}

		int count = 0;
		for (int i = 0; i < _ordered.Count && count < UnitSnapshotCodec.MaxUnits; i++)
		{
			Unit unit = _ordered[i];
			_snapshotScratch[count++] = new UnitSnapshot
			{
				UnitId = unit.UnitId,
				DefinitionId = unit.DefinitionId,
				Position = unit.GlobalPosition,
				Yaw = unit.Yaw,
				State = unit.State,
				Team = unit.Team,
				HealthPercent = unit.HealthPercent,
			};
		}

		// Still one packet for everyone. M4 put the fog in front of the *player*
		// snapshot, which is where the asymmetry is: every unit on the field is the
		// strategist's, so filtering this per peer would hide nothing from the side
		// that owns them, and hiding units from the ground force at a 7.5 Hz sphere
		// test would make them blink in and out of an FPS at forty metres
		// (docs/NETCODE.md §6.2).
		byte[] payload = UnitSnapshotCodec.Encode(tick, _snapshotScratch.AsSpan(0, count));
		Rpc(MethodName.ServerUnitSnapshot, payload);
		NetworkManager.Instance?.Stats.RecordSent(payload.Length * peers);
	}

	private void BroadcastBarracksState(uint tick)
	{
		int peers = Multiplayer.HasMultiplayerPeer() ? Multiplayer.GetPeers().Length : 0;
		if (peers == 0)
		{
			return;
		}

		for (int i = 0; i < _barracks.Count; i++)
		{
			BuildQueue queue = _barracks[i].Queue;
			Rpc(MethodName.ServerBarracksState, i, (byte)Mathf.Min(queue.Count, byte.MaxValue), queue.Head,
				(byte)Mathf.RoundToInt(queue.Progress(tick) * byte.MaxValue));
		}

		NetworkManager.Instance?.Stats.RecordSent(BarracksStateBytes * _barracks.Count * peers);
	}

	/// <summary>Server -> clients, unreliable, at <see cref="SimConfig.UnitSnapshotRate"/>.</summary>
	[Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false,
		TransferMode = MultiplayerPeer.TransferModeEnum.Unreliable)]
	private void ServerUnitSnapshot(byte[] payload)
	{
		NetworkManager net = NetworkManager.Instance;
		if (net == null || !net.IsClient)
		{
			return;
		}

		net.Stats.RecordReceived(payload.Length);

		// Queued rather than applied here: this arrives on the idle frame and the
		// render clock it is sampled against only advances inside the physics step.
		_pendingSnapshots.Add(payload);
		if (_pendingSnapshots.Count > MaxPendingSnapshots)
		{
			_pendingSnapshots.RemoveAt(0);
		}
	}

	/// <summary>
	/// Server -> clients, reliable. Ids are sent as <c>int</c> rather than
	/// <c>ushort</c> because that is the width every other id on this wire already
	/// uses, and a roster message costs four bytes once per unit per life.
	/// </summary>
	[Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false,
		TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
	private void ServerSpawnUnit(int unitId, byte definitionId, byte team, Vector3 position)
	{
		if (NetworkManager.Instance is not { IsClient: true }
			|| unitId <= 0 || unitId > ushort.MaxValue
			|| _units.ContainsKey((ushort)unitId))
		{
			return;
		}

		CreateUnit((ushort)unitId, definitionId, position, (Team)team, simulated: false);
		_interpolators[(ushort)unitId] = new SnapshotInterpolator();
	}

	[Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false,
		TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
	private void ServerDespawnUnit(int unitId)
	{
		if (NetworkManager.Instance is { IsClient: true } && unitId > 0 && unitId <= ushort.MaxValue)
		{
			Despawn((ushort)unitId);
		}
	}

	[Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false,
		TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
	private void ServerBarracksState(int index, byte queued, byte head, byte progress)
	{
		NetworkManager net = NetworkManager.Instance;
		if (net == null || !net.IsClient)
		{
			return;
		}

		net.Stats.RecordReceived(BarracksStateBytes);

		if (index < 0 || index >= _barracksStatus.Length)
		{
			return;
		}

		_barracksStatus[index] = new BarracksStatus
		{
			Queued = queued,
			Head = head,
			Progress = progress / (float)byte.MaxValue,
		};
	}

	private void ApplyPendingSnapshots()
	{
		for (int s = 0; s < _pendingSnapshots.Count; s++)
		{
			if (!UnitSnapshotCodec.TryDecode(_pendingSnapshots[s], _snapshotScratch, out uint tick, out int count))
			{
				continue;
			}

			for (int i = 0; i < count; i++)
			{
				ref UnitSnapshot snapshot = ref _snapshotScratch[i];
				Unit unit = Find(snapshot.UnitId);
				if (unit == null)
				{
					// The spawn message has not arrived yet; the next snapshot will do.
					continue;
				}

				unit.ApplyRemoteState(snapshot.State, snapshot.HealthPercent);
				if (_interpolators.TryGetValue(snapshot.UnitId, out SnapshotInterpolator interpolator))
				{
					interpolator.Push(tick, snapshot.Position, snapshot.Yaw, 0f);
				}
			}
		}

		_pendingSnapshots.Clear();
	}

	/// <summary>
	/// The roster a joining peer needs before any snapshot means anything, plus the
	/// state of every barracks.
	/// </summary>
	private void OnPeerJoined(int peerId)
	{
		if (NetworkManager.Instance is not { IsServer: true })
		{
			return;
		}

		for (int i = 0; i < _ordered.Count; i++)
		{
			Unit unit = _ordered[i];
			RpcId(peerId, MethodName.ServerSpawnUnit, (int)unit.UnitId, unit.DefinitionId, (byte)unit.Team,
				unit.GlobalPosition);
		}

		for (int i = 0; i < _barracks.Count; i++)
		{
			BuildQueue queue = _barracks[i].Queue;
			RpcId(peerId, MethodName.ServerBarracksState, i, (byte)Mathf.Min(queue.Count, byte.MaxValue),
				queue.Head, (byte)0);
		}
	}

	// ---- roster -------------------------------------------------------------

	private Unit CreateUnit(ushort id, byte definitionId, Vector3 position, Team team, bool simulated)
	{
		var unit = new Unit
		{
			Name = $"Unit_{id}",
			UnitId = id,
			DefinitionId = definitionId,
			Definition = UnitCatalog.Definition(definitionId),
			Team = team,
			IsSimulated = simulated,
			Position = position,
		};

		_unitsRoot.AddChild(unit);
		_units[id] = unit;
		_ordered.Add(unit);
		return unit;
	}

	private void Despawn(ushort unitId)
	{
		if (!_units.Remove(unitId, out Unit unit))
		{
			return;
		}

		_ordered.Remove(unit);
		_interpolators.Remove(unitId);
		unit.QueueFree();
	}

	/// <summary>Clears the field. Called between rounds, on the server.</summary>
	public void ClearUnits()
	{
		for (int i = _ordered.Count - 1; i >= 0; i--)
		{
			ushort id = _ordered[i].UnitId;
			Despawn(id);
			if (Multiplayer.HasMultiplayerPeer() && Multiplayer.GetPeers().Length > 0)
			{
				Rpc(MethodName.ServerDespawnUnit, (int)id);
			}
		}

		for (int i = 0; i < _barracks.Count; i++)
		{
			_barracks[i].Queue.Clear();
		}

		UnitsProduced = 0;
		UnitsLost = 0;
	}

	/// <summary>
	/// The barracks come from the map, as nodes in the <c>barracks</c> group,
	/// sorted by name: an order every peer derives from the same scene, because the
	/// index is what an order names on the wire.
	/// </summary>
	private void CollectBarracks()
	{
		foreach (Node node in GetTree().GetNodesInGroup(Barracks.Group))
		{
			if (node is Barracks barracks)
			{
				_barracks.Add(barracks);
			}
		}

		_barracks.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
		_barracksStatus = new BarracksStatus[_barracks.Count];

		if (_barracks.Count == 0)
		{
			GD.PushWarning($"[rts] no nodes in group '{Barracks.Group}'; the strategist has nothing to build from");
		}
	}

	/// <summary>
	/// What a client knows about a barracks. On a server this is read straight from
	/// the queue instead, so the HUD has one shape either way.
	/// </summary>
	public BarracksStatus StatusOf(int index, uint tick)
	{
		Barracks barracks = BarracksAt(index);
		if (barracks != null && NetworkManager.Instance is { IsServer: true })
		{
			return new BarracksStatus
			{
				Queued = (byte)Mathf.Min(barracks.Queue.Count, byte.MaxValue),
				Head = barracks.Queue.Head,
				Progress = barracks.Queue.Progress(tick),
			};
		}

		return index >= 0 && index < _barracksStatus.Length ? _barracksStatus[index] : default;
	}

	// ---- navigation ---------------------------------------------------------

	/// <summary>
	/// Bakes the map's navigation mesh, once, on the server.
	///
	/// Baked at runtime rather than shipped in the scene because the greybox map is
	/// edited constantly and a stale baked mesh is a bug that looks like a pathing
	/// bug. It is synchronous on purpose: this runs on the first server tick, before
	/// anyone has connected, and a unit that spawns into a half-baked map paths
	/// through a wall.
	/// </summary>
	private void BakeNavigation()
	{
		NavigationRegion3D region = null;
		foreach (Node node in GetTree().GetNodesInGroup(NavigationGroup))
		{
			if (node is NavigationRegion3D found)
			{
				region = found;
				break;
			}
		}

		if (region?.NavigationMesh is not NavigationMesh mesh)
		{
			GD.PushWarning($"[rts] no NavigationRegion3D in group '{NavigationGroup}';"
				+ " units will steer straight at their destination");
			return;
		}

		UnitDefinition infantry = UnitCatalog.Definition(UnitCatalog.Infantry);

		// Baked for the rifleman, and used by all three of M5's tiers. Godot bakes one
		// mesh per region and an agent radius is baked in, so a tank at 1.6 m will path
		// through gaps it does not fit through and shove itself out of them. The
		// alternative is a second region and a second bake per tier, which is a real
		// cost for a greybox whose map is mostly open ground — so this is a deliberate
		// cut, and the thing to fix first if vehicles start wedging in doorways
		// (docs/IMPLEMENTATION_PLAN.md §M5).
		//
		// Set here rather than in the scene so the numbers are compile-checked and so
		// they track the unit they exist for.
		//
		// The two cell sizes divide the infantry's radius, height and climb exactly
		// (0.4/0.2, 1.8/0.1, 0.5/0.1). Anything else is ceiled to whole voxels and the
		// engine warns about the precision it lost doing it. They must also match
		// `navigation/3d/default_cell_*` in project.godot, or the map rasterizes the
		// baked mesh's edges at a different resolution from the one it was baked at.
		mesh.CellSize = 0.2f;
		mesh.CellHeight = 0.1f;
		mesh.AgentRadius = infantry?.RadiusMeters ?? 0.4f;
		mesh.AgentHeight = infantry?.HeightMeters ?? 1.8f;
		mesh.AgentMaxClimb = 0.5f;
		mesh.AgentMaxSlope = 45f;

		// Colliders only, not meshes. A mesh source would make the bake read geometry
		// back off the GPU, which the engine warns about and which a headless server
		// exported with Strip Visuals does not have in the first place
		// (docs/DEPLOYMENT.md §2). Everything a unit can walk on or into is a collider.
		mesh.GeometryParsedGeometryType = NavigationMesh.ParsedGeometryType.StaticColliders;
		mesh.GeometryCollisionMask = CollisionLayers.World;
		mesh.GeometrySourceGeometryMode = NavigationMesh.SourceGeometryMode.GroupsWithChildren;
		mesh.GeometrySourceGroupName = "navmesh";

		region.BakeNavigationMesh(onThread: false);

		NavigationReady = mesh.GetVertices().Length > 0;
		GD.Print(NavigationReady
			? $"[rts] navigation baked: {mesh.GetVertices().Length} vertices"
			: "[rts] navigation bake produced no geometry; units will steer straight at their destination");
	}

	private bool HasLineOfSight(Vector3 from, Vector3 to)
	{
		PhysicsDirectSpaceState3D space = GetTree()?.Root?.World3D?.DirectSpaceState;
		if (space == null)
		{
			return true;
		}

		var query = PhysicsRayQueryParameters3D.Create(from, to, CollisionLayers.World);
		return space.IntersectRay(query).Count == 0;
	}
}
