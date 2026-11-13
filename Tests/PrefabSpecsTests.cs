using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Gdpyr.Sim;
using Gdpyr.Sim.Level;
using Godot;
using Xunit;
using RegexMatch = System.Text.RegularExpressions.Match;

namespace Gdpyr.Tests;

/// <summary>
/// The prefab catalog's engine-free half (docs/LEVEL_EDITOR.md §6.5): ids that do not
/// move, §6.5's table as values, the sizes that are known held to the files they come
/// from, and "in the way" (LE-D11) as boxes in plan and in height.
/// </summary>
public class PrefabSpecsTests
{
	[Fact]
	public void TheIds_AreAppendOnly_EachNamedAsSection6Point5Has()
	{
		// An id is two bytes in a file somebody else will open: this list only grows.
		string[] names =
		{
			"spawn_line", "weapon_locker", "vehicle_kiosk", "barracks", "resource_node", "strategist_anchor",
			"heavy_gun", "mortar_launcher", "ammo_can", "cover_low_wall", "cover_block", "bunker",
			"structure_pillbox", "structure_sandbag_wall", "structure_sniper_tower", "bridge_span", "bridge_ramp",
		};
		Assert.Equal(PrefabSpecs.Count, names.Length);
		Assert.Equal(names.Length, PrefabSpecs.All.Count);
		for (ushort id = 0; id < names.Length; id++)
		{
			Assert.Equal(id, PrefabSpecs.All[id].Id);
			Assert.Equal(names[id], PrefabSpecs.Get(id).Name);
		}

		Assert.False(PrefabSpecs.TryGet(PrefabSpecs.Count, out _));
		Assert.False(PrefabSpecs.TryGet(ushort.MaxValue, out _));
		Assert.Throws<ArgumentOutOfRangeException>(() => PrefabSpecs.Get(PrefabSpecs.Count));
	}

	[Theory]
	// id, kind, footprint across × along, pad, steepest (NaN: none), the group's most (0: no limit)
	[InlineData(0, PrefabKind.Round, 20, 6, true, 10, 0)]
	[InlineData(1, PrefabKind.Round, 3, 3, true, 10, 0)]
	[InlineData(2, PrefabKind.Round, 26, 10, true, 8, 1)]
	[InlineData(3, PrefabKind.Round, 13, 13, true, 12, 4)]
	[InlineData(4, PrefabKind.Round, 0, 0, true, 15, 8)]
	[InlineData(5, PrefabKind.Round, 0, 0, false, double.NaN, 1)]
	[InlineData(6, PrefabKind.Round, 1.5, 1.5, false, 20, 8)]
	[InlineData(7, PrefabKind.Round, 1.5, 1.5, false, 20, 8)]
	[InlineData(8, PrefabKind.Round, 0.6, 0.6, false, 20, 12)]
	[InlineData(9, PrefabKind.Cover, 4, 0.8, true, 25, 96)]
	[InlineData(10, PrefabKind.Cover, 3, 3, true, 25, 96)]
	[InlineData(11, PrefabKind.Scenery, 30, 30, true, 12, 0)]
	[InlineData(12, PrefabKind.Structure, 3.6, 3.6, true, 8, 16)]
	[InlineData(13, PrefabKind.Structure, 4, 0.8, true, 8, 16)]
	[InlineData(14, PrefabKind.Structure, 3.2, 3.2, true, 8, 16)]
	[InlineData(15, PrefabKind.Bridge, 6, 12, false, double.NaN, 0)]
	[InlineData(16, PrefabKind.Bridge, 6, 12, false, double.NaN, 0)]
	public void EachPrefab_IsSection6Point5sRow(int id, PrefabKind kind, double across, double along, bool pad,
		double steepest, int most)
	{
		PrefabSpec spec = PrefabSpecs.Get((ushort)id);
		Assert.Equal(kind, spec.Kind);
		Assert.Equal((float)across, spec.HalfWidth * 2f, 4);
		Assert.Equal((float)along, spec.HalfDepth * 2f, 4);
		Assert.Equal(pad, spec.MakesPad);
		if (double.IsNaN(steepest))
		{
			Assert.False(spec.HasGradeLimit);
		}
		else
		{
			Assert.Equal((float)steepest, spec.SteepestDegrees);
		}

		Assert.Equal(most == 0 ? int.MaxValue : most, PrefabSpecs.MaxOf(spec.Group));
		Assert.Equal(id == 4, spec.DiscFootprint);
		Assert.Equal(kind == PrefabKind.Bridge, spec.FreeHeight);
		Assert.Equal(id == 3, spec.Door.HasValue);

		// Every box of a prefab with a rectangle is inside it in plan.
		foreach (StructureBox box in spec.Boxes)
		{
			if (spec.HalfWidth > 0f)
			{
				Assert.True(MathF.Abs(box.Center.X) + box.HalfExtents.X <= spec.HalfWidth + 1e-4f);
				Assert.True(MathF.Abs(box.Center.Z) + box.HalfExtents.Z <= spec.HalfDepth + 1e-4f);
			}
		}
	}

