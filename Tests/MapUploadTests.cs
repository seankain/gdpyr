using System;
using System.Collections.Generic;
using Gdpyr.Sim.Level;
using Xunit;

namespace Gdpyr.Tests;

/// <summary>
/// A map from a client to a dedicated server, and the server's next map (docs/LEVEL_EDITOR.md
/// §9.5, LE8): the server's gate, the client's uploader and the rules of <c>maps/active</c>, with no
/// socket and no disk in them. A server takes no map from anyone without the token (§12).
/// </summary>
public class MapUploadTests
{
	private const string Token = "correct-horse-battery-staple";
	private const string OtherToken = "another-token-altogether";

	private static byte[] FileOf(MapDocument document)
	{
		Assert.True(MapFile.TryWrite(document, out byte[] file, out string error), error);
		return file;
	}

	private static byte[] Generated(int seed) => FileOf(LevelMaps.Generate(seed));

	private static byte[] Nonce(int seed)
	{
		var nonce = new byte[MapTransfer.NonceBytes];
		new Random(seed).NextBytes(nonce);
		return nonce;
	}

	private static byte[] Proof(byte[] nonce, string hash, MapProofOp op = MapProofOp.Upload, string token = Token) =>
		MapProof.Compute(token, nonce, hash, op);

	private static List<byte[]> Packets(byte[] file)
	{
		var packets = new List<byte[]>();
		for (int i = 0; i < MapTransfer.ChunkCount(file.Length); i++)
		{
			packets.Add(MapTransfer.Packet(file, i));
		}

		return packets;
	}

	private static readonly Func<string, bool> HasNone = _ => false;
	private static readonly Func<string, bool> HasAll = _ => true;

	/// <summary>An offer of a file, rightly proved, on a connection of its own.</summary>
	private static MapOfferAnswer Offer(MapUploadGate gate, int peer, byte[] file, Func<string, bool> has = null)
	{
		byte[] nonce = Nonce(peer);
		string hash = MapFile.HashHex(file);
		return gate.Offer(peer, nonce, hash, file.Length, Proof(nonce, hash), has ?? HasNone);
	}

	// ---- the answers, as the wire and §9.5 have them ---------------------------------------------

	[Fact]
	public void AnOffersAnswers_AreSection9_5s_AndTheirNumbersAreTheWires()
	{
		Assert.Equal("ok", MapAnswers.Name(MapOfferAnswer.Ok));
		Assert.Equal("have_it", MapAnswers.Name(MapOfferAnswer.HaveIt));
		Assert.Equal("uploads_off", MapAnswers.Name(MapOfferAnswer.UploadsOff));
		Assert.Equal("bad_proof", MapAnswers.Name(MapOfferAnswer.BadProof));
		Assert.Equal("too_large", MapAnswers.Name(MapOfferAnswer.TooLarge));
		Assert.Equal("busy", MapAnswers.Name(MapOfferAnswer.Busy));
		Assert.Equal(new[] { 0, 1, 2, 3, 4, 5 }, new[]
		{
			(int)MapOfferAnswer.Ok, (int)MapOfferAnswer.HaveIt, (int)MapOfferAnswer.UploadsOff,
			(int)MapOfferAnswer.BadProof, (int)MapOfferAnswer.TooLarge, (int)MapOfferAnswer.Busy,
		});

		for (int wire = 0; wire <= 5; wire++)
		{
			Assert.True(MapAnswers.TryOffer(wire, out MapOfferAnswer answer));
			Assert.Equal(wire, (int)answer);
			Assert.DoesNotContain("does not know", MapAnswers.Describe(answer));
		}

		Assert.False(MapAnswers.TryOffer(-1, out _));
		Assert.False(MapAnswers.TryOffer(6, out _));
		Assert.False(MapAnswers.TryOffer(int.MaxValue, out _));
		Assert.Contains("does not know (6)", MapAnswers.Describe((MapOfferAnswer)6));
		Assert.Contains("does not know (-1)", MapAnswers.Unknown(-1));
		Assert.Contains("--map-upload-token", MapAnswers.Describe(MapOfferAnswer.UploadsOff));
		Assert.Contains("token", MapAnswers.Describe(MapOfferAnswer.BadProof));
		Assert.Contains("1,048,576", MapAnswers.Describe(MapOfferAnswer.TooLarge));
	}

	[Fact]
	public void ANextMapsAnswers_HaveTheirNamesAndNumbers_AndThreeOfThemAreTaken()
	{
		var names = new Dictionary<MapNextAnswer, string>
		{
			[MapNextAnswer.Ok] = "ok",
			[MapNextAnswer.OkStaysDown] = "ok_stays_down",
			[MapNextAnswer.Playing] = "playing",
			[MapNextAnswer.UploadsOff] = "uploads_off",
			[MapNextAnswer.BadProof] = "bad_proof",
			[MapNextAnswer.NotHere] = "not_here",
			[MapNextAnswer.Pinned] = "pinned",
			[MapNextAnswer.NotWritten] = "not_written",
		};
		int wire = 0;
		foreach ((MapNextAnswer answer, string name) in names)
		{
			Assert.Equal(wire, (int)answer);
			Assert.Equal(name, MapAnswers.Name(answer));
			Assert.True(MapAnswers.TryNext(wire, out MapNextAnswer parsed));
			Assert.Equal(answer, parsed);
			Assert.Equal(wire <= 2, MapAnswers.Accepted(answer));
			Assert.DoesNotContain("does not know", MapAnswers.Describe(answer));
			wire++;
		}

		Assert.False(MapAnswers.TryNext(-1, out _));
		Assert.False(MapAnswers.TryNext(8, out _));
		Assert.Contains("does not know (8)", MapAnswers.Describe((MapNextAnswer)8));

		// A server run by hand stays down, and says so when it accepts the request (§9.5).
		Assert.Contains("nothing there starts it again", MapAnswers.Describe(MapNextAnswer.OkStaysDown));
		Assert.Contains("joins again", MapAnswers.Describe(MapNextAnswer.Ok));
		Assert.Contains("--map", MapAnswers.Describe(MapNextAnswer.Pinned));
	}

