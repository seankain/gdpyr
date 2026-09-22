using System;
using Gdpyr.Sim;
using Godot;
using Xunit;

namespace Gdpyr.Tests;

/// <summary>
/// A barracks' own guns and mortars (docs/NETCODE.md §10.4): what they will engage,
/// how fast a gun comes round, when either may fire, where a bot waits for them to
/// stop, and where a mortar has to point for its shell to come down on somebody.
/// </summary>
public class DefenseTests
{
	/// <summary>Weapons/projectiles/mortar_81.tres, which the engine loads and this project cannot.</summary>
	private static readonly ProjectileStats Shell = new(
		ProjectileParams.FromGramsAndMillimeters(4200f, 40.5f, 0.30f),
		muzzleVelocity: 34f,
		damage: 100f,
		explosionRadiusMeters: 8f,
		explosionDamage: 120f,
		lifetimeSeconds: 14f,
		sweepRadiusMeters: 0.1f);

	private static DefenseTraits Gun(float range = 100f, int reactionTicks = 30) =>
		new(DefenseKind.Gun, range, 0f, reactionTicks, Mathf.DegToRad(90f) / SimConfig.TickRate,
			Mathf.DegToRad(2f));

	private static DefenseTraits Mortar(float range = 100f, float floor = 15f) =>
		new(DefenseKind.Mortar, range, floor, 90, 0f, 0f);

	// ---- who is a target ---------------------------------------------------

	[Fact]
	public void AGunNeedsToSeeWhatItShoots()
	{
		Assert.True(DefenseSim.CanEngage(Gun(), 60f, lineOfSight: true));
		Assert.False(DefenseSim.CanEngage(Gun(), 60f, lineOfSight: false));
	}

	[Fact]
	public void AMortarDoesNot()
	{
		// The whole reason to have one: cover beside the door stops the gun and not this.
		Assert.True(DefenseSim.CanEngage(Mortar(), 60f, lineOfSight: false));
	}

	[Fact]
	public void NothingIsEngagedPastItsRange()
	{
		Assert.True(DefenseSim.CanEngage(Gun(), 100f, true));
		Assert.False(DefenseSim.CanEngage(Gun(), 100.5f, true));
		Assert.False(DefenseSim.CanEngage(Mortar(), 100.5f, false));
	}

	[Fact]
	public void AMortarCannotDropAShellAtItsOwnFeet()
	{
		Assert.False(DefenseSim.CanEngage(Mortar(floor: 15f), 10f, false));
		Assert.True(DefenseSim.CanEngage(Mortar(floor: 15f), 15f, false));
		Assert.False(DefenseSim.ShouldKeep(Mortar(floor: 15f), 10f, false));
	}

	[Fact]
	public void ATargetIsHeldALittlePastTheRingButNotForever()
	{
		// Hysteresis: somebody walking the edge must not flicker in and out.
		Assert.False(DefenseSim.CanEngage(Gun(), 105f, true));
		Assert.True(DefenseSim.ShouldKeep(Gun(), 105f, true));
		Assert.False(DefenseSim.ShouldKeep(Gun(), 100f * DefenseSim.KeepRangeScale + 0.1f, true));
	}

	[Fact]
	public void AGunDropsSomebodyWhoHasGoneBehindAWall()
	{
		Assert.False(DefenseSim.ShouldKeep(Gun(), 50f, lineOfSight: false));
		Assert.True(DefenseSim.ShouldKeep(Mortar(), 50f, lineOfSight: false));
	}

	[Fact]
	public void TheFloorIsNeverAboveTheRange()
	{
		var traits = new DefenseTraits(DefenseKind.Mortar, 30f, 50f, 0, 0f, 0f);
		Assert.Equal(30f, traits.MinRangeMeters);
	}

	// ---- turning -----------------------------------------------------------

