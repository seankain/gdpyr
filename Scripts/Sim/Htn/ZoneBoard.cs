using System;
using Godot;

namespace Gdpyr.Sim.Htn;

/// <summary>The places a side plans around (docs/HTN_BOTS.md §4.3).</summary>
public enum ZoneKind : byte
{
	ResourceNode = 0,

	Barracks = 1,

	GroundSpawn = 2,
}

/// <summary>One place on a <see cref="ZoneBoard"/>, and what the side knows about it.</summary>
public struct Zone
{
	public ZoneKind Kind;

	/// <summary>Its index in its own list: the economy's nodes, the unit manager's barracks, the spawn points.</summary>
	public int Index;

	public Vector3 Position;

	/// <summary>How far round it a body counts as being at it.</summary>
	public float RadiusMeters;

	public NodeHolder Holder;

	public bool Contested;

	/// <summary>The side's own strength here (<see cref="ForceRatio.Strength"/>), from the last census.</summary>
	public float FriendlyStrength;

	/// <summary>The other side's, estimated from what the side remembers (<see cref="ZoneBoard.AddEnemies"/>).</summary>
	public float EnemyStrength;

	/// <summary>False until one of the side's sensors has covered it.</summary>
	public bool Observed;

	public uint LastObservedTick;
}

/// <summary>
/// What each kind of contact is worth to an enemy-strength estimate — its cost —
/// and how much worse armour is when there is nothing to answer it with
/// (docs/HTN_BOTS.md §4.3). The engine half fills the costs in from the catalogs;
/// these are numbers, not lookups, so the board stays engine-free.
/// </summary>
public readonly struct ThreatWeights
{
	public readonly float Infantry;
	public readonly float Armour;
	public readonly float ArmedStructure;
	public readonly float Player;

	/// <summary>
	/// What a bullet-proof contact counts for, times its weight, when the side has no
	/// explosive to answer it with. A tank the ground force cannot hurt is worth more
	/// than one it can.
	/// </summary>
	public readonly float UnansweredArmourScale;

	public ThreatWeights(float infantry, float armour, float armedStructure, float player,
		float unansweredArmourScale)
	{
		Infantry = infantry;
		Armour = armour;
		ArmedStructure = armedStructure;
		Player = player;
		UnansweredArmourScale = MathF.Max(unansweredArmourScale, 1f);
	}

	public float Of(ContactKind kind) => kind switch
	{
		ContactKind.Armour => Armour,
		ContactKind.ArmedStructure => ArmedStructure,
		ContactKind.Player => Player,
		_ => Infantry,
	};
}

/// <summary>
/// One side's picture of the map's places — each resource node, each barracks,
/// each ground spawn (docs/HTN_BOTS.md §4.3): who holds it, whether it is
/// contested, the side's strength there, the other side's estimated strength, and
/// how long it has been since the side had eyes on it. The commander's Defend Zone,
/// Attack and Recon read it (§5.3), and the ground coordinator's sweep reads the
/// stalest node off it (§5.1).
///
/// A census, not a history: the layout is added once per map, and each rebuild is
/// <see cref="BeginCensus"/>, then the friendly bodies, then the remembered enemies,
/// then the sensors. Engine-free and fixed-size; nothing allocated after
/// construction.
/// </summary>
public sealed class ZoneBoard
{
	/// <summary>Every node and barracks a map may carry, and a spawn point per player.</summary>
	public const int DefaultCapacity = SimConfig.MaxResourceNodes + SimConfig.MaxBarracks + SnapshotCodec.MaxPlayers;

	private readonly Zone[] _zones;

	public ZoneBoard(int capacity = DefaultCapacity) => _zones = new Zone[Math.Max(capacity, 1)];

	public int Capacity => _zones.Length;

	public int Count { get; private set; }

	/// <summary>Places refused because the board was full. Anything but 0 is a sizing bug.</summary>
	public int Overflows { get; private set; }

	public Zone At(int index) => index >= 0 && index < Count ? _zones[index] : default;

	/// <summary>Adds one place to the layout. Returns its index on the board, or -1 when the board is full.</summary>
	public int Add(ZoneKind kind, int index, Vector3 position, float radiusMeters)
	{
		if (Count == _zones.Length)
		{
			Overflows++;
			return -1;
		}

		_zones[Count] = new Zone
		{
			Kind = kind,
			Index = index,
			Position = position,
			RadiusMeters = MathF.Max(radiusMeters, 0f),
		};
		return Count++;
	}

