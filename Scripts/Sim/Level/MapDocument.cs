using System;
using System.Collections.Generic;
using System.Text;
using Godot;

namespace Gdpyr.Sim.Level;

/// <summary>One parameter of a placed object: a key the object's prefab defines, and its value.</summary>
public readonly struct MapParameter : IEquatable<MapParameter>
{
	public readonly byte Key;
	public readonly int Value;

	public MapParameter(byte key, int value)
	{
		Key = key;
		Value = value;
	}

	public bool Equals(MapParameter other) => Key == other.Key && Value == other.Value;

	public override bool Equals(object obj) => obj is MapParameter other && Equals(other);

	public override int GetHashCode() => HashCode.Combine(Key, Value);

	public override string ToString() => $"{Key}={Value}";
}

/// <summary>
/// A placed prefab, as the file holds it (docs/LEVEL_EDITOR.md §6.8): its id, where
/// it stands in centimetres, its yaw in 1/65,536 of a turn, a flag byte and up to
/// eight parameters. The quantities are the wire's (<see cref="Quantize"/>) and are
/// kept as they are stored, so that a document read from a file is the document
/// written and a peer that builds from one derives nothing (LE-D14).
///
/// Immutable: an edit replaces an object, and undo puts the old one back.
/// </summary>
public sealed class MapObject : IEquatable<MapObject>
{
	private static readonly MapParameter[] None = Array.Empty<MapParameter>();

	private readonly MapParameter[] _parameters;

	/// <summary>
	/// An object as stored. Parameters are kept sorted by key; a key given twice, or
	/// more than <see cref="MapLimits.MaxObjectParameters"/>, is an argument error —
	/// the file's reader refuses both before it gets here.
	/// </summary>
	public MapObject(ushort prefabId, short x, short y, short z, ushort yaw, byte flags = 0,
		ReadOnlySpan<MapParameter> parameters = default)
	{
		if (parameters.Length > MapLimits.MaxObjectParameters)
		{
			throw new ArgumentException($"an object carries at most {MapLimits.MaxObjectParameters} parameters",
				nameof(parameters));
		}

		PrefabId = prefabId;
		X = x;
		Y = y;
		Z = z;
		Yaw = yaw;
		Flags = flags;
		if (parameters.IsEmpty)
		{
			_parameters = None;
			return;
		}

		_parameters = parameters.ToArray();
		Array.Sort(_parameters, (a, b) => a.Key.CompareTo(b.Key));
		for (int i = 1; i < _parameters.Length; i++)
		{
			if (_parameters[i].Key == _parameters[i - 1].Key)
			{
				throw new ArgumentException($"parameter {_parameters[i].Key} is given twice", nameof(parameters));
			}
		}
	}

	/// <summary>An object at a point in metres and a yaw in radians, both quantized as the file stores them.</summary>
	public static MapObject At(ushort prefabId, Vector3 position, float yaw, ReadOnlySpan<MapParameter> parameters = default) =>
		new(prefabId, Quantize.MetersToI16(position.X), Quantize.MetersToI16(position.Y), Quantize.MetersToI16(position.Z),
			Quantize.YawToU16(yaw), 0, parameters);

	public ushort PrefabId { get; }

	/// <summary>Where it stands [cm].</summary>
	public short X { get; }

	/// <summary>
	/// The height it stands at [cm]. For a prefab that makes a pad, the pad's level is
	/// the step nearest this (<see cref="PrefabPlacement.PadLevelSample"/>).
	/// </summary>
	public short Y { get; }

	public short Z { get; }

	/// <summary>Which way it faces, in 1/65,536 of a turn: a node's <c>Rotation.Y</c>, as <see cref="StructurePose.Yaw"/> is.</summary>
	public ushort Yaw { get; }

	/// <summary>None is defined at this version: a set bit is the validator's to refuse.</summary>
	public byte Flags { get; }

