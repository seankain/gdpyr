using Gdpyr.Sim;
using Godot;
using Xunit;

namespace Gdpyr.Tests;

public class BotBrainTests
{
	private const int BotPeerId = BotRoster.FirstPeerId;

	/// <summary>
	/// The shipped profile with the error cone taken out. The cone is tested on its
	/// own; everything else is easier to reason about when the bot aims where it
	/// means to.
	/// </summary>
	private static readonly BotTraits Steady = new(
		turnRateRadians: 4.5f,
		aimConeRadians: 0f,
		aimJitterTicks: 20,
		reactionTicks: 12,
		engageRangeMeters: 70f,
		preferredRangeMeters: 25f,
		sensorRadiusMeters: 80f,
		fireAngleRadians: Spread.ConeFromDegrees(6f),
		strafePeriodTicks: 45);

	private static BotSituation Situation(bool alive = true, float yaw = 0f, float pitch = 0f, float speed = 0f,
		bool hasTarget = false, Vector3 aimDirection = default, float targetDistance = 999f,
		bool targetVisible = false, int ticksOnTarget = 0, bool hasDestination = false,
		Vector3 moveDirection = default, float destinationDistance = 0f, bool stuck = false,
		bool needsReload = false) =>
		new(alive, yaw, pitch, speed, hasTarget, aimDirection, targetDistance, targetVisible, ticksOnTarget,
			hasDestination, moveDirection, destinationDistance, stuck, needsReload);

	// ---- movement axes -----------------------------------------------------

	[Theory]
	[InlineData(0f)]
	[InlineData(1.2f)]
	[InlineData(-2.7f)]
	[InlineData(3.1415f)]
	public void MoveAxes_AreTheInverseOfTheMovementStepsDirection(float yaw)
	{
		// The character will feed these straight back through Movement.Direction, so
		// anything but an exact inverse is a bot that walks at an angle to where it
		// meant to go.
		Vector3 wanted = new Vector3(0.3f, 0f, -0.9f).Normalized();

		Vector3 got = Movement.Direction(BotBrain.MoveAxes(wanted, yaw), yaw);

		Assert.Equal(wanted.X, got.X, precision: 4);
		Assert.Equal(wanted.Z, got.Z, precision: 4);
	}

	[Fact]
	public void MoveAxes_AreNeverLongerThanOne()
	{
		// An axis pair past unit length survives quantization and would buy the bot
		// speed the movement step does not give a player.
		Assert.True(BotBrain.MoveAxes(new Vector3(9f, 0f, -9f), 0.5f).Length() <= 1.0001f);
	}

	[Fact]
	public void MoveAxes_AreZeroForNowhereToGo()
	{
		Assert.Equal(Vector2.Zero, BotBrain.MoveAxes(Vector3.Zero, 1f));
		Assert.Equal(Vector2.Zero, BotBrain.MoveAxes(Vector3.Up, 1f));
	}

	// ---- aiming ------------------------------------------------------------

	[Fact]
	public void Slew_TurnsTheShortWayAndNoFurtherThanItMay()
	{
		Assert.Equal(0.1f, BotBrain.Slew(0f, 1f, 0.1f), precision: 5);
		Assert.Equal(-0.1f, BotBrain.Slew(0f, -1f, 0.1f), precision: 5);

		// Across the wrap: 6.0 rad is a short turn clockwise from 0, not a long one
		// the other way.
		Assert.Equal(-0.1f, BotBrain.Slew(0f, 6f, 0.1f), precision: 5);
	}

	[Fact]
	public void Aim_TurnsNoFasterThanTheTraitsAllow()
	{
		InputFrame frame = BotBrain.Frame(0, BotPeerId,
			Situation(hasTarget: true, aimDirection: Vector3.Right, targetDistance: 30f, targetVisible: true),
			Steady);

		// One tick of turn, not a snap onto the target. Measured as a difference,
		// because the wire's yaw is unsigned: a small turn left comes back as 6.2 rad.
		float turned = Mathf.Abs(Mathf.AngleDifference(0f, frame.YawRadians));

		Assert.InRange(turned, 0f, (Steady.TurnRateRadians * SimConfig.TickDelta) + 0.01f);
	}

