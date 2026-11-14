using System;
using System.Collections.Generic;
using Gdpyr.Core;
using Gdpyr.Fps;
using Gdpyr.Rts;
using Gdpyr.Sim;
using Gdpyr.Sim.Level;
using Godot;

namespace Gdpyr.Level;

/// <summary>
/// A data map's objects in the world (docs/LEVEL_EDITOR.md §7.1): each one as the node a
/// scene map would have — the scripts and groups of MAPS.md §3 — named by its order in the
/// file among its kind (<c>Barracks1</c>, <c>ResourceNode3</c>, <c>AmmoCan07</c>), so the
/// name-sorted indices on the wire are the same on every peer. Then the perimeter's walls.
///
/// An object stands where the file says, upright, turned by its yaw. A padded one stands
/// on its pad's level, the step its stored height rounds to (§6.5), which is the ground
/// the file holds under it to the bit. Nothing is derived (LE-D14).
///
/// A strategist's structure that stands when a round starts is put up through
/// <c>ServerPlaceStructure</c> on the first tick, which is LE5's: here it is counted and
/// left. The catalog's engine half — an id to a scene or a builder — is this switch until
/// LE5 makes it <c>PrefabCatalog</c>.
/// </summary>
public static class LevelBuilder
{
	public const string BarracksScene = "res://Scenes/Environment/Barracks.tscn";
	public const string BunkerScene = "res://Scenes/Environment/Pillbox.tscn";

	/// <summary>How high over its pad a spawn marker is, as MapBase.tscn has them.</summary>
	public const float SpawnMarkerHeightMeters = 1f;

	/// <summary>A spawn line's four markers along its own X, 4 m apart (§6.5).</summary>
	public static readonly float[] SpawnMarkerOffsets = { -6f, -2f, 2f, 6f };

	/// <summary>The kiosk's pads in its own frame: inside its 26 × 10 m, either side of the cabinet at its centre.</summary>
	public static readonly Vector3 Pad4x4Offset = new(-7.5f, 0f, -1f);
	public static readonly Vector3 PadHelicopterOffset = new(7.5f, 0f, -1.5f);

	/// <summary>What was built, counted: the numbers LE3's check holds the world to.</summary>
	public readonly struct Summary
	{
		public readonly int SpawnLines;
		public readonly int SpawnMarkers;
		public readonly int Lockers;
		public readonly int Kiosks;
		public readonly int Barracks;
		public readonly int Nodes;
		public readonly int Anchors;
		public readonly int Emplacements;
		public readonly int AmmoCans;
		public readonly int Cover;
		public readonly int Scenery;
		public readonly int BridgeSections;
		public readonly int Piers;
		public readonly int StructuresLeft;
		public readonly int Unknown;

		public Summary(int[] counts)
		{
			SpawnLines = counts[0];
			SpawnMarkers = counts[1];
			Lockers = counts[2];
			Kiosks = counts[3];
			Barracks = counts[4];
			Nodes = counts[5];
			Anchors = counts[6];
			Emplacements = counts[7];
			AmmoCans = counts[8];
			Cover = counts[9];
			Scenery = counts[10];
			BridgeSections = counts[11];
			Piers = counts[12];
			StructuresLeft = counts[13];
			Unknown = counts[14];
		}

		public override string ToString() =>
			$"built {SpawnLines} spawn lines of {SpawnMarkers} markers, {Lockers} lockers, {Kiosks} kiosks,"
			+ $" {Barracks} barracks, {Nodes} nodes, {Anchors} anchors, {Emplacements} emplacements, {AmmoCans} ammo cans,"
			+ $" {Cover} cover boxes, {Scenery} scenery, {BridgeSections} bridge sections on {Piers} piers, the perimeter;"
			+ $" {StructuresLeft} structures left for the round's start (LE5)"
			+ (Unknown > 0 ? $"; {Unknown} objects of no prefab this build has" : string.Empty);
	}

	private enum Count
	{
		SpawnLines, SpawnMarkers, Lockers, Kiosks, Barracks, Nodes, Anchors, Emplacements, AmmoCans, Cover,
		Scenery, BridgeSections, Piers, StructuresLeft, Unknown, Max,
	}

