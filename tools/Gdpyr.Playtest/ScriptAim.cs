using System;

namespace Gdpyr.Playtest;

/// <summary>
/// A scripted ground seat's <c>look_at</c> (docs/GROUND_VEHICLES.md §11): the absolute look
/// that puts the seat's eye on a point of the map, worked out from where the seat's own
/// observations put it.
///
/// A script's look is otherwise a delta per decision, which holds a stationary seat on a
/// stationary target and nothing else: a passenger in a 4x4 at 18 m/s, 25 m from a
/// rifleman, sweeps through 0.7 rad/s at the closest point, and a fixed sweep is a degree
/// off — half a metre at the target — inside a second. This is what a policy aiming at a
/// point does with its own position, and it reads nothing a policy is not sent: the
/// observation's <c>self.position</c>, twice, for its own velocity, because
/// <c>self.velocity</c> is normalized to 16 m/s and saturates in a fast vehicle.
///
/// Angles are the game's look convention (<c>Scripts/Sim/Aim.cs</c>): yaw 0 looks along −Z
/// and turns about +Y, positive pitch looks up. Engine-free, and compiled into the tests.
/// </summary>
public static class ScriptAim
{
	/// <summary>
	/// Where a body's eye is above its origin on the authority the harness runs [m]:
	/// <c>Scenes/fps_controller.tscn</c>'s <c>CameraController</c> at rest, 0.596 m, where a
	/// headless server, which plays no animation, leaves it. A seated body's origin is put
	/// so this camera is at the seat's eye (<c>fps_controller.PinToSeat</c>), so it is the
	/// seat's eye too. Measured in GV7: a bed passenger's aim from 1.6 m — the drawn standing
	/// eye — read the seat's eye as 4.04 m up against the 3.05 m it is, and every round went
	/// into the ground at a rifleman's feet.
	/// </summary>
	public const float EyeHeightMeters = 0.596f;

	/// <summary>
	/// The look, as an absolute yaw and pitch [rad], from an eye that was at
	/// <paramref name="previous"/> and is now at <paramref name="now"/>,
	/// <paramref name="ticksBetween"/> ticks apart, to <paramref name="target"/>, led by its
	/// own motion over <paramref name="leadTicks"/> — the ticks between the observation and
	/// the shot. False, with the look unchanged, when the eye is on the target.
	/// </summary>
	public static bool Toward(ReadOnlySpan<float> now, ReadOnlySpan<float> previous, int ticksBetween,
		int leadTicks, ReadOnlySpan<float> target, out float yaw, out float pitch)
	{
		yaw = 0f;
		pitch = 0f;
		if (now.Length < 3 || target.Length < 3)
		{
			return false;
		}

		float lead = ticksBetween > 0 && previous.Length >= 3 ? leadTicks / (float)ticksBetween : 0f;
		float x = now[0] + ((now[0] - (previous.Length >= 3 ? previous[0] : now[0])) * lead);
		float y = now[1] + ((now[1] - (previous.Length >= 3 ? previous[1] : now[1])) * lead);
		float z = now[2] + ((now[2] - (previous.Length >= 3 ? previous[2] : now[2])) * lead);

		float dx = target[0] - x;
		float dy = target[1] - y;
		float dz = target[2] - z;
		float flat = MathF.Sqrt((dx * dx) + (dz * dz));
		if (flat < 1e-4f && MathF.Abs(dy) < 1e-4f)
		{
			return false;
		}

		yaw = MathF.Atan2(-dx, -dz);
		pitch = MathF.Atan2(dy, flat);
		return true;
	}
}
