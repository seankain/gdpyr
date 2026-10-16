using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Gdpyr.Playtest;

/// <summary>A scenario file that could not be read, with a sentence saying why.</summary>
public sealed class ScenarioException : Exception
{
	public ScenarioException(string message) : base(message)
	{
	}
}

/// <summary>
/// One step of a scripted seat: what to do, and for how many ticks. A ground seat's
/// step is input — a move, a look, buttons — held for its ticks; a strategist seat's is
/// <see cref="Commands"/>, sent once, on the step's first tick (docs/VEHICLES.md §11).
/// </summary>
public struct ScriptStep
{
	public int Ticks;
	public float MoveX;
	public float MoveZ;

	/// <summary>Look deltas, in radians per decision. Clamped server-side to the turn-rate ceiling.</summary>
	public float DeltaYaw;

	public float DeltaPitch;

	/// <summary>Button names, as §7.2 spells them.</summary>
	public string[] Buttons;

	/// <summary>True when the step said anything about a move, a look or buttons: a ground seat's input.</summary>
	public bool HasInput;

	/// <summary>A strategist seat's commands, naming units by spawn label. Null for a ground seat's step.</summary>
	public ScriptCommand[] Commands;
}

/// <summary>
/// One command a scripted strategist seat sends (docs/VEHICLES.md §11): an
/// <c>embark</c>, an <c>unload</c> or an <c>order</c>, naming units by the labels
/// their spawns gave them — a scenario cannot know the ids the server will hand out.
/// </summary>
public sealed class ScriptCommand
{
	/// <summary>The names the step type knows.</summary>
	public static readonly string[] Names = { "embark", "unload", "order" };

	/// <summary>The order kinds an <c>order</c> may name, as the control plane spells them.</summary>
	public static readonly string[] OrderKinds = { "move", "attack", "patrol", "defend", "stop" };

	public string Cmd = "order";

	/// <summary>For an order: one of <see cref="OrderKinds"/>.</summary>
	public string Kind = "move";

	/// <summary>Spawn labels: the units to embark or to order, or the vehicles to unload.</summary>
	public string[] Units = Array.Empty<string>();

	/// <summary>For an embark: the spawn label of the vehicle to get into.</summary>
	public string Vehicle;

	/// <summary>For an order: where, as <c>[x, z]</c>.</summary>
	public float[] Target = Array.Empty<float>();
}

/// <summary>
/// A scripted seat's program (docs/AGENT_API.md §9.1).
///
/// A list of steps walked in order and, by default, repeated — so
/// "hold still and fire" is two lines rather than twelve hundred. A script is
/// deliberately not a policy: it exists so that a scenario can put the game in a
/// known situation, and the interesting assertions are about what the *game* did
/// with it.
/// </summary>
public sealed class ScenarioScript
{
	public ScenarioScript(IReadOnlyList<ScriptStep> steps, bool repeat)
	{
		Steps = steps ?? Array.Empty<ScriptStep>();
		Repeat = repeat;

		int total = 0;
		for (int i = 0; i < Steps.Count; i++)
		{
			total += Math.Max(Steps[i].Ticks, 1);
		}

		TotalTicks = total;
	}

	public IReadOnlyList<ScriptStep> Steps { get; }

	public bool Repeat { get; }

	/// <summary>Ticks one pass through the script takes.</summary>
	public int TotalTicks { get; }

	/// <summary>
	/// The step that starts on <paramref name="elapsed"/>, if one does: what a strategist
	/// seat's commands are sent on, once each — on every pass for a script that repeats,
	/// and never again after the last step for one that does not.
	/// </summary>
	public bool TryEnter(int elapsed, out ScriptStep step)
	{
		step = default;
		if (Steps.Count == 0 || TotalTicks <= 0 || elapsed < 0 || (!Repeat && elapsed >= TotalTicks))
		{
			return false;
		}

		elapsed %= TotalTicks;
		int at = 0;
		for (int i = 0; i < Steps.Count && at <= elapsed; i++)
		{
			if (at == elapsed)
			{
				step = Steps[i];
				return true;
			}

			at += Math.Max(Steps[i].Ticks, 1);
		}

		return false;
	}