	[Fact]
	public void AGunTurnsNoFasterThanItsTraverse()
	{
		DefenseTraits traits = Gun();
		float yaw = 0f;
		float pitch = 0f;

		DefenseSim.Slew(ref yaw, ref pitch, Mathf.Pi * 0.5f, 0.3f, traits);

		Assert.Equal(traits.TraverseRadiansPerTick, yaw, 5);
		Assert.Equal(traits.TraverseRadiansPerTick, pitch, 5);
	}

	[Fact]
	public void AQuarterTurnTakesAQuarterOfASecondAtNinetyDegreesASecond()
	{
		DefenseTraits traits = Gun();
		float yaw = 0f;
		float pitch = 0f;
		int ticks = 0;
		float error = float.MaxValue;

		while (error > traits.OnTargetRadians && ticks < 1000)
		{
			error = DefenseSim.Slew(ref yaw, ref pitch, Mathf.Pi * 0.5f, 0f, traits);
			ticks++;
		}

		// 90° at 90°/s, less the last two degrees it is allowed to fire from.
		Assert.InRange(ticks, 58, 60);
	}

	[Fact]
	public void AGunTurnsTheShortWayRoundThroughTheBack()
	{
		DefenseTraits traits = Gun();
		float yaw = Mathf.Pi - 0.05f;
		float pitch = 0f;

		DefenseSim.Slew(ref yaw, ref pitch, -Mathf.Pi + 0.05f, 0f, traits);

		// Across ±π, not three hundred and fifty degrees the other way.
		Assert.True(MathF.Abs(Mathf.AngleDifference(yaw, -Mathf.Pi + 0.05f)) < 0.1f - (traits.TraverseRadiansPerTick * 0.5f));
		Assert.InRange(yaw, -Mathf.Pi, Mathf.Pi);
	}

	[Fact]
	public void SlewReportsTheErrorItLeft()
	{
		DefenseTraits traits = Gun();
		float yaw = 0.2f;
		float pitch = 0.1f;

		float error = DefenseSim.Slew(ref yaw, ref pitch, 0.2f, 0.1f, traits);

		Assert.Equal(0f, error, 4);
	}

	// ---- firing ------------------------------------------------------------

	[Fact]
	public void NothingFiresBeforeItsReaction()
	{
		DefenseTraits traits = Gun(reactionTicks: 30);

		Assert.False(DefenseSim.MayFire(traits, 129, 100, 0f));
		Assert.True(DefenseSim.MayFire(traits, 130, 100, 0f));
	}

	[Fact]
	public void AGunStillSwingingOntoItsTargetHoldsItsFire()
	{
		DefenseTraits traits = Gun();

		Assert.False(DefenseSim.MayFire(traits, 1000, 0, traits.OnTargetRadians * 2f));
		Assert.True(DefenseSim.MayFire(traits, 1000, 0, traits.OnTargetRadians * 0.5f));
	}

	// ---- waiting outside ---------------------------------------------------

	[Fact]
	public void TheStandoffIsOnTheRingOnTheSideTheBotCameFrom()
	{
		var center = new Vector3(80f, 0.5f, -130f);
		var from = new Vector3(5f, 1f, 7f);

		Vector3 point = DefenseSim.StandoffPoint(center, from, 117f, 0f);

		Vector3 flat = point - center;
		flat.Y = 0f;
		Assert.Equal(117f, flat.Length(), 3);

		Vector3 toBot = from - center;
		toBot.Y = 0f;
		Assert.True(flat.Normalized().Dot(toBot.Normalized()) > 0.9999f);

		// At the bot's own height, so the navigation agent is not asked for a point
		// in the air.
		Assert.Equal(from.Y, point.Y);
	}

	[Fact]
	public void AnOffsetSwingsTheStandoffRoundTheRing()
	{
		var center = Vector3.Zero;
		var from = new Vector3(0f, 0f, 50f);

		Vector3 straight = DefenseSim.StandoffPoint(center, from, 20f, 0f);
		Vector3 swung = DefenseSim.StandoffPoint(center, from, 20f, Mathf.Pi / 6f);

		Assert.Equal(20f, swung.Length(), 3);
		Assert.Equal(Mathf.Pi / 6f, straight.AngleTo(swung), 3);
	}

