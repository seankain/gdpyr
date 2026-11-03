using System;
using System.Buffers.Binary;
using Godot;

namespace Gdpyr.Sim;

/// <summary>
/// The kiosk's line for one kind of crew vehicle, as the header of
/// <see cref="CrewVehicleCodec"/> carries it (docs/GROUND_VEHICLES.md §7.1): what the HUD
/// shows under the round's line. State in a message that is going out anyway — the
/// drone shelf's precedent (docs/CONTROL_POINTS.md §4.4).
/// </summary>
public readonly struct KioskLine
{
	public readonly KioskStatus Status;

	/// <summary>While <see cref="KioskStatus.Counting"/>, the whole seconds left; 0 otherwise.</summary>
	public readonly byte SecondsLeft;

	public KioskLine(KioskStatus status, int secondsLeft)
	{
		Status = status;
		SecondsLeft = (byte)Math.Clamp(secondsLeft, 0, byte.MaxValue);
	}

	/// <summary>A kind the field offers no kiosk for — every kind until the kiosk is on the map (GV6).</summary>
	public static KioskLine None => new(KioskStatus.Disabled, 0);
}

/// <summary>
/// One crew vehicle as <c>ServerCrewVehicles</c> carries it (docs/GROUND_VEHICLES.md
/// §7.1): enough for every client to draw it and who sits in it, and for its driver's
/// client to restore the state its replay starts from (§8.2). 46 bytes on the wire.
///
/// <list type="table">
/// <item><term>0</term><description>slot, 0–3</description></item>
/// <item><term>1</term><description>kind (bits 0–1) · destroyed · airborne · driver seated · gun reloading</description></item>
/// <item><term>2</term><description>position, 3 × i16 cm</description></item>
/// <item><term>8</term><description>velocity, 3 × i16 cm/s</description></item>
/// <item><term>14</term><description>yaw, u16 over a turn</description></item>
/// <item><term>16</term><description>yaw rate, i16 mrad/s</description></item>
/// <item><term>18</term><description>pitch, pitch rate, roll, roll rate: i16 over ±90° and i16 mrad/s</description></item>
/// <item><term>26</term><description>the 4x4's steering (i8, 5 mrad), or the helicopter's rotor (u8, /255)</description></item>
/// <item><term>27</term><description>health, u16</description></item>
/// <item><term>29</term><description>rounds in the gun's box</description></item>
/// <item><term>30</term><description>the driver's last input consumed, u32; 0 with nobody driving</description></item>
/// <item><term>34</term><description>the seats, 3 × i32 peer id, 0 for empty</description></item>
/// </list>
/// </summary>
public struct CrewVehicleRecord
{
	public const int SizeBytes = 46;

	public byte Slot;

	/// <summary>The kind's id, as the catalog numbers it; bits 0–1 on the wire.</summary>
	public byte KindId;

	public bool Destroyed;

	/// <summary>The 4x4 with fewer than two wheels down; the helicopter off the ground.</summary>
	public bool Airborne;

	public bool DriverSeated;

	public bool GunReloading;

	public Vector3 Position;

	public Vector3 Velocity;

	public float Yaw;

	public float YawRate;

	public float Pitch;

	public float PitchRate;

	public float Roll;

	public float RollRate;

	/// <summary>The 4x4's front wheels [rad]; unused for a helicopter.</summary>
	public float Steer;

	/// <summary>The helicopter's rotor, 0 to 1; unused for a 4x4.</summary>
	public float Rotor;

	public ushort Health;

	public byte GunRounds;

	/// <summary>
	/// The newest input frame of the driver's that the server had consumed when this state
	/// was taken — their acknowledgement, which <see cref="CrewLedger.Decide"/> reads. 0
	/// with nobody at the controls.
	/// </summary>
	public uint DriverInputTick;

	public int Seat0;

	public int Seat1;

	public int Seat2;

	public CrewVehicleKind Kind
	{
		readonly get => (CrewVehicleKind)KindId;
		set => KindId = (byte)value;
	}

	/// <summary>Who is in seat <paramref name="seat"/>: a peer id, or 0.</summary>
	public readonly int SeatPeer(int seat) => seat switch
	{
		0 => Seat0,
		1 => Seat1,
		2 => Seat2,
		_ => 0,
	};

	public void SetSeatPeer(int seat, int peerId)
	{
		switch (seat)
		{
			case 0:
				Seat0 = peerId;
				break;
			case 1:
				Seat1 = peerId;
				break;
			case 2:
				Seat2 = peerId;
				break;
		}
	}

	/// <summary>The seat <paramref name="peerId"/> is in, or −1.</summary>
	public readonly int SeatOf(int peerId)
	{
		for (int seat = 0; peerId != 0 && seat < CrewLayout.MaxSeats; seat++)
		{
			if (SeatPeer(seat) == peerId)
			{
				return seat;
			}
		}

		return -1;
	}

