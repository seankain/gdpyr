using System;
using System.Collections.Generic;
using Godot;

namespace Gdpyr.Sim.Level;

/// <summary>What a prefab is to a round (docs/LEVEL_EDITOR.md LE-D9, §6.5).</summary>
public enum PrefabKind : byte
{
	/// <summary>A piece a round needs or uses (MAPS.md §3): kept inside ±115 m and out of the water.</summary>
	Round,

	/// <summary>A <c>CSGBox3D</c> in group <c>cover</c> (COVER.md §2.3).</summary>
	Cover,

	/// <summary>Set pieces: walked round, shot at, nothing more.</summary>
	Scenery,

	/// <summary>A strategist's structure that stands when a round starts, as destructible as one a builder made.</summary>
	Structure,

	/// <summary>A bridge section (§6.7): connects at its ends; its height is free.</summary>
	Bridge,
}

/// <summary>Prefabs a map counts together (§6.5's last column, §6.9's counts).</summary>
public enum PrefabGroup : byte
{
	None,
	SpawnLine,
	WeaponLocker,
	VehicleKiosk,
	Barracks,
	ResourceNode,
	StrategistAnchor,

	/// <summary>Heavy guns and mortar launchers: one group on the wire (<c>emplacement</c>).</summary>
	Emplacement,
	AmmoCan,
	Cover,
	Structure,
	Bridge,
	Scenery,
}

/// <summary>A key a prefab's objects may carry, and the values it takes.</summary>
public readonly struct PrefabParameter
{
	public readonly byte Key;
	public readonly string Name;
	public readonly int Min;
	public readonly int Max;
	public readonly int Default;

	public PrefabParameter(byte key, string name, int min, int max, int otherwise)
	{
		Key = key;
		Name = name;
		Min = min;
		Max = max;
		Default = otherwise;
	}
}

/// <summary>
/// Where a bridge section joins (§6.7): a point at the deck's top in the section's own
/// frame, and the heading out of it. Two join where they are the same point with
/// opposite headings.
/// </summary>
public readonly struct PrefabConnector
{
	public readonly Vector3 Local;

	/// <summary>Out of the section [radians], as a yaw: 0 along the section's +Z.</summary>
	public readonly float Heading;

	public PrefabConnector(Vector3 local, float heading)
	{
		Local = local;
		Heading = heading;
	}
}

/// <summary>
/// One box of a placed object, in the world: a rectangle in plan and a span of height.
/// "In the way" (LE-D11) is two of these overlapping in plan by more than
/// <see cref="PrefabSpecs.OverlapAllowanceMeters"/> and in height at all.
/// </summary>
public readonly struct PlacedBox
{
	public readonly Footprint Plan;
	public readonly float Bottom;
	public readonly float Top;

	public PlacedBox(in Footprint plan, float bottom, float top)
	{
		Plan = plan;
		Bottom = bottom;
		Top = top;
	}

	public bool Overlaps(in PlacedBox other) =>
		Bottom < other.Top && other.Bottom < Top && Plan.Overlaps(other.Plan, PrefabSpecs.OverlapAllowanceMeters);

	/// <summary>How far the box reaches from its centre, flat: a quick test before the separating axes.</summary>
	public float Reach => MathF.Sqrt((Plan.HalfWidth * Plan.HalfWidth) + (Plan.HalfDepth * Plan.HalfDepth));
}

