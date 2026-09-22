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
/// It also holds the other half of that question: who has not answered it yet. A
/// person is asked when they join and again when a round starts, and is not on the
/// field until they say (<see cref="RequireChoice"/>); a computer player is never
/// asked, because it has no menu to answer with.
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

	/// <summary>
	/// Peers that have been asked which side they want this round and have not
	/// answered. They are in <see cref="_teams"/> as well — on the ground, because
	/// the wire has two teams and no third state (<see cref="Team"/>) — but they are
	/// held off the field until they answer (<c>PlayerCombat.AwaitingRole</c>).
	/// </summary>
	private readonly HashSet<int> _awaiting = new();

	/// <summary>Bumped whenever an assignment changes.</summary>
	public uint Version { get; private set; }

	public int Count => _teams.Count;

	/// <summary>Peers that owe an answer to the role menu.</summary>
	public int AwaitingCount => _awaiting.Count;

	/// <summary>Peers on a side of their own choosing, and therefore on the field.</summary>
	public int ReadyCount => _teams.Count - _awaiting.Count;

	public bool AwaitingChoice(int peerId) => _awaiting.Contains(peerId);

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

		// An assignment is an answer, whoever it came from: the menu, the two keys, a
		// bot director filling a seat, or an agent taking one.
		bool answered = _awaiting.Remove(peerId);

		if (!answered && _teams.TryGetValue(peerId, out Team current) && current == granted)
		{
			return granted;
		}

		_teams[peerId] = granted;
		Version++;
		return granted;
	}

	/// <summary>
	/// Asks a peer which side they want, and stops counting them as being on one
	/// until they say (docs/IMPLEMENTATION_PLAN.md §M3). Returns true when the
	/// question was actually put to them.
	///
	/// A computer player is never asked: it has no menu to answer with, and the
	/// director assigns it a side on the tick it joins. Asking one would hold a seat
	/// open that the fill policy has already counted as taken.
	/// </summary>
	public bool RequireChoice(int peerId)
	{
		if (BotRoster.IsBot(peerId))
		{
			return false;
		}

		// Registered on the ground meanwhile: the snapshot's team bit has two values
		// and a player who has not chosen still has to ride in it.
		if (!_teams.ContainsKey(peerId))
		{
			_teams[peerId] = Team.GroundForce;
			Version++;
		}

		return _awaiting.Add(peerId);
	}

	/// <summary>
	/// Puts the question to every person here. What a fresh round does: the sides are
	/// picked once per round, not once per connection, so that a strategist who wants
	/// to play the next one on the ground is asked rather than having to remember a
	/// key.
	/// </summary>
	public int RequireChoiceFromPeople()
	{
		int asked = 0;
		foreach (KeyValuePair<int, Team> entry in _teams)
		{
			if (!BotRoster.IsBot(entry.Key) && _awaiting.Add(entry.Key))
			{
				asked++;
			}
		}

		return asked;
	}

	public void Remove(int peerId)
	{
		_awaiting.Remove(peerId);

		if (_teams.Remove(peerId))
		{
			Version++;
		}
	}

	public void Clear()
	{
		_awaiting.Clear();

		if (_teams.Count == 0)
		{
			return;
		}

		_teams.Clear();
		Version++;
	}
}
