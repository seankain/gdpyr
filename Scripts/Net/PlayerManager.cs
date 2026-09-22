using System;
using System.Collections.Generic;
using Gdpyr.Agent;
using Gdpyr.Bots;
using Gdpyr.Core;
using Gdpyr.Fps;
using Gdpyr.Match;
using Gdpyr.Rts;
using Gdpyr.Sim;
using Godot;

namespace Gdpyr.Net;

/// <summary>
/// Third autoload: the per-tick simulation loop and the player roster.
///
/// It is the only thing that ticks gameplay. On a server it pops one
/// <see cref="InputFrame"/> per client per tick from that client's jitter buffer,
/// simulates every character and broadcasts a snapshot; on a client it predicts
/// its own character, replays it against the server's corrections, and
/// interpolates everyone else (docs/NETCODE.md §3).
/// </summary>
public partial class PlayerManager : Node
{
	public static PlayerManager Instance { get; private set; }

	private const string PlayerScenePath = "res://Scenes/fps_controller.tscn";

	/// <summary>Spawn points are found by group so the map owns them, not this file.</summary>
	private const string SpawnGroup = "player_spawn";

	/// <summary>
	/// Snapshots arrive on the idle frame but can only be applied inside a physics
	/// step, so they queue. This bounds the queue if the physics step stalls.
	/// </summary>
	private const int MaxPendingSnapshots = 16;

	private sealed class Player
	{
		public int PeerId;
		public int SpawnIndex;
		public fps_controller Character;

		/// <summary>Server only, and only for a remote client's character.</summary>
		public ServerInputQueue Queue;

		/// <summary>Client only, and only for someone else's character.</summary>
		public SnapshotInterpolator Interpolator;

		/// <summary>
		/// Server only: this character's frames come from <see cref="BotDirector"/>
		/// rather than from a socket (docs/IMPLEMENTATION_PLAN.md §M3.5). Nothing
		/// else about it differs, which is the whole design.
		/// </summary>
		public bool IsBot;
	}

	private readonly Dictionary<int, Player> _players = new();
	private readonly List<Player> _ordered = new();
	private readonly List<byte[]> _pendingSnapshots = new();

	private readonly PredictionLedger _ledger = new();
	private readonly InputFrame[] _inputScratch = new InputFrame[InputCodec.MaxFrames];
	private readonly PlayerSnapshot[] _snapshotScratch = new PlayerSnapshot[SnapshotCodec.MaxPlayers];

	/// <summary>One strategist's view of the roster, compacted out of <see cref="_snapshotScratch"/>.</summary>
	private readonly PlayerSnapshot[] _visibleScratch = new PlayerSnapshot[SnapshotCodec.MaxPlayers];

	private readonly List<Transform3D> _spawnPoints = new();

	private PackedScene _playerScene;
	private Node3D _playersRoot;
	private LocalInputSampler _sampler;
	private BotDirector _bots;

	/// <summary>The agent control channel, when <c>--agent-api</c> asked for one. Null otherwise.</summary>
	private AgentServer _agents;
	private Player _local;
	private bool _started;

	private uint _lastSampledTick;

	/// <summary>
	/// Client-side: the newest tick this process has decoded a snapshot for, whoever
	/// it was about. It is the reference the fog is measured against — a record that
	/// is behind it was left out of a packet that did arrive (docs/NETCODE.md §6.2).
	/// </summary>
	private uint _newestSnapshotTick;

	/// <summary>Ticks replayed by the last reconciliation. For the net HUD.</summary>
	public int LastReplayTicks => _ledger.LastReplayLength;

	/// <summary>Input packets that failed to decode. Anything but 0 is a bug or an attack.</summary>
	public int RejectedInputPackets { get; private set; }

	public int PlayerCount => _ordered.Count;

	public string LocalStateName => _local?.Character?.StateName ?? "-";

	/// <summary>The computer players, on the authority. Null on a client, which never has any.</summary>
	public BotDirector Bots => _bots;

	/// <summary>Spawn points the map supplied, for whoever needs somewhere to go.</summary>
	public int SpawnPointCount => _spawnPoints.Count;

	/// <summary>Where the <paramref name="index"/>-th spawn point is. Wraps, like <see cref="SpawnTransform"/>.</summary>
	public Vector3 SpawnPositionAt(int index) => SpawnTransform(index).Origin;

	public bool HasPlayer(int peerId) => _players.ContainsKey(peerId);

