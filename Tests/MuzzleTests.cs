using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Gdpyr.Sim;
using Godot;
using Xunit;

namespace Gdpyr.Tests;

/// <summary>
/// The engine-free half of muzzle effects (docs/MUZZLE_EFFECTS.md): what each kind of shot
/// draws, how far from the camera each part is drawn, where a body's muzzle is, which way a
/// case is thrown and how it falls and comes to rest, and how each part changes with age.
/// </summary>
public class MuzzleTests
{
	private const float Dt = 1f / 60f;

	public static IEnumerable<object[]> ShotKinds() => Muzzles.Drawn.Select(kind => new object[] { kind });

	// ---- the profiles (§3) ----------------------------------------------------------------

	[Theory]
	[MemberData(nameof(ShotKinds))]
	public void EveryKind_HasAFlashOfAFewFramesAndSmoke(ShotKind kind)
	{
		MuzzleProfile profile = Muzzles.Profile(kind);

		Assert.True(profile.HasFlash, $"{kind}");
		Assert.True(profile.HasSmoke, $"{kind}");
		Assert.InRange(profile.FlashSeconds, 2f / 60f, 0.1f);
		Assert.True(profile.FlashWidth > 0f && profile.LightEnergy > 0f && profile.LightRange > 0f, $"{kind}");
		Assert.True(profile.SmokeSeconds > profile.FlashSeconds, $"{kind}");
		Assert.InRange(profile.SmokeAlpha, 0.05f, 1f);
		Assert.True(profile.Reach >= 1f, $"{kind}");
	}

	[Fact]
	public void NothingFired_DrawsNothing()
	{
		Assert.True(Muzzles.Profile(ShotKind.None).IsNone);
		Assert.Equal(MuzzleParts.None, Muzzles.Profile(ShotKind.None).Parts);
		Assert.True(Muzzles.Profile((ShotKind)200).IsNone);
	}

	[Fact]
	public void Flashes_GrowWithTheGun()
	{
		float Length(ShotKind kind) => Muzzles.Profile(kind).FlashLength;

		Assert.True(Length(ShotKind.Pistol) < Length(ShotKind.Rifle));
		Assert.True(Length(ShotKind.Rifle) < Length(ShotKind.Dmr));
		Assert.True(Length(ShotKind.Dmr) < Length(ShotKind.HeavyMachineGun));
		Assert.True(Length(ShotKind.HeavyMachineGun) < Length(ShotKind.Cannon));
		Assert.True(Muzzles.Profile(ShotKind.Rifle).LightEnergy < Muzzles.Profile(ShotKind.Cannon).LightEnergy);
	}

	[Theory]
	[InlineData(ShotKind.Pistol, true)]
	[InlineData(ShotKind.Rifle, true)]
	[InlineData(ShotKind.Dmr, true)]
	[InlineData(ShotKind.MachineGun, true)]
	[InlineData(ShotKind.HeavyMachineGun, true)]
	[InlineData(ShotKind.Launcher, false)] // the 40 mm case stays in the launcher
	[InlineData(ShotKind.Cannon, false)]   // and the tank's in its turret
	[InlineData(ShotKind.Mortar, false)]   // a mortar round has none
	public void Cases_AreThrownByWhatEjectsThem(ShotKind kind, bool ejects)
	{
		MuzzleProfile profile = Muzzles.Profile(kind);
		Assert.Equal(ejects, profile.HasCasing);
		Assert.Equal(ejects, (profile.Parts & MuzzleParts.Casing) != 0);
		if (ejects)
		{
			Assert.True(profile.CasingDiameter < profile.CasingLength, $"{kind}");
			Assert.True(profile.EjectSpeed > 0f && profile.PortBackMeters > 0f, $"{kind}");
			Assert.InRange(profile.CasingSeconds, 1f, 10f);
		}
	}

