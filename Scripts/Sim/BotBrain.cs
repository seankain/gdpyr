using System;
using Godot;

namespace Gdpyr.Sim;

/// <summary>
/// How good a computer player is at its job, and how it behaves while doing it
/// (docs/IMPLEMENTATION_PLAN.md §M3.5).
///
/// Every number here is a playtest knob rather than a constant of the simulation:
/// what a bot is *for* is giving one person something to shoot at, and "are the
/// bots too good" is the first thing anyone will say out loud.
/// </summary>
public readonly struct BotTraits
{
	/// <summary>How fast it can swing its aim [rad/s]. The main difficulty dial.</summary>
	public readonly float TurnRateRadians;

	/// <summary>
	/// Half-angle of the error cone its aim sits inside [rad]. Applied to the *look*
	/// direction rather than to the shot, so a bot misses the way a player does:
	/// by being pointed slightly wrong for a while, not by a round leaving in a
	/// direction it was never aimed in.
	/// </summary>
	public readonly float AimConeRadians;

	/// <summary>Ticks between re-rolls of that error. Longer means smoother, and easier to dodge.</summary>
	public readonly int AimJitterTicks;

	/// <summary>Ticks between acquiring a target and being willing to shoot at it.</summary>
	public readonly int ReactionTicks;

	/// <summary>It will not open fire past this [m], whatever its weapon could reach.</summary>
	public readonly float EngageRangeMeters;

	/// <summary>Closes to about here, then holds and strafes rather than walking into the muzzle [m].</summary>
	public readonly float PreferredRangeMeters;

	/// <summary>How far it notices an enemy [m]. The bot's half of the fog of war, pending M4.</summary>
	public readonly float SensorRadiusMeters;

	/// <summary>How close its aim must be to the target before it pulls the trigger [rad].</summary>
	public readonly float FireAngleRadians;

	/// <summary>Ticks a strafe runs before it can reverse.</summary>
	public readonly int StrafePeriodTicks;

	public BotTraits(float turnRateRadians, float aimConeRadians, int aimJitterTicks, int reactionTicks,
		float engageRangeMeters, float preferredRangeMeters, float sensorRadiusMeters, float fireAngleRadians,
		int strafePeriodTicks)
	{
		TurnRateRadians = MathF.Max(turnRateRadians, 0.01f);
		AimConeRadians = MathF.Max(aimConeRadians, 0f);
		AimJitterTicks = Math.Max(aimJitterTicks, 1);
		ReactionTicks = Math.Max(reactionTicks, 0);
		EngageRangeMeters = MathF.Max(engageRangeMeters, 0f);
		PreferredRangeMeters = MathF.Max(preferredRangeMeters, 0f);
		SensorRadiusMeters = MathF.Max(sensorRadiusMeters, 0f);
		FireAngleRadians = MathF.Max(fireAngleRadians, 0f);
		StrafePeriodTicks = Math.Max(strafePeriodTicks, 1);
	}

	/// <summary>
	/// The one profile M3.5 ships. Deliberately mediocre: it turns at about half the
	/// rate a mouse does, takes a fifth of a second to react, and holds its aim
	/// three degrees off centre — which at the infantry's 40 m engagement range is
	/// two metres of miss, so a bot is a threat in a group and an annoyance alone.
	/// </summary>
	public static BotTraits Default => new(
		turnRateRadians: 4.5f,
		aimConeRadians: Spread.ConeFromDegrees(3f),
		aimJitterTicks: 20,
		reactionTicks: 12,
		engageRangeMeters: 70f,
		preferredRangeMeters: 25f,
		sensorRadiusMeters: 80f,
		fireAngleRadians: Spread.ConeFromDegrees(6f),
		strafePeriodTicks: 45);
}

/// <summary>
/// Everything a bot is allowed to know about one tick. Assembled by the pilot,
/// which is the half that is allowed to touch the engine.
/// </summary>
public readonly struct BotSituation
{
	public readonly bool Alive;

	/// <summary>Where it is looking now, as the last simulated frame left it.</summary>
	public readonly float Yaw;
	public readonly float Pitch;

	/// <summary>Horizontal speed [m/s]. Only the sprint decision reads it (see <see cref="BotBrain.Frame"/>).</summary>
	public readonly float Speed;

	public readonly bool HasTarget;

	/// <summary>Unit vector from the eye to where the target will be when the round arrives.</summary>
	public readonly Vector3 AimDirection;

	public readonly float TargetDistance;

	/// <summary>False when the shot is blocked — by the world, or by somebody on its own side.</summary>
	public readonly bool TargetVisible;

	/// <summary>Ticks this target has been held for, against <see cref="BotTraits.ReactionTicks"/>.</summary>
	public readonly int TicksOnTarget;

	public readonly bool HasDestination;

	/// <summary>Unit vector, world space, towards wherever it is walking.</summary>
	public readonly Vector3 MoveDirection;

	public readonly float DestinationDistance;

	/// <summary>Set by the pilot when the character has stopped making progress.</summary>
	public readonly bool Stuck;

	/// <summary>True when the magazine is worth topping up between fights.</summary>
	public readonly bool NeedsReload;

	public BotSituation(bool alive, float yaw, float pitch, float speed, bool hasTarget, Vector3 aimDirection,
		float targetDistance, bool targetVisible, int ticksOnTarget, bool hasDestination, Vector3 moveDirection,
		float destinationDistance, bool stuck, bool needsReload)
	{
		Alive = alive;
		Yaw = yaw;
		Pitch = pitch;
		Speed = speed;
		HasTarget = hasTarget;
		AimDirection = aimDirection;
		TargetDistance = targetDistance;
		TargetVisible = targetVisible;
		TicksOnTarget = ticksOnTarget;
		HasDestination = hasDestination;
		MoveDirection = moveDirection;
		DestinationDistance = destinationDistance;
		Stuck = stuck;
		NeedsReload = needsReload;
	}
}

