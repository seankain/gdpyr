using Gdpyr.Core;
using Gdpyr.Fps;
using Gdpyr.Match;
using Gdpyr.Rts;
using Gdpyr.Sim;
using Godot;

namespace Gdpyr.Bots;

/// <summary>
/// An HTN ground bot and the FPV drone (docs/CONTROL_POINTS.md §4.5): it
/// launches one off the side's shelf at a tank it can see (<see cref="DroneAutopilot"/>),
/// flies it at the hull with its look angles, and sets it off if the tank is gone before
/// it gets there. Ahead of the bot's own plan, which picks up where it was when the drone
/// is done: a pilot's body stands still meanwhile, whatever the plan would have it do.
/// </summary>
public sealed partial class BotPilot
{
	/// <summary>How often a bot with a drone ready looks for a tank to put it up at.</summary>
	private const int DroneCheckTicks = SimConfig.TickRate / 2;

	/// <summary>The tank this bot's drone is after; 0 while it flies none.</summary>
	private ushort _droneTarget;

	private uint _nextDroneCheck;
	private bool _droneLaunching;

	/// <summary>
	/// The frame for this tick when the drone has it: steering one, or putting one up.
	/// False leaves the tick to the bot's plan.
	/// </summary>
	private bool TrySampleDrone(fps_controller character, PlayerCombat combat, uint tick, out InputFrame frame)
	{
		frame = default;
		CombatManager manager = CombatManager.Instance;
		UnitManager units = UnitManager.Instance;
		if (manager == null || units == null || combat.Team != Team.GroundForce)
		{
			return false;
		}

		int slot = manager.PilotedSlot(_peerId);
		if (slot >= 0)
		{
			_droneLaunching = false;
			frame = Steer(manager.DroneAt(slot), units.Find(_droneTarget), character, tick);
			return true;
		}

		_droneTarget = _droneLaunching ? _droneTarget : (ushort)0;

		// The key went down last tick and the server has not put a drone up: let it come
		// back up so the next press is an edge, and stop trying until the next check.
		if (_droneLaunching)
		{
			_droneLaunching = false;
			frame = InputFrame.Create(tick, 0f, 0f, character.Yaw, character.Pitch, InputButtons.None);
			return true;
		}

		if (tick < _nextDroneCheck || !manager.Drones.CanLaunch || manager.IsPiloting(_peerId))
		{
			return false;
		}

		_nextDroneCheck = tick + DroneCheckTicks;

		Vector3 eye = character.EyePosition;
		Unit tank = NearestTankInSight(units, eye, out float tankDistance, out float infantryDistance);
		if (tank == null || !DroneAutopilot.ShouldLaunch(true, tankDistance, infantryDistance))
		{
			return false;
		}

		DroneAutopilot.Steer(eye, Hull(tank), out float yaw, out float pitch);
		_droneTarget = tank.UnitId;
		_droneLaunching = true;
		frame = InputFrame.Create(tick, 0f, 0f, yaw, pitch, InputButtons.Drone);
		return true;
	}

	/// <summary>At the target's hull while it lives; the trigger once it does not, so the drone goes off where it is.</summary>
	private InputFrame Steer(in Drone drone, Unit target, fps_controller character, uint tick)
	{
		if (target == null || !target.IsOnField)
		{
			return InputFrame.Create(tick, 0f, 0f, character.Yaw, character.Pitch, InputButtons.Fire);
		}

		DroneAutopilot.Steer(drone.Position, Hull(target), out float yaw, out float pitch);
		return InputFrame.Create(tick, 0f, 0f, yaw, pitch, InputButtons.None);
	}

	private static Vector3 Hull(Unit unit) => unit.GlobalPosition + (Vector3.Up * DroneAutopilot.HullAimHeightMeters);

	/// <summary>
	/// The nearest strategist tank on the field with nothing of the world between the bot's
	/// eye and its hull, and how far the nearest of the strategist's units on foot is.
	/// </summary>
	private Unit NearestTankInSight(UnitManager units, Vector3 eye, out float tankDistance, out float infantryDistance)
	{
		Unit best = null;
		tankDistance = float.MaxValue;
		infantryDistance = float.MaxValue;
		PhysicsDirectSpaceState3D space = _character.GetWorld3D()?.DirectSpaceState;

		for (int i = 0; i < units.SlotCount; i++)
		{
			Unit unit = units.UnitAt(i);
			if (unit == null || !unit.IsOnField || unit.Team != Team.Strategist)
			{
				continue;
			}

			float distance = eye.DistanceTo(unit.GlobalPosition);
			if (unit.Definition?.IsVehicle != true)
			{
				infantryDistance = Mathf.Min(infantryDistance, distance);
				continue;
			}

			if (unit.DefinitionId != UnitCatalog.Tank || distance >= tankDistance
				|| distance > DroneAutopilot.LaunchRangeMeters)
			{
				continue;
			}

			if (space != null)
			{
				var ray = PhysicsRayQueryParameters3D.Create(eye, Hull(unit), CollisionLayers.World);
				if (space.IntersectRay(ray).Count > 0)
				{
					continue;
				}
			}

			best = unit;
			tankDistance = distance;
		}

		return best;
	}
}
