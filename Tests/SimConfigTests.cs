using Gdpyr.Sim;
using Xunit;

namespace Gdpyr.Tests;

public class SimConfigTests
{
	[Fact]
	public void TickDelta_IsTheReciprocalOfTheTickRate()
	{
		Assert.Equal(1.0f / SimConfig.TickRate, SimConfig.TickDelta, precision: 6);
	}

	[Fact]
	public void TickRate_IsSixtyHertz()
	{
		// Recorded tick indices, the hitbox history ring and every client's clock
		// estimate are denominated in this. Changing it is a netcode decision, not a
		// tuning knob (docs/NETCODE.md §2).
		Assert.Equal(60, SimConfig.TickRate);
	}
}
