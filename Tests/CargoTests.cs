using System;
using Gdpyr.Sim;
using Godot;
using Xunit;

namespace Gdpyr.Tests;

/// <summary>
/// The rules for getting into and out of a vehicle (docs/VEHICLES.md §4.1): a hold's
/// seats, who gets one when more were sent than there are, how close a unit has to be,
/// where it walks to, where it gets out, and what a wreck does to the riders.
/// </summary>
public class CargoTests
{
	// The bodies the plan is written against (§8.1): a rifleman, the technical, the dropship.
	private const float Rifleman = 0.4f;
	private const float Technical = 1.0f;
	private const float Dropship = 2.0f;

	private static readonly Vector3 Truck = new(10f, 0.5f, -20f);

	// ---- the hold --------------------------------------------------------------

	[Fact]
	public void TheFifthRider_IsRefused_AtFourSeats()
	{
		var hold = new CargoHold(4);
		for (ushort id = 1; id <= 4; id++)
		{
			Assert.True(hold.TryAdd(id));
		}

		Assert.True(hold.IsFull);
		Assert.False(hold.TryAdd(5));
		Assert.Equal(4, hold.Count);
		Assert.False(hold.Contains(5));
	}

	[Fact]
	public void ADuplicate_AndIdZero_AreRefused()
	{
		var hold = new CargoHold(4);
		Assert.True(hold.TryAdd(7));

		Assert.False(hold.TryAdd(7));
		Assert.False(hold.TryAdd(0));
		Assert.Equal(1, hold.Count);
		Assert.False(hold.Contains(0));
	}

	[Fact]
	public void Seats_AreClampedToTheCapacity_AndZeroCarriesNobody()
	{
		Assert.Equal(SimConfig.MaxPassengers, new CargoHold(SimConfig.MaxPassengers + 5).Seats);
		Assert.Equal(0, new CargoHold(-1).Seats);

		var none = new CargoHold(0);
		Assert.True(none.IsFull);
		Assert.False(none.TryAdd(1));
	}

	[Fact]
	public void TheDropshipsEight_FitTheCapacity()
	{
		// V-D2: MaxPassengers bounds both vehicles.
		var hold = new CargoHold(8);
		for (ushort id = 1; id <= 8; id++)
		{
			Assert.True(hold.TryAdd(id));
		}

		Assert.Equal(8, hold.Seats);
		Assert.True(hold.IsFull);
	}

	[Fact]
	public void Removing_KeepsEverybodyElsesOrder()
	{
		var hold = new CargoHold(4);
		hold.TryAdd(11);
		hold.TryAdd(12);
		hold.TryAdd(13);

		Assert.True(hold.Remove(12));
		Assert.False(hold.Remove(12));
		Assert.False(hold.Remove(0));

		Assert.Equal(2, hold.Count);
		Assert.Equal(11, hold[0]);
		Assert.Equal(13, hold[1]);
		Assert.Equal(0, hold[2]);
		Assert.Equal(2, hold.FreeSeats);
	}

	[Fact]
	public void TheOrdinalAccessor_ReadsZeroOutsideTheRiders()
	{
		var hold = new CargoHold(4);
		hold.TryAdd(3);

		Assert.Equal(3, hold[0]);
		Assert.Equal(0, hold[1]);
		Assert.Equal(0, hold[-1]);
		Assert.Equal(0, hold[CargoHold.Capacity]);
	}

	[Fact]
	public void Clearing_EmptiesTheHold_AndItFillsAgain()
	{
		var hold = new CargoHold(2);
		hold.TryAdd(1);
		hold.TryAdd(2);

		hold.Clear();

		Assert.True(hold.IsEmpty);
		Assert.False(hold.Contains(1));
		Assert.Equal(0, hold[0]);
		Assert.True(hold.TryAdd(2));
		Assert.True(hold.TryAdd(1));
		Assert.Equal(2, hold[0]);
	}

	// ---- who gets a seat ------------------------------------------------------

	[Fact]
	public void Admit_KeepsTheNearestFirst_AndReturnsTheRest()
	{
		ushort[] ids = { 21, 22, 23, 24, 25, 26 };
		float[] distances = { 9f, 2f, 14f, 5f, 3f, 30f };
		var boarders = new ushort[6];
		var leftovers = new ushort[6];

		int boarding = Cargo.Admit(ids, distances, 4, boarders, leftovers);

		Assert.Equal(4, boarding);
		Assert.Equal(new ushort[] { 22, 25, 24, 21 }, boarders[..boarding]);
		Assert.Equal(new ushort[] { 23, 26 }, leftovers[..(ids.Length - boarding)]);
	}

