using System;
using Gdpyr.Sim;
using Gdpyr.Sim.Htn;
using Godot;
using Xunit;

namespace Gdpyr.Tests;

/// <summary>
/// What the unit domain knows about its squad (docs/HTN_BOTS.md §5.2): where it is
/// gathered, who is in a fight, the one target it focuses, each unit's place in it,
/// the phase units report up, and the posts a defending squad stands on.
/// </summary>
public class SquadCensusTests
{
	private const int Human = 1;
	private const int Bot = BotRoster.FirstPeerId;

	private static readonly Vector3 There = new(40f, 0f, 10f);

	private static readonly int Alpha = OwnerId.ForPeer(2);
	private static readonly int Bravo = OwnerId.ForPeer(3);
	private static readonly int Charlie = OwnerId.ForPeer(4);

	private static int Order(SquadBoard board, uint tick, OrderKind kind, int focus, params ushort[] units)
	{
		float[] strengths = new float[units.Length];
		Array.Fill(strengths, 50f);
		return board.Form(tick, Human, kind, There, focus, units, strengths);
	}

	private static ContactMemory Memory(params (int owner, float health)[] contacts)
	{
		var memory = new ContactMemory(capacity: 8, observers: 1, liveWindowTicks: 8);
		foreach ((int owner, float health) in contacts)
		{
			memory.Observe(100, 0, new KnownContact { OwnerId = owner, Kind = ContactKind.Player, HealthFraction = health });
		}

		return memory;
	}

	// ---- counts ------------------------------------------------------------

	[Fact]
	public void ItCountsTheLiving_WhereTheyAre_AndWhoIsInAFight()
	{
		var board = new SquadBoard();
		int squad = Order(board, 100, OrderKind.Attack, OwnerId.None, 11, 12, 13);
		var census = new SquadCensus();

		census.Begin();
		census.Add(squad, 11, new Vector3(0f, 0f, 0f), engaging: true, Alpha);
		census.Add(squad, 12, new Vector3(6f, 0f, 0f), engaging: true, Alpha);
		census.Add(squad, 13, new Vector3(0f, 0f, 9f), engaging: false, OwnerId.None);
		census.End(board, null);

		Assert.Equal(3, census.AliveIn(squad));
		Assert.Equal(new Vector3(2f, 0f, 3f), census.CentroidOf(squad));
		Assert.Equal(2, census.EngagingIn(squad));
		Assert.Equal(new Vector3(3f, 0f, 0f), census.EngagedCentroidOf(squad));
		Assert.Equal(0, census.AliveIn(squad + 1));
		Assert.Equal(Vector3.Zero, census.CentroidOf(squad + 1));
	}

	[Fact]
	public void ARank_IsAPlaceByIdWhateverOrderTheUnitsWereCountedIn()
	{
		var census = new SquadCensus();
		census.Begin();
		census.Add(0, 40, Vector3.Zero, false, OwnerId.None);
		census.Add(0, 7, Vector3.Zero, false, OwnerId.None);
		census.Add(1, 3, Vector3.Zero, false, OwnerId.None);
		census.Add(0, 19, Vector3.Zero, false, OwnerId.None);

		Assert.Equal(0, census.Rank(0, 7));
		Assert.Equal(1, census.Rank(0, 19));
		Assert.Equal(2, census.Rank(0, 40));
		Assert.Equal(0, census.Rank(1, 3));
		Assert.Equal(-1, census.Rank(0, 3));
		Assert.Equal(-1, census.Rank(0, 99));
	}

	// ---- the focus ---------------------------------------------------------

	[Fact]
	public void TheFocus_IsWhatMostMembersHold()
	{
		var board = new SquadBoard();
		int squad = Order(board, 100, OrderKind.Attack, OwnerId.None, 11, 12, 13);
		var census = new SquadCensus();

		census.Begin();
		census.Add(squad, 11, Vector3.Zero, true, Bravo);
		census.Add(squad, 12, Vector3.Zero, true, Alpha);
		census.Add(squad, 13, Vector3.Zero, true, Alpha);
		census.End(board, Memory((Bravo, 0.1f), (Alpha, 1f)));

		Assert.Equal(Alpha, census.FocusOf(squad));
	}

	[Fact]
	public void AmongEquals_TheFocusIsTheWeakestTheSideRemembers_ThenTheLowestId()
	{
		var board = new SquadBoard();
		int squad = Order(board, 100, OrderKind.Attack, OwnerId.None, 11, 12);
		var census = new SquadCensus();

		census.Begin();
		census.Add(squad, 11, Vector3.Zero, true, Alpha);
		census.Add(squad, 12, Vector3.Zero, true, Bravo);
		census.End(board, Memory((Alpha, 0.9f), (Bravo, 0.4f)));
		Assert.Equal(Bravo, census.FocusOf(squad));

		var fresh = new SquadCensus();
		fresh.Begin();
		fresh.Add(squad, 11, Vector3.Zero, true, Charlie);
		fresh.Add(squad, 12, Vector3.Zero, true, Bravo);
		fresh.End(board, null);
		Assert.Equal(Bravo, fresh.FocusOf(squad));
	}

