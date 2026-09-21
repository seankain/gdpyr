using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text.Json;
using Gdpyr.Sim.Agent;
using Xunit;

namespace Gdpyr.Tests;

/// <summary>
/// The event stream (docs/AGENT_API.md §8).
///
/// The API emits events rather than rewards: what a trainer values is the
/// trainer's business. What the game owes is the raw record — and a reader that
/// has fallen behind has to be told it lost some, rather than being left to
/// believe it saw everything.
/// </summary>
public class AgentEventTests
{
	[Fact]
	public void AReaderSeesEverythingWrittenSinceItsCursor()
	{
		var log = new AgentEventLog();
		log.Emit(AgentEvent.Make(AgentEventKind.Kill, 10, 1, 2, 3, 63.4f));
		log.Emit(AgentEvent.Make(AgentEventKind.Damage, 11, 1, 2, 3, 25f, 75f));

		var drain = new AgentEvent[8];
		int count = log.Since(0, drain, out ulong next, out ulong lost);

		Assert.Equal(2, count);
		Assert.Equal(2ul, next);
		Assert.Equal(0ul, lost);
		Assert.Equal(AgentEventKind.Kill, drain[0].Kind);
		Assert.Equal(11u, drain[1].Tick);
	}

	[Fact]
	public void AnUpToDateReaderSeesNothingAndStaysWhereItIs()
	{
		var log = new AgentEventLog();
		log.Emit(AgentEvent.Make(AgentEventKind.RoundStart, 1));

		var drain = new AgentEvent[8];
		log.Since(0, drain, out ulong next, out _);

		Assert.Equal(0, log.Since(next, drain, out ulong again, out _));
		Assert.Equal(next, again);
	}

	[Fact]
	public void AReaderThatFellOffTheRingIsToldHowMuchItLost()
	{
		var log = new AgentEventLog();
		for (int i = 0; i < AgentEventLog.Capacity + 10; i++)
		{
			log.Emit(AgentEvent.Make(AgentEventKind.Damage, (uint)i));
		}

		var drain = new AgentEvent[AgentEventLog.Capacity];
		int count = log.Since(0, drain, out ulong next, out ulong lost);

		Assert.Equal(10ul, lost);
		Assert.Equal(AgentEventLog.Capacity, count);
		Assert.Equal((ulong)(AgentEventLog.Capacity + 10), next);
		Assert.Equal(10u, drain[0].Tick);
	}

	[Fact]
	public void ADrainSmallerThanTheBacklogIsResumable()
	{
		var log = new AgentEventLog();
		for (int i = 0; i < 10; i++)
		{
			log.Emit(AgentEvent.Make(AgentEventKind.UnitBuilt, (uint)i));
		}

		var drain = new AgentEvent[4];
		int first = log.Since(0, drain, out ulong next, out _);
		Assert.Equal(4, first);
		Assert.Equal(4ul, next);

		int second = log.Since(next, drain, out next, out _);
		Assert.Equal(4, second);
		Assert.Equal(4u, drain[0].Tick);
	}

	[Fact]
	public void EveryKindHasAFullSlateOfFieldNames()
	{
		for (byte kind = 0; kind <= (byte)AgentEventKind.SeatReleased; kind++)
		{
			var value = (AgentEventKind)kind;
			Assert.NotEqual("unknown", AgentEventSchema.NameOf(value));
			Assert.Equal(5, AgentEventSchema.FieldsOf(value).Length);
		}
	}

	[Fact]
	public void NoSlotIsNamedKindOrTick_BecauseEveryRecordAlreadyHasThose()
	{
		// WriteEvent writes "kind" and "tick" for every record; a slot with either
		// name would make a document with a duplicate key, which a strict JSON
		// reader throws on rather than merging.
		for (byte kind = 0; kind <= (byte)AgentEventKind.SeatReleased; kind++)
		{
			foreach (string name in AgentEventSchema.FieldsOf((AgentEventKind)kind))
			{
				Assert.NotEqual("kind", name);
				Assert.NotEqual("tick", name);
			}
		}
	}

	[Fact]
	public void EveryEventSerializesWithNoDuplicateKeys()
	{
		for (byte kind = 0; kind <= (byte)AgentEventKind.SeatReleased; kind++)
		{
			var buffer = new ArrayBufferWriter<byte>(256);
			using (var writer = new Utf8JsonWriter(buffer))
			{
				AgentJson.WriteEvent(writer, AgentEvent.Make((AgentEventKind)kind, 1, 2, 3, 4, 5f, 6f));
			}

			using JsonDocument document = JsonDocument.Parse(buffer.WrittenMemory);
			var seen = new HashSet<string>();
			foreach (JsonProperty property in document.RootElement.EnumerateObject())
			{
				Assert.True(seen.Add(property.Name), $"{(AgentEventKind)kind} writes '{property.Name}' twice");
			}
		}
	}

	[Fact]
	public void AKillSerializesToTheObjectTheDesignShows()
	{
		var buffer = new ArrayBufferWriter<byte>(256);
		using (var writer = new Utf8JsonWriter(buffer))
		{
			AgentJson.WriteEvent(writer, AgentEvent.Make(AgentEventKind.Kill, 51204, 2147483641, 3, 2, 63.4f));
		}

		using JsonDocument document = JsonDocument.Parse(buffer.WrittenMemory);
		JsonElement root = document.RootElement;

		Assert.Equal("kill", root.GetProperty("kind").GetString());
		Assert.Equal(51204u, root.GetProperty("tick").GetUInt32());
		Assert.Equal(2147483641, root.GetProperty("attacker").GetInt32());
		Assert.Equal(3, root.GetProperty("victim").GetInt32());
		Assert.Equal(2, root.GetProperty("weapon").GetInt32());
		Assert.Equal(63.4f, root.GetProperty("distance").GetSingle(), 3);

		// A kill has no fifth slot, and an unnamed slot is not written at all.
		Assert.Equal(6, CountProperties(root));
	}

	[Fact]
	public void TheBusIsSilentUntilSomebodyIsListening()
	{
		Assert.Null(AgentEventBus.Active);

		// A round nobody has attached to costs a null check, not a record.
		AgentEventBus.Emit(AgentEventKind.Kill, 1);

		var log = new AgentEventLog();
		AgentEventBus.Active = log;
		try
		{
			AgentEventBus.Emit(AgentEventKind.Kill, 2);
			Assert.Equal(1ul, log.Sequence);
		}
		finally
		{
			AgentEventBus.Active = null;
		}
	}

	private static int CountProperties(JsonElement element)
	{
		int count = 0;
		foreach (JsonProperty _ in element.EnumerateObject())
		{
			count++;
		}

		return count;
	}
}
