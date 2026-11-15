using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using Gdpyr.Sim;
using Gdpyr.Sim.Level;
using Xunit;

namespace Gdpyr.Tests;

/// <summary>
/// A map on the wire (docs/LEVEL_EDITOR.md §6.10, §9.3, LE7): the chunks, the sender and the
/// receiver, with no socket in them. A client gets the map it lacks, and only a whole and valid
/// one (§12).
/// </summary>
public class MapTransferTests
{
	private static byte[] FileOf(MapDocument document)
	{
		Assert.True(MapFile.TryWrite(document, out byte[] file, out string error), error);
		return file;
	}

	private static byte[] Generated(int seed) => FileOf(LevelMaps.Generate(seed));

	/// <summary>Every chunk of a file, in order, as a sender would send them.</summary>
	private static List<byte[]> Packets(byte[] file)
	{
		var packets = new List<byte[]>();
		for (int i = 0; i < MapTransfer.ChunkCount(file.Length); i++)
		{
			packets.Add(MapTransfer.Packet(file, i));
		}

		return packets;
	}

	private static MapReceiver Start(byte[] file) => Start(MapFile.HashHex(file), file.Length);

	private static MapReceiver Start(string hash, int length)
	{
		Assert.True(MapReceiver.TryStart(hash, length, out MapReceiver receiver, out string error), error);
		return receiver;
	}

	private static void AssertKeptNothing(MapReceiver receiver, string because)
	{
		Assert.Equal(MapReceiveState.Refused, receiver.State);
		Assert.Contains(because, receiver.Error);
		Assert.Null(receiver.File);
		Assert.Null(receiver.Document);
		Assert.Null(receiver.Report);
	}

	// ---- the chunks ------------------------------------------------------------------------

	[Fact]
	public void AChunk_Is1024BytesBehindItsIndex_AndItsMessageIsOneDatagram()
	{
		Assert.Equal(1024, MapTransfer.ChunkBytes);
		Assert.Equal(4, MapTransfer.IndexBytes);
		Assert.Equal(1028, MapTransfer.MaxPacketBytes);

		// §3: a message whose only argument is a byte array is those bytes behind 3–7 B.
		Assert.Equal(1035, MapTransfer.MaxPacketBytes + MapTransfer.RpcHeaderBytes);
		Assert.True(MapTransfer.MaxPacketBytes + MapTransfer.RpcHeaderBytes < MapTransfer.EnetMtuBytes);
	}

	[Theory]
	[InlineData(1, 1, 1)]
	[InlineData(1024, 1, 1024)]
	[InlineData(1025, 2, 1)]
	[InlineData(2048, 2, 1024)]
	[InlineData(27_299, 27, 675)]
	public void AFile_IsCutIntoWholeChunksAndARemainder(int length, int chunks, int last)
	{
		Assert.Equal(chunks, MapTransfer.ChunkCount(length));
		for (int i = 0; i < chunks - 1; i++)
		{
			Assert.Equal(1024, MapTransfer.ChunkLength(length, i));
		}

		Assert.Equal(last, MapTransfer.ChunkLength(length, chunks - 1));
		Assert.Equal(0, MapTransfer.ChunkLength(length, chunks));
		Assert.Equal(0, MapTransfer.ChunkLength(length, -1));
		Assert.Equal(0, MapTransfer.ChunkCount(0));
	}

	[Fact]
	public void APacket_IsItsIndexLittleEndian_ThenThoseBytesOfTheFile()
	{
		byte[] file = Generated(1);
		for (int i = 0; i < MapTransfer.ChunkCount(file.Length); i++)
		{
			byte[] packet = MapTransfer.Packet(file, i);
			Assert.Equal((uint)i, BinaryPrimitives.ReadUInt32LittleEndian(packet));
			Assert.Equal(file.AsSpan(i * 1024, MapTransfer.ChunkLength(file.Length, i)).ToArray(), packet[4..]);
		}

		Assert.Throws<ArgumentOutOfRangeException>(() => MapTransfer.Packet(file, MapTransfer.ChunkCount(file.Length)));
	}

