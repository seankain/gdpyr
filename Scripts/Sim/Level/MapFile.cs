using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Unicode;

namespace Gdpyr.Sim.Level;

/// <summary>
/// A map's file (docs/LEVEL_EDITOR.md §6.8): DEMOS.md §2's container, for its reasons —
/// tagged, length-delimited, little-endian, the dullest format that could work,
/// written and read through a <see cref="Stream"/>.
///
/// <code>
/// "GDPYRMAP" (8) · format version (1) · chunk count (1); then chunks: tag (4) · length (u32) · payload
///
/// META  map guid (16) · revision (u32) · created, modified (i64, unix s)
///       parent guid (16) · parent hash (32) · prefab catalog count (u16)
///       name ≤ 48 B · author ≤ 32 B · description ≤ 255 B       each a u8 length, then UTF-8
/// TERR  width, depth (u16) · spacing (u16, mm) · water level (i16, 1/256 m)
///       the six band numbers (u16, thousandths)
///       heights: each sample less (left + up − upper-left), as int16, deflated
/// OBJS  count (u16), then for each: prefab id (u16) · x, y, z (i16, cm) · yaw (u16) · flags (u8)
///       parameter count (u8, at most 8) · (key u8, value i32) × n
/// ROAD  count (u8), then for each: style (u8) · width (u8, dm) · grade limit (u8, 0.1 %)
///       point count (u8) · (x, z: i16, cm) × n
/// </code>
///
/// There is no float in the file (LE-D14). The six band numbers are, in order, the
/// slope's gain, the slope that costs nothing, the sand's, mud's and grass's tops and
/// the blend (§6.4). The heights are deflated in zlib's format, whose Adler-32 is a
/// check on what inflates, and the predictor is exact on a plane, so level and evenly
/// sloped ground deflates to almost nothing (§10).
///
/// <b>The reader is total.</b> A truncated, spliced, oversized or hostile file is a
/// refusal with its reason, never an exception, and nothing of it is kept: chunks in
/// this order, once each; no unknown tag; this build's version; no length past the
/// end of the file or past its chunk's limit, checked before anything is allocated;
/// no height outside LE-D4; a heights stream that inflates to exactly width × depth ×
/// 2 bytes; nothing after the last chunk. What a map <i>means</i> — counts, places,
/// pads — is <see cref="MapValidator"/>'s.
///
/// A map's hash is the SHA-256 of its file (LE-D15). A file is not re-encoded to be
/// hashed, sent or cached.
/// </summary>
public static class MapFile
{
	/// <summary>The extension a map is saved under.</summary>
	public const string Extension = ".map";

	public const int MagicBytes = 8;

	/// <summary>ASCII <c>GDPYRMAP</c>: the first eight bytes of every map.</summary>
	public static ReadOnlySpan<byte> Magic => "GDPYRMAP"u8;

	/// <summary>The format this build writes and the only one it reads. A reader refuses any other rather than guess.</summary>
	public const byte Version = 1;

	/// <summary>Magic, version, chunk count.</summary>
	public const int HeaderBytes = MagicBytes + 2;

	/// <summary>Tag and length.</summary>
	public const int ChunkHeaderBytes = 8;

	/// <summary>META, TERR, OBJS, ROAD: every file has the four.</summary>
	public const int ChunkCount = 4;

	public static ReadOnlySpan<byte> MetaTag => "META"u8;

	public static ReadOnlySpan<byte> TerrainTag => "TERR"u8;

	public static ReadOnlySpan<byte> ObjectsTag => "OBJS"u8;

	public static ReadOnlySpan<byte> RoadsTag => "ROAD"u8;

	/// <summary>META's fixed part: two guids, a revision, two times, a hash and the catalog count.</summary>
	public const int MetaFixedBytes = 16 + 4 + 8 + 8 + 16 + MapDocument.HashBytes + 2;

	/// <summary>The longest META: its fixed part and three strings at their longest, 424 B (§10).</summary>
	public const int MaxMetaBytes = MetaFixedBytes + 3 + MapDocument.MaxNameBytes + MapDocument.MaxAuthorBytes
		+ MapDocument.MaxDescriptionBytes;

