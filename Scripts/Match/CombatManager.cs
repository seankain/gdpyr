using System;
using System.Collections.Generic;
using Gdpyr.Core;
using Gdpyr.Fps;
using Gdpyr.Net;
using Gdpyr.Rts;
using Gdpyr.Sim;
using Gdpyr.Sim.Agent;
using Gdpyr.Ui;
using Godot;

namespace Gdpyr.Match;

/// <summary>
/// Fourth autoload: weapons, projectiles, damage, death and the round.
///
/// The server is the only authority here. It runs every player's weapon over the
/// inputs it received, puts the resulting projectiles in the air, steps them,
/// decides what they hit, and charges the ground force a ticket for each death.
/// Clients run the same weapon function on their own input so a tracer and an ammo
/// count appear on the tick the trigger was pulled (docs/NETCODE.md §4.3), and
/// otherwise do as they are told.
///
/// Like <see cref="NetworkManager"/>, it never ticks itself: <see cref="PlayerManager"/>
/// drives it, so the order of "simulate the character", "fire its weapon" and
/// "step the projectiles" is written down rather than inherited from the autoload
/// list.
/// </summary>
public partial class CombatManager : Node
{
	public static CombatManager Instance { get; private set; }

	private const string GameModePath = "res://Match/default_gamemode.tres";

	/// <summary>
	/// Marks a client's own predicted tracer. Server ids start at 1 and count up, so
	/// the high bit is free and the two spaces cannot collide.
	/// </summary>
	private const uint PredictedIdBit = 0x8000_0000u;

	/// <summary>
	/// How far a predicted tracer's spawn tick may be from the authoritative one and
	/// still be the same shot. A client's input clock runs ahead of the server's by
	/// its latency, and the two estimates of "now" are never exactly equal.
	/// </summary>
	private const int PredictionMatchWindowTicks = 8;

	/// <summary>
	/// How long a predicted tracer waits to be confirmed before it is dropped. Two
	/// seconds is far longer than any playable round trip.
	/// </summary>
	private const int PredictionExpiryTicks = SimConfig.TickRate * 2;

	/// <summary>Messages arriving on the idle frame wait for the physics step, as snapshots do.</summary>
	private const int MaxPendingMessages = 64;

	/// <summary>Bytes counted for the match-state RPC, for the bandwidth HUD.</summary>
	private const int MatchStateBytes = 22;

	/// <summary>Bytes counted for one resource node's state RPC.</summary>
	private const int NodeStateBytes = 8;

	private readonly Dictionary<int, PlayerCombat> _players = new();
	private readonly List<PlayerCombat> _ordered = new();
	private readonly List<byte[]> _pendingSpawns = new();
	private readonly List<byte[]> _pendingHits = new();

	/// <summary>Client-side: tracers fired locally and not yet matched to the server's account of them.</summary>
	private readonly List<(uint Id, uint Tick)> _predicted = new();

	private ProjectileSim _projectiles;
	private GameModeDefinition _gameMode;
	private ProjectileView _view;
	private CombatHud _hud;
	private TeamSelect _teamSelect;
	private RoleSelect _roleSelect;
	private Scoreboard _scoreboard;
	private StrategistController _strategist;

	private uint[] _replicatedNodeVersions = Array.Empty<uint>();

	/// <summary>The end-of-round table, on every peer. Empty until a round ends.</summary>
	private readonly ScoreEntry[] _scores = new ScoreEntry[ScoreboardCodec.MaxEntries];

	private RoundSummary _summary;
	private int _scoreCount;

	private uint _nextPredictedId = 1;
	private uint _replicatedMatchVersion;
	private uint _replicatedPointsVersion;
	private bool _economyStarted;
	private uint _intermissionEndTick;
	private bool _intermissionArmed;

	/// <summary>The round. Server-authoritative; clients hold a replicated copy.</summary>
	public MatchState Match { get; } = new();

	/// <summary>
	/// Who is on which side. Server-side: a client learns its own team from the
	/// snapshot's flag byte and everyone else's the same way
	/// (docs/NETCODE.md §7), so this dictionary is only ever consulted here.
	/// </summary>
	public TeamService Teams { get; } = new();

	/// <summary>
	/// What the strategist is allowed to know (docs/IMPLEMENTATION_PLAN.md §M4).
	/// Server-side: it decides which records go in which peer's snapshot. A client
	/// holds one too but never recomputes it — on a listen host the local
	/// strategist reads it to draw its own fog, which is a curtain rather than a
	/// filter (docs/NETCODE.md §6.2).
	/// </summary>
	public VisibilityService Visibility { get; private set; }

	/// <summary>
	/// The strategist's income and the nodes it comes from
	/// (docs/IMPLEMENTATION_PLAN.md §M5). Ticked on the server; a client holds the
	/// same nodes and is told who owns each of them.
	/// </summary>
	public EconomyService Economy { get; } = new();

	/// <summary>Projectiles in flight in this process. For the net HUD (docs/NETCODE.md §8).</summary>
	public int LiveProjectiles => _projectiles?.LiveCount ?? 0;

	/// <summary>Shots this process has put in the air. For the net HUD.</summary>
	public int ShotsFired { get; private set; }

	/// <summary>Hits the server has resolved. Server-side only.</summary>
	public int HitsResolved { get; private set; }

	/// <summary>Projectiles refused because the pool was full. Anything but 0 is worth seeing.</summary>
	public int ProjectileOverflows => _projectiles?.Overflows ?? 0;

	public GameModeDefinition GameMode => _gameMode;

	public PlayerCombat Local { get; private set; }

