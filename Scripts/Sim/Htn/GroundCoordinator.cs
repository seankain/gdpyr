using System;
using Godot;

namespace Gdpyr.Sim.Htn;

/// <summary>One ground-force player the coordinator counts: a bot, or a person it never gives a role to.</summary>
public struct GroundMember
{
	/// <summary>A peer id; never 0.</summary>
	public int Id;

	/// <summary>A bot's roster slot, 0 to the coordinator's capacity; -1 for a person.</summary>
	public int Slot;

	public bool Alive;

	public Vector3 Position;

	/// <summary>A launcher in hand.</summary>
	public bool Explosive;

	/// <summary>How far this one's own eyes reach, for choosing a focus target it can see.</summary>
	public float SensorRadiusMeters;

	/// <summary>The bot's <see cref="GroundContext.FailedLockerTrips"/>; 0 for a person.</summary>
	public int FailedLockerTrips;

	public bool IsBot => Slot >= 0;
}

/// <summary>One resource node, as the coordinator plans around it.</summary>
public struct GroundNode
{
	public Vector3 Position;

	public float RadiusMeters;

	public NodeHolder Holder;
}

/// <summary>What the coordinator hands one bot (docs/HTN_BOTS.md §5.1).</summary>
public struct GroundOrders
{
	public GroundRole Role;

	/// <summary>The node a denier holds, by its index in the economy's list; -1 for none.</summary>
	public int Zone;

	/// <summary>Where on that node it stands.</summary>
	public Vector3 ZonePoint;

	/// <summary>The stale node this bot sweeps, or -1.</summary>
	public int SweepZone;

	public Vector3 SweepPoint;

	/// <summary>The contact this bot's cluster focuses on; 0 for none.</summary>
	public int FocusOwnerId;

	/// <summary>The teammate this bot advances with: a peer id, 0 for none.</summary>
	public int BuddyId;

	public Vector3 BuddyPosition;
}

/// <summary>The coordinator's knobs (§9: every threshold is one).</summary>
public readonly struct GroundCoordinatorTraits
{
	/// <summary>At most one ground player in this many is a denier: a third of the team.</summary>
	public readonly int DenierShare;

	/// <summary>A locker runner is wanted while fewer than this many launchers are in hand.</summary>
	public readonly int WantedExplosives;

	/// <summary>How long a bot whose trip to the locker failed is left off the job.</summary>
	public readonly int LockerRetryTicks;

	/// <summary>How near a ground player has to come to a node for the team to have seen it. Range only, no ray.</summary>
	public readonly float SweepSightMeters;

	/// <summary>How long a node may go unseen before a bot is sent to look.</summary>
	public readonly int SweepStaleTicks;

	/// <summary>Where on a node a denier stands, as a fraction of its capture radius from the centre.</summary>
	public readonly float HoldOffsetFraction;

	public GroundCoordinatorTraits(int denierShare, int wantedExplosives, int lockerRetryTicks, float sweepSightMeters,
		int sweepStaleTicks, float holdOffsetFraction)
	{
		DenierShare = Math.Max(denierShare, 1);
		WantedExplosives = Math.Max(wantedExplosives, 0);
		LockerRetryTicks = Math.Max(lockerRetryTicks, 0);
		SweepSightMeters = MathF.Max(sweepSightMeters, 0f);
		SweepStaleTicks = Math.Max(sweepStaleTicks, 0);
		HoldOffsetFraction = Math.Clamp(holdOffsetFraction, 0f, 1f);
	}

	public static GroundCoordinatorTraits Default => new(
		denierShare: 3,
		wantedExplosives: 2,
		lockerRetryTicks: SimConfig.TickRate * 30,
		sweepSightMeters: 40f,
		sweepStaleTicks: SimConfig.TickRate * 30,
		holdOffsetFraction: 0.4f);
}