	[Fact]
	public void Cases_AreTheRounds()
	{
		float Length(ShotKind kind) => Muzzles.Profile(kind).CasingLength;

		Assert.True(Length(ShotKind.Pistol) < Length(ShotKind.Rifle));
		Assert.Equal(Length(ShotKind.Rifle), Length(ShotKind.MachineGun)); // the same 5.56 mm round
		Assert.True(Length(ShotKind.Rifle) < Length(ShotKind.Dmr));
		Assert.True(Length(ShotKind.Dmr) < Length(ShotKind.HeavyMachineGun));
	}

	[Fact]
	public void BeltFedGuns_ThrowTheirCasesDown()
	{
		Assert.True(Muzzles.Profile(ShotKind.MachineGun).EjectUpSpeed < 0f);
		Assert.True(Muzzles.Profile(ShotKind.HeavyMachineGun).EjectUpSpeed < 0f);
		Assert.True(Muzzles.Profile(ShotKind.Rifle).EjectUpSpeed > 0f);
	}

	[Theory]
	[InlineData(ShotKind.Pistol, true)]
	[InlineData(ShotKind.Rifle, true)]
	[InlineData(ShotKind.Dmr, true)]
	[InlineData(ShotKind.MachineGun, true)]
	[InlineData(ShotKind.Launcher, true)]
	[InlineData(ShotKind.HeavyMachineGun, false)]
	[InlineData(ShotKind.Cannon, false)]
	[InlineData(ShotKind.Mortar, false)]
	public void HandHeldWeapons_AreHeldAheadOfTheEye(ShotKind kind, bool held)
	{
		MuzzleProfile profile = Muzzles.Profile(kind);
		Assert.Equal(held, profile.HandHeld);
		if (held)
		{
			Assert.InRange(profile.HoldReach, 0.3f, 1.2f);
			Assert.InRange(profile.HoldDrop, 0f, 0.4f);
			Assert.True(profile.PortBackMeters < profile.HoldReach, $"{kind}: the port is in front of the eye");
		}
	}

	[Fact]
	public void SlowBigGuns_AreDrawnFurtherThanSmallArms()
	{
		foreach (ShotKind kind in new[] { ShotKind.Pistol, ShotKind.Rifle, ShotKind.Dmr, ShotKind.MachineGun, ShotKind.Launcher })
		{
			Assert.Equal(1f, Muzzles.Profile(kind).Reach);
		}

		Assert.True(Muzzles.Profile(ShotKind.HeavyMachineGun).Reach > 1f);
		Assert.True(Muzzles.Profile(ShotKind.Mortar).Reach > Muzzles.Profile(ShotKind.HeavyMachineGun).Reach);
		Assert.True(Muzzles.Profile(ShotKind.Cannon).Reach > Muzzles.Profile(ShotKind.Mortar).Reach);
		Assert.Equal(1f, new MuzzleProfile { RangeScale = float.NaN }.Reach);
		Assert.Equal(1f, new MuzzleProfile { RangeScale = -2f }.Reach);
	}

	[Fact]
	public void AWeaponIsSeenFiring_ExactlyWhenItIsHeardFiring()
	{
		// Every weapon file's shot kind has something to draw, and none draws something unheard.
		int weapons = 0;
		for (int id = 0; ; id++)
		{
			string path;
			try
			{
				path = TrainedUnitResourceTests.WeaponPath(id);
			}
			catch (ArgumentOutOfRangeException)
			{
				break;
			}

			Dictionary<string, string> file = TrainedUnitResourceTests.Resource(path["res://".Length..]);
			var shot = (ShotKind)(file.TryGetValue("Shot", out string value) ? int.Parse(value, CultureInfo.InvariantCulture) : 0);
			Assert.True((shot != ShotKind.None) == !Muzzles.Profile(shot).IsNone, $"{path}: {shot}");
			weapons++;
		}

		Assert.Equal(17, weapons);
	}

	// ---- how far from the camera (§2) ----------------------------------------------------

