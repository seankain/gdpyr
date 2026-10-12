using System;
using Godot;

namespace Gdpyr.Sim;

/// <summary>
/// Where a structure's trained units come out, and where they walk to
/// (docs/NETCODE.md §10.8), as pure functions over its footprint.
///
/// A sniper tower and a pillbox train from a queue of their own, as a barracks
/// does (<see cref="BuildQueue"/>), but a barracks has a door the map authored and
/// a structure has four walls somebody put down wherever they liked. So the door
/// is worked out: the side facing home — the nearest friendly barracks — because
/// that is the side away from whatever the structure was put up to face, and a
/// unit that steps out towards the enemy steps out in front of its own gun. A side
/// with another structure's footprint on the door is passed over for the next,
/// round the walls; a structure boxed in on all four keeps the home side, and the
/// unit is shoved out of whatever it lands in, as a barracks' are.
/// </summary>
public static class StructureTraining
{
	/// <summary>How far from the wall the door is: clear of the collider, inside a builder's reach of it.</summary>
	public const float DoorStandoffMeters = 1.5f;

	/// <summary>How much room a door needs round it: a unit's radius and a little.</summary>
	public const float DoorClearanceMeters = 0.6f;

	/// <summary>Across the door between one fresh unit and the next, and between rows.</summary>
	public const float SlotSpacingMeters = 1.2f;

	public const int SlotsPerRow = 4;

	/// <summary>Rows before the slots wrap: fresh units walk off to the rally point, so the first row is empty again by then.</summary>
	public const int SlotRows = 2;

	/// <summary>How far out from the door fresh units walk when no rally point has been set.</summary>
	public const float RallyMeters = 6f;

	/// <summary>
	/// The door of <paramref name="footprint"/>: just outside the wall nearest
	/// <paramref name="home"/>, unless one of <paramref name="others"/> is on it, in
	/// which case the next side round — left of home, right of home, then away from
	/// it — that is clear. <paramref name="others"/> must not include the structure's own.
	/// </summary>
	public static Vector3 Door(in Footprint footprint, Vector3 home, ReadOnlySpan<Footprint> others)
	{
		Vector3 towards = home - footprint.Center;
		towards.Y = 0f;

		// Home is never inside it — a structure cannot be put on a barracks' door — but
		// any side will do if it is.
		towards = towards.LengthSquared() > 1e-6f ? towards.Normalized() : footprint.AxisZ;

		// Asked from outside the walls, so the answer is a door on the side asked about
		// rather than the nearest way out of the middle.
		towards *= footprint.HalfWidth + footprint.HalfDepth + DoorStandoffMeters;

		Vector3 first = footprint.ApproachPoint(footprint.Center + towards, DoorStandoffMeters);
		if (IsClear(first, others))
		{
			return first;
		}

		// Left, right, then behind: the rest of the walls, nearest home first.
		Vector3 left = new(-towards.Z, 0f, towards.X);
		for (int turn = 1; turn <= 3; turn++)
		{
			Vector3 direction = turn switch { 1 => left, 2 => -left, _ => -towards };
			Vector3 door = footprint.ApproachPoint(footprint.Center + direction, DoorStandoffMeters);
			if (IsClear(door, others))
			{
				return door;
			}
		}

		return first;
	}

	/// <summary>
	/// Where the <paramref name="ordinal"/>-th fresh unit stands at
	/// <paramref name="door"/>: rows along the wall, filling across and stepping
	/// outwards, wrapping after <see cref="SlotRows"/> rows.
	/// </summary>
	public static Vector3 Slot(in Footprint footprint, Vector3 door, int ordinal)
	{
		Vector3 outward = Outward(footprint, door);
		Vector3 across = new(outward.Z, 0f, -outward.X);

		int index = Math.Max(ordinal, 0) % (SlotsPerRow * SlotRows);
		int row = index / SlotsPerRow;
		float lateral = ((index % SlotsPerRow) - ((SlotsPerRow - 1) * 0.5f)) * SlotSpacingMeters;

		return door + (across * lateral) + (outward * (row * SlotSpacingMeters));
	}

	/// <summary>Where fresh units walk to when nobody has said: <see cref="RallyMeters"/> straight out from the door.</summary>
	public static Vector3 DefaultRally(in Footprint footprint, Vector3 door) =>
		door + (Outward(footprint, door) * RallyMeters);

	/// <summary>The flat direction from the structure's middle out through its door.</summary>
	private static Vector3 Outward(in Footprint footprint, Vector3 door)
	{
		Vector3 outward = door - footprint.Center;
		outward.Y = 0f;
		return outward.LengthSquared() > 1e-6f ? outward.Normalized() : footprint.AxisZ;
	}

	private static bool IsClear(Vector3 door, ReadOnlySpan<Footprint> others)
	{
		for (int i = 0; i < others.Length; i++)
		{
			if (others[i].Contains(door, DoorClearanceMeters))
			{
				return false;
			}
		}

		return true;
	}
}
