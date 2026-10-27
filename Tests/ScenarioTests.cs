using System;
using System.Collections.Generic;
using Gdpyr.Playtest;
using Xunit;

namespace Gdpyr.Tests;

/// <summary>
/// Scenario files and the assertion vocabulary (docs/AGENT_API.md §9.1).
///
/// A scenario is data, and the promise is that authoring one needs no Godot
/// install — so what a scenario file means is answered here, by
/// <c>dotnet test</c>, with no server and no engine. The vocabulary is
/// distributional on purpose (§3): <c>between</c>, <c>percentile</c> and
/// <c>over_seeds</c> exist, and there is deliberately nothing that names a tick.
/// </summary>
public class ScenarioTests
{
	private const string Minimal = """
		{
		  "name": "a claim",
		  "seed": 7,
		  "assert": [ { "metric": "events.kill.count", "op": ">=", "value": 1 } ]
		}
		""";

	// ---- the parser --------------------------------------------------------

	[Fact]
	public void AScenarioIsSeedRosterSeatsAndClaims()
	{
		Scenario scenario = Scenario.Parse("""
			{
			  "name": "rifle lethality at 100 m",
			  "seed": 7,
			  "duration_ticks": 1200,
			  "roster": { "ground": 1, "strategists": 0 },
			  "seats": [ { "team": "ground", "policy": "ground", "step_mul": 4 } ],
			  "spawns": [ { "peer": "agent0", "at": [0, 1, 0], "look": [0, 0] },
			              { "unit": "infantry", "team": "strategist", "at": [0, 1, -100] } ],
			  "assert": [ { "metric": "events.kill.count", "op": ">=", "value": 1 } ]
			}
			""");

		Assert.Equal("rifle lethality at 100 m", scenario.Name);
		Assert.Equal(new[] { 7 }, scenario.Seeds);
		Assert.Equal(1200, scenario.DurationTicks);
		Assert.Equal(1, scenario.GroundBots);
		Assert.Equal(0, scenario.StrategistBots);

		// A seat that does not name itself is agent0, agent1, … so a spawn can.
		Assert.Single(scenario.Seats);
		Assert.Equal("agent0", scenario.Seats[0].Id);
		Assert.Equal(4, scenario.Seats[0].StepMul);

		Assert.Equal(2, scenario.Spawns.Count);
		Assert.Equal("agent0", scenario.Spawns[0].Peer);
		Assert.Equal(-100f, scenario.Spawns[1].At[2]);
	}

	[Fact]
	public void AScenarioMayNameTheBotAiItsClaimsAreAbout()
	{
		// docs/HTN_BOTS.md §6, H6: a claim about the HTN bots names them, and the
		// harness starts its server with them unless --bot-ai says otherwise.
		Scenario htn = Scenario.Parse("""
			{ "bot_ai": "htn", "assert": [ { "metric": "events.kill.count", "op": ">=", "value": 1 } ] }
			""");
		Assert.Equal("htn", htn.BotAi);
		Assert.Null(Scenario.Parse(Minimal).BotAi);

		ScenarioException error = Assert.Throws<ScenarioException>(() => Scenario.Parse("""
			{ "bot_ai": "fsm", "assert": [ { "metric": "events.kill.count", "op": ">=", "value": 1 } ] }
			"""));
		Assert.Contains("bot_ai", error.Message);
		Assert.Throws<ScenarioException>(() => Scenario.Parse("""
			{ "bot_ai": 1, "assert": [ { "metric": "events.kill.count", "op": ">=", "value": 1 } ] }
			"""));
	}

	[Fact]
	public void AScenarioMayNameWhatItsUnitsStartWith()
	{
		// docs/TACTICS.md §5.9: a claim about the tactics names them, and the harness
		// starts its server with them unless --unit-autonomy says otherwise.
		Scenario on = Scenario.Parse("""
			{ "unit_autonomy": "on", "assert": [ { "metric": "events.kill.count", "op": ">=", "value": 1 } ] }
			""");
		Assert.Equal("on", on.UnitAutonomy);
		Assert.Null(Scenario.Parse(Minimal).UnitAutonomy);

		ScenarioException error = Assert.Throws<ScenarioException>(() => Scenario.Parse("""
			{ "unit_autonomy": "yes", "assert": [ { "metric": "events.kill.count", "op": ">=", "value": 1 } ] }
			"""));
		Assert.Contains("unit_autonomy", error.Message);
	}

	[Fact]
	public void AUnitSpawnMayNameItsOwnMicroAutonomy()
	{
		Scenario scenario = Scenario.Parse(WithTruck("""{ "unit": "infantry", "id": "r1", "at": [3, 1, 0], "autonomy": "on" }"""));
		Assert.Equal("on", scenario.Spawns[1].Autonomy);
		Assert.Null(scenario.Spawns[0].Autonomy);

		ScenarioException bad = Assert.Throws<ScenarioException>(() => Scenario.Parse(
			WithTruck("""{ "unit": "infantry", "at": [3, 1, 0], "autonomy": "maybe" }""")));
		Assert.Contains("'autonomy' is", bad.Message);
		Assert.Throws<ScenarioException>(() => Scenario.Parse(
			WithTruck("""{ "structure": "sandbag_wall", "at": [9, 1, 0], "autonomy": "on" }""")));
	}

	[Fact]
	public void AStrategistScript_MaySetMicroAutonomy_ForUnitsAndForNewOnes()
	{
		Scenario scenario = Scenario.Parse(Scripted("""
			{ "ticks": 1, "commands": [ { "cmd": "autonomy", "units": ["r1", "r2"], "mode": "on" },
			                            { "cmd": "autonomy_default", "mode": "off" } ] }
			"""));

		Assert.True(scenario.Seats[0].Script.TryEnter(0, out ScriptStep step));
		Assert.Equal("autonomy", step.Commands[0].Cmd);
		Assert.Equal(new[] { "r1", "r2" }, step.Commands[0].Units);
		Assert.Equal("on", step.Commands[0].Mode);
		Assert.Equal("autonomy_default", step.Commands[1].Cmd);
		Assert.Empty(step.Commands[1].Units);
		Assert.Equal("off", step.Commands[1].Mode);
	}

	[Theory]
	[InlineData("""{ "ticks": 1, "commands": [ { "cmd": "autonomy", "units": ["r1"] } ] }""", "'mode' is")]
	[InlineData("""{ "ticks": 1, "commands": [ { "cmd": "autonomy", "units": ["r1"], "mode": "auto" } ] }""", "'mode' is")]
	[InlineData("""{ "ticks": 1, "commands": [ { "cmd": "autonomy", "units": [], "mode": "on" } ] }""",
		"names its units by spawn label")]
	[InlineData("""{ "ticks": 1, "commands": [ { "cmd": "autonomy_default" } ] }""", "'mode' is")]
	public void AnAutonomyCommandThatCannotBeSent_IsASentence(string steps, string expected)
	{
		ScenarioException error = Assert.Throws<ScenarioException>(() => Scenario.Parse(Scripted(steps)));
		Assert.Contains(expected, error.Message);
	}