	[Fact]
	public void AServersOwnWords_AreShownAsOneLine_OfAtMost240Characters()
	{
		Assert.Equal(string.Empty, MapAnswers.Clean(null));
		Assert.Equal(string.Empty, MapAnswers.Clean(string.Empty));
		Assert.Equal("chunk 3 arrived out of turn", MapAnswers.Clean("chunk 3 arrived out of turn"));
		Assert.Equal("one two  three ", MapAnswers.Clean("one\ntwo\r\nthree\u001b"));

		string exactly = new('a', MapAnswers.MaxReasonLength);
		Assert.Equal(exactly, MapAnswers.Clean(exactly));
		string clipped = MapAnswers.Clean(exactly + "b");
		Assert.Equal(240, MapAnswers.MaxReasonLength);
		Assert.Equal(MapAnswers.MaxReasonLength, clipped.Length);
		Assert.EndsWith("a…", clipped);
		Assert.Equal(MapAnswers.MaxReasonLength, MapAnswers.Clean(new string('\n', 5000)).Length);
	}

	// ---- the gate: who is answered what -------------------------------------------------------------

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	public void AServerWithNoToken_TakesNoMapFromAnyone_AndIsToldNothing(string token)
	{
		var gate = new MapUploadGate(token);
		Assert.False(gate.Enabled);

		byte[] file = Generated(1);
		string hash = MapFile.HashHex(file);
		byte[] nonce = Nonce(1);

		// Whatever is offered, and whatever is said to prove it: by a real token, by none, by an empty one.
		using var keyless = new System.Security.Cryptography.HMACSHA256(Array.Empty<byte>());
		var message = new List<byte>(nonce);
		message.AddRange(Convert.FromHexString(hash));
		message.Add((byte)MapProofOp.Upload);
		foreach (byte[] proof in new[] { Proof(nonce, hash), new byte[MapProof.Bytes], Array.Empty<byte>(), null,
			keyless.ComputeHash(message.ToArray()) })
		{
			Assert.Equal(MapOfferAnswer.UploadsOff, gate.Offer(5, nonce, hash, file.Length, proof, HasNone));
			Assert.Equal(MapOfferAnswer.UploadsOff, gate.Offer(5, nonce, hash, file.Length, proof, HasAll));
			Assert.Equal(MapNextAnswer.UploadsOff, gate.Next(nonce, hash, proof, HasAll, null, pinned: false, supervised: true));
		}

		// Its chunks are dropped: nothing is being taken, from this peer or any.
		Assert.Equal(0, gate.UploaderPeer);
		Assert.Null(gate.Receiving);
		foreach (byte[] packet in Packets(file))
		{
			Assert.False(gate.Chunk(5, packet));
		}

		Assert.Null(gate.Receiving);
		Assert.False(gate.Finished);
		gate.Advance(100);
		Assert.Null(gate.Release(out int peer));
		Assert.Equal(0, peer);
	}

	[Fact]
	public void AnOffer_IsAnsweredInOrder_TheProofBeforeAnythingElseIsSaid()
	{
		var gate = new MapUploadGate(Token);
		Assert.True(gate.Enabled);
		byte[] file = Generated(1);
		string hash = MapFile.HashHex(file);
		byte[] nonce = Nonce(9);
		byte[] proof = Proof(nonce, hash);
		byte[] wrong = Proof(nonce, hash, token: OtherToken);

		// One upload under way, so that an offer could be busy.
		Assert.Equal(MapOfferAnswer.Ok, Offer(gate, 5, Generated(2)));

		// Without the proof, nothing else is learnt: not that it is too long, had already, or busy.
		Assert.Equal(MapOfferAnswer.BadProof, gate.Offer(9, nonce, hash, MapLimits.MaxFileBytes + 1, wrong, HasAll));
		Assert.Equal(MapOfferAnswer.BadProof, gate.Offer(9, nonce, hash, file.Length, wrong, HasAll));
		Assert.Equal(MapOfferAnswer.BadProof, gate.Offer(9, nonce, hash, file.Length, wrong, HasNone));

		// With it: too long, before whether it is had; had, before busy; then busy.
		Assert.Equal(MapOfferAnswer.TooLarge, gate.Offer(9, nonce, hash, MapLimits.MaxFileBytes + 1, proof, HasAll));
		Assert.Equal(MapOfferAnswer.HaveIt, gate.Offer(9, nonce, hash, file.Length, proof, HasAll));
		Assert.Equal(MapOfferAnswer.Busy, gate.Offer(9, nonce, hash, file.Length, proof, HasNone));

		// None of which disturbed the upload under way.
		Assert.Equal(5, gate.UploaderPeer);
		Assert.Equal(0, gate.Receiving.Received);
		Assert.Equal(MapReceiveState.Receiving, gate.Receiving.State);
	}

