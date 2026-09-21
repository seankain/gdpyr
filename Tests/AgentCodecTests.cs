using System;
using Gdpyr.Sim;
using Gdpyr.Sim.Agent;
using Xunit;

namespace Gdpyr.Tests;

/// <summary>
/// The agent socket's framing and its ground action (docs/AGENT_API.md §4, §7.2).
///
/// Decoding is a trust boundary even on loopback — the channel can spawn players
/// and reset rounds — so the interesting cases here are the hostile ones.
/// </summary>
public class AgentCodecTests
{
	// ---- framing -----------------------------------------------------------

	[Fact]
	public void FrameRoundTripsKindAndCorrelation()
	{
		byte[] body = { 1, 2, 3, 4, 5 };
		byte[] frame = AgentProtocol.Encode(AgentFrameKind.Response, 0xDEADBEEF, body);

		Assert.Equal(AgentProtocol.FrameBytes(body.Length), frame.Length);
		Assert.True(AgentProtocol.TryDecode(frame, out AgentFrameKind kind, out uint correlation,
			out int offset, out int length, out int consumed, out bool fatal));

		Assert.False(fatal);
		Assert.Equal(AgentFrameKind.Response, kind);
		Assert.Equal(0xDEADBEEFu, correlation);
		Assert.Equal(body.Length, length);
		Assert.Equal(frame.Length, consumed);
		Assert.Equal(body, frame.AsSpan(offset, length).ToArray());
	}

	[Fact]
	public void TwoFramesInOneBufferDecodeInOrder()
	{
		byte[] first = AgentProtocol.Encode(AgentFrameKind.Request, 1, new byte[] { 9 });
		byte[] second = AgentProtocol.Encode(AgentFrameKind.Event, 2, new byte[] { 8, 7 });
		var buffer = new byte[first.Length + second.Length];
		first.CopyTo(buffer, 0);
		second.CopyTo(buffer, first.Length);

		Assert.True(AgentProtocol.TryDecode(buffer, out _, out uint a, out _, out _, out int consumed, out _));
		Assert.Equal(1u, a);
		Assert.True(AgentProtocol.TryDecode(buffer.AsSpan(consumed), out _, out uint b, out _, out int length,
			out _, out _));
		Assert.Equal(2u, b);
		Assert.Equal(2, length);
	}

	[Fact]
	public void APartialFrameIsNotAnError_ItIsJustNotHereYet()
	{
		byte[] frame = AgentProtocol.Encode(AgentFrameKind.Request, 1, new byte[] { 1, 2, 3 });

		for (int prefix = 0; prefix < frame.Length; prefix++)
		{
			Assert.False(AgentProtocol.TryDecode(frame.AsSpan(0, prefix), out _, out _, out _, out _, out _,
				out bool fatal));
			Assert.False(fatal);
		}
	}

	[Fact]
	public void AnAbsurdLengthPrefixIsFatalRatherThanAnAllocation()
	{
		var buffer = new byte[16];
		System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(buffer, uint.MaxValue);

		Assert.False(AgentProtocol.TryDecode(buffer, out _, out _, out _, out _, out _, out bool fatal));
		Assert.True(fatal);
	}

	[Fact]
	public void AnUnknownFrameKindIsFatal()
	{
		byte[] frame = AgentProtocol.Encode(AgentFrameKind.Request, 0, new byte[] { 1 });
		frame[AgentProtocol.LengthBytes] = 99;

		Assert.False(AgentProtocol.TryDecode(frame, out _, out _, out _, out _, out _, out bool fatal));
		Assert.True(fatal);
	}

	// ---- the ground action -------------------------------------------------

	[Fact]
	public void EveryButtonBitSurvivesTheRoundTrip()
	{
		var packed = new byte[AgentActionCodec.GroundSizeBytes];
		foreach (InputButtons button in Enum.GetValues<InputButtons>())
		{
			var action = new AgentGroundAction { Buttons = (ushort)button };

			Assert.Equal(AgentActionCodec.GroundSizeBytes,
				AgentActionCodec.EncodeGround(7, action, 100, packed));
			Assert.True(AgentActionCodec.TryDecodeGround(packed, out int seat, out uint tick,
				out AgentGroundAction back));

			Assert.Equal(7, seat);
			Assert.Equal(100u, tick);
			Assert.Equal((ushort)button, back.Buttons);
		}
	}

	[Fact]
	public void EveryNamedButtonMapsToABit()
	{
		foreach (string name in AgentActionCodec.ButtonNames)
		{
			Assert.NotEqual(InputButtons.None, AgentActionCodec.ButtonFromName(name));
		}

		// An unknown name degrades rather than disconnecting: a policy built against a
		// newer schema must keep playing.
		Assert.Equal(InputButtons.None, AgentActionCodec.ButtonFromName("teleport"));
	}