	[Fact]
	public void AStrategistScript_MaySendBuildersToPutAStructureUp()
	{
		Scenario scenario = Scenario.Parse(Scripted("""
			{ "ticks": 1, "commands": [ { "cmd": "construct", "units": ["r1"], "structure": "sandbag_wall",
			                              "target": [-11, 12], "yaw": 3.14159 } ] }
			"""));

		Assert.True(scenario.Seats[0].Script.TryEnter(0, out ScriptStep step));
		Assert.Equal("construct", step.Commands[0].Cmd);
		Assert.Equal(new[] { "r1" }, step.Commands[0].Units);
		Assert.Equal("sandbag_wall", step.Commands[0].Structure);
		Assert.Equal(new[] { -11f, 12f }, step.Commands[0].Target);
		Assert.Equal(3.14159f, step.Commands[0].Yaw, 4);
	}

	[Theory]
	[InlineData("""{ "ticks": 1, "commands": [ { "cmd": "construct", "units": ["r1"], "target": [1, 2] } ] }""")]
	[InlineData("""{ "ticks": 1, "commands": [ { "cmd": "construct", "units": ["r1"], "structure": "barracks", "target": [1, 2] } ] }""")]
	[InlineData("""{ "ticks": 1, "commands": [ { "cmd": "construct", "units": ["r1"], "structure": "pillbox" } ] }""")]
	public void AConstructCommandThatCannotBeSent_IsASentence(string steps)
	{
		ScenarioException error = Assert.Throws<ScenarioException>(() => Scenario.Parse(Scripted(steps)));
		Assert.Contains("a 'construct' command names its 'structure'", error.Message);
	}

	[Fact]
	public void EveryCheckedInScenarioParsesAndNamesOnlyEventMetricsThatExist()
	{
		// A typo in a checked-in scenario's filter would otherwise surface as a failed
		// playtest, a Godot install later. An event metric resolves on an episode with
		// nothing in it — to zero — exactly when it names a kind, a field, an
		// aggregate and any seat that exist (§9.1).
		string directory = ScenarioDirectory();
		string[] files = System.IO.Directory.GetFiles(directory, "*.json");
		Assert.NotEmpty(files);

		foreach (string file in files)
		{
			Scenario scenario = Scenario.Load(file);
			var empty = new EpisodeMetrics(0);
			foreach (ScenarioSeat seat in scenario.Seats)
			{
				empty.Seat(seat.Id, 0);
			}

			foreach (Assertion assertion in scenario.Assertions)
			{
				if (assertion.Metric.StartsWith("events.", StringComparison.Ordinal))
				{
					Assert.True(empty.TryValue(assertion.Metric, out _, out _),
						$"{System.IO.Path.GetFileName(file)}: {assertion.Metric}");
				}
			}
		}
	}

	private static string ScenarioDirectory()
	{
		for (var at = new System.IO.DirectoryInfo(AppContext.BaseDirectory); at != null; at = at.Parent)
		{
			string candidate = System.IO.Path.Combine(at.FullName, "Tests", "Scenarios");
			if (System.IO.Directory.Exists(candidate))
			{
				return candidate;
			}
		}

		throw new InvalidOperationException("no Tests/Scenarios above " + AppContext.BaseDirectory);
	}

	[Fact]
	public void ASpawnCanPutUpAStructureFacingAWay()
	{
		Scenario scenario = Scenario.Parse("""
			{
			  "spawns": [ { "structure": "sandbag_wall", "at": [-60, 0.5, 50], "yaw": 1.5 } ],
			  "assert": [ { "metric": "events.damage.count", "op": ">=", "value": 1 } ]
			}
			""");

		ScenarioSpawn spawn = Assert.Single(scenario.Spawns);
		Assert.Equal("sandbag_wall", spawn.Structure);
		Assert.Null(spawn.Unit);
		Assert.Null(spawn.Peer);
		Assert.Equal(1.5f, spawn.Yaw);
		Assert.Equal("strategist", spawn.Team);
	}

	[Theory]
	[InlineData("""{ "at": [0, 0, 0] }""")]
	[InlineData("""{ "unit": "infantry", "structure": "pillbox" }""")]
	[InlineData("""{ "peer": "agent0", "unit": "infantry" }""")]
	public void ASpawnNamesExactlyOneThing(string spawn)
	{
		var error = Assert.Throws<ScenarioException>(() => Scenario.Parse($$"""
			{
			  "spawns": [ {{spawn}} ],
			  "assert": [ { "metric": "events.kill.count", "op": ">=", "value": 1 } ]
			}
			"""));

		Assert.Contains("exactly one", error.Message);
	}

	// ---- vehicles (docs/VEHICLES.md §11) ------------------------------------

	/// <summary>A strategist seat, a technical labelled <c>truck</c>, and <paramref name="spawns"/> after it.</summary>
	private static string WithTruck(string spawns, string seats = """{ "id": "chair", "team": "strategist", "policy": "strategist" }""") => $$"""
		{
		  "name": "a truck",
		  "seats": [ {{seats}} ],
		  "spawns": [
		    { "unit": "technical", "id": "truck", "at": [0, 1, 0] }{{(spawns.Length > 0 ? "," : "")}}
		    {{spawns}}
		  ],
		  "assert": [ { "metric": "events.unit_disembarked.count", "op": "==", "value": 1 } ]
		}
		""";

	[Fact]
	public void AUnitSpawnCanStartAboardAnEarlierOne_LoadedByTheOnlyStrategistSeat()
	{
		Scenario scenario = Scenario.Parse(WithTruck("""{ "unit": "infantry", "id": "r1", "aboard": "truck" }"""));

		ScenarioSpawn rider = scenario.Spawns[1];
		Assert.Equal("truck", scenario.Spawns[0].Id);
		Assert.Equal("r1", rider.Id);
		Assert.Equal("truck", rider.Aboard);
		Assert.Equal("chair", Scenario.LoaderOf(scenario, rider).Id);
	}

	[Fact]
	public void TheDropshipScenario_SeatsEight_AllLoadedByItsOneStrategistSeat()
	{
		// docs/VEHICLES.md §11: the dropship's scenario, eight aboard one airframe.
		string path = System.IO.Path.Combine(ScenarioDirectory(), "dropship_lands_to_unload.json");
		Scenario scenario = Scenario.Load(path);

		ScenarioSpawn ship = scenario.Spawns[0];
		Assert.Equal("dropship", ship.Unit);
		Assert.Equal("ship", ship.Id);

		int aboard = 0;
		foreach (ScenarioSpawn spawn in scenario.Spawns)
		{
			if (spawn.Aboard == "ship")
			{
				aboard++;
				Assert.Equal("chair", Scenario.LoaderOf(scenario, spawn).Id);
			}
		}

		Assert.Equal(8, aboard);
	}

