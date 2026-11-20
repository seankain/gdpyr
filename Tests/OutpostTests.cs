using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Gdpyr.Sim;
using Gdpyr.Sim.Agent;
using Gdpyr.Ui;
using Godot;
using Xunit;

namespace Gdpyr.Tests;

/// <summary>
/// The outpost and its two units (docs/OUTPOST.md): the rules that are engine-free — the
/// smoke field, the scout drone's strike, the operative's tactics, covering fire, which
/// kind a train request gets, a knife's reach — and the resource files and tables that
/// put them in the game, read as text as <see cref="TrainedUnitResourceTests"/> reads them.
/// </summary>
public class OutpostTests
{
	private const uint T0 = 1000;

	// ---- smoke (§4.3) --------------------------------------------------------------------

	[Fact]
	public void ACloud_Blooms_Stands_AndThins()
	{
		Assert.Equal(0f, SmokeRules.RadiusAt(-1));
		Assert.Equal(SmokeRules.StartRadiusMeters, SmokeRules.RadiusAt(0), 4);
		Assert.True(SmokeRules.RadiusAt(SmokeRules.BloomTicks / 2) > SmokeRules.StartRadiusMeters);
		Assert.True(SmokeRules.RadiusAt(SmokeRules.BloomTicks / 2) < SmokeRules.RadiusMeters);
		Assert.Equal(SmokeRules.RadiusMeters, SmokeRules.RadiusAt(SmokeRules.BloomTicks), 4);
		Assert.Equal(SmokeRules.RadiusMeters, SmokeRules.RadiusAt(SmokeRules.LifeTicks - SmokeRules.ThinTicks), 4);
		Assert.True(SmokeRules.RadiusAt(SmokeRules.LifeTicks - (SmokeRules.ThinTicks / 2)) < SmokeRules.RadiusMeters);
		Assert.Equal(0f, SmokeRules.RadiusAt(SmokeRules.LifeTicks));
	}

	[Fact]
	public void TheChord_IsWhatOfALineIsInsideTheCloud()
	{
		var centre = new Vector3(0f, 1f, 0f);
		Assert.Equal(8f, SmokeRules.ChordInside(new Vector3(-10f, 1f, 0f), new Vector3(10f, 1f, 0f), centre, 4f), 3);
		Assert.Equal(0f, SmokeRules.ChordInside(new Vector3(-10f, 1f, 5f), new Vector3(10f, 1f, 5f), centre, 4f));

		// A line that ends inside counts only what it has inside: from the edge to its end.
		Assert.Equal(4f, SmokeRules.ChordInside(new Vector3(-10f, 1f, 0f), centre, centre, 4f), 3);
		Assert.Equal(0f, SmokeRules.ChordInside(Vector3.Zero, Vector3.Zero, centre, 4f));
		Assert.Equal(0f, SmokeRules.ChordInside(new Vector3(-10f, 1f, 0f), new Vector3(10f, 1f, 0f), centre, 0f));
	}

	[Fact]
	public void SmokeBlocksALineThroughIt_NotOneBesideIt_NorOneThatOnlyGrazesIt()
	{
		var field = new SmokeField();
		field.Add(new Vector3(0f, 0f, 0f), T0);
		field.Step(T0 + SmokeRules.BloomTicks);

		Vector3 eye(float x, float z) => new(x, 1.5f, z);
		Assert.True(field.Blocks(eye(0f, -20f), eye(0f, 20f)));
		Assert.False(field.Blocks(eye(10f, -20f), eye(10f, 20f)));

		// Through the edge: well under a metre of it is inside.
		float edge = SmokeRules.RadiusMeters - 0.02f;
		Assert.False(field.Blocks(new Vector3(edge, SmokeRules.LiftMeters, -20f), new Vector3(edge, SmokeRules.LiftMeters, 20f)));

		// Somebody inside it is hidden from outside it, and sees nothing out of it.
		Assert.True(field.Blocks(eye(0f, -20f), eye(0f, 0f)));
		Assert.True(field.Blocks(eye(0f, 0f), eye(0f, 20f)));
		Assert.True(field.Covers(eye(0f, 0f)));
		Assert.False(field.Covers(eye(0f, 10f)));
	}

	[Fact]
	public void SmokeIsGone_OnceItsLifeIsOut()
	{
		var field = new SmokeField();
		field.Add(Vector3.Zero, T0);
		Assert.Equal(1, field.Count);

		field.Step(T0 + SmokeRules.LifeTicks - 1);
		Assert.Equal(1, field.Count);

		field.Step(T0 + SmokeRules.LifeTicks);
		Assert.Equal(0, field.Count);
		Assert.False(field.Blocks(new Vector3(0f, 1.5f, -20f), new Vector3(0f, 1.5f, 20f)));
		Assert.Equal(1, field.Released);
	}

