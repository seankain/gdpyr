using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Gdpyr.Sim.Level;
using Godot;
using Xunit;

namespace Gdpyr.Tests;

/// <summary>
/// A map's file (docs/LEVEL_EDITOR.md §6.8): a document reads back as it was written,
/// sample by sample and field by field; §10's sizes are the file's; and the reader
/// refuses every case §6.8 names — the truncated, spliced, oversized and hostile —
/// with its reason, keeps nothing, and never throws. As <c>DemoCodecTests</c> does for
/// a demo.
/// </summary>
public class MapFileTests
{
	// ---- reading back what was written ----------------------------------------------------

	[Theory]
	[InlineData(1)]
	[InlineData(2)]
	[InlineData(3)]
	[InlineData(4)]
	public void AGeneratedMap_ReadsBackAsTheSameDocument(int seed)
	{
		MapDocument written = LevelMaps.Generate(seed);
		byte[] file = MapFile.Write(written);

		Assert.True(MapFile.TryRead(file, out MapDocument read, out string error), error);
		Assert.True(written.SameAs(read, out string difference), difference);
		Assert.True(read.Objects.Count > 20);

		// Written again from what was read, the bytes are the same: nothing was lost or made up.
		Assert.Equal(file, MapFile.Write(read));
	}

	[Fact]
	public void ADocumentWithEveryFieldSet_ReadsBackAsTheSameDocument()
	{
		MapDocument written = LevelMaps.Empty();
		written.Revision = 7;
		written.ModifiedUnixSeconds = -5;
		written.ParentId = Guid.NewGuid();
		written.ParentHash = Enumerable.Range(1, 32).Select(i => (byte)(i * 7)).ToArray();
		written.PrefabCatalogCount = 40;
		written.Name = "ridge ümlaut ✓";
		written.Author = "someone";
		written.Description = "two lines\nof text";
		written.Bands = new MapBands
		{
			WaterLevel = -300, SlopeGain = 1, SlopeFree = 65535, SandTop = 2, MudTop = 3, GrassTop = 4, Blend = 0,
		};

		var random = new Random(5);
		for (int i = 0; i < written.Terrain.Heights.Length; i++)
		{
			written.Terrain.Heights[i] = HeightField.ToMeters((short)random.Next(MapLimits.MinHeightSample, MapLimits.MaxHeightSample + 1));
		}

		written.Terrain[0, 0] = MapLimits.MinHeightMeters;
		written.Terrain[240, 240] = MapLimits.MaxHeightMeters;
		written.Objects.Add(new MapObject(3, -32768, 32767, 1, 65535, 255,
			new[] { new MapParameter(9, int.MinValue), new MapParameter(0, int.MaxValue) }));
		written.Objects.Add(new MapObject(65535, 0, 0, 0, 0));
		written.Roads.Add(new MapRoad(1, 60, 100, new[] { new RoadPoint(-100, 200), new RoadPoint(32767, -32768) }));
		written.Roads.Add(new MapRoad(0, 0, 0, ReadOnlySpan<RoadPoint>.Empty));

		byte[] file = MapFile.Write(written);
		Assert.True(MapFile.TryRead(file, out MapDocument read, out string error), error);
		Assert.True(written.SameAs(read, out string difference), difference);
		Assert.Equal(written.Bands.Settings.WaterLevelMeters, read.Bands.Settings.WaterLevelMeters);
	}

