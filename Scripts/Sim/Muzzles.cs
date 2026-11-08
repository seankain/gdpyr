using System;
using Godot;

namespace Gdpyr.Sim;

/// <summary>
/// The three things drawn at a gun when it fires (docs/MUZZLE_EFFECTS.md): a flash, a
/// puff of smoke and a spent case thrown out of the side. Flags, because how far the
/// viewer is from the muzzle decides each one on its own (<see cref="MuzzleRanges"/>).
/// </summary>
[Flags]
public enum MuzzleParts : byte
{
	None = 0,

	Flash = 1,

	Smoke = 2,

	Casing = 4,

	All = Flash | Smoke | Casing,
}

/// <summary>
/// The numbers one <see cref="ShotKind"/>'s muzzle effects are drawn with
/// (docs/MUZZLE_EFFECTS.md §3). Sizes are metres, durations seconds from the shot, speeds
/// metres a second. Each part is optional: a flash of no length, no smoke puffs or a case
/// of no length leaves that part out.
/// </summary>
public readonly struct MuzzleProfile
{
	// ---- flash -------------------------------------------------------------------------

	/// <summary>How far the flame reaches out of the muzzle along the barrel.</summary>
	public float FlashLength { get; init; }

	/// <summary>How wide it is across the barrel, and across the star seen end on.</summary>
	public float FlashWidth { get; init; }

	/// <summary>How long it is drawn: two or three frames for a rifle.</summary>
	public float FlashSeconds { get; init; }

	/// <summary>How bright the light it throws on what is round it is at the shot (<c>OmniLight3D.LightEnergy</c>).</summary>
	public float LightEnergy { get; init; }

	/// <summary>How far that light reaches.</summary>
	public float LightRange { get; init; }

	public Color FlashColor { get; init; }

	// ---- smoke -------------------------------------------------------------------------

	/// <summary>Puffs a shot leaves; 0 for none.</summary>
	public int SmokePuffs { get; init; }

	/// <summary>A puff's radius as it leaves the muzzle; it grows to <see cref="Muzzles.SmokeGrowth"/> times this.</summary>
	public float SmokeRadius { get; init; }

	public float SmokeSeconds { get; init; }

	/// <summary>How fast a puff climbs.</summary>
	public float SmokeRiseSpeed { get; init; }

	/// <summary>How fast a puff leaves the muzzle down the barrel's line; the air takes it off within a quarter of a second.</summary>
	public float SmokeForwardSpeed { get; init; }

	/// <summary>How opaque a puff is at its thickest, 0 to 1.</summary>
	public float SmokeAlpha { get; init; }

	public Color SmokeColor { get; init; }

	// ---- the spent case ----------------------------------------------------------------

	/// <summary>How long a case is; 0 for a weapon that throws none out — a launcher, a cannon, a mortar.</summary>
	public float CasingLength { get; init; }

	public float CasingDiameter { get; init; }

	public Color CasingColor { get; init; }

	/// <summary>How far behind the muzzle the ejection port is, along the barrel.</summary>
	public float PortBackMeters { get; init; }

	/// <summary>How fast a case leaves the port to the gun's right.</summary>
	public float EjectSpeed { get; init; }

	/// <summary>How fast it leaves upward, in the gun's frame; below zero throws it downward, as a belt-fed gun does.</summary>
	public float EjectUpSpeed { get; init; }

	/// <summary>How fast it leaves back towards the shooter.</summary>
	public float EjectBackSpeed { get; init; }

	/// <summary>How long a case is drawn for, from the shot: flight, bounces and lying on the floor.</summary>
	public float CasingSeconds { get; init; }

	// ---- where the muzzle is -----------------------------------------------------------

	/// <summary>
	/// Whether the weapon is fired from a body's hands, whose round leaves from its eye
	/// (docs/NETCODE.md §5): the muzzle is drawn at <see cref="HoldReach"/> ahead of it. A
	/// mounted gun's round already leaves from its muzzle.
	/// </summary>
	public bool HandHeld { get; init; }

	/// <summary>How far ahead of the eye a body holds the muzzle, along the line of fire.</summary>
	public float HoldReach { get; init; }

	/// <summary>How far below the line of sight it is held.</summary>
	public float HoldDrop { get; init; }

	/// <summary>How far to the right of the eye: a shouldered weapon is on the right shoulder.</summary>
	public float HoldSide { get; init; }

	// ---- how far it is drawn -----------------------------------------------------------

	/// <summary>
	/// How many times <see cref="MuzzleRanges"/>' reach this kind is drawn to; 0 is once.
	/// A small arm's flash and smoke are lost past a few metres and it fires ten times a
	/// second; a cannon's are metres across and it fires three times a minute, so it is
	/// drawn three times as far for next to nothing.
	/// </summary>
	public float RangeScale { get; init; }

	/// <summary><see cref="RangeScale"/>, with 0 — or anything not a positive number — as one.</summary>
	public float Reach => RangeScale > 0f && float.IsFinite(RangeScale) ? RangeScale : 1f;

	public bool HasFlash => FlashLength > 0f && FlashSeconds > 0f;

	public bool HasSmoke => SmokePuffs > 0 && SmokeSeconds > 0f && SmokeRadius > 0f;

	public bool HasCasing => CasingLength > 0f && CasingDiameter > 0f && CasingSeconds > 0f;

	/// <summary>True for nothing at all: <see cref="ShotKind.None"/> or an unknown value.</summary>
	public bool IsNone => !HasFlash && !HasSmoke && !HasCasing;

	/// <summary>The parts this profile has.</summary>
	public MuzzleParts Parts =>
		(HasFlash ? MuzzleParts.Flash : MuzzleParts.None)
		| (HasSmoke ? MuzzleParts.Smoke : MuzzleParts.None)
		| (HasCasing ? MuzzleParts.Casing : MuzzleParts.None);
}