	[Fact]
	public void AWrongToken_AndAReplayedProof_AreBadProof()
	{
		var gate = new MapUploadGate(Token);
		byte[] file = Generated(1);
		string hash = MapFile.HashHex(file);
		string other = MapFile.HashHex(Generated(2));
		byte[] nonce = Nonce(1);
		byte[] proof = Proof(nonce, hash);

		// By another token; heard on another connection; made for another file; made for the other op;
		// cut short; nothing at all.
		Assert.Equal(MapOfferAnswer.BadProof, gate.Offer(5, nonce, hash, file.Length, Proof(nonce, hash, token: OtherToken), HasNone));
		Assert.Equal(MapOfferAnswer.BadProof, gate.Offer(5, Nonce(2), hash, file.Length, proof, HasNone));
		Assert.Equal(MapOfferAnswer.BadProof, gate.Offer(5, nonce, other, file.Length, proof, HasNone));
		Assert.Equal(MapOfferAnswer.BadProof, gate.Offer(5, nonce, hash, file.Length, Proof(nonce, hash, MapProofOp.SetNext), HasNone));
		Assert.Equal(MapOfferAnswer.BadProof, gate.Offer(5, nonce, hash, file.Length, proof.AsSpan(0, 31), HasNone));
		Assert.Equal(MapOfferAnswer.BadProof, gate.Offer(5, nonce, hash, file.Length, null, HasNone));

		// A connection whose nonce the server does not have, and a map named by no hash.
		Assert.Equal(MapOfferAnswer.BadProof, gate.Offer(5, null, hash, file.Length, proof, HasNone));
		Assert.Equal(MapOfferAnswer.BadProof, gate.Offer(5, nonce, hash.ToUpperInvariant(), file.Length, proof, HasNone));
		Assert.Equal(MapOfferAnswer.BadProof, gate.Offer(5, nonce, null, file.Length, proof, HasNone));
		Assert.Null(gate.Receiving);

		// The proof itself, on its own connection, for its own file: taken.
		Assert.Equal(MapOfferAnswer.Ok, gate.Offer(5, nonce, hash, file.Length, proof, HasNone));
	}

	[Theory]
	[InlineData(0, MapOfferAnswer.TooLarge)]
	[InlineData(-1, MapOfferAnswer.TooLarge)]
	[InlineData(int.MinValue, MapOfferAnswer.TooLarge)]
	[InlineData(MapLimits.MaxFileBytes + 1, MapOfferAnswer.TooLarge)]
	[InlineData(int.MaxValue, MapOfferAnswer.TooLarge)]
	[InlineData(MapLimits.MaxFileBytes, MapOfferAnswer.Ok)]
	[InlineData(1, MapOfferAnswer.Ok)]
	public void ALengthNoMapCanBe_IsTooLarge(int length, MapOfferAnswer expected)
	{
		var gate = new MapUploadGate(Token);
		string hash = MapFile.HashHex(Generated(1));
		byte[] nonce = Nonce(1);
		Assert.Equal(expected, gate.Offer(5, nonce, hash, length, Proof(nonce, hash), HasNone));
		Assert.Equal(expected == MapOfferAnswer.Ok, gate.Receiving != null);
		if (expected == MapOfferAnswer.Ok)
		{
			Assert.Equal(length, gate.Receiving.Length);
			Assert.Equal(hash, gate.Receiving.Hash);
		}
	}

	[Fact]
	public void AMapTheServerHas_IsNotSentAgain()
	{
		var gate = new MapUploadGate(Token);
		byte[] file = Generated(1);
		string hash = MapFile.HashHex(file);
		var asked = new List<string>();
		Assert.Equal(MapOfferAnswer.HaveIt, Offer(gate, 5, file, h =>
		{
			asked.Add(h);
			return h == hash;
		}));
		Assert.Equal(hash, Assert.Single(asked));
		Assert.Null(gate.Receiving);
		Assert.Equal(0, gate.UploaderPeer);

		// One it has not: taken. With nobody to ask what it has, it has nothing.
		Assert.Equal(MapOfferAnswer.Ok, Offer(gate, 5, Generated(2), h => h == hash));
		var other = new MapUploadGate(Token);
		byte[] nonce = Nonce(1);
		Assert.Equal(MapOfferAnswer.Ok, other.Offer(5, nonce, hash, file.Length, Proof(nonce, hash), null));
	}

	// ---- the gate: whose chunks are taken ----------------------------------------------------------

