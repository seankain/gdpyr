using System;
using Gdpyr.Sim;
using Godot;
using Xunit;

namespace Gdpyr.Tests;

/// <summary>
/// The tests docs/NETCODE.md §4.5 asks for. The integrator decides where every
/// shot in the game goes, and it is the one part of combat that can be checked
/// against an answer that is known exactly.
/// </summary>
public class BallisticsTests
{
	/// <summary>A 5.56-like round: 4 g, 2.8 mm, drag on.</summary>
	private static ProjectileParams Rifle() =>
		ProjectileParams.FromGramsAndMillimeters(4f, 2.8f, dragCoefficient: 0.295f);

	private static ProjectileParams Vacuum() =>
		ProjectileParams.FromGramsAndMillimeters(4f, 2.8f, dragCoefficient: 0f, airDensity: 0f);

	private static Vector3 Fire(in ProjectileParams p, Vector3 velocity, float seconds, int subStepsPerTick,
		out Vector3 endVelocity)
	{
		var position = Vector3.Zero;
		endVelocity = velocity;

		int ticks = (int)MathF.Round(seconds / SimConfig.TickDelta);
		for (int i = 0; i < ticks; i++)
		{
			Ballistics.Integrate(SimConfig.TickDelta, subStepsPerTick, ref position, ref endVelocity, p);
		}
		return position;
	}

	[Fact]
	public void WithoutDrag_MatchesTheClosedFormSolution()
	{
		var p = Vacuum();
		var v0 = new Vector3(0f, 0f, -400f);

		Vector3 integrated = Fire(p, v0, seconds: 2f, subStepsPerTick: SimConfig.ProjectileSubSteps, out _);
		Vector3 exact = Ballistics.VacuumPosition(Vector3.Zero, v0, 2f);

		// p(t) = p0 + v0 t + ½ g t² is quadratic and Heun integrates a quadratic
		// exactly, so what is left is float32 accumulation: 960 sub-steps summed onto
		// a coordinate that reaches 800 m, where one ulp is already 6e-5 m. That puts
		// docs/NETCODE.md §4.5's flat 1e-3 m out of reach at this range in single
		// precision, so the bound that means something is the relative one; the
		// absolute figure is asserted at a centimetre, which is the resolution the
		// wire has anyway (docs/NETCODE.md §7).
		float error = (integrated - exact).Length();
		Assert.True(error < 0.01f, $"{integrated} vs {exact}");
		Assert.True(error / exact.Length() < 1e-5f, $"relative error {error / exact.Length()}");
	}

	[Fact]
	public void WithoutDrag_MatchesTheClosedFormSolutionToAMillimetreAtShortRange()
	{
		// The same check where float32 is not the limit: 100 m of flight.
		var p = Vacuum();
		var v0 = new Vector3(0f, 0f, -50f);

		Vector3 integrated = Fire(p, v0, seconds: 2f, subStepsPerTick: SimConfig.ProjectileSubSteps, out _);
		Vector3 exact = Ballistics.VacuumPosition(Vector3.Zero, v0, 2f);

		Assert.True((integrated - exact).Length() < 1e-3f, $"{integrated} vs {exact}");
	}

	[Fact]
	public void WithoutDrag_SpeedIsUnchangedHorizontally()
	{
		var p = Vacuum();
		Fire(p, new Vector3(0f, 0f, -400f), seconds: 1f, subStepsPerTick: SimConfig.ProjectileSubSteps,
			out Vector3 velocity);

		Assert.InRange(velocity.Z, -400.001f, -399.999f);
		Assert.InRange(velocity.Y, -Ballistics.Gravity - 0.01f, -Ballistics.Gravity + 0.01f);
	}

	[Fact]
	public void WithDrag_SpeedDecreasesMonotonically()
	{
		var p = Rifle();
		var position = Vector3.Zero;
		var velocity = new Vector3(0f, 0f, -940f);
		float previous = velocity.Length();

		for (int i = 0; i < 60; i++)
		{
			Ballistics.Integrate(SimConfig.TickDelta, SimConfig.ProjectileSubSteps, ref position, ref velocity, p);
			float speed = velocity.Length();
			Assert.True(speed < previous, $"tick {i}: {speed} >= {previous}");
			previous = speed;
		}
	}