/// <summary>
/// How far from the viewer each part is still drawn (docs/MUZZLE_EFFECTS.md §2): the
/// performance rule. The local player's own shots are drawn whole wherever the camera is;
/// everyone else's, part by part, only when the muzzle is within that part's reach of the
/// camera. A case is a few centimetres long and lost past a few metres; a puff of smoke is
/// a little further; the flash is the one that tells you where a shot came from, and the
/// cheapest of the three to draw — two quads and a light for three frames — so it reaches
/// furthest.
/// </summary>
public readonly struct MuzzleRanges
{
	public MuzzleRanges(float flashMeters, float smokeMeters, float casingMeters)
	{
		FlashMeters = Clean(flashMeters);
		SmokeMeters = Clean(smokeMeters);
		CasingMeters = Clean(casingMeters);
	}

	/// <summary>The ranges the game starts with; the console's <c>muzzle range</c> changes them for this peer.</summary>
	public static MuzzleRanges Default => new(10f, 8f, 5f);

	/// <summary>The most any range can be set to: past it a muzzle effect is noise, and the pools are sized for a few shooters.</summary>
	public const float MaxMeters = 200f;

	public float FlashMeters { get; }

	public float SmokeMeters { get; }

	public float CasingMeters { get; }

	/// <summary>The furthest of the three: past it nothing is drawn, and nothing else need be asked.</summary>
	public float FurthestMeters => MathF.Max(FlashMeters, MathF.Max(SmokeMeters, CasingMeters));

	/// <summary>
	/// The parts drawn for a muzzle <paramref name="distanceSquared"/> (square metres) from
	/// the camera, each range stretched by <paramref name="scale"/> — a kind's
	/// <see cref="MuzzleProfile.Reach"/>. Squared, so the caller need not take a root for the
	/// common answer — nothing. Inclusive at each range; anything not a number is nothing.
	/// </summary>
	public MuzzleParts Within(float distanceSquared, float scale = 1f)
	{
		if (!(distanceSquared >= 0f) || !(scale > 0f) || !float.IsFinite(scale))
		{
			return MuzzleParts.None;
		}

		float scale2 = scale * scale;
		MuzzleParts parts = MuzzleParts.None;
		if (distanceSquared <= FlashMeters * FlashMeters * scale2)
		{
			parts |= MuzzleParts.Flash;
		}

		if (distanceSquared <= SmokeMeters * SmokeMeters * scale2)
		{
			parts |= MuzzleParts.Smoke;
		}

		if (distanceSquared <= CasingMeters * CasingMeters * scale2)
		{
			parts |= MuzzleParts.Casing;
		}

		return parts;
	}

	private static float Clean(float meters) => float.IsFinite(meters) ? Math.Clamp(meters, 0f, MaxMeters) : 0f;
}

