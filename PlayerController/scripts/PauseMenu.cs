using Godot;

// A simple pause menu shown when the player presses Escape.
// Pauses the game, frees the mouse and offers Resume / Quit.
public partial class PauseMenu : Control
{
	private Button _resumeButton;
	private Button _quitButton;

	public override void _Ready()
	{
		// Keep processing input even while the SceneTree is paused so the
		// menu buttons and the Escape toggle keep working.
		ProcessMode = ProcessModeEnum.Always;

		AnchorRight = 1;
		AnchorBottom = 1;
		OffsetRight = 0;
		OffsetBottom = 0;

		BuildUi();

		Hide();
	}

	private void BuildUi()
	{
		// Dim the game behind the menu.
		var background = new ColorRect
		{
			Color = new Color(0, 0, 0, 0.5f),
			MouseFilter = MouseFilterEnum.Stop,
		};
		background.SetAnchorsPreset(LayoutPreset.FullRect);
		AddChild(background);

		var center = new CenterContainer();
		center.SetAnchorsPreset(LayoutPreset.FullRect);
		AddChild(center);

		var buttons = new VBoxContainer();
		buttons.AddThemeConstantOverride("separation", 12);
		center.AddChild(buttons);

		_resumeButton = new Button
		{
			Text = "Resume",
			CustomMinimumSize = new Vector2(160, 40),
		};
		_resumeButton.Pressed += Resume;
		buttons.AddChild(_resumeButton);

		_quitButton = new Button
		{
			Text = "Quit",
			CustomMinimumSize = new Vector2(160, 40),
		};
		_quitButton.Pressed += Quit;
		buttons.AddChild(_quitButton);
	}

	public override void _UnhandledInput(InputEvent @event)
	{
		if (@event is InputEventKey eventKey && eventKey.Pressed && !eventKey.Echo
			&& eventKey.Keycode == Key.Escape)
		{
			Toggle();
			GetViewport().SetInputAsHandled();
		}
	}

	private void Toggle()
	{
		if (Visible)
		{
			Resume();
		}
		else
		{
			Pause();
		}
	}

	private void Pause()
	{
		GetTree().Paused = true;
		Input.MouseMode = Input.MouseModeEnum.Visible;
		Show();
		_resumeButton.GrabFocus();
	}

	private void Resume()
	{
		Hide();
		GetTree().Paused = false;
		Input.MouseMode = Input.MouseModeEnum.Captured;
	}

	private void Quit()
	{
		GetTree().Quit();
	}
}
