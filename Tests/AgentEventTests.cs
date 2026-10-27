using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text.Json;
using Gdpyr.Sim;
using Gdpyr.Sim.Agent;
using Gdpyr.Sim.Htn;
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
		for (byte kind = 0; kind <= (byte)AgentEventSchema.Last; kind++)
		{
			var value = (AgentEventKind)kind;
			Assert.NotEqual("unknown", AgentEventSchema.NameOf(value));
			Assert.Equal(6, AgentEventSchema.FieldsOf(value).Length);
		}
	}

	[Fact]
	public void NoSlotIsNamedKindOrTick_BecauseEveryRecordAlreadyHasThose()
	{
		// WriteEvent writes "kind" and "tick" for every record; a slot with either
		// name would make a document with a duplicate key, which a strict JSON
		// reader throws on rather than merging.
		for (byte kind = 0; kind <= (byte)AgentEventSchema.Last; kind++)
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
		for (byte kind = 0; kind <= (byte)AgentEventSchema.Last; kind++)
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
			AgentJson.WriteEvent(writer, AgentEvent.Make(AgentEventKind.Kill, 51204, 2147483641, 3, 2, 63.4f, 47f));
		}

		using JsonDocument document = JsonDocument.Parse(buffer.WrittenMemory);
		JsonElement root = document.RootElement;

		Assert.Equal("kill", root.GetProperty("kind").GetString());
		Assert.Equal(51204u, root.GetProperty("tick").GetUInt32());
		Assert.Equal(2147483641, root.GetProperty("attacker").GetInt32());
		Assert.Equal(3, root.GetProperty("victim").GetInt32());
		Assert.Equal(2, root.GetProperty("weapon").GetInt32());
		Assert.Equal(63.4f, root.GetProperty("distance").GetSingle(), 3);
		Assert.Equal(47, root.GetProperty("rounds_fired").GetInt32());
		Assert.Equal(7, CountProperties(root));
	}

	[Fact]
	public void ADamageRecordSaysWhatTheVestTookBesideTheHealthLost()
	{
		// docs/LOADOUT.md §5.2: amount stays health lost, which every reward reads, and
		// absorbed is added beside it — a rifle round on a full heavy vest.
		var buffer = new ArrayBufferWriter<byte>(256);
		using (var writer = new Utf8JsonWriter(buffer))
		{
			AgentJson.WriteEvent(writer, AgentEvent.Make(AgentEventKind.Damage, 700, OwnerId.ForUnit(4), 3, 3, 7f, 93f,
				13f));
		}

		using JsonDocument document = JsonDocument.Parse(buffer.WrittenMemory);
		JsonElement root = document.RootElement;
		Assert.Equal("damage", root.GetProperty("kind").GetString());
		Assert.Equal(7f, root.GetProperty("amount").GetSingle());
		Assert.Equal(93f, root.GetProperty("remaining").GetSingle());
		Assert.Equal(13f, root.GetProperty("absorbed").GetSingle());
		Assert.Equal(8, CountProperties(root));
		Assert.Equal("absorbed", AgentEventSchema.FieldsOf(AgentEventKind.Damage)[5]);
	}

	[Fact]
	public void TheSixthSlotIsWrittenOnlyForTheKindThatNamesIt()
	{
		// A kill has five fields; whatever is in its sixth slot is not one of them.
		var buffer = new ArrayBufferWriter<byte>(256);
		using (var writer = new Utf8JsonWriter(buffer))
		{
			AgentJson.WriteEvent(writer, AgentEvent.Make(AgentEventKind.Kill, 51204, 2, 3, 2, 63.4f, 47f, 99f));
		}

		using JsonDocument document = JsonDocument.Parse(buffer.WrittenMemory);
		Assert.Equal(7, CountProperties(document.RootElement));
		Assert.Equal(99f, AgentEvent.Make(AgentEventKind.Kill, 1, z: 99f).Z);
	}

	[Fact]
	public void AKillOfAUnitSaysItCountedNoRounds()
	{
		// A unit or a structure carries no primary to count (docs/LOADOUT.md L0).
		var buffer = new ArrayBufferWriter<byte>(256);
		using (var writer = new Utf8JsonWriter(buffer))
		{
			AgentJson.WriteEvent(writer, AgentEvent.Make(AgentEventKind.Kill, 900, 3, OwnerId.ForUnit(12), 2, 40f,
				AgentEventSchema.NoRoundsFired));
		}

		using JsonDocument document = JsonDocument.Parse(buffer.WrittenMemory);
		Assert.Equal(-12, document.RootElement.GetProperty("victim").GetInt32());
		Assert.Equal(-1, document.RootElement.GetProperty("rounds_fired").GetInt32());
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

	[Fact]
	public void StructuresAreOnTheStreamUnderTheirOwnNames()
	{
		Assert.Equal("structure_placed", AgentEventSchema.NameOf(AgentEventKind.StructurePlaced));
		Assert.Equal("structure_built", AgentEventSchema.NameOf(AgentEventKind.StructureBuilt));
		Assert.Equal("structure_lost", AgentEventSchema.NameOf(AgentEventKind.StructureLost));
		Assert.Equal("killer", AgentEventSchema.FieldsOf(AgentEventKind.StructureLost)[2]);
	}

	[Fact]
	public void TheHtnEventsAreOnTheStreamUnderTheirOwnNames()
	{
		// docs/HTN_BOTS.md §6, H6: what a scenario needs to say about the bots that
		// the older kinds cannot.
		Assert.Equal("squad_task", AgentEventSchema.NameOf(AgentEventKind.SquadTask));
		Assert.Equal("contact_spotted", AgentEventSchema.NameOf(AgentEventKind.ContactSpotted));
		Assert.Equal("node_contested", AgentEventSchema.NameOf(AgentEventKind.NodeContested));
		Assert.Equal((AgentEventKind)14, AgentEventKind.NodeContested);
		Assert.Equal("ticks", AgentEventSchema.FieldsOf(AgentEventKind.NodeContested)[3]);
		Assert.Equal("goal", AgentEventSchema.FieldsOf(AgentEventKind.SquadTask)[2]);
		Assert.Equal("observer", AgentEventSchema.FieldsOf(AgentEventKind.ContactSpotted)[1]);
	}

	[Fact]
	public void TheVehicleEventsAreAppended_UnderTheirOwnNames()
	{
		// docs/VEHICLES.md §10: after node_contested, so every recorded trace keeps its numbers.
		Assert.Equal(15, (int)AgentEventKind.UnitEmbarked);
		Assert.Equal(16, (int)AgentEventKind.UnitDisembarked);
		Assert.Equal(17, (int)AgentEventKind.DropshipLanded);

		Assert.Equal("unit_embarked", AgentEventSchema.NameOf(AgentEventKind.UnitEmbarked));
		Assert.Equal("unit_disembarked", AgentEventSchema.NameOf(AgentEventKind.UnitDisembarked));
		Assert.Equal("dropship_landed", AgentEventSchema.NameOf(AgentEventKind.DropshipLanded));
		Assert.Equal("vehicle", AgentEventSchema.FieldsOf(AgentEventKind.UnitEmbarked)[1]);
		Assert.Equal("tier", AgentEventSchema.FieldsOf(AgentEventKind.UnitDisembarked)[2]);
		Assert.Equal("aboard", AgentEventSchema.FieldsOf(AgentEventKind.DropshipLanded)[1]);
	}

	[Fact]
	public void GrenadeThrown_IsAppendedAfterTheVehicleEvents()
	{
		// docs/TACTICS.md §5.4: after dropship_landed, so every recorded trace keeps its numbers.
		Assert.Equal(18, (int)AgentEventKind.GrenadeThrown);
		Assert.Equal("grenade_thrown", AgentEventSchema.NameOf(AgentEventKind.GrenadeThrown));
		Assert.Equal(new[] { "thrower", "tier", "hidden", "x", "z", "" },
			AgentEventSchema.FieldsOf(AgentEventKind.GrenadeThrown).ToArray());
	}

	[Fact]
	public void UnitTask_IsAppendedAfterGrenadeThrown_AndItsGoalsAreProtocolNumbers()
	{
		// docs/TACTICS.md §5.1: the goal is the UnitGoal's number, and a scenario names it.
		Assert.Equal(19, (int)AgentEventKind.UnitTask);
		Assert.Equal("unit_task", AgentEventSchema.NameOf(AgentEventKind.UnitTask));
		Assert.Equal(new[] { "unit", "tier", "goal", "x", "z", "" }, AgentEventSchema.FieldsOf(AgentEventKind.UnitTask).ToArray());
		Assert.Equal(18, (int)UnitGoal.TakeCoverFromFire);
		Assert.Equal(19, (int)UnitGoal.ReturnFire);
		Assert.Equal(20, (int)UnitGoal.ThrowGrenade);
		Assert.Equal(21, (int)UnitGoal.ToHide);
		Assert.Equal(22, (int)UnitGoal.PopOut);
		Assert.Equal(23, (int)UnitGoal.FireFromPeek);
		Assert.Equal(24, (int)UnitGoal.Duck);
		Assert.Equal(25, (int)UnitGoal.Relocate);
		Assert.Equal(26, (int)UnitGoal.SuppressPoint);
		Assert.Equal(27, (int)UnitGoal.Reload);
		Assert.Equal(28, (int)UnitGoal.DrillGround);
		Assert.Equal(29, (int)UnitGoal.DrillFirePosition);
		Assert.Equal(30, (int)UnitGoal.DrillBound);
		Assert.Equal(31, (int)UnitGoal.DrillHold);
		Assert.Equal(32, (int)UnitGoal.DrillRush);
		Assert.Equal(33, (int)UnitGoal.GuardBuilder);
		Assert.True(UnitContext.IsTactic(UnitGoal.TakeCoverFromFire));
		Assert.True(UnitContext.IsTactic(UnitGoal.ReturnFire));
		Assert.True(UnitContext.IsTactic(UnitGoal.ThrowGrenade));
		Assert.True(UnitContext.IsTactic(UnitGoal.FireFromPeek));
		Assert.True(UnitContext.IsTactic(UnitGoal.SuppressPoint));
		Assert.True(UnitContext.IsTactic(UnitGoal.Reload));
		Assert.True(UnitContext.IsTactic(UnitGoal.DrillBound));
		Assert.True(UnitContext.IsTactic(UnitGoal.GuardBuilder));
		Assert.False(UnitContext.IsTactic(UnitGoal.HoldCover));
	}

	[Fact]
	public void SquadDrill_IsAppendedAfterUnitTask_AndItsPhasesAreProtocolNumbers()
	{
		// docs/TACTICS.md §5.5: the phase is the DrillPhase's number, and a scenario names it.
		Assert.Equal(20, (int)AgentEventKind.SquadDrill);
		Assert.Equal("squad_drill", AgentEventSchema.NameOf(AgentEventKind.SquadDrill));
		Assert.Equal(new[] { "squad", "phase", "elements", "x", "z", "" },
			AgentEventSchema.FieldsOf(AgentEventKind.SquadDrill).ToArray());
		Assert.Equal(5, (int)DrillPhase.Bound);
		Assert.Equal(7, (int)DrillPhase.Rush);
	}

	[Fact]
	public void Kit_IsAppendedAfterSquadDrill_AndNamesThePrimaryByCatalogId()
	{
		// docs/LOADOUT.md §5.3: a kit taken at a locker. Appended, so every kind before
		// it keeps its number.
		Assert.Equal(21, (int)AgentEventKind.Kit);
		Assert.Equal(AgentEventKind.Kit, AgentEventSchema.Last);
		Assert.Equal("kit", AgentEventSchema.NameOf(AgentEventKind.Kit));
		Assert.Equal(new[] { "peer", "primary", "armour", "magazines", "grenades", "" },
			AgentEventSchema.FieldsOf(AgentEventKind.Kit).ToArray());
		Assert.Equal(-1, AgentEventSchema.NoPrimary);
	}

	[Fact]
	public void AKitSerializesAsItsNamedFields()
	{
		var buffer = new ArrayBufferWriter<byte>(256);
		using (var writer = new Utf8JsonWriter(buffer))
		{
			AgentJson.WriteEvent(writer, AgentEvent.Make(AgentEventKind.Kit, 600, 3, 4, 2, 3f, 0f));
		}

		using JsonDocument document = JsonDocument.Parse(buffer.WrittenMemory);
		JsonElement root = document.RootElement;
		Assert.Equal("kit", root.GetProperty("kind").GetString());
		Assert.Equal(600u, root.GetProperty("tick").GetUInt32());
		Assert.Equal(3, root.GetProperty("peer").GetInt32());
		Assert.Equal(4, root.GetProperty("primary").GetInt32());
		Assert.Equal(2, root.GetProperty("armour").GetInt32());
		Assert.Equal(3.0, root.GetProperty("magazines").GetDouble());
		Assert.Equal(0.0, root.GetProperty("grenades").GetDouble());
	}

	[Fact]
	public void TheSchemaNamesTheVehicleCommands_AppendedAfterTheOldOnes()
	{
		Assert.Equal(6, (int)AgentCommandKind.Embark);
		Assert.Equal(7, (int)AgentCommandKind.Unload);
		Assert.Equal(new[] { "order", "build", "cancel", "rally", "noop", "construct" }, AgentJson.CommandNames[..6]);
		Assert.Contains("embark", AgentJson.CommandNames);
		Assert.Contains("unload", AgentJson.CommandNames);
	}

	[Fact]
	public void TheSchemaNamesTheAutonomyCommands_AppendedAfterTheVehicleOnes()
	{
		// docs/TACTICS.md §5.9: appended, so every older command keeps its number.
		Assert.Equal(8, (int)AgentCommandKind.Autonomy);
		Assert.Equal(9, (int)AgentCommandKind.AutonomyDefault);
		Assert.Equal(new[] { "order", "build", "cancel", "rally", "noop", "construct", "embark", "unload" },
			AgentJson.CommandNames[..8]);
		Assert.Equal(new[] { "autonomy", "autonomy_default" }, AgentJson.CommandNames[8..]);
		Assert.Equal(new[] { "off", "on" }, AgentJson.AutonomyNames);
	}

	[Fact]
	public void AnAutonomyCommandKeepsItsModeAndItsUnits()
	{
		var list = new AgentCommandList();
		Assert.True(list.AddWithUnits(new AgentCommand { Kind = AgentCommandKind.Autonomy, Autonomy = MicroAutonomy.On },
			new[] { 3, 4 }));
		Assert.True(list.Add(new AgentCommand { Kind = AgentCommandKind.AutonomyDefault, Autonomy = MicroAutonomy.Off }));

		AgentCommand set = list.Commands[0];
		Assert.Equal(MicroAutonomy.On, set.Autonomy);
		Assert.Equal(new[] { 3, 4 }, list.UnitsOf(set).ToArray());
		Assert.Equal(MicroAutonomy.Off, list.Commands[1].Autonomy);
		Assert.True(list.UnitsOf(list.Commands[1]).IsEmpty);
	}

	[Fact]
	public void AnEmbarkCommandKeepsItsVehicleAndItsPassengers_AndAnUnloadItsVehicles()
	{
		var list = new AgentCommandList();
		Assert.True(list.AddWithUnits(new AgentCommand { Kind = AgentCommandKind.Embark, Vehicle = 12 }, new[] { 3, 4, 5 }));
		Assert.True(list.AddWithUnits(new AgentCommand { Kind = AgentCommandKind.Unload }, new[] { 12, 14 }));

		AgentCommand[] stored = list.Commands.ToArray();
		Assert.Equal(AgentCommandKind.Embark, stored[0].Kind);
		Assert.Equal(12, stored[0].Vehicle);
		Assert.Equal(new[] { 3, 4, 5 }, list.UnitsOf(stored[0]).ToArray());
		Assert.Equal(AgentCommandKind.Unload, stored[1].Kind);
		Assert.Equal(new[] { 12, 14 }, list.UnitsOf(stored[1]).ToArray());
	}

	[Fact]
	public void TheSquadGoalNumbersAreTheOnesTheApiPublishes()
	{
		// squad_task carries a CommandGoal by number (docs/AGENT_API.md §8): a
		// renumbering would silently change what every recorded trace means.
		Assert.Equal(0, (int)CommandGoal.None);
		Assert.Equal(1, (int)CommandGoal.FallBack);
		Assert.Equal(2, (int)CommandGoal.Reinforce);
		Assert.Equal(3, (int)CommandGoal.Stage);
		Assert.Equal(4, (int)CommandGoal.Strike);
		Assert.Equal(5, (int)CommandGoal.Scout);
		Assert.Equal(6, (int)CommandGoal.Station);
		Assert.Equal(7, (int)CommandGoal.Refill);
		Assert.Equal(8, (int)CommandGoal.Hold);
	}

	[Fact]
	public void TheSchemaNamesEveryStructureAndTheCommandThatBuildsThem()
	{
		Assert.Equal(StructureKinds.Count, AgentJson.StructureNames.Length);
		Assert.Contains("construct", AgentJson.CommandNames);
	}

	[Fact]
	public void AConstructCommandKeepsItsKindAndItsBuilders()
	{
		var list = new AgentCommandList();
		var command = new AgentCommand
		{
			Kind = AgentCommandKind.Construct,
			Structure = StructureKinds.SniperTower,
			Yaw = 1.25f,
			X = 10f,
			Z = -4f,
		};

		Assert.True(list.AddWithUnits(command, new[] { 7, 9 }));

		AgentCommand stored = Assert.Single(list.Commands.ToArray());
		Assert.Equal(AgentCommandKind.Construct, stored.Kind);
		Assert.Equal(StructureKinds.SniperTower, stored.Structure);
		Assert.Equal(1.25f, stored.Yaw);
		Assert.Equal(new[] { 7, 9 }, list.UnitsOf(stored).ToArray());
	}

	[Fact]
	public void ThePlaytestHarnessKnowsEveryKindTheGameEmits()
	{
		// A kind the harness does not know reads as a typo rather than as zero, so a
		// scenario claiming "no structure was lost" would fail for the wrong reason.
		for (byte kind = 0; kind <= (byte)AgentEventSchema.Last; kind++)
		{
			Assert.Contains(AgentEventSchema.NameOf((AgentEventKind)kind),
				Gdpyr.Playtest.EpisodeMetrics.AgentEventKinds);
		}
	}

	[Fact]
	public void ThePlaytestHarnessKnowsEveryFieldOfEveryKind()
	{
		// A filter on a field the harness does not know is a typo; one it knows that
		// the game does not write would read zero for ever.
		Assert.Equal((byte)AgentEventSchema.Last + 1, Gdpyr.Playtest.EpisodeMetrics.AgentEventFields.Count);
		for (byte kind = 0; kind <= (byte)AgentEventSchema.Last; kind++)
		{
			string name = AgentEventSchema.NameOf((AgentEventKind)kind);
			var published = new List<string>();
			foreach (string field in AgentEventSchema.FieldsOf((AgentEventKind)kind))
			{
				if (field.Length > 0)
				{
					published.Add(field);
				}
			}

			Assert.True(Gdpyr.Playtest.EpisodeMetrics.AgentEventFields.TryGetValue(name, out string[] known), name);
			Assert.Equal(published, known);
		}
	}
}
