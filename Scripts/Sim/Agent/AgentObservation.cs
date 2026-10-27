using System;
using Godot;

namespace Gdpyr.Sim.Agent;

/// <summary>The round, as a seat is allowed to see it.</summary>
public struct AgentRoundView
{
	/// <summary><see cref="RoundPhase"/>.</summary>
	public byte Phase;

	public float SecondsRemaining;
	public float SecondsTotal;
	public int GroundTickets;
	public int StartingGroundTickets;

	/// <summary>The server tick this observation was taken on.</summary>
	public uint Tick;

	/// <summary>The seat's action repeat, so a policy can align its own clock (docs/AGENT_API.md §5.3).</summary>
	public int StepMul;
}

/// <summary>The character the seat is driving.</summary>
public struct AgentSelfView
{
	public Vector3 Position;
	public Vector3 Velocity;
	public float Yaw;
	public float Pitch;
	public bool Grounded;

	/// <summary><c>fps_controller.StateId</c> — an index into the movement FSM.</summary>
	public byte MovementState;

	public int Health;
	public bool Alive;

	/// <summary>
	/// The carry's scale times the kit's (docs/LOADOUT.md §5.5). Clamped to 1 in the
	/// vector as it always was, so a kit lighter than the default reads 1 there;
	/// <see cref="LoadScale"/> says by how much.
	/// </summary>
	public float MoveSpeedScale;

	/// <summary>Hand grenades left (docs/LOADOUT.md §9). One of the extras, not the vector.</summary>
	public int Grenades;

	/// <summary>The vest's points left (docs/LOADOUT.md §4.3), 0 with none. An extra.</summary>
	public int Armour;

	/// <summary>What the vest has when full: its tier's points, 0 with none. An extra.</summary>
	public int ArmourMax;

	/// <summary>The vest, <see cref="BodyArmourTier"/>: 0 none to 3 heavy. An extra.</summary>
	public int ArmourTier;

	/// <summary>What the kit weighs [kg] (docs/LOADOUT.md §4.1). An extra.</summary>
	public float LoadKg;

	/// <summary>
	/// The share of its speeds the kit leaves the seat (L-D4): 1 for the default kit,
	/// 0.70 to 1.12 for any. The kit's factor alone, where <see cref="MoveSpeedScale"/> is
	/// the product with the carry's. An extra.
	/// </summary>
	public float LoadScale;
}

/// <summary>What the seat is holding.</summary>
public struct AgentWeaponView
{
	public int Slot;
	public int Ammo;
	public int MagazineSize;

	/// <summary>True for the weapons whose magazine is not counted (melee, a mounted gun's belt).</summary>
	public bool UnlimitedAmmo;

	/// <summary>0 when not reloading, otherwise how far through the reload it is.</summary>
	public float ReloadProgress;

	public bool CanFire;
	public bool Ads;
	public bool CarryingGun;
	public bool MountedOnGun;

	/// <summary>Spare rounds for the weapon in the hands, or -1 for one that reloads forever. An extra.</summary>
	public int Reserve;

	/// <summary>What the kit's pouches hold for it when full, or -1 for unlimited. An extra.</summary>
	public int ReserveMax;
}

/// <summary>
/// One thing the seat's own eyes have acquired. Bearings are relative to where
/// the character is looking, which is the frame a policy acts in.
/// </summary>
public struct AgentContactView
{
	public bool IsPlayer;
	public bool Hostile;

	/// <summary>Radians, relative to the seat's yaw, positive to the left.</summary>
	public float Bearing;

	/// <summary>Radians above the horizon.</summary>
	public float Elevation;

	public float DistanceMeters;

	/// <summary>Metres per second along the line of sight, positive closing.</summary>
	public float ClosingSpeed;

	/// <summary>Metres per second across the line of sight.</summary>
	public float LateralSpeed;

	public float HealthFraction;
	public int TicksSinceSeen;
}

/// <summary>What the seat is walking towards, and how the map is going.</summary>
public struct AgentObjectiveView
{
	public bool Known;

	/// <summary>Radians to the nearest enemy barracks, relative to the seat's yaw.</summary>
	public float Bearing;

	public float DistanceMeters;
	public int GroundNodes;
	public int StrategistNodes;
	public int ContestedNodes;
}

/// <summary>One named run of floats in the observation vector.</summary>
public readonly struct AgentField
{
	public readonly string Name;
	public readonly int Offset;
	public readonly int Count;
	public readonly float Min;
	public readonly float Max;

	public AgentField(string name, int offset, int count, float min, float max)
	{
		Name = name;
		Offset = offset;
		Count = count;
		Min = min;
		Max = max;
	}
}

