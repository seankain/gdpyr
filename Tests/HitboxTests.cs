using System;
using Gdpyr.Sim;
using Godot;
using Xunit;

namespace Gdpyr.Tests;

public class HitboxTests
{
	/// <summary>A standing player: 2 m tall, 0.5 m radius, standing at the origin.</summary>
	private static HitCapsule Player(Vector3 feet = default) => HitCapsule.FromFeet(feet, height: 2f, radius: 0.5f);

	[Fact]
	public void FromFeet_PlacesHemisphereCentresInsideTheCapsule()
	{
		HitCapsule c = Player();

		Assert.Equal(0.5f, c.Bottom.Y, 4);
		Assert.Equal(1.5f, c.Top.Y, 4);
		Assert.Equal(1.0f, c.Center.Y, 4);
	}

	[Fact]
	public void FromFeet_DegeneratesToASphereWhenTheCapsuleIsAllHemispheres()
	{
		HitCapsule c = HitCapsule.FromFeet(Vector3.Zero, height: 1f, radius: 0.5f);

		Assert.Equal(c.Bottom, c.Top);
		Assert.True(Hitbox.SegmentIntersects(c, new Vector3(-2f, 0.5f, 0f), new Vector3(2f, 0.5f, 0f), 0f, out _));
	}

	[Fact]
	public void SegmentThroughTheChest_Hits()
	{
		HitCapsule c = Player();

		Assert.True(Hitbox.SegmentIntersects(c, new Vector3(-5f, 1f, 0f), new Vector3(5f, 1f, 0f), 0f, out float t));

		// Entry, not closest approach: the near surface is 4.5 m along a 10 m segment.
		Assert.Equal(0.45f, t, 3);
	}

	[Fact]
	public void SegmentPastTheShoulder_Misses()
	{
		HitCapsule c = Player();

		Assert.False(Hitbox.SegmentIntersects(c, new Vector3(-5f, 1f, 0.51f), new Vector3(5f, 1f, 0.51f), 0f, out _));
	}

	[Fact]
	public void SegmentPastTheShoulder_HitsOnceSwept()
	{
		HitCapsule c = Player();

		// The same miss, with a grenade's body rather than a bullet's.
		Assert.True(Hitbox.SegmentIntersects(c, new Vector3(-5f, 1f, 0.51f), new Vector3(5f, 1f, 0.51f), 0.1f, out _));
	}

	[Fact]
	public void SegmentOverTheHead_Misses()
	{
		HitCapsule c = Player();

		Assert.False(Hitbox.SegmentIntersects(c, new Vector3(-5f, 2.6f, 0f), new Vector3(5f, 2.6f, 0f), 0f, out _));
	}

	[Fact]
	public void SegmentThroughTheTopOfTheHead_HitsTheCap()
	{
		HitCapsule c = Player();

		Assert.True(Hitbox.SegmentIntersects(c, new Vector3(0f, 5f, 0f), new Vector3(0f, 1.5f, 0f), 0f, out float t));

		// The crown is at y = 2.0; a 3.5 m segment from y = 5 reaches it at 6/7.
		Assert.Equal(3f / 3.5f, t, 3);
	}

	[Fact]
	public void SegmentParallelToTheAxisButOutside_Misses()
	{
		HitCapsule c = Player();

		Assert.False(Hitbox.SegmentIntersects(c, new Vector3(0.75f, 5f, 0f), new Vector3(0.75f, -5f, 0f), 0f, out _));
	}

	[Fact]
	public void SegmentEndingShortOfTheCapsule_Misses()
	{
		HitCapsule c = Player();

		// A bullet that runs out of tick before it gets there has not hit anything.
		Assert.False(Hitbox.SegmentIntersects(c, new Vector3(-5f, 1f, 0f), new Vector3(-1f, 1f, 0f), 0f, out _));
	}

	[Fact]
	public void SegmentStartingInside_HitsImmediately()
	{
		HitCapsule c = Player();

		Assert.True(Hitbox.SegmentIntersects(c, new Vector3(0f, 1f, 0f), new Vector3(5f, 1f, 0f), 0f, out float t));
		Assert.Equal(0f, t);
	}

	[Fact]
	public void ZeroLengthSegment_HitsOnlyWhenItIsInside()
	{
		HitCapsule c = Player();

		Assert.True(Hitbox.SegmentIntersects(c, new Vector3(0f, 1f, 0f), new Vector3(0f, 1f, 0f), 0f, out _));
		Assert.False(Hitbox.SegmentIntersects(c, new Vector3(9f, 1f, 0f), new Vector3(9f, 1f, 0f), 0f, out _));
	}

