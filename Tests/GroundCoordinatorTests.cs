using System;
using Gdpyr.Sim;
using Gdpyr.Sim.Htn;
using Godot;
using Xunit;

namespace Gdpyr.Tests;

/// <summary>
/// The ground force's team layer (docs/HTN_BOTS.md §5.1): deniers on the
/// strategist's nodes, one locker runner while armour is known, one sweeper for a
/// stale node, focus targets and buddy pairs — and people counted, never given a role.
/// </summary>
public class GroundCoordinatorTests
{
	private static readonly Vector3 Spawn = new(3.5f, 0.5f, 7.5f);
	private static readonly Vector3 Locker = new(8f, 0.5f, 11f);

	/// <summary>The test map's three nodes: two near the spawn, one far.</summary>
	private static GroundNode[] Nodes(NodeHolder a, NodeHolder b, NodeHolder c) => new[]
	{
		new GroundNode { Position = new Vector3(-30f, 0.5f, -15f), RadiusMeters = 10f, Holder = a },
		new GroundNode { Position = new Vector3(0f, 0.5f, -35f), RadiusMeters = 12f, Holder = b },
		new GroundNode { Position = new Vector3(-25f, 0.5f, -100f), RadiusMeters = 10f, Holder = c },
	};

	private static GroundMember Bot(int slot, Vector3 at, bool explosive = false, bool alive = true) => new()
	{
		Id = BotRoster.PeerIdFor(slot),
		Slot = slot,
		Alive = alive,
		Position = at,
		Explosive = explosive,
		SensorRadiusMeters = 80f,
	};

	private static GroundMember Person(int id, Vector3 at, bool explosive = false) => new()
	{
		Id = id,
		Slot = -1,
		Alive = true,
		Position = at,
		Explosive = explosive,
		SensorRadiusMeters = 80f,
	};

	/// <summary>Five bots and one person at the spawn, as the default game mode fields them; slot 2 has a launcher.</summary>
	private static GroundMember[] Team() => new[]
	{
		Bot(0, new Vector3(-2f, 0.5f, 7.5f)),
		Bot(1, new Vector3(1f, 0.5f, 7.5f)),
		Bot(2, new Vector3(5f, 0.5f, 7.5f), explosive: true),
		Bot(3, new Vector3(9f, 0.5f, 7.5f)),
		Bot(4, new Vector3(9.5f, 0.5f, 10f)),
		Person(1, new Vector3(0f, 0.5f, 5f)),
	};

	private static GroundCoordinator Coordinator() => new(BotRoster.MaxBots, GroundCoordinatorTraits.Default);

	private static GroundOrders[] Assign(GroundCoordinator coordinator, uint tick, GroundMember[] members,
		GroundNode[] nodes, ContactMemory contacts = null, bool hasLocker = true)
	{
		var orders = new GroundOrders[members.Length];
		coordinator.Assign(tick, members, nodes, contacts, Spawn, hasLocker, Locker, orders);
		return orders;
	}

	private static int Count(GroundOrders[] orders, GroundRole role)
	{
		int n = 0;
		foreach (GroundOrders order in orders)
		{
			n += order.Role == role ? 1 : 0;
		}

		return n;
	}

	private static ContactMemory Remembering(uint tick, params (int ownerId, bool bulletProof, Vector3 at, float health)[] seen)
	{
		var memory = new ContactMemory(SimConfig.MaxUnits, BotRoster.MaxBots, SimConfig.UnitTargetRefreshTicks);
		foreach ((int ownerId, bool bulletProof, Vector3 at, float health) in seen)
		{
			memory.Observe(tick, 0, new KnownContact
			{
				OwnerId = ownerId,
				Kind = bulletProof ? ContactKind.Armour : ContactKind.Infantry,
				BulletProof = bulletProof,
				Position = at,
				HealthFraction = health,
			});
		}

		return memory;
	}

	// ---- deniers -----------------------------------------------------------

	[Fact]
	public void AThirdOfTheTeam_PeopleCounted_DenyTheStrategistsNodesNearestTheSpawn()
	{
		GroundOrders[] orders = Assign(Coordinator(), 100, Team(),
			Nodes(NodeHolder.Strategist, NodeHolder.Strategist, NodeHolder.Strategist));

		Assert.Equal(2, Count(orders, GroundRole.Denier));
		Assert.Contains(orders, o => o.Role == GroundRole.Denier && o.Zone == 0);
		Assert.Contains(orders, o => o.Role == GroundRole.Denier && o.Zone == 1);
		Assert.DoesNotContain(orders, o => o.Zone == 2);
	}

