using System;
using System.Collections.Generic;
using Gdpyr.Agent;
using Gdpyr.Bots;
using Gdpyr.Core;
using Gdpyr.Fps;
using Gdpyr.Match;
using Gdpyr.Rts;
using Gdpyr.Sim;
using Gdpyr.Sim.Demo;
using Gdpyr.Ui;
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

		/// <summary>
		/// Demo playback only: the newest keyframe said this character was dead — or
		/// a strategist, which is never alive. The journal holds the frame as it was
		/// resolved, before the death rule was applied to it, so playback has to
		/// apply that rule too or a corpse walks (docs/DEMOS.md §5.2).
		/// </summary>
		public bool DemoDead;
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

	/// <summary>The demo being recorded, or null (docs/DEMOS.md §4).</summary>
	private DemoRecorder _demo;

	/// <summary>The demo being watched, or null (docs/DEMOS.md §5).</summary>
	private DemoPlayback _playback;

	/// <summary>
	/// The free camera a demo is watched through. Built with the first playback and
	/// kept afterwards: it is the only camera in the scene, and freeing it at the
	/// end of a demo would hand back the map through nothing at all.
	/// </summary>
	private DemoCamera _demoCamera;

	/// <summary>
	/// A demo the console asked for from the main menu, where there is no map to put
	/// it in yet. Picked up by <see cref="EnsureStarted"/> on the far side of the
	/// scene change, the same way a command line's mode is.
	/// </summary>
	private static DemoPlayback _pendingPlayback;

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

	/// <summary>
	/// The demo being recorded, or null. Read by <see cref="UnitManager"/>, which
	/// has the other half of what goes in a demo and needs to know whether to build
	/// a snapshot nobody is going to be sent.
	/// </summary>
	public DemoRecorder Recorder => _demo;

	/// <summary>The demo being watched, or null. Null in every live round.</summary>
	public DemoPlayback Playback => _playback;

	/// <summary>
	/// True once this process has watched a demo, and never false again. It is the
	/// difference the console needs between a map with a finished demo in it — which
	/// another demo may be loaded over — and a live round, which may not
	/// (docs/DEMOS.md §5.1).
	/// </summary>
	public bool IsDemoSession { get; private set; }

	/// <summary>
	/// True while this process is watching a demo rather than playing. Nothing is
	/// simulated from a device, no computer players are kept, and the agent channel
	/// is not opened: a replay is a thing being read, not a round.
	/// </summary>
	public bool IsPlayingDemo => _playback != null;

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

		// A demo is a file handle. The process going away must close it, or the last
		// few seconds of a round are in a buffer nobody flushed.
		_demo?.Stop();
		_demo = null;

		_playback?.Dispose();
		_playback = null;

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

		// A demo is read instead of a round being played, not alongside one: no
		// device is sampled, no bot thinks and no packet is sent (docs/DEMOS.md §5).
		if (_playback != null)
		{
			PlaybackTick();
			return;
		}

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

		// A demo the console opened while the main menu was up: there was no map to
		// put it in then and there is one now (docs/DEMOS.md §5.1).
		if (_pendingPlayback is { } pending)
		{
			_pendingPlayback = null;
			BeginPlayback(pending);
			return;
		}

		// `--playdemo` is the same thing said on the command line, and it is the only
		// launch that skips the roster entirely.
		if (Bootstrap.Options.PlayDemo is { } launchDemo)
		{
			if (!BeginPlaybackOf(launchDemo, out string demoError))
			{
				// Fatal for the reason an agent channel that will not open is: a process
				// launched to watch a demo and quietly not watching it is worse than one
				// that says why and stops.
				GD.PrintErr($"gdpyr: --playdemo {launchDemo}: {demoError}");
				GetTree().Quit(1);
			}
			return;
		}

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
			// Before the early return, and after nothing: a client's roster is empty
			// here — every character of it is still on its way over the wire — so there
			// is nothing for the recording to be opened *after*.
			StartLaunchRecording();
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

		StartLaunchRecording();
	}

	/// <summary>
	/// Opens the recording <c>--record</c> asked for, from the first tick of the
	/// round (docs/DEMOS.md §4.3). A client does this too, so a person who wants a
	/// demo of a whole session does not have to remember to type a command in the
	/// first second of it.
	/// </summary>
	private void StartLaunchRecording()
	{
		if (Bootstrap.Options.RecordDemo is not { } name)
		{
			return;
		}

		if (!StartRecording(name, out string message))
		{
			// Not fatal, unlike the agent channel: a round that is not being recorded
			// is still a round, and a full disk should not stop a server starting.
			GD.PushWarning($"[demo] --record {name}: {message}");
		}
	}

	// ---- demos (docs/DEMOS.md) ---------------------------------------------

	/// <summary>
	/// Starts recording. The kind is decided here and not by the caller, because it
	/// is not an option: an authority has every character's input and records a
	/// journal, a client has only the snapshots it was sent and records those
	/// (docs/DEMOS.md §4.1).
	///
	/// Returns false with a reason: a name that is not a file name, a directory
	/// that cannot be written, or a recording already in progress.
	/// </summary>
	public bool StartRecording(string name, out string message)
	{
		if (_demo != null)
		{
			message = $"already recording to {_demo.FileName}";
			return false;
		}

		if (_playback != null)
		{
			message = "already watching a demo";
			return false;
		}

		NetworkManager net = NetworkManager.Instance;
		if (net == null || !Session.InGame)
		{
			message = "there is no round to record";
			return false;
		}

		DemoKind kind = net.IsClient ? DemoKind.Stream : DemoKind.Journal;
		_demo = DemoRecorder.TryStart(name, kind, net.Tick, net.LocalPeerId, out message);
		if (_demo == null)
		{
			return false;
		}

		// The roster as it stands, before the first tick of it is written: a demo
		// that started mid-round has to say who was already on the field, or the
		// inputs that follow are about characters that never spawned.
		for (int i = 0; i < _ordered.Count; i++)
		{
			_demo.Spawn(net.Tick, _ordered[i].PeerId, _ordered[i].SpawnIndex, _ordered[i].IsBot);
		}

		message = $"recording to {_demo.FileName}";
		GD.Print($"[demo] {message} ({kind}, from tick {net.Tick})");
		return true;
	}

	/// <summary>Closes the recording. Returns false when there was none.</summary>
	public bool StopRecording(out string message)
	{
		if (_demo == null)
		{
			message = "not recording";
			return false;
		}

		DemoRecorder demo = _demo;
		_demo = null;
		demo.Stop();

		message = demo.Failed == null
			? $"wrote {demo}"
			: $"recording to {demo.FileName} failed: {demo.Failed}";
		GD.Print($"[demo] {message}");
		return demo.Failed == null;
	}

	/// <summary>
	/// Puts a line of text in the demo at the current tick, if one is being
	/// recorded. Nothing reads it back; it is there so that "watch this bit"
	/// survives into the file.
	/// </summary>
	public bool Mark(string text, out string message)
	{
		if (_demo == null)
		{
			message = "not recording";
			return false;
		}

		_demo.Mark(NetworkManager.Instance?.Tick ?? 0, text);
		message = "marked";
		return true;
	}

	/// <summary>
	/// Opens a demo and starts watching it (docs/DEMOS.md §5.1).
	///
	/// From the map this takes effect immediately, replacing whatever was being
	/// watched. From the main menu the demo is held and the map is loaded, because
	/// there is nothing to draw a round in until it is.
	/// </summary>
	public static bool BeginPlaybackOf(string name, out string message)
	{
		DemoPlayback playback = DemoPlayback.TryStart(name, out message);
		if (playback == null)
		{
			return false;
		}

		message = $"playing {playback.FileName}: {playback.Header}";

		if (Instance is { _started: true } manager)
		{
			manager.BeginPlayback(playback);
			return true;
		}

		_pendingPlayback?.Dispose();
		_pendingPlayback = playback;
		return true;
	}

	/// <summary>
	/// Points the demo camera at a peer's character, or frees it with peer 0.
	/// No-op when nothing is playing or when this process has no camera — a
	/// dedicated server reads a demo without looking at it.
	/// </summary>
	public void FollowInDemo(int peerId) => _demoCamera?.SetFollow(peerId);

	/// <summary>
	/// Stops watching. False when nothing was playing.
	///
	/// The field is left exactly where the demo left it and the camera is left where
	/// it was flying: freeing either would hand back an empty map through no camera
	/// at all, and the next <c>playdemo</c> clears the roster itself.
	/// </summary>
	public bool StopPlayback(out string message)
	{
		if (_playback == null)
		{
			message = "no demo is playing";
			return false;
		}

		message = $"stopped {_playback.FileName} at tick {_playback.Tick}";
		GD.Print($"[demo] {message}");

		_playback.Dispose();
		_playback = null;
		return true;
	}

	/// <summary>
	/// Swaps in a demo: whatever was on the field goes, and the file's own roster
	/// replaces it as its spawn records arrive.
	/// </summary>
	private void BeginPlayback(DemoPlayback playback)
	{
		_demo?.Stop();
		_demo = null;

		_agents?.Dispose();
		_agents = null;

		_bots = null;
		_playback?.Dispose();
		_playback = playback;
		IsDemoSession = true;

		ClearRoster();

		if (Bootstrap.IsDedicatedServer)
		{
			// Nothing to look through. A headless process can still read a demo — the
			// records are applied and the state is right — it just has no camera.
			return;
		}

		_demoCamera ??= new DemoCamera { Name = "DemoCamera" };
		if (_demoCamera.GetParent() == null)
		{
			AddChild(_demoCamera);
		}

		GD.Print($"[demo] playing {playback.FileName}: {playback.Header}");
	}

	/// <summary>Takes every character off the field, without telling anybody: nobody is connected.</summary>
	private void ClearRoster()
	{
		for (int i = _ordered.Count - 1; i >= 0; i--)
		{
			Despawn(_ordered[i].PeerId);
		}

		_local = null;
		UnitManager.Instance?.ClearUnits();
	}

	/// <summary>
	/// One frame of a demo: as many recorded ticks as the speed asks for, then the
	/// interpolated entities placed at the read head's render clock — an
	/// interpolation delay behind the newest keyframe, so that two of them bracket
	/// whatever is being drawn (<see cref="DemoPlayback.RenderTick"/>).
	/// </summary>
	private void PlaybackTick()
	{
		DemoPlayback playback = _playback;
		int steps = playback.Paused || playback.Ended ? 0 : playback.Speed;

		for (int i = 0; i < steps; i++)
		{
			if (!playback.TryAdvance(out IReadOnlyList<DemoRecord> records))
			{
				GD.Print(playback.Error == null
					? $"[demo] {playback.FileName} ended at tick {playback.Tick}"
					: $"[demo] {playback.FileName} ended at tick {playback.Tick}: {playback.Error}");
				GameConsole.Print(playback.Error == null
					? $"end of {playback.FileName}"
					: $"{playback.FileName} ends early: {playback.Error}");
				break;
			}

			ApplyPlaybackRecords(playback, records);
		}

		float renderTick = playback.RenderTick;
		UpdateRemotes(renderTick);
		UnitManager.Instance?.ClientTick(renderTick);
		_demoCamera?.Follow();
	}

	/// <summary>
	/// One recorded tick. Roster records come first in the file and are applied
	/// first here, so an input or a keyframe naming a character always finds one.
	/// </summary>
	private void ApplyPlaybackRecords(DemoPlayback playback, IReadOnlyList<DemoRecord> records)
	{
		for (int i = 0; i < records.Count; i++)
		{
			DemoRecord record = records[i];
			switch (record.Kind)
			{
				case DemoRecordKind.Spawn:
					PlaybackSpawn(playback, record);
					break;

				case DemoRecordKind.Despawn:
					Despawn(record.PeerId);
					break;

				case DemoRecordKind.Input:
					// The journal is what makes a demo a demo: between two keyframes the
					// characters are advanced by the same function the authority ran, over
					// the same frames, at the full simulation rate rather than interpolated
					// at the snapshot rate (docs/DEMOS.md §5.2).
					if (playback.Simulated && _players.TryGetValue(record.PeerId, out Player player))
					{
						player.Character.Simulate(player.DemoDead
							? InputFrame.LookOnly(record.Frame)
							: record.Frame);
					}
					break;

				case DemoRecordKind.PlayerSnapshot:
					ApplyPlaybackSnapshot(playback, record);
					break;

				case DemoRecordKind.UnitSnapshot:
					UnitManager.Instance?.ApplyRecordedSnapshot(record.Payload);
					break;

				case DemoRecordKind.Mark:
					GameConsole.Print($"[{playback.Seconds:0.0}s] {record.Text}");
					break;
			}
		}
	}

	private void PlaybackSpawn(DemoPlayback playback, in DemoRecord record)
	{
		if (_players.ContainsKey(record.PeerId))
		{
			return;
		}

		// A journalled character walks; a streamed one is placed. Either way it is
		// nobody's local player: a demo is watched from outside it, so no viewmodel
		// is built and no camera is taken (docs/DEMOS.md §5.2).
		Player player = Spawn(record.PeerId, record.SpawnIndex, simulated: playback.Simulated, local: false,
			registerCombat: false);

		if (!playback.Simulated)
		{
			player.Interpolator = new SnapshotInterpolator();
		}
	}

	/// <summary>
	/// The keyframe: what the authority said was true at this tick.
	///
	/// For a journalled demo it is a correction, applied the way a client applies
	/// one to its own character — the re-simulation between keyframes is free to
	/// drift, and this is what stops the drift accumulating. For a streamed one it
	/// is the only account of where anybody was, and goes to the interpolator.
	/// </summary>
	private void ApplyPlaybackSnapshot(DemoPlayback playback, in DemoRecord record)
	{
		if (!SnapshotCodec.TryDecode(record.Payload, _snapshotScratch, out uint tick, out int count))
		{
			return;
		}

		playback.NoteKeyframe(tick);

		for (int i = 0; i < count; i++)
		{
			ref PlayerSnapshot snapshot = ref _snapshotScratch[i];
			if (!_players.TryGetValue(snapshot.PeerId, out Player player))
			{
				continue;
			}

			// A strategist is never alive and neither is a corpse, so one test hides
			// both — which is the whole of what the presentation needs from a demo.
			player.DemoDead = !snapshot.IsAlive;
			player.Character.SetDeadPresentation(player.DemoDead);

			if (playback.Simulated)
			{
				player.Character.RestoreState(snapshot.ToCharacterState());
			}
			else
			{
				player.Interpolator?.Push(tick, snapshot.Position, snapshot.Yaw, snapshot.Pitch);
			}
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
			ReplicateSnapshot(net);
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

			// The journal, and the one place it could go: this is where the three
			// sources of a character's intent become one frame, and past it nothing
			// knows which it was. Recorded before Simulate rather than inside it, so
			// the file holds the frame as resolved and playback re-applies the rules
			// that frame is then put through — a dead player's look-only frame is
			// derived on both sides rather than recorded on one (docs/DEMOS.md §4.1).
			_demo?.Input(net.Tick, player.PeerId, frame);

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
	private void ReplicateSnapshot(NetworkManager net)
	{
		int[] peers = Multiplayer.HasMultiplayerPeer() ? Multiplayer.GetPeers() : Array.Empty<int>();

		// A demo is the second consumer of this packet, and an authority recording
		// one may have no peers at all — offline is the cheapest way to record
		// anything (docs/DEMOS.md §4.1). The roster is collected when either of the
		// two wants it and skipped when neither does.
		if (peers.Length == 0 && _demo == null)
		{
			return;
		}

		int count = CollectSnapshot(net);

		CombatManager combat = CombatManager.Instance;
		VisibilityService fog = combat?.Visibility;

		// Encoded once and reused: every peer that is not a strategist gets the same
		// bytes, the demo gets the same bytes, and the allocation is the one this
		// method always made.
		byte[] unfiltered = null;

		if (_demo != null)
		{
			// Deliberately the unfiltered roster. A demo recorded by the authority is a
			// record of the round and not of one peer's view of it — the fog is a rule
			// about what a *player* may know during a round, and there is no player
			// here (docs/DEMOS.md §4.1). A client recording one gets what it was sent,
			// fog and all, because that is all it has.
			unfiltered = SnapshotCodec.Encode(net.Tick, _snapshotScratch.AsSpan(0, count));
			_demo.PlayerSnapshot(net.Tick, unfiltered);
		}

		if (peers.Length == 0)
		{
			return;
		}

		if (fog == null || !AnyStrategist(combat, peers))
		{
			unfiltered ??= SnapshotCodec.Encode(net.Tick, _snapshotScratch.AsSpan(0, count));
			Rpc(MethodName.ServerSnapshot, unfiltered);
			net.Stats.RecordSent(unfiltered.Length * peers.Length);
			return;
		}

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

			// A client's half of the journal: its own intent, which is the only intent
			// it has. Recorded before the death rule is applied, exactly as the
			// authority records it, and blocked under the *server*-tick estimate
			// rather than the input clock that stamped the frame — the frame carries
			// its own tick, and the block index has to stay in one clock with the
			// snapshots beside it (docs/DEMOS.md §4.2).
			_demo?.Input(net.Tick, net.LocalPeerId, frame);

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

			// Recorded here rather than where it arrives, because this is where the
			// packet's own tick has been decoded and a demo's records are ordered by
			// the tick they are about (docs/DEMOS.md §4.2).
			_demo?.PlayerSnapshot(tick, _pendingSnapshots[s]);

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

		// The one broadcast an offline round does make, and only when something is
		// listening for it: a demo (docs/DEMOS.md §4.1). ReplicateSnapshot is a no-op
		// with no peers and no recording.
		if (_demo != null && net.Tick % SimConfig.SnapshotIntervalTicks == 0)
		{
			ReplicateSnapshot(net);
		}

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
		RecordRoster(peerId, spawnIndex, spawned: true);

		if (Multiplayer.HasMultiplayerPeer() && Multiplayer.GetPeers().Length > 0)
		{
			Rpc(MethodName.SpawnPlayer, peerId, spawnIndex);
		}
	}

	private void BroadcastDespawn(int peerId)
	{
		RecordRoster(peerId, 0, spawned: false);

		if (Multiplayer.HasMultiplayerPeer() && Multiplayer.GetPeers().Length > 0)
		{
			Rpc(MethodName.DespawnPlayer, peerId);
		}
	}

	/// <summary>
	/// Journals a roster change (docs/DEMOS.md §4.1). It is stamped with the tick
	/// that has just been simulated rather than the one about to be, because a peer
	/// joining arrives on the idle frame between the two, and a character that first
	/// moves on the next tick is a character that spawned at the end of this one.
	/// </summary>
	private void RecordRoster(int peerId, int spawnIndex, bool spawned)
	{
		if (_demo == null)
		{
			return;
		}

		uint tick = NetworkManager.Instance?.Tick ?? 0;
		if (spawned)
		{
			_demo.Spawn(tick, peerId, spawnIndex, _players.TryGetValue(peerId, out Player p) && p.IsBot);
		}
		else
		{
			_demo.Despawn(tick, peerId);
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

		_demo?.Spawn(net.Tick, peerId, spawnIndex, isBot: false);

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
		if (NetworkManager.Instance is { IsClient: true } net)
		{
			_demo?.Despawn(net.Tick, peerId);
			Despawn(peerId);
		}
	}

	/// <summary>
	/// Puts a character on the field. <paramref name="registerCombat"/> is false
	/// only for demo playback, where there is no round for a character to be part
	/// of: registering one would put it on a team, ask a side of it and give it a
	/// ticket to lose (docs/DEMOS.md §5.2).
	/// </summary>
	private Player Spawn(int peerId, int spawnIndex, bool simulated, bool local, bool registerCombat = true)
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

		if (registerCombat)
		{
			CombatManager.Instance?.Register(peerId, character, local);
		}

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
