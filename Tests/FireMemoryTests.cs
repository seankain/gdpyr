using System;
using Gdpyr.Sim;
using Godot;
using Xunit;

namespace Gdpyr.Tests;

/// <summary>
/// Being fired upon (docs/TACTICS.md §4.1): the window rounds keep open, the worst
/// kind in it, the latest shooter, and the error a unit makes about where a shot
/// came from — along the line and never off it, at most a tenth of the range, the
/// same every run.
/// </summary>
public class FireMemoryTests
{
	private const int Shooter = 3;

	private static readonly Vector3 Eye = new(0f, 1.5f, 0f);
	private static readonly Vector3 Muzzle = new(0f, 1.5f, 60f);

	private static FireMemory FiredAt(uint tick, FireKind kind = FireKind.NearMiss)
	{
		var memory = default(FireMemory);
		FiredUpon.Record(ref memory, kind, Muzzle, Shooter, tick);
		return memory;
	}

	[Fact]
	public void ARound_OpensAWindowOfThreeSeconds()
	{
		FireMemory memory = FiredAt(1000);

		Assert.True(memory.IsSet);
		Assert.Equal(FireKind.NearMiss, FiredUpon.KindAt(memory, 1000));
		Assert.Equal(FireKind.NearMiss, FiredUpon.KindAt(memory, 1000 + FiredUpon.WindowTicks));
		Assert.Equal(FireKind.None, FiredUpon.KindAt(memory, 1000 + FiredUpon.WindowTicks + 1));
		Assert.Equal(SimConfig.TickRate * 3, FiredUpon.WindowTicks);
	}

	[Fact]
	public void RoundsWithinTheWindow_KeepItOpen_AndCount()
	{
		FireMemory memory = FiredAt(1000);
		FiredUpon.Record(ref memory, FireKind.NearMiss, Muzzle, Shooter, 1000 + FiredUpon.WindowTicks);

		Assert.Equal(2, memory.Rounds);
		Assert.True(FiredUpon.IsRecent(memory, 1000 + (2 * FiredUpon.WindowTicks)));
	}

	[Fact]
	public void AHit_OutranksANearMiss_UntilTheWindowCloses()
	{
		FireMemory memory = FiredAt(1000, FireKind.Hit);
		FiredUpon.Record(ref memory, FireKind.NearMiss, Muzzle, Shooter, 1010);

		Assert.Equal(FireKind.Hit, FiredUpon.KindAt(memory, 1010));

		// After it closed, a near miss is a new window and only a near miss.
		FiredUpon.Record(ref memory, FireKind.NearMiss, Muzzle, Shooter, 1010 + FiredUpon.WindowTicks + 1);
		Assert.Equal(FireKind.NearMiss, memory.Kind);
		Assert.Equal(1, memory.Rounds);
	}

	[Fact]
	public void TheLatestRound_SaysWhereAndWhose()
	{
		FireMemory memory = FiredAt(1000);
		var elsewhere = new Vector3(20f, 1.5f, 40f);
		FiredUpon.Record(ref memory, FireKind.NearMiss, elsewhere, 9, 1005);

		Assert.Equal(elsewhere, memory.Origin);
		Assert.Equal(9, memory.ShooterOwnerId);
		Assert.Equal(1005u, memory.LastTick);
	}

	[Fact]
	public void NothingRecorded_IsNotARound()
	{
		var memory = default(FireMemory);
		FiredUpon.Record(ref memory, FireKind.None, Muzzle, Shooter, 1000);

		Assert.False(memory.IsSet);
		Assert.Equal(FireKind.None, FiredUpon.KindAt(memory, 1000));
	}

	[Fact]
	public void AMemoryFromALaterTick_IsNotRecent()
	{
		// A new round restarts the clock: what was remembered from the last one is not "now".
		Assert.False(FiredUpon.IsRecent(FiredAt(5000), 100));
	}

	[Fact]
	public void TheRoundCount_Saturates()
	{
		var memory = default(FireMemory);
		for (int i = 0; i < ushort.MaxValue + 10; i++)
		{
			FiredUpon.Record(ref memory, FireKind.NearMiss, Muzzle, Shooter, 1000);
		}

		Assert.Equal(ushort.MaxValue, memory.Rounds);
	}

