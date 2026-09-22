using System;
using System.Collections.Generic;

namespace Gdpyr.Playtest;

/// <summary>
/// What one episode produced, in the vocabulary an assertion names
/// (docs/AGENT_API.md §9.1).
///
/// Two streams go in — the events the game emitted, and the observation fields the
/// harness sampled — and named metrics come out: <c>events.kill.count</c>,
/// <c>events.damage.sum</c>, <c>observation.self.health</c>. Both are recorded with
/// the tick they happened on, because a failed assertion that does not say *when*
/// is a failed assertion somebody has to reproduce by hand.
///
/// Deliberately free of both Godot and the protocol client, so the metric
/// vocabulary is answered by <c>dotnet test</c> with no server and no engine.
/// </summary>
public sealed class EpisodeMetrics
{
	/// <summary>An accumulator over one numeric stream.</summary>
	private sealed class Series
	{
		public int Count;
		public double Sum;
		public double Min = double.PositiveInfinity;
		public double Max = double.NegativeInfinity;
		public double Last;

		/// <summary>The tick each of those last moved on, so a failure can name one.</summary>
		public uint LastTick;

		public uint MinTick;
		public uint MaxTick;

		public void Add(double value, uint tick)
		{
			Count++;
			Sum += value;
			Last = value;
			LastTick = tick;

			if (value < Min)
			{
				Min = value;
				MinTick = tick;
			}

			if (value > Max)
			{
				Max = value;
				MaxTick = tick;
			}
		}
	}

	private readonly Dictionary<string, Series> _series = new(StringComparer.Ordinal);
	private readonly Dictionary<string, int> _counters = new(StringComparer.Ordinal);

	/// <summary>How many of each event kind arrived, and when the last one did.</summary>
	private readonly Dictionary<string, (int Count, uint LastTick)> _events = new(StringComparer.Ordinal);

	public EpisodeMetrics(int seed) => Seed = seed;

	/// <summary>The seed this episode was labelled with. It does not determine it (docs/AGENT_API.md §3.1).</summary>
	public int Seed { get; }

	/// <summary>The tick the episode was started on.</summary>
	public uint StartTick { get; set; }

	/// <summary>The tick it stopped on.</summary>
	public uint EndTick { get; set; }

	/// <summary>True when the server was running with <c>--agent-omniscient</c>.</summary>
	public bool Omniscient { get; set; }

	/// <summary>True when the server was running with <c>--agent-unbounded</c>.</summary>
	public bool Unbounded { get; set; }

	/// <summary>Every metric name this episode can answer. For a helpful error on a typo.</summary>
	public IEnumerable<string> Names
	{
		get
		{
			foreach (string name in _events.Keys)
			{
				yield return $"events.{name}.count";
			}

			foreach (string name in _series.Keys)
			{
				yield return name;
			}

			foreach (string name in _counters.Keys)
			{
				yield return $"counters.{name}";
			}
		}
	}

	/// <summary>
	/// The field an <c>events.&lt;kind&gt;.&lt;agg&gt;</c> shorthand aggregates, per
	/// event kind.
	///
	/// The shorthand exists because the design's own example writes
	/// <c>events.damage.sum</c> rather than <c>events.damage.amount.sum</c>, and one
	/// documented table is better than either a magic guess or an example that does
	/// not work. Anything not in this table has to be named in full.
	/// </summary>
	public static string DefaultFieldOf(string kind) => kind switch
	{
		"damage" => "amount",
		"kill" => "distance",
		"unit_built" or "unit_lost" => "tier",
		"node_captured" => "owner",
		"round_end" => "outcome",
		"round_start" => "seed",
		_ => null,
	};

	/// <summary>Records one event off the stream (docs/AGENT_API.md §8).</summary>
	public void Event(string kind, uint tick, IReadOnlyDictionary<string, double> fields)
	{
		if (string.IsNullOrEmpty(kind))
		{
			return;
		}

		_events.TryGetValue(kind, out (int Count, uint LastTick) seen);
		_events[kind] = (seen.Count + 1, tick);

		if (fields == null)
		{
			return;
		}

		foreach (KeyValuePair<string, double> field in fields)
		{
			Track($"events.{kind}.{field.Key}").Add(field.Value, tick);
		}
	}

	/// <summary>Records one observation field, by the name the schema publishes.</summary>
	public void Sample(string field, uint tick, double value) =>
		Track($"observation.{field}").Add(value, tick);

	/// <summary>Records a harness counter: pilot fallbacks, rejected orders, and the like.</summary>
	public void Counter(string name, int value) => _counters[name] = value;

