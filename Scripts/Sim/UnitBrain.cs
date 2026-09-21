using System;
using Godot;

namespace Gdpyr.Sim;

/// <summary>
/// What a unit is doing, as one byte on the wire
/// (docs/IMPLEMENTATION_PLAN.md §M3: Idle → Moving → Engaging → Dead).
///
/// The state says what the unit is doing *about the enemy*; where it is walking is
/// a separate question answered by <see cref="UnitBrain.Destination"/>. A unit can
/// be <see cref="Engaging"/> while still walking — that is what a move order
/// through a firefight looks like — and separating the two is what keeps the FSM
/// from needing a state per combination.
/// </summary>
public enum UnitStateId : byte
{
	Idle = 0,
	Moving = 1,
	Engaging = 2,
	Dead = 3,
}

/// <summary>
/// The parts of a <c>UnitDefinition</c> the brain reads. A struct of floats rather
/// than the resource itself, so the decision is testable without an engine
/// (docs/IMPLEMENTATION_PLAN.md §3).
/// </summary>
public readonly struct UnitTraits
{
	/// <summary>How far the unit notices an enemy. The lever that makes scouting a decision (docs/NETCODE.md §6.2).</summary>
	public readonly float SensorRadiusMeters;

	/// <summary>How far it will shoot from. Usually the shorter of the two.</summary>
	public readonly float EngageRangeMeters;

	/// <summary>How close counts as arrived.</summary>
	public readonly float ArrivalRadiusMeters;

	/// <summary>How far a defending unit will stray from its anchor before turning back.</summary>
	public readonly float LeashRadiusMeters;

	public UnitTraits(float sensorRadiusMeters, float engageRangeMeters, float arrivalRadiusMeters,
		float leashRadiusMeters)
	{
		SensorRadiusMeters = MathF.Max(sensorRadiusMeters, 0f);
		EngageRangeMeters = MathF.Max(engageRangeMeters, 0f);
		ArrivalRadiusMeters = MathF.Max(arrivalRadiusMeters, 0.1f);
		LeashRadiusMeters = MathF.Max(leashRadiusMeters, 0f);
	}
}

/// <summary>Everything the brain is allowed to know about one tick.</summary>
public readonly struct UnitSituation
{
	public readonly bool Alive;
	public readonly OrderKind Order;

	/// <summary>True when a live enemy has been acquired; its distance is then meaningful.</summary>
	public readonly bool HasTarget;

	public readonly float TargetDistance;

	/// <summary>Distance to wherever <see cref="UnitBrain.Destination"/> last said to go.</summary>
	public readonly float DestinationDistance;

	public UnitSituation(bool alive, OrderKind order, bool hasTarget, float targetDistance,
		float destinationDistance)
	{
		Alive = alive;
		Order = order;
		HasTarget = hasTarget;
		TargetDistance = targetDistance;
		DestinationDistance = destinationDistance;
	}
}

/// <summary>
/// The unit FSM, as a pure function (docs/IMPLEMENTATION_PLAN.md §M3).
///
/// Units are server-only and never predicted, so unlike the character simulation
/// this does not *have* to be pure. It is anyway, for the same reason
/// <c>Scripts/Sim</c> exists at all: "why did twenty units walk into a wall" is a
/// question worth being able to answer from a test rather than from a server.
///
/// Pathing is not here. Where a unit wants to be is a decision; how it gets there
/// is <c>NavigationAgent3D</c>'s problem.
/// </summary>
public static class UnitBrain
{
	/// <summary>
	/// The state this unit is in, given its situation. A total function of its
	/// arguments: the same situation always produces the same state, whatever the
	/// unit was doing a tick ago.
	/// </summary>
	public static UnitStateId Next(in UnitSituation situation, in UnitTraits traits)
	{
		if (!situation.Alive)
		{
			return UnitStateId.Dead;
		}

		if (situation.HasTarget && situation.TargetDistance <= traits.EngageRangeMeters)
		{
			return UnitStateId.Engaging;
		}

		return situation.DestinationDistance > traits.ArrivalRadiusMeters
			? UnitStateId.Moving
			: UnitStateId.Idle;
	}

