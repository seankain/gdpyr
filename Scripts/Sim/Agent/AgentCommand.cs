using System;

namespace Gdpyr.Sim.Agent;

/// <summary>What a strategist seat may ask for (docs/AGENT_API.md §7.4).</summary>
public enum AgentCommandKind : byte
{
	/// <summary>Spends a tick doing nothing. Carried so a policy's action space has a null action.</summary>
	Noop = 0,

	/// <summary>Order units about: move, attack, patrol, defend, stop.</summary>
	Order = 1,

	/// <summary>Put a unit on a barracks queue.</summary>
	Build = 2,

	/// <summary>Take the last unit off a queue and get the points back.</summary>
	Cancel = 3,

	/// <summary>Move where a barracks' fresh units walk to.</summary>
	Rally = 4,

	/// <summary>
	/// Send builders to put a structure up (docs/NETCODE.md §10.5). Names its
	/// builders as an order names its units, and lands in the same
	/// <c>ServerConstruct</c> a human strategist's request does.
	/// </summary>
	Construct = 5,
}

/// <summary>
/// One command from a strategist policy.
///
/// Deliberately flat and fixed-size: a command list arrives twice a second, is
/// held for the seat's <c>step_mul</c> and is applied inside the server tick, and
/// an allocation per command would be an allocation per tick on the one path the
/// plan forbids it on (docs/IMPLEMENTATION_PLAN.md §3). The unit ids a command
/// names live in one shared buffer on the seat; this struct carries the window
/// into it.
///
/// Nothing here is validated. A command is a request, and every check a client's
/// RPC gets — is the sender a strategist, does that order exist, does this side
/// own those units — is applied by <c>UnitManager.ApplyOrder</c> when the command
/// runs, which is the whole point of going through the server-side entry points
/// rather than beside them (docs/AGENT_API.md §2).
/// </summary>
public struct AgentCommand
{
	public AgentCommandKind Kind;

	/// <summary><see cref="OrderKind"/> for <see cref="AgentCommandKind.Order"/>.</summary>
	public byte Order;

	/// <summary>Barracks index for build, cancel and rally.</summary>
	public int Barracks;

	/// <summary>Unit tier for build.</summary>
	public byte Tier;

	/// <summary>Where: an order's destination or a rally point. Y is the map's, not the policy's.</summary>
	public float X;

	public float Z;

	/// <summary>What to shoot at, as an <see cref="OwnerId"/>. Only meaningful for an attack order.</summary>
	public int TargetOwnerId;

	/// <summary>Which structure, as a structure-catalog index. Only meaningful for <see cref="AgentCommandKind.Construct"/>.</summary>
	public byte Structure;

	/// <summary>Which way the structure faces [rad]. Only meaningful for <see cref="AgentCommandKind.Construct"/>.</summary>
	public float Yaw;

	/// <summary>Index into the seat's unit-id buffer, for an order.</summary>
	public int UnitsOffset;

	public int UnitsCount;
}

/// <summary>
/// A strategist seat's pending command list: the commands, and the unit ids they
/// name.
///
/// One buffer per seat, reused every decision. A policy that asks for more than
/// fits is truncated rather than refused — the APM cap (§7.3) is the thing that
/// decides how much a seat may do, and a buffer is not a second, quieter cap.
/// </summary>
public sealed class AgentCommandList
{
	/// <summary>
	/// Commands held for one decision. Comfortably past the APM cap's burst, so the
	/// ceiling that bites is the one that is documented.
	/// </summary>
	public const int MaxCommands = 64;

	/// <summary>Unit ids one decision may name in total, across every order in it.</summary>
	public const int MaxUnitIds = SimConfig.MaxUnits * 4;

	private readonly AgentCommand[] _commands = new AgentCommand[MaxCommands];
	private readonly int[] _unitIds = new int[MaxUnitIds];

	private int _count;
	private int _ids;

	public int Count => _count;

	/// <summary>Commands dropped because the list was full. A policy emitting garbage shows up rather than silently doing nothing.</summary>
	public int Overflowed { get; private set; }

	public ReadOnlySpan<AgentCommand> Commands => _commands.AsSpan(0, _count);

	/// <summary>The unit ids one order names.</summary>
	public ReadOnlySpan<int> UnitsOf(in AgentCommand command) =>
		command.UnitsCount > 0 && command.UnitsOffset >= 0
			&& command.UnitsOffset + command.UnitsCount <= _ids
			? _unitIds.AsSpan(command.UnitsOffset, command.UnitsCount)
			: ReadOnlySpan<int>.Empty;

	public void Clear()
	{
		_count = 0;
		_ids = 0;
	}

	/// <summary>Adds a command with no unit list. False when the list is full.</summary>
	public bool Add(in AgentCommand command)
	{
		if (_count >= MaxCommands)
		{
			Overflowed++;
			return false;
		}

		_commands[_count++] = command;
		return true;
	}

	/// <summary>
	/// Adds an order and copies the unit ids it names into the shared buffer. Ids
	/// past <see cref="MaxUnitIds"/> are dropped; the order still runs with the ones
	/// that fit, because half an order is closer to what was asked for than none.
	/// </summary>
	public bool AddOrder(AgentCommand command, ReadOnlySpan<int> unitIds)
	{
		command.Kind = AgentCommandKind.Order;
		return AddWithUnits(command, unitIds);
	}

	/// <summary>
	/// Adds a command that names units — an order, or a construct naming its
	/// builders — copying the ids into the shared buffer as <see cref="AddOrder"/>
	/// does. The command keeps whatever kind it was given.
	/// </summary>
	public bool AddWithUnits(AgentCommand command, ReadOnlySpan<int> unitIds)
	{
		int room = Math.Min(unitIds.Length, MaxUnitIds - _ids);
		command.UnitsOffset = _ids;
		command.UnitsCount = Math.Max(room, 0);

		for (int i = 0; i < room; i++)
		{
			_unitIds[_ids++] = unitIds[i];
		}

		return Add(command);
	}
}
