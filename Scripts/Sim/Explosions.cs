using System;
using Godot;

namespace Gdpyr.Sim;

/// <summary>
/// What blew up, as far as drawing it goes (docs/EFFECTS.md). Presentation only:
/// nothing in the simulation reads it and it never goes on the wire. A round says
/// which one it is in its <c>ProjectileDefinition.Explosion</c>; a vehicle is always
/// <see cref="Vehicle"/>.
/// </summary>
public enum ExplosionKind : byte
{
	/// <summary>A round that only stops: the impact marker, not an explosion.</summary>
	None = 0,

	/// <summary>A hand grenade's fuse running out: mostly dirt and smoke, a small flash.</summary>
	HandGrenade = 1,

	/// <summary>The launcher's 40 mm round.</summary>
	LauncherGrenade = 2,

	/// <summary>A heavy shell: the tank's main gun, the barracks mortar.</summary>
	Shell = 3,

	/// <summary>An FPV drone's warhead.</summary>
	Drone = 4,

	/// <summary>A vehicle destroyed: a large fireball, debris, and a burning, smoking wreck site.</summary>
	Vehicle = 5,
}

/// <summary>
/// The numbers one <see cref="ExplosionKind"/> is drawn with (docs/EFFECTS.md §3).
/// Sizes are metres at a scale of one; every duration is seconds from the moment it
/// went off. Each part is optional — a count, a radius or a duration of zero leaves
/// that part out.
/// </summary>
public readonly struct ExplosionProfile
{
	/// <summary>Radius the fireball grows to.</summary>
	public float FireballRadius { get; init; }

	public float FireballSeconds { get; init; }

	/// <summary>How bright the flash is at its peak (an <c>OmniLight3D</c>'s energy), and how far it reaches.</summary>
	public float FlashEnergy { get; init; }

	public float FlashRange { get; init; }

	public float FlashSeconds { get; init; }

	public int SmokePuffs { get; init; }

	/// <summary>Radius one puff grows to.</summary>
	public float SmokeRadius { get; init; }

	/// <summary>How long one puff lives.</summary>
	public float SmokeSeconds { get; init; }

	/// <summary>
	/// Over how long the puffs are let out, first to last: a burst for a round, a
	/// column for a burning wreck. Zero lets them all out at once.
	/// </summary>
	public float SmokeEmitSeconds { get; init; }

	/// <summary>How fast a puff climbs [m/s].</summary>
	public float SmokeRiseSpeed { get; init; }

	public Color SmokeColor { get; init; }

	public int DebrisPieces { get; init; }

	/// <summary>How fast debris leaves the blast [m/s].</summary>
	public float DebrisSpeed { get; init; }

	public float DebrisSeconds { get; init; }

	/// <summary>Edge length of one piece of debris.</summary>
	public float DebrisSize { get; init; }

	public Color DebrisColor { get; init; }

	/// <summary>
	/// How long the ring that sweeps out to the blast's reach takes. What tells a
	/// player how far the damage went (a grenade "kills within 2 m and reaches 7 m");
	/// zero for something that does no blast damage — a vehicle's wreck.
	/// </summary>
	public float ReachSeconds { get; init; }

	/// <summary>The dark mark left on the floor under it, if there is a floor close enough.</summary>
	public float ScorchRadius { get; init; }

	public float ScorchSeconds { get; init; }

	/// <summary>How long a fire burns where it went off: a wreck's.</summary>
	public float BurnSeconds { get; init; }

	/// <summary>When every part has finished, and the effect can be put away.</summary>
	public float Lifetime => MathF.Max(
		MathF.Max(MathF.Max(FireballSeconds, FlashSeconds), MathF.Max(ReachSeconds, DebrisSeconds)),
		MathF.Max(MathF.Max(SmokePuffs > 0 ? SmokeEmitSeconds + SmokeSeconds : 0f, ScorchSeconds), BurnSeconds));
}

