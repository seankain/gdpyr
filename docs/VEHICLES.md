# gdpyr — Vehicles that carry units: the technical, and a dropship

**Status: plan.** Nothing here is built yet. It replaces the "Dropships and troop carriers" scope
cut in [`IMPLEMENTATION_PLAN.md`](IMPLEMENTATION_PLAN.md) §5 with a design and a milestone list.

Companion documents: [`NETCODE.md`](NETCODE.md) §6 (unit replication, commands), §10.5 (builders,
the request pattern this copies), §10.7 (the supply truck, the precedent for "a unit kind is one
number on its definition"); [`HTN_BOTS.md`](HTN_BOTS.md) (D1, D5); [`AGENT_API.md`](AGENT_API.md)
§6–§9.

---

## 1. What is asked

1. **The technical carries units.** It gets a number of seats. With units selected, the strategist
   right-clicks a friendly technical: the selected units walk to it and get in, one at a time, until
   the seats are full. The technical drives somewhere. Selected again, its card offers **Unload**;
   pressed, the passengers get out round it.
2. **A dropship.** A new vertical take-off and landing unit with the same boarding and unloading.
   It flies at altitude, lands to load and unload, and takes off again.

## 2. Decisions

These are the defaults the plan is written against. Each is one line to change; flag any you
disagree with before V2 starts.

| | Decision | Why |
|---|---|---|
| V-D1 | **Who rides:** any unit on foot (not `IsVehicle`): rifleman, builder, sniper, heavy gunner. One seat each. No vehicle carries a vehicle. | Matches the request; a seat-size or vehicle-carrying rule is a second system (§14). |
| V-D2 | **Seats:** technical 4, dropship 8. `SimConfig.MaxPassengers = 8` bounds both. | A technical's bed; a dropship is the squad mover. Playtest knobs. |
| V-D3 | **The gesture is a right-click** on a friendly vehicle with units selected. | Left-click replaces the selection (`StrategistController.FinishSelection`), so "select units, then click the vehicle" can only be a right-click. |
| V-D4 | **More selected than free seats:** the ones nearest the vehicle board (ties to the lower id); **the rest stop where they are.** | Deterministic, and an order every selected unit visibly answered. |
| V-D5 | **Unload is "unload here":** `U` or the card's button. The technical stops; the dropship lands where it is; everybody gets out at once. | The request. "Unload at a point" is one armed key on top of this and is deferred (§14). |
| V-D6 | **Passengers are inert.** They do not shoot, see (fog), capture, heal or throw, and nothing can hit them. The technical keeps its own gun. | One rule, one predicate (§5.1). Firing from the bed is deferred (§14). |
| V-D7 | **A vehicle destroyed with passengers:** a technical, or a dropship within 4 m of the ground, throws them out round the wreck, each taking half its maximum health as explosive damage; a dropship higher than that kills them all. The killer is credited with every unit that dies. | A loaded technical is worth a launcher round, but a squad is not one hit point. 4 m is `CapturePad.VerticalToleranceMeters`, the game's existing "on the ground". |
| V-D8 | **The dropship:** unarmed, 400 health, bullets do half, 12 m/s at 14 m, lands when it arrives, trained at the barracks on `T` for 250 points and 14 s. | Every number is a playtest knob (§8.1). Unarmed keeps it a transport, not a gunship. |
| V-D9 | **Computer strategists never board or unload of their own accord** (one exception: §9.1's orphan rule). They are made robust to passengers instead. | Teaching the commander transports is new goals, a squad-tree change and a clash with the technical's scout role (§9). |
| V-D10 | **Agent API: additive only.** New commands, `list_units` fields and events; schema stays `gdpyr-agent-obs-2`, action heads unchanged. | Every trained checkpoint keeps loading (§10). |
| V-D11 | **Wire:** the 13-byte unit record stays 13 bytes. One flag bit says *aboard*, two say the flight phase, and an aboard unit's yaw field carries its vehicle's id. `DemoFormat.Version` 1 → 2. | §6. |
| V-D12 | **The ground force can work out how many ride in each vehicle** from the unit snapshot. Accepted. | Units are one unfiltered broadcast by design (`NETCODE.md` §6.2); a modified client can already count the army. |

---

## 3. The shape of it

A passenger **stays a `Unit`** — same id, same registry entry, same health, same squad bookkeeping
up to the moment it boards. It is alive, hidden, solid to nothing, not simulated, and pinned to its
vehicle's position every tick. Nothing is despawned and nothing is respawned. That is what keeps
`LiveUnitCount` (the defeat condition), `MaxUnits`, demo playback (where the snapshot is the
roster, `UnitManager.ApplyRecordedSnapshot`) and every agent observation row correct without a
second representation.

The alternative — despawn into a manifest of `(definition, health)` and respawn on unload — was
rejected: it loses ids, plans, squads and grenades, turns every unload into new `unit_built`-shaped
traffic, makes demo playback despawn and respawn on every boarding, and needs the defeat condition
to count something that is not a unit.

```
right-click vehicle ─► RequestEmbark(ids, vehicle) ─► ApplyEmbark: seats, nearest first, rest stop
                                                          │
             each tick: walk to the vehicle (Order.Kind = Embark, target refreshed)
                                                          │ in reach, seat free, (dropship landed)
                                                          ▼
                       RidingIn = vehicle; hidden; not simulated; pinned to the vehicle
                                                          │
U / Unload ─► RequestUnload(vehicles) ─► vehicle Order.Kind = Unload: stop / land, then
                                          everyone out at checked slots, Hold where they stand
```

Names: **not** "transport" (`TransportFactory` is the network layer) and **not** "carrier"
(`Emplacement.CarrierPeerId`, `EmplacementStateId.Carried`, `GrenadeSim.Carried` already exist).
This plan uses **seats**, **cargo**, **passenger**, **riding**.

---

## 4. The rules, engine-free

Everything decidable without the engine goes in `Scripts/Sim`, which `Tests/Gdpyr.Tests.csproj`
compiles directly, and is tested there — the `BuildQueue`, `StructureTraining` and `Resupply`
pattern.

### 4.1 `Scripts/Sim/Cargo.cs` (new) — `Tests/CargoTests.cs`

- **`CargoHold`** — fixed `ushort[SimConfig.MaxPassengers]`, `Seats`, `Count`, `IsFull`,
  `TryAdd(id)` (refuses full, duplicate, 0), `Remove(id)`, `Contains(id)`, `Clear()`, and an
  ordinal accessor. Allocated once when a unit with seats is created; nothing per tick.
- **`Cargo.Admit(candidates, distances, freeSeats, boarders, leftovers)`** — orders candidates by
  flat distance to the vehicle, ties to the lower id, and splits them at `freeSeats` (V-D4).
- **`Cargo.InReach(vehicle, vehicleRadius, unit, unitRadius)`** — flat (XZ) distance ≤
  `vehicleRadius + unitRadius + Cargo.ReachSlackMeters` (1 m): 2.4 m for a rifleman at the
  technical, 3.4 m at the dropship. Flat, so a unit next to a landed dropship on a slope still
  reaches it. Infantry stop on the technical's collider at ~1.4 m, inside reach.
- **`Cargo.ExitSlot(vehicle, yaw, vehicleRadius, passengerRadius, ordinal)`** — the `ordinal`-th
  candidate point: a row behind the vehicle first, then its sides, then its front, then a second
  ring further out, each `vehicleRadius + passengerRadius + 0.6 m` from the centre and 1.2 m apart
  (`StructureTraining.Slot`'s spacing). Candidates only: the engine half checks each one (§5.4).
- **`Cargo.WreckDamage(maxHealth)`** and **`Cargo.WreckKillsAll(heightAboveGround)`** — V-D7's two
  numbers, so the rule is one place.

Tests: the fifth rider is refused at four seats; a duplicate is refused; `Admit` keeps nearest
first and returns the rest; reach is flat (a unit 3 m below a landed hull's centre still reaches
it); exit slots are clear of the vehicle's radius and of each other; the first slot is behind the
vehicle at every yaw; the wreck rule's threshold is inclusive at 4 m.

### 4.2 `Scripts/Sim/Flight.cs` (new, V6) — `Tests/FlightTests.cs`

The dropship's phase machine:

```
            wants air                    reached cruise
 Grounded ───────────► TakingOff ──────────────────────► Cruising
    ▲                      ▲ wants air                       │ arrived, and a landing spot found
    │ touched down         │                                 ▼
    └──────────────────────┴──────────────────────────── Landing
```

- `FlightTraits` (from `UnitDefinition.ToFlightTraits()`): cruise altitude 14 m, climb 4 m/s,
  descent 3 m/s, **clear altitude** 10 m — below it the dropship moves only vertically, so it never
  slides into a barracks roof (6.5 m) or a sniper tower cabin (8.4 m) on the way up or down.
- `Flight.Step(traits, ref state, wantsAir, heightAboveGround, dt)` returns the vertical velocity
  and whether horizontal movement is allowed. "Wants air" is "has a destination further than its
  arrival radius, measured flat".
- `Flight.CanBoard` / `CanUnload` are true only when `Grounded`.

Tests: take-off reaches cruise in `ceil(14 / 4 / dt)` ticks; no horizontal movement below clear
altitude; landing returns to the ground; a new destination during landing takes off again;
boarding and unloading are refused unless grounded; altitude never leaves
`±SimConfig.MaxPositionMeters` (327.67 m; the map's floor top is at 0.5 m).

---

## 5. The server

### 5.1 Unit state and one predicate

On `Unit` (`Scripts/Rts/Unit.cs`):

- `ushort RidingIn` — the vehicle's `UnitId`, 0 on foot. `bool IsRiding => RidingIn != 0`.
- `bool IsOnField => IsAlive && !IsRiding` — **the question every system outside the unit loop
  is really asking when it says `IsAlive` today.**
- On a unit with seats: `CargoHold Cargo` and `int CargoIssuer` (the peer whose order last put
  somebody aboard; §9.1).
- On an aircraft: `FlightState Flight` (§8).

**Presentation in one place.** `SetDeadPresentation` (`Unit.cs:296`) becomes `ApplyPresentation()`:
`Visible = IsAlive && !IsRiding`, `CollisionLayer = Units` only then (0 otherwise), and on the
server `CollisionMask = 0` while riding so the hidden body never pushes its vehicle. An aircraft off
the ground also gets `CollisionLayer = 0` (§8.3). It is called from `ApplyDamage`, from
`ApplyRemoteState` on any change of alive, aboard or flight phase (today it reacts only to alive,
`Unit.cs:280`), and on boarding and unloading. Two setters would undo each other — a passenger
killed with its vehicle shown again, or a corpse shown on unload.

### 5.2 Orders and requests

`OrderKind` (`Scripts/Sim/UnitOrder.cs`) gains two wire values, appended:

| Value | On | `Target` | `TargetOwnerId` |
|---|---|---|---|
| `Embark = 7` | a passenger walking to its vehicle, and while it rides | the vehicle's position, refreshed every tick | `OwnerId.ForUnit(vehicle)` |
| `Unload = 8` | a vehicle | where it comes to rest: its own position (V-D5) | — |

Neither goes through the generic order message: `UnitOrder.IsIssuable` stays the range
`Move..Stop`, which already excludes 7 and 8, as it excludes `Build`. They arrive by their own
requests, in a new partial **`Scripts/Rts/UnitManager.Cargo.cs`**, each the
`Request*`/`Client*`/`Server*`/`Apply*` quartet `RequestAssist` uses
(`UnitManager.Structures.cs:186`) so a person, a bot and an agent reach the same checks:

- **`ClientEmbark(int[] unitIds, int vehicleId)`** → `ApplyEmbark`. Refused (`RejectedOrders++`)
  unless the sender is a strategist and the vehicle is a live unit of theirs with seats that is not
  itself riding. Each id must be a live own unit that can ride (V-D1) and is not the vehicle; an id
  that is **already riding** is skipped *without* counting — an honest client's selection lags the
  server by a round trip. Free seats are the vehicle's seats less those aboard less those already
  walking to it (live units whose order is `Embark` naming it). `Cargo.Admit` picks the boarders;
  they get `Embark`, the rest `UnitOrder.Hold(where they stand)`. All of them: issuer and tick set,
  `NextScanTick = 0`, `ResetPlan`, `Suppression.Clear`, `Squads.Leave`.
- **`ClientUnload(int[] vehicleIds)`** → `ApplyUnload`. Same sender and ownership checks; an empty
  vehicle is a no-op, not a rejection (the button is disabled, but a passenger may have died). Sets
  the vehicle's order to `Unload` at its own position, issuer and tick, `ResetPlan`.

**`ApplyOrder` skips riders silently** (`UnitManager.cs:1649`): an order to a unit that boarded
within the last round trip is a race, not a forged packet, and `RejectedOrders` is documented as
"0 for honest clients" (`NETCODE.md` §6.3). Ordering a vehicle never touches its passengers.

`UnitBrain.Destination` gains `Embark => order.Target` and `Unload => order.Target`; neither holds
to fight (`HoldsWhileEngaging` stays false), so a unit walking to board shoots on the way as on a
move.

### 5.3 The tick

In `SimulateUnit` (`UnitManager.cs:441`):

1. Right after the `IsAlive` check: **a rider returns.** No plan, scan, fire, throw, steer or
   resupply.
2. Beside `ResolveBuildOrder`: `ResolveEmbarkOrder(unit)` re-validates the vehicle every tick (alive,
   same team, has a free seat or this unit's reserved one) and refreshes `Order.Target` to its
   position; anything invalid stands the unit down to `Hold`, as `ResolveBuildOrder` does. In reach
   (`Cargo.InReach`) of a vehicle that can take it (`Flight.CanBoard` for the dropship) it
   **boards**: `RidingIn` set, `Cargo.TryAdd`, `ApplyPresentation`, `ResetPlan`, `Squads.Leave`,
   `HasCoverSpot = false`, `Unit.TargetOwnerId = None` (the shooting target — not
   `Order.TargetOwnerId`, which names the vehicle), `Suppression.Clear`, and a `unit_embarked`
   event. Registry order decides who takes the last seat when two arrive on one tick.

A new pass **`ServerCargo(tick)`** runs in `ServerTick` (`UnitManager.cs:255`) after the unit loop
and before `ServerStructures`, so it sees every vehicle where it moved to this tick and the
snapshot that follows sees the result. Both server entry points (`PlayerManager.ServerTick` and
`OfflineTick`) reach it through `UnitManager.ServerTick`.

- **Pin:** every rider's `GlobalPosition` and velocity become its vehicle's. Fog, snapshots, the
  agent views and the spectator then read a rider at the right place with no code of their own.
- **Unload:** a vehicle whose order is `Unload` and that is at rest — the technical the tick after
  the order (`Unit.Steer` sets velocity directly, so it stops at once), the dropship once
  `Grounded` — lets everybody out (§5.4) and then holds where it is.
- **Wrecks:** resolved here, the tick after the kill (§5.5).

### 5.4 Getting out

For each rider in hold order, take `Cargo.ExitSlot` candidates in turn and accept the first that
passes the engine checks `ValidateSite` and `Obstructs` already make
(`UnitManager.Structures.cs`), pulled out into shared helpers: a World-layer ground ray,
`NavigationServer3D.MapGetClosestPoint` within slack (so nobody is put off the navmesh, or on the
strip outside the walls), and an `IntersectShape` with the passenger's capsule lifted 0.15 m
against World, Units and Players, excluding the vehicle. The navmesh check alone is not enough:
the map's invisible `Cube`, `Cube2` and `Cube3` colliders are not carved from it. Slots taken this
tick are remembered in a scratch array, since a body moved this tick may not be in the broadphase
yet.

A placed rider: `GlobalPosition` = the slot, `RidingIn = 0`, `Cargo.Remove`, `ApplyPresentation`,
`Order = Hold(slot)` from the unloading peer with the current tick (so D1's claim follows the
unloader), `NextScanTick = 0`, a forced re-path (the agent's 0.75 m threshold would otherwise keep
the pre-boarding path, `Unit.SetDestination`), and a `unit_disembarked` event. It is in no squad
until its next order, as a fresh unit at its door is. A rider with no clear slot stays aboard and is
retried every scan interval (10 ticks) while the order stands; a new order to the vehicle cancels
the unload and leaves the rest aboard.

### 5.5 Wrecks

`UnitManager.Damage` (`UnitManager.cs:1472`) is the only death path. When it kills a vehicle with
anybody aboard it records the wreck — vehicle id, killer's owner id, height above ground — in a
fixed list and does nothing else. `ServerCargo` resolves the list at the start of the next tick:

- the vehicle low enough (`Cargo.WreckKillsAll` false): each rider is placed as in §5.4 and takes
  `Cargo.WreckDamage` as explosive damage through `CombatManager.DamageUnit`, with the killer as
  attacker, so kills, hit flags and `Damage`/`Kill`/`unit_lost` events are the normal ones;
- otherwise, or a rider with no slot: it is killed the same way.

Deferring by a tick is what makes it order-independent. Riders are invisible to `QuerySegment` and
`Explode` (§5.6), so the blast that killed the vehicle cannot touch them, and the splash loop walks
the registry by index (`CombatManager.cs:856`), so resolving inside it would hurt the riders listed
after the vehicle and spare those before. The corpse is reaped two seconds later
(`ServerReapCorpses`), long after its riders are out. `Despawn` also releases any riders left (it
should never find one) and removes a despawned rider from its vehicle; `ClearUnits` clears both.

### 5.6 Every consumer of "is this unit in the world"

Each of these filters on `IsAlive` (or nothing) today and switches to `IsOnField`, or to the rule
given:

| Where | What changes |
|---|---|
| `UnitManager.QuerySegment` (`UnitManager.cs:1429`) | skips riders: rounds and melee cannot hit them |
| `CombatManager.Explode`, unit splash loop (`CombatManager.cs:856`) | skips riders |
| `VisibilityService.BuildSensors` (`VisibilityService.cs:270`) | riders are not sensors — they would also crowd the three-candidate `VisionField.Gather` list at the vehicle's point and starve a sensor elsewhere that could see |
| `EconomyService` presence count (`EconomyService.cs:230`) | riders do not hold a node; the vehicle counts as one body |
| `UnitManager.CollectSupplies` | a riding truck is not a source (cannot happen under V-D1; filtered anyway) |
| `UnitManager.CountSquads` (`UnitManager.cs:896`) | riders are neither neighbours nor census members: otherwise they skew `Neighbourhood` odds, `ControlPoint.Holds` and squad centroids |
| `UnitManager.Cover` (`UnitManager.Cover.cs:110`, `:179`) | boarding clears `HasCoverSpot`, so both loops agree |
| `UnitManager.TryComplete` (`UnitManager.Structures.cs:635`) | does not teleport riders, or an aircraft passing over, out of a finishing footprint |
| `UnitManager.GatherBuilders` (`UnitManager.Structures.cs:486`) | skips riders — `PlaceSite` would charge for a site nobody can walk to |
| `UnitManager.ApplyOrder` | skips riders silently (§5.2) |
| `UnitManager.LiveUnitCount` | **unchanged**: riders are the strategist's army for `WinConditions` |
| `GroundSensor.Scan` (`GroundSensor.cs:129`) | skips riders: no bot sees or targets them |
| `BotDirector.PruneGroundContacts` (`BotDirector.cs:274`) | forgets a rider as it forgets a dead unit, or the shared contact memory keeps it for 8 s |
| `BotPilot.TryTargetState`, `TryCoverThreat` (`BotPilot.cs:540`, `:888`) | a rider is a lost target and no threat |
| `BotStrategist` (§9.1) | `Decide`, `FillZones`, `Fortify`, `QueueSupply`, legacy `Command` skip riders |
| `StrategistController.IsSelectable`, `PruneSelection` (`:1008`, `:1191`) | riders are not selectable and drop out of a selection |
| `SpectatorView` pick (`SpectatorView.cs:472`), `AiEntities.TryPosition` | riders are not pickable; following one follows its vehicle |
| `AiDebugPublisher` unit labels | no label for a rider; the vehicle's says "aboard 3/4" |
| `Scripts/Ui/Debug.cs:177` | the units line reads `live/64 (n riding)` |

Two views deliberately keep riders, **both or neither**, because policies join them by registry
order: `AgentStrategistView.Units` (the vector rows) and `AgentServerOps.ListUnits`. Both show a
rider at its vehicle's position, which pinning makes true, and `list_units` says which vehicle
(§10). `AgentPlaneView`'s own-unit plane likewise counts riders where they are.

---

## 6. On the wire

### 6.1 The unit record stays 13 bytes

`UnitFlags` (`Scripts/Sim/UnitSnapshot.cs:50`) has state in bits 0–1 (all four `UnitStateId`
values taken) and team in bit 2; bits 3–7 are free and today's decoders mask them off.

| Bits | Meaning |
|---|---|
| 0–1 | `UnitStateId`, unchanged |
| 2 | team, unchanged |
| **3** | **aboard** — the unit is riding |
| **4–5** | **flight phase** — 0 grounded, 1 taking off, 2 cruising, 3 landing; always 0 on the ground units |
| 6–7 | spare |

**While aboard, the u16 yaw field carries `RidingIn`** — a hidden passenger's heading means nothing
— and the position is the vehicle's. A client that decodes an aboard record pushes the vehicle's
yaw (or its own last one) into the interpolator, never the id read as an angle. The client keeps a
per-vehicle aboard count by counting the records that name each vehicle: exact, because units are
never fog-filtered or truncated.

`MaxPayloadBytes` stays 837 for a full field of 64. Riders stay **in** the packet — leaving them
out would make demo playback despawn them (`UnitManager.ApplyRecordedSnapshot`) and freeze live
puppets, visible, where they boarded.

The rejected layout appends a u16 vehicle id (15 B, 965 B for 64): simpler to decode, two bytes
per unit per snapshot for what is zero on almost every record.

### 6.2 Everything else

- **`DemoFormat.Version` 1 → 2**, as its own comment requires for any change to a copied codec;
  old demos are refused by `DemoHeader.IsPlayable`. `ApplyRecordedSnapshot` handles the new bits as
  the live path does.
- **Joining peers:** `ServerSpawnUnit` carries no aboard bit, so a new puppet stays hidden until its
  first snapshot record (≤ 50 ms). That also fixes the same flash for any unit.
- **New messages** (`NETCODE.md` §7 table): `ClientEmbark` ~8 B + 4 B per unit and `ClientUnload`
  ~4 B + 4 B per vehicle, both reliable, client → server. No new server → client message: the
  snapshot carries it all.
- **One build for everybody.** There is no live-wire version check, and new `[Rpc]` methods on
  `UnitManager` probably renumber its RPC ids (Godot assigns them by method; not verified here). A
  server and clients from different commits misroute silently instead of failing a decode. This is
  the existing practice; it is now load-bearing.

---

## 7. The strategist's controls

| Input | Does |
|---|---|
| right-click a friendly technical or dropship, with units selected | the units that can ride board it (V-D3, V-D4) |
| `U`, or **Unload (U)** on the card | every selected vehicle with somebody aboard unloads where it is |
| `T` | queue a dropship at the barracks |

`U` (physical keycode 85) and `T` (84) are free; every digit is taken and `G` must stay unique
(`GrenadeTests.TheGrenadeKeyIsBound`).

- **`StrategistController.CommandAtCursor`** (`:643`) gets `TryEmbarkAtCursor()` after the
  placement branch and before `TryAssistAtCursor` and `PickEnemyPlayer`, modelled on
  `TryAssistAtCursor` (`:886`). It fires only when the selection holds at least one unit that can
  ride (other than the vehicle) and the nearest own vehicle with seats is within a pick radius
  scaled by its on-screen size and is nearer the cursor than any enemy contact. `PickUnit`'s fixed
  36 px around `GlobalPosition + Up` would let a rifleman beside the truck steal the click. It sends
  `RequestEmbark` with the eligible ids, flashes an embark marker on the vehicle and resets the
  pending order to Move. A selection of vehicles only falls through to an ordinary move. A full
  vehicle is still asked; the server answers (V-D4) and the card says "full".
- **`_UnhandledInput`** gets `rts_unload` and `rts_build_dropship` in its chain.
- **`StrategistHud`:** `Refresh` is passed a summary of the selection (vehicles, aboard, seats)
  rather than `_selected.Count`. With a vehicle selected, the bottom-right card — which only a
  barracks or structure uses today, and a unit selection excludes those — shows
  "technical · 3/4 aboard", the Unload button (built with `NewCardButton`, disabled when empty) and
  the hint "right-click with units selected to board". `CardUnits`/`CardKeys` gain the dropship and
  `T`; the tooltip gains "seats 8". The key-help label gains a line, and its fixed 168 px height and
  the card's offsets grow with it.
- **`SelectionOverlay`:** seat pips above every own vehicle with seats (filled for aboard), drawn
  like `DrawStructures`' bars; a ground ring under an airborne dropship; colours for the embark and
  unload markers in `OrderColor`.
- **`README.md`** controls table, the Units line, the grenade sentence (vehicles carry none) and
  the armour paragraph name the dropship.

---

## 8. The dropship

### 8.1 Definition

`Units/dropship.tres`, appended to `UnitCatalog.Paths` as **id 7** (`UnitCatalog.Dropship`), out of
`UnitCatalog.Tiers`. Appending keeps every earlier id, which demos, scenarios (`unit_built.tier`),
the agent's `tier` and trained policies all depend on. `SimConfig.MaxUnitDefinitions = 8` is not a
catalog limit (`UnitCatalog.Load` never reads it) but `StructureDefinition.Trains` is bounded by
it, so a ninth unit kind raises it.

| | technical (now) | dropship |
|---|---|---|
| cost, seconds | 150, 9 | 250, 14 |
| health, bullets do | 220, all | 400, half (`BulletDamageScale = 0.5`, the first fractional value) |
| `IsVehicle`, grenades | yes, 0 | yes, 0 |
| speed | 8 m/s | 12 m/s in the air, 0 on the ground |
| body | radius 1.0, height 2.2 | radius 2.0, height 4.0 (height ≥ 2 × radius keeps the hitbox a capsule) |
| sees, shoots | 70 m, 50 m | 40 m, 0 (unarmed: `EngageRangeMeters = 0`; `WeaponId` is clamped into the catalog, so range is the only off switch) |
| seats | 4 (new) | 8 |
| trained by, key | barracks, `2` | barracks, `T` |

New `UnitDefinition` exports: `Seats` (0 carries nobody) in "Body"; a "Flight" category —
`CruiseAltitudeMeters` (0 = a ground unit; `IsAircraft` is `> 0`), `ClimbMetersPerSecond`,
`DescentMetersPerSecond`, `ClearAltitudeMeters` — with `ToFlightTraits()` beside `ToTraits()`.
`UnitCatalog` gains `SeatsOf`, `CanRide` and `IsAircraft`, after `CanConstruct`/`IsSupply`.

### 8.2 Flying

- **`Unit.Steer`** (`Unit.cs:174`) branches for aircraft into `SteerFlying`: no
  `NavigationAgent3D` (`BuildAgent` is skipped for aircraft), a straight line in XZ at `MoveSpeed`
  when `Flight.Step` allows horizontal movement, vertical velocity from `Flight.Step`, `Velocity`
  always written with Y (ground bots lead with it), then `MoveAndSlide` with
  `MotionMode.Floating` while airborne. Height above ground is one World-layer ray down per
  aircraft per tick.
- **`SimulateUnit` measures arrival flat for aircraft only** (`UnitManager.cs:515`, `:521`): a
  dropship 14 m above its destination is there. Ground units keep the 3D distance — legacy's
  decisions must not move (D5).
- **It lands when it arrives.** A Move, Attack or Defend order flies there and lands; Stop lands
  where it is; Patrol keeps it flying between its ends. Embark orders to a dropship in the air send
  the walkers to stand under it; they board when it lands.
- **Landing spot:** on entering `Landing`, `TryLandingSpot(target)` runs §5.4's checks with a
  hull-sized shape from the ground to clear altitude, over a short nearest-first ring of candidates
  (6 m, then 12 m out). None clear: it hovers, holding cruise, and the card says "no room to land".
  At 2 m it re-checks for bodies (Units, Players) beneath and waits there until clear.
- **Order targets are clamped** for aircraft to inside the walls (|x|, |z| ≤ 115 m) and must be
  finite; `ApplyOrder` checks neither today, and the hidden 300 × 300 m slab means there is ground,
  and probably navmesh, outside the walls.
- **Spawning:** `Barracks.SpawnSlot` is 2 m from the barracks wall, inside a 2 m hull. An aircraft
  spawns pushed out along the door's normal by its radius plus 1 m, and takes off for the rally
  point.

### 8.3 Collision, combat and sight

- Grounded: `CollisionLayer = Units`, mask as any unit. Off the ground: layer 0, mask World. A
  player standing on a landed dropship drops off when it lifts (≤ 4 m, no fall damage); the client
  learns from the phase bits ~100 ms later and reconciles. Nothing can ride the roof into the air.
- It is shootable at altitude with no combat change: `Unit.Hitbox` is built from `GlobalPosition`,
  and hits are analytic capsules. Rifles do half, the launcher and grenades their explosive damage
  (grenades ×0.25 to vehicles), the heavy emplacement within its ±30° of elevation (it reaches a
  14 m target only beyond ~22 m). Barracks guns and structures target players only; strategist
  units never shoot units. There is no other anti-air, so `MaxHealth` and `BulletDamageScale` are
  the whole balance.
- Ground bots (`GroundSensor`, `BotPilot`) already see it by 3D range and line of sight. Two
  changes: an airborne target's position is projected to the ground for the bot's movement goal
  (`BotPilot.Sample` would otherwise walk at a point in the sky), and grenades are never thrown at
  it (already true for vehicles). Launcher drop compensation is deferred (§14).
- Fog: its eye at ~16 m sees over every wall on the map. Range is a sphere (`VisionField`), so a
  40 m sensor covers ~37 m of ground at cruise. It is a scout as well as a carrier — a knob.
- Capture: landed, it is one body on a pad; above 4 m it is not on it.

### 8.4 Presentation

A hull box, two rotor discs and landing legs, built only when `!Bootstrap.IsDedicatedServer` (as
the supply truck's ring is); rotors spin from the phase bits; the tilt is on the mesh child, never
the body's basis. The collider stays the capsule the hitbox is.

---

## 9. Computer players

### 9.1 The commander and legacy

Under `--bot-ai htn`, `BotStrategist.Decide` (`BotStrategist.cs:223`) turns every living
non-builder into a `CommandUnit`; once a person's 30 s claim (D1) lapses, it would re-order riders,
recruit a loaded technical as its scout (`Commander.Recruit` takes technicals first) and patrol it
away with the person's squad aboard, and put a dropship in the garrison. So:

- `Decide`, `FillZones`, `ApplyIntents` (as a guard), `Fortify` and `QueueSupply` skip riders.
- **A loaded vehicle is claimed for as long as the peer that loaded it (`CargoIssuer`) is seated
  as a person or a policy strategist** — not only for 30 s. Unloaded, it is an ordinary unit again
  under D1.
- **Orphan rule:** a loaded vehicle whose loader is no longer seated is unloaded where it stands on
  the commander's next decision, through `ServerUnload`. Otherwise a person who loads a squad and
  leaves strands units that never fight and, being alive, never let the strategist lose.
- Dropships are skipped by `Decide` altogether: no role, never in the garrison, never bought (not
  in `Tiers`; `BotStrategist.Build` treats tier index as definition id, so adding it there would
  make the builder buyable).
- The technical keeps `Scout`. A loaded one is never offered to `Recruit` because it is claimed.
  `CommanderHtnTests`' recon facts and `htn_recon_finds_contact` must pass unchanged.
- Legacy `Command` (`:565`) skips riders, loaded vehicles and aircraft. For a round with no
  transports that is no change at all, which is what D5 requires.

### 9.2 Unit HTN

- Riders are never planned: `SimulateUnit` returns before `PlanOf`, and boarding calls `ResetPlan`.
- `UnitDomain`'s Retreat and Resupply selectors exclude `Move` and `Build` orders
  (`UnitDomain.cs:959`, `:973`); `Embark` and `Unload` join them, so an autonomous unit walking to
  board, or a vehicle unloading, is not pulled off it. An order kind no selector matches yields no
  plan and falls back to `UnitBrain.Destination`, which §5.2 teaches the two new kinds.
- Aircraft get no unit context from `PlanOf`: cover, defend posts and pad posts mean nothing to
  them. They follow their order through `UnitBrain`.
- No new `UnitFact`, `UnitGoal` or `CommandGoal` in this plan, so `HtnSchema`,
  `DescribeUnitPlans`' sizing, `AiGoals.Family` and the P8 rehearsal are untouched.

---

## 10. Agent API, debugger and spectator

All additive (V-D10), as the supply truck and H6's events were.

- **Commands:** `AgentCommandKind.Embark = 6`, `Unload = 7`; `{"cmd":"embark","units":[…],
  "vehicle":id}` and `{"cmd":"unload","units":[vehicle ids]}` in `ParseCommand`
  (`AgentServerOps.cs:586`) and `AgentJson.CommandNames`; `AgentServer.ApplyCommands` calls
  `ServerEmbark`/`ServerUnload`. They are commands, not order kinds: `OrderFromName` turns an
  unknown kind into a Move, so `"kind":"embark"` sent to an older server would march units to the
  origin, whereas an unknown `cmd` is skipped.
- **`list_units`** gains `riding_in` (0 or the vehicle's id), `seats`, `aboard` and `y`.
- **Events**, appended after `NodeContested = 14`: `unit_embarked` {unit, vehicle, tier, x, z},
  `unit_disembarked` {unit, vehicle, tier, x, z}, `dropship_landed` {unit, aboard, "", x, z}.
  `tools/Gdpyr.Playtest/EpisodeMetrics` mirrors them; `AgentEventTests`' `Last` assertion moves.
- **Scenario `spawn`:** `UnitTier` learns `"dropship"`/`"7"` — today an unknown name silently
  becomes a rifleman — and a unit spawn may name a vehicle to start aboard (§11).
- **`state_hash`** folds `RidingIn` per unit.
- **Unchanged:** the 1,092-float strategist vector, the feature planes, the 6 × 8 action heads and
  `obs-2`. A rider is a row at its vehicle's position; order bytes 7 and 8 fall outside the 5-wide
  order one-hot as `Stop` and `Build` do, and id 7 has no tier bit, as ids 3–6 have none. A policy
  that wants transport state reads `list_units`. A trailing transport block would be `obs-3`
  (§14).
- **C# client and reference action space:** `GdpyrCommand.Embark/Unload`; `GdpyrUnit` gains the
  four fields; `StrategistActionSpace.Select` leaves riders out of every group, so `all`, `idle`
  and `armor` never order a passenger. Heads and widths are untouched, so checkpoints load. A loaded
  vehicle's death is several `unit_lost` events at once; `StrategistReward` will feel it.
- **AI debugger:** `AiValueKey` appends `RidingIn`, `Aboard`, `Seats`, `Altitude`; `AiMarkerKey`
  appends `Vehicle` (where a boarding unit is walking); `AiDebugText`'s unit header prints
  "riding in technical 12" or "aboard 3/4"; `AiDebugWorld` labels the new marker. Byte enums are
  appended, so `AiDebugCodec.Version` stays 1.

---

## 11. Harness and scenarios

The harness cannot express this yet: a spawn cannot refer to another spawn, and `Runner.Act`
(`tools/Gdpyr.Playtest/Runner.cs:246`) ignores strategist seats. `ScriptStep` is ground input only.

- **Spawns** gain an optional `"id"` label and `"aboard": "<label>"` naming an earlier unit spawn;
  `Scenario.ReadSpawns` refuses a forward or unknown label with a sentence, as it refuses
  everything else. The server boards the unit directly (harness-only, like `ServerPlaceUnit`).
- **Strategist scripts:** a step type of timed commands — `embark`, `unload`, `order` — naming units
  by spawn label; `Runner` keeps spawned ids by label and sends the steps through
  `GdpyrConnection.ActCommands`.
- Parser facts for both in `Tests/ScenarioTests.cs`.

New scenarios, asserting only what the vocabulary allows (events with x/z filters, counts):

| Scenario | Set-up | Asserts |
|---|---|---|
| `technical_boards_squad` | a technical and six riflemen beside it; scripted embark of all six | `unit_embarked.count == 4` |
| `technical_carries_squad` | a technical with four aboard; scripted move 60 m, then unload | four `unit_disembarked` past the line; no `unit_lost` |
| `technical_wreck_ejects` | a loaded technical in front of launcher bots | over seeds: riders get out or are lost, never neither |
| `dropship_lands_to_unload` | a dropship with eight aboard; scripted move, then unload | `dropship_landed.count >= 1`; eight `unit_disembarked` |

The existing suite must pass as it does at the commit V2 starts from: on the last measured build
18 of 19, with `htn_strategist_defends_node` the known seed-dependent one.

---

## 12. Milestones

Each ends with `dotnet test` green, the tests named here added, and the docs it touches updated in
the same change.

### V1 — Cargo rules (½ day)

`Scripts/Sim/Cargo.cs`, `SimConfig.MaxPassengers`, `Tests/CargoTests.cs` (§4.1).

### V2 — The technical carries units, on the server (1½ days)

- `UnitDefinition.Seats`; `Units/technical.tres` `Seats = 4`; `UnitCatalog.SeatsOf`/`CanRide`.
- `OrderKind.Embark`/`Unload`, `UnitBrain.Destination` cases (`UnitBrainTests`).
- `Unit.RidingIn`, `IsOnField`, `Cargo`, `CargoIssuer`, `ApplyPresentation`.
- `UnitManager.Cargo.cs`: the two request quartets, `ResolveEmbarkOrder`, `ServerCargo` (pin,
  unload, wrecks), the exit-slot and ground helpers out of `ValidateSite`/`Obstructs`.
- §5.6's sweep, in full, in this milestone — a half-done sweep leaks hidden units into the game.
- **Done when:** on a listen host, units board, ride, unload and survive a wreck; a passenger
  cannot be hit, does not count on a node and does not light the fog.

### V3 — The wire and the strategist's controls (1 day)

- `UnitFlags` bits and the yaw-slot rule, `UnitSnapshotCodec`, client apply on both paths, hidden
  until first record, the per-vehicle aboard count; `DemoFormat.Version = 2`.
- §7: right-click, `U`, the card, the overlay, the input map, `README.md`.
- Tests: `UnitSnapshotCodecTests` (aboard and vehicle id round trip; flight bits; a ground record
  decodes as before; state, team and the new bits share the byte; still 13 B and under one
  datagram), `DemoCodecTests` (version).
- **Done when:** the same on a dedicated server with a remote strategist and a remote ground player.

### V4 — Bots, agents, debugger (1 day)

§9 and §10. `AgentEventTests`, `StrategistActionSpaceTests` (a rider is in no group),
`AgentObservationTests` (order bytes 7–8 and tier 7 leave the one-hots zero), and the existing
`CommanderHtnTests`/`UnitHtnTests` unchanged.

### V5 — Harness and the technical's scenarios (1 day)

§11's spawn labels, `aboard`, strategist scripts, parser facts, and the three technical scenarios.
The existing suite re-run against its baseline.

### V6 — The dropship (2–3 days)

`Scripts/Sim/Flight.cs` and `FlightTests`; `Units/dropship.tres`, id 7, the flight exports;
`SteerFlying`, flat arrival, landing spots, clamped targets, spawning; collision by phase; the
ground bots' goal projection; presentation; `T` and the card; `dropship_lands_to_unload`.
`TrainedUnitResourceTests` (path 7 is the dropship; only the technical and the dropship have seats;
only the dropship has flight keys), `GrenadeTests.UnitsOnFootCarryThree_VehiclesNone` (the dropship
joins its vehicle list).

### V7 — Docs (alongside each)

A `NETCODE.md` §10.10 in §10.7's shape (the rule, the table, on the wire, who uses it, not in it);
§6.1's record description and §7's table; `DEMOS.md`'s version note; `AGENT_API.md` §6.2, §7.4,
§8, §9.1; `HTN_BOTS.md` on the loaded-vehicle claim and the orphan rule; this document's status, and
§5's scope-cut row struck as the builders' was.

**About 7–8 days in all**; V1–V5 (the technical) stand on their own and ship first.

---

## 13. Risks

| Risk | Signal | Mitigation |
|---|---|---|
| A consumer still asks `IsAlive` and leaks a hidden passenger into play | bots shoot at a parked truck's empty bed; a node is held by nobody visible; selection grabs invisible units | One predicate; §5.6 done in one milestone; a review grep for `IsAlive` over `Scripts/` before V2 closes |
| Passengers stranded aboard | live units that never fight; a round that cannot end | Unload retries; the orphan rule; riders show in the debug HUD's units line |
| Unloading into geometry | units stuck in a wall or outside the map | Slots checked against the navmesh *and* physics; slots taken this tick remembered |
| Dropship lands on a body, or cannot land | units or players squeezed out; a dropship hovering for ever | Pre-landing and 2 m checks; "no room to land" on the card |
| Ground bots helpless against the dropship | `htn_*` ground scenarios degrade when a strategist fields one | Half bullet damage, not none; goal projection; drop compensation in §14 if playtests ask |
| Mixed client and server builds after new RPCs | every unit RPC misrouted, silently | Ship together (§6.2); a join-time protocol version is a separate, small change worth making |
| Recon regresses because the technical is now also a transport | `htn_recon_finds_contact` fails | The commander never loads a technical; a loaded one is claimed; the recon tests gate V4 |
| Reward or metric spikes from multi-unit deaths | `StrategistReward` and `unit_lost` counts jump when a loaded vehicle dies | It is what happened; note it in `TRAINING.md` when V4 lands |

---

## 14. Not in this plan

- **The computer strategist using vehicles** — a ferry task, dropship assaults, a `CommandGoal`
  (append-only) and a squad-tree branch with its P8 rehearsal. The dropship is the better first
  target: it has no other role.
- **Unload at a point** — an armed `U` then right-click, the `X`/`C`/`B` pattern, storing the
  point in `Unload`'s `Target`.
- **Firing from the technical's bed**, per-unit seat sizes, vehicles carrying vehicles.
- **A helipad** that trains dropships instead of the barracks (a fourth `StructureKinds`, a key, a
  card).
- **A transport block in the agent observation** under `obs-3`, and a seventh, opt-in action head.
- **Ground bots compensating launcher drop** against aircraft; a door gun on the dropship.
