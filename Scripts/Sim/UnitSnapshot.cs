using System;
using Godot;

namespace Gdpyr.Sim;

/// <summary>
/// One unit's authoritative state at a server tick (docs/NETCODE.md §6.1, §7).
///
/// Thirteen bytes, against twenty-nine for a player, because almost nothing about
/// a unit is worth a client's attention: it is never predicted, never reconciled
/// and never simulated anywhere but the server, so there is no acknowledgement to
/// carry and no velocity to restore. Position, facing, what it is doing and how
/// hurt it is are the whole of it.
///
/// Health is a percentage rather than hit points: a tank and a rifleman do not
/// share a scale, and a health bar over a unit is the only thing that reads it.
/// </summary>
public struct UnitSnapshot
{
	/// <summary>Bytes on the wire: 2 + 1 + 6 + 2 + 1 + 1.</summary>
	public const int SizeBytes = 13;

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

	public readonly bool IsAlive => State != UnitStateId.Dead && HealthPercent > 0;
}

/// <summary>
/// The unit snapshot's one packed byte: what it is doing, and whose it is.
///
/// Separate from <see cref="WeaponFlags"/> because it packs different things, and
/// one byte rather than two fields for the same reason — this goes out per unit
/// per snapshot, fifty times over, twenty times a second.
/// </summary>
public static class UnitFlags
{
	private const int StateMask = 0b11;
	private const int TeamBit = 1 << 2;

	public static byte Pack(UnitStateId state, Team team)
	{
		int packed = (int)state & StateMask;
		if (team == Team.Strategist)
		{
			packed |= TeamBit;
		}
		return (byte)packed;
	}

	public static UnitStateId State(byte flags) => (UnitStateId)(flags & StateMask);

	public static Team TeamOf(byte flags) => (flags & TeamBit) != 0 ? Team.Strategist : Team.GroundForce;

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
