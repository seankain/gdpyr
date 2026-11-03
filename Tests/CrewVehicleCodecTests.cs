using System;
using Gdpyr.Sim;
using Godot;
using Xunit;

namespace Gdpyr.Tests;

/// <summary>
/// <c>ServerCrewVehicles</c> (docs/GROUND_VEHICLES.md §7.1): its size, what survives the
/// trip, a decoder nothing can crash, and a record whose state the driver's replay can
/// start from (§8.2).
/// </summary>
public class CrewVehicleCodecTests
{
	private const float Dt = SimConfig.TickDelta;

	private static CrewVehicleRecord Truck(byte slot = 0) => new()
	{
		Slot = slot,
		Kind = CrewVehicleKind.FourByFour,
		Destroyed = false,
		Airborne = true,
		DriverSeated = true,
		GunReloading = true,
		Position = new Vector3(-81.37f, 1.204f, 63.919f),
		Velocity = new Vector3(12.345f, -0.42f, -9.876f),
		Yaw = -2.75f,
		YawRate = 0.8123f,
		Pitch = 0.1234f,
		PitchRate = -1.5678f,
		Roll = -0.0567f,
		RollRate = 2.3456f,
		Steer = -0.4321f,
		Health = 412,
		GunRounds = 87,
		DriverInputTick = 123_456,
		Seat0 = 1_234_567,
		Seat1 = 0,
		Seat2 = 1,
	};

	private static CrewVehicleRecord Helicopter(byte slot = 1) => new()
	{
		Slot = slot,
		Kind = CrewVehicleKind.Helicopter,
		Airborne = true,
		DriverSeated = true,
		Position = new Vector3(40.5f, 17.25f, -12.75f),
		Velocity = new Vector3(3f, 1.5f, -4f),
		Yaw = 1.25f,
		YawRate = -1.5f,
		Pitch = -0.2f,
		PitchRate = 0.25f,
		Roll = 0.15f,
		RollRate = -0.5f,
		Rotor = 0.73f,
		Health = 380,
		DriverInputTick = 99,
		Seat0 = 7,
		Seat1 = 8,
	};

	private static readonly KioskLine[] Kiosk = { new(KioskStatus.Ready, 0), new(KioskStatus.Counting, 42) };

	[Fact]
	public void TheHeaderIsNineBytes_AndAVehicleFortySix()
	{
		Assert.Equal(9, CrewVehicleCodec.HeaderBytes);
		Assert.Equal(46, CrewVehicleRecord.SizeBytes);
		Assert.Equal(101, CrewVehicleCodec.PayloadBytes(2));
		Assert.Equal(193, CrewVehicleCodec.PayloadBytes(4));
		Assert.Equal(193, CrewVehicleCodec.MaxPayloadBytes);
		Assert.Equal(101, CrewVehicleCodec.Encode(1, Kiosk, new[] { Truck(0), Helicopter(1) }).Length);
	}

	[Fact]
	public void TwoVehiclesAtThirtyHertz_AreAboutThreeKilobytesASecond()
	{
		// §7.1: "Two vehicles are 101 bytes, 3.0 KB/s per client."
		float bytesPerSecond = CrewVehicleCodec.PayloadBytes(2) * SimConfig.SnapshotRate;
		Assert.InRange(bytesPerSecond / 1024f, 2.9f, 3.0f);
	}

