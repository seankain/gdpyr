using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Gdpyr.AgentClient;

namespace Gdpyr.Playtest;

/// <summary>What running one scenario came to.</summary>
public sealed class ScenarioResult
{
	public Scenario Scenario;
	public List<EpisodeMetrics> Episodes = new();
	public List<AssertionResult> Assertions = new();

	/// <summary>Set when the harness could not run the scenario at all. Exit code 2.</summary>
	public string Error;

	public double WallSeconds;

	public int Ticks;

	public bool Passed
	{
		get
		{
			if (Error != null)
			{
				return false;
			}

			for (int i = 0; i < Assertions.Count; i++)
			{
				if (!Assertions[i].Passed)
				{
					return false;
				}
			}

			return true;
		}
	}
}

/// <summary>
/// Runs a scenario against a headless gdpyr server and evaluates its claims
/// (docs/AGENT_API.md §9).
///
/// **It is synchronous.** <c>reset → step(n) → observe → assert</c>, with no
/// sleeps and no polling: stepped mode (§5.2) is what buys that, and wall-clock
/// waits in a test are how a suite becomes flaky.
///
/// **It fails with a trace.** Every episode writes <c>trace-&lt;seed&gt;.jsonl</c>
/// — every action, every event, and the <c>state_hash</c> once a second — so a
/// failed assertion names the tick it failed on and a file that replays it.
/// </summary>
public sealed class Runner
{
	/// <summary>Ticks between two <c>state_hash</c> probes written to the trace: one a second.</summary>
	private const int HashIntervalTicks = 60;

	private readonly GdpyrConnection _connection;
	private readonly string _traceDirectory;

	public Runner(GdpyrConnection connection, string traceDirectory)
	{
		_connection = connection;
		_traceDirectory = traceDirectory;
	}

	public async Task<ScenarioResult> RunAsync(Scenario scenario, CancellationToken cancel = default)
	{
		var result = new ScenarioResult { Scenario = scenario };
		long started = DateTime.UtcNow.Ticks;

		// A result obtained by cheating is not a result about this game
		// (docs/AGENT_API.md §6.4).
		if (_connection.Omniscient && scenario.ClaimsAWin)
		{
			result.Error = "this server is running with --agent-omniscient, and this scenario asserts a win."
				+ " A win obtained by dropping the fog is not a result about this game (§6.4)";
			return result;
		}

		try
		{
			await _connection.ConfigureAsync(stepped: true, cancel: cancel).ConfigureAwait(false);
			await AttachAsync(scenario, cancel).ConfigureAwait(false);

			foreach (int seed in scenario.Seeds)
			{
				result.Episodes.Add(await EpisodeAsync(scenario, seed, cancel).ConfigureAwait(false));
				result.Ticks += scenario.DurationTicks;
			}

			await DetachAsync(scenario, cancel).ConfigureAwait(false);
		}
		catch (Exception error)
		{
			result.Error = error.Message;
			return result;
		}

		foreach (Assertion assertion in scenario.Assertions)
		{
			result.Assertions.Add(AssertionEvaluator.Evaluate(assertion, result.Episodes));
		}

		result.WallSeconds = (DateTime.UtcNow.Ticks - started) / (double)TimeSpan.TicksPerSecond;
		return result;
	}

	// ---- seats -------------------------------------------------------------

	private async Task AttachAsync(Scenario scenario, CancellationToken cancel)
	{
		foreach (ScenarioSeat seat in scenario.Seats)
		{
			seat.PeerId = seat.Policy == "strategist"
				? await _connection.AttachStrategistAsync(seat.StepMul > 0 ? seat.StepMul : 30, cancel: cancel)
					.ConfigureAwait(false)
				: await _connection.AttachGroundAsync(seat.StepMul > 0 ? seat.StepMul : 4, cancel: cancel)
					.ConfigureAwait(false);
		}
	}

	private async Task DetachAsync(Scenario scenario, CancellationToken cancel)
	{
		foreach (ScenarioSeat seat in scenario.Seats)
		{
			if (seat.PeerId != 0)
			{
				await _connection.DetachAsync(seat.PeerId, cancel).ConfigureAwait(false);
			}
		}
	}

