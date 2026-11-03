using System;
using Godot;

namespace Gdpyr.Sim;

/// <summary>
/// What a strategist is told about the crew vehicles (docs/GROUND_VEHICLES.md §7.3,
/// GV-D14): the players' rule. A vehicle is carried in a strategist's packet while the
/// side's sensors see its hull's centre with a clear line; and a player in one of its
/// seats is carried in the player snapshot exactly when the vehicle is, so the two
/// messages never disagree about who is in it. Everybody else — the ground force, a
/// spectator — is told about every vehicle.
///
/// The seeing itself is <c>VisibilityService</c>'s, against the engine's walls; this is
/// the half that decides what goes in which packet from what it saw.
/// </summary>
public static class CrewFog
{
	/// <summary>Whether a vehicle's record goes in a packet for a viewer on <paramref name="viewerTeam"/>.</summary>
	public static bool Carries(Team viewerTeam, bool vehicleSeen) => viewerTeam != Team.Strategist || vehicleSeen;

	/// <summary>
	/// Whether a strategist may be told where a player is: a seated one exactly when their
	/// vehicle is seen, whatever their own capsule would show; anybody else by their own.
	/// </summary>
	public static bool PlayerSeen(bool seated, bool vehicleSeen, bool playerSeen) => seated ? vehicleSeen : playerSeen;

	/// <summary>
	/// The vehicle records a viewer on <paramref name="viewerTeam"/> is sent, copied into
	/// <paramref name="into"/> in order; <paramref name="seenBySlot"/> is whether the side's
	/// sensors see each slot's hull. Returns how many.
	/// </summary>
	public static int Filter(Team viewerTeam, ReadOnlySpan<CrewVehicleRecord> all, ReadOnlySpan<bool> seenBySlot,
		Span<CrewVehicleRecord> into)
	{
		int count = 0;
		for (int i = 0; i < all.Length && count < into.Length; i++)
		{
			int slot = all[i].Slot;
			bool seen = slot < seenBySlot.Length && seenBySlot[slot];
			if (Carries(viewerTeam, seen))
			{
				into[count++] = all[i];
			}
		}

		return count;
	}

	/// <summary>
	/// The point a vehicle is seen by (§7.3): its hull's centre — the middle of the box
	/// round every one of its hull boxes, in its own frame — where <paramref name="pose"/>
	/// puts it.
	/// </summary>
	public static Vector3 HullCentre(ReadOnlySpan<HullBox> hull, in CrewPose pose) =>
		pose.ToWorld(CrewLayout.HullCentre(hull));
}