	[Fact]
	public void Admit_BreaksATie_ToTheLowerId()
	{
		ushort[] ids = { 40, 31, 35 };
		float[] distances = { 4f, 4f, 4f };
		var boarders = new ushort[3];
		var leftovers = new ushort[3];

		int boarding = Cargo.Admit(ids, distances, 2, boarders, leftovers);

		Assert.Equal(2, boarding);
		Assert.Equal(new ushort[] { 31, 35 }, boarders[..2]);
		Assert.Equal(40, leftovers[0]);
	}

	[Fact]
	public void Admit_WithNoFreeSeat_LeavesEverybody()
	{
		ushort[] ids = { 5, 6 };
		float[] distances = { 1f, 2f };
		var boarders = new ushort[2];
		var leftovers = new ushort[2];

		Assert.Equal(0, Cargo.Admit(ids, distances, 0, boarders, leftovers));
		Assert.Equal(0, Cargo.Admit(ids, distances, -3, boarders, leftovers));
		Assert.Equal(new ushort[] { 5, 6 }, leftovers);
	}

	[Fact]
	public void Admit_WithMoreSeatsThanCandidates_SeatsThemAll()
	{
		ushort[] ids = { 9, 8 };
		float[] distances = { 3f, 1f };
		var boarders = new ushort[8];

		Assert.Equal(2, Cargo.Admit(ids, distances, 8, boarders, Span<ushort>.Empty));
		Assert.Equal(new ushort[] { 8, 9 }, boarders[..2]);
	}

	[Fact]
	public void Admit_CountsAMissingOrNaNDistance_AsFurthest()
	{
		ushort[] ids = { 1, 2, 3 };
		float[] distances = { float.NaN, 50f };
		var boarders = new ushort[3];
		var leftovers = new ushort[3];

		int boarding = Cargo.Admit(ids, distances, 1, boarders, leftovers);

		Assert.Equal(1, boarding);
		Assert.Equal(2, boarders[0]);
		Assert.Equal(new ushort[] { 1, 3 }, leftovers[..2]);
	}

	[Fact]
	public void Admit_NeverSeatsMoreThanTheBoardersSpanHolds_AndLosesNobodyToIt()
	{
		ushort[] ids = { 1, 2, 3 };
		float[] distances = { 1f, 2f, 3f };
		var boarders = new ushort[1];
		var leftovers = new ushort[3];

		Assert.Equal(1, Cargo.Admit(ids, distances, 3, boarders, leftovers));
		Assert.Equal(1, boarders[0]);
		Assert.Equal(new ushort[] { 2, 3 }, leftovers[..2]);
	}

	// ---- reach ----------------------------------------------------------------

	[Fact]
	public void TheReach_IsBothRadiiAndAMetre()
	{
		// §4.1: 2.4 m for a rifleman at the technical, 3.4 m at the dropship.
		Assert.Equal(2.4f, Cargo.ReachMeters(Technical, Rifleman), 4);
		Assert.Equal(3.4f, Cargo.ReachMeters(Dropship, Rifleman), 4);
	}

	[Fact]
	public void TheReach_IsFlat_WithinTheBand()
	{
		// On a slope, 3 m below the truck and just inside the flat reach.
		Assert.True(Cargo.InReach(Truck, Technical, Truck + new Vector3(2.39f, -3f, 0f), Rifleman));
		Assert.True(Cargo.InReach(Truck, Technical, Truck + new Vector3(0f, 0f, -2.4f), Rifleman));
		Assert.False(Cargo.InReach(Truck, Technical, Truck + new Vector3(0f, 0f, -2.45f), Rifleman));
	}

	[Fact]
	public void TheReach_IsRefusedAboveOrBelowTheBand()
	{
		Vector3 beside = Truck + new Vector3(1.5f, 0f, 0f);

		Assert.True(Cargo.InReach(Truck, Technical, beside + (Vector3.Up * CapturePad.VerticalToleranceMeters), Rifleman));
		Assert.False(Cargo.InReach(Truck, Technical, beside + (Vector3.Up * 4.01f), Rifleman));
		Assert.False(Cargo.InReach(Truck, Technical, beside + (Vector3.Down * 4.01f), Rifleman));

		// A dropship still coming down: the walker is beside it, the hull is not.
		Assert.False(Cargo.InReach(Truck + (Vector3.Up * 6f), Dropship, Truck + new Vector3(2.9f, 0f, 0f), Rifleman));
	}

	[Fact]
	public void TheReach_RefusesANonFinitePosition()
	{
		Assert.False(Cargo.InReach(Truck, Technical, new Vector3(float.NaN, 0.5f, -20f), Rifleman));
	}

