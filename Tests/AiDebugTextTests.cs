using System;
using System.Linq;
using Gdpyr.Sim;
using Gdpyr.Sim.AiDebug;
using Gdpyr.Sim.Htn;
using Godot;
using Xunit;

namespace Gdpyr.Tests;

/// <summary>
/// What the AI debugger says (docs/AI_DEBUG.md §4.1, §7): the console's target
/// grammar, the five-task colouring, a commander's squad table, and one inspected
/// agent described from what a frame carries.
/// </summary>
public class AiDebugTextTests
{
	// ---- the console's targets ---------------------------------------------

	[Theory]
	[InlineData("bot 3", AiEntityKind.Player, 0x4000_0002, 0)]
	[InlineData("bot3", AiEntityKind.Player, 0x4000_0002, 0)]
	[InlineData("BOT 16", AiEntityKind.Player, 0x4000_000F, 0)]
	[InlineData("peer 1234567", AiEntityKind.Player, 1234567, 0)]
	[InlineData("unit 42", AiEntityKind.Unit, 42, 0)]
	[InlineData("u42", AiEntityKind.Unit, 42, 0)]
	[InlineData("squad 2", AiEntityKind.Squad, AiTargets.FirstStrategist, 2)]
	[InlineData("squad 2 bot 7", AiEntityKind.Squad, 0x4000_0006, 2)]
	[InlineData("s2 b7", AiEntityKind.Squad, 0x4000_0006, 2)]
	public void Targets_Parse(string line, AiEntityKind kind, int id, int slot)
	{
		Assert.True(AiTargets.TryParse(line.Split(' '), out AiEntityRef target, out string error), error);

		Assert.Equal(kind, target.Kind);
		Assert.Equal(id, target.Id);
		Assert.Equal(slot, target.Slot);
	}

	[Theory]
	[InlineData("")]
	[InlineData("bot")]
	[InlineData("bot 0")]
	[InlineData("bot 17")]
	[InlineData("bot x")]
	[InlineData("tank 3")]
	[InlineData("unit 70000")]
	[InlineData("unit -1")]
	[InlineData("squad 2 unit 3")]
	[InlineData("bot 3 bot 4")]
	public void Targets_RefuseWhatTheyCannotMean(string line)
	{
		Assert.False(AiTargets.TryParse(line.Split(' ', StringSplitOptions.RemoveEmptyEntries), out _, out string error));
		Assert.False(string.IsNullOrEmpty(error));
	}

	[Fact]
	public void ARefNamesItselfAsTheRosterDoes()
	{
		Assert.Equal("bot 3", AiEntityRef.Player(BotRoster.PeerIdFor(2)).ToString());
		Assert.Equal("peer 99", AiEntityRef.Player(99).ToString());
		Assert.Equal("unit 4", AiEntityRef.Unit(4).ToString());
		Assert.Equal("squad 1 of bot 7", AiEntityRef.Squad(BotRoster.PeerIdFor(6), 1).ToString());
		Assert.Equal(0, AiEntityRef.Player(5).Slot);
	}

	// ---- goals -------------------------------------------------------------

	[Fact]
	public void EveryGoalOfEveryDomain_BelongsToAFamily()
	{
		foreach (GroundGoal goal in Enum.GetValues<GroundGoal>().Where(g => g != GroundGoal.None))
		{
			Assert.NotEqual(AiTaskFamily.None, AiGoals.Family(HtnDomainKind.Ground, (byte)goal));
		}

		foreach (UnitGoal goal in Enum.GetValues<UnitGoal>().Where(g => g != UnitGoal.None))
		{
			Assert.NotEqual(AiTaskFamily.None, AiGoals.Family(HtnDomainKind.Unit, (byte)goal));
		}

		foreach (CommandGoal goal in Enum.GetValues<CommandGoal>().Where(g => g != CommandGoal.None))
		{
			Assert.NotEqual(AiTaskFamily.None, AiGoals.Family(HtnDomainKind.Squad, (byte)goal));
		}
	}

	[Fact]
	public void ARetreatIsARetreat_OnEveryLayer()
	{
		// §5.4's table, read down its Retreat row: one colour for all three.
		Assert.Equal(AiTaskFamily.Retreat, AiGoals.Family(HtnDomainKind.Ground, (byte)GroundGoal.FallBack));
		Assert.Equal(AiTaskFamily.Retreat, AiGoals.Family(HtnDomainKind.Unit, (byte)UnitGoal.Retreat));
		Assert.Equal(AiTaskFamily.Retreat, AiGoals.Family(HtnDomainKind.Squad, (byte)CommandGoal.FallBack));

		Assert.Equal("Defend Zone", AiGoals.Describe(AiTaskFamily.DefendZone));
		Assert.Equal("idle", AiGoals.Name(HtnDomainKind.Unit, 0));
		Assert.Equal("HoldPost", AiGoals.Name(HtnDomainKind.Unit, (byte)UnitGoal.HoldPost));
		Assert.Equal(7, Enum.GetValues<AiTaskFamily>().Skip(1).Select(AiGoals.Color).Distinct().Count());
	}

	// ---- the commander ------------------------------------------------------