	[Fact]
	public void AFullField_GivesUpItsOldestCloud()
	{
		var field = new SmokeField();
		for (int i = 0; i < SmokeRules.Capacity; i++)
		{
			field.Add(new Vector3(i * 20f, 0f, 0f), T0 + (uint)i);
		}

		field.Add(new Vector3(-500f, 0f, 0f), T0 + 100);
		Assert.Equal(SmokeRules.Capacity, field.Count);

		bool oldest = false;
		bool newest = false;
		for (int i = 0; i < field.Count; i++)
		{
			oldest |= field.At(i).StartTick == T0;
			newest |= field.At(i).Ground.X == -500f;
		}

		Assert.False(oldest);
		Assert.True(newest);
		field.Add(new Vector3(float.NaN, 0f, 0f), T0);
		Assert.Equal(SmokeRules.Capacity + 1, field.Released);
	}

	// ---- the scout drone's strike (§3.3) -------------------------------------------------

	[Fact]
	public void AStrike_FliesStraightAtItsAim_AtItsSpeed_AndGoesOffThere()
	{
		var from = new Vector3(0f, 12f, 0f);
		var aim = new Vector3(0f, 1f, 30f);
		Assert.Equal(StrikeEnd.None, StrikeSim.Step(from, aim, T0, T0, SimConfig.TickDelta, out Vector3 velocity));
		Assert.Equal(StrikeRules.SpeedMetersPerSecond, velocity.Length(), 3);
		Assert.True(velocity.Normalized().Dot((aim - from).Normalized()) > 0.9999f);

		// Never past its aim in one tick.
		Vector3 near = aim + new Vector3(0f, 0f, -1.6f);
		Assert.Equal(StrikeEnd.None, StrikeSim.Step(near, aim, T0, T0, SimConfig.TickDelta, out velocity));
		Assert.True(velocity.Length() * SimConfig.TickDelta <= 1.6f + 1e-4f);

		Assert.Equal(StrikeEnd.Arrived, StrikeSim.Step(aim + new Vector3(0f, 0f, -1.4f), aim, T0, T0,
			SimConfig.TickDelta, out velocity));
		Assert.Equal(Vector3.Zero, velocity);
	}

	[Fact]
	public void AStrike_GoesOffWhereItIs_WhenItsTimeRunsOut_OrNearAnEnemy()
	{
		var from = new Vector3(0f, 12f, 0f);
		var aim = new Vector3(0f, 1f, 300f);
		Assert.Equal(StrikeEnd.None, StrikeSim.Step(from, aim, T0, T0 + StrikeRules.MaxTicks - 1, SimConfig.TickDelta, out _));
		Assert.Equal(StrikeEnd.Expired, StrikeSim.Step(from, aim, T0, T0 + StrikeRules.MaxTicks, SimConfig.TickDelta, out _));
		Assert.Equal(StrikeEnd.Expired, StrikeSim.Step(new Vector3(float.NaN, 0f, 0f), aim, T0, T0, SimConfig.TickDelta, out _));

		Assert.True(StrikeSim.Proximate(from, from + new Vector3(0f, -1.9f, 0f)));
		Assert.False(StrikeSim.Proximate(from, from + new Vector3(0f, -2.1f, 0f)));
		Assert.Equal(new Vector3(3f, 1f + StrikeRules.GroundAimLiftMeters, 4f), StrikeSim.GroundAim(new Vector3(3f, 1f, 4f)));
	}

	[Fact]
	public void OnlyAStrikeOrder_IsAStrike_AndItIsIssuable_AndNamesATarget()
	{
		Assert.Equal(9, (int)OrderKind.Strike);
		Assert.True(UnitOrder.IsIssuable((byte)OrderKind.Strike));
		Assert.False(UnitOrder.IsIssuable((byte)OrderKind.Unload));
		Assert.False(UnitOrder.IsIssuable(10));
		Assert.True(UnitOrder.NamesTarget(OrderKind.Strike));
		Assert.True(UnitOrder.NamesTarget(OrderKind.Attack));
		Assert.False(UnitOrder.NamesTarget(OrderKind.Move));
	}

	// ---- the operative's tactics (§4.2) ----------------------------------------------------

	private static OperativeSituation Watched(float distance, uint tick = T0) => new()
	{
		Tick = tick,
		Feet = Vector3.Zero,
		HasQuarry = true,
		QuarryOwnerId = 7,
		QuarryFeet = new Vector3(0f, 0f, distance),
		QuarrySeesIt = true,
		PathExposed = true,
		HasSmoke = true,
	};

	[Fact]
	public void NoQuarry_IsNoHunt()
	{
		var state = new OperativeState();
		OperativeDecision decision = OperativeSim.Step(ref state, new OperativeSituation { Tick = T0 });
		Assert.False(decision.Hunting);
		Assert.Equal(OperativePhase.None, state.Phase);
	}

