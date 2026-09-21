using System;
using System.Collections.Generic;
using Gdpyr.Core;
using Gdpyr.Fps;
using Gdpyr.Net;
using Gdpyr.Rts;
using Gdpyr.Sim;
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
	private StrategistController _strategist;

	private uint _nextPredictedId = 1;
	private uint _replicatedMatchVersion;
	private uint _replicatedPointsVersion;
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

		if (NetworkManager.Instance is { IsServer: true })
		{
			// Everyone arrives on the ground; the strategist slot is opted into.
			combat.Team = Teams.Assign(peerId, Team.GroundForce);
		}

		if (isLocal)
		{
			Local = combat;
			EnsureLocalUi();
		}

		return combat;
	}

	public void Unregister(int peerId)
	{
		Teams.Remove(peerId);

		if (!_players.Remove(peerId, out PlayerCombat combat))
		{
			return;
		}

		_ordered.Remove(combat);
		if (Local == combat)
		{
			Local = null;
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
		if (combat == null || !combat.IsAlive)
		{
			return;
		}

		combat.Slot = WeaponSim.SelectSlot(combat.Slot, input);

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
		RecordHitboxes(tick);
		StepProjectiles(tick, authoritative: true);
		ServerRespawns(tick);
		ServerRound(tick);
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
		}));

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
			flags |= DamageUnit(unitVictim, stats.Damage, attacker, tick);
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
		}));
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
			flags |= DamageUnit(unitVictim, stats.Damage, attacker, tick);
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
		}));
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
				flags |= DamageUnit(unit, damage, attacker, tick);
			}
		}

		return flags;
	}

	/// <summary>
	/// Applies damage to a unit. Unlike a player's death this costs no ticket — the
	/// ground force's pool is theirs, and a unit is already paid for out of the
	/// strategist's points.
	/// </summary>
	private HitFlags DamageUnit(Unit unit, float amount, PlayerCombat attacker, uint tick)
	{
		UnitManager units = UnitManager.Instance;
		if (units == null || unit == null || !unit.IsAlive || amount <= 0f)
		{
			return HitFlags.None;
		}

		bool killed = units.Damage(unit, amount, tick);
		ConfirmHit(attacker, null, killed);

		if (killed && attacker != null)
		{
			attacker.Kills++;
		}

		return killed ? HitFlags.Killed : HitFlags.None;
	}

	/// <summary>
	/// Applies damage and, if that killed the victim, spends a ticket for it.
	///
	/// <paramref name="attackerOwnerId"/> is carried alongside
	/// <paramref name="attacker"/> because a unit has no <see cref="PlayerCombat"/>:
	/// it is null for every round a unit fires, and a kill log that called all of
	/// those "the world" would make the one record M6 derives its per-round CSV from
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
			// the field to put anywhere (see PlayerCombat.IsAlive).
			if (combat.Team == Team.Strategist || combat.IsAlive || tick < combat.RespawnTick)
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
			// first player rather than on the server's first tick.
			case RoundPhase.Warmup when _ordered.Count > 0:
				Match.Start(tick, _gameMode.GroundForceTickets, _gameMode.StrategistTickets,
					_gameMode.RoundDurationMinutes * 60 * SimConfig.TickRate);
				GD.Print($"[match] round live | tickets {Match.GroundTickets}"
					+ $" | points {Match.StrategistPoints}"
					+ $" | {_gameMode.RoundDurationMinutes} minutes");
				break;

			case RoundPhase.Live:
				Match.Advance(tick);
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
			GD.Print($"[match] round over: {Match.Outcome} | tickets {Match.GroundTickets}"
				+ $" | deaths {Match.GroundDeaths} | next round in {_gameMode.IntermissionSeconds:0}s");
			return;
		}

		if (tick < _intermissionEndTick)
		{
			return;
		}

		_intermissionArmed = false;
		Match.Reset();
		_projectiles.Clear();
		UnitManager.Instance?.ClearUnits();
		for (int i = 0; i < _ordered.Count; i++)
		{
			Respawn(_ordered[i], tick);
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

		NetworkManager net = NetworkManager.Instance;
		if (net != null && net.IsClient)
		{
			RpcId(1, MethodName.ClientSelectTeam, (byte)team);
			return;
		}

		AssignTeam(Local.PeerId, team);
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
	/// Server-side: puts a peer on a side, subject to the strategist cap, and makes
	/// their body match. Going up takes the body off the field; coming back down
	/// puts a fresh one on it.
	/// </summary>
	private void AssignTeam(int peerId, Team requested)
	{
		PlayerCombat combat = Find(peerId);
		if (combat == null)
		{
			return;
		}

		Team granted = Teams.Assign(peerId, requested);
		if (granted == combat.Team)
		{
			return;
		}

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

		GD.Print($"[match] peer {peerId} is now {granted}"
			+ $" ({Teams.StrategistCount}/{TeamService.MaxStrategists} strategists)");
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
		if (combat?.Character == null || !combat.IsAlive)
		{
			return;
		}

		combat.Slot = WeaponSim.SelectSlot(combat.Slot, input);

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
		// change to it.
		SendMatchState(peerId);
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

	private void Broadcast(StringName method, byte[] payload)
	{
		if (!Multiplayer.HasMultiplayerPeer())
		{
			return;
		}

		int peers = Multiplayer.GetPeers().Length;
		if (peers == 0)
		{
			return;
		}

		Rpc(method, payload);
		NetworkManager.Instance?.Stats.RecordSent(payload.Length * peers);
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
	}

	private void UpdateHud(uint tick)
	{
		SyncLocalRole();
		_view?.Render(_projectiles);
		_hud?.Refresh(Local, Match, tick);
		_strategist?.Refresh(tick);
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