/// <summary>
/// The ground a padded object stands on and keeps level (LE-D10): its footprint — a
/// rectangle, or a resource node's disc — out to <see cref="TerrainConform.PadMarginMeters"/>.
/// </summary>
public readonly struct PadArea
{
	public readonly bool IsDisc;
	public readonly Footprint Rect;
	public readonly float X;
	public readonly float Z;
	public readonly float Radius;

	private PadArea(bool disc, in Footprint rect, float x, float z, float radius)
	{
		IsDisc = disc;
		Rect = rect;
		X = x;
		Z = z;
		Radius = radius;
	}

	public static PadArea Of(in Footprint rect) => new(false, rect, rect.Center.X, rect.Center.Z,
		MathF.Sqrt((rect.HalfWidth * rect.HalfWidth) + (rect.HalfDepth * rect.HalfDepth)));

	public static PadArea Disc(float x, float z, float radius) => new(true, default, x, z, radius);

	/// <summary>How far a point is from the footprint, flat; zero on or in it. The distance a pad's skirt is measured by (§6.3).</summary>
	public float DistanceTo(float x, float z)
	{
		if (IsDisc)
		{
			float dx = x - X;
			float dz = z - Z;
			return MathF.Max(MathF.Sqrt((dx * dx) + (dz * dz)) - Radius, 0f);
		}

		return Rect.DistanceTo(new Vector3(x, 0f, z));
	}

	/// <summary>Whether a point is under the footprint itself.</summary>
	public bool Covers(float x, float z) =>
		IsDisc ? DistanceTo(x, z) <= 0f : Rect.Contains(new Vector3(x, 0f, z));

	/// <summary>The samples within <paramref name="margin"/> of the footprint could be in, as a rectangle on the field.</summary>
	public SampleRect Bounds(HeightField field, float margin)
	{
		float reach = Radius + margin;
		return new SampleRect(
			(int)MathF.Floor(X - reach - field.OriginX), (int)MathF.Floor(Z - reach - field.OriginZ),
			(int)MathF.Ceiling(X + reach - field.OriginX), (int)MathF.Ceiling(Z + reach - field.OriginZ))
			.Intersect(field.Bounds);
	}

	/// <summary>Levels the ground under it to <paramref name="level"/>, with the 30° skirt (§6.3). The samples moved.</summary>
	public SampleRect Apply(HeightField field, float level) =>
		IsDisc ? TerrainConform.Disc(field, X, Z, Radius, level) : TerrainConform.Pad(field, Rect, level);
}

/// <summary>
/// A prefab, engine-free (docs/LEVEL_EDITOR.md §6.5): an id, a kind, the boxes of its
/// body in its own frame (as <see cref="StructureBox"/> is), its footprint, whether it
/// makes a pad, the steepest average grade it accepts, the group it is counted in,
/// and for a bridge section its connectors. The engine half — the scene or builder
/// an id stands for — is <c>Scripts/Core/PrefabCatalog.cs</c>, LE3's and LE5's.
/// </summary>
public sealed class PrefabSpec
{
	internal PrefabSpec(ushort id, string name, PrefabKind kind, PrefabGroup group, float width, float depth,
		bool pad, float steepestDegrees, StructureBox[] boxes)
	{
		Id = id;
		Name = name;
		Kind = kind;
		Group = group;
		HalfWidth = width * 0.5f;
		HalfDepth = depth * 0.5f;
		MakesPad = pad;
		SteepestDegrees = steepestDegrees;
		Boxes = boxes;
	}

	public ushort Id { get; }

	/// <summary>The catalog's name, as §6.5's table has it.</summary>
	public string Name { get; }

	public PrefabKind Kind { get; }

	public PrefabGroup Group { get; }

	/// <summary>Half the footprint across its own X [m]. Zero for a prefab with none.</summary>
	public float HalfWidth { get; }

	/// <summary>Half the footprint along its own Z [m].</summary>
	public float HalfDepth { get; }

	/// <summary>The footprint is a disc of the capture radius parameter, not a rectangle: a resource node's.</summary>
	public bool DiscFootprint { get; internal init; }

	/// <summary>Whether placing it levels the ground under it (LE-D10).</summary>
	public bool MakesPad { get; }

	/// <summary>The steepest average grade under the footprint it is placed on [degrees]; NaN where there is no limit.</summary>
	public float SteepestDegrees { get; }

	/// <summary>Its height is where it is put, not where the ground is: a bridge section's (§6.7).</summary>
	public bool FreeHeight { get; internal init; }

	/// <summary>Its body, in its own frame, from the point it stands on.</summary>
	public IReadOnlyList<StructureBox> Boxes { get; }

	public IReadOnlyList<PrefabConnector> Connectors { get; internal init; } = Array.Empty<PrefabConnector>();

	public IReadOnlyList<PrefabParameter> Parameters { get; internal init; } = Array.Empty<PrefabParameter>();

