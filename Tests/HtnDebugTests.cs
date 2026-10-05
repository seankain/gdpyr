using System;
using System.Linq;
using FluidHTN.Factory;
using Gdpyr.Sim;
using Gdpyr.Sim.AiDebug;
using Gdpyr.Sim.Htn;
using Godot;
using Xunit;

namespace Gdpyr.Tests;

/// <summary>
/// The debugger's view of an HTN (docs/AI_DEBUG.md §3, §4): the three real domains
/// flattened and numbered the same way on every build; a live context read into a
/// trace — the task running, the tasks planned behind it, the ones behind a pause,
/// which conditions hold; the planner's history, without an allocation on the tick;
/// and the text and graph the spectator reads it all through.
/// </summary>
public class HtnDebugTests
{
	private static readonly UnitTraits Rifleman = new(45f, 40f, 1.5f, 20f);

	// ---- maps --------------------------------------------------------------

	[Theory]
	[InlineData(HtnDomainKind.Ground, "ground", typeof(GroundGoal))]
	[InlineData(HtnDomainKind.Unit, "unit", typeof(UnitGoal))]
	[InlineData(HtnDomainKind.Squad, "squad", typeof(CommandGoal))]
	public void EachDomain_FlattensIntoAMapWhosePrimitivesAreItsGoals(HtnDomainKind kind, string root, Type goals)
	{
		HtnDomainMap map = HtnDomains.For(kind);

		Assert.Equal(kind, map.Kind);
		Assert.Equal(root, map.Name);
		Assert.Equal(HtnNodeKind.Root, map[0].Kind);
		Assert.InRange(map.Count, 2, HtnDomainMap.MaxNodes);
		Assert.InRange(map.Conditions.Count, 1, HtnDomainMap.MaxConditions);

		string[] goalNames = Enum.GetNames(goals);
		foreach (HtnNode node in map.Nodes.Where(n => n.Kind == HtnNodeKind.Primitive))
		{
			Assert.Contains(node.Name, goalNames);
			Assert.Empty(node.Children);
		}

		// Parent, child, depth and branch all describe one tree.
		for (int i = 1; i < map.Count; i++)
		{
			HtnNode node = map[i];
			HtnNode parent = map[node.Parent];
			Assert.Equal(parent.Depth + 1, node.Depth);
			Assert.Equal(i, parent.Children[node.Branch]);
			Assert.True(node.Parent < i, "a depth-first walk numbers a parent before its children");
		}

		// Every condition is listed once, on the node it guards.
		int listed = map.Nodes.Sum(n => n.Conditions.Count + n.Executing.Count);
		Assert.Equal(map.Conditions.Count, listed);
		foreach (HtnConditionInfo condition in map.Conditions)
		{
			bool onNode = condition.Executing
				? map[condition.Node].Executing.Contains(condition.Index)
				: map[condition.Node].Conditions.Contains(condition.Index);
			Assert.True(onNode, condition.Name);
		}
	}

	[Fact]
	public void TheServersMaps_AndASpectatorsFreshBuilds_HaveOneSignature()
	{
		// The whole wire format rests on this: a task index means the same task on both
		// ends because both ends number the same build of the same domain.
		Assert.Equal(new GroundPlanning(GroundPlanTraits.Default).Map.Signature, HtnDomains.Ground.Signature);
		Assert.Equal(new UnitPlanning(UnitPlanTraits.Default).Map.Signature, HtnDomains.Unit.Signature);
		Assert.Equal(new CommanderPlanning(CommanderTraits.Default).Map.Signature, HtnDomains.Squad.Signature);

		Assert.Equal(3, new[] { HtnDomains.Ground.Signature, HtnDomains.Unit.Signature, HtnDomains.Squad.Signature }
			.Distinct().Count());
	}

	[Fact]
	public void ADifferentBuildOfADomain_HasADifferentSignature()
	{
		// Without executing conditions (§3.4, P3's control) the tree has the same tasks
		// and fewer conditions: a spectator on such a build would mis-read every
		// condition bit, and the signature is what says so.
		var without = HtnDomainMap.Build(HtnDomainKind.Ground,
			GroundDomain.Build(new PooledHtnFactory(), executingConditions: false));

		Assert.Equal(HtnDomains.Ground.Count, without.Count);
		Assert.NotEqual(HtnDomains.Ground.Signature, without.Signature);
	}

	[Fact]
	public void PathOf_NamesTheWayDownToATask()
	{
		HtnDomainMap map = HtnDomains.Ground;
		int engage = IndexOfPrimitive(map, "Engage");

		Assert.Equal("ground › attack › engage › Engage", map.PathOf(engage));
		Assert.True(map.IsAncestorOf(0, engage));
		Assert.False(map.IsAncestorOf(engage, 0));
	}

