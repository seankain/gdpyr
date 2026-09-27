using System;
using Gdpyr.Sim;
using Gdpyr.Sim.Htn;
using Godot;
using Xunit;

namespace Gdpyr.Tests;

/// <summary>
/// A side's memory of the other side (docs/HTN_BOTS.md §4.3): what is live, what is
/// a ghost, when it is forgotten, who saw it, and what happens when it is full.
/// </summary>
public class ContactMemoryTests
{
	/// <summary>A ground bot's scan interval, which is the ground memory's live window.</summary>
	private const int Window = 10;

	private static ContactMemory Memory(int capacity = 8, int observers = 4) => new(capacity, observers, Window);

	private static KnownContact Tank(ushort unitId, float x = 0f) => new()
	{
		OwnerId = OwnerId.ForUnit(unitId),
		Kind = ContactKind.Armour,
		BulletProof = true,
		Position = new Vector3(x, 0f, 0f),
		HealthFraction = 1f,
	};

	[Fact]
	public void ASightingIsLive_AndSeenByWhoeverReportedIt()
	{
		ContactMemory memory = Memory();

		Assert.True(memory.Observe(100, observer: 2, Tank(7)));

		Assert.Equal(1, memory.Count);
		int index = memory.IndexOf(OwnerId.ForUnit(7));
		Assert.True(memory.IsLive(index, 100));
		Assert.Equal(1, memory.SeenBy(index, 100));
		Assert.Equal(100u, memory.At(index).LastSeenTick);
		Assert.Equal(ContactKind.Armour, memory.At(index).Kind);
	}

	[Fact]
	public void AContactStaysLiveThroughOneFullScanInterval_ThenIsAGhost()
	{
		// Inclusive, so a contact every scan still finds never flickers to a ghost on
		// the tick its observer is due to report it again.
		ContactMemory memory = Memory();
		memory.Observe(100, 0, Tank(7));

		Assert.True(memory.IsLive(0, 100 + Window));
		Assert.False(memory.IsGhost(0, 100 + Window));
		Assert.False(memory.IsLive(0, 101 + Window));
		Assert.True(memory.IsGhost(0, 101 + Window));
	}

	[Fact]
	public void AGhostIsForgottenOnceTheGhostLifetimeHasPassed()
	{
		// Eight seconds, as the strategist's ghost markers last (SimConfig.GhostLifetimeTicks).
		ContactMemory memory = Memory();
		memory.Observe(100, 0, Tank(7));
		uint lastRemembered = 100 + (uint)SimConfig.GhostLifetimeTicks;

		memory.Age(lastRemembered);
		Assert.Equal(1, memory.Count);
		Assert.True(memory.IsRemembered(0, lastRemembered));

		// Exact between two Age passes too: the query does not wait for the sweep.
		Assert.False(memory.IsRemembered(0, lastRemembered + 1));
		Assert.False(memory.IsGhost(0, lastRemembered + 1));
		Assert.Equal(0, memory.GhostCount(lastRemembered + 1));

		memory.Age(lastRemembered + 1);
		Assert.Equal(0, memory.Count);
	}

	[Fact]
	public void ASecondSighting_UpdatesTheContactInPlace()
	{
		ContactMemory memory = Memory();
		memory.Observe(100, 0, Tank(7, x: 1f));

		KnownContact moved = Tank(7, x: 5f);
		moved.HealthFraction = 0.4f;
		memory.Observe(110, 1, moved);

		Assert.Equal(1, memory.Count);
		Assert.Equal(5f, memory.At(0).Position.X);
		Assert.Equal(0.4f, memory.At(0).HealthFraction);
		Assert.Equal(110u, memory.At(0).LastSeenTick);
	}

	[Fact]
	public void SeenBy_CountsDistinctObservers_WithinTheLiveWindow()
	{
		ContactMemory memory = Memory();
		memory.Observe(100, 0, Tank(7));
		memory.Observe(100, 0, Tank(7));
		memory.Observe(104, 3, Tank(7));

		Assert.Equal(2, memory.SeenBy(0, 104));

		// Observer 0 stops reporting it; observer 3 goes on.
		memory.Observe(114, 3, Tank(7));
		Assert.Equal(1, memory.SeenBy(0, 114));
	}

	[Fact]
	public void AnObserverOutsideTheRange_UpdatesTheContact_AndIsNotCounted()
	{
		ContactMemory memory = Memory(observers: 2);

		Assert.True(memory.Observe(100, observer: 5, Tank(7)));
		Assert.True(memory.Observe(100, observer: -1, Tank(8)));

		Assert.Equal(2, memory.Count);
		Assert.True(memory.IsLive(0, 100));
		Assert.Equal(0, memory.SeenBy(0, 100));
	}