	[Fact]
	public void TheRates_AreLE_D17s_AndSection10sTimes()
	{
		// A chunk a tick to a peer: 61,440 B/s. At most four a tick in all: under 256 KB/s.
		Assert.Equal(61_440, MapTransfer.ChunkBytes * MapTransfer.ChunksPerTickToAPeer * SimConfig.TickRate);
		Assert.Equal(245_760, MapTransfer.ChunkBytes * MapTransfer.MaxChunksPerTick * SimConfig.TickRate);
		Assert.True(MapTransfer.ChunkBytes * (MapTransfer.MaxChunksPerTick + 1) * SimConfig.TickRate > 256 * 1024);

		// §10: the largest a 1 m map's file can be is 149 chunks, 2.47 s; a new map one chunk; 1 MiB, 17.1 s.
		Assert.Equal(151_643, MapFile.MaxMapBytes);
		Assert.Equal(149, MapTransfer.ChunkCount(MapFile.MaxMapBytes));
		Assert.Equal(2.47, Math.Round(MapFile.MaxMapBytes / 61_440.0, 2));
		Assert.Equal(1, MapTransfer.ChunkCount(FileOf(LevelMaps.Empty()).Length));
		Assert.Equal(1024, MapTransfer.ChunkCount(MapLimits.MaxFileBytes));
		Assert.Equal(17.1, Math.Round(MapLimits.MaxFileBytes / 61_440.0, 1));
		Assert.Equal(10.0, MapTransfer.StallSeconds);
		Assert.Equal(16, MapTransfer.NonceBytes);
	}

	[Fact]
	public void AHash_IsSixtyFourLowerCaseHexDigits()
	{
		string hash = MapFile.HashHex(Generated(1));
		Assert.True(MapTransfer.IsHash(hash));
		Assert.False(MapTransfer.IsHash(hash.ToUpperInvariant()));
		Assert.False(MapTransfer.IsHash(hash[..63]));
		Assert.False(MapTransfer.IsHash(hash + "0"));
		Assert.False(MapTransfer.IsHash(hash[..63] + "g"));
		Assert.False(MapTransfer.IsHash(null));
		Assert.False(MapTransfer.IsHash(string.Empty));
	}

	[Fact]
	public void TheStatusLine_IsSection9_3s()
	{
		Assert.Equal("fetching 'ridge' 47 / 114", MapTransfer.Progress("ridge", 47, 114));
		Assert.Equal("6d0aa3ff…", MapTransfer.Short("6d0aa3ff6ebe3487e53d4f4488318b2ebe47ada2e27fb3ae568320a4a8450958"));
	}

	// ---- the whole way ------------------------------------------------------------------------

	[Theory]
	[InlineData(1)]
	[InlineData(2)]
	[InlineData(3)]
	public void AGeneratedMap_SentChunkByChunk_ArrivesAsTheSameBytesAndTheSameMap(int seed)
	{
		MapDocument document = LevelMaps.Generate(seed);
		byte[] file = FileOf(document);
		var sender = new MapSender(file);
		Assert.Equal(MapFile.HashHex(file), sender.Hash);
		Assert.Equal(file.Length, sender.Length);

		MapReceiver receiver = Start(sender.Hash, sender.Length);
		Assert.Equal(sender.Chunks, receiver.Chunks);
		Assert.Equal(MapRequestAnswer.Sending, sender.Request(7, receiver.Hash));

		var tick = new List<(int Peer, int Index)>();
		int ticks = 0;
		while (receiver.State == MapReceiveState.Receiving)
		{
			sender.Tick(tick);
			Assert.Single(tick);
			Assert.Equal(7, tick[0].Peer);
			Assert.Equal(receiver.Received, tick[0].Index);
			receiver.Accept(sender.Packet(tick[0].Index));
			receiver.Advance(1.0 / SimConfig.TickRate);
			ticks++;
		}

		Assert.Equal(MapReceiveState.Complete, receiver.State);
		Assert.Null(receiver.Error);
		Assert.Equal(sender.Chunks, ticks);
		Assert.Equal(file, receiver.File);
		Assert.True(receiver.Document.SameAs(document, out string difference), difference);
		Assert.True(receiver.Report.IsPlayable);

		// The sender is done with it, and sends nothing more.
		Assert.False(sender.IsSendingTo(7));
		Assert.Equal(sender.Chunks, sender.SentTo(7));
		sender.Tick(tick);
		Assert.Empty(tick);
	}