	[Fact]
	public void TheGroupsCountedTogether_AndTheFewestAMapHolds_AreSection6Point9s()
	{
		Assert.Equal(PrefabSpecs.Get(PrefabSpecs.HeavyGun).Group, PrefabSpecs.Get(PrefabSpecs.MortarLauncher).Group);
		Assert.Equal(PrefabSpecs.Get(PrefabSpecs.CoverLowWall).Group, PrefabSpecs.Get(PrefabSpecs.CoverBlock).Group);
		Assert.Equal(PrefabSpecs.Get(PrefabSpecs.StructurePillbox).Group, PrefabSpecs.Get(PrefabSpecs.StructureSniperTower).Group);
		Assert.Equal(SimConfig.MaxStructures, 2 * PrefabSpecs.MaxOf(PrefabGroup.Structure));
		Assert.Equal(CoverBoard.LevelCapacity, PrefabSpecs.MaxOf(PrefabGroup.Cover));

		foreach (PrefabGroup group in Enum.GetValues<PrefabGroup>())
		{
			bool required = group is PrefabGroup.SpawnLine or PrefabGroup.WeaponLocker or PrefabGroup.Barracks
				or PrefabGroup.ResourceNode;
			Assert.Equal(required ? 1 : 0, PrefabSpecs.MinOf(group));
		}
	}

	[Fact]
	public void TheStructures_AreTheirTresFiles()
	{
		foreach ((ushort id, string file) in new[]
		{
			(PrefabSpecs.StructurePillbox, "pillbox"), (PrefabSpecs.StructureSandbagWall, "sandbag_wall"),
			(PrefabSpecs.StructureSniperTower, "sniper_tower"),
		})
		{
			string text = File.ReadAllText(Path.Combine(RepoRoot(), "Structures", file + ".tres"));
			Vector3 body = Vector(text, "BodySize");
			Vector3 top = Vector(text, "TopSize");
			float elevation = float.Parse(Regex.Match(text, @"TopElevationMeters = ([-\d.]+)").Groups[1].Value, CultureInfo.InvariantCulture);

			var expected = new List<StructureBox> { StructureBox.Standing(body.X, body.Z, body.Y) };
			if (top.X > 0f)
			{
				expected.Add(StructureBox.Standing(top.X, top.Z, top.Y, elevation));
			}

			PrefabSpec spec = PrefabSpecs.Get(id);
			Assert.Equal(expected.Count, spec.Boxes.Count);
			for (int i = 0; i < expected.Count; i++)
			{
				Assert.Equal(expected[i].Center, spec.Boxes[i].Center);
				Assert.Equal(expected[i].HalfExtents, spec.Boxes[i].HalfExtents);
			}

			var shape = new StructureShape(expected[0], expected.Count > 1 ? expected[1] : default);
			Assert.Equal(shape.HalfWidth, spec.HalfWidth, 5);
			Assert.Equal(shape.HalfDepth, spec.HalfDepth, 5);
		}
	}

