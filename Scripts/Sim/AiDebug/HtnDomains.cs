using System;
using Gdpyr.Sim.Htn;

namespace Gdpyr.Sim.AiDebug;

/// <summary>
/// The three domains, built here for the side of the wire that does not plan
/// (docs/AI_DEBUG.md §3.1). A spectator's process has no bots and no planning
/// contexts — those are the server's — but it has the same code, so it builds the
/// same domains and numbers them the same way, and a task index off the wire means
/// the same task on both ends. The server's own maps are on the planning objects
/// (<c>GroundPlanning.Map</c> and its siblings); the frame carries their signatures,
/// and a spectator whose maps disagree says so instead of drawing the wrong tree.
///
/// Each map is built on first use and kept: one domain build is about 18 KB, once
/// (docs/HTN_BOTS.md §3.4, P9).
/// </summary>
public static class HtnDomains
{
	private static readonly Lazy<HtnDomainMap> GroundMap =
		new(() => HtnDomainMap.Build(HtnDomainKind.Ground, GroundDomain.Build(new PooledHtnFactory())));

	private static readonly Lazy<HtnDomainMap> UnitMap =
		new(() => HtnDomainMap.Build(HtnDomainKind.Unit, UnitDomain.Build(new PooledHtnFactory())));

	private static readonly Lazy<HtnDomainMap> SquadMap =
		new(() => HtnDomainMap.Build(HtnDomainKind.Squad, CommanderDomain.Build(new PooledHtnFactory())));

	public static HtnDomainMap Ground => GroundMap.Value;

	public static HtnDomainMap Unit => UnitMap.Value;

	public static HtnDomainMap Squad => SquadMap.Value;

	/// <summary>The map for a domain, or null for <see cref="HtnDomainKind.None"/>.</summary>
	public static HtnDomainMap For(HtnDomainKind kind) => kind switch
	{
		HtnDomainKind.Ground => Ground,
		HtnDomainKind.Unit => Unit,
		HtnDomainKind.Squad => Squad,
		_ => null,
	};

	/// <summary>Parses <c>ground</c>, <c>unit</c> or <c>squad</c> (and a few spellings of each), for the console.</summary>
	public static bool TryParse(string text, out HtnDomainKind kind)
	{
		switch (text?.Trim().ToLowerInvariant())
		{
			case "ground":
			case "bot":
			case "bots":
				kind = HtnDomainKind.Ground;
				return true;

			case "unit":
			case "units":
				kind = HtnDomainKind.Unit;
				return true;

			case "squad":
			case "squads":
			case "commander":
			case "strategist":
				kind = HtnDomainKind.Squad;
				return true;

			default:
				kind = HtnDomainKind.None;
				return false;
		}
	}
}
