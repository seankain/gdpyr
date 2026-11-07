using System;
using Gdpyr.Core;
using Gdpyr.Sim;

namespace Gdpyr.Match;

/// <summary>
/// What a host picks in the main menu's host panel before the round opens
/// (docs/LAN.md §3.4): the map, the round's length, the ground force's ticket pool,
/// and how many players each side is.
///
/// A side's size is the number of players it is filled to, humans included —
/// computer players take the seats nobody is sitting in, exactly as
/// <c>--bots &lt;ground&gt;:&lt;strategists&gt;</c> does (docs/IMPLEMENTATION_PLAN.md
/// §M3.5). The strategists' size is also the most strategists there may be at once:
/// a request for a full chair lands on the ground, as it always has
/// (<see cref="TeamService.StrategistCap"/>). The ground force has no such ceiling,
/// because there is nowhere else to put a person; the roster's sixteen slots are
/// its bound.
///
/// Immutable, and clamped on the way in, so a value read off a widget cannot carry
/// a round out of the ranges below. Engine-free, so what those ranges are is a test.
/// </summary>
public sealed class HostSettings
{
	public const int MinRoundMinutes = 1;

	/// <summary>An hour. The match clock's ticks are a <c>uint</c>; the menu's ceiling is a person's patience.</summary>
	public const int MaxRoundMinutes = 60;

	/// <summary>One: the first death ends the round. Zero would end it before anybody spawned.</summary>
	public const int MinGroundForceTickets = 1;

	public const int MaxGroundForceTickets = 999;

	/// <summary>A side of one is the host alone, or one bot.</summary>
	public const int MinTeamSize = 1;

	/// <summary>
	/// The roster's slots less the most strategists there can be: a ground force this
	/// size and a full strategists' side fit in one snapshot together
	/// (<see cref="SnapshotCodec.MaxPlayers"/>).
	/// </summary>
	public const int MaxGroundForceSize = SnapshotCodec.MaxPlayers - TeamService.MaxStrategists;

	/// <summary>The two chairs the design has (<see cref="TeamService.MaxStrategists"/>).</summary>
	public const int MaxStrategistSize = TeamService.MaxStrategists;

	public HostSettings(string mapId, int roundMinutes, int groundForceTickets, int groundForceSize,
		int strategistSize)
	{
		MapId = MapCatalog.IndexOf(mapId) >= 0 ? mapId : MapCatalog.Default.Id;
		RoundMinutes = Math.Clamp(roundMinutes, MinRoundMinutes, MaxRoundMinutes);
		GroundForceTickets = Math.Clamp(groundForceTickets, MinGroundForceTickets, MaxGroundForceTickets);
		GroundForceSize = Math.Clamp(groundForceSize, MinTeamSize, MaxGroundForceSize);
		StrategistSize = Math.Clamp(strategistSize, MinTeamSize, MaxStrategistSize);
	}

	/// <summary>A map from <see cref="MapCatalog"/>. An id the catalog does not have is replaced by its default.</summary>
	public string MapId { get; }

	/// <summary>How long a round lasts on the clock, in minutes.</summary>
	public int RoundMinutes { get; }

	/// <summary>The respawns the ground force shares. At zero the round ends.</summary>
	public int GroundForceTickets { get; }

	/// <summary>Ground-force players to fill to, humans included.</summary>
	public int GroundForceSize { get; }

	/// <summary>Strategists to fill to, humans included, and the most there may be at once.</summary>
	public int StrategistSize { get; }

	public MapInfo Map => MapCatalog.TryFind(MapId, out MapInfo map) ? map : MapCatalog.Default;

	/// <summary>The round's length in simulation ticks.</summary>
	public int RoundTicks => RoundMinutes * 60 * SimConfig.TickRate;

	/// <summary>
	/// What the menu offers before the host changes anything: the game mode's numbers,
	/// with the command line's bot counts over them where it gave any, pulled into range.
	/// </summary>
	public static HostSettings Defaults(int roundMinutes, int groundForceTickets, int groundForceSize,
		int strategistSize) =>
		new(MapCatalog.Default.Id, roundMinutes, groundForceTickets, groundForceSize, strategistSize);

	public override string ToString() =>
		$"map {MapId} | {RoundMinutes} min | {GroundForceTickets} tickets"
		+ $" | {GroundForceSize} ground force, {StrategistSize} strategist{(StrategistSize == 1 ? string.Empty : "s")}";
}
