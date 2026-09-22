using System;
using Gdpyr.Core;
using Gdpyr.Fps;
using Gdpyr.Match;
using Gdpyr.Rts;
using Gdpyr.Sim;
using Godot;

namespace Gdpyr.Bots;

/// <summary>One thing a ground-force seat's own eyes have picked up.</summary>
public struct GroundContact
{
	public bool IsPlayer;

	/// <summary>Set when <see cref="IsPlayer"/>.</summary>
	public int PeerId;

	/// <summary>Set when this is a unit.</summary>
	public ushort UnitId;

	/// <summary>
	/// Set when this is a structure with a gun: its <c>OwnerId.ForStructure</c>, and 0
	/// for anything else (docs/NETCODE.md §10.5).
	/// </summary>
	public int StructureOwnerId;

	public bool IsStructure => StructureOwnerId != 0;

	public Team Team;

	/// <summary>Where the thing is aimed at — a hitbox centre, not a pair of feet.</summary>
	public Vector3 Position;

	public Vector3 Velocity;

	public float HealthFraction;

	public float DistanceMeters;
}

/// <summary>
/// What a ground-force seat can see: its own eyes, at its own sensor radius, with
/// line of sight (docs/NETCODE.md §9).
///
/// One implementation, shared by <see cref="BotPilot"/> and by the agent API's
/// observation encoder, because a policy that sat behind a second, subtly
/// different filter would not be playing the game the bots play — and a change to
/// what a ground bot may know would then have to be made twice
/// (docs/AGENT_API.md §6.1).
///
/// Server-only, like every other decision in this codebase.
/// </summary>
public static class GroundSensor
{
	/// <summary>Contacts one scan will carry. Bounds the sort as much as the memory.</summary>
	public const int MaxContacts = 16;

	/// <summary>
	/// Fills <paramref name="into"/> with everything <paramref name="character"/>
	/// can see, nearest first, and returns how many.
	///
	/// Players *and* units, friendly and hostile: what the bot may *shoot* is a
	/// narrower question than what it can *see*, and the two are separated here so
	/// the observation is not lying about a teammate standing in the doorway.
	/// </summary>
	/// <param name="ignoreLineOfSight">
	/// Set by <c>--agent-omniscient</c>, which drops both filters rather than one
	/// (docs/AGENT_API.md §6.4). It exists because "is the policy losing because it
	/// cannot see, or because it is bad" is otherwise unanswerable, and every
	/// observation produced under it is labelled as cheating.
	/// </param>
	public static int Scan(fps_controller character, int peerId, Team team, float sensorRadiusMeters,
		Span<GroundContact> into, bool ignoreLineOfSight = false)
	{
		if (character == null || !GodotObject.IsInstanceValid(character) || into.Length == 0)
		{
			return 0;
		}

		Vector3 eye = character.EyePosition;
		PhysicsDirectSpaceState3D space = ignoreLineOfSight ? null : character.GetWorld3D()?.DirectSpaceState;
		int count = 0;

		CombatManager combat = CombatManager.Instance;
		for (int i = 0; combat != null && i < combat.PlayerCount; i++)
		{
			PlayerCombat other = combat.PlayerAt(i);
			if (other == null || other.PeerId == peerId || !other.IsAlive || other.Character == null
				|| !GodotObject.IsInstanceValid(other.Character))
			{
				continue;
			}

			Vector3 at = other.Character.Hitbox.Center;
			float distance = eye.DistanceTo(at);
			if (distance > sensorRadiusMeters || !HasLineOfSight(space, eye, at))
			{
				continue;
			}

			Insert(into, ref count, new GroundContact
			{
				IsPlayer = true,
				PeerId = other.PeerId,
				Team = other.Team,
				Position = at,
				Velocity = other.Character.Velocity,
				HealthFraction = Mathf.Clamp(other.Health / (float)SimConfig.MaxHealth, 0f, 1f),
				DistanceMeters = distance,
			});
		}

		UnitManager units = UnitManager.Instance;
		for (int i = 0; units != null && i < units.SlotCount; i++)
		{
			Unit unit = units.UnitAt(i);
			if (unit == null || !unit.IsAlive)
			{
				continue;
			}

			Vector3 at = unit.Hitbox.Center;
			float distance = eye.DistanceTo(at);
			if (distance > sensorRadiusMeters || !HasLineOfSight(space, eye, at))
			{
				continue;
			}

			Insert(into, ref count, new GroundContact
			{
				IsPlayer = false,
				UnitId = unit.UnitId,
				Team = unit.Team,
				Position = at,
				Velocity = unit.Velocity,
				HealthFraction = unit.HealthPercent / 255f,
				DistanceMeters = distance,
			});
		}

		// Structures with a gun are things that shoot back, so they are contacts;
		// a sandbag wall is scenery, which the ray fan already reports.
		for (int i = 0; units != null && i < SimConfig.MaxStructures; i++)
		{
			Structure structure = units.StructureAt(i);
			if (structure == null || structure.IsDestroyed || !structure.IsArmed)
			{
				continue;
			}

			Vector3 at = structure.AimPoint;
			float distance = eye.DistanceTo(at);
			if (distance > sensorRadiusMeters || !CanSee(space, eye, structure))
			{
				continue;
			}

			Insert(into, ref count, new GroundContact
			{
				IsPlayer = false,
				StructureOwnerId = structure.ShooterId,
				Team = structure.Team,
				Position = at,
				Velocity = Vector3.Zero,
				HealthFraction = Mathf.Clamp(structure.Health / Mathf.Max(structure.MaxHealth, 1f), 0f, 1f),
				DistanceMeters = distance,
			});
		}

		return count;
	}

