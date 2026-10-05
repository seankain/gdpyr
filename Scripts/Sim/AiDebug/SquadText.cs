using System;
using System.Text;
using Gdpyr.Sim.Htn;

namespace Gdpyr.Sim.AiDebug;

/// <summary>
/// A computer strategist's commander as text (docs/AI_DEBUG.md §4.1): one row per
/// squad — role, size, task, target, running goal, phase, strength against what it
/// formed with — for the console's <c>ai_squads</c> and the spectator's strategist tab.
/// </summary>
public static class SquadText
{
	/// <summary>The debug HUD's letters (<c>commander</c> row): g garrison, s scout, r supply, a assault, f forming.</summary>
	public static string RoleLetter(AiSquad squad) => (CommandRole)squad.Role switch
	{
		CommandRole.Garrison => "g",
		CommandRole.Scout => "s",
		CommandRole.Supply => "r",
		_ => (squad.Flags & AiSquadFlags.Ready) != 0 ? "a" : "f",
	};

	public static string RoleName(AiSquad squad) => (CommandRole)squad.Role switch
	{
		CommandRole.Garrison => "garrison",
		CommandRole.Scout => "scout",
		CommandRole.Supply => "supply",
		_ => (squad.Flags & AiSquadFlags.Ready) != 0 ? "assault" : "forming",
	};

	/// <summary>What is left of it, as a share of the most it has been: what Retreat and Resupply measure (§5.3).</summary>
	public static string Strength(AiSquad squad) =>
		squad.StrengthAtFormation > 0f ? $"{MathF.Round(100f * squad.Strength / squad.StrengthAtFormation):0}%" : "-";

	public static string Flags(AiSquad squad)
	{
		var text = new StringBuilder();
		Append(text, squad.Flags, AiSquadFlags.Engaged, "engaged");
		Append(text, squad.Flags, AiSquadFlags.FallingBack, "falling back");
		Append(text, squad.Flags, AiSquadFlags.Depleted, "depleted");
		Append(text, squad.Flags, AiSquadFlags.Healable, "healable");
		Append(text, squad.Flags, AiSquadFlags.Short, "short");
		return text.ToString();
	}

	/// <summary>What the task is aimed at: <c>zone 3</c>, <c>bot 2</c>, <c>sweep</c>.</summary>
	public static string Target(AiSquad squad)
	{
		switch ((CommandTarget)squad.Target)
		{
			case CommandTarget.Contact:
				return squad.TargetOwnerId > 0 ? BotRoster.NameOf(squad.TargetOwnerId) : "contact";
			case CommandTarget.Sweep:
				return squad.Zone >= 0 ? $"sweep {squad.Zone}" : "sweep";
			default:
				return squad.Zone >= 0 ? $"zone {squad.Zone}" : "-";
		}
	}

	/// <summary>The commander's squads as a table. <paramref name="link"/> makes each row a BBCode link to that squad.</summary>
	public static string Table(AiCommander commander, HtnTextStyle style, bool link = false)
	{
		var text = new StringBuilder();
		if (commander == null)
		{
			return string.Empty;
		}

		text.Append($"{BotRoster.NameOf(commander.PeerId)}'s commander: {commander.Squads.Count} squads");
		if (commander.ReconWanted)
		{
			text.Append(" · wants recon");
		}

		if (commander.BuyTimeZone >= 0)
		{
			text.Append($" · buying time at zone {commander.BuyTimeZone}");
		}

		text.Append('\n');
		string header = $"{"#",-3}{"role",-9}{"n",3}  {"task",-8}{"target",-10}{"goal",-10}{"phase",-11}{"str",5}  flags";
		text.Append(style == HtnTextStyle.Rich ? $"[color=#8a8f98]{header}[/color]" : header);

		foreach (AiSquad squad in commander.Squads)
		{
			string goal = AiGoals.Name(HtnDomainKind.Squad, squad.Goal);
			string row = $"{squad.Slot,-3}{RoleName(squad),-9}{squad.Members.Count,3}  "
				+ $"{(CommandTask)squad.Task,-8}{Clip(Target(squad), 9),-10}{goal,-10}{(SquadPhase)squad.Phase,-11}"
				+ $"{Strength(squad),5}  {Flags(squad)}";

			text.Append('\n');
			if (style != HtnTextStyle.Rich)
			{
				text.Append(row);
				continue;
			}

			string color = AiGoals.Color(AiGoals.Family(HtnDomainKind.Squad, squad.Goal));
			if (link)
			{
				text.Append($"[url=squad:{commander.PeerId}:{squad.Slot}]");
			}

			text.Append($"[color=#{color}]");
			HtnText.Escape(text, row);
			text.Append("[/color]");
			if (link)
			{
				text.Append("[/url]");
			}
		}

		return text.ToString();
	}

	private static string Clip(string text, int length) =>
		text.Length <= length ? text : text[..Math.Max(length - 1, 1)] + "…";

	private static void Append(StringBuilder text, AiSquadFlags flags, AiSquadFlags flag, string word)
	{
		if ((flags & flag) == 0)
		{
			return;
		}

		if (text.Length > 0)
		{
			text.Append(' ');
		}

		text.Append(word);
	}
}