	// ---- where a boarder walks to ---------------------------------------------

	[Fact]
	public void TheStandOff_IsInReach_AndOutsideTheVehicle_AtEveryApproachAngle()
	{
		foreach (float vehicle in new[] { Technical, Dropship })
		{
			for (int degrees = 0; degrees < 360; degrees += 5)
			{
				float angle = Mathf.DegToRad(degrees);
				Vector3 from = Truck + new Vector3(MathF.Cos(angle) * 30f, 0f, MathF.Sin(angle) * 30f);

				Vector3 point = Cargo.StandOff(Truck, from, vehicle, Rifleman);

				Assert.True(Cargo.InReach(Truck, vehicle, point, Rifleman), $"{vehicle} m at {degrees}°");
				Assert.True(Flat(point, Truck) > vehicle + Rifleman, $"{vehicle} m at {degrees}°");

				// The nearest point of the ring: on the line from the vehicle to the walker.
				Vector3 towards = (from - Truck).Normalized();
				Assert.Equal(1f, (point - Truck).Normalized().Dot(towards), 3);
			}
		}
	}

	[Fact]
	public void TheStandOff_IsNeverTheCentre_EvenFromTheCentre()
	{
		Vector3 point = Cargo.StandOff(Truck, Truck, Technical, Rifleman);

		Assert.Equal(Cargo.StandOffMeters(Technical, Rifleman), Flat(point, Truck), 4);
		Assert.True(Cargo.InReach(Truck, Technical, point, Rifleman));
	}

	[Fact]
	public void UnderAnAirborneDropship_TheStandOffIsOnTheGround_AndInReachOnceItLands()
	{
		Vector3 ground = new(30f, 0.5f, 30f);
		Vector3 hovering = ground + (Vector3.Up * 9f);
		Vector3 walker = ground + new Vector3(-20f, 0f, 0f);

		Vector3 point = Cargo.StandOff(hovering, walker, Dropship, Rifleman, heightAboveGround: 9f);

		Assert.Equal(ground.Y, point.Y, 4);
		Assert.False(Cargo.InReach(hovering, Dropship, point, Rifleman));
		Assert.True(Cargo.InReach(ground, Dropship, point, Rifleman));
	}

	[Fact]
	public void TheStandOff_IgnoresANonFiniteHeight()
	{
		Vector3 point = Cargo.StandOff(Truck, Truck + Vector3.Left * 10f, Technical, Rifleman, float.NaN);

		Assert.Equal(Truck.Y, point.Y, 4);
	}

	// ---- the field -------------------------------------------------------------

	[Fact]
	public void TheField_IsInsideTheWalls_EdgeIncluded()
	{
		Assert.True(Cargo.InField(Vector3.Zero));
		Assert.True(Cargo.InField(new Vector3(115f, 3f, -115f)));
		Assert.False(Cargo.InField(new Vector3(115.1f, 0f, 0f)));
		Assert.False(Cargo.InField(new Vector3(0f, 0f, -119.6f)));
		Assert.False(Cargo.InField(new Vector3(float.NaN, 0f, 0f)));
		Assert.False(Cargo.InField(new Vector3(0f, 0f, float.PositiveInfinity)));
	}

	// ---- getting out -----------------------------------------------------------

	[Fact]
	public void ExitSlots_AreClearOfTheVehicle_AndOfEachOther()
	{
		foreach ((float vehicle, float passenger) in new[] { (Technical, Rifleman), (Dropship, Rifleman), (Technical, 0.45f) })
		{
			int count = Cargo.ExitSlotCount(vehicle, passenger);
			var slots = new Vector3[count];
			for (int i = 0; i < count; i++)
			{
				slots[i] = Cargo.ExitSlot(Truck, 0.7f, vehicle, passenger, i);
				Assert.True(Flat(slots[i], Truck) >= vehicle + passenger + Cargo.ExitClearanceMeters - 1e-3f);
				Assert.Equal(Truck.Y, slots[i].Y, 4);
			}

			for (int i = 0; i < count; i++)
			{
				for (int j = i + 1; j < count; j++)
				{
					Assert.True(Flat(slots[i], slots[j]) >= Cargo.ExitSpacingMeters - 1e-3f,
						$"slots {i} and {j} of {count} round a {vehicle} m vehicle");
				}
			}
		}
	}

	[Fact]
	public void ThereAreSlotsEnoughForAFullHold_OnTheFirstRing()
	{
		Assert.True(Cargo.ExitSlotCount(Technical, Rifleman) > 4);
		Assert.True(Cargo.ExitSlotCount(Dropship, Rifleman) > 8);

		// The first ring alone seats each vehicle's full load.
		Assert.True(FirstRing(Technical, Rifleman) >= 4);
		Assert.True(FirstRing(Dropship, Rifleman) >= 8);
	}