	public static Summary Build(Node3D root, Node3D geometry, MapDocument document, TerrainNode terrain, bool draws)
	{
		var counts = new int[(int)Count.Max];
		var cover = new Node3D { Name = "Cover" };
		var objects = new Node3D { Name = "Objects" };
		geometry.AddChild(cover);
		geometry.AddChild(objects);

		var materials = new Materials(draws);
		int heavyGuns = 0;
		int launchers = 0;
		int lowWalls = 0;
		int blocks = 0;

		foreach (MapObject placed in document.Objects)
		{
			if (!PrefabSpecs.TryGet(placed.PrefabId, out PrefabSpec spec))
			{
				counts[(int)Count.Unknown]++;
				continue;
			}

			Transform3D at = PoseOf(placed, spec);
			switch (placed.PrefabId)
			{
				case PrefabSpecs.SpawnLine:
				{
					int line = ++counts[(int)Count.SpawnLines];
					var group = new Node3D { Name = $"SpawnLine{line}", Transform = at };
					for (int k = 0; k < SpawnMarkerOffsets.Length; k++)
					{
						var marker = new Marker3D
						{
							Name = $"Spawn{line:00}{k + 1}",
							Position = new Vector3(SpawnMarkerOffsets[k], SpawnMarkerHeightMeters, 0f),
						};
						marker.AddToGroup("player_spawn");
						group.AddChild(marker);
						counts[(int)Count.SpawnMarkers]++;
					}

					root.AddChild(group);
					break;
				}

				case PrefabSpecs.WeaponLocker:
				{
					var locker = new WeaponLocker { Name = $"WeaponLocker{++counts[(int)Count.Lockers]}", Transform = at };
					locker.AddToGroup(WeaponLocker.Group);
					root.AddChild(locker);
					break;
				}

				case PrefabSpecs.VehicleKiosk:
				{
					var kiosk = new VehicleKiosk { Name = $"VehicleKiosk{++counts[(int)Count.Kiosks]}", Transform = at };
					kiosk.AddToGroup(VehicleKiosk.Group);

					// Each vehicle faces its marker's −Z: turned to face the way the cabinet does.
					var turned = new Basis(Vector3.Up, Mathf.Pi);
					kiosk.AddChild(new Marker3D { Name = "Pad4x4", Transform = new Transform3D(turned, Pad4x4Offset) });
					kiosk.AddChild(new Marker3D
					{
						Name = "PadHelicopter",
						Transform = new Transform3D(turned, PadHelicopterOffset),
					});
					root.AddChild(kiosk);
					break;
				}

				case PrefabSpecs.Barracks:
				{
					var barracks = GD.Load<PackedScene>(BarracksScene).Instantiate<Node3D>();
					barracks.Name = $"Barracks{++counts[(int)Count.Barracks]}";
					barracks.Transform = at;
					root.AddChild(barracks);
					break;
				}

				case PrefabSpecs.ResourceNode:
				{
					var node = new ResourceNode
					{
						Name = $"ResourceNode{++counts[(int)Count.Nodes]}",
						Transform = at,
						CaptureRadiusMeters = spec.ValueOf(placed, PrefabSpecs.NodeCaptureRadiusKey) / 100f,
						Perk = (NodePerk)spec.ValueOf(placed, PrefabSpecs.NodePerkKey),
					};
					node.AddToGroup(ResourceNode.Group);
					root.AddChild(node);
					break;
				}

				case PrefabSpecs.StrategistAnchor:
				{
					// One a map (§6.9), named as MapBase.tscn names it.
					int anchors = ++counts[(int)Count.Anchors];
					var anchor = new Marker3D
					{
						Name = anchors == 1 ? "StrategistAnchor" : $"StrategistAnchor{anchors}",
						Transform = at,
					};
					anchor.AddToGroup("strategist_anchor");
					root.AddChild(anchor);
					break;
				}

				case PrefabSpecs.HeavyGun:
				case PrefabSpecs.MortarLauncher:
				{
					counts[(int)Count.Emplacements]++;
					Emplacement gun = placed.PrefabId == PrefabSpecs.MortarLauncher
						? new MortarLauncher { Name = $"MortarLauncher{++launchers}" }
						: new Emplacement { Name = $"Emplacement{++heavyGuns}" };
					gun.Transform = at;
					gun.AddToGroup(Emplacement.Group);
					root.AddChild(gun);
					break;
				}

				case PrefabSpecs.AmmoCan:
				{
					var can = new AmmoCan { Name = $"AmmoCan{++counts[(int)Count.AmmoCans]:00}", Transform = at };
					can.AddToGroup(AmmoCan.Group);
					root.AddChild(can);
					break;
				}

				case PrefabSpecs.CoverLowWall:
				case PrefabSpecs.CoverBlock:
				{
					counts[(int)Count.Cover]++;
					bool low = placed.PrefabId == PrefabSpecs.CoverLowWall;
					StructureBox box = spec.Boxes[0];
					var csg = new CsgBox3D
					{
						Name = low ? $"CoverLow{++lowWalls:00}" : $"CoverBlock{++blocks:00}",
						Transform = at.TranslatedLocal(box.Center),
						Size = box.HalfExtents * 2f,
						UseCollision = true,
						CollisionLayer = CollisionLayers.World,
						CollisionMask = 0,
						Material = low ? materials.CoverLow : materials.CoverBlock,
					};
					csg.AddToGroup("cover");
					cover.AddChild(csg);
					break;
				}

				case PrefabSpecs.Bunker:
				{
					var bunker = GD.Load<PackedScene>(BunkerScene).Instantiate<Node3D>();
					bunker.Name = $"Bunker{++counts[(int)Count.Scenery]}";
					bunker.Transform = at;
					objects.AddChild(bunker);
					break;
				}

				case PrefabSpecs.StructurePillbox:
				case PrefabSpecs.StructureSandbagWall:
				case PrefabSpecs.StructureSniperTower:
					counts[(int)Count.StructuresLeft]++;
					break;

				case PrefabSpecs.BridgeSpan:
				case PrefabSpecs.BridgeRamp:
					counts[(int)Count.Piers] += BuildBridge(objects, placed, spec, ++counts[(int)Count.BridgeSections],
						materials);
					break;

				default:
					counts[(int)Count.Unknown]++;
					break;
			}
		}

		BuildPerimeter(geometry, terrain, materials);
		return new Summary(counts);
	}

