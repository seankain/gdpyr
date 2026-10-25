using System;
using Gdpyr.Core;
using Gdpyr.Match;
using Gdpyr.Sim;
using Gdpyr.Sim.Htn;
using Godot;

namespace Gdpyr.Rts;

/// <summary>
/// Escorting exposed builders (docs/TACTICS.md §5.10), the engine half: once a tick, before
/// any unit moves, every builder at its site that was fired upon or has an enemy the side
/// remembers near it is handed, with the units that may escort it, to
/// <see cref="EscortPlan.Assign"/>; on each escort's scan, the spot it guards the builder
/// from — a low wall by the builder it can fire over at the threat, else a screen between
/// the two — and whether its covering fire has a line, read into its planner. No ray: the
/// builder's fire memory, what the side remembers, and the cover board.
/// </summary>
public partial class UnitManager
{
	private readonly EscortCharge[] _escortCharges = new EscortCharge[EscortPlan.ChargeCapacity];
	private readonly int[] _escortCounts = new int[EscortPlan.ChargeCapacity];
	private int _escortChargeCount;

	private readonly EscortCandidate[] _escortCandidates = new EscortCandidate[SimConfig.MaxUnits];
	private readonly ushort[] _escortCandidateUnits = new ushort[SimConfig.MaxUnits];
	private readonly int[] _escortChargeOf = new int[SimConfig.MaxUnits];
	private readonly int[] _escortSlotOf = new int[SimConfig.MaxUnits];

	/// <summary>
	/// Who escorts whom this tick (§5.10): the exposed builders, the units that may escort,
	/// and <see cref="EscortPlan.Assign"/> between them, written onto each unit as
	/// <see cref="Unit.Escorting"/>. After the census, before any unit moves, so a unit's scan
	/// and its target search read this tick's duty.
	/// </summary>
	private void AssignEscorts(uint tick)
	{
		ContactMemory memory = CombatManager.Instance?.Visibility?.Contacts;
		_escortChargeCount = 0;
		for (int i = 0; i < _ordered.Count && _escortChargeCount < _escortCharges.Length; i++)
		{
			Unit builder = _ordered[i];
			if (!builder.IsOnField || builder.Team != Team.Strategist || builder.Order.Kind != OrderKind.Build
				|| !UnitCatalog.CanConstruct(builder.DefinitionId)
				|| !EscortPlan.AtSite(builder.GlobalPosition, builder.Order.Anchor))
			{
				continue;
			}

			bool fired = FiredUpon.IsRecent(builder.Fire, tick);
			if (!EscortPlan.TryThreat(builder.GlobalPosition, fired, FiredFromFeet(builder), memory, tick, IsAlivePlayer,
				out Vector3 threat))
			{
				continue;
			}

			_escortCharges[_escortChargeCount++] = new EscortCharge
			{
				UnitId = builder.UnitId,
				Position = builder.GlobalPosition,
				Threat = threat,
				FiredUpon = fired,
				FiredFrom = builder.Fire.Origin,
				FiredTick = builder.Fire.LastTick,
				ShooterOwnerId = fired ? builder.Fire.ShooterOwnerId : OwnerId.None,
			};
		}

		int candidates = 0;
		for (int i = 0; i < _ordered.Count; i++)
		{
			Unit unit = _ordered[i];
			ushort previous = unit.Escorting;
			unit.Escorting = 0;
			unit.EscortSlot = 0;
			if (_escortChargeCount == 0 || !MayEscort(unit) || candidates >= _escortCandidates.Length)
			{
				continue;
			}

			_escortCandidateUnits[candidates] = unit.UnitId;
			_escortCandidates[candidates++] = new EscortCandidate
			{
				UnitId = unit.UnitId,
				Position = unit.GlobalPosition,
				Anchor = unit.Order.Anchor,
				LeashMeters = unit.Traits.LeashRadiusMeters,
				Previous = previous,
			};
		}

		if (_escortChargeCount == 0 || candidates == 0)
		{
			Array.Clear(_escortCounts);
			return;
		}

		EscortPlan.Assign(_escortCharges.AsSpan(0, _escortChargeCount), _escortCandidates.AsSpan(0, candidates),
			_escortChargeOf, _escortSlotOf, _escortCounts);
		for (int i = 0; i < candidates; i++)
		{
			int charge = _escortChargeOf[i];
			if (charge >= 0 && Find(_escortCandidateUnits[i]) is { } unit)
			{
				unit.Escorting = _escortCharges[charge].UnitId;
				unit.EscortSlot = _escortSlotOf[i];
			}
		}
	}