	[Fact]
	public void InCover_ItHoldsCrouched_AndCallsForFire()
	{
		var state = new OperativeState();
		OperativeSituation s = Watched(30f);
		s.InCover = true;

		OperativeDecision decision = OperativeSim.Step(ref state, s);
		Assert.True(decision.Hunting);
		Assert.Equal(OperativePhase.Hold, state.Phase);
		Assert.True(decision.Crouch);
		Assert.False(decision.Moves);
		Assert.True(decision.CallCover);
		Assert.True(decision.HoldFire);
	}

	[Fact]
	public void WithCoverAhead_ItBoundsToIt_ThenHoldsThere()
	{
		var state = new OperativeState();
		OperativeSituation s = Watched(30f);
		s.HasBound = true;
		s.BoundSpot = new Vector3(1f, 0f, 8f);

		OperativeDecision decision = OperativeSim.Step(ref state, s);
		Assert.Equal(OperativePhase.Bound, state.Phase);
		Assert.True(decision.Moves);
		Assert.False(decision.Crouch);
		Assert.Equal(s.BoundSpot, decision.Point);
		Assert.Equal(1, state.Bounds);

		s.Tick = T0 + 60;
		s.Feet = s.BoundSpot;
		s.InCover = true;
		decision = OperativeSim.Step(ref state, s);
		Assert.Equal(OperativePhase.Hold, state.Phase);
		Assert.True(decision.Crouch);
	}

	[Fact]
	public void InCover_ItGoesOnOnlyWhenCovered_Screened_OrUnwatched_AfterALook()
	{
		foreach (Func<OperativeSituation, OperativeSituation> chance in new Func<OperativeSituation, OperativeSituation>[]
		{
			s => { s.Covered = true; return s; },
			s => { s.Screened = true; return s; },
			s => { s.PathExposed = false; return s; },
		})
		{
			var state = new OperativeState();
			OperativeSituation s = Watched(30f);
			s.InCover = true;
			s.HasBound = true;
			s.BoundSpot = new Vector3(0f, 0f, 9f);
			OperativeSim.Step(ref state, s);

			// Watched, covered by nothing: it stays put however long it has been.
			OperativeSituation watched = s;
			watched.Tick = T0 + OperativeRules.MinHoldTicks;
			OperativeState waiting = state;
			OperativeSim.Step(ref waiting, watched);
			Assert.Equal(OperativePhase.Hold, waiting.Phase);

			OperativeSituation now = chance(s);
			now.Tick = T0 + OperativeRules.MinHoldTicks - 1;
			OperativeSim.Step(ref state, now);
			Assert.Equal(OperativePhase.Hold, state.Phase);

			now.Tick = T0 + OperativeRules.MinHoldTicks;
			OperativeDecision decision = OperativeSim.Step(ref state, now);
			Assert.Equal(OperativePhase.Bound, state.Phase);
			Assert.Equal(s.BoundSpot, decision.Point);
		}
	}

	[Fact]
	public void WatchedOverOpenGround_ItSmokesIt_WaitsForTheCloud_ThenRuns()
	{
		var state = new OperativeState();
		OperativeSituation s = Watched(30f);
		s.InCover = true;
		OperativeSim.Step(ref state, s);

		s.Tick = T0 + OperativeRules.MinHoldTicks;
		OperativeDecision decision = OperativeSim.Step(ref state, s);
		Assert.True(decision.ThrowSmoke);
		Assert.Equal(OperativePhase.Smoke, state.Phase);
		Assert.Equal(new Vector3(0f, 0f, OperativeRules.SmokeLeadMeters), decision.SmokePoint);
		Assert.Equal(1, state.Smokes);

		s.Tick += OperativeRules.SmokeWaitTicks - 1;
		decision = OperativeSim.Step(ref state, s);
		Assert.False(decision.ThrowSmoke);
		Assert.False(decision.Moves);
		Assert.Equal(OperativePhase.Smoke, state.Phase);

		s.Tick += 1;
		decision = OperativeSim.Step(ref state, s);
		Assert.Equal(OperativePhase.Dash, state.Phase);
		Assert.True(decision.Moves);
		Assert.True(decision.CallCover);
		Assert.True(decision.Point.Z > 0f && decision.Point.Z < 30f - OperativeRules.CloseMeters + 2f);
	}

	[Fact]
	public void UnderItsSidesFire_ItRunsAndKeepsItsSmoke()
	{
		var state = new OperativeState();
		OperativeSituation s = Watched(30f);
		s.InCover = true;
		OperativeSim.Step(ref state, s);

		s.Covered = true;
		s.Tick = T0 + OperativeRules.MinHoldTicks;
		OperativeDecision decision = OperativeSim.Step(ref state, s);
		Assert.False(decision.ThrowSmoke);
		Assert.Equal(OperativePhase.Dash, state.Phase);
		Assert.Equal(0, state.Smokes);
	}

