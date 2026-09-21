using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Gdpyr.Bots;
using Gdpyr.Fps;
using Gdpyr.Match;
using Gdpyr.Net;
using Gdpyr.Rts;
using Gdpyr.Sim;
using Gdpyr.Sim.Agent;
using Godot;

namespace Gdpyr.Agent;

/// <summary>
/// The control plane: what each <c>op</c> does (docs/AGENT_API.md §7).
///
/// Split from the transport half so the socket bookkeeping and the game calls
/// read separately. Every op that changes the world goes through a server-side
/// entry point a client's RPC already goes through, which is what keeps an
/// agent's order behind the identical ownership and fairness checks a person's
/// is.
/// </summary>
public sealed partial class AgentServer
{
	private void Dispatch(AgentSession session, AgentFrameKind kind, uint correlation, ReadOnlySpan<byte> body,
		uint tick)
	{
		// Kind 3 is the packed-binary lane in both directions: an observation from
		// the server, a packed action from the agent (docs/AGENT_API.md §4, §7.2).
		if (kind == AgentFrameKind.Observation)
		{
			ActBinary(session, correlation, body, tick);
			return;
		}

		if (kind != AgentFrameKind.Request)
		{
			session.SendError(correlation, "bad_frame", $"a client may not send frame kind {kind}");
			return;
		}

		JsonDocument document;
		try
		{
			document = JsonDocument.Parse(body.ToArray());
		}
		catch (JsonException error)
		{
			session.SendError(correlation, "bad_json", error.Message);
			return;
		}

		using (document)
		{
			JsonElement root = document.RootElement;
			if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("op", out JsonElement opValue)
				|| opValue.ValueKind != JsonValueKind.String)
			{
				session.SendError(correlation, "bad_request", "every request needs a string 'op'");
				return;
			}

			string op = opValue.GetString();
			if (!session.Authenticated && op != "hello")
			{
				session.SendError(correlation, "unauthenticated", "say hello first");
				return;
			}

			switch (op)
			{
				case "hello": Hello(session, correlation, root, tick); break;
				case "config": Config(session, correlation, root); break;
				case "list_seats": ListSeats(session, correlation); break;
				case "attach": Attach(session, correlation, root, tick); break;
				case "detach": Detach(session, correlation, root); break;
				case "act": Act(session, correlation, root, tick); break;
				case "observe": Observe(session, correlation, root, tick); break;
				case "step": Step(session, correlation, root, tick); break;
				case "reset": Reset(session, correlation, root, tick); break;
				case "events": Events(session, correlation, root); break;
				case "state_hash": StateHash(session, correlation, tick); break;
				case "quit": session.Close(); break;
				default: session.SendError(correlation, "unknown_op", $"no op '{op}'"); break;
			}
		}
	}

	// ---- handshake ---------------------------------------------------------

	private void Hello(AgentSession session, uint correlation, JsonElement root, uint tick)
	{
		int protocol = Int(root, "protocol", AgentProtocol.Version);
		if (protocol != AgentProtocol.Version)
		{
			session.SendError(correlation, "protocol_mismatch",
				$"server speaks protocol {AgentProtocol.Version}, client asked for {protocol}");
			session.Close();
			return;
		}

		if (_token != null && !TokenMatches(Text(root, "token")))
		{
			session.SendError(correlation, "bad_token", "token rejected");
			session.Close();
			return;
		}

		session.BinaryObservations = Text(root, "observations", "binary") != "json";
		session.Authenticated = true;

		CombatManager combat = CombatManager.Instance;
		session.SendJson(AgentFrameKind.Response, correlation, writer =>
		{
			writer.WriteString("op", "welcome");
			writer.WriteNumber("protocol", AgentProtocol.Version);
			writer.WriteNumber("tick_rate", SimConfig.TickRate);
			writer.WriteNumber("tick", tick);
			writer.WriteNumber("seed", combat?.RoundSeed ?? 0);
			writer.WriteString("build", Engine.GetVersionInfo()["string"].AsString());
			writer.WriteString("observations", session.BinaryObservations ? "binary" : "json");
			writer.WriteString("mode", session.Stepped ? "stepped" : "realtime");

			// Both research flags are stamped into the handshake, so a trace can never
			// be read as an ordinary round (docs/AGENT_API.md §6.4, §7.3).
			writer.WriteBoolean("unbounded", Limits.Unbounded);
			writer.WriteBoolean("omniscient", Limits.Omniscient);
			writer.WriteNumber("turn_rate_radians", Limits.Unbounded ? 0f : Limits.TurnRateRadians);
			writer.WriteNumber("commands_per_second", Limits.Unbounded ? 0 : Limits.CommandsPerSecond);
			writer.WriteNumber("grace_ticks", AgentSeatBook.DefaultGraceTicks);
			AgentJson.WriteSchema(writer);
		});

		GD.Print($"[agent] session {session.Id} said hello"
			+ $" | observations {(session.BinaryObservations ? "binary" : "json")}");
	}

	private bool TokenMatches(string offered)
	{
		if (offered == null)
		{
			return false;
		}

		byte[] bytes = Encoding.UTF8.GetBytes(offered);
		return CryptographicOperations.FixedTimeEquals(bytes, _token);
	}

	private void Config(AgentSession session, uint correlation, JsonElement root)
	{
		string mode = Text(root, "mode");
		if (mode == "stepped")
		{
			if (HumanConnected())
			{
				session.SendError(correlation, "human_connected",
					"stepped mode cannot engage while a human peer is connected: their clock would resync"
					+ " and the round would be unplayable");
				return;
			}

			session.Stepped = true;
			session.PendingSteps = 0;
		}
		else if (mode == "realtime")
		{
			session.Stepped = false;
			session.PendingSteps = 0;
		}
		else if (mode != null)
		{
			session.SendError(correlation, "bad_request", "mode is 'realtime' or 'stepped'");
			return;
		}

		if (root.TryGetProperty("step_timeout_ms", out JsonElement timeout) && timeout.TryGetInt32(out int ms))
		{
			session.StepTimeoutMilliseconds = Math.Clamp(ms, 100, 600_000);
		}

		string observations = Text(root, "observations");
		if (observations != null)
		{
			session.BinaryObservations = observations != "json";
		}

		session.SendJson(AgentFrameKind.Response, correlation, writer =>
		{
			writer.WriteString("op", "config");
			writer.WriteString("mode", session.Stepped ? "stepped" : "realtime");
			writer.WriteNumber("step_timeout_ms", session.StepTimeoutMilliseconds);
			writer.WriteString("observations", session.BinaryObservations ? "binary" : "json");
		});
	}

	// ---- seats -------------------------------------------------------------

	private void ListSeats(AgentSession session, uint correlation)
	{
		CombatManager combat = CombatManager.Instance;
		BotDirector bots = PlayerManager.Instance?.Bots;

		session.SendJson(AgentFrameKind.Response, correlation, writer =>
		{
			writer.WriteString("op", "seats");
			writer.WriteStartArray("seats");
			for (int i = 0; combat != null && i < combat.PlayerCount; i++)
			{
				PlayerCombat player = combat.PlayerAt(i);
				if (player == null)
				{
					continue;
				}

				AgentSeatController controller = ControllerOf(player.PeerId, bots);
				writer.WriteStartObject();
				writer.WriteNumber("peer", player.PeerId);
				writer.WriteString("team", player.Team == Team.Strategist ? "strategist" : "ground");
				writer.WriteString("controller", controller.ToString().ToLowerInvariant());
				writer.WriteBoolean("alive", player.IsAlive);
				if (_book.TryGet(player.PeerId, out AgentSeat seat))
				{
					writer.WriteNumber("session", seat.SessionId);
					writer.WriteNumber("step_mul", seat.StepMul);
				}
				writer.WriteEndObject();
			}
			writer.WriteEndArray();
		});
	}

	private AgentSeatController ControllerOf(int peerId, BotDirector bots)
	{
		if (_book.IsAttached(peerId))
		{
			return AgentSeatController.Agent;
		}

		return bots != null && bots.IsBot(peerId) ? AgentSeatController.Bot : AgentSeatController.Human;
	}

	/// <summary>
	/// Claims a seat by team and policy kind, spawning a bot when the side has room
	/// (docs/AGENT_API.md §2.2). A caller may name a <c>peer_id</c> to re-attach to
	/// a seat it held before a reconnect.
	/// </summary>
	private void Attach(AgentSession session, uint correlation, JsonElement root, uint tick)
	{
		BotDirector bots = PlayerManager.Instance?.Bots;
		if (bots == null)
		{
			session.SendError(correlation, "no_roster", "this process has no bot roster to attach to");
			return;
		}

		string policyText = Text(root, "policy", "ground");
		AgentPolicyKind policy = policyText == "strategist"
			? AgentPolicyKind.Strategist
			: AgentPolicyKind.Ground;

		if (policy == AgentPolicyKind.Strategist)
		{
			// The strategist observation and the command list are M7
			// (docs/IMPLEMENTATION_PLAN.md §M7). Refused rather than half-answered: a
			// seat that attached and then saw nothing would be a worse bug.
			session.SendError(correlation, "not_implemented",
				"strategist seats arrive with M7; attach with policy 'ground'");
			return;
		}

		Team team = Text(root, "team", "ground") == "strategist" ? Team.Strategist : Team.GroundForce;
		int requested = Int(root, "peer_id", 0);
		int stepMul = Int(root, "step_mul", AgentSeatBook.DefaultStepMul(policy));

		int peerId = requested != 0 ? requested : FreeBotSeat(bots, team);
		if (peerId == 0)
		{
			session.SendError(correlation, "no_free_seat",
				$"no bot seat free on {team} and the roster has no room for another");
			return;
		}

		AgentSeatController controller = ControllerOf(peerId, bots);
		AgentSeat seat = _book.Attach(peerId, controller, policy, session.Id, stepMul, tick,
			out AgentAttachRefusal refusal);

		if (seat == null)
		{
			session.SendError(correlation, refusal switch
			{
				AgentAttachRefusal.SeatIsHuman => "seat_is_human",
				AgentAttachRefusal.SeatIsTaken => "seat_is_taken",
				_ => "no_free_seat",
			}, $"seat {peerId} refused: {refusal}");
			return;
		}

		session.HoldSeat(peerId);
		_events.Emit(AgentEvent.Make(AgentEventKind.SeatAttached, tick, peerId, (int)policy, seat.StepMul));

		session.SendJson(AgentFrameKind.Response, correlation, writer =>
		{
			writer.WriteString("op", "attached");
			writer.WriteNumber("seat", peerId);
			writer.WriteString("policy", policyText);
			writer.WriteString("team", team == Team.Strategist ? "strategist" : "ground");
			writer.WriteNumber("step_mul", seat.StepMul);
			writer.WriteNumber("attached_tick", tick);
		});

		GD.Print($"[agent] session {session.Id} attached to {BotRoster.NameOf(peerId)}"
			+ $" | step_mul {seat.StepMul}");
	}

	/// <summary>
	/// A bot seat on <paramref name="team"/> nobody has claimed, or a fresh one when
	/// the side is under its target and the roster has room.
	/// </summary>
	private int FreeBotSeat(BotDirector bots, Team team)
	{
		CombatManager combat = CombatManager.Instance;
		for (int i = 0; combat != null && i < combat.PlayerCount; i++)
		{
			PlayerCombat player = combat.PlayerAt(i);
			if (player == null || player.Team != team || !bots.IsBot(player.PeerId)
				|| _book.IsAttached(player.PeerId))
			{
				continue;
			}

			return player.PeerId;
		}

		return bots.SpawnSeat(team);
	}

	private void Detach(AgentSession session, uint correlation, JsonElement root)
	{
		int peerId = Int(root, "seat", 0);
		if (!_book.TryGet(peerId, out AgentSeat seat) || seat.SessionId != session.Id)
		{
			session.SendError(correlation, "not_attached", $"this session does not hold seat {peerId}");
			return;
		}

		ReleaseSeat(peerId, "detach");
		session.SendJson(AgentFrameKind.Response, correlation, writer =>
		{
			writer.WriteString("op", "detached");
			writer.WriteNumber("seat", peerId);
		});
	}

	// ---- actions -----------------------------------------------------------

	/// <summary>
	/// One or more packed ground actions in a single kind-3 frame
	/// (docs/AGENT_API.md §7.2). Silent on success: a trainer at 15 Hz per seat does
	/// not want an acknowledgement per action.
	/// </summary>
	private void ActBinary(AgentSession session, uint correlation, ReadOnlySpan<byte> body, uint tick)
	{
		if (!session.Authenticated)
		{
			session.SendError(correlation, "unauthenticated", "say hello first");
			return;
		}

		int at = 0;
		while (at + AgentActionCodec.GroundSizeBytes <= body.Length)
		{
			if (!AgentActionCodec.TryDecodeGround(body[at..], out int peerId, out uint actionTick,
				out AgentGroundAction action))
			{
				session.SendError(correlation, "bad_action", "packed action did not decode");
				return;
			}

			Submit(session, correlation, peerId, actionTick == 0 ? tick : actionTick, action);
			at += AgentActionCodec.GroundSizeBytes;
		}
	}

	private void Act(AgentSession session, uint correlation, JsonElement root, uint tick)
	{
		int peerId = Int(root, "seat", 0);
		uint actionTick = (uint)Math.Max(Int(root, "tick", (int)tick), 0);

		if (!root.TryGetProperty("action", out JsonElement action) || action.ValueKind != JsonValueKind.Object)
		{
			session.SendError(correlation, "bad_request", "act needs an 'action' object");
			return;
		}

		if (action.TryGetProperty("commands", out _))
		{
			session.SendError(correlation, "not_implemented",
				"strategist commands arrive with M7 (docs/IMPLEMENTATION_PLAN.md §M7)");
			return;
		}

		var parsed = new AgentGroundAction();
		if (action.TryGetProperty("move", out JsonElement move) && move.ValueKind == JsonValueKind.Array
			&& move.GetArrayLength() >= 2)
		{
			parsed.MoveX = (float)move[0].GetDouble();
			parsed.MoveZ = (float)move[1].GetDouble();
		}

		if (action.TryGetProperty("look", out JsonElement look) && look.ValueKind == JsonValueKind.Object)
		{
			if (look.TryGetProperty("dyaw", out JsonElement dyaw))
			{
				parsed.LookIsDelta = true;
				parsed.Yaw = (float)dyaw.GetDouble();
				parsed.Pitch = look.TryGetProperty("dpitch", out JsonElement dpitch)
					? (float)dpitch.GetDouble()
					: 0f;
			}
			else if (look.TryGetProperty("yaw", out JsonElement yaw))
			{
				parsed.Yaw = (float)yaw.GetDouble();
				parsed.Pitch = look.TryGetProperty("pitch", out JsonElement pitch)
					? (float)pitch.GetDouble()
					: 0f;
			}
		}

		if (action.TryGetProperty("buttons", out JsonElement buttons) && buttons.ValueKind == JsonValueKind.Array)
		{
			ushort held = 0;
			foreach (JsonElement button in buttons.EnumerateArray())
			{
				if (button.ValueKind == JsonValueKind.String)
				{
					held |= (ushort)AgentActionCodec.ButtonFromName(button.GetString());
				}
			}

			parsed.Buttons = held;
		}

		Submit(session, correlation, peerId, actionTick, parsed);
	}

	private void Submit(AgentSession session, uint correlation, int peerId, uint actionTick,
		in AgentGroundAction action)
	{
		if (!_book.TryGet(peerId, out AgentSeat seat) || seat.SessionId != session.Id)
		{
			session.SendError(correlation, "not_attached", $"this session does not hold seat {peerId}");
			return;
		}

		seat.Submit(actionTick, action);
	}

	// ---- observations ------------------------------------------------------

	private void Observe(AgentSession session, uint correlation, JsonElement root, uint tick)
	{
		int peerId = Int(root, "seat", 0);
		if (!_book.TryGet(peerId, out AgentSeat seat) || seat.SessionId != session.Id)
		{
			session.SendError(correlation, "not_attached", $"this session does not hold seat {peerId}");
			return;
		}

		if (!Encode(seat, tick))
		{
			session.SendError(correlation, "no_character", $"seat {peerId} has no character this tick");
			return;
		}

		SendObservation(session, correlation, seat, tick, AgentFrameKind.Response);
	}

	private bool Encode(AgentSeat seat, uint tick) =>
		_view.Encode(seat.PeerId, tick, seat.StepMul, Limits, _observation);

	private void SendObservation(AgentSession session, uint correlation, AgentSeat seat, uint tick,
		AgentFrameKind kind)
	{
		if (session.BinaryObservations)
		{
			// seat (i32) · flags (u16) · reserved (u16) · tick (u32) · floats, so a
			// decoder never has to pair an observation with a separate envelope to know
			// whose it is. The seat is a peer id and peer ids are ints, so the field is
			// as wide as one (see AgentActionCodec.GroundSizeBytes).
			int header = 12;
			int size = header + AgentObservation.Bytes;
			if (_observationBytes.Length < size)
			{
				_observationBytes = new byte[size];
			}

			Span<byte> frame = _observationBytes.AsSpan(0, size);
			BinaryPrimitives.WriteInt32LittleEndian(frame, seat.PeerId);
			BinaryPrimitives.WriteUInt16LittleEndian(frame[4..], (ushort)((Limits.Omniscient ? 1 : 0)
				| (Limits.Unbounded ? 2 : 0)));
			BinaryPrimitives.WriteUInt16LittleEndian(frame[6..], 0);
			BinaryPrimitives.WriteUInt32LittleEndian(frame[8..], tick);
			for (int i = 0; i < AgentObservation.Floats; i++)
			{
				BinaryPrimitives.WriteSingleLittleEndian(frame[(header + (i * 4))..], _observation[i]);
			}

			session.Send(kind == AgentFrameKind.Response ? AgentFrameKind.Observation : kind, correlation, frame);
			return;
		}

		session.SendJson(kind, correlation, writer =>
		{
			writer.WriteString("op", "observation");
			writer.WriteNumber("seat", seat.PeerId);
			writer.WriteNumber("tick", tick);
			writer.WriteBoolean("omniscient", Limits.Omniscient);
			writer.WriteBoolean("unbounded", Limits.Unbounded);
			AgentJson.WriteObservation(writer, _observation);
		});
	}

	/// <summary>Pushes an observation for every seat whose decision tick this is (docs/AGENT_API.md §5.3).</summary>
	private void PushObservations(AgentSession session, uint tick)
	{
		for (int i = 0; i < session.Seats.Count; i++)
		{
			if (!_book.TryGet(session.Seats[i], out AgentSeat seat) || !seat.IsDecisionTick(tick))
			{
				continue;
			}

			if (Encode(seat, tick))
			{
				SendObservation(session, 0, seat, tick, AgentFrameKind.Observation);
			}
		}
	}

	// ---- stepping ----------------------------------------------------------

	private void Step(AgentSession session, uint correlation, JsonElement root, uint tick)
	{
		if (!session.Stepped)
		{
			session.SendError(correlation, "not_stepped", "send config {mode:'stepped'} before step");
			return;
		}

		if (session.StepOutstanding)
		{
			session.SendError(correlation, "step_outstanding", "one step at a time");
			return;
		}

		int ticks = Math.Clamp(Int(root, "n", 1), 1, SimConfig.TickRate * 60);
		session.PendingSteps = ticks;
		session.StepOutstanding = true;
		session.StepCorrelation = correlation;
	}

	private void SendStepResponse(AgentSession session, uint tick)
	{
		session.SendJson(AgentFrameKind.Response, session.StepCorrelation, writer =>
		{
			writer.WriteString("op", "step");
			writer.WriteNumber("tick", tick);
			writer.WriteBoolean("truncated", false);
			writer.WriteStartArray("seats");
			for (int i = 0; i < session.Seats.Count; i++)
			{
				if (!_book.TryGet(session.Seats[i], out AgentSeat seat))
				{
					continue;
				}

				writer.WriteStartObject();
				writer.WriteNumber("seat", seat.PeerId);
				writer.WriteBoolean("alive", CombatManager.Instance?.IsAlive(seat.PeerId) ?? false);
				writer.WriteNumber("pilot_fallbacks", seat.PilotFallbacks);
				writer.WriteEndObject();
			}
			writer.WriteEndArray();
		});

		// The observations follow the acknowledgement, one frame per seat, in the
		// format this session asked for. A trainer reads the step, then reads one
		// observation per seat it holds.
		for (int i = 0; i < session.Seats.Count; i++)
		{
			if (_book.TryGet(session.Seats[i], out AgentSeat seat) && Encode(seat, tick))
			{
				SendObservation(session, 0, seat, tick, AgentFrameKind.Observation);
			}
		}
	}

	// ---- episodes ----------------------------------------------------------

	private void Reset(AgentSession session, uint correlation, JsonElement root, uint tick)
	{
		CombatManager combat = CombatManager.Instance;
		if (combat == null)
		{
			session.SendError(correlation, "no_round", "this process is not running a round");
			return;
		}

		combat.RoundSeed = Int(root, "seed", combat.RoundSeed);
		combat.ServerResetRound(tick);

		session.SendJson(AgentFrameKind.Response, correlation, writer =>
		{
			writer.WriteString("op", "reset");
			writer.WriteNumber("tick", tick);
			writer.WriteNumber("seed", combat.RoundSeed);
		});
	}

	private void Events(AgentSession session, uint correlation, JsonElement root)
	{
		ulong cursor = root.TryGetProperty("since", out JsonElement since) && since.TryGetUInt64(out ulong value)
			? value
			: session.EventCursor;

		int count = _events.Since(cursor, _drain, out ulong next, out ulong lost);
		AgentEvent[] drained = _drain;

		session.SendJson(AgentFrameKind.Response, correlation, writer =>
		{
			writer.WriteString("op", "events");
			writer.WriteNumber("next", next);
			writer.WriteNumber("lost", lost);
			writer.WriteStartArray("events");
			for (int i = 0; i < count; i++)
			{
				AgentJson.WriteEvent(writer, drained[i]);
			}
			writer.WriteEndArray();
		});
	}

	/// <summary>Drains the stream to this session as unsolicited kind-4 frames.</summary>
	private void PushEvents(AgentSession session)
	{
		while (true)
		{
			int count = _events.Since(session.EventCursor, _drain, out ulong next, out ulong lost);
			if (count == 0)
			{
				session.EventCursor = next;
				return;
			}

			AgentEvent[] drained = _drain;
			session.SendJson(AgentFrameKind.Event, 0, writer =>
			{
				writer.WriteString("op", "events");
				writer.WriteNumber("next", next);
				writer.WriteNumber("lost", lost);
				writer.WriteStartArray("events");
				for (int i = 0; i < count; i++)
				{
					AgentJson.WriteEvent(writer, drained[i]);
				}
				writer.WriteEndArray();
			});

			session.EventCursor = next;
			if (session.Closed)
			{
				return;
			}
		}
	}

	/// <summary>
	/// A hash of the tick's simulation state (docs/AGENT_API.md §3).
	///
	/// It exists to *measure* divergence rather than to promise there is none:
	/// <c>Scripts/Sim</c> is reproducible, <c>MoveAndSlide()</c> and
	/// <c>NavigationAgent3D</c> are not, and the tick at which two seeded episodes
	/// part company is the number that decides how tightly an assertion may be
	/// written.
	/// </summary>
	private void StateHash(AgentSession session, uint correlation, uint tick)
	{
		AgentStateHash hash = AgentStateHash.Start();
		CombatManager combat = CombatManager.Instance;
		UnitManager units = UnitManager.Instance;

		if (combat != null)
		{
			hash.Add((int)combat.Match.Phase);
			hash.Add(combat.Match.GroundTickets);
			hash.Add(combat.Match.StrategistPoints);

			for (int i = 0; i < combat.PlayerCount; i++)
			{
				PlayerCombat player = combat.PlayerAt(i);
				if (player?.Character == null)
				{
					continue;
				}

				hash.Add(player.PeerId);
				hash.Add(player.Health);
				hash.Add(player.Equipped.Ammo);
				hash.Add(player.Character.StateId);
				hash.AddPosition(player.Character.SimPosition);
				hash.AddVelocity(player.Character.Velocity);
				hash.Add(player.Character.Yaw, 0.0001f);
				hash.Add(player.Character.Pitch, 0.0001f);
			}
		}

		for (int i = 0; units != null && i < units.SlotCount; i++)
		{
			Unit unit = units.UnitAt(i);
			if (unit == null)
			{
				continue;
			}

			hash.Add(unit.UnitId);
			hash.Add(unit.Health, 0.01f);
			hash.Add((int)unit.Order.Kind);
			hash.AddPosition(unit.GlobalPosition);
		}

		ulong value = hash.Value;
		session.SendJson(AgentFrameKind.Response, correlation, writer =>
		{
			writer.WriteString("op", "state_hash");
			writer.WriteNumber("tick", tick);
			writer.WriteString("hash", value.ToString("x16"));
		});
	}

	// ---- json helpers ------------------------------------------------------

	private static string Text(JsonElement root, string name, string fallback = null) =>
		root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
			? value.GetString()
			: fallback;

	private static int Int(JsonElement root, string name, int fallback) =>
		root.TryGetProperty(name, out JsonElement value) && value.TryGetInt32(out int parsed)
			? parsed
			: fallback;
}
