using System;
using Gdpyr.Sim;
using Godot;
using Xunit;

namespace Gdpyr.Tests;

/// <summary>
/// What a strategist is told about the crew vehicles (docs/GROUND_VEHICLES.md §7.3,
/// GV-D14): a vehicle while the side's sensors see its hull, its occupants exactly when
/// it is, and everybody else every vehicle.
/// </summary>
public class CrewFogTests
{
	private static CrewVehicleRecord Record(byte slot, params int[] seats)
	{
		var record = new CrewVehicleRecord { Slot = slot, Kind = CrewVehicleKind.FourByFour };
		for (int i = 0; i < seats.Length; i++)
		{
			record.SetSeatPeer(i, seats[i]);
		}

		return record;
	}

	[Fact]
	public void TheGroundForce_IsToldAboutEveryVehicle_SeenOrNot()
	{
		CrewVehicleRecord[] all = { Record(0), Record(1), Record(3) };
		var into = new CrewVehicleRecord[4];
		Assert.Equal(3, CrewFog.Filter(Team.GroundForce, all, new bool[4], into));
	}

	[Fact]
	public void AStrategist_IsToldAboutTheVehiclesTheSideSees_AndNoOthers()
	{
		CrewVehicleRecord[] all = { Record(0), Record(1), Record(3) };
		var into = new CrewVehicleRecord[4];
		int count = CrewFog.Filter(Team.Strategist, all, new[] { false, true, true, false }, into);
		Assert.Equal(1, count);
		Assert.Equal(1, into[0].Slot);
	}

	[Fact]
	public void ASeatedPlayer_IsCarriedExactlyWhenTheirVehicleIs_WhateverTheirOwnCapsuleShows()
	{
		foreach (bool vehicleSeen in new[] { false, true })
		{
			foreach (bool playerSeen in new[] { false, true })
			{
				Assert.Equal(vehicleSeen, CrewFog.PlayerSeen(seated: true, vehicleSeen, playerSeen));
				Assert.Equal(playerSeen, CrewFog.PlayerSeen(seated: false, vehicleSeen, playerSeen));
			}
		}
	}

	[Fact]
	public void TheTwoMessages_NeverDisagreeAboutWhoIsInAVehicle()
	{
		// Every combination of which of three vehicles the side sees: a seated player is in
		// the player snapshot if and only if a vehicle record naming them in a seat is in
		// the vehicles' message.
		CrewVehicleRecord[] all = { Record(0, 11, 12), Record(1, 21), Record(2, 0, 0, 31) };
		int[] seated = { 11, 12, 21, 31 };
		var into = new CrewVehicleRecord[4];

		for (int mask = 0; mask < 8; mask++)
		{
			bool[] seen = { (mask & 1) != 0, (mask & 2) != 0, (mask & 4) != 0, false };
			int count = CrewFog.Filter(Team.Strategist, all, seen, into);

			foreach (int peer in seated)
			{
				int slot = Array.FindIndex(all, r => r.SeatOf(peer) >= 0);
				bool inPlayers = CrewFog.PlayerSeen(seated: true, seen[slot], playerSeen: true);
				bool inVehicles = false;
				for (int i = 0; i < count; i++)
				{
					inVehicles |= into[i].SeatOf(peer) >= 0;
				}

				Assert.Equal(inVehicles, inPlayers);
			}
		}
	}

	[Fact]
	public void TheHullsCentre_IsTheMiddleOfTheBoxRoundItsBoxes_WhereThePosePutsIt()
	{
		HullBox[] hull =
		{
			new(new Vector3(0f, 1f, -1f), new Vector3(1f, 0.5f, 1f)),
			new(new Vector3(0f, 0.5f, 2f), new Vector3(0.5f, 0.5f, 1f)),
		};

		// x −1..1, y 0..1.5, z −2..3.
		Assert.Equal(new Vector3(0f, 0.75f, 0.5f), CrewLayout.HullCentre(hull));
		Assert.Equal(Vector3.Zero, CrewLayout.HullCentre(ReadOnlySpan<HullBox>.Empty));

		var pose = new CrewPose(new Vector3(10f, 0f, 5f), MathF.PI);
		Vector3 world = CrewFog.HullCentre(hull, pose);
		Assert.True(world.DistanceTo(new Vector3(10f, 0.75f, 4.5f)) < 1e-4f);
	}

	[Fact]
	public void EachShippedHullsCentre_IsInsideOneOfItsBoxes_OrBetweenThem()
	{
		// The point the fog looks at is part of the vehicle, not the air beside it: within
		// its hull's bounds on every axis.
		foreach (string path in CrewVehicleFiles.Paths)
		{
			HullBox[] hull = CrewVehicleFiles.Hull(path);
			Vector3 centre = CrewLayout.HullCentre(hull);
			for (int axis = 0; axis < 3; axis++)
			{
				float min = float.MaxValue;
				float max = float.MinValue;
				foreach (HullBox box in hull)
				{
					min = MathF.Min(min, box.Center[axis] - box.HalfExtents[axis]);
					max = MathF.Max(max, box.Center[axis] + box.HalfExtents[axis]);
				}

				Assert.InRange(centre[axis], min, max);
			}
		}
	}
}
