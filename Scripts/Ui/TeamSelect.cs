using Gdpyr.Match;
using Gdpyr.Sim;
using Godot;

namespace Gdpyr.Ui;

/// <summary>
/// Two keys: F1 puts you on the ground, F2 in the strategist's chair
/// (docs/IMPLEMENTATION_PLAN.md §3, "Ui/ Role select").
///
/// The panel that *asks* is <see cref="RoleSelect"/>, and it is up only while the
/// server is holding this player off the field — at a join and at the start of a
/// round. These two keys are the other half: mid-round, with the mouse captured and
/// a rifle in your hands, changing your mind should not cost a modal.
///
/// Either way the request is a request: the server enforces the two-strategist cap
/// and the answer arrives in the next snapshot's team bit, which is what the HUD
/// reads.
/// </summary>
public partial class TeamSelect : Node
{
	public override void _Ready() => ProcessMode = ProcessModeEnum.Always;

	public override void _UnhandledInput(InputEvent @event)
	{
		if (@event.IsActionPressed("team_ground"))
		{
			CombatManager.Instance?.RequestTeam(Team.GroundForce);
			GetViewport().SetInputAsHandled();
		}
		else if (@event.IsActionPressed("team_strategist"))
		{
			CombatManager.Instance?.RequestTeam(Team.Strategist);
			GetViewport().SetInputAsHandled();
		}
	}
}
