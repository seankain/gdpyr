using System;

namespace Gdpyr.Sim;

/// <summary>
/// The wire representation of every continuous quantity the netcode sends
/// (docs/NETCODE.md §7). Kept in one place because both ends must round
/// identically: a client predicts against the same quantized angle the server
/// simulates, so the quantization has to happen at the *sampler*, not only in
/// the codec.
/// </summary>
public static class Quantize
{
	public const float TwoPi = MathF.PI * 2f;
	public const float HalfPi = MathF.PI * 0.5f;

	/// <summary>-128 is never produced, so the decoded range is symmetric.</summary>
	private const float AxisScale = 127f;

	private const float YawScale = 65536f / TwoPi;
	private const float PitchScale = short.MaxValue / HalfPi;
	private const float PositionScale = 1f / SimConfig.PositionQuantum;

	/// <summary>A movement axis in [-1,1] to one signed byte.</summary>
	public static sbyte AxisToI8(float axis) =>
		(sbyte)MathF.Round(Math.Clamp(axis, -1f, 1f) * AxisScale);

	/// <summary>
	/// Back to [-1,1]. Clamps -128, which no encoder produces but a hostile client
	/// can still put on the wire.
	/// </summary>
	public static float I8ToAxis(sbyte axis) => Math.Max((int)axis, -127) / AxisScale;

	/// <summary>
	/// Yaw to 16 bits over a full turn (~0.0055° per step). Wraps rather than
	/// clamps: yaw is unbounded and a player spinning past 2π must not stick.
	/// </summary>
	public static ushort YawToU16(float radians)
	{
		float wrapped = radians - TwoPi * MathF.Floor(radians / TwoPi);
		return (ushort)((int)MathF.Round(wrapped * YawScale) & 0xFFFF);
	}

	/// <summary>Decoded yaw is always in [0, 2π).</summary>
	public static float U16ToYaw(ushort units) => units / YawScale;

	/// <summary>Pitch to 16 bits over ±90°, clamped: looking past vertical is not a thing.</summary>
	public static short PitchToI16(float radians) =>
		(short)MathF.Round(Math.Clamp(radians, -HalfPi, HalfPi) * PitchScale);

	public static float I16ToPitch(short units) => units / PitchScale;

	/// <summary>
	/// Metres to int16 centimetres. Also used for velocity components, where the
	/// same range reads as ±327 m/s — far past terminal velocity for a character.
	/// </summary>
	public static short MetersToI16(float meters) =>
		(short)MathF.Round(Math.Clamp(meters, -SimConfig.MaxPositionMeters, SimConfig.MaxPositionMeters) * PositionScale);

	public static float I16ToMeters(short units) => units / PositionScale;
}
