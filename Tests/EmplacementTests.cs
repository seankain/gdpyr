using Gdpyr.Sim;
using Godot;
using Xunit;

namespace Gdpyr.Tests;

/// <summary>
/// What a press of the use key means, what carrying something costs, and where a
/// mounted gun will point (docs/IMPLEMENTATION_PLAN.md §M5).
///
/// Engine-free because both ends of the prediction run it: the server decides what
/// the press meant, and the owning client predicts the walk and the tracer that
/// follow from it.
/// </summary>
public class EmplacementTests
{
	private static UseSituation Situation(UseIntent intent = UseIntent.Tap,
		CarryKind carrying = CarryKind.None, bool mounted = false, bool gun = false, bool can = false,
		bool onGround = true, bool locker = false) =>
		new(intent, carrying, mounted, gun, can, onGround, locker);

	/// <summary>What a weapon locker holds, in the catalog's order: DMR, rifle, launcher.</summary>
	private static readonly byte[] LargeWeapons = { 2, 3, 4 };

	// ---- the use key -------------------------------------------------------

	[Fact]
	public void NoPressIsNoAction()
	{
		Assert.Equal(UseAction.None, EmplacementSim.Resolve(Situation(UseIntent.None, gun: true)));
	}

	[Fact]
	public void ATapNearADeployedGunMountsItAndAHoldTakesIt()
	{
		Assert.Equal(UseAction.Mount, EmplacementSim.Resolve(Situation(UseIntent.Tap, gun: true)));
		Assert.Equal(UseAction.PickUpGun, EmplacementSim.Resolve(Situation(UseIntent.Hold, gun: true)));
	}

	[Fact]
	public void BehindAGunATapGetsOffItAndAHoldTakesItWithYou()
	{
		Assert.Equal(UseAction.Dismount,
			EmplacementSim.Resolve(Situation(UseIntent.Tap, mounted: true, gun: true)));

		// The one way to move a gun somebody has already set up, including your own.
		Assert.Equal(UseAction.PickUpGun,
			EmplacementSim.Resolve(Situation(UseIntent.Hold, mounted: true, gun: true)));
	}

	[Fact]
	public void ACarriedGunGoesDownOnEitherPress()
	{
		Assert.Equal(UseAction.Deploy,
			EmplacementSim.Resolve(Situation(UseIntent.Tap, CarryKind.Gun)));
		Assert.Equal(UseAction.Deploy,
			EmplacementSim.Resolve(Situation(UseIntent.Hold, CarryKind.Gun)));
	}

	[Fact]
	public void NothingIsPutDownInMidAir()
	{
		Assert.Equal(UseAction.None,
			EmplacementSim.Resolve(Situation(UseIntent.Tap, CarryKind.Gun, onGround: false)));
		Assert.Equal(UseAction.None,
			EmplacementSim.Resolve(Situation(UseIntent.Tap, CarryKind.AmmoCan, onGround: false)));
	}

	[Fact]
	public void ACanIsLoadedIntoAGunInReachAndOtherwisePutDown()
	{
		Assert.Equal(UseAction.Resupply,
			EmplacementSim.Resolve(Situation(UseIntent.Tap, CarryKind.AmmoCan, gun: true)));

		Assert.Equal(UseAction.DropCan,
			EmplacementSim.Resolve(Situation(UseIntent.Tap, CarryKind.AmmoCan)));

		// Holding it puts the can down even next to a gun, so a can can be stashed.
		Assert.Equal(UseAction.DropCan,
			EmplacementSim.Resolve(Situation(UseIntent.Hold, CarryKind.AmmoCan, gun: true)));
	}

	[Fact]
	public void ACanUnderfootIsPickedUpBeforeAGunIsMounted()
	{
		// The caller only sets CanInReach when the can is the nearer of the two, so
		// this is the tie-break arriving already made.
		Assert.Equal(UseAction.PickUpCan,
			EmplacementSim.Resolve(Situation(UseIntent.Tap, gun: true, can: true)));
	}

	// ---- the weapon locker (docs/NETCODE.md §10.6) ------------------------

	[Fact]
	public void ATapAtALockerSwapsTheLargeWeapon()
	{
		Assert.Equal(UseAction.SwapWeapon, EmplacementSim.Resolve(Situation(UseIntent.Tap, locker: true)));
	}

