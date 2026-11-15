using System;
using System.Collections.Generic;
using System.IO;
using Gdpyr.Core;
using Gdpyr.Sim.Level;
using Godot;

// Godot has a FileAccess of its own, and this file wants both: Godot's for what is inside
// the pack (res://), System.IO for user:// and the machine's own paths, as DemoFiles does.
using FileAccess = Godot.FileAccess;

namespace Gdpyr.Level;

/// <summary>
/// Where data maps are, and the one place a map's bytes are read (docs/LEVEL_EDITOR.md
/// §9.1): <c>user://maps/&lt;name&gt;.map</c>, one's own; <c>user://maps/cache/&lt;hash&gt;.map</c>,
/// received from a server (LE7, <see cref="TryCache"/>); <c>res://Maps/&lt;id&gt;.map</c>, bundled with the build and listed in
/// <see cref="MapCatalog"/>.
///
/// A file is a map only if it reads (§6.8) and the validator finds no error in it (§6.9):
/// <see cref="TryLoad"/> asks both, every time, of the bytes it read, and hashes those
/// bytes (LE-D15). A bundled map that does not hash to its catalog entry is refused:
/// it is not the file this build was made with.
/// </summary>
public static class MapLibrary
{
	public const string UserDirectory = "user://maps/";
	public const string CacheDirectory = "user://maps/cache/";

	/// <summary>One's own maps, as a real path on this machine.</summary>
	public static string UserRoot => ProjectSettings.GlobalizePath(UserDirectory);

	/// <summary>Received maps, as a real path on this machine.</summary>
	public static string CacheRoot => ProjectSettings.GlobalizePath(CacheDirectory);

	/// <summary>
	/// What a <c>--map</c> reference names (§9.5), read and validated: a catalog id, a
	/// library name, 8 or more hex digits of a hash, or a path ending <c>.map</c>. A scene
	/// map comes back with no document. False with every reason when it names nothing or
	/// names a file that is not a map.
	/// </summary>
	public static bool TryResolve(string reference, out MapInfo map, out MapDocument document, out byte[] file,
		out string error)
	{
		map = default;
		document = null;
		file = null;

		var known = new Dictionary<string, string>(StringComparer.Ordinal);
		MapReferenceKind kind = MapNames.Resolve(reference,
			id => MapCatalog.TryFind(id, out _),
			name => File.Exists(Path.Combine(UserRoot, MapNames.FileName(name))),
			KnownHashes(known), out string key, out error);

		switch (kind)
		{
			case MapReferenceKind.Catalog:
				MapCatalog.TryFind(key, out map);
				return !map.IsData || TryLoad(map, out document, out file, out error);

			case MapReferenceKind.Library:
				return TryLoadFile(UserDirectory + MapNames.FileName(key), null, out map, out document, out file, out error);

			case MapReferenceKind.Hash:
				if (FindBundled(key, out MapInfo bundled))
				{
					map = bundled;
					return TryLoad(map, out document, out file, out error);
				}

				return TryLoadFile(known[key], key, out map, out document, out file, out error);

			case MapReferenceKind.Path:
				return TryLoadFile(key, null, out map, out document, out file, out error);

			default:
				return false;
		}
	}

	/// <summary>
	/// Reads, hashes, reads as a map and validates a data map's file. For a bundled map the
	/// hash must be its catalog entry's.
	/// </summary>
	public static bool TryLoad(in MapInfo map, out MapDocument document, out string error) =>
		TryLoad(map, out document, out _, out error);

	/// <summary>
	/// <see cref="TryLoad(in MapInfo, out MapDocument, out string)"/>, with the bytes that were read:
	/// the file a server sends is the one it loaded (§6.10).
	/// </summary>
	public static bool TryLoad(in MapInfo map, out MapDocument document, out byte[] file, out string error)
	{
		document = null;
		file = null;
		if (!map.IsData)
		{
			error = $"{map} is a scene, not a map file";
			return false;
		}

		if (!TryReadBytes(map.Path, out byte[] bytes, out error))
		{
			return false;
		}

		string hash = MapFile.HashHex(bytes);
		if (!string.IsNullOrEmpty(map.Hash) && !string.Equals(hash, map.Hash, StringComparison.Ordinal))
		{
			error = string.IsNullOrEmpty(map.Id)
				? $"{map.Path} hashes to {hash[..8]}…, not the {map.Hash[..8]}… it did when it was listed: it has changed since"
				: $"{map.Path} hashes to {hash[..8]}…, not the {map.Hash[..8]}… this build was made with";
			return false;
		}

		if (!TryParse(map.Path, bytes, out document, out error))
		{
			return false;
		}

		file = bytes;
		return true;
	}

