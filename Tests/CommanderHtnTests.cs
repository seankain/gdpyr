using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using FluidHTN;
using FluidHTN.Factory;
using Gdpyr.Sim;
using Gdpyr.Sim.Htn;
using Godot;
using Xunit;

namespace Gdpyr.Tests;

/// <summary>
/// The strategist's commander (docs/HTN_BOTS.md §5.3, H4): the squad domain's rows,
/// each as a fact vector and the task it must produce; the assignment above it —
/// garrison, scout, squads formed from production, a threatened zone given a squad
/// strong enough for it, an idle squad given a target it is strong enough for; stage
/// then strike as a partial plan; retreat pre-empting; the human-claim rule (D1);
/// ghosts never named (D4); supply trucks stationed, and a depleted squad healing at
/// the nearest supply source when healing can bring it back (H5, D2); every operator
/// ending when its premise goes (§3.4, P3); determinism (P7) and planning without
/// allocating (P8).
/// </summary>
public class CommanderHtnTests
{
	private static readonly Vector3 Home = Vector3.Zero;
	private static readonly Vector3 Threat = new(0f, 0f, 200f);

	private static readonly Vector3 NearNode = new(0f, 0f, 50f);
	private static readonly Vector3 FarNode = new(80f, 0f, 120f);

	private const int NearZone = 0;
	private const int FarZone = 1;
	private const int BarracksZone = 2;
	private const int SpawnZone = 3;

	/// <summary>Costs as in the catalog: a rifleman 50, a technical 150, a tank 400; a ground player two riflemen.</summary>
	private static readonly ThreatWeights Weights = new(50f, 400f, 400f, 100f, 1f);

	private const float Rifleman = 50f;
	private const float Technical = 150f;
	private const float Tank = 400f;
	private const float Truck = 120f;
	private const float Gunner = 90f;
	private const float Sniper = 60f;

	private sealed class Rig
	{
		public readonly CommanderPlanning Planning;
		public readonly Commander Commander;
		public readonly ZoneBoard Zones = new();
		public readonly ContactMemory Contacts = new(16, observers: 1, liveWindowTicks: 8);
		public readonly List<CommandUnit> Units = new();
		public uint Tick = 1000;
		public bool ObserveZones = true;
		private ushort _nextId = 1;

		public Rig(IFactory factory = null, CommanderTraits? traits = null)
		{
			Planning = new CommanderPlanning(traits ?? CommanderTraits.Default, factory ?? new PooledHtnFactory());
			Commander = new Commander(Planning, Weights);

			Zones.Add(ZoneKind.ResourceNode, 0, NearNode, 10f);
			Zones.Add(ZoneKind.ResourceNode, 1, FarNode, 10f);
			Zones.Add(ZoneKind.Barracks, 0, Home, 20f);
			Zones.Add(ZoneKind.GroundSpawn, 0, Threat, 20f);
			Zones.SetHolder(NearZone, NodeHolder.Strategist, contested: false);
			Zones.SetHolder(FarZone, NodeHolder.Strategist, contested: false);
			Zones.SetHolder(BarracksZone, NodeHolder.Strategist, contested: false);
			Zones.SetHolder(SpawnZone, NodeHolder.GroundForce, contested: false);
		}

		/// <summary>A unit; <paramref name="fullStrength"/> 0 is one resupply cannot bring back, as the tests before H5 had.</summary>
		public ushort Add(Vector3 at, float strength = Rifleman, bool armour = false, bool scout = false,
			bool supply = false, float fullStrength = 0f, bool gunner = false, bool marksman = false)
		{
			ushort id = _nextId++;
			Units.Add(new CommandUnit
			{
				Id = id, Position = at, Strength = strength, Armour = armour, Scout = scout, Supply = supply,
				FullStrength = fullStrength, Gunner = gunner, Marksman = marksman,
			});
			return id;
		}

		public ushort AddTruck(Vector3 at) => Add(at, Truck, supply: true, fullStrength: Truck);

		public ushort AddGunner(Vector3 at) => Add(at, Gunner, gunner: true);

		public ushort AddSniper(Vector3 at) => Add(at, Sniper, marksman: true);

		/// <summary>Every unit worth its full cost at full health: squads resupply can bring back (D2).</summary>
		public void Heals()
		{
			for (int i = 0; i < Units.Count; i++)
			{
				Units[i] = Units[i] with { FullStrength = Units[i].Supply ? Truck : Rifleman };
			}
		}

		/// <summary>Riflemen standing in a row at home.</summary>
		public void AddRiflemen(int count)
		{
			for (int i = 0; i < count; i++)
			{
				Add(new Vector3(Units.Count, 0f, 0f));
			}
		}

		public void Set(ushort id, Func<CommandUnit, CommandUnit> change)
		{
			int i = Units.FindIndex(u => u.Id == id);
			Units[i] = change(Units[i]);
		}

		public void Kill(ushort id) => Units.RemoveAll(u => u.Id == id);

		public void Move(int squad, Vector3 to)
		{
			foreach (ushort id in Members(squad))
			{
				Set(id, u => u with { Position = to });
			}
		}

		public void See(int peer, Vector3 at, float health = 1f) => Contacts.Observe(Tick, 0, new KnownContact
		{
			OwnerId = OwnerId.ForPeer(peer),
			Kind = ContactKind.Player,
			Position = at,
			HealthFraction = health,
		});

		/// <summary>Decides on <see cref="Tick"/>, then moves it on to the next decision's.</summary>
		public void Decide(int times = 1)
		{
			for (int i = 0; i < times; i++, Tick += 30)
			{
				if (ObserveZones)
				{
					for (int z = 0; z < Zones.Count; z++)
					{
						Zones.MarkObserved(z, Tick);
					}
				}

				Commander.Decide(new CommanderWorld { Tick = Tick, Home = Home, Threat = Threat },
					CollectionsMarshal.AsSpan(Units), Zones, Contacts);
			}
		}

		public ushort[] Members(int squad)
		{
			var into = new ushort[SimConfig.MaxUnits];
			return into[..Commander.MembersOf(squad, into)];
		}

		public CommandSquad Squad(int squad) => Commander.SquadAt(squad);

		public CommandIntent Intent(int squad) => Commander.IntentOf(squad);

		public int Find(CommandRole role, bool? ready = null)
		{
			for (int s = 0; s < Commander.Capacity; s++)
			{
				CommandSquad squad = Commander.SquadAt(s);
				if (squad.Active && squad.Role == role && (ready == null || squad.Ready == ready))
				{
					return s;
				}
			}

			return -1;
		}

		public int Assault => Find(CommandRole.Assault, ready: true);
	}

	private static CommanderTraits With(int reconQuietTicks = SimConfig.TickRate * 20, int stageMaxTicks = SimConfig.TickRate * 30)
	{
		CommanderTraits d = CommanderTraits.Default;
		return new CommanderTraits(d.GarrisonUnits, d.AssaultSquadSize, d.RetreatShare, d.RetreatEnemyRatio,
			d.EnemyNearMeters, d.ThreatMeters, d.ReinforceRatio, d.AttackRatio, d.ClusterMeters, d.StagingMeters,
			d.AssembleMeters, d.AssembleShare, stageMaxTicks, reconQuietTicks, d.ReconStaleTicks, d.ResupplyShare,
			d.ResupplyHysteresis, d.ClaimTicks, d.ArriveMeters, d.ReserveOffsetMeters, d.SweepIntervalTicks,
			d.RefillHealTicks, d.SupplyStandoffMeters);
	}

	private static Vector3 Flat(Vector3 v) => new(v.X, 0f, v.Z);

	// ---- squads formed from production --------------------------------------

	[Fact]
	public void TheFirstUnits_AreTheGarrison_DefendingHome()
	{
		var rig = new Rig();
		rig.AddRiflemen(4);
		rig.Decide();

		CommandSquad garrison = rig.Squad(Commander.GarrisonSlot);
		Assert.Equal(CommandRole.Garrison, garrison.Role);
		Assert.Equal(4, garrison.Members);

		CommandIntent intent = rig.Intent(Commander.GarrisonSlot);
		Assert.Equal(CommandGoal.Reinforce, intent.Goal);
		Assert.Equal(OrderKind.Defend, intent.Order);
		Assert.Equal(Home, intent.Point);
		Assert.Equal(SquadMission.DefendZone, intent.Mission);
	}

	[Fact]
	public void TheNextUnits_FormAnAssault_ThatWaitsInFrontOfHomeUntilItIsFull()
	{
		var rig = new Rig();
		rig.AddRiflemen(6);
		rig.Decide();

		int forming = rig.Find(CommandRole.Assault, ready: false);
		Assert.True(forming > 0);
		Assert.Equal(2, rig.Squad(forming).Members);

		CommandIntent intent = rig.Intent(forming);
		Assert.Equal(CommandGoal.Hold, intent.Goal);
		Assert.Equal(OrderKind.Defend, intent.Order);
		Assert.Equal(new Vector3(0f, 0f, CommanderTraits.Default.ReserveOffsetMeters), intent.Point);
	}

	[Fact]
	public void ATank_IsNeverTheGarrison_AndGoesWithAnInfantrySquad()
	{
		var rig = new Rig();
		ushort tank = rig.Add(Vector3.Zero, Tank, armour: true);
		rig.AddRiflemen(6);
		rig.Decide();

		Assert.NotEqual(Commander.GarrisonSlot, rig.Commander.SquadOf(tank));
		Assert.Equal(4, rig.Squad(Commander.GarrisonSlot).Members);

		// It formed with the first infantry out of the garrison, and a second tank joins
		// the squad with infantry and the fewest tanks rather than forming its own.
		int squad = rig.Commander.SquadOf(tank);
		Assert.Equal(3, rig.Squad(squad).Members);
		ushort second = rig.Add(Vector3.Zero, Tank, armour: true);
		rig.Decide();
		Assert.Equal(squad, rig.Commander.SquadOf(second));
	}

	// ---- gunners and snipers (docs/TACTICS.md §6, T7) ------------------------

