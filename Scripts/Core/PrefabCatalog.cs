using System;
using System.Collections.Generic;
using Gdpyr.Fps;
using Gdpyr.Rts;
using Gdpyr.Sim;
using Gdpyr.Sim.Level;
using Godot;

namespace Gdpyr.Core;

/// <summary>Where a prefab's node goes in a data map's world (docs/LEVEL_EDITOR.md §7.1).</summary>
public enum PrefabParent : byte
{
	/// <summary>Under the map's root, as a scene map has its round's pieces.</summary>
	Root,

	/// <summary>Under <c>Geometry/Cover</c>: a <c>CSGBox3D</c> in group <c>cover</c>.</summary>
	Cover,

	/// <summary>Under <c>Geometry/Objects</c>: scenery and bridge sections, world geometry.</summary>
	Objects,
}

/// <summary>
/// What a placed prefab is drawn with: the materials of what the catalog builds out of boxes.
/// All null where nothing is drawn — a dedicated server, a headless process.
/// </summary>
public sealed class PrefabLook
{
	public readonly Material Wall;
	public readonly Material CoverLow;
	public readonly Material CoverBlock;
	public readonly Material Deck;
	public readonly Material Pier;

	public PrefabLook(bool draws)
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

/// <summary>
/// The prefab catalog's engine half (docs/LEVEL_EDITOR.md §6.5, LE5): what each id is in the
/// world — a scene, or a builder — named by its order among its kind in the file, which is
/// MAPS.md §3's name and so every peer's index on the wire. <see cref="PrefabSpecs"/> is the
/// engine-free half, and what an id is to a placement and the validator.
///
/// A strategist's structure is not built here: the round puts it up itself, through
/// <c>ServerPlaceStructure</c>, on its first tick and after every clear of the field
/// (<see cref="PrefabSpecs.TryGetStructureKind"/>), so that it is as destructible as one a
/// builder made. Until LE5 this was <c>LevelBuilder</c>'s switch.
/// </summary>
public static class PrefabCatalog
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

