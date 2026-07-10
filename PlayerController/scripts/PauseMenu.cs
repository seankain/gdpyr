using Godot;

// Behavior for PauseMenu.tscn. Shown when the player presses Escape:
// pauses the game, frees the mouse and offers Resume / Quit.
public partial class PauseMenu : Control
{
	private Button _resumeButton;
	private Button _quitButton;

	public override void _Ready()
	{
		// Keep processing input even while the SceneTree is paused so the
		// menu buttons and the Escape toggle keep working.
		ProcessMode = ProcessModeEnum.Always;

		_resumeButton = GetNode<Button>("Center/Buttons/ResumeButton");
		_quitButton = GetNode<Button>("Center/Buttons/QuitButton");

		_resumeButton.Pressed += Resume;
		_quitButton.Pressed += Quit;

		Hide();
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