	[Fact]
	public void AFileAtEveryLimit_IsAtMostTheLargestA1mMapCanBe_AndReadsBack()
	{
		// §10: every limit of §6.8 reached, and heights that do not deflate.
		MapDocument written = LevelMaps.Empty();
		written.Name = new string('n', MapDocument.MaxNameBytes);
		written.Author = new string('a', MapDocument.MaxAuthorBytes);
		written.Description = new string('d', MapDocument.MaxDescriptionBytes);
		var random = new Random(9);
		for (int i = 0; i < written.Terrain.Heights.Length; i++)
		{
			written.Terrain.Heights[i] = HeightField.ToMeters((short)random.Next(MapLimits.MinHeightSample, MapLimits.MaxHeightSample + 1));
		}

		var parameters = Enumerable.Range(0, MapLimits.MaxObjectParameters).Select(k => new MapParameter((byte)k, random.Next())).ToArray();
		for (int i = 0; i < MapLimits.MaxObjects; i++)
		{
			written.Objects.Add(new MapObject((ushort)(i % 17), (short)i, (short)-i, (short)(i * 3), (ushort)(i * 99), 0, parameters));
		}

		var points = Enumerable.Range(0, MapLimits.MaxRoadPoints).Select(p => new RoadPoint((short)p, (short)-p)).ToArray();
		for (int i = 0; i < MapLimits.MaxRoads; i++)
		{
			written.Roads.Add(new MapRoad(0, 80, 150, points));
		}

		byte[] file = MapFile.Write(written);
		Assert.InRange(file.Length, 140_000, MapFile.MaxMapBytes);
		Assert.True(MapFile.TryRead(file, out MapDocument read, out string error), error);
		Assert.True(written.SameAs(read, out string difference), difference);

		// The chunks one by one against §10's arithmetic.
		var chunks = LevelMaps.Chunks(file);
		Assert.Equal(MapFile.MaxMetaBytes, chunks[0].Payload.Length);
		Assert.True(chunks[1].Payload.Length <= MapFile.MaxTerrainBytes, $"TERR is {chunks[1].Payload.Length} B");
		Assert.Equal(MapFile.MaxObjectsBytes, chunks[2].Payload.Length);
		Assert.Equal(MapFile.MaxRoadsBytes, chunks[3].Payload.Length);
	}

	[Fact]
	public void TheLimits_AreSection10s()
	{
		Assert.Equal(424, MapFile.MaxMetaBytes);
		Assert.Equal(116_162, MapFile.RawHeightBytes);
		Assert.Equal(116_230, MapFile.MaxTerrainBytes);
		Assert.Equal(26_626, MapFile.MaxObjectsBytes);
		Assert.Equal(8_321, MapFile.MaxRoadsBytes);
		Assert.Equal(151_643, MapFile.MaxMapBytes);
		Assert.Equal(1 << 20, MapLimits.MaxFileBytes);
		Assert.True(MapFile.MaxMapBytes < MapLimits.MaxFileBytes);
	}

	[Fact]
	public void ANewMap_IsAFewHundredBytes_ItsHeightsAlmostNothing()
	{
		// §10: the predictor is exact on a plane, so a new map's heights deflate to almost nothing.
		byte[] file = MapFile.Write(LevelMaps.Empty());
		var chunks = LevelMaps.Chunks(file);
		Assert.Equal(new[] { "META", "TERR", "OBJS", "ROAD" }, chunks.Select(c => c.Tag));
		Assert.InRange(chunks[1].Payload.Length - MapFile.TerrainFixedBytes, 1, 200);
		Assert.InRange(file.Length, 1, 400);

		// An even slope, too: under 1 % of the raw heights.
		MapDocument slope = LevelMaps.Empty();
		for (int z = 0; z < 241; z++)
		{
			for (int x = 0; x < 241; x++)
			{
				slope.Terrain[x, z] = HeightField.ToMeters((short)((x * 3) - (z * 5) + 100));
			}
		}

		Assert.InRange(LevelMaps.Chunks(MapFile.Write(slope))[1].Payload.Length - MapFile.TerrainFixedBytes, 1, MapFile.RawHeightBytes / 100);
	}

	[Fact]
	public void ThePredictor_IsExactOnAPlane_AndWritesWhatSection6Point8Says()
	{
		var samples = new short[9 * 7];
		for (int z = 0; z < 7; z++)
		{
			for (int x = 0; x < 9; x++)
			{
				samples[(z * 9) + x] = (short)(1000 + (x * 13) - (z * 21));
			}
		}

		byte[] residuals = MapFile.PredictHeights(samples, 9, 7);
		Assert.Equal(LevelMaps.Residuals(samples, 9, 7), residuals);
		for (int z = 1; z < 7; z++)
		{
			for (int x = 1; x < 9; x++)
			{
				Assert.Equal(0, BinaryPrimitives.ReadInt16LittleEndian(residuals.AsSpan(((z * 9) + x) * 2)));
			}
		}
	}

