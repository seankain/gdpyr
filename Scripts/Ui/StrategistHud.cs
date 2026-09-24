using System;
using Gdpyr.Core;
using Gdpyr.Match;
using Gdpyr.Rts;
using Gdpyr.Sim;
using Godot;

namespace Gdpyr.Ui;

/// <summary>
/// What the strategist needs on screen: points, what the barracks is building,
/// what is selected, what the next right-click will mean, and the keys
/// (docs/IMPLEMENTATION_PLAN.md §3, "Ui/ RTS HUD").
///
/// Built in code and hosting <see cref="SelectionOverlay"/>, for the same reason
/// <see cref="CombatHud"/> is: a greybox HUD that costs a scene file costs a merge
/// conflict every time a milestone touches it.
///
/// The key list is on screen permanently on purpose. A playtester who has to be
/// told the controls is a playtester whose first round measures how well they were
/// told them. The barracks card is the same argument for the build keys: a new
/// player clicks the building, and the buttons say what the keys are.
/// </summary>
public partial class StrategistHud : CanvasLayer
{
	private const string Keys =
		"WASD/edge pan · Space/Ctrl raise/lower · hold RMB and drag to look (Q/E also turn) · wheel zoom\n"
		+ "LMB select · click the barracks to train units · RMB order · X attack-move · C patrol · B defend · Z stop · F select all\n"
		+ "1/2/3 queue infantry/technical/tank · 4 queue builder · Backspace cancel · RMB with no units selected sets the rally point\n"
		+ "with builders selected: 5/6/7 then RMB places pillbox/sandbags/tower (turn the view to turn it) · RMB on a site finishes or mends it\n"
		+ "F1 ground force · F2 strategist";

	/// <summary>What the barracks card offers, in the order the number keys are: the three tiers, then the builder.</summary>
	private static readonly byte[] CardUnits =
		{ UnitCatalog.Infantry, UnitCatalog.Technical, UnitCatalog.Tank, UnitCatalog.Builder };

	/// <summary>The key that queues each of <see cref="CardUnits"/>, printed on its button.</summary>
	private static readonly string[] CardKeys = { "1", "2", "3", "4" };

	private static readonly Color CardBorderColor = new(0.35f, 0.45f, 0.55f, 0.9f);

	private Label _points;
	private Label _production;
	private Label _selection;
	private Label _economy;
	private Label _builders;
	private Label _keys;

	private Control _card;
	private Label _cardStatus;
	private ProgressBar _cardProgress;
	private readonly Button[] _cardButtons = new Button[CardUnits.Length];
	private Button _cardCancel;

	public SelectionOverlay Overlay { get; private set; }

	/// <summary>A unit button on the barracks card was pressed. Carries the unit's catalog id.</summary>
	public event Action<byte> BuildPressed;

	/// <summary>The barracks card's cancel button was pressed.</summary>
	public event Action CancelPressed;

	public override void _Ready()
	{
		ProcessMode = ProcessModeEnum.Always;
		Layer = 2;

		var root = new Control
		{
			MouseFilter = Control.MouseFilterEnum.Ignore,
			AnchorRight = 1f,
			AnchorBottom = 1f,
		};
		AddChild(root);

		Overlay = new SelectionOverlay { Name = "SelectionOverlay" };
		root.AddChild(Overlay);

		_points = NewLabel(root, new Vector2(24f, 16f), new Vector2(420f, 32f));
		_production = NewLabel(root, new Vector2(24f, 48f), new Vector2(520f, 32f));
		_selection = NewLabel(root, new Vector2(24f, 80f), new Vector2(520f, 32f));
		_economy = NewLabel(root, new Vector2(24f, 112f), new Vector2(640f, 32f));
		_builders = NewLabel(root, new Vector2(24f, 144f), new Vector2(760f, 32f));
		_keys = NewLabel(root, new Vector2(24f, -132f), new Vector2(1100f, 120f), anchorTop: 1f);
		_keys.Text = Keys;

		BuildCard(root);
	}

	/// <summary>
	/// Called once per simulation tick by <see cref="StrategistController"/>.
	/// <paramref name="barracks"/> is the one the build keys mean, and
	/// <paramref name="barracksSelected"/> whether the strategist clicked on it,
	/// which is what puts its card up.
	/// </summary>
	public void Refresh(MatchState match, UnitManager units, uint tick, int selectedCount, OrderKind pendingOrder,
		EconomyService economy, int barracks, bool barracksSelected)
	{
		int live = units?.LiveUnitCount ?? 0;

		_points.Text = match.Phase == RoundPhase.Live
			? $"points {match.StrategistPoints}   units {live}/{SimConfig.MaxUnits}   {Clock(match.SecondsRemaining(tick))}"
			: $"points {match.StrategistPoints}   units {live}/{SimConfig.MaxUnits}   {match.Phase}";

		UnitManager.BarracksStatus status = units?.StatusOf(barracks, tick) ?? default;
		_production.Text = status.Queued > 0
			? $"barracks: {UnitCatalog.NameOf(status.Head)} {Mathf.RoundToInt(status.Progress * 100f)}%"
				+ $"   queued {status.Queued}"
			: "barracks: idle";

		_selection.Text = barracksSelected
			? "selected barracks   right-click the ground to set its rally point"
			: $"selected {selectedCount}   next order: {Describe(pendingOrder)}";
		_economy.Text = Economy(economy);

		RefreshCard(match, status, barracksSelected);
	}

