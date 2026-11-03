using System;
using Gdpyr.Sim;
using Godot;
using Xunit;

namespace Gdpyr.Tests;

/// <summary>
/// A client's crew vehicles (docs/GROUND_VEHICLES.md §8.1): made on the first record,
/// remade for another kind, freed two seconds after a wreck or fifteen packets after the
/// server stopped naming one — never for the fog — and the driver's correction drawn
/// away rather than jumped (§8.2).
/// </summary>
public class CrewPuppetTests
{
	private static CrewVehicleRecord Record(byte slot, CrewVehicleKind kind = CrewVehicleKind.FourByFour,
		bool destroyed = false) =>
		new() { Slot = slot, Kind = kind, Destroyed = destroyed };

	private static PuppetChange[] Receive(CrewPuppetRoster roster, uint tick, params CrewVehicleRecord[] records)
	{
		var changes = new PuppetChange[SimConfig.MaxCrewVehicles];
		roster.Receive(tick, records, changes);
		return changes;
	}

	[Fact]
	public void TheFirstRecordForASlot_MakesItsNode_OfTheKindItSays()
	{
		var roster = new CrewPuppetRoster();
		PuppetChange[] changes = Receive(roster, 100, Record(2, CrewVehicleKind.Helicopter));
		Assert.Equal(PuppetChange.Create, changes[2]);
		Assert.True(roster.IsLive(2));
		Assert.Equal((byte)CrewVehicleKind.Helicopter, roster.KindOf(2));
		Assert.False(roster.IsLive(0));

		Assert.Equal(PuppetChange.Keep, Receive(roster, 102, Record(2, CrewVehicleKind.Helicopter))[2]);
	}

	[Fact]
	public void ARecordOfAnotherKind_ReplacesTheNode()
	{
		var roster = new CrewPuppetRoster();
		Receive(roster, 100, Record(0));
		Assert.Equal(PuppetChange.Replace, Receive(roster, 102, Record(0, CrewVehicleKind.Helicopter))[0]);
		Assert.Equal((byte)CrewVehicleKind.Helicopter, roster.KindOf(0));
	}

	[Fact]
	public void AWreck_IsFreedTwoSecondsAfterTheFirstRecordThatMarkedIt_FoggedOrNot()
	{
		foreach (bool fogged in new[] { false, true })
		{
			var roster = new CrewPuppetRoster();
			Receive(roster, 100, Record(1));
			Receive(roster, 110, Record(1, destroyed: true));
			Receive(roster, 112, Record(1, destroyed: true));
			Assert.True(roster.IsDestroyed(1));

			Assert.False(roster.DueToFree(1, 110 + CrewPuppetRoster.DestroyedLingerTicks - 1, fogged));
			Assert.True(roster.DueToFree(1, 110 + CrewPuppetRoster.DestroyedLingerTicks, fogged));
		}
	}

	[Fact]
	public void OnAClientNothingIsFoggedFor_AVehicleIsFreedAfterFifteenPacketsWithoutIt()
	{
		var roster = new CrewPuppetRoster();
		Receive(roster, 100, Record(0), Record(3));
		uint tick = 100;
		for (int i = 0; i < CrewPuppetRoster.MissedPacketsToFree - 1; i++)
		{
			tick += 2;
			Receive(roster, tick, Record(3));
		}

		Assert.False(roster.DueToFree(0, tick, fogged: false));
		tick += 2;
		Receive(roster, tick, Record(3));
		Assert.True(roster.DueToFree(0, tick, fogged: false));
		Assert.False(roster.DueToFree(3, tick, fogged: false));

		// A strategist's client takes the same silence for the fog: hidden, never freed.
		Assert.False(roster.DueToFree(0, tick, fogged: true));
		Assert.True(Fog.IsLost(roster.AgeTicks(0)));
		Assert.False(Fog.IsLost(roster.AgeTicks(3)));
	}

	[Fact]
	public void ALostPacket_IsNotAMiss_AndAReorderedOneIsNotEither()
	{
		var roster = new CrewPuppetRoster();
		Receive(roster, 100, Record(0));
		Receive(roster, 110, Record(0));

		// An old packet arriving late, without the vehicle: it says nothing new.
		for (int i = 0; i < 40; i++)
		{
			Receive(roster, 104);
		}

		Assert.False(roster.DueToFree(0, 110, fogged: false));
		Assert.Equal(0f, roster.AgeTicks(0));
	}

	[Fact]
	public void AVehicleBackOutOfTheFog_IsRestarted_SoItAppearsWhereItIs()
	{
		var roster = new CrewPuppetRoster();
		Receive(roster, 100, Record(0));
		Receive(roster, 102, Record(0));
		for (uint tick = 104; tick <= 140; tick += 2)
		{
			Receive(roster, tick);
		}

		Assert.Equal(PuppetChange.Restart, Receive(roster, 142, Record(0))[0]);
		Assert.Equal(PuppetChange.Keep, Receive(roster, 144, Record(0))[0]);
	}

	[Fact]
	public void AFreedSlot_IgnoresStragglers_AndIsMadeAgainByANewerRecord()
	{
		var roster = new CrewPuppetRoster();
		Receive(roster, 100, Record(0, destroyed: true));
		roster.Free(0);
		Assert.False(roster.IsLive(0));

		Assert.Equal(PuppetChange.Ignore, Receive(roster, 98, Record(0, destroyed: true))[0]);
		Assert.Equal(PuppetChange.Ignore, Receive(roster, 100, Record(0, destroyed: true))[0]);
		Assert.False(roster.IsLive(0));

		Assert.Equal(PuppetChange.Create, Receive(roster, 300, Record(0))[0]);
		Assert.False(roster.IsDestroyed(0));
	}

