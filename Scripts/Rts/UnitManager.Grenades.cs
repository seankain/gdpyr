using System;
using Gdpyr.Core;
using Gdpyr.Match;
using Gdpyr.Sim;
using Gdpyr.Sim.Agent;
using Gdpyr.Sim.Htn;
using Godot;

namespace Gdpyr.Rts;

/// <summary>
/// Grenades at enemies behind cover (docs/TACTICS.md §5.4): on its scan, a unit under
/// micro-autonomy with no target of its own looks for a hidden enemy the side saw, or
/// a round came from, within the fuse, and an arc that comes to rest within a kill of
/// them; its planner says when; and the throw is made here, one grenade per enemy at
/// a time for the whole side. No ray: what the side remembers, and the cover board.
/// </summary>
public partial class UnitManager
{
	/// <summary>A ground-force body's half-width [m]: the capsule's radius, Godot's default the character keeps.</summary>
	private const float PlayerRadiusMeters = 0.5f;

	/// <summary>How long a unit that found no arc to a hidden enemy waits before it looks for one again [ticks].</summary>
	private const int NoArcRetryTicks = SimConfig.TickRate;

	/// <summary>Grenades in the air at hidden enemies, per side, indexed by <see cref="Team"/>.</summary>
	private readonly GrenadeClaims[] _grenadeClaims = { new(SimConfig.MaxUnits), new(SimConfig.MaxUnits) };

	/// <summary>One scan's candidates: every ground-force player the side remembers, and where the unit was last fired upon from.</summary>
	private readonly GrenadeCandidate[] _grenadeCandidates = new GrenadeCandidate[SnapshotCodec.MaxPlayers + 1];

	/// <summary>Grenades units threw at hidden enemies this round. Server-side, for the debug HUD's <c>fire</c> row.</summary>
	public int HiddenThrows { get; private set; }

	/// <summary>
	/// The hidden enemy this unit would throw at, on its scan, into its planner's
	/// <see cref="UnitFact.Grenade"/>: none unless it may (micro-autonomy, a doctrine, a
	/// grenade ready, no target of its own, an order it throws under) and one is a
	/// candidate (<see cref="GrenadePlan.Choose"/>) that an arc reaches
	/// (<see cref="GrenadeSim.TrySolveOver"/>).
	/// </summary>
	private void SenseGrenade(Unit unit, UnitContext plan, in UnitOrder order, uint tick, ContactMemory memory,
		bool hasLine, Vector3 lineTo)
	{
		bool may = unit.Autonomy == MicroAutonomy.On && unit.Traits.Doctrine != UnitDoctrine.None
			&& unit.TargetOwnerId == OwnerId.None && GrenadeSim.AiReady(unit.Grenades, tick)
			&& tick >= unit.NoArcUntilTick
			&& order.Kind is not (OrderKind.Build or OrderKind.Embark or OrderKind.Unload);
		if (!may)
		{
			plan.SenseGrenade(false, Vector3.Zero);
			return;
		}

		// What the strategist's side remembers is ground-force players, where it last saw
		// them. Not one that has died since: a grenade is not spent on a body, as a
		// gunner's burst is not (Suppress).
		int count = 0;
		CombatManager combat = CombatManager.Instance;
		for (int i = 0; unit.Team == Team.Strategist && memory != null && i < memory.Count
			&& count < _grenadeCandidates.Length - 1; i++)
		{
			KnownContact known = memory.At(i);
			if (memory.AgeOf(i, tick) <= GrenadePlan.FreshTicks
				&& combat?.Find(PeerId(known.OwnerId)) is { IsAlive: true })
			{
				_grenadeCandidates[count++] = GrenadeCandidate.Contact(known.OwnerId, known.Position, known.LastSeenTick);
			}
		}

		if (FiredUpon.IsRecent(unit.Fire, tick))
		{
			_grenadeCandidates[count++] = GrenadeCandidate.Fired(FiredFromFeet(unit), unit.Fire.LastTick);
		}

		Vector3 eye = unit.EyePosition;
		int chosen = GrenadePlan.Choose(_grenadeCandidates.AsSpan(0, count), eye, tick, Cover,
			_grenadeClaims[(int)unit.Team], unit.UnitId, hasLine, unit.GlobalPosition, lineTo);
		if (chosen < 0)
		{
			plan.SenseGrenade(false, Vector3.Zero);
			return;
		}

		// An arc is a few hundred microseconds of flying (GrenadeSim.TrySolveOver): a unit
		// that found none waits a second before it flies another.
		if (!GrenadeSim.TrySolveOver(eye, _grenadeCandidates[chosen].Feet, HandGrenadeRound, Cover, KillReachMeters,
			out _, out _))
		{
			unit.NoArcUntilTick = tick + NoArcRetryTicks;
			plan.SenseGrenade(false, Vector3.Zero);
			return;
		}

		unit.ThrowKey = _grenadeCandidates[chosen].Key;
		plan.SenseGrenade(true, _grenadeCandidates[chosen].Feet);
	}

