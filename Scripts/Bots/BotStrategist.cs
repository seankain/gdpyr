using System;
using Gdpyr.Core;
using Gdpyr.Fps;
using Gdpyr.Match;
using Gdpyr.Net;
using Gdpyr.Rts;
using Gdpyr.Sim;
using Godot;

namespace Gdpyr.Bots;

/// <summary>
/// One computer strategist (docs/IMPLEMENTATION_PLAN.md §M3.5).
///
/// It holds a strategist slot like a person does — the same peer id, the same
/// entry in <see cref="TeamService"/>, the same cap of two — and plays through the
/// same two server-side requests a human client's RPCs land in: queue a unit, and
/// order units about (docs/NETCODE.md §6.3). Nothing here is privileged; an order
/// it gives goes through the identical ownership checks, so a bug that would let a
/// bot command somebody else's units is a bug that would let a client do it too.
///
/// Its intelligence is its units' intelligence: the only way it learns where the
/// ground force is, is that one of its own riflemen can see somebody. Until M4
/// that was read off <see cref="Unit.TargetOwnerId"/>, which is what a unit has
/// *acquired* and therefore a little narrower than what the side can see; it now
/// reads the same <see cref="VisibilityService"/> the human strategist's snapshot
/// is filtered through, so the two are behind one fog and not two
/// (docs/NETCODE.md §6.2, §9).
/// </summary>
public sealed class BotStrategist
{
	/// <summary>Ticks between sweeping the next spawn area when nothing has been seen.</summary>
	private const int SweepIntervalTicks = SimConfig.TickRate * 25;

	private readonly int _peerId;
	private readonly StrategistTraits _traits;

	/// <summary>Order batches, pre-sized: the plan forbids per-tick allocation on the unit path.</summary>
	private readonly int[] _assault = new int[SimConfig.MaxUnits];
	private readonly int[] _garrison = new int[SimConfig.MaxUnits];

	/// <summary>The army it already has, by tier, and what each tier costs. Rebuilt per decision.</summary>
	private readonly int[] _census = new int[SimConfig.MaxUnitDefinitions];
	private readonly int[] _costs = new int[SimConfig.MaxUnitDefinitions];

	/// <summary>Builders with nothing to build, gathered per decision.</summary>
	private readonly int[] _idleBuilders = new int[SimConfig.MaxUnits];

	/// <summary>The places it means to fortify, and which resource node each one is.</summary>
	private readonly FortificationSite[] _sites = new FortificationSite[SimConfig.MaxResourceNodes];
	private readonly int[] _siteNodes = new int[SimConfig.MaxResourceNodes];

	/// <summary>
	/// Per node and structure, the tick a refused placement may be tried again on.
	/// A site the server refuses — a wall in the way, ground a builder cannot reach —
	/// would otherwise be asked for twice a second for the rest of the round.
	/// </summary>
	private readonly uint[] _retryTick = new uint[SimConfig.MaxResourceNodes * StructureKinds.Count];

	/// <summary>How long a refused placement is left alone before it is tried again.</summary>
	private const int PlacementRetryTicks = SimConfig.TickRate * 30;

	/// <summary>
	/// Points held back from the queue this decision for a structure a builder is
	/// waiting to put up (<see cref="StrategistBrain.StructureSavings"/>). Set by
	/// <see cref="Fortify"/>, which runs first, and read by <see cref="Build"/>.
	/// </summary>
	private int _savings;

	private uint _nextDecisionTick;

	public BotStrategist(int peerId, in StrategistTraits traits)
	{
		_peerId = peerId;
		_traits = traits;
	}

	public int PeerId => _peerId;

	/// <summary>Units it has ordered forward at the last decision. For the debug HUD.</summary>
	public int AssaultCount { get; private set; }

	/// <summary>Units it is keeping at home. For the debug HUD.</summary>
	public int GarrisonCount { get; private set; }

