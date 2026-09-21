using System;

namespace Gdpyr.Sim;

/// <summary>
/// How many computer players each side should have, and what they are called on
/// the wire (docs/IMPLEMENTATION_PLAN.md §M3.5).
///
/// A bot is a player. It holds a roster slot, a character, a loadout and a place
/// in the snapshot exactly as a human does; the only thing that differs is where
/// its <see cref="InputFrame"/> comes from. That decision is what keeps bots from
/// being a second kind of entity with a second set of rules — no new message, no
/// new lifecycle, and every client already knows how to draw one.
///
/// Being a player means having a peer id, and peer ids are Godot's to hand out.
/// Bots take a band at the top of the positive range instead: ids stay positive,
/// so <see cref="OwnerId"/>'s fold of units into the negative half is untouched,
/// and <see cref="IsBot"/> is one comparison anywhere that needs to know.
///
/// Engine-free, because "what happens to the bots when a fifth human joins" is
/// worth answering from a test rather than from four people and a server.
/// </summary>
public static class BotRoster
{
	/// <summary>
	/// The first bot's peer id. Godot draws a client's id at random from the whole
	/// positive range, so this band is unlikely rather than impossible to collide
	/// with; the director checks and re-slots on the way in, which makes it safe
	/// rather than improbable.
	/// </summary>
	public const int FirstPeerId = 0x4000_0000;

	/// <summary>
	/// Bot slots in the band. Sixteen matches <c>SnapshotCodec.MaxPlayers</c>: a
	/// roster is bounded by the snapshot either way, and a bot that would not fit
	/// in the broadcast must not be spawned.
	/// </summary>
	public const int MaxBots = SnapshotCodec.MaxPlayers;

	public static bool IsBot(int peerId) => peerId >= FirstPeerId && peerId < FirstPeerId + MaxBots;

	/// <summary>The peer id for a bot slot, or 0 when the slot is out of the band.</summary>
	public static int PeerIdFor(int slot) =>
		slot >= 0 && slot < MaxBots ? FirstPeerId + slot : 0;

	/// <summary>The slot a bot's peer id names, or -1 for a human's.</summary>
	public static int SlotOf(int peerId) => IsBot(peerId) ? peerId - FirstPeerId : -1;

	/// <summary>A bot's display name, which is also the character node's name.</summary>
	public static string NameOf(int peerId) =>
		IsBot(peerId) ? $"bot {SlotOf(peerId) + 1}" : $"peer {peerId}";
}

/// <summary>
/// What the roster looks like right now, and what it is being asked to look like.
/// Every field is a count, so the whole backfill decision is arithmetic.
/// </summary>
public readonly struct BotDemand
{
	/// <summary>Ground-force players wanted in total, humans included. 0 disables ground bots.</summary>
	public readonly int GroundTarget;

	/// <summary>Strategists wanted in total, humans included.</summary>
	public readonly int StrategistTarget;

	public readonly int GroundHumans;
	public readonly int StrategistHumans;

	/// <summary>Roster slots the snapshot can carry — <c>SnapshotCodec.MaxPlayers</c>.</summary>
	public readonly int Seats;

	/// <summary>Strategists allowed at once, from <c>TeamService.MaxStrategists</c>.</summary>
	public readonly int StrategistCap;

	public BotDemand(int groundTarget, int strategistTarget, int groundHumans, int strategistHumans,
		int seats, int strategistCap)
	{
		GroundTarget = Math.Max(groundTarget, 0);
		StrategistTarget = Math.Max(strategistTarget, 0);
		GroundHumans = Math.Max(groundHumans, 0);
		StrategistHumans = Math.Max(strategistHumans, 0);
		Seats = Math.Max(seats, 0);
		StrategistCap = Math.Max(strategistCap, 0);
	}

	public int Humans => GroundHumans + StrategistHumans;
}

/// <summary>How many bots each side should have. The answer to a <see cref="BotDemand"/>.</summary>
public readonly struct BotFill
{
	public readonly int Ground;
	public readonly int Strategist;

	public BotFill(int ground, int strategist)
	{
		Ground = ground;
		Strategist = strategist;
	}

	public int Total => Ground + Strategist;

	public override string ToString() => $"{Ground} ground, {Strategist} strategist";
}

/// <summary>
/// The backfill policy: bots fill the seats nobody is sitting in, and get up when
/// somebody wants one.
/// </summary>
public static class BotFillPolicy
{
	/// <summary>
	/// How many bots each side should have, given who is already there.
	///
	/// Three rules, in order:
	/// <list type="number">
	/// <item>An empty server gets no bots at all. Nobody is watching, and a headless
	/// Godot burning a core on a firefight nobody can see is what spends the EC2
	/// box's CPU credits before a playtest starts (docs/DEPLOYMENT.md §5).</item>
	/// <item>The strategist seat is filled before the ground ones. A ground force
	/// with nothing shooting back is not a game; a strategist with nobody to order
	/// units at is at least still an RTS.</item>
	/// <item>Humans always displace bots. A bot holds a seat only while nobody else
	/// wants it, which is the whole of what "for when the servers are empty" means.</item>
	/// </list>
	/// </summary>
	public static BotFill Plan(in BotDemand demand)
	{
		if (demand.Humans == 0)
		{
			return new BotFill(0, 0);
		}

		int seats = Math.Max(demand.Seats - demand.Humans, 0);

		int strategists = Math.Clamp(demand.StrategistTarget - demand.StrategistHumans, 0,
			Math.Max(demand.StrategistCap - demand.StrategistHumans, 0));
		strategists = Math.Min(strategists, seats);
		seats -= strategists;

		int ground = Math.Clamp(demand.GroundTarget - demand.GroundHumans, 0, seats);

		int total = Math.Min(strategists + ground, BotRoster.MaxBots);
		strategists = Math.Min(strategists, total);
		ground = total - strategists;

		return new BotFill(ground, strategists);
	}
}