	[Fact]
	public void TheBarracks_DoorAndRing_AreItsScenes()
	{
		string text = File.ReadAllText(Path.Combine(RepoRoot(), "Scenes", "Environment", "Barracks.tscn"));

		// The door is the SpawnPoint marker.
		RegexMatch spawn = Regex.Match(text, @"\[node name=""SpawnPoint""[^\n]*\ntransform = Transform3D\(([^)]*)\)");
		float[] t = spawn.Groups[1].Value.Split(',').Select(v => float.Parse(v, CultureInfo.InvariantCulture)).ToArray();
		Assert.Equal(new Vector3(t[9], t[10], t[11]), PrefabSpecs.Get(PrefabSpecs.Barracks).Door);

		// The ring: the furthest mount's flat offset and the 100 m every mount there reaches.
		float ring = 0f;
		foreach (RegexMatch mount in Regex.Matches(text, @"\[node name=""Defense\w+""[^\n]*\ntransform = Transform3D\(([^)]*)\)"))
		{
			float[] m = mount.Groups[1].Value.Split(',').Select(v => float.Parse(v, CultureInfo.InvariantCulture)).ToArray();
			ring = MathF.Max(ring, MathF.Sqrt((m[9] * m[9]) + (m[11] * m[11])) + 100f);
		}

		// No mount sets its reach: each has DefenseMount's default (MinRangeMeters is another export).
		Assert.DoesNotMatch(@"(?m)^RangeMeters\s*=", text);
		Assert.Equal(ring, PrefabSpecs.BarracksDefendedRadiusMeters, 4);
		Assert.Equal(107.1f, PrefabSpecs.BarracksDefendedRadiusMeters, 1);

		// The building is inside the box the barracks keeps clear.
		Assert.Contains("size = Vector3(8, 6, 8)", text);
		StructureBox box = PrefabSpecs.Get(PrefabSpecs.Barracks).Boxes.Single();
		Assert.True(box.HalfExtents.X >= 4f && box.HalfExtents.Z >= 4f && box.HalfExtents.Y * 2f >= 6f);
	}

	[Fact]
	public void TheFieldOfPlay_IsCargos()
	{
		Assert.Equal(Cargo.FieldHalfExtentMeters, MapLimits.PlayHalfMeters);
		Assert.Equal(StructurePlacement.OverlapAllowanceMeters, PrefabSpecs.OverlapAllowanceMeters);
	}

	[Fact]
	public void ABridgeSection_HasAConnectorAtEachEnd_AtTheDecksTop()
	{
		PrefabSpec span = PrefabSpecs.Get(PrefabSpecs.BridgeSpan);
		PrefabSpec ramp = PrefabSpecs.Get(PrefabSpecs.BridgeRamp);
		foreach (PrefabSpec section in new[] { span, ramp })
		{
			Assert.Equal(2, section.Connectors.Count);
			Vector3 back = section.Connectors[0].Local;
			Vector3 front = section.Connectors[1].Local;
			Assert.Equal(12f, front.Z - back.Z, 5);
			Assert.Equal(MathF.PI, MathF.Abs(section.Connectors[0].Heading - section.Connectors[1].Heading), 5);

			// The deck's top at each end is the connector's height.
			float topBack = section.Boxes.Where(b => b.HalfExtents.X == 3f).Min(b => b.Center.Y + b.HalfExtents.Y);
			Assert.Equal(back.Y + (section == ramp ? 0.375f : 0f), topBack, 4);
		}

		Assert.Equal(0f, span.Connectors[1].Local.Y);

		// 1 in 8: 7.1°, which a 4x4 climbs from rest (LE-D24).
		Assert.Equal(1.5f, ramp.Connectors[1].Local.Y);
		Assert.Equal(7.13f, HeightField.Degrees(ramp.Connectors[1].Local.Y / 12f), 2);
	}