	/// <summary>This peer's character, or null. The agent API's observations are taken from it.</summary>
	public fps_controller CharacterOf(int peerId) =>
		_players.TryGetValue(peerId, out Player player) ? player.Character : null;

	/// <summary>
	/// Characters whose intent arrives over a socket — the people connected to this
	/// authority. Read by the agent API, which refuses stepped mode while any of
	/// them is playing (docs/AGENT_API.md §5.2).
	/// </summary>
	public int RemotePeerCount
	{
		get
		{
			int count = 0;
			for (int i = 0; i < _ordered.Count; i++)
			{
				if (!_ordered[i].IsBot && _ordered[i].Queue != null)
				{
					count++;
				}
			}

			return count;
		}
	}

	public override void _Ready()
	{
		Instance = this;

		// Keeps simulating while the local pause menu is up; see NetworkManager.
		ProcessMode = ProcessModeEnum.Always;

		_playerScene = GD.Load<PackedScene>(PlayerScenePath);

		_playersRoot = new Node3D { Name = "Players" };
		AddChild(_playersRoot);

		if (NetworkManager.Instance is { } net)
		{
			net.PeerJoined += OnPeerJoined;
			net.PeerLeft += OnPeerLeft;
		}
	}

	public override void _ExitTree()
	{
		_agents?.Dispose();
		_agents = null;

		if (Instance == this)
		{
			Instance = null;
		}
	}

	public override void _PhysicsProcess(double delta)
	{
		NetworkManager net = NetworkManager.Instance;
		if (net == null)
		{
			return;
		}

		// Nothing is simulated while the main menu is up (docs/IMPLEMENTATION_PLAN.md
		// §3, "Ui/ Main menu"). This node is an autoload and the menu is a scene, so
		// without the gate the roster would be built, the spawn points collected off
		// the menu scene and the agent channel opened before anybody had picked a
		// server to play on.
		if (!Session.InGame)
		{
			return;
		}

		EnsureStarted(net);

		if (_agents != null)
		{
			// Drained before the gate, so an action that arrives while the sim is held
			// is seen on the tick that unblocks (docs/AGENT_API.md §5.2).
			_agents.Pump(net.Tick);

			// Stepped mode: the engine frame still happens, the *game* does not.
			if (_agents.ShouldHoldTick(net.Tick))
			{
				return;
			}
		}

		net.BeginTick();

		if (net.IsServer)
		{
			ServerTick(net);
		}
		else if (net.IsClient)
		{
			ClientTick(net);
		}
		else
		{
			OfflineTick(net);
		}
	}

	/// <summary>
	/// Autoloads are ready before the main scene is, so the roster cannot be built
	/// in <c>_Ready</c> — the map's spawn points do not exist yet. It is built on the
	/// first physics tick, or on the first peer to connect, whichever comes first.
	/// </summary>
	private void EnsureStarted(NetworkManager net)
	{
		// A peer can connect in the frame between a listen host binding its port and
		// the map entering the tree. Its character is spawned either way; the roster
		// is not built off a scene with no spawn points in it.
		if (_started || !Session.InGame)
		{
			return;
		}

		_started = true;
		CollectSpawnPoints();

		if (net.HasLocalPlayer && !Bootstrap.IsDedicatedServer)
		{
			_sampler = new LocalInputSampler { Name = "LocalInputSampler" };
			AddChild(_sampler);
		}

		// A client's characters all arrive from the server, including its own. Bots
		// are the authority's business for the same reason units are: a client is
		// told about them and simulates neither (docs/NETCODE.md §9).
		if (net.IsClient)
		{
			return;
		}

		_bots = new BotDirector(CombatManager.Instance?.GameMode, Bootstrap.Options);

		if (Bootstrap.Options is { HasAgentApi: true })
		{
			_agents = AgentServer.TryStart(Bootstrap.Options);
			if (_agents == null)
			{
				// Fatal, for the reason a server that cannot bind its UDP port is fatal:
				// an agent channel that silently is not listening looks exactly like a
				// training run that is not learning (docs/AGENT_API.md §4.1).
				GD.PrintErr("gdpyr: --agent-api was given but the channel could not be opened");
				GetTree().Quit(1);
				return;
			}
		}

		// Only a host that is playing gets a character. A dedicated server used to
		// spawn one for itself too, and an unmanned body standing on a spawn point is
		// not free: units acquire it and shoot it, every one of those kills costs the
		// ground force a ticket, and its presence in the roster starts the round
		// before anybody has connected (see CombatManager.ServerRound) and reads to
		// the bot director as a person to backfill around.
		if (net.HasLocalPlayer)
		{
			_local = Spawn(net.LocalPeerId, NextSpawnIndex(), simulated: true, local: true);
		}
	}

