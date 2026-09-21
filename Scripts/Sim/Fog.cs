using System;

namespace Gdpyr.Sim;

/// <summary>
/// What a client makes of the records it was *not* sent (docs/NETCODE.md §6.2).
///
/// The server hides an entity by leaving it out of that peer's snapshot, so a
/// client never receives a "you have lost contact" message: it infers one from a
/// record that has stopped arriving in packets that have not. That inference is
/// this file — how far behind the newest packet a record has to fall before it
/// means the fog, and how a lost contact decays on the screen afterwards.
///
/// Stale information is what makes the strategist's role interesting, so the rule
/// is to decay it rather than hide it: a contact goes from solid, to a ghost at its
/// last known position, to nothing.
/// </summary>
public static class Fog
{
	/// <summary>
	/// Whether a record this old means the fog.
	///
	/// The age is how far behind the newest snapshot the client has decoded this
	/// entity's own record has fallen — not how long ago it arrived. A snapshot
	/// carries the whole of what its peer may see, so a record missing from one that
	/// arrived was withheld on purpose, and a snapshot that was lost takes the
	/// reference with it rather than aging anything. That is why this is a small
	/// number and not a latency budget: it covers an unreliable datagram arriving out
	/// of order, and nothing else.
	/// </summary>
	public static bool IsLost(float ageTicks) => ageTicks > SimConfig.FogContactTimeoutTicks;

	/// <summary>
	/// How solidly a lost contact is drawn: 1 the moment it was lost, falling to 0
	/// over <see cref="SimConfig.GhostLifetimeTicks"/>. A contact that is not lost
	/// at all is 1.
	/// </summary>
	public static float GhostAlpha(float ageTicks)
	{
		float decayed = ageTicks - SimConfig.FogContactTimeoutTicks;
		if (decayed <= 0f)
		{
			return 1f;
		}

		return Math.Clamp(1f - (decayed / SimConfig.GhostLifetimeTicks), 0f, 1f);
	}

	/// <summary>Whether a contact this old has faded out completely and is not worth drawing.</summary>
	public static bool IsForgotten(float ageTicks) => GhostAlpha(ageTicks) <= 0f;
}