	[Fact]
	public void WatchedWithNoSmokeAndNoFire_ItWaitsAsLongAsItWill_ThenRunsAnyway()
	{
		var state = new OperativeState();
		OperativeSituation s = Watched(30f);
		s.InCover = true;
		s.HasSmoke = false;
		OperativeSim.Step(ref state, s);

		s.Tick = T0 + OperativeRules.MaxHoldTicks - 1;
		OperativeSim.Step(ref state, s);
		Assert.Equal(OperativePhase.Hold, state.Phase);

		s.Tick = T0 + OperativeRules.MaxHoldTicks;
		OperativeSim.Step(ref state, s);
		Assert.Equal(OperativePhase.Dash, state.Phase);
	}

	[Fact]
	public void ADash_EndsInAHold_WhenItGetsThere_OrRunsOutOfBreath()
	{
		var state = new OperativeState();
		OperativeSituation s = Watched(30f);
		s.HasSmoke = false;
		OperativeDecision decision = OperativeSim.Step(ref state, s);
		Assert.Equal(OperativePhase.Dash, state.Phase);

		s.Tick = T0 + OperativeRules.DashTicks;
		decision = OperativeSim.Step(ref state, s);
		Assert.Equal(OperativePhase.Hold, state.Phase);
		Assert.True(decision.CallCover);
	}

	[Fact]
	public void Close_ItRushes_ShootsWhenSeen_AndStabsWhatItsKnifeReaches()
	{
		var state = new OperativeState();
		OperativeSituation s = Watched(OperativeRules.CloseMeters - 1f);
		OperativeDecision decision = OperativeSim.Step(ref state, s);
		Assert.Equal(OperativePhase.Close, state.Phase);
		Assert.Equal(s.QuarryFeet, decision.Point);
		Assert.False(decision.HoldFire);
		Assert.False(decision.Knife);

		s.InKnifeReach = true;
		decision = OperativeSim.Step(ref state, s);
		Assert.True(decision.Knife);

		// One stab per interval.
		s.Tick += 1;
		Assert.False(OperativeSim.Step(ref state, s).Knife);
		s.Tick = T0 + OperativeRules.KnifeIntervalTicks;
		Assert.True(OperativeSim.Step(ref state, s).Knife);
	}

	[Fact]
	public void ItHoldsItsFire_WhileFar_OrUnseen_UnlessItIsShotAt()
	{
		var state = new OperativeState();
		Assert.True(OperativeSim.Step(ref state, Watched(OperativeRules.OpenFireMeters + 1f)).HoldFire);

		OperativeSituation unseen = Watched(5f);
		unseen.QuarrySeesIt = false;
		Assert.True(OperativeSim.Step(ref state, unseen).HoldFire);

		unseen.FiredUpon = true;
		Assert.False(OperativeSim.Step(ref state, unseen).HoldFire);

		OperativeSituation far = Watched(40f);
		far.FiredUpon = true;
		Assert.False(OperativeSim.Step(ref state, far).HoldFire);
	}

	[Fact]
	public void ANewQuarry_IsANewApproach()
	{
		var state = new OperativeState();
		OperativeSituation s = Watched(30f);
		s.InCover = true;
		OperativeSim.Step(ref state, s);
		s.Tick += 10;
		OperativeSim.Step(ref state, s);
		Assert.Equal(T0, state.PhaseTick);

		s.QuarryOwnerId = 8;
		OperativeSim.Step(ref state, s);
		Assert.Equal(8, state.QuarryOwnerId);
		Assert.Equal(T0 + 10, state.PhaseTick);
	}

	[Fact]
	public void SmokeIsThrownTowardsTheQuarry_NoNearerThanAGrenadeIsAimed_NoFurtherThanHalfway()
	{
		Assert.Equal(OperativeRules.SmokeLeadMeters, OperativeSim.SmokePoint(Watched(40f)).Z, 4);
		Assert.Equal(7f, OperativeSim.SmokePoint(Watched(14f)).Z, 4);
		Assert.Equal(OperativeRules.MinSmokeMeters, OperativeSim.SmokePoint(Watched(8f)).Z, 4);
		Assert.True(OperativeRules.MinSmokeMeters >= GrenadeSim.MinThrowMeters);
	}

	[Fact]
	public void AQuarry_WatchesWhatIsInFrontOfThem_NotWhatIsBehind()
	{
		var eye = new Vector3(0f, 1.6f, 0f);
		var north = new Vector3(0f, 0f, 1f);
		Assert.True(OperativeSim.Watches(eye, north, new Vector3(0f, 0f, 20f)));
		Assert.True(OperativeSim.Watches(eye, north, new Vector3(15f, 0f, 10f)));
		Assert.False(OperativeSim.Watches(eye, north, new Vector3(0f, 0f, -20f)));
		Assert.False(OperativeSim.Watches(eye, north, new Vector3(20f, 0f, 1f)));
		Assert.True(OperativeSim.Watches(eye, Vector3.Zero, new Vector3(0f, 0f, -20f)));
	}

