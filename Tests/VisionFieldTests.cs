using Gdpyr.Sim;
using Godot;
using Xunit;

namespace Gdpyr.Tests;

/// <summary>
/// The geometry half of the fog of war (docs/NETCODE.md §6.2). Whether a wall is
/// in the way is the engine's business; whether anything is close enough to care
/// is this, and it is the half worth answering from a test.
/// </summary>
public class VisionFieldTests
{
	private static VisionField Field(int capacity = 8) => new(capacity);

	[Fact]
	public void AnEmptyFieldSeesNothing()
	{
		// A strategist with no units on the field is blind, which is the whole reason
		// building one is a decision (docs/IMPLEMENTATION_PLAN.md §M4).
		Assert.False(Field().Sees(Vector3.Zero));
	}

	[Fact]
	public void APointInsideASensorIsSeen()
	{
		VisionField field = Field();
		field.Add(new Vector3(10f, 0f, 0f), 45f);

		Assert.True(field.Sees(new Vector3(30f, 0f, 0f)));
		Assert.False(field.Sees(new Vector3(70f, 0f, 0f)));
	}

	[Fact]
	public void TheEdgeOfTheRadiusCounts()
	{
		VisionField field = Field();
		field.Add(Vector3.Zero, 10f);

		Assert.True(field.Sees(new Vector3(0f, 0f, 10f)));
		Assert.False(field.Sees(new Vector3(0f, 0f, 10.01f)));
	}

	[Fact]
	public void RangeIsSpherical()
	{
		// A unit at the bottom of a shaft should not see the roof of a building at the
		// same map coordinates, so height is part of the distance.
		VisionField field = Field();
		field.Add(Vector3.Zero, 10f);

		Assert.False(field.Sees(new Vector3(8f, 8f, 0f)));
	}

	[Fact]
	public void ASensorWithNoRadiusIsNotASensor()
	{
		VisionField field = Field();

		Assert.False(field.Add(Vector3.Zero, 0f));
		Assert.False(field.Add(Vector3.Zero, -5f));
		Assert.Equal(0, field.Count);
	}

	[Fact]
	public void ClearingKeepsTheStorage()
	{
		VisionField field = Field();
		field.Add(Vector3.Zero, 10f);
		field.Clear();

		Assert.Equal(0, field.Count);
		Assert.False(field.Sees(Vector3.Zero));
		Assert.True(field.Add(Vector3.Zero, 10f));
	}

	[Fact]
	public void AFullFieldRefusesRatherThanGrows()
	{
		// Pre-sized and never grown, like every other per-tick structure here
		// (docs/IMPLEMENTATION_PLAN.md §3).
		VisionField field = Field(capacity: 2);

		Assert.True(field.Add(Vector3.Zero, 10f));
		Assert.True(field.Add(Vector3.One, 10f));
		Assert.False(field.Add(new Vector3(2f, 2f, 2f), 10f));
		Assert.Equal(2, field.Count);
		Assert.Equal(1, field.Overflows);
	}

	[Fact]
	public void GatherReturnsOnlyTheSensorsThatCover()
	{
		VisionField field = Field();
		field.Add(new Vector3(0f, 0f, 0f), 10f);      // covers
		field.Add(new Vector3(100f, 0f, 0f), 10f);    // far away
		field.Add(new Vector3(5f, 0f, 0f), 10f);      // covers

		var candidates = new int[3];
		int found = field.Gather(new Vector3(1f, 0f, 0f), candidates);

		Assert.Equal(2, found);
		Assert.DoesNotContain(1, candidates[..found]);
	}

	[Fact]
	public void GatherIsNearestFirst()
	{
		// The caller spends a line-of-sight ray per candidate, and the nearest sensor
		// is both the likeliest to have a clear line and the cheapest to ask.
		VisionField field = Field();
		field.Add(new Vector3(0f, 0f, 30f), 50f);
		field.Add(new Vector3(0f, 0f, 5f), 50f);
		field.Add(new Vector3(0f, 0f, 15f), 50f);

		var candidates = new int[3];
		int found = field.Gather(Vector3.Zero, candidates);

		Assert.Equal(3, found);
		Assert.Equal(new[] { 1, 2, 0 }, candidates);
	}

	[Fact]
	public void GatherKeepsTheNearestWhenMoreCoverThanItWillHold()
	{
		VisionField field = Field(capacity: 16);
		for (int i = 0; i < 10; i++)
		{
			// Sensor i sits i metres away, so the nearest two are 0 and 1.
			field.Add(new Vector3(0f, 0f, i), 50f);
		}

		var candidates = new int[2];
		int found = field.Gather(Vector3.Zero, candidates);

		Assert.Equal(2, found);
		Assert.Equal(new[] { 0, 1 }, candidates);
	}

	[Fact]
	public void GatherIntoNothingFindsNothing()
	{
		VisionField field = Field();
		field.Add(Vector3.Zero, 10f);

		Assert.Equal(0, field.Gather(Vector3.Zero, System.Span<int>.Empty));
	}

	[Fact]
	public void SensorsCanBeReadBackForTheRay()
	{
		VisionField field = Field();
		var origin = new Vector3(3f, 1.5f, -2f);
		field.Add(origin, 45f);

		Assert.Equal(origin, field.OriginAt(0));
		Assert.Equal(45f, field.RadiusAt(0));

		// Out of range reads are zero rather than an exception: this is walked from a
		// loop bound by Count, and a mistake there should not take the server down.
		Assert.Equal(Vector3.Zero, field.OriginAt(1));
		Assert.Equal(0f, field.RadiusAt(-1));
	}
}
