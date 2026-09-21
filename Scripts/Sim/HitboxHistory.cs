using Godot;

namespace Gdpyr.Sim;

/// <summary>
/// The last half second of one damageable entity's hitbox, as a fixed ring
/// (docs/NETCODE.md §5).
///
/// The server records every entity's capsule every tick. Melee is validated
/// against the attacker's *own* view of the world — the tick their client was
/// looking at when they swung — rather than against where the target has since
/// moved, which is what makes a swing that visibly connected land.
///
/// Projectiles do not read this: they are lag-compensated at spawn instead, by
/// advancing them by the shooter's latency (docs/NETCODE.md §4.3). The ring is
/// built now regardless, because it is also the full-rewind upgrade's prerequisite
/// and the only record that can settle a disputed kill after the fact.
///
/// Fixed storage, no allocation after construction: this runs per entity per tick.
/// </summary>
public sealed class HitboxHistory
{
	/// <summary>Ticks retained. A power of two, so the ring index is a mask.</summary>
	public const int Capacity = SimConfig.HitboxHistoryTicks;

	private const int Mask = Capacity - 1;

	private readonly HitCapsule[] _capsules = new HitCapsule[Capacity];
	private readonly uint[] _ticks = new uint[Capacity];
	private readonly bool[] _filled = new bool[Capacity];

	/// <summary>The newest tick recorded, or 0 before anything has been.</summary>
	public uint NewestTick { get; private set; }

	/// <summary>True once at least one sample has been recorded.</summary>
	public bool HasSamples { get; private set; }

	public void Record(uint tick, in HitCapsule capsule)
	{
		int slot = (int)(tick & Mask);
		_capsules[slot] = capsule;
		_ticks[slot] = tick;
		_filled[slot] = true;

		if (!HasSamples || tick >= NewestTick)
		{
			NewestTick = tick;
			HasSamples = true;
		}
	}

	/// <summary>
	/// The capsule recorded for exactly <paramref name="tick"/>. False once that
	/// tick has aged out of the ring — a caller that wants a best effort should ask
	/// for <see cref="TryLatest"/> instead of interpolating a gap it cannot see.
	/// </summary>
	public bool TrySample(uint tick, out HitCapsule capsule)
	{
		int slot = (int)(tick & Mask);
		if (_filled[slot] && _ticks[slot] == tick)
		{
			capsule = _capsules[slot];
			return true;
		}

		capsule = default;
		return false;
	}

	/// <summary>
	/// The capsule for <paramref name="tick"/>, falling back to the newest sample
	/// when that tick is outside the ring. This is what a hit test wants: a
	/// compensation window that has run out is a reason to stop compensating, not a
	/// reason to stop registering hits.
	/// </summary>
	public bool TrySampleOrLatest(uint tick, out HitCapsule capsule)
	{
		if (TrySample(tick, out capsule))
		{
			return true;
		}
		return TryLatest(out _, out capsule);
	}

	public bool TryLatest(out uint tick, out HitCapsule capsule)
	{
		tick = NewestTick;
		if (HasSamples)
		{
			return TrySample(NewestTick, out capsule);
		}

		capsule = default;
		return false;
	}

	public void Clear()
	{
		for (int i = 0; i < Capacity; i++)
		{
			_filled[i] = false;
		}
		HasSamples = false;
		NewestTick = 0;
	}
}