	[Fact]
	public void TheWritersFile_IsTheFileSection6Point8Describes()
	{
		// Composed here from the layout, not from the writer, and read back.
		MapDocument document = LevelMaps.Generate(1);
		byte[] file = MapFile.Write(document);
		var chunks = LevelMaps.Chunks(file);

		short[] samples = document.Terrain.ToSamples();
		var composed = LevelMaps.Compose(new[]
		{
			chunks[0],
			("TERR", LevelMaps.Terrain(241, 241, 1000, samples, document.Bands.WaterLevel)),
			chunks[2],
			chunks[3],
		});
		Assert.True(MapFile.TryRead(composed, out MapDocument read, out string error), error);
		Assert.True(document.SameAs(read, out string difference), difference);

		// META: the guid's bytes first, then the revision; OBJS: the count, then the first object's id.
		Assert.Equal(document.Id, new Guid(chunks[0].Payload.AsSpan(0, 16)));
		Assert.Equal(document.Objects.Count, BinaryPrimitives.ReadUInt16LittleEndian(chunks[2].Payload));
		Assert.Equal(document.Objects[0].PrefabId, BinaryPrimitives.ReadUInt16LittleEndian(chunks[2].Payload.AsSpan(2)));
		Assert.Equal(0, chunks[3].Payload.Single());
	}

	[Fact]
	public void AStream_ReadsAsItsBytesDo_AndOneOver1MiB_IsRefused()
	{
		byte[] file = MapFile.Write(LevelMaps.Generate(2));
		Assert.True(MapFile.TryRead(new MemoryStream(file), out MapDocument fromStream, out string error), error);
		Assert.True(LevelMaps.Generate(2).SameAs(fromStream, out string difference), difference);

		var huge = new byte[(2 << 20) + 5];
		file.CopyTo(huge, 0);
		Assert.False(MapFile.TryRead(new MemoryStream(huge), out MapDocument none, out error));
		Assert.Null(none);
		Assert.Contains("over 1,048,576 bytes", error);
	}

	[Fact]
	public void TheHash_IsTheSha256OfTheFile()
	{
		byte[] file = MapFile.Write(LevelMaps.Generate(3));
		Assert.Equal(SHA256.HashData(file), MapFile.Hash(file));
		string hex = MapFile.HashHex(file);
		Assert.Equal(64, hex.Length);
		Assert.Equal(hex.ToLowerInvariant(), hex);

		file[^1] ^= 1;
		Assert.NotEqual(hex, MapFile.HashHex(file));
	}

	// ---- refusals, each with its reason ---------------------------------------------------

	private static byte[] Valid() => MapFile.Write(LevelMaps.Generate(1));

	[Fact]
	public void NotAMap_IsRefused()
	{
		LevelMaps.Refused(Array.Empty<byte>(), "not a gdpyr map");
		LevelMaps.Refused(Encoding.ASCII.GetBytes("GDPYRDEM\u0005\u0004"), "not a gdpyr map");
		LevelMaps.Refused(Encoding.ASCII.GetBytes("GDPYRMA"), "not a gdpyr map");
		LevelMaps.Refused(Encoding.ASCII.GetBytes("GDPYRMAP"), "ends in its header");
		LevelMaps.Refused(Encoding.ASCII.GetBytes("GDPYRMAP\u0001"), "ends in its header");
	}

	[Fact]
	public void AnotherVersion_OrAnotherChunkCount_IsRefused()
	{
		var chunks = LevelMaps.Chunks(Valid());
		LevelMaps.Refused(LevelMaps.Compose(chunks, version: 2), "map format version 2; this build reads 1");
		LevelMaps.Refused(LevelMaps.Compose(chunks, version: 0), "map format version 0");
		LevelMaps.Refused(LevelMaps.Compose(chunks.Take(3)), "the file has 3 chunks");
		LevelMaps.Refused(LevelMaps.Compose(chunks, count: 5), "the file has 5 chunks");
	}

