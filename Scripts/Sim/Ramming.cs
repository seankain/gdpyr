using System;
using Godot;

namespace Gdpyr.Sim;

/// <summary>
/// Running the strategist's units over (docs/GROUND_VEHICLES.md §5.9, GV-D17): when a
/// crew vehicle's hull touches a unit, how fast it was going where it touched, whether
/// that kills, and whose kill it is.
///
/// Engine-free, so the threshold is a fact with a test rather than a feeling from a
/// playtest. The engine half finds the units near each vehicle and deals the death; the
/// vehicle's own motion is never changed by it, so a driver's prediction never depends
/// on units their client sees a round trip late.
/// </summary>
public static class Ramming
{
	/// <summary>How long after leaving the controls a driver is still credited [s].</summary>
	public const float CreditSeconds = 3f;

	public const int CreditTicks = (int)(CreditSeconds * SimConfig.TickRate);

	/// <summary>
	/// Below the kill speed by this little still kills: float noise in a speed that was
	/// meant to be exactly it [m/s].
	/// </summary>
	public const float SpeedToleranceMetersPerSecond = 1e-3f;

	/// <summary>
	/// Whether a unit's capsule touches the hull (§5.9): its axis passes within its
	/// radius of one of the boxes — the slab test with the radius as the sweep — or starts
	/// inside one. Writes the box it touches, the first in hull order, or −1.
	/// </summary>
	public static bool Touches(ReadOnlySpan<HitObb> hull, in HitCapsule unit, out int box)
	{
		for (int i = 0; i < hull.Length; i++)
		{
			if (Hitbox.SegmentIntersects(hull[i], unit.Bottom, unit.Top, unit.Radius, out _))
			{
				box = i;
				return true;
			}
		}

		box = -1;
		return false;
	}

	/// <summary>
	/// The hull's speed towards the unit where it touches (§5.9): the vehicle's velocity
	/// plus what its yaw rate sweeps at <paramref name="contact"/> — both flat — taken
	/// along the flat line from the contact to <paramref name="unit"/>'s centre. Zero or
	/// less is no impact. The unit's own velocity is left out: a rifleman walking into a
	/// parked truck walks into a wall.
	///
	/// A unit whose centre is at the contact — inside the hull — is measured along the
	/// line from the vehicle's centre, and one at that too along the vehicle's motion.
	/// </summary>
	public static float ImpactSpeed(Vector3 vehicleCentre, Vector3 velocity, float yawRate, Vector3 contact,
		Vector3 unit)
	{
		Vector3 r = contact - vehicleCentre;

		// ω × r for ω = (0, yawRate, 0), flat.
		var swept = new Vector3(yawRate * r.Z, 0f, -yawRate * r.X);
		var hull = new Vector3(velocity.X, 0f, velocity.Z) + swept;

		Vector3 toward = Flat(unit - contact);
		if (toward.LengthSquared() < 1e-8f)
		{
			toward = Flat(unit - vehicleCentre);
		}

		if (toward.LengthSquared() < 1e-8f)
		{
			toward = hull;
		}

		if (toward.LengthSquared() < 1e-12f || !float.IsFinite(toward.X) || !float.IsFinite(toward.Z))
		{
			return 0f;
		}

		float speed = hull.Dot(toward.Normalized());
		return float.IsFinite(speed) && speed > 0f ? speed : 0f;
	}

	/// <summary>
	/// The hull's speed into a unit it touches: <see cref="ImpactSpeed"/> at the point of
	/// the touched box nearest the unit's capsule centre. Zero when it touches nothing.
	/// </summary>
	public static float ImpactSpeed(ReadOnlySpan<HitObb> hull, in HitCapsule unit, Vector3 vehicleCentre,
		Vector3 velocity, float yawRate)
	{
		if (!Touches(hull, unit, out int box))
		{
			return 0f;
		}

		Vector3 centre = unit.Center;
		Vector3 contact = Hitbox.ClosestPoint(hull[box], centre);
		return ImpactSpeed(vehicleCentre, velocity, yawRate, contact, centre);
	}

	/// <summary>
	/// Whether an impact kills (GV-D17): <paramref name="killSpeed"/> or more, and a unit
	/// on foot. The strategist's vehicles are never run over; slower, a unit is pushed
	/// aside unhurt by its own <c>MoveAndSlide</c>.
	/// </summary>
	public static bool Kills(float impactSpeed, float killSpeed, bool unitIsVehicle) =>
		!unitIsVehicle && float.IsFinite(impactSpeed) && impactSpeed > 0f
		&& impactSpeed >= killSpeed - SpeedToleranceMetersPerSecond;

	/// <summary>
	/// Whose kill it is (§5.9): whoever is at the controls; with nobody there, the last
	/// one, for <see cref="CreditTicks"/> after they left, inclusive; then nobody (0).
	/// <paramref name="leftTick"/> is the tick <paramref name="lastAtControls"/> left.
	/// </summary>
	public static int Credit(int atControls, int lastAtControls, uint leftTick, uint tick)
	{
		if (atControls != 0)
		{
			return atControls;
		}

		if (lastAtControls == 0 || tick < leftTick)
		{
			return 0;
		}

		return tick - leftTick <= (uint)CreditTicks ? lastAtControls : 0;
	}

	private static Vector3 Flat(Vector3 v) => new(v.X, 0f, v.Z);
}
