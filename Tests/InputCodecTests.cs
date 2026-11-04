using System;
using Gdpyr.Sim;
using Xunit;

namespace Gdpyr.Tests;

public class InputCodecTests
{
	private static InputFrame Sample(uint tick) =>
		InputFrame.Create(tick, -1f, 0.5f, 2.5f, -0.75f, InputButtons.Jump | InputButtons.Sprint);

	[Fact]
	public void Frame_IsTwelveBytesOnTheWire()
	{
		// The budget in docs/NETCODE.md §7 assumes ~48 B for three frames plus header: the
		// count and, from docs/GROUND_VEHICLES.md's GV4, how far behind the client drew.
		Assert.Equal(12, InputFrame.SizeBytes);
		Assert.Equal(2, InputCodec.HeaderBytes);
		Assert.Equal(38, InputCodec.PayloadBytes(SimConfig.InputRedundancy));
	}

	[Fact]
	public void HowFarBehindTheNewestFrameTheClientDrew_RidesTheHeader_InQuarterTicks()
	{
		var frames = new[] { Sample(100), Sample(101), Sample(102) };
		byte behind = InputCodec.DrawnBehind(15.3f);
		Assert.Equal(61, behind);
		byte[] payload = InputCodec.Encode(frames, behind);

		var into = new InputFrame[InputCodec.MaxFrames];
		Assert.True(InputCodec.TryDecode(payload, into, out int count, out byte read));
		Assert.Equal(3, count);
		Assert.Equal(behind, read);
		Assert.Equal(15.25f, InputCodec.DrawnBehindTicks(read));

		// The frames are where they were: one header byte later, unchanged.
		Assert.Equal(102u, into[2].Tick);
		Assert.Equal(frames[2].Buttons, into[2].Buttons);
	}

	[Fact]
	public void ADrawnOffsetNoByteHolds_OrNoneAtAll_IsNotKnown()
	{
		Assert.Equal(0, InputCodec.DrawnBehind(0f));
		Assert.Equal(0, InputCodec.DrawnBehind(-3f));
		Assert.Equal(0, InputCodec.DrawnBehind(float.NaN));
		Assert.Equal(0, InputCodec.DrawnBehind(64f));
		Assert.Equal(255, InputCodec.DrawnBehind(63.75f));
		Assert.Equal(1, InputCodec.DrawnBehind(0.2f));
		Assert.True(float.IsNaN(InputCodec.DrawnBehindTicks(0)));

		// A packet that says nothing reads as not known.
		byte[] payload = InputCodec.Encode(new[] { Sample(5) });
		Assert.True(InputCodec.TryDecode(payload, new InputFrame[8], out _, out byte read));
		Assert.Equal(0, read);
	}

	[Fact]
	public void RoundTrip_PreservesEveryField()
	{
		var frames = new[] { Sample(100), Sample(101), Sample(102) };
		byte[] payload = InputCodec.Encode(frames);

		var into = new InputFrame[InputCodec.MaxFrames];
		Assert.True(InputCodec.TryDecode(payload, into, out int count));
		Assert.Equal(3, count);

		for (int i = 0; i < count; i++)
		{
			Assert.Equal(frames[i].Tick, into[i].Tick);
			Assert.Equal(frames[i].MoveX, into[i].MoveX);
			Assert.Equal(frames[i].MoveZ, into[i].MoveZ);
			Assert.Equal(frames[i].Yaw, into[i].Yaw);
			Assert.Equal(frames[i].Pitch, into[i].Pitch);
			Assert.Equal(frames[i].Buttons, into[i].Buttons);
		}
	}

	[Fact]
	public void Encode_TruncatesPastTheFrameCap()
	{
		var frames = new InputFrame[InputCodec.MaxFrames + 4];
		byte[] payload = InputCodec.Encode(frames);
		Assert.Equal(InputCodec.MaxPayloadBytes, payload.Length);
	}

	[Fact]
	public void Decode_RejectsAnOversizedFrameCount()
	{
		// The count byte is attacker-controlled: it must not be able to make the
		// server allocate or loop past the buffer.
		var payload = new byte[InputCodec.MaxPayloadBytes];
		payload[0] = 255;
		Assert.False(InputCodec.TryDecode(payload, new InputFrame[InputCodec.MaxFrames], out _));
	}

	[Fact]
	public void Decode_RejectsATruncatedPayload()
	{
		byte[] payload = InputCodec.Encode(new[] { Sample(1), Sample(2) });
		Assert.False(InputCodec.TryDecode(payload.AsSpan(0, payload.Length - 3), new InputFrame[8], out _));
	}

	[Fact]
	public void Decode_RejectsAnEmptyPayload()
	{
		Assert.False(InputCodec.TryDecode(System.Array.Empty<byte>(), new InputFrame[8], out _));
		Assert.False(InputCodec.TryDecode(new byte[] { 0 }, new InputFrame[8], out _));
	}

	[Fact]
	public void Decode_ClampsTheOutOfRangeMoveAxis()
	{
		byte[] payload = InputCodec.Encode(new[] { Sample(7) });
		payload[InputCodec.HeaderBytes + 4] = 0x80; // MoveX = -128
		payload[InputCodec.HeaderBytes + 5] = 0x80; // MoveZ = -128

		var into = new InputFrame[1];
		Assert.True(InputCodec.TryDecode(payload, into, out int count));
		Assert.Equal(1, count);
		Assert.Equal(-127, into[0].MoveX);
		Assert.Equal(-127, into[0].MoveZ);
		Assert.InRange(into[0].MoveAxes.Length(), 0f, 1.5f);
	}

	[Fact]
	public void LookOnly_KeepsTheTickAndTheAnglesAndDropsEverythingElse()
	{
		// What a dead player's frame becomes, on the server and on their own client,
		// applied to the same recorded frame so the two cannot disagree.
		InputFrame frame = InputFrame.Create(1234, 1f, -1f, 1.25f, -0.5f,
			InputButtons.Fire | InputButtons.Jump | InputButtons.Sprint);

		InputFrame stripped = InputFrame.LookOnly(frame);

		Assert.Equal(frame.Tick, stripped.Tick);
		Assert.Equal(frame.Yaw, stripped.Yaw);
		Assert.Equal(frame.Pitch, stripped.Pitch);
		Assert.Equal(0, stripped.MoveX);
		Assert.Equal(0, stripped.MoveZ);
		Assert.Equal(0, stripped.Buttons);
		Assert.False(stripped.Held(InputButtons.Fire));
	}

	[Fact]
	public void LookOnly_IsIdempotent()
	{
		InputFrame once = InputFrame.LookOnly(InputFrame.Create(7, 1f, 1f, 2f, 0.5f, InputButtons.Fire));
		InputFrame twice = InputFrame.LookOnly(once);

		Assert.Equal(once.Tick, twice.Tick);
		Assert.Equal(once.Yaw, twice.Yaw);
		Assert.Equal(once.Buttons, twice.Buttons);
	}
}
