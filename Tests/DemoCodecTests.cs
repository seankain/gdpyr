using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Gdpyr.Sim;
using Gdpyr.Sim.Demo;
using Xunit;

namespace Gdpyr.Tests;

/// <summary>
/// The demo container (docs/DEMOS.md §2). Everything here runs over a
/// <see cref="MemoryStream"/>: the format is engine-free and so is the question
/// of whether a truncated file can be read without taking a client down.
/// </summary>
public class DemoCodecTests
{
	private static InputFrame Sample(uint tick, float yaw = 1.25f) =>
		InputFrame.Create(tick, -1f, 0.5f, yaw, -0.75f, InputButtons.Fire | InputButtons.Sprint);

	private static DemoHeader Header(DemoKind kind = DemoKind.Journal, uint startTick = 900) =>
		DemoHeader.Create(kind, startTick, 1_700_000_000L, 4242, 7, "res://Scenes/Test.tscn", "the kitchen box");

	private static MemoryStream Write(Action<DemoWriter> body, DemoHeader? header = null)
	{
		var stream = new MemoryStream();
		using (var writer = new DemoWriter(stream, header ?? Header(), leaveOpen: true))
		{
			body(writer);
		}

		stream.Position = 0;
		return stream;
	}

	private static List<DemoRecord> ReadAll(Stream stream, out DemoReader reader)
	{
		Assert.True(DemoReader.TryOpen(stream, out reader, out string error), error);

		var records = new List<DemoRecord>();
		while (reader.TryReadRecord(out DemoRecord record))
		{
			records.Add(record);
		}

		return records;
	}

	// ---- the header --------------------------------------------------------

	[Fact]
	public void Header_RoundTripsEveryField()
	{
		DemoHeader written = Header(DemoKind.Stream, startTick: 12_345);
		byte[] bytes = written.Encode();

		Assert.True(DemoFormat.HasMagic(bytes));
		Assert.True(DemoHeader.TryDecode(bytes, out DemoHeader read, out int consumed));
		Assert.Equal(bytes.Length, consumed);

		Assert.Equal(DemoFormat.Version, read.Version);
		Assert.Equal(DemoKind.Stream, read.Kind);
		Assert.Equal((byte)SimConfig.TickRate, read.TickRate);
		Assert.Equal((byte)SimConfig.SnapshotRate, read.SnapshotRate);
		Assert.Equal((byte)SimConfig.UnitSnapshotRate, read.UnitSnapshotRate);
		Assert.Equal(12_345u, read.StartTick);
		Assert.Equal(1_700_000_000L, read.RecordedAtUnixSeconds);
		Assert.Equal(4242, read.RoundSeed);
		Assert.Equal(7, read.LocalPeerId);
		Assert.Equal("res://Scenes/Test.tscn", read.Map);
		Assert.Equal("the kitchen box", read.RecordedBy);
	}

	[Fact]
	public void Header_RefusesAnythingThatIsNotADemo()
	{
		Assert.False(DemoHeader.TryDecode(
			System.Text.Encoding.ASCII.GetBytes("not a demo at all, sorry"), out _, out _));
		Assert.False(DemoHeader.TryDecode(ReadOnlySpan<byte>.Empty, out _, out _));
	}

	[Fact]
	public void Header_RefusesATruncatedOne()
	{
		byte[] bytes = Header().Encode();
		Assert.False(DemoHeader.TryDecode(bytes.AsSpan(0, bytes.Length - 3), out _, out _));
	}

	[Fact]
	public void Header_RefusesAVersionOrATickRateThisBuildCannotPlay()
	{
		DemoHeader header = Header();
		Assert.True(header.IsPlayable(out _));

		header.Version = (byte)(DemoFormat.Version + 1);
		Assert.False(header.IsPlayable(out string reason));
		Assert.Contains("version", reason);

		header = Header();
		header.TickRate = 30;
		Assert.False(header.IsPlayable(out reason));
		Assert.Contains("Hz", reason);

		// A tick index means something different at another rate, so this is refused
		// rather than scaled (docs/DEMOS.md §2.1).
		header = Header();
		header.Kind = (DemoKind)99;
		Assert.False(header.IsPlayable(out _));
	}

