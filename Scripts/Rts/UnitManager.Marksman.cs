using System;
using Gdpyr.Core;
using Gdpyr.Match;
using Gdpyr.Sim;
using Gdpyr.Sim.Htn;
using Godot;

namespace Gdpyr.Rts;

/// <summary>
/// A marksman's concealment (docs/TACTICS.md §5.3): on its scan, a sniper under
/// micro-autonomy gathers the threats the side knows of within
/// <see cref="MarksmanPlan.ThreatMeters"/>, picks the one to shoot, and keeps or finds
/// a peek pair on the cover board — a spot no threat sees, and beside it a spot that
/// sees the target; its planner says when to hide, pop out, fire, duck and move on. No
/// ray: what the side remembers, and the cover board.
/// </summary>
public partial class UnitManager
{
	/// <summary>One scan's threats' feet: every ground-force player the side remembers in reach, and where the unit was last fired upon from.</summary>
	private readonly Vector3[] _peekThreats = new Vector3[SnapshotCodec.MaxPlayers + 1];

	/// <summary>One scan's contacts it could shoot.</summary>
	private readonly MarksmanCandidate[] _peekCandidates = new MarksmanCandidate[SnapshotCodec.MaxPlayers];

	/// <summary>Reused by every relocation: the fire spots it keeps away from.</summary>
	private readonly Vector3[] _recentFireSpots = new Vector3[UnitContext.RecentFireCapacity];

	/// <summary>
	/// The pair and target a marksman's planner works from, on its scan, into its
	/// <see cref="UnitFact.Peek"/>, <see cref="UnitFact.PeekTarget"/> and
	/// <see cref="UnitFact.Standoff"/>: none unless it may (micro-autonomy, the marksman's
	/// doctrine, an order it peeks under, no walk given up on lately) and the side knows
	/// of a threat. A pair it has is kept while it still does; a burned one is not, and
	/// the next is found away from where it last fired.
	/// </summary>
	private void SenseMarksman(Unit unit, UnitContext plan, in UnitOrder order, uint tick, ContactMemory memory,
		bool objective)
	{
		bool may = unit.Autonomy == MicroAutonomy.On && unit.Traits.Doctrine == UnitDoctrine.Marksman
			&& unit.Team == Team.Strategist && Cover.Count > 0 && UnitContext.PeeksUnder(order.Kind, objective)
			&& tick >= plan.PeekRetryTick;
		if (!may)
		{
			NoPeek(unit, plan, standoff: false);
			return;
		}

		// What the side remembers is ground-force players, where it last saw them: live
		// and ghosts both, as a place an enemy was is one a marksman hides from. Not one
		// that has died since.
		Vector3 at = unit.GlobalPosition;
		int threats = 0;
		int candidates = 0;
		bool standoff = false;
		int nearest = -1;
		float nearestMeters = float.MaxValue;
		CombatManager combat = CombatManager.Instance;
		for (int i = 0; memory != null && i < memory.Count && candidates < _peekCandidates.Length; i++)
		{
			KnownContact known = memory.At(i);
			if (!memory.IsRemembered(i, tick) || combat?.Find(PeerId(known.OwnerId)) is not { IsAlive: true })
			{
				continue;
			}

			float meters = Flat(at, known.Position);
			if (meters > MarksmanPlan.ThreatMeters)
			{
				continue;
			}

			standoff |= meters < MarksmanPlan.StandoffMeters;
			if (meters < nearestMeters)
			{
				nearest = threats;
				nearestMeters = meters;
			}

			_peekThreats[threats++] = known.Position;
			_peekCandidates[candidates++] = new MarksmanCandidate(known.OwnerId, known.Position,
				new Vector2(known.Velocity.X, known.Velocity.Z).Length(), known.HealthFraction,
				EngagedByFriend(unit, known.OwnerId));
		}

		if (FiredUpon.IsRecent(unit.Fire, tick))
		{
			Vector3 from = FiredFromFeet(unit);
			float meters = Flat(at, from);
			standoff |= meters < MarksmanPlan.StandoffMeters;
			if (meters < nearestMeters)
			{
				nearest = threats;
			}

			_peekThreats[threats++] = from;
		}

		UnitBody body = unit.Body;
		var query = new PeekQuery
		{
			From = at,
			ThreatEyeMeters = CoverThreatEyeMeters,
			SearchMeters = MarksmanPlan.SearchMeters,
			MinRangeMeters = MarksmanPlan.MinRangeMeters,
			MaxRangeMeters = MathF.Min(MarksmanPlan.MaxRangeMeters, unit.Traits.EngageRangeMeters),
			StandoffMeters = MarksmanPlan.StandoffMeters,
			PairMeters = MarksmanPlan.PairMeters,
			BodyRadiusMeters = body.RadiusMeters,
			HeightMeters = body.Height(UnitPosture.Standing),
			EyeMeters = body.EyeHeight(UnitPosture.Standing),
			CrouchHeightMeters = body.CanCrouch ? body.CrouchHeightMeters : 0f,
			HasLeash = order.Kind is OrderKind.Defend or OrderKind.None,
			LeashAnchor = order.Anchor,
			LeashMeters = unit.Traits.LeashRadiusMeters,
			HasAxis = plan.HasSquad && plan.SquadAlive > 1,
			AxisFrom = plan.SquadCentroid,
		};

		// Moving on from a burned pair: away from the last fire spot, and from every recent one.
		int recent = 0;
		if (plan.Is(UnitFact.Burned))
		{
			query.HasLast = plan.HasLastFireSpot;
			query.Last = plan.LastFireSpot;
			query.LastMeters = plan.Traits.RelocateMeters;
			query.RecentMeters = plan.Traits.RecentFireMeters;
			recent = plan.RecentFireSpots(_recentFireSpots);
		}

		// Whom to peek at, and the pair: the pair it has, while it still sees the one it
		// would choose; else the best pair against that one, then against the next it would
		// choose, and so on; else one that only hides it from the nearest threat. A target
		// no fire spot in reach sees is no reason to stand in the open.
		ReadOnlySpan<Vector3> threatSpan = _peekThreats.AsSpan(0, threats);
		ReadOnlySpan<Vector3> recentSpan = _recentFireSpots.AsSpan(0, recent);
		int targetOwnerId = OwnerId.None;
		PeekPair pair = default;
		bool found = false;
		for (int left = candidates; left > 0 && !found; left--)
		{
			int target = MarksmanPlan.ChooseTarget(_peekCandidates.AsSpan(0, left), plan.FocusOwnerId);
			query.Target = _peekCandidates[target].Feet;
			found = TryPeek(unit, query, threatSpan, recentSpan, out pair);
			targetOwnerId = _peekCandidates[target].OwnerId;

			// Tried: out of the running, behind the ones still in it.
			(_peekCandidates[target], _peekCandidates[left - 1]) = (_peekCandidates[left - 1], _peekCandidates[target]);
		}

		if (!found && nearest >= 0)
		{
			query.Target = _peekThreats[nearest];
			found = TryPeek(unit, query, threatSpan, recentSpan, out pair);
			targetOwnerId = OwnerId.None;
		}

		unit.HasPeek = found;
		unit.Peek = pair;
		if (!found)
		{
			NoPeek(unit, plan, standoff);
			return;
		}

		plan.SensePeek(true, pair.Hide, pair.Fire, pair.Low, targetOwnerId, standoff);
	}

