using Gdpyr.Sim;
using Xunit;

namespace Gdpyr.Tests;

/// <summary>
/// The economy's rules (docs/IMPLEMENTATION_PLAN.md §M5). Engine-free on purpose:
/// "how long does a node take to flip, and what does it pay" is worth answering
/// from a test rather than from a twenty-minute round.
/// </summary>
public class CaptureTests
{
	private const int Interval = SimConfig.CaptureScanIntervalTicks;
	private const int CaptureTicks = 60;
	private const int IncomeInterval = 32;
	private const int IncomePoints = 25;

	private static CaptureRules Rules() => new(CaptureTicks, Interval, IncomeInterval, IncomePoints);

	/// <summary>Runs <paramref name="scans"/> occupancy scans and returns what was paid.</summary>
	private static int Run(CaptureState state, int ground, int strategist, int scans, ref uint tick)
	{
		int paid = 0;
		for (int i = 0; i < scans; i++)
		{
			tick += Interval;
			paid += state.Tick(tick, ground, strategist, Rules());
		}
		return paid;
	}

	[Fact]
	public void ANodeStartsOwnedByNobody()
	{
		var state = new CaptureState();

		Assert.Equal(NodeHolder.Neutral, state.Owner);
		Assert.Equal(NodeHolder.Neutral, state.Claimant);
		Assert.False(state.Contested);
		Assert.Equal(0f, state.Progress(Rules()));
		Assert.False(state.IsHeldBy(NodeHolder.Neutral));
	}

	[Fact]
	public void StandingOnANodeAloneTakesIt()
	{
		var state = new CaptureState();
		uint tick = 0;

		Run(state, ground: 0, strategist: 1, scans: (CaptureTicks / Interval) - 1, ref tick);
		Assert.Equal(NodeHolder.Neutral, state.Owner);
		Assert.Equal(NodeHolder.Strategist, state.Claimant);

		Run(state, ground: 0, strategist: 1, scans: 1, ref tick);
		Assert.Equal(NodeHolder.Strategist, state.Owner);
		Assert.True(state.IsHeldBy(NodeHolder.Strategist));

		// The meter is spent on the flip rather than left full: the next thing it
		// measures is somebody taking it back.
		Assert.Equal(0f, state.Progress(Rules()));
		Assert.Equal(NodeHolder.Neutral, state.Claimant);
	}

	[Fact]
	public void MoreBodiesDoNotCaptureFaster()
	{
		var one = new CaptureState();
		var many = new CaptureState();
		uint a = 0;
		uint b = 0;

		Run(one, ground: 1, strategist: 0, scans: 3, ref a);
		Run(many, ground: 9, strategist: 0, scans: 3, ref b);

		Assert.Equal(one.Progress(Rules()), many.Progress(Rules()));
	}

	[Fact]
	public void BothSidesAtOnceFreezesTheMeterAndPaysNothing()
	{
		var state = new CaptureState();
		uint tick = 0;

		Run(state, ground: 0, strategist: 1, scans: 3, ref tick);
		float held = state.Progress(Rules());

		int paid = Run(state, ground: 1, strategist: 1, scans: 20, ref tick);

		Assert.True(state.Contested);
		Assert.Equal(held, state.Progress(Rules()));
		Assert.Equal(0, paid);
		Assert.Equal(NodeHolder.Neutral, state.Owner);
	}

	[Fact]
	public void AContestedNodeResumesWhereItGotTo()
	{
		var state = new CaptureState();
		uint tick = 0;

		Run(state, ground: 0, strategist: 1, scans: 5, ref tick);
		Run(state, ground: 2, strategist: 1, scans: 5, ref tick);
		Run(state, ground: 0, strategist: 1, scans: (CaptureTicks / Interval) - 5, ref tick);

		Assert.False(state.Contested);
		Assert.Equal(NodeHolder.Strategist, state.Owner);
	}

	[Fact]
	public void AnOwnerStandingOnItsOwnNodeUndoesTheOtherSidesProgress()
	{
		var state = new CaptureState();
		uint tick = 0;

		Run(state, ground: 0, strategist: 1, scans: CaptureTicks / Interval, ref tick);
		Run(state, ground: 1, strategist: 0, scans: 4, ref tick);
		Assert.True(state.Progress(Rules()) > 0f);

		Run(state, ground: 0, strategist: 1, scans: 4, ref tick);

		Assert.Equal(0f, state.Progress(Rules()));
		Assert.Equal(NodeHolder.Neutral, state.Claimant);
		Assert.Equal(NodeHolder.Strategist, state.Owner);
	}

