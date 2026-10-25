using Gdpyr.Fps;
using Gdpyr.Sim;
using Godot;

namespace Gdpyr.Match;

/// <summary>
/// Ground bots perceive fire (docs/TACTICS.md §5.7): the near-miss pass the units have
/// (<c>UnitManager.NoteRound</c>), run for the strategist's rounds against the ground
/// characters that read it — bots that plan (<see cref="PlayerCombat.PerceivesFire"/>).
/// A person is suppressed by rounds and nothing else, and a legacy bot plans nothing, so
/// neither is asked. No ray: geometry against the capsules the hit test already has.
/// </summary>
public partial class CombatManager
{
	/// <summary>
	/// One chord of a strategist round, from <paramref name="from"/> to where it stopped
	/// (<paramref name="to"/>): fired upon for <paramref name="victim"/>, for every bot that
	/// perceives fire it passed within <see cref="NearMiss.Meters"/> of, and — for a round
	/// that went off where it stopped — for every one its blast reached.
	/// </summary>
	private void NoteRoundAtBots(Team shooterTeam, Vector3 from, Vector3 to, PlayerCombat victim, float blastRadius,
		uint tick)
	{
		if (shooterTeam != Team.Strategist)
		{
			return;
		}

		for (int i = 0; i < _ordered.Count; i++)
		{
			PlayerCombat player = _ordered[i];
			if (!player.PerceivesFire || !player.IsAlive)
			{
				continue;
			}

			if (player == victim || (player.History.TrySample(tick, out HitCapsule body)
				&& (NearMiss.InReach(body, to, blastRadius) || NearMiss.Passes(body, from, to))))
			{
				player.LastFiredUponTick = tick;
			}
		}
	}

	/// <summary>A strategist blast at <paramref name="point"/>: fired upon for every bot that perceives fire within its reach.</summary>
	private void NoteBlastAtBots(Team shooterTeam, Vector3 point, float radius, uint tick)
	{
		if (shooterTeam != Team.Strategist)
		{
			return;
		}

		for (int i = 0; i < _ordered.Count; i++)
		{
			PlayerCombat player = _ordered[i];
			if (player.PerceivesFire && player.IsAlive && player.History.TrySample(tick, out HitCapsule body)
				&& NearMiss.InReach(body, point, radius))
			{
				player.LastFiredUponTick = tick;
			}
		}
	}

	/// <summary>Ground bots a strategist round has come near in the last <paramref name="windowTicks"/>. Server-side, for the debug HUD.</summary>
	public int BotsFiredUpon(uint tick, int windowTicks)
	{
		int count = 0;
		for (int i = 0; i < _ordered.Count; i++)
		{
			PlayerCombat player = _ordered[i];
			count += player.PerceivesFire && player.IsAlive && player.LastFiredUponTick != 0
				&& tick >= player.LastFiredUponTick && tick - player.LastFiredUponTick <= (uint)windowTicks
					? 1
					: 0;
		}

		return count;
	}
}