	/// <summary>A barracks' door, in its own frame: where its units come out (<c>SpawnPoint</c>).</summary>
	public Vector3? Door { get; internal init; }

	/// <summary>One of the pieces a round needs or uses.</summary>
	public bool IsRoundPiece => Kind == PrefabKind.Round;

	public bool HasFootprint => DiscFootprint || (HalfWidth > 0f && HalfDepth > 0f);

	public bool HasGradeLimit => !float.IsNaN(SteepestDegrees);

	public bool TryGetParameter(byte key, out PrefabParameter parameter)
	{
		foreach (PrefabParameter each in Parameters)
		{
			if (each.Key == key)
			{
				parameter = each;
				return true;
			}
		}

		parameter = default;
		return false;
	}

	/// <summary>An object's value for a key this prefab defines, or the key's default.</summary>
	public int ValueOf(MapObject placed, byte key) =>
		TryGetParameter(key, out PrefabParameter parameter) ? placed.Parameter(key, parameter.Default) : 0;

	public override string ToString() => $"{Id} {Name}";
}

/// <summary>
/// The prefab catalog's engine-free half (docs/LEVEL_EDITOR.md §6.5). Ids are
/// append-only, as <c>StructureCatalog</c>'s are: an id is two bytes in a file
/// somebody else will open. <c>PrefabSpecsTests</c> holds them to their numbers.
///
/// These are the plan's starting values; LE5 authors them from the scenes, and a test
/// there holds each to its scene's colliders. Heights the table does not give are the
/// bodies' that are known — the barracks' 6 m building, the bunker's 7.12 m, the
/// structures' <c>Structures/*.tres</c> — and otherwise the room a piece needs kept clear.
/// </summary>
public static class PrefabSpecs
{
	public const ushort SpawnLine = 0;
	public const ushort WeaponLocker = 1;
	public const ushort VehicleKiosk = 2;
	public const ushort Barracks = 3;
	public const ushort ResourceNode = 4;
	public const ushort StrategistAnchor = 5;
	public const ushort HeavyGun = 6;
	public const ushort MortarLauncher = 7;
	public const ushort AmmoCan = 8;
	public const ushort CoverLowWall = 9;
	public const ushort CoverBlock = 10;
	public const ushort Bunker = 11;
	public const ushort StructurePillbox = 12;
	public const ushort StructureSandbagWall = 13;
	public const ushort StructureSniperTower = 14;
	public const ushort BridgeSpan = 15;
	public const ushort BridgeRamp = 16;

	/// <summary>Prefabs this build knows. A file's id at or past it "needs a newer build" (§6.9).</summary>
	public const ushort Count = 17;

	/// <summary>How far two boxes may interpenetrate in plan and still both stand: the strategist's own allowance.</summary>
	public const float OverlapAllowanceMeters = StructurePlacement.OverlapAllowanceMeters;

	// ---- a resource node's parameters ------------------------------------------------------

	/// <summary>A node's perk: 0 none, 1 drones (<see cref="NodePerk"/>).</summary>
	public const byte NodePerkKey = 0;

	/// <summary>A node's capture radius [cm]: the disc its pad levels (§6.5).</summary>
	public const byte NodeCaptureRadiusKey = 1;

	/// <summary><c>ResourceNode.CaptureRadiusMeters</c>' default [cm].</summary>
	public const int DefaultCaptureRadiusCentimeters = 1000;

	// ---- a bridge section's parameters -----------------------------------------------------

	/// <summary>Whether the section stands on piers: 0 or 1.</summary>
	public const byte SupportsKey = 0;

	/// <summary>The pier under the section's −Z end [cm], deck's underside down; 0 for none.</summary>
	public const byte PierBackKey = 1;

	/// <summary>The pier under the +Z end [cm].</summary>
	public const byte PierFrontKey = 2;

	/// <summary>The longest pier [cm]: from a deck at the highest ground to 0.5 m under the lowest, and some.</summary>
	public const int MaxPierCentimeters = 8100;

	// ---- a bridge section (§6.7) ----------------------------------------------------------