	[Fact]
	public void DefaultRanges_AreAFewMetres_FlashFurthestCasesNearest()
	{
		MuzzleRanges ranges = MuzzleRanges.Default;
		Assert.True(ranges.CasingMeters > 0f);
		Assert.True(ranges.CasingMeters <= ranges.SmokeMeters);
		Assert.True(ranges.SmokeMeters <= ranges.FlashMeters);
		Assert.InRange(ranges.FlashMeters, 3f, 15f);
		Assert.Equal(ranges.FlashMeters, ranges.FurthestMeters);
	}

	[Fact]
	public void Within_IsEachPartToItsOwnRange_Inclusive()
	{
		var ranges = new MuzzleRanges(10f, 8f, 5f);

		Assert.Equal(MuzzleParts.All, ranges.Within(0f));
		Assert.Equal(MuzzleParts.All, ranges.Within(5f * 5f));
		Assert.Equal(MuzzleParts.Flash | MuzzleParts.Smoke, ranges.Within(5.01f * 5.01f));
		Assert.Equal(MuzzleParts.Flash | MuzzleParts.Smoke, ranges.Within(8f * 8f));
		Assert.Equal(MuzzleParts.Flash, ranges.Within(9f * 9f));
		Assert.Equal(MuzzleParts.Flash, ranges.Within(10f * 10f));
		Assert.Equal(MuzzleParts.None, ranges.Within(10.01f * 10.01f));
		Assert.Equal(MuzzleParts.None, ranges.Within(1e9f));
	}

	[Fact]
	public void Within_IsNothing_ForAnythingNotADistance()
	{
		var ranges = new MuzzleRanges(10f, 8f, 5f);

		Assert.Equal(MuzzleParts.None, ranges.Within(float.NaN));
		Assert.Equal(MuzzleParts.None, ranges.Within(-1f));
		Assert.Equal(MuzzleParts.None, ranges.Within(float.PositiveInfinity));
		Assert.Equal(MuzzleParts.None, ranges.Within(1f, 0f));
		Assert.Equal(MuzzleParts.None, ranges.Within(1f, float.NaN));
	}

	[Fact]
	public void Within_StretchesEveryRangeByTheKindsReach()
	{
		var ranges = new MuzzleRanges(10f, 8f, 5f);

		// A cannon 20 m off: inside 30 m of flash and 24 m of smoke, outside 15 m of cases.
		Assert.Equal(MuzzleParts.Flash | MuzzleParts.Smoke, ranges.Within(20f * 20f, 3f));
		Assert.Equal(MuzzleParts.None, ranges.Within(20f * 20f, 1f));
	}

	[Fact]
	public void Ranges_AreClampedToSomethingSensible()
	{
		var ranges = new MuzzleRanges(float.NaN, -3f, 1e6f);

		Assert.Equal(0f, ranges.FlashMeters);
		Assert.Equal(0f, ranges.SmokeMeters);
		Assert.Equal(MuzzleRanges.MaxMeters, ranges.CasingMeters);

		// A range of nothing draws nothing anyone else fires, even point blank.
		Assert.Equal(MuzzleParts.None, new MuzzleRanges(0f, 0f, 0f).Within(0.01f));
	}

	// ---- where the muzzle is (§2.1) ------------------------------------------------------

	[Fact]
	public void Frame_LookingLevel_IsTheCamerasOwn()
	{
		Muzzles.Frame(Vector3.Forward, out Vector3 forward, out Vector3 right, out Vector3 up);

		AssertNear(Vector3.Forward, forward);
		AssertNear(Vector3.Right, right);
		AssertNear(Vector3.Up, up);
	}

	[Theory]
	[InlineData(0f, 1f, 0f)]
	[InlineData(0f, -1f, 0f)]
	[InlineData(0f, 0f, 0f)]
	[InlineData(3f, 4f, -2f)]
	[InlineData(float.NaN, 1f, 0f)]
	public void Frame_IsOrthonormal_WhateverTheDirection(float x, float y, float z)
	{
		Muzzles.Frame(new Vector3(x, y, z), out Vector3 forward, out Vector3 right, out Vector3 up);

		Assert.Equal(1f, forward.Length(), 4);
		Assert.Equal(1f, right.Length(), 4);
		Assert.Equal(1f, up.Length(), 4);
		Assert.Equal(0f, forward.Dot(right), 4);
		Assert.Equal(0f, forward.Dot(up), 4);
		Assert.Equal(0f, right.Dot(up), 4);
		Assert.True(up.Y >= 0f || Mathf.Abs(forward.Y) > 0.99f, "up is up, unless firing straight up or down");
	}