	[Fact]
	public void SegmentBehindTheShooter_Misses()
	{
		HitCapsule c = Player(new Vector3(0f, 0f, -10f));

		// Aimed the other way: the capsule is behind the segment's start.
		Assert.False(Hitbox.SegmentIntersects(c, Vector3.Zero, new Vector3(0f, 1f, 10f), 0f, out _));
	}

	[Fact]
	public void NearerTargetWins_WhenTwoAreInLine()
	{
		HitCapsule near = Player(new Vector3(0f, 0f, -20f));
		HitCapsule far = Player(new Vector3(0f, 0f, -60f));
		var from = new Vector3(0f, 1f, 0f);
		var to = new Vector3(0f, 1f, -100f);

		Assert.True(Hitbox.SegmentIntersects(near, from, to, 0f, out float nearT));
		Assert.True(Hitbox.SegmentIntersects(far, from, to, 0f, out float farT));
		Assert.True(nearT < farT);
	}

	[Fact]
	public void ATickOfFlightAtRealisticSpeed_StillFindsTheTarget()
	{
		// The reason the test is against a segment and not a point: at 940 m/s a
		// round covers 15.7 m per tick, so a player 8 m away is passed *through*
		// inside a single step (docs/NETCODE.md §4.2).
		HitCapsule c = Player(new Vector3(0f, 0f, -8f));
		var from = new Vector3(0f, 1f, 0f);
		var to = from + new Vector3(0f, 0f, -940f * SimConfig.TickDelta);

		Assert.True(Hitbox.SegmentIntersects(c, from, to, 0f, out _));
	}

	[Fact]
	public void DegenerateCapsule_NeverHits()
	{
		var c = new HitCapsule(Vector3.Zero, Vector3.Up, 0f);

		Assert.False(Hitbox.SegmentIntersects(c, new Vector3(-1f, 0.5f, 0f), new Vector3(1f, 0.5f, 0f), 0f, out _));
		Assert.False(c.IsValid);
	}

	// ---- hull boxes (docs/GROUND_VEHICLES.md §5.4) ---------------------------------------

	/// <summary>A 2 × 1 × 4 m box at (1, 2, 3), turned 30° about +Y: no face lies on a world axis.</summary>
	private static HitObb Turned()
	{
		Basis basis = new(Vector3.Up, Mathf.DegToRad(30f));
		return HitObb.Place(basis, new Vector3(1f, 2f, 3f), Vector3.Zero, new Vector3(1f, 0.5f, 2f));
	}

	public static TheoryData<int> Faces => new() { 0, 1, 2, 3, 4, 5 };

	[Theory]
	[MemberData(nameof(Faces))]
	public void ABox_IsHitThroughEachOfItsSixFaces_AtTheFace(int face)
	{
		HitObb box = Turned();
		Vector3 axis = (face / 2) switch { 0 => box.AxisX, 1 => box.AxisY, _ => box.AxisZ };
		float half = (face / 2) switch { 0 => box.HalfExtents.X, 1 => box.HalfExtents.Y, _ => box.HalfExtents.Z };
		Vector3 outward = face % 2 == 0 ? axis : -axis;

		Vector3 from = box.Center + (outward * 10f);
		Vector3 to = box.Center - (outward * 10f);

		Assert.True(Hitbox.SegmentIntersects(box, from, to, 0f, out float t));
		Assert.Equal((10f - half) / 20f, t, 4);
		Assert.Equal(box.Center + (outward * half), from + ((to - from) * t), new Vector3Comparer(1e-3f));
	}

	[Fact]
	public void ASegmentParallelToAFace_AndOutsideIt_Misses()
	{
		HitObb box = Turned();

		// Along the box's Z, 1 cm outside its +X face, from end to end and beyond.
		Vector3 offset = box.AxisX * (box.HalfExtents.X + 0.01f);
		Vector3 from = box.Center + offset - (box.AxisZ * 10f);
		Vector3 to = box.Center + offset + (box.AxisZ * 10f);

		Assert.False(Hitbox.SegmentIntersects(box, from, to, 0f, out _));

		// The same line 1 cm inside the face hits.
		Vector3 inside = box.AxisX * (box.HalfExtents.X - 0.01f);
		Assert.True(Hitbox.SegmentIntersects(box, box.Center + inside - (box.AxisZ * 10f),
			box.Center + inside + (box.AxisZ * 10f), 0f, out _));
	}

	[Fact]
	public void ASegmentThatStartsInside_HitsAtItsStart()
	{
		HitObb box = Turned();

		Assert.True(Hitbox.SegmentIntersects(box, box.Center, box.Center + new Vector3(30f, 0f, 0f), 0f, out float t));
		Assert.Equal(0f, t);

		// A zero-length one hits only inside.
		Assert.True(Hitbox.SegmentIntersects(box, box.Center, box.Center, 0f, out _));
		Vector3 outside = box.Center + (box.AxisY * 3f);
		Assert.False(Hitbox.SegmentIntersects(box, outside, outside, 0f, out _));
	}

