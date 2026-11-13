using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using Gdpyr.MapTool;
using Gdpyr.Sim.Level;
using Xunit;

namespace Gdpyr.Tests;

/// <summary>
/// Maps and map files for the level editor's LE2 tests (docs/LEVEL_EDITOR.md §11): the
/// generator's documents, cached by seed since a document is cloned before it is
/// changed, and the means to take a file apart into its chunks and put one together
/// from chunks — the file's layout written out here a second time, so that a test of
/// the reader does not ask the writer what a file is.
/// </summary>
internal static class LevelMaps
{
	private static readonly Dictionary<int, MapDocument> Generated = new();

	/// <summary>The generator's map for a seed, as a copy to change.</summary>
	public static MapDocument Generate(int seed)
	{
		lock (Generated)
		{
			if (!Generated.TryGetValue(seed, out MapDocument document))
			{
				document = MapGenerator.Generate(seed);
				Generated[seed] = document;
			}

			return document.Clone();
		}
	}

	/// <summary>A new map, flat and empty, with an id that does not change.</summary>
	public static MapDocument Empty() =>
		MapDocument.Create(new Guid("0123456789abcdef0123456789abcdef"), "empty", 1_700_000_000);

	/// <summary>A file's chunks: tag and payload, in order. Asserts the header is a map's.</summary>
	public static List<(string Tag, byte[] Payload)> Chunks(byte[] file)
	{
		Assert.Equal("GDPYRMAP", Encoding.ASCII.GetString(file, 0, 8));
		var chunks = new List<(string, byte[])>();
		int at = 10;
		for (int i = 0; i < file[9]; i++)
		{
			string tag = Encoding.ASCII.GetString(file, at, 4);
			int length = (int)BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(at + 4));
			chunks.Add((tag, file.AsSpan(at + 8, length).ToArray()));
			at += 8 + length;
		}

		Assert.Equal(file.Length, at);
		return chunks;
	}

	/// <summary>A file from chunks, with the header given or a map's.</summary>
	public static byte[] Compose(IEnumerable<(string Tag, byte[] Payload)> chunks, byte version = 1, int? count = null)
	{
		var list = new List<(string Tag, byte[] Payload)>(chunks);
		using var stream = new MemoryStream();
		stream.Write("GDPYRMAP"u8);
		stream.WriteByte(version);
		stream.WriteByte((byte)(count ?? list.Count));
		foreach ((string tag, byte[] payload) in list)
		{
			stream.Write(Encoding.ASCII.GetBytes(tag));
			var length = new byte[4];
			BinaryPrimitives.WriteUInt32LittleEndian(length, (uint)payload.Length);
			stream.Write(length);
			stream.Write(payload);
		}

		return stream.ToArray();
	}

	/// <summary>A file with one chunk's payload replaced.</summary>
	public static byte[] With(byte[] file, string tag, byte[] payload)
	{
		var chunks = Chunks(file);
		int i = chunks.FindIndex(c => c.Tag == tag);
		chunks[i] = (tag, payload);
		return Compose(chunks);
	}

	/// <summary>
	/// A TERR payload: the size, spacing, water and six numbers, then the samples less
	/// left + up − upper-left as int16, zlib-deflated — §6.8 written out, not asked of the writer.
	/// </summary>
	public static byte[] Terrain(int width, int depth, int spacing, short[] samples, short water = -1280,
		byte[] residuals = null)
	{
		using var stream = new MemoryStream();
		var head = new byte[20];
		BinaryPrimitives.WriteUInt16LittleEndian(head, (ushort)width);
		BinaryPrimitives.WriteUInt16LittleEndian(head.AsSpan(2), (ushort)depth);
		BinaryPrimitives.WriteUInt16LittleEndian(head.AsSpan(4), (ushort)spacing);
		BinaryPrimitives.WriteInt16LittleEndian(head.AsSpan(6), water);
		ushort[] numbers = { 600, 15000, 1500, 4000, 22000, 1000 };
		for (int i = 0; i < 6; i++)
		{
			BinaryPrimitives.WriteUInt16LittleEndian(head.AsSpan(8 + (i * 2)), numbers[i]);
		}

		stream.Write(head);
		using (var deflate = new ZLibStream(stream, CompressionLevel.Optimal, leaveOpen: true))
		{
			deflate.Write(residuals ?? Residuals(samples, width, depth));
		}

		return stream.ToArray();
	}

	/// <summary>Each sample less its prediction, as little-endian int16 that wraps.</summary>
	public static byte[] Residuals(short[] samples, int width, int depth)
	{
		var bytes = new byte[width * depth * 2];
		for (int z = 0; z < depth; z++)
		{
			for (int x = 0; x < width; x++)
			{
				int i = (z * width) + x;
				int predicted = x > 0 && z > 0
					? samples[i - 1] + samples[i - width] - samples[i - width - 1]
					: x > 0 ? samples[i - 1] : z > 0 ? samples[i - width] : 0;
				BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2), unchecked((short)(samples[i] - predicted)));
			}
		}

		return bytes;
	}

	/// <summary>Raw zlib of some bytes, for a heights stream built to be wrong.</summary>
	public static byte[] Deflate(byte[] bytes)
	{
		using var stream = new MemoryStream();
		using (var deflate = new ZLibStream(stream, CompressionLevel.Optimal, leaveOpen: true))
		{
			deflate.Write(bytes);
		}

		return stream.ToArray();
	}

	/// <summary>An OBJS payload of the given objects, as raw bytes: count, then each one's 12 bytes and parameters.</summary>
	public static byte[] Objects(int count, Func<int, byte[]> each)
	{
		using var stream = new MemoryStream();
		var head = new byte[2];
		BinaryPrimitives.WriteUInt16LittleEndian(head, (ushort)count);
		stream.Write(head);
		for (int i = 0; i < count; i++)
		{
			stream.Write(each(i));
		}

		return stream.ToArray();
	}

	/// <summary>One object's bytes: an id at a point, flags, and parameters given as (key, value).</summary>
	public static byte[] Object(ushort id, short x, short y, short z, ushort yaw = 0, byte flags = 0,
		params (byte Key, int Value)[] parameters)
	{
		var bytes = new byte[12 + (parameters.Length * 5)];
		BinaryPrimitives.WriteUInt16LittleEndian(bytes, id);
		BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(2), x);
		BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(4), y);
		BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(6), z);
		BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(8), yaw);
		bytes[10] = flags;
		bytes[11] = (byte)parameters.Length;
		for (int p = 0; p < parameters.Length; p++)
		{
			bytes[12 + (p * 5)] = parameters[p].Key;
			BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(13 + (p * 5)), parameters[p].Value);
		}

		return bytes;
	}

	/// <summary>A refusal: false, no document, and a reason that says what it was asked to.</summary>
	public static void Refused(byte[] file, string because)
	{
		bool read = MapFile.TryRead(file, out MapDocument document, out string error);
		Assert.False(read, $"read a file that should be refused for '{because}'");
		Assert.Null(document);
		Assert.NotNull(error);
		Assert.Contains(because, error);
	}
}
