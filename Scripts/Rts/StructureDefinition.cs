using Gdpyr.Sim;
using Godot;

namespace Gdpyr.Rts;

/// <summary>
/// One kind of structure a builder can put up, as an editable resource: what it
/// costs, how long it takes, how much it can take, how big it is and — for the two
/// with a gun — what it shoots with and how (docs/NETCODE.md §10.5).
///
/// The same arrangement as <see cref="UnitDefinition"/>: three files rather than
/// three classes, because a pillbox and a tower differ in their numbers and not in
/// their behaviour. The gun's numbers are the ones a barracks defence authors
/// (<see cref="DefenseMount"/>), because the gun is one: it runs through the same
/// <see cref="DefenseSim"/> rules, and only the thing it is mounted on differs.
/// </summary>
[GlobalClass]
public partial class StructureDefinition : Resource
{
	[Export]
	public StringName Name = "structure";

	[ExportCategory("Economy")]

	/// <summary>Points the strategist pays when the site is placed. Not refunded if it is knocked down.</summary>
	[Export]
	public int Cost = 100;

	/// <summary>Seconds one builder takes to put it up. Two take half as long, up to four.</summary>
	[Export]
	public float BuildSeconds = 10f;

	[ExportCategory("Body")]

	[Export]
	public float MaxHealth = 500f;

	/// <summary>
	/// What fraction of a bullet's damage it takes; explosives always do the whole
	/// (<see cref="Armour"/>). The number that decides whether rifles or launchers
	/// are the answer to it: the pillbox and the tower are at zero, which small arms
	/// cannot hurt at all, and a wall of sandbags takes a share (docs/NETCODE.md §10.6).
	/// </summary>
	[Export]
	public float BulletDamageScale = 0.25f;

	/// <summary>True for a structure bullets do nothing to.</summary>
	public bool IsBulletProof => Armour.IsBulletProof(BulletDamageScale);

	/// <summary>The main box: across the front, front to back, and up.</summary>
	[Export]
	public Vector3 BodySize = new(3f, 2f, 3f);

	/// <summary>A second box on top of the first — the tower's cabin. Zero for none.</summary>
	[Export]
	public Vector3 TopSize = Vector3.Zero;

	/// <summary>How far above the ground the second box starts.</summary>
	[Export]
	public float TopElevationMeters;

	[ExportCategory("Senses and weapons")]

	/// <summary>
	/// How far the side that owns it can see from it (docs/NETCODE.md §6.2). Zero is a
	/// structure that sees nothing, which is what a wall of sandbags is.
	/// </summary>
	[Export]
	public float SensorRadiusMeters;

	/// <summary>Where it looks from, above its base: the top of the pillbox, the tower's marksman.</summary>
	[Export]
	public float EyeHeightMeters = 2f;

	/// <summary>Which catalog weapon it fires, or -1 for none.</summary>
	[Export]
	public int WeaponId = -1;

	/// <summary>Where its rounds leave from, above its base. A pillbox fires through the slit in whichever wall faces the target.</summary>
	[Export]
	public float MuzzleHeightMeters = 1.5f;

	/// <summary>Furthest it engages from, eye to target. Kept at or under the sensor.</summary>
	[Export]
	public float RangeMeters = 40f;

	/// <summary>Seconds between picking somebody out and firing at them.</summary>
	[Export]
	public float ReactionSeconds = 0.6f;

	[Export]
	public float TraverseDegreesPerSecond = 120f;

	[Export]
	public float OnTargetDegrees = 2f;

	/// <summary>Half-angle of its accuracy cone, as a unit's and a defence's are.</summary>
	[Export]
	public float AccuracyConeDegrees = 2f;

	[ExportCategory("Presentation")]

	[Export]
	public Color Color = new(0.55f, 0.55f, 0.52f);

	public bool IsArmed => WeaponId >= 0 && WeaponId < SimConfig.MaxWeaponDefinitions;

	public byte WeaponDefinitionId => (byte)Mathf.Clamp(WeaponId, 0, SimConfig.MaxWeaponDefinitions - 1);

	public int BuildTicks => Mathf.Max(WeaponStats.SecondsToTicks(BuildSeconds), 1);

	public float AccuracyConeRadians => Spread.ConeFromDegrees(AccuracyConeDegrees);

	/// <summary>The boxes every peer builds its collider from and the server tests rounds against.</summary>
	public StructureShape ToShape() => new(
		StructureBox.Standing(BodySize.X, BodySize.Z, BodySize.Y),
		TopSize.X > 0f && TopSize.Y > 0f && TopSize.Z > 0f
			? StructureBox.Standing(TopSize.X, TopSize.Z, TopSize.Y, TopElevationMeters)
			: default);

	/// <summary>The gun's rules, in the units the simulation runs in, as a defence's are built.</summary>
	public DefenseTraits ToGunTraits() => new(
		DefenseKind.Gun,
		Mathf.Min(RangeMeters, SensorRadiusMeters > 0f ? SensorRadiusMeters : RangeMeters),
		0f,
		WeaponStats.SecondsToTicks(ReactionSeconds),
		Mathf.DegToRad(TraverseDegreesPerSecond) * SimConfig.TickDelta,
		Mathf.DegToRad(OnTargetDegrees));
}