	// ---- one episode -------------------------------------------------------

	private async Task<EpisodeMetrics> EpisodeAsync(Scenario scenario, int seed, CancellationToken cancel)
	{
		var metrics = new EpisodeMetrics(seed)
		{
			Omniscient = _connection.Omniscient,
			Unbounded = _connection.Unbounded,
		};

		Directory.CreateDirectory(_traceDirectory);
		string tracePath = Path.Combine(_traceDirectory,
			$"trace-{Sanitize(scenario.Name)}-{seed}.jsonl");

		using var trace = new StreamWriter(tracePath, append: false, Encoding.UTF8);
		Trace(trace, 0, "scenario", writer =>
		{
			writer.WriteString("name", scenario.Name);
			writer.WriteNumber("seed", seed);
			writer.WriteNumber("duration_ticks", scenario.DurationTicks);

			// Both research flags are in the trace, always, so a recorded run can never
			// be read as an ordinary one (docs/AGENT_API.md §6.4, §7.3).
			writer.WriteBoolean("omniscient", _connection.Omniscient);
			writer.WriteBoolean("unbounded", _connection.Unbounded);
		});

		await _connection.ResetAsync(seed, cancel).ConfigureAwait(false);

		// The backfill settles before the clock starts, so the first seed does not
		// play alone while the rest play against a full roster.
		if (scenario.WarmupTicks > 0)
		{
			await StepAsync(scenario, 0, scenario.WarmupTicks, metrics, trace, cancel).ConfigureAwait(false);
		}

		await SpawnAsync(scenario, trace, cancel).ConfigureAwait(false);

		metrics.StartTick = _connection.Tick;
		await StepAsync(scenario, 0, scenario.DurationTicks, metrics, trace, cancel).ConfigureAwait(false);
		metrics.EndTick = _connection.Tick;

		// One more read: the last step's observations and events are still in flight
		// when its acknowledgement lands.
		await CollectAsync(scenario, metrics, trace, cancel).ConfigureAwait(false);

		int fallbacks = 0;
		foreach (GdpyrSeatStatus seat in _connection.LastStep)
		{
			fallbacks += seat.PilotFallbacks;
		}

		// A scenario that cares whether its own script actually drove the seat can say
		// so: a fallback is a tick a bot played instead (docs/AGENT_API.md §2.1).
		metrics.Counter("pilot_fallbacks", fallbacks);
		metrics.Counter("seeds", scenario.Seeds.Length);

		trace.Flush();
		return metrics;
	}

	/// <summary>
	/// Steps the simulation, submitting each scripted seat's action for every tick
	/// and reading back whatever the server pushed.
	///
	/// One tick at a time rather than a batch: the point of the harness is that a
	/// scenario can say what happened on which tick, and a step of 600 would make
	/// every metric's tick the last one.
	/// </summary>
	private async Task StepAsync(Scenario scenario, int from, int ticks, EpisodeMetrics metrics,
		StreamWriter trace, CancellationToken cancel)
	{
		for (int i = 0; i < ticks; i++)
		{
			int elapsed = from + i;
			foreach (ScenarioSeat seat in scenario.Seats)
			{
				Act(seat, elapsed, trace);
			}

			bool truncated = await _connection.StepAsync(1, cancel).ConfigureAwait(false);
			await CollectAsync(scenario, metrics, trace, cancel).ConfigureAwait(false);

			if (truncated)
			{
				throw new InvalidOperationException(
					$"the server truncated the episode at tick {_connection.Tick}: it stopped stepping");
			}

			if (elapsed % HashIntervalTicks == 0)
			{
				string hash = await _connection.StateHashAsync(cancel).ConfigureAwait(false);
				Trace(trace, _connection.Tick, "state_hash", writer => writer.WriteString("hash", hash));
			}
		}
	}

