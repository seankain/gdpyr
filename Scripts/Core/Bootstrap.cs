using Gdpyr.Sim;
using Godot;

namespace Gdpyr.Core;

/// <summary>
/// First autoload. Parses the command line once, pins the process-wide engine
/// settings the simulation depends on, and publishes the result for everything
/// that runs after it (notably <c>NetworkManager</c>, the next autoload).
/// </summary>
public partial class Bootstrap : Node
{
	/// <summary>
	/// The parsed command line. Reads as <see cref="LaunchOptions.Offline"/> until
	/// this autoload's <c>_Ready</c> has run, which is before any scene node's.
	/// </summary>
	public static LaunchOptions Options { get; private set; } = LaunchOptions.Offline;

	/// <summary>
	/// True in a build exported with "Export as dedicated server". Client-only code
	/// (mouse capture, viewmodels, audio) must be guarded with this
	/// (docs/DEPLOYMENT.md §2).
	/// </summary>
	public static bool IsDedicatedServer => OS.HasFeature("dedicated_server");

	public override void _Ready()
	{
		var options = LaunchOptions.Parse(OS.GetCmdlineUserArgs(), IsDedicatedServer);

		if (options.Error != null)
		{
			// Quit rather than fall back to offline: a dedicated server that silently
			// stops listening is worse than one that fails to start.
			GD.PrintErr($"gdpyr: {options.Error}");
			GD.PrintErr(LaunchOptions.Usage);
			GetTree().Quit(1);
			return;
		}

		// The map, before anything reads the options: a reference that names nothing, or a
		// file that is not a map, is a launch error like any other (docs/LEVEL_EDITOR.md §9.5).
		if (options.MapReference != null)
		{
			if (!Gdpyr.Level.MapLibrary.TryResolve(options.MapReference, out MapInfo map,
				out Gdpyr.Sim.Level.MapDocument document, out string mapError))
			{
				GD.PrintErr($"gdpyr: --map {options.MapReference}: {mapError}");
				GetTree().Quit(1);
				return;
			}

			Session.SelectResolved(map, document);
			GD.Print($"[map] playing {map}" + (map.IsData ? $" from {map.Path}, sha-256 {map.Hash}" : string.Empty));
		}

		Options = options;

		// The simulation is a fixed-tick simulation; nothing may depend on the render
		// rate (docs/NETCODE.md §2).
		Engine.PhysicsTicksPerSecond = SimConfig.TickRate;

		if (options.Mode == LaunchMode.Server)
		{
			// A headless Godot main loop runs as fast as the CPU allows and will hold a
			// core at 100%, which burns t3 CPU credits until the box throttles
			// mid-playtest (docs/DEPLOYMENT.md §5).
			Engine.MaxFps = SimConfig.TickRate;
		}

		GD.Print($"[boot] gdpyr {options} | tick {SimConfig.TickRate} Hz"
			+ $" | dedicated server: {IsDedicatedServer}");
	}
}