	[Fact]
	public void AGunner_IsNeverTheGarrison_AndAnAssaultFormsRoundIt()
	{
		var rig = new Rig();
		ushort gunner = rig.AddGunner(Vector3.Zero);
		rig.AddRiflemen(7);
		rig.Decide();

		CommandSquad garrison = rig.Squad(Commander.GarrisonSlot);
		Assert.Equal(4, garrison.Members);
		Assert.Equal(0, garrison.Gunners);

		// The first unit on the field, and still not home's: it formed the next
		// assault, which the riflemen after the garrison filled — a gunner and three.
		int squad = rig.Commander.SquadOf(gunner);
		Assert.NotEqual(Commander.GarrisonSlot, squad);
		Assert.Equal(CommandRole.Assault, rig.Squad(squad).Role);
		Assert.Equal(4, rig.Squad(squad).Members);
		Assert.Equal(1, rig.Squad(squad).Gunners);
		Assert.True(rig.Squad(squad).Ready);
	}

	[Fact]
	public void AGunner_JoinsTheRifleSquadWithoutOne_WithTheMostRiflemen()
	{
		var rig = new Rig();
		rig.AddRiflemen(12);
		rig.Decide();
		int[] assaults = Assaults(rig);
		Assert.Equal(2, assaults.Length);

		// The first squad loses a rifleman: the second has more, and gets the gunner.
		rig.Kill(rig.Members(assaults[0])[0]);
		ushort first = rig.AddGunner(Vector3.Zero);
		rig.Decide();
		Assert.Equal(assaults[1], rig.Commander.SquadOf(first));

		// The next goes to the squad without one, though it has fewer riflemen.
		ushort second = rig.AddGunner(Vector3.Zero);
		rig.Decide();
		Assert.Equal(assaults[0], rig.Commander.SquadOf(second));
		Assert.Equal(1, rig.Squad(assaults[0]).Gunners);
		Assert.Equal(1, rig.Squad(assaults[1]).Gunners);
	}

	[Fact]
	public void ASecondGunner_FormsASquadOfItsOwn_RatherThanJoinOneThatHasAGunner()
	{
		var rig = new Rig();
		rig.AddRiflemen(4);
		ushort first = rig.AddGunner(Vector3.Zero);
		rig.AddRiflemen(1);
		rig.Decide();

		// The squad still forming has a gunner and a rifleman: the second gunner does
		// not join it, and the next riflemen fill the first squad still forming.
		int squad = rig.Commander.SquadOf(first);
		ushort second = rig.AddGunner(Vector3.Zero);
		rig.Decide();
		int own = rig.Commander.SquadOf(second);
		Assert.True(own > Commander.GarrisonSlot);
		Assert.NotEqual(squad, own);
		Assert.Equal(1, rig.Squad(squad).Gunners);
		Assert.Equal(1, rig.Squad(own).Gunners);

		ushort rifleman = rig.Add(Vector3.Zero);
		rig.Decide();
		Assert.Equal(squad, rig.Commander.SquadOf(rifleman));
	}

	[Fact]
	public void AGunner_IsNotASquadOfTanks_Base_ButStartsOneForTheRiflemen()
	{
		var rig = new Rig();
		rig.AddRiflemen(4);
		for (int i = 0; i < 4; i++)
		{
			rig.Add(Vector3.Zero, Tank, armour: true);
		}

		rig.Decide();
		int tanks = rig.Assault;
		Assert.Equal(4, rig.Squad(tanks).Armour);

		// No riflemen to cover, so no drill: it forms the next squad, and the next
		// rifleman joins it rather than the tanks.
		ushort gunner = rig.AddGunner(Vector3.Zero);
		rig.Decide();
		int own = rig.Commander.SquadOf(gunner);
		Assert.True(own > Commander.GarrisonSlot);
		Assert.NotEqual(tanks, own);

		ushort rifleman = rig.Add(Vector3.Zero);
		rig.Decide();
		Assert.Equal(own, rig.Commander.SquadOf(rifleman));
	}

	[Fact]
	public void WithEverySquadAGunner_AndNoSlotLeft_AGunnerWaitsInNoSquad()
	{
		var rig = new Rig();
		rig.AddRiflemen(4);
		for (int squad = 1; squad < rig.Commander.Capacity; squad++)
		{
			rig.AddGunner(Vector3.Zero);
			rig.AddRiflemen(3);
		}

		ushort extra = rig.AddGunner(Vector3.Zero);
		ushort rifleman = rig.Add(Vector3.Zero);
		rig.Decide();

		for (int s = 0; s < rig.Commander.Capacity; s++)
		{
			Assert.True(rig.Squad(s).Gunners <= 1);
		}

		Assert.Equal(-1, rig.Commander.SquadOf(extra));

		// A rifleman still finds the smallest squad, as before.
		Assert.True(rig.Commander.SquadOf(rifleman) > Commander.GarrisonSlot);
	}

	[Fact]
	public void AssaultSlots_AreTheAssaultSquads_AndTheSlotsStillFree()
	{
		var rig = new Rig();
		rig.AddRiflemen(4);
		rig.Decide();
		Assert.Equal(rig.Commander.Capacity - 1, rig.Commander.AssaultSlots);

		// An assault squad is one of them; the trucks' squad is not.
		rig.AddRiflemen(4);
		rig.AddTruck(Vector3.Zero);
		rig.Decide();
		Assert.True(rig.Assault > Commander.GarrisonSlot);
		Assert.True(rig.Find(CommandRole.Supply) > Commander.GarrisonSlot);
		Assert.Equal(rig.Commander.Capacity - 2, rig.Commander.AssaultSlots);
	}

	[Fact]
	public void ASniper_StandsWithTheGarrison_OnTopOfItsRiflemen()
	{
		var rig = new Rig();
		rig.AddRiflemen(4);
		rig.Decide();

		ushort sniper = rig.AddSniper(Vector3.Zero);
		ushort fifth = rig.Add(Vector3.Zero);
		rig.Decide();
		Assert.Equal(Commander.GarrisonSlot, rig.Commander.SquadOf(sniper));
		Assert.Equal(5, rig.Squad(Commander.GarrisonSlot).Members);
		Assert.Equal(1, rig.Squad(Commander.GarrisonSlot).Marksmen);
		Assert.NotEqual(Commander.GarrisonSlot, rig.Commander.SquadOf(fifth));

		// A rifleman lost from the garrison is made up by a rifleman.
		rig.Kill(rig.Members(Commander.GarrisonSlot).First(id => id != sniper));
		ushort fresh = rig.Add(Vector3.Zero);
		rig.Decide();
		Assert.Equal(Commander.GarrisonSlot, rig.Commander.SquadOf(fresh));
	}

	[Fact]
	public void ASecondSniper_GoesToASquadReinforcingAZone_TheFewestSnipersFirst()
	{
		var rig = new Rig();
		rig.AddRiflemen(8);
		rig.Zones.SetHolder(NearZone, NodeHolder.Strategist, contested: true);
		rig.Decide();
		int reinforcing = rig.Assault;
		Assert.Equal(CommandTask.Defend, rig.Squad(reinforcing).Task);

		// Both at home: the first stands with the garrison, the nearer; the second
		// with the squad on the node, which has none.
		ushort first = rig.AddSniper(Vector3.Zero);
		ushort second = rig.AddSniper(Vector3.Zero);
		rig.Decide();
		Assert.Equal(Commander.GarrisonSlot, rig.Commander.SquadOf(first));
		Assert.Equal(reinforcing, rig.Commander.SquadOf(second));
	}

	[Fact]
	public void ASniper_NeverFormsAnAssault_AndWaitsForASquadHoldingGround()
	{
		var rig = new Rig();
		ushort tank = rig.Add(Vector3.Zero, Tank, armour: true);
		ushort sniper = rig.AddSniper(Vector3.Zero);
		rig.Decide(2);
		Assert.True(rig.Commander.SquadOf(tank) > Commander.GarrisonSlot);
		Assert.Equal(-1, rig.Commander.SquadOf(sniper));

		// The garrison forms, and is defending home from the decision after.
		rig.AddRiflemen(4);
		rig.Decide(2);
		Assert.Equal(Commander.GarrisonSlot, rig.Commander.SquadOf(sniper));
	}

	[Fact]
	public void ASniper_DoesNotFollowAGarrisonOutOnAnAssault()
	{
		var rig = new Rig();
		rig.Zones.SetHolder(FarZone, NodeHolder.GroundForce, contested: false);
		rig.AddRiflemen(12);
		Guard(rig, FarNode, 3);
		rig.Decide();
		Assert.Equal(CommandTask.Attack, rig.Squad(Commander.GarrisonSlot).Task);

		ushort sniper = rig.AddSniper(Vector3.Zero);
		Guard(rig, FarNode, 3);
		rig.Decide();
		Assert.NotEqual(Commander.GarrisonSlot, rig.Commander.SquadOf(sniper));
	}

	[Fact]
	public void NeitherAGunnerNorASniper_IsTheScout()
	{
		// No technical and no rifleman: the side wants a scout after 20 s quiet, and
		// has only a gunner in the squad forming and a sniper with no squad to join.
		var rig = new Rig();
		ushort gunner = rig.AddGunner(Vector3.Zero);
		ushort sniper = rig.AddSniper(Vector3.Zero);
		rig.Decide(CommanderTraits.Default.ReconQuietTicks / 30 + 2);

		Assert.True(rig.Commander.ReconWanted);
		Assert.Equal(-1, rig.Find(CommandRole.Scout));
		Assert.Equal(CommandRole.Assault, rig.Squad(rig.Commander.SquadOf(gunner)).Role);
		Assert.Equal(-1, rig.Commander.SquadOf(sniper));
	}

	// ---- Attack: stage, pause, strike -----------------------------------------

