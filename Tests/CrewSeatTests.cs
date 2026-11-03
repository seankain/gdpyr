using System;
using Gdpyr.Sim;
using Godot;
using Xunit;

namespace Gdpyr.Tests;

/// <summary>
/// Getting into, moving about and getting out of a crew vehicle, what a seat may fire,
/// and the use key's grown table (docs/GROUND_VEHICLES.md §5.3).
/// </summary>
public class CrewSeatTests
{
	// ---- the use key ----------------------------------------------------------------------

	private static UseSituation Situation(UseIntent intent = UseIntent.Tap, CarryKind carrying = CarryKind.None,
		bool mounted = false, bool gun = false, bool can = false, bool onGround = true, bool locker = false,
		bool seated = false, bool vehicle = false, bool kiosk = false) =>
		new(intent, carrying, mounted, gun, can, onGround, locker, seated, vehicle, kiosk);

	/// <summary>
	/// The table as it stood before crew vehicles (<c>6699b03</c>), copied here so that
	/// what it answered is a fixed reference rather than whatever the table says now.
	/// </summary>
	private static UseAction OldResolve(in UseSituation s)
	{
		if (s.Intent == UseIntent.None)
		{
			return UseAction.None;
		}

		bool hold = s.Intent == UseIntent.Hold;
		if (s.Mounted)
		{
			return hold ? UseAction.PickUpGun : UseAction.Dismount;
		}

		switch (s.Carrying)
		{
			case CarryKind.Gun:
				return s.OnGround ? UseAction.Deploy : UseAction.None;
			case CarryKind.AmmoCan:
				if (s.GunInReach && !hold)
				{
					return UseAction.Resupply;
				}

				return s.OnGround ? UseAction.DropCan : UseAction.None;
		}

		if (s.LockerInReach)
		{
			return hold ? UseAction.None : UseAction.OpenLocker;
		}

		if (s.CanInReach)
		{
			return UseAction.PickUpCan;
		}

		if (s.GunInReach)
		{
			return hold ? UseAction.PickUpGun : UseAction.Mount;
		}

		return UseAction.None;
	}

	[Fact]
	public void EveryPressTheOldTableAnswered_StillAnswersTheSame()
	{
		int rows = 0;
		foreach (UseIntent intent in Enum.GetValues<UseIntent>())
		{
			foreach (CarryKind carrying in Enum.GetValues<CarryKind>())
			{
				for (int bits = 0; bits < 32; bits++)
				{
					var s = Situation(intent, carrying, mounted: (bits & 1) != 0, gun: (bits & 2) != 0,
						can: (bits & 4) != 0, onGround: (bits & 8) != 0, locker: (bits & 16) != 0);
					Assert.Equal(OldResolve(s), EmplacementSim.Resolve(s));
					rows++;
				}
			}
		}

		Assert.Equal(3 * 3 * 32, rows);
	}

	[Fact]
	public void TheNewActionsAreAppended_SoEveryOldOneKeepsItsByte()
	{
		Assert.Equal(8, (int)UseAction.OpenLocker);
		Assert.Equal(9, (int)UseAction.EnterVehicle);
		Assert.Equal(10, (int)UseAction.ExitVehicle);
		Assert.Equal(11, (int)UseAction.ChangeSeat);
		Assert.Equal(12, (int)UseAction.OpenKiosk);
	}

	[Fact]
	public void Seated_ATapGetsOut_AndAHoldChangesSeat()
	{
		Assert.Equal(UseAction.ExitVehicle, EmplacementSim.Resolve(Situation(UseIntent.Tap, seated: true)));
		Assert.Equal(UseAction.ChangeSeat, EmplacementSim.Resolve(Situation(UseIntent.Hold, seated: true)));
		Assert.Equal(UseAction.None, EmplacementSim.Resolve(Situation(UseIntent.None, seated: true)));
	}

	[Fact]
	public void Seated_NothingElseIsInReach()
	{
		// A gun, a can, the locker, the kiosk or another vehicle beside the seat changes nothing.
		foreach (UseIntent intent in new[] { UseIntent.Tap, UseIntent.Hold })
		{
			UseAction expected = intent == UseIntent.Tap ? UseAction.ExitVehicle : UseAction.ChangeSeat;
			Assert.Equal(expected, EmplacementSim.Resolve(Situation(intent, seated: true, gun: true, can: true,
				locker: true, vehicle: true, kiosk: true)));
			Assert.Equal(expected, EmplacementSim.Resolve(Situation(intent, seated: true, onGround: false)));
		}
	}

