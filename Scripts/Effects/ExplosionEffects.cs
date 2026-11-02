using System;
using Gdpyr.Core;
using Gdpyr.Sim;
using Godot;
using Godot.Collections;

namespace Gdpyr.Effects;

/// <summary>
/// Every explosion this peer draws (docs/EFFECTS.md): a pool of effects per
/// <see cref="ExplosionKind"/>, played where a round went off or a vehicle was
/// destroyed, moved on every frame, and put back when they finish.
///
/// Made only by a process that draws — <c>CombatManager</c> builds it beside the
/// tracers, never on a dedicated server — so <see cref="Instance"/> is null there and
/// every caller's <c>?.</c> is the whole of "a server draws nothing". Purely cosmetic:
/// it reads nothing from the simulation it was not handed, and writes nothing back.
///
/// Each kind is drawn by the scene at <c>res://Effects/explosion_&lt;kind&gt;.tscn</c> when
/// there is one, and by <see cref="PlaceholderExplosion"/> until then (§4).
/// </summary>
public partial class ExplosionEffects : Node3D
{
	/// <summary>Where an artist's scene for a kind goes (<see cref="Explosions.NameOf"/>).</summary>
	public const string SceneDirectory = "res://Effects";

	/// <summary>How far below a round's burst the floor is looked for, to scorch and to land debris on [m].</summary>
	public const float DefaultFloorProbeMeters = 2.5f;

	/// <summary>How far above it the probe starts: a grenade lying on the floor is a few centimetres up.</summary>
	private const float FloorProbeAboveMeters = 0.3f;

	/// <summary>
	/// How many of each kind can play at once, by kind; past it the oldest is cut short.
	/// A grenade's whole effect is two seconds, so ten covers a hard fight; a wreck burns
	/// for eight, and six at once is a bad round for the strategist.
	/// </summary>
	private static readonly int[] Capacities = { 0, 10, 10, 10, 4, 6 };

	public static ExplosionEffects Instance { get; private set; }

	private readonly EffectPool<IExplosionEffect>[] _pools = new EffectPool<IExplosionEffect>[Capacities.Length];
	private readonly PackedScene[] _scenes = new PackedScene[Capacities.Length];
	private readonly bool[] _looked = new bool[Capacities.Length];
	private readonly Random _random = new();

	/// <summary>Explosions played so far, for the debug HUD's <c>explosions</c> row.</summary>
	public int Played { get; private set; }

	/// <summary>Explosions playing now.</summary>
	public int Active
	{
		get
		{
			int active = 0;
			foreach (EffectPool<IExplosionEffect> pool in _pools)
			{
				active += pool?.ActiveCount ?? 0;
			}
			return active;
		}
	}

	/// <summary>Explosions cut short for a newer one: anything but 0 means a kind's pool is small for the fight.</summary>
	public int Evictions
	{
		get
		{
			int evictions = 0;
			foreach (EffectPool<IExplosionEffect> pool in _pools)
			{
				evictions += pool?.Evictions ?? 0;
			}
			return evictions;
		}
	}

	public override void _EnterTree()
	{
		Instance = this;
	}

	public override void _ExitTree()
	{
		if (Instance == this)
		{
			Instance = null;
		}
	}

	/// <summary>
	/// Draws one explosion of <paramref name="kind"/> at <paramref name="point"/>.
	/// <paramref name="reachMeters"/> is how far its blast does damage, for the ring
	/// that shows it (0: none); <paramref name="scale"/> sizes the whole of it;
	/// <paramref name="tint"/> colours the debris, a vehicle's own colour. Nothing for
	/// <see cref="ExplosionKind.None"/>.
	/// </summary>
	public void Spawn(ExplosionKind kind, Vector3 point, float reachMeters, float scale = 1f, Color? tint = null,
		float floorProbeMeters = DefaultFloorProbeMeters)
	{
		int slot = (int)kind;
		if (kind == ExplosionKind.None || slot >= Capacities.Length || !IsInsideTree())
		{
			return;
		}

		bool hasFloor = TryFindFloor(point, floorProbeMeters, out Vector3 floor);
		var request = new ExplosionRequest
		{
			Kind = kind,
			Point = point,
			ReachMeters = float.IsFinite(reachMeters) ? MathF.Max(reachMeters, 0f) : 0f,
			Scale = float.IsFinite(scale) && scale > 0f ? scale : 1f,
			HasFloor = hasFloor,
			Floor = floor,
			HasTint = tint.HasValue,
			Tint = tint ?? Colors.White,
			Seed = (uint)_random.Next(1, int.MaxValue),
		};

		IExplosionEffect effect = PoolFor(kind).Acquire(out _, out _);
		effect.Play(request);
		Played++;
	}

