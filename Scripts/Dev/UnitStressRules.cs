using System;
using System.Collections.Generic;
using Godot;

namespace Gdpyr.Dev;

/// <summary>What the unit stress test's panel does on its own (docs/UNIT_STRESS_TEST.md §3).</summary>
public enum StressMode : byte
{
	/// <summary>Nothing but the Spawn button.</summary>
	Manual,

	/// <summary>A wave of the panel's count every interval.</summary>
	Waves,

	/// <summary>The field topped up to the panel's count once a second.</summary>
	KeepAlive,

	/// <summary>The field topped up to a target that rises by the panel's count every interval, measured at each step.</summary>
	Ramp,
}

/// <summary>What a spawned unit is told to do.</summary>
public enum StressOrder : byte
{
	/// <summary>Attack-move on the local player, the point refreshed as they move, the player named as the target.</summary>
	HuntPlayer,

	/// <summary>Attack-move on the middle of the ground force's spawn line.</summary>
	AttackSpawn,

	/// <summary>Stand where it was put and fight what comes into reach.</summary>
	Hold,
}

/// <summary>
/// Where a wave stands round the barracks: rings from the inside out, each <c>spacing</c>
/// further out than the last and filled <c>spacing</c> apart, every other ring turned by half
/// a step so no spot is straight behind the one inside it. Engine-free; the caller drops the
/// spots that are off the navigation mesh or taken.
/// </summary>
public static class StressRings
{
	/// <summary>The fewest spots on a ring, so the first ring round a small building is not a triangle.</summary>
	public const int MinPerRing = 6;

	/// <summary>Spacing for a unit of this radius [m]: two bodies and a metre between them, never under 1.6 m (a barracks row's).</summary>
	public static float SpacingFor(float radiusMeters) => MathF.Max(1.6f, (2f * MathF.Max(radiusMeters, 0f)) + 1f);

	/// <summary>How many spots ring <paramref name="ring"/> (0 the innermost) has.</summary>
	public static int SpotsOn(int ring, float innerRadius, float spacing)
	{
		float radius = innerRadius + (ring * spacing);
		return Math.Max(MinPerRing, (int)MathF.Floor(MathF.Tau * radius / MathF.Max(spacing, 0.1f)));
	}

	/// <summary>Every spot on the first <paramref name="rings"/> rings round <paramref name="centre"/>, inside out, at its height.</summary>
	public static IEnumerable<Vector3> Spots(Vector3 centre, float innerRadius, float spacing, int rings)
	{
		spacing = MathF.Max(spacing, 0.1f);
		for (int ring = 0; ring < rings; ring++)
		{
			float radius = innerRadius + (ring * spacing);
			int count = SpotsOn(ring, innerRadius, spacing);
			float step = MathF.Tau / count;
			float phase = (ring % 2) * step * 0.5f;
			for (int i = 0; i < count; i++)
			{
				float angle = phase + (i * step);
				yield return new Vector3(centre.X + (MathF.Cos(angle) * radius), centre.Y,
					centre.Z + (MathF.Sin(angle) * radius));
			}
		}
	}

	/// <summary>Whether <paramref name="spot"/> is at least <paramref name="clearance"/> from every point in <paramref name="taken"/>, measured flat.</summary>
	public static bool IsClear(Vector3 spot, IReadOnlyList<Vector3> taken, float clearance)
	{
		float squared = clearance * clearance;
		for (int i = 0; i < taken.Count; i++)
		{
			float dx = taken[i].X - spot.X;
			float dz = taken[i].Z - spot.Z;
			if ((dx * dx) + (dz * dz) < squared)
			{
				return false;
			}
		}

		return true;
	}
}

/// <summary>
/// Frame times over the last <see cref="WindowSeconds"/>: their mean, 95th percentile and
/// worst. Fed once a rendered frame with that frame's time; engine-free.
/// </summary>
public sealed class FrameWindow
{
	private readonly Queue<float> _samples = new();
	private float[] _sorted = Array.Empty<float>();
	private double _sumMs;

	public FrameWindow(float windowSeconds) => WindowSeconds = MathF.Max(windowSeconds, 0.05f);

	public float WindowSeconds { get; }

	public int Count => _samples.Count;

	/// <summary>Mean frame time [ms]; 0 with no samples.</summary>
	public float MeanMs => _samples.Count == 0 ? 0f : (float)(_sumMs / _samples.Count);

	/// <summary>Frames a second the mean frame time is worth; 0 with no samples.</summary>
	public float Fps => MeanMs > 0f ? 1000f / MeanMs : 0f;

	public void Add(float frameMs)
	{
		if (!float.IsFinite(frameMs) || frameMs < 0f)
		{
			return;
		}

		_samples.Enqueue(frameMs);
		_sumMs += frameMs;

		// The window is time, not frames: a slow frame holds fewer of them.
		while (_samples.Count > 1 && _sumMs - _samples.Peek() >= WindowSeconds * 1000f)
		{
			_sumMs -= _samples.Dequeue();
		}
	}