	/// <summary>
	/// The card's live half: what is training, and which buttons can be pressed.
	/// A button the strategist cannot afford is greyed out rather than hidden, so
	/// the card always shows the whole menu and the price of each thing on it.
	/// </summary>
	private void RefreshCard(MatchState match, UnitManager.BarracksStatus status, bool shown)
	{
		_card.Visible = shown;
		if (!shown)
		{
			return;
		}

		_cardStatus.Text = status.Queued > 0
			? $"barracks: training {UnitCatalog.NameOf(status.Head)} {Mathf.RoundToInt(status.Progress * 100f)}%"
				+ $"   queued {status.Queued}"
			: "barracks: idle, pick a unit to train";
		_cardProgress.Value = status.Queued > 0 ? status.Progress : 0f;

		bool full = status.Queued >= SimConfig.MaxBuildQueue;
		for (int i = 0; i < CardUnits.Length; i++)
		{
			byte id = CardUnits[i];
			int cost = UnitCatalog.CostOf(id);
			Button button = _cardButtons[i];

			// Set every refresh rather than once, because the catalog is loaded by the
			// unit manager and this HUD does not know which of them was ready first.
			// The setters do nothing when the text is unchanged.
			button.Text = $"{UnitCatalog.NameOf(id).Capitalize()} ({CardKeys[i]})\n{cost} pts";
			button.TooltipText = UnitCatalog.Definition(id) is { } definition
				? $"{cost} points, {definition.BuildSeconds:0.#} s to train"
				: string.Empty;
			button.Disabled = full || !UnitCatalog.IsBuildable(id) || match.StrategistPoints < cost;
		}

		_cardCancel.Disabled = status.Queued == 0;
	}

	/// <summary>
	/// The builders' line (docs/NETCODE.md §10.5): how many there are, what is
	/// standing and what is going up, and — while a structure is armed — what the
	/// next right-click will put down and what it costs.
	/// </summary>
	public void RefreshBuilders(UnitManager units, Team team, byte placing, int buildersSelected)
	{
		int builders = units?.BuilderCount(team) ?? 0;
		int standing = units?.StructureCount ?? 0;
		int sites = units?.SitesUnderConstruction ?? 0;

		string line = $"builders {builders}   structures {standing}/{SimConfig.MaxStructures}"
			+ (sites > 0 ? $" ({sites} going up)" : string.Empty);

		if (StructureKinds.IsValid(placing))
		{
			line += buildersSelected > 0
				? $"   placing {StructureCatalog.NameOf(placing).Replace('_', ' ')} ({StructureCatalog.CostOf(placing)})"
				: "   select a builder to place with";
		}

		_builders.Text = line;
	}

	/// <summary>
	/// What the nodes are doing, in one line: what is being held, what is being
	/// fought over, and what has been earned (docs/IMPLEMENTATION_PLAN.md §M5).
	///
	/// A strategist with no nodes and no units is one losing condition away from
	/// the round being over, so this is the line that says how close that is.
	/// </summary>
	private static string Economy(EconomyService economy)
	{
		if (economy == null || economy.NodeCount == 0)
		{
			return "nodes: none on this map";
		}

		string contested = economy.ContestedNodes > 0 ? $"   {economy.ContestedNodes} contested" : string.Empty;
		return $"nodes {economy.StrategistNodes}/{economy.NodeCount} held"
			+ $"   {economy.GroundNodes} lost{contested}   earned {economy.IncomePaid}";
	}

	private static string Describe(OrderKind kind) => kind switch
	{
		OrderKind.Attack => "attack-move",
		OrderKind.Patrol => "patrol",
		OrderKind.Defend => "defend",
		_ => "move",
	};

	private static string Clock(float seconds)
	{
		int whole = Mathf.Max(0, Mathf.RoundToInt(seconds));
		return $"{whole / 60:00}:{whole % 60:00}";
	}

