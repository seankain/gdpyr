using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Gdpyr.Sim;
using Gdpyr.Sim.Htn;
using Godot;
using Xunit;

namespace Gdpyr.Tests;

/// <summary>
/// Forward deployment (docs/CONTROL_POINTS.md §2, §3): which node a barracks puts its
/// units out at, the slots round a node, the capture's record of the ground force on a
/// pad, and the computer strategist's choice of node and where its idle squads wait.
/// </summary>
public class ForwardDeployTests
{
	private static readonly Vector3 NodeA = new(0f, 0f, 0f);
	private static readonly Vector3 NodeB = new(40f, 0f, 0f);

	private static DeploySite[] Sites(NodeHolder a, NodeHolder b, bool deniedA = false, bool deniedB = false) => new[]
	{
		new DeploySite(NodeA, 10f, a, deniedA),
		new DeploySite(NodeB, 12f, b, deniedB),
	};

	// ---- the rule -----------------------------------------------------------

	[Fact]
	public void ARallyPointOnAHeldNode_DeploysThere()
	{
		DeploySite[] sites = Sites(NodeHolder.Strategist, NodeHolder.Strategist);
		Assert.Equal(1, ForwardDeploy.Choose(NodeB + new Vector3(5f, 0f, 3f), sites));
		Assert.Equal(0, ForwardDeploy.Choose(NodeA, sites));
	}

	[Theory]
	[InlineData(NodeHolder.Neutral)]
	[InlineData(NodeHolder.GroundForce)]
	public void ANodeTheStrategistDoesNotHold_DeploysNothing(NodeHolder holder)
	{
		Assert.Equal(ForwardDeploy.Door, ForwardDeploy.Choose(NodeA, Sites(holder, NodeHolder.Strategist)));
	}

	[Fact]
	public void AHeldNodeWithTheGroundForceOnIt_DeploysNothing()
	{
		DeploySite[] sites = Sites(NodeHolder.Strategist, NodeHolder.Strategist, deniedA: true);
		Assert.False(sites[0].Deployable);
		Assert.Equal(ForwardDeploy.Door, ForwardDeploy.Choose(NodeA, sites));
	}

	[Fact]
	public void ARallyPointOffEveryPad_IsTheDoor()
	{
		DeploySite[] sites = Sites(NodeHolder.Strategist, NodeHolder.Strategist);
		Assert.Equal(ForwardDeploy.Door, ForwardDeploy.Choose(new Vector3(20f, 0f, 0f), sites));
		Assert.Equal(ForwardDeploy.Door, ForwardDeploy.Choose(NodeA, ReadOnlySpan<DeploySite>.Empty));
	}

	[Fact]
	public void ARallyPointHighAboveAPad_IsNotOnIt()
	{
		DeploySite[] sites = Sites(NodeHolder.Strategist, NodeHolder.Strategist);
		Assert.Equal(ForwardDeploy.Door,
			ForwardDeploy.Choose(NodeA + (Vector3.Up * (CapturePad.VerticalToleranceMeters + 1f)), sites));
	}

	[Fact]
	public void OverlappingPads_TheNearestMiddleWins_AndATieTheLowerIndex()
	{
		var sites = new[]
		{
			new DeploySite(new Vector3(0f, 0f, 0f), 10f, NodeHolder.Strategist, false),
			new DeploySite(new Vector3(8f, 0f, 0f), 10f, NodeHolder.Strategist, false),
		};

		Assert.Equal(1, ForwardDeploy.Choose(new Vector3(6f, 0f, 0f), sites));
		Assert.Equal(0, ForwardDeploy.Choose(new Vector3(4f, 0f, 0f), sites));

		// Denied, the nearer one gives way to the other pad the point is still on.
		sites[1] = new DeploySite(new Vector3(8f, 0f, 0f), 10f, NodeHolder.Strategist, true);
		Assert.Equal(0, ForwardDeploy.Choose(new Vector3(6f, 0f, 0f), sites));
	}

	[Fact]
	public void PadUnder_NamesTheNodeWhoeverHoldsIt()
	{
		DeploySite[] sites = Sites(NodeHolder.Neutral, NodeHolder.GroundForce);
		Assert.Equal(0, ForwardDeploy.PadUnder(NodeA, sites));
		Assert.Equal(1, ForwardDeploy.PadUnder(NodeB, sites));
		Assert.Equal(ForwardDeploy.Door, ForwardDeploy.PadUnder(new Vector3(20f, 0f, 0f), sites));
	}

	// ---- slots ---------------------------------------------------------------

