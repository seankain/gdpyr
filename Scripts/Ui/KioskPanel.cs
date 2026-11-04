using Gdpyr.Core;
using Gdpyr.Fps;
using Gdpyr.Match;
using Gdpyr.Sim;
using Godot;

namespace Gdpyr.Ui;

// TODO(GV6, docs/GROUND_VEHICLES.md §12): check on a display: the panel's two rows, the refusal
// at its foot, the HUD's kiosk line under the round's. Driven headless, never looked at.
/// <summary>
/// The vehicle kiosk's panel (docs/GROUND_VEHICLES.md §8.5): a row per kind with its
/// status, <c>1</c> and <c>2</c> to ask, the refusal at its foot. It draws a
/// <see cref="KioskMenu"/> and does what the menu says, as <see cref="LoadoutPanel"/> draws
/// its draft: every key, line and closing rule is the menu's, and tested there.
///
/// While it is up it holds <see cref="InputFocus.Menu"/>, so the sampler sends neutral
/// frames, as for the locker; the vehicle it asks for travels as a request beside the
/// frames (<see cref="CrewVehicleManager.RequestVehicle"/>), never in them.
/// </summary>
public partial class KioskPanel : Control
{
	private const string ThemePath = "res://Ui/theme.tres";
	private const float Width = 460f;
	private const float Height = 260f;

	private static readonly Color Muted = new(0.75f, 0.75f, 0.78f);
	private static readonly Color Refused = new(1f, 0.61f, 0.48f);

	private readonly KioskMenu _menu = new();
	private readonly Button[] _rows = new Button[KioskMenu.Kinds];
	private Label _status;
	private Button _close;

	public bool IsOpen => _menu.IsOpen;

	/// <summary>The menu this panel draws. For the checks.</summary>
	public KioskMenu Menu => _menu;

	public override void _Ready()
	{
		Visible = false;
		AnchorLeft = 0.5f;
		AnchorTop = 0.5f;
		AnchorRight = 0.5f;
		AnchorBottom = 0.5f;
		OffsetLeft = -Width * 0.5f;
		OffsetTop = -Height * 0.5f;
		OffsetRight = Width * 0.5f;
		OffsetBottom = Height * 0.5f;
		Theme = GD.Load<Theme>(ThemePath);
		Build();
	}

	public override void _ExitTree()
	{
		if (IsOpen)
		{
			InputFocus.EndMenu();
		}
	}

	/// <summary>Opens, at the kiosk. Nothing if it is already up.</summary>
	public void Open(PlayerCombat combat)
	{
		if (IsOpen || combat?.Character == null || Bootstrap.IsDedicatedServer)
		{
			return;
		}

		_menu.Open();
		Visible = true;
		InputFocus.BeginMenu();
		Input.MouseMode = Input.MouseModeEnum.Visible;
		Redraw();
	}

	/// <summary>The kiosk's answer (<c>ServerKioskResult</c>); false when this panel was not waiting for one.</summary>
	public bool Answer(KioskRefusal refusal)
	{
		if (!IsOpen || !_menu.Awaiting)
		{
			return false;
		}

		Act(_menu.Answer(refusal));
		return true;
	}

	/// <summary>
	/// Once per simulation tick, from the HUD: closes the panel when it is no longer wanted
	/// (<see cref="KioskMenu.StillWanted"/>), and otherwise redraws its rows, whose statuses
	/// count down.
	/// </summary>
	public void Refresh(in KioskMenuSituation situation)
	{
		if (!IsOpen)
		{
			return;
		}

		if (!_menu.StillWanted(situation))
		{
			Shut();
			return;
		}

		if (!InputFocus.TextEntry && Input.MouseMode != Input.MouseModeEnum.Visible)
		{
			Input.MouseMode = Input.MouseModeEnum.Visible;
		}

		Redraw();
	}

	/// <summary>
	/// The panel's keys (<see cref="KioskMenu.ActionOf"/>), and the use action, which closes
	/// it whatever it is bound to. In <c>_Input</c>, ahead of the pause menu, so that Escape
	/// closes the panel and not the game.
	/// </summary>
	public override void _Input(InputEvent @event)
	{
		if (!IsOpen || InputFocus.TextEntry || @event is not InputEventKey { Pressed: true, Echo: false } key)
		{
			return;
		}

		KioskAction action = @event.IsActionPressed("use") ? KioskAction.Close : KioskMenu.ActionOf(key.Keycode);
		if (action == KioskAction.None)
		{
			return;
		}

		GetViewport().SetInputAsHandled();
		Act(_menu.Apply(action));
	}

