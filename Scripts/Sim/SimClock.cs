using System;

namespace Gdpyr.Sim;

/// <summary>
/// The client's two clocks (docs/NETCODE.md §2).
///
/// <see cref="ServerTick"/> is an estimate of what the server is simulating right
/// now, derived from the RTT probe; remote entities render
/// <see cref="SimConfig.InterpolationDelayTicks"/> behind it.
/// <see cref="InputTick"/> runs a further half-RTT plus a jitter margin *ahead*,
/// so sampled input lands at the server just before it is needed.
///
/// Collapsing these into one clock is the classic mistake: one of the two
/// directions of travel would then be unaccounted for.
///
/// Engine-free: the caller supplies measured RTTs and calls <see cref="Advance"/>
/// once per physics tick.
/// </summary>
public sealed class SimClock
{
	/// <summary>False until the first probe reply; before that a client must not send input.</summary>
	public bool Initialized { get; private set; }

	/// <summary>Estimate of the tick the server is simulating now.</summary>
	public uint ServerTick { get; private set; }

	/// <summary>The last tick stamped on a locally sampled input.</summary>
	public uint InputTick { get; private set; }

	/// <summary>Smoothed round trip time, in seconds.</summary>
	public float RttSeconds { get; private set; }

	/// <summary>Smoothed mean deviation of the RTT samples — the jitter the buffer has to absorb.</summary>
	public float RttJitterSeconds { get; private set; }

	/// <summary>The server's last reported depth of this client's input buffer.</summary>
	public int InputBufferDepth { get; private set; }

	/// <summary>±1-tick corrections applied to the input clock. A healthy link nudges rarely.</summary>
	public int Nudges { get; private set; }

	/// <summary>Hard clock resets. Anything other than 0 after the first second is a problem.</summary>
	public int Resyncs { get; private set; }

	private int _pendingNudge;
	private int _nudgeCooldownTicks;

	/// <summary>Ticks left before another nudge may be applied. Exposed for the HUD.</summary>
	public int NudgeCooldownTicks => _nudgeCooldownTicks;

	/// <summary>
	/// The tick remote entities are rendered at: far enough behind the server that
	/// two snapshots always bracket it. Fractional, because snapshots are 30 Hz and
	/// the render clock moves at 60.
	/// </summary>
	public float RenderTick => ServerTick - (float)SimConfig.InterpolationDelayTicks;

	/// <summary>Ticks between this client's input clock and its server-tick estimate.</summary>
	public long InputLeadTicks => (long)InputTick - ServerTick;

	/// <summary>
	/// Folds one RTT probe in. <paramref name="serverTickAtReply"/> is the tick the
	/// server stamped the reply with; by the time it arrives the server has moved on
	/// by another half RTT, and an input sent now needs a further half RTT to get
	/// there — hence the two half-RTT terms.
	/// </summary>
	public void OnClockReply(uint serverTickAtReply, float rttSeconds)
	{
		rttSeconds = Math.Max(rttSeconds, 0f);

		if (!Initialized)
		{
			RttSeconds = rttSeconds;
			RttJitterSeconds = 0f;
		}
		else
		{
			RttJitterSeconds += (Math.Abs(rttSeconds - RttSeconds) - RttJitterSeconds) * SimConfig.RttSmoothing;
			RttSeconds += (rttSeconds - RttSeconds) * SimConfig.RttSmoothing;
		}

		uint halfRttTicks = (uint)MathF.Ceiling(RttSeconds * 0.5f / SimConfig.TickDelta);
		uint serverNow = serverTickAtReply + halfRttTicks;
		uint targetInputTick = serverNow + halfRttTicks + SimConfig.JitterBufferTicks;

		if (!Initialized)
		{
			ServerTick = serverNow;
			// InputTick is "the last tick sampled", so the next sample is the target.
			InputTick = targetInputTick - 1;
			Initialized = true;
			return;
		}

		ApplyServerTickEstimate(serverNow);

		long inputError = (long)targetInputTick - 1 - InputTick;
		if (Math.Abs(inputError) > SimConfig.ClockResyncThresholdTicks)
		{
			InputTick = targetInputTick - 1;
			Resyncs++;
		}
	}

	/// <summary>
	/// The server's view of how early this client's input is arriving. This is the
	/// fine-grained correction; the probe only catches gross error.
	/// </summary>
	public void OnInputBufferDepth(int depth)
	{
		InputBufferDepth = depth;

		// This reading is a round trip old. Acting on it again before the last
		// correction has had time to show up in it would chase the same error twice
		// and oscillate between stalling and double-stepping.
		if (_nudgeCooldownTicks > 0)
		{
			return;
		}

		if (depth > SimConfig.MaxInputBufferDepth)
		{
			// Too far ahead: stall one tick so the server can catch up.
			_pendingNudge = -1;
		}
		else if (depth < SimConfig.MinInputBufferDepth)
		{
			// Arriving late: produce an extra tick of input this frame.
			_pendingNudge = 1;
		}
		else
		{
			_pendingNudge = 0;
		}
	}

	/// <summary>
	/// One physics tick. Returns how many input ticks to sample and simulate this
	/// frame: normally 1, 0 when stalling, 2 when catching up.
	/// </summary>
	public int Advance()
	{
		if (!Initialized)
		{
			return 0;
		}

		ServerTick++;

		if (_nudgeCooldownTicks > 0)
		{
			_nudgeCooldownTicks--;
		}

		int steps = 1 + _pendingNudge;
		if (_pendingNudge != 0)
		{
			Nudges++;
			_pendingNudge = 0;
			_nudgeCooldownTicks = CooldownTicks();
		}
		return steps;
	}

	/// <summary>Consumes the next input tick index. Call once per step returned by <see cref="Advance"/>.</summary>
	public uint NextInputTick() => ++InputTick;

	/// <summary>One round trip plus the jitter margin: how long a nudge takes to become visible.</summary>
	private int CooldownTicks()
	{
		int rttTicks = (int)MathF.Ceiling(RttSeconds / SimConfig.TickDelta) + SimConfig.JitterBufferTicks;
		return Math.Clamp(rttTicks, SimConfig.MinNudgeCooldownTicks, SimConfig.MaxNudgeCooldownTicks);
	}

	/// <summary>
	/// Corrects the render clock. Small errors are walked off a tick at a time:
	/// snapping it would jump the interpolation window and visibly stutter every
	/// remote player.
	/// </summary>
	private void ApplyServerTickEstimate(uint serverNow)
	{
		long error = (long)serverNow - ServerTick;
		if (Math.Abs(error) > SimConfig.ClockResyncThresholdTicks)
		{
			ServerTick = serverNow;
			Resyncs++;
		}
		else if (error >= 2)
		{
			ServerTick++;
		}
		else if (error <= -2)
		{
			ServerTick--;
		}
	}
}
