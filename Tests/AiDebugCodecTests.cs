using System;
using System.Linq;
using FluidHTN;
using Gdpyr.Sim;
using Gdpyr.Sim.AiDebug;
using Gdpyr.Sim.Htn;
using Godot;
using Xunit;

namespace Gdpyr.Tests;

/// <summary>
/// The spectator debugger's wire format (docs/AI_DEBUG.md §5): a frame and a watch
/// request survive the trip whole, and anything that is not exactly one well-formed
/// message is refused without throwing — the server decodes watch requests from
/// whoever has said they are spectating.
/// </summary>
public class AiDebugCodecTests
{
	[Fact]
	public void AFullFrame_RoundTrips()
	{
		AiDebugFrame frame = SampleFrame();

		byte[] bytes = AiDebugCodec.EncodeFrame(frame);
		Assert.True(AiDebugCodec.TryDecodeFrame(bytes, out AiDebugFrame back));

		Assert.Equal(frame.Tick, back.Tick);
		Assert.Equal(BotAi.Htn, back.BotAi);
		Assert.Equal(frame.GroundSignature, back.GroundSignature);
		Assert.Equal(frame.UnitSignature, back.UnitSignature);
		Assert.Equal(frame.SquadSignature, back.SquadSignature);

		Assert.Equal(2, back.Labels.Count);
		AiLabel bot = back.Labels[0];
		Assert.Equal(AiEntityRef.Player(BotRoster.PeerIdFor(2)), bot.Ref);
		Assert.Equal((byte)GroundGoal.HoldNode, bot.Goal);
		Assert.Equal((byte)GroundRole.Denier, bot.Detail);
		Assert.Equal(new Vector3(-30.12f, 0.5f, 14f), bot.Point);
		Assert.Equal(OwnerId.ForUnit(7), bot.TargetOwnerId);
		Assert.Equal(Vector3.Zero, back.Labels[1].Point);

		AiCommander commander = Assert.Single(back.Commanders);
		Assert.Equal(BotRoster.PeerIdFor(6), commander.PeerId);
		Assert.Equal(3, commander.BuyTimeZone);
		Assert.True(commander.ReconWanted);
		AiSquad squad = Assert.Single(commander.Squads);
		Assert.Equal(2, squad.Slot);
		Assert.Equal((byte)CommandGoal.Stage, squad.Goal);
		Assert.Equal(AiSquadFlags.Ready | AiSquadFlags.Engaged, squad.Flags);
		Assert.Equal(-1, squad.Zone);
		Assert.Equal(412f, squad.Strength);
		Assert.Equal(new Vector3(10f, 0f, -60f), squad.StagingPoint);
		Assert.Equal(new ushort[] { 4, 9, 12 }, squad.Members);
		AssertSameTrace(frame.Commanders[0].Squads[0].Trace, squad.Trace);

		AiInspect inspect = Assert.Single(back.Inspected);
		Assert.Equal(AiEntityRef.Unit(9), inspect.Ref);
		Assert.True(inspect.Planning);
		AssertSameTrace(frame.Inspected[0].Trace, inspect.Trace);
		Assert.True(inspect.TryMarker(AiMarkerKey.Post, out Vector3 post));
		Assert.Equal(new Vector3(1f, 2f, 3f), post);
		Assert.True(inspect.TryValue(AiValueKey.Issuer, out int issuer));
		Assert.Equal(BotRoster.PeerIdFor(6), issuer);
		Assert.True(inspect.TryValue(AiValueKey.CommanderSquad, out int slot));
		Assert.Equal(-1, slot);
	}

	[Fact]
	public void AnEmptyFrame_IsSmall_AndRoundTrips()
	{
		var frame = new AiDebugFrame { Tick = 9, BotAi = BotAi.Legacy };
		byte[] bytes = AiDebugCodec.EncodeFrame(frame);

		Assert.True(bytes.Length <= 24, $"{bytes.Length} bytes for nothing");
		Assert.True(AiDebugCodec.TryDecodeFrame(bytes, out AiDebugFrame back));
		Assert.Equal(BotAi.Legacy, back.BotAi);
		Assert.Empty(back.Labels);
	}

	[Fact]
	public void EveryTruncation_AndAnyTrailingByte_IsRefused()
	{
		byte[] bytes = AiDebugCodec.EncodeFrame(SampleFrame());

		for (int length = 0; length < bytes.Length; length++)
		{
			Assert.False(AiDebugCodec.TryDecodeFrame(bytes.AsSpan(0, length), out _), $"accepted {length} of {bytes.Length}");
		}

		byte[] longer = bytes.Concat(new byte[] { 0 }).ToArray();
		Assert.False(AiDebugCodec.TryDecodeFrame(longer, out _));
	}

	[Fact]
	public void AnotherVersion_IsRefused()
	{
		byte[] bytes = AiDebugCodec.EncodeFrame(SampleFrame());
		bytes[0] = AiDebugCodec.Version + 1;
		Assert.False(AiDebugCodec.TryDecodeFrame(bytes, out _));

		byte[] watch = AiDebugCodec.EncodeWatch(new AiWatch());
		watch[0] = 0;
		Assert.False(AiDebugCodec.TryDecodeWatch(watch, new AiWatch()));
	}