	[Fact]
	public void Header_TruncatesAnAbsurdlyLongNameOnACharacterBoundary()
	{
		DemoHeader header = DemoHeader.Create(DemoKind.Journal, 0, 0, 0, 1, "map",
			new string('é', DemoFormat.MaxTextBytes));

		Assert.True(DemoHeader.TryDecode(header.Encode(), out DemoHeader read, out _));

		// Two bytes each, so the clamp lands mid-rune unless it walks back.
		Assert.True(read.RecordedBy.Length <= DemoFormat.MaxTextBytes / 2);
		Assert.DoesNotContain('�', read.RecordedBy);
	}

	// ---- the record stream -------------------------------------------------

	[Fact]
	public void RoundTrip_PreservesEveryRecordAndItsTick()
	{
		byte[] players = SnapshotCodec.Encode(101, new[] { new PlayerSnapshot { PeerId = 3, Health = 55 } });
		byte[] units = UnitSnapshotCodec.Encode(101, new[] { new UnitSnapshot { UnitId = 9, HealthPercent = 200 } });

		using MemoryStream stream = Write(writer =>
		{
			writer.WriteSpawn(100, peerId: 3, spawnIndex: 2, isBot: false);
			writer.WriteSpawn(100, peerId: 40_000, spawnIndex: 5, isBot: true);
			writer.WriteInput(100, 3, Sample(100));
			writer.WriteInput(101, 3, Sample(101, yaw: -2f));
			writer.WritePlayerSnapshot(101, players);
			writer.WriteUnitSnapshot(101, units);
			writer.WriteMark(102, "this is the bit");
			writer.WriteDespawn(103, 3);
		});

		List<DemoRecord> records = ReadAll(stream, out DemoReader reader);
		Assert.Null(reader.Error);
		Assert.Equal(8, records.Count);

		Assert.Equal(DemoRecordKind.Spawn, records[0].Kind);
		Assert.Equal(100u, records[0].Tick);
		Assert.Equal(3, records[0].PeerId);
		Assert.Equal(2, records[0].SpawnIndex);
		Assert.False(records[0].IsBot);

		Assert.Equal(40_000, records[1].PeerId);
		Assert.True(records[1].IsBot);

		Assert.Equal(DemoRecordKind.Input, records[2].Kind);
		Assert.Equal(Sample(100).Yaw, records[2].Frame.Yaw);
		Assert.Equal(Sample(100).Buttons, records[2].Frame.Buttons);
		Assert.Equal(100u, records[2].Frame.Tick);

		Assert.Equal(101u, records[3].Tick);

		Assert.Equal(DemoRecordKind.PlayerSnapshot, records[4].Kind);
		Assert.Equal(players, records[4].Payload);

		Assert.Equal(DemoRecordKind.UnitSnapshot, records[5].Kind);
		Assert.Equal(units, records[5].Payload);

		Assert.Equal(DemoRecordKind.Mark, records[6].Kind);
		Assert.Equal("this is the bit", records[6].Text);
		Assert.Equal(102u, records[6].Tick);

		Assert.Equal(DemoRecordKind.Despawn, records[7].Kind);
		Assert.Equal(103u, records[7].Tick);
	}

	[Fact]
	public void TickMarkers_AreWrittenOncePerTickAndNotAtAllForAnIdleOne()
	{
		using MemoryStream stream = Write(writer =>
		{
			writer.WriteInput(10, 1, Sample(10));
			writer.WriteInput(10, 2, Sample(10));
			writer.WriteInput(10, 3, Sample(10));
			// Nothing for ticks 11..19; the file jumps.
			writer.WriteInput(20, 1, Sample(20));

			Assert.Equal(2, writer.TickCount);
			Assert.Equal(4, writer.RecordCount);
		});

		ReadAll(stream, out DemoReader reader);
		Assert.Equal(4, reader.RecordsRead);
	}

