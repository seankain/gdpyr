using System;
using Gdpyr.Sim;
using Godot;
using Xunit;

namespace Gdpyr.Tests;

/// <summary>
/// LE-D21 (docs/LEVEL_EDITOR.md §4, §7.5): a structure built during a round needs ground
/// under its footprint that varies by at most 0.5 m, sampled at its centre and corners;
/// it stands on the highest, on a plinth down past the lowest.
/// </summary>
public class StructureGroundTests
{
	// Structures/*.tres: the pillbox's body is 3.6 m a side, the sandbag wall 4 × 0.8 m,
	// the sniper tower's top 3.2 m a side (PrefabSpecsTests reads each from its file).
	private const float PillboxHalf = 1.8f;
	private const float WallHalfLength = 2f;
	private const float WallHalfThickness = 0.4f;

	/// <summary>The ground's heights at LE-D21's points, on a plane of <paramref name="degrees"/> rising along <paramref name="uphill"/>.</summary>
	private static float[] OnPlane(in Footprint footprint, float degrees, float uphillYaw, float lift = 3.7f)
	{
		Span<Vector3> points = stackalloc Vector3[StructurePlacement.GroundPointCount];
		StructurePlacement.GroundPoints(footprint, points);
		var uphill = new Vector3(MathF.Sin(uphillYaw), 0f, MathF.Cos(uphillYaw));
		float grade = MathF.Tan(degrees * MathF.PI / 180f);
		var heights = new float[points.Length];
		for (int i = 0; i < points.Length; i++)
		{
			heights[i] = lift + (grade * points[i].Dot(uphill));
		}

		return heights;
	}

	private static PlacementResult On(in Footprint footprint, float degrees, float uphillYaw) =>
		StructurePlacement.CheckGround(OnPlane(footprint, degrees, uphillYaw), out _);

	/// <summary>The steepest grade a footprint is taken on, to 0.01°, by bisection on the rule itself.</summary>
	private static float Limit(in Footprint footprint, float uphillYaw)
	{
		float low = 0f;
		float high = 45f;
		while (high - low > 0.001f)
		{
			float mid = (low + high) * 0.5f;
			if (On(footprint, mid, uphillYaw) == PlacementResult.Ok)
			{
				low = mid;
			}
			else
			{
				high = mid;
			}
		}

		return low;
	}

	[Fact]
	public void TheNumbersAreThePlans()
	{
		Assert.Equal(0.5f, StructurePlacement.MaxGroundReliefMeters);
		Assert.Equal(5, StructurePlacement.GroundPointCount);
		Assert.Equal(11, (int)PlacementResult.TooSteep);
		Assert.Equal("the ground is too steep", StructurePlacement.Describe(PlacementResult.TooSteep));
		Assert.True(StructurePlacement.GroundWindowMeters > StructurePlacement.MaxGroundReliefMeters);
	}

	[Theory]
	[InlineData(0f)]
	[InlineData(0.6f)]
	[InlineData(2.3f)]
	public void ThePointsAreTheCentreAndTheCorners(float yaw)
	{
		var footprint = new Footprint(new Vector3(12f, 4f, -7f), yaw, 2f, 0.4f);
		Span<Vector3> points = stackalloc Vector3[StructurePlacement.GroundPointCount];
		StructurePlacement.GroundPoints(footprint, points);

		Assert.Equal(footprint.Center, points[0]);
		for (int i = 1; i < points.Length; i++)
		{
			// On the rectangle, and at its reach from the centre: a corner.
			Assert.True(footprint.DistanceTo(points[i]) < 1e-4f);
			Assert.Equal(MathF.Sqrt((2f * 2f) + (0.4f * 0.4f)), (points[i] - footprint.Center).Length(), 4);
			for (int j = 1; j < i; j++)
			{
				Assert.True((points[i] - points[j]).Length() > 0.7f);
			}
		}
	}

	[Fact]
	public void OnLevelGroundNothingChanges()
	{
		var footprint = new Footprint(new Vector3(-30f, 0f, 40f), 0.4f, PillboxHalf, PillboxHalf);
		float[] heights = OnPlane(footprint, 0f, 0f, lift: 0.5f);

		Assert.Equal(PlacementResult.Ok, StructurePlacement.CheckGround(heights, out float baseHeight));
		Assert.Equal(0.5f, baseHeight);
	}

	[Theory]
	[InlineData(2f)]
	[InlineData(5f)]
	[InlineData(7.5f)]
	public void ItStandsOnTheHighest(float degrees)
	{
		var footprint = new Footprint(Vector3.Zero, 0f, PillboxHalf, PillboxHalf);
		float[] heights = OnPlane(footprint, degrees, 0f);

		Assert.Equal(PlacementResult.Ok, StructurePlacement.CheckGround(heights, out float baseHeight));
		float highest = float.NegativeInfinity;
		float lowest = float.PositiveInfinity;
		foreach (float h in heights)
		{
			highest = MathF.Max(highest, h);
			lowest = MathF.Min(lowest, h);
		}

		Assert.Equal(highest, baseHeight);

		// And the plinth reaches the lowest ground under it.
		StructureBox plinth = StructurePlacement.Plinth(StructureBox.Standing(3.6f, 3.6f, 2.2f));
		Assert.True(baseHeight + plinth.Center.Y - plinth.HalfExtents.Y <= lowest);
	}

