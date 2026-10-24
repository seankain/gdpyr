using System;
using Godot;

namespace Gdpyr.Sim;

/// <summary>
/// Everything the simulation needs to know about one projectile type: how it
/// flies, how hard it hits and how long it lasts.
/// </summary>
public readonly struct ProjectileStats
{
	public readonly ProjectileParams Physics;

	/// <summary>Speed at the muzzle [m/s]. A tuning knob, not a realism constant (docs/NETCODE.md §4.4).</summary>
	public readonly float MuzzleVelocity;

	/// <summary>Damage on a direct hit.</summary>
	public readonly float Damage;

	/// <summary>Radius of the blast [m], or 0 for a projectile that only damages what it hits.</summary>
	public readonly float ExplosionRadiusMeters;

	/// <summary>Damage at the centre of the blast, falling off linearly to zero at the radius.</summary>
	public readonly float ExplosionDamage;

	public readonly float LifetimeSeconds;

	/// <summary>
	/// The projectile's own radius for hit tests [m]. A bullet is a ray; a grenade
	/// is a body, and testing it as a ray makes it pass through a shoulder.
	/// </summary>
	public readonly float SweepRadiusMeters;

	/// <summary>
	/// True for a round on a fuse — the hand grenade (docs/NETCODE.md §10.9). It does
	/// not go off on contact: it stops where it lands, and detonates where it is when
	/// <see cref="LifetimeSeconds"/>, which is then its fuse, runs out.
	/// </summary>
	public readonly bool Fused;

	/// <summary>
	/// The share of its damage a vehicle takes from it — direct and blast alike, on top
	/// of the vehicle's own armour (<see cref="Armour"/>). One for every round but the
	/// hand grenade, which is anti-personnel.
	/// </summary>
	public readonly float VehicleDamageScale;

	/// <summary>The same share for a structure. One for every round but the hand grenade.</summary>
	public readonly float StructureDamageScale;

	public ProjectileStats(in ProjectileParams physics, float muzzleVelocity, float damage,
		float explosionRadiusMeters = 0f, float explosionDamage = 0f,
		float lifetimeSeconds = SimConfig.MaxProjectileLifetimeSeconds, float sweepRadiusMeters = 0f,
		bool fused = false, float vehicleDamageScale = 1f, float structureDamageScale = 1f)
	{
		Physics = physics;
		MuzzleVelocity = MathF.Max(muzzleVelocity, 0f);
		Damage = damage;
		ExplosionRadiusMeters = MathF.Max(explosionRadiusMeters, 0f);
		ExplosionDamage = explosionDamage;
		LifetimeSeconds = lifetimeSeconds > 0f ? lifetimeSeconds : SimConfig.MaxProjectileLifetimeSeconds;
		SweepRadiusMeters = MathF.Max(sweepRadiusMeters, 0f);
		Fused = fused;
		VehicleDamageScale = MathF.Max(vehicleDamageScale, 0f);
		StructureDamageScale = MathF.Max(structureDamageScale, 0f);
	}

	public bool IsExplosive => ExplosionRadiusMeters > 0f && ExplosionDamage != 0f;

	/// <summary>
	/// Blast damage at a distance from the detonation, falling off linearly. Linear
	/// rather than inverse-square because inverse-square in a greybox means "lethal
	/// everywhere or nowhere, depending on one constant".
	/// </summary>
	public float SplashDamageAt(float distanceMeters)
	{
		if (!IsExplosive || distanceMeters >= ExplosionRadiusMeters)
		{
			return 0f;
		}
		return ExplosionDamage * (1f - distanceMeters / ExplosionRadiusMeters);
	}
}

/// <summary>
/// One projectile in flight. Data, not a node: nothing about it is replicated
/// per tick (docs/NETCODE.md §4.2).
/// </summary>
public struct Projectile
{
	public uint Id;
	public uint SpawnTick;
	public int OwnerPeerId;
	public byte DefinitionId;
	public Vector3 Position;
	public Vector3 Velocity;

	/// <summary>
	/// Where it left the muzzle or the hand. Server-side bookkeeping, already on the
	/// wire in the spawn message: what a unit it passes perceives as where the shot
	/// came from (docs/TACTICS.md §4.1).
	/// </summary>
	public Vector3 Origin;

	/// <summary>Seconds of flight so far.</summary>
	public float Age;

	/// <summary>Ticks still owed to this projectile before it is where it should be.</summary>
	public int PendingCatchUpTicks;

	/// <summary>
	/// A fused round that has landed (<see cref="ProjectileSim.Land"/>): it stays where
	/// it is, flying no chord, until its fuse runs out.
	/// </summary>
	public bool Resting;

	public bool Alive;
}