	[Fact]
	public void AHoldAtALockerDoesNothing()
	{
		// A hold is what carries things off, and there is nothing at a locker to carry.
		Assert.Equal(UseAction.None, EmplacementSim.Resolve(Situation(UseIntent.Hold, locker: true)));
	}

	[Fact]
	public void ALockerIsUsedBeforeAGunOrACanItIsNearerThan()
	{
		// The caller only sets LockerInReach when the locker is the nearest thing in
		// reach, so this is that tie-break arriving already made.
		Assert.Equal(UseAction.SwapWeapon,
			EmplacementSim.Resolve(Situation(UseIntent.Tap, gun: true, can: true, locker: true)));
	}

	[Fact]
	public void NothingIsSwappedWithSomethingOnYourBackOrYourHandsOnAGun()
	{
		// Whatever is being carried is what the key is about, wherever it is pressed:
		// a can is put down beside the locker rather than kept while a weapon is taken.
		Assert.Equal(UseAction.DropCan,
			EmplacementSim.Resolve(Situation(UseIntent.Tap, CarryKind.AmmoCan, locker: true)));
		Assert.Equal(UseAction.Deploy,
			EmplacementSim.Resolve(Situation(UseIntent.Tap, CarryKind.Gun, locker: true)));
		Assert.Equal(UseAction.Dismount,
			EmplacementSim.Resolve(Situation(UseIntent.Tap, mounted: true, locker: true)));
	}

	[Theory]
	[InlineData(3, 4)] // rifle -> launcher: the answer to armour is one tap away
	[InlineData(4, 2)] // launcher -> DMR
	[InlineData(2, 3)] // DMR -> rifle, and round again
	public void ALockerHandsOverTheNextLargeWeapon(byte current, byte next)
	{
		Assert.Equal(next, LockerSim.Next(current, LargeWeapons));
	}

	[Fact]
	public void ThreeTapsAtALockerAreWhereYouStarted()
	{
		byte weapon = 3;
		for (int i = 0; i < LargeWeapons.Length; i++)
		{
			weapon = LockerSim.Next(weapon, LargeWeapons);
		}

		Assert.Equal(3, weapon);
	}

	[Fact]
	public void ALockerNeverHandsOverSomethingItDoesNotHold()
	{
		// A hammer or a pistol id in the large slot is not something a sanitized
		// loadout holds, but a locker answers with a large weapon regardless.
		Assert.Equal(2, LockerSim.Next(0, LargeWeapons));
		Assert.Equal(2, LockerSim.Next(200, LargeWeapons));
		Assert.Equal(7, LockerSim.Next(7, System.ReadOnlySpan<byte>.Empty));
	}

	[Fact]
	public void ALargeWeaponIsFoundByItsPlaceInTheLocker()
	{
		Assert.Equal(0, LockerSim.IndexOf(2, LargeWeapons));
		Assert.Equal(2, LockerSim.IndexOf(4, LargeWeapons));
		Assert.Equal(-1, LockerSim.IndexOf(1, LargeWeapons));
	}

	[Fact]
	public void EmptyHandsAndNothingInReachIsNothing()
	{
		Assert.Equal(UseAction.None, EmplacementSim.Resolve(Situation(UseIntent.Tap)));
		Assert.Equal(UseAction.None, EmplacementSim.Resolve(Situation(UseIntent.Hold)));
	}

	// ---- tap against hold --------------------------------------------------

	private static InputContext Frame(bool use, bool wasUse) =>
		new(InputFrame.Create(0, 0f, 0f, 0f, 0f, use ? InputButtons.Use : InputButtons.None),
			(ushort)(wasUse ? InputButtons.Use : InputButtons.None), SimConfig.TickDelta);

	[Fact]
	public void AReleaseInsideTheWindowIsATap()
	{
		var tracker = new UseTracker();

		for (int i = 0; i < UseTracker.HoldTicks - 1; i++)
		{
			Assert.Equal(UseIntent.None, tracker.Sample(Frame(use: true, wasUse: i > 0)));
		}

		Assert.Equal(UseIntent.Tap, tracker.Sample(Frame(use: false, wasUse: true)));
	}

