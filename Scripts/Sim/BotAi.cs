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
	/// The HTN planners of docs/HTN_BOTS.md §5: the ground bot's (H2), the unit's
	/// (H3) and the strategist's commander (H4).
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