	public void Clear()
	{
		_samples.Clear();
		_sumMs = 0;
	}

	/// <summary>The worst frame [ms]; 0 with no samples.</summary>
	public float WorstMs
	{
		get
		{
			float worst = 0f;
			foreach (float sample in _samples)
			{
				worst = MathF.Max(worst, sample);
			}

			return worst;
		}
	}

	/// <summary>The frame time [ms] 95 % of the window's frames were no slower than (nearest rank); 0 with no samples.</summary>
	public float P95Ms => Percentile(0.95f);

	/// <summary>Nearest-rank percentile of the window's frame times, <paramref name="fraction"/> in [0, 1].</summary>
	public float Percentile(float fraction)
	{
		int count = _samples.Count;
		if (count == 0)
		{
			return 0f;
		}

		if (_sorted.Length < count)
		{
			_sorted = new float[Math.Max(count, _sorted.Length * 2)];
		}

		_samples.CopyTo(_sorted, 0);
		Array.Sort(_sorted, 0, count);
		int rank = (int)MathF.Ceiling(Math.Clamp(fraction, 0f, 1f) * count);
		return _sorted[Math.Clamp(rank - 1, 0, count - 1)];
	}
}

/// <summary>
/// What a frame cost besides its length, as the engine and the unit manager report it
/// (docs/UNIT_STRESS_TEST.md §4). Godot sets its two times once a second, to the slowest step of
/// that second; the unit manager its unit pass once a second, to the mean of that second's ticks.
/// </summary>
public readonly struct FrameCosts
{
	public FrameCosts(float unitPassMs, float physicsWorstMs, float idleWorstMs)
	{
		UnitPassMs = Finite(unitPassMs);
		PhysicsWorstMs = Finite(physicsWorstMs);
		IdleWorstMs = Finite(idleWorstMs);
	}

	/// <summary>The unit manager's pass over every unit, a tick, averaged over the last second [ms] (<c>UnitManager.UnitMilliseconds</c>).</summary>
	public float UnitPassMs { get; }

	/// <summary>The slowest physics step in the last second [ms]: the whole simulation tick (<c>Performance.TimePhysicsProcess</c>).</summary>
	public float PhysicsWorstMs { get; }

	/// <summary>The slowest idle frame in the last second [ms]: scripts' process, navigation, and handing the frame to the renderer (<c>Performance.TimeProcess</c>).</summary>
	public float IdleWorstMs { get; }

	private static float Finite(float ms) => float.IsFinite(ms) ? MathF.Max(ms, 0f) : 0f;
}

/// <summary>One step of a ramp as it was measured.</summary>
public readonly struct RampStep
{
	public RampStep(int target, float meanAlive, float meanMs, float p95Ms, float worstMs, int frames,
		float meanUnitPassMs = 0f, float worstPhysicsMs = 0f, float worstIdleMs = 0f)
	{
		Target = target;
		MeanAlive = meanAlive;
		MeanMs = meanMs;
		P95Ms = p95Ms;
		WorstMs = worstMs;
		Frames = frames;
		MeanUnitPassMs = meanUnitPassMs;
		WorstPhysicsMs = worstPhysicsMs;
		WorstIdleMs = worstIdleMs;
	}

	/// <summary>Units the field was topped up to.</summary>
	public int Target { get; }

	/// <summary>Units actually alive, averaged over the measured frames: lower than the target when the player kills them faster than the top-up.</summary>
	public float MeanAlive { get; }

	public float MeanMs { get; }

	public float P95Ms { get; }

	public float WorstMs { get; }

	public int Frames { get; }

	/// <summary>The unit pass a tick, averaged over the measured frames [ms].</summary>
	public float MeanUnitPassMs { get; }

	/// <summary>The slowest simulation tick in the measured part of the step [ms]. Over 16.7 ms, the 60 Hz tick fell behind.</summary>
	public float WorstPhysicsMs { get; }

	/// <summary>The slowest idle frame, render hand-off included, in the measured part of the step [ms].</summary>
	public float WorstIdleMs { get; }

	public float Fps => MeanMs > 0f ? 1000f / MeanMs : 0f;

	public override string ToString() =>
		$"target {Target,2} | alive {MeanAlive,5:0.0} | {Fps,6:0.0} fps | frame mean {MeanMs,6:0.00} ms, p95 {P95Ms,6:0.00},"
		+ $" worst {WorstMs,6:0.00} | unit pass {MeanUnitPassMs,6:0.000} ms | worst physics tick {WorstPhysicsMs,6:0.00} ms"
		+ $" | worst idle frame {WorstIdleMs,6:0.00} ms | {Frames} frames";
}

