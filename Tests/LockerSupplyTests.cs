using System;
using Gdpyr.Sim;
using Godot;
using Xunit;

namespace Gdpyr.Tests;

/// <summary>
/// Where a weapon locker reaches (docs/LOADOUT.md §4.4, L-D7, L-D8): a metre, flat,
/// from the cabinet's turned 1.2 × 0.5 m footprint — and what it gives there: a
/// pouch's rounds every half second, a grenade a second and thirty armour points a
/// second, up to the kit, and nothing for three seconds after a hit.
/// </summary>
public class LockerSupplyTests
{
	/// <summary>The Test map's locker: at (8, 0.5, 11), turned 180° so its racks face −Z, towards the spawn.</summary>
	private static readonly Vector3 TestLocker = new(8f, 0.5f, 11f);

	private const float Precision = 1e-4f;

	[Fact]
	public void TheFootprintIsTheCabinetUnderIt()
	{
		Assert.Equal(1.2f, LockerSupply.CabinetWidthMeters);
		Assert.Equal(0.5f, LockerSupply.CabinetDepthMeters);
	}

	[Theory]
	[InlineData(0f)]
	[InlineData(MathF.PI)]
	public void InsideTheCabinetIsNoDistanceAtAll(float yaw)
	{
		Assert.Equal(0f, LockerSupply.FootprintDistance(TestLocker, TestLocker, yaw));
		Assert.Equal(0f, LockerSupply.FootprintDistance(TestLocker + new Vector3(0.59f, 0f, 0.24f), TestLocker, yaw));
		Assert.Equal(0f, LockerSupply.FootprintDistance(TestLocker + new Vector3(-0.6f, 0f, -0.25f), TestLocker, yaw),
			Precision);
	}

	[Theory]
	[InlineData(0f)]
	[InlineData(MathF.PI)]
	public void TheTestMapsLockerIsAMetreFromItsFaceAndFromItsEnds(float yaw)
	{
		// Turned 180° the rectangle is the same rectangle: both turns measure alike.
		Assert.Equal(1f, LockerSupply.FootprintDistance(new Vector3(8f, 1f, 9.75f), TestLocker, yaw), Precision);
		Assert.Equal(1f, LockerSupply.FootprintDistance(new Vector3(8f, 1f, 12.25f), TestLocker, yaw), Precision);
		Assert.Equal(1f, LockerSupply.FootprintDistance(new Vector3(6.4f, 1f, 11f), TestLocker, yaw), Precision);
		Assert.Equal(1f, LockerSupply.FootprintDistance(new Vector3(9.6f, 1f, 11f), TestLocker, yaw), Precision);
	}

	[Fact]
	public void PointNineNineIsInAndOnePointOhOneIsOut()
	{
		float yaw = MathF.PI;
		float justIn = LockerSupply.FootprintDistance(new Vector3(8f, 1f, 10.75f - 0.99f), TestLocker, yaw);
		float justOut = LockerSupply.FootprintDistance(new Vector3(8f, 1f, 10.75f - 1.01f), TestLocker, yaw);

		Assert.True(LockerSupply.InReach(justIn));
		Assert.False(LockerSupply.InReach(justOut));

		float endIn = LockerSupply.FootprintDistance(new Vector3(8.6f + 0.99f, 1f, 11f), TestLocker, yaw);
		float endOut = LockerSupply.FootprintDistance(new Vector3(8.6f + 1.01f, 1f, 11f), TestLocker, yaw);
		Assert.True(LockerSupply.InReach(endIn));
		Assert.False(LockerSupply.InReach(endOut));
	}

	[Fact]
	public void TheScenariosSeatsStandWhereThePlanSaysTheyDo()
	{
		// locker_launcher_vs_tank's seat, 0.75 m from the face; where it stood before
		// L2, 1.25 m — outside the reach, inside the request's slack; and
		// locker_kit_refused's, 3 m out.
		float yaw = MathF.PI;
		Assert.Equal(0.75f, LockerSupply.FootprintDistance(new Vector3(8f, 1f, 10f), TestLocker, yaw), Precision);
		Assert.Equal(1.25f, LockerSupply.FootprintDistance(new Vector3(8f, 1f, 9.5f), TestLocker, yaw), Precision);
		Assert.Equal(3f, LockerSupply.FootprintDistance(new Vector3(8f, 1f, 7.75f), TestLocker, yaw), Precision);
	}