	/// <summary>The step in force on <paramref name="elapsed"/> ticks since the episode started.</summary>
	public bool TryStep(int elapsed, out ScriptStep step)
	{
		step = default;
		if (Steps.Count == 0 || TotalTicks <= 0)
		{
			return false;
		}

		if (elapsed >= TotalTicks)
		{
			if (!Repeat)
			{
				// A script that has run out holds still rather than repeating: a policy
				// that stops deciding falls back to its bot, and a scenario that meant
				// that should say so with a step.
				step = new ScriptStep { Ticks = 1 };
				return true;
			}

			elapsed %= TotalTicks;
		}

		int at = 0;
		for (int i = 0; i < Steps.Count; i++)
		{
			at += Math.Max(Steps[i].Ticks, 1);
			if (elapsed < at)
			{
				step = Steps[i];
				return true;
			}
		}

		step = Steps[^1];
		return true;
	}
}

/// <summary>One seat a scenario attaches.</summary>
public sealed class ScenarioSeat
{
	/// <summary>The name spawns refer to this seat by. Defaults to <c>agent0</c>, <c>agent1</c>, …</summary>
	public string Id = string.Empty;

	public string Team = "ground";

	public string Policy = "ground";

	public int StepMul;

	public ScenarioScript Script;

	/// <summary>Filled in by the runner once the server has said which peer id this is.</summary>
	public int PeerId;
}

/// <summary>Something a scenario puts on the map before the clock starts.</summary>
public sealed class ScenarioSpawn
{
	/// <summary>The seat to place, by its scenario id. Null for a unit spawn.</summary>
	public string Peer;

	/// <summary>The unit tier to place. Null for a seat placement.</summary>
	public string Unit;

	/// <summary>
	/// A structure to place, finished and free — <c>pillbox</c>, <c>sandbag_wall</c>,
	/// <c>sniper_tower</c> (docs/NETCODE.md §10.5). Null for anything else.
	/// </summary>
	public string Structure;

	/// <summary>Which way a structure faces [rad]: its front, and for a wall its broad side, towards +Z at 0.</summary>
	public float Yaw;

	public string Team = "strategist";

	public string Order = "none";

	public float[] At = { 0f, 0f, 0f };

	public float[] Look = { 0f, 0f };

	/// <summary>
	/// A unit spawn's label (docs/VEHICLES.md §11): what a later spawn's <see cref="Aboard"/>
	/// and a strategist script's commands name it by. Null for none.
	/// </summary>
	public string Id;

	/// <summary>
	/// The label of an earlier unit spawn — a vehicle — this unit starts aboard. Placed
	/// where the vehicle is, and seated by the server directly; null to stand at
	/// <see cref="At"/>.
	/// </summary>
	public string Aboard;

	/// <summary>
	/// For a spawn <see cref="Aboard"/>: the strategist seat that loaded it, by its
	/// scenario id, which the vehicle's cargo then answers to. Null for the scenario's
	/// only strategist seat.
	/// </summary>
	public string Seat;
}

/// <summary>
/// A scenario: the seed sweep, the roster, what to put on the map, what to script,
/// and what to claim about the result (docs/AGENT_API.md §9.1).
///
/// It is data. Authoring one needs no Godot install and no C#; only running one
/// needs the headless server. Parsing is total — every malformed field is a
/// <see cref="ScenarioException"/> with a sentence, because exit code 2 is
/// supposed to mean "the harness could not run this", and a stack trace is not an
/// explanation.
/// </summary>
public sealed class Scenario
{
	/// <summary>Ticks of round to run when a scenario does not say. Twenty seconds.</summary>
	public const int DefaultDurationTicks = 1200;

	public string Name = "scenario";

	/// <summary>Where the file was read from, so a script path can be resolved beside it.</summary>
	public string Path = string.Empty;

