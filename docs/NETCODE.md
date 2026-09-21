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
   loop bound in the broadcast and a per-node API call in the alternative — M4 put that filter on
   the player snapshot rather than on this one (§6.2 says why), and it is the same loop in the
   same shape;
3. it closes the risk the plan flags against the built-in route — that per-peer visibility gates
   synchronization but perhaps not spawning — because a unit a peer is not told about has no record
   in its packet and therefore no node. M4 does not need the spike.

Cost: ~180 lines of codec and roster plumbing, and a manual roster replay to joining peers.
Budget check at 50 units: 50 × 13 B × 20 Hz ≈ **13 KB/s** per client, against the ~25 KB/s §7
budgets for everything.

### 6.2 Fog of war

Fog of war is an **anti-cheat property, not a shader**. If the strategist client receives the
position, a modified client can display it. Filter on the server.

**M4 shipped it as a filter over the *player* snapshot**, not the unit one. §6.1 chose the packed
replicator partly to make this a loop bound rather than a per-node API call, and it is — but the
loop it turned out to be bound in is `PlayerManager.BroadcastSnapshot`, which is where the asymmetry
actually lives. Every unit on the field is the strategist's, so filtering `UnitSnapshot` per peer
would hide nothing from the side that owns them; the only thing it could hide is the strategist's
army from the ground force, and a unit blinking out of an FPS at forty metres because a 7.5 Hz
sphere test said so is a worse defect than the radar it would be closing. Units stay one broadcast.

What ships:

```
// VisibilityService, server-only, every SimConfig.FogRefreshIntervalTicks (8 ticks, 7.5 Hz)
sensors.Clear();
foreach (var unit in strategistUnits)      sensors.Add(unit.EyePosition, unit.SensorRadius);
foreach (var player in groundForcePlayers) contact[player] = sensors.Sees(player.Chest)
                                                             && anyClearLineOfSight(player.Chest);
// then, per peer, at SnapshotRate:
RpcId(peer, ServerSnapshot, Encode(tick, recordsVisibleTo(peer)))
```

- **Visibility is a property of the side, not of a peer.** Two strategists share an army and
  therefore share its eyes, so this is one recomputation and one `IsVisible` table however many
  strategists are seated.
- **Three records always go in a strategist's packet**: their own (their prediction is reconciled
  against it and their health comes from it), every other strategist's, and every visible
  ground-force player's. Everything else is a record that is not written.
- **Sensor radius lives on `UnitDefinition`** (45 m for infantry); different unit types get
  different radii, and that is the lever that makes scouting a real decision.
- **Line of sight is bounded, not free.** Fifty units may cover one player; only the
  `FogLineOfSightCandidates` nearest are asked whether a wall is in the way
  (`VisionField.Gather`), because each answer costs a ray against the physics world. Six players
  against three candidates at 7.5 Hz is ~135 rays a second, against ~2 250 for testing every
  sensor that covers.
- **Projectile messages are filtered by where they happened**, not by who fired them. A tracer
  leaving a muzzle says where somebody is just as loudly as a position record, and a flanker who
  opened fire would otherwise light themselves up on the strategist's screen from across the map.
  The test is `VisibilityService.Covers` — range only, no ray: gunfire thirty metres from a
  rifleman is something the man on the ground notices whether or not there is a wall between them,
  and a ray per shot would be a ray per shot. It falls out of this that a unit's own shots and hits
  on a unit are always sent, because the unit is a sensor standing at that point.
- **A client is never told it has lost contact.** There is no message for it, and none is needed:
  a snapshot carries the whole of what its peer may see, so a record missing from a packet that
  *arrived* was withheld on purpose. `Fog.IsLost` measures each peer's newest record against the
  newest packet the client has decoded, which makes the inference exact rather than a latency
  guess — a lost datagram takes the reference with it instead of aging anything, so loss and ping
  cannot manufacture a ghost. The timeout is four ticks, which covers an unreliable datagram
  arriving out of order and nothing else.
