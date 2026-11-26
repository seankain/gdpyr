using System;
using Gdpyr.Sim;
using Godot;
using Xunit;

namespace Gdpyr.Tests;

/// <summary>Scripts/Sim/Ladders.cs: taking a ladder, climbing it, going over the top and letting go (docs/LADDERS.md).</summary>
public class LadderTests
{
	private const float Dt = SimConfig.TickDelta;

	/// <summary>A 3.5 m ladder whose foot is at the origin, facing +Z: its climber stands on +Z and faces -Z.</summary>
	private static readonly LadderSpan Ladder = new(Vector3.Zero, Vector3.Back, 0.5f, 3.5f);

	private static readonly Vector3 Into = Vector3.Forward;
	private static readonly Vector3 Away = Vector3.Back;

	private static LadderSpan[] One => new[] { Ladder };

	/// <summary>Where a climber is held, <paramref name="up"/> above the foot.</summary>
	private static Vector3 OnIt(float up) => new(0f, up, Ladders.HoldOutMeters);

	// ---- the frame ----------------------------------------------------------------------

	[Fact]
	public void Span_IsLevelAndOfUnitLength_HoweverItIsPlaced()
	{
		var tilted = new LadderSpan(Vector3.Zero, new Vector3(0f, 2f, 3f), 0.5f, 3f);
		Assert.Equal(0f, tilted.Normal.Y);
		Assert.Equal(1f, tilted.Normal.Length(), precision: 5);
		Assert.Equal(Vector3.Back, new LadderSpan(Vector3.Zero, Vector3.Up, 0.5f, 3f).Normal);
	}

	[Fact]
	public void Place_MeasuresOutAlongAndUp()
	{
		var ladder = new LadderSpan(new Vector3(10f, 1f, 5f), Vector3.Right, 0.6f, 4f);
		LadderPlace at = ladder.Place(new Vector3(10.7f, 3f, 4.8f));

		Assert.Equal(0.7f, at.Out, precision: 4);
		Assert.Equal(2f, at.Up, precision: 4);

		// Facing a ladder that looks along +X is facing -X; the climber's right is -Z.
		Assert.Equal(Vector3.Forward, ladder.Right);
		Assert.Equal(0.2f, at.Side, precision: 4);
		Assert.Equal(5f, ladder.TopY, precision: 4);
	}

	// ---- taking it ----------------------------------------------------------------------

	[Fact]
	public void WalkingIntoIt_TakesIt_FromTheGround()
	{
		Assert.True(Ladders.TryGrab(One, new Vector3(0f, 0f, 0.8f), Into, onFloor: true, rising: false, out int index));
		Assert.Equal(0, index);
	}

	[Fact]
	public void WalkingAtSixtyDegrees_StillTakesIt_AndPastSixtyDoesNot()
	{
		Vector3 feet = new(0f, 0f, 0.8f);
		Vector3 at55 = new Vector3(MathF.Sin(Mathf.DegToRad(55f)), 0f, -MathF.Cos(Mathf.DegToRad(55f)));
		Vector3 at65 = new Vector3(MathF.Sin(Mathf.DegToRad(65f)), 0f, -MathF.Cos(Mathf.DegToRad(65f)));

		Assert.True(Ladders.TryGrab(One, feet, at55, true, false, out _));
		Assert.False(Ladders.TryGrab(One, feet, at65, true, false, out _));
	}

	[Fact]
	public void WalkingPastIt_OrAwayFromIt_OrStandingStill_DoesNotTakeIt()
	{
		Vector3 feet = new(0f, 0f, 0.8f);
		Assert.False(Ladders.TryGrab(One, feet, Vector3.Right, true, false, out _));
		Assert.False(Ladders.TryGrab(One, feet, Away, true, false, out _));
		Assert.False(Ladders.TryGrab(One, feet, Vector3.Zero, true, false, out _));
	}

	[Theory]
	[InlineData(0f, 0f, 0.95f)] // out of reach
	[InlineData(0f, 0f, -0.1f)] // behind its face
	[InlineData(0.65f, 0f, 0.7f)] // past the end of its rungs
	[InlineData(0f, -0.6f, 0.7f)] // below its foot
	[InlineData(0f, 3.3f, 0.7f)] // level with its landing: that is taken from the landing
	public void OutsideItsReach_ItIsNotTaken(float x, float y, float z)
	{
		Assert.False(Ladders.TryGrab(One, new Vector3(x, y, z), Into, true, false, out _));
	}

