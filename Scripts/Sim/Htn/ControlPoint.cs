using System;
using Godot;

namespace Gdpyr.Sim.Htn;

/// <summary>
/// The units taking a control point (docs/HTN_BOTS.md §5.2, "take the point"): who of
/// them must stand on it, and how far out on it their posts may be.
///
/// Presence is boolean — one body takes a node, and more are no faster
/// (<see cref="CaptureState"/>) — so the first few on the pad hold it and the rest may
/// fight from cover just off it. Who holds is decided by position, not by who was
/// told to, so a holder that retreats, resupplies or dies is replaced by whoever comes
/// next: a unit on the pad holds while fewer than <c>holders</c> friendly units on it
/// have a lower id; a unit off the pad holds while fewer than <c>holders</c> friendly
/// units are on it. A unit leaving the pad always has a higher id than the holders
/// who stay, so leaving never changes who they are.
/// </summary>
public static class ControlPoint
{
	/// <summary>Whether the unit <paramref name="selfId"/> at <paramref name="self"/> must stand on the point.</summary>
	public static bool Holds(int selfId, Vector3 self, Vector3 centre, float radiusMeters,
		ReadOnlySpan<Neighbour> friends, int holders)
	{
		bool on = CapturePad.Covers(centre, radiusMeters, self);
		int count = 0;
		for (int i = 0; i < friends.Length; i++)
		{
			Neighbour friend = friends[i];
			if (friend.Id != selfId && (!on || friend.Id < selfId)
				&& CapturePad.Covers(centre, radiusMeters, friend.Position))
			{
				count++;
			}
		}

		return count < holders;
	}

	/// <summary>
	/// How far from the centre a unit's post on the point may be: in from the edge by
	/// what counts it as on the point (<paramref name="enterMeters"/>), by how far short
	/// of its destination it may stop (<paramref name="arrivalMeters"/>), and by
	/// <paramref name="marginMeters"/> more.
	/// </summary>
	public static float PostLimit(float radiusMeters, float enterMeters, float arrivalMeters, float marginMeters) =>
		MathF.Max(radiusMeters - enterMeters - arrivalMeters - marginMeters, 0f);
}
