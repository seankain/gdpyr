using System;
using Gdpyr.Core;
using Gdpyr.Match;
using Gdpyr.Rts;
using Gdpyr.Sim;
using Godot;

namespace Gdpyr.Ui;

/// <summary>
/// What the strategist needs on screen: points, what the barracks is building, the
/// nodes, and a command card for whatever is selected (docs/IMPLEMENTATION_PLAN.md §3,
/// "Ui/ RTS HUD").
///
/// Built in code and hosting <see cref="SelectionOverlay"/>, for the same reason
/// <see cref="CombatHud"/> is: a greybox HUD that costs a scene file costs a merge
/// conflict every time a milestone touches it.
///
/// Little text on screen at any one time, on purpose: three short lines top left, and
/// the card, which shows only what the selection can do — a barracks' units, a
/// builder's structures, a vehicle's riders, the orders — each a button with its key
/// printed on it (<see cref="StrategistCards"/>). A new player clicks a thing and the
/// buttons say what the keys are; an experienced one never needs to look. The full
/// key list is folded away behind <c>K</c> and the <b>Keys</b> button.
/// </summary>
public partial class StrategistHud : CanvasLayer
{
	/// <summary>
	/// What the strategist has selected, as the HUD needs it (docs/VEHICLES.md §7):
	/// how many units, and of them the vehicles that carry units — how many, how many
	/// are aboard them and how many seats they have, and whether one is an aircraft
	/// with nowhere to land. <see cref="Notice"/> is a line shown for a moment at the
	/// top of the screen: a vehicle that was full.
	/// </summary>
	public struct SelectionSummary
	{
		public int Count;
		public int Vehicles;
		public int Aboard;
		public int Seats;
		public bool CannotLand;

		/// <summary>Units in the selection whose micro-autonomy means anything: those with a doctrine (docs/TACTICS.md §5.9).</summary>
		public int Tactical;

		/// <summary>Of those, the ones with it on.</summary>
		public int Autonomous;

		/// <summary>
		/// Units in the selection that put structures up (docs/NETCODE.md §10.5). When it is
		/// all of them, the card is the builder card.
		/// </summary>
		public int Builders;

		/// <summary>The one vehicle's kind when <see cref="Vehicles"/> is 1, as the catalog names it.</summary>
		public string VehicleName;

		/// <summary>The one unit's kind when <see cref="Count"/> is 1, as the catalog names it.</summary>
		public string UnitName;

		public string Notice;
	}

	/// <summary>Pixels the HUD keeps from the window's edges.</summary>
	private const float Margin = 16f;

	/// <summary>Pixels between the key list, its button and the card.</summary>
	private const float Gap = 8f;

	private const float KeysButtonHeight = 28f;

	/// <summary>Pixels below the top of the window the notice sits: under the round clock and the new-units button.</summary>
	private const float NoticeTop = 56f;

	/// <summary>
	/// Every unit a card can offer, in the order the keys are: the three tiers, the
	/// builder and the supply truck, which a barracks trains, then the sniper and the
	/// heavy gunner, which a sniper tower and a pillbox do (docs/NETCODE.md §10.8), and
	/// the dropship, which a barracks trains on <c>T</c> (docs/VEHICLES.md §7). A card
	/// shows the buttons for what the thing it is up for trains, and hides the rest.
	/// </summary>
	private static readonly byte[] CardUnits =
	{
		UnitCatalog.Infantry, UnitCatalog.Technical, UnitCatalog.Tank, UnitCatalog.Builder, UnitCatalog.SupplyTruck,
		UnitCatalog.Sniper, UnitCatalog.HeavyGunner, UnitCatalog.Dropship,
	};

	/// <summary>The key that queues each of <see cref="CardUnits"/>, printed on its button.</summary>
	private static readonly string[] CardKeys = { "1", "2", "3", "4", "8", "9", "0", "T" };

	private static readonly Color CardBorderColor = new(0.35f, 0.45f, 0.55f, 0.9f);