/// <summary>
/// The ground force's team layer (docs/HTN_BOTS.md §4.1, §5.1): every
/// <see cref="IntervalTicks"/>, who denies which node, who fetches a launcher, who
/// looks at the node nobody has seen, which contact each bot focuses on, and who
/// advances with whom. A plain function rather than a planner: it assigns, it does
/// not sequence, and each bot's own domain does the rest.
///
/// People are counted — for the size of the team, for launchers in hand, as a
/// buddy, as eyes on a node — and never given a role. Assignments are sticky: a
/// bot keeps its node, its locker run or its sweep while it is still a right one
/// to have it, so roles do not reshuffle every half second.
///
/// Engine-free, deterministic (ties go to the lower index) and allocation-free
/// after construction.
/// </summary>
public sealed class GroundCoordinator
{
	/// <summary>Half a second, the strategist's decision interval (§4.1).</summary>
	public const int IntervalTicks = 30;

	/// <summary>Distinguishes the hold point's use of <see cref="Spread.Seed"/> from every other one.</summary>
	private const byte HoldSeedSalt = SimConfig.MaxWeaponDefinitions + 4;

	/// <summary>Half the arc of a node's spawn-facing side that hold points are spread over.</summary>
	public const float HoldArcRadians = MathF.PI / 3f;

	private readonly GroundCoordinatorTraits _traits;

	// Per bot slot: what it was given last time, and the locker bookkeeping.
	private readonly int[] _zoneOf;
	private readonly GroundRole[] _roleOf;
	private readonly int[] _sweepOf;
	private readonly int[] _failuresSeen;
	private readonly uint[] _barredUntil;

	// Scratch for one assignment, per member.
	private readonly int[] _buddyOf;

	// Scratch per node.
	private readonly int[] _candidates;

	private readonly ZoneBoard _zones = new(SimConfig.MaxResourceNodes);

	/// <param name="capacity">Bot roster slots, and members per call: the roster's size.</param>
	public GroundCoordinator(int capacity, in GroundCoordinatorTraits traits)
	{
		capacity = Math.Max(capacity, 1);
		_traits = traits;
		_zoneOf = new int[capacity];
		_roleOf = new GroundRole[capacity];
		_sweepOf = new int[capacity];
		_failuresSeen = new int[capacity];
		_barredUntil = new uint[capacity];
		_buddyOf = new int[capacity];
		_candidates = new int[SimConfig.MaxResourceNodes];
		Array.Fill(_zoneOf, -1);
		Array.Fill(_sweepOf, -1);
	}

	/// <summary>Members one assignment takes, bots and people together, and the highest bot slot plus one.</summary>
	public int Capacity => _buddyOf.Length;

	/// <summary>What the team has seen of the nodes. For the debug HUD and the tests.</summary>
	public ZoneBoard Zones => _zones;

	/// <summary>
	/// One assignment. <paramref name="orders"/> is written for every member,
	/// people included, and only a bot's is meant to be applied. Members past
	/// <see cref="Capacity"/>, and bots whose slot is past it, get no role.
	/// </summary>
	/// <param name="contacts">The team's memory; null for a team that remembers nothing.</param>
	/// <param name="spawn">Where the ground force comes in: deniers go to the nodes nearest it first.</param>
	/// <param name="locker">The weapon locker a runner is sent to, when <paramref name="hasLocker"/>.</param>
	public void Assign(uint tick, ReadOnlySpan<GroundMember> members, ReadOnlySpan<GroundNode> nodes,
		ContactMemory contacts, Vector3 spawn, bool hasLocker, Vector3 locker, Span<GroundOrders> orders)
	{
		int count = Math.Min(Math.Min(members.Length, orders.Length), Capacity);
		members = members[..count];
		nodes = nodes[..Math.Min(nodes.Length, _candidates.Length)];

		for (int i = 0; i < count; i++)
		{
			orders[i] = new GroundOrders { Role = GroundRole.Assault, Zone = -1, SweepZone = -1 };
			_buddyOf[i] = -1;
		}

		Observe(tick, members, nodes);
		NoteLockerFailures(tick, members);

		AssignDeniers(members, nodes, spawn, orders);
		AssignRunner(tick, members, contacts, hasLocker, locker, orders);
		AssignSweep(tick, members, nodes, orders);
		AssignFocus(tick, members, contacts, orders);
		AssignBuddies(members, orders);

		for (int i = 0; i < count; i++)
		{
			if (Roled(members[i]))
			{
				int slot = members[i].Slot;
				_roleOf[slot] = orders[i].Role;
				_zoneOf[slot] = orders[i].Zone;
				_sweepOf[slot] = orders[i].SweepZone;
			}
		}
	}