	public const float DeckWidthMeters = 6f;
	public const float DeckLengthMeters = 12f;
	public const float DeckThicknessMeters = 0.6f;
	public const float RailHeightMeters = 1f;
	public const float RailThicknessMeters = 0.2f;

	/// <summary>A ramp's rise over its length: 1 in 8, 7.1°, which a 4x4 climbs from rest (LE-D24).</summary>
	public const float RampRiseMeters = 1.5f;

	/// <summary>A pier's side [m].</summary>
	public const float PierSideMeters = 1.2f;

	/// <summary>
	/// A barracks' defended ring [m]: its guns stand 7.1 m off its centre and reach
	/// 100 m (<c>Barracks.DefendedRadiusMeters</c>, MAPS.md §2.3). What the validator's
	/// layout warning measures a node and the spawn line against.
	/// </summary>
	public static readonly float BarracksDefendedRadiusMeters = MathF.Sqrt((5.5f * 5.5f) + (4.5f * 4.5f)) + 100f;

	private static readonly PrefabSpec[] Specs = Build();

	/// <summary>Every prefab, by id.</summary>
	public static IReadOnlyList<PrefabSpec> All => Specs;

	public static bool TryGet(ushort id, out PrefabSpec spec)
	{
		spec = id < Specs.Length ? Specs[id] : null;
		return spec != null;
	}

	public static PrefabSpec Get(ushort id) =>
		TryGet(id, out PrefabSpec spec) ? spec : throw new ArgumentOutOfRangeException(nameof(id), $"no prefab {id}");

	/// <summary>The most a map may hold of a group; <see cref="int.MaxValue"/> where only the map's object limit holds.</summary>
	public static int MaxOf(PrefabGroup group) => group switch
	{
		PrefabGroup.VehicleKiosk => 1,
		PrefabGroup.Barracks => SimConfig.MaxBarracks,
		PrefabGroup.ResourceNode => SimConfig.MaxResourceNodes,
		PrefabGroup.StrategistAnchor => 1,
		PrefabGroup.Emplacement => SimConfig.MaxEmplacements,
		PrefabGroup.AmmoCan => SimConfig.MaxAmmoCans,
		PrefabGroup.Cover => CoverBoard.LevelCapacity,

		// Half of SimConfig.MaxStructures: the builders keep the other half (§6.5).
		PrefabGroup.Structure => SimConfig.MaxStructures / 2,
		_ => int.MaxValue,
	};

	/// <summary>The fewest a map holds of a group to be played.</summary>
	public static int MinOf(PrefabGroup group) => group switch
	{
		PrefabGroup.SpawnLine or PrefabGroup.WeaponLocker or PrefabGroup.Barracks or PrefabGroup.ResourceNode => 1,
		_ => 0,
	};

	// ---- an object in the world ------------------------------------------------------------

	/// <summary>Where an object's footprint is, or false for one without.</summary>
	public static bool TryGetPadArea(MapObject placed, out PadArea area)
	{
		area = default;
		if (!TryGet(placed.PrefabId, out PrefabSpec spec) || !spec.HasFootprint)
		{
			return false;
		}

		Vector3 at = placed.Position;
		if (spec.DiscFootprint)
		{
			area = PadArea.Disc(at.X, at.Z, spec.ValueOf(placed, NodeCaptureRadiusKey) / 100f);
			return true;
		}

		area = PadArea.Of(new Footprint(at, placed.YawRadians, spec.HalfWidth, spec.HalfDepth));
		return true;
	}

	/// <summary>
	/// An object's boxes in the world, its piers among them: what "in the way" is asked
	/// of (LE-D11). Adds them to <paramref name="into"/>.
	/// </summary>
	public static void AddBoxes(MapObject placed, List<PlacedBox> into)
	{
		if (!TryGet(placed.PrefabId, out PrefabSpec spec))
		{
			return;
		}

		var pose = new StructurePose(placed.Position, placed.YawRadians);
		foreach (StructureBox box in spec.Boxes)
		{
			into.Add(Place(pose, box));
		}

		if (spec.Kind != PrefabKind.Bridge || spec.ValueOf(placed, SupportsKey) == 0)
		{
			return;
		}

		AddPier(pose, spec, spec.ValueOf(placed, PierBackKey), -1, into);
		AddPier(pose, spec, spec.ValueOf(placed, PierFrontKey), 1, into);
	}

