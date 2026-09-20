using Gdpyr.Sim;
using Godot;
using Xunit;

namespace Gdpyr.Tests;

public class SnapshotInterpolatorTests
{
	private static SnapshotInterpolator WithLine(uint firstTick, int count, int step = 2)
	{
		// A remote player walking +X at 1 m per snapshot, sampled every `step` ticks.
		var interpolator = new SnapshotInterpolator();
		for (int i = 0; i < count; i++)
		{
			interpolator.Push((uint)(firstTick + (i * step)), new Vector3(i, 0f, 0f), 0f, 0f);
		}
		return interpolator;
	}

	[Fact]
	public void Sample_FailsBeforeAnythingArrives()
	{
		Assert.False(new SnapshotInterpolator().TrySample(10f, out _, out _, out _));
	}

	[Fact]
	public void Sample_InterpolatesBetweenTheBracketingSnapshots()
	{
		var interpolator = WithLine(100, 3);

		Assert.True(interpolator.TrySample(101f, out Vector3 position, out _, out _));
		Assert.Equal(0.5f, position.X, precision: 4);

		Assert.True(interpolator.TrySample(103f, out position, out _, out _));
		Assert.Equal(1.5f, position.X, precision: 4);
	}

	[Fact]
	public void Sample_TakesTheShortWayRoundOnYaw()
	{
		// Interpolating 350° -> 10° the long way spins a remote player through a
		// full turn every time they cross north.
		var interpolator = new SnapshotInterpolator();
		interpolator.Push(10, Vector3.Zero, Mathf.DegToRad(350f), 0f);
		interpolator.Push(12, Vector3.Zero, Mathf.DegToRad(10f), 0f);

		Assert.True(interpolator.TrySample(11f, out _, out float yaw, out _));
		float degrees = Mathf.RadToDeg(yaw);
		if (degrees > 180f)
		{
			degrees -= 360f;
		}
		Assert.Equal(0f, degrees, precision: 2);
	}

	[Fact]
	public void Sample_HoldsTheOldestWhenTheRenderClockIsBehindTheBuffer()
	{
		var interpolator = WithLine(100, 3);
		Assert.True(interpolator.TrySample(50f, out Vector3 position, out _, out _));
		Assert.Equal(0f, position.X, precision: 4);
	}

	[Fact]
	public void Sample_ExtrapolatesBriefly_ThenFreezes()
	{
		var interpolator = WithLine(100, 3); // newest tick 104 at x = 2, 0.5 m/tick

		Assert.True(interpolator.TrySample(105f, out Vector3 oneAhead, out _, out _));
		Assert.Equal(2.5f, oneAhead.X, precision: 4);

		// Past the extrapolation limit the position stops moving rather than
		// sliding a lost player through the map (docs/NETCODE.md §3.3).
		Assert.True(interpolator.TrySample(106f, out Vector3 atLimit, out _, out _));
		Assert.True(interpolator.TrySample(200f, out Vector3 farPast, out _, out _));
		Assert.Equal(atLimit.X, farPast.X, precision: 4);
		Assert.Equal(2f + SimConfig.MaxExtrapolationTicks * 0.5f, farPast.X, precision: 4);
	}

	[Fact]
	public void Push_AcceptsAnOutOfOrderSnapshotStillInsideTheWindow()
	{
		var interpolator = new SnapshotInterpolator();
		interpolator.Push(100, new Vector3(0f, 0f, 0f), 0f, 0f);
		interpolator.Push(104, new Vector3(2f, 0f, 0f), 0f, 0f);
		interpolator.Push(102, new Vector3(1f, 0f, 0f), 0f, 0f); // reordered by the network

		Assert.Equal(3, interpolator.Count);
		Assert.True(interpolator.TrySample(103f, out Vector3 position, out _, out _));
		Assert.Equal(1.5f, position.X, precision: 4);
	}

	[Fact]
	public void Push_IgnoresARetransmittedSnapshot()
	{
		var interpolator = new SnapshotInterpolator();
		interpolator.Push(100, Vector3.Zero, 0f, 0f);
		interpolator.Push(100, Vector3.One, 0f, 0f);

		Assert.Equal(1, interpolator.Count);
		Assert.True(interpolator.TrySample(100f, out Vector3 position, out _, out _));
		Assert.Equal(Vector3.Zero, position);
	}

	[Fact]
	public void Buffer_KeepsTheNewestWhenItOverflows()
	{
		var interpolator = WithLine(0, SimConfig.SnapshotBufferTicks + 10);

		Assert.Equal(SimConfig.SnapshotBufferTicks, interpolator.Count);
		Assert.Equal((uint)((SimConfig.SnapshotBufferTicks + 9) * 2), interpolator.NewestTick);
	}

	[Fact]
	public void Push_DropsASnapshotOlderThanTheWholeBuffer()
	{
		var interpolator = WithLine(1000, SimConfig.SnapshotBufferTicks);
		interpolator.Push(1, Vector3.Zero, 0f, 0f);

		Assert.Equal(1, interpolator.Stale);
		Assert.Equal(1000u, interpolator.OldestTick);
	}

	[Fact]
	public void Sample_IsMonotonicAcrossASteadyStream()
	{
		// 30 Hz snapshots consumed by a 60 Hz render clock: every step must move
		// forward, which is the whole point of rendering behind the server.
		var interpolator = WithLine(100, 20);
		float previous = float.NegativeInfinity;

		for (float renderTick = 100f; renderTick <= 138f; renderTick += 1f)
		{
			Assert.True(interpolator.TrySample(renderTick, out Vector3 position, out _, out _));
			Assert.True(position.X >= previous);
			previous = position.X;
		}
	}
}
