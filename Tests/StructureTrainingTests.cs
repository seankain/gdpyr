using System;
using Gdpyr.Sim;
using Godot;
using Xunit;

namespace Gdpyr.Tests;

/// <summary>
/// Where a sniper tower's and a pillbox's trained units come out and walk to
/// (docs/NETCODE.md §10.8): the side facing home, the next side round when another
/// structure is on it, rows along the wall, and a rally point straight out.
/// </summary>
public class StructureTrainingTests
{
	/// <summary>A pillbox's footprint, 3.6 m square, at the origin and turned square to the world.</summary>
	private static readonly Footprint Pillbox = new(Vector3.Zero, 0f, 1.8f, 1.8f);

	/// <summary>The barracks, due -Z of it.</summary>
	private static readonly Vector3 Home = new(0f, 0f, -60f);

	[Fact]
	public void TheDoor_IsOnTheSideFacingHome_ClearOfTheWall()
	{
		Vector3 door = StructureTraining.Door(Pillbox, Home, ReadOnlySpan<Footprint>.Empty);

		Assert.Equal(0f, door.X, 3);
		Assert.Equal(-(1.8f + StructureTraining.DoorStandoffMeters), door.Z, 3);
		Assert.False(Pillbox.Contains(door, StructureTraining.DoorClearanceMeters));
	}

	[Fact]
	public void ADoorBlockedByAnotherStructure_MovesToTheNextSideRound()
	{
		// A sandbag wall laid along the home side.
		var wall = new Footprint(new Vector3(0f, 0f, -3.4f), 0f, 2f, 0.4f);

		Vector3 door = StructureTraining.Door(Pillbox, Home, new[] { wall });

		Assert.False(wall.Contains(door, StructureTraining.DoorClearanceMeters));
		Assert.Equal(1.8f + StructureTraining.DoorStandoffMeters, MathF.Abs(door.X), 3);
		Assert.Equal(0f, door.Z, 3);
	}

	[Fact]
	public void BoxedInOnEverySide_ItKeepsTheHomeSide()
	{
		var around = new[]
		{
			new Footprint(new Vector3(0f, 0f, -3.4f), 0f, 2f, 0.4f),
			new Footprint(new Vector3(0f, 0f, 3.4f), 0f, 2f, 0.4f),
			new Footprint(new Vector3(-3.4f, 0f, 0f), 0f, 0.4f, 2f),
			new Footprint(new Vector3(3.4f, 0f, 0f), 0f, 0.4f, 2f),
		};

		Vector3 door = StructureTraining.Door(Pillbox, Home, around);

		Assert.Equal(-(1.8f + StructureTraining.DoorStandoffMeters), door.Z, 3);
	}

	[Fact]
	public void TheDoor_FollowsTheStructuresOwnTurn()
	{
		// Turned a quarter: its own axes are the world's, swapped, and the door is still
		// on the wall facing home.
		var turned = new Footprint(Vector3.Zero, MathF.PI / 2f, 1.8f, 1.0f);

		Vector3 door = StructureTraining.Door(turned, Home, ReadOnlySpan<Footprint>.Empty);

		Assert.Equal(0f, door.X, 3);
		Assert.Equal(-(1.8f + StructureTraining.DoorStandoffMeters), door.Z, 3);
	}

	[Fact]
	public void FreshUnits_StandInRowsAlongTheWall_AndWrap()
	{
		Vector3 door = StructureTraining.Door(Pillbox, Home, ReadOnlySpan<Footprint>.Empty);
		int perCycle = StructureTraining.SlotsPerRow * StructureTraining.SlotRows;

		for (int i = 0; i < perCycle; i++)
		{
			Vector3 slot = StructureTraining.Slot(Pillbox, door, i);
			Assert.False(Pillbox.Contains(slot, 0.4f));

			for (int j = 0; j < i; j++)
			{
				Assert.True(slot.DistanceTo(StructureTraining.Slot(Pillbox, door, j))
					>= StructureTraining.SlotSpacingMeters - 1e-3f);
			}
		}

		Assert.Equal(StructureTraining.Slot(Pillbox, door, 0), StructureTraining.Slot(Pillbox, door, perCycle));
	}

	[Fact]
	public void TheDefaultRally_IsStraightOutFromTheDoor()
	{
		Vector3 door = StructureTraining.Door(Pillbox, Home, ReadOnlySpan<Footprint>.Empty);
		Vector3 rally = StructureTraining.DefaultRally(Pillbox, door);

		Assert.Equal(door.Z - StructureTraining.RallyMeters, rally.Z, 3);
		Assert.Equal(0f, rally.X, 3);
	}
}
