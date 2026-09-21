using System;
using Godot;

namespace Gdpyr.Sim;

/// <summary>
/// The one definition of where a character is looking.
///
/// It exists because three processes have to agree on it exactly: the client that
/// predicts a tracer, the server that decides what the shot hit, and every other
/// client that draws the round in flight. Deriving it from a camera node's basis
/// in one place and from Euler angles in another is how a shot ends up landing
/// somewhere nobody aimed.
/// </summary>
public static class Aim
{
	/// <summary>
	/// The unit direction for a pair of look angles, in Godot's convention: -Z is
	/// forward, yaw turns about +Y, positive pitch looks up.
	///
	/// This is <c>Basis.FromEuler(0, yaw, 0) * Basis.FromEuler(pitch, 0, 0) *
	/// Vector3.Forward</c> — the character's own body-then-camera rotation — written
	/// out, because the wire carries the two angles and not a basis.
	/// </summary>
	public static Vector3 Direction(float yaw, float pitch)
	{
		float cosPitch = MathF.Cos(pitch);
		return new Vector3(
			-MathF.Sin(yaw) * cosPitch,
			MathF.Sin(pitch),
			-MathF.Cos(yaw) * cosPitch);
	}

	/// <summary>
	/// The look angles for a direction: the inverse of <see cref="Direction"/>, for
	/// putting an arbitrary aim vector on the wire.
	/// </summary>
	public static void Angles(Vector3 direction, out float yaw, out float pitch)
	{
		if (direction == Vector3.Zero)
		{
			yaw = 0f;
			pitch = 0f;
			return;
		}

		direction = direction.Normalized();
		pitch = MathF.Asin(Math.Clamp(direction.Y, -1f, 1f));
		yaw = MathF.Atan2(-direction.X, -direction.Z);
	}
}