	/// <summary>The parameters, sorted by key.</summary>
	public ReadOnlySpan<MapParameter> Parameters => _parameters;

	/// <summary>Where it stands [m].</summary>
	public Vector3 Position => new(Quantize.I16ToMeters(X), Quantize.I16ToMeters(Y), Quantize.I16ToMeters(Z));

	/// <summary>Its yaw [radians], in [0, 2π).</summary>
	public float YawRadians => Quantize.U16ToYaw(Yaw);

	public bool TryGetParameter(byte key, out int value)
	{
		foreach (MapParameter parameter in _parameters)
		{
			if (parameter.Key == key)
			{
				value = parameter.Value;
				return true;
			}
		}

		value = 0;
		return false;
	}

	/// <summary>A parameter's value, or <paramref name="otherwise"/> when the object does not carry it.</summary>
	public int Parameter(byte key, int otherwise) => TryGetParameter(key, out int value) ? value : otherwise;

	/// <summary>The same object at another height [cm].</summary>
	public MapObject WithY(short y) => new(PrefabId, X, y, Z, Yaw, Flags, _parameters);

	public bool Equals(MapObject other) =>
		other is not null && PrefabId == other.PrefabId && X == other.X && Y == other.Y && Z == other.Z
		&& Yaw == other.Yaw && Flags == other.Flags && _parameters.AsSpan().SequenceEqual(other._parameters);

	public override bool Equals(object obj) => obj is MapObject other && Equals(other);

	public override int GetHashCode()
	{
		var hash = new HashCode();
		hash.Add(PrefabId);
		hash.Add(X);
		hash.Add(Y);
		hash.Add(Z);
		hash.Add(Yaw);
		hash.Add(Flags);
		foreach (MapParameter parameter in _parameters)
		{
			hash.Add(parameter);
		}

		return hash.ToHashCode();
	}

	public override string ToString()
	{
		string name = PrefabSpecs.TryGet(PrefabId, out PrefabSpec spec) ? spec.Name : $"prefab {PrefabId}";
		return $"{name} at ({X / 100f:0.00}, {Y / 100f:0.00}, {Z / 100f:0.00}) m";
	}
}

/// <summary>A road's control point [cm].</summary>
public readonly struct RoadPoint : IEquatable<RoadPoint>
{
	public readonly short X;
	public readonly short Z;

	public RoadPoint(short x, short z)
	{
		X = x;
		Z = z;
	}

	public static RoadPoint At(float x, float z) => new(Quantize.MetersToI16(x), Quantize.MetersToI16(z));

	public float XMeters => Quantize.I16ToMeters(X);

	public float ZMeters => Quantize.I16ToMeters(Z);

	public bool Equals(RoadPoint other) => X == other.X && Z == other.Z;

	public override bool Equals(object obj) => obj is RoadPoint other && Equals(other);

	public override int GetHashCode() => HashCode.Combine(X, Z);
}

/// <summary>
/// What a road was drawn from (docs/LEVEL_EDITOR.md §6.6, §6.8): its control points,
/// width and grade limit. The ground it graded is in the heights; this travels so
/// that a received map can be cloned and its roads edited (LE-D14). The rules that
/// grade it are LE6's (<c>RoadPath</c>).
/// </summary>
public sealed class MapRoad : IEquatable<MapRoad>
{
	/// <summary>The one surface there is (§14: one road surface).</summary>
	public const byte StyleDirt = 0;

	/// <summary>The widths a road may be [dm]: 4, 6 and 8 m.</summary>
	public static ReadOnlySpan<byte> Widths => new byte[] { 40, 60, 80 };

	/// <summary>The shallowest grade limit [0.1 %]: 6 %.</summary>
	public const byte MinGradeLimitPermille = 60;

	/// <summary>The steepest: 15 %, 8.5°, under the 9.1° a 4x4 climbs from rest (LE-D24).</summary>
	public const byte MaxGradeLimitPermille = 150;