	[Fact]
	public void AMetreFromTheOriginIsNotAMetreFromTheCabinet()
	{
		// What measuring from the footprint is for: from the origin a metre reached
		// 0.75 m past the face and only 0.4 m past an end.
		float yaw = 0f;
		Assert.Equal(0.4f, LockerSupply.FootprintDistance(TestLocker + new Vector3(1f, 0f, 0f), TestLocker, yaw),
			Precision);
		Assert.Equal(0.75f, LockerSupply.FootprintDistance(TestLocker + new Vector3(0f, 0f, 1f), TestLocker, yaw),
			Precision);
	}

	[Fact]
	public void TheCornerIsMeasuredToTheCorner()
	{
		Vector3 feet = TestLocker + new Vector3(0.6f + 0.3f, 0f, 0.25f + 0.4f);
		Assert.Equal(0.5f, LockerSupply.FootprintDistance(feet, TestLocker, 0f), Precision);
	}

	[Fact]
	public void AQuarterTurnTurnsTheFootprint()
	{
		// Turned 90° the long side runs along world Z: a metre off the end in X is now
		// only 0.75 m off its face.
		float quarter = MathF.PI * 0.5f;
		Assert.Equal(0.75f, LockerSupply.FootprintDistance(TestLocker + new Vector3(1f, 0f, 0f), TestLocker, quarter),
			Precision);
		Assert.Equal(0.4f, LockerSupply.FootprintDistance(TestLocker + new Vector3(0f, 0f, 1f), TestLocker, quarter),
			Precision);
	}

	[Fact]
	public void HeightIsIgnored()
	{
		Assert.Equal(
			LockerSupply.FootprintDistance(new Vector3(8f, 0f, 9f), TestLocker, MathF.PI),
			LockerSupply.FootprintDistance(new Vector3(8f, 5f, 9f), TestLocker, MathF.PI));
	}

	[Fact]
	public void TheStandPointIsInFrontOfTheRacksAndInReach()
	{
		// The Test map's locker faces −Z, towards the spawn.
		Vector3 stand = LockerSupply.StandPoint(TestLocker, MathF.PI);
		Assert.Equal(8f, stand.X, Precision);
		Assert.Equal(11f - 0.25f - LockerSupply.StandOffMeters, stand.Z, Precision);
		Assert.Equal(TestLocker.Y, stand.Y);

		float distance = LockerSupply.FootprintDistance(stand, TestLocker, MathF.PI);
		Assert.Equal(LockerSupply.StandOffMeters, distance, Precision);
		Assert.True(LockerSupply.InReach(distance));

		// Unturned, it is on +Z.
		Assert.True(LockerSupply.StandPoint(TestLocker, 0f).Z > TestLocker.Z);
	}

	// ---- the floor ring ----------------------------------------------------

	[Theory]
	[InlineData(1f, 6)]
	[InlineData(0.9f, 6)]
	[InlineData(1.1f, 1)]
	public void TheRingIsDrawnWhereTheReachEnds(float offset, int segments)
	{
		Vector3[] outline = LockerSupply.ReachOutline(offset, segments);
		Assert.Equal(4 * (segments + 1), outline.Length);

		// Every point, and the middle of every chord between two that are a side apart,
		// is the offset from the footprint: the ring is the reach's edge, not a circle.
		for (int i = 0; i < outline.Length; i++)
		{
			Vector3 a = outline[i];
			Vector3 b = outline[(i + 1) % outline.Length];
			Assert.Equal(0f, a.Y);
			Assert.Equal(offset, LockerSupply.FootprintDistance(a, Vector3.Zero, 0f), 1e-4f);
			if (i % (segments + 1) == segments)
			{
				Assert.Equal(offset, LockerSupply.FootprintDistance((a + b) * 0.5f, Vector3.Zero, 0f), 1e-4f);
			}
		}
	}