	[Theory]
	[InlineData("""{ "unit": "infantry", "aboard": "lorry" }""", "not the 'id' of an earlier unit spawn")]
	[InlineData("""{ "unit": "infantry", "id": "r1", "aboard": "r1" }""", "not the 'id' of an earlier unit spawn")]
	[InlineData("""{ "unit": "infantry", "id": "truck" }""", "two spawns have the id 'truck'")]
	[InlineData("""{ "peer": "chair", "id": "me" }""", "only a unit spawn takes")]
	[InlineData("""{ "unit": "infantry", "seat": "chair" }""", "aboard nothing")]
	[InlineData("""{ "unit": "infantry", "id": "r1", "aboard": "truck" }, { "unit": "infantry", "aboard": "r1" }""",
		"nothing rides in a passenger")]
	public void AVehicleFieldThatCannotBeResolved_IsASentence(string spawns, string expected)
	{
		ScenarioException error = Assert.Throws<ScenarioException>(() => Scenario.Parse(WithTruck(spawns)));
		Assert.Contains(expected, error.Message);
	}

	[Fact]
	public void AForwardReference_IsRefused_BecauseTheVehicleMustBeOnTheFieldFirst()
	{
		ScenarioException error = Assert.Throws<ScenarioException>(() => Scenario.Parse("""
			{
			  "name": "backwards",
			  "seats": [ { "id": "chair", "team": "strategist", "policy": "strategist" } ],
			  "spawns": [
			    { "unit": "infantry", "aboard": "truck" },
			    { "unit": "technical", "id": "truck", "at": [0, 1, 0] }
			  ],
			  "assert": [ { "metric": "events.unit_disembarked.count", "op": "==", "value": 1 } ]
			}
			"""));
		Assert.Contains("earlier unit spawn", error.Message);
	}

	[Theory]
	[InlineData("""{ "id": "a0", "team": "ground", "policy": "ground" }""", "not exactly one strategist seat")]
	[InlineData("""{ "id": "a", "team": "strategist", "policy": "strategist" }, { "id": "b", "team": "strategist", "policy": "strategist" }""",
		"not exactly one strategist seat")]
	public void ASpawnAboard_NeedsOneStrategistSeatForItsCargoToAnswerTo(string seats, string expected)
	{
		ScenarioException error = Assert.Throws<ScenarioException>(() =>
			Scenario.Parse(WithTruck("""{ "unit": "infantry", "aboard": "truck" }""", seats)));
		Assert.Contains(expected, error.Message);
	}

	[Fact]
	public void WithTwoStrategistSeats_ASpawnAboardNamesItsLoader()
	{
		Scenario scenario = Scenario.Parse(WithTruck("""{ "unit": "infantry", "aboard": "truck", "seat": "b" }""",
			"""{ "id": "a", "team": "strategist", "policy": "strategist" }, { "id": "b", "team": "strategist", "policy": "strategist" }"""));

		Assert.Equal("b", Scenario.LoaderOf(scenario, scenario.Spawns[1]).Id);

		ScenarioException error = Assert.Throws<ScenarioException>(() =>
			Scenario.Parse(WithTruck("""{ "unit": "infantry", "aboard": "truck", "seat": "c" }""",
				"""{ "id": "a", "team": "strategist", "policy": "strategist" }""")));
		Assert.Contains("no strategist seat has that id", error.Message);
	}

	/// <summary>A strategist seat scripted with <paramref name="steps"/>, the truck and two riflemen.</summary>
	private static string Scripted(string steps, string policy = "strategist") => $$"""
		{
		  "name": "scripted",
		  "seats": [ { "id": "chair", "team": "strategist", "policy": "{{policy}}", "script": { "repeat": false, "steps": [ {{steps}} ] } } ],
		  "spawns": [
		    { "unit": "technical", "id": "truck", "at": [0, 1, 0] },
		    { "unit": "infantry", "id": "r1", "at": [3, 1, 0] },
		    { "unit": "infantry", "id": "r2", "at": [4, 1, 0] }
		  ],
		  "assert": [ { "metric": "events.unit_embarked.count", "op": "==", "value": 2 } ]
		}
		""";

	[Fact]
	public void AStrategistScript_IsCommandsByLabel_SentOnceOnEachStepsFirstTick()
	{
		Scenario scenario = Scenario.Parse(Scripted("""
			{ "ticks": 30, "commands": [ { "cmd": "embark", "units": ["r1", "r2"], "vehicle": "truck" } ] },
			{ "ticks": 240, "commands": [ { "cmd": "order", "kind": "move", "units": ["truck"], "target": [60, 0] } ] },
			{ "ticks": 60, "commands": [ { "cmd": "unload", "units": ["truck"] } ] }
			"""));

		ScenarioScript script = scenario.Seats[0].Script;
		Assert.True(script.TryEnter(0, out ScriptStep first));
		ScriptCommand embark = Assert.Single(first.Commands);
		Assert.Equal("embark", embark.Cmd);
		Assert.Equal(new[] { "r1", "r2" }, embark.Units);
		Assert.Equal("truck", embark.Vehicle);

		Assert.False(script.TryEnter(1, out _));
		Assert.False(script.TryEnter(29, out _));
		Assert.True(script.TryEnter(30, out ScriptStep second));
		Assert.Equal(new[] { 60f, 0f }, Assert.Single(second.Commands).Target);
		Assert.True(script.TryEnter(270, out ScriptStep third));
		Assert.Equal("unload", Assert.Single(third.Commands).Cmd);

		// It does not repeat: nothing is entered after the last step.
		Assert.False(script.TryEnter(330, out _));
		Assert.False(script.TryEnter(360, out _));
	}

	[Fact]
	public void ARepeatingScript_EntersItsStepsAgainOnEveryPass()
	{
		var script = new ScenarioScript(new[] { new ScriptStep { Ticks = 10 }, new ScriptStep { Ticks = 5 } }, repeat: true);

		Assert.True(script.TryEnter(0, out _));
		Assert.True(script.TryEnter(10, out _));
		Assert.True(script.TryEnter(15, out _));
		Assert.True(script.TryEnter(25, out _));
		Assert.False(script.TryEnter(16, out _));
	}

