using System;
using System.Buffers.Binary;

namespace Gdpyr.Sim;

/// <summary>
/// A strategist unit's ammunition (docs/NETCODE.md §10.7), as pure functions: the
/// rounds it carries for its weapon, which it spends away from supply and gets back
/// inside a supply source's reach.
///
/// The count is the unit's, not the weapon's. A unit's magazine still empties and
/// reloads on its weapon's cadence (<see cref="WeaponSim"/>), and a reload still
/// draws on nothing; what runs out is <c>UnitDefinition.MaxRounds</c>, every round
/// that leaves the muzzle, whatever the magazine says. Kept apart so that a weapon
/// with no magazine at all — the sniper's — runs out like any other, and so that a
/// player's identical weapon is untouched.
///
/// Inside a friendly supply source's reach — a barracks' defended ring or a supply
/// truck's, never its own truck (<see cref="Resupply"/>) — a unit is filled back up
/// at once, every tick, with no lockout: in reach its ammunition is not finite at all.
/// Health is still healed at a rate and only out of the fight; ammunition is what
/// makes a tank go home.
///
/// Engine-free, like the resupply rule: who runs dry and when are questions for
/// <c>dotnet test</c>.
/// </summary>
public static class Ammunition
{
	/// <summary>
	/// The rounds of a unit that never runs out: one whose definition carries none,
	/// which is what a file that says nothing gets — the dropship, which has no gun.
	/// </summary>
	public const int Unlimited = -1;

	/// <summary>Most rounds a unit carries: what the strategist's ammunition message can count (<see cref="AmmoStateCodec"/>).</summary>
	public const int MaxCarried = ushort.MaxValue;

	/// <summary>
	/// What a unit carries full: <paramref name="maxRounds"/>, clamped to
	/// <see cref="MaxCarried"/>, or <see cref="Unlimited"/> for zero or less.
	/// </summary>
	public static int Full(int maxRounds) => maxRounds > 0 ? Math.Min(maxRounds, MaxCarried) : Unlimited;

	/// <summary>True for a count that can run out.</summary>
	public static bool IsLimited(int rounds) => rounds >= 0;

	/// <summary>Whether it has a round to fire: anything but an empty count.</summary>
	public static bool CanFire(int rounds) => rounds != 0;

	/// <summary>The count after one round leaves the muzzle; an unlimited count stays unlimited, an empty one empty.</summary>
	public static int Spend(int rounds) => rounds > 0 ? rounds - 1 : rounds;

	/// <summary>Whether a unit with <paramref name="rounds"/> of <paramref name="maxRounds"/> has all it carries.</summary>
	public static bool IsFull(int rounds, int maxRounds) => !IsLimited(rounds) || rounds >= Full(maxRounds);

	/// <summary>
	/// The count after a tick of the resupply rule: full when the unit is
	/// <paramref name="supplied"/>, unchanged when it is not.
	/// </summary>
	public static int Rearm(int rounds, int maxRounds, bool supplied) =>
		supplied && IsLimited(rounds) ? Full(maxRounds) : rounds;

	/// <summary>What share of what it carries it has left: 1 for a unit that never runs out.</summary>
	public static float Fraction(int rounds, int maxRounds)
	{
		int full = Full(maxRounds);
		return !IsLimited(rounds) || full <= 0 ? 1f : Math.Clamp(rounds / (float)full, 0f, 1f);
	}

	/// <summary>
	/// Whether it has less than <paramref name="share"/> of what it carries left — never
	/// for a unit that does not run out. What a unit a computer strategist ordered goes
	/// back for more at (docs/HTN_BOTS.md §5.2).
	/// </summary>
	public static bool IsBelow(int rounds, int maxRounds, float share) =>
		IsLimited(rounds) && Fraction(rounds, maxRounds) < share;
}

/// <summary>One unit with fewer rounds than it carries full.</summary>
public struct UnitAmmoRecord
{
	public ushort UnitId;

	public ushort Rounds;
}

/// <summary>
/// Packs <c>ServerAmmoState</c> (docs/NETCODE.md §10.7): the strategist side's
/// ammunition, as its strategists' clients need it to draw the selection card. Sent
/// reliably, to strategist peers only, whole, at most five times a second while it
/// changes and to a peer that takes the chair — what a tank has left is the strategist's
/// to know, and not on the wire to the ground force, so not in the unit snapshot every
/// peer receives.
///
/// <code>
/// [count: u16] count × [unit id: u16] [rounds: u16]
/// </code>
///
/// Only units short of a full load are listed: every other unit is full, or never
/// runs out. A unit that has died is simply no longer listed. Decoding is total, as
/// every codec here is: a malformed payload returns false and never throws.
/// </summary>
public static class AmmoStateCodec
{
	public const int HeaderBytes = 2;

	public const int RecordBytes = 4;

	/// <summary>Never more units on the field than this, so never more records.</summary>
	public const int MaxRecords = SimConfig.MaxUnits;

	public const int MaxPayloadBytes = HeaderBytes + (MaxRecords * RecordBytes);

	public static int PayloadBytes(int records) => HeaderBytes + (records * RecordBytes);

	public static byte[] Encode(ReadOnlySpan<UnitAmmoRecord> records)
	{
		var payload = new byte[PayloadBytes(Math.Min(records.Length, MaxRecords))];
		Encode(records, payload);
		return payload;
	}

	/// <summary>Returns the number of bytes written, or 0 if <paramref name="into"/> is too small.</summary>
	public static int Encode(ReadOnlySpan<UnitAmmoRecord> records, Span<byte> into)
	{
		int count = Math.Min(records.Length, MaxRecords);
		int size = PayloadBytes(count);
		if (into.Length < size)
		{
			return 0;
		}

		BinaryPrimitives.WriteUInt16LittleEndian(into, (ushort)count);

		int offset = HeaderBytes;
		for (int i = 0; i < count; i++)
		{
			BinaryPrimitives.WriteUInt16LittleEndian(into[offset..], records[i].UnitId);
			BinaryPrimitives.WriteUInt16LittleEndian(into[(offset + 2)..], records[i].Rounds);
			offset += RecordBytes;
		}

		return size;
	}

	public static bool TryDecode(ReadOnlySpan<byte> payload, Span<UnitAmmoRecord> into, out int count)
	{
		count = 0;
		if (payload.Length < HeaderBytes)
		{
			return false;
		}

		int records = BinaryPrimitives.ReadUInt16LittleEndian(payload);
		if (records > MaxRecords || payload.Length < PayloadBytes(records) || into.Length < records)
		{
			return false;
		}

		int offset = HeaderBytes;
		for (int i = 0; i < records; i++)
		{
			into[i] = new UnitAmmoRecord
			{
				UnitId = BinaryPrimitives.ReadUInt16LittleEndian(payload[offset..]),
				Rounds = BinaryPrimitives.ReadUInt16LittleEndian(payload[(offset + 2)..]),
			};
			offset += RecordBytes;
		}

		count = records;
		return true;
	}
}
