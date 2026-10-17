using System;
using Gdpyr.Sim;
using Xunit;

namespace Gdpyr.Tests;

/// <summary>
/// The dropship's phase machine (docs/VEHICLES.md §4.2): it climbs to cruise, moves
/// across the ground only once clear of the roofs, comes straight down to land, takes off
/// again when given somewhere to go, and lets nobody in or out unless it is on the ground.
/// </summary>
public class FlightTests
{
	// The dropship as the plan writes it (§8.1).
	private static readonly FlightTraits Dropship = new(cruiseAltitudeMeters: 14f, climbMetersPerSecond: 4f,
		descentMetersPerSecond: 3f, clearAltitudeMeters: 10f);

	private const float Dt = SimConfig.TickDelta;

	/// <summary>The map's floor top: where the ground is under everything here.</summary>
	private const float Floor = 0.5f;

	/// <summary>One tick of the phase machine, with the height moved by the velocity it returns.</summary>
	private static bool Tick(in FlightTraits traits, ref FlightState state, ref float height, bool wantsAir)
	{
		float velocity = Flight.Step(traits, ref state, wantsAir, height, Dt, out bool horizontal);
		height = MathF.Max(height + (velocity * Dt), 0f);
		return horizontal;
	}

	[Fact]
	public void TakeOff_ReachesCruise_InTheTicksTheClimbRateTakes()
	{
		var state = new FlightState();
		float height = 0f;
		int expected = (int)MathF.Ceiling(14f / 4f / Dt);

		int ticks = 0;
		while (state.Phase != FlightPhase.Cruising && ticks < 10_000)
		{
			Tick(Dropship, ref state, ref height, wantsAir: true);
			ticks++;
		}

		Assert.Equal(expected, ticks);
		Assert.Equal(14f, height, 3);
	}

	[Fact]
	public void TheFirstTickOfATakeOff_IsAlreadyClimbing()
	{
		var state = new FlightState();
		float velocity = Flight.Step(Dropship, ref state, wantsAir: true, heightAboveGround: 0f, Dt, out _);

		Assert.Equal(FlightPhase.TakingOff, state.Phase);
		Assert.Equal(4f, velocity, 4);
	}

	[Fact]
	public void BelowClearAltitude_ItMovesOnlyUpAndDown_OnTheWayUp()
	{
		var state = new FlightState();
		float height = 0f;
		for (int i = 0; i < 400; i++)
		{
			float before = height;
			bool horizontal = Tick(Dropship, ref state, ref height, wantsAir: true);
			Assert.Equal(before >= 10f, horizontal);
		}

		Assert.Equal(FlightPhase.Cruising, state.Phase);
	}

	[Fact]
	public void ComingDown_ItNeverMovesAcross()
	{
		FlightState state = Cruising(out float height);
		for (int i = 0; i < 600 && state.Phase != FlightPhase.Grounded; i++)
		{
			Assert.False(Tick(Dropship, ref state, ref height, wantsAir: false));
		}

		Assert.Equal(FlightPhase.Grounded, state.Phase);
	}

	[Fact]
	public void OnTheGround_ItDoesNotMoveAtAll()
	{
		var state = new FlightState();
		float velocity = Flight.Step(Dropship, ref state, wantsAir: false, heightAboveGround: 0f, Dt, out bool horizontal);

		Assert.Equal(0f, velocity);
		Assert.False(horizontal);
		Assert.Equal(FlightPhase.Grounded, state.Phase);
	}

	[Fact]
	public void Landing_ReturnsItToTheGround()
	{
		FlightState state = Cruising(out float height);

		int ticks = 0;
		while (state.Phase != FlightPhase.Grounded && ticks < 10_000)
		{
			Tick(Dropship, ref state, ref height, wantsAir: false);
			ticks++;
		}

		Assert.Equal(FlightPhase.Grounded, state.Phase);
		Assert.InRange(height, 0f, Flight.TouchdownMeters);

		// At 3 m/s from 14 m, and not faster.
		Assert.InRange(ticks, (int)MathF.Floor((14f - Flight.TouchdownMeters) / 3f / Dt), (int)MathF.Ceiling(14f / 3f / Dt) + 1);
	}

	[Fact]
	public void ANewDestination_WhileLanding_TakesItOffAgain()
	{
		FlightState state = Cruising(out float height);
		for (int i = 0; i < 120; i++)
		{
			Tick(Dropship, ref state, ref height, wantsAir: false);
		}

		Assert.Equal(FlightPhase.Landing, state.Phase);
		float low = height;

		Tick(Dropship, ref state, ref height, wantsAir: true);
		Assert.Equal(FlightPhase.TakingOff, state.Phase);
		Assert.True(height > low);

		for (int i = 0; i < 600 && state.Phase != FlightPhase.Cruising; i++)
		{
			Tick(Dropship, ref state, ref height, wantsAir: true);
		}

		Assert.Equal(FlightPhase.Cruising, state.Phase);
	}