	/// <summary>Unless set: 10 %.</summary>
	public const byte DefaultGradeLimitPermille = 100;

	private readonly RoadPoint[] _points;

	public MapRoad(byte style, byte widthDecimeters, byte gradeLimitPermille, ReadOnlySpan<RoadPoint> points)
	{
		if (points.Length > MapLimits.MaxRoadPoints)
		{
			throw new ArgumentException($"a road has at most {MapLimits.MaxRoadPoints} points", nameof(points));
		}

		Style = style;
		WidthDecimeters = widthDecimeters;
		GradeLimitPermille = gradeLimitPermille;
		_points = points.ToArray();
	}

	public byte Style { get; }

	/// <summary>Across [dm].</summary>
	public byte WidthDecimeters { get; }

	/// <summary>The steepest its profile may be [0.1 %].</summary>
	public byte GradeLimitPermille { get; }

	public ReadOnlySpan<RoadPoint> Points => _points;

	public bool Equals(MapRoad other) =>
		other is not null && Style == other.Style && WidthDecimeters == other.WidthDecimeters
		&& GradeLimitPermille == other.GradeLimitPermille && _points.AsSpan().SequenceEqual(other._points);

	public override bool Equals(object obj) => obj is MapRoad other && Equals(other);

	public override int GetHashCode() => HashCode.Combine(Style, WidthDecimeters, GradeLimitPermille, _points.Length);
}

/// <summary>
/// A map's water level and its six band numbers as the file holds them (§6.4, §6.8):
/// the water in steps of 1/256 m, the six in thousandths. <see cref="BandSettings"/>
/// is what they mean.
/// </summary>
public struct MapBands : IEquatable<MapBands>
{
	public short WaterLevel;
	public ushort SlopeGain;
	public ushort SlopeFree;
	public ushort SandTop;
	public ushort MudTop;
	public ushort GrassTop;
	public ushort Blend;

	/// <summary>
	/// A new map's: §6.4's numbers, with the water 5 m under the level ground a new map
	/// is. Decided by LE2: at 0 m, as <see cref="BandSettings.Default"/> has it, level
	/// ground at 0 m is sand; 5 m over the water it is grass, clear of the half metre
	/// of the mud's blend under 4.5 m, and water is where the ground is dug 5 m down.
	/// </summary>
	public static MapBands NewMap
	{
		get
		{
			BandSettings settings = BandSettings.Default;
			settings.WaterLevelMeters = MapDocument.NewMapWaterLevelMeters;
			return From(settings);
		}
	}

	/// <summary>The numbers nearest the settings that a file can hold. A number outside a field's range is clamped to it.</summary>
	public static MapBands From(in BandSettings settings) => new()
	{
		WaterLevel = HeightField.ToSample(settings.WaterLevelMeters),
		SlopeGain = Thousandths(settings.SlopeGainMetersPerDegree),
		SlopeFree = Thousandths(settings.SlopeFreeDegrees),
		SandTop = Thousandths(settings.SandTopMeters),
		MudTop = Thousandths(settings.MudTopMeters),
		GrassTop = Thousandths(settings.GrassTopMeters),
		Blend = Thousandths(settings.BlendMeters),
	};

	/// <summary>What the numbers mean. Exact: n / 1000 is the float nearest n thousandths, as a literal is.</summary>
	public readonly BandSettings Settings => new()
	{
		WaterLevelMeters = HeightField.ToMeters(WaterLevel),
		SlopeGainMetersPerDegree = SlopeGain / 1000f,
		SlopeFreeDegrees = SlopeFree / 1000f,
		SandTopMeters = SandTop / 1000f,
		MudTopMeters = MudTop / 1000f,
		GrassTopMeters = GrassTop / 1000f,
		BlendMeters = Blend / 1000f,
	};

	/// <summary>The water's height [m].</summary>
	public readonly float WaterLevelMeters => HeightField.ToMeters(WaterLevel);