	[Fact]
	public void ASlotGivenAgainBeforeItsWreckWasFreed_GetsANewNode()
	{
		var roster = new CrewPuppetRoster();
		Receive(roster, 100, Record(0));
		Receive(roster, 102, Record(0, destroyed: true));
		Assert.Equal(PuppetChange.Replace, Receive(roster, 104, Record(0))[0]);
		Assert.False(roster.IsDestroyed(0));
		Assert.False(roster.DueToFree(0, 102 + CrewPuppetRoster.DestroyedLingerTicks, fogged: false));
	}

	[Fact]
	public void AJoinerArrivingDuringAWreck_DrawsItUntilItsTimeIsUp()
	{
		var roster = new CrewPuppetRoster();
		Assert.Equal(PuppetChange.Create, Receive(roster, 500, Record(2, destroyed: true))[2]);
		Assert.True(roster.IsDestroyed(2));
		Assert.True(roster.DueToFree(2, 500 + CrewPuppetRoster.DestroyedLingerTicks, fogged: false));
	}

	[Fact]
	public void Clear_ForgetsEverything()
	{
		var roster = new CrewPuppetRoster();
		Receive(roster, 100, Record(0), Record(1));
		roster.Clear();
		Assert.False(roster.IsLive(0));
		Assert.Equal(0u, roster.NewestPacketTick);
		Assert.Equal(PuppetChange.Create, Receive(roster, 5, Record(0))[0]);
	}

	// ---- the driver's correction, drawn away -------------------------------------------

	[Fact]
	public void ACorrection_IsAbsorbed_SoTheHullIsDrawnWhereItWas()
	{
		var before = new CrewPose(new Vector3(1f, 0.7f, 2f), 3.1f, 0.05f, -0.02f);
		var after = new CrewPose(new Vector3(1.2f, 0.7f, 2.1f), -3.1f, 0.04f, 0f);
		CrewVisualOffset offset = CrewVisualOffset.Absorb(before, after);

		CrewPose drawn = offset.Apply(after);
		Assert.True(drawn.Position.DistanceTo(before.Position) < 1e-5f);
		Assert.InRange(MathF.Abs(DroneSim.AngleBetween(drawn.Yaw, before.Yaw)), 0f, 1e-5f);
		Assert.Equal(before.Pitch, drawn.Pitch, 5);
		Assert.Equal(before.Roll, drawn.Roll, 5);

		// The short way round, not most of a turn.
		Assert.InRange(MathF.Abs(offset.Yaw), 0f, 0.1f);
	}

	[Fact]
	public void ACorrectionPastAMetre_IsShownAtOnce()
	{
		var before = new CrewPose(Vector3.Zero, 0f);
		Assert.True(CrewVisualOffset.Absorb(before, new CrewPose(new Vector3(1.01f, 0f, 0f), 0.3f)).IsZero);
		Assert.False(CrewVisualOffset.Absorb(before, new CrewPose(new Vector3(0.99f, 0f, 0f), 0f)).IsZero);
	}

	[Fact]
	public void AnOffset_HalvesEveryFiftyMilliseconds_AndIsGone()
	{
		CrewVisualOffset offset = CrewVisualOffset.Absorb(new CrewPose(new Vector3(0.4f, 0f, 0f), 0.2f),
			new CrewPose(Vector3.Zero, 0f));
		offset.Decay(SimConfig.VisualErrorHalfLifeSeconds);
		Assert.Equal(0.2f, offset.Position.X, 4);
		Assert.Equal(0.1f, offset.Yaw, 4);

		for (int i = 0; i < 120; i++)
		{
			offset.Decay(1f / 60f);
		}

		Assert.True(offset.IsZero);
	}

	// ---- the clock vehicles are drawn at ------------------------------------------------

	[Fact]
	public void ASteadyRenderClock_IsFollowedExactly()
	{
		var clock = new SmoothRenderClock();
		for (int i = 0; i < 100; i++)
		{
			Assert.Equal(1000f + i, clock.Step(1000f + i));
		}
	}

	[Theory]
	[InlineData(1f)]
	[InlineData(-1f)]
	public void AWholeTickCorrection_IsSlewedAWayOverTenTicks_NotJumped(float correction)
	{
		// SimClock walks its error off a tick at a time (docs/NETCODE.md §2); a 4x4 drawn at
		// that clock jumps a tick's travel — 30 cm at 18 m/s — and its passenger with it.
		var clock = new SmoothRenderClock();
		float render = 500f;
		clock.Step(render);
		render += 1f + correction;
		float previous = clock.Tick;
		for (int i = 0; i < 12; i++)
		{
			float drawn = clock.Step(render);
			Assert.InRange(drawn - previous, 1f - SmoothRenderClock.MaxSlewTicks - 1e-4f, 1f + SmoothRenderClock.MaxSlewTicks + 1e-4f);
			previous = drawn;
			render += 1f;
		}

		Assert.Equal(render - 1f, clock.Tick, 3);
	}

	[Fact]
	public void AResync_IsTakenAtOnce_AndSoIsTheFirstTick()
	{
		var clock = new SmoothRenderClock();
		Assert.Equal(77f, clock.Step(77f));
		clock.Step(78f);
		Assert.Equal(78f + SimConfig.ClockResyncThresholdTicks + 2f, clock.Step(78f + SimConfig.ClockResyncThresholdTicks + 2f));

		clock.Reset();
		Assert.False(clock.Started);
		Assert.Equal(5f, clock.Step(5f));
	}
}
