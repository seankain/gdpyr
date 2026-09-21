using System;
using System.Collections.Generic;

namespace Gdpyr.AgentClient;

/// <summary>
/// The discrete action heads a strategist policy learns, and how they become the
/// command list the server applies (docs/AGENT_API.md §7.4).
///
/// **This is a research decision written down, not a fact about gdpyr.** The
/// game's strategist action is an unbounded list of commands naming arbitrary
/// unit ids and arbitrary points — a space no discrete agent can address
/// directly. Something has to choose which slice of it a policy may reach, and
/// M7 deliberately shipped the protocol without choosing (docs/TRAINING.md §7).
/// This file is that choice, made in one place so you can disagree with it in one
/// place, exactly as <see cref="GroundReward"/> is for the reward.
///
/// Six heads of eight, and at most two commands per decision:
///
/// | Head | 0 | 1 | 2 | 3 | 4 | 5 | 6 | 7 |
/// |---|---|---|---|---|---|---|---|---|
/// | production | none | build infantry | build technical | build tank | cancel | rally to cell | rally to node | rally home |
/// | barracks | which door the production head addresses, modulo the ones this seat owns |
/// | group | none | all | idle | near cell | far from cell | damaged | armor | infantry |
/// | order | none | move | attack | patrol | defend | stop | attack nearest contact | defend nearest node |
/// | target_x | the column of an 8×8 grid over the map |
/// | target_z | the row |
///
/// Three things that are choices rather than plumbing:
///
/// - **Two commands a decision, not sixty-four.** A strategist seat at
///   <c>step_mul</c> 30 decides twice a second, and the APM cap is eight commands
///   a second (§7.3), so two leaves headroom and keeps one decision legible in a
///   trace. A policy that wants to move three groups separately does it over
///   three decisions, which is what a person does anyway.
/// - **Units are addressed by predicate, not by id.** Learning "unit 47" is
///   learning a number that means something different next round. The groups
///   below are the categories a person plays with — everything, the idle ones,
///   the ones near where I am pointing, the hurt ones, the armor.
/// - **The grid is fitted to the map, not to the encoder's half-extent.** The
///   observation normalizes positions against 256 m (§6.2) and a greybox map is
///   far smaller than that, so an 8×8 grid over the whole normalized square would
///   spend most of its cells off the navmesh. It is fitted to the bounding box of
///   the resource nodes and this seat's barracks instead — map geometry, always
///   visible, and it does not move during a round.
/// </summary>
public sealed class StrategistActionSpace
{
	/// <summary>Every head is this wide. RLMatrix refuses a ragged action space.</summary>
	public const int HeadWidth = 8;

	/// <summary>The target grid is the head width square: one head for the column, one for the row.</summary>
	public const int GridSize = HeadWidth;

	/// <summary>The heads, in order. This is what <c>actionSize</c> hands RLMatrix.</summary>
	public static readonly int[] Heads =
	{
		HeadWidth, HeadWidth, HeadWidth, HeadWidth, HeadWidth, HeadWidth,
	};

	/// <summary>Human-readable names, for a trace and for the log.</summary>
	public static readonly string[] HeadNames =
	{
		"production", "barracks", "group", "order", "target_x", "target_z",
	};

	public static readonly string[] ProductionNames =
	{
		"none", "build_infantry", "build_technical", "build_tank", "cancel",
		"rally_cell", "rally_node", "rally_home",
	};

	public static readonly string[] GroupNames =
	{
		"none", "all", "idle", "near_cell", "far_cell", "damaged", "armor", "infantry",
	};

	public static readonly string[] OrderNames =
	{
		"none", "move", "attack", "patrol", "defend", "stop", "attack_contact", "defend_node",
	};

	/// <summary>Order names as the control plane spells them, by head index.</summary>
	private static readonly string[] OrderKinds =
	{
		null, "move", "attack", "patrol", "defend", "stop", "attack", "defend",
	};

	/// <summary>A grid narrower than this is a map the client failed to find. Metres.</summary>
	private const float MinimumSpanMeters = 32f;

	/// <summary>How far past the outermost node or barracks the grid reaches.</summary>
	private const float GridPadding = 0.15f;

	private readonly GdpyrSchema _schema;
	private readonly GdpyrBlock _barracks;
	private readonly GdpyrBlock _nodes;
	private readonly GdpyrBlock _contacts;

	private readonly List<int> _units = new();
	private readonly List<int> _doors = new();