	/// <summary>
	/// Whether a unit may escort a builder (§5.10): a planning unit of the strategist's on the
	/// field and on foot, its micro-autonomy on, a rifleman or a heavy gunner, holding ground —
	/// a defend order or none — and its squad not falling back.
	/// </summary>
	private bool MayEscort(Unit unit)
	{
		if (unit.Plan == null || !unit.IsOnField || unit.IsAircraft || unit.Team != Team.Strategist
			|| unit.Autonomy != MicroAutonomy.On || !UnitContext.Escorts(unit.Traits.Doctrine)
			|| !UnitContext.EscortsUnder(unit.Order.Kind))
		{
			return false;
		}

		int squad = Squads.SquadOf(unit.UnitId);
		return squad < 0 || Squads.At(squad).Phase != SquadPhase.FallingBack;
	}

	/// <summary>The exposed builder <paramref name="builderId"/> stands for this tick, and how many escort it.</summary>
	private bool TryEscortCharge(ushort builderId, out EscortCharge charge, out int escorts)
	{
		for (int i = 0; builderId != 0 && i < _escortChargeCount; i++)
		{
			if (_escortCharges[i].UnitId == builderId)
			{
				charge = _escortCharges[i];
				escorts = _escortCounts[i];
				return true;
			}
		}

		charge = default;
		escorts = 0;
		return false;
	}

	/// <summary>
	/// Whoever is shooting at the builder a unit escorts, as a peer id, for its target search
	/// to try first after its own shooter (§5.10); 0 for none.
	/// </summary>
	private int EscortShooter(Unit unit) =>
		unit.Plan != null && unit.Autonomy == MicroAutonomy.On && unit.Escorting != 0
		&& TryEscortCharge(unit.Escorting, out EscortCharge charge, out _) && charge.FiredUpon
			? PeerId(charge.ShooterOwnerId)
			: 0;

	/// <summary>
	/// The escort's spot and covering fire, on its scan (§5.10), into its planner: none for a
	/// unit escorting nobody. A low wall within <see cref="EscortPlan.SpotMeters"/> of the
	/// builder that it can fire over at the threat, inside its leash, by the cover board's
	/// rules and claims — unless it already holds a spot from the fire it is reacting to
	/// (<paramref name="mayTakeCover"/> false), one spot a unit; else a screen between the
	/// builder and the threat, brought inside its leash. Its fire may go where the builder's
	/// came from when that is in its engage range from its standing eye with a line over the
	/// board. Returns true for a unit on the duty: the fight's cover is not looked for.
	/// </summary>
	private bool SenseEscort(Unit unit, UnitContext plan, in UnitOrder order, bool mayTakeCover)
	{
		if (unit.Escorting == 0 || !TryEscortCharge(unit.Escorting, out EscortCharge charge, out int escorts))
		{
			plan.SenseGuard(false, Vector3.Zero, false, false, Vector3.Zero, 0);
			return false;
		}

		bool covered = false;
		Vector3 spot = Vector3.Zero;
		if (mayTakeCover && Cover.Count > 0)
		{
			CoverQuery query = UnitCoverQuery(unit, charge.Threat, mustFire: true, EscortPlan.SpotMeters);
			query.From = charge.Position;
			if (UpdateUnitCover(unit, query, out CoverSpot found) && InLeash(order, found.Position, unit))
			{
				covered = true;
				spot = found.Position;
			}
			else
			{
				unit.HasCoverSpot = false;
			}
		}

		if (!covered)
		{
			spot = EscortPlan.IntoLeash(EscortPlan.ScreenPoint(charge.Position, charge.Threat, unit.EscortSlot, escorts),
				order.Anchor, unit.Traits.LeashRadiusMeters);
		}

		Vector3 eye = unit.GlobalPosition + (Vector3.Up * unit.Body.EyeHeight(UnitPosture.Standing));
		bool fires = charge.FiredUpon && eye.DistanceTo(charge.FiredFrom) <= unit.Traits.EngageRangeMeters
			&& Cover.ClearLine(eye, charge.FiredFrom);
		plan.SenseGuard(true, spot, covered, fires, charge.FiredFrom, charge.FiredTick);
		return true;
	}

	/// <summary>Builders that have an escort this tick. Server-side, for the debug HUD's <c>fire</c> row.</summary>
	private int BuildersEscorted()
	{
		int count = 0;
		for (int i = 0; i < _escortChargeCount; i++)
		{
			count += _escortCounts[i] > 0 ? 1 : 0;
		}

		return count;
	}
}
