using System;
using System.Collections.Generic;
using System.Globalization;

namespace Gdpyr.Playtest;

/// <summary>
/// What an assertion may claim (docs/AGENT_API.md §3, §9.2).
///
/// The vocabulary is **distributional on purpose**. This is a stochastic
/// environment with a seeded core and an engine that is not reproducible tick for
/// tick (§3.1), so a suite that asserts trajectories is a suite that flakes.
/// There is deliberately no <c>assert_position_equals</c>, and there is no
/// operator that names a tick.
/// </summary>
public enum AssertionOp
{
	Equal,
	NotEqual,
	Less,
	LessOrEqual,
	Greater,
	GreaterOrEqual,

	/// <summary>Inside a closed interval, in every episode.</summary>
	Between,

	/// <summary>The p-th percentile across the seed sweep satisfies a comparison.</summary>
	Percentile,

	/// <summary>The comparison holds in at least a fraction of the seed sweep.</summary>
	OverSeeds,
}

/// <summary>One claim a scenario makes.</summary>
public sealed class Assertion
{
	/// <summary>The metric name: <c>events.kill.count</c>, <c>observation.self.health</c>, <c>round.ticks</c>.</summary>
	public string Metric = string.Empty;

	public AssertionOp Op = AssertionOp.GreaterOrEqual;

	/// <summary>The right-hand side for a scalar comparison.</summary>
	public double Value;

	/// <summary>The closed interval, for <see cref="AssertionOp.Between"/>.</summary>
	public double[] Range;

	/// <summary>The comparison the distributional operators apply.</summary>
	public AssertionOp Comparison = AssertionOp.GreaterOrEqual;

	/// <summary>Which percentile, in [0,100], for <see cref="AssertionOp.Percentile"/>.</summary>
	public double Percentile = 50d;

	/// <summary>How much of the sweep has to hold, in (0,1], for <see cref="AssertionOp.OverSeeds"/>.</summary>
	public double Fraction = 1d;

	/// <summary>Tolerance for <see cref="AssertionOp.Equal"/>. Floats, not integers, come out of an observation.</summary>
	public double Tolerance = 1e-4d;

	/// <summary>
	/// Whether this claim is about who won.
	///
	/// A win obtained under <c>--agent-omniscient</c> is not a result about this
	/// game, so the harness fails any scenario that makes one under it
	/// (docs/AGENT_API.md §6.4).
	/// </summary>
	public bool IsWinClaim =>
		Metric is "round.outcome" || Metric.StartsWith("events.round_end.outcome", StringComparison.Ordinal)
		|| Metric.StartsWith("events.round_end.count", StringComparison.Ordinal);

	public override string ToString() => Op switch
	{
		AssertionOp.Between => $"{Metric} in [{Format(Range is { Length: > 0 } ? Range[0] : 0d)},"
			+ $"{Format(Range is { Length: > 1 } ? Range[1] : 0d)}]",
		AssertionOp.Percentile => $"p{Format(Percentile)}({Metric}) {Symbol(Comparison)} {Format(Value)}",
		AssertionOp.OverSeeds => $"{Metric} {Symbol(Comparison)} {Format(Value)}"
			+ $" over {Format(Fraction * 100d)}% of seeds",
		_ => $"{Metric} {Symbol(Op)} {Format(Value)}",
	};

	internal static string Symbol(AssertionOp op) => op switch
	{
		AssertionOp.Equal => "==",
		AssertionOp.NotEqual => "!=",
		AssertionOp.Less => "<",
		AssertionOp.LessOrEqual => "<=",
		AssertionOp.Greater => ">",
		AssertionOp.GreaterOrEqual => ">=",
		AssertionOp.Between => "between",
		AssertionOp.Percentile => "percentile",
		_ => "over_seeds",
	};

	internal static string Format(double value) =>
		value == Math.Floor(value) && Math.Abs(value) < 1e9
			? ((long)value).ToString(CultureInfo.InvariantCulture)
			: value.ToString("0.###", CultureInfo.InvariantCulture);
}

/// <summary>Whether one claim held, and what the run actually produced.</summary>
public sealed class AssertionResult
{
	public Assertion Assertion;
	public bool Passed;

	/// <summary>What the metric came to: one number, or the sweep's summary.</summary>
	public string Observed = string.Empty;

	/// <summary>The tick the failing episode's metric last moved on. 0 when there is no one tick.</summary>
	public uint Tick;

	/// <summary>The seed of the episode that failed, when one episode is to blame.</summary>
	public int Seed;

	/// <summary>Set when the claim could not be evaluated at all — a metric nothing answers to.</summary>
	public string Error;
}

