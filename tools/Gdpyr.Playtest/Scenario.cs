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

/// <summary>One step of a scripted seat: what to do, and for how many ticks.</summary>
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

	public string Team = "strategist";

	public string Order = "none";

	public float[] At = { 0f, 0f, 0f };

	public float[] Look = { 0f, 0f };
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
				Seeds = Seeds(root),
			};

			if (root.TryGetProperty("roster", out JsonElement roster) && roster.ValueKind == JsonValueKind.Object)
			{
				scenario.GroundBots = Math.Max(Int(roster, "ground", 1), 0);
				scenario.StrategistBots = Math.Max(Int(roster, "strategists", 0), 0);
			}

			ReadSeats(scenario, root);
			ReadSpawns(scenario, root);
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

	private static int[] Seeds(JsonElement root)
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

			var parsed = new ScriptStep { Ticks = Math.Max(Int(step, "ticks", 1), 1) };

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
				Team = Text(element, "team", "strategist"),
				Order = Text(element, "order", "none"),
				At = Numbers(element, "at", new[] { 0f, 0f, 0f }),
				Look = Numbers(element, "look", new[] { 0f, 0f }),
			};

			if (spawn.Peer == null && spawn.Unit == null)
			{
				throw new ScenarioException(
					$"'{scenario.Name}': a spawn names either a 'peer' (a seat to place) or a 'unit'");
			}

			scenario.Spawns.Add(spawn);
		}
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