	[Fact]
	public void TheStrategistsNodesComeBeforeNeutralOnes_AndTheGroundForcesNeverNeedDenying()
	{
		GroundOrders[] orders = Assign(Coordinator(), 100, Team(),
			Nodes(NodeHolder.GroundForce, NodeHolder.Neutral, NodeHolder.Strategist));

		Assert.Contains(orders, o => o.Role == GroundRole.Denier && o.Zone == 2);
		Assert.Contains(orders, o => o.Role == GroundRole.Denier && o.Zone == 1);
		Assert.DoesNotContain(orders, o => o.Zone == 0);
	}

	[Fact]
	public void APerson_IsNeverGivenARole()
	{
		GroundMember[] members =
		{
			Person(1, new Vector3(-30f, 0.5f, -15f)),
			Person(2, new Vector3(-30f, 0.5f, -14f)),
			Bot(0, new Vector3(50f, 0.5f, 50f)),
		};

		GroundOrders[] orders = Assign(Coordinator(), 100, members,
			Nodes(NodeHolder.Strategist, NodeHolder.Neutral, NodeHolder.Neutral));

		Assert.Equal(GroundRole.Assault, orders[0].Role);
		Assert.Equal(GroundRole.Assault, orders[1].Role);
		Assert.Equal(GroundRole.Denier, orders[2].Role);
		Assert.Equal(0, orders[2].Zone);
	}

	[Fact]
	public void ADeadBot_IsNotSentAnywhere()
	{
		GroundMember[] members = Team();
		for (int i = 0; i < members.Length; i++)
		{
			members[i].Alive = !members[i].IsBot;
		}

		GroundOrders[] orders = Assign(Coordinator(), 100, members,
			Nodes(NodeHolder.Strategist, NodeHolder.Strategist, NodeHolder.Strategist));

		Assert.Equal(0, Count(orders, GroundRole.Denier));
	}

	[Fact]
	public void ADenierKeepsItsNode_WhenAnotherBotComesNearer()
	{
		GroundCoordinator coordinator = Coordinator();
		GroundMember[] members = Team();
		GroundNode[] nodes = Nodes(NodeHolder.Strategist, NodeHolder.Neutral, NodeHolder.Neutral);

		GroundOrders[] first = Assign(coordinator, 100, members, nodes);
		int denier = Array.FindIndex(first, o => o.Zone == 0);
		Assert.True(denier >= 0);

		int other = denier == 1 ? 3 : 1;
		members[other].Position = new Vector3(-30f, 0.5f, -15f);
		GroundOrders[] second = Assign(coordinator, 130, members, nodes);

		Assert.Equal(0, second[denier].Zone);
		Assert.NotEqual(0, second[other].Zone);
	}

	[Fact]
	public void TheHoldPoint_IsOnTheNodesSpawnSide_AndTheSameEveryTime()
	{
		GroundCoordinator coordinator = Coordinator();
		GroundNode node = Nodes(NodeHolder.Strategist, NodeHolder.Neutral, NodeHolder.Neutral)[1];
		Vector3 toSpawn = (Spawn - node.Position) with { Y = 0f };

		for (int slot = 0; slot < BotRoster.MaxBots; slot++)
		{
			Vector3 point = coordinator.HoldPoint(node, BotRoster.PeerIdFor(slot), Spawn);
			Vector3 off = (point - node.Position) with { Y = 0f };

			Assert.InRange(off.Length(), 0.39f * node.RadiusMeters, 0.41f * node.RadiusMeters);
			Assert.True(off.Normalized().Dot(toSpawn.Normalized()) >= MathF.Cos(GroundCoordinator.HoldArcRadians) - 1e-4f);
			Assert.Equal(point, coordinator.HoldPoint(node, BotRoster.PeerIdFor(slot), Spawn));
		}
	}

	// ---- the locker runner -------------------------------------------------

	private static readonly Vector3 Tank = new(8f, 1f, -35.5f);

	[Fact]
	public void ArmourKnown_AndOneLauncherInHand_TheSmallArmsBotNearestTheLockerRuns()
	{
		ContactMemory armour = Remembering(100, (OwnerId.ForUnit(9), true, Tank, 1f));
		GroundOrders[] orders = Assign(Coordinator(), 100, Team(),
			Nodes(NodeHolder.GroundForce, NodeHolder.GroundForce, NodeHolder.GroundForce), armour);

		Assert.Equal(1, Count(orders, GroundRole.LockerRunner));
		Assert.Equal(GroundRole.LockerRunner, orders[4].Role);
	}