	[Fact]
	public void ItTakesOneUploadAtATime_FromThePeerWhoseOfferItTook()
	{
		var gate = new MapUploadGate(Token);
		byte[] file = Generated(1);
		byte[] second = Generated(2);
		List<byte[]> packets = Packets(file);

		Assert.Equal(MapOfferAnswer.Ok, Offer(gate, 5, file));
		Assert.Equal(5, gate.UploaderPeer);
		Assert.Equal(MapFile.HashHex(file), gate.Receiving.Hash);

		// Another peer's offer, and this peer's own second, wait their turn.
		Assert.Equal(MapOfferAnswer.Busy, Offer(gate, 6, second));
		Assert.Equal(MapOfferAnswer.Busy, Offer(gate, 5, second));
		Assert.Equal(MapOfferAnswer.Busy, Offer(gate, 5, file));

		// Another peer's chunks are dropped, right as they are; so are peer 0's.
		Assert.True(gate.Chunk(5, packets[0]));
		Assert.False(gate.Chunk(6, packets[1]));
		Assert.False(gate.Chunk(0, packets[1]));
		Assert.False(gate.Chunk(-5, packets[1]));
		Assert.Equal(1, gate.Receiving.Received);
		Assert.Equal(MapReceiveState.Receiving, gate.Receiving.State);
		Assert.False(gate.Finished);

		for (int i = 1; i < packets.Count; i++)
		{
			Assert.True(gate.Chunk(5, packets[i]));
		}

		Assert.True(gate.Finished);
		Assert.Equal(MapReceiveState.Complete, gate.Receiving.State);

		// Whole, it takes no more chunks; and it is still the one upload until it is let go of.
		Assert.False(gate.Chunk(5, packets[0]));
		Assert.Equal(MapOfferAnswer.Busy, Offer(gate, 6, second));

		MapReceiver done = gate.Release(out int peer);
		Assert.Equal(5, peer);
		Assert.Equal(file, done.File);
		Assert.Equal(0, gate.UploaderPeer);
		Assert.Null(gate.Receiving);
		Assert.False(gate.Finished);
		Assert.Equal(MapOfferAnswer.Ok, Offer(gate, 6, second));
		Assert.Equal(6, gate.UploaderPeer);
	}

	[Fact]
	public void AChunkWithNoOfferTaken_IsDropped()
	{
		var gate = new MapUploadGate(Token);
		byte[] file = Generated(1);
		foreach (byte[] packet in Packets(file))
		{
			Assert.False(gate.Chunk(5, packet));
		}

		Assert.Null(gate.Receiving);

		// And after an offer that was refused.
		byte[] nonce = Nonce(5);
		string hash = MapFile.HashHex(file);
		Assert.Equal(MapOfferAnswer.BadProof, gate.Offer(5, nonce, hash, file.Length, Proof(nonce, hash, token: OtherToken), HasNone));
		Assert.False(gate.Chunk(5, Packets(file)[0]));
		Assert.Null(gate.Receiving);
	}

	[Fact]
	public void AnUpload_IsHashedReadAndValidated_AndARefusedOneKeepsNothing()
	{
		// Bytes that do not hash to what was offered.
		byte[] file = Generated(1);
		byte[] changed = (byte[])file.Clone();
		changed[changed.Length / 2] ^= 0x10;
		var gate = new MapUploadGate(Token);
		Assert.Equal(MapOfferAnswer.Ok, Offer(gate, 5, file));
		foreach (byte[] packet in Packets(changed))
		{
			gate.Chunk(5, packet);
		}

		Assert.True(gate.Finished);
		MapReceiver refused = gate.Release(out _);
		Assert.Equal(MapReceiveState.Refused, refused.State);
		Assert.Contains($"hash to {MapFile.HashHex(changed)[..8]}…, not the {MapFile.HashHex(file)[..8]}… the uploader named", refused.Error);
		Assert.Null(refused.File);
		Assert.Null(refused.Document);

		// Bytes that hash right and are not a map.
		byte[] noise = new byte[3000];
		new Random(3).NextBytes(noise);
		Assert.Equal(MapOfferAnswer.Ok, Offer(gate, 5, noise));
		foreach (byte[] packet in Packets(noise))
		{
			gate.Chunk(5, packet);
		}

		refused = gate.Release(out _);
		Assert.Equal(MapReceiveState.Refused, refused.State);
		Assert.Contains("the uploader's map does not read: ", refused.Error);
		Assert.Null(refused.File);

		// A map this build cannot play.
		MapDocument document = LevelMaps.Generate(1);
		document.Objects.Add(new MapObject(999, 0, 0, 0, 0));
		byte[] unplayable = FileOf(document);
		Assert.Equal(MapOfferAnswer.Ok, Offer(gate, 5, unplayable));
		foreach (byte[] packet in Packets(unplayable))
		{
			gate.Chunk(5, packet);
		}

		refused = gate.Release(out _);
		Assert.Equal(MapReceiveState.Refused, refused.State);
		Assert.Contains("the uploader's map does not play on this build: ", refused.Error);
		Assert.Contains("needs a newer build", refused.Error);
		Assert.Null(refused.File);

		// A chunk out of turn ends it there.
		Assert.Equal(MapOfferAnswer.Ok, Offer(gate, 5, file));
		Assert.True(gate.Chunk(5, Packets(file)[1]));
		Assert.True(gate.Finished);
		Assert.Contains("out of turn", gate.Release(out _).Error);

		// A fetch's refusal still names the host.
		Assert.True(MapReceiver.TryStart(MapFile.HashHex(noise), noise.Length, out MapReceiver fetch, out _));
		foreach (byte[] packet in Packets(noise))
		{
			fetch.Accept(packet);
		}

		Assert.Contains("the host's map does not read: ", fetch.Error);
		Assert.Equal("the host", MapReceiver.Host);
		Assert.Equal("the uploader", MapReceiver.Uploader);
	}