	// ---- the sender ---------------------------------------------------------------------------

	[Fact]
	public void TheSender_SendsOnlyTheMapItIsPlaying_OnlyToAPeerThatAsked_OnceAConnection()
	{
		byte[] file = Generated(1);
		var sender = new MapSender(file);
		var tick = new List<(int Peer, int Index)>();

		// Nobody asked: nothing goes.
		sender.Tick(tick);
		Assert.Empty(tick);

		// Another map's hash: nothing, and the asking is spent.
		string other = MapFile.HashHex(Generated(2));
		Assert.Equal(MapRequestAnswer.NotThisMap, sender.Request(2, other));
		Assert.Equal(MapRequestAnswer.AlreadyAsked, sender.Request(2, sender.Hash));
		Assert.Equal(MapRequestAnswer.NotThisMap, sender.Request(3, sender.Hash.ToUpperInvariant()));
		Assert.Equal(MapRequestAnswer.NotThisMap, sender.Request(4, null));
		sender.Tick(tick);
		Assert.Empty(tick);

		// Asked rightly: sent, once.
		Assert.Equal(MapRequestAnswer.Sending, sender.Request(5, sender.Hash));
		Assert.Equal(MapRequestAnswer.AlreadyAsked, sender.Request(5, sender.Hash));
		for (int i = 0; i < sender.Chunks; i++)
		{
			sender.Tick(tick);
		}

		Assert.False(sender.IsSendingTo(5));
		Assert.Equal(MapRequestAnswer.AlreadyAsked, sender.Request(5, sender.Hash));
		sender.Tick(tick);
		Assert.Empty(tick);

		// A new connection, even under a peer id seen before, may ask again.
		sender.Forget(5);
		Assert.Equal(0, sender.SentTo(5));
		Assert.Equal(MapRequestAnswer.Sending, sender.Request(5, sender.Hash));
		sender.Tick(tick);
		Assert.Equal((5, 0), Assert.Single(tick));

		// A peer gone in the middle is sent nothing more.
		sender.Forget(5);
		sender.Tick(tick);
		Assert.Empty(tick);
	}

	[Fact]
	public void TheSender_SendsTheBytesItLoaded_NotWhatTheirArrayHoldsLater()
	{
		byte[] file = Generated(1);
		byte[] original = (byte[])file.Clone();
		var sender = new MapSender(file);
		Array.Fill(file, (byte)0xA5);

		var received = new List<byte>();
		for (int i = 0; i < sender.Chunks; i++)
		{
			received.AddRange(sender.Packet(i)[4..]);
		}

		Assert.Equal(original, received.ToArray());
		Assert.Equal(MapFile.HashHex(original), sender.Hash);
	}

