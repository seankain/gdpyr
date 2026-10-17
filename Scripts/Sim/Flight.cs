using System;
using Godot;

namespace Gdpyr.Sim;

/// <summary>
/// The parts of a <c>UnitDefinition</c> that say how it flies (docs/VEHICLES.md §4.2):
/// how high it cruises, how fast it climbs and comes down, and below what height it
/// moves only up and down. A struct of floats, as <see cref="UnitTraits"/> is, so the
/// phase machine is testable without an engine. A cruise altitude of zero is a unit
/// that does not fly.
/// </summary>
public readonly struct FlightTraits
{
	/// <summary>
	/// The highest an aircraft may cruise above the ground. With the ground anywhere on
	/// the map, which is under 30 m, it keeps an aircraft well inside the ±327.67 m a
	/// position can be on the wire (<see cref="SimConfig.MaxPositionMeters"/>).
	/// </summary>
	public const float MaxCruiseAltitudeMeters = 100f;

	/// <summary>The slowest an aircraft climbs or comes down, so a file that says zero cannot leave one hanging.</summary>
	public const float MinVerticalSpeed = 0.1f;

	/// <summary>Height above the ground it holds once it is up. Zero for a unit that does not fly.</summary>
	public readonly float CruiseAltitudeMeters;

	public readonly float ClimbMetersPerSecond;

	public readonly float DescentMetersPerSecond;

	/// <summary>
	/// Below this it moves only up and down, so it never slides into a barracks roof
	/// (6.5 m) or a sniper tower's cabin (8.4 m) on its way up or down. At most the
	/// cruise altitude.
	/// </summary>
	public readonly float ClearAltitudeMeters;

	public FlightTraits(float cruiseAltitudeMeters, float climbMetersPerSecond, float descentMetersPerSecond,
		float clearAltitudeMeters)
	{
		CruiseAltitudeMeters = Clamp(cruiseAltitudeMeters, 0f, MaxCruiseAltitudeMeters);
		ClimbMetersPerSecond = MathF.Max(Finite(climbMetersPerSecond), MinVerticalSpeed);
		DescentMetersPerSecond = MathF.Max(Finite(descentMetersPerSecond), MinVerticalSpeed);
		ClearAltitudeMeters = Clamp(clearAltitudeMeters, 0f, CruiseAltitudeMeters);
	}

	/// <summary>True for a unit that flies.</summary>
	public bool IsAircraft => CruiseAltitudeMeters > 0f;

	private static float Clamp(float value, float min, float max) => Math.Clamp(Finite(value), min, max);

	private static float Finite(float value) => float.IsFinite(value) ? value : 0f;
}

/// <summary>
/// Where one aircraft is in taking off and landing, and the two things the engine half
/// has found out about the ground that the phase machine needs to know: whether there
/// is anywhere to land, and whether somebody is standing where it is coming down.
/// Server-side; the phase and <see cref="CannotLand"/> are replicated in the unit
/// snapshot's flag byte (docs/VEHICLES.md §6.1).
/// </summary>
public struct FlightState
{
	public FlightPhase Phase;

	/// <summary>
	/// Set by the engine half while it has found nowhere clear to land: the aircraft holds
	/// cruise instead of coming down. Replicated, so the strategist's card can say so.
	/// </summary>
	public bool CannotLand;

	/// <summary>
	/// Set by the engine half while a unit or a player is under an aircraft that is coming
	/// down: it holds at <see cref="Flight.FinalCheckMeters"/> until they have gone.
	/// Never replicated.
	/// </summary>
	public bool GroundBlocked;
}

/// <summary>
/// The dropship's phase machine (docs/VEHICLES.md §4.2), as a pure function of what it
/// wants, how high it is and how long a tick is:
///
/// <code>
///             wants air                    reached cruise
///  Grounded ───────────► TakingOff ──────────────────────► Cruising
///     ▲                      ▲ wants air                       │ nowhere to go, and a landing spot found
///     │ touched down         │                                 ▼
///     └──────────────────────┴──────────────────────────── Landing
/// </code>
///
/// "Wants air" is the caller's: it has a destination further than its arrival radius,
/// measured flat. One that stops wanting air on the way up comes down again, as one
/// that is told to stop in the air does. On the ground it does not move at all; below
/// <see cref="FlightTraits.ClearAltitudeMeters"/> it moves only up and down; coming down
/// it moves only down.
///
/// Engine-free, like the cargo rules beside it. The engine half measures the height,
/// finds the landing spot and checks the ground under it; this is the part that has an
/// answer without them.
/// </summary>
public static class Flight
{
	/// <summary>How close to the ground counts as on it: a capsule resting on the floor reads a few millimetres up.</summary>
	public const float TouchdownMeters = 0.1f;