	/// <summary>The seeds to run. One episode each (docs/AGENT_API.md §3.1: a seed labels an episode).</summary>
	public int[] Seeds = { 0 };

	public int DurationTicks = DefaultDurationTicks;

	/// <summary>
	/// Ticks to run before the episode's metrics start counting: enough for the
	/// backfill to settle, so the first seed does not play alone while the rest
	/// play against a full roster.
	/// </summary>
	public int WarmupTicks = 60;

	public int GroundBots = 1;

	public int StrategistBots;

	/// <summary>
	/// What the server's bots should decide with, <c>legacy</c> or <c>htn</c>
	/// (docs/HTN_BOTS.md §4.2 rule 6); null for the server's default. A scenario
	/// that makes a claim about one bot AI names it; <c>--bot-ai</c> on the command
	/// line overrides it, which is how the same claim is checked against the other.
	/// </summary>
	public string BotAi;

	public List<ScenarioSeat> Seats = new();

	public List<ScenarioSpawn> Spawns = new();

	public List<Assertion> Assertions = new();

	/// <summary>True when any claim is about who won (docs/AGENT_API.md §6.4).</summary>
	public bool ClaimsAWin
	{
		get
		{
			for (int i = 0; i < Assertions.Count; i++)
			{
				if (Assertions[i].IsWinClaim)
				{
					return true;
				}
			}

			return false;
		}
	}

	public static Scenario Load(string path)
	{
		string text;
		try
		{
			text = System.IO.File.ReadAllText(path);
		}
		catch (Exception error)
		{
			throw new ScenarioException($"cannot read '{path}': {error.Message}");
		}

		Scenario scenario = Parse(text, path);
		scenario.Path = path;
		return scenario;
	}

	public static Scenario Parse(string json, string path = "")
	{
		JsonDocument document;
		try
		{
			document = JsonDocument.Parse(json, new JsonDocumentOptions
			{
				AllowTrailingCommas = true,
				CommentHandling = JsonCommentHandling.Skip,
			});
		}
		catch (JsonException error)
		{
			throw new ScenarioException($"'{path}' is not valid JSON: {error.Message}");
		}

		using (document)
		{
			JsonElement root = document.RootElement;
			if (root.ValueKind != JsonValueKind.Object)
			{
				throw new ScenarioException($"'{path}' is not a scenario object");
			}

			var scenario = new Scenario
			{
				Path = path,
				Name = Text(root, "name", System.IO.Path.GetFileNameWithoutExtension(path)),
				DurationTicks = Math.Max(Int(root, "duration_ticks", DefaultDurationTicks), 1),
				WarmupTicks = Math.Max(Int(root, "warmup_ticks", 60), 0),
				Seeds = ReadSeeds(root),
			};

			if (root.TryGetProperty("roster", out JsonElement roster) && roster.ValueKind == JsonValueKind.Object)
			{
				scenario.GroundBots = Math.Max(Int(roster, "ground", 1), 0);
				scenario.StrategistBots = Math.Max(Int(roster, "strategists", 0), 0);
			}

			if (root.TryGetProperty("bot_ai", out JsonElement botAi))
			{
				string named = botAi.ValueKind == JsonValueKind.String ? botAi.GetString() : null;
				if (named is not ("legacy" or "htn"))
				{
					throw new ScenarioException($"'{scenario.Name}': 'bot_ai' is \"legacy\" or \"htn\"");
				}

				scenario.BotAi = named;
			}

			ReadSeats(scenario, root);
			ReadSpawns(scenario, root);
			CheckLabels(scenario);
			ReadAssertions(scenario, root);

			if (scenario.Assertions.Count == 0)
			{
				throw new ScenarioException(
					$"'{scenario.Name}' claims nothing: a scenario with no assertions cannot fail,"
					+ " and a test that cannot fail is not one");
			}

			return scenario;
		}
	}