	public readonly CrewPose Pose => new(Position, Yaw, Pitch, Roll);

	/// <summary>What the driver's prediction is compared on (§5.7).</summary>
	public readonly CrewSample ToSample(uint tick) => new(tick, Position, Velocity, Yaw, Pitch, Roll);

	/// <summary>A 4x4's state into a record: the pose and rates, the steering, and whether it is in the air.</summary>
	public void SetDrive(in FourByFourState state)
	{
		Position = state.Position;
		Velocity = state.Velocity;
		Yaw = state.Yaw;
		YawRate = state.YawRate;
		Pitch = state.Pitch;
		PitchRate = state.PitchRate;
		Roll = state.Roll;
		RollRate = state.RollRate;
		Steer = state.Steer;
		Rotor = 0f;
		Airborne = state.Airborne;
	}

	/// <summary>A helicopter's state into a record: the pose and rates, the rotor, and whether it is off the ground.</summary>
	public void SetFlight(in HelicopterState state)
	{
		Position = state.Position;
		Velocity = state.Velocity;
		Yaw = state.Yaw;
		YawRate = state.YawRate;
		Pitch = state.Pitch;
		PitchRate = state.PitchRate;
		Roll = state.Roll;
		RollRate = state.RollRate;
		Steer = 0f;
		Rotor = state.Rotor;
		Airborne = !state.Grounded;
	}

	/// <summary>
	/// The 4x4's state this record restores (§8.2): everything <see cref="FourByFourSim.Step"/>
	/// reads. How many ticks it has been in the air is not on the wire and not read by the
	/// step — the step counts them afresh from its wheels — so in the air is one tick.
	/// </summary>
	public readonly FourByFourState ToDrive() => new()
	{
		Position = Position,
		Velocity = Velocity,
		Yaw = FourByFourSim.WrapAngle(Yaw),
		YawRate = YawRate,
		Pitch = Pitch,
		PitchRate = PitchRate,
		Roll = Roll,
		RollRate = RollRate,
		Steer = Steer,
		AirTicks = Airborne ? 1 : 0,
	};

	/// <summary>The helicopter's state this record restores (§8.2): everything <see cref="HelicopterSim.Step"/> reads.</summary>
	public readonly HelicopterState ToFlight() => new()
	{
		Position = Position,
		Velocity = Velocity,
		Yaw = DroneSim.WrapAngle(Yaw),
		YawRate = YawRate,
		Pitch = Pitch,
		PitchRate = PitchRate,
		Roll = Roll,
		RollRate = RollRate,
		Rotor = Rotor,
		Grounded = !Airborne,
	};

	/// <summary>This record as a client decodes it: every number at the wire's resolution.</summary>
	public readonly CrewVehicleRecord Quantized()
	{
		Span<byte> bytes = stackalloc byte[SizeBytes];
		CrewVehicleCodec.Write(this, bytes);
		return CrewVehicleCodec.Read(bytes);
	}
}

/// <summary>
/// Packs <c>ServerCrewVehicles</c> (docs/GROUND_VEHICLES.md §7.1): unreliable, at 30 Hz,
/// on the player snapshot's tick. A 9-byte header — the tick, the count, and the
/// kiosk's line for each of the <see cref="Kinds"/> kinds — then one
/// <see cref="CrewVehicleRecord"/> a vehicle. Two vehicles are 101 bytes; four, 193.
///
/// Decoding is total, like every codec here: a malformed datagram — short, a count past
/// <see cref="SimConfig.MaxCrewVehicles"/>, a slot outside them or named twice — returns
/// false and never throws.
/// </summary>
public static class CrewVehicleCodec
{
	/// <summary>The kinds the header has a kiosk line for: the 4x4 and the helicopter.</summary>
	public const int Kinds = 2;

	public const int HeaderBytes = 5 + (Kinds * 2);

	public const int MaxVehicles = SimConfig.MaxCrewVehicles;

	public const int MaxPayloadBytes = HeaderBytes + (MaxVehicles * CrewVehicleRecord.SizeBytes);

	/// <summary>The steering's step on the wire [rad]: an i8 of these covers ±0.635 rad, past the 4x4's 32° lock.</summary>
	public const float SteerQuantum = 0.005f;

	/// <summary>A rate's step on the wire [rad/s]: i16 milliradians a second, ±32.8 rad/s.</summary>
	public const float RateQuantum = 0.001f;

	public static int PayloadBytes(int vehicles) => HeaderBytes + (vehicles * CrewVehicleRecord.SizeBytes);