	[Fact]
	public void ChunksOutOfOrder_Twice_Missing_OrUnknown_AreRefused()
	{
		var c = LevelMaps.Chunks(Valid());
		LevelMaps.Refused(LevelMaps.Compose(new[] { c[1], c[0], c[2], c[3] }), "a 'TERR' chunk where the META chunk belongs");
		LevelMaps.Refused(LevelMaps.Compose(new[] { c[0], c[0], c[2], c[3] }), "a 'META' chunk where the TERR chunk belongs");
		LevelMaps.Refused(LevelMaps.Compose(new[] { c[0], c[1], c[3], c[2] }), "where the OBJS chunk belongs");
		LevelMaps.Refused(LevelMaps.Compose(new[] { c[0], c[1], c[2], c[2] }), "where the ROAD chunk belongs");
		LevelMaps.Refused(LevelMaps.Compose(new[] { c[0], c[1], c[2] }, count: 4), "ends before its ROAD chunk");
		LevelMaps.Refused(LevelMaps.Compose(new[] { c[0], ("TRRN", c[1].Payload), c[2], c[3] }), "an unknown chunk 'TRRN'");
		LevelMaps.Refused(LevelMaps.Compose(new[] { c[0], ("\0\u0001ab", c[1].Payload), c[2], c[3] }), @"unknown chunk '\x00\x01ab'");
	}

	[Fact]
	public void ALength_PastTheEnd_OrPastItsLimit_IsRefusedBeforeAnythingIsRead()
	{
		byte[] file = Valid();
		var c = LevelMaps.Chunks(file);

		// The last chunk says it is longer than what is left of the file.
		byte[] longer = (byte[])file.Clone();
		int roadAt = file.Length - c[3].Payload.Length - 4;
		BinaryPrimitives.WriteUInt32LittleEndian(longer.AsSpan(roadAt), (uint)c[3].Payload.Length + 1);
		LevelMaps.Refused(longer, "the ROAD chunk's 2 bytes run past the end of the file");

		// Each chunk's length set to one past its limit, and to the most a u32 says.
		int at = 10;
		int[] limits = { MapFile.MaxMetaBytes, MapFile.MaxTerrainBytes, MapFile.MaxObjectsBytes, MapFile.MaxRoadsBytes };
		for (int i = 0; i < 4; i++)
		{
			foreach (uint length in new[] { (uint)limits[i] + 1, uint.MaxValue })
			{
				byte[] over = (byte[])file.Clone();
				BinaryPrimitives.WriteUInt32LittleEndian(over.AsSpan(at + 4), length);
				LevelMaps.Refused(over, $"the {c[i].Tag} chunk is {length:N0} bytes, over its limit of {limits[i]:N0}");
			}

			at += 8 + c[i].Payload.Length;
		}

		// A chunk at its limit, over the end of a short file.
		byte[] cut = LevelMaps.Compose(new[] { c[0], c[1], ("OBJS", new byte[10]) }, count: 4);
		BinaryPrimitives.WriteUInt32LittleEndian(cut.AsSpan(cut.Length - 14), (uint)MapFile.MaxObjectsBytes);
		LevelMaps.Refused(cut, "run past the end of the file");
	}

	[Fact]
	public void AFileOver1MiB_IsRefused()
	{
		byte[] file = Valid();
		var over = new byte[MapLimits.MaxFileBytes + 1];
		file.CopyTo(over, 0);
		LevelMaps.Refused(over, "the file is over 1,048,576 bytes");

		// At the limit it is the bytes after the last chunk that are refused, not the size.
		var at = new byte[MapLimits.MaxFileBytes];
		file.CopyTo(at, 0);
		LevelMaps.Refused(at, $"{MapLimits.MaxFileBytes - file.Length} bytes after the last chunk");
	}

	[Fact]
	public void BytesAfterTheLastChunk_OrLeftInOne_AreRefused()
	{
		byte[] file = Valid();
		LevelMaps.Refused(file.Concat(new byte[] { 0 }).ToArray(), "1 bytes after the last chunk");

		var c = LevelMaps.Chunks(file);
		LevelMaps.Refused(LevelMaps.With(file, "META", c[0].Payload.Concat(new byte[] { 0 }).ToArray()), "the META chunk has 1 bytes left over");
		LevelMaps.Refused(LevelMaps.With(file, "OBJS", c[2].Payload.Concat(new byte[] { 1, 2 }).ToArray()), "the OBJS chunk has 2 bytes left over");
		LevelMaps.Refused(LevelMaps.With(file, "ROAD", new byte[] { 0, 9 }), "the ROAD chunk has 1 bytes left over");
	}

