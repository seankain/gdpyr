using System;

namespace Gdpyr.Sim;

/// <summary>
/// What a humanoid body is drawn doing (Scripts/Characters/HumanoidRig.cs): one clip
/// at a time, full body. Which file clip each one is, and how fast it plays, is the
/// rig's; this is only the vocabulary <see cref="BodyAnimator"/> decides in.
/// </summary>
public enum BodyClip : byte
{
	Idle,
	Walk,
	Run,
	Sprint,
	CrouchIdle,
	CrouchWalk,

	/// <summary>The push off the ground, played once at the start of a jump.</summary>
	JumpStart,

	/// <summary>In the air, from a jump that has finished its push or from a fall.</summary>
	Airborne,

	/// <summary>Taking a landing after long enough in the air, played once.</summary>
	Land,

	/// <summary>Standing with the weapon up and level.</summary>
	Aim,

	AimUp,
	AimDown,

	/// <summary>One round's recoil, played once over the aim.</summary>
	Shoot,

	/// <summary>An overhand throw, played once: a hand grenade.</summary>
	Throw,

	/// <summary>Played once and held until the body is revived.</summary>
	Death,
}

/// <summary>
/// What a body's owner knows about it this frame. The ground speed is measured by
/// the rig, from where the body is drawn, rather than read from a velocity: a client's
/// remote players and units have only interpolated positions (docs/NETCODE.md §3.3, §6.1).
/// </summary>
public struct BodyPose
{
	/// <summary>Horizontal speed [m/s].</summary>
	public float GroundSpeed;

	public bool Crouched;

	/// <summary>Off the ground, whether by a jump or a fall.</summary>
	public bool Airborne;

	/// <summary>Off the ground by a jump, which pushes off first; a fall does not.</summary>
	public bool Jumped;

	/// <summary>Running flat out, which shows the sprint at a speed a heavy kit leaves below the sprint's own.</summary>
	public bool Sprinting;

	/// <summary>Holding a weapon on something: a unit engaging.</summary>
	public bool Aiming;

	/// <summary>Where the weapon points above the horizontal [rad]; positive looks up, as <see cref="Aim"/> has it.</summary>
	public float AimPitch;
}

/// <summary>How long each clip that plays once runs as played [s]: its length over its rate.</summary>
public readonly struct BodyClipTimes
{
	public readonly float JumpStart;
	public readonly float Land;
	public readonly float Shoot;
	public readonly float Throw;
	public readonly float Death;

	public BodyClipTimes(float jumpStart, float land, float shoot, float @throw, float death)
	{
		JumpStart = MathF.Max(jumpStart, 0f);
		Land = MathF.Max(land, 0f);
		Shoot = MathF.Max(shoot, 0f);
		Throw = MathF.Max(@throw, 0f);
		Death = MathF.Max(death, 0f);
	}
}

/// <summary>
/// Which clip a humanoid body shows, frame by frame, from its pose and from the
/// things that happen to it — a shot, a throw, a death. Presentation only: nothing the
/// simulation decides reads it, and a dedicated server never makes one.
///
/// One clip at a time, so priorities rather than layers: dead over everything, a throw
/// over moving, the air over the ground, moving over standing. A shot shows only on a
/// body standing still; one that is moving keeps its legs, and the shot keeps it aiming
/// for <see cref="AimHoldSeconds"/> once it stops. Engine-free, so what a body does when
/// it lands running is a question for <c>dotnet test</c>.
/// </summary>
public sealed class BodyAnimator
{
	/// <summary>Below this a body is standing [m/s]; interpolation jitter is not a step.</summary>
	public const float StandingSpeed = 0.3f;

	/// <summary>From here a body runs rather than walks [m/s]: between the walk's stride and the jog's.</summary>
	public const float RunSpeed = 2.2f;

	/// <summary>From here a body sprints whatever its state says [m/s]: past a jog's 5.4.</summary>
	public const float SprintSpeed = 6.6f;

	/// <summary>How far up or down a weapon points before the aim is drawn high or low [rad]: 20°.</summary>
	public const float AimPitchRadians = 0.35f;

	/// <summary>How long a body that fired keeps its weapon up once it stands still [s].</summary>
	public const float AimHoldSeconds = 1.5f;

	/// <summary>Less time than this in the air is a step off a kerb, not a landing [s].</summary>
	public const float MinAirSeconds = 0.25f;

	/// <summary>The bounds of how far a stride is sped up or slowed to meet the ground speed.</summary>
	public const float MinStrideScale = 0.5f;

	public const float MaxStrideScale = 2f;

	private readonly BodyClipTimes _times;

	private BodyClip _action;
	private float _actionLeft;
	private float _aimLeft;
	private float _airSeconds;
	private bool _wasAirborne;
	private float _deadSeconds;

	public BodyAnimator(BodyClipTimes times)
	{
		_times = times;
	}

	public bool IsDead { get; private set; }

	/// <summary>Dead, and the death clip not yet over: a corpse still worth drawing.</summary>
	public bool IsDying => IsDead && _deadSeconds < _times.Death;