	[Fact]
	public void AnUploadThatStalls_IsRefusedAfterTenSeconds_AndTheNextOfferIsTaken()
	{
		var gate = new MapUploadGate(Token);
		byte[] file = Generated(1);
		List<byte[]> packets = Packets(file);
		Assert.Equal(MapOfferAnswer.Ok, Offer(gate, 5, file));
		gate.Chunk(5, packets[0]);
		gate.Advance(9.5);
		Assert.False(gate.Finished);
		Assert.Equal(MapOfferAnswer.Busy, Offer(gate, 6, Generated(2)));

		// A chunk starts the clock again.
		gate.Chunk(5, packets[1]);
		gate.Advance(9.5);
		Assert.False(gate.Finished);
		gate.Advance(0.5);
		Assert.True(gate.Finished);
		Assert.Equal(MapReceiveState.Refused, gate.Receiving.State);
		Assert.Contains("stopped arriving: 2 of", gate.Receiving.Error);

		// Refused, it takes no more of that upload; let go of, it takes the next.
		Assert.False(gate.Chunk(5, packets[2]));
		gate.Release(out int peer);
		Assert.Equal(5, peer);
		Assert.Equal(MapOfferAnswer.Ok, Offer(gate, 6, Generated(2)));

		// Time with nothing being taken is nothing.
		var idle = new MapUploadGate(Token);
		idle.Advance(1000);
		Assert.False(idle.Finished);
	}

	[Fact]
	public void APeerGone_TakesItsUploadWithIt_AndNobodyElses()
	{
		var gate = new MapUploadGate(Token);
		byte[] file = Generated(1);
		Assert.Equal(MapOfferAnswer.Ok, Offer(gate, 5, file));
		gate.Chunk(5, Packets(file)[0]);

		Assert.False(gate.Forget(6));
		Assert.False(gate.Forget(0));
		Assert.Equal(5, gate.UploaderPeer);
		Assert.Equal(1, gate.Receiving.Received);

		Assert.True(gate.Forget(5));
		Assert.Equal(0, gate.UploaderPeer);
		Assert.Null(gate.Receiving);
		Assert.False(gate.Forget(5));
		Assert.False(gate.Forget(0));

		// A new connection, even under the same peer id, starts again from the first chunk.
		Assert.False(gate.Chunk(5, Packets(file)[1]));
		Assert.Equal(MapOfferAnswer.Ok, Offer(gate, 5, file));
		Assert.Equal(0, gate.Receiving.Received);
	}

	[Fact]
	public void TheGate_NeverThrows_OnNoise()
	{
		var random = new Random(17);
		byte[] file = Generated(1);
		string hash = MapFile.HashHex(file);
		for (int trial = 0; trial < 500; trial++)
		{
			var gate = new MapUploadGate(trial % 5 == 0 ? null : Token);
			for (int m = 0; m < 30; m++)
			{
				int peer = random.Next(-1, 4);
				byte[] bytes = new byte[random.Next(0, 1100)];
				random.NextBytes(bytes);
				byte[] nonce = random.Next(3) == 0 ? bytes : Nonce(peer);
				switch (random.Next(5))
				{
					case 0:
						gate.Offer(peer, nonce, random.Next(2) == 0 ? hash : "x", random.Next(-5, 5000), bytes, HasNone);
						break;
					case 1:
						gate.Offer(peer, nonce, hash, file.Length, Proof(Nonce(peer), hash), HasNone);
						break;
					case 2:
						gate.Chunk(peer, bytes);
						break;
					case 3:
						gate.Next(nonce, hash, bytes, HasAll, null, false, true);
						break;
					default:
						gate.Advance(random.NextDouble() * 4);
						gate.Forget(random.Next(-1, 4));
						break;
				}

				if (gate.Finished)
				{
					Assert.NotEqual(MapReceiveState.Complete, gate.Release(out _).State);
				}
			}
		}
	}

	// ---- the gate: the next map ---------------------------------------------------------------------

	[Fact]
	public void ANextMapRequest_IsAnsweredInOrder_AndOnlyByItsOwnProof()
	{
		var gate = new MapUploadGate(Token);
		string hash = MapFile.HashHex(Generated(1));
		string playing = MapFile.HashHex(Generated(2));
		byte[] nonce = Nonce(1);
		byte[] proof = Proof(nonce, hash, MapProofOp.SetNext);

		// The proof first: by another token, for an upload, heard on another connection, for another map.
		foreach (byte[] wrong in new[] { Proof(nonce, hash, MapProofOp.SetNext, OtherToken), Proof(nonce, hash, MapProofOp.Upload),
			Proof(Nonce(2), hash, MapProofOp.SetNext), Proof(nonce, playing, MapProofOp.SetNext), new byte[MapProof.Bytes], null })
		{
			Assert.Equal(MapNextAnswer.BadProof, gate.Next(nonce, hash, wrong, HasAll, playing, pinned: true, supervised: true));
			Assert.Equal(MapNextAnswer.BadProof, gate.Next(nonce, hash, wrong, HasNone, hash, pinned: false, supervised: false));
		}

		// A server started with --map plays that map whenever it starts: before whether it has this one.
		Assert.Equal(MapNextAnswer.Pinned, gate.Next(nonce, hash, proof, HasNone, playing, pinned: true, supervised: true));
		Assert.Equal(MapNextAnswer.Pinned, gate.Next(nonce, hash, proof, HasAll, hash, pinned: true, supervised: true));

		// A map it has not got, or nobody to say it has.
		Assert.Equal(MapNextAnswer.NotHere, gate.Next(nonce, hash, proof, HasNone, playing, pinned: false, supervised: true));
		Assert.Equal(MapNextAnswer.NotHere, gate.Next(nonce, hash, proof, null, playing, pinned: false, supervised: true));
		Assert.Equal(MapNextAnswer.NotHere, gate.Next(nonce, hash, proof, HasNone, hash, pinned: false, supervised: true));

		// The one being played: nothing to restart for.
		Assert.Equal(MapNextAnswer.Playing, gate.Next(nonce, hash, proof, HasAll, hash, pinned: false, supervised: true));
		Assert.Equal(MapNextAnswer.Playing, gate.Next(nonce, hash, proof, HasAll, hash, pinned: false, supervised: false));

		// Another: taken, and a server nothing restarts says so.
		Assert.Equal(MapNextAnswer.Ok, gate.Next(nonce, hash, proof, HasAll, playing, pinned: false, supervised: true));
		Assert.Equal(MapNextAnswer.OkStaysDown, gate.Next(nonce, hash, proof, HasAll, playing, pinned: false, supervised: false));
		Assert.Equal(MapNextAnswer.Ok, gate.Next(nonce, hash, proof, HasAll, null, pinned: false, supervised: true));

		// It is asked only what it has of the map named.
		var asked = new List<string>();
		gate.Next(nonce, hash, proof, h =>
		{
			asked.Add(h);
			return true;
		}, playing, pinned: false, supervised: true);
		Assert.Equal(hash, Assert.Single(asked));

		// A request takes no upload's place, and an upload under way does not hold one up.
		Assert.Null(gate.Receiving);
		Assert.Equal(MapOfferAnswer.Ok, Offer(gate, 5, Generated(3)));
		Assert.Equal(MapNextAnswer.Ok, gate.Next(nonce, hash, proof, HasAll, playing, pinned: false, supervised: true));
		Assert.Equal(5, gate.UploaderPeer);
	}