	private static int[] ReadSeeds(JsonElement root)
	{
		if (root.TryGetProperty("seeds", out JsonElement seeds) && seeds.ValueKind == JsonValueKind.Array)
		{
			var read = new List<int>();
			foreach (JsonElement seed in seeds.EnumerateArray())
			{
				if (seed.TryGetInt32(out int value))
				{
					read.Add(value);
				}
			}

			if (read.Count > 0)
			{
				return read.ToArray();
			}
		}

		return new[] { Int(root, "seed", 0) };
	}

	private static void ReadSeats(Scenario scenario, JsonElement root)
	{
		if (!root.TryGetProperty("seats", out JsonElement seats) || seats.ValueKind != JsonValueKind.Array)
		{
			return;
		}

		int index = 0;
		foreach (JsonElement element in seats.EnumerateArray())
		{
			if (element.ValueKind != JsonValueKind.Object)
			{
				throw new ScenarioException($"'{scenario.Name}': every entry in 'seats' is an object");
			}

			var seat = new ScenarioSeat
			{
				Id = Text(element, "id", $"agent{index}"),
				Team = Text(element, "team", "ground"),
				Policy = Text(element, "policy", "ground"),
				StepMul = Int(element, "step_mul", 0),
			};

			if (seat.Policy is not ("ground" or "strategist"))
			{
				throw new ScenarioException(
					$"'{scenario.Name}': seat '{seat.Id}' has policy '{seat.Policy}';"
					+ " it is 'ground' or 'strategist'");
			}

			seat.Script = ReadScript(scenario, element);
			CheckScriptFits(scenario, seat);
			scenario.Seats.Add(seat);
			index++;
		}
	}

	private static ScenarioScript ReadScript(Scenario scenario, JsonElement seat)
	{
		if (!seat.TryGetProperty("script", out JsonElement script))
		{
			return null;
		}

		if (script.ValueKind == JsonValueKind.String)
		{
			string named = script.GetString();
			string directory = System.IO.Path.GetDirectoryName(scenario.Path) ?? ".";
			string resolved = System.IO.Path.Combine(directory, named);
			if (!System.IO.File.Exists(resolved) && !named.EndsWith(".json", StringComparison.Ordinal))
			{
				resolved += ".json";
			}

			if (!System.IO.File.Exists(resolved))
			{
				throw new ScenarioException($"'{scenario.Name}': no script at '{resolved}'");
			}

			try
			{
				using JsonDocument document = JsonDocument.Parse(System.IO.File.ReadAllText(resolved),
					new JsonDocumentOptions { AllowTrailingCommas = true });
				return ParseScript(scenario, document.RootElement);
			}
			catch (JsonException error)
			{
				throw new ScenarioException($"'{resolved}' is not valid JSON: {error.Message}");
			}
		}

		return ParseScript(scenario, script);
	}

	/// <summary>A script is an array of steps, or an object with <c>steps</c> and <c>repeat</c>.</summary>
	public static ScenarioScript ParseScript(Scenario scenario, JsonElement element)
	{
		bool repeat = true;
		JsonElement steps = element;

		if (element.ValueKind == JsonValueKind.Object)
		{
			repeat = !element.TryGetProperty("repeat", out JsonElement value)
				|| value.ValueKind != JsonValueKind.False;

			if (!element.TryGetProperty("steps", out steps))
			{
				throw new ScenarioException($"'{scenario.Name}': a script object needs 'steps'");
			}
		}

		if (steps.ValueKind != JsonValueKind.Array)
		{
			throw new ScenarioException($"'{scenario.Name}': a script is an array of steps");
		}

		var read = new List<ScriptStep>();
		foreach (JsonElement step in steps.EnumerateArray())
		{
			if (step.ValueKind != JsonValueKind.Object)
			{
				throw new ScenarioException($"'{scenario.Name}': every script step is an object");
			}

			var parsed = new ScriptStep
			{
				Ticks = Math.Max(Int(step, "ticks", 1), 1),
				HasInput = step.TryGetProperty("move", out _) || step.TryGetProperty("look", out _)
					|| step.TryGetProperty("buttons", out _),
				Commands = ReadCommands(scenario, step),
			};

			if (step.TryGetProperty("move", out JsonElement move) && move.ValueKind == JsonValueKind.Array
				&& move.GetArrayLength() >= 2)
			{
				parsed.MoveX = (float)move[0].GetDouble();
				parsed.MoveZ = (float)move[1].GetDouble();
			}

			if (step.TryGetProperty("look", out JsonElement look) && look.ValueKind == JsonValueKind.Object)
			{
				parsed.DeltaYaw = (float)Number(look, "dyaw", 0d);
				parsed.DeltaPitch = (float)Number(look, "dpitch", 0d);
			}

			if (step.TryGetProperty("buttons", out JsonElement buttons)
				&& buttons.ValueKind == JsonValueKind.Array)
			{
				var names = new List<string>();
				foreach (JsonElement button in buttons.EnumerateArray())
				{
					if (button.ValueKind == JsonValueKind.String)
					{
						names.Add(button.GetString());
					}
				}

				parsed.Buttons = names.ToArray();
			}

			read.Add(parsed);
		}

		return new ScenarioScript(read, repeat);
	}