	// ---- traces ------------------------------------------------------------

	[Fact]
	public void ABotWithNothingKnown_IsRunningStandoff_WithNothingPlannedBehindIt()
	{
		var planning = new GroundPlanning(GroundPlanTraits.Default);
		GroundContext c = planning.CreateContext();
		c.Tick = 100;
		planning.Tick(c);

		HtnTrace trace = Capture(planning.Map, c);

		Assert.Equal(HtnDomainKind.Ground, trace.Domain);
		Assert.Equal("Standoff", planning.Map.NameOf(trace.Current));
		Assert.Empty(trace.Planned);
		Assert.Empty(trace.Paused);
		Assert.Equal(HtnTaskState.Running, trace.StateOf(trace.Current));
		Assert.Equal((int)GroundFact.Count, trace.Facts.Count);
		Assert.Equal(planning.Map.Conditions.Count, trace.Holds.Count);

		// Recon is the fifth top-level task: the record says so, and the path runs through it.
		Assert.Equal(new[] { 4, 1 }, trace.Traversal);
		bool[] path = trace.PathMask(planning.Map);
		Assert.True(path[0]);
		Assert.True(path[trace.Current]);
		Assert.True(path[Child(planning.Map, 0, "recon")]);
		Assert.False(path[Child(planning.Map, 0, "attack")]);
	}

	[Fact]
	public void ALockerRun_IsOneTaskRunning_AndTwoPlannedBehindIt()
	{
		var planning = new GroundPlanning(GroundPlanTraits.Default);
		GroundContext c = planning.CreateContext();
		c.Tick = 100;
		c.HasLocker = true;
		c.LockerPoint = new Vector3(8f, 0.5f, 11f);
		c.Position = new Vector3(0f, 0.5f, -40f);
		c.Sense(GroundFact.Role, (byte)GroundRole.LockerRunner);
		c.Sense(GroundFact.Contact, (byte)ContactLevel.Visible);
		c.Sense(GroundFact.ThreatKind, (byte)Threat.Armour);
		planning.Tick(c);

		HtnTrace trace = Capture(planning.Map, c);
		HtnDomainMap map = planning.Map;

		Assert.Equal("PathToLocker", map.NameOf(trace.Current));
		Assert.Equal(new[] { "UseLocker", "EngageArmour" }, trace.Planned.Select(map.NameOf));
		Assert.Equal(HtnTaskState.Planned, trace.StateOf(trace.Planned[1]));
		Assert.Equal(2, trace.OrderOf(trace.Planned[1]));

		// EngageArmour was planned through UseLocker's prediction (§3.4, P4): its own
		// condition does not hold yet, and the trace says so.
		int armed = map[trace.Planned[1]].Conditions.Single();
		Assert.Equal("Armed=Explosive", map.ConditionName(armed));
		Assert.False(trace.Held(armed));
		Assert.Contains("Armed:=Explosive", map[trace.Planned[0]].Effects);
	}

	[Fact]
	public void AStagingSquad_HasItsStrikeSetAsideBehindThePause()
	{
		var planning = new CommanderPlanning(CommanderTraits.Default);
		CommandContext c = planning.CreateContext();
		c.Tick = 1000;
		c.Sense(CommandFact.Task, (byte)CommandTask.Attack);
		planning.Planner.Tick(planning.Domain, c);

		HtnTrace trace = Capture(planning.Map, c);

		Assert.Equal("Stage", planning.Map.NameOf(trace.Current));
		Assert.Empty(trace.Planned);
		Assert.Equal(new[] { "Strike" }, trace.Paused.Select(planning.Map.NameOf));
		Assert.Equal(HtnTaskState.Paused, trace.StateOf(trace.Paused[0]));

		// Assembled: the remainder is planned, and runs.
		c.Assembled = true;
		c.Tick += 30;
		planning.Planner.Tick(planning.Domain, c);
		trace = Capture(planning.Map, c);

		Assert.Equal("Strike", planning.Map.NameOf(trace.Current));
		Assert.Empty(trace.Paused);
	}