	[Fact]
	public void EmptyHandedOnTheGroundBesideAHull_ATapGetsIn_AndAHoldDoesNothing()
	{
		Assert.Equal(UseAction.EnterVehicle, EmplacementSim.Resolve(Situation(UseIntent.Tap, vehicle: true)));
		Assert.Equal(UseAction.None, EmplacementSim.Resolve(Situation(UseIntent.Hold, vehicle: true)));
		Assert.Equal(UseAction.None, EmplacementSim.Resolve(Situation(UseIntent.Tap, vehicle: true, onGround: false)));
	}

	[Fact]
	public void SomethingInYourHands_IsWhatTheKeyIsAbout_BesideAHullOrTheKiosk()
	{
		Assert.Equal(UseAction.Deploy, EmplacementSim.Resolve(Situation(UseIntent.Tap, CarryKind.Gun, vehicle: true)));
		Assert.Equal(UseAction.DropCan, EmplacementSim.Resolve(Situation(UseIntent.Tap, CarryKind.AmmoCan, vehicle: true)));
		Assert.Equal(UseAction.Deploy, EmplacementSim.Resolve(Situation(UseIntent.Tap, CarryKind.Gun, kiosk: true)));
		Assert.Equal(UseAction.Dismount, EmplacementSim.Resolve(Situation(UseIntent.Tap, mounted: true, vehicle: true)));
		Assert.Equal(UseAction.Dismount, EmplacementSim.Resolve(Situation(UseIntent.Tap, mounted: true, kiosk: true)));
	}

	[Fact]
	public void AtTheKiosk_ATapOpensIt_AndAHoldDoesNothing()
	{
		Assert.Equal(UseAction.OpenKiosk, EmplacementSim.Resolve(Situation(UseIntent.Tap, kiosk: true)));
		Assert.Equal(UseAction.None, EmplacementSim.Resolve(Situation(UseIntent.Hold, kiosk: true)));
	}

	[Fact]
	public void TheNearestThingInReachWins_AsTheCallerSetsIt()
	{
		// The caller sets the kiosk's and the hull's flags only when each is the nearest of
		// locker, kiosk, gun, can and hull, as it already does for the locker and the can.
		Assert.Equal(UseAction.OpenKiosk, EmplacementSim.Resolve(Situation(UseIntent.Tap, gun: true, can: true, kiosk: true)));
		Assert.Equal(UseAction.EnterVehicle, EmplacementSim.Resolve(Situation(UseIntent.Tap, gun: true, can: true, vehicle: true)));
		Assert.Equal(UseAction.Mount, EmplacementSim.Resolve(Situation(UseIntent.Tap, gun: true)));
	}

	// ---- getting in -----------------------------------------------------------------------

	private static EnterSituation Enter(bool alive = true, bool groundForce = true,
		CarryKind carrying = CarryKind.None, bool mounted = false, bool drone = false, bool seated = false,
		bool destroyed = false, float speed = 0f, bool freeSeat = true,
		CrewVehicleKind kind = CrewVehicleKind.FourByFour, bool grounded = true) =>
		new(alive, groundForce, carrying, mounted, drone, seated, destroyed, speed, freeSeat, kind, grounded);

	[Fact]
	public void AnybodyFitAndEmptyHanded_GetsIntoAStillVehicleWithAFreeSeat()
	{
		Assert.True(CrewSeats.MayEnter(Enter()));
		Assert.True(CrewSeats.MayEnter(Enter(kind: CrewVehicleKind.Helicopter)));
	}

	[Fact]
	public void EachGateOfGettingIn_RefusesOnItsOwn()
	{
		Assert.False(CrewSeats.MayEnter(Enter(alive: false)));
		Assert.False(CrewSeats.MayEnter(Enter(groundForce: false)));
		Assert.False(CrewSeats.MayEnter(Enter(carrying: CarryKind.Gun)));
		Assert.False(CrewSeats.MayEnter(Enter(carrying: CarryKind.AmmoCan)));
		Assert.False(CrewSeats.MayEnter(Enter(mounted: true)));
		Assert.False(CrewSeats.MayEnter(Enter(drone: true)));
		Assert.False(CrewSeats.MayEnter(Enter(seated: true)));
		Assert.False(CrewSeats.MayEnter(Enter(destroyed: true)));
		Assert.False(CrewSeats.MayEnter(Enter(freeSeat: false)));
		Assert.False(CrewSeats.MayEnter(Enter(kind: CrewVehicleKind.Helicopter, grounded: false)));
	}

