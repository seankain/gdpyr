using System.Collections.Generic;
using Gdpyr.Bots;
using Gdpyr.Core;
using Gdpyr.Sim;
using Gdpyr.Sim.AiDebug;
using Gdpyr.Ui;
using Gdpyr.Ui.Spectator;
using Godot;

namespace Gdpyr.Net;

/// <summary>
/// Spectators, and the AI debugger they watch through (docs/AI_DEBUG.md §2, §6).
///
/// A spectator is a connected peer with no character. It is in no roster, no
/// snapshot record and no team — the wire's team is one bit and stays one bit
/// (<see cref="Team"/>) — so nothing that counts players, spawns them, asks them for
/// a side or fogs their packets needs to learn a third kind of peer. What it does get
/// is every broadcast every other peer gets, and the ground force's unfiltered view
/// of the roster, which it is sent because it is not a strategist.
///
/// The authority keeps who is spectating and what each spectator has asked the
/// debugger for, and sends each one a frame of its bots' plans ten times a second.
/// A listen host or an offline round can spectate too: it is then its own spectator,
/// and its frames go from this file to its own panel without touching a socket.
/// </summary>
public partial class PlayerManager
{
	/// <summary>Ticks between AI debug frames: ten a second. The planners themselves rescan every ten ticks.</summary>
	public const int AiDebugIntervalTicks = SimConfig.TickRate / 10;

	/// <summary>How soon a peer may change its mind again. Spectating spawns and despawns a character.</summary>
	private const int SpectateCooldownTicks = SimConfig.TickRate;

	/// <summary>A watch request is a few dozen bytes; anything much longer is not one.</summary>
	private const int MaxWatchBytes = 64;

	private sealed class SpectatorSeat
	{
		public readonly AiWatch Watch = new();
	}

	/// <summary>Authority: who is spectating, and what each wants from the debugger.</summary>
	private readonly Dictionary<int, SpectatorSeat> _spectators = new();
	private readonly Dictionary<int, uint> _spectateChangedTick = new();
	private readonly AiWatch _watchScratch = new();

	private AiDebugPublisher _aiPublisher;
	private SpectatorView _spectatorView;

	/// <summary>A spectate asked for by the main menu before the map existed (<see cref="SpectateOnStart"/>).</summary>
	private static bool _pendingSpectate;

	/// <summary>The client's own <c>--spectate</c>, asked once the link is up.</summary>
	private bool _launchSpectateSent;

	/// <summary>Authority: peers spectating, this process included when it is one. Counted by the bot fill policy.</summary>
	public int SpectatorCount => _spectators.Count;

	public bool IsSpectator(int peerId) => _spectators.ContainsKey(peerId);

	/// <summary>True while this process is watching rather than playing.</summary>
	public bool IsSpectating { get; private set; }

	/// <summary>Whether the authority this process spectates sends its bots' plans.</summary>
	public bool SpectatorAiDebug { get; private set; }

	/// <summary>The newest debug frame, or null; replaced ten times a second while anything is watched.</summary>
	public AiDebugFrame AiFrame { get; private set; }

	/// <summary>When <see cref="AiFrame"/> arrived, in engine milliseconds: how stale it is.</summary>
	public ulong AiFrameReceivedMsec { get; private set; }

	/// <summary>The authority's reader of its own planners, for the console. Null on a client.</summary>
	public AiDebugPublisher AiPublisher =>
		_bots == null ? null : _aiPublisher ??= new AiDebugPublisher(_bots);

	/// <summary>Called by the main menu's "Watch the bots": spectate as soon as the round is up.</summary>
	public static void SpectateOnStart() => _pendingSpectate = true;

	/// <summary>
	/// Asks to spectate, or to stop and be asked for a side like somebody who has just
	/// joined. Applied at once on the authority; a client hears back with
	/// <see cref="ServerSpectating"/>.
	/// </summary>
	public void RequestSpectate(bool watch)
	{
		NetworkManager net = NetworkManager.Instance;
		if (net == null || !_started || _playback != null)
		{
			return;
		}

		if (net.IsClient)
		{
			if (net.Connected)
			{
				RpcId(1, MethodName.ClientSpectate, watch);
			}

			return;
		}

		if (net.HasLocalPlayer)
		{
			ServerSetSpectating(net.LocalPeerId, watch);
		}
	}