/// <summary>
/// The straight line a projectile covered on one tick. Hits are resolved against
/// this, not against the projectile's position: a 940 m/s round moves 15.7 m per
/// 60 Hz tick, so a point test would miss both thin geometry and players
/// (docs/NETCODE.md §4.2).
/// </summary>
public readonly struct ProjectileSegment
{
	public readonly uint Id;
	public readonly int OwnerPeerId;
	public readonly byte DefinitionId;
	public readonly Vector3 From;
	public readonly Vector3 To;

	/// <summary>The projectile's own radius, for a swept rather than a thin test.</summary>
	public readonly float SweepRadius;

	/// <summary>
	/// A fused round whose fuse runs out at the end of this chord: it detonates at
	/// <see cref="To"/>, or wherever the chord stopped first. Never set for a round that
	/// goes off on contact.
	/// </summary>
	public readonly bool Detonates;

	/// <summary>The chord of a round lying where it landed: no length, and nothing to hit.</summary>
	public readonly bool Resting;

	/// <summary>Where the round was fired from (<see cref="Projectile.Origin"/>).</summary>
	public readonly Vector3 Origin;

	public ProjectileSegment(uint id, int ownerPeerId, byte definitionId, Vector3 from, Vector3 to, float sweepRadius,
		bool detonates = false, bool resting = false, Vector3 origin = default)
	{
		Id = id;
		OwnerPeerId = ownerPeerId;
		DefinitionId = definitionId;
		From = from;
		To = to;
		SweepRadius = sweepRadius;
		Detonates = detonates;
		Resting = resting;
		Origin = origin;
	}

	public Vector3 Delta => To - From;

	public Vector3 PointAt(float t) => From + (To - From) * t;
}

/// <summary>
/// Analytic, server-authoritative projectiles (docs/NETCODE.md §4.2): a fixed
/// pool, stepped with the ballistics integrator, emitting one chord per
/// projectile per tick for the caller to test against the world.
///
/// The same class runs on clients, where it is fed by <c>ProjectileSpawn</c>
/// messages and drives nothing but tracers — given the same spawn record it
/// reproduces the identical trajectory, which is the whole point of sending
/// twenty bytes instead of a replicated node.
///
/// Storage is fixed at construction and there is no allocation per tick or per
/// shot: C# GC hitches are the most likely reason a prototype like this feels bad
/// (docs/IMPLEMENTATION_PLAN.md §6).
/// </summary>
public sealed class ProjectileSim
{
	private readonly ProjectileStats[] _definitions;
	private readonly Projectile[] _items;
	private readonly ProjectileSegment[] _segments;

	private int _count;
	private int _segmentCount;
	private uint _nextId = 1;

	/// <summary>Shots this pool had to refuse because it was full. Anything but 0 is worth seeing.</summary>
	public int Overflows { get; private set; }

	public ProjectileSim(ProjectileStats[] definitions, int capacity = SimConfig.MaxLiveProjectiles)
	{
		_definitions = definitions ?? Array.Empty<ProjectileStats>();
		Capacity = Math.Max(capacity, 1);
		_items = new Projectile[Capacity];
		_segments = new ProjectileSegment[Capacity];
	}

	public int Capacity { get; }

	/// <summary>Projectiles in flight.</summary>
	public int LiveCount
	{
		get
		{
			int live = 0;
			for (int i = 0; i < _count; i++)
			{
				if (_items[i].Alive)
				{
					live++;
				}
			}
			return live;
		}
	}

	/// <summary>The chords covered by the last <see cref="Step"/>.</summary>
	public ReadOnlySpan<ProjectileSegment> Segments => _segments.AsSpan(0, _segmentCount);

	public ProjectileStats StatsFor(byte definitionId) =>
		definitionId < _definitions.Length ? _definitions[definitionId] : default;

	/// <summary>Allocates the next id. Server-side only: ids are the server's to hand out.</summary>
	public uint ReserveId() => _nextId++;

	/// <summary>
	/// Puts a projectile in the air.
	///
	/// <paramref name="catchUpTicks"/> is how far behind the present this spawn is:
	/// on a server it is the shooter's one-way latency, so the shot arrives where
	/// they aimed (docs/NETCODE.md §4.3); on a client it is the gap between the
	/// spawn tick and the tick being rendered.
	/// </summary>
	public bool TrySpawn(uint id, uint spawnTick, int ownerPeerId, byte definitionId, Vector3 origin,
		Vector3 direction, int catchUpTicks, out int index)
	{
		index = -1;
		if (direction == Vector3.Zero)
		{
			return false;
		}

		if (!TryTakeSlot(out index))
		{
			Overflows++;
			return false;
		}

		ProjectileStats stats = StatsFor(definitionId);
		_items[index] = new Projectile
		{
			Id = id,
			SpawnTick = spawnTick,
			OwnerPeerId = ownerPeerId,
			DefinitionId = definitionId,
			Position = origin,
			Velocity = direction.Normalized() * stats.MuzzleVelocity,
			Origin = origin,
			Age = 0f,
			PendingCatchUpTicks = Math.Clamp(catchUpTicks, 0, SimConfig.MaxProjectileCatchUpTicks),
			Alive = true,
		};
		return true;
	}

	/// <summary>Spawns with a server-allocated id.</summary>
	public uint Spawn(uint spawnTick, int ownerPeerId, byte definitionId, Vector3 origin, Vector3 direction,
		int catchUpTicks)
	{
		uint id = ReserveId();
		return TrySpawn(id, spawnTick, ownerPeerId, definitionId, origin, direction, catchUpTicks, out _) ? id : 0u;
	}

