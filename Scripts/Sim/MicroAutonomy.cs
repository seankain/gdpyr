namespace Gdpyr.Sim;

/// <summary>
/// Whether a unit uses the tactics of docs/TACTICS.md §5 on its own, or carries out
/// its orders as given (§5.9). The strategist's to set, per unit; the unit's planner
/// observes it as a fact (<c>UnitFact.MicroAutonomy</c>), so switching it is a replan
/// like any other change. The numbers are on the wire.
/// </summary>
public enum MicroAutonomy : byte
{
	/// <summary>Orders carried out as the unit domain did before docs/TACTICS.md: direct control.</summary>
	Off = 0,

	/// <summary>The unit takes cover from fire, suppresses, throws, bounds and peeks on its own.</summary>
	On = 1,
}

/// <summary>The spellings <c>--unit-autonomy</c>, a scenario and the agent API take.</summary>
public static class MicroAutonomyNames
{
	public const string Off = "off";
	public const string On = "on";

	/// <summary>
	/// What new units start with when neither <c>--unit-autonomy</c> nor the game mode
	/// says: off until docs/TACTICS.md T8 has measured the tactics, so a round plays as
	/// it did unless a strategist or a scenario turns them on.
	/// </summary>
	public const MicroAutonomy Default = MicroAutonomy.Off;

	/// <summary><c>--unit-autonomy</c>, else the game mode's, else <see cref="Default"/>.</summary>
	public static MicroAutonomy Resolve(MicroAutonomy? option, MicroAutonomy? gameMode) =>
		option ?? gameMode ?? Default;

	public static bool TryParse(string text, out MicroAutonomy mode)
	{
		switch (text?.Trim().ToLowerInvariant())
		{
			case On:
				mode = MicroAutonomy.On;
				return true;
			case Off:
				mode = MicroAutonomy.Off;
				return true;
			default:
				mode = MicroAutonomy.Off;
				return false;
		}
	}

	/// <summary>Whether a byte off the wire names a mode.</summary>
	public static bool IsValid(byte raw) => raw <= (byte)MicroAutonomy.On;

	public static string Name(MicroAutonomy mode) => mode == MicroAutonomy.On ? On : Off;
}