	// ---- the uploader ---------------------------------------------------------------------------------

	[Theory]
	[InlineData(1)]
	[InlineData(2)]
	[InlineData(3)]
	public void AMap_OfferedTakenSentAndKept_ArrivesAsTheSameBytesAndTheSameMap(int seed)
	{
		MapDocument document = LevelMaps.Generate(seed);
		byte[] file = FileOf(document);
		var uploader = new MapUploader(file);
		Assert.Equal(MapFile.HashHex(file), uploader.Hash);
		Assert.Equal(file.Length, uploader.Length);
		Assert.Equal(MapTransfer.ChunkCount(file.Length), uploader.Chunks);
		Assert.Equal(MapUploadState.Offered, uploader.State);

		var gate = new MapUploadGate(Token);
		byte[] nonce = Nonce(seed);
		MapOfferAnswer answer = gate.Offer(7, nonce, uploader.Hash, uploader.Length, Proof(nonce, uploader.Hash), HasNone);
		uploader.Answered((int)answer);
		Assert.Equal(MapOfferAnswer.Ok, uploader.Answer);
		Assert.Equal(MapUploadState.Sending, uploader.State);

		int ticks = 0;
		while (uploader.NextPacket() is { } packet)
		{
			Assert.False(gate.Finished);
			Assert.True(gate.Chunk(7, packet));
			ticks++;
		}

		Assert.Equal(uploader.Chunks, ticks);
		Assert.Equal(uploader.Chunks, uploader.Sent);
		Assert.Equal(MapUploadState.Sent, uploader.State);
		Assert.False(uploader.Done);

		Assert.True(gate.Finished);
		MapReceiver received = gate.Release(out int peer);
		Assert.Equal(7, peer);
		Assert.Equal(MapReceiveState.Complete, received.State);
		Assert.Equal(file, received.File);
		Assert.True(received.Document.SameAs(document, out string difference), difference);

		uploader.Kept(received.Hash, string.Empty);
		Assert.Equal(MapUploadState.Stored, uploader.State);
		Assert.True(uploader.Done);
		Assert.True(uploader.ServerHasIt);
		Assert.Null(uploader.Error);
	}

	[Fact]
	public void TheUploader_SendsNothingUntilItsOfferIsTaken_ThenAChunkAtATimeInOrder()
	{
		byte[] file = Generated(1);
		var uploader = new MapUploader(file);
		Assert.Null(uploader.NextPacket());
		Assert.Equal(0, uploader.Sent);
		Assert.Null(uploader.Answer);

		uploader.Answered((int)MapOfferAnswer.Ok);
		for (int i = 0; i < uploader.Chunks; i++)
		{
			Assert.Equal(MapUploadState.Sending, uploader.State);
			Assert.Equal(MapTransfer.Packet(file, i), uploader.NextPacket());
			Assert.Equal(i + 1, uploader.Sent);
		}

		Assert.Equal(MapUploadState.Sent, uploader.State);
		Assert.Null(uploader.NextPacket());
		Assert.Equal(uploader.Chunks, uploader.Sent);
	}

	[Fact]
	public void TheUploader_SendsTheBytesItWasGiven_NotWhatTheirArrayHoldsLater()
	{
		byte[] file = Generated(1);
		byte[] original = (byte[])file.Clone();
		var uploader = new MapUploader(file);
		Array.Fill(file, (byte)0xA5);
		uploader.Answered(0);

		var sent = new List<byte>();
		while (uploader.NextPacket() is { } packet)
		{
			sent.AddRange(packet[MapTransfer.IndexBytes..]);
		}

		Assert.Equal(original, sent.ToArray());
		Assert.Equal(MapFile.HashHex(original), uploader.Hash);
		Assert.Throws<ArgumentException>(() => new MapUploader(Array.Empty<byte>()));
		Assert.Throws<ArgumentException>(() => new MapUploader(null));
	}

