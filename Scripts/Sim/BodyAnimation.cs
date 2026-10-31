using System;

namespace Gdpyr.Sim;

/// <summary>
/// The clips a humanoid body is drawn with (Scripts/Characters/HumanoidRig.cs). Which
/// file clip each one is, and how fast it plays, is the rig's; this is only the
/// vocabulary <see cref="BodyAnimator"/> decides in. <see cref="Idle"/> to
/// <see cref="Land"/> are the legs' (<see cref="BodyMix.Legs"/>); the aims and
/// <see cref="Shoot"/> are the arms', over whatever the legs do; <see cref="Throw"/>
/// is the arms' and over the aim; <see cref="Death"/> is the whole body's.
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

	/// <summary>The weapon up and level.</summary>
	Aim,

	AimUp,
	AimDown,

	/// <summary>One round's recoil, played once and added to the aim wherever it points.</summary>
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
/// What a body is drawn doing this frame, layer by layer, as the rig's AnimationTree
/// takes it: the legs' clip under everything, the arms over the legs, a death over
/// both. The clips played once on top — a recoil, a throw — are not in it: they are
/// started, not held, and <see cref="BodyAnimator.ArmsSerial"/> says when.
/// </summary>
public struct BodyMix
{
	/// <summary>What the legs do, and the whole body where nothing is layered over it: <see cref="BodyClip.Idle"/> to <see cref="BodyClip.Land"/>.</summary>
	public BodyClip Legs;

	/// <summary>How far the chest, arms and head are given to the weapon [0, 1]: the aim and its recoil, over the legs.</summary>
	public float Arms;

	/// <summary>Where the arms point between the low, level and high aims [-1, 1] (<see cref="BodyAnimator.AimBlend"/>).</summary>
	public float AimBlend;

	/// <summary>How far the death has taken the body over [0, 1].</summary>
	public float Death;
}

/// <summary>
/// How a humanoid body is posed, frame by frame, from its pose and from the things
/// that happen to it — a shot, a throw, a death. Presentation only: nothing the
/// simulation decides reads it, and a dedicated server never makes one.
///
/// Layers rather than one clip at a time, so a body runs and shoots at once. The legs
/// are chosen by priority — the air over the ground, moving over standing — and the
/// arms are raised over whatever the legs do while the body aims or has just fired,
/// with the aim blended between low, level and high by the pitch and each round's
/// recoil added on top. A throw is the arms' too, over the aim; a death is
/// everything's. Engine-free, so what a body does when it lands running, or fires
/// mid-stride, is a question for <c>dotnet test</c>.
/// </summary>
public sealed class BodyAnimator
{
	/// <summary>Below this a body is standing [m/s]; interpolation jitter is not a step.</summary>
	public const float StandingSpeed = 0.3f;

	/// <summary>From here a body runs rather than walks [m/s]: between the walk's stride and the jog's.</summary>
	public const float RunSpeed = 2.2f;

	/// <summary>From here a body sprints whatever its state says [m/s]: past a jog's 5.4.</summary>
	public const float SprintSpeed = 6.6f;

	/// <summary>
	/// The pitch the high aim is drawn at [rad]: 69.3°, the shoulder-to-hand line of
	/// UAL1_Standard.glb's Pistol_Aim_Up against the level aim's −0.6°.
	/// </summary>
	public const float AimUpRadians = 1.21f;

	/// <summary>The pitch below the horizontal the low aim is drawn at [rad]: 78.8°, Pistol_Aim_Down's.</summary>
	public const float AimDownRadians = 1.375f;

	/// <summary>How long a body that fired keeps its weapon up [s].</summary>
	public const float AimHoldSeconds = 1.5f;

	/// <summary>How long the arms take to come up to the aim, or go back down from it [s].</summary>
	public const float ArmsBlendSeconds = 0.15f;

	/// <summary>How long a death takes to take the body over [s].</summary>
	public const float DeathBlendSeconds = 0.1f;

