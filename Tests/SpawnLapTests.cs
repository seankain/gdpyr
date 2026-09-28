using System.Collections.Generic;
using Gdpyr.Sim;
using Godot;
using Xunit;

namespace Gdpyr.Tests;

/// <summary>
/// Where a player stands when there are more players than spawn points
/// (<see cref="SpawnLap"/>): found in H2 as bodies pushed out of the world at a
/// round reset, fixed in H6 (docs/HTN_BOTS.md §6).
/// </summary>
public class SpawnLapTests
{
	// The test map's four points, 4 m apart in a row, as Scenes/Test.tscn has them.
	private static readonly Vector3[] Points =
	{
		new(-2.41335f, 4.067f, 7.49199f), new(1.58665f, 4.067f, 7.49199f),
		new(5.58665f, 4.067f, 7.49199f), new(9.58665f, 4.067f, 7.49199f),
	};

	private static Vector3 Where(int index) => Points[index % Points.Length] + SpawnLap.Offset(index, Points.Length);

	[Fact]
	public void TheFirstLapStandsOnThePoints()
	{
		for (int i = 0; i < Points.Length; i++)
		{
			Assert.Equal(Vector3.Zero, SpawnLap.Offset(i, Points.Length));
			Assert.Equal(Points[i], Where(i));
		}
	}

	[Fact]
	public void EveryPlayerTheSnapshotCanCarryHasAPlaceOfItsOwn()
	{
		// Sixteen players on four points: no two within a capsule's width of each other.
		var placed = new List<Vector3>();
		for (int i = 0; i < SnapshotCodec.MaxPlayers; i++)
		{
			Vector3 at = Where(i);
			foreach (Vector3 other in placed)
			{
				Assert.True(at.DistanceTo(other) >= 1.9f, $"index {i} at {at} is {at.DistanceTo(other)} m from {other}");
			}

			placed.Add(at);
		}
	}

	[Fact]
	public void SixGroundPlayersOnFourPointsDoNotShareOne()
	{
		// The case that pushed two pairs of bots out of the world at every reset.
		Assert.NotEqual(Where(0), Where(4));
		Assert.NotEqual(Where(1), Where(5));
		Assert.Equal(new Vector3(SpawnLap.StepMeters, 0f, 0f), SpawnLap.Offset(4, 4));
	}

	[Fact]
	public void TheSlotsRepeatSoARespawnNeverWalksFar()
	{
		// A respawn adds the player's deaths to its index: forty deaths is still
		// within one slot of its point.
		for (int index = 0; index < 400; index++)
		{
			Vector3 offset = SpawnLap.Offset(index, Points.Length);
			Assert.InRange(offset.X, 0f, SpawnLap.StepMeters);
			Assert.InRange(offset.Z, 0f, SpawnLap.StepMeters);
			Assert.Equal(0f, offset.Y);
		}

		Assert.Equal(SpawnLap.Offset(4, 4), SpawnLap.Offset(4 + (4 * SpawnLap.Slots), 4));
	}

	[Fact]
	public void AMapWithNoPointsOrOnePointStillHasSlots()
	{
		Assert.Equal(Vector3.Zero, SpawnLap.Offset(3, 0));
		Assert.Equal(Vector3.Zero, SpawnLap.Offset(0, 1));
		Assert.NotEqual(SpawnLap.Offset(1, 1), SpawnLap.Offset(2, 1));
	}
}
