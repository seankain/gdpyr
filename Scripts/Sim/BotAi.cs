namespace Gdpyr.Sim;

/// <summary>
/// What the computer players decide with (docs/HTN_BOTS.md §4.2 rule 6). Chosen
/// by <c>--bot-ai</c>, else by the game mode's <c>BotAi</c>.
/// </summary>
public enum BotAi
{
	/// <summary>
	/// Today's goal selection: <c>BotPilot</c>, <c>UnitManager.SimulateUnit</c> over
	/// <c>UnitBrain</c>, and <c>BotStrategist</c>. The default, and the RL baseline,
	/// until H6 flips it (§8, D5).
	/// </summary>
	Legacy,

	/// <summary>
	/// The HTN planners of docs/HTN_BOTS.md §5. Each decider switches over as its
	/// phase lands (H2 ground bot, H3 unit, H4 strategist); until then it runs legacy.
	/// </summary>
	Htn,
}

/// <summary>The spellings <c>--bot-ai</c> takes.</summary>
public static class BotAiNames
{
	public const string Legacy = "legacy";
	public const string Htn = "htn";

	public static bool TryParse(string text, out BotAi ai)
	{
		switch (text?.Trim().ToLowerInvariant())
		{
			case Legacy:
				ai = BotAi.Legacy;
				return true;
			case Htn:
				ai = BotAi.Htn;
				return true;
			default:
				ai = BotAi.Legacy;
				return false;
		}
	}

	public static string Name(BotAi ai) => ai == BotAi.Htn ? Htn : Legacy;
}