	[Fact]
	public void ARoundTrip_KeepsEverything_WithinTheWiresResolution()
	{
		CrewVehicleRecord[] sent = { Truck(2), Helicopter(0) };
		byte[] payload = CrewVehicleCodec.Encode(777, Kiosk, sent);

		var kiosk = new KioskLine[CrewVehicleCodec.Kinds];
		var got = new CrewVehicleRecord[CrewVehicleCodec.MaxVehicles];
		Assert.True(CrewVehicleCodec.TryDecode(payload, kiosk, got, out uint tick, out int count));
		Assert.Equal(777u, tick);
		Assert.Equal(2, count);
		Assert.Equal(KioskStatus.Ready, kiosk[0].Status);
		Assert.Equal(KioskStatus.Counting, kiosk[1].Status);
		Assert.Equal(42, kiosk[1].SecondsLeft);

		for (int i = 0; i < count; i++)
		{
			CrewVehicleRecord a = sent[i];
			CrewVehicleRecord b = got[i];
			Assert.Equal(a.Slot, b.Slot);
			Assert.Equal(a.Kind, b.Kind);
			Assert.Equal(a.Destroyed, b.Destroyed);
			Assert.Equal(a.Airborne, b.Airborne);
			Assert.Equal(a.DriverSeated, b.DriverSeated);
			Assert.Equal(a.GunReloading, b.GunReloading);
			Assert.Equal(a.Health, b.Health);
			Assert.Equal(a.GunRounds, b.GunRounds);
			Assert.Equal(a.DriverInputTick, b.DriverInputTick);
			Assert.Equal(a.Seat0, b.Seat0);
			Assert.Equal(a.Seat1, b.Seat1);
			Assert.Equal(a.Seat2, b.Seat2);

			for (int axis = 0; axis < 3; axis++)
			{
				Assert.InRange(MathF.Abs(a.Position[axis] - b.Position[axis]), 0f, SimConfig.PositionQuantum * 0.5f + 1e-5f);
				Assert.InRange(MathF.Abs(a.Velocity[axis] - b.Velocity[axis]), 0f, SimConfig.PositionQuantum * 0.5f + 1e-5f);
			}

			Assert.InRange(MathF.Abs(DroneSim.AngleBetween(a.Yaw, b.Yaw)), 0f, 1e-4f);
			Assert.InRange(MathF.Abs(a.Pitch - b.Pitch), 0f, 1e-4f);
			Assert.InRange(MathF.Abs(a.Roll - b.Roll), 0f, 1e-4f);
			Assert.InRange(MathF.Abs(a.YawRate - b.YawRate), 0f, CrewVehicleCodec.RateQuantum * 0.5f + 1e-6f);
			Assert.InRange(MathF.Abs(a.PitchRate - b.PitchRate), 0f, CrewVehicleCodec.RateQuantum * 0.5f + 1e-6f);
			Assert.InRange(MathF.Abs(a.RollRate - b.RollRate), 0f, CrewVehicleCodec.RateQuantum * 0.5f + 1e-6f);
		}

		Assert.InRange(MathF.Abs(sent[0].Steer - got[0].Steer), 0f, CrewVehicleCodec.SteerQuantum * 0.5f + 1e-6f);
		Assert.InRange(MathF.Abs(sent[1].Rotor - got[1].Rotor), 0f, 0.5f / 255f + 1e-6f);
	}

	[Fact]
	public void EachFlag_IsItsOwnBit()
	{
		for (int bit = 0; bit < 4; bit++)
		{
			var record = new CrewVehicleRecord
			{
				Kind = CrewVehicleKind.Helicopter,
				Destroyed = bit == 0,
				Airborne = bit == 1,
				DriverSeated = bit == 2,
				GunReloading = bit == 3,
			};

			CrewVehicleRecord back = record.Quantized();
			Assert.Equal(CrewVehicleKind.Helicopter, back.Kind);
			Assert.Equal(bit == 0, back.Destroyed);
			Assert.Equal(bit == 1, back.Airborne);
			Assert.Equal(bit == 2, back.DriverSeated);
			Assert.Equal(bit == 3, back.GunReloading);
		}
	}

	[Fact]
	public void TheSteeringByte_CoversTheFullLock_AndTheRotorByteItsRange()
	{
		float fullLock = FourByFourTraits.Default.SteerLimitAtRest;
		foreach (float steer in new[] { fullLock, -fullLock })
		{
			var record = new CrewVehicleRecord { Kind = CrewVehicleKind.FourByFour, Steer = steer };
			Assert.InRange(MathF.Abs(record.Quantized().Steer - steer), 0f, CrewVehicleCodec.SteerQuantum * 0.5f + 1e-6f);
		}

		Assert.Equal(1f, new CrewVehicleRecord { Kind = CrewVehicleKind.Helicopter, Rotor = 1f }.Quantized().Rotor);
		Assert.Equal(0f, new CrewVehicleRecord { Kind = CrewVehicleKind.Helicopter, Rotor = -3f }.Quantized().Rotor);
	}

