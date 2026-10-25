using System;
using Gdpyr.Sim.Htn;
using Godot;

namespace Gdpyr.Sim;

/// <summary>
/// Where a heavy gunner fires when it sees nobody (docs/TACTICS.md §4.2, §5.2), as
/// numbers and pure functions over what the side remembers: no ray. A place an enemy is
/// seen now by any friend, was last seen, or fired from is a place to suppress, while
/// the gunner has a line to it over the cover board and it is inside the gun's reach.
/// The rhythm of the bursts is the planner's (<c>UnitPlanTraits</c>); the places are the
/// scan's.
/// </summary>
public static class GunnerPlan
{
	/// <summary>
	/// How far above where an enemy was recorded the gunner aims [m]: rounds aimed there
	/// strike the top of a low wall or the head over it.
	/// </summary>
	public const float SuppressAimMeters = 1.3f;

	/// <summary>
	/// How long after a round a place it came from is still one to suppress [ticks]: as long
	/// as the side remembers a ghost (<see cref="SimConfig.GhostLifetimeTicks"/>, 8 s).
	/// </summary>
	public const int FiredFromTicks = SimConfig.GhostLifetimeTicks;

	/// <summary>Two places within this of each other are one enemy [m]: a round from where a contact stands is that contact.</summary>
	public const float SameEnemyMeters = 3f;

	/// <summary>How many places one gunner keeps: every ground-force player, and where its last round came from.</summary>
	public const int Capacity = 9;

	/// <summary>
	/// The places one gunner might suppress, into <paramref name="into"/> (docs/TACTICS.md
	/// §4.2): every contact <paramref name="memory"/> still remembers, at its live position
	/// while a friend sees it and where it was last seen once it is a ghost, that
	/// <paramref name="alive"/> says has not died since; then where its last round came
	/// from, when <paramref name="firedFrom"/>. Returns how many, and in
	/// <paramref name="contacts"/> how many of those are contacts. A contact the side has
	/// forgotten is no place.
	/// </summary>
	public static int Gather(ContactMemory memory, uint tick, Func<int, bool> alive, bool firedFrom,
		Vector3 firedFromFeet, Span<Vector3> into, out int contacts)
	{
		int count = 0;
		int room = into.Length - (firedFrom ? 1 : 0);
		for (int i = 0; memory != null && i < memory.Count && count < room; i++)
		{
			if (memory.IsRemembered(i, tick) && (alive == null || alive(memory.At(i).OwnerId)))
			{
				into[count++] = memory.At(i).Position;
			}
		}

		contacts = count;
		if (firedFrom && count < into.Length)
		{
			into[count++] = firedFromFeet;
		}

		return count;
	}

	/// <summary>The point a burst at a place is aimed at: <see cref="SuppressAimMeters"/> above it.</summary>
	public static Vector3 AimPoint(Vector3 feet) => feet + (Vector3.Up * SuppressAimMeters);

	/// <summary>
	/// Whether the place at <paramref name="feet"/> is one to fire at from <paramref name="eye"/>:
	/// its aim point inside <paramref name="rangeMeters"/>, and no box on the board across
	/// the line to it — rounds that would only strike a block are not suppression.
	/// </summary>
	public static bool IsEntry(Vector3 eye, Vector3 feet, float rangeMeters, CoverBoard boxes)
	{
		Vector3 aim = AimPoint(feet);
		return eye.DistanceSquaredTo(aim) <= rangeMeters * rangeMeters && (boxes == null || boxes.ClearLine(eye, aim));
	}

	/// <summary>
	/// Keeps, in place, the places in <paramref name="feet"/> that are entries from
	/// <paramref name="eye"/> (<see cref="IsEntry"/>), nearest first, ties to the earlier;
	/// returns how many. The first <paramref name="contacts"/> are where the side saw an
	/// enemy; the rest are where rounds came from, and one within
	/// <see cref="SameEnemyMeters"/> of a contact is that contact and is dropped.
	/// </summary>
	public static int Entries(Span<Vector3> feet, int contacts, Vector3 eye, float rangeMeters, CoverBoard boxes)
	{
		// The rounds' places first, against the contacts as the side saw them, before any
		// contact is dropped or moved.
		contacts = Math.Clamp(contacts, 0, feet.Length);
		int count = contacts;
		for (int i = contacts; i < feet.Length; i++)
		{
			if (Near(feet[..contacts], feet[i]) < 0)
			{
				feet[count++] = feet[i];
			}
		}

		int kept = 0;
		for (int i = 0; i < count; i++)
		{
			Vector3 at = feet[i];
			if (!IsEntry(eye, at, rangeMeters, boxes))
			{
				continue;
			}

			// Insertion, nearest first: a stable sort over a handful of places.
			float meters = eye.DistanceSquaredTo(at);
			int slot = kept;
			while (slot > 0 && eye.DistanceSquaredTo(feet[slot - 1]) > meters)
			{
				slot--;
			}

			for (int k = kept; k > slot; k--)
			{
				feet[k] = feet[k - 1];
			}

			feet[slot] = at;
			kept++;
		}

		return kept;
	}

	/// <summary>
	/// The entry the next burst goes to (<paramref name="entries"/> nearest first): the
	/// nearest, then the next, and round again — the one after the entry the last burst
	/// was fired at when it is still listed, else the nearest. -1 for none.
	/// </summary>
	public static int Next(ReadOnlySpan<Vector3> entries, bool hasLast, Vector3 last)
	{
		if (entries.Length == 0)
		{
			return -1;
		}

		int at = hasLast ? Near(entries, last) : -1;
		return at < 0 ? 0 : (at + 1) % entries.Length;
	}

	/// <summary>The first place in <paramref name="places"/> within <see cref="SameEnemyMeters"/> of <paramref name="at"/>, measured flat; -1 for none.</summary>
	public static int Near(ReadOnlySpan<Vector3> places, Vector3 at)
	{
		for (int i = 0; i < places.Length; i++)
		{
			float x = places[i].X - at.X;
			float z = places[i].Z - at.Z;
			if ((x * x) + (z * z) <= SameEnemyMeters * SameEnemyMeters)
			{
				return i;
			}
		}

		return -1;
	}
}