	/// <summary>
	/// How close to cruise counts as there, so a climb that ends a hair short of it in
	/// floating point still ends.
	/// </summary>
	public const float CruiseToleranceMeters = 0.01f;

	/// <summary>
	/// The height an aircraft coming down looks again for bodies under it, and waits at
	/// while there are any (docs/VEHICLES.md §8.2).
	/// </summary>
	public const float FinalCheckMeters = 2f;

	/// <summary>How far out the first ring of landing candidates is from where it was meant to land.</summary>
	public const float LandingRingMeters = 6f;

	/// <summary>Rings of landing candidates: 6 m, then 12 m out.</summary>
	public const int LandingRings = 2;

	/// <summary>Candidates on each ring, 45° apart.</summary>
	public const int LandingCandidatesPerRing = 8;

	/// <summary>Every landing candidate: the point itself, then each ring.</summary>
	public const int LandingCandidateCount = 1 + (LandingRings * LandingCandidatesPerRing);

	/// <summary>
	/// One tick of flight. Moves <paramref name="state"/> through its phases and returns
	/// the vertical velocity to fly at this tick [m/s]; <paramref name="horizontal"/>
	/// says whether it may move across the ground as well.
	///
	/// <paramref name="heightAboveGround"/> is the height of its feet over the ground
	/// under it, before this tick's move; one that is not known is taken to be the
	/// cruise altitude, which neither climbs nor comes down. The phase it is left in is
	/// the one this tick's velocity puts it in, so a climb that reaches cruise this tick
	/// reports <see cref="FlightPhase.Cruising"/>. Cruising holds the altitude, so a
	/// move the world cut short is made good on the next tick.
	///
	/// A unit that does not fly is left grounded, moving freely and with no vertical
	/// velocity of its own.
	/// </summary>
	public static float Step(in FlightTraits traits, ref FlightState state, bool wantsAir, float heightAboveGround,
		float dt, out bool horizontal)
	{
		if (!traits.IsAircraft)
		{
			state = default;
			horizontal = true;
			return 0f;
		}

		float cruise = traits.CruiseAltitudeMeters;
		float height = float.IsFinite(heightAboveGround) ? MathF.Max(heightAboveGround, 0f) : cruise;
		float step = dt > 0f && float.IsFinite(dt) ? dt : SimConfig.TickDelta;

		switch (state.Phase)
		{
			case FlightPhase.Grounded:
				if (!wantsAir)
				{
					horizontal = false;
					return 0f;
				}

				state.Phase = FlightPhase.TakingOff;
				return Climb(traits, ref state, height, step, out horizontal);

			case FlightPhase.TakingOff:
				if (!wantsAir && !state.CannotLand)
				{
					state.Phase = FlightPhase.Landing;
					return Descend(traits, ref state, height, step, out horizontal);
				}

				return Climb(traits, ref state, height, step, out horizontal);

			case FlightPhase.Cruising:
				if (!wantsAir && !state.CannotLand)
				{
					state.Phase = FlightPhase.Landing;
					return Descend(traits, ref state, height, step, out horizontal);
				}

				horizontal = height >= traits.ClearAltitudeMeters;
				return Toward(height, cruise, traits, step);

			default:
				if (wantsAir)
				{
					state.Phase = FlightPhase.TakingOff;
					return Climb(traits, ref state, height, step, out horizontal);
				}

				return Descend(traits, ref state, height, step, out horizontal);
		}
	}

