using Gdpyr.AgentClient;
using Xunit;

namespace Gdpyr.Tests;

/// <summary>
/// Frame stacking, which is what a feed-forward policy has instead of memory
/// (docs/RL_ARCHITECTURE.md §4).
///
/// Two of these are about aliasing rather than about arithmetic. A learner keeps
/// the state array it was handed inside a transition, so a stack that returned
/// its own buffer would rewrite transitions that had already been recorded — a
/// bug that produces a policy that trains and does not learn, and shows up
/// nowhere else.
/// </summary>
public class ObservationStackTests
{
	[Fact]
	public void OneFrameIsTheObservationItself()
	{
		var stack = new ObservationStack(3, 1);
		Assert.Equal(3, stack.Floats);
		Assert.Equal(new[] { 1f, 2f, 3f }, stack.Push(new[] { 1f, 2f, 3f }));
	}

	[Fact]
	public void FillRepeatsTheFrameRatherThanZeroingTheHistory()
	{
		var stack = new ObservationStack(2, 3);

		Assert.Equal(6, stack.Floats);
		Assert.Equal(new[] { 4f, 5f, 4f, 5f, 4f, 5f }, stack.Fill(new[] { 4f, 5f }));
	}

	[Fact]
	public void PushShiftsTheWindowAndTheNewestFrameIsLast()
	{
		var stack = new ObservationStack(2, 3);
		stack.Fill(new[] { 0f, 0f });

		stack.Push(new[] { 1f, 1f });
		stack.Push(new[] { 2f, 2f });
		float[] state = stack.Push(new[] { 3f, 3f });

		Assert.Equal(new[] { 1f, 1f, 2f, 2f, 3f, 3f }, state);
	}

	[Fact]
	public void EveryPushReturnsAFreshArray()
	{
		var stack = new ObservationStack(2, 2);
		float[] first = stack.Fill(new[] { 1f, 1f });
		float[] second = stack.Push(new[] { 2f, 2f });

		Assert.NotSame(first, second);
		Assert.Equal(new[] { 1f, 1f, 1f, 1f }, first);
	}

	[Fact]
	public void AVectorOfTheWrongLengthIsPaddedOrTruncatedRatherThanThrowing()
	{
		var stack = new ObservationStack(3, 2);

		Assert.Equal(new[] { 0f, 0f, 0f, 7f, 0f, 0f }, stack.Push(new[] { 7f }));
		Assert.Equal(new[] { 7f, 0f, 0f, 1f, 2f, 3f }, stack.Push(new[] { 1f, 2f, 3f, 4f }));
	}
}
