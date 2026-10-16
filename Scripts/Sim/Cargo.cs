using System;
using Godot;

namespace Gdpyr.Sim;

/// <summary>
/// Who is riding in one vehicle (docs/VEHICLES.md §4.1): its seats, and the ids of
/// the units in them, in the order they got in.
///
/// A rider stays a unit — same id, same registry entry, same health — so the hold
/// is only a list of ids, and the order is the one they are let out in. Fixed
/// storage, allocated once when a unit with seats is created; nothing per tick.
/// </summary>
public sealed class CargoHold
{
	public const int Capacity = SimConfig.MaxPassengers;

	private readonly ushort[] _riders = new ushort[Capacity];

	/// <summary>A hold of <paramref name="seats"/>, clamped to [0, <see cref="Capacity"/>]; 0 carries nobody.</summary>
	public CargoHold(int seats)
	{
		Seats = Math.Clamp(seats, 0, Capacity);
	}

	public int Seats { get; }

	public int Count { get; private set; }

	public int FreeSeats => Seats - Count;

	public bool IsEmpty => Count == 0;

	public bool IsFull => Count >= Seats;

	/// <summary>The <paramref name="ordinal"/>-th rider in boarding order, or 0 — never a unit — past the last.</summary>
	public ushort this[int ordinal] => (uint)ordinal < (uint)Count ? _riders[ordinal] : (ushort)0;

	/// <summary>Seats <paramref name="unitId"/>. False when the hold is full, the id is 0, or it is already aboard.</summary>
	public bool TryAdd(ushort unitId)
	{
		if (unitId == 0 || IsFull || IndexOf(unitId) >= 0)
		{
			return false;
		}

		_riders[Count++] = unitId;
		return true;
	}

	/// <summary>Lets <paramref name="unitId"/> out, keeping everybody else's order. False when it was not aboard.</summary>
	public bool Remove(ushort unitId)
	{
		int index = unitId == 0 ? -1 : IndexOf(unitId);
		if (index < 0)
		{
			return false;
		}

		for (int i = index + 1; i < Count; i++)
		{
			_riders[i - 1] = _riders[i];
		}

		Count--;
		_riders[Count] = 0;
		return true;
	}

	public bool Contains(ushort unitId) => unitId != 0 && IndexOf(unitId) >= 0;

	public void Clear()
	{
		Array.Clear(_riders);
		Count = 0;
	}

	private int IndexOf(ushort unitId)
	{
		for (int i = 0; i < Count; i++)
		{
			if (_riders[i] == unitId)
			{
				return i;
			}
		}

		return -1;
	}
}

/// <summary>
/// The rules for units getting into and out of a vehicle (docs/VEHICLES.md §4.1),
/// as pure functions: who gets a seat when more were sent than there are seats,
/// how close a unit has to be to get in, where it walks to before it does, where it
/// is put when it gets out, and what happens to it when the vehicle is destroyed
/// with it aboard.
///
/// Engine-free, like the resupply and training rules beside it. The engine half
/// (§5) asks the physics world whether a slot is actually clear; everything here is
/// the part that has an answer without one.
///
/// Positions are a unit's feet, as <c>Unit.GlobalPosition</c> is. Distances are
/// flat (XZ) unless a height is named, so a slope does not change who is in reach.
/// </summary>
public static class Cargo
{
	/// <summary>How far past touching — the vehicle's radius plus the unit's — a unit can still get in.</summary>
	public const float ReachSlackMeters = 1f;

	/// <summary>
	/// How far past touching a boarder walks to: inside <see cref="ReachSlackMeters"/>,
	/// so it is in reach when it gets there, and outside the vehicle's own body.
	/// </summary>
	public const float StandOffSlackMeters = 0.5f;

	/// <summary>How far past touching the first ring of exit slots is: a unit's width of room.</summary>
	public const float ExitClearanceMeters = 0.6f;

	/// <summary>Between two exit slots on a ring, and between the rings: the spacing a barracks' door uses.</summary>
	public const float ExitSpacingMeters = StructureTraining.SlotSpacingMeters;

	/// <summary>Rings of exit slots round a vehicle. Candidates past the last wrap to the first.</summary>
	public const int ExitRings = 2;

	/// <summary>
	/// Half the side of the square inside the perimeter walls (their inner faces are
	/// at ±119.5 m) that exit slots, landing spots and aircraft destinations are kept
	/// in. The ground outside the walls is a navigation island of its own, so no
	/// navmesh test keeps anything off it.
	/// </summary>
	public const float FieldHalfExtentMeters = 115f;

	/// <summary>How long a boarder walks to its vehicle before it gives up and holds where it is: 20 s.</summary>
	public const int BoardTimeoutTicks = SimConfig.TickRate * 20;

	/// <summary>
	/// The highest a vehicle can be off the ground when it is destroyed and its riders
	/// still get out alive (V-D7): the game's existing "on the ground", a capture pad's.
	/// </summary>
	public const float WreckSurvivableHeightMeters = CapturePad.VerticalToleranceMeters;