	// ---- server ------------------------------------------------------------

	private void ServerTick(NetworkManager net)
	{
		// Bots before the roster loop: one that joins on this tick is simulated on
		// this tick, and one that has left is gone before anything asks it for a
		// frame (docs/IMPLEMENTATION_PLAN.md §M3.5).
		_bots?.ServerTick(net.Tick);

		SimulatePlayers(net);

		// Units first, then combat: a round a unit fired on this tick has to be in
		// the air before the projectiles are stepped and resolved, or every unit's
		// shot would be a tick late (docs/IMPLEMENTATION_PLAN.md §M3).
		UnitManager.Instance?.ServerTick(net.Tick);

		// After the roster loop, which is where a use press is read, and before
		// combat: a gun somebody let go of this tick is a gun that is on the ground
		// by the time anything asks where it is (docs/IMPLEMENTATION_PLAN.md §M5).
		EmplacementManager.Instance?.ServerTick(net.Tick);

		CombatManager.Instance?.ServerPostTick(net.Tick);

		if (net.Tick % SimConfig.SnapshotIntervalTicks == 0)
		{
			BroadcastSnapshot(net);
		}

		ApplyLocalFog();

		// Last, so an observation carries the state the tick actually ended in and an
		// event is on the stream before the trainer is asked to act on it.
		_agents?.AfterTick(net.Tick);
	}

	/// <summary>
	/// Simulates every character on the authority, each from whichever source owns
	/// its intent this tick: a remote client's jitter buffer, the local device, or a
	/// bot's brain. The three are resolved here and nowhere else — past this point
	/// nothing in the simulation knows or cares which it was.
	/// </summary>
	private void SimulatePlayers(NetworkManager net)
	{
		for (int i = 0; i < _ordered.Count; i++)
		{
			Player player = _ordered[i];
			InputFrame frame;

			if (player.IsBot)
			{
				frame = _bots?.Sample(player.PeerId, net.Tick)
					?? InputFrame.Neutral(net.Tick, player.Character.Yaw, player.Character.Pitch);
			}
			else if (player.Queue != null)
			{
				if (!player.Queue.TryPop(out frame))
				{
					// Nothing has ever arrived for this peer. Simulate a neutral frame
					// anyway so the character falls to the floor and settles, rather than
					// hanging where it spawned until its owner's first packet lands.
					frame = InputFrame.Neutral(net.Tick, player.Character.Yaw, player.Character.Pitch);
				}
			}
			else
			{
				// The listen host's own character: authoritative and locally driven, so
				// there is nothing to buffer and nothing to predict.
				frame = _sampler != null ? _sampler.Sample(net.Tick) : InputFrame.Neutral(net.Tick, player.Character.Yaw, player.Character.Pitch);
			}

			Simulate(player, frame, net.Tick, authoritative: true);
		}
	}

	/// <summary>
	/// One character's tick: movement, then its weapons, from one input frame.
	///
	/// The two are driven from here rather than from each other so that they see the
	/// same frame and the same button edges — a weapon that decided "was fire
	/// pressed" from a different record than the movement FSM used would fire on a
	/// tick the replay does not (docs/NETCODE.md §3.1).
	/// </summary>
	private void Simulate(Player player, InputFrame frame, uint tick, bool authoritative)
	{
		CombatManager combat = CombatManager.Instance;

		// A dead player keeps their look and loses everything else, on the server and
		// on their own client alike.
		if (combat != null && !combat.IsAlive(player.PeerId))
		{
			frame = InputFrame.LookOnly(frame);
		}

		var context = new InputContext(frame, player.Character.PreviousButtons, SimConfig.TickDelta);

		// What they are carrying decides how fast they walk, and it has to be in
		// place before the step rather than after it — on the server and on the
		// owning client alike, which is what keeps the two predicting the same walk
		// (docs/IMPLEMENTATION_PLAN.md §M5).
		player.Character.MoveSpeedScale = EmplacementManager.Instance?.MoveScaleOf(player.PeerId) ?? 1f;

		player.Character.Simulate(frame);

		if (authoritative)
		{
			combat?.ServerSimulate(player.PeerId, context, tick);
		}
		else
		{
			combat?.ClientSimulateLocal(context, tick);
		}
	}

