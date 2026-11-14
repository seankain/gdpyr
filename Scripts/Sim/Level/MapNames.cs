using System;
using System.Collections.Generic;

namespace Gdpyr.Sim.Level;

/// <summary>What a <c>--map</c> reference turned out to name (docs/LEVEL_EDITOR.md §9.5).</summary>
public enum MapReferenceKind : byte
{
	/// <summary>Nothing it could name was found.</summary>
	None,

	/// <summary>A <c>MapCatalog</c> id: a scene map, or a data map bundled with the build.</summary>
	Catalog,

	/// <summary>One's own map, <c>user://maps/&lt;name&gt;.map</c>.</summary>
	Library,

	/// <summary>A map whose hash starts with the reference: in the library, the cache or the bundled maps.</summary>
	Hash,

	/// <summary>A file named by its path.</summary>
	Path,
}

/// <summary>
/// The one place a typed string becomes a map's name or a path (docs/LEVEL_EDITOR.md
/// §9.1): a name is <c>[a-z0-9_-]</c>, 1–48 of them, parsed as a demo's is
/// (<c>DemoFormat.TryParseName</c>); a reference is a catalog id, a library name, a
/// hash's first 8 or more hex digits, or a path ending <c>.map</c>, tried in that
/// order. Engine-free: where the maps are is the engine's (<c>MapLibrary</c>), and what
/// a reference resolves to among them is answered here, from what it is told exists.
/// </summary>
public static class MapNames
{
	/// <summary>The longest name: a document's own limit (<see cref="MapDocument.MaxNameBytes"/>).</summary>
	public const int MaxNameLength = MapDocument.MaxNameBytes;

	/// <summary>The fewest hex digits of a hash that name a map by it.</summary>
	public const int MinHashPrefix = 8;

	/// <summary>A hash's hex digits: SHA-256's 32 bytes.</summary>
	public const int HashHexLength = MapDocument.HashBytes * 2;

	/// <summary>The longest path a reference may be.</summary>
	public const int MaxPathLength = 4096;

	/// <summary>
	/// A library name: lower-case letters, digits, <c>_</c> and <c>-</c>, 1–48 of them,
	/// not starting with <c>-</c> (a flag) or <c>.</c>. Whitespace either side is dropped;
	/// nothing else is changed, so a name is the file's name less <c>.map</c>.
	/// </summary>
	public static bool TryParseName(string text, out string name, out string error)
	{
		name = null;
		error = null;
		text = text?.Trim();
		if (string.IsNullOrEmpty(text))
		{
			error = "a map needs a name";
			return false;
		}

		if (text.Length > MaxNameLength)
		{
			error = $"a map's name is at most {MaxNameLength} characters";
			return false;
		}

		foreach (char c in text)
		{
			if (!IsNameChar(c))
			{
				error = $"'{Printable(c)}' is not allowed in a map's name: a-z, 0-9, _ and - only";
				return false;
			}
		}

		if (text[0] == '-')
		{
			error = "a map's name may not start with '-'";
			return false;
		}

		name = text;
		return true;
	}

	/// <summary>The file a library name is kept in.</summary>
	public static string FileName(string name) => name + MapFile.Extension;

	/// <summary>Whether a text is the start of a hash: 8–64 lower- or upper-case hex digits.</summary>
	public static bool IsHashPrefix(string text)
	{
		if (text == null || text.Length < MinHashPrefix || text.Length > HashHexLength)
		{
			return false;
		}

		foreach (char c in text)
		{
			if (!Uri.IsHexDigit(c))
			{
				return false;
			}
		}

		return true;
	}

	/// <summary>
	/// Whether <paramref name="text"/> can be a reference at all, before anything is looked
	/// for: a path ending <c>.map</c>, or something that is a name or a hash prefix. Only
	/// syntax; what it names is <see cref="Resolve"/>'s.
	/// </summary>
	public static bool TryParseReference(string text, out string reference, out bool isPath, out string error)
	{
		reference = null;
		isPath = false;
		error = null;
		text = text?.Trim();
		if (string.IsNullOrEmpty(text))
		{
			error = "a map reference is empty";
			return false;
		}

		foreach (char c in text)
		{
			if (char.IsControl(c))
			{
				error = "a map reference may not hold a control character";
				return false;
			}
		}

		if (text.EndsWith(MapFile.Extension, StringComparison.OrdinalIgnoreCase))
		{
			if (text.Length > MaxPathLength)
			{
				error = $"a map's path is at most {MaxPathLength} characters";
				return false;
			}

			reference = text;
			isPath = true;
			return true;
		}

		if (IsHashPrefix(text) || TryParseName(text, out _, out _))
		{
			reference = text;
			return true;
		}

		error = $"'{text}' is not a map: a catalog id, a map's name (a-z, 0-9, _ and -, at most {MaxNameLength}),"
			+ $" {MinHashPrefix} or more hex digits of its hash, or a path ending {MapFile.Extension}";
		return false;
	}

	/// <summary>
	/// What a reference names, in §9.5's order: a path ending <c>.map</c> is a path and
	/// nothing else; otherwise a catalog id, then a library name, then a hash prefix,
	/// the first that something answers to. A hash prefix that more than one map's hash
	/// starts with names none of them, and says so.
	/// </summary>
	/// <param name="hashes">The hashes of every map that can be named by one, lower-case hex.</param>
	public static MapReferenceKind Resolve(string text, Func<string, bool> catalogHas, Func<string, bool> libraryHas,
		IEnumerable<string> hashes, out string key, out string error)
	{
		key = null;
		if (!TryParseReference(text, out string reference, out bool isPath, out error))
		{
			return MapReferenceKind.None;
		}

		if (isPath)
		{
			key = reference;
			return MapReferenceKind.Path;
		}

		if (catalogHas != null && catalogHas(reference))
		{
			key = reference;
			return MapReferenceKind.Catalog;
		}

		if (TryParseName(reference, out string name, out _) && libraryHas != null && libraryHas(name))
		{
			key = name;
			return MapReferenceKind.Library;
		}

		if (IsHashPrefix(reference))
		{
			string prefix = reference.ToLowerInvariant();
			string found = null;
			int matches = 0;
			var seen = new HashSet<string>(StringComparer.Ordinal);
			foreach (string hash in hashes ?? Array.Empty<string>())
			{
				if (hash != null && seen.Add(hash) && hash.StartsWith(prefix, StringComparison.Ordinal))
				{
					found = hash;
					matches++;
				}
			}

			if (matches == 1)
			{
				key = found;
				return MapReferenceKind.Hash;
			}

			if (matches > 1)
			{
				error = $"'{reference}' is the start of {matches} maps' hashes: give more of it";
				return MapReferenceKind.None;
			}
		}

		error = $"no map is called '{reference}': not a catalog id, not a map in the library, and no map's hash starts with it";
		return MapReferenceKind.None;
	}

	private static bool IsNameChar(char c) => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_' or '-';

	private static string Printable(char c) => char.IsControl(c) ? $"\\u{(int)c:x4}" : c.ToString();
}