	[Fact]
	public void HeldMuzzle_IsAheadBelowAndRightOfTheEye()
	{
		MuzzleProfile rifle = Muzzles.Profile(ShotKind.Rifle);
		var eye = new Vector3(2f, 1.5f, 3f);

		Vector3 muzzle = Muzzles.HeldMuzzle(eye, Vector3.Forward, rifle);

		AssertNear(eye + new Vector3(rifle.HoldSide, -rifle.HoldDrop, -rifle.HoldReach), muzzle);
	}

	[Fact]
	public void HeldMuzzle_TurnsWithTheAim()
	{
		MuzzleProfile rifle = Muzzles.Profile(ShotKind.Rifle);
		Vector3 eye = Vector3.Up * 1.5f;
		Vector3 up45 = new Vector3(0f, 1f, -1f).Normalized();

		Vector3 offset = Muzzles.HeldMuzzle(eye, up45, rifle) - eye;

		Assert.Equal(rifle.HoldReach, offset.Dot(up45), 4);
		Assert.Equal(
			MathF.Sqrt((rifle.HoldReach * rifle.HoldReach) + (rifle.HoldSide * rifle.HoldSide) + (rifle.HoldDrop * rifle.HoldDrop)),
			offset.Length(), 4);
	}

	[Fact]
	public void HeldMuzzle_OfAMountedGun_IsItsOwnMuzzle()
	{
		var origin = new Vector3(1f, 2f, 3f);
		Assert.Equal(origin, Muzzles.HeldMuzzle(origin, Vector3.Right, Muzzles.Profile(ShotKind.HeavyMachineGun)));
		Assert.Equal(origin, Muzzles.HeldMuzzle(origin, Vector3.Right, Muzzles.Profile(ShotKind.Cannon)));
	}

	[Fact]
	public void HullMuzzle_IsTheHullsEdge_OnTheLineOfFire()
	{
		var eye = new Vector3(0f, 1.9f, 0f);

		AssertNear(eye + new Vector3(1.3f, 0f, 0f), Muzzles.HullMuzzle(eye, new Vector3(5f, 0f, 0f), 1.3f));
		Assert.Equal(eye, Muzzles.HullMuzzle(eye, Vector3.Right, -1f));
		Assert.Equal(eye, Muzzles.HullMuzzle(eye, Vector3.Right, float.NaN));
	}

	[Fact]
	public void EjectionPort_IsBehindTheMuzzle_OnTheRight()
	{
		MuzzleProfile rifle = Muzzles.Profile(ShotKind.Rifle);
		Vector3 muzzle = Vector3.Zero;

		Vector3 port = Muzzles.EjectionPort(muzzle, Vector3.Forward, rifle);

		AssertNear(new Vector3(Muzzles.PortSideMeters, 0f, rifle.PortBackMeters), port);
	}

	[Fact]
	public void Cases_LeaveRightUpAndBack_AtTheProfilesSpeeds()
	{
		MuzzleProfile rifle = Muzzles.Profile(ShotKind.Rifle);

		// The middle of each jitter is the profile's own speed.
		Vector3 v = Muzzles.EjectVelocity(Vector3.Forward, rifle, Vector3.Zero, 0.5f, 0.5f, 0.5f);

		Assert.Equal(rifle.EjectSpeed, v.X, 4);
		Assert.Equal(rifle.EjectUpSpeed, v.Y, 4);
		Assert.Equal(rifle.EjectBackSpeed, v.Z, 4);
	}