	[Fact]
	public void AReadySquad_StagesShortOfADeniedNode_ThenStrikesOnceAssembled()
	{
		var rig = new Rig();
		rig.Zones.SetHolder(FarZone, NodeHolder.GroundForce, contested: false);
		rig.AddRiflemen(8);
		rig.Decide();

		int assault = rig.Assault;
		CommandIntent stage = rig.Intent(assault);
		Assert.Equal(CommandGoal.Stage, stage.Goal);
		Assert.Equal(OrderKind.Attack, stage.Order);
		Assert.Equal(FarNode, stage.Point);
		Assert.Equal(OwnerId.None, stage.TargetOwnerId);
		Assert.Equal(SquadPhase.Gathering, stage.Phase);
		Assert.Equal(SquadMission.Attack, stage.Mission);

		// Sixty metres short of the node, on the squad's side of it.
		Vector3 staging = stage.StagingPoint;
		Assert.Equal(CommanderTraits.Default.StagingMeters, new Vector2(staging.X - FarNode.X, staging.Z - FarNode.Z).Length(), 3);
		Assert.True(staging.DistanceTo(rig.Squad(assault).Centroid) < FarNode.DistanceTo(rig.Squad(assault).Centroid));

		// Not yet assembled: still staging.
		rig.Decide();
		Assert.Equal(CommandGoal.Stage, rig.Intent(assault).Goal);

		// Assembled: the paused remainder of the plan runs, against the world as it is now.
		rig.Move(assault, staging);
		rig.Decide();
		CommandIntent strike = rig.Intent(assault);
		Assert.Equal(CommandGoal.Strike, strike.Goal);
		Assert.Equal(OrderKind.Attack, strike.Order);
		Assert.Equal(SquadPhase.Moving, strike.Phase);
		Assert.Equal(FarNode, strike.Point);
	}

	[Fact]
	public void AStage_ThatNeverAssembles_StrikesAnywayAfterItsTime()
	{
		var rig = new Rig(traits: With(stageMaxTicks: SimConfig.TickRate * 3));
		rig.Zones.SetHolder(FarZone, NodeHolder.GroundForce, contested: false);
		rig.AddRiflemen(8);
		rig.Decide();

		int assault = rig.Assault;
		Assert.Equal(CommandGoal.Stage, rig.Intent(assault).Goal);

		rig.Decide(times: 6);
		Assert.Equal(CommandGoal.Strike, rig.Intent(assault).Goal);
	}

	[Fact]
	public void ATakenNode_EndsTheAttack()
	{
		var rig = new Rig();
		rig.Zones.SetHolder(FarZone, NodeHolder.GroundForce, contested: false);
		rig.AddRiflemen(8);
		rig.Decide();
		int assault = rig.Assault;
		Assert.Equal(CommandTask.Attack, rig.Squad(assault).Task);

		rig.Zones.SetHolder(FarZone, NodeHolder.Strategist, contested: false);
		rig.Decide();

		// Nothing left to take, nothing seen: it sweeps the spawn, as legacy does.
		Assert.Equal(CommandTarget.Sweep, rig.Squad(assault).Target);
		Assert.Equal(Threat, rig.Intent(assault).Point);
	}

	[Fact]
	public void ALiveContact_IsNamed_AndTakesPrecedenceOverANode()
	{
		var rig = new Rig();
		rig.Zones.SetHolder(FarZone, NodeHolder.GroundForce, contested: false);
		rig.AddRiflemen(8);
		Vector3 player = new(-40f, 0f, 100f);
		rig.See(7, player);
		rig.Decide();

		CommandIntent intent = rig.Intent(rig.Assault);
		Assert.Equal(OwnerId.ForPeer(7), intent.TargetOwnerId);
		Assert.Equal(player, intent.Point);
	}

	[Fact]
	public void AGhost_IsNeverNamed()
	{
		var rig = new Rig();
		rig.Zones.SetHolder(FarZone, NodeHolder.GroundForce, contested: false);
		rig.AddRiflemen(8);
		rig.See(7, new Vector3(-40f, 0f, 100f));
		rig.Tick += 60;
		rig.Decide();

		CommandIntent intent = rig.Intent(rig.Assault);
		Assert.Equal(OwnerId.None, intent.TargetOwnerId);
		Assert.Equal(FarNode, intent.Point);
	}

	[Fact]
	public void ASquadTooWeakForEveryTarget_Waits_AndIsSentTheNextUnitProduced()
	{
		var rig = new Rig();
		rig.AddRiflemen(8);

		// Four players on the only thing known: 400 against 200, which needs 600.
		for (int p = 1; p <= 4; p++)
		{
			rig.See(p, new Vector3(p, 0f, 150f));
		}

		rig.Decide();
		int assault = rig.Assault;
		Assert.Equal(CommandTask.None, rig.Squad(assault).Task);
		Assert.True(rig.Squad(assault).Short);
		Assert.Equal(CommandGoal.Hold, rig.Intent(assault).Goal);

		ushort fresh = rig.Add(Vector3.Zero);
		for (int p = 1; p <= 4; p++)
		{
			rig.See(p, new Vector3(p, 0f, 150f));
		}

		rig.Decide();
		Assert.Equal(assault, rig.Commander.SquadOf(fresh));
	}

	// ---- Attack: combined assaults ----------------------------------------------

	/// <summary>Players the side sees round a point, a metre apart: each is worth two riflemen.</summary>
	private static void Guard(Rig rig, Vector3 at, int players)
	{
		for (int p = 1; p <= players; p++)
		{
			rig.See(p, at + new Vector3(p, 0f, 0f));
		}
	}

	/// <summary>The ready assault squads, lowest slot first.</summary>
	private static int[] Assaults(Rig rig) => Enumerable.Range(0, rig.Commander.Capacity)
		.Where(s => rig.Squad(s).Active && rig.Squad(s).Role == CommandRole.Assault && rig.Squad(s).Ready)
		.ToArray();

	[Fact]
	public void TwoSquadsTooWeakAlone_AssaultADeniedNodeTogether_FromOneStagingPoint()
	{
		var rig = new Rig();
		rig.Zones.SetHolder(FarZone, NodeHolder.GroundForce, contested: false);
		rig.AddRiflemen(12);

		// Two players on the far node, 200: each squad of 200 needs 300 alone.
		Guard(rig, FarNode, 2);
		rig.Decide();

		int[] assaults = Assaults(rig);
		Assert.Equal(2, assaults.Length);
		foreach (int s in assaults)
		{
			CommandSquad squad = rig.Squad(s);
			Assert.Equal(CommandTask.Attack, squad.Task);
			Assert.Equal(CommandTarget.Zone, squad.Target);
			Assert.Equal(FarZone, squad.Zone);
			Assert.False(squad.Short);
			Assert.Equal(assaults[0], squad.Lead);

			CommandIntent intent = rig.Intent(s);
			Assert.Equal(CommandGoal.Stage, intent.Goal);
			Assert.Equal(OrderKind.Attack, intent.Order);
			Assert.Equal(FarNode, intent.Point);
			Assert.Equal(OwnerId.None, intent.TargetOwnerId);
		}

		// One staging point for both, and the same order: one squad on the board.
		Assert.Equal(rig.Intent(assaults[0]).StagingPoint, rig.Intent(assaults[1]).StagingPoint);

		// 400 was enough without the garrison, which stays at home.
		Assert.Equal(CommandTask.Defend, rig.Squad(Commander.GarrisonSlot).Task);
		Assert.Equal(CommandGoal.Reinforce, rig.Intent(Commander.GarrisonSlot).Goal);
	}

	[Fact]
	public void ACombinedAssault_StrikesOnlyOnceTheWholeOfItHasAssembled_AndThenAllAtOnce()
	{
		var rig = new Rig();
		rig.Zones.SetHolder(FarZone, NodeHolder.GroundForce, contested: false);
		rig.AddRiflemen(12);
		Guard(rig, FarNode, 2);
		rig.Decide();

		int[] assaults = Assaults(rig);
		Vector3 staging = rig.Intent(assaults[0]).StagingPoint;

		// Half of it there: under 80 % of the assault, so neither squad strikes.
		rig.Move(assaults[0], staging);
		Guard(rig, FarNode, 2);
		rig.Decide();
		Assert.All(assaults, s => Assert.Equal(CommandGoal.Stage, rig.Intent(s).Goal));

		// All of it there: both strike on the same decision.
		rig.Move(assaults[1], staging);
		Guard(rig, FarNode, 2);
		rig.Decide();
		Assert.All(assaults, s => Assert.Equal(CommandGoal.Strike, rig.Intent(s).Goal));
		Assert.All(assaults, s => Assert.Equal(FarNode, rig.Intent(s).Point));
	}

	[Fact]
	public void TheSquadStillForming_JoinsACombinedAssault_AndProductionFormsANewOne()
	{
		var rig = new Rig();
		rig.Zones.SetHolder(FarZone, NodeHolder.GroundForce, contested: false);
		rig.AddRiflemen(10);
		Guard(rig, FarNode, 2);
		rig.Decide();

		// The garrison, a ready squad of 200 and two riflemen still forming, 100: 300
		// between the two squads, which is what two players need.
		int[] assaults = Assaults(rig);
		Assert.Equal(2, assaults.Length);
		CommandSquad formed = rig.Squad(assaults[1]);
		Assert.Equal(2, formed.Members);
		Assert.All(assaults, s => Assert.Equal(CommandTask.Attack, rig.Squad(s).Task));
		Assert.All(assaults, s => Assert.Equal(FarZone, rig.Squad(s).Zone));

		// Committed, it is no longer the squad production fills.
		ushort fresh = rig.Add(Vector3.Zero);
		Guard(rig, FarNode, 2);
		rig.Decide();
		int squad = rig.Commander.SquadOf(fresh);
		Assert.DoesNotContain(squad, assaults);
		Assert.Equal(CommandRole.Assault, rig.Squad(squad).Role);
		Assert.False(rig.Squad(squad).Ready);
	}

	[Fact]
	public void ATankLeftOnItsOwn_AndTheGarrison_TakeANodeTogether()
	{
		// docs/HTN_BOTS.md §6, H6: a strategist with no income left with a garrison of
		// four and a tank whose infantry died, each holding where it was.
		var rig = new Rig();
		rig.AddRiflemen(4);
		ushort tank = rig.Add(Vector3.Zero, Tank, armour: true);
		rig.AddRiflemen(3);
		rig.Decide();
		int armour = rig.Commander.SquadOf(tank);
		Assert.True(rig.Squad(armour).Ready);

		foreach (ushort id in rig.Members(armour).Where(id => id != tank))
		{
			rig.Kill(id);
		}

		// 400 of 550 left: not depleted. Three players on the denied node, 300, need 450.
		rig.Zones.SetHolder(FarZone, NodeHolder.GroundForce, contested: false);
		Guard(rig, FarNode, 3);
		rig.Decide();

		Assert.False(rig.Squad(armour).Depleted);
		Assert.Equal(CommandTask.Attack, rig.Squad(armour).Task);
		Assert.Equal(FarZone, rig.Squad(armour).Zone);

		CommandSquad garrison = rig.Squad(Commander.GarrisonSlot);
		Assert.Equal(CommandRole.Garrison, garrison.Role);
		Assert.Equal(CommandTask.Attack, garrison.Task);
		Assert.Equal(FarZone, garrison.Zone);
		Assert.Equal(garrison.Lead, rig.Squad(armour).Lead);
		Assert.Equal(CommandGoal.Stage, rig.Intent(Commander.GarrisonSlot).Goal);
		Assert.Equal(rig.Intent(armour).StagingPoint, rig.Intent(Commander.GarrisonSlot).StagingPoint);
	}