	[Fact]
	public void TheRingAtTheReachIsOnTheEdgeOfIt()
	{
		foreach (Vector3 point in LockerSupply.ReachOutline(SimConfig.LockerReachMeters, 8))
		{
			Assert.True(LockerSupply.InReach(LockerSupply.FootprintDistance(point * 0.999f, Vector3.Zero, 0f)));
			Assert.False(LockerSupply.InReach(LockerSupply.FootprintDistance(point * 1.01f, Vector3.Zero, 0f)));
		}
	}

	// ---- the resupply ------------------------------------------------------

	private static readonly CarryStats Rifle = new(3.6f, 30, 0.5f);
	private static readonly CarryStats Launcher = new(6f, 2, 0.9f);

	private const byte RifleId = 3;
	private const byte LauncherId = 4;

	private static LoadoutSelection Kit(byte primary = RifleId, int magazines = 5, int grenades = 3,
		BodyArmourTier armour = BodyArmourTier.Medium) => new()
		{
			Large = primary,
			Magazines = (byte)magazines,
			Grenades = (byte)grenades,
			Armour = armour,
		};

	/// <summary>Steps a player standing at the locker from <paramref name="from"/> for <paramref name="ticks"/>, summing what it gave.</summary>
	private static (int Rounds, int Grenades, int Points) Stand(ref KitState kit, ref SupplyPacing pacing,
		in LoadoutSelection loadout, in CarryStats primary, uint from, int ticks, uint lastDamagedTick = 0)
	{
		int rounds = 0, grenades = 0, points = 0;
		for (uint t = from; t < from + ticks; t++)
		{
			SupplyGiven given = LockerSupply.Step(ref kit, ref pacing, loadout, primary, t, lastDamagedTick);
			rounds += given.Rounds;
			grenades += given.Grenades;
			points += given.ArmourPoints;
		}

		return (rounds, grenades, points);
	}

	[Fact]
	public void ThePaceIsThePlans()
	{
		Assert.Equal(30, LockerSupply.PouchIntervalTicks);
		Assert.Equal(60, LockerSupply.GrenadeIntervalTicks);
		Assert.Equal(30, LockerSupply.ArmourPerSecond);
		Assert.Equal(6, LockerSupply.ArmourIntervalTicks);
		Assert.Equal(3, LockerSupply.ArmourPerGrant);
		Assert.Equal(180, Resupply.DamageLockoutTicks);
	}

	[Fact]
	public void APouchComesAtOnceAndTheNextHalfASecondLater()
	{
		var kit = new KitState(60, 3, 60);
		var pacing = default(SupplyPacing);
		LoadoutSelection loadout = Kit();

		SupplyGiven first = LockerSupply.Step(ref kit, ref pacing, loadout, Rifle, 1000, 0);
		Assert.Equal(30, first.Rounds);
		Assert.Equal(90, kit.Reserve);

		for (uint t = 1001; t < 1030; t++)
		{
			Assert.Equal(0, LockerSupply.Step(ref kit, ref pacing, loadout, Rifle, t, 0).Rounds);
		}

		Assert.Equal(30, LockerSupply.Step(ref kit, ref pacing, loadout, Rifle, 1030, 0).Rounds);
		Assert.Equal(120, kit.Reserve);
	}

	[Fact]
	public void ThreeEmptyPouchesAreFullASecondAfterArriving()
	{
		var kit = new KitState(60, 3, 60);
		var pacing = default(SupplyPacing);
		var (rounds, _, _) = Stand(ref kit, ref pacing, Kit(), Rifle, 500, 61);
		Assert.Equal(90, rounds);
		Assert.Equal(150, kit.Reserve);

		// One tick short of that second, the third has not come.
		kit = new KitState(60, 3, 60);
		pacing = default;
		Assert.Equal(60, Stand(ref kit, ref pacing, Kit(), Rifle, 500, 60).Rounds);
	}

	[Fact]
	public void AGrenadeASecond()
	{
		var kit = new KitState(150, 0, 60);
		var pacing = default(SupplyPacing);
		Assert.Equal(1, Stand(ref kit, ref pacing, Kit(), Rifle, 200, 1).Grenades);
		Assert.Equal(0, Stand(ref kit, ref pacing, Kit(), Rifle, 201, 59).Grenades);
		Assert.Equal(2, Stand(ref kit, ref pacing, Kit(), Rifle, 260, 61).Grenades);
		Assert.Equal(3, kit.Grenades);
	}

