using System;
using Gdpyr.Sim;
using Godot;
using Xunit;

namespace Gdpyr.Tests;

public class SpreadTests
{
	private static readonly Vector3 Forward = new(0f, 0f, -1f);

	private static float AngleBetween(Vector3 a, Vector3 b) =>
		MathF.Acos(Math.Clamp(a.Normalized().Dot(b.Normalized()), -1f, 1f));

	[Fact]
	public void AConeOfZeroChangesNothing()
	{
		// M2's four weapons all author 0 degrees, so this is the path every existing
		// shot takes (docs/IMPLEMENTATION_PLAN.md §7).
		Assert.Equal(Forward, Spread.Apply(Forward, 0f, seed: 12345u));
	}

	[Fact]
	public void TheResultIsAUnitVectorInsideTheCone()
	{
		float cone = Spread.ConeFromDegrees(3f);

		for (uint shot = 0; shot < 2000; shot++)
		{
			Vector3 result = Spread.Apply(Forward, cone, Spread.Seed(7, 3, shot));

			Assert.Equal(1f, result.Length(), 3);
			Assert.True(AngleBetween(Forward, result) <= cone + 1e-4f,
				$"shot {shot} left the cone");
		}
	}

	[Fact]
	public void TheSameShotAlwaysGoesTheSameWay()
	{
		// The whole point: the server, the shooter's client and every other client
		// derive this independently and must agree (docs/NETCODE.md §4.3).
		uint seed = Spread.Seed(ownerId: 4, definitionId: 2, shotIndex: 91);
		Vector3 first = Spread.Apply(Forward, Spread.ConeFromDegrees(5f), seed);

		for (int i = 0; i < 1000; i++)
		{
			Assert.Equal(first, Spread.Apply(Forward, Spread.ConeFromDegrees(5f), seed));
		}
	}

	[Fact]
	public void ConsecutiveShotsGoDifferentWays()
	{
		float cone = Spread.ConeFromDegrees(4f);
		Vector3 a = Spread.Apply(Forward, cone, Spread.Seed(1, 3, 10));
		Vector3 b = Spread.Apply(Forward, cone, Spread.Seed(1, 3, 11));

		Assert.NotEqual(a, b);
	}

	[Fact]
	public void DifferentShootersWithTheSameShotIndexDiffer()
	{
		// Otherwise a firing line of twenty units would put every round of a volley
		// down the same line.
		float cone = Spread.ConeFromDegrees(4f);
		Vector3 a = Spread.Apply(Forward, cone, Spread.Seed(-1, 3, 5));
		Vector3 b = Spread.Apply(Forward, cone, Spread.Seed(-2, 3, 5));

		Assert.NotEqual(a, b);
	}

	[Fact]
	public void ShotsFillTheConeRatherThanPilingIntoTheMiddle()
	{
		// Uniform over the spherical cap means E[1 - cos θ] is half of (1 - cos cone).
		// Sampling the angle uniformly instead would make a wide cone behave like a
		// narrow one, which is the bug this test exists to catch.
		float cone = Spread.ConeFromDegrees(10f);
		float expected = (1f - MathF.Cos(cone)) * 0.5f;

		double total = 0;
		const int Samples = 20000;
		for (uint shot = 0; shot < Samples; shot++)
		{
			Vector3 result = Spread.Apply(Forward, cone, Spread.Seed(3, 1, shot));
			total += 1f - Forward.Dot(result);
		}

		double mean = total / Samples;
		Assert.InRange(mean, expected * 0.9, expected * 1.1);
	}

	[Fact]
	public void AVerticalShotDoesNotDegenerate()
	{
		// The cone's frame is built from a reference vector, and Up is useless as one
		// when the shot is already up.
		float cone = Spread.ConeFromDegrees(6f);

		foreach (Vector3 direction in new[] { Vector3.Up, Vector3.Down })
		{
			Vector3 result = Spread.Apply(direction, cone, Spread.Seed(9, 0, 3));

			Assert.Equal(1f, result.Length(), 3);
			Assert.True(AngleBetween(direction, result) <= cone + 1e-4f);
		}
	}

	[Fact]
	public void AZeroDirectionStaysZero()
	{
		Assert.Equal(Vector3.Zero, Spread.Apply(Vector3.Zero, Spread.ConeFromDegrees(5f), 1u));
	}

	[Fact]
	public void DegreesConvertToRadiansAndClamp()
	{
		Assert.Equal(0f, Spread.ConeFromDegrees(0f));
		Assert.Equal(0f, Spread.ConeFromDegrees(-3f));
		Assert.Equal(MathF.PI / 180f, Spread.ConeFromDegrees(1f), 6);
		Assert.Equal(MathF.PI / 2f, Spread.ConeFromDegrees(400f), 6);
	}
}