	[Fact]
	public void AVehicleMovingFasterThan3MetresASecond_CannotBeGotInto()
	{
		Assert.True(CrewSeats.MayEnter(Enter(speed: 3f)));
		Assert.False(CrewSeats.MayEnter(Enter(speed: 3.01f)));
		Assert.False(CrewSeats.MayEnter(Enter(speed: float.NaN)));
	}

	[Fact]
	public void A4x4InTheAir_CanStillBeGotIntoIfSlow_OnlyAHelicopterMustBeDown()
	{
		Assert.True(CrewSeats.MayEnter(Enter(kind: CrewVehicleKind.FourByFour, grounded: false)));
	}

	// ---- picking and changing seats ---------------------------------------------------------

	private static CrewSeat Seat(SeatRole role, Vector3 door) =>
		new(role, role == SeatRole.Driver, new HitCapsule(Vector3.Zero, Vector3.Up, 0.3f), Vector3.Up, door, door,
			SeatArc.FromDegrees(-180f, 180f, -90f, 90f));

	private static readonly CrewSeat[] ThreeSeats =
	{
		Seat(SeatRole.Driver, new Vector3(-1.5f, 0f, -0.4f)),
		Seat(SeatRole.Gunner, new Vector3(0f, 0f, 3f)),
		Seat(SeatRole.Passenger, new Vector3(1.5f, 0f, 1.6f)),
	};

	[Fact]
	public void TheFreeSeatWhoseDoorIsNearest_IsTheOneTaken()
	{
		var pose = new CrewPose(Vector3.Zero, 0f);

		Assert.Equal(0, CrewSeats.PickSeat(ThreeSeats, new int[3], pose, new Vector3(-2f, 0f, -0.5f)));
		Assert.Equal(1, CrewSeats.PickSeat(ThreeSeats, new int[3], pose, new Vector3(0f, 0f, 4f)));
		Assert.Equal(2, CrewSeats.PickSeat(ThreeSeats, new int[3], pose, new Vector3(2f, 0f, 1.5f)));

		// Beside the bonnet the driver's door is nearest and the bed's next; somebody is driving.
		Assert.Equal(0, CrewSeats.PickSeat(ThreeSeats, new int[3], pose, new Vector3(0f, 0f, -0.4f)));
		Assert.Equal(2, CrewSeats.PickSeat(ThreeSeats, new[] { 7, 0, 0 }, pose, new Vector3(0f, 0f, -0.4f)));
	}

	[Fact]
	public void TheDoorsAreWhereThePoseTurnsThem()
	{
		// Turned half round, the driver's door — on the left — is on the world's right.
		var pose = new CrewPose(Vector3.Zero, MathF.PI);

		Assert.Equal(0, CrewSeats.PickSeat(ThreeSeats, new int[3], pose, new Vector3(2f, 0f, 0.5f)));
	}

	[Fact]
	public void ATie_GoesToTheLowerSeat()
	{
		CrewSeat[] seats =
		{
			Seat(SeatRole.Driver, new Vector3(-1f, 0f, 0f)),
			Seat(SeatRole.Passenger, new Vector3(1f, 0f, 0f)),
		};

		Assert.Equal(0, CrewSeats.PickSeat(seats, new int[2], new CrewPose(Vector3.Zero, 0f), Vector3.Zero));
	}

	[Fact]
	public void EverySeatTaken_IsNoSeat()
	{
		Assert.Equal(-1, CrewSeats.PickSeat(ThreeSeats, new[] { 2, 3, 4 }, new CrewPose(Vector3.Zero, 0f), Vector3.Zero));
		Assert.False(CrewSeats.HasFreeSeat(new[] { 2, 3, 4 }, 3));
		Assert.True(CrewSeats.HasFreeSeat(new[] { 2, 0, 4 }, 3));
		Assert.Equal(2, CrewSeats.Occupied(new[] { 2, 0, 4 }, 3));
	}

	[Fact]
	public void AHold_MovesToTheNextFreeSeat_RoundPastTheLast()
	{
		Assert.Equal(1, CrewSeats.NextSeat(3, new[] { 5, 0, 0 }, 0));
		Assert.Equal(2, CrewSeats.NextSeat(3, new[] { 5, 6, 0 }, 0));
		Assert.Equal(0, CrewSeats.NextSeat(3, new[] { 0, 6, 5 }, 2));
		Assert.Equal(1, CrewSeats.NextSeat(3, new[] { 5, 0, 6 }, 2));
	}