	[Fact]
	public void NoLongerWantingAir_OnTheWayUp_BringsItDownAgain()
	{
		var state = new FlightState();
		float height = 0f;
		for (int i = 0; i < 60; i++)
		{
			Tick(Dropship, ref state, ref height, wantsAir: true);
		}

		Assert.Equal(FlightPhase.TakingOff, state.Phase);

		for (int i = 0; i < 600 && state.Phase != FlightPhase.Grounded; i++)
		{
			Tick(Dropship, ref state, ref height, wantsAir: false);
		}

		Assert.Equal(FlightPhase.Grounded, state.Phase);
	}

	[Fact]
	public void WithNowhereToLand_ItHoldsCruise()
	{
		FlightState state = Cruising(out float height);
		state.CannotLand = true;

		for (int i = 0; i < 600; i++)
		{
			Assert.True(Tick(Dropship, ref state, ref height, wantsAir: false));
		}

		Assert.Equal(FlightPhase.Cruising, state.Phase);
		Assert.Equal(14f, height, 3);

		state.CannotLand = false;
		Tick(Dropship, ref state, ref height, wantsAir: false);
		Assert.Equal(FlightPhase.Landing, state.Phase);
	}

	[Fact]
	public void SomebodyUnderIt_HoldsItAtTheFinalCheck_UntilTheyGo()
	{
		FlightState state = Cruising(out float height);
		state.GroundBlocked = true;

		for (int i = 0; i < 1200; i++)
		{
			Tick(Dropship, ref state, ref height, wantsAir: false);
		}

		Assert.Equal(FlightPhase.Landing, state.Phase);
		Assert.Equal(Flight.FinalCheckMeters, height, 3);
		Assert.True(Flight.NeedsGroundCheck(state, height));

		state.GroundBlocked = false;
		for (int i = 0; i < 120 && state.Phase != FlightPhase.Grounded; i++)
		{
			Tick(Dropship, ref state, ref height, wantsAir: false);
		}

		Assert.Equal(FlightPhase.Grounded, state.Phase);
	}

	[Fact]
	public void TheGroundIsLookedAt_OnlyComingDown_AndOnlyLow()
	{
		var landing = new FlightState { Phase = FlightPhase.Landing };
		Assert.True(Flight.NeedsGroundCheck(landing, 1.5f));
		Assert.False(Flight.NeedsGroundCheck(landing, 6f));

		Assert.False(Flight.NeedsGroundCheck(new FlightState { Phase = FlightPhase.TakingOff }, 1.5f));
		Assert.False(Flight.NeedsGroundCheck(new FlightState { Phase = FlightPhase.Grounded }, 0f));
	}

	[Theory]
	[InlineData(FlightPhase.TakingOff)]
	[InlineData(FlightPhase.Cruising)]
	[InlineData(FlightPhase.Landing)]
	public void BoardingAndUnloading_AreRefused_OffTheGround(FlightPhase phase)
	{
		var state = new FlightState { Phase = phase };
		Assert.False(Flight.CanBoard(state));
		Assert.False(Flight.CanUnload(state));
	}

	[Fact]
	public void BoardingAndUnloading_AreAllowed_OnTheGround()
	{
		var state = new FlightState();
		Assert.True(Flight.CanBoard(state));
		Assert.True(Flight.CanUnload(state));
	}

	[Fact]
	public void Cruising_HoldsTheAltitude_AfterTheWorldCutsAMoveShort()
	{
		FlightState state = Cruising(out float height);

		// Something stopped it a metre short; the next ticks climb back.
		height -= 1f;
		for (int i = 0; i < 60; i++)
		{
			Tick(Dropship, ref state, ref height, wantsAir: true);
		}

		Assert.Equal(FlightPhase.Cruising, state.Phase);
		Assert.Equal(14f, height, 3);
	}

	[Fact]
	public void AnUnknownHeight_NeitherClimbsNorComesDown()
	{
		FlightState state = Cruising(out _);
		float velocity = Flight.Step(Dropship, ref state, wantsAir: true, float.NaN, Dt, out _);

		Assert.Equal(0f, velocity);
		Assert.Equal(FlightPhase.Cruising, state.Phase);
	}

	[Fact]
	public void AltitudeNeverLeavesTheWiresRange()
	{
		// A file that asks for a kilometre gets the most a position can carry and more.
		var absurd = new FlightTraits(1000f, 50f, 50f, 2000f);
		Assert.Equal(FlightTraits.MaxCruiseAltitudeMeters, absurd.CruiseAltitudeMeters);
		Assert.True(absurd.ClearAltitudeMeters <= absurd.CruiseAltitudeMeters);

		var state = new FlightState();
		float height = 0f;
		for (int i = 0; i < 60 * 60; i++)
		{
			Tick(absurd, ref state, ref height, wantsAir: (i / 600) % 2 == 0);
			Assert.InRange(Floor + height, -SimConfig.MaxPositionMeters, SimConfig.MaxPositionMeters);
		}
	}

