using System;
using Gdpyr.Match;
using Gdpyr.Sim;
using Gdpyr.Sim.Agent;
using Gdpyr.Sim.Htn;
using Godot;

namespace Gdpyr.Rts;

/// <summary>
/// The squad drill's engine half (docs/TACTICS.md §5.5): every board squad, on its
/// decision, handed to <see cref="SquadDrill"/> as its members, its order and the spots the
/// rest of the side has taken; and on each unit's scan, what its squad's drill told it, read
/// back off the <see cref="DrillBoard"/> into its planner. No ray: the side's memory, the
/// units' fire memories and the cover board.
/// </summary>
public partial class UnitManager
{
	/// <summary>The drill every board squad shares under <c>--bot-ai htn</c>; null under legacy.</summary>
	private SquadDrill _drill;

	private readonly DrillMember[] _drillMembers = new DrillMember[SimConfig.MaxUnits];
	private readonly ushort[] _drillUnits = new ushort[SimConfig.MaxUnits];
	private readonly Vector3[] _drillClaims = new Vector3[SquadDrill.ClaimCapacity];
	private readonly DrillPhase[] _drillPhases = new DrillPhase[SquadBoard.DefaultSquads];

	/// <summary>One gunner's places in a drill, in the drill's order: the threat point first, or after the lift where it shifts to.</summary>
	private readonly Vector3[] _drillPlaces = new Vector3[DrillBoard.EntryCapacity];

	/// <summary>The board squads' drills, for the spectator's debugger and the debug HUD; null under legacy and on a client.</summary>
	public SquadDrill Drill => _drill;

	/// <summary>
	/// Each board squad's drill decision, staggered by slot so eight squads make at most one
	/// a tick (§5.5, "cadence"); after the census, before any unit moves. A squad that does
	/// not drill has its drill stopped; one whose phase changed is on the agent stream.
	/// </summary>
	private void RunDrills(uint tick)
	{
		ContactMemory memory = CombatManager.Instance?.Visibility?.Contacts;
		for (int s = 0; s < Squads.Capacity && s < _drillPhases.Length; s++)
		{
			if (!_drill.IsDue(s, tick))
			{
				continue;
			}

			Squad squad = Squads.At(s);
			DrillOrder order = DrillOrderOf(squad);
			int count = order.Drills ? GatherDrillMembers(s, tick) : 0;
			ReadOnlySpan<Vector3> claims = order.Drills ? DrillClaims(count) : ReadOnlySpan<Vector3>.Empty;
			DrillPhase phase = _drill.Decide(s, tick, order, _drillMembers.AsSpan(0, count), memory, IsAlivePlayer, Cover,
				claims);

			for (int i = 0; i < count; i++)
			{
				if (Find(_drillMembers[i].UnitId) is { } unit)
				{
					bool has = _drill.Board.TryGet(s, unit.UnitId, out DrillDirective d) && d.HasSpot;
					unit.HasDrillSpot = has;
					unit.DrillSpot = has ? d.Spot : Vector3.Zero;
				}
			}

			if (phase != _drillPhases[s])
			{
				_drillPhases[s] = phase;
				Vector3 at = _drill.Board.TryThreat(s, out Vector3 threat) ? threat : squad.Target;
				AgentEventBus.Emit(AgentEventKind.SquadDrill, tick, s, (int)phase, CountElements(count), at.X, at.Z);
			}
		}
	}

	/// <summary>
	/// What a board squad's order is to its drill: an attack, or a move onto a control point,
	/// by the rule the economy counts bodies with — not while the squad gathers or falls back.
	/// </summary>
	private DrillOrder DrillOrderOf(in Squad squad)
	{
		if (!squad.Active || squad.Phase is SquadPhase.Gathering or SquadPhase.FallingBack
			|| squad.Order is not (OrderKind.Attack or OrderKind.Move))
		{
			return default;
		}

		var order = new DrillOrder
		{
			FormedTick = squad.FormedTick,
			OrderedTick = squad.OrderedTick,
			Toward = squad.Target,
		};

		EconomyService economy = CombatManager.Instance?.Economy;
		bool named = squad.Order == OrderKind.Attack && squad.FocusOwnerId != OwnerId.None;
		for (int i = 0; !named && economy != null && i < economy.NodeCount && !order.Objective; i++)
		{
			ResourceNode node = economy.NodeAt(i);
			if (node != null && node.Covers(squad.Target))
			{
				order.Objective = true;
				order.Point = node.GlobalPosition;
				order.Radius = node.CaptureRadiusMeters;
				order.Toward = node.GlobalPosition;
			}
		}

		order.Drills = squad.Order == OrderKind.Attack || order.Objective;
		return order;
	}

	/// <summary>A board squad's members on the field, as its drill sees them. Returns how many.</summary>
	private int GatherDrillMembers(int squad, uint tick)
	{
		int members = Squads.MembersOf(squad, _drillUnits);
		int count = 0;
		for (int m = 0; m < members && count < _drillMembers.Length; m++)
		{
			Unit unit = Find(_drillUnits[m]);
			if (unit == null || !unit.IsOnField || unit.IsAircraft)
			{
				continue;
			}

			bool element = unit.Plan != null && unit.Autonomy == MicroAutonomy.On
				&& unit.Traits.Doctrine != UnitDoctrine.None && !(unit.Definition?.IsVehicle ?? false);
			bool fired = FiredUpon.IsRecent(unit.Fire, tick);
			_drillMembers[count++] = new DrillMember
			{
				UnitId = unit.UnitId,
				Position = unit.GlobalPosition,
				Doctrine = unit.Traits.Doctrine,
				Element = element,
				LastShotTick = unit.LastShotTick,
				FiredUpon = fired,
				FiredFrom = fired ? FiredFromFeet(unit) : Vector3.Zero,
				FiredTick = unit.Fire.LastTick,
				GrenadeReady = GrenadeSim.AiReady(unit.Grenades, tick),
				LastThrowTick = unit.Grenades.LastThrowTick,
				Body = UnitCoverQuery(unit, unit.GlobalPosition, mustFire: false, 0f),
			};
		}

		return count;
	}

