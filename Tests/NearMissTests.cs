using System;
using Gdpyr.Sim;
using Godot;
using Xunit;

namespace Gdpyr.Tests;

/// <summary>
/// Near misses (docs/TACTICS.md §4.1): a round's chord passing within 1.5 m of a
/// body's axis, a blast reaching its surface, and the box test in front of both.
/// </summary>
public class NearMissTests
{
	/// <summary>A rifleman standing at the origin: 1.8 m tall, 0.4 m round.</summary>
	private static readonly HitCapsule Body = HitCapsule.FromFeet(Vector3.Zero, 1.8f, 0.4f);

	/// <summary>A chord across the body's front, at chest height, <paramref name="offset"/> metres to its side.</summary>
	private static bool Across(float offset, float height = 1f) =>
		NearMiss.Passes(Body, new Vector3(offset, height, -20f), new Vector3(offset, height, 20f));

	[Theory]
	[InlineData(0.5f, true)]
	[InlineData(1.0f, true)]
	[InlineData(1.49f, true)]
	[InlineData(1.51f, false)]
	[InlineData(3f, false)]
	[InlineData(-1.4f, true)]
	public void ARoundPassingBeside_IsANearMissWithinOneAndAHalfMetresOfTheAxis(float offset, bool near)
	{
		Assert.Equal(near, Across(offset));
	}

	[Fact]
	public void ARoundOverTheHead_CountsFromTheTopOfTheAxis()
	{
		// The axis runs from 0.4 m to 1.4 m; a round 1.4 m above its top passes within 1.5 m.
		Assert.True(Across(0f, height: 2.8f));
		Assert.False(Across(0f, height: 3f));
	}

	[Fact]
	public void ARoundThatStopsShort_IsNoNearMiss()
	{
		// The chord is the round's up to what it hit: one that hit a wall 5 m short went by nobody.
		Assert.False(NearMiss.Passes(Body, new Vector3(0.5f, 1f, -20f), new Vector3(0.5f, 1f, -5f)));
	}

	[Fact]
	public void ARoundThroughTheBody_PassesWithinIt_SoTheCallerLeavesTheVictimOut()
	{
		Assert.True(Across(0f));
	}

	[Fact]
	public void ANonPositiveDistance_PassesNothing()
	{
		Assert.False(NearMiss.Passes(Body, new Vector3(0f, 1f, -20f), new Vector3(0f, 1f, 20f), meters: 0f));
	}

	[Fact]
	public void TheBroadPhase_TurnsAwayARoundOnTheOtherSideOfTheMap()
	{
		Assert.False(NearMiss.Passes(Body, new Vector3(200f, 1f, -20f), new Vector3(200f, 1f, 20f)));
		Assert.False(NearMiss.Passes(Body, new Vector3(-5f, 30f, -5f), new Vector3(5f, 30f, 5f)));
	}

	[Theory]
	[InlineData(7f, 7.3f, true)]
	[InlineData(7f, 7.5f, false)]
	[InlineData(0f, 0.1f, false)]
	public void ABlast_ReachesTheSurface_AsItsDamageIsMeasured(float radius, float distance, bool reaches)
	{
		// From the axis: the surface is 0.4 m nearer, as CombatManager.Explode measures.
		Assert.Equal(reaches, NearMiss.InReach(Body, new Vector3(distance, 1f, 0f), radius));
	}

	[Fact]
	public void TheChordsBox_NeverTurnsAwayABodyTheExactTestWouldKeep()
	{
		// The pass over the field asks the box first and builds a capsule only inside it.
		var random = new Random(5);
		int kept = 0;
		for (int i = 0; i < 20_000; i++)
		{
			var feet = new Vector3(Next(random, 20f), Next(random, 1f), Next(random, 20f));
			var body = HitCapsule.FromFeet(feet, 1.8f, 0.4f);
			var from = new Vector3(Next(random, 25f), 1f + Next(random, 2f), Next(random, 25f));
			var to = from + new Vector3(Next(random, 16f), Next(random, 1f), Next(random, 16f));

			bool passes = NearMiss.Passes(body, from, to);
			kept += passes ? 1 : 0;
			Assert.True(!passes || new NearMiss.Chord(from, to).MayPass(feet, 1.8f));
		}

		Assert.True(kept > 100, $"the sample reached too few bodies to say anything: {kept}");

		static float Next(Random random, float half) => (float)((random.NextDouble() * 2.0) - 1.0) * half;
	}

	[Fact]
	public void TheChordsBox_TurnsAwayABodyFarFromIt()
	{
		var chord = new NearMiss.Chord(new Vector3(0f, 1f, -20f), new Vector3(0f, 1f, 20f));
		Assert.True(chord.MayPass(new Vector3(1.4f, 0f, 0f), 1.8f));
		Assert.False(chord.MayPass(new Vector3(1.6f, 0f, 0f), 1.8f));
		Assert.False(chord.MayPass(new Vector3(0f, 0f, 22f), 1.8f));
		Assert.False(chord.MayPass(new Vector3(0f, 10f, 0f), 1.8f));
	}

	[Fact]
	public void ItAllocatesNothing()
	{
		Run();
		long before = GC.GetAllocatedBytesForCurrentThread();
		Run();
		Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);

		static void Run()
		{
			int near = 0;
			for (int i = 0; i < 10_000; i++)
			{
				float x = (i % 400) * 0.01f;
				near += NearMiss.Passes(Body, new Vector3(x, 1f, -20f), new Vector3(x, 1f, 20f)) ? 1 : 0;
				near += NearMiss.InReach(Body, new Vector3(x, 1f, 0f), 2f) ? 1 : 0;
			}

			Assert.True(near > 0);
		}
	}
}
