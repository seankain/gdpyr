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

	/// <summary>Every event as it arrived, for a filtered metric (<c>events.unit_lost[tier=2].count</c>).</summary>
	private readonly List<(string Kind, uint Tick, IReadOnlyDictionary<string, double> Fields)> _records = new();

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
		"squad_task" => "goal",
		"contact_spotted" => "observer",
		"node_contested" => "ticks",
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
		_records.Add((kind, tick, fields));

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

		// events.<kind>[<field><op><value>,…].<rest> is the same vocabulary over the
		// events that match every condition.
		if (metric.StartsWith("events.", StringComparison.Ordinal) && metric.IndexOf('[') > 0)
		{
			return TryFiltered(metric, out value, out tick);
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
		// about the round, not a typo — and so does one of its fields, named in full.
		// An observation field that answers to nothing still fails, because that one
		// *is* a typo — the harness samples every field the schema publishes, so a
		// name it does not know is a name that does not exist.
		value = 0d;
		tick = EndTick;
		int split = kind.IndexOf('.');
		if (split > 0 && AgentEventFields.TryGetValue(kind[..split], out string[] fields))
		{
			return Array.IndexOf(fields, kind[(split + 1)..]) >= 0;
		}

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
		"squad_task", "contact_spotted", "node_contested",
	};

	/// <summary>
	/// The fields each kind carries, as the game's event schema names them, so that a
	/// filter or an aggregate over a field the kind does not have is a typo rather
	/// than a zero — which it would otherwise read as when no event matched.
	/// </summary>
	public static readonly Dictionary<string, string[]> AgentEventFields = new(StringComparer.Ordinal)
	{
		["round_start"] = new[] { "seed", "ground_tickets", "strategist_points", "duration_minutes" },
		["round_end"] = new[] { "outcome", "ground_tickets", "units_lost", "duration_minutes", "units_built" },
		["kill"] = new[] { "attacker", "victim", "weapon", "distance" },
		["damage"] = new[] { "attacker", "victim", "weapon", "amount", "remaining" },
		["unit_built"] = new[] { "unit", "tier", "barracks", "x", "z" },
		["unit_lost"] = new[] { "unit", "tier", "killer", "x", "z" },
		["node_captured"] = new[] { "node", "owner", "claimant", "x", "z" },
		["seat_attached"] = new[] { "seat", "policy", "step_mul" },
		["seat_released"] = new[] { "seat", "policy", "reason" },
		["structure_placed"] = new[] { "structure", "type", "builders", "x", "z" },
		["structure_built"] = new[] { "structure", "type", "x", "z" },
		["structure_lost"] = new[] { "structure", "type", "killer", "x", "z" },
		["squad_task"] = new[] { "strategist", "squad", "goal", "x", "z" },
		["contact_spotted"] = new[] { "peer", "observer", "first", "x", "z" },
		["node_contested"] = new[] { "node", "owner", "contested", "ticks" },
	};

	/// <summary>
	/// A metric over the events of one kind that match every condition in the
	/// brackets: <c>events.node_captured[owner=1,claimant=2].count</c> is how many
	/// strategist nodes the ground force took, <c>events.unit_lost[tier=2].count</c>
	/// how many tanks died. A condition is a field, one of <c>=</c> <c>!=</c>
	/// <c>&lt;</c> <c>&lt;=</c> <c>&gt;</c> <c>&gt;=</c>, and a number; what follows
	/// the brackets is what follows a kind anywhere else — <c>count</c>, an
	/// aggregate of the kind's own field, or a field and an aggregate. Nothing
	/// matching reads zero, as a kind that never fired does; a kind, a field or an
	/// operator nothing answers to fails.
	/// </summary>
	private bool TryFiltered(string metric, out double value, out uint tick)
	{
		value = 0d;
		tick = EndTick;

		int open = metric.IndexOf('[');
		int close = metric.IndexOf(']', open + 1);
		if (close < 0 || close + 2 > metric.Length || metric[close + 1] != '.')
		{
			return false;
		}

		string kind = metric[7..open];
		if (!AgentEventFields.TryGetValue(kind, out string[] known))
		{
			return false;
		}

		string[] terms = metric[(open + 1)..close].Split(',');
		var conditions = new (string Field, string Op, double Value)[terms.Length];
		for (int i = 0; i < terms.Length; i++)
		{
			if (!TryCondition(terms[i], known, out conditions[i]))
			{
				return false;
			}
		}

		string rest = metric[(close + 2)..];
		string field;
		string aggregate;
		if (rest == "count")
		{
			field = null;
			aggregate = "count";
		}
		else if (IsAggregate(rest))
		{
			field = DefaultFieldOf(kind);
			aggregate = rest;
			if (field == null)
			{
				return false;
			}
		}
		else
		{
			int dot = rest.LastIndexOf('.');
			field = dot < 0 ? rest : rest[..dot];
			aggregate = dot < 0 ? "last" : rest[(dot + 1)..];
		}

		if (!IsAggregate(aggregate) || (field != null && Array.IndexOf(known, field) < 0))
		{
			return false;
		}

		var series = new Series();
		int matched = 0;
		uint matchedTick = EndTick;
		foreach ((string Kind, uint Tick, IReadOnlyDictionary<string, double> Fields) record in _records)
		{
			if (record.Kind != kind || !Matches(record.Fields, conditions))
			{
				continue;
			}

			matched++;
			matchedTick = record.Tick;
			if (field != null && record.Fields != null && record.Fields.TryGetValue(field, out double sample))
			{
				series.Add(sample, record.Tick);
			}
		}

		if (field == null)
		{
			value = matched;
			tick = matchedTick;
			return true;
		}

		if (series.Count == 0)
		{
			return true;
		}

		value = aggregate switch
		{
			"count" => series.Count,
			"sum" => series.Sum,
			"mean" => series.Sum / series.Count,
			"min" => series.Min,
			"max" => series.Max,
			_ => series.Last,
		};
		tick = aggregate switch
		{
			"min" => series.MinTick,
			"max" => series.MaxTick,
			_ => series.LastTick,
		};
		return true;
	}

	private static bool TryCondition(string term, string[] known, out (string Field, string Op, double Value) condition)
	{
		condition = default;
		int at = term.IndexOfAny(new[] { '=', '!', '<', '>' });
		if (at <= 0)
		{
			return false;
		}

		int length = at + 1 < term.Length && term[at + 1] == '=' ? 2 : 1;
		string op = term.Substring(at, length);
		string field = term[..at].Trim();
		if (op is not ("=" or "!=" or "<" or "<=" or ">" or ">=") || Array.IndexOf(known, field) < 0
			|| !double.TryParse(term[(at + length)..].Trim(), System.Globalization.NumberStyles.Float,
				System.Globalization.CultureInfo.InvariantCulture, out double wanted))
		{
			return false;
		}

		condition = (field, op, wanted);
		return true;
	}

	private static bool Matches(IReadOnlyDictionary<string, double> fields,
		(string Field, string Op, double Value)[] conditions)
	{
		for (int i = 0; i < conditions.Length; i++)
		{
			(string field, string op, double wanted) = conditions[i];
			if (fields == null || !fields.TryGetValue(field, out double actual))
			{
				return false;
			}

			bool holds = op switch
			{
				"=" => actual == wanted,
				"!=" => actual != wanted,
				"<" => actual < wanted,
				"<=" => actual <= wanted,
				">" => actual > wanted,
				_ => actual >= wanted,
			};

			if (!holds)
			{
				return false;
			}
		}

		return true;
	}

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
