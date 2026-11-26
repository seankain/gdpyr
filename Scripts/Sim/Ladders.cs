using System;
using Godot;

namespace Gdpyr.Sim;

/// <summary>
/// A ladder as movement sees it (docs/LADDERS.md §2): the middle of its face at its foot,
/// the way its face looks — level, out towards whoever climbs it — how wide its rungs are,
/// and how high it goes. Its landing, the floor it leads onto, is <see cref="Height"/>
/// above its foot and on the far side of its face from the climber: a climber faces the
/// ladder, and goes over the top the way they face.
///
/// Placed by <c>Scripts/Fps/Ladder.cs</c> from the node's transform: the node stands at the
/// foot and looks the way its climber does, so its +Z is <see cref="Normal"/>.
/// </summary>
public readonly struct LadderSpan
{
	public readonly Vector3 Foot;

	/// <summary>Level and of unit length, out of the face: where the climber hangs.</summary>
	public readonly Vector3 Normal;

	public readonly float Width;

	/// <summary>From the foot up to the landing [m].</summary>
	public readonly float Height;

	public LadderSpan(Vector3 foot, Vector3 normal, float width, float height)
	{
		Foot = foot;
		normal.Y = 0f;
		Normal = normal.LengthSquared() > 1e-8f ? normal.Normalized() : Vector3.Back;
		Width = MathF.Max(width, 0f);
		Height = MathF.Max(height, 0f);
	}

	/// <summary>Along the rungs, to the right of a climber who faces the ladder.</summary>
	public Vector3 Right => Vector3.Up.Cross(Normal);

	public float HalfWidth => Width * 0.5f;

	/// <summary>The landing's height in the world.</summary>
	public float TopY => Foot.Y + Height;

	/// <summary>Where <paramref name="feet"/> are, measured off the ladder.</summary>
	public LadderPlace Place(Vector3 feet)
	{
		Vector3 d = feet - Foot;
		return new LadderPlace(d.Dot(Normal), d.Dot(Right), d.Y);
	}
}

/// <summary>A point in a ladder's own frame.</summary>
public readonly struct LadderPlace
{
	/// <summary>Out from the face, towards the climber's side; negative is over the landing.</summary>
	public readonly float Out;

	/// <summary>Along the rungs from their middle, to a climber's right.</summary>
	public readonly float Side;

	/// <summary>Above the foot.</summary>
	public readonly float Up;

	public LadderPlace(float outward, float side, float up)
	{
		Out = outward;
		Side = side;
		Up = up;
	}
}

/// <summary>One tick on a ladder: the velocity to move with, and whether it is the step over the top.</summary>
public readonly struct ClimbStep
{
	public readonly Vector3 Velocity;

	/// <summary>Level with the landing and going over onto it.</summary>
	public readonly bool Mounting;

	public ClimbStep(Vector3 velocity, bool mounting)
	{
		Velocity = velocity;
		Mounting = mounting;
	}
}

/// <summary>
/// Climbing a ladder (docs/LADDERS.md): when a body takes one, how it moves on it, and when
/// it lets go — as pure functions of where its feet are and the way it is asked to walk, so a
/// client's replay of a tick climbs exactly as the server did (docs/NETCODE.md §3.2), and so
/// all of it is answered by <c>dotnet test</c>.
///
/// Walking into a ladder takes it; on it, walking towards it climbs and walking away from it
/// goes down, whichever way the body looks — a body that faces the ladder climbs on forward
/// and comes down on back, one that backs down it from the landing comes down on forward.
/// Strafing does nothing: the body is held on the rungs' middle and a hand-span off the
/// face. At the top, still walking into it, the body goes over onto the landing; at the
/// foot, standing on the ground and not climbing, it lets go; a jump pushes off it.
///
/// Found by the geometry alone, not by an <c>Area3D</c>: an area's overlaps are the physics
/// server's, updated once a physics frame, and a reconciliation replays many ticks in one.
/// </summary>
public static class Ladders
{
	/// <summary>Up or down, metres per second, times the body's speed scale.</summary>
	public const float ClimbSpeed = 3f;

