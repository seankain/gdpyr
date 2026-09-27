using System;
using Gdpyr.Sim;
using Gdpyr.Sim.Htn;
using Godot;
using Xunit;

namespace Gdpyr.Tests;

/// <summary>
/// The strategist side's squads (docs/HTN_BOTS.md §4.3): one order is one squad,
/// the same order again is the same squad, and a unit leaves when it is re-ordered
/// or dies.
/// </summary>
public class SquadBoardTests
{
	private const int Human = 1;
	private const int Bot = BotRoster.FirstPeerId;

	private static readonly Vector3 There = new(40f, 0f, 10f);

	private static int Order(SquadBoard board, uint tick, int issuer, OrderKind kind, Vector3 target,
		params ushort[] units) => Focused(board, tick, issuer, kind, target, OwnerId.None, units);

	private static int Focused(SquadBoard board, uint tick, int issuer, OrderKind kind, Vector3 target, int focus,
		params ushort[] units)
	{
		float[] strengths = new float[units.Length];
		Array.Fill(strengths, 50f);
		return board.Form(tick, issuer, kind, target, focus, units, strengths);
	}

	[Fact]
	public void OneOrder_IsOneSquad()
	{
		var board = new SquadBoard();

		int squad = Focused(board, 100, Human, OrderKind.Attack, There, OwnerId.ForPeer(3), 11, 12, 13);

		Assert.Equal(1, board.Count);
		Assert.Equal(3, board.MemberCount);
		Squad formed = board.At(squad);
		Assert.True(formed.Active);
		Assert.Equal(Human, formed.Issuer);
		Assert.Equal(OrderKind.Attack, formed.Order);
		Assert.Equal(SquadMission.Attack, formed.Mission);
		Assert.Equal(OwnerId.ForPeer(3), formed.FocusOwnerId);
		Assert.Equal(SquadPhase.Moving, formed.Phase);
		Assert.Equal(3, formed.Members);
		Assert.Equal(150f, formed.StrengthAtFormation);
		Assert.Equal(100u, formed.FormedTick);
		Assert.Equal(squad, board.SquadOf(12));
	}

	[Fact]
	public void TheSameOrderAgain_JoinsTheSameSquad()
	{
		// A computer strategist reinforcing its garrison one unit at a time keeps one
		// garrison, not one squad per unit.
		var board = new SquadBoard();
		int garrison = Order(board, 100, Bot, OrderKind.Defend, There, 11);

		int again = Order(board, 130, Bot, OrderKind.Defend, There + new Vector3(1f, 0f, 1f), 12);

		Assert.Equal(garrison, again);
		Assert.Equal(1, board.Count);
		Assert.Equal(2, board.At(garrison).Members);
		Assert.Equal(100f, board.At(garrison).StrengthAtFormation);
		Assert.Equal(100u, board.At(garrison).FormedTick);
		Assert.Equal(130u, board.At(garrison).OrderedTick);
	}

	[Fact]
	public void ADifferentIssuer_Kind_Focus_OrPlace_IsADifferentSquad()
	{
		var board = new SquadBoard();
		int first = Order(board, 100, Human, OrderKind.Attack, There, 11);

		Assert.NotEqual(first, Order(board, 100, Bot, OrderKind.Attack, There, 12));
		Assert.NotEqual(first, Order(board, 100, Human, OrderKind.Move, There, 13));
		Assert.NotEqual(first, Focused(board, 100, Human, OrderKind.Attack, There, OwnerId.ForPeer(3), 14));
		Assert.NotEqual(first, Order(board, 100, Human, OrderKind.Attack,
			There + new Vector3(SquadBoard.MergeRadiusMeters + 0.1f, 0f, 0f), 15));

		Assert.Equal(5, board.Count);
	}

	[Fact]
	public void AReorderedUnit_LeavesItsSquad_AndAnEmptySquadIsGone()
	{
		var board = new SquadBoard();
		int first = Order(board, 100, Human, OrderKind.Attack, There, 11, 12);

		int second = Order(board, 200, Human, OrderKind.Move, Vector3.Zero, 11);

		Assert.Equal(1, board.At(first).Members);
		Assert.Equal(second, board.SquadOf(11));

		Order(board, 300, Human, OrderKind.Move, Vector3.Zero, 12);

		Assert.False(board.At(first).Active);
		Assert.Equal(1, board.Count);
		Assert.Equal(2, board.At(second).Members);
	}