/// <summary>
/// One spent case in the air or on the floor: where it is, how it is moving and turning,
/// and whether it has come to rest. Stepped by <see cref="Muzzles.StepCasing"/>; drawn by
/// <c>Scripts/Effects/MuzzleEffects.cs</c>.
/// </summary>
public struct CasingBody
{
	public Vector3 Position;

	public Vector3 Velocity;

	/// <summary>Euler angles [rad] the case is drawn at: its long axis is its own Y.</summary>
	public Vector3 Rotation;

	/// <summary>How fast each of <see cref="Rotation"/> turns [rad/s].</summary>
	public Vector3 Spin;

	/// <summary>Seconds since it left the port.</summary>
	public float Age;

	public int Bounces;

	/// <summary>Lying on the floor, still.</summary>
	public bool Resting;
}

/// <summary>
/// The engine-free half of muzzle effects (docs/MUZZLE_EFFECTS.md): what each kind of
/// shot draws, how far from the viewer each part is drawn, where a body's muzzle is, which
/// way a case is thrown and how it falls, bounces and comes to rest, and how each part
/// changes with age — so all of it is a question <c>dotnet test</c> can ask.
/// <c>Scripts/Effects/MuzzleEffects.cs</c> is the half that draws them. Presentation only:
/// nothing in the simulation reads any of it and none of it goes on the wire.
/// </summary>
public static class Muzzles
{
	public const float Gravity = 9.8f;

	/// <summary>The share of its speed into the floor a case keeps when it bounces.</summary>
	public const float Restitution = 0.35f;

	/// <summary>The share of its speed along the floor a case keeps when it bounces.</summary>
	public const float Friction = 0.55f;

	/// <summary>A case meeting the floor slower than this [m/s] stops there rather than bouncing.</summary>
	public const float RestSpeed = 0.6f;

	/// <summary>The most times a case bounces before it lies still.</summary>
	public const int MaxBounces = 3;

	/// <summary>How many times its starting radius a puff grows to by the end of its life.</summary>
	public const float SmokeGrowth = 3f;

	/// <summary>Time constant the air takes a puff's speed down the barrel's line off with [s].</summary>
	public const float SmokeDragSeconds = 0.25f;

	/// <summary>How long a puff takes to thicken at the muzzle, so it does not pop in [s].</summary>
	public const float SmokeFadeInSeconds = 0.03f;

	/// <summary>
	/// The share of a puff's opacity the local player's own shots are drawn with: their
	/// muzzle is a little more than half a metre in front of the eye and, with the sights
	/// up, on the line of sight, where a whole puff a shot buries the target in a burst.
	/// </summary>
	public const float OwnSmokeAlpha = 0.35f;

	/// <summary>
	/// How much larger the local player's own flash is drawn than anyone else's: the gun
	/// hides the half of it below and right of the barrel, and a flash of its true size a
	/// metre from the eye is a smudge on the end of the gun rather than a shot.
	/// </summary>
	public const float OwnFlashScale = 1.6f;

	/// <summary>
	/// How far to the side of the gun the ejection port is [m]: the case leaves the side of
	/// the receiver, not its middle.
	/// </summary>
	public const float PortSideMeters = 0.03f;

	/// <summary>Every kind of shot that has something to draw, in the order the console lists them.</summary>
	public static ShotKind[] Drawn => WeaponSounds.Shots;

	/// <summary>The flash's tint over its texture's own white-to-orange: the light it throws is this colour.</summary>
	private static readonly Color Flame = new(1f, 0.82f, 0.55f);
	private static readonly Color PaleSmoke = new(0.78f, 0.77f, 0.75f);
	private static readonly Color GreySmoke = new(0.6f, 0.59f, 0.57f);
	private static readonly Color Brass = new(0.8f, 0.62f, 0.26f);

	private static readonly MuzzleProfile PistolProfile = new()
	{
		FlashLength = 0.12f, FlashWidth = 0.09f, FlashSeconds = 0.04f, LightEnergy = 1.5f, LightRange = 3f,
		FlashColor = Flame,
		SmokePuffs = 1, SmokeRadius = 0.04f, SmokeSeconds = 0.6f, SmokeRiseSpeed = 0.25f, SmokeForwardSpeed = 0.6f,
		SmokeAlpha = 0.3f, SmokeColor = PaleSmoke,
		CasingLength = 0.019f, CasingDiameter = 0.0099f, CasingColor = Brass, PortBackMeters = 0.08f,
		EjectSpeed = 2.2f, EjectUpSpeed = 1.6f, EjectBackSpeed = 0.3f, CasingSeconds = 3f,
		HandHeld = true, HoldReach = 0.5f, HoldDrop = 0.12f, HoldSide = 0f,
	};