	[Fact]
	public void Noise_NeverThrows()
	{
		var random = new Random(7);
		var watch = new AiWatch();
		for (int i = 0; i < 20_000; i++)
		{
			var bytes = new byte[random.Next(0, 200)];
			random.NextBytes(bytes);
			if (bytes.Length > 0 && random.Next(2) == 0)
			{
				bytes[0] = AiDebugCodec.Version;
			}

			AiDebugCodec.TryDecodeFrame(bytes, out _);
			AiDebugCodec.TryDecodeWatch(bytes, watch);
		}
	}

	[Fact]
	public void AWatch_RoundTrips()
	{
		var watch = new AiWatch { Flags = AiWatchFlags.Labels | AiWatchFlags.Commander, CommanderPeerId = 77 };
		watch.Selected.Add(AiEntityRef.Unit(12));
		watch.Selected.Add(AiEntityRef.Player(BotRoster.PeerIdFor(1)));
		watch.Selected.Add(AiEntityRef.Squad(BotRoster.PeerIdFor(6), 3));

		var back = new AiWatch();
		Assert.True(AiDebugCodec.TryDecodeWatch(AiDebugCodec.EncodeWatch(watch), back));

		Assert.True(back.SameAs(watch));
		Assert.Equal(3, back.Selected[2].Slot);
		Assert.Equal("squad 3 of bot 7", back.Selected[2].ToString());
	}

	[Fact]
	public void AWatchAskingForTooMuch_IsRefused()
	{
		var watch = new AiWatch();
		byte[] bytes = AiDebugCodec.EncodeWatch(watch);

		byte[] tooMany = (byte[])bytes.Clone();
		tooMany[^1] = AiWatch.MaxSelected + 1;
		Assert.False(AiDebugCodec.TryDecodeWatch(tooMany, new AiWatch()));

		byte[] unknownFlag = (byte[])bytes.Clone();
		unknownFlag[1] = 0x80;
		Assert.False(AiDebugCodec.TryDecodeWatch(unknownFlag, new AiWatch()));
	}

	[Fact]
	public void TheEncoderCapsWhatIsSelected()
	{
		var watch = new AiWatch();
		for (ushort i = 0; i < 10; i++)
		{
			watch.Selected.Add(AiEntityRef.Unit(i));
		}

		var back = new AiWatch();
		Assert.True(AiDebugCodec.TryDecodeWatch(AiDebugCodec.EncodeWatch(watch), back));
		Assert.Equal(AiWatch.MaxSelected, back.Selected.Count);
	}

	[Fact]
	public void AFrameOnADifferentBuild_GivesNoMapToReadItWith()
	{
		AiDebugFrame frame = SampleFrame();
		Assert.Same(HtnDomains.Unit, frame.MapFor(HtnDomainKind.Unit));

		frame.UnitSignature ^= 1;
		Assert.Null(frame.MapFor(HtnDomainKind.Unit));
	}

	[Fact]
	public void AWorstCaseFrame_StaysUnderTenKilobytes()
	{
		// Every unit and every ground slot labelled, two commanders with eight full
		// squads each, and the four inspections a watch may ask for: what a spectator
		// with everything switched on is sent ten times a second (docs/AI_DEBUG.md §5.3).
		var frame = new AiDebugFrame { BotAi = BotAi.Htn };
		for (int i = 0; i < AiDebugCodec.MaxLabels; i++)
		{
			frame.Labels.Add(new AiLabel
			{
				Ref = AiEntityRef.Unit((ushort)i),
				Flags = AiLabelFlags.HasPoint,
				TargetOwnerId = i,
			});
		}

		for (int c = 0; c < 2; c++)
		{
			var commander = new AiCommander { PeerId = c };
			for (int s = 0; s < SquadBoard.DefaultSquads; s++)
			{
				var squad = new AiSquad { Slot = (byte)s };
				for (ushort m = 0; m < 8; m++)
				{
					squad.Members.Add(m);
				}

				Fill(squad.Trace, HtnDomains.Squad);
				commander.Squads.Add(squad);
			}

			frame.Commanders.Add(commander);
		}

		for (int i = 0; i < AiWatch.MaxSelected; i++)
		{
			var inspect = new AiInspect { Ref = AiEntityRef.Unit((ushort)i), Planning = true };
			Fill(inspect.Trace, HtnDomains.Unit);
			for (int m = 0; m < 12; m++)
			{
				inspect.Add((AiMarkerKey)(m + 1), Vector3.One);
				inspect.Add((AiValueKey)(m + 1), m);
			}

			frame.Inspected.Add(inspect);
		}

		byte[] bytes = AiDebugCodec.EncodeFrame(frame);
		Assert.True(bytes.Length < 10_000, $"{bytes.Length} bytes");
		Assert.True(AiDebugCodec.TryDecodeFrame(bytes, out _));
	}