	[Fact]
	public void ATerrainOfAnotherSize_OrSpacing_IsRefused()
	{
		byte[] file = Valid();
		short[] small = new short[240 * 241];
		LevelMaps.Refused(LevelMaps.With(file, "TERR", LevelMaps.Terrain(240, 241, 1000, small)),
			"the terrain is 240 × 241 samples at 1000 mm; this build reads 241 × 241 at 1,000 mm");
		LevelMaps.Refused(LevelMaps.With(file, "TERR", LevelMaps.Terrain(241, 241, 500, new short[241 * 241])),
			"at 500 mm; this build reads");
		LevelMaps.Refused(LevelMaps.With(file, "TERR", LevelMaps.Terrain(0, 0, 1000, Array.Empty<short>())),
			"the terrain is 0 × 0 samples");
		LevelMaps.Refused(LevelMaps.With(file, "TERR", new byte[19]), "the TERR chunk is too short");
	}

	[Theory]
	[InlineData(MapLimits.MaxHeightSample + 1, "is 64.004 m, outside -16 … +64 m")]
	[InlineData(MapLimits.MinHeightSample - 1, "is -16.004 m, outside")]
	[InlineData(short.MaxValue, "is 127.996 m, outside -16 … +64 m")]
	[InlineData(short.MinValue, "is -128 m, outside -16 … +64 m")]
	public void AHeightOutsideLeD4_IsRefused(int height, string because)
	{
		var samples = new short[241 * 241];
		samples[(100 * 241) + 37] = (short)height;
		LevelMaps.Refused(LevelMaps.With(Valid(), "TERR", LevelMaps.Terrain(241, 241, 1000, samples)), "sample (37, 100) " + because);

		// At the limits themselves, every sample: taken.
		Array.Fill(samples, MapLimits.MaxHeightSample);
		Assert.True(MapFile.TryRead(LevelMaps.With(Valid(), "TERR", LevelMaps.Terrain(241, 241, 1000, samples)), out _, out string error), error);
		Array.Fill(samples, MapLimits.MinHeightSample);
		Assert.True(MapFile.TryRead(LevelMaps.With(Valid(), "TERR", LevelMaps.Terrain(241, 241, 1000, samples)), out _, out error), error);
	}

	[Fact]
	public void AHeightsStream_ThatInflatesToAnythingButWidthTimesDepthTimesTwo_IsRefused()
	{
		byte[] file = Valid();
		var samples = new short[241 * 241];
		byte[] residuals = LevelMaps.Residuals(samples, 241, 241);
		byte[] head = LevelMaps.Terrain(241, 241, 1000, samples).AsSpan(0, 20).ToArray();

		byte[] Terrain(byte[] deflated) => head.Concat(deflated).ToArray();

		LevelMaps.Refused(LevelMaps.With(file, "TERR", Terrain(LevelMaps.Deflate(residuals[..^2]))),
			"the heights inflate to 116,160 bytes, not 116,162");
		LevelMaps.Refused(LevelMaps.With(file, "TERR", Terrain(LevelMaps.Deflate(residuals.Concat(new byte[] { 0 }).ToArray()))),
			"the heights inflate to more than 116,162 bytes");
		LevelMaps.Refused(LevelMaps.With(file, "TERR", Terrain(LevelMaps.Deflate(new byte[10 << 20]))),
			"the heights inflate to more than 116,162 bytes");
		LevelMaps.Refused(LevelMaps.With(file, "TERR", Terrain(Array.Empty<byte>())), "the heights inflate to 0 bytes");

		// Not zlib, cut short, and with its check broken.
		byte[] good = LevelMaps.Deflate(residuals);
		LevelMaps.Refused(LevelMaps.With(file, "TERR", Terrain(Enumerable.Repeat((byte)0xAB, 200).ToArray())), "the heights do not inflate");
		LevelMaps.Refused(LevelMaps.With(file, "TERR", Terrain(good[..^6])), "the heights inflate to");
		byte[] badCheck = (byte[])good.Clone();
		badCheck[^1] ^= 0xFF;
		LevelMaps.Refused(LevelMaps.With(file, "TERR", Terrain(badCheck)), "the heights do not inflate");

		// Raw deflate, without zlib's header.
		using var raw = new MemoryStream();
		using (var deflate = new System.IO.Compression.DeflateStream(raw, System.IO.Compression.CompressionLevel.Optimal, true))
		{
			deflate.Write(residuals);
		}

		LevelMaps.Refused(LevelMaps.With(file, "TERR", Terrain(raw.ToArray())), "the heights do not inflate");
	}

