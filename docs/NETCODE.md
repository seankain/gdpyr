# gdpyr — Netcode & Simulation Design

Technical companion to [`IMPLEMENTATION_PLAN.md`](IMPLEMENTATION_PLAN.md). Model:
**server-authoritative fixed-tick simulation, client prediction for the local character only,
snapshot interpolation for everything else, server-side lag compensation.**

---

## 1. Authority model

| Entity | Simulated on | Client behaviour |
|---|---|---|
| Local FPS character | Server (authoritative) + owning client (predicted) | Predict, reconcile, replay |
| Remote FPS characters | Server | Interpolate between snapshots (~100 ms behind) |
| Projectiles | Server (authoritative hits) | Deterministic replay from spawn parameters — see §4 |
| RTS units | Server only | Interpolate; never predicted |
| Strategist commands | Server | Immediate local UI acknowledgement, no prediction |
| Resource / ticket / point state | Server | Replicated on change |

Nothing a client says is trusted except its `InputFrame`, and even that is range-clamped on arrival
(move axes to [-1,1], look delta to a sane per-tick maximum, fire rate enforced server-side).

---

## 2. Time

- Simulation tick: **60 Hz**, in `_PhysicsProcess` only. `Engine.PhysicsTicksPerSecond = 60`.
- The server owns tick index `T_s` (a `uint`, wraps in ~2.2 years at 60 Hz — ignore).
- Clients estimate `T_s` from a ping/pong RTT probe every 0.5 s, then run their *input* clock ahead
  by `RTT/2 + jitterBuffer` so inputs arrive just before the server needs them, and run their
  *render* clock behind by ~100 ms so remote entities always have two snapshots to interpolate
  between. This is the standard two-clock arrangement; do not try to collapse it into one.
- The server reports, per client, the depth of its input buffer. The client nudges its clock
  ±1 tick when the buffer drifts outside 2–4 frames. Show this in the net HUD.

```
client input clock   ──►  T_s + RTT/2 + jitter     (inputs land slightly early)
server sim clock     ──►  T_s
client render clock  ──►  T_s − ~100 ms            (interpolation window)
```

---

## 3. Local character prediction

### 3.1 Required refactor

`fps_controller` currently reads `Input.GetVector(...)` inside `UpdateInput`
(`Scripts/Fps/fps_controller.cs:108`) and each `*State.Update` polls
`Input.IsActionJustPressed`. Replace with:

```csharp
public struct InputFrame
{
    public uint  Tick;
    public sbyte MoveX, MoveZ;   // quantized [-127,127]
    public ushort Yaw;           // quantized 0..65535 over 2π
    public short Pitch;          // quantized over ±π/2
    public ushort Buttons;       // jump, crouch, sprint, fire, ads, reload, use, melee, swap1..3
}
```

and make movement a pure function of `(state, input, dt)`:

```csharp
public static CharacterState Step(CharacterState s, in InputFrame input, float dt);
```

The FSM stays — `State.Update` takes an `InputFrame` instead of polling `Input`, and
`StateMachine` is driven from `_PhysicsProcess`, not `_Process`. Viewmodel sway
(`WeaponInit.SwayWeapon`) stays on the render frame; it is cosmetic and must not feed the
simulation.

### 3.2 Loop

```
client tick N:
    sample InputFrame  ─► ring buffer[N]
    apply Step(localState, input[N])           // predicted
    send {N-2, N-1, N} unreliable              // 3-frame redundancy, ~48 bytes

server tick N:
    pop input[N] for each client (repeat last frame if starved)
    Step(state, input)
    record hitbox transforms into history ring (§5)
    broadcast snapshot {tick, per-player state}

client receives snapshot for tick K:
    rewind local state to server state at K
    replay Step() for inputs K+1 .. N          // typically 6–12 ticks at 100ms RTT
    if |replayed - previously predicted| > 1cm : count a misprediction (HUD)
```

`MoveAndSlide()` is re-run during replay. It is deterministic enough for this *provided every
replayed tick uses the same fixed delta* (it does — it reads the physics step internally) *and*
position **and** velocity are both restored before replay. If residual drift shows up as constant
micro-corrections while standing still, do not chase determinism: apply the correction to the
simulation immediately but decay the *visual* offset over ~100 ms so the camera never snaps.

### 3.3 Remote characters

Buffer the last ~0.5 s of snapshots per remote player; render at `now − 100 ms` by interpolating
position and yaw between the two bracketing snapshots. Extrapolate for at most 2 ticks on packet
loss, then freeze. No prediction, no physics.