	/// <summary>
	/// Where a bot stands on a node: off the centre by a fraction of the radius, on
	/// the side facing <paramref name="spawn"/>, swung by a per-bot angle within
	/// <see cref="HoldArcRadians"/>. The ground force's side of a node is the side away
	/// from the strategist's barracks, whose defended ring can reach over the far half
	/// of a node — and a bot is never sent inside that ring (BotPilot.OutsideDefences).
	/// </summary>
	public Vector3 HoldPoint(in GroundNode node, int botId, Vector3 spawn)
	{
		Vector3 toward = spawn - node.Position;
		float facing = toward.X * toward.X + toward.Z * toward.Z > 0.0001f ? MathF.Atan2(toward.Z, toward.X) : 0f;
		float swing = (((Spread.Seed(botId, HoldSeedSalt, 0u) >> 8) / 16777216f) * 2f) - 1f;
		float angle = facing + (swing * HoldArcRadians);
		float reach = node.RadiusMeters * _traits.HoldOffsetFraction;
		return node.Position + new Vector3(MathF.Cos(angle), 0f, MathF.Sin(angle)) * reach;
	}

	/// <summary>Forgets every assignment, between rounds.</summary>
	public void Reset()
	{
		Array.Fill(_zoneOf, -1);
		Array.Fill(_sweepOf, -1);
		Array.Clear(_roleOf);
		Array.Clear(_failuresSeen);
		Array.Clear(_barredUntil);
		_zones.Clear();
	}

	// ---- nodes -------------------------------------------------------------

	/// <summary>Keeps the board's layout in step with the nodes, and marks the ones a ground player is near.</summary>
	private void Observe(uint tick, ReadOnlySpan<GroundMember> members, ReadOnlySpan<GroundNode> nodes)
	{
		bool same = _zones.Count == nodes.Length;
		for (int z = 0; same && z < nodes.Length; z++)
		{
			same = _zones.At(z).Position == nodes[z].Position;
		}

		if (!same)
		{
			_zones.Clear();
			for (int z = 0; z < nodes.Length; z++)
			{
				_zones.Add(ZoneKind.ResourceNode, z, nodes[z].Position, nodes[z].RadiusMeters);
			}
		}

		float sight = _traits.SweepSightMeters * _traits.SweepSightMeters;
		for (int z = 0; z < nodes.Length; z++)
		{
			_zones.SetHolder(z, nodes[z].Holder, false);
			for (int i = 0; i < members.Length; i++)
			{
				if (members[i].Alive && FlatSquared(members[i].Position, nodes[z].Position) <= sight)
				{
					_zones.MarkObserved(z, tick);
					break;
				}
			}
		}
	}

	/// <summary>
	/// One denier per node the strategist holds, nearest the spawn first — then the
	/// neutral ones, which it is on its way to take — up to a third of the team,
	/// people counted. A denier keeps its node while the node still wants one.
	/// </summary>
	private void AssignDeniers(ReadOnlySpan<GroundMember> members, ReadOnlySpan<GroundNode> nodes, Vector3 spawn,
		Span<GroundOrders> orders)
	{
		int wanted = members.Length / _traits.DenierShare;
		int candidates = 0;
		for (int z = 0; z < nodes.Length; z++)
		{
			if (nodes[z].Holder != NodeHolder.GroundForce)
			{
				_candidates[candidates++] = z;
			}
		}

		// Insertion sort, strategist-held first, then nearest the spawn, then index.
		for (int a = 1; a < candidates; a++)
		{
			int node = _candidates[a];
			int b = a - 1;
			while (b >= 0 && DenyBefore(nodes, spawn, node, _candidates[b]))
			{
				_candidates[b + 1] = _candidates[b];
				b--;
			}

			_candidates[b + 1] = node;
		}

		int assigned = 0;
		for (int c = 0; c < candidates && assigned < wanted; c++)
		{
			int zone = _candidates[c];
			int pick = -1;

			// Whoever had it last time keeps it.
			for (int i = 0; i < members.Length && pick < 0; i++)
			{
				if (Free(members[i], orders[i]) && _roleOf[members[i].Slot] == GroundRole.Denier
					&& _zoneOf[members[i].Slot] == zone)
				{
					pick = i;
				}
			}

			if (pick < 0)
			{
				pick = NearestFree(members, orders, nodes[zone].Position);
			}

			if (pick < 0)
			{
				break;
			}

			orders[pick].Role = GroundRole.Denier;
			orders[pick].Zone = zone;
			orders[pick].ZonePoint = HoldPoint(nodes[zone], members[pick].Id, spawn);
			assigned++;
		}
	}