	/// <summary>
	/// Which prefabs share a count for their names: a bridge's span and ramp are both
	/// <c>Bridge01</c>, <c>Bridge02</c>…; every other prefab counts its own.
	/// </summary>
	public static ushort NamingFamily(ushort prefabId) =>
		prefabId == PrefabSpecs.BridgeRamp ? PrefabSpecs.BridgeSpan : prefabId;

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
	/// The node a placed object is, the <paramref name="ordinal"/>-th of its naming family in
	/// the file (from 1), and where it goes; null for a structure, which the round puts up,
	/// and for an id this build has not got.
	/// </summary>
	public static Node3D Build(MapObject placed, PrefabSpec spec, int ordinal, PrefabLook look, out PrefabParent parent)
	{
		parent = PrefabParent.Root;
		Transform3D at = PoseOf(placed, spec);
		switch (spec.Id)
		{
			case PrefabSpecs.SpawnLine:
			{
				var line = new Node3D { Name = $"SpawnLine{ordinal}", Transform = at };
				for (int k = 0; k < SpawnMarkerOffsets.Length; k++)
				{
					var marker = new Marker3D
					{
						Name = $"Spawn{ordinal:00}{k + 1}",
						Position = new Vector3(SpawnMarkerOffsets[k], SpawnMarkerHeightMeters, 0f),
					};
					marker.AddToGroup("player_spawn");
					line.AddChild(marker);
				}

				return line;
			}

			case PrefabSpecs.WeaponLocker:
			{
				var locker = new WeaponLocker { Name = $"WeaponLocker{ordinal}", Transform = at };
				locker.AddToGroup(WeaponLocker.Group);
				return locker;
			}

			case PrefabSpecs.VehicleKiosk:
			{
				var kiosk = new VehicleKiosk { Name = $"VehicleKiosk{ordinal}", Transform = at };
				kiosk.AddToGroup(VehicleKiosk.Group);

				// Each vehicle faces its marker's −Z: turned to face the way the cabinet does.
				var turned = new Basis(Vector3.Up, Mathf.Pi);
				kiosk.AddChild(new Marker3D { Name = "Pad4x4", Transform = new Transform3D(turned, Pad4x4Offset) });
				kiosk.AddChild(new Marker3D { Name = "PadHelicopter", Transform = new Transform3D(turned, PadHelicopterOffset) });
				return kiosk;
			}

			case PrefabSpecs.Barracks:
			{
				var barracks = GD.Load<PackedScene>(BarracksScene).Instantiate<Node3D>();
				barracks.Name = $"Barracks{ordinal}";
				barracks.Transform = at;
				return barracks;
			}

			case PrefabSpecs.ResourceNode:
			{
				var node = new ResourceNode
				{
					Name = $"ResourceNode{ordinal}",
					Transform = at,
					CaptureRadiusMeters = spec.ValueOf(placed, PrefabSpecs.NodeCaptureRadiusKey) / 100f,
					Perk = (NodePerk)spec.ValueOf(placed, PrefabSpecs.NodePerkKey),
				};
				node.AddToGroup(ResourceNode.Group);
				return node;
			}

			case PrefabSpecs.StrategistAnchor:
			{
				// One a map (§6.9), named as MapBase.tscn names it.
				var anchor = new Marker3D
				{
					Name = ordinal == 1 ? "StrategistAnchor" : $"StrategistAnchor{ordinal}",
					Transform = at,
				};
				anchor.AddToGroup("strategist_anchor");
				return anchor;
			}

			case PrefabSpecs.HeavyGun:
			case PrefabSpecs.MortarLauncher:
			{
				Emplacement gun = spec.Id == PrefabSpecs.MortarLauncher
					? new MortarLauncher { Name = $"MortarLauncher{ordinal}" }
					: new Emplacement { Name = $"Emplacement{ordinal}" };
				gun.Transform = at;
				gun.AddToGroup(Emplacement.Group);
				return gun;
			}

			case PrefabSpecs.AmmoCan:
			{
				var can = new AmmoCan { Name = $"AmmoCan{ordinal:00}", Transform = at };
				can.AddToGroup(AmmoCan.Group);
				return can;
			}

			case PrefabSpecs.CoverLowWall:
			case PrefabSpecs.CoverBlock:
			{
				parent = PrefabParent.Cover;
				bool low = spec.Id == PrefabSpecs.CoverLowWall;
				StructureBox box = spec.Boxes[0];
				var csg = new CsgBox3D
				{
					Name = low ? $"CoverLow{ordinal:00}" : $"CoverBlock{ordinal:00}",
					Transform = at.TranslatedLocal(box.Center),
					Size = box.HalfExtents * 2f,
					UseCollision = true,
					CollisionLayer = CollisionLayers.World,
					CollisionMask = 0,
					Material = low ? look.CoverLow : look.CoverBlock,
				};
				csg.AddToGroup("cover");
				return csg;
			}

			case PrefabSpecs.Bunker:
			{
				parent = PrefabParent.Objects;
				var bunker = GD.Load<PackedScene>(BunkerScene).Instantiate<Node3D>();
				bunker.Name = $"Bunker{ordinal}";
				bunker.Transform = at;
				return bunker;
			}

			case PrefabSpecs.BridgeSpan:
			case PrefabSpecs.BridgeRamp:
				parent = PrefabParent.Objects;
				return BuildBridge(placed, spec, ordinal, look);

			default:
				// A structure, which the round puts up itself; or an id this build has not got.
				return null;
		}
	}

	/// <summary>
	/// A bridge section (§6.7): its deck and rails as static boxes on layer 1, a ramp's tilted
	/// as its prefab is; and a pier under each end the file gives one, of its stored length.
	/// </summary>
	private static StaticBody3D BuildBridge(MapObject placed, PrefabSpec spec, int ordinal, PrefabLook look)
	{
		var body = new StaticBody3D
		{
			Name = $"Bridge{ordinal:00}",
			Transform = PoseOf(placed, spec),
			CollisionLayer = CollisionLayers.World,
			CollisionMask = 0,
		};

		float rise = placed.PrefabId == PrefabSpecs.BridgeRamp ? PrefabSpecs.RampRiseMeters : 0f;
		float half = PrefabSpecs.DeckLengthMeters * 0.5f;
		float thickness = PrefabSpecs.DeckThicknessMeters;
		float railX = (PrefabSpecs.DeckWidthMeters - PrefabSpecs.RailThicknessMeters) * 0.5f;
		float railHeight = PrefabSpecs.RailHeightMeters;

		// The deck and its rails rise with the deck and end square at the section's ends: each a
		// sheared box, its sides vertical, so that it is inside the section's boxes and two sections
		// joined end to end meet at one plane. A tilted box overhung that plane by its thickness
		// times the tilt's sine — 7 cm under a ramp's deck, 12 cm at its rails' tops (LE5).
		AddPrism(body, -PrefabSpecs.DeckWidthMeters * 0.5f, PrefabSpecs.DeckWidthMeters * 0.5f, half, rise, -thickness, 0f, look.Deck);
		foreach (float side in new[] { -railX, railX })
		{
			AddPrism(body, side - (PrefabSpecs.RailThicknessMeters * 0.5f), side + (PrefabSpecs.RailThicknessMeters * 0.5f), half,
				rise, 0f, railHeight, look.Deck);
		}

		// The piers are the spec's boxes past the section's own: PrefabSpecs.AddBoxes' order.
		List<PlacedBox> boxes = PrefabSpecs.BoxesOf(placed);
		Transform3D inverse = body.Transform.AffineInverse();
		for (int i = spec.Boxes.Count; i < boxes.Count; i++)
		{
			PlacedBox pier = boxes[i];
			Vector3 centre = inverse * new Vector3(pier.Plan.Center.X, (pier.Bottom + pier.Top) * 0.5f, pier.Plan.Center.Z);
			AddBox(body, new Transform3D(Basis.Identity, centre),
				new Vector3(pier.Plan.HalfWidth * 2f, pier.Top - pier.Bottom, pier.Plan.HalfDepth * 2f), look.Pier);
		}

		return body;
	}