	private static ushort Thousandths(float number) =>
		(ushort)Math.Clamp(MathF.Round(number * 1000f), 0f, ushort.MaxValue);

	public readonly bool Equals(MapBands other) =>
		WaterLevel == other.WaterLevel && SlopeGain == other.SlopeGain && SlopeFree == other.SlopeFree
		&& SandTop == other.SandTop && MudTop == other.MudTop && GrassTop == other.GrassTop && Blend == other.Blend;

	public override readonly bool Equals(object obj) => obj is MapBands other && Equals(other);

	public override readonly int GetHashCode() =>
		HashCode.Combine(WaterLevel, SlopeGain, SlopeFree, SandTop, MudTop, GrassTop, Blend);
}

/// <summary>
/// What a map is, in memory (docs/LEVEL_EDITOR.md §5, §6.8): who it is and where it
/// came from, its ground, its water and bands, its objects in the order the world
/// builds them, and what its roads were drawn from. The editor's commands change one;
/// <see cref="MapFile"/> writes it and reads it back; <see cref="MapValidator"/> says
/// whether a round can be played on it.
///
/// The ground is a <see cref="HeightField"/>, whose heights are whole steps of
/// 1/256 m outside a stroke (§6.1). Everything else is held as the file holds it.
/// </summary>
public sealed class MapDocument
{
	/// <summary>The longest name, in UTF-8 bytes.</summary>
	public const int MaxNameBytes = 48;

	/// <summary>The longest author, in UTF-8 bytes. Typed into the map's settings, never taken from the machine (§9.2).</summary>
	public const int MaxAuthorBytes = 32;

	/// <summary>The longest description, in UTF-8 bytes.</summary>
	public const int MaxDescriptionBytes = 255;

	/// <summary>A hash is a SHA-256 (LE-D15).</summary>
	public const int HashBytes = 32;

	/// <summary>Where a new map's water is [m]: see <see cref="MapBands.NewMap"/>.</summary>
	public const float NewMapWaterLevelMeters = -5f;

	private byte[] _parentHash = new byte[HashBytes];

	public MapDocument(HeightField terrain)
	{
		Terrain = terrain ?? throw new ArgumentNullException(nameof(terrain));
	}

	/// <summary>A new map: level ground at 0 m, the water under it, nothing on it.</summary>
	public static MapDocument Create(Guid id, string name, long unixSeconds) => new(new HeightField())
	{
		Id = id,
		Name = name ?? string.Empty,
		CreatedUnixSeconds = unixSeconds,
		ModifiedUnixSeconds = unixSeconds,
		PrefabCatalogCount = PrefabSpecs.Count,
		Bands = MapBands.NewMap,
	};

	/// <summary>Its lineage: the same for every revision and for nothing else (LE-D15).</summary>
	public Guid Id { get; set; }

	/// <summary>Saves so far: a save writes the next one (§9.2).</summary>
	public uint Revision { get; set; }

	public long CreatedUnixSeconds { get; set; }

	public long ModifiedUnixSeconds { get; set; }

	/// <summary>The map this was cloned from; empty for one made from new.</summary>
	public Guid ParentId { get; set; }

	/// <summary>The SHA-256 of the file this was cloned from: 32 bytes, all zero for none.</summary>
	public byte[] ParentHash
	{
		get => _parentHash;
		set
		{
			if (value != null && value.Length != HashBytes)
			{
				throw new ArgumentException($"a hash is {HashBytes} bytes", nameof(value));
			}

			_parentHash = value == null ? new byte[HashBytes] : (byte[])value.Clone();
		}
	}

	/// <summary>How many prefabs the catalog that made it had: a reader with fewer may still take it, if it uses none past its own (§9.3).</summary>
	public ushort PrefabCatalogCount { get; set; }

	public string Name { get; set; } = string.Empty;

	public string Author { get; set; } = string.Empty;

	public string Description { get; set; } = string.Empty;

	public HeightField Terrain { get; private set; }