	private static readonly MuzzleProfile RifleProfile = new()
	{
		FlashLength = 0.22f, FlashWidth = 0.12f, FlashSeconds = 0.05f, LightEnergy = 2f, LightRange = 4f,
		FlashColor = Flame,
		SmokePuffs = 1, SmokeRadius = 0.06f, SmokeSeconds = 0.8f, SmokeRiseSpeed = 0.3f, SmokeForwardSpeed = 0.9f,
		SmokeAlpha = 0.3f, SmokeColor = PaleSmoke,
		CasingLength = 0.045f, CasingDiameter = 0.0096f, CasingColor = Brass, PortBackMeters = 0.35f,
		EjectSpeed = 2.8f, EjectUpSpeed = 1.2f, EjectBackSpeed = 0.4f, CasingSeconds = 4f,
		HandHeld = true, HoldReach = 0.7f, HoldDrop = 0.15f, HoldSide = 0.12f,
	};

	private static readonly MuzzleProfile DmrProfile = new()
	{
		FlashLength = 0.28f, FlashWidth = 0.15f, FlashSeconds = 0.05f, LightEnergy = 2.5f, LightRange = 5f,
		FlashColor = Flame,
		SmokePuffs = 2, SmokeRadius = 0.08f, SmokeSeconds = 1f, SmokeRiseSpeed = 0.3f, SmokeForwardSpeed = 1.1f,
		SmokeAlpha = 0.32f, SmokeColor = PaleSmoke,
		CasingLength = 0.071f, CasingDiameter = 0.012f, CasingColor = Brass, PortBackMeters = 0.45f,
		EjectSpeed = 3f, EjectUpSpeed = 1f, EjectBackSpeed = 0.5f, CasingSeconds = 4f,
		HandHeld = true, HoldReach = 0.85f, HoldDrop = 0.15f, HoldSide = 0.12f,
	};

	private static readonly MuzzleProfile MachineGunProfile = new()
	{
		FlashLength = 0.24f, FlashWidth = 0.14f, FlashSeconds = 0.05f, LightEnergy = 2.2f, LightRange = 4.5f,
		FlashColor = Flame,
		SmokePuffs = 1, SmokeRadius = 0.07f, SmokeSeconds = 0.9f, SmokeRiseSpeed = 0.3f, SmokeForwardSpeed = 1f,
		SmokeAlpha = 0.3f, SmokeColor = PaleSmoke,
		CasingLength = 0.045f, CasingDiameter = 0.0096f, CasingColor = Brass, PortBackMeters = 0.4f,
		EjectSpeed = 2f, EjectUpSpeed = -0.4f, EjectBackSpeed = 0.2f, CasingSeconds = 4f,
		HandHeld = true, HoldReach = 0.75f, HoldDrop = 0.25f, HoldSide = 0.15f,
	};

	private static readonly MuzzleProfile HeavyMachineGunProfile = new()
	{
		FlashLength = 0.45f, FlashWidth = 0.3f, FlashSeconds = 0.06f, LightEnergy = 3.5f, LightRange = 7f,
		FlashColor = Flame,
		SmokePuffs = 2, SmokeRadius = 0.12f, SmokeSeconds = 1.3f, SmokeRiseSpeed = 0.35f, SmokeForwardSpeed = 1.4f,
		SmokeAlpha = 0.35f, SmokeColor = PaleSmoke,
		CasingLength = 0.099f, CasingDiameter = 0.02f, CasingColor = Brass, PortBackMeters = 0.9f,
		EjectSpeed = 1.6f, EjectUpSpeed = -0.6f, EjectBackSpeed = 0.2f, CasingSeconds = 5f,
		HandHeld = false, RangeScale = 1.5f,
	};

