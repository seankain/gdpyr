using System;
using Gdpyr.Sim;
using Godot;
using Xunit;

namespace Gdpyr.Tests;

/// <summary>
/// Running the strategist's units over (docs/GROUND_VEHICLES.md §5.9, GV-D17): contact,
/// the hull's speed where it touches, the 8 m/s threshold, and the credit.
/// </summary>
public class RammingTests
{
	private const float KillSpeed = 8f;

	private static readonly HullBox[] Hull = CrewVehicleFiles.Hull(CrewVehicleFiles.FourByFourPath);

	/// <summary>A 4x4 at rest on flat ground at the origin, facing −Z, as the hull's boxes are authored.</summary>
	private static readonly CrewPose Pose = new(new Vector3(0f, FourByFourTraits.Default.RideHeightMeters, 0f), 0f);

	private static HitObb[] Placed(in CrewPose pose)
	{
		var boxes = new HitObb[Hull.Length];
		for (int i = 0; i < Hull.Length; i++)
		{
			boxes[i] = Hull[i].Place(pose);
		}

		return boxes;
	}

	/// <summary>A rifleman standing at <paramref name="feet"/>: 1.8 m, 0.4 m in radius (<c>Units/infantry.tres</c>).</summary>
	private static HitCapsule Rifleman(Vector3 feet) => HitCapsule.FromFeet(feet, 1.8f, 0.4f);

	private static float Impact(Vector3 feet, Vector3 velocity, float yawRate = 0f) =>
		Ramming.ImpactSpeed(Placed(Pose), Rifleman(feet), Pose.Position, velocity, yawRate);

	/// <summary>Just touching the bonnet's front face, dead ahead.</summary>
	private static readonly Vector3 DeadAhead = new(0f, 0f, -2.4f - 0.35f);

	[Fact]
	public void ARiflemanDeadAhead_DiesAt8MetresASecond_AndNotAt7Point9()
	{
		Assert.True(Ramming.Kills(Impact(DeadAhead, new Vector3(0f, 0f, -8f)), KillSpeed, unitIsVehicle: false));
		Assert.False(Ramming.Kills(Impact(DeadAhead, new Vector3(0f, 0f, -7.9f)), KillSpeed, unitIsVehicle: false));
		Assert.Equal(8f, Impact(DeadAhead, new Vector3(0f, 0f, -8f)), 4);
	}

	[Fact]
	public void OneItsSideGrazesAt18MetresASecond_WithNoSpeedTowardsIt_DoesNot()
	{
		// Beside the cab, touching its right face, the truck going past at top speed.
		var beside = new Vector3(0.95f + 0.35f, 0f, -0.4f);
		Assert.True(Ramming.Touches(Placed(Pose), Rifleman(beside), out _));

		float speed = Impact(beside, new Vector3(0f, 0f, -18f));
		Assert.Equal(0f, speed, 4);
		Assert.False(Ramming.Kills(speed, KillSpeed, false));
	}

	[Fact]
	public void OneSweptByTheTailOfATruckSpinningOnItsHandbrake_At8OrMoreThere_Does()
	{
		// Beside the bed's right corner. Spinning anticlockwise seen from above — positive
		// yaw rate, the nose going left — the tail swings to the right, into the rifleman.
		var tail = new Vector3(0.95f + 0.35f, 0f, 2.2f);
		float yawRate = 4f;
		float speed = Impact(tail, Vector3.Zero, yawRate);

		// ω × r at the bed's right face, 2.2 m back: 4 rad/s × 2.2 m = 8.8 m/s, all of it sideways.
		Assert.True(speed >= 8f, $"{speed} m/s");
		Assert.True(Ramming.Kills(speed, KillSpeed, false));

		// Spinning the other way, the tail swings away from them.
		Assert.Equal(0f, Impact(tail, Vector3.Zero, -yawRate));
	}

