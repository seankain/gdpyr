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

	/// <summary>
	/// The map this process plays on: <see cref="MapCatalog.Default"/> until a host
	/// picks another in the main menu, or a client is told the one its host picked
	/// (docs/LAN.md §3.4). Every path into the world loads this.
	/// </summary>
	public static MapInfo Map { get; private set; } = MapCatalog.Default;

	/// <summary>The scene <see cref="Map"/> is.</summary>
	public static string WorldScenePath => Map.ScenePath;

	/// <summary>
	/// What the host picked in the main menu's host panel, or null: a round launched
	/// from the command line, offline, joined or watched is played on the game mode's
	/// numbers and the flags' (<c>Match/default_gamemode.tres</c>). Read by the bot
	/// director when it is built, which is after the menu has handed over.
	/// </summary>
	public static Gdpyr.Match.HostSettings Host { get; private set; }

	/// <summary>Plays the next world on this map. The menu calls it before it changes scene.</summary>
	public static void SelectMap(MapInfo map)
	{
		if (map.IsValid)
		{
			Map = map;
		}
	}

	/// <summary>Records what the host panel was set to, and plays its map.</summary>
	public static void HostWith(Gdpyr.Match.HostSettings settings)
	{
		Host = settings;
		if (settings != null)
		{
			SelectMap(settings.Map);
		}
	}

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