	/// <summary>
	/// How far out from the face a climber's feet are held: the player capsule's radius
	/// (0.5 m, <c>Scenes/fps_controller.tscn</c>) and 5 cm, so the body hangs clear of the wall
	/// or the scaffold edge behind the rungs.
	/// </summary>
	public const float HoldOutMeters = 0.55f;

	/// <summary>How hard the body is pulled back onto its holding line, per second of error.</summary>
	public const float HoldGain = 10f;

	/// <summary>The fastest the hold moves the body sideways or in and out [m/s].</summary>
	public const float MaxHoldSpeed = 2f;

	/// <summary>Farthest out from the face that a body can take the ladder from below [m].</summary>
	public const float ReachMeters = 0.9f;

	/// <summary>Farthest back onto the landing that a body can take the ladder from above [m].</summary>
	public const float TopReachMeters = 0.6f;

	/// <summary>Farthest out past the face, over the drop, a body standing on the landing can take it from.</summary>
	public const float TopEdgeMeters = 0.3f;

	/// <summary>Past either end of the rungs that a ladder can still be taken [m].</summary>
	public const float SideSlackMeters = 0.35f;

	/// <summary>
	/// How squarely a body has to walk at a ladder to take it: the cosine of 60°. A body
	/// walking past along a wall does not catch on it.
	/// </summary>
	public const float GrabDot = 0.5f;

	/// <summary>Less than this of the walk towards or away from the ladder is neither.</summary>
	public const float DeadZone = 0.2f;

	/// <summary>How far above the landing the feet are lifted before they go over onto it.</summary>
	public const float MountClearanceMeters = 0.1f;

	/// <summary>Over the top, onto the landing [m/s].</summary>
	public const float MountSpeed = 2.5f;

	/// <summary>How far past the face, onto the landing, the ladder is let go of: the capsule's radius and more.</summary>
	public const float MountDepthMeters = 0.65f;

	/// <summary>A body whose feet are this near the landing's height is on the landing, for a grab from above.</summary>
	public const float LandingBandMeters = 0.35f;

	/// <summary>A body whose feet are this near the foot's height is at the bottom.</summary>
	public const float FootBandMeters = 0.5f;

	/// <summary>A jump off a ladder: out from it, and up.</summary>
	/// <remarks>
	/// Out at 4.5 m/s because the fall after it has the ground's deceleration with nothing held
	/// (0.25 m/s a tick): it carries the body about 0.7 m clear of the hold line.
	/// </remarks>
	public const float JumpOffSpeed = 4.5f;

	public const float JumpOffLift = 3f;

	/// <summary>Within this of the top, the body counts as level with it [m].</summary>
	private const float LevelTolerance = 0.02f;

	/// <summary>
	/// The ladder a body takes this tick, if any: the nearest of those it is walking
	/// squarely into from below — on the ground, or coming down, never going up from a jump —
	/// or squarely off the landing at the top of. <paramref name="wish"/> is the level
	/// unit direction the body is asked to walk in (<see cref="Movement.Direction"/>), zero
	/// for none.
	/// </summary>
	public static bool TryGrab(ReadOnlySpan<LadderSpan> ladders, Vector3 feet, Vector3 wish, bool onFloor,
		bool rising, out int index)
	{
		index = -1;
		if (wish.LengthSquared() < 1e-6f)
		{
			return false;
		}

		float best = float.MaxValue;
		for (int i = 0; i < ladders.Length; i++)
		{
			ref readonly LadderSpan ladder = ref ladders[i];
			LadderPlace at = ladder.Place(feet);
			if (MathF.Abs(at.Side) > ladder.HalfWidth + SideSlackMeters)
			{
				continue;
			}

			float into = -wish.Dot(ladder.Normal);
			bool fromBelow = !rising && into >= GrabDot
				&& at.Out >= 0f && at.Out <= ReachMeters
				&& at.Up >= -FootBandMeters && at.Up <= ladder.Height - LandingBandMeters;
			bool fromAbove = onFloor && -into >= GrabDot
				&& at.Out >= -TopReachMeters && at.Out <= TopEdgeMeters
				&& MathF.Abs(at.Up - ladder.Height) <= LandingBandMeters;
			if (!fromBelow && !fromAbove)
			{
				continue;
			}

			float distance = Distance(at);
			if (distance < best)
			{
				best = distance;
				index = i;
			}
		}

		return index >= 0;
	}

