using System;
using System.Buffers;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Gdpyr.Bots;
using Gdpyr.Core;
using Gdpyr.Fps;
using Gdpyr.Match;
using Gdpyr.Net;
using Gdpyr.Rts;
using Gdpyr.Sim;
using Gdpyr.Sim.Agent;
using Godot;

namespace Gdpyr.Agent;

/// <summary>
/// The agent control channel: an out-of-band socket that lets a process which is
/// not a Godot client take a seat in a round (docs/AGENT_API.md).
///
/// It is deliberately *beside* the game rather than inside it. A policy attaches
/// to a bot seat <see cref="BotDirector"/> has already created, so
/// <see cref="PlayerManager"/>, <see cref="CombatManager"/> and every client see
/// a round with one more bot in it — the same peer id, the same character, the
/// same loadout, the same ticket when it dies. The only new branch anywhere in
/// the game is one line in <see cref="BotDirector.Sample"/>.
///
/// Server-only, and off unless <c>--agent-api</c> was passed. The socket can
/// spawn players, issue orders and reset rounds, so the deploy unit never passes
/// it and a non-loopback bind without a token is refused at start-up
/// (<see cref="LaunchOptions"/>, docs/AGENT_API.md §4.1).
/// </summary>
public sealed partial class AgentServer : IDisposable
{
	public static AgentServer Instance { get; private set; }

	private readonly TcpListener _listener;
	private readonly List<AgentSession> _sessions = new();
	private readonly Dictionary<int, AgentSession> _byId = new();
	private readonly AgentSeatBook _book = new();
	private readonly AgentEventLog _events = new();
	private readonly AgentGroundView _groundView = new();
	private readonly AgentStrategistView _strategistView = new();
	private readonly AgentPlaneView _planes = new();
	private readonly byte[] _token;
	private readonly List<int> _scratchSeats = new();
	private readonly AgentEvent[] _drain = new AgentEvent[64];

	/// <summary>Unit ids read out of one order before they are copied into a seat's command list.</summary>
	private readonly int[] _unitIdScratch = new int[SimConfig.MaxUnits];

	/// <summary>One buffer per observation space, reused: an observation a tick is not an allocation a tick.</summary>
	private readonly float[] _groundObservation = new float[AgentObservation.Floats];

	private readonly float[] _strategistObservation = new float[AgentStrategistObservation.Floats];

	private byte[] _observationBytes;
	private int _nextSessionId = 1;
	private long _holdSinceMilliseconds;

	/// <summary>
	/// Hoisted out of <see cref="Pump"/>: it runs once per session per tick, and a
	/// closure allocated there would be per-tick garbage in the server loop, which
	/// is the one thing this codebase's hot paths do not do.
	/// </summary>
	private Action<AgentSession, AgentFrameKind, uint, ReadOnlyMemory<byte>> _handle;

	private uint _pumpTick;

	private AgentServer(TcpListener listener, LaunchOptions options)
	{
		_listener = listener;
		Limits = AgentLimits.From(BotTraits.Default, options.AgentUnbounded, options.AgentOmniscient);
		_token = options.AgentToken != null ? Encoding.UTF8.GetBytes(options.AgentToken) : null;

		_observationBytes = new byte[AgentStrategistObservation.Bytes + AgentFeaturePlanes.Bytes + 16];

		AgentEventBus.Active = _events;
	}

	/// <summary>The ceilings every attached seat plays under (docs/AGENT_API.md §7.3).</summary>
	public AgentLimits Limits { get; }

	public int SessionCount => _sessions.Count;

	public int AttachedSeats => _book.Count;

	/// <summary>True while a stepped session is holding the simulation. For the debug HUD.</summary>
	public bool Holding { get; private set; }