	[Fact]
	public void ConditionTruth_IsReadFromTheWorldStateTheAgentIsExecutingAgainst()
	{
		var planning = new UnitPlanning(UnitPlanTraits.Default);
		UnitContext c = planning.CreateContext(Rifleman);
		c.Sense(UnitFact.Order, (byte)OrderKind.Attack);
		c.Sense(UnitFact.Contact, (byte)UnitContact.Own);
		c.Tick = 100;
		planning.Tick(c);

		HtnDomainMap map = planning.Map;
		HtnTrace trace = Capture(map, c);

		Assert.Equal("EngageFocus", map.NameOf(trace.Current));
		foreach (int condition in map[trace.Current].Executing)
		{
			Assert.True(trace.Held(condition), map.ConditionName(condition));
		}

		int obey = Child(map, 0, "obey");
		Assert.False(trace.Held(map[obey].Conditions.Single()));
	}

	// ---- history -----------------------------------------------------------

	[Fact]
	public void History_SaysWhatReplacedWhat_AndWhichConditionEndedATask()
	{
		var planning = new GroundPlanning(GroundPlanTraits.Default);
		GroundContext c = planning.CreateContext();
		HtnDomainMap map = planning.Map;

		c.Tick = 100;
		c.Sense(GroundFact.Contact, (byte)ContactLevel.Visible);
		planning.Tick(c);
		Assert.Equal(GroundGoal.Engage, c.Intent.Goal);

		// Retreat pre-empts the fight (§3.4, P2).
		c.Tick = 110;
		c.Sense(GroundFact.InsideDefences, true);
		planning.Tick(c);
		Assert.Equal(GroundGoal.LeaveDefences, c.Intent.Goal);

		// Out of the ring: the executing condition ends the task (P3).
		c.Tick = 120;
		c.Sense(GroundFact.InsideDefences, false);
		planning.Tick(c);

		HtnTrace trace = Capture(map, c);
		var events = trace.History.Select(e => (e.Kind, Node: map.NameOf(e.Node))).ToList();

		Assert.Contains((HtnEventKind.NewPlan, "Engage"), events);
		Assert.Contains((HtnEventKind.ReplacePlan, "LeaveDefences"), events);
		Assert.Contains((HtnEventKind.Stopped, "Engage"), events);

		HtnEvent replaced = trace.History.First(e => e.Kind == HtnEventKind.ReplacePlan);
		Assert.Equal("Engage", map.NameOf(replaced.Other));
		Assert.Equal(110u, replaced.Tick);

		HtnEvent ended = trace.History.First(e => e.Kind == HtnEventKind.ConditionFailed);
		Assert.Equal("LeaveDefences", map.NameOf(ended.Node));
		Assert.Equal("while InsideDefences=True", map.ConditionName(ended.Other));
		Assert.Equal(120u, ended.Tick);

		Assert.Equal("LeaveDefences ended: while InsideDefences=True stopped holding", HtnText.Describe(map, ended));
	}

	[Fact]
	public void History_KeepsTheNewest_InOrder()
	{
		var history = new HtnHistory();
		for (int i = 0; i < HtnHistory.Capacity + 5; i++)
		{
			history.Record(HtnEventKind.Started, i, -1);
		}

		Assert.Equal(HtnHistory.Capacity, history.Count);
		Assert.Equal(HtnHistory.Capacity + 5, history.Total);
		Assert.Equal(5, history[0].Node);
		Assert.Equal(HtnHistory.Capacity + 4, history[HtnHistory.Capacity - 1].Node);

		history.Record(HtnEventKind.Reset, -1, 300);
		Assert.Equal(HtnHistory.None, history[HtnHistory.Capacity - 1].Node);
		Assert.Equal(HtnHistory.None, history[HtnHistory.Capacity - 1].Other);
	}

	[Fact]
	public void AResetByTheEngine_IsInTheHistory()
	{
		var planning = new GroundPlanning(GroundPlanTraits.Default);
		GroundContext c = planning.CreateContext();
		c.Tick = 5;
		planning.Tick(c);
		planning.Reset(c);

		Assert.Equal(HtnEventKind.Reset, c.History[c.History.Count - 1].Kind);
	}

	[Fact]
	public void PlanningWithHistory_AllocatesNothingOnTheTick()
	{
		// The history hangs off every planner the game makes, so it is under the same
		// rule the planner is (docs/HTN_BOTS.md §4.2 rule 3): pre-emptions, executing
		// conditions failing and plans succeeding, a thousand times, for nothing.
		var planning = new GroundPlanning(GroundPlanTraits.Default);
		GroundContext[] bots = Enumerable.Range(0, 16).Select(_ => planning.CreateContext()).ToArray();

		void Run(uint from, uint ticks)
		{
			for (uint t = from; t < from + ticks; t++)
			{
				for (int i = 0; i < bots.Length; i++)
				{
					GroundContext c = bots[i];
					c.Tick = t;
					uint phase = (t + (uint)i * 7) / 10 % 4;
					c.Sense(GroundFact.Contact, (byte)(phase == 1 ? ContactLevel.Visible : ContactLevel.None));
					c.Sense(GroundFact.InsideDefences, phase == 2);
					c.Sense(GroundFact.MagazineLow, phase == 3);
					planning.Tick(c);
				}
			}
		}

		Run(0, 2000);
		int before = bots.Sum(b => b.History.Total);
		long bytes = GC.GetAllocatedBytesForCurrentThread();
		Run(2000, 3000);
		bytes = GC.GetAllocatedBytesForCurrentThread() - bytes;

		Assert.True(bots.Sum(b => b.History.Total) - before > 1000, "the run recorded nothing");
		Assert.Equal(0, bytes);
	}