	private static readonly MuzzleProfile LauncherProfile = new()
	{
		FlashLength = 0.15f, FlashWidth = 0.18f, FlashSeconds = 0.04f, LightEnergy = 1.5f, LightRange = 3f,
		FlashColor = Flame,
		SmokePuffs = 3, SmokeRadius = 0.1f, SmokeSeconds = 1.4f, SmokeRiseSpeed = 0.3f, SmokeForwardSpeed = 1.2f,
		SmokeAlpha = 0.4f, SmokeColor = PaleSmoke,
		HandHeld = true, HoldReach = 0.6f, HoldDrop = 0.18f, HoldSide = 0.12f,
	};

	private static readonly MuzzleProfile CannonProfile = new()
	{
		FlashLength = 1.6f, FlashWidth = 1.1f, FlashSeconds = 0.09f, LightEnergy = 8f, LightRange = 14f,
		FlashColor = Flame,
		SmokePuffs = 6, SmokeRadius = 0.45f, SmokeSeconds = 2.5f, SmokeRiseSpeed = 0.4f, SmokeForwardSpeed = 3f,
		SmokeAlpha = 0.5f, SmokeColor = GreySmoke,
		HandHeld = false, RangeScale = 3f,
	};

	private static readonly MuzzleProfile MortarProfile = new()
	{
		FlashLength = 0.35f, FlashWidth = 0.4f, FlashSeconds = 0.06f, LightEnergy = 4f, LightRange = 6f,
		FlashColor = Flame,
		SmokePuffs = 4, SmokeRadius = 0.22f, SmokeSeconds = 2f, SmokeRiseSpeed = 0.35f, SmokeForwardSpeed = 2f,
		SmokeAlpha = 0.45f, SmokeColor = GreySmoke,
		HandHeld = false, RangeScale = 2.5f,
	};

	/// <summary>The numbers a shot of <paramref name="kind"/> is drawn with; nothing for <see cref="ShotKind.None"/> or an unknown value.</summary>
	public static MuzzleProfile Profile(ShotKind kind) => kind switch
	{
		ShotKind.Pistol => PistolProfile,
		ShotKind.Rifle => RifleProfile,
		ShotKind.Dmr => DmrProfile,
		ShotKind.MachineGun => MachineGunProfile,
		ShotKind.HeavyMachineGun => HeavyMachineGunProfile,
		ShotKind.Launcher => LauncherProfile,
		ShotKind.Cannon => CannonProfile,
		ShotKind.Mortar => MortarProfile,
		_ => default,
	};

	// ---- where it is drawn -------------------------------------------------------------

	/// <summary>
	/// The frame a shot is drawn in: <paramref name="right"/> and <paramref name="up"/>
	/// square to <paramref name="direction"/>, with up as near the world's as the line of
	/// fire allows. A shot straight up or down — a mortar's — takes the world's X as its right.
	/// </summary>
	public static void Frame(Vector3 direction, out Vector3 forward, out Vector3 right, out Vector3 up)
	{
		forward = Normal(direction, Vector3.Forward);
		right = forward.Cross(Vector3.Up);
		if (right.LengthSquared() < 1e-6f)
		{
			right = Vector3.Right;
		}

		right = right.Normalized();
		up = right.Cross(forward).Normalized();
	}

	/// <summary>
	/// Where a body holding a weapon of <paramref name="profile"/> has its muzzle, given the
	/// eye its round leaves from and the line it leaves along: ahead of the eye, below the line
	/// of sight and, for a shouldered weapon, to its right. The eye itself for a weapon that
	/// is not held.
	/// </summary>
	public static Vector3 HeldMuzzle(Vector3 eye, Vector3 direction, in MuzzleProfile profile)
	{
		if (!profile.HandHeld)
		{
			return eye;
		}

		Frame(direction, out Vector3 forward, out Vector3 right, out Vector3 up);
		return eye + (forward * profile.HoldReach) + (right * profile.HoldSide) - (up * profile.HoldDrop);
	}

	/// <summary>
	/// Where a vehicle unit's gun is drawn firing from: its round leaves from the middle of
	/// the hull at its eye, so the flash is put at the hull's edge on the line of fire, where
	/// it is not inside the box the hull is drawn as.
	/// </summary>
	public static Vector3 HullMuzzle(Vector3 eye, Vector3 direction, float hullRadiusMeters)
	{
		Frame(direction, out Vector3 forward, out _, out _);
		float radius = float.IsFinite(hullRadiusMeters) ? MathF.Max(hullRadiusMeters, 0f) : 0f;
		return eye + (forward * radius);
	}

