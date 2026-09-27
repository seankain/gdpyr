using System;
using Gdpyr.Sim;
using Gdpyr.Sim.Htn;
using Godot;
using Xunit;

namespace Gdpyr.Tests;

/// <summary>
/// A side's picture of the map's places (docs/HTN_BOTS.md §4.3): the census of
/// both sides' strength at each, armour against explosives, and which one the side
/// has gone longest without seeing.
/// </summary>
public class ZoneBoardTests
{
	private const float Radius = 10f;

	/// <summary>Round numbers rather than the catalog's: the arithmetic is the point.</summary>
	private static readonly ThreatWeights Weights = new(infantry: 50f, armour: 300f, armedStructure: 200f,
		player: 100f, unansweredArmourScale: 3f);

	private static ZoneBoard Board()
	{
		var board = new ZoneBoard();
		board.Add(ZoneKind.ResourceNode, 0, new Vector3(0f, 0f, 0f), Radius);
		board.Add(ZoneKind.ResourceNode, 1, new Vector3(100f, 0f, 0f), Radius);
		board.Add(ZoneKind.Barracks, 0, new Vector3(0f, 0f, 100f), Radius);
		return board;
	}

	private static KnownContact Contact(int ownerId, ContactKind kind, float x, float health = 1f) => new()
	{
		OwnerId = ownerId,
		Kind = kind,
		BulletProof = kind is ContactKind.Armour or ContactKind.ArmedStructure,
		Position = new Vector3(x, 0f, 0f),
		HealthFraction = health,
	};

	private static ContactMemory Memory() => new(capacity: 16, observers: 1, liveWindowTicks: 10);

	[Fact]
	public void AFullBoardRefusesAPlace()
	{
		var board = new ZoneBoard(capacity: 1);

		Assert.Equal(0, board.Add(ZoneKind.ResourceNode, 0, Vector3.Zero, Radius));
		Assert.Equal(-1, board.Add(ZoneKind.ResourceNode, 1, Vector3.Zero, Radius));
		Assert.Equal(1, board.Overflows);
		Assert.Equal(1, board.Count);
	}

	[Fact]
	public void TheCensusCountsFriendsWhereTheyStand_AndStartsFromNothing()
	{
		ZoneBoard board = Board();
		board.BeginCensus();
		board.AddFriendly(new Vector3(Radius, 0f, 0f), 50f);
		board.AddFriendly(new Vector3(Radius + 0.1f, 0f, 0f), 50f);
		board.AddFriendly(new Vector3(100f, 0f, 3f), 300f);

		Assert.Equal(50f, board.At(0).FriendlyStrength);
		Assert.Equal(300f, board.At(1).FriendlyStrength);
		Assert.Equal(0f, board.At(2).FriendlyStrength);

		board.BeginCensus();
		Assert.Equal(0f, board.At(0).FriendlyStrength);
	}

	[Fact]
	public void Enemies_AreWorthTheirWeightTimesTheirHealth_GhostsIncluded()
	{
		ContactMemory memory = Memory();
		memory.Observe(100, 0, Contact(OwnerId.ForPeer(5), ContactKind.Player, 1f));
		memory.Observe(150, 0, Contact(OwnerId.ForPeer(6), ContactKind.Player, 2f, health: 0.5f));
		memory.Observe(150, 0, Contact(OwnerId.ForPeer(7), ContactKind.Player, 50f));

		ZoneBoard board = Board();
		board.BeginCensus();
		board.AddEnemies(memory, 150, Weights, explosives: 0);

		// Peer 5 is a ghost by 150 and still counts; peer 7 is between the nodes.
		Assert.Equal(150f, board.At(0).EnemyStrength);
		Assert.Equal(0f, board.At(1).EnemyStrength);
	}

	[Fact]
	public void AForgottenContact_IsNotCounted()
	{
		ContactMemory memory = Memory();
		memory.Observe(100, 0, Contact(OwnerId.ForPeer(5), ContactKind.Player, 1f));

		ZoneBoard board = Board();
		board.BeginCensus();
		board.AddEnemies(memory, 100 + (uint)SimConfig.GhostLifetimeTicks + 1, Weights, explosives: 0);

		Assert.Equal(0f, board.At(0).EnemyStrength);
	}