- **Stale information is what makes the role interesting, so it decays rather than disappearing.**
  The body is hidden the moment the contact is lost; `SelectionOverlay` keeps drawing a marker at
  the position it froze at, fading over `GhostLifetimeTicks` (8 s) and then gone. A right-click
  picks from those contacts rather than from the roster, so an order against a ghost is an order
  to where they were last seen — and a player whose ghost has faded cannot be clicked at all.
- **A listen host draws a curtain, not a filter.** The authority *is* the state and cannot be
  filtered against itself, so `PlayerManager.ApplyLocalFog` hides the characters instead. It is
  presentation and not protection, which is exactly why the plan verifies every milestone against
  the dedicated server.

Two things it deliberately does not do. The ground force is not fogged at all — they see each
other and the units with their eyes, the client needs the node to draw it, and a snapshot gap on
that side is packet loss rather than the fog. And a player who changes side while hidden keeps
their old team bit on the strategist's client until the next time they are seen, because the bit
rides the record that is being withheld; the cost is that an unseen player stays unseen, and it
corrects itself within one snapshot of them reappearing.

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
| `PlayerSnapshot` (visible players) | S→C | Unreliable | 30 Hz | 29 B × players |
| `UnitSnapshot` (visible units) | S→C | Unreliable | 20 Hz | 5 B + 13 B × units |
| `ServerSpawnUnit` / `ServerDespawnUnit` | S→C | Reliable | per unit per life | ~24 B / 8 B |
| `ProjectileSpawn` | S→C | Reliable | per shot | 23 B |
| `ProjectileHit` | S→C | Reliable | per hit | 15 B |
| `ClientIssueOrder` | C→S | Reliable | per click | ~16 B + 4 B/unit |
| `ServerBarracksState` | S→C | Reliable | 5 Hz per barracks | ~8 B |
| `MatchState` (tickets, points) | S→C | Reliable | on change | ~22 B |
| `ServerNodeState` | S→C | Reliable | 5 Hz per *changed* node | ~8 B |
| `ServerGunState` / `ServerCanState` | S→C | Reliable | per transition | ~32 B / ~24 B |
| `ServerRoundSummary` (scoreboard) | S→C | Reliable | once per round | 16 B + 9 B/player |
| `ClockProbe` / `ClockReply` | both | Unreliable | 2 Hz | ~12 B |

Both snapshots say *visible*, and since M4 the player one means it: a strategist peer is sent its
own record, the other strategists' and whichever of the ground force its units can see (§6.2), so
it is one packet per strategist rather than one broadcast. The ground force still shares a single
packet, and a round with nobody in the chair is still a single broadcast. The fog only ever
subtracts, so the budget below is the ceiling.

Quantize positions to three `int16` at 1 cm resolution (±327 m covers the map) and angles to
`uint16`. Rough budget at 8 players + 50 visible units, at the rates actually shipped (players
30 Hz, units 20 Hz):

- Downstream per client: 8 × 29 B × 30 Hz ≈ 7 KB/s + (5 + 50 × 13) B × 20 Hz ≈ 13 KB/s ≈
  **20 KB/s**, plus whatever is being shot.
- Upstream per client: ≈ **3 KB/s**.

Comfortable. The unit snapshot is now the larger half, so it is the one to thin first — drop it to
10 Hz, or stop sending records for units that have not moved since the last one.

M5's four messages change none of that. Three of them are state rather than samples — who holds a
node, where a gun is, what the round ended as — and cost bytes only when something happens; the
node one is additionally filtered to the nodes whose state actually changed since the last report,
so eight nodes at a standstill cost nothing at all (§10.1).

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

M5 added the economy and the guns: how many nodes each side holds and how many are contested, what
the nodes have paid the strategist against what is left of it, and how many guns are manned,
carried and fed. "0 contested" next to a live round says nobody is walking out to a node; "0 cans
spent" says nobody is feeding a gun. Both are findings rather than bugs, and both are invisible
without a number.

