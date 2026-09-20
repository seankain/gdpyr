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
	};

	[Fact]
	public void Player_IsTwentySixBytesOnTheWire()
	{
		// docs/NETCODE.md §7 budgets ~30 B per player at 30-60 Hz.
		Assert.Equal(26, PlayerSnapshot.SizeBytes);
		Assert.Equal(5 + (8 * 26), SnapshotCodec.PayloadBytes(8));
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
	public void EightPlayersAtThirtyHertz_FitTheBandwidthBudget()
	{
		// docs/NETCODE.md §7: downstream per client should stay well inside 25 KB/s
		// with the RTS units still to come in M3.
		int bytesPerSecond = SnapshotCodec.PayloadBytes(8) * SimConfig.SnapshotRate;
		Assert.InRange(bytesPerSecond, 0, 15_000);
	}
}
