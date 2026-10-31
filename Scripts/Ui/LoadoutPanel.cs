using Gdpyr.Core;
using Gdpyr.Fps;
using Gdpyr.Match;
using Gdpyr.Sim;
using Godot;

namespace Gdpyr.Ui;

/// <summary>
/// The weapon locker's panel, and the death screen's editor of the next spawn's kit
/// (docs/LOADOUT.md §7.2, §7.3): the primary, the nine pouches, the vest, and what the
/// kit weighs, how fast it moves and what it stands.
///
/// It draws a <see cref="LoadoutDraft"/> and does what the draft says: every rule —
/// what a key does, what is greyed out, what an answer from the locker means, when the
/// panel is no longer wanted — is the draft's, and tested there. Mouse and keys do the
/// same things because both go through the draft's one table.
///
/// While it is up it owns the keyboard and the pointer (L-D12): the sampler sends
/// neutral frames, so the character stands still and its keys mean panel things rather
/// than being recorded as input; the kit it takes travels as a request beside the
/// frames (<see cref="CombatManager.RequestLockerKit"/>), never in them. Built in code,
/// as the rest of the HUD is, on <c>Ui/theme.tres</c>.
/// </summary>
public partial class LoadoutPanel : Control
{
	private const string ThemePath = "res://Ui/theme.tres";
	private const float Width = 780f;
	private const float Height = 330f;

	private static readonly Color Muted = new(0.75f, 0.75f, 0.78f);
	private static readonly Color Refused = new(1f, 0.61f, 0.48f);

	private readonly Button[] _primaries = new Button[4];
	private readonly Button[] _vests = new Button[4];

	private LoadoutDraft _draft;
	private Label _title;
	private Label _cells;
	private Label _magazines;
	private Label _grenades;
	private Button _fewerMagazines;
	private Button _moreMagazines;
	private Button _fewerGrenades;
	private Button _moreGrenades;
	private Button _lighterVest;
	private Button _heavierVest;
	private Label _load;
	private ProgressBar _speed;
	private Label _vest;
	private Label _ammo;
	private Label _status;
	private Button _take;
	private Button _revert;
	private Button _default;
	private Button _close;

	public bool IsOpen => _draft is { IsOpen: true };

	public override void _Ready()
	{
		Visible = false;

		// Centred by its anchors and offsets, not by Position: this runs inside the tree,
		// where moving an anchor keeps the rect where it was and Position is measured from
		// the parent's corner, so the panel would sit off the top left of the screen.
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
		// The flag is process state and this node is what sets it, as the console's is.
		if (IsOpen)
		{
			InputFocus.EndMenu();
		}
	}

	/// <summary>
	/// Opens on the player's kit: at a locker what they carry, from the death screen the
	/// next spawn's. Nothing if it is already up.
	/// </summary>
	public void Open(DraftMode mode, PlayerCombat combat)
	{
		if (IsOpen || combat?.Character == null || Bootstrap.IsDedicatedServer)
		{
			return;
		}

		// The authored speeds are the character's, the rest the catalog's; both are fixed
		// for the process, so the catalog is read once.
		_draft ??= new LoadoutDraft(WeaponCatalog.DraftCatalogFor(
			combat.Character.AuthoredSpeed("PlayerWalkingState"),
			combat.Character.AuthoredSpeed("PlayerSprintingState")));

		_draft.Open(mode, mode == DraftMode.Locker ? combat.Loadout : combat.PendingLoadout);
		Visible = true;
		InputFocus.BeginMenu();
		Input.MouseMode = Input.MouseModeEnum.Visible;
		Redraw();
	}

	/// <summary>The locker's answer to the kit this panel asked for (<c>ServerKitResult</c>).</summary>
	public void Answer(KitRefusal refusal)
	{
		if (IsOpen)
		{
			Act(_draft.Answer(refusal));
		}
	}

	/// <summary>
	/// Called once per simulation tick by the HUD: closes the panel when it is no longer
	/// wanted (<see cref="LoadoutDraft.StillWanted"/>) — a death, a side change, the role
	/// menu, the end of the round, leaving the locker's reach, or the spawn the death
	/// screen was editing for.
	/// </summary>
	public void Refresh(in DraftSituation situation)
	{
		if (!IsOpen)
		{
			return;
		}

		if (!_draft.StillWanted(situation))
		{
			Shut();
			return;
		}

		// The console hands back the pointer it found when it closes, which was this
		// panel's; anything else that captured it while the panel is up is put right.
		if (!InputFocus.TextEntry && Input.MouseMode != Input.MouseModeEnum.Visible)
		{
			Input.MouseMode = Input.MouseModeEnum.Visible;
		}
	}