	[Fact]
	public void ASegmentThatStopsShortOrPointsAway_Misses()
	{
		HitObb box = HitObb.Aligned(Vector3.Zero, Vector3.One);

		Assert.False(Hitbox.SegmentIntersects(box, new Vector3(5f, 0f, 0f), new Vector3(1.01f, 0f, 0f), 0f, out _));
		Assert.False(Hitbox.SegmentIntersects(box, new Vector3(5f, 0f, 0f), new Vector3(9f, 0f, 0f), 0f, out _));
	}

	[Fact]
	public void ASweepRadius_GrowsTheBox()
	{
		HitObb box = HitObb.Aligned(Vector3.Zero, Vector3.One);
		var from = new Vector3(-5f, 1.2f, 0f);
		var to = new Vector3(5f, 1.2f, 0f);

		Assert.False(Hitbox.SegmentIntersects(box, from, to, 0f, out _));
		Assert.True(Hitbox.SegmentIntersects(box, from, to, 0.25f, out float t));
		Assert.Equal((5f - 1.25f) / 10f, t, 4);
	}

	[Fact]
	public void DistanceToABox_IsZeroInside_AndToTheNearestFaceEdgeOrCornerOutside()
	{
		HitObb box = Turned();

		Assert.Equal(0f, Hitbox.DistanceTo(box, box.Center));
		Assert.Equal(0f, Hitbox.DistanceTo(box, box.Center + (box.AxisZ * 1.99f)));

		Assert.Equal(3f, Hitbox.DistanceTo(box, box.Center + (box.AxisX * 4f)), 4);
		Assert.Equal(5f, Hitbox.DistanceTo(box, box.Center + (box.AxisX * 4f) + (box.AxisY * 4.5f)), 4);

		Vector3 corner = box.Center + (box.AxisX * 2f) + (box.AxisY * 1.5f) + (box.AxisZ * 3f);
		Assert.Equal(MathF.Sqrt(3f), Hitbox.DistanceTo(box, corner), 4);
	}

	[Fact]
	public void AHullBoxPlacedByAPose_TurnsAndTiltsWithIt()
	{
		// Nose up 20°: a box authored ahead of the origin rises with the nose.
		var pose = new CrewPose(new Vector3(10f, 1f, 0f), 0f, Mathf.DegToRad(20f), 0f);
		HitObb box = pose.Place(new Vector3(0f, 0f, -2f), new Vector3(0.5f, 0.5f, 0.5f));

		Assert.Equal(1f + (2f * MathF.Sin(Mathf.DegToRad(20f))), box.Center.Y, 4);
		Assert.Equal(pose.Forward, -box.AxisZ, new Vector3Comparer(1e-5f));
	}

	[Theory]
	[InlineData(CrewVehicleFiles.FourByFourPath)]
	[InlineData(CrewVehicleFiles.HelicopterPath)]
	public void EveryEnclosedSeatsCapsule_LiesInsideAHullBox_ByFiveCentimetresOnEveryAxis(string path)
	{
		// GV-D11's shielding: a segment from outside a convex box enters the box before it
		// reaches anything inside it, so a driver or a pilot is never struck before the hull.
		CrewSeat[] seats = CrewVehicleFiles.Seats(path);
		HullBox[] hull = CrewVehicleFiles.Hull(path);
		int enclosed = 0;

		foreach (CrewSeat seat in seats)
		{
			if (!seat.Enclosed)
			{
				continue;
			}

			enclosed++;
			Assert.True(Array.Exists(hull, box => Margin(box, seat.Capsule) >= 0.05f - 1e-5f),
				$"{path}: the {seat.Role} seat's capsule is not 5 cm inside any hull box");
			Assert.True(Array.Exists(hull, box => Inside(box, seat.Eye)), $"{path}: the {seat.Role}'s eye is outside the hull");
		}

		Assert.Equal(1, enclosed);
	}

