using System.Collections.Generic;
using System.IO;

namespace Gdpyr.Sim.Demo;

/// <summary>
/// What is in a demo, without playing it (docs/DEMOS.md §3.2). What
/// <c>demoinfo</c> prints, and the cheapest answer to "did that actually record
/// anything".
///
/// Reading one walks the whole file. That is the point — a count of records is
/// the only thing that distinguishes a demo that was cut off from one that was
/// recorded with nothing happening — and at a few hundred kilobytes a round it is
/// not worth being cleverer about.
/// </summary>
public readonly struct DemoSummary
{
	public readonly DemoHeader Header;

	/// <summary>The first and last ticks the file has records for.</summary>
	public readonly uint FirstTick;

	public readonly uint LastTick;

	/// <summary>Ticks with at least one record. Not <c>LastTick - FirstTick</c>: idle ticks are not written.</summary>
	public readonly int Ticks;

	public readonly int Inputs;
	public readonly int Spawns;
	public readonly int Despawns;
	public readonly int PlayerSnapshots;
	public readonly int UnitSnapshots;

	/// <summary>The marks, in order, with the tick each was written at.</summary>
	public readonly IReadOnlyList<(uint Tick, string Text)> Marks;

	/// <summary>
	/// Why the file ended early, or null. A demo whose process was killed
	/// mid-round reads to the end with no error and simply stops — this is set only
	/// when the bytes themselves do not make sense.
	/// </summary>
	public readonly string Error;

	private DemoSummary(in DemoHeader header, uint firstTick, uint lastTick, int ticks, int inputs, int spawns,
		int despawns, int playerSnapshots, int unitSnapshots,
		IReadOnlyList<(uint Tick, string Text)> marks, string error)
	{
		Header = header;
		FirstTick = firstTick;
		LastTick = lastTick;
		Ticks = ticks;
		Inputs = inputs;
		Spawns = spawns;
		Despawns = despawns;
		PlayerSnapshots = playerSnapshots;
		UnitSnapshots = unitSnapshots;
		Marks = marks;
		Error = error;
	}

	/// <summary>Seconds of round in the file, at the simulation's fixed rate.</summary>
	public float Seconds => LastTick >= FirstTick ? (LastTick - FirstTick) / (float)SimConfig.TickRate : 0f;

	/// <summary>
	/// Walks a demo. False with a reason when the stream is not one this build can
	/// read; a file that is fine but truncated comes back true with
	/// <see cref="Error"/> set, because everything before the truncation is still
	/// worth knowing.
	/// </summary>
	public static bool TryRead(Stream stream, out DemoSummary summary, out string error)
	{
		summary = default;

		if (!DemoReader.TryOpen(stream, out DemoReader reader, out error))
		{
			return false;
		}

		using (reader)
		{
			uint first = 0, last = 0;
			bool any = false;
			int ticks = 0, inputs = 0, spawns = 0, despawns = 0, players = 0, units = 0;
			var marks = new List<(uint, string)>();

			while (reader.TryReadRecord(out DemoRecord record))
			{
				if (!any || record.Tick != last)
				{
					ticks++;
				}

				if (!any)
				{
					first = record.Tick;
					any = true;
				}

				last = record.Tick;

				switch (record.Kind)
				{
					case DemoRecordKind.Input: inputs++; break;
					case DemoRecordKind.Spawn: spawns++; break;
					case DemoRecordKind.Despawn: despawns++; break;
					case DemoRecordKind.PlayerSnapshot: players++; break;
					case DemoRecordKind.UnitSnapshot: units++; break;
					case DemoRecordKind.Mark: marks.Add((record.Tick, record.Text)); break;
				}
			}

			if (!any)
			{
				first = reader.Header.StartTick;
				last = first;
			}

			summary = new DemoSummary(reader.Header, first, last, ticks, inputs, spawns, despawns, players,
				units, marks, reader.Error);
		}

		return true;
	}

	/// <summary>One line per fact, for a console that has no table to put them in.</summary>
	public IReadOnlyList<string> Describe()
	{
		var lines = new List<string>
		{
			$"  kind        {Header.Kind}"
				+ (Header.Kind == DemoKind.Journal
					? "  (every character's input; playback re-runs it)"
					: "  (one client's view; playback interpolates it)"),
			$"  recorded    {System.DateTimeOffset.FromUnixTimeSeconds(Header.RecordedAtUnixSeconds).LocalDateTime:yyyy-MM-dd HH:mm}"
				+ (string.IsNullOrEmpty(Header.RecordedBy) ? string.Empty : $" by {Header.RecordedBy}"),
			$"  map         {(string.IsNullOrEmpty(Header.Map) ? "unknown" : Header.Map)}",
			$"  ticks       {Ticks} recorded, {FirstTick}-{LastTick} ({Seconds:0.0}s at {Header.TickRate} Hz)",
			$"  records     {Inputs} input, {PlayerSnapshots} player, {UnitSnapshots} unit,"
				+ $" {Spawns} spawn, {Despawns} despawn",
			$"  seed        {Header.RoundSeed}",
		};

		for (int i = 0; i < Marks.Count; i++)
		{
			(uint tick, string text) = Marks[i];
			float seconds = tick >= FirstTick ? (tick - FirstTick) / (float)SimConfig.TickRate : 0f;
			lines.Add($"  mark        {seconds:0.0}s  {text}");
		}

		if (Error != null)
		{
			lines.Add($"  truncated   {Error}");
		}

		return lines;
	}
}