	private static bool DenyBefore(ReadOnlySpan<GroundNode> nodes, Vector3 spawn, int a, int b)
	{
		bool heldA = nodes[a].Holder == NodeHolder.Strategist;
		bool heldB = nodes[b].Holder == NodeHolder.Strategist;
		if (heldA != heldB)
		{
			return heldA;
		}

		float da = FlatSquared(nodes[a].Position, spawn);
		float db = FlatSquared(nodes[b].Position, spawn);
		return da < db || (da == db && a < b);
	}

	// ---- the locker --------------------------------------------------------

	/// <summary>A trip that produced no launcher takes the bot off the job for a while (§3.4, P4's give-up fact).</summary>
	private void NoteLockerFailures(uint tick, ReadOnlySpan<GroundMember> members)
	{
		for (int i = 0; i < members.Length; i++)
		{
			if (!Roled(members[i]))
			{
				continue;
			}

			int slot = members[i].Slot;
			if (members[i].FailedLockerTrips > _failuresSeen[slot])
			{
				_barredUntil[slot] = tick + (uint)_traits.LockerRetryTicks;
			}

			_failuresSeen[slot] = members[i].FailedLockerTrips;
		}
	}

	/// <summary>
	/// At most one runner, while the team knows of armour and fewer than
	/// <see cref="GroundCoordinatorTraits.WantedExplosives"/> launchers are in hand,
	/// people's included: the living bot with small arms nearest the locker, or
	/// whoever was already running.
	/// </summary>
	private void AssignRunner(uint tick, ReadOnlySpan<GroundMember> members, ContactMemory contacts, bool hasLocker,
		Vector3 locker, Span<GroundOrders> orders)
	{
		if (!hasLocker || !ArmourKnown(contacts, tick))
		{
			return;
		}

		int explosives = 0;
		for (int i = 0; i < members.Length; i++)
		{
			if (members[i].Alive && members[i].Explosive)
			{
				explosives++;
			}
		}

		if (explosives >= _traits.WantedExplosives)
		{
			return;
		}

		int pick = -1;
		for (int i = 0; i < members.Length && pick < 0; i++)
		{
			if (Runnable(tick, members[i], orders[i]) && _roleOf[members[i].Slot] == GroundRole.LockerRunner)
			{
				pick = i;
			}
		}

		if (pick < 0)
		{
			float best = float.MaxValue;
			for (int i = 0; i < members.Length; i++)
			{
				float d = FlatSquared(members[i].Position, locker);
				if (Runnable(tick, members[i], orders[i]) && d < best)
				{
					best = d;
					pick = i;
				}
			}
		}

		if (pick >= 0)
		{
			orders[pick].Role = GroundRole.LockerRunner;
		}
	}

	private bool Runnable(uint tick, in GroundMember member, in GroundOrders order) =>
		Free(member, order) && !member.Explosive && tick >= _barredUntil[member.Slot];

	private static bool ArmourKnown(ContactMemory contacts, uint tick)
	{
		for (int i = 0; contacts != null && i < contacts.Count; i++)
		{
			if (contacts.At(i).BulletProof && contacts.IsRemembered(i, tick))
			{
				return true;
			}
		}

		return false;
	}

	// ---- recon -------------------------------------------------------------

	/// <summary>
	/// One sweeper, to the node the team has gone longest without seeing, once that
	/// is <see cref="GroundCoordinatorTraits.SweepStaleTicks"/>: the bot already on the
	/// way, else the nearest assault bot.
	/// </summary>
	private void AssignSweep(uint tick, ReadOnlySpan<GroundMember> members, ReadOnlySpan<GroundNode> nodes,
		Span<GroundOrders> orders)
	{
		int zone = _zones.Stalest(ZoneKind.ResourceNode, tick);
		if (zone < 0 || zone >= nodes.Length || _zones.TicksSinceObserved(zone, tick) < (uint)_traits.SweepStaleTicks)
		{
			return;
		}

		int pick = -1;
		for (int i = 0; i < members.Length && pick < 0; i++)
		{
			if (Free(members[i], orders[i]) && _sweepOf[members[i].Slot] == zone)
			{
				pick = i;
			}
		}

		if (pick < 0)
		{
			pick = NearestFree(members, orders, nodes[zone].Position);
		}

		if (pick >= 0)
		{
			orders[pick].SweepZone = zone;
			orders[pick].SweepPoint = nodes[zone].Position;
		}
	}

