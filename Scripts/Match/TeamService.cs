using System.Collections.Generic;
using Gdpyr.Sim;

namespace Gdpyr.Match;

/// <summary>
/// Who is on which side, and who is allowed to be
/// (docs/IMPLEMENTATION_PLAN.md §3).
///
/// Server-authoritative: a client asks for a side and is told what it got. The one
/// rule worth enforcing is the strategist cap — the round is 6v2, and a lobby
/// where everyone picks the interesting-looking role is a lobby with nobody on the
/// ground.
///
/// Engine-free, so "what happens when the third person asks to be a strategist" is
/// answerable from a test rather than from two machines and a guess. The server's
/// copy of a player's side lives here; <c>PlayerCombat.Team</c> mirrors it for the
/// gameplay code, and on a client it is driven by the snapshot's team bit instead
/// (docs/NETCODE.md §7).
/// </summary>
public sealed class TeamService
{
	/// <summary>
	/// Strategists allowed at once. Two, per the 6v2 the whole design is scoped to
	/// (docs/IMPLEMENTATION_PLAN.md, header).
	/// </summary>
	public const int MaxStrategists = 2;

	private readonly Dictionary<int, Team> _teams = new();

	/// <summary>Bumped whenever an assignment changes.</summary>
	public uint Version { get; private set; }

	public int Count => _teams.Count;

	public int StrategistCount
	{
		get
		{
			int count = 0;
			foreach (KeyValuePair<int, Team> entry in _teams)
			{
				if (entry.Value == Team.Strategist)
				{
					count++;
				}
			}
			return count;
		}
	}

	/// <summary>Everyone joins on the ground; the strategist slot is opted into.</summary>
	public Team TeamOf(int peerId) => _teams.TryGetValue(peerId, out Team team) ? team : Team.GroundForce;

	public bool Knows(int peerId) => _teams.ContainsKey(peerId);

	/// <summary>
	/// Whether <paramref name="peerId"/> could take <paramref name="team"/> right
	/// now. Someone already holding a strategist slot keeps it — asking for the
	/// side you are on must never be refused for want of room on it.
	/// </summary>
	public bool CanJoin(int peerId, Team team) =>
		team != Team.Strategist || TeamOf(peerId) == Team.Strategist || StrategistCount < MaxStrategists;

	/// <summary>
	/// Puts a peer on a side and returns the side they actually got. A refused
	/// strategist request lands on the ground rather than erroring: the client
	/// showed the choice the instant it was clicked, and the correction has to be
	/// a state it can display.
	/// </summary>
	public Team Assign(int peerId, Team requested)
	{
		Team granted = CanJoin(peerId, requested) ? requested : Team.GroundForce;

		if (_teams.TryGetValue(peerId, out Team current) && current == granted)
		{
			return granted;
		}

		_teams[peerId] = granted;
		Version++;
		return granted;
	}

	public void Remove(int peerId)
	{
		if (_teams.Remove(peerId))
		{
			Version++;
		}
	}

	public void Clear()
	{
		if (_teams.Count == 0)
		{
			return;
		}

		_teams.Clear();
		Version++;
	}
}