	/// <summary>
	/// Whether a structure can be seen from <paramref name="eye"/>: a clear line to
	/// the middle of it, or a line whose first obstacle is the structure itself —
	/// which, once it is finished, it always is, because it is a world collider too.
	/// </summary>
	public static bool CanSee(PhysicsDirectSpaceState3D space, Vector3 eye, Structure structure)
	{
		if (space == null)
		{
			return true;
		}

		PhysicsRayQueryParameters3D query = PhysicsRayQueryParameters3D.Create(eye, structure.AimPoint,
			CollisionLayers.World);
		Godot.Collections.Dictionary hit = space.IntersectRay(query);
		return hit.Count == 0 || UnitManager.DistanceToBlast(structure, hit["position"].AsVector3()) < 0.1f;
	}

	/// <summary>
	/// What a ground bot shoots at, as an owner id: the nearest hostile unit in the
	/// scan, and only when there is none, the nearest hostile structure with a gun.
	/// Units first because they move and a pillbox does not — and because a rifle
	/// does a quarter of its damage to concrete, a bot that preferred the pillbox
	/// would stand in front of it losing the argument.
	/// </summary>
	public static int NearestHostileTarget(ReadOnlySpan<GroundContact> contacts, Team team)
	{
		ushort unit = NearestHostileUnit(contacts, team);
		if (unit != 0)
		{
			return OwnerId.ForUnit(unit);
		}

		for (int i = 0; i < contacts.Length; i++)
		{
			if (contacts[i].IsStructure && contacts[i].Team != team)
			{
				return contacts[i].StructureOwnerId;
			}
		}

		return OwnerId.None;
	}

	/// <summary>
	/// The nearest hostile *unit* in a scan, which is what a ground bot shoots at.
	///
	/// Enemy players are never candidates. There are only two sides, a strategist
	/// has no body on the field, and friendly fire between players is on — a bot
	/// that could acquire a player would eventually acquire a teammate
	/// (see <see cref="BotPilot"/>).
	/// </summary>
	public static ushort NearestHostileUnit(ReadOnlySpan<GroundContact> contacts, Team team)
	{
		for (int i = 0; i < contacts.Length; i++)
		{
			if (!contacts[i].IsPlayer && !contacts[i].IsStructure && contacts[i].Team != team)
			{
				return contacts[i].UnitId;
			}
		}

		return 0;
	}

	/// <summary>Whether the level lets these two points see each other.</summary>
	public static bool HasLineOfSight(Node3D viewer, Vector3 from, Vector3 to) =>
		HasLineOfSight(viewer?.GetWorld3D()?.DirectSpaceState, from, to);

	/// <summary>
	/// The same test against a space state the caller already has. A scan asks this
	/// once per candidate, so looking the space up once is worth the overload.
	/// </summary>
	public static bool HasLineOfSight(PhysicsDirectSpaceState3D space, Vector3 from, Vector3 to)
	{
		if (space == null)
		{
			// No world to be blocked by: a map with no geometry is one everybody can
			// see across, which is what the bots already assume.
			return true;
		}

		PhysicsRayQueryParameters3D query = PhysicsRayQueryParameters3D.Create(from, to, CollisionLayers.World);
		return space.IntersectRay(query).Count == 0;
	}

	/// <summary>
	/// Keeps the buffer sorted by distance as it fills, and drops anything further
	/// than the furthest thing already held once it is full. An insertion sort over
	/// sixteen entries beats sorting a list afterwards and allocates nothing.
	/// </summary>
	private static void Insert(Span<GroundContact> into, ref int count, in GroundContact contact)
	{
		if (count == into.Length && contact.DistanceMeters >= into[count - 1].DistanceMeters)
		{
			return;
		}

		int at = Math.Min(count, into.Length - 1);
		while (at > 0 && into[at - 1].DistanceMeters > contact.DistanceMeters)
		{
			into[at] = into[at - 1];
			at--;
		}

		into[at] = contact;
		if (count < into.Length)
		{
			count++;
		}
	}
}
