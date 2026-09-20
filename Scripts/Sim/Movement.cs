using Godot;

namespace Gdpyr.Sim;

/// <summary>Per-state movement tuning, as the movement FSM exports it.</summary>
public readonly struct MoveParams
{
	public readonly float Speed;

	/// <summary>Blend weight towards the target velocity, applied once per tick.</summary>
	public readonly float Acceleration;

	/// <summary>Metres per second shed per tick when there is no input.</summary>
	public readonly float Deceleration;

	public MoveParams(float speed, float acceleration, float deceleration)
	{
		Speed = speed;
		Acceleration = acceleration;
		Deceleration = deceleration;
	}
}

/// <summary>
/// The character's velocity update as a pure function of (velocity, input, dt)
/// — the prerequisite for prediction (docs/IMPLEMENTATION_PLAN.md §3). Collision
/// resolution stays in the engine (<c>MoveAndSlide</c>); everything that decides
/// *where the character wants to go* is here, and is unit-tested without one.
///
/// The arithmetic is deliberately identical to the pre-M1 single-player
/// controller, so movement feel does not change with the netcode refactor. What
/// changes is that it now runs on a fixed tick rather than the render frame,
/// which is the M1 defect it exists to fix.
/// </summary>
public static class Movement
{
	public static Vector3 ApplyGravity(Vector3 velocity, float gravity, float dt)
	{
		velocity.Y -= gravity * dt;
		return velocity;
	}

	/// <summary>
	/// Steers the horizontal velocity towards <paramref name="moveAxes"/>, taken in
	/// the character's own frame and rotated by <paramref name="yaw"/>. Vertical
	/// velocity is untouched — gravity and jumps own it.
	/// </summary>
	public static Vector3 ApplyMove(Vector3 velocity, Vector2 moveAxes, float yaw, in MoveParams p)
	{
		Vector3 direction = Direction(moveAxes, yaw);

		if (direction != Vector3.Zero)
		{
			velocity.X = Mathf.Lerp(velocity.X, direction.X * p.Speed, p.Acceleration);
			velocity.Z = Mathf.Lerp(velocity.Z, direction.Z * p.Speed, p.Acceleration);
		}
		else
		{
			velocity.X = Mathf.MoveToward(velocity.X, 0f, p.Deceleration);
			velocity.Z = Mathf.MoveToward(velocity.Z, 0f, p.Deceleration);
		}

		return velocity;
	}

	/// <summary>
	/// The world-space unit direction the player is asking for, or zero for no
	/// input. Normalizing is what keeps diagonal movement from being faster, and
	/// what makes an out-of-range axis pair from a hostile client harmless.
	/// </summary>
	public static Vector3 Direction(Vector2 moveAxes, float yaw) =>
		(Basis.FromEuler(new Vector3(0f, yaw, 0f)) * new Vector3(moveAxes.X, 0f, moveAxes.Y)).Normalized();

	/// <summary>A jump: replaces upward velocity rather than adding to it, so it cannot be stacked.</summary>
	public static Vector3 ApplyJump(Vector3 velocity, float jumpVelocity)
	{
		velocity.Y = jumpVelocity;
		return velocity;
	}
}
