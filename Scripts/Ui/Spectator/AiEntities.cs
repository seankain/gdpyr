using Gdpyr.Fps;
using Gdpyr.Match;
using Gdpyr.Net;
using Gdpyr.Rts;
using Gdpyr.Sim;
using Gdpyr.Sim.AiDebug;
using Godot;

namespace Gdpyr.Ui.Spectator;

/// <summary>
/// Where the things a debug frame names are in this process's world, and what to
/// call them. A spectator's characters and units are the interpolated ones every
/// client draws; a squad has no body, and is where its last frame put its middle.
/// </summary>
public static class AiEntities
{
	public static bool TryPosition(AiEntityRef target, AiDebugFrame frame, out Vector3 position)
	{
		position = default;
		switch (target.Kind)
		{
			case AiEntityKind.Player:
				if (PlayerManager.Instance?.CharacterOf(target.Id) is { } character
					&& GodotObject.IsInstanceValid(character))
				{
					position = character.GlobalPosition;
					return true;
				}

				return false;

			case AiEntityKind.Unit:
				if (UnitManager.Instance?.Find((ushort)target.Id) is { } unit && GodotObject.IsInstanceValid(unit))
				{
					position = unit.GlobalPosition;
					return true;
				}

				return false;

			case AiEntityKind.Squad:
				if (frame?.Commander(target.Id)?.Squad(target.Slot) is { } squad)
				{
					position = squad.Centroid;
					return true;
				}

				return false;

			default:
				return false;
		}
	}

	/// <summary>Where whatever an owner id names is: a player, a unit, a structure.</summary>
	public static bool TryOwnerPosition(int ownerId, out Vector3 position)
	{
		position = default;
		if (ownerId == OwnerId.None)
		{
			return false;
		}

		if (OwnerId.IsUnit(ownerId))
		{
			return TryPosition(AiEntityRef.Unit(OwnerId.UnitOf(ownerId)), null, out position);
		}

		if (OwnerId.IsStructure(ownerId))
		{
			if (UnitManager.Instance?.StructureOf(ownerId) is { } structure && GodotObject.IsInstanceValid(structure))
			{
				position = structure.GlobalPosition;
				return true;
			}

			return false;
		}

		return TryPosition(AiEntityRef.Player(OwnerId.PeerOf(ownerId)), null, out position);
	}

	/// <summary><c>bot 3</c>, <c>rifleman 42</c>, <c>squad 2 of bot 7</c>.</summary>
	public static string NameOf(AiEntityRef target)
	{
		if (target.Kind == AiEntityKind.Unit && UnitManager.Instance?.Find((ushort)target.Id) is { } unit
			&& unit.Definition != null)
		{
			return $"{unit.Definition.Name} {target.Id}";
		}

		return target.ToString();
	}

	/// <summary>What an owner id names, in words, for "target" and "focus" lines.</summary>
	public static string OwnerName(int ownerId)
	{
		if (ownerId == OwnerId.None)
		{
			return "nothing";
		}

		if (OwnerId.IsUnit(ownerId))
		{
			return NameOf(AiEntityRef.Unit(OwnerId.UnitOf(ownerId)));
		}

		if (OwnerId.IsStructure(ownerId))
		{
			return UnitManager.Instance?.StructureOf(ownerId) is { } structure ? $"{structure.Name}" : "a structure";
		}

		return BotRoster.NameOf(OwnerId.PeerOf(ownerId));
	}

	/// <summary>Whether a player is on the ground force and alive: one a spectator can click.</summary>
	public static bool IsPickable(PlayerCombat player) =>
		player is { Team: Team.GroundForce, AwaitingRole: false, Character: not null };
}