	[Fact]
	public void OutOfRangeAndNonFiniteNumbers_AreClampedOrZero_NeverGarbage()
	{
		var record = new CrewVehicleRecord
		{
			Kind = CrewVehicleKind.FourByFour,
			Position = new Vector3(float.NaN, 1e9f, float.NegativeInfinity),
			YawRate = 1e6f,
			PitchRate = float.NaN,
			Pitch = float.PositiveInfinity,
			Steer = 9f,
		};

		CrewVehicleRecord back = record.Quantized();
		Assert.Equal(0f, back.Position.X);
		Assert.Equal(SimConfig.MaxPositionMeters, back.Position.Y, 3);
		Assert.Equal(0f, back.Position.Z);
		Assert.Equal(short.MaxValue * CrewVehicleCodec.RateQuantum, back.YawRate, 3);
		Assert.Equal(0f, back.PitchRate);
		Assert.Equal(0f, back.Pitch);
		Assert.Equal(sbyte.MaxValue * CrewVehicleCodec.SteerQuantum, back.Steer, 4);
		Assert.Equal(0, CrewVehicleCodec.HealthToU16(float.NaN));
		Assert.Equal(1, CrewVehicleCodec.HealthToU16(0.2f));
		Assert.Equal(600, CrewVehicleCodec.HealthToU16(600f));
	}

	[Fact]
	public void Quantizing_IsIdempotent()
	{
		// What the server sends and what the driver restores are the same numbers however
		// many times they pass through the wire's rounding (§14's mitigation rests on it).
		foreach (CrewVehicleRecord record in new[] { Truck(), Helicopter() })
		{
			CrewVehicleRecord once = record.Quantized();
			CrewVehicleRecord twice = once.Quantized();
			Assert.Equal(once.Position, twice.Position);
			Assert.Equal(once.Velocity, twice.Velocity);
			Assert.Equal(once.Yaw, twice.Yaw);
			Assert.Equal(once.YawRate, twice.YawRate);
			Assert.Equal(once.Pitch, twice.Pitch);
			Assert.Equal(once.PitchRate, twice.PitchRate);
			Assert.Equal(once.Roll, twice.Roll);
			Assert.Equal(once.RollRate, twice.RollRate);
			Assert.Equal(once.Steer, twice.Steer);
			Assert.Equal(once.Rotor, twice.Rotor);
		}
	}

	[Fact]
	public void EveryTruncation_IsRefused()
	{
		byte[] payload = CrewVehicleCodec.Encode(5, Kiosk, new[] { Truck(0), Helicopter(1) });
		var kiosk = new KioskLine[CrewVehicleCodec.Kinds];
		var into = new CrewVehicleRecord[CrewVehicleCodec.MaxVehicles];
		for (int length = 0; length < payload.Length; length++)
		{
			Assert.False(CrewVehicleCodec.TryDecode(payload.AsSpan(0, length), kiosk, into, out _, out _));
		}

		Assert.True(CrewVehicleCodec.TryDecode(payload, kiosk, into, out _, out int count));
		Assert.Equal(2, count);
	}

	[Fact]
	public void ACountPastFour_ASlotPastThree_OrASlotNamedTwice_IsRefused()
	{
		var kiosk = new KioskLine[CrewVehicleCodec.Kinds];
		var into = new CrewVehicleRecord[8];

		byte[] tooMany = new byte[CrewVehicleCodec.PayloadBytes(5)];
		tooMany[4] = 5;
		Assert.False(CrewVehicleCodec.TryDecode(tooMany, kiosk, into, out _, out _));

		byte[] badSlot = CrewVehicleCodec.Encode(5, Kiosk, new[] { Truck(0) });
		badSlot[CrewVehicleCodec.HeaderBytes] = 4;
		Assert.False(CrewVehicleCodec.TryDecode(badSlot, kiosk, into, out _, out _));

		byte[] twice = CrewVehicleCodec.Encode(5, Kiosk, new[] { Truck(2), Helicopter(2) });
		Assert.False(CrewVehicleCodec.TryDecode(twice, kiosk, into, out _, out _));
	}

