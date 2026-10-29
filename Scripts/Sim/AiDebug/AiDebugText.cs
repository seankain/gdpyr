using System;
using System.Text;
using Gdpyr.Sim.Htn;

namespace Gdpyr.Sim.AiDebug;

/// <summary>
/// One inspected agent as text (docs/AI_DEBUG.md §4.1): who it is and what it knows,
/// then its plan, its facts, its domain with the plan marked on it, and what its
/// planner has done lately. The console prints the plain style; the spectator's
/// panel shows the same sections in the rich one.
/// </summary>
public static class AiDebugText
{
	/// <summary>Everything, one section after another.</summary>
	public static string Describe(AiInspect inspect, AiDebugFrame frame, HtnTextStyle style, bool collapse = true)
	{
		var text = new StringBuilder();
		text.Append(Header(inspect, frame, style));

		HtnDomainMap map = MapFor(inspect, frame, out string problem);
		if (map == null)
		{
			if (problem != null)
			{
				text.Append('\n').Append(problem);
			}

			return text.ToString();
		}

		uint tick = frame?.Tick ?? 0;
		text.Append("\n\n").Append(HtnText.Plan(map, inspect.Trace, style, tick));
		text.Append("\n\n").Append(HtnText.Facts(HtnSchema.For(map.Kind), inspect.Trace, style));
		var options = new HtnTreeOptions { Conditions = true, Collapse = collapse, Tick = tick };
		text.Append("\n\n").Append(HtnText.Tree(map, inspect.Trace, style, options));
		text.Append("\n\n").Append(HtnText.History(map, inspect.Trace, style, tick));
		return text.ToString();
	}

	/// <summary>
	/// The map to read <paramref name="inspect"/>'s trace with, or null with the reason:
	/// it has no plan, or this build numbers its domain differently from the server's.
	/// </summary>
	public static HtnDomainMap MapFor(AiInspect inspect, AiDebugFrame frame, out string problem)
	{
		problem = null;
		if (inspect == null || !inspect.Planning || !inspect.Trace.HasPlan)
		{
			return null;
		}

		HtnDomainMap map = frame?.MapFor(inspect.Trace.Domain);
		if (map == null)
		{
			problem = $"this build numbers the {inspect.Trace.Domain.ToString().ToLowerInvariant()} domain differently"
				+ " from the server's (signature mismatch): run the same commit on both";
		}

		return map;
	}

