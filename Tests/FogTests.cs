using Gdpyr.Sim;
using Xunit;

namespace Gdpyr.Tests;

/// <summary>
/// What a client makes of the records it was not sent (docs/NETCODE.md §6.2).
/// There is no "you have lost contact" message, so these two functions are the
/// whole of the inference, and getting the timeout wrong would either flicker
/// every player on a lag spike or leave a flanker on screen.
/// </summary>
public class FogTests
{
	[Fact]
	public void ARecordInTheNewestPacketIsNotLost()
	{
		Assert.False(Fog.IsLost(0f));

		// A record cannot be newer than the packet that carried it, but a datagram
		// arriving out of order can make it look that way for a frame.
		Assert.False(Fog.IsLost(-2f));
	}

	[Fact]
	public void TheTimeoutCoversReorderingAndNothingElse()
	{
		// It is measured against the newest packet the client decoded, not against a
		// clock: a lost datagram takes the reference with it rather than aging
		// anything, so this has no latency budget to cover (docs/NETCODE.md §6.2).
		Assert.False(Fog.IsLost(SimConfig.SnapshotIntervalTicks));
		Assert.False(Fog.IsLost(SimConfig.FogContactTimeoutTicks));
		Assert.True(Fog.IsLost(SimConfig.FogContactTimeoutTicks + 1f));

		Assert.True(SimConfig.FogContactTimeoutTicks >= SimConfig.SnapshotIntervalTicks * 2,
			"a record two snapshots behind the newest packet is reordering, not the fog");
	}

	[Fact]
	public void AContactStillHeldIsDrawnSolid()
	{
		Assert.Equal(1f, Fog.GhostAlpha(0f));
		Assert.Equal(1f, Fog.GhostAlpha(SimConfig.FogContactTimeoutTicks));
	}

	[Fact]
	public void AGhostFadesOverItsLifetime()
	{
		float justLost = Fog.GhostAlpha(SimConfig.FogContactTimeoutTicks + 1f);
		float halfWay = Fog.GhostAlpha(SimConfig.FogContactTimeoutTicks + (SimConfig.GhostLifetimeTicks / 2f));

		Assert.True(justLost > halfWay);
		Assert.Equal(0.5f, halfWay, 2);
	}

	[Fact]
	public void FadingIsMonotonic()
	{
		float previous = 1f;
		for (float age = 0f; age < SimConfig.GhostLifetimeTicks * 2f; age += 3f)
		{
			float alpha = Fog.GhostAlpha(age);
			Assert.True(alpha <= previous, $"alpha rose again at age {age}");
			Assert.InRange(alpha, 0f, 1f);
			previous = alpha;
		}
	}

	[Fact]
	public void AGhostIsForgottenOnceItHasFaded()
	{
		float lifetime = SimConfig.FogContactTimeoutTicks + SimConfig.GhostLifetimeTicks;

		Assert.False(Fog.IsForgotten(lifetime - 1f));
		Assert.True(Fog.IsForgotten(lifetime));
		Assert.True(Fog.IsForgotten(lifetime + SimConfig.TickRate));
	}

	[Fact]
	public void SomethingNeverSeenIsAlreadyForgotten()
	{
		// What a client reports for a peer no record has ever arrived for.
		Assert.True(Fog.IsLost(float.MaxValue));
		Assert.Equal(0f, Fog.GhostAlpha(float.MaxValue));
		Assert.True(Fog.IsForgotten(float.MaxValue));
	}

	[Fact]
	public void TheRefreshRateIsInsideTheRangeTheDesignAsksFor()
	{
		// 5–10 Hz (docs/IMPLEMENTATION_PLAN.md §M4).
		float hertz = SimConfig.TickRate / (float)SimConfig.FogRefreshIntervalTicks;

		Assert.InRange(hertz, 5f, 10f);
	}

	[Fact]
	public void AContactTheAuthorityStillHoldsReadsAsFresh()
	{
		// The other route to an age: on a listen host the fog is read straight off
		// VisibilityService, which reports 0 while a contact is held and ticks since
		// it was last seen afterwards. A held contact must not read as lost even
		// though the refresh only runs every FogRefreshIntervalTicks, and a contact
		// dropped at a refresh must (docs/NETCODE.md §6.2).
		Assert.False(Fog.IsLost(0f));
		Assert.True(Fog.IsLost(SimConfig.FogRefreshIntervalTicks));
	}
}
