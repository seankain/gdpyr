using System;
using Godot;

namespace Gdpyr.Sim.Agent;

/// <summary>
/// A hash of the tick's simulation state, for measuring divergence rather than
/// for promising there is none (docs/AGENT_API.md §3, §10).
///
/// <c>Scripts/Sim</c> is reproducible; <c>MoveAndSlide()</c> and
/// <c>NavigationAgent3D</c> are not. The divergence probe runs the same seeded
/// episode twice and reports the tick at which these hashes part company — and
/// that number, not an assumption, decides how tightly a regression assertion may
/// be written.
///
/// Positions are quantized to a millimetre before they are folded in. Below that
/// the difference is not observable in the game and would make the probe report
/// noise instead of divergence; above it, two runs really have taken different
/// paths.
/// </summary>
public struct AgentStateHash
{
	private const ulong OffsetBasis = 14695981039346656037;
	private const ulong Prime = 1099511628211;

	/// <summary>Millimetres. See the class remarks for why this is not finer.</summary>
	public const float PositionQuantum = 0.001f;

	/// <summary>Millimetres per second.</summary>
	public const float VelocityQuantum = 0.001f;

	private ulong _value;

	public static AgentStateHash Start() => new() { _value = OffsetBasis };

	public readonly ulong Value => _value;

	public void Add(ulong value)
	{
		for (int i = 0; i < 8; i++)
		{
			_value ^= (byte)(value >> (i * 8));
			_value *= Prime;
		}
	}

	public void Add(int value) => Add((ulong)(uint)value);

	public void Add(uint value) => Add((ulong)value);

	public void Add(bool value) => Add(value ? 1u : 0u);

	/// <summary>Folds a quantized scalar in. <paramref name="quantum"/> is the smallest difference that counts.</summary>
	public void Add(float value, float quantum)
	{
		float scaled = value / MathF.Max(quantum, float.Epsilon);
		Add((ulong)(long)MathF.Round(Math.Clamp(scaled, -1e15f, 1e15f)));
	}

	public void AddPosition(Vector3 value)
	{
		Add(value.X, PositionQuantum);
		Add(value.Y, PositionQuantum);
		Add(value.Z, PositionQuantum);
	}

	public void AddVelocity(Vector3 value)
	{
		Add(value.X, VelocityQuantum);
		Add(value.Y, VelocityQuantum);
		Add(value.Z, VelocityQuantum);
	}

	/// <summary>Radians to a ten-thousandth, as a character's look is folded.</summary>
	public const float AngleQuantum = 0.0001f;

	/// <summary>
	/// Folds one crew vehicle in (docs/GROUND_VEHICLES.md §11): its slot and kind, whether it
	/// is a wreck or in the air, where it is and how it moves, its heading and attitude —
	/// the 4x4's sprung chassis, the helicopter's tilt — its steering or rotor, its health,
	/// its gun's box, and who sits in each seat. Everything its record carries (§7.1) but the
	/// driver's acknowledgement, which is the wire's and not the simulation's, taken before
	/// the record's own rounding.
	/// </summary>
	public void Add(in CrewVehicleRecord vehicle)
	{
		Add((uint)vehicle.Slot);
		Add((uint)vehicle.KindId);
		Add(vehicle.Destroyed);
		Add(vehicle.Airborne);
		AddPosition(vehicle.Position);
		AddVelocity(vehicle.Velocity);
		Add(vehicle.Yaw, AngleQuantum);
		Add(vehicle.YawRate, AngleQuantum);
		Add(vehicle.Pitch, AngleQuantum);
		Add(vehicle.PitchRate, AngleQuantum);
		Add(vehicle.Roll, AngleQuantum);
		Add(vehicle.RollRate, AngleQuantum);
		Add(vehicle.Steer, AngleQuantum);
		Add(vehicle.Rotor, AngleQuantum);
		Add((uint)vehicle.Health);
		Add((uint)vehicle.GunRounds);
		Add(vehicle.Seat0);
		Add(vehicle.Seat1);
		Add(vehicle.Seat2);
	}
}
