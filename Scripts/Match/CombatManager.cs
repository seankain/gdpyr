using System;
using System.Collections.Generic;
using Gdpyr.Core;
using Gdpyr.Fps;
using Gdpyr.Net;
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
	private const int MatchStateBytes = 18;

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

	private uint _nextPredictedId = 1;
	private uint _replicatedMatchVersion;
	private uint _intermissionEndTick;
	private bool _intermissionArmed;

	/// <summary>The round. Server-authoritative; clients hold a replicated copy.</summary>
	public MatchState Match { get; } = new();

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

		if (isLocal)
		{
			Local = combat;
			EnsureLocalUi();
		}

		return combat;
	}

	public void Unregister(int peerId)
	{
		if (!_players.Remove(peerId, out PlayerCombat combat))
		{
			return;
		}

		_ordered.Remove(combat);
		if (Local == combat)
		{
			Local = null;
		}
	}

	public PlayerCombat Find(int peerId) => _players.GetValueOrDefault(peerId);

	/// <summary>
	/// Whether this peer's character should be simulating its own movement. A dead
	/// player is fed a neutral frame instead of their input, on the server and on
	/// their own client alike, so the two agree about a corpse standing still.
	/// </summary>
	public bool IsAlive(int peerId) => Find(peerId)?.IsAlive ?? true;

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
		ShotsFired++;

		if (stats.IsMelee)
		{
			ResolveMelee(combat, stats, tick);
			return;
		}

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
		Vector3 origin = combat.Character.EyePosition;
		Vector3 direction = combat.Character.AimDirection;

		// Lag compensation, the cheap way (docs/NETCODE.md §4.3): the round starts its
		// life the shooter's one-way latency further along, so it arrives where they
		// aimed rather than where they would have had to lead to allow for the link.
		uint id = _projectiles.Spawn(tick, combat.PeerId, definitionId, origin, direction,
			combat.LagCompensationTicks);

		if (id == 0)
		{
			GD.PushWarning($"[combat] projectile pool full; dropped a shot from peer {combat.PeerId}");
			return;
		}

		Broadcast(MethodName.ServerProjectileSpawn, ProjectileCodec.EncodeSpawn(new ProjectileSpawn
		{
			Id = id,
			SpawnTick = tick,
			OwnerPeerId = combat.PeerId,
			DefinitionId = definitionId,
			Origin = origin,
			Direction = direction,
		}));
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

		HitFlags flags = HitFlags.Melee;
		if (victim != null)
		{
			flags |= Damage(victim, stats.Damage, attacker, tick);
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
			VictimPeerId = victim?.PeerId ?? 0,
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

		float nearest = WorldHitFraction(segment.From, segment.To, out Vector3 point, out bool blocked);
		PlayerCombat victim = null;

		for (int i = 0; i < _ordered.Count; i++)
		{
			PlayerCombat target = _ordered[i];

			// The shooter's own capsule contains the muzzle, so a round would kill its
			// owner on the tick it was fired.
			if (target.PeerId == segment.OwnerPeerId || !target.IsAlive)
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

		if (!blocked)
		{
			return;
		}

		_projectiles.Kill(segment.Id);
		HitsResolved++;

		PlayerCombat attacker = Find(segment.OwnerPeerId);
		HitFlags flags = HitFlags.None;

		if (victim != null)
		{
			flags |= Damage(victim, stats.Damage, attacker, tick);
		}

		if (stats.IsExplosive)
		{
			flags |= HitFlags.Exploded;
			flags |= Explode(point, stats, attacker, victim, tick);
		}

		Broadcast(MethodName.ServerProjectileHit, ProjectileCodec.EncodeHit(new ProjectileHit
		{
			Id = segment.Id,
			Point = point,
			VictimPeerId = victim?.PeerId ?? 0,
			Flags = flags,
		}));
	}

	/// <summary>
	/// Blast damage, to everyone in reach including whoever fired it. Self-damage is
	/// deliberate: a launcher that is safe at point blank is a shotgun.
	/// </summary>
	private HitFlags Explode(Vector3 point, in ProjectileStats stats, PlayerCombat attacker, PlayerCombat direct,
		uint tick)
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
				flags |= Damage(target, damage, attacker, tick);
			}
		}

		return flags;
	}

	/// <summary>Applies damage and, if that killed the victim, spends a ticket for it.</summary>
	private HitFlags Damage(PlayerCombat victim, float amount, PlayerCombat attacker, uint tick)
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

		// Everyone is ground force until the strategist exists (M3), so every death
		// is a ground-force death.
		Match.RegisterGroundDeath(tick);

		GD.Print($"[combat] peer {victim.PeerId} killed by {(attacker == null ? "the world" : attacker.PeerId.ToString())}"
			+ $" | tickets {Match.GroundTickets}");
		return HitFlags.Killed;
	}

	private void ServerRespawns(uint tick)
	{
		for (int i = 0; i < _ordered.Count; i++)
		{
			PlayerCombat combat = _ordered[i];
			if (combat.IsAlive || tick < combat.RespawnTick)
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
				Match.Start(tick, _gameMode.GroundForceTickets,
					_gameMode.RoundDurationMinutes * 60 * SimConfig.TickRate);
				GD.Print($"[match] round live | tickets {Match.GroundTickets}"
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
		for (int i = 0; i < _ordered.Count; i++)
		{
			Respawn(_ordered[i], tick);
		}
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

		uint id = PredictedIdBit | _nextPredictedId++;
		if (_projectiles.TrySpawn(id, tick, combat.PeerId, combat.EquippedDefinitionId,
			combat.Character.EyePosition, combat.Character.AimDirection, 0, out _))
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
	private void ServerMatchState(byte phase, byte outcome, int tickets, int startingTickets, uint endTick,
		uint version)
	{
		if (NetworkManager.Instance is not { IsClient: true })
		{
			return;
		}

		NetworkManager.Instance.Stats.RecordReceived(MatchStateBytes);
		Match.Apply((RoundPhase)phase, (RoundOutcome)outcome, tickets, startingTickets, endTick, version);
	}

	private void ReplicateMatchState()
	{
		if (Match.Version == _replicatedMatchVersion)
		{
			return;
		}

		_replicatedMatchVersion = Match.Version;

		if (Multiplayer.HasMultiplayerPeer() && Multiplayer.GetPeers().Length > 0)
		{
			Rpc(MethodName.ServerMatchState, (byte)Match.Phase, (byte)Match.Outcome, Match.GroundTickets,
				Match.StartingGroundTickets, Match.EndTick, Match.Version);
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
		RpcId(peerId, MethodName.ServerMatchState, (byte)Match.Phase, (byte)Match.Outcome, Match.GroundTickets,
			Match.StartingGroundTickets, Match.EndTick, Match.Version);
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
	}

	private void UpdateHud(uint tick)
	{
		_view?.Render(_projectiles);
		_hud?.Refresh(Local, Match, tick);
	}
}