	/// <summary>
	/// Sends every peer the roster it is allowed to have
	/// (docs/IMPLEMENTATION_PLAN.md §M4).
	///
	/// This is the harder half of the fog of war docs/NETCODE.md §6.2 warns about:
	/// one broadcast becomes one packet per strategist, because a ground-force
	/// player the strategist's units cannot see is a record that is not written.
	/// Everyone else — the ground force, who see each other with their eyes — still
	/// shares a single packet, and a round with no strategist connected is still a
	/// single broadcast.
	/// </summary>
	private void BroadcastSnapshot(NetworkManager net)
	{
		int[] peers = Multiplayer.GetPeers();
		if (peers.Length == 0)
		{
			return;
		}

		int count = CollectSnapshot(net);

		CombatManager combat = CombatManager.Instance;
		VisibilityService fog = combat?.Visibility;

		if (fog == null || !AnyStrategist(combat, peers))
		{
			byte[] broadcast = SnapshotCodec.Encode(net.Tick, _snapshotScratch.AsSpan(0, count));
			Rpc(MethodName.ServerSnapshot, broadcast);
			net.Stats.RecordSent(broadcast.Length * peers.Length);
			return;
		}

		// Encoded once and reused: every peer that is not a strategist gets the same
		// bytes, and the allocation is the one this method always made.
		byte[] unfiltered = null;

		for (int i = 0; i < peers.Length; i++)
		{
			int peerId = peers[i];

			if (combat.TeamOf(peerId) != Team.Strategist)
			{
				unfiltered ??= SnapshotCodec.Encode(net.Tick, _snapshotScratch.AsSpan(0, count));
				RpcId(peerId, MethodName.ServerSnapshot, unfiltered);
				net.Stats.RecordSent(unfiltered.Length);
				continue;
			}

			int visible = 0;
			for (int record = 0; record < count; record++)
			{
				if (fog.IsVisibleTo(peerId, _snapshotScratch[record]))
				{
					_visibleScratch[visible++] = _snapshotScratch[record];
				}
			}

			fog.CountWithheld(count - visible);

			byte[] payload = SnapshotCodec.Encode(net.Tick, _visibleScratch.AsSpan(0, visible));
			RpcId(peerId, MethodName.ServerSnapshot, payload);
			net.Stats.RecordSent(payload.Length);
		}
	}

	/// <summary>Builds the whole roster's records into <see cref="_snapshotScratch"/>; returns how many.</summary>
	private int CollectSnapshot(NetworkManager net)
	{
		int count = 0;
		for (int i = 0; i < _ordered.Count && count < SnapshotCodec.MaxPlayers; i++)
		{
			Player player = _ordered[i];
			fps_controller character = player.Character;
			PlayerCombat combat = CombatManager.Instance?.Find(player.PeerId);
			_snapshotScratch[count++] = new PlayerSnapshot
			{
				PeerId = player.PeerId,
				Position = character.SimPosition,
				Velocity = character.Velocity,
				Yaw = character.Yaw,
				Pitch = character.Pitch,
				LastInputTick = player.Queue?.AckTick ?? net.Tick,
				StateId = character.StateId,
				InputBufferDepth = (byte)Mathf.Clamp(player.Queue?.Depth ?? 0, 0, byte.MaxValue),
				Health = combat?.SnapshotHealth ?? (byte)SimConfig.MaxHealth,
				Ammo = combat?.SnapshotAmmo ?? (byte)0,
				WeaponFlags = combat?.SnapshotFlags ?? (byte)0,
			};
		}

		return count;
	}

	/// <summary>
	/// Whether anybody connected is a strategist, and therefore whether this tick's
	/// snapshot has to be built per peer at all. A bot strategist is not one: it has
	/// no client to send a packet to (docs/NETCODE.md §9).
	/// </summary>
	private static bool AnyStrategist(CombatManager combat, int[] peers)
	{
		for (int i = 0; i < peers.Length; i++)
		{
			if (combat.TeamOf(peers[i]) == Team.Strategist)
			{
				return true;
			}
		}

		return false;
	}

