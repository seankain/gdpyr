using System;
using Godot;

namespace Gdpyr.Sim;

/// <summary>
/// One side's sensors, and the only question the fog of war asks of them: is there
/// anything here that can see that point (docs/NETCODE.md §6.2)?
///
/// Engine-free and fixed-size, like everything else under <c>Scripts/Sim</c>: the
/// server rebuilds it from the live units several times a second, so it must not
/// allocate and must be answerable from a test rather than from two machines and a
/// guess (docs/IMPLEMENTATION_PLAN.md §3).
///
/// It knows nothing about walls. Line of sight costs a ray against the physics
/// world, which is the caller's business; what this does is narrow "which of fifty
/// units could possibly see this" down to the two or three worth spending a ray
/// on (<see cref="Gather"/>).
/// </summary>
public sealed class VisionField
{
	private readonly Vector3[] _origins;
	private readonly float[] _radii;
	private readonly float[] _radiiSquared;

	public VisionField(int capacity)
	{
		int size = Math.Max(capacity, 1);
		_origins = new Vector3[size];
		_radii = new float[size];
		_radiiSquared = new float[size];
	}

	public int Capacity => _origins.Length;

	public int Count { get; private set; }

	/// <summary>Sensors refused because the field was full. Anything but 0 is a sizing bug.</summary>
	public int Overflows { get; private set; }

	public void Clear() => Count = 0;

	/// <summary>
	/// Adds one sensor. Returns false for a sensor that cannot see anything or that
	/// the field has no room for — a radius of zero is a unit with no sensor, not an
	/// error.
	/// </summary>
	public bool Add(Vector3 origin, float radius)
	{
		if (radius <= 0f)
		{
			return false;
		}

		if (Count == _origins.Length)
		{
			Overflows++;
			return false;
		}

		_origins[Count] = origin;
		_radii[Count] = radius;
		_radiiSquared[Count] = radius * radius;
		Count++;
		return true;
	}

	public Vector3 OriginAt(int index) => index >= 0 && index < Count ? _origins[index] : Vector3.Zero;

	public float RadiusAt(int index) => index >= 0 && index < Count ? _radii[index] : 0f;

	/// <summary>Whether any sensor has <paramref name="point"/> inside its radius.</summary>
	public bool Sees(Vector3 point)
	{
		for (int i = 0; i < Count; i++)
		{
			if (point.DistanceSquaredTo(_origins[i]) <= _radiiSquared[i])
			{
				return true;
			}
		}

		return false;
	}

	/// <summary>
	/// The sensors covering <paramref name="point"/>, nearest first, as many as
	/// <paramref name="nearest"/> will hold; returns how many were written.
	///
	/// Nearest first and bounded because the caller pays a line-of-sight ray per
	/// candidate: the nearest sensor is both the likeliest to have a clear line and
	/// the one whose line is shortest. Testing every sensor that covers a point
	/// would make a crowd of units cost a ray each.
	/// </summary>
	public int Gather(Vector3 point, Span<int> nearest)
	{
		if (nearest.Length == 0)
		{
			return 0;
		}

		int found = 0;

		for (int i = 0; i < Count; i++)
		{
			float distance = point.DistanceSquaredTo(_origins[i]);
			if (distance > _radiiSquared[i])
			{
				continue;
			}

			int slot = found;
			if (slot == nearest.Length)
			{
				// The list is full: only a sensor nearer than the furthest one held is
				// worth displacing it.
				if (distance >= point.DistanceSquaredTo(_origins[nearest[slot - 1]]))
				{
					continue;
				}
				slot--;
			}
			else
			{
				found++;
			}

			while (slot > 0 && point.DistanceSquaredTo(_origins[nearest[slot - 1]]) > distance)
			{
				nearest[slot] = nearest[slot - 1];
				slot--;
			}

			nearest[slot] = i;
		}

		return found;
	}
}