	/// <summary>A strategist step's <c>commands</c>, each checked for what its kind needs; null when the step has none.</summary>
	private static ScriptCommand[] ReadCommands(Scenario scenario, JsonElement step)
	{
		if (!step.TryGetProperty("commands", out JsonElement commands))
		{
			return null;
		}

		if (commands.ValueKind != JsonValueKind.Array)
		{
			throw new ScenarioException($"'{scenario.Name}': a step's 'commands' is an array");
		}

		var read = new List<ScriptCommand>();
		foreach (JsonElement element in commands.EnumerateArray())
		{
			if (element.ValueKind != JsonValueKind.Object)
			{
				throw new ScenarioException($"'{scenario.Name}': every command in a step is an object");
			}

			var command = new ScriptCommand
			{
				Cmd = Text(element, "cmd", null),
				Kind = Text(element, "kind", "move"),
				Units = Labels(scenario, element, "units"),
				Vehicle = Text(element, "vehicle", null),
				Target = Numbers(element, "target", Array.Empty<float>()),
			};

			if (Array.IndexOf(ScriptCommand.Names, command.Cmd) < 0)
			{
				throw new ScenarioException(
					$"'{scenario.Name}': a step's command is one of {string.Join(", ", ScriptCommand.Names)},"
					+ $" not '{command.Cmd ?? "nothing"}'");
			}

			if (command.Units.Length == 0)
			{
				throw new ScenarioException(
					$"'{scenario.Name}': a '{command.Cmd}' command names its units by spawn label, in 'units'");
			}

			if (command.Cmd == "embark" && command.Vehicle == null)
			{
				throw new ScenarioException(
					$"'{scenario.Name}': an 'embark' command names the vehicle to get into, by spawn label, in 'vehicle'");
			}

			if (command.Cmd == "order")
			{
				if (Array.IndexOf(ScriptCommand.OrderKinds, command.Kind) < 0)
				{
					throw new ScenarioException(
						$"'{scenario.Name}': an order's 'kind' is one of {string.Join(", ", ScriptCommand.OrderKinds)}");
				}

				if (command.Kind != "stop" && command.Target.Length < 2)
				{
					throw new ScenarioException(
						$"'{scenario.Name}': a '{command.Kind}' order needs a 'target' of [x, z]");
				}
			}

			read.Add(command);
		}

		return read.ToArray();
	}