	[Fact]
	public void TheSender_SendsAChunkATickToAPeer_AndAtMostFourATick_ThePeersInTurn()
	{
		byte[] file = Generated(1);
		var sender = new MapSender(file);
		int[] peers = { 11, 12, 13, 14, 15, 16 };
		foreach (int peer in peers)
		{
			Assert.Equal(MapRequestAnswer.Sending, sender.Request(peer, sender.Hash));
		}

		var tick = new List<(int Peer, int Index)>();
		var next = new Dictionary<int, int>();
		var last = new Dictionary<int, int>();
		int widestGap = 0;
		int ticks = 0;
		while (sender.Sending > 0)
		{
			sender.Tick(tick);
			ticks++;
			Assert.InRange(tick.Count, 1, MapTransfer.MaxChunksPerTick);
			var seen = new HashSet<int>();
			foreach ((int peer, int index) in tick)
			{
				Assert.True(seen.Add(peer), $"peer {peer} was sent two chunks in tick {ticks}");
				Assert.Equal(next.GetValueOrDefault(peer), index);
				next[peer] = index + 1;
				if (last.TryGetValue(peer, out int before))
				{
					widestGap = Math.Max(widestGap, ticks - before);
				}

				last[peer] = ticks;
			}
		}

		// Six peers on four a tick: each is served every tick and a half, none waits more than two.
		Assert.Equal(2, widestGap);
		foreach (int peer in peers)
		{
			Assert.Equal(sender.Chunks, next[peer]);
		}

		Assert.Equal((int)Math.Ceiling(peers.Length * sender.Chunks / (double)MapTransfer.MaxChunksPerTick), ticks);
	}

	[Fact]
	public void TheSender_RefusesAnEmptyFile()
	{
		Assert.Throws<ArgumentException>(() => new MapSender(Array.Empty<byte>()));
		Assert.Throws<ArgumentException>(() => new MapSender(null));
	}

	// ---- the receiver: what it is told first -------------------------------------------------

	[Theory]
	[InlineData(0, "bytes long")]
	[InlineData(-1, "bytes long")]
	[InlineData(MapLimits.MaxFileBytes + 1, "over the")]
	public void TheReceiver_RefusesALengthNoMapCanBe(int length, string because)
	{
		string hash = MapFile.HashHex(Generated(1));
		Assert.False(MapReceiver.TryStart(hash, length, out MapReceiver receiver, out string error));
		Assert.Null(receiver);
		Assert.Contains(because, error);
		Assert.True(MapReceiver.TryStart(hash, MapLimits.MaxFileBytes, out _, out _));
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("hills")]
	[InlineData("6D0AA3FF6EBE3487E53D4F4488318B2EBE47ADA2E27FB3AE568320A4A8450958")]
	public void TheReceiver_RefusesANameThatIsNotAHash(string hash)
	{
		Assert.False(MapReceiver.TryStart(hash, 1000, out MapReceiver receiver, out string error));
		Assert.Null(receiver);
		Assert.Contains("not a hash", error);
	}

	// ---- the receiver: what arrives ----------------------------------------------------------

	[Fact]
	public void AChunkOutOfTurn_IsRefused_WhetherAheadOrRepeated()
	{
		byte[] file = Generated(1);
		List<byte[]> packets = Packets(file);

		MapReceiver ahead = Start(file);
		ahead.Accept(packets[0]);
		Assert.Equal(MapReceiveState.Refused, ahead.Accept(packets[2]));
		AssertKeptNothing(ahead, "chunk 2 arrived out of turn: 1 was next");

		MapReceiver repeated = Start(file);
		repeated.Accept(packets[0]);
		repeated.Accept(packets[1]);
		Assert.Equal(MapReceiveState.Refused, repeated.Accept(packets[1]));
		AssertKeptNothing(repeated, "chunk 1 arrived out of turn: 2 was next");

		MapReceiver backwards = Start(file);
		Assert.Equal(MapReceiveState.Refused, backwards.Accept(packets[^1]));
		AssertKeptNothing(backwards, "out of turn: 0 was next");
	}

	[Fact]
	public void AChunkPastTheLength_IsRefused()
	{
		byte[] file = Generated(1);
		MapReceiver receiver = Start(file);
		var packet = new byte[8];
		BinaryPrimitives.WriteUInt32LittleEndian(packet, (uint)receiver.Chunks);
		Assert.Equal(MapReceiveState.Refused, receiver.Accept(packet));
		AssertKeptNothing(receiver, $"chunk {receiver.Chunks} is past the map's {receiver.Chunks}");

		MapReceiver far = Start(file);
		BinaryPrimitives.WriteUInt32LittleEndian(packet, uint.MaxValue);
		far.Accept(packet);
		AssertKeptNothing(far, "past the map's");
	}