	// ---- text --------------------------------------------------------------

	[Fact]
	public void TheTree_MarksWhatIsRunning_WhatIsPlanned_AndWhatHolds()
	{
		var planning = new GroundPlanning(GroundPlanTraits.Default);
		GroundContext c = planning.CreateContext();
		c.Tick = 100;
		c.HasLocker = true;
		c.Position = new Vector3(0f, 0.5f, -40f);
		c.LockerPoint = new Vector3(8f, 0.5f, 11f);
		c.Sense(GroundFact.Role, (byte)GroundRole.LockerRunner);
		c.Sense(GroundFact.Contact, (byte)ContactLevel.Visible);
		c.Sense(GroundFact.ThreatKind, (byte)Threat.Armour);
		planning.Tick(c);
		c.Tick = 220;

		HtnTrace trace = Capture(planning.Map, c);
		string tree = HtnText.Tree(planning.Map, trace, HtnTextStyle.Plain, HtnTreeOptions.Full(c.Tick));
		string[] lines = tree.Split('\n');

		Assert.Equal("ground", lines[0]);
		Assert.Contains(lines, l => l.Contains(">> PathToLocker  running 2.0s"));
		Assert.Contains(lines, l => l.Contains("#1 UseLocker  planned") && l.Contains("=> Armed:=Explosive"));
		Assert.Contains(lines, l => l.Contains("#2 EngageArmour  planned") && l.Contains("[-Armed=Explosive]"));
		Assert.Contains(lines, l => l.TrimStart('│', ' ', '├', '└', '─').StartsWith("rearm for armour")
			&& l.Contains("+Role=LockerRunner") && l.Contains("+ThreatKind=Armour"));
		Assert.Contains(lines, l => l.Contains("{+while Role=LockerRunner}"));
		Assert.Equal(planning.Map.Count, lines.Length);
	}

	[Fact]
	public void TheCollapsedTree_FoldsEverythingOffThePath()
	{
		var planning = new GroundPlanning(GroundPlanTraits.Default);
		GroundContext c = planning.CreateContext();
		planning.Tick(c);
		HtnTrace trace = Capture(planning.Map, c);

		var options = new HtnTreeOptions { Collapse = true };
		string[] lines = HtnText.Tree(planning.Map, trace, HtnTextStyle.Plain, options).Split('\n');

		// The root, five top-level tasks, and recon's two methods with Standoff under one.
		Assert.Equal(1 + 5 + 2 + 1, lines.Length);
		Assert.Contains(lines, l => l.Contains("attack  (+4)"));
		Assert.Contains(lines, l => l.Contains(">> Standoff"));
	}

	[Fact]
	public void TheRichStyle_EscapesItsOwnBrackets()
	{
		var planning = new GroundPlanning(GroundPlanTraits.Default);
		GroundContext c = planning.CreateContext();
		planning.Tick(c);
		HtnTrace trace = Capture(planning.Map, c);

		string rich = HtnText.Tree(planning.Map, trace, HtnTextStyle.Rich, HtnTreeOptions.Full(0));

		Assert.Contains("[lb]", rich);
		Assert.Contains("[rb]", rich);
		Assert.Contains("[color=#7cfc6a]", rich);
		Assert.DoesNotContain("[-InsideDefences", rich);

		// Stripping the markup gives back the plain text, character for character.
		string stripped = System.Text.RegularExpressions.Regex.Replace(rich, @"\[/?(b|color[^\]]*)\]", string.Empty)
			.Replace("[lb]", "[").Replace("[rb]", "]");
		Assert.Equal(HtnText.Tree(planning.Map, trace, HtnTextStyle.Plain, HtnTreeOptions.Full(0)), stripped);
	}