	/// <summary>The clip played once that is running, if <see cref="IsActing"/>.</summary>
	public BodyClip Action => _action;

	public bool IsActing => _actionLeft > 0f;

	/// <summary>
	/// Counts every clip played once that starts, so the rig can tell a throw begun again
	/// from the throw it is already showing and play it from the top.
	/// </summary>
	public int ActionSerial { get; private set; }

	/// <summary>
	/// A round fired. Keeps the weapon up, and shows the recoil unless the body is
	/// already doing something it plays once — a recoil does not cut a throw short, and
	/// automatic fire does not restart one recoil on every round.
	/// </summary>
	public void Shoot()
	{
		if (IsDead)
		{
			return;
		}

		_aimLeft = AimHoldSeconds;
		if (!IsActing)
		{
			Begin(BodyClip.Shoot, _times.Shoot);
		}
	}

	/// <summary>A grenade thrown: over anything but a death, and from the top if one was already in the air.</summary>
	public void Throw()
	{
		if (IsDead)
		{
			return;
		}

		_aimLeft = 0f;
		Begin(BodyClip.Throw, _times.Throw);
	}

	public void Die()
	{
		if (IsDead)
		{
			return;
		}

		IsDead = true;
		_deadSeconds = 0f;
		_actionLeft = 0f;
	}

	/// <summary>Back on its feet: a respawn, with nothing of the last life carried over.</summary>
	public void Revive()
	{
		IsDead = false;
		_deadSeconds = 0f;
		_actionLeft = 0f;
		_aimLeft = 0f;
		_airSeconds = 0f;
		_wasAirborne = false;
	}

	/// <summary>Advances the clocks by <paramref name="dt"/> and says which clip to show.</summary>
	public BodyClip Step(in BodyPose pose, float dt)
	{
		dt = MathF.Max(dt, 0f);
		if (IsDead)
		{
			_deadSeconds += dt;
			return BodyClip.Death;
		}

		_aimLeft = MathF.Max(_aimLeft - dt, 0f);
		_actionLeft = MathF.Max(_actionLeft - dt, 0f);
		bool throwing = IsActing && _action == BodyClip.Throw;

		if (pose.Airborne)
		{
			if (!_wasAirborne && pose.Jumped && !throwing)
			{
				Begin(BodyClip.JumpStart, _times.JumpStart);
			}

			_airSeconds += dt;
			_wasAirborne = true;
		}
		else if (_wasAirborne)
		{
			if (IsActing && _action == BodyClip.JumpStart)
			{
				_actionLeft = 0f;
			}

			if (_airSeconds >= MinAirSeconds && !throwing)
			{
				Begin(BodyClip.Land, _times.Land);
			}

			_airSeconds = 0f;
			_wasAirborne = false;
		}

		if (throwing)
		{
			return BodyClip.Throw;
		}

		if (pose.Airborne)
		{
			return IsActing && _action == BodyClip.JumpStart ? BodyClip.JumpStart : BodyClip.Airborne;
		}

		if (IsActing && _action == BodyClip.Land)
		{
			// Landing on the run carries on running: an absorbed landing with the legs
			// planted would slide across the ground at the run's speed.
			if (pose.GroundSpeed < RunSpeed)
			{
				return BodyClip.Land;
			}

			_actionLeft = 0f;
		}

		if (pose.Crouched)
		{
			return pose.GroundSpeed >= StandingSpeed ? BodyClip.CrouchWalk : BodyClip.CrouchIdle;
		}

		if (pose.GroundSpeed >= SprintSpeed || (pose.Sprinting && pose.GroundSpeed >= RunSpeed))
		{
			return BodyClip.Sprint;
		}

		if (pose.GroundSpeed >= RunSpeed)
		{
			return BodyClip.Run;
		}

		if (pose.GroundSpeed >= StandingSpeed)
		{
			return BodyClip.Walk;
		}

		if (IsActing && _action == BodyClip.Shoot)
		{
			return BodyClip.Shoot;
		}

		if (pose.Aiming || _aimLeft > 0f)
		{
			return pose.AimPitch >= AimPitchRadians ? BodyClip.AimUp
				: pose.AimPitch <= -AimPitchRadians ? BodyClip.AimDown
				: BodyClip.Aim;
		}

		return BodyClip.Idle;
	}

	/// <summary>
	/// How fast to play a stride authored at <paramref name="clipSpeed"/> [m/s] so the
	/// feet keep up with <paramref name="groundSpeed"/>, within
	/// [<see cref="MinStrideScale"/>, <see cref="MaxStrideScale"/>]. 1 for a clip that
	/// does not travel.
	/// </summary>
	public static float StrideScale(float groundSpeed, float clipSpeed) =>
		clipSpeed > 0f ? Math.Clamp(groundSpeed / clipSpeed, MinStrideScale, MaxStrideScale) : 1f;

	private void Begin(BodyClip action, float seconds)
	{
		_action = action;
		_actionLeft = seconds;
		ActionSerial++;
	}
}