	[Fact]
	public void AHold_WithNoOtherFreeSeat_GoesNowhere()
	{
		Assert.Equal(-1, CrewSeats.NextSeat(3, new[] { 5, 6, 7 }, 1));
		Assert.Equal(-1, CrewSeats.NextSeat(2, new[] { 5, 6 }, 0));
		Assert.Equal(-1, CrewSeats.NextSeat(1, new[] { 5 }, 0));
	}

	// ---- getting out ----------------------------------------------------------------------

	[Fact]
	public void OutOfAHelicopter_OnlyWithin4MetresOfTheGround_OutOfA4x4Always()
	{
		Assert.True(CrewSeats.MayExit(CrewVehicleKind.Helicopter, 0f));
		Assert.True(CrewSeats.MayExit(CrewVehicleKind.Helicopter, 4f));
		Assert.False(CrewSeats.MayExit(CrewVehicleKind.Helicopter, 4.01f));
		Assert.False(CrewSeats.MayExit(CrewVehicleKind.Helicopter, float.NaN));
		Assert.True(CrewSeats.MayExit(CrewVehicleKind.FourByFour, 40f));
	}

	[Fact]
	public void ExitCandidates_AreThisSeatsExit_ThenTheOthersInOrder_ThenTheRing()
	{
		var pose = new CrewPose(new Vector3(5f, 0f, 5f), 0f);
		const float radius = 2.4f;
		const float body = 0.4f;

		Assert.Equal(pose.ToWorld(ThreeSeats[1].Exit), CrewSeats.ExitCandidate(ThreeSeats, 1, 0, pose, radius, body));
		Assert.Equal(pose.ToWorld(ThreeSeats[0].Exit), CrewSeats.ExitCandidate(ThreeSeats, 1, 1, pose, radius, body));
		Assert.Equal(pose.ToWorld(ThreeSeats[2].Exit), CrewSeats.ExitCandidate(ThreeSeats, 1, 2, pose, radius, body));
		Assert.Equal(Cargo.ExitSlot(pose.Position, pose.Yaw, radius, body, 0),
			CrewSeats.ExitCandidate(ThreeSeats, 1, 3, pose, radius, body));

		// Past the last it wraps.
		int count = CrewSeats.ExitCandidateCount(3, radius, body);
		Assert.Equal(3 + Cargo.ExitSlotCount(radius, body), count);
		Assert.Equal(CrewSeats.ExitCandidate(ThreeSeats, 1, 0, pose, radius, body),
			CrewSeats.ExitCandidate(ThreeSeats, 1, count, pose, radius, body));
	}

	// ---- firing ---------------------------------------------------------------------------

	[Fact]
	public void DriversNeverFire_TheGunnerFiresTheGun_APassengerTheirOwn()
	{
		Assert.Equal(SeatWeapon.None, CrewSeats.WeaponOf(SeatRole.Driver));
		Assert.Equal(SeatWeapon.MountedGun, CrewSeats.WeaponOf(SeatRole.Gunner));
		Assert.Equal(SeatWeapon.OwnWeapon, CrewSeats.WeaponOf(SeatRole.Passenger));

		foreach (string path in CrewVehicleFiles.Paths)
		{
			foreach (CrewSeat seat in CrewVehicleFiles.Seats(path))
			{
				for (float yaw = -3.1f; yaw <= 3.1f; yaw += 0.1f)
				{
					for (float pitch = -1.5f; pitch <= 1.5f; pitch += 0.1f)
					{
						bool fires = CrewSeats.MayFire(seat, yaw, pitch);
						if (seat.Role == SeatRole.Driver)
						{
							Assert.False(fires);
						}
						else if (seat.Role == SeatRole.Gunner)
						{
							Assert.True(fires);
						}
					}
				}
			}
		}
	}