	private void Act(ScenarioSeat seat, int elapsed, StreamWriter trace)
	{
		if (seat.PeerId == 0 || seat.Script == null || !seat.Script.TryStep(elapsed, out ScriptStep step))
		{
			return;
		}

		if (seat.Policy == "strategist")
		{
			// A scripted strategist has nothing to say yet: scripts are an FPS
			// affordance, and a scenario that wants a commanding strategist attaches a
			// policy rather than a script.
			return;
		}

		var action = new AgentGroundAction
		{
			MoveX = step.MoveX,
			MoveZ = step.MoveZ,
			DeltaYaw = step.DeltaYaw,
			DeltaPitch = step.DeltaPitch,
			Buttons = Buttons(step.Buttons),
		};

		_connection.Act(seat.PeerId, action);

		Trace(trace, _connection.Tick, "action", writer =>
		{
			writer.WriteNumber("seat", seat.PeerId);
			writer.WriteStartArray("move");
			writer.WriteNumberValue(step.MoveX);
			writer.WriteNumberValue(step.MoveZ);
			writer.WriteEndArray();
			writer.WriteNumber("dyaw", step.DeltaYaw);
			writer.WriteNumber("dpitch", step.DeltaPitch);
			writer.WriteNumber("buttons", (ushort)action.Buttons);
		});
	}

	/// <summary>Drains what the step produced into the metrics and the trace.</summary>
	private async Task CollectAsync(Scenario scenario, EpisodeMetrics metrics, StreamWriter trace,
		CancellationToken cancel)
	{
		// The server writes the step's acknowledgement first and then one observation
		// per seat, so the frames this step produced are usually still on the socket
		// when the acknowledgement lands. Anything not yet arrived is picked up by the
		// next step's read loop, one tick later, which no metric minds.
		await _connection.PollAsync(cancel).ConfigureAwait(false);

		foreach (GdpyrEvent record in _connection.DrainEvents())
		{
			metrics.Event(record.Kind, record.Tick, record.Fields);
			Trace(trace, record.Tick, "event", writer =>
			{
				// Not "kind": Trace already wrote that for the trace line itself, and a
				// duplicate key is a JSON document a strict reader throws on rather than
				// one it merges (AgentEventSchema says the same thing about the wire).
				writer.WriteString("event", record.Kind);
				foreach (KeyValuePair<string, double> field in record.Fields)
				{
					writer.WriteNumber(field.Key, field.Value);
				}
			});
		}

		while (_connection.TryDequeueObservation(out GdpyrObservation observation))
		{
			Sample(scenario, metrics, observation);
		}
	}

	/// <summary>
	/// Records every field of an observation under its published name, so an
	/// assertion reads <c>observation.self.health</c> rather than offset 37 of a
	/// float array (docs/AGENT_API.md §9.2).
	///
	/// The bare name is the scenario's first seat; every seat is also recorded under
	/// its scenario id, so a two-seat scenario can say which one it means.
	/// </summary>
	private void Sample(Scenario scenario, EpisodeMetrics metrics, GdpyrObservation observation)
	{
		string id = null;
		bool primary = false;

		for (int i = 0; i < scenario.Seats.Count; i++)
		{
			if (scenario.Seats[i].PeerId != observation.Seat)
			{
				continue;
			}

			id = scenario.Seats[i].Id;
			primary = i == 0;
			break;
		}

		IReadOnlyCollection<string> names = observation.Policy == GdpyrPolicy.Strategist
			? _connection.Schema.StrategistNames
			: _connection.Schema.Names;

		foreach (string name in names)
		{
			ReadOnlySpan<float> run = observation.Field(_connection.Schema, name);
			for (int i = 0; i < run.Length; i++)
			{
				// A field that is one float is named plainly; a run is named plainly for
				// its first element and by index for the rest, so a claim about the
				// nearest contact's distance reads observation.contacts.6 rather than an
				// offset into an array a scenario author has to count out.
				string element = i == 0 ? name : $"{name}.{i}";

				if (primary)
				{
					metrics.Sample(element, observation.Tick, run[i]);
				}

				if (id != null)
				{
					metrics.Sample($"{id}.{element}", observation.Tick, run[i]);
				}
			}
		}
	}

