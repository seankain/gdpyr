namespace Gdpyr.Sim;

/// <summary>
/// Constants the whole simulation agrees on. Engine-free by design: everything
/// under Scripts/Sim is unit-testable without Godot (docs/IMPLEMENTATION_PLAN.md §3).
///
/// Server and clients must be built from the same values. Every one of these is a
/// protocol constant, not a tuning knob: changing one changes the meaning of the
/// bytes on the wire or of a recorded tick index.
/// </summary>
public static class SimConfig
{
	/// <summary>
	/// Fixed simulation rate, in hertz. The server owns the tick; clients predict
	/// against it. Changing this changes the meaning of every recorded tick index,
	/// so server and clients must be built from the same value
	/// (docs/NETCODE.md §2).
	/// </summary>
	public const int TickRate = 60;

	/// <summary>Seconds per simulation tick.</summary>
	public const float TickDelta = 1.0f / TickRate;

	// ---- replication -------------------------------------------------------

	/// <summary>
	/// Player snapshot rate. 30 Hz is the first thing to drop if bandwidth gets
	/// tight; interpolation hides it (docs/NETCODE.md §7).
	/// </summary>
	public const int SnapshotRate = 30;

	/// <summary>Ticks between snapshot broadcasts. <see cref="TickRate"/> must divide evenly by <see cref="SnapshotRate"/>.</summary>
	public const int SnapshotIntervalTicks = TickRate / SnapshotRate;

	/// <summary>
	/// How far behind its server-tick estimate a client renders remote entities, so
	/// there are always two snapshots to interpolate between: 6 ticks = 100 ms at
	/// 60 Hz (docs/NETCODE.md §2).
	/// </summary>
	public const int InterpolationDelayTicks = 6;

	/// <summary>Remote entities extrapolate this far past the newest snapshot, then freeze (docs/NETCODE.md §3.3).</summary>
	public const int MaxExtrapolationTicks = 2;

	/// <summary>Snapshots retained per remote entity: 32 ticks ≈ 0.5 s at 60 Hz.</summary>
	public const int SnapshotBufferTicks = 32;

	// ---- client clocks (docs/NETCODE.md §2) --------------------------------

	/// <summary>
	/// Ticks a client runs its input clock ahead of <c>T_s + RTT/2</c>, so inputs
	/// land slightly early rather than slightly late.
	/// </summary>
	public const int JitterBufferTicks = 3;

	/// <summary>Server-side input buffer depth the client steers towards, inclusive.</summary>
	public const int MinInputBufferDepth = 2;

	/// <summary>See <see cref="MinInputBufferDepth"/>.</summary>
	public const int MaxInputBufferDepth = 4;

	/// <summary>Drift beyond this many ticks is a hard clock resync rather than a ±1 nudge.</summary>
	public const int ClockResyncThresholdTicks = 6;

	/// <summary>
	/// Floor and ceiling on the wait between input-clock nudges. The buffer depth a
	/// snapshot reports is a round trip old, so nudging again before that has
	/// elapsed corrects for the same error twice and sets the clock oscillating.
	/// The actual wait is one round trip plus the jitter margin.
	/// </summary>
	public const int MinNudgeCooldownTicks = 8;

	/// <summary>See <see cref="MinNudgeCooldownTicks"/>.</summary>
	public const int MaxNudgeCooldownTicks = 60;

	/// <summary>Interval between RTT probes. 2 Hz costs ~24 B/s and tracks a route change within a second.</summary>
	public const float ClockProbeIntervalSeconds = 0.5f;

	/// <summary>Weight of a new RTT sample in the smoothed estimate.</summary>
	public const float RttSmoothing = 0.2f;

	// ---- prediction (docs/NETCODE.md §3) -----------------------------------

	/// <summary>
	/// Inputs and predicted states retained for replay. A power of two so the ring
	/// index is a mask. 128 ticks ≈ 2.1 s, far more than any playable RTT.
	/// </summary>
	public const int InputBufferTicks = 128;

	/// <summary>Redundant input frames per packet: a single lost packet costs nothing (docs/NETCODE.md §3.2).</summary>
	public const int InputRedundancy = 3;

	/// <summary>
	/// Position error above which a server correction is applied and counted as a
	/// misprediction. 1 cm — below this the correction costs more than it fixes.
	/// </summary>
	public const float MispredictionThreshold = 0.01f;

	/// <summary>Velocity error (m/s) that forces a correction even when the position agrees.</summary>
	public const float VelocityCorrectionThreshold = 0.5f;

	/// <summary>
	/// Corrections larger than this snap the camera instead of being smoothed away:
	/// a teleport, a respawn or a long stall is not a misprediction to hide.
	/// </summary>
	public const float MaxSmoothedErrorMeters = 1.0f;

	/// <summary>
	/// Half-life of the visual correction offset. ~0.05 s halves the error every
	/// 50 ms, so a correction is invisible within ~100 ms (docs/NETCODE.md §3.2).
	/// </summary>
	public const float VisualErrorHalfLifeSeconds = 0.05f;

	// ---- wire quantization (docs/NETCODE.md §7) ----------------------------

	/// <summary>Position resolution on the wire, in metres.</summary>
	public const float PositionQuantum = 0.01f;

	/// <summary>
	/// Half the representable position range: int16 centimetres covers ±327.67 m,
	/// which has to contain the whole map plus the kill plane.
	/// </summary>
	public const float MaxPositionMeters = 327.67f;
}