	/// <summary>
	/// Whether a body with its feet at <paramref name="feet"/> is still on
	/// <paramref name="ladder"/>: in front of its face or not yet far enough over its landing,
	/// between the ends of its rungs, and between its foot and a little above its top.
	/// Everything <see cref="TryGrab"/> takes is held.
	/// </summary>
	public static bool Holds(in LadderSpan ladder, Vector3 feet)
	{
		LadderPlace at = ladder.Place(feet);
		return at.Out >= -MountDepthMeters && at.Out <= ReachMeters + 0.35f
			&& MathF.Abs(at.Side) <= ladder.HalfWidth + SideSlackMeters
			&& at.Up >= -FootBandMeters && at.Up <= ladder.Height + MountClearanceMeters + 0.4f;
	}

	/// <summary>The nearest ladder that <see cref="Holds"/> the body, or -1 for none: the one a climber is on.</summary>
	public static int Holding(ReadOnlySpan<LadderSpan> ladders, Vector3 feet)
	{
		int index = -1;
		float best = float.MaxValue;
		for (int i = 0; i < ladders.Length; i++)
		{
			if (!Holds(ladders[i], feet))
			{
				continue;
			}

			float distance = Distance(ladders[i].Place(feet));
			if (distance < best)
			{
				best = distance;
				index = i;
			}
		}

		return index;
	}

	/// <summary>
	/// How much of <paramref name="wish"/> is towards the ladder: +1 straight into it, which
	/// climbs, -1 straight away from it, which comes down, and 0 inside the dead zone.
	/// </summary>
	public static float Intent(in LadderSpan ladder, Vector3 wish)
	{
		float into = Math.Clamp(-wish.Dot(ladder.Normal), -1f, 1f);
		return MathF.Abs(into) < DeadZone ? 0f : into;
	}

	/// <summary>
	/// One tick on <paramref name="ladder"/>: up or down by the walk's <see cref="Intent"/>,
	/// never higher than <see cref="MountClearanceMeters"/> over the landing; held on the
	/// rungs' middle at <see cref="HoldOutMeters"/>; and, level with the top and still climbing,
	/// over onto the landing. <paramref name="speedScale"/> is the body's
	/// (<c>fps_controller.MoveSpeedScale</c>): a heavy kit climbs slower as it walks slower.
	/// No gravity: the ladder holds the body up.
	/// </summary>
	public static ClimbStep Climb(in LadderSpan ladder, Vector3 feet, Vector3 wish, float speedScale, float dt)
	{
		LadderPlace at = ladder.Place(feet);
		float intent = Intent(ladder, wish);
		float scale = MathF.Max(speedScale, 0f);
		float top = ladder.Height + MountClearanceMeters;

		float side = Math.Clamp(-at.Side * HoldGain, -MaxHoldSpeed, MaxHoldSpeed);
		bool mounting = intent > 0f && at.Up >= top - LevelTolerance;

		float outward;
		float upward;
		if (mounting)
		{
			outward = -MountSpeed * scale;
			upward = 0f;
		}
		else
		{
			upward = intent * ClimbSpeed * scale;
			if (upward > 0f && dt > 0f)
			{
				upward = MathF.Min(upward, MathF.Max(top - at.Up, 0f) / dt);
			}

			outward = Math.Clamp((HoldOutMeters - at.Out) * HoldGain, -MaxHoldSpeed, MaxHoldSpeed);
		}

		Vector3 velocity = (ladder.Normal * outward) + (ladder.Right * side) + (Vector3.Up * upward);
		return new ClimbStep(velocity, mounting);
	}