	[Fact]
	public void AKnife_ReachesWhatIsWithinItsLength()
	{
		HitCapsule body = HitCapsule.FromFeet(new Vector3(0f, 0f, 2f), 1.8f, 0.5f);
		var eye = new Vector3(0f, 1.5f, 0f);
		Assert.True(UnitMelee.Reaches(eye, body, 1.8f));
		Assert.False(UnitMelee.Reaches(eye, body, 1.4f));
		Assert.False(UnitMelee.Reaches(eye, body, 0f));
		Assert.False(UnitMelee.Reaches(new Vector3(float.NaN, 0f, 0f), body, 1.8f));
	}

	// ---- covering fire (§4.5) --------------------------------------------------------------

	[Fact]
	public void FireNearTheQuarry_CoversIt_ForAMoment()
	{
		var board = new CoveringFireBoard();
		var quarry = new Vector3(0f, 0f, 40f);
		Assert.False(board.Covers(quarry, T0));

		board.NoteShot(quarry + new Vector3(3f, 1.2f, 0f), T0);
		Assert.True(board.Covers(quarry, T0 + CoveringFireBoard.WindowTicks));
		Assert.False(board.Covers(quarry, T0 + CoveringFireBoard.WindowTicks + 1));
		Assert.False(board.Covers(quarry + new Vector3(10f, 0f, 0f), T0));

		board.Clear();
		Assert.False(board.Covers(quarry, T0));
	}

	[Fact]
	public void ACallIsAnswered_ByTheNearest_InReach_AndNear_TheOperative()
	{
		var board = new CoveringFireBoard();
		var operative = new Vector3(0f, 0f, 20f);
		board.Call(1, operative, new Vector3(0f, 0f, 50f), T0 + 120);
		board.Call(2, operative, new Vector3(10f, 0f, 30f), T0 + 120);
		Assert.Equal(2, board.Calls);

		Assert.True(board.TryAnswer(new Vector3(0f, 0f, 10f), 40f, T0, out Vector3 point));
		Assert.Equal(new Vector3(10f, 0f, 30f), point);

		// Out of its reach, or too far from whoever called.
		Assert.False(board.TryAnswer(new Vector3(0f, 0f, -40f), 40f, T0, out _));
		Assert.False(board.TryAnswer(new Vector3(0f, 0f, 120f), 200f, T0, out _));

		// A second call replaces the first; a withdrawn one is gone; an old one runs out.
		board.Call(2, operative, new Vector3(0f, 0f, 45f), T0 + 10);
		Assert.Equal(2, board.Calls);
		board.Withdraw(1);
		Assert.Equal(1, board.Calls);
		board.Expire(T0 + 11);
		Assert.Equal(0, board.Calls);
		Assert.False(board.TryAnswer(new Vector3(0f, 0f, 10f), 40f, T0 + 11, out _));
	}

	// ---- the outpost's queue (§2) -----------------------------------------------------------

	[Fact]
	public void ATrainRequest_GetsTheKindItNamed_AtAnOutpost_AndTheOneKind_Elsewhere()
	{
		Assert.True(StructureTraining.TryChoose(8, 9, 2, 9, out byte kind));
		Assert.Equal(9, kind);
		Assert.True(StructureTraining.TryChoose(8, 9, 2, 8, out kind));
		Assert.Equal(8, kind);
		Assert.False(StructureTraining.TryChoose(8, 9, 2, -1, out _));
		Assert.False(StructureTraining.TryChoose(8, 9, 2, 5, out _));

		Assert.True(StructureTraining.TryChoose(5, 5, 1, -1, out kind));
		Assert.Equal(5, kind);
		Assert.True(StructureTraining.TryChoose(5, 5, 1, 0, out kind));
		Assert.Equal(5, kind);
		Assert.False(StructureTraining.TryChoose(0, 0, 0, 0, out _));
	}

	// ---- the files and tables ---------------------------------------------------------------

	[Fact]
	public void TheOutpost_IsAppended_AndTrainsTheScoutDroneAndTheOperative()
	{
		Assert.Equal(3, StructureKinds.Outpost);
		Assert.Equal(4, StructureKinds.Count);
		Assert.Equal(StructureKinds.Count, AgentJson.StructureNames.Length);
		Assert.Equal("outpost", AgentJson.StructureNames[StructureKinds.Outpost]);

		Dictionary<string, string> outpost = TrainedUnitResourceTests.Resource("Structures/outpost.tres");
		Assert.Equal("res://Units/scout_drone.tres", TrainedUnitResourceTests.UnitPath(TrainedUnitResourceTests.Int(outpost, "TrainsUnitId")));
		Assert.Equal("res://Units/operative.tres", TrainedUnitResourceTests.UnitPath(TrainedUnitResourceTests.Int(outpost, "AlsoTrainsUnitId")));
		Assert.Equal(-1, TrainedUnitResourceTests.Int(outpost, "WeaponId"));

		// Not armour: rifles chip at it, as at a sandbag wall.
		Assert.InRange(TrainedUnitResourceTests.Float(outpost, "BulletDamageScale"), 0.01f, 0.99f);

		string catalog = File.ReadAllText(TrainedUnitResourceTests.Repo("Scripts/Core/StructureCatalog.cs"));
		Assert.Contains("\"res://Structures/outpost.tres\",      // 3", catalog);
	}