	[Fact]
	public void AKioskStatusThisBuildDoesNotKnow_ReadsAsDisabled()
	{
		byte[] payload = CrewVehicleCodec.Encode(5, Kiosk, ReadOnlySpan<CrewVehicleRecord>.Empty);
		payload[5] = 200;
		var kiosk = new KioskLine[CrewVehicleCodec.Kinds];
		Assert.True(CrewVehicleCodec.TryDecode(payload, kiosk, new CrewVehicleRecord[4], out _, out int count));
		Assert.Equal(0, count);
		Assert.Equal(KioskStatus.Disabled, kiosk[0].Status);
		Assert.Equal(KioskStatus.Counting, kiosk[1].Status);
	}

	[Fact]
	public void RandomBytes_NeverThrow()
	{
		var random = new Random(31);
		var kiosk = new KioskLine[CrewVehicleCodec.Kinds];
		var into = new CrewVehicleRecord[CrewVehicleCodec.MaxVehicles];
		for (int i = 0; i < 20_000; i++)
		{
			byte[] bytes = new byte[random.Next(0, CrewVehicleCodec.MaxPayloadBytes + 8)];
			random.NextBytes(bytes);
			if (bytes.Length > 4 && random.Next(2) == 0)
			{
				bytes[4] = (byte)random.Next(0, 6);
			}

			if (CrewVehicleCodec.TryDecode(bytes, kiosk, into, out _, out int count))
			{
				Assert.InRange(count, 0, CrewVehicleCodec.MaxVehicles);
				for (int v = 0; v < count; v++)
				{
					Assert.InRange((int)into[v].Slot, 0, CrewVehicleCodec.MaxVehicles - 1);
					Assert.True(float.IsFinite(into[v].Position.X) && float.IsFinite(into[v].Steer));
				}
			}
		}
	}

	[Fact]
	public void TheSeats_NameWhoSitsWhere()
	{
		CrewVehicleRecord record = Truck();
		Assert.Equal(0, record.SeatOf(1_234_567));
		Assert.Equal(2, record.SeatOf(1));
		Assert.Equal(-1, record.SeatOf(0));
		Assert.Equal(-1, record.SeatOf(42));
		record.SetSeatPeer(1, 42);
		Assert.Equal(1, record.SeatOf(42));
		Assert.Equal(0, record.SeatPeer(3));
	}

	[Fact]
	public void AFourByFoursState_SurvivesTheTrip_AsItsStepReadsIt()
	{
		var state = new FourByFourState
		{
			Position = new Vector3(10f, 0.71f, -20f),
			Velocity = new Vector3(5f, 0.1f, 2f),
			Yaw = 3.1f,
			YawRate = 0.4f,
			Pitch = 0.05f,
			PitchRate = -0.3f,
			Roll = -0.02f,
			RollRate = 0.6f,
			Steer = 0.25f,
			AirTicks = 17,
		};

		var record = new CrewVehicleRecord { Kind = CrewVehicleKind.FourByFour };
		record.SetDrive(state);
		FourByFourState back = record.Quantized().ToDrive();

		Assert.True((back.Position - state.Position).Length() < 0.01f);
		Assert.True((back.Velocity - state.Velocity).Length() < 0.01f);
		Assert.InRange(MathF.Abs(DroneSim.AngleBetween(state.Yaw, back.Yaw)), 0f, 1e-4f);
		Assert.InRange(back.Yaw, -MathF.PI, MathF.PI);
		Assert.InRange(MathF.Abs(back.Steer - state.Steer), 0f, CrewVehicleCodec.SteerQuantum);
		Assert.True(back.Airborne);
		Assert.Equal(1, back.AirTicks);

		state.AirTicks = 0;
		record.SetDrive(state);
		Assert.False(record.Quantized().ToDrive().Airborne);
	}

