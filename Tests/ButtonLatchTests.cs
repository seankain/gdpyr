using Gdpyr.Sim;
using Xunit;

namespace Gdpyr.Tests;

/// <summary>
/// A button held when the game goes back to listening is kept out of the frames until it
/// is let go (docs/LOADOUT.md L-D12): the key that closed the locker's panel is not a
/// use press, and a trigger held through the pause menu is not a shot.
/// </summary>
public class ButtonLatchTests
{
	[Fact]
	public void ALiveSampleWithNothingBeforeItPassesEverything()
	{
		var latch = new ButtonLatch();
		Assert.Equal(InputButtons.Use | InputButtons.Fire, latch.Filter(InputButtons.Use | InputButtons.Fire));
		Assert.Equal(InputButtons.None, latch.Held);
	}

	[Fact]
	public void TheKeyThatClosedThePanelIsNotAPressUntilItIsLetGo()
	{
		var latch = new ButtonLatch();
		latch.Away();

		// E is still down on the first tick the game listens again.
		Assert.Equal(InputButtons.None, latch.Filter(InputButtons.Use));
		Assert.Equal(InputButtons.None, latch.Filter(InputButtons.Use));
		Assert.Equal(InputButtons.Use, latch.Held);

		// Let go: nothing, and so no release for the use tracker to read as a tap.
		Assert.Equal(InputButtons.None, latch.Filter(InputButtons.None));
		Assert.Equal(InputButtons.None, latch.Held);

		// Pressed again, it is the game's.
		Assert.Equal(InputButtons.Use, latch.Filter(InputButtons.Use));
	}

	[Fact]
	public void APressThatBeginsOnceTheGameListensIsTheGamesWhateverElseIsHeld()
	{
		var latch = new ButtonLatch();
		latch.Away();
		Assert.Equal(InputButtons.None, latch.Filter(InputButtons.Fire));

		// The trigger held through the pause menu is still down; jump is new.
		Assert.Equal(InputButtons.Jump, latch.Filter(InputButtons.Fire | InputButtons.Jump));
		Assert.Equal(InputButtons.Fire, latch.Held);
		Assert.Equal(InputButtons.None, latch.Filter(InputButtons.Fire));

		// Let go and clicked again, it is a shot.
		Assert.Equal(InputButtons.None, latch.Filter(InputButtons.None));
		Assert.Equal(InputButtons.Fire, latch.Filter(InputButtons.Fire));
	}

	[Fact]
	public void EachButtonIsReleasedOnItsOwn()
	{
		var latch = new ButtonLatch();
		latch.Away();
		latch.Filter(InputButtons.Use | InputButtons.Crouch);

		Assert.Equal(InputButtons.None, latch.Filter(InputButtons.Crouch));
		Assert.Equal(InputButtons.Crouch, latch.Held);

		// Use was let go and pressed again; crouch has not been let go yet.
		Assert.Equal(InputButtons.Use, latch.Filter(InputButtons.Use | InputButtons.Crouch));
		Assert.Equal(InputButtons.Use, latch.Filter(InputButtons.Use));
	}

	[Fact]
	public void ATickAwayWithNothingHeldLatchesNothing()
	{
		var latch = new ButtonLatch();
		latch.Away();
		Assert.Equal(InputButtons.None, latch.Filter(InputButtons.None));
		Assert.Equal(InputButtons.Sprint, latch.Filter(InputButtons.Sprint));
	}

	[Fact]
	public void BeingAwayAgainLatchesWhatIsDownThen()
	{
		var latch = new ButtonLatch();
		latch.Away();
		latch.Filter(InputButtons.Use);
		latch.Filter(InputButtons.None);

		latch.Away();
		latch.Away();
		Assert.Equal(InputButtons.None, latch.Filter(InputButtons.Fire));
		Assert.Equal(InputButtons.Fire, latch.Held);
	}
}