	[Fact]
	public void JumpingUpAtIt_DoesNotTakeIt_UntilTheJumpComesDown()
	{
		Vector3 feet = new(0f, 0.6f, 0.7f);
		Assert.False(Ladders.TryGrab(One, feet, Into, onFloor: false, rising: true, out _));
		Assert.True(Ladders.TryGrab(One, feet, Into, onFloor: false, rising: false, out _));
	}

	[Fact]
	public void FromTheLanding_WalkingOffTowardsIt_TakesIt_AndOnlyStandingOnTheLanding()
	{
		Vector3 feet = new(0f, 3.5f, -0.4f);
		Assert.True(Ladders.TryGrab(One, feet, Away, onFloor: true, rising: false, out _));
		Assert.False(Ladders.TryGrab(One, feet, Away, onFloor: false, rising: false, out _));
		Assert.False(Ladders.TryGrab(One, feet, Into, onFloor: true, rising: false, out _));
		Assert.False(Ladders.TryGrab(One, new Vector3(0f, 3.5f, -0.8f), Away, true, false, out _));
	}

	[Fact]
	public void OfTwoInReach_TheNearerIsTaken()
	{
		var other = new LadderSpan(new Vector3(0.9f, 0f, 0f), Vector3.Back, 0.5f, 3.5f);
		LadderSpan[] both = { other, Ladder };

		Assert.True(Ladders.TryGrab(both, new Vector3(0.2f, 0f, 0.6f), Into, true, false, out int index));
		Assert.Equal(1, index);
		Assert.True(Ladders.TryGrab(both, new Vector3(0.7f, 0f, 0.6f), Into, true, false, out index));
		Assert.Equal(0, index);
	}

	[Fact]
	public void EverythingTaken_IsHeld()
	{
		foreach (Vector3 feet in new[]
		{
			new Vector3(0f, 0f, 0.9f), new Vector3(0.6f, 0f, 0f), new Vector3(0f, 3.15f, 0.5f),
			new Vector3(0f, 3.85f, -0.6f), new Vector3(-0.6f, 3.15f, 0.3f),
		})
		{
			bool below = Ladders.TryGrab(One, feet, Into, true, false, out _);
			bool above = Ladders.TryGrab(One, feet, Away, true, false, out _);
			if (below || above)
			{
				Assert.True(Ladders.Holds(Ladder, feet), $"taken but not held at {feet}");
			}
		}
	}

	// ---- climbing it --------------------------------------------------------------------

	[Fact]
	public void IntoItClimbs_AwayFromItComesDown_PastItDoesNeither()
	{
		Assert.Equal(Ladders.ClimbSpeed, Ladders.Climb(Ladder, OnIt(1f), Into, 1f, Dt).Velocity.Y, precision: 4);
		Assert.Equal(-Ladders.ClimbSpeed, Ladders.Climb(Ladder, OnIt(1f), Away, 1f, Dt).Velocity.Y, precision: 4);
		Assert.Equal(0f, Ladders.Climb(Ladder, OnIt(1f), Vector3.Right, 1f, Dt).Velocity.Y, precision: 4);
		Assert.Equal(0f, Ladders.Climb(Ladder, OnIt(1f), Vector3.Zero, 1f, Dt).Velocity.Y, precision: 4);
	}

	[Fact]
	public void ItIsTheWalkThatClimbs_NotTheLook()
	{
		// Backing down from the landing: facing away, forward is away from the ladder.
		Vector3 forwardFacingAway = Movement.Direction(new Vector2(0f, -1f), Mathf.Pi);
		Assert.True(Ladders.Climb(Ladder, OnIt(2f), forwardFacingAway, 1f, Dt).Velocity.Y < 0f);

		// Facing it, back is away from it.
		Vector3 backFacingIt = Movement.Direction(new Vector2(0f, 1f), 0f);
		Assert.True(Ladders.Climb(Ladder, OnIt(2f), backFacingIt, 1f, Dt).Velocity.Y < 0f);
	}