	/// <summary>
	/// Where an object stands: its point, turned by its yaw. A padded one at its pad's level
	/// (§6.5), which the ground under it is to the bit; anything else at its stored height.
	/// </summary>
	public static Transform3D PoseOf(MapObject placed, PrefabSpec spec)
	{
		Vector3 position = placed.Position;
		if (spec.MakesPad)
		{
			position.Y = HeightField.ToMeters(PrefabPlacement.PadLevelSample(placed.Y));
		}

		return new Transform3D(new Basis(Vector3.Up, placed.YawRadians), position);
	}

	/// <summary>
	/// A bridge section (§6.7): its deck and rails as static boxes on layer 1, a ramp's tilted
	/// as its prefab is; and a pier under each end the file gives one, of its stored length.
	/// Returns the piers built.
	/// </summary>
	private static int BuildBridge(Node3D objects, MapObject placed, PrefabSpec spec, int index, Materials materials)
	{
		var body = new StaticBody3D
		{
			Name = $"Bridge{index:00}",
			Transform = PoseOf(placed, spec),
			CollisionLayer = CollisionLayers.World,
			CollisionMask = 0,
		};

		float rise = placed.PrefabId == PrefabSpecs.BridgeRamp ? PrefabSpecs.RampRiseMeters : 0f;
		float length = PrefabSpecs.DeckLengthMeters;
		float tilt = MathF.Atan2(rise, length);
		float slope = MathF.Sqrt((length * length) + (rise * rise));

		// Rotating about X by −tilt takes the box's +Z up the deck.
		var basis = new Basis(Vector3.Right, -tilt);
		Vector3 normal = basis * Vector3.Up;
		var middle = new Vector3(0f, rise * 0.5f, 0f);
		float thickness = PrefabSpecs.DeckThicknessMeters;
		AddBox(body, new Transform3D(basis, middle - (normal * (thickness * 0.5f))),
			new Vector3(PrefabSpecs.DeckWidthMeters, thickness, slope), materials.Deck);

		float railX = (PrefabSpecs.DeckWidthMeters - PrefabSpecs.RailThicknessMeters) * 0.5f;
		float railHeight = PrefabSpecs.RailHeightMeters;
		foreach (float side in new[] { -railX, railX })
		{
			AddBox(body, new Transform3D(basis, middle + new Vector3(side, 0f, 0f) + (normal * (railHeight * 0.5f))),
				new Vector3(PrefabSpecs.RailThicknessMeters, railHeight, slope), materials.Deck);
		}

		// The piers are the spec's boxes past the section's own: PrefabSpecs.AddBoxes' order.
		int piers = 0;
		List<PlacedBox> boxes = PrefabSpecs.BoxesOf(placed);
		Transform3D inverse = body.Transform.AffineInverse();
		for (int i = spec.Boxes.Count; i < boxes.Count; i++)
		{
			PlacedBox pier = boxes[i];
			Vector3 centre = inverse * new Vector3(pier.Plan.Center.X, (pier.Bottom + pier.Top) * 0.5f, pier.Plan.Center.Z);
			AddBox(body, new Transform3D(Basis.Identity, centre),
				new Vector3(pier.Plan.HalfWidth * 2f, pier.Top - pier.Bottom, pier.Plan.HalfDepth * 2f), materials.Pier);
			piers++;
		}

		objects.AddChild(body);
		return piers;
	}