	/// <summary>
	/// Advances every projectile one tick — plus whatever catch-up it is owed — and
	/// records the chord each one covered. Returns the number of chords written.
	///
	/// A projectile that expires on this tick still contributes its chord: the tick
	/// it runs out on is a tick it was flying, and a round that reaches its target
	/// on the last tick of its life has hit. A fused round's last chord is marked
	/// <see cref="ProjectileSegment.Detonates"/>; one lying where it landed flies no
	/// chord at all until then, when it gets one of no length to go off on.
	/// </summary>
	public int Step(float dt)
	{
		Compact();
		_segmentCount = 0;

		for (int i = 0; i < _count; i++)
		{
			ref Projectile p = ref _items[i];
			if (!p.Alive)
			{
				continue;
			}

			ProjectileStats stats = StatsFor(p.DefinitionId);
			Vector3 start = p.Position;

			int ticks = 1 + p.PendingCatchUpTicks;
			p.PendingCatchUpTicks = 0;
			for (int t = 0; !p.Resting && t < ticks; t++)
			{
				Ballistics.Integrate(dt, SimConfig.ProjectileSubSteps, ref p.Position, ref p.Velocity, stats.Physics);
			}
			p.Age += dt * ticks;

			bool expired = p.Age >= stats.LifetimeSeconds;
			bool detonates = expired && stats.Fused;
			if (!p.Resting || detonates)
			{
				_segments[_segmentCount++] = new ProjectileSegment(p.Id, p.OwnerPeerId, p.DefinitionId, start,
					p.Position, stats.SweepRadiusMeters, detonates, p.Resting, p.Origin);
			}

			if (expired || p.Position.Y < SimConfig.ProjectileKillPlaneY)
			{
				p.Alive = false;
			}
		}

		return _segmentCount;
	}

	/// <summary>
	/// Puts a fused round down where it landed (docs/NETCODE.md §10.9): it stops there
	/// and stays until its fuse runs out. The server decides where, and a client is
	/// told the same point in a hit message, so the two agree on where it lies. False
	/// for an id that is not in the air.
	/// </summary>
	public bool Land(uint id, Vector3 point)
	{
		for (int i = 0; i < _count; i++)
		{
			if (_items[i].Alive && _items[i].Id == id)
			{
				_items[i].Position = point;
				_items[i].Velocity = Vector3.Zero;
				_items[i].Resting = true;
				return true;
			}
		}
		return false;
	}

	public bool TryGet(uint id, out Projectile projectile)
	{
		for (int i = 0; i < _count; i++)
		{
			if (_items[i].Alive && _items[i].Id == id)
			{
				projectile = _items[i];
				return true;
			}
		}

		projectile = default;
		return false;
	}

	/// <summary>
	/// Renames a projectile in place.
	///
	/// This exists for exactly one case: a client's predicted tracer, once the
	/// server's account of the same shot arrives carrying the real id. Replacing the
	/// tracer instead would drop a projectile that has been flying for a round trip
	/// and start a fresh one further back down the trajectory, which reads as the
	/// round jumping backwards out of the barrel (docs/NETCODE.md §4.3).
	/// </summary>
	public bool Rekey(uint oldId, uint newId)
	{
		for (int i = 0; i < _count; i++)
		{
			if (_items[i].Alive && _items[i].Id == oldId)
			{
				_items[i].Id = newId;
				return true;
			}
		}
		return false;
	}

	/// <summary>Takes a projectile out of the air. A hit, or a client being told about one.</summary>
	public bool Kill(uint id)
	{
		for (int i = 0; i < _count; i++)
		{
			if (_items[i].Alive && _items[i].Id == id)
			{
				_items[i].Alive = false;
				return true;
			}
		}
		return false;
	}

	/// <summary>Every projectile in flight, alive ones only. For rendering.</summary>
	public bool TryGetAt(int index, out Projectile projectile)
	{
		if (index >= 0 && index < _count && _items[index].Alive)
		{
			projectile = _items[index];
			return true;
		}

		projectile = default;
		return false;
	}

	/// <summary>Upper bound for <see cref="TryGetAt"/>; includes slots whose projectile has died.</summary>
	public int SlotCount => _count;

	public void Clear()
	{
		_count = 0;
		_segmentCount = 0;
	}

	private bool TryTakeSlot(out int index)
	{
		// Dead slots are reused before the array is grown into, so a firefight does
		// not walk the pool's high-water mark up to the cap and stay there.
		for (int i = 0; i < _count; i++)
		{
			if (!_items[i].Alive)
			{
				index = i;
				return true;
			}
		}

		if (_count < Capacity)
		{
			index = _count++;
			return true;
		}

		index = -1;
		return false;
	}

	/// <summary>
	/// Drops dead projectiles off the end of the dense range. Done at the top of a
	/// step rather than on death so that a caller still has the whole tick to
	/// resolve the chords the last step produced.
	/// </summary>
	private void Compact()
	{
		while (_count > 0 && !_items[_count - 1].Alive)
		{
			_count--;
		}
	}
}