	[Fact]
	public void ASightingOfNobody_IsRefused()
	{
		ContactMemory memory = Memory();
		KnownContact nobody = Tank(7);
		nobody.OwnerId = OwnerId.None;

		Assert.False(memory.Observe(100, 0, nobody));
		Assert.Equal(0, memory.Count);
		Assert.Equal(0, memory.Overflows);
	}

	[Fact]
	public void AMemoryFullOfLiveContacts_RefusesANewOne_AndKeepsWhatItHas()
	{
		ContactMemory memory = Memory(capacity: 2);
		memory.Observe(100, 0, Tank(1));
		memory.Observe(100, 0, Tank(2));

		Assert.False(memory.Observe(100 + Window, 0, Tank(3)));

		Assert.Equal(1, memory.Overflows);
		Assert.Equal(0, memory.Evictions);
		Assert.Equal(2, memory.Count);
		Assert.Equal(-1, memory.IndexOf(OwnerId.ForUnit(3)));
	}

	[Fact]
	public void AFullMemory_GivesTheStalestGhostsPlaceToANewContact()
	{
		ContactMemory memory = Memory(capacity: 3);
		memory.Observe(100, 0, Tank(1));
		memory.Observe(120, 0, Tank(2));
		memory.Observe(200, 0, Tank(3));

		// Unit 1 is the stalest ghost; 2 is a ghost too, but a younger one.
		Assert.True(memory.Observe(200, 1, Tank(4)));

		Assert.Equal(1, memory.Evictions);
		Assert.Equal(-1, memory.IndexOf(OwnerId.ForUnit(1)));
		Assert.NotEqual(-1, memory.IndexOf(OwnerId.ForUnit(2)));

		// The newcomer does not inherit the evicted contact's observers.
		int newcomer = memory.IndexOf(OwnerId.ForUnit(4));
		Assert.Equal(1, memory.SeenBy(newcomer, 200));
	}

	[Fact]
	public void Forgetting_KeepsEveryOtherContactsObserversWithIt()
	{
		// Removal moves the last entry into the hole; its reports have to move too.
		ContactMemory memory = Memory();
		memory.Observe(100, 0, Tank(1));
		memory.Observe(100, 1, Tank(2));
		memory.Observe(100, 2, Tank(3));
		memory.Observe(100, 3, Tank(3));

		Assert.True(memory.Forget(OwnerId.ForUnit(1)));
		Assert.False(memory.Forget(OwnerId.ForUnit(1)));

		Assert.Equal(2, memory.Count);
		Assert.Equal(2, memory.SeenBy(memory.IndexOf(OwnerId.ForUnit(3)), 100));
		Assert.Equal(1, memory.SeenBy(memory.IndexOf(OwnerId.ForUnit(2)), 100));
	}

	[Fact]
	public void LiveAndGhostCounts_PartitionWhatIsRemembered()
	{
		ContactMemory memory = Memory();
		memory.Observe(100, 0, Tank(1));
		memory.Observe(150, 0, Tank(2));
		memory.Observe(155, 0, Tank(3));

		Assert.Equal(2, memory.LiveCount(155));
		Assert.Equal(1, memory.GhostCount(155));
	}

	[Fact]
	public void Clearing_ForgetsEverything_AndZeroesTheCounters()
	{
		ContactMemory memory = Memory(capacity: 1);
		memory.Observe(100, 0, Tank(1));
		memory.Observe(100, 0, Tank(2));
		Assert.Equal(1, memory.Overflows);

		memory.Clear();

		Assert.Equal(0, memory.Count);
		Assert.Equal(0, memory.Overflows);
		Assert.True(memory.Observe(200, 0, Tank(2)));
		Assert.Equal(1, memory.SeenBy(0, 200));
	}

	[Fact]
	public void ObservingAndAgeing_AllocateNothing()
	{
		// §4.2 rule 3: every board is updated on the tick, so none may allocate there.
		ContactMemory memory = new(SimConfig.MaxUnits + SimConfig.MaxStructures, BotRoster.MaxBots, Window);
		Run(memory, 0);

		long before = GC.GetAllocatedBytesForCurrentThread();
		Run(memory, 10_000);
		Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);

		static void Run(ContactMemory memory, uint start)
		{
			for (uint t = start; t < start + 2_000; t++)
			{
				memory.Observe(t, (int)(t % BotRoster.MaxBots), Tank((ushort)(1 + (t % 150))));
				memory.Age(t);
				memory.LiveCount(t);
				memory.GhostCount(t);
				memory.SeenBy(0, t);
			}
		}
	}
}