	[Fact]
	public void TheFirstSlot_IsBehindTheVehicle_AtEveryYaw()
	{
		for (int degrees = -180; degrees < 180; degrees += 15)
		{
			float yaw = Mathf.DegToRad(degrees);
			Vector3 forward = Aim.Direction(yaw, 0f);

			Vector3 slot = Cargo.ExitSlot(Truck, yaw, Technical, Rifleman, 0);
			Vector3 offset = slot - Truck;

			Assert.Equal(-1f, offset.Normalized().Dot(forward), 3);
			Assert.Equal(Cargo.ExitRingMeters(Technical, Rifleman), Flat(slot, Truck), 3);
		}
	}

	[Fact]
	public void TheFirstRing_GoesRoundFromBehind_ToTheFront()
	{
		const float Yaw = 1.1f;
		Vector3 forward = Aim.Direction(Yaw, 0f);
		int ring = FirstRing(Technical, Rifleman);

		float previous = -2f;
		for (int i = 0; i < ring; i++)
		{
			float facing = (Cargo.ExitSlot(Truck, Yaw, Technical, Rifleman, i) - Truck).Normalized().Dot(forward);
			Assert.True(facing >= previous - 1e-4f, $"slot {i} is further back than slot {i - 1}");
			previous = facing;
		}

		// An even ring ends straight in front; the second ring starts behind again.
		Assert.Equal(1f, previous, 3);
		Vector3 secondRing = Cargo.ExitSlot(Truck, Yaw, Technical, Rifleman, ring) - Truck;
		Assert.Equal(-1f, secondRing.Normalized().Dot(forward), 3);
		Assert.Equal(Cargo.ExitRingMeters(Technical, Rifleman) + Cargo.ExitSpacingMeters, secondRing.Length(), 3);
	}

	[Fact]
	public void ExitSlots_WrapPastTheLast_AndANegativeOrdinalIsTheFirst()
	{
		int count = Cargo.ExitSlotCount(Technical, Rifleman);

		Assert.Equal(Cargo.ExitSlot(Truck, 0.3f, Technical, Rifleman, 2),
			Cargo.ExitSlot(Truck, 0.3f, Technical, Rifleman, count + 2));
		Assert.Equal(Cargo.ExitSlot(Truck, 0.3f, Technical, Rifleman, 0),
			Cargo.ExitSlot(Truck, 0.3f, Technical, Rifleman, -4));
	}

	// ---- wrecks ----------------------------------------------------------------

	[Fact]
	public void TheWreckRule_IsInclusiveAtFourMetres()
	{
		Assert.Equal(CapturePad.VerticalToleranceMeters, Cargo.WreckSurvivableHeightMeters);

		Assert.False(Cargo.WreckKillsAll(0f));
		Assert.False(Cargo.WreckKillsAll(4f));
		Assert.True(Cargo.WreckKillsAll(4.01f));
		Assert.True(Cargo.WreckKillsAll(14f));

		// A height nobody could measure is not taken for the ground.
		Assert.True(Cargo.WreckKillsAll(float.NaN));
	}

	[Fact]
	public void AWreck_DealsHalfOfARidersMaximumHealth()
	{
		Assert.Equal(50f, Cargo.WreckDamage(100f), 4);
		Assert.Equal(0f, Cargo.WreckDamage(-5f), 4);
		Assert.Equal(0f, Cargo.WreckDamage(float.NaN), 4);
	}

	[Fact]
	public void ABoarder_GivesUpAfterTwentySeconds()
	{
		Assert.Equal(20 * SimConfig.TickRate, Cargo.BoardTimeoutTicks);
	}

	// ---- helpers ---------------------------------------------------------------

	private static float Flat(Vector3 a, Vector3 b)
	{
		Vector3 d = a - b;
		d.Y = 0f;
		return d.Length();
	}

	/// <summary>Slots on the first ring: the ordinals before the first one at the second ring's radius.</summary>
	private static int FirstRing(float vehicle, float passenger)
	{
		float inner = Cargo.ExitRingMeters(vehicle, passenger);
		int count = Cargo.ExitSlotCount(vehicle, passenger);
		for (int i = 0; i < count; i++)
		{
			if (Flat(Cargo.ExitSlot(Vector3.Zero, 0f, vehicle, passenger, i), Vector3.Zero) > inner + 0.1f)
			{
				return i;
			}
		}

		return count;
	}
}