	[Fact]
	public void APillboxIsTakenTo7_9DegreesAcrossASideAnd5_6AcrossItsDiagonal()
	{
		// §7.5: 0.5 m over 3.6 m is 7.9°; over 3.6 · √2 m, 5.6°.
		var footprint = new Footprint(new Vector3(20f, 0f, 20f), 0f, PillboxHalf, PillboxHalf);

		Assert.InRange(Limit(footprint, 0f), 7.90f, 7.92f);
		Assert.InRange(Limit(footprint, MathF.PI * 0.5f), 7.90f, 7.92f);
		Assert.InRange(Limit(footprint, MathF.PI * 0.25f), 5.60f, 5.62f);
		Assert.InRange(Limit(footprint, MathF.PI * 1.25f), 5.60f, 5.62f);

		Assert.Equal(PlacementResult.Ok, On(footprint, 7.8f, 0f));
		Assert.Equal(PlacementResult.TooSteep, On(footprint, 8.0f, 0f));
		Assert.Equal(PlacementResult.Ok, On(footprint, 5.5f, MathF.PI * 0.25f));
		Assert.Equal(PlacementResult.TooSteep, On(footprint, 5.7f, MathF.PI * 0.25f));
	}

	[Fact]
	public void ASandbagWallIsTakenTo7_1DegreesAlongItsLength()
	{
		// Its length is its own X: 0.5 m over 4 m is 7.1°. Across its 0.8 m it takes far more.
		var wall = new Footprint(Vector3.Zero, 0f, WallHalfLength, WallHalfThickness);

		Assert.InRange(Limit(wall, MathF.PI * 0.5f), 7.12f, 7.14f);
		Assert.InRange(Limit(wall, 0f), 31.9f, 32.1f);
	}

	[Fact]
	public void TheLimitTurnsWithTheFootprint()
	{
		// The same slope and the same wall, turned a quarter: what was along it is across it.
		var alongX = new Footprint(Vector3.Zero, 0f, WallHalfLength, WallHalfThickness);
		var alongZ = new Footprint(Vector3.Zero, MathF.PI * 0.5f, WallHalfLength, WallHalfThickness);

		Assert.Equal(PlacementResult.TooSteep, On(alongX, 10f, MathF.PI * 0.5f));
		Assert.Equal(PlacementResult.Ok, On(alongZ, 10f, MathF.PI * 0.5f));
	}

	[Fact]
	public void ACornerWhoseGroundWasNotFoundIsTooSteep()
	{
		float[] heights = { 1f, 1.1f, float.NaN, 1f, 0.9f };
		Assert.Equal(PlacementResult.TooSteep, StructurePlacement.CheckGround(heights, out float baseHeight));
		Assert.True(float.IsNaN(baseHeight));

		Assert.Equal(PlacementResult.TooSteep, StructurePlacement.CheckGround(ReadOnlySpan<float>.Empty, out _));
		Assert.Equal(PlacementResult.TooSteep,
			StructurePlacement.CheckGround(new[] { 0f, float.PositiveInfinity, 0f, 0f, 0f }, out _));
	}

	[Fact]
	public void HalfAMetreIsTakenAndJustOverIsNot()
	{
		Assert.Equal(PlacementResult.Ok, StructurePlacement.CheckGround(new[] { 2f, 2.5f, 2.2f, 2f, 2.1f }, out float at));
		Assert.Equal(2.5f, at);
		Assert.Equal(PlacementResult.TooSteep, StructurePlacement.CheckGround(new[] { 2f, 2.51f, 2.2f, 2f, 2.1f }, out _));

		// The centre counts as a corner does: a crest under the middle is relief too.
		Assert.Equal(PlacementResult.TooSteep, StructurePlacement.CheckGround(new[] { 2.6f, 2f, 2f, 2f, 2f }, out _));
	}

	[Fact]
	public void ThePlinthIsTheBodysPlanFromHalfAMetreDownToTheBase()
	{
		StructureBox body = StructureBox.Standing(4f, 0.8f, 1.1f);
		StructureBox plinth = StructurePlacement.Plinth(body);

		Assert.Equal(body.HalfExtents.X, plinth.HalfExtents.X);
		Assert.Equal(body.HalfExtents.Z, plinth.HalfExtents.Z);
		Assert.Equal(body.Center.X, plinth.Center.X);
		Assert.Equal(body.Center.Z, plinth.Center.Z);
		Assert.Equal(0f, plinth.Center.Y + plinth.HalfExtents.Y, 6);
		Assert.Equal(-StructurePlacement.MaxGroundReliefMeters, plinth.Center.Y - plinth.HalfExtents.Y, 6);

		Assert.True(StructurePlacement.Plinth(default).IsEmpty);
	}
}
