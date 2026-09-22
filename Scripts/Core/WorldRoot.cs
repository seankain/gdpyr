using Godot;

namespace Gdpyr.Core;

/// <summary>
/// The map's root node. Its whole job is to say that the map is loaded
/// (<see cref="Session"/>).
///
/// The simulation is driven by autoloads, which exist before any scene does and
/// survive every scene change. This is the node that tells them the world they
/// simulate is actually in the tree — whether it was reached through the main menu
/// or by a command line that skipped it — so that a process sitting in the menu
/// does not tick a round on a scene with no spawn points in it.
/// </summary>
public partial class WorldRoot : Node3D
{
	public override void _EnterTree() => Session.EnterGame();
}