	[Fact]
	public void WithDrag_DropGrowsWithRange()
	{
		var p = Rifle();
		var position = Vector3.Zero;
		var velocity = new Vector3(0f, 0f, -400f);
		float previousDrop = 0f;

		for (int i = 0; i < 60; i++)
		{
			Ballistics.Integrate(SimConfig.TickDelta, SimConfig.ProjectileSubSteps, ref position, ref velocity, p);
			float drop = -position.Y;
			Assert.True(drop > previousDrop, $"tick {i}: drop {drop} did not grow from {previousDrop}");
			previousDrop = drop;
		}
	}

	[Fact]
	public void WithDrag_ReachesAGivenRangeLaterThanInVacuum()
	{
		const float range = 200f;
		float dragTime = TimeToRange(Rifle(), range);
		float vacuumTime = TimeToRange(Vacuum(), range);

		Assert.True(dragTime > vacuumTime, $"drag {dragTime}s vs vacuum {vacuumTime}s");
	}

	[Fact]
	public void WithDrag_DropsFurtherThanInVacuumOverTheSameRange()
	{
		const float range = 200f;
		float dragDrop = DropAtRange(Rifle(), range);
		float vacuumDrop = DropAtRange(Vacuum(), range);

		Assert.True(dragDrop > vacuumDrop, $"drag {dragDrop} m vs vacuum {vacuumDrop} m");
	}

	[Fact]
	public void Integration_IsStableAcrossStepSizes()
	{
		// docs/NETCODE.md §4.5: 1 ms and 0.5 ms steps must agree within 1 cm at 300 m.
		var p = Rifle();
		var v0 = new Vector3(0f, 0f, -400f);

		Vector3 coarse = FireFixedStep(p, v0, 0.001f, 300f);
		Vector3 fine = FireFixedStep(p, v0, 0.0005f, 300f);

		Assert.True((coarse - fine).Length() < 0.01f, $"{coarse} vs {fine}");
	}

	[Fact]
	public void Integration_AtTheTickSubStepAgreesWithAMillisecondStep()
	{
		// The sub-step count the game actually runs has to be one of the stable ones,
		// or the previous test is measuring something the game never does.
		var p = Rifle();
		var v0 = new Vector3(0f, 0f, -400f);

		Vector3 game = Fire(p, v0, seconds: 0.75f, subStepsPerTick: SimConfig.ProjectileSubSteps, out _);
		Vector3 reference = FireFixedStepForTime(p, v0, 0.001f, 0.75f);

		Assert.True((game - reference).Length() < 0.01f, $"{game} vs {reference}");
	}

	[Fact]
	public void Integration_IsDeterministic()
	{
		// docs/NETCODE.md §4.2: every peer reproduces the identical trajectory from
		// the spawn record alone, so the same inputs must give bit-identical output.
		var p = Rifle();
		var v0 = new Vector3(0.3f, 0.1f, -400f);
		Vector3 first = Fire(p, v0, seconds: 1f, subStepsPerTick: SimConfig.ProjectileSubSteps, out Vector3 firstVelocity);

		for (int run = 0; run < 1000; run++)
		{
			Vector3 again = Fire(p, v0, seconds: 1f, subStepsPerTick: SimConfig.ProjectileSubSteps, out Vector3 againVelocity);
			Assert.Equal(first.X, again.X);
			Assert.Equal(first.Y, again.Y);
			Assert.Equal(first.Z, again.Z);
			Assert.Equal(firstVelocity.Z, againVelocity.Z);
		}
	}