	/// <summary>
	/// A script drives its seat the only way the seat can be driven: a ground seat by
	/// input, a strategist seat by commands. The other kind of step would do nothing,
	/// silently, which is a scenario that tests less than it says.
	/// </summary>
	private static void CheckScriptFits(Scenario scenario, ScenarioSeat seat)
	{
		if (seat.Script == null)
		{
			return;
		}

		bool strategist = seat.Policy == "strategist";
		foreach (ScriptStep step in seat.Script.Steps)
		{
			if (strategist && step.HasInput)
			{
				throw new ScenarioException(
					$"'{scenario.Name}': seat '{seat.Id}' is a strategist; its script's steps carry 'commands',"
					+ " not 'move', 'look' or 'buttons'");
			}

			if (!strategist && step.Commands != null)
			{
				throw new ScenarioException(
					$"'{scenario.Name}': seat '{seat.Id}' is a ground seat; only a strategist seat's script carries 'commands'");
			}
		}
	}

	/// <summary>
	/// Every label a strategist script names is a unit spawn's (docs/VEHICLES.md §11) —
	/// checked here, not when the command would have been sent, so a typo is exit 2
	/// before a server is started rather than a command that finds nobody half a round in.
	/// </summary>
	private static void CheckLabels(Scenario scenario)
	{
		var labels = new HashSet<string>(StringComparer.Ordinal);
		foreach (ScenarioSpawn spawn in scenario.Spawns)
		{
			if (spawn.Id != null)
			{
				labels.Add(spawn.Id);
			}
		}

		foreach (ScenarioSeat seat in scenario.Seats)
		{
			for (int i = 0; seat.Script != null && i < seat.Script.Steps.Count; i++)
			{
				ScriptCommand[] commands = seat.Script.Steps[i].Commands;
				for (int c = 0; commands != null && c < commands.Length; c++)
				{
					foreach (string unit in commands[c].Units)
					{
						RequireLabel(scenario, labels, unit, commands[c].Cmd);
					}

					if (commands[c].Vehicle != null)
					{
						RequireLabel(scenario, labels, commands[c].Vehicle, commands[c].Cmd);
					}
				}
			}
		}
	}

	private static void RequireLabel(Scenario scenario, HashSet<string> labels, string label, string command)
	{
		if (!labels.Contains(label))
		{
			throw new ScenarioException(
				$"'{scenario.Name}': a '{command}' command names '{label}', and no unit spawn has that 'id'");
		}
	}

	/// <summary>An array of strings: spawn labels. Anything that is not a string is a mistake worth a sentence.</summary>
	private static string[] Labels(Scenario scenario, JsonElement element, string name)
	{
		if (!element.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.Array)
		{
			return Array.Empty<string>();
		}

		var read = new List<string>();
		foreach (JsonElement label in value.EnumerateArray())
		{
			if (label.ValueKind != JsonValueKind.String)
			{
				throw new ScenarioException($"'{scenario.Name}': a command's '{name}' are spawn labels, which are strings");
			}

			read.Add(label.GetString());
		}

		return read.ToArray();
	}

	private static void ReadSpawns(Scenario scenario, JsonElement root)
	{
		if (!root.TryGetProperty("spawns", out JsonElement spawns) || spawns.ValueKind != JsonValueKind.Array)
		{
			return;
		}

		foreach (JsonElement element in spawns.EnumerateArray())
		{
			if (element.ValueKind != JsonValueKind.Object)
			{
				throw new ScenarioException($"'{scenario.Name}': every entry in 'spawns' is an object");
			}

			var spawn = new ScenarioSpawn
			{
				Peer = Text(element, "peer", null),
				Unit = Text(element, "unit", null),
				Structure = Text(element, "structure", null),
				Yaw = (float)Number(element, "yaw", 0.0),
				Team = Text(element, "team", "strategist"),
				Order = Text(element, "order", "none"),
				At = Numbers(element, "at", new[] { 0f, 0f, 0f }),
				Look = Numbers(element, "look", new[] { 0f, 0f }),
				Id = Text(element, "id", null),
				Aboard = Text(element, "aboard", null),
				Seat = Text(element, "seat", null),
			};

			int named = (spawn.Peer != null ? 1 : 0) + (spawn.Unit != null ? 1 : 0) + (spawn.Structure != null ? 1 : 0);
			if (named != 1)
			{
				throw new ScenarioException(
					$"'{scenario.Name}': a spawn names exactly one of a 'peer' (a seat to place), a 'unit'"
					+ " or a 'structure'");
			}

			CheckVehicleFields(scenario, spawn);
			scenario.Spawns.Add(spawn);
		}
	}

