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

		// A name nothing answers to is a typo, and a typo that read as zero would be
		// an assertion that passes for the wrong reason.
		Assert.False(metrics.TryValue("events.kil.count", out _, out _));
		Assert.False(metrics.TryValue("observation.self.helth", out _, out _));
		Assert.False(metrics.TryValue("counters.nothing", out _, out _));
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
}