	public static byte[] Encode(uint tick, ReadOnlySpan<KioskLine> kiosk, ReadOnlySpan<CrewVehicleRecord> vehicles)
	{
		var payload = new byte[PayloadBytes(Math.Min(vehicles.Length, MaxVehicles))];
		Encode(tick, kiosk, vehicles, payload);
		return payload;
	}

	/// <summary>Returns the number of bytes written, or 0 if <paramref name="into"/> is too small.</summary>
	public static int Encode(uint tick, ReadOnlySpan<KioskLine> kiosk, ReadOnlySpan<CrewVehicleRecord> vehicles,
		Span<byte> into)
	{
		int count = Math.Min(vehicles.Length, MaxVehicles);
		int size = PayloadBytes(count);
		if (into.Length < size)
		{
			return 0;
		}

		BinaryPrimitives.WriteUInt32LittleEndian(into, tick);
		into[4] = (byte)count;
		for (int kind = 0; kind < Kinds; kind++)
		{
			KioskLine line = kind < kiosk.Length ? kiosk[kind] : KioskLine.None;
			into[5 + (kind * 2)] = (byte)line.Status;
			into[6 + (kind * 2)] = line.SecondsLeft;
		}

		for (int i = 0; i < count; i++)
		{
			Write(vehicles[i], into[(HeaderBytes + (i * CrewVehicleRecord.SizeBytes))..]);
		}

		return size;
	}

	/// <summary>
	/// Decodes a payload. False, with nothing in <paramref name="vehicles"/> to trust, for
	/// anything malformed; a kiosk status byte this build does not know reads as
	/// <see cref="KioskStatus.Disabled"/>.
	/// </summary>
	public static bool TryDecode(ReadOnlySpan<byte> payload, Span<KioskLine> kiosk, Span<CrewVehicleRecord> vehicles,
		out uint tick, out int count)
	{
		tick = 0;
		count = 0;
		if (payload.Length < HeaderBytes)
		{
			return false;
		}

		int n = payload[4];
		if (n > MaxVehicles || payload.Length < PayloadBytes(n) || vehicles.Length < n)
		{
			return false;
		}

		int seen = 0;
		for (int i = 0; i < n; i++)
		{
			byte slot = payload[HeaderBytes + (i * CrewVehicleRecord.SizeBytes)];
			if (slot >= MaxVehicles || (seen & (1 << slot)) != 0)
			{
				return false;
			}

			seen |= 1 << slot;
		}

		tick = BinaryPrimitives.ReadUInt32LittleEndian(payload);
		for (int kind = 0; kind < Kinds && kind < kiosk.Length; kind++)
		{
			byte status = payload[5 + (kind * 2)];
			kiosk[kind] = new KioskLine(status <= (byte)KioskStatus.Disabled ? (KioskStatus)status : KioskStatus.Disabled,
				payload[6 + (kind * 2)]);
		}

		for (int i = 0; i < n; i++)
		{
			vehicles[i] = Read(payload[(HeaderBytes + (i * CrewVehicleRecord.SizeBytes))..]);
		}

		count = n;
		return true;
	}

	/// <summary>One record into its 46 bytes.</summary>
	public static void Write(in CrewVehicleRecord r, Span<byte> into)
	{
		into[0] = r.Slot;
		into[1] = (byte)((r.KindId & 0b11)
			| (r.Destroyed ? 1 << 2 : 0)
			| (r.Airborne ? 1 << 3 : 0)
			| (r.DriverSeated ? 1 << 4 : 0)
			| (r.GunReloading ? 1 << 5 : 0));
		WriteVector(into[2..], r.Position);
		WriteVector(into[8..], r.Velocity);
		BinaryPrimitives.WriteUInt16LittleEndian(into[14..], Quantize.YawToU16(Finite(r.Yaw)));
		BinaryPrimitives.WriteInt16LittleEndian(into[16..], RateToI16(r.YawRate));
		BinaryPrimitives.WriteInt16LittleEndian(into[18..], Quantize.PitchToI16(Finite(r.Pitch)));
		BinaryPrimitives.WriteInt16LittleEndian(into[20..], RateToI16(r.PitchRate));
		BinaryPrimitives.WriteInt16LittleEndian(into[22..], Quantize.PitchToI16(Finite(r.Roll)));
		BinaryPrimitives.WriteInt16LittleEndian(into[24..], RateToI16(r.RollRate));
		into[26] = r.Kind == CrewVehicleKind.Helicopter ? RotorToU8(r.Rotor) : (byte)SteerToI8(r.Steer);
		BinaryPrimitives.WriteUInt16LittleEndian(into[27..], r.Health);
		into[29] = r.GunRounds;
		BinaryPrimitives.WriteUInt32LittleEndian(into[30..], r.DriverInputTick);
		BinaryPrimitives.WriteInt32LittleEndian(into[34..], r.Seat0);
		BinaryPrimitives.WriteInt32LittleEndian(into[38..], r.Seat1);
		BinaryPrimitives.WriteInt32LittleEndian(into[42..], r.Seat2);
	}

