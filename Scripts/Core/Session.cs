namespace Gdpyr.Core;

/// <summary>What this process is doing right now: a menu, or a round.</summary>
public enum SessionPhase : byte
{
	/// <summary>The main menu is up. Nothing is simulated and no tick is advanced.</summary>
	Menu,

	/// <summary>The map is loaded and <see cref="Gdpyr.Net.PlayerManager"/> is ticking.</summary>
	InGame,
}

/// <summary>
/// Which of the two things this process is doing, for the autoloads that outlive
/// both (docs/IMPLEMENTATION_PLAN.md §3).
///
/// The managers are autoloads and the menu is a scene, so the menu cannot simply
/// not be the game: <see cref="Gdpyr.Net.PlayerManager"/> would tick, collect the
/// menu scene's (nonexistent) spawn points and open the agent channel before
/// anybody had chosen a server. One flag, read at the top of the one node that
/// ticks, is the whole gate.
///
/// Static rather than a sixth autoload for the same reason
/// <see cref="Bootstrap.Options"/> is: it is process state that is written once and
/// read everywhere, and an autoload would add an ordering question to answer.
/// </summary>
public static class Session
{
	/// <summary>The menu. Also the main scene, so a build with no flags starts here.</summary>
	public const string MenuScenePath = "res://Scenes/MainMenu.tscn";

	/// <summary>The map. One map, iterated on (docs/IMPLEMENTATION_PLAN.md §5).</summary>
	public const string WorldScenePath = "res://Scenes/Test.tscn";

	/// <summary>
	/// Menu until something says otherwise. <see cref="WorldRoot"/> flips it when the
	/// map enters the tree, which is the one moment that is true however the map was
	/// reached — from the menu, or from a command line that skipped it.
	/// </summary>
	public static SessionPhase Phase { get; private set; } = SessionPhase.Menu;

	public static bool InGame => Phase == SessionPhase.InGame;

	public static void EnterGame() => Phase = SessionPhase.InGame;

	public static void EnterMenu() => Phase = SessionPhase.Menu;
}
