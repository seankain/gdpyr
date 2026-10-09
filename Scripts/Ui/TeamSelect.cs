using Gdpyr.Match;
using Gdpyr.Net;
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
///
/// F3 is the third answer: watch instead of play (docs/AI_DEBUG.md §2), and F3 again
/// to come back. A spectator has no side to change, so F1 and F2 while spectating
/// bring the role menu back, which is where a side is chosen from nothing.
/// </summary>
public partial class TeamSelect : Node
{
	public override void _Ready() => ProcessMode = ProcessModeEnum.Always;

	public override void _UnhandledInput(InputEvent @event)
	{
		if (@event.IsActionPressed("team_spectate"))
		{
			if (PlayerManager.Instance is { } players)
			{
				players.RequestSpectate(!players.IsSpectating);
			}

			GetViewport().SetInputAsHandled();
		}
		else if (PlayerManager.Instance is { IsSpectating: true } spectating
			&& (@event.IsActionPressed("team_ground") || @event.IsActionPressed("team_strategist")))
		{
			spectating.RequestSpectate(false);
			GetViewport().SetInputAsHandled();
		}
		else if (@event.IsActionPressed("team_ground"))
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
