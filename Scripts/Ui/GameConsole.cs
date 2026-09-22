using System;
using System.Collections.Generic;
using System.IO;
using Gdpyr.Core;
using Gdpyr.Net;
using Gdpyr.Sim.Demo;
using Godot;

namespace Gdpyr.Ui;

/// <summary>
/// The drop-down console, on <c>~</c> (docs/DEMOS.md §3).
///
/// Sixth autoload, and the only one that is not part of the simulation: it exists
/// at the main menu and in the map alike, because <c>playdemo</c> is a thing you
/// ask for before there is a round and <c>record</c> is a thing you ask for during
/// one.
///
/// It owns no commands of its own beyond <c>help</c>, <c>clear</c>, <c>echo</c> and
/// <c>quit</c>. What a command *means* is a
/// <see cref="ConsoleCommandTable"/>, which is engine-free and unit-tested; this
/// is the part with a text box in it.
///
/// <c>~</c> rather than <c>`</c> because <c>`</c> was already the net debug HUD
/// and has been since M1 (docs/NETCODE.md §8). They are the same physical key, and
/// the two actions are matched exactly — with shift and without — so neither
/// steals the other's press.
/// </summary>
public partial class GameConsole : CanvasLayer
{
	/// <summary>Lines kept in the scrollback. Beyond this the oldest go.</summary>
	private const int MaxLines = 400;

	/// <summary>Commands kept for the up arrow.</summary>
	private const int MaxHistory = 64;

	/// <summary>Fraction of the window the panel covers.</summary>
	private const float HeightFraction = 0.45f;

	public static GameConsole Instance { get; private set; }

	private readonly ConsoleCommandTable _commands = new();
	private readonly List<string> _history = new();
	private readonly List<string> _lines = new();

	private Control _root;
	private RichTextLabel _output;
	private LineEdit _entry;

	/// <summary>The one-line replay bar, up only while a demo is playing.</summary>
	private Label _replayBar;

	private int _historyCursor = -1;
	private Input.MouseModeEnum _mouseBefore = Input.MouseModeEnum.Visible;

	/// <summary>True while the console is down. Nothing behind it sees the keyboard.</summary>
	public static bool IsOpen => Instance?._root is { Visible: true };

	/// <summary>
	/// Puts a line in the scrollback from anywhere. Safe before the console exists
	/// and on a dedicated server, where it does not: the line goes to the log
	/// instead, which is the only place anybody would read it.
	/// </summary>
	public static void Print(string text)
	{
		if (Instance?._output == null)
		{
			GD.Print($"[console] {text}");
			return;
		}

		Instance.Append(text);
	}

	public override void _Ready()
	{
		Instance = this;

		// Nothing here is part of a round, and a paused tree still has to be able to
		// take a command.
		ProcessMode = ProcessModeEnum.Always;

		// Above everything: the role menu is layer 3, and a console that opens behind
		// the panel that is waiting for an answer is a console nobody can find.
		Layer = 10;

		if (Bootstrap.IsDedicatedServer)
		{
			// No window to drop anything down over. The demo flags on the command line
			// are how a headless process records (docs/DEMOS.md §4.3).
			SetProcessInput(false);
			return;
		}

		Build();
		RegisterCommands();
		Append("gdpyr console — `help` for what it takes, `~` to close.");
	}

	public override void _ExitTree()
	{
		// The flag is process state and this node is what sets it: going away while
		// the console is down would leave the keyboard blocked for whatever comes
		// next (Scripts/Core/InputFocus.cs).
		InputFocus.EndTextEntry();

		if (Instance == this)
		{
			Instance = null;
		}
	}

	/// <summary>
	/// The replay bar: where the read head is, and at what speed
	/// (docs/DEMOS.md §5.3).
	///
	/// It is here rather than in the net debug HUD because that panel lives in the
	/// local player's interface and a replay has no local player. This layer is the
	/// only thing on screen during playback that this process built.
	/// </summary>
	public override void _Process(double delta)
	{
		if (_replayBar == null)
		{
			return;
		}

		if (PlayerManager.Instance?.Playback is not { } playback)
		{
			_replayBar.Visible = false;
			return;
		}

		_replayBar.Visible = true;
		_replayBar.Text = $"{playback.FileName}   {playback.Seconds:0.0}s   tick {playback.Tick}"
			+ (playback.Speed > 1 ? $"   x{playback.Speed}" : string.Empty)
			+ (playback.Paused ? "   paused" : string.Empty)
			+ (playback.Ended ? "   end" : string.Empty);
	}