	[Theory]
	[InlineData(45f, 315f)]
	[InlineData(15f, 165f)]
	[InlineData(-60f, 60f)]
	public void ASeatsArc_IncludesItsEdges_AndNothingPastThem(float yawMin, float yawMax)
	{
		var seat = new CrewSeat(SeatRole.Passenger, false, new HitCapsule(Vector3.Zero, Vector3.Up, 0.3f), Vector3.Up,
			Vector3.Zero, Vector3.Zero, SeatArc.FromDegrees(yawMin, yawMax, -30f, 60f));
		float Rad(float degrees) => DroneSim.WrapAngle(Mathf.DegToRad(degrees));
		float mid = (yawMin + yawMax) * 0.5f;

		Assert.True(CrewSeats.MayFire(seat, Rad(yawMin), 0f));
		Assert.True(CrewSeats.MayFire(seat, Rad(yawMax), 0f));
		Assert.True(CrewSeats.MayFire(seat, Rad(mid), 0f));
		Assert.False(CrewSeats.MayFire(seat, Rad(yawMin - 0.5f), 0f));
		Assert.False(CrewSeats.MayFire(seat, Rad(yawMax + 0.5f), 0f));

		Assert.True(CrewSeats.MayFire(seat, Rad(mid), Mathf.DegToRad(-30f)));
		Assert.True(CrewSeats.MayFire(seat, Rad(mid), Mathf.DegToRad(60f)));
		Assert.False(CrewSeats.MayFire(seat, Rad(mid), Mathf.DegToRad(-30.5f)));
		Assert.False(CrewSeats.MayFire(seat, Rad(mid), Mathf.DegToRad(60.5f)));
	}

	[Fact]
	public void ADriversArc_HoldsNoDirection()
	{
		for (float yaw = -3.2f; yaw <= 3.2f; yaw += 0.05f)
		{
			for (float pitch = -1.6f; pitch <= 1.6f; pitch += 0.05f)
			{
				Assert.False(SeatArc.None.Contains(yaw, pitch));
			}
		}

		Assert.False(SeatArc.None.Contains(0f, 1f));
		Assert.True(SeatArc.None.IsEmpty);
		Assert.False(SeatArc.FromDegrees(-180f, 180f, -15f, 50f).Contains(float.NaN, 0f));
	}

	[Fact]
	public void TheBedPassenger_CannotFireThroughTheCab_AndTheDoorPassenger_OnlyOutOfTheDoor()
	{
		CrewSeat bed = Array.Find(CrewVehicleFiles.Seats(CrewVehicleFiles.FourByFourPath), s => s.Role == SeatRole.Passenger);
		Assert.False(CrewSeats.MayFire(bed, 0f, 0f), "dead ahead, through the cab");
		Assert.True(CrewSeats.MayFire(bed, MathF.PI / 2f, 0f), "out to the left");
		Assert.True(CrewSeats.MayFire(bed, -MathF.PI / 2f, 0f), "out to the right");
		Assert.True(CrewSeats.MayFire(bed, MathF.PI, 0f), "behind");

		CrewSeat door = Array.Find(CrewVehicleFiles.Seats(CrewVehicleFiles.HelicopterPath), s => s.Role == SeatRole.Passenger);
		Assert.True(CrewSeats.MayFire(door, MathF.PI / 2f, -0.5f), "out of the left door, and down");
		Assert.False(CrewSeats.MayFire(door, -MathF.PI / 2f, 0f), "across the cabin to the right");
		Assert.False(CrewSeats.MayFire(door, 0f, 0f), "through the nose");
	}

	[Fact]
	public void AimRelativeToTheHull_TakesItsAttitudeOut()
	{
		// Nose up 10°: a level look is 10° down to the hull.
		var pitched = new CrewPose(Vector3.Zero, 0.7f, Mathf.DegToRad(10f), 0f);
		CrewSeats.RelativeAim(pitched, 0.7f, 0f, out float yaw, out float pitch);
		Assert.Equal(0f, yaw, 4);
		Assert.Equal(Mathf.DegToRad(-10f), pitch, 4);

		// Turned 90° left: a look due north is 90° to the hull's right.
		var turned = new CrewPose(Vector3.Zero, MathF.PI / 2f);
		CrewSeats.RelativeAim(turned, 0f, 0f, out yaw, out _);
		Assert.Equal(-MathF.PI / 2f, yaw, 4);
	}

	// ---- the collider ----------------------------------------------------------------------

