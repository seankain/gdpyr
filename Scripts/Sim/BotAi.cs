namespace Gdpyr.Sim;

/// <summary>
/// What the computer players decide with (docs/HTN_BOTS.md §4.2 rule 6). Chosen
/// by <c>--bot-ai</c>, else by the game mode's <c>BotAi</c>.
/// </summary>
public enum BotAi
{
	/// <summary>
	/// The goal selection from before the HTN: <c>BotPilot</c>,
	/// <c>UnitManager.SimulateUnit</c> over <c>UnitBrain</c>, and
	/// <c>BotStrategist</c>. The default until H6, and still the RL baseline: the
	/// training and evaluation scripts pin it (§8, D5).
	/// </summary>
	Legacy = 0,

	/// <summary>
	/// The HTN planners of docs/HTN_BOTS.md §5: the ground bot's (H2), the unit's
	/// (H3) and the strategist's commander (H4), with unit resupply (H5). The
	/// default from H6.
	/// </summary>
	Htn = 1,
}

/// <summary>The spellings <c>--bot-ai</c> takes.</summary>
public static class BotAiNames
{
	public const string Legacy = "legacy";
	public const string Htn = "htn";

	/// <summary>What the bots decide with when neither <c>--bot-ai</c> nor the game mode says (H6).</summary>
	public const BotAi Default = BotAi.Htn;

	/// <summary><c>--bot-ai</c>, else the game mode's, else <see cref="Default"/>.</summary>
	public static BotAi Resolve(BotAi? option, BotAi? gameMode) => option ?? gameMode ?? Default;

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