	/// <summary>Where a case leaves the gun: behind the muzzle by the port's distance, on the gun's right.</summary>
	public static Vector3 EjectionPort(Vector3 muzzle, Vector3 direction, in MuzzleProfile profile)
	{
		Frame(direction, out Vector3 forward, out Vector3 right, out _);
		return muzzle - (forward * profile.PortBackMeters) + (right * PortSideMeters);
	}

	/// <summary>
	/// How a case leaves the port: to the gun's right, up (or down) and back in its frame,
	/// each between 80 and 120 % of the profile's speed by <paramref name="r1"/>…<paramref name="r3"/>
	/// (0 to 1), plus <paramref name="shooterVelocity"/> — a case is moving with the gun that
	/// threw it.
	/// </summary>
	public static Vector3 EjectVelocity(Vector3 direction, in MuzzleProfile profile, Vector3 shooterVelocity,
		float r1, float r2, float r3)
	{
		Frame(direction, out Vector3 forward, out Vector3 right, out Vector3 up);
		Vector3 velocity = (right * (profile.EjectSpeed * Jitter(r1)))
			+ (up * (profile.EjectUpSpeed * Jitter(r2)))
			- (forward * (profile.EjectBackSpeed * Jitter(r3)));
		return float.IsFinite(shooterVelocity.X) && float.IsFinite(shooterVelocity.Y) && float.IsFinite(shooterVelocity.Z)
			? velocity + shooterVelocity
			: velocity;
	}

	/// <summary>
	/// How far up a weapon's bounds its bore is, as a share of their height: above the
	/// middle, which the grip, the trigger and the magazine pull down.
	/// </summary>
	public const float BoreHeight = 0.75f;

	/// <summary>
	/// The end of a viewmodel's barrel in the mesh's own space, from its bounds: on the face
	/// of the box <paramref name="position"/>, <paramref name="size"/> that
	/// <paramref name="localForward"/> — the camera's forward, in the mesh's space — points
	/// out of most, across it in the middle and <see cref="BoreHeight"/> of the way up it
	/// along <paramref name="localUp"/>, the camera's up. For a weapon whose mesh does not say
	/// where its muzzle is, which is every greybox box.
	/// </summary>
	public static Vector3 BarrelEnd(Vector3 position, Vector3 size, Vector3 localForward, Vector3 localUp)
	{
		Vector3 center = position + (size * 0.5f);
		Vector3 d = Normal(localForward, Vector3.Forward);
		int axis = (int)d.Abs().MaxAxisIndex();
		Vector3 end = center;
		end[axis] += (d[axis] >= 0f ? 1f : -1f) * size[axis] * 0.5f;

		// Up is whichever other axis the camera's up is most along.
		Vector3 u = Normal(localUp, Vector3.Up);
		u[axis] = 0f;
		if (u.LengthSquared() > 1e-6f)
		{
			int upAxis = (int)u.Abs().MaxAxisIndex();
			end[upAxis] += (u[upAxis] >= 0f ? 1f : -1f) * size[upAxis] * (BoreHeight - 0.5f);
		}

		return end;
	}

	// ---- the case's flight --------------------------------------------------------------

	/// <summary>
	/// Moves a case on by <paramref name="dt"/> seconds: it falls and turns, and on reaching
	/// <paramref name="floorY"/> — when there is a floor under it — bounces, keeping
	/// <see cref="Restitution"/> of its speed into the floor and <see cref="Friction"/> of its
	/// speed along it, until it meets the floor slower than <see cref="RestSpeed"/> or has
	/// bounced <see cref="MaxBounces"/> times, when it lies still on its side. Its middle is
	/// kept half its <paramref name="diameter"/> above the floor.
	/// </summary>
	public static void StepCasing(ref CasingBody casing, float dt, bool hasFloor, float floorY, float diameter)
	{
		if (!(dt > 0f))
		{
			return;
		}

		casing.Age += dt;
		if (casing.Resting)
		{
			return;
		}

		casing.Velocity += Vector3.Down * (Gravity * dt);
		casing.Position += casing.Velocity * dt;
		casing.Rotation += casing.Spin * dt;

		float rest = floorY + (MathF.Max(diameter, 0f) * 0.5f);
		if (!hasFloor || casing.Position.Y > rest)
		{
			return;
		}

		casing.Position.Y = rest;
		float into = -casing.Velocity.Y;
		if (into < RestSpeed || casing.Bounces >= MaxBounces)
		{
			casing.Velocity = Vector3.Zero;
			casing.Spin = Vector3.Zero;

			// On its side: its long axis, its own Y, along the floor.
			casing.Rotation = new Vector3(MathF.PI * 0.5f, casing.Rotation.Y, 0f);
			casing.Resting = true;
			return;
		}

		casing.Velocity = new Vector3(casing.Velocity.X * Friction, into * Restitution, casing.Velocity.Z * Friction);
		casing.Spin *= 0.5f;
		casing.Bounces++;
	}