	[Fact]
	public void AMapTheServerHas_IsDoneWithNothingSent()
	{
		var uploader = new MapUploader(Generated(1));
		uploader.Answered((int)MapOfferAnswer.HaveIt);
		Assert.Equal(MapUploadState.HaveIt, uploader.State);
		Assert.Equal(MapOfferAnswer.HaveIt, uploader.Answer);
		Assert.True(uploader.Done);
		Assert.True(uploader.ServerHasIt);
		Assert.Null(uploader.Error);
		Assert.Null(uploader.NextPacket());
		Assert.Equal(0, uploader.Sent);
	}

	[Theory]
	[InlineData(MapOfferAnswer.UploadsOff)]
	[InlineData(MapOfferAnswer.BadProof)]
	[InlineData(MapOfferAnswer.TooLarge)]
	[InlineData(MapOfferAnswer.Busy)]
	public void AnOfferNotTaken_IsRefused_WithWhatTheAnswerMeans_AndNothingIsSent(MapOfferAnswer answer)
	{
		var uploader = new MapUploader(Generated(1));
		uploader.Answered((int)answer);
		Assert.Equal(MapUploadState.Refused, uploader.State);
		Assert.Equal(answer, uploader.Answer);
		Assert.Equal(MapAnswers.Describe(answer), uploader.Error);
		Assert.True(uploader.Done);
		Assert.False(uploader.ServerHasIt);
		Assert.Null(uploader.NextPacket());
		Assert.Equal(0, uploader.Sent);
	}

	[Theory]
	[InlineData(-1)]
	[InlineData(6)]
	[InlineData(99)]
	public void AnAnswerThisBuildDoesNotKnow_IsARefusal(int wire)
	{
		var uploader = new MapUploader(Generated(1));
		uploader.Answered(wire);
		Assert.Equal(MapUploadState.Refused, uploader.State);
		Assert.Null(uploader.Answer);
		Assert.Contains($"does not know ({wire})", uploader.Error);
		Assert.Null(uploader.NextPacket());
	}

	[Fact]
	public void OnlyTheFirstAnswerCounts()
	{
		var taken = new MapUploader(Generated(1));
		taken.Answered((int)MapOfferAnswer.Ok);
		taken.Answered((int)MapOfferAnswer.Busy);
		taken.Answered((int)MapOfferAnswer.HaveIt);
		Assert.Equal(MapUploadState.Sending, taken.State);
		Assert.Equal(MapOfferAnswer.Ok, taken.Answer);

		var refused = new MapUploader(Generated(1));
		refused.Answered((int)MapOfferAnswer.BadProof);
		refused.Answered((int)MapOfferAnswer.Ok);
		Assert.Equal(MapUploadState.Refused, refused.State);
		Assert.Equal(MapOfferAnswer.BadProof, refused.Answer);
		Assert.Null(refused.NextPacket());
	}

	[Fact]
	public void WhatTheServerSaysBecameOfIt_IsHeldToWhatWasSent()
	{
		byte[] file = Generated(1);
		string hash = MapFile.HashHex(file);

		// Before the offer is answered, nothing has become of anything.
		var early = new MapUploader(file);
		early.Kept(hash, string.Empty);
		early.Kept(hash, "no");
		Assert.Equal(MapUploadState.Offered, early.State);

		// Refused part of the way through, with the server's reason, as one clean line.
		var cut = new MapUploader(file);
		cut.Answered(0);
		cut.NextPacket();
		cut.Kept(hash, "chunk 3 arrived\nout of turn");
		Assert.Equal(MapUploadState.Refused, cut.State);
		Assert.Equal("the server did not keep it: chunk 3 arrived out of turn", cut.Error);
		Assert.Null(cut.NextPacket());

		// Said to be kept before it was all sent.
		var premature = new MapUploader(file);
		premature.Answered(0);
		premature.NextPacket();
		premature.Kept(hash, null);
		Assert.Equal(MapUploadState.Refused, premature.State);
		Assert.Contains($"with 1 of {premature.Chunks} chunks sent", premature.Error);

		// Another map said to be kept.
		var other = new MapUploader(file);
		other.Answered(0);
		while (other.NextPacket() != null)
		{
		}

		other.Kept(MapFile.HashHex(Generated(2)), string.Empty);
		Assert.Equal(MapUploadState.Refused, other.State);
		Assert.Contains("another map", other.Error);

		// Refused after the last chunk.
		var late = new MapUploader(file);
		late.Answered(0);
		while (late.NextPacket() != null)
		{
		}

		late.Kept(hash, "the uploader's map does not play on this build: needs a newer build");
		Assert.Equal(MapUploadState.Refused, late.State);
		Assert.Contains("needs a newer build", late.Error);

		// Kept; and nothing said after that changes it.
		var kept = new MapUploader(file);
		kept.Answered(0);
		while (kept.NextPacket() != null)
		{
		}

		kept.Kept(hash, null);
		Assert.Equal(MapUploadState.Stored, kept.State);
		kept.Kept(hash, "too late");
		kept.Answered((int)MapOfferAnswer.Busy);
		kept.Advance(100);
		kept.Dropped();
		Assert.Equal(MapUploadState.Stored, kept.State);
		Assert.Null(kept.Error);
	}