	/// <summary>
	/// A sheared box on a body: across [<paramref name="minX"/>, <paramref name="maxX"/>], along
	/// ±<paramref name="half"/>, and between <paramref name="below"/> and <paramref name="above"/>
	/// of a deck top that rises <paramref name="rise"/> from its −Z end to its +Z end. A box where
	/// the rise is nothing. Its collider is the eight corners' hull, and its mesh their six faces.
	/// </summary>
	private static void AddPrism(StaticBody3D body, float minX, float maxX, float half, float rise, float below, float above,
		Material material)
	{
		if (rise == 0f)
		{
			AddBox(body, new Transform3D(Basis.Identity, new Vector3((minX + maxX) * 0.5f, (below + above) * 0.5f, 0f)),
				new Vector3(maxX - minX, above - below, 2f * half), material);
			return;
		}

		var corners = new Vector3[8];
		for (int i = 0; i < 8; i++)
		{
			float z = (i & 2) == 0 ? -half : half;
			float top = rise * (z + half) / (2f * half);
			corners[i] = new Vector3((i & 1) == 0 ? minX : maxX, top + ((i & 4) == 0 ? below : above), z);
		}

		body.AddChild(new CollisionShape3D { Shape = new ConvexPolygonShape3D { Points = corners } });
		if (material == null)
		{
			return;
		}

		// Six faces, each wound so that it faces out of the prism (Godot's fronts are clockwise).
		int[][] faces =
		{
			new[] { 0, 1, 3, 2 }, new[] { 4, 6, 7, 5 }, new[] { 0, 4, 5, 1 }, new[] { 2, 3, 7, 6 }, new[] { 0, 2, 6, 4 },
			new[] { 1, 5, 7, 3 },
		};
		Vector3 middle = Vector3.Zero;
		foreach (Vector3 corner in corners)
		{
			middle += corner / 8f;
		}

		var tool = new SurfaceTool();
		tool.Begin(Mesh.PrimitiveType.Triangles);
		foreach (int[] face in faces)
		{
			foreach ((int a, int b, int c) in new[] { (face[0], face[1], face[2]), (face[0], face[2], face[3]) })
			{
				Vector3 pa = corners[a];
				Vector3 pb = corners[b];
				Vector3 pc = corners[c];
				bool outward = (pb - pa).Cross(pc - pa).Dot(((pa + pb + pc) / 3f) - middle) < 0f;
				tool.AddVertex(pa);
				tool.AddVertex(outward ? pb : pc);
				tool.AddVertex(outward ? pc : pb);
			}
		}

		tool.GenerateNormals();
		body.AddChild(new MeshInstance3D { Mesh = tool.Commit(), MaterialOverride = material });
	}

	/// <summary>The piers a placed bridge section stands on: its boxes past its own.</summary>
	public static int PiersOf(MapObject placed, PrefabSpec spec) =>
		spec.Kind == PrefabKind.Bridge ? PrefabSpecs.BoxesOf(placed).Count - spec.Boxes.Count : 0;

	/// <summary>A static box on a body, and its mesh where it is drawn.</summary>
	public static void AddBox(StaticBody3D body, Transform3D at, Vector3 size, Material material, string name = null)
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
}
