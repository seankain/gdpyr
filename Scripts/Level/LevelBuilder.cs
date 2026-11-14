using System;
using System.Collections.Generic;
using Gdpyr.Core;
using Gdpyr.Sim.Level;
using Godot;

namespace Gdpyr.Level;

/// <summary>
/// A data map's objects in the world (docs/LEVEL_EDITOR.md §7.1): each one as the node a
/// scene map would have — the scripts and groups of MAPS.md §3 — named by its order in the
/// file among its kind (<c>Barracks1</c>, <c>ResourceNode3</c>, <c>AmmoCan07</c>), so the
/// name-sorted indices on the wire are the same on every peer. Then the perimeter's walls.
///
/// What each prefab is, is <see cref="PrefabCatalog"/>'s (LE5): this counts, names by
/// order, and puts each where it goes. A strategist's structure is counted and left: the
/// round puts it up through <c>ServerPlaceStructure</c> on its first tick (LE5). Nothing
/// is derived (LE-D14).
/// </summary>
public static class LevelBuilder
{
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
			+ $" {StructuresLeft} structures for the round to put up"
			+ (Unknown > 0 ? $"; {Unknown} objects of no prefab this build has" : string.Empty);
	}

	/// <summary>
	/// Builds the map's objects and walls. <paramref name="byObject"/>, when given, is filled
	/// with the node built for each object in the file's order, null for one the round puts up
	/// itself or this build has not got: what the editor and its check find an object's node by.
	/// </summary>
	public static Summary Build(Node3D root, Node3D geometry, MapDocument document, TerrainNode terrain, bool draws,
		List<Node3D> byObject = null)
	{
		byObject?.Clear();
		var counts = new int[15];
		var cover = new Node3D { Name = "Cover" };
		var objects = new Node3D { Name = "Objects" };
		geometry.AddChild(cover);
		geometry.AddChild(objects);

		var look = new PrefabLook(draws);
		var ordinals = new Dictionary<ushort, int>();
		foreach (MapObject placed in document.Objects)
		{
			if (!PrefabSpecs.TryGet(placed.PrefabId, out PrefabSpec spec))
			{
				counts[14]++;
				byObject?.Add(null);
				continue;
			}

			ushort family = PrefabCatalog.NamingFamily(spec.Id);
			int ordinal = ordinals.TryGetValue(family, out int seen) ? seen + 1 : 1;
			ordinals[family] = ordinal;

			Node3D node = PrefabCatalog.Build(placed, spec, ordinal, look, out PrefabParent parent);
			byObject?.Add(node);
			if (node == null)
			{
				counts[spec.Kind == PrefabKind.Structure ? 13 : 14]++;
				continue;
			}

			(parent switch
			{
				PrefabParent.Cover => cover,
				PrefabParent.Objects => objects,
				_ => root,
			}).AddChild(node);
			Tally(counts, placed, spec);
		}

		BuildPerimeter(geometry, terrain, look);
		return new Summary(counts);
	}

	/// <summary>Where an object stands (§6.5): <see cref="PrefabCatalog.PoseOf"/>.</summary>
	public static Transform3D PoseOf(MapObject placed, PrefabSpec spec) => PrefabCatalog.PoseOf(placed, spec);

	/// <summary>The summary's counts, in <see cref="Summary"/>'s order.</summary>
	private static void Tally(int[] counts, MapObject placed, PrefabSpec spec)
	{
		switch (spec.Group)
		{
			case PrefabGroup.SpawnLine:
				counts[0]++;
				counts[1] += PrefabCatalog.SpawnMarkerOffsets.Length;
				break;
			case PrefabGroup.WeaponLocker:
				counts[2]++;
				break;
			case PrefabGroup.VehicleKiosk:
				counts[3]++;
				break;
			case PrefabGroup.Barracks:
				counts[4]++;
				break;
			case PrefabGroup.ResourceNode:
				counts[5]++;
				break;
			case PrefabGroup.StrategistAnchor:
				counts[6]++;
				break;
			case PrefabGroup.Emplacement:
				counts[7]++;
				break;
			case PrefabGroup.AmmoCan:
				counts[8]++;
				break;
			case PrefabGroup.Cover:
				counts[9]++;
				break;
			case PrefabGroup.Scenery:
				counts[10]++;
				break;
			case PrefabGroup.Bridge:
				counts[11]++;
				counts[12] += PrefabCatalog.PiersOf(placed, spec);
				break;
		}
	}

	/// <summary>
	/// Four walls as MapBase.tscn has them, centred on the field's edges, 1 m thick, each from
	/// 1 m under its edge's lowest ground to 4 m over its highest (§7.1).
	/// </summary>
	private static void BuildPerimeter(Node3D geometry, TerrainNode terrain, PrefabLook look)
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
			PrefabCatalog.AddBox(perimeter, new Transform3D(Basis.Identity, centre), size, look.Wall, names[edge]);
		}

		geometry.AddChild(perimeter);
	}

}
