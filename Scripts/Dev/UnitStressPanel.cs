using System.Text;
using Gdpyr.Core;
using Gdpyr.Match;
using Gdpyr.Rts;
using Gdpyr.Sim;
using Godot;

namespace Gdpyr.Dev;

/// <summary>
/// The unit stress test's panel (docs/UNIT_STRESS_TEST.md §3): what to put round the
/// barracks and how many, what they are told, the modes that keep putting them there, and what
/// a frame costs while they fight. <c>F10</c> opens and closes it; a line in the top-right
/// corner says it is there and what the field and the frame rate are.
///
/// While it is open it holds <see cref="InputFocus.Menu"/>, as the weapon locker's panel does:
/// the player stands still, the pointer is free, and Escape closes it rather than pausing.
/// The round goes on underneath.
/// </summary>
public partial class UnitStressPanel : CanvasLayer
{
	private const string ThemePath = "res://Ui/theme.tres";
	private const float Width = 440f;
	private const Key ToggleKey = Key.F10;

	/// <summary>How often the numbers are redrawn [s].</summary>
	private const float RedrawSeconds = 0.25f;

	private static readonly Color Muted = new(0.75f, 0.75f, 0.78f);
	private static readonly Color Accent = new(1f, 0.83f, 0.45f);

	private static readonly string[] OrderLabels = { "Hunt the player", "Attack-move on the spawn", "Hold round the barracks" };

	private static readonly string[] ModeLabels =
	{
		"Manual", "A wave every interval", "Keep the count alive", "Ramp: + count every interval",
	};

	public UnitStressTest Stress { get; set; }

	public bool IsOpen => _panel?.Visible ?? false;

	private Label _badge;
	private PanelContainer _panel;
	private OptionButton _unit;
	private SpinBox _count;
	private OptionButton _order;
	private CheckBox _tactics;
	private OptionButton _mode;
	private SpinBox _interval;
	private SpinBox _floor;
	private CheckBox _vsync;
	private Label _stats;
	private Label _ramp;
	private Label _status;
	private float _redraw;

	public override void _Ready()
	{
		Layer = 5;
		Build();
	}

	public override void _ExitTree()
	{
		if (IsOpen)
		{
			InputFocus.EndMenu();
		}
	}

	public override void _Input(InputEvent @event)
	{
		if (InputFocus.TextEntry || @event is not InputEventKey { Pressed: true, Echo: false } key)
		{
			return;
		}

		if (key.Keycode == ToggleKey || (IsOpen && key.Keycode == Key.Escape))
		{
			GetViewport().SetInputAsHandled();
			SetOpen(!IsOpen);
		}
	}

	public override void _Process(double delta)
	{
		if (IsOpen && !InputFocus.TextEntry && Input.MouseMode != Input.MouseModeEnum.Visible)
		{
			// A respawn takes the pointer back for the first-person view; the panel wants it.
			Input.MouseMode = Input.MouseModeEnum.Visible;
		}

		_redraw += (float)delta;
		if (_redraw < RedrawSeconds || Stress == null)
		{
			return;
		}

		_redraw = 0f;
		UnitManager units = UnitManager.Instance;
		_badge.Text = $"F10 unit stress · {units?.LiveUnitCount ?? 0} units · {Stress.Frames.Fps:0} fps";
		if (IsOpen)
		{
			Redraw(units);
		}
	}

	/// <summary>Opens or closes the panel, and gives the pointer to it or back to the first-person view.</summary>
	public void SetOpen(bool open)
	{
		if (open == IsOpen)
		{
			return;
		}

		_panel.Visible = open;
		if (open)
		{
			InputFocus.BeginMenu();
			Input.MouseMode = Input.MouseModeEnum.Visible;
			Redraw(UnitManager.Instance);
			return;
		}

		GetViewport().GuiReleaseFocus();
		InputFocus.EndMenu();
		if (CombatManager.Instance is { LocalAwaitingRole: false, Local: { Team: Team.GroundForce } local }
			&& !InputFocus.TextEntry)
		{
			local.Character?.EnterFirstPerson();
		}
	}