	[Fact]
	public void AChunkOfTheWrongSize_IsRefused_ShortOrLong_InTheMiddleOrAtTheEnd()
	{
		byte[] file = Generated(1);
		List<byte[]> packets = Packets(file);

		MapReceiver shortened = Start(file);
		shortened.Accept(packets[0][..^1]);
		AssertKeptNothing(shortened, "chunk 0 is 1023 B, not 1024");

		// The last chunk carries what is left, and no more: a file one byte longer than announced.
		MapReceiver longer = Start(file);
		for (int i = 0; i < packets.Count - 1; i++)
		{
			longer.Accept(packets[i]);
		}

		byte[] last = packets[^1];
		var padded = new byte[last.Length + 1];
		last.CopyTo(padded, 0);
		Assert.Equal(MapReceiveState.Refused, longer.Accept(padded));
		AssertKeptNothing(longer, $"is {last.Length - 3} B, not {last.Length - 4}");
	}

	[Theory]
	[InlineData(0)]
	[InlineData(3)]
	[InlineData(4)]
	[InlineData(1029)]
	public void AMessageThatIsNotAChunk_IsRefused(int length)
	{
		MapReceiver receiver = Start(Generated(1));
		Assert.Equal(MapReceiveState.Refused, receiver.Accept(new byte[length]));
		AssertKeptNothing(receiver, $"a message of {length} B is not a chunk");
	}

	[Fact]
	public void AStreamThatStallsForTenSeconds_IsRefused_AndAChunkStartsTheClockAgain()
	{
		byte[] file = Generated(1);
		List<byte[]> packets = Packets(file);
		MapReceiver receiver = Start(file);

		// Ten seconds before the first chunk is a stall too.
		Assert.Equal(MapReceiveState.Receiving, receiver.Advance(9.9));
		receiver.Accept(packets[0]);
		Assert.Equal(MapReceiveState.Receiving, receiver.Advance(9.9));
		receiver.Accept(packets[1]);
		for (int i = 0; i < 599; i++)
		{
			Assert.Equal(MapReceiveState.Receiving, receiver.Advance(1.0 / 60.0));
		}

		Assert.Equal(MapReceiveState.Refused, receiver.Advance(1.0 / 60.0));
		AssertKeptNothing(receiver, $"the map stopped arriving: 2 of {packets.Count} chunks, then nothing for 10 s");

		// Time going backwards, or not at all, or not a number, is no time: it neither holds the
		// clock back nor stops it.
		MapReceiver still = Start(file);
		still.Advance(-100);
		still.Advance(double.NaN);
		still.Advance(0);
		Assert.Equal(MapReceiveState.Receiving, still.Advance(9.99));
		Assert.Equal(MapReceiveState.Refused, still.Advance(0.02));
	}

	[Fact]
	public void AFetchCutOffAtAnyChunk_NeverCompletes_AndIsRefusedWhenItStalls()
	{
		byte[] file = Generated(2);
		List<byte[]> packets = Packets(file);
		for (int cut = 0; cut < packets.Count; cut++)
		{
			MapReceiver receiver = Start(file);
			for (int i = 0; i < cut; i++)
			{
				Assert.Equal(MapReceiveState.Receiving, receiver.Accept(packets[i]));
			}

			Assert.Null(receiver.File);
			Assert.Equal(MapReceiveState.Refused, receiver.Advance(MapTransfer.StallSeconds));
			AssertKeptNothing(receiver, $"{cut} of {packets.Count} chunks");
		}
	}