	/// <summary>
	/// The barracks' command card: what it is training, a button per unit it can
	/// train, and a way to take the last one back. Up only while a barracks is
	/// selected, in the bottom-right corner above the key list.
	/// </summary>
	private void BuildCard(Control root)
	{
		var panel = new PanelContainer
		{
			Visible = false,

			// Stop, so a click on the card is not also a box select of the map behind it.
			MouseFilter = Control.MouseFilterEnum.Stop,
		};
		panel.AddThemeStyleboxOverride("panel", new StyleBoxFlat
		{
			BgColor = new Color(0.04f, 0.05f, 0.07f, 0.85f),
			BorderColor = CardBorderColor,
			BorderWidthTop = 2,
			ContentMarginLeft = 12f,
			ContentMarginRight = 12f,
			ContentMarginTop = 8f,
			ContentMarginBottom = 10f,
		});
		root.AddChild(panel);

		var column = new VBoxContainer();
		column.AddThemeConstantOverride("separation", 6);
		panel.AddChild(column);

		_cardStatus = new Label();
		column.AddChild(_cardStatus);

		_cardProgress = new ProgressBar
		{
			MaxValue = 1.0,
			Step = 0.0,
			ShowPercentage = false,
			CustomMinimumSize = new Vector2(0f, 6f),
			MouseFilter = Control.MouseFilterEnum.Ignore,
		};

		// The overlay's site-progress colours, so "going up" looks the same everywhere.
		_cardProgress.AddThemeStyleboxOverride("background", new StyleBoxFlat { BgColor = new Color(0f, 0f, 0f, 0.6f) });
		_cardProgress.AddThemeStyleboxOverride("fill", new StyleBoxFlat { BgColor = new Color(1f, 0.85f, 0.35f) });
		column.AddChild(_cardProgress);

		var units = new HBoxContainer();
		units.AddThemeConstantOverride("separation", 6);
		column.AddChild(units);

		for (int i = 0; i < CardUnits.Length; i++)
		{
			byte id = CardUnits[i];
			_cardButtons[i] = NewCardButton(units, new Vector2(104f, 56f));
			_cardButtons[i].Pressed += () => BuildPressed?.Invoke(id);
		}

		var footer = new HBoxContainer();
		footer.AddThemeConstantOverride("separation", 12);
		column.AddChild(footer);

		_cardCancel = NewCardButton(footer, new Vector2(0f, 32f));
		_cardCancel.Text = "Cancel last (Backspace)";
		_cardCancel.TooltipText = "takes the last unit off the queue and refunds it";
		_cardCancel.Pressed += () => CancelPressed?.Invoke();

		var hint = new Label
		{
			Text = "right-click the ground to set the rally point",
			VerticalAlignment = VerticalAlignment.Center,
			MouseFilter = Control.MouseFilterEnum.Ignore,
		};
		hint.AddThemeColorOverride("font_color", new Color(0.75f, 0.75f, 0.78f));
		hint.AddThemeFontSizeOverride("font_size", 12);
		footer.AddChild(hint);

		// Pinned by its bottom-right corner and grown up and to the left, so it is
		// whatever size its buttons need without this having to know it.
		panel.SetAnchorsPreset(Control.LayoutPreset.BottomRight);
		panel.GrowHorizontal = Control.GrowDirection.Begin;
		panel.GrowVertical = Control.GrowDirection.Begin;
		panel.OffsetLeft = -16f;
		panel.OffsetRight = -16f;
		panel.OffsetTop = -140f;
		panel.OffsetBottom = -140f;

		_card = panel;
	}

	private static Button NewCardButton(Control parent, Vector2 minimumSize)
	{
		var button = new Button
		{
			CustomMinimumSize = minimumSize,

			// Never focused: Space raises the camera, and a focused button takes Space
			// as a press.
			FocusMode = Control.FocusModeEnum.None,
		};

		// The default theme's buttons are near-black, which on a near-black card
		// reads as text rather than as something to press.
		button.AddThemeStyleboxOverride("normal", CardButtonStyle(new Color(0.14f, 0.18f, 0.23f)));
		button.AddThemeStyleboxOverride("hover", CardButtonStyle(new Color(0.2f, 0.26f, 0.33f)));
		button.AddThemeStyleboxOverride("pressed", CardButtonStyle(new Color(0.09f, 0.11f, 0.14f)));
		button.AddThemeStyleboxOverride("disabled", CardButtonStyle(new Color(0.08f, 0.09f, 0.11f)));

		parent.AddChild(button);
		return button;
	}

	private static StyleBoxFlat CardButtonStyle(Color background)
	{
		var style = new StyleBoxFlat { BgColor = background, BorderColor = CardBorderColor };
		style.SetBorderWidthAll(1);
		style.SetCornerRadiusAll(3);
		style.SetContentMarginAll(6f);
		return style;
	}

	private static Label NewLabel(Control parent, Vector2 position, Vector2 size, float anchorTop = 0f)
	{
		var label = new Label
		{
			MouseFilter = Control.MouseFilterEnum.Ignore,
			AnchorTop = anchorTop,
			Position = position,
			Size = size,
		};

		// Greybox legibility: the map is a bright grey box and the HUD has no panel
		// behind it.
		label.AddThemeColorOverride("font_outline_color", Colors.Black);
		label.AddThemeConstantOverride("outline_size", 4);
		parent.AddChild(label);
		return label;
	}
}