---

## 4. Ballistics

### 4.1 Port

`pyrrhic/Assets/Scripts/NPC/Weapons/BallisticArc.cs` contains `IntegrationMethods.Heuns`,
`BulletPhysics.CalculateBulletDragAcc`, `CalculateBulletLiftAcc` and `BulletData`
(originally from Erik Nordeus' Unity ballistics tutorial). It is pure math; the port is a
`UnityEngine.Vector3` → `Godot.Vector3` substitution and a move into `Scripts/Sim/Ballistics.cs`.

Drop the `BallisticArc` class itself — precomputing a `List<BallisticArcPoint>` per shot allocates
heavily. Step the integrator live instead.

### 4.2 Analytic projectiles, not rigid bodies

A projectile is **data, not a replicated node**:

```csharp
public struct ProjectileSpawn
{
    public uint    Id;
    public uint    SpawnTick;
    public int     OwnerId;
    public Vector3 Origin;
    public Vector3 Direction;
    public byte    ProjectileDefId;   // mass, radius, BC, muzzle velocity, damage
}
```

Given the spawn record every peer reproduces the identical trajectory with the same integrator.
Per-tick transform replication for bullets — the `PyrProjectile` rigid-body approach in pyrrhic —
is what makes 50 units × automatic weapons unaffordable. One 20-byte spawn message replaces it.

Per server tick, for each live projectile:

```
p1 = Heuns(p0, v0, dt)                         // integrate one tick
hit = IntersectRay(p0 → p1, mask)              // segment cast: no tunnelling at 900 m/s
if hit: resolve damage server-side, broadcast ProjectileHit{Id, point, surface}
if lifetime > max or below kill plane: expire
```

`IntersectRay` on `PhysicsDirectSpaceState3D` is the correct primitive; a 940 m/s round travels
15.7 m per 60 Hz tick, so anything but a segment cast will miss thin geometry and players.

### 4.3 Making it feel instant

- **Shooter**: on trigger pull, spawn a purely cosmetic local tracer immediately from the same
  integrator, and fire the `FireRequest` RPC. When the authoritative `ProjectileSpawn` arrives,
  drop the cosmetic one.
- **Other clients**: receive `ProjectileSpawn` with `SpawnTick` and fast-forward the trajectory to
  their current render tick before displaying it.
- **Lag compensation (prototype)**: on spawn, the server advances the projectile by the shooter's
  one-way latency. The shot then arrives where the shooter aimed. Favours the shooter; this is the
  Halo/Battlefield behaviour and it is one line of code.
- **Lag compensation (upgrade)**: full rewind — test each projectile step against hitbox positions
  at `now − shooterLatency` from the history ring (§5). Build the ring buffer in M2 even if you do
  not use it yet; it is ~80 lines and retrofitting it later is unpleasant.

### 4.4 Muzzle velocity is a gameplay knob, not a realism constant

Vacuum lower bounds (drag only makes flight time and drop *larger*), perpendicular target at
6 m/s sprint:

| Muzzle velocity | Range | Time of flight | Drop | Required lead |
|---|---|---|---|---|
| 940 m/s (real 5.56) | 100 m | 0.106 s | 0.06 m | 0.64 m |
| 940 m/s | 200 m | 0.213 s | 0.22 m | 1.28 m |
| 400 m/s | 100 m | 0.25 s | 0.31 m | 1.5 m |
| 400 m/s | 200 m | 0.50 s | 1.23 m | 3.0 m |
| 250 m/s | 100 m | 0.40 s | 0.79 m | 2.4 m |
| 250 m/s | 200 m | 0.80 s | 3.14 m | 4.8 m |

At realistic velocities and prototype engagement ranges, drop is centimetres and leading is under
one body width — the ballistics work is invisible. If travel time and target leading are meant to be
a *skill expression*, expect to run 200–400 m/s. Expose muzzle velocity, mass, radius and drag on the
`ProjectileDefinition` resource and tune them from playtests.

M2 ships in that band on purpose: 320 m/s for the pistol, 340 for the rifle, 400 for the DMR and
90 for the launcher. All four are guesses until someone has tried to lead a sprinting player with
them.

One correction to the port: the resource field is `DragCoefficient`, the dimensionless C_d of the
drag equation — *not* a G1 ballistic coefficient. `BallisticArc.cs` passed a G1 BC into that slot,
and the two are different quantities, so doing the same would have silently mis-scaled drag.

### 4.5 Accuracy cones

M3 added the second tuning knob the plan's §7 asks for: a half-angle cone about where the shooter
aimed (`Scripts/Sim/Spread.cs`). It is seeded, not random — the seed is
`(ownerId, weaponId, shotIndex)`, and `shotIndex` is advanced inside the deterministic weapon step,
so the server and the owning client derive the same offset for the same round without exchanging a
byte, and the predicted tracer of §4.3 stays on the authoritative line.

Directions are uniform over the spherical cap rather than over the angle: sampling the angle
uniformly piles shots into the middle and makes a wide cone behave like a narrow one.

All four of M2's weapons author **0 degrees** and are therefore unchanged. Cones are a unit
property in M3 (`UnitDefinition.AccuracyConeDegrees`, 2.5° for infantry) because that is what the
milestone needed them for; widening a player weapon is a playtest decision, not a side effect of
the mechanism arriving.

### 4.6 Tests (`Tests/`, no engine required)

- Zero-drag integration matches the closed-form `p(t) = p₀ + v₀t + ½gt²` within 1e-3 over 2 s.
- With drag: speed is strictly decreasing; drop is strictly increasing with range; flight time
  exceeds the vacuum solution.
- Integrator is step-size stable: 1 ms vs 0.5 ms steps agree within 1 cm at 300 m.
- Determinism: same spawn record → bit-identical trajectory across 1000 runs.

---

## 5. Hitbox history

Server-side ring buffer, 500 ms (32 ticks at 60 Hz, a power of two so the index is a mask), per
damageable entity: the capsule plus the tick index. Fixed-size array, no allocation.

Built in M2 (`Scripts/Sim/HitboxHistory.cs`). **Melee already rewinds through it** — a swing is
instantaneous, so testing it against where the attacker's client saw the target is exactly right.
Projectiles do not: they use §4.3's cheap compensation instead, and moving them to a full rewind is
the upgrade this ring exists to make cheap.

RTS units keep no history and are tested against their current capsule. They have no client whose
view of them a hit has to be validated against, and at 4.5 m/s the half second this ring holds is
worth two metres of a capsule 0.8 m wide — which is to say, a rewind against a unit would change
the answer more often than it corrected it. Player hits are resolved against these capsules in
engine-free code (`Scripts/Sim/Hitbox.cs`), not by a physics query, which is why characters moved
to their own collision layer in M2 — a projectile's world query must not find them. Also the single
best debugging aid you will have: dump it on a disputed kill.

---

## 6. RTS replication and fog of war

### 6.1 Units

**M3 took the custom packed replicator, not `MultiplayerSpawner` + `MultiplayerSynchronizer`.**
One unreliable message at 20 Hz carrying an array of `{id, def, quantized pos, yaw, state+team,
health%}` — 13 bytes each, 837 bytes for a full field of 64 — plus a reliable spawn and despawn per
unit per life. `Scripts/Sim/UnitSnapshotCodec.cs`, decoded into a `SnapshotInterpolator` per unit,
the same one remote characters use. Units never simulate on clients.

The original plan said to use the built-in nodes because they would be the least code. In this
codebase they are not, and there were three reasons to change:

1. every other message here is already a hand-packed codec with a total decoder and a unit test, so
   a second replication mechanism alongside them is a second set of rules to remember;
2. the fog of war in §6.2 is a filter over *which records go in which peer's packet*, which is a
   loop bound in the broadcast and a per-node API call in the alternative;
3. it closes the risk the plan flags against the built-in route — that per-peer visibility gates
   synchronization but perhaps not spawning — because a unit a peer is not told about has no record
   in its packet and therefore no node. M4 does not need the spike.

Cost: ~180 lines of codec and roster plumbing, and a manual roster replay to joining peers.
Budget check at 50 units: 50 × 13 B × 20 Hz ≈ **13 KB/s** per client, against the ~25 KB/s §7
budgets for everything.

### 6.2 Fog of war

Fog of war is an **anti-cheat property, not a shader**. If the strategist client receives the
position, a modified client can display it. Filter on the server.

With §6.1's packed replicator the shape changes but the property does not: the filter is applied
while building each peer's packet, in `UnitManager.BroadcastUnitSnapshot`, and a hidden entity is a
record that is not written. There is nothing to spike — a peer that is never sent a spawn message
has no node to read a position off.

```
// VisibilityService, server-only, 5–10 Hz
foreach (var peer in strategistPeers)
    foreach (var e in groundTeamEntities)
    {
        bool seen = sensors.AnyFriendlyUnitWithin(e.Position, peer.Team);  // + optional LOS ray
        if (seen) lastKnown[peer.Id][e.Id] = (e.Position, currentTick);
    }
// then: RpcId(peer.Id, ServerSnapshot, Encode(tick, visibleRecordsFor(peer)))
```

- Sensor radius lives on `UnitDefinition` (M3 ships it at 45 m for infantry); different unit types
  get different radii, and that is the lever that makes scouting a real decision.
- Client renders `lastKnown` as a ghost marker that fades after N seconds. Stale information is what
  makes the role interesting — do not hide it, decay it.
- The player snapshot needs the same treatment and is the harder half: it is one broadcast today
  (`PlayerManager.BroadcastSnapshot`) and becomes one packet per strategist peer.

### 6.3 Commands

Plain RPCs, reliable, server-validated. As shipped in M3
(`Scripts/Rts/UnitManager.cs`):

```csharp
[Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false,
     TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
private void ClientIssueOrder(int[] unitIds, byte kind, Vector3 target, int targetOwnerId)
```

Server checks, in order: the order type is one of Move/Attack/Patrol/Defend/Stop, the sender is a
strategist, and each id names a live unit on the sender's side. Anything that fails is counted in
`RejectedOrders` — visible in the debug HUD, and anything but 0 on a server with honest clients is
a bug.

`targetOwnerId` is an `OwnerId` (§4.2): positive names a peer, negative a unit, 0 nothing. The
same folded id space that lets one `ProjectileSpawn` field name either kind of shooter lets one
order field name either kind of target.

There are three more, all with the same shape and the same strategist check: `ClientQueueUnit`,
`ClientCancelBuild` and `ClientSetRally`, each naming a barracks by its index in the map's
`barracks` group sorted by name — an order every peer derives from the same scene, like spawn
points.

Not checked: whether the target point is on the navmesh. A unit told to walk into a wall paths as
close as it can and stops, which is the same thing that happens when a wall is built across its
route, so there is no second behaviour to write.

---

## 7. Message set

| Message | Direction | Transfer | Rate | ~Size |
|---|---|---|---|---|
| `Input` (3 frames) | C→S | Unreliable | 60 Hz | ~48 B |
| `PlayerSnapshot` (all players) | S→C | Unreliable | 30 Hz | 29 B × players |
| `UnitSnapshot` (visible units) | S→C | Unreliable | 20 Hz | 5 B + 13 B × units |
| `ServerSpawnUnit` / `ServerDespawnUnit` | S→C | Reliable | per unit per life | ~24 B / 8 B |
| `ProjectileSpawn` | S→C | Reliable | per shot | 23 B |
| `ProjectileHit` | S→C | Reliable | per hit | 15 B |
| `ClientIssueOrder` | C→S | Reliable | per click | ~16 B + 4 B/unit |
| `ServerBarracksState` | S→C | Reliable | 5 Hz per barracks | ~8 B |
| `MatchState` (tickets, points) | S→C | Reliable | on change | ~22 B |
| `ClockProbe` / `ClockReply` | both | Unreliable | 2 Hz | ~12 B |

Quantize positions to three `int16` at 1 cm resolution (±327 m covers the map) and angles to
`uint16`. Rough budget at 8 players + 50 visible units, at the rates actually shipped (players
30 Hz, units 20 Hz):

- Downstream per client: 8 × 29 B × 30 Hz ≈ 7 KB/s + (5 + 50 × 13) B × 20 Hz ≈ 13 KB/s ≈
  **20 KB/s**, plus whatever is being shot.
- Upstream per client: ≈ **3 KB/s**.

Comfortable. The unit snapshot is now the larger half, so it is the one to thin first — drop it to
10 Hz, or stop sending records for units that have not moved since the last one.

---

## 8. Debug HUD (build in M1, not later)

In the existing `Debug` panel (`Scripts/Ui/Debug.cs`, toggled with `` ` ``):

RTT · client/server tick delta · server-side input buffer depth · mispredictions per second ·
mean/max prediction error · bytes in/out per second · live projectile count · replicated unit count ·
server frame time.

M3 added the unit half: live units against the cap, units built and lost, whether the navigation
mesh baked, the strategist's points, how many strategists are seated, and — on a server — orders
refused. "Live units against the cap" is the number that says whether §6 of the implementation
plan's 50-unit budget is real; "no navmesh" next to it is why twenty riflemen are walking into a
wall.

Every hard bug in this system is a timing bug. Without these numbers you are guessing.