/// <summary>
/// The ground-force observation: what an egocentric policy is handed every
/// decision (docs/AGENT_API.md §6.1).
///
/// The layout is a table rather than a run of magic offsets, and the encoder
/// writes through a cursor that the table indexes — so the schema <c>welcome</c>
/// publishes and the bytes the encoder produces cannot drift apart, which is the
/// one failure mode a self-describing binary format has to rule out
/// (`Tests/AgentObservationTests.cs`).
///
/// Everything here is engine-free. The filtering — what this seat's eyes have
/// actually acquired — happens before the view structs are filled, in the game's
/// own acquisition rather than in a second implementation of it.
/// </summary>
public static class AgentObservation
{
	/// <summary>Contacts carried, nearest first, zero-padded (docs/AGENT_API.md §6.1).</summary>
	public const int MaxContacts = 8;

	/// <summary>Floats per contact record.</summary>
	public const int ContactFloats = 11;

	/// <summary>Rays in the horizontal fan.</summary>
	public const int Rays = 16;

	/// <summary>Half-width of the ray fan [rad]: ±60° about the look direction.</summary>
	public const float RayFanHalfAngleRadians = 1.0471976f;

	/// <summary>How far a ray is cast before it reports clear [m].</summary>
	public const float RayRangeMeters = 40f;

	/// <summary>
	/// Movement states in the FSM, and the width of the one-hot that carries them.
	///
	/// The design sketched six; the shipped FSM has seven (idle, walking,
	/// sprinting, crouching, sliding, jumping, falling), so the self block is 20
	/// floats and the vector is 145 rather than 144. Recorded rather than rounded,
	/// because a client decodes by the published schema and not by the document.
	/// </summary>
	public const int MovementStates = 7;

	// ---- normalization bounds, published with the schema -------------------

	/// <summary>Half-extent the map's coordinates are divided by.</summary>
	public const float PositionScaleMeters = 256f;

	public const float VelocityScaleMetersPerSecond = 16f;

	/// <summary>Contact and objective distances are divided by this and clamped.</summary>
	public const float DistanceScaleMeters = 128f;

	public const float ClosingSpeedScaleMetersPerSecond = 24f;

	/// <summary>
	/// Where <c>self.load_kg</c> is clamped [kg]: past the heaviest kit §4.1's weights
	/// make, 26.1 kg, with room for the <c>.tres</c> files to be tuned.
	/// </summary>
	public const float MaxLoadKg = 40f;

	private static readonly AgentField[] Table = Build();

	/// <summary>Floats in one ground observation.</summary>
	public static int Floats { get; } = Total(Table);

	/// <summary>Bytes in one ground observation, packed float32.</summary>
	public static int Bytes => Floats * sizeof(float);

	/// <summary>The layout, in order. This is what <c>welcome</c> publishes.</summary>
	public static ReadOnlySpan<AgentField> Fields => Table;

	private static readonly AgentField[] ExtraTable =
	{
		new("self.grenades", 0, 1, 0f, SimConfig.MaxGrenades),
		new("weapon.reserve", 1, 1, -1f, KitFlags.MaxReserve),
		new("weapon.reserve_max", 2, 1, -1f, KitFlags.MaxReserve),
		new("self.armour", 3, 1, 0f, KitFlags.MaxArmourPoints),
		new("self.armour_max", 4, 1, 0f, KitFlags.MaxArmourPoints),
		new("self.armour_tier", 5, 1, 0f, (float)BodyArmourTier.Heavy),
		new("self.load_kg", 6, 1, 0f, MaxLoadKg),
		new("self.load_scale", 7, 1, Loadout.MinSpeedScale, Loadout.MaxSpeedScale),
	};

	/// <summary>
	/// Fields beyond the vector, in raw units rather than normalized (docs/LOADOUT.md
	/// §9, L-D15). Every JSON observation carries them by name; a binary one only when
	/// its session asked at <c>hello</c> (<c>"extras": true</c>), after the vector and
	/// any planes, with bit 3 of the frame's flags set. So the vector — and the
	/// schema's version — stay what every trained policy reads until L8 appends its
	/// floats. Offsets are into the extras, not the vector.
	/// </summary>
	public static ReadOnlySpan<AgentField> ExtraFields => ExtraTable;

	/// <summary>Floats in the extras.</summary>
	public static int ExtraFloats { get; } = Total(ExtraTable);

	/// <summary>The bit of a binary observation's flags that says the extras follow.</summary>
	public const ushort ExtrasFlag = 8;

