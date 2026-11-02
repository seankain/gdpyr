using System;
using Godot;

namespace Gdpyr.Sim;

/// <summary>One resource node, as forward deployment sees it (docs/CONTROL_POINTS.md §2).</summary>
public readonly struct DeploySite
{
	public readonly Vector3 Position;

	/// <summary>The node's capture radius: its pad.</summary>
	public readonly float RadiusMeters;

	public readonly NodeHolder Owner;

	/// <summary>Somebody from the ground force is standing on it (<see cref="CaptureState.GroundPresent"/>).</summary>
	public readonly bool Denied;

	public DeploySite(Vector3 position, float radiusMeters, NodeHolder owner, bool denied)
	{
		Position = position;
		RadiusMeters = MathF.Max(radiusMeters, 0f);
		Owner = owner;
		Denied = denied;
	}

	/// <summary>Whether a barracks may put a unit out here now: the strategist's, and nobody from the ground force on it.</summary>
	public bool Deployable => ForwardDeploy.CanDeploy(Owner, Denied);
}

/// <summary>
/// Where a barracks puts a finished unit: at its own door, or out at a resource node
/// the strategist holds (docs/CONTROL_POINTS.md §2).
///
/// The rule is the rally point. A barracks whose rally point is on a held node's pad
/// deploys there instead of at the door, so a strategist who sends the next units to a
/// node they hold gets them at the node rather than a walk away from it — the same
/// right-click that set the rally point always set where they went, and now it sets
/// where they come from too. A node with anybody from the ground force standing on it
/// deploys nothing, on the same terms as it pays nothing (<see cref="CaptureState"/>):
/// one rifleman on a pad is enough to make the strategist walk.
///
/// Engine-free, like the capture it reads: what "held" and "denied" mean is decided
/// here once, and the server only asks.
/// </summary>
public static class ForwardDeploy
{
	/// <summary>No node: the barracks' own door.</summary>
	public const int Door = -1;

	/// <summary>The innermost ring of deploy slots round a node's middle [m]. Clear of a 1.3 m tank's hull at the centre.</summary>
	public const float FirstRingMeters = 2.5f;

	/// <summary>Between two rings, and the least distance between two slots on one [m].</summary>
	public const float SpacingMeters = 1.8f;

	/// <summary>Rings of slots. Three at 2.5, 4.3 and 6.1 m: every slot is on a 10 m pad.</summary>
	public const int Rings = 3;

	/// <summary>Whether a node held by <paramref name="owner"/>, with or without the ground force on it, deploys.</summary>
	public static bool CanDeploy(NodeHolder owner, bool denied) => owner == NodeHolder.Strategist && !denied;

	/// <summary>
	/// The node a barracks with its rally point at <paramref name="rally"/> deploys at,
	/// or <see cref="Door"/>: of the deployable nodes whose pad the rally point is on,
	/// the one whose middle is nearest it. The lowest index wins a tie, so every peer
	/// that asks gets the same node.
	/// </summary>
	public static int Choose(Vector3 rally, ReadOnlySpan<DeploySite> sites)
	{
		int best = Door;
		float bestDistance = float.MaxValue;

		for (int i = 0; i < sites.Length; i++)
		{
			DeploySite site = sites[i];
			if (!site.Deployable || !CapturePad.Covers(site.Position, site.RadiusMeters, rally))
			{
				continue;
			}

			float distance = Flat(site.Position, rally);
			if (distance < bestDistance)
			{
				best = i;
				bestDistance = distance;
			}
		}

		return best;
	}

	/// <summary>
	/// The node whose pad <paramref name="point"/> is on, held or not, nearest middle
	/// first; <see cref="Door"/> for none. What the strategist's HUD names a rally point
	/// by before the node under it is theirs.
	/// </summary>
	public static int PadUnder(Vector3 point, ReadOnlySpan<DeploySite> sites)
	{
		int best = Door;
		float bestDistance = float.MaxValue;

		for (int i = 0; i < sites.Length; i++)
		{
			if (!CapturePad.Covers(sites[i].Position, sites[i].RadiusMeters, point))
			{
				continue;
			}

			float distance = Flat(sites[i].Position, point);
			if (distance < bestDistance)
			{
				best = i;
				bestDistance = distance;
			}
		}

		return best;
	}

	/// <summary>Slot candidates round a node, over every ring; <see cref="Slot"/> wraps after this many.</summary>
	public static int SlotCount
	{
		get
		{
			int count = 0;
			for (int ring = 0; ring < Rings; ring++)
			{
				count += SlotsOnRing(RingMeters(ring));
			}

			return count;
		}
	}

	/// <summary>
	/// The <paramref name="ordinal"/>-th place a unit may be put round a node's middle:
	/// the inner ring first, starting on the side facing <paramref name="towards"/> —
	/// the rally point, so the first units out are already on their way — and working
	/// round both ways from there. Rings rather than rows because a node has no door and
	/// no building to keep out of; the server still asks the world whether each slot is
	/// clear, because a node a strategist holds is the node it has built a pillbox on.
	/// </summary>
	public static Vector3 Slot(Vector3 centre, Vector3 towards, int ordinal)
	{
		int index = Math.Max(ordinal, 0) % SlotCount;

		float heading = 0f;
		Vector3 facing = towards - centre;
		facing.Y = 0f;
		if (facing.LengthSquared() > 1e-4f)
		{
			heading = MathF.Atan2(facing.X, facing.Z);
		}

		for (int ring = 0; ring < Rings; ring++)
		{
			float radius = RingMeters(ring);
			int slots = SlotsOnRing(radius);
			if (index >= slots)
			{
				index -= slots;
				continue;
			}

			// 0, +1, -1, +2, -2 ...: alternate either side of the facing.
			int step = (index + 1) / 2;
			float sign = (index & 1) == 1 ? 1f : -1f;
			float angle = heading + (sign * step * MathF.Tau / slots);
			return centre + new Vector3(MathF.Sin(angle) * radius, 0f, MathF.Cos(angle) * radius);
		}

		return centre;
	}

	private static float RingMeters(int ring) => FirstRingMeters + (ring * SpacingMeters);

	/// <summary>How many slots fit on a ring at least <see cref="SpacingMeters"/> apart.</summary>
	private static int SlotsOnRing(float radius) =>
		Math.Max(1, (int)MathF.Floor(MathF.Tau * radius / SpacingMeters));

	private static float Flat(Vector3 a, Vector3 b)
	{
		float x = a.X - b.X;
		float z = a.Z - b.Z;
		return MathF.Sqrt((x * x) + (z * z));
	}
}