	/// <summary>
	/// Handled in <c>_Input</c> rather than <c>_UnhandledInput</c>: the text box has
	/// focus while the console is open and would otherwise eat the key that closes
	/// it. Everything this consumes is marked handled, so nothing below sees it.
	/// </summary>
	public override void _Input(InputEvent @event)
	{
		if (_root == null)
		{
			return;
		}

		// Exact matching, both here and in the debug HUD: `~` and `` ` `` are the same
		// key with and without shift, and an inexact match would open both.
		if (@event.IsActionPressed("console", exactMatch: true))
		{
			Toggle();
			GetViewport().SetInputAsHandled();
			return;
		}

		if (!_root.Visible || @event is not InputEventKey { Pressed: true, Echo: false } key)
		{
			return;
		}

		switch (key.Keycode)
		{
			case Key.Escape:
				Close();
				GetViewport().SetInputAsHandled();
				break;

			case Key.Up:
				Recall(1);
				GetViewport().SetInputAsHandled();
				break;

			case Key.Down:
				Recall(-1);
				GetViewport().SetInputAsHandled();
				break;

			case Key.Tab:
				Complete();
				GetViewport().SetInputAsHandled();
				break;
		}
	}

	public void Toggle()
	{
		if (_root.Visible)
		{
			Close();
		}
		else
		{
			Open();
		}
	}

	private void Open()
	{
		_mouseBefore = Input.MouseMode;
		_root.Visible = true;
		Input.MouseMode = Input.MouseModeEnum.Visible;

		// The pollers stop reading the device while this is set: typing `record` must
		// not also reload the weapon (see Core/InputFocus.cs).
		InputFocus.BeginTextEntry();

		_entry.Clear();
		_entry.GrabFocus();
		_historyCursor = -1;
	}

	private void Close()
	{
		_root.Visible = false;
		_entry.ReleaseFocus();
		InputFocus.EndTextEntry();
		Input.MouseMode = _mouseBefore;
	}

	private void Submit(string line)
	{
		_entry.Clear();
		_historyCursor = -1;

		if (string.IsNullOrWhiteSpace(line))
		{
			return;
		}

		Append($"> {line}");
		Remember(line);

		ConsoleResult result = _commands.Execute(line);
		for (int i = 0; i < result.Lines.Count; i++)
		{
			Append(result.Ok ? result.Lines[i] : $"[color=#ff9c7a]{Escape(result.Lines[i])}[/color]", raw: true);
		}
	}

	private void Remember(string line)
	{
		if (_history.Count == 0 || _history[^1] != line)
		{
			_history.Add(line);
		}

		if (_history.Count > MaxHistory)
		{
			_history.RemoveAt(0);
		}
	}

	private void Recall(int direction)
	{
		if (_history.Count == 0)
		{
			return;
		}

		_historyCursor = Math.Clamp(_historyCursor + direction, -1, _history.Count - 1);
		_entry.Text = _historyCursor < 0 ? string.Empty : _history[^(_historyCursor + 1)];
		_entry.CaretColumn = _entry.Text.Length;
	}

	private void Complete()
	{
		IReadOnlyList<string> matches = _commands.Complete(_entry.Text);
		if (matches.Count == 0)
		{
			return;
		}

		if (matches.Count == 1)
		{
			_entry.Text = matches[0] + " ";
			_entry.CaretColumn = _entry.Text.Length;
			return;
		}

		Append(string.Join("  ", matches));
	}

	private void Append(string text, bool raw = false)
	{
		_lines.Add(raw ? text : Escape(text));
		if (_lines.Count > MaxLines)
		{
			_lines.RemoveRange(0, _lines.Count - MaxLines);
		}

		if (_output == null)
		{
			return;
		}

		_output.Text = string.Join("\n", _lines);
		// Scrolled after the text is in, not before: the line count is what decides
		// where the bottom is.
		_output.ScrollToLine(Math.Max(_output.GetLineCount() - 1, 0));
	}

