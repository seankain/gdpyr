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
				if (inspect.TryValue(AiValueKey.Commander, out int commander)
					&& inspect.TryValue(AiValueKey.CommanderSquad, out int slot))
				{
					text.Append($" · in squad {slot} of {BotRoster.NameOf(commander)}");
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

		return text.ToString();
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