	[Fact]
	public void NoRunner_WithoutArmour_WithoutALocker_OrWithTwoLaunchersInHand()
	{
		GroundNode[] nodes = Nodes(NodeHolder.GroundForce, NodeHolder.GroundForce, NodeHolder.GroundForce);
		ContactMemory armour = Remembering(100, (OwnerId.ForUnit(9), true, Tank, 1f));
		ContactMemory infantry = Remembering(100, (OwnerId.ForUnit(9), false, Tank, 1f));

		Assert.Equal(0, Count(Assign(Coordinator(), 100, Team(), nodes, infantry), GroundRole.LockerRunner));
		Assert.Equal(0, Count(Assign(Coordinator(), 100, Team(), nodes, armour, hasLocker: false),
			GroundRole.LockerRunner));

		// A person's launcher counts: two are in hand.
		GroundMember[] members = Team();
		members[5].Explosive = true;
		Assert.Equal(0, Count(Assign(Coordinator(), 100, members, nodes, armour), GroundRole.LockerRunner));
	}

	[Fact]
	public void ARunnerWhoseTripFailed_IsLeftOffTheJob_ForTheRetryTime()
	{
		GroundCoordinator coordinator = Coordinator();
		GroundNode[] nodes = Nodes(NodeHolder.GroundForce, NodeHolder.GroundForce, NodeHolder.GroundForce);
		GroundMember[] members = Team();
		ContactMemory armour = Remembering(100, (OwnerId.ForUnit(9), true, Tank, 1f));

		Assert.Equal(GroundRole.LockerRunner, Assign(coordinator, 100, members, nodes, armour)[4].Role);

		members[4].FailedLockerTrips = 1;
		armour = Remembering(130, (OwnerId.ForUnit(9), true, Tank, 1f));
		GroundOrders[] barred = Assign(coordinator, 130, members, nodes, armour);
		Assert.NotEqual(GroundRole.LockerRunner, barred[4].Role);
		Assert.Equal(1, Count(barred, GroundRole.LockerRunner));

		// Once the time is up it is the nearest again. The bot that took over has died,
		// which is what frees the job: a runner keeps it while it is still running.
		uint later = 130 + (uint)GroundCoordinatorTraits.Default.LockerRetryTicks;
		armour = Remembering(later, (OwnerId.ForUnit(9), true, Tank, 1f));
		members[3].Alive = false;
		Assert.Equal(GroundRole.LockerRunner, Assign(coordinator, later, members, nodes, armour)[4].Role);
	}

	[Fact]
	public void ADenierIsNotAlsoTheRunner()
	{
		GroundMember[] members =
		{
			Bot(0, Locker),
			Bot(1, new Vector3(50f, 0.5f, 50f)),
			Bot(2, new Vector3(60f, 0.5f, 50f)),
		};
		ContactMemory armour = Remembering(100, (OwnerId.ForUnit(9), true, Tank, 1f));
		GroundNode[] nodes =
		{
			new GroundNode { Position = Locker + new Vector3(0f, 0f, -12f), RadiusMeters = 10f, Holder = NodeHolder.Strategist },
		};

		GroundOrders[] orders = Assign(Coordinator(), 100, members, nodes, armour);

		Assert.Equal(GroundRole.Denier, orders[0].Role);
		Assert.Equal(1, Count(orders, GroundRole.LockerRunner));
		Assert.NotEqual(GroundRole.LockerRunner, orders[0].Role);
	}

	// ---- recon -------------------------------------------------------------

	[Fact]
	public void ANodeNobodyHasBeenNear_IsSweptByTheNearestFreeBot_OnlyOnceItIsStale()
	{
		GroundCoordinator coordinator = Coordinator();
		GroundNode[] nodes = Nodes(NodeHolder.GroundForce, NodeHolder.GroundForce, NodeHolder.GroundForce);
		GroundMember[] members = Team();
		members[1].Position = nodes[1].Position;

		// Never seen is the stalest there is: the far node is swept from the start.
		GroundOrders[] orders = Assign(coordinator, 100, members, nodes);
		int sweeper = Array.FindIndex(orders, o => o.SweepZone >= 0);
		Assert.True(sweeper >= 0);
		Assert.Equal(2, orders[sweeper].SweepZone);
		Assert.Equal(nodes[2].Position, orders[sweeper].SweepPoint);
		Assert.Single(orders, o => o.SweepZone >= 0);

		// Somebody goes and looks: nothing is stale for SweepStaleTicks.
		members[sweeper].Position = nodes[2].Position;
		Assert.DoesNotContain(Assign(coordinator, 130, members, nodes), o => o.SweepZone >= 0);

		members[sweeper].Position = nodes[1].Position;
		uint stale = 130 + (uint)GroundCoordinatorTraits.Default.SweepStaleTicks;
		Assert.Contains(Assign(coordinator, stale, members, nodes), o => o.SweepZone == 2);
	}