	/// <summary>
	/// The throw a unit's plan asks for (<see cref="UnitIntent.ThrowAt"/>), made on the
	/// tick it asks: from where it stands now, at the point its scan chose, if nobody
	/// else's grenade has gone there since and the arc still reaches. Tried once — the
	/// plan's fact goes off either way, until the next scan.
	/// </summary>
	private void ThrowAtHidden(Unit unit, UnitContext plan, uint tick)
	{
		Vector3 feet = plan.Intent.ThrowPoint;
		plan.Threw();

		GrenadeClaims claims = _grenadeClaims[(int)unit.Team];
		Vector3 eye = unit.EyePosition;
		var situation = new ThrowSituation(
			hasGrenade: unit.Grenades.HasAny,
			ready: GrenadeSim.AiReady(unit.Grenades, tick),
			distanceMeters: eye.DistanceTo(feet),
			targetSpeed: 0f,
			targetInCover: true,
			endangersFriends: false);

		if (CombatManager.Instance is not { } combat || unit.Posture != UnitPosture.Standing
			|| claims.IsClaimedByOther(unit.ThrowKey, tick, unit.UnitId)
			|| !GrenadeSim.ShouldThrow(situation)
			|| !GrenadeSim.TrySolveOver(eye, feet, HandGrenadeRound, Cover, KillReachMeters, out Vector3 aim, out _)
			|| !GrenadeSim.TrySpend(ref unit.Grenades, tick, GrenadeSim.ThrowIntervalTicks))
		{
			return;
		}

		claims.Claim(unit.ThrowKey, tick, unit.UnitId);
		HiddenThrows++;
		LaunchGrenade(unit, combat, tick, aim, hidden: true);
	}

	/// <summary>A unit's grenade leaving its hand along <paramref name="aim"/>, with its throwing error, and the agent stream told.</summary>
	private static void LaunchGrenade(Unit unit, CombatManager combat, uint tick, Vector3 aim, bool hidden)
	{
		int owner = OwnerId.ForUnit(unit.UnitId);
		Vector3 direction = Spread.Apply(aim, unit.Definition?.AccuracyConeRadians ?? 0f,
			Spread.Seed(owner, WeaponCatalog.HandGrenade, unit.Grenades.Thrown));
		combat.SpawnProjectile(tick, owner, WeaponCatalog.HandGrenade, unit.EyePosition, direction, catchUpTicks: 0);
		AgentEventBus.Emit(AgentEventKind.GrenadeThrown, tick, owner, unit.DefinitionId, hidden ? 1 : 0,
			unit.GlobalPosition.X, unit.GlobalPosition.Z);
	}

	private static ProjectileStats HandGrenadeRound => WeaponCatalog.RoundFor(WeaponCatalog.HandGrenade);

	/// <summary>How far from a full-health body's feet a unit's grenade may come to rest and still kill it.</summary>
	private static float KillReachMeters =>
		GrenadeSim.KillReach(HandGrenadeRound, SimConfig.MaxHealth, PlayerRadiusMeters);

	/// <summary>A new round: no grenade is on its way anywhere.</summary>
	private void ResetGrenades()
	{
		HiddenThrows = 0;
		for (int i = 0; i < _grenadeClaims.Length; i++)
		{
			_grenadeClaims[i].Clear();
		}
	}
}