	/// <summary>The armed order's or structure's button, and the notice: the overlay's site-progress amber.</summary>
	private static readonly Color ArmedColor = new(1f, 0.85f, 0.35f);

	/// <summary>
	/// Whether the key list is open. Static so that it stays the way the player left it
	/// when they swap seats and come back, which builds a new HUD.
	/// </summary>
	private static bool s_keysShown;

	private Label _points;
	private Label _production;
	private Label _economy;
	private Label _notice;
	private Button _defaultTactics;

	private Control _keysPanel;
	private Button _keysButton;

	private Control _card;
	private Label _cardStatus;
	private ProgressBar _cardProgress;
	private Control _unitRow;
	private readonly Button[] _cardButtons = new Button[CardUnits.Length];
	private Control _structureRow;
	private readonly Button[] _structureButtons = new Button[StrategistCards.Structures.Length];
	private Control _orderRow;
	private readonly Button[] _orderButtons = new Button[StrategistCards.Orders.Length];
	private Button _cardCancel;
	private Button _cardUnload;
	private Button _cardTactics;
	private Label _cardHint;

	public SelectionOverlay Overlay { get; private set; }

	/// <summary>A unit button on the card was pressed. Carries the unit's catalog id.</summary>
	public event Action<byte> BuildPressed;

	/// <summary>A structure button on the builder card was pressed: arm it, or put it away. Carries its <see cref="StructureKinds"/>.</summary>
	public event Action<byte> PlacePressed;

	/// <summary>An order button on the card was pressed: arm the order (or put it away), or, for stop, give it now.</summary>
	public event Action<OrderKind> OrderPressed;

	/// <summary>The card's cancel button was pressed.</summary>
	public event Action CancelPressed;

	/// <summary>The card's unload button was pressed, with vehicles selected.</summary>
	public event Action UnloadPressed;

	/// <summary>The card's tactics button was pressed: switch the selection's micro-autonomy (docs/TACTICS.md §5.9).</summary>
	public event Action TacticsPressed;

	/// <summary>The new-units button was pressed: switch what the side's new units start with.</summary>
	public event Action TacticsDefaultPressed;

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
		_economy = NewLabel(root, new Vector2(24f, 80f), new Vector2(520f, 32f));

		// What the side's new units start with (docs/TACTICS.md §5.9), beside the points:
		// a setting, so a button rather than a key.
		_defaultTactics = NewCardButton(root, new Vector2(0f, 28f));
		_defaultTactics.Position = new Vector2(460f, 14f);
		_defaultTactics.TooltipText = "what the units your side makes from now on start with;"
			+ " auto units take cover from fire and fire back on their own";
		_defaultTactics.Pressed += () => TacticsDefaultPressed?.Invoke();

