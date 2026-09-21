using System;
using System.Collections.Generic;
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
	}

	private readonly Dictionary<int, Player> _players = new();
	private readonly List<Player> _ordered = new();
	private readonly List<byte[]> _pendingSnapshots = new();

	private readonly PredictionLedger _ledger = new();
	private readonly InputFrame[] _inputScratch = new InputFrame[InputCodec.MaxFrames];
	private readonly PlayerSnapshot[] _snapshotScratch = new PlayerSnapshot[SnapshotCodec.MaxPlayers];

	private readonly List<Transform3D> _spawnPoints = new();

	private PackedScene _playerScene;
	private Node3D _playersRoot;
	private LocalInputSampler _sampler;
	private Player _local;
	private bool _started;

	private uint _lastSampledTick;

	/// <summary>Ticks replayed by the last reconciliation. For the net HUD.</summary>
	public int LastReplayTicks => _ledger.LastReplayLength;

	/// <summary>Input packets that failed to decode. Anything but 0 is a bug or an attack.</summary>
	public int RejectedInputPackets { get; private set; }

	public int PlayerCount => _ordered.Count;

	public string LocalStateName => _local?.Character?.StateName ?? "-";

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

		EnsureStarted(net);
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
		if (_started)
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

		// A client's characters all arrive from the server, including its own.
		if (net.IsClient)
		{
			return;
		}

		Player host = Spawn(net.LocalPeerId, NextSpawnIndex(), simulated: true, local: net.HasLocalPlayer);
		_local = net.HasLocalPlayer ? host : null;
	}

	// ---- server ------------------------------------------------------------

	private void ServerTick(NetworkManager net)
	{
		for (int i = 0; i < _ordered.Count; i++)
		{
			Player player = _ordered[i];
			InputFrame frame;

			if (player.Queue != null)
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

		// Units first, then combat: a round a unit fired on this tick has to be in
		// the air before the projectiles are stepped and resolved, or every unit's
		// shot would be a tick late (docs/IMPLEMENTATION_PLAN.md §M3).
		UnitManager.Instance?.ServerTick(net.Tick);
		CombatManager.Instance?.ServerPostTick(net.Tick);

		if (net.Tick % SimConfig.SnapshotIntervalTicks == 0)
		{
			BroadcastSnapshot(net);
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

	private void BroadcastSnapshot(NetworkManager net)
	{
		int peers = Multiplayer.GetPeers().Length;
		if (peers == 0)
		{
			return;
		}

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

		byte[] payload = SnapshotCodec.Encode(net.Tick, _snapshotScratch.AsSpan(0, count));
		Rpc(MethodName.ServerSnapshot, payload);
		net.Stats.RecordSent(payload.Length * peers);
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
		}
	}

	// ---- offline -----------------------------------------------------------

	private void OfflineTick(NetworkManager net)
	{
		if (_local == null || _sampler == null)
		{
			return;
		}

		// Offline is a server with nobody to tell: the same authoritative path, with
		// every broadcast a no-op for want of a peer.
		Simulate(_local, _sampler.Sample(net.Tick), net.Tick, authoritative: true);
		UnitManager.Instance?.ServerTick(net.Tick);
		CombatManager.Instance?.ServerPostTick(net.Tick);
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

		Rpc(MethodName.SpawnPlayer, peerId, spawnIndex);
	}

	private void OnPeerLeft(int peerId)
	{
		NetworkManager net = NetworkManager.Instance;
		if (net == null || !net.IsServer)
		{
			return;
		}

		Despawn(peerId);
		Rpc(MethodName.DespawnPlayer, peerId);
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