	/// <summary>Writes the extras. Returns the floats written, or 0 when <paramref name="into"/> is too small.</summary>
	public static int EncodeExtras(in AgentSelfView self, in AgentWeaponView weapon, Span<float> into)
	{
		if (into.Length < ExtraFloats)
		{
			return 0;
		}

		into[0] = Math.Clamp(self.Grenades, 0, SimConfig.MaxGrenades);
		into[1] = Math.Clamp(weapon.Reserve, -1, KitFlags.MaxReserve);
		into[2] = Math.Clamp(weapon.ReserveMax, -1, KitFlags.MaxReserve);
		into[3] = Math.Clamp(self.Armour, 0, KitFlags.MaxArmourPoints);
		into[4] = Math.Clamp(self.ArmourMax, 0, KitFlags.MaxArmourPoints);
		into[5] = Math.Clamp(self.ArmourTier, 0, (int)BodyArmourTier.Heavy);
		into[6] = Math.Clamp(self.LoadKg, 0f, MaxLoadKg);
		into[7] = Math.Clamp(self.LoadScale, Loadout.MinSpeedScale, Loadout.MaxSpeedScale);
		return ExtraFloats;
	}

	/// <summary>Where a named run starts, or -1. For tests and for readable assertions.</summary>
	public static int OffsetOf(string name)
	{
		for (int i = 0; i < Table.Length; i++)
		{
			if (Table[i].Name == name)
			{
				return Table[i].Offset;
			}
		}

		return -1;
	}

	private static AgentField[] Build()
	{
		int at = 0;
		AgentField Next(string name, int count, float min, float max)
		{
			var field = new AgentField(name, at, count, min, max);
			at += count;
			return field;
		}

		return new[]
		{
			// Round
			Next("round.phase", 1, 0f, 3f),
			Next("round.seconds_remaining", 1, 0f, 1f),
			Next("round.ticket_fraction", 1, 0f, 1f),
			Next("round.step_phase", 1, 0f, 1f),

			// Self
			Next("self.position", 3, -1f, 1f),
			Next("self.velocity", 3, -1f, 1f),
			Next("self.yaw_sin_cos", 2, -1f, 1f),
			Next("self.pitch", 1, -1f, 1f),
			Next("self.grounded", 1, 0f, 1f),
			Next("self.movement_state", MovementStates, 0f, 1f),
			Next("self.health", 1, 0f, 1f),
			Next("self.alive", 1, 0f, 1f),
			Next("self.move_speed_scale", 1, 0f, 1f),

			// Weapon
			Next("weapon.slot", 3, 0f, 1f),
			Next("weapon.magazine", 1, 0f, 1f),
			Next("weapon.unlimited", 1, 0f, 1f),
			Next("weapon.reload_progress", 1, 0f, 1f),
			Next("weapon.can_fire", 1, 0f, 1f),
			Next("weapon.ads", 1, 0f, 1f),
			Next("weapon.carrying_gun", 1, 0f, 1f),
			Next("weapon.mounted", 1, 0f, 1f),
			Next("weapon.shots_since_reload", 1, 0f, 1f),

			// Contacts
			Next("contacts", MaxContacts * ContactFloats, -1f, 1f),

			// Rays
			Next("rays", Rays, 0f, 1f),

			// Objective
			Next("objective.bearing_sin_cos", 2, -1f, 1f),
			Next("objective.distance", 1, 0f, 1f),
			Next("objective.ground_nodes", 1, 0f, 1f),
			Next("objective.strategist_nodes", 1, 0f, 1f),
			Next("objective.contested_nodes", 1, 0f, 1f),
		};
	}

	private static int Total(AgentField[] table)
	{
		int total = 0;
		for (int i = 0; i < table.Length; i++)
		{
			total += table[i].Count;
		}

		return total;
	}

