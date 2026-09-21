using System;
using Gdpyr.Sim;
using Godot;
using Xunit;

namespace Gdpyr.Tests;

public class ProjectileCodecTests
{
	[Fact]
	public void SpawnIsTwentyThreeBytesOnTheWire()
	{
		// docs/NETCODE.md §7 budgets ~20 B for a shot; this is what replaces
		// replicating a projectile's transform every tick.
		Assert.Equal(23, ProjectileSpawn.SizeBytes);
		Assert.Equal(15, ProjectileHit.SizeBytes);
	}

	[Fact]
	public void SpawnRoundTrips()
	{
		var spawn = new ProjectileSpawn
		{
			Id = 987654,
			SpawnTick = 123456,
			OwnerPeerId = 1234567,
			DefinitionId = 3,
			Origin = new Vector3(10.25f, 1.75f, -42.5f),
			Direction = new Vector3(0.3f, 0.1f, -1f).Normalized(),
		};

		byte[] payload = ProjectileCodec.EncodeSpawn(spawn);
		Assert.True(ProjectileCodec.TryDecodeSpawn(payload, out ProjectileSpawn decoded));

		Assert.Equal(spawn.Id, decoded.Id);
		Assert.Equal(spawn.SpawnTick, decoded.SpawnTick);
		Assert.Equal(spawn.OwnerPeerId, decoded.OwnerPeerId);
		Assert.Equal(spawn.DefinitionId, decoded.DefinitionId);
		Assert.True((spawn.Origin - decoded.Origin).Length() < 0.01f);

		// Angles are 16 bit, so the direction comes back within a hundredth of a
		// degree — 2 cm of aim error at 100 m, well inside a player's width.
		Assert.True(decoded.Direction.IsNormalized());
		Assert.True(spawn.Direction.AngleTo(decoded.Direction) < 1e-3f);
	}

	[Fact]
	public void SpawnSurvivesStraightUpAndStraightDown()
	{
		foreach (Vector3 direction in new[] { Vector3.Up, Vector3.Down })
		{
			byte[] payload = ProjectileCodec.EncodeSpawn(new ProjectileSpawn { Direction = direction });
			Assert.True(ProjectileCodec.TryDecodeSpawn(payload, out ProjectileSpawn decoded));
			Assert.True(direction.AngleTo(decoded.Direction) < 1e-3f);
		}
	}

	[Fact]
	public void HitRoundTrips()
	{
		var hit = new ProjectileHit
		{
			Id = 42,
			Point = new Vector3(-3.5f, 1.25f, 88.75f),
			VictimPeerId = 77,
			Flags = HitFlags.Killed | HitFlags.Exploded,
		};

		byte[] payload = ProjectileCodec.EncodeHit(hit);
		Assert.True(ProjectileCodec.TryDecodeHit(payload, out ProjectileHit decoded));

		Assert.Equal(hit.Id, decoded.Id);
		Assert.Equal(hit.VictimPeerId, decoded.VictimPeerId);
		Assert.Equal(hit.Flags, decoded.Flags);
		Assert.True((hit.Point - decoded.Point).Length() < 0.01f);
	}

	[Fact]
	public void TruncatedPayloadsAreRefusedRatherThanThrowing()
	{
		byte[] spawn = ProjectileCodec.EncodeSpawn(new ProjectileSpawn { Direction = Vector3.Forward });
		byte[] hit = ProjectileCodec.EncodeHit(new ProjectileHit());

		for (int length = 0; length < spawn.Length; length++)
		{
			Assert.False(ProjectileCodec.TryDecodeSpawn(spawn.AsSpan(0, length), out _));
		}

		for (int length = 0; length < hit.Length; length++)
		{
			Assert.False(ProjectileCodec.TryDecodeHit(hit.AsSpan(0, length), out _));
		}
	}

	[Fact]
	public void EncodingIntoATooSmallBufferWritesNothing()
	{
		var into = new byte[4];
		Assert.Equal(0, ProjectileCodec.EncodeSpawn(new ProjectileSpawn(), into));
		Assert.Equal(0, ProjectileCodec.EncodeHit(new ProjectileHit(), into));
	}

	[Fact]
	public void AimAnglesRoundTripThroughADirection()
	{
		// Aim.Direction is what the shooter, the server and every observer each use
		// to turn two angles back into a shot; it has to be one function.
		for (float yaw = -3f; yaw < 3f; yaw += 0.37f)
		{
			for (float pitch = -1.5f; pitch < 1.5f; pitch += 0.29f)
			{
				Vector3 direction = Aim.Direction(yaw, pitch);
				Assert.True(direction.IsNormalized());

				Aim.Angles(direction, out float decodedYaw, out float decodedPitch);
				Assert.True(Aim.Direction(decodedYaw, decodedPitch).AngleTo(direction) < 1e-4f);
			}
		}
	}

	[Fact]
	public void AimDirectionMatchesGodotsBasisConvention()
	{
		// -Z forward, yaw about +Y, positive pitch up.
		Assert.True(Aim.Direction(0f, 0f).IsEqualApprox(Vector3.Forward));
		Assert.True(Aim.Direction(0f, Quantize.HalfPi).IsEqualApprox(Vector3.Up));

		Vector3 viaBasis = Basis.FromEuler(new Vector3(0f, 1.1f, 0f))
			* Basis.FromEuler(new Vector3(-0.4f, 0f, 0f)) * Vector3.Forward;
		Assert.True(Aim.Direction(1.1f, -0.4f).IsEqualApprox(viaBasis));
	}

	[Fact]
	public void AimAnglesOfNothingAreZero()
	{
		Aim.Angles(Vector3.Zero, out float yaw, out float pitch);
		Assert.Equal(0f, yaw);
		Assert.Equal(0f, pitch);
	}
}