	[Fact]
	public void ReissuingAnOrderToItsOwnSquad_DoesNotCountItsStrengthTwice()
	{
		var board = new SquadBoard();
		int squad = Order(board, 100, Human, OrderKind.Attack, There, 11, 12);

		Assert.Equal(squad, Order(board, 160, Human, OrderKind.Attack, There, 11, 12));

		Assert.Equal(2, board.At(squad).Members);
		Assert.Equal(100f, board.At(squad).StrengthAtFormation);
	}

	[Fact]
	public void AUnitThatDies_LeavesItsSquad_AndTheLastOneClosesIt()
	{
		var board = new SquadBoard();
		int squad = Order(board, 100, Human, OrderKind.Attack, There, 11, 12);

		Assert.True(board.Leave(11));
		Assert.False(board.Leave(11));
		Assert.Equal(1, board.At(squad).Members);
		Assert.Equal(-1, board.SquadOf(11));

		// What it was worth when it formed is what a retreat is measured against.
		Assert.Equal(100f, board.At(squad).StrengthAtFormation);

		board.Leave(12);
		Assert.Equal(0, board.Count);
		Assert.Equal(0, board.MemberCount);
	}

	[Fact]
	public void AUnitNamedTwiceInOneOrder_IsInItOnce()
	{
		// A client picks the list; a repeated id is something the server answers for.
		var board = new SquadBoard();
		int squad = Order(board, 100, Human, OrderKind.Attack, There, 11, 11, 11, 0);

		Assert.Equal(1, board.At(squad).Members);
		Assert.Equal(50f, board.At(squad).StrengthAtFormation);
		Assert.Equal(1, board.MemberCount);
	}

	[Fact]
	public void AFullBoard_RecyclesTheSquadOrderedLeastRecently()
	{
		var board = new SquadBoard(squads: 2);
		int old = Order(board, 100, Human, OrderKind.Move, new Vector3(0f, 0f, 0f), 11, 12);
		int kept = Order(board, 110, Human, OrderKind.Move, new Vector3(50f, 0f, 0f), 13);

		// Joins the second, which makes it the more recently ordered of the two.
		Assert.Equal(kept, Order(board, 120, Human, OrderKind.Move, new Vector3(50f, 0f, 1f), 14));

		int newest = Order(board, 130, Human, OrderKind.Attack, There, 15);

		Assert.Equal(1, board.Evictions);
		Assert.Equal(old, newest);
		Assert.Equal(-1, board.SquadOf(11));
		Assert.Equal(-1, board.SquadOf(12));
		Assert.Equal(kept, board.SquadOf(13));
		Assert.Equal(2, board.Count);
		Assert.Equal(3, board.MemberCount);
	}

	[Fact]
	public void AFullMembershipTable_RefusesTheRest()
	{
		var board = new SquadBoard(members: 2);

		int squad = Order(board, 100, Human, OrderKind.Attack, There, 11, 12, 13);

		Assert.Equal(2, board.At(squad).Members);
		Assert.Equal(1, board.Overflows);
		Assert.Equal(-1, board.SquadOf(13));
	}

	[Fact]
	public void AnOrderNamingNobody_FormsNothing_AndRecyclesNothing()
	{
		var board = new SquadBoard(squads: 1);
		int squad = Order(board, 100, Human, OrderKind.Move, Vector3.Zero, 11);

		Assert.Equal(-1, Order(board, 200, Human, OrderKind.Attack, There));
		Assert.Equal(-1, Order(board, 200, Human, OrderKind.Attack, There, 0));

		Assert.Equal(0, board.Evictions);
		Assert.Equal(squad, board.SquadOf(11));
	}

	[Theory]
	[InlineData(OrderKind.Attack, SquadMission.Attack)]
	[InlineData(OrderKind.Defend, SquadMission.DefendZone)]
	[InlineData(OrderKind.Patrol, SquadMission.Recon)]
	[InlineData(OrderKind.Move, SquadMission.None)]
	public void AnOrdersMission(OrderKind order, SquadMission mission)
	{
		Assert.Equal(mission, SquadBoard.MissionFor(order));
	}

