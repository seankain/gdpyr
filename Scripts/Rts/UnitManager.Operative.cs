using System;
using Gdpyr.Core;
using Gdpyr.Fps;
using Gdpyr.Match;
using Gdpyr.Sim;
using Gdpyr.Sim.Agent;
using Gdpyr.Sim.Htn;
using Godot;

namespace Gdpyr.Rts;

/// <summary>
/// The operative (docs/OUTPOST.md §4): the engine half of <see cref="OperativeSim"/>. At its
/// scan rate an operative under micro-autonomy picks a quarry — the player its order names,
/// else the one it sees, else the nearest the side remembers within reach — and finds out
/// what the tactics need: whether it is in cover from them, the next cover spot nearer them,
/// whether they can see it, whether smoke stands between, and whether its side's riflemen,
/// tanks or heavy gunners are firing on them. Every tick the tactics decide where it runs,
/// whether it crouches, holds its fire, throws smoke, stabs, and calls for covering fire;
/// and the units that give covering fire answer the calls (<see cref="CoveringFireBoard"/>).
///
/// With micro-autonomy off it is a soft, fast unit with a pistol that does what it is told.
/// It never hunts on a move order — that is the strategist's way of placing it — nor while
/// building, boarding or striking, which it cannot do anyway.
/// </summary>
public partial class UnitManager
{
	/// <summary>How far past its own sensor it will go after a contact the side remembers [m].</summary>
	private const float HuntBeyondSensorMeters = 15f;

	/// <summary>How far an operative on a defend order strays after a quarry: twice its leash.</summary>
	private const float DefendHuntLeashes = 2f;

	/// <summary>How far from its line an operative's next bound may stray, measured flat [m].</summary>
	private const float BoundOffLineMeters = 8f;

	/// <summary>How far up from a quarry's feet a unit answering a call aims: their chest.</summary>
	private const float CoverCallChestMeters = 1.2f;

	/// <summary>One covering-fire board per side, indexed by <see cref="Team"/>.</summary>
	private readonly CoveringFireBoard[] _coveringFire = { new(), new() };

	/// <summary>Bounds, dashes and smoke grenades operatives made this round; stabs are the combat manager's.</summary>
	public int OperativeBounds { get; private set; }

	public int OperativeDashes { get; private set; }

	public int OperativeSmokes { get; private set; }

	/// <summary>Calls for covering fire units took up this round.</summary>
	public int CoveringFireAnswers { get; private set; }

	private static bool IsOperative(Unit unit) => unit.Definition?.Doctrine == UnitDoctrine.Operative;

