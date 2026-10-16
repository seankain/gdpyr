using System;
using System.Buffers.Binary;
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

	// ---- riding and flying (docs/VEHICLES.md §6.1) -------------------------

	[Fact]
	public void APassengerRoundTrips_WithItsVehicleInTheYawField()
	{
		UnitSnapshot rider = Sample(12);
		rider.RidingIn = 40_001;
		rider.Yaw = 2.5f;

		byte[] payload = UnitSnapshotCodec.Encode(77u, new[] { rider, Sample(13) });
		var into = new UnitSnapshot[2];
		Assert.True(UnitSnapshotCodec.TryDecode(payload, into, out _, out _));

		Assert.Equal((ushort)40_001, into[0].RidingIn);
		Assert.True(into[0].IsAboard);
		Assert.Equal(0f, into[0].Yaw);
		Assert.Equal(rider.Position.X, into[0].Position.X, 2);
		Assert.Equal(UnitStateId.Engaging, into[0].State);
		Assert.Equal(Team.Strategist, into[0].Team);

		// Its neighbour, on foot, is untouched.
		Assert.Equal((ushort)0, into[1].RidingIn);
		Assert.Equal(1.75f, into[1].Yaw, 3);
	}

	[Fact]
	public void AnAboardRecordNamingVehicleZero_DecodesOnFoot()
	{
		byte[] payload = UnitSnapshotCodec.Encode(1u, new[] { Sample(5) });
		int flags = UnitSnapshotCodec.HeaderBytes + 11;
		int yaw = UnitSnapshotCodec.HeaderBytes + 9;
		payload[flags] |= 1 << 3;
		payload[yaw] = 0;
		payload[yaw + 1] = 0;

		var into = new UnitSnapshot[1];
		Assert.True(UnitSnapshotCodec.TryDecode(payload, into, out _, out _));

		Assert.False(into[0].IsAboard);
		Assert.Equal((ushort)0, into[0].RidingIn);
	}

	[Theory]
	[InlineData(FlightPhase.Grounded, false)]
	[InlineData(FlightPhase.TakingOff, false)]
	[InlineData(FlightPhase.Cruising, true)]
	[InlineData(FlightPhase.Landing, false)]
	public void TheFlightPhaseAndCannotLand_RoundTrip(FlightPhase phase, bool cannotLand)
	{
		UnitSnapshot aircraft = Sample(3);
		aircraft.Phase = phase;
		aircraft.CannotLand = cannotLand;

		byte[] payload = UnitSnapshotCodec.Encode(1u, new[] { aircraft });
		var into = new UnitSnapshot[1];
		Assert.True(UnitSnapshotCodec.TryDecode(payload, into, out _, out _));

		Assert.Equal(phase, into[0].Phase);
		Assert.Equal(cannotLand, into[0].CannotLand);
		Assert.Equal(1.75f, into[0].Yaw, 3);
	}

	[Fact]
	public void AGroundRecord_IsTheBytesItAlwaysWas()
	{
		// Bits 3–7 clear: a record with nothing riding or flying encodes as before the
		// vehicles, so the two-argument pack and the five-argument one agree.
		UnitSnapshot unit = Sample(9);
		byte[] payload = UnitSnapshotCodec.Encode(1u, new[] { unit });

		Assert.Equal(UnitFlags.Pack(unit.State, unit.Team), payload[UnitSnapshotCodec.HeaderBytes + 11]);
		Assert.Equal(0, payload[UnitSnapshotCodec.HeaderBytes + 11] & 0b1111_1000);
		Assert.Equal(Quantize.YawToU16(unit.Yaw),
			BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(UnitSnapshotCodec.HeaderBytes + 9)));
	}

	[Theory]
	[InlineData(UnitStateId.Dead, Team.GroundForce, true, FlightPhase.Landing, true)]
	[InlineData(UnitStateId.Engaging, Team.Strategist, false, FlightPhase.Cruising, false)]
	[InlineData(UnitStateId.Idle, Team.Strategist, true, FlightPhase.Grounded, false)]
	[InlineData(UnitStateId.Moving, Team.GroundForce, false, FlightPhase.TakingOff, true)]
	public void StateTeamAndTheNewBits_ShareAByte(UnitStateId state, Team team, bool aboard, FlightPhase phase,
		bool cannotLand)
	{
		byte packed = UnitFlags.Pack(state, team, aboard, phase, cannotLand);

		Assert.Equal(state, UnitFlags.State(packed));
		Assert.Equal(team, UnitFlags.TeamOf(packed));
		Assert.Equal(aboard, UnitFlags.IsAboard(packed));
		Assert.Equal(phase, UnitFlags.PhaseOf(packed));
		Assert.Equal(cannotLand, UnitFlags.CannotLand(packed));
		Assert.Equal(0, packed & 0b1000_0000);
	}

	[Fact]
	public void AFullFieldOfPassengers_IsStillThirteenBytesEach_AndOneDatagram()
	{
		var units = new UnitSnapshot[UnitSnapshotCodec.MaxUnits];
		for (int i = 0; i < units.Length; i++)
		{
			units[i] = Sample((ushort)(i + 1));
			units[i].RidingIn = (ushort)(i % 2 == 0 ? 0 : 1);
		}

		byte[] payload = UnitSnapshotCodec.Encode(1u, units);

		Assert.Equal(UnitSnapshotCodec.MaxPayloadBytes, payload.Length);
		Assert.Equal(837, payload.Length);
		Assert.True(payload.Length < 1200);
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