	private void Redraw(UnitManager units)
	{
		var stats = new StringBuilder();
		stats.AppendLine($"alive {units?.LiveUnitCount ?? 0}   slots {units?.SlotCount ?? 0}/{SimConfig.MaxUnits}"
			+ $"   spawned {Stress.Spawned}   lost {units?.UnitsLost ?? 0}");
		stats.AppendLine($"{Stress.Frames.Fps:0.0} fps   frame {Stress.Frames.MeanMs:0.00} ms"
			+ $"   p95 {Stress.Frames.P95Ms:0.00}   worst {Stress.Frames.WorstMs:0.00}");
		FrameCosts costs = UnitStressTest.Costs();
		stats.AppendLine($"unit pass {costs.UnitPassMs:0.000} ms a tick (mean, last second)");
		stats.AppendLine($"slowest in 1 s: physics tick {costs.PhysicsWorstMs:0.0} ms   idle frame {costs.IdleWorstMs:0.0} ms");
		stats.Append($"draws {UnitStressTest.Monitor(Performance.Monitor.RenderTotalDrawCallsInFrame):0}"
			+ $"   objects {UnitStressTest.Monitor(Performance.Monitor.RenderTotalObjectsInFrame):0}"
			+ $"   primitives {UnitStressTest.Monitor(Performance.Monitor.RenderTotalPrimitivesInFrame) / 1000.0:0.0} k");
		_stats.Text = stats.ToString();

		_ramp.Text = RampText(Stress.Ramp);
		_ramp.Visible = _ramp.Text.Length > 0;
		_status.Text = Stress.DisabledReason ?? $"{ModeLabels[(int)Stress.Mode].ToLowerInvariant()} · {Stress.LastAction}";

		if (_mode.Selected != (int)Stress.Mode && Stress.Mode == StressMode.Manual)
		{
			// A ramp that finished on its own is back to manual.
			_mode.Selected = (int)StressMode.Manual;
		}
	}

	private static string RampText(StressRamp ramp)
	{
		if (ramp == null)
		{
			return string.Empty;
		}

		var text = new StringBuilder();
		text.AppendLine(ramp.Running
			? $"ramp: target {ramp.Target}, {(ramp.Measuring ? "measuring" : "settling")} {ramp.StepElapsed:0}/{ramp.StepSeconds:0} s"
			: $"ramp done: {ramp.StopReason}. Held {ramp.FpsFloor:0} fps up to {ramp.HeldTarget} units.");

		// The log has every step in full (UnitStressTest.LogRamp); the panel the last eight, short.
		int first = System.Math.Max(0, ramp.Steps.Count - 8);
		for (int i = first; i < ramp.Steps.Count; i++)
		{
			RampStep step = ramp.Steps[i];
			string alive = step.MeanAlive < step.Target - 0.5f ? $" ({step.MeanAlive:0})" : string.Empty;
			text.AppendLine($"{step.Target,2}{alive} units  {step.Fps:0.0} fps · p95 {step.P95Ms:0.0}"
				+ $" · pass {step.MeanUnitPassMs:0.00} · tick {step.WorstPhysicsMs:0.0} ms");
		}

		return text.ToString().TrimEnd();
	}

	// ---- layout -----------------------------------------------------------------------------