	[Fact]
	public void TheScoutDroneAndTheOperative_AreAppended_AfterTheDropship()
	{
		Assert.Equal("res://Units/scout_drone.tres", TrainedUnitResourceTests.UnitPath(8));
		Assert.Equal("res://Units/operative.tres", TrainedUnitResourceTests.UnitPath(9));
		Assert.True(10 <= SimConfig.MaxUnitDefinitions);

		foreach (string unit in new[] { "scout_drone", "operative" })
		{
			Assert.Equal("false", TrainedUnitResourceTests.Resource($"Units/{unit}.tres")["TrainedInBarracks"]);
		}
	}

	[Fact]
	public void TheScoutDrone_HoversUnarmed_SeesFar_AndCarriesTheCharge()
	{
		Dictionary<string, string> drone = TrainedUnitResourceTests.Resource("Units/scout_drone.tres");
		Dictionary<string, string> infantry = TrainedUnitResourceTests.Resource("Units/infantry.tres");

		Assert.Equal("true", drone["Hovers"]);
		Assert.Equal("true", drone["IsVehicle"]);
		Assert.Equal(0f, TrainedUnitResourceTests.Float(drone, "EngageRangeMeters"));
		Assert.True(TrainedUnitResourceTests.Float(drone, "SensorRadiusMeters") > TrainedUnitResourceTests.Float(infantry, "SensorRadiusMeters"));
		Assert.Equal("res://Weapons/scout_charge/scout_charge.tres",
			TrainedUnitResourceTests.WeaponPath(TrainedUnitResourceTests.Int(drone, "StrikeWeaponId")));

		// It climbs clear of every roof before it moves, and its capsule is a capsule.
		float clear = TrainedUnitResourceTests.Float(drone, "ClearAltitudeMeters");
		Assert.InRange(clear, 8.4f, TrainedUnitResourceTests.Float(drone, "CruiseAltitudeMeters"));
		Assert.True(TrainedUnitResourceTests.Float(drone, "HeightMeters") >= 2f * TrainedUnitResourceTests.Float(drone, "RadiusMeters"));

		// Soft: a few rifle rounds bring it down.
		Assert.True(TrainedUnitResourceTests.Float(drone, "MaxHealth") <= 100f);
		Assert.False(drone.ContainsKey("BulletDamageScale"));

		// Nothing else carries a charge, and nothing else hovers.
		foreach (string unit in new[] { "infantry", "technical", "tank", "builder", "supply_truck", "sniper", "heavy_gunner", "dropship", "operative" })
		{
			Dictionary<string, string> file = TrainedUnitResourceTests.Resource($"Units/{unit}.tres");
			Assert.False(file.ContainsKey("StrikeWeaponId"), unit);
			Assert.False(file.ContainsKey("Hovers"), unit);
		}
	}

	[Fact]
	public void TheScoutCharge_KillsAPlayerItGoesOffBeside_AndIsDrawnAsADrone()
	{
		Dictionary<string, string> charge = TrainedUnitResourceTests.Resource("Weapons/projectiles/scout_charge.tres");
		float radius = TrainedUnitResourceTests.Float(charge, "ExplosionRadiusMeters");
		float damage = TrainedUnitResourceTests.Float(charge, "ExplosionDamage");
		Assert.True(damage * (1f - (1.5f / radius)) >= SimConfig.MaxHealth, "a kill within a metre and a half");
		Assert.Equal(ExplosionKind.Drone, (ExplosionKind)TrainedUnitResourceTests.Int(charge, "Explosion"));
	}