	// ---- scripted spawns ---------------------------------------------------

	private async Task SpawnAsync(Scenario scenario, StreamWriter trace, CancellationToken cancel)
	{
		foreach (ScenarioSpawn spawn in scenario.Spawns)
		{
			if (spawn.Unit != null)
			{
				int unit = await _connection.SpawnUnitAsync(spawn.Unit, spawn.Team, spawn.At, spawn.Order,
					cancel).ConfigureAwait(false);

				Trace(trace, _connection.Tick, "spawn", writer =>
				{
					writer.WriteString("unit", spawn.Unit);
					writer.WriteString("team", spawn.Team);
					writer.WriteNumber("id", unit);
					WriteVector(writer, "at", spawn.At);
				});
				continue;
			}

			ScenarioSeat seat = Seat(scenario, spawn.Peer);
			if (seat == null)
			{
				throw new ScenarioException(
					$"'{scenario.Name}': a spawn places '{spawn.Peer}', and no seat has that id");
			}

			float yaw = spawn.Look is { Length: > 0 } ? spawn.Look[0] : 0f;
			float pitch = spawn.Look is { Length: > 1 } ? spawn.Look[1] : 0f;
			await _connection.SpawnSeatAsync(seat.PeerId, spawn.At, yaw, pitch, cancel).ConfigureAwait(false);

			Trace(trace, _connection.Tick, "spawn", writer =>
			{
				writer.WriteString("peer", seat.Id);
				writer.WriteNumber("seat", seat.PeerId);
				WriteVector(writer, "at", spawn.At);
				writer.WriteNumber("yaw", yaw);
				writer.WriteNumber("pitch", pitch);
			});
		}
	}

	private static void WriteVector(Utf8JsonWriter writer, string name, float[] values)
	{
		writer.WriteStartArray(name);
		for (int i = 0; values != null && i < values.Length; i++)
		{
			writer.WriteNumberValue(values[i]);
		}

		writer.WriteEndArray();
	}

	private static ScenarioSeat Seat(Scenario scenario, string id)
	{
		for (int i = 0; i < scenario.Seats.Count; i++)
		{
			if (scenario.Seats[i].Id == id)
			{
				return scenario.Seats[i];
			}
		}

		return null;
	}

	// ---- plumbing ----------------------------------------------------------

	private static AgentButtons Buttons(string[] names)
	{
		AgentButtons held = AgentButtons.None;
		for (int i = 0; names != null && i < names.Length; i++)
		{
			held |= names[i] switch
			{
				"jump" => AgentButtons.Jump,
				"crouch" => AgentButtons.Crouch,
				"sprint" => AgentButtons.Sprint,
				"fire" => AgentButtons.Fire,
				"ads" => AgentButtons.Ads,
				"reload" => AgentButtons.Reload,
				"use" => AgentButtons.Use,
				"melee" => AgentButtons.Melee,
				"weapon1" => AgentButtons.Weapon1,
				"weapon2" => AgentButtons.Weapon2,
				"weapon3" => AgentButtons.Weapon3,
				_ => AgentButtons.None,
			};
		}

		return held;
	}

	private static void Trace(StreamWriter trace, uint tick, string kind, Action<Utf8JsonWriter> write)
	{
		var buffer = new System.Buffers.ArrayBufferWriter<byte>(256);
		using (var writer = new Utf8JsonWriter(buffer))
		{
			writer.WriteStartObject();
			writer.WriteNumber("t", tick);
			writer.WriteString("kind", kind);
			write(writer);
			writer.WriteEndObject();
		}

		trace.WriteLine(Encoding.UTF8.GetString(buffer.WrittenSpan));
	}

	private static string Sanitize(string name)
	{
		var clean = new StringBuilder(name.Length);
		foreach (char character in name)
		{
			clean.Append(char.IsLetterOrDigit(character) ? char.ToLowerInvariant(character) : '-');
		}

		return clean.ToString();
	}

	/// <summary>Formats a number the way an assertion line prints one.</summary>
	public static string Format(double value) =>
		value.ToString("0.###", CultureInfo.InvariantCulture);
}
