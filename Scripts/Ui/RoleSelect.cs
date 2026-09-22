using Gdpyr.Match;
using Gdpyr.Sim;
using Godot;

namespace Gdpyr.Ui;

/// <summary>
/// The panel a round opens on: six on the ground, or two above it
/// (docs/IMPLEMENTATION_PLAN.md §M3).
///
/// It is up while the server is holding this player off the field — on joining, and
/// again at the start of every round — and it closes on the click. The click is a
/// request like any other: the server may hand back the ground force when both
/// strategist chairs are taken, and that correction arrives in the snapshot's team
/// bit rather than as a second menu (docs/NETCODE.md §7).
///
/// <see cref="TeamSelect"/>'s two keys keep working and mean the same thing. They
/// are the fast way to change your mind mid-round; this is the one that asks.
/// </summary>
public partial class RoleSelect : CanvasLayer
{
	private Control _root;
	private Label _subtitle;
	private Button _ground;
	private Button _strategist;

	private bool _shown;

	public override void _Ready()
	{
		// Above the combat HUD, and drawn while the tree is paused: this is the one
		// thing on screen that a paused player still has to be able to answer.
		Layer = 3;
		ProcessMode = ProcessModeEnum.Always;

		Build();
	}

	/// <summary>Called once per simulation tick by the combat manager, which owns this node.</summary>
	public void Refresh(bool awaiting, Team team)
	{
		if (awaiting == _shown)
		{
			// The pause menu takes the mouse back when it closes, and it can close
			// over this panel. While the panel is up it owns the pointer — except
			// while the tree is paused, when the pause menu does.
			if (awaiting && !GetTree().Paused && Input.MouseMode != Input.MouseModeEnum.Visible)
			{
				Input.MouseMode = Input.MouseModeEnum.Visible;
			}

			return;
		}

		_shown = awaiting;
		_root.Visible = awaiting;

		if (awaiting)
		{
			_subtitle.Text = "the round is waiting for you";
			_ground.GrabFocus();
			Input.MouseMode = Input.MouseModeEnum.Visible;
			return;
		}

		// Hand the mouse back to whichever game this player just chose. Derived from
		// the side rather than remembered, because the answer may have been given with
		// a key, with a click, or by the server correcting a full strategist's chair.
		Input.MouseMode = team == Team.Strategist
			? Input.MouseModeEnum.Visible
			: Input.MouseModeEnum.Captured;
	}

	private void Choose(Team team) => CombatManager.Instance?.RequestTeam(team);

	private void Build()
	{
		_root = new Control { Visible = false };
		_root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
		AddChild(_root);

		var background = new ColorRect
		{
			Color = new Color(0f, 0f, 0f, 0.55f),
			MouseFilter = Control.MouseFilterEnum.Stop,
		};
		background.SetAnchorsPreset(Control.LayoutPreset.FullRect);
		_root.AddChild(background);

		var centre = new CenterContainer();
		centre.SetAnchorsPreset(Control.LayoutPreset.FullRect);
		_root.AddChild(centre);

		var column = new VBoxContainer();
		column.AddThemeConstantOverride("separation", 12);
		centre.AddChild(column);

		var title = new Label
		{
			Text = "pick a side",
			HorizontalAlignment = HorizontalAlignment.Center,
		};
		title.AddThemeFontSizeOverride("font_size", 32);
		column.AddChild(title);

		_subtitle = new Label
		{
			Text = "the round is waiting for you",
			HorizontalAlignment = HorizontalAlignment.Center,
		};
		column.AddChild(_subtitle);

		_ground = NewChoice(column, "Ground force  (F1)",
			"a rifle, a ticket pool, and whatever the strategist sends at you");

		_strategist = NewChoice(column, "Strategist  (F2)",
			"the map from above, the units on it, and only what they can see — two seats");

		_ground.Pressed += () => Choose(Team.GroundForce);
		_strategist.Pressed += () => Choose(Team.Strategist);
	}

	private static Button NewChoice(Control parent, string text, string blurb)
	{
		var button = new Button
		{
			Text = text,
			CustomMinimumSize = new Vector2(480f, 48f),
		};
		parent.AddChild(button);

		var label = new Label
		{
			Text = blurb,
			HorizontalAlignment = HorizontalAlignment.Center,
			MouseFilter = Control.MouseFilterEnum.Ignore,
		};
		label.AddThemeColorOverride("font_color", new Color(0.75f, 0.75f, 0.78f));
		parent.AddChild(label);

		return button;
	}
}