	/// <summary>Less time than this in the air is a step off a kerb, not a landing [s].</summary>
	public const float MinAirSeconds = 0.25f;

	/// <summary>The bounds of how far a stride is sped up or slowed to meet the ground speed.</summary>
	public const float MinStrideScale = 0.5f;

	public const float MaxStrideScale = 2f;

	private readonly BodyClipTimes _times;

	private BodyClip _legsAction;
	private float _legsLeft;
	private BodyClip _armsAction;
	private float _armsLeft;
	private float _aimLeft;
	private float _airSeconds;
	private bool _wasAirborne;
	private float _deadSeconds;
	private float _arms;
	private float _aimBlend;
	private float _death;

	public BodyAnimator(BodyClipTimes times)
	{
		_times = times;
	}

	public bool IsDead { get; private set; }

	/// <summary>Dead, and the death clip not yet over: a corpse still worth drawing.</summary>
	public bool IsDying => IsDead && _deadSeconds < _times.Death;

	/// <summary>The legs' clip played once that is running, if <see cref="IsLegsActing"/>: a push-off or a landing.</summary>
	public BodyClip LegsAction => _legsAction;

	public bool IsLegsActing => _legsLeft > 0f;

	/// <summary>Counts every push-off and landing that starts, so the rig can play one begun again from the top.</summary>
	public int LegsSerial { get; private set; }

	/// <summary>The arms' clip played once that is running, if <see cref="IsArmsActing"/>: a recoil or a throw.</summary>
	public BodyClip ArmsAction => _armsAction;

	public bool IsArmsActing => _armsLeft > 0f;

	/// <summary>
	/// Counts every recoil and throw that starts, so the rig can start the one-shot
	/// that plays it — and a throw begun again from the top.
	/// </summary>
	public int ArmsSerial { get; private set; }

	/// <summary>
	/// A round fired. Raises the weapon and keeps it up, and plays the recoil unless the
	/// arms are already playing something once — a recoil does not cut a throw short,
	/// and automatic fire does not restart one recoil on every round.
	/// </summary>
	public void Shoot()
	{
		if (IsDead)
		{
			return;
		}

		_aimLeft = AimHoldSeconds;
		if (!IsArmsActing)
		{
			BeginArms(BodyClip.Shoot, _times.Shoot);
		}
	}

	/// <summary>A grenade thrown: over the aim and the legs, and from the top if one was already in the air.</summary>
	public void Throw()
	{
		if (IsDead)
		{
			return;
		}

		_aimLeft = 0f;
		BeginArms(BodyClip.Throw, _times.Throw);
	}

	public void Die()
	{
		if (IsDead)
		{
			return;
		}

		IsDead = true;
		_deadSeconds = 0f;
		_legsLeft = 0f;
		_armsLeft = 0f;
		_aimLeft = 0f;
	}

	/// <summary>Back on its feet at once: a respawn, with nothing of the last life carried over.</summary>
	public void Revive()
	{
		IsDead = false;
		_deadSeconds = 0f;
		_legsLeft = 0f;
		_armsLeft = 0f;
		_aimLeft = 0f;
		_airSeconds = 0f;
		_wasAirborne = false;
		_arms = 0f;
		_aimBlend = 0f;
		_death = 0f;
	}