	[Fact]
	public void TheGarrison_GoesOnlyWhenTheSquadsAreNotEnoughWithoutIt()
	{
		var rig = new Rig();
		rig.Zones.SetHolder(FarZone, NodeHolder.GroundForce, contested: false);
		rig.AddRiflemen(12);

		// Three players, 300, need 450: the two squads make 400, and the garrison 600.
		Guard(rig, FarNode, 3);
		rig.Decide();

		Assert.All(Assaults(rig), s => Assert.Equal(CommandTask.Attack, rig.Squad(s).Task));
		Assert.Equal(CommandTask.Attack, rig.Squad(Commander.GarrisonSlot).Task);
		Assert.Equal(CommandGoal.Stage, rig.Intent(Commander.GarrisonSlot).Goal);
	}

	[Fact]
	public void TheGarrison_StaysHome_WhileHomeIsThreatened()
	{
		var rig = new Rig();
		rig.Zones.SetHolder(FarZone, NodeHolder.GroundForce, contested: false);
		rig.AddRiflemen(12);
		Guard(rig, FarNode, 3);

		// A player 30 m from home: the garrison is home's defence. One squad goes after
		// the player, whom it beats alone; the other cannot take the node without the
		// garrison, so it waits.
		rig.See(9, new Vector3(0f, 0f, -30f));
		rig.Decide();

		Assert.Equal(CommandTask.Defend, rig.Squad(Commander.GarrisonSlot).Task);
		Assert.Equal(CommandGoal.Reinforce, rig.Intent(Commander.GarrisonSlot).Goal);
		Assert.DoesNotContain(Enumerable.Range(0, rig.Commander.Capacity),
			s => rig.Squad(s).Task == CommandTask.Attack && rig.Squad(s).Zone == FarZone);
		Assert.Single(Assaults(rig), s => rig.Squad(s).Short);
	}

	[Fact]
	public void AGarrisonOnAnAssault_IsCalledHome_WhenHomeIsThreatened_AndTheOthersGoOn()
	{
		var rig = new Rig();
		rig.Zones.SetHolder(FarZone, NodeHolder.GroundForce, contested: false);
		rig.AddRiflemen(12);
		Guard(rig, FarNode, 3);
		rig.Decide();
		Assert.Equal(CommandTask.Attack, rig.Squad(Commander.GarrisonSlot).Task);

		Guard(rig, FarNode, 3);
		rig.See(9, new Vector3(0f, 0f, -30f));
		rig.Decide();

		Assert.Equal(CommandTask.Defend, rig.Squad(Commander.GarrisonSlot).Task);
		CommandIntent home = rig.Intent(Commander.GarrisonSlot);
		Assert.Equal(CommandGoal.Reinforce, home.Goal);
		Assert.Equal(Home, home.Point);
		Assert.All(Assaults(rig), s => Assert.Equal(CommandTask.Attack, rig.Squad(s).Task));
	}

	[Fact]
	public void AGarrisonOnAnAssault_IsNotSentProduction_AndGoesHomeOnceTheNodeIsTaken()
	{
		var rig = new Rig();
		rig.Zones.SetHolder(FarZone, NodeHolder.GroundForce, contested: false);
		rig.AddRiflemen(12);
		Guard(rig, FarNode, 3);
		rig.Decide();
		ushort[] garrison = rig.Members(Commander.GarrisonSlot);
		Assert.Equal(CommandTask.Attack, rig.Squad(Commander.GarrisonSlot).Task);

		// One of it dies on the way: a unit produced now does not walk out after it.
		rig.Kill(garrison[0]);
		ushort fresh = rig.Add(Vector3.Zero);
		Guard(rig, FarNode, 3);
		rig.Decide();
		Assert.NotEqual(Commander.GarrisonSlot, rig.Commander.SquadOf(fresh));
		Assert.Equal(3, rig.Squad(Commander.GarrisonSlot).Members);

		// Taken: the garrison goes back to defending home.
		rig.Zones.SetHolder(FarZone, NodeHolder.Strategist, contested: false);
		rig.Decide();
		Assert.Equal(CommandTask.Defend, rig.Squad(Commander.GarrisonSlot).Task);
		Assert.Equal(CommandGoal.Reinforce, rig.Intent(Commander.GarrisonSlot).Goal);
		Assert.Equal(Home, rig.Intent(Commander.GarrisonSlot).Point);
	}

	[Fact]
	public void AGarrisonLosingItsAssault_FallsBack()
	{
		var rig = new Rig();
		rig.Zones.SetHolder(FarZone, NodeHolder.GroundForce, contested: false);
		rig.AddRiflemen(12);
		Guard(rig, FarNode, 3);
		rig.Decide();
		Assert.Equal(CommandTask.Attack, rig.Squad(Commander.GarrisonSlot).Task);

		// At the node, 30 % left, against two players: more than 1.5 times 60.
		Vector3 fight = FarNode + new Vector3(0f, 0f, -20f);
		rig.Move(Commander.GarrisonSlot, fight);
		foreach (ushort id in rig.Members(Commander.GarrisonSlot))
		{
			rig.Set(id, u => u with { Strength = Rifleman * 0.3f });
		}

		rig.See(1, fight + new Vector3(10f, 0f, 0f));
		rig.See(2, fight + new Vector3(-10f, 0f, 0f));
		rig.Decide();

		Assert.True(rig.Squad(Commander.GarrisonSlot).FallingBack);
		Assert.Equal(CommandGoal.FallBack, rig.Intent(Commander.GarrisonSlot).Goal);
	}

	[Fact]
	public void SquadsTooWeakEvenTogether_Hold()
	{
		var rig = new Rig();
		rig.Zones.SetHolder(FarZone, NodeHolder.GroundForce, contested: false);
		rig.AddRiflemen(12);

		// Five players, 500, need 750: the whole side is 600.
		Guard(rig, FarNode, 5);
		rig.Decide();

		Assert.Equal(CommandTask.Defend, rig.Squad(Commander.GarrisonSlot).Task);
		foreach (int s in Assaults(rig))
		{
			Assert.Equal(CommandTask.None, rig.Squad(s).Task);
			Assert.True(rig.Squad(s).Short);
			Assert.Equal(CommandGoal.Hold, rig.Intent(s).Goal);
		}
	}

	/// <summary>
	/// What a bot-only round came to from 39 s in: a garrison of four, and a tank on its
	/// own in the squad still forming; every node the ground force's, and nobody seen at
	/// any of them.
	/// </summary>
	private static (Rig rig, ushort tank) GarrisonAndALoneTank()
	{
		var rig = new Rig();
		rig.AddRiflemen(4);
		ushort tank = rig.Add(Vector3.Zero, Tank, armour: true);
		rig.Zones.SetHolder(NearZone, NodeHolder.GroundForce, contested: false);
		rig.Zones.SetHolder(FarZone, NodeHolder.GroundForce, contested: false);
		return (rig, tank);
	}

	[Fact]
	public void TheSquadStillForming_WouldDoAlone_ButGoesWithTheGarrison()
	{
		(Rig rig, ushort tank) = GarrisonAndALoneTank();
		rig.Decide();

		int forming = rig.Commander.SquadOf(tank);
		Assert.True(rig.Squad(forming).Ready);
		Assert.Equal(CommandTask.Attack, rig.Squad(forming).Task);
		Assert.Equal(NearZone, rig.Squad(forming).Zone);
		Assert.Equal(OrderKind.Attack, rig.Intent(forming).Order);
		Assert.Equal(NearNode, rig.Intent(forming).Point);

		CommandSquad garrison = rig.Squad(Commander.GarrisonSlot);
		Assert.Equal(CommandTask.Attack, garrison.Task);
		Assert.Equal(NearZone, garrison.Zone);
		Assert.Equal(garrison.Lead, rig.Squad(forming).Lead);
	}

	[Fact]
	public void ATechnicalOnItsOwnInTheSquadForming_IsKeptForRecon_NotSentWithTheGarrison()
	{
		// htn_recon_finds_contact, seed 2: before recon is wanted, a technical alone in
		// the squad forming went out with the garrison, and was never the scout.
		var rig = new Rig();
		rig.AddRiflemen(4);
		ushort technical = rig.Add(Vector3.Zero, Technical, scout: true);
		rig.Zones.SetHolder(FarZone, NodeHolder.GroundForce, contested: false);
		rig.Decide();

		Assert.False(rig.Commander.ReconWanted);
		Assert.Equal(CommandTask.Defend, rig.Squad(Commander.GarrisonSlot).Task);
		Assert.Equal(CommandGoal.Hold, rig.Intent(rig.Commander.SquadOf(technical)).Goal);

		rig.Decide(times: SimConfig.TickRate * 20 / 30);
		Assert.Equal(CommandRole.Scout, rig.Squad(rig.Commander.SquadOf(technical)).Role);
	}

	[Fact]
	public void TheSquadStillForming_NeverGoesAlone()
	{
		(Rig rig, ushort tank) = GarrisonAndALoneTank();

		// A player 30 m from home: the garrison stays, and the tank waits for infantry.
		rig.See(9, new Vector3(0f, 0f, -30f));
		rig.Decide();

		Assert.Equal(CommandTask.Defend, rig.Squad(Commander.GarrisonSlot).Task);
		int forming = rig.Commander.SquadOf(tank);
		Assert.False(rig.Squad(forming).Ready);
		Assert.Equal(CommandGoal.Hold, rig.Intent(forming).Goal);
	}

	// ---- Defend Zone ---------------------------------------------------------