	[Fact]
	public void AStreamWhoseBytesDoNotHashToWhatWasNamed_IsRefused_ByteByByte()
	{
		byte[] file = Generated(1);
		string hash = MapFile.HashHex(file);
		var random = new Random(7);
		for (int trial = 0; trial < 40; trial++)
		{
			byte[] changed = (byte[])file.Clone();
			int at = trial == 0 ? changed.Length - 1 : random.Next(changed.Length);
			changed[at] ^= (byte)(1 << random.Next(8));
			MapReceiver receiver = Start(hash, file.Length);
			foreach (byte[] packet in Packets(changed))
			{
				receiver.Accept(packet);
			}

			AssertKeptNothing(receiver, $"hash to {MapFile.HashHex(changed)[..8]}…, not the {hash[..8]}… the host named");
		}

		// Another whole map, of another length, named by the first's hash and length: refused on its size.
		byte[] other = Generated(2);
		MapReceiver swapped = Start(hash, file.Length);
		foreach (byte[] packet in Packets(other))
		{
			if (swapped.Accept(packet) != MapReceiveState.Receiving)
			{
				break;
			}
		}

		Assert.Equal(MapReceiveState.Refused, swapped.State);
		Assert.Null(swapped.File);
	}

	[Fact]
	public void BytesThatHashRightButAreNotAMap_AreRefused()
	{
		byte[] noise = new byte[3000];
		new Random(3).NextBytes(noise);
		MapReceiver receiver = Start(noise);
		foreach (byte[] packet in Packets(noise))
		{
			receiver.Accept(packet);
		}

		AssertKeptNothing(receiver, "the host's map does not read: ");
	}

	[Fact]
	public void AMapThisBuildCannotPlay_IsRefused_WithTheValidatorsReason()
	{
		MapDocument document = LevelMaps.Generate(1);
		document.Objects.Add(new MapObject(999, 0, 0, 0, 0));
		byte[] file = FileOf(document);
		MapReceiver receiver = Start(file);
		foreach (byte[] packet in Packets(file))
		{
			receiver.Accept(packet);
		}

		AssertKeptNothing(receiver, "does not play on this build: ");
		Assert.Contains("needs a newer build", receiver.Error);
	}

	[Fact]
	public void ARefusal_IsFinal_AndAFetchThatIsDone_TakesNothingMore()
	{
		byte[] file = Generated(1);
		List<byte[]> packets = Packets(file);

		MapReceiver refused = Start(file);
		refused.Accept(packets[1]);
		string why = refused.Error;
		foreach (byte[] packet in packets)
		{
			Assert.Equal(MapReceiveState.Refused, refused.Accept(packet));
		}

		Assert.Equal(MapReceiveState.Refused, refused.Advance(100));
		Assert.Equal(why, refused.Error);
		Assert.Null(refused.File);

		MapReceiver done = Start(file);
		foreach (byte[] packet in packets)
		{
			done.Accept(packet);
		}

		Assert.Equal(MapReceiveState.Complete, done.Accept(packets[0]));
		Assert.Equal(MapReceiveState.Complete, done.Advance(100));
		Assert.Equal(file, done.File);
	}

	[Fact]
	public void TheReceiver_NeverThrows_OnNoise()
	{
		byte[] file = Generated(1);
		var random = new Random(11);
		for (int trial = 0; trial < 2_000; trial++)
		{
			MapReceiver receiver = Start(file);
			int messages = random.Next(1, 40);
			for (int m = 0; m < messages && receiver.State == MapReceiveState.Receiving; m++)
			{
				byte[] packet = new byte[random.Next(0, 1100)];
				random.NextBytes(packet);
				if (packet.Length >= 4 && random.Next(2) == 0)
				{
					BinaryPrimitives.WriteUInt32LittleEndian(packet, (uint)receiver.Received);
				}

				receiver.Accept(packet);
				receiver.Advance(random.NextDouble());
			}

			Assert.NotEqual(MapReceiveState.Complete, receiver.State);
			Assert.True(receiver.State == MapReceiveState.Receiving || receiver.Error != null);
		}
	}