	/// <summary>Advances the clocks by <paramref name="dt"/> and says how to pose the body.</summary>
	public BodyMix Step(in BodyPose pose, float dt)
	{
		dt = MathF.Max(dt, 0f);
		if (IsDead)
		{
			// The legs settle to standing and the arms come down under the death, so a
			// revive — which is a teleport — has nothing to fade out of.
			_deadSeconds += dt;
			_death = Toward(_death, 1f, dt, DeathBlendSeconds);
			_arms = Toward(_arms, 0f, dt, ArmsBlendSeconds);
			return new BodyMix { Legs = BodyClip.Idle, Arms = _arms, AimBlend = _aimBlend, Death = _death };
		}

		_aimLeft = MathF.Max(_aimLeft - dt, 0f);
		_legsLeft = MathF.Max(_legsLeft - dt, 0f);
		_armsLeft = MathF.Max(_armsLeft - dt, 0f);

		BodyClip legs = StepLegs(pose, dt);

		// Up while it aims or has just fired, but not on a sprint, which carries the
		// weapon; a round fired on one raises it for the recoil all the same.
		bool shooting = IsArmsActing && _armsAction == BodyClip.Shoot;
		bool raised = shooting || ((pose.Aiming || _aimLeft > 0f) && legs != BodyClip.Sprint);
		_arms = Toward(_arms, raised ? 1f : 0f, dt, ArmsBlendSeconds);
		_aimBlend = AimBlend(pose.AimPitch);

		return new BodyMix { Legs = legs, Arms = _arms, AimBlend = _aimBlend, Death = _death };
	}

	/// <summary>
	/// Where a weapon pitched at <paramref name="pitchRadians"/> points between the low
	/// (-1), level (0) and high (+1) aims: in proportion up to the pitch each is drawn at,
	/// and held at it beyond.
	/// </summary>
	public static float AimBlend(float pitchRadians) => pitchRadians >= 0f
		? MathF.Min(pitchRadians / AimUpRadians, 1f)
		: MathF.Max(pitchRadians / AimDownRadians, -1f);

	/// <summary>
	/// How fast to play a stride authored at <paramref name="clipSpeed"/> [m/s] so the
	/// feet keep up with <paramref name="groundSpeed"/>, within
	/// [<see cref="MinStrideScale"/>, <see cref="MaxStrideScale"/>]. 1 for a clip that
	/// does not travel.
	/// </summary>
	public static float StrideScale(float groundSpeed, float clipSpeed) =>
		clipSpeed > 0f ? Math.Clamp(groundSpeed / clipSpeed, MinStrideScale, MaxStrideScale) : 1f;

	/// <summary>The legs' clip, by priority: the air over the ground, moving over standing.</summary>
	private BodyClip StepLegs(in BodyPose pose, float dt)
	{
		if (pose.Airborne)
		{
			if (!_wasAirborne && pose.Jumped)
			{
				BeginLegs(BodyClip.JumpStart, _times.JumpStart);
			}

			_airSeconds += dt;
			_wasAirborne = true;
		}
		else if (_wasAirborne)
		{
			if (IsLegsActing && _legsAction == BodyClip.JumpStart)
			{
				_legsLeft = 0f;
			}

			if (_airSeconds >= MinAirSeconds)
			{
				BeginLegs(BodyClip.Land, _times.Land);
			}

			_airSeconds = 0f;
			_wasAirborne = false;
		}

		if (pose.Airborne)
		{
			return IsLegsActing && _legsAction == BodyClip.JumpStart ? BodyClip.JumpStart : BodyClip.Airborne;
		}

		if (IsLegsActing && _legsAction == BodyClip.Land)
		{
			// Landing on the run carries on running: an absorbed landing with the legs
			// planted would slide across the ground at the run's speed.
			if (pose.GroundSpeed < RunSpeed)
			{
				return BodyClip.Land;
			}

			_legsLeft = 0f;
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

		return pose.GroundSpeed >= StandingSpeed ? BodyClip.Walk : BodyClip.Idle;
	}

	private void BeginLegs(BodyClip action, float seconds)
	{
		_legsAction = action;
		_legsLeft = seconds;
		LegsSerial++;
	}

	private void BeginArms(BodyClip action, float seconds)
	{
		_armsAction = action;
		_armsLeft = seconds;
		ArmsSerial++;
	}

	/// <summary>Moves <paramref name="value"/> toward <paramref name="target"/> at a rate that covers 0 to 1 in <paramref name="seconds"/>.</summary>
	private static float Toward(float value, float target, float dt, float seconds)
	{
		float step = seconds > 0f ? dt / seconds : 1f;
		return value < target ? MathF.Min(value + step, target) : MathF.Max(value - step, target);
	}
}