	[Theory]
	[InlineData("""{ "ticks": 1, "commands": [ { "cmd": "embark", "units": ["r9"], "vehicle": "truck" } ] }""",
		"names 'r9', and no unit spawn has that 'id'")]
	[InlineData("""{ "ticks": 1, "commands": [ { "cmd": "embark", "units": ["r1"], "vehicle": "lorry" } ] }""",
		"names 'lorry'")]
	[InlineData("""{ "ticks": 1, "commands": [ { "cmd": "embark", "units": ["r1"] } ] }""", "names the vehicle")]
	[InlineData("""{ "ticks": 1, "commands": [ { "cmd": "order", "kind": "move", "units": ["r1"] } ] }""",
		"needs a 'target'")]
	[InlineData("""{ "ticks": 1, "commands": [ { "cmd": "order", "kind": "charge", "units": ["r1"], "target": [0, 0] } ] }""",
		"an order's 'kind' is one of")]
	[InlineData("""{ "ticks": 1, "commands": [ { "cmd": "board", "units": ["r1"] } ] }""", "not 'board'")]
	[InlineData("""{ "ticks": 1, "commands": [ { "cmd": "unload", "units": [] } ] }""", "names its units by spawn label")]
	[InlineData("""{ "ticks": 1, "move": [1, 0] }""", "its script's steps carry 'commands'")]
	public void AStrategistScriptThatCannotBeSent_IsASentence(string steps, string expected)
	{
		ScenarioException error = Assert.Throws<ScenarioException>(() => Scenario.Parse(Scripted(steps)));
		Assert.Contains(expected, error.Message);
	}

	[Fact]
	public void AGroundSeatsScript_CarriesNoCommands()
	{
		ScenarioException error = Assert.Throws<ScenarioException>(() => Scenario.Parse(Scripted(
			"""{ "ticks": 1, "commands": [ { "cmd": "unload", "units": ["truck"] } ] }""", policy: "ground")));
		Assert.Contains("only a strategist seat's script carries 'commands'", error.Message);
	}

	[Fact]
	public void AStopNeedsNoTarget()
	{
		Scenario scenario = Scenario.Parse(Scripted(
			"""{ "ticks": 1, "commands": [ { "cmd": "order", "kind": "stop", "units": ["truck"] } ] }"""));

		Assert.Equal("stop", Assert.Single(scenario.Seats[0].Script.Steps[0].Commands).Kind);
	}

	[Fact]
	public void ASweepIsASeedsArrayAndASingleRunIsASeed()
	{
		Assert.Equal(new[] { 7, 11, 13 }, Scenario.Parse("""
			{ "seeds": [7, 11, 13],
			  "assert": [ { "metric": "round.ticks", "op": ">", "value": 0 } ] }
			""").Seeds);

		Assert.Equal(new[] { 0 }, Scenario.Parse("""
			{ "assert": [ { "metric": "round.ticks", "op": ">", "value": 0 } ] }
			""").Seeds);
	}

	[Fact]
	public void AScenarioThatClaimsNothingIsRefused()
	{
		ScenarioException error = Assert.Throws<ScenarioException>(
			() => Scenario.Parse("""{ "name": "nothing" }"""));

		Assert.Contains("cannot fail", error.Message);
	}

	[Fact]
	public void MalformedJsonIsASentenceRatherThanAStackTrace()
	{
		Assert.Throws<ScenarioException>(() => Scenario.Parse("{ not json"));
	}

	[Fact]
	public void AnUnknownOperatorNamesTheWholeVocabulary()
	{
		ScenarioException error = Assert.Throws<ScenarioException>(() => Scenario.Parse("""
			{ "assert": [ { "metric": "round.ticks", "op": "approximately", "value": 1 } ] }
			"""));

		Assert.Contains("over_seeds", error.Message);
		Assert.Contains("names a tick", error.Message);
	}

	[Fact]
	public void ThereIsNoOperatorThatAssertsAPosition()
	{
		// §3: this is a stochastic environment with a seeded core, and a suite that
		// asserts trajectories is a suite that flakes.
		Assert.Null(AssertionEvaluator.ParseOp("assert_position_equals"));
		Assert.Null(AssertionEvaluator.ParseOp("at_tick"));
	}

	[Fact]
	public void BetweenTakesTwoBoundsTheRightWayRound()
	{
		Scenario scenario = Scenario.Parse("""
			{ "assert": [ { "metric": "events.damage.sum", "op": "between", "value": [100, 400] } ] }
			""");

		Assert.Equal(AssertionOp.Between, scenario.Assertions[0].Op);
		Assert.Equal(new double[] { 100d, 400d }, scenario.Assertions[0].Range);

		Assert.Throws<ScenarioException>(() => Scenario.Parse("""
			{ "assert": [ { "metric": "events.damage.sum", "op": "between", "value": [400, 100] } ] }
			"""));

		Assert.Throws<ScenarioException>(() => Scenario.Parse("""
			{ "assert": [ { "metric": "events.damage.sum", "op": "between", "value": 400 } ] }
			"""));
	}

	[Fact]
	public void ADistributionalOperatorCannotNestAnotherOne()
	{
		Assert.Throws<ScenarioException>(() => Scenario.Parse("""
			{ "assert": [ { "metric": "round.ticks", "op": "percentile", "p": 50,
			                "cmp": "over_seeds", "value": 1 } ] }
			"""));
	}

	[Fact]
	public void OverSeedsTakesAFractionInTheUnitInterval()
	{
		Assert.Throws<ScenarioException>(() => Scenario.Parse("""
			{ "assert": [ { "metric": "round.ticks", "op": "over_seeds", "cmp": ">",
			                "value": 1, "fraction": 2 } ] }
			"""));
	}

	[Fact]
	public void AScriptRepeatsUnlessItSaysOtherwise()
	{
		Scenario scenario = Scenario.Parse("""
			{
			  "seats": [ { "script": [ { "ticks": 2, "move": [0, -1], "buttons": ["fire"] },
			                           { "ticks": 3, "look": { "dyaw": 0.5 } } ] } ],
			  "assert": [ { "metric": "round.ticks", "op": ">", "value": 0 } ]
			}
			""");

		ScenarioScript script = scenario.Seats[0].Script;
		Assert.Equal(5, script.TotalTicks);
		Assert.True(script.Repeat);

		Assert.True(script.TryStep(0, out ScriptStep first));
		Assert.Equal(-1f, first.MoveZ);
		Assert.Equal(new[] { "fire" }, first.Buttons);

		Assert.True(script.TryStep(3, out ScriptStep second));
		Assert.Equal(0.5f, second.DeltaYaw, 4);

		// And it wraps.
		Assert.True(script.TryStep(5, out ScriptStep wrapped));
		Assert.Equal(-1f, wrapped.MoveZ);
	}

	[Fact]
	public void AScriptThatDoesNotRepeatHoldsStillAfterwards()
	{
		Scenario scenario = Scenario.Parse("""
			{
			  "seats": [ { "script": { "repeat": false,
			                           "steps": [ { "ticks": 2, "move": [1, 0] } ] } } ],
			  "assert": [ { "metric": "round.ticks", "op": ">", "value": 0 } ]
			}
			""");

		ScenarioScript script = scenario.Seats[0].Script;
		Assert.False(script.Repeat);
		Assert.True(script.TryStep(99, out ScriptStep after));
		Assert.Equal(0f, after.MoveX);
	}