	[Fact]
	public void AContestedNode_AndAnIdleSquad_IsDefendZoneOnThatNode()
	{
		var rig = new Rig();
		rig.AddRiflemen(8);
		rig.Zones.SetHolder(NearZone, NodeHolder.Strategist, contested: true);
		rig.Decide();

		int assault = rig.Assault;
		Assert.Equal(CommandTask.Defend, rig.Squad(assault).Task);
		Assert.Equal(NearZone, rig.Squad(assault).Zone);

		CommandIntent intent = rig.Intent(assault);
		Assert.Equal(CommandGoal.Reinforce, intent.Goal);
		Assert.Equal(OrderKind.Defend, intent.Order);
		Assert.Equal(NearNode, intent.Point);
		Assert.Equal(-1, rig.Commander.BuyTimeZone);
	}

	[Fact]
	public void AThreatNearAHeldNode_SendsASquadStrongEnoughForIt()
	{
		var rig = new Rig();
		rig.AddRiflemen(8);
		rig.See(3, NearNode + new Vector3(30f, 0f, 0f));
		rig.Decide();

		// One player, 100: a squad of 200 is 1.2 times that.
		int assault = rig.Assault;
		Assert.Equal(CommandTask.Defend, rig.Squad(assault).Task);
		Assert.Equal(NearNode, rig.Intent(assault).Point);
	}

	[Fact]
	public void AThreatNoSquadCanMeet_SendsTheReserve_AndBuysTime()
	{
		var rig = new Rig();
		rig.AddRiflemen(6);
		for (int p = 1; p <= 3; p++)
		{
			rig.See(p, NearNode + new Vector3(p, 0f, 20f));
		}

		rig.Decide();

		int forming = rig.Find(CommandRole.Assault, ready: false);
		Assert.Equal(CommandTask.Defend, rig.Squad(forming).Task);
		Assert.Equal(NearNode, rig.Intent(forming).Point);
		Assert.Equal(NearZone, rig.Commander.BuyTimeZone);
	}

	[Fact]
	public void TheGarrison_CountsAsTheDefenceOfHome()
	{
		var rig = new Rig();
		rig.AddRiflemen(8);
		rig.See(3, new Vector3(0f, 0f, -30f));
		rig.Decide();

		// 200 at home against 100: covered, and the assault is free to attack.
		Assert.Equal(BarracksZone, rig.Squad(Commander.GarrisonSlot).Zone);
		Assert.NotEqual(CommandTask.Defend, rig.Squad(rig.Assault).Task);
	}

	[Fact]
	public void ADefence_EndsWhenTheThreatDoes()
	{
		var rig = new Rig();
		rig.AddRiflemen(8);
		rig.Zones.SetHolder(NearZone, NodeHolder.Strategist, contested: true);
		rig.Decide();
		int assault = rig.Assault;
		Assert.Equal(CommandGoal.Reinforce, rig.Intent(assault).Goal);

		rig.Zones.SetHolder(NearZone, NodeHolder.Strategist, contested: false);
		rig.Decide();
		Assert.NotEqual(CommandTask.Defend, rig.Squad(assault).Task);
		Assert.NotEqual(CommandGoal.Reinforce, rig.Intent(assault).Goal);
	}

	// ---- Recon -------------------------------------------------------------------

	[Fact]
	public void NoContactFor20s_AndATechnical_IsRecon()
	{
		var rig = new Rig();
		rig.AddRiflemen(6);
		ushort technical = rig.Add(Vector3.Zero, Technical, scout: true);
		rig.Decide();
		Assert.False(rig.Commander.ReconWanted);

		rig.Decide(times: SimConfig.TickRate * 20 / 30);
		Assert.True(rig.Commander.ReconWanted);

		int scout = rig.Commander.SquadOf(technical);
		Assert.Equal(CommandRole.Scout, rig.Squad(scout).Role);
		Assert.Equal(1, rig.Squad(scout).Members);

		CommandIntent intent = rig.Intent(scout);
		Assert.Equal(CommandGoal.Scout, intent.Goal);
		Assert.Equal(OrderKind.Patrol, intent.Order);
		Assert.Equal(SquadMission.Recon, intent.Mission);
	}

	[Fact]
	public void TheFirstUnitOnTheField_BeingATechnical_IsNotTheGarrison_AndScouts()
	{
		var rig = new Rig();
		ushort technical = rig.Add(Vector3.Zero, Technical, scout: true);
		rig.AddRiflemen(4);
		rig.Decide();
		Assert.NotEqual(Commander.GarrisonSlot, rig.Commander.SquadOf(technical));
		Assert.Equal(4, rig.Squad(Commander.GarrisonSlot).Members);

		rig.Decide(times: SimConfig.TickRate * 20 / 30);
		Assert.Equal(CommandRole.Scout, rig.Squad(rig.Commander.SquadOf(technical)).Role);
	}

	[Fact]
	public void WithNoTechnical_ARiflemanScouts()
	{
		var rig = new Rig(traits: With(reconQuietTicks: 0));
		rig.AddRiflemen(6);
		rig.Decide();

		int scout = rig.Find(CommandRole.Scout);
		Assert.True(scout > 0);
		Assert.Equal(1, rig.Squad(scout).Members);
		Assert.Equal(CommandGoal.Scout, rig.Intent(scout).Goal);
	}

	[Fact]
	public void ANodeUnseenFor60s_IsWhereTheScoutGoes()
	{
		var rig = new Rig(traits: With(reconQuietTicks: int.MaxValue));
		rig.AddRiflemen(4);
		rig.Add(Vector3.Zero, Technical, scout: true);
		rig.Decide();
		Assert.False(rig.Commander.ReconWanted);

		// The far node goes unseen; everything else stays in sight.
		rig.ObserveZones = false;
		for (int i = 0; i < SimConfig.TickRate * 60 / 30 + 1; i++)
		{
			for (int z = 0; z < rig.Zones.Count; z++)
			{
				if (z != FarZone)
				{
					rig.Zones.MarkObserved(z, rig.Tick);
				}
			}

			rig.Decide();
		}

		Assert.True(rig.Commander.ReconWanted);
		int scout = rig.Find(CommandRole.Scout);
		Assert.Equal(FarNode, rig.Intent(scout).Point);
	}

	[Fact]
	public void RecentContacts_AndEverythingInSight_WantNoScout()
	{
		var rig = new Rig(traits: With(reconQuietTicks: 0));
		rig.AddRiflemen(4);
		ushort technical = rig.Add(Vector3.Zero, Technical, scout: true);
		rig.Decide();
		Assert.Equal(CommandRole.Scout, rig.Squad(rig.Commander.SquadOf(technical)).Role);

		var quiet = new Rig(traits: With(reconQuietTicks: int.MaxValue));
		quiet.AddRiflemen(4);
		ushort other = quiet.Add(Vector3.Zero, Technical, scout: true);
		quiet.Decide();
		Assert.NotEqual(CommandRole.Scout, quiet.Squad(quiet.Commander.SquadOf(other)).Role);
	}

	// ---- Retreat -----------------------------------------------------------------

	/// <summary>A ready squad on its way to a denied node, its units at <paramref name="at"/>.</summary>
	private static (Rig rig, int assault) Attacking(Vector3 at)
	{
		var rig = new Rig();
		rig.Zones.SetHolder(FarZone, NodeHolder.GroundForce, contested: false);
		rig.AddRiflemen(8);
		rig.Decide();
		int assault = rig.Assault;
		rig.Move(assault, at);
		rig.Decide();
		return (rig, assault);
	}

	[Fact]
	public void ASquadAt30Percent_Against2xEnemy_Retreats_PreemptingItsAttack()
	{
		Vector3 fight = FarNode + new Vector3(0f, 0f, -20f);
		(Rig rig, int assault) = Attacking(fight);
		Assert.Equal(CommandTask.Attack, rig.Squad(assault).Task);

		int replacements = 0;
		CommandContext context = rig.Commander.ContextOf(assault);
		context.PlannerState.OnReplacePlan = (_, _, _) => replacements++;

		// 30 %: 60 of 200 left, against two players, 200 — more than 1.5 times 60.
		foreach (ushort id in rig.Members(assault))
		{
			rig.Set(id, u => u with { Strength = Rifleman * 0.3f, Engaging = true });
		}

		rig.See(1, fight + new Vector3(10f, 0f, 0f));
		rig.See(2, fight + new Vector3(-10f, 0f, 0f));
		rig.Decide();

		Assert.True(rig.Squad(assault).FallingBack);
		Assert.Equal(CommandTask.None, rig.Squad(assault).Task);

		CommandIntent intent = rig.Intent(assault);
		Assert.Equal(CommandGoal.FallBack, intent.Goal);
		Assert.Equal(OrderKind.Move, intent.Order);
		Assert.Equal(SquadMission.Retreat, intent.Mission);
		Assert.Equal(SquadPhase.FallingBack, intent.Phase);
		Assert.True(replacements <= 1);
	}

	[Fact]
	public void ARetreat_GoesToTheNearestHeldZoneWithFriends_ThatAreNotLosingThemselves()
	{
		Vector3 fight = FarNode + new Vector3(0f, 0f, -20f);
		(Rig rig, int assault) = Attacking(fight);

		// Friends on the near node; nobody on the far one, which the squad is losing.
		rig.Zones.BeginCensus();
		rig.Zones.AddFriendly(NearNode, 200f);
		rig.Zones.AddFriendly(fight, 60f);

		foreach (ushort id in rig.Members(assault))
		{
			rig.Set(id, u => u with { Strength = Rifleman * 0.3f });
		}

		rig.See(1, fight);
		rig.See(2, fight);
		rig.Decide();

		Assert.Equal(NearNode, rig.Intent(assault).Point);
	}

	[Fact]
	public void ARetreatThatArrives_IsMerged_AndItsUnitsRefillAnotherSquad()
	{
		Vector3 fight = FarNode + new Vector3(0f, 0f, -20f);
		(Rig rig, int assault) = Attacking(fight);
		ushort[] members = rig.Members(assault);
		foreach (ushort id in members)
		{
			rig.Set(id, u => u with { Strength = Rifleman * 0.3f });
		}

		rig.See(1, fight);
		rig.See(2, fight);
		rig.Decide();
		Vector3 fallback = rig.Squad(assault).FallbackPoint;

		rig.Move(assault, fallback);
		rig.Decide();

		// The squad is gone, and its units are routed again at the next decision.
		Assert.False(rig.Squad(assault).Active && rig.Squad(assault).FallingBack);
		rig.Decide();
		Assert.All(members, id => Assert.True(rig.Commander.SquadOf(id) >= 0));
	}