	/// <summary>
	/// A spawn's label and what it starts aboard (docs/VEHICLES.md §11). A label is a unit
	/// spawn's and unique. <c>aboard</c> names an earlier unit spawn — earlier, because the
	/// vehicle has to be on the field to be got into — that is not itself aboard
	/// anything, and needs a strategist seat for the cargo to answer to: one a vehicle's
	/// loader has gone is unloaded on its first tick (§5.3), so a scenario with nobody to
	/// load it would test the orphan rule and nothing else.
	/// </summary>
	private static void CheckVehicleFields(Scenario scenario, ScenarioSpawn spawn)
	{
		if ((spawn.Id != null || spawn.Aboard != null || spawn.Seat != null) && spawn.Unit == null)
		{
			throw new ScenarioException(
				$"'{scenario.Name}': only a unit spawn takes an 'id', an 'aboard' or a 'seat'");
		}

		if (spawn.Id != null && FindSpawn(scenario, spawn.Id) != null)
		{
			throw new ScenarioException($"'{scenario.Name}': two spawns have the id '{spawn.Id}'");
		}

		if (spawn.Seat != null && spawn.Aboard == null)
		{
			throw new ScenarioException(
				$"'{scenario.Name}': a spawn's 'seat' is the strategist seat that loaded it, and it is aboard nothing");
		}

		if (spawn.Aboard == null)
		{
			return;
		}

		ScenarioSpawn vehicle = FindSpawn(scenario, spawn.Aboard);
		if (vehicle == null || spawn.Aboard == spawn.Id)
		{
			throw new ScenarioException(
				$"'{scenario.Name}': a spawn starts aboard '{spawn.Aboard}', which is not the 'id' of an earlier unit spawn");
		}

		if (vehicle.Aboard != null)
		{
			throw new ScenarioException(
				$"'{scenario.Name}': '{spawn.Aboard}' is itself aboard '{vehicle.Aboard}'; nothing rides in a passenger");
		}

		if (LoaderOf(scenario, spawn) == null)
		{
			throw new ScenarioException(spawn.Seat != null
				? $"'{scenario.Name}': a spawn is loaded by seat '{spawn.Seat}', and no strategist seat has that id"
				: $"'{scenario.Name}': a spawn starts aboard '{spawn.Aboard}', and there is not exactly one strategist"
					+ " seat for its cargo to answer to; attach one, or name it in the spawn's 'seat'");
		}
	}

	/// <summary>The earlier unit spawn with this label, or null.</summary>
	private static ScenarioSpawn FindSpawn(Scenario scenario, string id)
	{
		foreach (ScenarioSpawn spawn in scenario.Spawns)
		{
			if (spawn.Id == id)
			{
				return spawn;
			}
		}

		return null;
	}

	/// <summary>
	/// The strategist seat a spawn aboard a vehicle was loaded by: the one its <c>seat</c>
	/// names, or the scenario's only strategist seat; null when that is nobody.
	/// </summary>
	public static ScenarioSeat LoaderOf(Scenario scenario, ScenarioSpawn spawn)
	{
		ScenarioSeat only = null;
		int strategists = 0;
		foreach (ScenarioSeat seat in scenario.Seats)
		{
			if (seat.Policy != "strategist")
			{
				continue;
			}

			if (spawn.Seat != null && seat.Id == spawn.Seat)
			{
				return seat;
			}

			only = seat;
			strategists++;
		}

		return spawn.Seat == null && strategists == 1 ? only : null;
	}

	private static void ReadAssertions(Scenario scenario, JsonElement root)
	{
		if (!root.TryGetProperty("assert", out JsonElement asserts) || asserts.ValueKind != JsonValueKind.Array)
		{
			return;
		}

		foreach (JsonElement element in asserts.EnumerateArray())
		{
			scenario.Assertions.Add(ReadAssertion(scenario, element));
		}
	}

