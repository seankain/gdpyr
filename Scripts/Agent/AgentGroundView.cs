using System;
using Gdpyr.Bots;
using Gdpyr.Core;
using Gdpyr.Fps;
using Gdpyr.Match;
using Gdpyr.Rts;
using Gdpyr.Sim;
using Gdpyr.Sim.Agent;
using Godot;

namespace Gdpyr.Agent;

/// <summary>
/// Builds the ground-force observation out of server state
/// (docs/AGENT_API.md §6.1).
///
/// The engine half of the encoder: it looks the world up and fills the view
/// structs <see cref="AgentObservation"/> turns into floats. Every number it
/// reads is one the seat is allowed to know — contacts come from
/// <see cref="GroundSensor"/>, which is the same scan a bot acquires through, so
/// the fog on a policy is the game's own and not a second implementation of it.
///
/// <c>--agent-omniscient</c> is the one exception, and it is stamped into every
/// observation frame that was produced under it.
/// </summary>
public sealed class AgentGroundView
{
	private readonly GroundContact[] _contacts = new GroundContact[GroundSensor.MaxContacts];
	private readonly AgentContactView[] _views = new AgentContactView[AgentObservation.MaxContacts];
	private readonly float[] _rays = new float[AgentObservation.Rays];

	/// <summary>
	/// Fills <paramref name="into"/> with one observation for
	/// <paramref name="peerId"/>. Returns false when the seat has no character to
	/// observe from, which is what a policy sees between a despawn and a respawn.
	/// </summary>
	public bool Encode(int peerId, uint tick, int stepMul, in AgentLimits limits, Span<float> into)
	{
		CombatManager combat = CombatManager.Instance;
		PlayerCombat player = combat?.Find(peerId);
		fps_controller character = player?.Character;
		if (combat == null || player == null || character == null || !GodotObject.IsInstanceValid(character))
		{
			return false;
		}

		AgentRoundView round = Round(combat, tick, stepMul);
		AgentSelfView self = Self(player, character);
		AgentWeaponView weapon = Weapon(player, character, tick);

		// Omniscient seats see the whole field — both filters dropped, not one;
		// everyone else sees what their own eyes picked up, at the sensor radius the
		// bots already use (docs/AGENT_API.md §6.1, §6.4).
		float radius = limits.Omniscient ? float.MaxValue : BotTraits.Default.SensorRadiusMeters;
		int found = GroundSensor.Scan(character, peerId, player.Team, radius, _contacts,
			ignoreLineOfSight: limits.Omniscient);
		int carried = Contacts(character, player.Team, found);

		Rays(character);

		AgentObjectiveView objective = Objective(combat, character, player.Team);

		return AgentObservation.Encode(round, self, weapon, _views.AsSpan(0, carried), _rays, objective, into)
			== AgentObservation.Floats;
	}

	// ---- blocks ------------------------------------------------------------

	private static AgentRoundView Round(CombatManager combat, uint tick, int stepMul)
	{
		MatchState match = combat.Match;
		float total = (combat.GameMode?.RoundDurationMinutes ?? 20) * 60f;

		return new AgentRoundView
		{
			Phase = (byte)match.Phase,
			SecondsRemaining = match.SecondsRemaining(tick),
			SecondsTotal = total,
			GroundTickets = match.GroundTickets,
			StartingGroundTickets = match.StartingGroundTickets,
			Tick = tick,
			StepMul = stepMul,
		};
	}

	private static AgentSelfView Self(PlayerCombat player, fps_controller character) => new()
	{
		Position = character.SimPosition,
		Velocity = character.Velocity,
		Yaw = character.Yaw,
		Pitch = character.Pitch,
		Grounded = character.IsOnFloor(),
		MovementState = character.StateId,
		Health = player.Health,
		Alive = player.IsAlive,
		MoveSpeedScale = character.MoveSpeedScale,
	};

	private static AgentWeaponView Weapon(PlayerCombat player, fps_controller character, uint tick)
	{
		ref WeaponState state = ref player.Equipped;
		WeaponStats stats = player.EquippedStats;

		float reload = 0f;
		if (state.IsReloading && stats.ReloadTicks > 0)
		{
			long remaining = (long)state.ReloadEndTick - tick;
			reload = Math.Clamp(1f - (remaining / (float)stats.ReloadTicks), 0f, 1f);
		}

		EmplacementManager guns = EmplacementManager.Instance;
		CarryKind carrying = guns?.CarryOf(player.PeerId) ?? CarryKind.None;

		return new AgentWeaponView
		{
			Slot = player.Slot,
			Ammo = state.Ammo,
			MagazineSize = stats.MagazineSize,
			UnlimitedAmmo = stats.HasUnlimitedAmmo,
			ReloadProgress = reload,
			CanFire = player.IsAlive && !state.IsReloading && tick >= state.NextFireTick
				&& (stats.HasUnlimitedAmmo || state.Ammo > 0),
			Ads = (character.PreviousButtons & (ushort)InputButtons.Ads) != 0,
			CarryingGun = carrying != CarryKind.None,
			MountedOnGun = guns?.TryMounted(player.PeerId, out _) ?? false,
		};
	}

