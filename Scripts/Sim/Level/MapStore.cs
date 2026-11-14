using System;
using System.Collections.Generic;
using System.IO;

namespace Gdpyr.Sim.Level;

/// <summary>One of one's own maps, as the editor lists it (docs/LEVEL_EDITOR.md §8.1).</summary>
public readonly struct StoredMap
{
	/// <summary>Its library name: the file's, less <c>.map</c>.</summary>
	public readonly string Name;

	/// <summary>Where it is.</summary>
	public readonly string Path;

	/// <summary>What it read as; null when it did not read.</summary>
	public readonly MapDocument Document;

	/// <summary>Why it did not read; null when it did.</summary>
	public readonly string Error;

	/// <summary>The SHA-256 of its bytes, hex; null when they were not read.</summary>
	public readonly string Hash;

	/// <summary>What §6.9 found in it: null when it did not read.</summary>
	public readonly MapReport Report;

	/// <summary>Whether an autosave newer than it is beside it (§8.6).</summary>
	public readonly bool HasAutosave;

	public StoredMap(string name, string path, MapDocument document, string error, string hash, MapReport report,
		bool hasAutosave)
	{
		Name = name;
		Path = path;
		Document = document;
		Error = error;
		Hash = hash;
		Report = report;
		HasAutosave = hasAutosave;
	}

	public bool Reads => Document != null;

	public override string ToString() =>
		Reads
			? $"{Name}: '{Document.Name}', revision {Document.Revision}, {MapStore.Describe(Report)}"
			: $"{Name}: {Error}";
}

/// <summary>
/// One's own maps on disk (docs/LEVEL_EDITOR.md §8.6, §9.2): a directory of
/// <c>&lt;name&gt;.map</c>, where the editor saves, autosaves and lists. Engine-free and on
/// <c>System.IO</c>, as the demos' folder is, so that what a save leaves on the disk is a
/// test; <c>MapLibrary</c> says which directory is <c>user://maps/</c>.
///
/// <b>Save</b> writes <c>name.map.tmp</c>, copies the last <c>name.map</c> to
/// <c>name.map.bak</c>, and renames the new file over <c>name.map</c>: a crash at any point
/// leaves a whole map under the name, and a rename is one step. It is the next revision,
/// modified now; the autosave beside it is dropped. <b>An autosave</b> is
/// <c>name.autosave.map</c>, written the same way, with the revision unchanged; the editor
/// offers it back when it opens the map. Neither name is a library name — a dot is not
/// one of its characters (§9.1) — so neither is listed as a map of its own.
/// </summary>
public static class MapStore
{
	/// <summary>What an autosave's name adds before <c>.map</c>.</summary>
	public const string AutosaveSuffix = ".autosave";

	/// <summary>What the last saved file is kept as, after <c>.map</c>.</summary>
	public const string BackupSuffix = ".bak";

	/// <summary>What a file being written is called until it is whole, after <c>.map</c>.</summary>
	public const string TempSuffix = ".tmp";

	public static string PathOf(string directory, string name) => Path.Combine(directory, MapNames.FileName(name));

	public static string AutosavePathOf(string directory, string name) =>
		Path.Combine(directory, name + AutosaveSuffix + MapFile.Extension);

	public static string BackupPathOf(string directory, string name) => PathOf(directory, name) + BackupSuffix;

	public static string TempPathOf(string directory, string name) => PathOf(directory, name) + TempSuffix;

	/// <summary>Whether one's own map of this name is on the disk.</summary>
	public static bool Exists(string directory, string name) =>
		MapNames.TryParseName(name, out string parsed, out _) && File.Exists(PathOf(directory, parsed));