	[Fact]
	public void APersonsEyesCountForTheSweep()
	{
		GroundCoordinator coordinator = Coordinator();
		GroundNode[] nodes = Nodes(NodeHolder.GroundForce, NodeHolder.GroundForce, NodeHolder.GroundForce);
		GroundMember[] members = Team();
		members[1].Position = nodes[1].Position;
		members[5].Position = nodes[2].Position;

		Assert.DoesNotContain(Assign(coordinator, 100, members, nodes), o => o.SweepZone >= 0);
	}

	// ---- focus and buddies -------------------------------------------------

	[Fact]
	public void TheFocus_IsTheWeakestLiveContactInSight_ThatTheBotCanHurt()
	{
		Vector3 near = new(0f, 1f, -20f);
		ContactMemory contacts = Remembering(100,
			(OwnerId.ForUnit(1), false, near, 0.9f),
			(OwnerId.ForUnit(2), false, near + new Vector3(3f, 0f, 0f), 0.4f),
			(OwnerId.ForUnit(3), true, near, 0.1f),
			(OwnerId.ForUnit(4), false, new Vector3(0f, 1f, -200f), 0.05f));

		GroundOrders[] orders = Assign(Coordinator(), 100, Team(),
			Nodes(NodeHolder.GroundForce, NodeHolder.GroundForce, NodeHolder.GroundForce), contacts);

		Assert.Equal(OwnerId.ForUnit(2), orders[0].FocusOwnerId);
		Assert.Equal(OwnerId.ForUnit(3), orders[2].FocusOwnerId);
		Assert.Equal(0, orders[5].FocusOwnerId);
	}

	[Fact]
	public void AGhost_IsNobodysFocus()
	{
		ContactMemory contacts = Remembering(100, (OwnerId.ForUnit(1), false, new Vector3(0f, 1f, -20f), 0.5f));

		GroundOrders[] orders = Assign(Coordinator(), 200, Team(),
			Nodes(NodeHolder.GroundForce, NodeHolder.GroundForce, NodeHolder.GroundForce), contacts);

		Assert.All(orders, o => Assert.Equal(0, o.FocusOwnerId));
	}

	[Fact]
	public void Buddies_AreTheClosestPairs_PeopleIncluded()
	{
		GroundMember[] members =
		{
			Bot(0, new Vector3(0f, 0f, 0f)),
			Bot(1, new Vector3(50f, 0f, 0f)),
			Person(1, new Vector3(2f, 0f, 0f)),
			Bot(2, new Vector3(53f, 0f, 0f)),
			Bot(3, new Vector3(200f, 0f, 0f)),
		};

		GroundOrders[] orders = Assign(Coordinator(), 100, members,
			Nodes(NodeHolder.GroundForce, NodeHolder.GroundForce, NodeHolder.GroundForce));

		Assert.Equal(1, orders[0].BuddyId);
		Assert.Equal(members[0].Id, orders[2].BuddyId);
		Assert.Equal(members[3].Id, orders[1].BuddyId);
		Assert.Equal(members[1].Id, orders[3].BuddyId);
		Assert.Equal(0, orders[4].BuddyId);
		Assert.Equal(members[2].Position, orders[0].BuddyPosition);
	}

	// ---- determinism and allocation ----------------------------------------

	[Fact]
	public void TheSameTeam_GetsTheSameOrders()
	{
		ContactMemory armour = Remembering(100, (OwnerId.ForUnit(9), true, Tank, 1f));
		GroundNode[] nodes = Nodes(NodeHolder.Strategist, NodeHolder.Neutral, NodeHolder.Strategist);

		Assert.Equal(Assign(Coordinator(), 100, Team(), nodes, armour), Assign(Coordinator(), 100, Team(), nodes, armour));
	}

	[Fact]
	public void Assigning_AllocatesNothing()
	{
		GroundCoordinator coordinator = Coordinator();
		GroundMember[] members = new GroundMember[BotRoster.MaxBots];
		for (int i = 0; i < members.Length; i++)
		{
			members[i] = Bot(i, new Vector3(i * 3f, 0.5f, i % 5), explosive: i % 3 == 2);
		}

		GroundNode[] nodes = Nodes(NodeHolder.Strategist, NodeHolder.Neutral, NodeHolder.Strategist);
		var orders = new GroundOrders[members.Length];
		ContactMemory contacts = Remembering(100,
			(OwnerId.ForUnit(1), false, new Vector3(0f, 1f, -20f), 0.9f),
			(OwnerId.ForUnit(9), true, Tank, 1f));

		coordinator.Assign(100, members, nodes, contacts, Spawn, true, Locker, orders);
		long before = GC.GetAllocatedBytesForCurrentThread();
		for (uint t = 0; t < 100; t++)
		{
			coordinator.Assign(100 + t, members, nodes, contacts, Spawn, true, Locker, orders);
		}

		Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
	}
}