	/// <summary>
	/// One decision, at most every <see cref="StrategistTraits.DecisionIntervalTicks"/>.
	/// Called every server tick by <see cref="BotDirector"/>, which does not itself
	/// know how often a strategist should think.
	/// </summary>
	public void ServerTick(uint tick)
	{
		if (tick < _nextDecisionTick)
		{
			return;
		}

		// Staggered off the peer id so two strategists do not decide on the same tick.
		_nextDecisionTick = tick + (uint)_traits.DecisionIntervalTicks
			+ (uint)(_peerId % _traits.DecisionIntervalTicks);

		CombatManager combat = CombatManager.Instance;
		UnitManager units = UnitManager.Instance;
		if (combat == null || units == null || combat.TeamOf(_peerId) != Team.Strategist)
		{
			return;
		}

		// Nothing is worth spending or ordering between rounds: the field is cleared
		// and the queues emptied when the next one starts (UnitManager.ClearUnits).
		if (combat.Match.Phase == RoundPhase.Ended)
		{
			return;
		}

		// Fortify first: it decides whether this decision's points are the army's or
		// a waiting builder's.
		Fortify(tick, combat, units);
		Build(combat, units);
		Command(tick, combat, units);
	}

	/// <summary>
	/// Puts units on every barracks queue it owns, while the points last.
	///
	/// Every barracks, not only the first: the strategist HUD can address one today
	/// (docs/IMPLEMENTATION_PLAN.md §7) but the RPC has always taken an index, and a
	/// bot that only ever used one door would make a two-door map read as a broken
	/// map.
	///
	/// Since M5 it also has three tiers to choose between. The choice is
	/// <see cref="StrategistBrain.TryChooseTier"/> and the census it needs is taken
	/// here, once per decision rather than once per barracks.
	/// </summary>
	private void Build(CombatManager combat, UnitManager units)
	{
		// The fighting tiers only: a builder is not the best thing it can afford,
		// whatever its index says (Fortify buys those).
		int tiers = Math.Min(UnitCatalog.Tiers.Length, Math.Min(UnitCatalog.Count, SimConfig.MaxUnitDefinitions));
		if (tiers == 0)
		{
			return;
		}

		Array.Clear(_census, 0, tiers);
		for (int i = 0; i < tiers; i++)
		{
			_costs[i] = UnitCatalog.CostOf((byte)i);
		}

		for (int i = 0; i < units.SlotCount; i++)
		{
			Unit unit = units.UnitAt(i);
			if (unit != null && unit.IsAlive && unit.Team == Team.Strategist && unit.DefinitionId < tiers)
			{
				_census[unit.DefinitionId]++;
			}
		}

		int live = units.LiveUnitCount;

		for (int i = 0; i < units.BarracksCount; i++)
		{
			Barracks barracks = units.BarracksAt(i);
			if (barracks == null || barracks.Team != Team.Strategist)
			{
				continue;
			}

			// The balance is re-read per barracks because the previous one may just
			// have spent it, less whatever a waiting builder is being saved for.
			int points = combat.Match.StrategistPoints - _savings;
			if (!StrategistBrain.TryChooseTier(points, _costs.AsSpan(0, tiers), _census.AsSpan(0, tiers),
				_traits, out byte tier))
			{
				continue;
			}

			if (!StrategistBrain.ShouldQueue(points, _costs[tier], barracks.Queue.Count, live, _traits,
				SimConfig.MaxUnits))
			{
				continue;
			}

			if (units.ServerQueueUnit(_peerId, i, tier))
			{
				// Counted as though it were already on the field, so that two barracks
				// do not each decide the army is short of the same tank.
				_census[tier]++;
			}
		}
	}

