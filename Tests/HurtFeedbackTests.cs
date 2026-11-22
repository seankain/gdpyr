using Gdpyr.Sim;
using Xunit;

namespace Gdpyr.Tests;

/// <summary>
/// The engine-free half of the hurt feedback (docs/HURT_FEEDBACK.md): how grievous a hit was,
/// how hits close together add up, how long the tunnel vision lasts, and the camera shake's
/// trauma. What either effect looks like is its scene's, edited in Godot, and checked by
/// <c>scripts/hurt-feedback-checks.sh</c>.
/// </summary>
public class HurtFeedbackTests
{
	private const float Dt = 1f / 60f;

	// ---- severity ------------------------------------------------------------------------

	[Fact]
	public void Severity_IsTheShareOfALifeTheHitTook()
	{
		Assert.Equal(0.2f, HurtFeedback.Severity(20, 0, killed: false), 4);
		Assert.Equal(0.6f, HurtFeedback.Severity(60, 0, killed: false), 4);
		Assert.Equal(1f, HurtFeedback.Severity(100, 0, killed: false), 4);
	}

	[Fact]
	public void Severity_RisesWithTheWeapon()
	{
		// The rifle's 20, the HMG's 45, the DMR's 60 (Weapons/projectiles/).
		float rifle = HurtFeedback.Severity(20, 0, killed: false);
		float hmg = HurtFeedback.Severity(45, 0, killed: false);
		float dmr = HurtFeedback.Severity(60, 0, killed: false);

		Assert.True(rifle < hmg && hmg < dmr, $"{rifle} < {hmg} < {dmr}");
	}

	[Fact]
	public void AKillingBlow_IsTheWorstThereIs_HoweverLittleItTook()
	{
		Assert.Equal(1f, HurtFeedback.Severity(5, 0, killed: true));
		Assert.Equal(1f, HurtFeedback.Severity(0, 0, killed: true));
		Assert.True(HurtFeedback.Severity(99, 0, killed: false) < HurtFeedback.Severity(5, 0, killed: true));
	}

	[Fact]
	public void AVest_SoftensAHit_ButAHitItSoakedIsStillFelt()
	{
		// A rifle round on a medium vest: 50 % of 20 to each.
		float vested = HurtFeedback.Severity(10, 10, killed: false);
		float bare = HurtFeedback.Severity(20, 0, killed: false);
		float soaked = HurtFeedback.Severity(0, 20, killed: false);

		Assert.True(vested < bare, $"{vested} < {bare}");
		Assert.True(soaked > 0f && soaked < vested, $"0 < {soaked} < {vested}");
		Assert.Equal(20f * HurtFeedback.VestWeight / 100f, soaked, 4);
	}

	[Fact]
	public void NothingLost_IsNothingFelt_AndRisesAreNotHits()
	{
		Assert.Equal(0f, HurtFeedback.Severity(0, 0, killed: false));

		// A locker's resupply in the same record as a hit: the vest's points went up.
		Assert.Equal(0.2f, HurtFeedback.Severity(20, -15, killed: false), 4);
		Assert.Equal(0f, HurtFeedback.Severity(-10, -15, killed: false));
	}

	[Fact]
	public void Severity_StaysInItsRange()
	{
		Assert.Equal(1f, HurtFeedback.Severity(800, 100, killed: false));
		Assert.Equal(0f, HurtFeedback.Severity(20, 0, killed: false, maxHealth: 0));
		Assert.Equal(0.5f, HurtFeedback.Severity(20, 0, killed: false, maxHealth: 40), 4);
	}

	// ---- stacking ------------------------------------------------------------------------

	[Fact]
	public void Stack_TwoHits_DeeperThanEither()
	{
		float stacked = HurtFeedback.Stack(0.3f, 0.3f, 0.5f);

		Assert.Equal(0.45f, stacked, 4);
		Assert.True(stacked > 0.3f);
	}

	[Fact]
	public void Stack_NeverShallowerThanItWas_OrThanTheHit()
	{
		Assert.True(HurtFeedback.Stack(0.8f, 0.1f, 0f) >= 0.8f);
		Assert.True(HurtFeedback.Stack(0.1f, 0.8f, 0f) >= 0.8f);
		Assert.Equal(0.8f, HurtFeedback.Stack(0f, 0.8f, 0.5f), 4);
		Assert.Equal(0.8f, HurtFeedback.Stack(0.8f, 0f, 0.5f), 4);
	}

	[Fact]
	public void Stack_StaysInItsRange()
	{
		Assert.Equal(1f, HurtFeedback.Stack(0.9f, 0.9f, 1f));
		Assert.Equal(1f, HurtFeedback.Stack(2f, 2f, 2f));
		Assert.Equal(0f, HurtFeedback.Stack(-1f, -1f, 0.5f));
	}