	public StrategistActionSpace(GdpyrSchema schema)
	{
		_schema = schema ?? throw new ArgumentNullException(nameof(schema));
		_barracks = schema.Block("barracks");
		_nodes = schema.Block("nodes");
		_contacts = schema.Block("contacts");
	}

	/// <summary>Commands one decision may produce: one production, one order.</summary>
	public const int MaxCommandsPerDecision = 2;

	/// <summary>
	/// Turns one head vector into the commands the server is sent.
	///
	/// <paramref name="observation"/> is this seat's strategist vector and
	/// <paramref name="army"/> is what <c>list_units</c> last returned — the ids
	/// the observation does not carry (docs/AGENT_API.md §7.5). An empty result is
	/// a decision to do nothing, which is a thing a strategist does.
	/// </summary>
	public void Decode(int[] heads, float[] observation, IReadOnlyList<GdpyrUnit> army,
		List<GdpyrCommand> into)
	{
		if (into == null)
		{
			return;
		}

		into.Clear();
		if (heads == null || heads.Length < Heads.Length)
		{
			return;
		}

		Bounds(observation, out float minX, out float minZ, out float maxX, out float maxZ);
		float cellX = minX + ((Clamp(heads[4]) + 0.5f) * (maxX - minX) / GridSize);
		float cellZ = minZ + ((Clamp(heads[5]) + 0.5f) * (maxZ - minZ) / GridSize);

		Production(Clamp(heads[0]), Clamp(heads[1]), observation, cellX, cellZ, into);
		Order(Clamp(heads[2]), Clamp(heads[3]), observation, army, cellX, cellZ, into);
	}

	// ---- production --------------------------------------------------------

	private void Production(int action, int door, float[] observation, float cellX, float cellZ,
		List<GdpyrCommand> into)
	{
		if (action == 0 || _barracks == null)
		{
			return;
		}

		// The slots this seat actually owns. A barracks it does not own keeps its
		// index and is left zeroed (docs/AGENT_API.md §6.2), and addressing one is a
		// command the server refuses and counts — so the head is folded onto the
		// owned ones rather than spending half its range on refusals.
		Owned(observation, _doors);
		if (_doors.Count == 0)
		{
			return;
		}

		int index = _doors[door % _doors.Count];

		switch (action)
		{
			case 1:
			case 2:
			case 3:
				into.Add(GdpyrCommand.Build(index, action - 1));
				return;

			case 4:
				into.Add(GdpyrCommand.Cancel(index));
				return;

			case 5:
				into.Add(GdpyrCommand.Rally(index, cellX, cellZ));
				return;

			case 6:
			{
				// Fresh units walk to a node this side is holding, which is what keeps
				// income alive without the policy having to order every rifleman.
				float doorX = Meters(_barracks.Value(observation, index, "x"));
				float doorZ = Meters(_barracks.Value(observation, index, "z"));
				if (NearestNode(observation, doorX, doorZ, ownedOnly: true, out float nodeX, out float nodeZ)
					|| NearestNode(observation, doorX, doorZ, ownedOnly: false, out nodeX, out nodeZ))
				{
					into.Add(GdpyrCommand.Rally(index, nodeX, nodeZ));
					return;
				}

				into.Add(GdpyrCommand.Rally(index, cellX, cellZ));
				return;
			}

			default:
			{
				// Rally home: the door itself, which is the defensive posture the
				// scripted strategist keeps four units in (docs/NETCODE.md §9).
				float doorX = Meters(_barracks.Value(observation, index, "x"));
				float doorZ = Meters(_barracks.Value(observation, index, "z"));
				into.Add(GdpyrCommand.Rally(index, doorX, doorZ));
				return;
			}
		}
	}

	// ---- orders ------------------------------------------------------------

	private void Order(int group, int order, float[] observation, IReadOnlyList<GdpyrUnit> army,
		float cellX, float cellZ, List<GdpyrCommand> into)
	{
		if (group == 0 || order == 0 || army == null || army.Count == 0)
		{
			return;
		}

		float targetX = cellX;
		float targetZ = cellZ;

		// "The nearest contact" and "the nearest node" are nearest to the army, not
		// to the origin: a strategist points at what its units can reach.
		Centroid(army, out float armyX, out float armyZ);

		if (order == 6 && NearestContact(observation, armyX, armyZ, out float contactX, out float contactZ))
		{
			targetX = contactX;
			targetZ = contactZ;
		}
		else if (order == 7
			&& (NearestNode(observation, armyX, armyZ, ownedOnly: true, out float nodeX, out float nodeZ)
				|| NearestNode(observation, armyX, armyZ, ownedOnly: false, out nodeX, out nodeZ)))
		{
			targetX = nodeX;
			targetZ = nodeZ;
		}

		Select(group, army, targetX, targetZ, _units);
		if (_units.Count == 0)
		{
			return;
		}

		into.Add(GdpyrCommand.Order(OrderKinds[order], _units.ToArray(), targetX, targetZ));
	}