	[Fact]
	public void TheGarrison_NeverRetreats()
	{
		var rig = new Rig();
		rig.AddRiflemen(4);
		rig.Decide();
		foreach (ushort id in rig.Members(Commander.GarrisonSlot))
		{
			rig.Set(id, u => u with { Strength = 5f });
		}

		rig.See(1, Home);
		rig.See(2, Home);
		rig.Decide();

		Assert.False(rig.Squad(Commander.GarrisonSlot).FallingBack);
		Assert.Equal(CommandGoal.Reinforce, rig.Intent(Commander.GarrisonSlot).Goal);
	}

	// ---- Resupply ----------------------------------------------------------------

	[Fact]
	public void ADepletedSquadOutOfTheFight_RefillsAtHome_AndTakesTheNextUnits()
	{
		Vector3 far = FarNode + new Vector3(0f, 0f, -20f);
		(Rig rig, int assault) = Attacking(far);
		ushort[] members = rig.Members(assault);

		// Two of four dead, nobody near: below 60 %, not losing a fight.
		rig.Kill(members[0]);
		rig.Kill(members[1]);
		rig.Decide();

		Assert.True(rig.Squad(assault).Depleted);
		Assert.Equal(CommandTask.None, rig.Squad(assault).Task);
		CommandIntent intent = rig.Intent(assault);
		Assert.Equal(CommandGoal.Refill, intent.Goal);
		Assert.Equal(OrderKind.Move, intent.Order);
		Assert.Equal(Home, intent.Point);
		Assert.Equal(SquadMission.Resupply, intent.Mission);

		// Falling back on the board, so its units do not stop to take cover from fire on
		// the way home (docs/TACTICS.md §5.6).
		Assert.Equal(SquadPhase.FallingBack, intent.Phase);

		// Production goes to it until it is back above 70 % of what it was: one
		// rifleman makes 150 of 200, and the next goes elsewhere.
		ushort fresh = rig.Add(Vector3.Zero);
		rig.Decide();
		Assert.Equal(assault, rig.Commander.SquadOf(fresh));
		Assert.False(rig.Squad(assault).Depleted);
		Assert.Equal(200f, rig.Squad(assault).StrengthAtFormation);

		ushort another = rig.Add(Vector3.Zero);
		rig.Decide();
		Assert.NotEqual(assault, rig.Commander.SquadOf(another));
	}

	[Fact]
	public void ADepletedSquadThatReachesHome_IsMergedIntoTheSquadForming()
	{
		Vector3 far = FarNode + new Vector3(0f, 0f, -20f);
		(Rig rig, int assault) = Attacking(far);
		ushort[] members = rig.Members(assault);
		rig.Kill(members[0]);
		rig.Kill(members[1]);
		rig.Decide();
		Assert.Equal(CommandGoal.Refill, rig.Intent(assault).Goal);

		// Nothing produced: it walks home, and there it stops being a squad of its own.
		// The far node is taken meanwhile, so nothing calls the two out again with the
		// garrison (a combined assault).
		rig.Zones.SetHolder(FarZone, NodeHolder.Strategist, contested: false);
		rig.Move(assault, Home);
		rig.Decide();
		rig.Decide();

		int forming = rig.Commander.SquadOf(members[2]);
		Assert.Equal(forming, rig.Commander.SquadOf(members[3]));
		Assert.False(rig.Squad(forming).Ready);
		Assert.Equal(CommandGoal.Hold, rig.Intent(forming).Goal);

		// Two more and it is four again, and ready.
		rig.Add(Home);
		rig.Add(Home);
		rig.Decide();
		Assert.True(rig.Squad(forming).Ready);
	}

	[Fact]
	public void ADepletedSquadInAFight_KeepsFighting()
	{
		Vector3 far = FarNode + new Vector3(0f, 0f, -20f);
		(Rig rig, int assault) = Attacking(far);
		ushort[] members = rig.Members(assault);
		rig.Kill(members[0]);
		rig.Kill(members[1]);
		rig.Set(members[2], u => u with { Engaging = true });
		rig.Decide();

		Assert.True(rig.Squad(assault).Depleted);
		Assert.Equal(CommandTask.Attack, rig.Squad(assault).Task);
	}

	// ---- Supply trucks and healing (H5, D2) ------------------------------------------

	[Fact]
	public void ASupplyTruck_IsNeverTheGarrison_AndWithNothingToSupplyWaitsInReserve()
	{
		var rig = new Rig();
		ushort truck = rig.AddTruck(Home);
		rig.AddRiflemen(4);
		rig.Decide();

		int trucks = rig.Find(CommandRole.Supply);
		Assert.True(trucks > Commander.GarrisonSlot);
		Assert.Equal(trucks, rig.Commander.SquadOf(truck));
		Assert.Equal(4, rig.Squad(Commander.GarrisonSlot).Members);
		Assert.Equal(CommandTask.Supply, rig.Squad(trucks).Task);

		CommandIntent intent = rig.Intent(trucks);
		Assert.Equal(CommandGoal.Station, intent.Goal);
		Assert.Equal(OrderKind.Move, intent.Order);
		Assert.Equal(SquadMission.Resupply, intent.Mission);
		Assert.Equal(new Vector3(0f, 0f, CommanderTraits.Default.ReserveOffsetMeters), intent.Point);
	}

	[Fact]
	public void ASupplyTruck_NeverScouts_NorFillsAnAssault()
	{
		var rig = new Rig(traits: With(reconQuietTicks: 0));
		ushort truck = rig.AddTruck(Home);
		rig.AddRiflemen(9);
		rig.Decide();

		Assert.Equal(rig.Find(CommandRole.Supply), rig.Commander.SquadOf(truck));
		int scout = rig.Find(CommandRole.Scout);
		Assert.True(scout > 0);
		Assert.DoesNotContain(truck, rig.Members(scout));
		Assert.DoesNotContain(truck, rig.Members(rig.Assault));
	}

	[Fact]
	public void TheTrucks_WaitAtTheStagingPointOfAnAttack()
	{
		(Rig rig, int assault) = Attacking(FarNode + new Vector3(0f, 0f, -80f));
		Assert.Equal(CommandTask.Attack, rig.Squad(assault).Task);

		ushort truck = rig.AddTruck(Home);
		rig.Decide();

		int trucks = rig.Commander.SquadOf(truck);
		Assert.Equal(rig.Squad(assault).StagingPoint, rig.Intent(trucks).Point);
	}

	[Fact]
	public void TheTrucks_StandBehindASquadDefendingANodeAwayFromHome()
	{
		var rig = new Rig();
		rig.AddTruck(Home);
		rig.AddRiflemen(8);
		rig.Zones.SetHolder(NearZone, NodeHolder.Strategist, contested: true);
		rig.Decide();
		Assert.Equal(CommandTask.Defend, rig.Squad(rig.Assault).Task);

		// The near node is 50 m out towards the threat; 40 m back from it towards home.
		CommandIntent intent = rig.Intent(rig.Find(CommandRole.Supply));
		Assert.Equal(NearNode - new Vector3(0f, 0f, CommanderTraits.Default.SupplyStandoffMeters), intent.Point);
	}

	[Fact]
	public void TheTrucks_StayAStagingDistanceBack_FromASquadThatStagedWhereItStood()
	{
		// A squad already inside the staging distance stages where it stands, in the fight.
		(Rig rig, int assault) = Attacking(FarNode + new Vector3(0f, 0f, -20f));
		Assert.Equal(CommandTask.Attack, rig.Squad(assault).Task);
		Assert.True(Flat(rig.Squad(assault).StagingPoint - FarNode).Length() < CommanderTraits.Default.StagingMeters);

		ushort truck = rig.AddTruck(Home);
		rig.Decide();

		Vector3 point = rig.Intent(rig.Commander.SquadOf(truck)).Point;
		Assert.Equal(CommanderTraits.Default.StagingMeters, Flat(point - FarNode).Length(), 2);
		Assert.True(Flat(point).Length() < Flat(FarNode).Length());
	}

	/// <summary>An attack at <paramref name="at"/> that has taken every member to half health: 100 of 200, and all four alive.</summary>
	private static (Rig rig, int assault) Wounded(Vector3 at)
	{
		(Rig rig, int assault) = Attacking(at);
		rig.Heals();
		foreach (ushort id in rig.Members(assault))
		{
			rig.Set(id, u => u with { Strength = Rifleman * 0.5f });
		}

		return (rig, assault);
	}

	[Fact]
	public void ADepletedSquadHealingCanRestore_GoesToTheNearestTruck_WhichComesToMeetIt()
	{
		Vector3 far = FarNode + new Vector3(0f, 0f, -20f);
		(Rig rig, int assault) = Wounded(far);
		Vector3 truckAt = far + new Vector3(-20f, 0f, -20f);
		ushort truck = rig.AddTruck(truckAt);
		rig.Decide();

		CommandSquad squad = rig.Squad(assault);
		Assert.True(squad.Depleted);
		Assert.True(squad.Healable);

		CommandIntent intent = rig.Intent(assault);
		Assert.Equal(CommandGoal.Refill, intent.Goal);
		Assert.Equal(OrderKind.Move, intent.Order);
		Assert.Equal(truckAt, intent.Point);
		Assert.Equal(SquadMission.Resupply, intent.Mission);

		// The trucks go to meet it, rather than it walking all the way to them.
		Assert.Equal(Flat(squad.Centroid), Flat(rig.Intent(rig.Commander.SquadOf(truck)).Point));
	}

	[Fact]
	public void TheTrucks_AreNotSentWhereTheEnemyIsRemembered()
	{
		Vector3 far = FarNode + new Vector3(0f, 0f, -20f);
		(Rig rig, int assault) = Wounded(far);
		ushort truck = rig.AddTruck(far + new Vector3(-20f, 0f, -20f));

		// A player the side saw 20 m from the squad: the squad walks to the trucks,
		// and the trucks stay where nobody is.
		rig.See(4, far + new Vector3(0f, 0f, 20f));
		rig.Decide();

		Assert.Equal(CommandGoal.Refill, rig.Intent(assault).Goal);
		Vector3 point = rig.Intent(rig.Commander.SquadOf(truck)).Point;
		Assert.NotEqual(Flat(rig.Squad(assault).Centroid), Flat(point));
		Assert.Equal(new Vector3(0f, 0f, CommanderTraits.Default.ReserveOffsetMeters), point);
	}