	/// <summary>
	/// Saves a document as one's own map <paramref name="name"/>: the next revision,
	/// modified at <paramref name="unixSeconds"/>, through a file beside it and a rename;
	/// the last file kept as <c>.bak</c>, and the autosave dropped. A document a file cannot
	/// hold, or a disk that refuses, is an error, and the document is left as it was.
	/// Errors in the map do not stop it: an unfinished map is a file (§8.7).
	/// </summary>
	public static bool TrySave(MapDocument document, string directory, string name, long unixSeconds, out byte[] file,
		out string error)
	{
		file = null;
		if (!MapNames.TryParseName(name, out name, out error))
		{
			return false;
		}

		uint revision = document.Revision;
		long modified = document.ModifiedUnixSeconds;
		document.Revision = revision == uint.MaxValue ? revision : revision + 1;
		document.ModifiedUnixSeconds = unixSeconds;
		if (!MapFile.TryWrite(document, out file, out error)
			|| !TryWriteWhole(directory, PathOf(directory, name), TempPathOf(directory, name),
				BackupPathOf(directory, name), file, out error))
		{
			document.Revision = revision;
			document.ModifiedUnixSeconds = modified;
			file = null;
			return false;
		}

		DiscardAutosave(directory, name);
		return true;
	}

	/// <summary>
	/// Writes a document as the autosave of <paramref name="name"/>: the revision and the
	/// times as they are, since nothing was saved (§8.6).
	/// </summary>
	public static bool TryAutosave(MapDocument document, string directory, string name, out string error)
	{
		if (!MapNames.TryParseName(name, out name, out error) || !MapFile.TryWrite(document, out byte[] file, out error))
		{
			return false;
		}

		string path = AutosavePathOf(directory, name);
		return TryWriteWhole(directory, path, path + TempSuffix, null, file, out error);
	}

	/// <summary>Drops the autosave of <paramref name="name"/>, if there is one: it was saved, or turned down.</summary>
	public static void DiscardAutosave(string directory, string name)
	{
		if (!MapNames.TryParseName(name, out name, out _))
		{
			return;
		}

		try
		{
			File.Delete(AutosavePathOf(directory, name));
		}
		catch (Exception e) when (e is IOException or UnauthorizedAccessException)
		{
			// An autosave that cannot be dropped is offered again next time; nothing is lost.
		}
	}

	/// <summary>
	/// Reads one's own map. It is read as §6.8 reads a file and not validated: an unfinished
	/// map is opened to be finished. The bytes come back, and their hash is the map's.
	/// </summary>
	public static bool TryOpen(string directory, string name, out MapDocument document, out byte[] file, out string error)
	{
		document = null;
		file = null;
		return MapNames.TryParseName(name, out name, out error)
			&& TryReadFile(PathOf(directory, name), out document, out file, out error);
	}

	/// <summary>
	/// The autosave of <paramref name="name"/>, read, when there is one to offer: it reads,
	/// and it is not the map as saved — an autosave whose document is the saved file's is
	/// dropped rather than offered.
	/// </summary>
	public static bool TryReadAutosave(string directory, string name, out MapDocument document, out string error)
	{
		document = null;
		error = null;
		if (!MapNames.TryParseName(name, out name, out error))
		{
			return false;
		}

		string path = AutosavePathOf(directory, name);
		if (!File.Exists(path))
		{
			error = "no autosave";
			return false;
		}

		if (!TryReadFile(path, out document, out _, out error))
		{
			return false;
		}

		if (TryReadFile(PathOf(directory, name), out MapDocument saved, out _, out _) && saved.SameAs(document, out _))
		{
			DiscardAutosave(directory, name);
			document = null;
			error = "the autosave is the map as saved";
			return false;
		}

		return true;
	}

	/// <summary>
	/// One's own maps (§8.1): every <c>&lt;name&gt;.map</c> in <paramref name="directory"/>
	/// whose name is a library name, by name, each with what it read as and what the
	/// validator finds in it. A map that does not validate is listed, to be finished — what
	/// it may not be is hosted, sent or listed to play (LE-D16); one that does not read is
	/// listed with why. Decided by LE4.
	/// </summary>
	public static List<StoredMap> List(string directory)
	{
		var maps = new List<StoredMap>();
		string[] files;
		try
		{
			files = Directory.Exists(directory)
				? Directory.GetFiles(directory, "*" + MapFile.Extension)
				: Array.Empty<string>();
		}
		catch (Exception e) when (e is IOException or UnauthorizedAccessException)
		{
			return maps;
		}

		Array.Sort(files, StringComparer.Ordinal);
		foreach (string path in files)
		{
			string stem = Path.GetFileNameWithoutExtension(path);
			if (!MapNames.TryParseName(stem, out string name, out _) || name != stem)
			{
				continue;
			}

			bool autosave = File.Exists(AutosavePathOf(directory, name));
			if (TryReadFile(path, out MapDocument document, out byte[] file, out string error))
			{
				maps.Add(new StoredMap(name, path, document, null, MapFile.HashHex(file), MapValidator.Validate(document),
					autosave));
			}
			else
			{
				maps.Add(new StoredMap(name, path, null, error, null, null, autosave));
			}
		}

		return maps;
	}

