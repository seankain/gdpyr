using System;
using System.Collections.Generic;
using Godot;

namespace Gdpyr.Sim.AiDebug;

/// <summary>What a debugger reference names (docs/AI_DEBUG.md §5.1).</summary>
public enum AiEntityKind : byte
{
	None = 0,

	/// <summary>A ground-force player, by peer id: a ground bot, or a person (who has no plan to show).</summary>
	Player = 1,

	/// <summary>An RTS unit, by unit id.</summary>
	Unit = 2,

	/// <summary>One of a computer strategist's commander squads: the strategist's peer id, and the slot.</summary>
	Squad = 3,
}

/// <summary>One thing a spectator can inspect.</summary>
public readonly struct AiEntityRef : IEquatable<AiEntityRef>
{
	public AiEntityRef(AiEntityKind kind, int id, byte slot = 0)
	{
		Kind = kind;
		Id = id;
		Slot = kind == AiEntityKind.Squad ? slot : (byte)0;
	}

	public AiEntityKind Kind { get; }

	public int Id { get; }

	/// <summary>The squad slot, for <see cref="AiEntityKind.Squad"/>; 0 otherwise.</summary>
	public byte Slot { get; }

	public bool IsValid => Kind != AiEntityKind.None;

	public static AiEntityRef Player(int peerId) => new(AiEntityKind.Player, peerId);

	public static AiEntityRef Unit(ushort unitId) => new(AiEntityKind.Unit, unitId);

	public static AiEntityRef Squad(int strategistPeerId, int slot) =>
		new(AiEntityKind.Squad, strategistPeerId, (byte)Math.Clamp(slot, 0, byte.MaxValue));

	public bool Equals(AiEntityRef other) => Kind == other.Kind && Id == other.Id && Slot == other.Slot;

	public override bool Equals(object obj) => obj is AiEntityRef other && Equals(other);

	public override int GetHashCode() => HashCode.Combine(Kind, Id, Slot);

	public static bool operator ==(AiEntityRef a, AiEntityRef b) => a.Equals(b);

	public static bool operator !=(AiEntityRef a, AiEntityRef b) => !a.Equals(b);

	public override string ToString() => Kind switch
	{
		AiEntityKind.Player => BotRoster.NameOf(Id),
		AiEntityKind.Unit => $"unit {Id}",
		AiEntityKind.Squad => $"squad {Slot} of {BotRoster.NameOf(Id)}",
		_ => "nothing",
	};
}

[Flags]
public enum AiLabelFlags : byte
{
	None = 0,

	/// <summary><see cref="AiLabel.Point"/> is where the running task is taking it.</summary>
	HasPoint = 1,

	/// <summary>A unit advancing on an attack order, holding for its squad to close up (§5.2, keeping pace).</summary>
	Waiting = 2,

	/// <summary>A ground bot the coordinator has sent to look at a stale node.</summary>
	Sweeping = 4,

	/// <summary>It has a target of its own this tick.</summary>
	Engaging = 8,
}

/// <summary>
/// One agent in a line: what it is doing, and where. What the world overlay draws over
/// every planning agent when the spectator asks for all of them (docs/AI_DEBUG.md §4.3).
/// </summary>
public struct AiLabel
{
	public AiEntityRef Ref;

	/// <summary>The running task's goal: a <c>GroundGoal</c> for a player, a <c>UnitGoal</c> for a unit.</summary>
	public byte Goal;

	/// <summary>A ground bot's <c>GroundRole</c>; a unit's <c>OrderKind</c>.</summary>
	public byte Detail;

	public AiLabelFlags Flags;

	public Vector3 Point;

	/// <summary>What it is shooting at, as an owner id; 0 for nothing.</summary>
	public int TargetOwnerId;
}

/// <summary>Places an inspected agent knows about, by what they are to it (docs/AI_DEBUG.md §4.3).</summary>
public enum AiMarkerKey : byte
{
	None = 0,

	/// <summary>Where the running task is taking it.</summary>
	Intent = 1,

	/// <summary>Ground: the edge of the enemy barracks' ring straight out.</summary>
	Exit = 2,

	/// <summary>Where a retreat goes.</summary>
	Fallback = 3,

	Locker = 4,

	/// <summary>Ground: the remembered contact it would investigate.</summary>
	Ghost = 5,

	/// <summary>Ground: the nearest known bullet-proof contact.</summary>
	Armour = 6,

	/// <summary>Ground: where it stands on the node it denies.</summary>
	Zone = 7,

	/// <summary>Ground: the stale node it sweeps.</summary>
	Sweep = 8,

	Buddy = 9,

	/// <summary>Unit: its order's target point.</summary>
	Order = 10,

	/// <summary>Unit: its order's anchor.</summary>
	Anchor = 11,

	/// <summary>Unit: where the side last saw the target its attack order named.</summary>
	NamedTarget = 12,