	[Fact]
	public void TheText_LongerThanItsLimit_NotUtf8_OrWithAControlCharacter_IsRefused()
	{
		byte[] file = Valid();
		byte[] meta = LevelMaps.Chunks(file)[0].Payload;

		byte[] Meta(byte[] name, byte[] author, byte[] description)
		{
			var bytes = meta.AsSpan(0, MapFile.MetaFixedBytes).ToArray().ToList();
			foreach (byte[] text in new[] { name, author, description })
			{
				bytes.Add((byte)text.Length);
				bytes.AddRange(text);
			}

			return bytes.ToArray();
		}

		byte[] A(int n) => Enumerable.Repeat((byte)'a', n).ToArray();

		Assert.True(MapFile.TryRead(LevelMaps.With(file, "META", Meta(A(48), A(32), A(255))), out _, out string error), error);
		LevelMaps.Refused(LevelMaps.With(file, "META", Meta(A(49), A(1), A(1))), "the name is 49 bytes, over 48");
		LevelMaps.Refused(LevelMaps.With(file, "META", Meta(A(1), A(33), A(1))), "the author is 33 bytes, over 32");
		LevelMaps.Refused(LevelMaps.With(file, "META", Meta(new byte[] { 0xC3, 0x28 }, A(1), A(1))), "the name is not UTF-8");
		LevelMaps.Refused(LevelMaps.With(file, "META", Meta(A(1), new byte[] { 0xED, 0xA0, 0x80 }, A(1))), "the author is not UTF-8");
		LevelMaps.Refused(LevelMaps.With(file, "META", Meta(A(1), A(1), new byte[] { 0xFF })), "the description is not UTF-8");
		LevelMaps.Refused(LevelMaps.With(file, "META", Meta(new byte[] { (byte)'a', 0x1B, (byte)'[' }, A(1), A(1))), "the name holds a control character");
		LevelMaps.Refused(LevelMaps.With(file, "META", Meta(A(1), new byte[] { (byte)'\n' }, A(1))), "the author holds a control character");
		LevelMaps.Refused(LevelMaps.With(file, "META", Meta(A(1), A(1), new byte[] { (byte)'\r' })), "the description holds a control character");
		Assert.True(MapFile.TryRead(LevelMaps.With(file, "META", Meta(A(1), A(1), new byte[] { (byte)'a', (byte)'\n', (byte)'b' })), out _, out error), error);

		// A length that runs past the chunk, and a chunk that ends before its text.
		byte[] past = Meta(A(3), A(1), A(1));
		past[MapFile.MetaFixedBytes] = 40;
		LevelMaps.Refused(LevelMaps.With(file, "META", past), "the name runs past the end of the META chunk");
		LevelMaps.Refused(LevelMaps.With(file, "META", meta.AsSpan(0, MapFile.MetaFixedBytes).ToArray()), "the META chunk ends before its name");
		LevelMaps.Refused(LevelMaps.With(file, "META", meta.AsSpan(0, 50).ToArray()), "the META chunk is too short");
	}