	/// <summary>TERR's fixed part: the size, the spacing, the water and the six numbers.</summary>
	public const int TerrainFixedBytes = 2 + 2 + 2 + 2 + (6 * 2);

	/// <summary>A map's heights, raw: 241 × 241 int16, 116,162 B.</summary>
	public const int RawHeightBytes = MapLimits.TerrainSamples * MapLimits.TerrainSamples * 2;

	/// <summary>
	/// The longest TERR, 116,230 B (§10): its fixed part and zlib's bound on what
	/// deflating the raw heights can come to, for ground that does not shrink.
	/// </summary>
	public const int MaxTerrainBytes = TerrainFixedBytes + RawHeightBytes + (RawHeightBytes >> 12)
		+ (RawHeightBytes >> 14) + (RawHeightBytes >> 25) + 13;

	/// <summary>One object at its most: id, position, yaw, flags, count, and eight parameters.</summary>
	public const int MaxObjectBytes = 2 + 6 + 2 + 1 + 1 + (MapLimits.MaxObjectParameters * 5);

	/// <summary>The longest OBJS, 26,626 B.</summary>
	public const int MaxObjectsBytes = 2 + (MapLimits.MaxObjects * MaxObjectBytes);

	/// <summary>The longest ROAD, 8,321 B.</summary>
	public const int MaxRoadsBytes = 1 + (MapLimits.MaxRoads * (4 + (MapLimits.MaxRoadPoints * 4)));

	/// <summary>The largest file a 1 m map can be, every limit reached: 151,643 B (§10).</summary>
	public const int MaxMapBytes = HeaderBytes + (ChunkCount * ChunkHeaderBytes) + MaxMetaBytes + MaxTerrainBytes
		+ MaxObjectsBytes + MaxRoadsBytes;

	private static readonly UTF8Encoding StrictUtf8 = new(false, true);

	// ---- writing --------------------------------------------------------------------------

	/// <summary>
	/// The file a document is, or false with the reason when it holds more than a file
	/// can: a string too long, too many objects or roads. Its heights are written as
	/// stored, each the nearest step inside LE-D4 (§6.1).
	/// </summary>
	public static bool TryWrite(MapDocument document, out byte[] file, out string error)
	{
		file = null;
		if (!CanWrite(document, out error))
		{
			return false;
		}

		using var stream = new MemoryStream(32 * 1024);
		stream.Write(Magic);
		stream.WriteByte(Version);
		stream.WriteByte(ChunkCount);
		WriteChunk(stream, MetaTag, Meta(document));
		WriteChunk(stream, TerrainTag, Terrain(document));
		WriteChunk(stream, ObjectsTag, Objects(document));
		WriteChunk(stream, RoadsTag, Roads(document));
		file = stream.ToArray();
		return true;
	}

	/// <summary>The file a document is. Throws for one a file cannot hold (<see cref="TryWrite(MapDocument, out byte[], out string)"/>).</summary>
	public static byte[] Write(MapDocument document) =>
		TryWrite(document, out byte[] file, out string error) ? file : throw new ArgumentException(error, nameof(document));

	/// <summary>Writes a document's file to a stream.</summary>
	public static void Write(MapDocument document, Stream stream) => stream.Write(Write(document));

	/// <summary>Whether a file can hold the document; the reason when it cannot.</summary>
	public static bool CanWrite(MapDocument document, out string error)
	{
		error = null;
		if (document == null)
		{
			error = "no document";
		}
		else if (MapDocument.Utf8Bytes(document.Name) > MapDocument.MaxNameBytes)
		{
			error = $"the name is over {MapDocument.MaxNameBytes} bytes";
		}
		else if (MapDocument.Utf8Bytes(document.Author) > MapDocument.MaxAuthorBytes)
		{
			error = $"the author is over {MapDocument.MaxAuthorBytes} bytes";
		}
		else if (MapDocument.Utf8Bytes(document.Description) > MapDocument.MaxDescriptionBytes)
		{
			error = $"the description is over {MapDocument.MaxDescriptionBytes} bytes";
		}
		else if (document.Terrain.Width > ushort.MaxValue || document.Terrain.Depth > ushort.MaxValue)
		{
			error = "the terrain is wider than a file can say";
		}
		else if (document.Objects.Count > MapLimits.MaxObjects)
		{
			error = $"{document.Objects.Count} objects, over {MapLimits.MaxObjects}";
		}
		else if (document.Roads.Count > MapLimits.MaxRoads)
		{
			error = $"{document.Roads.Count} roads, over {MapLimits.MaxRoads}";
		}

		return error == null;
	}