	public override void _Ready()
	{
		Instance = this;
		ProcessMode = ProcessModeEnum.Always;

		WeaponCatalog.Load();
		_projectiles = new ProjectileSim(WeaponCatalog.Projectiles);

		// The ray is handed over rather than reached for: the fog is a Match concern
		// and which physics world answers "is there a wall in the way" is this
		// node's.
		Visibility = new VisibilityService(HasLineOfSight);

		_gameMode = GD.Load<GameModeDefinition>(GameModePath) ?? new GameModeDefinition();

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

	// ---- roster, driven by PlayerManager -----------------------------------

	public PlayerCombat Register(int peerId, fps_controller character, bool isLocal)
	{
		if (_players.TryGetValue(peerId, out PlayerCombat existing))
		{
			existing.Character = character;
			return existing;
		}

		var combat = new PlayerCombat(peerId, character);
		_players[peerId] = combat;
		_ordered.Add(combat);

		if (isLocal)
		{
			Local = combat;
			EnsureLocalUi();

			// The server asks a peer for its side while it is registering it, which on
			// a client is before the spawn message it is about to send. Anything that
			// arrived early is applied here, so a character is never briefly predicted
			// as alive while the server is holding it (docs/NETCODE.md §3.2).
			combat.AwaitingRole = LocalAwaitingRole;
		}

		// The authority, which offline is too: there is one there and it is this
		// process. Only a client is told what its team is instead of deciding.
		// Registered after Local, so that the local player's question is put to the
		// menu in this process rather than sent to it as an RPC.
		if (NetworkManager.Instance is { IsClient: false })
		{
			// A person is asked which side they want before they are put anywhere
			// (docs/IMPLEMENTATION_PLAN.md §M3). A computer player is not: it has no
			// menu to answer with, and the director assigns it a side on this tick.
			if (Teams.RequireChoice(peerId))
			{
				combat.Team = Teams.TeamOf(peerId);
				RequireRoleChoice(combat);
			}
			else
			{
				// Everyone arrives on the ground; the strategist slot is opted into.
				combat.Team = Teams.Assign(peerId, Team.GroundForce);
			}
		}

		return combat;
	}

	public void Unregister(int peerId)
	{
		Teams.Remove(peerId);
		Visibility.Forget(peerId);

		if (!_players.Remove(peerId, out PlayerCombat combat))
		{
			return;
		}

		_ordered.Remove(combat);
		if (Local == combat)
		{
			Local = null;
			LocalAwaitingRole = false;
			SyncLocalRole();
		}
	}

	public PlayerCombat Find(int peerId) => _players.GetValueOrDefault(peerId);

	/// <summary>
	/// The roster, as an index rather than an enumerator: <see cref="Rts.UnitManager"/>
	/// walks it once per unit scan, and the plan is explicit that no per-entity query
	/// may allocate or scan the scene (docs/IMPLEMENTATION_PLAN.md §1, §6).
	/// </summary>
	public int PlayerCount => _ordered.Count;

	public PlayerCombat PlayerAt(int index) => index >= 0 && index < _ordered.Count ? _ordered[index] : null;

	/// <summary>
	/// Whether this peer's character should be simulating its own movement. A dead
	/// player is fed a neutral frame instead of their input, on the server and on
	/// their own client alike, so the two agree about a corpse standing still. A
	/// strategist is never alive, which is how their body comes to stand still too.
	/// </summary>
	public bool IsAlive(int peerId) => Find(peerId)?.IsAlive ?? true;

	/// <summary>Which side a peer is on. Unknown peers are on the ground, like everyone else by default.</summary>
	public Team TeamOf(int peerId) => Find(peerId)?.Team ?? Team.GroundForce;

	// ---- server ------------------------------------------------------------

	/// <summary>
	/// Runs one player's weapons for a tick. Called immediately after that player's
	/// character has been simulated for the same tick, with the same input.
	/// </summary>
	public void ServerSimulate(int peerId, in InputContext input, uint tick)
	{
		PlayerCombat combat = Find(peerId);
		if (combat == null)
		{
			return;
		}

		if (!combat.IsAlive)
		{
			// A player who is off the field still has sights to lower: their frame is
			// look-only by then (InputFrame.LookOnly), so the same step that raised them
			// brings them down over the same ticks rather than snapping the view back
			// the instant they are killed.
			Ads.Step(ref combat.Aim, combat.EquippedAim, input);
			return;
		}

		EmplacementManager emplacements = EmplacementManager.Instance;
		emplacements?.ServerUse(combat, input, tick);

		// A gunner's own weapons are not in their hands (docs/IMPLEMENTATION_PLAN.md
		// §M5): while they are behind a gun, the trigger fires the gun.
		if (emplacements != null && emplacements.TryMounted(peerId, out Emplacement mounted))
		{
			// A mounted gun is aimed over its own sights, which is what the traverse arc
			// already is; whatever the gunner had up is put away with the weapon.
			Ads.Step(ref combat.Aim, AimStats.None, input);
			FireMounted(combat, mounted, input, tick, authoritative: true);
			return;
		}

		combat.Slot = WeaponSim.SelectSlot(combat.Slot, input);

		// After the slot, so that the weapon that has just come up is the one whose
		// raise this tick counts against.
		Ads.Step(ref combat.Aim, combat.EquippedAim, input);

		WeaponStats stats = combat.EquippedStats;
		if (WeaponSim.Step(ref combat.Equipped, stats, input, tick) != WeaponAction.Fire)
		{
			return;
		}

		combat.ShotsFired++;

		if (stats.IsMelee)
		{
			ShotsFired++;
			ResolveMelee(combat, stats, tick);
			return;
		}

		// The process-wide counter is bumped by SpawnProjectile, which is also what
		// a unit's shot goes through, so the HUD's "shots fired" is every round this
		// process put in the air and not only the players'.
		Fire(combat, tick);
	}

	/// <summary>
	/// Closes the server's tick: records where everyone was, flies the projectiles,
	/// resolves what they hit, brings the dead back and runs the round's clock.
	/// </summary>
	public void ServerPostTick(uint tick)
	{
		EnsureEconomy();

		RecordHitboxes(tick);
		StepProjectiles(tick, authoritative: true);
		ServerRespawns(tick);

		// Before the round is judged, not after: the strategist's defeat condition
		// reads both the balance the nodes have just paid into and which nodes are
		// held (docs/IMPLEMENTATION_PLAN.md §M5).
		Economy.ServerTick(tick, this, UnitManager.Instance);
		BroadcastNodeState(tick);

		ServerRound(tick);

		// After the units have moved and before PlayerManager broadcasts the snapshot
		// this decides the contents of (docs/IMPLEMENTATION_PLAN.md §M4).
		Visibility.ServerTick(tick, this, UnitManager.Instance);

		ReplicateMatchState();
		UpdateHud(tick);
	}

	private void RecordHitboxes(uint tick)
	{
		NetworkManager net = NetworkManager.Instance;

		for (int i = 0; i < _ordered.Count; i++)
		{
			PlayerCombat combat = _ordered[i];

			// Refreshed for everyone, alive or not: a player who respawns this tick
			// shoots on the next one, and would otherwise do it uncompensated.
			combat.LagCompensationTicks = net?.LagCompensationTicks(combat.PeerId) ?? 0;

			if (combat.Character == null || !combat.IsAlive)
			{
				continue;
			}

			combat.History.Record(tick, combat.Character.Hitbox);
		}
	}

	private void Fire(PlayerCombat combat, uint tick)
	{
		byte definitionId = combat.EquippedDefinitionId;

		// The cone is applied from the weapon state the step just advanced, so the
		// owning client's predicted tracer computes the identical direction from the
		// identical shot index (docs/IMPLEMENTATION_PLAN.md §7).
		Vector3 direction = WeaponSim.FireDirection(combat.Equipped, combat.EquippedStats, combat.PeerId,
			combat.Character.AimDirection);

		// Lag compensation, the cheap way (docs/NETCODE.md §4.3): the round starts its
		// life the shooter's one-way latency further along, so it arrives where they
		// aimed rather than where they would have had to lead to allow for the link.
		SpawnProjectile(tick, OwnerId.ForPeer(combat.PeerId), definitionId, combat.Character.EyePosition,
			direction, combat.LagCompensationTicks);
	}

	/// <summary>
	/// Runs a deployed heavy gun over its gunner's input
	/// (docs/IMPLEMENTATION_PLAN.md §M5).
	///
	/// The same <see cref="WeaponSim"/> step a rifle goes through, over the same
	/// frame, on both ends — so the belt count and the tracer appear on the tick the
	/// trigger was pulled and the server confirms them a round trip later
	/// (docs/NETCODE.md §4.3). What differs is where the round comes from and where
	/// it may be pointed: the muzzle rather than the gunner's eye, and inside the
	/// traverse arc the gun was deployed at rather than wherever they are looking.
	/// </summary>
	private void FireMounted(PlayerCombat combat, Emplacement gun, in InputContext input, uint tick,
		bool authoritative)
	{
		WeaponStats stats = gun.Stats;

		// The gunner's client steps the same belt the server does and keeps its own
		// count: ServerGunState will not overwrite it while this peer is the gunner,
		// because the server's number is a round trip old (docs/NETCODE.md §10.2).
		WeaponAction action = WeaponSim.Step(ref gun.Weapon, stats, input, tick);

		if (action != WeaponAction.Fire)
		{
			return;
		}

		combat.ShotsFired++;

		Vector3 aim = EmplacementSim.Traverse(combat.Character.Yaw, combat.Character.Pitch, gun.DeployYaw);
		Vector3 direction = WeaponSim.FireDirection(gun.Weapon, stats, combat.PeerId, aim);

		if (authoritative)
		{
			SpawnProjectile(tick, OwnerId.ForPeer(combat.PeerId), gun.WeaponDefinitionId, gun.MuzzlePosition,
				direction, combat.LagCompensationTicks);
			return;
		}

		ShotsFired++;

		uint id = PredictedIdBit | _nextPredictedId++;
		if (_projectiles.TrySpawn(id, tick, OwnerId.ForPeer(combat.PeerId), gun.WeaponDefinitionId,
			gun.MuzzlePosition, direction, 0, out _))
		{
			_predicted.Add((id, tick));
		}
	}

	/// <summary>
	/// Puts one authoritative round in the air and tells every client about it.
	///
	/// Shared by players and by units, because units fire the *same* projectiles
	/// (docs/IMPLEMENTATION_PLAN.md §M3): one pool, one integrator, one spawn
	/// message, and one place where "what does a shot cost on the wire" is answered.
	/// <paramref name="ownerId"/> is an <see cref="OwnerId"/>, so it names either.
	/// </summary>
	public uint SpawnProjectile(uint tick, int ownerId, byte definitionId, Vector3 origin, Vector3 direction,
		int catchUpTicks)
	{
		uint id = _projectiles.Spawn(tick, ownerId, definitionId, origin, direction, catchUpTicks);

		if (id == 0)
		{
			GD.PushWarning($"[combat] projectile pool full; dropped a shot from owner {ownerId}");
			return 0;
		}

		ShotsFired++;

		Broadcast(MethodName.ServerProjectileSpawn, ProjectileCodec.EncodeSpawn(new ProjectileSpawn
		{
			Id = id,
			SpawnTick = tick,
			OwnerPeerId = ownerId,
			DefinitionId = definitionId,
			Origin = origin,
			Direction = direction,
		}), origin);

		return id;
	}

	/// <summary>
	/// A melee swing: instantaneous, so it is the one attack for which rewinding the
	/// target to the attacker's view of the world is exactly right, and the reason
	/// the hitbox ring exists at this milestone (docs/NETCODE.md §5).
	/// </summary>
	private void ResolveMelee(PlayerCombat attacker, in WeaponStats stats, uint tick)
	{
		Vector3 origin = attacker.Character.EyePosition;
		Vector3 to = origin + (attacker.Character.AimDirection * stats.RangeMeters);
		uint rewindTick = tick - (uint)Math.Min(attacker.LagCompensationTicks, (int)tick);

		float nearest = WorldHitFraction(origin, to, out Vector3 wallPoint, out bool blocked);
		PlayerCombat victim = null;
		Vector3 point = blocked ? wallPoint : to;

		for (int i = 0; i < _ordered.Count; i++)
		{
			PlayerCombat target = _ordered[i];
			if (target == attacker || !target.IsAlive || !target.History.TrySampleOrLatest(rewindTick, out HitCapsule capsule))
			{
				continue;
			}

			if (Hitbox.SegmentIntersects(capsule, origin, to, stats.SweepRadiusMeters, out float t) && t < nearest)
			{
				nearest = t;
				victim = target;
				point = origin + ((to - origin) * t);
			}
		}

		// Units are not rewound: they have no client whose view of them a swing has to
		// be validated against, and at 4.5 m/s half a second of rewind is worth two
		// metres of a capsule that is 0.8 m wide.
		Unit unitVictim = null;
		if (UnitManager.Instance is { } units)
		{
			unitVictim = units.QuerySegment(origin, to, stats.SweepRadiusMeters,
				OwnerId.ForPeer(attacker.PeerId), attacker.Team, ref nearest, out Vector3 unitPoint);

			if (unitVictim != null)
			{
				// Nearer than whatever the player loop found, or QuerySegment would not
				// have returned it.
				victim = null;
				point = unitPoint;
			}
		}

		HitFlags flags = HitFlags.Melee;
		if (victim != null)
		{
			flags |= Damage(victim, stats.Damage, attacker, OwnerId.ForPeer(attacker.PeerId), tick);
		}
		else if (unitVictim != null)
		{
			flags |= DamageUnit(unitVictim, stats.Damage, attacker, OwnerId.ForPeer(attacker.PeerId), tick);
		}
		else if (!blocked)
		{
			// A swing that connected with nothing is still worth telling clients
			// about: it is the only feedback the animation has.
			point = to;
		}

		Broadcast(MethodName.ServerProjectileHit, ProjectileCodec.EncodeHit(new ProjectileHit
		{
			Id = 0,
			Point = point,
			VictimPeerId = victim?.PeerId ?? (unitVictim != null ? OwnerId.ForUnit(unitVictim.UnitId) : 0),
			Flags = flags,
		}), point);
	}

	/// <summary>
	/// Flies every projectile a tick and, on the server, decides what each one hit.
	///
	/// World geometry is the engine's problem — a ray query against the world layer —
	/// and players are ours, tested analytically against the capsules recorded this
	/// tick (docs/NETCODE.md §5). Splitting them that way is what lets a hit that
	/// killed someone be re-derived later without an engine.
	/// </summary>
	private void StepProjectiles(uint tick, bool authoritative)
	{
		_projectiles.Step(SimConfig.TickDelta);

		if (!authoritative)
		{
			return;
		}

		ReadOnlySpan<ProjectileSegment> segments = _projectiles.Segments;
		for (int i = 0; i < segments.Length; i++)
		{
			ResolveSegment(segments[i], tick);
		}
	}

	private void ResolveSegment(in ProjectileSegment segment, uint tick)
	{
		ProjectileStats stats = _projectiles.StatsFor(segment.DefinitionId);
		int ownerId = segment.OwnerPeerId;
		Team ownerTeam = TeamOfOwner(ownerId);

		float nearest = WorldHitFraction(segment.From, segment.To, out Vector3 point, out bool blocked);
		PlayerCombat victim = null;

		for (int i = 0; i < _ordered.Count; i++)
		{
			PlayerCombat target = _ordered[i];

			// The shooter's own capsule contains the muzzle, so a round would kill its
			// owner on the tick it was fired.
			if (OwnerId.ForPeer(target.PeerId) == ownerId || !target.IsAlive)
			{
				continue;
			}

			if (!target.History.TrySample(tick, out HitCapsule capsule))
			{
				continue;
			}

			if (Hitbox.SegmentIntersects(capsule, segment.From, segment.To, segment.SweepRadius, out float t)
				&& t < nearest)
			{
				nearest = t;
				victim = target;
				blocked = true;
				point = segment.PointAt(t);
			}
		}

		// Units are resolved after players and against the same fraction, so whichever
		// the round reaches first wins however they are ordered in memory.
		Unit unitVictim = null;
		if (UnitManager.Instance is { } units)
		{
			unitVictim = units.QuerySegment(segment.From, segment.To, segment.SweepRadius, ownerId, ownerTeam,
				ref nearest, out Vector3 unitPoint);

			if (unitVictim != null)
			{
				victim = null;
				blocked = true;
				point = unitPoint;
			}
		}

		if (!blocked)
		{
			return;
		}

		_projectiles.Kill(segment.Id);
		HitsResolved++;

		PlayerCombat attacker = Find(OwnerId.PeerOf(ownerId));
		HitFlags flags = HitFlags.None;

		if (victim != null)
		{
			flags |= Damage(victim, stats.Damage, attacker, ownerId, tick);
		}
		else if (unitVictim != null)
		{
			flags |= DamageUnit(unitVictim, stats.Damage, attacker, ownerId, tick);
		}

		if (stats.IsExplosive)
		{
			flags |= HitFlags.Exploded;
			flags |= Explode(point, stats, attacker, ownerId, ownerTeam, victim, unitVictim, tick);
		}

		Broadcast(MethodName.ServerProjectileHit, ProjectileCodec.EncodeHit(new ProjectileHit
		{
			Id = segment.Id,
			Point = point,
			VictimPeerId = victim?.PeerId ?? (unitVictim != null ? OwnerId.ForUnit(unitVictim.UnitId) : 0),
			Flags = flags,
		}), point);
	}

	/// <summary>
	/// Whose round this is. A projectile carries one owner id across both id spaces
	/// (<see cref="OwnerId"/>), and every friendly-fire question downstream is
	/// really a question about this.
	/// </summary>
	private Team TeamOfOwner(int ownerId)
	{
		if (OwnerId.IsUnit(ownerId))
		{
			return UnitManager.Instance?.Find(OwnerId.UnitOf(ownerId))?.Team ?? Team.Strategist;
		}

		return TeamOf(OwnerId.PeerOf(ownerId));
	}

	/// <summary>
	/// Blast damage, to everyone in reach including whoever fired it. Self-damage is
	/// deliberate: a launcher that is safe at point blank is a shotgun.
	/// </summary>
	private HitFlags Explode(Vector3 point, in ProjectileStats stats, PlayerCombat attacker, int attackerOwnerId,
		Team attackerTeam, PlayerCombat direct, Unit directUnit, uint tick)
	{
		HitFlags flags = HitFlags.None;

		for (int i = 0; i < _ordered.Count; i++)
		{
			PlayerCombat target = _ordered[i];
			if (target == direct || !target.IsAlive || !target.History.TrySampleOrLatest(tick, out HitCapsule capsule))
			{
				continue;
			}

			float distance = Mathf.Max(0f, Mathf.Sqrt(Hitbox.DistanceToAxisSquared(capsule, point)) - capsule.Radius);
			float damage = stats.SplashDamageAt(distance);
			if (damage > 0f)
			{
				flags |= Damage(target, damage, attacker, attackerOwnerId, tick);
			}
		}

		UnitManager units = UnitManager.Instance;
		for (int i = 0; units != null && i < units.SlotCount; i++)
		{
			Unit unit = units.UnitAt(i);

			// A blast spares its own side, unlike the self-damage above: a launcher
			// that clears a room is the point, and one that also clears the room's
			// owner's infantry is a different weapon.
			if (unit == null || unit == directUnit || !unit.IsAlive || unit.Team == attackerTeam)
			{
				continue;
			}

			float damage = stats.SplashDamageAt(UnitManager.DistanceToBlast(unit, point));
			if (damage > 0f)
			{
				flags |= DamageUnit(unit, damage, attacker, attackerOwnerId, tick);
			}
		}

		return flags;
	}

	/// <summary>
	/// Applies damage to a unit. Unlike a player's death this costs no ticket — the
	/// ground force's pool is theirs, and a unit is already paid for out of the
	/// strategist's points.
	/// </summary>
	private HitFlags DamageUnit(Unit unit, float amount, PlayerCombat attacker, int attackerOwnerId, uint tick)
	{
		UnitManager units = UnitManager.Instance;
		if (units == null || unit == null || !unit.IsAlive || amount <= 0f)
		{
			return HitFlags.None;
		}

		bool killed = units.Damage(unit, amount, attackerOwnerId, tick);
		ConfirmHit(attacker, null, killed);

		if (killed && attacker != null)
		{
			attacker.Kills++;
		}

		// A unit is named on the stream the way a projectile names it: one signed id
		// across both spaces, units in the negative half (see OwnerId). A ground
		// policy shoots units far more often than it shoots people, so a stream that
		// only recorded player damage would be a stream nothing could learn from.
		if (AgentEventBus.Active != null)
		{
			byte weapon = attacker?.EquippedDefinitionId ?? (byte)0;
			int victimId = OwnerId.ForUnit(unit.UnitId);
			AgentEventBus.Emit(AgentEventKind.Damage, tick, attackerOwnerId, victimId, weapon, amount,
				unit.Health);
			if (killed)
			{
				AgentEventBus.Emit(AgentEventKind.Kill, tick, attackerOwnerId, victimId, weapon,
					UnitSeparation(attacker, unit));
			}
		}

		return killed ? HitFlags.Killed : HitFlags.None;
	}

	/// <summary>How far the shooter was from a unit it killed. Zero when a unit or the world did it.</summary>
	private static float UnitSeparation(PlayerCombat attacker, Unit unit) =>
		attacker?.Character == null || unit == null
			? 0f
			: attacker.Character.SimPosition.DistanceTo(unit.GlobalPosition);

	/// <summary>
	/// Applies damage and, if that killed the victim, spends a ticket for it.
	///
	/// <paramref name="attackerOwnerId"/> is carried alongside
	/// <paramref name="attacker"/> because a unit has no <see cref="PlayerCombat"/>:
	/// it is null for every round a unit fires, and a kill log that called all of
	/// those "the world" would make the one record M8 derives its per-round CSV from
	/// useless the moment the strategist starts winning.
	/// </summary>
	private HitFlags Damage(PlayerCombat victim, float amount, PlayerCombat attacker, int attackerOwnerId,
		uint tick)
	{
		if (victim == null || !victim.IsAlive || amount <= 0f)
		{
			return HitFlags.None;
		}

		bool killed = victim.ApplyDamage(amount);
		ConfirmHit(attacker, victim, killed);

		// The event stream is what a trainer derives its own reward from, and what
		// M8's per-round CSV is written out of (docs/AGENT_API.md §8). It costs a
		// null check on a round nobody has attached to.
		if (AgentEventBus.Active != null)
		{
			byte weapon = attacker?.EquippedDefinitionId ?? (byte)0;
			float range = Separation(attacker, victim);
			AgentEventBus.Emit(AgentEventKind.Damage, tick, attackerOwnerId, victim.PeerId, weapon,
				amount, victim.Health);
			if (killed)
			{
				AgentEventBus.Emit(AgentEventKind.Kill, tick, attackerOwnerId, victim.PeerId, weapon, range);
			}
		}

		if (!killed)
		{
			return HitFlags.None;
		}

		victim.Deaths++;
		victim.RespawnTick = tick + (uint)WeaponStats.SecondsToTicks(_gameMode.RespawnSeconds);
		victim.Character?.SetDeadPresentation(true);
		victim.History.Clear();

		if (attacker != null && attacker != victim)
		{
			attacker.Kills++;
		}

		// A strategist has no body on the field, so every player death is a
		// ground-force death and costs the pool a ticket.
		Match.RegisterGroundDeath(tick);

		GD.Print($"[combat] peer {victim.PeerId} killed by {DescribeOwner(attackerOwnerId)}"
			+ $" | tickets {Match.GroundTickets}");
		return HitFlags.Killed;
	}

	/// <summary>
	/// How far apart two players were, for the kill record. Zero when the attacker
	/// was a unit or the world, which is the same "unknown" a null attacker means
	/// everywhere else on the stream.
	/// </summary>
	private static float Separation(PlayerCombat attacker, PlayerCombat victim)
	{
		if (attacker?.Character == null || victim?.Character == null)
		{
			return 0f;
		}

		return attacker.Character.SimPosition.DistanceTo(victim.Character.SimPosition);
	}

	/// <summary>Names whatever fired a round, for the server log.</summary>
	private string DescribeOwner(int ownerId)
	{
		if (OwnerId.IsUnit(ownerId))
		{
			ushort unitId = OwnerId.UnitOf(ownerId);
			return $"{UnitCatalog.NameOf(UnitManager.Instance?.Find(unitId)?.DefinitionId ?? 0)} {unitId}";
		}

		return OwnerId.IsPeer(ownerId) ? $"peer {OwnerId.PeerOf(ownerId)}" : "the world";
	}

	private void ServerRespawns(uint tick)
	{
		for (int i = 0; i < _ordered.Count; i++)
		{
			PlayerCombat combat = _ordered[i];

			// A strategist is never alive and never comes back: they have no body on
			// the field to put anywhere (see PlayerCombat.IsAlive). Neither does
			// somebody who has not said which of the two they want yet — the clock
			// that brings a body back is started by their answer, not by a timer.
			if (combat.Team == Team.Strategist || combat.AwaitingRole || combat.IsAlive
				|| tick < combat.RespawnTick)
			{
				continue;
			}

			// A round that is over does not put anyone back on the field; they come
			// back together when the next one starts.
			if (Match.Phase == RoundPhase.Ended)
			{
				continue;
			}

			Respawn(combat, tick);
		}
	}

	private void Respawn(PlayerCombat combat, uint tick)
	{
		if (combat.Team == Team.Strategist)
		{
			return;
		}

		combat.Respawn();
		combat.History.Clear();
		combat.Character?.SetDeadPresentation(false);
		PlayerManager.Instance?.TeleportToSpawn(combat.PeerId, combat.Deaths);
		GD.Print($"[combat] peer {combat.PeerId} respawned with {WeaponCatalog.NameOf(combat.Loadout.Large)}");
	}

	private void ServerRound(uint tick)
	{
		switch (Match.Phase)
		{
			// Nothing counts until somebody is on the field, so the round starts on the
			// first player rather than on the server's first tick — and a player who
			// is still looking at the role menu is not on it yet (docs/NETCODE.md §7).
			case RoundPhase.Warmup when Teams.ReadyCount > 0:
				Match.Start(tick, _gameMode.GroundForceTickets, _gameMode.StrategistTickets,
					_gameMode.RoundDurationMinutes * 60 * SimConfig.TickRate);
				AgentEventBus.Emit(AgentEventKind.RoundStart, tick, RoundSeed, Match.GroundTickets,
					Match.StrategistPoints, _gameMode.RoundDurationMinutes);
				GD.Print($"[match] round live | tickets {Match.GroundTickets}"
					+ $" | points {Match.StrategistPoints}"
					+ $" | {_gameMode.RoundDurationMinutes} minutes");
				break;

			case RoundPhase.Live:
				Match.Advance(tick);
				CheckStrategistDefeat(tick);
				break;
		}

		if (Match.Phase != RoundPhase.Ended)
		{
			return;
		}

		// A round ends on the clock here, or on the last ticket inside Damage(). The
		// intermission is armed off the phase so that both routes go through one path.
		if (!_intermissionArmed)
		{
			_intermissionArmed = true;
			_intermissionEndTick = tick + (uint)WeaponStats.SecondsToTicks(_gameMode.IntermissionSeconds);
			PublishScoreboard(tick, 0);
			AgentEventBus.Emit(AgentEventKind.RoundEnd, tick, (int)Match.Outcome, Match.GroundTickets,
				UnitManager.Instance?.UnitsLost ?? 0, _summary.Minutes,
				UnitManager.Instance?.UnitsProduced ?? 0);
			GD.Print($"[match] round over: {Match.Outcome} | winner {Match.Winner?.ToString() ?? "nobody"}"
				+ $" | tickets {Match.GroundTickets} | deaths {Match.GroundDeaths}"
				+ $" | next round in {_gameMode.IntermissionSeconds:0}s");
			return;
		}

		if (tick < _intermissionEndTick)
		{
			return;
		}

		RestartRound(tick);
	}

	/// <summary>
	/// Wipes the field and puts the round back in warmup, which the next tick with
	/// anybody on it turns live again.
	///
	/// Extracted from the intermission so that the agent API's <c>reset</c> and the
	/// clock arrive at the same state by the same path (docs/AGENT_API.md §7.1): a
	/// training loop that reset the round through a shortcut would eventually be
	/// training against a round shape no playtest ever produces.
	/// </summary>
	private void RestartRound(uint tick)
	{
		_intermissionArmed = false;
		_scoreCount = 0;
		Match.Reset();
		_projectiles.Clear();
		Visibility.Clear();
		Economy.Reset();
		SendNodeState(0);
		EmplacementManager.Instance?.ResetAll();
		UnitManager.Instance?.ClearUnits();

		// A fresh round asks the people here which side they want to play it on,
		// before anybody is put back on the field (docs/IMPLEMENTATION_PLAN.md §M3).
		// The bots are not asked and are simply put back; the director will re-plan
		// the seats around whatever the people choose.
		Teams.RequireChoiceFromPeople();

		for (int i = 0; i < _ordered.Count; i++)
		{
			// A fresh round is a fresh scoreboard. Without this the table published at
			// the end of round two carries round one's kills, and every player
			// respawns at their own spawn point offset by a death count from a round
			// that is over — so no two rounds even start in the same places.
			_ordered[i].Kills = 0;
			_ordered[i].Deaths = 0;

			if (Teams.AwaitingChoice(_ordered[i].PeerId))
			{
				RequireRoleChoice(_ordered[i]);
				continue;
			}

			Respawn(_ordered[i], tick);
		}
	}

	/// <summary>
	/// Ends the round now and starts a fresh one — what the agent API's
	/// <c>reset</c> does (docs/AGENT_API.md §7.1).
	///
	/// A live round is ended as undecided and its scoreboard is published, so a
	/// policy's episode terminates with the same <c>RoundSummary</c> a human's
	/// would; a round already over skips straight past the intermission. Server
	/// only, and never reachable from a client: the one caller is the agent socket,
	/// which the deploy unit never opens.
	/// </summary>
	public void ServerResetRound(uint tick)
	{
		if (Match.Phase == RoundPhase.Live)
		{
			Match.End(tick, RoundOutcome.Undecided);
			PublishScoreboard(tick, 0);
			AgentEventBus.Emit(AgentEventKind.RoundEnd, tick, (int)Match.Outcome, Match.GroundTickets,
				UnitManager.Instance?.UnitsLost ?? 0, Match.ElapsedTicks(tick) / (float)SimConfig.TickRate / 60f,
				UnitManager.Instance?.UnitsProduced ?? 0);
		}

		GD.Print($"[match] round reset by the agent api at tick {tick}");
		RestartRound(tick);
	}

	/// <summary>
	/// Ends the round if the strategist has nothing left to play with
	/// (docs/IMPLEMENTATION_PLAN.md §M5).
	///
	/// Cheap enough to ask every tick — five integers, all of them already counted —
	/// and it has to be, because the tick the last queue empties is the tick the
	/// answer changes. The condition itself is engine-free and tested
	/// (<see cref="WinConditions.IsStrategistEliminated"/>).
	/// </summary>
	private void CheckStrategistDefeat(uint tick)
	{
		UnitManager units = UnitManager.Instance;
		if (units == null)
		{
			return;
		}

		// Nobody in the chair is not a defeat: a round with no strategist connected
		// is a round the bots have not filled yet (docs/IMPLEMENTATION_PLAN.md §M3.5),
		// and ending it would make an empty server restart forever.
		if (Teams.StrategistCount == 0)
		{
			return;
		}

		if (!WinConditions.IsStrategistEliminated(Match.StrategistPoints, UnitCatalog.CheapestCost,
			units.LiveUnitCount, units.QueuedUnits, Economy.StrategistNodes))
		{
			return;
		}

		if (Match.RegisterStrategistDefeat(tick))
		{
			GD.Print($"[match] strategist eliminated | points {Match.StrategistPoints}"
				+ $" | units {units.LiveUnitCount} | nodes {Economy.StrategistNodes}");
		}
	}

	// ---- the scoreboard (docs/IMPLEMENTATION_PLAN.md §M5) -------------------

	/// <summary>
	/// The table the round ended on: one row per player, plus what the round cost
	/// both sides. Valid on every peer once the round is over.
	/// </summary>
	public ReadOnlySpan<ScoreEntry> Scores => _scores.AsSpan(0, _scoreCount);

	public RoundSummary Summary => _summary;

	/// <summary>
	/// The label the agent API's <c>reset</c> put on this episode
	/// (docs/AGENT_API.md §7.1). It is carried on <c>round_start</c> and in the
	/// trace; the simulation consumes no global seed, because it has no RNG of its
	/// own to seed (docs/AGENT_API.md §1.2).
	/// </summary>
	public int RoundSeed { get; set; }

	/// <summary>
	/// Builds the scoreboard and sends it. Kills and deaths are server-side counters
	/// the whole round — nothing else puts them on the wire — so this one reliable
	/// message at the end is where a client learns them.
	///
	/// <paramref name="peerId"/> of 0 broadcasts; anything else is a peer that
	/// joined after the round ended and needs the table that is on everyone else's
	/// screen.
	/// </summary>
	private void PublishScoreboard(uint tick, int peerId)
	{
		if (peerId == 0)
		{
			CaptureScores(tick);
		}

		if (_scoreCount == 0 || !Multiplayer.HasMultiplayerPeer() || Multiplayer.GetPeers().Length == 0)
		{
			return;
		}

		byte[] payload = ScoreboardCodec.Encode(_summary, Scores);

		if (peerId == 0)
		{
			Rpc(MethodName.ServerRoundSummary, payload);
			NetworkManager.Instance?.Stats.RecordSent(payload.Length * Multiplayer.GetPeers().Length);
			return;
		}

		RpcId(peerId, MethodName.ServerRoundSummary, payload);
		NetworkManager.Instance?.Stats.RecordSent(payload.Length);
	}

	/// <summary>
	/// Fills the local table from the roster. The server does this for itself as
	/// well as for the wire: a listen host sends itself nothing, and its scoreboard
	/// has to come from somewhere.
	/// </summary>
	private void CaptureScores(uint tick)
	{
		UnitManager units = UnitManager.Instance;

		_scoreCount = 0;
		for (int i = 0; i < _ordered.Count && _scoreCount < ScoreboardCodec.MaxEntries; i++)
		{
			PlayerCombat player = _ordered[i];
			_scores[_scoreCount++] = new ScoreEntry
			{
				PeerId = player.PeerId,
				Kills = (short)Mathf.Clamp(player.Kills, short.MinValue, short.MaxValue),
				Deaths = (short)Mathf.Clamp(player.Deaths, short.MinValue, short.MaxValue),
				Team = player.Team,
				IsBot = BotRoster.IsBot(player.PeerId),
			};
		}

		_summary = new RoundSummary
		{
			Outcome = (byte)Match.Outcome,
			GroundTickets = (short)Mathf.Clamp(Match.GroundTickets, short.MinValue, short.MaxValue),
			StrategistPoints = Match.StrategistPoints,
			UnitsBuilt = (ushort)Mathf.Clamp(units?.UnitsProduced ?? 0, 0, ushort.MaxValue),
			UnitsLost = (ushort)Mathf.Clamp(units?.UnitsLost ?? 0, 0, ushort.MaxValue),
			RoundTicks = Match.ElapsedTicks(tick),
		};
	}

	[Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false,
		TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
	private void ServerRoundSummary(byte[] payload)
	{
		NetworkManager net = NetworkManager.Instance;
		if (net == null || !net.IsClient)
		{
			return;
		}

		net.Stats.RecordReceived(payload.Length);

		if (ScoreboardCodec.TryDecode(payload, _scores, out RoundSummary summary, out int count))
		{
			_summary = summary;
			_scoreCount = count;
		}
	}

	// ---- teams (docs/IMPLEMENTATION_PLAN.md §M3) ----------------------------

	/// <summary>
	/// Asks the server for a side. Applied immediately when this process is the
	/// authority; a client finds out what it got from the next snapshot's team bit,
	/// which is a frame or two and no new message.
	/// </summary>
	public void RequestTeam(Team team)
	{
		if (Local == null)
		{
			return;
		}

		// The menu closes on the click rather than a round trip later. The answer it
		// is closing on may still be corrected — a full strategist's chair lands on
		// the ground — but that correction arrives as a team bit the HUD reads, not as
		// a second menu (docs/NETCODE.md §7).
		LocalAwaitingRole = false;

		NetworkManager net = NetworkManager.Instance;
		if (net != null && net.IsClient)
		{
			// The hold is the server's to lift, and it lifts it in the snapshot that
			// brings the health back. Clearing the mirror here only stops the client
			// from holding itself for a round trip longer than the server does.
			Local.AwaitingRole = false;
			RpcId(1, MethodName.ClientSelectTeam, (byte)team);
			return;
		}

		AssignTeam(Local.PeerId, team);
	}

	/// <summary>
	/// True while this process's own player owes an answer to the role menu
	/// (<see cref="Ui.RoleSelect"/>).
	///
	/// A client's copy of a question the server asked, kept here rather than on
	/// <see cref="PlayerCombat"/> because the question can arrive before the client
	/// has a character to hang it on: the server registers a peer, and therefore asks
	/// it, inside the same spawn it is about to announce.
	/// </summary>
	public bool LocalAwaitingRole { get; private set; }

	/// <summary>
	/// Server-side: takes a player off the field and asks which side they want
	/// (docs/IMPLEMENTATION_PLAN.md §M3). Done when they join and again when a round
	/// starts, so that the sides are picked per round rather than per connection.
	///
	/// A bot is never asked — <see cref="TeamService.RequireChoice"/> refuses to ask
	/// one — so this is only ever reached for a person.
	/// </summary>
	private void RequireRoleChoice(PlayerCombat combat)
	{
		combat.HoldForRoleChoice();
		combat.History.Clear();
		combat.Character?.SetDeadPresentation(true);

		if (combat == Local)
		{
			// A listen host or an offline round: the question and the menu are in the
			// same process, so there is nothing to send.
			LocalAwaitingRole = true;
			return;
		}

		if (Multiplayer.HasMultiplayerPeer() && !BotRoster.IsBot(combat.PeerId))
		{
			RpcId(combat.PeerId, MethodName.ServerRequestRoleChoice);
		}
	}

	/// <summary>
	/// Server -> one peer: pick a side. Reliable, and the only message the role menu
	/// needed — the answer comes back as the team request that already existed, and
	/// the consequences of it ride the snapshot's health and team bits.
	/// </summary>
	[Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false,
		TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
	private void ServerRequestRoleChoice()
	{
		if (NetworkManager.Instance is not { IsClient: true })
		{
			return;
		}

		LocalAwaitingRole = true;

		// The character predicts nothing while it is being held, which is what the
		// server is doing to it. Without this the client would predict a couple of
		// ticks of walking before the first snapshot said otherwise.
		if (Local != null)
		{
			Local.AwaitingRole = true;
		}
	}

	[Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false,
		TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
	private void ClientSelectTeam(byte team)
	{
		if (NetworkManager.Instance is { IsServer: true })
		{
			// Anything but a known team reads as the ground force, which is where a
			// client that sends nonsense belongs (docs/NETCODE.md §1).
			AssignTeam(Multiplayer.GetRemoteSenderId(),
				team == (byte)Team.Strategist ? Team.Strategist : Team.GroundForce);
		}
	}

	/// <summary>
	/// Puts a peer on a side without them having asked, and reports what they got.
	///
	/// This is how a computer player takes a seat (docs/IMPLEMENTATION_PLAN.md
	/// §M3.5): a bot has no client to send <see cref="ClientSelectTeam"/> from, but
	/// it is a peer in every other respect and goes through the same assignment,
	/// the same strategist cap and the same body handling as a person.
	/// </summary>
	public Team ServerAssignTeam(int peerId, Team team)
	{
		NetworkManager net = NetworkManager.Instance;
		if (net != null && net.IsClient)
		{
			return TeamOf(peerId);
		}

		return AssignTeam(peerId, team);
	}

	/// <summary>
	/// Server-side: puts a peer on a side, subject to the strategist cap, and makes
	/// their body match. Going up takes the body off the field; coming back down
	/// puts a fresh one on it. Returns the side they ended up on.
	/// </summary>
	private Team AssignTeam(int peerId, Team requested)
	{
		PlayerCombat combat = Find(peerId);
		if (combat == null)
		{
			return Team.GroundForce;
		}

		// A computer strategist holding the last chair stands up for a person who
		// wants it. Bots never do this for each other — the fill policy already
		// accounts for the cap, and a bot evicting a bot would be a loop
		// (docs/IMPLEMENTATION_PLAN.md §M3.5).
		if (requested == Team.Strategist && !BotRoster.IsBot(peerId) && !Teams.CanJoin(peerId, requested))
		{
			PlayerManager.Instance?.Bots?.YieldSeat(Team.Strategist);
		}

		// An answer to the role menu is an assignment like any other, but it is also
		// the thing that puts a body back on the field — so it must not take the
		// early-out below just because the side it picked is the side the player was
		// already filed under (docs/IMPLEMENTATION_PLAN.md §M3).
		bool wasAwaiting = combat.AwaitingRole;

		Team granted = Teams.Assign(peerId, requested);
		if (granted == combat.Team && !wasAwaiting)
		{
			return granted;
		}

		combat.AwaitingRole = false;
		combat.Team = granted;
		combat.History.Clear();

		if (granted == Team.Strategist)
		{
			combat.Character?.SetDeadPresentation(true);
		}
		else
		{
			// Back on the ground with a full magazine and whatever they last chose,
			// at a spawn point rather than wherever the body was parked.
			combat.Respawn();
			combat.Character?.SetDeadPresentation(false);
			PlayerManager.Instance?.TeleportToSpawn(peerId, combat.Deaths);
		}

		GD.Print($"[match] {BotRoster.NameOf(peerId)} is now {granted}"
			+ $" ({Teams.StrategistCount}/{TeamService.MaxStrategists} strategists)");
		return granted;
	}

	// ---- client ------------------------------------------------------------

	/// <summary>
	/// Runs the local player's weapons for a predicted tick. Identical to the
	/// server's step over the identical frame, which is what makes the tracer and
	/// the ammo count it produces the same ones the server will confirm
	/// (docs/NETCODE.md §4.3).
	/// </summary>
	public void ClientSimulateLocal(in InputContext input, uint tick)
	{
		PlayerCombat combat = Local;
		if (combat?.Character == null)
		{
			return;
		}

		if (!combat.IsAlive)
		{
			// The same lowering the server runs for a player who is off the field, over
			// the same look-only frame.
			Ads.Step(ref combat.Aim, combat.EquippedAim, input);
			return;
		}

		// Mounting is not predicted — it is one round trip, once, and a rejected
		// mount has no message to correct it with. Firing the gun is, through the
		// same step the server will run over the same frames (docs/NETCODE.md §10.2).
		if (EmplacementManager.Instance is { } emplacements
			&& emplacements.TryMounted(combat.PeerId, out Emplacement mounted))
		{
			Ads.Step(ref combat.Aim, AimStats.None, input);
			FireMounted(combat, mounted, input, tick, authoritative: false);
			return;
		}

		combat.Slot = WeaponSim.SelectSlot(combat.Slot, input);
		Ads.Step(ref combat.Aim, combat.EquippedAim, input);

		WeaponStats stats = combat.EquippedStats;
		WeaponAction action = WeaponSim.Step(ref combat.Equipped, stats, input, tick);
		if (action == WeaponAction.None)
		{
			return;
		}

		combat.LastPredictedWeaponTick = tick;
		if (action != WeaponAction.Fire)
		{
			return;
		}

		combat.ShotsFired++;
		ShotsFired++;

		if (stats.IsMelee)
		{
			// Nothing to predict: a swing's only feedback is the server's hit message.
			return;
		}

		// The same cone the server will apply, from the same shot index: the weapon
		// step above advanced both ends identically, so the tracer this puts up is
		// the line the authoritative round will fly down
		// (docs/IMPLEMENTATION_PLAN.md §7).
		Vector3 direction = WeaponSim.FireDirection(combat.Equipped, stats, combat.PeerId,
			combat.Character.AimDirection);

		uint id = PredictedIdBit | _nextPredictedId++;
		if (_projectiles.TrySpawn(id, tick, OwnerId.ForPeer(combat.PeerId), combat.EquippedDefinitionId,
			combat.Character.EyePosition, direction, 0, out _))
		{
			_predicted.Add((id, tick));
		}
	}

	/// <summary>Closes a client's tick: applies what the server said, then flies the tracers.</summary>
	public void ClientPostTick(uint tick)
	{
		EnsureEconomy();
		ApplyPendingMessages(tick);
		PrunePredictions(tick);
		StepProjectiles(tick, authoritative: false);
		UpdateHud(tick);
	}

	/// <summary>
	/// Forgets predicted tracers the server never accounted for. A shot the server
	/// refused — fired on a tick it had already decided the shooter was dead on, or
	/// while its pool was full — never gets an authoritative spawn to be adopted by,
	/// and the list would otherwise only grow.
	/// </summary>
	private void PrunePredictions(uint tick)
	{
		for (int i = _predicted.Count - 1; i >= 0; i--)
		{
			if (tick > _predicted[i].Tick + PredictionExpiryTicks)
			{
				_projectiles.Kill(_predicted[i].Id);
				_predicted.RemoveAt(i);
			}
		}
	}

	/// <summary>Takes the server's word on a player's health, ammunition and weapon.</summary>
	public void ApplySnapshot(in PlayerSnapshot snapshot)
	{
		PlayerCombat combat = Find(snapshot.PeerId);
		if (combat == null)
		{
			return;
		}

		bool wasAlive = combat.IsAlive;
		combat.ApplyAuthoritative(snapshot.Health, snapshot.Ammo, snapshot.WeaponFlags, snapshot.LastInputTick,
			combat == Local);

		// A player the server has put back on the field is a player it has stopped
		// waiting on, whatever became of the answer this client sent. Belt and braces:
		// the client is never left holding a character the server is simulating.
		if (combat == Local && combat.AwaitingRole && snapshot.Health > 0)
		{
			combat.AwaitingRole = false;
			LocalAwaitingRole = false;
		}

		if (wasAlive != combat.IsAlive)
		{
			combat.Character?.SetDeadPresentation(!combat.IsAlive);
		}
	}

	private void ApplyPendingMessages(uint tick)
	{
		float renderTick = NetworkManager.Instance?.Clock.RenderTick ?? tick;

		for (int i = 0; i < _pendingSpawns.Count; i++)
		{
			if (ProjectileCodec.TryDecodeSpawn(_pendingSpawns[i], out ProjectileSpawn spawn))
			{
				ApplySpawn(spawn, renderTick);
			}
		}
		_pendingSpawns.Clear();

		for (int i = 0; i < _pendingHits.Count; i++)
		{
			if (ProjectileCodec.TryDecodeHit(_pendingHits[i], out ProjectileHit hit))
			{
				ApplyHit(hit);
			}
		}
		_pendingHits.Clear();
	}

	private void ApplySpawn(in ProjectileSpawn spawn, float renderTick)
	{
		// The client's own shot is already in the air as a predicted tracer. Adopting
		// it — taking the server's id for the projectile that is already flying —
		// rather than replacing it keeps the tracer from jumping back down its own
		// trajectory by a round trip's worth of flight.
		if (Local != null && spawn.OwnerPeerId == Local.PeerId && TryAdoptPrediction(spawn))
		{
			return;
		}

		// Everyone else's shot is fast-forwarded to the tick this client is rendering,
		// which is where the shooter is drawn too (docs/NETCODE.md §4.3).
		int catchUp = (int)Mathf.Max(0f, renderTick - spawn.SpawnTick);
		_projectiles.TrySpawn(spawn.Id, spawn.SpawnTick, spawn.OwnerPeerId, spawn.DefinitionId, spawn.Origin,
			spawn.Direction, catchUp, out _);
	}

	private bool TryAdoptPrediction(in ProjectileSpawn spawn)
	{
		for (int i = 0; i < _predicted.Count; i++)
		{
			(uint id, uint predictedTick) = _predicted[i];
			if (Math.Abs((long)predictedTick - spawn.SpawnTick) > PredictionMatchWindowTicks)
			{
				continue;
			}

			_predicted.RemoveAt(i);
			return _projectiles.Rekey(id, spawn.Id);
		}

		return false;
	}

	private void ApplyHit(in ProjectileHit hit)
	{
		if (hit.Id != 0)
		{
			_projectiles.Kill(hit.Id);
		}

		_view?.Impact(hit.Point, hit.Flags);

	}

	/// <summary>
	/// Server -> the shooter alone. The hit message everyone gets says where a round
	/// stopped; it deliberately does not say who fired it, so "did I hit them" is a
	/// separate two-byte message to the one peer the answer belongs to.
	/// </summary>
	[Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false,
		TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
	private void ServerHitConfirm(bool killed)
	{
		_hud?.FlashHitMarker(killed);
	}

	private void ConfirmHit(PlayerCombat attacker, PlayerCombat victim, bool killed)
	{
		// Blowing yourself up is not a hit marker.
		if (attacker == null || attacker == victim)
		{
			return;
		}

		if (attacker == Local)
		{
			_hud?.FlashHitMarker(killed);
			return;
		}

		if (Multiplayer.HasMultiplayerPeer() && attacker.PeerId != Multiplayer.GetUniqueId())
		{
			RpcId(attacker.PeerId, MethodName.ServerHitConfirm, killed);
		}
	}

	// ---- loadout -----------------------------------------------------------

	/// <summary>
	/// Asks the server for a different large weapon on the next spawn. Applied
	/// immediately when this process is the authority.
	/// </summary>
	public void RequestLoadout(byte largeWeaponId)
	{
		if (Local == null)
		{
			return;
		}

		LoadoutSelection wanted = WeaponCatalog.Sanitize(new LoadoutSelection { Large = largeWeaponId });
		Local.PendingLoadout = wanted;

		NetworkManager net = NetworkManager.Instance;
		if (net != null && net.IsClient)
		{
			RpcId(1, MethodName.ClientSelectLoadout, wanted.Large);
		}
	}

	[Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false,
		TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
	private void ClientSelectLoadout(byte largeWeaponId)
	{
		if (NetworkManager.Instance is not { IsServer: true })
		{
			return;
		}

		PlayerCombat combat = Find(Multiplayer.GetRemoteSenderId());
		if (combat == null)
		{
			return;
		}

		// Sanitized rather than trusted: this is a byte a client chose
		// (docs/NETCODE.md §1).
		combat.PendingLoadout = WeaponCatalog.Sanitize(new LoadoutSelection { Large = largeWeaponId });
	}

	// ---- replication -------------------------------------------------------

	[Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false,
		TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
	private void ServerProjectileSpawn(byte[] payload)
	{
		if (NetworkManager.Instance is not { IsClient: true })
		{
			return;
		}

		NetworkManager.Instance.Stats.RecordReceived(payload.Length);
		Queue(_pendingSpawns, payload);
	}

	[Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false,
		TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
	private void ServerProjectileHit(byte[] payload)
	{
		if (NetworkManager.Instance is not { IsClient: true })
		{
			return;
		}

		NetworkManager.Instance.Stats.RecordReceived(payload.Length);
		Queue(_pendingHits, payload);
	}

	[Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false,
		TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
	private void ServerMatchState(byte phase, byte outcome, int tickets, int startingTickets, int points,
		uint endTick, uint version)
	{
		if (NetworkManager.Instance is not { IsClient: true })
		{
			return;
		}

		NetworkManager.Instance.Stats.RecordReceived(MatchStateBytes);
		Match.Apply((RoundPhase)phase, (RoundOutcome)outcome, tickets, startingTickets, points, endTick, version);
	}

	/// <summary>
	/// Sends the round's state when any part of it has changed. The strategist's
	/// balance keeps its own version because it changes on a different schedule from
	/// the phase — every unit queued moves it, and none of those is a round event —
	/// but the two ride the same message, which is small and reliable and rare.
	/// </summary>
	private void ReplicateMatchState()
	{
		if (Match.Version == _replicatedMatchVersion && Match.Strategist.Version == _replicatedPointsVersion)
		{
			return;
		}

		_replicatedMatchVersion = Match.Version;
		_replicatedPointsVersion = Match.Strategist.Version;

		if (Multiplayer.HasMultiplayerPeer() && Multiplayer.GetPeers().Length > 0)
		{
			SendMatchState(0);
			NetworkManager.Instance?.Stats.RecordSent(MatchStateBytes * Multiplayer.GetPeers().Length);
		}
	}

	private void OnPeerJoined(int peerId)
	{
		if (NetworkManager.Instance is not { IsServer: true })
		{
			return;
		}

		// The round is already running; a newcomer needs its state, not the next
		// change to it. Same for the nodes: who holds one is state, and the next
		// change to it might be minutes away.
		SendMatchState(peerId);
		SendNodeState(peerId);

		// Somebody who connects during the intermission sees the table everyone else
		// is looking at rather than an empty one.
		if (Match.Phase == RoundPhase.Ended)
		{
			PublishScoreboard(0, peerId);
		}
	}

	/// <summary>Broadcasts when <paramref name="peerId"/> is 0, otherwise sends to that one peer.</summary>
	private void SendMatchState(int peerId)
	{
		if (peerId == 0)
		{
			Rpc(MethodName.ServerMatchState, (byte)Match.Phase, (byte)Match.Outcome, Match.GroundTickets,
				Match.StartingGroundTickets, Match.StrategistPoints, Match.EndTick, Match.Version);
			return;
		}

		RpcId(peerId, MethodName.ServerMatchState, (byte)Match.Phase, (byte)Match.Outcome, Match.GroundTickets,
			Match.StartingGroundTickets, Match.StrategistPoints, Match.EndTick, Match.Version);
	}

	// ---- resource nodes (docs/IMPLEMENTATION_PLAN.md §M5) -------------------

	/// <summary>
	/// Tells everyone about the nodes that have changed since the last report.
	///
	/// Reliable and only on change, unlike a snapshot: who holds a node is state
	/// that changes a handful of times a round, and the progress bar in between is
	/// worth 5 Hz and eight bytes. There is no fog over it — both sides can see who
	/// is standing on a pad from the other end of the map in any RTS anyone has
	/// played, and a node nobody can find is not an objective.
	/// </summary>
	private void BroadcastNodeState(uint tick)
	{
		if (tick % SimConfig.NodeReportIntervalTicks != 0
			|| !Multiplayer.HasMultiplayerPeer() || Multiplayer.GetPeers().Length == 0)
		{
			return;
		}

		int peers = Multiplayer.GetPeers().Length;
		int sent = 0;

		for (int i = 0; i < Economy.NodeCount; i++)
		{
			ResourceNode node = Economy.NodeAt(i);
			if (node == null || node.Capture.Version == VersionOf(i))
			{
				continue;
			}

			SetVersion(i, node.Capture.Version);
			Rpc(MethodName.ServerNodeState, i, (byte)node.Capture.Owner, (byte)node.Capture.Claimant,
				NodeFlags(node), ProgressByte(node));
			sent++;
		}

		if (sent > 0)
		{
			NetworkManager.Instance?.Stats.RecordSent(NodeStateBytes * sent * peers);
		}
	}

	/// <summary>Broadcasts every node when <paramref name="peerId"/> is 0, otherwise sends to that one peer.</summary>
	private void SendNodeState(int peerId)
	{
		if (!Multiplayer.HasMultiplayerPeer() || Multiplayer.GetPeers().Length == 0)
		{
			return;
		}

		for (int i = 0; i < Economy.NodeCount; i++)
		{
			ResourceNode node = Economy.NodeAt(i);
			if (node == null)
			{
				continue;
			}

			SetVersion(i, node.Capture.Version);

			if (peerId == 0)
			{
				Rpc(MethodName.ServerNodeState, i, (byte)node.Capture.Owner, (byte)node.Capture.Claimant,
					NodeFlags(node), ProgressByte(node));
			}
			else
			{
				RpcId(peerId, MethodName.ServerNodeState, i, (byte)node.Capture.Owner,
					(byte)node.Capture.Claimant, NodeFlags(node), ProgressByte(node));
			}
		}
	}

	[Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false,
		TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
	private void ServerNodeState(int index, byte owner, byte claimant, byte flags, byte progress)
	{
		NetworkManager net = NetworkManager.Instance;
		if (net == null || !net.IsClient)
		{
			return;
		}

		net.Stats.RecordReceived(NodeStateBytes);

		ResourceNode node = Economy.NodeAt(index);
		if (node == null)
		{
			return;
		}

		node.Capture.Apply(Sanitize(owner), Sanitize(claimant), (flags & 1) != 0,
			progress / (float)byte.MaxValue, node.Rules);
	}

	/// <summary>A holder byte that arrived from the wire. Anything unknown is nobody's.</summary>
	private static NodeHolder Sanitize(byte holder) =>
		holder <= (byte)NodeHolder.Strategist ? (NodeHolder)holder : NodeHolder.Neutral;

	private static byte NodeFlags(ResourceNode node) => (byte)(node.Capture.Contested ? 1 : 0);

	private static byte ProgressByte(ResourceNode node) =>
		(byte)Mathf.RoundToInt(node.Capture.Progress(node.Rules) * byte.MaxValue);

	private uint VersionOf(int index) =>
		index >= 0 && index < _replicatedNodeVersions.Length ? _replicatedNodeVersions[index] : 0u;

	private void SetVersion(int index, uint version)
	{
		if (index >= 0 && index < _replicatedNodeVersions.Length)
		{
			_replicatedNodeVersions[index] = version;
		}
	}

	/// <summary>
	/// Finds the map's resource nodes, once. Autoloads are ready before the map is,
	/// the same arrangement the barracks and the spawn points use.
	///
	/// On a client as well as on the server: a client holds the same nodes, paints
	/// them for whoever the server says owns them, and never ticks a capture.
	/// </summary>
	private void EnsureEconomy()
	{
		if (_economyStarted)
		{
			return;
		}

		_economyStarted = true;
		Economy.Collect(GetTree());
		_replicatedNodeVersions = new uint[Economy.NodeCount];

		if (Economy.NodeCount == 0)
		{
			GD.PushWarning($"[economy] no nodes in group '{ResourceNode.Group}';"
				+ " the strategist's points are a fixed pool and cannot be denied");
		}
	}

	/// <summary>
	/// Sends a projectile message to everyone it is allowed to reach: a tracer
	/// leaving a muzzle or a round striking a wall says where somebody is, so a
	/// strategist whose fog does not reach <paramref name="point"/> does not get it
	/// (docs/NETCODE.md §6.2). A flanker who opens fire would otherwise light
	/// themselves up on the strategist's screen from across the map.
	///
	/// One test, not one per peer: the strategists share an army and therefore its
	/// eyes. A shot inside the fog is still the common case, and stays a single
	/// broadcast.
	/// </summary>
	private void Broadcast(StringName method, byte[] payload, Vector3 point)
	{
		if (!Multiplayer.HasMultiplayerPeer())
		{
			return;
		}

		int[] peers = Multiplayer.GetPeers();
		if (peers.Length == 0)
		{
			return;
		}

		if (Visibility.Covers(point))
		{
			Rpc(method, payload);
			NetworkManager.Instance?.Stats.RecordSent(payload.Length * peers.Length);
			return;
		}

		int sent = 0;
		int withheld = 0;
		for (int i = 0; i < peers.Length; i++)
		{
			if (TeamOf(peers[i]) == Team.Strategist)
			{
				withheld++;
				continue;
			}

			RpcId(peers[i], method, payload);
			sent++;
		}

		Visibility.CountWithheld(withheld);
		if (sent > 0)
		{
			NetworkManager.Instance?.Stats.RecordSent(payload.Length * sent);
		}
	}

	private static void Queue(List<byte[]> into, byte[] payload)
	{
		into.Add(payload);
		if (into.Count > MaxPendingMessages)
		{
			into.RemoveAt(0);
		}
	}

	// ---- world queries -----------------------------------------------------

	/// <summary>
	/// How far along a segment the world stops it, or 1 for a clear line. Only world
	/// geometry: players live on their own collision layer and are resolved against
	/// recorded hitboxes instead, so that a hit can be re-derived without the physics
	/// server's current state.
	/// </summary>
	private float WorldHitFraction(Vector3 from, Vector3 to, out Vector3 point, out bool blocked)
	{
		point = to;
		blocked = false;

		PhysicsDirectSpaceState3D space = GetTree()?.Root?.World3D?.DirectSpaceState;
		if (space == null || from.IsEqualApprox(to))
		{
			return 1f;
		}

		var query = PhysicsRayQueryParameters3D.Create(from, to, CollisionLayers.World);
		Godot.Collections.Dictionary result = space.IntersectRay(query);
		if (result.Count == 0)
		{
			return 1f;
		}

		point = result["position"].AsVector3();
		blocked = true;

		float length = (to - from).Length();
		return length > 0f ? Mathf.Clamp((point - from).Length() / length, 0f, 1f) : 0f;
	}

	/// <summary>
	/// Whether the line between two points is clear of world geometry. The fog's one
	/// engine dependency (<see cref="VisibilityService"/>), and the same query a
	/// unit's own target acquisition makes.
	/// </summary>
	private bool HasLineOfSight(Vector3 from, Vector3 to)
	{
		WorldHitFraction(from, to, out _, out bool blocked);
		return !blocked;
	}

	// ---- local presentation ------------------------------------------------

	private void EnsureLocalUi()
	{
		if (Bootstrap.IsDedicatedServer || _view != null)
		{
			return;
		}

		_view = new ProjectileView { Name = "ProjectileView" };
		AddChild(_view);

		_hud = new CombatHud { Name = "CombatHud" };
		AddChild(_hud);

		_teamSelect = new TeamSelect { Name = "TeamSelect" };
		AddChild(_teamSelect);

		_roleSelect = new RoleSelect { Name = "RoleSelect" };
		AddChild(_roleSelect);

		_scoreboard = new Scoreboard { Name = "Scoreboard" };
		AddChild(_scoreboard);
	}

	private void UpdateHud(uint tick)
	{
		SyncLocalRole();
		Economy.Refresh();
		_view?.Render(_projectiles);
		_hud?.Refresh(Local, Match, tick, LocalAwaitingRole);
		_strategist?.Refresh(tick);
		_scoreboard?.Refresh(this);

		// After SyncLocalRole, which is what puts the strategist's camera up or takes
		// it down: closing this panel hands the mouse to whichever of the two games
		// the player has just chosen.
		_roleSelect?.Refresh(LocalAwaitingRole, Local?.Team ?? Team.GroundForce);
	}

	/// <summary>
	/// Puts the right game in front of the local player.
	///
	/// A strategist's character stays in the world — the roster, the snapshot and
	/// the prediction loop are all built around every peer having one — but it is
	/// hidden, uncollidable and fed nothing but its look angles, and the process
	/// draws the map from above instead. Switching back frees the camera and hands
	/// the mouse back to the first-person rig.
	/// </summary>
	private void SyncLocalRole()
	{
		if (Bootstrap.IsDedicatedServer)
		{
			return;
		}

		bool wantsStrategist = Local is { Team: Team.Strategist };

		if (wantsStrategist && _strategist == null)
		{
			_strategist = new StrategistController { Name = "StrategistController" };
			AddChild(_strategist);
		}
		else if (!wantsStrategist && _strategist != null)
		{
			_strategist.QueueFree();
			_strategist = null;
			Local?.Character?.EnterFirstPerson();
		}
	}
}
