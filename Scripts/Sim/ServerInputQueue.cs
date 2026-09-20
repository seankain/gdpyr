namespace Gdpyr.Sim;

/// <summary>
/// The server's jitter buffer for one client (docs/NETCODE.md §2).
///
/// Clients stamp each <see cref="InputFrame"/> with the server tick they estimate
/// it is for and send each frame three times; this absorbs the reordering,
/// duplication and jitter that comes with that, and hands the simulation exactly
/// one frame per tick. <see cref="Depth"/> goes back to the client in every
/// snapshot so it can steer its clock — the buffer measures the client's clock
/// error, which is the only reason to report it.
///
/// Fixed storage, no allocation after construction: this runs per client per tick.
/// </summary>
public sealed class ServerInputQueue
{
	/// <summary>Slots, indexed by tick. Also the largest gap that can be bridged before a resync.</summary>
	public const int Capacity = 64;

	private readonly InputFrame[] _slots = new InputFrame[Capacity];
	private readonly bool[] _filled = new bool[Capacity];
	private readonly uint[] _slotTicks = new uint[Capacity];

	/// <summary>Frames buffered and not yet consumed.</summary>
	public int Depth { get; private set; }

	/// <summary>True once a frame has been consumed; before that the queue is still filling.</summary>
	public bool Started { get; private set; }

	/// <summary>The tick of the last *real* frame consumed. This is what the snapshot acknowledges.</summary>
	public uint AckTick { get; private set; }

	/// <summary>The last frame consumed, repeated when the buffer runs dry.</summary>
	public InputFrame Last { get; private set; }

	/// <summary>Ticks where the buffer was empty and the last frame had to be repeated.</summary>
	public int Starvations { get; private set; }

	/// <summary>Frames that arrived after their tick had already been simulated.</summary>
	public int Discards { get; private set; }

	/// <summary>Duplicates from the 3-frame redundancy. Expected to be ~2× the frame rate.</summary>
	public int Duplicates { get; private set; }

	/// <summary>Times the client's tick stamps jumped clear out of the window.</summary>
	public int Resyncs { get; private set; }

	/// <summary>The next tick <see cref="TryPop"/> will look for.</summary>
	private uint _nextTick;

	public void Accept(in InputFrame frame)
	{
		if (Started)
		{
			if (frame.Tick < _nextTick)
			{
				Discards++;
				return;
			}

			// A client that reconnects, resyncs its clock or is simply lying can put a
			// tick far in the future on the wire. Rebase rather than stall for a
			// minute: the client is the authority on its own input clock.
			if (frame.Tick >= _nextTick + Capacity)
			{
				Clear();
				Resyncs++;
				_nextTick = frame.Tick;
			}
		}

		int slot = (int)(frame.Tick % Capacity);
		if (_filled[slot])
		{
			if (_slotTicks[slot] == frame.Tick)
			{
				Duplicates++;
				return;
			}

			// Same slot, different tick: the buffer wrapped. Keep the newer frame.
			Depth--;
		}

		_slots[slot] = frame;
		_slotTicks[slot] = frame.Tick;
		_filled[slot] = true;
		Depth++;
	}

	/// <summary>
	/// Takes the frame for the next tick. Returns false only before the first frame
	/// has ever arrived — after that a starved queue repeats the last frame, which
	/// is what keeps a character moving through a dropped packet instead of
	/// stuttering. Repeating also cannot double-fire an edge-triggered action,
	/// because the buttons are identical to the previous tick's.
	/// </summary>
	public bool TryPop(out InputFrame frame)
	{
		if (!Started)
		{
			if (Depth == 0)
			{
				frame = default;
				return false;
			}
			_nextTick = OldestBufferedTick();
			Started = true;
		}

		int slot = (int)(_nextTick % Capacity);
		if (_filled[slot] && _slotTicks[slot] == _nextTick)
		{
			frame = _slots[slot];
			_filled[slot] = false;
			Depth--;
			Last = frame;
			AckTick = frame.Tick;
		}
		else
		{
			Starvations++;
			frame = Last;
		}

		_nextTick++;
		return true;
	}

	private uint OldestBufferedTick()
	{
		uint oldest = 0;
		bool found = false;
		for (int i = 0; i < Capacity; i++)
		{
			if (_filled[i] && (!found || _slotTicks[i] < oldest))
			{
				oldest = _slotTicks[i];
				found = true;
			}
		}
		return oldest;
	}

	private void Clear()
	{
		for (int i = 0; i < Capacity; i++)
		{
			_filled[i] = false;
		}
		Depth = 0;
	}
}