/// <summary>
/// One explosion to draw (docs/EFFECTS.md §2): what it was, where, how far its blast
/// reached, and how big it is drawn.
/// </summary>
public readonly struct ExplosionRequest
{
	public ExplosionKind Kind { get; init; }

	/// <summary>Where it went off: a round's hit point, a vehicle's middle.</summary>
	public Vector3 Point { get; init; }

	/// <summary>The blast's damage radius, for the reach ring; 0 draws none.</summary>
	public float ReachMeters { get; init; }

	/// <summary>Multiplies every size in the profile: a dropship's wreck is bigger than a truck's.</summary>
	public float Scale { get; init; }

	/// <summary>The floor under <see cref="Point"/>, if one was found close enough to scorch.</summary>
	public bool HasFloor { get; init; }

	public Vector3 Floor { get; init; }

	/// <summary>What colour the debris is, when it is a vehicle's own; the profile's when <see cref="HasTint"/> is false.</summary>
	public bool HasTint { get; init; }

	public Color Tint { get; init; }

	/// <summary>Seeds where the smoke and debris of this one go. Cosmetic: no two blasts need look alike, and none need agree across peers.</summary>
	public uint Seed { get; init; }
}

/// <summary>
/// The engine-free half of explosion effects (docs/EFFECTS.md): which kind a hit or a
/// death is drawn as, each kind's numbers, and the curves that turn an effect's age
/// into a size, a colour and an opacity. The nodes that draw them are
/// <c>Scripts/Effects</c>; everything they decide is here, so it is answered by
/// <c>dotnet test</c> rather than by looking at a screen.
/// </summary>
public static class Explosions
{
	/// <summary>
	/// What an explosive hit is drawn as when this peer cannot say which round it was —
	/// a strategist on a client who never got the spawn of a round fired from the fog.
	/// The launcher's: mid-sized, and not mistakable for a wreck.
	/// </summary>
	public const ExplosionKind Unidentified = ExplosionKind.LauncherGrenade;

	/// <summary>The most smoke puffs and pieces of debris an effect is built with; a profile asking for more gets this many.</summary>
	public const int MaxSmokePuffs = 16;

	public const int MaxDebrisPieces = 16;

	/// <summary>A tank's hull radius [m]: the size a wreck is drawn at a scale of one.</summary>
	public const float ReferenceHullRadius = 1.3f;

	/// <summary>The scale a wreck is drawn at either side of the tank's, so a mistyped radius cannot fill the screen.</summary>
	public const float MinHullScale = 0.6f;

	public const float MaxHullScale = 2f;

	private static readonly Color Dirt = new(0.36f, 0.31f, 0.26f);
	private static readonly Color Smoke = new(0.42f, 0.42f, 0.42f);
	private static readonly Color BlackSmoke = new(0.13f, 0.12f, 0.12f);
	private static readonly Color Clod = new(0.24f, 0.2f, 0.16f);
	private static readonly Color Metal = new(0.22f, 0.22f, 0.24f);

	private static readonly ExplosionProfile HandGrenadeProfile = new()
	{
		FireballRadius = 0.9f, FireballSeconds = 0.22f,
		FlashEnergy = 4f, FlashRange = 7f, FlashSeconds = 0.18f,
		SmokePuffs = 6, SmokeRadius = 1.3f, SmokeSeconds = 1.8f, SmokeEmitSeconds = 0.1f, SmokeRiseSpeed = 1.1f,
		SmokeColor = Dirt,
		DebrisPieces = 10, DebrisSpeed = 7f, DebrisSeconds = 1.2f, DebrisSize = 0.1f, DebrisColor = Clod,
		ReachSeconds = 0.45f,
		ScorchRadius = 1.1f, ScorchSeconds = 8f,
	};

	private static readonly ExplosionProfile LauncherProfile = new()
	{
		FireballRadius = 1.3f, FireballSeconds = 0.3f,
		FlashEnergy = 6f, FlashRange = 10f, FlashSeconds = 0.22f,
		SmokePuffs = 6, SmokeRadius = 1.5f, SmokeSeconds = 2f, SmokeEmitSeconds = 0.15f, SmokeRiseSpeed = 1.3f,
		SmokeColor = Smoke,
		DebrisPieces = 8, DebrisSpeed = 8f, DebrisSeconds = 1.2f, DebrisSize = 0.1f, DebrisColor = Clod,
		ReachSeconds = 0.45f,
		ScorchRadius = 1.4f, ScorchSeconds = 8f,
	};

