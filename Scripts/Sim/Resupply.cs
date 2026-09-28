using System;
using Godot;

namespace Gdpyr.Sim;

/// <summary>
/// One place a strategist unit is resupplied (docs/HTN_BOTS.md §8, D2): a friendly
/// barracks' defended ring, or the reach of a supply truck.
/// </summary>
public readonly struct SupplySource
{
	/// <summary>
	/// The middle of its reach: the barracks building, or the truck. Also where a unit
	/// sent to it walks, since walking towards the middle crosses the edge wherever the
	/// unit starts, and a unit is resupplied from the edge in.
	/// </summary>
	public readonly Vector3 Position;

	/// <summary>How far round <see cref="Position"/>, measured flat, a unit is resupplied.</summary>
	public readonly float ReachMeters;

	/// <summary>The truck's unit id; 0 for a barracks. A truck does not resupply itself.</summary>
	public readonly ushort UnitId;

	public readonly Team Team;

	public SupplySource(Vector3 position, float reachMeters, ushort unitId, Team team)
	{
		Position = position;
		ReachMeters = MathF.Max(reachMeters, 0f);
		UnitId = unitId;
		Team = team;
	}
}

/// <summary>
/// The strategist's resupply rule (docs/HTN_BOTS.md §8, D2; H5), as pure functions.
///
/// A unit gets health back at its own <c>UnitDefinition.RegenPerSecond</c> while it
/// is inside the reach of a friendly supply source — a barracks' defended ring, or a
/// supply truck — and nothing has hurt it for <see cref="DamageLockoutTicks"/>. That
/// last clause is what makes it resupply rather than armour: a fight at the door is
/// fought at the health the units walked into it with, and healing is what happens
/// between fights. Magazines already reload without a reserve, so health is the one
/// thing a unit has to resupply.
///
/// It is a rule of the game, not of a bot: a person's units heal where a computer
/// strategist's do, under <c>--bot-ai legacy</c> as under <c>htn</c>. The HTNs only
/// decide when to go and get it (§5.2, §5.3).
///
/// Engine-free, like the defences' rules: who is resupplied, and how much, are
/// questions for <c>dotnet test</c>.
/// </summary>
public static class Resupply
{
	/// <summary>
	/// How long after a hit a unit gets nothing back. Three seconds: longer than one
	/// burst, shorter than walking out of a fight.
	/// </summary>
	public const int DamageLockoutTicks = SimConfig.TickRate * 3;

	/// <summary>
	/// A barracks' reach when it has no defences, or defences that reach less than
	/// this: where its rally point and the units formed up round it stand.
	/// </summary>
	public const float BarracksMinReachMeters = 25f;

	/// <summary>A barracks resupplies inside its defended ring, and never less than <see cref="BarracksMinReachMeters"/>.</summary>
	public static float BarracksReach(float defendedRadiusMeters) =>
		MathF.Max(defendedRadiusMeters, BarracksMinReachMeters);

	/// <summary>How far outside a source's reach a point is, measured flat; negative inside.</summary>
	public static float EdgeDistance(Vector3 at, in SupplySource source) => Flat(at, source.Position) - source.ReachMeters;

	/// <summary>Whether a unit last hurt on <paramref name="lastDamagedTick"/> (0 for never) may heal on <paramref name="tick"/>.</summary>
	public static bool CanHeal(uint tick, uint lastDamagedTick, int lockoutTicks = DamageLockoutTicks) =>
		lastDamagedTick == 0 || tick < lastDamagedTick || tick - lastDamagedTick >= (uint)Math.Max(lockoutTicks, 0);

	/// <summary>
	/// The source of <paramref name="team"/>'s whose reach <paramref name="at"/> is
	/// nearest the inside of — the smallest <see cref="EdgeDistance"/> — leaving out the
	/// unit's own truck; -1 for none. Ties go to the lower index.
	/// </summary>
	public static int Nearest(Vector3 at, ushort selfId, Team team, ReadOnlySpan<SupplySource> sources,
		out float edgeMeters)
	{
		int best = -1;
		edgeMeters = float.MaxValue;
		for (int i = 0; i < sources.Length; i++)
		{
			SupplySource source = sources[i];
			if (source.Team != team || (source.UnitId != 0 && source.UnitId == selfId))
			{
				continue;
			}

			float edge = EdgeDistance(at, source);
			if (edge < edgeMeters)
			{
				edgeMeters = edge;
				best = i;
			}
		}

		return best;
	}

	/// <summary>Whether <paramref name="at"/> is inside the reach of one of <paramref name="team"/>'s sources, not counting its own.</summary>
	public static bool IsSupplied(Vector3 at, ushort selfId, Team team, ReadOnlySpan<SupplySource> sources) =>
		Nearest(at, selfId, team, sources, out float edge) >= 0 && edge <= 0f;

	/// <summary>
	/// Health after <paramref name="dt"/> seconds of resupply: <paramref name="regenPerSecond"/>
	/// more, and never past <paramref name="maxHealth"/>. The dead stay dead.
	/// </summary>
	public static float Heal(float health, float maxHealth, float regenPerSecond, float dt)
	{
		if (health <= 0f || regenPerSecond <= 0f || dt <= 0f || health >= maxHealth)
		{
			return health;
		}

		return MathF.Min(health + (regenPerSecond * dt), maxHealth);
	}

	private static float Flat(Vector3 a, Vector3 b)
	{
		float x = a.X - b.X;
		float z = a.Z - b.Z;
		return MathF.Sqrt((x * x) + (z * z));
	}
}