	/// <summary>
	/// One tick of an operative's tactics: a fresh look at its scan rate, its quarry's
	/// position kept live while it sees them, and the decision. What it decides to throw
	/// and stab with is done here; where it goes, its posture and its trigger are the
	/// caller's to apply, as a plan's are.
	/// </summary>
	private OperativeDecision StepOperative(Unit unit, uint tick, bool hasTarget)
	{
		CoveringFireBoard board = _coveringFire[(int)unit.Team];
		if (tick >= unit.OperativeScanTick)
		{
			SenseOperative(unit, tick, board);
			uint stagger = unit.OperativeScanTick == 0 ? unit.UnitId % (uint)SimConfig.UnitTargetRefreshTicks : 0u;
			unit.OperativeScanTick = tick + (uint)SimConfig.UnitTargetRefreshTicks + stagger;
		}

		ref OperativeSituation s = ref unit.OperativeSense;
		s.Tick = tick;
		s.Feet = unit.GlobalPosition;
		s.HasSmoke = unit.Smokes.HasAny && tick >= unit.Smokes.NextThrowTick;
		s.InKnifeReach = false;

		PlayerCombat quarry = s.HasQuarry ? CombatManager.Instance?.Find(PeerId(s.QuarryOwnerId)) : null;
		if (s.HasQuarry && quarry is not { IsAlive: true, Character: not null })
		{
			// Dead since the last look, or gone: nobody to go after until it looks again.
			s.HasQuarry = false;
			quarry = null;
		}

		if (quarry != null)
		{
			// Where it sees them is where they are; otherwise where it last knew them.
			if (hasTarget && unit.TargetOwnerId == s.QuarryOwnerId)
			{
				s.QuarryFeet = quarry.Character.SimPosition;
			}

			WeaponStats knife = unit.Definition is { HasMelee: true } melee
				? WeaponCatalog.StatsFor(melee.MeleeWeaponDefinitionId)
				: default;
			s.InKnifeReach = knife.IsMelee && UnitMelee.Reaches(unit.EyePosition, quarry.Character.Hitbox, knife.RangeMeters);
		}

		OperativePhase before = unit.Operative.Phase;
		OperativeDecision decision = OperativeSim.Step(ref unit.Operative, s);

		if (unit.Operative.Phase != before && unit.Operative.Phase != OperativePhase.None)
		{
			OperativeBounds += unit.Operative.Phase == OperativePhase.Bound ? 1 : 0;
			OperativeDashes += unit.Operative.Phase == OperativePhase.Dash ? 1 : 0;
			EmitOperative(unit, act: 0, tick);
		}

		// Done hunting: its planner starts afresh rather than picking up a plan it set down.
		if (!decision.Hunting && before != OperativePhase.None)
		{
			ResetPlan(unit);
		}

		if (decision.CallCover)
		{
			board.Call(unit.UnitId, s.Feet, s.QuarryFeet, tick + OperativeRules.CoverRequestTicks);
		}
		else
		{
			board.Withdraw(unit.UnitId);
		}

		if (decision.ThrowSmoke)
		{
			ThrowSmoke(unit, decision.SmokePoint, tick);
		}

		if (decision.Knife && quarry != null && unit.Definition is { HasMelee: true } definition
			&& CombatManager.Instance is { } combat)
		{
			bool alive = quarry.IsAlive;
			if (combat.ServerUnitStab(unit, quarry, definition.MeleeWeaponDefinitionId, tick))
			{
				EmitOperative(unit, act: alive && !quarry.IsAlive ? 3 : 2, tick);
			}
		}

		return decision;
	}

	/// <summary>
	/// An operative's look round, at its scan rate: who its quarry is, and what stands
	/// between them. Two rays from its quarry's eye — to its chest, and to where its chest
	/// would be a few metres on — and a cover test and a cover search, when it has a quarry;
	/// nothing when it has none.
	/// </summary>
	private void SenseOperative(Unit unit, uint tick, CoveringFireBoard board)
	{
		var s = new OperativeSituation { Tick = tick, Feet = unit.GlobalPosition, QuarryOwnerId = OwnerId.None };
		unit.OperativeSense = s;

		if (unit.Autonomy != MicroAutonomy.On || CombatManager.Instance is not { } combat
			|| unit.Order.Kind is OrderKind.Move or OrderKind.Build or OrderKind.Embark or OrderKind.Unload
				or OrderKind.Strike
			|| !TryQuarry(unit, combat, tick, out int quarryOwner, out Vector3 quarryFeet))
		{
			return;
		}

		s.HasQuarry = true;
		s.QuarryOwnerId = quarryOwner;
		s.QuarryFeet = quarryFeet;

		// Whether its quarry sees it as it is, and whether they would see it standing a few
		// metres on: the ground it has to cross.
		Vector3 quarryEye = quarryFeet + (Vector3.Up * CoverThreatEyeMeters);
		Vector3 feet = unit.GlobalPosition;
		Vector3 chest = unit.Hitbox.Center;
		Vector3 toward = quarryFeet - feet;
		toward.Y = 0f;
		toward = toward.LengthSquared() > 1e-6f ? toward.Normalized() : Vector3.Zero;
		Vector3 ahead = unit.Body.Hitbox(feet + (toward * OperativeRules.AheadMeters), UnitPosture.Standing).Center;

		bool clearNow = WorldClear(quarryEye, chest);
		bool smokedNow = clearNow && combat.Smoke.Blocks(quarryEye, chest);
		bool clearAhead = WorldClear(quarryEye, ahead);
		bool smokedAhead = clearAhead && combat.Smoke.Blocks(quarryEye, ahead);
		// Which way they look, when it can see them to tell; when it cannot, it supposes
		// they look its way.
		Vector3 look = combat.Find(PeerId(quarryOwner)) is { Character: { } body } && unit.TargetOwnerId == quarryOwner
			? body.AimDirection
			: Vector3.Zero;
		s.QuarrySeesIt = clearNow && !smokedNow && OperativeSim.Watches(quarryEye, look, chest);
		s.PathExposed = clearAhead && !smokedAhead && OperativeSim.Watches(quarryEye, look, ahead);
		s.Screened = smokedNow || smokedAhead;
		s.Covered = board.Covers(quarryFeet, tick);
		s.FiredUpon = FiredUpon.IsRecent(unit.Fire, tick);

		// Cover as it will use it: crouched, so a sandbag wall counts.
		CoverQuery query = UnitCoverQuery(unit, quarryFeet, mustFire: false, OperativeRules.BoundSearchMeters);
		query.ProtectMeters = unit.Body.ProtectMeters(UnitPosture.Crouched);
		s.InCover = Cover.Count > 0 && Cover.Protects(unit.GlobalPosition, query);

		query.HasToward = true;
		query.Toward = quarryFeet;
		query.MaxBackMeters = 0f;
		query.MaxOffLineMeters = BoundOffLineMeters;
		query.MinProgressMeters = OperativeRules.MinBoundMeters;
		query.StaySameSide = true;
		query.MinThreatMeters = OperativeRules.CloseMeters * 0.5f;
		if (Cover.Count > 0 && Cover.TryFindSpot(query, CoverClaims(unit, unit.Team), out CoverSpot spot))
		{
			s.HasBound = true;
			s.BoundSpot = spot.Position;
			unit.HasCoverSpot = true;
			unit.CoverSpot = spot.Position;
		}

		unit.OperativeSense = s;
	}