	/// <summary>
	/// A copy of a map to be one's own (§9.2): a new GUID, the source's GUID and hash as its
	/// parent, revision 0 and made now, under its new name. What it holds is the source's;
	/// the author is the source's until somebody types another (§9.2).
	/// </summary>
	public static MapDocument Fork(MapDocument source, ReadOnlySpan<byte> sourceHash, Guid id, string name,
		long unixSeconds)
	{
		MapDocument copy = source.Clone();
		BecomeFork(copy, sourceHash, id, unixSeconds);
		if (name != null)
		{
			copy.Name = name;
		}

		return copy;
	}

	/// <summary>
	/// Makes a document the fork of what it was (§9.2): what the editor's <b>Save as</b> does to
	/// a map that has a file, so that two files never share a lineage. Its GUID and
	/// <paramref name="hash"/> — the file it was last saved as — become its parent's; it is a
	/// new GUID, at revision 0, made now. Decided by LE4.
	/// </summary>
	public static void BecomeFork(MapDocument document, ReadOnlySpan<byte> hash, Guid id, long unixSeconds)
	{
		document.ParentId = document.Id;
		document.ParentHash = hash.Length == MapDocument.HashBytes ? hash.ToArray() : null;
		document.Id = id;
		document.Revision = 0;
		document.CreatedUnixSeconds = unixSeconds;
		document.ModifiedUnixSeconds = unixSeconds;
	}

	/// <summary>A file's bytes, at most <see cref="MapLimits.MaxFileBytes"/>, read as a map.</summary>
	public static bool TryReadFile(string path, out MapDocument document, out byte[] file, out string error)
	{
		document = null;
		file = null;
		try
		{
			var info = new FileInfo(path);
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

			file = File.ReadAllBytes(path);
		}
		catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException
			or ArgumentException)
		{
			error = $"{path}: {e.Message}";
			return false;
		}

		if (!MapFile.TryRead(file, out document, out error))
		{
			error = $"{Path.GetFileName(path)} is not a map: {error}";
			file = null;
			return false;
		}

		return true;
	}

	/// <summary>"no issues", or how many errors and warnings.</summary>
	public static string Describe(MapReport report)
	{
		if (report == null)
		{
			return "not read";
		}

		if (report.Errors == 0 && report.Warnings == 0)
		{
			return "no issues";
		}

		string errors = report.Errors == 1 ? "1 error" : $"{report.Errors} errors";
		string warnings = report.Warnings == 1 ? "1 warning" : $"{report.Warnings} warnings";
		return report.Errors == 0 ? warnings : report.Warnings == 0 ? errors : $"{errors}, {warnings}";
	}

	/// <summary>
	/// <paramref name="bytes"/> as the whole of <paramref name="path"/>: written beside it and
	/// renamed over it, the last kept at <paramref name="backup"/> when one is named.
	/// </summary>
	private static bool TryWriteWhole(string directory, string path, string temp, string backup, byte[] bytes,
		out string error)
	{
		error = null;
		try
		{
			Directory.CreateDirectory(directory);
			using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
			{
				stream.Write(bytes);
				stream.Flush(true);
			}

			if (backup != null && File.Exists(path))
			{
				File.Copy(path, backup, true);
			}

			File.Move(temp, path, true);
			return true;
		}
		catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException
			or ArgumentException)
		{
			error = $"{path}: {e.Message}";
			try
			{
				File.Delete(temp);
			}
			catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
			{
				// What could not be written cannot always be removed either; it is not the map.
			}

			return false;
		}
	}
}