	[Fact]
	public void AHealedSquad_IsNotMerged_AndGoesBackToWork()
	{
		Vector3 far = FarNode + new Vector3(0f, 0f, -20f);
		(Rig rig, int assault) = Wounded(far);
		ushort[] members = rig.Members(assault);
		rig.AddTruck(far);
		rig.Decide();
		Assert.Equal(CommandGoal.Refill, rig.Intent(assault).Goal);

		// Standing at the truck while it heals: still its own squad.
		rig.Decide(3);
		Assert.Equal(CommandGoal.Refill, rig.Intent(assault).Goal);
		Assert.Equal(members, rig.Members(assault));

		// Healed past 70 %: the task that ended it was the resupply, not the squad.
		foreach (ushort id in members)
		{
			rig.Set(id, u => u with { Strength = Rifleman });
		}

		rig.Decide();
		Assert.False(rig.Squad(assault).Depleted);
		Assert.Equal(members, rig.Members(assault));
		Assert.NotEqual(CommandGoal.Refill, rig.Intent(assault).Goal);
		Assert.Equal(CommandTask.Attack, rig.Squad(assault).Task);
	}

	[Fact]
	public void AHealableSquadWithNoTruck_HealsAtHome_WithoutBeingMerged()
	{
		Vector3 far = FarNode + new Vector3(0f, 0f, -20f);
		(Rig rig, int assault) = Wounded(far);
		ushort[] members = rig.Members(assault);
		rig.Decide();
		Assert.Equal(Home, rig.Intent(assault).Point);

		rig.Move(assault, Home);
		rig.Decide(2);

		Assert.Equal(CommandGoal.Refill, rig.Intent(assault).Goal);
		Assert.Equal(members, rig.Members(assault));
	}

	[Fact]
	public void ASquadHealingCannotRestore_GoesHomeToBeMerged_EvenWithATruckNearer()
	{
		Vector3 far = FarNode + new Vector3(0f, 0f, -20f);
		(Rig rig, int assault) = Attacking(far);
		rig.Heals();
		ushort[] members = rig.Members(assault);

		// Two of four dead: at full health the two left are 100 of 200, short of 70 %.
		rig.Kill(members[0]);
		rig.Kill(members[1]);
		rig.AddTruck(far);
		rig.Decide();

		Assert.True(rig.Squad(assault).Depleted);
		Assert.False(rig.Squad(assault).Healable);
		Assert.Equal(Home, rig.Intent(assault).Point);
	}

	[Fact]
	public void AHealThatTakesTooLong_IsGivenUp_AndTheSquadGoesHomeToBeMerged()
	{
		Vector3 far = FarNode + new Vector3(0f, 0f, -20f);
		(Rig rig, int assault) = Wounded(far);
		Vector3 truckAt = far + new Vector3(5f, 0f, 0f);
		ushort truck = rig.AddTruck(truckAt);
		rig.Decide();
		Assert.Equal(truckAt, rig.Intent(assault).Point);

		// Nothing heals it — the truck's reach never lets it, say, or it keeps being hit.
		int decisions = CommanderTraits.Default.RefillHealTicks / 30;
		rig.Decide(decisions - 1);
		Assert.Equal(truckAt, rig.Intent(assault).Point);

		rig.Decide(2);
		Assert.Equal(Home, rig.Intent(assault).Point);
		Assert.NotEqual(rig.Commander.SquadOf(truck), assault);
	}

	// ---- D1: a person's units ----------------------------------------------------

	[Fact]
	public void AUnitSomebodyElseClaimed_LeavesItsSquad_AndComesBackWhenReleased()
	{
		var rig = new Rig();
		rig.AddRiflemen(4);
		rig.Decide();
		ushort claimed = rig.Members(Commander.GarrisonSlot)[0];

		rig.Set(claimed, u => u with { Claimed = true });
		rig.Decide();
		Assert.Equal(-1, rig.Commander.SquadOf(claimed));
		Assert.Equal(3, rig.Squad(Commander.GarrisonSlot).Members);

		rig.Set(claimed, u => u with { Claimed = false });
		rig.Decide();
		Assert.Equal(Commander.GarrisonSlot, rig.Commander.SquadOf(claimed));
	}

	// ---- the domain on its own: priorities and P3 ---------------------------------

	private sealed class Squad
	{
		public readonly Domain<CommandContext> Domain;
		public readonly Planner<CommandContext> Planner = new();
		public readonly CommandContext C;
		public readonly List<string> Plans = new();
		public int Replacements;

		public Squad(bool executingConditions = true)
		{
			IFactory factory = new PooledHtnFactory();
			Domain = CommanderDomain.Build(factory, executingConditions);
			C = new CommandContext(factory, CommanderTraits.Default);
			C.Init();
			C.PlannerState.OnNewPlan = plan => Plans.Add(Names(plan));
			C.PlannerState.OnReplacePlan = (_, _, plan) =>
			{
				Replacements++;
				Plans.Add(Names(plan));
			};
			C.Tick = 1000;
		}

		public void Tick(int times = 1)
		{
			for (int i = 0; i < times; i++)
			{
				C.Tick += 30;
				Planner.Tick(Domain, C);
			}
		}

		public CommandGoal Goal => C.Intent.Goal;
	}

	private static string Names(Queue<ITask> plan) => "[" + string.Join(", ", plan.Select(t => t.Name)) + "]";

	[Theory]
	[InlineData(CommandTask.None, false, false, false, CommandGoal.Hold)]
	[InlineData(CommandTask.Defend, false, false, false, CommandGoal.Reinforce)]
	[InlineData(CommandTask.Attack, false, false, false, CommandGoal.Stage)]
	[InlineData(CommandTask.Recon, false, false, false, CommandGoal.Scout)]
	[InlineData(CommandTask.Supply, false, false, false, CommandGoal.Station)]
	[InlineData(CommandTask.Supply, true, false, false, CommandGoal.FallBack)]
	[InlineData(CommandTask.None, false, true, false, CommandGoal.Refill)]
	[InlineData(CommandTask.None, false, true, true, CommandGoal.Hold)]
	[InlineData(CommandTask.Attack, false, true, false, CommandGoal.Stage)]
	[InlineData(CommandTask.Defend, true, false, false, CommandGoal.FallBack)]
	[InlineData(CommandTask.Attack, true, true, true, CommandGoal.FallBack)]
	public void EachRowOfTheTree(CommandTask task, bool fallingBack, bool depleted, bool engaged, CommandGoal expected)
	{
		var squad = new Squad();
		squad.C.Sense(CommandFact.Task, (byte)task);
		squad.C.Sense(CommandFact.FallingBack, fallingBack);
		squad.C.Sense(CommandFact.Depleted, depleted);
		squad.C.Sense(CommandFact.Engaged, engaged);
		squad.Tick();

		Assert.Equal(expected, squad.Goal);
	}

	[Fact]
	public void Retreat_PreemptsARunningStrike_OnTheNextDecision()
	{
		var squad = new Squad();
		squad.C.Sense(CommandFact.Task, (byte)CommandTask.Attack);
		squad.C.Assembled = true;
		squad.Tick();
		Assert.Equal(CommandGoal.Strike, squad.Goal);

		squad.C.Sense(CommandFact.FallingBack, true);
		squad.Tick();
		Assert.Equal(CommandGoal.FallBack, squad.Goal);
		Assert.Equal(1, squad.Replacements);
	}

	[Fact]
	public void DefendZone_PreemptsAStagedAttack()
	{
		var squad = new Squad();
		squad.C.Sense(CommandFact.Task, (byte)CommandTask.Attack);
		squad.Tick();
		Assert.Equal(CommandGoal.Stage, squad.Goal);

		squad.C.Sense(CommandFact.Task, (byte)CommandTask.Defend);
		squad.Tick();
		Assert.Equal(CommandGoal.Reinforce, squad.Goal);
	}

	[Fact]
	public void ARefillAtHome_SaysItHasArrived()
	{
		var squad = new Squad();
		squad.C.Sense(CommandFact.Depleted, true);
		squad.Tick();
		Assert.Equal(CommandGoal.Refill, squad.Goal);
		Assert.False(squad.C.Arrived);

		squad.C.AtHome = true;
		squad.Tick();
		Assert.True(squad.C.Arrived);
	}

	[Fact]
	public void ARefillThatWillHeal_GoesToTheNearestSupply_AndStaysThere_UntilItsTimeIsUp()
	{
		var squad = new Squad();
		squad.C.Healable = true;
		squad.C.SupplyPoint = new Vector3(30f, 0f, 40f);
		squad.C.HomePoint = Home;
		squad.C.Sense(CommandFact.Depleted, true);
		squad.Tick();
		Assert.Equal(CommandGoal.Refill, squad.Goal);
		Assert.Equal(squad.C.SupplyPoint, squad.C.Intent.Point);
		Assert.Equal(SquadPhase.FallingBack, squad.C.Intent.Phase);

		// Home, and healing: it is not merged while it heals.
		squad.C.AtHome = true;
		squad.C.SupplyPoint = Home;
		squad.Tick();
		Assert.False(squad.C.Arrived);

		squad.Tick(CommanderTraits.Default.RefillHealTicks / 30);
		Assert.True(squad.C.Arrived);
	}

	[Fact]
	public void AFallBackThatArrives_TakesTheLatchOff_AndSaysSo()
	{
		var squad = new Squad();
		squad.C.Sense(CommandFact.FallingBack, true);
		squad.Tick();
		Assert.Equal(CommandGoal.FallBack, squad.Goal);

		squad.C.AtFallback = true;
		squad.Tick();
		Assert.True(squad.C.Arrived);
		Assert.False(squad.C.Is(CommandFact.FallingBack));
		Assert.Equal(CommandGoal.Hold, squad.Goal);
	}