	/// <summary>
	/// Four walls as MapBase.tscn has them, centred on the field's edges, 1 m thick, each from
	/// 1 m under its edge's lowest ground to 4 m over its highest (§7.1).
	/// </summary>
	private static void BuildPerimeter(Node3D geometry, TerrainNode terrain, Materials materials)
	{
		var perimeter = new StaticBody3D
		{
			Name = "Perimeter",
			CollisionLayer = CollisionLayers.World,
			CollisionMask = 0,
		};

		float half = MapLimits.FieldHalfMeters;
		string[] names = { "North", "South", "West", "East" };
		for (int edge = 0; edge < 4; edge++)
		{
			terrain.EdgeRange(edge, out float lowest, out float highest);
			float bottom = lowest - 1f;
			float top = highest + 4f;
			bool alongX = edge < 2;
			var size = alongX ? new Vector3(2f * half, top - bottom, 1f) : new Vector3(1f, top - bottom, 2f * half);
			Vector3 centre = edge switch
			{
				0 => new Vector3(0f, (top + bottom) * 0.5f, -half),
				1 => new Vector3(0f, (top + bottom) * 0.5f, half),
				2 => new Vector3(-half, (top + bottom) * 0.5f, 0f),
				_ => new Vector3(half, (top + bottom) * 0.5f, 0f),
			};
			AddBox(perimeter, new Transform3D(Basis.Identity, centre), size, materials.Wall, names[edge]);
		}

		geometry.AddChild(perimeter);
	}

	private static void AddBox(StaticBody3D body, Transform3D at, Vector3 size, Material material, string name = null)
	{
		var shape = new CollisionShape3D { Transform = at, Shape = new BoxShape3D { Size = size } };
		if (name != null)
		{
			shape.Name = $"{name}Shape";
		}

		body.AddChild(shape);
		if (material != null)
		{
			var mesh = new MeshInstance3D { Transform = at, Mesh = new BoxMesh { Size = size }, MaterialOverride = material };
			if (name != null)
			{
				mesh.Name = $"{name}Mesh";
			}

			body.AddChild(mesh);
		}
	}

	/// <summary>What the built pieces are drawn with; nulls where nothing is drawn.</summary>
	private sealed class Materials
	{
		public readonly Material Wall;
		public readonly Material CoverLow;
		public readonly Material CoverBlock;
		public readonly Material Deck;
		public readonly Material Pier;

		public Materials(bool draws)
		{
			if (!draws)
			{
				return;
			}

			Wall = GD.Load<Material>("res://Levels/materials/PrototypeMat1.tres");
			CoverLow = Grid("res://ThirdParty/GreyboxTextures/greybox_light_grid.png");
			CoverBlock = Grid("res://ThirdParty/GreyboxTextures/greybox_dark_grid.png");
			Deck = Grid("res://ThirdParty/GreyboxTextures/greybox_purple_grid.png");
			Pier = Grid("res://ThirdParty/GreyboxTextures/greybox_grey_solid_2.png");
		}

		private static Material Grid(string texture) => new StandardMaterial3D
		{
			AlbedoTexture = GD.Load<Texture2D>(texture),
			Uv1Triplanar = true,
			Uv1Scale = new Vector3(0.5f, 0.5f, 0.5f),
		};
	}
}
