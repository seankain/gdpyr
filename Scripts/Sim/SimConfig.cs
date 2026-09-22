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

	// ---- combat (docs/NETCODE.md §4, §5) -----------------------------------

	/// <summary>
	/// Density of air at 20 °C and 101.325 kPa [kg/m³]. The value the ported
	/// ballistics used; a protocol constant, because two peers integrating a
	/// trajectory in different air would disagree about where a bullet went.
	/// </summary>
	public const float AirDensity = 1.204f;

	/// <summary>
	/// Heun steps per simulation tick. Eight is 2.08 ms per step at 60 Hz, which
	/// puts the integrator well inside the step-size stability the design asks for
	/// (docs/NETCODE.md §4.6); hit detection still uses the whole tick's chord.
	/// </summary>
	public const int ProjectileSubSteps = 8;

	/// <summary>
	/// Projectiles in flight at once, server-wide. Pre-sized and never grown: no
	/// per-tick allocation in the projectile path (docs/IMPLEMENTATION_PLAN.md §3).
	/// </summary>
	public const int MaxLiveProjectiles = 128;

	/// <summary>
	/// Seconds a projectile lives before it expires unheard-of. At 250 m/s that is
	/// two kilometres, well past the map.
	/// </summary>
	public const float MaxProjectileLifetimeSeconds = 8f;

	/// <summary>Below this height a projectile has left the map and is dropped.</summary>
	public const float ProjectileKillPlaneY = -50f;

	/// <summary>
	/// Ticks a projectile may be fast-forwarded through in one step. A client
	/// catches a newly announced projectile up to its render clock, which is at most
	/// the interpolation delay plus its latency; the cap is what keeps a bogus
	/// spawn tick from costing a second of integration in one frame.
	/// </summary>
	public const int MaxProjectileCatchUpTicks = 60;

	/// <summary>
	/// Cap on the lag compensation a shooter can be given, in ticks: 15 ticks is
	/// 250 ms of one-way latency (docs/NETCODE.md §4.3). It is a cap and not a
	/// measurement because the number derives from what the *client* reports its
	/// RTT to be, and a client can lie.
	/// </summary>
	public const int MaxLagCompensationTicks = 15;

	/// <summary>
	/// Hitbox history retained per damageable entity (docs/NETCODE.md §5): 32 ticks
	/// is 533 ms at 60 Hz, and a power of two so the ring index is a mask.
	/// </summary>
	public const int HitboxHistoryTicks = 32;

	/// <summary>Full health. One byte on the wire, so the ceiling is 255.</summary>
	public const int MaxHealth = 100;

	/// <summary>
	/// Weapon definitions addressable by a single byte on the wire. The catalog's
	/// order is a protocol constant: an id means nothing unless both ends resolve
	/// it to the same weapon.
	/// </summary>
	public const int MaxWeaponDefinitions = 32;

	/// <summary>Weapon slots a ground-force loadout carries: melee, sidearm, large.</summary>
	public const int WeaponSlots = 3;

	// ---- units (docs/NETCODE.md §6.1) --------------------------------------

	/// <summary>
	/// Units alive at once, server-wide. The design budgets for fifty
	/// (docs/IMPLEMENTATION_PLAN.md §M3); the pool is sized a little over that so
	/// the cap is a decision and not an accident, and it also bounds one unit
	/// snapshot to 837 bytes — one datagram.
	/// </summary>
	public const int MaxUnits = 64;

	/// <summary>
	/// Unit definitions addressable by one byte on the wire. As with weapons, the
	/// catalog's order is a protocol constant.
	/// </summary>
	public const int MaxUnitDefinitions = 8;

	/// <summary>
	/// Unit snapshot rate. Units are never predicted and interpolated on arrival,
	/// so a third of the player rate is invisible and a third of the bandwidth
	/// (docs/NETCODE.md §6.1).
	/// </summary>
	public const int UnitSnapshotRate = 20;

	/// <summary>Ticks between unit broadcasts. <see cref="TickRate"/> must divide evenly by <see cref="UnitSnapshotRate"/>.</summary>
	public const int UnitSnapshotIntervalTicks = TickRate / UnitSnapshotRate;

	/// <summary>
	/// Ticks between a unit re-scanning for a target. Acquisition is the expensive
	/// part of a unit's tick, and a sixth of a second of staleness is invisible next
	/// to how long it takes one to walk anywhere. Units are staggered across this
	/// window so the cost does not land on one tick.
	/// </summary>
	public const int UnitTargetRefreshTicks = 10;

	/// <summary>Units one barracks may have on order at once.</summary>
	public const int MaxBuildQueue = 24;

	/// <summary>
	/// Barracks one map may carry. A barracks is named by its index in the map's
	/// <c>barracks</c> group on the wire, exactly as a resource node is, so this is
	/// the width of that name rather than a level-design opinion — and it is the
	/// width of the barracks block in a strategist's observation
	/// (docs/AGENT_API.md §6.2).
	/// </summary>
	public const int MaxBarracks = 4;

	/// <summary>
	/// Barracks defences one map may carry — the guns and mortars that keep the
	/// ground force off the door (<see cref="DefenseSim"/>). A defence names itself
	/// on the wire as the owner of every round it fires, by its index in the map's
	/// <c>barracks_defense</c> group (<see cref="OwnerId.ForDefense"/>), so this is
	/// the width of that range of the owner-id space rather than a level-design
	/// opinion.
	/// </summary>
	public const int MaxDefenses = 16;

	// ---- fog of war (docs/NETCODE.md §6.2) ---------------------------------

	/// <summary>
	/// How often the server recomputes what each side can see: every 8 ticks is
	/// 7.5 Hz, inside the 5–10 Hz the design asks for
	/// (docs/IMPLEMENTATION_PLAN.md §M4). It is a cost, not a resolution — a unit
	/// walks 0.6 m between two of these, and the snapshot that carries the result
	/// still goes out at <see cref="SnapshotRate"/>.
	/// </summary>
	public const int FogRefreshIntervalTicks = 8;

	/// <summary>
	/// Line-of-sight rays a contact is worth per refresh. Fifty units may cover one
	/// player; only the nearest few are asked whether a wall is in the way, because
	/// each answer costs a ray against the physics world (<see cref="VisionField.Gather"/>).
	/// </summary>
	public const int FogLineOfSightCandidates = 3;

	/// <summary>
	/// How far behind the newest snapshot a client has decoded a peer's own record
	/// may fall before the gap means the fog (docs/NETCODE.md §6.2).
	///
	/// Measured against the newest *packet* rather than against the clock, which is
	/// what makes it exact: a snapshot carries the whole of what its peer is allowed
	/// to see, so a record missing from one that arrived was withheld, and one that
	/// never arrived takes the reference with it. It therefore has nothing to do with
	/// latency or with packet loss, and only has to cover an unreliable datagram
	/// arriving out of order — four ticks is two snapshot intervals at
	/// <see cref="SnapshotRate"/>.
	/// </summary>
	public const int FogContactTimeoutTicks = 4;

	/// <summary>
	/// How long a lost contact's ghost takes to fade from where it was last seen.
	/// Eight seconds: long enough to act on, short enough that acting on it late is
	/// a mistake. Client-side presentation only.
	/// </summary>
	public const int GhostLifetimeTicks = TickRate * 8;

	// ---- economy (docs/IMPLEMENTATION_PLAN.md §M5) -------------------------

	/// <summary>
	/// Resource nodes one map may carry. A node is named by its index in the map's
	/// <c>resource_node</c> group on the wire, exactly as a barracks is, so this is
	/// the width of that name and not a level-design opinion.
	/// </summary>
	public const int MaxResourceNodes = 8;

	/// <summary>
	/// Ticks between two occupancy scans of every node: every 4 ticks is 15 Hz.
	///
	/// A scan is eight nodes against six players and fifty units — cheap, but not
	/// free sixty times a second, and a capture meter that advances four ticks at a
	/// time is a capture meter nobody can see advancing four ticks at a time.
	/// Capture progress is counted in these, so the server and a client's bar agree
	/// about what "half" means.
	/// </summary>
	public const int CaptureScanIntervalTicks = 4;

	/// <summary>How often a node tells everyone who holds it. 5 Hz is a progress bar.</summary>
	public const int NodeReportIntervalTicks = TickRate / 5;

	// ---- emplacements (docs/IMPLEMENTATION_PLAN.md §M5) --------------------

	/// <summary>
	/// Heavy guns one map may carry, named by index in the <c>emplacement</c> group
	/// exactly as a barracks and a resource node are.
	/// </summary>
	public const int MaxEmplacements = 8;

	/// <summary>Ammunition cans one map may carry, named the same way.</summary>
	public const int MaxAmmoCans = 12;

	/// <summary>
	/// How close a player has to be to pick a gun up, deploy onto it or mount it.
	/// A protocol constant rather than a knob on the node: the owning client
	/// predicts its own use press against the same radius the server will check it
	/// against, and two answers would be a prediction that never reconciles.
	/// </summary>
	public const float EmplacementReachMeters = 2.5f;

	/// <summary>
	/// What carrying a heavy gun does to a player's speed. Every movement state
	/// scales by this, so sprinting with one is slower than walking without it.
	/// Prediction-relevant for the same reason the reach is.
	/// </summary>
	public const float CarryMoveScale = 0.55f;

	/// <summary>What carrying an ammunition can does. Lighter than a gun, and still worth noticing.</summary>
	public const float AmmoCanMoveScale = 0.8f;

	/// <summary>
	/// How far either side of where it was deployed a mounted gun will traverse
	/// [rad]. A heavy gun that turns like a rifle is a rifle, and where it was put
	/// down stops being a decision.
	/// </summary>
	public const float EmplacementTraverseRadians = 0.7854f;

	/// <summary>How far a mounted gun may be depressed or elevated [rad].</summary>
	public const float EmplacementElevationRadians = 0.5236f;

	/// <summary>Where the barrel sits above the gun's feet [m]. Shots leave from here.</summary>
	public const float EmplacementMuzzleHeightMeters = 1.1f;

	/// <summary>
	/// Ticks between a can being spent and another appearing where it started.
	/// Resupply is meant to be a walk back to the spawn, not a walk back to the
	/// spawn once.
	/// </summary>
	public const int AmmoCanRespawnTicks = TickRate * 20;

	// ---- wire quantization (docs/NETCODE.md §7) ----------------------------

	/// <summary>Position resolution on the wire, in metres.</summary>
	public const float PositionQuantum = 0.01f;

	/// <summary>
	/// Half the representable position range: int16 centimetres covers ±327.67 m,
	/// which has to contain the whole map plus the kill plane.
	/// </summary>
	public const float MaxPositionMeters = 327.67f;
}
