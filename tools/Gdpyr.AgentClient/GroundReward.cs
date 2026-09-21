using System;
using System.Collections.Generic;

namespace Gdpyr.AgentClient;

/// <summary>
/// What a ground policy is being asked to want.
///
/// **This is the trainer's file, not the game's.** The agent API emits events and
/// no rewards, on purpose: baking a reward function into the server would be a
/// gameplay claim disguised as plumbing (docs/AGENT_API.md §8). Every number below
/// is a hypothesis about what makes a good ground-force player, and changing them
/// changes what you get — nothing here is a fact about gdpyr.
///
/// The shape is the usual one: sparse outcome terms that say what winning is, and
/// a small dense term so that a policy which has never killed anything still has a
/// gradient to climb.
/// </summary>
public sealed class GroundReward
{
	/// <summary>Per enemy killed by this seat — a unit or a person.</summary>
	public float Kill = 2f;

	/// <summary>Per point of damage this seat dealt.</summary>
	public float DamageDealt = 0.01f;

	/// <summary>Per point of damage this seat took.</summary>
	public float DamageTaken = -0.01f;

	/// <summary>On this seat's own death.</summary>
	public float Death = -1f;

	/// <summary>Per point of friendly damage. Friendly fire between players is on, and it costs the side a ticket.</summary>
	public float FriendlyDamage = -0.03f;

	/// <summary>
	/// Per normalized unit closed on the nearest enemy barracks. Dense, small, and
	/// the only shaping term: it is what stops a fresh policy from standing still
	/// forever, and it is a bet that walking towards the objective is roughly right.
	/// </summary>
	public float ObjectiveProgress = 0.5f;

	/// <summary>Per decision, so that dawdling is not free.</summary>
	public float StepCost = -0.001f;

	/// <summary>At <c>round_end</c>, by outcome.</summary>
	public float RoundWon = 5f;

	public float RoundLost = -5f;

	/// <summary>
	/// The reward for one decision.
	///
	/// <paramref name="previousObjectiveDistance"/> and
	/// <paramref name="objectiveDistance"/> are the normalized
	/// <c>objective.distance</c> field, before and after.
	/// </summary>
	public float Score(int seat, IEnumerable<GdpyrEvent> events, float previousObjectiveDistance,
		float objectiveDistance, out bool roundEnded, out bool died)
	{
		roundEnded = false;
		died = false;

		float reward = StepCost;
		if (objectiveDistance > 0f && previousObjectiveDistance > 0f)
		{
			reward += (previousObjectiveDistance - objectiveDistance) * ObjectiveProgress;
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

					if (attacker == seat)
					{
						// A negative victim id is a unit; OwnerId folds the two id spaces
						// into one signed int. Every unit on the field belongs to the
						// strategist and every body on the field is ground force, so for a
						// ground seat "hit a unit" is the enemy and "hit a player" is
						// friendly fire — including itself, from a launcher.
						reward += amount * (victim < 0 ? DamageDealt : FriendlyDamage);
					}

					if (victim == seat)
					{
						reward += amount * DamageTaken;
					}

					break;
				}

				case "kill":
				{
					int attacker = (int)record.Get("attacker");
					int victim = (int)record.Get("victim");

					if (attacker == seat)
					{
						reward += Kill;
					}

					if (victim == seat)
					{
						reward += Death;
						died = true;
					}

					break;
				}

				case "round_end":
				{
					roundEnded = true;

					// RoundOutcome: 0 undecided · 1 ground eliminated · 2 time expired
					// (a ground-force win) · 3 strategist eliminated (also a ground win).
					int outcome = (int)record.Get("outcome");
					reward += outcome switch
					{
						1 => RoundLost,
						2 or 3 => RoundWon,
						_ => 0f,
					};

					break;
				}
			}
		}

		return reward;
	}
}