/// <summary>
/// Evaluates a scenario's claims against the episodes it ran
/// (docs/AGENT_API.md §9.1).
///
/// The rule that decides what "over 20 seeds" means is stated once, here: a
/// scalar comparison and <c>between</c> are **per-episode invariants** and have to
/// hold in every episode of the sweep, while <c>percentile</c> and
/// <c>over_seeds</c> are the two operators that talk about the distribution. That
/// split is what makes "the round ends within 8–20 minutes over 20 seeds" the
/// natural thing to write and "the round ended on tick 51,204" impossible to.
/// </summary>
public static class AssertionEvaluator
{
	public static AssertionResult Evaluate(Assertion assertion, IReadOnlyList<EpisodeMetrics> episodes)
	{
		var result = new AssertionResult { Assertion = assertion, Passed = true };

		if (episodes == null || episodes.Count == 0)
		{
			result.Passed = false;
			result.Error = "no episodes ran";
			return result;
		}

		var values = new double[episodes.Count];
		var ticks = new uint[episodes.Count];

		for (int i = 0; i < episodes.Count; i++)
		{
			if (!episodes[i].TryValue(assertion.Metric, out values[i], out ticks[i]))
			{
				result.Passed = false;
				result.Seed = episodes[i].Seed;
				result.Error = $"no metric called '{assertion.Metric}'";
				return result;
			}
		}

		switch (assertion.Op)
		{
			case AssertionOp.Percentile:
				return Distributional(assertion, result, values, Percentile(values, assertion.Percentile),
					$"p{Assertion.Format(assertion.Percentile)} = ");

			case AssertionOp.OverSeeds:
				return OverSeeds(assertion, result, episodes, values);

			default:
				return EveryEpisode(assertion, result, episodes, values, ticks);
		}
	}

	private static AssertionResult EveryEpisode(Assertion assertion, AssertionResult result,
		IReadOnlyList<EpisodeMetrics> episodes, double[] values, uint[] ticks)
	{
		for (int i = 0; i < values.Length; i++)
		{
			if (Holds(assertion.Op, assertion, values[i]))
			{
				continue;
			}

			result.Passed = false;
			result.Observed = Assertion.Format(values[i]);
			result.Tick = ticks[i];
			result.Seed = episodes[i].Seed;
			return result;
		}

		result.Observed = values.Length == 1
			? Assertion.Format(values[0])
			: $"{Assertion.Format(Min(values))}..{Assertion.Format(Max(values))} over {values.Length} seeds";
		return result;
	}

	private static AssertionResult Distributional(Assertion assertion, AssertionResult result, double[] values,
		double statistic, string label)
	{
		result.Passed = Holds(assertion.Comparison, assertion, statistic);
		result.Observed = $"{label}{Assertion.Format(statistic)} over {values.Length} seeds";
		return result;
	}

	private static AssertionResult OverSeeds(Assertion assertion, AssertionResult result,
		IReadOnlyList<EpisodeMetrics> episodes, double[] values)
	{
		int held = 0;
		for (int i = 0; i < values.Length; i++)
		{
			if (Holds(assertion.Comparison, assertion, values[i]))
			{
				held++;
			}
		}

		double fraction = held / (double)values.Length;
		result.Passed = fraction + 1e-9d >= assertion.Fraction;
		result.Observed = $"{held}/{values.Length} seeds";

		if (!result.Passed && episodes.Count > 0)
		{
			result.Seed = episodes[0].Seed;
		}

		return result;
	}

	/// <summary>
	/// The p-th percentile by linear interpolation between the order statistics —
	/// the same definition NumPy's default uses, so a claim written next to a Python
	/// analysis means the same thing in both.
	/// </summary>
	public static double Percentile(double[] values, double percentile)
	{
		if (values == null || values.Length == 0)
		{
			return 0d;
		}

		var sorted = (double[])values.Clone();
		Array.Sort(sorted);

		double rank = Math.Clamp(percentile, 0d, 100d) / 100d * (sorted.Length - 1);
		int low = (int)Math.Floor(rank);
		int high = (int)Math.Ceiling(rank);

		return low == high ? sorted[low] : sorted[low] + ((sorted[high] - sorted[low]) * (rank - low));
	}

	private static bool Holds(AssertionOp op, Assertion assertion, double value) => op switch
	{
		AssertionOp.Equal => Math.Abs(value - assertion.Value) <= assertion.Tolerance,
		AssertionOp.NotEqual => Math.Abs(value - assertion.Value) > assertion.Tolerance,
		AssertionOp.Less => value < assertion.Value,
		AssertionOp.LessOrEqual => value <= assertion.Value,
		AssertionOp.Greater => value > assertion.Value,
		AssertionOp.GreaterOrEqual => value >= assertion.Value,
		AssertionOp.Between => assertion.Range is { Length: >= 2 }
			&& value >= assertion.Range[0] && value <= assertion.Range[1],

		// A distributional operator is never the comparison inside another one.
		_ => false,
	};

	private static double Min(double[] values)
	{
		double least = values[0];
		for (int i = 1; i < values.Length; i++)
		{
			least = Math.Min(least, values[i]);
		}

		return least;
	}

	private static double Max(double[] values)
	{
		double most = values[0];
		for (int i = 1; i < values.Length; i++)
		{
			most = Math.Max(most, values[i]);
		}

		return most;
	}

	/// <summary>Parses an operator name. Null when the name is not in the vocabulary.</summary>
	public static AssertionOp? ParseOp(string name) => name switch
	{
		"==" or "eq" => AssertionOp.Equal,
		"!=" or "ne" => AssertionOp.NotEqual,
		"<" or "lt" => AssertionOp.Less,
		"<=" or "le" => AssertionOp.LessOrEqual,
		">" or "gt" => AssertionOp.Greater,
		">=" or "ge" => AssertionOp.GreaterOrEqual,
		"between" => AssertionOp.Between,
		"percentile" => AssertionOp.Percentile,
		"over_seeds" => AssertionOp.OverSeeds,
		_ => null,
	};
}
