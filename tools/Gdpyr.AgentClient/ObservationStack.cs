using System;

namespace Gdpyr.AgentClient;

/// <summary>
/// The last N observations, concatenated oldest first.
///
/// Both gdpyr seats are partially observed — a ground policy sees eight contacts
/// its own eyes acquired and a ray fan, a strategist sees ghosts that decay — and
/// a single frame of either is not a Markov state: it says where things are and
/// not where they are going. The two standard answers are a recurrent policy,
/// which carries its own memory, and a stack of recent frames, which hands the
/// memory to the network as more input. This is the second one, because it costs
/// one array copy per decision and works with any feed-forward agent
/// (docs/RL_ARCHITECTURE.md §4).
///
/// Every push returns a **fresh** array. A learner keeps the state it was handed
/// inside a transition, and a buffer reused across steps would quietly rewrite
/// transitions that had already been recorded.
/// </summary>
public sealed class ObservationStack
{
	private readonly float[] _window;
	private readonly int _floats;
	private readonly int _frames;

	/// <param name="floats">Floats in one observation.</param>
	/// <param name="frames">Observations kept. 1 is no stacking at all.</param>
	public ObservationStack(int floats, int frames)
	{
		_floats = Math.Max(floats, 0);
		_frames = Math.Max(frames, 1);
		_window = new float[_floats * _frames];
	}

	/// <summary>Floats in the stacked vector: one observation's, times the frames kept.</summary>
	public int Floats => _window.Length;

	/// <summary>Observations kept.</summary>
	public int Frames => _frames;

	/// <summary>
	/// Fills every frame with one observation and returns it. For an episode's
	/// first decision, where there is no history and a zeroed one would teach the
	/// policy that every round starts with everything standing still.
	/// </summary>
	public float[] Fill(float[] observation)
	{
		for (int frame = 0; frame < _frames; frame++)
		{
			Copy(observation, frame * _floats);
		}

		return Snapshot();
	}

	/// <summary>Shifts the window along and writes one observation into the newest slot.</summary>
	public float[] Push(float[] observation)
	{
		if (_frames > 1)
		{
			Array.Copy(_window, _floats, _window, 0, _floats * (_frames - 1));
		}

		Copy(observation, (_frames - 1) * _floats);
		return Snapshot();
	}

	private float[] Snapshot()
	{
		var values = new float[_window.Length];
		Array.Copy(_window, values, _window.Length);
		return values;
	}

	/// <summary>
	/// Writes one observation into a frame slot, zero-padding or truncating a vector
	/// that is not the length this stack was built for rather than throwing: a
	/// server that grew a float mid-run is a thing to notice in the log, not a
	/// crash in the middle of a rollout.
	/// </summary>
	private void Copy(float[] observation, int at)
	{
		Array.Clear(_window, at, _floats);
		int count = Math.Min(observation?.Length ?? 0, _floats);
		if (count > 0)
		{
			Array.Copy(observation, 0, _window, at, count);
		}
	}
}
