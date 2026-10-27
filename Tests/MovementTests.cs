using System;
using Gdpyr.Sim;
using Godot;
using Xunit;

namespace Gdpyr.Tests;

public class MovementTests
{
	private static readonly MoveParams Walk = new(speed: 5f, acceleration: 0.1f, deceleration: 0.25f);

	[Fact]
	public void Direction_IsZeroForNoInput()
	{
		// Godot's Normalized() returns zero for a zero vector rather than NaN, which
		// is what lets the movement step branch on it.
		Assert.Equal(Vector3.Zero, Movement.Direction(Vector2.Zero, 1.23f));
	}

	[Fact]
	public void Direction_IsUnitLengthForAnyInput()
	{
		Assert.Equal(1f, Movement.Direction(new Vector2(1f, 1f), 0f).Length(), precision: 5);
		Assert.Equal(1f, Movement.Direction(new Vector2(0f, -1f), 2.2f).Length(), precision: 5);
	}

	[Fact]
	public void Direction_ForwardIsMinusZAtZeroYaw()
	{
		Vector3 forward = Movement.Direction(new Vector2(0f, -1f), 0f);
		Assert.Equal(0f, forward.X, precision: 5);
		Assert.Equal(-1f, forward.Z, precision: 5);
	}

	[Fact]
	public void Direction_RotatesWithYaw()
	{
		// A quarter turn left puts "forward" on -X in Godot's right-handed frame.
		Vector3 forward = Movement.Direction(new Vector2(0f, -1f), Mathf.Pi / 2f);
		Assert.Equal(-1f, forward.X, precision: 4);
		Assert.Equal(0f, forward.Z, precision: 4);
	}

	[Fact]
	public void Direction_IgnoresAnOutOfRangeAxisPair()
	{
		// Axes are clamped at decode, but normalizing is what makes a lying client
		// unable to buy speed with a long input vector.
		Assert.Equal(1f, Movement.Direction(new Vector2(50f, 50f), 0f).Length(), precision: 5);
	}

	[Fact]
	public void ApplyMove_ApproachesTopSpeedAndStopsThere()
	{
		Vector3 velocity = Vector3.Zero;
		for (int tick = 0; tick < 600; tick++)
		{
			velocity = Movement.ApplyMove(velocity, new Vector2(0f, -1f), 0f, Walk);
		}

		Assert.Equal(Walk.Speed, new Vector2(velocity.X, velocity.Z).Length(), precision: 3);
	}

	[Fact]
	public void ApplyMove_LeavesVerticalVelocityAlone()
	{
		Vector3 velocity = new(0f, -9f, 0f);
		velocity = Movement.ApplyMove(velocity, new Vector2(1f, 0f), 0f, Walk);
		Assert.Equal(-9f, velocity.Y, precision: 5);
	}

	[Fact]
	public void ApplyMove_DeceleratesToRestWithNoInput()
	{
		Vector3 velocity = new(5f, 0f, 5f);
		for (int tick = 0; tick < 120; tick++)
		{
			velocity = Movement.ApplyMove(velocity, Vector2.Zero, 0f, Walk);
		}

		Assert.Equal(0f, velocity.X, precision: 5);
		Assert.Equal(0f, velocity.Z, precision: 5);
	}

	[Fact]
	public void ApplyMove_IsDeterministic()
	{
		// Reconciliation replays the same inputs over the same state and must land
		// on the same velocity, bit for bit (docs/NETCODE.md §3.2).
		Vector3 start = new(1.5f, -2f, 0.25f);
		var axes = new Vector2(0.5f, -1f);

		Vector3 first = start;
		Vector3 second = start;
		for (int tick = 0; tick < 50; tick++)
		{
			first = Movement.ApplyMove(first, axes, 1.1f, Walk);
			second = Movement.ApplyMove(second, axes, 1.1f, Walk);
		}

		Assert.Equal(first.X, second.X);
		Assert.Equal(first.Y, second.Y);
		Assert.Equal(first.Z, second.Z);
	}

	[Fact]
	public void ApplyMove_IsUnaffectedByAngleQuantization()
	{
		// The sampler quantizes yaw before it predicts with it, so the client and the
		// server step the same angle; this pins the size of the residual.
		float yaw = 1.234567f;
		float quantized = Quantize.U16ToYaw(Quantize.YawToU16(yaw));

		Vector3 raw = Movement.ApplyMove(Vector3.Zero, new Vector2(0f, -1f), yaw, Walk);
		Vector3 wire = Movement.ApplyMove(Vector3.Zero, new Vector2(0f, -1f), quantized, Walk);

		Assert.True((raw - wire).Length() < 1e-3f);
	}