	// ---- helpers -----------------------------------------------------------

	private static AiDebugFrame SampleFrame()
	{
		var frame = new AiDebugFrame
		{
			Tick = 123_456,
			BotAi = BotAi.Htn,
			GroundSignature = HtnDomains.Ground.Signature,
			UnitSignature = HtnDomains.Unit.Signature,
			SquadSignature = HtnDomains.Squad.Signature,
		};

		frame.Labels.Add(new AiLabel
		{
			Ref = AiEntityRef.Player(BotRoster.PeerIdFor(2)),
			Goal = (byte)GroundGoal.HoldNode,
			Detail = (byte)GroundRole.Denier,
			Flags = AiLabelFlags.HasPoint | AiLabelFlags.Engaging,
			Point = new Vector3(-30.12f, 0.5f, 14f),
			TargetOwnerId = OwnerId.ForUnit(7),
		});
		frame.Labels.Add(new AiLabel
		{
			Ref = AiEntityRef.Unit(9),
			Goal = (byte)UnitGoal.HoldPost,
			Detail = (byte)OrderKind.Defend,
			Point = new Vector3(5f, 5f, 5f), // no HasPoint: not sent
		});

		var commander = new AiCommander { PeerId = BotRoster.PeerIdFor(6), BuyTimeZone = 3, ReconWanted = true };
		var squad = new AiSquad
		{
			Slot = 2,
			Role = (byte)CommandRole.Assault,
			Task = (byte)CommandTask.Attack,
			Target = (byte)CommandTarget.Zone,
			Goal = (byte)CommandGoal.Stage,
			Order = (byte)OrderKind.Attack,
			Phase = (byte)SquadPhase.Gathering,
			Flags = AiSquadFlags.Ready | AiSquadFlags.Engaged,
			Zone = -1,
			Strength = 412.4f,
			FullStrength = 500f,
			StrengthAtFormation = 480f,
			Centroid = new Vector3(0f, 0f, -20f),
			TaskPoint = new Vector3(30f, 0f, -120f),
			StagingPoint = new Vector3(10f, 0f, -60f),
			OrderPoint = new Vector3(30f, 0f, -120f),
		};
		squad.Members.AddRange(new ushort[] { 4, 9, 12 });
		Fill(squad.Trace, HtnDomains.Squad);
		commander.Squads.Add(squad);
		frame.Commanders.Add(commander);

		var inspect = new AiInspect { Ref = AiEntityRef.Unit(9), Goal = (byte)UnitGoal.HoldPost, Planning = true };
		Fill(inspect.Trace, HtnDomains.Unit);
		inspect.Add(AiMarkerKey.Post, new Vector3(1f, 2f, 3f));
		inspect.Add(AiMarkerKey.Order, new Vector3(-4f, 0f, 9.99f));
		inspect.Add(AiValueKey.Issuer, BotRoster.PeerIdFor(6));
		inspect.Add(AiValueKey.CommanderSquad, -1);
		frame.Inspected.Add(inspect);

		return frame;
	}

	private static void Fill(HtnTrace trace, HtnDomainMap map)
	{
		trace.Clear();
		trace.Domain = map.Kind;
		trace.Current = map.Count - 1;
		trace.LastStatus = TaskStatus.Continue;
		trace.Planned.AddRange(new[] { 3, 5 });
		trace.Paused.Add(map.Count - 2);
		trace.Traversal.AddRange(new[] { 2, 0 });
		for (int f = 0; f < 4; f++)
		{
			trace.Facts.Add((byte)(f * 3));
		}

		for (int c = 0; c < map.Conditions.Count; c++)
		{
			trace.Holds.Add(c % 3 == 0);
		}

		for (int e = 0; e < HtnHistory.Capacity; e++)
		{
			trace.History.Add(new HtnEvent
			{
				Tick = (uint)(1000 + e),
				Kind = (HtnEventKind)(1 + (e % 9)),
				Node = (byte)(e % map.Count),
				Other = e % 2 == 0 ? HtnHistory.None : (byte)e,
			});
		}
	}

	private static void AssertSameTrace(HtnTrace expected, HtnTrace actual)
	{
		Assert.Equal(expected.Domain, actual.Domain);
		Assert.Equal(expected.Current, actual.Current);
		Assert.Equal(expected.LastStatus, actual.LastStatus);
		Assert.Equal(expected.Planned, actual.Planned);
		Assert.Equal(expected.Paused, actual.Paused);
		Assert.Equal(expected.Traversal, actual.Traversal);
		Assert.Equal(expected.Facts, actual.Facts);
		Assert.Equal(expected.Holds, actual.Holds);
		Assert.Equal(expected.History.Count, actual.History.Count);
		for (int i = 0; i < expected.History.Count; i++)
		{
			Assert.Equal(expected.History[i].Tick, actual.History[i].Tick);
			Assert.Equal(expected.History[i].Kind, actual.History[i].Kind);
			Assert.Equal(expected.History[i].Node, actual.History[i].Node);
			Assert.Equal(expected.History[i].Other, actual.History[i].Other);
		}
	}
}