	/// <summary>
	/// The <paramref name="ordinal"/>-th place an aircraft at <paramref name="from"/> might
	/// come down, meaning to land at <paramref name="centre"/> (docs/VEHICLES.md §8.2): the
	/// point itself, then a ring <see cref="LandingRingMeters"/> out, then a second twice
	/// that, each ring nearest the aircraft first — straight towards it, then round both
	/// sides alternately. At the centre's height. Past the last candidate it wraps.
	///
	/// Candidates only: the engine half asks the world whether each is in the field, on
	/// the ground, on the navigation mesh and clear of everything up to clear altitude, and
	/// takes the first that is.
	/// </summary>
	public static Vector3 LandingCandidate(Vector3 centre, Vector3 from, int ordinal)
	{
		int index = Math.Max(ordinal, 0) % LandingCandidateCount;
		if (index == 0)
		{
			return centre;
		}

		index--;
		int ring = index / LandingCandidatesPerRing;
		int slot = index % LandingCandidatesPerRing;

		Vector3 toward = from - centre;
		toward.Y = 0f;
		toward = toward.LengthSquared() > 1e-6f && float.IsFinite(toward.X) && float.IsFinite(toward.Z)
			? toward.Normalized()
			: Vector3.Back;

		// 0, +1, -1, +2, -2, … steps round from the side facing the aircraft.
		int steps = (slot + 1) / 2;
		float turn = (slot % 2 == 1 ? steps : -steps) * (MathF.Tau / LandingCandidatesPerRing);
		float sin = MathF.Sin(turn);
		float cos = MathF.Cos(turn);
		var direction = new Vector3((toward.X * cos) + (toward.Z * sin), 0f, (toward.Z * cos) - (toward.X * sin));

		return centre + (direction * (LandingRingMeters * (ring + 1)));
	}

	/// <summary>Whether units can get into it: only on the ground. Always true of a unit that does not fly.</summary>
	public static bool CanBoard(in FlightState state) => state.Phase == FlightPhase.Grounded;

	/// <summary>Whether it can let anybody out: only on the ground.</summary>
	public static bool CanUnload(in FlightState state) => state.Phase == FlightPhase.Grounded;

	/// <summary>
	/// Whether the engine half should look under an aircraft for bodies this tick: it is
	/// coming down and at or below <see cref="FinalCheckMeters"/>.
	/// </summary>
	public static bool NeedsGroundCheck(in FlightState state, float heightAboveGround) =>
		state.Phase == FlightPhase.Landing && heightAboveGround <= FinalCheckMeters + CruiseToleranceMeters;

	/// <summary>Up towards cruise, across the ground only once clear; cruising from the tick it gets there.</summary>
	private static float Climb(in FlightTraits traits, ref FlightState state, float height, float dt,
		out bool horizontal)
	{
		float cruise = traits.CruiseAltitudeMeters;
		float velocity = Toward(height, cruise, traits, dt);
		if (height + (velocity * dt) >= cruise - CruiseToleranceMeters)
		{
			state.Phase = FlightPhase.Cruising;
		}

		horizontal = height >= traits.ClearAltitudeMeters;
		return velocity;
	}

	/// <summary>
	/// Straight down, never across, to the ground and no further; held at
	/// <see cref="FinalCheckMeters"/> while something is under it. Grounded from the tick
	/// it touches down.
	/// </summary>
	private static float Descend(in FlightTraits traits, ref FlightState state, float height, float dt,
		out bool horizontal)
	{
		horizontal = false;
		if (height <= TouchdownMeters)
		{
			state.Phase = FlightPhase.Grounded;
			state.GroundBlocked = false;
			return 0f;
		}

		float floor = state.GroundBlocked ? MathF.Min(FinalCheckMeters, height) : 0f;
		float velocity = MathF.Max(-traits.DescentMetersPerSecond, (floor - height) / dt);
		if (!state.GroundBlocked && height + (velocity * dt) <= TouchdownMeters)
		{
			state.Phase = FlightPhase.Grounded;
		}

		return velocity;
	}

	/// <summary>The vertical velocity that takes <paramref name="height"/> to <paramref name="target"/>, no faster than climbing or coming down allows and without passing it.</summary>
	private static float Toward(float height, float target, in FlightTraits traits, float dt)
	{
		float rate = (target - height) / dt;
		return rate >= 0f
			? MathF.Min(rate, traits.ClimbMetersPerSecond)
			: MathF.Max(rate, -traits.DescentMetersPerSecond);
	}
}