	[Fact]
	public void Slots_AreOnThePad_ApartFromEachOther_AndClearOfTheMiddle()
	{
		Vector3 centre = new(10f, 0.5f, -20f);
		int count = ForwardDeploy.SlotCount;
		Assert.True(count >= 24, $"only {count} slots");

		var slots = new List<Vector3>();
		for (int i = 0; i < count; i++)
		{
			Vector3 slot = ForwardDeploy.Slot(centre, centre + Vector3.Forward, i);
			float flat = new Vector2(slot.X - centre.X, slot.Z - centre.Z).Length();
			Assert.InRange(flat, ForwardDeploy.FirstRingMeters - 0.01f, 10f);
			Assert.Equal(centre.Y, slot.Y);
			slots.Add(slot);
		}

		for (int i = 0; i < slots.Count; i++)
		{
			for (int j = i + 1; j < slots.Count; j++)
			{
				Assert.True(slots[i].DistanceTo(slots[j]) >= ForwardDeploy.SpacingMeters * 0.95f,
					$"slots {i} and {j} are {slots[i].DistanceTo(slots[j]):0.00} m apart");
			}
		}
	}

	[Fact]
	public void TheFirstSlot_FacesTheRallyPoint_AndTheOrdinalWraps()
	{
		Vector3 centre = Vector3.Zero;
		Vector3 towards = new(30f, 0f, 0f);
		Vector3 first = ForwardDeploy.Slot(centre, towards, 0);
		Assert.Equal(ForwardDeploy.FirstRingMeters, first.X, 3);
		Assert.Equal(0f, first.Z, 3);

		Assert.Equal(first, ForwardDeploy.Slot(centre, towards, ForwardDeploy.SlotCount));
		Assert.Equal(first, ForwardDeploy.Slot(centre, towards, -3));

		// Rallying on the middle itself has no facing, and still gives a slot off it.
		Assert.True(ForwardDeploy.Slot(centre, centre, 0).Length() >= ForwardDeploy.FirstRingMeters - 0.01f);
	}

	// ---- the capture's record of who is on the pad --------------------------

	[Fact]
	public void GroundPresent_IsTheLastScan_WhoeverHoldsTheNode()
	{
		var rules = new CaptureRules(captureTicks: 60, intervalTicks: 15, incomeIntervalTicks: 300, incomePoints: 25);
		var capture = new CaptureState();
		uint tick = 15;

		for (int i = 0; i < 4; i++, tick += 15)
		{
			capture.Tick(tick, groundOccupants: 0, strategistOccupants: 1, rules);
		}

		Assert.True(capture.IsHeldBy(NodeHolder.Strategist));
		Assert.False(capture.GroundPresent);

		capture.Tick(tick, groundOccupants: 1, strategistOccupants: 0, rules);
		Assert.True(capture.GroundPresent);
		Assert.False(capture.Contested);
		tick += 15;

		capture.Tick(tick, groundOccupants: 1, strategistOccupants: 2, rules);
		Assert.True(capture.GroundPresent);
		Assert.True(capture.Contested);
		tick += 15;

		capture.Tick(tick, groundOccupants: 0, strategistOccupants: 2, rules);
		Assert.False(capture.GroundPresent);

		capture.Tick(tick + 15, groundOccupants: 1, strategistOccupants: 0, rules);
		capture.Reset();
		Assert.False(capture.GroundPresent);
	}

	// ---- the computer strategist (§3) ----------------------------------------

	private static readonly Vector3 Home = Vector3.Zero;
	private static readonly Vector3 Threat = new(0f, 0f, 200f);
	private static readonly Vector3 NearNode = new(0f, 0f, 50f);
	private static readonly Vector3 FarNode = new(20f, 0f, 120f);
	private static readonly Vector3 BehindNode = new(0f, 0f, -40f);

	private const int NearZone = 0;
	private const int FarZone = 1;
	private const int BehindZone = 2;

	private sealed class Rig
	{
		public readonly Commander Commander;
		public readonly ZoneBoard Zones = new();
		public readonly ContactMemory Contacts = new(16, observers: 1, liveWindowTicks: 8);
		public readonly List<CommandUnit> Units = new();
		public uint Tick = 1000;
		private ushort _nextId = 1;

		public Rig()
		{
			Commander = new Commander(new CommanderPlanning(CommanderTraits.Default, new PooledHtnFactory()),
				new ThreatWeights(50f, 400f, 400f, 100f, 1f));

			Zones.Add(ZoneKind.ResourceNode, 0, NearNode, 10f);
			Zones.Add(ZoneKind.ResourceNode, 1, FarNode, 10f);
			Zones.Add(ZoneKind.ResourceNode, 2, BehindNode, 10f);
			Zones.Add(ZoneKind.Barracks, 0, Home, 20f);
			Zones.Add(ZoneKind.GroundSpawn, 0, Threat, 20f);
			for (int z = 0; z < 4; z++)
			{
				Zones.SetHolder(z, NodeHolder.Strategist, contested: false);
			}

			Zones.SetHolder(4, NodeHolder.GroundForce, contested: false);
		}

		public CommanderWorld World(Vector3? muster = null) => new()
		{
			Tick = Tick,
			Home = Home,
			Threat = Threat,
			Muster = muster ?? default,
			HasMuster = muster.HasValue,
		};

		public void AddRiflemen(int count, Vector3 at)
		{
			for (int i = 0; i < count; i++)
			{
				Units.Add(new CommandUnit { Id = _nextId++, Position = at + new Vector3(i, 0f, 0f), Strength = 50f });
			}
		}