	// ---- how each part changes with age -------------------------------------------------

	/// <summary>How far through a part's life <paramref name="age"/> is, 0 to 1; 1 for a part of no length.</summary>
	public static float Progress(float age, float seconds) =>
		seconds > 0f ? Math.Clamp(age / seconds, 0f, 1f) : 1f;

	/// <summary>
	/// A flash's size as a share of its profile's: whole on the frame of the shot and gone
	/// at its end, shrinking faster at first — the gas burns out from the tip.
	/// </summary>
	public static float FlashScale(float age, float seconds)
	{
		float left = 1f - Progress(age, seconds);
		return age < 0f ? 0f : MathF.Sqrt(left);
	}

	/// <summary>The flash's light as a share of its <see cref="MuzzleProfile.LightEnergy"/>: out faster than the flame.</summary>
	public static float FlashLight(float age, float seconds)
	{
		float left = 1f - Progress(age, seconds);
		return age < 0f ? 0f : left * left;
	}

	/// <summary>A puff's radius as a share of its starting one: from one to <see cref="SmokeGrowth"/>, quickly at first.</summary>
	public static float SmokeScale(float age, float seconds) =>
		1f + ((SmokeGrowth - 1f) * MathF.Sqrt(Progress(age, seconds)));

	/// <summary>A puff's opacity as a share of its profile's: in over <see cref="SmokeFadeInSeconds"/>, then thinning out to nothing.</summary>
	public static float SmokeOpacity(float age, float seconds)
	{
		if (age < 0f || age >= seconds)
		{
			return 0f;
		}

		float fadeIn = Math.Clamp(age / SmokeFadeInSeconds, 0f, 1f);
		float left = 1f - Progress(age, seconds);
		return fadeIn * MathF.Pow(left, 1.5f);
	}

	/// <summary>
	/// How far a puff has gone from where it started after <paramref name="age"/> seconds,
	/// leaving down the line of fire at <paramref name="forwardSpeed"/> that the air takes
	/// off (<see cref="SmokeDragSeconds"/>), and climbing at <paramref name="riseSpeed"/>.
	/// </summary>
	public static Vector3 SmokeOffset(Vector3 forward, float forwardSpeed, float riseSpeed, float age)
	{
		float t = MathF.Max(age, 0f);
		float along = forwardSpeed * SmokeDragSeconds * (1f - MathF.Exp(-t / SmokeDragSeconds));
		return (forward * along) + (Vector3.Up * (riseSpeed * t));
	}

	/// <summary>
	/// A case's size as a share of its own: whole until the last quarter second of its
	/// <paramref name="seconds"/>, then shrinking away rather than vanishing.
	/// </summary>
	public static float CasingScale(float age, float seconds)
	{
		const float Out = 0.25f;
		if (age >= seconds)
		{
			return 0f;
		}

		float left = seconds - age;
		return left >= Out ? 1f : MathF.Max(left / Out, 0f);
	}

	/// <summary>A shot kind's name, as the console takes it.</summary>
	public static string NameOf(ShotKind kind) => WeaponSounds.NameOf(kind);

	/// <summary>Between 80 and 120 %, by <paramref name="r"/> from 0 to 1.</summary>
	private static float Jitter(float r) => 0.8f + (0.4f * Math.Clamp(float.IsFinite(r) ? r : 0.5f, 0f, 1f));

	private static Vector3 Normal(Vector3 v, Vector3 fallback)
	{
		float length = v.Length();
		return length > 1e-6f && float.IsFinite(length) ? v / length : fallback;
	}
}