	/// <summary>
	/// Binds the listener, or returns null when <c>--agent-api</c> was not passed.
	///
	/// A bind that fails is fatal and says so, the same way a server that cannot
	/// bind its UDP port is fatal: an agent channel that silently is not listening
	/// would look exactly like a training run that is not learning.
	/// </summary>
	public static AgentServer TryStart(LaunchOptions options)
	{
		if (options is not { HasAgentApi: true })
		{
			return null;
		}

		IPAddress address;
		if (!IPAddress.TryParse(options.AgentHost, out address))
		{
			address = LaunchOptions.IsLoopback(options.AgentHost) ? IPAddress.Loopback : IPAddress.Any;
		}

		var listener = new TcpListener(address, options.AgentPort.Value);
		try
		{
			listener.Start();
		}
		catch (Exception error)
		{
			GD.PrintErr($"[agent] cannot bind {options.AgentHost}:{options.AgentPort}: {error.Message}");
			return null;
		}

		var server = new AgentServer(listener, options);
		Instance = server;

		GD.Print($"[agent] listening on {options.AgentHost}:{options.AgentPort}"
			+ $" | schema {AgentProtocol.SchemaVersion} | {AgentObservation.Floats} floats"
			+ (options.AgentToken != null ? " | token required" : string.Empty)
			+ (options.AgentUnbounded ? " | UNBOUNDED" : string.Empty)
			+ (options.AgentOmniscient ? " | OMNISCIENT (cheating)" : string.Empty));
		return server;
	}

	// ---- the tick ----------------------------------------------------------

	/// <summary>
	/// Accepts connections and drains every socket. Runs before the gate, so an
	/// action that arrives while the sim is held is seen on the tick it unblocks.
	/// </summary>
	public void Pump(uint tick)
	{
		Accept(tick);

		_pumpTick = tick;
		_handle ??= (s, kind, correlation, body) => Dispatch(s, kind, correlation, body.Span, _pumpTick);

		for (int i = _sessions.Count - 1; i >= 0; i--)
		{
			AgentSession session = _sessions[i];
			if (!session.Receive(_handle))
			{
				Drop(session, "closed");
				continue;
			}

			if (!session.Authenticated
				&& Now() - session.ConnectedAtMilliseconds > AgentSession.AuthDeadlineMilliseconds)
			{
				session.SendError(0, "unauthenticated", "no hello within the authentication deadline");
				Drop(session, "unauthenticated");
			}
		}
	}

	/// <summary>
	/// Whether the simulation must stand still this tick (docs/AGENT_API.md §5.2).
	///
	/// A stepped session that has not asked for a tick holds the whole sim, which is
	/// what makes a regression test synchronous instead of sleep-and-hope. It also
	/// means a hung trainer hangs the round, so the hold is bounded: past
	/// <c>step_timeout_ms</c> the session is told and dropped back to real time
	/// rather than the server standing still forever.
	/// </summary>
	public bool ShouldHoldTick(uint tick)
	{
		bool hold = false;
		for (int i = 0; i < _sessions.Count; i++)
		{
			AgentSession session = _sessions[i];
			if (!session.Authenticated || !session.Stepped)
			{
				continue;
			}

			if (HumanConnected())
			{
				// Stepping would stall a person's clock and then resync it, which makes
				// the round unplayable. The refusal is loud rather than quiet.
				session.Stepped = false;
				session.PendingSteps = 0;
				session.SendError(0, "human_connected",
					"stepped mode cannot run while a human peer is connected; dropped back to real time");
				continue;
			}

			if (session.PendingSteps <= 0)
			{
				hold = true;
			}
		}

		if (!hold)
		{
			Holding = false;
			_holdSinceMilliseconds = 0;
			return false;
		}

		long now = Now();
		if (!Holding)
		{
			Holding = true;
			_holdSinceMilliseconds = now;
			return true;
		}

		for (int i = 0; i < _sessions.Count; i++)
		{
			AgentSession session = _sessions[i];
			if (!session.Stepped || session.PendingSteps > 0
				|| now - _holdSinceMilliseconds <= session.StepTimeoutMilliseconds)
			{
				continue;
			}

			session.Stepped = false;
			if (session.StepOutstanding)
			{
				session.StepOutstanding = false;
				session.SendJson(AgentFrameKind.Response, session.StepCorrelation, writer =>
				{
					writer.WriteString("op", "step");
					writer.WriteNumber("tick", tick);
					writer.WriteBoolean("truncated", true);
				});
			}

			session.SendError(0, "step_timeout",
				$"no step within {session.StepTimeoutMilliseconds} ms; dropped back to real time");
		}

		return ShouldStillHold();
	}

	/// <summary>
	/// Observations, events and any outstanding <c>step</c>, once the tick is done.
	/// </summary>
	public void AfterTick(uint tick)
	{
		for (int i = _sessions.Count - 1; i >= 0; i--)
		{
			AgentSession session = _sessions[i];
			if (!session.Authenticated)
			{
				continue;
			}

			if (session.PendingSteps > 0)
			{
				session.PendingSteps--;
			}

			PushEvents(session);
			PushObservations(session, tick);

			if (session.StepOutstanding && session.PendingSteps <= 0)
			{
				session.StepOutstanding = false;
				SendStepResponse(session, tick);
			}

			if (session.Closed)
			{
				Drop(session, "closed");
			}
		}
	}