	/// <summary>
	/// Sorts the units into a garrison and an assault, and gives each group the one
	/// order it needs. Two messages at most per decision, however many units there
	/// are, because every unit in a group is being sent to the same place.
	/// </summary>
	private void Command(uint tick, CombatManager combat, UnitManager units)
	{
		bool hasObjective = TryObjective(tick, combat, units, out Vector3 objective, out int targetOwnerId);
		Vector3 home = Home(units, objective, hasObjective);

		int assault = 0;
		int garrison = 0;
		int assigned = 0;
		int assaultOrders = 0;
		int garrisonOrders = 0;

		for (int i = 0; i < units.SlotCount; i++)
		{
			Unit unit = units.UnitAt(i);
			if (unit == null || !unit.IsAlive || unit.Team != Team.Strategist)
			{
				continue;
			}

			// Builders are Fortify's, and are never sent to fight.
			if (UnitCatalog.CanConstruct(unit.DefinitionId))
			{
				continue;
			}

			// Roles are assigned in registry order, so a garrison that loses somebody
			// is refilled by the next unit to need an order rather than by a bespoke
			// bookkeeping pass.
			bool defends = StrategistBrain.ShouldGarrison(assigned, _traits);
			assigned++;

			if (defends)
			{
				garrison++;
			}
			else
			{
				assault++;
				if (!hasObjective)
				{
					// Nothing seen and nowhere to sweep: it keeps whatever it was doing.
					continue;
				}
			}

			Vector3 destination = defends ? home : objective;
			OrderKind wanted = StrategistBrain.OrderFor(defends);
			float drift = unit.Order.Target.DistanceTo(destination);
			if (!StrategistBrain.NeedsOrder(unit.State, unit.Order.Kind, wanted, drift, _traits))
			{
				continue;
			}

			if (defends)
			{
				_garrison[garrisonOrders++] = unit.UnitId;
			}
			else
			{
				_assault[assaultOrders++] = unit.UnitId;
			}
		}

		AssaultCount = assault;
		GarrisonCount = garrison;

		if (garrisonOrders > 0)
		{
			units.ServerIssueOrder(_peerId, _garrison.AsSpan(0, garrisonOrders),
				StrategistBrain.OrderFor(garrison: true), home, OwnerId.None);
		}

		if (assaultOrders > 0)
		{
			// Attack-move rather than move: a bot has no plan past "go where they
			// are", so stopping to fight whatever it meets on the way *is* the plan
			// (StrategistBrain.OrderFor).
			units.ServerIssueOrder(_peerId, _assault.AsSpan(0, assaultOrders),
				StrategistBrain.OrderFor(garrison: false), objective, targetOwnerId);
		}
	}

	/// <summary>
	/// Keeps a builder and uses it (docs/NETCODE.md §10.5): one is bought once there
	/// is an army out, an idle one finishes any site that has nobody working on it,
	/// and otherwise fortifies the resource nodes — the ones it holds first, then the
	/// neutral ones, nearest home first — with a pillbox, then a wall in front of it,
	/// then a tower behind it, facing the ground force's spawn.
	///
	/// The nodes, because they are what the strategist's income is and what the
	/// ground force walks to: a pillbox on one is the thing standing on it that a
	/// rifleman cannot. The spawn as the threat is static map geometry, as the
	/// assault's sweep is; nothing here reads where anybody actually is.
	/// </summary>
	private void Fortify(uint tick, CombatManager combat, UnitManager units)
	{
		_savings = 0;
		if (_traits.Builders == 0 || !UnitCatalog.IsBuildable(UnitCatalog.Builder))
		{
			return;
		}

		int builders = 0;
		int idle = 0;
		for (int i = 0; i < units.SlotCount; i++)
		{
			Unit unit = units.UnitAt(i);
			if (unit == null || !unit.IsAlive || unit.Team != Team.Strategist
				|| !UnitCatalog.CanConstruct(unit.DefinitionId))
			{
				continue;
			}

			builders++;
			if (unit.Order.Kind != OrderKind.Build)
			{
				_idleBuilders[idle++] = unit.UnitId;
			}
		}

		QueueBuilder(combat, units, builders);

		if (idle == 0)
		{
			return;
		}

		ReadOnlySpan<int> free = _idleBuilders.AsSpan(0, idle);

		// A site somebody paid for and nobody is building is money on the floor.
		int abandoned = AbandonedSite(units);
		if (abandoned >= 0)
		{
			units.ServerAssist(_peerId, free, abandoned);
			return;
		}

		PlayerManager players = PlayerManager.Instance;
		if (players == null || players.SpawnPointCount == 0)
		{
			return;
		}

		Vector3 threat = players.SpawnPositionAt(0);
		int sites = CollectSites(tick, combat.Economy, units, threat);
		if (!StrategistBrain.TryPlanStructure(_sites.AsSpan(0, sites), out int site, out byte kind))
		{
			return;
		}

		int cost = StructureCatalog.CostOf(kind);
		if (combat.Match.StrategistPoints < cost)
		{
			_savings = StrategistBrain.StructureSavings(cost, builderWaiting: true, units.LiveUnitCount - builders,
				_traits);
			return;
		}

		StrategistBrain.Layout(_sites[site].Anchor, threat, kind, out Vector3 at, out float facing);
		PlacementResult result = units.ServerConstruct(_peerId, free, kind, at, facing, out _);
		if (result != PlacementResult.Ok && result != PlacementResult.CannotAfford)
		{
			_retryTick[(_siteNodes[site] * StructureKinds.Count) + kind] = tick + PlacementRetryTicks;
		}
	}