	[Fact]
	public void ADiagonalWalk_ClimbsByWhatIsIntoIt()
	{
		Vector3 diagonal = new Vector3(1f, 0f, -1f).Normalized();
		Assert.Equal(Ladders.ClimbSpeed * 0.7071f, Ladders.Climb(Ladder, OnIt(1f), diagonal, 1f, Dt).Velocity.Y,
			precision: 3);
	}

	[Fact]
	public void TheSpeedScale_ScalesTheClimb()
	{
		Assert.Equal(Ladders.ClimbSpeed * 0.55f, Ladders.Climb(Ladder, OnIt(1f), Into, 0.55f, Dt).Velocity.Y,
			precision: 4);
		Assert.Equal(0f, Ladders.Climb(Ladder, OnIt(1f), Into, -1f, Dt).Velocity.Y, precision: 4);
	}

	[Fact]
	public void TheBodyIsHeldOnTheRungsMiddle_AHandSpanOffTheFace()
	{
		Vector3 v = Ladders.Climb(Ladder, new Vector3(0.3f, 1f, 0.9f), Vector3.Zero, 1f, Dt).Velocity;
		Assert.True(v.X < 0f, "pulled back to the middle");
		Assert.True(v.Z < 0f, "pulled in to the holding line");

		Vector3 held = Ladders.Climb(Ladder, OnIt(1f), Vector3.Zero, 1f, Dt).Velocity;
		Assert.Equal(Vector3.Zero, held);

		// Strafing does not move it off the middle.
		Assert.Equal(0f, Ladders.Climb(Ladder, OnIt(1f), Vector3.Right, 1f, Dt).Velocity.X, precision: 4);
	}

	[Fact]
	public void UpAndOver_TheTopLandsTheBodyOnTheLanding_AndLetsGo()
	{
		Vector3 feet = OnIt(0f);
		int ticks = 0;
		float highest = 0f;
		bool mounted = false;
		while (Ladders.Holds(Ladder, feet) && ticks < 10 * SimConfig.TickRate)
		{
			ClimbStep step = Ladders.Climb(Ladder, feet, Into, 1f, Dt);
			mounted |= step.Mounting;
			feet += step.Velocity * Dt;
			highest = MathF.Max(highest, feet.Y);
			ticks++;
		}

		Assert.True(mounted);
		Assert.False(Ladders.Holds(Ladder, feet));
		Assert.Equal(Ladder.Height + Ladders.MountClearanceMeters, highest, precision: 3);
		Assert.True(feet.Z < -Ladders.MountDepthMeters, $"over the landing at {feet}");
		Assert.Equal(0f, feet.X, precision: 4);

		// 3.6 m up at 3 m/s, then 1.2 m over at 2.5 m/s: about 1.7 s.
		float seconds = ticks * Dt;
		Assert.InRange(seconds, 1.6f, 1.8f);
	}

	[Fact]
	public void LevelWithTheTop_TheClimbStopsThere_UntilItGoesOver()
	{
		float top = Ladder.Height + Ladders.MountClearanceMeters;
		ClimbStep step = Ladders.Climb(Ladder, OnIt(top - 0.01f), Into, 1f, Dt);
		Assert.True(step.Mounting);
		Assert.Equal(0f, step.Velocity.Y);
		Assert.Equal(-Ladders.MountSpeed, step.Velocity.Z, precision: 4);

		// A tick from the top, the climb is cut so as not to overshoot it.
		step = Ladders.Climb(Ladder, OnIt(top - 0.03f), Into, 1f, Dt);
		Assert.False(step.Mounting);
		Assert.Equal(0.03f, step.Velocity.Y * Dt, precision: 4);

		// Level with the top and not climbing, it hangs there.
		step = Ladders.Climb(Ladder, OnIt(top), Vector3.Zero, 1f, Dt);
		Assert.False(step.Mounting);
		Assert.Equal(Vector3.Zero, step.Velocity);
	}

	[Fact]
	public void TakenFromTheLanding_ItGoesOutOverTheEdge_AndDown()
	{
		Vector3 v = Ladders.Climb(Ladder, new Vector3(0f, 3.5f, -0.5f), Away, 1f, Dt).Velocity;
		Assert.Equal(Ladders.MaxHoldSpeed, v.Z, precision: 4);
		Assert.Equal(-Ladders.ClimbSpeed, v.Y, precision: 4);
	}