	[Fact]
	public void TheTraits_AreClampedIntoSomethingThatFlies()
	{
		var broken = new FlightTraits(14f, 0f, float.NaN, -3f);
		Assert.Equal(FlightTraits.MinVerticalSpeed, broken.ClimbMetersPerSecond);
		Assert.Equal(FlightTraits.MinVerticalSpeed, broken.DescentMetersPerSecond);
		Assert.Equal(0f, broken.ClearAltitudeMeters);
		Assert.True(broken.IsAircraft);

		Assert.False(new FlightTraits(0f, 4f, 3f, 10f).IsAircraft);
		Assert.False(new FlightTraits(float.NaN, 4f, 3f, 10f).IsAircraft);
	}

	[Fact]
	public void AUnitThatDoesNotFly_StaysGrounded_AndMovesFreely()
	{
		var ground = new FlightTraits(0f, 4f, 3f, 10f);
		var state = new FlightState { Phase = FlightPhase.Cruising, CannotLand = true };

		float velocity = Flight.Step(ground, ref state, wantsAir: true, heightAboveGround: 5f, Dt, out bool horizontal);

		Assert.Equal(0f, velocity);
		Assert.True(horizontal);
		Assert.Equal(FlightPhase.Grounded, state.Phase);
		Assert.False(state.CannotLand);
	}

	// ---- landing candidates ------------------------------------------------------

	private static readonly Godot.Vector3 Centre = new(20f, Floor, -40f);

	[Fact]
	public void TheFirstLandingCandidate_IsWhereItWasMeantToLand()
	{
		Assert.Equal(Centre, Flight.LandingCandidate(Centre, new Godot.Vector3(60f, 14.5f, 0f), 0));
	}

	[Fact]
	public void LandingCandidates_AreOnRingsOfSixAndTwelveMetres_AtTheCentresHeight()
	{
		var from = new Godot.Vector3(-30f, 14.5f, 10f);
		for (int i = 1; i < Flight.LandingCandidateCount; i++)
		{
			Godot.Vector3 candidate = Flight.LandingCandidate(Centre, from, i);
			float flat = new Godot.Vector2(candidate.X - Centre.X, candidate.Z - Centre.Z).Length();
			float ring = i <= Flight.LandingCandidatesPerRing ? 6f : 12f;

			Assert.Equal(ring, flat, 3);
			Assert.Equal(Centre.Y, candidate.Y);
		}
	}

	[Fact]
	public void EachRing_IsTriedNearestTheAircraftFirst()
	{
		var from = new Godot.Vector3(60f, 14.5f, -10f);
		for (int ring = 0; ring < Flight.LandingRings; ring++)
		{
			float last = -1f;
			for (int k = 0; k < Flight.LandingCandidatesPerRing; k++)
			{
				Godot.Vector3 candidate = Flight.LandingCandidate(Centre, from, 1 + (ring * Flight.LandingCandidatesPerRing) + k);
				float distance = new Godot.Vector2(candidate.X - from.X, candidate.Z - from.Z).Length();
				Assert.True(distance >= last - 1e-3f, $"ring {ring} slot {k}: {distance} after {last}");
				last = distance;
			}
		}

		// The first on the inner ring lies straight towards the aircraft.
		Godot.Vector3 first = Flight.LandingCandidate(Centre, from, 1);
		Godot.Vector3 toward = (from - Centre) with { Y = 0f };
		Godot.Vector3 offset = (first - Centre) with { Y = 0f };
		Assert.True(toward.Normalized().Dot(offset.Normalized()) > 0.999f);
	}

	[Fact]
	public void LandingCandidates_AreDistinct_AndWrap()
	{
		var from = new Godot.Vector3(0f, 14.5f, 0f);
		for (int i = 0; i < Flight.LandingCandidateCount; i++)
		{
			for (int j = i + 1; j < Flight.LandingCandidateCount; j++)
			{
				Assert.True(Flight.LandingCandidate(Centre, from, i).DistanceTo(Flight.LandingCandidate(Centre, from, j)) > 1f);
			}
		}

		Assert.Equal(Flight.LandingCandidate(Centre, from, 3),
			Flight.LandingCandidate(Centre, from, 3 + Flight.LandingCandidateCount));
	}

	[Fact]
	public void AnAircraftStraightOverTheCentre_StillGetsARing()
	{
		Godot.Vector3 candidate = Flight.LandingCandidate(Centre, Centre + new Godot.Vector3(0f, 14f, 0f), 1);
		Assert.Equal(6f, candidate.DistanceTo(Centre), 3);
	}

	/// <summary>A dropship at cruise, got there the way it would be.</summary>
	private static FlightState Cruising(out float height)
	{
		var state = new FlightState();
		height = 0f;
		for (int i = 0; i < 10_000 && state.Phase != FlightPhase.Cruising; i++)
		{
			Tick(Dropship, ref state, ref height, wantsAir: true);
		}

		Assert.Equal(FlightPhase.Cruising, state.Phase);
		return state;
	}
}