	/// <summary>
	/// Whether a unit that is shooting stands still to do it.
	///
	/// A move order is "be over there"; interrupting it to fight is how an RTS
	/// player loses a flank they had already paid for. An attack-move is the order
	/// that means "stop for anything you meet", and is what the strategist is meant
	/// to reach for when they want a firefight.
	/// </summary>
	public static bool HoldsWhileEngaging(OrderKind order) =>
		order is OrderKind.None or OrderKind.Attack or OrderKind.Defend;

	/// <summary>
	/// Where the unit should be walking, given its order and what it can see.
	/// <paramref name="hasDestination"/> is false when it should stand still, in
	/// which case the returned point is its own position.
	/// </summary>
	public static Vector3 Destination(in UnitOrder order, Vector3 self, bool hasTarget, Vector3 target,
		in UnitTraits traits, out bool hasDestination)
	{
		hasDestination = true;

		switch (order.Kind)
		{
			case OrderKind.Move:
				return order.Target;

			case OrderKind.Attack:
				// Attack-move: the ordered point until something turns up, then the
				// something. A unit already in range stops, which Next() reports as
				// Engaging and HoldsWhileEngaging turns into standing still.
				return hasTarget ? target : order.Target;

			case OrderKind.Patrol:
				return order.Returning ? order.Anchor : order.Target;

			case OrderKind.Defend:
				// Chases only as far as the leash, then goes home. Without this a probe
				// at the edge of the sensor drags the whole garrison off the point.
				if (hasTarget && self.DistanceTo(order.Anchor) <= traits.LeashRadiusMeters
					&& target.DistanceTo(order.Anchor) <= traits.LeashRadiusMeters)
				{
					return target;
				}
				return order.Anchor;

			default:
				hasDestination = false;
				return self;
		}
	}

	/// <summary>
	/// Turns a patrol around when it reaches an end. Called with the result of
	/// arriving, so that "arrived" is decided once, by the same radius everything
	/// else uses.
	/// </summary>
	public static void AdvancePatrol(ref UnitOrder order, bool arrived)
	{
		if (order.Kind == OrderKind.Patrol && arrived)
		{
			order.Returning = !order.Returning;
		}
	}

	/// <summary>
	/// Whether a candidate is worth acquiring: alive, on the other side, and inside
	/// the sensor. Separate from the FSM because acquisition is throttled — a unit
	/// re-scans every few ticks, not every tick (docs/IMPLEMENTATION_PLAN.md §6, on
	/// scene scans) — while the FSM runs on all of them.
	/// </summary>
	public static bool CanAcquire(float distance, in UnitTraits traits) =>
		distance <= traits.SensorRadiusMeters;

	/// <summary>
	/// Whether an acquired target should be dropped. Hysteresis: a target is kept
	/// until it is well outside the sensor, so a player walking the sensor boundary
	/// does not make the unit flicker between Idle and Engaging every tick.
	/// </summary>
	public static bool ShouldDropTarget(float distance, in UnitTraits traits) =>
		distance > traits.SensorRadiusMeters * 1.25f;

	/// <summary>
	/// Where to aim to hit a target that is moving, given how long the round takes
	/// to arrive. First order only — it ignores that the target moves during the
	/// lead itself — which at prototype ranges is worth centimetres.
	///
	/// Without this a unit firing a 340 m/s round at a player sprinting across it
	/// at 50 m misses by most of a metre every time, and the firefight the milestone
	/// is trying to produce never happens (docs/NETCODE.md §4.4).
	/// </summary>
	public static Vector3 Lead(Vector3 muzzle, Vector3 target, Vector3 targetVelocity, float projectileSpeed)
	{
		if (projectileSpeed <= 0f)
		{
			return target;
		}

		float flight = muzzle.DistanceTo(target) / projectileSpeed;
		return target + (targetVelocity * flight);
	}
}
