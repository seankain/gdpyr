using Godot;

namespace Gdpyr.Sim;

/// <summary>
/// The four orders a strategist can give, plus the two that are not orders
/// (docs/IMPLEMENTATION_PLAN.md §M3), plus the one only a builder is given, plus
/// the two a vehicle that carries units and its passengers are given
/// (docs/VEHICLES.md §5.2).
///
/// The numbers are on the wire, so they are written down rather than inherited
/// from declaration order. <see cref="Stop"/> is a command and never a stored
/// state: a unit that is told to stop ends up holding <see cref="None"/>.
/// </summary>
public enum OrderKind : byte
{
	/// <summary>No standing order. The unit holds where it is and shoots what it can reach.</summary>
	None = 0,

	/// <summary>Go there. Shoots on the way without stopping, and does not chase.</summary>
	Move = 1,

	/// <summary>Attack-move: go there, but stop and fight anything met on the way.</summary>
	Attack = 2,

	/// <summary>Walk between two points until told otherwise, shooting on the way.</summary>
	Patrol = 3,

	/// <summary>Hold a point, engaging anything that comes within leash, then return to it.</summary>
	Defend = 4,

	/// <summary>Clear the standing order. Never stored (see <see cref="None"/>).</summary>
	Stop = 5,

	/// <summary>
	/// Go and work on a structure: build it if it is a site, mend it if it is hurt
	/// (docs/NETCODE.md §10.5). Builders only, and never through the generic order
	/// message: it names a structure, so it arrives by its own request and
	/// <see cref="UnitOrder.IsIssuable"/> refuses it. <see cref="UnitOrder.Target"/> is
	/// where to stand, <see cref="UnitOrder.Anchor"/> the structure's base point and
	/// <see cref="UnitOrder.TargetOwnerId"/> its <see cref="OwnerId.ForStructure"/>.
	/// </summary>
	Build = 6,

	/// <summary>
	/// Get into a vehicle, and ride in it (docs/VEHICLES.md §5.2). Never through the
	/// generic order message, for <see cref="Build"/>'s reason: it names a vehicle, so
	/// it arrives by its own request. <see cref="UnitOrder.Target"/> is where to stand
	/// beside the vehicle while walking to it, and the vehicle's position once aboard;
	/// <see cref="UnitOrder.Anchor"/> where the unit stood when it was ordered;
	/// <see cref="UnitOrder.TargetOwnerId"/> the vehicle's <see cref="OwnerId.ForUnit"/>.
	/// Shoots on the walk, as a move does.
	/// </summary>
	Embark = 7,

	/// <summary>
	/// A vehicle letting everybody aboard out where it is (docs/VEHICLES.md §5.2,
	/// V-D5): it stands still, and they get out round it. Its own request, too.
	/// <see cref="UnitOrder.Target"/> and <see cref="UnitOrder.Anchor"/> are where it
	/// was told, which nothing steers by.
	/// </summary>
	Unload = 8,
}

/// <summary>
/// One unit's standing order. Server-side state: a client shows the marker it
/// clicked immediately and is told nothing about what became of the order beyond
/// where the unit subsequently goes (docs/NETCODE.md §6.3).
/// </summary>
public struct UnitOrder
{
	public OrderKind Kind;

	/// <summary>The destination, the patrol's far end, or the point being defended.</summary>
	public Vector3 Target;

	/// <summary>The patrol's near end, and the anchor a defending unit returns to.</summary>
	public Vector3 Anchor;

	/// <summary>
	/// What to shoot at, as an <see cref="OwnerId"/>: a peer, a unit, or
	/// <see cref="OwnerId.None"/> for "whatever you find". Only meaningful for
	/// <see cref="OrderKind.Attack"/> — and for <see cref="OrderKind.Build"/>, where it
	/// names the structure to work on rather than anything to shoot.
	/// </summary>
	public int TargetOwnerId;

	/// <summary>Patrol only: true while walking back towards <see cref="Anchor"/>.</summary>
	public bool Returning;

	public readonly bool IsStanding => Kind != OrderKind.None;

	public static UnitOrder Hold(Vector3 at) => new()
	{
		Kind = OrderKind.None,
		Target = at,
		Anchor = at,
		TargetOwnerId = OwnerId.None,
	};

	/// <summary>
	/// Clamps an order type off the wire. A client picks the byte, so "order 200"
	/// is something the server has to answer for (docs/NETCODE.md §1).
	/// <see cref="OrderKind.Build"/> is not issuable here: it has its own request,
	/// which checks what the generic one cannot — that the unit is a builder and the
	/// structure is the sender's. Nor are <see cref="OrderKind.Embark"/> and
	/// <see cref="OrderKind.Unload"/>, which name a vehicle and have requests of their own.
	/// </summary>
	public static bool IsIssuable(byte kind) =>
		kind is >= (byte)OrderKind.Move and <= (byte)OrderKind.Stop;
}