	[Fact]
	public void AScriptFileThatIsNotThereIsAHarnessErrorRatherThanASilentNoOp()
	{
		Assert.Throws<ScenarioException>(() => Scenario.Parse("""
			{
			  "seats": [ { "script": "nowhere/at/all.json" } ],
			  "assert": [ { "metric": "round.ticks", "op": ">", "value": 0 } ]
			}
			"""));
	}

	[Fact]
	public void AWinClaimIsRecognisedSoOmniscientRunsCanBeRefused()
	{
		// §6.4: a result obtained by dropping the fog is not a result about this game.
		Assert.True(Scenario.Parse("""
			{ "assert": [ { "metric": "round.outcome", "op": "==", "value": 1 } ] }
			""").ClaimsAWin);

		Assert.False(Scenario.Parse(Minimal).ClaimsAWin);
	}

	// ---- metrics -----------------------------------------------------------

	private static EpisodeMetrics Episode(int seed = 7)
	{
		var metrics = new EpisodeMetrics(seed) { StartTick = 1000, EndTick = 2200 };

		metrics.Event("kill", 1100, Fields(("attacker", 1), ("victim", 2), ("distance", 63.4)));
		metrics.Event("damage", 1100, Fields(("amount", 20), ("remaining", 80)));
		metrics.Event("damage", 1200, Fields(("amount", 30), ("remaining", 50)));

		metrics.Sample("self.health", 1100, 1.0);
		metrics.Sample("self.health", 1200, 0.5);
		metrics.Sample("self.health", 1300, 0.8);
		metrics.Counter("pilot_fallbacks", 3);

		return metrics;
	}

	private static Dictionary<string, double> Fields(params (string Name, double Value)[] entries)
	{
		var fields = new Dictionary<string, double>();
		foreach ((string name, double value) in entries)
		{
			fields[name] = value;
		}

		return fields;
	}

	[Fact]
	public void EventCountsAreCountsAndNotTheLastValue()
	{
		EpisodeMetrics metrics = Episode();

		Assert.True(metrics.TryValue("events.damage.count", out double count, out uint tick));
		Assert.Equal(2d, count);
		Assert.Equal(1200u, tick);

		Assert.True(metrics.TryValue("events.kill.count", out double kills, out _));
		Assert.Equal(1d, kills);
	}

	[Fact]
	public void AnEventKindThatNeverFiredReadsZeroAndATypoFails()
	{
		EpisodeMetrics metrics = Episode();

		// "no units were lost" is a fact about the round.
		Assert.True(metrics.TryValue("events.unit_lost.count", out double lost, out _));
		Assert.Equal(0d, lost);
		Assert.True(metrics.TryValue("events.unit_lost.sum", out double sum, out _));
		Assert.Equal(0d, sum);
		Assert.True(metrics.TryValue("events.unit_lost.tier.max", out double tier, out _));
		Assert.Equal(0d, tier);
		Assert.False(metrics.TryValue("events.unit_lost.teir.max", out _, out _));

		// A name nothing answers to is a typo, and a typo that read as zero would be
		// an assertion that passes for the wrong reason.
		Assert.False(metrics.TryValue("events.kil.count", out _, out _));
		Assert.False(metrics.TryValue("observation.self.helth", out _, out _));
		Assert.False(metrics.TryValue("counters.nothing", out _, out _));
	}

	[Fact]
	public void AFilterCountsAndAggregatesOnlyTheEventsThatMatch()
	{
		var metrics = new EpisodeMetrics(7) { StartTick = 0, EndTick = 3000 };
		metrics.Event("node_captured", 600, Fields(("node", 0), ("owner", 2), ("claimant", 0), ("x", -30), ("z", -15)));
		metrics.Event("node_captured", 1200, Fields(("node", 0), ("owner", 1), ("claimant", 2), ("x", -30), ("z", -15)));
		metrics.Event("node_captured", 1800, Fields(("node", 2), ("owner", 1), ("claimant", 0), ("x", -25), ("z", -100)));
		metrics.Event("unit_lost", 900, Fields(("unit", 3), ("tier", 0), ("killer", 5)));
		metrics.Event("unit_lost", 950, Fields(("unit", 4), ("tier", 2), ("killer", 5)));

		// A strategist node the ground force took: both conditions, one event.
		Assert.True(metrics.TryValue("events.node_captured[owner=1,claimant=2].count", out double taken, out uint tick));
		Assert.Equal(1d, taken);
		Assert.Equal(1200u, tick);

		Assert.True(metrics.TryValue("events.node_captured[owner=1].count", out double ground, out _));
		Assert.Equal(2d, ground);
		Assert.True(metrics.TryValue("events.node_captured[owner=1].node.max", out double node, out _));
		Assert.Equal(2d, node);

		// The shorthand is the kind's own field, as it is without a filter.
		Assert.True(metrics.TryValue("events.unit_lost[tier>=1].max", out double tier, out _));
		Assert.Equal(2d, tier);
		Assert.True(metrics.TryValue("events.unit_lost[tier!=2].count", out double soft, out _));
		Assert.Equal(1d, soft);
		Assert.True(metrics.TryValue("events.unit_lost[tier<1].unit", out double unit, out _));
		Assert.Equal(3d, unit);
	}

	[Fact]
	public void ContestedTimeIsTheSumOfTheContestsThatEnded()
	{
		// docs/HTN_BOTS.md §6, H6: "strategist nodes contested for N ticks" is a sum
		// over node_contested's ends, which carry how long each lasted.
		var metrics = new EpisodeMetrics(7) { StartTick = 0, EndTick = 3000 };
		metrics.Event("node_contested", 600, Fields(("node", 2), ("owner", 2), ("contested", 1), ("ticks", 0)));
		metrics.Event("node_contested", 900, Fields(("node", 2), ("owner", 2), ("contested", 0), ("ticks", 300)));
		metrics.Event("node_contested", 1200, Fields(("node", 0), ("owner", 0), ("contested", 1), ("ticks", 0)));
		metrics.Event("node_contested", 1260, Fields(("node", 0), ("owner", 0), ("contested", 0), ("ticks", 60)));

		Assert.True(metrics.TryValue("events.node_contested[owner=2,contested=0].sum", out double held, out _));
		Assert.Equal(300d, held);
		Assert.True(metrics.TryValue("events.node_contested[contested=1].count", out double contests, out _));
		Assert.Equal(2d, contests);
		Assert.Equal("ticks", EpisodeMetrics.DefaultFieldOf("node_contested"));
	}

