using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
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
				case "list_units": ListUnits(session, correlation, root); break;
				case "attach": Attach(session, correlation, root, tick); break;
				case "detach": Detach(session, correlation, root); break;
				case "act": Act(session, correlation, root, tick); break;
				case "observe": Observe(session, correlation, root, tick); break;
				case "step": Step(session, correlation, root, tick); break;
				case "reset": Reset(session, correlation, root, tick); break;
				case "spawn": Spawn(session, correlation, root, tick); break;
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

		if (root.TryGetProperty("feature_planes", out JsonElement planes)
			&& planes.ValueKind is JsonValueKind.True or JsonValueKind.False)
		{
			session.FeaturePlanes = planes.GetBoolean();
		}

		session.SendJson(AgentFrameKind.Response, correlation, writer =>
		{
			writer.WriteString("op", "config");
			writer.WriteString("mode", session.Stepped ? "stepped" : "realtime");
			writer.WriteNumber("step_timeout_ms", session.StepTimeoutMilliseconds);
			writer.WriteString("observations", session.BinaryObservations ? "binary" : "json");
			writer.WriteBoolean("feature_planes", session.FeaturePlanes);
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

	/// <summary>
	/// The units a strategist seat commands, in the order its observation carries
	/// them (docs/AGENT_API.md §7.5).
	///
	/// The strategist vector carries a unit's tier, position, health and order and
	/// no id (§6.2) — an id is not a number a policy has any use for as an input —
	/// while an <c>order</c> command names ids (§7.4). This is the one call that
	/// joins the two: slot <em>i</em> here is slot <em>i</em> of the observation's
	/// unit block, because both walk <c>UnitManager</c> in registry order and
	/// filter on the same team.
	///
	/// It is bounded by ownership rather than by sight, which costs the fog
	/// nothing: every unit on the field belongs to the strategist, so this is a
	/// seat reading its own army — the thing a human strategist's client draws
	/// without asking. A ground seat is refused.
	/// </summary>
	private void ListUnits(AgentSession session, uint correlation, JsonElement root)
	{
		int peerId = Int(root, "seat", 0);
		if (!_book.TryGet(peerId, out AgentSeat seat) || seat.SessionId != session.Id)
		{
			session.SendError(correlation, "not_attached", $"this session does not hold seat {peerId}");
			return;
		}

		if (seat.Kind != AgentPolicyKind.Strategist)
		{
			session.SendError(correlation, "wrong_policy",
				$"seat {peerId} is a ground seat; it commands no units");
			return;
		}

		CombatManager combat = CombatManager.Instance;
		PlayerCombat player = combat?.Find(peerId);
		UnitManager units = UnitManager.Instance;
		if (player == null || units == null)
		{
			session.SendError(correlation, "no_round", "this process is not running a round");
			return;
		}

		Team team = player.Team;
		session.SendJson(AgentFrameKind.Response, correlation, writer =>
		{
			writer.WriteString("op", "units");
			writer.WriteNumber("seat", peerId);
			writer.WriteStartArray("units");
			for (int i = 0; i < units.SlotCount; i++)
			{
				Unit unit = units.UnitAt(i);
				if (unit == null || unit.Team != team)
				{
					continue;
				}

				// A corpse is carried until the reaper takes it, flagged dead, for the
				// reason the observation carries one: a policy that saw a unit vanish a
				// tick before its unit_lost would have to infer the loss from a hole.
				float maxHealth = unit.Definition?.MaxHealth ?? 100f;
				writer.WriteStartObject();
				writer.WriteNumber("id", (int)unit.UnitId);
				writer.WriteNumber("tier", (int)unit.DefinitionId);
				writer.WriteBoolean("alive", unit.IsAlive);
				writer.WriteNumber("x", unit.GlobalPosition.X);
				writer.WriteNumber("z", unit.GlobalPosition.Z);
				writer.WriteNumber("health", maxHealth > 0f ? unit.Health / maxHealth : 0f);
				writer.WriteNumber("order", (int)unit.Order.Kind);
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

		// A strategist policy sits in a strategist's chair. The team is implied by
		// the policy kind rather than asked for twice, because the two cannot
		// disagree: there is no strategist seat on the ground force.
		Team team = policy == AgentPolicyKind.Strategist || Text(root, "team", "ground") == "strategist"
			? Team.Strategist
			: Team.GroundForce;
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

		if (action.TryGetProperty("commands", out JsonElement commands))
		{
			ActCommands(session, correlation, peerId, actionTick, commands);
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

	/// <summary>
	/// A strategist seat's decision: a list of commands applied in order, rate
	/// limited by the APM cap when the tick runs them (docs/AGENT_API.md §7.4).
	///
	/// Parsing is total. A command the schema does not name, a barracks index that
	/// does not exist, a unit the seat does not own — none of them is an error here:
	/// the first is skipped, and the other two are refused by the same
	/// <c>ApplyOrder</c> / <c>ApplyBuild</c> a client's RPC lands in, and counted in
	/// <c>UnitManager.RejectedOrders</c>. A policy emitting garbage shows up on the
	/// debug HUD rather than disconnecting itself.
	/// </summary>
	private void ActCommands(AgentSession session, uint correlation, int peerId, uint actionTick,
		JsonElement commands)
	{
		if (!_book.TryGet(peerId, out AgentSeat seat) || seat.SessionId != session.Id)
		{
			session.SendError(correlation, "not_attached", $"this session does not hold seat {peerId}");
			return;
		}

		if (seat.Kind != AgentPolicyKind.Strategist || seat.Commands == null)
		{
			session.SendError(correlation, "wrong_policy",
				$"seat {peerId} is a ground seat; send an InputFrame action rather than commands");
			return;
		}

		if (commands.ValueKind != JsonValueKind.Array)
		{
			session.SendError(correlation, "bad_request", "'commands' is an array");
			return;
		}

		AgentCommandList list = seat.Commands;
		list.Clear();

		foreach (JsonElement element in commands.EnumerateArray())
		{
			if (element.ValueKind != JsonValueKind.Object)
			{
				continue;
			}

			ParseCommand(list, element);
		}

		seat.SubmitCommands(actionTick);
	}

	private void ParseCommand(AgentCommandList list, JsonElement element)
	{
		string kind = Text(element, "cmd", "noop");
		Point(element, "target", out float x, out float z);

		var command = new AgentCommand
		{
			Barracks = Int(element, "barracks", 0),
			Tier = (byte)Math.Clamp(Int(element, "tier", 0), 0, byte.MaxValue),
			TargetOwnerId = Int(element, "target_owner", OwnerId.None),
			X = x,
			Z = z,
		};

		switch (kind)
		{
			case "order":
				command.Order = (byte)OrderFromName(Text(element, "kind", "move"));
				list.AddOrder(command, UnitIds(element));
				break;

			case "build":
				command.Kind = AgentCommandKind.Build;
				list.Add(command);
				break;

			case "cancel":
				command.Kind = AgentCommandKind.Cancel;
				list.Add(command);
				break;

			case "rally":
				command.Kind = AgentCommandKind.Rally;
				list.Add(command);
				break;

			case "construct":
				// The builders to send, the structure by name or index, where, and
				// which way it faces (docs/NETCODE.md §10.5).
				command.Kind = AgentCommandKind.Construct;
				command.Structure = StructureKind(Text(element, "structure", "pillbox"));
				command.Yaw = element.TryGetProperty("yaw", out JsonElement yaw) && yaw.TryGetDouble(out double radians)
					? (float)radians
					: 0f;
				list.AddWithUnits(command, UnitIds(element));
				break;

			case "noop":
				command.Kind = AgentCommandKind.Noop;
				list.Add(command);
				break;

			default:
				// Unknown names are ignored rather than rejected, the same way an
				// unknown button name is: a policy built against a newer schema must
				// degrade, not disconnect (AgentActionCodec.ButtonFromName).
				break;
		}
	}

	/// <summary>The unit ids an order names, read into the scratch buffer the seat's list copies from.</summary>
	private ReadOnlySpan<int> UnitIds(JsonElement element)
	{
		if (!element.TryGetProperty("units", out JsonElement units) || units.ValueKind != JsonValueKind.Array)
		{
			return ReadOnlySpan<int>.Empty;
		}

		int count = 0;
		foreach (JsonElement id in units.EnumerateArray())
		{
			if (count >= _unitIdScratch.Length)
			{
				break;
			}

			if (id.TryGetInt32(out int value))
			{
				_unitIdScratch[count++] = value;
			}
		}

		return _unitIdScratch.AsSpan(0, count);
	}

	/// <summary>
	/// Order names as the control plane spells them. <c>stop</c> is a command and
	/// never a stored state, which <see cref="UnitOrder"/> already knows.
	/// </summary>
	private static OrderKind OrderFromName(string name) => name switch
	{
		"move" => OrderKind.Move,
		"attack" => OrderKind.Attack,
		"patrol" => OrderKind.Patrol,
		"defend" => OrderKind.Defend,
		"stop" => OrderKind.Stop,
		_ => OrderKind.Move,
	};

	/// <summary>An <c>[x, z]</c> or <c>[x, y, z]</c> array: the ground plane, however the caller wrote it.</summary>
	private static void Point(JsonElement root, string name, out float x, out float z)
	{
		x = 0f;
		z = 0f;

		if (!root.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.Array)
		{
			return;
		}

		int length = value.GetArrayLength();
		if (length >= 3)
		{
			x = (float)value[0].GetDouble();
			z = (float)value[2].GetDouble();
		}
		else if (length == 2)
		{
			x = (float)value[0].GetDouble();
			z = (float)value[1].GetDouble();
		}
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

	/// <summary>
	/// The buffer this seat's observation is written into: one per observation
	/// space, and the space is the seat's policy kind (docs/AGENT_API.md §6).
	/// </summary>
	private float[] BufferFor(AgentSeat seat) =>
		seat.Kind == AgentPolicyKind.Strategist ? _strategistObservation : _groundObservation;

	private bool Encode(AgentSeat seat, uint tick) =>
		seat.Kind == AgentPolicyKind.Strategist
			? _strategistView.Encode(seat.PeerId, tick, seat.StepMul, Limits, _strategistObservation)
			: _groundView.Encode(seat.PeerId, tick, seat.StepMul, Limits, _groundObservation);

	/// <summary>
	/// Whether this frame carries the feature planes: the session asked for them and
	/// the seat is one they mean anything for (docs/AGENT_API.md §6.3).
	/// </summary>
	private bool PlanesFor(AgentSession session, AgentSeat seat) =>
		session.FeaturePlanes && seat.Kind == AgentPolicyKind.Strategist;

	private void SendObservation(AgentSession session, uint correlation, AgentSeat seat, uint tick,
		AgentFrameKind kind)
	{
		float[] values = BufferFor(seat);
		int floats = seat.Kind == AgentPolicyKind.Strategist
			? AgentStrategistObservation.Floats
			: AgentObservation.Floats;

		bool planes = PlanesFor(session, seat) && _planes.Fill(seat.PeerId, tick, Limits);

		if (session.BinaryObservations)
		{
			// seat (i32) · flags (u16) · reserved (u16) · tick (u32) · floats, so a
			// decoder never has to pair an observation with a separate envelope to know
			// whose it is. The seat is a peer id and peer ids are ints, so the field is
			// as wide as one (see AgentActionCodec.GroundSizeBytes).
			//
			// Bit 2 of the flags says the planes follow the vector in the same frame,
			// so a decoder that asked for them and a server that could not build them
			// cannot disagree about where the floats end.
			int header = 12;
			int total = floats + (planes ? AgentFeaturePlanes.Floats : 0);
			int size = header + (total * sizeof(float));
			if (_observationBytes.Length < size)
			{
				_observationBytes = new byte[size];
			}

			Span<byte> frame = _observationBytes.AsSpan(0, size);
			BinaryPrimitives.WriteInt32LittleEndian(frame, seat.PeerId);
			BinaryPrimitives.WriteUInt16LittleEndian(frame[4..], (ushort)((Limits.Omniscient ? 1 : 0)
				| (Limits.Unbounded ? 2 : 0) | (planes ? 4 : 0)));
			BinaryPrimitives.WriteUInt16LittleEndian(frame[6..], 0);
			BinaryPrimitives.WriteUInt32LittleEndian(frame[8..], tick);

			for (int i = 0; i < floats; i++)
			{
				BinaryPrimitives.WriteSingleLittleEndian(frame[(header + (i * 4))..], values[i]);
			}

			if (planes)
			{
				ReadOnlySpan<float> grid = _planes.Grid;
				for (int i = 0; i < AgentFeaturePlanes.Floats; i++)
				{
					BinaryPrimitives.WriteSingleLittleEndian(
						frame[(header + ((floats + i) * 4))..], grid[i]);
				}
			}

			session.Send(kind == AgentFrameKind.Response ? AgentFrameKind.Observation : kind, correlation, frame);
			return;
		}

		session.SendJson(kind, correlation, writer =>
		{
			writer.WriteString("op", "observation");
			writer.WriteNumber("seat", seat.PeerId);
			writer.WriteString("policy", seat.Kind == AgentPolicyKind.Strategist ? "strategist" : "ground");
			writer.WriteNumber("tick", tick);
			writer.WriteBoolean("omniscient", Limits.Omniscient);
			writer.WriteBoolean("unbounded", Limits.Unbounded);

			if (seat.Kind == AgentPolicyKind.Strategist)
			{
				AgentJson.WriteObservation(writer, AgentStrategistObservation.Fields, values);
			}
			else
			{
				AgentJson.WriteObservation(writer, AgentObservation.Fields, values);
			}

			if (planes)
			{
				AgentJson.WritePlanes(writer, _planes.Grid);
			}
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

		// The passability plane is the map's and is probed once; a reset is the one
		// moment the map could have changed under it (docs/AGENT_API.md §6.3).
		_planes.Invalidate();

		session.SendJson(AgentFrameKind.Response, correlation, writer =>
		{
			writer.WriteString("op", "reset");
			writer.WriteNumber("tick", tick);
			writer.WriteNumber("seed", combat.RoundSeed);
		});
	}

	/// <summary>
	/// Puts a character or a unit where a scenario asked for one
	/// (docs/AGENT_API.md §9.1).
	///
	/// This exists for the playtest harness and for nothing else: "a rifleman at
	/// 100 m hits a stationary target ≥ 60% of 50 shots" is not a claim anybody can
	/// make by waiting for a round to produce that situation. It is refused unless
	/// the caller holds the seat it is moving, and a unit spawn is charged to
	/// nobody — a scenario is a laboratory, not a round.
	/// </summary>
	private void Spawn(AgentSession session, uint correlation, JsonElement root, uint tick)
	{
		Point(root, "at", out float x, out float z);
		float y = root.TryGetProperty("at", out JsonElement at) && at.ValueKind == JsonValueKind.Array
			&& at.GetArrayLength() >= 3
			? (float)at[1].GetDouble()
			: 0f;

		var where = new Vector3(x, y, z);

		if (root.TryGetProperty("structure", out JsonElement _))
		{
			SpawnStructure(session, correlation, root, where);
			return;
		}

		if (root.TryGetProperty("unit", out JsonElement _))
		{
			UnitManager units = UnitManager.Instance;
			if (units == null)
			{
				session.SendError(correlation, "no_round", "this process has no unit manager");
				return;
			}

			byte tier = UnitTier(Text(root, "unit", "infantry"));
			Team team = Text(root, "team", "strategist") == "ground" ? Team.GroundForce : Team.Strategist;

			// "none" is a scenario's way of saying "stand there", which is the whole
			// point of a scripted spawn; OrderFromName's fallback is move, which is not.
			string ordered = Text(root, "order", "none");
			OrderKind order = ordered == "none" ? OrderKind.None : OrderFromName(ordered);

			ushort unitId = units.ServerPlaceUnit(tier, where, team, order);
			if (unitId == 0)
			{
				session.SendError(correlation, "unit_pool_full",
					$"no room on the field for another unit ({SimConfig.MaxUnits})");
				return;
			}

			session.SendJson(AgentFrameKind.Response, correlation, writer =>
			{
				writer.WriteString("op", "spawned");
				writer.WriteNumber("unit", unitId);
				writer.WriteNumber("tier", tier);
				writer.WriteString("team", team == Team.Strategist ? "strategist" : "ground");
			});
			return;
		}

		int peerId = Int(root, "seat", 0);
		if (!_book.TryGet(peerId, out AgentSeat seat) || seat.SessionId != session.Id)
		{
			session.SendError(correlation, "not_attached", $"this session does not hold seat {peerId}");
			return;
		}

		fps_controller character = PlayerManager.Instance?.CharacterOf(seat.PeerId);
		if (character == null)
		{
			session.SendError(correlation, "no_character", $"seat {peerId} has no character this tick");
			return;
		}

		Point(root, "look", out float yaw, out float pitch);

		// Teleport clears the velocity and the visual offset, which is what a
		// placement is; the second call is only there because the look angles are set
		// through the same public entry point a remote character's are.
		character.Teleport(new Transform3D(Basis.Identity, where));
		character.ApplyRemoteTransform(where, yaw, pitch);

		session.SendJson(AgentFrameKind.Response, correlation, writer =>
		{
			writer.WriteString("op", "spawned");
			writer.WriteNumber("seat", seat.PeerId);
			writer.WriteNumber("tick", tick);
		});
	}

	/// <summary>A unit tier by name, or by catalog index when a scenario wrote a number.</summary>
	private static byte UnitTier(string name) => name switch
	{
		"infantry" or "rifleman" or "0" => UnitCatalog.Infantry,
		"technical" or "1" => UnitCatalog.Technical,
		"tank" or "2" => UnitCatalog.Tank,
		"builder" or "3" => UnitCatalog.Builder,
		_ => UnitCatalog.Infantry,
	};

	/// <summary>
	/// A structure by the name the schema publishes, or by catalog index; 255 for
	/// anything else, which the server refuses rather than guessing at.
	/// </summary>
	private static byte StructureKind(string name)
	{
		for (int i = 0; i < AgentJson.StructureNames.Length; i++)
		{
			if (name == AgentJson.StructureNames[i] || name == i.ToString(System.Globalization.CultureInfo.InvariantCulture))
			{
				return (byte)i;
			}
		}

		return byte.MaxValue;
	}

	/// <summary>
	/// Puts a finished structure on the map for a scenario, free and without a
	/// builder (docs/AGENT_API.md §9.1, docs/NETCODE.md §10.5). The structure half
	/// of the unit spawn above, and as narrow.
	/// </summary>
	private void SpawnStructure(AgentSession session, uint correlation, JsonElement root, Vector3 where)
	{
		UnitManager units = UnitManager.Instance;
		if (units == null)
		{
			session.SendError(correlation, "no_round", "this process has no unit manager");
			return;
		}

		string name = Text(root, "structure", "pillbox");
		byte kind = StructureKind(name);
		Team team = Text(root, "team", "strategist") == "ground" ? Team.GroundForce : Team.Strategist;
		float yaw = root.TryGetProperty("yaw", out JsonElement value) && value.TryGetDouble(out double read)
			? (float)read
			: 0f;

		int slot = units.ServerPlaceStructure(kind, where, yaw, team, out PlacementResult result);
		if (slot < 0)
		{
			session.SendError(correlation, "structure_refused",
				$"no {name} at ({where.X:0.#}, {where.Z:0.#}): {StructurePlacement.Describe(result)}");
			return;
		}

		session.SendJson(AgentFrameKind.Response, correlation, writer =>
		{
			writer.WriteString("op", "spawned");
			writer.WriteNumber("structure", slot);
			writer.WriteNumber("owner", OwnerId.ForStructure(slot));
			writer.WriteString("kind", AgentJson.StructureNames[kind]);
			writer.WriteString("team", team == Team.Strategist ? "strategist" : "ground");
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
