using System;
using System.Buffers.Binary;
using Godot;

namespace Gdpyr.Sim;

/// <summary>A shot, as it goes on the wire (docs/NETCODE.md §4.2, §7).</summary>
public struct ProjectileSpawn
{
	/// <summary>Bytes on the wire: 4 + 4 + 4 + 1 + 6 + 2 + 2.</summary>
	public const int SizeBytes = 23;

	public uint Id;
	public uint SpawnTick;
	public int OwnerPeerId;
	public byte DefinitionId;
	public Vector3 Origin;

	/// <summary>
	/// Direction of travel. Sent as the two look angles rather than as a vector:
	/// three quantized components would cost the same and would additionally have
	/// to be renormalized on arrival.
	/// </summary>
	public Vector3 Direction;
}

/// <summary>What a projectile hit, as the server reports it (docs/NETCODE.md §7).</summary>
public struct ProjectileHit
{
	/// <summary>Bytes on the wire: 4 + 6 + 4 + 1.</summary>
	public const int SizeBytes = 15;

	public uint Id;
	public Vector3 Point;

	/// <summary>The peer that was hit, or 0 for world geometry.</summary>
	public int VictimPeerId;

	public HitFlags Flags;
}

[Flags]
public enum HitFlags : byte
{
	None = 0,

	/// <summary>The hit killed the victim. Drives the shooter's feedback, not the scoreboard.</summary>
	Killed = 1 << 0,

	/// <summary>The projectile detonated rather than simply stopping.</summary>
	Exploded = 1 << 1,

	/// <summary>A melee swing rather than a projectile; <see cref="ProjectileHit.Id"/> is 0.</summary>
	Melee = 1 << 2,
}

/// <summary>
/// Wire format for the two per-shot messages. Both are reliable and low rate —
/// one per trigger pull — so they are encoded individually rather than batched,
/// but they are still quantized: a shot is 23 bytes, which is what makes
/// replicating spawn records instead of projectile transforms worth doing at all
/// (docs/NETCODE.md §4.2).
///
/// Decoding is total, like every other codec here: a malformed datagram must
/// return false, never throw.
/// </summary>
public static class ProjectileCodec
{
	public static byte[] EncodeSpawn(in ProjectileSpawn spawn)
	{
		var payload = new byte[ProjectileSpawn.SizeBytes];
		EncodeSpawn(spawn, payload);
		return payload;
	}

	public static int EncodeSpawn(in ProjectileSpawn spawn, Span<byte> into)
	{
		if (into.Length < ProjectileSpawn.SizeBytes)
		{
			return 0;
		}

		Aim.Angles(spawn.Direction, out float yaw, out float pitch);

		BinaryPrimitives.WriteUInt32LittleEndian(into, spawn.Id);
		BinaryPrimitives.WriteUInt32LittleEndian(into[4..], spawn.SpawnTick);
		BinaryPrimitives.WriteInt32LittleEndian(into[8..], spawn.OwnerPeerId);
		into[12] = spawn.DefinitionId;
		WriteVector(into[13..], spawn.Origin);
		BinaryPrimitives.WriteUInt16LittleEndian(into[19..], Quantize.YawToU16(yaw));
		BinaryPrimitives.WriteInt16LittleEndian(into[21..], Quantize.PitchToI16(pitch));
		return ProjectileSpawn.SizeBytes;
	}

	public static bool TryDecodeSpawn(ReadOnlySpan<byte> payload, out ProjectileSpawn spawn)
	{
		spawn = default;
		if (payload.Length < ProjectileSpawn.SizeBytes)
		{
			return false;
		}

		float yaw = Quantize.U16ToYaw(BinaryPrimitives.ReadUInt16LittleEndian(payload[19..]));
		float pitch = Quantize.I16ToPitch(BinaryPrimitives.ReadInt16LittleEndian(payload[21..]));

		spawn = new ProjectileSpawn
		{
			Id = BinaryPrimitives.ReadUInt32LittleEndian(payload),
			SpawnTick = BinaryPrimitives.ReadUInt32LittleEndian(payload[4..]),
			OwnerPeerId = BinaryPrimitives.ReadInt32LittleEndian(payload[8..]),
			DefinitionId = payload[12],
			Origin = ReadVector(payload[13..]),
			Direction = Aim.Direction(yaw, pitch),
		};
		return true;
	}

	public static byte[] EncodeHit(in ProjectileHit hit)
	{
		var payload = new byte[ProjectileHit.SizeBytes];
		EncodeHit(hit, payload);
		return payload;
	}

	public static int EncodeHit(in ProjectileHit hit, Span<byte> into)
	{
		if (into.Length < ProjectileHit.SizeBytes)
		{
			return 0;
		}

		BinaryPrimitives.WriteUInt32LittleEndian(into, hit.Id);
		WriteVector(into[4..], hit.Point);
		BinaryPrimitives.WriteInt32LittleEndian(into[10..], hit.VictimPeerId);
		into[14] = (byte)hit.Flags;
		return ProjectileHit.SizeBytes;
	}

	public static bool TryDecodeHit(ReadOnlySpan<byte> payload, out ProjectileHit hit)
	{
		hit = default;
		if (payload.Length < ProjectileHit.SizeBytes)
		{
			return false;
		}

		hit = new ProjectileHit
		{
			Id = BinaryPrimitives.ReadUInt32LittleEndian(payload),
			Point = ReadVector(payload[4..]),
			VictimPeerId = BinaryPrimitives.ReadInt32LittleEndian(payload[10..]),
			Flags = (HitFlags)payload[14],
		};
		return true;
	}

	private static void WriteVector(Span<byte> into, Vector3 v)
	{
		BinaryPrimitives.WriteInt16LittleEndian(into, Quantize.MetersToI16(v.X));
		BinaryPrimitives.WriteInt16LittleEndian(into[2..], Quantize.MetersToI16(v.Y));
		BinaryPrimitives.WriteInt16LittleEndian(into[4..], Quantize.MetersToI16(v.Z));
	}

	private static Vector3 ReadVector(ReadOnlySpan<byte> from) => new(
		Quantize.I16ToMeters(BinaryPrimitives.ReadInt16LittleEndian(from)),
		Quantize.I16ToMeters(BinaryPrimitives.ReadInt16LittleEndian(from[2..])),
		Quantize.I16ToMeters(BinaryPrimitives.ReadInt16LittleEndian(from[4..])));
}