	[Fact]
	public void Cases_VaryWithinAFifth_AndCarryTheShootersVelocity()
	{
		MuzzleProfile rifle = Muzzles.Profile(ShotKind.Rifle);

		Vector3 slow = Muzzles.EjectVelocity(Vector3.Forward, rifle, Vector3.Zero, 0f, 0f, 0f);
		Vector3 fast = Muzzles.EjectVelocity(Vector3.Forward, rifle, Vector3.Zero, 1f, 1f, 1f);
		Vector3 running = Muzzles.EjectVelocity(Vector3.Forward, rifle, new Vector3(0f, 0f, -5f), 0.5f, 0.5f, 0.5f);
		Vector3 broken = Muzzles.EjectVelocity(Vector3.Forward, rifle, new Vector3(float.NaN, 0f, 0f), 0.5f, 0.5f, 0.5f);
		Vector3 still = Muzzles.EjectVelocity(Vector3.Forward, rifle, Vector3.Zero, 0.5f, 0.5f, 0.5f);

		Assert.Equal(rifle.EjectSpeed * 0.8f, slow.X, 4);
		Assert.Equal(rifle.EjectSpeed * 1.2f, fast.X, 4);
		AssertNear(still + new Vector3(0f, 0f, -5f), running);
		AssertNear(still, broken);
	}

	// ---- the viewmodel's barrel (§2.2) ---------------------------------------------------

	[Fact]
	public void BarrelEnd_OfAGreyboxRifle_IsTheFrontFace_ThreeQuartersUp()
	{
		// rifle.tres: a 0.10 × 0.12 × 0.8 box, held unturned, looking down −Z.
		var size = new Vector3(0.1f, 0.12f, 0.8f);
		Vector3 position = -size * 0.5f;

		Vector3 end = Muzzles.BarrelEnd(position, size, Vector3.Forward, Vector3.Up);

		AssertNear(new Vector3(0f, -0.06f + (0.12f * Muzzles.BoreHeight), -0.4f), end);
	}

	[Fact]
	public void BarrelEnd_OfATurnedMesh_FollowsTheCamerasForwardIntoIt()
	{
		// pistol.tres turns its mesh −90° about Y, so the camera's forward is the mesh's −X.
		var position = new Vector3(-0.104955f, -0.069936f, -0.014302f);
		var size = new Vector3(0.212628f, 0.14096f, 0.03324f);

		Vector3 end = Muzzles.BarrelEnd(position, size, Vector3.Left, Vector3.Up);

		Assert.Equal(position.X, end.X, 5);
		Assert.Equal(position.Y + (size.Y * Muzzles.BoreHeight), end.Y, 5);
		Assert.Equal(position.Z + (size.Z * 0.5f), end.Z, 5);
	}

	[Fact]
	public void BarrelEnd_WithNoUsableUp_IsTheMiddleOfTheFace()
	{
		var size = new Vector3(0.2f, 0.2f, 1f);
		Vector3 position = -size * 0.5f;

		// Up along the barrel's own axis says nothing about which way is up across it.
		AssertNear(new Vector3(0f, 0f, -0.5f), Muzzles.BarrelEnd(position, size, Vector3.Forward, Vector3.Forward));

		// Upside down, the bore is towards the mesh's −Y.
		AssertNear(new Vector3(0f, -0.05f, -0.5f), Muzzles.BarrelEnd(position, size, Vector3.Forward, Vector3.Down));
	}

	// ---- a case's flight -----------------------------------------------------------------

	[Fact]
	public void ACase_FallsUnderGravity_WithNoFloor()
	{
		var casing = new CasingBody { Position = Vector3.Zero };

		for (int i = 0; i < 60; i++)
		{
			Muzzles.StepCasing(ref casing, Dt, hasFloor: false, floorY: 0f, diameter: 0.01f);
		}

		Assert.Equal(-0.5f * Muzzles.Gravity, casing.Position.Y, 0); // within a few centimetres of a second's fall
		Assert.Equal(-Muzzles.Gravity, casing.Velocity.Y, 3);
		Assert.False(casing.Resting);
		Assert.Equal(1f, casing.Age, 4);
	}