	[Fact]
	public void TwoSpans_PutEndToEnd_ShareAPointWithOppositeHeadings_AndAreNotInEachOthersWay()
	{
		float yaw = 0.7f;
		var first = MapObject.At(PrefabSpecs.BridgeSpan, new Vector3(10f, 5f, 20f), yaw);
		Span<Vector3> ends = stackalloc Vector3[2];
		Span<float> headings = stackalloc float[2];
		PrefabSpecs.Connectors(first, ends, headings);

		// The second's back connector on the first's front one.
		Vector3 offset = new Vector3(0f, 0f, 6f).Rotated(Vector3.Up, first.YawRadians);
		var second = MapObject.At(PrefabSpecs.BridgeSpan, ends[1] + offset, first.YawRadians);
		Span<Vector3> secondEnds = stackalloc Vector3[2];
		Span<float> secondHeadings = stackalloc float[2];
		PrefabSpecs.Connectors(second, secondEnds, secondHeadings);

		Assert.True(secondEnds[0].DistanceTo(ends[1]) < 0.02f);
		Assert.Equal(MathF.PI, MathF.Abs(secondHeadings[0] - headings[1]), 4);
		Assert.False(PrefabSpecs.InTheWay(PrefabSpecs.BoxesOf(first), PrefabSpecs.BoxesOf(second)));
	}

	[Fact]
	public void InTheWay_IsOverlapInPlanBeyondTheAllowance_AndInHeight()
	{
		var block = MapObject.At(PrefabSpecs.CoverBlock, Vector3.Zero, 0f);
		foreach ((float gap, bool expected) in new[] { (0.1f, false), (0f, false), (-0.04f, false), (-0.06f, true), (-1f, true) })
		{
			var other = MapObject.At(PrefabSpecs.CoverBlock, new Vector3(3f + gap, 0f, 0f), 0f);
			Assert.Equal(expected, PrefabSpecs.InTheWay(PrefabSpecs.BoxesOf(block), PrefabSpecs.BoxesOf(other)));
		}

		// Turned 45°, a corner into a side.
		var turned = MapObject.At(PrefabSpecs.CoverBlock, new Vector3(3.5f, 0f, 0f), MathF.PI / 4f);
		Assert.True(PrefabSpecs.InTheWay(PrefabSpecs.BoxesOf(block), PrefabSpecs.BoxesOf(turned)));

		// A bridge may pass over a pillbox (LE-D11): the deck's underside over its roof is not in its way.
		var pillbox = MapObject.At(PrefabSpecs.StructurePillbox, Vector3.Zero, 0f);
		var high = MapObject.At(PrefabSpecs.BridgeSpan, new Vector3(0f, 2.9f, 0f), 0f);
		var low = MapObject.At(PrefabSpecs.BridgeSpan, new Vector3(0f, 2.7f, 0f), 0f);
		Assert.False(PrefabSpecs.InTheWay(PrefabSpecs.BoxesOf(pillbox), PrefabSpecs.BoxesOf(high)));
		Assert.True(PrefabSpecs.InTheWay(PrefabSpecs.BoxesOf(pillbox), PrefabSpecs.BoxesOf(low)));

		// A strategist anchor has no body.
		Assert.Empty(PrefabSpecs.BoxesOf(MapObject.At(PrefabSpecs.StrategistAnchor, Vector3.Zero, 0f)));
	}

