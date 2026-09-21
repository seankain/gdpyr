using Godot;

namespace Gdpyr.Sim;

/// <summary>
/// The four orders a strategist can give, plus the two that are not orders
/// (docs/IMPLEMENTATION_PLAN.md §M3).
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
	/// <see cref="OrderKind.Attack"/>.
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
	/// </summary>
	public static bool IsIssuable(byte kind) =>
		kind is >= (byte)OrderKind.Move and <= (byte)OrderKind.Stop;
}