	[Fact]
	public void TheFocus_IsKeptWhileAnyMemberHoldsIt()
	{
		var board = new SquadBoard();
		int squad = Order(board, 100, OrderKind.Attack, OwnerId.None, 11, 12, 13);
		var census = new SquadCensus();

		census.Begin();
		census.Add(squad, 11, Vector3.Zero, true, Alpha);
		census.End(board, null);
		Assert.Equal(Alpha, census.FocusOf(squad));

		// Two now hold another, weaker one: the focus stays while anybody holds it.
		census.Begin();
		census.Add(squad, 11, Vector3.Zero, true, Alpha);
		census.Add(squad, 12, Vector3.Zero, true, Bravo);
		census.Add(squad, 13, Vector3.Zero, true, Bravo);
		census.End(board, Memory((Bravo, 0.1f)));
		Assert.Equal(Alpha, census.FocusOf(squad));

		census.Begin();
		census.Add(squad, 12, Vector3.Zero, true, Bravo);
		census.End(board, null);
		Assert.Equal(Bravo, census.FocusOf(squad));

		census.Begin();
		census.End(board, null);
		Assert.Equal(OwnerId.None, census.FocusOf(squad));
	}

	[Fact]
	public void TheTargetTheOrderNamed_IsTheFocusWhileAMemberHoldsIt()
	{
		var board = new SquadBoard();
		int squad = Order(board, 100, OrderKind.Attack, Charlie, 11, 12, 13);
		var census = new SquadCensus();

		census.Begin();
		census.Add(squad, 11, Vector3.Zero, true, Alpha);
		census.Add(squad, 12, Vector3.Zero, true, Alpha);
		census.End(board, null);
		Assert.Equal(Alpha, census.FocusOf(squad));

		census.Begin();
		census.Add(squad, 11, Vector3.Zero, true, Alpha);
		census.Add(squad, 12, Vector3.Zero, true, Alpha);
		census.Add(squad, 13, Vector3.Zero, true, Charlie);
		census.End(board, null);
		Assert.Equal(Charlie, census.FocusOf(squad));
	}

	[Fact]
	public void ASquadInARecycledSlot_ChoosesAFocusOfItsOwn()
	{
		var board = new SquadBoard(squads: 1);
		int squad = Order(board, 100, OrderKind.Attack, OwnerId.None, 11, 12);
		var census = new SquadCensus(squads: 1);

		census.Begin();
		census.Add(squad, 11, Vector3.Zero, true, Alpha);
		census.End(board, null);
		Assert.Equal(Alpha, census.FocusOf(squad));

		// The board is full; a new order recycles the slot for a new squad.
		squad = Order(board, 200, OrderKind.Move, OwnerId.None, 21, 22);
		census.Begin();
		census.Add(squad, 21, Vector3.Zero, true, Bravo);
		census.Add(squad, 22, Vector3.Zero, true, Alpha);
		census.End(board, Memory((Bravo, 0.2f), (Alpha, 0.9f)));

		Assert.Equal(Bravo, census.FocusOf(squad));
	}

	// ---- the phase units report up -----------------------------------------

	[Fact]
	public void ASquadWithAUnitInAFight_IsEngaged_AndMovingAgainWhenNoneIs()
	{
		var board = new SquadBoard();
		int squad = Order(board, 100, OrderKind.Attack, OwnerId.None, 11, 12);
		var census = new SquadCensus();

		census.Begin();
		census.Add(squad, 11, Vector3.Zero, true, Alpha);
		census.Add(squad, 12, Vector3.Zero, false, OwnerId.None);
		census.End(board, null);
		Assert.Equal(SquadPhase.Engaged, board.At(squad).Phase);

		census.Begin();
		census.Add(squad, 11, Vector3.Zero, false, OwnerId.None);
		census.Add(squad, 12, Vector3.Zero, false, OwnerId.None);
		census.End(board, null);
		Assert.Equal(SquadPhase.Moving, board.At(squad).Phase);
	}

	[Theory]
	[InlineData(SquadPhase.Gathering)]
	[InlineData(SquadPhase.FallingBack)]
	public void GatheringAndFallingBack_AreTheCommandersToEnd(SquadPhase phase)
	{
		var board = new SquadBoard();
		int squad = Order(board, 100, OrderKind.Attack, OwnerId.None, 11);
		board.SetPhase(squad, phase);
		var census = new SquadCensus();

		census.Begin();
		census.Add(squad, 11, Vector3.Zero, true, Alpha);
		census.End(board, null);

		Assert.Equal(phase, board.At(squad).Phase);
	}