	private static readonly ExplosionProfile ShellProfile = new()
	{
		FireballRadius = 2f, FireballSeconds = 0.35f,
		FlashEnergy = 9f, FlashRange = 15f, FlashSeconds = 0.25f,
		SmokePuffs = 8, SmokeRadius = 2f, SmokeSeconds = 2.6f, SmokeEmitSeconds = 0.2f, SmokeRiseSpeed = 1.6f,
		SmokeColor = Smoke,
		DebrisPieces = 12, DebrisSpeed = 10f, DebrisSeconds = 1.4f, DebrisSize = 0.14f, DebrisColor = Clod,
		ReachSeconds = 0.5f,
		ScorchRadius = 2f, ScorchSeconds = 10f,
	};

	private static readonly ExplosionProfile DroneProfile = new()
	{
		FireballRadius = 1.7f, FireballSeconds = 0.3f,
		FlashEnergy = 8f, FlashRange = 12f, FlashSeconds = 0.22f,
		SmokePuffs = 6, SmokeRadius = 1.6f, SmokeSeconds = 2f, SmokeEmitSeconds = 0.15f, SmokeRiseSpeed = 1.4f,
		SmokeColor = Smoke,
		DebrisPieces = 10, DebrisSpeed = 9f, DebrisSeconds = 1.2f, DebrisSize = 0.08f, DebrisColor = Metal,
		ReachSeconds = 0.45f,
		ScorchRadius = 1.5f, ScorchSeconds = 8f,
	};

	private static readonly ExplosionProfile VehicleProfile = new()
	{
		FireballRadius = 3f, FireballSeconds = 0.6f,
		FlashEnergy = 12f, FlashRange = 22f, FlashSeconds = 0.4f,
		SmokePuffs = 14, SmokeRadius = 2.2f, SmokeSeconds = 3.5f, SmokeEmitSeconds = 6f, SmokeRiseSpeed = 2.4f,
		SmokeColor = BlackSmoke,
		DebrisPieces = 16, DebrisSpeed = 11f, DebrisSeconds = 2f, DebrisSize = 0.3f, DebrisColor = Metal,
		ReachSeconds = 0f,
		ScorchRadius = 3f, ScorchSeconds = 14f,
		BurnSeconds = 7f,
	};

	/// <summary>Every kind that draws something, in the enum's order.</summary>
	public static readonly ExplosionKind[] Drawn =
	{
		ExplosionKind.HandGrenade, ExplosionKind.LauncherGrenade, ExplosionKind.Shell, ExplosionKind.Drone,
		ExplosionKind.Vehicle,
	};

	/// <summary>
	/// A kind's name, as the console takes it and as an artist's scene for it is called
	/// (<c>res://Effects/explosion_&lt;name&gt;.tscn</c>, docs/EFFECTS.md §4); empty for <see cref="ExplosionKind.None"/>.
	/// </summary>
	public static string NameOf(ExplosionKind kind) => kind switch
	{
		ExplosionKind.HandGrenade => "hand_grenade",
		ExplosionKind.LauncherGrenade => "launcher_grenade",
		ExplosionKind.Shell => "shell",
		ExplosionKind.Drone => "drone",
		ExplosionKind.Vehicle => "vehicle",
		_ => string.Empty,
	};

	/// <summary>The kind <see cref="NameOf"/> calls <paramref name="name"/>, ignoring case; false for anything else, <c>none</c> included.</summary>
	public static bool TryParse(string name, out ExplosionKind kind)
	{
		foreach (ExplosionKind candidate in Drawn)
		{
			if (string.Equals(NameOf(candidate), name?.Trim(), StringComparison.OrdinalIgnoreCase))
			{
				kind = candidate;
				return true;
			}
		}

		kind = ExplosionKind.None;
		return false;
	}

	/// <summary>The numbers <paramref name="kind"/> is drawn with; nothing at all for <see cref="ExplosionKind.None"/> or an unknown value.</summary>
	public static ExplosionProfile Profile(ExplosionKind kind) => kind switch
	{
		ExplosionKind.HandGrenade => HandGrenadeProfile,
		ExplosionKind.LauncherGrenade => LauncherProfile,
		ExplosionKind.Shell => ShellProfile,
		ExplosionKind.Drone => DroneProfile,
		ExplosionKind.Vehicle => VehicleProfile,
		_ => default,
	};