	/// <summary>The share of its maximum health a rider thrown from a wreck loses, as explosive damage.</summary>
	public const float WreckDamageFraction = 0.5f;

	/// <summary>
	/// Who of <paramref name="candidates"/> gets one of <paramref name="freeSeats"/>
	/// (V-D4): the nearest to the vehicle, ties to the lower id, then to the earlier
	/// entry. <paramref name="distances"/> runs beside <paramref name="candidates"/>
	/// — flat metres to the vehicle — and a missing or NaN one counts as furthest.
	///
	/// Writes the boarders, nearest first, into <paramref name="boarders"/> and
	/// returns how many: the smallest of the free seats, the candidates and the room
	/// in <paramref name="boarders"/>. Every other candidate is written, nearest
	/// first, into <paramref name="leftovers"/>, as many as fit. Ids are not checked:
	/// the caller passes distinct ids of units that can ride, as it filters them, and
	/// <see cref="CargoHold.TryAdd"/> refuses a duplicate in any case.
	///
	/// No allocation: each candidate's place is the number of candidates ahead of it,
	/// counted. Quadratic in a list no longer than a selection.
	/// </summary>
	public static int Admit(ReadOnlySpan<ushort> candidates, ReadOnlySpan<float> distances, int freeSeats,
		Span<ushort> boarders, Span<ushort> leftovers)
	{
		int boarding = Math.Min(Math.Min(Math.Max(freeSeats, 0), candidates.Length), boarders.Length);

		for (int i = 0; i < candidates.Length; i++)
		{
			float distance = DistanceOf(distances, i);
			int place = 0;
			for (int j = 0; j < candidates.Length; j++)
			{
				if (j != i && Precedes(candidates[j], DistanceOf(distances, j), j, candidates[i], distance, i))
				{
					place++;
				}
			}

			if (place < boarding)
			{
				boarders[place] = candidates[i];
			}
			else if (place - boarding < leftovers.Length)
			{
				leftovers[place - boarding] = candidates[i];
			}
		}

		return boarding;
	}

	/// <summary>How far, flat, from a vehicle's centre a unit can get in: both radii and <see cref="ReachSlackMeters"/>.</summary>
	public static float ReachMeters(float vehicleRadius, float unitRadius) =>
		NonNegative(vehicleRadius) + NonNegative(unitRadius) + ReachSlackMeters;

	/// <summary>
	/// Whether a unit with its feet at <paramref name="unit"/> can get into a vehicle
	/// with its feet at <paramref name="vehicle"/>: within <see cref="ReachMeters"/>
	/// measured flat, and within <see cref="CapturePad.VerticalToleranceMeters"/> of
	/// the vehicle's height. Flat, so a slope does not matter; banded, so a rifleman on
	/// a roof above a parked truck does not get in and later get out at street level,
	/// and nobody gets into a dropship that has not come down.
	/// </summary>
	public static bool InReach(Vector3 vehicle, float vehicleRadius, Vector3 unit, float unitRadius)
	{
		if (MathF.Abs(unit.Y - vehicle.Y) > CapturePad.VerticalToleranceMeters)
		{
			return false;
		}

		float reach = ReachMeters(vehicleRadius, unitRadius);
		return FlatDistanceSquared(vehicle, unit) <= reach * reach;
	}

	/// <summary>How far, flat, from a vehicle's centre a boarder walks to: both radii and <see cref="StandOffSlackMeters"/>.</summary>
	public static float StandOffMeters(float vehicleRadius, float unitRadius) =>
		NonNegative(vehicleRadius) + NonNegative(unitRadius) + StandOffSlackMeters;

	/// <summary>
	/// Where a boarder at <paramref name="unit"/> walks to: the nearest point to it on
	/// a ring <see cref="StandOffMeters"/> round the vehicle, which is inside
	/// <see cref="InReach"/>. Never the vehicle's centre — an airborne dropship is
	/// solid to nothing, and boarders standing under it are the bodies its landing
	/// waits for. A boarder already over the centre is sent out along +Z.
	///
	/// At the ground under the vehicle: <paramref name="heightAboveGround"/> below its
	/// feet, so a walker can arrive while a dropship is still coming down, and
	/// <see cref="InReach"/>, measured against the vehicle, still keeps it out until
	/// it has landed.
	/// </summary>
	public static Vector3 StandOff(Vector3 vehicle, Vector3 unit, float vehicleRadius, float unitRadius,
		float heightAboveGround = 0f)
	{
		Vector3 away = unit - vehicle;
		away.Y = 0f;
		away = away.LengthSquared() > 1e-6f ? away.Normalized() : Vector3.Back;

		Vector3 point = vehicle + (away * StandOffMeters(vehicleRadius, unitRadius));
		point.Y = vehicle.Y - NonNegative(heightAboveGround);
		return point;
	}