M4 added the fog. On the authority: how many of the ground force its sensors have out of how many
are on the field, how many sensors that is, how many records and messages the filter has kept off
the wire, and how many line-of-sight rays it has spent. "0 withheld" next to a live round with a
strategist in it means the fog is not being applied; "seen" climbing towards "tracked" as an attack
goes in is what scouting looks like as a number. On a strategist's client, the other side of it:
how many players it is still being told about, and how many it is drawing from memory.

Every hard bug in this system is a timing bug. Without these numbers you are guessing.

---

## 9. Computer players

A bot is a player. It holds a peer id, a roster slot, a character, a loadout and a place in the
player snapshot exactly as a person does; the only thing that differs is where its `InputFrame`
comes from — `BotDirector` rather than a socket (`Scripts/Net/PlayerManager.cs`, one branch in the
server's roster loop).

That is the whole design, and it is what keeps the feature out of the netcode:

- **No new message.** A bot arrives on a client through the same reliable `SpawnPlayer` and is
  interpolated from the same snapshot as any other remote character. A client cannot tell the
  difference, and does not need to.
- **No new authority.** A bot's shots go through `WeaponSim` and `ProjectileSim`, its hits through
  the same resolution, its deaths through the same ticket pool. A computer strategist's orders go
  through the same `ApplyOrder`/`ApplyBuild` a client's RPC lands in, so it is subject to every
  ownership check a person is (§6.3).
- **No lag compensation.** A bot has no client and therefore no latency to owe, exactly as a unit
  has none (§4.3). Its `LagCompensationTicks` stays 0 by virtue of never appearing in the clock
  probe's table.

Cost on the wire: 29 B per bot per snapshot at 30 Hz — **0.9 KB/s down per client per bot**. Seven
bots (a full 6 + 1) is ~6 KB/s, which is the same as seven more people and is already inside the
budget in §7 for that reason. Bots are also bounded by the same `SnapshotCodec.MaxPlayers` as
everybody else: `BotFillPolicy` will not spawn one that the broadcast could not carry.

What a bot knows is deliberately not everything the server does. A ground bot acquires only what it
has line of sight to, at its own sensor radius. A computer strategist has no sensor of its own at
all: its intel is what its units can see, and when it has seen nothing it sweeps the ground force's
spawn areas, which are static map geometry a human strategist can see on screen anyway.

M4 made that literal. Until then the bot read `Unit.TargetOwnerId` — what a unit had *acquired*,
which is a little narrower than what the side can see; it now reads the same `VisibilityService`
the human strategist's packet is filtered through, so the two are behind one fog rather than two,
and a change to what the fog shows changes both. It reads the live contact and not the last known
position on purpose: a bot that chased ghosts would be a different opponent from the one this
exists to be.

---

## 10. Economy, emplacements and the scoreboard

M5 added three things that are neither a per-tick sample nor a projectile, and all three are on the
wire as **state**: sent when it changes, reliable, and absent the rest of the time.

### 10.1 Resource nodes

A node is a map node in the `resource_node` group, named on the wire by its index in that group
sorted by name — the same arrangement barracks and spawn points use, and for the same reason: the
index has to be something every peer derives from the same scene rather than something a message
has to carry a path for.

The capture itself is engine-free (`Scripts/Sim/Capture.cs`) and counted in *ticks*, not seconds:

```
every SimConfig.CaptureScanIntervalTicks (4 ticks, 15 Hz), per node:
    ground      = living ground-force players inside the radius
    strategist  = living strategist units inside the radius
    contested   = both
    progress   += one scan, towards whichever side is there alone
    income      = owner is the strategist AND no ground-force body is standing on it
```

Four consequences worth stating, because each is a design decision rather than an implementation
detail:

- **Presence captures, not builders.** §5 of the implementation plan cuts builder units from the
  first pass; a proximity test exercises the same economy loop with far less code, and what is
  being measured is whether the loop is worth playing at all.
- **One rifleman stops the money.** Denial does not need a capture: standing on a strategist's node
  stops it paying from the first scan, and the eight seconds of capture on top of that are what
  it takes to make the node yours. This is the whole of the ground force's reason to leave the
  fight and walk somewhere.
- **Contested freezes rather than resets.** A firefight on a pad that one side walks away from
  leaves the other where it got to.
- **Income is paid on a tick index**, never accumulated into a float. Two peers have to agree what
  the strategist has; sixty additions a second of `0.41666` would not.

Replication is one ~8-byte reliable message per node whose state changed, at 5 Hz, plus the whole
set to a joining peer. There is **no fog over it**: both sides can see who is standing on a pad in
any RTS anyone has played, and a node nobody can find is not an objective.

### 10.2 Heavy guns: what is predicted, and what is not

A gun and an ammunition can are map nodes in the `emplacement` and `ammo_can` groups, named by
index the same way. The use bit has been on the wire since M1 (`InputButtons.Use`); M5 is where the
device end of it is finally sampled and where it means something.

One key does everything, which needs two intents, and both come out of the recorded frames rather
than out of a timer (`UseTracker`): a **tap** is every interaction that leaves things where they
are — mount, dismount, deploy, load a can — and a **hold** of half a second is the one that picks
something up and walks off with it. The table is `EmplacementSim.Resolve`, engine-free and tested.

**Transitions are not predicted. Firing is.**

That split is the interesting part:

- A transition happens perhaps four times in a round. Predicting one buys a round trip and costs a
  client that has mounted a gun the server never gave it — with *no message to correct it*, because
  a rejection is not a transition and only transitions are on the wire. The same round trip is what
  the plan already accepts for every RTS order (§2).
- Firing happens sixty times a second, and the cost of not predicting it is the thing §4.3 exists
  to avoid. So a mounted gun goes through the identical `WeaponSim.Step` a rifle does, over the
  identical input frames, on the server and on the gunner's client — tracer and belt count on the
  tick the trigger was pulled, confirmed a round trip later.
- Two things differ from a rifle: the round leaves the **muzzle** rather than the gunner's eye, and
  the direction is clamped to the gun's **traverse arc** about the yaw it was deployed at
  (`EmplacementSim.Traverse`). Both ends compute that from the same replicated `DeployYaw`, so the
  predicted tracer is still on the authoritative line.
- The gunner's client keeps its own belt count and ignores the server's, exactly as it ignores the
  server's magazine for its own rifle (§3.2's argument, in `PlayerCombat.ApplyAuthoritative`): the
  server's number is a round trip old and applying it would make the HUD count backwards.