	[Fact]
	public void HoldingPastTheWindowFiresOnceOnTheTickItCrosses()
	{
		var tracker = new UseTracker();
		UseIntent seen = UseIntent.None;

		for (int i = 0; i < UseTracker.HoldTicks; i++)
		{
			UseIntent intent = tracker.Sample(Frame(use: true, wasUse: i > 0));
			if (intent != UseIntent.None)
			{
				Assert.Equal(UseIntent.None, seen);
				seen = intent;
			}
		}

		Assert.Equal(UseIntent.Hold, seen);

		// Keeping it down does not fire again, and letting go is not also a tap.
		Assert.Equal(UseIntent.None, tracker.Sample(Frame(use: true, wasUse: true)));
		Assert.Equal(UseIntent.None, tracker.Sample(Frame(use: false, wasUse: true)));
	}

	[Fact]
	public void AKeyThatWasNeverDownIsNeverAPress()
	{
		var tracker = new UseTracker();

		Assert.Equal(UseIntent.None, tracker.Sample(Frame(use: false, wasUse: false)));
		Assert.Equal(0f, tracker.HoldProgress);
	}

	[Fact]
	public void TheTrackerIsAFunctionOfTheFramesAlone()
	{
		// Two trackers fed the same frames agree, whatever happened to the player in
		// between: nothing resets this by hand, which is what lets the server run one
		// per peer without having to remember to.
		var a = new UseTracker();
		var b = new UseTracker();

		for (int i = 0; i < UseTracker.HoldTicks + 4; i++)
		{
			bool down = i % 7 < 3;
			bool was = i > 0 && (i - 1) % 7 < 3;
			Assert.Equal(a.Sample(Frame(down, was)), b.Sample(Frame(down, was)));
		}

		Assert.Equal(a.HeldTicks, b.HeldTicks);
	}

	// ---- carrying and traversing -------------------------------------------

	[Fact]
	public void CarryingCostsSpeedAndCarryingAGunCostsMore()
	{
		Assert.Equal(1f, EmplacementSim.MoveScale(CarryKind.None));
		Assert.True(EmplacementSim.MoveScale(CarryKind.Gun) < EmplacementSim.MoveScale(CarryKind.AmmoCan));
		Assert.True(EmplacementSim.MoveScale(CarryKind.AmmoCan) < 1f);
	}

	[Fact]
	public void AMountedGunPointsWhereItIsAimedInsideItsArc()
	{
		Vector3 straight = EmplacementSim.Traverse(0f, 0f, 0f);

		Assert.Equal(Aim.Direction(0f, 0f), straight);
	}

	[Fact]
	public void AMountedGunWillNotTurnPastItsArc()
	{
		const float deployYaw = 1f;
		float wanted = deployYaw + Mathf.Pi; // straight behind it

		Vector3 direction = EmplacementSim.Traverse(wanted, 0f, deployYaw);
		Aim.Angles(direction, out float yaw, out _);

		Assert.Equal(SimConfig.EmplacementTraverseRadians,
			Mathf.Abs(Mathf.AngleDifference(deployYaw, yaw)), 3);
	}

	[Fact]
	public void AMountedGunWillNotPointAtTheSky()
	{
		Vector3 up = EmplacementSim.Traverse(0f, Mathf.Pi / 2f, 0f);
		Aim.Angles(up, out _, out float pitch);

		Assert.Equal(SimConfig.EmplacementElevationRadians, pitch, 3);
	}

	// ---- ammunition --------------------------------------------------------

	[Fact]
	public void ACanFillsTheBeltAndWastesTheRest()
	{
		Assert.Equal(60, EmplacementSim.Resupply(20, 60, 40));

		// Walked out to a gun that was nearly full: the rest of the can is gone.
		Assert.Equal(60, EmplacementSim.Resupply(50, 60, 40));
		Assert.Equal(40, EmplacementSim.Resupply(0, 60, 40));
	}

	[Fact]
	public void AFullBeltIsNotWorthACan()
	{
		Assert.False(EmplacementSim.NeedsAmmo(60, 60));
		Assert.True(EmplacementSim.NeedsAmmo(59, 60));
		Assert.True(EmplacementSim.NeedsAmmo(0, 60));
	}
}