	/// <summary>One record out of its 46 bytes. Total: every bit pattern is some record.</summary>
	public static CrewVehicleRecord Read(ReadOnlySpan<byte> from)
	{
		byte flags = from[1];
		var r = new CrewVehicleRecord
		{
			Slot = from[0],
			KindId = (byte)(flags & 0b11),
			Destroyed = (flags & (1 << 2)) != 0,
			Airborne = (flags & (1 << 3)) != 0,
			DriverSeated = (flags & (1 << 4)) != 0,
			GunReloading = (flags & (1 << 5)) != 0,
			Position = ReadVector(from[2..]),
			Velocity = ReadVector(from[8..]),
			Yaw = Quantize.U16ToYaw(BinaryPrimitives.ReadUInt16LittleEndian(from[14..])),
			YawRate = I16ToRate(BinaryPrimitives.ReadInt16LittleEndian(from[16..])),
			Pitch = Quantize.I16ToPitch(BinaryPrimitives.ReadInt16LittleEndian(from[18..])),
			PitchRate = I16ToRate(BinaryPrimitives.ReadInt16LittleEndian(from[20..])),
			Roll = Quantize.I16ToPitch(BinaryPrimitives.ReadInt16LittleEndian(from[22..])),
			RollRate = I16ToRate(BinaryPrimitives.ReadInt16LittleEndian(from[24..])),
			Health = BinaryPrimitives.ReadUInt16LittleEndian(from[27..]),
			GunRounds = from[29],
			DriverInputTick = BinaryPrimitives.ReadUInt32LittleEndian(from[30..]),
			Seat0 = BinaryPrimitives.ReadInt32LittleEndian(from[34..]),
			Seat1 = BinaryPrimitives.ReadInt32LittleEndian(from[38..]),
			Seat2 = BinaryPrimitives.ReadInt32LittleEndian(from[42..]),
		};

		if (r.Kind == CrewVehicleKind.Helicopter)
		{
			r.Rotor = from[26] / 255f;
		}
		else
		{
			r.Steer = I8ToSteer((sbyte)from[26]);
		}

		return r;
	}

	/// <summary>A rate to i16 milliradians a second, clamped; a non-finite one is 0.</summary>
	public static short RateToI16(float radiansPerSecond) => float.IsFinite(radiansPerSecond)
		? (short)MathF.Round(Math.Clamp(radiansPerSecond / RateQuantum, -short.MaxValue, short.MaxValue))
		: (short)0;

	public static float I16ToRate(short units) => Math.Max((int)units, -short.MaxValue) * RateQuantum;

	/// <summary>The steering to an i8 of <see cref="SteerQuantum"/>, clamped; a non-finite one is 0.</summary>
	public static sbyte SteerToI8(float radians) => float.IsFinite(radians)
		? (sbyte)MathF.Round(Math.Clamp(radians / SteerQuantum, -sbyte.MaxValue, sbyte.MaxValue))
		: (sbyte)0;

	public static float I8ToSteer(sbyte units) => Math.Max((int)units, -sbyte.MaxValue) * SteerQuantum;

	/// <summary>The rotor, 0 to 1, to a byte; a non-finite one is 0.</summary>
	public static byte RotorToU8(float rotor) => float.IsFinite(rotor)
		? (byte)MathF.Round(Math.Clamp(rotor, 0f, 1f) * 255f)
		: (byte)0;

	/// <summary>Health to the wire: whole points, rounded up, so a vehicle with a scrap left never reads as none.</summary>
	public static ushort HealthToU16(float health) => float.IsFinite(health)
		? (ushort)Math.Clamp(MathF.Ceiling(health), 0f, ushort.MaxValue)
		: (ushort)0;

	private static void WriteVector(Span<byte> into, Vector3 v)
	{
		BinaryPrimitives.WriteInt16LittleEndian(into, Quantize.MetersToI16(Finite(v.X)));
		BinaryPrimitives.WriteInt16LittleEndian(into[2..], Quantize.MetersToI16(Finite(v.Y)));
		BinaryPrimitives.WriteInt16LittleEndian(into[4..], Quantize.MetersToI16(Finite(v.Z)));
	}

	private static Vector3 ReadVector(ReadOnlySpan<byte> from) => new(
		Quantize.I16ToMeters(BinaryPrimitives.ReadInt16LittleEndian(from)),
		Quantize.I16ToMeters(BinaryPrimitives.ReadInt16LittleEndian(from[2..])),
		Quantize.I16ToMeters(BinaryPrimitives.ReadInt16LittleEndian(from[4..])));

	private static float Finite(float value) => float.IsFinite(value) ? value : 0f;
}
