using Gdpyr.Sim;

namespace Gdpyr.Ui;

/// <summary>What the strategist's command card is up for, chosen by <see cref="StrategistCards.Choose"/>.</summary>
public enum StrategistCardMode : byte
{
	/// <summary>Nothing selected: no card.</summary>
	None = 0,

	/// <summary>A barracks clicked on: its queue, a button per unit it trains.</summary>
	Barracks = 1,

	/// <summary>A finished sniper tower or pillbox clicked on: its queue, one button.</summary>
	Structure = 2,

	/// <summary>Nothing but builders selected: a button per structure they put up, and the orders.</summary>
	Builders = 3,

	/// <summary>Vehicles that carry units in the selection: who is aboard, unload, and the orders.</summary>
	Vehicles = 4,

	/// <summary>Any other selection of units: the orders, and tactics where they mean anything.</summary>
	Units = 5,
}

/// <summary>
/// The engine-free half of the strategist's command card (<see cref="StrategistHud"/>):
/// which card a selection gets, what its buttons are and which key each one is, and
/// what the next right-click means.
///
/// The card is the one place a new strategist reads the controls from — a button
/// per thing the selection can do, with its key printed on it — so the key list
/// can stay folded away until somebody asks for it (<c>K</c>). Kept apart from the
/// HUD so that "a selection of nothing but builders gets the builder card" and
/// "the card's keys are the input map's keys" are answered by <c>dotnet test</c>
/// rather than by standing a match up.
/// </summary>
public static class StrategistCards
{
	/// <summary>
	/// The orders on the card's order row, in the order their keys sit on the
	/// keyboard. Move is not on it: it is what a right-click does when none is armed.
	/// </summary>
	public static readonly OrderKind[] Orders = { OrderKind.Attack, OrderKind.Patrol, OrderKind.Defend, OrderKind.Stop };

	/// <summary>The key for each of <see cref="Orders"/>, printed on its button: the input map's <c>rts_order_*</c>.</summary>
	public static readonly string[] OrderKeys = { "X", "C", "B", "Z" };

	/// <summary>The structures on the builder card, in the order their keys are.</summary>
	public static readonly byte[] Structures = { StructureKinds.Pillbox, StructureKinds.SandbagWall, StructureKinds.SniperTower };

	/// <summary>The key for each of <see cref="Structures"/>, printed on its button: the input map's <c>rts_place_*</c>.</summary>
	public static readonly string[] StructureKeys = { "5", "6", "7" };

	/// <summary>
	/// Every key the strategist has, for the list <c>K</c> shows and hides. Folded away
	/// by default: what the selection can do is on the card, with its keys.
	/// </summary>
	public const string KeyList =
		"camera: WASD or the screen edge pan · Space/Ctrl raise/lower · hold RMB and drag to look, Q/E turn · wheel zoom\n"
		+ "select: LMB click or drag · F select all · click the barracks, a finished tower or a pillbox for its queue\n"
		+ "orders: RMB order · X attack-move · C patrol · B defend · Z stop · H tactics auto/direct · U unload\n"
		+ "    RMB an enemy to attack it · RMB a technical (4 seats) or dropship (8) with units selected to board\n"
		+ "barracks: 1 infantry · 2 technical · 3 tank · 4 builder · 8 supply truck · T dropship · Backspace cancel\n"
		+ "    RMB the ground with nothing selected moves the rally point\n"
		+ "towers and pillboxes: 9 sniper at a sniper tower · 0 heavy gunner at a pillbox\n"
		+ "builders: 5 pillbox · 6 sandbags · 7 sniper tower, then RMB places it (turn the view to turn it)\n"
		+ "    RMB a site or a damaged structure to finish or mend it\n"
		+ "units out of a fight for 3 s heal inside the barracks ring and a supply truck's ring\n"
		+ "K hide this list · F1 ground force · F2 strategist";

	/// <summary>
	/// Which card the selection gets. A clicked structure or barracks first — a click
	/// selects one or the other or units, never two — then units: the builder card only
	/// when every one of them is a builder, the vehicle card when any carries units, and
	/// the plain unit card otherwise.
	/// </summary>
	public static StrategistCardMode Choose(bool structureSelected, bool barracksSelected, int selected, int builders,
		int vehicles)
	{
		if (structureSelected)
		{
			return StrategistCardMode.Structure;
		}

		if (barracksSelected)
		{
			return StrategistCardMode.Barracks;
		}

		if (selected <= 0)
		{
			return StrategistCardMode.None;
		}

		if (builders >= selected)
		{
			return StrategistCardMode.Builders;
		}

		return vehicles > 0 ? StrategistCardMode.Vehicles : StrategistCardMode.Units;
	}

	/// <summary>Whether the card has the order row: any card that is up for units rather than a building.</summary>
	public static bool HasOrders(StrategistCardMode mode) =>
		mode is StrategistCardMode.Builders or StrategistCardMode.Vehicles or StrategistCardMode.Units;

	/// <summary>
	/// What an order button arms when pressed: the order, or — pressed again while it is
	/// armed — nothing, so the button is its own way back. The keys are unchanged: <c>X</c>
	/// pressed twice is still attack-move.
	/// </summary>
	public static OrderKind ArmFromCard(OrderKind pending, OrderKind pressed) =>
		pending == pressed ? OrderKind.Move : pressed;

	/// <summary>An order as the card says it.</summary>
	public static string NameOf(OrderKind kind) => kind switch
	{
		OrderKind.Attack => "attack-move",
		OrderKind.Patrol => "patrol",
		OrderKind.Defend => "defend",
		OrderKind.Stop => "stop",
		_ => "move",
	};

	/// <summary>What the order button says it does, for its tooltip.</summary>
	public static string DescribeOrder(OrderKind kind) => kind switch
	{
		OrderKind.Attack => "attack-move: the next right-click sends them there, fighting anything met on the way",
		OrderKind.Patrol => "patrol: the next right-click has them walk between here and there until told otherwise",
		OrderKind.Defend => "defend: the next right-click has them hold that point and engage anything that comes near",
		OrderKind.Stop => "stop: they drop their order and hold where they are",
		_ => "move",
	};

	/// <summary>
	/// The card's footer: what the next right-click does. <paramref name="placing"/> is the
	/// armed structure's name, or null when none is armed.
	/// </summary>
	public static string RightClick(StrategistCardMode mode, OrderKind pending, string placing)
	{
		if (!string.IsNullOrEmpty(placing) && mode is StrategistCardMode.Builders or StrategistCardMode.Units
			or StrategistCardMode.Vehicles)
		{
			return $"right-click: place the {placing} · turn the view to turn it";
		}

		return mode switch
		{
			StrategistCardMode.Barracks or StrategistCardMode.Structure => "right-click the ground: rally point",
			StrategistCardMode.Builders when pending == OrderKind.Move => "right-click: move · on a site: build or mend it",
			StrategistCardMode.Vehicles when pending == OrderKind.Move => "right-click: drive there",
			StrategistCardMode.None => string.Empty,
			_ => $"right-click: {NameOf(pending)}",
		};
	}
}