	// ---- an upload's proof (§6.10, LE8) ---------------------------------------------------------

	private const string Token = "correct-horse-battery-staple";

	private static byte[] Nonce(int seed)
	{
		var nonce = new byte[MapTransfer.NonceBytes];
		new Random(seed).NextBytes(nonce);
		return nonce;
	}

	[Fact]
	public void AProof_IsHmacSha256_OfTheNonceTheHashAndTheOp_KeyedByTheToken()
	{
		// Worked outside this code, by Python's hmac: the token as UTF-8 the key; the nonce's 16 bytes,
		// the hash's 32, and the op's one, the message.
		byte[] nonce = { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15 };
		const string hash = "a44972c687e19fc7ffddd6292e54c7997ec3979c3b7560537c10a5312c6981d1";
		Assert.Equal(hash, MapFile.HashHex("gdpyr"u8));
		Assert.Equal("b8548dc9814a1429ad9778b7e4cb21035c180d57c285a247fbfe85d821a2e32a",
			Convert.ToHexString(MapProof.Compute(Token, nonce, hash, MapProofOp.Upload)).ToLowerInvariant());
		Assert.Equal("684729bc4a554906664a2c3235adf084c4ed04097e600c869c1fb90926feb13c",
			Convert.ToHexString(MapProof.Compute(Token, nonce, hash, MapProofOp.SetNext)).ToLowerInvariant());

		// And by the framework's, over a message put together here.
		byte[] file = Generated(1);
		string mapHash = MapFile.HashHex(file);
		byte[] random = Nonce(3);
		var message = new List<byte>(random);
		message.AddRange(MapFile.Hash(file));
		message.Add(1);
		using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(Token));
		byte[] proof = MapProof.Compute(Token, random, mapHash, MapProofOp.Upload);
		Assert.Equal(hmac.ComputeHash(message.ToArray()), proof);
		Assert.Equal(32, MapProof.Bytes);
		Assert.Equal(MapProof.Bytes, proof.Length);
		Assert.Equal(1, (int)MapProofOp.Upload);
		Assert.Equal(2, (int)MapProofOp.SetNext);
		Assert.True(MapProof.Verify(Token, random, mapHash, MapProofOp.Upload, proof));
	}

	[Fact]
	public void AProof_DoesNotPass_OnAnotherConnection_ForAnotherFile_ForAnotherOp_OrByAnotherToken()
	{
		string hash = MapFile.HashHex(Generated(1));
		string other = MapFile.HashHex(Generated(2));
		byte[] nonce = Nonce(1);
		byte[] proof = MapProof.Compute(Token, nonce, hash, MapProofOp.Upload);
		Assert.True(MapProof.Verify(Token, nonce, hash, MapProofOp.Upload, proof));

		// Another connection has another nonce: a proof heard on one is not one on the next.
		Assert.False(MapProof.Verify(Token, Nonce(2), hash, MapProofOp.Upload, proof));
		Assert.False(MapProof.Verify(Token, nonce, other, MapProofOp.Upload, proof));
		Assert.False(MapProof.Verify(Token, nonce, hash, MapProofOp.SetNext, proof));
		Assert.False(MapProof.Verify(Token + "x", nonce, hash, MapProofOp.Upload, proof));
		Assert.False(MapProof.Verify(Token.ToUpperInvariant(), nonce, hash, MapProofOp.Upload, proof));

		// A nonce one bit away, and a proof one bit away, anywhere in it.
		for (int bit = 0; bit < MapTransfer.NonceBytes * 8; bit++)
		{
			byte[] near = (byte[])nonce.Clone();
			near[bit / 8] ^= (byte)(1 << (bit % 8));
			Assert.False(MapProof.Verify(Token, near, hash, MapProofOp.Upload, proof));
		}

		for (int bit = 0; bit < MapProof.Bytes * 8; bit++)
		{
			byte[] near = (byte[])proof.Clone();
			near[bit / 8] ^= (byte)(1 << (bit % 8));
			Assert.False(MapProof.Verify(Token, nonce, hash, MapProofOp.Upload, near));
		}
	}

	[Fact]
	public void WhatNoProofCanBeMadeFrom_HasNone_AndAProofOfAnotherLengthIsNotOne()
	{
		string hash = MapFile.HashHex(Generated(1));
		byte[] nonce = Nonce(1);
		byte[] proof = MapProof.Compute(Token, nonce, hash, MapProofOp.Upload);

		Assert.Null(MapProof.Compute(null, nonce, hash, MapProofOp.Upload));
		Assert.Null(MapProof.Compute(string.Empty, nonce, hash, MapProofOp.Upload));
		Assert.Null(MapProof.Compute(Token, nonce.AsSpan(0, 15), hash, MapProofOp.Upload));
		Assert.Null(MapProof.Compute(Token, new byte[17], hash, MapProofOp.Upload));
		Assert.Null(MapProof.Compute(Token, ReadOnlySpan<byte>.Empty, hash, MapProofOp.Upload));
		Assert.Null(MapProof.Compute(Token, nonce, hash.ToUpperInvariant(), MapProofOp.Upload));
		Assert.Null(MapProof.Compute(Token, nonce, hash[..63], MapProofOp.Upload));
		Assert.Null(MapProof.Compute(Token, nonce, null, MapProofOp.Upload));

		Assert.False(MapProof.Verify(null, nonce, hash, MapProofOp.Upload, proof));
		Assert.False(MapProof.Verify(string.Empty, nonce, hash, MapProofOp.Upload, proof));
		Assert.False(MapProof.Verify(Token, nonce.AsSpan(0, 15), hash, MapProofOp.Upload, proof));
		Assert.False(MapProof.Verify(Token, nonce, "hills", MapProofOp.Upload, proof));
		Assert.False(MapProof.Verify(Token, nonce, hash, MapProofOp.Upload, proof.AsSpan(0, 31)));
		Assert.False(MapProof.Verify(Token, nonce, hash, MapProofOp.Upload, ReadOnlySpan<byte>.Empty));
		var longer = new byte[33];
		proof.CopyTo(longer, 0);
		Assert.False(MapProof.Verify(Token, nonce, hash, MapProofOp.Upload, longer));

		// An empty key's HMAC is a number like any other: with no token there is no proof to match it.
		using var keyless = new HMACSHA256(Array.Empty<byte>());
		var message = new List<byte>(nonce);
		message.AddRange(Convert.FromHexString(hash));
		message.Add(1);
		Assert.False(MapProof.Verify(string.Empty, nonce, hash, MapProofOp.Upload, keyless.ComputeHash(message.ToArray())));
		Assert.False(MapProof.Verify(null, nonce, hash, MapProofOp.Upload, keyless.ComputeHash(message.ToArray())));
	}

	[Theory]
	[InlineData("0123456789abcdef", true)]
	[InlineData("correct-horse-battery-staple", true)]
	[InlineData("0123456789abcde", false)]
	[InlineData("hunter2", false)]
	[InlineData("", false)]
	[InlineData(null, false)]
	[InlineData("0123456789 abcdef", false)]
	[InlineData("0123456789abcdef\n", false)]
	[InlineData("0123456789\tabcdef", false)]
	public void AToken_IsSixteenCharactersOrMore_WithNoSpaceInIt(string token, bool taken)
	{
		Assert.Equal(taken, MapProof.IsToken(token, out string error));
		Assert.Equal(taken, error == null);
		Assert.Equal(16, MapProof.MinTokenLength);
		Assert.True(MapProof.IsToken(new string('k', MapProof.MaxTokenLength), out _));
		Assert.False(MapProof.IsToken(new string('k', MapProof.MaxTokenLength + 1), out _));
	}
}