	[Fact]
	public void TheOperative_IsFastAndSoft_WithAPistolAKnifeAndSmoke()
	{
		Dictionary<string, string> operative = TrainedUnitResourceTests.Resource("Units/operative.tres");
		Dictionary<string, string> infantry = TrainedUnitResourceTests.Resource("Units/infantry.tres");

		Assert.Equal((int)UnitDoctrine.Operative, TrainedUnitResourceTests.Int(operative, "Doctrine"));
		Assert.True(TrainedUnitResourceTests.Float(operative, "MoveSpeed") >= 1.5f * TrainedUnitResourceTests.Float(infantry, "MoveSpeed"));
		Assert.True(TrainedUnitResourceTests.Float(operative, "MaxHealth") < TrainedUnitResourceTests.Float(infantry, "MaxHealth"));
		Assert.False(operative.ContainsKey("BulletDamageScale"));

		Assert.Equal("res://Weapons/suppressed_pistol/suppressed_pistol.tres",
			TrainedUnitResourceTests.WeaponPath(TrainedUnitResourceTests.Int(operative, "WeaponId")));
		Assert.Equal("res://Weapons/knife/knife.tres",
			TrainedUnitResourceTests.WeaponPath(TrainedUnitResourceTests.Int(operative, "MeleeWeaponId")));
		Assert.True(TrainedUnitResourceTests.Int(operative, "SmokeGrenades") > 0);
		Assert.Equal(0, TrainedUnitResourceTests.Int(operative, "Grenades"));

		// On foot: it crouches below a sandbag wall, as every unit on foot does.
		Assert.Equal(1.0f, TrainedUnitResourceTests.Float(operative, "CrouchHeightMeters"));
		Assert.Equal(0.85f, TrainedUnitResourceTests.Float(operative, "CrouchEyeHeightMeters"));

		// Its pistol reaches past where it opens fire, and its knife kills outright.
		Assert.True(TrainedUnitResourceTests.Float(operative, "EngageRangeMeters") > OperativeRules.OpenFireMeters);
		Dictionary<string, string> knife = TrainedUnitResourceTests.Resource("Weapons/knife/knife.tres");
		Assert.Equal("true", knife["IsMelee"]);
		Assert.True(TrainedUnitResourceTests.Float(knife, "DamageAmount") >= SimConfig.MaxHealth);

		// The smoke grenade is harmless, on a fuse, and lets out a cloud.
		Dictionary<string, string> smoke = TrainedUnitResourceTests.Resource("Weapons/projectiles/smoke_grenade.tres");
		Assert.Equal("true", smoke["Fused"]);
		Assert.Equal("true", smoke["MakesSmoke"]);
		Assert.Equal(0f, TrainedUnitResourceTests.Float(smoke, "ExplosionDamage"));
		Assert.Equal(0f, TrainedUnitResourceTests.Float(smoke, "Damage"));
	}

	[Fact]
	public void CoveringFire_ComesFromRiflemenTanksAndHeavyGunners_AndNothingElse()
	{
		foreach (string unit in new[] { "infantry", "tank", "heavy_gunner" })
		{
			Assert.Equal("true", TrainedUnitResourceTests.Resource($"Units/{unit}.tres")["GivesCoveringFire"]);
		}

		foreach (string unit in new[] { "technical", "builder", "supply_truck", "sniper", "dropship", "scout_drone", "operative" })
		{
			Assert.False(TrainedUnitResourceTests.Resource($"Units/{unit}.tres").ContainsKey("GivesCoveringFire"), unit);
		}
	}

	[Fact]
	public void TheNewWeapons_AreAppended_AfterTheMortarLauncher()
	{
		string catalog = File.ReadAllText(TrainedUnitResourceTests.Repo("Scripts/Core/WeaponCatalog.cs"));
		Assert.Contains("public const byte Knife = 17;", catalog);
		Assert.Contains("public const byte SuppressedPistol = 18;", catalog);
		Assert.Contains("public const byte SmokeGrenade = 19;", catalog);
		Assert.Contains("public const byte ScoutCharge = 20;", catalog);
		Assert.Equal(ShotKind.SuppressedPistol,
			(ShotKind)TrainedUnitResourceTests.Int(TrainedUnitResourceTests.Resource("Weapons/suppressed_pistol/suppressed_pistol.tres"), "Shot"));
	}

	[Fact]
	public void TheSuppressedPistol_IsQuieterThanAPistol_AndHasNoFlashToSpeakOf()
	{
		SoundVoice pistol = WeaponSounds.Shot(ShotKind.Pistol);
		SoundVoice suppressed = WeaponSounds.Shot(ShotKind.SuppressedPistol);
		Assert.True(suppressed.MaxDistanceMeters < pistol.MaxDistanceMeters / 2f);
		Assert.True(suppressed.CrackGain < pistol.CrackGain / 4f);
		Assert.True(suppressed.VolumeDb < pistol.VolumeDb);
		Assert.True(Muzzles.Profile(ShotKind.SuppressedPistol).LightEnergy < Muzzles.Profile(ShotKind.Pistol).LightEnergy / 4f);
		Assert.True(Muzzles.Profile(ShotKind.SuppressedPistol).FlashLength < Muzzles.Profile(ShotKind.Pistol).FlashLength);
	}

