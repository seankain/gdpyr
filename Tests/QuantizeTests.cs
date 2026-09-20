using Gdpyr.Sim;
using Xunit;

namespace Gdpyr.Tests;

public class QuantizeTests
{
	[Theory]
	[InlineData(0f)]
	[InlineData(1f)]
	[InlineData(-1f)]
	[InlineData(0.7071f)]
	[InlineData(-0.5f)]
	public void Axis_RoundTripsWithinOneStep(float axis)
	{
		float decoded = Quantize.I8ToAxis(Quantize.AxisToI8(axis));
		Assert.InRange(decoded - axis, -1f / 127f, 1f / 127f);
	}

	[Theory]
	[InlineData(5f)]
	[InlineData(-5f)]
	public void Axis_ClampsOutOfRangeInput(float axis)
	{
		// A client can put anything on the wire; the decoded axis must stay in [-1,1]
		// or the movement step would scale straight past the state's move speed.
		float decoded = Quantize.I8ToAxis(Quantize.AxisToI8(axis));
		Assert.InRange(decoded, -1f, 1f);
	}

	[Fact]
	public void Axis_MinusOneTwentyEight_IsClampedOnDecode()
	{
		// -128 is unreachable through AxisToI8 but reachable through a raw byte.
		Assert.Equal(-1f, Quantize.I8ToAxis(-128), precision: 5);
	}

	[Theory]
	[InlineData(0f)]
	[InlineData(1f)]
	[InlineData(3.14159f)]
	[InlineData(6.2f)]
	public void Yaw_RoundTripsWithinOneStep(float yaw)
	{
		float decoded = Quantize.U16ToYaw(Quantize.YawToU16(yaw));
		Assert.InRange(decoded - yaw, -1e-4f, 1e-4f);
	}

	[Fact]
	public void Yaw_WrapsRatherThanClamps()
	{
		// A player who keeps turning right must not stick at 2π.
		Assert.Equal(Quantize.YawToU16(0.25f), Quantize.YawToU16(0.25f + Quantize.TwoPi));
		Assert.Equal(Quantize.YawToU16(0.25f), Quantize.YawToU16(0.25f - Quantize.TwoPi));
	}

	[Fact]
	public void Yaw_DecodesIntoASingleTurn()
	{
		float decoded = Quantize.U16ToYaw(Quantize.YawToU16(-1f));
		Assert.InRange(decoded, 0f, Quantize.TwoPi);
		Assert.InRange(decoded - (Quantize.TwoPi - 1f), -1e-4f, 1e-4f);
	}

	[Theory]
	[InlineData(0f)]
	[InlineData(1.5f)]
	[InlineData(-1.5f)]
	public void Pitch_RoundTripsWithinOneStep(float pitch)
	{
		float decoded = Quantize.I16ToPitch(Quantize.PitchToI16(pitch));
		Assert.InRange(decoded - pitch, -1e-4f, 1e-4f);
	}

	[Fact]
	public void Pitch_ClampsPastVertical()
	{
		Assert.Equal(Quantize.HalfPi, Quantize.I16ToPitch(Quantize.PitchToI16(3f)), precision: 4);
		Assert.Equal(-Quantize.HalfPi, Quantize.I16ToPitch(Quantize.PitchToI16(-3f)), precision: 4);
	}

	[Theory]
	[InlineData(0f)]
	[InlineData(12.34f)]
	[InlineData(-250.5f)]
	public void Meters_RoundTripWithinHalfACentimetre(float meters)
	{
		float decoded = Quantize.I16ToMeters(Quantize.MetersToI16(meters));
		Assert.InRange(decoded - meters, -0.005f, 0.005f);
	}

	[Fact]
	public void Meters_ClampToTheRepresentableRange()
	{
		// Past ±327 m the wire format silently wraps unless it is clamped, which
		// would teleport anything that fell off the map.
		Assert.Equal(SimConfig.MaxPositionMeters, Quantize.I16ToMeters(Quantize.MetersToI16(1000f)), precision: 2);
		Assert.Equal(-SimConfig.MaxPositionMeters, Quantize.I16ToMeters(Quantize.MetersToI16(-1000f)), precision: 2);
	}
}