	/// <summary>
	/// Client -> server, unreliable, every tick. Carries the last few input frames,
	/// so a single dropped datagram is invisible (docs/NETCODE.md §3.2).
	/// </summary>
	[Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false,
		TransferMode = MultiplayerPeer.TransferModeEnum.Unreliable)]
	private void ClientInput(byte[] payload)
	{
		NetworkManager net = NetworkManager.Instance;
		if (net == null || !net.IsServer)
		{
			return;
		}

		net.Stats.RecordReceived(payload.Length);

		int sender = Multiplayer.GetRemoteSenderId();
		if (!_players.TryGetValue(sender, out Player player) || player.Queue == null)
		{
			return;
		}

		if (!InputCodec.TryDecode(payload, _inputScratch, out int count))
		{
			RejectedInputPackets++;
			return;
		}

		for (int i = 0; i < count; i++)
		{
			player.Queue.Accept(_inputScratch[i]);
		}
	}

	// ---- client ------------------------------------------------------------

	private void ClientTick(NetworkManager net)
	{
		ApplyPendingSnapshots(net);

		int steps = net.StepsThisTick;
		for (int i = 0; i < steps; i++)
		{
			uint tick = net.Clock.NextInputTick();
			InputFrame frame = _sampler != null ? _sampler.Sample(tick) : InputFrame.Neutral(tick);

			_lastSampledTick = tick;

			if (_local != null)
			{
				if (CombatManager.Instance is { } combat && !combat.IsAlive(_local.PeerId))
				{
					// Recorded as it will be simulated: the ledger must hold the frame the
					// replay will re-run, not the one the device produced.
					frame = InputFrame.LookOnly(frame);
				}

				Simulate(_local, frame, tick, authoritative: false);
				_ledger.Record(tick, frame, _local.Character.CaptureState(tick));
			}
			else
			{
				// Not spawned yet: still worth sending, so the server's buffer is warm
				// by the time it has a character to feed.
				_ledger.RecordInput(tick, frame);
			}
		}

		if (steps > 0 && net.Connected)
		{
			SendInput(net);
		}

		UpdateRemotes(net.Clock.RenderTick);
		UnitManager.Instance?.ClientTick(net.Clock.RenderTick);
		EmplacementManager.Instance?.ClientTick();
		CombatManager.Instance?.ClientPostTick(net.Tick);
	}

	private void SendInput(NetworkManager net)
	{
		int count = 0;
		for (int back = SimConfig.InputRedundancy - 1; back >= 0; back--)
		{
			long tick = (long)_lastSampledTick - back;
			if (tick < 0)
			{
				continue;
			}

			if (!_ledger.TryGetInput((uint)tick, out InputFrame frame))
			{
				// Never sampled — the clock stalled a tick to let the server catch up.
				continue;
			}
			_inputScratch[count++] = frame;
		}

		if (count == 0)
		{
			return;
		}

		byte[] payload = InputCodec.Encode(_inputScratch.AsSpan(0, count));
		RpcId(1, MethodName.ClientInput, payload);
		net.Stats.RecordSent(payload.Length);
	}

	/// <summary>Server -> clients, unreliable, at <see cref="SimConfig.SnapshotRate"/>.</summary>
	[Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false,
		TransferMode = MultiplayerPeer.TransferModeEnum.Unreliable)]
	private void ServerSnapshot(byte[] payload)
	{
		NetworkManager net = NetworkManager.Instance;
		if (net == null || !net.IsClient)
		{
			return;
		}

		net.Stats.RecordReceived(payload.Length);

		// Queued rather than applied here: reconciliation replays MoveAndSlide, which
		// is only valid inside the physics step, and this arrives on the idle frame.
		_pendingSnapshots.Add(payload);
		if (_pendingSnapshots.Count > MaxPendingSnapshots)
		{
			_pendingSnapshots.RemoveAt(0);
		}
	}

	private void ApplyPendingSnapshots(NetworkManager net)
	{
		for (int s = 0; s < _pendingSnapshots.Count; s++)
		{
			if (!SnapshotCodec.TryDecode(_pendingSnapshots[s], _snapshotScratch, out uint tick, out int count))
			{
				continue;
			}

			// Whoever it was about: this is the reference the fog is measured against,
			// and a packet that says nothing about a peer is exactly the evidence
			// (docs/NETCODE.md §6.2).
			if (tick > _newestSnapshotTick)
			{
				_newestSnapshotTick = tick;
			}

			for (int i = 0; i < count; i++)
			{
				int peerId = _snapshotScratch[i].PeerId;
				if (!_players.TryGetValue(peerId, out Player player))
				{
					// The spawn message has not arrived yet; the next snapshot will do.
					continue;
				}

				CombatManager.Instance?.ApplySnapshot(_snapshotScratch[i]);

				if (player == _local)
				{
					Reconcile(net, _snapshotScratch[i]);
				}
				else
				{
					player.Interpolator?.Push(tick, _snapshotScratch[i].Position,
						_snapshotScratch[i].Yaw, _snapshotScratch[i].Pitch);
				}
			}
		}

		_pendingSnapshots.Clear();
	}

	/// <summary>
	/// Compares the server's state for an acknowledged tick against what was
	/// predicted for it and, when they disagree, rewinds and replays the inputs the
	/// server had not yet seen (docs/NETCODE.md §3.2).
	/// </summary>
	private void Reconcile(NetworkManager net, in PlayerSnapshot snapshot)
	{
		// Feed the clock first: this is true even of a snapshot that says nothing new
		// about the prediction, and a starved buffer reports itself by repeating an
		// acknowledgement.
		net.Clock.OnInputBufferDepth(snapshot.InputBufferDepth);

		fps_controller character = _local.Character;
		ReplayDecision decision = _ledger.Decide(snapshot);

		switch (decision.Action)
		{
			case ReplayAction.Ignore:
				return;

			case ReplayAction.InSync:
				net.Stats.RecordPredictionError(decision.PositionError);
				return;

			case ReplayAction.Snap:
				character.RestoreState(snapshot.ToCharacterState());
				return;
		}

		net.Stats.RecordPredictionError(decision.PositionError);
		net.Stats.RecordMisprediction();

		Vector3 renderPositionBefore = character.GlobalPosition;
		character.RestoreState(snapshot.ToCharacterState());

		for (uint tick = decision.FromTick; tick <= decision.ToTick; tick++)
		{
			if (!_ledger.TryGetInput(tick, out InputFrame frame))
			{
				// Never sampled — the clock stalled that tick to let the server catch up.
				continue;
			}

			character.Simulate(frame);
			_ledger.UpdateState(tick, character.CaptureState(tick));
		}

		character.AbsorbVisualError(renderPositionBefore);
	}

	private void UpdateRemotes(float renderTick)
	{
		// Only a strategist is fogged. A ground-force client is sent everybody, so
		// there is nothing to infer and nothing to hide (docs/NETCODE.md §6.2).
		bool fogged = CombatManager.Instance is { Local.Team: Team.Strategist };

		for (int i = 0; i < _ordered.Count; i++)
		{
			Player player = _ordered[i];
			if (player == _local || player.Interpolator == null)
			{
				continue;
			}

			if (player.Interpolator.TrySample(renderTick, out Vector3 position, out float yaw, out float pitch))
			{
				player.Character.ApplyRemoteTransform(position, yaw, pitch);
			}
			player.Interpolator.Prune(renderTick);

			// The server hides an entity by leaving it out of the packet, so a record
			// that is behind the newest packet is the only notice a client gets that it
			// has lost contact. The body goes; the ghost the strategist steers by is
			// drawn by SelectionOverlay from the position it froze at.
			player.Character.SetFogHidden(fogged && Fog.IsLost(SnapshotAge(player)));
		}
	}

	/// <summary>
	/// Client-side: how far behind the newest snapshot this process has decoded the
	/// newest record for <paramref name="peerId"/> is, in ticks, or
	/// <see cref="float.MaxValue"/> when nothing has ever arrived for it. This is
	/// what a client has instead of a "you have lost contact" message
	/// (docs/NETCODE.md §6.2).
	/// </summary>
	public float SnapshotAgeTicks(int peerId) =>
		_players.TryGetValue(peerId, out Player player) ? SnapshotAge(player) : float.MaxValue;

	private float SnapshotAge(Player player)
	{
		if (player.Interpolator is not { Count: > 0 } buffer)
		{
			return float.MaxValue;
		}

		// A record cannot be newer than the packet that carried it, but an
		// out-of-order arrival can make it look that way for a frame.
		return buffer.NewestTick >= _newestSnapshotTick ? 0f : _newestSnapshotTick - buffer.NewestTick;
	}

	// ---- offline -----------------------------------------------------------

	private void OfflineTick(NetworkManager net)
	{
		// Offline is a server with nobody to tell: the same authoritative path, with
		// every broadcast skipped for want of a peer. Bots run here too — playing a
		// round on your own with no networking at all is the cheapest way to see
		// whether they are worth anything.
		_bots?.ServerTick(net.Tick);
		SimulatePlayers(net);
		UnitManager.Instance?.ServerTick(net.Tick);
		EmplacementManager.Instance?.ServerTick(net.Tick);
		CombatManager.Instance?.ServerPostTick(net.Tick);
		ApplyLocalFog();
	}

	/// <summary>
	/// Draws a local strategist's fog on the authority
	/// (docs/IMPLEMENTATION_PLAN.md §M4).
	///
	/// On a client the fog is a filter — the records never arrive. The authority has
	/// no such luxury, because it *is* the state, so a listen host hides the
	/// characters instead. That is a curtain and not protection, which is why the
	/// plan says every milestone is verified against the dedicated server: what a
	/// host can see is not what a player can (docs/NETCODE.md §6.2).
	/// </summary>
	private void ApplyLocalFog()
	{
		if (Bootstrap.IsDedicatedServer)
		{
			return;
		}

		CombatManager combat = CombatManager.Instance;
		if (combat?.Visibility == null)
		{
			return;
		}

		bool fogged = combat is { Local.Team: Team.Strategist };

		for (int i = 0; i < _ordered.Count; i++)
		{
			Player player = _ordered[i];
			if (player == _local || player.Character == null)
			{
				continue;
			}

			player.Character.SetFogHidden(fogged
				&& combat.TeamOf(player.PeerId) == Team.GroundForce
				&& !combat.Visibility.IsVisible(player.PeerId));
		}
	}

	// ---- roster ------------------------------------------------------------

	private void OnPeerJoined(int peerId)
	{
		NetworkManager net = NetworkManager.Instance;
		if (net == null || !net.IsServer)
		{
			return;
		}

		EnsureStarted(net);

		// Tell the newcomer who is already here before announcing it, so that its own
		// spawn is the last one it processes and every client ends up with the same
		// roster.
		for (int i = 0; i < _ordered.Count; i++)
		{
			RpcId(peerId, MethodName.SpawnPlayer, _ordered[i].PeerId, _ordered[i].SpawnIndex);
		}

		int spawnIndex = NextSpawnIndex();
		Player player = Spawn(peerId, spawnIndex, simulated: true, local: false);
		player.Queue = new ServerInputQueue();

		BroadcastSpawn(peerId, spawnIndex);
	}

	/// <summary>
	/// Puts a computer player on the field (docs/IMPLEMENTATION_PLAN.md §M3.5).
	/// Returns its character, or null when this process is not the authority or the
	/// id is taken.
	///
	/// It is deliberately the same spawn every other player gets, announced with the
	/// same reliable message: a client cannot tell a bot from a person, and does not
	/// need to.
	/// </summary>
	public fps_controller SpawnBot(int peerId)
	{
		NetworkManager net = NetworkManager.Instance;
		if (net is { IsClient: true } || _players.ContainsKey(peerId))
		{
			return null;
		}

		int spawnIndex = NextSpawnIndex();
		Player player = Spawn(peerId, spawnIndex, simulated: true, local: false);
		player.IsBot = true;

		BroadcastSpawn(peerId, spawnIndex);
		return player.Character;
	}

	/// <summary>Takes a computer player off the field again. Humans leave through <see cref="OnPeerLeft"/>.</summary>
	public void DespawnBot(int peerId)
	{
		if (NetworkManager.Instance is { IsClient: true } || !_players.TryGetValue(peerId, out Player player)
			|| !player.IsBot)
		{
			return;
		}

		Despawn(peerId);
		BroadcastDespawn(peerId);
	}

	/// <summary>
	/// Announces a spawn to whoever is connected. Guarded, because the authority is
	/// also a process that may have no peer at all — offline, or a listen host
	/// before anyone has joined.
	/// </summary>
	private void BroadcastSpawn(int peerId, int spawnIndex)
	{
		if (Multiplayer.HasMultiplayerPeer() && Multiplayer.GetPeers().Length > 0)
		{
			Rpc(MethodName.SpawnPlayer, peerId, spawnIndex);
		}
	}

	private void BroadcastDespawn(int peerId)
	{
		if (Multiplayer.HasMultiplayerPeer() && Multiplayer.GetPeers().Length > 0)
		{
			Rpc(MethodName.DespawnPlayer, peerId);
		}
	}

	private void OnPeerLeft(int peerId)
	{
		NetworkManager net = NetworkManager.Instance;
		if (net == null || !net.IsServer)
		{
			return;
		}

		Despawn(peerId);
		BroadcastDespawn(peerId);
	}

	/// <summary>Server -> clients, reliable: the roster is state, not a sample.</summary>
	[Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false,
		TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
	private void SpawnPlayer(int peerId, int spawnIndex)
	{
		NetworkManager net = NetworkManager.Instance;
		if (net == null || !net.IsClient || _players.ContainsKey(peerId))
		{
			return;
		}

		bool local = peerId == net.LocalPeerId;
		Player player = Spawn(peerId, spawnIndex, simulated: local, local: local);

		if (local)
		{
			_local = player;
			_ledger.Reset();

			// The fog's reference is a server tick, so it means nothing across a join:
			// a second server's clock does not continue the first one's, and a stale
			// high-water mark would read every record as ancient.
			_newestSnapshotTick = 0;
			_sampler?.Align(player.Character.Yaw, 0f);
		}
		else
		{
			player.Interpolator = new SnapshotInterpolator();
		}
	}

	[Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false,
		TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
	private void DespawnPlayer(int peerId)
	{
		if (NetworkManager.Instance is { IsClient: true })
		{
			Despawn(peerId);
		}
	}

	private Player Spawn(int peerId, int spawnIndex, bool simulated, bool local)
	{
		Transform3D spawn = SpawnTransform(spawnIndex);

		var character = _playerScene.Instantiate<fps_controller>();
		// Set before the node enters the tree: _Ready decides what to strip from the
		// scene from these, and reads its starting position from the transform.
		character.Name = $"Player_{peerId}";
		character.PeerId = peerId;
		character.IsSimulated = simulated;
		character.IsLocalPlayer = local;
		character.Position = spawn.Origin;
		character.Basis = spawn.Basis;

		_playersRoot.AddChild(character);

		var player = new Player
		{
			PeerId = peerId,
			SpawnIndex = spawnIndex,
			Character = character,
		};

		_players[peerId] = player;
		_ordered.Add(player);
		CombatManager.Instance?.Register(peerId, character, local);
		GD.Print($"[players] spawned peer {peerId} at spawn {spawnIndex}"
			+ $" (simulated: {simulated}, local: {local})");
		return player;
	}

	private void Despawn(int peerId)
	{
		if (!_players.Remove(peerId, out Player player))
		{
			return;
		}

		_ordered.Remove(player);
		_bots?.Release(peerId);
		CombatManager.Instance?.Unregister(peerId);
		if (_local == player)
		{
			_local = null;
		}
		player.Character.QueueFree();
		GD.Print($"[players] despawned peer {peerId}");
	}

	/// <summary>
	/// Puts a character back on a spawn point. Server-side, called by the combat
	/// manager on respawn: spawn points belong to the map and the roster, which are
	/// this class's business, not combat's.
	///
	/// <paramref name="variation"/> walks the player off its original spawn on each
	/// death, so repeatedly dying does not mean repeatedly reappearing in the same
	/// place for whoever is watching it.
	/// </summary>
	public bool TeleportToSpawn(int peerId, int variation)
	{
		if (!_players.TryGetValue(peerId, out Player player) || player.Character == null)
		{
			return false;
		}

		player.Character.Teleport(SpawnTransform(player.SpawnIndex + variation));
		return true;
	}

	/// <summary>The lowest spawn point nobody is using, so a rejoin does not stack players.</summary>
	private int NextSpawnIndex()
	{
		for (int index = 0; index < 64; index++)
		{
			bool taken = false;
			for (int i = 0; i < _ordered.Count && !taken; i++)
			{
				taken = _ordered[i].SpawnIndex == index;
			}
			if (!taken)
			{
				return index;
			}
		}
		return 0;
	}

	/// <summary>
	/// Spawn points come from the map, as nodes in the <c>player_spawn</c> group,
	/// sorted by name: every peer has to derive the same order from the same scene.
	/// </summary>
	private void CollectSpawnPoints()
	{
		var found = new List<Node3D>();
		foreach (Node node in GetTree().GetNodesInGroup(SpawnGroup))
		{
			if (node is Node3D spawn)
			{
				found.Add(spawn);
			}
		}

		found.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
		foreach (Node3D spawn in found)
		{
			_spawnPoints.Add(spawn.GlobalTransform);
		}

		if (_spawnPoints.Count == 0)
		{
			GD.PushWarning($"[players] no nodes in group '{SpawnGroup}'; using fallback spawn points");
		}
	}

	private Transform3D SpawnTransform(int index)
	{
		if (_spawnPoints.Count > 0)
		{
			return _spawnPoints[index % _spawnPoints.Count];
		}

		// Fallback ring, so a map with no spawn points is still playable rather than
		// stacking every player at the origin.
		float angle = index * Mathf.Tau / 8f;
		var origin = new Vector3(Mathf.Cos(angle) * 4f, 2f, Mathf.Sin(angle) * 4f);
		return new Transform3D(Basis.FromEuler(new Vector3(0f, -angle, 0f)), origin);
	}
}