	/// <summary>
	/// The frame an attached ground seat is simulated from this tick, if it has one.
	///
	/// This is the one branch the rest of the game learns about, and it lives in
	/// <see cref="BotDirector.Sample"/> — the one class that already knows bots
	/// exist as a category. False means "not attached, or the lease is in its grace
	/// window", and the caller falls back to <see cref="BotPilot"/>.
	/// </summary>
	public bool TrySample(int peerId, uint tick, out InputFrame frame)
	{
		frame = default;
		if (!_book.TryGet(peerId, out AgentSeat seat) || seat.Kind != AgentPolicyKind.Ground)
		{
			return false;
		}

		bool stepped = _byId.TryGetValue(seat.SessionId, out AgentSession session) && session.Stepped;

		// Stepped mode has no grace: the gate already guarantees the action is for
		// this tick, and a fallback would quietly make an episode non-reproducible.
		int grace = stepped ? int.MaxValue : seat.GraceTicks(AgentSeatBook.DefaultGraceTicks);
		if (seat.ShouldPilot(tick, grace))
		{
			seat.CountFallback();
			return false;
		}

		fps_controller character = PlayerManager.Instance?.CharacterOf(peerId);
		if (character == null)
		{
			return false;
		}

		frame = AgentActionCodec.Resolve(seat.Action, tick, character.Yaw, character.Pitch, Limits,
			SimConfig.TickDelta);
		return true;
	}

	public bool IsAttached(int peerId) => _book.IsAttached(peerId);

	/// <summary>
	/// Whether an external policy is driving this strategist seat this tick, and if
	/// so, the commands it asked for (docs/AGENT_API.md §7.4).
	///
	/// The strategist half of <see cref="TrySample"/>, and it lives in the same
	/// place for the same reason: one branch in <see cref="BotDirector"/>, the one
	/// class that already knows computer players exist as a category. False means
	/// "not attached, or the lease is in its grace window", and the caller falls
	/// back to <see cref="BotStrategist"/>.
	///
	/// A command list is consumed rather than held: holding one for the seat's
	/// <c>step_mul</c> would put the same unit on the same queue thirty times.
	/// </summary>
	public bool TryCommand(int peerId, uint tick)
	{
		if (!_book.TryGet(peerId, out AgentSeat seat) || seat.Kind != AgentPolicyKind.Strategist)
		{
			return false;
		}

		bool stepped = _byId.TryGetValue(seat.SessionId, out AgentSession session) && session.Stepped;
		int grace = stepped ? int.MaxValue : seat.GraceTicks(AgentSeatBook.DefaultGraceTicks);
		if (seat.ShouldPilot(tick, grace))
		{
			seat.CountFallback();
			return false;
		}

		if (seat.HasPendingCommands)
		{
			seat.CommandsRun(ApplyCommands(seat, tick));
		}

		// The seat is the policy's whether or not this decision asked for anything:
		// an attached strategist that means to build nothing this second is not a
		// strategist that wants its bot back.
		return true;
	}

	/// <summary>
	/// Runs one decision's commands, in order, through the same server-side entry
	/// points a person's RPCs land in — so an agent's order goes through the
	/// identical ownership checks, and a unit id the seat does not own is counted in
	/// <c>UnitManager.RejectedOrders</c> rather than silently doing nothing
	/// (docs/AGENT_API.md §7.4).
	///
	/// The APM cap is spent here rather than at submission, because it is a cap on
	/// what the game does and not on what the socket carries.
	/// </summary>
	private int ApplyCommands(AgentSeat seat, uint tick)
	{
		UnitManager units = UnitManager.Instance;
		AgentCommandList list = seat.Commands;
		if (units == null || list == null || list.Count == 0)
		{
			return 0;
		}

		int allowed = seat.Budget.Take(tick, list.Count, Limits);
		int ran = 0;

		foreach (AgentCommand command in list.Commands)
		{
			if (ran >= allowed)
			{
				break;
			}

			ran++;
			var at = new Vector3(command.X, 0f, command.Z);

			switch (command.Kind)
			{
				case AgentCommandKind.Order:
					units.ServerIssueOrder(seat.PeerId, list.UnitsOf(command), (OrderKind)command.Order, at,
						command.TargetOwnerId);
					break;

				case AgentCommandKind.Build:
					units.ServerQueueUnit(seat.PeerId, command.Barracks, command.Tier);
					break;

				case AgentCommandKind.Cancel:
					units.ServerCancelBuild(seat.PeerId, command.Barracks);
					break;

				case AgentCommandKind.Rally:
					units.ServerSetRally(seat.PeerId, command.Barracks, at);
					break;

				case AgentCommandKind.Construct:
					// The ground under the point is found by the server; the policy only
					// names where on the map (docs/NETCODE.md §10.5).
					units.ServerConstruct(seat.PeerId, list.UnitsOf(command), command.Structure, at, command.Yaw,
						out _);
					break;

				case AgentCommandKind.Noop:
				default:
					break;
			}
		}

		return ran;
	}