	/// <summary>
	/// Whether a point is inside the field — |x| and |z| at most
	/// <see cref="FieldHalfExtentMeters"/>. The one bound for exit slots, landing spots
	/// and aircraft destinations. A non-finite point is not.
	/// </summary>
	public static bool InField(Vector3 point) =>
		MathF.Abs(point.X) <= FieldHalfExtentMeters && MathF.Abs(point.Z) <= FieldHalfExtentMeters;

	/// <summary>How far, flat, the first ring of exit slots is from the vehicle's centre.</summary>
	public static float ExitRingMeters(float vehicleRadius, float passengerRadius) =>
		NonNegative(vehicleRadius) + NonNegative(passengerRadius) + ExitClearanceMeters;

	/// <summary>Exit-slot candidates round a vehicle, over every ring; <see cref="ExitSlot"/> wraps after this many.</summary>
	public static int ExitSlotCount(float vehicleRadius, float passengerRadius)
	{
		float inner = ExitRingMeters(vehicleRadius, passengerRadius);
		int count = 0;
		for (int ring = 0; ring < ExitRings; ring++)
		{
			count += SlotsOnRing(inner + (ring * ExitSpacingMeters));
		}

		return count;
	}

	/// <summary>
	/// The <paramref name="ordinal"/>-th place a passenger might get out at, round a
	/// vehicle at <paramref name="vehicle"/> facing <paramref name="yaw"/>: directly
	/// behind it first, then round both sides alternately, then in front, on a ring
	/// <see cref="ExitRingMeters"/> out with slots at least <see cref="ExitSpacingMeters"/>
	/// apart; then the same on a second ring a spacing further out. Past the last
	/// candidate it wraps. At the vehicle's height.
	///
	/// Candidates only: the engine half asks the world whether each one is in the
	/// field, in sight of the vehicle, on the ground, on the navigation mesh and clear
	/// of bodies, and takes the first that is (docs/VEHICLES.md §5.4).
	/// </summary>
	public static Vector3 ExitSlot(Vector3 vehicle, float yaw, float vehicleRadius, float passengerRadius, int ordinal)
	{
		float inner = ExitRingMeters(vehicleRadius, passengerRadius);
		int index = Math.Max(ordinal, 0) % ExitSlotCount(vehicleRadius, passengerRadius);

		for (int ring = 0; ring < ExitRings; ring++)
		{
			float radius = inner + (ring * ExitSpacingMeters);
			int slots = SlotsOnRing(radius);
			if (index >= slots)
			{
				index -= slots;
				continue;
			}

			// 0, +1, -1, +2, -2, … steps from straight behind: a row behind, the sides,
			// and the front last.
			int steps = (index + 1) / 2;
			float turn = (index % 2 == 1 ? steps : -steps) * (MathF.Tau / slots);

			// Behind is the opposite of Aim.Direction's forward, (-sin yaw, 0, -cos yaw).
			float bearing = yaw + turn;
			return vehicle + new Vector3(MathF.Sin(bearing) * radius, 0f, MathF.Cos(bearing) * radius);
		}

		return vehicle;
	}

	/// <summary>
	/// Whether a vehicle destroyed <paramref name="heightAboveGround"/> off the ground
	/// kills everybody aboard (V-D7): higher than
	/// <see cref="WreckSurvivableHeightMeters"/>, or a height that is not known. At
	/// exactly that height they get out.
	/// </summary>
	public static bool WreckKillsAll(float heightAboveGround) => !(heightAboveGround <= WreckSurvivableHeightMeters);

	/// <summary>What a rider thrown from a survivable wreck is dealt, as explosive damage: half its maximum health.</summary>
	public static float WreckDamage(float maxHealth) => NonNegative(maxHealth) * WreckDamageFraction;

	/// <summary>
	/// Slots that fit on a ring of <paramref name="radius"/> at least
	/// <see cref="ExitSpacingMeters"/> apart, measured straight across, spread evenly.
	/// </summary>
	private static int SlotsOnRing(float radius)
	{
		float half = MathF.Min(ExitSpacingMeters * 0.5f / MathF.Max(radius, 1e-3f), 1f);
		return Math.Max((int)MathF.Floor(MathF.PI / MathF.Asin(half)), 1);
	}

	private static bool Precedes(ushort idA, float distanceA, int indexA, ushort idB, float distanceB, int indexB)
	{
		if (distanceA != distanceB)
		{
			return distanceA < distanceB;
		}

		return idA != idB ? idA < idB : indexA < indexB;
	}

	private static float DistanceOf(ReadOnlySpan<float> distances, int index)
	{
		float distance = index < distances.Length ? distances[index] : float.PositiveInfinity;
		return float.IsNaN(distance) ? float.PositiveInfinity : distance;
	}

	private static float NonNegative(float value) => float.IsFinite(value) && value > 0f ? value : 0f;

	private static float FlatDistanceSquared(Vector3 a, Vector3 b)
	{
		float x = a.X - b.X;
		float z = a.Z - b.Z;
		return (x * x) + (z * z);
	}
}
