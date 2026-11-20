using Gdpyr.Core;
using Gdpyr.Fps;
using Gdpyr.Match;
using Gdpyr.Sim;
using Godot;

namespace Gdpyr.Rts;

/// <summary>
/// The scout drone's strike (docs/OUTPOST.md §3.3): a drone with
/// <see cref="OrderKind.Strike"/> flies straight at its aim at <see cref="StrikeRules.SpeedMetersPerSecond"/>
/// and goes off — on reaching it, on an enemy coming within
/// <see cref="StrikeRules.ProximityMeters"/>, on flying into the world, or when
/// <see cref="StrikeRules.MaxTicks"/> run out. Its aim is the player the order named, while
/// the drone itself can see them, and otherwise the point the order carries — which,
/// for a named player, is wherever the drone last saw them.
///
/// The strike is the only way a drone goes off. It is an order like any other: a later
/// order — a stop, a move — calls it off, and the drone climbs back to its cruise.
/// </summary>
public partial class UnitManager
{
	/// <summary>Strikes flown to the end this round. Server-side, for the debug HUD.</summary>
	public int StrikesFlown { get; private set; }

	/// <summary>One tick of a drone flying its strike, in place of everything else a unit does.</summary>
	private void ServerStrike(Unit unit, uint tick)
	{
		CombatManager combat = CombatManager.Instance;
		if (combat == null || unit.Definition is not { CanStrike: true })
		{
			unit.Order = UnitOrder.Hold(unit.GlobalPosition);
			return;
		}

		// Off the ground and out of the way, as anything flying is.
		if (unit.Phase == FlightPhase.Grounded)
		{
			unit.Flight.Phase = FlightPhase.Cruising;
			unit.ApplyPresentation();
		}

		Vector3 middle = unit.Hitbox.Center;
		Vector3 aim = StrikeSim.GroundAim(unit.Order.Target);

		// Guided onto a named player while its own camera has them: the order's point
		// follows them, so a target that ducks out of sight is dived on where they went.
		if (OwnerId.IsPeer(unit.Order.TargetOwnerId)
			&& combat.Find(OwnerId.PeerOf(unit.Order.TargetOwnerId)) is { IsAlive: true, Character: { } character } named
			&& named.Team != unit.Team)
		{
			Vector3 chest = character.Hitbox.Center;
			if (unit.EyePosition.DistanceTo(chest) <= unit.Traits.SensorRadiusMeters
				&& HasLineOfSight(unit.EyePosition, chest))
			{
				aim = chest;
				unit.Order.Target = character.SimPosition;
			}
		}

		StrikeEnd end = StrikeEnd.None;
		for (int i = 0; i < combat.PlayerCount && end == StrikeEnd.None; i++)
		{
			PlayerCombat player = combat.PlayerAt(i);
			if (player is { IsAlive: true, Character: { } body } && player.Team != unit.Team
				&& StrikeSim.Proximate(middle, body.Hitbox.Center))
			{
				end = StrikeEnd.Proximity;
			}
		}

		if (end == StrikeEnd.None)
		{
			end = StrikeSim.Step(middle, aim, unit.OrderTick, tick, SimConfig.TickDelta, out Vector3 velocity);
			if (end == StrikeEnd.None && unit.FlyStrike(velocity))
			{
				end = StrikeEnd.Impact;
			}
		}

		unit.State = UnitStateId.Moving;
		unit.FaceTowards(aim, SimConfig.TickDelta);

		if (end != StrikeEnd.None)
		{
			StrikesFlown++;
			combat.ServerDetonateUnit(unit, end, tick);
		}
	}
}