	[Fact]
	public void DragAcceleration_OpposesMotionAndScalesWithSquaredSpeed()
	{
		var p = Rifle();
		Vector3 slow = Ballistics.DragAcceleration(new Vector3(0f, 0f, -100f), p);
		Vector3 fast = Ballistics.DragAcceleration(new Vector3(0f, 0f, -200f), p);

		Assert.True(slow.Z > 0f, "drag must push back along +Z against -Z motion");
		Assert.InRange(fast.Length() / slow.Length(), 3.9f, 4.1f);
	}

	[Fact]
	public void DragAcceleration_IsZeroAtRestAndInVacuum()
	{
		Assert.Equal(Vector3.Zero, Ballistics.DragAcceleration(Vector3.Zero, Rifle()));
		Assert.Equal(Vector3.Zero, Ballistics.DragAcceleration(new Vector3(0f, 0f, -900f), Vacuum()));
	}

	[Fact]
	public void DragAcceleration_FollowsTheWind()
	{
		// A round travelling exactly with the air feels no drag at all.
		var p = ProjectileParams.FromGramsAndMillimeters(4f, 2.8f, 0.295f,
			windVelocity: new Vector3(0f, 0f, -400f));

		Assert.Equal(Vector3.Zero, Ballistics.DragAcceleration(new Vector3(0f, 0f, -400f), p));
	}

	[Fact]
	public void LiftAcceleration_IsZeroWithoutACoefficient()
	{
		Assert.Equal(Vector3.Zero, Ballistics.LiftAcceleration(new Vector3(0f, 0f, -400f), Rifle(), Vector3.Up));
	}

	[Fact]
	public void LiftAcceleration_ActsAlongTheGivenUp()
	{
		var p = ProjectileParams.FromGramsAndMillimeters(4f, 2.8f, 0.295f, liftCoefficient: 0.5f);
		Vector3 lift = Ballistics.LiftAcceleration(new Vector3(0f, 0f, -400f), p, Vector3.Up);

		Assert.True(lift.Y > 0f);
		Assert.Equal(0f, lift.X);
		Assert.Equal(0f, lift.Z);
	}

	[Fact]
	public void MassIsNeverZero()
	{
		// A zero-mass definition would divide by zero in the drag term and put NaNs
		// on the wire, so the struct refuses one.
		var p = ProjectileParams.FromGramsAndMillimeters(0f, 2.8f, 0.295f);
		Vector3 drag = Ballistics.DragAcceleration(new Vector3(0f, 0f, -400f), p);

		Assert.False(float.IsNaN(drag.X) || float.IsNaN(drag.Y) || float.IsNaN(drag.Z));
	}

	private static float TimeToRange(in ProjectileParams p, float range)
	{
		var position = Vector3.Zero;
		var velocity = new Vector3(0f, 0f, -400f);
		float time = 0f;

		while (-position.Z < range && time < 10f)
		{
			Ballistics.Integrate(SimConfig.TickDelta, SimConfig.ProjectileSubSteps, ref position, ref velocity, p);
			time += SimConfig.TickDelta;
		}
		return time;
	}

	private static float DropAtRange(in ProjectileParams p, float range)
	{
		var position = Vector3.Zero;
		var velocity = new Vector3(0f, 0f, -400f);

		while (-position.Z < range)
		{
			Ballistics.Integrate(SimConfig.TickDelta, SimConfig.ProjectileSubSteps, ref position, ref velocity, p);
		}
		return -position.Y;
	}

	private static Vector3 FireFixedStep(in ProjectileParams p, Vector3 velocity, float step, float range)
	{
		var position = Vector3.Zero;
		while (-position.Z < range)
		{
			Ballistics.Integrate(step, 1, ref position, ref velocity, p);
		}
		return position;
	}

	private static Vector3 FireFixedStepForTime(in ProjectileParams p, Vector3 velocity, float step, float seconds)
	{
		var position = Vector3.Zero;
		int steps = (int)MathF.Round(seconds / step);
		for (int i = 0; i < steps; i++)
		{
			Ballistics.Integrate(step, 1, ref position, ref velocity, p);
		}
		return position;
	}
}