	/// <summary>Unit: its squad's centroid.</summary>
	Squad = 13,

	/// <summary>Unit: the fight it would support or answer.</summary>
	Support = 14,

	/// <summary>Unit: its post on its squad's defended ring.</summary>
	Post = 15,

	/// <summary>The staging point of an attack.</summary>
	Staging = 16,

	/// <summary>Unit: the nearest friendly unit, for a builder under fire.</summary>
	Friend = 17,

	/// <summary>The nearest supply source's middle.</summary>
	Supply = 18,
}

/// <summary>Numbers an inspected agent knows about.</summary>
public enum AiValueKey : byte
{
	None = 0,

	/// <summary>Ground: <c>GroundRole</c>.</summary>
	Role = 1,

	/// <summary>The intent's movement mode: <c>GroundMove</c> or <c>UnitMove</c>.</summary>
	Move = 2,

	/// <summary>What it is shooting at, as an owner id.</summary>
	Target = 3,

	/// <summary>The focus target its coordinator or squad gave it, as an owner id.</summary>
	Focus = 4,

	/// <summary>Ground: locker trips given up on.</summary>
	FailedTrips = 5,

	/// <summary>Health, in percent.</summary>
	Health = 6,

	/// <summary>Unit: <c>OrderKind</c>.</summary>
	Order = 7,

	/// <summary>Unit: who gave its order, as a peer id; 0 for nobody.</summary>
	Issuer = 8,

	/// <summary>Unit: its squad's living units.</summary>
	SquadAlive = 9,

	/// <summary>Unit: 1 while an advance holds for its squad.</summary>
	Waiting = 10,

	/// <summary>Unit: resupplies given up on.</summary>
	FailedResupplies = 11,

	/// <summary>Unit: the commander squad slot it is in, -1 for none.</summary>
	CommanderSquad = 12,

	/// <summary>Unit: the computer strategist whose commander holds it, as a peer id.</summary>
	Commander = 13,

	/// <summary>Unit: 1 when it stands to fight on the way.</summary>
	StopToFight = 14,

	/// <summary>1 for a person: nothing here plans for it.</summary>
	Person = 15,

	/// <summary>Ground: <c>Team</c> as a byte.</summary>
	Team = 16,
}

public struct AiMarker
{
	public AiMarkerKey Key;
	public Vector3 Point;
}

public struct AiValue
{
	public AiValueKey Key;
	public int Value;
}

/// <summary>Everything about one inspected agent: its plan, and the places and numbers it plans with.</summary>
public sealed class AiInspect
{
	public AiEntityRef Ref;

	/// <summary>The running task's goal, as in <see cref="AiLabel.Goal"/>.</summary>
	public byte Goal;

	/// <summary>False for an agent that exists but does not plan: a person, or anything under legacy.</summary>
	public bool Planning;

	public readonly HtnTrace Trace = new();

	public readonly List<AiMarker> Markers = new();

	public readonly List<AiValue> Values = new();

	public void Add(AiMarkerKey key, Vector3 point) => Markers.Add(new AiMarker { Key = key, Point = point });

	public void Add(AiValueKey key, int value) => Values.Add(new AiValue { Key = key, Value = value });

	public bool TryValue(AiValueKey key, out int value)
	{
		foreach (AiValue v in Values)
		{
			if (v.Key == key)
			{
				value = v.Value;
				return true;
			}
		}

		value = 0;
		return false;
	}

	public bool TryMarker(AiMarkerKey key, out Vector3 point)
	{
		foreach (AiMarker m in Markers)
		{
			if (m.Key == key)
			{
				point = m.Point;
				return true;
			}
		}

		point = default;
		return false;
	}
}

[Flags]
public enum AiSquadFlags : byte
{
	None = 0,
	Ready = 1,
	Depleted = 2,
	Healable = 4,
	FallingBack = 8,

	/// <summary>An idle assault too weak for every known target: production goes to it first.</summary>
	Short = 16,

	/// <summary>A member is in a fight.</summary>
	Engaged = 32,
}

/// <summary>One commander squad, as the last decision left it (docs/HTN_BOTS.md §5.3).</summary>
public sealed class AiSquad
{
	public byte Slot;

	/// <summary><c>CommandRole</c>.</summary>
	public byte Role;

	/// <summary><c>CommandTask</c>: what the assignment gave it.</summary>
	public byte Task;

	/// <summary><c>CommandTarget</c>: what its attack is aimed at.</summary>
	public byte Target;

	/// <summary><c>CommandGoal</c>: what its running task asks for.</summary>
	public byte Goal;

	/// <summary><c>OrderKind</c> of the order its members are under.</summary>
	public byte Order;

	/// <summary><c>SquadPhase</c> the goal wants on the board.</summary>
	public byte Phase;

	public AiSquadFlags Flags;

	/// <summary>The board zone its task is at; -1 for none.</summary>
	public int Zone = -1;

	public int TargetOwnerId;