	/// <summary>
	/// What a hit message is drawn as (docs/EFFECTS.md §2.1). Nothing unless it went off:
	/// a round that only stopped is the impact marker, a grenade that landed is still
	/// lying there, a swing is a swing. One that went off is its round's
	/// <paramref name="round"/>, or <see cref="Unidentified"/> when this peer cannot say
	/// which round that was.
	/// </summary>
	public static ExplosionKind ForHit(HitFlags flags, ExplosionKind round)
	{
		if ((flags & HitFlags.Exploded) == 0 || (flags & (HitFlags.Landed | HitFlags.Melee)) != 0)
		{
			return ExplosionKind.None;
		}

		return round == ExplosionKind.None ? Unidentified : round;
	}

	/// <summary>How big a vehicle's wreck is drawn, from its hull radius: the tank's is one.</summary>
	public static float HullScale(float hullRadiusMeters) =>
		float.IsFinite(hullRadiusMeters) && hullRadiusMeters > 0f
			? Math.Clamp(hullRadiusMeters / ReferenceHullRadius, MinHullScale, MaxHullScale)
			: 1f;

	// ---- curves ---------------------------------------------------------------------------

	/// <summary>How far along a part of <paramref name="seconds"/> is at <paramref name="age"/>: 0 to 1, clamped.</summary>
	public static float Progress(float age, float seconds) =>
		seconds > 0f ? Math.Clamp(age / seconds, 0f, 1f) : 1f;

	/// <summary>
	/// A fireball's radius as a share of its full size: a third at once, and the rest
	/// fast then slow (an ease-out), full at the end of its time.
	/// </summary>
	public static float FireballGrowth(float age, float seconds)
	{
		float t = Progress(age, seconds);
		float eased = 1f - ((1f - t) * (1f - t) * (1f - t));
		return 0.35f + (0.65f * eased);
	}

	/// <summary>
	/// Opacity over a part's life: whole for the first <paramref name="hold"/> of it,
	/// then down in a straight line to nothing at its end. Nothing outside it.
	/// </summary>
	public static float Fade(float age, float seconds, float hold)
	{
		if (seconds <= 0f || age < 0f || age >= seconds)
		{
			return 0f;
		}

		float holdSeconds = seconds * Math.Clamp(hold, 0f, 1f);
		if (age <= holdSeconds)
		{
			return 1f;
		}

		return 1f - ((age - holdSeconds) / (seconds - holdSeconds));
	}

	/// <summary>The flash's brightness as a share of its peak: all of it at once, falling off as a square.</summary>
	public static float Flash(float age, float seconds)
	{
		if (seconds <= 0f || age < 0f || age >= seconds)
		{
			return 0f;
		}

		float left = 1f - (age / seconds);
		return left * left;
	}

	/// <summary>The reach ring's radius as a share of the blast's: fast then slow, all of it at the end.</summary>
	public static float Reach(float age, float seconds)
	{
		float t = Progress(age, seconds);
		return 1f - ((1f - t) * (1f - t));
	}

	/// <summary>
	/// The fireball's colour at a share <paramref name="t"/> of its life: white-hot,
	/// through orange, to a dull red as it goes out.
	/// </summary>
	public static Color FireballColor(float t)
	{
		t = Math.Clamp(t, 0f, 1f);
		Color hot = new(1f, 0.95f, 0.75f);
		Color orange = new(1f, 0.55f, 0.15f);
		Color ember = new(0.55f, 0.12f, 0.05f);
		return t < 0.4f ? hot.Lerp(orange, t / 0.4f) : orange.Lerp(ember, (t - 0.4f) / 0.6f);
	}

	/// <summary>
	/// When puff <paramref name="index"/> of <paramref name="count"/> is let out:
	/// evenly over <paramref name="emitSeconds"/>, the first at once.
	/// </summary>
	public static float PuffDelay(int index, int count, float emitSeconds) =>
		count > 1 && emitSeconds > 0f ? emitSeconds * Math.Clamp(index, 0, count - 1) / (count - 1) : 0f;
}

