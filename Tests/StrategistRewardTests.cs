using System.Collections.Generic;
using Gdpyr.AgentClient;
using Xunit;

namespace Gdpyr.Tests;

/// <summary>
/// What a strategist policy is being asked to want
/// (<c>tools/Gdpyr.AgentClient/StrategistReward.cs</c>).
///
/// None of these numbers is a fact about gdpyr — the game emits events and no
/// rewards, on purpose (docs/AGENT_API.md §8). What is worth testing is that the
/// file reads the event stream the way the game writes it: that a unit is the
/// negative half of the id space, that outcome 1 is this side's win and not the
/// other's, and that the dense term cannot collect a whole ticket pool on the
/// first decision after a reset.
/// </summary>
public class StrategistRewardTests
{
	private static GdpyrEvent Event(string kind, params (string Name, double Value)[] fields)
	{
		var record = new GdpyrEvent { Kind = kind };
		foreach ((string name, double value) in fields)
		{
			record.Fields[name] = value;
		}

		return record;
	}

	private static readonly List<GdpyrEvent> Quiet = new();

	[Fact]
	public void DoingNothingCostsTheStepAndNothingElse()
	{
		var reward = new StrategistReward();

		float score = reward.Score(Quiet, 1f, 1f, 0f, 0f, out bool roundEnded);

		Assert.False(roundEnded);
		Assert.Equal(reward.StepCost, score, 4);
	}

	[Fact]
	public void EmptyingTheEnemyTicketPoolIsTheDenseTerm()
	{
		var reward = new StrategistReward();

		float score = reward.Score(Quiet, 0.5f, 0.4f, 0f, 0f, out bool _);

		Assert.Equal(reward.StepCost + (0.1f * reward.TicketProgress), score, 4);
	}

	[Fact]
	public void ARoundThatHasNotStartedCannotCollectTheWholePool()
	{
		var reward = new StrategistReward();

		// A ticket fraction of zero is a round that has not started rather than one
		// that has been won, and the first decision after a reset sees exactly that.
		float score = reward.Score(Quiet, 0f, 1f, 0f, 0f, out bool _);

		Assert.Equal(reward.StepCost, score, 4);
	}

	[Fact]
	public void IncomeCountsOnlyWhenItGrows()
	{
		var reward = new StrategistReward();

		Assert.Equal(reward.StepCost + (0.25f * reward.IncomeProgress),
			reward.Score(Quiet, 1f, 1f, 0.25f, 0.5f, out bool _), 4);

		// A reset drops it back to nothing, and a negative income is not a penalty.
		Assert.Equal(reward.StepCost, reward.Score(Quiet, 1f, 1f, 0.5f, 0f, out bool _), 4);
	}

	[Fact]
	public void DamageIsAttributedBySideBecauseEveryUnitIsThisSides()
	{
		var reward = new StrategistReward();

		// OwnerId folds units into the negative half (Scripts/Sim/OwnerId.cs): a
		// negative attacker is one of this strategist's units shooting a person.
		var dealt = new List<GdpyrEvent>
		{
			Event("damage", ("attacker", -12), ("victim", 4), ("amount", 30)),
		};

		var taken = new List<GdpyrEvent>
		{
			Event("damage", ("attacker", 4), ("victim", -12), ("amount", 30)),
		};

		Assert.Equal(reward.StepCost + (30f * reward.DamageDealt),
			reward.Score(dealt, 1f, 1f, 0f, 0f, out bool _), 4);

		Assert.Equal(reward.StepCost + (30f * reward.DamageTaken),
			reward.Score(taken, 1f, 1f, 0f, 0f, out bool _), 4);
	}

	[Fact]
	public void LosingAUnitCostsAndTakingANodePays()
	{
		var reward = new StrategistReward();

		var events = new List<GdpyrEvent>
		{
			Event("unit_lost", ("unit", 12), ("tier", 0)),

			// NodeHolder: 2 is the strategist, 1 the ground force.
			Event("node_captured", ("node", 1), ("owner", 2)),
			Event("node_captured", ("node", 2), ("owner", 1)),
		};

		float score = reward.Score(events, 1f, 1f, 0f, 0f, out bool _);

		Assert.Equal(reward.StepCost + reward.UnitLost + reward.NodeGained + reward.NodeLost, score, 4);
	}

	[Fact]
	public void OutcomeOneIsThisSidesWinAndTheOtherTwoAreNot()
	{
		var reward = new StrategistReward();

		float won = reward.Score(new List<GdpyrEvent> { Event("round_end", ("outcome", 1)) },
			1f, 1f, 0f, 0f, out bool endedOnAWin);

		float expired = reward.Score(new List<GdpyrEvent> { Event("round_end", ("outcome", 2)) },
			1f, 1f, 0f, 0f, out bool endedOnTheClock);

		float eliminated = reward.Score(new List<GdpyrEvent> { Event("round_end", ("outcome", 3)) },
			1f, 1f, 0f, 0f, out bool _);

		Assert.True(endedOnAWin);
		Assert.True(endedOnTheClock);
		Assert.Equal(reward.StepCost + reward.RoundWon, won, 4);
		Assert.Equal(reward.StepCost + reward.RoundLost, expired, 4);
		Assert.Equal(reward.StepCost + reward.RoundLost, eliminated, 4);
	}

	[Fact]
	public void AnUndecidedRoundEndIsStillTheEndOfTheEpisode()
	{
		var reward = new StrategistReward();

		// What a `reset` from the other learner in a co-training run looks like from
		// here (docs/TRAINING.md §8): the round ended, nobody won it.
		float score = reward.Score(new List<GdpyrEvent> { Event("round_end", ("outcome", 0)) },
			1f, 1f, 0f, 0f, out bool roundEnded);

		Assert.True(roundEnded);
		Assert.Equal(reward.StepCost, score, 4);
	}
}