	/// <summary>An object's boxes in the world, as a new list.</summary>
	public static List<PlacedBox> BoxesOf(MapObject placed)
	{
		var boxes = new List<PlacedBox>(4);
		AddBoxes(placed, boxes);
		return boxes;
	}

	/// <summary>Whether two objects are in each other's way: a box of one overlaps a box of the other (LE-D11).</summary>
	public static bool InTheWay(List<PlacedBox> a, List<PlacedBox> b)
	{
		foreach (PlacedBox one in a)
		{
			foreach (PlacedBox other in b)
			{
				Vector3 apart = one.Plan.Center - other.Plan.Center;
				float reach = one.Reach + other.Reach;
				if ((apart.X * apart.X) + (apart.Z * apart.Z) < reach * reach && one.Overlaps(other))
				{
					return true;
				}
			}
		}

		return false;
	}

	/// <summary>
	/// The strategist's structure a structure prefab is (<see cref="StructureKinds"/>): what the
	/// round puts up for it through <c>ServerPlaceStructure</c> on its first tick and after every
	/// clear of the field (§6.5, LE5). False for any other prefab.
	/// </summary>
	public static bool TryGetStructureKind(ushort prefabId, out byte kind)
	{
		kind = prefabId switch
		{
			StructurePillbox => StructureKinds.Pillbox,
			StructureSandbagWall => StructureKinds.SandbagWall,
			StructureSniperTower => StructureKinds.SniperTower,
			_ => byte.MaxValue,
		};
		return kind != byte.MaxValue;
	}

	/// <summary>A barracks' door in the world, or false for anything else.</summary>
	public static bool TryGetDoor(MapObject placed, out Vector3 door)
	{
		door = default;
		if (!TryGet(placed.PrefabId, out PrefabSpec spec) || spec.Door is not Vector3 local)
		{
			return false;
		}

		door = new StructurePose(placed.Position, placed.YawRadians).ToWorld(local);
		return true;
	}

	/// <summary>A bridge section's connectors in the world: points, and headings out of the section.</summary>
	public static int Connectors(MapObject placed, Span<Vector3> points, Span<float> headings)
	{
		if (!TryGet(placed.PrefabId, out PrefabSpec spec))
		{
			return 0;
		}

		var pose = new StructurePose(placed.Position, placed.YawRadians);
		int count = Math.Min(spec.Connectors.Count, Math.Min(points.Length, headings.Length));
		for (int i = 0; i < count; i++)
		{
			points[i] = pose.ToWorld(spec.Connectors[i].Local);
			headings[i] = placed.YawRadians + spec.Connectors[i].Heading;
		}

		return count;
	}

	private static PlacedBox Place(in StructurePose pose, in StructureBox box)
	{
		Vector3 centre = pose.ToWorld(box.Center);
		return new PlacedBox(new Footprint(centre, pose.Yaw, box.HalfExtents.X, box.HalfExtents.Z),
			centre.Y - box.HalfExtents.Y, centre.Y + box.HalfExtents.Y);
	}

	/// <summary>A pier under one end (§6.7): its top at the deck's underside there, its length as stored.</summary>
	private static void AddPier(in StructurePose pose, PrefabSpec spec, int centimeters, int end, List<PlacedBox> into)
	{
		if (centimeters <= 0)
		{
			return;
		}

		float length = centimeters / 100f;
		float deckTop = spec.Id == BridgeRamp && end > 0 ? RampRiseMeters : 0f;
		float top = deckTop - DeckThicknessMeters;
		float along = end * ((DeckLengthMeters - PierSideMeters) * 0.5f);
		var box = new StructureBox(new Vector3(0f, top - (length * 0.5f), along),
			new Vector3(PierSideMeters * 0.5f, length * 0.5f, PierSideMeters * 0.5f));
		into.Add(Place(pose, box));
	}

	// ---- the table -------------------------------------------------------------------------