/// <summary>
/// A ground-force bot's tick, as a pure function of its situation
/// (docs/IMPLEMENTATION_PLAN.md §M3.5).
///
/// It produces an <see cref="InputFrame"/> and nothing else. That is the whole
/// trick: a bot is a player whose frames come from here instead of from a socket,
/// so it moves through the same movement FSM, fires through the same
/// <see cref="WeaponSim"/>, dies to the same projectiles and costs the same ticket
/// — and no code downstream of <c>PlayerManager</c> has to know it exists.
///
/// Engine-free and stateless for the same reason <see cref="UnitBrain"/> is:
/// "why did the bot walk into a wall and not shoot" should be answerable from a
/// test. What state a bot has — what it is shooting at, how long it has been
/// stuck — lives in the pilot, which passes it in.
/// </summary>
public static class BotBrain
{
	/// <summary>
	/// Distinguishes this use of <see cref="Spread.Seed"/> from a weapon's, which is
	/// keyed on a real definition id. Past the catalog's ceiling, so the two spaces
	/// cannot collide.
	/// </summary>
	private const byte AimSeedSalt = SimConfig.MaxWeaponDefinitions + 1;

	private const byte StrafeSeedSalt = SimConfig.MaxWeaponDefinitions + 2;

	/// <summary>Below this speed the character is standing still, and sprint would be an edge it never sees.</summary>
	private const float SprintMinSpeed = 1.5f;

	/// <summary>Metres past which a bot with nothing to shoot at runs rather than walks.</summary>
	private const float SprintDistanceMeters = 12f;

	/// <summary>
	/// One tick of intent. <paramref name="peerId"/> seeds the aim error and the
	/// strafe, so two bots standing in the same place do not move or miss
	/// identically.
	/// </summary>
	public static InputFrame Frame(uint tick, int peerId, in BotSituation situation, in BotTraits traits)
	{
		if (!situation.Alive)
		{
			// The same frame a dead player's client is recorded with: it keeps its
			// angles and loses everything else (see InputFrame.LookOnly).
			return InputFrame.Neutral(tick, situation.Yaw, situation.Pitch);
		}

		Vector3 look = LookDirection(tick, peerId, situation, traits);
		Aim.Angles(look, out float wantedYaw, out float wantedPitch);

		float step = traits.TurnRateRadians * SimConfig.TickDelta;
		float yaw = Slew(situation.Yaw, wantedYaw, step);
		float pitch = Mathf.Clamp(Slew(situation.Pitch, wantedPitch, step), -Quantize.HalfPi, Quantize.HalfPi);

		Vector2 axes = MoveAxes(WalkDirection(tick, peerId, situation, traits), yaw);

		InputButtons buttons = Buttons(tick, situation, traits, yaw, pitch);

		return InputFrame.Create(tick, axes.X, axes.Y, yaw, pitch, buttons);
	}

	/// <summary>
	/// Where it wants to be looking: at the target, with its error cone applied, or
	/// else along the way it is walking.
	///
	/// The error is re-rolled every <see cref="BotTraits.AimJitterTicks"/> rather
	/// than every tick. Per-tick noise averages out over a burst and makes a bot
	/// *more* accurate the longer it fires; a held offset is what a player who has
	/// mis-tracked a target actually does.
	/// </summary>
	private static Vector3 LookDirection(uint tick, int peerId, in BotSituation situation, in BotTraits traits)
	{
		if (situation.HasTarget && situation.AimDirection != Vector3.Zero)
		{
			uint bucket = tick / (uint)traits.AimJitterTicks;
			return Spread.Apply(situation.AimDirection, traits.AimConeRadians,
				Spread.Seed(peerId, AimSeedSalt, bucket));
		}

		if (situation.HasDestination && situation.MoveDirection != Vector3.Zero)
		{
			Vector3 flat = situation.MoveDirection;
			flat.Y = 0f;
			if (flat.LengthSquared() > 0.0001f)
			{
				return flat.Normalized();
			}
		}

		return Aim.Direction(situation.Yaw, 0f);
	}