	/// <summary>
	/// Which units an order names. Predicates rather than ids, because an id is a
	/// number that means something else next round.
	/// </summary>
	private static void Select(int group, IReadOnlyList<GdpyrUnit> army, float targetX, float targetZ,
		List<int> into)
	{
		into.Clear();

		for (int i = 0; i < army.Count; i++)
		{
			GdpyrUnit unit = army[i];
			if (unit == null || !unit.Alive)
			{
				continue;
			}

			bool take = group switch
			{
				1 => true,

				// OrderKind.None: standing where it was left, which is the thing a
				// person looks for when they want somebody to send.
				2 => unit.Order == 0,
				5 => unit.Health < 0.5f,
				6 => unit.Tier >= 1,
				7 => unit.Tier == 0,
				_ => true,
			};

			if (take)
			{
				into.Add(i);
			}
		}

		if (group != 3 && group != 4)
		{
			Ids(army, into);
			return;
		}

		// The nearest or farthest third, which is how a person splits a force
		// without drawing a box: the ones already down there, and the ones still
		// back here. A third of one unit is one unit.
		into.Sort((left, right) =>
		{
			float a = DistanceSquared(army[left], targetX, targetZ);
			float b = DistanceSquared(army[right], targetX, targetZ);
			return group == 3 ? a.CompareTo(b) : b.CompareTo(a);
		});

		int keep = Math.Max(1, (into.Count + 2) / 3);
		if (into.Count > keep)
		{
			into.RemoveRange(keep, into.Count - keep);
		}

		Ids(army, into);
	}

	/// <summary>Turns the indices a predicate selected into the ids a command names.</summary>
	private static void Ids(IReadOnlyList<GdpyrUnit> army, List<int> indices)
	{
		for (int i = 0; i < indices.Count; i++)
		{
			indices[i] = army[indices[i]].Id;
		}
	}

	private static float DistanceSquared(GdpyrUnit unit, float x, float z)
	{
		float dx = unit.X - x;
		float dz = unit.Z - z;
		return (dx * dx) + (dz * dz);
	}

	private static void Centroid(IReadOnlyList<GdpyrUnit> army, out float x, out float z)
	{
		x = 0f;
		z = 0f;
		int alive = 0;

		for (int i = 0; i < army.Count; i++)
		{
			if (army[i] == null || !army[i].Alive)
			{
				continue;
			}

			x += army[i].X;
			z += army[i].Z;
			alive++;
		}

		if (alive > 0)
		{
			x /= alive;
			z /= alive;
		}
	}

	// ---- the map -----------------------------------------------------------

	/// <summary>
	/// The grid's bounding box, in metres: every resource node plus this seat's
	/// barracks, padded. Both are map geometry and neither moves during a round, so
	/// the grid a policy learned in one episode means the same thing in the next.
	/// </summary>
	private void Bounds(float[] observation, out float minX, out float minZ, out float maxX, out float maxZ)
	{
		float scale = _schema.PositionScaleMeters;
		minX = float.MaxValue;
		minZ = float.MaxValue;
		maxX = float.MinValue;
		maxZ = float.MinValue;
		int found = 0;

		for (int i = 0; _nodes != null && i < _nodes.Max; i++)
		{
			if (!NodePresent(observation, i))
			{
				continue;
			}

			Extend(Meters(_nodes.Value(observation, i, "x")), Meters(_nodes.Value(observation, i, "z")),
				ref minX, ref minZ, ref maxX, ref maxZ);
			found++;
		}

		Owned(observation, _doors);
		for (int i = 0; i < _doors.Count; i++)
		{
			Extend(Meters(_barracks.Value(observation, _doors[i], "x")),
				Meters(_barracks.Value(observation, _doors[i], "z")),
				ref minX, ref minZ, ref maxX, ref maxZ);
			found++;
		}

		if (found == 0)
		{
			// Nothing to fit to: fall back to the square the encoder normalizes
			// against, which is always right and always coarse.
			minX = -scale;
			minZ = -scale;
			maxX = scale;
			maxZ = scale;
			return;
		}

		float padX = Math.Max((maxX - minX) * GridPadding, MinimumSpanMeters * 0.5f);
		float padZ = Math.Max((maxZ - minZ) * GridPadding, MinimumSpanMeters * 0.5f);
		minX -= padX;
		maxX += padX;
		minZ -= padZ;
		maxZ += padZ;
	}