	/// <summary>A file named by its path, as a map from the library: no catalog id, its name and hash its own.</summary>
	private static bool TryLoadFile(string path, string expectedHash, out MapInfo map, out MapDocument document,
		out byte[] file, out string error)
	{
		map = default;
		document = null;
		file = null;
		if (!TryReadBytes(path, out byte[] bytes, out error))
		{
			return false;
		}

		string hash = MapFile.HashHex(bytes);
		if (expectedHash != null && !string.Equals(hash, expectedHash, StringComparison.Ordinal))
		{
			error = $"{path} changed while it was being read: it hashes to {hash[..8]}…, not {expectedHash[..8]}…";
			return false;
		}

		if (!TryParse(path, bytes, out document, out error))
		{
			return false;
		}

		map = MapInfo.Data(string.Empty, document.Name, path, hash, document.Description);
		file = bytes;
		return true;
	}

	/// <summary>§6.8's reader, then §6.9's validator: a file the validator finds an error in is not a map.</summary>
	private static bool TryParse(string path, byte[] bytes, out MapDocument document, out string error)
	{
		if (!MapFile.TryRead(bytes, out document, out error))
		{
			error = $"{path} is not a map: {error}";
			return false;
		}

		MapReport report = MapValidator.Validate(document);
		if (!report.IsPlayable)
		{
			var reasons = new List<string>();
			foreach (MapIssue issue in report.Issues)
			{
				if (issue.Severity == MapIssueSeverity.Error)
				{
					reasons.Add(issue.Message);
				}
			}

			error = $"{path} does not play: {string.Join("; ", reasons)}";
			document = null;
			return false;
		}

		error = null;
		return true;
	}