	private static PrefabSpec[] Build()
	{
		const float None = float.NaN;
		var nodeParameters = new[]
		{
			new PrefabParameter(NodePerkKey, "perk", (int)NodePerk.None, (int)NodePerk.Drone, (int)NodePerk.None),
			new PrefabParameter(NodeCaptureRadiusKey, "capture radius [cm]", 500, 1500, DefaultCaptureRadiusCentimeters),
		};
		var bridgeParameters = new[]
		{
			new PrefabParameter(SupportsKey, "supports", 0, 1, 0),
			new PrefabParameter(PierBackKey, "pier at -Z [cm]", 0, MaxPierCentimeters, 0),
			new PrefabParameter(PierFrontKey, "pier at +Z [cm]", 0, MaxPierCentimeters, 0),
		};

		return new[]
		{
			// Four player_spawn markers 4 m apart and the slots beside them: kept clear to a body's height.
			new PrefabSpec(SpawnLine, "spawn_line", PrefabKind.Round, PrefabGroup.SpawnLine, 20f, 6f, true, 10f,
				new[] { StructureBox.Standing(20f, 6f, 2f) }),
			new PrefabSpec(WeaponLocker, "weapon_locker", PrefabKind.Round, PrefabGroup.WeaponLocker, 3f, 3f, true, 10f,
				new[] { StructureBox.Standing(3f, 3f, 2.2f) }),

			// With Pad4x4 and PadHelicopter inside it: kept clear for what it hands out.
			new PrefabSpec(VehicleKiosk, "vehicle_kiosk", PrefabKind.Round, PrefabGroup.VehicleKiosk, 26f, 10f, true, 8f,
				new[] { StructureBox.Standing(26f, 10f, 3f) }),

			// Scenes/Environment/Barracks.tscn: an 8 × 8 × 6 m building, its guns, and the door's apron.
			new PrefabSpec(Barracks, "barracks", PrefabKind.Round, PrefabGroup.Barracks, 13f, 13f, true, 12f,
				new[] { StructureBox.Standing(13f, 13f, 6f) })
			{
				Door = new Vector3(0f, 0f, -6f),
			},

			// Its pole and ring are in the way; the capture disc it levels is ground to stand on.
			new PrefabSpec(ResourceNode, "resource_node", PrefabKind.Round, PrefabGroup.ResourceNode, 0f, 0f, true, 15f,
				new[] { StructureBox.Standing(1.2f, 1.2f, 3f) })
			{
				DiscFootprint = true,
				Parameters = nodeParameters,
			},
			new PrefabSpec(StrategistAnchor, "strategist_anchor", PrefabKind.Round, PrefabGroup.StrategistAnchor, 0f, 0f,
				false, None, Array.Empty<StructureBox>()),
			new PrefabSpec(HeavyGun, "heavy_gun", PrefabKind.Round, PrefabGroup.Emplacement, 1.5f, 1.5f, false, 20f,
				new[] { StructureBox.Standing(1.5f, 1.5f, 1.6f) }),
			new PrefabSpec(MortarLauncher, "mortar_launcher", PrefabKind.Round, PrefabGroup.Emplacement, 1.5f, 1.5f, false,
				20f, new[] { StructureBox.Standing(1.5f, 1.5f, 1.2f) }),
			new PrefabSpec(AmmoCan, "ammo_can", PrefabKind.Round, PrefabGroup.AmmoCan, 0.6f, 0.6f, false, 20f,
				new[] { StructureBox.Standing(0.6f, 0.6f, 0.5f) }),

			// 4 m long, 1.2 m high, 0.8 m thick; 3 m a side (COVER.md §2.3).
			new PrefabSpec(CoverLowWall, "cover_low_wall", PrefabKind.Cover, PrefabGroup.Cover, 4f, 0.8f, true, 25f,
				new[] { StructureBox.Standing(4f, 0.8f, 1.2f) }),
			new PrefabSpec(CoverBlock, "cover_block", PrefabKind.Cover, PrefabGroup.Cover, 3f, 3f, true, 25f,
				new[] { StructureBox.Standing(3f, 3f, 3f) }),

			// Scenes/Environment/Pillbox.tscn: 30 × 30 m, 7.12 m to its roof.
			new PrefabSpec(Bunker, "bunker", PrefabKind.Scenery, PrefabGroup.Scenery, 30f, 30f, true, 12f,
				new[] { StructureBox.Standing(30f, 30f, 7.2f) }),

			// Structures/*.tres: the body, and the tower's top on its 7 m shaft.
			new PrefabSpec(StructurePillbox, "structure_pillbox", PrefabKind.Structure, PrefabGroup.Structure, 3.6f, 3.6f,
				true, 8f, new[] { StructureBox.Standing(3.6f, 3.6f, 2.2f) }),
			new PrefabSpec(StructureSandbagWall, "structure_sandbag_wall", PrefabKind.Structure, PrefabGroup.Structure, 4f,
				0.8f, true, 8f, new[] { StructureBox.Standing(4f, 0.8f, 1.1f) }),
			new PrefabSpec(StructureSniperTower, "structure_sniper_tower", PrefabKind.Structure, PrefabGroup.Structure,
				3.2f, 3.2f, true, 8f,
				new[] { StructureBox.Standing(2f, 2f, 7f), StructureBox.Standing(3.2f, 3.2f, 0.9f, 7f) }),

			// A bridge section stands on its deck's top at its −Z end, with its connectors there and at +Z.
			new PrefabSpec(BridgeSpan, "bridge_span", PrefabKind.Bridge, PrefabGroup.Bridge, DeckWidthMeters,
				DeckLengthMeters, false, None, SectionBoxes(0f))
			{
				FreeHeight = true,
				Parameters = bridgeParameters,
				Connectors = new[]
				{
					new PrefabConnector(new Vector3(0f, 0f, -DeckLengthMeters * 0.5f), MathF.PI),
					new PrefabConnector(new Vector3(0f, 0f, DeckLengthMeters * 0.5f), 0f),
				},
			},
			new PrefabSpec(BridgeRamp, "bridge_ramp", PrefabKind.Bridge, PrefabGroup.Bridge, DeckWidthMeters,
				DeckLengthMeters, false, None, SectionBoxes(RampRiseMeters))
			{
				FreeHeight = true,
				Parameters = bridgeParameters,
				Connectors = new[]
				{
					new PrefabConnector(new Vector3(0f, 0f, -DeckLengthMeters * 0.5f), MathF.PI),
					new PrefabConnector(new Vector3(0f, RampRiseMeters, DeckLengthMeters * 0.5f), 0f),
				},
			},
		};
	}

