using System;
using Godot;

namespace Gdpyr.Sim;

/// <summary>
/// One unit's authoritative state at a server tick (docs/NETCODE.md §6.1, §7).
///
/// Fourteen bytes, against twenty-nine for a player, because almost nothing about
/// a unit is worth a client's attention: it is never predicted, never reconciled
/// and never simulated anywhere but the server, so there is no acknowledgement to
/// carry and no velocity to restore. Position, facing, what it is doing and how
/// hurt it is are the whole of it.
///
/// Health is a percentage rather than hit points: a tank and a rifleman do not
/// share a scale, and a health bar over a unit is the only thing that reads it.
///
/// A unit riding in a vehicle (docs/VEHICLES.md §6.1) costs nothing extra: one bit
/// of the body byte says it is aboard, and the yaw field — a hidden passenger's
/// heading means nothing — carries the vehicle's id instead. Its position is the
/// vehicle's.
///
/// Version 2 (docs/TACTICS.md §5.8) split version 1's one flag byte in two: the
/// body's (<see cref="UnitBodyFlags"/>, with the posture) and an aircraft's flight
/// (<see cref="UnitFlightFlags"/>), which mean nothing to each other.
/// </summary>
public struct UnitSnapshot
{
	/// <summary>Bytes on the wire: 2 + 1 + 6 + 2 + 1 + 1 + 1.</summary>
	public const int SizeBytes = 14;

	/// <summary>Server-allocated, never 0 (docs/IMPLEMENTATION_PLAN.md §M3).</summary>
	public ushort UnitId;

	/// <summary>Index into <c>UnitCatalog</c>. A protocol value, like a weapon id.</summary>
	public byte DefinitionId;

	public Vector3 Position;

	public float Yaw;

	public UnitStateId State;

	public Team Team;

	/// <summary>Health as 0–255 over the unit's own maximum. 0 means dead.</summary>
	public byte HealthPercent;

	/// <summary>The vehicle it is riding in, by unit id; 0 on foot. Never set on a dead unit's record.</summary>
	public ushort RidingIn;

	/// <summary>An aircraft's flight phase; <see cref="FlightPhase.Grounded"/> for everything that does not fly.</summary>
	public FlightPhase Phase;

	/// <summary>An aircraft holding at cruise because it has found nowhere to land.</summary>
	public bool CannotLand;

	/// <summary>Standing or crouched (docs/TACTICS.md §5.8); standing for anything that cannot crouch.</summary>
	public UnitPosture Posture;

	public readonly bool IsAlive => State != UnitStateId.Dead && HealthPercent > 0;

	public readonly bool IsAboard => RidingIn != 0;
}

/// <summary>
/// Where an aircraft is in taking off and landing (docs/VEHICLES.md §4.2): two bits
/// of the unit snapshot's flight byte, so the order is a protocol constant. Always
/// <see cref="Grounded"/> for a unit that does not fly.
/// </summary>
public enum FlightPhase : byte
{
	Grounded = 0,
	TakingOff = 1,
	Cruising = 2,
	Landing = 3,
}

/// <summary>
/// The unit snapshot's body byte: what it is doing, whose it is, whether it is
/// riding in a vehicle, and how it holds itself (docs/TACTICS.md §5.8).
///
/// <code>
/// bits 0–1  state     bit 2  team     bit 3  aboard
/// bits 4–5  posture   bits 6–7  spare
/// </code>
///
/// One byte rather than four fields because this goes out per unit per snapshot,
/// fifty times over, twenty times a second.
/// </summary>
public static class UnitBodyFlags
{
	private const int StateMask = 0b11;
	private const int TeamBit = 1 << 2;
	private const int AboardBit = 1 << 3;
	private const int PostureShift = 4;
	private const int PostureMask = 0b11 << PostureShift;

	public static byte Pack(UnitStateId state, Team team) => Pack(state, team, aboard: false, UnitPosture.Standing);

	public static byte Pack(UnitStateId state, Team team, bool aboard, UnitPosture posture)
	{
		int packed = (int)state & StateMask;
		if (team == Team.Strategist)
		{
			packed |= TeamBit;
		}

		if (aboard)
		{
			packed |= AboardBit;
		}

		packed |= ((int)posture << PostureShift) & PostureMask;
		return (byte)packed;
	}

	public static UnitStateId State(byte flags) => (UnitStateId)(flags & StateMask);

	public static Team TeamOf(byte flags) => (flags & TeamBit) != 0 ? Team.Strategist : Team.GroundForce;

	/// <summary>Whether the record is a passenger's, whose yaw field names its vehicle.</summary>
	public static bool IsAboard(byte flags) => (flags & AboardBit) != 0;

	/// <summary>The posture, with the two reserved values read as standing.</summary>
	public static UnitPosture PostureOf(byte flags) =>
		((flags & PostureMask) >> PostureShift) == (int)UnitPosture.Crouched ? UnitPosture.Crouched : UnitPosture.Standing;
}

/// <summary>
/// The unit snapshot's flight byte: an aircraft's phase and whether it has found
/// nowhere to land (docs/VEHICLES.md §4.2). Zero for everything that does not fly.
///
/// <code>
/// bits 0–1  phase     bit 2  cannot land     bits 3–7  spare
/// </code>
/// </summary>
public static class UnitFlightFlags
{
	private const int PhaseMask = 0b11;
	private const int CannotLandBit = 1 << 2;

	public static byte Pack(FlightPhase phase, bool cannotLand) =>
		(byte)(((int)phase & PhaseMask) | (cannotLand ? CannotLandBit : 0));

	public static FlightPhase PhaseOf(byte flags) => (FlightPhase)(flags & PhaseMask);

	public static bool CannotLand(byte flags) => (flags & CannotLandBit) != 0;
}

/// <summary>Health as the unit snapshot and the structure snapshot carry it.</summary>
public static class UnitFlags
{
	/// <summary>Health as the wire carries it: 0–255 over the unit's own maximum.</summary>
	public static byte PackHealth(float health, float maxHealth)
	{
		if (health <= 0f || maxHealth <= 0f)
		{
			return 0;
		}

		// Rounds up, so a unit with one hit point left never reads as dead. A live
		// unit drawn as a corpse is a bug report nobody can reproduce.
		int percent = (int)MathF.Ceiling(Math.Clamp(health / maxHealth, 0f, 1f) * byte.MaxValue);
		return (byte)Math.Clamp(percent, 1, byte.MaxValue);
	}
}
