using System;
using Godot;

namespace Gdpyr.Sim.Level;

/// <summary>
/// The editor's <i>buildable</i> overlay (docs/LEVEL_EDITOR.md §7.5, §8.3, LE5): where
/// LE-D21's rule takes a strategist's structure. A point is buildable when a pillbox — the
/// widest of the three — centred on it is taken square to the field and turned 45°: the
/// ground under its centre and corners no more than
/// <see cref="StructurePlacement.MaxGroundReliefMeters"/> apart either way. The ground is the
/// collider's (§6.1's <c>Sample</c>), which is what the round's rays meet. Where both turns
/// are taken, every turn between is near enough: the rule is asked of five points of a
/// 3.6 m square. Decided by LE5.
/// </summary>
public static class BuildableGround
{
	/// <summary>The turns a site is asked at [radians].</summary>
	public static readonly float[] Turns = { 0f, MathF.PI / 4f };

	/// <summary>Whether the rule takes a pillbox centred at (x, z), at every turn of <see cref="Turns"/>.</summary>
	public static bool At(HeightField field, float x, float z)
	{
		PrefabSpec pillbox = PrefabSpecs.Get(PrefabSpecs.StructurePillbox);
		Span<Vector3> points = stackalloc Vector3[StructurePlacement.GroundPointCount];
		Span<float> heights = stackalloc float[StructurePlacement.GroundPointCount];
		foreach (float turn in Turns)
		{
			var footprint = new Footprint(new Vector3(x, 0f, z), turn, pillbox.HalfWidth, pillbox.HalfDepth);
			StructurePlacement.GroundPoints(footprint, points);
			for (int i = 0; i < points.Length; i++)
			{
				heights[i] = field.Sample(points[i].X, points[i].Z);
			}

			if (StructurePlacement.CheckGround(heights, out _) != PlacementResult.Ok)
			{
				return false;
			}
		}

		return true;
	}
}