	[Fact]
	public void TooManyObjects_OrParameters_OrAParameterGivenTwice_AreRefused()
	{
		byte[] file = Valid();
		byte[] one = LevelMaps.Object(1, 0, 0, 0);
		Assert.True(MapFile.TryRead(LevelMaps.With(file, "OBJS", LevelMaps.Objects(512, _ => one)), out MapDocument read, out string error), error);
		Assert.Equal(512, read.Objects.Count);

		LevelMaps.Refused(LevelMaps.With(file, "OBJS", LevelMaps.Objects(513, _ => one)), "513 objects, over 512");
		LevelMaps.Refused(LevelMaps.With(file, "OBJS", LevelMaps.Objects(65535, _ => Array.Empty<byte>())), "65535 objects, over 512");

		var nine = Enumerable.Range(0, 9).Select(k => ((byte)k, k)).ToArray();
		LevelMaps.Refused(LevelMaps.With(file, "OBJS", LevelMaps.Objects(1, _ => LevelMaps.Object(4, 0, 0, 0, 0, 0, nine))),
			"object 0 has 9 parameters, over 8");
		LevelMaps.Refused(LevelMaps.With(file, "OBJS", LevelMaps.Objects(2, i => LevelMaps.Object(4, 0, 0, 0, 0, 0, ((byte)1, 5), ((byte)i, 6)))),
			"object 1 gives parameter 1 twice");

		// Cut short in an object, and in its parameters.
		LevelMaps.Refused(LevelMaps.With(file, "OBJS", LevelMaps.Objects(2, _ => one)[..^3]), "the OBJS chunk ends in object 1");
		byte[] withParameter = LevelMaps.Objects(1, _ => LevelMaps.Object(4, 0, 0, 0, 0, 0, ((byte)1, 5)));
		LevelMaps.Refused(LevelMaps.With(file, "OBJS", withParameter[..^1]), "the OBJS chunk ends in object 0's parameters");
		LevelMaps.Refused(LevelMaps.With(file, "OBJS", new byte[1]), "the OBJS chunk is too short");
	}

	[Fact]
	public void TooManyRoads_OrPoints_AreRefused()
	{
		byte[] file = Valid();
		byte[] Roads(int count, int points)
		{
			var bytes = new List<byte> { (byte)count };
			for (int i = 0; i < count; i++)
			{
				bytes.AddRange(new byte[] { 0, 60, 100, (byte)points });
				bytes.AddRange(new byte[points * 4]);
			}

			return bytes.ToArray();
		}

		Assert.True(MapFile.TryRead(LevelMaps.With(file, "ROAD", Roads(32, 64)), out MapDocument read, out string error), error);
		Assert.Equal(32, read.Roads.Count);
		LevelMaps.Refused(LevelMaps.With(file, "ROAD", Roads(33, 2)), "33 roads, over 32");
		LevelMaps.Refused(LevelMaps.With(file, "ROAD", Roads(1, 65)), "road 0 has 65 points, over 64");
		LevelMaps.Refused(LevelMaps.With(file, "ROAD", Roads(2, 3)[..^1]), "the ROAD chunk ends in road 1's points");
		LevelMaps.Refused(LevelMaps.With(file, "ROAD", Roads(2, 3)[..19]), "the ROAD chunk ends in road 1");
		LevelMaps.Refused(LevelMaps.With(file, "ROAD", Array.Empty<byte>()), "the ROAD chunk is too short");
	}

	// ---- the truncated, the spliced, the hostile: never a throw -----------------------------

	[Fact]
	public void EveryTruncation_IsRefusedWithAReason()
	{
		byte[] file = MapFile.Write(LevelMaps.Generate(2));
		for (int length = 0; length < file.Length; length++)
		{
			Assert.False(MapFile.TryRead(file.AsSpan(0, length), out MapDocument document, out string error), $"read {length} bytes");
			Assert.Null(document);
			Assert.False(string.IsNullOrEmpty(error));
		}
	}

	[Fact]
	public void SplicedFiles_AreRefusedOrReadWhole_AndNeverThrow()
	{
		byte[] a = MapFile.Write(LevelMaps.Generate(1));
		byte[] b = MapFile.Write(LevelMaps.Generate(3));
		var random = new Random(4);
		int refused = 0;
		for (int trial = 0; trial < 400; trial++)
		{
			int cutA = random.Next(a.Length);
			int cutB = random.Next(b.Length);
			byte[] spliced = a[..cutA].Concat(b[cutB..]).ToArray();
			if (Read(spliced, out _))
			{
				continue;
			}

			refused++;
		}

		Assert.InRange(refused, 390, 400);
	}