	/// <summary>
	/// Text a command produced is text, not markup. A demo called
	/// <c>[b]whatever</c> must not turn the rest of the scrollback bold.
	/// </summary>
	private static string Escape(string text) => text?.Replace("[", "[lb]") ?? string.Empty;

	// ---- the commands ------------------------------------------------------

	private void RegisterCommands()
	{
		_commands.Register("help", "help [command]", "what these do", Help);

		_commands.Register("record", "record <name>",
			"start writing a demo of this round, e.g. `record game.demo`", Record);

		_commands.Register("stoprecord", "stoprecord", "close the demo being recorded", StopRecord);

		_commands.Register("mark", "mark <text>", "put a note in the demo at this tick", Mark);

		_commands.Register("demos", "demos", "what is in the demo folder", ListDemos);

		_commands.Register("demoinfo", "demoinfo <name>", "what is in one, without playing it", DemoInfo);

		_commands.Register("playdemo", "playdemo <name>", "watch one", PlayDemo);

		_commands.Register("stopdemo", "stopdemo", "stop watching", StopDemo);

		_commands.Register("demospeed", "demospeed <1-8>", "recorded ticks per frame", DemoSpeed);

		_commands.Register("demopause", "demopause", "hold the read head where it is", DemoPause);

		_commands.Register("demofollow", "demofollow [peer]", "sit on a player's shoulder; no peer frees the camera",
			DemoFollow);

		_commands.Register("clear", "clear", "empty the scrollback", _ =>
		{
			_lines.Clear();
			if (_output != null)
			{
				_output.Text = string.Empty;
			}
			return ConsoleResult.Done();
		});

		_commands.Register("echo", "echo <text>", "say it back", args => ConsoleResult.Say(string.Join(" ", args)));

		_commands.Register("quit", "quit", "leave the game", _ =>
		{
			GetTree().Quit();
			return ConsoleResult.Done();
		});
	}

	private ConsoleResult Help(IReadOnlyList<string> args)
	{
		if (args.Count == 0)
		{
			var lines = new List<string>(_commands.Count + 1) { "commands:" };
			lines.AddRange(_commands.Describe());
			return new ConsoleResult(true, lines);
		}

		if (!_commands.TryGet(args[0], out ConsoleCommand command))
		{
			return ConsoleResult.Failed($"no command called '{args[0]}'");
		}

		return ConsoleResult.Say($"  {command.Usage}", $"  {command.Help}");
	}

	private static ConsoleResult Record(IReadOnlyList<string> args)
	{
		if (args.Count != 1)
		{
			return ConsoleResult.Failed("usage: record <name>");
		}

		if (PlayerManager.Instance is not { } players)
		{
			return ConsoleResult.Failed("there is no round to record");
		}

		return players.StartRecording(args[0], out string message)
			? ConsoleResult.Say(message)
			: ConsoleResult.Failed(message);
	}

	private static ConsoleResult StopRecord(IReadOnlyList<string> args)
	{
		if (PlayerManager.Instance is not { } players)
		{
			return ConsoleResult.Failed("not recording");
		}

		return players.StopRecording(out string message) ? ConsoleResult.Say(message) : ConsoleResult.Failed(message);
	}

	private static ConsoleResult Mark(IReadOnlyList<string> args)
	{
		if (args.Count == 0)
		{
			return ConsoleResult.Failed("usage: mark <text>");
		}

		if (PlayerManager.Instance is not { } players)
		{
			return ConsoleResult.Failed("not recording");
		}

		return players.Mark(string.Join(" ", args), out string message)
			? ConsoleResult.Say(message)
			: ConsoleResult.Failed(message);
	}