	// ---- letting go ---------------------------------------------------------------------

	[Fact]
	public void AtTheFoot_OnTheGround_NotClimbing_ItLetsGo()
	{
		Assert.True(Ladders.LetsGoAtFoot(Ladder, OnIt(0f), Away, onFloor: true));
		Assert.True(Ladders.LetsGoAtFoot(Ladder, OnIt(0f), Vector3.Zero, onFloor: true));
		Assert.False(Ladders.LetsGoAtFoot(Ladder, OnIt(0f), Into, onFloor: true));
		Assert.False(Ladders.LetsGoAtFoot(Ladder, OnIt(0.3f), Away, onFloor: false));

		// Standing on the landing, coming down onto the ladder, is not the foot.
		Assert.False(Ladders.LetsGoAtFoot(Ladder, new Vector3(0f, 3.5f, -0.4f), Away, onFloor: true));
	}

	[Fact]
	public void AJump_PushesOffOutAndUp()
	{
		Vector3 v = Ladders.JumpOff(Ladder);
		Assert.Equal(Ladders.JumpOffSpeed, v.Z, precision: 4);
		Assert.Equal(Ladders.JumpOffLift, v.Y, precision: 4);
		Assert.False(Ladders.TryGrab(One, OnIt(1f), Into, onFloor: false, rising: v.Y > 0f, out _));
	}

	[Fact]
	public void Holding_IsNoneOffEveryLadder()
	{
		Assert.Equal(-1, Ladders.Holding(One, new Vector3(5f, 0f, 5f)));
		Assert.Equal(-1, Ladders.Holding(ReadOnlySpan<LadderSpan>.Empty, OnIt(1f)));
		Assert.Equal(0, Ladders.Holding(One, OnIt(1f)));
	}

	// ---- a bot --------------------------------------------------------------------------

	[Fact]
	public void ABot_ClimbsTowardsAWaypointNearTheTop_AndDownTowardsOneNearTheFoot()
	{
		Assert.Equal(Into, Ladders.Steer(Ladder, new Vector3(0f, 3.5f, -1.6f)));
		Assert.Equal(Into, Ladders.Steer(Ladder, new Vector3(4f, 3.2f, 3f)));
		Assert.Equal(Away, Ladders.Steer(Ladder, new Vector3(0f, 0f, 1.8f)));

		// A waypoint at the bottom, straight behind the ladder, is still down.
		Assert.Equal(Away, Ladders.Steer(Ladder, new Vector3(0f, 0.2f, -3f)));
	}

	[Fact]
	public void ABotPassingAFoot_DoesNotTakeTheLadder()
	{
		Vector3 feet = new(0.2f, 0f, 0.7f);
		Vector3 diagonal = new Vector3(1f, 0f, -1f).Normalized();
		Assert.True(Ladders.TryGrab(One, feet, diagonal, true, false, out _));

		Vector3 kept = Ladders.WithoutGrab(One, feet, diagonal);
		Assert.False(Ladders.TryGrab(One, feet, kept, true, false, out _));
		Assert.True(kept.IsEqualApprox(Vector3.Right), $"{kept}");

		// Straight into it, nothing is left.
		Assert.Equal(Vector3.Zero, Ladders.WithoutGrab(One, feet, Into));

		// Away from it, nothing is taken out.
		Assert.True(Ladders.WithoutGrab(One, feet, Away).IsEqualApprox(Away));

		// Nowhere near it, nothing either.
		Assert.True(Ladders.WithoutGrab(One, new Vector3(5f, 0f, 5f), diagonal).IsEqualApprox(diagonal));
	}

	[Fact]
	public void ABotWalkingAlongTheLanding_DoesNotStepOntoTheLadder()
	{
		Vector3 feet = new(0f, 3.5f, -0.4f);
		Vector3 offTheEdge = new Vector3(1f, 0f, 1f).Normalized();
		Assert.True(Ladders.TryGrab(One, feet, offTheEdge, true, false, out _));

		Vector3 kept = Ladders.WithoutGrab(One, feet, offTheEdge);
		Assert.False(Ladders.TryGrab(One, feet, kept, true, false, out _));
		Assert.True(kept.IsEqualApprox(Vector3.Right), $"{kept}");
	}
}
