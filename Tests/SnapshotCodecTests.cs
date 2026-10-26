using System;
using Gdpyr.Sim;
using Godot;
using Xunit;

namespace Gdpyr.Tests;

public class SnapshotCodecTests
{
	private static PlayerSnapshot Sample(int peerId) => new()
	{
		PeerId = peerId,
		Position = new Vector3(12.34f, 4.5f, -67.89f),
		Velocity = new Vector3(-3.25f, 1.5f, 0.75f),
		Yaw = 2.5f,
		Pitch = -0.75f,
		LastInputTick = 4242,
		StateId = 3,
		InputBufferDepth = 4,
		Health = 73,
		Ammo = 17,
		WeaponFlags = Sim.WeaponFlags.Pack(2, reloading: true, Team.GroundForce),
		Reserve = 270,
		ArmourPoints = 100,
		Kit = KitFlags.Pack(new LoadoutSelection
		{
			Large = 3, Magazines = 9, Grenades = 0, Armour = BodyArmourTier.Heavy,
		}),
	};

	[Fact]
	public void Player_IsThirtyTwoBytesOnTheWire()
	{
		// docs/NETCODE.md §7 budgets ~30 B per player at 30-60 Hz. M2 spends three
		// of the remaining bytes on health, ammo and weapon flags, and the loadout's
		// record v2 three more on the spare rounds, the vest and the kit
		// (docs/LOADOUT.md §6.1).
		Assert.Equal(32, PlayerSnapshot.SizeBytes);
		Assert.Equal(5 + (8 * 32), SnapshotCodec.PayloadBytes(8));
	}

	[Fact]
	public void RoundTrip_PreservesStateWithinQuantizationError()
	{
		var players = new[] { Sample(1), Sample(1234567) };
		byte[] payload = SnapshotCodec.Encode(900, players);

		var into = new PlayerSnapshot[SnapshotCodec.MaxPlayers];
		Assert.True(SnapshotCodec.TryDecode(payload, into, out uint tick, out int count));
		Assert.Equal(900u, tick);
		Assert.Equal(2, count);

		for (int i = 0; i < count; i++)
		{
			Assert.Equal(players[i].PeerId, into[i].PeerId);
			Assert.Equal(players[i].LastInputTick, into[i].LastInputTick);
			Assert.Equal(players[i].StateId, into[i].StateId);
			Assert.Equal(players[i].InputBufferDepth, into[i].InputBufferDepth);
			Assert.Equal(players[i].Health, into[i].Health);
			Assert.Equal(players[i].Ammo, into[i].Ammo);
			Assert.Equal(players[i].WeaponFlags, into[i].WeaponFlags);
			Assert.Equal(270, into[i].Reserve);
			Assert.Equal(100, into[i].ArmourPoints);
			Assert.Equal(players[i].Kit, into[i].Kit);
			Assert.Equal(2, into[i].EquippedSlot);
			Assert.True(into[i].IsReloading);
			Assert.True(into[i].IsAlive);
			Assert.True((players[i].Position - into[i].Position).Length() < 0.01f);
			Assert.True((players[i].Velocity - into[i].Velocity).Length() < 0.01f);
			Assert.InRange(into[i].Yaw - players[i].Yaw, -1e-4f, 1e-4f);
			Assert.InRange(into[i].Pitch - players[i].Pitch, -1e-4f, 1e-4f);
		}
	}

	[Fact]
	public void RoundTrip_HandlesAnEmptyRoster()
	{
		byte[] payload = SnapshotCodec.Encode(12, System.ReadOnlySpan<PlayerSnapshot>.Empty);
		Assert.True(SnapshotCodec.TryDecode(payload, new PlayerSnapshot[4], out uint tick, out int count));
		Assert.Equal(12u, tick);
		Assert.Equal(0, count);
	}

	[Fact]
	public void Decode_RejectsAnOversizedPlayerCount()
	{
		var payload = new byte[SnapshotCodec.MaxPayloadBytes];
		payload[4] = 255;
		Assert.False(SnapshotCodec.TryDecode(payload, new PlayerSnapshot[SnapshotCodec.MaxPlayers], out _, out _));
	}

	[Fact]
	public void Decode_RejectsATruncatedPayload()
	{
		byte[] payload = SnapshotCodec.Encode(1, new[] { Sample(1), Sample(2) });
		Assert.False(SnapshotCodec.TryDecode(payload.AsSpan(0, payload.Length - 5), new PlayerSnapshot[4], out _, out _));
	}