	[Fact]
	public void AFilterNothingMatchesReadsZeroAndAFilterTypoFails()
	{
		var metrics = new EpisodeMetrics(7) { StartTick = 0, EndTick = 3000 };
		metrics.Event("unit_lost", 900, Fields(("unit", 3), ("tier", 0), ("killer", 5)));

		// No tank died: a fact about the round, as a kind that never fired is.
		Assert.True(metrics.TryValue("events.unit_lost[tier=2].count", out double tanks, out _));
		Assert.Equal(0d, tanks);
		Assert.True(metrics.TryValue("events.squad_task[goal=1].count", out double retreats, out _));
		Assert.Equal(0d, retreats);
		Assert.True(metrics.TryValue("events.squad_task[goal=1].x.min", out double x, out _));
		Assert.Equal(0d, x);

		// A field, a kind or an operator nothing answers to is a typo.
		Assert.False(metrics.TryValue("events.unit_lost[teir=2].count", out _, out _));
		Assert.False(metrics.TryValue("events.unit_lst[tier=2].count", out _, out _));
		Assert.False(metrics.TryValue("events.unit_lost[tier~2].count", out _, out _));
		Assert.False(metrics.TryValue("events.unit_lost[tier=two].count", out _, out _));
		Assert.False(metrics.TryValue("events.unit_lost[tier=2].helth.max", out _, out _));
		Assert.False(metrics.TryValue("events.unit_lost[tier=2].tier.median", out _, out _));
		Assert.False(metrics.TryValue("events.unit_lost[tier=2]", out _, out _));
		Assert.False(metrics.TryValue("events.unit_lost[tier=2.count", out _, out _));

		// A kind with no shorthand needs its field named.
		Assert.False(metrics.TryValue("events.seat_attached[policy=0].max", out _, out _));
	}

	[Fact]
	public void AConditionCanNameASeatByItsId()
	{
		// The armour scenario's two lanes (docs/LOADOUT.md §9): which seat a hit was on
		// is a peer id the server chose at attach, so a filter names the seat instead.
		var metrics = new EpisodeMetrics(6) { StartTick = 0, EndTick = 1800 };
		metrics.Seat("bare", 2147483646);
		metrics.Seat("heavy", 2147483645);
		metrics.Event("damage", 300, Fields(("victim", 2147483646), ("amount", 20), ("absorbed", 0)));
		metrics.Event("damage", 310, Fields(("victim", 2147483645), ("amount", 7), ("absorbed", 13)));
		metrics.Event("damage", 320, Fields(("victim", 2147483645), ("amount", 7), ("absorbed", 13)));

		Assert.True(metrics.TryValue("events.damage[victim=@bare].count", out double bare, out _));
		Assert.Equal(1d, bare);
		Assert.True(metrics.TryValue("events.damage[victim=@heavy].count", out double heavy, out uint tick));
		Assert.Equal(2d, heavy);
		Assert.Equal(320u, tick);
		Assert.True(metrics.TryValue("events.damage[victim=@heavy].absorbed.sum", out double absorbed, out _));
		Assert.Equal(26d, absorbed);
		Assert.True(metrics.TryValue("events.damage[victim!=@heavy].absorbed.max", out double none, out _));
		Assert.Equal(0d, none);

		// A seat the scenario does not have is a typo, as a field is.
		Assert.False(metrics.TryValue("events.damage[victim=@medium].count", out _, out _));
		Assert.False(metrics.TryValue("events.damage[victim=@].count", out _, out _));
	}

	[Fact]
	public void AFilteredClaimParsesAndEvaluatesAcrossTheSweep()
	{
		Scenario scenario = Scenario.Parse("""
			{ "assert": [ { "metric": "events.node_captured[owner=1,claimant=2].count", "op": "over_seeds",
			                "cmp": ">=", "value": 1, "fraction": 0.5 } ] }
			""");

		var taken = new EpisodeMetrics(1) { StartTick = 0, EndTick = 100 };
		taken.Event("node_captured", 50, Fields(("node", 1), ("owner", 1), ("claimant", 2)));
		var held = new EpisodeMetrics(2) { StartTick = 0, EndTick = 100 };
		held.Event("node_captured", 50, Fields(("node", 1), ("owner", 2), ("claimant", 0)));

		AssertionResult result = AssertionEvaluator.Evaluate(scenario.Assertions[0],
			new List<EpisodeMetrics> { taken, held });
		Assert.True(result.Passed, result.Observed);
		Assert.Null(result.Error);
	}

	[Fact]
	public void TheShorthandAggregatesTheKindsOwnField()
	{
		EpisodeMetrics metrics = Episode();

		// events.damage.sum is events.damage.amount.sum — what the design's own
		// example writes.
		Assert.True(metrics.TryValue("events.damage.sum", out double shorthand, out _));
		Assert.True(metrics.TryValue("events.damage.amount.sum", out double full, out _));
		Assert.Equal(50d, shorthand);
		Assert.Equal(shorthand, full);

		Assert.Equal("amount", EpisodeMetrics.DefaultFieldOf("damage"));
		Assert.Null(EpisodeMetrics.DefaultFieldOf("seat_attached"));
	}

	[Fact]
	public void ObservationsAggregateAndCarryTheTickTheyMovedOn()
	{
		EpisodeMetrics metrics = Episode();

		Assert.True(metrics.TryValue("observation.self.health", out double last, out uint lastTick));
		Assert.Equal(0.8d, last, 4);
		Assert.Equal(1300u, lastTick);

		Assert.True(metrics.TryValue("observation.self.health.min", out double least, out uint leastTick));
		Assert.Equal(0.5d, least, 4);
		Assert.Equal(1200u, leastTick);

		Assert.True(metrics.TryValue("observation.self.health.max", out double most, out _));
		Assert.Equal(1.0d, most, 4);

		Assert.True(metrics.TryValue("observation.self.health.mean", out double mean, out _));
		Assert.Equal(2.3d / 3d, mean, 4);
	}

	[Fact]
	public void TheRoundsOwnMetricsComeOffTheEpisodeBoundaries()
	{
		EpisodeMetrics metrics = Episode();

		Assert.True(metrics.TryValue("round.ticks", out double ticks, out _));
		Assert.Equal(1200d, ticks);

		Assert.True(metrics.TryValue("counters.pilot_fallbacks", out double fallbacks, out _));
		Assert.Equal(3d, fallbacks);
	}

	// ---- the vocabulary ----------------------------------------------------

	private static AssertionResult Check(Assertion assertion, params EpisodeMetrics[] episodes) =>
		AssertionEvaluator.Evaluate(assertion, episodes);