	/// <summary>
	/// Who an operative goes after: the player its attack order names, while alive; else
	/// the one it has in its sights; else the nearest player the side remembers, alive and
	/// within its sensor and a little more — on a defend order, within twice its leash of
	/// what it defends. Where it knows them to be: where it sees them, else where the side
	/// last saw them, else where the order said.
	/// </summary>
	private static bool TryQuarry(Unit unit, CombatManager combat, uint tick, out int ownerId, out Vector3 feet)
	{
		ownerId = OwnerId.None;
		feet = Vector3.Zero;
		ContactMemory memory = combat.Visibility?.Contacts;

		if (unit.Order.Kind == OrderKind.Attack && OwnerId.IsPeer(unit.Order.TargetOwnerId)
			&& combat.Find(OwnerId.PeerOf(unit.Order.TargetOwnerId)) is { IsAlive: true, Character: not null } named
			&& named.Team != unit.Team)
		{
			ownerId = unit.Order.TargetOwnerId;
			feet = unit.TargetOwnerId == ownerId ? named.Character.SimPosition
				: TryRemembered(memory, ownerId, tick, out Vector3 known) ? known
				: unit.Order.Target;
			return true;
		}

		if (OwnerId.IsPeer(unit.TargetOwnerId)
			&& combat.Find(OwnerId.PeerOf(unit.TargetOwnerId)) is { IsAlive: true, Character: not null } seen
			&& seen.Team != unit.Team)
		{
			ownerId = unit.TargetOwnerId;
			feet = seen.Character.SimPosition;
			return true;
		}

		if (memory == null)
		{
			return false;
		}

		float reach = unit.Traits.SensorRadiusMeters + HuntBeyondSensorMeters;
		float best = reach * reach;
		for (int i = 0; i < memory.Count; i++)
		{
			KnownContact contact = memory.At(i);
			if (!memory.IsRemembered(i, tick) || !OwnerId.IsPeer(contact.OwnerId)
				|| combat.Find(OwnerId.PeerOf(contact.OwnerId)) is not { IsAlive: true } player || player.Team == unit.Team)
			{
				continue;
			}

			if (unit.Order.Kind == OrderKind.Defend
				&& Flat(unit.Order.Anchor, contact.Position) > unit.Traits.LeashRadiusMeters * DefendHuntLeashes)
			{
				continue;
			}

			float distance = unit.GlobalPosition.DistanceSquaredTo(contact.Position);
			if (distance < best)
			{
				best = distance;
				ownerId = contact.OwnerId;
				feet = contact.Position;
			}
		}

		return ownerId != OwnerId.None;
	}