	[Fact]
	public void APier_IsUnderItsEnd_FromTheDecksUnderside_ItsStoredLength_AndOnlyWithSupports()
	{
		MapParameter[] Piers(int supports, int back, int front) => new[]
		{
			new MapParameter(PrefabSpecs.SupportsKey, supports), new MapParameter(PrefabSpecs.PierBackKey, back),
			new MapParameter(PrefabSpecs.PierFrontKey, front),
		};

		var deck = new Vector3(0f, 6f, 0f);
		List<PlacedBox> none = PrefabSpecs.BoxesOf(MapObject.At(PrefabSpecs.BridgeSpan, deck, 0f, Piers(0, 500, 500)));
		List<PlacedBox> one = PrefabSpecs.BoxesOf(MapObject.At(PrefabSpecs.BridgeSpan, deck, 0f, Piers(1, 0, 650)));
		List<PlacedBox> both = PrefabSpecs.BoxesOf(MapObject.At(PrefabSpecs.BridgeRamp, deck, 0f, Piers(1, 500, 650)));
		Assert.Equal(3, none.Count);
		Assert.Equal(4, one.Count);
		Assert.Equal(14, both.Count);

		PlacedBox pier = one[^1];
		Assert.Equal(6f - 0.6f, pier.Top, 4);
		Assert.Equal(6f - 0.6f - 6.5f, pier.Bottom, 4);
		Assert.Equal(5.4f, pier.Plan.Center.Z, 4);
		Assert.Equal(0.6f, pier.Plan.HalfWidth, 4);

		// On a ramp the high end's pier hangs from the high end.
		Assert.Equal(6f + 1.5f - 0.6f, both[^1].Top, 4);
		Assert.Equal(6f - 0.6f, both[^2].Top, 4);

		// A cover block under the deck's end is in a pier's way, and not in the deck's.
		var block = MapObject.At(PrefabSpecs.CoverBlock, new Vector3(0f, 0f, 5.4f), 0f);
		Assert.True(PrefabSpecs.InTheWay(one, PrefabSpecs.BoxesOf(block)));
		Assert.False(PrefabSpecs.InTheWay(none, PrefabSpecs.BoxesOf(block)));
	}

	[Fact]
	public void APadArea_IsTheFootprint_OrANodesDisc_OfItsCaptureRadius()
	{
		var node = MapObject.At(PrefabSpecs.ResourceNode, new Vector3(10f, 0f, -5f), 1f,
			new[] { new MapParameter(PrefabSpecs.NodeCaptureRadiusKey, 1250) });
		Assert.True(PrefabSpecs.TryGetPadArea(node, out PadArea disc));
		Assert.True(disc.IsDisc);
		Assert.Equal(12.5f, disc.Radius);
		Assert.Equal(0f, disc.DistanceTo(22.4f, -5f));
		Assert.Equal(1f, disc.DistanceTo(10f, 8.5f), 4);

		var plain = MapObject.At(PrefabSpecs.ResourceNode, Vector3.Zero, 0f);
		Assert.True(PrefabSpecs.TryGetPadArea(plain, out disc));
		Assert.Equal(10f, disc.Radius);

		var kiosk = MapObject.At(PrefabSpecs.VehicleKiosk, Vector3.Zero, MathF.PI / 2f);
		Assert.True(PrefabSpecs.TryGetPadArea(kiosk, out PadArea rect));
		Assert.False(rect.IsDisc);

		// Turned a quarter, its 26 m runs along z.
		Assert.True(rect.Covers(0f, 12.9f));
		Assert.False(rect.Covers(12.9f, 0f));
		Assert.False(PrefabSpecs.TryGetPadArea(MapObject.At(PrefabSpecs.StrategistAnchor, Vector3.Zero, 0f), out _));
	}

	internal static string RepoRoot()
	{
		string dir = AppContext.BaseDirectory;
		while (dir != null && !File.Exists(Path.Combine(dir, "project.godot")))
		{
			dir = Path.GetDirectoryName(dir);
		}

		return dir ?? throw new InvalidOperationException("no project.godot above the test assembly");
	}

	private static Vector3 Vector(string text, string key)
	{
		RegexMatch match = Regex.Match(text, key + @" = Vector3\(([^)]*)\)");
		float[] v = match.Groups[1].Value.Split(',').Select(s => float.Parse(s, CultureInfo.InvariantCulture)).ToArray();
		return new Vector3(v[0], v[1], v[2]);
	}
}