	[Fact]
	public void ThirtyArmourPointsASecond()
	{
		var kit = new KitState(150, 3, 0);
		var pacing = default(SupplyPacing);
		Assert.Equal(30, Stand(ref kit, ref pacing, Kit(), Rifle, 0, 60).Points);

		// A medium vest from nothing is full on its twentieth handful, 114 ticks after
		// its first.
		kit = new KitState(150, 3, 0);
		pacing = default;
		Assert.Equal(57, Stand(ref kit, ref pacing, Kit(), Rifle, 0, 114).Points);
		Assert.Equal(60, Stand(ref kit, ref pacing, Kit(), Rifle, 114, 1).Points + 57);
		Assert.Equal(60, kit.ArmourPoints);
	}

	[Fact]
	public void SteppingOutAndBackDoesNotBuyAPouchSooner()
	{
		var kit = new KitState(0, 3, 60);
		var pacing = default(SupplyPacing);
		Assert.Equal(30, LockerSupply.Step(ref kit, ref pacing, Kit(), Rifle, 100, 0).Rounds);

		// Out of reach for a fifth of a second — nobody steps it — and back in.
		Assert.Equal(0, LockerSupply.Step(ref kit, ref pacing, Kit(), Rifle, 112, 0).Rounds);
		Assert.Equal(30, LockerSupply.Step(ref kit, ref pacing, Kit(), Rifle, 130, 0).Rounds);

		// Away longer than the pace, the next one is due the moment they are back.
		Assert.Equal(30, LockerSupply.Step(ref kit, ref pacing, Kit(), Rifle, 400, 0).Rounds);
	}

	[Fact]
	public void NothingForThreeSecondsAfterAHit()
	{
		var kit = new KitState(0, 0, 0);
		var pacing = default(SupplyPacing);
		const uint Hit = 1000;

		var during = Stand(ref kit, ref pacing, Kit(), Rifle, Hit, Resupply.DamageLockoutTicks, Hit);
		Assert.Equal((0, 0, 0), during);
		Assert.Equal(new KitState(0, 0, 0), kit);

		SupplyGiven after = LockerSupply.Step(ref kit, ref pacing, Kit(), Rifle,
			Hit + Resupply.DamageLockoutTicks, Hit);
		Assert.Equal(30, after.Rounds);
		Assert.Equal(1, after.Grenades);
		Assert.Equal(3, after.ArmourPoints);
	}

	[Fact]
	public void TheLockoutIsTheUnitsRule()
	{
		Assert.False(LockerSupply.IsLockedOut(500, 0));
		Assert.True(LockerSupply.IsLockedOut(500, 500));
		Assert.True(LockerSupply.IsLockedOut(500 + 179, 500));
		Assert.False(LockerSupply.IsLockedOut(500 + 180, 500));
	}

	[Fact]
	public void SinceHurtIsMinusOneForNever()
	{
		Assert.Equal(-1, LockerSupply.TicksSinceHurt(500, 0));
		Assert.Equal(0, LockerSupply.TicksSinceHurt(500, 500));
		Assert.Equal(180, LockerSupply.TicksSinceHurt(680, 500));
		Assert.Equal(-1, LockerSupply.TicksSinceHurt(400, 500));
	}

	[Fact]
	public void NeverPastTheKit()
	{
		// Ten rounds short of five magazines: the pouch tops it up by ten, not thirty.
		var kit = new KitState(140, 3, 55);
		var pacing = default(SupplyPacing);
		var given = Stand(ref kit, ref pacing, Kit(), Rifle, 0, 600);
		Assert.Equal((10, 0, 5), given);
		Assert.Equal(new KitState(150, 3, 60), kit);

		// A kit with two grenade pouches stops at two; one with three magazines at 90.
		kit = new KitState(0, 0, 0);
		pacing = default;
		Stand(ref kit, ref pacing, Kit(magazines: 3, grenades: 2), Rifle, 0, 600);
		Assert.Equal(new KitState(90, 2, 60), kit);
	}