	[Fact]
	public void CountingEveryUnitEveryTick_AllocatesNothing()
	{
		var board = new SquadBoard();
		var census = new SquadCensus();
		var units = new ushort[SimConfig.MaxUnits];
		var strengths = new float[SimConfig.MaxUnits];
		for (int i = 0; i < units.Length; i++)
		{
			units[i] = (ushort)(i + 1);
			strengths[i] = 50f;
		}

		for (int s = 0; s < 8; s++)
		{
			board.Form(1, Bot, OrderKind.Attack, new Vector3(s * 10f, 0f, 0f), OwnerId.None, units.AsSpan(s * 8, 8),
				strengths.AsSpan(s * 8, 8));
		}

		ContactMemory memory = Memory((Alpha, 0.5f), (Bravo, 0.7f));

		Run();
		long before = GC.GetAllocatedBytesForCurrentThread();
		Run();
		Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
		Assert.Equal(0, census.Overflows);

		void Run()
		{
			for (int t = 0; t < 600; t++)
			{
				census.Begin();
				for (int i = 0; i < units.Length; i++)
				{
					census.Add(board.SquadOf(units[i]), units[i], new Vector3(i, 0f, t), (i + t) % 3 == 0,
						(i + t) % 4 == 0 ? Alpha : (i + t) % 4 == 1 ? Bravo : OwnerId.None);
				}

				census.End(board, memory);
				for (int i = 0; i < units.Length; i++)
				{
					census.Rank(board.SquadOf(units[i]), units[i]);
				}
			}
		}
	}

	// ---- posts -------------------------------------------------------------

	[Theory]
	[InlineData(1, 0f)]
	[InlineData(2, 4f)]
	[InlineData(4, 4f)]
	[InlineData(8, 5.093f)]
	[InlineData(40, 10f)]
	public void TheRing_SpacesPostsFourMetresApart_BetweenItsLimits(int count, float radius)
	{
		Assert.Equal(radius, DefendPosts.Radius(count, 4f, 4f, 10f), 3);
	}

	[Fact]
	public void AShortLeash_WinsOverTheRingsLeastRadius()
	{
		Assert.Equal(3f, DefendPosts.Radius(4, 4f, 4f, 3f));
	}

	[Fact]
	public void PostsAreDistinct_OnTheRing_TheFirstOnTheThreat()
	{
		var anchor = new Vector3(10f, 2f, -5f);
		const int count = 5;
		const float radius = 6f;
		float bearing = DefendPosts.Bearing(anchor, anchor + new Vector3(0f, 0f, 50f));

		Vector3 first = DefendPosts.Post(anchor, 0, count, bearing, radius);
		Assert.Equal(anchor.X, first.X, 3);
		Assert.Equal(anchor.Z + radius, first.Z, 3);

		for (int k = 0; k < count; k++)
		{
			Vector3 post = DefendPosts.Post(anchor, k, count, bearing, radius);
			Assert.Equal(radius, post.DistanceTo(anchor), 3);
			Assert.Equal(anchor.Y, post.Y);
			for (int j = 0; j < k; j++)
			{
				// Neighbours on a ring of five at 6 m stand 7 m apart.
				Assert.True(post.DistanceTo(DefendPosts.Post(anchor, j, count, bearing, radius)) > 7f);
			}
		}
	}

	[Fact]
	public void ASquadOfOne_StandsOnTheAnchor()
	{
		var anchor = new Vector3(1f, 0f, 2f);
		Assert.Equal(anchor, DefendPosts.Post(anchor, 0, 1, 1f, 6f));
		Assert.Equal(anchor, DefendPosts.Post(anchor, 1, 3, 1f, 0f));
	}

	[Theory]
	[InlineData(0f, 50f, 0f)]
	[InlineData(50f, 0f, 90f)]
	[InlineData(50f, 45f, 45f)]
	[InlineData(50f, 60f, 45f)]
	[InlineData(0f, -50f, 180f)]
	[InlineData(-50f, 0f, 270f)]
	[InlineData(-50f, 10f, 270f)]
	public void TheBearing_IsRoundedToAnEighthOfTheCompass(float x, float z, float degrees)
	{
		float bearing = DefendPosts.Bearing(Vector3.Zero, new Vector3(x, 3f, z));
		Assert.Equal(degrees, Mathf.RadToDeg(bearing), 3);
	}

	[Fact]
	public void TheBearingToTheSamePoint_IsNorth()
	{
		Assert.Equal(0f, DefendPosts.Bearing(There, There));
	}
}