	/// <summary>Drops the layout, for a new map.</summary>
	public void Clear()
	{
		Array.Clear(_zones);
		Count = 0;
		Overflows = 0;
	}

	public void SetHolder(int zone, NodeHolder holder, bool contested)
	{
		if (zone >= 0 && zone < Count)
		{
			_zones[zone].Holder = holder;
			_zones[zone].Contested = contested;
		}
	}

	/// <summary>Starts a rebuild: both sides' strengths back to nothing. Holders and observation are kept.</summary>
	public void BeginCensus()
	{
		for (int i = 0; i < Count; i++)
		{
			_zones[i].FriendlyStrength = 0f;
			_zones[i].EnemyStrength = 0f;
		}
	}

	/// <summary>One of the side's own bodies, counted at every zone whose radius it stands in.</summary>
	public void AddFriendly(Vector3 position, float strength)
	{
		for (int i = 0; i < Count; i++)
		{
			if (Covers(i, position))
			{
				_zones[i].FriendlyStrength += MathF.Max(strength, 0f);
			}
		}
	}

	/// <summary>
	/// The other side, from what <paramref name="contacts"/> remembers — live and
	/// ghost, at their last-known positions: an estimate, which is what the side is
	/// entitled to. Each contact is worth its kind's weight times its health.
	///
	/// Bullet-proof contacts are counted against <paramref name="explosives"/>, the
	/// side's explosives in hand: as many as there are explosives count at their
	/// weight, the rest at <see cref="ThreatWeights.UnansweredArmourScale"/> times it.
	/// Which ones are answered is not decided — the share is applied to their sum —
	/// and the explosives are the side's, not the zone's, so two zones' tanks are
	/// each measured against every launcher the side has.
	/// </summary>
	public void AddEnemies(ContactMemory contacts, uint tick, in ThreatWeights weights, int explosives)
	{
		if (contacts == null)
		{
			return;
		}

		explosives = Math.Max(explosives, 0);

		for (int z = 0; z < Count; z++)
		{
			float soft = 0f;
			float armour = 0f;
			int armoured = 0;

			for (int i = 0; i < contacts.Count; i++)
			{
				KnownContact contact = contacts.At(i);
				if (!contacts.IsRemembered(i, tick) || !Covers(z, contact.Position))
				{
					continue;
				}

				float strength = ForceRatio.Strength(weights.Of(contact.Kind), contact.HealthFraction);
				if (contact.BulletProof)
				{
					armour += strength;
					armoured++;
				}
				else
				{
					soft += strength;
				}
			}

			if (armoured > 0)
			{
				float answered = Math.Min(explosives, armoured) / (float)armoured;
				armour *= answered + ((1f - answered) * weights.UnansweredArmourScale);
			}

			_zones[z].EnemyStrength += soft + armour;
		}
	}

	/// <summary>Marks every zone one of the side's sensors covers as observed now. No rays: range only.</summary>
	public void Observe(VisionField sensors, uint tick)
	{
		for (int i = 0; sensors != null && i < Count; i++)
		{
			if (sensors.Sees(_zones[i].Position))
			{
				MarkObserved(i, tick);
			}
		}
	}

	public void MarkObserved(int zone, uint tick)
	{
		if (zone >= 0 && zone < Count)
		{
			_zones[zone].Observed = true;
			_zones[zone].LastObservedTick = tick;
		}
	}

	/// <summary>Ticks since the side last had eyes on it; <see cref="uint.MaxValue"/> for never.</summary>
	public uint TicksSinceObserved(int zone, uint tick)
	{
		if (zone < 0 || zone >= Count || !_zones[zone].Observed)
		{
			return uint.MaxValue;
		}

		uint seen = _zones[zone].LastObservedTick;
		return tick >= seen ? tick - seen : 0u;
	}

	/// <summary>
	/// The zone of this kind the side has gone longest without seeing — never seen
	/// first — or -1 when there is none. Ties go to the lower index, so the answer
	/// is the same on every run (§4.2 rule 5).
	/// </summary>
	public int Stalest(ZoneKind kind, uint tick)
	{
		int best = -1;
		uint oldest = 0;

		for (int i = 0; i < Count; i++)
		{
			if (_zones[i].Kind != kind)
			{
				continue;
			}

			uint age = TicksSinceObserved(i, tick);
			if (best < 0 || age > oldest)
			{
				best = i;
				oldest = age;
			}
		}

		return best;
	}

	private bool Covers(int zone, Vector3 position)
	{
		float radius = _zones[zone].RadiusMeters;
		return _zones[zone].Position.DistanceSquaredTo(position) <= radius * radius;
	}
}