/// <summary>
/// The rounds a client has seen fired that go off, by id, so that a hit message — which
/// names a round by id alone (docs/NETCODE.md §7) — can be drawn as the round it was.
/// The projectile itself is no help by then for the one that matters most: a client's
/// copy of a hand grenade runs out its fuse and is gone on its own clock, a round trip
/// before the server's word that it went off arrives.
///
/// A fixed ring: the oldest entry is forgotten first, and nothing is allocated after
/// construction. Only explosive rounds are noted, so it need only be as long as the
/// number that can be in the air at once.
/// </summary>
public sealed class RecentRounds
{
	private readonly uint[] _ids;
	private readonly byte[] _definitions;
	private int _next;

	public RecentRounds(int capacity = 64)
	{
		Capacity = Math.Max(capacity, 1);
		_ids = new uint[Capacity];
		_definitions = new byte[Capacity];
	}

	public int Capacity { get; }

	/// <summary>Remembers which round <paramref name="id"/> is. Id 0 is nobody's and is not noted.</summary>
	public void Note(uint id, byte definitionId)
	{
		if (id == 0)
		{
			return;
		}

		_ids[_next] = id;
		_definitions[_next] = definitionId;
		_next = (_next + 1) % Capacity;
	}

	/// <summary>Which round <paramref name="id"/> was, newest note first; false once it has been forgotten, or never seen.</summary>
	public bool TryGet(uint id, out byte definitionId)
	{
		definitionId = 0;
		if (id == 0)
		{
			return false;
		}

		for (int n = 1; n <= Capacity; n++)
		{
			int i = (_next - n + Capacity) % Capacity;
			if (_ids[i] == id)
			{
				definitionId = _definitions[i];
				return true;
			}
		}

		return false;
	}
}

/// <summary>
/// A fixed number of reusable effects of one kind (docs/EFFECTS.md §4): the first free
/// one, else a new one while there is room, else the oldest still playing, taken off
/// whatever it was doing. Effects are made on first need and kept, so a fight allocates
/// nothing once its first few explosions have been drawn.
/// </summary>
public sealed class EffectPool<T> where T : class
{
	private readonly Func<T> _create;
	private readonly T[] _items;
	private readonly bool[] _active;
	private readonly ulong[] _startedAt;
	private ulong _sequence;

	public EffectPool(int capacity, Func<T> create)
	{
		_create = create ?? throw new ArgumentNullException(nameof(create));
		Capacity = Math.Max(capacity, 1);
		_items = new T[Capacity];
		_active = new bool[Capacity];
		_startedAt = new ulong[Capacity];
	}

	public int Capacity { get; }

	/// <summary>How many effects have been made. Slots past it are empty.</summary>
	public int Created { get; private set; }

	public int ActiveCount { get; private set; }

	/// <summary>Effects that had to be cut short for a new one: anything but 0 means the capacity is low for the fight.</summary>
	public int Evictions { get; private set; }

	public T At(int index) => index >= 0 && index < Created ? _items[index] : null;

	public bool IsActive(int index) => index >= 0 && index < Created && _active[index];

	/// <summary>
	/// An effect to play, marked active. <paramref name="evicted"/> says it was still
	/// playing something else, which the caller restarts rather than finishes.
	/// </summary>
	public T Acquire(out int index, out bool evicted)
	{
		evicted = false;
		index = -1;

		for (int i = 0; i < Created; i++)
		{
			if (!_active[i])
			{
				index = i;
				break;
			}
		}

		if (index < 0 && Created < Capacity)
		{
			index = Created;
			_items[index] = _create();
			Created++;
		}

		if (index < 0)
		{
			index = 0;
			for (int i = 1; i < Created; i++)
			{
				if (_startedAt[i] < _startedAt[index])
				{
					index = i;
				}
			}

			evicted = true;
			Evictions++;
		}
		else
		{
			ActiveCount++;
		}

		_active[index] = true;
		_startedAt[index] = ++_sequence;
		return _items[index];
	}

	/// <summary>Puts an effect back; false for one that was not playing.</summary>
	public bool Release(int index)
	{
		if (!IsActive(index))
		{
			return false;
		}

		_active[index] = false;
		ActiveCount--;
		return true;
	}

	/// <summary>Puts every effect back, made ones kept for next time.</summary>
	public void ReleaseAll()
	{
		Array.Clear(_active);
		ActiveCount = 0;
	}
}