	/// <summary>Puts a builder on the first queue it owns, when it has fewer than it wants.</summary>
	private void QueueBuilder(CombatManager combat, UnitManager units, int builders)
	{
		int queued = 0;
		int door = -1;
		for (int i = 0; i < units.BarracksCount; i++)
		{
			Barracks barracks = units.BarracksAt(i);
			if (barracks == null || barracks.Team != Team.Strategist)
			{
				continue;
			}

			queued += barracks.Queue.CountOf(UnitCatalog.Builder);
			if (door < 0)
			{
				door = i;
			}
		}

		int fighting = units.LiveUnitCount - builders;
		if (door >= 0 && StrategistBrain.ShouldQueueBuilder(combat.Match.StrategistPoints,
			UnitCatalog.CostOf(UnitCatalog.Builder), builders, queued, fighting, _traits))
		{
			units.ServerQueueUnit(_peerId, door, UnitCatalog.Builder);
		}
	}

	/// <summary>The slot of a site of its own that no builder is working on, or -1.</summary>
	private static int AbandonedSite(UnitManager units)
	{
		for (int slot = 0; slot < SimConfig.MaxStructures; slot++)
		{
			Structure structure = units.StructureAt(slot);
			if (structure == null || structure.IsBuilt || structure.IsDestroyed || structure.Team != Team.Strategist)
			{
				continue;
			}

			bool worked = false;
			for (int i = 0; i < units.SlotCount && !worked; i++)
			{
				Unit unit = units.UnitAt(i);
				worked = unit != null && unit.IsAlive && unit.Order.Kind == OrderKind.Build
					&& unit.Order.TargetOwnerId == structure.ShooterId;
			}

			if (!worked)
			{
				return slot;
			}
		}

		return -1;
	}

	/// <summary>
	/// The nodes worth fortifying, most important first — held, then neutral; nearest
	/// home first within each — with what already stands at each. A node the ground
	/// force holds or is fighting over is not somewhere to send a builder.
	/// </summary>
	private int CollectSites(uint tick, EconomyService economy, UnitManager units, Vector3 threat)
	{
		int count = 0;
		if (economy == null)
		{
			return 0;
		}

		Vector3 home = Home(units, threat, hasObjective: true);

		for (int pass = 0; pass < 2; pass++)
		{
			NodeHolder wanted = pass == 0 ? NodeHolder.Strategist : NodeHolder.Neutral;
			int first = count;

			for (int i = 0; i < economy.NodeCount && count < _sites.Length; i++)
			{
				ResourceNode node = economy.NodeAt(i);
				if (node == null || node.Capture.Owner != wanted || node.Capture.Contested)
				{
					continue;
				}

				var site = new FortificationSite { Anchor = node.GlobalPosition, Threat = threat };
				Survey(units, ref site);

				int retry = i * StructureKinds.Count;
				site.HasPillbox |= tick < _retryTick[retry + StructureKinds.Pillbox];
				site.HasWall |= tick < _retryTick[retry + StructureKinds.SandbagWall];
				site.HasTower |= tick < _retryTick[retry + StructureKinds.SniperTower];

				// Nearest home first, by insertion into this pass's run.
				float distance = home.DistanceSquaredTo(site.Anchor);
				int at = count;
				while (at > first && home.DistanceSquaredTo(_sites[at - 1].Anchor) > distance)
				{
					_sites[at] = _sites[at - 1];
					_siteNodes[at] = _siteNodes[at - 1];
					at--;
				}

				_sites[at] = site;
				_siteNodes[at] = i;
				count++;
			}
		}

		return count;
	}

