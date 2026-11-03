# gdpyr — Vehicles the ground force drives: a 4x4 and a helicopter

**Status: plan; nothing is built.** Written against `6699b03`. The decisions (GV-D1–GV-D17, §3)
are the defaults the plan is written against; each is one line to change, so flag any you disagree
with before GV2 starts. The phases (GV0–GV9, §12) say what each builds, what checks it, and what is
left for a person at the controls.

This is not [`VEHICLES.md`](VEHICLES.md). That plan's technical and dropship are the strategist's
units: server-only, ordered by clicking, their passengers hidden and inert. These belong to the
ground force: driven by a player's input frames, predicted on the driver's client, with passengers
who shoot. **In code they are crew vehicles (`Crew*`)**, because `Vehicle`, `IsVehicle` and
`VehicleDamageScale` already mean a strategist unit that is a vehicle. **Players, the HUD and the
agent API call them the 4x4 and the helicopter**, because "technical" already names the strategist's
truck on the card, in scenarios (`"unit": "technical"`) and on the event stream.

Companion documents: [`NETCODE.md`](NETCODE.md) §3 (prediction), §4.3 (lag compensation), §10.2
(what is predicted at a mounted gun), §10.6 (armour, the locker); [`VEHICLES.md`](VEHICLES.md) §5.4
(exit slots), §8 (flight); [`CONTROL_POINTS.md`](CONTROL_POINTS.md) §4 (a player steering something
by looking); [`LOADOUT.md`](LOADOUT.md) §7.2 (the locker panel the kiosk's panel copies).

---

## 1. What is asked

1. The ground force can drive vehicles.
2. A **4x4 technical** with **springy suspension**, a **mounted gun** and a **passenger seat**.
3. A **helicopter** with **one passenger seat** besides the pilot's.
4. **Passengers fire their weapons from the seat.**
5. Vehicles are **spawned on a timer from a vehicle kiosk near the weapon locker**.
6. A vehicle **tolerates a significant amount of small-arms fire** and is **destroyed by a direct
   hit from a tank**.
7. **A collision above a certain speed kills a unit.**

## 2. What the code does today

Facts this plan stands on, each read in the source at `6699b03`.

| Fact | Where |
|---|---|
| A client predicts only its own character: `Simulate` on the input clock, `Reconcile` on each snapshot — rewind to the acknowledged tick, replay the rest. `PredictionLedger.Decide` holds the thresholds, 1 cm and 0.5 m/s | `Scripts/Net/PlayerManager.cs:865`, `:1086`, `:1256`; `Scripts/Sim/PredictionLedger.cs`; `SimConfig.MispredictionThreshold` |
| A player steering something else keeps a look-only body: a drone pilot's frame is sent whole and the body gets `InputFrame.LookOnly`, on the server and in the client's replay | `PlayerManager.cs:879`, `:1301`; `Scripts/Match/CombatManager.Drones.cs` |
| At a mounted gun, transitions are not predicted and firing is: `FireMounted` runs the same `WeaponSim.Step` on both ends, from the muzzle, clamped by `EmplacementSim.Traverse` | `Scripts/Match/CombatManager.cs:493`; `Scripts/Sim/Emplacements.cs:255`; NETCODE.md §10.2 |
| One use key, a tap or a hold (`UseTracker.HoldTicks` = 30), resolved by one table for guns, cans and the locker. The locker's panel opens from the client's own prediction of the tap | `Emplacements.cs:73`, `:184`; `CombatManager.ClientSimulateLocal`, `:1848` |
| A round's first contact is a World-layer ray, then analytic capsules for players (from the hitbox ring) and units, then structures; the nearest wins | `CombatManager.FirstContact`, `:774` |
| A blast reaches players, units and structures, and leaves its direct victim out | `CombatManager.Explode`, `:967` |
| Armour is one number on the target (`BulletDamageScale`; explosives whole) and one on the round (`VehicleDamageScale`: 0.25 for the hand grenade, 1 for the rest) | `Scripts/Sim/Armour.cs:27`, `:39` |
| The tank's round does 90 on a direct hit and up to 80 in a 4.5 m blast, one every 3 s. Nothing in the catalog does more than 100 on a direct hit but the drone, 800 | `Weapons/projectiles/cannon_76.tres`, `mortar_81.tres`, `fpv_drone.tres`; `Weapons/cannon/cannon.tres` |
| Units aim at a player's `Hitbox.Center` and lead by `Character.Velocity`; the fog asks whether a sensor sees `Hitbox.Center` | `Scripts/Rts/UnitManager.cs:1058–1059`; `Scripts/Match/VisibilityService.cs:213` |
| A unit's direct-hit query skips the shooter's own side, so the tank's round never strikes a strategist unit | `UnitManager.QuerySegment`, `:1727` |
| The strategist's units on foot — rifleman, builder, sniper, heavy gunner — move at 4.5 m/s at most, are 0.4–0.45 m in radius and set no `BulletDamageScale` (so 1). Its technical, tank, supply truck and dropship are units marked `IsVehicle`. A unit's hitbox is an analytic capsule, and one killed through `UnitManager.Damage` credits its killer and emits `unit_lost` | `Units/*.tres`; `Scripts/Rts/UnitDefinition.cs:72`; `Scripts/Rts/Unit.cs:337`; `UnitManager.cs:1808` |
| A weapon definition needs no round: the hammer has none and loads as `default` projectile stats | `Weapons/geopick/geopick.tres`; `Scripts/Core/WeaponCatalog.cs` (`Projectile?.ToStats() ?? default`) |
| Three physics layers: World, Players, Units. Every player, and every unit on foot that is not riding, calls `MoveAndSlide` every tick, idle or not | `Scripts/Core/CollisionLayers.cs`; `project.godot` `[layer_names]`; `Scripts/Fps/State.cs:46`; `UnitManager.cs:643` → `Scripts/Rts/Unit.cs:443` |
| The Test map's weapon locker is at (−79.45, 1.95, 91.38), inside the ground force's 30 × 30 × 7.1 m concrete bunker (`Scenes/Environment/Pillbox.tscn`, origin (−75.83, 0.5, 87.46)), whose one door — in its local +X wall — is 3.2 m wide and 4 m high. NETCODE.md §10.6 still gives the locker's earlier position, (8, 0.5, 11) | `Scenes/Test.tscn:422`, `:426`; the bunker's collision mesh |
| The floor is a flat 240 × 240 m slab; its obstacles are 1.2 m low walls, 2.8–3 m blocks and the range posts | `Scenes/Test.tscn` |
| The ground force's keys: W A S D, Space, Ctrl, Shift, 1 2 3, R, V, E, G, Q, F1–F3, both mouse buttons. U, T and G are each asserted to carry one action | `project.godot`; `Tests/VehicleControlsTests.cs`; `GrenadeTests.TheGrenadeKeyIsBound` |
| There is no fall damage: a player's health changes only in `CombatManager.Damage`, which only rounds, blasts and swings reach | `CombatManager.cs:1226`, `:1238` |
| No `dotnet` and no Godot were available where this plan was written: nothing in it has been run | — |

---

## 3. Decisions

| | Decision | Why |
|---|---|---|
| GV-D1 | **A system of their own, not `Unit`.** A crew vehicle is a `CharacterBody3D` (`CrewVehicle`) in its own registry (`CrewVehicleManager`). | Units are never predicted, never fogged (NETCODE.md §6.2), count towards the strategist's defeat (`LiveUnitCount`), and take orders and plans. Each is wrong for a vehicle a player drives. |
| GV-D2 | **Seats.** 4x4: driver (enclosed), gunner at the mounted gun (exposed), passenger in the bed (exposed). Helicopter: pilot (enclosed), passenger in the left door (exposed). Drivers and pilots do not fire. | The request. A driver's mouse belongs to the camera, a pilot's to the heading. |
| GV-D3 | **Movement: a kinematic body with a sprung chassis, engine-free.** The 4x4's step is a pure function of its state, the input frame and four wheel-ray contacts; four spring-dampers set its ride height, pitch and roll. The collider stays upright; the attitude is drawn on the mesh and applied to the seats and the hull's hit boxes. No `RigidBody3D` or `VehicleBody3D`. | The springs are the request, and a pure step is what a replay re-runs and what xUnit tests (§5.1). A rigid body cannot be stepped alone for a replay, and the solver's state is not the same on two peers (IMPLEMENTATION_PLAN.md §2). The dropship already keeps its tilt off the body (VEHICLES.md §8.4). |
| GV-D4 | **The driver's client predicts its vehicle** as it predicts its character: the same input ring, a second ledger, a replay through the same step. Everybody else interpolates it. Getting in, getting out and changing seat are not predicted. | Unpredicted, steering would answer a round trip plus the 100 ms interpolation delay late — about 200 ms at 100 ms RTT. The drone accepts a round trip (CONTROL_POINTS.md §4.4); a truck that steers that late does not feel like one. NETCODE.md §10.2's rule for transitions stands. |
| GV-D5 | **Controls, with no new key and no new input bit.** Tap `E` beside a vehicle: the free seat whose door is nearest. Seated: tap `E` gets out, hold `E` moves to the next free seat. 4x4: `W`/`S` throttle, brake and reverse; `A`/`D` steer; `Space` handbrake; the mouse looks, and the camera follows the truck. Helicopter: `W`/`S`/`A`/`D` move, `Space`/`Ctrl` climb and descend, and the mouse turns it towards where the pilot looks. | The frame stays twelve bytes and every recorded frame keeps its meaning; U, T and G keep one action each. Steering by looking is the drone's, which players already know. |
| GV-D6 | **Passengers fire their own primary and pistol** — sights, reload and swap too; not the hammer, a grenade or a drone. Each seat has a fire arc, outside which the trigger does nothing. A round fired from a seat never hits that vehicle or anybody in it. | The request. The arc keeps a passenger from shooting through the cab; the exclusion keeps a round at the arc's edge off its own hull. Grenades and the hammer from a moving seat are rules of their own (§15). |
| GV-D7 | **Aim is stabilised, and a seated shooter's round leaves from where their client drew the seat.** The gunner's and a passenger's look are world angles, as on foot: the bounce moves the muzzle, not the aim. The server takes the origin from the vehicle's pose at the tick that shooter's client was drawing (§5.6). | At 18 m/s and 100 ms RTT the truck a passenger sees is 3.6 m behind the server's. Without it every round fired from a moving vehicle would leave 3.6 m from its tracer. |
| GV-D8 | **Bodies are solid to vehicles; vehicles are not solid to bodies.** A vehicle collides with the world, structures and other vehicles. Players and units collide with vehicles: they cannot walk into one, and one that drives into them pushes them aside through their own `MoveAndSlide`. Below GV-D17's speed, contact hurts nobody. | A body that blocked a vehicle would let one rifleman stop a truck. A vehicle that passed through bodies both ways would let a player stand inside a hull that takes every round meant for them. And the driver's prediction never collides with an interpolated body. |
| GV-D9 | **Armour.** The 4x4: 600 health, bullets do 0.15. The helicopter: 450, bullets do 0.2. Explosives do their damage times the round's `VehicleDamageScale`. | 200 rifle hits to destroy the 4x4 and 113 the helicopter, against 25 for the strategist's technical and 40 for its dropship (§9). |
| GV-D10 | **A direct hit by the tank's round destroys either.** `ProjectileDefinition` gains `VehicleImpactDamage`, used in place of `Damage` against a vehicle it strikes when above zero; `cannon_76.tres` sets 1,000. Its blast is unchanged, so a near miss does up to 80. | The request, as one number on the round that has it — the drone's 800 against the tank's 700 is the precedent (CONTROL_POINTS.md §4.3). The tank never strikes its own side's units (§2), so nothing else moves. |
| GV-D11 | **Enclosed seats are shielded; exposed seats are not.** A driver's or pilot's capsule lies inside its hull box, so a round reaches the hull first, and a blast skips them. A gunner or a passenger is a player like any other above the bed or in the door. Friendly fire is as for players. | The hull's health is the driver's armour; the people shooting out of it are what can be picked off. |
| GV-D12 | **A wreck** throws everybody aboard out at an exit point, each taking 50 as explosive damage, the vest its share, in the killer's name. A helicopter destroyed more than 4 m above the ground kills everybody aboard. The wreck burns for 2 s and goes. It costs no ticket of its own. | VEHICLES.md V-D7's rule for units, at a player's 100. 4 m is `CapturePad.VerticalToleranceMeters`. |
| GV-D13 | **The kiosk and its timer.** One kiosk, beside the locker. Per kind, at most one alive; both are ready when the round starts; once one is lost — destroyed, or abandoned and removed — the next is ready 45 s (4x4) or 90 s (helicopter) later. A request at the kiosk puts the vehicle on its kind's pad outside the bunker. A vehicle that is empty, with nobody of the ground force within 15 m, for 60 s is removed. | "Spawned on a timer from a kiosk". A cap of one each puts at most five of six players in vehicles. Removal keeps a truck left in a corner from holding its kind's only slot for the rest of the round. |
| GV-D14 | **Fog: the players' rule.** A strategist is told about a vehicle while the side's sensors see its hull's centre with a clear line, and about its occupants exactly when it is told about the vehicle. | One rule, the one players already have (NETCODE.md §6.2). |
| GV-D15 | **Bots never drive or fly.** They are made robust to vehicles (§10). HTN ground bots may ride with a person (GV8, optional); legacy bots are unchanged. The computer strategist is unchanged: its units shoot at the occupants, and the hull takes it. | Bots that drive are a navigation system of their own (§15). Legacy bots are the baseline policies are measured against (TRAINING.md §3). |
| GV-D16 | **Wire and agent API: additive.** One unreliable 30 Hz message and one request with its answer; the input frame and the 32-byte player record unchanged; `DemoFormat.Version` 4 → 5; the agent schema stays `gdpyr-agent-obs-4`; new ops and events only. | Every trained checkpoint keeps loading; an old demo is refused rather than misread. |
| GV-D17 | **Running units over.** A crew vehicle whose hull strikes one of the strategist's units on foot at 8 m/s or more — measured where it touches, towards the unit — kills it, in the name of whoever is at the controls, or of the last one for 3 s after they leave them. Slower, the unit is pushed aside unhurt (GV-D8). The strategist's vehicles are never run over, the ground force is never hurt by contact, and the crew vehicle loses no speed and takes no damage. | The request. 8 m/s is faster than any unit on foot walks (4.5 m/s) and than the 4x4 reverses (6 m/s), so nothing dies walking into a parked truck or under one backing up, and the 4x4 has it about 2 s from rest. A tank killed by a truck would be absurd. A ram that changed the vehicle's motion would make the driver's prediction depend on units the client sees a round trip late. |

---

## 4. The shape of it

```
 E at the kiosk ─► panel ─► ClientRequestVehicle(kind) ─► KioskRules.Check ─► vehicle on its pad
                                                           (one validator: a person, a bot, an agent)

 tap E beside a hull ─► UseAction.EnterVehicle ─► seated, on the server ─► the next ServerCrewVehicles
                                                                            names the peer in the seat
   the driver's client   predicts the vehicle from its own frames and reconciles on each record
   every other client    interpolates the vehicle at the render clock; seated bodies ride its seats
```

The server's tick (`PlayerManager.ServerTick`), with what is new:

```
SimulatePlayers        a driver's frame is kept for its vehicle; every seated body gets LookOnly;
                       a gunner or passenger fires from the pose their client was drawing (§5.6)
CrewVehicles.Tick      NEW: step every vehicle (its driver's frame, or none), MoveAndSlide, pose ring;
                       run over every unit on foot its hull struck at 8 m/s or more (§5.9);
                       then pin every seated body to its seat: position, velocity, hitbox
UnitManager.Tick       units see and shoot seated players where they sit, leading by the vehicle's velocity
EmplacementManager.Tick
CombatManager.PostTick hitbox ring ← seats; projectiles ← hull boxes; ResolveWrecks, then
                       ResolveCrewWrecks (NEW); kiosk timers and abandonment; the fog, vehicles included
snapshots              the player record, unchanged, and ServerCrewVehicles, on the same tick at 30 Hz
```

**Rejected:** a `Unit` with a ground-force team (GV-D1); `VehicleBody3D` (GV-D3); no prediction
(GV-D4) — kept as the fallback if the sprung step will not reconcile (§14).

---

## 5. The rules, engine-free

All of it in `Scripts/Sim`, which `Tests/Gdpyr.Tests.csproj` compiles directly, as the cargo, flight
and drone rules are.

### 5.1 The 4x4 — `Scripts/Sim/FourByFour.cs`, `Tests/FourByFourTests.cs`

**State** (`FourByFourState`): position, velocity, yaw and yaw rate, the chassis's pitch and roll and
their rates, steering angle, ticks airborne. **Inputs**: traits (from `Vehicles/four_by_four.tres`),
the frame, and four `WheelContact` {hit, distance}, one per wheel: the engine half casts each straight
down from its hardpoint on the upright body, `rest + travel + wheel radius` long, through one cached
query.

`FourByFourSim.Step(ref state, in traits, in InputFrame, ReadOnlySpan<WheelContact>, dt)`:

1. **Springs.** A corner's height includes the chassis's attitude. Compression is the rest length
   less the spring's length, clamped to the travel, with a bump stop ten times as stiff in the last
   5 cm. Force is stiffness × compression less damping × the corner's vertical speed, and never pulls.
   The sum against gravity is the heave; the moments, plus load transfer from this tick's
   longitudinal and lateral acceleration at the centre of mass's height, are the pitch and roll.
   Defaults: 1.6 Hz and a damping ratio of 0.3 — the chassis overshoots by about 37 % and is inside
   2 % of rest in about 1.3 s. Pitch and roll stay within ±35°: it does not roll over.
2. **Traction**, with two or more wheels down. Throttle is `−MoveZ` and sets the speed it drives
   towards: full, 18 m/s (0 to 18 in about 4.5 s); a quarter, 4.5 m/s. Braking is 9 m/s² against the
   motion, reverse goes to 6 m/s, and coasting slows it at 1 m/s².
   Steering moves at 2.5 rad/s towards `MoveX` × a limit that falls from 32° at rest to 9° at top
   speed. Yaw rate follows `v · tan(steer) / wheelbase`, cut back so the lateral acceleration it asks
   for stays under grip (9 m/s²); grip bleeds off sideways speed. `Space` is the handbrake: 6 m/s²,
   and a third of the grip, so it slides.
3. **Airborne**, with fewer than two wheels down: gravity, no traction, the rates damped.
4. It returns the velocity the engine half gives `MoveAndSlide`; `AfterMove` takes back where the
   body went and how fast it is moving.

Tests: dropped 0.3 m onto flat contacts the chassis overshoots at least once and settles within
1.5 s — springy, and stable; with no input on flat ground its energy above rest never exceeds 101 %
of its start and is under 1 % of it after 10 s; full throttle raises speed monotonically to
18 ± 0.4 m/s with the nose up and a quarter holds 4.5 ± 0.2 m/s, braking puts the nose down, a turn rolls the body outward; no turn asks for
more than grip; the handbrake slides; attitude stays within ±35° over 10,000 random inputs and
contacts; with no contacts it falls at g and the throttle changes nothing; the same inputs give
bit-identical states over 1,000 runs; position stays inside the wire's ±327.67 m.

### 5.2 The helicopter — `Scripts/Sim/Helicopter.cs`, `Tests/HelicopterTests.cs`

**State**: position, velocity, yaw and yaw rate, tilt (pitch, roll), rotor (0–1), grounded. **Inputs**:
traits, the frame, height above the ground — one ray down, as the dropship's (VEHICLES.md §8.2).

- **Rotor:** spins up in 2.5 s while a pilot is seated and down in 4 s without one. No lift below 60 %.
- **Flying:** `W`/`S` along the heading and `A`/`D` across it, at 5 m/s², to 22 m/s forward and 10 m/s
  sideways or back. `Space` climbs at up to 5 m/s and `Ctrl` descends at up to 6; with neither it
  holds its height. The heading turns towards the frame's yaw at up to 90°/s, the short way round —
  the drone's rule (`DroneSim.Step`). Ceiling 25 m above the ground under it; the position is clamped
  into `Cargo.InField` (x and z within ±115 m).
- **Tilt:** nose down to accelerate, banked into a sideways push, ±20° at most. Drawn, and it moves the
  door seat.
- **No pilot in the air:** it comes down at 4 m/s, its horizontal speed decaying, and lands.
- **Grounded** at 5 cm or less with no upward speed: no horizontal speed, nothing slides.

Tests: no lift below 60 %; spin-up and spin-down times; climb, descent, hold and ceiling; the field
clamp; the turn's rate and short way round; an unpiloted helicopter at 20 m lands and never descends
faster than 4 m/s; grounded, it never slides; tilt bounded and in proportion; determinism; altitude
inside the wire's range.

### 5.3 Seats and the use key — `Scripts/Sim/CrewSeats.cs`, `Tests/CrewSeatTests.cs`

A seat (from the definition): role (driver, gunner, passenger), enclosed or not, its seated capsule,
its eye, its door (entry point), its exit point, and its fire arc in yaw and pitch relative to the
hull.

- **`UseSituation`** gains `Seated`, `VehicleInReach` and `KioskInReach`; **`UseAction`** gains
  `EnterVehicle`, `ExitVehicle`, `ChangeSeat` and `OpenKiosk`, appended. `EmplacementSim.Resolve`:
  seated, a tap gets out, a hold changes seat, and nothing else is in reach; empty-handed on the
  ground within `SimConfig.EmplacementReachMeters` (2.5 m) of a hull box, a tap gets in; within the
  kiosk's footprint (`LockerSupply.FootprintDistance`, 1 m), a tap opens it. The nearest of locker,
  kiosk, gun, can and hull wins, as today.
- **`MayEnter`**: alive, ground force, carrying nothing, not mounted, not flying a drone, the vehicle
  not destroyed and moving at 3 m/s or less, a free seat, and a helicopter on the ground.
  **`PickSeat`**: the free seat whose door is nearest; a tie goes to the lower seat. **`NextSeat`**:
  the next free seat in seat order, or none.
- **`MayExit`**: a helicopter no more than 4 m above the ground; anything else, always. Candidates in
  order: this seat's exit point, the other seats', then VEHICLES.md's `Cargo.ExitSlot` ring.
- **`MayFire`**: a driver or pilot never; the gunner the mounted gun; a passenger their own weapon,
  while the aim is inside the seat's arc.

Tests: every row of the grown table, including that every press the old table answered still
answers the same; each gate of entering, changing and getting out; seat picking and its tie; each
arc at its edges.

### 5.4 Hulls — `Scripts/Sim/Hitbox.cs`, `Tests/HitboxTests.cs`

- **`HitObb`** — centre, axes, half extents — with `Hitbox.SegmentIntersects(in HitObb, …, out t)`, a
  slab test with the half extents grown by the round's sweep radius, and `Hitbox.DistanceTo(in HitObb,
  point)` for blasts.
- A hull is up to four boxes: the 4x4's cab, bonnet, bed and gun shield; the helicopter's cabin and
  tail boom.

Tests: hits through all six faces, parallel misses, a segment that starts inside; zero distance
inside; **every enclosed seat's capsule lies inside its hull box by at least 5 cm on every axis** —
the proof that no segment from outside reaches a driver or a pilot before the hull, since a segment
into a convex box enters the box before anything inside it; every exposed seat's capsule stands at
least 0.4 m outside its hull on its open side.

### 5.5 The kiosk — `Scripts/Sim/VehicleKiosk.cs`, `Tests/VehicleKioskTests.cs`

- **`KioskTimer`**, one per kind: ready when the round starts; `OnLost(tick)` starts the respawn;
  `Status(tick, alive)` is ready, out, counting (seconds left) or disabled.
- **`KioskRules.Check`** (`KioskRefusal`, beside `KitRequest.Check`'s `KitRefusal`), in order: `dead`,
  `not_ground`, `not_at_kiosk` (the footprint plus the locker's 0.5 m of latency slack), `seated`,
  `mounted`, `carrying`, `piloting`, `invalid`, `out` (the cap), `not_ready`, `pad_blocked`,
  `too_soon` (a quarter-second after the last request).
- **`KioskRules.Abandoned`**: empty, nobody of the ground force within 15 m, for 60 s.

Tests: the timer runs from the loss, not the spawn; the cap; ready at the round's start; every
refusal, in order; each of abandonment's three conditions.

### 5.6 Where a seated shooter fired from — `Scripts/Sim/PoseHistory.cs`, `Tests/PoseHistoryTests.cs`

Each vehicle records its pose (position and orientation) every tick in a 32-tick ring, the size of
the hitbox ring. A client draws the vehicle at its render clock, `InterpolationDelayTicks` (6)
behind its estimate of the server's tick (`SimClock.RenderTick`), and its input clock leads that
estimate by its one-way latency plus however long its frames wait in the server's buffer. So the
tick a client was drawing when it sampled the frame the server consumes at tick `N` is

```
view = N − LagCompensationTicks(peer) − InputBufferDepth(peer) − InterpolationDelayTicks      (clamped to the ring)
```

and a seated shooter with no remote client — a bot, an agent's seat, the listen host — is at `N`.
The muzzle, the eye and the fire arc are taken from the pose at `view`; NETCODE.md §4.3's cheap
compensation then advances the round as it does anybody's. At 18 m/s and 100 ms RTT — three ticks
each way, a three-tick buffer — that is 3 + 3 + 6 = 12 ticks, 3.6 m.

Tests: the ring wraps; the formula and its clamp; `N` itself for a seat with no client; a sampled
pose is the one recorded for its tick.

### 5.7 The driver's ledger — `Scripts/Sim/CrewLedger.cs`, `Tests/CrewLedgerTests.cs`

Predicted vehicle states by tick, beside `PredictionLedger`, whose input ring both share.
`Decide(ack, server)`: an older or repeated acknowledgement is ignored; no prediction on record for
`ack` — the first record after getting in — **snaps and replays** `ack + 1` onwards from the recorded
inputs; within 3 cm, 0.5 m/s and 0.5° it is in sync; otherwise it replays. Tests mirror
`PredictionLedgerTests`.

### 5.8 Definitions

- **`CrewVehicleDefinition`** (`Scripts/Fps/`, a `[GlobalClass]` resource): name, health, bullet scale,
  respawn seconds, cap, collider, hull boxes, seats, gun, the speed that runs a unit over
  (`RamKillSpeedMetersPerSecond`, 8 m/s), and drive or flight traits, with `ToFourByFourTraits()`
  and `ToHelicopterTraits()`. `Vehicles/four_by_four.tres` (id 0) and `Vehicles/helicopter.tres`
  (id 1); `Scripts/Core/CrewVehicleCatalog.cs`, append-only.
- **The mounted gun:** weapon id 14, `Weapons/vehicle_gun/vehicle_gun.tres`, appended to
  `WeaponCatalog.Paths` (32 allowed): the 12.7 mm round (`hmg_127`, 45), 450 rpm, a 100-round box, a
  5 s reload, reloading for ever (`Reserve = −1`, as every unit weapon and mounted gun does). It
  traverses 360° and elevates from −15° to +50° relative to the hull.
- **The ram:** weapon id 15, `Weapons/ram/ram.tres`, appended: no round and no slot, as the hammer has
  no round. It is there so that a unit run over is named on the stream, in the server's log and in
  the kill counts as any other kill is.
- **`ProjectileDefinition.VehicleImpactDamage`**, carried through `ProjectileStats` (GV-D10).
- **`SimConfig.MaxCrewVehicles = 4`**: slots 0–3 on the wire; two kinds at a cap of one use two.

Tests that read the files, as `TrainedUnitResourceTests` does: ids 0 and 1 are the two kinds;
weapon ids 0–13 are where they were, 14 is the gun and 15 the ram; `cannon_76`'s vehicle impact is
at least both vehicles' health and its blast at the face is less than either; 5.56 mm hits to
destroy are at least 150 for the 4x4 and 100 for the helicopter; the speed that runs a unit over is
above every unit on foot's `MoveSpeed` and above the 4x4's reverse speed; the seats are GV-D2's.

### 5.9 Running units over — `Scripts/Sim/Ramming.cs`, `Tests/RammingTests.cs`

- **Contact:** a unit's capsule touches the hull when its axis passes within its radius of one of
  the hull's boxes — §5.4's `Hitbox.SegmentIntersects`, with the capsule's radius as the sweep — or
  starts inside one. The grown box overstates contact at a vertical edge by up to 0.17 m for a 0.4 m
  capsule, less than one tick's travel at the 4x4's top speed.
- **Speed** (`Ramming.ImpactSpeed`): the hull's velocity where it touches — the vehicle's velocity
  plus what its yaw rate sweeps at that point, flat — taken towards the unit. Zero or less is no
  impact. The unit's own velocity is left out: a rifleman walking into a parked truck walks into a
  wall.
- **Kills** (`Ramming.Kills`): at the definition's `RamKillSpeedMetersPerSecond` (8 m/s for both
  kinds) or more, a unit on foot — not `IsVehicle` — dies; slower, it is pushed aside unhurt
  (GV-D8). A strategist's vehicle never dies of it.
- **Credit** (`Ramming.Credit`): the driver or pilot at the controls; the last one for 3 s after
  they leave the seat; after that, nobody.

Tests: a rifleman dead ahead of a 4x4 at 8 m/s dies and at 7.9 m/s does not; one its side grazes at
18 m/s, with no speed towards it, does not; one swept by the tail of a truck spinning on its
handbrake, at 8 m/s or more there, does; a parked truck kills nothing, whatever walks into it; full
reverse, 6 m/s, kills nothing; a strategist's vehicle is never killed; a capsule further than its
radius from every box is no contact, and one inside a box is; the credit's 3 s, inclusive.

---

## 6. The server

### 6.1 Nodes

- **`Scripts/Fps/CrewVehicle.cs`** — a `CharacterBody3D` in `MotionMode.Floating`, on a new physics
  layer 4, **Vehicles** (`CollisionLayers.Vehicles` and `project.godot`'s `[layer_names]`), with mask
  World | Vehicles. Its collider is a bevelled convex hull, so it slides off wall ends rather than
  catching on them. It casts its own rays and moves itself; its presentation — the greybox hull,
  wheels placed by their compression, rotor discs, the gun on its mount — is built only off a
  dedicated server, as the dropship's is.
- **`Scripts/Fps/CrewVehicleManager.cs`** — a sixth autoload, beside `EmplacementManager`: the
  registry (`MaxCrewVehicles`, fixed), the seats, the kiosk, the tick, the message, a client's
  puppets and the driver's prediction.
- **`Scripts/Fps/VehicleKiosk.cs`** — a map node in the `vehicle_kiosk` group, with one `Marker3D` pad
  per kind among its children.
- **`CollisionLayers.CharacterMask` and `UnitMask` gain Vehicles** (GV-D8). A body pushed by a hull
  is pushed by its own `MoveAndSlide`'s recovery; players and units on foot call it every tick (§2).
  The strategist's own vehicles are units and collide with crew vehicles as any unit does; the riders
  inside them are on no layer and no mask (VEHICLES.md §5.1), so the change never reaches them.

### 6.2 The tick

As in §4. A vehicle with nobody in the driver's seat steps on a neutral frame: the 4x4 brakes to a
stop on its handbrake; the helicopter's rotor spins down and, in the air, it comes down.

### 6.3 Getting in, changing seat, getting out

- **In:** the server resolves the tap (`EmplacementManager.ServerUse` → `CrewVehicleManager.Enter`):
  the player is seated, and their body's layer and mask become 0, as a corpse's layer does
  (`fps_controller.SetDeadPresentation`). The sights come down; a driver's weapons are put away; a
  gunner's trigger is the gun's.
- **Pinned**, every tick after the vehicles step: the body's position is the seat's, its velocity
  the vehicle's, its hitbox the seat's capsule. Units then lead, the fog then looks and the hitbox
  ring then records at the right place with no code of their own — the pin VEHICLES.md §5.3 uses for
  riders, with velocity added, because units read it (§2).
- **Changing seat** (a hold) goes to `NextSeat`. A driver who moves to the gun leaves the 4x4
  braking.
- **Out** (a tap): the first candidate (§5.3) that passes VEHICLES.md §5.4's checks — in the field,
  reachable by a ray from the hull, ground beneath within 4 m of the vehicle's, and a clear capsule
  against World, Players, Units and Vehicles but this hull. The body is put there with no velocity,
  back on its layer. No clear candidate: the player stays seated and the HUD says so.
- **Unseated without a press** by death (the body falls where the seat was), a change of side,
  leaving, and the round's end.

### 6.4 Firing from a seat

- **Driver and pilot:** nothing steps but the sights, lowered as at a mounted gun (`Ads.Step` with
  `AimStats.None`).
- **Gunner:** `FireMounted` takes a mount (`IGunMount`: weapon state, stats, muzzle, traverse), which
  `Emplacement` and the vehicle's gun both implement. Muzzle and traverse come from the pose at
  `view`. The gunner's client keeps its own box count and ignores the server's, NETCODE.md §10.2's
  rule.
- **Passenger:** `WeaponSim.SelectSlot`, `Ads.Step` and `WeaponSim.Step` as on foot, with the fire bit
  stripped outside the seat's arc; `Fire`'s origin is the seat's eye at `view`.
- **Never from a seat:** a grenade (`GrenadeSim.Step` sees no grenade bit), the hammer, a drone
  (`DroneSim.MayLaunch` gains `seated`), a locker kit (`KitRequest.Check` refuses `seated`), the
  locker's resupply.
- Every round names its shooter's peer, as a mounted gunner's does.

### 6.5 Damage and wrecks

- **`FirstContact`** asks `CrewVehicleManager.QuerySegment(…, ownerId, ref nearest)` after the units
  and structures, so the nearest wins as before. It skips the shooter's own vehicle, and the player
  loop skips that vehicle's occupants.
- **`ResolveSegment`** deals `VehicleImpactDamage` when the round has one and struck a vehicle, else
  `Damage`, through `DamageCrewVehicle`.
- **`Explode`** walks the vehicles by `Hitbox.DistanceTo` the nearest hull box, the direct victim
  left out, and its player loop skips enclosed seats.
- **`DamageCrewVehicle`**: `Armour.DamageTaken(amount, explosive, definition.BulletDamageScale,
  round.VehicleDamageScale)`. Nothing dealt is no marker and no event (NETCODE.md §10.6); otherwise
  `ConfirmHit(attacker, null, killed)` and a `vehicle_damaged` event. A kill writes a crew wreck —
  vehicle, attacker, weapon, height above the ground.
- **`ResolveCrewWrecks`**, right after `ResolveWrecks` in `ServerPostTick` and for the same reasons
  (VEHICLES.md §5.5): every occupant out at a candidate and dealt 50 as explosive damage in the
  killer's name, or killed outright when the wreck is a helicopter over 4 m up or no candidate is
  clear. Then `vehicle_destroyed`, the kind's timer starts, and the wreck burns — `PresentDestroyed`
  and EFFECTS.md §2.3's `vehicle` kind — and is removed two seconds later.
- Nobody is credited with a kill for a crew vehicle: it was the ground force's own.
- **Running units over** (GV-D17), in `CrewVehicleManager`'s tick right after each vehicle steps and
  before `UnitManager.ServerTick`, so a unit is struck where it stood, before its own `MoveAndSlide`
  pushes it out of the hull: every unit on the field that is not a vehicle, within the hull's
  bounding radius plus 1 m, is tested by §5.9. One struck fast enough goes to a new
  `CombatManager.RamUnit`, which deals it its health through `DamageUnit` — as an explosive, so that
  no armour scale can leave it standing — with weapon 15 and the credited driver as the attacker.
  `Damage`, `Kill`, `unit_lost`, the driver's hit marker and kill count are then the ordinary ones,
  and a `vehicle_rammed` event records the speed. With nobody credited the kill is nobody's
  (`OwnerId.None`, no kill counted). A destroyed vehicle does not move and runs nothing over. The
  cost is at most 64 units × 4 boxes a vehicle a tick, and the distance check rejects nearly all of
  them.

### 6.6 The kiosk

- **The map:** `VehicleKiosk1` beside `WeaponLocker1`, inside the bunker. `Pad4x4` 8 m out from the
  bunker's door on the door's axis, facing away from it; `PadHelicopter` 25 m out, with 8 m clear
  round it. The editor sets their positions. At start each pad is checked — the hull's boxes ×1.2
  clear of World and Vehicles, ground within 0.6 m below — and a pad that fails disables its kind,
  with an error in the log and a line on the debug HUD.
- **A request**, `ClientRequestVehicle(byte kind)` → `KioskRules.Check` → the vehicle on its pad,
  facing its marker's −Z, and `ServerKioskResult(byte refusal)` to the asker alone. An agent's
  `vehicle` op (§11) goes through the same validator.
- **Abandonment** is checked once a second; a removal starts the kind's timer like a loss.
- **`ServerResetRound` and `RestartRound`** clear every vehicle and make both kinds ready.
- **A test strip**, built in GV2: two 0.3 m bumps and a 1 m ramp on open ground beside the pads, on
  the World layer, out of the `navmesh` group, where no unit paths. The floor is otherwise flat, and
  would show the springs only as squat, dive and roll.

### 6.7 Every consumer

| Where | What changes |
|---|---|
| `PlayerManager.Simulate` (`:865`) | seated: a driver's frame is kept for the vehicle; the body is look-only, as a pilot's is |
| `PlayerManager.ClientTick` (`:1086`), `Reconcile` (`:1256`) | seated: the character is pinned, not reconciled — the clock is still fed; a driver records and replays the vehicle (§8.2) |
| `PlayerManager.UpdateRemotes` (`:1313`) | a seated remote body sits at its seat on the interpolated vehicle, with its record's look |
| `PlayerManager.ApplyLocalFog` | a listen-host strategist's curtain hides vehicles too |
| `CombatManager.ServerSimulate` (`:303`), `ClientSimulateLocal` (`:1848`) | weapons by seat (§6.4) |
| `CombatManager.Fire` (`:451`), `FireMounted` (`:493`) | origin, arc and mount from the pose at `view` |
| `CombatManager.FirstContact` (`:774`), `ResolveSegment` (`:695`), `Explode` (`:967`) | §6.5 |
| `CombatManager.ServerPostTick` (`:392`) | `ResolveCrewWrecks` after `ResolveWrecks`; the kiosk's timers and abandonment |
| `CombatManager.TryApplyLockerKit`, `KitRequest.Check`, `ServerResupply` | refused, or nothing, while seated |
| `EmplacementSim.Resolve`, `EmplacementManager.Resolve` | §5.3 |
| `DroneSim.MayLaunch` | refused while seated |
| `CombatManager.RamUnit` (new), from `CrewVehicleManager`'s tick before `UnitManager.ServerTick` | §6.5's running over, through `DamageUnit` |
| `WeaponCatalog.Paths` | ids 14, the gun, and 15, the ram, appended |
| `fps_controller.Hitbox` (`:135`) | the seat's capsule while seated |
| `HumanoidRig`, `BodyPose` | `Seated`: the crouch pose until a sitting clip is chosen from the Universal Animation Library |
| `VisibilityService` | vehicles are seen as players are, and their occupants with them |
| `UnitManager.TryComplete`, the dropship's landing and 2 m checks (`UnitManager.Flight.cs`), riders' exit slots (`UnitManager.Cargo.cs`) | their shape queries gain Vehicles: no structure finishes round a truck, no dropship lands on one, no rider gets out inside one |
| `EconomyService` presence | unchanged: a seated player counts where the seat is, within a pad's 4 m band |
| `GroundSensor`, `BotPilot` | §10 |
| `Scripts/Ui/Debug.cs` | a `vehicles` row (§8.5) |
| `CombatHud`, a new `Scripts/Ui/KioskPanel.cs` | §8.5 |
| `DemoRecorder`, playback | §7.4 |
| `AgentServerOps`, `AgentEvents`, `EpisodeMetrics`, `Scenario` | §11 |

---

## 7. On the wire

### 7.1 `ServerCrewVehicles` — unreliable, 30 Hz, on the player snapshot's tick

`Scripts/Sim/CrewVehicleCodec.cs`, with a total decoder, like every codec here. Header, 9 bytes:
tick (u32), count (u8), and for each kind a status byte (ready, out, counting, disabled) and its
seconds left. Then 46 bytes a vehicle:

| Offset | Bytes | Contents |
|---|---|---|
| 0 | 1 | slot, 0–3 |
| 1 | 1 | kind (bits 0–1) · destroyed · airborne · driver seated · gun reloading · 2 spare |
| 2 | 6 | position, 3 × i16 cm |
| 8 | 6 | velocity, 3 × i16 cm/s |
| 14 | 2 | yaw, u16 |
| 16 | 2 | yaw rate, i16 mrad/s |
| 18 | 8 | pitch, pitch rate, roll, roll rate, 4 × i16 — the 4x4's sprung chassis, the helicopter's tilt |
| 26 | 1 | the 4x4's steering angle (i8), or the helicopter's rotor (u8) |
| 27 | 2 | health, u16 |
| 29 | 1 | rounds in the gun's box |
| 30 | 4 | the driver's last input consumed, u32; 0 with nobody driving — the driver's acknowledgement |
| 34 | 12 | the seats, 3 × i32 peer id, 0 for empty |

Two vehicles are 101 bytes, 3.0 KB/s per client; four are 193 bytes, one datagram. It carries the
pose and rates the driver's replay restores, the occupants every client draws, and the kiosk's
status the HUD shows. The kiosk's status is the drone shelf's precedent (CONTROL_POINTS.md §4.4):
state in a message that is going out anyway.

### 7.2 Requests

`ClientRequestVehicle(byte kind)` (reliable, ~3 B) and `ServerKioskResult(byte refusal)` (reliable,
to the requester, ~3 B), the locker's `ClientLockerLoadout`/`ServerKitResult` pair. Getting in,
changing seat and getting out are the use key in the input frame: nothing new on the wire. Nor for
running a unit over: its death rides the unit snapshot and the driver's hit marker
`ServerHitConfirm`, as a shot's do.

### 7.3 Fog

Ground-force clients and spectators get every vehicle. A strategist's packet carries a vehicle while
`VisibilityService` sees it (GV-D14), and the player-snapshot filter carries a seated player exactly
when their vehicle is carried, so the two never disagree about who is in it. As for players, a record
that stops arriving is the only notice: a strategist's client hides the hull once `Fog.IsLost` says so.

### 7.4 Demos

`DemoFormat.Version` 4 → 5. The authority records each `ServerCrewVehicles` payload it broadcasts to
the ground force (the unfogged one); a client records what it received. Playback applies them as the
live path does. The journal's step for a seated body is look-only, so playback takes who sits where,
on every tick, from those records.

### 7.5 One build for everybody

The new messages are RPCs on a new autoload. A server and a client from different commits misroute
or drop them rather than fail a decode — there is no protocol version on the live wire — so the two
ship together, as NETCODE.md §10.10 already requires for the strategist's vehicles.

---

## 8. The client

### 8.1 Puppets

A vehicle's node is made on the first record that names its slot, of the kind it says, and freed two
seconds after a record marks it destroyed — or, on a client nothing is fogged for, after fifteen
packets without it. Position, yaw and attitude are interpolated at the render clock
(`SnapshotInterpolator`, with an attitude channel), and wheels are placed by rays cast on the client:
presentation only.

### 8.2 The driver

A client is the driver from the first record that puts its peer in a driver's seat. From that tick:

- `ClientTick` steps the vehicle (`CrewVehicle.Step(frame)`) instead of the character, and records the
  vehicle's state in `CrewLedger` beside the frame in `PredictionLedger`.
- Each record for its vehicle goes to `CrewLedger.Decide(record.DriverInputTick, record)`: a replay
  restores the record's state and re-runs `CrewVehicle.Step` over the stored frames, rays and
  `MoveAndSlide` included, exactly as `Reconcile` re-runs the character.
- A correction is shown as a decaying offset on the hull's mesh — position and attitude — at the
  character's 50 ms half-life (`SimConfig.VisualErrorHalfLifeSeconds`); past 1 m it is shown at once.
- Getting out is a correction like any other: the next player record puts the body at its exit
  point.

### 8.3 Seated bodies

Every seated body, local or remote, is placed at its seat on the vehicle as this client draws it:
predicted for a driver, interpolated for everybody else. The local player's own record is not
reconciled while seated (§6.7); its health, ammunition and kit apply as before.

### 8.4 Cameras and look

| Seat | Camera | Look |
|---|---|---|
| 4x4 driver | a chase camera on a `SpringArm3D` behind and above the hull, so it does not pass through walls | the sampler adds the drawn hull's yaw change to its yaw each tick while driving, so the camera follows the truck — the device end of the boundary, as `LocalAim.LookScale` is; the 4x4's step never reads the driver's look |
| Helicopter pilot | a chase camera behind the look direction | the frame's yaw is the heading the helicopter turns to |
| 4x4 gunner | over the gun's sights, at its muzzle | world angles, clamped to the mount |
| Passenger | first person at the seat's eye, the viewmodel kept | world angles; the trigger is dead outside the arc, and the reticle says so |

### 8.5 HUD, panel and presentation

- **Prompts** under the reticle: `[E] drive 4x4`, `[E] 4x4 gun`, `[E] 4x4 bed`, `[E] fly helicopter`,
  `[E] helicopter door`, `[E] vehicle kiosk`.
- **Seated:** `4x4 · driver · 412/600 · 14 m/s`, `4x4 · gun · 87/100`, `helicopter · pilot · 380/450
  · 12 m up`; under it `[E] get out · hold [E] gun` (the next free seat's name), or `too high to get
  out`, or `no room to get out`.
- **Under the round's line**, for the ground force: `4x4 ready · helicopter 0:42`.
- **`KioskPanel`**, `LoadoutPanel`'s pattern: a row per kind with its status, `1` and `2` to ask,
  the refusal at its foot (`refused (not_ready): ready in 0:42`); it closes on a vehicle taken, death,
  leaving the reach, the round's end, `E` and `Esc`, and holds `InputFocus.Menu` while open, so the
  sampler sends neutral frames, as for the locker.
- **Debug HUD:** `vehicles 2 · 3 aboard · 4x4 out · helicopter 0:42 · mispredicted 0.4/s · replay 9 · run over 3`.
- **Presentation:** greybox boxes, cylinders for wheels and discs for rotors, in the ground force's
  colour; a seated body in its pose; a wreck burning as a unit vehicle's does. A unit run over falls
  as a shot one does, a round trip after the driver struck it, and the driver gets a hit marker.

---

## 9. Numbers

Hits to destroy each from full health, every round striking, from §2's files and GV-D9's armour.
Blast figures are at the hull's face.

| Round | 4x4 (600, bullets 0.15) | Helicopter (450, bullets 0.2) |
|---|---|---|
| 5.56 mm, 20 — rifle, heavy gunner, strategist rifleman | 200 | 113 |
| 9 mm, 25 — pistol | 160 | 90 |
| 7.62 mm, 60 — DMR, sniper | 67 | 38 |
| 12.7 mm, 45 — strategist technical, barracks gun, heavy gun | 89 | 50 |
| hand grenade, 140 × 0.25 blast | ≥ 18 | ≥ 13 |
| 40 mm launcher, 40 / 90 | 15 direct; ≥ 7 by blast | 12 direct; ≥ 5 by blast |
| 81 mm mortar, 100 / 120 | 6 direct; ≥ 5 by blast | 5 direct; ≥ 4 by blast |
| **76 mm tank round, `VehicleImpactDamage` 1,000 / 80** | **1 direct**; ≥ 8 by blast | **1 direct**; ≥ 6 by blast |
| FPV drone, 800 | 1 | 1 |

For scale: a player without a vest takes 5 rifle hits, the strategist's technical (500, bullets 1)
25, its dropship (400, bullets 0.5) 40.

The least time each survives one weapon at that weapon's own rate, every round hitting:

| Firing | 4x4 | Helicopter |
|---|---|---|
| one rifle, 600 rpm, 30 rounds, 2.2 s reload | 33 s | 18 s |
| one heavy gunner's LMG, 550 rpm, 150 rounds, 6 s reload | 28 s | 12 s |
| the strategist technical's 12.7 mm, 480 rpm, 60 rounds, 5 s reload | 16 s | 6.3 s |
| a barracks gun, 450 rpm, no magazine | 12 s | 6.7 s |
| a tank | its first direct hit; it fires every 3 s | the same |

**Running a unit over** takes 8 m/s at the hull where it touches, whatever the unit's health. The 4x4
has it about 2 s and 8 m from rest, and never in reverse; the helicopter only with its cabin or tail
boom low enough to touch one.

Every number is a playtest dial: `Vehicles/*.tres` (health, bullet scale, timers, cap, the speed that
runs a unit over, the drive and flight traits), `Weapons/vehicle_gun/vehicle_gun.tres`, and
`cannon_76.tres`'s one new field.

---

## 10. Computer players

- **Robust, under either AI:** `GroundSensor` never takes a crew vehicle as a target;
  `BotSituation.HoldFire` counts a friendly hull in the line of fire as a teammate in the way; a
  launcher bot's blast check (`BotPilot.BlastEndangersFriends`) counts friendly hulls; a bot is a body
  and walks round a vehicle as round a wall. A legacy bot's decisions are otherwise unchanged
  (HTN_BOTS.md D5), so its numbers hold.
- **The strategist's units:** unchanged. They target the players inside, lead by the vehicle's
  velocity (the pin), and their rounds meet the hull. A tank engages a driver as it would anybody.
- **The computer strategist:** unchanged. Seated players are contacts where their vehicle is.
- **Nothing gets out of a vehicle's way.** A unit on foot struck fast enough dies under either AI
  (GV-D17); no unit sees a crew vehicle coming or steps aside (§15). A person in the strategist's
  chair can move them.
- **GV8, optional — HTN ground bots ride with a person:** a bot within 15 m of a stopped vehicle with
  a person driving and a free gunner's or passenger's seat, and nothing in sight, gets in, gunner
  first; it fights from the seat with its existing aim and fire, its movement goals ignored; it gets
  out when the driver does. The legacy bots never get in.

---

## 11. Agent API, harness and scenarios

- **Ops:** `{"op":"vehicle","kind":"4x4"}` asks a kiosk, through `KioskRules.Check`, and is answered
  with `taken` or the refusal's name; `{"op":"list_vehicles"}` returns every vehicle — slot, kind,
  position, yaw, health, speed, its seats' peers, destroyed — and the kiosk's status.
- **Events**, appended after `Resupply = 22`: `vehicle_requested` {peer, kind, refusal},
  `vehicle_spawned` {vehicle, kind, peer, x, z}, `vehicle_entered` {peer, vehicle, seat},
  `vehicle_exited` {peer, vehicle, seat, x, z}, `vehicle_damaged` {attacker, vehicle, weapon, dealt,
  health}, `vehicle_destroyed` {attacker, vehicle, weapon, x, z}, `vehicle_rammed` {vehicle, unit,
  speed, x, z}. A unit run over also has the ordinary `damage` and `kill`, naming weapon 15, and
  `unit_lost`, naming the driver. `EpisodeMetrics` mirrors them; `AgentEventTests`' `Last` assertion
  moves.
- **Observation:** `gdpyr-agent-obs-4`, unchanged. A seated seat's `self` is at its seat with its
  vehicle's velocity. Getting in, driving and getting out are the frame's buttons and axes, which a
  policy already sends. A vehicle block in the vector would be `obs-5` (§15).
- `state_hash` folds every vehicle's state and seats. The C# client gains the two ops; the Python
  client passes them through.
- **Scenario spawns:** `{"vehicle": "4x4", "id": "truck", "at": [x, y, z], "yaw": 0, "health": 600}`;
  and a peer spawn may sit in one, `"seat": {"vehicle": "truck", "role": "gunner"}`, through a
  harness-only op as V5's riders are (`UnitManager.ServerSeatPlaced`). Parse-time sentences for an
  unknown or forward label, a role taken twice, and a role the kind does not have
  (`Tests/ScenarioTests.cs`).

New scenarios, one metric per assertion:

| Scenario | Set-up | Asserts |
|---|---|---|
| `vehicle_kiosk_cap` | a seat at the kiosk asks for a 4x4 twice, a second apart | one `vehicle_requested` taken and one refused `out`; `vehicle_spawned.count == 1` |
| `vehicle_4x4_drives` | a seated driver; throttle 4 s, steer 2 s, brake | the driver's observed position moves ≥ 60 m; `vehicle_damaged.count == 0` |
| `vehicle_4x4_small_arms` | a seated driver 35 m from four strategist riflemen, 5 s | `vehicle_destroyed.count == 0`; `vehicle_damaged.count >= 60`; `kill.count == 0` |
| `vehicle_tank_direct_hit` | a seated driver 30 m in front of a tank, nothing else of the strategist's | `vehicle_damaged.count == 1`; `vehicle_destroyed.count == 1`; `vehicle_exited.count == 1` — one hit, and it was the one that destroyed it |
| `vehicle_wreck_ejects` | a driver alone in a 4x4 at 40 health, 35 m from a rifleman | `vehicle_destroyed.count == 1`; `vehicle_exited.count == 1`; the driver's first `damage` is the wreck's 50 — the hull took every round before it went |
| `vehicle_passenger_fires` | a 4x4 at full speed past an infantry unit 25 m off its line; a passenger firing at it | `damage[attacker=@passenger].count >= 3` |
| `heli_flies_and_lands` | a pilot and a passenger on the pad: climb 5 s, forward 4 s, down, both out | `vehicle_exited.count == 2`, both ≥ 40 m from the pad |
| `heli_wreck_at_height` | a helicopter hovering 15 m up with two aboard at 10 health, a rifleman 30 m off | `vehicle_destroyed.count == 1`; `kill.count == 2` |
| `vehicle_rams_infantry` | a seated driver 60 m from three strategist riflemen standing abreast, 0.8 m apart, across its path; full throttle | `vehicle_rammed.count == 3`; `kill[weapon=15].count == 3`; `unit_lost.count == 3` |
| `vehicle_pushes_slowly` | the same riflemen 15 m ahead; a quarter throttle, 4.5 m/s | `vehicle_rammed.count == 0`; `unit_lost.count == 0` |

A harness seat has no client, so `vehicle_passenger_fires` checks firing from a moving seat, not
§5.6's rewind: that needs a client on a link, GV4's check. The stream names a strategist's round by
weapon 0 (`CombatManager.WeaponOnStream`), so the tank's scenario counts hits rather than filtering
on the tank's weapon id.

---

## 12. Phases

Each ends with `dotnet test` green, the tests named here added, `dotnet build` of `Gdpyr.csproj`,
`tools/Gdpyr.Playtest` and `tools/Gdpyr.AgentClient` at 0 warnings, and the docs it touches updated
in the same change. A guard is checked by breaking it and watching a fact fail, as the earlier plans
did.

### GV0 — The baseline (½ day)

`dotnet test`'s count and `./scripts/playtest.sh Tests/Scenarios/*.json`, headless Godot 4.6 .NET
with `--fixed-fps 60`, at the start commit. The last recorded suite is 33 of 41
(CONTROL_POINTS.md §5.4), six of its eight failures older than the branch it was taken on; GV7
compares against this run, not that one.

### GV1 — The rules, engine-free (3 days)

All of §5. **Done when** each fact in §5 passes and each of these, broken, fails one: no bump stop, a
damping ratio of 0, an enclosed capsule poking out of its hull, the timer counted from the spawn, the
view tick unclamped, a ram that kills at any speed. Nothing in the game calls any of it yet.

### GV2 — The 4x4 on the authority (3 days)

`CrewVehicle`, layer 4 and both masks, `CrewVehicleManager`, the definitions and their files, seats
and the use key, the pin, getting out, the driver's controls and camera, presentation, the test strip,
and a console command, `vehicle <4x4|helicopter>`, that puts one in front of you offline or on a
listen host (`GameConsole`'s `_commands.Register`). **Done when**, offline and on a listen host, a
person drives, bounces over the strip, slides on the handbrake, gets in, changes seat and gets out,
and players and units cannot walk into the hull and are pushed aside by it. Two engine behaviours are
relied on here and checked here: a body is pushed out of a hull that moved into it by its own
`MoveAndSlide`, and a bevelled hull in `MotionMode.Floating` does not catch on wall ends at 18 m/s.

### GV3 — The wire, the fog and prediction (3 days)

§7.1–§7.3 and §8.1–§8.3. **Done when**, on a dedicated server with 80 ms RTT and 20 ms jitter
(`tc netem`, DEPLOYMENT.md §4): driving straight on the floor, the steady prediction error is under
5 cm and mispredictions under one a second; no replay is longer than twelve ticks; nothing snaps after
the strip; a passenger's view is smooth; a strategist sees a vehicle only when the side's sensors do;
a client that joins sees the vehicles and who is in them.

### GV4 — Combat from and against vehicles (3 days)

§6.4, §6.5 — running units over included — §5.6 in the game, `VehicleImpactDamage`, wrecks, the
explosion, events, and §10's robustness. **Done when**, on the same dedicated server: at full speed a
passenger's tracer and the server's round leave within 0.3 m of each other (both origins logged by
projectile id); a tank's direct hit wrecks a 4x4, its driver gets out at 50 health, and its exposed
crew take the shell's blast as well; a rifleman's rounds at the driver stop at the hull; the gun's
box counts down on the tick the gunner fires; a 4x4 at full speed through a line of riflemen kills
each one it strikes, in its driver's name, and at walking pace pushes them aside unhurt.

### GV5 — The helicopter (2 days)

§5.2 in the game: the rotor, ceiling, field clamp, landing and coming down unpiloted; presentation;
the pilot's controls and camera; getting out above 4 m refused; the door's arc. The barracks mortar is
checked against a target 25 m up (§14). **Done when**, on a listen host and the dedicated server,
a person takes off, flies the arena, lands on the floor and on the bunker's roof, and the passenger
fires from the door; a helicopter whose pilot leaves at 4 m settles.

### GV6 — The kiosk (1½ days)

§6.6, `KioskPanel`, the HUD's kiosk line, the kiosk and pads on `Scenes/Test.tscn`, the round's reset.
**Done when** a person on a listen host takes each kind from the kiosk, loses it, waits out the timer
and takes it again, and is refused with a reason at every gate in §5.5 they can reach.

### GV7 — Agent API, harness, scenarios and demos (2 days)

§11 and §7.4; the ten scenarios; the whole suite against GV0's run. **Done when** the new scenarios
pass over their seeds and the old ones report what they did at GV0, within the engine's run-to-run
spread (AGENT_API.md §3).

### GV8 — Bots ride with a person (1½ days, optional)

§10's last item, with an `htn_*` scenario and `UnitHtnTests`-style facts for the ground domain's new
task.

### GV9 — Docs (alongside; ½ day to close)

README (controls, a paragraph), NETCODE.md (§1's table, §7's table, a §10.11 in §10.10's shape, and
§10.6's stale locker position), IMPLEMENTATION_PLAN.md M12, AGENT_API.md (ops, events, spawns),
DEMOS.md (version 5), EFFECTS.md (§1's table), and this document's status.

**About 20 days in all.** GV0–GV4 with GV6, about 14, are the 4x4 alone and can ship first.

---

## 13. Verification at a glance

| Claim | Checked by | Where it runs |
|---|---|---|
| The suspension is springy and stable | §5.1's overshoot, settling and energy facts | `dotnet test` |
| A direct tank hit destroys either; a near miss does not | §5.8's file facts; `vehicle_tank_direct_hit` | `dotnet test`; Godot headless |
| Significant small-arms tolerance | §5.8's hits-to-destroy facts; `vehicle_4x4_small_arms` | `dotnet test`; Godot headless |
| Drivers and pilots are shielded | §5.4's containment fact | `dotnet test` |
| Passengers fire from a moving seat | `vehicle_passenger_fires`; GV4's origin log over a link | Godot headless; a person, dedicated server |
| The kiosk's timer and cap | §5.5's facts; `vehicle_kiosk_cap` | `dotnet test`; Godot headless |
| A collision at 8 m/s or more kills a unit on foot; a slower one does not | §5.9's facts; `vehicle_rams_infantry`, `vehicle_pushes_slowly` | `dotnet test`; Godot headless |
| Driving feels like driving | GV3's error, misprediction and replay numbers | a person, dedicated server with `tc netem` |

---

## 14. Risks

| Risk | Signal | Mitigation |
|---|---|---|
| The sprung step does not reconcile: a bounce magnifies small differences | constant corrections driving straight; replays at their longest | damping ratio ≥ 0.25; attitude and its rates in the thresholds; the server rounds its vehicle state to the wire's resolution each tick, so a replay starts from the server's own numbers; last, GV-D4's fallback — the driver interpolates, as a passenger does |
| A hull catches on corners, or a fast hull swallows a body | trucks stopping dead on wall ends; a player inside a hull | a bevelled collider, checked at 18 m/s in GV2; a body found inside a hull is moved out by the server |
| Units wedge on a parked vehicle the navmesh knows nothing of | riflemen stuck against a truck | vehicles are few and are removed after 60 s abandoned; if it shows, a `NavigationObstacle3D` on a parked hull |
| §5.6's view tick is off by a tick or two | tracer and round more than 0.3 m apart at speed | GV4's measured check; the formula is one tested function |
| The barracks mortar aims at a point in the air | shells bursting overhead, or no solution found | checked in GV5; if it misbehaves, the mortar skips airborne players |
| A truck parked in the bunker's door shuts the spawn in | nobody leaves the bunker | anybody can drive it away with `E`; it is removed after 60 s empty |
| The vehicles decide rounds | the strategist stops winning against people in them | the cap, the timers and the armour are files (§9); one helicopter at 90 s is the first dial |
| Running infantry over decides rounds | most of the strategist's infantry losses are `kill[weapon=15]` | one number per kind (`RamKillSpeedMetersPerSecond`), then the 4x4's top speed and acceleration; units that step aside are §15's |
| Mixed builds | misrouted RPCs | ship together (§7.5) |

---

## 15. Not in this plan

- **Damage from collisions** to players or to a crew vehicle itself, and **ramming the strategist's
  vehicles**, which a crew vehicle pushes aside (GV-D8, GV-D17).
- **Units that get out of a vehicle's way**, and a ram that does more than kill — damage scaled
  with speed below the threshold, a body thrown.
- **Repair and rearming** at the kiosk.
- **Bots that drive or fly**; the computer strategist answering vehicles — tanks preferring them,
  units shooting at empty ones.
- **Grenades, the hammer and the drone from a seat**; getting out above 4 m (there is no fall damage
  to make it a decision, and no parachute).
- **A vehicle block in the agent observation** (`obs-5`).
- **More kinds, more seats, a second kiosk**, or a seat held for whoever asked at the kiosk.
- **Rewound hits against vehicles**: a hull is tested where it is now, as a unit's capsule is
  (NETCODE.md §5).