	private int CountElements(int count)
	{
		int elements = 0;
		for (int i = 0; i < count; i++)
		{
			elements += _drillMembers[i].Element ? 1 : 0;
		}

		return elements;
	}

	/// <summary>
	/// The spots the rest of the side has taken, for one squad's searches: every other unit's
	/// cover spot, hide spot and drill spot, and its own members' cover and hide spots — their
	/// drill spots are the drill's to keep apart.
	/// </summary>
	private ReadOnlySpan<Vector3> DrillClaims(int members)
	{
		int count = 0;
		for (int i = 0; i < _ordered.Count && count < _drillClaims.Length - 2; i++)
		{
			Unit unit = _ordered[i];
			if (!unit.IsAlive || unit.Team != Team.Strategist)
			{
				continue;
			}

			if (unit.HasCoverSpot)
			{
				_drillClaims[count++] = unit.CoverSpot;
			}

			if (unit.HasPeek)
			{
				_drillClaims[count++] = unit.Peek.Hide;
			}

			if (unit.HasDrillSpot && !IsDrillMember(unit.UnitId, members))
			{
				_drillClaims[count++] = unit.DrillSpot;
			}
		}

		return _drillClaims.AsSpan(0, count);
	}

	private bool IsDrillMember(ushort unitId, int members)
	{
		for (int i = 0; i < members; i++)
		{
			if (_drillMembers[i].UnitId == unitId)
			{
				return true;
			}
		}

		return false;
	}

	/// <summary>
	/// What its squad's drill tells a unit, on its scan (§5.5): the phase, its task, its
	/// spot, where a rush goes, and whether its fire may go to the threat point — in its
	/// engage range from its standing eye, with a line over the cover board. Nothing for a
	/// unit in no drill, or whose micro-autonomy is off.
	/// </summary>
	private void SenseDrill(Unit unit, UnitContext plan, int squad)
	{
		DrillBoard board = _drill?.Board;
		if (board == null || unit.Autonomy != MicroAutonomy.On || !board.TryGet(squad, unit.UnitId, out DrillDirective d))
		{
			unit.HasDrillSpot = false;
			plan.SenseDrill(DrillPhase.None, DrillTask.None, false, Vector3.Zero, false, false, Vector3.Zero, Vector3.Zero);
			return;
		}

		bool hasThreat = board.TryThreat(squad, out Vector3 threat);
		Vector3 eye = unit.GlobalPosition + (Vector3.Up * unit.Body.EyeHeight(UnitPosture.Standing));
		bool fires = hasThreat && GunnerPlan.IsEntry(eye, threat, unit.Traits.EngageRangeMeters, Cover);
		plan.SenseDrill(board.PhaseOf(squad), d.Task, d.HasSpot, d.Spot, d.Covered, fires, GunnerPlan.AimPoint(threat),
			board.RushPointOf(squad));
	}

	/// <summary>
	/// A heavy gunner's places when it is its squad's base (§5.2, "in a drill"): the drill's,
	/// in the drill's order — its threat point first, and after the lift only where it shifts
	/// to — those it can reach with a line over the cover board. Returns how many; -1 when
	/// the drill has nothing for it, and its own gathering stands.
	/// </summary>
	private int DrillPlaces(Unit unit, UnitContext plan, int squad)
	{
		if (_drill == null || plan.Get(UnitFact.DrillTask) != (byte)DrillTask.FirePosition)
		{
			return -1;
		}

		ReadOnlySpan<Vector3> places = _drill.Board.SuppressOf(squad);
		bool lifted = _drill.Board.IsLifted(squad);
		Vector3 eye = unit.GlobalPosition + (Vector3.Up * unit.Body.EyeHeight(UnitPosture.Standing));
		int count = 0;
		for (int i = 0; i < places.Length && count < _drillPlaces.Length; i++)
		{
			if (GunnerPlan.IsEntry(eye, places[i], unit.Traits.EngageRangeMeters, Cover))
			{
				_drillPlaces[count++] = places[i];
			}
		}

		return count > 0 || lifted ? count : -1;
	}

	/// <summary>A new round: no squad drills.</summary>
	private void ResetDrills()
	{
		_drill?.Clear();
		Array.Clear(_drillPhases);
	}

	/// <summary>The drills, for the debug HUD: how many squads drill, and in which phase.</summary>
	public string DescribeDrills()
	{
		if (_drill == null)
		{
			return string.Empty;
		}

		var text = new System.Text.StringBuilder();
		for (int s = 0; s < _drill.Board.Capacity; s++)
		{
			if (_drill.Board.IsActive(s))
			{
				text.Append('s').Append(s).Append(' ').Append(_drill.Board.PhaseOf(s)).Append("  ");
			}
		}

		return text.Length == 0 ? "no squad drills" : text.ToString();
	}
}
