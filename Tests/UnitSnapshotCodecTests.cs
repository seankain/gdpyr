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
	public void ARecordIsFourteenBytes()
	{
		// docs/NETCODE.md §7: 14 B × 64 units × 20 Hz is 17.9 KB/s at a full field, the
		// v2 record's byte more (docs/TACTICS.md §5.8) 1.3 KB/s of it.
		Assert.Equal(14, UnitSnapshot.SizeBytes);
		Assert.Equal(UnitSnapshotCodec.HeaderBytes + 14, UnitSnapshotCodec.PayloadBytes(1));
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

	// ---- the body byte -----------------------------------------------------

	[Theory]
	[InlineData(UnitStateId.Idle, Team.GroundForce)]
	[InlineData(UnitStateId.Moving, Team.Strategist)]
	[InlineData(UnitStateId.Engaging, Team.GroundForce)]
	[InlineData(UnitStateId.Dead, Team.Strategist)]
	public void StateAndTeamShareAByte(UnitStateId state, Team team)
	{
		byte packed = UnitBodyFlags.Pack(state, team);

		Assert.Equal(state, UnitBodyFlags.State(packed));
		Assert.Equal(team, UnitBodyFlags.TeamOf(packed));
		Assert.Equal(UnitPosture.Standing, UnitBodyFlags.PostureOf(packed));
	}

	// ---- posture (docs/TACTICS.md §5.8) --------------------------------------

	[Theory]
	[InlineData(UnitPosture.Standing)]
	[InlineData(UnitPosture.Crouched)]
	public void ThePostureRoundTrips_AndTouchesNothingElse(UnitPosture posture)
	{
		UnitSnapshot unit = Sample(21);
		unit.Posture = posture;
		unit.Phase = FlightPhase.Cruising;
		unit.CannotLand = true;

		byte[] payload = UnitSnapshotCodec.Encode(1u, new[] { unit, Sample(22) });
		var into = new UnitSnapshot[2];
		Assert.True(UnitSnapshotCodec.TryDecode(payload, into, out _, out _));

		Assert.Equal(posture, into[0].Posture);
		Assert.Equal(UnitStateId.Engaging, into[0].State);
		Assert.Equal(Team.Strategist, into[0].Team);
		Assert.Equal(FlightPhase.Cruising, into[0].Phase);
		Assert.True(into[0].CannotLand);
		Assert.Equal((byte)200, into[0].HealthPercent);
		Assert.Equal(1.75f, into[0].Yaw, 3);
		Assert.Equal(UnitPosture.Standing, into[1].Posture);
	}

	[Theory]
	[InlineData(2)]
	[InlineData(3)]
	public void AReservedPosture_DecodesStanding(int reserved)
	{
		byte[] payload = UnitSnapshotCodec.Encode(1u, new[] { Sample(4) });
		int body = UnitSnapshotCodec.HeaderBytes + 11;
		payload[body] = (byte)((payload[body] & 0b1100_1111) | (reserved << 4));

		var into = new UnitSnapshot[1];
		Assert.True(UnitSnapshotCodec.TryDecode(payload, into, out _, out _));

		Assert.Equal(UnitPosture.Standing, into[0].Posture);
		Assert.Equal(UnitStateId.Engaging, into[0].State);
	}

	[Fact]
	public void EveryValueOfEitherFlagByte_DecodesToSomethingNamed()
	{
		// Total: no byte a peer sends makes a value outside its enum.
		for (int value = 0; value < 256; value++)
		{
			foreach (int offset in new[] { 11, 12 })
			{
				byte[] payload = UnitSnapshotCodec.Encode(1u, new[] { Sample(6) });
				payload[UnitSnapshotCodec.HeaderBytes + offset] = (byte)value;
				var into = new UnitSnapshot[1];
				Assert.True(UnitSnapshotCodec.TryDecode(payload, into, out _, out _));

				Assert.True(Enum.IsDefined(into[0].Posture));
				Assert.True(Enum.IsDefined(into[0].Phase));
				Assert.True(Enum.IsDefined(into[0].State));
			}
		}
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
	public void AStandingGroundRecord_IsStateAndTeam_AndAZeroFlightByte()
	{
		// Nothing riding, flying or crouched: the body byte is the two-argument pack, and
		// the flight byte is zero, as it is for everything that does not fly.
		UnitSnapshot unit = Sample(9);
		byte[] payload = UnitSnapshotCodec.Encode(1u, new[] { unit });

		Assert.Equal(UnitBodyFlags.Pack(unit.State, unit.Team), payload[UnitSnapshotCodec.HeaderBytes + 11]);
		Assert.Equal(0, payload[UnitSnapshotCodec.HeaderBytes + 11] & 0b1111_1000);
		Assert.Equal(0, payload[UnitSnapshotCodec.HeaderBytes + 12]);
		Assert.Equal(unit.HealthPercent, payload[UnitSnapshotCodec.HeaderBytes + 13]);
		Assert.Equal(Quantize.YawToU16(unit.Yaw),
			BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(UnitSnapshotCodec.HeaderBytes + 9)));
	}

	[Theory]
	[InlineData(UnitStateId.Dead, Team.GroundForce, true, UnitPosture.Standing)]
	[InlineData(UnitStateId.Engaging, Team.Strategist, false, UnitPosture.Crouched)]
	[InlineData(UnitStateId.Idle, Team.Strategist, true, UnitPosture.Crouched)]
	[InlineData(UnitStateId.Moving, Team.GroundForce, false, UnitPosture.Standing)]
	public void StateTeamAboardAndPosture_ShareTheBodyByte(UnitStateId state, Team team, bool aboard,
		UnitPosture posture)
	{
		byte packed = UnitBodyFlags.Pack(state, team, aboard, posture);

		Assert.Equal(state, UnitBodyFlags.State(packed));
		Assert.Equal(team, UnitBodyFlags.TeamOf(packed));
		Assert.Equal(aboard, UnitBodyFlags.IsAboard(packed));
		Assert.Equal(posture, UnitBodyFlags.PostureOf(packed));
		Assert.Equal(0, packed & 0b1100_0000);
	}

	[Theory]
	[InlineData(FlightPhase.Grounded, false)]
	[InlineData(FlightPhase.TakingOff, true)]
	[InlineData(FlightPhase.Cruising, true)]
	[InlineData(FlightPhase.Landing, false)]
	public void ThePhaseAndCannotLand_ShareTheFlightByte(FlightPhase phase, bool cannotLand)
	{
		byte packed = UnitFlightFlags.Pack(phase, cannotLand);

		Assert.Equal(phase, UnitFlightFlags.PhaseOf(packed));
		Assert.Equal(cannotLand, UnitFlightFlags.CannotLand(packed));
		Assert.Equal(0, packed & 0b1111_1000);
	}

	[Fact]
	public void AFullFieldOfPassengers_IsFourteenBytesEach_AndOneDatagram()
	{
		var units = new UnitSnapshot[UnitSnapshotCodec.MaxUnits];
		for (int i = 0; i < units.Length; i++)
		{
			units[i] = Sample((ushort)(i + 1));
			units[i].RidingIn = (ushort)(i % 2 == 0 ? 0 : 1);
		}

		byte[] payload = UnitSnapshotCodec.Encode(1u, units);

		Assert.Equal(UnitSnapshotCodec.MaxPayloadBytes, payload.Length);
		Assert.Equal(901, payload.Length);
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