	[Theory]
	[InlineData(0, 1800f)]
	[InlineData(1, 1200f)]
	[InlineData(2, 600f)]
	[InlineData(5, 600f)]
	public void Armour_IsCountedAgainstTheExplosivesTheSideHas(int explosives, float expected)
	{
		// Two tanks at 300: answered, 600; unanswered, three times that; one launcher
		// answers half of them.
		ContactMemory memory = Memory();
		memory.Observe(100, 0, Contact(OwnerId.ForUnit(1), ContactKind.Armour, 1f));
		memory.Observe(100, 0, Contact(OwnerId.ForUnit(2), ContactKind.Armour, 2f));

		ZoneBoard board = Board();
		board.BeginCensus();
		board.AddEnemies(memory, 100, Weights, explosives);

		Assert.Equal(expected, board.At(0).EnemyStrength, 3);
	}

	[Fact]
	public void Infantry_IsNotScaledByAShortageOfExplosives()
	{
		ContactMemory memory = Memory();
		memory.Observe(100, 0, Contact(OwnerId.ForUnit(1), ContactKind.Infantry, 1f));
		memory.Observe(100, 0, Contact(OwnerId.ForUnit(2), ContactKind.Armour, 2f));

		ZoneBoard board = Board();
		board.BeginCensus();
		board.AddEnemies(memory, 100, Weights, explosives: 0);

		Assert.Equal(50f + 900f, board.At(0).EnemyStrength, 3);
	}

	[Fact]
	public void Holders_AreKeptAcrossACensus()
	{
		ZoneBoard board = Board();
		board.SetHolder(1, NodeHolder.GroundForce, contested: true);
		board.BeginCensus();

		Assert.Equal(NodeHolder.GroundForce, board.At(1).Holder);
		Assert.True(board.At(1).Contested);
	}

	[Fact]
	public void ObservingMarksWhatTheSensorsCover()
	{
		ZoneBoard board = Board();
		var sensors = new VisionField(4);
		sensors.Add(new Vector3(95f, 0f, 0f), 20f);

		Assert.Equal(uint.MaxValue, board.TicksSinceObserved(1, 100));

		board.Observe(sensors, 100);

		Assert.Equal(30u, board.TicksSinceObserved(1, 130));
		Assert.Equal(uint.MaxValue, board.TicksSinceObserved(0, 130));
	}

	[Fact]
	public void TheStalestZone_IsOneNeverSeen_ThenTheOldest_ThenTheLowerIndex()
	{
		ZoneBoard board = Board();
		board.Add(ZoneKind.ResourceNode, 2, new Vector3(200f, 0f, 0f), Radius);

		Assert.Equal(0, board.Stalest(ZoneKind.ResourceNode, 0));

		board.MarkObserved(0, 50);
		board.MarkObserved(3, 50);
		Assert.Equal(1, board.Stalest(ZoneKind.ResourceNode, 100));

		board.MarkObserved(1, 60);
		Assert.Equal(0, board.Stalest(ZoneKind.ResourceNode, 100));

		Assert.Equal(2, board.Stalest(ZoneKind.Barracks, 100));
		Assert.Equal(-1, board.Stalest(ZoneKind.GroundSpawn, 100));
	}

	[Fact]
	public void TheCensus_AllocatesNothing()
	{
		ContactMemory memory = new(SimConfig.MaxUnits + SimConfig.MaxStructures, 1, 10);
		for (int i = 0; i < SimConfig.MaxUnits; i++)
		{
			memory.Observe(0, 0, Contact(OwnerId.ForUnit((ushort)(i + 1)), (ContactKind)(i % 3), i));
		}

		var board = new ZoneBoard();
		for (int i = 0; i < board.Capacity; i++)
		{
			board.Add((ZoneKind)(i % 3), i, new Vector3(i * 5f, 0f, 0f), Radius);
		}

		var sensors = new VisionField(SimConfig.MaxUnits);
		for (int i = 0; i < SimConfig.MaxUnits; i++)
		{
			sensors.Add(new Vector3(i * 3f, 0f, 0f), 20f);
		}

		Run();
		long before = GC.GetAllocatedBytesForCurrentThread();
		Run();
		Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);

		void Run()
		{
			for (uint t = 0; t < 100; t++)
			{
				board.BeginCensus();
				for (int i = 0; i < SimConfig.MaxUnits; i++)
				{
					board.AddFriendly(new Vector3(i, 0f, 0f), 50f);
				}

				board.AddEnemies(memory, t, Weights, explosives: 1);
				board.Observe(sensors, t);
				board.Stalest(ZoneKind.ResourceNode, t);
			}
		}
	}
}