	[Fact]
	public void Clearing_ForgetsIt()
	{
		FireMemory memory = FiredAt(1000);
		FiredUpon.Clear(ref memory);

		Assert.False(memory.IsSet);
	}

	[Fact]
	public void TheErrorIsAlongTheLine_AndWithinATenthOfTheRange()
	{
		float range = Eye.DistanceTo(Muzzle);
		float worst = 0f;
		bool nearer = false;
		bool further = false;
		for (uint round = 1; round <= 500; round++)
		{
			uint seed = FiredUpon.Seed(OwnerId.ForUnit(4), new FireSource(Muzzle, Shooter, 3, round));
			Vector3 perceived = FiredUpon.Perceive(Eye, Muzzle, FiredUpon.ErrorFraction, seed);

			// The direction is exact: the perceived point is on the line from the eye.
			Vector3 line = (Muzzle - Eye).Normalized();
			Vector3 seen = perceived - Eye;
			Assert.True(seen.Normalized().DistanceTo(line) < 1e-4f);

			float error = seen.Length() - range;
			worst = MathF.Max(worst, MathF.Abs(error));
			nearer |= error < -1f;
			further |= error > 1f;
		}

		Assert.True(worst <= (range * FiredUpon.ErrorFraction) + 1e-3f);
		Assert.True(worst > range * FiredUpon.ErrorFraction * 0.8f, $"the error never came near its bound: {worst}");
		Assert.True(nearer && further, "the error goes both ways");
	}

	[Fact]
	public void TheError_IsTheSameEveryRun_AndDiffersByUnitAndRound()
	{
		var source = new FireSource(Muzzle, Shooter, 3, 77);
		Vector3 a = FiredUpon.Perceive(Eye, Muzzle, FiredUpon.ErrorFraction, FiredUpon.Seed(OwnerId.ForUnit(4), source));
		Vector3 b = FiredUpon.Perceive(Eye, Muzzle, FiredUpon.ErrorFraction, FiredUpon.Seed(OwnerId.ForUnit(4), source));
		Vector3 otherUnit = FiredUpon.Perceive(Eye, Muzzle, FiredUpon.ErrorFraction,
			FiredUpon.Seed(OwnerId.ForUnit(5), source));
		Vector3 otherRound = FiredUpon.Perceive(Eye, Muzzle, FiredUpon.ErrorFraction,
			FiredUpon.Seed(OwnerId.ForUnit(4), new FireSource(Muzzle, Shooter, 3, 78)));

		Assert.Equal(a, b);
		Assert.NotEqual(a, otherUnit);
		Assert.NotEqual(a, otherRound);
	}

	[Fact]
	public void NoErrorOrNoRange_IsTheTrueOrigin()
	{
		Assert.Equal(Muzzle, FiredUpon.Perceive(Eye, Muzzle, 0f, 12345u));
		Assert.Equal(Eye, FiredUpon.Perceive(Eye, Eye, FiredUpon.ErrorFraction, 12345u));
	}

	[Fact]
	public void SignedNoise_StaysInItsInterval()
	{
		for (uint seed = 0; seed < 10_000; seed++)
		{
			float u = Spread.Signed(seed);
			Assert.InRange(u, -1f, 1f);
			Assert.True(u < 1f);
		}
	}

	[Fact]
	public void RecordingAndReading_AllocateNothing()
	{
		Run();
		long before = GC.GetAllocatedBytesForCurrentThread();
		Run();
		Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);

		static void Run()
		{
			var memory = default(FireMemory);
			for (uint t = 1; t < 5_000; t++)
			{
				var source = new FireSource(Muzzle, Shooter, 3, t);
				Vector3 perceived = FiredUpon.Perceive(Eye, Muzzle, FiredUpon.ErrorFraction,
					FiredUpon.Seed(OwnerId.ForUnit(4), source));
				FiredUpon.Record(ref memory, (t % 7) == 0 ? FireKind.Hit : FireKind.NearMiss, perceived, Shooter, t * 3);
				FiredUpon.KindAt(memory, t * 3);
			}
		}
	}
}