	/// <summary>Who it is, and the numbers and places it plans with, in two or three lines.</summary>
	public static string Header(AiInspect inspect, AiDebugFrame frame, HtnTextStyle style)
	{
		if (inspect == null)
		{
			return "not on the field";
		}

		var text = new StringBuilder();
		Bold(text, inspect.Ref.ToString(), style);

		inspect.TryValue(AiValueKey.Health, out int health);
		switch (inspect.Ref.Kind)
		{
			case AiEntityKind.Player:
				if (!inspect.Planning)
				{
					inspect.TryValue(AiValueKey.Team, out int team);
					inspect.TryValue(AiValueKey.Person, out int person);
					text.Append(team == (int)Team.Strategist
						? " · a strategist's seat: its plan is the commander's (F6)"
						: person != 0
							? " · a person: nothing plans for them"
							: frame?.BotAi == BotAi.Legacy
								? " · a legacy bot: --bot-ai legacy has no plan to show"
								: " · driven by an external policy: no plan to show");
					text.Append($" · health {health}");
					AppendKit(text, inspect);
					return text.ToString();
				}

				inspect.TryValue(AiValueKey.Role, out int role);
				text.Append($" · ground bot · role {(GroundRole)role} · health {health}");
				break;

			case AiEntityKind.Unit:
				inspect.TryValue(AiValueKey.Order, out int order);
				inspect.TryValue(AiValueKey.Issuer, out int issuer);
				text.Append($" · unit · {(OrderKind)order} order from {(issuer == 0 ? "nobody" : BotRoster.NameOf(issuer))}"
					+ $" · health {health}%");

				// docs/VEHICLES.md §10: a passenger plans nothing while it rides, so this is
				// the line that says why its plan is empty.
				if (inspect.TryValue(AiValueKey.RidingIn, out int vehicle))
				{
					text.Append($" · riding in {AiEntityRef.Unit((ushort)Math.Clamp(vehicle, 0, ushort.MaxValue))}");
				}
				else if (inspect.TryValue(AiValueKey.Seats, out int seats))
				{
					inspect.TryValue(AiValueKey.Aboard, out int aboard);
					text.Append($" · aboard {aboard}/{seats}");
				}

				// An aircraft's height, which its position alone does not say over uneven ground;
				// nothing on the ground, where it reads a few millimetres up.
				if (inspect.TryValue(AiValueKey.Altitude, out int centimetres)
					&& centimetres > Flight.TouchdownMeters * 100f)
				{
					// Whole metres and tenths in integers, so no locale writes "14,0".
					text.Append($" · {centimetres / 100}.{centimetres % 100 / 10} m up");
				}
				if (inspect.TryValue(AiValueKey.Commander, out int commander)
					&& inspect.TryValue(AiValueKey.CommanderSquad, out int slot))
				{
					text.Append($" · in squad {slot} of {BotRoster.NameOf(commander)}");
				}

				// docs/TACTICS.md §5.5: what its squad's drill has it doing.
				if (inspect.TryValue(AiValueKey.DrillTask, out int drillTask)
					&& inspect.TryValue(AiValueKey.DrillSquad, out int drillSquad))
				{
					inspect.TryValue(AiValueKey.DrillPhase, out int drillPhase);
					text.Append($" · drill {drillSquad} {(DrillPhase)drillPhase}: {(DrillTask)drillTask}");
				}

				if (!inspect.Planning)
				{
					text.Append(" · no plan (legacy)");
					return text.ToString();
				}

				break;

			case AiEntityKind.Squad:
				text.Append(" · commander squad");
				break;

			case AiEntityKind.Drill:
				inspect.TryValue(AiValueKey.Elements, out int elements);
				inspect.TryValue(AiValueKey.Bounds, out int bounds);
				inspect.TryValue(AiValueKey.GiveUps, out int giveUps);
				text.Append($" · squad drill · {elements} taking part · {bounds} bounds");
				if (inspect.TryValue(AiValueKey.Moving, out int moving) && moving != 0)
				{
					text.Append($" · half {(DrillElement)moving} moving");
				}

				if (giveUps > 0)
				{
					text.Append($" · given up {giveUps}×");
				}

				break;
		}

		HtnDomainKind domain = AiGoals.DomainOf(inspect.Ref.Kind);
		AiTaskFamily family = AiGoals.Family(domain, inspect.Goal);
		text.Append('\n');
		text.Append("goal ");
		Colored(text, $"{AiGoals.Name(domain, inspect.Goal)} ({AiGoals.Describe(family)})", AiGoals.Color(family), style);

		if (inspect.TryValue(AiValueKey.Move, out int move))
		{
			text.Append(" · move ").Append(domain == HtnDomainKind.Ground ? ((GroundMove)move).ToString() : ((UnitMove)move).ToString());
		}

		if (inspect.TryValue(AiValueKey.Target, out int target))
		{
			text.Append(" · shooting ").Append(Owner(target));
		}

		if (inspect.TryValue(AiValueKey.Focus, out int focus) && focus != 0)
		{
			text.Append(" · focus ").Append(Owner(focus));
		}

		if (inspect.TryValue(AiValueKey.SquadAlive, out int alive) && alive > 0)
		{
			text.Append($" · squad of {alive}");
		}

		if (inspect.TryValue(AiValueKey.Waiting, out int waiting) && waiting != 0)
		{
			text.Append(" · waiting for its squad");
		}

		if (inspect.TryValue(AiValueKey.FailedTrips, out int trips) && trips > 0)
		{
			text.Append($" · {trips} locker trips given up");
		}

		if (inspect.TryValue(AiValueKey.FailedResupplies, out int resupplies) && resupplies > 0)
		{
			text.Append($" · {resupplies} resupplies given up");
		}

		if (inspect.TryValue(AiValueKey.FailedRearms, out int rearms) && rearms > 0)
		{
			text.Append($" · {rearms} rearms given up");
		}

		AppendKit(text, inspect);
		return text.ToString();
	}