	[Theory]
	[InlineData(CrewVehicleFiles.FourByFourPath)]
	[InlineData(CrewVehicleFiles.HelicopterPath)]
	public void NoSegmentFromOutside_ReachesAnEnclosedSeat_BeforeTheHull(string path)
	{
		// The containment fact above, checked the way a round would find out: 20,000
		// segments from 30 m out, aimed through the seat, each hits the hull no later than
		// the capsule, whatever pose the vehicle is in.
		var random = new Random(4);
		CrewSeat seat = Array.Find(CrewVehicleFiles.Seats(path), s => s.Enclosed);
		HullBox[] hull = CrewVehicleFiles.Hull(path);

		for (int i = 0; i < 20_000; i++)
		{
			var pose = new CrewPose(new Vector3(Next(random, 50f), Next(random, 5f), Next(random, 50f)),
				Next(random, MathF.PI), Next(random, 0.6f), Next(random, 0.6f));
			HitCapsule capsule = pose.Place(seat.Capsule);
			Vector3 target = capsule.Bottom.Lerp(capsule.Top, (float)random.NextDouble())
				+ (RandomUnit(random) * (capsule.Radius * (float)random.NextDouble()));
			Vector3 from = target + (RandomUnit(random) * 30f);
			Vector3 to = target + ((target - from) * 0.5f);
			float sweep = i % 4 == 0 ? 0.15f : 0f;

			Assert.True(Hitbox.SegmentIntersects(capsule, from, to, sweep, out float tSeat));
			float tHull = float.PositiveInfinity;
			foreach (HullBox box in hull)
			{
				if (Hitbox.SegmentIntersects(box.Place(pose), from, to, sweep, out float t))
				{
					tHull = MathF.Min(tHull, t);
				}
			}

			Assert.True(tHull <= tSeat, $"segment {i} reached the {seat.Role} at {tSeat} before the hull at {tHull}");
		}
	}

	[Theory]
	[InlineData(CrewVehicleFiles.FourByFourPath)]
	[InlineData(CrewVehicleFiles.HelicopterPath)]
	public void EveryExposedSeatsCapsule_StandsAtLeast40CentimetresClearOfTheHull(string path)
	{
		// GV-D11: a gunner or a passenger is a player like any other above the bed or in
		// the door. The top 0.4 m of the capsule, swept by its radius, touches no hull box.
		CrewSeat[] seats = CrewVehicleFiles.Seats(path);
		HullBox[] hull = CrewVehicleFiles.Hull(path);
		int exposed = 0;

		foreach (CrewSeat seat in seats)
		{
			if (seat.Enclosed)
			{
				continue;
			}

			exposed++;
			Vector3 axis = (seat.Capsule.Top - seat.Capsule.Bottom).Normalized();
			Vector3 from = seat.Capsule.Top - (axis * 0.4f);
			foreach (HullBox box in hull)
			{
				Assert.False(Hitbox.SegmentIntersects(box.Local, from, seat.Capsule.Top, seat.Capsule.Radius, out _),
					$"{path}: the {seat.Role} seat's top 0.4 m touches the hull box at {box.Center}");
			}
		}

		Assert.True(exposed >= 1);
	}

	/// <summary>How far inside <paramref name="box"/> a capsule in the same frame is, on its tightest axis; negative when it pokes out.</summary>
	private static float Margin(in HullBox box, in HitCapsule capsule)
	{
		float margin = float.PositiveInfinity;
		for (int axis = 0; axis < 3; axis++)
		{
			float low = MathF.Min(capsule.Bottom[axis], capsule.Top[axis]) - capsule.Radius;
			float high = MathF.Max(capsule.Bottom[axis], capsule.Top[axis]) + capsule.Radius;
			float boxLow = box.Center[axis] - box.HalfExtents[axis];
			float boxHigh = box.Center[axis] + box.HalfExtents[axis];
			margin = MathF.Min(margin, MathF.Min(low - boxLow, boxHigh - high));
		}

		return margin;
	}

	private static bool Inside(in HullBox box, Vector3 point) => Hitbox.DistanceTo(box.Local, point) == 0f;

	private static float Next(Random random, float range) => ((float)random.NextDouble() * 2f - 1f) * range;

	private static Vector3 RandomUnit(Random random)
	{
		Vector3 v;
		do
		{
			v = new Vector3(Next(random, 1f), Next(random, 1f), Next(random, 1f));
		}
		while (v.LengthSquared() < 1e-3f || v.LengthSquared() > 1f);

		return v.Normalized();
	}
}

/// <summary>Vector equality within a tolerance on each axis, for <c>Assert.Equal</c>.</summary>
internal sealed class Vector3Comparer : System.Collections.Generic.IEqualityComparer<Vector3>
{
	private readonly float _tolerance;

	public Vector3Comparer(float tolerance) => _tolerance = tolerance;

	public bool Equals(Vector3 a, Vector3 b) =>
		MathF.Abs(a.X - b.X) <= _tolerance && MathF.Abs(a.Y - b.Y) <= _tolerance && MathF.Abs(a.Z - b.Z) <= _tolerance;

	public int GetHashCode(Vector3 v) => 0;
}