		BuildNotice(root);
		BuildKeys(root);
		BuildCard(root);
		ShowKeys(s_keysShown);
	}

	/// <summary>
	/// Called once per simulation tick by <see cref="StrategistController"/>.
	/// <paramref name="barracks"/> is the one the build keys mean, and
	/// <paramref name="barracksSelected"/> whether the strategist clicked on it.
	/// <paramref name="structure"/> is the sniper tower or pillbox clicked on for its
	/// queue, or null. <paramref name="placing"/> is the structure the next right-click
	/// puts up, or anything that is not a <see cref="StructureKinds"/> when none is armed.
	/// Which card is up is <see cref="StrategistCards.Choose"/>'s to say.
	/// </summary>
	public void Refresh(MatchState match, UnitManager units, uint tick, in SelectionSummary selection,
		OrderKind pendingOrder, byte placing, EconomyService economy, int barracks, bool barracksSelected,
		Structure structure)
	{
		int live = units?.LiveUnitCount ?? 0;

		_points.Text = match.Phase == RoundPhase.Live
			? $"points {match.StrategistPoints} · units {live}/{SimConfig.MaxUnits} · {Clock(match.SecondsRemaining(tick))}"
			: $"points {match.StrategistPoints} · units {live}/{SimConfig.MaxUnits} · {match.Phase}";

		UnitManager.BarracksStatus status = units?.StatusOf(barracks, tick) ?? default;
		// Forward deployment (docs/CONTROL_POINTS.md §2): its rally point on a node the
		// side holds puts the units out there, which is worth saying where the queue is.
		// TODO(control points 3, docs/CONTROL_POINTS.md §6): check on a display that this line and
		// the drone warning on the node line fit their 520 px labels. Never looked at.
		string deploy = status.DeployNode != ForwardDeploy.Door ? " · out at the held node" : string.Empty;
		_production.Text = status.Queued > 0
			? $"barracks: {Display(UnitCatalog.NameOf(status.Head))} {Mathf.RoundToInt(status.Progress * 100f)}%"
				+ $" · queued {status.Queued}{deploy}"
			: $"barracks: idle{deploy}";

		_economy.Text = Economy(economy);

		// What the ground force's drone nodes have built them (docs/CONTROL_POINTS.md §4.4):
		// the one thing on the field that kills a tank in one hit, and worth a tank's distance.
		if (CombatManager.Instance?.Drones is { CanLaunch: true } drones)
		{
			_economy.Text += $" · ground force has {drones.Ready} drone{(drones.Ready == 1 ? string.Empty : "s")} ready";
		}
		_defaultTactics.Text = $"new units: {(units?.AutonomyDefault == MicroAutonomy.On ? "Auto" : "Direct")}";

		string placingName = StructureKinds.IsValid(placing) ? Display(StructureCatalog.NameOf(placing)) : null;
		RefreshNotice(selection, placingName);

		StrategistCardMode mode = StrategistCards.Choose(structure != null, barracksSelected, selection.Count,
			selection.Builders, selection.Vehicles);

		_card.Visible = mode != StrategistCardMode.None;
		if (mode == StrategistCardMode.None)
		{
			return;
		}

		ShowRows(mode, selection);
		_cardHint.Text = StrategistCards.RightClick(mode, pendingOrder, selection.Builders > 0 ? placingName : null);

		switch (mode)
		{
			case StrategistCardMode.Structure:
				RefreshTraining(match, NameOf(structure), structure.TrainingQueued, structure.TrainsUnit,
					structure.TrainingProgress(tick), only: structure.TrainsUnit);
				break;
			case StrategistCardMode.Barracks:
				RefreshTraining(match, "barracks", status.Queued, status.Head, status.Progress, only: -1);
				break;
			case StrategistCardMode.Builders:
				RefreshBuilders(match, units, selection, placing);
				break;
			case StrategistCardMode.Vehicles:
				RefreshVehicles(selection);
				break;
			default:
				RefreshUnits(selection);
				break;
		}

		if (StrategistCards.HasOrders(mode))
		{
			RefreshOrders(pendingOrder);
		}

		PlaceCard();
	}

	/// <summary><c>K</c> and the <b>Keys</b> button: opens the full key list, or folds it away.</summary>
	public void ToggleKeys() => ShowKeys(!s_keysShown);

	private void ShowKeys(bool shown)
	{
		s_keysShown = shown;
		_keysPanel.Visible = shown;
		_keysButton.Text = shown ? "Hide keys (K)" : "Keys (K)";
		PlaceCard();
	}

	/// <summary>
	/// The one line at the top that comes and goes: a notice — a vehicle that was full,
	/// tactics switched — or, while a structure is armed with no builder selected to put
	/// it up, what is missing. Hidden otherwise.
	/// </summary>
	private void RefreshNotice(in SelectionSummary selection, string placingName)
	{
		string text = !string.IsNullOrEmpty(selection.Notice)
			? selection.Notice
			: placingName != null && selection.Builders == 0
				? $"placing a {placingName}: select a builder to put it up"
				: string.Empty;

		_notice.Text = text;
		_notice.Visible = text.Length > 0;
	}

	/// <summary>
	/// Which of the card's rows the mode has. Set once per refresh, each to its final
	/// value, so a container is not laid out twice a tick by a row that goes and comes back.
	/// </summary>
	private void ShowRows(StrategistCardMode mode, in SelectionSummary selection)
	{
		bool training = mode is StrategistCardMode.Barracks or StrategistCardMode.Structure;
		bool orders = StrategistCards.HasOrders(mode);

		_cardProgress.Visible = training;
		_unitRow.Visible = training;
		_cardCancel.Visible = training;
		_structureRow.Visible = mode == StrategistCardMode.Builders;
		_orderRow.Visible = orders;
		_cardUnload.Visible = mode == StrategistCardMode.Vehicles;
		_cardTactics.Visible = orders && selection.Tactical > 0;
	}

	/// <summary>
	/// The builder card (docs/NETCODE.md §10.5): how many builders, how many structures
	/// stand and are going up, and a button per structure they put up — pressed is the
	/// same as its key, and the armed one stays lit until it is placed or put away. One
	/// the strategist cannot afford is greyed out, as on the barracks card.
	/// </summary>
	private void RefreshBuilders(MatchState match, UnitManager units, in SelectionSummary selection, byte placing)
	{
		int standing = units?.StructureCount ?? 0;
		int sites = units?.SitesUnderConstruction ?? 0;
		string who = selection.Count == 1 ? "builder" : $"{selection.Count} builders";
		_cardStatus.Text = $"{who} · structures {standing}/{SimConfig.MaxStructures}"
			+ (sites > 0 ? $", {sites} going up" : string.Empty);

		bool full = standing >= SimConfig.MaxStructures;
		for (int i = 0; i < StrategistCards.Structures.Length; i++)
		{
			byte kind = StrategistCards.Structures[i];
			int cost = StructureCatalog.CostOf(kind);
			bool armed = placing == kind;
			Button button = _structureButtons[i];

			// Every refresh, for the reason the unit buttons are: this HUD does not know
			// whether the catalog was loaded before it was built.
			button.Text = $"{StructureCatalog.NameOf(kind).Capitalize()} ({StrategistCards.StructureKeys[i]})\n{cost} pts";
			button.TooltipText = StructureTooltip(kind);

			// The armed one stays pressable, so a click puts it away whatever the points say.
			button.Disabled = !armed && (full || !StructureCatalog.IsBuildable(kind) || match.StrategistPoints < cost);
			button.SetPressedNoSignal(armed);
		}
	}

	/// <summary>What a structure is, for its button's tooltip: the numbers the README gives.</summary>
	private static string StructureTooltip(byte kind)
	{
		if (StructureCatalog.Definition(kind) is not { } definition)
		{
			return string.Empty;
		}

		string text = $"{definition.Cost} points, {definition.BuildSeconds:0.#} s for one builder to put up,"
			+ $" {definition.MaxHealth:0} health";
		text += definition.IsBulletProof ? ", bullet-proof" : string.Empty;
		text += definition.IsArmed ? $"; a gun that reaches {definition.RangeMeters:0} m" : "; cover, and nothing else";
		text += definition.Trains ? $"; trains {Display(UnitCatalog.NameOf(definition.TrainsUnit))}s" : string.Empty;
		return text;
	}

	/// <summary>
	/// The unit card (docs/TACTICS.md §5.9): what is selected, how it fights — auto,
	/// direct, or mixed — the orders, and the tactics switch where it means anything.
	/// </summary>
	private void RefreshUnits(in SelectionSummary selection)
	{
		string who = selection.Count == 1 && selection.UnitName != null
			? Display(selection.UnitName)
			: $"{selection.Count} selected";

		_cardStatus.Text = selection.Tactical > 0
			? $"{who} · tactics {MicroAutonomyNames.Describe(selection.Tactical, selection.Autonomous).ToLowerInvariant()}"
				+ (selection.Autonomous > 0 && selection.Autonomous < selection.Tactical
					? $" ({selection.Autonomous} of {selection.Tactical} auto)"
					: string.Empty)
			: who;

		RefreshTactics(selection);
	}

	/// <summary>The tactics button's label. Whether it is up at all is <see cref="ShowRows"/>'.</summary>
	private void RefreshTactics(in SelectionSummary selection)
	{
		_cardTactics.Text = $"Tactics: {MicroAutonomyNames.Describe(selection.Tactical, selection.Autonomous)} (H)";
	}

	/// <summary>
	/// The vehicle card (docs/VEHICLES.md §7): the selected vehicles that carry units,
	/// how many are aboard of how many seats, the orders, and the button that lets them
	/// out — greyed out with nobody aboard.
	/// </summary>
	private void RefreshVehicles(in SelectionSummary selection)
	{
		string what = selection.Vehicles == 1
			? Display(selection.VehicleName ?? "vehicle")
			: $"{selection.Vehicles} vehicles";
		_cardStatus.Text = selection.CannotLand
			? $"{what} · no room to land"
			: $"{what} · {selection.Aboard}/{selection.Seats} aboard";

		_cardUnload.Disabled = selection.Aboard == 0;
		RefreshTactics(selection);
	}

	/// <summary>The order row: the armed order's button lit. Stop is never armed; it is given when pressed.</summary>
	private void RefreshOrders(OrderKind pendingOrder)
	{
		for (int i = 0; i < StrategistCards.Orders.Length; i++)
		{
			OrderKind kind = StrategistCards.Orders[i];
			_orderButtons[i].SetPressedNoSignal(kind != OrderKind.Stop && kind == pendingOrder);
		}
	}

	/// <summary>
	/// The barracks or structure card: what is training, and which buttons can be
	/// pressed. It offers what a barracks trains when <paramref name="only"/> is
	/// negative, and that one unit when it is a structure's. A button the strategist
	/// cannot afford is greyed out rather than hidden, so the card always shows the
	/// whole menu and the price of each thing on it.
	/// </summary>
	private void RefreshTraining(MatchState match, string title, int queued, byte head, float progress, int only)
	{
		_cardStatus.Text = queued > 0
			? $"{title}: training {Display(UnitCatalog.NameOf(head))} {Mathf.RoundToInt(progress * 100f)}%"
				+ $" · queued {queued}"
			: $"{title}: idle";
		_cardProgress.Value = queued > 0 ? progress : 0f;

		bool full = queued >= SimConfig.MaxBuildQueue;
		for (int i = 0; i < CardUnits.Length; i++)
		{
			byte id = CardUnits[i];
			int cost = UnitCatalog.CostOf(id);
			Button button = _cardButtons[i];

			button.Visible = only >= 0 ? id == only : UnitCatalog.IsTrainedInBarracks(id);
			if (!button.Visible)
			{
				continue;
			}

			// Set every refresh rather than once, because the catalog is loaded by the
			// unit manager and this HUD does not know which of them was ready first.
			// The setters do nothing when the text is unchanged.
			button.Text = $"{UnitCatalog.NameOf(id).Capitalize()} ({CardKeys[i]})\n{cost} pts";
			button.TooltipText = UnitCatalog.Definition(id) is { } definition
				? $"{cost} points, {definition.BuildSeconds:0.#} s to train"
					+ (definition.IsSupply
						? $"; heals friendly units within {definition.SupplyRadiusMeters:0} m that are out of a fight"
						: string.Empty)
					+ (definition.SuppressSeconds > 0f
						? $"; keeps firing for {definition.SuppressSeconds:0.#} s where a target went to ground"
						: string.Empty)
					+ (UnitCatalog.SeatsOf(id) > 0 ? $"; seats {UnitCatalog.SeatsOf(id)}" : string.Empty)
					+ (definition.IsAircraft ? "; flies, unarmed, lands to load and unload" : string.Empty)
				: string.Empty;
			button.Disabled = full || !UnitCatalog.IsBuildable(id) || match.StrategistPoints < cost;
		}

		_cardCancel.Disabled = queued == 0;
	}

	/// <summary>
	/// The card sits in the bottom-right corner, and above the key list while that is
	/// open: the list is as wide as a narrow window.
	/// </summary>
	private void PlaceCard()
	{
		if (_card == null || _keysPanel == null)
		{
			return;
		}

		float bottom = -Margin - (s_keysShown
			? KeysButtonHeight + Gap + _keysPanel.GetCombinedMinimumSize().Y + Gap
			: 0f);
		_card.OffsetTop = bottom;
		_card.OffsetBottom = bottom;
	}

	/// <summary>A structure's name as a person reads it: "sniper tower", not "sniper_tower".</summary>
	private static string NameOf(Structure structure) => Display(StructureCatalog.NameOf(structure.Kind));

	/// <summary>A catalog name as a person reads it.</summary>
	private static string Display(string name) => name.Replace('_', ' ');

	/// <summary>"attack-move" as a button says it: "Attack-move".</summary>
	private static string Sentence(string text) =>
		string.IsNullOrEmpty(text) ? text : char.ToUpperInvariant(text[0]) + text[1..];

	/// <summary>
	/// What the nodes are doing, in one line: what is being held, what has been lost
	/// and what is being fought over (docs/IMPLEMENTATION_PLAN.md §M5). Only what is
	/// not zero; nothing at all on a map without nodes.
	///
	/// A strategist with no nodes and no units is one losing condition away from
	/// the round being over, so this is the line that says how close that is.
	/// </summary>
	private static string Economy(EconomyService economy)
	{
		if (economy == null || economy.NodeCount == 0)
		{
			return string.Empty;
		}

		return $"nodes {economy.StrategistNodes}/{economy.NodeCount} held"
			+ (economy.GroundNodes > 0 ? $" · {economy.GroundNodes} lost" : string.Empty)
			+ (economy.ContestedNodes > 0 ? $" · {economy.ContestedNodes} contested" : string.Empty);
	}

	private static string Clock(float seconds)
	{
		int whole = Mathf.Max(0, Mathf.RoundToInt(seconds));
		return $"{whole / 60:00}:{whole % 60:00}";
	}

	private void BuildNotice(Control root)
	{
		_notice = new Label
		{
			MouseFilter = Control.MouseFilterEnum.Ignore,
			HorizontalAlignment = HorizontalAlignment.Center,
			Visible = false,
		};
		_notice.AddThemeColorOverride("font_color", ArmedColor);
		_notice.AddThemeColorOverride("font_outline_color", Colors.Black);
		_notice.AddThemeConstantOverride("outline_size", 4);
		root.AddChild(_notice);

		// Pinned by the middle of its top edge and grown both ways, so it is centred
		// whatever it says.
		_notice.SetAnchorsPreset(Control.LayoutPreset.CenterTop);
		_notice.GrowHorizontal = Control.GrowDirection.Both;
		_notice.OffsetLeft = 0f;
		_notice.OffsetRight = 0f;
		_notice.OffsetTop = NoticeTop;
		_notice.OffsetBottom = NoticeTop;
	}

	/// <summary>
	/// The key list, folded away behind a small button in the bottom-left corner. It
	/// passes clicks through to the map: it is something to read, not to press.
	/// </summary>
	private void BuildKeys(Control root)
	{
		_keysButton = NewCardButton(root, new Vector2(0f, KeysButtonHeight));
		_keysButton.TooltipText = "every key the strategist has";
		_keysButton.Pressed += ToggleKeys;
		_keysButton.SetAnchorsPreset(Control.LayoutPreset.BottomLeft);
		_keysButton.GrowVertical = Control.GrowDirection.Begin;
		_keysButton.OffsetLeft = Margin;
		_keysButton.OffsetRight = Margin;
		_keysButton.OffsetTop = -Margin;
		_keysButton.OffsetBottom = -Margin;

		var panel = new PanelContainer { Visible = false, MouseFilter = Control.MouseFilterEnum.Ignore };
		panel.AddThemeStyleboxOverride("panel", CardStyle());
		root.AddChild(panel);

		var keys = new Label { Text = StrategistCards.KeyList, MouseFilter = Control.MouseFilterEnum.Ignore };
		keys.AddThemeFontSizeOverride("font_size", 13);
		panel.AddChild(keys);

		float bottom = -(Margin + KeysButtonHeight + Gap);
		panel.SetAnchorsPreset(Control.LayoutPreset.BottomLeft);
		panel.GrowVertical = Control.GrowDirection.Begin;
		panel.OffsetLeft = Margin;
		panel.OffsetRight = Margin;
		panel.OffsetTop = bottom;
		panel.OffsetBottom = bottom;

		_keysPanel = panel;
	}

	/// <summary>
	/// The command card: a status line, then the rows the selection has — what a
	/// barracks or structure trains, what builders put up, the orders — and a footer
	/// with cancel, unload, tactics and what a right-click does. Up only while
	/// something is selected, in the bottom-right corner.
	/// </summary>
	private void BuildCard(Control root)
	{
		var panel = new PanelContainer
		{
			Visible = false,

			// Stop, so a click on the card is not also a box select of the map behind it.
			MouseFilter = Control.MouseFilterEnum.Stop,
		};
		panel.AddThemeStyleboxOverride("panel", CardStyle());
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
		_cardProgress.AddThemeStyleboxOverride("fill", new StyleBoxFlat { BgColor = ArmedColor });
		column.AddChild(_cardProgress);

		_unitRow = NewRow(column);
		for (int i = 0; i < CardUnits.Length; i++)
		{
			byte id = CardUnits[i];
			_cardButtons[i] = NewCardButton(_unitRow, new Vector2(104f, 56f));
			_cardButtons[i].Pressed += () => BuildPressed?.Invoke(id);
		}

		_structureRow = NewRow(column);
		for (int i = 0; i < StrategistCards.Structures.Length; i++)
		{
			byte kind = StrategistCards.Structures[i];
			_structureButtons[i] = NewCardButton(_structureRow, new Vector2(132f, 56f), toggle: true);
			_structureButtons[i].Pressed += () => PlacePressed?.Invoke(kind);
		}

		_orderRow = NewRow(column);
		for (int i = 0; i < StrategistCards.Orders.Length; i++)
		{
			OrderKind kind = StrategistCards.Orders[i];
			Button button = NewCardButton(_orderRow, new Vector2(0f, 32f), toggle: kind != OrderKind.Stop);
			button.Text = $"{Sentence(StrategistCards.NameOf(kind))} ({StrategistCards.OrderKeys[i]})";
			button.TooltipText = StrategistCards.DescribeOrder(kind);
			button.Pressed += () => OrderPressed?.Invoke(kind);
			_orderButtons[i] = button;
		}

		var footer = new HBoxContainer();
		footer.AddThemeConstantOverride("separation", 12);
		column.AddChild(footer);

		_cardCancel = NewCardButton(footer, new Vector2(0f, 32f));
		_cardCancel.Text = "Cancel last (Backspace)";
		_cardCancel.TooltipText = "takes the last unit off the queue and refunds it";
		_cardCancel.Pressed += () => CancelPressed?.Invoke();

		_cardUnload = NewCardButton(footer, new Vector2(0f, 32f));
		_cardUnload.Text = "Unload (U)";
		_cardUnload.TooltipText = "everybody aboard gets out round the vehicle, where it stands";
		_cardUnload.Visible = false;
		_cardUnload.Pressed += () => UnloadPressed?.Invoke();

		_cardTactics = NewCardButton(footer, new Vector2(0f, 32f));
		_cardTactics.TooltipText = "auto: they take cover from where they are fired on and fire back, on their own;"
			+ " direct: they do exactly what they are told";
		_cardTactics.Visible = false;
		_cardTactics.Pressed += () => TacticsPressed?.Invoke();

		// Set per mode: what a right-click means depends on what the card is up for.
		_cardHint = new Label
		{
			VerticalAlignment = VerticalAlignment.Center,
			MouseFilter = Control.MouseFilterEnum.Ignore,
		};
		_cardHint.AddThemeColorOverride("font_color", new Color(0.75f, 0.75f, 0.78f));
		_cardHint.AddThemeFontSizeOverride("font_size", 12);
		footer.AddChild(_cardHint);

		// Pinned by its bottom-right corner and grown up and to the left, so it is
		// whatever size its buttons need without this having to know it.
		panel.SetAnchorsPreset(Control.LayoutPreset.BottomRight);
		panel.GrowHorizontal = Control.GrowDirection.Begin;
		panel.GrowVertical = Control.GrowDirection.Begin;
		panel.OffsetLeft = -Margin;
		panel.OffsetRight = -Margin;
		panel.OffsetTop = -Margin;
		panel.OffsetBottom = -Margin;

		_card = panel;
	}

	private static HBoxContainer NewRow(Control parent)
	{
		var row = new HBoxContainer();
		row.AddThemeConstantOverride("separation", 6);
		parent.AddChild(row);
		return row;
	}

	/// <summary>
	/// A button for the card. <paramref name="toggle"/> makes it one that stays down while
	/// what it arms is armed: an order or a structure. The HUD sets whether it is down,
	/// every refresh, from what the controller says is armed.
	/// </summary>
	private static Button NewCardButton(Control parent, Vector2 minimumSize, bool toggle = false)
	{
		var button = new Button
		{
			CustomMinimumSize = minimumSize,
			ToggleMode = toggle,

			// Never focused: Space raises the camera, and a focused button takes Space
			// as a press.
			FocusMode = Control.FocusModeEnum.None,
		};

		// The default theme's buttons are near-black, which on a near-black card
		// reads as text rather than as something to press.
		button.AddThemeStyleboxOverride("normal", CardButtonStyle(new Color(0.14f, 0.18f, 0.23f), CardBorderColor));
		button.AddThemeStyleboxOverride("hover", CardButtonStyle(new Color(0.2f, 0.26f, 0.33f), CardBorderColor));
		button.AddThemeStyleboxOverride("disabled", CardButtonStyle(new Color(0.08f, 0.09f, 0.11f), CardBorderColor));

		if (toggle)
		{
			// Down means armed: the next right-click is this. Lit, not darkened.
			button.AddThemeStyleboxOverride("pressed", CardButtonStyle(new Color(0.32f, 0.27f, 0.12f), ArmedColor));
			button.AddThemeStyleboxOverride("hover_pressed", CardButtonStyle(new Color(0.38f, 0.32f, 0.14f), ArmedColor));
		}
		else
		{
			button.AddThemeStyleboxOverride("pressed", CardButtonStyle(new Color(0.09f, 0.11f, 0.14f), CardBorderColor));
		}

		parent.AddChild(button);
		return button;
	}

	private static StyleBoxFlat CardStyle() => new()
	{
		BgColor = new Color(0.04f, 0.05f, 0.07f, 0.85f),
		BorderColor = CardBorderColor,
		BorderWidthTop = 2,
		ContentMarginLeft = 12f,
		ContentMarginRight = 12f,
		ContentMarginTop = 8f,
		ContentMarginBottom = 10f,
	};

	private static StyleBoxFlat CardButtonStyle(Color background, Color border)
	{
		var style = new StyleBoxFlat { BgColor = background, BorderColor = border };
		style.SetBorderWidthAll(1);
		style.SetCornerRadiusAll(3);
		style.SetContentMarginAll(6f);
		return style;
	}

	private static Label NewLabel(Control parent, Vector2 position, Vector2 size)
	{
		var label = new Label
		{
			MouseFilter = Control.MouseFilterEnum.Ignore,
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