	/// <summary>
	/// The heights as the file stores them, before deflating: each sample less
	/// left + up − upper-left, as an int16 that wraps. The first row is predicted from
	/// its left, the first column from above, and sample (0, 0) from nothing.
	/// </summary>
	public static byte[] PredictHeights(ReadOnlySpan<short> samples, int width, int depth)
	{
		var bytes = new byte[width * depth * 2];
		for (int z = 0; z < depth; z++)
		{
			for (int x = 0; x < width; x++)
			{
				int i = (z * width) + x;
				BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2),
					unchecked((short)(samples[i] - Predict(samples, width, x, z))));
			}
		}

		return bytes;
	}

	private static int Predict(ReadOnlySpan<short> samples, int width, int x, int z)
	{
		int i = (z * width) + x;
		if (x > 0 && z > 0)
		{
			return samples[i - 1] + samples[i - width] - samples[i - width - 1];
		}

		return x > 0 ? samples[i - 1] : z > 0 ? samples[i - width] : 0;
	}

	private static void WriteChunk(Stream stream, ReadOnlySpan<byte> tag, byte[] payload)
	{
		Span<byte> header = stackalloc byte[ChunkHeaderBytes];
		tag.CopyTo(header);
		BinaryPrimitives.WriteUInt32LittleEndian(header[4..], (uint)payload.Length);
		stream.Write(header);
		stream.Write(payload);
	}

	private static byte[] Meta(MapDocument document)
	{
		using var stream = new MemoryStream(MaxMetaBytes);
		Span<byte> fixedPart = stackalloc byte[MetaFixedBytes];
		document.Id.TryWriteBytes(fixedPart);
		BinaryPrimitives.WriteUInt32LittleEndian(fixedPart[16..], document.Revision);
		BinaryPrimitives.WriteInt64LittleEndian(fixedPart[20..], document.CreatedUnixSeconds);
		BinaryPrimitives.WriteInt64LittleEndian(fixedPart[28..], document.ModifiedUnixSeconds);
		document.ParentId.TryWriteBytes(fixedPart[36..]);
		document.ParentHash.CopyTo(fixedPart[52..]);
		BinaryPrimitives.WriteUInt16LittleEndian(fixedPart[84..], document.PrefabCatalogCount);
		stream.Write(fixedPart);
		WriteText(stream, document.Name);
		WriteText(stream, document.Author);
		WriteText(stream, document.Description);
		return stream.ToArray();
	}

	private static void WriteText(Stream stream, string text)
	{
		byte[] bytes = Encoding.UTF8.GetBytes(text ?? string.Empty);
		stream.WriteByte((byte)bytes.Length);
		stream.Write(bytes);
	}

	private static byte[] Terrain(MapDocument document)
	{
		HeightField field = document.Terrain;
		MapBands bands = document.Bands;
		using var stream = new MemoryStream(1024);
		Span<byte> fixedPart = stackalloc byte[TerrainFixedBytes];
		BinaryPrimitives.WriteUInt16LittleEndian(fixedPart, (ushort)field.Width);
		BinaryPrimitives.WriteUInt16LittleEndian(fixedPart[2..], (ushort)field.Depth);
		BinaryPrimitives.WriteUInt16LittleEndian(fixedPart[4..], (ushort)MapLimits.TerrainSpacingMillimeters);
		BinaryPrimitives.WriteInt16LittleEndian(fixedPart[6..], bands.WaterLevel);
		BinaryPrimitives.WriteUInt16LittleEndian(fixedPart[8..], bands.SlopeGain);
		BinaryPrimitives.WriteUInt16LittleEndian(fixedPart[10..], bands.SlopeFree);
		BinaryPrimitives.WriteUInt16LittleEndian(fixedPart[12..], bands.SandTop);
		BinaryPrimitives.WriteUInt16LittleEndian(fixedPart[14..], bands.MudTop);
		BinaryPrimitives.WriteUInt16LittleEndian(fixedPart[16..], bands.GrassTop);
		BinaryPrimitives.WriteUInt16LittleEndian(fixedPart[18..], bands.Blend);
		stream.Write(fixedPart);

		byte[] residuals = PredictHeights(field.ToSamples(), field.Width, field.Depth);
		using (var deflate = new ZLibStream(stream, CompressionLevel.Optimal, leaveOpen: true))
		{
			deflate.Write(residuals);
		}

		return stream.ToArray();
	}

	private static byte[] Objects(MapDocument document)
	{
		using var stream = new MemoryStream(2 + (document.Objects.Count * 12));
		Span<byte> bytes = stackalloc byte[MaxObjectBytes];
		BinaryPrimitives.WriteUInt16LittleEndian(bytes, (ushort)document.Objects.Count);
		stream.Write(bytes[..2]);
		foreach (MapObject placed in document.Objects)
		{
			BinaryPrimitives.WriteUInt16LittleEndian(bytes, placed.PrefabId);
			BinaryPrimitives.WriteInt16LittleEndian(bytes[2..], placed.X);
			BinaryPrimitives.WriteInt16LittleEndian(bytes[4..], placed.Y);
			BinaryPrimitives.WriteInt16LittleEndian(bytes[6..], placed.Z);
			BinaryPrimitives.WriteUInt16LittleEndian(bytes[8..], placed.Yaw);
			bytes[10] = placed.Flags;
			ReadOnlySpan<MapParameter> parameters = placed.Parameters;
			bytes[11] = (byte)parameters.Length;
			int at = 12;
			foreach (MapParameter parameter in parameters)
			{
				bytes[at] = parameter.Key;
				BinaryPrimitives.WriteInt32LittleEndian(bytes[(at + 1)..], parameter.Value);
				at += 5;
			}

			stream.Write(bytes[..at]);
		}

		return stream.ToArray();
	}

	private static byte[] Roads(MapDocument document)
	{
		using var stream = new MemoryStream(1 + (document.Roads.Count * 64));
		stream.WriteByte((byte)document.Roads.Count);
		Span<byte> point = stackalloc byte[4];
		foreach (MapRoad road in document.Roads)
		{
			stream.WriteByte(road.Style);
			stream.WriteByte(road.WidthDecimeters);
			stream.WriteByte(road.GradeLimitPermille);
			stream.WriteByte((byte)road.Points.Length);
			foreach (RoadPoint each in road.Points)
			{
				BinaryPrimitives.WriteInt16LittleEndian(point, each.X);
				BinaryPrimitives.WriteInt16LittleEndian(point[2..], each.Z);
				stream.Write(point);
			}
		}

		return stream.ToArray();
	}

	// ---- reading --------------------------------------------------------------------------

	/// <summary>
	/// Reads a map from a stream: at most <see cref="MapLimits.MaxFileBytes"/> of it,
	/// and a stream with more is refused unread. The stream is read, never seeked.
	/// </summary>
	public static bool TryRead(Stream stream, out MapDocument document, out string error)
	{
		document = null;
		if (stream == null || !stream.CanRead)
		{
			error = "not a readable stream";
			return false;
		}

		var buffer = new byte[MapLimits.MaxFileBytes + 1];
		int read = 0;
		try
		{
			int got;
			while (read < buffer.Length && (got = stream.Read(buffer, read, buffer.Length - read)) > 0)
			{
				read += got;
			}
		}
		catch (IOException exception)
		{
			error = $"the file could not be read: {exception.Message}";
			return false;
		}

		return TryRead(buffer.AsSpan(0, read), out document, out error);
	}

	/// <summary>Reads a map from its bytes. False, with the reason, for anything but a whole and well-formed file.</summary>
	public static bool TryRead(ReadOnlySpan<byte> file, out MapDocument document, out string error)
	{
		document = null;
		error = null;
		if (file.Length > MapLimits.MaxFileBytes)
		{
			error = $"the file is over {MapLimits.MaxFileBytes:N0} bytes";
			return false;
		}

		if (file.Length < MagicBytes || !file[..MagicBytes].SequenceEqual(Magic))
		{
			error = "not a gdpyr map";
			return false;
		}

		if (file.Length < HeaderBytes)
		{
			error = "the file ends in its header";
			return false;
		}

		if (file[MagicBytes] != Version)
		{
			error = $"map format version {file[MagicBytes]}; this build reads {Version}";
			return false;
		}

		if (file[MagicBytes + 1] != ChunkCount)
		{
			error = $"the file has {file[MagicBytes + 1]} chunks; a map has {ChunkCount}";
			return false;
		}

		int at = HeaderBytes;
		var result = new MapDocument(new HeightField(2, 2));
		if (!NextChunk(file, ref at, MetaTag, MaxMetaBytes, out ReadOnlySpan<byte> meta, out error)
			|| !ReadMeta(meta, result, out error)
			|| !NextChunk(file, ref at, TerrainTag, MaxTerrainBytes, out ReadOnlySpan<byte> terrain, out error)
			|| !ReadTerrain(terrain, out HeightField field, out MapBands bands, out error)
			|| !NextChunk(file, ref at, ObjectsTag, MaxObjectsBytes, out ReadOnlySpan<byte> objects, out error)
			|| !ReadObjects(objects, result, out error)
			|| !NextChunk(file, ref at, RoadsTag, MaxRoadsBytes, out ReadOnlySpan<byte> roads, out error)
			|| !ReadRoads(roads, result, out error))
		{
			return false;
		}

		if (at != file.Length)
		{
			error = $"{file.Length - at} bytes after the last chunk";
			return false;
		}

		document = new MapDocument(field)
		{
			Id = result.Id,
			Revision = result.Revision,
			CreatedUnixSeconds = result.CreatedUnixSeconds,
			ModifiedUnixSeconds = result.ModifiedUnixSeconds,
			ParentId = result.ParentId,
			ParentHash = result.ParentHash,
			PrefabCatalogCount = result.PrefabCatalogCount,
			Name = result.Name,
			Author = result.Author,
			Description = result.Description,
			Bands = bands,
		};
		document.Objects.AddRange(result.Objects);
		document.Roads.AddRange(result.Roads);
		return true;
	}

	/// <summary>A map's hash: the SHA-256 of its file (LE-D15).</summary>
	public static byte[] Hash(ReadOnlySpan<byte> file) => SHA256.HashData(file);

	/// <summary>A hash as lower-case hex: what a cache file is named by and the wire and the console show.</summary>
	public static string HashHex(ReadOnlySpan<byte> file) => Convert.ToHexString(Hash(file)).ToLowerInvariant();

	/// <summary>The next chunk, which must be <paramref name="tag"/> and no longer than its limit or the file.</summary>
	private static bool NextChunk(ReadOnlySpan<byte> file, ref int at, ReadOnlySpan<byte> tag, int limit,
		out ReadOnlySpan<byte> payload, out string error)
	{
		payload = default;
		error = null;
		string name = Encoding.ASCII.GetString(tag);
		if (file.Length - at < ChunkHeaderBytes)
		{
			error = $"the file ends before its {name} chunk";
			return false;
		}

		ReadOnlySpan<byte> found = file.Slice(at, 4);
		if (!found.SequenceEqual(tag))
		{
			error = IsKnownTag(found)
				? $"a {Printable(found)} chunk where the {name} chunk belongs"
				: $"an unknown chunk {Printable(found)} where the {name} chunk belongs";
			return false;
		}

		uint length = BinaryPrimitives.ReadUInt32LittleEndian(file[(at + 4)..]);
		at += ChunkHeaderBytes;
		if (length > (uint)limit)
		{
			error = $"the {name} chunk is {length:N0} bytes, over its limit of {limit:N0}";
			return false;
		}

		if (length > (uint)(file.Length - at))
		{
			error = $"the {name} chunk's {length:N0} bytes run past the end of the file";
			return false;
		}

		payload = file.Slice(at, (int)length);
		at += (int)length;
		return true;
	}

	private static bool IsKnownTag(ReadOnlySpan<byte> tag) =>
		tag.SequenceEqual(MetaTag) || tag.SequenceEqual(TerrainTag) || tag.SequenceEqual(ObjectsTag)
		|| tag.SequenceEqual(RoadsTag);

	/// <summary>A tag as text, for a reason: its printable ASCII, and any other byte in hex.</summary>
	private static string Printable(ReadOnlySpan<byte> tag)
	{
		var text = new StringBuilder(16);
		foreach (byte b in tag)
		{
			text.Append(b is >= 0x20 and < 0x7F ? ((char)b).ToString() : $"\\x{b:x2}");
		}

		return $"'{text}'";
	}

	private static bool ReadMeta(ReadOnlySpan<byte> payload, MapDocument into, out string error)
	{
		error = null;
		if (payload.Length < MetaFixedBytes)
		{
			error = "the META chunk is too short";
			return false;
		}

		into.Id = new Guid(payload[..16]);
		into.Revision = BinaryPrimitives.ReadUInt32LittleEndian(payload[16..]);
		into.CreatedUnixSeconds = BinaryPrimitives.ReadInt64LittleEndian(payload[20..]);
		into.ModifiedUnixSeconds = BinaryPrimitives.ReadInt64LittleEndian(payload[28..]);
		into.ParentId = new Guid(payload.Slice(36, 16));
		into.ParentHash = payload.Slice(52, MapDocument.HashBytes).ToArray();
		into.PrefabCatalogCount = BinaryPrimitives.ReadUInt16LittleEndian(payload[84..]);

		int at = MetaFixedBytes;
		if (!ReadText(payload, ref at, "name", MapDocument.MaxNameBytes, false, out string name, out error)
			|| !ReadText(payload, ref at, "author", MapDocument.MaxAuthorBytes, false, out string author, out error)
			|| !ReadText(payload, ref at, "description", MapDocument.MaxDescriptionBytes, true, out string description,
				out error))
		{
			return false;
		}

		if (at != payload.Length)
		{
			error = $"the META chunk has {payload.Length - at} bytes left over";
			return false;
		}

		into.Name = name;
		into.Author = author;
		into.Description = description;
		return true;
	}

	/// <summary>
	/// A u8 length and that much UTF-8. Refused: a length over the limit or past the
	/// chunk, bytes that are not UTF-8, and a control character — in a description a
	/// line break is allowed and nothing else.
	/// </summary>
	private static bool ReadText(ReadOnlySpan<byte> payload, ref int at, string what, int limit, bool lines,
		out string text, out string error)
	{
		text = null;
		error = null;
		if (at >= payload.Length)
		{
			error = $"the META chunk ends before its {what}";
			return false;
		}

		int length = payload[at++];
		if (length > limit)
		{
			error = $"the {what} is {length} bytes, over {limit}";
			return false;
		}

		if (length > payload.Length - at)
		{
			error = $"the {what} runs past the end of the META chunk";
			return false;
		}

		ReadOnlySpan<byte> bytes = payload.Slice(at, length);
		at += length;
		if (!Utf8.IsValid(bytes))
		{
			error = $"the {what} is not UTF-8";
			return false;
		}

		text = StrictUtf8.GetString(bytes);
		foreach (char c in text)
		{
			if (char.IsControl(c) && !(lines && c == '\n'))
			{
				error = $"the {what} holds a control character";
				text = null;
				return false;
			}
		}

		return true;
	}

	private static bool ReadTerrain(ReadOnlySpan<byte> payload, out HeightField field, out MapBands bands,
		out string error)
	{
		field = null;
		bands = default;
		error = null;
		if (payload.Length < TerrainFixedBytes)
		{
			error = "the TERR chunk is too short";
			return false;
		}

		int width = BinaryPrimitives.ReadUInt16LittleEndian(payload);
		int depth = BinaryPrimitives.ReadUInt16LittleEndian(payload[2..]);
		int spacing = BinaryPrimitives.ReadUInt16LittleEndian(payload[4..]);
		if (width != MapLimits.TerrainSamples || depth != MapLimits.TerrainSamples
			|| spacing != MapLimits.TerrainSpacingMillimeters)
		{
			error = $"the terrain is {width} × {depth} samples at {spacing} mm; this build reads "
				+ $"{MapLimits.TerrainSamples} × {MapLimits.TerrainSamples} at {MapLimits.TerrainSpacingMillimeters:N0} mm";
			return false;
		}

		bands = new MapBands
		{
			WaterLevel = BinaryPrimitives.ReadInt16LittleEndian(payload[6..]),
			SlopeGain = BinaryPrimitives.ReadUInt16LittleEndian(payload[8..]),
			SlopeFree = BinaryPrimitives.ReadUInt16LittleEndian(payload[10..]),
			SandTop = BinaryPrimitives.ReadUInt16LittleEndian(payload[12..]),
			MudTop = BinaryPrimitives.ReadUInt16LittleEndian(payload[14..]),
			GrassTop = BinaryPrimitives.ReadUInt16LittleEndian(payload[16..]),
			Blend = BinaryPrimitives.ReadUInt16LittleEndian(payload[18..]),
		};

		int expected = width * depth * 2;
		var residuals = new byte[expected];
		if (!Inflate(payload[TerrainFixedBytes..], residuals, out error))
		{
			return false;
		}

		var samples = new short[width * depth];
		for (int z = 0; z < depth; z++)
		{
			for (int x = 0; x < width; x++)
			{
				int i = (z * width) + x;
				short residual = BinaryPrimitives.ReadInt16LittleEndian(residuals.AsSpan(i * 2));
				short sample = unchecked((short)(residual + Predict(samples, width, x, z)));
				if (sample < MapLimits.MinHeightSample || sample > MapLimits.MaxHeightSample)
				{
					error = $"sample ({x}, {z}) is {HeightField.ToMeters(sample):0.###} m, outside "
						+ $"{MapLimits.MinHeightMeters:0} … +{MapLimits.MaxHeightMeters:0} m";
					return false;
				}

				samples[i] = sample;
			}
		}

		field = HeightField.FromSamples(samples, width, depth);
		return true;
	}

	/// <summary>
	/// Inflates the heights into exactly <paramref name="into"/>'s length, reading one
	/// byte past it to know there is no more: a stream that inflates to less or to more
	/// is refused, and one that does not inflate at all is a reason, not an exception.
	/// The inflater never writes more than the buffer, so no stream can make it allocate.
	/// </summary>
	private static bool Inflate(ReadOnlySpan<byte> deflated, byte[] into, out string error)
	{
		error = null;
		int read = 0;
		bool more;
		try
		{
			using var source = new MemoryStream(deflated.ToArray(), writable: false);
			using var inflater = new ZLibStream(source, CompressionMode.Decompress);
			int got;
			while (read < into.Length && (got = inflater.Read(into, read, into.Length - read)) > 0)
			{
				read += got;
			}

			Span<byte> past = stackalloc byte[1];
			more = read == into.Length && inflater.Read(past) > 0;
		}
		catch (InvalidDataException exception)
		{
			error = $"the heights do not inflate: {exception.Message}";
			return false;
		}

		if (more)
		{
			error = $"the heights inflate to more than {into.Length:N0} bytes";
			return false;
		}

		if (read != into.Length)
		{
			error = $"the heights inflate to {read:N0} bytes, not {into.Length:N0}";
			return false;
		}

		return true;
	}

	private static bool ReadObjects(ReadOnlySpan<byte> payload, MapDocument into, out string error)
	{
		error = null;
		if (payload.Length < 2)
		{
			error = "the OBJS chunk is too short";
			return false;
		}

		int count = BinaryPrimitives.ReadUInt16LittleEndian(payload);
		if (count > MapLimits.MaxObjects)
		{
			error = $"{count} objects, over {MapLimits.MaxObjects}";
			return false;
		}

		int at = 2;
		Span<MapParameter> parameters = stackalloc MapParameter[MapLimits.MaxObjectParameters];
		for (int n = 0; n < count; n++)
		{
			if (payload.Length - at < 12)
			{
				error = $"the OBJS chunk ends in object {n}";
				return false;
			}

			ushort id = BinaryPrimitives.ReadUInt16LittleEndian(payload[at..]);
			short x = BinaryPrimitives.ReadInt16LittleEndian(payload[(at + 2)..]);
			short y = BinaryPrimitives.ReadInt16LittleEndian(payload[(at + 4)..]);
			short z = BinaryPrimitives.ReadInt16LittleEndian(payload[(at + 6)..]);
			ushort yaw = BinaryPrimitives.ReadUInt16LittleEndian(payload[(at + 8)..]);
			byte flags = payload[at + 10];
			int parameterCount = payload[at + 11];
			at += 12;
			if (parameterCount > MapLimits.MaxObjectParameters)
			{
				error = $"object {n} has {parameterCount} parameters, over {MapLimits.MaxObjectParameters}";
				return false;
			}

			if (payload.Length - at < parameterCount * 5)
			{
				error = $"the OBJS chunk ends in object {n}'s parameters";
				return false;
			}

			for (int p = 0; p < parameterCount; p++)
			{
				byte key = payload[at];
				for (int q = 0; q < p; q++)
				{
					if (parameters[q].Key == key)
					{
						error = $"object {n} gives parameter {key} twice";
						return false;
					}
				}

				parameters[p] = new MapParameter(key, BinaryPrimitives.ReadInt32LittleEndian(payload[(at + 1)..]));
				at += 5;
			}

			into.Objects.Add(new MapObject(id, x, y, z, yaw, flags, parameters[..parameterCount]));
		}

		if (at != payload.Length)
		{
			error = $"the OBJS chunk has {payload.Length - at} bytes left over";
			return false;
		}

		return true;
	}

	private static bool ReadRoads(ReadOnlySpan<byte> payload, MapDocument into, out string error)
	{
		error = null;
		if (payload.Length < 1)
		{
			error = "the ROAD chunk is too short";
			return false;
		}

		int count = payload[0];
		if (count > MapLimits.MaxRoads)
		{
			error = $"{count} roads, over {MapLimits.MaxRoads}";
			return false;
		}

		int at = 1;
		Span<RoadPoint> points = stackalloc RoadPoint[MapLimits.MaxRoadPoints];
		for (int n = 0; n < count; n++)
		{
			if (payload.Length - at < 4)
			{
				error = $"the ROAD chunk ends in road {n}";
				return false;
			}

			byte style = payload[at];
			byte width = payload[at + 1];
			byte grade = payload[at + 2];
			int pointCount = payload[at + 3];
			at += 4;
			if (pointCount > MapLimits.MaxRoadPoints)
			{
				error = $"road {n} has {pointCount} points, over {MapLimits.MaxRoadPoints}";
				return false;
			}

			if (payload.Length - at < pointCount * 4)
			{
				error = $"the ROAD chunk ends in road {n}'s points";
				return false;
			}

			for (int p = 0; p < pointCount; p++)
			{
				points[p] = new RoadPoint(BinaryPrimitives.ReadInt16LittleEndian(payload[at..]),
					BinaryPrimitives.ReadInt16LittleEndian(payload[(at + 2)..]));
				at += 4;
			}

			into.Roads.Add(new MapRoad(style, width, grade, points[..pointCount]));
		}

		if (at != payload.Length)
		{
			error = $"the ROAD chunk has {payload.Length - at} bytes left over";
			return false;
		}

		return true;
	}
}