	// ---- attack ------------------------------------------------------------

	/// <summary>
	/// For each living bot, the live contact it could see and hurt that has the least
	/// health left, then the nearest, then the lowest id. Bots in one place see one set
	/// of contacts and so share a focus: a cluster, without clustering.
	/// </summary>
	private static void AssignFocus(uint tick, ReadOnlySpan<GroundMember> members, ContactMemory contacts,
		Span<GroundOrders> orders)
	{
		for (int i = 0; contacts != null && i < members.Length; i++)
		{
			if (!members[i].IsBot || !members[i].Alive)
			{
				continue;
			}

			float reach = members[i].SensorRadiusMeters * members[i].SensorRadiusMeters;
			int best = 0;
			float bestHealth = float.MaxValue;
			float bestDistance = float.MaxValue;

			for (int c = 0; c < contacts.Count; c++)
			{
				KnownContact contact = contacts.At(c);
				if (!contacts.IsLive(c, tick) || (contact.BulletProof && !members[i].Explosive))
				{
					continue;
				}

				float distance = members[i].Position.DistanceSquaredTo(contact.Position);
				if (distance > reach)
				{
					continue;
				}

				float health = contact.HealthFraction;
				if (health < bestHealth
					|| (health == bestHealth && (distance < bestDistance
						|| (distance == bestDistance && contact.OwnerId < best))))
				{
					best = contact.OwnerId;
					bestHealth = health;
					bestDistance = distance;
				}
			}

			orders[i].FocusOwnerId = best;
		}
	}

	/// <summary>
	/// Buddy pairs by proximity, people included: the closest two unpaired living
	/// members, then the next closest two, until one or none is left.
	/// </summary>
	private void AssignBuddies(ReadOnlySpan<GroundMember> members, Span<GroundOrders> orders)
	{
		while (true)
		{
			int a = -1;
			int b = -1;
			float best = float.MaxValue;

			for (int i = 0; i < members.Length; i++)
			{
				if (!members[i].Alive || _buddyOf[i] >= 0)
				{
					continue;
				}

				for (int j = i + 1; j < members.Length; j++)
				{
					if (!members[j].Alive || _buddyOf[j] >= 0)
					{
						continue;
					}

					float d = FlatSquared(members[i].Position, members[j].Position);
					if (d < best)
					{
						best = d;
						a = i;
						b = j;
					}
				}
			}

			if (a < 0)
			{
				break;
			}

			_buddyOf[a] = b;
			_buddyOf[b] = a;
			orders[a].BuddyId = members[b].Id;
			orders[a].BuddyPosition = members[b].Position;
			orders[b].BuddyId = members[a].Id;
			orders[b].BuddyPosition = members[a].Position;
		}
	}

	// ---- helpers -----------------------------------------------------------

	/// <summary>A living bot this coordinator can give a role to, that has none yet this assignment.</summary>
	private bool Free(in GroundMember member, in GroundOrders order) =>
		Roled(member) && member.Alive && order.Role == GroundRole.Assault && order.SweepZone < 0;

	private bool Roled(in GroundMember member) => member.IsBot && member.Slot < _roleOf.Length;

	private int NearestFree(ReadOnlySpan<GroundMember> members, Span<GroundOrders> orders, Vector3 to)
	{
		int pick = -1;
		float best = float.MaxValue;
		for (int i = 0; i < members.Length; i++)
		{
			if (!Free(members[i], orders[i]))
			{
				continue;
			}

			float d = FlatSquared(members[i].Position, to);
			if (d < best)
			{
				best = d;
				pick = i;
			}
		}

		return pick;
	}

	private static float FlatSquared(Vector3 a, Vector3 b)
	{
		Vector3 d = a - b;
		d.Y = 0f;
		return d.LengthSquared();
	}
}