	private static EpisodeMetrics WithKills(int seed, int kills)
	{
		var metrics = new EpisodeMetrics(seed) { StartTick = 0, EndTick = 600 };
		for (int i = 0; i < kills; i++)
		{
			metrics.Event("kill", (uint)(100 + i), Fields(("distance", 10d * (i + 1))));
		}

		return metrics;
	}

	[Fact]
	public void AScalarClaimHasToHoldInEveryEpisodeOfTheSweep()
	{
		var assertion = new Assertion
		{
			Metric = "events.kill.count",
			Op = AssertionOp.GreaterOrEqual,
			Value = 1d,
		};

		Assert.True(Check(assertion, WithKills(1, 1), WithKills(2, 4)).Passed);

		AssertionResult failed = Check(assertion, WithKills(1, 3), WithKills(2, 0));
		Assert.False(failed.Passed);
		Assert.Equal(2, failed.Seed);
	}

	[Fact]
	public void BetweenIsAClosedIntervalAndNamesTheTickItFailedOn()
	{
		var assertion = new Assertion
		{
			Metric = "events.kill.count",
			Op = AssertionOp.Between,
			Range = new[] { 1d, 3d },
		};

		Assert.True(Check(assertion, WithKills(1, 1)).Passed);
		Assert.True(Check(assertion, WithKills(1, 3)).Passed);

		AssertionResult failed = Check(assertion, WithKills(9, 5));
		Assert.False(failed.Passed);
		Assert.Equal(9, failed.Seed);
		Assert.Equal(104u, failed.Tick);
		Assert.Equal("5", failed.Observed);
	}

	[Fact]
	public void PercentileIsTheSweepsOrderStatisticWithLinearInterpolation()
	{
		// The same definition NumPy's default uses, so a claim written next to a
		// Python analysis means the same thing in both.
		var values = new[] { 1d, 2d, 3d, 4d };
		Assert.Equal(2.5d, AssertionEvaluator.Percentile(values, 50d), 6);
		Assert.Equal(1d, AssertionEvaluator.Percentile(values, 0d), 6);
		Assert.Equal(4d, AssertionEvaluator.Percentile(values, 100d), 6);

		var assertion = new Assertion
		{
			Metric = "events.kill.count",
			Op = AssertionOp.Percentile,
			Percentile = 50d,
			Comparison = AssertionOp.GreaterOrEqual,
			Value = 2d,
		};

		Assert.True(Check(assertion, WithKills(1, 0), WithKills(2, 4), WithKills(3, 5)).Passed);
		Assert.False(Check(assertion, WithKills(1, 0), WithKills(2, 1), WithKills(3, 5)).Passed);
	}

	[Fact]
	public void OverSeedsRelaxesEveryEpisodeToAFractionOfThem()
	{
		var assertion = new Assertion
		{
			Metric = "events.kill.count",
			Op = AssertionOp.OverSeeds,
			Comparison = AssertionOp.GreaterOrEqual,
			Value = 1d,
			Fraction = 0.6d,
		};

		AssertionResult passed = Check(assertion, WithKills(1, 1), WithKills(2, 0), WithKills(3, 2));
		Assert.True(passed.Passed);
		Assert.Equal("2/3 seeds", passed.Observed);

		Assert.False(Check(assertion, WithKills(1, 1), WithKills(2, 0), WithKills(3, 0)).Passed);
	}

	[Fact]
	public void AWholeSweepHasToHoldWhenTheFractionIsOne()
	{
		var assertion = new Assertion
		{
			Metric = "events.kill.count",
			Op = AssertionOp.OverSeeds,
			Comparison = AssertionOp.Greater,
			Value = 0d,
			Fraction = 1d,
		};

		Assert.True(Check(assertion, WithKills(1, 1), WithKills(2, 1)).Passed);
		Assert.False(Check(assertion, WithKills(1, 1), WithKills(2, 0)).Passed);
	}

	[Fact]
	public void EqualityOnAFloatIsATolerance()
	{
		var assertion = new Assertion
		{
			Metric = "observation.self.health",
			Op = AssertionOp.Equal,
			Value = 1d,
			Tolerance = 1e-3d,
		};

		var metrics = new EpisodeMetrics(1) { EndTick = 10 };
		metrics.Sample("self.health", 10, 0.99995d);

		Assert.True(Check(assertion, metrics).Passed);
	}

	[Fact]
	public void AClaimAboutAMetricNothingAnswersToFailsWithAnExplanation()
	{
		var assertion = new Assertion { Metric = "observation.nope", Op = AssertionOp.Greater, Value = 0d };
		AssertionResult result = Check(assertion, WithKills(1, 1));

		Assert.False(result.Passed);
		Assert.Contains("no metric called", result.Error);
	}

	[Fact]
	public void AClaimWithNoEpisodesFailsRatherThanPassingVacuously()
	{
		AssertionResult result = AssertionEvaluator.Evaluate(
			new Assertion { Metric = "round.ticks", Op = AssertionOp.Greater, Value = 0d },
			Array.Empty<EpisodeMetrics>());

		Assert.False(result.Passed);
		Assert.Equal("no episodes ran", result.Error);
	}

	[Fact]
	public void AClaimPrintsAsSomethingAPersonCanRead()
	{
		Assert.Equal("events.kill.count >= 1", new Assertion
		{
			Metric = "events.kill.count",
			Op = AssertionOp.GreaterOrEqual,
			Value = 1d,
		}.ToString());

		Assert.Equal("events.damage.sum in [100,400]", new Assertion
		{
			Metric = "events.damage.sum",
			Op = AssertionOp.Between,
			Range = new[] { 100d, 400d },
		}.ToString());

		Assert.Equal("round.ticks >= 1 over 80% of seeds", new Assertion
		{
			Metric = "round.ticks",
			Op = AssertionOp.OverSeeds,
			Comparison = AssertionOp.GreaterOrEqual,
			Value = 1d,
			Fraction = 0.8d,
		}.ToString());
	}

	// ---- a seat's kit (docs/LOADOUT.md §9) ----------------------------------------

	private static Scenario WithKit(string kit) => Scenario.Parse($$"""
		{
		  "name": "a kit",
		  "seed": 1,
		  "seats": [ { "id": "rifleman", "team": "ground", "policy": "ground", "kit": {{kit}} } ],
		  "assert": [ { "metric": "events.damage.count", "op": ">=", "value": 0 } ]
		}
		""");