	/// <summary>
	/// The panel's keys (<see cref="LoadoutDraft.ActionOf"/>), and the use action, which
	/// closes it whatever it is bound to. In <c>_Input</c>, ahead of the pause menu, so
	/// that Escape closes the panel and not the game; only keys the panel has a meaning
	/// for are taken, and none while the console is typing.
	/// </summary>
	public override void _Input(InputEvent @event)
	{
		if (!IsOpen || InputFocus.TextEntry || @event is not InputEventKey { Pressed: true, Echo: false } key)
		{
			return;
		}

		DraftAction action = @event.IsActionPressed("use") ? DraftAction.Close : LoadoutDraft.ActionOf(key.Keycode);
		if (action == DraftAction.None)
		{
			return;
		}

		GetViewport().SetInputAsHandled();
		Act(_draft.Apply(action));
	}

	private void Act(DraftOutcome outcome)
	{
		switch (outcome)
		{
			// Nothing changed, but a radio button clicked a second time has toggled itself
			// off, and the kit says it is still the one chosen.
			case DraftOutcome.None when IsOpen:
			case DraftOutcome.Edited:
				Redraw();
				break;

			case DraftOutcome.AskLocker:
				// Drawn first: on the authority the answer comes back inside the request and
				// may already have closed the panel when it returns.
				Redraw();
				CombatManager.Instance?.RequestLockerKit(_draft.Kit);
				break;

			case DraftOutcome.SetNextSpawn:
				CombatManager.Instance?.RequestLoadout(_draft.Kit);
				Shut();
				break;

			case DraftOutcome.Close:
				Shut();
				break;
		}
	}

	/// <summary>
	/// Takes the panel down and gives the pointer back to the first-person view — unless
	/// the player is no longer on the ground, where the role menu or the strategist's
	/// camera has it, or the console is typing, which hands it back when it closes.
	/// </summary>
	private void Shut()
	{
		_draft?.Close();
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
		LoadoutDraft draft = _draft;
		LoadoutSelection kit = draft.Kit;

		_title.Text = draft.Title;
		for (int i = 0; i < _primaries.Length; i++)
		{
			bool exists = i < draft.Catalog.Primaries.Length;
			_primaries[i].Visible = exists;
			_primaries[i].Text = draft.PrimaryLabel(i);
			_primaries[i].SetPressedNoSignal(exists && draft.Catalog.Primaries[i] == kit.Large);
		}

		for (int i = 0; i < _vests.Length; i++)
		{
			_vests[i].SetPressedNoSignal((int)kit.Armour == i);
		}

		_cells.Text = draft.PouchCells;
		_magazines.Text = kit.Magazines.ToString();
		_grenades.Text = kit.Grenades.ToString();
		Enable(_fewerMagazines, DraftAction.FewerMagazines);
		Enable(_moreMagazines, DraftAction.MoreMagazines);
		Enable(_fewerGrenades, DraftAction.FewerGrenades);
		Enable(_moreGrenades, DraftAction.MoreGrenades);
		Enable(_lighterVest, DraftAction.LighterVest);
		Enable(_heavierVest, DraftAction.HeavierVest);

		_load.Text = draft.LoadLine;
		_speed.Value = draft.SpeedScale;
		_vest.Text = draft.VestLine;
		_ammo.Text = draft.AmmoLine;
		_status.Text = draft.StatusLine;
		_status.AddThemeColorOverride("font_color", draft.LastRefusal != KitRefusal.None ? Refused : Muted);

		_take.Text = draft.KeyLabel(DraftAction.Take);
		_revert.Text = draft.KeyLabel(DraftAction.Revert);
		_default.Text = draft.KeyLabel(DraftAction.Default);
		_close.Text = draft.KeyLabel(DraftAction.Close);
		Enable(_take, DraftAction.Take);
		Enable(_revert, DraftAction.Revert);
		Enable(_default, DraftAction.Default);
	}

	private void Enable(Button button, DraftAction action) => button.Disabled = !_draft.IsEnabled(action);

	// ---- layout -------------------------------------------------------------

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