	[Theory]
	[InlineData(0.95f, 0.95f, 2.4f, 0.25f)]
	[InlineData(0.9f, 1.2f, 1.5f, 0.3f)]
	[InlineData(1f, 0.5f, 2f, 0.5f)]
	public void TheCollidersTwelveEdges_AreBevelled_NotOnlyItsCorners(float hx, float hy, float hz, float bevel)
	{
		// Every point of the hull keeps |a| + |b| at most the edge's own sum less the bevel,
		// for each pair of axes; a hull of points that does so is cut along every edge, so
		// the middle of a vertical edge — where a wall's end meets a truck's front corner —
		// is outside it (docs/GROUND_VEHICLES.md §6.1). A hull that only truncates the
		// corners has points on the edges, and fails here.
		var centre = new Vector3(0.3f, 0.5f, -0.2f);
		Vector3[] points = CrewLayout.BevelledBox(centre, new Vector3(hx, hy, hz), bevel);
		Assert.Equal(24, points.Length);

		float xMax = 0f, yMax = 0f, zMax = 0f;
		foreach (Vector3 p in points)
		{
			Vector3 d = (p - centre).Abs();
			Assert.True(d.X + d.Z <= hx + hz - bevel + 1e-5f, $"{p}: on a vertical edge");
			Assert.True(d.X + d.Y <= hx + hy - bevel + 1e-5f, $"{p}: on a fore-and-aft edge");
			Assert.True(d.Y + d.Z <= hy + hz - bevel + 1e-5f, $"{p}: on a sideways edge");
			xMax = MathF.Max(xMax, d.X);
			yMax = MathF.Max(yMax, d.Y);
			zMax = MathF.Max(zMax, d.Z);
		}

		// And each face is still there, flat, at the box's own extent.
		Assert.Equal(hx, xMax, 5);
		Assert.Equal(hy, yMax, 5);
		Assert.Equal(hz, zMax, 5);
	}

	[Fact]
	public void ABevelOfNothing_IsTheBox()
	{
		Vector3[] points = CrewLayout.BevelledBox(Vector3.Zero, new Vector3(1f, 2f, 3f), 0f);
		foreach (Vector3 p in points)
		{
			Assert.Equal(new Vector3(1f, 2f, 3f), p.Abs());
		}
	}

	[Fact]
	public void TheGun_TurnsAllTheWayRound_AndElevatesFromMinus15ToPlus50_RelativeToTheHull()
	{
		var pose = new CrewPose(Vector3.Zero, 0.3f, Mathf.DegToRad(8f), Mathf.DegToRad(-5f));
		float min = Mathf.DegToRad(-15f);
		float max = Mathf.DegToRad(50f);

		Vector3 behind = CrewSeats.GunDirection(pose, 0.3f + MathF.PI, 0f, MathF.PI, min, max);
		CrewSeats.RelativeAim(pose, 0.3f + MathF.PI, 0f, out float wantedYaw, out _);
		Aim.Angles(pose.DirectionToLocal(behind), out float yaw, out _);
		Assert.Equal(wantedYaw, yaw, 3);

		Vector3 sky = CrewSeats.GunDirection(pose, 0.3f, 1.5f, MathF.PI, min, max);
		Aim.Angles(pose.DirectionToLocal(sky), out _, out float up);
		Assert.Equal(max, up, 4);

		Vector3 ground = CrewSeats.GunDirection(pose, 0.3f, -1.5f, MathF.PI, min, max);
		Aim.Angles(pose.DirectionToLocal(ground), out _, out float down);
		Assert.Equal(min, down, 4);
	}

	// ---- poses ----------------------------------------------------------------------------

	[Fact]
	public void APosesForward_IsTheLookDirectionOfItsYawAndPitch()
	{
		for (float yaw = -3f; yaw <= 3f; yaw += 0.5f)
		{
			for (float pitch = -0.6f; pitch <= 0.6f; pitch += 0.3f)
			{
				Assert.Equal(Aim.Direction(yaw, pitch), new CrewPose(Vector3.Zero, yaw, pitch).Forward,
					new Vector3Comparer(1e-5f));
			}
		}
	}

	[Fact]
	public void APositiveRoll_LiftsTheRightSide_AndToLocalUndoesToWorld()
	{
		var pose = new CrewPose(new Vector3(3f, 1f, -2f), 1.1f, 0.2f, 0.3f);
		var level = new CrewPose(pose.Position, 1.1f, 0.2f, 0f);

		Assert.True(pose.ToWorld(Vector3.Right).Y > level.ToWorld(Vector3.Right).Y);

		var local = new Vector3(0.4f, -0.7f, 2.2f);
		Assert.Equal(local, pose.ToLocal(pose.ToWorld(local)), new Vector3Comparer(1e-5f));
	}
}