	/// <summary>
	/// A file's bytes, at most <see cref="MapLimits.MaxFileBytes"/> of them: <c>res://</c>
	/// from the pack, anything else from the disk.
	/// </summary>
	public static bool TryReadBytes(string path, out byte[] bytes, out string error)
	{
		bytes = null;
		error = null;
		if (string.IsNullOrEmpty(path))
		{
			error = "no path";
			return false;
		}

		if (path.StartsWith("res://", StringComparison.Ordinal))
		{
			using FileAccess file = FileAccess.Open(path, FileAccess.ModeFlags.Read);
			if (file == null)
			{
				error = $"{path} is not in this build ({FileAccess.GetOpenError()})";
				return false;
			}

			ulong length = file.GetLength();
			if (length > MapLimits.MaxFileBytes)
			{
				error = $"{path} is {length:N0} bytes, over the {MapLimits.MaxFileBytes:N0} a map may be";
				return false;
			}

			bytes = file.GetBuffer((long)length);
			if (bytes.Length != (int)length)
			{
				error = $"{path}: read {bytes.Length:N0} of {length:N0} bytes";
				bytes = null;
				return false;
			}

			return true;
		}

		string real = path.StartsWith("user://", StringComparison.Ordinal) ? ProjectSettings.GlobalizePath(path) : path;
		try
		{
			var info = new FileInfo(real);
			if (!info.Exists)
			{
				error = $"there is no file {path}";
				return false;
			}

			if (info.Length > MapLimits.MaxFileBytes)
			{
				error = $"{path} is {info.Length:N0} bytes, over the {MapLimits.MaxFileBytes:N0} a map may be";
				return false;
			}

			bytes = File.ReadAllBytes(real);
			return true;
		}
		catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException
			or ArgumentException)
		{
			error = $"{path}: {e.Message}";
			return false;
		}
	}

	/// <summary>The received map with this hash is kept here (LE-D16).</summary>
	public static string CachePathOf(string hash) => Path.Combine(CacheRoot, hash + MapFile.Extension);

	/// <summary>Where a map this process has is: built in, one's own, or received.</summary>
	public enum Source : byte
	{
		None,
		Bundled,
		Library,
		Cache,
	}

	/// <summary>
	/// The map with this hash, if this process has it (docs/LEVEL_EDITOR.md §9.3, LE7): bundled with the
	/// build, one's own, or in the cache under the hash's name — read, hashed again and validated, as
	/// every map is when it is loaded. A file in the cache under the hash's name that does not hash to
	/// it is not that map, and is passed over (<paramref name="note"/> says so): the fetch that follows
	/// writes the real one over it. False with <see cref="Source.None"/> when there is no such map; false
	/// with where it is, and <paramref name="error"/>, when there is and it does not play on this build.
	/// </summary>
	public static bool TryFindHash(string hash, out MapInfo map, out MapDocument document, out byte[] file,
		out Source source, out string error, out string note)
	{
		map = default;
		document = null;
		file = null;
		source = Source.None;
		error = null;
		note = null;
		if (!Gdpyr.Sim.Level.MapTransfer.IsHash(hash))
		{
			error = $"'{hash}' is not a map's hash";
			return false;
		}

		if (FindBundled(hash, out MapInfo bundled))
		{
			source = Source.Bundled;
			map = bundled;
			return TryLoad(map, out document, out file, out error);
		}

		string cached = CachePathOf(hash);
		if (File.Exists(cached))
		{
			if (TryReadBytes(cached, out byte[] bytes, out string unread) && MapFile.HashHex(bytes) == hash)
			{
				source = Source.Cache;
				return TryLoadFile(cached, hash, out map, out document, out file, out error);
			}

			note = unread ?? $"{CacheDirectory}{hash}{MapFile.Extension} is not that map: it hashes to"
				+ $" {Gdpyr.Sim.Level.MapTransfer.Short(MapFile.HashHex(bytes))}";
		}

		foreach (string path in FilesIn(UserRoot))
		{
			string stem = Path.GetFileNameWithoutExtension(path);
			if (MapNames.TryParseName(stem, out string name, out _) && name == stem
				&& TryReadBytes(path, out byte[] bytes, out _) && MapFile.HashHex(bytes) == hash)
			{
				source = Source.Library;
				return TryLoadFile(path, hash, out map, out document, out file, out error);
			}
		}

		return false;
	}

	/// <summary>
	/// Keeps a received map (§6.10): a file that has been hashed, read and validated, written to
	/// <c>cache/&lt;hash&gt;.map</c> through a file beside it and a rename, so the name is only ever
	/// the whole map. Its bytes as they arrived: a file is not re-encoded (LE-D15).
	/// </summary>
	public static bool TryCache(byte[] file, MapDocument document, out MapInfo map, out string error)
	{
		string hash = MapFile.HashHex(file);
		string path = CachePathOf(hash);
		map = MapInfo.Data(string.Empty, document.Name, path, hash, document.Description);
		string part = path + ".part";
		try
		{
			Directory.CreateDirectory(CacheRoot);
			File.WriteAllBytes(part, file);
			File.Move(part, path, overwrite: true);
			error = null;
			return true;
		}
		catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException
			or ArgumentException)
		{
			try
			{
				File.Delete(part);
			}
			catch (Exception gone) when (gone is IOException or UnauthorizedAccessException)
			{
				// Nothing more to do: a .part is never read as a map.
			}

			error = $"{path}: {e.Message}";
			return false;
		}
	}

	/// <summary>
	/// One's own maps a round can be played on, for the host panel's picker (§9.1): every map in
	/// <c>user://maps/</c> that reads and that the validator finds no error in, by name (LE-D16).
	/// </summary>
	public static List<MapInfo> PlayableOwnMaps()
	{
		var maps = new List<MapInfo>();
		foreach (StoredMap stored in MapStore.List(UserRoot))
		{
			if (stored.Reads && stored.Report.IsPlayable)
			{
				maps.Add(MapInfo.Data(string.Empty, stored.Document.Name, stored.Path, stored.Hash,
					stored.Document.Description));
			}
		}

		return maps;
	}

	/// <summary>Every hash a map can be named by, and where its file is: the bundled maps', the library's and the cache's.</summary>
	private static IEnumerable<string> KnownHashes(Dictionary<string, string> where)
	{
		foreach (MapInfo map in MapCatalog.All)
		{
			if (map.IsData && !string.IsNullOrEmpty(map.Hash))
			{
				where.TryAdd(map.Hash, map.Path);
			}
		}

		// One's own maps by their library names: an autosave, `name.autosave.map`, is not one (LE4).
		foreach (string file in FilesIn(UserRoot))
		{
			string stem = Path.GetFileNameWithoutExtension(file);
			if (MapNames.TryParseName(stem, out string name, out _) && name == stem
				&& TryReadBytes(file, out byte[] bytes, out _))
			{
				where.TryAdd(MapFile.HashHex(bytes), file);
			}
		}

		// A received map is kept under its hash's name, and is hashed again when it is read.
		foreach (string file in FilesIn(CacheRoot))
		{
			string name = Path.GetFileNameWithoutExtension(file);
			if (name.Length == MapNames.HashHexLength && MapNames.IsHashPrefix(name))
			{
				where.TryAdd(name.ToLowerInvariant(), file);
			}
		}

		return where.Keys;
	}

	private static bool FindBundled(string hash, out MapInfo map)
	{
		foreach (MapInfo candidate in MapCatalog.All)
		{
			if (candidate.IsData && string.Equals(candidate.Hash, hash, StringComparison.Ordinal))
			{
				map = candidate;
				return true;
			}
		}

		map = default;
		return false;
	}

	private static IEnumerable<string> FilesIn(string directory)
	{
		try
		{
			return Directory.Exists(directory)
				? Directory.GetFiles(directory, "*" + MapFile.Extension)
				: Array.Empty<string>();
		}
		catch (Exception e) when (e is IOException or UnauthorizedAccessException)
		{
			return Array.Empty<string>();
		}
	}
}