	[Fact]
	public void AServerThatSaysNothingForTenSeconds_WillNot()
	{
		byte[] file = Generated(1);

		// To the offer.
		var unanswered = new MapUploader(file);
		unanswered.Advance(9.9);
		Assert.Equal(MapUploadState.Offered, unanswered.State);
		unanswered.Advance(double.NaN);
		unanswered.Advance(-50);
		unanswered.Advance(0);
		Assert.Equal(MapUploadState.Offered, unanswered.State);
		unanswered.Advance(0.1);
		Assert.Equal(MapUploadState.Refused, unanswered.State);
		Assert.Contains("did not answer the offer in 10 s", unanswered.Error);

		// While it sends, the server owes nothing: the clock does not run, however long a tick is.
		var sending = new MapUploader(file);
		sending.Advance(9);
		sending.Answered(0);
		sending.Advance(100);
		Assert.Equal(MapUploadState.Sending, sending.State);

		// After the last chunk, from then: the offer's wait is not counted again.
		while (sending.NextPacket() != null)
		{
		}

		sending.Advance(9.9);
		Assert.Equal(MapUploadState.Sent, sending.State);
		sending.Advance(0.1);
		Assert.Equal(MapUploadState.Refused, sending.State);
		Assert.Contains("did not say what became of the map in 10 s", sending.Error);
	}

	[Fact]
	public void ALinkThatDrops_EndsAnUploadNotYetOver()
	{
		var offered = new MapUploader(Generated(1));
		offered.Dropped();
		Assert.Equal(MapUploadState.Refused, offered.State);
		Assert.Contains($"dropped with 0 of {offered.Chunks} chunks sent", offered.Error);

		var had = new MapUploader(Generated(1));
		had.Answered((int)MapOfferAnswer.HaveIt);
		had.Dropped();
		Assert.Equal(MapUploadState.HaveIt, had.State);

		var refused = new MapUploader(Generated(1));
		refused.Answered((int)MapOfferAnswer.Busy);
		string why = refused.Error;
		refused.Dropped();
		Assert.Equal(why, refused.Error);
	}

	// ---- maps/active, and when a server leaves for its next map -------------------------------------

	[Fact]
	public void MapsActive_HoldsAHash_AndNothingElse()
	{
		Assert.Equal("active", MapChange.ActiveFileName);
		string hash = MapFile.HashHex(Generated(1));
		Assert.Equal(hash + "\n", MapChange.ActiveText(hash));

		foreach (string text in new[] { MapChange.ActiveText(hash), hash, hash + "\r\n", "  " + hash + " \n\n", "\t" + hash })
		{
			Assert.True(MapChange.TryParseActive(text, out string parsed, out string error), error);
			Assert.Equal(hash, parsed);
			Assert.Null(error);
		}

		foreach (string text in new[] { null, string.Empty, "\n", "   \r\n" })
		{
			Assert.False(MapChange.TryParseActive(text, out string parsed, out string error));
			Assert.Null(parsed);
			Assert.Contains("empty", error);
		}

		// A name, a hash's start, a hash in upper case, two hashes, a hash and a word: none is a hash.
		foreach (string text in new[] { "hills", hash[..8], hash[..63], hash + "0", hash.ToUpperInvariant(), hash + "\n" + hash,
			hash + " ridge", "sha256:" + hash })
		{
			Assert.False(MapChange.TryParseActive(text, out string parsed, out string error));
			Assert.Null(parsed);
			Assert.Contains("not a map's hash", error);
		}

		// What it says of a file that is not one is short and one line.
		Assert.False(MapChange.TryParseActive("ab\u0007cd\nef" + new string('z', 5000), out _, out string longError));
		Assert.True(longError.Length < 200);
		Assert.Contains("'ab cd ef", longError, StringComparison.Ordinal);
		Assert.DoesNotContain("\u0007", longError, StringComparison.Ordinal);
		Assert.DoesNotContain("\n", longError, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData(null, null, false)]
	[InlineData("", "", false)]
	[InlineData("  ", null, false)]
	[InlineData(null, "0", false)]
	[InlineData(null, "true", false)]
	[InlineData(null, "11", false)]
	[InlineData("0b1d2e3f4a5b6c7d8e9f0a1b2c3d4e5f", null, true)]
	[InlineData(null, "1", true)]
	[InlineData("", " 1 ", true)]
	[InlineData("0b1d2e3f4a5b6c7d8e9f0a1b2c3d4e5f", "0", true)]
	public void AServerIsSupervised_UnderSystemd_OrWhenItIsToldSo(string invocationId, string supervised, bool expected)
	{
		Assert.Equal(expected, MapChange.Supervised(invocationId, supervised));
		Assert.Equal("INVOCATION_ID", MapChange.SystemdVariable);
		Assert.Equal("GDPYR_SUPERVISED", MapChange.SupervisedVariable);
	}

	[Theory]
	[InlineData(false, false, 0, false)]
	[InlineData(false, true, 0, false)]
	[InlineData(false, false, 3, false)]
	[InlineData(false, true, 3, false)]
	[InlineData(true, true, 3, false)]
	[InlineData(true, true, 1, false)]
	[InlineData(true, false, 3, true)]
	[InlineData(true, true, 0, true)]
	[InlineData(true, false, 0, true)]
	[InlineData(true, true, -1, true)]
	public void AServerToldItsNextMap_LeavesAtOnce_OnlyWhenNoRoundIsInProgress_OrNobodyIsConnected(bool named, bool roundInProgress,
		int peers, bool expected)
	{
		Assert.Equal(expected, MapChange.LeavesNow(named, roundInProgress, peers));
	}
}