	// ---- weight (docs/LOADOUT.md L-D4) --------------------------------------

	/// <summary>The sprint as Scenes/fps_controller.tscn authors it.</summary>
	private static readonly MoveParams Sprint = new(speed: 8f, acceleration: 0.1f, deceleration: 0.25f);

	/// <summary>A state's params at a speed scale, as <c>fps_controller.ApplyMove</c> builds them.</summary>
	private static MoveParams Scaled(in MoveParams p, float scale) => new(p.Speed * scale, p.Acceleration, p.Deceleration);

	private static float TopSpeed(in MoveParams p, float scale)
	{
		Vector3 velocity = Vector3.Zero;
		for (int tick = 0; tick < 600; tick++)
		{
			velocity = Movement.ApplyMove(velocity, new Vector2(0f, -1f), 0f, Scaled(p, scale));
		}

		return new Vector2(velocity.X, velocity.Z).Length();
	}

	[Theory]
	// §4.1's kit table: the stripped kit at the cap, the default kit, the heaviest at the floor.
	[InlineData(Loadout.MaxSpeedScale, 5.6f, 9.0f)]
	[InlineData(1f, 5.0f, 8.0f)]
	[InlineData(Loadout.MinSpeedScale, 3.5f, 5.6f)]
	public void ApplyMove_ReachesTheKitsWalkAndSprint(float scale, float walk, float sprint)
	{
		Assert.Equal(5f * scale, TopSpeed(Walk, scale), precision: 3);
		Assert.Equal(8f * scale, TopSpeed(Sprint, scale), precision: 3);
		Assert.Equal(walk, MathF.Round(TopSpeed(Walk, scale), 1), precision: 3);
		Assert.Equal(sprint, MathF.Round(TopSpeed(Sprint, scale), 1), precision: 3);
	}

	[Fact]
	public void ApplyMove_TheHeaviestKitsSprintCannotSlide()
	{
		// The sliding state wants more than 6 m/s (PlayerSprintingState); the floor's
		// sprint never reaches it, and the default's does (docs/LOADOUT.md §11).
		Assert.True(TopSpeed(Sprint, Loadout.MinSpeedScale) < 6f);
		Assert.True(TopSpeed(Sprint, 1f) > 6f);
	}

	[Fact]
	public void ApplyMove_DistanceIsLinearInTheScale()
	{
		// What weight_speed.json measures: from rest, a few ticks' walk and then a sprint
		// cover distances in the ratio of their scales, because every velocity on the way
		// is the default's times the scale — the lerp is linear in its target.
		float Distance(float scale)
		{
			Vector3 velocity = Vector3.Zero;
			float metres = 0f;
			for (int tick = 0; tick < 185; tick++)
			{
				velocity = Movement.ApplyMove(velocity, new Vector2(0f, -1f), 0f,
					Scaled(tick < 5 ? Walk : Sprint, scale));
				metres += -velocity.Z * SimConfig.TickDelta;
			}

			return metres;
		}

		float baseline = Distance(1f);
		Assert.InRange(baseline, 20f, 25f);
		Assert.Equal(Loadout.MaxSpeedScale, Distance(Loadout.MaxSpeedScale) / baseline, precision: 4);
		Assert.Equal(Loadout.MinSpeedScale, Distance(Loadout.MinSpeedScale) / baseline, precision: 4);
	}

	[Fact]
	public void ApplyGravity_AccumulatesOverTicks()
	{
		Vector3 velocity = Vector3.Zero;
		for (int tick = 0; tick < SimConfig.TickRate; tick++)
		{
			velocity = Movement.ApplyGravity(velocity, 9.8f, SimConfig.TickDelta);
		}

		Assert.Equal(-9.8f, velocity.Y, precision: 3);
	}

	[Fact]
	public void ApplyJump_ReplacesUpwardVelocity()
	{
		Vector3 velocity = Movement.ApplyJump(new Vector3(1f, 4.5f, 1f), 4.5f);
		Assert.Equal(4.5f, velocity.Y, precision: 5);
		Assert.Equal(1f, velocity.X, precision: 5);
	}
}
