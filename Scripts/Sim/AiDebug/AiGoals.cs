using System;
using System.Collections.Generic;
using Gdpyr.Sim.Htn;

namespace Gdpyr.Sim.AiDebug;

/// <summary>
/// The five tasks every layer is built round (docs/HTN_BOTS.md §5.4), plus the two a
/// unit has that are not one of them: what the world overlay colours a goal by, so
/// that a retreat reads as a retreat whichever layer is doing it.
/// </summary>
public enum AiTaskFamily : byte
{
	None = 0,
	Retreat = 1,
	Resupply = 2,
	Attack = 3,
	DefendZone = 4,
	Recon = 5,

	/// <summary>A unit's build order: working, or taking cover from it.</summary>
	Build = 6,

	/// <summary>A unit's move order, never second-guessed (§5.2).</summary>
	Obey = 7,
}

/// <summary>Goal bytes as names and families, per domain.</summary>
public static class AiGoals
{
	public static string Name(HtnDomainKind kind, byte goal) =>
		goal == 0 ? "idle" : HtnSchema.For(kind)?.GoalName(goal) ?? goal.ToString();

	public static AiTaskFamily Family(HtnDomainKind kind, byte goal) => kind switch
	{
		HtnDomainKind.Ground => (GroundGoal)goal switch
		{
			GroundGoal.LeaveDefences or GroundGoal.FallBack => AiTaskFamily.Retreat,
			GroundGoal.Reload or GroundGoal.PathToLocker or GroundGoal.UseLocker => AiTaskFamily.Resupply,
			GroundGoal.EngageArmour or GroundGoal.TakeFocusTarget or GroundGoal.AdvanceWithBuddy
				or GroundGoal.Engage or GroundGoal.InvestigateGhost or GroundGoal.TakeCover
				or GroundGoal.HoldCover => AiTaskFamily.Attack,
			GroundGoal.TakeNode or GroundGoal.HoldNode => AiTaskFamily.DefendZone,
			GroundGoal.SweepZone or GroundGoal.Standoff => AiTaskFamily.Recon,
			_ => AiTaskFamily.None,
		},
		HtnDomainKind.Unit => (UnitGoal)goal switch
		{
			UnitGoal.Retreat => AiTaskFamily.Retreat,
			UnitGoal.Resupply => AiTaskFamily.Resupply,
			UnitGoal.TakeCover or UnitGoal.Work => AiTaskFamily.Build,
			UnitGoal.WaitForSquad or UnitGoal.EngageFocus or UnitGoal.Support or UnitGoal.Advance
				or UnitGoal.HoldCover => AiTaskFamily.Attack,
			UnitGoal.EngageInLeash or UnitGoal.AnswerCall or UnitGoal.HoldPost => AiTaskFamily.DefendZone,
			UnitGoal.Patrol => AiTaskFamily.Recon,
			UnitGoal.Obey => AiTaskFamily.Obey,
			_ => AiTaskFamily.None,
		},
		HtnDomainKind.Squad => (CommandGoal)goal switch
		{
			CommandGoal.FallBack => AiTaskFamily.Retreat,
			CommandGoal.Station or CommandGoal.Refill => AiTaskFamily.Resupply,
			CommandGoal.Stage or CommandGoal.Strike => AiTaskFamily.Attack,
			CommandGoal.Reinforce or CommandGoal.Hold => AiTaskFamily.DefendZone,
			CommandGoal.Scout => AiTaskFamily.Recon,
			_ => AiTaskFamily.None,
		},
		_ => AiTaskFamily.None,
	};

	/// <summary>The family as the docs name it: "Defend Zone", not "DefendZone".</summary>
	public static string Describe(AiTaskFamily family) => family switch
	{
		AiTaskFamily.DefendZone => "Defend Zone",
		AiTaskFamily.None => "-",
		_ => family.ToString(),
	};

	/// <summary>RGB hex per family, shared by the panel and the world overlay.</summary>
	public static string Color(AiTaskFamily family) => family switch
	{
		AiTaskFamily.Retreat => "ff5a5a",
		AiTaskFamily.Resupply => "4fb3ff",
		AiTaskFamily.Attack => "ff9f2e",
		AiTaskFamily.DefendZone => "5fd35f",
		AiTaskFamily.Recon => "c7a6ff",
		AiTaskFamily.Build => "ffe066",
		AiTaskFamily.Obey => "f0f0f0",
		_ => "9aa0a6",
	};