	public MapBands Bands { get; set; } = MapBands.NewMap;

	/// <summary>In the order the world builds them: <c>LevelBuilder</c> names nodes by it, so the name-sorted indices are every peer's (§6.8).</summary>
	public List<MapObject> Objects { get; } = new();

	public List<MapRoad> Roads { get; } = new();

	/// <summary>A copy that shares nothing that can change.</summary>
	public MapDocument Clone()
	{
		var copy = new MapDocument(Terrain.Clone())
		{
			Id = Id,
			Revision = Revision,
			CreatedUnixSeconds = CreatedUnixSeconds,
			ModifiedUnixSeconds = ModifiedUnixSeconds,
			ParentId = ParentId,
			ParentHash = _parentHash,
			PrefabCatalogCount = PrefabCatalogCount,
			Name = Name,
			Author = Author,
			Description = Description,
			Bands = Bands,
		};
		copy.Objects.AddRange(Objects);
		copy.Roads.AddRange(Roads);
		return copy;
	}

	/// <summary>How many objects are of a group. What <see cref="PrefabPlacementResult.LimitReached"/> and the validator's counts read.</summary>
	public int CountOf(PrefabGroup group)
	{
		int count = 0;
		foreach (MapObject placed in Objects)
		{
			if (PrefabSpecs.TryGet(placed.PrefabId, out PrefabSpec spec) && spec.Group == group)
			{
				count++;
			}
		}

		return count;
	}

	/// <summary>
	/// Whether two documents are the same map, field by field and sample by sample as
	/// stored; <paramref name="difference"/> names the first that is not. What "a file
	/// reads back as it was written" is checked with.
	/// </summary>
	public bool SameAs(MapDocument other, out string difference)
	{
		difference = null;
		if (other == null)
		{
			difference = "the other is null";
			return false;
		}

		if (Id != other.Id)
		{
			difference = "the id";
		}
		else if (Revision != other.Revision)
		{
			difference = "the revision";
		}
		else if (CreatedUnixSeconds != other.CreatedUnixSeconds || ModifiedUnixSeconds != other.ModifiedUnixSeconds)
		{
			difference = "the times";
		}
		else if (ParentId != other.ParentId || !_parentHash.AsSpan().SequenceEqual(other._parentHash))
		{
			difference = "the parent";
		}
		else if (PrefabCatalogCount != other.PrefabCatalogCount)
		{
			difference = "the catalog count";
		}
		else if (Name != other.Name || Author != other.Author || Description != other.Description)
		{
			difference = "the text";
		}
		else if (!Bands.Equals(other.Bands))
		{
			difference = "the water or bands";
		}
		else if (Terrain.Width != other.Terrain.Width || Terrain.Depth != other.Terrain.Depth)
		{
			difference = "the terrain's size";
		}
		else
		{
			short[] mine = Terrain.ToSamples();
			short[] theirs = other.Terrain.ToSamples();
			for (int i = 0; i < mine.Length && difference == null; i++)
			{
				if (mine[i] != theirs[i])
				{
					difference = $"sample ({i % Terrain.Width}, {i / Terrain.Width})";
				}
			}

			if (difference == null)
			{
				difference = FirstDifference(Objects, other.Objects, "object")
					?? FirstDifference(Roads, other.Roads, "road");
			}
		}

		return difference == null;
	}

	/// <summary>A string's length as a file stores it.</summary>
	public static int Utf8Bytes(string text) => Encoding.UTF8.GetByteCount(text ?? string.Empty);

	private static string FirstDifference<T>(List<T> mine, List<T> theirs, string what) where T : IEquatable<T>
	{
		if (mine.Count != theirs.Count)
		{
			return $"the {what} count";
		}

		for (int i = 0; i < mine.Count; i++)
		{
			if (!mine[i].Equals(theirs[i]))
			{
				return $"{what} {i}";
			}
		}

		return null;
	}
}