	public float Strength;

	public float FullStrength;

	public float StrengthAtFormation;

	public Vector3 Centroid;

	public Vector3 TaskPoint;

	public Vector3 StagingPoint;

	public Vector3 FallbackPoint;

	/// <summary>Where the order its members are under sends them.</summary>
	public Vector3 OrderPoint;

	public readonly List<ushort> Members = new();

	public readonly HtnTrace Trace = new();
}

/// <summary>One computer strategist's commander (docs/HTN_BOTS.md §5.3).</summary>
public sealed class AiCommander
{
	public int PeerId;

	/// <summary>The zone production is being sent to because no squad was strong enough for it; -1 for none.</summary>
	public int BuyTimeZone = -1;

	public bool ReconWanted;

	/// <summary>Active squads only.</summary>
	public readonly List<AiSquad> Squads = new();

	public AiSquad Squad(int slot)
	{
		foreach (AiSquad squad in Squads)
		{
			if (squad.Slot == slot)
			{
				return squad;
			}
		}

		return null;
	}
}

/// <summary>
/// What a spectator is sent about the bots, ten times a second (docs/AI_DEBUG.md §5):
/// a label per planning agent if asked for, each computer strategist's commander if
/// asked for, and the full plan of each agent the spectator has selected.
/// </summary>
public sealed class AiDebugFrame
{
	public uint Tick;

	public BotAi BotAi;

	/// <summary>The server's map signatures (<see cref="HtnDomainMap.Signature"/>), 0 for a domain nobody plans with.</summary>
	public uint GroundSignature;

	public uint UnitSignature;

	public uint SquadSignature;

	public readonly List<AiLabel> Labels = new();

	public readonly List<AiCommander> Commanders = new();

	public readonly List<AiInspect> Inspected = new();

	public uint SignatureOf(HtnDomainKind kind) => kind switch
	{
		HtnDomainKind.Ground => GroundSignature,
		HtnDomainKind.Unit => UnitSignature,
		HtnDomainKind.Squad => SquadSignature,
		_ => 0u,
	};

	/// <summary>
	/// The map to read a trace of <paramref name="kind"/> with: this process's own build
	/// of the domain, provided it numbers the domain the way the server does. Null when it
	/// does not — the views say so rather than drawing the wrong tasks.
	/// </summary>
	public HtnDomainMap MapFor(HtnDomainKind kind)
	{
		HtnDomainMap map = HtnDomains.For(kind);
		return map != null && map.Signature == SignatureOf(kind) ? map : null;
	}

	public AiInspect Find(AiEntityRef target)
	{
		foreach (AiInspect inspect in Inspected)
		{
			if (inspect.Ref == target)
			{
				return inspect;
			}
		}

		return null;
	}

	public bool TryLabel(AiEntityRef target, out AiLabel label)
	{
		foreach (AiLabel l in Labels)
		{
			if (l.Ref == target)
			{
				label = l;
				return true;
			}
		}

		label = default;
		return false;
	}

	public AiCommander Commander(int peerId)
	{
		foreach (AiCommander commander in Commanders)
		{
			if (commander.PeerId == peerId)
			{
				return commander;
			}
		}

		return null;
	}
}

[Flags]
public enum AiWatchFlags : byte
{
	None = 0,

	/// <summary>A label for every planning ground bot and unit.</summary>
	Labels = 1,

	/// <summary>The computer strategists' commanders.</summary>
	Commander = 2,
}

/// <summary>What a spectator wants to be sent (docs/AI_DEBUG.md §5.2). Sent when it changes.</summary>
public sealed class AiWatch
{
	/// <summary>Agents inspected at once. Each costs a full trace in every frame.</summary>
	public const int MaxSelected = 4;

	public AiWatchFlags Flags;

	/// <summary>One computer strategist's commander, by peer id; 0 for every one.</summary>
	public int CommanderPeerId;

	public readonly List<AiEntityRef> Selected = new();

	public bool IsEmpty => Flags == AiWatchFlags.None && Selected.Count == 0;

	public bool Wants(AiWatchFlags flag) => (Flags & flag) != 0;

	public void CopyFrom(AiWatch other)
	{
		Flags = other?.Flags ?? AiWatchFlags.None;
		CommanderPeerId = other?.CommanderPeerId ?? 0;
		Selected.Clear();
		if (other != null)
		{
			for (int i = 0; i < other.Selected.Count && Selected.Count < MaxSelected; i++)
			{
				Selected.Add(other.Selected[i]);
			}
		}
	}

	public bool SameAs(AiWatch other)
	{
		if (other == null || Flags != other.Flags || CommanderPeerId != other.CommanderPeerId
			|| Selected.Count != other.Selected.Count)
		{
			return false;
		}

		for (int i = 0; i < Selected.Count; i++)
		{
			if (Selected[i] != other.Selected[i])
			{
				return false;
			}
		}

		return true;
	}
}