	[Fact]
	public void Stack_NoStacking_IsTheDeeperOfTheTwo()
	{
		Assert.Equal(0.6f, HurtFeedback.Stack(0.4f, 0.6f, 0f), 4);
	}

	// ---- duration ------------------------------------------------------------------------

	[Fact]
	public void Seconds_DeeperLastsLonger()
	{
		Assert.Equal(0.8f, HurtFeedback.Seconds(0f, 0.8f, 3f), 4);
		Assert.Equal(3f, HurtFeedback.Seconds(1f, 0.8f, 3f), 4);
		Assert.True(HurtFeedback.Seconds(0.3f, 0.8f, 3f) < HurtFeedback.Seconds(0.7f, 0.8f, 3f));
		Assert.Equal(3f, HurtFeedback.Seconds(4f, 0.8f, 3f), 4);
		Assert.Equal(0f, HurtFeedback.Seconds(0.5f, -2f, -1f));
	}

	// ---- the pulse -----------------------------------------------------------------------

	[Fact]
	public void Pulse_RunsItsTimeAndEnds()
	{
		var pulse = new HurtPulse();
		Assert.False(pulse.IsActive);

		pulse.Start(0.6f, 1f);
		Assert.True(pulse.IsActive);
		Assert.Equal(0f, pulse.Progress);

		for (int i = 0; i < 30; i++)
		{
			pulse.Advance(Dt);
		}

		Assert.True(pulse.IsActive);
		Assert.Equal(0.5f, pulse.Progress, 3);
		Assert.Equal(0.6f, pulse.Peak);

		for (int i = 0; i < 31; i++)
		{
			pulse.Advance(Dt);
		}

		Assert.False(pulse.IsActive);
		Assert.Equal(1f, pulse.Progress);
	}

	[Fact]
	public void Pulse_StartingAgain_BeginsFromTheTop()
	{
		var pulse = new HurtPulse();
		pulse.Start(0.3f, 1f);
		pulse.Advance(0.7f);

		pulse.Start(0.5f, 2f);

		Assert.Equal(0f, pulse.Progress);
		Assert.Equal(0.5f, pulse.Peak);
		Assert.Equal(2f, pulse.Seconds);
	}

	[Fact]
	public void Pulse_NothingToShow_IsNotActive()
	{
		var none = new HurtPulse();
		none.Start(0f, 2f);
		Assert.False(none.IsActive);

		var instant = new HurtPulse();
		instant.Start(0.5f, 0f);
		Assert.False(instant.IsActive);
		Assert.Equal(1f, instant.Progress);

		var cleared = new HurtPulse();
		cleared.Start(0.5f, 2f);
		cleared.Clear();
		Assert.False(cleared.IsActive);
	}

	[Fact]
	public void Pulse_TimeDoesNotRunBackwards()
	{
		var pulse = new HurtPulse();
		pulse.Start(0.5f, 1f);
		pulse.Advance(0.25f);
		pulse.Advance(-1f);

		Assert.Equal(0.25f, pulse.Progress, 4);
	}

	// ---- the shake's trauma --------------------------------------------------------------

	[Fact]
	public void Trauma_AddsUpToOne_AndDrainsToNothing()
	{
		var trauma = new ShakeTrauma();
		trauma.Add(0.4f);
		trauma.Add(0.4f);
		Assert.Equal(0.8f, trauma.Value, 4);

		trauma.Add(0.4f);
		Assert.Equal(1f, trauma.Value);

		// 2 a second: half a second empties it.
		for (int i = 0; i < 30; i++)
		{
			trauma.Decay(Dt, 2f);
		}

		Assert.False(trauma.IsActive);
		Assert.Equal(0f, trauma.Value);
	}

	[Fact]
	public void Trauma_NothingNegative_IsAdded_OrDrained()
	{
		var trauma = new ShakeTrauma();
		trauma.Add(0.5f);
		trauma.Add(-1f);
		trauma.Decay(-1f, 2f);
		trauma.Decay(1f, -2f);

		Assert.Equal(0.5f, trauma.Value, 4);
	}

	[Fact]
	public void Shake_IsTraumaToThePower_SoLightHitsAreATwitch()
	{
		var light = new ShakeTrauma();
		light.Add(0.3f);
		var heavy = new ShakeTrauma();
		heavy.Add(0.9f);

		Assert.Equal(0.09f, light.Shake(2f), 4);
		Assert.Equal(0.81f, heavy.Shake(2f), 4);

		// The gap between them widens with the power: a jolt is a jolt.
		Assert.True(heavy.Shake(2f) / light.Shake(2f) > heavy.Shake(1f) / light.Shake(1f));
	}

	[Fact]
	public void Shake_NeverOutrunsTheTrauma()
	{
		var trauma = new ShakeTrauma();
		trauma.Add(0.5f);

		Assert.Equal(0.5f, trauma.Shake(0.25f), 4);
		Assert.Equal(0f, new ShakeTrauma().Shake(2f));
	}
}
