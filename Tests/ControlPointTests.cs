using System;
using Gdpyr.Sim;
using Gdpyr.Sim.Htn;
using Godot;
using Xunit;

namespace Gdpyr.Tests;

/// <summary>
/// Taking a control point (docs/HTN_BOTS.md §5.2): the presence rule the economy and
/// the unit planner share, who of the units taking a point must stand on it, and how
/// far out on it their posts may be.
/// </summary>
public class ControlPointTests
{
	private static readonly Vector3 Centre = new(-30f, 0.5f, -15f);
	private const float Radius = 10f;

	// ---- presence ------------------------------------------------------------

	[Theory]
	[InlineData(0f, 0f, true)]
	[InlineData(10f, 0f, true)]
	[InlineData(10.01f, 0f, false)]
	[InlineData(6f, 8f, true)]
	[InlineData(7.2f, 7.2f, false)]
	public void APadIsFlat_AndItsEdgeIsOnIt(float x, float z, bool expected)
	{
		Assert.Equal(expected, CapturePad.Covers(Centre, Radius, Centre + new Vector3(x, 0f, z)));
	}

	[Theory]
	[InlineData(3.9f, true)]
	[InlineData(-3.9f, true)]
	[InlineData(4.1f, false)]
	public void ABodyAboveOrBelowThePad_IsOnItWithinFourMetres(float height, bool expected)
	{
		Assert.Equal(expected, CapturePad.Covers(Centre, Radius, Centre + new Vector3(2f, height, 0f)));
	}

	// ---- who stands on the point ---------------------------------------------

	private static Neighbour On(int id, float x = 0f) => new(id, Centre + new Vector3(x, 0f, 0f), 1f, false);

	private static Neighbour Off(int id) => new(id, Centre + new Vector3(Radius + 5f, 0f, 0f), 1f, false);

	private static bool Holds(int id, Vector3 at, params Neighbour[] friends) =>
		ControlPoint.Holds(id, at, Centre, Radius, friends, holders: 2);

	[Fact]
	public void WithNobodyOnThePoint_EveryoneMustGoOntoIt()
	{
		Neighbour[] friends = { Off(1), Off(2), Off(3), Off(4) };
		foreach (Neighbour friend in friends)
		{
			Assert.True(Holds(friend.Id, friend.Position, friends));
		}
	}

	[Fact]
	public void WithTwoOnThePoint_TheRestMayFightFromBesideIt()
	{
		Neighbour[] friends = { On(7, 1f), On(9, -1f), Off(3), Off(4) };

		Assert.False(Holds(3, friends[2].Position, friends));
		Assert.False(Holds(4, friends[3].Position, friends));
		Assert.True(Holds(7, friends[0].Position, friends));
		Assert.True(Holds(9, friends[1].Position, friends));
	}

	[Fact]
	public void OnThePoint_TheTwoLowestIdsHold_AndTheRestMayLeave()
	{
		Neighbour[] friends = { On(5, 1f), On(2, 2f), On(8, 3f), On(3, 4f) };

		Assert.True(Holds(2, friends[1].Position, friends));
		Assert.True(Holds(3, friends[3].Position, friends));
		Assert.False(Holds(5, friends[0].Position, friends));
		Assert.False(Holds(8, friends[2].Position, friends));
	}

	[Fact]
	public void AUnitThatLeavesThePoint_DoesNotChangeWhoHoldsIt()
	{
		// 5 and 8 may leave; once off, 2 and 3 are still the two on it.
		Neighbour[] friends = { Off(5), On(2, 2f), Off(8), On(3, 4f) };

		Assert.True(Holds(2, friends[1].Position, friends));
		Assert.True(Holds(3, friends[3].Position, friends));
		Assert.False(Holds(5, friends[0].Position, friends));
		Assert.False(Holds(8, friends[2].Position, friends));
	}

	[Fact]
	public void AHolderThatGoes_ForAnyReason_IsReplacedFromBesideThePoint()
	{
		// Holder 2 has fallen back, or gone to heal, or died: only 3 is on the point,
		// so the units beside it must come onto it.
		Neighbour[] friends = { Off(5), Off(2), Off(8), On(3, 4f) };

		Assert.True(Holds(5, friends[0].Position, friends));
		Assert.True(Holds(8, friends[2].Position, friends));
		Assert.True(Holds(2, friends[1].Position, friends));
	}

	[Fact]
	public void AUnitCountsItselfOnlyOnce()
	{
		// The survey list holds every friendly unit, the asking one included.
		Neighbour[] friends = { On(4, 1f), On(6, 2f) };
		Assert.True(Holds(4, friends[0].Position, friends));
		Assert.True(Holds(6, friends[1].Position, friends));
	}

	[Fact]
	public void Holders_AllocatesNothing()
	{
		Neighbour[] friends = { On(5, 1f), On(2, 2f), Off(8), On(3, 4f) };
		Holds(5, friends[0].Position, friends);

		long before = GC.GetAllocatedBytesForCurrentThread();
		bool any = false;
		for (int i = 0; i < 1000; i++)
		{
			any |= ControlPoint.Holds(5, friends[0].Position, Centre, Radius, friends, 2);
		}

		Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
		Assert.False(any);
	}

	// ---- posts ---------------------------------------------------------------

	[Theory]
	[InlineData(10f, 1.5f, 6.5f)] // a rifleman on the test map's 10 m nodes
	[InlineData(12f, 1.5f, 8.5f)] // and on its 12 m one
	[InlineData(10f, 3f, 5f)] // a tank stops further short
	[InlineData(2f, 3f, 0f)] // never past the centre
	public void APost_IsInFromTheEdge_ByWhatKeepsItOnThePoint(float radius, float arrival, float expected)
	{
		Assert.Equal(expected, ControlPoint.PostLimit(radius, enterMeters: 1f, arrival, marginMeters: 1f), 3);
	}
}