	[Fact]
	public void StandingOnTheCentreStillHasAnAnswer()
	{
		Vector3 point = DefenseSim.StandoffPoint(Vector3.Zero, Vector3.Zero, 10f, 0f);

		Assert.Equal(10f, point.Length(), 3);
		Assert.Equal(point, DefenseSim.StandoffPoint(Vector3.Zero, Vector3.Zero, 10f, 0f));
	}

	[Fact]
	public void InsideIsMeasuredFlat()
	{
		var center = new Vector3(0f, 0f, 0f);

		Assert.True(DefenseSim.IsInside(center, new Vector3(9f, 40f, 0f), 10f));
		Assert.False(DefenseSim.IsInside(center, new Vector3(11f, 0f, 0f), 10f));
		Assert.False(DefenseSim.IsInside(center, center, 0f));
	}

	// ---- the mortar ----------------------------------------------------------

	[Theory]
	[InlineData(20f, 0f)]
	[InlineData(35f, 0f)]
	[InlineData(60f, 0f)]
	[InlineData(100f, 0f)]
	[InlineData(40f, -6.6f)]
	[InlineData(100f, -6.6f)]
	[InlineData(70f, 3f)]
	public void AShellComesDownWhereItWasAimed(float distance, float height)
	{
		// Solved with one integration step a tick; flown with the eight the simulation
		// uses. The two must agree, or the shortcut in the solver is a miss.
		var origin = new Vector3(80f, 7.1f, -130f);
		Vector3 bearing = new Vector3(-0.48f, 0f, 0.88f).Normalized();
		Vector3 target = origin + (bearing * distance) + new Vector3(0f, height, 0f);

		Assert.True(MortarSolver.TrySolve(origin, target, Shell, out Vector3 direction, out float seconds));

		Vector3 landed = Fly(origin, direction, target.Y, out float flown);

		Vector3 miss = landed - target;
		miss.Y = 0f;
		Assert.True(miss.Length() < 0.5f, $"missed by {miss.Length():0.00} m at {distance} m");
		Assert.Equal(flown, seconds, 1);
	}

	[Fact]
	public void TheArcIsTheHighOne()
	{
		Assert.True(MortarSolver.TrySolve(Vector3.Zero, new Vector3(0f, 0f, -60f), Shell, out Vector3 direction,
			out _));

		Aim.Angles(direction, out _, out float pitch);
		Assert.True(pitch > Mathf.DegToRad(45f), $"pitch {Mathf.RadToDeg(pitch):0.0}°");
	}

	[Fact]
	public void TheAuthoredShellReachesTheAuthoredRange()
	{
		// The Test map's mortar stands on a six-metre roof and engages to 100 m; the
		// shell has to get there from level ground too, or a map that puts the tube
		// at ground level has a ring with a hole in it.
		Assert.True(MortarSolver.TryRange((MathF.PI * 0.25f), 0f, Shell, out float range, out _));
		Assert.True(range > 100f, $"reaches {range:0.0} m");
	}

	[Fact]
	public void OutOfReachIsNoSolution()
	{
		Assert.False(MortarSolver.TrySolve(Vector3.Zero, new Vector3(0f, 0f, -300f), Shell, out _, out _));
	}

	[Fact]
	public void InsideTheSteepestArcIsNoSolution()
	{
		// At 88° a 34 m/s shell still carries several metres; closer than that the tube
		// would have to lean back over its own crew.
		Assert.False(MortarSolver.TrySolve(Vector3.Zero, new Vector3(0f, 0f, -1f), Shell, out _, out _));
	}

	[Fact]
	public void StraightUpIsNoSolution()
	{
		Assert.False(MortarSolver.TrySolve(Vector3.Zero, new Vector3(0f, 10f, 0f), Shell, out _, out _));
	}

