using System.Collections.Generic;

namespace Gdpyr.AgentClient;

/// <summary>
/// What a strategist policy is being asked to want.
///
/// **This is the trainer's file, not the game's.** The agent API emits events and
/// no rewards, on purpose (docs/AGENT_API.md §8), and every number below is a
/// hypothesis about what makes a good strategist. Nothing here is a fact about
/// gdpyr; changing them changes what you get.
///
/// The shape mirrors <see cref="GroundReward"/> — sparse outcome terms that say
/// what winning is, and a dense term so a policy that has never won anything
/// still has a gradient — but the two sides do not want the same things, and this
/// is where that shows:
///
/// - **The dense term is the enemy's ticket pool, not a distance.** A ground
///   policy is shaped towards the objective because a body has to walk somewhere;
///   a strategist wins by emptying the ticket pool (<c>RoundOutcome</c> 1) and the
///   pool is already in its observation, normalized. So the one shaping term is
///   the thing it is actually being asked to do, which is a much weaker claim
///   than "walking forward is good".
/// - **Attribution is by side, not by seat.** Every unit on the field belongs to
///   the strategist, so a <c>damage</c> event whose attacker is a unit
///   (<c>OwnerId</c> folds units into the negative half) is this policy's army
///   doing work, whoever pulled the trigger.
/// - **Income is worth something on its own.** Points not spent win nothing, but a
///   round where the nodes were held is a round with an army in it, and the credit
///   assignment from "held a node at minute three" to "won at minute nineteen" is
///   otherwise nineteen minutes long.
/// </summary>
public sealed class StrategistReward
{
	/// <summary>
	/// Per normalized unit of the ground force's ticket pool emptied. The pool is
	/// <c>round.ticket_fraction</c>: 1.0 at the start, 0.0 when the strategist has
	/// won. Dense, and the only shaping term.
	/// </summary>
	public float TicketProgress = 6f;

	/// <summary>Per normalized unit of income paid, which is nodes held over time.</summary>
	public float IncomeProgress = 1f;

	/// <summary>Per point of damage one of this side's units dealt.</summary>
	public float DamageDealt = 0.01f;

	/// <summary>Per point of damage one of this side's units took.</summary>
	public float DamageTaken = -0.005f;

	/// <summary>Per unit lost. Material, and it is points that have already been spent.</summary>
	public float UnitLost = -0.25f;

	/// <summary>Per resource node this side took.</summary>
	public float NodeGained = 1f;

	/// <summary>Per resource node the ground force took off it.</summary>
	public float NodeLost = -1f;

	/// <summary>
	/// Per decision. A strategist that does nothing loses on the clock — twenty
	/// minutes with a ticket left is a ground-force win — so idling has to cost
	/// something before the clock says so.
	/// </summary>
	public float StepCost = -0.005f;

	/// <summary>At <c>round_end</c>, by outcome.</summary>
	public float RoundWon = 10f;

	public float RoundLost = -10f;

	/// <summary>
	/// The reward for one decision.
	///
	/// <paramref name="previousTicketFraction"/> and
	/// <paramref name="ticketFraction"/> are <c>round.ticket_fraction</c> before
	/// and after; <paramref name="previousIncome"/> and <paramref name="income"/>
	/// are <c>round.income_paid</c>. Both are already normalized by the encoder.
	/// </summary>
	public float Score(IEnumerable<GdpyrEvent> events, float previousTicketFraction,
		float ticketFraction, float previousIncome, float income, out bool roundEnded)
	{
		roundEnded = false;
		float reward = StepCost;

		// Guarded the way the ground seat's objective term is: a fraction of zero is
		// a round that has not started rather than a pool that has been emptied, and
		// the first decision after a reset would otherwise collect the whole thing.
		if (ticketFraction > 0f && previousTicketFraction > 0f)
		{
			reward += (previousTicketFraction - ticketFraction) * TicketProgress;
		}

		if (income > previousIncome)
		{
			reward += (income - previousIncome) * IncomeProgress;
		}

		foreach (GdpyrEvent record in events)
		{
			switch (record.Kind)
			{
				case "damage":
				{
					int attacker = (int)record.Get("attacker");
					int victim = (int)record.Get("victim");
					float amount = (float)record.Get("amount");

					// Units take the negative half of the id space, and every unit on the
					// field is this side's (Scripts/Sim/OwnerId.cs).
					if (attacker < 0)
					{
						reward += amount * DamageDealt;
					}

					if (victim < 0)
					{
						reward += amount * DamageTaken;
					}

					break;
				}

				case "unit_lost":
					reward += UnitLost;
					break;

				case "node_captured":
				{
					// NodeHolder: 0 neutral, 1 ground force, 2 strategist.
					int owner = (int)record.Get("owner");
					if (owner == 2)
					{
						reward += NodeGained;
					}
					else if (owner == 1)
					{
						reward += NodeLost;
					}

					break;
				}

				case "round_end":
				{
					roundEnded = true;

					// RoundOutcome: 0 undecided · 1 ground force eliminated (the
					// strategist's win) · 2 time expired · 3 strategist eliminated.
					int outcome = (int)record.Get("outcome");
					reward += outcome switch
					{
						1 => RoundWon,
						2 or 3 => RoundLost,
						_ => 0f,
					};

					break;
				}
			}
		}

		return reward;
	}
}
