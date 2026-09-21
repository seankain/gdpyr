using System;
using Gdpyr.Sim;
using Godot;
using Xunit;

namespace Gdpyr.Tests;

public class UnitSnapshotCodecTests
{
	private static UnitSnapshot Sample(ushort id) => new()
	{
		UnitId = id,
		DefinitionId = 0,
		Position = new Vector3(12.34f, 0.5f, -87.65f),
		Yaw = 1.75f,
		State = UnitStateId.Engaging,
		Team = Team.Strategist,
		HealthPercent = 200,
	};

	[Fact]
	public void ARecordIsThirteenBytes()
	{
		// docs/NETCODE.md §7 budgets ~10 B per unit at 20 Hz; 13 B × 50 units × 20 Hz
		// is 13 KB/s, which the §7 budget has room for.
		Assert.Equal(13, UnitSnapshot.SizeBytes);
		Assert.Equal(UnitSnapshotCodec.HeaderBytes + 13, UnitSnapshotCodec.PayloadBytes(1));
	}

	[Fact]
	public void OneDatagramHoldsTheWholeField()
	{
		// Unreliable, so a fragmented packet would be a packet that is mostly lost
		// rather than mostly late.
		Assert.True(UnitSnapshotCodec.MaxPayloadBytes < 1200);
	}

	[Fact]
	public void RoundTripsAUnit()
	{
		byte[] payload = UnitSnapshotCodec.Encode(4321u, new[] { Sample(7) });
		var into = new UnitSnapshot[UnitSnapshotCodec.MaxUnits];

		Assert.True(UnitSnapshotCodec.TryDecode(payload, into, out uint tick, out int count));

		Assert.Equal(4321u, tick);
		Assert.Equal(1, count);
		Assert.Equal((ushort)7, into[0].UnitId);
		Assert.Equal(UnitStateId.Engaging, into[0].State);
		Assert.Equal(Team.Strategist, into[0].Team);
		Assert.Equal((byte)200, into[0].HealthPercent);
	}

	[Fact]
	public void PositionSurvivesWithinAQuantum()
	{
		byte[] payload = UnitSnapshotCodec.Encode(1u, new[] { Sample(1) });
		var into = new UnitSnapshot[1];
		UnitSnapshotCodec.TryDecode(payload, into, out _, out _);

		Assert.Equal(12.34f, into[0].Position.X, 2);
		Assert.Equal(0.5f, into[0].Position.Y, 2);
		Assert.Equal(-87.65f, into[0].Position.Z, 2);
		Assert.Equal(1.75f, into[0].Yaw, 3);
	}

	[Fact]
	public void RoundTripsAFullField()
	{
		var units = new UnitSnapshot[UnitSnapshotCodec.MaxUnits];
		for (int i = 0; i < units.Length; i++)
		{
			units[i] = Sample((ushort)(i + 1));
		}

		byte[] payload = UnitSnapshotCodec.Encode(9u, units);
		var into = new UnitSnapshot[UnitSnapshotCodec.MaxUnits];

		Assert.True(UnitSnapshotCodec.TryDecode(payload, into, out _, out int count));
		Assert.Equal(UnitSnapshotCodec.MaxUnits, count);
		Assert.Equal((ushort)UnitSnapshotCodec.MaxUnits, into[^1].UnitId);
	}

	[Fact]
	public void MoreUnitsThanFitAreTruncatedRatherThanOverflowing()
	{
		var units = new UnitSnapshot[UnitSnapshotCodec.MaxUnits + 8];
		for (int i = 0; i < units.Length; i++)
		{
			units[i] = Sample((ushort)(i + 1));
		}

		byte[] payload = UnitSnapshotCodec.Encode(1u, units);

		Assert.Equal(UnitSnapshotCodec.MaxPayloadBytes, payload.Length);
	}

	// ---- decoding is total (docs/NETCODE.md §7) ----------------------------

	[Fact]
	public void ATruncatedHeaderIsRefused()
	{
		var into = new UnitSnapshot[4];

		Assert.False(UnitSnapshotCodec.TryDecode(new byte[3], into, out _, out _));
	}

	[Fact]
	public void ATruncatedBodyIsRefused()
	{
		byte[] payload = UnitSnapshotCodec.Encode(1u, new[] { Sample(1), Sample(2) });
		var into = new UnitSnapshot[4];

		Assert.False(UnitSnapshotCodec.TryDecode(payload.AsSpan(0, payload.Length - 3), into, out _, out _));
	}

	[Fact]
	public void AnImpossibleCountIsRefused()
	{
		byte[] payload = UnitSnapshotCodec.Encode(1u, new[] { Sample(1) });
		payload[4] = byte.MaxValue;
		var into = new UnitSnapshot[UnitSnapshotCodec.MaxUnits];

		Assert.False(UnitSnapshotCodec.TryDecode(payload, into, out _, out _));
	}

	[Fact]
	public void ADestinationTooSmallToHoldTheResultIsRefused()
	{
		byte[] payload = UnitSnapshotCodec.Encode(1u, new[] { Sample(1), Sample(2) });

		Assert.False(UnitSnapshotCodec.TryDecode(payload, new UnitSnapshot[1], out _, out _));
	}

	[Fact]
	public void AnEmptySnapshotIsValid()
	{
		byte[] payload = UnitSnapshotCodec.Encode(5u, Array.Empty<UnitSnapshot>());
		var into = new UnitSnapshot[1];

		Assert.True(UnitSnapshotCodec.TryDecode(payload, into, out uint tick, out int count));
		Assert.Equal(5u, tick);
		Assert.Equal(0, count);
	}

	// ---- the packed byte ---------------------------------------------------

	[Theory]
	[InlineData(UnitStateId.Idle, Team.GroundForce)]
	[InlineData(UnitStateId.Moving, Team.Strategist)]
	[InlineData(UnitStateId.Engaging, Team.GroundForce)]
	[InlineData(UnitStateId.Dead, Team.Strategist)]
	public void StateAndTeamShareAByte(UnitStateId state, Team team)
	{
		byte packed = UnitFlags.Pack(state, team);

		Assert.Equal(state, UnitFlags.State(packed));
		Assert.Equal(team, UnitFlags.TeamOf(packed));
	}

	[Fact]
	public void HealthScalesToAByteWithoutRoundingALiveUnitToDead()
	{
		Assert.Equal((byte)0, UnitFlags.PackHealth(0f, 100f));
		Assert.Equal(byte.MaxValue, UnitFlags.PackHealth(100f, 100f));
		Assert.Equal(byte.MaxValue, UnitFlags.PackHealth(150f, 100f));

		// One hit point out of a thousand is 0.255 of a byte, and rounding it down
		// would draw a live unit as a corpse.
		Assert.True(UnitFlags.PackHealth(1f, 1000f) >= 1);
	}

	[Fact]
	public void AHealthPercentOfZeroReadsAsDead()
	{
		UnitSnapshot snapshot = Sample(1);
		snapshot.HealthPercent = 0;

		Assert.False(snapshot.IsAlive);
	}
}
