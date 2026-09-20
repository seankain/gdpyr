using System;

namespace Gdpyr.Sim;

/// <summary>
/// The numbers behind the net debug HUD (docs/NETCODE.md §8). Built in M1 on
/// purpose: every hard bug in this system is a timing bug, and without these the
/// later milestones' bugs are unfalsifiable.
///
/// Rates are published once per second from running accumulators; the byte
/// counters measure application payload, not what ENet and IP add on top.
/// </summary>
public sealed class NetStats
{
	public float BytesInPerSecond { get; private set; }
	public float BytesOutPerSecond { get; private set; }
	public float MispredictionsPerSecond { get; private set; }

	/// <summary>Mean position error at reconciliation over the last window, in metres.</summary>
	public float MeanErrorMeters { get; private set; }

	/// <summary>Worst position error over the last window, in metres.</summary>
	public float MaxErrorMeters { get; private set; }

	public long TotalBytesIn { get; private set; }
	public long TotalBytesOut { get; private set; }
	public long TotalMispredictions { get; private set; }

	private float _window;
	private int _bytesIn;
	private int _bytesOut;
	private int _mispredictions;
	private int _errorSamples;
	private float _errorSum;
	private float _errorMax;

	public void RecordSent(int bytes)
	{
		_bytesOut += bytes;
		TotalBytesOut += bytes;
	}

	public void RecordReceived(int bytes)
	{
		_bytesIn += bytes;
		TotalBytesIn += bytes;
	}

	/// <summary>
	/// Records the gap between what was predicted for a tick and what the server
	/// says actually happened. Called for every reconciliation, correction or not:
	/// the mean is only meaningful if the good ticks are in it too.
	/// </summary>
	public void RecordPredictionError(float meters)
	{
		_errorSamples++;
		_errorSum += meters;
		if (meters > _errorMax)
		{
			_errorMax = meters;
		}
	}

	public void RecordMisprediction()
	{
		_mispredictions++;
		TotalMispredictions++;
	}

	/// <summary>Advances the publishing window. Call once per tick.</summary>
	public void Advance(float dt)
	{
		_window += dt;

		// The epsilon is not cosmetic: sixty additions of 1f/60f land just under 1,
		// so an exact comparison publishes every 61st tick and reads ~2% low.
		if (_window + 1e-4f < 1f)
		{
			return;
		}

		float inv = 1f / _window;
		BytesInPerSecond = _bytesIn * inv;
		BytesOutPerSecond = _bytesOut * inv;
		MispredictionsPerSecond = _mispredictions * inv;
		MeanErrorMeters = _errorSamples > 0 ? _errorSum / _errorSamples : 0f;
		MaxErrorMeters = _errorMax;

		_window = 0f;
		_bytesIn = 0;
		_bytesOut = 0;
		_mispredictions = 0;
		_errorSamples = 0;
		_errorSum = 0f;
		_errorMax = 0f;
	}

	/// <summary>Formats a byte rate for the HUD.</summary>
	public static string FormatRate(float bytesPerSecond) => bytesPerSecond >= 1024f
		? $"{bytesPerSecond / 1024f:0.0} KB/s"
		: $"{MathF.Round(bytesPerSecond)} B/s";
}