	/// <summary>
	/// A vehicle's wreck going up (docs/EFFECTS.md §2.3), from where its feet were: at the
	/// middle of the hull, sized by the hull against the tank's, its debris the vehicle's
	/// <paramref name="color"/>, and the floor looked for as far below the hull as a
	/// round's is below its burst.
	/// </summary>
	public void SpawnWreck(Vector3 feet, float heightMeters, float radiusMeters, Color color)
	{
		float halfHeight = MathF.Max(heightMeters, 0f) * 0.5f;
		Spawn(ExplosionKind.Vehicle, feet + (Vector3.Up * halfHeight), reachMeters: 0f,
			Explosions.HullScale(radiusMeters), color, halfHeight + DefaultFloorProbeMeters);
	}

	/// <summary>Takes every explosion off the screen at once, keeping the effects for next time.</summary>
	public void Clear()
	{
		foreach (EffectPool<IExplosionEffect> pool in _pools)
		{
			if (pool == null)
			{
				continue;
			}

			for (int i = 0; i < pool.Created; i++)
			{
				pool.At(i).Stop();
			}

			pool.ReleaseAll();
		}
	}

	public override void _Process(double delta)
	{
		float dt = (float)delta;
		foreach (EffectPool<IExplosionEffect> pool in _pools)
		{
			if (pool == null || pool.ActiveCount == 0)
			{
				continue;
			}

			for (int i = 0; i < pool.Created; i++)
			{
				if (pool.IsActive(i) && !pool.At(i).Advance(dt))
				{
					pool.Release(i);
				}
			}
		}
	}

	private EffectPool<IExplosionEffect> PoolFor(ExplosionKind kind)
	{
		int slot = (int)kind;
		return _pools[slot] ??= new EffectPool<IExplosionEffect>(Capacities[slot], () => Create(kind));
	}

	/// <summary>An artist's effect for <paramref name="kind"/> when there is a usable one, else the placeholder; in the tree either way.</summary>
	private IExplosionEffect Create(ExplosionKind kind)
	{
		Node3D node = Instantiate(kind) ?? new PlaceholderExplosion();
		node.Name = $"{Explosions.NameOf(kind)}_{_pools[(int)kind]?.Created ?? 0}";
		AddChild(node);
		return (IExplosionEffect)node;
	}

	private Node3D Instantiate(ExplosionKind kind)
	{
		int slot = (int)kind;
		if (!_looked[slot])
		{
			_looked[slot] = true;
			string path = $"{SceneDirectory}/explosion_{Explosions.NameOf(kind)}.tscn";
			if (ResourceLoader.Exists(path))
			{
				_scenes[slot] = GD.Load<PackedScene>(path);
			}
		}

		if (_scenes[slot] == null)
		{
			return null;
		}

		Node node = _scenes[slot].Instantiate();
		if (node is Node3D node3D and IExplosionEffect)
		{
			return node3D;
		}

		// Loud once, then the placeholder for good: a scene that cannot be played is a
		// broken asset, not a reason to draw nothing.
		GD.PushWarning($"[effects] {_scenes[slot].ResourcePath} is not a Node3D implementing {nameof(IExplosionEffect)};"
			+ " drawing the placeholder instead");
		node.Free();
		_scenes[slot] = null;
		return null;
	}

	/// <summary>The floor under <paramref name="point"/>, if the world has one within <paramref name="probeMeters"/>.</summary>
	private bool TryFindFloor(Vector3 point, float probeMeters, out Vector3 floor)
	{
		floor = point;
		PhysicsDirectSpaceState3D space = GetWorld3D()?.DirectSpaceState;
		if (space == null || probeMeters <= 0f)
		{
			return false;
		}

		PhysicsRayQueryParameters3D query = PhysicsRayQueryParameters3D.Create(
			point + (Vector3.Up * FloorProbeAboveMeters), point + (Vector3.Down * probeMeters), CollisionLayers.World);
		Dictionary hit = space.IntersectRay(query);
		if (hit.Count == 0)
		{
			return false;
		}

		floor = hit["position"].AsVector3();
		return true;
	}
}