	[Fact]
	public void ACase_BouncesLower_AndComesToRestOnItsSide_OnTheFloor()
	{
		const float Floor = 0.5f;
		const float Diameter = 0.0096f;
		var casing = new CasingBody
		{
			Position = new Vector3(0f, Floor + 1.4f, 0f),
			Velocity = new Vector3(2.8f, 1.2f, 0.4f),
			Spin = new Vector3(20f, 5f, -20f),
		};

		float highestAfterFirstBounce = float.NegativeInfinity;
		for (int i = 0; i < 3 * 60 && !casing.Resting; i++)
		{
			Muzzles.StepCasing(ref casing, Dt, hasFloor: true, Floor, Diameter);
			Assert.True(casing.Position.Y >= Floor + (Diameter * 0.5f) - 1e-5f, "never through the floor");
			if (casing.Bounces >= 1)
			{
				highestAfterFirstBounce = MathF.Max(highestAfterFirstBounce, casing.Position.Y);
			}
		}

		Assert.True(casing.Resting, "at rest within three seconds");
		Assert.InRange(casing.Bounces, 1, Muzzles.MaxBounces);
		Assert.True(highestAfterFirstBounce < Floor + 1.4f, "a bounce is lower than the drop");
		Assert.Equal(Floor + (Diameter * 0.5f), casing.Position.Y, 5);
		Assert.Equal(Vector3.Zero, casing.Velocity);
		Assert.Equal(Vector3.Zero, casing.Spin);
		Assert.Equal(MathF.PI * 0.5f, casing.Rotation.X, 5);
		Assert.Equal(0f, casing.Rotation.Z, 5);

		// And stays there.
		Vector3 at = casing.Position;
		float age = casing.Age;
		Muzzles.StepCasing(ref casing, 1f, hasFloor: true, Floor, Diameter);
		Assert.Equal(at, casing.Position);
		Assert.Equal(age + 1f, casing.Age, 4);
	}

	[Fact]
	public void ABounce_KeepsTheRestitutionOfItsSpeedIntoTheFloor_AndTheFrictionOfItsSpeedAlongIt()
	{
		var casing = new CasingBody
		{
			Position = new Vector3(0f, 0.001f, 0f),
			Velocity = new Vector3(2f, -3f, 1f),
		};

		Muzzles.StepCasing(ref casing, Dt, hasFloor: true, floorY: 0f, diameter: 0f);
		float into = 3f + (Muzzles.Gravity * Dt);

		Assert.Equal(1, casing.Bounces);
		Assert.Equal(into * Muzzles.Restitution, casing.Velocity.Y, 4);
		Assert.Equal(2f * Muzzles.Friction, casing.Velocity.X, 4);
		Assert.Equal(1f * Muzzles.Friction, casing.Velocity.Z, 4);
	}

	[Fact]
	public void ACase_ThatLandsSoftly_StopsWhereItLands()
	{
		var casing = new CasingBody { Position = new Vector3(0f, 0.001f, 0f), Velocity = new Vector3(1f, -0.1f, 0f) };

		Muzzles.StepCasing(ref casing, Dt, hasFloor: true, floorY: 0f, diameter: 0f);

		Assert.True(casing.Resting);
		Assert.Equal(0, casing.Bounces);
	}

	[Fact]
	public void ACase_StopsBouncing_AfterItsLastBounce()
	{
		var casing = new CasingBody { Position = new Vector3(0f, 0.001f, 0f), Velocity = new Vector3(0f, -20f, 0f), Bounces = Muzzles.MaxBounces };

		Muzzles.StepCasing(ref casing, Dt, hasFloor: true, floorY: 0f, diameter: 0f);

		Assert.True(casing.Resting);
	}

	[Theory]
	[InlineData(0f)]
	[InlineData(-1f)]
	[InlineData(float.NaN)]
	public void NoTime_MovesNothing(float dt)
	{
		var casing = new CasingBody { Position = Vector3.One, Velocity = Vector3.One };

		Muzzles.StepCasing(ref casing, dt, hasFloor: true, floorY: 0f, diameter: 0.01f);

		Assert.Equal(Vector3.One, casing.Position);
		Assert.Equal(0f, casing.Age);
	}