	[Fact]
	public void Aim_ConvergesOnTheTargetOverAFewTicks()
	{
		float yaw = 0f;
		float pitch = 0f;

		for (uint tick = 0; tick < 60; tick++)
		{
			InputFrame frame = BotBrain.Frame(tick, BotPeerId,
				Situation(yaw: yaw, pitch: pitch, hasTarget: true, aimDirection: Vector3.Right,
					targetDistance: 30f, targetVisible: true, ticksOnTarget: (int)tick),
				Steady);

			yaw = frame.YawRadians;
			pitch = frame.PitchRadians;
		}

		Aim.Angles(Vector3.Right, out float wantedYaw, out _);
		Assert.Equal(0f, Mathf.AngleDifference(yaw, wantedYaw), precision: 2);
	}

	[Fact]
	public void Aim_FollowsTheWayItIsWalkingWhenThereIsNothingToShoot()
	{
		InputFrame frame = BotBrain.Frame(0, BotPeerId,
			Situation(yaw: 0f, hasDestination: true, moveDirection: Vector3.Left, destinationDistance: 40f),
			Steady);

		// Left is -X, which is a quarter turn from the -Z it starts facing: it should
		// have begun turning that way rather than staying put.
		Assert.True(frame.YawRadians > 0f);
	}

	[Fact]
	public void Aim_ErrorIsHeldForAWholeJitterWindowRatherThanReRolledPerTick()
	{
		// Per-tick noise averages out over a burst and makes a bot more accurate the
		// longer it fires; a held offset is a bot that is simply mis-tracking.
		var jittery = new BotTraits(turnRateRadians: 100f, aimConeRadians: Spread.ConeFromDegrees(10f),
			aimJitterTicks: 20, reactionTicks: 0, engageRangeMeters: 70f, preferredRangeMeters: 25f,
			sensorRadiusMeters: 80f, fireAngleRadians: Spread.ConeFromDegrees(30f), strafePeriodTicks: 45);

		BotSituation situation = Situation(hasTarget: true, aimDirection: Vector3.Right, targetDistance: 30f,
			targetVisible: true, ticksOnTarget: 30);

		InputFrame first = BotBrain.Frame(0, BotPeerId, situation, jittery);
		InputFrame sameWindow = BotBrain.Frame(19, BotPeerId, situation, jittery);
		InputFrame nextWindow = BotBrain.Frame(20, BotPeerId, situation, jittery);

		Assert.Equal((int)first.Yaw, (int)sameWindow.Yaw);
		Assert.NotEqual((int)first.Yaw, (int)nextWindow.Yaw);
	}

	// ---- the trigger -------------------------------------------------------

	[Fact]
	public void Fire_OnlyOnceItHasBothSeenTheTargetAndTurnedOntoIt()
	{
		BotSituation aimed = Situation(yaw: YawTowards(Vector3.Right), hasTarget: true,
			aimDirection: Vector3.Right, targetDistance: 30f, targetVisible: true, ticksOnTarget: 30);

		Assert.True(BotBrain.ShouldFire(aimed, Steady, aimed.Yaw, aimed.Pitch));
	}

	[Fact]
	public void Fire_WaitsOutTheReactionTime()
	{
		BotSituation fresh = Situation(yaw: YawTowards(Vector3.Right), hasTarget: true,
			aimDirection: Vector3.Right, targetDistance: 30f, targetVisible: true, ticksOnTarget: 1);

		Assert.False(BotBrain.ShouldFire(fresh, Steady, fresh.Yaw, fresh.Pitch));
	}

	[Fact]
	public void Fire_NotWhileTheShotIsBlocked()
	{
		// Visibility is where "a friendly is standing in front of me" ends up
		// (BotPilot.FriendlyInLineOfFire), so this is the friendly-fire guard.
		BotSituation blocked = Situation(yaw: YawTowards(Vector3.Right), hasTarget: true,
			aimDirection: Vector3.Right, targetDistance: 30f, targetVisible: false, ticksOnTarget: 30);

		Assert.False(BotBrain.ShouldFire(blocked, Steady, blocked.Yaw, blocked.Pitch));
	}

	[Fact]
	public void Fire_NotPastItsEngagementRange()
	{
		BotSituation far = Situation(yaw: YawTowards(Vector3.Right), hasTarget: true,
			aimDirection: Vector3.Right, targetDistance: 200f, targetVisible: true, ticksOnTarget: 30);

		Assert.False(BotBrain.ShouldFire(far, Steady, far.Yaw, far.Pitch));
	}

	[Fact]
	public void Fire_NotWhileStillSwingingOntoTheTarget()
	{
		BotSituation turning = Situation(yaw: 0f, hasTarget: true, aimDirection: Vector3.Right,
			targetDistance: 30f, targetVisible: true, ticksOnTarget: 30);

		Assert.False(BotBrain.ShouldFire(turning, Steady, turning.Yaw, turning.Pitch));
	}

