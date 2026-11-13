using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Gdpyr.Sim.Level;

namespace Gdpyr.MapTool;

/// <summary>
/// <c>gdpyr-map</c>: a map file from the command line (docs/LEVEL_EDITOR.md §11, LE2;
/// §9.5). What a box is given before anything is copied to it, and where the first
/// maps come from.
///
/// <code>
///   gdpyr-map info     &lt;file&gt;      what the file holds, its hash, and what the validator says
///   gdpyr-map hash     &lt;file&gt;      the SHA-256 the wire names it by
///   gdpyr-map validate &lt;file&gt;      §6.8's reader and §6.9's validator: exit 0 if it plays
///   gdpyr-map generate &lt;file&gt; [--seed N] [--name TEXT] [--author TEXT] [--time UNIX-SECONDS] [--hills METRES]
/// </code>
///
/// Exit codes: 0 the file is a map that plays (or was written, or hashed); 1 it is
/// refused, by the reader or by the validator's errors; 2 the command line or the disk
/// was wrong.
/// </summary>
public static class Program
{
	private const int Plays = 0;
	private const int Refused = 1;
	private const int Usage = 2;

	public static int Main(string[] args)
	{
		if (args.Length < 2)
		{
			return Help();
		}

		try
		{
			return args[0] switch
			{
				"info" when args.Length == 2 => Info(args[1]),
				"hash" when args.Length == 2 => Hash(args[1]),
				"validate" when args.Length == 2 => Validate(args[1]),
				"generate" => Generate(args[1], args[2..]),
				_ => Help(),
			};
		}
		catch (IOException exception)
		{
			Console.Error.WriteLine($"gdpyr-map: {exception.Message}");
			return Usage;
		}
		catch (UnauthorizedAccessException exception)
		{
			Console.Error.WriteLine($"gdpyr-map: {exception.Message}");
			return Usage;
		}
	}

	private static int Help()
	{
		Console.Error.WriteLine("usage: gdpyr-map info <file>");
		Console.Error.WriteLine("       gdpyr-map hash <file>");
		Console.Error.WriteLine("       gdpyr-map validate <file>");
		Console.Error.WriteLine("       gdpyr-map generate <file> [--seed N] [--name TEXT] [--author TEXT] [--time UNIX-SECONDS] [--hills METRES]");
		return Usage;
	}

	/// <summary>The file's bytes, up to one past what a reader takes, so that one too large is refused by the reader and not read whole.</summary>
	private static byte[] ReadBounded(string path)
	{
		using FileStream stream = File.OpenRead(path);
		var buffer = new byte[MapLimits.MaxFileBytes + 1];
		int read = 0;
		int got;
		while (read < buffer.Length && (got = stream.Read(buffer, read, buffer.Length - read)) > 0)
		{
			read += got;
		}

		return buffer[..read];
	}

	private static int Hash(string path)
	{
		using FileStream stream = File.OpenRead(path);
		byte[] hash = System.Security.Cryptography.SHA256.HashData(stream);
		Console.WriteLine($"{Convert.ToHexString(hash).ToLowerInvariant()}  {path}");
		return Plays;
	}

	private static int Validate(string path)
	{
		byte[] file = ReadBounded(path);
		if (!MapFile.TryRead(file, out MapDocument document, out string error))
		{
			Console.WriteLine($"refused: {error}");
			return Refused;
		}

		MapReport report = MapValidator.Validate(document);
		foreach (MapIssue issue in report.Issues)
		{
			Console.WriteLine(issue);
		}

		Console.WriteLine(Summary(report));
		return report.IsPlayable ? Plays : Refused;
	}