	/// <summary>
	/// Gives a seat back to its bot. Called when a session closes, when a policy
	/// detaches, and when the roster despawns the bot out from under it.
	/// </summary>
	public void ReleaseSeat(int peerId, string reason)
	{
		if (!_book.TryGet(peerId, out AgentSeat seat))
		{
			return;
		}

		_book.Detach(peerId);
		if (_byId.TryGetValue(seat.SessionId, out AgentSession session))
		{
			session.DropSeat(peerId);
		}

		_events.Emit(AgentEvent.Make(AgentEventKind.SeatReleased, NetworkManager.Instance?.Tick ?? 0,
			peerId, (int)seat.Kind, reason == "detach" ? 0 : 1));
		GD.Print($"[agent] seat {BotRoster.NameOf(peerId)} released ({reason})");
	}

	public void Dispose()
	{
		for (int i = _sessions.Count - 1; i >= 0; i--)
		{
			_sessions[i].Close();
		}

		_sessions.Clear();
		_byId.Clear();
		_book.Clear();

		try
		{
			_listener.Stop();
		}
		catch (Exception)
		{
			// Already down.
		}

		if (AgentEventBus.Active == _events)
		{
			AgentEventBus.Active = null;
		}

		if (Instance == this)
		{
			Instance = null;
		}
	}

	// ---- sessions ----------------------------------------------------------

	private void Accept(uint tick)
	{
		while (true)
		{
			bool pending;
			try
			{
				pending = _listener.Pending();
			}
			catch (Exception)
			{
				return;
			}

			if (!pending)
			{
				return;
			}

			TcpClient client;
			try
			{
				client = _listener.AcceptTcpClient();
			}
			catch (Exception)
			{
				return;
			}

			var session = new AgentSession(_nextSessionId++, client, Now())
			{
				EventCursor = _events.Sequence,
			};

			_sessions.Add(session);
			_byId[session.Id] = session;
			GD.Print($"[agent] session {session.Id} connected at tick {tick}");
		}
	}

	private void Drop(AgentSession session, string reason)
	{
		_scratchSeats.Clear();
		for (int i = session.Seats.Count - 1; i >= 0; i--)
		{
			_scratchSeats.Add(session.Seats[i]);
		}

		for (int i = 0; i < _scratchSeats.Count; i++)
		{
			ReleaseSeat(_scratchSeats[i], reason);
		}

		session.Close();
		_sessions.Remove(session);
		_byId.Remove(session.Id);
		GD.Print($"[agent] session {session.Id} gone ({reason})");
	}

	private bool ShouldStillHold()
	{
		for (int i = 0; i < _sessions.Count; i++)
		{
			if (_sessions[i].Stepped && _sessions[i].PendingSteps <= 0)
			{
				return true;
			}
		}

		Holding = false;
		_holdSinceMilliseconds = 0;
		return false;
	}

	private static long Now() => (long)Time.GetTicksMsec();

	/// <summary>
	/// Whether anybody is playing through the ordinary client socket. Stepped mode
	/// refuses while one is (docs/AGENT_API.md §5.2).
	/// </summary>
	private static bool HumanConnected()
	{
		NetworkManager net = NetworkManager.Instance;
		if (net == null)
		{
			return false;
		}

		if (net.HasLocalPlayer)
		{
			return true;
		}

		PlayerManager players = PlayerManager.Instance;
		return players != null && players.RemotePeerCount > 0;
	}
}