	/// <summary>Tells the authority what this spectator wants from the debugger. Sent when it changes.</summary>
	public void SendAiWatch(AiWatch watch)
	{
		NetworkManager net = NetworkManager.Instance;
		if (net == null || !IsSpectating || watch == null)
		{
			return;
		}

		if (net.IsClient)
		{
			if (net.Connected)
			{
				byte[] payload = AiDebugCodec.EncodeWatch(watch);
				RpcId(1, MethodName.ClientAiWatch, payload);
				net.Stats.RecordSent(payload.Length);
			}

			return;
		}

		if (_spectators.TryGetValue(net.LocalPeerId, out SpectatorSeat seat))
		{
			seat.Watch.CopyFrom(watch);
		}
	}

	// ---- authority ---------------------------------------------------------

	[Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false,
		TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
	private void ClientSpectate(bool watch)
	{
		if (NetworkManager.Instance is { IsServer: true })
		{
			ServerSetSpectating(Multiplayer.GetRemoteSenderId(), watch);
		}
	}

	[Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false,
		TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
	private void ClientAiWatch(byte[] payload)
	{
		if (NetworkManager.Instance is not { IsServer: true } net || payload == null)
		{
			return;
		}

		net.Stats.RecordReceived(payload.Length);

		// Only a spectator is listened to: the plans are a spectator's to see
		// (docs/AI_DEBUG.md §6), and a player asking is a player asking for the other
		// side's intentions.
		int sender = Multiplayer.GetRemoteSenderId();
		if (payload.Length > MaxWatchBytes || !_spectators.TryGetValue(sender, out SpectatorSeat seat)
			|| !AiDebugCodec.TryDecodeWatch(payload, _watchScratch))
		{
			return;
		}

		seat.Watch.CopyFrom(_watchScratch);
	}

	/// <summary>
	/// Takes a peer off the field to watch, or puts it back on it. Back on means what
	/// joining means: a fresh character at a free spawn, and the role menu — so leaving
	/// a spectator's seat is a choice of side like any other, under the same cap.
	/// </summary>
	private void ServerSetSpectating(int peerId, bool watch)
	{
		NetworkManager net = NetworkManager.Instance;
		if (net == null || net.IsClient || BotRoster.IsBot(peerId))
		{
			return;
		}

		if (watch == _spectators.ContainsKey(peerId))
		{
			// Already so: say it again, in case the answer was what got lost.
			NotifySpectating(peerId, watch);
			return;
		}

		if (_spectateChangedTick.TryGetValue(peerId, out uint changed) && net.Tick < changed + SpectateCooldownTicks)
		{
			return;
		}

		_spectateChangedTick[peerId] = net.Tick;
		bool local = peerId == net.LocalPeerId && net.HasLocalPlayer;

		if (watch)
		{
			_spectators[peerId] = new SpectatorSeat();

			// Before the body goes: a client puts its free camera up on this message, and
			// the reliable channel delivers it ahead of the despawn.
			NotifySpectating(peerId, true);

			if (_players.ContainsKey(peerId))
			{
				Despawn(peerId);
				BroadcastDespawn(peerId);
			}

			GD.Print($"[players] {BotRoster.NameOf(peerId)} is spectating ({_spectators.Count} watching)");
			return;
		}

		_spectators.Remove(peerId);
		NotifySpectating(peerId, false);

		if (!_players.ContainsKey(peerId))
		{
			int spawnIndex = NextSpawnIndex();
			Player player = Spawn(peerId, spawnIndex, simulated: true, local: local);
			if (local)
			{
				_local = player;
			}
			else
			{
				player.Queue = new ServerInputQueue();
			}

			BroadcastSpawn(peerId, spawnIndex);
		}

		GD.Print($"[players] {BotRoster.NameOf(peerId)} stopped spectating ({_spectators.Count} watching)");
	}

	private void NotifySpectating(int peerId, bool watching)
	{
		NetworkManager net = NetworkManager.Instance;
		bool aiDebug = Bootstrap.Options.AiDebug;

		if (net != null && peerId == net.LocalPeerId && !net.IsClient)
		{
			// A listen host or an offline round: its own bots' plans are in its own
			// memory, so the debugger is always on for it.
			ApplySpectating(watching, aiDebug: true);
			return;
		}

		if (Multiplayer.HasMultiplayerPeer())
		{
			RpcId(peerId, MethodName.ServerSpectating, watching, aiDebug);
		}
	}

	/// <summary>A peer has gone: it is nobody's spectator any more.</summary>
	private void ForgetSpectator(int peerId)
	{
		_spectators.Remove(peerId);
		_spectateChangedTick.Remove(peerId);
	}

	/// <summary>
	/// Every <see cref="AiDebugIntervalTicks"/>, a frame per spectator that has asked for
	/// one. Last in the tick, so it shows the plans the tick ended with.
	/// </summary>
	private void PublishAiDebug(NetworkManager net)
	{
		if (_spectators.Count == 0 || _bots == null || net.Tick % AiDebugIntervalTicks != 0)
		{
			return;
		}

		foreach (KeyValuePair<int, SpectatorSeat> entry in _spectators)
		{
			AiWatch watch = entry.Value.Watch;
			if (watch.IsEmpty)
			{
				continue;
			}

			bool local = entry.Key == net.LocalPeerId && !net.IsClient;
			if (!local && !Bootstrap.Options.AiDebug)
			{
				continue;
			}

			byte[] payload = AiDebugCodec.EncodeFrame(AiPublisher.Build(net.Tick, watch));
			if (local)
			{
				// The same bytes a remote spectator decodes: one path, so what a listen
				// host sees is what a spectator across the room sees.
				ReceiveAiFrame(payload);
				continue;
			}

			RpcId(entry.Key, MethodName.ServerAiFrame, payload);
			net.Stats.RecordSent(payload.Length);
		}
	}

	// ---- this process as a spectator ----------------------------------------

	/// <summary>Server -> one peer: you are spectating now, or not; and whether the bots' plans will come.</summary>
	[Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false,
		TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
	private void ServerSpectating(bool watching, bool aiDebug)
	{
		if (NetworkManager.Instance is { IsClient: true })
		{
			ApplySpectating(watching, aiDebug);
		}
	}

	/// <summary>Server -> one spectator, ten times a second. Unreliable: the next one replaces a lost one.</summary>
	[Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false,
		TransferMode = MultiplayerPeer.TransferModeEnum.Unreliable)]
	private void ServerAiFrame(byte[] payload)
	{
		if (NetworkManager.Instance is { IsClient: true } net && payload != null)
		{
			net.Stats.RecordReceived(payload.Length);
			ReceiveAiFrame(payload);
		}
	}

	private void ReceiveAiFrame(byte[] payload)
	{
		if (!IsSpectating || !AiDebugCodec.TryDecodeFrame(payload, out AiDebugFrame frame))
		{
			return;
		}

		// Unreliable: an older frame that arrives late is dropped rather than shown.
		if (AiFrame != null && frame.Tick < AiFrame.Tick)
		{
			return;
		}

		AiFrame = frame;
		AiFrameReceivedMsec = Time.GetTicksMsec();
	}

	private void ApplySpectating(bool watching, bool aiDebug)
	{
		bool changed = watching != IsSpectating;
		IsSpectating = watching;
		SpectatorAiDebug = aiDebug;

		if (!watching)
		{
			AiFrame = null;
		}

		if (Bootstrap.IsDedicatedServer)
		{
			return;
		}

		if (watching)
		{
			if (_spectatorView == null)
			{
				_spectatorView = new SpectatorView { Name = "SpectatorView" };
				AddChild(_spectatorView);
			}
		}
		else if (_spectatorView != null)
		{
			_spectatorView.QueueFree();
			_spectatorView = null;
		}

		if (changed)
		{
			GameConsole.Print(watching
				? "spectating: F3 or 'play' to play; click a bot or a unit to see its plan"
				: "back in the round: pick a side");
		}
	}

	/// <summary>Drops every spectator and this process's own seat among them: a demo is starting.</summary>
	private void EndSpectating()
	{
		_spectators.Clear();
		_spectateChangedTick.Clear();
		_aiPublisher = null;
		if (IsSpectating)
		{
			ApplySpectating(false, aiDebug: false);
		}
	}

	/// <summary>
	/// Starts this process's spectating where a launch or the main menu asked for it:
	/// on the authority as soon as the roster is up, on a client once it is connected.
	/// </summary>
	private void SpectateIfAsked(NetworkManager net)
	{
		if (_launchSpectateSent || _playback != null || !(Bootstrap.Options.Spectate || _pendingSpectate))
		{
			return;
		}

		if (net.IsClient && !net.Connected)
		{
			return;
		}

		_launchSpectateSent = true;
		_pendingSpectate = false;
		RequestSpectate(true);
	}
}