	private void Build()
	{
		var theme = GD.Load<Theme>(ThemePath);

		_badge = new Label
		{
			Text = "F10 unit stress",
			HorizontalAlignment = HorizontalAlignment.Right,
			MouseFilter = Control.MouseFilterEnum.Ignore,
			Theme = theme,
		};
		_badge.SetAnchorsPreset(Control.LayoutPreset.TopRight);
		_badge.OffsetLeft = -420f;
		_badge.OffsetRight = -12f;
		_badge.OffsetTop = 8f;
		_badge.AddThemeColorOverride("font_color", Accent);
		_badge.AddThemeColorOverride("font_shadow_color", new Color(0f, 0f, 0f, 0.8f));
		_badge.AddThemeFontSizeOverride("font_size", 14);
		AddChild(_badge);

		// The right-hand side of the screen, top to bottom, and its contents scroll when the
		// window is too short for them: the ramp's table grows.
		_panel = new PanelContainer { Visible = false, Theme = theme };
		_panel.SetAnchorsPreset(Control.LayoutPreset.RightWide);
		_panel.OffsetLeft = -Width - 12f;
		_panel.OffsetRight = -12f;
		_panel.OffsetTop = 34f;
		_panel.OffsetBottom = -12f;
		_panel.AddThemeStyleboxOverride("panel", new StyleBoxFlat
		{
			BgColor = new Color(0.08f, 0.09f, 0.1f, 0.9f),
			BorderColor = new Color(0.45f, 0.47f, 0.5f),
			BorderWidthLeft = 2,
			BorderWidthTop = 2,
			BorderWidthRight = 2,
			BorderWidthBottom = 2,
			ContentMarginLeft = 14,
			ContentMarginTop = 10,
			ContentMarginRight = 14,
			ContentMarginBottom = 12,
		});
		AddChild(_panel);

		var scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
		_panel.AddChild(scroll);

		var column = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
		column.AddThemeConstantOverride("separation", 8);
		scroll.AddChild(column);

		var title = new Label { Text = "Unit stress test" };
		title.AddThemeFontSizeOverride("font_size", 20);
		column.AddChild(title);
		column.AddChild(Note("F10 or Esc closes it; the round goes on underneath."));

		// What to put there.
		var spawn = Grid(column);
		_unit = new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
		foreach ((string label, byte[] _) in UnitStressTest.UnitChoices)
		{
			_unit.AddItem(label);
		}
		_unit.Selected = Stress?.Kind ?? 0;
		_unit.ItemSelected += index => Stress.Kind = (int)index;
		Row(spawn, "Unit", _unit);

		_count = Spin(1, SimConfig.MaxUnits, Stress?.Count ?? 16, string.Empty);
		_count.ValueChanged += value => Stress.Count = (int)value;
		Row(spawn, "Count", _count);

		_order = new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
		foreach (string label in OrderLabels)
		{
			_order.AddItem(label);
		}
		_order.Selected = (int)(Stress?.Order ?? StressOrder.HuntPlayer);
		_order.ItemSelected += index => Stress.Order = (StressOrder)index;
		Row(spawn, "Order", _order);

		_tactics = new CheckBox { Text = "micro-autonomy on", ButtonPressed = Stress?.Tactics ?? true };
		_tactics.Toggled += on => Stress.Tactics = on;
		Row(spawn, "Tactics", _tactics);

		var actions = new HBoxContainer();
		actions.AddThemeConstantOverride("separation", 8);
		column.AddChild(actions);
		actions.AddChild(ActionButton("Spawn wave", () => Stress.SpawnWave(Stress.Count)));
		actions.AddChild(ActionButton("Clear field", () =>
		{
			Stress.ClearField();
			_mode.Selected = (int)StressMode.Manual;
		}));

		column.AddChild(new HSeparator());

		// What keeps putting them there.
		var modes = Grid(column);
		_mode = new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
		foreach (string label in ModeLabels)
		{
			_mode.AddItem(label);
		}
		_mode.Selected = (int)(Stress?.Mode ?? StressMode.Manual);
		Row(modes, "Mode", _mode);

		_interval = Spin(5, 300, Stress?.IntervalSeconds ?? 20f, " s");
		_interval.ValueChanged += value => Stress.IntervalSeconds = (float)value;
		Row(modes, "Interval", _interval);

		_floor = Spin(10, 240, Stress?.FpsFloor ?? 45f, " fps");
		_floor.ValueChanged += value => Stress.FpsFloor = (float)value;
		Row(modes, "Ramp floor", _floor);

		var run = new HBoxContainer();
		run.AddThemeConstantOverride("separation", 8);
		column.AddChild(run);
		run.AddChild(ActionButton("Start mode", () => Stress.SetMode((StressMode)_mode.Selected)));
		run.AddChild(ActionButton("Stop", () =>
		{
			Stress.SetMode(StressMode.Manual);
			_mode.Selected = (int)StressMode.Manual;
		}));

		bool vsync = DisplayServer.WindowGetVsyncMode() != DisplayServer.VSyncMode.Disabled;
		_vsync = new CheckBox { Text = VsyncText(vsync), ButtonPressed = vsync };
		_vsync.Toggled += on =>
		{
			DisplayServer.WindowSetVsyncMode(on ? DisplayServer.VSyncMode.Enabled : DisplayServer.VSyncMode.Disabled);
			_vsync.Text = VsyncText(on);
		};
		Row(Grid(column), "V-sync", _vsync);

		column.AddChild(new HSeparator());

		// Wrapped rather than widened: a long line pushes a label's container, and this one is
		// anchored to the screen's right edge.
		_stats = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
		_stats.AddThemeFontSizeOverride("font_size", 13);
		column.AddChild(_stats);

		_ramp = new Label { Visible = false, AutowrapMode = TextServer.AutowrapMode.WordSmart };
		_ramp.AddThemeFontSizeOverride("font_size", 13);
		_ramp.AddThemeColorOverride("font_color", Accent);
		column.AddChild(_ramp);

		_status = Note(string.Empty);
		_status.AutowrapMode = TextServer.AutowrapMode.WordSmart;
		column.AddChild(_status);
	}

	private static string VsyncText(bool on) =>
		on ? "on: frames wait for the display" : "off: a frame's own cost is measured";

	private static GridContainer Grid(Container parent)
	{
		var grid = new GridContainer { Columns = 2 };
		grid.AddThemeConstantOverride("h_separation", 10);
		grid.AddThemeConstantOverride("v_separation", 6);
		parent.AddChild(grid);
		return grid;
	}

	private static void Row(GridContainer grid, string label, Control control)
	{
		var name = new Label { Text = label, CustomMinimumSize = new Vector2(80f, 0f) };
		name.AddThemeColorOverride("font_color", Muted);
		grid.AddChild(name);
		grid.AddChild(control);
	}

	private static SpinBox Spin(double min, double max, double value, string suffix) => new()
	{
		MinValue = min,
		MaxValue = max,
		Step = 1,
		Value = value,
		Suffix = suffix,
		SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
	};

	private static Button ActionButton(string text, System.Action pressed)
	{
		var button = new Button { Text = text, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
		button.Pressed += pressed;
		return button;
	}

	private static Label Note(string text)
	{
		var label = new Label { Text = text };
		label.AddThemeColorOverride("font_color", Muted);
		label.AddThemeFontSizeOverride("font_size", 13);
		return label;
	}
}