	[Fact]
	public void AJournalledTickIsTwelveBytesPerPlayerPlusATag()
	{
		// The whole argument for journalling rather than recording state: the round
		// costs what the players pressed (docs/DEMOS.md §2.3).
		using MemoryStream empty = Write(_ => { });
		long headerBytes = empty.Length;

		using MemoryStream stream = Write(writer =>
		{
			for (uint tick = 0; tick < 60; tick++)
			{
				for (int peer = 1; peer <= 8; peer++)
				{
					writer.WriteInput(tick, peer, Sample(tick));
				}
			}
		});

		// 60 tick markers of 5 bytes, then 480 records of a tag, a peer id and a frame.
		long expected = headerBytes + (60 * 5) + (60 * 8 * (1 + 4 + InputFrame.SizeBytes));
		Assert.Equal(expected, stream.Length);

		// A second of eight players is under nine kilobytes; a ten-minute round is
		// about five megabytes.
		Assert.True(stream.Length - headerBytes < 9 * 1024);
	}

	// ---- reading a tick at a time ------------------------------------------

	[Fact]
	public void TryReadTick_HandsBackOneBlockAtATime()
	{
		using MemoryStream stream = Write(writer =>
		{
			writer.WriteInput(5, 1, Sample(5));
			writer.WriteInput(5, 2, Sample(5));
			writer.WriteInput(6, 1, Sample(6));
			writer.WriteMark(7, "and here");
		});

		Assert.True(DemoReader.TryOpen(stream, out DemoReader reader, out string error), error);

		var block = new List<DemoRecord>();

		Assert.True(reader.TryReadTick(block, out uint tick));
		Assert.Equal(5u, tick);
		Assert.Equal(2, block.Count);

		Assert.True(reader.TryReadTick(block, out tick));
		Assert.Equal(6u, tick);
		Assert.Single(block);

		Assert.True(reader.TryReadTick(block, out tick));
		Assert.Equal(7u, tick);
		Assert.Single(block);
		Assert.Equal(DemoRecordKind.Mark, block[0].Kind);

		// The last block must not be dropped by the one-record lookahead.
		Assert.False(reader.TryReadTick(block, out _));
		Assert.Empty(block);
		Assert.True(reader.Ended);
		Assert.Null(reader.Error);
	}

	// ---- hostile and broken files ------------------------------------------

	[Fact]
	public void TryOpen_RefusesSomethingThatIsNotADemo()
	{
		using var stream = new MemoryStream(new byte[] { 0x50, 0x4b, 0x03, 0x04, 0x14, 0x00, 0x00, 0x00, 0x08 });
		Assert.False(DemoReader.TryOpen(stream, out _, out string error));
		Assert.Equal("not a gdpyr demo", error);
	}

	[Fact]
	public void TryOpen_RefusesAnEmptyFile()
	{
		using var stream = new MemoryStream();
		Assert.False(DemoReader.TryOpen(stream, out _, out _));
	}

	[Fact]
	public void ATruncatedFileEndsWhereItWasCutAndSaysSo()
	{
		using MemoryStream whole = Write(writer =>
		{
			writer.WriteInput(1, 1, Sample(1));
			writer.WriteInput(2, 1, Sample(2));
			writer.WriteInput(3, 1, Sample(3));
		});

		// Lop off half of the last record, as a killed process would.
		byte[] bytes = whole.ToArray();
		using var cut = new MemoryStream(bytes.AsSpan(0, bytes.Length - 6).ToArray());

		List<DemoRecord> records = ReadAll(cut, out DemoReader reader);
		Assert.Equal(2, records.Count);
		Assert.NotNull(reader.Error);
		Assert.Contains("ends in the middle", reader.Error);
	}

	[Fact]
	public void AFileThatWasNeverClosedReadsToItsEndWithNoError()
	{
		// No Finish(): the writer is abandoned, which is what a crash leaves behind.
		var stream = new MemoryStream();
		var writer = new DemoWriter(stream, Header(), leaveOpen: true);
		writer.WriteInput(1, 1, Sample(1));
		writer.WriteInput(2, 1, Sample(2));
		stream.Position = 0;

		List<DemoRecord> records = ReadAll(stream, out DemoReader reader);
		Assert.Equal(2, records.Count);
		Assert.True(reader.Ended);
		Assert.Null(reader.Error);
	}

