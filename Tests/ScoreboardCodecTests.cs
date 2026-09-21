using System;
using Gdpyr.Sim;
using Xunit;

namespace Gdpyr.Tests;

/// <summary>
/// The one message that carries kills and deaths (docs/IMPLEMENTATION_PLAN.md
/// §M5). Total decoding, like every other codec here: a malformed payload returns
/// false rather than throwing.
/// </summary>
public class ScoreboardCodecTests
{
	private static ScoreEntry[] Roster(int count)
	{
		var entries = new ScoreEntry[count];
		for (int i = 0; i < count; i++)
		{
			entries[i] = new ScoreEntry
			{
				PeerId = 1000 + i,
				Kills = (short)(i * 3),
				Deaths = (short)(i + 1),
				Team = i % 3 == 0 ? Team.Strategist : Team.GroundForce,
				IsBot = i % 2 == 0,
			};
		}
		return entries;
	}

	private static RoundSummary Summary() => new()
	{
		Outcome = 3,
		GroundTickets = 17,
		StrategistPoints = 1234,
		UnitsBuilt = 42,
		UnitsLost = 40,
		RoundTicks = SimConfig.TickRate * 60 * 7,
	};

	[Fact]
	public void ARoundTripKeepsEveryRow()
	{
		ScoreEntry[] entries = Roster(8);
		byte[] payload = ScoreboardCodec.Encode(Summary(), entries);

		var into = new ScoreEntry[ScoreboardCodec.MaxEntries];
		Assert.True(ScoreboardCodec.TryDecode(payload, into, out RoundSummary summary, out int count));

		Assert.Equal(8, count);
		Assert.Equal(3, summary.Outcome);
		Assert.Equal(17, summary.GroundTickets);
		Assert.Equal(1234, summary.StrategistPoints);
		Assert.Equal(42, summary.UnitsBuilt);
		Assert.Equal(40, summary.UnitsLost);
		Assert.Equal(7f, summary.Minutes, 3);

		for (int i = 0; i < count; i++)
		{
			Assert.Equal(entries[i].PeerId, into[i].PeerId);
			Assert.Equal(entries[i].Kills, into[i].Kills);
			Assert.Equal(entries[i].Deaths, into[i].Deaths);
			Assert.Equal(entries[i].Team, into[i].Team);
			Assert.Equal(entries[i].IsBot, into[i].IsBot);
		}
	}

	[Fact]
	public void AnEmptyRoundStillEncodes()
	{
		byte[] payload = ScoreboardCodec.Encode(Summary(), ReadOnlySpan<ScoreEntry>.Empty);

		var into = new ScoreEntry[ScoreboardCodec.MaxEntries];
		Assert.True(ScoreboardCodec.TryDecode(payload, into, out _, out int count));
		Assert.Equal(0, count);
		Assert.Equal(ScoreboardCodec.HeaderBytes, payload.Length);
	}

	[Fact]
	public void MoreRowsThanTheMessageHoldsAreDropped()
	{
		byte[] payload = ScoreboardCodec.Encode(Summary(), Roster(ScoreboardCodec.MaxEntries + 4));

		var into = new ScoreEntry[ScoreboardCodec.MaxEntries];
		Assert.True(ScoreboardCodec.TryDecode(payload, into, out _, out int count));
		Assert.Equal(ScoreboardCodec.MaxEntries, count);
		Assert.Equal(ScoreboardCodec.MaxPayloadBytes, payload.Length);
	}

	[Fact]
	public void ATruncatedPayloadIsRefused()
	{
		byte[] payload = ScoreboardCodec.Encode(Summary(), Roster(4));

		var into = new ScoreEntry[ScoreboardCodec.MaxEntries];
		for (int length = 0; length < payload.Length; length++)
		{
			Assert.False(ScoreboardCodec.TryDecode(payload.AsSpan(0, length), into, out _, out _));
		}
	}

	[Fact]
	public void ACountThatDoesNotMatchTheLengthIsRefused()
	{
		byte[] payload = ScoreboardCodec.Encode(Summary(), Roster(2));
		payload[1] = 12;

		var into = new ScoreEntry[ScoreboardCodec.MaxEntries];
		Assert.False(ScoreboardCodec.TryDecode(payload, into, out _, out _));
	}

	[Fact]
	public void ABufferTooSmallForTheRowsIsRefused()
	{
		byte[] payload = ScoreboardCodec.Encode(Summary(), Roster(6));

		Assert.False(ScoreboardCodec.TryDecode(payload, new ScoreEntry[2], out _, out _));
		Assert.False(ScoreboardCodec.TryDecode(payload, null, out _, out _));
	}

	[Fact]
	public void TheWholeRosterFitsInOneDatagram()
	{
		// One reliable message a round, and it has to be one message.
		Assert.True(ScoreboardCodec.MaxPayloadBytes < 512);
	}
}
