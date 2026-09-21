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
}