	[Fact]
	public void AParkedTruck_KillsNothing_WhateverWalksIntoIt()
	{
		var random = new Random(2);
		for (int i = 0; i < 1000; i++)
		{
			var feet = new Vector3((float)random.NextDouble() * 6f - 3f, 0f, (float)random.NextDouble() * 8f - 4f);
			float speed = Impact(feet, Vector3.Zero);
			Assert.Equal(0f, speed);
			Assert.False(Ramming.Kills(speed, KillSpeed, false));
		}
	}

	[Fact]
	public void FullReverse_KillsNothing()
	{
		// Backing into a rifleman behind the bed at the 4x4's 6 m/s.
		var behind = new Vector3(0f, 0f, 2.4f + 0.35f);
		float speed = Impact(behind, new Vector3(0f, 0f, FourByFourTraits.Default.ReverseSpeed));

		Assert.Equal(6f, speed, 4);
		Assert.False(Ramming.Kills(speed, KillSpeed, false));
	}

	[Fact]
	public void TheStrategistsVehicles_AreNeverRunOver()
	{
		Assert.False(Ramming.Kills(18f, KillSpeed, unitIsVehicle: true));
		Assert.False(Ramming.Kills(1000f, KillSpeed, unitIsVehicle: true));
		Assert.True(Ramming.Kills(18f, KillSpeed, unitIsVehicle: false));
	}

	[Fact]
	public void ACapsuleFurtherThanItsRadiusFromEveryBox_IsNoContact_AndOneInsideABoxIs()
	{
		HitObb[] hull = Placed(Pose);

		Assert.False(Ramming.Touches(hull, Rifleman(new Vector3(0f, 0f, -2.4f - 0.41f)), out int none));
		Assert.Equal(-1, none);
		Assert.True(Ramming.Touches(hull, Rifleman(new Vector3(0f, 0f, -2.4f - 0.39f)), out _));

		// Standing in the bed.
		Assert.True(Ramming.Touches(hull, Rifleman(new Vector3(0f, 0.3f, 1.5f)), out int bed));
		Assert.Equal(2, bed);
		Assert.Equal(0f, Impact(new Vector3(30f, 0f, 30f), new Vector3(0f, 0f, -18f)));
	}

	[Fact]
	public void AUnitInsideTheHull_IsMeasuredFromTheVehiclesCentre()
	{
		Vector3 centre = Pose.Position;
		Assert.Equal(8f, Ramming.ImpactSpeed(centre, new Vector3(0f, 0f, -8f), 0f, centre + new Vector3(0f, 0f, -1f),
			centre + new Vector3(0f, 0f, -1f)), 4);
		Assert.Equal(8f, Ramming.ImpactSpeed(centre, new Vector3(0f, 0f, -8f), 0f, centre, centre), 4);
		Assert.Equal(0f, Ramming.ImpactSpeed(centre, Vector3.Zero, 0f, centre, centre));
	}

	[Fact]
	public void TheSpeedThatKills_IsAboveEveryUnitOnFoot_AndAboveThe4x4sReverse()
	{
		Assert.True(KillSpeed > 4.5f);
		Assert.True(KillSpeed > FourByFourTraits.Default.ReverseSpeed);
	}

	[Fact]
	public void TheCredit_IsWhoeverIsAtTheControls_OrTheLastOneFor3SecondsInclusive()
	{
		Assert.Equal(7, Ramming.Credit(7, 0, 0, 1000));
		Assert.Equal(7, Ramming.Credit(7, 5, 900, 1000));

		Assert.Equal(5, Ramming.Credit(0, 5, 1000, 1000));
		Assert.Equal(5, Ramming.Credit(0, 5, 1000, 1000 + (uint)Ramming.CreditTicks));
		Assert.Equal(0, Ramming.Credit(0, 5, 1000, 1000 + (uint)Ramming.CreditTicks + 1));
		Assert.Equal(180, Ramming.CreditTicks);

		Assert.Equal(0, Ramming.Credit(0, 0, 1000, 1001));
	}
}
