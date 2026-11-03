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
	private readonly float[] _drawnBehind = new float[Capacity];

	/// <summary>Frames buffered and not yet consumed.</summary>
	public int Depth { get; private set; }

	/// <summary>
	/// <see cref="Depth"/> after each pop, smoothed over about a third of a second: how
	/// long this client's frames wait to be stepped, without the tick-to-tick jitter one
	/// datagram's early or late arrival puts on the depth. What a seated shooter's view
	/// tick is measured with (docs/GROUND_VEHICLES.md §5.6).
	/// </summary>
	public float SmoothedDepth { get; private set; }

	/// <summary>How much of each pop's depth goes into <see cref="SmoothedDepth"/>.</summary>
	public const float DepthSmoothing = 0.05f;

	/// <summary>True once a frame has been consumed; before that the queue is still filling.</summary>
	public bool Started { get; private set; }

	/// <summary>The tick of the last *real* frame consumed. This is what the snapshot acknowledges.</summary>
	public uint AckTick { get; private set; }

	/// <summary>
	/// The tick of the slot the last <see cref="TryPop"/> filled — with its own frame, or
	/// with <see cref="Last"/> repeated when it had none — and so the frame the server
	/// stepped as that tick. One more every pop, starved or not; 0 before the first. A
	/// frame for it that arrives later is discarded, so it is what a replay of anything
	/// stepped once per tick has to start after (docs/GROUND_VEHICLES.md §7.1): a crew
	/// vehicle's record acknowledges it, where the player snapshot acknowledges
	/// <see cref="AckTick"/> and reads a repeat as starvation.
	/// </summary>
	public uint ConsumedTick { get; private set; }

	/// <summary>The last frame consumed, repeated when the buffer runs dry.</summary>
	public InputFrame Last { get; private set; }

	/// <summary>
	/// The tick this client was drawing the field at when it sampled the frame the last
	/// <see cref="TryPop"/> handed out (docs/GROUND_VEHICLES.md §5.6): that frame's tick
	/// less how far behind it the client said it drew — said for the newest frame of each
	/// packet, and carried to a frame that came only as an older one from the frame before
	/// it, the client's lead changing rarely. NaN until the client has said.
	/// </summary>
	public float ConsumedDrawnTick { get; private set; } = float.NaN;

	private float _lastDrawnBehind = float.NaN;

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

	/// <summary>False until the first pop, which <see cref="SmoothedDepth"/> starts from.</summary>
	private bool _smoothing;

	public void Accept(in InputFrame frame) => Accept(frame, float.NaN);

	/// <summary>
	/// A frame, and how far behind its tick its client drew the field when it sampled it
	/// [ticks] — NaN for not known, which every frame but a packet's newest is.
	/// </summary>
	public void Accept(in InputFrame frame, float drawnBehindTicks)
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
				// Reordered: the copy that came as a packet's newest, after an older copy.
				if (float.IsFinite(drawnBehindTicks))
				{
					_drawnBehind[slot] = drawnBehindTicks;
				}

				Duplicates++;
				return;
			}

			// Same slot, different tick: the buffer wrapped. Keep the newer frame.
			Depth--;
		}

		_slots[slot] = frame;
		_slotTicks[slot] = frame.Tick;
		_filled[slot] = true;
		_drawnBehind[slot] = drawnBehindTicks;
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
			if (float.IsFinite(_drawnBehind[slot]))
			{
				_lastDrawnBehind = _drawnBehind[slot];
			}
		}
		else
		{
			Starvations++;
			frame = Last;
		}

		ConsumedTick = _nextTick;
		ConsumedDrawnTick = float.IsFinite(_lastDrawnBehind) ? frame.Tick - _lastDrawnBehind : float.NaN;
		_nextTick++;
		SmoothedDepth = _smoothing ? SmoothedDepth + ((Depth - SmoothedDepth) * DepthSmoothing) : Depth;
		_smoothing = true;
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