	/// <summary>
	/// The unit's pair when it still does for the query, else the best one nobody else
	/// has, or none. Its own pair stays its own until the scan has settled on another.
	/// </summary>
	private bool TryPeek(Unit unit, in PeekQuery query, ReadOnlySpan<Vector3> threats, ReadOnlySpan<Vector3> recent,
		out PeekPair pair)
	{
		if (unit.HasPeek && Cover.KeepsPeek(unit.Peek, query, threats, recent))
		{
			pair = unit.Peek;
			return true;
		}

		return Cover.TryFindPeek(query, threats, recent, CoverClaims(unit, unit.Team), out pair);
	}

	/// <summary>No pair: the marksman fights as a rifleman, and lets its hide spot go.</summary>
	private static void NoPeek(Unit unit, UnitContext plan, bool standoff)
	{
		unit.HasPeek = false;
		plan.SensePeek(false, Vector3.Zero, Vector3.Zero, false, OwnerId.None, standoff);
	}

	/// <summary>Whether a unit of <paramref name="self"/>'s side other than it has <paramref name="ownerId"/> as its target.</summary>
	private bool EngagedByFriend(Unit self, int ownerId)
	{
		for (int i = 0; i < _ordered.Count; i++)
		{
			Unit unit = _ordered[i];
			if (unit != self && unit.IsOnField && unit.Team == self.Team && unit.TargetOwnerId == ownerId)
			{
				return true;
			}
		}

		return false;
	}

	/// <summary>
	/// Whether a unit holding its fire (<see cref="UnitIntent.HoldFire"/>) leaves a target
	/// <paramref name="targetDistance"/> off alone: anything beyond the plan's
	/// <see cref="UnitPlanTraits.HoldFireMeters"/>.
	/// </summary>
	private static bool HoldsFire(UnitContext plan, bool hasTarget, float targetDistance) =>
		plan is { Intent.HoldFire: true } && hasTarget && targetDistance > plan.Traits.HoldFireMeters;
}