	/// <summary>
	/// A section's deck and its two rails, as boxes. A ramp's deck is tilted in its
	/// prefab and a box is not, so a ramp is four steps of a quarter of it each, every
	/// one from under its low edge to over its high one: no box is bigger than the
	/// section by more than a quarter of the rise.
	/// </summary>
	private static StructureBox[] SectionBoxes(float rise)
	{
		int steps = rise > 0f ? 4 : 1;
		var boxes = new StructureBox[steps * 3];
		float length = DeckLengthMeters / steps;
		float railX = (DeckWidthMeters - RailThicknessMeters) * 0.5f;
		for (int i = 0; i < steps; i++)
		{
			float z = (-DeckLengthMeters * 0.5f) + ((i + 0.5f) * length);
			float low = rise * i / steps;
			float high = rise * (i + 1) / steps;
			float deckBottom = low - DeckThicknessMeters;
			boxes[i * 3] = new StructureBox(new Vector3(0f, (deckBottom + high) * 0.5f, z),
				new Vector3(DeckWidthMeters * 0.5f, (high - deckBottom) * 0.5f, length * 0.5f));
			float railTop = high + RailHeightMeters;
			for (int side = 0; side < 2; side++)
			{
				boxes[(i * 3) + 1 + side] = new StructureBox(
					new Vector3(side == 0 ? -railX : railX, (low + railTop) * 0.5f, z),
					new Vector3(RailThicknessMeters * 0.5f, (railTop - low) * 0.5f, length * 0.5f));
			}
		}

		return boxes;
	}
}