	/// <summary>
	/// Whether a climber lets go at the foot: standing on the ground near its height and not
	/// climbing. The landing at the top is let go of by going over it, which takes the body
	/// out of the ladder's reach (<see cref="Holds"/>).
	/// </summary>
	public static bool LetsGoAtFoot(in LadderSpan ladder, Vector3 feet, Vector3 wish, bool onFloor) =>
		onFloor && Intent(ladder, wish) <= 0f && ladder.Place(feet).Up <= FootBandMeters;

	/// <summary>A jump off a ladder: out from its face and up, whichever way the body looks.</summary>
	public static Vector3 JumpOff(in LadderSpan ladder) =>
		(ladder.Normal * JumpOffSpeed) + (Vector3.Up * JumpOffLift);

	// ---- for a bot (Scripts/Bots/BotPilot.cs) ------------------------------------

	/// <summary>
	/// The way a bot on <paramref name="ladder"/> walks to get to
	/// <paramref name="waypoint"/>: into the ladder, which climbs and goes over the top,
	/// when the waypoint is nearer the landing's height than the foot's; away from it,
	/// which comes down, when it is nearer the foot's. By height rather than by the level
	/// line to the waypoint, which swings round as the bot passes under or over it.
	/// </summary>
	public static Vector3 Steer(in LadderSpan ladder, Vector3 waypoint) =>
		MathF.Abs(waypoint.Y - ladder.TopY) < MathF.Abs(waypoint.Y - ladder.Foot.Y) ? -ladder.Normal : ladder.Normal;

	/// <summary>
	/// <paramref name="direction"/> with whatever would take a ladder taken out of it: for a
	/// bot whose path does not go up or down one, walking past its foot or along its landing
	/// towards some corner beyond it. The part towards the ladder (or off the landing) is
	/// removed, a little more widely than <see cref="TryGrab"/> would take it; what is left is
	/// renormalized, or zero when nothing is.
	/// </summary>
	public static Vector3 WithoutGrab(ReadOnlySpan<LadderSpan> ladders, Vector3 feet, Vector3 direction)
	{
		direction.Y = 0f;
		if (direction.LengthSquared() < 1e-6f)
		{
			return Vector3.Zero;
		}

		direction = direction.Normalized();
		const float margin = 0.15f;
		for (int i = 0; i < ladders.Length; i++)
		{
			ref readonly LadderSpan ladder = ref ladders[i];
			LadderPlace at = ladder.Place(feet);
			if (MathF.Abs(at.Side) > ladder.HalfWidth + SideSlackMeters + margin)
			{
				continue;
			}

			float into = -direction.Dot(ladder.Normal);
			bool nearFoot = at.Out >= -margin && at.Out <= ReachMeters + margin
				&& at.Up >= -FootBandMeters - margin && at.Up <= ladder.Height - LandingBandMeters + margin;
			bool nearTop = at.Out >= -TopReachMeters - margin && at.Out <= TopEdgeMeters + margin
				&& MathF.Abs(at.Up - ladder.Height) <= LandingBandMeters + margin;

			if (nearFoot && into > 0f)
			{
				direction += ladder.Normal * into;
			}
			else if (nearTop && into < 0f)
			{
				direction += ladder.Normal * into;
			}
		}

		direction.Y = 0f;
		return direction.LengthSquared() < 1e-4f ? Vector3.Zero : direction.Normalized();
	}

	/// <summary>How far a body is from a ladder's holding line, for choosing between two in reach.</summary>
	private static float Distance(in LadderPlace at) => MathF.Abs(at.Out - HoldOutMeters) + MathF.Abs(at.Side);
}