	[Fact]
	public void TheSmokeIsDrawn_ToTheCloudsNumbers()
	{
		ExplosionProfile smoke = Explosions.Profile(ExplosionKind.Smoke);
		Assert.Equal(SmokeRules.RadiusMeters, smoke.SmokeRadius);
		Assert.Equal(SmokeRules.Seconds, smoke.SmokeSeconds);
		Assert.True(smoke.Lifetime >= SmokeRules.Seconds);
		Assert.Equal(ExplosionKind.Smoke, Explosions.ForHit(HitFlags.Exploded, ExplosionKind.Smoke));
		Assert.True(Explosions.TryParse("smoke", out ExplosionKind kind));
		Assert.Equal(ExplosionKind.Smoke, kind);
	}

	[Fact]
	public void EveryExplosionKind_HasAPoolToBeDrawnFrom()
	{
		// A kind past the end of ExplosionEffects' capacity table is dropped without a word:
		// the smoke grenade's cloud was, until it had one.
		string effects = File.ReadAllText(TrainedUnitResourceTests.Repo("Scripts/Effects/ExplosionEffects.cs"));
		System.Text.RegularExpressions.Match table = Regex.Match(effects, @"int\[\] Capacities = \{([^}]*)\}");
		Assert.True(table.Success);
		string[] capacities = table.Groups[1].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		Assert.Equal(Enum.GetValues<ExplosionKind>().Length, capacities.Length);
		for (int kind = 1; kind < capacities.Length; kind++)
		{
			Assert.True(int.Parse(capacities[kind]) > 0, ((ExplosionKind)kind).ToString());
		}
	}

	[Fact]
	public void TheEventStream_AppendsTheStrike_TheOperative_AndCoveringFire()
	{
		Assert.Equal(30, (int)AgentEventKind.UnitStrike);
		Assert.Equal(31, (int)AgentEventKind.Operative);
		Assert.Equal(32, (int)AgentEventKind.CoveringFire);
		Assert.Equal("unit_strike", AgentEventSchema.NameOf(AgentEventKind.UnitStrike));
		Assert.Equal("operative", AgentEventSchema.NameOf(AgentEventKind.Operative));
		Assert.Equal("covering_fire", AgentEventSchema.NameOf(AgentEventKind.CoveringFire));
		Assert.Equal(new[] { "unit", "end", "killed", "x", "z", "" }, AgentEventSchema.FieldsOf(AgentEventKind.UnitStrike).ToArray());
		Assert.Equal(new[] { "unit", "phase", "act", "x", "z", "" }, AgentEventSchema.FieldsOf(AgentEventKind.Operative).ToArray());
		Assert.Equal(new[] { "unit", "tier", "", "x", "z", "" }, AgentEventSchema.FieldsOf(AgentEventKind.CoveringFire).ToArray());
		Assert.Equal("killed", Gdpyr.Playtest.EpisodeMetrics.DefaultFieldOf("unit_strike"));
		Assert.Equal("phase", Gdpyr.Playtest.EpisodeMetrics.DefaultFieldOf("operative"));
		Assert.Contains("strike", AgentJson.OrderNames);
	}

	// ---- the strategist's keys -----------------------------------------------------------

	[Fact]
	public void TheOutpostsKeys_AreBound_ToKeysNothingElseOnTheStrategistsSideUses()
	{
		string project = File.ReadAllText(TrainedUnitResourceTests.Repo("project.godot"));
		var expected = new Dictionary<string, int>
		{
			["rts_place_outpost"] = 'O',
			["rts_build_scout_drone"] = '-',
			["rts_build_operative"] = '=',
			["rts_order_strike"] = 'J',
		};

		var strategist = new Dictionary<int, string>();
		foreach (System.Text.RegularExpressions.Match action in Regex.Matches(project,
			@"^(rts_\w+)=\{[^}]*?""physical_keycode"":(\d+)", RegexOptions.Multiline))
		{
			int key = int.Parse(action.Groups[2].Value);
			Assert.False(strategist.ContainsKey(key), $"{action.Groups[1].Value} shares a key with {strategist.GetValueOrDefault(key)}");
			strategist[key] = action.Groups[1].Value;
		}

		string controller = File.ReadAllText(TrainedUnitResourceTests.Repo("Scripts/Rts/StrategistController.cs"));
		foreach ((string action, int key) in expected)
		{
			Assert.Equal(action, strategist[key]);
			Assert.Contains($"\"{action}\"", controller);
		}

		Assert.Equal("J", StrategistCards.StrikeKey);
		Assert.Contains("O outpost", StrategistCards.KeyList);
		Assert.Contains("- scout drone · = operative", StrategistCards.KeyList);
		Assert.Contains("J then RMB strikes", StrategistCards.KeyList);
		Assert.Equal("strike", StrategistCards.NameOf(OrderKind.Strike));
		Assert.Equal("right-click: strike there · on an enemy: strike them",
			StrategistCards.RightClick(StrategistCardMode.Units, OrderKind.Strike, null));
		Assert.Equal(string.Empty, StrategistCards.RightClick(StrategistCardMode.None, OrderKind.Strike, null));
	}
}