/// <summary>
/// The ramp (docs/UNIT_STRESS_TEST.md §3.3): a target that starts at one step and rises by a
/// step every <see cref="StepSeconds"/>. The first <see cref="SettleSeconds"/> of each step are
/// not measured — that is where the new units are placed and plan their first paths — and the
/// rest is. It stops when a step's mean frame rate falls under <see cref="FpsFloor"/>, or after
/// the step at <see cref="Limit"/>. Engine-free: fed frame times and the live count, it says
/// what the field should hold.
/// </summary>
public sealed class StressRamp
{
	private readonly List<RampStep> _steps = new();
	private readonly FrameWindow _frames;
	private float _elapsed;
	private double _aliveSum;
	private double _unitPassSum;
	private int _samples;
	private float _worstPhysics;
	private float _worstIdle;

	public StressRamp(int step, int limit, float stepSeconds, float settleSeconds, float fpsFloor)
	{
		Step = Math.Max(step, 1);
		Limit = Math.Max(limit, 1);
		StepSeconds = MathF.Max(stepSeconds, 1f);
		SettleSeconds = Math.Clamp(settleSeconds, 0f, StepSeconds * 0.9f);
		FpsFloor = MathF.Max(fpsFloor, 0f);

		// Holds every measured frame of a step: the window is the whole step.
		_frames = new FrameWindow(StepSeconds + 1f);
	}

	public int Step { get; }

	public int Limit { get; }

	public float StepSeconds { get; }

	public float SettleSeconds { get; }

	public float FpsFloor { get; }

	/// <summary>Units the field is to hold now; 0 before <see cref="Start"/>.</summary>
	public int Target { get; private set; }

	public bool Running { get; private set; }

	public bool Finished { get; private set; }

	/// <summary>Why it finished; null while it has not.</summary>
	public string StopReason { get; private set; }

	/// <summary>Seconds into the current step.</summary>
	public float StepElapsed => _elapsed;

	/// <summary>Whether the current step is past its settle time and being measured.</summary>
	public bool Measuring => Running && _elapsed >= SettleSeconds;

	public IReadOnlyList<RampStep> Steps => _steps;

	/// <summary>
	/// The largest target whose step held <see cref="FpsFloor"/> or better, every step before
	/// it included; 0 when the first did not.
	/// </summary>
	public int HeldTarget
	{
		get
		{
			int held = 0;
			for (int i = 0; i < _steps.Count; i++)
			{
				if (_steps[i].Fps < FpsFloor)
				{
					break;
				}

				held = _steps[i].Target;
			}

			return held;
		}
	}

	public void Start()
	{
		_steps.Clear();
		Target = Math.Min(Step, Limit);
		Running = true;
		Finished = false;
		StopReason = null;
		BeginStep();
	}

	/// <summary>Stops where it is, keeping the steps measured so far.</summary>
	public void Stop(string reason)
	{
		if (!Running)
		{
			return;
		}

		Running = false;
		Finished = true;
		StopReason = reason;
	}

	/// <summary>
	/// One rendered frame: <paramref name="frameMs"/> long, with <paramref name="alive"/> units
	/// on the field and what it cost besides. True when the step ended on this frame — the target
	/// has moved, or the ramp has finished.
	/// </summary>
	public bool Sample(float frameMs, int alive, FrameCosts costs = default)
	{
		if (!Running)
		{
			return false;
		}

		if (_elapsed >= SettleSeconds)
		{
			_frames.Add(frameMs);
			_aliveSum += alive;
			_unitPassSum += costs.UnitPassMs;
			_worstPhysics = MathF.Max(_worstPhysics, costs.PhysicsWorstMs);
			_worstIdle = MathF.Max(_worstIdle, costs.IdleWorstMs);
			_samples++;
		}

		_elapsed += MathF.Max(frameMs, 0f) / 1000f;
		if (_elapsed < StepSeconds)
		{
			return false;
		}

		var measured = new RampStep(Target, Mean(_aliveSum), _frames.MeanMs, _frames.P95Ms, _frames.WorstMs, _frames.Count,
			Mean(_unitPassSum), _worstPhysics, _worstIdle);
		_steps.Add(measured);

		if (measured.Frames > 0 && measured.Fps < FpsFloor)
		{
			Stop($"{measured.Fps:0.0} fps at {Target} units is under the {FpsFloor:0} fps floor");
			return true;
		}

		if (Target >= Limit)
		{
			Stop($"the field's limit, {Limit} units, held {measured.Fps:0.0} fps");
			return true;
		}

		Target = Math.Min(Target + Step, Limit);
		BeginStep();
		return true;
	}

	private float Mean(double sum) => _samples == 0 ? 0f : (float)(sum / _samples);

	private void BeginStep()
	{
		_elapsed = 0f;
		_aliveSum = 0;
		_unitPassSum = 0;
		_samples = 0;
		_worstPhysics = 0f;
		_worstIdle = 0f;
		_frames.Clear();
	}
}
