using Gdpyr.Sim;
using Xunit;

namespace Gdpyr.Tests;

public class ServerInputQueueTests
{
	private static InputFrame Frame(uint tick, float moveX = 0f) =>
		InputFrame.Create(tick, moveX, 0f, 0f, 0f, InputButtons.None);

	[Fact]
	public void Pop_FailsUntilTheFirstFrameArrives()
	{
		var queue = new ServerInputQueue();
		Assert.False(queue.TryPop(out _));
		Assert.False(queue.Started);
	}

	[Fact]
	public void Pop_ReturnsFramesInTickOrder()
	{
		var queue = new ServerInputQueue();
		queue.Accept(Frame(10));
		queue.Accept(Frame(11));
		queue.Accept(Frame(12));

		for (uint tick = 10; tick <= 12; tick++)
		{
			Assert.True(queue.TryPop(out InputFrame frame));
			Assert.Equal(tick, frame.Tick);
		}
		Assert.Equal(0, queue.Depth);
		Assert.Equal(12u, queue.AckTick);
	}

	[Fact]
	public void Accept_ToleratesReordering()
	{
		// UDP does not promise order; the buffer is what makes that a non-event.
		var queue = new ServerInputQueue();
		queue.Accept(Frame(31));
		queue.Accept(Frame(30));
		queue.Accept(Frame(32));

		Assert.True(queue.TryPop(out InputFrame first));
		Assert.Equal(30u, first.Tick);
		Assert.True(queue.TryPop(out InputFrame second));
		Assert.Equal(31u, second.Tick);
	}

	[Fact]
	public void Accept_IgnoresTheRedundantCopies()
	{
		// Every frame is sent three times (docs/NETCODE.md §3.2); two of those are
		// duplicates and must not deepen the buffer.
		var queue = new ServerInputQueue();
		for (int i = 0; i < 3; i++)
		{
			queue.Accept(Frame(5));
		}

		Assert.Equal(1, queue.Depth);
		Assert.Equal(2, queue.Duplicates);
	}

	[Fact]
	public void Accept_DiscardsAFrameThatArrivesTooLate()
	{
		var queue = new ServerInputQueue();
		queue.Accept(Frame(1));
		queue.Accept(Frame(2));
		Assert.True(queue.TryPop(out _));
		Assert.True(queue.TryPop(out _));

		queue.Accept(Frame(1));
		Assert.Equal(1, queue.Discards);
		Assert.Equal(0, queue.Depth);
	}

	[Fact]
	public void Pop_RepeatsTheLastFrameWhenStarved()
	{
		// A dropped packet must not stop the character dead: repeating the last
		// input keeps it moving until the next one lands.
		var queue = new ServerInputQueue();
		queue.Accept(Frame(1, moveX: 1f));
		Assert.True(queue.TryPop(out InputFrame real));

		Assert.True(queue.TryPop(out InputFrame repeated));
		Assert.Equal(real.MoveX, repeated.MoveX);
		Assert.Equal(1, queue.Starvations);
		// The acknowledgement stays on the last real frame, so the client keeps the
		// inputs it still has to replay.
		Assert.Equal(1u, queue.AckTick);
	}

	[Fact]
	public void Pop_RepeatingCannotRetriggerAnEdge()
	{
		var queue = new ServerInputQueue();
		queue.Accept(InputFrame.Create(1, 0f, 0f, 0f, 0f, InputButtons.Jump));
		Assert.True(queue.TryPop(out InputFrame first));
		Assert.True(queue.TryPop(out InputFrame repeat));

		// Identical buttons two ticks running means JustPressed is false on the
		// second: the character cannot double jump off a lost packet.
		var context = new InputContext(repeat, first.Buttons, SimConfig.TickDelta);
		Assert.True(context.Held(InputButtons.Jump));
		Assert.False(context.JustPressed(InputButtons.Jump));
	}

	[Fact]
	public void Accept_ResyncsWhenTheClientClockJumps()
	{
		var queue = new ServerInputQueue();
		queue.Accept(Frame(1));
		Assert.True(queue.TryPop(out _));

		queue.Accept(Frame(10_000));
		Assert.Equal(1, queue.Resyncs);
		Assert.True(queue.TryPop(out InputFrame frame));
		Assert.Equal(10_000u, frame.Tick);
	}

	[Fact]
	public void Depth_TracksWhatIsBuffered()
	{
		var queue = new ServerInputQueue();
		for (uint tick = 1; tick <= 4; tick++)
		{
			queue.Accept(Frame(tick));
		}
		Assert.Equal(4, queue.Depth);

		Assert.True(queue.TryPop(out _));
		Assert.Equal(3, queue.Depth);
	}

	[Fact]
	public void SteadyState_NeverStarvesAtTheTargetBufferDepth()
	{
		// One frame produced and one consumed per tick, with three-frame redundancy
		// and a two-frame head start: the shape of a healthy connection.
		var queue = new ServerInputQueue();
		for (uint tick = 1; tick <= 200; tick++)
		{
			queue.Accept(Frame(tick));
			queue.Accept(Frame(tick > 1 ? tick - 1 : tick));
			if (tick > SimConfig.MinInputBufferDepth)
			{
				Assert.True(queue.TryPop(out _));
			}
		}

		Assert.Equal(0, queue.Starvations);
		Assert.InRange(queue.Depth, SimConfig.MinInputBufferDepth, SimConfig.MaxInputBufferDepth);
	}
}