	// ---- the rest of the buttons -------------------------------------------

	[Fact]
	public void ADeadBotHoldsItsAnglesAndNothingElse()
	{
		InputFrame frame = BotBrain.Frame(10, BotPeerId,
			Situation(alive: false, yaw: 1.2f, pitch: -0.3f, hasTarget: true, aimDirection: Vector3.Right,
				targetVisible: true, ticksOnTarget: 60, stuck: true),
			Steady);

		Assert.Equal(0, (int)frame.Buttons);
		Assert.Equal(0, (int)frame.MoveX);
		Assert.Equal(0, (int)frame.MoveZ);
		Assert.Equal(1.2f, frame.YawRadians, precision: 2);
	}

	[Fact]
	public void Sprint_OnlyWhenMovingFarWithNothingToShootAt()
	{
		InputFrame running = BotBrain.Frame(0, BotPeerId,
			Situation(speed: 4f, hasDestination: true, moveDirection: Vector3.Forward,
				destinationDistance: 60f),
			Steady);

		Assert.True(running.Held(InputButtons.Sprint));

		// Standing still: sprint is edge-triggered out of the walking state, and a
		// press made while idle is an edge the FSM never sees again.
		InputFrame standing = BotBrain.Frame(0, BotPeerId,
			Situation(speed: 0f, hasDestination: true, moveDirection: Vector3.Forward,
				destinationDistance: 60f),
			Steady);

		Assert.False(standing.Held(InputButtons.Sprint));

		InputFrame fighting = BotBrain.Frame(0, BotPeerId,
			Situation(speed: 4f, hasTarget: true, aimDirection: Vector3.Right, targetDistance: 40f,
				targetVisible: true, hasDestination: true, moveDirection: Vector3.Forward,
				destinationDistance: 60f),
			Steady);

		Assert.False(fighting.Held(InputButtons.Sprint));
	}

	[Fact]
	public void Jump_WhenWedged()
	{
		InputFrame frame = BotBrain.Frame(0, BotPeerId,
			Situation(hasDestination: true, moveDirection: Vector3.Forward, destinationDistance: 30f,
				stuck: true),
			Steady);

		Assert.True(frame.Held(InputButtons.Jump));
	}

	[Fact]
	public void Reload_BetweenFightsAndNotDuringOne()
	{
		InputFrame quiet = BotBrain.Frame(0, BotPeerId, Situation(needsReload: true), Steady);
		Assert.True(quiet.Held(InputButtons.Reload));

		InputFrame engaged = BotBrain.Frame(0, BotPeerId,
			Situation(hasTarget: true, aimDirection: Vector3.Right, targetDistance: 30f, targetVisible: true,
				needsReload: true),
			Steady);

		Assert.False(engaged.Held(InputButtons.Reload));
	}

	// ---- strafing ----------------------------------------------------------

	[Fact]
	public void Strafe_HoldsItsDirectionForAWholePeriod()
	{
		float first = BotBrain.StrafeSign(0, BotPeerId, 45);

		Assert.Equal(first, BotBrain.StrafeSign(44, BotPeerId, 45));
		Assert.Equal(1f, Mathf.Abs(first));
	}

	[Fact]
	public void Strafe_DoesNotHaveEveryBotMirroringEveryOther()
	{
		bool differs = false;
		for (int slot = 1; slot < BotRoster.MaxBots && !differs; slot++)
		{
			differs = BotBrain.StrafeSign(0, BotRoster.PeerIdFor(0), 45)
				!= BotBrain.StrafeSign(0, BotRoster.PeerIdFor(slot), 45);
		}

		Assert.True(differs);
	}

	[Fact]
	public void AtItsPreferredRangeItStrafesAcrossTheTargetRatherThanIntoIt()
	{
		// Standing still in the open while exchanging fire is the single thing that
		// makes a bot read as a target rather than an opponent.
		float yaw = YawTowards(Vector3.Right);
		InputFrame frame = BotBrain.Frame(0, BotPeerId,
			Situation(yaw: yaw, hasTarget: true, aimDirection: Vector3.Right, targetDistance: 10f,
				targetVisible: true, ticksOnTarget: 30),
			Steady);

		Vector3 world = Movement.Direction(frame.MoveAxes, frame.YawRadians);

		Assert.NotEqual(Vector3.Zero, world);
		Assert.Equal(0f, world.Dot(Vector3.Right), precision: 1);
	}

	private static float YawTowards(Vector3 direction)
	{
		Aim.Angles(direction, out float yaw, out _);
		return yaw;
	}
}