	/// <summary>
	/// An operative's smoke grenade at <paramref name="at"/>, on the low arc, with its own
	/// cone; nothing when no arc reaches or its hand is not free.
	/// </summary>
	private void ThrowSmoke(Unit unit, Vector3 at, uint tick)
	{
		ProjectileStats round = WeaponCatalog.RoundFor(WeaponCatalog.SmokeGrenade);
		Vector3 eye = unit.EyePosition;
		if (CombatManager.Instance is not { } combat || !GrenadeSim.TrySolve(eye, at, round, out Vector3 aim)
			|| !GrenadeSim.TrySpend(ref unit.Smokes, tick, GrenadeSim.ThrowIntervalTicks))
		{
			return;
		}

		int owner = OwnerId.ForUnit(unit.UnitId);
		Vector3 direction = Spread.Apply(aim, unit.Definition?.AccuracyConeRadians ?? 0f,
			Spread.Seed(owner, WeaponCatalog.SmokeGrenade, unit.Smokes.Thrown));
		combat.SpawnProjectile(tick, owner, WeaponCatalog.SmokeGrenade, eye, direction, catchUpTicks: 0);
		unit.Humanoid?.Throw();
		OperativeSmokes++;
		EmitOperative(unit, act: 1, tick);
	}

	/// <summary>
	/// At a supporting unit's scan: the call for covering fire it should answer, if it has
	/// nothing of its own to shoot (docs/OUTPOST.md §4.5) — a rifleman, tank or heavy gunner
	/// under micro-autonomy, holding, defending, patrolling or attacking, with the called
	/// point in its reach and a clear line to it. One ray, only while a call stands.
	/// </summary>
	private void SenseCoveringFire(Unit unit, uint tick)
	{
		CoveringFireBoard board = _coveringFire[(int)unit.Team];
		bool may = unit.Definition is { GivesCoveringFire: true } && unit.Autonomy == MicroAutonomy.On
			&& unit.TargetOwnerId == OwnerId.None && unit.HasRounds && board.Calls > 0
			&& unit.Order.Kind is OrderKind.None or OrderKind.Defend or OrderKind.Patrol or OrderKind.Attack;

		bool had = unit.HasCoverCall && tick <= unit.CoverCallUntil;
		unit.HasCoverCall = false;
		if (!may || !board.TryAnswer(unit.GlobalPosition, unit.Traits.EngageRangeMeters, tick, out Vector3 quarry))
		{
			return;
		}

		Vector3 point = quarry + (Vector3.Up * CoverCallChestMeters);
		if (!WorldClear(unit.EyePosition, point))
		{
			return;
		}

		unit.HasCoverCall = true;
		unit.CoverCallPoint = point;
		unit.CoverCallUntil = tick + (uint)(SimConfig.UnitTargetRefreshTicks * 2);
		if (!had)
		{
			CoveringFireAnswers++;
			AgentEventBus.Emit(AgentEventKind.CoveringFire, tick, unit.UnitId, unit.DefinitionId, 0, point.X, point.Z);
		}
	}

	/// <summary>Whether the world stands between two points; smoke aside — covering fire goes into it.</summary>
	private bool WorldClear(Vector3 from, Vector3 to)
	{
		PhysicsDirectSpaceState3D space = GetTree()?.Root?.World3D?.DirectSpaceState;
		if (space == null)
		{
			return true;
		}

		var query = PhysicsRayQueryParameters3D.Create(from, to, CollisionLayers.World);
		return space.IntersectRay(query).Count == 0;
	}

	private static void EmitOperative(Unit unit, int act, uint tick)
	{
		Vector3 at = unit.GlobalPosition;
		AgentEventBus.Emit(AgentEventKind.Operative, tick, unit.UnitId, (int)unit.Operative.Phase, act, at.X, at.Z);
	}

	/// <summary>A new round: no calls stand, and nothing has been fired.</summary>
	private void ResetOperatives()
	{
		OperativeBounds = 0;
		OperativeDashes = 0;
		OperativeSmokes = 0;
		CoveringFireAnswers = 0;
		StrikesFlown = 0;
		for (int i = 0; i < _coveringFire.Length; i++)
		{
			_coveringFire[i].Clear();
		}
	}
}