	private static ConsoleResult ListDemos(IReadOnlyList<string> args)
	{
		IReadOnlyList<FileInfo> files = DemoFiles.List();
		if (files.Count == 0)
		{
			return ConsoleResult.Say($"nothing in {DemoFiles.Root}");
		}

		var lines = new List<string>(files.Count + 1) { DemoFiles.Root + ":" };
		for (int i = 0; i < files.Count; i++)
		{
			lines.Add($"  {files[i].Name,-32} {DemoRecorder.FormatBytes(files[i].Length),10}"
				+ $"  {files[i].LastWriteTime:yyyy-MM-dd HH:mm}");
		}

		return new ConsoleResult(true, lines);
	}

	private static ConsoleResult DemoInfo(IReadOnlyList<string> args)
	{
		if (args.Count != 1)
		{
			return ConsoleResult.Failed("usage: demoinfo <name>");
		}

		string path = DemoFiles.Resolve(args[0], out string fileName, out string error);
		if (path == null)
		{
			return ConsoleResult.Failed(error);
		}

		if (!File.Exists(path))
		{
			return ConsoleResult.Failed($"no demo called '{fileName}'");
		}

		Stream stream = DemoFiles.TryOpen(path, out error);
		if (stream == null)
		{
			return ConsoleResult.Failed(error);
		}

		using (stream)
		{
			if (!DemoSummary.TryRead(stream, out DemoSummary summary, out error))
			{
				return ConsoleResult.Failed($"{fileName}: {error}");
			}

			var lines = new List<string> { fileName + ":" };
			lines.AddRange(summary.Describe());
			return new ConsoleResult(true, lines);
		}
	}

	private static ConsoleResult PlayDemo(IReadOnlyList<string> args)
	{
		if (args.Count != 1)
		{
			return ConsoleResult.Failed("usage: playdemo <name>");
		}

		if (Session.InGame && PlayerManager.Instance is { IsDemoSession: false })
		{
			// A live round cannot be torn down from here: the roster, the clock and the
			// prediction ledger are all built around this process's place in it, and
			// none of them is rebuilt by loading a file (see NetworkManager
			// .AbandonConnection). A map this process opened to watch demos in has none
			// of that, so a second demo goes straight over the first.
			return ConsoleResult.Failed("quit to the main menu to watch a demo");
		}

		NetworkManager.Instance?.PlayOffline();

		if (!PlayerManager.BeginPlaybackOf(args[0], out string message))
		{
			return ConsoleResult.Failed(message);
		}

		if (!Session.InGame)
		{
			// Deferred, because a scene cannot be changed from inside the signal of a
			// widget belonging to the scene that is going away — which is exactly where
			// this is, having been typed into a text box (see MainMenu.EnterWorld).
			Callable.From(EnterWorld).CallDeferred();
		}

		return ConsoleResult.Say(message, "`demofollow <peer>` to ride along, WASD to fly, `~` to close this.");
	}

	/// <summary>The scene change <c>playdemo</c> defers when it is run from the main menu.</summary>
	private static void EnterWorld()
	{
		SceneTree tree = Instance?.GetTree();
		if (tree == null)
		{
			return;
		}

		Error loaded = tree.ChangeSceneToFile(Session.WorldScenePath);
		if (loaded != Error.Ok)
		{
			Print($"could not load {Session.WorldScenePath}: {loaded}");
		}
	}

	private static ConsoleResult StopDemo(IReadOnlyList<string> args)
	{
		if (PlayerManager.Instance is not { } players)
		{
			return ConsoleResult.Failed("no demo is playing");
		}

		return players.StopPlayback(out string message) ? ConsoleResult.Say(message) : ConsoleResult.Failed(message);
	}

	private static ConsoleResult DemoSpeed(IReadOnlyList<string> args)
	{
		if (PlayerManager.Instance?.Playback is not { } playback)
		{
			return ConsoleResult.Failed("no demo is playing");
		}

		if (args.Count == 0)
		{
			return ConsoleResult.Say($"x{playback.Speed}");
		}

		if (!int.TryParse(args[0], out int speed) || speed < 1 || speed > DemoPlayback.MaxSpeed)
		{
			return ConsoleResult.Failed($"usage: demospeed <1-{DemoPlayback.MaxSpeed}>");
		}

		playback.Speed = speed;
		return ConsoleResult.Say($"x{playback.Speed}");
	}

