using Gdpyr.Core;
using Gdpyr.Fps;
using Gdpyr.Rts;
using Gdpyr.Sim;
using Godot;

namespace Gdpyr.Match;

/// <summary>
/// The map's kill plane (docs/MAPS.md §3.2, <see cref="KillPlane"/>): once a tick on the
/// server, every player whose feet and every unit on the field whose origin is below it
/// dies, through the same paths a round's death takes — a ticket for a player, the respawn
/// timer, <c>damage</c> and <c>kill</c> in the world's name, and <c>unit_lost</c> for a unit.
///
/// A player in a crew vehicle's seat is left to the vehicle: the plane is for a body in the
/// water, and one who gets out there is under it on the next tick.
/// </summary>
public partial class CombatManager
{
	/// <summary>The scene the plane was read from: a new map is read again.</summary>
	private Node _killPlaneScene;

	/// <summary>The plane's height, or <see cref="KillPlane.None"/> for a map without one.</summary>
	private float _killPlaneY = KillPlane.None;

	private void ServerKillPlane(uint tick)
	{
		float plane = KillPlaneOfMap();
		if (!float.IsFinite(plane))
		{
			return;
		}

		CrewVehicleManager crew = CrewVehicleManager.Instance;
		for (int i = 0; i < _ordered.Count; i++)
		{
			PlayerCombat combat = _ordered[i];
			if (combat?.Character == null || !combat.IsAlive
				|| (crew != null && crew.TrySeat(combat.PeerId, out _, out _)))
			{
				continue;
			}

			Vector3 feet = combat.Character.SimPosition;
			if (!KillPlane.IsBelow(feet.Y, plane))
			{
				continue;
			}

			GD.Print($"[combat] peer {combat.PeerId} went below the kill plane at {plane:0.00} m, at {feet}");
			Damage(combat, KillPlane.LethalDamage(combat.Health, combat.Vest.Points), explosive: false,
				attacker: null, OwnerId.None, WeaponCatalog.None, tick);
		}

		UnitManager units = UnitManager.Instance;
		for (int i = 0; units != null && i < units.SlotCount; i++)
		{
			Unit unit = units.UnitAt(i);
			if (unit == null || !unit.IsOnField || !KillPlane.IsBelow(unit.GlobalPosition.Y, plane))
			{
				continue;
			}

			GD.Print($"[combat] {UnitCatalog.NameOf(unit.DefinitionId)} {unit.UnitId} went below the kill plane"
				+ $" at {plane:0.00} m, at {unit.GlobalPosition}");
			DamageUnit(unit, unit.Health, explosive: true, vehicleScale: 1f, attacker: null, OwnerId.None,
				WeaponCatalog.None, tick);
		}
	}

	/// <summary>
	/// The current map's plane: the first node in <see cref="KillPlane.Group"/>, read once per
	/// map, as its other pieces are (docs/MAPS.md §3).
	/// </summary>
	private float KillPlaneOfMap()
	{
		Node scene = GetTree()?.CurrentScene;
		if (scene == _killPlaneScene)
		{
			return _killPlaneY;
		}

		_killPlaneScene = scene;
		_killPlaneY = KillPlane.None;
		foreach (Node node in GetTree().GetNodesInGroup(KillPlane.Group))
		{
			if (node is Node3D marker)
			{
				_killPlaneY = marker.GlobalPosition.Y;
				GD.Print($"[combat] kill plane at {_killPlaneY:0.00} m ({marker.Name})");
				break;
			}
		}

		return _killPlaneY;
	}
}