	[Theory]
	[InlineData(0f, 0f)]
	[InlineData(1f, -1f)]
	[InlineData(-1f, 1f)]
	[InlineData(0.5f, -0.25f)]
	public void MovementAxesSurviveTheSbyteQuantization(float x, float z)
	{
		var action = new AgentGroundAction { MoveX = x, MoveZ = z };
		Span<byte> packed = stackalloc byte[AgentActionCodec.GroundSizeBytes];
		AgentActionCodec.EncodeGround(0, action, 1, packed);
		Assert.True(AgentActionCodec.TryDecodeGround(packed, out _, out _, out AgentGroundAction back));

		Assert.InRange(back.MoveX, x - 0.01f, x + 0.01f);
		Assert.InRange(back.MoveZ, z - 0.01f, z + 0.01f);
	}

	[Fact]
	public void AnOutOfRangeAxisIsClampedRatherThanWrapped()
	{
		var action = new AgentGroundAction { MoveX = 7f, MoveZ = -7f };
		Span<byte> packed = stackalloc byte[AgentActionCodec.GroundSizeBytes];
		AgentActionCodec.EncodeGround(0, action, 1, packed);
		Assert.True(AgentActionCodec.TryDecodeGround(packed, out _, out _, out AgentGroundAction back));

		Assert.InRange(back.MoveX, 0.99f, 1.01f);
		Assert.InRange(back.MoveZ, -1.01f, -0.99f);
	}

	[Fact]
	public void ANegativeLookDeltaComesBackNegative()
	{
		// A delta is quantized the same way an absolute yaw is, so it arrives in
		// [0, 2π); the shortest way round is the one that was meant.
		var action = new AgentGroundAction { LookIsDelta = true, Yaw = -0.031f, Pitch = 0.004f };
		Span<byte> packed = stackalloc byte[AgentActionCodec.GroundSizeBytes];
		AgentActionCodec.EncodeGround(3, action, 51204, packed);

		Assert.True(AgentActionCodec.TryDecodeGround(packed, out _, out _, out AgentGroundAction back));
		Assert.True(back.LookIsDelta);
		Assert.InRange(back.Yaw, -0.032f, -0.030f);
		Assert.InRange(back.Pitch, 0.003f, 0.005f);
	}

	[Fact]
	public void ASeatIsNamedByItsPeerId_WhichDoesNotFitInSixteenBits()
	{
		// BotRoster draws bot ids from the top of the positive int range so they
		// cannot collide with a Godot client's. A seat field narrower than an int
		// would address somebody else's character, or nobody's.
		int seat = BotRoster.PeerIdFor(0);
		Assert.True(seat > ushort.MaxValue);

		var packed = new byte[AgentActionCodec.GroundSizeBytes];
		AgentActionCodec.EncodeGround(seat, AgentGroundAction.Neutral, 1, packed);

		Assert.True(AgentActionCodec.TryDecodeGround(packed, out int back, out _, out _));
		Assert.Equal(seat, back);
	}

	[Fact]
	public void ATruncatedActionDoesNotDecode()
	{
		Span<byte> packed = stackalloc byte[AgentActionCodec.GroundSizeBytes - 1];
		Assert.False(AgentActionCodec.TryDecodeGround(packed, out _, out _, out _));
	}

	[Fact]
	public void DeltaLookIsAppliedToWhereTheCharacterIsLooking()
	{
		var action = new AgentGroundAction { LookIsDelta = true, Yaw = 0.2f, Pitch = -0.1f };
		AgentActionCodec.Target(action, 1f, 0.3f, out float yaw, out float pitch);

		Assert.Equal(1.2f, yaw, 4);
		Assert.Equal(0.2f, pitch, 4);
	}

	[Fact]
	public void AbsoluteLookIsTakenAsGiven()
	{
		var action = new AgentGroundAction { Yaw = 2f, Pitch = 0.5f };
		AgentActionCodec.Target(action, 1f, 0.3f, out float yaw, out float pitch);

		Assert.Equal(2f, yaw, 4);
		Assert.Equal(0.5f, pitch, 4);
	}

	[Fact]
	public void ResolveProducesAnOrdinaryInputFrame()
	{
		var action = new AgentGroundAction
		{
			MoveX = 0f,
			MoveZ = -1f,
			Yaw = 1f,
			Pitch = 0f,
			Buttons = (ushort)(InputButtons.Sprint | InputButtons.Fire),
		};

		InputFrame frame = AgentActionCodec.Resolve(action, 42, 1f, 0f, AgentLimits.Default,
			SimConfig.TickDelta);

		Assert.Equal(42u, frame.Tick);
		Assert.True(frame.Held(InputButtons.Sprint));
		Assert.True(frame.Held(InputButtons.Fire));
		Assert.False(frame.Held(InputButtons.Jump));
		Assert.InRange(frame.MoveZAxis, -1.01f, -0.99f);
	}
}