	private static ConsoleResult DemoPause(IReadOnlyList<string> args)
	{
		if (PlayerManager.Instance?.Playback is not { } playback)
		{
			return ConsoleResult.Failed("no demo is playing");
		}

		playback.Paused = !playback.Paused;
		return ConsoleResult.Say(playback.Paused ? $"held at tick {playback.Tick}" : "running");
	}

	private static ConsoleResult DemoFollow(IReadOnlyList<string> args)
	{
		if (PlayerManager.Instance is not { IsPlayingDemo: true } players)
		{
			return ConsoleResult.Failed("no demo is playing");
		}

		int peerId = 0;
		if (args.Count > 0 && (!int.TryParse(args[0], out peerId) || peerId <= 0))
		{
			return ConsoleResult.Failed("usage: demofollow [peer]");
		}

		if (peerId != 0 && !players.HasPlayer(peerId))
		{
			return ConsoleResult.Failed($"peer {peerId} is not on the field right now");
		}

		players.FollowInDemo(peerId);
		return ConsoleResult.Say(peerId == 0 ? "free camera" : $"following peer {peerId}");
	}

	// ---- the widget --------------------------------------------------------

	private void Build()
	{
		// Anchored by hand rather than from a preset: the panel is the top fraction of
		// the window at every size, and a preset would leave offsets behind that the
		// fractional bottom anchor then fights with.
		_root = new Control
		{
			Visible = false,
			MouseFilter = Control.MouseFilterEnum.Stop,
			AnchorLeft = 0f,
			AnchorTop = 0f,
			AnchorRight = 1f,
			AnchorBottom = HeightFraction,
			OffsetLeft = 0f,
			OffsetTop = 0f,
			OffsetRight = 0f,
			OffsetBottom = 0f,
		};
		AddChild(_root);

		var panel = new PanelContainer();
		panel.SetAnchorsPreset(Control.LayoutPreset.FullRect);
		var style = new StyleBoxFlat
		{
			BgColor = new Color(0.04f, 0.05f, 0.07f, 0.92f),
			BorderColor = new Color(0.35f, 0.45f, 0.55f, 0.9f),
			BorderWidthBottom = 2,
		};
		panel.AddThemeStyleboxOverride("panel", style);
		_root.AddChild(panel);

		var margin = new MarginContainer();
		foreach (string side in new[] { "left", "right", "top", "bottom" })
		{
			margin.AddThemeConstantOverride($"margin_{side}", 12);
		}
		panel.AddChild(margin);

		var column = new VBoxContainer();
		margin.AddChild(column);

		_output = new RichTextLabel
		{
			BbcodeEnabled = true,
			ScrollFollowing = true,
			SizeFlagsVertical = Control.SizeFlags.ExpandFill,
			FocusMode = Control.FocusModeEnum.None,
		};
		column.AddChild(_output);

		_entry = new LineEdit
		{
			PlaceholderText = "record game.demo",
			CaretBlink = true,
		};
		_entry.TextSubmitted += Submit;
		column.AddChild(_entry);

		// Anything queued by Print() before the widget existed.
		if (_lines.Count > 0)
		{
			_output.Text = string.Join("\n", _lines);
		}

		BuildReplayBar();
	}

	private void BuildReplayBar()
	{
		_replayBar = new Label
		{
			Visible = false,
			MouseFilter = Control.MouseFilterEnum.Ignore,
			HorizontalAlignment = HorizontalAlignment.Right,
			AnchorLeft = 0f,
			AnchorTop = 0f,
			AnchorRight = 1f,
			AnchorBottom = 0f,
			OffsetLeft = 0f,
			OffsetTop = 8f,
			OffsetRight = -16f,
			OffsetBottom = 32f,
		};

		// Drawn over the map, which is whatever colour the map is: an outline is what
		// keeps one line of white text readable without a panel behind it.
		_replayBar.AddThemeColorOverride("font_outline_color", new Color(0f, 0f, 0f, 0.8f));
		_replayBar.AddThemeConstantOverride("outline_size", 4);
		AddChild(_replayBar);
	}
}
