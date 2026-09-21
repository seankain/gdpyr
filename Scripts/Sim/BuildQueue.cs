using System;

namespace Gdpyr.Sim;

/// <summary>
/// A barracks' production queue (docs/IMPLEMENTATION_PLAN.md §M3).
///
/// One unit at a time, in order, each taking its own build time. The queue owns
/// the money: <see cref="TryEnqueue"/> charges the ledger and only queues if the
/// charge went through, and <see cref="CancelLast"/> hands it back. Charging on
/// order rather than on completion is what makes twenty queued riflemen a
/// commitment rather than an intention, which is the whole reason a strategist has
/// to choose.
///
/// Fixed storage and engine-free: this ticks on the server sixty times a second
/// per barracks, and "how many did it actually build" is worth answering from a
/// test.
/// </summary>
public sealed class BuildQueue
{
	public const int Capacity = SimConfig.MaxBuildQueue;

	private readonly byte[] _definitions = new byte[Capacity];
	private readonly int[] _costs = new int[Capacity];
	private readonly int[] _buildTicks = new int[Capacity];

	private int _count;

	/// <summary>The tick the head finishes on, or 0 before it has started.</summary>
	private uint _headEndTick;

	private int _headSpanTicks;

	public int Count => _count;

	public bool IsFull => _count >= Capacity;

	/// <summary>Units this queue has completed. For the debug HUD and M8's CSV.</summary>
	public int Produced { get; private set; }

	/// <summary>Bumped on every change worth replicating to the strategist's HUD.</summary>
	public uint Version { get; private set; }

	/// <summary>
	/// Charges <paramref name="ledger"/> and queues the unit. False when the queue
	/// is full or the points are not there — and in the latter case nothing was
	/// charged.
	/// </summary>
	public bool TryEnqueue(byte definitionId, int cost, int buildTicks, ResourceLedger ledger)
	{
		if (IsFull || ledger == null || !ledger.TrySpend(cost))
		{
			return false;
		}

		_definitions[_count] = definitionId;
		_costs[_count] = cost;
		_buildTicks[_count] = Math.Max(buildTicks, 1);
		_count++;
		Version++;
		return true;
	}

	/// <summary>
	/// Advances the head and reports a completed unit. At most one per call: a
	/// barracks that emptied its queue into one tick would put twenty units inside
	/// each other.
	/// </summary>
	public bool Tick(uint tick, out byte definitionId)
	{
		definitionId = 0;
		if (_count == 0)
		{
			return false;
		}

		if (_headEndTick == 0)
		{
			_headSpanTicks = _buildTicks[0];
			_headEndTick = tick + (uint)_headSpanTicks;
		}

		if (tick < _headEndTick)
		{
			return false;
		}

		definitionId = _definitions[0];
		Dequeue();
		Produced++;
		return true;
	}

	/// <summary>
	/// Cancels the most recently queued unit and refunds it — the last, not the
	/// first, so that cancelling does not throw away work already done.
	/// </summary>
	public bool CancelLast(ResourceLedger ledger)
	{
		if (_count == 0)
		{
			return false;
		}

		_count--;
		ledger?.Refund(_costs[_count]);

		if (_count == 0)
		{
			_headEndTick = 0;
			_headSpanTicks = 0;
		}

		Version++;
		return true;
	}

	/// <summary>
	/// How far the head is through its build, in [0,1]. Reads as 0 for an empty
	/// queue and for a head that has not been ticked yet.
	/// </summary>
	public float Progress(uint tick)
	{
		if (_count == 0 || _headEndTick == 0 || _headSpanTicks <= 0)
		{
			return 0f;
		}

		long remaining = (long)_headEndTick - tick;
		if (remaining <= 0)
		{
			return 1f;
		}

		return Math.Clamp(1f - ((float)remaining / _headSpanTicks), 0f, 1f);
	}

	/// <summary>The definition at the head, or 0 when nothing is queued.</summary>
	public byte Head => _count > 0 ? _definitions[0] : (byte)0;

	public void Clear()
	{
		_count = 0;
		_headEndTick = 0;
		_headSpanTicks = 0;
		Version++;
	}

	private void Dequeue()
	{
		for (int i = 1; i < _count; i++)
		{
			_definitions[i - 1] = _definitions[i];
			_costs[i - 1] = _costs[i];
			_buildTicks[i - 1] = _buildTicks[i];
		}

		_count--;
		_headEndTick = 0;
		_headSpanTicks = 0;
		Version++;
	}
}