	/// <summary>What of its own already stands within reach of a site, finished or going up.</summary>
	private static void Survey(UnitManager units, ref FortificationSite site)
	{
		float radius = StrategistBrain.SiteRadiusMeters;
		for (int slot = 0; slot < SimConfig.MaxStructures; slot++)
		{
			Structure structure = units.StructureAt(slot);
			if (structure == null || structure.IsDestroyed || structure.Team != Team.Strategist
				|| structure.Pose.Base.DistanceSquaredTo(site.Anchor) > radius * radius)
			{
				continue;
			}

			switch (structure.Kind)
			{
				case StructureKinds.Pillbox: site.HasPillbox = true; break;
				case StructureKinds.SandbagWall: site.HasWall = true; break;
				case StructureKinds.SniperTower: site.HasTower = true; break;
			}
		}
	}

	/// <summary>
	/// Where to send the assault: the nearest player its units can see, or — when
	/// nothing has been seen — the ground force's spawn areas, walked one at a time.
	///
	/// "Can see" is <see cref="VisibilityService"/>, the same answer the human
	/// strategist's packet is built from, so neither of them knows anything the
	/// other would not (docs/IMPLEMENTATION_PLAN.md §M4). It deliberately reads the
	/// live contact rather than the last known position: a bot that chased ghosts
	/// would be a different opponent from the one this exists to be, and the
	/// position it orders an attack on is the one the fog is currently offering.
	///
	/// Sweeping known spawns is not cheating: they are static map geometry, which a
	/// human strategist can see on the same screen. What the bot does not get is a
	/// position for a player nobody is looking at.
	/// </summary>
	private bool TryObjective(uint tick, CombatManager combat, UnitManager units, out Vector3 objective,
		out int targetOwnerId)
	{
		objective = Vector3.Zero;
		targetOwnerId = OwnerId.None;

		PlayerCombat best = null;
		float bestDistance = float.MaxValue;

		for (int i = 0; i < combat.PlayerCount; i++)
		{
			PlayerCombat seen = combat.PlayerAt(i);
			if (seen?.Character == null || !seen.IsAlive || seen.Team != Team.GroundForce
				|| combat.Visibility?.IsVisible(seen.PeerId) != true)
			{
				continue;
			}

			float distance = NearestUnitDistance(units, seen.Character.SimPosition);
			if (distance < bestDistance)
			{
				bestDistance = distance;
				best = seen;
			}
		}

		if (best != null)
		{
			objective = best.Character.SimPosition;
			targetOwnerId = OwnerId.ForPeer(best.PeerId);
			return true;
		}

		PlayerManager players = PlayerManager.Instance;
		if (players == null || players.SpawnPointCount == 0)
		{
			return false;
		}

		uint sweep = tick / (uint)SweepIntervalTicks;
		objective = players.SpawnPositionAt((int)(sweep % (uint)players.SpawnPointCount));
		return true;
	}

	/// <summary>
	/// How far the nearest unit is from a contact. It decides which of several
	/// contacts the assault is sent at, so it is measured from the army and not from
	/// the bot, which has no position of its own.
	/// </summary>
	private static float NearestUnitDistance(UnitManager units, Vector3 point)
	{
		float nearest = float.MaxValue;

		for (int i = 0; i < units.SlotCount; i++)
		{
			Unit unit = units.UnitAt(i);
			if (unit == null || !unit.IsAlive || unit.Team != Team.Strategist)
			{
				continue;
			}

			float distance = unit.GlobalPosition.DistanceTo(point);
			if (distance < nearest)
			{
				nearest = distance;
			}
		}

		return nearest;
	}

	/// <summary>
	/// What the garrison defends: the first barracks it owns, or the objective when
	/// the map has none — a defend order with no anchor would park every unit on the
	/// origin.
	/// </summary>
	private static Vector3 Home(UnitManager units, Vector3 objective, bool hasObjective)
	{
		for (int i = 0; i < units.BarracksCount; i++)
		{
			Barracks barracks = units.BarracksAt(i);
			if (barracks != null && barracks.Team == Team.Strategist)
			{
				return barracks.RallyPoint;
			}
		}

		return hasObjective ? objective : Vector3.Zero;
	}
}
