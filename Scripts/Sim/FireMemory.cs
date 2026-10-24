using System;
using Godot;

namespace Gdpyr.Sim;

/// <summary>How close a round came to a unit (docs/TACTICS.md §4.1). Ordered by how much it matters.</summary>
public enum FireKind : byte
{
	None = 0,

	/// <summary>
	/// A round passed within <see cref="NearMiss.Meters"/> of its body, or a grenade
	/// came to rest within its blast's reach of it.
	/// </summary>
	NearMiss = 1,

	/// <summary>A round struck it, or a blast reached it.</summary>
	Hit = 2,
}

/// <summary>
/// A round as a unit it was fired at knows it: where it came from, and enough to
/// seed that unit's error about where. Who fired it is carried for the unit to look
/// for, never to know where they are now (docs/TACTICS.md §2 rule 4).
/// </summary>
public readonly struct FireSource
{
	/// <summary>Where it left the muzzle, the hand, or the swing.</summary>
	public readonly Vector3 Origin;

	/// <summary>Whose round, as an <see cref="Sim.OwnerId"/>.</summary>
	public readonly int OwnerId;

	public readonly byte DefinitionId;

	/// <summary>The projectile's id, or a tick for a swing: half the seed of the error.</summary>
	public readonly uint RoundId;

	public FireSource(Vector3 origin, int ownerId, byte definitionId, uint roundId)
	{
		Origin = origin;
		OwnerId = ownerId;
		DefinitionId = definitionId;
		RoundId = roundId;
	}
}

/// <summary>
/// What one unit knows about being shot at (docs/TACTICS.md §4.1): the worst that
/// has come near it this window, where it reckons the latest round came from, and
/// whose it was. Server-side, one per unit, never replicated.
/// </summary>
public struct FireMemory
{
	/// <summary>The worst this window: a hit outranks a near miss.</summary>
	public FireKind Kind;

	/// <summary>Where the latest round came from, as the unit perceives it (<see cref="FiredUpon.Perceive"/>).</summary>
	public Vector3 Origin;

	/// <summary>Whose the latest round was.</summary>
	public int ShooterOwnerId;

	/// <summary>The tick of the latest round.</summary>
	public uint LastTick;

	/// <summary>
	/// The tick of the latest round that hit, in any window; 0 for none this life. What
	/// tells a unit whose cap on reacting is spent that it has been hit since
	/// (docs/TACTICS.md §5.1), which a near miss in a window that already holds a hit does not.
	/// </summary>
	public uint LastHitTick;

	/// <summary>Rounds this window, counting to <see cref="ushort.MaxValue"/> and no further.</summary>
	public ushort Rounds;

	public readonly bool IsSet => Rounds > 0;
}

/// <summary>
/// Being fired upon (docs/TACTICS.md §4.1), as pure functions over a
/// <see cref="FireMemory"/>, in the way <see cref="Suppression"/> is over its
/// memory. A window opens with the first round and stays open while rounds keep
/// coming within <see cref="WindowTicks"/> of each other; a round after it has
/// closed opens a new one.
///
/// The origin a unit keeps is not the true one. It is where the round came from
/// moved along the line from the unit by up to <see cref="ErrorFraction"/> of the
/// range, in or out: a unit knows which way a shot came from, and roughly how far
/// (decision T-D3). The error is seeded from the unit and the round, so a replay
/// perceives the same.
/// </summary>
public static class FiredUpon
{
	/// <summary>How long after the latest round a unit still counts as fired upon: 3 s.</summary>
	public const int WindowTicks = SimConfig.TickRate * 3;

	/// <summary>How far off, as a share of the range, a unit's sense of where a shot came from may be.</summary>
	public const float ErrorFraction = 0.1f;

	/// <summary>
	/// Records a round at <paramref name="tick"/>. A round outside the window starts a
	/// new one; inside it the kind is the worse of the two, and the origin and the
	/// shooter are the latest's.
	/// </summary>
	public static void Record(ref FireMemory memory, FireKind kind, Vector3 perceivedOrigin, int shooterOwnerId,
		uint tick, int windowTicks = WindowTicks)
	{
		if (kind == FireKind.None)
		{
			return;
		}

		if (!IsRecent(memory, tick, windowTicks))
		{
			uint lastHit = memory.LastHitTick;
			memory = default;
			memory.LastHitTick = lastHit;
		}

		if (kind == FireKind.Hit)
		{
			memory.LastHitTick = tick;
		}

		memory.Kind = kind > memory.Kind ? kind : memory.Kind;
		memory.Origin = perceivedOrigin;
		memory.ShooterOwnerId = shooterOwnerId;
		memory.LastTick = tick;
		if (memory.Rounds < ushort.MaxValue)
		{
			memory.Rounds++;
		}
	}

	/// <summary>
	/// Whether the latest round was within <paramref name="windowTicks"/> of
	/// <paramref name="tick"/>. A memory from a later tick than asked about — a
	/// clock that went back with a new round — is not recent.
	/// </summary>
	public static bool IsRecent(in FireMemory memory, uint tick, int windowTicks = WindowTicks) =>
		memory.IsSet && tick >= memory.LastTick && tick - memory.LastTick <= (uint)Math.Max(windowTicks, 0);

	/// <summary>The worst this window, or <see cref="FireKind.None"/> once the window has closed.</summary>
	public static FireKind KindAt(in FireMemory memory, uint tick, int windowTicks = WindowTicks) =>
		IsRecent(memory, tick, windowTicks) ? memory.Kind : FireKind.None;

	/// <summary>
	/// Where <paramref name="observer"/> reckons a round from <paramref name="origin"/>
	/// came from: on the same line, at the range scaled by 1 ± up to
	/// <paramref name="errorFraction"/>. A round from where the observer stands is
	/// perceived where it is.
	/// </summary>
	public static Vector3 Perceive(Vector3 observer, Vector3 origin, float errorFraction, uint seed)
	{
		Vector3 line = origin - observer;
		float error = Math.Clamp(errorFraction, 0f, 1f);
		if (error <= 0f || line.LengthSquared() < 1e-6f)
		{
			return origin;
		}

		return observer + (line * (1f + (error * Spread.Signed(seed))));
	}

	/// <summary>The seed of one unit's error about one round.</summary>
	public static uint Seed(int observerOwnerId, in FireSource source) =>
		Spread.Seed(observerOwnerId, source.DefinitionId, source.RoundId);

	/// <summary>Forgets it: a new life.</summary>
	public static void Clear(ref FireMemory memory) => memory = default;
}