What *is* mispredicted, deliberately: **how fast a carrier walks**. `MoveSpeedScale` is applied
before each simulated tick from what this process believes the player is carrying, so for one round
trip after a pickup the client thinks it is walking at full speed and the server disagrees. That is
a correction of a few centimetres arriving through the ordinary reconciliation path (§3.2), which
is what that path is for. A replay also uses the *current* scale rather than the one in force on the
tick being replayed, for the same reason and with the same size of error.

A deployed gun is deliberately **not a collider**. One that could be sheltered behind would have to
be a static body that moves, which the navigation bake and the projectile queries would both have
to be taught about; here it is something to stand behind and shoot with, and rounds pass through it.

Ammunition is a belt and not a magazine: the heavy gun is the one weapon in the catalog with
`ReloadSeconds = 0`, so `WeaponSim` cannot refill it and an empty one dry-fires until somebody
carries a can out to it. A can fills the belt and wastes whatever does not fit, which is what makes
walking one out to a nearly-full gun a wasted walk rather than a free top-up.

### 10.3 The scoreboard

Kills and deaths are server-side counters all round — nothing puts them on the wire, because a
snapshot carrying them thirty times a second would be paying continuously for a table that is
looked at once. At the end of a round one reliable `ServerRoundSummary` carries the whole thing:
16 bytes of round summary and 9 bytes per player (`ScoreboardCodec`), which is 160 bytes for a full
server, once.

The server fills the same table locally before sending it, because a listen host sends itself
nothing and its scoreboard has to come from somewhere; a peer that connects during the intermission
is sent the table everyone else is looking at.
