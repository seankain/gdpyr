using System;
using Godot;

namespace Gdpyr.Sim;

/// <summary>
/// How a unit on foot holds itself (docs/TACTICS.md §5.8): two bits of the unit
/// snapshot's body byte, so the order is a protocol constant and the two values not
/// named here are reserved, read as <see cref="Standing"/>.
/// </summary>
public enum UnitPosture : byte
{
	Standing = 0,

	/// <summary>Down behind something: a lower hitbox and eye, half speed, and no fire — it stands to shoot.</summary>
	Crouched = 1,
}

/// <summary>
/// A unit's body as its definition writes it, standing and crouched, and what each
/// posture makes of it: how tall it is, where its eye is, the capsule a round hits,
/// the height a cover box must hide and how fast it walks (docs/TACTICS.md §5.8).
/// Engine-free, so the arithmetic the server shoots, sees and hides by is a question
/// for <c>dotnet test</c>.
/// </summary>
public readonly struct UnitBody
{
	/// <summary>How much of its walking speed a crouched unit keeps.</summary>
	public const float CrouchedSpeedFactor = 0.5f;

	public readonly float HeightMeters;
	public readonly float RadiusMeters;
	public readonly float EyeHeightMeters;

	/// <summary>Its height crouched; 0 for a body that cannot crouch — a vehicle, an aircraft.</summary>
	public readonly float CrouchHeightMeters;

	public readonly float CrouchEyeHeightMeters;

	public UnitBody(float heightMeters, float radiusMeters, float eyeHeightMeters, float crouchHeightMeters = 0f,
		float crouchEyeHeightMeters = 0f)
	{
		HeightMeters = MathF.Max(heightMeters, 0f);
		RadiusMeters = MathF.Max(radiusMeters, 0f);
		EyeHeightMeters = eyeHeightMeters;

		// A crouch that is no lower than standing, or has no eye below its top, is no crouch.
		bool can = crouchHeightMeters > 0f && crouchHeightMeters < HeightMeters
			&& crouchEyeHeightMeters > 0f && crouchEyeHeightMeters <= crouchHeightMeters;
		CrouchHeightMeters = can ? crouchHeightMeters : 0f;
		CrouchEyeHeightMeters = can ? crouchEyeHeightMeters : 0f;
	}

	public bool CanCrouch => CrouchHeightMeters > 0f;

	/// <summary>The posture it actually takes when asked for <paramref name="wanted"/>: standing, for one that cannot crouch.</summary>
	public UnitPosture Take(UnitPosture wanted) =>
		wanted == UnitPosture.Crouched && CanCrouch ? UnitPosture.Crouched : UnitPosture.Standing;

	public float Height(UnitPosture posture) => Take(posture) == UnitPosture.Crouched ? CrouchHeightMeters : HeightMeters;

	public float EyeHeight(UnitPosture posture) =>
		Take(posture) == UnitPosture.Crouched ? CrouchEyeHeightMeters : EyeHeightMeters;

	/// <summary>The capsule a round hits, from its feet: the posture's height, the one radius.</summary>
	public HitCapsule Hitbox(Vector3 feet, UnitPosture posture) =>
		HitCapsule.FromFeet(feet, Height(posture), RadiusMeters);

	/// <summary>
	/// How far up its body a cover box must hide it [m]: its middle, so a crouched unit
	/// is behind cover behind a box that would leave it standing in the open.
	/// </summary>
	public float ProtectMeters(UnitPosture posture) => Height(posture) * 0.5f;

	public float SpeedFactor(UnitPosture posture) =>
		Take(posture) == UnitPosture.Crouched ? CrouchedSpeedFactor : 1f;
}
