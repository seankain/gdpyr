using Gdpyr.Sim;
using Godot;

namespace Gdpyr.Fps;

/// <summary>
/// One weapon: what it looks like in the viewmodel, and what it does.
///
/// Was <c>Weapons</c> before M2, holding only the viewmodel and sway settings the
/// single-player tutorial needed. The combat half added here — fire mode, rate,
/// magazine, reload, and the projectile it launches — is what the simulation
/// reads, via <see cref="ToStats"/>, and it is deliberately expressed in the units
/// a designer tunes in (RPM, seconds, metres) rather than in ticks. The conversion
/// to ticks happens once, in <see cref="WeaponStats"/>, because a weapon's cadence
/// has to be a whole number of ticks to be replayable (docs/NETCODE.md §3.1).
/// </summary>
[GlobalClass]
public partial class WeaponDefinition : Resource
{
	public enum WeaponSlot
	{
		Melee,
		Sidearm,
		Large
	}

	[Export]
	public StringName Name;
	[Export]
	public WeaponSlot Slot;
	[Export]
	[ExportCategory("Weapon Orientation")]
	public Vector3 Position;
	[Export]
	public Vector3 Rotation;

	[Export]
	public Vector3 Scale;

	[Export]
	[ExportCategory("Visual Settings")]
	public Mesh Mesh;
	[Export]
	public bool Shadow;

	/// <summary>
	/// Damage a melee swing does. A ranged weapon's damage lives on its
	/// <see cref="Projectile"/>, because that is what actually arrives.
	/// </summary>
	[Export]
	public float DamageAmount;

	[ExportCategory("Combat")]

	[Export]
	public FireMode FireMode = FireMode.Semi;

	/// <summary>
	/// Rate of fire. Quantized to whole ticks by <see cref="WeaponStats.TicksPerShot"/>,
	/// so at 60 Hz the authored value is rounded to the nearest achievable cadence.
	/// </summary>
	[Export]
	public float RoundsPerMinute = 400f;

	/// <summary>Rounds per magazine, or 0 for a weapon with nothing to run out of.</summary>
	[Export]
	public int MagazineSize = 12;

	[Export]
	public float ReloadSeconds = 1.5f;

	/// <summary>True for a weapon resolved as a swing rather than as a projectile.</summary>
	[Export]
	public bool IsMelee;

	/// <summary>Reach of a swing, from the eye.</summary>
	[Export]
	public float MeleeRangeMeters = 2.5f;

	/// <summary>
	/// Sweep radius of a swing. A hammer is a shapecast and not a ray: a melee
	/// attack that has to be aimed like a rifle is a melee attack nobody lands
	/// (docs/IMPLEMENTATION_PLAN.md §M2).
	/// </summary>
	[Export]
	public float MeleeSweepRadiusMeters = 0.35f;

	/// <summary>
	/// Half-angle of the accuracy cone, in degrees. Zero for everything M2 shipped:
	/// those four were tuned without one, and widening them is a playtest decision
	/// rather than a side effect of M3 needing cones for units
	/// (docs/IMPLEMENTATION_PLAN.md §7).
	/// </summary>
	[Export]
	public float SpreadDegrees;

	/// <summary>What this weapon launches. Null for melee.</summary>
	[Export]
	public ProjectileDefinition Projectile;

	[ExportCategory("Optics")]

	/// <summary>
	/// How much the view is magnified with the sights all the way up. 1 — the
	/// default — is a weapon that does not aim at all, which is what the mounted
	/// guns and the hammer are: a player pressing the aim button with one of those
	/// up gets nothing, rather than a magnification of one applied to everything.
	///
	/// A magnification rather than an aimed field of view, so that the number means
	/// the same thing whatever FOV the camera is carried at
	/// (<see cref="Ads.FovDegrees"/>).
	/// </summary>
	[Export]
	public float AimMagnification = 1f;

	/// <summary>
	/// Time from the hip to fully aimed, and back. Quantized to whole ticks by
	/// <see cref="WeaponStats.SecondsToTicks"/> for the reason the rate of fire is:
	/// a raise that is not a whole number of ticks cannot be reproduced from the
	/// recorded frames.
	/// </summary>
	[Export]
	public float AimSeconds = 0.2f;

	/// <summary>
	/// True for a weapon aimed through an optic. The scope's surround takes the
	/// screen once it is most of the way up and the viewmodel goes with it; a weapon
	/// with this false simply brings its own sights into the middle of the screen.
	/// </summary>
	[Export]
	public bool Scoped;

	/// <summary>
	/// Where the viewmodel is held with the sights up, in the same camera-relative
	/// space <see cref="Position"/> is authored in. Presentation only, like
	/// <see cref="Position"/> itself.
	/// </summary>
	[Export]
	public Vector3 AimPosition;

	/// <summary>The viewmodel's rotation with the sights up [rad], as <see cref="Rotation"/> is.</summary>
	[Export]
	public Vector3 AimRotation;

	[ExportCategory("WeaponSway")]
	[Export]
	public Vector2 SwayMin = new Vector2(-20,20);
	[ExportCategory("WeaponSway")]
	[Export]
	public Vector2 SwayMax = new Vector2(-20,20);
	[ExportCategory("WeaponSway")]
	[Export]
	public float SwayAmountPosition = 0.1f;
	[ExportCategory("WeaponSway")]
	[Export]
	public float SwayAmountRotation = 30.0f;

	[ExportCategory("WeaponSway")]
	[Export]
	public float IdleSwayAdjustment = 10.0f;

	[ExportCategory("WeaponSway")]
	[Export]
	public float IdleSwayRotationStrength = 300.0f;

	[ExportCategory("WeaponSway")]
	[Export]
	public float RandomSwayAmount = 5.0f;

	/// <summary>
	/// What raising this weapon's sights does, in ticks and magnification
	/// (<see cref="Ads"/>). A melee weapon has nothing to raise whatever else is
	/// authored on it, and so does anything left at a magnification of one.
	/// </summary>
	public AimStats ToAimStats() =>
		IsMelee || AimMagnification <= 1f
			? AimStats.None
			: new AimStats(AimMagnification, WeaponStats.SecondsToTicks(AimSeconds), Scoped);

	/// <summary>The simulation's view of this weapon: everything in ticks and rounds.</summary>
	public WeaponStats ToStats() => new(
		FireMode,
		WeaponStats.TicksPerShot(RoundsPerMinute),
		MagazineSize,
		WeaponStats.SecondsToTicks(ReloadSeconds),
		DamageAmount,
		IsMelee,
		MeleeRangeMeters,
		MeleeSweepRadiusMeters,
		Spread.ConeFromDegrees(SpreadDegrees));
}
