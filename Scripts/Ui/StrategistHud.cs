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
/// told them.
/// </summary>
public partial class StrategistHud : CanvasLayer
{
	private const string Keys =
		"WASD/edge pan · Q/E rotate · wheel zoom · LMB select · RMB order\n"
		+ "X attack-move · C patrol · B defend · Z stop · F select all\n"
		+ "1/2/3 queue infantry/technical/tank · 4 queue builder · Backspace cancel · RMB with nothing selected sets the rally point\n"
		+ "with builders selected: 5/6/7 then RMB places pillbox/sandbags/tower (Q/E turns it) · RMB on a site finishes or mends it\n"
		+ "F1 ground force · F2 strategist";

	private Label _points;
	private Label _production;
	private Label _selection;
	private Label _economy;
	private Label _builders;
	private Label _keys;

	public SelectionOverlay Overlay { get; private set; }

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
	}

	/// <summary>Called once per simulation tick by <see cref="StrategistController"/>.</summary>
	public void Refresh(MatchState match, UnitManager units, uint tick, int selectedCount, OrderKind pendingOrder,
		EconomyService economy)
	{
		int live = units?.LiveUnitCount ?? 0;

		_points.Text = match.Phase == RoundPhase.Live
			? $"points {match.StrategistPoints}   units {live}/{SimConfig.MaxUnits}   {Clock(match.SecondsRemaining(tick))}"
			: $"points {match.StrategistPoints}   units {live}/{SimConfig.MaxUnits}   {match.Phase}";

		UnitManager.BarracksStatus status = units?.StatusOf(0, tick) ?? default;
		_production.Text = status.Queued > 0
			? $"barracks: {UnitCatalog.NameOf(status.Head)} {Mathf.RoundToInt(status.Progress * 100f)}%"
				+ $"   queued {status.Queued}"
			: "barracks: idle";

		_selection.Text = $"selected {selectedCount}   next order: {Describe(pendingOrder)}";
		_economy.Text = Economy(economy);
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