	[Fact]
	public void TheKitByteCarriesTheVestTheMagazinesAndTheGrenades()
	{
		for (int magazines = 0; magazines <= Loadout.PouchSlots; magazines++)
		{
			for (int grenades = 0; grenades <= SimConfig.MaxGrenades; grenades++)
			{
				for (int tier = 0; tier <= (int)BodyArmourTier.Heavy; tier++)
				{
					byte kit = KitFlags.Pack(new LoadoutSelection
					{
						Magazines = (byte)magazines, Grenades = (byte)grenades, Armour = (BodyArmourTier)tier,
					});
					Assert.Equal(magazines, KitFlags.Magazines(kit));
					Assert.Equal(grenades, KitFlags.Grenades(kit));
					Assert.Equal((BodyArmourTier)tier, KitFlags.Vest(kit));
				}
			}
		}
	}

	[Fact]
	public void TheSpareWordHoldsNineRiflePouchesAndAHeavyVest()
	{
		ushort spare = KitFlags.PackSpare(270, 100);
		Assert.Equal(270, KitFlags.Reserve(spare));
		Assert.Equal(100, KitFlags.ArmourPoints(spare));

		// Clamped to their fields rather than spilling into each other.
		ushort over = KitFlags.PackSpare(10_000, 1_000);
		Assert.Equal(KitFlags.MaxReserve, KitFlags.Reserve(over));
		Assert.Equal(KitFlags.MaxArmourPoints, KitFlags.ArmourPoints(over));
		ushort under = KitFlags.PackSpare(-1, -5);
		Assert.Equal(0, KitFlags.Reserve(under));
		Assert.Equal(0, KitFlags.ArmourPoints(under));
		Assert.True(KitFlags.MaxReserve >= 9 * 30);
		Assert.True(KitFlags.MaxArmourPoints >= 100);
	}

	[Fact]
	public void TheSparesTwoFieldsNeverReachIntoEachOther()
	{
		// Nine bits and seven, filling the word: every pair at the fields' edges comes
		// back as it went in.
		Assert.Equal(16, 9 + 7);
		foreach (int reserve in new[] { 0, 1, 255, 256, 270, KitFlags.MaxReserve })
		{
			foreach (int armour in new[] { 0, 1, 35, 60, 100, KitFlags.MaxArmourPoints })
			{
				ushort spare = KitFlags.PackSpare(reserve, armour);
				Assert.Equal(reserve, KitFlags.Reserve(spare));
				Assert.Equal(armour, KitFlags.ArmourPoints(spare));
			}
		}
	}

	[Fact]
	public void AZeroLargeWeaponIsNoPrimary()
	{
		// Since record v2 the large bits' zero means no primary rather than "not said".
		byte flags = Sim.WeaponFlags.Pack(1, reloading: false, Team.GroundForce, largeChoice: -1, grenades: 2);
		Assert.Equal(-1, Sim.WeaponFlags.LargeChoice(flags));
		Assert.Equal(2, Sim.WeaponFlags.Grenades(flags));

		for (int choice = 0; choice < SimConfig.LargeWeaponChoices; choice++)
		{
			Assert.Equal(choice, Sim.WeaponFlags.LargeChoice(Sim.WeaponFlags.Pack(2, false, Team.GroundForce, choice)));
		}
	}

	[Fact]
	public void AMalformedKitByteDecodesToSomethingSanitizeCanTrim()
	{
		// Every bit set: fifteen magazines, three grenades, heavy. The reader sanitizes.
		var malformed = new PlayerSnapshot { PeerId = 9, Kit = 0xFF, Reserve = 0xFFFF, ArmourPoints = 0xFF };
		byte[] payload = SnapshotCodec.Encode(5, new[] { malformed });
		var into = new PlayerSnapshot[1];
		Assert.True(SnapshotCodec.TryDecode(payload, into, out _, out _));

		Assert.Equal(15, KitFlags.Magazines(into[0].Kit));
		Assert.Equal(KitFlags.MaxReserve, into[0].Reserve);
		Assert.Equal(KitFlags.MaxArmourPoints, into[0].ArmourPoints);

		LoadoutSelection kit = Loadout.Sanitize(new LoadoutSelection
		{
			Large = 3,
			Magazines = (byte)KitFlags.Magazines(into[0].Kit),
			Grenades = (byte)KitFlags.Grenades(into[0].Kit),
			Armour = KitFlags.Vest(into[0].Kit),
		}, new byte[] { 2, 3, 4 }, 3);
		Assert.True(kit.Magazines + kit.Grenades <= Loadout.PouchSlots);
	}

	[Fact]
	public void EightPlayersAtThirtyHertz_FitTheBandwidthBudget()
	{
		// docs/NETCODE.md §7: downstream per client should stay well inside 25 KB/s
		// with the RTS units still to come in M3.
		int bytesPerSecond = SnapshotCodec.PayloadBytes(8) * SimConfig.SnapshotRate;
		Assert.InRange(bytesPerSecond, 0, 15_000);
	}
}