	[Fact]
	public void TheSquadTable_HasARowPerSquad_AndLinksWhenAsked()
	{
		var commander = new AiCommander { PeerId = BotRoster.PeerIdFor(6), ReconWanted = true, BuyTimeZone = 2 };
		commander.Squads.Add(Squad(0, CommandRole.Garrison, CommandTask.Defend, CommandGoal.Reinforce, members: 4));
		AiSquad assault = Squad(3, CommandRole.Assault, CommandTask.Attack, CommandGoal.Stage, members: 5);
		assault.Flags = AiSquadFlags.Ready | AiSquadFlags.Engaged;
		assault.Target = (byte)CommandTarget.Zone;
		assault.Zone = 4;
		assault.Phase = (byte)SquadPhase.Gathering;
		assault.Strength = 240f;
		assault.StrengthAtFormation = 480f;
		commander.Squads.Add(assault);

		string[] plain = SquadText.Table(commander, HtnTextStyle.Plain).Split('\n');

		Assert.Equal("bot 7's commander: 2 squads · wants recon · buying time at zone 2", plain[0]);
		Assert.Equal(4, plain.Length);
		Assert.StartsWith("0  garrison   4  Defend", plain[2]);
		Assert.Contains("Attack  zone 4    Stage     Gathering    50%  engaged", plain[3]);
		Assert.Equal("a", SquadText.RoleLetter(assault));

		string rich = SquadText.Table(commander, HtnTextStyle.Rich, link: true);
		Assert.Contains($"[url=squad:{commander.PeerId}:3]", rich);
	}

	// ---- an inspection --------------------------------------------------------

	[Fact]
	public void AnInspectedBot_ReadsAsWhoItIs_ThenItsPlan()
	{
		var planning = new GroundPlanning(GroundPlanTraits.Default);
		GroundContext c = planning.CreateContext();
		c.Tick = 600;
		c.Sense(GroundFact.Role, (byte)GroundRole.Denier);
		c.HasZone = true;
		c.ZonePoint = new Vector3(10f, 0f, 10f);
		planning.Tick(c);
		c.Tick = 660;

		var inspect = new AiInspect { Ref = AiEntityRef.Player(BotRoster.PeerIdFor(0)), Planning = true, Goal = (byte)c.Intent.Goal };
		HtnTracer.Capture(planning.Map, c, c.History, inspect.Trace);
		inspect.Add(AiValueKey.Health, 80);
		inspect.Add(AiValueKey.Role, (int)GroundRole.Denier);
		inspect.Add(AiValueKey.Move, (int)c.Intent.Move);
		inspect.Add(AiValueKey.Target, OwnerId.ForUnit(5));

		var frame = new AiDebugFrame { Tick = 660, BotAi = BotAi.Htn, GroundSignature = HtnDomains.Ground.Signature };
		frame.Inspected.Add(inspect);

		string text = AiDebugText.Describe(inspect, frame, HtnTextStyle.Plain);
		string[] lines = text.Split('\n');

		Assert.Equal("bot 1 · ground bot · role Denier · health 80", lines[0]);
		Assert.Equal("goal TakeNode (Defend Zone) · move Run · shooting unit 5", lines[1]);
		Assert.Contains("running TakeNode 1.0s  then HoldNode", text);
		Assert.Contains("Role=Denier", text);
		Assert.Contains(">> TakeNode", text);
		Assert.Contains("planned TakeNode", text);
	}

	[Fact]
	public void AFrameFromAnotherBuild_SaysSoInsteadOfDrawingTheWrongTree()
	{
		var inspect = new AiInspect { Ref = AiEntityRef.Unit(3), Planning = true };
		inspect.Trace.Domain = HtnDomainKind.Unit;
		inspect.Trace.Current = 5;
		var frame = new AiDebugFrame { UnitSignature = HtnDomains.Unit.Signature + 1 };

		Assert.Null(AiDebugText.MapFor(inspect, frame, out string problem));
		Assert.Contains("signature mismatch", problem);
		Assert.Contains("signature mismatch", AiDebugText.Describe(inspect, frame, HtnTextStyle.Plain));
	}

	[Theory]
	[InlineData(1, 0, "a person: nothing plans for them")]
	[InlineData(0, 1, "a strategist's seat")]
	public void SomebodyWithNoPlan_IsSaidToHaveNone(int person, int team, string expected)
	{
		var inspect = new AiInspect { Ref = AiEntityRef.Player(77) };
		inspect.Add(AiValueKey.Person, person);
		inspect.Add(AiValueKey.Team, team);
		inspect.Add(AiValueKey.Health, 100);

		Assert.Contains(expected, AiDebugText.Header(inspect, new AiDebugFrame(), HtnTextStyle.Plain));
	}

	[Fact]
	public void Owners_AreNamedByWhatTheyAre()
	{
		Assert.Equal("nothing", AiDebugText.Owner(OwnerId.None));
		Assert.Equal("unit 9", AiDebugText.Owner(OwnerId.ForUnit(9)));
		Assert.Equal("structure 2", AiDebugText.Owner(OwnerId.ForStructure(2)));
		Assert.Equal("defence 1", AiDebugText.Owner(OwnerId.ForDefense(1)));
		Assert.Equal("bot 2", AiDebugText.Owner(BotRoster.PeerIdFor(1)));
	}

	private static AiSquad Squad(int slot, CommandRole role, CommandTask task, CommandGoal goal, int members)
	{
		var squad = new AiSquad
		{
			Slot = (byte)slot,
			Role = (byte)role,
			Task = (byte)task,
			Goal = (byte)goal,
			Phase = (byte)SquadPhase.Moving,
			Strength = 400f,
			StrengthAtFormation = 400f,
		};

		for (ushort m = 0; m < members; m++)
		{
			squad.Members.Add(m);
		}

		return squad;
	}
}