	[Fact]
	public void AnUnknownRecordTagEndsTheStreamRatherThanBeingSkipped()
	{
		using MemoryStream whole = Write(writer => writer.WriteInput(1, 1, Sample(1)));
		byte[] bytes = whole.ToArray();

		// Overwrite the End tag the writer put on: resynchronizing on a tag byte
		// would read a payload's contents as records (docs/DEMOS.md §2.2).
		bytes[^1] = 0x7f;

		using var stream = new MemoryStream(bytes);
		List<DemoRecord> records = ReadAll(stream, out DemoReader reader);
		Assert.Single(records);
		Assert.Contains("unknown record tag", reader.Error);
	}

	[Fact]
	public void ARecordStampedWithAnEarlierTickJoinsTheBlockBeingWritten()
	{
		// A client has three clocks and this is what keeps its demo in one order: its
		// input clock runs ahead of its estimate of the server's, and the snapshots it
		// records are from a tick that has already happened (docs/NETCODE.md §2).
		using MemoryStream stream = Write(writer =>
		{
			writer.WriteInput(10, 1, Sample(10));
			writer.WriteInput(9, 1, Sample(9));
			writer.WriteInput(11, 1, Sample(11));

			Assert.Equal(2, writer.TickCount);
		});

		List<DemoRecord> records = ReadAll(stream, out DemoReader reader);

		Assert.Equal(3, records.Count);
		Assert.Equal(new[] { 10u, 10u, 11u }, records.Select(r => r.Tick));

		// The tick the frame is really about is inside the frame and is untouched.
		Assert.Equal(9u, records[1].Frame.Tick);
		Assert.Null(reader.Error);
	}

	[Fact]
	public void BlocksThatGoBackwardsInTheFileAreRefusedOnRead()
	{
		// The writer cannot produce one, so this is a spliced or corrupted file — and
		// a demo arrives from wherever it arrives from. Replaying it would put inputs
		// on ticks that have already been simulated.
		using MemoryStream whole = Write(writer =>
		{
			writer.WriteInput(10, 1, Sample(10));
			writer.WriteInput(11, 1, Sample(11));
		});

		byte[] bytes = whole.ToArray();
		int marker = FindSecondTickMarker(bytes);
		bytes[marker + 1] = 9;

		using var stream = new MemoryStream(bytes);
		List<DemoRecord> records = ReadAll(stream, out DemoReader reader);
		Assert.Single(records);
		Assert.Contains("follows tick", reader.Error);
	}

	/// <summary>Byte offset of the second <see cref="DemoRecordKind.Tick"/> tag in a two-block file.</summary>
	private static int FindSecondTickMarker(byte[] bytes)
	{
		int offset = DemoHeader.TryDecode(bytes, out _, out int headerBytes) ? headerBytes : 0;

		// Block one: a tick marker (5 B) then one input record.
		Assert.Equal((byte)DemoRecordKind.Tick, bytes[offset]);
		offset += 5 + 1 + 4 + InputFrame.SizeBytes;

		Assert.Equal((byte)DemoRecordKind.Tick, bytes[offset]);
		return offset;
	}

	[Fact]
	public void APayloadBiggerThanTheCeilingIsRefusedRatherThanTruncated()
	{
		using MemoryStream stream = Write(writer =>
		{
			writer.WritePlayerSnapshot(1, new byte[DemoFormat.MaxPayloadBytes + 1]);
			Assert.NotNull(writer.Failed);
		});

		Assert.Empty(ReadAll(stream, out _));
	}

	[Fact]
	public void TheLargestSnapshotEitherCodecProducesFitsARecord()
	{
		Assert.True(SnapshotCodec.MaxPayloadBytes <= DemoFormat.MaxPayloadBytes);
		Assert.True(UnitSnapshotCodec.MaxPayloadBytes <= DemoFormat.MaxPayloadBytes);
	}

	[Fact]
	public void WritingToABrokenStreamStopsTheRecordingAndNotTheRound()
	{
		var writer = new DemoWriter(new ClosedStream(), Header());

		writer.WriteInput(1, 1, Sample(1));
		Assert.NotNull(writer.Failed);
		Assert.False(writer.IsWriting);

		// And every subsequent call is a no-op rather than a second exception.
		writer.WriteSpawn(2, 1, 0, false);
		writer.WriteMark(2, "still here");
		writer.Finish();
		writer.Dispose();
	}

	// ---- the summary -------------------------------------------------------