	private void Act(KioskOutcome outcome)
	{
		switch (outcome)
		{
			case KioskOutcome.Edited:
				Redraw();
				break;

			case KioskOutcome.Ask:
				// Drawn first: on the authority the answer comes back inside the request and
				// may already have closed the panel when it returns.
				Redraw();
				CrewVehicleManager.Instance?.RequestVehicle(_menu.AskedKind, out _);
				break;

			case KioskOutcome.Close:
				Shut();
				break;
		}
	}

	/// <summary>Takes the panel down and gives the pointer back to the first-person view, as the locker's does.</summary>
	private void Shut()
	{
		_menu.Close();
		Visible = false;
		InputFocus.EndMenu();

		CombatManager combat = CombatManager.Instance;
		if (combat is { LocalAwaitingRole: false, Local: { Team: Team.GroundForce } local } && !InputFocus.TextEntry)
		{
			local.Character?.EnterFirstPerson();
		}
	}

	private void Redraw()
	{
		CrewVehicleManager crew = CrewVehicleManager.Instance;
		for (byte kind = 0; kind < _rows.Length; kind++)
		{
			KioskLine line = crew?.KioskLineOf(kind) ?? KioskLine.None;
			_rows[kind].Text = KioskMenu.RowLabel(kind, line);
			_rows[kind].Disabled = !_menu.IsEnabled(kind == 0 ? KioskAction.Ask4x4 : KioskAction.AskHelicopter);
		}

		_status.Text = crew != null ? _menu.StatusLine(crew.KioskLines) : string.Empty;
		_status.AddThemeColorOverride("font_color", _menu.LastRefusal != KioskRefusal.None ? Refused : Muted);
	}

	// ---- layout -------------------------------------------------------------------------

	private void Build()
	{
		var frame = new PanelContainer();
		frame.SetAnchorsPreset(LayoutPreset.FullRect);
		frame.AddThemeStyleboxOverride("panel", new StyleBoxFlat
		{
			BgColor = new Color(0.08f, 0.09f, 0.1f, 0.88f),
			BorderColor = new Color(0.45f, 0.47f, 0.5f),
			BorderWidthLeft = 2,
			BorderWidthTop = 2,
			BorderWidthRight = 2,
			BorderWidthBottom = 2,
			CornerRadiusTopLeft = 5,
			CornerRadiusTopRight = 5,
			CornerRadiusBottomLeft = 5,
			CornerRadiusBottomRight = 5,
		});
		AddChild(frame);

		var margin = new MarginContainer();
		frame.AddChild(margin);

		var column = new VBoxContainer();
		column.AddThemeConstantOverride("separation", 8);
		margin.AddChild(column);

		Label title = NewLabel(column, KioskMenu.Title);
		title.AddThemeFontSizeOverride("font_size", 22);

		for (int i = 0; i < _rows.Length; i++)
		{
			KioskAction action = i == 0 ? KioskAction.Ask4x4 : KioskAction.AskHelicopter;
			_rows[i] = new Button
			{
				Text = string.Empty,
				FocusMode = FocusModeEnum.None,
				Alignment = HorizontalAlignment.Left,
			};
			_rows[i].Pressed += () => Act(_menu.Apply(action));
			column.AddChild(_rows[i]);
		}

		_status = NewLabel(column, string.Empty);
		NewLabel(column, KioskMenu.KeysLine).SelfModulate = Muted;

		_close = new Button { Text = "[E] close", FocusMode = FocusModeEnum.None };
		_close.Pressed += () => Act(_menu.Apply(KioskAction.Close));
		column.AddChild(_close);
	}

	private static Label NewLabel(Control parent, string text)
	{
		var label = new Label
		{
			Text = text,
			MouseFilter = MouseFilterEnum.Ignore,
			SizeFlagsVertical = SizeFlags.ShrinkCenter,
		};
		parent.AddChild(label);
		return label;
	}
}