	[Fact]
	public void PlanAndFacts_ReadAsOneLineAndATable()
	{
		var planning = new CommanderPlanning(CommanderTraits.Default);
		CommandContext c = planning.CreateContext();
		c.Tick = 1000;
		c.Sense(CommandFact.Task, (byte)CommandTask.Attack);
		planning.Planner.Tick(planning.Domain, c);
		HtnTrace trace = Capture(planning.Map, c);

		Assert.Equal("running Stage 0.0s  ‖ after the pause Strike   MTR 2.0",
			HtnText.Plan(planning.Map, trace, HtnTextStyle.Plain, 1000));

		string facts = HtnText.Facts(HtnSchema.Squad, trace, HtnTextStyle.Plain, perLine: 2);
		Assert.Contains("Task=Attack", facts);
		Assert.Contains("FallingBack=False", facts);
		Assert.Equal(2, facts.Split('\n').Length);
	}

	[Theory]
	[InlineData(HtnDomainKind.Ground, (int)GroundFact.Count)]
	[InlineData(HtnDomainKind.Unit, (int)UnitFact.Count)]
	[InlineData(HtnDomainKind.Squad, (int)CommandFact.Count)]
	public void EachSchema_NamesEveryFact(HtnDomainKind kind, int facts)
	{
		HtnSchema schema = HtnSchema.For(kind);

		Assert.Equal(facts, schema.FactCount);
		Assert.Equal("Contact", HtnSchema.Ground.FactName((int)GroundFact.Contact));
		Assert.Equal("Visible", HtnSchema.Ground.FactValue((int)GroundFact.Contact, (byte)ContactLevel.Visible));
		Assert.Equal("Patrol", HtnSchema.Unit.FactValue((int)UnitFact.Order, (byte)OrderKind.Patrol));
		Assert.Equal("True", HtnSchema.Squad.FactValue((int)CommandFact.Depleted, 1));
		Assert.Equal("200", HtnSchema.Ground.FactValue((int)GroundFact.Contact, 200));
		Assert.Equal("Strike", HtnSchema.Squad.GoalName((byte)CommandGoal.Strike));
	}

	// ---- graph -------------------------------------------------------------

	[Fact]
	public void TheGraph_PutsEveryLeafOnItsOwnRow_AndEveryParentAmongItsChildren()
	{
		HtnDomainMap map = HtnDomains.Unit;
		HtnGraphLayout layout = HtnGraphLayout.Compute(map, null, collapse: false);

		int leaves = map.Nodes.Count(n => n.Children.Count == 0);
		Assert.Equal(leaves, layout.Rows);
		Assert.Equal(map.Nodes.Max(n => n.Depth) + 1, layout.Columns);
		Assert.All(layout.Visible, Assert.True);

		foreach (HtnNode node in map.Nodes.Where(n => n.Children.Count > 0))
		{
			float first = layout.Row[node.Children[0]];
			float last = layout.Row[node.Children[^1]];
			Assert.Equal((first + last) * 0.5f, layout.Row[node.Index]);
			Assert.Equal(node.Depth, layout.Column[node.Index]);
		}
	}

	[Fact]
	public void TheCollapsedGraph_HidesWhatTheCollapsedTreeFolds()
	{
		var planning = new UnitPlanning(UnitPlanTraits.Default);
		UnitContext c = planning.CreateContext(Rifleman);
		c.Sense(UnitFact.Order, (byte)OrderKind.Patrol);
		planning.Tick(c);
		HtnTrace trace = Capture(planning.Map, c);

		HtnGraphLayout layout = HtnGraphLayout.Compute(planning.Map, trace, collapse: true);
		int attack = Child(planning.Map, 0, "attack");
		int patrol = trace.Current;

		Assert.True(layout.Visible[attack]);
		Assert.False(layout.Visible[planning.Map[attack].Children[0]]);
		Assert.True(layout.Visible[patrol]);
		Assert.True(layout.Rows < HtnGraphLayout.Compute(planning.Map, trace, collapse: false).Rows);
	}

	// ---- helpers -----------------------------------------------------------

	private static HtnTrace Capture(HtnDomainMap map, FluidHTN.IContext context)
	{
		var trace = new HtnTrace();
		HtnHistory history = context switch
		{
			GroundContext g => g.History,
			UnitContext u => u.History,
			CommandContext s => s.History,
			_ => null,
		};

		HtnTracer.Capture(map, context, history, trace);
		return trace;
	}

	private static int IndexOfPrimitive(HtnDomainMap map, string name) =>
		map.Nodes.First(n => n.Kind == HtnNodeKind.Primitive && n.Name == name).Index;

	private static int Child(HtnDomainMap map, int parent, string name) =>
		map[parent].Children.First(c => map[c].Name == name);
}
