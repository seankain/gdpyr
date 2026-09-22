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

### 4.7 Aiming down the sights

The `Ads` bit has been in `InputFrame` since M1 and unread until now. What reads it is
`Scripts/Sim/Ads.cs`: a pure step over `(state, stats, input)`, exactly as `WeaponSim` is, that
counts how many ticks of a weapon's raise have elapsed. Held is up, released is down, at the same
rate in both directions, and a swap mid-raise keeps *how far up* the sights are rather than how many
ticks of the last weapon's raise that took.

Nothing authoritative reads the result yet — no accuracy cone narrows, no movement slows — so **the
aim state stays off the wire**. It needs to be there anyway rather than on the render frame, for two
reasons:

1. What it decides is what the local player sees: the camera's field of view, where the viewmodel is
   held, whether the scope's surround is up, and how far the mouse turns them. All four are
   presentation, and all four read the *recorded* frame, so a paused, dead or role-choosing player's
   sights behave like everything else that reads an `InputFrame` (§3.1) — a player killed behind an
   optic lowers it over the same ticks they raised it, because their frame becomes look-only.
2. The server steps it for every player anyway, from the inputs it already has, so the day something
   authoritative does want to know whether a shot was aimed, the number is there and both ends
   already agree on it without a byte being added to the snapshot.

**Zoom and look sensitivity move together.** Magnification is defined as the ratio of the two
tangents — `tan(hip/2) / tan(aimed/2)` — and `LocalInputSampler` divides sampled mouse motion by
whatever magnification is in force, so a pixel of travel is worth the same distance on screen at
every power. It is a change at the device end of the boundary and on the right side of it: what
reaches the simulation is still an absolute, quantized angle, and the sights the scale came from were
themselves advanced from a recorded frame.

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
| `ServerSpawnStructure` / `ServerDespawnStructure` | S→C | Reliable | per structure per life | ~26 B / ~8 B |
| `ServerStructureState` | S→C | Reliable | on a flag change, else 5 Hz while it changes | ~8 B |
| `ClientConstruct` / `ClientAssist` | C→S | Reliable | per placement | ~24 B / ~8 B + 4 B/builder |
| `ServerRoundSummary` (scoreboard) | S→C | Reliable | once per round | 16 B + 9 B/player |
| `ServerRequestRoleChoice` | S→C | Reliable | per person per round | ~4 B |
| `ClientSelectTeam` / `ClientSelectLoadout` | C→S | Reliable | per choice | ~4 B |
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

`ServerRequestRoleChoice` is the role menu, and is deliberately the only message it needed. The
server holds a person off the field until they pick a side — on joining, and again when a round
starts — and "held" is expressed as a health of zero, which every client already knows how to draw
(`PlayerCombat.HoldForRoleChoice`). So the *consequences* ride the snapshot that was going out
anyway, the *answer* rides `ClientSelectTeam`, which has existed since M3, and the team a player
ends up on rides the flag byte's team bit. There is no third team and no wire change: `Team` is one
bit and stays one bit.

**LAN discovery is not on this wire at all.** The server browser answers "who is listening?" before
there is a connection to ask it over, so it is its own datagram on its own UDP socket
(`Scripts/Net/DiscoveryCodec.cs`, [`LAN.md`](LAN.md) §4) and the transport knows nothing about it.

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

M8.5 added one row that is only there while it is true: the demo being recorded, its kind, how many
ticks are in it and how big it is ([`DEMOS.md`](DEMOS.md)). "Two hundred bytes after five minutes"
is a recording that stopped, and there is no other way to see it. There is no row for a demo being
*watched*, because this panel lives in the local player's interface and a replay has no local
player — the read head gets a one-line bar of its own instead.

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

## 10. Economy, emplacements, the scoreboard, barracks defences and structures

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

### 10.4 Barracks defences

Added after playtesting, because the default round had become the ground force parked on the
barracks door shooting every unit the strategist paid for as it walked out. A barracks now defends
the ground around itself: nobody can stand within about 100 m of it for long.

A defence is a `DefenseMount`: a map node authored as a child of the barracks it guards, in the
`barracks_defense` group, named by its index in that group sorted by scene path. It is the
building's, not the strategist's — it costs nothing, is never on a queue, cannot be selected,
ordered or killed, runs out of nothing, and is not a unit, so it counts for nothing in the
strategist's defeat condition ([`IMPLEMENTATION_PLAN.md`](IMPLEMENTATION_PLAN.md) §M5). Two kinds, with every number an export on the node:

| | Gun (`Kind = Gun`) | Mortar (`Kind = Mortar`) |
|---|---|---|
| weapon | `turret` (catalog 8): the technical's 12.7 mm round, 450 rpm, **no magazine** | `mortar` (catalog 9): an 81 mm shell at 34 m/s, one every 4 s, **no magazine** |
| engages | nearest ground-force player within 100 m **it can see** | nearest ground-force player 15–100 m away, **seen or not** |
| before it fires | 0.5 s reaction, then it must have swung to within 2° at 90°/s | 1.5 s laying time |
| aims | first-order lead on the chest, 1.2° cone | where they are standing now, no lead, 3 m scatter |

The gun is the one a player reads: it has to see you, turn to you and has a reaction to beat, so a
sprint across its front to cover is a real decision. The mortar is the answer to that cover. It has
no line-of-sight test at all and drops an 8 m blast on wherever you were when it fired, six to seven
seconds later — so what it punishes is **standing still inside the ring**, which is exactly what
camping a door is. The high-arc elevation is found by false position over the real integrator
(`MortarSolver`, in `Scripts/Sim/Defense.cs`): range falls monotonically above the elevation of
greatest range, so a bracket on that branch cannot find the low, flat solution a wall would stop.

**Nothing new is on the wire.** A defence fires through `CombatManager.SpawnProjectile` like a
unit, so every client already receives its rounds as ordinary spawn records, and the only state a
client ever needs — which way the barrel is pointing — is the direction of the last one
(`DefenseBattery.OnShot`). Its rounds name it as their owner from a third range of the owner id's
negative half, below the units': `OwnerId.ForDefense(i)` is `-(65536 + i)`, and
`SimConfig.MaxDefenses` (16) is the width of that range. "Negative is the strategist's side" stays
true, which is what the agent client's reward functions read the event stream by
([`AGENT_API.md`](AGENT_API.md) §8). Nothing is predicted, because nothing about a defence is any
client's own.

**The ring is a level-design constraint, and the map has to be built around it.** A barracks'
`DefendedRadiusMeters` — the furthest any of its defences reaches, plus how far that defence stands
off-centre — is drawn on the ground as a red ring, and it is what the rules below are measured
against:

- **The ground force's spawn goes outside it**, with room to spare. A spawn inside the ring is a
  ticket drain with no decision in it.
- **Every resource node goes outside it.** A node inside is one the ground force can never stand on,
  and a strategist holding a node can never be eliminated (`WinConditions.IsStrategistEliminated`),
  so a node inside the ring turns the round into a clock.
- **Keep the ring inside the playable space where you can.** A ring that runs past a wall leaves
  ground between the two that looks outside the ring and is not.

`Scenes/Test.tscn` moved its barracks to (100, 0.5, −100), in the corner of the walled 240 m arena,
for these reasons: the ring is 107 m; the ground spawn is 144 m from the nearest defence; and
resource node 3 moved out of the ring to (−25, −100). Its two guns stand at diagonally opposite
corners of the building, so that between them they see all four walls, and the mortar is on the
roof.

**Ground bots wait at the ring's edge**, 10 m outside it on the side they came from, instead of on
the door; a unit they chase inside it is fought from that edge, and a bot that finds itself inside
walks straight out (`BotPilot.OutsideDefences`). The waiting point is snapped to the navigation
mesh, because the part of a ring beyond a wall is a waiting point a bot slides along the wall
towards — back into the guns. A three-minute soak of six ground bots against one strategist bot on
the Test map loses nobody to the defences; before the snap, the same soak lost 11 bots, every one of
them to a gun, after sliding along a wall into the ring.

Measured on the Test map (`Tests/Scenarios/barracks_defense.json`, `barracks_mortar.json`): a player
standing still 40 m in front of the door is hit 0.6 s after coming into view and dead at 0.87 s, three
rounds of 45; one standing behind the arena wall, where neither gun can see, takes 82 from the first
shell at 8.6 s and dies to the second at 12.6 s.

### 10.5 Builders and structures

Added so that the strategist can shape the ground rather than only fill it. §5 of the plan cut
builder units from the first pass because capture-by-presence tests the economy with less code, and
that is still how the economy works: a builder does not mine anything. It **puts up structures** —
pillboxes, sandbag walls and sniper towers — that stop rounds, stop bodies, and, for the two with a
gun, shoot.

**The builder** is the fourth line in the unit catalog (`Units/builder.tres`, id 3): 60 points, six
seconds at the barracks, 80 health, a rifleman's pace and a pistol. What makes it a builder is one
flag, `UnitDefinition.CanConstruct`. It is not a tier — `UnitCatalog.Tiers` is still the three
fighting units, which is what the computer strategist chooses its army from — and it costs more than
a rifleman, so the "points below the cheapest unit" half of the defeat condition is unchanged.