	/// <summary>
	/// Writes one ground observation. Returns the floats written, or 0 when
	/// <paramref name="into"/> is too small.
	///
	/// <paramref name="contacts"/> is expected nearest-first and is truncated at
	/// <see cref="MaxContacts"/>; <paramref name="rays"/> carries normalized hit
	/// distances and is zero-filled when the caller has none. Both are padded with
	/// zeros so the tensor shape never changes mid-episode.
	/// </summary>
	public static int Encode(in AgentRoundView round, in AgentSelfView self, in AgentWeaponView weapon,
		ReadOnlySpan<AgentContactView> contacts, ReadOnlySpan<float> rays, in AgentObjectiveView objective,
		Span<float> into)
	{
		if (into.Length < Floats)
		{
			return 0;
		}

		into[..Floats].Clear();
		int at = 0;

		// ---- round ----------------------------------------------------------
		into[at++] = round.Phase;
		into[at++] = round.SecondsTotal > 0f
			? Math.Clamp(round.SecondsRemaining / round.SecondsTotal, 0f, 1f)
			: 0f;
		into[at++] = round.StartingGroundTickets > 0
			? Math.Clamp(round.GroundTickets / (float)round.StartingGroundTickets, 0f, 1f)
			: 0f;
		into[at++] = round.StepMul > 1 ? (round.Tick % (uint)round.StepMul) / (float)round.StepMul : 0f;

		// ---- self -----------------------------------------------------------
		into[at++] = Normalize(self.Position.X, PositionScaleMeters);
		into[at++] = Normalize(self.Position.Y, PositionScaleMeters);
		into[at++] = Normalize(self.Position.Z, PositionScaleMeters);
		into[at++] = Normalize(self.Velocity.X, VelocityScaleMetersPerSecond);
		into[at++] = Normalize(self.Velocity.Y, VelocityScaleMetersPerSecond);
		into[at++] = Normalize(self.Velocity.Z, VelocityScaleMetersPerSecond);
		into[at++] = MathF.Sin(self.Yaw);
		into[at++] = MathF.Cos(self.Yaw);
		into[at++] = Math.Clamp(self.Pitch / Quantize.HalfPi, -1f, 1f);
		into[at++] = self.Grounded ? 1f : 0f;

		if (self.MovementState < MovementStates)
		{
			into[at + self.MovementState] = 1f;
		}
		at += MovementStates;

		into[at++] = Math.Clamp(self.Health / (float)SimConfig.MaxHealth, 0f, 1f);
		into[at++] = self.Alive ? 1f : 0f;
		into[at++] = Math.Clamp(self.MoveSpeedScale, 0f, 1f);

		// ---- weapon ---------------------------------------------------------
		if (weapon.Slot is >= 0 and < SimConfig.WeaponSlots)
		{
			into[at + weapon.Slot] = 1f;
		}
		at += SimConfig.WeaponSlots;

		float magazine = weapon.MagazineSize > 0
			? Math.Clamp(weapon.Ammo / (float)weapon.MagazineSize, 0f, 1f)
			: 1f;
		into[at++] = magazine;
		into[at++] = weapon.UnlimitedAmmo ? 1f : 0f;
		into[at++] = Math.Clamp(weapon.ReloadProgress, 0f, 1f);
		into[at++] = weapon.CanFire ? 1f : 0f;
		into[at++] = weapon.Ads ? 1f : 0f;
		into[at++] = weapon.CarryingGun ? 1f : 0f;
		into[at++] = weapon.MountedOnGun ? 1f : 0f;
		into[at++] = 1f - magazine;

		// ---- contacts -------------------------------------------------------
		int carried = Math.Min(contacts.Length, MaxContacts);
		for (int i = 0; i < carried; i++)
		{
			ref readonly AgentContactView contact = ref contacts[i];
			int slot = at + (i * ContactFloats);
			into[slot + 0] = contact.IsPlayer ? 1f : 0f;
			into[slot + 1] = contact.IsPlayer ? 0f : 1f;
			into[slot + 2] = contact.Hostile ? 1f : 0f;
			into[slot + 3] = MathF.Sin(contact.Bearing);
			into[slot + 4] = MathF.Cos(contact.Bearing);
			into[slot + 5] = Math.Clamp(contact.Elevation / Quantize.HalfPi, -1f, 1f);
			into[slot + 6] = Normalize(contact.DistanceMeters, DistanceScaleMeters);
			into[slot + 7] = Normalize(contact.ClosingSpeed, ClosingSpeedScaleMetersPerSecond);
			into[slot + 8] = Normalize(contact.LateralSpeed, ClosingSpeedScaleMetersPerSecond);
			into[slot + 9] = Math.Clamp(contact.HealthFraction, 0f, 1f);
			into[slot + 10] = Math.Clamp(contact.TicksSinceSeen / (float)SimConfig.TickRate, 0f, 1f);
		}
		at += MaxContacts * ContactFloats;

		// ---- rays -----------------------------------------------------------
		int cast = Math.Min(rays.Length, Rays);
		for (int i = 0; i < cast; i++)
		{
			into[at + i] = Math.Clamp(rays[i], 0f, 1f);
		}
		at += Rays;

		// ---- objective ------------------------------------------------------
		into[at++] = objective.Known ? MathF.Sin(objective.Bearing) : 0f;
		into[at++] = objective.Known ? MathF.Cos(objective.Bearing) : 0f;
		into[at++] = objective.Known ? Normalize(objective.DistanceMeters, DistanceScaleMeters) : 0f;
		into[at++] = Math.Clamp(objective.GroundNodes / (float)SimConfig.MaxResourceNodes, 0f, 1f);
		into[at++] = Math.Clamp(objective.StrategistNodes / (float)SimConfig.MaxResourceNodes, 0f, 1f);
		into[at++] = Math.Clamp(objective.ContestedNodes / (float)SimConfig.MaxResourceNodes, 0f, 1f);

		return at;
	}

	private static float Normalize(float value, float scale) => Math.Clamp(value / scale, -1f, 1f);
}