		_title = NewLabel(column, "WEAPON LOCKER");
		_title.AddThemeFontSizeOverride("font_size", 22);

		HBoxContainer primary = NewRow(column, "PRIMARY");
		for (int i = 0; i < _primaries.Length; i++)
		{
			int index = i;
			_primaries[i] = NewButton(primary, string.Empty, () => Act(_draft.ChoosePrimary(index)), toggle: true);
		}

		HBoxContainer pouches = NewRow(column, "POUCHES");
		_cells = NewLabel(pouches, string.Empty);
		_cells.CustomMinimumSize = new Vector2(150f, 0f);
		NewLabel(pouches, "magazines").SelfModulate = Muted;
		_fewerMagazines = NewButton(pouches, "5 −", () => Act(_draft.Apply(DraftAction.FewerMagazines)));
		_magazines = NewCount(pouches);
		_moreMagazines = NewButton(pouches, "6 +", () => Act(_draft.Apply(DraftAction.MoreMagazines)));
		NewLabel(pouches, "   grenades").SelfModulate = Muted;
		_fewerGrenades = NewButton(pouches, "7 −", () => Act(_draft.Apply(DraftAction.FewerGrenades)));
		_grenades = NewCount(pouches);
		_moreGrenades = NewButton(pouches, "8 +", () => Act(_draft.Apply(DraftAction.MoreGrenades)));

		HBoxContainer armour = NewRow(column, "ARMOUR");
		for (int i = 0; i < _vests.Length; i++)
		{
			var tier = (BodyArmourTier)i;
			_vests[i] = NewButton(armour, LoadoutDraft.VestLabel(tier), () => Act(_draft.ChooseVest(tier)), toggle: true);
		}

		_lighterVest = NewButton(armour, "9 −", () => Act(_draft.Apply(DraftAction.LighterVest)));
		_heavierVest = NewButton(armour, "0 +", () => Act(_draft.Apply(DraftAction.HeavierVest)));

		var load = new HBoxContainer();
		load.AddThemeConstantOverride("separation", 12);
		column.AddChild(load);
		_load = NewLabel(load, string.Empty);

		// The share of the movement speeds the kit leaves, from the floor to the cap.
		_speed = new ProgressBar
		{
			MinValue = Loadout.MinSpeedScale,
			MaxValue = Loadout.MaxSpeedScale,
			Step = 0.001,
			ShowPercentage = false,
			CustomMinimumSize = new Vector2(140f, 14f),
			SizeFlagsVertical = SizeFlags.ShrinkCenter,
			MouseFilter = MouseFilterEnum.Ignore,
		};
		load.AddChild(_speed);

		_vest = NewLabel(column, string.Empty);
		_ammo = NewLabel(column, string.Empty);
		_status = NewLabel(column, string.Empty);

		var keys = new HBoxContainer();
		keys.AddThemeConstantOverride("separation", 10);
		column.AddChild(keys);
		_take = NewButton(keys, string.Empty, () => Act(_draft.Apply(DraftAction.Take)));
		_revert = NewButton(keys, string.Empty, () => Act(_draft.Apply(DraftAction.Revert)));
		_default = NewButton(keys, string.Empty, () => Act(_draft.Apply(DraftAction.Default)));
		_close = NewButton(keys, string.Empty, () => Act(_draft.Apply(DraftAction.Close)));
	}

	private static HBoxContainer NewRow(Control parent, string heading)
	{
		var row = new HBoxContainer();
		row.AddThemeConstantOverride("separation", 6);
		parent.AddChild(row);

		Label label = NewLabel(row, heading);
		label.CustomMinimumSize = new Vector2(88f, 0f);
		label.SelfModulate = Muted;
		return row;
	}

	private static Label NewCount(Control parent)
	{
		Label count = NewLabel(parent, "0");
		count.CustomMinimumSize = new Vector2(18f, 0f);
		count.HorizontalAlignment = HorizontalAlignment.Center;
		return count;
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

	/// <summary>
	/// A button the mouse presses and the keyboard never focuses: the panel's keys are
	/// its own table, and a focused button would also take Space and the arrows.
	/// </summary>
	private static Button NewButton(Control parent, string text, System.Action pressed, bool toggle = false)
	{
		var button = new Button
		{
			Text = text,
			ToggleMode = toggle,
			FocusMode = FocusModeEnum.None,
		};
		button.Pressed += pressed;
		parent.AddChild(button);
		return button;
	}
}