**The structures** are three resources (`Structures/*.tres`, `StructureCatalog`, ids in
`StructureKinds`), and every number that decides how strong one is lives there:

| | Pillbox | Sandbag wall | Sniper tower |
|---|---|---|---|
| cost, build time (one builder) | 150, 15 s | 25, 5 s | 125, 14 s |
| health; share of a bullet it takes | 700; 0.25 | 400; 0.3 | 450; 0.5 |
| boxes (across × deep × high) | 3.6 × 3.6 × 2.2 m | 4.0 × 0.8 × 1.1 m | 2 × 2 × 7 m column, 3.2 × 3.2 × 0.9 m cabin on it |
| gun | `turret` (catalog 8): 45 m, 0.6 s reaction, 120°/s, 2° cone | — | `marksman` (catalog 10): the DMR round every 2 s, bottomless; 90 m, 1.2 s, 60°/s, 0.35° |
| eyes | 50 m, from just outside each wall | none | 90 m, from over the parapet |

Explosives do their whole damage to a structure and bullets a share of it
(`Construction.DamageTaken`), which is what makes the launcher the answer to a pillbox and a rifle a
poor one. The sniper tower's cabin hides the ground within about 12 m of its foot from the marksman —
dead ground, and the way in.

**Placing one.** With builders selected, `5`/`6`/`7` arm a pillbox, a wall or a tower the way `X`
arms an attack-move, and the next right-click sends `ClientConstruct` (builders, kind, point, yaw).
The yaw is the camera's, so a wall lies across the screen and `Q`/`E` are how one is turned. The
server's `ApplyConstruct` answers every byte of it, in this order (`PlacementResult`): the sender is a
strategist; the kind exists; at least one named unit is a live builder of theirs; there is a free
slot of `SimConfig.MaxStructures` (32); the rectangle is on the map, clear of every standing
structure (touching is allowed, so walls chain) and more than 10 m from a barracks door; the ground
is found by a ray down, and the site is moved onto it; the navigation mesh comes within 1.5 m of the
footprint, so a builder can reach it and it is not on a roof; and a box query finds nothing on the
world layer where its boxes would go. Then it is **charged, on placement**, exactly as a unit is
charged on the queue — a site is a commitment, and one knocked down before it is finished is not
refunded. The builders get `OrderKind.Build`, which is deliberately not `IsIssuable` through the
generic order message: it names a structure, so it has its own request with its own checks.

**Building one.** Every builder standing within 2.5 m of the footprint adds one tick of work a tick,
up to four (`Construction.Work`), so two builders take half as long. A site starts at 10% of its
health and gains the rest with the work, so a site shot at while it goes up finishes as hurt as it
was made. Until it is finished it is **not solid** and is only as tall as it has been built — the
model and the analytic hit volume are both squashed by the same progress (`StructureShape.Raised`),
so what you can see of a half-built wall is exactly what stops a round. When the last tick of work is
in, it finishes only if **no ground-force body is standing in its footprint**: standing on a site is
how the ground force keeps one from finishing, as standing on a node is how it keeps one from paying,
and the strategist's HUD rings a held site in red. The strategist's own units are walked out of the
footprint instead. Builders right-clicked onto a site finish it; onto a hurt structure, they mend it
at half the build rate, for nothing — the price of a repair is a builder standing in the open.

**Finished, it is part of the world.** A finished structure is a `StaticBody3D` on the world layer
**on every peer**, so movement and a client's prediction of its own movement, every sight line (units,
fog, defences, bots) and the projectile world ray all stop against it without being taught a new
layer. Which structure a round hit is still decided analytically, against the same boxes grown by
2 cm, so that the analytic answer wins a tie with the world ray at the same face and the round is
credited. A round a structure's own side fires is stopped by it and never damages it. The navigation
mesh is **re-baked on a thread** when a structure finishes or falls (`UnitManager.ServerNavigation`,
requests folded while a bake is running); until the new mesh is swapped in, a unit may walk into a
new wall and slide along it, which is the fallback every unit already has.

**Its gun is a barracks gun.** The target selection, reaction, slew and fire that §10.4 describes now
run over a small interface, `IGunPost`, which a `DefenseMount` and a `Structure` both implement; the
only differences are where a post looks from and where its round leaves from. A pillbox does both
through the slit in whichever wall faces the target (a sight line from the middle of a concrete box
would start inside the concrete, and one from the roof would miss everybody standing close to it),
and gives the fog four sensors, one outside each wall. A structure's rounds name it from a fourth
range of the owner id's negative half: `OwnerId.ForStructure(slot)` is `-(65552 + slot)`, directly
below the defences'.

