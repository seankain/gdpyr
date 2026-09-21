using System;
using Gdpyr.Sim;
using Gdpyr.Sim.Agent;
using Xunit;

using ClientAction = Gdpyr.AgentClient.AgentGroundAction;
using ClientButtons = Gdpyr.AgentClient.AgentButtons;
using ClientFrameKind = Gdpyr.AgentClient.AgentFrameKind;
using ClientProtocol = Gdpyr.AgentClient.AgentProtocol;

namespace Gdpyr.Tests;

/// <summary>
/// The external client against the server, byte for byte.
///
/// <c>tools/Gdpyr.AgentClient</c> implements the framing and the packed action a
/// second time, on purpose: it is what an outside process links, and a client
/// that compiled against the game's own source would hide exactly the skew the
/// published schema exists to catch (docs/AGENT_API.md §4). That only helps if
/// somebody checks the two agree, which is what this file is.
/// </summary>
public class AgentInteropTests
{
	[Fact]
	public void TheTwoImplementationsAgreeOnTheFramingConstants()
	{
		Assert.Equal(AgentProtocol.Version, ClientProtocol.Version);
		Assert.Equal(AgentProtocol.SchemaVersion, ClientProtocol.SchemaVersion);
		Assert.Equal(AgentProtocol.LengthBytes, ClientProtocol.LengthBytes);
		Assert.Equal(AgentProtocol.HeaderBytes, ClientProtocol.HeaderBytes);
		Assert.Equal(AgentProtocol.MaxFrameBytes, ClientProtocol.MaxFrameBytes);
		Assert.Equal(AgentActionCodec.GroundSizeBytes, ClientAction.SizeBytes);
	}

	[Fact]
	public void EveryFrameKindHasTheSameNumberOnBothSides()
	{
		Assert.Equal((byte)AgentFrameKind.Request, (byte)ClientFrameKind.Request);
		Assert.Equal((byte)AgentFrameKind.Response, (byte)ClientFrameKind.Response);
		Assert.Equal((byte)AgentFrameKind.Observation, (byte)ClientFrameKind.Observation);
		Assert.Equal((byte)AgentFrameKind.Event, (byte)ClientFrameKind.Event);
		Assert.Equal((byte)AgentFrameKind.Error, (byte)ClientFrameKind.Error);
	}

	[Fact]
	public void EveryButtonHasTheSameBitOnBothSides()
	{
		Assert.Equal((ushort)InputButtons.Jump, (ushort)ClientButtons.Jump);
		Assert.Equal((ushort)InputButtons.Crouch, (ushort)ClientButtons.Crouch);
		Assert.Equal((ushort)InputButtons.Sprint, (ushort)ClientButtons.Sprint);
		Assert.Equal((ushort)InputButtons.Fire, (ushort)ClientButtons.Fire);
		Assert.Equal((ushort)InputButtons.Ads, (ushort)ClientButtons.Ads);
		Assert.Equal((ushort)InputButtons.Reload, (ushort)ClientButtons.Reload);
		Assert.Equal((ushort)InputButtons.Use, (ushort)ClientButtons.Use);
		Assert.Equal((ushort)InputButtons.Melee, (ushort)ClientButtons.Melee);
		Assert.Equal((ushort)InputButtons.Weapon1, (ushort)ClientButtons.Weapon1);
		Assert.Equal((ushort)InputButtons.Weapon2, (ushort)ClientButtons.Weapon2);
		Assert.Equal((ushort)InputButtons.Weapon3, (ushort)ClientButtons.Weapon3);
	}

	[Fact]
	public void AFrameTheClientWroteIsAFrameTheServerReads()
	{
		byte[] body = { 7, 7, 7 };
		byte[] frame = ClientProtocol.Encode(ClientFrameKind.Request, 1234, body);

		Assert.True(AgentProtocol.TryDecode(frame, out AgentFrameKind kind, out uint correlation,
			out int offset, out int length, out int consumed, out bool fatal));

		Assert.False(fatal);
		Assert.Equal(AgentFrameKind.Request, kind);
		Assert.Equal(1234u, correlation);
		Assert.Equal(frame.Length, consumed);
		Assert.Equal(body, frame.AsSpan(offset, length).ToArray());
	}

	[Fact]
	public void AFrameTheServerWroteIsAFrameTheClientReads()
	{
		byte[] frame = AgentProtocol.Encode(AgentFrameKind.Observation, 99, new byte[] { 1, 2 });

		Assert.True(ClientProtocol.TryDecode(frame, out ClientFrameKind kind, out uint correlation, out _,
			out int length, out _, out _));

		Assert.Equal(ClientFrameKind.Observation, kind);
		Assert.Equal(99u, correlation);
		Assert.Equal(2, length);
	}

	[Fact]
	public void AnActionTheClientPackedIsTheActionTheServerSimulates()
	{
		var action = new ClientAction
		{
			MoveX = -1f,
			MoveZ = -1f,
			DeltaYaw = -0.25f,
			DeltaPitch = 0.05f,
			Buttons = ClientButtons.Fire | ClientButtons.Sprint,
		};

		int expected = BotRoster.PeerIdFor(0);
		byte[] packed = action.Pack(seat: expected, tick: 51204);

		Assert.True(AgentActionCodec.TryDecodeGround(packed, out int seat, out uint tick,
			out AgentGroundAction decoded));

		Assert.Equal(expected, seat);
		Assert.Equal(51204u, tick);
		Assert.True(decoded.LookIsDelta);
		Assert.InRange(decoded.MoveX, -1.01f, -0.99f);
		Assert.InRange(decoded.MoveZ, -1.01f, -0.99f);
		Assert.InRange(decoded.Yaw, -0.26f, -0.24f);
		Assert.InRange(decoded.Pitch, 0.04f, 0.06f);
		Assert.True(decoded.Held(InputButtons.Fire));
		Assert.True(decoded.Held(InputButtons.Sprint));
		Assert.False(decoded.Held(InputButtons.Jump));
	}

	[Theory]
	[InlineData(0f)]
	[InlineData(0.5f)]
	[InlineData(-0.5f)]
	[InlineData(3f)]
	[InlineData(-3f)]
	public void ADeltaYawSurvivesTheWrapInBothDirections(float delta)
	{
		var action = new ClientAction { DeltaYaw = delta };
		byte[] packed = action.Pack(1, 1);

		Assert.True(AgentActionCodec.TryDecodeGround(packed, out _, out _, out AgentGroundAction decoded));
		Assert.InRange(decoded.Yaw, delta - 0.001f, delta + 0.001f);
	}

	[Fact]
	public void AClientActionRunsThroughTheSameCeilingABotPlaysUnder()
	{
		// A policy that asks for a full turn in one decision gets one tick of the
		// bot's turn rate, whichever implementation packed the request.
		var action = new ClientAction { DeltaYaw = 3f };
		byte[] packed = action.Pack(1, 1);
		AgentActionCodec.TryDecodeGround(packed, out _, out _, out AgentGroundAction decoded);

		InputFrame frame = AgentActionCodec.Resolve(decoded, 1, 0f, 0f, AgentLimits.Default,
			SimConfig.TickDelta);

		Assert.InRange(frame.YawRadians, 0f, (BotTraits.Default.TurnRateRadians * SimConfig.TickDelta) + 0.01f);
	}
}