	/// <summary>The domain an entity plans with.</summary>
	public static HtnDomainKind DomainOf(AiEntityKind kind) => kind switch
	{
		AiEntityKind.Player => HtnDomainKind.Ground,
		AiEntityKind.Unit => HtnDomainKind.Unit,
		AiEntityKind.Squad => HtnDomainKind.Squad,
		_ => HtnDomainKind.None,
	};

	/// <summary>A label's detail byte, in words: a ground bot's role, or a unit's order.</summary>
	public static string Detail(AiEntityKind kind, byte detail) => kind switch
	{
		AiEntityKind.Player => ((GroundRole)detail).ToString(),
		AiEntityKind.Unit => ((OrderKind)detail).ToString(),
		_ => string.Empty,
	};
}

/// <summary>
/// What the console's <c>ai_*</c> commands take (docs/AI_DEBUG.md §7): <c>bot 3</c>,
/// <c>peer 1234</c>, <c>unit 42</c>, <c>squad 2</c>, <c>squad 2 bot 7</c>. A squad
/// with no strategist named is the first computer strategist's, which is
/// <see cref="FirstStrategist"/> until the caller resolves it.
/// </summary>
public static class AiTargets
{
	/// <summary>Stands for "the first computer strategist" in a squad reference nobody resolved yet.</summary>
	public const int FirstStrategist = 0;

	public const string Usage = "bot <n> | peer <id> | unit <id> | squad <slot> [bot <n>]";

	public static bool TryParse(IReadOnlyList<string> args, out AiEntityRef target, out string error)
	{
		target = default;
		error = null;

		var words = new List<string>();
		foreach (string arg in args ?? Array.Empty<string>())
		{
			Split(arg, words);
		}

		if (words.Count == 0)
		{
			error = $"which one? {Usage}";
			return false;
		}

		int at = 0;
		if (!TryOne(words, ref at, out target, out error))
		{
			return false;
		}

		if (target.Kind == AiEntityKind.Squad && at < words.Count)
		{
			if (!TryOne(words, ref at, out AiEntityRef owner, out error) || owner.Kind != AiEntityKind.Player)
			{
				error ??= $"a squad belongs to a strategist: squad {target.Slot} bot <n>";
				return false;
			}

			target = AiEntityRef.Squad(owner.Id, target.Slot);
		}

		if (at < words.Count)
		{
			error = $"unexpected '{words[at]}'; {Usage}";
			return false;
		}

		return true;
	}

	private static bool TryOne(List<string> words, ref int at, out AiEntityRef target, out string error)
	{
		target = default;
		error = null;
		string kind = words[at++].ToLowerInvariant();

		if (at >= words.Count || !int.TryParse(words[at], out int number) || number < 0)
		{
			error = at < words.Count ? $"'{words[at]}' is not a number" : $"'{kind}' needs a number";
			return false;
		}

		at++;
		switch (kind)
		{
			case "bot":
			case "b":
				if (number < 1 || number > BotRoster.MaxBots)
				{
					error = $"bots are numbered 1-{BotRoster.MaxBots}";
					return false;
				}

				target = AiEntityRef.Player(BotRoster.PeerIdFor(number - 1));
				return true;

			case "peer":
			case "p":
				target = AiEntityRef.Player(number);
				return true;

			case "unit":
			case "u":
				if (number > ushort.MaxValue)
				{
					error = $"unit ids end at {ushort.MaxValue}";
					return false;
				}

				target = AiEntityRef.Unit((ushort)number);
				return true;

			case "squad":
			case "s":
				if (number > byte.MaxValue)
				{
					error = "squad slots are small numbers";
					return false;
				}

				target = AiEntityRef.Squad(FirstStrategist, number);
				return true;

			default:
				error = $"'{kind}' is not bot, peer, unit or squad";
				return false;
		}
	}

	/// <summary>Splits <c>bot3</c> into <c>bot</c> and <c>3</c>, so either spelling works.</summary>
	private static void Split(string arg, List<string> into)
	{
		if (string.IsNullOrWhiteSpace(arg))
		{
			return;
		}

		arg = arg.Trim();
		int digits = arg.Length;
		while (digits > 0 && char.IsDigit(arg[digits - 1]))
		{
			digits--;
		}

		if (digits > 0 && digits < arg.Length)
		{
			into.Add(arg[..digits]);
			into.Add(arg[digits..]);
			return;
		}

		into.Add(arg);
	}
}
