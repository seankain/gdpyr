using System;
using Gdpyr.Match;
using Gdpyr.Net;
using Xunit;

namespace Gdpyr.Tests;

public class DiscoveryCodecTests
{
	private static ServerInfo Sample() => new()
	{
		Name = "the kitchen box",
		GamePort = 7777,
		Players = 7,
		Bots = 6,
		MaxPlayers = 16,
		Phase = RoundPhase.Live,
		SecondsRemaining = 733,
	};

	[Fact]
	public void AQueryIsRecognized()
	{
		Assert.True(DiscoveryCodec.TryDecodeQuery(DiscoveryCodec.EncodeQuery()));
	}

	[Fact]
	public void AQueryIsNotAReply()
	{
		// The two share a header. A server that answered its own reply would answer
		// every other server on the network, once per refresh, forever.
		Assert.False(DiscoveryCodec.TryDecodeQuery(DiscoveryCodec.EncodeReply(Sample())));
		Assert.False(DiscoveryCodec.TryDecodeReply(DiscoveryCodec.EncodeQuery(), out _));
	}

	[Fact]
	public void AReplyRoundTrips()
	{
		ServerInfo sent = Sample();

		Assert.True(DiscoveryCodec.TryDecodeReply(DiscoveryCodec.EncodeReply(sent), out ServerInfo received));

		Assert.Equal(sent.Name, received.Name);
		Assert.Equal(sent.GamePort, received.GamePort);
		Assert.Equal(sent.Players, received.Players);
		Assert.Equal(sent.Bots, received.Bots);
		Assert.Equal(sent.MaxPlayers, received.MaxPlayers);
		Assert.Equal(sent.Phase, received.Phase);
		Assert.Equal(sent.SecondsRemaining, received.SecondsRemaining);
	}

	[Fact]
	public void PeopleAreCountedApartFromBots()
	{
		// "7 players" that are six bots and one person is the one number a server
		// browser must not round off.
		ServerInfo info = Sample();

		Assert.Equal(1, info.People);
	}

	[Fact]
	public void TheAddressIsNotOnTheWire()
	{
		// It is whatever the datagram arrived from: a host behind three interfaces
		// does not know which of its addresses the client can reach.
		Assert.True(DiscoveryCodec.TryDecodeReply(DiscoveryCodec.EncodeReply(Sample()), out ServerInfo received));

		Assert.Null(received.Address);
	}

	[Fact]
	public void ARoundClockOverAnHourIsClamped()
	{
		ServerInfo sent = Sample();
		sent.SecondsRemaining = 500_000;

		Assert.True(DiscoveryCodec.TryDecodeReply(DiscoveryCodec.EncodeReply(sent), out ServerInfo received));
		Assert.Equal(ushort.MaxValue, received.SecondsRemaining);
	}

	[Fact]
	public void ARosterLargerThanAByteIsClamped()
	{
		ServerInfo sent = Sample();
		sent.Players = 4000;

		Assert.True(DiscoveryCodec.TryDecodeReply(DiscoveryCodec.EncodeReply(sent), out ServerInfo received));
		Assert.Equal(byte.MaxValue, received.Players);
	}

	[Fact]
	public void AnEmptyNameSurvives()
	{
		ServerInfo sent = Sample();
		sent.Name = string.Empty;

		Assert.True(DiscoveryCodec.TryDecodeReply(DiscoveryCodec.EncodeReply(sent), out ServerInfo received));
		Assert.Equal(string.Empty, received.Name);
	}

	[Fact]
	public void ALongNameIsCutAtACharacterBoundary()
	{
		ServerInfo sent = Sample();
		sent.Name = new string('é', DiscoveryCodec.MaxNameBytes);

		byte[] packet = DiscoveryCodec.EncodeReply(sent);

		Assert.True(packet.Length <= DiscoveryCodec.MaxReplyLength);
		Assert.True(DiscoveryCodec.TryDecodeReply(packet, out ServerInfo received));

		// Half as many characters as were asked for, and not one of them mangled.
		Assert.Equal(DiscoveryCodec.MaxNameBytes / 2, received.Name.Length);
		Assert.DoesNotContain('�', received.Name);
	}

	[Fact]
	public void SomebodyElsesDatagramIsNotOurs()
	{
		// The socket is open to whatever else is on the Wi-Fi (docs/LAN.md §3.2).
		Assert.False(DiscoveryCodec.TryDecodeQuery(new byte[] { 1, 2, 3, 4, 5, 6 }));
		Assert.False(DiscoveryCodec.TryDecodeReply(new byte[32], out _));
	}

	[Fact]
	public void AnEmptyDatagramIsNotOurs()
	{
		Assert.False(DiscoveryCodec.TryDecodeQuery(System.Array.Empty<byte>()));
		Assert.False(DiscoveryCodec.TryDecodeReply(System.Array.Empty<byte>(), out _));
	}

	[Fact]
	public void AnotherProtocolVersionIsIgnoredRatherThanGuessedAt()
	{
		byte[] packet = DiscoveryCodec.EncodeReply(Sample());
		packet[4]++;

		Assert.False(DiscoveryCodec.TryDecodeReply(packet, out _));
	}

	[Fact]
	public void ATruncatedReplyIsRefused()
	{
		byte[] packet = DiscoveryCodec.EncodeReply(Sample());

		for (int length = 0; length < packet.Length; length++)
		{
			Assert.False(DiscoveryCodec.TryDecodeReply(packet.AsSpan(0, length), out _));
		}

		Assert.True(DiscoveryCodec.TryDecodeReply(packet, out _));
	}

	[Fact]
	public void APortOfZeroIsRefused()
	{
		// It is the one field a client acts on by dialling it.
		ServerInfo sent = Sample();
		sent.GamePort = 0;

		Assert.False(DiscoveryCodec.TryDecodeReply(DiscoveryCodec.EncodeReply(sent), out _));
	}

	[Fact]
	public void AnUnknownPhaseReadsAsWarmup()
	{
		byte[] packet = DiscoveryCodec.EncodeReply(Sample());
		packet[11] = 200;

		Assert.True(DiscoveryCodec.TryDecodeReply(packet, out ServerInfo received));
		Assert.Equal(RoundPhase.Warmup, received.Phase);
	}

	[Fact]
	public void TheDiscoveryRangeDoesNotOverlapTheGamesDefaultPort()
	{
		// docs/LAN.md §3.2 puts a second host on 7778. Discovery starts above both.
		Assert.True(DiscoveryCodec.PortBase > Gdpyr.Core.LaunchOptions.DefaultPort + 1);
		Assert.True(DiscoveryCodec.PortSpan > 1);
	}
}