	/// <summary>
	/// Every operator but Hold, the premise that planned it, and a change that takes
	/// that premise away without planning anything higher (§3.4, P3).
	/// </summary>
	public static IEnumerable<object[]> Premises()
	{
		yield return new object[] { CommandGoal.Reinforce, (Action<CommandContext>)(c => c.Sense(CommandFact.Task, (byte)CommandTask.Defend)),
			(Action<CommandContext>)(c => c.Sense(CommandFact.Task, (byte)CommandTask.None)) };
		yield return new object[] { CommandGoal.Stage, (Action<CommandContext>)(c => c.Sense(CommandFact.Task, (byte)CommandTask.Attack)),
			(Action<CommandContext>)(c => c.Sense(CommandFact.Task, (byte)CommandTask.None)) };
		yield return new object[] { CommandGoal.Strike, (Action<CommandContext>)(c =>
			{
				c.Sense(CommandFact.Task, (byte)CommandTask.Attack);
				c.Assembled = true;
			}),
			(Action<CommandContext>)(c => c.Sense(CommandFact.Task, (byte)CommandTask.None)) };
		yield return new object[] { CommandGoal.Scout, (Action<CommandContext>)(c => c.Sense(CommandFact.Task, (byte)CommandTask.Recon)),
			(Action<CommandContext>)(c => c.Sense(CommandFact.Task, (byte)CommandTask.None)) };
		yield return new object[] { CommandGoal.Station, (Action<CommandContext>)(c => c.Sense(CommandFact.Task, (byte)CommandTask.Supply)),
			(Action<CommandContext>)(c => c.Sense(CommandFact.Task, (byte)CommandTask.None)) };
		yield return new object[] { CommandGoal.Refill, (Action<CommandContext>)(c => c.Sense(CommandFact.Depleted, true)),
			(Action<CommandContext>)(c => c.Sense(CommandFact.Depleted, false)) };
		yield return new object[] { CommandGoal.Refill, (Action<CommandContext>)(c => c.Sense(CommandFact.Depleted, true)),
			(Action<CommandContext>)(c => c.Sense(CommandFact.Engaged, true)) };
		yield return new object[] { CommandGoal.FallBack, (Action<CommandContext>)(c => c.Sense(CommandFact.FallingBack, true)),
			(Action<CommandContext>)(c => c.Sense(CommandFact.FallingBack, false)) };
	}

	[Theory]
	[MemberData(nameof(Premises))]
	public void EveryOperator_EndsWhenItsPremiseGoes(CommandGoal goal, Action<CommandContext> premise,
		Action<CommandContext> gone)
	{
		var squad = new Squad();
		premise(squad.C);
		squad.Tick();
		Assert.Equal(goal, squad.Goal);

		gone(squad.C);
		squad.Tick();
		Assert.NotEqual(goal, squad.Goal);
	}

	[Fact]
	public void WithoutExecutingConditions_AScoutOutlivesItsTask()
	{
		// P3 on the real domain: Hold is below Recon, so only the executing condition
		// can end a patrol whose task has been taken away.
		var squad = new Squad(executingConditions: false);
		squad.C.Sense(CommandFact.Task, (byte)CommandTask.Recon);
		squad.Tick();
		squad.C.Sense(CommandFact.Task, (byte)CommandTask.None);
		squad.Tick(times: 10);
		Assert.Equal(CommandGoal.Scout, squad.Goal);

		var guarded = new Squad();
		guarded.C.Sense(CommandFact.Task, (byte)CommandTask.Recon);
		guarded.Tick();
		guarded.C.Sense(CommandFact.Task, (byte)CommandTask.None);
		guarded.Tick();
		Assert.Equal(CommandGoal.Hold, guarded.Goal);
	}

	// ---- P7, P8 ------------------------------------------------------------------

	/// <summary>
	/// A round in miniature over 64 units: production arriving, contacts coming and
	/// going round the nodes, nodes changing hands, units hurt and killed, squads
	/// staging, striking, falling back and refilling. Returns a trace of every intent.
	/// </summary>
	private static long Churn(Rig rig, int decisions, List<string> trace = null, Action check = null)
	{
		uint seed = 12345;
		uint Next() => seed = (seed * 1664525u) + 1013904223u;

		long bytes = 0;
		for (int d = 0; d < decisions; d++)
		{
			// Everything the engine would change between decisions, outside the measurement.
			if (rig.Units.Count < 64 && Next() % 3 == 0)
			{
				bool tank = Next() % 7 == 0;
				bool technical = !tank && Next() % 5 == 0;
				bool truck = !tank && !technical && Next() % 13 == 0;
				uint kind = Next() % 9;
				bool gunner = !tank && !technical && !truck && kind == 0;
				bool marksman = !tank && !technical && !truck && kind == 1;
				float cost = tank ? Tank : technical ? Technical : truck ? Truck : gunner ? Gunner : marksman ? Sniper
					: Rifleman;
				rig.Add(new Vector3(Next() % 10, 0f, Next() % 10), cost, tank, technical, truck, cost, gunner, marksman);
			}

			for (int i = 0; i < rig.Units.Count; i++)
			{
				CommandUnit unit = rig.Units[i];
				// As the unit domain walks them: to the staging point while gathering.
				int squad = rig.Commander.SquadOf(unit.Id);
				CommandIntent intent = squad >= 0 ? rig.Commander.IntentOf(squad) : default;
				Vector3 goal = squad < 0 || intent.Goal == CommandGoal.None ? unit.Position
					: intent.Phase == SquadPhase.Gathering ? intent.StagingPoint
					: intent.Point;
				Vector3 step = goal - unit.Position;
				step.Y = 0f;
				unit.Position += step.Length() > 3f ? step.Normalized() * 3f : step;
				unit.Engaging = Next() % 6 == 0;
				unit.Claimed = Next() % 40 == 0;
				if (Next() % 9 == 0)
				{
					unit.Strength *= 0.7f;
				}

				rig.Units[i] = unit;
			}

			rig.Units.RemoveAll(u => u.Strength < 10f);

			// Contacts in spells, with quiet spells of 40 s between them.
			if ((d / 80) % 2 == 0 && Next() % 4 == 0)
			{
				int peer = (int)(Next() % 6) + 1;
				Vector3 near = (Next() % 3) switch { 0 => NearNode, 1 => FarNode, _ => Threat };
				rig.See(peer, near + new Vector3(Next() % 30, 0f, Next() % 30), (Next() % 10 + 1) / 10f);
			}

			if (Next() % 10 == 0)
			{
				rig.Zones.SetHolder((int)(Next() % 2), (NodeHolder)(Next() % 3), Next() % 2 == 0);
			}

			rig.Contacts.Age(rig.Tick);

			long before = GC.GetAllocatedBytesForCurrentThread();
			rig.Decide();
			bytes += GC.GetAllocatedBytesForCurrentThread() - before;
			check?.Invoke();

			if (trace != null)
			{
				for (int s = 0; s < rig.Commander.Capacity; s++)
				{
					CommandIntent intent = rig.Commander.IntentOf(s);
					trace.Add($"{d}:{s}:{intent.Goal}:{intent.Order}:{intent.Point}:{intent.TargetOwnerId}:{rig.Squad(s).Members}:{rig.Squad(s).Lead}");
				}
			}
		}

		return bytes;
	}

	[Fact]
	public void TheSameRound_DecidesTheSame()
	{
		var first = new List<string>();
		var second = new List<string>();
		Churn(new Rig(), 2000, first);
		Churn(new Rig(), 2000, second);

		Assert.Equal(first, second);

		// And the round exercised the tree, not one branch of it.
		var missing = new List<CommandGoal>();
		foreach (CommandGoal goal in new[] { CommandGoal.Reinforce, CommandGoal.Stage, CommandGoal.Strike, CommandGoal.Scout,
			CommandGoal.Station, CommandGoal.Refill, CommandGoal.FallBack, CommandGoal.Hold })
		{
			if (!first.Any(line => line.Contains($":{goal}:")))
			{
				missing.Add(goal);
			}
		}

		Assert.Empty(missing);

		// Combined assaults among them.
		Assert.Contains(first, line => line.Contains($":{CommandGoal.Stage}:") && !line.EndsWith(":-1"));
	}

	[Fact]
	public void AcrossARound_NoSquadHasTwoGunners_AndNeitherAGunnerNorASniperScouts()
	{
		var rig = new Rig();
		int gunnersPlaced = 0;
		int twoGunnersOut = 0;
		int snipersHolding = 0;
		Churn(rig, 2000, check: () =>
		{
			int squadsWithAGunner = 0;
			for (int s = 0; s < rig.Commander.Capacity; s++)
			{
				CommandSquad squad = rig.Squad(s);
				int gunners = 0;
				int marksmen = 0;
				foreach (ushort id in rig.Members(s))
				{
					CommandUnit unit = rig.Units.Find(u => u.Id == id);
					gunners += unit.Gunner ? 1 : 0;
					marksmen += unit.Marksman ? 1 : 0;
				}

				Assert.True(gunners <= 1, $"squad {s} has {gunners} gunners");
				Assert.False(squad.Role is CommandRole.Scout or CommandRole.Supply && gunners + marksmen > 0);
				Assert.False(squad.Role == CommandRole.Garrison && gunners > 0);
				gunnersPlaced += gunners;
				squadsWithAGunner += gunners;
				snipersHolding += marksmen;
			}

			twoGunnersOut += squadsWithAGunner >= 2 ? 1 : 0;
		});

		// And the round put them in squads, more than one gunner at a time.
		Assert.True(gunnersPlaced > 0);
		Assert.True(twoGunnersOut > 0);
		Assert.True(snipersHolding > 0);
	}

	[Fact]
	public void P8_ACommanderDecides_WithoutAllocating()
	{
		// Warmed first: the pooled factory makes an array or a queue the first time a
		// plan needs one of a new size. Contexts are walked through every branch when
		// they are made (CommanderPlanning.CreateContext), so this is short.
		var rig = new Rig();
		Churn(rig, 800);

		Assert.Equal(0, Churn(rig, 4000));
		Assert.Equal(0, rig.Commander.Overflows);
	}

	[Fact]
	public void P8_TheGuardCanFail()
	{
		var rig = new Rig(new DefaultFactory());
		Churn(rig, 200);

		Assert.True(Churn(rig, 400) > 0);
	}
}
