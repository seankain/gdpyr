using Gdpyr.Core;
using Gdpyr.Fps;
using Gdpyr.Rts;
using Gdpyr.Sim;
using Gdpyr.Sim.Agent;
using Godot;

namespace Gdpyr.Match;

/// <summary>
/// What the outpost's units do to the round (docs/OUTPOST.md): a scout drone's charge
/// going off on the strategist's strike order, an operative's knife, and the smoke a
/// smoke grenade lets out — the one thing on the field that stops a sight line without
/// stopping a round.
///
/// All of it on the authority. A client is told what it is told about any blast or
/// swing: a hit message, which it draws (docs/EFFECTS.md §2.1) — the smoke grenade's as
/// a standing cloud, to the numbers the server's cloud keeps (<see cref="SmokeRules"/>).
/// </summary>
public partial class CombatManager
{
	/// <summary>How far under a smoke grenade that went off in the air its cloud is put down, at most [m].</summary>
	private const float SmokeDropMeters = 3f;

	/// <summary>
	/// The smoke standing on the field (<see cref="SmokeField"/>). Server-side: the units'
	/// sight, the fog and the ground bots' sensors ask it after the world says a line is
	/// clear. Stepped by the unit manager at the top of each server tick, before anybody looks.
	/// </summary>
	public SmokeField Smoke { get; } = new();

	/// <summary>Scout drones that have gone off this round. Server-side, for the debug HUD and the log.</summary>
	public int DroneStrikes { get; private set; }

	/// <summary>Of those, the strikes that killed at least one player.</summary>
	public int DroneStrikeKills { get; private set; }

	/// <summary>Stabs that landed this round, and the ones that killed.</summary>
	public int Stabs { get; private set; }

	public int StabKills { get; private set; }

	/// <summary>
	/// A unit's charge going off where it is (docs/OUTPOST.md §3.3), and the unit with it:
	/// the blast to everyone in reach who is not on its side, as every blast is, the hit
	/// message that draws it, and what the units and the ground bots make of it. Called by
	/// the unit manager only for a unit flying the strategist's strike order.
	/// </summary>
	public void ServerDetonateUnit(Unit unit, StrikeEnd end, uint tick)
	{
		if (unit == null || !unit.IsAlive || unit.Definition is not { CanStrike: true } definition)
		{
			return;
		}

		byte charge = definition.StrikeWeaponDefinitionId;
		ProjectileStats stats = WeaponCatalog.RoundFor(charge);
		Vector3 point = unit.Hitbox.Center;
		int ownerId = OwnerId.ForUnit(unit.UnitId);
		Team team = unit.Team;

		HitFlags flags = HitFlags.Exploded | Explode(point, stats, null, ownerId, team, WeaponOnStream(null, charge),
			null, null, null, null, tick);

		UnitManager.Instance?.NoteBlast(new FireSource(point, ownerId, charge, 0), team, point,
			stats.ExplosionRadiusMeters, FireKind.Hit, tick);
		NoteBlastAtBots(team, point, stats.ExplosionRadiusMeters, tick);

		PublishHit(new ProjectileHit
		{
			Id = 0,
			Point = point,
			VictimPeerId = 0,
			Flags = flags,
		}, charge);

		DroneStrikes++;
		bool killed = flags.HasFlag(HitFlags.Killed);
		DroneStrikeKills += killed ? 1 : 0;
		AgentEventBus.Emit(AgentEventKind.UnitStrike, tick, unit.UnitId, (int)end, killed ? 1 : 0, point.X, point.Z);

		// It goes with its charge, in its own name.
		UnitManager.Instance?.Damage(unit, unit.Health + 1f, explosive: true, vehicleScale: 1f, ownerId, tick, out _);

		GD.Print($"[outpost] {UnitCatalog.NameOf(unit.DefinitionId)} {unit.UnitId} struck"
			+ $" ({end.ToString().ToLowerInvariant()}) at ({point.X:0.0}, {point.Y:0.0}, {point.Z:0.0})"
			+ $" | {(killed ? "killed" : "no kill")}");
	}

	/// <summary>
	/// A unit's knife (docs/OUTPOST.md §4.4): an instant swing at <paramref name="victim"/>,
	/// landing when the blade reaches their capsule from the unit's eye and nothing solid
	/// stands between. Not through a hull: a player in a crew vehicle's seat is out of
	/// reach. True when it landed.
	/// </summary>
	public bool ServerUnitStab(Unit unit, PlayerCombat victim, byte weaponId, uint tick)
	{
		WeaponStats stats = WeaponCatalog.StatsFor(weaponId);
		if (unit == null || !unit.IsAlive || !stats.IsMelee || victim?.Character == null || !victim.IsAlive
			|| victim.Team == unit.Team
			|| (CrewVehicleManager.Instance is { } crew && crew.TrySeat(victim.PeerId, out _, out _)))
		{
			return false;
		}

		Vector3 origin = unit.EyePosition;
		HitCapsule capsule = victim.Character.Hitbox;
		if (!UnitMelee.Reaches(origin, capsule, stats.RangeMeters) || !HasLineOfSight(origin, capsule.Center))
		{
			return false;
		}

		int ownerId = OwnerId.ForUnit(unit.UnitId);
		HitFlags flags = HitFlags.Melee | Damage(victim, stats.Damage, explosive: false, null, ownerId,
			WeaponOnStream(null, weaponId), tick);

		Stabs++;
		StabKills += flags.HasFlag(HitFlags.Killed) ? 1 : 0;

		PublishHit(new ProjectileHit
		{
			Id = 0,
			Point = capsule.Center,
			VictimPeerId = victim.PeerId,
			Flags = flags,
		}, weaponId);

		unit.Humanoid?.Throw();
		return true;
	}

	/// <summary>
	/// A smoke grenade's fuse has run out (docs/OUTPOST.md §4.3): its cloud, put down on the
	/// floor under where it went off, into <see cref="Smoke"/>, and the hit message every
	/// peer draws it from. It hurts nobody, and no unit or bot takes it for fire.
	/// </summary>
	private void LetOutSmoke(in ProjectileSegment segment, Vector3 point, uint tick)
	{
		_projectiles.Kill(segment.Id);
		HitsResolved++;

		WorldHitFraction(point + (Vector3.Up * 0.1f), point + (Vector3.Down * SmokeDropMeters), out Vector3 floor,
			out bool found);
		Vector3 ground = found ? floor : point;
		Smoke.Add(ground, tick);

		PublishHit(new ProjectileHit
		{
			Id = segment.Id,
			Point = ground,
			VictimPeerId = 0,
			Flags = HitFlags.Exploded,
		}, segment.DefinitionId);
	}
}