	[Fact]
	public void AHelicoptersState_SurvivesTheTrip()
	{
		HelicopterState state = HelicopterState.At(new Vector3(1f, 2f, 3f), -1f);
		state.Rotor = 0.5f;
		state.Grounded = false;
		state.Velocity = new Vector3(0f, 2f, -5f);

		var record = new CrewVehicleRecord { Kind = CrewVehicleKind.Helicopter };
		record.SetFlight(state);
		HelicopterState back = record.Quantized().ToFlight();
		Assert.False(back.Grounded);
		Assert.InRange(MathF.Abs(back.Rotor - 0.5f), 0f, 1f / 255f);
		Assert.True((back.Velocity - state.Velocity).Length() < 0.01f);
		Assert.InRange(MathF.Abs(DroneSim.AngleBetween(state.Yaw, back.Yaw)), 0f, 1e-4f);

		state.Grounded = true;
		record.SetFlight(state);
		Assert.True(record.Quantized().ToFlight().Grounded);
	}

	// ---- a replay from the wire --------------------------------------------------------

	private static WheelContact[] Flat(in FourByFourState state)
	{
		var wheels = new WheelContact[FourByFourSim.Wheels];
		FourByFourTraits traits = FourByFourTraits.Default;
		for (int i = 0; i < wheels.Length; i++)
		{
			float distance = FourByFourSim.Hardpoint(state, traits, i).Y;
			wheels[i] = distance >= 0f && distance <= traits.RayLengthMeters ? WheelContact.At(distance) : WheelContact.Miss;
		}

		return wheels;
	}

	private static void Tick(ref FourByFourState state, in InputFrame frame)
	{
		Vector3 velocity = FourByFourSim.Step(ref state, FourByFourTraits.Default, frame, Flat(state), Dt);
		FourByFourSim.AfterMove(ref state, state.Position + (velocity * Dt), velocity);
	}

	[Theory]
	[InlineData(1f, 0f, false)]
	[InlineData(1f, 1f, false)]
	[InlineData(1f, -0.6f, true)]
	[InlineData(-1f, 0.5f, false)]
	public void TwelveTicksReplayedFromTheWiresState_LandWithinTheLedgersThresholds(float throttle, float steer,
		bool handbrake)
	{
		// The driver's replay starts from the record, not from the server's own numbers
		// (§8.2). Over the longest replay GV3 allows — twelve ticks — the rounding must not
		// grow past what CrewLedger calls in sync, or every replay would be followed by
		// another.
		FourByFourState server = FourByFourState.At(new Vector3(0f, FourByFourTraits.Default.RideHeightMeters, 0f), 0.3f);
		for (int t = 0; t < 180; t++)
		{
			Tick(ref server, InputFrame.Create((uint)t, 0f, -1f, 0f, 0f, InputButtons.None));
		}

		for (int t = 0; t < 30; t++)
		{
			Tick(ref server, InputFrame.Create((uint)t, steer, -throttle, 0f, 0f,
				handbrake ? InputButtons.Jump : InputButtons.None));
		}

		var record = new CrewVehicleRecord { Kind = CrewVehicleKind.FourByFour };
		record.SetDrive(server);
		FourByFourState client = record.Quantized().ToDrive();

		for (int t = 0; t < 12; t++)
		{
			var frame = InputFrame.Create((uint)t, steer, -throttle, 0f, 0f, handbrake ? InputButtons.Jump : InputButtons.None);
			Tick(ref server, frame);
			Tick(ref client, frame);
		}

		Assert.InRange((client.Position - server.Position).Length(), 0f, CrewLedger.PositionThresholdMeters);
		Assert.InRange((client.Velocity - server.Velocity).Length(), 0f, CrewLedger.VelocityThreshold);
		Assert.InRange(MathF.Abs(DroneSim.AngleBetween(client.Yaw, server.Yaw)), 0f, CrewLedger.AttitudeThresholdRadians);
		Assert.InRange(MathF.Abs(client.Pitch - server.Pitch), 0f, CrewLedger.AttitudeThresholdRadians);
		Assert.InRange(MathF.Abs(client.Roll - server.Roll), 0f, CrewLedger.AttitudeThresholdRadians);
	}
}