	/// <summary>
	/// A ground player's kit and what is left of it, on a line of its own (docs/LOADOUT.md
	/// §8): <c>kit rifle · 4 mags · 2 grenades · light vest · rounds 30 + 120 · grenades 2 · vest 35</c>.
	/// Nothing for anything else.
	/// </summary>
	private static void AppendKit(StringBuilder text, AiInspect inspect)
	{
		if (!inspect.TryValue(AiValueKey.KitPrimary, out int primary))
		{
			return;
		}

		inspect.TryValue(AiValueKey.KitMagazines, out int magazines);
		inspect.TryValue(AiValueKey.KitGrenades, out int kitGrenades);
		inspect.TryValue(AiValueKey.KitVest, out int vest);
		inspect.TryValue(AiValueKey.Magazine, out int magazine);
		inspect.TryValue(AiValueKey.Reserve, out int reserve);
		inspect.TryValue(AiValueKey.Grenades, out int grenades);
		inspect.TryValue(AiValueKey.VestPoints, out int points);

		bool armed = primary > 0 && primary < Loadout.PrimaryNames.Length;
		text.Append("\nkit ").Append(armed ? Loadout.PrimaryNames[primary] : "no primary");
		if (armed)
		{
			text.Append($" · {magazines} mag{(magazines == 1 ? string.Empty : "s")}");
		}

		text.Append($" · {kitGrenades} grenade{(kitGrenades == 1 ? string.Empty : "s")}");
		bool worn = vest > 0 && vest < Loadout.ArmourNames.Length;
		text.Append(worn ? $" · {Loadout.ArmourNames[vest]} vest" : " · no vest");

		if (armed)
		{
			text.Append($" · rounds {magazine} + {(reserve < 0 ? "∞" : reserve.ToString())}");
			if (magazine <= 0 && reserve == 0)
			{
				text.Append(" dry");
			}
		}

		text.Append($" · grenades {grenades}");
		if (worn)
		{
			text.Append($" · vest {points}");
		}
	}

	/// <summary>What an owner id names: <c>unit 12</c>, <c>bot 3</c>, <c>structure 4</c>, <c>nothing</c>.</summary>
	public static string Owner(int ownerId)
	{
		if (ownerId == OwnerId.None)
		{
			return "nothing";
		}

		if (OwnerId.IsUnit(ownerId))
		{
			return $"unit {OwnerId.UnitOf(ownerId)}";
		}

		if (OwnerId.IsStructure(ownerId))
		{
			return $"structure {OwnerId.StructureOf(ownerId)}";
		}

		if (OwnerId.IsDefense(ownerId))
		{
			return $"defence {OwnerId.DefenseOf(ownerId)}";
		}

		return BotRoster.NameOf(OwnerId.PeerOf(ownerId));
	}

	private static void Bold(StringBuilder text, string value, HtnTextStyle style)
	{
		if (style == HtnTextStyle.Rich)
		{
			text.Append("[b]");
			HtnText.Escape(text, value);
			text.Append("[/b]");
			return;
		}

		text.Append(value);
	}

	private static void Colored(StringBuilder text, string value, string color, HtnTextStyle style)
	{
		if (style == HtnTextStyle.Rich)
		{
			text.Append("[color=#").Append(color).Append(']');
			HtnText.Escape(text, value);
			text.Append("[/color]");
			return;
		}

		text.Append(value);
	}
}
