using Godot;

namespace Gdpyr.Sim;

/// <summary>
/// The receive buffer for one remote character (docs/NETCODE.md §3.3).
///
/// Remote players are never predicted and never simulated on a client: they are
/// drawn at <c>SimClock.RenderTick</c>, which sits far enough behind the server
/// estimate that two snapshots always bracket it. On loss it extrapolates for at
/// most <see cref="SimConfig.MaxExtrapolationTicks"/> and then freezes — a frozen
/// player reads as a hitch, an extrapolated one reads as a player running through
/// a wall.
///
/// Fixed storage, kept sorted by tick, so a snapshot that arrives out of order is
/// still usable as long as the render clock has not passed it yet.
/// </summary>
public sealed class SnapshotInterpolator
{
	private readonly uint[] _ticks = new uint[SimConfig.SnapshotBufferTicks];
	private readonly Vector3[] _positions = new Vector3[SimConfig.SnapshotBufferTicks];
	private readonly float[] _yaws = new float[SimConfig.SnapshotBufferTicks];
	private readonly float[] _pitches = new float[SimConfig.SnapshotBufferTicks];

	public int Count { get; private set; }

	public uint OldestTick => Count > 0 ? _ticks[0] : 0;
	public uint NewestTick => Count > 0 ? _ticks[Count - 1] : 0;

	/// <summary>Snapshots dropped because the render clock had already passed their tick.</summary>
	public int Stale { get; private set; }

	public void Push(uint tick, Vector3 position, float yaw, float pitch)
	{
		// Duplicate tick: a retransmit of something already held.
		for (int i = Count - 1; i >= 0; i--)
		{
			if (_ticks[i] == tick)
			{
				return;
			}
			if (_ticks[i] < tick)
			{
				break;
			}
		}

		if (Count == _ticks.Length)
		{
			if (tick <= _ticks[0])
			{
				Stale++;
				return;
			}
			Shift();
		}

		int insert = Count;
		while (insert > 0 && _ticks[insert - 1] > tick)
		{
			_ticks[insert] = _ticks[insert - 1];
			_positions[insert] = _positions[insert - 1];
			_yaws[insert] = _yaws[insert - 1];
			_pitches[insert] = _pitches[insert - 1];
			insert--;
		}

		_ticks[insert] = tick;
		_positions[insert] = position;
		_yaws[insert] = yaw;
		_pitches[insert] = pitch;
		Count++;
	}

	/// <summary>
	/// Samples the buffer at a fractional tick. False only when nothing has ever
	/// been received for this entity.
	/// </summary>
	public bool TrySample(float renderTick, out Vector3 position, out float yaw, out float pitch)
	{
		position = Vector3.Zero;
		yaw = 0f;
		pitch = 0f;

		if (Count == 0)
		{
			return false;
		}

		if (Count == 1 || renderTick <= _ticks[0])
		{
			position = _positions[0];
			yaw = _yaws[0];
			pitch = _pitches[0];
			return true;
		}

		int newest = Count - 1;
		if (renderTick >= _ticks[newest])
		{
			ExtrapolatePastNewest(renderTick, newest, out position, out yaw, out pitch);
			return true;
		}

		for (int i = newest; i > 0; i--)
		{
			if (_ticks[i - 1] <= renderTick)
			{
				float span = _ticks[i] - _ticks[i - 1];
				float t = span > 0f ? (renderTick - _ticks[i - 1]) / span : 0f;
				position = _positions[i - 1].Lerp(_positions[i], t);
				yaw = Mathf.LerpAngle(_yaws[i - 1], _yaws[i], t);
				pitch = Mathf.Lerp(_pitches[i - 1], _pitches[i], t);
				return true;
			}
		}

		position = _positions[0];
		yaw = _yaws[0];
		pitch = _pitches[0];
		return true;
	}

	/// <summary>Drops snapshots the render clock has moved past, keeping one either side of it.</summary>
	public void Prune(float renderTick)
	{
		while (Count > 2 && _ticks[1] < renderTick - SimConfig.SnapshotBufferTicks)
		{
			Shift();
		}
	}

	private void ExtrapolatePastNewest(float renderTick, int newest, out Vector3 position, out float yaw, out float pitch)
	{
		float ahead = renderTick - _ticks[newest];
		float limit = SimConfig.MaxExtrapolationTicks;
		if (ahead > limit)
		{
			ahead = limit;
		}

		float span = _ticks[newest] - _ticks[newest - 1];
		if (ahead <= 0f || span <= 0f)
		{
			position = _positions[newest];
			yaw = _yaws[newest];
			pitch = _pitches[newest];
			return;
		}

		Vector3 perTick = (_positions[newest] - _positions[newest - 1]) / span;
		position = _positions[newest] + (perTick * ahead);
		// Angles are not extrapolated: a player who was mid-turn when the packets
		// stopped would spin off, and a wrong facing is worse than a stale one.
		yaw = _yaws[newest];
		pitch = _pitches[newest];
	}

	private void Shift()
	{
		for (int i = 1; i < Count; i++)
		{
			_ticks[i - 1] = _ticks[i];
			_positions[i - 1] = _positions[i];
			_yaws[i - 1] = _yaws[i];
			_pitches[i - 1] = _pitches[i];
		}
		Count--;
	}
}
