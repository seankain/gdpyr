using System;
using Godot;

namespace Gdpyr.Sim;

/// <summary>
/// Where a unit last saw its target, and until when it keeps shooting there
/// (<see cref="Suppression"/>). Server-side, one per unit, never replicated.
/// </summary>
public struct SuppressionMemory
{
	/// <summary>Whom it last saw, as an owner id (<see cref="Sim.OwnerId"/>); none for nobody.</summary>
	public int OwnerId;

	/// <summary>The point it was aiming at when it last saw them: the chest, or the head over a wall.</summary>
	public Vector3 Point;

	/// <summary>The last tick it keeps firing at <see cref="Point"/>; 0 for none.</summary>
	public uint UntilTick;

	public readonly bool IsSet => UntilTick != 0;
}

/// <summary>
/// Suppressive fire (docs/NETCODE.md §10.8), as pure functions: a unit with a
/// suppression window — the heavy gunner's <c>UnitDefinition.SuppressSeconds</c> —
/// that loses sight of a live target keeps firing at the point it last saw it for
/// that long, as though it still had a target there.
///
/// That is the whole of it, and it is enough to be the thing it is named for. A
/// player who goes to ground behind a wall finds the wall still being shot at, and
/// one who stands up to shoot back inside the window stands up into the burst; a
/// unit that stops when it stops seeing gives a player the moment to move that a
/// gunner does not. It works on what the unit was shown, not on what is true: it
/// keeps firing at the spot even when the player has crawled away from it, which
/// is what fire on a position is.
///
/// What it is not is a status on the player. Nothing here touches a player's aim
/// or view: a ground-force client predicts its own shots, and a spread the server
/// widened without telling it would be a misprediction on every round.
///
/// A unit with no window keeps nothing (<see cref="Remember"/> clears it), so every
/// other unit stops shooting when it stops seeing, as it always has.
/// </summary>
public static class Suppression
{
	/// <summary>
	/// Records that the unit sees <paramref name="ownerId"/> at <paramref name="point"/>
	/// this tick. Called on every tick it has a target; the window runs from the last.
	/// </summary>
	public static void Remember(ref SuppressionMemory memory, int ownerId, Vector3 point, uint tick, int windowTicks)
	{
		if (windowTicks <= 0 || ownerId == OwnerId.None)
		{
			memory = default;
			return;
		}

		memory.OwnerId = ownerId;
		memory.Point = point;
		memory.UntilTick = Math.Max(tick + (uint)windowTicks, 1u);
	}

	/// <summary>
	/// Where to keep firing on a tick the unit has no target: the remembered point,
	/// while the window is open, the player it named is still alive, and the point is
	/// inside <paramref name="rangeMeters"/> of <paramref name="eye"/>. False for
	/// everything else — including a unit that never suppresses.
	/// </summary>
	public static bool TryPoint(in SuppressionMemory memory, uint tick, bool targetAlive, Vector3 eye,
		float rangeMeters, out Vector3 point)
	{
		point = memory.Point;
		return memory.IsSet && tick <= memory.UntilTick && targetAlive
			&& eye.DistanceSquaredTo(memory.Point) <= rangeMeters * rangeMeters;
	}

	/// <summary>Forgets it: a new order, or a death. An order outranks the last thing the unit saw.</summary>
	public static void Clear(ref SuppressionMemory memory) => memory = default;
}