	private static Assertion ReadAssertion(Scenario scenario, JsonElement element)
	{
		if (element.ValueKind != JsonValueKind.Object)
		{
			throw new ScenarioException($"'{scenario.Name}': every entry in 'assert' is an object");
		}

		string metric = Text(element, "metric", null);
		if (string.IsNullOrWhiteSpace(metric))
		{
			throw new ScenarioException($"'{scenario.Name}': every assertion names a 'metric'");
		}

		string opName = Text(element, "op", ">=");
		AssertionOp? op = AssertionEvaluator.ParseOp(opName);
		if (op == null)
		{
			throw new ScenarioException(
				$"'{scenario.Name}': no operator '{opName}'. The vocabulary is ==, !=, <, <=, >, >=,"
				+ " between, percentile and over_seeds — and deliberately nothing that names a tick"
				+ " (docs/AGENT_API.md §3)");
		}

		var assertion = new Assertion
		{
			Metric = metric,
			Op = op.Value,
			Percentile = Number(element, "p", 50d),
			Fraction = Number(element, "fraction", 1d),
			Tolerance = Number(element, "tolerance", 1e-4d),
		};

		if (element.TryGetProperty("cmp", out JsonElement cmp) && cmp.ValueKind == JsonValueKind.String)
		{
			AssertionOp? comparison = AssertionEvaluator.ParseOp(cmp.GetString());
			if (comparison == null || comparison.Value is AssertionOp.Between or AssertionOp.Percentile
				or AssertionOp.OverSeeds)
			{
				throw new ScenarioException(
					$"'{scenario.Name}': '{cmp.GetString()}' is not a comparison a distributional"
					+ " operator can apply");
			}

			assertion.Comparison = comparison.Value;
		}

		if (assertion.Op == AssertionOp.Between)
		{
			float[] range = Numbers(element, "value", null);
			if (range == null || range.Length < 2)
			{
				throw new ScenarioException(
					$"'{scenario.Name}': 'between' takes a two-element value, e.g. [100, 400]");
			}

			assertion.Range = new double[] { range[0], range[1] };
			if (assertion.Range[0] > assertion.Range[1])
			{
				throw new ScenarioException($"'{scenario.Name}': 'between' has its bounds the wrong way round");
			}
		}
		else
		{
			if (!element.TryGetProperty("value", out JsonElement value) || !value.TryGetDouble(out double number))
			{
				throw new ScenarioException($"'{scenario.Name}': '{metric}' needs a numeric 'value'");
			}

			assertion.Value = number;
		}

		if (assertion.Op == AssertionOp.OverSeeds && (assertion.Fraction <= 0d || assertion.Fraction > 1d))
		{
			throw new ScenarioException(
				$"'{scenario.Name}': 'over_seeds' takes a fraction in (0,1], not {assertion.Fraction}");
		}

		return assertion;
	}

	// ---- json helpers ------------------------------------------------------

	private static string Text(JsonElement root, string name, string fallback) =>
		root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
			? value.GetString()
			: fallback;

	private static int Int(JsonElement root, string name, int fallback) =>
		root.TryGetProperty(name, out JsonElement value) && value.TryGetInt32(out int parsed)
			? parsed
			: fallback;

	private static double Number(JsonElement root, string name, double fallback) =>
		root.TryGetProperty(name, out JsonElement value) && value.TryGetDouble(out double parsed)
			? parsed
			: fallback;

	private static float[] Numbers(JsonElement root, string name, float[] fallback)
	{
		if (!root.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.Array)
		{
			return fallback;
		}

		var read = new float[value.GetArrayLength()];
		int at = 0;
		foreach (JsonElement element in value.EnumerateArray())
		{
			read[at++] = element.TryGetDouble(out double parsed) ? (float)parsed : 0f;
		}

		return read;
	}
}