	// ---- how each part changes with age (§3) ---------------------------------------------

	[Fact]
	public void AFlash_IsWholeAtTheShot_AndGoneAtItsEnd_ItsLightFirst()
	{
		const float Seconds = 0.05f;

		Assert.Equal(1f, Muzzles.FlashScale(0f, Seconds), 5);
		Assert.Equal(1f, Muzzles.FlashLight(0f, Seconds), 5);
		Assert.Equal(0f, Muzzles.FlashScale(Seconds, Seconds), 5);
		Assert.Equal(0f, Muzzles.FlashLight(Seconds, Seconds), 5);
		Assert.Equal(0f, Muzzles.FlashScale(-0.01f, Seconds));
		Assert.True(Muzzles.FlashLight(Seconds * 0.5f, Seconds) < Muzzles.FlashScale(Seconds * 0.5f, Seconds));

		float last = float.PositiveInfinity;
		for (float t = 0f; t <= Seconds; t += Seconds / 10f)
		{
			float scale = Muzzles.FlashScale(t, Seconds);
			Assert.True(scale <= last);
			last = scale;
		}
	}

	[Fact]
	public void APuff_Grows_ThickensQuickly_AndThinsOutToNothing()
	{
		const float Seconds = 1f;

		Assert.Equal(1f, Muzzles.SmokeScale(0f, Seconds), 5);
		Assert.Equal(Muzzles.SmokeGrowth, Muzzles.SmokeScale(Seconds, Seconds), 5);
		Assert.True(Muzzles.SmokeScale(0.25f, Seconds) > Muzzles.SmokeScale(0.1f, Seconds));

		Assert.Equal(0f, Muzzles.SmokeOpacity(0f, Seconds));
		Assert.True(Muzzles.SmokeOpacity(Muzzles.SmokeFadeInSeconds, Seconds) > 0.9f);
		Assert.True(Muzzles.SmokeOpacity(0.5f, Seconds) < Muzzles.SmokeOpacity(0.1f, Seconds));
		Assert.Equal(0f, Muzzles.SmokeOpacity(Seconds, Seconds));
		Assert.Equal(0f, Muzzles.SmokeOpacity(-1f, Seconds));
		for (float t = 0f; t < Seconds; t += 0.01f)
		{
			Assert.InRange(Muzzles.SmokeOpacity(t, Seconds), 0f, 1f);
		}
	}

	[Fact]
	public void APuff_LeavesDownTheBarrel_SlowsInTheAir_AndClimbs()
	{
		Assert.Equal(Vector3.Zero, Muzzles.SmokeOffset(Vector3.Forward, 1f, 0.3f, 0f));

		Vector3 early = Muzzles.SmokeOffset(Vector3.Forward, 1f, 0.3f, 0.1f);
		Vector3 late = Muzzles.SmokeOffset(Vector3.Forward, 1f, 0.3f, 2f);

		Assert.True(-early.Z > 0.05f);
		Assert.True(-late.Z <= 1f * Muzzles.SmokeDragSeconds);
		Assert.True(-late.Z > 0.99f * Muzzles.SmokeDragSeconds);
		Assert.Equal(0.3f * 2f, late.Y, 5);
	}

	[Fact]
	public void ACase_IsWhole_UntilItShrinksAwayAtTheEnd()
	{
		Assert.Equal(1f, Muzzles.CasingScale(0f, 4f));
		Assert.Equal(1f, Muzzles.CasingScale(3.7f, 4f));
		Assert.True(Muzzles.CasingScale(3.9f, 4f) is > 0f and < 1f);
		Assert.Equal(0f, Muzzles.CasingScale(4f, 4f));
	}

	private static void AssertNear(Vector3 expected, Vector3 actual, float tolerance = 1e-4f)
	{
		Assert.True(expected.DistanceTo(actual) <= tolerance, $"expected {expected}, got {actual}");
	}
}