		public void Decide(Vector3? muster = null)
		{
			for (int z = 0; z < Zones.Count; z++)
			{
				Zones.MarkObserved(z, Tick);
			}

			Commander.Decide(World(muster), CollectionsMarshal.AsSpan(Units), Zones, Contacts);
			Tick += 30;
		}

		public int Muster(int current = -1) => Commander.ChooseMuster(World(), Zones, Contacts, current);

		public void See(Vector3 at, int peer = 7) => Contacts.Observe(Tick, 0, new KnownContact
		{
			OwnerId = OwnerId.ForPeer(peer),
			Kind = ContactKind.Player,
			Position = at,
			HealthFraction = 1f,
		});
	}

	[Fact]
	public void WhileTheGarrisonIsShort_ProductionStaysAtTheDoor()
	{
		var rig = new Rig();
		Assert.True(rig.Commander.GarrisonWantsProduction);
		Assert.Equal(-1, rig.Muster());

		rig.AddRiflemen(CommanderTraits.Default.GarrisonUnits - 1, Home);
		rig.Decide();
		Assert.True(rig.Commander.GarrisonWantsProduction);
		Assert.Equal(-1, rig.Muster());
	}

	[Fact]
	public void WithTheGarrisonFull_ProductionGoesToTheHeldNodeNearestTheThreat()
	{
		var rig = new Rig();
		rig.AddRiflemen(CommanderTraits.Default.GarrisonUnits, Home);
		rig.Decide();

		Assert.False(rig.Commander.GarrisonWantsProduction);
		Assert.Equal(FarZone, rig.Muster());
	}

	[Fact]
	public void ANodeContested_OrNotHeld_OrWithAnEnemyNear_IsPassedOver()
	{
		var rig = new Rig();
		rig.AddRiflemen(CommanderTraits.Default.GarrisonUnits, Home);
		rig.Decide();

		rig.Zones.SetHolder(FarZone, NodeHolder.Strategist, contested: true);
		Assert.Equal(NearZone, rig.Muster());

		rig.Zones.SetHolder(FarZone, NodeHolder.GroundForce, contested: false);
		Assert.Equal(NearZone, rig.Muster());

		rig.Zones.SetHolder(FarZone, NodeHolder.Strategist, contested: false);
		rig.See(FarNode + new Vector3(CommanderTraits.Default.ThreatMeters - 5f, 0f, 0f));
		Assert.Equal(NearZone, rig.Muster());

		rig.See(NearNode + new Vector3(0f, 0f, 5f), peer: 8);
		Assert.Equal(-1, rig.Muster());
	}

	[Fact]
	public void ANodeNoNearerTheThreatThanHome_IsNeverTheMuster()
	{
		var rig = new Rig();
		rig.AddRiflemen(CommanderTraits.Default.GarrisonUnits, Home);
		rig.Decide();

		rig.Zones.SetHolder(NearZone, NodeHolder.Neutral, contested: false);
		rig.Zones.SetHolder(FarZone, NodeHolder.Neutral, contested: false);
		Assert.Equal(-1, rig.Muster());
	}

	[Fact]
	public void TheMusterChosenLast_IsKeptWhileItStillQualifies()
	{
		var rig = new Rig();
		rig.AddRiflemen(CommanderTraits.Default.GarrisonUnits, Home);
		rig.Decide();

		Assert.Equal(NearZone, rig.Muster(current: 0));
		Assert.Equal(FarZone, rig.Muster(current: 2));

		rig.Zones.SetHolder(NearZone, NodeHolder.Strategist, contested: true);
		Assert.Equal(FarZone, rig.Muster(current: 0));
	}

	[Fact]
	public void SquadsWithNothingToDo_WaitAtTheMuster_RatherThanInFrontOfHome()
	{
		var rig = new Rig();
		rig.AddRiflemen(CommanderTraits.Default.GarrisonUnits + 2, Home);
		rig.Decide();

		int forming = -1;
		for (int s = 0; s < rig.Commander.Capacity; s++)
		{
			CommandSquad squad = rig.Commander.SquadAt(s);
			if (squad.Active && squad.Role == CommandRole.Assault && !squad.Ready)
			{
				forming = s;
			}
		}

		Assert.True(forming > 0);
		CommandIntent before = rig.Commander.IntentOf(forming);
		Assert.Equal(CommandGoal.Hold, before.Goal);
		Assert.Equal(new Vector3(0f, 0f, CommanderTraits.Default.ReserveOffsetMeters), before.Point);

		rig.Decide(muster: FarNode);
		CommandIntent after = rig.Commander.IntentOf(forming);
		Assert.Equal(CommandGoal.Hold, after.Goal);
		Assert.Equal(OrderKind.Defend, after.Order);
		Assert.Equal(FarNode, after.Point);

		// The garrison still defends home.
		Assert.Equal(Home, rig.Commander.IntentOf(Commander.GarrisonSlot).Point);
	}
}