	/// <summary>
	/// Turns the scan into contact records, relative to where the seat is looking.
	/// Already nearest-first out of <see cref="GroundSensor"/>, so this truncates
	/// rather than sorts.
	/// </summary>
	private int Contacts(fps_controller character, Team team, int found)
	{
		Vector3 eye = character.EyePosition;
		Vector3 own = character.Velocity;
		int carried = Math.Min(found, AgentObservation.MaxContacts);

		for (int i = 0; i < carried; i++)
		{
			ref GroundContact contact = ref _contacts[i];
			Vector3 to = contact.Position - eye;
			Aim.Angles(to, out float yaw, out float pitch);

			Vector3 line = to.LengthSquared() > 0.0001f ? to.Normalized() : Vector3.Forward;
			Vector3 relative = contact.Velocity - own;
			float closing = -relative.Dot(line);
			float lateral = (relative - (line * relative.Dot(line))).Length();

			_views[i] = new AgentContactView
			{
				IsPlayer = contact.IsPlayer,
				Hostile = contact.Team != team,
				Bearing = AgentLimits.ShortestDelta(character.Yaw, yaw),
				Elevation = pitch,
				DistanceMeters = contact.DistanceMeters,
				ClosingSpeed = closing,
				LateralSpeed = lateral,
				HealthFraction = contact.HealthFraction,

				// The scan is taken on the decision tick, so nothing in it is stale.
				// The field is carried anyway because a strategist's ghosts are, and a
				// policy trained on one observation shape should read both.
				TicksSinceSeen = 0,
			};
		}

		return carried;
	}

	/// <summary>
	/// A horizontal fan about the look direction: the cheap "can I walk that way"
	/// signal an FPS policy otherwise has to learn from collisions. 1 is clear,
	/// 0 is a wall in the face.
	/// </summary>
	private void Rays(fps_controller character)
	{
		Array.Fill(_rays, 1f);

		PhysicsDirectSpaceState3D space = character.GetWorld3D()?.DirectSpaceState;
		if (space == null)
		{
			return;
		}

		// Cast from chest height rather than from the eye: what the policy is asking
		// is whether the body fits, and the eye clears a waist-high crate the body
		// does not.
		Vector3 from = character.SimPosition + (Vector3.Up * 1f);
		float step = AgentObservation.Rays > 1
			? (AgentObservation.RayFanHalfAngleRadians * 2f) / (AgentObservation.Rays - 1)
			: 0f;

		for (int i = 0; i < AgentObservation.Rays; i++)
		{
			float yaw = character.Yaw + AgentObservation.RayFanHalfAngleRadians - (step * i);
			Vector3 to = from + (Aim.Direction(yaw, 0f) * AgentObservation.RayRangeMeters);

			PhysicsRayQueryParameters3D query =
				PhysicsRayQueryParameters3D.Create(from, to, CollisionLayers.World);
			Godot.Collections.Dictionary hit = space.IntersectRay(query);
			if (hit.Count == 0)
			{
				continue;
			}

			var at = (Vector3)hit["position"];
			_rays[i] = Mathf.Clamp(from.DistanceTo(at) / AgentObservation.RayRangeMeters, 0f, 1f);
		}
	}

	/// <summary>
	/// The nearest enemy barracks and how the map is going. The same anchor a bot
	/// walks towards, because it is the only fixed thing that matters: it is where
	/// the strategist's pressure comes from.
	/// </summary>
	private static AgentObjectiveView Objective(CombatManager combat, fps_controller character, Team team)
	{
		UnitManager units = UnitManager.Instance;
		var view = new AgentObjectiveView
		{
			GroundNodes = combat.Economy.GroundNodes,
			StrategistNodes = combat.Economy.StrategistNodes,
			ContestedNodes = combat.Economy.ContestedNodes,
		};

		float best = float.MaxValue;
		for (int i = 0; units != null && i < units.BarracksCount; i++)
		{
			Barracks barracks = units.BarracksAt(i);
			if (barracks == null || barracks.Team == team)
			{
				continue;
			}

			float distance = character.SimPosition.DistanceTo(barracks.GlobalPosition);
			if (distance >= best)
			{
				continue;
			}

			best = distance;
			Aim.Angles(barracks.GlobalPosition - character.SimPosition, out float yaw, out _);
			view.Known = true;
			view.Bearing = AgentLimits.ShortestDelta(character.Yaw, yaw);
			view.DistanceMeters = distance;
		}

		return view;
	}
}