	private static void Extend(float x, float z, ref float minX, ref float minZ, ref float maxX,
		ref float maxZ)
	{
		minX = Math.Min(minX, x);
		maxX = Math.Max(maxX, x);
		minZ = Math.Min(minZ, z);
		maxZ = Math.Max(maxZ, z);
	}

	/// <summary>
	/// A node slot that carries a node. The owner is a one-hot, so a real record
	/// always has exactly one of the three set and a zero-padded slot has none.
	/// </summary>
	private bool NodePresent(float[] observation, int index) =>
		_nodes != null
		&& (_nodes.Value(observation, index, "owner_neutral")
			+ _nodes.Value(observation, index, "owner_ground")
			+ _nodes.Value(observation, index, "owner_strategist")) > 0.5f;

	/// <summary>
	/// The barracks slots this seat owns. One it does not own is zeroed, so a record
	/// with nothing in it is one it may not spend from — including, in principle, an
	/// owned barracks standing exactly on the origin with an empty queue and its
	/// rally point on itself, which is a map worth not building.
	/// </summary>
	private void Owned(float[] observation, List<int> into)
	{
		into.Clear();
		for (int i = 0; _barracks != null && i < _barracks.Max; i++)
		{
			bool any = false;
			for (int field = 0; field < _barracks.FloatsEach && !any; field++)
			{
				int at = _barracks.Offset + (i * _barracks.FloatsEach) + field;
				any = at >= 0 && at < (observation?.Length ?? 0) && Math.Abs(observation[at]) > 1e-6f;
			}

			if (any)
			{
				into.Add(i);
			}
		}
	}

	private bool NearestNode(float[] observation, float fromX, float fromZ, bool ownedOnly, out float x,
		out float z)
	{
		x = 0f;
		z = 0f;
		float best = float.MaxValue;

		for (int i = 0; _nodes != null && i < _nodes.Max; i++)
		{
			if (!NodePresent(observation, i))
			{
				continue;
			}

			if (ownedOnly && _nodes.Value(observation, i, "owner_strategist") < 0.5f)
			{
				continue;
			}

			float nodeX = Meters(_nodes.Value(observation, i, "x"));
			float nodeZ = Meters(_nodes.Value(observation, i, "z"));
			float distance = ((nodeX - fromX) * (nodeX - fromX)) + ((nodeZ - fromZ) * (nodeZ - fromZ));
			if (distance >= best)
			{
				continue;
			}

			best = distance;
			x = nodeX;
			z = nodeZ;
		}

		return best < float.MaxValue;
	}

	/// <summary>
	/// Where the nearest ground-force contact a sensor has right now is. Ghosts are
	/// deliberately not eligible: "attack where somebody was forty seconds ago" is
	/// a cell the grid can already name.
	/// </summary>
	private bool NearestContact(float[] observation, float fromX, float fromZ, out float x, out float z)
	{
		x = 0f;
		z = 0f;
		float best = float.MaxValue;

		for (int i = 0; _contacts != null && i < _contacts.Max; i++)
		{
			if (_contacts.Value(observation, i, "visible") < 0.5f
				|| _contacts.Value(observation, i, "is_strategist") > 0.5f)
			{
				continue;
			}

			float contactX = Meters(_contacts.Value(observation, i, "x"));
			float contactZ = Meters(_contacts.Value(observation, i, "z"));

			// The low slots are the live ones (docs/AGENT_API.md §6.2), so every
			// candidate here is current; distance from the army decides between them.
			float distance = ((contactX - fromX) * (contactX - fromX))
				+ ((contactZ - fromZ) * (contactZ - fromZ));
			if (distance >= best)
			{
				continue;
			}

			best = distance;
			x = contactX;
			z = contactZ;
		}

		return best < float.MaxValue;
	}

	private float Meters(float normalized) => normalized * _schema.PositionScaleMeters;

	private static int Clamp(int value) => value < 0 ? 0 : value >= HeadWidth ? HeadWidth - 1 : value;
}