	[Fact]
	public void ASeatsKitIsReadWithTheDefaultKitForWhatItLeavesOut()
	{
		Gdpyr.AgentClient.GdpyrKit kit = WithKit("""{ "magazines": 0, "grenades": 0 }""").Seats[0].Kit;
		Assert.Equal("rifle", kit.Primary);
		Assert.Equal(0, kit.Magazines);
		Assert.Equal(0, kit.Grenades);
		Assert.Equal("none", kit.Armour);

		kit = WithKit("""{ "primary": "none", "magazines": 0, "grenades": 3, "armour": "heavy" }""").Seats[0].Kit;
		Assert.Equal(("none", 3, "heavy"), (kit.Primary, kit.Grenades, kit.Armour));

		// A seat that names no kit keeps its own: nothing is sent.
		Scenario plain = Scenario.Parse("""
			{
			  "name": "no kit",
			  "seed": 1,
			  "seats": [ { "id": "rifleman", "team": "ground", "policy": "ground" } ],
			  "assert": [ { "metric": "events.damage.count", "op": ">=", "value": 0 } ]
			}
			""");
		Assert.Null(plain.Seats[0].Kit);
	}

	[Theory]
	[InlineData("""{ "primary": "shotgun" }""", "primary")]
	[InlineData("""{ "armour": "plate" }""", "armour")]
	[InlineData("""{ "grenades": 4 }""", "pouches")]
	[InlineData("""{ "magazines": 7, "grenades": 3 }""", "pouches")]
	[InlineData("""{ "magazines": -1 }""", "pouches")]
	[InlineData("""{ "primary": "none", "magazines": 1, "grenades": 0 }""", "no primary")]
	[InlineData("""{ "magazines": "lots" }""", "wrong type")]
	[InlineData("""{ "helmet": true }""", "helmet")]
	[InlineData("""[1, 2]""", "not an object")]
	public void AKitThatBreaksARuleFailsAtParseTime(string kit, string says)
	{
		ScenarioException error = Assert.Throws<ScenarioException>(() => WithKit(kit));
		Assert.Contains(says, error.Message);
	}

	[Fact]
	public void AStrategistCarriesNoKit()
	{
		ScenarioException error = Assert.Throws<ScenarioException>(() => Scenario.Parse("""
			{
			  "name": "a kit",
			  "seed": 1,
			  "seats": [ { "id": "s", "team": "strategist", "policy": "strategist", "kit": {} } ],
			  "assert": [ { "metric": "events.damage.count", "op": ">=", "value": 0 } ]
			}
			"""));
		Assert.Contains("strategist", error.Message);
	}

	[Fact]
	public void TheHarnessChecksAKitAgainstTheGamesOwnLimits()
	{
		Assert.Equal(Gdpyr.Sim.Loadout.PrimaryNames, ScenarioKit.Primaries);
		Assert.Equal(Gdpyr.Sim.Loadout.ArmourNames, ScenarioKit.Armours);
		Assert.Equal(Gdpyr.Sim.Loadout.PouchSlots, ScenarioKit.PouchSlots);
		Assert.Equal(Gdpyr.Sim.SimConfig.MaxGrenades, ScenarioKit.MaxGrenades);

		// The client's default kit is the game's.
		var kit = new Gdpyr.AgentClient.GdpyrKit();
		Assert.Equal(Gdpyr.Sim.Loadout.DefaultMagazines, kit.Magazines);
		Assert.Equal(Gdpyr.Sim.Loadout.DefaultGrenades, kit.Grenades);
	}

	// ---- a script step's loadout (docs/LOADOUT.md §9) ------------------------------

	private static Scenario WithLoadout(string loadout, string policy = "ground", string then = """{ "ticks": 8, "buttons": ["fire"] }""") =>
		Scenario.Parse($$"""
		{
		  "name": "a loadout",
		  "seed": 1,
		  "seats": [ { "id": "seat", "team": "{{policy}}", "policy": "{{policy}}",
		    "script": [ { "ticks": 8, "loadout": {{loadout}} }, {{then}} ] } ],
		  "assert": [ { "metric": "events.damage.count", "op": ">=", "value": 0 } ]
		}
		""");

	[Fact]
	public void AStepsLoadoutIsReadWithTheLockerForWhenItLeavesOut()
	{
		ScriptStep step = WithLoadout("""{ "kit": { "primary": "launcher", "magazines": 3, "grenades": 0 } }""")
			.Seats[0].Script.Steps[0];

		Assert.Equal("launcher", step.Loadout.Primary);
		Assert.Equal(3, step.Loadout.Magazines);
		Assert.Equal(0, step.Loadout.Grenades);
		Assert.Equal("none", step.Loadout.Armour);
		Assert.Equal("locker", step.LoadoutWhen);

		// A step with only a kit is not input: the seat holds still while it asks.
		Assert.False(step.HasInput);

		step = WithLoadout("""{ "kit": { "primary": "none", "magazines": 0 }, "when": "spawn" }""").Seats[0].Script.Steps[0];
		Assert.Equal("spawn", step.LoadoutWhen);
	}

	[Fact]
	public void AStepWithNoLoadoutAsksForNothing()
	{
		ScriptStep step = WithLoadout("""{ "kit": {} }""").Seats[0].Script.Steps[1];
		Assert.Null(step.Loadout);
	}

	[Theory]
	[InlineData("""{ "kit": { "primary": "shotgun" } }""", "primary")]
	[InlineData("""{ "kit": { "magazines": 7, "grenades": 3 } }""", "pouches")]
	[InlineData("""{ "kit": {}, "when": "later" }""", "when")]
	[InlineData("""{ "kit": {}, "at": "locker" }""", "'at'")]
	[InlineData("""{ "primary": "launcher" }""", "'kit'")]
	[InlineData("""[1]""", "'kit'")]
	public void ALoadoutThatIsNotOneFailsAtParseTime(string loadout, string says)
	{
		ScenarioException error = Assert.Throws<ScenarioException>(() => WithLoadout(loadout));
		Assert.Contains(says, error.Message);
	}

	[Fact]
	public void AStrategistsStepCarriesNoLoadout()
	{
		// Its other step is a strategist's own, so the kit is the only thing wrong.
		ScenarioException error = Assert.Throws<ScenarioException>(() =>
			WithLoadout("""{ "kit": {} }""", policy: "strategist", then: """{ "ticks": 8, "commands": [] }"""));
		Assert.Contains("loadout", error.Message);

		Scenario fine = WithLoadout("""{ "kit": {} }""", then: """{ "ticks": 8 }""");
		Assert.NotNull(fine.Seats[0].Script.Steps[0].Loadout);
	}

	[Fact]
	public void TheHarnessCountsEveryRefusalTheGameCanAnswer()
	{
		// counters.loadouts_refused.<code> exists for each of these, so a scenario that
		// claims one names a code the server can actually send (docs/LOADOUT.md §6.2).
		var codes = new List<string>();
		for (byte value = 1; Gdpyr.Sim.KitRequest.TryRead(value, out Gdpyr.Sim.KitRefusal refusal); value++)
		{
			codes.Add(Gdpyr.Sim.KitRequest.CodeOf(refusal));
		}

		Assert.Equal(codes, ScenarioKit.Refusals);
		Assert.Equal(new[] { "locker", "spawn" }, ScenarioKit.Whens);
	}
}