	[Fact]
	public void ATargetAboveTheTopOfTheArcIsNoSolution()
	{
		Assert.False(MortarSolver.TrySolve(Vector3.Zero, new Vector3(0f, 80f, -20f), Shell, out _, out _));
	}

	[Fact]
	public void AShellWithNoMuzzleVelocityGoesNowhere()
	{
		ProjectileStats dud = new(Shell.Physics, 0f, 100f);
		Assert.False(MortarSolver.TrySolve(Vector3.Zero, new Vector3(0f, 0f, -40f), dud, out _, out _));
	}

	[Fact]
	public void RangeFallsAsTheTubeRises()
	{
		// What the bisection depends on: above the elevation of greatest range, a
		// steeper tube always lands shorter.
		float previous = float.MaxValue;
		for (float degrees = 46f; degrees <= 88f; degrees += 2f)
		{
			Assert.True(MortarSolver.TryRange(Mathf.DegToRad(degrees), 0f, Shell, out float range, out _));
			Assert.True(range < previous, $"{degrees}° went {range:0.00} m, further than {previous:0.00} m");
			previous = range;
		}
	}

	// ---- scatter -------------------------------------------------------------

	[Fact]
	public void ScatterStaysInsideItsCircleAndOnTheGround()
	{
		for (uint shot = 0; shot < 2000; shot++)
		{
			Vector3 offset = Spread.Disc(3f, Spread.Seed(OwnerId.ForDefense(2), 9, shot));
			Assert.Equal(0f, offset.Y);
			Assert.True(offset.Length() <= 3f + 1e-4f);
		}
	}

	[Fact]
	public void ScatterIsSeededNotRandom()
	{
		uint seed = Spread.Seed(OwnerId.ForDefense(1), 9, 42);
		Assert.Equal(Spread.Disc(3f, seed), Spread.Disc(3f, seed));
		Assert.NotEqual(Spread.Disc(3f, seed), Spread.Disc(3f, Spread.Seed(OwnerId.ForDefense(1), 9, 43)));
	}

	[Fact]
	public void ScatterCoversTheAreaRatherThanPilingIntoTheMiddle()
	{
		// Uniform over a disc puts half the shells outside 0.707 R. A uniform radius
		// would put only 29 % there.
		const int Shots = 4000;
		int outside = 0;
		for (uint shot = 0; shot < Shots; shot++)
		{
			if (Spread.Disc(1f, Spread.Seed(OwnerId.ForDefense(0), 9, shot)).Length() > MathF.Sqrt(0.5f))
			{
				outside++;
			}
		}

		Assert.InRange(outside / (float)Shots, 0.46f, 0.54f);
	}

	[Fact]
	public void NoScatterIsNoOffset()
	{
		Assert.Equal(Vector3.Zero, Spread.Disc(0f, 12345u));
	}

	/// <summary>
	/// Flies a shell through the real projectile pool and returns where it came down
	/// through <paramref name="height"/>, and after how long.
	/// </summary>
	private static Vector3 Fly(Vector3 origin, Vector3 direction, float height, out float seconds)
	{
		var sim = new ProjectileSim(new[] { Shell }, 4);
		Assert.NotEqual(0u, sim.Spawn(0, OwnerId.ForDefense(0), 0, origin, direction, 0));

		for (int tick = 1; tick < SimConfig.TickRate * 20; tick++)
		{
			sim.Step(SimConfig.TickDelta);
			ProjectileSegment segment = sim.Segments[0];

			if (segment.From.Y > height && segment.To.Y <= height)
			{
				float f = (segment.From.Y - height) / (segment.From.Y - segment.To.Y);
				seconds = (tick - 1 + f) * SimConfig.TickDelta;
				return segment.PointAt(f);
			}
		}

		throw new InvalidOperationException("the shell never came down");
	}
}