	[Fact]
	public void Summary_CountsWhatIsInTheFile()
	{
		byte[] players = SnapshotCodec.Encode(11, new[] { new PlayerSnapshot { PeerId = 1 } });

		using MemoryStream stream = Write(writer =>
		{
			writer.WriteSpawn(10, 1, 0, false);
			writer.WriteInput(10, 1, Sample(10));
			writer.WriteInput(11, 1, Sample(11));
			writer.WritePlayerSnapshot(11, players);
			writer.WriteUnitSnapshot(11, UnitSnapshotCodec.Encode(11, Array.Empty<UnitSnapshot>()));
			writer.WriteMark(12, "watch this");
			writer.WriteDespawn(13, 1);
		}, Header(startTick: 10));

		Assert.True(DemoSummary.TryRead(stream, out DemoSummary summary, out string error), error);

		Assert.Equal(DemoKind.Journal, summary.Header.Kind);
		Assert.Equal(10u, summary.FirstTick);
		Assert.Equal(13u, summary.LastTick);
		Assert.Equal(4, summary.Ticks);
		Assert.Equal(2, summary.Inputs);
		Assert.Equal(1, summary.Spawns);
		Assert.Equal(1, summary.Despawns);
		Assert.Equal(1, summary.PlayerSnapshots);
		Assert.Equal(1, summary.UnitSnapshots);
		Assert.Single(summary.Marks);
		Assert.Equal(12u, summary.Marks[0].Tick);
		Assert.Equal("watch this", summary.Marks[0].Text);
		Assert.Null(summary.Error);

		Assert.Contains(summary.Describe(), line => line.Contains("watch this"));
	}

	[Fact]
	public void Summary_OfAnEmptyRecordingIsNotAnError()
	{
		using MemoryStream stream = Write(_ => { }, Header(startTick: 77));

		Assert.True(DemoSummary.TryRead(stream, out DemoSummary summary, out _));
		Assert.Equal(0, summary.Ticks);
		Assert.Equal(77u, summary.FirstTick);
		Assert.Equal(77u, summary.LastTick);
		Assert.Empty(summary.Marks);
	}

	// ---- names -------------------------------------------------------------

	[Theory]
	[InlineData("game", "game.dem")]
	[InlineData("  game  ", "game.dem")]
	[InlineData("game.demo", "game.demo")]
	[InlineData("game.dem", "game.dem")]
	[InlineData("last night", "last night.dem")]
	public void AName_BecomesAFileName(string typed, string expected)
	{
		Assert.True(DemoFormat.TryParseName(typed, out string fileName, out string error), error);
		Assert.Equal(expected, fileName);
	}

	[Theory]
	[InlineData("")]
	[InlineData("   ")]
	[InlineData(null)]
	[InlineData("../../etc/passwd")]
	[InlineData("demos/game")]
	[InlineData("demos\\game")]
	[InlineData("C:game")]
	[InlineData(".hidden")]
	[InlineData("..")]
	[InlineData("what?")]
	[InlineData("star*")]
	[InlineData("pipe|it")]
	[InlineData("quote\"it")]
	public void AName_ThatIsNotAFileNameIsRefused(string typed)
	{
		Assert.False(DemoFormat.TryParseName(typed, out string fileName, out string error));
		Assert.Null(fileName);
		Assert.NotNull(error);
	}

	[Fact]
	public void AName_HasACeiling()
	{
		Assert.False(DemoFormat.TryParseName(new string('a', DemoFormat.MaxNameBytes + 1), out _, out string error));
		Assert.Contains("at most", error);
	}

	[Fact]
	public void AName_WithAControlCharacterIsRefused() =>
		Assert.False(DemoFormat.TryParseName("game\n.dem", out _, out _));

	/// <summary>A stream that refuses every write, which is what a full disk looks like.</summary>
	private sealed class ClosedStream : Stream
	{
		public override bool CanRead => false;
		public override bool CanSeek => false;
		public override bool CanWrite => true;
		public override long Length => 0;

		public override long Position
		{
			get => 0;
			set { }
		}

		public override void Flush() => throw new IOException("no space left on device");

		public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

		public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

		public override void SetLength(long value) => throw new NotSupportedException();

		public override void Write(byte[] buffer, int offset, int count) =>
			throw new IOException("no space left on device");
	}
}