	[Fact]
	public void AnEmptyNodeForgetsAPartialCapture()
	{
		var state = new CaptureState();
		uint tick = 0;

		Run(state, ground: 1, strategist: 0, scans: 5, ref tick);
		Run(state, ground: 0, strategist: 0, scans: 5, ref tick);

		Assert.Equal(0f, state.Progress(Rules()));
		Assert.Equal(NodeHolder.Neutral, state.Claimant);
	}

	[Fact]
	public void AHeldNodePaysOnItsOwnClockAndNotOnTheFlip()
	{
		var state = new CaptureState();
		uint tick = 0;

		int duringCapture = Run(state, ground: 0, strategist: 1, scans: CaptureTicks / Interval, ref tick);
		Assert.Equal(0, duringCapture);

		// One interval's worth of scans pays exactly once, however many scans that is.
		int paid = Run(state, ground: 0, strategist: 1, scans: IncomeInterval / Interval, ref tick);

		Assert.Equal(IncomePoints, paid);
		Assert.Equal(IncomePoints, state.Paid);
	}

	[Fact]
	public void OneRiflemanStandingOnItStopsThePayments()
	{
		var state = new CaptureState();
		uint tick = 0;

		Run(state, ground: 0, strategist: 1, scans: CaptureTicks / Interval, ref tick);
		Run(state, ground: 0, strategist: 1, scans: IncomeInterval / Interval, ref tick);

		// Alone, so not even contested — and it still pays nothing, which is the
		// whole argument for walking out there.
		int denied = Run(state, ground: 1, strategist: 0, scans: (CaptureTicks / Interval) - 1, ref tick);

		Assert.Equal(0, denied);
		Assert.Equal(NodeHolder.Strategist, state.Owner);
		Assert.Equal(IncomePoints, state.Paid);
	}

	[Fact]
	public void ANodeThatChangesHandsPaysItsNewOwnerNothingForTheInterval()
	{
		var state = new CaptureState();
		uint tick = 0;

		// The ground force takes it, holds it a while, then loses it back.
		Run(state, ground: 1, strategist: 0, scans: CaptureTicks / Interval, ref tick);
		Run(state, ground: 1, strategist: 0, scans: 20, ref tick);

		int paid = Run(state, ground: 0, strategist: 1, scans: CaptureTicks / Interval, ref tick);
		Assert.Equal(0, paid);
		Assert.Equal(NodeHolder.Strategist, state.Owner);

		// And the first payment is a full interval after the flip, not immediately.
		Assert.Equal(0, Run(state, ground: 0, strategist: 1, scans: (IncomeInterval / Interval) - 1, ref tick));
		Assert.Equal(IncomePoints, Run(state, ground: 0, strategist: 1, scans: 1, ref tick));
	}

	[Fact]
	public void AReplicatedStateIsWhatTheClientDraws()
	{
		var state = new CaptureState();

		state.Apply(NodeHolder.GroundForce, NodeHolder.Strategist, contested: true, progress: 0.5f, Rules());

		Assert.Equal(NodeHolder.GroundForce, state.Owner);
		Assert.Equal(NodeHolder.Strategist, state.Claimant);
		Assert.True(state.Contested);
		Assert.Equal(0.5f, state.Progress(Rules()), 2);
	}

	[Fact]
	public void ResettingHandsTheNodeBackToNobody()
	{
		var state = new CaptureState();
		uint tick = 0;
		Run(state, ground: 0, strategist: 1, scans: CaptureTicks / Interval, ref tick);
		Run(state, ground: 0, strategist: 1, scans: IncomeInterval / Interval, ref tick);

		state.Reset();

		Assert.Equal(NodeHolder.Neutral, state.Owner);
		Assert.Equal(0, state.Paid);
		Assert.Equal(0f, state.Progress(Rules()));
	}

	[Fact]
	public void TheRulesReportWhatANodeIsWorthPerMinute()
	{
		var rules = new CaptureRules(CaptureTicks, Interval, SimConfig.TickRate * 5, 25);

		Assert.Equal(300f, rules.PointsPerMinute, 1);
	}
}