	private static int Info(string path)
	{
		byte[] file = ReadBounded(path);
		Line("file", path);
		int chunks = (file.Length + 1023) / 1024;
		Line("bytes", $"{file.Length:N0}: {chunks:N0} chunk{(chunks == 1 ? string.Empty : "s")} of 1,024 B, {file.Length / 61_440d:0.00} s to send at one a tick");
		Line("sha-256", MapFile.HashHex(file));
		if (!MapFile.TryRead(file, out MapDocument document, out string error))
		{
			Line("refused", error);
			return Refused;
		}

		Line("format", $"version {MapFile.Version}");
		Line("name", document.Name);
		Line("author", document.Author.Length == 0 ? "(none)" : document.Author);
		Line("description", document.Description.Replace("\n", " / "));
		Line("id", $"{document.Id}, revision {document.Revision}");
		Line("created", $"{Time(document.CreatedUnixSeconds)}; modified {Time(document.ModifiedUnixSeconds)}");
		Line("parent", document.ParentId == Guid.Empty
			? "none"
			: $"{document.ParentId}, {Convert.ToHexString(document.ParentHash).ToLowerInvariant()}");
		Line("catalog", $"made with {document.PrefabCatalogCount} prefabs; this build has {PrefabSpecs.Count}");

		HeightField field = document.Terrain;
		float lowest = field.Heights.Min();
		float highest = field.Heights.Max();
		Faces(field, out int faces, out int steep);
		Line("terrain", $"{field.Width} × {field.Depth} samples at {MapLimits.TerrainSpacingMillimeters:N0} mm; "
			+ $"{lowest:0.00} … {highest:0.00} m; steepest face {HeightField.Degrees(field.SteepestFace()):0.0}°; "
			+ $"{100d * steep / faces:0.00} % of faces over {MapLimits.WalkableSlopeDegrees:0}°");
		BandSettings bands = document.Bands.Settings;
		int under = field.Heights.Count(h => h < bands.WaterLevelMeters);
		Line("water", $"{bands.WaterLevelMeters:0.00} m; {100d * under / field.Heights.Length:0.00} % of samples under it");
		Line("bands", $"u = e + {bands.SlopeGainMetersPerDegree:0.###} · max(0, s − {bands.SlopeFreeDegrees:0.###}°); "
			+ $"sand < {bands.SandTopMeters:0.###}, mud < {bands.MudTopMeters:0.###}, grass < {bands.GrassTopMeters:0.###} m; "
			+ $"blend {bands.BlendMeters:0.###} m");

		var counts = new SortedDictionary<ushort, int>();
		foreach (MapObject placed in document.Objects)
		{
			counts[placed.PrefabId] = counts.GetValueOrDefault(placed.PrefabId) + 1;
		}

		Line("objects", $"{document.Objects.Count}" + (counts.Count == 0
			? string.Empty
			: ": " + string.Join(", ", counts.Select(pair =>
				$"{(PrefabSpecs.TryGet(pair.Key, out PrefabSpec spec) ? spec.Name : $"prefab {pair.Key}")} {pair.Value}"))));
		Line("roads", $"{document.Roads.Count}");

		MapReport report = MapValidator.Validate(document);
		Line("validator", Summary(report));
		foreach (MapIssue issue in report.Issues)
		{
			Console.WriteLine($"  {issue}");
		}

		return report.IsPlayable ? Plays : Refused;
	}

	private static int Generate(string path, string[] options)
	{
		int seed = 1;
		string name = null;
		string author = string.Empty;
		long time = 0;
		float hills = MapGenerator.DefaultHillMeters;
		for (int i = 0; i < options.Length; i++)
		{
			string value = i + 1 < options.Length ? options[i + 1] : null;
			bool ok = options[i] switch
			{
				"--seed" => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out seed),
				"--name" => (name = value) != null,
				"--author" => (author = value) != null,
				"--time" => long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out time),
				"--hills" => float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out hills)
					&& hills >= 0f && hills <= 20f,
				_ => false,
			};
			if (!ok)
			{
				Console.Error.WriteLine($"gdpyr-map: '{options[i]}' {(value == null ? "wants a value" : $"does not take '{value}'")}");
				return Usage;
			}

			i++;
		}

		MapDocument document;
		try
		{
			document = MapGenerator.Generate(seed, name, time, author, hills);
		}
		catch (InvalidOperationException exception)
		{
			Console.Error.WriteLine($"gdpyr-map: {exception.Message}");
			return Refused;
		}

		if (!MapFile.TryWrite(document, out byte[] file, out string error))
		{
			Console.Error.WriteLine($"gdpyr-map: {error}");
			return Usage;
		}

		File.WriteAllBytes(path, file);
		MapReport report = MapValidator.Validate(document);
		Console.WriteLine($"{path}: '{document.Name}', seed {seed}, {file.Length:N0} bytes, sha-256 {MapFile.HashHex(file)}");
		Console.WriteLine(Summary(report));
		foreach (MapIssue issue in report.Issues)
		{
			Console.WriteLine($"  {issue}");
		}

		return report.IsPlayable ? Plays : Refused;
	}

	private static void Faces(HeightField field, out int faces, out int steep)
	{
		float limit = HeightField.Grade(MapLimits.WalkableSlopeDegrees);
		faces = 0;
		steep = 0;
		for (int z = 0; z + 1 < field.Depth; z++)
		{
			for (int x = 0; x + 1 < field.Width; x++)
			{
				faces += 2;
				steep += (field.FaceGrade(x, z, false) > limit ? 1 : 0) + (field.FaceGrade(x, z, true) > limit ? 1 : 0);
			}
		}
	}

	private static string Summary(MapReport report) =>
		$"{report.Errors} error{(report.Errors == 1 ? string.Empty : "s")}, {report.Warnings} warning{(report.Warnings == 1 ? string.Empty : "s")}"
		+ (report.IsPlayable ? ": it plays" : ": it does not play");

	private static string Time(long unixSeconds) =>
		unixSeconds is >= -62_135_596_800 and <= 253_402_300_799
			? DateTimeOffset.FromUnixTimeSeconds(unixSeconds).ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture)
			: $"{unixSeconds} (unix seconds)";

	private static void Line(string label, string value) => Console.WriteLine($"{label,-12} {value}");
}