	/// <summary>
	/// Resolves a metric name to a number and the tick it last moved on. False when
	/// nothing in this episode answers to that name — which is a failed assertion
	/// rather than a zero, because a typo that silently reads as zero is an
	/// assertion that passes for the wrong reason.
	/// </summary>
	public bool TryValue(string metric, out double value, out uint tick)
	{
		value = 0d;
		tick = EndTick;

		if (string.IsNullOrWhiteSpace(metric))
		{
			return false;
		}

		switch (metric)
		{
			case "round.ticks":
				value = EndTick >= StartTick ? EndTick - StartTick : 0d;
				return true;

			case "round.seconds":
				value = (EndTick >= StartTick ? EndTick - StartTick : 0d) / 60d;
				return true;
		}

		// events.<kind>.count is the number of events, not the last one's value, so it
		// is answered before anything reaches the series table.
		if (metric.StartsWith("events.", StringComparison.Ordinal)
			&& metric.EndsWith(".count", StringComparison.Ordinal))
		{
			string counted = metric[7..^6];
			if (_events.TryGetValue(counted, out (int Count, uint LastTick) seen))
			{
				value = seen.Count;
				tick = seen.LastTick;
				return true;
			}

			// An event kind that never fired has a count, and it is zero. A name that
			// is not an event kind falls through to the general path below, where
			// events.kill.distance.count is still the number of samples.
			if (AgentEventKinds.Contains(counted))
			{
				value = 0d;
				return true;
			}
		}

		if (metric.StartsWith("counters.", StringComparison.Ordinal))
		{
			if (!_counters.TryGetValue(metric[9..], out int counter))
			{
				return false;
			}

			value = counter;
			return true;
		}

		// A bare name is the stream's last value; a trailing aggregate names one of
		// the five the vocabulary has.
		if (TryRead(metric, "last", out value, out tick))
		{
			return true;
		}

		int dot = metric.LastIndexOf('.');
		if (dot <= 0)
		{
			return false;
		}

		string aggregate = metric[(dot + 1)..];
		string name = metric[..dot];

		if (!IsAggregate(aggregate))
		{
			return false;
		}

		if (TryRead(name, aggregate, out value, out tick))
		{
			return true;
		}

		// events.<kind>.<agg> is shorthand for the kind's own field.
		if (!name.StartsWith("events.", StringComparison.Ordinal))
		{
			return false;
		}

		string kind = name[7..];
		string field = DefaultFieldOf(kind);
		if (field != null && TryRead($"events.{kind}.{field}", aggregate, out value, out tick))
		{
			return true;
		}

		// An event kind that never fired reads zero: "no kills this round" is a fact
		// about the round, not a typo. An observation field that answers to nothing
		// still fails, because that one *is* a typo — the harness samples every field
		// the schema publishes, so a name it does not know is a name that does not
		// exist.
		value = 0d;
		tick = EndTick;
		return AgentEventKinds.Contains(kind);
	}

	private static bool IsAggregate(string name) =>
		name is "count" or "sum" or "mean" or "min" or "max" or "last";

	/// <summary>
	/// Every event kind the game emits (docs/AGENT_API.md §8), so that
	/// <c>events.kill.count</c> is zero in a round with no kills and a typo is
	/// still a failure rather than a silent zero.
	/// </summary>
	public static readonly HashSet<string> AgentEventKinds = new(StringComparer.Ordinal)
	{
		"round_start", "round_end", "kill", "damage", "unit_built", "unit_lost", "node_captured",
		"seat_attached", "seat_released", "structure_placed", "structure_built", "structure_lost",
	};

	private bool TryRead(string name, string aggregate, out double value, out uint tick)
	{
		value = 0d;
		tick = EndTick;

		if (!_series.TryGetValue(name, out Series series) || series.Count == 0)
		{
			return false;
		}

		switch (aggregate)
		{
			case "count":
				value = series.Count;
				tick = series.LastTick;
				return true;

			case "sum":
				value = series.Sum;
				tick = series.LastTick;
				return true;

			case "mean":
				value = series.Sum / series.Count;
				tick = series.LastTick;
				return true;

			case "min":
				value = series.Min;
				tick = series.MinTick;
				return true;

			case "max":
				value = series.Max;
				tick = series.MaxTick;
				return true;

			default:
				value = series.Last;
				tick = series.LastTick;
				return true;
		}
	}

	private Series Track(string name)
	{
		if (!_series.TryGetValue(name, out Series found))
		{
			found = new Series();
			_series[name] = found;
		}

		return found;
	}
}