	[Fact]
	public void MembersOf_ListsASquadsUnits()
	{
		var board = new SquadBoard();
		int first = Order(board, 100, Human, OrderKind.Attack, There, 11, 12);
		Order(board, 100, Human, OrderKind.Move, Vector3.Zero, 13);

		Span<ushort> into = stackalloc ushort[SimConfig.MaxUnits];
		int count = board.MembersOf(first, into);

		Assert.Equal(2, count);
		Assert.Contains((ushort)11, into[..count].ToArray());
		Assert.Contains((ushort)12, into[..count].ToArray());
	}

	[Fact]
	public void Clearing_DisbandsEverything()
	{
		var board = new SquadBoard(squads: 1);
		Order(board, 100, Human, OrderKind.Attack, There, 11);
		Order(board, 100, Human, OrderKind.Move, Vector3.Zero, 12);

		board.Clear();

		Assert.Equal(0, board.Count);
		Assert.Equal(0, board.MemberCount);
		Assert.Equal(0, board.Evictions);
		Assert.Equal(-1, board.SquadOf(12));
	}

	[Fact]
	public void APhaseIsSet_OnASquadOnTheBoard_AndOnNoOther()
	{
		var board = new SquadBoard();
		int squad = Order(board, 100, Bot, OrderKind.Attack, There, 11, 12);

		Assert.True(board.SetPhase(squad, SquadPhase.Engaged));
		Assert.Equal(SquadPhase.Engaged, board.At(squad).Phase);

		Assert.False(board.SetPhase(squad + 1, SquadPhase.Engaged));
		Assert.False(board.SetPhase(-1, SquadPhase.Engaged));
		Assert.False(board.SetPhase(board.Capacity, SquadPhase.Engaged));
		Assert.False(board.At(squad + 1).Active);
		Assert.Equal(1, board.Count);
	}

	[Fact]
	public void Staging_SetsTheSquadGatheringAtThePoint()
	{
		var board = new SquadBoard();
		int squad = Order(board, 100, Bot, OrderKind.Attack, There, 11, 12);
		var staging = new Vector3(20f, 0f, 5f);

		Assert.True(board.Stage(squad, staging));

		Squad staged = board.At(squad);
		Assert.Equal(SquadPhase.Gathering, staged.Phase);
		Assert.True(staged.HasStaging);
		Assert.Equal(staging, staged.StagingPoint);
		Assert.False(board.Stage(squad + 1, staging));
	}

	[Fact]
	public void ANewSquadInARecycledSlot_HasNoStagingPoint()
	{
		var board = new SquadBoard(squads: 1);
		int squad = Order(board, 100, Bot, OrderKind.Attack, There, 11);
		board.Stage(squad, There);

		board.Leave(11);
		squad = Order(board, 200, Human, OrderKind.Move, Vector3.Zero, 12);

		Assert.False(board.At(squad).HasStaging);
		Assert.Equal(SquadPhase.Moving, board.At(squad).Phase);
	}

	[Fact]
	public void OrdersAndDeaths_AllocateNothing()
	{
		var board = new SquadBoard();
		var units = new ushort[SimConfig.MaxUnits];
		var strengths = new float[SimConfig.MaxUnits];
		for (int i = 0; i < units.Length; i++)
		{
			units[i] = (ushort)(i + 1);
			strengths[i] = 50f;
		}

		Run();
		long before = GC.GetAllocatedBytesForCurrentThread();
		Run();
		Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);

		void Run()
		{
			for (uint t = 0; t < 500; t++)
			{
				int start = (int)(t % 48);
				board.Form(t, Human, (OrderKind)(1 + (t % 4)), new Vector3(t % 7, 0f, 0f), OwnerId.None,
					units.AsSpan(start, 16), strengths.AsSpan(start, 16));
				board.Leave((ushort)(1 + (t % 64)));
				board.SquadOf((ushort)(1 + (t % 64)));
			}
		}
	}
}