**On the wire:** three reliable messages, all state — `ServerSpawnStructure` (slot, kind, team, base,
yaw; ~26 B, once per structure per life), `ServerStructureState` (health, progress, flags; ~8 B,
sent at once when it is finished, knocked down or held up, because those change what a client
collides with, and at 5 Hz while only its health or progress moves) and `ServerDespawnStructure` —
plus `ClientConstruct` and `ClientAssist` the other way. Rounds are ordinary spawn records, and a
client points the barrel from them, as for a defence. A knocked-down structure lies as rubble for
three seconds before it is despawned. Structures, like defences, **count for nothing in the defeat
condition**: a pillbox holds no ground and earns nothing, so a strategist with only concrete left
has nothing left to play with.

**The computer strategist** keeps one builder, bought once it has four fighting units, and fortifies
the resource nodes — the ones it holds, then the neutral ones, nearest its barracks first — with a
pillbox 6 m in front of the node towards the ground force's spawn, then a wall 10 m in front lying
across that line, then a tower 7 m behind (`StrategistBrain.TryPlanStructure`, `Layout`). While a
builder is idle with a structure planned that it cannot yet afford, the queue stops spending the
structure's price (`StructureSavings`) — without that, the queue spends every point as it arrives
and no pillbox is ever affordable. A refused placement is not asked for again for 30 s. **Ground bots**
shoot the nearest hostile unit, and only when there is none the nearest structure with a gun — a
rifle does a quarter of its damage to concrete, and a bot that preferred the pillbox would stand in
front of it losing the argument.

Measured on the Test map (`Tests/Scenarios/pillbox_fire.json`, `sniper_tower_reach.json`,
`sandbag_cover.json`, `sandbag_absorbs.json`, `builder_fortifies.json`): a player standing 30 m from
a pillbox is first hit 0.67 s after being put there and dead at 1.2 s, three rounds of 45, while one
65 m away is untouched; a player 80 m from a tower is hit at 1.4 s and dead at 3.4 s, two rounds of
60; a crouched player behind a sandbag wall is never fired on by a rifleman 30 m away while one
crouched in the open is killed; thirty rifle rounds into a wall stop in it and do 6 each, not 20; and
the computer strategist, alone on the map, buys its builder at 44 s, places a pillbox 6 m from node 3
at 45 s, takes the node with the builder standing on it at 80 s and finishes the pillbox at 87 s. (Its
first choice, node 2, is refused as blocked — a crate stands where the pillbox would go — and the
30-second retry sends it to the next node, which is the refusal path working.) The re-bake that
follows takes 1.07 s on the worker and 0.2 ms of the tick.

**Not in it:** demos do not record structures (they are not in the unit snapshot, and a structure's
messages are not journalled — [`DEMOS.md`](DEMOS.md) §6); the agent API's strategist observation has
no structure block, so the schema is unchanged (a policy may build through `construct` but cannot
see what it built, [`AGENT_API.md`](AGENT_API.md) §6.2); a hammer does not damage a structure; and
one navigation mesh still serves every unit (§M5 of the plan).

---

## 11. Demos

The tick model §2 and §3 describe has one property that is worth more than it cost: the simulation
reads nothing but the recorded `InputFrame` for the tick, and `PlayerManager.SimulatePlayers` is the
one place a character's intent — a socket, a device or a bot's brain — becomes that frame. Writing
the frame out there writes the round down. That is the input journal Quake III's `com_journal` is
named after, and it is what a demo is here.

Two consequences follow from the authority model rather than from a decision.

**Only the authority can journal.** A client is never told what anybody else pressed (§1), so a
client's demo is what it *was* sent — its own input and the snapshots — and plays back by
interpolation rather than by re-simulation. The two kinds are `Journal` and `Stream`, and `record`
writes whichever the process it is running in can.

**A journal alone is not enough to draw.** Physics is not bit-exact across builds and the RTS half
is not journalled at all, so the file also carries the snapshots the authority broadcast, verbatim:
the player snapshot at `SnapshotRate` and the unit snapshot at `UnitSnapshotRate`, the same bytes
§7's message set already puts on the wire. Playback re-runs the movement step over the journal at
the full tick rate and lets those keyframes correct it every second tick, which is the same
arrangement §3.2 uses on a client — predict at 60 Hz, correct at 30 — pointed at a file instead of a
socket.

The demo's version is therefore tied to the wire's: the payloads are the codecs' own bytes and a
protocol change invalidates old demos. `DemoHeader.IsPlayable` makes that a refusal rather than a
mystery, and the same check refuses a demo recorded at a different `SimConfig.TickRate` — every
recorded tick index would mean something else.

The whole of it, including what this first cut does not replay, is [`DEMOS.md`](DEMOS.md).