	[Fact]
	public void NeverTakesAnythingAway()
	{
		// More than the kit holds — which a kit change trims, but the step does not
		// rely on — is left alone.
		var kit = new KitState(200, 3, 60);
		var pacing = default(SupplyPacing);
		Assert.Equal((0, 0, 0), Stand(ref kit, ref pacing, Kit(), Rifle, 0, 120));
		Assert.Equal(new KitState(200, 3, 60), kit);
	}

	[Fact]
	public void NothingForNoVestOrNoPrimary()
	{
		var kit = new KitState(0, 3, 0);
		var pacing = default(SupplyPacing);
		LoadoutSelection bare = Kit(Loadout.NoPrimary, magazines: 0, armour: BodyArmourTier.None);
		Assert.Equal((0, 0, 0), Stand(ref kit, ref pacing, bare, default, 0, 600));
		Assert.False(LockerSupply.NeedsAnything(kit, bare, default));

		// A rifle with no magazine pouches has no reserve to fill either.
		kit = new KitState(0, 3, 0);
		pacing = default;
		Assert.Equal((0, 0, 0), Stand(ref kit, ref pacing, Kit(magazines: 0, armour: BodyArmourTier.None), Rifle, 0, 600));
	}

	[Fact]
	public void AWeaponThatReloadsForeverIsLeftAlone()
	{
		var kit = new KitState(-1, 3, 60);
		var pacing = default(SupplyPacing);
		Assert.Equal((0, 0, 0), Stand(ref kit, ref pacing, Kit(), Rifle, 0, 120));
		Assert.Equal(-1, kit.Reserve);
		Assert.False(LockerSupply.NeedsAnything(kit, Kit(), Rifle));
	}

	[Fact]
	public void ALaunchersPouchIsTwoRounds()
	{
		var kit = new KitState(0, 3, 60);
		var pacing = default(SupplyPacing);
		LoadoutSelection loadout = Kit(LauncherId, magazines: 3);
		Assert.Equal(2, LockerSupply.Step(ref kit, ref pacing, loadout, Launcher, 0, 0).Rounds);
		Assert.Equal(6, Stand(ref kit, ref pacing, loadout, Launcher, 1, 600).Rounds + 2);
		Assert.Equal(6, kit.Reserve);
	}

	[Fact]
	public void TheCeilingIsTheKitsAndNeedsAnythingReadsIt()
	{
		Assert.Equal(new KitState(150, 3, 60), LockerSupply.Ceiling(Kit(), Rifle));
		Assert.Equal(new KitState(0, 0, 100),
			LockerSupply.Ceiling(Kit(Loadout.NoPrimary, 0, 0, BodyArmourTier.Heavy), default));

		Assert.False(LockerSupply.NeedsAnything(new KitState(150, 3, 60), Kit(), Rifle));
		Assert.True(LockerSupply.NeedsAnything(new KitState(149, 3, 60), Kit(), Rifle));
		Assert.True(LockerSupply.NeedsAnything(new KitState(150, 2, 60), Kit(), Rifle));
		Assert.True(LockerSupply.NeedsAnything(new KitState(150, 3, 59), Kit(), Rifle));
	}

	[Fact]
	public void TheStepIsAPureFunctionOfItsArguments()
	{
		var a = new KitState(30, 1, 10);
		var b = a;
		var pa = default(SupplyPacing);
		var pb = default(SupplyPacing);
		for (uint t = 0; t < 300; t++)
		{
			SupplyGiven ga = LockerSupply.Step(ref a, ref pa, Kit(), Rifle, t, 0);
			SupplyGiven gb = LockerSupply.Step(ref b, ref pb, Kit(), Rifle, t, 0);
			Assert.Equal((ga.Rounds, ga.Grenades, ga.ArmourPoints), (gb.Rounds, gb.Grenades, gb.ArmourPoints));
			Assert.Equal(ga.Any, ga.Rounds + ga.Grenades + ga.ArmourPoints > 0);
		}

		Assert.Equal(a, b);
		Assert.Equal(pa, pb);
	}
}