	[Fact]
	public void CorruptedFiles_NeverThrow_AndARefusalHasAReason()
	{
		byte[] file = MapFile.Write(LevelMaps.Generate(4));
		var random = new Random(77);
		int refused = 0;
		const int trials = 3000;
		for (int trial = 0; trial < trials; trial++)
		{
			byte[] bad = (byte[])file.Clone();
			switch (trial % 4)
			{
				case 0:
					// One bit.
					bad[random.Next(bad.Length)] ^= (byte)(1 << random.Next(8));
					break;
				case 1:
					// A few bytes anywhere.
					for (int i = random.Next(1, 8); i > 0; i--)
					{
						bad[random.Next(bad.Length)] = (byte)random.Next(256);
					}

					break;
				case 2:
					// The front of the file, where the lengths and counts are: a byte of the first 120.
					bad[random.Next(10, 120)] = (byte)random.Next(256);
					break;
				default:
					// Everything after the header, noise.
					random.NextBytes(bad.AsSpan(10 + random.Next(bad.Length - 10)));
					break;
			}

			refused += Read(bad, out _) ? 0 : 1;
		}

		Assert.InRange(refused, trials / 2, trials);
	}

	[Fact]
	public void NoiseWithAMapsHeader_NeverThrows()
	{
		var random = new Random(3);
		for (int trial = 0; trial < 2000; trial++)
		{
			var bytes = new byte[random.Next(10, 600)];
			random.NextBytes(bytes);
			"GDPYRMAP"u8.CopyTo(bytes);
			bytes[8] = 1;
			bytes[9] = 4;
			Assert.False(Read(bytes, out _));
		}
	}

	/// <summary>Reads, asserting no exception, and a reason when refused and none when not.</summary>
	private static bool Read(byte[] file, out MapDocument document)
	{
		bool read = false;
		string error = null;
		MapDocument result = null;
		Exception thrown = Record.Exception(() => read = MapFile.TryRead(file, out result, out error));
		Assert.Null(thrown);
		document = result;
		if (read)
		{
			Assert.NotNull(result);
			Assert.Null(error);
		}
		else
		{
			Assert.Null(result);
			Assert.False(string.IsNullOrEmpty(error));
		}

		return read;
	}

	// ---- the writer ------------------------------------------------------------------------

	[Fact]
	public void AWriter_RefusesWhatAFileCannotHold()
	{
		MapDocument document = LevelMaps.Empty();
		document.Name = new string('é', 25);
		Assert.False(MapFile.TryWrite(document, out byte[] file, out string error));
		Assert.Null(file);
		Assert.Contains("the name is over 48 bytes", error);
		Assert.Throws<ArgumentException>(() => MapFile.Write(document));

		document = LevelMaps.Empty();
		for (int i = 0; i < 513; i++)
		{
			document.Objects.Add(new MapObject(8, 0, 0, 0, 0));
		}

		Assert.False(MapFile.TryWrite(document, out _, out error));
		Assert.Contains("513 objects, over 512", error);

		document = LevelMaps.Empty();
		for (int i = 0; i < 33; i++)
		{
			document.Roads.Add(new MapRoad(0, 60, 100, ReadOnlySpan<RoadPoint>.Empty));
		}

		Assert.False(MapFile.TryWrite(document, out _, out error));
		Assert.Contains("33 roads, over 32", error);
	}

	[Fact]
	public void AHeightBetweenSteps_OrPastTheLimits_IsWrittenAsTheStepItRoundsTo()
	{
		MapDocument document = LevelMaps.Empty();
		document.Terrain[3, 4] = 1.001f;
		document.Terrain[5, 6] = 99f;
		document.Terrain[7, 8] = -40f;
		Assert.True(MapFile.TryRead(MapFile.Write(document), out MapDocument read, out string error), error);
		Assert.Equal(1f, read.Terrain[3, 4]);
		Assert.Equal(MapLimits.MaxHeightMeters, read.Terrain[5, 6]);
		Assert.Equal(MapLimits.MinHeightMeters, read.Terrain[7, 8]);
	}

	[Fact]
	public void Writing_IsDeterministic()
	{
		MapDocument document = LevelMaps.Generate(2);
		Assert.Equal(MapFile.Write(document), MapFile.Write(document.Clone()));
		var stream = new MemoryStream();
		MapFile.Write(document, stream);
		Assert.Equal(MapFile.Write(document), stream.ToArray());
	}
}