	/// <summary>
	/// The world direction it walks in. A bot that has closed to its preferred range
	/// strafes across its target instead of continuing into it — standing still in
	/// the open while exchanging fire is the single thing that makes a bot read as a
	/// target rather than an opponent.
	/// </summary>
	private static Vector3 WalkDirection(uint tick, int peerId, in BotSituation situation, in BotTraits traits)
	{
		if (situation.HasTarget && situation.TargetVisible
			&& situation.TargetDistance <= traits.PreferredRangeMeters)
		{
			Vector3 forward = situation.AimDirection;
			forward.Y = 0f;
			if (forward.LengthSquared() < 0.0001f)
			{
				return Vector3.Zero;
			}

			forward = forward.Normalized();
			Vector3 right = new Vector3(-forward.Z, 0f, forward.X);
			return right * StrafeSign(tick, peerId, traits.StrafePeriodTicks);
		}

		if (!situation.HasDestination)
		{
			return Vector3.Zero;
		}

		Vector3 move = situation.MoveDirection;
		move.Y = 0f;
		return move.LengthSquared() < 0.0001f ? Vector3.Zero : move.Normalized();
	}

	/// <summary>
	/// Which way a bot sidesteps this tick: +1 or -1, held for a whole strafe period
	/// and seeded so that two bots in the same firefight do not mirror each other.
	/// </summary>
	public static float StrafeSign(uint tick, int peerId, int periodTicks)
	{
		uint bucket = tick / (uint)Math.Max(periodTicks, 1);
		return (Spread.Seed(peerId, StrafeSeedSalt, bucket) & 1u) == 0u ? 1f : -1f;
	}

	/// <summary>
	/// A world direction expressed in the axes an <see cref="InputFrame"/> carries:
	/// the exact inverse of <see cref="Movement.Direction"/>, which is what the
	/// character will do with them a moment later.
	/// </summary>
	public static Vector2 MoveAxes(Vector3 worldDirection, float yaw)
	{
		worldDirection.Y = 0f;
		if (worldDirection.LengthSquared() < 0.0001f)
		{
			return Vector2.Zero;
		}

		Vector3 local = Basis.FromEuler(new Vector3(0f, yaw, 0f)).Inverse() * worldDirection.Normalized();
		var axes = new Vector2(local.X, local.Z);
		return axes.LengthSquared() > 1f ? axes.Normalized() : axes;
	}

	/// <summary>
	/// Turns <paramref name="current"/> towards <paramref name="wanted"/> by at most
	/// <paramref name="maxStep"/>, the short way round.
	/// </summary>
	public static float Slew(float current, float wanted, float maxStep)
	{
		float difference = Mathf.AngleDifference(current, wanted);
		return current + Mathf.Clamp(difference, -maxStep, maxStep);
	}

	/// <summary>
	/// The buttons for this tick, with the aim already slewed — the trigger has to
	/// be decided from where the bot *is* pointed after turning, not from where it
	/// wanted to point.
	/// </summary>
	private static InputButtons Buttons(uint tick, in BotSituation situation, in BotTraits traits,
		float yaw, float pitch)
	{
		InputButtons buttons = InputButtons.None;

		if (ShouldFire(situation, traits, yaw, pitch))
		{
			buttons |= InputButtons.Fire;
		}
		else if (situation.NeedsReload && !situation.HasTarget)
		{
			// Between fights only: a reload started with something in the open is how
			// a bot dies holding an empty rifle. An empty magazine under fire reloads
			// itself through WeaponSim, which is the behaviour a player would get.
			buttons |= InputButtons.Reload;
		}

		if (situation.Stuck)
		{
			// A jump clears the kerb or the crate the character is pressed against.
			// The pilot also turns the bot away when this persists; between the two,
			// nothing stays wedged for more than a second or so.
			buttons |= InputButtons.Jump;
		}

		// Sprint is edge-triggered out of the walking state (PlayerWalkingState), so
		// it is only held once the character is definitely moving — held from a
		// standstill, the press happens while idle and the edge is never seen again.
		if (!situation.HasTarget && situation.HasDestination && situation.Speed > SprintMinSpeed
			&& situation.DestinationDistance > SprintDistanceMeters)
		{
			buttons |= InputButtons.Sprint;
		}

		return buttons;
	}

	/// <summary>
	/// Whether to pull the trigger: something visible, in range, held long enough to
	/// have reacted to, and near enough to the middle of the screen.
	/// </summary>
	public static bool ShouldFire(in BotSituation situation, in BotTraits traits, float yaw, float pitch)
	{
		if (!situation.HasTarget || !situation.TargetVisible || situation.AimDirection == Vector3.Zero)
		{
			return false;
		}

		if (situation.TargetDistance > traits.EngageRangeMeters || situation.TicksOnTarget < traits.ReactionTicks)
		{
			return false;
		}

		// Against the *ideal* direction, not the jittered one: the cone is what makes
		// the bot miss, and testing the trigger against it too would make it hold
		// fire until it happened to be aimed correctly — which is a better shot, not
		// a worse one.
		float alignment = Aim.Direction(yaw, pitch).Dot(situation.AimDirection);
		return alignment >= MathF.Cos(traits.FireAngleRadians);
	}
}
